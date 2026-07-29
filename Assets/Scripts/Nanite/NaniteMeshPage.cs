using System;
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
                EnsureLegacyPayload(out _);
                return legacyIndices != null && legacyIndices.Length > 0
                    ? legacyIndices
                    : decodedIndices ?? Array.Empty<int>();
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
                EnsureLegacyPayload(out _);
                return legacyVertexData != null && legacyVertexData.Length > 0
                    ? legacyVertexData
                    : decodedVertexData ?? Array.Empty<float>();
            }
            set
            {
                legacyVertexData = value ?? Array.Empty<float>();
                decodedVertexData = null;
                ResetDecodeState();
            }
        }

        public TextAsset BinaryPayload => binaryPayload;
        public int BinaryPayloadOffset => Mathf.Max(0, binaryPayloadOffset);
        public int BinaryPayloadSize => binaryPayloadSize > 0
            ? binaryPayloadSize
            : (binaryPayload != null ? binaryPayload.bytes.Length : 0);
        public NanitePageBinaryStats BinaryStats => binaryStats;
        public bool HasBinaryPayload => binaryPayload != null;

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
            int payloadSize = -1)
        {
            binaryPayload = payload;
            binaryPayloadOffset = Mathf.Max(0, payloadOffset);
            binaryPayloadSize = payloadSize > 0
                ? payloadSize
                : (payload != null ? payload.bytes.Length : 0);
            binaryStats = stats;
            ResetDecodeState();
        }

        public bool TryGetStoragePayload(out byte[] storagePayload, out string error)
        {
            storagePayload = null;
            if (binaryPayload == null)
            {
                error = "Page has no binary payload.";
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
            if (binaryPayload == null)
            {
                error = "Page has no binary payload.";
                return false;
            }
            byte[] bulk = binaryPayload.bytes;
            if (!TryResolveStorageRange(bulk, out int offset, out int size, out error) ||
                !NanitePageStorageCodec.TryUnpack(bulk, offset, size, out byte[] packedPage, out error))
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
    }
}
