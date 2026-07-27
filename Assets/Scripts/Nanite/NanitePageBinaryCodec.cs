using System;
using System.IO;
using UnityEngine;

namespace Nanite
{
    [Flags]
    public enum NanitePageBinaryFlags
    {
        None = 0,
        QuantizedPosition16 = 1 << 0,
        OctNormal16 = 1 << 1,
        OctTangent16 = 1 << 2,
        HalfUv = 1 << 3,
        Index16 = 1 << 4,
        FloatUv = 1 << 5
    }

    [Serializable]
    public struct NanitePageBinaryStats
    {
        public int formatVersion;
        public int flags;
        public int blobBytes;
        public int legacyRawBytes;
        public int vertexBytes;
        public int indexBytes;
        public int hierarchyBytes;
        public int storageBytes;
        public int storageCodec;
        public float rawSizeRatio;
        public float storageSizeRatio;
        public float maxPositionError;
        public float maxUvError;
        public float maxNormalAngleError;
        public float maxTangentAngleError;
    }

    /// <summary>
    /// Decoded compatibility view of a Page blob. The fixed GPU page pool will consume
    /// packed sections directly; this object exists for migration, validation and fallback.
    /// </summary>
    public sealed class NanitePageDecodedData
    {
        public NaniteMeshPart[] parts;
        public NaniteCluster[] clusters;
        public int[] indices;
        public int[] clusterMip;
        public NaniteBvhNode[] bvhNodes;
        public int bvhRoot;
        public int[] mipBvhRoots;
        public float[] vertexData;
        public int vertexStride;
        public int vertexCount;
    }

    /// <summary>
    /// Versioned little-endian Nanite Page blob. V1 keeps hierarchy metadata lossless,
    /// quantizes position/normal/tangent/UV and uses 16-bit local indices when possible.
    /// </summary>
    public static class NanitePageBinaryCodec
    {
        public const uint Magic = 0x3147504E; // "NPG1"
        public const int CurrentVersion = 1;
        public const int HeaderSize = 136;

        const int SectionCount = 7;
        const int CrcOffset = 16;
        const int ClusterBytes = 60;
        const int PartBytes = 48;
        const int BvhNodeBytes = 60;
        const int QuantizedVertexHalfUvBytes = 20;
        const int QuantizedVertexFloatUvBytes = 24;
        const int MaxElementCount = 16 * 1024 * 1024;

        enum Section
        {
            Vertices,
            Indices,
            Clusters,
            Parts,
            ClusterMip,
            BvhNodes,
            MipBvhRoots
        }

        struct SectionRange
        {
            public int offset;
            public int size;
        }

        struct Header
        {
            public NanitePageBinaryFlags flags;
            public int totalSize;
            public uint crc;
            public int vertexCount;
            public int vertexStride;
            public int indexCount;
            public int clusterCount;
            public int partCount;
            public int clusterMipCount;
            public int bvhNodeCount;
            public int mipBvhRootCount;
            public int bvhRoot;
            public Vector3 positionMin;
            public Vector3 positionExtent;
            public SectionRange[] sections;
        }

