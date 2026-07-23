#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Nanite.Editor
{
    /// <summary>
    /// 离线资源打包：SubMesh DAG → Part（粗剔除单元）→ Page（分块加载）。
    /// 简化 UE Nanite 的 BVH + Persistent Threads：两层 Part/Cluster 并行剔除。
    /// </summary>
    public static class NaniteAssetBaker
    {
        public const int MaxPartPerPage = 128;
        public const int MaxClusterPerPart = 8;
        static bool sCancelRequested;

        public static bool IsBakeRunning { get; private set; }
        public static bool IsCancelRequested => sCancelRequested;

        sealed class BuildPart
        {
            public List<int> clusterIndices = new List<int>();
            public int mip;
            public int subMeshId;
        }

        public static void RequestCancel()
        {
            if (!IsBakeRunning || sCancelRequested)
                return;

            sCancelRequested = true;
            Debug.LogWarning("[Nanite] 收到取消请求，将在安全检查点停止…");
        }

        public static void Bake(Mesh mesh)
        {
            if (IsBakeRunning)
            {
                Debug.LogWarning("[Nanite] Bake 已在进行中。");
                return;
            }

            if (mesh == null)
            {
                Debug.LogError("[Nanite] Bake 需要有效的 Mesh。");
                return;
            }

            if (!mesh.isReadable)
            {
                Debug.LogError("[Nanite] Mesh 不可读，请在 Import Settings 勾选 Read/Write Enabled。");
                return;
            }

            IsBakeRunning = true;
            sCancelRequested = false;

            var totalTimer = Stopwatch.StartNew();
            long dagMs = 0, partMs = 0, pageMs = 0, saveMs = 0;

            try
            {
                var vertices = mesh.vertices;
                var normals = mesh.normals;
                var uvs = mesh.uv;
                var tangents = mesh.tangents;
                int subMeshCount = mesh.subMeshCount;

                Debug.Log($"[Nanite] Bake 开始: mesh={mesh.name}, vertices={vertices.Length}, subMeshes={subMeshCount}");

                var subMeshList = new List<NaniteSubMesh>(subMeshCount);
                int totalClusterCount = 0;
                int maxMipLevel = 0;

                var stageTimer = Stopwatch.StartNew();
                for (int i = 0; i < subMeshCount; i++)
                {
                    float p = subMeshCount <= 0 ? 0f : (i / (float)subMeshCount) * 0.4f;
                    UpdateProgress($"Build DAG SubMesh {i + 1}/{subMeshCount}", p);

                    var subTimer = Stopwatch.StartNew();
                    var triangles = mesh.GetTriangles(i);
                    var subMesh = NaniteMeshBuilder.Build(vertices, normals, triangles, () => sCancelRequested);
                    subMeshList.Add(subMesh);
                    totalClusterCount += subMesh.clusterList.Count;
                    maxMipLevel = Mathf.Max(maxMipLevel, subMesh.maxMipLevel);

                    Debug.Log(
                        $"[Nanite] SubMesh {i + 1}/{subMeshCount}: tris={triangles.Length / 3}, " +
                        $"clusters={subMesh.clusterList.Count}, groups={subMesh.clusterGroupList.Count}, " +
                        $"maxMip={subMesh.maxMipLevel}, {subTimer.ElapsedMilliseconds} ms");
                }
                dagMs = stageTimer.ElapsedMilliseconds;

                stageTimer.Restart();
                var buildParts = BuildPartsFromGroups(subMeshList);
                int buildPartCount = buildParts.Count;
                int pageCount = (buildPartCount + MaxPartPerPage - 1) / MaxPartPerPage;
                partMs = stageTimer.ElapsedMilliseconds;

                Debug.Log($"[Nanite] Part 划分完成: parts={buildPartCount}, pages={pageCount}, {partMs} ms");

                if (buildPartCount == 0)
                {
                    Debug.LogWarning("[Nanite] 未生成任何 Part，Bake 中止。");
                    return;
                }

                string meshAssetPath = AssetDatabase.GetAssetPath(mesh);
                if (string.IsNullOrEmpty(meshAssetPath))
                {
                    Debug.LogError("[Nanite] Mesh 必须是项目内的资源（有 Asset 路径）。");
                    return;
                }

                string basePath = meshAssetPath.Replace(Path.GetExtension(meshAssetPath), "");
                int totalNaniteVerts = 0;
                int totalIndexCount = 0;
                var pageArray = new NaniteMeshPage[pageCount];
                int partIndex = 0;

                stageTimer.Restart();
                for (int pageIdx = 0; pageIdx < pageCount; pageIdx++)
                {
                    float p = 0.4f + (pageCount <= 0 ? 0f : (pageIdx / (float)pageCount) * 0.55f);
                    UpdateProgress($"Build Page {pageIdx + 1}/{pageCount}", p);

                    var pageTimer = Stopwatch.StartNew();
                    var page = ScriptableObject.CreateInstance<NaniteMeshPage>();
                    pageArray[pageIdx] = page;

                    int startPart = pageIdx * MaxPartPerPage;
                    int partCount = Mathf.Min(MaxPartPerPage, buildPartCount - startPart);
                    page.parts = new NaniteMeshPart[partCount];

                    var pageClusters = new List<NaniteCluster>(partCount * MaxClusterPerPart);
                    var tempIndices = new List<int>();
                    var pageMips = new List<int>();

                    for (int j = 0; j < partCount; j++)
                    {
                        if ((j & 15) == 0)
                            ThrowIfCancelled();

                        var buildPart = buildParts[partIndex];
                        var partClusterIndices = buildPart.clusterIndices;
                        var clusters = subMeshList[buildPart.subMeshId].clusterList;

                        var part = new NaniteMeshPart
                        {
                            clusterStart = pageClusters.Count,
                        clusterCount = partClusterIndices.Count,
                        mipLevel = buildPart.mip
                        };

                        float maxParentErr = 0f;
                        for (int c = 0; c < partClusterIndices.Count; c++)
                        {
                            var cluster = clusters[partClusterIndices[c]];
                            pageMips.Add(cluster.mip);
                            totalIndexCount += cluster.indices.Length;

                            var nc = new NaniteCluster
                            {
                                indiceIndex = tempIndices.Count,
                                indiceCount = cluster.indices.Length,
                                parentError = cluster.parent.error,
                                parentSphere = SphereToVector4(cluster.parent),
                                selfError = cluster.self.error,
                                selfSphere = SphereToVector4(cluster.self),
                                subMeshId = buildPart.subMeshId,
                            partIndex = j,
                                vertexOffset = 0
                            };
                            maxParentErr = Mathf.Max(maxParentErr, nc.parentError);
                            tempIndices.AddRange(cluster.indices);
                            pageClusters.Add(nc);
                        }

                        var partBounds = NaniteMeshBuilder.MergeClusterBounds(clusters, partClusterIndices);
                        part.selfSphere = SphereToVector4(partBounds);
                        part.parentSphere = MergeParentSpheres(clusters, partClusterIndices);
                        part.maxParentLodError = maxParentErr;
                        page.parts[j] = part;
                        partIndex++;
                    }

                    page.clusterArray = pageClusters.ToArray();
                    page.indiceArray = tempIndices.ToArray();
                    page.clusterMip = pageMips.ToArray();
                    BuildPageBvh(page);

                    PackPageVertices(page, vertices, normals, tangents, uvs, out int pageVertCount);
                    totalNaniteVerts += pageVertCount;

                    string pagePath = $"{basePath}_p{pageIdx}.asset";
                    if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(pagePath) != null)
                        AssetDatabase.DeleteAsset(pagePath);
                    AssetDatabase.CreateAsset(page, pagePath);

                    Debug.Log(
                        $"[Nanite] Page {pageIdx + 1}/{pageCount}: parts={partCount}, clusters={page.clusterArray.Length}, " +
                        $"indices={page.indiceArray.Length}, verts={pageVertCount}, bvhNodes={page.bvhNodes.Length}, " +
                        $"{pageTimer.ElapsedMilliseconds} ms");
                }
                pageMs = stageTimer.ElapsedMilliseconds;

                stageTimer.Restart();
                UpdateProgress("Finalize assets", 0.97f);

                var naniteMesh = ScriptableObject.CreateInstance<NaniteMesh>();
                naniteMesh.subMeshCount = subMeshCount;
                naniteMesh.maxMipLevel = maxMipLevel;
                naniteMesh.pageArray = new NaniteMeshPage[pageCount];
                for (int i = 0; i < pageCount; i++)
                    naniteMesh.pageArray[i] = AssetDatabase.LoadAssetAtPath<NaniteMeshPage>($"{basePath}_p{i}.asset");

                var meshBounds = mesh.bounds;
                naniteMesh.boundingSphere = new Vector4(
                    meshBounds.center.x, meshBounds.center.y, meshBounds.center.z,
                    meshBounds.extents.magnitude);

                string meshPath = basePath + "_mesh.asset";
                if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(meshPath) != null)
                    AssetDatabase.DeleteAsset(meshPath);
                AssetDatabase.CreateAsset(naniteMesh, meshPath);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                saveMs = stageTimer.ElapsedMilliseconds;

                totalTimer.Stop();
                Debug.Log(
                    $"[Nanite] Bake 完成\n" +
                    $"  原 mesh 顶点: {vertices.Length}  Nanite 顶点(各 Page 合计): {totalNaniteVerts}\n" +
                    $"  Cluster: {totalClusterCount}  Part: {buildPartCount}  Page: {pageCount}\n" +
                    $"  阶段耗时(ms): DAG={dagMs}, Part={partMs}, Page={pageMs}, Save={saveMs}, Total={totalTimer.ElapsedMilliseconds}\n" +
                    $"  资源: {basePath}_mesh.asset");
            }
            finally
            {
                IsBakeRunning = false;
                sCancelRequested = false;
                EditorUtility.ClearProgressBar();
            }
        }

        static void ThrowIfCancelled()
        {
            if (sCancelRequested)
                throw new OperationCanceledException("Bake cancelled by hotkey.");
        }

        static void UpdateProgress(string message, float progress)
        {
            ThrowIfCancelled();

            string text = $"{message}  (按 F8 取消)";
            if (EditorUtility.DisplayCancelableProgressBar("Nanite", text, Mathf.Clamp01(progress)))
                throw new OperationCanceledException("Bake cancelled by user.");

            ThrowIfCancelled();
        }

        static List<BuildPart> BuildPartsFromGroups(List<NaniteSubMesh> subMeshList)
        {
            var buildParts = new List<BuildPart>();

            for (int subMeshIndex = 0; subMeshIndex < subMeshList.Count; subMeshIndex++)
            {
                var subMesh = subMeshList[subMeshIndex];
                var clusters = subMesh.clusterList;
                var groups = subMesh.clusterGroupList;
                BuildPart currentPart = null;

                for (int g = 0; g < groups.Count; g++)
                {
                    var group = groups[g];
                    for (int c = 0; c < group.children.Count; c++)
                    {
                        int clusterIndex = group.children[c];
                        int clusterMip = clusters[clusterIndex].mip;

                        if (currentPart == null
                            || currentPart.clusterIndices.Count >= MaxClusterPerPart
                            || currentPart.mip != clusterMip)
                        {
                            currentPart = new BuildPart
                            {
                                mip = clusterMip,
                                subMeshId = subMeshIndex
                            };
                            buildParts.Add(currentPart);
                        }

                        currentPart.clusterIndices.Add(clusterIndex);
                    }
                }
            }

            return buildParts;
        }

        static void PackPageVertices(
            NaniteMeshPage page,
            Vector3[] vertices,
            Vector3[] normals,
            Vector4[] tangents,
            Vector2[] uvs,
            out int vertexCount)
        {
            bool hasUv = uvs != null && uvs.Length == vertices.Length;
            bool hasTangents = tangents != null && tangents.Length == vertices.Length;
            var indexMap = new Dictionary<int, int>();
            var tempVerts = new List<Vector3>();
            var tempNormals = new List<Vector3>();
            var tempTangents = new List<Vector4>();
            var tempUvs = new List<Vector2>();

            for (int c = 0; c < page.clusterArray.Length; c++)
            {
                ref NaniteCluster cluster = ref page.clusterArray[c];
                int indexEnd = cluster.indiceIndex + cluster.indiceCount;

                for (int index = cluster.indiceIndex; index < indexEnd; index++)
                {
                    int vertIndex = page.indiceArray[index];
                    if (!indexMap.TryGetValue(vertIndex, out int newIndex))
                    {
                        newIndex = tempVerts.Count;
                        indexMap.Add(vertIndex, newIndex);
                        tempVerts.Add(vertices[vertIndex]);
                        tempNormals.Add(normals != null && vertIndex < normals.Length
                            ? normals[vertIndex]
                            : Vector3.up);
                        tempTangents.Add(hasTangents ? tangents[vertIndex] : new Vector4(1f, 0f, 0f, 1f));
                        tempUvs.Add(hasUv ? uvs[vertIndex] : Vector2.zero);
                    }

                    page.indiceArray[index] = newIndex;
                }

                cluster.vertexOffset = 0;
            }

            // Layout: position.xyz + uv.xy + normal.xyz + tangent.xyzw
            page.vertexStride = 12;
            page.vertexCount = tempVerts.Count;
            page.vertexData = new float[tempVerts.Count * page.vertexStride];

            for (int v = 0; v < tempVerts.Count; v++)
            {
                int o = v * page.vertexStride;
                page.vertexData[o + 0] = tempVerts[v].x;
                page.vertexData[o + 1] = tempVerts[v].y;
                page.vertexData[o + 2] = tempVerts[v].z;
                page.vertexData[o + 3] = tempUvs[v].x;
                page.vertexData[o + 4] = tempUvs[v].y;
                Vector3 n = tempNormals[v].sqrMagnitude > 1e-8f ? tempNormals[v].normalized : Vector3.up;
                page.vertexData[o + 5] = n.x;
                page.vertexData[o + 6] = n.y;
                page.vertexData[o + 7] = n.z;
                Vector4 t = tempTangents[v];
                Vector3 t3 = new Vector3(t.x, t.y, t.z);
                if (t3.sqrMagnitude < 1e-8f)
                    t3 = Vector3.right;
                t3.Normalize();
                page.vertexData[o + 8] = t3.x;
                page.vertexData[o + 9] = t3.y;
                page.vertexData[o + 10] = t3.z;
                page.vertexData[o + 11] = Mathf.Approximately(t.w, 0f) ? 1f : Mathf.Sign(t.w);
            }

            vertexCount = tempVerts.Count;
        }

        struct MortonPart
        {
            public uint morton;
            public int index;
        }

        static void BuildPageBvh(NaniteMeshPage page)
        {
            int partCount = page.parts != null ? page.parts.Length : 0;
            if (partCount == 0)
            {
                page.bvhNodes = System.Array.Empty<NaniteBvhNode>();
                page.mipBvhRoots = System.Array.Empty<int>();
                page.bvhRoot = -1;
                return;
            }

            int maxMip = 0;
            for (int i = 0; i < partCount; i++)
                maxMip = Mathf.Max(maxMip, page.parts[i].mipLevel);

            var perMipParts = new List<int>[maxMip + 1];
            page.mipBvhRoots = new int[maxMip + 1];
            for (int i = 0; i < page.mipBvhRoots.Length; i++)
                page.mipBvhRoots[i] = -1;

            for (int i = 0; i < partCount; i++)
            {
                int mip = Mathf.Max(0, page.parts[i].mipLevel);
                perMipParts[mip] ??= new List<int>();
                perMipParts[mip].Add(i);
            }

            var nodes = new List<NaniteBvhNode>(partCount * 2 + maxMip + 8);
            var lodRoots = new List<int>(maxMip + 1);

            for (int mip = 0; mip < perMipParts.Length; mip++)
            {
                var partIndices = perMipParts[mip];
                if (partIndices == null || partIndices.Count == 0)
                    continue;

                int root = BuildHierarchyFromPartIndices(page, partIndices, nodes);
                page.mipBvhRoots[mip] = root;
                lodRoots.Add(root);
            }

            if (lodRoots.Count == 0)
            {
                page.bvhNodes = System.Array.Empty<NaniteBvhNode>();
                page.bvhRoot = -1;
                return;
            }

            page.bvhRoot = lodRoots.Count == 1 ? lodRoots[0] : BuildHierarchyFromNodeIndices(lodRoots, nodes);
            page.bvhNodes = nodes.ToArray();
        }

        static int BuildHierarchyFromPartIndices(NaniteMeshPage page, List<int> partIndices, List<NaniteBvhNode> nodes)
        {
            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);

            for (int i = 0; i < partIndices.Count; i++)
            {
                Vector4 s = page.parts[partIndices[i]].selfSphere;
                var c = new Vector3(s.x, s.y, s.z);
                min = Vector3.Min(min, c);
                max = Vector3.Max(max, c);
            }

            var ext = max - min;
            if (ext.x < 1e-5f) ext.x = 1f;
            if (ext.y < 1e-5f) ext.y = 1f;
            if (ext.z < 1e-5f) ext.z = 1f;

            var morton = new MortonPart[partIndices.Count];
            for (int i = 0; i < partIndices.Count; i++)
            {
                Vector4 s = page.parts[partIndices[i]].selfSphere;
                float nx = Mathf.Clamp01((s.x - min.x) / ext.x);
                float ny = Mathf.Clamp01((s.y - min.y) / ext.y);
                float nz = Mathf.Clamp01((s.z - min.z) / ext.z);
                morton[i] = new MortonPart
                {
                    index = partIndices[i],
                    morton = Morton3D(nx, ny, nz)
                };
            }

            System.Array.Sort(morton, (a, b) => a.morton.CompareTo(b.morton));

            var leafNodeIndices = new List<int>(morton.Length);
            for (int i = 0; i < morton.Length; i++)
            {
                int partIndex = morton[i].index;
                var part = page.parts[partIndex];
                Vector4 lodSphere = part.parentSphere.w > 0f ? part.parentSphere : part.selfSphere;

                nodes.Add(new NaniteBvhNode
                {
                    sphere = part.selfSphere,
                    lodSphere = lodSphere,
                    maxParentLodError = part.maxParentLodError,
                    child0 = -1,
                    child1 = -1,
                    child2 = -1,
                    child3 = -1,
                    childCount = 0,
                    partIndex = partIndex
                });
                leafNodeIndices.Add(nodes.Count - 1);
            }

            return BuildHierarchyFromNodeIndices(leafNodeIndices, nodes);
        }

        static int BuildHierarchyFromNodeIndices(List<int> nodeIndices, List<NaniteBvhNode> nodes)
        {
            var current = new List<int>(nodeIndices);
            while (current.Count > 1)
            {
                var next = new List<int>((current.Count + 3) / 4);
                for (int i = 0; i < current.Count; i += 4)
                {
                    int count = Mathf.Min(4, current.Count - i);
                    int c0 = current[i + 0];
                    int c1 = count > 1 ? current[i + 1] : -1;
                    int c2 = count > 2 ? current[i + 2] : -1;
                    int c3 = count > 3 ? current[i + 3] : -1;

                    next.Add(CreateInternalNode(nodes, c0, c1, c2, c3, count));
                }

                current = next;
            }

            return current[0];
        }

        static int CreateInternalNode(List<NaniteBvhNode> nodes, int c0, int c1, int c2, int c3, int count)
        {
            Vector4 sphere = nodes[c0].sphere;
            Vector4 lodSphere = nodes[c0].lodSphere;
            float maxParentError = nodes[c0].maxParentLodError;

            if (c1 >= 0)
            {
                sphere = MergeSphere(sphere, nodes[c1].sphere);
                lodSphere = MergeSphere(lodSphere, nodes[c1].lodSphere);
                maxParentError = Mathf.Max(maxParentError, nodes[c1].maxParentLodError);
            }

            if (c2 >= 0)
            {
                sphere = MergeSphere(sphere, nodes[c2].sphere);
                lodSphere = MergeSphere(lodSphere, nodes[c2].lodSphere);
                maxParentError = Mathf.Max(maxParentError, nodes[c2].maxParentLodError);
            }

            if (c3 >= 0)
            {
                sphere = MergeSphere(sphere, nodes[c3].sphere);
                lodSphere = MergeSphere(lodSphere, nodes[c3].lodSphere);
                maxParentError = Mathf.Max(maxParentError, nodes[c3].maxParentLodError);
            }

            nodes.Add(new NaniteBvhNode
            {
                sphere = sphere,
                lodSphere = lodSphere,
                maxParentLodError = maxParentError,
                child0 = c0,
                child1 = c1,
                child2 = c2,
                child3 = c3,
                childCount = count,
                partIndex = -1
            });

            return nodes.Count - 1;
        }

        static Vector4 MergeParentSpheres(List<Cluster> clusters, List<int> group)
        {
            var centers = new Vector3[group.Count];
            var radii = new float[group.Count];

            for (int j = 0; j < group.Count; j++)
            {
                var b = clusters[group[j]].parent;
                centers[j] = b.center;
                radii[j] = b.radius;
            }

            var merged = MeshOptimizerNative.NativeComputeSphereBounds(
                centers,
                (UIntPtr)(uint)centers.Length,
                (UIntPtr)(uint)(sizeof(float) * 3),
                radii,
                (UIntPtr)(uint)sizeof(float));

            return new Vector4(merged.centerX, merged.centerY, merged.centerZ, merged.radius);
        }

        static Vector4 MergeSphere(Vector4 a, Vector4 b)
        {
            var ca = new Vector3(a.x, a.y, a.z);
            var cb = new Vector3(b.x, b.y, b.z);
            float ra = a.w;
            float rb = b.w;

            var d = cb - ca;
            float dist = d.magnitude;

            if (ra >= dist + rb)
                return a;
            if (rb >= dist + ra)
                return b;

            if (dist < 1e-6f)
            {
                float r = Mathf.Max(ra, rb);
                return new Vector4(ca.x, ca.y, ca.z, r);
            }

            float newR = (dist + ra + rb) * 0.5f;
            Vector3 newC = ca + d * ((newR - ra) / dist);
            return new Vector4(newC.x, newC.y, newC.z, newR);
        }

        static uint Morton3D(float x, float y, float z)
        {
            uint xx = (uint)Mathf.Clamp(Mathf.RoundToInt(x * 1023f), 0, 1023);
            uint yy = (uint)Mathf.Clamp(Mathf.RoundToInt(y * 1023f), 0, 1023);
            uint zz = (uint)Mathf.Clamp(Mathf.RoundToInt(z * 1023f), 0, 1023);
            return (ExpandBits(xx) << 2) | (ExpandBits(yy) << 1) | ExpandBits(zz);
        }

        static uint ExpandBits(uint v)
        {
            v &= 0x000003ffu;
            v = (v | (v << 16)) & 0x030000ffu;
            v = (v | (v << 8)) & 0x0300f00fu;
            v = (v | (v << 4)) & 0x030c30c3u;
            v = (v | (v << 2)) & 0x09249249u;
            return v;
        }

        static Vector4 SphereToVector4(LODBounds bounds) =>
            new Vector4(bounds.center.x, bounds.center.y, bounds.center.z, bounds.radius);
    }
}
#endif
