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
        const int kMaxMaterials = 65535;
        const int kLayoutVersion = 8;

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
            public uint localIndexOffset;
            public uint pageId;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct FallbackResidentVertex
        {
            public Vector3 positionOS;
            public Vector2 uv;
            public Vector3 normalOS;
            public Vector4 tangentOS;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct InstanceMaterialRange
        {
            public uint offset;
            public uint count;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public struct GpuMaterialData
        {
            public Vector4 baseColor;
            public Vector4 emissionColor;
            public Vector4 baseMapST;
            // cutoff, smoothness, metallic, bumpScale
            public Vector4 surface0;
            // occlusionStrength, alphaClip, hasBaseMap, hasNormalMap
            public Vector4 surface1;
            // hasMetallicGlossMap, hasOcclusionMap, hasEmissionMap, emissionEnabled
            public Vector4 surface2;
            // smoothnessFromAlbedoAlpha, twoSided, shaderFamily, reserved
            public Vector4 feature0;
        }

        readonly struct MaterialCompatibilityKey : IEquatable<MaterialCompatibilityKey>
        {
            readonly int resolvePipeline;
            readonly int shader;
            readonly int state;
            readonly int baseMap;
            readonly int normalMap;
            readonly int metallicMap;
            readonly int occlusionMap;
            readonly int emissionMap;
            readonly int extension;

            internal MaterialCompatibilityKey(Material material, int resolvePipeline)
            {
                this.resolvePipeline = resolvePipeline;
                shader = material != null && material.shader != null ? material.shader.GetInstanceID() : 0;
                state = MaterialKeywordState(material);
                baseMap = MaterialTextureId(material, "_BaseMap", "_MainTex");
                normalMap = MaterialTextureId(material, "_BumpMap");
                metallicMap = MaterialTextureId(material, "_MetallicGlossMap");
                occlusionMap = MaterialTextureId(material, "_OcclusionMap");
                emissionMap = MaterialTextureId(material, "_EmissionMap");
                extension = 0;
                if (resolvePipeline > 0 &&
                    NaniteMaterialResolveRegistry.TryGet(resolvePipeline, out INaniteMaterialResolveFamily family) &&
                    family is INaniteMaterialResolveDataProvider provider)
                {
                    try
                    {
                        extension = provider.GetCompatibilityHash(material);
                    }
                    catch (Exception exception)
                    {
                        Debug.LogException(exception);
                        extension = material != null ? material.GetInstanceID() : 0;
                    }
                }
            }

            public bool Equals(MaterialCompatibilityKey other) =>
                resolvePipeline == other.resolvePipeline && shader == other.shader && state == other.state &&
                baseMap == other.baseMap && normalMap == other.normalMap &&
                metallicMap == other.metallicMap && occlusionMap == other.occlusionMap &&
                emissionMap == other.emissionMap && extension == other.extension;

            public override bool Equals(object obj) =>
                obj is MaterialCompatibilityKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = resolvePipeline;
                    hash = hash * 397 ^ shader;
                    hash = hash * 397 ^ state;
                    hash = hash * 397 ^ baseMap;
                    hash = hash * 397 ^ normalMap;
                    hash = hash * 397 ^ metallicMap;
                    hash = hash * 397 ^ occlusionMap;
                    hash = hash * 397 ^ emissionMap;
                    return hash * 397 ^ extension;
                }
            }
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
        // Diagnostic lookup for the compact draw ABI. A draw queue entry stores
        // only firstTriangle/count/instance; the immutable first triangle is
        // enough to recover the baked mip without widening the GPU hot queue.
        readonly Dictionary<int, int> geometryMipByFirstTriangle = new Dictionary<int, int>(16384);
        readonly List<int> probeDirtyInstances = new List<int>(32);
        readonly List<Material> materialList = new List<Material>(128);
        readonly List<int> materialResolvePipelines = new List<int>(128);
        readonly List<int> materialResolveFamilies = new List<int>(128);
        readonly List<int> materialCompatibilityBins = new List<int>(128);
        readonly List<int> compatibilityBinRepresentativeMaterialIds = new List<int>(64);
        readonly Dictionary<MaterialCompatibilityKey, int> compatibilityBinByKey =
            new Dictionary<MaterialCompatibilityKey, int>(64);
        readonly Dictionary<int, int> materialIdByObjectId = new Dictionary<int, int>(128);
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
        ComputeBuffer instanceMaterialRangeBuffer;
        ComputeBuffer materialDataBuffer;
        ComputeBuffer instanceShBuffer;
        GraphicsBuffer drawArgsBuffer;
        ComputeBuffer compactedTriIdsBuffer;
        ComputeBuffer compactedTriInstancesBuffer;
        ComputeBuffer compactedTriCountsBuffer;
        ComputeBuffer compactCounterBuffer;
        ComputeBuffer clusterFirstTriBuffer;
        ComputeBuffer clusterTriCountBuffer;
        ComputeBuffer clusterInstanceBuffer;
        ComputeBuffer geometryClusterBoundsBuffer;
        ComputeBuffer geometryClusterLongestEdgeBuffer;
        GraphicsBuffer indexedTrianglePacketBuffer;
        GraphicsBuffer indexedDrawArgsBuffer;
        GraphicsBuffer indexedAppendDrawArgsBuffer;
        GraphicsBuffer indexedFallbackDrawArgsBuffer;
        GraphicsBuffer indexedOverflowClusterBuffer;
        ComputeBuffer indexedOverflowCountBuffer;
        ComputeBuffer indexedBuildDispatchArgsBuffer;
        ComputeBuffer indexedCameraBaseCountBuffer;
        GraphicsBuffer indexedCameraPacketSliceBuffer;
        ComputeBuffer indexedFallbackResidentTableBuffer;
        ComputeBuffer indexedFallbackResidentVertexBuffer;
        ComputeBuffer indexedFallbackResidentIndexBuffer;
        readonly GraphicsBuffer[] indexedShadowDrawArgsBuffers = new GraphicsBuffer[4];
        readonly GraphicsBuffer[] indexedShadowFallbackDrawArgsBuffers = new GraphicsBuffer[4];
        ComputeBuffer indexedShadowSliceDataBuffer;
        readonly ComputeBuffer[] indexedShadowBuildDispatchArgsBuffers = new ComputeBuffer[4];

        Matrix4x4[] instanceLocalToWorldCpu;
        int[] instanceSubMeshMaterialCpu;
        InstanceMaterialRange[] instanceMaterialRangeCpu;
        uint[] clusterVisibleCpu;
        InstanceShData[] instanceShCpu;

        int rebuildSignature;
        int materialBindingSignature;
        int materialDataSignature;
        int registryRevision = -1;
        int lastInstanceUpdateFrame = -1;
        int instanceTransformGeneration;
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
        int indexedDrawBufferMaxMiB = 128;
        bool indexedDrawRequested = true;
        int indexedTrianglePacketCapacity;
        int indexedPacketInstanceBits;
        uint indexedPacketInstanceMask;
        int indexedDrawClusterCapacity;
        int indexedShadowClusterCapacity;
        int indexedShadowDynamicSliceFrame = -1;
        int indexedShadowReadyMask;
        bool warnedMaterialOverflow;
        bool warnedLightmapFallback;
        bool packedPageRasterRequested = true;
        bool pageStreamingRequestsEnabled = true;
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
            pass1ClusterVisibleBuffer != null &&
            pass2ClusterVisibleBuffer != null &&
            secondPassCandidateBuffer != null &&
            instanceLocalToWorldBuffer != null &&
            instanceSubMeshMaterialBuffer != null &&
            instanceMaterialRangeBuffer != null &&
            materialDataBuffer != null &&
            instanceShBuffer != null &&
            drawArgsBuffer != null &&
            compactedTriIdsBuffer != null &&
            compactedTriInstancesBuffer != null &&
            compactedTriCountsBuffer != null &&
            compactCounterBuffer != null &&
            clusterFirstTriBuffer != null &&
            clusterTriCountBuffer != null &&
            clusterInstanceBuffer != null &&
            geometryClusterBoundsBuffer != null &&
            geometryClusterLongestEdgeBuffer != null &&
            slots.Count > 0 &&
            triangleCount > 0 &&
            indexCount > 0;

        public int MaterialCount => materialList.Count;
        public bool RequiresTwoSidedRaster
        {
            get
            {
                for (int materialIndex = 0; materialIndex < materialList.Count; materialIndex++)
                {
                    if (MaterialFloat(materialList[materialIndex], "_Cull", 2f) < 0.5f)
                        return true;
                }
                return false;
            }
        }
        public int MaxMaterialCount => kMaxMaterials;
        public int MaxSubMeshCount => maxSubMeshCount;
        public ComputeBuffer InstanceMaterialRangeBuffer => instanceMaterialRangeBuffer;
        public ComputeBuffer MaterialDataBuffer => materialDataBuffer;
        public int TriangleCount => triangleCount;
        public int VirtualTriangleCount => virtualTriangleCount;
        public int CompactedClusterTriangleSlots => compactedClusterTriangleSlots;
        public int ClusterCount => clusterCount;
        public int InstanceCount => slots.Count;
        public int UniqueMeshCount => geometrySlots.Count;
        public int GeometryVertexCount => geometryVertexCount;
        public int VirtualVertexCount => virtualVertexCount;
        public int GeometryGeneration => geometryGeneration;
        public int InstanceTransformGeneration => instanceTransformGeneration;
        public void RefreshInstanceTransforms() => UpdateInstanceTransforms();
        public int IndexedDrawBufferMaxMiB
        {
            get => indexedDrawBufferMaxMiB;
            set
            {
                int next = Mathf.Clamp(value, 16, 512);
                if (indexedDrawBufferMaxMiB == next)
                    return;
                indexedDrawBufferMaxMiB = next;
                registryRevision = -1;
            }
        }
        public bool IndexedDrawRequested
        {
            get => indexedDrawRequested;
            set
            {
                if (indexedDrawRequested == value)
                    return;
                indexedDrawRequested = value;
                registryRevision = -1;
            }
        }
        public bool IndexedDrawAvailable =>
            indexedTrianglePacketBuffer != null &&
            indexedDrawArgsBuffer != null &&
            indexedAppendDrawArgsBuffer != null &&
            indexedFallbackDrawArgsBuffer != null &&
            indexedOverflowClusterBuffer != null &&
            indexedOverflowCountBuffer != null &&
            indexedBuildDispatchArgsBuffer != null &&
            indexedCameraBaseCountBuffer != null &&
            indexedCameraPacketSliceBuffer != null &&
            indexedDrawClusterCapacity > 0;
        public GraphicsBuffer IndexedTrianglePacketBuffer => indexedTrianglePacketBuffer;
        public int IndexedPacketInstanceBits => indexedPacketInstanceBits;
        public uint IndexedPacketInstanceMask => indexedPacketInstanceMask;
        public GraphicsBuffer IndexedDrawArgsBuffer => indexedDrawArgsBuffer;
        public GraphicsBuffer IndexedAppendDrawArgsBuffer => indexedAppendDrawArgsBuffer;
        public GraphicsBuffer IndexedFallbackDrawArgsBuffer => indexedFallbackDrawArgsBuffer;
        public GraphicsBuffer IndexedOverflowClusterBuffer => indexedOverflowClusterBuffer;
        public GraphicsBuffer IndexedCameraPacketSliceBuffer => indexedCameraPacketSliceBuffer;
        public int IndexedDrawClusterCapacity => indexedDrawClusterCapacity;
        public int IndexedShadowClusterCapacity => indexedShadowClusterCapacity;
        public int IndexedShadowSharedClusterCapacity => indexedDrawClusterCapacity;
        public ComputeBuffer IndexedShadowSliceDataBuffer => indexedShadowSliceDataBuffer;
        public bool IndexedShadowDynamicSlicesReady =>
            indexedShadowSliceDataBuffer != null &&
            indexedShadowDynamicSliceFrame == Time.frameCount &&
            indexedShadowReadyMask != 0;
        public bool IsIndexedShadowCascadeReady(int cascadeIndex) =>
            cascadeIndex >= 0 && cascadeIndex < 4 &&
            indexedShadowSliceDataBuffer != null &&
            indexedShadowDynamicSliceFrame == Time.frameCount &&
            (indexedShadowReadyMask & (1 << cascadeIndex)) != 0;
        public long IndexedDrawBufferBytes =>
            (long)indexedTrianglePacketCapacity * sizeof(uint) +
            (long)indexedDrawClusterCapacity * sizeof(uint);
        public int IndexedTrianglePacketCapacity => indexedTrianglePacketCapacity;
        public GraphicsBuffer GetIndexedShadowDrawArgsBuffer(int cascadeIndex) =>
            cascadeIndex >= 0 && cascadeIndex < indexedShadowDrawArgsBuffers.Length
                ? indexedShadowDrawArgsBuffers[cascadeIndex]
                : null;
        public GraphicsBuffer GetIndexedShadowFallbackDrawArgsBuffer(int cascadeIndex) =>
            cascadeIndex >= 0 && cascadeIndex < indexedShadowFallbackDrawArgsBuffers.Length
                ? indexedShadowFallbackDrawArgsBuffers[cascadeIndex]
                : null;
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
        public bool PageStreamingRequestsEnabled
        {
            get => pageStreamingRequestsEnabled;
            set
            {
                if (pageStreamingRequestsEnabled == value)
                    return;
                pageStreamingRequestsEnabled = value;
                registryRevision = -1;
            }
        }
        public bool IsPagePoolReady => pagePool.IsReady;
        public int GlobalPageCount => pagePool.PageCount;
        public int ResidentPageCount => pagePool.ResidentPageCount;
        public bool AllPagesResident => pagePool.AllPagesResident;
        public int PinnedPageCount => pagePool.PinnedPageCount;
        public int LastRequestedPageCount => pagePool.LastRequestedPageCount;
        public int LastQueuedPageCount => pagePool.LastQueuedPageCount;
        public uint LastMaxPageRequestPriority => pagePool.LastMaxRequestPriority;
        public int RootPageCount => pagePool.RootPageCount;
        public int TotalStreamedPageCount => pagePool.TotalStreamedPageCount;
        public int TotalEvictedPageCount => pagePool.TotalEvictedPageCount;
        public int TotalRecycledAllocationCount => pagePool.TotalRecycledAllocationCount;
        public int RetiredPageAllocationCount => pagePool.RetiredAllocationCount;
        public int PagePoolBytes => pagePool.PoolBytes;
        public int ResidentPagePayloadBytes => pagePool.ResidentBytes;
        public long ResidentGeometryBytes => pagePool.ResidentGeometryBytes;
        public long TotalPageStorageBytesRead => pagePool.TotalStorageBytesRead;
        public int TotalPageStorageReadOperations => pagePool.TotalStorageReadOperations;
        public int StreamingFilePageCount => pagePool.StreamingFilePageCount;
        public long CompatibilityGeometryBytes =>
            (long)(vertexDataBuffer != null ? vertexDataBuffer.count : 0) * sizeof(float) +
            (long)(indexBuffer != null ? indexBuffer.count : 0) * sizeof(int);
        public long TrianglePageRefBytes => (long)triangleCount * sizeof(uint) * 2L;
        public bool PagePoolRequiresEviction => pagePool.RequiresEviction;
        public bool NeedsPageRetirementFence => pagePool.NeedsRetirementFence;
        public GraphicsBuffer PackedPagePoolBuffer => pagePool.PackedPoolBuffer;
        public ComputeBuffer PageTableBuffer => pagePool.PageTableBuffer;
        public ComputeBuffer PageDecodeBuffer => pagePool.PageDecodeBuffer;
        public ComputeBuffer ResidentPageTableBuffer => pagePool.ResidentPageTableBuffer;
        public GraphicsBuffer ResidentVertexBuffer => pagePool.ResidentVertexBuffer;
        public GraphicsBuffer ResidentIndexBuffer => pagePool.ResidentIndexBuffer;
        public ComputeBuffer FallbackResidentPageTableBuffer => indexedFallbackResidentTableBuffer;
        public ComputeBuffer FallbackResidentVertexBuffer => indexedFallbackResidentVertexBuffer;
        public ComputeBuffer FallbackResidentIndexBuffer => indexedFallbackResidentIndexBuffer;
        public int ResidentVertexCapacity =>
            pagePool.ResidentVertexBuffer != null ? pagePool.ResidentVertexBuffer.count : 0;
        public int IndexedVertexDomainCount =>
            UsePackedPageRaster ? ResidentVertexCapacity : geometryVertexCount;
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
        public ComputeBuffer GeometryClusterBoundsBuffer => geometryClusterBoundsBuffer;
        public ComputeBuffer GeometryClusterLongestEdgeBuffer => geometryClusterLongestEdgeBuffer;
        /// <summary>为 true 时跳过 CPU SetData，保留 GPU cull 直接写入的 clusterVisible。</summary>
        public bool GpuVisibleMaskReady { get; private set; }
        public int GpuVisibleMaskCameraId { get; private set; }
        public bool HasPrevVisible =>
            hasPrevVisible &&
            prevClusterVisibleBuffer != null &&
            prevVisibleGeometryGeneration == geometryGeneration;

        public bool IsGpuVisibleMaskReadyFor(Camera camera) =>
            camera != null &&
            GpuVisibleMaskReady &&
            GpuVisibleMaskCameraId == camera.GetInstanceID();

        public void MarkGpuVisibleMaskReady(Camera camera)
        {
            GpuVisibleMaskReady = camera != null;
            GpuVisibleMaskCameraId = camera != null ? camera.GetInstanceID() : 0;
        }

        public void ClearGpuVisibleMaskReady()
        {
            GpuVisibleMaskReady = false;
            GpuVisibleMaskCameraId = 0;
        }

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
            geometryMipByFirstTriangle.Clear();
            materialList.Clear();
            materialResolvePipelines.Clear();
            materialResolveFamilies.Clear();
            materialCompatibilityBins.Clear();
            compatibilityBinRepresentativeMaterialIds.Clear();
            compatibilityBinByKey.Clear();
            materialIdByObjectId.Clear();
            instanceLocalToWorldCpu = null;
            instanceSubMeshMaterialCpu = null;
            instanceMaterialRangeCpu = null;
            clusterVisibleCpu = null;
            instanceShCpu = null;
            ClearGpuVisibleMaskReady();
            rebuildSignature = 0;
            materialBindingSignature = 0;
            materialDataSignature = 0;
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
                int nextBindingSignature = ComputeMaterialBindingSignature();
                if (nextBindingSignature != materialBindingSignature)
                {
                    // Shader, keyword or texture changes can alter resolve-family
                    // admission and compatibility bins. Rebuild those mappings; the
                    // immutable Page files remain resident in the Page pool.
                    if (!Rebuild(proxies))
                        return false;
                    rebuildSignature = signature;
                }
                else
                {
                    GpuMaterialData[] materialData = BuildGpuMaterialData();
                    int nextDataSignature = ComputeMaterialDataSignature(materialData);
                    if (nextDataSignature != materialDataSignature)
                    {
                        // Scalar/color/ST edits do not change geometry or bins. Keep
                        // them as one compact buffer upload instead of rebuilding the
                        // complete GPU Scene.
                        materialDataBuffer.SetData(materialData);
                        materialDataSignature = nextDataSignature;
                    }
                }
                registryRevision = currentRevision;
                UpdateInstanceTransforms();
                return true;
            }

            if (!Rebuild(proxies))
                return false;

            rebuildSignature = signature;
            registryRevision = currentRevision;
            ClearGpuVisibleMaskReady();
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
                hash = hash * 31 + (PageStreamingRequestsEnabled ? 1 : 0);
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
            geometryMipByFirstTriangle.Clear();
            materialList.Clear();
            materialResolvePipelines.Clear();
            materialResolveFamilies.Clear();
            materialCompatibilityBins.Clear();
            compatibilityBinRepresentativeMaterialIds.Clear();
            compatibilityBinByKey.Clear();
            materialIdByObjectId.Clear();
            warnedMaterialOverflow = false;
            warnedLightmapFallback = false;

            int stride = -1;
            int totalVertices = 0;
            int totalIndices = 0;
            int totalTriangles = 0;
            int totalVirtualVertices = 0;
            int totalVirtualTriangles = 0;
            int totalVirtualClusters = 0;
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
                        int pageIndexCount = GetPageIndexCount(page);
                        totalIndices += pageIndexCount;
                        totalTriangles += pageIndexCount / 3;
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

                    // Cluster geometry is immutable and shared by every instance.
                    // The direct draw queue carries instanceId separately, so the
                    // scene address must stay in the unique-geometry domain.
                    slot.pageClusterBase[pageIndex] = slotGeometry.pageClusterBase[pageIndex];
                    slot.pageClusterCount[pageIndex] = page.clusterArray != null ? page.clusterArray.Length : 0;
                    totalVirtualClusters = checked(totalVirtualClusters + slot.pageClusterCount[pageIndex]);
                    totalVirtualVertices += page.vertexCount;
                    totalVirtualTriangles += GetPageIndexCount(page) / 3;
                }

                slots.Add(slot);
            }

            if (slots.Count == 0 || stride < 0 || totalVertices <= 0 || totalIndices <= 0 || totalTriangles <= 0)
                return false;

            var uniqueMeshes = new NaniteMesh[geometrySlots.Count];
            for (int i = 0; i < geometrySlots.Count; i++)
                uniqueMeshes[i] = geometrySlots[i].mesh;
            // An old/oversized Bake may fail the 256 KiB pool contract. Keep compatibility
            // geometry as a fallback, but do not force the complete virtual Page set resident.
            pagePool.EnsureInitialized(uniqueMeshes, Mathf.Max(1, PagePoolMaxMiB));
            UsePackedPageRaster = false;
            if (PackedPageRasterRequested && pagePool.IsReady)
            {
                if (!PageStreamingRequestsEnabled &&
                    pagePool.ResidentPageCount < pagePool.PageCount)
                {
                    Debug.LogWarning(
                        "[Nanite][PagePool] packed raster rejected because demand streaming is disabled; " +
                        "compatibility geometry remains active.");
                }
                // If the complete virtual set fits inside the configured pool, publish it
                // once. Streaming a 66-page mesh through a 512-slot pool only introduces
                // avoidable residency-driven LOD changes and asynchronous request overhead.
                // Larger scenes keep the root-resident demand path unchanged.
                else if ((pagePool.CanFitAllPages
                              ? pagePool.EnsureAllPagesResidentForPackedRaster(
                                  pageTranscodeShader,
                                  out string packedPageError)
                              : pagePool.EnsureResidentWorkingSetForPackedRaster(
                                  pageTranscodeShader,
                                  out packedPageError)))
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
            totalClusters = totalGeometryClusters;
            clusterCount = totalGeometryClusters;
            maxSubMeshCount = Mathf.Max(1, maxSubMesh);
            instanceLocalToWorldCpu = new Matrix4x4[slots.Count];
            instanceMaterialRangeCpu = new InstanceMaterialRange[slots.Count];
            int totalMaterialSlots = 0;
            for (int instanceIndex = 0; instanceIndex < slots.Count; instanceIndex++)
            {
                Slot materialSlot = slots[instanceIndex];
                int materialCount = materialSlot.materials != null ? materialSlot.materials.Length : 0;
                int subMeshCount = materialSlot.mesh != null ? materialSlot.mesh.subMeshCount : 0;
                int slotCount = Mathf.Max(1, Mathf.Max(materialCount, subMeshCount));
                instanceMaterialRangeCpu[instanceIndex] = new InstanceMaterialRange
                {
                    offset = checked((uint)totalMaterialSlots),
                    count = checked((uint)slotCount)
                };
                totalMaterialSlots = checked(totalMaterialSlots + slotCount);
            }
            instanceSubMeshMaterialCpu = new int[Mathf.Max(1, totalMaterialSlots)];
            clusterVisibleCpu = new uint[Mathf.Max(1, totalClusters)];
            var clusterFirstTriCpu = new uint[Mathf.Max(1, totalClusters)];
            var clusterTriCountCpu = new uint[Mathf.Max(1, totalClusters)];
            var clusterInstanceCpu = new uint[Mathf.Max(1, totalClusters)];
            var geometryClusterBoundsCpu = new Vector4[Mathf.Max(1, totalGeometryClusters)];
            var geometryClusterLongestEdgeCpu = new float[Mathf.Max(1, totalGeometryClusters)];
            instanceShCpu = new InstanceShData[slots.Count];

            // Packed production draws address the resident cache through Page ID + local
            // index. Keep only valid placeholder SRVs for legacy shader variants instead of
            // retaining a second decoded copy of the complete virtual geometry.
            bool keepCompatibilityGeometry = !UsePackedPageRaster;
            var mergedVertices = new float[keepCompatibilityGeometry
                ? totalVertices * stride
                : 1];
            var mergedIndices = new int[keepCompatibilityGeometry ? totalIndices : 1];
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
                    localIndexOffset = NaniteGpuPagePool.InvalidSlot,
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

                    if (keepCompatibilityGeometry)
                    {
                        int floatCount = Mathf.Min(page.vertexData.Length, page.vertexCount * stride);
                        Array.Copy(page.vertexData, 0, mergedVertices, vertexBase * stride, floatCount);
                    }

                    int indexBase = triangleBase * 3;
                    if (keepCompatibilityGeometry)
                    {
                        for (int index = 0; index < page.indiceArray.Length; index++)
                            mergedIndices[indexBase + index] = page.indiceArray[index] + vertexBase;
                    }

                    int srcIndexCount = GetPageIndexCount(page);
                    int srcTriangleCount = srcIndexCount / 3;
                    int[] triClusterLocal = BuildTriangleClusterMap(page, srcTriangleCount);
                    if (!ValidatePageTriangleAddressContract(
                            page,
                            triClusterLocal,
                            srcIndexCount,
                            keepCompatibilityGeometry,
                            out string triangleAddressError))
                    {
                        Debug.LogError(
                            $"[Nanite][TriangleAddressContract] mesh={geometry.mesh.name} " +
                            $"page={pageIndex}: {triangleAddressError}. Exact packet raster disabled.");
                        return false;
                    }
                    if (page.clusterArray != null)
                    {
                        for (int clusterIndex = 0; clusterIndex < page.clusterArray.Length; clusterIndex++)
                        {
                            NaniteCluster cluster = page.clusterArray[clusterIndex];
                            int geometryClusterIndex = geometryClusterBase + clusterIndex;
                            if ((uint)geometryClusterIndex < (uint)geometryClusterBoundsCpu.Length)
                            {
                                geometryClusterBoundsCpu[geometryClusterIndex] =
                                    cluster.geometrySphere.w > 0f ? cluster.geometrySphere : cluster.selfSphere;
                                Vector4 geometrySphere = geometryClusterBoundsCpu[geometryClusterIndex];
                                geometryClusterLongestEdgeCpu[geometryClusterIndex] = cluster.longestEdge > 0f
                                    ? cluster.longestEdge
                                    : 2f * Mathf.Max(0f, geometrySphere.w);
                            }
                            int firstTriangle = triangleBase + Mathf.Max(0, cluster.indiceIndex / 3);
                            int mip = page.clusterMip != null && clusterIndex < page.clusterMip.Length
                                ? page.clusterMip[clusterIndex]
                                : 0;
                            geometryMipByFirstTriangle[firstTriangle] = Mathf.Max(0, mip);
                        }
                    }
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
                        mergedTriPage[geometryTriangle] = hasGlobalPage ? globalPageId : pageIndex;
                        mergedTriPageRefs[geometryTriangle] = new TrianglePageRef
                        {
                            // Stable virtual address: local Page index offset + global Page ID.
                            // The shader resolves the current resident indexBase through the
                            // double-buffered table, so eviction never invalidates this array.
                            localIndexOffset = hasGlobalPage
                                ? checked((uint)(triangle * 3))
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
                            int triangleLimit = GetPageIndexCount(page) / 3;
                            int triangleCountInCluster = Mathf.Min(
                                Mathf.Max(0, cl.indiceCount / 3),
                                Mathf.Max(0, triangleLimit - firstTriangle));
                            clusterFirstTriCpu[globalCluster] = (uint)(triangleBase + firstTriangle);
                            clusterTriCountCpu[globalCluster] = (uint)triangleCountInCluster;
                            // Legacy mask compaction stores one representative
                            // instance. Production exact queues carry instanceId
                            // explicitly and do not consume this field.
                            clusterInstanceCpu[globalCluster] = 0u;
                        }
                    }
                }
            }

            // A textureless URP/Lit family with the same fixed-function state as
            // a textured compatibility bin can execute in that bin: material
            // flags skip all texture samples while parameters remain per material.
            // Publish that folding in GPU material data as well as in the CPU
            // submit policy so material-tile compaction sees the exact same bins.
            FoldTexturelessMaterialFamiliesIntoCompatibilityBins();

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
            pass1ClusterVisibleBuffer = new ComputeBuffer(Mathf.Max(1, totalClusters), sizeof(uint), ComputeBufferType.Structured);
            pass2ClusterVisibleBuffer = new ComputeBuffer(Mathf.Max(1, totalClusters), sizeof(uint), ComputeBufferType.Structured);
            secondPassCandidateBuffer = new ComputeBuffer(Mathf.Max(1, totalClusters), sizeof(uint), ComputeBufferType.Structured);
            hasPrevVisible = false;
            prevVisibleGeometryGeneration = -1;
            clusterFirstTriBuffer = new ComputeBuffer(Mathf.Max(1, totalClusters), sizeof(uint), ComputeBufferType.Structured);
            clusterTriCountBuffer = new ComputeBuffer(Mathf.Max(1, totalClusters), sizeof(uint), ComputeBufferType.Structured);
            clusterInstanceBuffer = new ComputeBuffer(Mathf.Max(1, totalClusters), sizeof(uint), ComputeBufferType.Structured);
            geometryClusterBoundsBuffer = new ComputeBuffer(
                Mathf.Max(1, totalGeometryClusters),
                sizeof(float) * 4,
                ComputeBufferType.Structured);
            geometryClusterLongestEdgeBuffer = new ComputeBuffer(
                Mathf.Max(1, totalGeometryClusters),
                sizeof(float),
                ComputeBufferType.Structured);
            instanceLocalToWorldBuffer = new ComputeBuffer(instanceLocalToWorldCpu.Length, sizeof(float) * 16, ComputeBufferType.Structured);
            instanceSubMeshMaterialBuffer = new ComputeBuffer(instanceSubMeshMaterialCpu.Length, sizeof(int), ComputeBufferType.Structured);
            instanceMaterialRangeBuffer = new ComputeBuffer(instanceMaterialRangeCpu.Length, sizeof(uint) * 2, ComputeBufferType.Structured);
            GpuMaterialData[] materialDataCpu = BuildGpuMaterialData();
            materialDataBuffer = new ComputeBuffer(
                Mathf.Max(1, materialDataCpu.Length),
                System.Runtime.InteropServices.Marshal.SizeOf<GpuMaterialData>(),
                ComputeBufferType.Structured);
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
            indexedFallbackResidentTableBuffer = new ComputeBuffer(
                1,
                NaniteGpuPagePool.ResidentPageEntryBytes,
                ComputeBufferType.Structured);
            indexedFallbackResidentVertexBuffer = new ComputeBuffer(
                1,
                NaniteGpuPagePool.ResidentVertexBytes,
                ComputeBufferType.Structured);
            indexedFallbackResidentIndexBuffer = new ComputeBuffer(
                1,
                sizeof(uint),
                ComputeBufferType.Structured);
            indexedFallbackResidentTableBuffer.SetData(new[]
            {
                new NaniteGpuPagePool.GpuResidentPageEntry
                {
                    vertexBase = NaniteGpuPagePool.InvalidSlot,
                    indexBase = NaniteGpuPagePool.InvalidSlot,
                    vertexCount = 0u,
                    indexCount = 0u,
                    flags = 0u,
                    generation = 0u,
                    reserved0 = 0u,
                    reserved1 = 0u
                }
            });
            indexedFallbackResidentVertexBuffer.SetData(new[] { new FallbackResidentVertex() });
            indexedFallbackResidentIndexBuffer.SetData(new uint[] { 0u });

            AllocateIndexedDrawBuffers(totalVirtualClusters);

            vertexDataBuffer.SetData(mergedVertices);
            indexBuffer.SetData(mergedIndices);
            triangleClusterBuffer.SetData(mergedTriCluster);
            trianglePageBuffer.SetData(mergedTriPage);
            trianglePageRefBuffer.SetData(mergedTriPageRefs);
            triangleInstanceBuffer.SetData(mergedTriInstance);
            triangleSubMeshBuffer.SetData(mergedTriSubMesh);
            clusterVisibleBuffer.SetData(clusterVisibleCpu);
            pass1ClusterVisibleBuffer.SetData(clusterVisibleCpu);
            pass2ClusterVisibleBuffer.SetData(clusterVisibleCpu);
            secondPassCandidateBuffer.SetData(clusterVisibleCpu);
            clusterFirstTriBuffer.SetData(clusterFirstTriCpu);
            clusterTriCountBuffer.SetData(clusterTriCountCpu);
            clusterInstanceBuffer.SetData(clusterInstanceCpu);
            geometryClusterBoundsBuffer.SetData(geometryClusterBoundsCpu);
            geometryClusterLongestEdgeBuffer.SetData(geometryClusterLongestEdgeCpu);
            instanceLocalToWorldBuffer.SetData(instanceLocalToWorldCpu);
            instanceSubMeshMaterialBuffer.SetData(instanceSubMeshMaterialCpu);
            instanceMaterialRangeBuffer.SetData(instanceMaterialRangeCpu);
            materialDataBuffer.SetData(materialDataCpu);
            materialBindingSignature = ComputeMaterialBindingSignature();
            materialDataSignature = ComputeMaterialDataSignature(materialDataCpu);
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

        // Zero is the explicit compatibility family (unique textures/custom shader).
        // Families 1..8 are textureless URP-Lit states and can resolve all parameter
        // variants in one pass from GpuMaterialData.
        public int GetMaterialResolveFamily(int materialId)
        {
            return materialId >= 0 && materialId < materialResolveFamilies.Count
                ? materialResolveFamilies[materialId]
                : 0;
        }

        public int GetMaterialResolvePipeline(int materialId) =>
            materialId >= 0 && materialId < materialResolvePipelines.Count
                ? materialResolvePipelines[materialId]
                : -1;

        public int CompatibilityBinCount => compatibilityBinRepresentativeMaterialIds.Count;

        public int GetMaterialCompatibilityBin(int materialId) =>
            materialId >= 0 && materialId < materialCompatibilityBins.Count
                ? materialCompatibilityBins[materialId]
                : 0;

        public int GetCompatibilityBinRepresentativeMaterialId(int compatibilityBin)
        {
            int index = compatibilityBin - 1;
            return index >= 0 && index < compatibilityBinRepresentativeMaterialIds.Count
                ? compatibilityBinRepresentativeMaterialIds[index]
                : -1;
        }

        public int GetCompatibilityBinStateFamily(int compatibilityBin)
        {
            int materialId = GetCompatibilityBinRepresentativeMaterialId(compatibilityBin);
            if (GetMaterialResolvePipeline(materialId) != 0)
                return 0;
            Material material = materialId >= 0 && materialId < materialList.Count
                ? materialList[materialId]
                : null;
            return MaterialKeywordState(material) + 1;
        }

        public bool TryGetGeometryMipForFirstTriangle(uint firstTriangle, out int mip)
        {
            if (firstTriangle > int.MaxValue)
            {
                mip = 0;
                return false;
            }
            return geometryMipByFirstTriangle.TryGetValue((int)firstTriangle, out mip);
        }

        void AllocateIndexedDrawBuffers(int totalClusters)
        {
            indexedTrianglePacketCapacity = 0;
            indexedPacketInstanceBits = RequiredBits(Mathf.Max(0, slots.Count - 1));
            indexedPacketInstanceMask = indexedPacketInstanceBits == 0
                ? 0u
                : ((1u << indexedPacketInstanceBits) - 1u);
            indexedDrawClusterCapacity = 0;
            indexedShadowClusterCapacity = 0;
            if (!indexedDrawRequested || totalClusters <= 0 || compactedClusterTriangleSlots <= 0 ||
                compactedClusterTriangleSlots > 128)
                return;
            int triangleBits = RequiredBits(Mathf.Max(0, triangleCount - 1));
            if (triangleBits + indexedPacketInstanceBits > 32)
                return;

            long packetsPerCluster = compactedClusterTriangleSlots;
            // Exact procedural submission bit-packs triangleId and instanceId
            // into one uint. The bit split is derived from the active GPU Scene;
            // scenes exceeding 32 total bits remain on the compatibility path.
            long budgetTriangles = (long)Mathf.Max(16, indexedDrawBufferMaxMiB) * 1024L * 1024L /
                                   sizeof(uint);
            // Rebuild has already expanded totalClusters across every GPU Scene
            // instance. Multiplying by slots again creates an O(instance^2)
            // overflow queue (154 cars previously allocated about 472 MiB).
            long virtualSceneClusters = totalClusters;
            long desiredPackets = Math.Min(
                (long)int.MaxValue,
                virtualSceneClusters * packetsPerCluster);
            // One uint per actual triangle. 8,388,608 packets cover a complete
            // 4K one-triangle-per-pixel budget; exact whole-cluster overflow is
            // sent to a compact uint index queue instead of reserving 128 slots
            // for every visible cluster.
            const long targetPacketCapacity = 8L * 1024L * 1024L;
            long allocatedPackets = Math.Min(
                desiredPackets,
                Math.Min(budgetTriangles, targetPacketCapacity));
            if (allocatedPackets <= 0 || virtualSceneClusters <= 0)
                return;

            indexedDrawClusterCapacity = (int)Math.Min(int.MaxValue, virtualSceneClusters);
            indexedTrianglePacketCapacity = checked((int)allocatedPackets);
            indexedShadowClusterCapacity = indexedDrawClusterCapacity;

            try
            {
                indexedTrianglePacketBuffer = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured,
                    Mathf.Max(1, indexedTrianglePacketCapacity),
                    sizeof(uint));
                indexedDrawArgsBuffer = new GraphicsBuffer(
                    GraphicsBuffer.Target.IndirectArguments | GraphicsBuffer.Target.Structured,
                    4,
                    sizeof(uint));
                indexedAppendDrawArgsBuffer = new GraphicsBuffer(
                    GraphicsBuffer.Target.IndirectArguments | GraphicsBuffer.Target.Structured,
                    4,
                    sizeof(uint));
                indexedFallbackDrawArgsBuffer = new GraphicsBuffer(
                    GraphicsBuffer.Target.IndirectArguments | GraphicsBuffer.Target.Structured,
                    4,
                    sizeof(uint));
                indexedOverflowClusterBuffer = new GraphicsBuffer(
                    GraphicsBuffer.Target.Append,
                    Mathf.Max(1, indexedDrawClusterCapacity),
                    sizeof(uint));
                indexedOverflowCountBuffer = new ComputeBuffer(
                    1,
                    sizeof(uint),
                    ComputeBufferType.Structured);
                indexedBuildDispatchArgsBuffer = new ComputeBuffer(
                    4,
                    sizeof(uint),
                    ComputeBufferType.IndirectArguments);
                indexedCameraBaseCountBuffer = new ComputeBuffer(1, sizeof(uint));
                indexedCameraPacketSliceBuffer = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured,
                    4,
                    sizeof(uint));
                indexedDrawArgsBuffer.SetData(new uint[] { 0u, 1u, 0u, 0u });
                indexedAppendDrawArgsBuffer.SetData(new uint[] { 0u, 1u, 0u, 0u });
                indexedFallbackDrawArgsBuffer.SetData(new uint[] { 0u, 1u, 0u, 0u });
                indexedOverflowClusterBuffer.SetCounterValue(0u);
                indexedOverflowCountBuffer.SetData(new uint[] { 0u });
                indexedBuildDispatchArgsBuffer.SetData(new uint[] { 0u, 0u, 1u, 0u });
                indexedCameraBaseCountBuffer.SetData(new uint[] { 0u });
                indexedCameraPacketSliceBuffer.SetData(new uint[4]);
                indexedShadowSliceDataBuffer = new ComputeBuffer(4, sizeof(uint) * 4);
                indexedShadowSliceDataBuffer.SetData(new uint[16]);
                for (int cascade = 0; cascade < indexedShadowDrawArgsBuffers.Length; cascade++)
                {
                    indexedShadowDrawArgsBuffers[cascade] = new GraphicsBuffer(
                        GraphicsBuffer.Target.IndirectArguments | GraphicsBuffer.Target.Structured,
                        4,
                        sizeof(uint));
                    indexedShadowFallbackDrawArgsBuffers[cascade] = new GraphicsBuffer(
                        GraphicsBuffer.Target.IndirectArguments | GraphicsBuffer.Target.Structured,
                        4,
                        sizeof(uint));
                    indexedShadowDrawArgsBuffers[cascade].SetData(new uint[] { 0u, 1u, 0u, 0u });
                    indexedShadowFallbackDrawArgsBuffers[cascade].SetData(new uint[] { 0u, 1u, 0u, 0u });
                    indexedShadowBuildDispatchArgsBuffers[cascade] = new ComputeBuffer(
                        4,
                        sizeof(uint),
                        ComputeBufferType.IndirectArguments);
                    indexedShadowBuildDispatchArgsBuffers[cascade].SetData(new uint[] { 0u, 0u, 1u, 0u });
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Nanite][PacketRaster] triangle-packet allocation failed; padded procedural fallback remains active. {e.Message}");
                ReleaseIndexedDrawBuffers();
            }
        }

        public bool DispatchIndexedDrawQueue(
            UnsafeCommandBuffer cmd,
            ComputeShader shader,
            int prepareKernel,
            int buildKernel,
            int finalizeOverflowKernel,
            ComputeBuffer drawClusters,
            ComputeBuffer drawCountArgs,
            int cascadeIndex = -1,
            int clusterOffset = 0,
            bool allowProceduralFallback = true)
        {
            if (!IndexedDrawAvailable || cmd == null || shader == null ||
                prepareKernel < 0 || buildKernel < 0 || finalizeOverflowKernel < 0 ||
                drawClusters == null || drawCountArgs == null ||
                (!UsePackedPageRaster && indexBuffer == null))
                return false;

            bool shadow = cascadeIndex >= 0 && cascadeIndex < 4;
            int capacityClusters = shadow ? indexedShadowClusterCapacity : indexedDrawClusterCapacity;
            GraphicsBuffer indexedArgs = shadow
                ? indexedShadowDrawArgsBuffers[cascadeIndex]
                : indexedDrawArgsBuffer;
            GraphicsBuffer fallbackArgs = shadow
                ? indexedShadowFallbackDrawArgsBuffers[cascadeIndex]
                : indexedFallbackDrawArgsBuffer;
            if (indexedArgs == null || fallbackArgs == null || capacityClusters <= 0)
                return false;

            cmd.SetBufferCounterValue(indexedOverflowClusterBuffer, 0u);

            cmd.SetComputeIntParam(shader, "_CompactedClusterTriangleSlots", compactedClusterTriangleSlots);
            int indexedVertexDomain = IndexedVertexDomainCount;
            cmd.SetComputeIntParam(shader, "_GeometryVertexCount", indexedVertexDomain);
            cmd.SetComputeIntParam(shader, "_UsePackedPageGeometry", UsePackedPageRaster ? 1 : 0);
            cmd.SetComputeIntParam(shader, "_IndexedDrawBaseIndex", 0);
            cmd.SetComputeIntParam(shader, "_IndexedDrawCapacityClusters", capacityClusters);
            cmd.SetComputeIntParam(shader, "_IndexedDrawClusterOffset", Mathf.Max(0, clusterOffset));
            cmd.SetComputeIntParam(shader, "_IndexedAllowProceduralFallback", allowProceduralFallback ? 1 : 0);
            cmd.SetComputeBufferParam(shader, prepareKernel, "_VisibleDrawCountArgs", drawCountArgs);
            cmd.SetComputeBufferParam(shader, prepareKernel, "_IndexedDrawArgs", indexedArgs);
            cmd.SetComputeBufferParam(shader, prepareKernel, "_IndexedFallbackDrawArgs", fallbackArgs);
            cmd.SetComputeBufferParam(shader, prepareKernel, "_IndexedBuildDispatchArgs", indexedBuildDispatchArgsBuffer);
            cmd.SetComputeBufferParam(shader, prepareKernel, "_IndexedCameraBaseCount", indexedCameraBaseCountBuffer);
            cmd.DispatchCompute(shader, prepareKernel, 1, 1, 1);

            cmd.SetComputeBufferParam(shader, buildKernel, "_VisibleDrawCountArgs", drawCountArgs);
            cmd.SetComputeBufferParam(shader, buildKernel, "_CompactedDrawClusters", drawClusters);
            cmd.SetComputeBufferParam(shader, buildKernel, "_IndexedTrianglePackets", indexedTrianglePacketBuffer);
            cmd.SetComputeBufferParam(shader, buildKernel, "_IndexedDrawArgs", indexedArgs);
            cmd.SetComputeBufferParam(shader, buildKernel, "_IndexedBuildDispatchArgs", indexedBuildDispatchArgsBuffer);
            // One group owns one cluster. Prepare writes an exact 2D indirect
            // dispatch, so empty/partial chunks no longer launch the complete
            // configured capacity only to return immediately.
            cmd.SetComputeIntParam(shader, "_IndexedDispatchGroupsX", 1024);
            cmd.SetComputeIntParam(shader, "_IndexedPacketInstanceBits", indexedPacketInstanceBits);
            cmd.SetComputeIntParam(shader, "_IndexedPacketInstanceMask", unchecked((int)indexedPacketInstanceMask));
            cmd.SetComputeIntParam(shader, "_IndexedTrianglePacketCapacity", indexedTrianglePacketCapacity);
            cmd.SetComputeBufferParam(shader, buildKernel, "_IndexedOverflowClusterIndices", indexedOverflowClusterBuffer);
            cmd.DispatchCompute(shader, buildKernel, indexedBuildDispatchArgsBuffer, 0u);
            FinalizeIndexedOverflowArgs(cmd, shader, finalizeOverflowKernel, fallbackArgs);
            return true;
        }

        public bool DispatchIndexedDrawQueueAppend(
            UnsafeCommandBuffer cmd,
            ComputeShader shader,
            int prepareKernel,
            int buildKernel,
            int finalizeOverflowKernel,
            ComputeBuffer drawClusters,
            ComputeBuffer drawCountArgs,
            bool allowProceduralFallback = true)
        {
            if (!IndexedDrawAvailable || cmd == null || shader == null ||
                prepareKernel < 0 || buildKernel < 0 || finalizeOverflowKernel < 0 ||
                drawClusters == null || drawCountArgs == null)
                return false;

            cmd.SetComputeIntParam(shader, "_CompactedClusterTriangleSlots", compactedClusterTriangleSlots);
            cmd.SetComputeIntParam(shader, "_IndexedDrawCapacityClusters", indexedDrawClusterCapacity);
            cmd.SetComputeIntParam(shader, "_IndexedAllowProceduralFallback", allowProceduralFallback ? 1 : 0);
            cmd.SetComputeBufferParam(shader, prepareKernel, "_VisibleDrawCountArgs", drawCountArgs);
            cmd.SetComputeBufferParam(shader, prepareKernel, "_IndexedDrawArgsRead", indexedDrawArgsBuffer);
            cmd.SetComputeBufferParam(shader, prepareKernel, "_IndexedAppendDrawArgs", indexedAppendDrawArgsBuffer);
            cmd.SetComputeBufferParam(shader, prepareKernel, "_IndexedFallbackDrawArgs", indexedFallbackDrawArgsBuffer);
            cmd.SetComputeBufferParam(shader, prepareKernel, "_IndexedBuildDispatchArgs", indexedBuildDispatchArgsBuffer);
            cmd.SetComputeBufferParam(shader, prepareKernel, "_IndexedCameraBaseCountRead", indexedCameraBaseCountBuffer);
            cmd.SetComputeBufferParam(shader, prepareKernel, "_IndexedCameraPacketSlice", indexedCameraPacketSliceBuffer);
            cmd.DispatchCompute(shader, prepareKernel, 1, 1, 1);

            cmd.SetComputeIntParam(shader, "_IndexedDispatchGroupsX", 1024);
            cmd.SetComputeIntParam(shader, "_IndexedPacketInstanceBits", indexedPacketInstanceBits);
            cmd.SetComputeIntParam(shader, "_IndexedPacketInstanceMask", unchecked((int)indexedPacketInstanceMask));
            cmd.SetComputeIntParam(shader, "_IndexedTrianglePacketCapacity", indexedTrianglePacketCapacity);
            cmd.SetComputeBufferParam(shader, buildKernel, "_VisibleDrawCountArgs", drawCountArgs);
            cmd.SetComputeBufferParam(shader, buildKernel, "_CompactedDrawClusters", drawClusters);
            cmd.SetComputeBufferParam(shader, buildKernel, "_IndexedTrianglePackets", indexedTrianglePacketBuffer);
            cmd.SetComputeBufferParam(shader, buildKernel, "_IndexedDrawArgs", indexedDrawArgsBuffer);
            cmd.SetComputeBufferParam(shader, buildKernel, "_IndexedAppendDrawArgs", indexedAppendDrawArgsBuffer);
            cmd.SetComputeBufferParam(shader, buildKernel, "_IndexedCameraBaseCountRead", indexedCameraBaseCountBuffer);
            cmd.SetComputeBufferParam(shader, buildKernel, "_IndexedCameraPacketSliceRead", indexedCameraPacketSliceBuffer);
            cmd.SetComputeBufferParam(shader, buildKernel, "_IndexedBuildDispatchArgs", indexedBuildDispatchArgsBuffer);
            cmd.SetComputeBufferParam(shader, buildKernel, "_IndexedOverflowClusterIndices", indexedOverflowClusterBuffer);
            cmd.DispatchCompute(shader, buildKernel, indexedBuildDispatchArgsBuffer, 0u);
            FinalizeIndexedOverflowArgs(
                cmd,
                shader,
                finalizeOverflowKernel,
                indexedFallbackDrawArgsBuffer);
            return true;
        }

        void FinalizeIndexedOverflowArgs(
            UnsafeCommandBuffer cmd,
            ComputeShader shader,
            int finalizeKernel,
            GraphicsBuffer fallbackArgs)
        {
            cmd.CopyCounterValue(indexedOverflowClusterBuffer, indexedOverflowCountBuffer, 0u);
            cmd.SetComputeIntParam(shader, "_CompactedClusterTriangleSlots", compactedClusterTriangleSlots);
            cmd.SetComputeBufferParam(shader, finalizeKernel, "_VisibleDrawCountArgs", indexedOverflowCountBuffer);
            cmd.SetComputeBufferParam(shader, finalizeKernel, "_DrawArgs", fallbackArgs);
            cmd.DispatchCompute(shader, finalizeKernel, 1, 1, 1);
        }

        /// <summary>
        /// Builds one cascade into the complete indexed scratch allocation. The
        /// render graph guarantees build(N) -> raster(N) -> build(N+1), so no
        /// four-way partition or simultaneous lifetime is required.
        /// </summary>
        public bool DispatchIndexedShadowDrawQueueReuse(
            UnsafeCommandBuffer cmd,
            ComputeShader shader,
            int prepareKernel,
            int buildKernel,
            int finalizeOverflowKernel,
            ComputeBuffer drawClusters,
            ComputeBuffer drawCountArgs,
            int cascadeIndex)
        {
            if (!IndexedDrawAvailable || cmd == null || shader == null ||
                prepareKernel < 0 || buildKernel < 0 || finalizeOverflowKernel < 0 ||
                drawClusters == null || drawCountArgs == null ||
                cascadeIndex < 0 || cascadeIndex >= 4 ||
                indexedShadowSliceDataBuffer == null ||
                indexedShadowDrawArgsBuffers[cascadeIndex] == null ||
                indexedShadowFallbackDrawArgsBuffers[cascadeIndex] == null ||
                indexedShadowBuildDispatchArgsBuffers[cascadeIndex] == null ||
                (!UsePackedPageRaster && indexBuffer == null))
                return false;

            if (indexedShadowDynamicSliceFrame != Time.frameCount)
            {
                indexedShadowDynamicSliceFrame = Time.frameCount;
                indexedShadowReadyMask = 0;
            }

            GraphicsBuffer indexedArgs = indexedShadowDrawArgsBuffers[cascadeIndex];
            GraphicsBuffer fallbackArgs = indexedShadowFallbackDrawArgsBuffers[cascadeIndex];
            ComputeBuffer dispatchArgs = indexedShadowBuildDispatchArgsBuffers[cascadeIndex];

            cmd.SetBufferCounterValue(indexedOverflowClusterBuffer, 0u);

            cmd.SetComputeIntParam(shader, "_CompactedClusterTriangleSlots", compactedClusterTriangleSlots);
            cmd.SetComputeIntParam(shader, "_GeometryVertexCount", IndexedVertexDomainCount);
            cmd.SetComputeIntParam(shader, "_UsePackedPageGeometry", UsePackedPageRaster ? 1 : 0);
            cmd.SetComputeIntParam(shader, "_IndexedDrawTotalCapacityClusters", indexedDrawClusterCapacity);
            cmd.SetComputeIntParam(shader, "_IndexedShadowCascadeIndex", cascadeIndex);
            cmd.SetComputeIntParam(shader, "_IndexedShadowReuseScratch", 1);
            cmd.SetComputeBufferParam(shader, prepareKernel, "_VisibleDrawCountArgs", drawCountArgs);
            // Reuse mode only reads the active cascade count. Binding the same
            // valid buffer to all slots keeps the kernel ABI deterministic.
            cmd.SetComputeBufferParam(shader, prepareKernel, "_IndexedShadowVisibleDrawCountArgs0", drawCountArgs);
            cmd.SetComputeBufferParam(shader, prepareKernel, "_IndexedShadowVisibleDrawCountArgs1", drawCountArgs);
            cmd.SetComputeBufferParam(shader, prepareKernel, "_IndexedShadowVisibleDrawCountArgs2", drawCountArgs);
            cmd.SetComputeBufferParam(shader, prepareKernel, "_IndexedShadowVisibleDrawCountArgs3", drawCountArgs);
            cmd.SetComputeBufferParam(shader, prepareKernel, "_IndexedDrawArgs", indexedArgs);
            cmd.SetComputeBufferParam(shader, prepareKernel, "_IndexedFallbackDrawArgs", fallbackArgs);
            cmd.SetComputeBufferParam(shader, prepareKernel, "_IndexedShadowSliceData", indexedShadowSliceDataBuffer);
            cmd.SetComputeBufferParam(shader, prepareKernel, "_IndexedShadowBuildDispatchArgs", dispatchArgs);
            cmd.DispatchCompute(shader, prepareKernel, 1, 1, 1);

            cmd.SetComputeBufferParam(shader, buildKernel, "_VisibleDrawCountArgs", drawCountArgs);
            cmd.SetComputeBufferParam(shader, buildKernel, "_CompactedDrawClusters", drawClusters);
            cmd.SetComputeBufferParam(shader, buildKernel, "_IndexedTrianglePackets", indexedTrianglePacketBuffer);
            cmd.SetComputeIntParam(shader, "_IndexedPacketInstanceBits", indexedPacketInstanceBits);
            cmd.SetComputeIntParam(shader, "_IndexedPacketInstanceMask", unchecked((int)indexedPacketInstanceMask));
            cmd.SetComputeIntParam(shader, "_IndexedTrianglePacketCapacity", indexedTrianglePacketCapacity);
            cmd.SetComputeBufferParam(shader, buildKernel, "_IndexedShadowSliceData", indexedShadowSliceDataBuffer);
            cmd.SetComputeBufferParam(shader, buildKernel, "_IndexedDrawArgs", indexedArgs);
            cmd.SetComputeBufferParam(shader, buildKernel, "_IndexedShadowBuildDispatchArgs", dispatchArgs);
            cmd.SetComputeBufferParam(shader, buildKernel, "_IndexedOverflowClusterIndices", indexedOverflowClusterBuffer);
            cmd.DispatchCompute(shader, buildKernel, dispatchArgs, 0u);
            FinalizeIndexedOverflowArgs(cmd, shader, finalizeOverflowKernel, fallbackArgs);

            indexedShadowReadyMask |= 1 << cascadeIndex;
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

        static int RequiredBits(int maxValue)
        {
            uint value = (uint)Mathf.Max(0, maxValue);
            int bits = 0;
            while (value != 0u)
            {
                bits++;
                value >>= 1;
            }
            return bits;
        }

        int lastShUploadFrame = -100000;

        GpuMaterialData[] BuildGpuMaterialData()
        {
            int count = Mathf.Max(1, materialList.Count);
            var result = new GpuMaterialData[count];
            for (int materialIndex = 0; materialIndex < count; materialIndex++)
            {
                Material material = materialIndex < materialList.Count
                    ? materialList[materialIndex]
                    : EnsureFallbackMaterial();
                bool emissionEnabled = MaterialEmissionEnabled(material);
                bool hasEmissionMap = emissionEnabled && MaterialHasTexture(material, "_EmissionMap");
                bool doubleSided = MaterialFloat(material, "_Cull", 2f) < 0.5f;
                GpuMaterialData materialData = new GpuMaterialData
                {
                    baseColor = MaterialColor(material, "_BaseColor", "_Color", Color.white),
                    emissionColor = MaterialColor(material, "_EmissionColor", "_Color", Color.black),
                    baseMapST = MaterialVector(
                        material,
                        "_BaseMap_ST",
                        MaterialVector(material, "_MainTex_ST", new Vector4(1f, 1f, 0f, 0f))),
                    surface0 = new Vector4(
                        MaterialFloat(material, "_Cutoff", 0f),
                        MaterialFloat(material, "_Smoothness", 0.5f),
                        MaterialFloat(material, "_Metallic", 0f),
                        MaterialFloat(material, "_BumpScale", 1f)),
                    surface1 = new Vector4(
                        MaterialFloat(material, "_OcclusionStrength", 1f),
                        MaterialAlphaClipEnabled(material) ? 1f : 0f,
                        MaterialHasTexture(material, "_BaseMap") || MaterialHasTexture(material, "_MainTex") ? 1f : 0f,
                        MaterialHasTexture(material, "_BumpMap") ? 1f : 0f),
                    surface2 = new Vector4(
                        MaterialHasTexture(material, "_MetallicGlossMap") ? 1f : 0f,
                        MaterialHasTexture(material, "_OcclusionMap") ? 1f : 0f,
                        hasEmissionMap ? 1f : 0f,
                        emissionEnabled ? 1f : 0f),
                    feature0 = new Vector4(
                        material != null && material.IsKeywordEnabled("_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A") ? 1f : 0f,
                        doubleSided ? 1f : 0f,
                        ComputeMaterialResolveFamily(material),
                        GetMaterialCompatibilityBin(materialIndex))
                };
                int resolvePipeline = materialIndex < materialResolvePipelines.Count
                    ? materialResolvePipelines[materialIndex]
                    : 0;
                if (resolvePipeline > 0 &&
                    NaniteMaterialResolveRegistry.TryGet(resolvePipeline, out INaniteMaterialResolveFamily family) &&
                    family is INaniteMaterialResolveDataProvider provider)
                {
                    try
                    {
                        provider.Populate(material, ref materialData);
                    }
                    catch (Exception exception)
                    {
                        Debug.LogException(exception);
                    }
                }
                result[materialIndex] = materialData;
            }
            return result;
        }

        int ComputeMaterialBindingSignature()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + NaniteMaterialResolveRegistry.Revision;
                hash = hash * 31 + materialList.Count;
                for (int index = 0; index < materialList.Count; index++)
                {
                    Material material = materialList[index];
                    int pipeline = ResolveMaterialPipeline(material);
                    hash = hash * 31 + (material != null ? material.GetInstanceID() : 0);
                    hash = hash * 31 + pipeline;
                    hash = hash * 31 + new MaterialCompatibilityKey(material, pipeline).GetHashCode();
                }
                return hash;
            }
        }

        static int ComputeMaterialDataSignature(GpuMaterialData[] data)
        {
            unchecked
            {
                int hash = data != null ? data.Length : 0;
                if (data == null)
                    return hash;
                for (int index = 0; index < data.Length; index++)
                {
                    hash = HashVector(hash, data[index].baseColor);
                    hash = HashVector(hash, data[index].emissionColor);
                    hash = HashVector(hash, data[index].baseMapST);
                    hash = HashVector(hash, data[index].surface0);
                    hash = HashVector(hash, data[index].surface1);
                    hash = HashVector(hash, data[index].surface2);
                    hash = HashVector(hash, data[index].feature0);
                }
                return hash;
            }
        }

        static int HashVector(int hash, Vector4 value)
        {
            unchecked
            {
                hash = hash * 397 ^ value.x.GetHashCode();
                hash = hash * 397 ^ value.y.GetHashCode();
                hash = hash * 397 ^ value.z.GetHashCode();
                return hash * 397 ^ value.w.GetHashCode();
            }
        }

        static bool MaterialHasTexture(Material material, string propertyName)
        {
            if (material == null || !material.HasProperty(propertyName))
                return false;
            Texture texture = material.GetTexture(propertyName);
            if (texture == null || texture == Texture2D.whiteTexture || texture == Texture2D.blackTexture ||
                texture == Texture2D.grayTexture ||
                (Texture2D.normalTexture != null && texture == Texture2D.normalTexture))
                return false;
            return true;
        }

        static int ComputeMaterialResolveFamily(Material material)
        {
            if (!IsBuiltInUrpLitMaterial(material))
                return 0;
            if (MaterialHasTexture(material, "_BaseMap") ||
                MaterialHasTexture(material, "_MainTex") ||
                MaterialHasTexture(material, "_BumpMap") ||
                MaterialHasTexture(material, "_MetallicGlossMap") ||
                MaterialHasTexture(material, "_OcclusionMap") ||
                MaterialHasTexture(material, "_EmissionMap"))
                return 0;

            int state = 0;
            if (material.IsKeywordEnabled("_ENVIRONMENTREFLECTIONS_OFF")) state |= 1;
            if (material.IsKeywordEnabled("_SPECULARHIGHLIGHTS_OFF")) state |= 2;
            if (material.IsKeywordEnabled("_RECEIVE_SHADOWS_OFF")) state |= 4;
            return state + 1;
        }

        // A shader family is a code contract, not a matching set of property names.
        // The current formal resolve implements URP/Lit. Other programs must remain
        // on their native Renderer until they provide a dedicated Nanite family.
        public static bool SupportsFormalResolveMaterial(Material material)
        {
            if (IsBuiltInUrpLitMaterial(material))
                return true;
            return NaniteMaterialResolveRegistry.TryResolve(material, out _);
        }

        static bool IsBuiltInUrpLitMaterial(Material material)
        {
            if (material == null || material.shader == null)
                return true;
            string shaderName = material.shader.name ?? string.Empty;
            return shaderName.Equals("Universal Render Pipeline/Lit", StringComparison.OrdinalIgnoreCase) ||
                   shaderName.EndsWith("/URP/Lit", StringComparison.OrdinalIgnoreCase);
        }

        static int ResolveMaterialPipeline(Material material)
        {
            if (IsBuiltInUrpLitMaterial(material))
                return 0;
            return NaniteMaterialResolveRegistry.TryResolve(material, out int familyId)
                ? familyId
                : -1;
        }

        static int MaterialKeywordState(Material material)
        {
            if (material == null)
                return 0;
            int state = 0;
            if (material.IsKeywordEnabled("_ENVIRONMENTREFLECTIONS_OFF")) state |= 1;
            if (material.IsKeywordEnabled("_SPECULARHIGHLIGHTS_OFF")) state |= 2;
            if (material.IsKeywordEnabled("_RECEIVE_SHADOWS_OFF")) state |= 4;
            return state;
        }

        static int MaterialTextureId(Material material, string propertyName, string alias = null)
        {
            if (material == null)
                return 0;
            Texture texture = material.HasProperty(propertyName)
                ? material.GetTexture(propertyName)
                : null;
            if (texture == null && !string.IsNullOrEmpty(alias) && material.HasProperty(alias))
                texture = material.GetTexture(alias);
            return texture != null ? texture.GetInstanceID() : 0;
        }

        static bool MaterialEmissionEnabled(Material material) =>
            material != null &&
            (material.IsKeywordEnabled("_EMISSION") ||
             (material.HasProperty("_EmissionEnabled") && material.GetFloat("_EmissionEnabled") > 0.5f));

        static bool MaterialAlphaClipEnabled(Material material) =>
            material != null &&
            (material.IsKeywordEnabled("_ALPHATEST_ON") ||
             material.IsKeywordEnabled("_ALPHACLIP_ON") ||
             (material.HasProperty("_AlphaClip") && material.GetFloat("_AlphaClip") > 0.5f));

        static float MaterialFloat(Material material, string propertyName, float fallback) =>
            material != null && material.HasProperty(propertyName)
                ? material.GetFloat(propertyName)
                : fallback;

        static Vector4 MaterialVector(Material material, string propertyName, Vector4 fallback) =>
            material != null && material.HasProperty(propertyName)
                ? material.GetVector(propertyName)
                : fallback;

        static Color MaterialColor(Material material, string propertyA, string propertyB, Color fallback)
        {
            if (material != null && material.HasProperty(propertyA))
                return material.GetColor(propertyA);
            if (material != null && material.HasProperty(propertyB))
                return material.GetColor(propertyB);
            return fallback;
        }

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
            {
                instanceTransformGeneration++;
                UploadDirtyArrayRanges(
                    instanceLocalToWorldBuffer,
                    instanceLocalToWorldCpu,
                    probeDirtyInstances);
            }

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

            if (periodicRefresh)
            {
                instanceShBuffer?.SetData(instanceShCpu);
            }
            else
            {
                UploadDirtyArrayRanges(
                    instanceShBuffer,
                    instanceShCpu,
                    probeDirtyInstances);
            }
            lastShUploadFrame = Time.frameCount;
        }

        static void UploadDirtyArrayRanges<T>(
            ComputeBuffer destination,
            T[] source,
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

        void FillInstanceSubMeshMaterial(int instanceIndex, Material[] materials)
        {
            if ((uint)instanceIndex >= (uint)instanceMaterialRangeCpu.Length)
                return;

            InstanceMaterialRange range = instanceMaterialRangeCpu[instanceIndex];
            int baseOffset = checked((int)range.offset);
            int slotCount = checked((int)range.count);
            Material fallback = EnsureFallbackMaterial();
            for (int s = 0; s < slotCount; s++)
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
            int resolvePipeline = ResolveMaterialPipeline(resolved);
            materialResolvePipelines.Add(resolvePipeline);
            int family = resolvePipeline == 0 ? ComputeMaterialResolveFamily(resolved) : 0;
            materialResolveFamilies.Add(family);
            int compatibilityBin = 0;
            if (family == 0)
            {
                var keyForBin = new MaterialCompatibilityKey(resolved, resolvePipeline);
                if (!compatibilityBinByKey.TryGetValue(keyForBin, out compatibilityBin))
                {
                    compatibilityBin = compatibilityBinRepresentativeMaterialIds.Count + 1;
                    compatibilityBinByKey.Add(keyForBin, compatibilityBin);
                    compatibilityBinRepresentativeMaterialIds.Add(id);
                }
            }
            materialCompatibilityBins.Add(compatibilityBin);
            materialIdByObjectId[key] = id;
            return id;
        }

        void FoldTexturelessMaterialFamiliesIntoCompatibilityBins()
        {
            if (materialCompatibilityBins.Count != materialList.Count ||
                materialResolveFamilies.Count != materialList.Count)
                return;

            for (int materialId = 0; materialId < materialList.Count; materialId++)
            {
                int family = materialResolveFamilies[materialId];
                if (family <= 0 || materialCompatibilityBins[materialId] > 0)
                    continue;

                for (int compatibilityBin = 1;
                     compatibilityBin <= compatibilityBinRepresentativeMaterialIds.Count;
                     compatibilityBin++)
                {
                    if (GetCompatibilityBinStateFamily(compatibilityBin) != family)
                        continue;
                    materialCompatibilityBins[materialId] = compatibilityBin;
                    break;
                }
            }
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
            if (renderer != null && renderer.sharedMaterials != null && renderer.sharedMaterials.Length > 0)
                return renderer.sharedMaterials;
            if (proxy != null && proxy.naniteMesh != null &&
                proxy.naniteMesh.sourceMaterials != null && proxy.naniteMesh.sourceMaterials.Length > 0)
                return proxy.naniteMesh.sourceMaterials;
            return new Material[0];
        }

        static bool IsValidProxy(NaniteRuntimeProxy proxy) =>
            proxy != null &&
            proxy.isActiveAndEnabled &&
            proxy.NaniteRenderingActive &&
            proxy.naniteMesh != null &&
            proxy.naniteMesh.pageArray != null &&
            proxy.naniteMesh.pageArray.Length > 0;

        static bool IsValidPage(NaniteMeshPage page)
        {
            if (page == null || page.vertexCount <= 0 ||
                page.clusterArray == null || page.clusterArray.Length == 0)
                return false;
            if (page.HasBinaryPayload && page.BinaryVertexCount == page.vertexCount &&
                page.BinaryIndexCount >= 3)
                return true;
            return page.EnsureLegacyPayload(out _) && page.indiceArray.Length >= 3;
        }

        static int GetPageIndexCount(NaniteMeshPage page)
        {
            if (page == null)
                return 0;
            int binaryCount = page.BinaryIndexCount;
            if (page.HasBinaryPayload && binaryCount > 0)
                return binaryCount;
            return page.EnsureLegacyPayload(out _) ? page.indiceArray.Length : 0;
        }

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

        static bool ValidatePageTriangleAddressContract(
            NaniteMeshPage page,
            int[] triangleCluster,
            int indexCount,
            bool validateLogicalIndices,
            out string error)
        {
            error = string.Empty;
            if (page == null || triangleCluster == null)
            {
                error = "missing Page/index/triangle map";
                return false;
            }
            if (indexCount < 3 || indexCount % 3 != 0 ||
                triangleCluster.Length != indexCount / 3)
            {
                error = "index count is not a complete triangle domain";
                return false;
            }

            if (validateLogicalIndices && !page.EnsureLegacyPayload(out string decodeError))
            {
                error = $"index payload decode failed: {decodeError}";
                return false;
            }
            for (int index = 0; validateLogicalIndices && index < indexCount; index++)
            {
                int logicalVertex = page.indiceArray[index];
                if ((uint)logicalVertex >= (uint)page.vertexCount)
                {
                    error = $"index[{index}]={logicalVertex} exceeds vertexCount={page.vertexCount}";
                    return false;
                }
            }

            var ownership = new int[triangleCluster.Length];
            for (int triangle = 0; triangle < ownership.Length; triangle++)
                ownership[triangle] = -1;
            if (page.clusterArray == null || page.clusterArray.Length == 0)
            {
                error = "Page has no clusters";
                return false;
            }
            for (int clusterIndex = 0; clusterIndex < page.clusterArray.Length; clusterIndex++)
            {
                NaniteCluster cluster = page.clusterArray[clusterIndex];
                if (cluster.indiceIndex < 0 || cluster.indiceCount <= 0 ||
                    cluster.indiceIndex % 3 != 0 || cluster.indiceCount % 3 != 0 ||
                    cluster.indiceIndex > indexCount - cluster.indiceCount)
                {
                    error = $"cluster[{clusterIndex}] has invalid index range " +
                            $"[{cluster.indiceIndex},{cluster.indiceCount}]";
                    return false;
                }
                int firstTriangle = cluster.indiceIndex / 3;
                int endTriangle = firstTriangle + cluster.indiceCount / 3;
                for (int triangle = firstTriangle; triangle < endTriangle; triangle++)
                {
                    if (ownership[triangle] >= 0)
                    {
                        error = $"triangle[{triangle}] is owned by clusters " +
                                $"{ownership[triangle]} and {clusterIndex}";
                        return false;
                    }
                    ownership[triangle] = clusterIndex;
                }
            }
            for (int triangle = 0; triangle < ownership.Length; triangle++)
            {
                if (ownership[triangle] < 0 || triangleCluster[triangle] != ownership[triangle])
                {
                    error = $"triangle[{triangle}] has no stable cluster owner";
                    return false;
                }
            }
            return true;
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
            ReleaseIndexedDrawBuffers();
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
            instanceMaterialRangeBuffer?.Release();
            materialDataBuffer?.Release();
            instanceShBuffer?.Release();
            drawArgsBuffer?.Dispose();
            compactedTriIdsBuffer?.Release();
            compactedTriInstancesBuffer?.Release();
            compactedTriCountsBuffer?.Release();
            compactCounterBuffer?.Release();
            clusterFirstTriBuffer?.Release();
            clusterTriCountBuffer?.Release();
            clusterInstanceBuffer?.Release();
            geometryClusterBoundsBuffer?.Release();
            geometryClusterLongestEdgeBuffer?.Release();
            indexedFallbackResidentTableBuffer?.Release();
            indexedFallbackResidentVertexBuffer?.Release();
            indexedFallbackResidentIndexBuffer?.Release();

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
            instanceMaterialRangeBuffer = null;
            materialDataBuffer = null;
            instanceShBuffer = null;
            drawArgsBuffer = null;
            compactedTriIdsBuffer = null;
            compactedTriInstancesBuffer = null;
            compactedTriCountsBuffer = null;
            compactCounterBuffer = null;
            clusterFirstTriBuffer = null;
            clusterTriCountBuffer = null;
            clusterInstanceBuffer = null;
            geometryClusterBoundsBuffer = null;
            geometryClusterLongestEdgeBuffer = null;
            indexedFallbackResidentTableBuffer = null;
            indexedFallbackResidentVertexBuffer = null;
            indexedFallbackResidentIndexBuffer = null;
        }

        void ReleaseIndexedDrawBuffers()
        {
            indexedTrianglePacketBuffer?.Dispose();
            indexedDrawArgsBuffer?.Dispose();
            indexedAppendDrawArgsBuffer?.Dispose();
            indexedFallbackDrawArgsBuffer?.Dispose();
            indexedOverflowClusterBuffer?.Dispose();
            indexedOverflowCountBuffer?.Release();
            indexedBuildDispatchArgsBuffer?.Release();
            indexedCameraBaseCountBuffer?.Release();
            indexedCameraPacketSliceBuffer?.Dispose();
            indexedTrianglePacketBuffer = null;
            indexedDrawArgsBuffer = null;
            indexedAppendDrawArgsBuffer = null;
            indexedFallbackDrawArgsBuffer = null;
            indexedOverflowClusterBuffer = null;
            indexedOverflowCountBuffer = null;
            indexedBuildDispatchArgsBuffer = null;
            indexedCameraBaseCountBuffer = null;
            indexedCameraPacketSliceBuffer = null;
            indexedShadowDynamicSliceFrame = -1;
            indexedShadowSliceDataBuffer?.Release();
            indexedShadowSliceDataBuffer = null;
            for (int i = 0; i < indexedShadowDrawArgsBuffers.Length; i++)
            {
                indexedShadowDrawArgsBuffers[i]?.Dispose();
                indexedShadowFallbackDrawArgsBuffers[i]?.Dispose();
                indexedShadowDrawArgsBuffers[i] = null;
                indexedShadowFallbackDrawArgsBuffers[i] = null;
                indexedShadowBuildDispatchArgsBuffers[i]?.Release();
                indexedShadowBuildDispatchArgsBuffers[i] = null;
            }
            indexedTrianglePacketCapacity = 0;
            indexedDrawClusterCapacity = 0;
            indexedShadowClusterCapacity = 0;
        }
    }
}