        public static bool TryEncode(
            NaniteMeshPage page,
            out byte[] blob,
            out NanitePageBinaryStats stats,
            out string error)
        {
            blob = null;
            stats = default;
            error = null;

            if (!TryValidateSource(page, out error))
                return false;

            try
            {
                int vertexCount = page.vertexCount;
                bool floatUv = RequiresFloatUv(page.vertexData, page.vertexStride, vertexCount);
                bool index16 = vertexCount <= ushort.MaxValue;
                var flags = NanitePageBinaryFlags.QuantizedPosition16 |
                            NanitePageBinaryFlags.OctNormal16 |
                            NanitePageBinaryFlags.OctTangent16 |
                            (floatUv ? NanitePageBinaryFlags.FloatUv : NanitePageBinaryFlags.HalfUv) |
                            (index16 ? NanitePageBinaryFlags.Index16 : NanitePageBinaryFlags.None);

                CalculatePositionBounds(page.vertexData, page.vertexStride, vertexCount, out Vector3 min, out Vector3 extent);

                byte[][] sections =
                {
                    EncodeVertices(page.vertexData, page.vertexStride, vertexCount, min, extent, floatUv),
                    EncodeIndices(page.indiceArray, index16),
                    EncodeClusters(page.clusterArray),
                    EncodeParts(page.parts),
                    EncodeInts(page.clusterMip),
                    EncodeBvhNodes(page.bvhNodes),
                    EncodeInts(page.mipBvhRoots)
                };

                var ranges = new SectionRange[SectionCount];
                int totalSize = HeaderSize;
                for (int i = 0; i < sections.Length; i++)
                {
                    totalSize = Align4(totalSize);
                    ranges[i].offset = totalSize;
                    ranges[i].size = sections[i].Length;
                    totalSize = checked(totalSize + sections[i].Length);
                }

                blob = new byte[totalSize];
                using (var stream = new MemoryStream(blob, true))
                using (var writer = new BinaryWriter(stream))
                {
                    writer.Write(Magic);
                    writer.Write((ushort)CurrentVersion);
                    writer.Write((ushort)HeaderSize);
                    writer.Write((uint)flags);
                    writer.Write(totalSize);
                    writer.Write(0u);
                    writer.Write(vertexCount);
                    writer.Write(page.vertexStride);
                    writer.Write(page.indiceArray.Length);
                    writer.Write(page.clusterArray.Length);
                    writer.Write(page.parts.Length);
                    writer.Write(page.clusterMip.Length);
                    writer.Write(page.bvhNodes.Length);
                    writer.Write(page.mipBvhRoots.Length);
                    writer.Write(page.bvhRoot);
                    WriteVector3(writer, min);
                    WriteVector3(writer, extent);
                    for (int i = 0; i < ranges.Length; i++)
                    {
                        writer.Write(ranges[i].offset);
                        writer.Write(ranges[i].size);
                    }

                    if (stream.Position != HeaderSize)
                        throw new InvalidDataException($"Unexpected Page header size {stream.Position}, expected {HeaderSize}.");

                    for (int i = 0; i < sections.Length; i++)
                    {
                        stream.Position = ranges[i].offset;
                        writer.Write(sections[i]);
                    }
                }

                uint crc = Crc32.ComputeBlob(blob, CrcOffset, sizeof(uint));
                WriteUInt32LittleEndian(blob, CrcOffset, crc);

                if (!TryValidateRoundTrip(page, blob, out stats, out error))
                {
                    blob = null;
                    return false;
                }

                stats.formatVersion = CurrentVersion;
                stats.flags = (int)flags;
                stats.blobBytes = blob.Length;
                stats.legacyRawBytes = CalculateLegacyRawBytes(page);
                stats.vertexBytes = sections[(int)Section.Vertices].Length;
                stats.indexBytes = sections[(int)Section.Indices].Length;
                stats.hierarchyBytes = sections[(int)Section.Clusters].Length +
                                       sections[(int)Section.Parts].Length +
                                       sections[(int)Section.ClusterMip].Length +
                                       sections[(int)Section.BvhNodes].Length +
                                       sections[(int)Section.MipBvhRoots].Length;
                stats.rawSizeRatio = stats.legacyRawBytes > 0
                    ? stats.blobBytes / (float)stats.legacyRawBytes
                    : 1f;
                return true;
            }
            catch (Exception exception)
            {
                blob = null;
                stats = default;
                error = exception.Message;
                return false;
            }
        }

        public static bool TryDecode(byte[] blob, out NanitePageDecodedData data, out string error)
        {
            data = null;
            error = null;

            if (!TryReadHeader(blob, out Header header, out error))
                return false;

            try
            {
                bool floatUv = (header.flags & NanitePageBinaryFlags.FloatUv) != 0;
                bool index16 = (header.flags & NanitePageBinaryFlags.Index16) != 0;
                int vertexRecordBytes = floatUv ? QuantizedVertexFloatUvBytes : QuantizedVertexHalfUvBytes;

                ValidateSectionSize(header, Section.Vertices, header.vertexCount, vertexRecordBytes);
                ValidateSectionSize(header, Section.Indices, header.indexCount, index16 ? 2 : 4);
                ValidateSectionSize(header, Section.Clusters, header.clusterCount, ClusterBytes);
                ValidateSectionSize(header, Section.Parts, header.partCount, PartBytes);
                ValidateSectionSize(header, Section.ClusterMip, header.clusterMipCount, sizeof(int));
                ValidateSectionSize(header, Section.BvhNodes, header.bvhNodeCount, BvhNodeBytes);
                ValidateSectionSize(header, Section.MipBvhRoots, header.mipBvhRootCount, sizeof(int));

                data = new NanitePageDecodedData
                {
                    vertexStride = header.vertexStride,
                    vertexCount = header.vertexCount,
                    bvhRoot = header.bvhRoot,
                    vertexData = DecodeVertices(blob, header, floatUv),
                    indices = DecodeIndices(blob, header, index16),
                    clusters = DecodeClusters(blob, header),
                    parts = DecodeParts(blob, header),
                    clusterMip = DecodeInts(blob, header.sections[(int)Section.ClusterMip], header.clusterMipCount),
                    bvhNodes = DecodeBvhNodes(blob, header),
                    mipBvhRoots = DecodeInts(blob, header.sections[(int)Section.MipBvhRoots], header.mipBvhRootCount)
                };
                return true;
            }
            catch (Exception exception)
            {
                data = null;
                error = exception.Message;
                return false;
            }
        }

        /// <summary>Validates magic/version/section ranges/size and CRC without decoding geometry.</summary>
        public static bool TryValidateBlob(byte[] blob, out string error)
        {
            return TryReadHeader(blob, out _, out error);
        }

