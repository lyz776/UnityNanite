using System;
using System.Collections.Generic;
using Nanite;
using UnityEngine;
using UnityEngine.Rendering;

namespace RealtimeGI
{
    /// <summary>
    /// GI-owned compact triangle stream. It deliberately does not retain Nanite renderer buffers,
    /// whose ownership and lifetime are changing independently. Nanite uses sourceMesh first and
    /// root Page compatibility data only as a fallback.
    /// </summary>
    public sealed class GIGeometryStreamCache : IDisposable
    {
        readonly List<Vector3> vertices = new List<Vector3>(65536);
        readonly List<Vector2> uvs = new List<Vector2>(65536);
        readonly List<uint> indices = new List<uint>(131072);
        readonly List<uint> triangleSubMeshes = new List<uint>(65536);
        readonly List<GIGpuGeometryStreamData> ranges = new List<GIGpuGeometryStreamData>(256);

        GraphicsBuffer vertexBuffer;
        GraphicsBuffer uvBuffer;
        GraphicsBuffer indexBuffer;
        GraphicsBuffer triangleSubMeshBuffer;
        GraphicsBuffer rangeBuffer;
        int vertexCapacity;
        int uvCapacity;
        int indexCapacity;
        int triangleCapacity;
        int rangeCapacity;
        int signature;

        public GraphicsBuffer VertexBuffer => vertexBuffer;
        public GraphicsBuffer UvBuffer => uvBuffer;
        public GraphicsBuffer IndexBuffer => indexBuffer;
        public GraphicsBuffer TriangleSubMeshBuffer => triangleSubMeshBuffer;
        public GraphicsBuffer RangeBuffer => rangeBuffer;
        public IReadOnlyList<GIGpuGeometryStreamData> Ranges => ranges;
        public int InvalidGeometryCount { get; private set; }
        public int VertexCount => vertices.Count;
        public int TriangleCount => triangleSubMeshes.Count;

        public bool RebuildIfNeeded(RealtimeGIScene scene, HashSet<uint> activeGeometryIndices)
        {
            if (scene == null)
                return false;
            int nextSignature = ComputeSignature(scene, activeGeometryIndices);
            if (nextSignature == signature && rangeBuffer != null)
                return false;

            vertices.Clear();
            uvs.Clear();
            indices.Clear();
            triangleSubMeshes.Clear();
            ranges.Clear();
            InvalidGeometryCount = 0;

            IReadOnlyList<GIGpuGeometryData> geometryData = scene.CpuGeometries;
            for (int i = 0; i < geometryData.Count; i++)
            {
                if (activeGeometryIndices != null && !activeGeometryIndices.Contains((uint)i))
                {
                    ranges.Add(default);
                    continue;
                }
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

            Upload();
            signature = nextSignature;
            return true;
        }

        bool AppendNaniteMesh(NaniteMesh mesh)
        {
            if (mesh == null)
                return false;
            int vertexStart = vertices.Count;
            int uvStart = uvs.Count;
            int indexStart = indices.Count;
            int triangleStart = triangleSubMeshes.Count;
            bool appendedAnyPage = false;
            bool hasRootMetadata = mesh.pageStreamingInfo != null && mesh.pageStreamingInfo.Length > 0;

            // Root Pages are the stable coarse Nanite representation and avoid copying the full
            // source mesh. Assets without valid root metadata use sourceMesh as a compatibility path.
            if (hasRootMetadata && mesh.pageArray != null)
            {
                for (int pageIndex = 0; pageIndex < mesh.pageArray.Length; pageIndex++)
                {
                    if (pageIndex >= mesh.pageStreamingInfo.Length ||
                        !mesh.pageStreamingInfo[pageIndex].IsRootPage)
                        continue;
                    NaniteMeshPage page = mesh.pageArray[pageIndex];
                    if (page == null || !AppendNanitePage(page))
                        continue;
                    appendedAnyPage = true;
                }
            }

            if (!appendedAnyPage)
            {
                Rollback(vertexStart, uvStart, indexStart, triangleStart);
                return mesh.sourceMesh != null && AppendMesh(mesh.sourceMesh);
            }

            ranges.Add(new GIGpuGeometryStreamData
            {
                vertexOffset = (uint)vertexStart,
                indexOffset = (uint)indexStart,
                triangleOffset = (uint)triangleStart,
                indexCount = (uint)(indices.Count - indexStart),
                triangleCount = (uint)(triangleSubMeshes.Count - triangleStart),
                flags = 1u
            });
            return true;
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

        bool AppendNanitePage(NaniteMeshPage page)
        {
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
                if (page.clusterArray != null)
                {
                    for (int clusterIndex = 0; clusterIndex < page.clusterArray.Length; clusterIndex++)
                    {
                        NaniteCluster cluster = page.clusterArray[clusterIndex];
                        int first = Mathf.Max(0, cluster.indiceIndex / 3);
                        int last = Mathf.Min(triangleCount, (cluster.indiceIndex + cluster.indiceCount + 2) / 3);
                        uint subMesh = (uint)Mathf.Max(0, cluster.subMeshId);
                        for (int triangle = first; triangle < last; triangle++)
                            subMeshByTriangle[triangle] = subMesh;
                    }
                }

                for (int triangle = 0; triangle < triangleCount; triangle++)
                {
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
                return true;
            }
            catch (Exception exception)
            {
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

        void Upload()
        {
            EnsureBuffer(ref vertexBuffer, ref vertexCapacity, vertices.Count, 12, "GI Stream Vertices");
            EnsureBuffer(ref uvBuffer, ref uvCapacity, uvs.Count, 8, "GI Stream UV0");
            EnsureBuffer(ref indexBuffer, ref indexCapacity, indices.Count, 4, "GI Stream Indices");
            EnsureBuffer(ref triangleSubMeshBuffer, ref triangleCapacity, triangleSubMeshes.Count, 4, "GI Triangle SubMeshes");
            EnsureBuffer(ref rangeBuffer, ref rangeCapacity, ranges.Count, GISceneAbi.GeometryStreamStride, "GI Geometry Ranges");
            if (vertices.Count > 0) vertexBuffer.SetData(vertices, 0, 0, vertices.Count);
            if (uvs.Count > 0) uvBuffer.SetData(uvs, 0, 0, uvs.Count);
            if (indices.Count > 0) indexBuffer.SetData(indices, 0, 0, indices.Count);
            if (triangleSubMeshes.Count > 0) triangleSubMeshBuffer.SetData(triangleSubMeshes, 0, 0, triangleSubMeshes.Count);
            if (ranges.Count > 0) rangeBuffer.SetData(ranges, 0, 0, ranges.Count);
        }

        static int ComputeSignature(RealtimeGIScene scene, HashSet<uint> activeGeometryIndices)
        {
            unchecked
            {
                int hash = 17;
                IReadOnlyList<GIGpuGeometryData> data = scene.CpuGeometries;
                hash = hash * 31 + data.Count;
                for (int i = 0; i < data.Count; i++)
                {
                    bool active = activeGeometryIndices == null || activeGeometryIndices.Contains((uint)i);
                    hash = hash * 31 + (active ? 1 : 0);
                    if (!active)
                        continue;
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
            vertexBuffer = null;
            uvBuffer = null;
            indexBuffer = null;
            triangleSubMeshBuffer = null;
            rangeBuffer = null;
            vertexCapacity = uvCapacity = indexCapacity = triangleCapacity = rangeCapacity = 0;
            signature = 0;
        }
    }
}
