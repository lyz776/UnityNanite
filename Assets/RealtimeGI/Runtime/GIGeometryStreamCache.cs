using System;
using System.Collections.Generic;
using Nanite;
using UnityEngine;
using UnityEngine.Rendering;

namespace RealtimeGI
{
    /// <summary>
    /// GI-owned compact triangle stream. It deliberately does not retain Nanite renderer buffers,
    /// whose ownership and lifetime are changing independently. Nanite geometry is reconstructed
    /// from one complete hierarchy mip chosen for the GI voxel scale, rather than the ultra-coarse
    /// root set or the full source mesh.
    /// </summary>
    public sealed class GIGeometryStreamCache : IDisposable
    {
        // This stream is shared by every instance. Keeping the proxy bounded avoids multiplying a
        // full source mesh by the instance count, while still retaining orders of magnitude more
        // surface detail than a root Page (the dragon root is only 63 triangles).
        const int MaxNaniteProxyTriangles = 16384;
        const int GeometrySelectionVersion = 5;
        readonly List<Vector3> vertices = new List<Vector3>(65536);
        readonly List<Vector2> uvs = new List<Vector2>(65536);
        readonly List<uint> indices = new List<uint>(131072);
        readonly List<uint> triangleSubMeshes = new List<uint>(65536);
        readonly List<GIGpuGeometryStreamData> ranges = new List<GIGpuGeometryStreamData>(256);
        readonly List<GIGpuBvhNode> bvhNodes = new List<GIGpuBvhNode>(4096);
        readonly List<GIGpuBvhPrimitive> bvhPrimitives = new List<GIGpuBvhPrimitive>(4096);
        readonly List<GIGpuBvhRange> bvhRanges = new List<GIGpuBvhRange>(256);
        readonly List<GIGpuBvhNode> tlasNodes = new List<GIGpuBvhNode>(1024);
        readonly List<GIGpuBvhPrimitive> tlasPrimitives = new List<GIGpuBvhPrimitive>(1024);
        readonly List<GIGpuBvhNode> combinedBvhNodes = new List<GIGpuBvhNode>(5120);
        readonly List<GIGpuBvhPrimitive> combinedBvhPrimitives =
            new List<GIGpuBvhPrimitive>(5120);

        GraphicsBuffer vertexBuffer;
        GraphicsBuffer uvBuffer;
        GraphicsBuffer indexBuffer;
        GraphicsBuffer triangleSubMeshBuffer;
        GraphicsBuffer rangeBuffer;
        GraphicsBuffer bvhNodeBuffer;
        GraphicsBuffer bvhPrimitiveBuffer;
        GraphicsBuffer bvhRangeBuffer;
        int vertexCapacity;
        int uvCapacity;
        int indexCapacity;
        int triangleCapacity;
        int rangeCapacity;
        int bvhNodeCapacity;
        int bvhPrimitiveCapacity;
        int bvhRangeCapacity;
        int tlasNodeOffset;
        int tlasPrimitiveOffset;
        int signature;
        int tlasSignature;

        public GraphicsBuffer VertexBuffer => vertexBuffer;
        public GraphicsBuffer UvBuffer => uvBuffer;
        public GraphicsBuffer IndexBuffer => indexBuffer;
        public GraphicsBuffer TriangleSubMeshBuffer => triangleSubMeshBuffer;
        public GraphicsBuffer RangeBuffer => rangeBuffer;
        public GraphicsBuffer BvhNodeBuffer => bvhNodeBuffer;
        public GraphicsBuffer BvhPrimitiveBuffer => bvhPrimitiveBuffer;
        public GraphicsBuffer BvhRangeBuffer => bvhRangeBuffer;
        public IReadOnlyList<GIGpuGeometryStreamData> Ranges => ranges;
        public int InvalidGeometryCount { get; private set; }
        public int VertexCount => vertices.Count;
        public int TriangleCount => triangleSubMeshes.Count;
        public int TlasNodeCount => tlasNodes.Count;
        public int TlasNodeOffset => tlasNodeOffset;
        public long ResidentBytes =>
            vertices.Count * 12L + uvs.Count * 8L + indices.Count * 4L +
            triangleSubMeshes.Count * 4L + ranges.Count * GISceneAbi.GeometryStreamStride +
            bvhNodes.Count * GISceneAbi.BvhNodeStride +
            bvhPrimitives.Count * GISceneAbi.BvhPrimitiveStride +
            bvhRanges.Count * GISceneAbi.BvhRangeStride +
            tlasNodes.Count * GISceneAbi.BvhNodeStride +
            tlasPrimitives.Count * GISceneAbi.BvhPrimitiveStride;