        public static bool TryValidateRoundTrip(
            NaniteMeshPage source,
            byte[] blob,
            out NanitePageBinaryStats stats,
            out string error)
        {
            stats = default;
            error = null;

            if (!TryReadHeader(blob, out Header header, out error))
                return false;
            if (!TryDecode(blob, out NanitePageDecodedData decoded, out error))
                return false;

            stats.formatVersion = CurrentVersion;
            stats.flags = (int)header.flags;
            stats.blobBytes = blob.Length;

            if (!CountsMatch(source, decoded, out error) ||
                !IntsEqual(source.indiceArray, decoded.indices, "indices", out error) ||
                !IntsEqual(source.clusterMip, decoded.clusterMip, "clusterMip", out error) ||
                !IntsEqual(source.mipBvhRoots, decoded.mipBvhRoots, "mipBvhRoots", out error) ||
                !ClustersEqual(source.clusterArray, decoded.clusters, out error) ||
                !PartsEqual(source.parts, decoded.parts, out error) ||
                !BvhNodesEqual(source.bvhNodes, decoded.bvhNodes, out error))
            {
                return false;
            }

            int stride = source.vertexStride;
            for (int i = 0; i < source.vertexCount; i++)
            {
                int o = i * stride;
                var srcPosition = new Vector3(source.vertexData[o], source.vertexData[o + 1], source.vertexData[o + 2]);
                var dstPosition = new Vector3(decoded.vertexData[o], decoded.vertexData[o + 1], decoded.vertexData[o + 2]);
                stats.maxPositionError = Mathf.Max(stats.maxPositionError, Vector3.Distance(srcPosition, dstPosition));

                var srcUv = new Vector2(source.vertexData[o + 3], source.vertexData[o + 4]);
                var dstUv = new Vector2(decoded.vertexData[o + 3], decoded.vertexData[o + 4]);
                stats.maxUvError = Mathf.Max(stats.maxUvError, Vector2.Distance(srcUv, dstUv));

                var srcNormal = new Vector3(source.vertexData[o + 5], source.vertexData[o + 6], source.vertexData[o + 7]);
                var dstNormal = new Vector3(decoded.vertexData[o + 5], decoded.vertexData[o + 6], decoded.vertexData[o + 7]);
                stats.maxNormalAngleError = Mathf.Max(stats.maxNormalAngleError, Vector3.Angle(srcNormal, dstNormal));

                var srcTangent = new Vector3(source.vertexData[o + 8], source.vertexData[o + 9], source.vertexData[o + 10]);
                var dstTangent = new Vector3(decoded.vertexData[o + 8], decoded.vertexData[o + 9], decoded.vertexData[o + 10]);
                stats.maxTangentAngleError = Mathf.Max(stats.maxTangentAngleError, Vector3.Angle(srcTangent, dstTangent));
                if (Mathf.Sign(source.vertexData[o + 11]) != Mathf.Sign(decoded.vertexData[o + 11]))
                {
                    error = $"Tangent sign mismatch at vertex {i}.";
                    return false;
                }
            }

            CalculatePositionBounds(source.vertexData, source.vertexStride, source.vertexCount, out _, out Vector3 extent);
            float positionTolerance = Mathf.Max(1e-6f, extent.magnitude / ushort.MaxValue);
            bool uvFloat = (header.flags & NanitePageBinaryFlags.FloatUv) != 0;
            float uvTolerance = uvFloat ? 1e-6f : CalculateHalfUvTolerance(source.vertexData, stride, source.vertexCount);
            if (stats.maxPositionError > positionTolerance ||
                stats.maxUvError > uvTolerance ||
                stats.maxNormalAngleError > 0.1f ||
                stats.maxTangentAngleError > 0.1f)
            {
                error = $"Quantization error exceeded tolerance: pos={stats.maxPositionError}/{positionTolerance}, " +
                        $"uv={stats.maxUvError}/{uvTolerance}, normal={stats.maxNormalAngleError}/0.1deg, " +
                        $"tangent={stats.maxTangentAngleError}/0.1deg.";
                return false;
            }

            return true;
        }

        public static int CalculateLegacyRawBytes(NaniteMeshPage page)
        {
            if (ReferenceEquals(page, null))
                return 0;

            long bytes = sizeof(int);
            bytes += (long)(page.vertexData?.Length ?? 0) * sizeof(float);
            bytes += (long)(page.indiceArray?.Length ?? 0) * sizeof(int);
            bytes += (long)(page.clusterArray?.Length ?? 0) * ClusterBytes;
            bytes += (long)(page.parts?.Length ?? 0) * PartBytes;
            bytes += (long)(page.clusterMip?.Length ?? 0) * sizeof(int);
            bytes += (long)(page.bvhNodes?.Length ?? 0) * BvhNodeBytes;
            bytes += (long)(page.mipBvhRoots?.Length ?? 0) * sizeof(int);
            return bytes > int.MaxValue ? int.MaxValue : (int)bytes;
        }

