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
            public int maxCutClusterCount;
            public int[] pagePartBase;
            public int[] pagePartCount;
            public Vector4 bounds;
            public int hierarchyRootOffset;
            public int hierarchyRootCount;
            public int hierarchyMaxMip;
            public bool hasGpuHierarchy;
            public int hierarchyGroupOffset;
            public int hierarchyGroupCount;
            public int spatialRootOffset;
            public int spatialRootCount;
            public int spatialNodeCount;
            public int spatialMaxDepth;
            public bool hasSpatialHierarchy;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct GpuSpatialNode
        {
            public Vector4 boundingSphere;
            public Vector4 lodSphere;
            public float maxParentError;
            public uint childStart;
            public uint childCount;
            public uint partRefStart;
            public uint partRefCount;
            public uint flags;
        }

        const uint kSpatialNodeLeaf = 1u << 0;
        const uint kSpatialNodeHasTerminal = 1u << 1;
        const int kSpatialLeafPartCount = 8;
        const int kSpatialBranchFactor = 8;

        [StructLayout(LayoutKind.Sequential)]
        struct GpuHierarchyGroup
        {
            public Vector4 boundingSphere;
            public float minLodError;
            public float maxParentLodError;
            public uint fineClusterStart;
            public uint fineClusterCount;
            public uint coarseClusterStart;
            public uint coarseClusterCount;
            public uint mipLevel;
            public uint flags;
            // Geometric error protects shape quality; these counts stop traversal
            // once the current cut is already at micropolygon density.
            public uint fineTriangleCount;
            public uint coarseTriangleCount;
            public float fineSurfaceArea;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct GpuHierarchyClusterRef
        {
            public uint geometryClusterIndex;
            public uint pageIndex;
            public uint pageClusterIndex;
            public uint refinementGroupIndex;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct GpuHierarchyResidencyRef
        {
            public uint instanceIndex;
            public uint groupIndex;
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
        const int kMaxShadowCasterPlanesPerCascade = 10;
        const int kGpuLayoutVersion = 17;
        // The scalar persistent protocol is quarantined after two runtime failures
        // (wave deadlock and early-worker exit). Re-admission requires a cooperative
        // group/wave scheduler, not a user-facing toggle.
        const bool kEnableUnsafePersistentTraversal = false;
        // clusterlod/UE-style regrouping produces a DAG, not a tree.  The former
        // root-to-leaf producer replacement path canonicalized a producer on its
        // first coarse cluster; sibling coarse clusters can belong to different
        // later groups, so that traversal dropped the rest of the producer and
        // made it reappear at another distance.  Production therefore uses the
        // spatial Part queue followed by the official per-cluster two-boundary
        // predicate (consumer error > threshold && producer error <= threshold).
        // Re-enable hierarchy acceleration only after it is rebuilt as a BVH over
        // groups/levels, never as a recursive producer tree.
        const bool kEnableAtomicHierarchyTraversal = false;
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

        public readonly struct HzbViewParameters
        {
            public readonly bool valid;
            public readonly Matrix4x4 worldToClip;
            public readonly Vector3 cameraPosition;
            public readonly Vector3 cameraForward;
            public readonly Vector2 projectionNdcScale;
            public readonly float zNear;

            public HzbViewParameters(
                Matrix4x4 worldToClip,
                Vector3 cameraPosition,
                Vector3 cameraForward,
                Vector2 projectionNdcScale,
                float zNear)
            {
                valid = true;
                this.worldToClip = worldToClip;
                this.cameraPosition = cameraPosition;
                this.cameraForward = cameraForward.sqrMagnitude > 1e-12f
                    ? cameraForward.normalized
                    : Vector3.forward;
                this.projectionNdcScale = new Vector2(
                    Mathf.Max(1e-6f, Mathf.Abs(projectionNdcScale.x)),
                    Mathf.Max(1e-6f, Mathf.Abs(projectionNdcScale.y)));
                this.zNear = Mathf.Max(1e-3f, zNear);
            }

            public static HzbViewParameters FromCamera(Camera camera)
            {
                if (camera == null)
                    return default;
                Matrix4x4 projection = GL.GetGPUProjectionMatrix(camera.projectionMatrix, true);
                return new HzbViewParameters(
                    projection * camera.worldToCameraMatrix,
                    camera.transform.position,
                    camera.transform.forward,
                    new Vector2(Mathf.Abs(projection.m00), Mathf.Abs(projection.m11)),
                    camera.nearClipPlane);
            }
        }

        ComputeShader shader;
        int kernelPartCull = -1;
        int kernelClusterCull = -1;
        int kernelClusterCullByPart = -1;
        int kernelFinalizeVisiblePartDispatch = -1;
        int kernelClusterCullVisibleParts = -1;
        int kernelInstanceCull = -1;
        int kernelPartCullVisibleInstances = -1;
        int kernelSeedSpatialRoots = -1;
        int kernelTraverseSpatialNodes = -1;
        int kernelUpdateHierarchyGroupResidency = -1;
        int kernelSeedHierarchyRoots = -1;
        int kernelFinalizeHierarchyDispatch = -1;
        int kernelTraverseHierarchy = -1;
        int kernelClearHierarchyPersistent = -1;
        int kernelSeedHierarchyPersistent = -1;
        int kernelTraverseHierarchyPersistent = -1;
        int kernelSeedShadowHierarchyPersistent = -1;
        int kernelTraverseShadowHierarchyPersistent = -1;
        int kernelSeedShadowHierarchyRoots = -1;
        int kernelTraverseShadowHierarchy = -1;
        int kernelShadowCullMultiCascadeByPart = -1;
        int kernelSeedShadowSpatialRoots = -1;
        int kernelTraverseShadowSpatialNodes = -1;
        int kernelClearClusterVisible = -1;
        int kernelClearSecondPassCandidates = -1;
        int kernelCopyUintBuffer = -1;
        int kernelClearCullStats = -1;
        int kernelOrMasksToVisible = -1;
        int kernelFinalizeRasterBinArgs = -1;
        int kernelRecoverHzbRejectedClusters = -1;

        ComputeBuffer partsBuffer;
        ComputeBuffer clustersBuffer;
        ComputeBuffer virtualPartsBuffer;
        ComputeBuffer virtualClustersBuffer;
        ComputeBuffer visiblePartAppendBuffer;
        ComputeBuffer visiblePartDispatchArgsBuffer;
        ComputeBuffer visibleInstanceAppendBuffer;
        ComputeBuffer visibleInstancePartDispatchArgsBuffer;
        ComputeBuffer spatialNodesBuffer;
        ComputeBuffer spatialPartRefsBuffer;
        ComputeBuffer visibleDrawClusterAppendBuffer;
        ComputeBuffer visibleDrawCountArgsBuffer;
        ComputeBuffer hzbRejectedClusterAppendBuffer;
        ComputeBuffer hzbRejectedDispatchArgsBuffer;
        ComputeBuffer hardwareRasterClusterAppendBuffer;
        ComputeBuffer softwareRasterClusterAppendBuffer;
        ComputeBuffer hardwareRasterCountArgsBuffer;
        ComputeBuffer softwareRasterCountArgsBuffer;
        ComputeBuffer softwareRasterDispatchArgsBuffer;
        ComputeBuffer hierarchyGroupsBuffer;
        ComputeBuffer hierarchyClusterRefsBuffer;
        ComputeBuffer hierarchyGroupResidencyBuffer;
        ComputeBuffer hierarchyResidencyRefsBuffer;
        ComputeBuffer hierarchyRootGroupsBuffer;
        readonly ComputeBuffer[] hierarchyQueues = new ComputeBuffer[2];
        readonly ComputeBuffer[] hierarchyDispatchArgs = new ComputeBuffer[2];
        readonly ComputeBuffer[] shadowSpatialQueues = new ComputeBuffer[2];
        readonly ComputeBuffer[] shadowSpatialDispatchArgs = new ComputeBuffer[2];
        ComputeBuffer hierarchyPersistentWorkBuffer;
        ComputeBuffer shadowHierarchyPersistentWorkBuffer;
        readonly ComputeBuffer[] shadowDrawClusterAppendBuffers = new ComputeBuffer[kMaxShadowCascades];
        readonly ComputeBuffer[] shadowDrawCountArgsBuffers = new ComputeBuffer[kMaxShadowCascades];
        readonly GraphicsBuffer[] shadowDrawArgsBuffers = new GraphicsBuffer[kMaxShadowCascades];
        ComputeBuffer partVisibleBuffer;
        ComputeBuffer visibleClusterAppendBuffer;
        ComputeBuffer visibleCountBuffer;
        ComputeBuffer clusterCandidateBuffer;
        ComputeBuffer clusterSceneIndexBuffer;
        ComputeBuffer geometryClusterPageIndexBuffer;
        ComputeBuffer sceneClusterFirstTriBuffer;
        ComputeBuffer sceneClusterTriCountBuffer;
        ComputeBuffer scenePageResidencyBuffer;
        ComputeBuffer scenePageRequestBuffer;
        int scenePageCount;
        bool sceneTrackPageUsage;
        bool sceneAllPagesResident = true;
        ComputeBuffer instanceDataBuffer;
        ComputeBuffer instanceVisibleBuffer;
        ComputeBuffer fallbackClusterVisibleBuffer;
        ComputeBuffer fallbackPrevVisibleBuffer;
        ComputeBuffer fallbackSecondPassBuffer;
        ComputeBuffer fallbackPass2DrawnBuffer;
        ComputeBuffer cullStatsBuffer;
        RenderTexture fallbackHzbTexture;
        bool cullStatsReadbackPending;
        int cullStatsReadbackEpoch;
        bool loggedCameraCullDiagnostics;

        readonly List<BatchSlot> slots = new List<BatchSlot>(32);
        readonly List<GeometrySlot> geometrySlots = new List<GeometrySlot>(16);
        readonly Dictionary<int, int> geometryIndexByMeshId = new Dictionary<int, int>(16);
        readonly List<NaniteGpuCullingBackend.GpuPartData> mergedParts = new List<NaniteGpuCullingBackend.GpuPartData>(8192);
        readonly List<NaniteGpuCullingBackend.GpuClusterData> mergedClusters = new List<NaniteGpuCullingBackend.GpuClusterData>(65536);
        readonly List<GpuVirtualPartRef> virtualParts = new List<GpuVirtualPartRef>(8192);
        readonly List<GpuVirtualClusterRef> virtualClusters = new List<GpuVirtualClusterRef>(65536);
        readonly List<GpuHierarchyGroup> hierarchyGroups = new List<GpuHierarchyGroup>(4096);
        readonly List<GpuHierarchyClusterRef> hierarchyClusterRefs = new List<GpuHierarchyClusterRef>(16384);
        readonly List<GpuHierarchyResidencyRef> hierarchyResidencyRefs = new List<GpuHierarchyResidencyRef>(16384);
        readonly List<uint> hierarchyRootGroups = new List<uint>(256);
        readonly List<GpuSpatialNode> spatialNodes = new List<GpuSpatialNode>(4096);
        readonly List<uint> spatialPartRefs = new List<uint>(4096);
        readonly List<NaniteGpuCullingBackend.GpuInstanceData> instanceData = new List<NaniteGpuCullingBackend.GpuInstanceData>(32);
        readonly List<int> dirtyInstanceDataIndices = new List<int>(32);
        readonly List<int> bvhStack = new List<int>(256);
        readonly Plane[] frustumPlaneObjects = new Plane[6];
        readonly Vector4[] frustumPlanes = new Vector4[6];
        readonly Vector4[] shadowBatchCasterPlanes =
            new Vector4[kMaxShadowCascades * kMaxShadowCasterPlanesPerCascade];
        Vector4 shadowBatchProjectionScales;
        Vector4 shadowBatchCasterPlaneCounts;
        readonly Vector4[] shadowBatchCullingSpheres = new Vector4[kMaxShadowCascades];
        readonly Dictionary<int, List<NaniteVisibleClusterRef>> visibleScratch = new Dictionary<int, List<NaniteVisibleClusterRef>>();

        NaniteGpuCullingBackend.GpuPartData[] partDataCpu;
        NaniteGpuCullingBackend.GpuClusterData[] clusterDataCpu;
        GpuVirtualPartRef[] virtualPartRefsCpu;
        GpuVirtualClusterRef[] virtualClusterRefsCpu;
        uint[] partVisibleCpu;
        uint[] clusterCandidatesCpu;
        uint[] clusterSceneIndexCpu;
        uint[] geometryClusterPageIndexCpu;
        uint[] visibleCountCpu = new uint[1];
        // 复用读回缓冲，避免每帧 new[] 触发 GC。
        GpuVisibleRef[] visibleRefsScratch = Array.Empty<GpuVisibleRef>();
        int partCount;
        int clusterCount;
        int geometryPartCount;
        int geometryClusterCount;
        int clusterCandidateCount;
        int instanceCount;
        bool virtualRefsExpanded;
        int hierarchyTraversalPassCount;
        int hierarchyPersistentCapacity;
        int spatialTraversalPassCount;
        int spatialQueueCapacity;
        int drawQueueCapacity;
        bool forceFullDrawQueueCapacity;
        bool spatialReady;
        bool hierarchyReady;
        uint hierarchyPersistentEpoch;
        int rebuildSignature;
        int registryRevision = -1;
        int sceneIndexSignature;
        int sceneClusterCountMapped;
        int lastVisibleDrawQueueFrame = -1;
        int lastVisibleDrawCameraId;
        int lastTraversalRasterBinsFrame = -1;
        int lastTraversalRasterBinsCameraId;
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
            geometryClusterPageIndexBuffer != null &&
            visiblePartAppendBuffer != null &&
            visiblePartDispatchArgsBuffer != null &&
            visibleInstanceAppendBuffer != null &&
            visibleInstancePartDispatchArgsBuffer != null &&
            visibleDrawClusterAppendBuffer != null &&
            visibleDrawCountArgsBuffer != null &&
            hzbRejectedClusterAppendBuffer != null &&
            hzbRejectedDispatchArgsBuffer != null &&
            hardwareRasterClusterAppendBuffer != null &&
            softwareRasterClusterAppendBuffer != null &&
            hardwareRasterCountArgsBuffer != null &&
            softwareRasterCountArgsBuffer != null &&
            softwareRasterDispatchArgsBuffer != null &&
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
            kernelSeedSpatialRoots = -1;
            kernelTraverseSpatialNodes = -1;
            kernelUpdateHierarchyGroupResidency = -1;
            kernelSeedHierarchyRoots = -1;
            kernelFinalizeHierarchyDispatch = -1;
            kernelTraverseHierarchy = -1;
            kernelClearHierarchyPersistent = -1;
            kernelSeedHierarchyPersistent = -1;
            kernelTraverseHierarchyPersistent = -1;
            kernelSeedShadowHierarchyPersistent = -1;
            kernelTraverseShadowHierarchyPersistent = -1;
            kernelSeedShadowHierarchyRoots = -1;
            kernelTraverseShadowHierarchy = -1;
            kernelShadowCullMultiCascadeByPart = -1;
            kernelSeedShadowSpatialRoots = -1;
            kernelTraverseShadowSpatialNodes = -1;
            kernelRecoverHzbRejectedClusters = -1;
            slots.Clear();
            geometrySlots.Clear();
            geometryIndexByMeshId.Clear();
            mergedParts.Clear();
            mergedClusters.Clear();
            virtualParts.Clear();
            virtualClusters.Clear();
            hierarchyGroups.Clear();
            hierarchyClusterRefs.Clear();
            hierarchyResidencyRefs.Clear();
            hierarchyRootGroups.Clear();
            spatialNodes.Clear();
            spatialPartRefs.Clear();
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
            virtualRefsExpanded = false;
            hierarchyTraversalPassCount = 0;
            hierarchyPersistentCapacity = 0;
            hierarchyReady = false;
            spatialTraversalPassCount = 0;
            spatialQueueCapacity = 0;
            drawQueueCapacity = 0;
            spatialReady = false;
            hierarchyPersistentEpoch = 0u;
            lastTransformUpdateFrame = -1;
            rebuildSignature = 0;
            registryRevision = -1;
            sceneIndexSignature = 0;
            sceneClusterCountMapped = 0;
            lastVisibleDrawQueueFrame = -1;
            lastVisibleDrawCameraId = 0;
            lastTraversalRasterBinsFrame = -1;
            lastTraversalRasterBinsCameraId = 0;
            lastShadowDrawQueueFrame = -1;
            lastShadowDrawCameraId = 0;
            lastShadowDrawCascadeMask = 0;
            LastClusterCandidateCount = 0;
            LastClusterCount = 0;
            LastCull1Drawn = 0;
            LastCull2Candidates = 0;
            LastCull2Drawn = 0;
            LastCullStatsReadbackFrame = -1;
            LastCullStatsCameraId = 0;
            LastUsedCpuCandidates = false;
            LastUsedPartDrivenClusterCull = false;
            LastUsedVisiblePartQueue = false;
            LastUsedVisibleInstanceQueue = false;
            LastUsedHierarchyQueue = false;
            LastUsedSpatialHierarchy = false;
            LastShadowUsedFusedBatch = false;
            LastShadowUsedHierarchyQueue = false;
            LastShadowUsedSpatialHierarchy = false;
        }

        public bool EnsureClusterSceneIndex(NaniteSceneVisibilityBufferBackend scene)
        {
            if (!IsReady || scene == null || !scene.IsReady || clusterDataCpu == null ||
                slots.Count == 0)
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
            sceneAllPagesResident = !pageRequestsEnabled || scene.AllPagesResident;

            int sig = unchecked(
                scene.GeometryGeneration * 397 +
                clusterCount * 31 +
                geometryClusterCount * 17 +
                slots.Count +
                scenePageCount * 13);
            if (clusterSceneIndexBuffer != null &&
                clusterSceneIndexCpu != null &&
                clusterSceneIndexCpu.Length == geometryClusterCount &&
                sceneIndexSignature == sig &&
                sceneClusterCountMapped == scene.ClusterCount)
                return true;

            clusterSceneIndexCpu = new uint[Mathf.Max(1, geometryClusterCount)];
            geometryClusterPageIndexCpu = new uint[Mathf.Max(1, geometryClusterCount)];
            Array.Fill(clusterSceneIndexCpu, uint.MaxValue);
            Array.Fill(geometryClusterPageIndexCpu, uint.MaxValue);
            int mapped = 0;
            for (int geometryIndex = 0; geometryIndex < geometrySlots.Count; geometryIndex++)
            {
                int representativeProxyId = 0;
                for (int slotIndex = 0; slotIndex < slots.Count; slotIndex++)
                {
                    if (slots[slotIndex].geometryIndex == geometryIndex)
                    {
                        representativeProxyId = slots[slotIndex].proxyId;
                        break;
                    }
                }
                if (representativeProxyId == 0)
                    continue;

                GeometrySlot geometry = geometrySlots[geometryIndex];
                int end = Mathf.Min(
                    clusterDataCpu.Length,
                    geometry.clusterOffset + geometry.clusterCount);
                for (int geometryClusterIndex = geometry.clusterOffset;
                     geometryClusterIndex < end;
                     geometryClusterIndex++)
                {
                    var cluster = clusterDataCpu[geometryClusterIndex];
                    if (scene.TryGetGlobalClusterIndex(
                            representativeProxyId,
                            cluster.pageIndex,
                            cluster.clusterIndex,
                            out int sceneIndex) &&
                        sceneIndex >= 0)
                    {
                        clusterSceneIndexCpu[geometryClusterIndex] = (uint)sceneIndex;
                        mapped++;
                    }
                    if (scene.TryGetGlobalPageId(
                            representativeProxyId,
                            cluster.pageIndex,
                            out int globalPageId) &&
                        globalPageId >= 0)
                    {
                        geometryClusterPageIndexCpu[geometryClusterIndex] = (uint)globalPageId;
                    }
                }
            }

            if (mapped <= 0)
                return false;

            clusterSceneIndexBuffer?.Release();
            clusterSceneIndexBuffer = new ComputeBuffer(clusterSceneIndexCpu.Length, sizeof(uint), ComputeBufferType.Structured);
            clusterSceneIndexBuffer.SetData(clusterSceneIndexCpu);
            geometryClusterPageIndexBuffer.SetData(geometryClusterPageIndexCpu);
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
                HzbViewParameters.FromCamera(camera),
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
            HzbViewParameters hzbView,
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
                    hzbView,
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
            if (enableCullStats && cullPassMode != CullPassMode.Pass1PrevVisible)
                ReadbackCullStats(camera);
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

        void ReadbackCullStats(Camera camera)
        {
            if (cullStatsBuffer == null || cullStatsReadbackPending)
                return;
            cullStatsReadbackPending = true;
            int epoch = cullStatsReadbackEpoch;
            int requestFrame = Time.frameCount;
            int requestCameraId = camera != null ? camera.GetInstanceID() : 0;
            AsyncGPUReadback.Request(cullStatsBuffer, request =>
            {
                if (epoch != cullStatsReadbackEpoch)
                    return;
                cullStatsReadbackPending = false;
                if (request.hasError)
                    return;
                var data = request.GetData<uint>();
                if (data.Length < 3)
                    return;
                LastCull1Drawn = (int)data[0];
                LastCull2Candidates = (int)data[1];
                LastCull2Drawn = (int)data[2];
                LastCullStatsReadbackFrame = requestFrame;
                LastCullStatsCameraId = requestCameraId;
            });
        }

        public bool Run(
            Camera camera,
            Texture hzbTexture,
            int hzbMipCount,
            bool useHzb,
            Dictionary<int, NaniteRuntimeSelection> outputsByProxyId)
        {
            return Run(
                camera,
                hzbTexture,
                hzbMipCount,
                useHzb,
                HzbViewParameters.FromCamera(camera),
                outputsByProxyId);
        }

        public bool Run(
            Camera camera,
            Texture hzbTexture,
            int hzbMipCount,
            bool useHzb,
            HzbViewParameters hzbView,
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
                    hzbView,
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
            HzbViewParameters hzbView,
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
            // Main pass consumes the previous-frame HZB; post pass consumes the
            // current-frame HZB. Pass2 recovers disocclusions rejected by Pass1.
            bool enableHzb = useHzb &&
                             hzbTexture != null &&
                             hzbMipCount > 0;
            SetSharedParams(
                cameraPos,
                camera.transform.forward,
                new Vector2(Mathf.Abs(gpuProj.m00), Mathf.Abs(gpuProj.m11)),
                projectionScale,
                zNear,
                worldToClip,
                camera.pixelWidth,
                camera.pixelHeight,
                enableHzb,
                hzbView);
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

            if (cullPassMode == CullPassMode.Pass1PrevVisible)
                hzbRejectedClusterAppendBuffer.SetCounterValue(0);

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
            bool enableVisibleDrawQueue = enableClusterVisibleWrite &&
                                          sceneClusterFirstTriBuffer != null &&
                                          sceneClusterTriCountBuffer != null;
            if (cullPassMode == CullPassMode.Pass2CandidatesHzb &&
                enableVisibleDrawQueue &&
                DispatchHzbRejectedRecovery(
                    camera,
                    boundHzbTexture,
                    visibleCountParam,
                    pass2Buf,
                    enableCullStats))
            {
                LastUsedCpuCandidates = false;
                LastUsedPartDrivenClusterCull = false;
                LastUsedVisiblePartQueue = false;
                LastUsedVisibleInstanceQueue = false;
                LastUsedHierarchyQueue = false;
                LastUsedSpatialHierarchy = false;
                return true;
            }
            bool useHierarchyQueue = kEnableAtomicHierarchyTraversal &&
                                     !hasCpuCandidates &&
                                     enableVisibleDrawQueue &&
                                     hierarchyReady &&
                                     kernelSeedHierarchyRoots >= 0 &&
                                     kernelFinalizeHierarchyDispatch >= 0 &&
                                     kernelTraverseHierarchy >= 0 &&
                                     kernelClearHierarchyPersistent >= 0 &&
                                     kernelSeedHierarchyPersistent >= 0 &&
                                     kernelTraverseHierarchyPersistent >= 0 &&
                                     hierarchyPersistentWorkBuffer != null &&
                                     hierarchyQueues[0] != null &&
                                     hierarchyQueues[1] != null;
            bool useSpatialQueue = !useHierarchyQueue &&
                                   !hasCpuCandidates &&
                                   usePartDriven &&
                                   enableVisibleDrawQueue &&
                                   spatialReady &&
                                   kernelSeedSpatialRoots >= 0 &&
                                   kernelFinalizeHierarchyDispatch >= 0 &&
                                   kernelTraverseSpatialNodes >= 0 &&
                                   hierarchyQueues[0] != null &&
                                   hierarchyQueues[1] != null &&
                                   partCount >= Mathf.Max(1, PartQueueMinVirtualParts);
            bool useVisiblePartQueue = !useHierarchyQueue &&
                                       (useSpatialQueue || usePartDriven) &&
                                       kernelClusterCullVisibleParts >= 0 &&
                                       kernelFinalizeVisiblePartDispatch >= 0 &&
                                       partCount >= Mathf.Max(1, PartQueueMinVirtualParts);
            bool useVisibleInstanceQueue = (useHierarchyQueue || useVisiblePartQueue) &&
                                           kernelPartCullVisibleInstances >= 0 &&
                                           (useHierarchyQueue || useSpatialQueue ||
                                            instanceCount >= Mathf.Max(1, InstanceQueueMinInstances));

            // A compact large-scene build deliberately owns no instance-expanded
            // compatibility refs. Never fall through to a kernel that interprets
            // the one-element placeholder as a virtual Part/Cluster array.
            if (!virtualRefsExpanded && !useSpatialQueue && !useHierarchyQueue)
                return false;

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

                // Every direct cut consumes the same atomic producer residency
                // contract. Populate it even when the cost model skips the spatial
                // queue (for example a single instance), otherwise the fallback
                // reads stale zeros and can select overlapping hierarchy levels.
                if (!sceneAllPagesResident)
                    DispatchHierarchyGroupResidency();

                if (!useHierarchyQueue)
                {
                    if (useSpatialQueue)
                    {
                        DispatchSpatialTraversal(boundHzbTexture);
                    }
                    else
                    {
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
                        shader.SetBuffer(activePartKernel, "_PartVisibleWrite", partVisibleBuffer);
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
                        // Part admission evaluates the same hierarchy group
                        // error-or-density predicate as cluster selection.
                        BindResidencyHierarchy(activePartKernel);
                        BindCullSharedBuffers(activePartKernel, visibleTarget, sceneIndexBuf, prevBuf, secondBuf, pass2Buf);
                        BindInstanceBuffers(activePartKernel);
                        BindHzbTexture(activePartKernel, boundHzbTexture);

                        if (useVisibleInstanceQueue)
                            shader.DispatchIndirect(activePartKernel, visibleInstancePartDispatchArgsBuffer, 0);
                        else
                        {
                            int partGroups = (partCount + kThreadGroupSize - 1) / kThreadGroupSize;
                            shader.Dispatch(activePartKernel, partGroups, 1, 1);
                        }
                    }

                    if (useVisiblePartQueue)
                    {
                        ComputeBuffer.CopyCount(visiblePartAppendBuffer, visiblePartDispatchArgsBuffer, 0);
                        shader.SetBuffer(
                            kernelFinalizeVisiblePartDispatch,
                            "_VisiblePartDispatchArgsWrite",
                            visiblePartDispatchArgsBuffer);
                        shader.Dispatch(kernelFinalizeVisiblePartDispatch, 1, 1, 1);
                    }
                }
            }

            visibleClusterAppendBuffer.SetCounterValue(0);
            if (enableVisibleDrawQueue && clearMask)
            {
                visibleDrawClusterAppendBuffer.SetCounterValue(0);
                hardwareRasterClusterAppendBuffer.SetCounterValue(0);
                softwareRasterClusterAppendBuffer.SetCounterValue(0);
            }
            // CSClusterCullVisibleParts is the lean direct draw-queue kernel. If
            // a caller needs the legacy visibility mask instead, keep the normal
            // part-driven kernel so mask consumers retain their previous ABI.
            bool useVisiblePartClusterQueue = !useHierarchyQueue &&
                                              useVisiblePartQueue &&
                                              enableVisibleDrawQueue;
            shader.SetInt("_PartCount", partCount);
            shader.SetInt("_ClusterCount", clusterCount);
            shader.SetInt("_ClusterCandidateCount", hasCpuCandidates ? clusterCandidateCount : 0);
            shader.SetInt("_UseClusterCandidates", hasCpuCandidates ? 1 : 0);
            shader.SetInt("_UseInstanceCull", hasCpuCandidates ? 0 : 1);
            shader.SetInt("_EnableVisibleAppend", enableVisibleAppend ? 1 : 0);
            shader.SetInt("_EnableVisibleDrawAppend", enableVisibleDrawQueue ? 1 : 0);
            shader.SetInt("_EnableRasterBins", enableVisibleDrawQueue && EnableTraversalRasterBins ? 1 : 0);
            shader.SetFloat("_SoftwareRasterThresholdPixels", Mathf.Max(1f, SoftwareRasterThresholdPixels));
            shader.SetInt("_EnableClusterVisibleWrite", enableClusterVisibleWrite ? 1 : 0);
            shader.SetInt("_EnableClusterBackfaceCull", EnableClusterBackfaceCulling ? 1 : 0);
            shader.SetInt("_SceneClusterCount", visibleCountParam);
            shader.SetInt("_CullPassMode", (int)cullPassMode);
            shader.SetInt("_HasPrevVisible", hasPrevVisible ? 1 : 0);
            shader.SetInt("_EnableCullStats", enableCullStats ? 1 : 0);
            ComputeBuffer fallbackSceneClusterData = GetFallbackClusterVisibleBuffer();
            if (useHierarchyQueue)
            {
                DispatchHierarchyTraversal(
                    visibleTarget,
                    sceneIndexBuf,
                    prevBuf,
                    secondBuf,
                    pass2Buf,
                    boundHzbTexture,
                    fallbackSceneClusterData);
            }
            else
            {
                int activeClusterKernel = useVisiblePartClusterQueue
                    ? kernelClusterCullVisibleParts
                    : (usePartDriven ? kernelClusterCullByPart : kernelClusterCull);
                shader.SetBuffer(activeClusterKernel, "_Clusters", clustersBuffer);
                shader.SetBuffer(activeClusterKernel, "_PartVisible", partVisibleBuffer);
                shader.SetBuffer(activeClusterKernel, "_VisibleClusters", visibleClusterAppendBuffer);
                shader.SetBuffer(activeClusterKernel, "_ClusterCandidates", clusterCandidateBuffer);
                shader.SetBuffer(activeClusterKernel, "_CullStats", cullStatsBuffer);
                shader.SetBuffer(activeClusterKernel, "_VisibleDrawClusters", visibleDrawClusterAppendBuffer);
                if (useVisiblePartClusterQueue)
                    BindRasterBinOutputs(activeClusterKernel);
                shader.SetBuffer(
                    activeClusterKernel,
                    "_SceneClusterFirstTri",
                    sceneClusterFirstTriBuffer != null ? sceneClusterFirstTriBuffer : fallbackSceneClusterData);
                shader.SetBuffer(
                    activeClusterKernel,
                    "_SceneClusterTriCount",
                    sceneClusterTriCountBuffer != null ? sceneClusterTriCountBuffer : fallbackSceneClusterData);
                if (useVisiblePartClusterQueue)
                {
                    shader.SetBuffer(activeClusterKernel, "_VisiblePartsIn", visiblePartAppendBuffer);
                    shader.SetBuffer(activeClusterKernel, "_VisiblePartDispatchArgs", visiblePartDispatchArgsBuffer);
                }
                BindGpuSceneRefs(activeClusterKernel);
                BindPageStreamingBuffers(activeClusterKernel);
                BindResidencyHierarchy(activeClusterKernel);
                BindCullSharedBuffers(activeClusterKernel, visibleTarget, sceneIndexBuf, prevBuf, secondBuf, pass2Buf);
                BindHzbRejectedOutput(activeClusterKernel);
                BindInstanceBuffers(activeClusterKernel);
                BindHzbTexture(activeClusterKernel, boundHzbTexture);

                int clusterWorkCount = usePartDriven
                    ? partCount
                    : (hasCpuCandidates ? clusterCandidateCount : clusterCount);
                if (useVisiblePartClusterQueue)
                {
                    shader.DispatchIndirect(activeClusterKernel, visiblePartDispatchArgsBuffer, 0);
                }
                else if (clusterWorkCount > 0)
                {
                    int clusterGroups = (clusterWorkCount + kThreadGroupSize - 1) / kThreadGroupSize;
                    shader.Dispatch(activeClusterKernel, clusterGroups, 1, 1);
                }
            }

            if (enableVisibleDrawQueue)
            {
                if (cullPassMode == CullPassMode.Pass1PrevVisible)
                {
                    ComputeBuffer.CopyCount(
                        hzbRejectedClusterAppendBuffer,
                        hzbRejectedDispatchArgsBuffer,
                        0);
                    shader.SetBuffer(
                        kernelFinalizeHierarchyDispatch,
                        "_HierarchyDispatchArgsWrite",
                        hzbRejectedDispatchArgsBuffer);
                    shader.Dispatch(kernelFinalizeHierarchyDispatch, 1, 1, 1);
                }
                ComputeBuffer.CopyCount(visibleDrawClusterAppendBuffer, visibleDrawCountArgsBuffer, 0);
                ComputeBuffer.CopyCount(hardwareRasterClusterAppendBuffer, hardwareRasterCountArgsBuffer, 0);
                ComputeBuffer.CopyCount(softwareRasterClusterAppendBuffer, softwareRasterCountArgsBuffer, 0);
                if (kernelFinalizeRasterBinArgs >= 0)
                {
                    shader.SetInt("_RasterDispatchGroupsX", 65535);
                    shader.SetBuffer(
                        kernelFinalizeRasterBinArgs,
                        "_SoftwareRasterCountArgs",
                        softwareRasterCountArgsBuffer);
                    shader.SetBuffer(
                        kernelFinalizeRasterBinArgs,
                        "_SoftwareRasterDispatchArgs",
                        softwareRasterDispatchArgsBuffer);
                    shader.Dispatch(kernelFinalizeRasterBinArgs, 1, 1, 1);
                }
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
            LastUsedHierarchyQueue = useHierarchyQueue;
            LastUsedSpatialHierarchy = useSpatialQueue;
            if (!loggedCameraCullDiagnostics &&
                camera.cameraType == CameraType.Game &&
                sceneAllPagesResident &&
                enableVisibleDrawQueue &&
                enableClusterVisibleWrite &&
                clearMask &&
                cullPassMode == CullPassMode.Pass1PrevVisible)
            {
                LogCameraCullDiagnosticsOnce(
                    visibleTarget,
                    visibleCountParam,
                    hasCpuCandidates,
                    usePartDriven,
                    useVisibleInstanceQueue,
                    useVisiblePartQueue,
                    useHierarchyQueue,
                    useSpatialQueue);
            }
            bool producedTraversalRasterBins =
                enableVisibleDrawQueue &&
                EnableTraversalRasterBins &&
                useVisiblePartClusterQueue;
            // A later compatibility/auxiliary dispatch in the same frame must
            // not erase the producer stamp. RenderGraph records several views
            // and passes before Formal consumes this queue; readiness is scoped
            // by both frame and camera below, so retaining the last successful
            // producer cannot alias another camera.
            if (producedTraversalRasterBins)
            {
                lastTraversalRasterBinsFrame = Time.frameCount;
                lastTraversalRasterBinsCameraId = camera.GetInstanceID();
            }

            return true;
        }

        void LogCameraCullDiagnosticsOnce(
            ComputeBuffer clusterVisible,
            int clusterVisibleCount,
            bool usedCpuCandidates,
            bool usedPartDriven,
            bool usedVisibleInstanceQueue,
            bool usedVisiblePartQueue,
            bool usedHierarchyQueue,
            bool usedSpatialQueue)
        {
            // Deliberately synchronous and one-shot: this is a diagnosis breadcrumb,
            // not a recurring profiler readback. Waiting until all pages are resident
            // keeps the result from describing only the initial streaming frame.
            loggedCameraCullDiagnostics = true;
            try
            {
                int instanceReadCount = Mathf.Min(instanceCount, instanceVisibleBuffer.count);
                var instanceVisible = new uint[instanceReadCount];
                if (instanceReadCount > 0)
                    instanceVisibleBuffer.GetData(instanceVisible, 0, 0, instanceReadCount);

                int partReadCount = Mathf.Min(partCount, partVisibleBuffer.count);
                var partVisible = new uint[partReadCount];
                if (partReadCount > 0)
                    partVisibleBuffer.GetData(partVisible, 0, 0, partReadCount);

                int clusterReadCount = Mathf.Min(clusterVisibleCount, clusterVisible.count);
                var clusterMask = new uint[clusterReadCount];
                if (clusterReadCount > 0)
                    clusterVisible.GetData(clusterMask, 0, 0, clusterReadCount);

                var drawCountArgs = new uint[4];
                visibleDrawCountArgsBuffer.GetData(drawCountArgs);
                var partDispatchArgs = new uint[4];
                visiblePartDispatchArgsBuffer.GetData(partDispatchArgs);
                int partQueueSampleCount = Mathf.Min(8, (int)partDispatchArgs[3]);
                var partQueueSample = new Vector2Int[partQueueSampleCount];
                if (partQueueSampleCount > 0)
                    visiblePartAppendBuffer.GetData(partQueueSample, 0, 0, partQueueSampleCount);

                if (!forceFullDrawQueueCapacity && drawCountArgs[0] >= (uint)drawQueueCapacity)
                {
                    // A valid replacement DAG cannot produce a frontier larger
                    // than its terminal leaves. Reaching the bound is treated as
                    // ambiguous metadata and schedules a full geometry-bound
                    // rebuild instead of allowing a silent append overflow.
                    forceFullDrawQueueCapacity = true;
                    rebuildSignature = -1;
                    registryRevision = -1;
                    Debug.LogError(
                        $"[Nanite][QueueCapacity] selected cut reached compact capacity " +
                        $"{drawCountArgs[0]}/{drawQueueCapacity}; scheduling full-bound rebuild.");
                }

                Debug.Log(
                    "[Nanite][CullDiag] settled camera cull: " +
                    $"instanceVisible={CountNonZero(instanceVisible)}/{instanceReadCount}, " +
                    $"partVisible={CountNonZero(partVisible)}/{partReadCount}, " +
                    $"clusterVisible={CountNonZero(clusterMask)}/{clusterReadCount}, " +
                    $"visiblePartQueue={partDispatchArgs[3]}, " +
                    $"partHead={string.Join(";", partQueueSample)}, " +
                    $"drawQueue={drawCountArgs[0]}, " +
                    $"cpuCandidates={usedCpuCandidates}:{clusterCandidateCount}, " +
                    $"partDriven={usedPartDriven}, " +
                    $"instanceQueue={usedVisibleInstanceQueue}, " +
                    $"partQueue={usedVisiblePartQueue}, " +
                    $"hierarchyQueue={usedHierarchyQueue}, " +
                    $"spatialQueue={usedSpatialQueue}.");
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    $"[Nanite][CullDiag] one-shot GPU count readback failed: {exception.Message}");
            }
        }

        static int CountNonZero(uint[] values)
        {
            int count = 0;
            for (int i = 0; i < values.Length; i++)
            {
                if (values[i] != 0u)
                    count++;
            }
            return count;
        }

        void BindRasterBinOutputs(int kernel)
        {
            shader.SetBuffer(kernel, "_HardwareRasterClusters", hardwareRasterClusterAppendBuffer);
            shader.SetBuffer(kernel, "_SoftwareRasterClusters", softwareRasterClusterAppendBuffer);
        }

        void BindGpuSceneRefs(int kernel)
        {
            shader.SetBuffer(kernel, "_VirtualParts", virtualPartsBuffer);
            shader.SetBuffer(kernel, "_VirtualClusters", virtualClustersBuffer);
            shader.SetBuffer(kernel, "_GeometryClusterPageIndex", geometryClusterPageIndexBuffer);
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
            shader.SetInt("_AllPagesResident", !enabled || sceneAllPagesResident ? 1 : 0);
            shader.SetBuffer(
                kernel,
                "_PageResidency",
                enabled ? scenePageResidencyBuffer : fallback);
            shader.SetBuffer(
                kernel,
                "_PageRequests",
                enabled ? scenePageRequestBuffer : fallback);
        }

        void BindResidencyHierarchy(int kernel)
        {
            shader.SetInt("_HierarchyGroupCount", hierarchyGroups.Count);
            shader.SetInt("_HierarchyRefCount", hierarchyClusterRefs.Count);
            shader.SetInt("_HierarchyGroupResidencyCount", HierarchyGroupResidencyCount);
            shader.SetBuffer(kernel, "_HierarchyGroups", hierarchyGroupsBuffer);
            shader.SetBuffer(kernel, "_HierarchyClusterRefs", hierarchyClusterRefsBuffer);
            shader.SetBuffer(kernel, "_HierarchyGroupResidency", hierarchyGroupResidencyBuffer);
        }

        int HierarchyGroupResidencyCount => Mathf.Max(1, hierarchyResidencyRefs.Count);

        void DispatchHierarchyGroupResidency()
        {
            if (kernelUpdateHierarchyGroupResidency < 0 || hierarchyGroupResidencyBuffer == null ||
                hierarchyGroups.Count <= 0 || instanceCount <= 0 || sceneAllPagesResident)
                return;

            int kernel = kernelUpdateHierarchyGroupResidency;
            shader.SetInt("_InstanceCount", instanceCount);
            BindGpuSceneRefs(kernel);
            BindInstanceBuffers(kernel);
            BindPageStreamingBuffers(kernel);
            // HierarchyFineWorkingSetResident validates geometry-local cluster
            // indices against the immutable GPU Scene cluster table before it
            // reads the global Page address.  This kernel is normally skipped by
            // all-resident runs, so the missing SRV only surfaced in constrained
            // streaming pools.
            shader.SetBuffer(kernel, "_Clusters", clustersBuffer);
            shader.SetInt("_HierarchyGroupCount", hierarchyGroups.Count);
            shader.SetInt("_HierarchyRefCount", hierarchyClusterRefs.Count);
            shader.SetInt("_HierarchyGroupResidencyCount", HierarchyGroupResidencyCount);
            shader.SetBuffer(kernel, "_HierarchyGroups", hierarchyGroupsBuffer);
            shader.SetBuffer(kernel, "_HierarchyClusterRefs", hierarchyClusterRefsBuffer);
            shader.SetBuffer(kernel, "_HierarchyResidencyRefs", hierarchyResidencyRefsBuffer);
            shader.SetBuffer(kernel, "_HierarchyGroupResidencyWrite", hierarchyGroupResidencyBuffer);
            shader.Dispatch(kernel, (HierarchyGroupResidencyCount + kThreadGroupSize - 1) / kThreadGroupSize, 1, 1);
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
            if (cullStatsBuffer != null)
                shader.SetBuffer(kernel, "_CullStats", cullStatsBuffer);
            if (partsBuffer != null)
                shader.SetBuffer(kernel, "_Parts", partsBuffer);
            if (clustersBuffer != null)
                shader.SetBuffer(kernel, "_Clusters", clustersBuffer);
        }

        void BindHzbRejectedOutput(int kernel)
        {
            shader.SetBuffer(kernel, "_HzbRejectedClustersOut", hzbRejectedClusterAppendBuffer);
        }

        bool DispatchHzbRejectedRecovery(
            Camera camera,
            Texture hzbTexture,
            int sceneClusterCount,
            ComputeBuffer pass2Drawn,
            bool enableCullStats)
        {
            if (camera == null || hzbTexture == null ||
                kernelRecoverHzbRejectedClusters < 0 ||
                kernelFinalizeRasterBinArgs < 0 ||
                hzbRejectedClusterAppendBuffer == null ||
                hzbRejectedDispatchArgsBuffer == null)
                return false;

            int kernel = kernelRecoverHzbRejectedClusters;
            shader.SetInt("_ClusterCount", clusterCount);
            shader.SetInt("_SceneClusterCount", sceneClusterCount);
            shader.SetInt("_EnableVisibleDrawAppend", 1);
            shader.SetInt("_EnableRasterBins", EnableTraversalRasterBins ? 1 : 0);
            shader.SetFloat("_SoftwareRasterThresholdPixels", Mathf.Max(1f, SoftwareRasterThresholdPixels));
            shader.SetInt("_EnableCullStats", enableCullStats ? 1 : 0);
            shader.SetBuffer(kernel, "_HzbRejectedClusters", hzbRejectedClusterAppendBuffer);
            shader.SetBuffer(kernel, "_HzbRejectedDispatchArgs", hzbRejectedDispatchArgsBuffer);
            shader.SetBuffer(kernel, "_Clusters", clustersBuffer);
            shader.SetBuffer(kernel, "_SceneClusterFirstTri", sceneClusterFirstTriBuffer);
            shader.SetBuffer(kernel, "_SceneClusterTriCount", sceneClusterTriCountBuffer);
            shader.SetBuffer(kernel, "_VisibleDrawClusters", visibleDrawClusterAppendBuffer);
            shader.SetBuffer(kernel, "_Pass2Drawn", pass2Drawn);
            shader.SetBuffer(kernel, "_CullStats", cullStatsBuffer);
            BindRasterBinOutputs(kernel);
            BindInstanceBuffers(kernel);
            BindHzbTexture(kernel, hzbTexture);
            shader.DispatchIndirect(kernel, hzbRejectedDispatchArgsBuffer, 0);

            ComputeBuffer.CopyCount(visibleDrawClusterAppendBuffer, visibleDrawCountArgsBuffer, 0);
            ComputeBuffer.CopyCount(hardwareRasterClusterAppendBuffer, hardwareRasterCountArgsBuffer, 0);
            ComputeBuffer.CopyCount(softwareRasterClusterAppendBuffer, softwareRasterCountArgsBuffer, 0);
            shader.SetInt("_RasterDispatchGroupsX", 65535);
            shader.SetBuffer(
                kernelFinalizeRasterBinArgs,
                "_SoftwareRasterCountArgs",
                softwareRasterCountArgsBuffer);
            shader.SetBuffer(
                kernelFinalizeRasterBinArgs,
                "_SoftwareRasterDispatchArgs",
                softwareRasterDispatchArgsBuffer);
            shader.Dispatch(kernelFinalizeRasterBinArgs, 1, 1, 1);

            lastVisibleDrawQueueFrame = Time.frameCount;
            lastVisibleDrawCameraId = camera.GetInstanceID();
            return true;
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

            dirtyInstanceDataIndices.Clear();
            for (int s = 0; s < slots.Count; s++)
            {
                var proxy = slots[s].proxy;
                if (proxy == null || !proxy.isActiveAndEnabled || !proxy.NaniteRenderingActive)
                    continue;

                Transform proxyTransform = proxy.transform;
                var current = instanceData[s];
                float lodErr = LodErrorPixelsOverride > 0f ? LodErrorPixelsOverride : proxy.lodErrorPixels;
                if (!proxyTransform.hasChanged &&
                    Mathf.Approximately(current.lodErrorPixels, lodErr))
                {
                    continue;
                }

                Matrix4x4 m = proxyTransform.localToWorldMatrix;
                Vector3 lossyScale = proxyTransform.lossyScale;
                float maxScale = Mathf.Max(
                    Mathf.Abs(lossyScale.x),
                    Mathf.Abs(lossyScale.y),
                    Mathf.Abs(lossyScale.z));
                if (current.localToWorld != m ||
                    !Mathf.Approximately(current.maxScale, maxScale) ||
                    !Mathf.Approximately(current.lodErrorPixels, lodErr))
                {
                    current.localToWorld = m;
                    current.maxScale = maxScale;
                    current.lodErrorPixels = lodErr;
                    instanceData[s] = current;
                    dirtyInstanceDataIndices.Add(s);
                }
                proxyTransform.hasChanged = false;
            }

            if (dirtyInstanceDataIndices.Count == 0)
                return;
            UploadDirtyListRanges(instanceDataBuffer, instanceData, dirtyInstanceDataIndices);
        }

        static void UploadDirtyListRanges<T>(
            ComputeBuffer destination,
            List<T> source,
            List<int> sortedDirtyIndices)
            where T : struct
        {
            if (destination == null || source == null || sortedDirtyIndices == null ||
                sortedDirtyIndices.Count == 0)
            {
                return;
            }

            int runStart = sortedDirtyIndices[0];
            int runEnd = runStart + 1;
            for (int i = 1; i <= sortedDirtyIndices.Count; i++)
            {
                if (i < sortedDirtyIndices.Count && sortedDirtyIndices[i] == runEnd)
                {
                    runEnd++;
                    continue;
                }
                destination.SetData(source, runStart, runStart, runEnd - runStart);
                if (i < sortedDirtyIndices.Count)
                {
                    runStart = sortedDirtyIndices[i];
                    runEnd = runStart + 1;
                }
            }
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
            hierarchyGroups.Clear();
            hierarchyClusterRefs.Clear();
            hierarchyRootGroups.Clear();
            spatialNodes.Clear();
            spatialPartRefs.Clear();
            instanceData.Clear();
            bool allGeometryHasHierarchy = true;
            int maxHierarchyMip = 0;
            bool allGeometryHasSpatialHierarchy = true;
            int maxSpatialDepth = 0;
            int totalSpatialWorkCapacity = 0;
            int virtualHierarchyRefCapacity = 0;
            int logicalPartCount = 0;
            int logicalClusterCount = 0;
            int totalDrawQueueCapacity = 0;
            int estimatedPartRefs = 0;
            for (int proxyIndex = 0; proxyIndex < proxies.Count; proxyIndex++)
            {
                NaniteRuntimeProxy candidate = proxies[proxyIndex];
                if (!IsBatchableProxy(candidate))
                    continue;
                estimatedPartRefs = checked(
                    estimatedPartRefs + CountMeshParts(candidate.naniteMesh));
            }
            // Small scenes retain the compatibility refs used by CPU/readback
            // tools. Large scenes commit to the NVIDIA-style direct spatial
            // queue and never materialize instance x geometry references.
            virtualRefsExpanded =
                estimatedPartRefs < Mathf.Max(1, PartQueueMinVirtualParts);

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

                    bool hasGpuHierarchy = TryAppendGeometryHierarchy(
                        mesh,
                        geometryClusters,
                        out int hierarchyRootOffset,
                        out int hierarchyRootCount,
                        out int hierarchyMaxMip,
                        out int hierarchyGroupOffset);
                    int hierarchyGroupCount = hasGpuHierarchy
                        ? hierarchyGroups.Count - hierarchyGroupOffset
                        : 0;
                    if (hasGpuHierarchy)
                    {
                        // The spatial path visits immutable clusters directly. Attach the
                        // refinement group once at scene build so a missing fine Page can
                        // select this resident coarse cluster without switching schedulers.
                        for (int refIndex = 0; refIndex < mesh.hierarchyClusterRefs.Length; refIndex++)
                        {
                            NaniteHierarchyClusterRef clusterRef = mesh.hierarchyClusterRefs[refIndex];
                            if (clusterRef.refinementGroupIndex < 0 ||
                                (uint)clusterRef.geometryClusterIndex >= (uint)geometryClusters)
                                continue;
                            int mergedClusterIndex = geometryClusterOffset + clusterRef.geometryClusterIndex;
                            NaniteGpuCullingBackend.GpuClusterData cluster = mergedClusters[mergedClusterIndex];
                            cluster.refinementGroupIndex = (uint)(hierarchyGroupOffset + clusterRef.refinementGroupIndex);
                            mergedClusters[mergedClusterIndex] = cluster;
                        }

                        // Parts are Bake-aligned to one consumer group. Publish
                        // that immutable relation once so the direct spatial cut
                        // can evaluate the same group predicate on both sides of
                        // a DAG replacement edge. This avoids density heuristics
                        // independently dropping fine clusters and opening holes.
                        for (int groupIndex = 0; groupIndex < mesh.hierarchyGroups.Length; groupIndex++)
                        {
                            NaniteHierarchyGroup group = mesh.hierarchyGroups[groupIndex];
                            int fineEnd = group.fineClusterStart + group.fineClusterCount;
                            for (int refIndex = group.fineClusterStart; refIndex < fineEnd; refIndex++)
                            {
                                NaniteHierarchyClusterRef clusterRef = mesh.hierarchyClusterRefs[refIndex];
                                if ((uint)clusterRef.geometryClusterIndex >= (uint)geometryClusters)
                                    continue;
                                int mergedClusterIndex = geometryClusterOffset + clusterRef.geometryClusterIndex;
                                int mergedPartIndex = mergedClusters[mergedClusterIndex].partIndex;
                                if ((uint)mergedPartIndex >= (uint)mergedParts.Count)
                                    continue;
                                NaniteGpuCullingBackend.GpuPartData part = mergedParts[mergedPartIndex];
                                uint globalGroupIndex = (uint)(hierarchyGroupOffset + groupIndex);
                                if (part.consumerGroupIndex != uint.MaxValue &&
                                    part.consumerGroupIndex != globalGroupIndex)
                                {
                                    throw new InvalidOperationException(
                                        $"Nanite Part {mergedPartIndex} crosses consumer groups " +
                                        $"{part.consumerGroupIndex} and {globalGroupIndex}.");
                                }
                                part.consumerGroupIndex = globalGroupIndex;
                                mergedParts[mergedPartIndex] = part;
                            }
                        }
                    }
                    int spatialNodeStart = spatialNodes.Count;
                    bool hasSpatialHierarchy = TryAppendGeometrySpatialHierarchy(
                        geometryPartOffset,
                        geometryParts,
                        out int spatialRootOffset,
                        out int spatialRootCount,
                        out int spatialMaxDepth);
                    int maxCutClusterCount = hasGpuHierarchy
                        ? CountTerminalGeometryClusters(
                            mergedClusters,
                            geometryClusterOffset,
                            geometryClusters)
                        : geometryClusters;

                    var geometry = new GeometrySlot
                    {
                        mesh = mesh,
                        partOffset = geometryPartOffset,
                        clusterOffset = geometryClusterOffset,
                        partCount = geometryParts,
                        clusterCount = geometryClusters,
                        maxCutClusterCount = Mathf.Max(1, maxCutClusterCount),
                        pagePartBase = geometryPagePartBase,
                        pagePartCount = geometryPagePartCount,
                        bounds = ResolveMeshBounds(mesh, geometryPartOffset, geometryParts),
                        hierarchyRootOffset = hierarchyRootOffset,
                        hierarchyRootCount = hierarchyRootCount,
                        hierarchyMaxMip = hierarchyMaxMip,
                        hasGpuHierarchy = hasGpuHierarchy,
                        hierarchyGroupOffset = hierarchyGroupOffset,
                        hierarchyGroupCount = hierarchyGroupCount,
                        spatialRootOffset = spatialRootOffset,
                        spatialRootCount = spatialRootCount,
                        spatialNodeCount = hasSpatialHierarchy
                            ? spatialNodes.Count - spatialNodeStart
                            : 0,
                        spatialMaxDepth = spatialMaxDepth,
                        hasSpatialHierarchy = hasSpatialHierarchy
                    };
                    geometryIndex = geometrySlots.Count;
                    geometrySlots.Add(geometry);
                    geometryIndexByMeshId.Add(meshId, geometryIndex);
                }

                GeometrySlot geometrySlot = geometrySlots[geometryIndex];
                allGeometryHasHierarchy &= geometrySlot.hasGpuHierarchy;
                maxHierarchyMip = Mathf.Max(maxHierarchyMip, geometrySlot.hierarchyMaxMip);
                allGeometryHasSpatialHierarchy &= geometrySlot.hasSpatialHierarchy;
                totalSpatialWorkCapacity = checked(
                    totalSpatialWorkCapacity + geometrySlot.spatialNodeCount);
                maxSpatialDepth = Mathf.Max(maxSpatialDepth, geometrySlot.spatialMaxDepth);
                if (geometrySlot.hasGpuHierarchy && geometrySlot.mesh?.hierarchyClusterRefs != null)
                {
                    virtualHierarchyRefCapacity = checked(
                        virtualHierarchyRefCapacity + geometrySlot.mesh.hierarchyClusterRefs.Length);
                }
                int instanceIndex = slots.Count;
                int virtualPartOffset = logicalPartCount;
                int virtualClusterOffset = logicalClusterCount;
                logicalPartCount = checked(logicalPartCount + geometrySlot.partCount);
                logicalClusterCount = checked(logicalClusterCount + geometrySlot.clusterCount);
                totalDrawQueueCapacity = checked(
                    totalDrawQueueCapacity + geometrySlot.maxCutClusterCount);
                int hierarchyResidencyOffset = hierarchyResidencyRefs.Count;
                for (int localGroup = 0; localGroup < geometrySlot.hierarchyGroupCount; localGroup++)
                {
                    hierarchyResidencyRefs.Add(new GpuHierarchyResidencyRef
                    {
                        instanceIndex = (uint)instanceIndex,
                        groupIndex = (uint)(geometrySlot.hierarchyGroupOffset + localGroup)
                    });
                }

                if (virtualRefsExpanded)
                {
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
                    partCount = (uint)geometrySlot.partCount,
                    clusterOffset = (uint)virtualClusterOffset,
                    geometryPartOffset = (uint)geometrySlot.partOffset,
                    geometryClusterOffset = (uint)geometrySlot.clusterOffset,
                    hierarchyRootOffset = (uint)Mathf.Max(0, geometrySlot.hierarchyRootOffset),
                    hierarchyRootCount = (uint)Mathf.Max(0, geometrySlot.hierarchyRootCount),
                    hierarchyFlags = geometrySlot.hasGpuHierarchy ? 1u : 0u,
                    hierarchyGroupOffset = (uint)Mathf.Max(0, geometrySlot.hierarchyGroupOffset),
                    hierarchyGroupCount = (uint)Mathf.Max(0, geometrySlot.hierarchyGroupCount),
                    hierarchyResidencyOffset = (uint)hierarchyResidencyOffset,
                    spatialRootOffset = (uint)Mathf.Max(0, geometrySlot.spatialRootOffset),
                    spatialRootCount = (uint)Mathf.Max(0, geometrySlot.spatialRootCount),
                    spatialFlags = geometrySlot.hasSpatialHierarchy ? 1u : 0u,
                    spatialReserved = SupportsClusterConeCulling(proxy) ? 1u : 0u
                });
            }

            geometryPartCount = mergedParts.Count;
            geometryClusterCount = mergedClusters.Count;
            partCount = logicalPartCount;
            clusterCount = logicalClusterCount;
            instanceCount = slots.Count;
            hierarchyReady = allGeometryHasHierarchy &&
                             hierarchyGroups.Count > 0 &&
                             hierarchyClusterRefs.Count > 0 &&
                             hierarchyRootGroups.Count > 0;
            // Root seed plus one replacement step per mip. One guard pass drains terminal
            // refs without relying on a CPU-visible queue count.
            hierarchyTraversalPassCount = hierarchyReady
                ? Mathf.Clamp(maxHierarchyMip + 2, 2, 32)
                : 0;
            hierarchyPersistentCapacity = hierarchyReady
                ? Mathf.Max(clusterCount, virtualHierarchyRefCapacity)
                : 0;
            spatialReady = allGeometryHasSpatialHierarchy &&
                           spatialNodes.Count > 0 &&
                           spatialPartRefs.Count == geometryPartCount;
            spatialTraversalPassCount = spatialReady
                ? Mathf.Clamp(maxSpatialDepth, 1, 32)
                : 0;
            spatialQueueCapacity = spatialReady
                ? Mathf.Max(1, totalSpatialWorkCapacity)
                : Mathf.Max(1, clusterCount);
            drawQueueCapacity = Mathf.Max(
                1,
                forceFullDrawQueueCapacity ? clusterCount : totalDrawQueueCapacity);
            if (geometryPartCount == 0 || geometryClusterCount == 0 ||
                partCount == 0 || clusterCount == 0 || instanceCount == 0)
                return false;

            partDataCpu = mergedParts.ToArray();
            clusterDataCpu = mergedClusters.ToArray();
            virtualPartRefsCpu = virtualRefsExpanded
                ? virtualParts.ToArray()
                : new GpuVirtualPartRef[1];
            virtualClusterRefsCpu = virtualRefsExpanded
                ? virtualClusters.ToArray()
                : new GpuVirtualClusterRef[1];
            partVisibleCpu = new uint[virtualRefsExpanded ? partCount : 1];
            clusterCandidatesCpu = new uint[virtualRefsExpanded ? clusterCount : 1];
            clusterSceneIndexCpu = null;
            geometryClusterPageIndexCpu = new uint[Mathf.Max(1, geometryClusterCount)];
            Array.Fill(geometryClusterPageIndexCpu, uint.MaxValue);
            sceneIndexSignature = 0;
            sceneClusterCountMapped = 0;

            partsBuffer = new ComputeBuffer(geometryPartCount, Marshal.SizeOf<NaniteGpuCullingBackend.GpuPartData>());
            clustersBuffer = new ComputeBuffer(geometryClusterCount, Marshal.SizeOf<NaniteGpuCullingBackend.GpuClusterData>());
            virtualPartsBuffer = new ComputeBuffer(virtualPartRefsCpu.Length, Marshal.SizeOf<GpuVirtualPartRef>());
            virtualClustersBuffer = new ComputeBuffer(virtualClusterRefsCpu.Length, Marshal.SizeOf<GpuVirtualClusterRef>());
            geometryClusterPageIndexBuffer = new ComputeBuffer(
                Mathf.Max(1, geometryClusterCount),
                sizeof(uint),
                ComputeBufferType.Structured);
            if (spatialReady)
            {
                spatialNodesBuffer = new ComputeBuffer(
                    spatialNodes.Count,
                    Marshal.SizeOf<GpuSpatialNode>());
                spatialPartRefsBuffer = new ComputeBuffer(
                    spatialPartRefs.Count,
                    sizeof(uint));
            }
            // The fast spatial cluster kernel also consumes refinement metadata for
            // residency fallback, so typed one-element buffers remain bound even for
            // legacy meshes without a hierarchy.
            hierarchyGroupsBuffer = new ComputeBuffer(
                Mathf.Max(1, hierarchyGroups.Count),
                Marshal.SizeOf<GpuHierarchyGroup>());
            hierarchyClusterRefsBuffer = new ComputeBuffer(
                Mathf.Max(1, hierarchyClusterRefs.Count),
                Marshal.SizeOf<GpuHierarchyClusterRef>());
            hierarchyGroupResidencyBuffer = new ComputeBuffer(
                HierarchyGroupResidencyCount,
                sizeof(uint));
            hierarchyResidencyRefsBuffer = new ComputeBuffer(
                Mathf.Max(1, hierarchyResidencyRefs.Count),
                Marshal.SizeOf<GpuHierarchyResidencyRef>());
            if (hierarchyReady)
            {
                hierarchyRootGroupsBuffer = new ComputeBuffer(
                    hierarchyRootGroups.Count,
                    sizeof(uint));
            }
            if (hierarchyReady && kEnableUnsafePersistentTraversal)
            {
                // Eight state words followed by 16-byte task records. viewMask is
                // unused for camera traversal and carries four cascade bits for shadows.
                // includes independent reserved/published frontiers; do not fold
                // them back into one counter or consumers can claim incomplete work.
                int persistentWordCount = checked(8 + hierarchyPersistentCapacity * 4);
                hierarchyPersistentWorkBuffer = new ComputeBuffer(
                    persistentWordCount,
                    sizeof(uint),
                    ComputeBufferType.Raw);
                // Shadows may be recorded/executed on a different RenderGraph queue.
                // A separate arena prevents camera and four-view traversal from aliasing.
                shadowHierarchyPersistentWorkBuffer = new ComputeBuffer(
                    persistentWordCount,
                    sizeof(uint),
                    ComputeBufferType.Raw);
            }
            if (spatialReady || hierarchyReady)
            {
                for (int queueIndex = 0; queueIndex < 2; queueIndex++)
                {
                    hierarchyQueues[queueIndex] = new ComputeBuffer(
                        spatialReady ? spatialQueueCapacity : clusterCount,
                        sizeof(uint) * 2,
                        ComputeBufferType.Append);
                    hierarchyDispatchArgs[queueIndex] = new ComputeBuffer(
                        4,
                        sizeof(uint),
                        ComputeBufferType.IndirectArguments);
                }
            }
            if (spatialReady || hierarchyReady)
            {
                for (int queueIndex = 0; queueIndex < 2; queueIndex++)
                {
                    shadowSpatialQueues[queueIndex] = new ComputeBuffer(
                        spatialReady ? spatialQueueCapacity : clusterCount,
                        sizeof(uint) * 3,
                        ComputeBufferType.Append);
                    shadowSpatialDispatchArgs[queueIndex] = new ComputeBuffer(
                        4,
                        sizeof(uint),
                        ComputeBufferType.IndirectArguments);
                }
            }
            visiblePartAppendBuffer = new ComputeBuffer(partCount, sizeof(uint) * 2, ComputeBufferType.Append);
            visiblePartDispatchArgsBuffer = new ComputeBuffer(4, sizeof(uint), ComputeBufferType.IndirectArguments);
            visibleInstanceAppendBuffer = new ComputeBuffer(instanceCount, sizeof(uint), ComputeBufferType.Append);
            visibleInstancePartDispatchArgsBuffer = new ComputeBuffer(4, sizeof(uint), ComputeBufferType.IndirectArguments);
            visibleDrawClusterAppendBuffer = new ComputeBuffer(drawQueueCapacity, sizeof(uint) * 3, ComputeBufferType.Append);
            visibleDrawCountArgsBuffer = new ComputeBuffer(4, sizeof(uint), ComputeBufferType.IndirectArguments);
            hzbRejectedClusterAppendBuffer = new ComputeBuffer(drawQueueCapacity, sizeof(uint) * 3, ComputeBufferType.Append);
            hzbRejectedDispatchArgsBuffer = new ComputeBuffer(4, sizeof(uint), ComputeBufferType.IndirectArguments);
            hardwareRasterClusterAppendBuffer = new ComputeBuffer(drawQueueCapacity, sizeof(uint) * 3, ComputeBufferType.Append);
            softwareRasterClusterAppendBuffer = new ComputeBuffer(drawQueueCapacity, sizeof(uint) * 3, ComputeBufferType.Append);
            hardwareRasterCountArgsBuffer = new ComputeBuffer(4, sizeof(uint), ComputeBufferType.IndirectArguments);
            softwareRasterCountArgsBuffer = new ComputeBuffer(4, sizeof(uint), ComputeBufferType.IndirectArguments);
            softwareRasterDispatchArgsBuffer = new ComputeBuffer(4, sizeof(uint), ComputeBufferType.IndirectArguments);
            for (int cascadeIndex = 0; cascadeIndex < kMaxShadowCascades; cascadeIndex++)
            {
                shadowDrawClusterAppendBuffers[cascadeIndex] =
                    new ComputeBuffer(drawQueueCapacity, sizeof(uint) * 3, ComputeBufferType.Append);
                shadowDrawCountArgsBuffers[cascadeIndex] =
                    new ComputeBuffer(4, sizeof(uint), ComputeBufferType.IndirectArguments);
                shadowDrawArgsBuffers[cascadeIndex] = new GraphicsBuffer(
                    GraphicsBuffer.Target.IndirectArguments | GraphicsBuffer.Target.Structured,
                    4,
                    sizeof(uint));
            }
            partVisibleBuffer = new ComputeBuffer(virtualRefsExpanded ? partCount : 1, sizeof(uint));
            visibleClusterAppendBuffer = new ComputeBuffer(virtualRefsExpanded ? clusterCount : 1, Marshal.SizeOf<GpuVisibleRef>(), ComputeBufferType.Append);
            visibleCountBuffer = new ComputeBuffer(1, sizeof(uint), ComputeBufferType.Raw);
            clusterCandidateBuffer = new ComputeBuffer(virtualRefsExpanded ? clusterCount : 1, sizeof(uint));
            instanceDataBuffer = new ComputeBuffer(instanceCount, Marshal.SizeOf<NaniteGpuCullingBackend.GpuInstanceData>());
            instanceVisibleBuffer = new ComputeBuffer(instanceCount, sizeof(uint));
            partsBuffer.SetData(partDataCpu);
            clustersBuffer.SetData(clusterDataCpu);
            virtualPartsBuffer.SetData(virtualPartRefsCpu);
            virtualClustersBuffer.SetData(virtualClusterRefsCpu);
            geometryClusterPageIndexBuffer.SetData(geometryClusterPageIndexCpu);
            if (spatialReady)
            {
                spatialNodesBuffer.SetData(spatialNodes);
                spatialPartRefsBuffer.SetData(spatialPartRefs);
            }
            if (hierarchyGroups.Count > 0)
                hierarchyGroupsBuffer.SetData(hierarchyGroups);
            else
                hierarchyGroupsBuffer.SetData(new GpuHierarchyGroup[1]);
            if (hierarchyClusterRefs.Count > 0)
                hierarchyClusterRefsBuffer.SetData(hierarchyClusterRefs);
            else
                hierarchyClusterRefsBuffer.SetData(new GpuHierarchyClusterRef[1]);
            if (hierarchyResidencyRefs.Count > 0)
                hierarchyResidencyRefsBuffer.SetData(hierarchyResidencyRefs);
            else
                hierarchyResidencyRefsBuffer.SetData(new GpuHierarchyResidencyRef[1]);
            if (hierarchyReady)
            {
                hierarchyRootGroupsBuffer.SetData(hierarchyRootGroups);
            }
            if (spatialReady || hierarchyReady)
            {
                for (int queueIndex = 0; queueIndex < 2; queueIndex++)
                {
                    hierarchyQueues[queueIndex].SetCounterValue(0);
                    hierarchyDispatchArgs[queueIndex].SetData(new uint[] { 0u, 1u, 1u, 0u });
                }
            }
            if (spatialReady)
            {
                for (int queueIndex = 0; queueIndex < 2; queueIndex++)
                {
                    shadowSpatialQueues[queueIndex].SetCounterValue(0);
                    shadowSpatialDispatchArgs[queueIndex].SetData(new uint[] { 0u, 1u, 1u, 0u });
                }
            }
            visiblePartAppendBuffer.SetCounterValue(0);
            visiblePartDispatchArgsBuffer.SetData(new uint[] { 0u, 1u, 1u, 0u });
            visibleInstanceAppendBuffer.SetCounterValue(0);
            visibleInstancePartDispatchArgsBuffer.SetData(new uint[] { 0u, 1u, 1u, 0u });
            visibleDrawClusterAppendBuffer.SetCounterValue(0);
            visibleDrawCountArgsBuffer.SetData(new uint[] { 0u, 1u, 1u, 0u });
            hzbRejectedClusterAppendBuffer.SetCounterValue(0);
            hzbRejectedDispatchArgsBuffer.SetData(new uint[] { 0u, 1u, 1u, 0u });
            hardwareRasterClusterAppendBuffer.SetCounterValue(0);
            softwareRasterClusterAppendBuffer.SetCounterValue(0);
            hardwareRasterCountArgsBuffer.SetData(new uint[] { 0u, 1u, 1u, 0u });
            softwareRasterCountArgsBuffer.SetData(new uint[] { 0u, 1u, 1u, 0u });
            softwareRasterDispatchArgsBuffer.SetData(new uint[] { 0u, 1u, 1u, 0u });
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

        static bool SupportsClusterConeCulling(NaniteRuntimeProxy proxy)
        {
            if (proxy == null)
                return false;

            Vector3 scale = proxy.transform.lossyScale;
            float sx = Mathf.Abs(scale.x);
            float sy = Mathf.Abs(scale.y);
            float sz = Mathf.Abs(scale.z);
            float minScale = Mathf.Min(sx, Mathf.Min(sy, sz));
            float maxScale = Mathf.Max(sx, Mathf.Max(sy, sz));
            if (minScale <= 1e-6f || maxScale / minScale > 1.001f ||
                proxy.transform.localToWorldMatrix.determinant <= 0f)
                return false;

            Material[] materials = proxy.resolveMaterials;
            if (materials == null || materials.Length == 0)
                return false;
            for (int i = 0; i < materials.Length; i++)
            {
                Material material = materials[i];
                if (material == null || !material.HasProperty("_Cull") ||
                    Mathf.RoundToInt(material.GetFloat("_Cull")) != 2)
                    return false;
            }
            return true;
        }

        void DispatchSpatialTraversal(Texture hzbTexture)
        {
            shader.SetInt("_PartCount", partCount);
            shader.SetInt("_SpatialNodeCount", spatialNodes.Count);
            shader.SetInt("_SpatialPartRefCount", spatialPartRefs.Count);
            shader.SetInt("_EnableVisiblePartQueue", 1);
            shader.SetInt("_UseInstanceCull", 0);

            hierarchyQueues[0].SetCounterValue(0);
            int seedKernel = kernelSeedSpatialRoots;
            shader.SetBuffer(seedKernel, "_VisibleInstancesIn", visibleInstanceAppendBuffer);
            shader.SetBuffer(
                seedKernel,
                "_VisibleInstancePartDispatchArgs",
                visibleInstancePartDispatchArgsBuffer);
            shader.SetBuffer(seedKernel, "_SpatialNodes", spatialNodesBuffer);
            shader.SetBuffer(seedKernel, "_SpatialOutputQueue", hierarchyQueues[0]);
            BindInstanceBuffers(seedKernel);
            shader.DispatchIndirect(seedKernel, visibleInstancePartDispatchArgsBuffer, 0);

            int inputQueueIndex = 0;
            for (int passIndex = 0; passIndex < spatialTraversalPassCount; passIndex++)
            {
                int outputQueueIndex = 1 - inputQueueIndex;
                hierarchyQueues[outputQueueIndex].SetCounterValue(0);
                ComputeBuffer.CopyCount(
                    hierarchyQueues[inputQueueIndex],
                    hierarchyDispatchArgs[inputQueueIndex],
                    0);
                shader.SetBuffer(
                    kernelFinalizeHierarchyDispatch,
                    "_HierarchyDispatchArgsWrite",
                    hierarchyDispatchArgs[inputQueueIndex]);
                shader.Dispatch(kernelFinalizeHierarchyDispatch, 1, 1, 1);

                int kernel = kernelTraverseSpatialNodes;
                shader.SetBuffer(kernel, "_SpatialNodes", spatialNodesBuffer);
                shader.SetBuffer(kernel, "_SpatialPartRefs", spatialPartRefsBuffer);
                shader.SetBuffer(kernel, "_SpatialInputQueue", hierarchyQueues[inputQueueIndex]);
                shader.SetBuffer(kernel, "_SpatialOutputQueue", hierarchyQueues[outputQueueIndex]);
                shader.SetBuffer(kernel, "_HierarchyDispatchArgs", hierarchyDispatchArgs[inputQueueIndex]);
                shader.SetBuffer(kernel, "_Parts", partsBuffer);
                shader.SetBuffer(kernel, "_PartVisibleWrite", partVisibleBuffer);
                shader.SetBuffer(kernel, "_VisiblePartsOut", visiblePartAppendBuffer);
                BindGpuSceneRefs(kernel);
                BindResidencyHierarchy(kernel);
                BindInstanceBuffers(kernel);
                BindHzbTexture(kernel, hzbTexture);
                shader.DispatchIndirect(kernel, hierarchyDispatchArgs[inputQueueIndex], 0);
                inputQueueIndex = outputQueueIndex;
            }
        }

        void DispatchHierarchyTraversal(
            ComputeBuffer visibleTarget,
            ComputeBuffer sceneIndexBuffer,
            ComputeBuffer prevVisible,
            ComputeBuffer secondPassCandidates,
            ComputeBuffer pass2Drawn,
            Texture hzbTexture,
            ComputeBuffer fallbackSceneClusterData)
        {
            shader.SetInt("_HierarchyGroupCount", hierarchyGroups.Count);
            shader.SetInt("_HierarchyRefCount", hierarchyClusterRefs.Count);

            if (kEnableUnsafePersistentTraversal &&
                kernelClearHierarchyPersistent >= 0 &&
                kernelSeedHierarchyPersistent >= 0 &&
                kernelTraverseHierarchyPersistent >= 0 &&
                hierarchyPersistentWorkBuffer != null)
            {
                DispatchPersistentHierarchyTraversal(
                    visibleTarget,
                    sceneIndexBuffer,
                    prevVisible,
                    secondPassCandidates,
                    pass2Drawn,
                    hzbTexture,
                    fallbackSceneClusterData);
                return;
            }

            hierarchyQueues[0].SetCounterValue(0);
            shader.SetBuffer(kernelSeedHierarchyRoots, "_VisibleInstancesIn", visibleInstanceAppendBuffer);
            shader.SetBuffer(
                kernelSeedHierarchyRoots,
                "_VisibleInstancePartDispatchArgs",
                visibleInstancePartDispatchArgsBuffer);
            shader.SetBuffer(kernelSeedHierarchyRoots, "_HierarchyGroups", hierarchyGroupsBuffer);
            shader.SetBuffer(kernelSeedHierarchyRoots, "_HierarchyClusterRefs", hierarchyClusterRefsBuffer);
            shader.SetBuffer(kernelSeedHierarchyRoots, "_HierarchyRootGroups", hierarchyRootGroupsBuffer);
            shader.SetBuffer(kernelSeedHierarchyRoots, "_HierarchyOutputQueue", hierarchyQueues[0]);
            BindInstanceBuffers(kernelSeedHierarchyRoots);
            shader.DispatchIndirect(kernelSeedHierarchyRoots, visibleInstancePartDispatchArgsBuffer, 0);

            int inputQueueIndex = 0;
            for (int passIndex = 0; passIndex < hierarchyTraversalPassCount; passIndex++)
            {
                int outputQueueIndex = 1 - inputQueueIndex;
                hierarchyQueues[outputQueueIndex].SetCounterValue(0);
                ComputeBuffer.CopyCount(
                    hierarchyQueues[inputQueueIndex],
                    hierarchyDispatchArgs[inputQueueIndex],
                    0);
                shader.SetBuffer(
                    kernelFinalizeHierarchyDispatch,
                    "_HierarchyDispatchArgsWrite",
                    hierarchyDispatchArgs[inputQueueIndex]);
                shader.Dispatch(kernelFinalizeHierarchyDispatch, 1, 1, 1);

                int kernel = kernelTraverseHierarchy;
                shader.SetBuffer(kernel, "_HierarchyGroups", hierarchyGroupsBuffer);
                shader.SetBuffer(kernel, "_HierarchyClusterRefs", hierarchyClusterRefsBuffer);
                shader.SetBuffer(kernel, "_HierarchyInputQueue", hierarchyQueues[inputQueueIndex]);
                shader.SetBuffer(kernel, "_HierarchyOutputQueue", hierarchyQueues[outputQueueIndex]);
                shader.SetBuffer(kernel, "_HierarchyDispatchArgs", hierarchyDispatchArgs[inputQueueIndex]);
                shader.SetBuffer(kernel, "_VisibleDrawClusters", visibleDrawClusterAppendBuffer);
                shader.SetBuffer(
                    kernel,
                    "_SceneClusterFirstTri",
                    sceneClusterFirstTriBuffer ?? fallbackSceneClusterData);
                shader.SetBuffer(
                    kernel,
                    "_SceneClusterTriCount",
                    sceneClusterTriCountBuffer ?? fallbackSceneClusterData);
                BindGpuSceneRefs(kernel);
                BindPageStreamingBuffers(kernel);
                BindCullSharedBuffers(
                    kernel,
                    visibleTarget,
                    sceneIndexBuffer,
                    prevVisible,
                    secondPassCandidates,
                    pass2Drawn);
                BindHzbRejectedOutput(kernel);
                BindInstanceBuffers(kernel);
                BindHzbTexture(kernel, hzbTexture);
                shader.DispatchIndirect(kernel, hierarchyDispatchArgs[inputQueueIndex], 0);
                inputQueueIndex = outputQueueIndex;
            }
        }

        void DispatchPersistentHierarchyTraversal(
            ComputeBuffer visibleTarget,
            ComputeBuffer sceneIndexBuffer,
            ComputeBuffer prevVisible,
            ComputeBuffer secondPassCandidates,
            ComputeBuffer pass2Drawn,
            Texture hzbTexture,
            ComputeBuffer fallbackSceneClusterData)
        {
            uint epoch = NextHierarchyPersistentEpoch();
            shader.SetInt("_HierarchyPersistentCapacity", hierarchyPersistentCapacity);
            shader.SetInt("_HierarchyPersistentEpoch", unchecked((int)epoch));

            shader.SetBuffer(
                kernelClearHierarchyPersistent,
                "_HierarchyPersistentWork",
                hierarchyPersistentWorkBuffer);
            shader.Dispatch(kernelClearHierarchyPersistent, 1, 1, 1);

            int seedKernel = kernelSeedHierarchyPersistent;
            shader.SetBuffer(seedKernel, "_VisibleInstancesIn", visibleInstanceAppendBuffer);
            shader.SetBuffer(
                seedKernel,
                "_VisibleInstancePartDispatchArgs",
                visibleInstancePartDispatchArgsBuffer);
            shader.SetBuffer(seedKernel, "_HierarchyGroups", hierarchyGroupsBuffer);
            shader.SetBuffer(seedKernel, "_HierarchyClusterRefs", hierarchyClusterRefsBuffer);
            shader.SetBuffer(seedKernel, "_HierarchyRootGroups", hierarchyRootGroupsBuffer);
            shader.SetBuffer(seedKernel, "_HierarchyPersistentWork", hierarchyPersistentWorkBuffer);
            BindInstanceBuffers(seedKernel);
            shader.DispatchIndirect(seedKernel, visibleInstancePartDispatchArgsBuffer, 0);

            int kernel = kernelTraverseHierarchyPersistent;
            shader.SetBuffer(kernel, "_HierarchyGroups", hierarchyGroupsBuffer);
            shader.SetBuffer(kernel, "_HierarchyClusterRefs", hierarchyClusterRefsBuffer);
            shader.SetBuffer(kernel, "_HierarchyPersistentWork", hierarchyPersistentWorkBuffer);
            shader.SetBuffer(kernel, "_VisibleDrawClusters", visibleDrawClusterAppendBuffer);
            shader.SetBuffer(
                kernel,
                "_SceneClusterFirstTri",
                sceneClusterFirstTriBuffer ?? fallbackSceneClusterData);
            shader.SetBuffer(
                kernel,
                "_SceneClusterTriCount",
                sceneClusterTriCountBuffer ?? fallbackSceneClusterData);
            BindGpuSceneRefs(kernel);
            BindPageStreamingBuffers(kernel);
            BindCullSharedBuffers(
                kernel,
                visibleTarget,
                sceneIndexBuffer,
                prevVisible,
                secondPassCandidates,
                pass2Drawn);
            BindHzbRejectedOutput(kernel);
            BindInstanceBuffers(kernel);
            BindHzbTexture(kernel, hzbTexture);
            // Nyx uses vendor-tuned persistent group counts (512-1024 at 32 lanes).
            // Unity's compatibility kernel uses 64 lanes and less LDS, so 256 groups
            // provide the same 16K resident workers without six CPU-recorded passes.
            shader.Dispatch(kernel, 256, 1, 1);
        }

        uint NextHierarchyPersistentEpoch()
        {
            hierarchyPersistentEpoch++;
            // Zero is reserved for freshly allocated task storage.
            if (hierarchyPersistentEpoch == 0u)
                hierarchyPersistentEpoch = 1u;
            return hierarchyPersistentEpoch;
        }

        struct SpatialBuildRef
        {
            public uint morton;
            public int localPartIndex;
        }

        bool TryAppendGeometrySpatialHierarchy(
            int geometryPartOffset,
            int geometryPartCountInMesh,
            out int rootOffset,
            out int rootCount,
            out int maxDepth)
        {
            rootOffset = spatialNodes.Count;
            rootCount = 0;
            maxDepth = 0;
            if (geometryPartOffset < 0 || geometryPartCountInMesh <= 0 ||
                geometryPartOffset + geometryPartCountInMesh > mergedParts.Count)
                return false;

            Vector3 centerMin = new Vector3(
                float.PositiveInfinity,
                float.PositiveInfinity,
                float.PositiveInfinity);
            Vector3 centerMax = new Vector3(
                float.NegativeInfinity,
                float.NegativeInfinity,
                float.NegativeInfinity);
            for (int localPart = 0; localPart < geometryPartCountInMesh; localPart++)
            {
                Vector4 sphere = mergedParts[geometryPartOffset + localPart].selfSphere;
                Vector3 center = new Vector3(sphere.x, sphere.y, sphere.z);
                centerMin = Vector3.Min(centerMin, center);
                centerMax = Vector3.Max(centerMax, center);
            }

            Vector3 extent = centerMax - centerMin;
            extent.x = Mathf.Max(extent.x, 1e-6f);
            extent.y = Mathf.Max(extent.y, 1e-6f);
            extent.z = Mathf.Max(extent.z, 1e-6f);
            var refs = new List<SpatialBuildRef>(geometryPartCountInMesh);
            for (int localPart = 0; localPart < geometryPartCountInMesh; localPart++)
            {
                Vector4 sphere = mergedParts[geometryPartOffset + localPart].selfSphere;
                Vector3 center = new Vector3(sphere.x, sphere.y, sphere.z);
                Vector3 normalized = new Vector3(
                    Mathf.Clamp01((center.x - centerMin.x) / extent.x),
                    Mathf.Clamp01((center.y - centerMin.y) / extent.y),
                    Mathf.Clamp01((center.z - centerMin.z) / extent.z));
                refs.Add(new SpatialBuildRef
                {
                    morton = SpatialMorton3D(normalized),
                    localPartIndex = localPart
                });
            }
            refs.Sort((a, b) =>
            {
                int order = a.morton.CompareTo(b.morton);
                return order != 0 ? order : a.localPartIndex.CompareTo(b.localPartIndex);
            });

            spatialNodes.Add(default);
            maxDepth = BuildSpatialNodeAt(
                rootOffset,
                refs,
                0,
                refs.Count,
                geometryPartOffset);
            rootCount = 1;
            return true;
        }

        int BuildSpatialNodeAt(
            int nodeIndex,
            List<SpatialBuildRef> sortedRefs,
            int rangeStart,
            int rangeCount,
            int geometryPartOffset)
        {
            Vector4 bounds = Vector4.zero;
            Vector4 lodBounds = Vector4.zero;
            float maxParentError = 0f;
            uint flags = 0u;
            for (int i = 0; i < rangeCount; i++)
            {
                int localPart = sortedRefs[rangeStart + i].localPartIndex;
                NaniteGpuCullingBackend.GpuPartData part = mergedParts[geometryPartOffset + localPart];
                Vector4 partBounds = SanitizeSphere(part.selfSphere);
                Vector4 partLodBounds = SanitizeSphere(
                    part.parentSphere.w > 0f ? part.parentSphere : part.selfSphere);
                bounds = i == 0 ? partBounds : MergeSpatialSpheres(bounds, partBounds);
                lodBounds = i == 0 ? partLodBounds : MergeSpatialSpheres(lodBounds, partLodBounds);
                maxParentError = Mathf.Max(maxParentError, part.maxParentError);
                if ((part.lodFlags & NaniteGpuCullingBackend.TerminalDisappearLodFlag) != 0u)
                    flags |= kSpatialNodeHasTerminal;
            }

            if (rangeCount <= kSpatialLeafPartCount)
            {
                int partRefStart = spatialPartRefs.Count;
                for (int i = 0; i < rangeCount; i++)
                    spatialPartRefs.Add((uint)sortedRefs[rangeStart + i].localPartIndex);
                spatialNodes[nodeIndex] = new GpuSpatialNode
                {
                    boundingSphere = bounds,
                    lodSphere = lodBounds,
                    maxParentError = maxParentError,
                    childStart = 0u,
                    childCount = 0u,
                    partRefStart = (uint)partRefStart,
                    partRefCount = (uint)rangeCount,
                    flags = flags | kSpatialNodeLeaf
                };
                return 1;
            }

            int childCount = Mathf.Min(
                kSpatialBranchFactor,
                (rangeCount + kSpatialLeafPartCount - 1) / kSpatialLeafPartCount);
            int childStart = spatialNodes.Count;
            for (int child = 0; child < childCount; child++)
                spatialNodes.Add(default);

            int maxChildDepth = 0;
            int consumed = 0;
            for (int child = 0; child < childCount; child++)
            {
                int remaining = rangeCount - consumed;
                int childrenRemaining = childCount - child;
                int childRangeCount = (remaining + childrenRemaining - 1) / childrenRemaining;
                int childDepth = BuildSpatialNodeAt(
                    childStart + child,
                    sortedRefs,
                    rangeStart + consumed,
                    childRangeCount,
                    geometryPartOffset);
                maxChildDepth = Mathf.Max(maxChildDepth, childDepth);
                consumed += childRangeCount;
            }

            spatialNodes[nodeIndex] = new GpuSpatialNode
            {
                boundingSphere = bounds,
                lodSphere = lodBounds,
                maxParentError = maxParentError,
                childStart = (uint)childStart,
                childCount = (uint)childCount,
                partRefStart = 0u,
                partRefCount = 0u,
                flags = flags
            };
            return maxChildDepth + 1;
        }

        static Vector4 SanitizeSphere(Vector4 sphere)
        {
            if (float.IsNaN(sphere.x) || float.IsInfinity(sphere.x) ||
                float.IsNaN(sphere.y) || float.IsInfinity(sphere.y) ||
                float.IsNaN(sphere.z) || float.IsInfinity(sphere.z) ||
                float.IsNaN(sphere.w) || float.IsInfinity(sphere.w))
                return new Vector4(0f, 0f, 0f, 1e30f);
            sphere.w = Mathf.Max(0f, sphere.w);
            return sphere;
        }

        static Vector4 MergeSpatialSpheres(Vector4 a, Vector4 b)
        {
            Vector3 ac = new Vector3(a.x, a.y, a.z);
            Vector3 bc = new Vector3(b.x, b.y, b.z);
            float ar = Mathf.Max(0f, a.w);
            float br = Mathf.Max(0f, b.w);
            Vector3 delta = bc - ac;
            float distance = delta.magnitude;
            if (ar >= distance + br)
                return a;
            if (br >= distance + ar)
                return b;
            if (distance <= 1e-8f)
                return new Vector4(ac.x, ac.y, ac.z, Mathf.Max(ar, br));
            float radius = (distance + ar + br) * 0.5f;
            Vector3 center = ac + delta * ((radius - ar) / distance);
            return new Vector4(center.x, center.y, center.z, radius);
        }

        static uint SpatialMorton3D(Vector3 normalized)
        {
            uint x = (uint)Mathf.Clamp(Mathf.FloorToInt(normalized.x * 1023f), 0, 1023);
            uint y = (uint)Mathf.Clamp(Mathf.FloorToInt(normalized.y * 1023f), 0, 1023);
            uint z = (uint)Mathf.Clamp(Mathf.FloorToInt(normalized.z * 1023f), 0, 1023);
            return SpatialExpandMortonBits(x) |
                   (SpatialExpandMortonBits(y) << 1) |
                   (SpatialExpandMortonBits(z) << 2);
        }

        static uint SpatialExpandMortonBits(uint value)
        {
            value &= 0x000003ffu;
            value = (value | (value << 16)) & 0x030000FFu;
            value = (value | (value << 8)) & 0x0300F00Fu;
            value = (value | (value << 4)) & 0x030C30C3u;
            value = (value | (value << 2)) & 0x09249249u;
            return value;
        }

        bool TryAppendGeometryHierarchy(
            NaniteMesh mesh,
            int geometryClusterCountInMesh,
            out int rootOffset,
            out int rootCount,
            out int maxMip,
            out int groupOffset)
        {
            rootOffset = hierarchyRootGroups.Count;
            rootCount = 0;
            maxMip = 0;
            groupOffset = hierarchyGroups.Count;
            if (mesh == null ||
                mesh.hierarchyVersion != NaniteHierarchyGroup.CurrentVersion ||
                mesh.hierarchyGroups == null || mesh.hierarchyGroups.Length == 0 ||
                mesh.hierarchyClusterRefs == null || mesh.hierarchyClusterRefs.Length == 0 ||
                mesh.hierarchyRootGroups == null || mesh.hierarchyRootGroups.Length == 0)
                return false;

            int groupBase = hierarchyGroups.Count;
            groupOffset = groupBase;
            int refBase = hierarchyClusterRefs.Count;
            uint[] geometryClusterTriangleCounts = BuildGeometryClusterTriangleCounts(
                mesh,
                geometryClusterCountInMesh);
            float[] geometryClusterSurfaceArea = BuildGeometryClusterSurfaceArea(
                mesh,
                geometryClusterCountInMesh);
            for (int groupIndex = 0; groupIndex < mesh.hierarchyGroups.Length; groupIndex++)
            {
                NaniteHierarchyGroup group = mesh.hierarchyGroups[groupIndex];
                int fineEnd = group.fineClusterStart + group.fineClusterCount;
                int coarseEnd = group.coarseClusterStart + group.coarseClusterCount;
                if (group.fineClusterStart < 0 || group.fineClusterCount <= 0 ||
                    fineEnd > mesh.hierarchyClusterRefs.Length ||
                    group.coarseClusterStart < 0 || group.coarseClusterCount < 0 ||
                    coarseEnd > mesh.hierarchyClusterRefs.Length)
                    return false;
            }

            for (int refIndex = 0; refIndex < mesh.hierarchyClusterRefs.Length; refIndex++)
            {
                NaniteHierarchyClusterRef clusterRef = mesh.hierarchyClusterRefs[refIndex];
                if (clusterRef.geometryClusterIndex < 0 ||
                    clusterRef.geometryClusterIndex >= geometryClusterCountInMesh ||
                    clusterRef.refinementGroupIndex < -1 ||
                    clusterRef.refinementGroupIndex >= mesh.hierarchyGroups.Length)
                    return false;
            }

            for (int rootIndex = 0; rootIndex < mesh.hierarchyRootGroups.Length; rootIndex++)
            {
                int groupIndex = mesh.hierarchyRootGroups[rootIndex];
                if (groupIndex < 0 || groupIndex >= mesh.hierarchyGroups.Length ||
                    !mesh.hierarchyGroups[groupIndex].IsRootSet)
                    return false;
            }

            for (int groupIndex = 0; groupIndex < mesh.hierarchyGroups.Length; groupIndex++)
            {
                NaniteHierarchyGroup group = mesh.hierarchyGroups[groupIndex];
                uint fineTriangleCount = SumHierarchyTriangleCount(
                    mesh.hierarchyClusterRefs,
                    group.fineClusterStart,
                    group.fineClusterCount,
                    geometryClusterTriangleCounts);
                uint coarseTriangleCount = SumHierarchyTriangleCount(
                    mesh.hierarchyClusterRefs,
                    group.coarseClusterStart,
                    group.coarseClusterCount,
                    geometryClusterTriangleCounts);
                float fineSurfaceArea = SumHierarchySurfaceArea(
                    mesh.hierarchyClusterRefs,
                    group.fineClusterStart,
                    group.fineClusterCount,
                    geometryClusterSurfaceArea);
                hierarchyGroups.Add(new GpuHierarchyGroup
                {
                    boundingSphere = group.boundingSphere,
                    minLodError = group.minLodError,
                    maxParentLodError = group.maxParentLodError,
                    fineClusterStart = (uint)(refBase + group.fineClusterStart),
                    fineClusterCount = (uint)group.fineClusterCount,
                    coarseClusterStart = (uint)(refBase + group.coarseClusterStart),
                    coarseClusterCount = (uint)group.coarseClusterCount,
                    mipLevel = (uint)Mathf.Max(0, group.mipLevel),
                    flags = (uint)group.flags,
                    fineTriangleCount = fineTriangleCount,
                    coarseTriangleCount = coarseTriangleCount,
                    fineSurfaceArea = fineSurfaceArea
                });
                maxMip = Mathf.Max(maxMip, group.mipLevel);
            }

            for (int refIndex = 0; refIndex < mesh.hierarchyClusterRefs.Length; refIndex++)
            {
                NaniteHierarchyClusterRef clusterRef = mesh.hierarchyClusterRefs[refIndex];
                hierarchyClusterRefs.Add(new GpuHierarchyClusterRef
                {
                    geometryClusterIndex = (uint)clusterRef.geometryClusterIndex,
                    pageIndex = (uint)Mathf.Max(0, clusterRef.pageIndex),
                    pageClusterIndex = (uint)Mathf.Max(0, clusterRef.pageClusterIndex),
                    refinementGroupIndex = clusterRef.refinementGroupIndex >= 0
                        ? (uint)(groupBase + clusterRef.refinementGroupIndex)
                        : uint.MaxValue
                });
            }

            for (int rootIndex = 0; rootIndex < mesh.hierarchyRootGroups.Length; rootIndex++)
                hierarchyRootGroups.Add((uint)(groupBase + mesh.hierarchyRootGroups[rootIndex]));
            rootCount = mesh.hierarchyRootGroups.Length;
            return true;
        }

        static uint[] BuildGeometryClusterTriangleCounts(NaniteMesh mesh, int expectedClusterCount)
        {
            var counts = new uint[Mathf.Max(0, expectedClusterCount)];
            if (mesh?.pageArray == null)
                return counts;

            int geometryClusterIndex = 0;
            for (int pageIndex = 0; pageIndex < mesh.pageArray.Length; pageIndex++)
            {
                NaniteMeshPage page = mesh.pageArray[pageIndex];
                if (page?.clusterArray == null)
                    continue;
                for (int clusterIndex = 0;
                     clusterIndex < page.clusterArray.Length && geometryClusterIndex < counts.Length;
                     clusterIndex++, geometryClusterIndex++)
                {
                    counts[geometryClusterIndex] =
                        (uint)Mathf.Max(0, page.clusterArray[clusterIndex].indiceCount / 3);
                }
            }
            return counts;
        }

        static uint SumHierarchyTriangleCount(
            NaniteHierarchyClusterRef[] refs,
            int start,
            int count,
            uint[] geometryClusterTriangleCounts)
        {
            if (refs == null || geometryClusterTriangleCounts == null || count <= 0)
                return 0u;

            ulong sum = 0u;
            int end = Mathf.Min(refs.Length, start + count);
            for (int refIndex = Mathf.Max(0, start); refIndex < end; refIndex++)
            {
                int geometryClusterIndex = refs[refIndex].geometryClusterIndex;
                if ((uint)geometryClusterIndex < (uint)geometryClusterTriangleCounts.Length)
                    sum += geometryClusterTriangleCounts[geometryClusterIndex];
            }
            return (uint)Math.Min(sum, uint.MaxValue);
        }

        static float[] BuildGeometryClusterSurfaceArea(NaniteMesh mesh, int expectedClusterCount)
        {
            var result = new float[Mathf.Max(0, expectedClusterCount)];
            if (mesh?.pageArray == null)
                return result;
            int geometryClusterIndex = 0;
            for (int pageIndex = 0; pageIndex < mesh.pageArray.Length; pageIndex++)
            {
                NaniteMeshPage page = mesh.pageArray[pageIndex];
                if (page?.clusterArray == null)
                    continue;
                for (int clusterIndex = 0;
                     clusterIndex < page.clusterArray.Length && geometryClusterIndex < result.Length;
                     clusterIndex++, geometryClusterIndex++)
                {
                    NaniteCluster cluster = page.clusterArray[clusterIndex];
                    int stride = Mathf.Max(3, page.vertexStride);
                    int[] indices = page.indiceArray; float[] vertices = page.vertexData;
                    if (indices == null || vertices == null) continue;
                    int indexEnd = Mathf.Min(indices.Length, cluster.indiceIndex + cluster.indiceCount);
                    double area = 0.0;
                    for (int index = Mathf.Max(0, cluster.indiceIndex); index + 2 < indexEnd; index += 3)
                    {
                        int v0 = (indices[index] + cluster.vertexOffset) * stride;
                        int v1 = (indices[index + 1] + cluster.vertexOffset) * stride;
                        int v2 = (indices[index + 2] + cluster.vertexOffset) * stride;
                        if (v0 < 0 || v1 < 0 || v2 < 0 || v0 + 2 >= vertices.Length || v1 + 2 >= vertices.Length || v2 + 2 >= vertices.Length) continue;
                        Vector3 p0 = new Vector3(vertices[v0], vertices[v0 + 1], vertices[v0 + 2]);
                        Vector3 p1 = new Vector3(vertices[v1], vertices[v1 + 1], vertices[v1 + 2]);
                        Vector3 p2 = new Vector3(vertices[v2], vertices[v2 + 1], vertices[v2 + 2]);
                        area += 0.5 * Vector3.Cross(p1 - p0, p2 - p0).magnitude;
                    }
                    result[geometryClusterIndex] = (float)Math.Min(area, float.MaxValue);
                }
            }
            return result;
        }

        static float SumHierarchySurfaceArea(
            NaniteHierarchyClusterRef[] refs,
            int start,
            int count,
            float[] geometryClusterSurfaceArea)
        {
            if (refs == null || geometryClusterSurfaceArea == null || count <= 0)
                return 0f;
            double sum = 0.0;
            int end = Mathf.Min(refs.Length, start + count);
            for (int refIndex = Mathf.Max(0, start); refIndex < end; refIndex++)
            {
                int geometryClusterIndex = refs[refIndex].geometryClusterIndex;
                if ((uint)geometryClusterIndex < (uint)geometryClusterSurfaceArea.Length)
                    sum += geometryClusterSurfaceArea[geometryClusterIndex];
            }
            return (float)Math.Min(sum, float.MaxValue);
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

        static int CountMeshParts(NaniteMesh mesh)
        {
            if (mesh?.pageArray == null)
                return 0;
            int count = 0;
            for (int pageIndex = 0; pageIndex < mesh.pageArray.Length; pageIndex++)
                count = checked(count + (mesh.pageArray[pageIndex]?.parts?.Length ?? 0));
            return count;
        }

        static int CountTerminalGeometryClusters(
            List<NaniteGpuCullingBackend.GpuClusterData> clusters,
            int start,
            int count)
        {
            int end = Mathf.Min(clusters.Count, checked(start + count));
            int terminals = 0;
            for (int clusterIndex = Mathf.Max(0, start); clusterIndex < end; clusterIndex++)
            {
                if (clusters[clusterIndex].refinementGroupIndex == uint.MaxValue)
                    terminals++;
            }
            // A valid replacement DAG maps every frontier cluster to at least
            // one terminal producer. If malformed metadata has no terminal,
            // retain the full geometry bound instead of under-allocating.
            return terminals > 0 ? terminals : count;
        }

        public bool RecordShadowCull(
            UnsafeCommandBuffer cmd,
            Camera camera,
            int cascadeIndex,
            Matrix4x4 shadowViewMatrix,
            Matrix4x4 shadowProjectionMatrix,
            int shadowResolution,
            Vector3 shadowRayDirection)
        {
            if (cmd == null || camera == null || !SupportsGpuVisibleMask ||
                !virtualRefsExpanded ||
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
            LastShadowUsedHierarchyQueue = false;
            LastShadowUsedSpatialHierarchy = false;
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
                useVisibleInstanceQueue,
                shadowRayDirection);

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
                    "_VisiblePartDispatchArgsWrite",
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
            int[] shadowResolutions,
            Vector4[] shadowCasterPlanes,
            int[] shadowCasterPlaneCounts,
            Vector4[] shadowCullingSpheres,
            Vector3 shadowRayDirection)
        {
            int activeCascadeCount = Mathf.Clamp(cascadeCount, 0, kMaxShadowCascades);
            LastShadowUsedHierarchyQueue = false;
            if (cmd == null || camera == null || !SupportsGpuVisibleMask ||
                kernelShadowCullMultiCascadeByPart < 0 || activeCascadeCount <= 0 ||
                shadowViewMatrices == null || shadowProjectionMatrices == null || shadowResolutions == null ||
                shadowViewMatrices.Length < activeCascadeCount ||
                shadowProjectionMatrices.Length < activeCascadeCount ||
                shadowResolutions.Length < activeCascadeCount ||
                shadowCasterPlanes == null ||
                shadowCasterPlanes.Length < activeCascadeCount * kMaxShadowCasterPlanesPerCascade ||
                shadowCasterPlaneCounts == null ||
                shadowCasterPlaneCounts.Length < activeCascadeCount ||
                shadowCullingSpheres == null ||
                shadowCullingSpheres.Length < activeCascadeCount)
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
            shadowBatchCasterPlaneCounts = Vector4.zero;
            for (int cascadeIndex = 0; cascadeIndex < activeCascadeCount; cascadeIndex++)
            {
                int planeCount = Mathf.Clamp(
                    shadowCasterPlaneCounts[cascadeIndex],
                    0,
                    kMaxShadowCasterPlanesPerCascade);
                int planeBase = cascadeIndex * kMaxShadowCasterPlanesPerCascade;
                Array.Copy(
                    shadowCasterPlanes,
                    planeBase,
                    shadowBatchCasterPlanes,
                    planeBase,
                    kMaxShadowCasterPlanesPerCascade);
                if (cascadeIndex == 0)
                    shadowBatchCasterPlaneCounts.x = planeCount;
                else if (cascadeIndex == 1)
                    shadowBatchCasterPlaneCounts.y = planeCount;
                else if (cascadeIndex == 2)
                    shadowBatchCasterPlaneCounts.z = planeCount;
                else
                    shadowBatchCasterPlaneCounts.w = planeCount;
                shadowBatchCullingSpheres[cascadeIndex] = shadowCullingSpheres[cascadeIndex];

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
            cmd.SetComputeFloatParam(shader, "_ShadowLodMinTexels", Mathf.Max(1f, ShadowLodMinTexels));
            cmd.SetComputeVectorArrayParam(shader, "_ShadowCasterPlanes", shadowBatchCasterPlanes);
            cmd.SetComputeVectorParam(shader, "_ShadowCasterPlaneCounts", shadowBatchCasterPlaneCounts);
            cmd.SetComputeVectorArrayParam(shader, "_ShadowCullingSpheres", shadowBatchCullingSpheres);
            cmd.SetComputeVectorParam(shader, "_ShadowProjectionScales", shadowBatchProjectionScales);
            Vector3 normalizedShadowRay = shadowRayDirection.sqrMagnitude > 1e-12f
                ? shadowRayDirection.normalized
                : Vector3.forward;
            cmd.SetComputeVectorParam(shader, "_ShadowRayDirection", new Vector4(
                normalizedShadowRay.x,
                normalizedShadowRay.y,
                normalizedShadowRay.z,
                0f));
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
            cmd.SetComputeIntParam(shader, "_AllPagesResident", !pageRequestsEnabled || sceneAllPagesResident ? 1 : 0);
            cmd.SetComputeIntParam(
                shader,
                "_TrackPageUsage",
                pageRequestsEnabled && sceneTrackPageUsage ? 1 : 0);

            ComputeBuffer fallback = GetFallbackClusterVisibleBuffer();
            if (pageRequestsEnabled && !sceneAllPagesResident)
                RecordHierarchyGroupResidency(cmd, true, fallback);
            bool useBoundedHierarchyShadow = kEnableAtomicHierarchyTraversal &&
                                             hierarchyReady &&
                                             kernelSeedShadowHierarchyRoots >= 0 &&
                                             kernelTraverseShadowHierarchy >= 0 &&
                                             kernelFinalizeHierarchyDispatch >= 0 &&
                                             shadowSpatialQueues[0] != null &&
                                             shadowSpatialQueues[1] != null;
            if (useBoundedHierarchyShadow)
            {
                RecordShadowHierarchyTraversal(cmd, pageRequestsEnabled, fallback);
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
                LastShadowUsedHierarchyQueue = true;
                LastShadowUsedSpatialHierarchy = false;
                LastShadowUsedVisiblePartQueue = false;
                LastShadowUsedVisibleInstanceQueue = false;
                return true;
            }

            bool useSpatialShadow = spatialReady &&
                                    kernelSeedShadowSpatialRoots >= 0 &&
                                    kernelTraverseShadowSpatialNodes >= 0 &&
                                    kernelFinalizeHierarchyDispatch >= 0 &&
                                    shadowSpatialQueues[0] != null &&
                                    shadowSpatialQueues[1] != null;
            if (useSpatialShadow)
            {
                RecordShadowSpatialTraversal(cmd, pageRequestsEnabled, fallback);
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
                LastShadowUsedHierarchyQueue = false;
                LastShadowUsedSpatialHierarchy = true;
                LastShadowUsedVisiblePartQueue = true;
                LastShadowUsedVisibleInstanceQueue = true;
                return true;
            }
            bool useHierarchyShadow =
                kEnableUnsafePersistentTraversal &&
                hierarchyReady &&
                shadowHierarchyPersistentWorkBuffer != null &&
                kernelClearHierarchyPersistent >= 0 &&
                kernelSeedShadowHierarchyPersistent >= 0 &&
                kernelTraverseShadowHierarchyPersistent >= 0;
            if (useHierarchyShadow)
            {
                uint epoch = NextHierarchyPersistentEpoch();
                cmd.SetComputeIntParam(shader, "_HierarchyGroupCount", hierarchyGroups.Count);
                cmd.SetComputeIntParam(shader, "_HierarchyRefCount", hierarchyClusterRefs.Count);
                cmd.SetComputeIntParam(shader, "_HierarchyPersistentCapacity", hierarchyPersistentCapacity);
                cmd.SetComputeIntParam(shader, "_HierarchyPersistentEpoch", unchecked((int)epoch));

                cmd.SetComputeBufferParam(
                    shader,
                    kernelClearHierarchyPersistent,
                    "_HierarchyPersistentWork",
                    shadowHierarchyPersistentWorkBuffer);
                cmd.DispatchCompute(shader, kernelClearHierarchyPersistent, 1, 1, 1);

                int seedKernel = kernelSeedShadowHierarchyPersistent;
                cmd.SetComputeBufferParam(shader, seedKernel, "_HierarchyGroups", hierarchyGroupsBuffer);
                cmd.SetComputeBufferParam(shader, seedKernel, "_HierarchyRootGroups", hierarchyRootGroupsBuffer);
                cmd.SetComputeBufferParam(shader, seedKernel, "_Instances", instanceDataBuffer);
                cmd.SetComputeBufferParam(shader, seedKernel, "_HierarchyPersistentWork", shadowHierarchyPersistentWorkBuffer);
                cmd.DispatchCompute(shader, seedKernel, Mathf.Max(1, instanceCount), 1, 1);

                int traverseKernel = kernelTraverseShadowHierarchyPersistent;
                cmd.SetComputeBufferParam(shader, traverseKernel, "_HierarchyGroups", hierarchyGroupsBuffer);
                cmd.SetComputeBufferParam(shader, traverseKernel, "_HierarchyClusterRefs", hierarchyClusterRefsBuffer);
                cmd.SetComputeBufferParam(shader, traverseKernel, "_HierarchyPersistentWork", shadowHierarchyPersistentWorkBuffer);
                cmd.SetComputeBufferParam(shader, traverseKernel, "_Clusters", clustersBuffer);
                cmd.SetComputeBufferParam(shader, traverseKernel, "_VirtualClusters", virtualClustersBuffer);
                cmd.SetComputeBufferParam(shader, traverseKernel, "_GeometryClusterPageIndex", geometryClusterPageIndexBuffer);
                cmd.SetComputeBufferParam(shader, traverseKernel, "_Instances", instanceDataBuffer);
                cmd.SetComputeBufferParam(shader, traverseKernel, "_ClusterSceneIndex", clusterSceneIndexBuffer);
                cmd.SetComputeBufferParam(shader, traverseKernel, "_SceneClusterFirstTri", sceneClusterFirstTriBuffer);
                cmd.SetComputeBufferParam(shader, traverseKernel, "_SceneClusterTriCount", sceneClusterTriCountBuffer);
                cmd.SetComputeBufferParam(
                    shader,
                    traverseKernel,
                    "_PageResidency",
                    pageRequestsEnabled ? scenePageResidencyBuffer : fallback);
                cmd.SetComputeBufferParam(
                    shader,
                    traverseKernel,
                    "_PageRequests",
                    pageRequestsEnabled ? scenePageRequestBuffer : fallback);
                cmd.SetComputeBufferParam(shader, traverseKernel, "_ShadowVisibleDrawClusters0", shadowDrawClusterAppendBuffers[0]);
                cmd.SetComputeBufferParam(shader, traverseKernel, "_ShadowVisibleDrawClusters1", shadowDrawClusterAppendBuffers[1]);
                cmd.SetComputeBufferParam(shader, traverseKernel, "_ShadowVisibleDrawClusters2", shadowDrawClusterAppendBuffers[2]);
                cmd.SetComputeBufferParam(shader, traverseKernel, "_ShadowVisibleDrawClusters3", shadowDrawClusterAppendBuffers[3]);
                cmd.DispatchCompute(shader, traverseKernel, 256, 1, 1);

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
                LastShadowUsedHierarchyQueue = true;
                LastShadowUsedSpatialHierarchy = false;
                LastShadowUsedVisiblePartQueue = false;
                LastShadowUsedVisibleInstanceQueue = false;
                return true;
            }

            if (!virtualRefsExpanded)
                return false;

            cmd.SetComputeBufferParam(shader, kernel, "_Parts", partsBuffer);
            cmd.SetComputeBufferParam(shader, kernel, "_Clusters", clustersBuffer);
            cmd.SetComputeBufferParam(shader, kernel, "_VirtualParts", virtualPartsBuffer);
            cmd.SetComputeBufferParam(shader, kernel, "_VirtualClusters", virtualClustersBuffer);
            cmd.SetComputeBufferParam(shader, kernel, "_GeometryClusterPageIndex", geometryClusterPageIndexBuffer);
            cmd.SetComputeBufferParam(shader, kernel, "_Clusters", clustersBuffer);
            cmd.SetComputeBufferParam(shader, kernel, "_Instances", instanceDataBuffer);
            cmd.SetComputeBufferParam(shader, kernel, "_ClusterSceneIndex", clusterSceneIndexBuffer);
            cmd.SetComputeBufferParam(shader, kernel, "_SceneClusterFirstTri", sceneClusterFirstTriBuffer);
            cmd.SetComputeBufferParam(shader, kernel, "_SceneClusterTriCount", sceneClusterTriCountBuffer);
            cmd.SetComputeIntParam(shader, "_HierarchyGroupCount", hierarchyGroups.Count);
            cmd.SetComputeIntParam(shader, "_HierarchyRefCount", hierarchyClusterRefs.Count);
            cmd.SetComputeBufferParam(shader, kernel, "_HierarchyGroups", hierarchyGroupsBuffer);
            cmd.SetComputeBufferParam(shader, kernel, "_HierarchyClusterRefs", hierarchyClusterRefsBuffer);
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
            LastShadowUsedHierarchyQueue = false;
            LastShadowUsedSpatialHierarchy = false;
            LastShadowUsedVisiblePartQueue = false;
            LastShadowUsedVisibleInstanceQueue = false;
            return true;
        }

        void RecordShadowSpatialTraversal(
            UnsafeCommandBuffer cmd,
            bool pageRequestsEnabled,
            ComputeBuffer fallback)
        {
            cmd.SetComputeIntParam(shader, "_SpatialNodeCount", spatialNodes.Count);
            cmd.SetComputeIntParam(shader, "_SpatialPartRefCount", spatialPartRefs.Count);
            cmd.SetBufferCounterValue(shadowSpatialQueues[0], 0u);

            int seedKernel = kernelSeedShadowSpatialRoots;
            cmd.SetComputeBufferParam(shader, seedKernel, "_SpatialNodes", spatialNodesBuffer);
            cmd.SetComputeBufferParam(shader, seedKernel, "_Instances", instanceDataBuffer);
            cmd.SetComputeBufferParam(
                shader,
                seedKernel,
                "_ShadowSpatialOutputQueue",
                shadowSpatialQueues[0]);
            cmd.DispatchCompute(shader, seedKernel, Mathf.Max(1, instanceCount), 1, 1);

            int inputQueueIndex = 0;
            for (int passIndex = 0; passIndex < spatialTraversalPassCount; passIndex++)
            {
                int outputQueueIndex = 1 - inputQueueIndex;
                cmd.SetBufferCounterValue(shadowSpatialQueues[outputQueueIndex], 0u);
                cmd.CopyCounterValue(
                    shadowSpatialQueues[inputQueueIndex],
                    shadowSpatialDispatchArgs[inputQueueIndex],
                    0u);
                cmd.SetComputeBufferParam(
                    shader,
                    kernelFinalizeHierarchyDispatch,
                    "_HierarchyDispatchArgsWrite",
                    shadowSpatialDispatchArgs[inputQueueIndex]);
                cmd.DispatchCompute(shader, kernelFinalizeHierarchyDispatch, 1, 1, 1);

                int kernel = kernelTraverseShadowSpatialNodes;
                cmd.SetComputeBufferParam(shader, kernel, "_SpatialNodes", spatialNodesBuffer);
                cmd.SetComputeBufferParam(shader, kernel, "_SpatialPartRefs", spatialPartRefsBuffer);
                cmd.SetComputeBufferParam(
                    shader,
                    kernel,
                    "_ShadowSpatialInputQueue",
                    shadowSpatialQueues[inputQueueIndex]);
                cmd.SetComputeBufferParam(
                    shader,
                    kernel,
                    "_ShadowSpatialOutputQueue",
                    shadowSpatialQueues[outputQueueIndex]);
                cmd.SetComputeBufferParam(
                    shader,
                    kernel,
                    "_HierarchyDispatchArgs",
                    shadowSpatialDispatchArgs[inputQueueIndex]);
                cmd.SetComputeBufferParam(shader, kernel, "_Parts", partsBuffer);
                cmd.SetComputeBufferParam(shader, kernel, "_Clusters", clustersBuffer);
                cmd.SetComputeBufferParam(shader, kernel, "_VirtualParts", virtualPartsBuffer);
                cmd.SetComputeBufferParam(shader, kernel, "_VirtualClusters", virtualClustersBuffer);
                cmd.SetComputeBufferParam(shader, kernel, "_GeometryClusterPageIndex", geometryClusterPageIndexBuffer);
                cmd.SetComputeBufferParam(shader, kernel, "_Instances", instanceDataBuffer);
                cmd.SetComputeBufferParam(shader, kernel, "_ClusterSceneIndex", clusterSceneIndexBuffer);
                cmd.SetComputeBufferParam(shader, kernel, "_SceneClusterFirstTri", sceneClusterFirstTriBuffer);
                cmd.SetComputeBufferParam(shader, kernel, "_SceneClusterTriCount", sceneClusterTriCountBuffer);
                cmd.SetComputeIntParam(shader, "_HierarchyGroupCount", hierarchyGroups.Count);
                cmd.SetComputeIntParam(shader, "_HierarchyRefCount", hierarchyClusterRefs.Count);
                cmd.SetComputeIntParam(shader, "_HierarchyGroupResidencyCount", HierarchyGroupResidencyCount);
                cmd.SetComputeBufferParam(shader, kernel, "_HierarchyGroups", hierarchyGroupsBuffer);
                cmd.SetComputeBufferParam(shader, kernel, "_HierarchyClusterRefs", hierarchyClusterRefsBuffer);
                cmd.SetComputeBufferParam(shader, kernel, "_HierarchyGroupResidency", hierarchyGroupResidencyBuffer);
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
                cmd.SetComputeBufferParam(shader, kernel, "_ShadowVisibleDrawClusters0", shadowDrawClusterAppendBuffers[0]);
                cmd.SetComputeBufferParam(shader, kernel, "_ShadowVisibleDrawClusters1", shadowDrawClusterAppendBuffers[1]);
                cmd.SetComputeBufferParam(shader, kernel, "_ShadowVisibleDrawClusters2", shadowDrawClusterAppendBuffers[2]);
                cmd.SetComputeBufferParam(shader, kernel, "_ShadowVisibleDrawClusters3", shadowDrawClusterAppendBuffers[3]);
                cmd.DispatchCompute(
                    shader,
                    kernel,
                    shadowSpatialDispatchArgs[inputQueueIndex],
                    0u);
                inputQueueIndex = outputQueueIndex;
            }
        }

        void RecordShadowHierarchyTraversal(
            UnsafeCommandBuffer cmd,
            bool pageRequestsEnabled,
            ComputeBuffer fallback)
        {
            cmd.SetBufferCounterValue(shadowSpatialQueues[0], 0u);
            cmd.SetComputeIntParam(shader, "_HierarchyGroupCount", hierarchyGroups.Count);
            cmd.SetComputeIntParam(shader, "_HierarchyRefCount", hierarchyClusterRefs.Count);

            int seedKernel = kernelSeedShadowHierarchyRoots;
            cmd.SetComputeBufferParam(shader, seedKernel, "_HierarchyGroups", hierarchyGroupsBuffer);
            cmd.SetComputeBufferParam(shader, seedKernel, "_HierarchyRootGroups", hierarchyRootGroupsBuffer);
            cmd.SetComputeBufferParam(shader, seedKernel, "_Instances", instanceDataBuffer);
            cmd.SetComputeBufferParam(shader, seedKernel, "_ShadowSpatialOutputQueue", shadowSpatialQueues[0]);
            cmd.DispatchCompute(shader, seedKernel, Mathf.Max(1, instanceCount), 1, 1);

            int inputQueueIndex = 0;
            for (int passIndex = 0; passIndex < hierarchyTraversalPassCount; passIndex++)
            {
                int outputQueueIndex = 1 - inputQueueIndex;
                cmd.SetBufferCounterValue(shadowSpatialQueues[outputQueueIndex], 0u);
                cmd.CopyCounterValue(
                    shadowSpatialQueues[inputQueueIndex],
                    shadowSpatialDispatchArgs[inputQueueIndex],
                    0u);
                cmd.SetComputeBufferParam(
                    shader,
                    kernelFinalizeHierarchyDispatch,
                    "_HierarchyDispatchArgsWrite",
                    shadowSpatialDispatchArgs[inputQueueIndex]);
                cmd.DispatchCompute(shader, kernelFinalizeHierarchyDispatch, 1, 1, 1);

                int kernel = kernelTraverseShadowHierarchy;
                cmd.SetComputeBufferParam(shader, kernel, "_HierarchyGroups", hierarchyGroupsBuffer);
                cmd.SetComputeBufferParam(shader, kernel, "_HierarchyClusterRefs", hierarchyClusterRefsBuffer);
                cmd.SetComputeBufferParam(shader, kernel, "_ShadowSpatialInputQueue", shadowSpatialQueues[inputQueueIndex]);
                cmd.SetComputeBufferParam(shader, kernel, "_ShadowSpatialOutputQueue", shadowSpatialQueues[outputQueueIndex]);
                cmd.SetComputeBufferParam(shader, kernel, "_HierarchyDispatchArgs", shadowSpatialDispatchArgs[inputQueueIndex]);
                cmd.SetComputeBufferParam(shader, kernel, "_Clusters", clustersBuffer);
                cmd.SetComputeBufferParam(shader, kernel, "_VirtualClusters", virtualClustersBuffer);
                cmd.SetComputeBufferParam(shader, kernel, "_GeometryClusterPageIndex", geometryClusterPageIndexBuffer);
                cmd.SetComputeBufferParam(shader, kernel, "_Instances", instanceDataBuffer);
                cmd.SetComputeBufferParam(shader, kernel, "_ClusterSceneIndex", clusterSceneIndexBuffer);
                cmd.SetComputeBufferParam(shader, kernel, "_SceneClusterFirstTri", sceneClusterFirstTriBuffer);
                cmd.SetComputeBufferParam(shader, kernel, "_SceneClusterTriCount", sceneClusterTriCountBuffer);
                cmd.SetComputeIntParam(shader, "_HierarchyGroupResidencyCount", HierarchyGroupResidencyCount);
                cmd.SetComputeBufferParam(shader, kernel, "_HierarchyGroupResidency", hierarchyGroupResidencyBuffer);
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
                cmd.SetComputeBufferParam(shader, kernel, "_ShadowVisibleDrawClusters0", shadowDrawClusterAppendBuffers[0]);
                cmd.SetComputeBufferParam(shader, kernel, "_ShadowVisibleDrawClusters1", shadowDrawClusterAppendBuffers[1]);
                cmd.SetComputeBufferParam(shader, kernel, "_ShadowVisibleDrawClusters2", shadowDrawClusterAppendBuffers[2]);
                cmd.SetComputeBufferParam(shader, kernel, "_ShadowVisibleDrawClusters3", shadowDrawClusterAppendBuffers[3]);
                cmd.DispatchCompute(shader, kernel, shadowSpatialDispatchArgs[inputQueueIndex], 0u);
                inputQueueIndex = outputQueueIndex;
            }
        }

        void RecordHierarchyGroupResidency(
            UnsafeCommandBuffer cmd,
            bool pageRequestsEnabled,
            ComputeBuffer fallback)
        {
            if (kernelUpdateHierarchyGroupResidency < 0 || hierarchyGroupResidencyBuffer == null ||
                hierarchyGroups.Count <= 0 || instanceCount <= 0 || sceneAllPagesResident)
                return;

            int kernel = kernelUpdateHierarchyGroupResidency;
            cmd.SetComputeIntParam(shader, "_InstanceCount", instanceCount);
            cmd.SetComputeIntParam(shader, "_HierarchyGroupCount", hierarchyGroups.Count);
            cmd.SetComputeIntParam(shader, "_HierarchyRefCount", hierarchyClusterRefs.Count);
            cmd.SetComputeIntParam(shader, "_HierarchyGroupResidencyCount", HierarchyGroupResidencyCount);
            cmd.SetComputeBufferParam(shader, kernel, "_VirtualClusters", virtualClustersBuffer);
            cmd.SetComputeBufferParam(shader, kernel, "_GeometryClusterPageIndex", geometryClusterPageIndexBuffer);
            cmd.SetComputeBufferParam(shader, kernel, "_Instances", instanceDataBuffer);
            cmd.SetComputeBufferParam(shader, kernel, "_HierarchyGroups", hierarchyGroupsBuffer);
            cmd.SetComputeBufferParam(shader, kernel, "_HierarchyClusterRefs", hierarchyClusterRefsBuffer);
            cmd.SetComputeBufferParam(shader, kernel, "_HierarchyResidencyRefs", hierarchyResidencyRefsBuffer);
            cmd.SetComputeBufferParam(shader, kernel, "_HierarchyGroupResidencyWrite", hierarchyGroupResidencyBuffer);
            cmd.SetComputeBufferParam(shader, kernel, "_PageResidency",
                pageRequestsEnabled ? scenePageResidencyBuffer : fallback);
            cmd.SetComputeBufferParam(shader, kernel, "_PageRequests",
                pageRequestsEnabled ? scenePageRequestBuffer : fallback);
            cmd.DispatchCompute(shader, kernel,
                (HierarchyGroupResidencyCount + kThreadGroupSize - 1) / kThreadGroupSize, 1, 1);
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
            bool useVisibleInstanceQueue,
            Vector3 shadowRayDirection)
        {
            cmd.SetComputeVectorParam(shader, "_CameraPos", new Vector4(
                cameraPosition.x,
                cameraPosition.y,
                cameraPosition.z,
                0f));
            cmd.SetComputeFloatParam(shader, "_ProjectionScale", projectionScale);
            cmd.SetComputeFloatParam(shader, "_OrthographicLodScale", projectionScale);
            cmd.SetComputeFloatParam(shader, "_ShadowLodMinTexels", Mathf.Max(1f, ShadowLodMinTexels));
            // Legacy single-cascade callers do not receive Unity's native
            // ShadowSplitData planes; retain the conservative correctness path.
            cmd.SetComputeVectorParam(shader, "_ShadowCasterPlaneCounts", Vector4.zero);
            Array.Clear(shadowBatchCullingSpheres, 0, shadowBatchCullingSpheres.Length);
            cmd.SetComputeVectorArrayParam(shader, "_ShadowCullingSpheres", shadowBatchCullingSpheres);
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
            Vector3 normalizedShadowRay = shadowRayDirection.sqrMagnitude > 1e-12f
                ? shadowRayDirection.normalized
                : Vector3.forward;
            cmd.SetComputeVectorParam(shader, "_ShadowRayDirection", new Vector4(
                normalizedShadowRay.x,
                normalizedShadowRay.y,
                normalizedShadowRay.z,
                0f));
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
            cmd.SetComputeIntParam(
                shader,
                "_AllPagesResident",
                !pageRequestsEnabled || sceneAllPagesResident ? 1 : 0);
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
            cmd.SetComputeBufferParam(shader, kernel, "_PartVisibleWrite", partVisibleBuffer);
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
            cmd.SetComputeBufferParam(shader, kernel, "_GeometryClusterPageIndex", geometryClusterPageIndexBuffer);
            cmd.SetComputeBufferParam(shader, kernel, "_PartVisible", partVisibleBuffer);
            cmd.SetComputeBufferParam(shader, kernel, "_Instances", instanceDataBuffer);
            cmd.SetComputeBufferParam(shader, kernel, "_InstanceVisible", instanceVisibleBuffer);
            cmd.SetComputeBufferParam(shader, kernel, "_ClusterSceneIndex", clusterSceneIndexBuffer);
            cmd.SetComputeBufferParam(shader, kernel, "_SceneClusterFirstTri", sceneClusterFirstTriBuffer);
            cmd.SetComputeBufferParam(shader, kernel, "_SceneClusterTriCount", sceneClusterTriCountBuffer);
            cmd.SetComputeIntParam(shader, "_HierarchyGroupCount", hierarchyGroups.Count);
            cmd.SetComputeIntParam(shader, "_HierarchyRefCount", hierarchyClusterRefs.Count);
            cmd.SetComputeIntParam(shader, "_HierarchyGroupResidencyCount", HierarchyGroupResidencyCount);
            cmd.SetComputeBufferParam(shader, kernel, "_HierarchyGroups", hierarchyGroupsBuffer);
            cmd.SetComputeBufferParam(shader, kernel, "_HierarchyClusterRefs", hierarchyClusterRefsBuffer);
            cmd.SetComputeBufferParam(shader, kernel, "_HierarchyGroupResidency", hierarchyGroupResidencyBuffer);
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
        public ComputeBuffer VisibleInstanceCountArgsBuffer => visibleInstancePartDispatchArgsBuffer;
        public int UniqueGeometryCount => geometrySlots.Count;
        public int GeometryPartCount => geometryPartCount;
        public int GeometryClusterCount => geometryClusterCount;
        public int VirtualPartCount => partCount;
        public int VirtualClusterCount => clusterCount;
        public bool UsesExpandedVirtualRefs => virtualRefsExpanded;
        public int HierarchyGroupCount => hierarchyGroups.Count;
        public int HierarchyRefCount => hierarchyClusterRefs.Count;
        public int HierarchyTraversalPassCount =>
            kEnableUnsafePersistentTraversal && hierarchyReady && hierarchyPersistentWorkBuffer != null
                ? 1
                : hierarchyTraversalPassCount;
        public int SpatialNodeCount => spatialNodes.Count;
        public int SpatialTraversalPassCount => spatialTraversalPassCount;
        public int SpatialQueueCapacity => spatialQueueCapacity;
        public int DrawQueueCapacity => drawQueueCapacity;
        // Quarantined by default after a D3D12 TDR exposed a wave-level
        // deadlock in the first persistent consumer protocol. The corrected
        // kernel remains available for an explicit GPU-debug validation run;
        // production uses the bounded multi-pass hierarchy until then.
        // Experimental only. Scalar lane-level persistent consumption cannot
        // guarantee both wave progress and late-child draining on Unity's
        // compute model; never enable these in the production renderer.
        public int PartQueueMinVirtualParts { get; set; } = 4096;
        public int InstanceQueueMinInstances { get; set; } = 64;
        public int ShadowPartQueueMinVirtualParts { get; set; } = 32768;
        public int ShadowInstanceQueueMinInstances { get; set; } = 64;
        public bool PreferBvhCandidates { get; set; } = true;
        public bool PreferPartDrivenClusterCull { get; set; } = true;
        public bool EnableTraversalRasterBins { get; set; }
        public bool EnableClusterBackfaceCulling { get; set; }
        public float SoftwareRasterThresholdPixels { get; set; } = 16f;
        public float ShadowLodMinTexels { get; set; } = 1f;
        public bool LastUsedCpuCandidates { get; private set; }
        public bool LastUsedPartDrivenClusterCull { get; private set; }
        public bool LastUsedVisiblePartQueue { get; private set; }
        public bool LastUsedVisibleInstanceQueue { get; private set; }
        public bool LastUsedHierarchyQueue { get; private set; }
        public bool LastUsedSpatialHierarchy { get; private set; }
        public bool LastShadowUsedVisiblePartQueue { get; private set; }
        public bool LastShadowUsedVisibleInstanceQueue { get; private set; }
        public bool LastShadowUsedFusedBatch { get; private set; }
        public bool LastShadowUsedHierarchyQueue { get; private set; }
        public bool LastShadowUsedSpatialHierarchy { get; private set; }
        public ComputeBuffer VisibleDrawClusterBuffer => visibleDrawClusterAppendBuffer;
        public ComputeBuffer VisibleDrawCountArgsBuffer => visibleDrawCountArgsBuffer;
        public ComputeBuffer HardwareRasterClusterBuffer => hardwareRasterClusterAppendBuffer;
        public ComputeBuffer SoftwareRasterClusterBuffer => softwareRasterClusterAppendBuffer;
        public ComputeBuffer HardwareRasterCountArgsBuffer => hardwareRasterCountArgsBuffer;
        public ComputeBuffer SoftwareRasterCountArgsBuffer => softwareRasterCountArgsBuffer;
        public ComputeBuffer SoftwareRasterDispatchArgsBuffer => softwareRasterDispatchArgsBuffer;
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
        public bool IsTraversalRasterBinQueueReady(Camera camera) =>
            EnableTraversalRasterBins &&
            camera != null &&
            lastTraversalRasterBinsFrame == Time.frameCount &&
            lastTraversalRasterBinsCameraId == camera.GetInstanceID() &&
            kernelFinalizeRasterBinArgs >= 0 &&
            IsVisibleDrawQueueReady(camera) &&
            hardwareRasterClusterAppendBuffer != null &&
            softwareRasterClusterAppendBuffer != null &&
            hardwareRasterCountArgsBuffer != null &&
            softwareRasterCountArgsBuffer != null &&
            softwareRasterDispatchArgsBuffer != null;
        // RenderGraph records consumers before the cull pass executes. This is
        // the record-time contract: the configured production path owns valid
        // queue resources and will populate them before a dependent consumer.
        // IsTraversalRasterBinQueueReady remains the execution-time diagnostic.
        public bool CanRecordTraversalRasterBinQueue =>
            EnableTraversalRasterBins &&
            kernelClusterCullVisibleParts >= 0 &&
            kernelFinalizeVisiblePartDispatch >= 0 &&
            kernelFinalizeRasterBinArgs >= 0 &&
            PreferPartDrivenClusterCull &&
            partCount >= Mathf.Max(1, PartQueueMinVirtualParts) &&
            sceneClusterFirstTriBuffer != null &&
            sceneClusterTriCountBuffer != null &&
            visibleDrawClusterAppendBuffer != null &&
            visibleDrawCountArgsBuffer != null &&
            hardwareRasterClusterAppendBuffer != null &&
            softwareRasterClusterAppendBuffer != null &&
            hardwareRasterCountArgsBuffer != null &&
            softwareRasterCountArgsBuffer != null &&
            softwareRasterDispatchArgsBuffer != null;
        public int LastClusterCandidateCount { get; private set; }
        public int LastClusterCount { get; private set; }
        public int LastCull1Drawn { get; private set; }
        public int LastCull2Candidates { get; private set; }
        public int LastCull2Drawn { get; private set; }
        public int LastCullStatsReadbackFrame { get; private set; } = -1;
        public int LastCullStatsCameraId { get; private set; }

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
            Vector3 cameraForward,
            Vector2 projectionNdcScale,
            float projectionScale,
            float zNear,
            Matrix4x4 worldToClip,
            int screenWidth,
            int screenHeight,
            bool useHzb,
            HzbViewParameters hzbView)
        {
            shader.SetVector("_CameraPos", new Vector4(cameraPos.x, cameraPos.y, cameraPos.z, 0f));
            Vector3 normalizedForward = cameraForward.sqrMagnitude > 1e-12f
                ? cameraForward.normalized
                : Vector3.forward;
            shader.SetVector("_CameraForward", new Vector4(
                normalizedForward.x,
                normalizedForward.y,
                normalizedForward.z,
                0f));
            shader.SetVector("_ProjectionNdcScale", new Vector4(
                Mathf.Max(1e-6f, projectionNdcScale.x),
                Mathf.Max(1e-6f, projectionNdcScale.y),
                0f,
                0f));
            shader.SetFloat("_ProjectionScale", projectionScale);
            shader.SetFloat("_OrthographicLodScale", 0f);
            shader.SetInt("_UseOrthographicLod", 0);
            shader.SetFloat("_ZNear", zNear);
            shader.SetMatrix("_WorldToClip", worldToClip);
            HzbViewParameters resolvedHzbView = hzbView.valid
                ? hzbView
                : new HzbViewParameters(
                    worldToClip,
                    cameraPos,
                    normalizedForward,
                    projectionNdcScale,
                    zNear);
            shader.SetVector("_HzbCameraPos", new Vector4(
                resolvedHzbView.cameraPosition.x,
                resolvedHzbView.cameraPosition.y,
                resolvedHzbView.cameraPosition.z,
                0f));
            shader.SetVector("_HzbCameraForward", new Vector4(
                resolvedHzbView.cameraForward.x,
                resolvedHzbView.cameraForward.y,
                resolvedHzbView.cameraForward.z,
                0f));
            shader.SetVector("_HzbProjectionNdcScale", new Vector4(
                resolvedHzbView.projectionNdcScale.x,
                resolvedHzbView.projectionNdcScale.y,
                0f,
                0f));
            shader.SetFloat("_HzbZNear", resolvedHzbView.zNear);
            shader.SetMatrix("_HzbWorldToClip", resolvedHzbView.worldToClip);
            shader.SetVector("_ScreenSize", new Vector4(Mathf.Max(1, screenWidth), Mathf.Max(1, screenHeight), 1f / Mathf.Max(1, screenWidth), 1f / Mathf.Max(1, screenHeight)));
            shader.SetInt("_UseHzb", useHzb ? 1 : 0);
            shader.SetInt("_EnableClusterBackfaceCull", EnableClusterBackfaceCulling ? 1 : 0);
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
                kernelSeedSpatialRoots = shader.FindKernel("CSSeedSpatialRoots");
                kernelTraverseSpatialNodes = shader.FindKernel("CSTraverseSpatialNodes");
                kernelUpdateHierarchyGroupResidency = shader.FindKernel("CSUpdateHierarchyGroupResidency");
                kernelSeedHierarchyRoots = shader.FindKernel("CSSeedHierarchyRoots");
                kernelFinalizeHierarchyDispatch = shader.FindKernel("CSFinalizeHierarchyDispatch");
                kernelTraverseHierarchy = shader.FindKernel("CSTraverseHierarchy");
                kernelClearHierarchyPersistent = shader.FindKernel("CSClearHierarchyPersistent");
                kernelSeedHierarchyPersistent = shader.FindKernel("CSSeedHierarchyPersistent");
                kernelTraverseHierarchyPersistent = shader.FindKernel("CSTraverseHierarchyPersistent");
                kernelSeedShadowHierarchyPersistent = shader.FindKernel("CSSeedShadowHierarchyPersistent");
                kernelTraverseShadowHierarchyPersistent = shader.FindKernel("CSTraverseShadowHierarchyPersistent");
                kernelSeedShadowHierarchyRoots = shader.FindKernel("CSSeedShadowHierarchyRoots");
                kernelTraverseShadowHierarchy = shader.FindKernel("CSTraverseShadowHierarchy");
                try { kernelShadowCullMultiCascadeByPart = shader.FindKernel("CSShadowCullMultiCascadeByPart"); }
                catch { kernelShadowCullMultiCascadeByPart = -1; }
                try { kernelSeedShadowSpatialRoots = shader.FindKernel("CSSeedShadowSpatialRoots"); }
                catch { kernelSeedShadowSpatialRoots = -1; }
                try { kernelTraverseShadowSpatialNodes = shader.FindKernel("CSTraverseShadowSpatialNodes"); }
                catch { kernelTraverseShadowSpatialNodes = -1; }
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
                try { kernelFinalizeRasterBinArgs = shader.FindKernel("CSFinalizeRasterBinArgs"); }
                catch { kernelFinalizeRasterBinArgs = -1; }
                try { kernelRecoverHzbRejectedClusters = shader.FindKernel("CSRecoverHzbRejectedClusters"); }
                catch { kernelRecoverHzbRejectedClusters = -1; }
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
            cullStatsReadbackEpoch++;
            cullStatsReadbackPending = false;
            partsBuffer?.Release();
            clustersBuffer?.Release();
            virtualPartsBuffer?.Release();
            virtualClustersBuffer?.Release();
            geometryClusterPageIndexBuffer?.Release();
            spatialNodesBuffer?.Release();
            spatialPartRefsBuffer?.Release();
            hierarchyGroupsBuffer?.Release();
            hierarchyClusterRefsBuffer?.Release();
            hierarchyGroupResidencyBuffer?.Release();
            hierarchyResidencyRefsBuffer?.Release();
            hierarchyRootGroupsBuffer?.Release();
            hierarchyPersistentWorkBuffer?.Release();
            shadowHierarchyPersistentWorkBuffer?.Release();
            for (int hierarchyQueueIndex = 0; hierarchyQueueIndex < 2; hierarchyQueueIndex++)
            {
                hierarchyQueues[hierarchyQueueIndex]?.Release();
                hierarchyDispatchArgs[hierarchyQueueIndex]?.Release();
                hierarchyQueues[hierarchyQueueIndex] = null;
                hierarchyDispatchArgs[hierarchyQueueIndex] = null;
            }
            for (int queueIndex = 0; queueIndex < 2; queueIndex++)
            {
                shadowSpatialQueues[queueIndex]?.Release();
                shadowSpatialDispatchArgs[queueIndex]?.Release();
                shadowSpatialQueues[queueIndex] = null;
                shadowSpatialDispatchArgs[queueIndex] = null;
            }
            visiblePartAppendBuffer?.Release();
            visiblePartDispatchArgsBuffer?.Release();
            visibleInstanceAppendBuffer?.Release();
            visibleInstancePartDispatchArgsBuffer?.Release();
            visibleDrawClusterAppendBuffer?.Release();
            visibleDrawCountArgsBuffer?.Release();
            hzbRejectedClusterAppendBuffer?.Release();
            hzbRejectedDispatchArgsBuffer?.Release();
            hardwareRasterClusterAppendBuffer?.Release();
            softwareRasterClusterAppendBuffer?.Release();
            hardwareRasterCountArgsBuffer?.Release();
            softwareRasterCountArgsBuffer?.Release();
            softwareRasterDispatchArgsBuffer?.Release();
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
            geometryClusterPageIndexBuffer = null;
            spatialNodesBuffer = null;
            spatialPartRefsBuffer = null;
            hierarchyGroupsBuffer = null;
            hierarchyClusterRefsBuffer = null;
            hierarchyGroupResidencyBuffer = null;
            hierarchyResidencyRefsBuffer = null;
            hierarchyRootGroupsBuffer = null;
            hierarchyPersistentWorkBuffer = null;
            shadowHierarchyPersistentWorkBuffer = null;
            visiblePartAppendBuffer = null;
            visiblePartDispatchArgsBuffer = null;
            visibleInstanceAppendBuffer = null;
            visibleInstancePartDispatchArgsBuffer = null;
            visibleDrawClusterAppendBuffer = null;
            visibleDrawCountArgsBuffer = null;
            hzbRejectedClusterAppendBuffer = null;
            hzbRejectedDispatchArgsBuffer = null;
            hardwareRasterClusterAppendBuffer = null;
            softwareRasterClusterAppendBuffer = null;
            hardwareRasterCountArgsBuffer = null;
            softwareRasterCountArgsBuffer = null;
            softwareRasterDispatchArgsBuffer = null;
            partVisibleBuffer = null;
            visibleClusterAppendBuffer = null;
            visibleCountBuffer = null;
            clusterCandidateBuffer = null;
            clusterSceneIndexBuffer = null;
            geometryClusterPageIndexCpu = null;
            sceneClusterFirstTriBuffer = null;
            sceneClusterTriCountBuffer = null;
            scenePageResidencyBuffer = null;
            scenePageRequestBuffer = null;
            scenePageCount = 0;
            sceneTrackPageUsage = false;
            sceneAllPagesResident = true;
            loggedCameraCullDiagnostics = false;
            instanceDataBuffer = null;
            instanceVisibleBuffer = null;
            fallbackClusterVisibleBuffer = null;
            fallbackPrevVisibleBuffer = null;
            fallbackSecondPassBuffer = null;
            fallbackPass2DrawnBuffer = null;
            cullStatsBuffer = null;
            hierarchyReady = false;
            hierarchyTraversalPassCount = 0;
            hierarchyPersistentCapacity = 0;
            spatialReady = false;
            spatialTraversalPassCount = 0;
            spatialQueueCapacity = 0;
            drawQueueCapacity = 0;
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
            return (error * projectionScale) / distance;
        }
    }
}