        public bool RebuildIfNeeded(RealtimeGIScene scene)
        {
            if (scene == null)
                return false;
            int nextSignature = ComputeSignature(scene);
            if (nextSignature == signature && rangeBuffer != null)
            {
                bool tlasChanged = UpdateTlasIfNeeded(scene);
                if (tlasChanged)
                    UploadTlas();
                return tlasChanged;
            }

            vertices.Clear();
            uvs.Clear();
            indices.Clear();
            triangleSubMeshes.Clear();
            ranges.Clear();
            bvhNodes.Clear();
            bvhPrimitives.Clear();
            bvhRanges.Clear();
            InvalidGeometryCount = 0;

            IReadOnlyList<GIGpuGeometryData> geometryData = scene.CpuGeometries;
            for (int i = 0; i < geometryData.Count; i++)
            {
                UnityEngine.Object source = scene.GetGeometrySource(i);
                bool built = false;
                if (source is Mesh mesh)
                    built = AppendMesh(mesh);
                else if (source is NaniteMesh naniteMesh)
                    built = AppendNaniteMesh(naniteMesh);

                if (!built)
                {
                    InvalidGeometryCount++;
                    ranges.Add(default);
                }
            }

            BuildBlas(scene);
            BuildTlas(scene);
            Upload();
            signature = nextSignature;
            tlasSignature = ComputeTlasSignature(scene);
            return true;
        }

        bool AppendNaniteMesh(NaniteMesh mesh)
        {
            if (mesh == null)
                return false;
            NaniteRendererFeature naniteFeature = NaniteRendererFeature.ActiveInstance;
            NaniteResidentPageReadOnlyView liveView = default;
            int firstPageId = 0;
            int livePageCount = 0;
            int liveMeshIndex = 0;
            bool hasLivePages = naniteFeature != null &&
                naniteFeature.TryGetResidentPageReadOnlyView(out liveView) &&
                liveView.IsValid &&
                naniteFeature.TryGetResidentMeshPageRange(
                    mesh, out firstPageId, out livePageCount, out liveMeshIndex);
            int vertexStart = vertices.Count;
            int uvStart = uvs.Count;
            int indexStart = indices.Count;
            int triangleStart = triangleSubMeshes.Count;
            bool hasRootMetadata = mesh.pageStreamingInfo != null && mesh.pageStreamingInfo.Length > 0;

            // A single hierarchy mip is a complete, non-overlapping representation. Selecting only
            // root Pages created metre-scale triangles which then became the visible GI colour blocks.
            if (hasRootMetadata && mesh.pageArray != null)
            {
                int sourceTriangles = Mathf.Max(1, mesh.sourceTriangleCount);
                int estimatedMip = Mathf.Clamp(
                    Mathf.CeilToInt(Mathf.Log(
                        Mathf.Max(1f, sourceTriangles / (float)MaxNaniteProxyTriangles), 2f)),
                    0, Mathf.Max(0, mesh.maxMipLevel));
                for (int targetMip = estimatedMip; targetMip <= mesh.maxMipLevel; targetMip++)
                {
                    Rollback(vertexStart, uvStart, indexStart, triangleStart);
                    if (!AppendNaniteMip(mesh, targetMip))
                        continue;
                    int proxyTriangles = triangleSubMeshes.Count - triangleStart;
                    if (proxyTriangles <= 0 || proxyTriangles > MaxNaniteProxyTriangles)
                        continue;

                    ranges.Add(new GIGpuGeometryStreamData
                    {
                        vertexOffset = (uint)vertexStart,
                        indexOffset = (uint)indexStart,
                        triangleOffset = (uint)triangleStart,
                        indexCount = (uint)(indices.Count - indexStart),
                        triangleCount = (uint)proxyTriangles,
                        // The compact complete mip drives Clipmap voxelization. The live-page
                        // bit and mesh index remain attached so the BLAS can still reference
                        // authoritative resident triangles for strict/specular refinement.
                        flags = GISceneAbi.GeometryStreamNaniteProxy |
                                (hasLivePages ? GISceneAbi.GeometryStreamNaniteResidentPages : 0u) |
                                ((uint)targetMip << 8),
                        padding0 = hasLivePages ? (uint)liveMeshIndex : 0u,
                        padding1 = hasLivePages ? liveView.poolGeneration : 0u
                    });
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                    Debug.Log(
                        $"[RealtimeGI] Nanite GI proxy '{mesh.name}': mip={targetMip}, " +
                        $"triangles={proxyTriangles} (source={sourceTriangles}, budget={MaxNaniteProxyTriangles}).");
#endif
                    return true;
                }
            }

            // Assets without a readable hierarchy can still consume the live export. This
            // compatibility path is intentionally not preferred for voxelization because a
            // moving Clipmap would otherwise replay the full resident mesh for every instance.
            if (hasLivePages)
            {
                Rollback(vertexStart, uvStart, indexStart, triangleStart);
                ranges.Add(new GIGpuGeometryStreamData
                {
                    vertexOffset = (uint)firstPageId,
                    indexOffset = (uint)livePageCount,
                    indexCount = (uint)Math.Min(uint.MaxValue,
                        Math.Max(0L, (long)mesh.sourceTriangleCount * 3L)),
                    triangleCount = (uint)Mathf.Max(0, mesh.sourceTriangleCount),
                    flags = GISceneAbi.GeometryStreamNaniteResidentPages,
                    padding0 = (uint)liveMeshIndex,
                    padding1 = liveView.poolGeneration
                });
                return true;
            }

            // Compatibility fallback: a root representation is preferable to losing the object,
            // but is no longer the normal path. Assets without readable Pages fall back to sourceMesh.
            Rollback(vertexStart, uvStart, indexStart, triangleStart);
            bool appendedRoot = false;
            if (hasRootMetadata && mesh.pageArray != null)
            {
                for (int pageIndex = 0; pageIndex < mesh.pageArray.Length; pageIndex++)
                {
                    if (pageIndex >= mesh.pageStreamingInfo.Length ||
                        !mesh.pageStreamingInfo[pageIndex].IsRootPage)
                        continue;
                    NaniteMeshPage page = mesh.pageArray[pageIndex];
                    appendedRoot |= page != null && AppendNanitePage(page, -1);
                }
            }
            if (appendedRoot && triangleSubMeshes.Count > triangleStart)
            {
                ranges.Add(new GIGpuGeometryStreamData
                {
                    vertexOffset = (uint)vertexStart,
                    indexOffset = (uint)indexStart,
                    triangleOffset = (uint)triangleStart,
                    indexCount = (uint)(indices.Count - indexStart),
                    triangleCount = (uint)(triangleSubMeshes.Count - triangleStart),
                    flags = GISceneAbi.GeometryStreamNaniteProxy | 0x80000000u
                });
                Debug.LogWarning(
                    $"[RealtimeGI] Nanite GI proxy '{mesh.name}' fell back to root Pages " +
                    $"({triangleSubMeshes.Count - triangleStart} triangles). Re-bake readable hierarchy Pages " +
                    "to avoid coarse world-space lighting partitions.");
                return true;
            }
            Rollback(vertexStart, uvStart, indexStart, triangleStart);
            return mesh.sourceMesh != null && AppendMesh(mesh.sourceMesh);
        }