        static bool TryValidateSource(NaniteMeshPage page, out string error)
        {
            error = null;
            if (ReferenceEquals(page, null))
            {
                error = "Page is null.";
                return false;
            }

            if (page.vertexStride < 12 || page.vertexCount <= 0 || page.vertexData == null ||
                page.vertexData.Length < page.vertexCount * page.vertexStride)
            {
                error = "Page vertex payload is missing or uses an unsupported stride.";
                return false;
            }

            if (page.indiceArray == null || page.clusterArray == null || page.parts == null ||
                page.clusterMip == null || page.bvhNodes == null || page.mipBvhRoots == null)
            {
                error = "Page contains null legacy arrays.";
                return false;
            }

            if ((page.indiceArray.Length % 3) != 0 || page.clusterMip.Length != page.clusterArray.Length)
            {
                error = "Page triangle indices or cluster mip metadata have inconsistent counts.";
                return false;
            }

            for (int vertex = 0; vertex < page.vertexCount; vertex++)
            {
                int o = vertex * page.vertexStride;
                for (int component = 0; component < 12; component++)
                {
                    if (IsFinite(page.vertexData[o + component]))
                        continue;
                    error = $"Vertex {vertex} component {component} is not finite.";
                    return false;
                }
            }

            for (int i = 0; i < page.indiceArray.Length; i++)
            {
                int index = page.indiceArray[i];
                if (index < 0 || index >= page.vertexCount)
                {
                    error = $"Index {i} is outside the local vertex range: {index}/{page.vertexCount}.";
                    return false;
                }
            }

            return true;
        }

        static bool TryReadHeader(byte[] blob, out Header header, out string error)
        {
            header = default;
            error = null;
            if (blob == null || blob.Length < HeaderSize)
            {
                error = "Page blob is null or smaller than the V1 header.";
                return false;
            }

            try
            {
                using var stream = new MemoryStream(blob, false);
                using var reader = new BinaryReader(stream);
                uint magic = reader.ReadUInt32();
                int version = reader.ReadUInt16();
                int headerSize = reader.ReadUInt16();
                if (magic != Magic)
                    throw new InvalidDataException($"Invalid Page magic 0x{magic:X8}.");
                if (version != CurrentVersion)
                    throw new InvalidDataException($"Unsupported Page version {version}; expected {CurrentVersion}.");
                if (headerSize != HeaderSize)
                    throw new InvalidDataException($"Unsupported Page header size {headerSize}; expected {HeaderSize}.");

                header.flags = (NanitePageBinaryFlags)reader.ReadUInt32();
                header.totalSize = reader.ReadInt32();
                header.crc = reader.ReadUInt32();
                header.vertexCount = ReadCount(reader, "vertex");
                header.vertexStride = ReadCount(reader, "vertex stride");
                header.indexCount = ReadCount(reader, "index");
                header.clusterCount = ReadCount(reader, "cluster");
                header.partCount = ReadCount(reader, "part");
                header.clusterMipCount = ReadCount(reader, "cluster mip");
                header.bvhNodeCount = ReadCount(reader, "BVH node");
                header.mipBvhRootCount = ReadCount(reader, "mip BVH root");
                header.bvhRoot = reader.ReadInt32();
                header.positionMin = ReadVector3(reader);
                header.positionExtent = ReadVector3(reader);
                header.sections = new SectionRange[SectionCount];
                for (int i = 0; i < SectionCount; i++)
                {
                    header.sections[i].offset = reader.ReadInt32();
                    header.sections[i].size = reader.ReadInt32();
                }

                if (header.totalSize != blob.Length)
                    throw new InvalidDataException($"Page size mismatch: header={header.totalSize}, actual={blob.Length}.");
                if (header.vertexStride < 12 || header.vertexStride > 64)
                    throw new InvalidDataException($"Invalid decoded vertex stride {header.vertexStride}.");

                const NanitePageBinaryFlags knownFlags =
                    NanitePageBinaryFlags.QuantizedPosition16 |
                    NanitePageBinaryFlags.OctNormal16 |
                    NanitePageBinaryFlags.OctTangent16 |
                    NanitePageBinaryFlags.HalfUv |
                    NanitePageBinaryFlags.Index16 |
                    NanitePageBinaryFlags.FloatUv;
                const NanitePageBinaryFlags requiredFlags =
                    NanitePageBinaryFlags.QuantizedPosition16 |
                    NanitePageBinaryFlags.OctNormal16 |
                    NanitePageBinaryFlags.OctTangent16;
                bool halfUv = (header.flags & NanitePageBinaryFlags.HalfUv) != 0;
                bool floatUv = (header.flags & NanitePageBinaryFlags.FloatUv) != 0;
                if ((header.flags & ~knownFlags) != 0 || (header.flags & requiredFlags) != requiredFlags || halfUv == floatUv)
                    throw new InvalidDataException($"Invalid Page flags 0x{(uint)header.flags:X8}.");
                if (header.vertexCount == 0 || header.indexCount == 0)
                    throw new InvalidDataException("Page contains no geometry.");

                int expectedOffset = HeaderSize;
                for (int i = 0; i < header.sections.Length; i++)
                {
                    SectionRange section = header.sections[i];
                    expectedOffset = Align4(expectedOffset);
                    if (section.offset < HeaderSize || section.size < 0 ||
                        (long)section.offset + section.size > header.totalSize)
                    {
                        throw new InvalidDataException($"Section {(Section)i} is outside the Page blob.");
                    }
                    if (section.offset != expectedOffset)
                        throw new InvalidDataException($"Section {(Section)i} is not in canonical order.");
                    expectedOffset = checked(section.offset + section.size);
                }
                if (expectedOffset != header.totalSize)
                    throw new InvalidDataException("Page contains trailing or unaddressed bytes.");

                uint actualCrc = Crc32.ComputeBlob(blob, CrcOffset, sizeof(uint));
                if (actualCrc != header.crc)
                    throw new InvalidDataException($"Page CRC mismatch: header=0x{header.crc:X8}, actual=0x{actualCrc:X8}.");
                return true;
            }
            catch (Exception exception)
            {
                header = default;
                error = exception.Message;
                return false;
            }
        }

