using System;
using System.Collections.Generic;
using System.IO;

namespace Nanite
{
    /// <summary>
    /// Disk/chunk wrapper for a packed NPG1 Page. The GPU Page Pool always receives the
    /// uncompressed NPG1 bytes; the storage codec is intentionally outside the Page ABI.
    /// </summary>
    public static class NanitePageStorageCodec
    {
        public const uint Magic = 0x31435A4E; // "NZC1"
        public const ushort Version = 1;
        public const byte CodecLz4Block = 1;
        public const int HeaderBytes = 16;

        public static bool TryPack(
            byte[] packedPage,
            out byte[] storageBlob,
            out bool compressed,
            out string error)
        {
            storageBlob = null;
            compressed = false;
            error = null;
            if (packedPage == null || packedPage.Length == 0)
            {
                error = "Packed Page is empty.";
                return false;
            }
            if (!NanitePageBinaryCodec.TryValidateBlob(packedPage, out error))
                return false;

            byte[] payload;
            try
            {
                payload = CompressLz4Block(packedPage);
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }

            if (payload.Length + HeaderBytes >= packedPage.Length)
            {
                storageBlob = packedPage;
                return true;
            }

            storageBlob = new byte[HeaderBytes + payload.Length];
            using (var stream = new MemoryStream(storageBlob, true))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(Magic);
                writer.Write(Version);
                writer.Write(CodecLz4Block);
                writer.Write((byte)0);
                writer.Write(packedPage.Length);
                writer.Write(payload.Length);
                writer.Write(payload);
            }
            compressed = true;
            return true;
        }

        public static bool TryUnpack(byte[] storageBlob, out byte[] packedPage, out string error)
        {
            packedPage = null;
            error = null;
            if (storageBlob == null || storageBlob.Length < sizeof(uint))
            {
                error = "Page storage blob is empty.";
                return false;
            }

            if (ReadUInt32(storageBlob, 0) != Magic)
            {
                packedPage = storageBlob;
                return NanitePageBinaryCodec.TryValidateBlob(packedPage, out error);
            }

            if (storageBlob.Length < HeaderBytes)
            {
                error = "Compressed Page header is truncated.";
                return false;
            }

            try
            {
                ushort version = ReadUInt16(storageBlob, 4);
                byte codec = storageBlob[6];
                int unpackedBytes = ReadInt32(storageBlob, 8);
                int payloadBytes = ReadInt32(storageBlob, 12);
                if (version != Version)
                    throw new InvalidDataException($"Unsupported Page storage version {version}.");
                if (codec != CodecLz4Block)
                    throw new InvalidDataException($"Unsupported Page storage codec {codec}.");
                if (unpackedBytes <= 0 || unpackedBytes > 64 * 1024 * 1024)
                    throw new InvalidDataException($"Invalid unpacked Page size {unpackedBytes}.");
                if (payloadBytes <= 0 || payloadBytes != storageBlob.Length - HeaderBytes)
                    throw new InvalidDataException("Compressed Page payload size mismatch.");

                packedPage = DecompressLz4Block(
                    storageBlob,
                    HeaderBytes,
                    payloadBytes,
                    unpackedBytes);
                if (!NanitePageBinaryCodec.TryValidateBlob(packedPage, out error))
                {
                    packedPage = null;
                    return false;
                }
                return true;
            }
            catch (Exception exception)
            {
                packedPage = null;
                error = exception.Message;
                return false;
            }
        }

        static byte[] CompressLz4Block(byte[] source)
        {
            if (source.Length < 4)
                return EmitLiteralOnly(source);

            const int hashBits = 16;
            var hashTable = new int[1 << hashBits];
            Array.Fill(hashTable, -1);
            var output = new List<byte>(source.Length);
            int anchor = 0;
            int cursor = 0;
            int matchLimit = source.Length - 4;

            while (cursor <= matchLimit)
            {
                uint sequence = ReadUInt32(source, cursor);
                int hash = (int)((sequence * 2654435761u) >> (32 - hashBits));
                int match = hashTable[hash];
                hashTable[hash] = cursor;

                if (match < 0 ||
                    cursor - match > ushort.MaxValue ||
                    ReadUInt32(source, match) != sequence)
                {
                    cursor++;
                    continue;
                }

                int tokenIndex = output.Count;
                output.Add(0);
                int literalLength = cursor - anchor;
                byte token = (byte)(Math.Min(15, literalLength) << 4);
                WriteLength(output, literalLength - 15);
                for (int i = anchor; i < cursor; i++)
                    output.Add(source[i]);

                int distance = cursor - match;
                output.Add((byte)distance);
                output.Add((byte)(distance >> 8));

                int matchStart = cursor;
                cursor += 4;
                match += 4;
                while (cursor < source.Length && source[cursor] == source[match])
                {
                    cursor++;
                    match++;
                }

                int encodedMatchLength = cursor - matchStart - 4;
                token |= (byte)Math.Min(15, encodedMatchLength);
                output[tokenIndex] = token;
                WriteLength(output, encodedMatchLength - 15);
                anchor = cursor;

                // Seed the table near the end of a long match without quadratic updates.
                if (cursor - 2 >= 0 && cursor - 2 <= matchLimit)
                {
                    uint tail = ReadUInt32(source, cursor - 2);
                    int tailHash = (int)((tail * 2654435761u) >> (32 - hashBits));
                    hashTable[tailHash] = cursor - 2;
                }
            }

            EmitLastLiterals(output, source, anchor);
            return output.ToArray();
        }

