using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace Nanite
{
    /// <summary>
    /// 场景级 VisibilityBuffer 几何聚合后端：
    /// 1) 合并所有 proxy 为统一几何与元数据缓冲；
    /// 2) 维护 instance/subMesh -> material 映射；
    /// 3) 输出单次 DrawProceduralIndirect 所需缓冲。
    /// </summary>
    public sealed class NaniteSceneVisibilityBufferBackend : IDisposable
    {
        const int kMaxMaterials = 32;
        const int kLayoutVersion = 2;

        struct Slot
        {
            public NaniteRuntimeProxy proxy;
            public int proxyId;
            public NaniteMesh mesh;
            public Material[] materials;
            public int[] pageClusterBase;
            public int[] pageClusterCount;
        }

        readonly List<Slot> slots = new List<Slot>(32);
        readonly List<Material> materialList = new List<Material>(kMaxMaterials);
        readonly Dictionary<int, int> materialIdByObjectId = new Dictionary<int, int>(kMaxMaterials);

        ComputeBuffer vertexDataBuffer;
        ComputeBuffer indexBuffer;
        ComputeBuffer triangleClusterBuffer;
        ComputeBuffer trianglePageBuffer;
        ComputeBuffer triangleInstanceBuffer;
        ComputeBuffer triangleSubMeshBuffer;
        ComputeBuffer clusterVisibleBuffer;
        ComputeBuffer prevClusterVisibleBuffer;
        ComputeBuffer pass1ClusterVisibleBuffer;
        ComputeBuffer pass2ClusterVisibleBuffer;
        ComputeBuffer secondPassCandidateBuffer;
        bool hasPrevVisible;
        int prevVisibleGeometryGeneration = -1;
        ComputeBuffer instanceLocalToWorldBuffer;
        ComputeBuffer instanceSubMeshMaterialBuffer;
        ComputeBuffer instanceShArBuffer;
        ComputeBuffer instanceShAgBuffer;
        ComputeBuffer instanceShAbBuffer;
        ComputeBuffer instanceShBrBuffer;
        ComputeBuffer instanceShBgBuffer;
        ComputeBuffer instanceShBbBuffer;
        ComputeBuffer instanceShCBuffer;
        GraphicsBuffer drawArgsBuffer;
        ComputeBuffer compactedTriIdsBuffer;
        ComputeBuffer compactCounterBuffer;
        ComputeBuffer clusterFirstTriBuffer;
        ComputeBuffer clusterTriCountBuffer;

        Matrix4x4[] instanceLocalToWorldCpu;
        int[] instanceSubMeshMaterialCpu;
        uint[] clusterVisibleCpu;
        Vector4[] instanceShArCpu;
        Vector4[] instanceShAgCpu;
        Vector4[] instanceShAbCpu;
        Vector4[] instanceShBrCpu;
        Vector4[] instanceShBgCpu;
        Vector4[] instanceShBbCpu;
        Vector4[] instanceShCCpu;

        int rebuildSignature;
        int geometryGeneration;
        int vertexStride;
        int indexCount;
        int triangleCount;
        int clusterCount;
        int maxSubMeshCount;
        bool warnedMaterialOverflow;
        bool warnedLightmapFallback;
        Material fallbackMaterial;

        public bool IsReady =>
            vertexDataBuffer != null &&
            indexBuffer != null &&
            triangleClusterBuffer != null &&
            trianglePageBuffer != null &&
            triangleInstanceBuffer != null &&
            triangleSubMeshBuffer != null &&
            clusterVisibleBuffer != null &&
            prevClusterVisibleBuffer != null &&
            pass1ClusterVisibleBuffer != null &&
            pass2ClusterVisibleBuffer != null &&
            secondPassCandidateBuffer != null &&
            instanceLocalToWorldBuffer != null &&
            instanceSubMeshMaterialBuffer != null &&
            instanceShArBuffer != null &&
            instanceShAgBuffer != null &&
            instanceShAbBuffer != null &&
            instanceShBrBuffer != null &&
            instanceShBgBuffer != null &&
            instanceShBbBuffer != null &&
            instanceShCBuffer != null &&
            drawArgsBuffer != null &&
            compactedTriIdsBuffer != null &&
            compactCounterBuffer != null &&
            clusterFirstTriBuffer != null &&
            clusterTriCountBuffer != null &&
            slots.Count > 0 &&
            triangleCount > 0 &&
            indexCount > 0;

        public int MaterialCount => materialList.Count;
        public int MaxMaterialCount => kMaxMaterials;
        public int MaxSubMeshCount => maxSubMeshCount;
        public int TriangleCount => triangleCount;
        public int ClusterCount => clusterCount;
        public int InstanceCount => slots.Count;
        public int GeometryGeneration => geometryGeneration;
        public ComputeBuffer ClusterVisibleBuffer => clusterVisibleBuffer;
        public ComputeBuffer PrevClusterVisibleBuffer => prevClusterVisibleBuffer;
        public ComputeBuffer Pass1ClusterVisibleBuffer => pass1ClusterVisibleBuffer;
        public ComputeBuffer Pass2ClusterVisibleBuffer => pass2ClusterVisibleBuffer;
        public ComputeBuffer SecondPassCandidateBuffer => secondPassCandidateBuffer;
        public ComputeBuffer CompactedTriIdsBuffer => compactedTriIdsBuffer;
        /// <summary>为 true 时跳过 CPU SetData，保留 GPU cull 直接写入的 clusterVisible。</summary>
        public bool GpuVisibleMaskReady { get; private set; }
        public bool HasPrevVisible =>
            hasPrevVisible &&
            prevClusterVisibleBuffer != null &&
            prevVisibleGeometryGeneration == geometryGeneration;

        public void MarkGpuVisibleMaskReady() => GpuVisibleMaskReady = true;
        public void ClearGpuVisibleMaskReady() => GpuVisibleMaskReady = false;

        public void InvalidatePrevVisible()
        {
            hasPrevVisible = false;
        }

        public void MarkPrevVisibleReady()
        {
            hasPrevVisible = prevClusterVisibleBuffer != null;
            prevVisibleGeometryGeneration = geometryGeneration;
        }

        /// <summary>Feature 回写的近似可见 cluster 数（GPU mask 路径，供 Proxy Inspector 显示）。</summary>
        public int LastGpuVisibleApprox { get; set; }

        public bool TryGetGlobalClusterIndex(int proxyId, int pageIndex, int localClusterIndex, out int globalIndex)
        {
            globalIndex = -1;
            for (int i = 0; i < slots.Count; i++)
            {
                Slot slot = slots[i];
                if (slot.proxyId != proxyId)
                    continue;
                if (slot.pageClusterBase == null || slot.pageClusterCount == null)
                    return false;
                if ((uint)pageIndex >= (uint)slot.pageClusterBase.Length)
                    return false;
                int pageBase = slot.pageClusterBase[pageIndex];
                int pageCount = slot.pageClusterCount[pageIndex];
                if (pageBase < 0 || (uint)localClusterIndex >= (uint)pageCount)
                    return false;
                globalIndex = pageBase + localClusterIndex;
                return (uint)globalIndex < (uint)clusterCount;
            }

            return false;
        }
        public ComputeBuffer InstanceShArBuffer => instanceShArBuffer;
        public ComputeBuffer InstanceShAgBuffer => instanceShAgBuffer;
        public ComputeBuffer InstanceShAbBuffer => instanceShAbBuffer;
        public ComputeBuffer InstanceShBrBuffer => instanceShBrBuffer;
        public ComputeBuffer InstanceShBgBuffer => instanceShBgBuffer;
        public ComputeBuffer InstanceShBbBuffer => instanceShBbBuffer;
        public ComputeBuffer InstanceShCBuffer => instanceShCBuffer;

        public void Dispose()
        {
            ReleaseBuffers();
            slots.Clear();
            materialList.Clear();
            materialIdByObjectId.Clear();
            instanceLocalToWorldCpu = null;
            instanceSubMeshMaterialCpu = null;
            clusterVisibleCpu = null;
            instanceShArCpu = null;
            instanceShAgCpu = null;
            instanceShAbCpu = null;
            instanceShBrCpu = null;
            instanceShBgCpu = null;
            instanceShBbCpu = null;
            instanceShCCpu = null;
            GpuVisibleMaskReady = false;
            rebuildSignature = 0;
            vertexStride = 0;
            indexCount = 0;
            triangleCount = 0;
            clusterCount = 0;
            maxSubMeshCount = 0;
            warnedMaterialOverflow = false;
            warnedLightmapFallback = false;
            ReleaseFallbackMaterial();
        }

        public bool EnsureInitialized(IReadOnlyList<NaniteRuntimeProxy> proxies)
        {
            if (proxies == null)
                return false;

            int signature = ComputeSignature(proxies);
            if (signature == rebuildSignature && IsReady)
            {
                UpdateInstanceTransforms();
                return true;
            }

            if (!Rebuild(proxies))
                return false;

            rebuildSignature = signature;
            GpuVisibleMaskReady = false;
            InvalidatePrevVisible();
            return true;
        }

        public bool UpdateVisibleClusters(Dictionary<int, NaniteRuntimeSelection> selectionsByProxyId)
        {
            if (!IsReady || selectionsByProxyId == null || clusterVisibleCpu == null)
                return false;

            // GPU-resident cull 已写入 mask，禁止 CPU SetData 覆盖。
            if (GpuVisibleMaskReady)
                return true;

            Array.Clear(clusterVisibleCpu, 0, clusterVisibleCpu.Length);
            for (int i = 0; i < slots.Count; i++)
            {
                Slot slot = slots[i];
                if (!selectionsByProxyId.TryGetValue(slot.proxyId, out var selection) || selection == null)
                    continue;

                if (selection.visibleClusters != null)
                {
                    for (int v = 0; v < selection.visibleClusters.Count; v++)
                    {
                        var vr = selection.visibleClusters[v];
                        if (vr.pageIndex < 0 || vr.pageIndex >= slot.pageClusterBase.Length)
                            continue;

                        int pageClusterBase = slot.pageClusterBase[vr.pageIndex];
                        if (pageClusterBase < 0)
                            continue;

                        int pageClusterCount = slot.pageClusterCount[vr.pageIndex];
                        if (vr.clusterIndex < 0 || vr.clusterIndex >= pageClusterCount)
                            continue;

                        int globalCluster = pageClusterBase + vr.clusterIndex;
                        if ((uint)globalCluster >= (uint)clusterVisibleCpu.Length)
                            continue;
                        clusterVisibleCpu[globalCluster] = 1;
                    }
                }

                if (selection.packets == null)
                    continue;

                for (int p = 0; p < selection.packets.Count; p++)
                {
                    var packet = selection.packets[p];
                    if (packet.pageIndex < 0 || packet.pageIndex >= slot.pageClusterBase.Length)
                        continue;

                    int pageClusterBase = slot.pageClusterBase[packet.pageIndex];
                    if (pageClusterBase < 0)
                        continue;

                    int pageClusterCount = slot.pageClusterCount[packet.pageIndex];
                    if (packet.clusterIndex < 0 || packet.clusterIndex >= pageClusterCount)
                        continue;

                    int globalCluster = pageClusterBase + packet.clusterIndex;
                    if ((uint)globalCluster >= (uint)clusterVisibleCpu.Length)
                        continue;
                    clusterVisibleCpu[globalCluster] = 1;
                }
            }

            clusterVisibleBuffer.SetData(clusterVisibleCpu);
            return true;
        }

        public int TryGetCompactedTriangleCount()
        {
            if (drawArgsBuffer == null)
                return -1;
            var args = new uint[4];
            drawArgsBuffer.GetData(args);
            // drawArgs[0] = indexCount = triCount * 3
            return (int)(args[0] / 3u);
        }

        public int CountVisibleClustersCpu()
        {
            if (clusterVisibleCpu == null)
                return 0;
            int n = 0;
            for (int i = 0; i < clusterVisibleCpu.Length; i++)
            {
                if (clusterVisibleCpu[i] != 0)
                    n++;
            }
            return n;
        }

        /// <summary>
        /// 在 UpdateVisibleClusters 之后调用：按可见 cluster expand 三角形并刷新 drawArgs。
        /// </summary>
        public bool DispatchVisibleTriangleCompact(CommandBuffer cmd, ComputeShader shader, int kernelClear, int kernelCompact, int kernelFinalize)
        {
            if (!IsReady || cmd == null || shader == null || kernelClear < 0 || kernelCompact < 0 || kernelFinalize < 0)
                return false;
            if (clusterFirstTriBuffer == null || clusterTriCountBuffer == null || drawArgsBuffer == null)
                return false;

            BindCompactKernelCommon(cmd, shader, kernelClear, kernelCompact, kernelFinalize);
            cmd.DispatchCompute(shader, kernelClear, 1, 1, 1);
            int groups = Mathf.Max(1, (clusterCount + 63) / 64);
            cmd.DispatchCompute(shader, kernelCompact, groups, 1, 1);
            cmd.DispatchCompute(shader, kernelFinalize, 1, 1, 1);
            return true;
        }

        public bool DispatchVisibleTriangleCompact(UnsafeCommandBuffer cmd, ComputeShader shader, int kernelClear, int kernelCompact, int kernelFinalize)
        {
            if (!IsReady || cmd == null || shader == null || kernelClear < 0 || kernelCompact < 0 || kernelFinalize < 0)
                return false;
            if (clusterFirstTriBuffer == null || clusterTriCountBuffer == null || drawArgsBuffer == null)
                return false;

            BindCompactKernelCommon(cmd, shader, kernelClear, kernelCompact, kernelFinalize);
            cmd.DispatchCompute(shader, kernelClear, 1, 1, 1);
            int groups = Mathf.Max(1, (clusterCount + 63) / 64);
            cmd.DispatchCompute(shader, kernelCompact, groups, 1, 1);
            cmd.DispatchCompute(shader, kernelFinalize, 1, 1, 1);
            return true;
        }

        static readonly int CompactClusterVisibleId = Shader.PropertyToID("_ClusterVisible");
        static readonly int CompactClusterFirstTriId = Shader.PropertyToID("_ClusterFirstTri");
        static readonly int CompactClusterTriCountId = Shader.PropertyToID("_ClusterTriCount");
        static readonly int CompactedTriIdsId = Shader.PropertyToID("_CompactedTriIds");
        static readonly int CompactCounterId = Shader.PropertyToID("_CompactCounter");
        static readonly int CompactDrawArgsId = Shader.PropertyToID("_DrawArgs");
        static readonly int CompactClusterCountId = Shader.PropertyToID("_ClusterCount");

        void BindCompactKernelCommon(CommandBuffer cmd, ComputeShader shader, int kernelClear, int kernelCompact, int kernelFinalize)
        {
            cmd.SetComputeIntParam(shader, CompactClusterCountId, Mathf.Max(0, clusterCount));
            // Unity 把全局 buffer 声明挂到所有 kernel；每个 kernel 都绑全量，避免 "Property is not set"。
            BindCompactKernelResources(cmd, shader, kernelClear);
            BindCompactKernelResources(cmd, shader, kernelCompact);
            BindCompactKernelResources(cmd, shader, kernelFinalize);
        }

        void BindCompactKernelCommon(UnsafeCommandBuffer cmd, ComputeShader shader, int kernelClear, int kernelCompact, int kernelFinalize)
        {
            cmd.SetComputeIntParam(shader, CompactClusterCountId, Mathf.Max(0, clusterCount));
            BindCompactKernelResources(cmd, shader, kernelClear);
            BindCompactKernelResources(cmd, shader, kernelCompact);
            BindCompactKernelResources(cmd, shader, kernelFinalize);
        }

        void BindCompactKernelResources(CommandBuffer cmd, ComputeShader shader, int kernel)
        {
            cmd.SetComputeBufferParam(shader, kernel, CompactClusterVisibleId, clusterVisibleBuffer);
            cmd.SetComputeBufferParam(shader, kernel, CompactClusterFirstTriId, clusterFirstTriBuffer);
            cmd.SetComputeBufferParam(shader, kernel, CompactClusterTriCountId, clusterTriCountBuffer);
            cmd.SetComputeBufferParam(shader, kernel, CompactedTriIdsId, compactedTriIdsBuffer);
            cmd.SetComputeBufferParam(shader, kernel, CompactCounterId, compactCounterBuffer);
            cmd.SetComputeBufferParam(shader, kernel, CompactDrawArgsId, drawArgsBuffer);
        }

        void BindCompactKernelResources(UnsafeCommandBuffer cmd, ComputeShader shader, int kernel)
        {
            cmd.SetComputeBufferParam(shader, kernel, CompactClusterVisibleId, clusterVisibleBuffer);
            cmd.SetComputeBufferParam(shader, kernel, CompactClusterFirstTriId, clusterFirstTriBuffer);
            cmd.SetComputeBufferParam(shader, kernel, CompactClusterTriCountId, clusterTriCountBuffer);
            cmd.SetComputeBufferParam(shader, kernel, CompactedTriIdsId, compactedTriIdsBuffer);
            cmd.SetComputeBufferParam(shader, kernel, CompactCounterId, compactCounterBuffer);
            cmd.SetComputeBufferParam(shader, kernel, CompactDrawArgsId, drawArgsBuffer);
        }

        public Material GetMaterial(int materialId)
        {
            if (materialId >= 0 && materialId < materialList.Count)
                return materialList[materialId];
            return EnsureFallbackMaterial();
        }

        public bool TryGetSceneBuffers(
            out ComputeBuffer outVertexData,
            out ComputeBuffer outIndices,
            out ComputeBuffer outTriangleCluster,
            out ComputeBuffer outTrianglePage,
            out ComputeBuffer outTriangleInstance,
            out ComputeBuffer outTriangleSubMesh,
            out ComputeBuffer outClusterVisible,
            out ComputeBuffer outInstanceLocalToWorld,
            out ComputeBuffer outInstanceSubMeshMaterial,
            out GraphicsBuffer outDrawArgs,
            out int outVertexStride,
            out int outIndexCount)
        {
            outVertexData = vertexDataBuffer;
            outIndices = indexBuffer;
            outTriangleCluster = triangleClusterBuffer;
            outTrianglePage = trianglePageBuffer;
            outTriangleInstance = triangleInstanceBuffer;
            outTriangleSubMesh = triangleSubMeshBuffer;
            outClusterVisible = clusterVisibleBuffer;
            outInstanceLocalToWorld = instanceLocalToWorldBuffer;
            outInstanceSubMeshMaterial = instanceSubMeshMaterialBuffer;
            outDrawArgs = drawArgsBuffer;
            outVertexStride = vertexStride;
            outIndexCount = indexCount;
            return IsReady;
        }

        int ComputeSignature(IReadOnlyList<NaniteRuntimeProxy> proxies)
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + kLayoutVersion;
                for (int i = 0; i < proxies.Count; i++)
                {
                    var proxy = proxies[i];
                    if (!IsValidProxy(proxy))
                        continue;

                    hash = hash * 31 + proxy.GetInstanceID();
                    hash = hash * 31 + proxy.naniteMesh.GetInstanceID();

                    var mats = ResolveMaterials(proxy);
                    int len = mats != null ? mats.Length : 0;
                    hash = hash * 31 + len;
                    for (int m = 0; m < len; m++)
                        hash = hash * 31 + (mats[m] != null ? mats[m].GetInstanceID() : 0);
                }

                return hash;
            }
        }

        bool Rebuild(IReadOnlyList<NaniteRuntimeProxy> proxies)
        {
            ReleaseBuffers();
            slots.Clear();
            materialList.Clear();
            materialIdByObjectId.Clear();
            warnedMaterialOverflow = false;
            warnedLightmapFallback = false;

            int stride = -1;
            int totalVertices = 0;
            int totalIndices = 0;
            int totalTriangles = 0;
            int totalClusters = 0;
            int maxSubMesh = 1;

            for (int i = 0; i < proxies.Count; i++)
            {
                var proxy = proxies[i];
                if (!IsValidProxy(proxy))
                    continue;

                var mesh = proxy.naniteMesh;
                int pageCount = mesh.pageArray != null ? mesh.pageArray.Length : 0;
                if (pageCount <= 0)
                    continue;

                var slot = new Slot
                {
                    proxy = proxy,
                    proxyId = proxy.GetInstanceID(),
                    mesh = mesh,
                    materials = ResolveMaterials(proxy),
                    pageClusterBase = new int[pageCount],
                    pageClusterCount = new int[pageCount]
                };

                for (int p = 0; p < pageCount; p++)
                {
                    slot.pageClusterBase[p] = -1;
                    slot.pageClusterCount[p] = 0;
                }

                maxSubMesh = Mathf.Max(maxSubMesh, slot.materials != null ? slot.materials.Length : 0);
                maxSubMesh = Mathf.Max(maxSubMesh, mesh.subMeshCount);

                for (int pageIndex = 0; pageIndex < pageCount; pageIndex++)
                {
                    var page = mesh.pageArray[pageIndex];
                    if (!IsValidPage(page))
                        continue;

                    int pageStride = Mathf.Max(3, page.vertexStride);
                    if (stride < 0)
                        stride = pageStride;
                    if (pageStride != stride)
                    {
                        Debug.LogWarning($"[Nanite][SceneVBuffer] Vertex stride mismatch. proxy={proxy.name} page={pageIndex} stride={pageStride} expected={stride}");
                        continue;
                    }

                    slot.pageClusterBase[pageIndex] = totalClusters;
                    slot.pageClusterCount[pageIndex] = page.clusterArray != null ? page.clusterArray.Length : 0;
                    totalClusters += slot.pageClusterCount[pageIndex];
                    totalVertices += page.vertexCount;
                    totalIndices += page.indiceArray.Length;
                    totalTriangles += page.indiceArray.Length / 3;
                }

                slots.Add(slot);
            }

            if (slots.Count == 0 || stride < 0 || totalVertices <= 0 || totalIndices <= 0 || totalTriangles <= 0)
                return false;

            vertexStride = stride;
            indexCount = totalIndices;
            triangleCount = totalTriangles;
            clusterCount = totalClusters;
            maxSubMeshCount = Mathf.Max(1, maxSubMesh);
            instanceLocalToWorldCpu = new Matrix4x4[slots.Count];
            instanceSubMeshMaterialCpu = new int[slots.Count * maxSubMeshCount];
            clusterVisibleCpu = new uint[Mathf.Max(1, totalClusters)];
            var clusterFirstTriCpu = new uint[Mathf.Max(1, totalClusters)];
            var clusterTriCountCpu = new uint[Mathf.Max(1, totalClusters)];
            instanceShArCpu = new Vector4[slots.Count];
            instanceShAgCpu = new Vector4[slots.Count];
            instanceShAbCpu = new Vector4[slots.Count];
            instanceShBrCpu = new Vector4[slots.Count];
            instanceShBgCpu = new Vector4[slots.Count];
            instanceShBbCpu = new Vector4[slots.Count];
            instanceShCCpu = new Vector4[slots.Count];

            var mergedVertices = new float[totalVertices * stride];
            var mergedIndices = new int[totalIndices];
            var mergedTriCluster = new int[totalTriangles];
            var mergedTriPage = new int[totalTriangles];
            var mergedTriInstance = new int[totalTriangles];
            var mergedTriSubMesh = new int[totalTriangles];

            for (int t = 0; t < totalTriangles; t++)
            {
                mergedTriCluster[t] = -1;
                mergedTriPage[t] = -1;
                mergedTriInstance[t] = -1;
                mergedTriSubMesh[t] = 0;
            }

            RegisterMaterial(EnsureFallbackMaterial());

            int vertexCursor = 0;
            int indexCursor = 0;
            int triCursor = 0;
            for (int inst = 0; inst < slots.Count; inst++)
            {
                Slot slot = slots[inst];
                instanceLocalToWorldCpu[inst] = slot.proxy.transform.localToWorldMatrix;
                FillInstanceSubMeshMaterial(inst, slot.materials);
                UpdateInstanceProbeSh(inst, slot);

                var pages = slot.mesh.pageArray;
                for (int pageIndex = 0; pageIndex < pages.Length; pageIndex++)
                {
                    var page = pages[pageIndex];
                    if (!IsValidPage(page))
                        continue;

                    int srcVertexCount = page.vertexCount;
                    int srcTriangleCount = page.indiceArray.Length / 3;
                    int vertexOffset = vertexCursor;

                    for (int v = 0; v < srcVertexCount; v++)
                    {
                        int srcBase = v * stride;
                        int dstBase = (vertexCursor + v) * stride;
                        for (int c = 0; c < stride; c++)
                        {
                            int srcIdx = srcBase + c;
                            mergedVertices[dstBase + c] = (srcIdx >= 0 && srcIdx < page.vertexData.Length) ? page.vertexData[srcIdx] : 0f;
                        }
                    }

                    for (int i = 0; i < page.indiceArray.Length; i++)
                        mergedIndices[indexCursor + i] = page.indiceArray[i] + vertexOffset;

                    int[] triClusterLocal = BuildTriangleClusterMap(page, srcTriangleCount);
                    int pageClusterBase = slot.pageClusterBase[pageIndex];
                    for (int t = 0; t < srcTriangleCount; t++)
                    {
                        int localCluster = triClusterLocal[t];
                        int globalTri = triCursor + t;
                        mergedTriCluster[globalTri] = (localCluster >= 0 && pageClusterBase >= 0) ? pageClusterBase + localCluster : -1;
                        mergedTriPage[globalTri] = pageIndex;
                        mergedTriInstance[globalTri] = inst;
                        if (localCluster >= 0 && page.clusterArray != null && localCluster < page.clusterArray.Length)
                            mergedTriSubMesh[globalTri] = page.clusterArray[localCluster].subMeshId;
                    }

                    if (page.clusterArray != null && pageClusterBase >= 0)
                    {
                        for (int ci = 0; ci < page.clusterArray.Length; ci++)
                        {
                            int globalCluster = pageClusterBase + ci;
                            if ((uint)globalCluster >= (uint)clusterFirstTriCpu.Length)
                                continue;
                            var cl = page.clusterArray[ci];
                            clusterFirstTriCpu[globalCluster] = (uint)(triCursor + Mathf.Max(0, cl.indiceIndex / 3));
                            clusterTriCountCpu[globalCluster] = (uint)Mathf.Max(0, cl.indiceCount / 3);
                        }
                    }

                    vertexCursor += srcVertexCount;
                    indexCursor += page.indiceArray.Length;
                    triCursor += srcTriangleCount;
                }
            }

            vertexDataBuffer = new ComputeBuffer(mergedVertices.Length, sizeof(float), ComputeBufferType.Structured);
            indexBuffer = new ComputeBuffer(mergedIndices.Length, sizeof(int), ComputeBufferType.Structured);
            triangleClusterBuffer = new ComputeBuffer(mergedTriCluster.Length, sizeof(int), ComputeBufferType.Structured);
            trianglePageBuffer = new ComputeBuffer(mergedTriPage.Length, sizeof(int), ComputeBufferType.Structured);
            triangleInstanceBuffer = new ComputeBuffer(mergedTriInstance.Length, sizeof(int), ComputeBufferType.Structured);
            triangleSubMeshBuffer = new ComputeBuffer(mergedTriSubMesh.Length, sizeof(int), ComputeBufferType.Structured);
            clusterVisibleBuffer = new ComputeBuffer(Mathf.Max(1, totalClusters), sizeof(uint), ComputeBufferType.Structured);
            prevClusterVisibleBuffer = new ComputeBuffer(Mathf.Max(1, totalClusters), sizeof(uint), ComputeBufferType.Structured);
            pass1ClusterVisibleBuffer = new ComputeBuffer(Mathf.Max(1, totalClusters), sizeof(uint), ComputeBufferType.Structured);
            pass2ClusterVisibleBuffer = new ComputeBuffer(Mathf.Max(1, totalClusters), sizeof(uint), ComputeBufferType.Structured);
            secondPassCandidateBuffer = new ComputeBuffer(Mathf.Max(1, totalClusters), sizeof(uint), ComputeBufferType.Structured);
            hasPrevVisible = false;
            prevVisibleGeometryGeneration = -1;
            clusterFirstTriBuffer = new ComputeBuffer(Mathf.Max(1, totalClusters), sizeof(uint), ComputeBufferType.Structured);
            clusterTriCountBuffer = new ComputeBuffer(Mathf.Max(1, totalClusters), sizeof(uint), ComputeBufferType.Structured);
            instanceLocalToWorldBuffer = new ComputeBuffer(instanceLocalToWorldCpu.Length, sizeof(float) * 16, ComputeBufferType.Structured);
            instanceSubMeshMaterialBuffer = new ComputeBuffer(instanceSubMeshMaterialCpu.Length, sizeof(int), ComputeBufferType.Structured);
            instanceShArBuffer = new ComputeBuffer(instanceShArCpu.Length, sizeof(float) * 4, ComputeBufferType.Structured);
            instanceShAgBuffer = new ComputeBuffer(instanceShAgCpu.Length, sizeof(float) * 4, ComputeBufferType.Structured);
            instanceShAbBuffer = new ComputeBuffer(instanceShAbCpu.Length, sizeof(float) * 4, ComputeBufferType.Structured);
            instanceShBrBuffer = new ComputeBuffer(instanceShBrCpu.Length, sizeof(float) * 4, ComputeBufferType.Structured);
            instanceShBgBuffer = new ComputeBuffer(instanceShBgCpu.Length, sizeof(float) * 4, ComputeBufferType.Structured);
            instanceShBbBuffer = new ComputeBuffer(instanceShBbCpu.Length, sizeof(float) * 4, ComputeBufferType.Structured);
            instanceShCBuffer = new ComputeBuffer(instanceShCCpu.Length, sizeof(float) * 4, ComputeBufferType.Structured);
            // IndirectArguments|Structured：可被 compute UAV 写入，并直接给 DrawProceduralIndirect。
            drawArgsBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.IndirectArguments | GraphicsBuffer.Target.Structured,
                4,
                sizeof(uint));
            compactedTriIdsBuffer = new ComputeBuffer(Mathf.Max(1, triangleCount), sizeof(uint), ComputeBufferType.Structured);
            compactCounterBuffer = new ComputeBuffer(1, sizeof(uint), ComputeBufferType.Structured);

            vertexDataBuffer.SetData(mergedVertices);
            indexBuffer.SetData(mergedIndices);
            triangleClusterBuffer.SetData(mergedTriCluster);
            trianglePageBuffer.SetData(mergedTriPage);
            triangleInstanceBuffer.SetData(mergedTriInstance);
            triangleSubMeshBuffer.SetData(mergedTriSubMesh);
            clusterVisibleBuffer.SetData(clusterVisibleCpu);
            prevClusterVisibleBuffer.SetData(clusterVisibleCpu);
            pass1ClusterVisibleBuffer.SetData(clusterVisibleCpu);
            pass2ClusterVisibleBuffer.SetData(clusterVisibleCpu);
            secondPassCandidateBuffer.SetData(clusterVisibleCpu);
            clusterFirstTriBuffer.SetData(clusterFirstTriCpu);
            clusterTriCountBuffer.SetData(clusterTriCountCpu);
            instanceLocalToWorldBuffer.SetData(instanceLocalToWorldCpu);
            instanceSubMeshMaterialBuffer.SetData(instanceSubMeshMaterialCpu);
            instanceShArBuffer.SetData(instanceShArCpu);
            instanceShAgBuffer.SetData(instanceShAgCpu);
            instanceShAbBuffer.SetData(instanceShAbCpu);
            instanceShBrBuffer.SetData(instanceShBrCpu);
            instanceShBgBuffer.SetData(instanceShBgCpu);
            instanceShBbBuffer.SetData(instanceShBbCpu);
            instanceShCBuffer.SetData(instanceShCCpu);
            // 默认空绘制；首帧 compact 后写入 visibleTris*3。未启用 compact 时由 RF 回填全量 args。
            uint[] drawArgs = { 0u, 1u, 0u, 0u };
            drawArgsBuffer.SetData(drawArgs);
            compactCounterBuffer.SetData(new uint[] { 0u });
            geometryGeneration++;
            return true;
        }

        public void ResetDrawArgsToFullMesh()
        {
            if (drawArgsBuffer == null || indexCount <= 0)
                return;
            drawArgsBuffer.SetData(new uint[] { (uint)indexCount, 1u, 0u, 0u });
        }

        int lastShUploadFrame = -100000;

        void UpdateInstanceTransforms()
        {
            if (!IsReady || instanceLocalToWorldCpu == null || instanceLocalToWorldBuffer == null)
                return;

            bool transformDirty = false;
            for (int i = 0; i < slots.Count; i++)
            {
                Matrix4x4 m = slots[i].proxy.transform.localToWorldMatrix;
                if (instanceLocalToWorldCpu[i] != m)
                {
                    instanceLocalToWorldCpu[i] = m;
                    transformDirty = true;
                }
            }

            if (transformDirty)
                instanceLocalToWorldBuffer.SetData(instanceLocalToWorldCpu);

            // SH / LightProbe：每帧 7 次 SetData + GetInterpolatedProbe 很贵；变换脏或隔帧再刷。
            bool needSh = transformDirty || (Time.frameCount - lastShUploadFrame) >= 2;
            if (!needSh)
                return;

            for (int i = 0; i < slots.Count; i++)
                UpdateInstanceProbeSh(i, slots[i]);

            instanceShArBuffer?.SetData(instanceShArCpu);
            instanceShAgBuffer?.SetData(instanceShAgCpu);
            instanceShAbBuffer?.SetData(instanceShAbCpu);
            instanceShBrBuffer?.SetData(instanceShBrCpu);
            instanceShBgBuffer?.SetData(instanceShBgCpu);
            instanceShBbBuffer?.SetData(instanceShBbCpu);
            instanceShCBuffer?.SetData(instanceShCCpu);
            lastShUploadFrame = Time.frameCount;
        }

        void FillInstanceSubMeshMaterial(int instanceIndex, Material[] materials)
        {
            int baseOffset = instanceIndex * maxSubMeshCount;
            Material fallback = EnsureFallbackMaterial();
            for (int s = 0; s < maxSubMeshCount; s++)
            {
                Material src = fallback;
                if (materials != null && materials.Length > 0)
                {
                    int matIndex = Mathf.Clamp(s, 0, materials.Length - 1);
                    src = materials[matIndex] != null ? materials[matIndex] : fallback;
                }

                instanceSubMeshMaterialCpu[baseOffset + s] = RegisterMaterial(src);
            }
        }

        void UpdateInstanceProbeSh(int instanceIndex, Slot slot)
        {
            if (instanceIndex < 0 || instanceIndex >= slots.Count ||
                instanceShArCpu == null || instanceShAgCpu == null || instanceShAbCpu == null ||
                instanceShBrCpu == null || instanceShBgCpu == null || instanceShBbCpu == null || instanceShCCpu == null)
                return;

            var renderer = ResolveSourceRenderer(slot.proxy);
            var worldPos = slot.proxy != null ? slot.proxy.transform.position : Vector3.zero;
            if (!warnedLightmapFallback && renderer != null && renderer.lightmapIndex >= 0)
            {
                warnedLightmapFallback = true;
                Debug.LogWarning("[Nanite][SceneVBuffer] 检测到 Lightmap Renderer。Formal Resolve 目前使用每实例 SH 近似，不含 UV2 Lightmap 采样，RT3 与 URP Lit 仍可能存在差异。");
            }
            if (!TryGetProbeSh(renderer, worldPos, out var sh))
                sh = RenderSettings.ambientProbe;
            PackUnityShCoefficients(sh,
                out instanceShArCpu[instanceIndex],
                out instanceShAgCpu[instanceIndex],
                out instanceShAbCpu[instanceIndex],
                out instanceShBrCpu[instanceIndex],
                out instanceShBgCpu[instanceIndex],
                out instanceShBbCpu[instanceIndex],
                out instanceShCCpu[instanceIndex]);
        }

        static bool TryGetProbeSh(Renderer renderer, Vector3 worldPos, out SphericalHarmonicsL2 sh)
        {
            var probes = LightmapSettings.lightProbes;
            if (probes != null && probes.count > 0)
            {
                LightProbes.GetInterpolatedProbe(worldPos, renderer, out sh);
                return true;
            }

            sh = RenderSettings.ambientProbe;
            return true;
        }

        static void PackUnityShCoefficients(
            SphericalHarmonicsL2 sh,
            out Vector4 shAr, out Vector4 shAg, out Vector4 shAb,
            out Vector4 shBr, out Vector4 shBg, out Vector4 shBb,
            out Vector4 shC)
        {
            shAr = new Vector4(sh[0, 3], sh[0, 1], sh[0, 2], sh[0, 0] - sh[0, 6]);
            shAg = new Vector4(sh[1, 3], sh[1, 1], sh[1, 2], sh[1, 0] - sh[1, 6]);
            shAb = new Vector4(sh[2, 3], sh[2, 1], sh[2, 2], sh[2, 0] - sh[2, 6]);

            shBr = new Vector4(sh[0, 4], sh[0, 5], sh[0, 6] * 3.0f, sh[0, 7]);
            shBg = new Vector4(sh[1, 4], sh[1, 5], sh[1, 6] * 3.0f, sh[1, 7]);
            shBb = new Vector4(sh[2, 4], sh[2, 5], sh[2, 6] * 3.0f, sh[2, 7]);

            shC = new Vector4(sh[0, 8], sh[1, 8], sh[2, 8], 1.0f);
        }

        int RegisterMaterial(Material material)
        {
            Material resolved = material != null ? material : EnsureFallbackMaterial();
            int key = resolved.GetInstanceID();
            if (materialIdByObjectId.TryGetValue(key, out int existing))
                return existing;

            if (materialList.Count >= kMaxMaterials)
            {
                if (!warnedMaterialOverflow)
                {
                    warnedMaterialOverflow = true;
                    Debug.LogWarning($"[Nanite][SceneVBuffer] Material count exceeds {kMaxMaterials}. Overflow materials fallback to id=0.");
                }
                return 0;
            }

            int id = materialList.Count;
            materialList.Add(resolved);
            materialIdByObjectId[key] = id;
            return id;
        }

        static Renderer ResolveSourceRenderer(NaniteRuntimeProxy proxy)
        {
            if (proxy == null)
                return null;
            var renderer = proxy.GetComponent<Renderer>();
            if (renderer != null)
                return renderer;
            return proxy.GetComponentInChildren<Renderer>();
        }

        static Material[] ResolveMaterials(NaniteRuntimeProxy proxy)
        {
            if (proxy != null && proxy.resolveMaterials != null && proxy.resolveMaterials.Length > 0)
                return proxy.resolveMaterials;

            var renderer = ResolveSourceRenderer(proxy);
            if (renderer == null || renderer.sharedMaterials == null || renderer.sharedMaterials.Length == 0)
                return new Material[0];
            return renderer.sharedMaterials;
        }

        static bool IsValidProxy(NaniteRuntimeProxy proxy) =>
            proxy != null &&
            proxy.isActiveAndEnabled &&
            proxy.naniteMesh != null &&
            proxy.naniteMesh.pageArray != null &&
            proxy.naniteMesh.pageArray.Length > 0;

        static bool IsValidPage(NaniteMeshPage page) =>
            page != null &&
            page.vertexData != null &&
            page.indiceArray != null &&
            page.vertexCount > 0 &&
            page.indiceArray.Length >= 3;

        static int[] BuildTriangleClusterMap(NaniteMeshPage page, int triangleCount)
        {
            var triCluster = new int[triangleCount];
            for (int i = 0; i < triCluster.Length; i++)
                triCluster[i] = -1;

            if (page?.clusterArray == null)
                return triCluster;

            for (int ci = 0; ci < page.clusterArray.Length; ci++)
            {
                var cluster = page.clusterArray[ci];
                int startTri = Mathf.Max(0, cluster.indiceIndex / 3);
                int triCount = Mathf.Max(0, cluster.indiceCount / 3);
                int endTri = Mathf.Min(triangleCount, startTri + triCount);
                for (int t = startTri; t < endTri; t++)
                    triCluster[t] = ci;
            }

            return triCluster;
        }

        Material EnsureFallbackMaterial()
        {
            if (fallbackMaterial != null)
                return fallbackMaterial;
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
                shader = Shader.Find("Standard");
            fallbackMaterial = shader != null ? new Material(shader) : null;
            if (fallbackMaterial != null)
            {
                fallbackMaterial.name = "Nanite_SceneVBuffer_FallbackMaterial";
                if (fallbackMaterial.HasProperty("_BaseColor"))
                    fallbackMaterial.SetColor("_BaseColor", Color.white);
                if (fallbackMaterial.HasProperty("_Color"))
                    fallbackMaterial.SetColor("_Color", Color.white);
                if (fallbackMaterial.HasProperty("_Cutoff"))
                    fallbackMaterial.SetFloat("_Cutoff", 0f);
                if (fallbackMaterial.HasProperty("_Smoothness"))
                    fallbackMaterial.SetFloat("_Smoothness", 0.5f);
                if (fallbackMaterial.HasProperty("_Glossiness"))
                    fallbackMaterial.SetFloat("_Glossiness", 0.5f);
                if (fallbackMaterial.HasProperty("_Metallic"))
                    fallbackMaterial.SetFloat("_Metallic", 0f);
            }
            return fallbackMaterial;
        }

        void ReleaseFallbackMaterial()
        {
            if (fallbackMaterial == null)
                return;
            if (Application.isPlaying)
                UnityEngine.Object.Destroy(fallbackMaterial);
            else
                UnityEngine.Object.DestroyImmediate(fallbackMaterial);
            fallbackMaterial = null;
        }

        void ReleaseBuffers()
        {
            vertexDataBuffer?.Release();
            indexBuffer?.Release();
            triangleClusterBuffer?.Release();
            trianglePageBuffer?.Release();
            triangleInstanceBuffer?.Release();
            triangleSubMeshBuffer?.Release();
            clusterVisibleBuffer?.Release();
            prevClusterVisibleBuffer?.Release();
            pass1ClusterVisibleBuffer?.Release();
            pass2ClusterVisibleBuffer?.Release();
            secondPassCandidateBuffer?.Release();
            instanceLocalToWorldBuffer?.Release();
            instanceSubMeshMaterialBuffer?.Release();
            instanceShArBuffer?.Release();
            instanceShAgBuffer?.Release();
            instanceShAbBuffer?.Release();
            instanceShBrBuffer?.Release();
            instanceShBgBuffer?.Release();
            instanceShBbBuffer?.Release();
            instanceShCBuffer?.Release();
            drawArgsBuffer?.Dispose();
            compactedTriIdsBuffer?.Release();
            compactCounterBuffer?.Release();
            clusterFirstTriBuffer?.Release();
            clusterTriCountBuffer?.Release();

            vertexDataBuffer = null;
            indexBuffer = null;
            triangleClusterBuffer = null;
            trianglePageBuffer = null;
            triangleInstanceBuffer = null;
            triangleSubMeshBuffer = null;
            clusterVisibleBuffer = null;
            prevClusterVisibleBuffer = null;
            pass1ClusterVisibleBuffer = null;
            pass2ClusterVisibleBuffer = null;
            secondPassCandidateBuffer = null;
            hasPrevVisible = false;
            prevVisibleGeometryGeneration = -1;
            instanceLocalToWorldBuffer = null;
            instanceSubMeshMaterialBuffer = null;
            instanceShArBuffer = null;
            instanceShAgBuffer = null;
            instanceShAbBuffer = null;
            instanceShBrBuffer = null;
            instanceShBgBuffer = null;
            instanceShBbBuffer = null;
            instanceShCBuffer = null;
            drawArgsBuffer = null;
            compactedTriIdsBuffer = null;
            compactCounterBuffer = null;
            clusterFirstTriBuffer = null;
            clusterTriCountBuffer = null;
        }
    }
}