        bool AppendNaniteMip(NaniteMesh mesh, int targetMip)
        {
            bool appended = false;
            int pageCount = Mathf.Min(mesh.pageArray.Length, mesh.pageStreamingInfo.Length);
            for (int pageIndex = 0; pageIndex < pageCount; pageIndex++)
            {
                NanitePageStreamingInfo info = mesh.pageStreamingInfo[pageIndex];
                if (targetMip < info.minMip || targetMip > info.maxMip)
                    continue;
                NaniteMeshPage page = mesh.pageArray[pageIndex];
                appended |= page != null && AppendNanitePage(page, targetMip);
            }
            return appended;
        }

        void Rollback(int vertexStart, int uvStart, int indexStart, int triangleStart)
        {
            if (vertices.Count > vertexStart)
                vertices.RemoveRange(vertexStart, vertices.Count - vertexStart);
            if (uvs.Count > uvStart)
                uvs.RemoveRange(uvStart, uvs.Count - uvStart);
            if (indices.Count > indexStart)
                indices.RemoveRange(indexStart, indices.Count - indexStart);
            if (triangleSubMeshes.Count > triangleStart)
                triangleSubMeshes.RemoveRange(triangleStart, triangleSubMeshes.Count - triangleStart);
        }

        bool AppendNanitePage(NaniteMeshPage page, int targetMip)
        {
            int pageVertexStart = vertices.Count;
            int pageUvStart = uvs.Count;
            int pageIndexStart = indices.Count;
            int pageTriangleStart = triangleSubMeshes.Count;
            try
            {
                float[] sourceVertices = page.vertexData;
                int[] sourceIndices = page.indiceArray;
                int stride = page.vertexStride;
                if (sourceVertices == null || sourceIndices == null || stride < 3 ||
                    page.vertexCount <= 0 || sourceVertices.Length < page.vertexCount * stride)
                    return false;

                uint vertexBase = (uint)vertices.Count;
                for (int i = 0; i < page.vertexCount; i++)
                {
                    int offset = i * stride;
                    vertices.Add(new Vector3(
                        sourceVertices[offset], sourceVertices[offset + 1], sourceVertices[offset + 2]));
                    uvs.Add(stride >= 5
                        ? new Vector2(sourceVertices[offset + 3], sourceVertices[offset + 4])
                        : Vector2.zero);
                }

                int triangleCount = sourceIndices.Length / 3;
                var subMeshByTriangle = new uint[triangleCount];
                var includeTriangle = new bool[triangleCount];
                if (page.clusterArray != null)
                {
                    for (int clusterIndex = 0; clusterIndex < page.clusterArray.Length; clusterIndex++)
                    {
                        NaniteCluster cluster = page.clusterArray[clusterIndex];
                        int first = Mathf.Max(0, cluster.indiceIndex / 3);
                        int last = Mathf.Min(triangleCount, (cluster.indiceIndex + cluster.indiceCount + 2) / 3);
                        uint subMesh = (uint)Mathf.Max(0, cluster.subMeshId);
                        int clusterMip = page.clusterMip != null && clusterIndex < page.clusterMip.Length
                            ? page.clusterMip[clusterIndex]
                            : (page.parts != null && cluster.partIndex >= 0 && cluster.partIndex < page.parts.Length
                                ? page.parts[cluster.partIndex].mipLevel
                                : -1);
                        bool include = targetMip < 0 || clusterMip == targetMip;
                        for (int triangle = first; triangle < last; triangle++)
                        {
                            subMeshByTriangle[triangle] = subMesh;
                            includeTriangle[triangle] = include;
                        }
                    }
                }
                else if (targetMip < 0)
                {
                    Array.Fill(includeTriangle, true);
                }

                for (int triangle = 0; triangle < triangleCount; triangle++)
                {
                    if (!includeTriangle[triangle])
                        continue;
                    int indexOffset = triangle * 3;
                    int a = sourceIndices[indexOffset];
                    int b = sourceIndices[indexOffset + 1];
                    int c = sourceIndices[indexOffset + 2];
                    if ((uint)a >= (uint)page.vertexCount || (uint)b >= (uint)page.vertexCount ||
                        (uint)c >= (uint)page.vertexCount)
                        continue;
                    indices.Add(vertexBase + (uint)a);
                    indices.Add(vertexBase + (uint)b);
                    indices.Add(vertexBase + (uint)c);
                    triangleSubMeshes.Add(subMeshByTriangle[triangle]);
                }
                if (triangleSubMeshes.Count == pageTriangleStart)
                {
                    Rollback(pageVertexStart, pageUvStart, pageIndexStart, pageTriangleStart);
                    return false;
                }
                return true;
            }
            catch (Exception exception)
            {
                Rollback(pageVertexStart, pageUvStart, pageIndexStart, pageTriangleStart);
                Debug.LogWarning($"[RealtimeGI] Failed to decode Nanite Page '{page.name}': {exception.Message}");
                return false;
            }
        }