        static byte[] EncodeVertices(float[] source, int stride, int count, Vector3 min, Vector3 extent, bool floatUv)
        {
            int recordBytes = floatUv ? QuantizedVertexFloatUvBytes : QuantizedVertexHalfUvBytes;
            using var stream = new MemoryStream(checked(count * recordBytes));
            using var writer = new BinaryWriter(stream);
            for (int i = 0; i < count; i++)
            {
                int o = i * stride;
                writer.Write(QuantizeUnorm16(source[o], min.x, extent.x));
                writer.Write(QuantizeUnorm16(source[o + 1], min.y, extent.y));
                writer.Write(QuantizeUnorm16(source[o + 2], min.z, extent.z));

                EncodeOctahedral(new Vector3(source[o + 5], source[o + 6], source[o + 7]), out short nx, out short ny);
                writer.Write(nx);
                writer.Write(ny);
                EncodeOctahedral(new Vector3(source[o + 8], source[o + 9], source[o + 10]), out short tx, out short ty);
                writer.Write(tx);
                writer.Write(ty);

                if (floatUv)
                {
                    writer.Write(source[o + 3]);
                    writer.Write(source[o + 4]);
                }
                else
                {
                    writer.Write(FloatToHalf(source[o + 3]));
                    writer.Write(FloatToHalf(source[o + 4]));
                }

                writer.Write((short)(source[o + 11] < 0f ? -1 : 1));
            }
            return stream.ToArray();
        }

        static float[] DecodeVertices(byte[] blob, Header header, bool floatUv)
        {
            var result = new float[checked(header.vertexCount * header.vertexStride)];
            using var stream = OpenSection(blob, header.sections[(int)Section.Vertices]);
            using var reader = new BinaryReader(stream);
            for (int i = 0; i < header.vertexCount; i++)
            {
                int o = i * header.vertexStride;
                result[o] = DequantizeUnorm16(reader.ReadUInt16(), header.positionMin.x, header.positionExtent.x);
                result[o + 1] = DequantizeUnorm16(reader.ReadUInt16(), header.positionMin.y, header.positionExtent.y);
                result[o + 2] = DequantizeUnorm16(reader.ReadUInt16(), header.positionMin.z, header.positionExtent.z);

                Vector3 normal = DecodeOctahedral(reader.ReadInt16(), reader.ReadInt16());
                Vector3 tangent = DecodeOctahedral(reader.ReadInt16(), reader.ReadInt16());
                result[o + 3] = floatUv ? reader.ReadSingle() : HalfToFloat(reader.ReadUInt16());
                result[o + 4] = floatUv ? reader.ReadSingle() : HalfToFloat(reader.ReadUInt16());
                result[o + 5] = normal.x;
                result[o + 6] = normal.y;
                result[o + 7] = normal.z;
                result[o + 8] = tangent.x;
                result[o + 9] = tangent.y;
                result[o + 10] = tangent.z;
                result[o + 11] = reader.ReadInt16() < 0 ? -1f : 1f;
            }
            return result;
        }

        static byte[] EncodeIndices(int[] source, bool index16)
        {
            using var stream = new MemoryStream(checked(source.Length * (index16 ? 2 : 4)));
            using var writer = new BinaryWriter(stream);
            for (int i = 0; i < source.Length; i++)
            {
                if (index16)
                    writer.Write((ushort)source[i]);
                else
                    writer.Write(source[i]);
            }
            return stream.ToArray();
        }

        static int[] DecodeIndices(byte[] blob, Header header, bool index16)
        {
            var result = new int[header.indexCount];
            using var stream = OpenSection(blob, header.sections[(int)Section.Indices]);
            using var reader = new BinaryReader(stream);
            for (int i = 0; i < result.Length; i++)
                result[i] = index16 ? reader.ReadUInt16() : reader.ReadInt32();
            return result;
        }

        static byte[] EncodeClusters(NaniteCluster[] source)
        {
            using var stream = new MemoryStream(checked(source.Length * ClusterBytes));
            using var writer = new BinaryWriter(stream);
            for (int i = 0; i < source.Length; i++)
            {
                NaniteCluster cluster = source[i];
                writer.Write(cluster.indiceIndex);
                writer.Write(cluster.indiceCount);
                writer.Write(cluster.selfError);
                writer.Write(cluster.parentError);
                WriteVector4(writer, cluster.selfSphere);
                WriteVector4(writer, cluster.parentSphere);
                writer.Write(cluster.subMeshId);
                writer.Write(cluster.partIndex);
                writer.Write(cluster.vertexOffset);
            }
            return stream.ToArray();
        }

