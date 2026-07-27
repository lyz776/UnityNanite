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
        const int kLayoutVersion = 5;

        struct Slot
        {
            public NaniteRuntimeProxy proxy;
            public int proxyId;
            public NaniteMesh mesh;
            public Material[] materials;
            public int geometryIndex;
            public int[] pageClusterBase;
            public int[] pageClusterCount;
        }

        // Keep the seven Unity SH vectors in one structured element.  Besides reducing
        // resource slots, this turns a probe refresh from seven SetData calls into one.
        struct InstanceShData
        {
            public Vector4 shAr;
            public Vector4 shAg;
            public Vector4 shAb;
            public Vector4 shBr;
            public Vector4 shBg;
            public Vector4 shBb;
            public Vector4 shC;
        }

        struct TrianglePageRef
        {
            public uint firstResidentIndex;
            public uint pageId;
        }

        sealed class GeometrySlot
        {
            public NaniteMesh mesh;
            public int[] pageVertexBase;
            public int[] pageTriangleBase;
            public int[] pageClusterBase;
        }

        readonly List<Slot> slots = new List<Slot>(32);
        readonly List<GeometrySlot> geometrySlots = new List<GeometrySlot>(16);
        readonly Dictionary<int, int> geometryIndexByMeshId = new Dictionary<int, int>(16);
        readonly List<int> probeDirtyInstances = new List<int>(32);
        readonly List<Material> materialList = new List<Material>(kMaxMaterials);
        readonly Dictionary<int, int> materialIdByObjectId = new Dictionary<int, int>(kMaxMaterials);
        readonly NaniteGpuPagePool pagePool = new NaniteGpuPagePool();

        ComputeBuffer vertexDataBuffer;
        ComputeBuffer indexBuffer;
        ComputeBuffer triangleClusterBuffer;
        ComputeBuffer trianglePageBuffer;
        ComputeBuffer trianglePageRefBuffer;
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
        ComputeBuffer instanceShBuffer;
        GraphicsBuffer drawArgsBuffer;
        ComputeBuffer compactedTriIdsBuffer;
        ComputeBuffer compactedTriInstancesBuffer;
        ComputeBuffer compactedTriCountsBuffer;
        ComputeBuffer compactCounterBuffer;
        ComputeBuffer clusterFirstTriBuffer;
        ComputeBuffer clusterTriCountBuffer;
        ComputeBuffer clusterInstanceBuffer;

        Matrix4x4[] instanceLocalToWorldCpu;
        int[] instanceSubMeshMaterialCpu;
        uint[] clusterVisibleCpu;
        InstanceShData[] instanceShCpu;

        int rebuildSignature;
        int registryRevision = -1;
        int lastInstanceUpdateFrame = -1;
        int geometryGeneration;
        int vertexStride;
        int geometryVertexCount;
        int virtualVertexCount;
        int indexCount;
        int triangleCount;
        int virtualTriangleCount;
        int compactedClusterTriangleSlots;
        int clusterCount;
        int maxSubMeshCount;
        bool warnedMaterialOverflow;
        bool warnedLightmapFallback;
        bool packedPageRasterRequested = true;
        int pagePoolMaxMiB = 128;
        ComputeShader pageTranscodeShader;
        Material fallbackMaterial;

        public bool IsReady =>
            vertexDataBuffer != null &&
            indexBuffer != null &&
            triangleClusterBuffer != null &&
            trianglePageBuffer != null &&
            trianglePageRefBuffer != null &&
            triangleInstanceBuffer != null &&
            triangleSubMeshBuffer != null &&
            clusterVisibleBuffer != null &&
            prevClusterVisibleBuffer != null &&
            pass1ClusterVisibleBuffer != null &&
            pass2ClusterVisibleBuffer != null &&
            secondPassCandidateBuffer != null &&
            instanceLocalToWorldBuffer != null &&
            instanceSubMeshMaterialBuffer != null &&
            instanceShBuffer != null &&
            drawArgsBuffer != null &&
            compactedTriIdsBuffer != null &&
            compactedTriInstancesBuffer != null &&
            compactedTriCountsBuffer != null &&
            compactCounterBuffer != null &&
            clusterFirstTriBuffer != null &&
            clusterTriCountBuffer != null &&
            clusterInstanceBuffer != null &&
            slots.Count > 0 &&
            triangleCount > 0 &&
            indexCount > 0;

        public int MaterialCount => materialList.Count;
        public int MaxMaterialCount => kMaxMaterials;
        public int MaxSubMeshCount => maxSubMeshCount;
        public int TriangleCount => triangleCount;
        public int VirtualTriangleCount => virtualTriangleCount;
        public int CompactedClusterTriangleSlots => compactedClusterTriangleSlots;
        public int ClusterCount => clusterCount;
        public int InstanceCount => slots.Count;
        public int UniqueMeshCount => geometrySlots.Count;
        public int GeometryVertexCount => geometryVertexCount;
        public int VirtualVertexCount => virtualVertexCount;
        public int GeometryGeneration => geometryGeneration;
        public int PagePoolMaxMiB
        {
            get => pagePoolMaxMiB;
            set
            {
                int next = Mathf.Max(1, value);
                if (pagePoolMaxMiB == next)
                    return;
                pagePoolMaxMiB = next;
                registryRevision = -1;
            }
        }
        public bool PackedPageRasterRequested
        {
            get => packedPageRasterRequested;
            set
            {
                if (packedPageRasterRequested == value)
                    return;
                packedPageRasterRequested = value;
                registryRevision = -1;
            }
        }
        public ComputeShader PageTranscodeShader
        {
            get => pageTranscodeShader;
            set
            {
                if (pageTranscodeShader == value)
                    return;
                pageTranscodeShader = value;
                registryRevision = -1;
            }
        }
        public bool UsePackedPageRaster { get; private set; }
        public bool PageStreamingRequestsEnabled { get; set; }
        public bool IsPagePoolReady => pagePool.IsReady;
        public int GlobalPageCount => pagePool.PageCount;
        public int ResidentPageCount => pagePool.ResidentPageCount;
        public int PinnedPageCount => pagePool.PinnedPageCount;
        public int RootPageCount => pagePool.RootPageCount;
        public int PagePoolBytes => pagePool.PoolBytes;
        public int ResidentPagePayloadBytes => pagePool.ResidentBytes;
        public long ResidentGeometryBytes => pagePool.ResidentGeometryBytes;
        public long CompatibilityGeometryBytes =>
            (long)geometryVertexCount * Mathf.Max(0, vertexStride) * sizeof(float) +
            (long)indexCount * sizeof(int);
        public long TrianglePageRefBytes => (long)triangleCount * sizeof(uint) * 2L;
        public bool PagePoolRequiresEviction => pagePool.RequiresEviction;
        public bool NeedsPageRetirementFence => pagePool.NeedsRetirementFence;
        public GraphicsBuffer PackedPagePoolBuffer => pagePool.PackedPoolBuffer;
        public ComputeBuffer PageTableBuffer => pagePool.PageTableBuffer;
        public ComputeBuffer PageDecodeBuffer => pagePool.PageDecodeBuffer;
        public ComputeBuffer ResidentPageTableBuffer => pagePool.ResidentPageTableBuffer;
        public GraphicsBuffer ResidentVertexBuffer => pagePool.ResidentVertexBuffer;
        public GraphicsBuffer ResidentIndexBuffer => pagePool.ResidentIndexBuffer;
        public ComputeBuffer PageResidencyBitsetBuffer => pagePool.ResidencyBitsetBuffer;
        public ComputeBuffer PageRequestBitsetBuffer => pagePool.RequestBitsetBuffer;
        public ComputeBuffer TrianglePageRefBuffer => trianglePageRefBuffer;
        public void UpdatePageStreaming(int frameIndex, int readbackIntervalFrames, int maxUploadsPerPoll)
        {
            if (PageStreamingRequestsEnabled)
                pagePool.UpdateStreaming(frameIndex, readbackIntervalFrames, maxUploadsPerPoll);
        }
        public void AttachPageRetirementFence(GraphicsFence fence) =>
            pagePool.AttachRetirementFence(fence);
        /// <summary>0 refreshes probes only when an instance moves; positive values also refresh periodically.</summary>
        public int LightProbeRefreshInterval { get; set; } = 30;
        public ComputeBuffer ClusterVisibleBuffer => clusterVisibleBuffer;
        public ComputeBuffer PrevClusterVisibleBuffer => prevClusterVisibleBuffer;
        public ComputeBuffer Pass1ClusterVisibleBuffer => pass1ClusterVisibleBuffer;
        public ComputeBuffer Pass2ClusterVisibleBuffer => pass2ClusterVisibleBuffer;
        public ComputeBuffer SecondPassCandidateBuffer => secondPassCandidateBuffer;
        public ComputeBuffer CompactedTriIdsBuffer => compactedTriIdsBuffer;
        public ComputeBuffer CompactedTriInstancesBuffer => compactedTriInstancesBuffer;
        public ComputeBuffer CompactedTriCountsBuffer => compactedTriCountsBuffer;
        public ComputeBuffer ClusterFirstTriBuffer => clusterFirstTriBuffer;
        public ComputeBuffer ClusterTriCountBuffer => clusterTriCountBuffer;
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

        public bool TryGetGlobalPageId(int proxyId, int localPageIndex, out int globalPageId)
        {
            globalPageId = -1;
            for (int i = 0; i < slots.Count; i++)
            {
                Slot slot = slots[i];
                if (slot.proxyId != proxyId)
                    continue;
                return pagePool.TryGetPageId(slot.mesh, localPageIndex, out globalPageId);
            }
            return false;
        }
        public ComputeBuffer InstanceShBuffer => instanceShBuffer;

        public void Dispose()
        {
            ReleaseBuffers();
            slots.Clear();
            geometrySlots.Clear();
            geometryIndexByMeshId.Clear();
            materialList.Clear();
            materialIdByObjectId.Clear();
            instanceLocalToWorldCpu = null;
            instanceSubMeshMaterialCpu = null;
            clusterVisibleCpu = null;
            instanceShCpu = null;
            GpuVisibleMaskReady = false;
            rebuildSignature = 0;
            registryRevision = -1;
            lastInstanceUpdateFrame = -1;
            vertexStride = 0;
            geometryVertexCount = 0;
            virtualVertexCount = 0;
            indexCount = 0;
            triangleCount = 0;
            virtualTriangleCount = 0;
            compactedClusterTriangleSlots = 0;
            clusterCount = 0;
            maxSubMeshCount = 0;
            UsePackedPageRaster = false;
            warnedMaterialOverflow = false;
            warnedLightmapFallback = false;
            pagePool.Dispose();
            ReleaseFallbackMaterial();
        }

        public bool EnsureInitialized(IReadOnlyList<NaniteRuntimeProxy> proxies)
        {
            if (proxies == null)
                return false;

            int currentRevision = NaniteRuntimeRegistry.Revision;
            if (currentRevision == registryRevision && IsReady)
            {
                UpdateInstanceTransforms();
                return true;
            }

            int signature = ComputeSignature(proxies);
            if (signature == rebuildSignature && IsReady)
            {
                registryRevision = currentRevision;
                UpdateInstanceTransforms();
                return true;
            }

            if (!Rebuild(proxies))
                return false;

            rebuildSignature = signature;
            registryRevision = currentRevision;
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
            // cluster compact 使用固定 triangle slots；这里返回实际进入 VS 的槽位数（包含簇尾空槽）。
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
        /// 在 UpdateVisibleClusters 之后调用：压实可见 cluster，并按固定 triangle slots 刷新 drawArgs。
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
        static readonly int CompactedTriInstancesId = Shader.PropertyToID("_CompactedTriInstances");
        static readonly int CompactedTriCountsId = Shader.PropertyToID("_CompactedTriCounts");
        static readonly int CompactedClusterTriangleSlotsId = Shader.PropertyToID("_CompactedClusterTriangleSlots");
        static readonly int CompactCounterId = Shader.PropertyToID("_CompactCounter");
        static readonly int CompactDrawArgsId = Shader.PropertyToID("_DrawArgs");
        static readonly int CompactClusterCountId = Shader.PropertyToID("_ClusterCount");
        static readonly int CompactClusterInstanceId = Shader.PropertyToID("_ClusterInstance");
        static readonly int VisibleDrawCountArgsId = Shader.PropertyToID("_VisibleDrawCountArgs");

        public bool DispatchVisibleDrawQueueFinalize(
            CommandBuffer cmd,
            ComputeShader shader,
            int kernelFinalizeQueue,
            ComputeBuffer visibleDrawCountArgs)
        {
            if (!IsReady || cmd == null || shader == null || kernelFinalizeQueue < 0 || visibleDrawCountArgs == null)
                return false;

            cmd.SetComputeIntParam(shader, CompactedClusterTriangleSlotsId, Mathf.Max(1, compactedClusterTriangleSlots));
            cmd.SetComputeBufferParam(shader, kernelFinalizeQueue, VisibleDrawCountArgsId, visibleDrawCountArgs);
            cmd.SetComputeBufferParam(shader, kernelFinalizeQueue, CompactDrawArgsId, drawArgsBuffer);
            cmd.DispatchCompute(shader, kernelFinalizeQueue, 1, 1, 1);
            return true;
        }

        public bool DispatchVisibleDrawQueueFinalize(
            UnsafeCommandBuffer cmd,
            ComputeShader shader,
            int kernelFinalizeQueue,
            ComputeBuffer visibleDrawCountArgs)
        {
            if (!IsReady || cmd == null || shader == null || kernelFinalizeQueue < 0 || visibleDrawCountArgs == null)
                return false;

            cmd.SetComputeIntParam(shader, CompactedClusterTriangleSlotsId, Mathf.Max(1, compactedClusterTriangleSlots));
            cmd.SetComputeBufferParam(shader, kernelFinalizeQueue, VisibleDrawCountArgsId, visibleDrawCountArgs);
            cmd.SetComputeBufferParam(shader, kernelFinalizeQueue, CompactDrawArgsId, drawArgsBuffer);
            cmd.DispatchCompute(shader, kernelFinalizeQueue, 1, 1, 1);
            return true;
        }

        public bool DispatchVisibleDrawQueueFinalize(
            UnsafeCommandBuffer cmd,
            ComputeShader shader,
            int kernelFinalizeQueue,
            ComputeBuffer visibleDrawCountArgs,
            GraphicsBuffer destinationDrawArgs)
        {
            if (!IsReady || cmd == null || shader == null || kernelFinalizeQueue < 0 ||
                visibleDrawCountArgs == null || destinationDrawArgs == null)
                return false;

            cmd.SetComputeIntParam(
                shader,
                CompactedClusterTriangleSlotsId,
                Mathf.Max(1, compactedClusterTriangleSlots));
            cmd.SetComputeBufferParam(
                shader,
                kernelFinalizeQueue,
                VisibleDrawCountArgsId,
                visibleDrawCountArgs);
            cmd.SetComputeBufferParam(
                shader,
                kernelFinalizeQueue,
                CompactDrawArgsId,
                destinationDrawArgs);
            cmd.DispatchCompute(shader, kernelFinalizeQueue, 1, 1, 1);
            return true;
        }

        void BindCompactKernelCommon(CommandBuffer cmd, ComputeShader shader, int kernelClear, int kernelCompact, int kernelFinalize)
        {
            cmd.SetComputeIntParam(shader, CompactClusterCountId, Mathf.Max(0, clusterCount));
            cmd.SetComputeIntParam(shader, CompactedClusterTriangleSlotsId, Mathf.Max(1, compactedClusterTriangleSlots));
            // Unity 把全局 buffer 声明挂到所有 kernel；每个 kernel 都绑全量，避免 "Property is not set"。
            BindCompactKernelResources(cmd, shader, kernelClear);
            BindCompactKernelResources(cmd, shader, kernelCompact);
            BindCompactKernelResources(cmd, shader, kernelFinalize);
        }

        void BindCompactKernelCommon(UnsafeCommandBuffer cmd, ComputeShader shader, int kernelClear, int kernelCompact, int kernelFinalize)
        {
            cmd.SetComputeIntParam(shader, CompactClusterCountId, Mathf.Max(0, clusterCount));
            cmd.SetComputeIntParam(shader, CompactedClusterTriangleSlotsId, Mathf.Max(1, compactedClusterTriangleSlots));
            BindCompactKernelResources(cmd, shader, kernelClear);
            BindCompactKernelResources(cmd, shader, kernelCompact);
            BindCompactKernelResources(cmd, shader, kernelFinalize);
        }

        void BindCompactKernelResources(CommandBuffer cmd, ComputeShader shader, int kernel)
        {
            cmd.SetComputeBufferParam(shader, kernel, CompactClusterVisibleId, clusterVisibleBuffer);
            cmd.SetComputeBufferParam(shader, kernel, CompactClusterFirstTriId, clusterFirstTriBuffer);
            cmd.SetComputeBufferParam(shader, kernel, CompactClusterTriCountId, clusterTriCountBuffer);
            cmd.SetComputeBufferParam(shader, kernel, CompactClusterInstanceId, clusterInstanceBuffer);
            cmd.SetComputeBufferParam(shader, kernel, CompactedTriIdsId, compactedTriIdsBuffer);
            cmd.SetComputeBufferParam(shader, kernel, CompactedTriInstancesId, compactedTriInstancesBuffer);
            cmd.SetComputeBufferParam(shader, kernel, CompactedTriCountsId, compactedTriCountsBuffer);
            cmd.SetComputeBufferParam(shader, kernel, CompactCounterId, compactCounterBuffer);
            cmd.SetComputeBufferParam(shader, kernel, CompactDrawArgsId, drawArgsBuffer);
        }

        void BindCompactKernelResources(UnsafeCommandBuffer cmd, ComputeShader shader, int kernel)
        {
            cmd.SetComputeBufferParam(shader, kernel, CompactClusterVisibleId, clusterVisibleBuffer);
            cmd.SetComputeBufferParam(shader, kernel, CompactClusterFirstTriId, clusterFirstTriBuffer);
            cmd.SetComputeBufferParam(shader, kernel, CompactClusterTriCountId, clusterTriCountBuffer);
            cmd.SetComputeBufferParam(shader, kernel, CompactClusterInstanceId, clusterInstanceBuffer);
            cmd.SetComputeBufferParam(shader, kernel, CompactedTriIdsId, compactedTriIdsBuffer);
            cmd.SetComputeBufferParam(shader, kernel, CompactedTriInstancesId, compactedTriInstancesBuffer);
            cmd.SetComputeBufferParam(shader, kernel, CompactedTriCountsId, compactedTriCountsBuffer);
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
                hash = hash * 31 + PagePoolMaxMiB;
                hash = hash * 31 + (PackedPageRasterRequested ? 1 : 0);
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
            geometrySlots.Clear();
            geometryIndexByMeshId.Clear();
            materialList.Clear();
            materialIdByObjectId.Clear();
            warnedMaterialOverflow = false;
            warnedLightmapFallback = false;

            int stride = -1;
            int totalVertices = 0;
            int totalIndices = 0;
            int totalTriangles = 0;
            int totalVirtualVertices = 0;
            int totalVirtualTriangles = 0;
            int totalClusters = 0;
            int totalGeometryClusters = 0;
            int maxClusterTriangles = 1;
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

                int meshId = mesh.GetInstanceID();
                if (!geometryIndexByMeshId.TryGetValue(meshId, out int geometryIndex))
                {
                    var geometry = new GeometrySlot
                    {
                        mesh = mesh,
                        pageVertexBase = new int[pageCount],
                        pageTriangleBase = new int[pageCount],
                        pageClusterBase = new int[pageCount]
                    };
                    for (int p = 0; p < pageCount; p++)
                    {
                        geometry.pageVertexBase[p] = -1;
                        geometry.pageTriangleBase[p] = -1;
                        geometry.pageClusterBase[p] = -1;
                    }

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
                            Debug.LogWarning($"[Nanite][SceneVBuffer] Vertex stride mismatch. mesh={mesh.name} page={pageIndex} stride={pageStride} expected={stride}");
                            continue;
                        }

                        geometry.pageVertexBase[pageIndex] = totalVertices;
                        geometry.pageTriangleBase[pageIndex] = totalTriangles;
                        geometry.pageClusterBase[pageIndex] = totalGeometryClusters;
                        totalVertices += page.vertexCount;
                        totalIndices += page.indiceArray.Length;
                        totalTriangles += page.indiceArray.Length / 3;
                        totalGeometryClusters += page.clusterArray != null ? page.clusterArray.Length : 0;
                        if (page.clusterArray != null)
                        {
                            for (int clusterIndex = 0; clusterIndex < page.clusterArray.Length; clusterIndex++)
                                maxClusterTriangles = Mathf.Max(maxClusterTriangles, page.clusterArray[clusterIndex].indiceCount / 3);
                        }
                    }

                    geometryIndex = geometrySlots.Count;
                    geometrySlots.Add(geometry);
                    geometryIndexByMeshId.Add(meshId, geometryIndex);
                }

                var slot = new Slot
                {
                    proxy = proxy,
                    proxyId = proxy.GetInstanceID(),
                    mesh = mesh,
                    materials = ResolveMaterials(proxy),
                    geometryIndex = geometryIndex,
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

                GeometrySlot slotGeometry = geometrySlots[geometryIndex];
                for (int pageIndex = 0; pageIndex < pageCount; pageIndex++)
                {
                    var page = mesh.pageArray[pageIndex];
                    if (!IsValidPage(page) || slotGeometry.pageVertexBase[pageIndex] < 0)
                        continue;

                    slot.pageClusterBase[pageIndex] = totalClusters;
                    slot.pageClusterCount[pageIndex] = page.clusterArray != null ? page.clusterArray.Length : 0;
                    totalClusters += slot.pageClusterCount[pageIndex];
                    totalVirtualVertices += page.vertexCount;
                    totalVirtualTriangles += page.indiceArray.Length / 3;
                }

                slots.Add(slot);
            }

            if (slots.Count == 0 || stride < 0 || totalVertices <= 0 || totalIndices <= 0 || totalTriangles <= 0)
                return false;

            var uniqueMeshes = new NaniteMesh[geometrySlots.Count];
            for (int i = 0; i < geometrySlots.Count; i++)
                uniqueMeshes[i] = geometrySlots[i].mesh;
            // Transitional: an old/oversized Bake may fail the 256 KiB pool contract. Keep
            // compatibility geometry alive, report the gate, and let a re-Bake repair it.
            pagePool.EnsureInitialized(uniqueMeshes, Mathf.Max(1, PagePoolMaxMiB));
            UsePackedPageRaster = false;
            if (PackedPageRasterRequested && pagePool.IsReady)
            {
                if (pagePool.EnsureAllPagesResidentForPackedRaster(
                        pageTranscodeShader,
                        out string packedPageError))
                {
                    UsePackedPageRaster = true;
                }
                else
                {
                    Debug.LogWarning(
                        $"[Nanite][PagePool] packed raster rejected; compatibility geometry remains active. " +
                        $"reason={packedPageError}");
                }
            }

            vertexStride = stride;
            geometryVertexCount = totalVertices;
            virtualVertexCount = totalVirtualVertices;
            indexCount = totalIndices;
            triangleCount = totalTriangles;
            virtualTriangleCount = totalVirtualTriangles;
            compactedClusterTriangleSlots = Mathf.Max(1, maxClusterTriangles);
            clusterCount = totalClusters;
            maxSubMeshCount = Mathf.Max(1, maxSubMesh);
            instanceLocalToWorldCpu = new Matrix4x4[slots.Count];
            instanceSubMeshMaterialCpu = new int[slots.Count * maxSubMeshCount];
            clusterVisibleCpu = new uint[Mathf.Max(1, totalClusters)];
            var clusterFirstTriCpu = new uint[Mathf.Max(1, totalClusters)];
            var clusterTriCountCpu = new uint[Mathf.Max(1, totalClusters)];
            var clusterInstanceCpu = new uint[Mathf.Max(1, totalClusters)];
            instanceShCpu = new InstanceShData[slots.Count];

            var mergedVertices = new float[totalVertices * stride];
            var mergedIndices = new int[totalIndices];
            var mergedTriCluster = new int[totalTriangles];
            var mergedTriPage = new int[totalTriangles];
            var mergedTriPageRefs = new TrianglePageRef[totalTriangles];
            // Scene compact 输出显式携带 instanceId；兼容 buffer 也只按唯一三角形分配，不随实例复制。
            var mergedTriInstance = new int[totalTriangles];
            var mergedTriSubMesh = new int[totalTriangles];

            for (int t = 0; t < totalTriangles; t++)
            {
                mergedTriCluster[t] = -1;
                mergedTriPage[t] = -1;
                mergedTriPageRefs[t] = new TrianglePageRef
                {
                    firstResidentIndex = NaniteGpuPagePool.InvalidSlot,
                    pageId = NaniteGpuPagePool.InvalidSlot
                };
                mergedTriSubMesh[t] = 0;
            }

            RegisterMaterial(EnsureFallbackMaterial());

            // 静态几何只按唯一 NaniteMesh 展开一次：顶点、索引和 triangle 元数据均不再随实例数增长。
            for (int geometryIndex = 0; geometryIndex < geometrySlots.Count; geometryIndex++)
            {
                GeometrySlot geometry = geometrySlots[geometryIndex];
                var pages = geometry.mesh.pageArray;
                for (int pageIndex = 0; pageIndex < pages.Length; pageIndex++)
                {
                    var page = pages[pageIndex];
                    int vertexBase = geometry.pageVertexBase[pageIndex];
                    int triangleBase = geometry.pageTriangleBase[pageIndex];
                    int geometryClusterBase = geometry.pageClusterBase[pageIndex];
                    if (!IsValidPage(page) || vertexBase < 0 || triangleBase < 0)
                        continue;

                    int floatCount = Mathf.Min(page.vertexData.Length, page.vertexCount * stride);
                    Array.Copy(page.vertexData, 0, mergedVertices, vertexBase * stride, floatCount);

                    int indexBase = triangleBase * 3;
                    for (int index = 0; index < page.indiceArray.Length; index++)
                        mergedIndices[indexBase + index] = page.indiceArray[index] + vertexBase;

                    int srcTriangleCount = page.indiceArray.Length / 3;
                    int[] triClusterLocal = BuildTriangleClusterMap(page, srcTriangleCount);
                    for (int triangle = 0; triangle < srcTriangleCount; triangle++)
                    {
                        int localCluster = triClusterLocal[triangle];
                        int geometryTriangle = triangleBase + triangle;
                        mergedTriCluster[geometryTriangle] = localCluster >= 0 && geometryClusterBase >= 0
                            ? geometryClusterBase + localCluster
                            : -1;
                        bool hasGlobalPage = pagePool.TryGetPageId(
                            geometry.mesh,
                            pageIndex,
                            out int globalPageId);
                        bool hasResidentRange = pagePool.TryGetResidentGeometryRange(
                            geometry.mesh,
                            pageIndex,
                            out _,
                            out int residentIndexBase);
                        mergedTriPage[geometryTriangle] = hasGlobalPage ? globalPageId : pageIndex;
                        mergedTriPageRefs[geometryTriangle] = new TrianglePageRef
                        {
                            firstResidentIndex = hasResidentRange
                                ? checked((uint)(residentIndexBase + triangle * 3))
                                : NaniteGpuPagePool.InvalidSlot,
                            pageId = hasGlobalPage
                                ? (uint)globalPageId
                                : NaniteGpuPagePool.InvalidSlot
                        };
                        if (localCluster >= 0 && page.clusterArray != null && localCluster < page.clusterArray.Length)
                            mergedTriSubMesh[geometryTriangle] = page.clusterArray[localCluster].subMeshId;
                    }
                }
            }

            // 可见性仍按实例维护，但每个 virtual cluster 只保存唯一几何三角形范围与 instanceId。
            for (int inst = 0; inst < slots.Count; inst++)
            {
                Slot slot = slots[inst];
                instanceLocalToWorldCpu[inst] = slot.proxy.transform.localToWorldMatrix;
                FillInstanceSubMeshMaterial(inst, slot.materials);
                UpdateInstanceProbeSh(inst, slot);

                var pages = slot.mesh.pageArray;
                GeometrySlot geometry = geometrySlots[slot.geometryIndex];
                for (int pageIndex = 0; pageIndex < pages.Length; pageIndex++)
                {
                    var page = pages[pageIndex];
                    int triangleBase = geometry.pageTriangleBase[pageIndex];
                    if (!IsValidPage(page) || triangleBase < 0)
                        continue;

                    int pageClusterBase = slot.pageClusterBase[pageIndex];
                    if (page.clusterArray != null && pageClusterBase >= 0)
                    {
                        for (int ci = 0; ci < page.clusterArray.Length; ci++)
                        {
                            int globalCluster = pageClusterBase + ci;
                            if ((uint)globalCluster >= (uint)clusterFirstTriCpu.Length)
                                continue;
                            var cl = page.clusterArray[ci];
                            int firstTriangle = Mathf.Max(0, cl.indiceIndex / 3);
                            int triangleLimit = page.indiceArray.Length / 3;
                            int triangleCountInCluster = Mathf.Min(
                                Mathf.Max(0, cl.indiceCount / 3),
                                Mathf.Max(0, triangleLimit - firstTriangle));
                            clusterFirstTriCpu[globalCluster] = (uint)(triangleBase + firstTriangle);
                            clusterTriCountCpu[globalCluster] = (uint)triangleCountInCluster;
                            clusterInstanceCpu[globalCluster] = (uint)inst;
                        }
                    }
                }
            }

            vertexDataBuffer = new ComputeBuffer(mergedVertices.Length, sizeof(float), ComputeBufferType.Structured);
            indexBuffer = new ComputeBuffer(mergedIndices.Length, sizeof(int), ComputeBufferType.Structured);
            triangleClusterBuffer = new ComputeBuffer(mergedTriCluster.Length, sizeof(int), ComputeBufferType.Structured);
            trianglePageBuffer = new ComputeBuffer(mergedTriPage.Length, sizeof(int), ComputeBufferType.Structured);
            trianglePageRefBuffer = new ComputeBuffer(
                mergedTriPageRefs.Length,
                sizeof(uint) * 2,
                ComputeBufferType.Structured);
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
            clusterInstanceBuffer = new ComputeBuffer(Mathf.Max(1, totalClusters), sizeof(uint), ComputeBufferType.Structured);
            instanceLocalToWorldBuffer = new ComputeBuffer(instanceLocalToWorldCpu.Length, sizeof(float) * 16, ComputeBufferType.Structured);
            instanceSubMeshMaterialBuffer = new ComputeBuffer(instanceSubMeshMaterialCpu.Length, sizeof(int), ComputeBufferType.Structured);
            instanceShBuffer = new ComputeBuffer(instanceShCpu.Length, sizeof(float) * 4 * 7, ComputeBufferType.Structured);
            // IndirectArguments|Structured：可被 compute UAV 写入，并直接给 DrawProceduralIndirect。
            drawArgsBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.IndirectArguments | GraphicsBuffer.Target.Structured,
                4,
                sizeof(uint));
            compactedTriIdsBuffer = new ComputeBuffer(Mathf.Max(1, totalClusters), sizeof(uint), ComputeBufferType.Structured);
            compactedTriInstancesBuffer = new ComputeBuffer(Mathf.Max(1, totalClusters), sizeof(uint), ComputeBufferType.Structured);
            compactedTriCountsBuffer = new ComputeBuffer(Mathf.Max(1, totalClusters), sizeof(uint), ComputeBufferType.Structured);
            compactCounterBuffer = new ComputeBuffer(1, sizeof(uint), ComputeBufferType.Structured);

            vertexDataBuffer.SetData(mergedVertices);
            indexBuffer.SetData(mergedIndices);
            triangleClusterBuffer.SetData(mergedTriCluster);
            trianglePageBuffer.SetData(mergedTriPage);
            trianglePageRefBuffer.SetData(mergedTriPageRefs);
            triangleInstanceBuffer.SetData(mergedTriInstance);
            triangleSubMeshBuffer.SetData(mergedTriSubMesh);
            clusterVisibleBuffer.SetData(clusterVisibleCpu);
            prevClusterVisibleBuffer.SetData(clusterVisibleCpu);
            pass1ClusterVisibleBuffer.SetData(clusterVisibleCpu);
            pass2ClusterVisibleBuffer.SetData(clusterVisibleCpu);
            secondPassCandidateBuffer.SetData(clusterVisibleCpu);
            clusterFirstTriBuffer.SetData(clusterFirstTriCpu);
            clusterTriCountBuffer.SetData(clusterTriCountCpu);
            clusterInstanceBuffer.SetData(clusterInstanceCpu);
            instanceLocalToWorldBuffer.SetData(instanceLocalToWorldCpu);
            instanceSubMeshMaterialBuffer.SetData(instanceSubMeshMaterialCpu);
            instanceShBuffer.SetData(instanceShCpu);
            // 默认空绘制；scene 路径必须先 compact，才能把唯一 geometry triangle 与 instance 正确配对。
            uint[] drawArgs = { 0u, 1u, 0u, 0u };
            drawArgsBuffer.SetData(drawArgs);
            compactCounterBuffer.SetData(new uint[] { 0u });
            lastInstanceUpdateFrame = Time.frameCount;
            lastShUploadFrame = Time.frameCount;
            geometryGeneration++;
            return true;
        }

        public void ResetDrawArgsToFullMesh()
        {
            if (drawArgsBuffer == null || indexCount <= 0)
                return;
            // 单实例可安全使用兼容路径；多实例必须使用 compacted (triangle, instance) 列表。
            uint vertexCount = slots.Count == 1 ? (uint)indexCount : 0u;
            drawArgsBuffer.SetData(new uint[] { vertexCount, 1u, 0u, 0u });
        }

        int lastShUploadFrame = -100000;

        void UpdateInstanceTransforms()
        {
            if (!IsReady || instanceLocalToWorldCpu == null || instanceLocalToWorldBuffer == null)
                return;
            if (lastInstanceUpdateFrame == Time.frameCount)
                return;
            lastInstanceUpdateFrame = Time.frameCount;

            bool transformDirty = false;
            probeDirtyInstances.Clear();
            for (int i = 0; i < slots.Count; i++)
            {
                Matrix4x4 m = slots[i].proxy.transform.localToWorldMatrix;
                if (instanceLocalToWorldCpu[i] != m)
                {
                    instanceLocalToWorldCpu[i] = m;
                    transformDirty = true;
                    probeDirtyInstances.Add(i);
                }
            }

            if (transformDirty)
                instanceLocalToWorldBuffer.SetData(instanceLocalToWorldCpu);

            // SH / LightProbe：每帧 7 次 SetData + GetInterpolatedProbe 很贵；变换脏或隔帧再刷。
            int refreshInterval = Mathf.Max(0, LightProbeRefreshInterval);
            bool periodicRefresh = refreshInterval > 0 &&
                                   (Time.frameCount - lastShUploadFrame) >= refreshInterval;
            if (!periodicRefresh && probeDirtyInstances.Count == 0)
                return;

            if (periodicRefresh)
            {
                for (int i = 0; i < slots.Count; i++)
                    UpdateInstanceProbeSh(i, slots[i]);
            }
            else
            {
                for (int i = 0; i < probeDirtyInstances.Count; i++)
                {
                    int instanceIndex = probeDirtyInstances[i];
                    UpdateInstanceProbeSh(instanceIndex, slots[instanceIndex]);
                }
            }

            instanceShBuffer?.SetData(instanceShCpu);
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
            if (instanceIndex < 0 || instanceIndex >= slots.Count || instanceShCpu == null)
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
                out var shAr,
                out var shAg,
                out var shAb,
                out var shBr,
                out var shBg,
                out var shBb,
                out var shC);
            instanceShCpu[instanceIndex] = new InstanceShData
            {
                shAr = shAr,
                shAg = shAg,
                shAb = shAb,
                shBr = shBr,
                shBg = shBg,
                shBb = shBb,
                shC = shC
            };
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
            proxy.NaniteRenderingActive &&
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
            trianglePageRefBuffer?.Release();
            triangleInstanceBuffer?.Release();
            triangleSubMeshBuffer?.Release();
            clusterVisibleBuffer?.Release();
            prevClusterVisibleBuffer?.Release();
            pass1ClusterVisibleBuffer?.Release();
            pass2ClusterVisibleBuffer?.Release();
            secondPassCandidateBuffer?.Release();
            instanceLocalToWorldBuffer?.Release();
            instanceSubMeshMaterialBuffer?.Release();
            instanceShBuffer?.Release();
            drawArgsBuffer?.Dispose();
            compactedTriIdsBuffer?.Release();
            compactedTriInstancesBuffer?.Release();
            compactedTriCountsBuffer?.Release();
            compactCounterBuffer?.Release();
            clusterFirstTriBuffer?.Release();
            clusterTriCountBuffer?.Release();
            clusterInstanceBuffer?.Release();

            vertexDataBuffer = null;
            indexBuffer = null;
            triangleClusterBuffer = null;
            trianglePageBuffer = null;
            trianglePageRefBuffer = null;
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
            instanceShBuffer = null;
            drawArgsBuffer = null;
            compactedTriIdsBuffer = null;
            compactedTriInstancesBuffer = null;
            compactedTriCountsBuffer = null;
            compactCounterBuffer = null;
            clusterFirstTriBuffer = null;
            clusterTriCountBuffer = null;
            clusterInstanceBuffer = null;
        }
    }
}
