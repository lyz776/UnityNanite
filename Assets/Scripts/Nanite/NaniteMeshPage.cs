using System;
using System.IO;
using UnityEngine;
using UnityEngine.Serialization;

namespace Nanite
{
    /// <summary>一块可流式加载的 Page：若干 Part + 本页 Cluster/索引/顶点数据 + BVH。</summary>
    [CreateAssetMenu(fileName = "NaniteMeshPage", menuName = "Nanite/Mesh Page")]
    public class NaniteMeshPage : ScriptableObject
    {
        [Header("Versioned Page Payload")]
        [SerializeField] TextAsset binaryPayload;
        [SerializeField] string streamingRelativePath;
        [SerializeField] int binaryPayloadOffset;
        [SerializeField] int binaryPayloadSize;
        [SerializeField] NanitePageBinaryStats binaryStats;

        [Header("Legacy Compatibility Payload")]
        public NaniteMeshPart[] parts = Array.Empty<NaniteMeshPart>();
        public NaniteCluster[] clusterArray = Array.Empty<NaniteCluster>();
        public int[] clusterMip = Array.Empty<int>();

        public NaniteBvhNode[] bvhNodes = Array.Empty<NaniteBvhNode>();
        public int bvhRoot = -1;
        public int[] mipBvhRoots = Array.Empty<int>();

        [SerializeField, FormerlySerializedAs("indiceArray")]
        int[] legacyIndices = Array.Empty<int>();
        [SerializeField, FormerlySerializedAs("vertexData")]
        float[] legacyVertexData = Array.Empty<float>();

        [NonSerialized] int[] decodedIndices;
        [NonSerialized] float[] decodedVertexData;
        [NonSerialized] bool binaryDecodeAttempted;
        [NonSerialized] string binaryDecodeError;

        // Layout: position.xyz + uv.xy + normal.xyz + tangent.xyzw
        public int vertexStride = 12;
        public int vertexCount;

        public int[] indiceArray
        {
            get
            {
                if (legacyIndices != null && legacyIndices.Length > 0)
                    return legacyIndices;
                if (decodedIndices == null || decodedIndices.Length == 0)
                    EnsureLegacyPayload(out _);
                return decodedIndices ?? Array.Empty<int>();
            }
            set
            {
                legacyIndices = value ?? Array.Empty<int>();
                decodedIndices = null;
                ResetDecodeState();
            }
        }

        public float[] vertexData
        {
            get
            {
                if (legacyVertexData != null && legacyVertexData.Length > 0)
                    return legacyVertexData;
                if (decodedVertexData == null || decodedVertexData.Length == 0)
                    EnsureLegacyPayload(out _);
                return decodedVertexData ?? Array.Empty<float>();
            }
            set
            {
                legacyVertexData = value ?? Array.Empty<float>();
                decodedVertexData = null;
                ResetDecodeState();
            }
        }

        public TextAsset BinaryPayload => binaryPayload;
        public string StreamingRelativePath => streamingRelativePath ?? string.Empty;
        public int BinaryPayloadOffset => Mathf.Max(0, binaryPayloadOffset);
        public int BinaryPayloadSize => binaryPayloadSize > 0
            ? binaryPayloadSize
            : (binaryPayload != null ? binaryPayload.bytes.Length : 0);
        public NanitePageBinaryStats BinaryStats => binaryStats;
        public int BinaryVertexCount => ResolveBinaryCount(vertex: true);
        public int BinaryIndexCount => ResolveBinaryCount(vertex: false);
        public bool HasStreamingPayload => !string.IsNullOrWhiteSpace(streamingRelativePath);
        public bool HasBinaryPayload => binaryPayload != null || HasStreamingPayload;

        public bool HasLegacyPayload =>
            legacyVertexData != null && legacyVertexData.Length > 0 &&
            legacyIndices != null && legacyIndices.Length > 0 &&
            vertexCount > 0;

        public bool HasDecodedGeometry =>
            decodedVertexData != null && decodedVertexData.Length > 0 &&
            decodedIndices != null && decodedIndices.Length > 0;

        public void SetBinaryPayload(
            TextAsset payload,
            NanitePageBinaryStats stats,
            int payloadOffset = 0,
            int payloadSize = -1,
            string relativeStreamingPath = null)
        {
            binaryPayload = payload;
            streamingRelativePath = NormalizeStreamingRelativePath(relativeStreamingPath);
            binaryPayloadOffset = Mathf.Max(0, payloadOffset);
            binaryPayloadSize = payloadSize > 0
                ? payloadSize
                : (payload != null ? payload.bytes.Length : 0);
            binaryStats = stats;
            ResetDecodeState();
        }