        bool AppendMesh(Mesh mesh)
        {
            if (mesh == null)
                return false;
            int vertexStart = vertices.Count;
            int uvStart = uvs.Count;
            int indexStart = indices.Count;
            int triangleStart = triangleSubMeshes.Count;

            try
            {
                Vector3[] sourceVertices = mesh.vertices;
                if (sourceVertices == null || sourceVertices.Length == 0)
                    return false;
                vertices.AddRange(sourceVertices);
                Vector2[] sourceUvs = mesh.uv;
                bool hasUvs = sourceUvs != null && sourceUvs.Length == sourceVertices.Length;
                for (int vertex = 0; vertex < sourceVertices.Length; vertex++)
                    uvs.Add(hasUvs ? sourceUvs[vertex] : Vector2.zero);
                uint vertexBase = (uint)vertexStart;

                for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
                {
                    if (mesh.GetTopology(subMesh) != MeshTopology.Triangles)
                        continue;
                    int[] sourceIndices = mesh.GetIndices(subMesh, true);
                    int triangleCount = sourceIndices.Length / 3;
                    for (int triangle = 0; triangle < triangleCount; triangle++)
                    {
                        int indexOffset = triangle * 3;
                        int a = sourceIndices[indexOffset];
                        int b = sourceIndices[indexOffset + 1];
                        int c = sourceIndices[indexOffset + 2];
                        if ((uint)a >= (uint)sourceVertices.Length || (uint)b >= (uint)sourceVertices.Length ||
                            (uint)c >= (uint)sourceVertices.Length)
                            continue;
                        indices.Add(vertexBase + (uint)a);
                        indices.Add(vertexBase + (uint)b);
                        indices.Add(vertexBase + (uint)c);
                        triangleSubMeshes.Add((uint)subMesh);
                    }
                }

                if (triangleSubMeshes.Count == triangleStart)
                {
                    vertices.RemoveRange(vertexStart, vertices.Count - vertexStart);
                    uvs.RemoveRange(uvStart, uvs.Count - uvStart);
                    return false;
                }

                ranges.Add(new GIGpuGeometryStreamData
                {
                    vertexOffset = (uint)vertexStart,
                    indexOffset = (uint)indexStart,
                    triangleOffset = (uint)triangleStart,
                    indexCount = (uint)(indices.Count - indexStart),
                    triangleCount = (uint)(triangleSubMeshes.Count - triangleStart)
                });
                return true;
            }
            catch (Exception exception)
            {
                Rollback(vertexStart, uvStart, indexStart, triangleStart);
                Debug.LogWarning(
                    $"[RealtimeGI] Mesh '{mesh.name}' is unavailable for GI triangle streaming. " +
                    $"Enable Read/Write or provide a Nanite/root Page proxy. {exception.Message}");
                return false;
            }
        }

