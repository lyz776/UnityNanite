using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using Unity.Profiling;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule.Util;

namespace Nanite
{
    /// <summary>
    /// 全局批量 GPU 剔除：所有 proxy 合并为 1 次 PartCull + 1 次 ClusterCull。
    /// </summary>
    public sealed class NaniteGpuBatchedCullingBackend : IDisposable
    {
        struct BatchSlot
        {
            public NaniteRuntimeProxy proxy;
            public int proxyId;
            public NaniteMesh mesh;
            public int geometryIndex;
            public int virtualPartOffset;
            public int virtualClusterOffset;
            public int partCount;
            public int clusterCount;
            public int[] pagePartBase;
            public int[] pagePartCount;
        }

        sealed class GeometrySlot
        {
            public NaniteMesh mesh;
            public int partOffset;
            public int clusterOffset;
            public int partCount;
            public int clusterCount;
            public int[] pagePartBase;
            public int[] pagePartCount;
            public Vector4 bounds;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct GpuVirtualPartRef
        {
            public uint instanceIndex;
            public uint partIndex;
            public uint clusterStart;
            public uint clusterCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct GpuVirtualClusterRef
        {
            public uint instanceIndex;
            public uint clusterIndex;
            public uint partIndex;
            public uint reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct GpuVisibleRef
        {
            public uint instanceIndex;
            public uint pageIndex;
            public uint clusterIndex;
        }

        const int kThreadGroupSize = 64;
        const int kMaxShadowCascades = 4;
        const int kGpuLayoutVersion = 6;
        static readonly int[] kShadowVisibleDrawCountArgsIds =
        {
            Shader.PropertyToID("_VisibleDrawCountArgs0"),
            Shader.PropertyToID("_VisibleDrawCountArgs1"),
            Shader.PropertyToID("_VisibleDrawCountArgs2"),
            Shader.PropertyToID("_VisibleDrawCountArgs3")
        };
        static readonly int[] kShadowDrawArgsIds =
        {
            Shader.PropertyToID("_DrawArgs0"),
            Shader.PropertyToID("_DrawArgs1"),
            Shader.PropertyToID("_DrawArgs2"),
            Shader.PropertyToID("_DrawArgs3")
        };
        static readonly ProfilerMarker kDispatchMarker = new ProfilerMarker("Nanite.CPU.GpuCullDispatch");
        static readonly ProfilerMarker kCpuBvhMarker = new ProfilerMarker("Nanite.CPU.CpuBvhCandidates");

        public enum CullPassMode
        {
            Legacy = 0,
            Pass1PrevVisible = 1,
            Pass2CandidatesHzb = 2
        }

        ComputeShader shader;
        int kernelPartCull = -1;
        int kernelClusterCull = -1;
        int kernelClusterCullByPart = -1;
        int kernelFinalizeVisiblePartDispatch = -1;
        int kernelClusterCullVisibleParts = -1;
        int kernelInstanceCull = -1;
        int kernelPartCullVisibleInstances = -1;
        int kernelShadowCullMultiCascadeByPart = -1;
        int kernelClearClusterVisible = -1;
        int kernelClearSecondPassCandidates = -1;
        int kernelCopyUintBuffer = -1;
        int kernelClearCullStats = -1;
        int kernelOrMasksToVisible = -1;

        ComputeBuffer partsBuffer;
        ComputeBuffer clustersBuffer;
        ComputeBuffer virtualPartsBuffer;
        ComputeBuffer virtualClustersBuffer;
        ComputeBuffer visiblePartAppendBuffer;
        ComputeBuffer visiblePartDispatchArgsBuffer;
        ComputeBuffer visibleInstanceAppendBuffer;
        ComputeBuffer visibleInstancePartDispatchArgsBuffer;
        ComputeBuffer visibleDrawClusterAppendBuffer;
        ComputeBuffer visibleDrawCountArgsBuffer;
        readonly ComputeBuffer[] shadowDrawClusterAppendBuffers = new ComputeBuffer[kMaxShadowCascades];
        readonly ComputeBuffer[] shadowDrawCountArgsBuffers = new ComputeBuffer[kMaxShadowCascades];
        readonly GraphicsBuffer[] shadowDrawArgsBuffers = new GraphicsBuffer[kMaxShadowCascades];
        ComputeBuffer partVisibleBuffer;
        ComputeBuffer visibleClusterAppendBuffer;
        ComputeBuffer visibleCountBuffer;
        ComputeBuffer clusterCandidateBuffer;
        ComputeBuffer clusterSceneIndexBuffer;
        ComputeBuffer sceneClusterFirstTriBuffer;
        ComputeBuffer sceneClusterTriCountBuffer;
        ComputeBuffer scenePageResidencyBuffer;
        ComputeBuffer scenePageRequestBuffer;
        int scenePageCount;
        bool sceneTrackPageUsage;
        ComputeBuffer instanceDataBuffer;
        ComputeBuffer instanceVisibleBuffer;
        ComputeBuffer fallbackClusterVisibleBuffer;
        ComputeBuffer fallbackPrevVisibleBuffer;
        ComputeBuffer fallbackSecondPassBuffer;
        ComputeBuffer fallbackPass2DrawnBuffer;
        ComputeBuffer cullStatsBuffer;
        RenderTexture fallbackHzbTexture;
        readonly uint[] cullStatsCpu = new uint[3];

        readonly List<BatchSlot> slots = new List<BatchSlot>(32);
        readonly List<GeometrySlot> geometrySlots = new List<GeometrySlot>(16);
        readonly Dictionary<int, int> geometryIndexByMeshId = new Dictionary<int, int>(16);
        readonly List<NaniteGpuCullingBackend.GpuPartData> mergedParts = new List<NaniteGpuCullingBackend.GpuPartData>(8192);
        readonly List<NaniteGpuCullingBackend.GpuClusterData> mergedClusters = new List<NaniteGpuCullingBackend.GpuClusterData>(65536);
        readonly List<GpuVirtualPartRef> virtualParts = new List<GpuVirtualPartRef>(8192);
        readonly List<GpuVirtualClusterRef> virtualClusters = new List<GpuVirtualClusterRef>(65536);
        readonly List<NaniteGpuCullingBackend.GpuInstanceData> instanceData = new List<NaniteGpuCullingBackend.GpuInstanceData>(32);
        readonly List<int> bvhStack = new List<int>(256);
        readonly Plane[] frustumPlaneObjects = new Plane[6];
        readonly Vector4[] frustumPlanes = new Vector4[6];
        readonly Vector4[] shadowBatchFrustumPlanes = new Vector4[kMaxShadowCascades * 6];
        Vector4 shadowBatchProjectionScales;
        readonly Dictionary<int, List<NaniteVisibleClusterRef>> visibleScratch = new Dictionary<int, List<NaniteVisibleClusterRef>>();

        NaniteGpuCullingBackend.GpuPartData[] partDataCpu;
        NaniteGpuCullingBackend.GpuClusterData[] clusterDataCpu;
        GpuVirtualPartRef[] virtualPartRefsCpu;
        GpuVirtualClusterRef[] virtualClusterRefsCpu;
        uint[] partVisibleCpu;
        uint[] clusterCandidatesCpu;
        uint[] clusterSceneIndexCpu;
        uint[] visibleCountCpu = new uint[1];
        // 复用读回缓冲，避免每帧 new[] 触发 GC。
        GpuVisibleRef[] visibleRefsScratch = Array.Empty<GpuVisibleRef>();
        int partCount;
        int clusterCount;
        int geometryPartCount;
        int geometryClusterCount;
        int clusterCandidateCount;
        int instanceCount;
        int rebuildSignature;
        int registryRevision = -1;
        int sceneIndexSignature;
        int sceneClusterCountMapped;
        int lastVisibleDrawQueueFrame = -1;
        int lastVisibleDrawCameraId;
        int lastShadowDrawQueueFrame = -1;
        int lastShadowDrawCameraId;
        int lastShadowDrawCascadeMask;
        int lastTransformUpdateFrame = -1;

        public bool IsReady =>
            shader != null &&
            kernelPartCull >= 0 &&
            kernelClusterCull >= 0 &&
            partsBuffer != null &&
            clustersBuffer != null &&
            virtualPartsBuffer != null &&
            virtualClustersBuffer != null &&
            visiblePartAppendBuffer != null &&
            visiblePartDispatchArgsBuffer != null &&
            visibleInstanceAppendBuffer != null &&
            visibleInstancePartDispatchArgsBuffer != null &&
            visibleDrawClusterAppendBuffer != null &&
            visibleDrawCountArgsBuffer != null &&
            partVisibleBuffer != null &&
            visibleClusterAppendBuffer != null &&
            visibleCountBuffer != null &&
            clusterCandidateBuffer != null &&
            instanceDataBuffer != null &&
            instanceVisibleBuffer != null &&
            instanceCount > 0;

        public bool SupportsGpuVisibleMask =>
            IsReady &&
            kernelClearClusterVisible >= 0 &&
            clusterSceneIndexBuffer != null &&
            clusterSceneIndexCpu != null &&
            sceneClusterFirstTriBuffer != null &&
            sceneClusterTriCountBuffer != null &&
            sceneClusterCountMapped > 0;

        public void Dispose()
        {
            ReleaseBuffers();
            ReleaseFallbackHzbTexture();
            shader = null;
            kernelPartCull = -1;
            kernelClusterCull = -1;
            kernelClusterCullByPart = -1;
            kernelFinalizeVisiblePartDispatch = -1;
            kernelClusterCullVisibleParts = -1;
            kernelInstanceCull = -1;
            kernelPartCullVisibleInstances = -1;
            kernelShadowCullMultiCascadeByPart = -1;
            slots.Clear();
            geometrySlots.Clear();
            geometryIndexByMeshId.Clear();
            mergedParts.Clear();
            mergedClusters.Clear();
            virtualParts.Clear();
            virtualClusters.Clear();
            instanceData.Clear();
            partDataCpu = null;
            clusterDataCpu = null;
            virtualPartRefsCpu = null;
            virtualClusterRefsCpu = null;
            partVisibleCpu = null;
            clusterCandidatesCpu = null;
            clusterSceneIndexCpu = null;
            visibleRefsScratch = Array.Empty<GpuVisibleRef>();
            partCount = 0;
            clusterCount = 0;
            geometryPartCount = 0;
            geometryClusterCount = 0;
            clusterCandidateCount = 0;
            instanceCount = 0;
            lastTransformUpdateFrame = -1;
            rebuildSignature = 0;
            registryRevision = -1;
            sceneIndexSignature = 0;
            sceneClusterCountMapped = 0;
            lastVisibleDrawQueueFrame = -1;
            lastVisibleDrawCameraId = 0;
            lastShadowDrawQueueFrame = -1;
            lastShadowDrawCameraId = 0;
            lastShadowDrawCascadeMask = 0;
            LastClusterCandidateCount = 0;
            LastClusterCount = 0;
            LastCull1Drawn = 0;
            LastCull2Candidates = 0;
            LastCull2Drawn = 0;
            LastUsedCpuCandidates = false;
            LastUsedPartDrivenClusterCull = false;
            LastUsedVisiblePartQueue = false;
            LastUsedVisibleInstanceQueue = false;
            LastShadowUsedFusedBatch = false;
        }

        public bool EnsureClusterSceneIndex(NaniteSceneVisibilityBufferBackend scene)
        {
            if (!IsReady || scene == null || !scene.IsReady || clusterDataCpu == null ||
                virtualClusterRefsCpu == null || slots.Count == 0)
                return false;

            sceneClusterFirstTriBuffer = scene.ClusterFirstTriBuffer;
            sceneClusterTriCountBuffer = scene.ClusterTriCountBuffer;
            if (sceneClusterFirstTriBuffer == null || sceneClusterTriCountBuffer == null)
                return false;

            bool pageRequestsEnabled =
                scene.PageStreamingRequestsEnabled &&
                scene.IsPagePoolReady;
            scenePageResidencyBuffer = pageRequestsEnabled ? scene.PageResidencyBitsetBuffer : null;
            scenePageRequestBuffer = pageRequestsEnabled ? scene.PageRequestBitsetBuffer : null;
            scenePageCount = pageRequestsEnabled ? scene.GlobalPageCount : 0;
            sceneTrackPageUsage = pageRequestsEnabled && scene.PagePoolRequiresEviction;

            int sig = unchecked(
                scene.GeometryGeneration * 397 +
                clusterCount * 31 +
                geometryClusterCount * 17 +
                slots.Count +
                scenePageCount * 13);
            if (clusterSceneIndexBuffer != null &&
                clusterSceneIndexCpu != null &&
                clusterSceneIndexCpu.Length == clusterCount &&
                sceneIndexSignature == sig &&
                sceneClusterCountMapped == scene.ClusterCount)
                return true;

            clusterSceneIndexCpu = new uint[Mathf.Max(1, clusterCount)];
            int mapped = 0;
            for (int i = 0; i < clusterCount; i++)
            {
                var virtualCluster = virtualClusterRefsCpu[i];
                int inst = (int)virtualCluster.instanceIndex;
                uint sceneIndex = 0xFFFFFFFFu;
                if ((uint)inst < (uint)slots.Count && virtualCluster.clusterIndex < (uint)clusterDataCpu.Length)
                {
                    var c = clusterDataCpu[virtualCluster.clusterIndex];
                    int proxyId = slots[inst].proxyId;
                    if (scene.TryGetGlobalClusterIndex(proxyId, c.pageIndex, c.clusterIndex, out int gi) && gi >= 0)
                    {
                        sceneIndex = (uint)gi;
                        mapped++;
                    }
                    virtualCluster.reserved =
                        scene.TryGetGlobalPageId(proxyId, c.pageIndex, out int globalPageId) &&
                        globalPageId >= 0
                            ? (uint)globalPageId
                            : uint.MaxValue;
                    virtualClusterRefsCpu[i] = virtualCluster;
                }

                clusterSceneIndexCpu[i] = sceneIndex;
            }

            if (mapped <= 0)
                return false;

            clusterSceneIndexBuffer?.Release();
            clusterSceneIndexBuffer = new ComputeBuffer(clusterSceneIndexCpu.Length, sizeof(uint), ComputeBufferType.Structured);
            clusterSceneIndexBuffer.SetData(clusterSceneIndexCpu);
            virtualClustersBuffer.SetData(virtualClusterRefsCpu);
            sceneIndexSignature = sig;
            sceneClusterCountMapped = scene.ClusterCount;
            return true;
        }

        public bool EnsureInitialized(ComputeShader cullingShader, IReadOnlyList<NaniteRuntimeProxy> proxies)
        {
            if (cullingShader == null || proxies == null || proxies.Count == 0)
                return false;

            int currentRevision = NaniteRuntimeRegistry.Revision;
            if (shader == cullingShader &&
                registryRevision == currentRevision &&
                IsReady)
            {
                UpdateInstanceTransforms(proxies);
                return true;
            }

            int signature = ComputeRebuildSignature(proxies);
            if (shader != cullingShader)
            {
                shader = cullingShader;
                if (!TryFindKernels())
                {
                    Dispose();
                    return false;
                }
                rebuildSignature = -1;
            }

            if (signature != rebuildSignature)
            {
                if (!RebuildMergedData(proxies))
                {
                    Dispose();
                    return false;
                }

                rebuildSignature = signature;
            }
            else
            {
                UpdateInstanceTransforms(proxies);
            }

            registryRevision = currentRevision;
            return IsReady;
        }

        /// <summary>
        /// GPU-resident：直接写入 scene clusterVisible，无 GetData。
        /// clearMask=true 用于 FirstCull；false 用于 SecondCull（与 first 做 OR 合并）。
        /// </summary>
        public bool RunWriteClusterVisible(
            Camera camera,
            Texture hzbTexture,
            int hzbMipCount,
            bool useHzb,
            ComputeBuffer clusterVisible,
            int sceneClusterCount,
            bool clearMask)
        {
            return RunWriteClusterVisible(
                camera,
                hzbTexture,
                hzbMipCount,
                useHzb,
                clusterVisible,
                sceneClusterCount,
                clearMask,
                CullPassMode.Legacy,
                prevVisible: null,
                secondPassCandidates: null,
                pass2Drawn: null,
                hasPrevVisible: false,
                enableCullStats: false);
        }

        public bool RunWriteClusterVisible(
            Camera camera,
            Texture hzbTexture,
            int hzbMipCount,
            bool useHzb,
            ComputeBuffer clusterVisible,
            int sceneClusterCount,
            bool clearMask,
            CullPassMode cullPassMode,
            ComputeBuffer prevVisible,
            ComputeBuffer secondPassCandidates,
            ComputeBuffer pass2Drawn,
            bool hasPrevVisible,
            bool enableCullStats)
        {
            if (!SupportsGpuVisibleMask || camera == null || clusterVisible == null || sceneClusterCount <= 0)
                return false;
            if (clusterSceneIndexBuffer == null || sceneClusterCountMapped != sceneClusterCount)
                return false;

            if (!DispatchCullKernels(
                    camera,
                    hzbTexture,
                    hzbMipCount,
                    useHzb,
                    enableVisibleAppend: false,
                    enableClusterVisibleWrite: true,
                    clusterVisible,
                    sceneClusterCount,
                    clearMask,
                    allowCpuCandidates: PreferBvhCandidates,
                    cullPassMode,
                    prevVisible,
                    secondPassCandidates,
                    pass2Drawn,
                    hasPrevVisible,
                    enableCullStats,
                    out _,
                    out _))
                return false;

            LastClusterCount = clusterCount;
            LastClusterCandidateCount = LastUsedCpuCandidates ? clusterCandidateCount : clusterCount;
            if (enableCullStats)
                ReadbackCullStats();
            return true;
        }

        public bool DispatchCopyUintBuffer(ComputeBuffer src, ComputeBuffer dst, int count)
        {
            if (shader == null || kernelCopyUintBuffer < 0 || src == null || dst == null || count <= 0)
                return false;
            shader.SetInt("_SceneClusterCount", count);
            shader.SetBuffer(kernelCopyUintBuffer, "_UintCopySrc", src);
            shader.SetBuffer(kernelCopyUintBuffer, "_UintCopyDst", dst);
            int groups = (count + kThreadGroupSize - 1) / kThreadGroupSize;
            shader.Dispatch(kernelCopyUintBuffer, Mathf.Max(1, groups), 1, 1);
            return true;
        }

        public bool DispatchOrMasksToVisible(ComputeBuffer pass1, ComputeBuffer pass2, ComputeBuffer dstVisible, int count)
        {
            if (shader == null || kernelOrMasksToVisible < 0 || pass1 == null || pass2 == null || dstVisible == null || count <= 0)
                return false;
            shader.SetInt("_SceneClusterCount", count);
            shader.SetBuffer(kernelOrMasksToVisible, "_Pass1Visible", pass1);
            shader.SetBuffer(kernelOrMasksToVisible, "_OrMaskB", pass2);
            shader.SetBuffer(kernelOrMasksToVisible, "_ClusterVisible", dstVisible);
            int groups = (count + kThreadGroupSize - 1) / kThreadGroupSize;
            shader.Dispatch(kernelOrMasksToVisible, Mathf.Max(1, groups), 1, 1);
            return true;
        }

        void ReadbackCullStats()
        {
            if (cullStatsBuffer == null)
                return;
            cullStatsBuffer.GetData(cullStatsCpu);
            LastCull1Drawn = (int)cullStatsCpu[0];
            LastCull2Candidates = (int)cullStatsCpu[1];
            LastCull2Drawn = (int)cullStatsCpu[2];
        }

        public bool Run(
            Camera camera,
            Texture hzbTexture,
            int hzbMipCount,
            bool useHzb,
            Dictionary<int, NaniteRuntimeSelection> outputsByProxyId)
        {
            if (!IsReady || camera == null || outputsByProxyId == null)
                return false;

            foreach (var kv in outputsByProxyId)
                kv.Value.Clear();
            for (int s = 0; s < slots.Count; s++)
                GetVisibleScratch(slots[s].proxyId).Clear();

            if (!DispatchCullKernels(
                    camera,
                    hzbTexture,
                    hzbMipCount,
                    useHzb,
                    enableVisibleAppend: true,
                    enableClusterVisibleWrite: false,
                    clusterVisible: null,
                    sceneClusterCount: 0,
                    clearMask: false,
                    allowCpuCandidates: true,
                    CullPassMode.Legacy,
                    prevVisible: null,
                    secondPassCandidates: null,
                    pass2Drawn: null,
                    hasPrevVisible: false,
                    enableCullStats: false,
                    out bool hasCpuCandidates,
                    out int cpuTestedNodes))
                return false;

            ComputeBuffer.CopyCount(visibleClusterAppendBuffer, visibleCountBuffer, 0);
            visibleCountBuffer.GetData(visibleCountCpu);
            int visibleCount = (int)visibleCountCpu[0];
            if (visibleCount <= 0)
                return true;

            if (visibleRefsScratch == null || visibleRefsScratch.Length < visibleCount)
                visibleRefsScratch = new GpuVisibleRef[Mathf.NextPowerOfTwo(Mathf.Max(64, visibleCount))];
            visibleClusterAppendBuffer.GetData(visibleRefsScratch, 0, 0, visibleCount);

            for (int i = 0; i < visibleCount; i++)
            {
                int inst = (int)visibleRefsScratch[i].instanceIndex;
                if (inst < 0 || inst >= slots.Count)
                    continue;

                BatchSlot slot = slots[inst];
                if (!outputsByProxyId.TryGetValue(slot.proxyId, out _))
                    continue;

                var visible = GetVisibleScratch(slot.proxyId);
                visible.Add(new NaniteVisibleClusterRef
                {
                    pageIndex = (int)visibleRefsScratch[i].pageIndex,
                    clusterIndex = (int)visibleRefsScratch[i].clusterIndex
                });
            }

            for (int s = 0; s < slots.Count; s++)
            {
                BatchSlot slot = slots[s];
                if (!outputsByProxyId.TryGetValue(slot.proxyId, out var selection))
                    continue;

                var visible = GetVisibleScratch(slot.proxyId);
                var stats = new NaniteCullingStats
                {
                    testedInstances = 1,
                    testedNodes = hasCpuCandidates ? cpuTestedNodes / Mathf.Max(1, slots.Count) : 0,
                    testedParts = slot.partCount,
                    testedClusters = slot.clusterCount,
                    visibleClusters = visible.Count
                };

                selection.instanceLocalToWorld = slot.proxy != null ? slot.proxy.transform.localToWorldMatrix : Matrix4x4.identity;
                NaniteRuntimeCulling.BuildSelectionFromVisibleClusters(slot.mesh, visible, stats, selection);
                visible.Clear();
            }

            return true;
        }

        bool DispatchCullKernels(
            Camera camera,
            Texture hzbTexture,
            int hzbMipCount,
            bool useHzb,
            bool enableVisibleAppend,
            bool enableClusterVisibleWrite,
            ComputeBuffer clusterVisible,
            int sceneClusterCount,
            bool clearMask,
            bool allowCpuCandidates,
            CullPassMode cullPassMode,
            ComputeBuffer prevVisible,
            ComputeBuffer secondPassCandidates,
            ComputeBuffer pass2Drawn,
            bool hasPrevVisible,
            bool enableCullStats,
            out bool hasCpuCandidates,
            out int cpuTestedNodes)
        {
            using var profilerScope = kDispatchMarker.Auto();
            hasCpuCandidates = false;
            cpuTestedNodes = 0;

            GeometryUtility.CalculateFrustumPlanes(camera.cullingMatrix, frustumPlaneObjects);
            for (int i = 0; i < 6; i++)
            {
                var p = frustumPlaneObjects[i];
                frustumPlanes[i] = new Vector4(p.normal.x, p.normal.y, p.normal.z, p.distance);
            }

            Vector3 cameraPos = camera.transform.position;
            // 与 HZB/深度 RT 一致的 GPU 投影，避免 IsOccludedByHzb 系统性误杀。
            Matrix4x4 gpuProj = GL.GetGPUProjectionMatrix(camera.projectionMatrix, true);
            float projectionScale = Mathf.Abs(gpuProj.m11) * 0.5f * Mathf.Max(1, camera.pixelHeight);
            float zNear = Mathf.Max(1e-3f, camera.nearClipPlane);
            Matrix4x4 worldToClip = gpuProj * camera.worldToCameraMatrix;
            // Pass1 强制不测 HZB；Pass2/Legacy 按开关。
            bool enableHzb = cullPassMode != CullPassMode.Pass1PrevVisible &&
                             useHzb &&
                             hzbTexture != null &&
                             hzbMipCount > 0;
            SetSharedParams(
                cameraPos,
                projectionScale,
                zNear,
                worldToClip,
                camera.pixelWidth,
                camera.pixelHeight,
                enableHzb);
            shader.SetInt("_UseGpuSceneRefs", 1);
            shader.SetInt("_HizMipCount", enableHzb ? Mathf.Max(1, hzbMipCount) : 1);
            Texture boundHzbTexture = enableHzb ? hzbTexture : GetFallbackHzbTexture();

            if (allowCpuCandidates)
            {
                hasCpuCandidates = BuildCpuClusterCandidates(
                    frustumPlaneObjects,
                    cameraPos,
                    projectionScale,
                    zNear,
                    out cpuTestedNodes,
                    out _,
                    out clusterCandidateCount);
            }
            else
            {
                clusterCandidateCount = 0;
            }

            ComputeBuffer visibleTarget = clusterVisible != null ? clusterVisible : GetFallbackClusterVisibleBuffer();
            int visibleCountParam = clusterVisible != null ? Mathf.Max(1, sceneClusterCount) : 1;
            ComputeBuffer sceneIndexBuf = clusterSceneIndexBuffer != null ? clusterSceneIndexBuffer : GetFallbackClusterVisibleBuffer();
            ComputeBuffer prevBuf = prevVisible != null ? prevVisible : GetFallbackPrevVisibleBuffer();
            ComputeBuffer secondBuf = secondPassCandidates != null ? secondPassCandidates : GetFallbackSecondPassBuffer();
            ComputeBuffer pass2Buf = pass2Drawn != null ? pass2Drawn : GetFallbackPass2DrawnBuffer();
            EnsureCullStatsBuffer();

            // 仅 Pass1/Legacy 清零；Pass2 追加 cull2Drawn，保留 cull1 计数。
            if (enableCullStats &&
                kernelClearCullStats >= 0 &&
                cullPassMode != CullPassMode.Pass2CandidatesHzb)
            {
                shader.SetBuffer(kernelClearCullStats, "_CullStats", cullStatsBuffer);
                shader.Dispatch(kernelClearCullStats, 1, 1, 1);
            }

            if (enableClusterVisibleWrite && clearMask && kernelClearClusterVisible >= 0)
            {
                shader.SetInt("_SceneClusterCount", visibleCountParam);
                shader.SetInt("_EnableVisibleAppend", 0);
                shader.SetInt("_EnableClusterVisibleWrite", 0);
                BindCullSharedBuffers(kernelClearClusterVisible, visibleTarget, sceneIndexBuf, prevBuf, secondBuf, pass2Buf);
                BindInstanceBuffers(kernelClearClusterVisible);
                BindHzbTexture(kernelClearClusterVisible, boundHzbTexture);
                int clearGroups = (visibleCountParam + kThreadGroupSize - 1) / kThreadGroupSize;
                shader.Dispatch(kernelClearClusterVisible, Mathf.Max(1, clearGroups), 1, 1);
            }

            if (enableClusterVisibleWrite &&
                cullPassMode == CullPassMode.Pass1PrevVisible &&
                kernelClearSecondPassCandidates >= 0)
            {
                shader.SetInt("_SceneClusterCount", visibleCountParam);
                BindCullSharedBuffers(kernelClearSecondPassCandidates, visibleTarget, sceneIndexBuf, prevBuf, secondBuf, pass2Buf);
                int clearGroups = (visibleCountParam + kThreadGroupSize - 1) / kThreadGroupSize;
                shader.Dispatch(kernelClearSecondPassCandidates, Mathf.Max(1, clearGroups), 1, 1);
            }

            bool usePartDriven = !hasCpuCandidates &&
                                 PreferPartDrivenClusterCull &&
                                 kernelClusterCullByPart >= 0;
            bool useVisiblePartQueue = usePartDriven &&
                                       kernelClusterCullVisibleParts >= 0 &&
                                       kernelFinalizeVisiblePartDispatch >= 0 &&
                                       partCount >= Mathf.Max(1, PartQueueMinVirtualParts);
            bool useVisibleInstanceQueue = useVisiblePartQueue &&
                                           kernelPartCullVisibleInstances >= 0 &&
                                           instanceCount >= Mathf.Max(1, InstanceQueueMinInstances);

            if (hasCpuCandidates)
            {
                partVisibleBuffer.SetData(partVisibleCpu);
                if (clusterCandidateCount > 0)
                    clusterCandidateBuffer.SetData(clusterCandidatesCpu, 0, 0, clusterCandidateCount);
            }
            else
            {
                if (useVisiblePartQueue)
                    visiblePartAppendBuffer.SetCounterValue(0);
                if (useVisibleInstanceQueue)
                    visibleInstanceAppendBuffer.SetCounterValue(0);

                shader.SetInt("_CullPassMode", (int)cullPassMode);
                shader.SetInt("_UseInstanceCull", 1);
                shader.SetInt("_EnableVisibleInstanceQueue", useVisibleInstanceQueue ? 1 : 0);
                shader.SetBuffer(kernelInstanceCull, "_VisibleInstancesOut", visibleInstanceAppendBuffer);
                BindInstanceBuffers(kernelInstanceCull);
                BindHzbTexture(kernelInstanceCull, boundHzbTexture);
                int instanceGroups = (instanceCount + kThreadGroupSize - 1) / kThreadGroupSize;
                shader.Dispatch(kernelInstanceCull, Mathf.Max(1, instanceGroups), 1, 1);

                if (useVisibleInstanceQueue)
                    ComputeBuffer.CopyCount(
                        visibleInstanceAppendBuffer,
                        visibleInstancePartDispatchArgsBuffer,
                        0);

                int activePartKernel = useVisibleInstanceQueue
                    ? kernelPartCullVisibleInstances
                    : kernelPartCull;
                shader.SetInt("_PartCount", partCount);
                shader.SetInt("_SceneClusterCount", visibleCountParam);
                shader.SetInt("_CullPassMode", (int)cullPassMode);
                shader.SetInt("_HasPrevVisible", hasPrevVisible ? 1 : 0);
                shader.SetInt("_EnableVisibleAppend", 0);
                shader.SetInt("_EnableClusterVisibleWrite", 0);
                shader.SetInt("_EnableVisiblePartQueue", useVisiblePartQueue ? 1 : 0);
                shader.SetInt("_UseInstanceCull", useVisibleInstanceQueue ? 0 : 1);
                shader.SetBuffer(activePartKernel, "_Parts", partsBuffer);
                shader.SetBuffer(activePartKernel, "_PartVisible", partVisibleBuffer);
                shader.SetBuffer(activePartKernel, "_VisiblePartsOut", visiblePartAppendBuffer);
                if (useVisibleInstanceQueue)
                {
                    shader.SetBuffer(activePartKernel, "_VisibleInstancesIn", visibleInstanceAppendBuffer);
                    shader.SetBuffer(
                        activePartKernel,
                        "_VisibleInstancePartDispatchArgs",
                        visibleInstancePartDispatchArgsBuffer);
                }
                BindGpuSceneRefs(activePartKernel);
                BindCullSharedBuffers(activePartKernel, visibleTarget, sceneIndexBuf, prevBuf, secondBuf, pass2Buf);
                BindInstanceBuffers(activePartKernel);
                BindHzbTexture(activePartKernel, boundHzbTexture);

                if (useVisibleInstanceQueue)
                {
                    shader.DispatchIndirect(activePartKernel, visibleInstancePartDispatchArgsBuffer, 0);
                }
                else
                {
                    int partGroups = (partCount + kThreadGroupSize - 1) / kThreadGroupSize;
                    shader.Dispatch(activePartKernel, partGroups, 1, 1);
                }

                if (useVisiblePartQueue)
                {
                    ComputeBuffer.CopyCount(visiblePartAppendBuffer, visiblePartDispatchArgsBuffer, 0);
                    shader.SetBuffer(
                        kernelFinalizeVisiblePartDispatch,
                        "_VisiblePartDispatchArgs",
                        visiblePartDispatchArgsBuffer);
                    shader.Dispatch(kernelFinalizeVisiblePartDispatch, 1, 1, 1);
                }
            }

            visibleClusterAppendBuffer.SetCounterValue(0);
            bool enableVisibleDrawQueue = enableClusterVisibleWrite &&
                                          sceneClusterFirstTriBuffer != null &&
                                          sceneClusterTriCountBuffer != null;
            if (enableVisibleDrawQueue && clearMask)
                visibleDrawClusterAppendBuffer.SetCounterValue(0);
            int activeClusterKernel = useVisiblePartQueue
                ? kernelClusterCullVisibleParts
                : (usePartDriven ? kernelClusterCullByPart : kernelClusterCull);
            shader.SetInt("_PartCount", partCount);
            shader.SetInt("_ClusterCount", clusterCount);
            shader.SetInt("_ClusterCandidateCount", hasCpuCandidates ? clusterCandidateCount : 0);
            shader.SetInt("_UseClusterCandidates", hasCpuCandidates ? 1 : 0);
            shader.SetInt("_UseInstanceCull", hasCpuCandidates ? 0 : 1);
            shader.SetInt("_EnableVisibleAppend", enableVisibleAppend ? 1 : 0);
            shader.SetInt("_EnableVisibleDrawAppend", enableVisibleDrawQueue ? 1 : 0);
            shader.SetInt("_EnableClusterVisibleWrite", enableClusterVisibleWrite ? 1 : 0);
            shader.SetInt("_SceneClusterCount", visibleCountParam);
            shader.SetInt("_CullPassMode", (int)cullPassMode);
            shader.SetInt("_HasPrevVisible", hasPrevVisible ? 1 : 0);
            shader.SetInt("_EnableCullStats", enableCullStats ? 1 : 0);
            shader.SetBuffer(activeClusterKernel, "_Clusters", clustersBuffer);
            shader.SetBuffer(activeClusterKernel, "_PartVisible", partVisibleBuffer);
            shader.SetBuffer(activeClusterKernel, "_VisibleClusters", visibleClusterAppendBuffer);
            shader.SetBuffer(activeClusterKernel, "_ClusterCandidates", clusterCandidateBuffer);
            shader.SetBuffer(activeClusterKernel, "_CullStats", cullStatsBuffer);
            shader.SetBuffer(activeClusterKernel, "_VisibleDrawClusters", visibleDrawClusterAppendBuffer);
            ComputeBuffer fallbackSceneClusterData = GetFallbackClusterVisibleBuffer();
            shader.SetBuffer(
                activeClusterKernel,
                "_SceneClusterFirstTri",
                sceneClusterFirstTriBuffer != null ? sceneClusterFirstTriBuffer : fallbackSceneClusterData);
            shader.SetBuffer(
                activeClusterKernel,
                "_SceneClusterTriCount",
                sceneClusterTriCountBuffer != null ? sceneClusterTriCountBuffer : fallbackSceneClusterData);
            if (useVisiblePartQueue)
            {
                shader.SetBuffer(activeClusterKernel, "_VisiblePartsIn", visiblePartAppendBuffer);
                shader.SetBuffer(activeClusterKernel, "_VisiblePartDispatchArgs", visiblePartDispatchArgsBuffer);
            }
            BindGpuSceneRefs(activeClusterKernel);
            BindPageStreamingBuffers(activeClusterKernel);
            BindCullSharedBuffers(activeClusterKernel, visibleTarget, sceneIndexBuf, prevBuf, secondBuf, pass2Buf);
            BindInstanceBuffers(activeClusterKernel);
            BindHzbTexture(activeClusterKernel, boundHzbTexture);

            int clusterWorkCount = usePartDriven
                ? partCount
                : (hasCpuCandidates ? clusterCandidateCount : clusterCount);
            if (useVisiblePartQueue)
            {
                shader.DispatchIndirect(activeClusterKernel, visiblePartDispatchArgsBuffer, 0);
            }
            else if (clusterWorkCount > 0)
            {
                int clusterGroups = (clusterWorkCount + kThreadGroupSize - 1) / kThreadGroupSize;
                shader.Dispatch(activeClusterKernel, clusterGroups, 1, 1);
            }

            if (enableVisibleDrawQueue)
            {
                ComputeBuffer.CopyCount(visibleDrawClusterAppendBuffer, visibleDrawCountArgsBuffer, 0);
                lastVisibleDrawQueueFrame = Time.frameCount;
                lastVisibleDrawCameraId = camera.GetInstanceID();
            }
            else
            {
                lastVisibleDrawQueueFrame = -1;
                lastVisibleDrawCameraId = 0;
            }

            LastUsedCpuCandidates = hasCpuCandidates;
            LastUsedPartDrivenClusterCull = usePartDriven;
            LastUsedVisiblePartQueue = useVisiblePartQueue;
            LastUsedVisibleInstanceQueue = useVisibleInstanceQueue;

            return true;
        }

        void BindGpuSceneRefs(int kernel)
        {
            shader.SetBuffer(kernel, "_VirtualParts", virtualPartsBuffer);
            shader.SetBuffer(kernel, "_VirtualClusters", virtualClustersBuffer);
        }

        void BindPageStreamingBuffers(int kernel)
        {
            ComputeBuffer fallback = GetFallbackClusterVisibleBuffer();
            bool enabled =
                scenePageCount > 0 &&
                scenePageResidencyBuffer != null &&
                scenePageRequestBuffer != null;
            shader.SetInt("_PageCount", enabled ? scenePageCount : 0);
            shader.SetInt("_EnablePageRequests", enabled ? 1 : 0);
            shader.SetInt("_TrackPageUsage", enabled && sceneTrackPageUsage ? 1 : 0);
            shader.SetBuffer(
                kernel,
                "_PageResidency",
                enabled ? scenePageResidencyBuffer : fallback);
            shader.SetBuffer(
                kernel,
                "_PageRequests",
                enabled ? scenePageRequestBuffer : fallback);
        }

        void BindCullSharedBuffers(
            int kernel,
            ComputeBuffer clusterVisible,
            ComputeBuffer clusterSceneIndex,
            ComputeBuffer prevVisible,
            ComputeBuffer secondPassCandidates,
            ComputeBuffer pass2Drawn)
        {
            shader.SetBuffer(kernel, "_ClusterVisible", clusterVisible);
            shader.SetBuffer(kernel, "_ClusterSceneIndex", clusterSceneIndex);
            shader.SetBuffer(kernel, "_PrevClusterVisible", prevVisible);
            shader.SetBuffer(kernel, "_SecondPassCandidates", secondPassCandidates);
            shader.SetBuffer(kernel, "_Pass2Drawn", pass2Drawn);
            shader.SetBuffer(kernel, "_VisibleClusters", visibleClusterAppendBuffer);
            shader.SetBuffer(kernel, "_ClusterCandidates", clusterCandidateBuffer);
            shader.SetBuffer(kernel, "_PartVisible", partVisibleBuffer);
            if (cullStatsBuffer != null)
                shader.SetBuffer(kernel, "_CullStats", cullStatsBuffer);
            if (partsBuffer != null)
                shader.SetBuffer(kernel, "_Parts", partsBuffer);
            if (clustersBuffer != null)
                shader.SetBuffer(kernel, "_Clusters", clustersBuffer);
        }

        ComputeBuffer GetFallbackClusterVisibleBuffer()
        {
            if (fallbackClusterVisibleBuffer == null)
            {
                fallbackClusterVisibleBuffer = new ComputeBuffer(1, sizeof(uint), ComputeBufferType.Structured);
                fallbackClusterVisibleBuffer.SetData(new uint[] { 0u });
            }

            return fallbackClusterVisibleBuffer;
        }

        ComputeBuffer GetFallbackPrevVisibleBuffer()
        {
            if (fallbackPrevVisibleBuffer == null)
            {
                fallbackPrevVisibleBuffer = new ComputeBuffer(1, sizeof(uint), ComputeBufferType.Structured);
                fallbackPrevVisibleBuffer.SetData(new uint[] { 0u });
            }

            return fallbackPrevVisibleBuffer;
        }

        ComputeBuffer GetFallbackSecondPassBuffer()
        {
            if (fallbackSecondPassBuffer == null)
            {
                fallbackSecondPassBuffer = new ComputeBuffer(1, sizeof(uint), ComputeBufferType.Structured);
                fallbackSecondPassBuffer.SetData(new uint[] { 0u });
            }

            return fallbackSecondPassBuffer;
        }

        ComputeBuffer GetFallbackPass2DrawnBuffer()
        {
            if (fallbackPass2DrawnBuffer == null)
            {
                fallbackPass2DrawnBuffer = new ComputeBuffer(1, sizeof(uint), ComputeBufferType.Structured);
                fallbackPass2DrawnBuffer.SetData(new uint[] { 0u });
            }

            return fallbackPass2DrawnBuffer;
        }

        void EnsureCullStatsBuffer()
        {
            if (cullStatsBuffer != null)
                return;
            cullStatsBuffer = new ComputeBuffer(3, sizeof(uint), ComputeBufferType.Structured);
            cullStatsBuffer.SetData(new uint[] { 0u, 0u, 0u });
        }

        List<NaniteVisibleClusterRef> GetVisibleScratch(int proxyId)
        {
            if (!visibleScratch.TryGetValue(proxyId, out var list))
            {
                list = new List<NaniteVisibleClusterRef>(4096);
                visibleScratch[proxyId] = list;
            }

            return list;
        }

        void UpdateInstanceTransforms(IReadOnlyList<NaniteRuntimeProxy> proxies)
        {
            if (slots.Count == 0)
                return;
            if (lastTransformUpdateFrame == Time.frameCount)
                return;
            lastTransformUpdateFrame = Time.frameCount;

            bool dirty = false;
            for (int s = 0; s < slots.Count; s++)
            {
                var proxy = slots[s].proxy;
                if (proxy == null || !proxy.isActiveAndEnabled || !proxy.NaniteRenderingActive)
                    continue;

                Matrix4x4 m = proxy.transform.localToWorldMatrix;
                float maxScale = Mathf.Max(
                    Mathf.Abs(proxy.transform.lossyScale.x),
                    Mathf.Abs(proxy.transform.lossyScale.y),
                    Mathf.Abs(proxy.transform.lossyScale.z));
                float lodErr = LodErrorPixelsOverride > 0f ? LodErrorPixelsOverride : proxy.lodErrorPixels;
                var current = instanceData[s];
                if (current.localToWorld != m ||
                    !Mathf.Approximately(current.maxScale, maxScale) ||
                    !Mathf.Approximately(current.lodErrorPixels, lodErr))
                {
                    current.localToWorld = m;
                    current.maxScale = maxScale;
                    current.lodErrorPixels = lodErr;
                    instanceData[s] = current;
                    dirty = true;
                }
            }

            if (!dirty)
                return;

            instanceDataBuffer.SetData(instanceData, 0, 0, slots.Count);
        }

        bool RebuildMergedData(IReadOnlyList<NaniteRuntimeProxy> proxies)
        {
            ReleaseBuffers();
            slots.Clear();
            geometrySlots.Clear();
            geometryIndexByMeshId.Clear();
            mergedParts.Clear();
            mergedClusters.Clear();
            virtualParts.Clear();
            virtualClusters.Clear();
            instanceData.Clear();

            for (int i = 0; i < proxies.Count; i++)
            {
                var proxy = proxies[i];
                if (!IsBatchableProxy(proxy))
                    continue;

                NaniteMesh mesh = proxy.naniteMesh;
                int meshId = mesh.GetInstanceID();
                if (!geometryIndexByMeshId.TryGetValue(meshId, out int geometryIndex))
                {
                    int geometryPartOffset = mergedParts.Count;
                    int geometryClusterOffset = mergedClusters.Count;
                    NaniteGpuCullingBackend.BuildGpuData(
                        mesh,
                        0,
                        mergedParts,
                        mergedClusters,
                        out var geometryPagePartBase,
                        out var geometryPagePartCount);

                    int geometryParts = mergedParts.Count - geometryPartOffset;
                    int geometryClusters = mergedClusters.Count - geometryClusterOffset;
                    if (geometryParts <= 0 || geometryClusters <= 0)
                    {
                        if (geometryParts > 0)
                            mergedParts.RemoveRange(geometryPartOffset, geometryParts);
                        if (geometryClusters > 0)
                            mergedClusters.RemoveRange(geometryClusterOffset, geometryClusters);
                        continue;
                    }

                    var geometry = new GeometrySlot
                    {
                        mesh = mesh,
                        partOffset = geometryPartOffset,
                        clusterOffset = geometryClusterOffset,
                        partCount = geometryParts,
                        clusterCount = geometryClusters,
                        pagePartBase = geometryPagePartBase,
                        pagePartCount = geometryPagePartCount,
                        bounds = ResolveMeshBounds(mesh, geometryPartOffset, geometryParts)
                    };
                    geometryIndex = geometrySlots.Count;
                    geometrySlots.Add(geometry);
                    geometryIndexByMeshId.Add(meshId, geometryIndex);
                }

                GeometrySlot geometrySlot = geometrySlots[geometryIndex];
                int instanceIndex = slots.Count;
                int virtualPartOffset = virtualParts.Count;
                int virtualClusterOffset = virtualClusters.Count;

                for (int localCluster = 0; localCluster < geometrySlot.clusterCount; localCluster++)
                {
                    int geometryClusterIndex = geometrySlot.clusterOffset + localCluster;
                    int geometryPartIndex = mergedClusters[geometryClusterIndex].partIndex;
                    int localPart = geometryPartIndex - geometrySlot.partOffset;
                    uint virtualPartIndex = (uint)localPart < (uint)geometrySlot.partCount
                        ? (uint)(virtualPartOffset + localPart)
                        : uint.MaxValue;
                    virtualClusters.Add(new GpuVirtualClusterRef
                    {
                        instanceIndex = (uint)instanceIndex,
                        clusterIndex = (uint)geometryClusterIndex,
                        partIndex = virtualPartIndex,
                        reserved = 0u
                    });
                }

                for (int localPart = 0; localPart < geometrySlot.partCount; localPart++)
                {
                    int geometryPartIndex = geometrySlot.partOffset + localPart;
                    var part = mergedParts[geometryPartIndex];
                    int localClusterStart = part.clusterStart - geometrySlot.clusterOffset;
                    int safeClusterStart = Mathf.Clamp(localClusterStart, 0, geometrySlot.clusterCount);
                    int safeClusterCount = Mathf.Clamp(
                        part.clusterCount,
                        0,
                        geometrySlot.clusterCount - safeClusterStart);
                    virtualParts.Add(new GpuVirtualPartRef
                    {
                        instanceIndex = (uint)instanceIndex,
                        partIndex = (uint)geometryPartIndex,
                        clusterStart = (uint)(virtualClusterOffset + safeClusterStart),
                        clusterCount = (uint)safeClusterCount
                    });
                }

                int[] pagePartBase = new int[geometrySlot.pagePartBase.Length];
                for (int page = 0; page < pagePartBase.Length; page++)
                {
                    int geometryPageBase = geometrySlot.pagePartBase[page];
                    pagePartBase[page] = geometryPageBase >= geometrySlot.partOffset
                        ? virtualPartOffset + (geometryPageBase - geometrySlot.partOffset)
                        : -1;
                }

                slots.Add(new BatchSlot
                {
                    proxy = proxy,
                    proxyId = proxy.GetInstanceID(),
                    mesh = mesh,
                    geometryIndex = geometryIndex,
                    virtualPartOffset = virtualPartOffset,
                    virtualClusterOffset = virtualClusterOffset,
                    partCount = geometrySlot.partCount,
                    clusterCount = geometrySlot.clusterCount,
                    pagePartBase = pagePartBase,
                    pagePartCount = geometrySlot.pagePartCount
                });

                instanceData.Add(new NaniteGpuCullingBackend.GpuInstanceData
                {
                    localToWorld = proxy.transform.localToWorldMatrix,
                    bounds = geometrySlot.bounds,
                    maxScale = Mathf.Max(
                        Mathf.Abs(proxy.transform.lossyScale.x),
                        Mathf.Abs(proxy.transform.lossyScale.y),
                        Mathf.Abs(proxy.transform.lossyScale.z)),
                    lodErrorPixels = LodErrorPixelsOverride > 0f ? LodErrorPixelsOverride : proxy.lodErrorPixels,
                    partOffset = (uint)virtualPartOffset,
                    partCount = (uint)geometrySlot.partCount
                });
            }

            geometryPartCount = mergedParts.Count;
            geometryClusterCount = mergedClusters.Count;
            partCount = virtualParts.Count;
            clusterCount = virtualClusters.Count;
            instanceCount = slots.Count;
            if (geometryPartCount == 0 || geometryClusterCount == 0 ||
                partCount == 0 || clusterCount == 0 || instanceCount == 0)
                return false;

            partDataCpu = mergedParts.ToArray();
            clusterDataCpu = mergedClusters.ToArray();
            virtualPartRefsCpu = virtualParts.ToArray();
            virtualClusterRefsCpu = virtualClusters.ToArray();
            partVisibleCpu = new uint[partCount];
            clusterCandidatesCpu = new uint[clusterCount];
            clusterSceneIndexCpu = null;
            sceneIndexSignature = 0;
            sceneClusterCountMapped = 0;

            partsBuffer = new ComputeBuffer(geometryPartCount, Marshal.SizeOf<NaniteGpuCullingBackend.GpuPartData>());
            clustersBuffer = new ComputeBuffer(geometryClusterCount, Marshal.SizeOf<NaniteGpuCullingBackend.GpuClusterData>());
            virtualPartsBuffer = new ComputeBuffer(partCount, Marshal.SizeOf<GpuVirtualPartRef>());
            virtualClustersBuffer = new ComputeBuffer(clusterCount, Marshal.SizeOf<GpuVirtualClusterRef>());
            visiblePartAppendBuffer = new ComputeBuffer(partCount, sizeof(uint), ComputeBufferType.Append);
            visiblePartDispatchArgsBuffer = new ComputeBuffer(4, sizeof(uint), ComputeBufferType.IndirectArguments);
            visibleInstanceAppendBuffer = new ComputeBuffer(instanceCount, sizeof(uint), ComputeBufferType.Append);
            visibleInstancePartDispatchArgsBuffer = new ComputeBuffer(4, sizeof(uint), ComputeBufferType.IndirectArguments);
            visibleDrawClusterAppendBuffer = new ComputeBuffer(clusterCount, sizeof(uint) * 3, ComputeBufferType.Append);
            visibleDrawCountArgsBuffer = new ComputeBuffer(4, sizeof(uint), ComputeBufferType.IndirectArguments);
            for (int cascadeIndex = 0; cascadeIndex < kMaxShadowCascades; cascadeIndex++)
            {
                shadowDrawClusterAppendBuffers[cascadeIndex] =
                    new ComputeBuffer(clusterCount, sizeof(uint) * 3, ComputeBufferType.Append);
                shadowDrawCountArgsBuffers[cascadeIndex] =
                    new ComputeBuffer(4, sizeof(uint), ComputeBufferType.IndirectArguments);
                shadowDrawArgsBuffers[cascadeIndex] = new GraphicsBuffer(
                    GraphicsBuffer.Target.IndirectArguments | GraphicsBuffer.Target.Structured,
                    4,
                    sizeof(uint));
            }
            partVisibleBuffer = new ComputeBuffer(partCount, sizeof(uint));
            visibleClusterAppendBuffer = new ComputeBuffer(clusterCount, Marshal.SizeOf<GpuVisibleRef>(), ComputeBufferType.Append);
            visibleCountBuffer = new ComputeBuffer(1, sizeof(uint), ComputeBufferType.Raw);
            clusterCandidateBuffer = new ComputeBuffer(clusterCount, sizeof(uint));
            instanceDataBuffer = new ComputeBuffer(instanceCount, Marshal.SizeOf<NaniteGpuCullingBackend.GpuInstanceData>());
            instanceVisibleBuffer = new ComputeBuffer(instanceCount, sizeof(uint));

            partsBuffer.SetData(partDataCpu);
            clustersBuffer.SetData(clusterDataCpu);
            virtualPartsBuffer.SetData(virtualPartRefsCpu);
            virtualClustersBuffer.SetData(virtualClusterRefsCpu);
            visiblePartAppendBuffer.SetCounterValue(0);
            visiblePartDispatchArgsBuffer.SetData(new uint[] { 0u, 1u, 1u, 0u });
            visibleInstanceAppendBuffer.SetCounterValue(0);
            visibleInstancePartDispatchArgsBuffer.SetData(new uint[] { 0u, 1u, 1u, 0u });
            visibleDrawClusterAppendBuffer.SetCounterValue(0);
            visibleDrawCountArgsBuffer.SetData(new uint[] { 0u, 1u, 1u, 0u });
            for (int cascadeIndex = 0; cascadeIndex < kMaxShadowCascades; cascadeIndex++)
            {
                shadowDrawClusterAppendBuffers[cascadeIndex].SetCounterValue(0);
                shadowDrawCountArgsBuffers[cascadeIndex].SetData(new uint[] { 0u, 1u, 1u, 0u });
                shadowDrawArgsBuffers[cascadeIndex].SetData(new uint[] { 0u, 1u, 0u, 0u });
            }
            visibleClusterAppendBuffer.SetCounterValue(0);
            instanceDataBuffer.SetData(instanceData);
            instanceVisibleBuffer.SetData(new uint[instanceCount]);
            lastTransformUpdateFrame = Time.frameCount;
            lastShadowDrawQueueFrame = -1;
            lastShadowDrawCameraId = 0;
            lastShadowDrawCascadeMask = 0;
            return true;
        }

        Vector4 ResolveMeshBounds(NaniteMesh mesh, int partOffset, int partCountInMesh)
        {
            Vector4 sphere = mesh != null ? mesh.boundingSphere : Vector4.zero;
            if (sphere.w > 0f && !float.IsNaN(sphere.w) && !float.IsInfinity(sphere.w))
                return sphere;

            if (partCountInMesh <= 0 || partOffset < 0 || partOffset >= mergedParts.Count)
                return new Vector4(0f, 0f, 0f, 1e30f);

            Vector3 min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            Vector3 max = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
            int end = Mathf.Min(mergedParts.Count, partOffset + partCountInMesh);
            for (int i = partOffset; i < end; i++)
            {
                Vector4 p = mergedParts[i].selfSphere;
                Vector3 r = Vector3.one * Mathf.Max(0f, p.w);
                Vector3 c = new Vector3(p.x, p.y, p.z);
                min = Vector3.Min(min, c - r);
                max = Vector3.Max(max, c + r);
            }

            Vector3 center = (min + max) * 0.5f;
            float radius = (max - center).magnitude;
            return new Vector4(center.x, center.y, center.z, Mathf.Max(radius, 1e-5f));
        }

        static bool IsBatchableProxy(NaniteRuntimeProxy proxy)
        {
            // Feature 提供全局 culling shader 时可批；不再强制 proxy.useGpuCulling（默认 false 会导致整帧 GPU cull 永不启用）。
            return proxy != null &&
                   proxy.isActiveAndEnabled &&
                   proxy.NaniteRenderingActive &&
                   proxy.naniteMesh != null;
        }

        public bool RecordShadowCull(
            UnsafeCommandBuffer cmd,
            Camera camera,
            int cascadeIndex,
            Matrix4x4 shadowViewMatrix,
            Matrix4x4 shadowProjectionMatrix,
            int shadowResolution)
        {
            if (cmd == null || camera == null || !SupportsGpuVisibleMask ||
                kernelInstanceCull < 0 || kernelPartCull < 0 || kernelClusterCullByPart < 0 ||
                cascadeIndex < 0 || cascadeIndex >= kMaxShadowCascades)
                return false;

            ComputeBuffer drawQueue = shadowDrawClusterAppendBuffers[cascadeIndex];
            ComputeBuffer countArgs = shadowDrawCountArgsBuffers[cascadeIndex];
            GraphicsBuffer drawArgs = shadowDrawArgsBuffers[cascadeIndex];
            if (drawQueue == null || countArgs == null || drawArgs == null ||
                clusterSceneIndexBuffer == null || sceneClusterCountMapped <= 0)
                return false;

            bool useVisiblePartQueue =
                kernelClusterCullVisibleParts >= 0 &&
                kernelFinalizeVisiblePartDispatch >= 0 &&
                partCount >= Mathf.Max(1, ShadowPartQueueMinVirtualParts);
            bool useVisibleInstanceQueue =
                useVisiblePartQueue &&
                kernelPartCullVisibleInstances >= 0 &&
                instanceCount >= Mathf.Max(1, ShadowInstanceQueueMinInstances);
            LastShadowUsedFusedBatch = false;
            LastShadowUsedVisiblePartQueue = useVisiblePartQueue;
            LastShadowUsedVisibleInstanceQueue = useVisibleInstanceQueue;

            GeometryUtility.CalculateFrustumPlanes(
                shadowProjectionMatrix * shadowViewMatrix,
                frustumPlaneObjects);
            for (int i = 0; i < 6; i++)
            {
                Plane plane = frustumPlaneObjects[i];
                frustumPlanes[i] = new Vector4(
                    plane.normal.x,
                    plane.normal.y,
                    plane.normal.z,
                    plane.distance);
            }

            float shadowProjectionScale = ComputeShadowProjectionScale(
                shadowProjectionMatrix,
                shadowResolution);
            Matrix4x4 shadowGpuProjection = GL.GetGPUProjectionMatrix(shadowProjectionMatrix, true);
            Matrix4x4 shadowWorldToClip = shadowGpuProjection * shadowViewMatrix;
            Vector3 cameraPosition = camera.transform.position;

            cmd.SetBufferCounterValue(drawQueue, 0u);
            if (useVisiblePartQueue)
                cmd.SetBufferCounterValue(visiblePartAppendBuffer, 0u);
            if (useVisibleInstanceQueue)
                cmd.SetBufferCounterValue(visibleInstanceAppendBuffer, 0u);
            SetShadowCullParams(
                cmd,
                cameraPosition,
                shadowProjectionScale,
                Mathf.Max(1e-3f, camera.nearClipPlane),
                shadowWorldToClip,
                Mathf.Max(1, shadowResolution),
                useVisiblePartQueue,
                useVisibleInstanceQueue);

            // Instance frustum cull. The append output is disabled for this path; its buffer is
            // still bound because Unity validates every resource declared by the kernel.
            BindShadowInstanceKernel(cmd, kernelInstanceCull);
            cmd.DispatchCompute(
                shader,
                kernelInstanceCull,
                Mathf.Max(1, (instanceCount + kThreadGroupSize - 1) / kThreadGroupSize),
                1,
                1);

            if (useVisibleInstanceQueue)
                cmd.CopyCounterValue(visibleInstanceAppendBuffer, visibleInstancePartDispatchArgsBuffer, 0u);

            // Part cull keeps the camera-space LOD error metric while replacing only the
            // visibility volume with the current light cascade frustum. Large instance sets
            // expand only the append queue produced above.
            int activePartKernel = useVisibleInstanceQueue
                ? kernelPartCullVisibleInstances
                : kernelPartCull;
            BindShadowPartKernel(cmd, activePartKernel);
            if (useVisibleInstanceQueue)
            {
                cmd.DispatchCompute(shader, activePartKernel, visibleInstancePartDispatchArgsBuffer, 0u);
            }
            else
            {
                cmd.DispatchCompute(
                    shader,
                    activePartKernel,
                    Mathf.Max(1, (partCount + kThreadGroupSize - 1) / kThreadGroupSize),
                    1,
                    1);
            }

            if (useVisiblePartQueue)
            {
                cmd.CopyCounterValue(visiblePartAppendBuffer, visiblePartDispatchArgsBuffer, 0u);
                cmd.SetComputeBufferParam(
                    shader,
                    kernelFinalizeVisiblePartDispatch,
                    "_VisiblePartDispatchArgs",
                    visiblePartDispatchArgsBuffer);
                cmd.DispatchCompute(shader, kernelFinalizeVisiblePartDispatch, 1, 1, 1);
            }

            // Expand only surviving parts and append (firstTriangle, triangleCount, instance).
            int activeClusterKernel = useVisiblePartQueue
                ? kernelClusterCullVisibleParts
                : kernelClusterCullByPart;
            BindShadowClusterKernel(cmd, activeClusterKernel, drawQueue);
            if (useVisiblePartQueue)
            {
                cmd.DispatchCompute(shader, activeClusterKernel, visiblePartDispatchArgsBuffer, 0u);
            }
            else
            {
                cmd.DispatchCompute(
                    shader,
                    activeClusterKernel,
                    Mathf.Max(1, (partCount + kThreadGroupSize - 1) / kThreadGroupSize),
                    1,
                    1);
            }
            cmd.CopyCounterValue(drawQueue, countArgs, 0u);

            int cameraId = camera.GetInstanceID();
            if (lastShadowDrawQueueFrame != Time.frameCount ||
                lastShadowDrawCameraId != cameraId)
            {
                lastShadowDrawQueueFrame = Time.frameCount;
                lastShadowDrawCameraId = cameraId;
                lastShadowDrawCascadeMask = 0;
            }
            lastShadowDrawCascadeMask |= 1 << cascadeIndex;
            return true;
        }

        public bool RecordShadowCullBatch(
            UnsafeCommandBuffer cmd,
            Camera camera,
            int cascadeCount,
            Matrix4x4[] shadowViewMatrices,
            Matrix4x4[] shadowProjectionMatrices,
            int[] shadowResolutions)
        {
            int activeCascadeCount = Mathf.Clamp(cascadeCount, 0, kMaxShadowCascades);
            if (cmd == null || camera == null || !SupportsGpuVisibleMask ||
                kernelShadowCullMultiCascadeByPart < 0 || activeCascadeCount <= 0 ||
                shadowViewMatrices == null || shadowProjectionMatrices == null || shadowResolutions == null ||
                shadowViewMatrices.Length < activeCascadeCount ||
                shadowProjectionMatrices.Length < activeCascadeCount ||
                shadowResolutions.Length < activeCascadeCount ||
                partCount >= Mathf.Max(1, ShadowPartQueueMinVirtualParts))
                return false;

            for (int cascadeIndex = 0; cascadeIndex < kMaxShadowCascades; cascadeIndex++)
            {
                ComputeBuffer queue = shadowDrawClusterAppendBuffers[cascadeIndex];
                if (queue == null || shadowDrawCountArgsBuffers[cascadeIndex] == null ||
                    shadowDrawArgsBuffers[cascadeIndex] == null)
                    return false;
                cmd.SetBufferCounterValue(queue, 0u);
            }

            shadowBatchProjectionScales = Vector4.zero;
            for (int cascadeIndex = 0; cascadeIndex < activeCascadeCount; cascadeIndex++)
            {
                GeometryUtility.CalculateFrustumPlanes(
                    shadowProjectionMatrices[cascadeIndex] * shadowViewMatrices[cascadeIndex],
                    frustumPlaneObjects);
                int planeBase = cascadeIndex * 6;
                for (int planeIndex = 0; planeIndex < 6; planeIndex++)
                {
                    Plane plane = frustumPlaneObjects[planeIndex];
                    shadowBatchFrustumPlanes[planeBase + planeIndex] = new Vector4(
                        plane.normal.x,
                        plane.normal.y,
                        plane.normal.z,
                        plane.distance);
                }

                float scale = ComputeShadowProjectionScale(
                    shadowProjectionMatrices[cascadeIndex],
                    shadowResolutions[cascadeIndex]);
                if (cascadeIndex == 0)
                    shadowBatchProjectionScales.x = scale;
                else if (cascadeIndex == 1)
                    shadowBatchProjectionScales.y = scale;
                else if (cascadeIndex == 2)
                    shadowBatchProjectionScales.z = scale;
                else
                    shadowBatchProjectionScales.w = scale;
            }

            Matrix4x4 cameraGpuProjection = GL.GetGPUProjectionMatrix(camera.projectionMatrix, true);
            float projectionScale = Mathf.Abs(cameraGpuProjection.m11) * 0.5f *
                                    Mathf.Max(1, camera.pixelHeight);
            Vector3 cameraPosition = camera.transform.position;
            int kernel = kernelShadowCullMultiCascadeByPart;
            cmd.SetComputeVectorParam(shader, "_CameraPos", new Vector4(
                cameraPosition.x,
                cameraPosition.y,
                cameraPosition.z,
                0f));
            cmd.SetComputeFloatParam(shader, "_ProjectionScale", projectionScale);
            cmd.SetComputeFloatParam(shader, "_ZNear", Mathf.Max(1e-3f, camera.nearClipPlane));
            cmd.SetComputeIntParam(shader, "_ShadowCascadeCount", activeCascadeCount);
            cmd.SetComputeVectorArrayParam(shader, "_ShadowFrustumPlanes", shadowBatchFrustumPlanes);
            cmd.SetComputeVectorParam(shader, "_ShadowProjectionScales", shadowBatchProjectionScales);
            cmd.SetComputeIntParam(shader, "_InstanceCount", instanceCount);
            cmd.SetComputeIntParam(shader, "_PartCount", partCount);
            cmd.SetComputeIntParam(shader, "_ClusterCount", clusterCount);
            cmd.SetComputeIntParam(shader, "_SceneClusterCount", sceneClusterCountMapped);
            cmd.SetComputeIntParam(shader, "_UseGpuSceneRefs", 1);

            bool pageRequestsEnabled =
                scenePageCount > 0 &&
                scenePageResidencyBuffer != null &&
                scenePageRequestBuffer != null;
            cmd.SetComputeIntParam(shader, "_PageCount", pageRequestsEnabled ? scenePageCount : 0);
            cmd.SetComputeIntParam(shader, "_EnablePageRequests", pageRequestsEnabled ? 1 : 0);
            cmd.SetComputeIntParam(
                shader,
                "_TrackPageUsage",
                pageRequestsEnabled && sceneTrackPageUsage ? 1 : 0);

            ComputeBuffer fallback = GetFallbackClusterVisibleBuffer();
            cmd.SetComputeBufferParam(shader, kernel, "_Parts", partsBuffer);
            cmd.SetComputeBufferParam(shader, kernel, "_Clusters", clustersBuffer);
            cmd.SetComputeBufferParam(shader, kernel, "_VirtualParts", virtualPartsBuffer);
            cmd.SetComputeBufferParam(shader, kernel, "_VirtualClusters", virtualClustersBuffer);
            cmd.SetComputeBufferParam(shader, kernel, "_Instances", instanceDataBuffer);
            cmd.SetComputeBufferParam(shader, kernel, "_ClusterSceneIndex", clusterSceneIndexBuffer);
            cmd.SetComputeBufferParam(shader, kernel, "_SceneClusterFirstTri", sceneClusterFirstTriBuffer);
            cmd.SetComputeBufferParam(shader, kernel, "_SceneClusterTriCount", sceneClusterTriCountBuffer);
            cmd.SetComputeBufferParam(
                shader,
                kernel,
                "_PageResidency",
                pageRequestsEnabled ? scenePageResidencyBuffer : fallback);
            cmd.SetComputeBufferParam(
                shader,
                kernel,
                "_PageRequests",
                pageRequestsEnabled ? scenePageRequestBuffer : fallback);
            cmd.SetComputeBufferParam(
                shader,
                kernel,
                "_ShadowVisibleDrawClusters0",
                shadowDrawClusterAppendBuffers[0]);
            cmd.SetComputeBufferParam(
                shader,
                kernel,
                "_ShadowVisibleDrawClusters1",
                shadowDrawClusterAppendBuffers[1]);
            cmd.SetComputeBufferParam(
                shader,
                kernel,
                "_ShadowVisibleDrawClusters2",
                shadowDrawClusterAppendBuffers[2]);
            cmd.SetComputeBufferParam(
                shader,
                kernel,
                "_ShadowVisibleDrawClusters3",
                shadowDrawClusterAppendBuffers[3]);

            cmd.DispatchCompute(
                shader,
                kernel,
                Mathf.Max(1, (partCount + kThreadGroupSize - 1) / kThreadGroupSize),
                1,
                1);
            for (int cascadeIndex = 0; cascadeIndex < kMaxShadowCascades; cascadeIndex++)
            {
                cmd.CopyCounterValue(
                    shadowDrawClusterAppendBuffers[cascadeIndex],
                    shadowDrawCountArgsBuffers[cascadeIndex],
                    0u);
            }

            lastShadowDrawQueueFrame = Time.frameCount;
            lastShadowDrawCameraId = camera.GetInstanceID();
            lastShadowDrawCascadeMask = (1 << activeCascadeCount) - 1;
            LastShadowUsedFusedBatch = true;
            LastShadowUsedVisiblePartQueue = false;
            LastShadowUsedVisibleInstanceQueue = false;
            return true;
        }

        public bool DispatchShadowDrawQueueFinalizeBatch(
            UnsafeCommandBuffer cmd,
            ComputeShader finalizeShader,
            int kernel,
            int compactedClusterTriangleSlots)
        {
            if (cmd == null || finalizeShader == null || kernel < 0)
                return false;
            for (int cascadeIndex = 0; cascadeIndex < kMaxShadowCascades; cascadeIndex++)
            {
                if (shadowDrawCountArgsBuffers[cascadeIndex] == null ||
                    shadowDrawArgsBuffers[cascadeIndex] == null)
                    return false;
            }

            cmd.SetComputeIntParam(
                finalizeShader,
                "_CompactedClusterTriangleSlots",
                Mathf.Max(1, compactedClusterTriangleSlots));
            for (int cascadeIndex = 0; cascadeIndex < kMaxShadowCascades; cascadeIndex++)
            {
                cmd.SetComputeBufferParam(
                    finalizeShader,
                    kernel,
                    kShadowVisibleDrawCountArgsIds[cascadeIndex],
                    shadowDrawCountArgsBuffers[cascadeIndex]);
                cmd.SetComputeBufferParam(
                    finalizeShader,
                    kernel,
                    kShadowDrawArgsIds[cascadeIndex],
                    shadowDrawArgsBuffers[cascadeIndex]);
            }
            cmd.DispatchCompute(finalizeShader, kernel, 1, 1, 1);
            return true;
        }

        void SetShadowCullParams(
            UnsafeCommandBuffer cmd,
            Vector3 cameraPosition,
            float projectionScale,
            float zNear,
            Matrix4x4 shadowWorldToClip,
            int shadowResolution,
            bool useVisiblePartQueue,
            bool useVisibleInstanceQueue)
        {
            cmd.SetComputeVectorParam(shader, "_CameraPos", new Vector4(
                cameraPosition.x,
                cameraPosition.y,
                cameraPosition.z,
                0f));
            cmd.SetComputeFloatParam(shader, "_ProjectionScale", projectionScale);
            cmd.SetComputeFloatParam(shader, "_OrthographicLodScale", projectionScale);
            cmd.SetComputeIntParam(shader, "_UseOrthographicLod", 1);
            cmd.SetComputeFloatParam(shader, "_ZNear", zNear);
            cmd.SetComputeMatrixParam(shader, "_WorldToClip", shadowWorldToClip);
            cmd.SetComputeVectorParam(shader, "_ScreenSize", new Vector4(
                shadowResolution,
                shadowResolution,
                1f / shadowResolution,
                1f / shadowResolution));
            cmd.SetComputeIntParam(shader, "_UseHzb", 0);
            cmd.SetComputeIntParam(shader, "_HizMipCount", 1);
            cmd.SetComputeIntParam(shader, "_ReversedZ", SystemInfo.usesReversedZBuffer ? 1 : 0);
            cmd.SetComputeVectorArrayParam(shader, "_FrustumPlanes", frustumPlanes);
            cmd.SetComputeIntParam(shader, "_InstanceCount", instanceCount);
            cmd.SetComputeIntParam(shader, "_PartCount", partCount);
            cmd.SetComputeIntParam(shader, "_ClusterCount", clusterCount);
            cmd.SetComputeIntParam(shader, "_ClusterCandidateCount", 0);
            cmd.SetComputeIntParam(shader, "_UseClusterCandidates", 0);
            cmd.SetComputeIntParam(shader, "_UseGpuSceneRefs", 1);
            cmd.SetComputeIntParam(shader, "_UseInstanceCull", 1);
            cmd.SetComputeIntParam(shader, "_EnableVisibleInstanceQueue", useVisibleInstanceQueue ? 1 : 0);
            cmd.SetComputeIntParam(shader, "_EnableVisiblePartQueue", useVisiblePartQueue ? 1 : 0);
            cmd.SetComputeIntParam(shader, "_EnableVisibleAppend", 0);
            cmd.SetComputeIntParam(shader, "_EnableVisibleDrawAppend", 1);
            cmd.SetComputeIntParam(shader, "_EnableClusterVisibleWrite", 0);
            cmd.SetComputeIntParam(shader, "_SceneClusterCount", sceneClusterCountMapped);
            cmd.SetComputeIntParam(shader, "_CullPassMode", (int)CullPassMode.Legacy);
            cmd.SetComputeIntParam(shader, "_HasPrevVisible", 0);
            cmd.SetComputeIntParam(shader, "_EnableCullStats", 0);
            bool pageRequestsEnabled =
                scenePageCount > 0 &&
                scenePageResidencyBuffer != null &&
                scenePageRequestBuffer != null;
            cmd.SetComputeIntParam(
                shader,
                "_PageCount",
                pageRequestsEnabled ? scenePageCount : 0);
            cmd.SetComputeIntParam(
                shader,
                "_EnablePageRequests",
                pageRequestsEnabled ? 1 : 0);
            cmd.SetComputeIntParam(
                shader,
                "_TrackPageUsage",
                pageRequestsEnabled && sceneTrackPageUsage ? 1 : 0);
        }

        void BindShadowInstanceKernel(UnsafeCommandBuffer cmd, int kernel)
        {
            cmd.SetComputeBufferParam(shader, kernel, "_Instances", instanceDataBuffer);
            cmd.SetComputeBufferParam(shader, kernel, "_InstanceVisible", instanceVisibleBuffer);
            cmd.SetComputeBufferParam(shader, kernel, "_VisibleInstancesOut", visibleInstanceAppendBuffer);
        }

        void BindShadowPartKernel(UnsafeCommandBuffer cmd, int kernel)
        {
            cmd.SetComputeBufferParam(shader, kernel, "_Parts", partsBuffer);
            cmd.SetComputeBufferParam(shader, kernel, "_VirtualParts", virtualPartsBuffer);
            cmd.SetComputeBufferParam(shader, kernel, "_PartVisible", partVisibleBuffer);
            cmd.SetComputeBufferParam(shader, kernel, "_Instances", instanceDataBuffer);
            cmd.SetComputeBufferParam(shader, kernel, "_InstanceVisible", instanceVisibleBuffer);
            cmd.SetComputeBufferParam(shader, kernel, "_VisiblePartsOut", visiblePartAppendBuffer);
            cmd.SetComputeBufferParam(shader, kernel, "_VisibleInstancesIn", visibleInstanceAppendBuffer);
            cmd.SetComputeBufferParam(
                shader,
                kernel,
                "_VisibleInstancePartDispatchArgs",
                visibleInstancePartDispatchArgsBuffer);
            cmd.SetComputeTextureParam(shader, kernel, "_HizTexture", GetFallbackHzbTexture());
        }

        void BindShadowClusterKernel(
            UnsafeCommandBuffer cmd,
            int kernel,
            ComputeBuffer drawQueue)
        {
            ComputeBuffer fallback = GetFallbackClusterVisibleBuffer();
            cmd.SetComputeBufferParam(shader, kernel, "_Parts", partsBuffer);
            cmd.SetComputeBufferParam(shader, kernel, "_Clusters", clustersBuffer);
            cmd.SetComputeBufferParam(shader, kernel, "_VirtualParts", virtualPartsBuffer);
            cmd.SetComputeBufferParam(shader, kernel, "_VirtualClusters", virtualClustersBuffer);
            cmd.SetComputeBufferParam(shader, kernel, "_PartVisible", partVisibleBuffer);
            cmd.SetComputeBufferParam(shader, kernel, "_Instances", instanceDataBuffer);
            cmd.SetComputeBufferParam(shader, kernel, "_InstanceVisible", instanceVisibleBuffer);
            cmd.SetComputeBufferParam(shader, kernel, "_ClusterSceneIndex", clusterSceneIndexBuffer);
            cmd.SetComputeBufferParam(shader, kernel, "_SceneClusterFirstTri", sceneClusterFirstTriBuffer);
            cmd.SetComputeBufferParam(shader, kernel, "_SceneClusterTriCount", sceneClusterTriCountBuffer);
            cmd.SetComputeBufferParam(shader, kernel, "_VisibleDrawClusters", drawQueue);
            cmd.SetComputeBufferParam(shader, kernel, "_ClusterVisible", fallback);
            cmd.SetComputeBufferParam(shader, kernel, "_VisibleClusters", visibleClusterAppendBuffer);
            cmd.SetComputeBufferParam(shader, kernel, "_VisiblePartsIn", visiblePartAppendBuffer);
            cmd.SetComputeBufferParam(shader, kernel, "_VisiblePartDispatchArgs", visiblePartDispatchArgsBuffer);
            cmd.SetComputeBufferParam(shader, kernel, "_ClusterCandidates", clusterCandidateBuffer);
            cmd.SetComputeBufferParam(shader, kernel, "_PrevClusterVisible", GetFallbackPrevVisibleBuffer());
            cmd.SetComputeBufferParam(shader, kernel, "_SecondPassCandidates", GetFallbackSecondPassBuffer());
            cmd.SetComputeBufferParam(shader, kernel, "_Pass2Drawn", GetFallbackPass2DrawnBuffer());
            cmd.SetComputeBufferParam(shader, kernel, "_CullStats", cullStatsBuffer ?? fallback);
            bool pageRequestsEnabled =
                scenePageCount > 0 &&
                scenePageResidencyBuffer != null &&
                scenePageRequestBuffer != null;
            cmd.SetComputeBufferParam(
                shader,
                kernel,
                "_PageResidency",
                pageRequestsEnabled ? scenePageResidencyBuffer : fallback);
            cmd.SetComputeBufferParam(
                shader,
                kernel,
                "_PageRequests",
                pageRequestsEnabled ? scenePageRequestBuffer : fallback);
            cmd.SetComputeTextureParam(shader, kernel, "_HizTexture", GetFallbackHzbTexture());
        }

        public float LodErrorPixelsOverride { get; set; }
        public int InstanceCount => instanceCount;
        public int UniqueGeometryCount => geometrySlots.Count;
        public int GeometryPartCount => geometryPartCount;
        public int GeometryClusterCount => geometryClusterCount;
        public int VirtualPartCount => partCount;
        public int VirtualClusterCount => clusterCount;
        public int PartQueueMinVirtualParts { get; set; } = 4096;
        public int InstanceQueueMinInstances { get; set; } = 64;
        public int ShadowPartQueueMinVirtualParts { get; set; } = 32768;
        public int ShadowInstanceQueueMinInstances { get; set; } = 64;
        public bool PreferBvhCandidates { get; set; } = true;
        public bool PreferPartDrivenClusterCull { get; set; } = true;
        public bool LastUsedCpuCandidates { get; private set; }
        public bool LastUsedPartDrivenClusterCull { get; private set; }
        public bool LastUsedVisiblePartQueue { get; private set; }
        public bool LastUsedVisibleInstanceQueue { get; private set; }
        public bool LastShadowUsedVisiblePartQueue { get; private set; }
        public bool LastShadowUsedVisibleInstanceQueue { get; private set; }
        public bool LastShadowUsedFusedBatch { get; private set; }
        public ComputeBuffer VisibleDrawClusterBuffer => visibleDrawClusterAppendBuffer;
        public ComputeBuffer VisibleDrawCountArgsBuffer => visibleDrawCountArgsBuffer;
        public ComputeBuffer GetShadowDrawClusterBuffer(int cascadeIndex) =>
            cascadeIndex >= 0 && cascadeIndex < kMaxShadowCascades
                ? shadowDrawClusterAppendBuffers[cascadeIndex]
                : null;
        public ComputeBuffer GetShadowDrawCountArgsBuffer(int cascadeIndex) =>
            cascadeIndex >= 0 && cascadeIndex < kMaxShadowCascades
                ? shadowDrawCountArgsBuffers[cascadeIndex]
                : null;
        public GraphicsBuffer GetShadowDrawArgsBuffer(int cascadeIndex) =>
            cascadeIndex >= 0 && cascadeIndex < kMaxShadowCascades
                ? shadowDrawArgsBuffers[cascadeIndex]
                : null;
        public bool IsShadowDrawQueueReady(Camera camera, int cascadeIndex) =>
            camera != null &&
            cascadeIndex >= 0 && cascadeIndex < kMaxShadowCascades &&
            lastShadowDrawQueueFrame == Time.frameCount &&
            lastShadowDrawCameraId == camera.GetInstanceID() &&
            (lastShadowDrawCascadeMask & (1 << cascadeIndex)) != 0 &&
            shadowDrawClusterAppendBuffers[cascadeIndex] != null &&
            shadowDrawArgsBuffers[cascadeIndex] != null;
        public bool IsVisibleDrawQueueReady(Camera camera) =>
            camera != null &&
            lastVisibleDrawQueueFrame == Time.frameCount &&
            lastVisibleDrawCameraId == camera.GetInstanceID() &&
            visibleDrawClusterAppendBuffer != null &&
            visibleDrawCountArgsBuffer != null;
        public int LastClusterCandidateCount { get; private set; }
        public int LastClusterCount { get; private set; }
        public int LastCull1Drawn { get; private set; }
        public int LastCull2Candidates { get; private set; }
        public int LastCull2Drawn { get; private set; }

        static int ComputeRebuildSignature(IReadOnlyList<NaniteRuntimeProxy> proxies)
        {
            unchecked
            {
                int hash = 17;
                for (int i = 0; i < proxies.Count; i++)
                {
                    var proxy = proxies[i];
                    if (!IsBatchableProxy(proxy))
                        continue;
                    hash = hash * 31 + proxy.GetInstanceID();
                    hash = hash * 31 + (proxy.naniteMesh != null ? proxy.naniteMesh.GetInstanceID() : 0);
                }

                hash = hash * 31 + kGpuLayoutVersion;
                return hash;
            }
        }

        bool BuildCpuClusterCandidates(
            Plane[] planes,
            Vector3 cameraPos,
            float projectionScale,
            float zNear,
            out int testedNodes,
            out int testedParts,
            out int candidateCount)
        {
            using var profilerScope = kCpuBvhMarker.Auto();
            testedNodes = 0;
            testedParts = 0;
            candidateCount = 0;

            // The shared GPU Scene stores geometry once and virtualizes it through
            // compact references. The legacy CPU BVH path expects expanded arrays;
            // keep it in the single-mesh backend instead of rebuilding that duplication.
            if (virtualPartRefsCpu != null && virtualClusterRefsCpu != null)
                return false;

            if (partDataCpu == null || partVisibleCpu == null || clusterCandidatesCpu == null)
                return false;

            bool hasAnyBvh = false;
            for (int s = 0; s < slots.Count; s++)
            {
                var mesh = slots[s].mesh;
                if (mesh?.pageArray == null)
                    continue;
                for (int p = 0; p < mesh.pageArray.Length; p++)
                {
                    var page = mesh.pageArray[p];
                    if (page?.bvhNodes != null && page.bvhNodes.Length > 0 && page.bvhRoot >= 0)
                    {
                        hasAnyBvh = true;
                        break;
                    }
                }

                if (hasAnyBvh)
                    break;
            }

            if (!hasAnyBvh)
                return false;

            System.Array.Clear(partVisibleCpu, 0, partVisibleCpu.Length);
            int write = 0;

            for (int s = 0; s < slots.Count; s++)
            {
                BatchSlot slot = slots[s];
                var mesh = slot.mesh;
                if (mesh?.pageArray == null || slot.pagePartBase == null || slot.pagePartCount == null)
                    continue;

                var localToWorld = slot.proxy.transform.localToWorldMatrix;
                float maxScale = instanceData[s].maxScale;
                float lodErrorPixels = instanceData[s].lodErrorPixels;

                for (int pageIndex = 0; pageIndex < mesh.pageArray.Length; pageIndex++)
                {
                    var page = mesh.pageArray[pageIndex];
                    if (page?.parts == null)
                        continue;

                    int partBase = slot.pagePartBase[pageIndex];
                    int partCountInPage = slot.pagePartCount[pageIndex];
                    if (partBase < 0 || partCountInPage <= 0)
                        continue;

                    if (page.bvhNodes != null && page.bvhNodes.Length > 0 && page.bvhRoot >= 0)
                    {
                        bvhStack.Clear();
                        bvhStack.Add(page.bvhRoot);
                        while (bvhStack.Count > 0)
                        {
                            int idx = bvhStack[bvhStack.Count - 1];
                            bvhStack.RemoveAt(bvhStack.Count - 1);
                            if (idx < 0 || idx >= page.bvhNodes.Length)
                                continue;

                            ref readonly var node = ref page.bvhNodes[idx];
                            testedNodes++;

                            Vector4 worldNode = TransformSphere(node.sphere, localToWorld, maxScale);
                            if (!SphereVisible(worldNode, planes))
                                continue;

                            Vector4 lodSphereLocal = node.lodSphere.w > 0f ? node.lodSphere : node.sphere;
                            Vector4 worldLod = TransformSphere(lodSphereLocal, localToWorld, maxScale);
                            float nodeError = ProjectedErrorPixels(node.maxParentLodError, worldLod, cameraPos, projectionScale, zNear);
                            if (nodeError <= lodErrorPixels)
                                continue;

                            if (node.partIndex >= 0)
                            {
                                int localPart = node.partIndex;
                                if (localPart < 0 || localPart >= partCountInPage)
                                    continue;
                                int globalPart = partBase + localPart;
                                testedParts++;
                                if (!PartVisibleForLod(partDataCpu[globalPart], planes, cameraPos, projectionScale, zNear, lodErrorPixels, localToWorld, maxScale))
                                    continue;
                                if (partVisibleCpu[globalPart] == 0)
                                {
                                    partVisibleCpu[globalPart] = 1;
                                    write = AppendPartClusters(globalPart, write);
                                }
                            }
                            else
                            {
                                if (node.child0 >= 0) bvhStack.Add(node.child0);
                                if (node.child1 >= 0) bvhStack.Add(node.child1);
                                if (node.child2 >= 0) bvhStack.Add(node.child2);
                                if (node.child3 >= 0) bvhStack.Add(node.child3);
                            }
                        }
                    }
                    else
                    {
                        for (int localPart = 0; localPart < partCountInPage; localPart++)
                        {
                            int globalPart = partBase + localPart;
                            testedParts++;
                            if (!PartVisibleForLod(partDataCpu[globalPart], planes, cameraPos, projectionScale, zNear, lodErrorPixels, localToWorld, maxScale))
                                continue;
                            if (partVisibleCpu[globalPart] == 0)
                            {
                                partVisibleCpu[globalPart] = 1;
                                write = AppendPartClusters(globalPart, write);
                            }
                        }
                    }
                }
            }

            candidateCount = write;
            return true;
        }

        int AppendPartClusters(int globalPart, int write)
        {
            ref readonly var part = ref partDataCpu[globalPart];
            int start = part.clusterStart;
            int end = start + part.clusterCount;
            for (int i = start; i < end && write < clusterCandidatesCpu.Length; i++)
                clusterCandidatesCpu[write++] = (uint)i;
            return write;
        }

        void BindInstanceBuffers(int kernel)
        {
            shader.SetInt("_InstanceCount", instanceCount);
            shader.SetBuffer(kernel, "_Instances", instanceDataBuffer);
            shader.SetBuffer(kernel, "_InstanceVisible", instanceVisibleBuffer);
        }

        void SetSharedParams(
            Vector3 cameraPos,
            float projectionScale,
            float zNear,
            Matrix4x4 worldToClip,
            int screenWidth,
            int screenHeight,
            bool useHzb)
        {
            shader.SetVector("_CameraPos", new Vector4(cameraPos.x, cameraPos.y, cameraPos.z, 0f));
            shader.SetFloat("_ProjectionScale", projectionScale);
            shader.SetFloat("_OrthographicLodScale", 0f);
            shader.SetInt("_UseOrthographicLod", 0);
            shader.SetFloat("_ZNear", zNear);
            shader.SetMatrix("_WorldToClip", worldToClip);
            shader.SetVector("_ScreenSize", new Vector4(Mathf.Max(1, screenWidth), Mathf.Max(1, screenHeight), 1f / Mathf.Max(1, screenWidth), 1f / Mathf.Max(1, screenHeight)));
            shader.SetInt("_UseHzb", useHzb ? 1 : 0);
            shader.SetInt("_ReversedZ", SystemInfo.usesReversedZBuffer ? 1 : 0);
            shader.SetVectorArray("_FrustumPlanes", frustumPlanes);
        }

        static float ComputeShadowProjectionScale(Matrix4x4 projectionMatrix, int resolution)
        {
            // Directional cascades are orthographic. m00/m11 convert world-space radius to
            // NDC radius; half the tile resolution converts NDC to shadow texels.
            float ndcPerWorld = Mathf.Max(
                Mathf.Abs(projectionMatrix.m00),
                Mathf.Abs(projectionMatrix.m11));
            return 0.5f * Mathf.Max(1, resolution) * ndcPerWorld;
        }

        void BindHzbTexture(int kernel, Texture hzbTexture)
        {
            shader.SetTexture(kernel, "_HizTexture", hzbTexture);
        }

        bool TryFindKernels()
        {
            try
            {
                kernelPartCull = shader.FindKernel("CSPartCull");
                kernelClusterCull = shader.FindKernel("CSClusterCull");
                kernelClusterCullByPart = shader.FindKernel("CSClusterCullByPart");
                kernelFinalizeVisiblePartDispatch = shader.FindKernel("CSFinalizeVisiblePartDispatch");
                kernelClusterCullVisibleParts = shader.FindKernel("CSClusterCullVisibleParts");
                kernelInstanceCull = shader.FindKernel("CSInstanceCull");
                kernelPartCullVisibleInstances = shader.FindKernel("CSPartCullVisibleInstances");
                try { kernelShadowCullMultiCascadeByPart = shader.FindKernel("CSShadowCullMultiCascadeByPart"); }
                catch { kernelShadowCullMultiCascadeByPart = -1; }
                try
                {
                    kernelClearClusterVisible = shader.FindKernel("CSClearClusterVisible");
                }
                catch
                {
                    kernelClearClusterVisible = -1;
                }

                try { kernelClearSecondPassCandidates = shader.FindKernel("CSClearSecondPassCandidates"); }
                catch { kernelClearSecondPassCandidates = -1; }
                try { kernelCopyUintBuffer = shader.FindKernel("CSCopyUintBuffer"); }
                catch { kernelCopyUintBuffer = -1; }
                try { kernelClearCullStats = shader.FindKernel("CSClearCullStats"); }
                catch { kernelClearCullStats = -1; }
                try { kernelOrMasksToVisible = shader.FindKernel("CSOrMasksToVisible"); }
                catch { kernelOrMasksToVisible = -1; }

                return true;
            }
            catch
            {
                return false;
            }
        }

        void ReleaseBuffers()
        {
            partsBuffer?.Release();
            clustersBuffer?.Release();
            virtualPartsBuffer?.Release();
            virtualClustersBuffer?.Release();
            visiblePartAppendBuffer?.Release();
            visiblePartDispatchArgsBuffer?.Release();
            visibleInstanceAppendBuffer?.Release();
            visibleInstancePartDispatchArgsBuffer?.Release();
            visibleDrawClusterAppendBuffer?.Release();
            visibleDrawCountArgsBuffer?.Release();
            for (int cascadeIndex = 0; cascadeIndex < kMaxShadowCascades; cascadeIndex++)
            {
                shadowDrawClusterAppendBuffers[cascadeIndex]?.Release();
                shadowDrawCountArgsBuffers[cascadeIndex]?.Release();
                shadowDrawArgsBuffers[cascadeIndex]?.Dispose();
                shadowDrawClusterAppendBuffers[cascadeIndex] = null;
                shadowDrawCountArgsBuffers[cascadeIndex] = null;
                shadowDrawArgsBuffers[cascadeIndex] = null;
            }
            partVisibleBuffer?.Release();
            visibleClusterAppendBuffer?.Release();
            visibleCountBuffer?.Release();
            clusterCandidateBuffer?.Release();
            clusterSceneIndexBuffer?.Release();
            instanceDataBuffer?.Release();
            instanceVisibleBuffer?.Release();
            fallbackClusterVisibleBuffer?.Release();
            fallbackPrevVisibleBuffer?.Release();
            fallbackSecondPassBuffer?.Release();
            fallbackPass2DrawnBuffer?.Release();
            cullStatsBuffer?.Release();

            partsBuffer = null;
            clustersBuffer = null;
            virtualPartsBuffer = null;
            virtualClustersBuffer = null;
            visiblePartAppendBuffer = null;
            visiblePartDispatchArgsBuffer = null;
            visibleInstanceAppendBuffer = null;
            visibleInstancePartDispatchArgsBuffer = null;
            visibleDrawClusterAppendBuffer = null;
            visibleDrawCountArgsBuffer = null;
            partVisibleBuffer = null;
            visibleClusterAppendBuffer = null;
            visibleCountBuffer = null;
            clusterCandidateBuffer = null;
            clusterSceneIndexBuffer = null;
            sceneClusterFirstTriBuffer = null;
            sceneClusterTriCountBuffer = null;
            scenePageResidencyBuffer = null;
            scenePageRequestBuffer = null;
            scenePageCount = 0;
            sceneTrackPageUsage = false;
            instanceDataBuffer = null;
            instanceVisibleBuffer = null;
            fallbackClusterVisibleBuffer = null;
            fallbackPrevVisibleBuffer = null;
            fallbackSecondPassBuffer = null;
            fallbackPass2DrawnBuffer = null;
            cullStatsBuffer = null;
            lastShadowDrawQueueFrame = -1;
            lastShadowDrawCameraId = 0;
            lastShadowDrawCascadeMask = 0;
        }

        Texture GetFallbackHzbTexture()
        {
            if (fallbackHzbTexture != null)
                return fallbackHzbTexture;

            fallbackHzbTexture = new RenderTexture(1, 1, 0, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear)
            {
                name = "Nanite_Batched_Fallback_HZB",
                useMipMap = false,
                autoGenerateMips = false,
                enableRandomWrite = false,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            fallbackHzbTexture.Create();
            return fallbackHzbTexture;
        }

        void ReleaseFallbackHzbTexture()
        {
            if (fallbackHzbTexture == null)
                return;
            fallbackHzbTexture.Release();
            fallbackHzbTexture = null;
        }

        static bool PartVisibleForLod(
            in NaniteGpuCullingBackend.GpuPartData part,
            Plane[] planes,
            Vector3 cameraPos,
            float projectionScale,
            float zNear,
            float lodErrorPixels,
            Matrix4x4 localToWorld,
            float maxScale)
        {
            Vector4 worldPart = TransformSphere(part.selfSphere, localToWorld, maxScale);
            if (!SphereVisible(worldPart, planes))
                return false;

            Vector4 lodSphereLocal = part.parentSphere.w > 0f ? part.parentSphere : part.selfSphere;
            Vector4 worldLod = TransformSphere(lodSphereLocal, localToWorld, maxScale);
            float partError = ProjectedErrorPixels(part.maxParentError, worldLod, cameraPos, projectionScale, zNear);
            return partError > lodErrorPixels;
        }

        static Vector4 TransformSphere(in Vector4 localSphere, Matrix4x4 localToWorld, float maxScale)
        {
            Vector3 worldCenter = localToWorld.MultiplyPoint3x4(new Vector3(localSphere.x, localSphere.y, localSphere.z));
            float worldRadius = localSphere.w * Mathf.Max(1e-6f, maxScale);
            return new Vector4(worldCenter.x, worldCenter.y, worldCenter.z, worldRadius);
        }

        static bool SphereVisible(in Vector4 sphere, Plane[] planes)
        {
            var center = new Vector3(sphere.x, sphere.y, sphere.z);
            float radius = sphere.w;
            for (int i = 0; i < planes.Length; i++)
            {
                if (planes[i].GetDistanceToPoint(center) < -radius)
                    return false;
            }

            return true;
        }

        static float ProjectedErrorPixels(float error, in Vector4 sphere, Vector3 cameraPosition, float projectionScale, float zNear)
        {
            if (error >= float.MaxValue * 0.5f)
                return float.PositiveInfinity;
            var center = new Vector3(sphere.x, sphere.y, sphere.z);
            float distance = Vector3.Distance(center, cameraPosition) - sphere.w;
            distance = Mathf.Max(distance, zNear);
            return (2f * error * projectionScale) / distance;
        }
    }
}
