using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

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
            public int partOffset;
            public int clusterOffset;
            public int partCount;
            public int clusterCount;
            public int[] pagePartBase;
            public int[] pagePartCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct GpuVisibleRef
        {
            public uint instanceIndex;
            public uint pageIndex;
            public uint clusterIndex;
        }

        const int kThreadGroupSize = 64;
        const int kGpuLayoutVersion = 2;

        public enum CullPassMode
        {
            Legacy = 0,
            Pass1PrevVisible = 1,
            Pass2CandidatesHzb = 2
        }

        ComputeShader shader;
        int kernelPartCull = -1;
        int kernelClusterCull = -1;
        int kernelClearClusterVisible = -1;
        int kernelClearSecondPassCandidates = -1;
        int kernelCopyUintBuffer = -1;
        int kernelClearCullStats = -1;
        int kernelOrMasksToVisible = -1;

        ComputeBuffer partsBuffer;
        ComputeBuffer clustersBuffer;
        ComputeBuffer partVisibleBuffer;
        ComputeBuffer visibleClusterAppendBuffer;
        ComputeBuffer visibleCountBuffer;
        ComputeBuffer clusterCandidateBuffer;
        ComputeBuffer clusterSceneIndexBuffer;
        ComputeBuffer instanceLocalToWorldBuffer;
        ComputeBuffer instanceMaxScaleBuffer;
        ComputeBuffer instanceLodErrorBuffer;
        ComputeBuffer fallbackClusterVisibleBuffer;
        ComputeBuffer fallbackPrevVisibleBuffer;
        ComputeBuffer fallbackSecondPassBuffer;
        ComputeBuffer fallbackPass2DrawnBuffer;
        ComputeBuffer cullStatsBuffer;
        RenderTexture fallbackHzbTexture;
        readonly uint[] cullStatsCpu = new uint[3];

        readonly List<BatchSlot> slots = new List<BatchSlot>(32);
        readonly List<NaniteGpuCullingBackend.GpuPartData> mergedParts = new List<NaniteGpuCullingBackend.GpuPartData>(8192);
        readonly List<NaniteGpuCullingBackend.GpuClusterData> mergedClusters = new List<NaniteGpuCullingBackend.GpuClusterData>(65536);
        readonly List<Matrix4x4> instanceMatrices = new List<Matrix4x4>(32);
        readonly List<float> instanceMaxScales = new List<float>(32);
        readonly List<float> instanceLodErrors = new List<float>(32);
        readonly List<int> bvhStack = new List<int>(256);
        readonly Vector4[] frustumPlanes = new Vector4[6];
        readonly Dictionary<int, List<NaniteVisibleClusterRef>> visibleScratch = new Dictionary<int, List<NaniteVisibleClusterRef>>();

        NaniteGpuCullingBackend.GpuPartData[] partDataCpu;
        NaniteGpuCullingBackend.GpuClusterData[] clusterDataCpu;
        uint[] partVisibleCpu;
        uint[] clusterCandidatesCpu;
        uint[] clusterSceneIndexCpu;
        uint[] visibleCountCpu = new uint[1];
        // 复用读回缓冲，避免每帧 new[] 触发 GC。
        GpuVisibleRef[] visibleRefsScratch = Array.Empty<GpuVisibleRef>();
        int partCount;
        int clusterCount;
        int clusterCandidateCount;
        int instanceCount;
        int rebuildSignature;
        int sceneIndexSignature;
        int sceneClusterCountMapped;

        public bool IsReady =>
            shader != null &&
            kernelPartCull >= 0 &&
            kernelClusterCull >= 0 &&
            partsBuffer != null &&
            clustersBuffer != null &&
            partVisibleBuffer != null &&
            visibleClusterAppendBuffer != null &&
            visibleCountBuffer != null &&
            clusterCandidateBuffer != null &&
            instanceLocalToWorldBuffer != null &&
            instanceMaxScaleBuffer != null &&
            instanceLodErrorBuffer != null &&
            instanceCount > 0;

        public bool SupportsGpuVisibleMask =>
            IsReady &&
            kernelClearClusterVisible >= 0 &&
            clusterSceneIndexBuffer != null &&
            clusterSceneIndexCpu != null &&
            sceneClusterCountMapped > 0;

        public void Dispose()
        {
            ReleaseBuffers();
            ReleaseFallbackHzbTexture();
            shader = null;
            kernelPartCull = -1;
            kernelClusterCull = -1;
            slots.Clear();
            mergedParts.Clear();
            mergedClusters.Clear();
            instanceMatrices.Clear();
            instanceMaxScales.Clear();
            instanceLodErrors.Clear();
            partDataCpu = null;
            clusterDataCpu = null;
            partVisibleCpu = null;
            clusterCandidatesCpu = null;
            clusterSceneIndexCpu = null;
            visibleRefsScratch = Array.Empty<GpuVisibleRef>();
            partCount = 0;
            clusterCount = 0;
            clusterCandidateCount = 0;
            instanceCount = 0;
            rebuildSignature = 0;
            sceneIndexSignature = 0;
            sceneClusterCountMapped = 0;
            LastClusterCandidateCount = 0;
            LastClusterCount = 0;
            LastCull1Drawn = 0;
            LastCull2Candidates = 0;
            LastCull2Drawn = 0;
        }

        public bool EnsureClusterSceneIndex(NaniteSceneVisibilityBufferBackend scene)
        {
            if (!IsReady || scene == null || !scene.IsReady || clusterDataCpu == null || slots.Count == 0)
                return false;

            int sig = unchecked(scene.GeometryGeneration * 397 + clusterCount * 31 + slots.Count);
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
                var c = clusterDataCpu[i];
                int inst = c.instanceIndex;
                uint sceneIndex = 0xFFFFFFFFu;
                if ((uint)inst < (uint)slots.Count)
                {
                    int proxyId = slots[inst].proxyId;
                    if (scene.TryGetGlobalClusterIndex(proxyId, c.pageIndex, c.clusterIndex, out int gi) && gi >= 0)
                    {
                        sceneIndex = (uint)gi;
                        mapped++;
                    }
                }

                clusterSceneIndexCpu[i] = sceneIndex;
            }

            if (mapped <= 0)
                return false;

            clusterSceneIndexBuffer?.Release();
            clusterSceneIndexBuffer = new ComputeBuffer(clusterSceneIndexCpu.Length, sizeof(uint), ComputeBufferType.Structured);
            clusterSceneIndexBuffer.SetData(clusterSceneIndexCpu);
            sceneIndexSignature = sig;
            sceneClusterCountMapped = scene.ClusterCount;
            return true;
        }

        public bool EnsureInitialized(ComputeShader cullingShader, IReadOnlyList<NaniteRuntimeProxy> proxies)
        {
            if (cullingShader == null || proxies == null || proxies.Count == 0)
                return false;

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
            LastClusterCandidateCount = PreferBvhCandidates ? clusterCandidateCount : clusterCount;
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
            hasCpuCandidates = false;
            cpuTestedNodes = 0;

            var planes = GeometryUtility.CalculateFrustumPlanes(camera);
            for (int i = 0; i < 6; i++)
            {
                var p = planes[i];
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

            if (allowCpuCandidates)
            {
                hasCpuCandidates = BuildCpuClusterCandidates(
                    planes,
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
                BindHzb(kernelClearClusterVisible, hzbTexture, hzbMipCount, enableHzb);
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

            if (hasCpuCandidates)
            {
                partVisibleBuffer.SetData(partVisibleCpu);
                if (clusterCandidateCount > 0)
                    clusterCandidateBuffer.SetData(clusterCandidatesCpu, 0, 0, clusterCandidateCount);
            }
            else
            {
                SetSharedParams(kernelPartCull, cameraPos, projectionScale, zNear, worldToClip, camera.pixelWidth, camera.pixelHeight, enableHzb);
                shader.SetInt("_PartCount", partCount);
                shader.SetInt("_SceneClusterCount", visibleCountParam);
                shader.SetInt("_CullPassMode", (int)cullPassMode);
                shader.SetInt("_HasPrevVisible", hasPrevVisible ? 1 : 0);
                shader.SetInt("_EnableVisibleAppend", 0);
                shader.SetInt("_EnableClusterVisibleWrite", 0);
                shader.SetBuffer(kernelPartCull, "_Parts", partsBuffer);
                shader.SetBuffer(kernelPartCull, "_PartVisible", partVisibleBuffer);
                BindCullSharedBuffers(kernelPartCull, visibleTarget, sceneIndexBuf, prevBuf, secondBuf, pass2Buf);
                BindInstanceBuffers(kernelPartCull);
                BindHzb(kernelPartCull, hzbTexture, hzbMipCount, enableHzb);

                int partGroups = (partCount + kThreadGroupSize - 1) / kThreadGroupSize;
                shader.Dispatch(kernelPartCull, partGroups, 1, 1);
            }

            visibleClusterAppendBuffer.SetCounterValue(0);
            SetSharedParams(kernelClusterCull, cameraPos, projectionScale, zNear, worldToClip, camera.pixelWidth, camera.pixelHeight, enableHzb);
            shader.SetInt("_PartCount", partCount);
            shader.SetInt("_ClusterCount", clusterCount);
            shader.SetInt("_ClusterCandidateCount", hasCpuCandidates ? clusterCandidateCount : 0);
            shader.SetInt("_UseClusterCandidates", hasCpuCandidates ? 1 : 0);
            shader.SetInt("_EnableVisibleAppend", enableVisibleAppend ? 1 : 0);
            shader.SetInt("_EnableClusterVisibleWrite", enableClusterVisibleWrite ? 1 : 0);
            shader.SetInt("_SceneClusterCount", visibleCountParam);
            shader.SetInt("_CullPassMode", (int)cullPassMode);
            shader.SetInt("_HasPrevVisible", hasPrevVisible ? 1 : 0);
            shader.SetInt("_EnableCullStats", enableCullStats ? 1 : 0);
            shader.SetBuffer(kernelClusterCull, "_Clusters", clustersBuffer);
            shader.SetBuffer(kernelClusterCull, "_PartVisible", partVisibleBuffer);
            shader.SetBuffer(kernelClusterCull, "_VisibleClusters", visibleClusterAppendBuffer);
            shader.SetBuffer(kernelClusterCull, "_ClusterCandidates", clusterCandidateBuffer);
            shader.SetBuffer(kernelClusterCull, "_CullStats", cullStatsBuffer);
            BindCullSharedBuffers(kernelClusterCull, visibleTarget, sceneIndexBuf, prevBuf, secondBuf, pass2Buf);
            BindInstanceBuffers(kernelClusterCull);
            BindHzb(kernelClusterCull, hzbTexture, hzbMipCount, enableHzb);

            int clusterWorkCount = hasCpuCandidates ? clusterCandidateCount : clusterCount;
            if (clusterWorkCount > 0)
            {
                int clusterGroups = (clusterWorkCount + kThreadGroupSize - 1) / kThreadGroupSize;
                shader.Dispatch(kernelClusterCull, clusterGroups, 1, 1);
            }

            return true;
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

            bool dirty = false;
            for (int s = 0; s < slots.Count; s++)
            {
                var proxy = slots[s].proxy;
                if (proxy == null || !proxy.isActiveAndEnabled)
                    continue;

                Matrix4x4 m = proxy.transform.localToWorldMatrix;
                float maxScale = Mathf.Max(
                    Mathf.Abs(proxy.transform.lossyScale.x),
                    Mathf.Abs(proxy.transform.lossyScale.y),
                    Mathf.Abs(proxy.transform.lossyScale.z));
                float lodErr = LodErrorPixelsOverride > 0f ? LodErrorPixelsOverride : proxy.lodErrorPixels;
                if (instanceMatrices[s] != m ||
                    !Mathf.Approximately(instanceMaxScales[s], maxScale) ||
                    !Mathf.Approximately(instanceLodErrors[s], lodErr))
                {
                    instanceMatrices[s] = m;
                    instanceMaxScales[s] = maxScale;
                    instanceLodErrors[s] = lodErr;
                    dirty = true;
                }
            }

            if (!dirty)
                return;

            instanceLocalToWorldBuffer.SetData(instanceMatrices, 0, 0, slots.Count);
            instanceMaxScaleBuffer.SetData(instanceMaxScales, 0, 0, slots.Count);
            instanceLodErrorBuffer.SetData(instanceLodErrors, 0, 0, slots.Count);
        }

        bool RebuildMergedData(IReadOnlyList<NaniteRuntimeProxy> proxies)
        {
            ReleaseBuffers();
            slots.Clear();
            mergedParts.Clear();
            mergedClusters.Clear();
            instanceMatrices.Clear();
            instanceMaxScales.Clear();
            instanceLodErrors.Clear();

            for (int i = 0; i < proxies.Count; i++)
            {
                var proxy = proxies[i];
                if (!IsBatchableProxy(proxy))
                    continue;

                int instanceIndex = slots.Count;
                int partOffset = mergedParts.Count;
                int clusterOffset = mergedClusters.Count;

                NaniteGpuCullingBackend.BuildGpuData(
                    proxy.naniteMesh,
                    instanceIndex,
                    mergedParts,
                    mergedClusters,
                    out var pagePartBase,
                    out var pagePartCount);

                int partCountInMesh = mergedParts.Count - partOffset;
                int clusterCountInMesh = mergedClusters.Count - clusterOffset;
                if (partCountInMesh <= 0 || clusterCountInMesh <= 0)
                    continue;

                slots.Add(new BatchSlot
                {
                    proxy = proxy,
                    proxyId = proxy.GetInstanceID(),
                    mesh = proxy.naniteMesh,
                    partOffset = partOffset,
                    clusterOffset = clusterOffset,
                    partCount = partCountInMesh,
                    clusterCount = clusterCountInMesh,
                    pagePartBase = pagePartBase,
                    pagePartCount = pagePartCount
                });

                instanceMatrices.Add(proxy.transform.localToWorldMatrix);
                instanceMaxScales.Add(Mathf.Max(
                    Mathf.Abs(proxy.transform.lossyScale.x),
                    Mathf.Abs(proxy.transform.lossyScale.y),
                    Mathf.Abs(proxy.transform.lossyScale.z)));
                instanceLodErrors.Add(LodErrorPixelsOverride > 0f ? LodErrorPixelsOverride : proxy.lodErrorPixels);
            }

            partCount = mergedParts.Count;
            clusterCount = mergedClusters.Count;
            instanceCount = slots.Count;
            if (partCount == 0 || clusterCount == 0 || instanceCount == 0)
                return false;

            partDataCpu = mergedParts.ToArray();
            clusterDataCpu = mergedClusters.ToArray();
            partVisibleCpu = new uint[partCount];
            clusterCandidatesCpu = new uint[clusterCount];
            clusterSceneIndexCpu = null;
            sceneIndexSignature = 0;
            sceneClusterCountMapped = 0;

            partsBuffer = new ComputeBuffer(partCount, Marshal.SizeOf<NaniteGpuCullingBackend.GpuPartData>());
            clustersBuffer = new ComputeBuffer(clusterCount, Marshal.SizeOf<NaniteGpuCullingBackend.GpuClusterData>());
            partVisibleBuffer = new ComputeBuffer(partCount, sizeof(uint));
            visibleClusterAppendBuffer = new ComputeBuffer(clusterCount, Marshal.SizeOf<GpuVisibleRef>(), ComputeBufferType.Append);
            visibleCountBuffer = new ComputeBuffer(1, sizeof(uint), ComputeBufferType.Raw);
            clusterCandidateBuffer = new ComputeBuffer(clusterCount, sizeof(uint));
            instanceLocalToWorldBuffer = new ComputeBuffer(instanceCount, sizeof(float) * 16);
            instanceMaxScaleBuffer = new ComputeBuffer(instanceCount, sizeof(float));
            instanceLodErrorBuffer = new ComputeBuffer(instanceCount, sizeof(float));

            partsBuffer.SetData(partDataCpu);
            clustersBuffer.SetData(clusterDataCpu);
            visibleClusterAppendBuffer.SetCounterValue(0);
            instanceLocalToWorldBuffer.SetData(instanceMatrices);
            instanceMaxScaleBuffer.SetData(instanceMaxScales);
            instanceLodErrorBuffer.SetData(instanceLodErrors);
            return true;
        }

        static bool IsBatchableProxy(NaniteRuntimeProxy proxy)
        {
            // Feature 提供全局 culling shader 时可批；不再强制 proxy.useGpuCulling（默认 false 会导致整帧 GPU cull 永不启用）。
            return proxy != null &&
                   proxy.isActiveAndEnabled &&
                   proxy.naniteMesh != null;
        }

        public float LodErrorPixelsOverride { get; set; }
        public bool PreferBvhCandidates { get; set; } = true;
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
            testedNodes = 0;
            testedParts = 0;
            candidateCount = 0;

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
                float maxScale = instanceMaxScales[s];
                float lodErrorPixels = instanceLodErrors[s];

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
            shader.SetBuffer(kernel, "_InstanceLocalToWorld", instanceLocalToWorldBuffer);
            shader.SetBuffer(kernel, "_InstanceMaxScale", instanceMaxScaleBuffer);
            shader.SetBuffer(kernel, "_InstanceLodErrorPixels", instanceLodErrorBuffer);
        }

        void SetSharedParams(
            int kernel,
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
            shader.SetFloat("_ZNear", zNear);
            shader.SetMatrix("_WorldToClip", worldToClip);
            shader.SetVector("_ScreenSize", new Vector4(Mathf.Max(1, screenWidth), Mathf.Max(1, screenHeight), 1f / Mathf.Max(1, screenWidth), 1f / Mathf.Max(1, screenHeight)));
            shader.SetInt("_UseHzb", useHzb ? 1 : 0);
            shader.SetInt("_ReversedZ", SystemInfo.usesReversedZBuffer ? 1 : 0);
            shader.SetVectorArray("_FrustumPlanes", frustumPlanes);
        }

        void BindHzb(int kernel, Texture hzbTexture, int hzbMipCount, bool enableHzb)
        {
            if (enableHzb)
            {
                shader.SetInt("_UseHzb", 1);
                shader.SetInt("_HizMipCount", Mathf.Max(1, hzbMipCount));
                shader.SetTexture(kernel, "_HizTexture", hzbTexture);
                return;
            }

            shader.SetInt("_UseHzb", 0);
            shader.SetInt("_HizMipCount", 1);
            shader.SetTexture(kernel, "_HizTexture", GetFallbackHzbTexture());
        }

        bool TryFindKernels()
        {
            try
            {
                kernelPartCull = shader.FindKernel("CSPartCull");
                kernelClusterCull = shader.FindKernel("CSClusterCull");
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
            partVisibleBuffer?.Release();
            visibleClusterAppendBuffer?.Release();
            visibleCountBuffer?.Release();
            clusterCandidateBuffer?.Release();
            clusterSceneIndexBuffer?.Release();
            instanceLocalToWorldBuffer?.Release();
            instanceMaxScaleBuffer?.Release();
            instanceLodErrorBuffer?.Release();
            fallbackClusterVisibleBuffer?.Release();
            fallbackPrevVisibleBuffer?.Release();
            fallbackSecondPassBuffer?.Release();
            fallbackPass2DrawnBuffer?.Release();
            cullStatsBuffer?.Release();

            partsBuffer = null;
            clustersBuffer = null;
            partVisibleBuffer = null;
            visibleClusterAppendBuffer = null;
            visibleCountBuffer = null;
            clusterCandidateBuffer = null;
            clusterSceneIndexBuffer = null;
            instanceLocalToWorldBuffer = null;
            instanceMaxScaleBuffer = null;
            instanceLodErrorBuffer = null;
            fallbackClusterVisibleBuffer = null;
            fallbackPrevVisibleBuffer = null;
            fallbackSecondPassBuffer = null;
            fallbackPass2DrawnBuffer = null;
            cullStatsBuffer = null;
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