        struct PrimitiveBuild
        {
            public Vector3 min;
            public Vector3 max;
            public Vector3 centroid;
            public GIGpuBvhPrimitive primitive;
        }

        sealed class PrimitiveAxisComparer : IComparer<PrimitiveBuild>
        {
            public int axis;
            public int Compare(PrimitiveBuild a, PrimitiveBuild b) =>
                Axis(a.centroid, axis).CompareTo(Axis(b.centroid, axis));
        }

        static float Axis(Vector3 value, int axis) =>
            axis == 0 ? value.x : (axis == 1 ? value.y : value.z);

        void BuildBlas(RealtimeGIScene scene)
        {
            bvhNodes.Clear();
            bvhPrimitives.Clear();
            bvhRanges.Clear();
            var primitives = new List<PrimitiveBuild>(4096);
            var comparer = new PrimitiveAxisComparer();
            for (int geometryIndex = 0; geometryIndex < ranges.Count; geometryIndex++)
            {
                primitives.Clear();
                GIGpuGeometryStreamData range = ranges[geometryIndex];
                UnityEngine.Object source = scene.GetGeometrySource(geometryIndex);
                if ((range.flags & GISceneAbi.GeometryStreamNaniteResidentPages) != 0u &&
                    source is NaniteMesh naniteMesh)
                    GatherNanitePagePrimitives(naniteMesh, primitives);
                else
                    GatherTrianglePrimitives(range, primitives);

                if (primitives.Count == 0)
                {
                    bvhRanges.Add(default);
                    continue;
                }
                int root = bvhNodes.Count;
                bvhNodes.Add(default);
                int primitiveOffset = bvhPrimitives.Count;
                BuildBvhNodeAt(root, primitives, 0, primitives.Count,
                    bvhNodes, bvhPrimitives, comparer);
                bvhRanges.Add(new GIGpuBvhRange
                {
                    rootNode = (uint)root,
                    nodeCount = (uint)(bvhNodes.Count - root),
                    primitiveOffset = (uint)primitiveOffset,
                    primitiveCount = (uint)(bvhPrimitives.Count - primitiveOffset)
                });
            }
        }

        void GatherTrianglePrimitives(
            GIGpuGeometryStreamData range, List<PrimitiveBuild> output)
        {
            for (uint triangle = 0; triangle < range.triangleCount; triangle++)
            {
                int first = checked((int)(range.indexOffset + triangle * 3u));
                if (first < 0 || first + 2 >= indices.Count)
                    continue;
                uint i0 = indices[first];
                uint i1 = indices[first + 1];
                uint i2 = indices[first + 2];
                if (i0 >= (uint)vertices.Count || i1 >= (uint)vertices.Count ||
                    i2 >= (uint)vertices.Count)
                    continue;
                Vector3 p0 = vertices[(int)i0];
                Vector3 p1 = vertices[(int)i1];
                Vector3 p2 = vertices[(int)i2];
                Vector3 min = Vector3.Min(p0, Vector3.Min(p1, p2));
                Vector3 max = Vector3.Max(p0, Vector3.Max(p1, p2));
                output.Add(new PrimitiveBuild
                {
                    min = min,
                    max = max,
                    centroid = (p0 + p1 + p2) / 3f,
                    primitive = new GIGpuBvhPrimitive
                    {
                        primitiveKey = triangle,
                        firstTriangle = triangle,
                        triangleCount = 1u,
                        flags = 0u
                    }
                });
            }
        }

        static void EncapsulateSphere(ref Vector3 min, ref Vector3 max, Vector4 sphere)
        {
            if (sphere.w <= 0f)
                return;
            Vector3 center = new Vector3(sphere.x, sphere.y, sphere.z);
            Vector3 radius = Vector3.one * sphere.w;
            min = Vector3.Min(min, center - radius);
            max = Vector3.Max(max, center + radius);
        }