        static NaniteCluster[] DecodeClusters(byte[] blob, Header header)
        {
            var result = new NaniteCluster[header.clusterCount];
            using var stream = OpenSection(blob, header.sections[(int)Section.Clusters]);
            using var reader = new BinaryReader(stream);
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = new NaniteCluster
                {
                    indiceIndex = reader.ReadInt32(),
                    indiceCount = reader.ReadInt32(),
                    selfError = reader.ReadSingle(),
                    parentError = reader.ReadSingle(),
                    selfSphere = ReadVector4(reader),
                    parentSphere = ReadVector4(reader),
                    subMeshId = reader.ReadInt32(),
                    partIndex = reader.ReadInt32(),
                    vertexOffset = reader.ReadInt32()
                };
            }
            return result;
        }

        static byte[] EncodeParts(NaniteMeshPart[] source)
        {
            using var stream = new MemoryStream(checked(source.Length * PartBytes));
            using var writer = new BinaryWriter(stream);
            for (int i = 0; i < source.Length; i++)
            {
                NaniteMeshPart part = source[i];
                writer.Write(part.clusterStart);
                writer.Write(part.clusterCount);
                writer.Write(part.mipLevel);
                WriteVector4(writer, part.selfSphere);
                WriteVector4(writer, part.parentSphere);
                writer.Write(part.maxParentLodError);
            }
            return stream.ToArray();
        }