        public bool TryResolveStreamingFilePath(out string fullPath, out string error)
        {
            fullPath = null;
            error = null;
            if (!HasStreamingPayload)
            {
                error = "Page has no StreamingAssets payload.";
                return false;
            }

            try
            {
                string relative = NormalizeStreamingRelativePath(streamingRelativePath);
                if (string.IsNullOrEmpty(relative) || Path.IsPathRooted(relative))
                {
                    error = $"Invalid StreamingAssets Page path: '{streamingRelativePath}'.";
                    return false;
                }

                string root = Path.GetFullPath(Application.streamingAssetsPath);
                string rootWithSeparator = root.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string candidate = Path.GetFullPath(Path.Combine(
                    root,
                    relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
                {
                    error = $"Streaming Page path escaped StreamingAssets: '{streamingRelativePath}'.";
                    return false;
                }

                fullPath = candidate;
                return true;
            }
            catch (Exception exception)
            {
                error = $"Could not resolve StreamingAssets Page path '{streamingRelativePath}': {exception.Message}";
                return false;
            }
        }

        public bool TryGetStoragePayload(out byte[] storagePayload, out string error)
        {
            storagePayload = null;
            if (HasStreamingPayload)
            {
                if (!TryResolveStreamingFilePath(out string fullPath, out error))
                    return false;
                return TryReadFileRange(
                    fullPath,
                    BinaryPayloadOffset,
                    BinaryPayloadSize,
                    out storagePayload,
                    out error);
            }
            if (!HasBinaryPayload)
            {
                error = $"Page has no binary payload (streaming='{streamingRelativePath ?? "<null>"}', text={(binaryPayload != null)}).";
                return false;
            }

            byte[] bulk = binaryPayload.bytes;
            if (!TryResolveStorageRange(bulk, out int offset, out int size, out error))
                return false;

            if (offset == 0 && size == bulk.Length)
            {
                storagePayload = bulk;
                error = null;
                return true;
            }

            storagePayload = new byte[size];
            Buffer.BlockCopy(bulk, offset, storagePayload, 0, size);
            error = null;
            return true;
        }

        public bool TryResolveStorageRange(
            byte[] bulkPayload,
            out int offset,
            out int size,
            out string error)
        {
            offset = Mathf.Max(0, binaryPayloadOffset);
            size = binaryPayloadSize > 0
                ? binaryPayloadSize
                : (bulkPayload != null ? bulkPayload.Length : 0);
            if (bulkPayload == null || offset < 0 || size <= 0 ||
                offset > bulkPayload.Length - size)
            {
                int bulkSize = bulkPayload != null ? bulkPayload.Length : 0;
                error = $"Page payload range [{offset}, {offset + size}) exceeds bulk size {bulkSize}.";
                return false;
            }
            error = null;
            return true;
        }

        public bool TryDecodeBinaryPayload(out NanitePageDecodedData decoded, out string error)
        {
            decoded = null;
            if (!HasBinaryPayload)
            {
                error = $"Page has no binary payload (streaming='{streamingRelativePath ?? "<null>"}', text={(binaryPayload != null)}).";
                return false;
            }
            if (!TryGetStoragePayload(out byte[] storagePayload, out error) ||
                !NanitePageStorageCodec.TryUnpack(
                    storagePayload,
                    0,
                    storagePayload.Length,
                    out byte[] packedPage,
                    out error))
                return false;
            return NanitePageBinaryCodec.TryDecode(packedPage, out decoded, out error);
        }

        /// <summary>
        /// Transitional compatibility path for assets whose YAML arrays were stripped.
        /// Streaming code should decode directly into a page-pool slot instead of calling this.
        /// </summary>
        public bool EnsureLegacyPayload(out string error)
        {
            error = null;
            if (HasLegacyPayload || HasDecodedGeometry)
                return true;

            if (binaryDecodeAttempted)
            {
                error = binaryDecodeError ?? "Page binary decode previously failed.";
                return false;
            }

            binaryDecodeAttempted = true;
            if (!TryDecodeBinaryPayload(out NanitePageDecodedData decoded, out error))
            {
                binaryDecodeError = error;
                Debug.LogError($"[Nanite][PageIO] '{name}' decode failed: {error}", this);
                return false;
            }

            decodedIndices = decoded.indices;
            decodedVertexData = decoded.vertexData;
            if (parts == null || parts.Length == 0)
                parts = decoded.parts;
            if (clusterArray == null || clusterArray.Length == 0)
                clusterArray = decoded.clusters;
            if (clusterMip == null || clusterMip.Length == 0)
                clusterMip = decoded.clusterMip;
            if (bvhNodes == null || bvhNodes.Length == 0)
                bvhNodes = decoded.bvhNodes;
            if (mipBvhRoots == null || mipBvhRoots.Length == 0)
                mipBvhRoots = decoded.mipBvhRoots;
            if (bvhRoot < 0)
                bvhRoot = decoded.bvhRoot;
            vertexStride = decoded.vertexStride;
            vertexCount = decoded.vertexCount;
            binaryDecodeError = null;
            return true;
        }

        /// <summary>Keep the current decoded geometry cache, but stop serializing duplicate YAML arrays.</summary>
        public bool StripLegacyGeometryPayload(out string error)
        {
            error = null;
            if (!HasBinaryPayload)
            {
                error = "Cannot strip legacy geometry before assigning a binary Page payload.";
                return false;
            }

            int[] indices = indiceArray;
            float[] vertices = vertexData;
            if (indices.Length == 0 || vertices.Length == 0)
            {
                error = "Cannot strip an empty Page geometry payload.";
                return false;
            }

            decodedIndices = indices;
            decodedVertexData = vertices;
            legacyIndices = Array.Empty<int>();
            legacyVertexData = Array.Empty<float>();
            binaryDecodeAttempted = true;
            binaryDecodeError = null;
            return true;
        }

        /// <summary>Release the compatibility cache after a GPU page-pool slot owns the geometry.</summary>
        public void ReleaseDecodedGeometryPayload()
        {
            if (HasLegacyPayload)
                return;
            decodedIndices = null;
            decodedVertexData = null;
            ResetDecodeState();
        }

        void ResetDecodeState()
        {
            binaryDecodeAttempted = false;
            binaryDecodeError = null;
        }

        static string NormalizeStreamingRelativePath(string path) =>
            string.IsNullOrWhiteSpace(path)
                ? string.Empty
                : path.Trim().Replace('\\', '/').TrimStart('/');

        int ResolveBinaryCount(bool vertex)
        {
            if (binaryStats.formatVersion < 1 ||
                binaryStats.formatVersion > NanitePageBinaryCodec.CurrentVersion)
                return 0;
            NanitePageBinaryFlags flags = (NanitePageBinaryFlags)binaryStats.flags;
            int recordBytes = vertex
                ? ((flags & NanitePageBinaryFlags.FloatUv) != 0 ? 24 : 20)
                : ((flags & NanitePageBinaryFlags.Index16) != 0 ? 2 : 4);
            int byteCount = vertex ? binaryStats.vertexBytes : binaryStats.indexBytes;
            return byteCount > 0 && byteCount % recordBytes == 0
                ? byteCount / recordBytes
                : 0;
        }

        static bool TryReadFileRange(
            string fullPath,
            int offset,
            int size,
            out byte[] bytes,
            out string error)
        {
            bytes = null;
            error = null;
            if (offset < 0 || size <= 0)
            {
                error = $"Invalid Page file range [{offset}, {offset + size}).";
                return false;
            }

            try
            {
                using var stream = new FileStream(
                    fullPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    64 * 1024,
                    FileOptions.RandomAccess);
                if ((long)offset + size > stream.Length)
                {
                    error = $"Page file range [{offset}, {(long)offset + size}) exceeds '{fullPath}' ({stream.Length} bytes).";
                    return false;
                }

                stream.Seek(offset, SeekOrigin.Begin);
                bytes = new byte[size];
                int read = 0;
                while (read < size)
                {
                    int count = stream.Read(bytes, read, size - read);
                    if (count <= 0)
                        throw new EndOfStreamException($"Unexpected EOF after {read}/{size} bytes.");
                    read += count;
                }
                return true;
            }
            catch (Exception exception)
            {
                bytes = null;
                error = $"Could not read Page range from '{fullPath}': {exception.Message}";
                return false;
            }
        }
    }
}