        void GatherNanitePagePrimitives(
            NaniteMesh mesh, List<PrimitiveBuild> output)
        {
            NaniteRendererFeature feature = NaniteRendererFeature.ActiveInstance;
            if (feature == null || mesh?.pageArray == null)
                return;
            for (int localPage = 0; localPage < mesh.pageArray.Length; localPage++)
            {
                NaniteMeshPage page = mesh.pageArray[localPage];
                if (page == null ||
                    !feature.TryGetResidentPageId(mesh, localPage, out int pageId))
                    continue;
                bool appendedCluster = false;
                if (page.clusterArray != null)
                {
                    for (int clusterIndex = 0; clusterIndex < page.clusterArray.Length; clusterIndex++)
                    {
                        NaniteCluster cluster = page.clusterArray[clusterIndex];
                        Vector4 sphere = cluster.geometrySphere.w > 0f
                            ? cluster.geometrySphere : cluster.selfSphere;
                        if (sphere.w <= 0f || cluster.indiceCount < 3)
                            continue;
                        Vector3 center = new Vector3(sphere.x, sphere.y, sphere.z);
                        Vector3 radius = Vector3.one * sphere.w;
                        output.Add(new PrimitiveBuild
                        {
                            min = center - radius,
                            max = center + radius,
                            centroid = center,
                            primitive = new GIGpuBvhPrimitive
                            {
                                primitiveKey = (uint)pageId,
                                firstTriangle = (uint)Mathf.Max(0, cluster.indiceIndex / 3),
                                triangleCount = (uint)Mathf.Max(0, cluster.indiceCount / 3),
                                flags = 1u
                            }
                        });
                        appendedCluster = true;
                    }
                }
                if (appendedCluster)
                    continue;
                Vector3 min = new Vector3(float.PositiveInfinity, float.PositiveInfinity,
                                          float.PositiveInfinity);
                Vector3 max = new Vector3(float.NegativeInfinity, float.NegativeInfinity,
                                          float.NegativeInfinity);
                if (page.clusterArray != null)
                {
                    for (int clusterIndex = 0; clusterIndex < page.clusterArray.Length; clusterIndex++)
                    {
                        NaniteCluster cluster = page.clusterArray[clusterIndex];
                        Vector4 sphere = cluster.geometrySphere.w > 0f
                            ? cluster.geometrySphere : cluster.selfSphere;
                        EncapsulateSphere(ref min, ref max, sphere);
                    }
                }
                if (!IsFinite(min.x) && page.vertexData != null && page.vertexStride >= 3)
                {
                    for (int vertex = 0; vertex < page.vertexCount; vertex++)
                    {
                        int offset = vertex * page.vertexStride;
                        Vector3 position = new Vector3(
                            page.vertexData[offset], page.vertexData[offset + 1],
                            page.vertexData[offset + 2]);
                        min = Vector3.Min(min, position);
                        max = Vector3.Max(max, position);
                    }
                }
                if (!IsFinite(min.x) || !IsFinite(max.x))
                    continue;
                int triangleCount = page.indiceArray != null ? page.indiceArray.Length / 3 : 0;
                output.Add(new PrimitiveBuild
                {
                    min = min,
                    max = max,
                    centroid = (min + max) * 0.5f,
                    primitive = new GIGpuBvhPrimitive
                    {
                        primitiveKey = (uint)pageId,
                        firstTriangle = 0u,
                        triangleCount = (uint)Mathf.Max(0, triangleCount),
                        flags = 1u
                    }
                });
            }
        }

        static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        static void BuildBvhNodeAt(
            int nodeIndex,
            List<PrimitiveBuild> source,
            int start,
            int count,
            List<GIGpuBvhNode> nodes,
            List<GIGpuBvhPrimitive> primitiveOutput,
            PrimitiveAxisComparer comparer)
        {
            Vector3 boundsMin = new Vector3(float.PositiveInfinity, float.PositiveInfinity,
                                            float.PositiveInfinity);
            Vector3 boundsMax = new Vector3(float.NegativeInfinity, float.NegativeInfinity,
                                            float.NegativeInfinity);
            Vector3 centroidMin = boundsMin;
            Vector3 centroidMax = boundsMax;
            for (int i = start; i < start + count; i++)
            {
                boundsMin = Vector3.Min(boundsMin, source[i].min);
                boundsMax = Vector3.Max(boundsMax, source[i].max);
                centroidMin = Vector3.Min(centroidMin, source[i].centroid);
                centroidMax = Vector3.Max(centroidMax, source[i].centroid);
            }
            if (count <= 4)
            {
                int first = primitiveOutput.Count;
                for (int i = start; i < start + count; i++)
                    primitiveOutput.Add(source[i].primitive);
                nodes[nodeIndex] = new GIGpuBvhNode
                {
                    boundsMin = boundsMin,
                    boundsMax = boundsMax,
                    leftFirst = (uint)first,
                    primitiveCount = (uint)count
                };
                return;
            }
            Vector3 extent = centroidMax - centroidMin;
            comparer.axis = extent.x >= extent.y && extent.x >= extent.z
                ? 0 : (extent.y >= extent.z ? 1 : 2);
            source.Sort(start, count, comparer);
            int leftCount = count / 2;
            int leftChild = nodes.Count;
            nodes.Add(default);
            nodes.Add(default);
            nodes[nodeIndex] = new GIGpuBvhNode
            {
                boundsMin = boundsMin,
                boundsMax = boundsMax,
                leftFirst = (uint)leftChild,
                primitiveCount = 0u
            };
            BuildBvhNodeAt(leftChild, source, start, leftCount,
                nodes, primitiveOutput, comparer);
            BuildBvhNodeAt(leftChild + 1, source, start + leftCount, count - leftCount,
                nodes, primitiveOutput, comparer);
        }