        static NaniteMeshPart[] DecodeParts(byte[] blob, Header header)
        {
            var result = new NaniteMeshPart[header.partCount];
            using var stream = OpenSection(blob, header.sections[(int)Section.Parts]);
            using var reader = new BinaryReader(stream);
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = new NaniteMeshPart
                {
                    clusterStart = reader.ReadInt32(),
                    clusterCount = reader.ReadInt32(),
                    mipLevel = reader.ReadInt32(),
                    selfSphere = ReadVector4(reader),
                    parentSphere = ReadVector4(reader),
                    maxParentLodError = reader.ReadSingle()
                };
            }
            return result;
        }

        static byte[] EncodeBvhNodes(NaniteBvhNode[] source)
        {
            using var stream = new MemoryStream(checked(source.Length * BvhNodeBytes));
            using var writer = new BinaryWriter(stream);
            for (int i = 0; i < source.Length; i++)
            {
                NaniteBvhNode node = source[i];
                WriteVector4(writer, node.sphere);
                WriteVector4(writer, node.lodSphere);
                writer.Write(node.maxParentLodError);
                writer.Write(node.child0);
                writer.Write(node.child1);
                writer.Write(node.child2);
                writer.Write(node.child3);
                writer.Write(node.childCount);
                writer.Write(node.partIndex);
            }
            return stream.ToArray();
        }

        static NaniteBvhNode[] DecodeBvhNodes(byte[] blob, Header header)
        {
            var result = new NaniteBvhNode[header.bvhNodeCount];
            using var stream = OpenSection(blob, header.sections[(int)Section.BvhNodes]);
            using var reader = new BinaryReader(stream);
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = new NaniteBvhNode
                {
                    sphere = ReadVector4(reader),
                    lodSphere = ReadVector4(reader),
                    maxParentLodError = reader.ReadSingle(),
                    child0 = reader.ReadInt32(),
                    child1 = reader.ReadInt32(),
                    child2 = reader.ReadInt32(),
                    child3 = reader.ReadInt32(),
                    childCount = reader.ReadInt32(),
                    partIndex = reader.ReadInt32()
                };
            }
            return result;
        }

        static byte[] EncodeInts(int[] source)
        {
            using var stream = new MemoryStream(checked(source.Length * sizeof(int)));
            using var writer = new BinaryWriter(stream);
            for (int i = 0; i < source.Length; i++)
                writer.Write(source[i]);
            return stream.ToArray();
        }

        static int[] DecodeInts(byte[] blob, SectionRange section, int count)
        {
            var result = new int[count];
            using var stream = OpenSection(blob, section);
            using var reader = new BinaryReader(stream);
            for (int i = 0; i < result.Length; i++)
                result[i] = reader.ReadInt32();
            return result;
        }

        static MemoryStream OpenSection(byte[] blob, SectionRange section) =>
            new MemoryStream(blob, section.offset, section.size, false);

        static void ValidateSectionSize(Header header, Section section, int count, int stride)
        {
            int expected = checked(count * stride);
            int actual = header.sections[(int)section].size;
            if (actual != expected)
                throw new InvalidDataException($"Section {section} size mismatch: {actual}, expected {expected}.");
        }

        static int ReadCount(BinaryReader reader, string label)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > MaxElementCount)
                throw new InvalidDataException($"Invalid {label} count {count}.");
            return count;
        }

        static bool RequiresFloatUv(float[] vertices, int stride, int count)
        {
            for (int i = 0; i < count; i++)
            {
                int o = i * stride;
                float u = vertices[o + 3];
                float v = vertices[o + 4];
                if (!IsFinite(u) || !IsFinite(v) || Mathf.Abs(u) > 65504f || Mathf.Abs(v) > 65504f)
                    return true;
            }
            return false;
        }

        static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        static void CalculatePositionBounds(float[] vertices, int stride, int count, out Vector3 min, out Vector3 extent)
        {
            min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (int i = 0; i < count; i++)
            {
                int o = i * stride;
                var position = new Vector3(vertices[o], vertices[o + 1], vertices[o + 2]);
                min = Vector3.Min(min, position);
                max = Vector3.Max(max, position);
            }
            extent = max - min;
        }

        static ushort QuantizeUnorm16(float value, float min, float extent)
        {
            if (extent <= 1e-20f)
                return 0;
            return (ushort)Mathf.RoundToInt(Mathf.Clamp01((value - min) / extent) * ushort.MaxValue);
        }

        static float DequantizeUnorm16(ushort value, float min, float extent) =>
            extent <= 1e-20f ? min : min + value * (extent / ushort.MaxValue);

        static void EncodeOctahedral(Vector3 value, out short x, out short y)
        {
            if (value.sqrMagnitude < 1e-20f)
                value = Vector3.forward;
            value.Normalize();
            float inverseL1 = 1f / (Mathf.Abs(value.x) + Mathf.Abs(value.y) + Mathf.Abs(value.z));
            Vector2 oct = new Vector2(value.x * inverseL1, value.y * inverseL1);
            if (value.z < 0f)
            {
                float oldX = oct.x;
                oct.x = (1f - Mathf.Abs(oct.y)) * SignNotZero(oldX);
                oct.y = (1f - Mathf.Abs(oldX)) * SignNotZero(oct.y);
            }
            x = QuantizeSnorm16(oct.x);
            y = QuantizeSnorm16(oct.y);
        }

        static Vector3 DecodeOctahedral(short x, short y)
        {
            float fx = Mathf.Clamp(x / 32767f, -1f, 1f);
            float fy = Mathf.Clamp(y / 32767f, -1f, 1f);
            var value = new Vector3(fx, fy, 1f - Mathf.Abs(fx) - Mathf.Abs(fy));
            if (value.z < 0f)
            {
                float oldX = value.x;
                value.x = (1f - Mathf.Abs(value.y)) * SignNotZero(oldX);
                value.y = (1f - Mathf.Abs(oldX)) * SignNotZero(value.y);
            }
            return value.sqrMagnitude > 1e-20f ? value.normalized : Vector3.forward;
        }

        static short QuantizeSnorm16(float value) =>
            (short)Mathf.RoundToInt(Mathf.Clamp(value, -1f, 1f) * 32767f);

        static float SignNotZero(float value) => value < 0f ? -1f : 1f;

        static ushort FloatToHalf(float value)
        {
            uint bits = unchecked((uint)BitConverter.SingleToInt32Bits(value));
            uint sign = (bits >> 16) & 0x8000u;
            uint mantissa = bits & 0x007FFFFFu;
            int exponent = (int)((bits >> 23) & 0xFFu);

            if (exponent == 255)
                return (ushort)(sign | (mantissa == 0 ? 0x7C00u : 0x7E00u));

            int halfExponent = exponent - 127 + 15;
            if (halfExponent >= 31)
                return (ushort)(sign | 0x7C00u);
            if (halfExponent <= 0)
            {
                if (halfExponent < -10)
                    return (ushort)sign;
                mantissa |= 0x00800000u;
                int shift = 14 - halfExponent;
                uint rounded = (mantissa + (1u << (shift - 1))) >> shift;
                return (ushort)(sign | rounded);
            }

            uint halfMantissa = (mantissa + 0x00001000u) >> 13;
            if (halfMantissa == 0x400u)
            {
                halfMantissa = 0;
                halfExponent++;
                if (halfExponent >= 31)
                    return (ushort)(sign | 0x7C00u);
            }
            return (ushort)(sign | ((uint)halfExponent << 10) | (halfMantissa & 0x3FFu));
        }

        static float HalfToFloat(ushort value)
        {
            uint sign = (uint)(value & 0x8000) << 16;
            uint exponent = (uint)(value >> 10) & 0x1Fu;
            uint mantissa = (uint)value & 0x3FFu;
            uint bits;
            if (exponent == 0)
            {
                if (mantissa == 0)
                {
                    bits = sign;
                }
                else
                {
                    int exp = -14;
                    while ((mantissa & 0x400u) == 0)
                    {
                        mantissa <<= 1;
                        exp--;
                    }
                    mantissa &= 0x3FFu;
                    bits = sign | ((uint)(exp + 127) << 23) | (mantissa << 13);
                }
            }
            else if (exponent == 31)
            {
                bits = sign | 0x7F800000u | (mantissa << 13);
            }
            else
            {
                bits = sign | ((exponent - 15u + 127u) << 23) | (mantissa << 13);
            }
            return BitConverter.Int32BitsToSingle(unchecked((int)bits));
        }

        static float CalculateHalfUvTolerance(float[] vertices, int stride, int count)
        {
            float maxAbs = 1f;
            for (int i = 0; i < count; i++)
            {
                int o = i * stride;
                maxAbs = Mathf.Max(maxAbs, Mathf.Abs(vertices[o + 3]), Mathf.Abs(vertices[o + 4]));
            }
            return Mathf.Max(1e-5f, maxAbs * 0.001f);
        }

        static bool CountsMatch(NaniteMeshPage source, NanitePageDecodedData decoded, out string error)
        {
            error = null;
            if (source.vertexCount != decoded.vertexCount || source.vertexStride != decoded.vertexStride ||
                source.indiceArray.Length != decoded.indices.Length ||
                source.clusterArray.Length != decoded.clusters.Length || source.parts.Length != decoded.parts.Length ||
                source.clusterMip.Length != decoded.clusterMip.Length || source.bvhNodes.Length != decoded.bvhNodes.Length ||
                source.mipBvhRoots.Length != decoded.mipBvhRoots.Length || source.bvhRoot != decoded.bvhRoot)
            {
                error = "Decoded Page counts or roots do not match the source Page.";
                return false;
            }
            return true;
        }

        static bool IntsEqual(int[] a, int[] b, string label, out string error)
        {
            error = null;
            if (a.Length != b.Length)
            {
                error = $"{label} length mismatch.";
                return false;
            }
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] == b[i])
                    continue;
                error = $"{label} mismatch at {i}: {a[i]} != {b[i]}.";
                return false;
            }
            return true;
        }

        static bool ClustersEqual(NaniteCluster[] a, NaniteCluster[] b, out string error)
        {
            error = null;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i].indiceIndex == b[i].indiceIndex && a[i].indiceCount == b[i].indiceCount &&
                    a[i].selfError.Equals(b[i].selfError) && a[i].parentError.Equals(b[i].parentError) &&
                    a[i].selfSphere.Equals(b[i].selfSphere) && a[i].parentSphere.Equals(b[i].parentSphere) &&
                    a[i].subMeshId == b[i].subMeshId && a[i].partIndex == b[i].partIndex &&
                    a[i].vertexOffset == b[i].vertexOffset)
                    continue;
                error = $"Cluster metadata mismatch at {i}.";
                return false;
            }
            return true;
        }

        static bool PartsEqual(NaniteMeshPart[] a, NaniteMeshPart[] b, out string error)
        {
            error = null;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i].clusterStart == b[i].clusterStart && a[i].clusterCount == b[i].clusterCount &&
                    a[i].mipLevel == b[i].mipLevel && a[i].selfSphere.Equals(b[i].selfSphere) &&
                    a[i].parentSphere.Equals(b[i].parentSphere) &&
                    a[i].maxParentLodError.Equals(b[i].maxParentLodError))
                    continue;
                error = $"Part metadata mismatch at {i}.";
                return false;
            }
            return true;
        }

        static bool BvhNodesEqual(NaniteBvhNode[] a, NaniteBvhNode[] b, out string error)
        {
            error = null;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i].sphere.Equals(b[i].sphere) && a[i].lodSphere.Equals(b[i].lodSphere) &&
                    a[i].maxParentLodError.Equals(b[i].maxParentLodError) && a[i].child0 == b[i].child0 &&
                    a[i].child1 == b[i].child1 && a[i].child2 == b[i].child2 &&
                    a[i].child3 == b[i].child3 && a[i].childCount == b[i].childCount &&
                    a[i].partIndex == b[i].partIndex)
                    continue;
                error = $"BVH metadata mismatch at {i}.";
                return false;
            }
            return true;
        }

        static void WriteVector3(BinaryWriter writer, Vector3 value)
        {
            writer.Write(value.x);
            writer.Write(value.y);
            writer.Write(value.z);
        }

        static Vector3 ReadVector3(BinaryReader reader) =>
            new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

        static void WriteVector4(BinaryWriter writer, Vector4 value)
        {
            writer.Write(value.x);
            writer.Write(value.y);
            writer.Write(value.z);
            writer.Write(value.w);
        }

        static Vector4 ReadVector4(BinaryReader reader) =>
            new Vector4(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

        static int Align4(int value) => (value + 3) & ~3;

        static void WriteUInt32LittleEndian(byte[] target, int offset, uint value)
        {
            target[offset] = (byte)value;
            target[offset + 1] = (byte)(value >> 8);
            target[offset + 2] = (byte)(value >> 16);
            target[offset + 3] = (byte)(value >> 24);
        }

        static class Crc32
        {
            static readonly uint[] Table = BuildTable();

            public static uint ComputeBlob(byte[] data, int zeroOffset, int zeroCount)
            {
                uint crc = 0xFFFFFFFFu;
                int zeroEnd = checked(zeroOffset + zeroCount);
                for (int i = 0; i < data.Length; i++)
                {
                    byte value = i >= zeroOffset && i < zeroEnd ? (byte)0 : data[i];
                    crc = Table[(int)((crc ^ value) & 0xFF)] ^ (crc >> 8);
                }
                return ~crc;
            }

            static uint[] BuildTable()
            {
                var table = new uint[256];
                for (uint i = 0; i < table.Length; i++)
                {
                    uint value = i;
                    for (int bit = 0; bit < 8; bit++)
                        value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
                    table[i] = value;
                }
                return table;
            }
        }
    }
}