        static byte[] DecompressLz4Block(
            byte[] source,
            int sourceOffset,
            int sourceLength,
            int outputLength)
        {
            var output = new byte[outputLength];
            int sourceCursor = sourceOffset;
            int sourceEnd = checked(sourceOffset + sourceLength);
            int outputCursor = 0;

            while (sourceCursor < sourceEnd)
            {
                byte token = source[sourceCursor++];
                int literalLength = ReadLength(source, ref sourceCursor, sourceEnd, token >> 4);
                if (sourceCursor + literalLength > sourceEnd ||
                    outputCursor + literalLength > output.Length)
                    throw new InvalidDataException("LZ4 literal range is invalid.");
                Buffer.BlockCopy(source, sourceCursor, output, outputCursor, literalLength);
                sourceCursor += literalLength;
                outputCursor += literalLength;

                if (sourceCursor == sourceEnd)
                    break;
                if (sourceCursor + 2 > sourceEnd)
                    throw new InvalidDataException("LZ4 match offset is truncated.");

                int distance = source[sourceCursor] | (source[sourceCursor + 1] << 8);
                sourceCursor += 2;
                if (distance <= 0 || distance > outputCursor)
                    throw new InvalidDataException("LZ4 match offset is invalid.");

                int matchLength = ReadLength(source, ref sourceCursor, sourceEnd, token & 0x0F) + 4;
                if (outputCursor + matchLength > output.Length)
                    throw new InvalidDataException("LZ4 match exceeds the output Page.");
                int matchCursor = outputCursor - distance;
                for (int i = 0; i < matchLength; i++)
                    output[outputCursor++] = output[matchCursor++];
            }

            if (outputCursor != output.Length || sourceCursor != sourceEnd)
                throw new InvalidDataException("LZ4 Page did not decode to the declared size.");
            return output;
        }

        static int ReadLength(byte[] source, ref int cursor, int end, int nibble)
        {
            int length = nibble;
            if (nibble != 15)
                return length;
            byte value;
            do
            {
                if (cursor >= end)
                    throw new InvalidDataException("LZ4 length extension is truncated.");
                value = source[cursor++];
                length = checked(length + value);
            } while (value == byte.MaxValue);
            return length;
        }

        static void WriteLength(List<byte> output, int extension)
        {
            if (extension < 0)
                return;
            while (extension >= byte.MaxValue)
            {
                output.Add(byte.MaxValue);
                extension -= byte.MaxValue;
            }
            output.Add((byte)extension);
        }

        static void EmitLastLiterals(List<byte> output, byte[] source, int anchor)
        {
            int literalLength = source.Length - anchor;
            output.Add((byte)(Math.Min(15, literalLength) << 4));
            WriteLength(output, literalLength - 15);
            for (int i = anchor; i < source.Length; i++)
                output.Add(source[i]);
        }

        static byte[] EmitLiteralOnly(byte[] source)
        {
            var output = new List<byte>(source.Length + 2);
            EmitLastLiterals(output, source, 0);
            return output.ToArray();
        }

        static uint ReadUInt32(byte[] data, int offset) =>
            (uint)(data[offset] |
                   (data[offset + 1] << 8) |
                   (data[offset + 2] << 16) |
                   (data[offset + 3] << 24));

        static ushort ReadUInt16(byte[] data, int offset) =>
            (ushort)(data[offset] | (data[offset + 1] << 8));

        static int ReadInt32(byte[] data, int offset) => unchecked((int)ReadUInt32(data, offset));
    }
}