        bool UpdateTlasIfNeeded(RealtimeGIScene scene)
        {
            int next = ComputeTlasSignature(scene);
            if (next == tlasSignature && bvhNodeBuffer != null)
                return false;
            BuildTlas(scene);
            tlasSignature = next;
            return true;
        }

        void BuildTlas(RealtimeGIScene scene)
        {
            tlasNodes.Clear();
            tlasPrimitives.Clear();
            var leaves = new List<PrimitiveBuild>(scene.CpuInstances.Count);
            IReadOnlyList<GIGpuInstanceData> instances = scene.CpuInstances;
            for (int instanceIndex = 0; instanceIndex < instances.Count; instanceIndex++)
            {
                GIGpuInstanceData instance = instances[instanceIndex];
                GIInstanceFlags flags = (GIInstanceFlags)instance.flags;
                if ((flags & (GIInstanceFlags.Occluder | GIInstanceFlags.Contributor)) == 0 ||
                    instance.geometryIndex >= (uint)bvhRanges.Count ||
                    bvhRanges[(int)instance.geometryIndex].primitiveCount == 0u)
                    continue;
                Vector3 center = new Vector3(
                    instance.worldBoundingSphere.x, instance.worldBoundingSphere.y,
                    instance.worldBoundingSphere.z);
                Vector3 radius = Vector3.one * Mathf.Max(0.001f, instance.worldBoundingSphere.w);
                leaves.Add(new PrimitiveBuild
                {
                    min = center - radius,
                    max = center + radius,
                    centroid = center,
                    primitive = new GIGpuBvhPrimitive
                    {
                        primitiveKey = (uint)instanceIndex,
                        firstTriangle = 0u,
                        triangleCount = 0u,
                        flags = 2u
                    }
                });
            }
            if (leaves.Count == 0)
                return;
            tlasNodes.Add(default);
            BuildBvhNodeAt(0, leaves, 0, leaves.Count,
                tlasNodes, tlasPrimitives, new PrimitiveAxisComparer());
        }

        static int ComputeTlasSignature(RealtimeGIScene scene)
        {
            unchecked
            {
                int hash = 17;
                IReadOnlyList<GIGpuInstanceData> instances = scene.CpuInstances;
                hash = hash * 31 + instances.Count;
                for (int i = 0; i < instances.Count; i++)
                {
                    GIGpuInstanceData instance = instances[i];
                    hash = hash * 31 + (int)instance.objectId;
                    hash = hash * 31 + (int)instance.geometryIndex;
                    hash = hash * 31 + (int)instance.transformSignature;
                    hash = hash * 31 + (int)instance.revision;
                    hash = hash * 31 + instance.worldBoundingSphere.GetHashCode();
                    hash = hash * 31 + (int)instance.flags;
                }
                return hash;
            }
        }

        void Upload()
        {
            EnsureBuffer(ref vertexBuffer, ref vertexCapacity, vertices.Count, 12, "GI Stream Vertices");
            EnsureBuffer(ref uvBuffer, ref uvCapacity, uvs.Count, 8, "GI Stream UV0");
            EnsureBuffer(ref indexBuffer, ref indexCapacity, indices.Count, 4, "GI Stream Indices");
            EnsureBuffer(ref triangleSubMeshBuffer, ref triangleCapacity, triangleSubMeshes.Count, 4, "GI Triangle SubMeshes");
            EnsureBuffer(ref rangeBuffer, ref rangeCapacity, ranges.Count, GISceneAbi.GeometryStreamStride, "GI Geometry Ranges");
            EnsureBuffer(ref bvhRangeBuffer, ref bvhRangeCapacity, bvhRanges.Count,
                GISceneAbi.BvhRangeStride, "GI BLAS Ranges");
            if (vertices.Count > 0) vertexBuffer.SetData(vertices, 0, 0, vertices.Count);
            if (uvs.Count > 0) uvBuffer.SetData(uvs, 0, 0, uvs.Count);
            if (indices.Count > 0) indexBuffer.SetData(indices, 0, 0, indices.Count);
            if (triangleSubMeshes.Count > 0) triangleSubMeshBuffer.SetData(triangleSubMeshes, 0, 0, triangleSubMeshes.Count);
            if (ranges.Count > 0) rangeBuffer.SetData(ranges, 0, 0, ranges.Count);
            if (bvhRanges.Count > 0) bvhRangeBuffer.SetData(bvhRanges, 0, 0, bvhRanges.Count);
            UploadTlas();
        }

        void UploadTlas()
        {
            // D3D11 allows at most 32 buffers per compute kernel. Keep TLAS and BLAS in
            // common node/primitive buffers and publish offsets instead of consuming two
            // additional SRV slots in every strict-visibility kernel.
            combinedBvhNodes.Clear();
            combinedBvhPrimitives.Clear();
            combinedBvhNodes.AddRange(bvhNodes);
            combinedBvhPrimitives.AddRange(bvhPrimitives);
            tlasNodeOffset = combinedBvhNodes.Count;
            tlasPrimitiveOffset = combinedBvhPrimitives.Count;
            for (int i = 0; i < tlasNodes.Count; i++)
            {
                GIGpuBvhNode node = tlasNodes[i];
                node.leftFirst += node.primitiveCount == 0u
                    ? (uint)tlasNodeOffset
                    : (uint)tlasPrimitiveOffset;
                combinedBvhNodes.Add(node);
            }
            combinedBvhPrimitives.AddRange(tlasPrimitives);
            EnsureBuffer(ref bvhNodeBuffer, ref bvhNodeCapacity, combinedBvhNodes.Count,
                GISceneAbi.BvhNodeStride, "GI Unified BVH Nodes");
            EnsureBuffer(ref bvhPrimitiveBuffer, ref bvhPrimitiveCapacity,
                combinedBvhPrimitives.Count, GISceneAbi.BvhPrimitiveStride,
                "GI Unified BVH Primitives");
            if (combinedBvhNodes.Count > 0)
                bvhNodeBuffer.SetData(combinedBvhNodes, 0, 0, combinedBvhNodes.Count);
            if (combinedBvhPrimitives.Count > 0)
                bvhPrimitiveBuffer.SetData(
                    combinedBvhPrimitives, 0, 0, combinedBvhPrimitives.Count);
        }

        static int ComputeSignature(RealtimeGIScene scene)
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + GeometrySelectionVersion;
                NaniteRendererFeature feature = NaniteRendererFeature.ActiveInstance;
                NaniteResidentPageReadOnlyView liveView = default;
                bool hasLivePages = feature != null &&
                    feature.TryGetResidentPageReadOnlyView(out liveView) && liveView.IsValid;
                hash = hash * 31 + (hasLivePages ? 1 : 0);
                if (hasLivePages)
                    hash = hash * 31 + (int)liveView.poolGeneration;
                IReadOnlyList<GIGpuGeometryData> data = scene.CpuGeometries;
                hash = hash * 31 + data.Count;
                for (int i = 0; i < data.Count; i++)
                {
                    hash = hash * 31 + (int)data[i].sourceObjectId;
                    hash = hash * 31 + (int)data[i].sourceRevision;
                    hash = hash * 31 + (int)data[i].kind;
                }
                return hash;
            }
        }

        static void EnsureBuffer(
            ref GraphicsBuffer buffer,
            ref int capacity,
            int count,
            int stride,
            string name)
        {
            int required = Mathf.NextPowerOfTwo(Mathf.Max(1, count));
            if (buffer != null && capacity >= required)
                return;
            buffer?.Release();
            capacity = required;
            buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity, stride) { name = name };
        }

        public void Dispose()
        {
            vertexBuffer?.Release();
            uvBuffer?.Release();
            indexBuffer?.Release();
            triangleSubMeshBuffer?.Release();
            rangeBuffer?.Release();
            bvhNodeBuffer?.Release();
            bvhPrimitiveBuffer?.Release();
            bvhRangeBuffer?.Release();
            vertexBuffer = null;
            uvBuffer = null;
            indexBuffer = null;
            triangleSubMeshBuffer = null;
            rangeBuffer = null;
            bvhNodeBuffer = null;
            bvhPrimitiveBuffer = null;
            bvhRangeBuffer = null;
            vertexCapacity = uvCapacity = indexCapacity = triangleCapacity = rangeCapacity = 0;
            bvhNodeCapacity = bvhPrimitiveCapacity = bvhRangeCapacity = 0;
            tlasNodeOffset = tlasPrimitiveOffset = 0;
            signature = 0;
            tlasSignature = 0;
        }
    }
}
