using System;
using System.Collections.Generic;
using Nanite;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace RealtimeGI
{
    /// <summary>
    /// Phase 1/2 sparse Clipmap builder. Static world Bricks persist across camera scrolling;
    /// Dynamic Overlay Bricks are invalidated from changed old/new instance bounds and rebuilt
    /// from the current instance list. Receiver-only motion does not touch the world cache.
    /// </summary>
    [ExecuteAlways]
    [DefaultExecutionOrder(11000)]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(RealtimeGIScene))]
    public sealed class GIClipmapSystem : MonoBehaviour
    {
        // Increment whenever the persistent radiance representation or update addressing changes.
        // Including this in the lighting signature guarantees that stale cache contents are
        // requeued instead of surviving a shader-only implementation update.
        const int RadianceAlgorithmVersion = 17;
        public static GIClipmapSystem Active { get; private set; }
        [Header("Sources")]
        public RealtimeGIScene scene;
        public Transform focus;
        public ComputeShader clipmapBuildShader;
        public ComputeShader radianceCacheShader;

        [Header("Physical pools")]
        [Min(512)] public int staticBrickCapacity = 8192;
        [Min(128)] public int dynamicBrickCapacity = 2048;
        [Range(64, 16384)] public int maxCellsPerTriangle = 4096;
        [Tooltip("Hard cap for GPU-generated cooperative 1024-triangle voxel work items per layer. Dispatch is unfolded over XY, so capacities above the D3D dispatch-X limit remain valid.")]
        [Range(1024, 1048576)] public int maxVoxelWorkItems = 65535;
        [Range(1, 7)] public int distancePropagationPasses = 7;
        [Tooltip("Maximum static geometry Bricks rebuilt in one frame. Remaining GPU-dirty Bricks persist and are consumed fine-to-coarse on later frames.")]
        [Min(1)] public int staticGeometryBricksPerFrame = 8;
        [Tooltip("Maximum dynamic geometry Bricks rebuilt in one frame.")]
        [Min(1)] public int dynamicGeometryBricksPerFrame = 12;
        [Min(1)] public int staticRadianceBricksPerFrame = 64;
        [Min(1)] public int dynamicRadianceBricksPerFrame = 128;
        public bool enableRadianceShadows = true;
        [Min(1f)] public float radianceShadowDistance = 80f;
        [Header("Local lights and multi-bounce")]
        [Tooltip("Upload safety cap only. Per-Brick GPU Top-K selection decides which lights affect each cache region.")]
        [Range(64, 4096)] public int maxUploadedLocalLights = 2048;
        [Range(8, 64)] public int maxLocalLightsPerBrick = 32;
        [Range(0, 8)] public int maxShadowedLocalLightsPerSurface = 2;
        [Range(0, 4)] public int secondaryBounceRays = 1;
        [Range(0f, 1f)] public float secondaryBounceIntensity = 0.55f;
        [Tooltip("Exposure multiplier for physically normalized TOD sky irradiance. One is neutral; visibility is accumulated per Surface cell.")]
        [Range(0f, 2f)] public float skyIrradianceScale = 1f;
        [Tooltip("Directional-light energy available to indirect diffuse bounces. One is physically consistent with Unity light intensity.")]
        [Range(0f, 2f)] public float mainLightBounceScale = 1f;
        [Range(1, 8)] public int staticBounceSweeps = 3;
        [Tooltip("Surface Cache temporal stability. Values below 0.65 are internally clamped because sparse coloured bounce samples otherwise become voxel-sized blotches.")]
        [Range(0f, 0.95f)] public float radianceHistoryWeight = 0.65f;
        [Min(0.1f)] public float radianceClamp = 32f;
        [Range(32, 512)] public int materialTextureResolution = 128;
        public bool automaticUpdate = true;
        [Header("Scheduling")]
        [Tooltip("Record the Clipmap build as a RenderGraph compute pass on the async queue. RenderGraph inserts the fence before screen GI reads the cache.")]
        public bool enableAsyncCompute = true;

        [Header("Read-only statistics")]
        [SerializeField] int generation;
        [SerializeField] int staticBrickCount;
        [SerializeField] int dynamicBrickCount;
        [SerializeField] int droppedStaticBricks;
        [SerializeField] int droppedDynamicBricks;
        [SerializeField] int streamedVertexCount;
        [SerializeField] int streamedTriangleCount;
        [SerializeField] int invalidGeometryCount;
        [SerializeField] int invalidatedStaticBrickCount;
        [SerializeField] int invalidatedDynamicBrickCount;
        [SerializeField] uint staticVoxelWorkOverflow;
        [SerializeField] uint dynamicVoxelWorkOverflow;
        [SerializeField] int pendingStaticRadianceBricks;
        [SerializeField] int pendingDynamicRadianceBricks;
        [SerializeField] int updatedRadianceBricks;
        [SerializeField] int activeLocalLightCount;
        [SerializeField] int remainingStaticBounceSweeps;
        [SerializeField] float estimatedPoolMiB;

        readonly Vector3Int[] levelOrigins = new Vector3Int[GIClipmapConstants.LevelCount];
        readonly Vector3Int[] previousLevelOrigins = new Vector3Int[GIClipmapConstants.LevelCount];
        readonly GIGpuClipmapLevelData[] levelData = new GIGpuClipmapLevelData[GIClipmapConstants.LevelCount];
        readonly List<GILocalLight> localLightScratch = new List<GILocalLight>(64);
        readonly List<GIGpuLocalLightData> gpuLocalLights = new List<GIGpuLocalLightData>(64);

        GIGeometryStreamCache geometryCache;
        GIMaterialTextureCache materialTextureCache;
        GIClipmapLayer staticLayer;
        GIClipmapLayer dynamicLayer;
        GraphicsBuffer levelDataBuffer;
        GraphicsBuffer localLightBuffer;
        GraphicsBuffer radianceScratchBuffer;
        GraphicsBuffer validityScratchBuffer;
        GraphicsBuffer naniteFallbackPageTable;
        GraphicsBuffer naniteFallbackResidentPageTable;
        GraphicsBuffer naniteFallbackResidencyBits;
        GraphicsBuffer naniteFallbackVertices;
        GraphicsBuffer naniteFallbackIndices;
        GraphicsBuffer naniteFallbackTriangleSubMeshes;
        int clearKernel = -1;
        int clearRadianceKernel = -1;
        int resetPageAllocatorKernel = -1;
        int initializePhysicalPageAllocatorKernel = -1;
        int markRequiredPagesKernel = -1;
        int reprojectResidentPagesKernel = -1;
        int allocateRequiredPagesKernel = -1;
        int buildDirtyWorkQueuesKernel = -1;
        int buildRadianceWorkQueueKernel = -1;
        int advanceRadianceCursorKernel = -1;
        int markDirtyBricksKernel = -1;
        int resetVoxelWorkQueueKernel = -1;
        int buildVoxelWorkQueueKernel = -1;
        int clampVoxelDispatchArgsKernel = -1;
        int voxelizeWorkQueueKernel = -1;
        int initializeDistanceKernel = -1;
        int propagateDistanceKernel = -1;
        int finalizeDirtyBricksKernel = -1;
        int updateRadianceKernel = -1;
        int buildBrickLightListsKernel = -1;
        int commitRadianceKernel = -1;
        int lightingSignature;
        int lightingRevision;
        bool initialized;
        bool originsInitialized;
        int lastUpdateFrame = -1;
        int lastWarningFrame = -10000;
        int allocatedStaticCapacity;
        int allocatedDynamicCapacity;
        int localLightCapacity;
        int radianceScratchCapacity;
        int validityScratchCapacity;
        bool gpuForensicsIssued;
        GISceneGpuView preparedSceneView;
        NaniteResidentPageReadOnlyView preparedNaniteView;
        bool preparedNaniteViewValid;
        Vector3 preparedLightDirection;
        Color preparedLightColor;
        Color preparedSkyColor;
        int preparedFrame = -1;
        int preparedSequence;
        int recordedFrame = -1;
        bool preparedValid;
        uint voxelWorkGeneration = 1u;

        sealed class ClipmapRenderGraphPassData
        {
            public GIClipmapSystem owner;
            public int frame;
            public TextureHandle baseColorTextures;
            public TextureHandle emissionTextures;
            public TextureHandle normalTextures;
            public TextureHandle maskTextures;
        }

        interface IGIComputeCommands
        {
            void BeginSample(string name);
            void EndSample(string name);
            void SetComputeIntParam(ComputeShader shader, int id, int value);
            void SetComputeFloatParam(ComputeShader shader, int id, float value);
            void SetComputeVectorParam(ComputeShader shader, int id, Vector4 value);
            void SetComputeBufferParam(ComputeShader shader, int kernel, int id, GraphicsBuffer buffer);
            void SetComputeTextureParam(ComputeShader shader, int kernel, int id, Texture texture);
            void DispatchCompute(ComputeShader shader, int kernel, int x, int y, int z);
            void DispatchCompute(ComputeShader shader, int kernel, GraphicsBuffer indirectArgs, uint argsOffset);
        }

        sealed class LegacyComputeCommands : IGIComputeCommands
        {
            readonly CommandBuffer cmd;
            public LegacyComputeCommands(CommandBuffer cmd) => this.cmd = cmd;
            public void BeginSample(string name) => cmd.BeginSample(name);
            public void EndSample(string name) => cmd.EndSample(name);
            public void SetComputeIntParam(ComputeShader shader, int id, int value) =>
                cmd.SetComputeIntParam(shader, id, value);
            public void SetComputeFloatParam(ComputeShader shader, int id, float value) =>
                cmd.SetComputeFloatParam(shader, id, value);
            public void SetComputeVectorParam(ComputeShader shader, int id, Vector4 value) =>
                cmd.SetComputeVectorParam(shader, id, value);
            public void SetComputeBufferParam(
                ComputeShader shader, int kernel, int id, GraphicsBuffer buffer) =>
                cmd.SetComputeBufferParam(shader, kernel, id, buffer);
            public void SetComputeTextureParam(
                ComputeShader shader, int kernel, int id, Texture texture) =>
                cmd.SetComputeTextureParam(shader, kernel, id, texture);
            public void DispatchCompute(ComputeShader shader, int kernel, int x, int y, int z) =>
                cmd.DispatchCompute(shader, kernel, x, y, z);
            public void DispatchCompute(ComputeShader shader, int kernel, GraphicsBuffer indirectArgs, uint argsOffset) =>
                cmd.DispatchCompute(shader, kernel, indirectArgs, argsOffset);
        }

        sealed class RenderGraphComputeCommands : IGIComputeCommands
        {
            readonly ComputeCommandBuffer cmd;
            readonly GIMaterialTextureCache textures;
            readonly TextureHandle baseColor;
            readonly TextureHandle emission;
            readonly TextureHandle normal;
            readonly TextureHandle mask;

            public RenderGraphComputeCommands(
                ComputeCommandBuffer cmd,
                GIMaterialTextureCache textures,
                TextureHandle baseColor,
                TextureHandle emission,
                TextureHandle normal,
                TextureHandle mask)
            {
                this.cmd = cmd;
                this.textures = textures;
                this.baseColor = baseColor;
                this.emission = emission;
                this.normal = normal;
                this.mask = mask;
            }

            public void BeginSample(string name) => cmd.BeginSample(name);
            public void EndSample(string name) => cmd.EndSample(name);
            public void SetComputeIntParam(ComputeShader shader, int id, int value) =>
                cmd.SetComputeIntParam(shader, id, value);
            public void SetComputeFloatParam(ComputeShader shader, int id, float value) =>
                cmd.SetComputeFloatParam(shader, id, value);
            public void SetComputeVectorParam(ComputeShader shader, int id, Vector4 value) =>
                cmd.SetComputeVectorParam(shader, id, value);
            public void SetComputeBufferParam(
                ComputeShader shader, int kernel, int id, GraphicsBuffer buffer) =>
                cmd.SetComputeBufferParam(shader, kernel, id, buffer);
            public void SetComputeTextureParam(
                ComputeShader shader, int kernel, int id, Texture texture)
            {
                TextureHandle handle = ReferenceEquals(texture, textures.BaseColorArray) ? baseColor :
                    ReferenceEquals(texture, textures.EmissionArray) ? emission :
                    ReferenceEquals(texture, textures.NormalArray) ? normal : mask;
                cmd.SetComputeTextureParam(shader, kernel, id, handle);
            }
            public void DispatchCompute(ComputeShader shader, int kernel, int x, int y, int z) =>
                cmd.DispatchCompute(shader, kernel, x, y, z);
            public void DispatchCompute(ComputeShader shader, int kernel, GraphicsBuffer indirectArgs, uint argsOffset) =>
                cmd.DispatchCompute(shader, kernel, indirectArgs, argsOffset);
        }

        static readonly int DirtyBricksId = Shader.PropertyToID("_GIDirtyBricks");
        static readonly int DirtyBrickCountId = Shader.PropertyToID("_GIDirtyBrickCount");
        static readonly int OccupancyId = Shader.PropertyToID("_GIOccupancy");
        static readonly int SurfaceId = Shader.PropertyToID("_GISurface");
        static readonly int SurfaceUvId = Shader.PropertyToID("_GISurfaceUV");
        static readonly int SurfaceIdentityId = Shader.PropertyToID("_GISurfaceIdentity");
        static readonly int SurfaceStableIdId = Shader.PropertyToID("_GISurfaceStableId");
        static readonly int SurfaceKeyId = Shader.PropertyToID("_GISurfaceKey");
        static readonly int DistanceId = Shader.PropertyToID("_GIDistance");
        static readonly int InstancesId = Shader.PropertyToID("_GIInstances");
        static readonly int GeometriesId = Shader.PropertyToID("_GIGeometries");
        static readonly int MaterialBindingsId = Shader.PropertyToID("_GIMaterialBindings");
        static readonly int ClipmapMaterialsId = Shader.PropertyToID("_GIMaterials");
        static readonly int GeometryRangesId = Shader.PropertyToID("_GIGeometryRanges");
        static readonly int VerticesId = Shader.PropertyToID("_GIVertices");
        static readonly int UvsId = Shader.PropertyToID("_GIUVs");
        static readonly int IndicesId = Shader.PropertyToID("_GIIndices");
        static readonly int TriangleSubMeshesId = Shader.PropertyToID("_GITriangleSubMeshes");
        static readonly int LevelsId = Shader.PropertyToID("_GIClipmapLevels");
        static readonly int PageTableId = Shader.PropertyToID("_GIPageTable");
        static readonly int MaxCellsId = Shader.PropertyToID("_GIMaxCellsPerTriangle");
        static readonly int VoxelizePhaseId = Shader.PropertyToID("_GIVoxelizePhase");
        static readonly int RadianceId = Shader.PropertyToID("_GIRadiance");
        static readonly int ValidityId = Shader.PropertyToID("_GIValidity");
        static readonly int BrickDataId = Shader.PropertyToID("_GIBrickData");
        static readonly int RadianceDirtyBricksId = Shader.PropertyToID("_GIRadianceDirtyBricks");
        static readonly int RadianceDirtyBrickCountId = Shader.PropertyToID("_GIRadianceDirtyBrickCount");
        static readonly int PropagationStepId = Shader.PropertyToID("_GIPropagationStep");
        static readonly int MaterialsId = Shader.PropertyToID("_GIMaterials");
        static readonly int MaterialCountId = Shader.PropertyToID("_GIMaterialCount");
        static readonly int MainLightDirectionId = Shader.PropertyToID("_GIMainLightDirection");
        static readonly int MainLightColorId = Shader.PropertyToID("_GIMainLightColor");
        static readonly int TargetBrickDataId = Shader.PropertyToID("_GITargetBrickData");
        static readonly int TargetOccupancyId = Shader.PropertyToID("_GITargetOccupancy");
        static readonly int TargetSurfaceId = Shader.PropertyToID("_GITargetSurface");
        static readonly int TargetSurfaceUvId = Shader.PropertyToID("_GITargetSurfaceUV");
        static readonly int TargetSurfaceIdentityId = Shader.PropertyToID("_GITargetSurfaceIdentity");
        static readonly int TargetRadianceId = Shader.PropertyToID("_GITargetRadiance");
        static readonly int TargetValidityId = Shader.PropertyToID("_GITargetValidity");
        static readonly int StaticPageTableId = Shader.PropertyToID("_GIStaticPageTable");
        static readonly int StaticOccupancyId = Shader.PropertyToID("_GIStaticOccupancy");
        static readonly int StaticSurfaceId = Shader.PropertyToID("_GIStaticSurface");
        static readonly int StaticSurfaceIdentityId = Shader.PropertyToID("_GIStaticSurfaceIdentity");
        static readonly int StaticDistanceId = Shader.PropertyToID("_GIStaticDistance");
        static readonly int DynamicPageTableId = Shader.PropertyToID("_GIDynamicPageTable");
        static readonly int DynamicOccupancyId = Shader.PropertyToID("_GIDynamicOccupancy");
        static readonly int DynamicSurfaceId = Shader.PropertyToID("_GIDynamicSurface");
        static readonly int DynamicSurfaceIdentityId = Shader.PropertyToID("_GIDynamicSurfaceIdentity");
        static readonly int DynamicDistanceId = Shader.PropertyToID("_GIDynamicDistance");
        static readonly int RadianceShadowDistanceId = Shader.PropertyToID("_GIRadianceShadowDistance");
        static readonly int EnableRadianceShadowsId = Shader.PropertyToID("_GIEnableRadianceShadows");
        static readonly int LocalLightsId = Shader.PropertyToID("_GILocalLights");
        static readonly int LocalLightCountId = Shader.PropertyToID("_GILocalLightCount");
        static readonly int MaxLightsPerBrickId = Shader.PropertyToID("_GIMaxLightsPerBrick");
        static readonly int TargetLightCountsId = Shader.PropertyToID("_GITargetLightCounts");
        static readonly int TargetLightIndicesId = Shader.PropertyToID("_GITargetLightIndices");
        static readonly int MaxShadowedLocalLightsId = Shader.PropertyToID("_GIMaxShadowedLocalLights");
        static readonly int StaticRadianceId = Shader.PropertyToID("_GIStaticRadiance");
        static readonly int StaticValidityId = Shader.PropertyToID("_GIStaticValidity");
        static readonly int DynamicRadianceId = Shader.PropertyToID("_GIDynamicRadiance");
        static readonly int DynamicValidityId = Shader.PropertyToID("_GIDynamicValidity");
        static readonly int TargetRadianceScratchId = Shader.PropertyToID("_GITargetRadianceScratch");
        static readonly int TargetValidityScratchId = Shader.PropertyToID("_GITargetValidityScratch");
        static readonly int BaseColorTexturesId = Shader.PropertyToID("_GIBaseColorTextures");
        static readonly int EmissionTexturesId = Shader.PropertyToID("_GIEmissionTextures");
        static readonly int NormalTexturesId = Shader.PropertyToID("_GINormalTextures");
        static readonly int MaskTexturesId = Shader.PropertyToID("_GIMaskTextures");
        static readonly int TextureSliceCountId = Shader.PropertyToID("_GITextureSliceCount");
        static readonly int SecondaryBounceRaysId = Shader.PropertyToID("_GISecondaryBounceRays");
        static readonly int SecondaryBounceIntensityId = Shader.PropertyToID("_GISecondaryBounceIntensity");
        static readonly int SkyIrradianceScaleId = Shader.PropertyToID("_GISkyIrradianceScale");
        static readonly int MainLightBounceScaleId = Shader.PropertyToID("_GIMainLightBounceScale");
        static readonly int RadianceHistoryWeightId = Shader.PropertyToID("_GIRadianceHistoryWeight");
        static readonly int RadianceClampId = Shader.PropertyToID("_GIRadianceClamp");
        static readonly int RadianceFrameIndexId = Shader.PropertyToID("_GIRadianceFrameIndex");
        static readonly int TargetLayerId = Shader.PropertyToID("_GITargetLayer");
        static readonly int DirtyGenerationId = Shader.PropertyToID("_GIDirtyGeneration");
        static readonly int VoxelWorkQueueId = Shader.PropertyToID("_GIVoxelWorkQueue");
        static readonly int VoxelDispatchArgsId = Shader.PropertyToID("_GIVoxelDispatchArgs");
        static readonly int VoxelWorkQueueReadId =
            Shader.PropertyToID("_GIVoxelWorkQueueRead");
        static readonly int VoxelDispatchArgsReadId =
            Shader.PropertyToID("_GIVoxelDispatchArgsRead");
        static readonly int InstanceCountId = Shader.PropertyToID("_GIInstanceCount");
        static readonly int TargetDynamicId = Shader.PropertyToID("_GITargetDynamic");
        static readonly int VoxelWorkCapacityId = Shader.PropertyToID("_GIVoxelWorkCapacity");
        static readonly int WorkGenerationId = Shader.PropertyToID("_GIWorkGeneration");
        static readonly int RequiredPageMaskId = Shader.PropertyToID("_GIRequiredPageMask");
        static readonly int RequiredPageHashId = Shader.PropertyToID("_GIRequiredPageHash");
        static readonly int PhysicalPageHashId = Shader.PropertyToID("_GIPhysicalPageHash");
        static readonly int PhysicalRadianceGenerationId =
            Shader.PropertyToID("_GIPhysicalRadianceGeneration");
        static readonly int FreePageListId = Shader.PropertyToID("_GIFreePageList");
        static readonly int AllocatorStateId = Shader.PropertyToID("_GIAllocatorState");
        static readonly int PageDispatchArgsId = Shader.PropertyToID("_GIPageDispatchArgs");
        static readonly int PhysicalPageCapacityId = Shader.PropertyToID("_GIPhysicalPageCapacity");
        static readonly int InitializeAllocatorId = Shader.PropertyToID("_GIInitializeAllocator");
        static readonly int AllocationLevelId = Shader.PropertyToID("_GIAllocationLevel");
        static readonly int RadianceBrickBudgetId = Shader.PropertyToID("_GIRadianceBrickBudget");
        static readonly int DirtyBrickBudgetId = Shader.PropertyToID("_GIDirtyBrickBudget");
        static readonly int NanitePageTableId = Shader.PropertyToID("_GINanitePageTable");
        static readonly int NaniteResidentPageTableId =
            Shader.PropertyToID("_GINaniteResidentPageTable");
        static readonly int NaniteResidencyBitsId = Shader.PropertyToID("_GINaniteResidencyBits");
        static readonly int NaniteResidentVerticesId =
            Shader.PropertyToID("_GINaniteResidentVertices");
        static readonly int NaniteResidentIndicesId =
            Shader.PropertyToID("_GINaniteResidentIndices");
        static readonly int NaniteResidentTriangleSubMeshesId =
            Shader.PropertyToID("_GINaniteResidentTriangleSubMeshes");
        static readonly int NanitePageCountId = Shader.PropertyToID("_GINanitePageCount");
        static readonly int NanitePoolGenerationId =
            Shader.PropertyToID("_GINanitePoolGeneration");
        static readonly int NaniteGeometryReadyFlagId =
            Shader.PropertyToID("_GINaniteGeometryReadyFlag");

        public int Generation => generation;
        public int LightingRevision => lightingRevision;
        public RealtimeGIScene Scene => scene;
        public int StaticBrickCount => staticBrickCount;
        public int DynamicBrickCount => dynamicBrickCount;
        public int PendingRadianceBrickCount => pendingStaticRadianceBricks + pendingDynamicRadianceBricks;
        public float EstimatedPoolMiB => estimatedPoolMiB;

        void OnEnable()
        {
            Active = this;
        }

        void Reset()
        {
            scene = GetComponent<RealtimeGIScene>();
        }

        public bool PrepareClipmaps()
        {
            if (!isActiveAndEnabled || !SystemInfo.supportsComputeShaders ||
                SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                return false;
            if (Application.isPlaying && lastUpdateFrame == Time.frameCount)
                return preparedValid;
            lastUpdateFrame = Time.frameCount;
            preparedValid = false;

            if (!EnsureInitialized())
                return false;
            // RenderGraph work from the previous frame is complete before this update.
            // Issue the one-shot readback here so async-compute builds are audited too;
            // the legacy immediate path also calls it after its command buffer executes.
            RequestGpuForensicsOnce();
            scene.BuildNow();
            if (!scene.TryGetGpuView(out GISceneGpuView sceneView))
                return false;
            materialTextureCache.Update(scene, materialTextureResolution);

            Vector3 focusPosition = ResolveFocusPosition();
            BuildLocalLightList(focusPosition);
            bool originsChanged = UpdateLevelOrigins(focusPosition);
            // The GPU derives required pages and per-page invalidation hashes directly from
            // the scene buffers.  The compact fallback stream still contains every ordinary
            // Mesh; Nanite ranges are redirected to the live resident-page export below.
            bool geometryRebuilt = geometryCache.RebuildIfNeeded(scene);
            NaniteRendererFeature naniteFeature = NaniteRendererFeature.ActiveInstance;
            preparedNaniteViewValid = naniteFeature != null &&
                naniteFeature.TryGetResidentPageReadOnlyView(out preparedNaniteView) &&
                preparedNaniteView.IsValid;
            streamedVertexCount = geometryCache.VertexCount;
            streamedTriangleCount = geometryCache.TriangleCount;
            invalidGeometryCount = geometryCache.InvalidGeometryCount;
            invalidatedStaticBrickCount = 0;
            invalidatedDynamicBrickCount = 0;

            Light sun = RenderSettings.sun;
            Vector3 lightDirection = sun != null
                ? sun.transform.forward
                : new Vector3(0.3f, -0.8f, 0.2f).normalized;
            Color lightColorValue = sun != null ? sun.color.linear * sun.intensity : Color.white;
            // Track the analytic TOD environment that the compute shader samples.
            // Unity ambientSkyColor is intentionally not part of the GI contract.
            Color skyColorValue =
                (Shader.GetGlobalColor("_TODLightBottom") +
                 Shader.GetGlobalColor("_TODLightMiddle") +
                 Shader.GetGlobalColor("_TODLightTop") +
                 Shader.GetGlobalColor("_TODHorizonColor")) * 0.25f;
            skyColorValue *= Mathf.Max(0f, Shader.GetGlobalFloat("_TODSkyExposure"));
            int nextLightingSignature = ComputeLightingSignature(
                lightDirection, lightColorValue, skyColorValue);
            if (nextLightingSignature != lightingSignature)
            {
                lightingSignature = nextLightingSignature;
                unchecked { lightingRevision++; }
            }

            preparedSceneView = sceneView;
            preparedLightDirection = lightDirection;
            preparedLightColor = lightColorValue;
            preparedSkyColor = skyColorValue;
            unchecked { preparedFrame = ++preparedSequence; }
            preparedValid = true;
            // Runtime counts live in the allocator state buffer.  They are intentionally not
            // read back into the frame loop; optional diagnostics may sample them later.
            staticBrickCount = 0;
            dynamicBrickCount = 0;
            droppedStaticBricks = 0;
            droppedDynamicBricks = 0;
            pendingStaticRadianceBricks = 0;
            pendingDynamicRadianceBricks = 0;
            updatedRadianceBricks = 0;
            generation++;
            if (originsChanged || geometryRebuilt)
                estimatedPoolMiB = EstimatePoolMiB();
            WarnOnDegradation();
            return true;
        }

        /// <summary>Legacy/debug entry point. Runtime rendering uses RecordRenderGraph.</summary>
        public void UpdateClipmaps()
        {
            if (!PrepareClipmaps() || recordedFrame == preparedFrame)
                return;
            CommandBuffer cmd = CommandBufferPool.Get("RealtimeGI/Clipmap Update (Immediate Fallback)");
            RecordPreparedCommands(new LegacyComputeCommands(cmd));
            Graphics.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
            recordedFrame = preparedFrame;
            RequestGpuForensicsOnce();
        }

        public bool RecordRenderGraph(RenderGraph renderGraph)
        {
            if (renderGraph == null || !preparedValid || recordedFrame == preparedFrame)
                return false;

            using (var builder = renderGraph.AddComputePass<ClipmapRenderGraphPassData>(
                       "RealtimeGI/Clipmap Build", out ClipmapRenderGraphPassData passData))
            {
                passData.owner = this;
                passData.frame = preparedFrame;
                passData.baseColorTextures = renderGraph.ImportTexture(materialTextureCache.BaseColorHandle);
                passData.emissionTextures = renderGraph.ImportTexture(materialTextureCache.EmissionHandle);
                passData.normalTextures = renderGraph.ImportTexture(materialTextureCache.NormalHandle);
                passData.maskTextures = renderGraph.ImportTexture(materialTextureCache.MaskHandle);
                builder.AllowPassCulling(false);
                // Graphics.Blit refreshes imported material arrays outside RenderGraph. Keep
                // their upload frame on graphics; stable arrays can use the async queue safely.
                builder.EnableAsyncCompute(enableAsyncCompute && SystemInfo.supportsAsyncCompute &&
                                           !materialTextureCache.UpdatedThisFrame);

                builder.UseBuffer(renderGraph.ImportBuffer(preparedSceneView.instances), AccessFlags.Read);
                builder.UseBuffer(renderGraph.ImportBuffer(preparedSceneView.geometries), AccessFlags.Read);
                builder.UseBuffer(renderGraph.ImportBuffer(preparedSceneView.materials), AccessFlags.Read);
                builder.UseBuffer(renderGraph.ImportBuffer(preparedSceneView.materialBindings), AccessFlags.Read);
                builder.UseBuffer(renderGraph.ImportBuffer(geometryCache.RangeBuffer), AccessFlags.Read);
                builder.UseBuffer(renderGraph.ImportBuffer(geometryCache.VertexBuffer), AccessFlags.Read);
                builder.UseBuffer(renderGraph.ImportBuffer(geometryCache.UvBuffer), AccessFlags.Read);
                builder.UseBuffer(renderGraph.ImportBuffer(geometryCache.IndexBuffer), AccessFlags.Read);
                builder.UseBuffer(renderGraph.ImportBuffer(geometryCache.TriangleSubMeshBuffer), AccessFlags.Read);
                builder.UseBuffer(renderGraph.ImportBuffer(preparedNaniteViewValid
                    ? preparedNaniteView.pageTable : naniteFallbackPageTable), AccessFlags.Read);
                builder.UseBuffer(renderGraph.ImportBuffer(preparedNaniteViewValid
                    ? preparedNaniteView.residentPageTable : naniteFallbackResidentPageTable), AccessFlags.Read);
                builder.UseBuffer(renderGraph.ImportBuffer(preparedNaniteViewValid
                    ? preparedNaniteView.residencyBits : naniteFallbackResidencyBits), AccessFlags.Read);
                builder.UseBuffer(renderGraph.ImportBuffer(preparedNaniteViewValid
                    ? preparedNaniteView.residentVertices : naniteFallbackVertices), AccessFlags.Read);
                builder.UseBuffer(renderGraph.ImportBuffer(preparedNaniteViewValid
                    ? preparedNaniteView.residentIndices : naniteFallbackIndices), AccessFlags.Read);
                builder.UseBuffer(renderGraph.ImportBuffer(preparedNaniteViewValid
                    ? preparedNaniteView.residentTriangleSubMeshes
                    : naniteFallbackTriangleSubMeshes), AccessFlags.Read);
                builder.UseBuffer(renderGraph.ImportBuffer(levelDataBuffer), AccessFlags.Read);
                builder.UseBuffer(renderGraph.ImportBuffer(localLightBuffer), AccessFlags.Read);
                builder.UseBuffer(renderGraph.ImportBuffer(radianceScratchBuffer), AccessFlags.ReadWrite);
                builder.UseBuffer(renderGraph.ImportBuffer(validityScratchBuffer), AccessFlags.ReadWrite);
                DeclareLayerResources(renderGraph, builder, staticLayer);
                DeclareLayerResources(renderGraph, builder, dynamicLayer);
                builder.UseTexture(passData.baseColorTextures, AccessFlags.Read);
                builder.UseTexture(passData.emissionTextures, AccessFlags.Read);
                builder.UseTexture(passData.normalTextures, AccessFlags.Read);
                builder.UseTexture(passData.maskTextures, AccessFlags.Read);
                builder.SetRenderFunc(static (ClipmapRenderGraphPassData data, ComputeGraphContext context) =>
                {
                    if (!data.owner.preparedValid || data.owner.preparedFrame != data.frame)
                        return;
                    data.owner.RecordPreparedCommands(new RenderGraphComputeCommands(
                        context.cmd,
                        data.owner.materialTextureCache,
                        data.baseColorTextures,
                        data.emissionTextures,
                        data.normalTextures,
                        data.maskTextures));
                });
            }
            recordedFrame = preparedFrame;
            return true;
        }

        static void DeclareLayerResources(
            RenderGraph renderGraph,
            IComputeRenderGraphBuilder builder,
            GIClipmapLayer layer)
        {
            builder.UseBuffer(renderGraph.ImportBuffer(layer.DirtyBrickBuffer), AccessFlags.ReadWrite);
            builder.UseBuffer(renderGraph.ImportBuffer(layer.RadianceDirtyBuffer), AccessFlags.ReadWrite);
            builder.UseBuffer(renderGraph.ImportBuffer(layer.BrickDataBuffer), AccessFlags.ReadWrite);
            builder.UseBuffer(renderGraph.ImportBuffer(layer.PageTableBuffer), AccessFlags.ReadWrite);
            builder.UseBuffer(renderGraph.ImportBuffer(layer.OccupancyBuffer), AccessFlags.ReadWrite);
            builder.UseBuffer(renderGraph.ImportBuffer(layer.SurfaceBuffer), AccessFlags.ReadWrite);
            builder.UseBuffer(renderGraph.ImportBuffer(layer.SurfaceUvBuffer), AccessFlags.ReadWrite);
            builder.UseBuffer(renderGraph.ImportBuffer(layer.SurfaceIdentityBuffer), AccessFlags.ReadWrite);
            builder.UseBuffer(renderGraph.ImportBuffer(layer.SurfaceStableIdBuffer), AccessFlags.ReadWrite);
            builder.UseBuffer(renderGraph.ImportBuffer(layer.SurfaceKeyBuffer), AccessFlags.ReadWrite);
            builder.UseBuffer(renderGraph.ImportBuffer(layer.DistanceBuffer), AccessFlags.ReadWrite);
            builder.UseBuffer(renderGraph.ImportBuffer(layer.RadianceBuffer), AccessFlags.ReadWrite);
            builder.UseBuffer(renderGraph.ImportBuffer(layer.ValidityBuffer), AccessFlags.ReadWrite);
            builder.UseBuffer(renderGraph.ImportBuffer(layer.LightCountBuffer), AccessFlags.ReadWrite);
            builder.UseBuffer(renderGraph.ImportBuffer(layer.LightIndexBuffer), AccessFlags.ReadWrite);
            builder.UseBuffer(renderGraph.ImportBuffer(layer.DirtyGenerationBuffer), AccessFlags.ReadWrite);
            builder.UseBuffer(renderGraph.ImportBuffer(layer.VoxelWorkQueueBuffer), AccessFlags.ReadWrite);
            builder.UseBuffer(renderGraph.ImportBuffer(layer.VoxelDispatchArgsBuffer), AccessFlags.ReadWrite);
            builder.UseBuffer(renderGraph.ImportBuffer(layer.RequiredPageMaskBuffer), AccessFlags.ReadWrite);
            builder.UseBuffer(renderGraph.ImportBuffer(layer.RequiredPageHashBuffer), AccessFlags.ReadWrite);
            builder.UseBuffer(renderGraph.ImportBuffer(layer.PhysicalPageHashBuffer), AccessFlags.ReadWrite);
            builder.UseBuffer(renderGraph.ImportBuffer(layer.PhysicalRadianceGenerationBuffer), AccessFlags.ReadWrite);
            builder.UseBuffer(renderGraph.ImportBuffer(layer.FreePageListBuffer), AccessFlags.ReadWrite);
            builder.UseBuffer(renderGraph.ImportBuffer(layer.AllocatorStateBuffer), AccessFlags.ReadWrite);
            builder.UseBuffer(renderGraph.ImportBuffer(layer.PageDispatchArgsBuffer), AccessFlags.ReadWrite);
        }

        void RecordPreparedCommands(IGIComputeCommands cmd)
        {
            cmd.BeginSample("RealtimeGI/Clipmap Total");
            uint workGeneration = ++voxelWorkGeneration;
            if (workGeneration == 0u)
                workGeneration = voxelWorkGeneration = 1u;
            cmd.BeginSample("RealtimeGI/GPU Page Allocation");
            AllocateClipmapPagesGpu(cmd, preparedSceneView, staticLayer, false,
                staticGeometryBricksPerFrame, staticRadianceBricksPerFrame, workGeneration);
            AllocateClipmapPagesGpu(cmd, preparedSceneView, dynamicLayer, true,
                dynamicGeometryBricksPerFrame, dynamicRadianceBricksPerFrame, workGeneration);
            cmd.EndSample("RealtimeGI/GPU Page Allocation");
            cmd.BeginSample("RealtimeGI/Clipmap Clear");
            ClearDirtyBricks(cmd, staticLayer);
            ClearDirtyBricks(cmd, dynamicLayer);
            cmd.EndSample("RealtimeGI/Clipmap Clear");
            cmd.BeginSample("RealtimeGI/Voxelize Static");
            VoxelizeLayer(cmd, preparedSceneView, staticLayer, false);
            cmd.EndSample("RealtimeGI/Voxelize Static");
            cmd.BeginSample("RealtimeGI/Voxelize Dynamic");
            VoxelizeLayer(cmd, preparedSceneView, dynamicLayer, true);
            cmd.EndSample("RealtimeGI/Voxelize Dynamic");
            cmd.BeginSample("RealtimeGI/Distance Propagation");
            BuildDistance(cmd, staticLayer);
            BuildDistance(cmd, dynamicLayer);
            FinalizeDirtyBricks(cmd, staticLayer);
            FinalizeDirtyBricks(cmd, dynamicLayer);
            cmd.EndSample("RealtimeGI/Distance Propagation");
            cmd.BeginSample("RealtimeGI/Radiance Update");
            UpdateRadiance(cmd, preparedSceneView, staticLayer,
                preparedLightDirection, preparedLightColor, preparedSkyColor);
            UpdateRadiance(cmd, preparedSceneView, dynamicLayer,
                preparedLightDirection, preparedLightColor, preparedSkyColor);
            cmd.EndSample("RealtimeGI/Radiance Update");
            cmd.EndSample("RealtimeGI/Clipmap Total");
        }

        // This is deliberately a one-shot, raw-buffer audit. It avoids every screen-space
        // shader and reports whether the cache builder actually wrote the same physical cells
        // that the ray tracer can hit.
        void RequestGpuForensicsOnce()
        {
            if (gpuForensicsIssued || !Application.isPlaying || Time.frameCount < 30)
                return;
            gpuForensicsIssued = true;
            LogRadianceSourceForensics();
            RequestVoxelQueueForensics("Static", staticLayer, true);
            RequestVoxelQueueForensics("Dynamic", dynamicLayer, false);
            RequestLayerForensics("Static", staticLayer);
            RequestLayerForensics("Dynamic", dynamicLayer);
        }

        void RequestVoxelQueueForensics(string layerName, GIClipmapLayer layer, bool isStatic)
        {
            if (layer?.VoxelDispatchArgsBuffer == null)
                return;
            AsyncGPUReadback.Request(layer.VoxelDispatchArgsBuffer, request =>
            {
                if (this == null || request.hasError)
                    return;
                var args = request.GetData<uint>();
                uint overflow = args.Length > 3 ? args[3] : 0u;
                if (isStatic)
                    staticVoxelWorkOverflow = overflow;
                else
                    dynamicVoxelWorkOverflow = overflow;
                if (overflow > 0u)
                    Debug.LogError($"[RealtimeGI][VoxelQueue] {layerName} dropped {overflow} " +
                                   $"1024-triangle work items; raise Max Voxel Work Items or " +
                                   "reduce simultaneous dirty geometry.");
            });
        }

        void LogRadianceSourceForensics()
        {
            int emissiveMaterialCount = 0;
            float maximumEmissive = 0f;
            IReadOnlyList<GIGpuMaterialData> materials = scene != null ? scene.CpuMaterials : null;
            if (materials != null)
            {
                for (int i = 0; i < materials.Count; i++)
                {
                    Vector4 emissive = materials[i].emissive;
                    float intensity = Mathf.Max(emissive.x, Mathf.Max(emissive.y, emissive.z));
                    if (intensity <= 1e-5f)
                        continue;
                    emissiveMaterialCount++;
                    maximumEmissive = Mathf.Max(maximumEmissive, intensity);
                }
            }
            Light sun = RenderSettings.sun;
            string sunInfo = sun == null
                ? "none"
                : $"{sun.name}, intensity={sun.intensity:F3}, color={sun.color.linear}";
            Debug.Log($"[RealtimeGI][Forensics] Sources: materials={materials?.Count ?? 0}, " +
                      $"emissiveMaterials={emissiveMaterialCount}, maxEmissive={maximumEmissive:F3}, " +
                      $"sun={sunInfo}, localLights={activeLocalLightCount}, " +
                      $"radianceShadows={enableRadianceShadows}.");
        }

        static void RequestLayerForensics(string layerName, GIClipmapLayer layer)
        {
            if (layer == null)
            {
                Debug.Log($"[RealtimeGI][Forensics] {layerName}: layer unavailable.");
                return;
            }

            AsyncGPUReadback.Request(layer.ValidityBuffer, validityRequest =>
            {
                if (validityRequest.hasError)
                {
                    Debug.LogError($"[RealtimeGI][Forensics] {layerName}: validity readback failed.");
                    return;
                }

                var validity = validityRequest.GetData<uint>();
                int validCellCount = 0;
                int firstValidCell = -1;
                uint maximumConfidence = 0u;
                for (int i = 0; i < validity.Length; i++)
                {
                    uint confidence = validity[i] & 0xffu;
                    if (confidence == 0u)
                        continue;
                    validCellCount++;
                    maximumConfidence = Math.Max(maximumConfidence, confidence);
                    if (firstValidCell < 0)
                        firstValidCell = i;
                }
                if (firstValidCell < 0)
                {
                    Debug.LogError($"[RealtimeGI][Forensics] {layerName}: validity has 0 valid cells " +
                                   $"across {validity.Length} cells.");
                    return;
                }

                int physicalBrick = firstValidCell / GIClipmapConstants.CellsPerBrick;
                int localCell = firstValidCell % GIClipmapConstants.CellsPerBrick;
                uint rawValidity = validity[firstValidCell];
                int occupancyOffset = (physicalBrick * GIClipmapConstants.OccupancyWordsPerBrick +
                                       localCell / 32) * 4;
                int radianceOffset = (physicalBrick * GIClipmapConstants.RadianceWordsPerBrick) * 4;
                AsyncGPUReadback.Request(layer.OccupancyBuffer, 4, occupancyOffset, occupancyRequest =>
                {
                    if (occupancyRequest.hasError)
                    {
                        Debug.LogError($"[RealtimeGI][Forensics] {layerName}: occupancy readback failed.");
                        return;
                    }
                    uint occupancyWord = occupancyRequest.GetData<uint>()[0];
                    bool occupied = (occupancyWord & (1u << (localCell & 31))) != 0u;
                    AsyncGPUReadback.Request(layer.RadianceBuffer,
                        GIClipmapConstants.RadianceWordsPerBrick * 4, radianceOffset, radianceRequest =>
                    {
                        if (radianceRequest.hasError)
                        {
                            Debug.LogError($"[RealtimeGI][Forensics] {layerName}: radiance readback failed.");
                            return;
                        }
                        var radiance = radianceRequest.GetData<uint>();
                        int firstLobe = localCell * GIClipmapConstants.RadianceLobeCount;
                        int nonZeroLobes = 0;
                        for (int lobe = 0; lobe < GIClipmapConstants.RadianceLobeCount; lobe++)
                            nonZeroLobes += radiance[firstLobe + lobe] != 0u ? 1 : 0;
                        Debug.Log(
                            $"[RealtimeGI][Forensics] {layerName}: validCells={validCellCount}, " +
                            $"maxConfidence={maximumConfidence}, brick={physicalBrick}, cell={localCell}, " +
                            $"occupied={occupied}, validity=0x{rawValidity:X8}, " +
                            $"nonZeroLobes={nonZeroLobes}/6.");
                    });
                });
            });
        }

        bool EnsureInitialized()
        {
            if (initialized && (allocatedStaticCapacity != staticBrickCapacity ||
                                allocatedDynamicCapacity != dynamicBrickCapacity))
                ReleaseResources();
            if (initialized)
                return true;
            if (scene == null)
                scene = GetComponent<RealtimeGIScene>();
            if (scene == null)
                return false;
            if (clipmapBuildShader == null)
                clipmapBuildShader = Resources.Load<ComputeShader>("RealtimeGI/GIClipmapBuild");
            if (radianceCacheShader == null)
                radianceCacheShader = Resources.Load<ComputeShader>("RealtimeGI/GIRadianceCache");
            if (clipmapBuildShader == null)
            {
                Debug.LogError("[RealtimeGI] Missing Resources/RealtimeGI/GIClipmapBuild.compute.", this);
                return false;
            }
            if (radianceCacheShader == null)
            {
                Debug.LogError("[RealtimeGI] Missing Resources/RealtimeGI/GIRadianceCache.compute.", this);
                return false;
            }

            try
            {
                GIClipmapConstants.Validate();
                clearKernel = clipmapBuildShader.FindKernel("ClearBricks");
                clearRadianceKernel = clipmapBuildShader.FindKernel("ClearBrickRadiance");
                resetPageAllocatorKernel = clipmapBuildShader.FindKernel("ResetPageAllocatorFrame");
                initializePhysicalPageAllocatorKernel =
                    clipmapBuildShader.FindKernel("InitializePhysicalPageAllocator");
                markRequiredPagesKernel = clipmapBuildShader.FindKernel("MarkRequiredPages");
                reprojectResidentPagesKernel = clipmapBuildShader.FindKernel("ReprojectResidentPages");
                allocateRequiredPagesKernel = clipmapBuildShader.FindKernel("AllocateRequiredPages");
                buildDirtyWorkQueuesKernel = clipmapBuildShader.FindKernel("BuildDirtyWorkQueues");
                buildRadianceWorkQueueKernel = clipmapBuildShader.FindKernel("BuildRadianceWorkQueue");
                advanceRadianceCursorKernel = clipmapBuildShader.FindKernel("AdvanceRadianceCursor");
                markDirtyBricksKernel = clipmapBuildShader.FindKernel("MarkDirtyBricks");
                resetVoxelWorkQueueKernel = clipmapBuildShader.FindKernel("ResetVoxelWorkQueue");
                buildVoxelWorkQueueKernel = clipmapBuildShader.FindKernel("BuildVoxelWorkQueue");
                clampVoxelDispatchArgsKernel = clipmapBuildShader.FindKernel("ClampVoxelDispatchArgs");
                voxelizeWorkQueueKernel = clipmapBuildShader.FindKernel("VoxelizeWorkQueue");
                initializeDistanceKernel = clipmapBuildShader.FindKernel("InitializeDistance");
                propagateDistanceKernel = clipmapBuildShader.FindKernel("PropagateDistance");
                finalizeDirtyBricksKernel = clipmapBuildShader.FindKernel("FinalizeDirtyBricks");
                updateRadianceKernel = radianceCacheShader.FindKernel("UpdateSurfaceRadiance");
                buildBrickLightListsKernel = radianceCacheShader.FindKernel("BuildBrickLightLists");
                commitRadianceKernel = radianceCacheShader.FindKernel("CommitSurfaceRadiance");
                geometryCache = new GIGeometryStreamCache();
                materialTextureCache = new GIMaterialTextureCache();
                staticLayer = new GIClipmapLayer(staticBrickCapacity, "GI Static");
                dynamicLayer = new GIClipmapLayer(dynamicBrickCapacity, "GI Dynamic");
                staticLayer.EnsureVoxelWorkQueue(maxVoxelWorkItems, "GI Static");
                dynamicLayer.EnsureVoxelWorkQueue(maxVoxelWorkItems, "GI Dynamic");
                allocatedStaticCapacity = staticBrickCapacity;
                allocatedDynamicCapacity = dynamicBrickCapacity;
                levelDataBuffer = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured,
                    GIClipmapConstants.LevelCount,
                    GIClipmapConstants.LevelStride) { name = "GI Clipmap Levels" };
                EnsureAuxiliaryBuffers();
                initialized = true;
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogError($"[RealtimeGI] Clipmap initialization failed: {exception}", this);
                ReleaseResources();
                return false;
            }
        }

        void EnsureAuxiliaryBuffers()
        {
            staticLayer?.EnsureLightLists(maxLocalLightsPerBrick, "GI Static");
            dynamicLayer?.EnsureLightLists(maxLocalLightsPerBrick, "GI Dynamic");
            staticLayer?.EnsureVoxelWorkQueue(maxVoxelWorkItems, "GI Static");
            dynamicLayer?.EnsureVoxelWorkQueue(maxVoxelWorkItems, "GI Dynamic");
            if (naniteFallbackPageTable == null)
            {
                naniteFallbackPageTable = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured, 1, NaniteGpuPagePool.PageTableEntryBytes)
                    { name = "GI Null Nanite Page Table" };
                naniteFallbackResidentPageTable = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured, 1, NaniteGpuPagePool.ResidentPageEntryBytes)
                    { name = "GI Null Nanite Resident Table" };
                naniteFallbackResidencyBits = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured, 1, 4)
                    { name = "GI Null Nanite Residency" };
                naniteFallbackVertices = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured, 1, NaniteGpuPagePool.ResidentVertexBytes)
                    { name = "GI Null Nanite Vertices" };
                naniteFallbackIndices = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured, 1, 4)
                    { name = "GI Null Nanite Indices" };
                naniteFallbackTriangleSubMeshes = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured, 1, 4)
                    { name = "GI Null Nanite Triangle SubMeshes" };
            }
            int scratchBricks = Mathf.Max(staticBrickCapacity, dynamicBrickCapacity);
            int requiredScratch = scratchBricks * GIClipmapConstants.RadianceWordsPerBrick;
            if (radianceScratchBuffer == null || radianceScratchCapacity < requiredScratch)
            {
                radianceScratchBuffer?.Release();
                radianceScratchCapacity = requiredScratch;
                radianceScratchBuffer = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured, Mathf.Max(1, requiredScratch), 4)
                {
                    name = "GI Radiance Scratch"
                };
            }
            int requiredValidityScratch = scratchBricks * GIClipmapConstants.ValidityWordsPerBrick;
            if (validityScratchBuffer == null || validityScratchCapacity < requiredValidityScratch)
            {
                validityScratchBuffer?.Release();
                validityScratchCapacity = requiredValidityScratch;
                validityScratchBuffer = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured, Mathf.Max(1, requiredValidityScratch), 4)
                {
                    name = "GI Validity Scratch"
                };
            }
            int requiredLights = Mathf.NextPowerOfTwo(Mathf.Max(1, maxUploadedLocalLights));
            if (localLightBuffer == null || localLightCapacity < requiredLights)
            {
                localLightBuffer?.Release();
                localLightCapacity = requiredLights;
                localLightBuffer = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured, requiredLights, GILightAbi.LocalLightStride)
                {
                    name = "GI Local Lights"
                };
            }
        }

        Vector3 ResolveFocusPosition()
        {
            if (focus != null)
                return focus.position;
            Camera camera = Camera.main;
            return camera != null ? camera.transform.position : transform.position;
        }

        void BuildLocalLightList(Vector3 focusPosition)
        {
            EnsureAuxiliaryBuffers();
            localLightScratch.Clear();
            IReadOnlyList<GILocalLight> registered = GILocalLightRegistry.Lights;
            for (int i = 0; i < registered.Count; i++)
            {
                GILocalLight candidate = registered[i];
                if (candidate != null && candidate.IsSupported)
                    localLightScratch.Add(candidate);
            }
            gpuLocalLights.Clear();
            // CPU only discovers components and uploads the stable ABI. Off-screen lights
            // must remain available to world-cache rays; GPU Brick lists perform selection.
            int count = Mathf.Min(Mathf.Max(0, maxUploadedLocalLights), localLightScratch.Count);
            for (int i = 0; i < count; i++)
            {
                GILocalLight adapter = localLightScratch[i];
                Light light = adapter.Source;
                Color color = light.color.linear * light.intensity;
                bool spot = light.type == LightType.Spot;
                float outerCos = spot ? Mathf.Cos(light.spotAngle * 0.5f * Mathf.Deg2Rad) : -1f;
                float innerCos = spot ? Mathf.Cos(light.innerSpotAngle * 0.5f * Mathf.Deg2Rad) : -1f;
                Vector3 direction = light.transform.forward.normalized;
                gpuLocalLights.Add(new GIGpuLocalLightData
                {
                    positionRange = new Vector4(
                        light.transform.position.x, light.transform.position.y,
                        light.transform.position.z, light.range),
                    colorIntensity = new Vector4(color.r, color.g, color.b, 0f),
                    directionOuterCos = new Vector4(direction.x, direction.y, direction.z, outerCos),
                    parameters = new Vector4(
                        spot ? 1f : 0f, innerCos, adapter.castGIShadows ? 1f : 0f,
                        adapter.indirectMultiplier)
                });
            }
            activeLocalLightCount = gpuLocalLights.Count;
            if (gpuLocalLights.Count > 0)
                localLightBuffer.SetData(gpuLocalLights, 0, 0, gpuLocalLights.Count);
        }

        bool UpdateLevelOrigins(Vector3 focusPosition)
        {
            bool changed = !originsInitialized;
            for (int level = 0; level < GIClipmapConstants.LevelCount; level++)
            {
                float cellSize = GIClipmapConstants.CellSizes[level];
                float brickWorldSize = cellSize * GIClipmapConstants.BrickSize;
                Vector3Int centerBrick = new Vector3Int(
                    Mathf.FloorToInt(focusPosition.x / brickWorldSize),
                    Mathf.FloorToInt(focusPosition.y / brickWorldSize),
                    Mathf.FloorToInt(focusPosition.z / brickWorldSize));
                Vector3Int origin = centerBrick - Vector3Int.one * (GIClipmapConstants.BricksPerAxis / 2);
                previousLevelOrigins[level] = levelOrigins[level];
                levelOrigins[level] = origin;
                changed |= originsInitialized && origin != previousLevelOrigins[level];
                levelData[level] = new GIGpuClipmapLevelData
                {
                    worldOrigin = (Vector3)origin * brickWorldSize,
                    cellSize = cellSize,
                    originBrick = origin,
                    pageTableOffset = (uint)(level * GIClipmapConstants.BricksPerLevel)
                };
            }
            originsInitialized = true;
            levelDataBuffer.SetData(levelData);
            return changed;
        }

        void AllocateClipmapPagesGpu(
            IGIComputeCommands cmd,
            GISceneGpuView view,
            GIClipmapLayer layer,
            bool dynamic,
            int dirtyBudget,
            int radianceBudget,
            uint workGeneration)
        {
            int[] kernels =
            {
                resetPageAllocatorKernel,
                initializePhysicalPageAllocatorKernel,
                markRequiredPagesKernel,
                reprojectResidentPagesKernel,
                allocateRequiredPagesKernel,
                buildDirtyWorkQueuesKernel,
                buildRadianceWorkQueueKernel,
                advanceRadianceCursorKernel
            };
            foreach (int kernel in kernels)
            {
                cmd.SetComputeBufferParam(clipmapBuildShader, kernel, InstancesId, view.instances);
                cmd.SetComputeBufferParam(clipmapBuildShader, kernel, GeometriesId, view.geometries);
                cmd.SetComputeBufferParam(clipmapBuildShader, kernel, MaterialBindingsId,
                    view.materialBindings);
                cmd.SetComputeBufferParam(clipmapBuildShader, kernel, ClipmapMaterialsId,
                    view.materials);
                cmd.SetComputeBufferParam(clipmapBuildShader, kernel, GeometryRangesId,
                    geometryCache.RangeBuffer);
                cmd.SetComputeBufferParam(clipmapBuildShader, kernel, LevelsId, levelDataBuffer);
                cmd.SetComputeBufferParam(clipmapBuildShader, kernel, PageTableId,
                    layer.PageTableBuffer);
                cmd.SetComputeBufferParam(clipmapBuildShader, kernel, BrickDataId,
                    layer.BrickDataBuffer);
                cmd.SetComputeBufferParam(clipmapBuildShader, kernel, RequiredPageMaskId,
                    layer.RequiredPageMaskBuffer);
                cmd.SetComputeBufferParam(clipmapBuildShader, kernel, RequiredPageHashId,
                    layer.RequiredPageHashBuffer);
                cmd.SetComputeBufferParam(clipmapBuildShader, kernel, PhysicalPageHashId,
                    layer.PhysicalPageHashBuffer);
                cmd.SetComputeBufferParam(clipmapBuildShader, kernel,
                    PhysicalRadianceGenerationId,
                    layer.PhysicalRadianceGenerationBuffer);
                cmd.SetComputeBufferParam(clipmapBuildShader, kernel, FreePageListId,
                    layer.FreePageListBuffer);
                cmd.SetComputeBufferParam(clipmapBuildShader, kernel, AllocatorStateId,
                    layer.AllocatorStateBuffer);
                cmd.SetComputeBufferParam(clipmapBuildShader, kernel, PageDispatchArgsId,
                    layer.PageDispatchArgsBuffer);
                cmd.SetComputeBufferParam(clipmapBuildShader, kernel, DirtyBricksId,
                    layer.DirtyBrickBuffer);
                cmd.SetComputeBufferParam(clipmapBuildShader, kernel, RadianceDirtyBricksId,
                    layer.RadianceDirtyBuffer);
                cmd.SetComputeBufferParam(clipmapBuildShader, kernel, DirtyGenerationId,
                    layer.DirtyGenerationBuffer);
            }

            cmd.SetComputeIntParam(clipmapBuildShader, InstanceCountId, view.instanceCount);
            cmd.SetComputeIntParam(clipmapBuildShader, MaterialCountId, view.materialCount);
            cmd.SetComputeIntParam(clipmapBuildShader, TargetDynamicId, dynamic ? 1 : 0);
            cmd.SetComputeIntParam(clipmapBuildShader, PhysicalPageCapacityId, layer.Capacity);
            cmd.SetComputeIntParam(clipmapBuildShader, InitializeAllocatorId,
                layer.GpuAllocatorInitialized ? 0 : 1);
            cmd.SetComputeIntParam(clipmapBuildShader, WorkGenerationId,
                unchecked((int)workGeneration));
            cmd.SetComputeIntParam(clipmapBuildShader, RadianceBrickBudgetId,
                Mathf.Clamp(radianceBudget, 1, layer.Capacity));
            cmd.SetComputeIntParam(clipmapBuildShader, DirtyBrickBudgetId,
                Mathf.Clamp(dirtyBudget, 1, layer.Capacity));

            int resetCount = Mathf.Max(GIClipmapConstants.PageTableEntries, layer.Capacity);
            cmd.DispatchCompute(clipmapBuildShader, resetPageAllocatorKernel,
                Mathf.CeilToInt(resetCount / 64f), 1, 1);
            cmd.DispatchCompute(clipmapBuildShader, initializePhysicalPageAllocatorKernel,
                Mathf.CeilToInt(layer.Capacity / 64f), 1, 1);
            cmd.DispatchCompute(clipmapBuildShader, markRequiredPagesKernel,
                Mathf.Max(1, Mathf.CeilToInt(view.instanceCount / 64f)), 1, 1);
            cmd.DispatchCompute(clipmapBuildShader, reprojectResidentPagesKernel,
                Mathf.CeilToInt(layer.Capacity / 64f), 1, 1);
            // Level ordering is a deterministic priority rule under pool pressure: finer
            // pages always get a chance to allocate before their coarse fallbacks.
            for (int level = 0; level < GIClipmapConstants.LevelCount; level++)
            {
                cmd.SetComputeIntParam(clipmapBuildShader, AllocationLevelId, level);
                cmd.DispatchCompute(clipmapBuildShader, allocateRequiredPagesKernel,
                    Mathf.CeilToInt(GIClipmapConstants.BricksPerLevel / 64f), 1, 1);
            }
            // The bounded persistent queue is filled fine-to-coarse. This keeps newly visible
            // near geometry useful while preventing a camera scroll from rebuilding every
            // newly exposed page in one catastrophic frame.
            for (int level = 0; level < GIClipmapConstants.LevelCount; level++)
            {
                cmd.SetComputeIntParam(clipmapBuildShader, AllocationLevelId, level);
                cmd.DispatchCompute(clipmapBuildShader, buildDirtyWorkQueuesKernel,
                    Mathf.CeilToInt(layer.Capacity / 64f), 1, 1);
            }
            cmd.DispatchCompute(clipmapBuildShader, buildRadianceWorkQueueKernel,
                Mathf.CeilToInt(layer.Capacity / 64f), 1, 1);
            cmd.DispatchCompute(clipmapBuildShader, advanceRadianceCursorKernel, 1, 1, 1);
            layer.MarkGpuAllocatorRecorded();
        }

        void ClearDirtyBricks(IGIComputeCommands cmd, GIClipmapLayer layer)
        {
            cmd.SetComputeIntParam(clipmapBuildShader, DirtyBrickCountId, layer.Capacity);
            cmd.SetComputeBufferParam(clipmapBuildShader, clearKernel, DirtyBricksId, layer.DirtyBrickBuffer);
            cmd.SetComputeBufferParam(clipmapBuildShader, clearKernel, OccupancyId, layer.OccupancyBuffer);
            cmd.SetComputeBufferParam(clipmapBuildShader, clearKernel, SurfaceId, layer.SurfaceBuffer);
            cmd.SetComputeBufferParam(clipmapBuildShader, clearKernel, SurfaceUvId, layer.SurfaceUvBuffer);
            cmd.SetComputeBufferParam(clipmapBuildShader, clearKernel, SurfaceIdentityId, layer.SurfaceIdentityBuffer);
            cmd.SetComputeBufferParam(clipmapBuildShader, clearKernel, SurfaceStableIdId, layer.SurfaceStableIdBuffer);
            cmd.SetComputeBufferParam(clipmapBuildShader, clearKernel, SurfaceKeyId, layer.SurfaceKeyBuffer);
            cmd.SetComputeBufferParam(clipmapBuildShader, clearKernel, DistanceId, layer.DistanceBuffer);
            cmd.DispatchCompute(clipmapBuildShader, clearKernel,
                layer.PageDispatchArgsBuffer, 0u);
            // Stable IDs pushed the monolithic clear beyond D3D11's eight-UAV limit.
            // Lighting payload is independent, so clear it in a second bounded dispatch.
            cmd.SetComputeIntParam(clipmapBuildShader, DirtyBrickCountId, layer.Capacity);
            cmd.SetComputeBufferParam(clipmapBuildShader, clearRadianceKernel,
                DirtyBricksId, layer.DirtyBrickBuffer);
            cmd.SetComputeBufferParam(clipmapBuildShader, clearRadianceKernel,
                RadianceId, layer.RadianceBuffer);
            cmd.SetComputeBufferParam(clipmapBuildShader, clearRadianceKernel,
                ValidityId, layer.ValidityBuffer);
            cmd.DispatchCompute(clipmapBuildShader, clearRadianceKernel,
                layer.PageDispatchArgsBuffer, 0u);
        }

        void VoxelizeLayer(IGIComputeCommands cmd, GISceneGpuView view, GIClipmapLayer layer, bool dynamic)
        {
            uint workGeneration = voxelWorkGeneration;
            cmd.SetComputeIntParam(clipmapBuildShader, MaxCellsId, maxCellsPerTriangle);
            int[] kernels = { markDirtyBricksKernel, resetVoxelWorkQueueKernel,
                buildVoxelWorkQueueKernel, clampVoxelDispatchArgsKernel, voxelizeWorkQueueKernel };
            foreach (int kernel in kernels)
            {
                cmd.SetComputeBufferParam(clipmapBuildShader, kernel, InstancesId, view.instances);
                cmd.SetComputeBufferParam(clipmapBuildShader, kernel, GeometriesId, view.geometries);
                cmd.SetComputeBufferParam(clipmapBuildShader, kernel, MaterialBindingsId, view.materialBindings);
                cmd.SetComputeBufferParam(clipmapBuildShader, kernel, ClipmapMaterialsId, view.materials);
                cmd.SetComputeBufferParam(clipmapBuildShader, kernel, GeometryRangesId, geometryCache.RangeBuffer);
                cmd.SetComputeBufferParam(clipmapBuildShader, kernel, VerticesId, geometryCache.VertexBuffer);
                cmd.SetComputeBufferParam(clipmapBuildShader, kernel, UvsId, geometryCache.UvBuffer);
                cmd.SetComputeBufferParam(clipmapBuildShader, kernel, IndicesId, geometryCache.IndexBuffer);
                cmd.SetComputeBufferParam(clipmapBuildShader, kernel, TriangleSubMeshesId, geometryCache.TriangleSubMeshBuffer);
                cmd.SetComputeBufferParam(clipmapBuildShader, kernel, LevelsId, levelDataBuffer);
                cmd.SetComputeBufferParam(clipmapBuildShader, kernel, PageTableId, layer.PageTableBuffer);
                cmd.SetComputeBufferParam(clipmapBuildShader, kernel, BrickDataId, layer.BrickDataBuffer);
                cmd.SetComputeBufferParam(clipmapBuildShader, kernel, DirtyBricksId, layer.DirtyBrickBuffer);
                cmd.SetComputeBufferParam(clipmapBuildShader, kernel, DirtyGenerationId, layer.DirtyGenerationBuffer);
                cmd.SetComputeBufferParam(clipmapBuildShader, kernel, PageDispatchArgsId,
                    layer.PageDispatchArgsBuffer);
                cmd.SetComputeBufferParam(clipmapBuildShader, kernel, VoxelWorkQueueId, layer.VoxelWorkQueueBuffer);
                cmd.SetComputeBufferParam(clipmapBuildShader, kernel, VoxelDispatchArgsId, layer.VoxelDispatchArgsBuffer);
                BindNaniteResidentGeometry(cmd, clipmapBuildShader, kernel);
            }
            cmd.SetComputeIntParam(clipmapBuildShader, MaterialCountId, view.materialCount);
            cmd.SetComputeIntParam(clipmapBuildShader, TextureSliceCountId,
                materialTextureCache != null ? materialTextureCache.SliceCount : 0);
            cmd.SetComputeTextureParam(clipmapBuildShader, voxelizeWorkQueueKernel,
                BaseColorTexturesId, materialTextureCache.BaseColorArray);
            cmd.SetComputeTextureParam(clipmapBuildShader, voxelizeWorkQueueKernel,
                NormalTexturesId, materialTextureCache.NormalArray);
            cmd.SetComputeTextureParam(clipmapBuildShader, voxelizeWorkQueueKernel,
                MaskTexturesId, materialTextureCache.MaskArray);
            int voxelKernel = voxelizeWorkQueueKernel;
            cmd.SetComputeBufferParam(clipmapBuildShader, voxelKernel, OccupancyId, layer.OccupancyBuffer);
            cmd.SetComputeBufferParam(clipmapBuildShader, voxelKernel, SurfaceId, layer.SurfaceBuffer);
            cmd.SetComputeBufferParam(clipmapBuildShader, voxelKernel, SurfaceUvId, layer.SurfaceUvBuffer);
            cmd.SetComputeBufferParam(clipmapBuildShader, voxelKernel, SurfaceIdentityId, layer.SurfaceIdentityBuffer);
            cmd.SetComputeBufferParam(clipmapBuildShader, voxelKernel, SurfaceStableIdId, layer.SurfaceStableIdBuffer);
            cmd.SetComputeBufferParam(clipmapBuildShader, voxelKernel, SurfaceKeyId, layer.SurfaceKeyBuffer);
            cmd.SetComputeBufferParam(clipmapBuildShader, voxelKernel, DistanceId, layer.DistanceBuffer);
            // Read-only aliases keep queue/indirect metadata out of the D3D11 UAV count.
            // The producer dispatches complete before this consumer starts.
            cmd.SetComputeBufferParam(clipmapBuildShader, voxelKernel,
                VoxelWorkQueueReadId, layer.VoxelWorkQueueBuffer);
            cmd.SetComputeBufferParam(clipmapBuildShader, voxelKernel,
                VoxelDispatchArgsReadId, layer.VoxelDispatchArgsBuffer);
            cmd.SetComputeIntParam(clipmapBuildShader, DirtyBrickCountId, layer.Capacity);
            cmd.SetComputeIntParam(clipmapBuildShader, InstanceCountId, view.instanceCount);
            cmd.SetComputeIntParam(clipmapBuildShader, TargetDynamicId, dynamic ? 1 : 0);
            cmd.SetComputeIntParam(clipmapBuildShader, VoxelWorkCapacityId, layer.VoxelWorkCapacity);
            cmd.SetComputeIntParam(clipmapBuildShader, WorkGenerationId, unchecked((int)workGeneration));
            cmd.DispatchCompute(clipmapBuildShader, markDirtyBricksKernel,
                layer.PageDispatchArgsBuffer, 0u);
            cmd.DispatchCompute(clipmapBuildShader, resetVoxelWorkQueueKernel, 1, 1, 1);
            cmd.DispatchCompute(clipmapBuildShader, buildVoxelWorkQueueKernel,
                Mathf.CeilToInt(view.instanceCount / 64f), 1, 1);
            cmd.DispatchCompute(clipmapBuildShader, clampVoxelDispatchArgsKernel, 1, 1, 1);
            // Phase 0 selects a deterministic winner. Phase 1 commits its complete material,
            // stable IDs and exact triangle index. Both consume one GPU-generated work queue.
            for (int phase = 0; phase < 2; phase++)
            {
                cmd.SetComputeIntParam(clipmapBuildShader, VoxelizePhaseId, phase);
                cmd.DispatchCompute(clipmapBuildShader, voxelKernel, layer.VoxelDispatchArgsBuffer, 0u);
            }
        }

        void BindNaniteResidentGeometry(
            IGIComputeCommands cmd, ComputeShader shader, int kernel)
        {
            GraphicsBuffer pageTable = preparedNaniteViewValid
                ? preparedNaniteView.pageTable : naniteFallbackPageTable;
            GraphicsBuffer residentTable = preparedNaniteViewValid
                ? preparedNaniteView.residentPageTable : naniteFallbackResidentPageTable;
            GraphicsBuffer residency = preparedNaniteViewValid
                ? preparedNaniteView.residencyBits : naniteFallbackResidencyBits;
            GraphicsBuffer vertices = preparedNaniteViewValid
                ? preparedNaniteView.residentVertices : naniteFallbackVertices;
            GraphicsBuffer indices = preparedNaniteViewValid
                ? preparedNaniteView.residentIndices : naniteFallbackIndices;
            GraphicsBuffer subMeshes = preparedNaniteViewValid
                ? preparedNaniteView.residentTriangleSubMeshes
                : naniteFallbackTriangleSubMeshes;
            cmd.SetComputeBufferParam(shader, kernel, NanitePageTableId, pageTable);
            cmd.SetComputeBufferParam(shader, kernel, NaniteResidentPageTableId, residentTable);
            cmd.SetComputeBufferParam(shader, kernel, NaniteResidencyBitsId, residency);
            cmd.SetComputeBufferParam(shader, kernel, NaniteResidentVerticesId, vertices);
            cmd.SetComputeBufferParam(shader, kernel, NaniteResidentIndicesId, indices);
            cmd.SetComputeBufferParam(shader, kernel,
                NaniteResidentTriangleSubMeshesId, subMeshes);
            cmd.SetComputeIntParam(shader, NanitePageCountId,
                preparedNaniteViewValid ? preparedNaniteView.pageCount : 0);
            cmd.SetComputeIntParam(shader, NanitePoolGenerationId,
                preparedNaniteViewValid ? unchecked((int)preparedNaniteView.poolGeneration) : 0);
            cmd.SetComputeIntParam(shader, NaniteGeometryReadyFlagId,
                preparedNaniteViewValid ? unchecked((int)preparedNaniteView.geometryReadyFlag) : 0);
        }

        void BuildDistance(IGIComputeCommands cmd, GIClipmapLayer layer)
        {
            cmd.SetComputeIntParam(clipmapBuildShader, DirtyBrickCountId, layer.Capacity);
            cmd.SetComputeBufferParam(clipmapBuildShader, initializeDistanceKernel, DirtyBricksId, layer.DirtyBrickBuffer);
            cmd.SetComputeBufferParam(clipmapBuildShader, initializeDistanceKernel, OccupancyId, layer.OccupancyBuffer);
            cmd.SetComputeBufferParam(clipmapBuildShader, initializeDistanceKernel, DistanceId, layer.DistanceBuffer);
            cmd.DispatchCompute(clipmapBuildShader, initializeDistanceKernel,
                layer.PageDispatchArgsBuffer, 0u);

            cmd.SetComputeIntParam(clipmapBuildShader, PropagationStepId, 1);
            cmd.SetComputeBufferParam(clipmapBuildShader, propagateDistanceKernel, DirtyBricksId, layer.DirtyBrickBuffer);
            cmd.SetComputeBufferParam(clipmapBuildShader, propagateDistanceKernel, DistanceId, layer.DistanceBuffer);
            int passCount = Mathf.Clamp(distancePropagationPasses, 1, 7);
            for (int pass = 0; pass < passCount; pass++)
                cmd.DispatchCompute(clipmapBuildShader, propagateDistanceKernel,
                    layer.PageDispatchArgsBuffer, 0u);
        }

        void FinalizeDirtyBricks(IGIComputeCommands cmd, GIClipmapLayer layer)
        {
            cmd.SetComputeIntParam(clipmapBuildShader, DirtyBrickCountId, layer.Capacity);
            cmd.SetComputeBufferParam(clipmapBuildShader, finalizeDirtyBricksKernel,
                DirtyBricksId, layer.DirtyBrickBuffer);
            cmd.SetComputeBufferParam(clipmapBuildShader, finalizeDirtyBricksKernel,
                DirtyGenerationId, layer.DirtyGenerationBuffer);
            cmd.SetComputeBufferParam(clipmapBuildShader, finalizeDirtyBricksKernel,
                VoxelDispatchArgsReadId, layer.VoxelDispatchArgsBuffer);
            cmd.SetComputeBufferParam(clipmapBuildShader, finalizeDirtyBricksKernel,
                PageTableId, layer.PageTableBuffer);
            cmd.SetComputeBufferParam(clipmapBuildShader, finalizeDirtyBricksKernel,
                BrickDataId, layer.BrickDataBuffer);
            cmd.SetComputeBufferParam(clipmapBuildShader, finalizeDirtyBricksKernel,
                LevelsId, levelDataBuffer);
            cmd.DispatchCompute(clipmapBuildShader, finalizeDirtyBricksKernel,
                layer.PageDispatchArgsBuffer, 0u);
        }

        void UpdateRadiance(
            IGIComputeCommands cmd,
            GISceneGpuView view,
            GIClipmapLayer layer,
            Vector3 lightDirection,
            Color lightColor,
            Color skyColor)
        {
            cmd.SetComputeIntParam(radianceCacheShader, RadianceDirtyBrickCountId, layer.Capacity);
            cmd.SetComputeIntParam(radianceCacheShader, MaterialCountId, view.materialCount);
            cmd.SetComputeIntParam(radianceCacheShader, TextureSliceCountId,
                materialTextureCache != null ? materialTextureCache.SliceCount : 0);
            cmd.SetComputeIntParam(radianceCacheShader, EnableRadianceShadowsId, enableRadianceShadows ? 1 : 0);
            cmd.SetComputeIntParam(radianceCacheShader, LocalLightCountId, activeLocalLightCount);
            cmd.SetComputeIntParam(radianceCacheShader, MaxLightsPerBrickId, layer.LightsPerBrick);
            cmd.SetComputeIntParam(radianceCacheShader, MaxShadowedLocalLightsId,
                Mathf.Clamp(maxShadowedLocalLightsPerSurface, 0, 8));
            cmd.SetComputeIntParam(radianceCacheShader, SecondaryBounceRaysId,
                Mathf.Clamp(secondaryBounceRays, 0, 4));
            cmd.SetComputeIntParam(radianceCacheShader, RadianceFrameIndexId, generation);
            cmd.SetComputeIntParam(radianceCacheShader, TargetLayerId, layer == staticLayer ? 0 : 1);
            cmd.SetComputeFloatParam(radianceCacheShader, RadianceShadowDistanceId, radianceShadowDistance);
            cmd.SetComputeFloatParam(radianceCacheShader, SecondaryBounceIntensityId,
                Mathf.Clamp01(secondaryBounceIntensity));
            cmd.SetComputeFloatParam(radianceCacheShader, SkyIrradianceScaleId,
                Mathf.Max(0f, skyIrradianceScale));
            cmd.SetComputeFloatParam(radianceCacheShader, MainLightBounceScaleId,
                Mathf.Max(0f, mainLightBounceScale));
            cmd.SetComputeFloatParam(radianceCacheShader, RadianceHistoryWeightId,
                Mathf.Clamp(radianceHistoryWeight, 0f, 0.95f));
            cmd.SetComputeFloatParam(radianceCacheShader, RadianceClampId,
                Mathf.Max(0.1f, radianceClamp));
            cmd.SetComputeVectorParam(radianceCacheShader, MainLightDirectionId, lightDirection);
            cmd.SetComputeVectorParam(radianceCacheShader, MainLightColorId,
                new Vector4(lightColor.r, lightColor.g, lightColor.b, 0f));
            cmd.SetComputeBufferParam(radianceCacheShader, updateRadianceKernel,
                RadianceDirtyBricksId, layer.RadianceDirtyBuffer);
            cmd.SetComputeBufferParam(radianceCacheShader, updateRadianceKernel,
                TargetBrickDataId, layer.BrickDataBuffer);
            cmd.SetComputeBufferParam(radianceCacheShader, updateRadianceKernel,
                TargetOccupancyId, layer.OccupancyBuffer);
            cmd.SetComputeBufferParam(radianceCacheShader, updateRadianceKernel,
                TargetSurfaceId, layer.SurfaceBuffer);
            cmd.SetComputeBufferParam(radianceCacheShader, updateRadianceKernel,
                TargetSurfaceUvId, layer.SurfaceUvBuffer);
            cmd.SetComputeBufferParam(radianceCacheShader, updateRadianceKernel,
                TargetSurfaceIdentityId, layer.SurfaceIdentityBuffer);
            // UpdateSurfaceRadiance reads the previous persistent cache before writing its
            // compact staging result. These bindings are kernel-local in Unity; binding them
            // only for CommitSurfaceRadiance left the update dispatch with undefined inputs.
            cmd.SetComputeBufferParam(radianceCacheShader, updateRadianceKernel,
                TargetRadianceId, layer.RadianceBuffer);
            cmd.SetComputeBufferParam(radianceCacheShader, updateRadianceKernel,
                TargetValidityId, layer.ValidityBuffer);
            cmd.SetComputeBufferParam(radianceCacheShader, updateRadianceKernel,
                TargetRadianceScratchId, radianceScratchBuffer);
            cmd.SetComputeBufferParam(radianceCacheShader, updateRadianceKernel,
                TargetValidityScratchId, validityScratchBuffer);
            cmd.SetComputeBufferParam(radianceCacheShader, updateRadianceKernel,
                MaterialsId, view.materials);
            cmd.SetComputeBufferParam(radianceCacheShader, updateRadianceKernel,
                LocalLightsId, localLightBuffer);
            cmd.SetComputeBufferParam(radianceCacheShader, updateRadianceKernel,
                TargetLightCountsId, layer.LightCountBuffer);
            cmd.SetComputeBufferParam(radianceCacheShader, updateRadianceKernel,
                TargetLightIndicesId, layer.LightIndexBuffer);

            cmd.SetComputeBufferParam(radianceCacheShader, buildBrickLightListsKernel,
                RadianceDirtyBricksId, layer.RadianceDirtyBuffer);
            cmd.SetComputeBufferParam(radianceCacheShader, buildBrickLightListsKernel,
                TargetBrickDataId, layer.BrickDataBuffer);
            cmd.SetComputeBufferParam(radianceCacheShader, buildBrickLightListsKernel,
                LevelsId, levelDataBuffer);
            cmd.SetComputeBufferParam(radianceCacheShader, buildBrickLightListsKernel,
                LocalLightsId, localLightBuffer);
            cmd.SetComputeBufferParam(radianceCacheShader, buildBrickLightListsKernel,
                TargetLightCountsId, layer.LightCountBuffer);
            cmd.SetComputeBufferParam(radianceCacheShader, buildBrickLightListsKernel,
                TargetLightIndicesId, layer.LightIndexBuffer);
            cmd.SetComputeTextureParam(radianceCacheShader, updateRadianceKernel,
                BaseColorTexturesId, materialTextureCache.BaseColorArray);
            cmd.SetComputeTextureParam(radianceCacheShader, updateRadianceKernel,
                EmissionTexturesId, materialTextureCache.EmissionArray);
            cmd.SetComputeTextureParam(radianceCacheShader, updateRadianceKernel,
                MaskTexturesId, materialTextureCache.MaskArray);
            cmd.SetComputeBufferParam(radianceCacheShader, updateRadianceKernel,
                LevelsId, levelDataBuffer);
            cmd.SetComputeBufferParam(radianceCacheShader, updateRadianceKernel,
                StaticPageTableId, staticLayer.PageTableBuffer);
            cmd.SetComputeBufferParam(radianceCacheShader, updateRadianceKernel,
                StaticOccupancyId, staticLayer.OccupancyBuffer);
            cmd.SetComputeBufferParam(radianceCacheShader, updateRadianceKernel,
                StaticSurfaceId, staticLayer.SurfaceBuffer);
            cmd.SetComputeBufferParam(radianceCacheShader, updateRadianceKernel,
                StaticSurfaceIdentityId, staticLayer.SurfaceIdentityBuffer);
            cmd.SetComputeBufferParam(radianceCacheShader, updateRadianceKernel,
                StaticDistanceId, staticLayer.DistanceBuffer);
            cmd.SetComputeBufferParam(radianceCacheShader, updateRadianceKernel,
                StaticRadianceId, staticLayer.RadianceBuffer);
            cmd.SetComputeBufferParam(radianceCacheShader, updateRadianceKernel,
                StaticValidityId, staticLayer.ValidityBuffer);
            cmd.SetComputeBufferParam(radianceCacheShader, updateRadianceKernel,
                DynamicPageTableId, dynamicLayer.PageTableBuffer);
            cmd.SetComputeBufferParam(radianceCacheShader, updateRadianceKernel,
                DynamicOccupancyId, dynamicLayer.OccupancyBuffer);
            cmd.SetComputeBufferParam(radianceCacheShader, updateRadianceKernel,
                DynamicSurfaceId, dynamicLayer.SurfaceBuffer);
            cmd.SetComputeBufferParam(radianceCacheShader, updateRadianceKernel,
                DynamicSurfaceIdentityId, dynamicLayer.SurfaceIdentityBuffer);
            cmd.SetComputeBufferParam(radianceCacheShader, updateRadianceKernel,
                DynamicDistanceId, dynamicLayer.DistanceBuffer);
            cmd.SetComputeBufferParam(radianceCacheShader, updateRadianceKernel,
                DynamicRadianceId, dynamicLayer.RadianceBuffer);
            cmd.SetComputeBufferParam(radianceCacheShader, updateRadianceKernel,
                DynamicValidityId, dynamicLayer.ValidityBuffer);
            cmd.DispatchCompute(radianceCacheShader, buildBrickLightListsKernel,
                layer.PageDispatchArgsBuffer, 12u);
            cmd.DispatchCompute(radianceCacheShader, updateRadianceKernel,
                layer.PageDispatchArgsBuffer, 12u);

            cmd.SetComputeIntParam(radianceCacheShader, RadianceDirtyBrickCountId,
                layer.Capacity);
            cmd.SetComputeBufferParam(radianceCacheShader, commitRadianceKernel,
                RadianceDirtyBricksId, layer.RadianceDirtyBuffer);
            cmd.SetComputeBufferParam(radianceCacheShader, commitRadianceKernel,
                TargetRadianceScratchId, radianceScratchBuffer);
            cmd.SetComputeBufferParam(radianceCacheShader, commitRadianceKernel,
                TargetValidityScratchId, validityScratchBuffer);
            cmd.SetComputeBufferParam(radianceCacheShader, commitRadianceKernel,
                TargetRadianceId, layer.RadianceBuffer);
            cmd.SetComputeBufferParam(radianceCacheShader, commitRadianceKernel,
                TargetValidityId, layer.ValidityBuffer);
            cmd.DispatchCompute(radianceCacheShader, commitRadianceKernel,
                layer.PageDispatchArgsBuffer, 12u);
        }

        int ComputeLightingSignature(Vector3 lightDirection, Color lightColor, Color skyColor)
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + RadianceAlgorithmVersion;
                // TOD modifies these floats every frame.  Quantisation requeues radiance only
                // for a perceptible lighting change, instead of continuously invalidating the
                // cache and the one-ray temporal reconstruction.
                hash = hash * 31 + QuantizeLighting(lightDirection.x, 8f);
                hash = hash * 31 + QuantizeLighting(lightDirection.y, 8f);
                hash = hash * 31 + QuantizeLighting(lightDirection.z, 8f);
                hash = hash * 31 + QuantizeLighting(lightColor.r, 4f);
                hash = hash * 31 + QuantizeLighting(lightColor.g, 4f);
                hash = hash * 31 + QuantizeLighting(lightColor.b, 4f);
                hash = hash * 31 + QuantizeLighting(skyColor.r, 4f);
                hash = hash * 31 + QuantizeLighting(skyColor.g, 4f);
                hash = hash * 31 + QuantizeLighting(skyColor.b, 4f);
                hash = hash * 31 + secondaryBounceRays;
                hash = hash * 31 + secondaryBounceIntensity.GetHashCode();
                hash = hash * 31 + skyIrradianceScale.GetHashCode();
                hash = hash * 31 + mainLightBounceScale.GetHashCode();
                hash = hash * 31 + radianceShadowDistance.GetHashCode();
                hash = hash * 31 + (enableRadianceShadows ? 1 : 0);
                hash = hash * 31 + maxShadowedLocalLightsPerSurface;
                hash = hash * 31 + maxLocalLightsPerBrick;
                hash = hash * 31 + staticBounceSweeps;
                hash = hash * 31 + radianceHistoryWeight.GetHashCode();
                hash = hash * 31 + radianceClamp.GetHashCode();
                hash = hash * 31 + (materialTextureCache != null ? materialTextureCache.Revision : 0);
                for (int i = 0; i < gpuLocalLights.Count; i++)
                {
                    GIGpuLocalLightData light = gpuLocalLights[i];
                    hash = hash * 31 + light.positionRange.GetHashCode();
                    hash = hash * 31 + light.colorIntensity.GetHashCode();
                    hash = hash * 31 + light.directionOuterCos.GetHashCode();
                    hash = hash * 31 + light.parameters.GetHashCode();
                }
                IReadOnlyList<GIGpuMaterialData> materials = scene.CpuMaterials;
                for (int i = 0; i < materials.Count; i++)
                    hash = hash * 31 + (int)materials[i].revision;
                return hash;
            }
        }

        static int QuantizeLighting(float value, float scale) => Mathf.RoundToInt(value * scale);

        float EstimatePoolMiB()
        {
            long bytesPerBrick =
                GIClipmapConstants.OccupancyWordsPerBrick * 4L +
                GIClipmapConstants.SurfaceWordsPerBrick * 4L +
                GIClipmapConstants.SurfaceUvWordsPerBrick * 4L +
                GIClipmapConstants.SurfaceIdentityWordsPerBrick * 4L +
                GIClipmapConstants.SurfaceStableIdEntriesPerBrick * 8L +
                GIClipmapConstants.SurfaceKeyWordsPerBrick * 4L +
                GIClipmapConstants.DistanceWordsPerBrick * 4L +
                GIClipmapConstants.RadianceWordsPerBrick * 4L +
                GIClipmapConstants.ValidityWordsPerBrick * 4L +
                GIClipmapConstants.BrickDataStride;
            long bytes = bytesPerBrick * (staticBrickCapacity + dynamicBrickCapacity) +
                         GIClipmapConstants.PageTableEntries * 4L * 2L +
                         // Required mask/hash are per logical page and physical hash,
                         // radiance-generation and free-list are per physical page.
                         GIClipmapConstants.PageTableEntries * 8L * 2L +
                         (staticBrickCapacity + dynamicBrickCapacity) * 12L +
                         GIClipmapConstants.LevelCount * GIClipmapConstants.LevelStride +
                         Math.Max(staticBrickCapacity, dynamicBrickCapacity) *
                         (GIClipmapConstants.RadianceWordsPerBrick +
                          GIClipmapConstants.ValidityWordsPerBrick) * 4L +
                         Math.Max(1, maxUploadedLocalLights) * GILightAbi.LocalLightStride +
                         (staticBrickCapacity + dynamicBrickCapacity) *
                         (8L + Math.Max(8, maxLocalLightsPerBrick) * 4L) +
                         Math.Max(1024, Math.Min(1048576, maxVoxelWorkItems)) * 16L * 2L + 40L +
                         (geometryCache?.ResidentBytes ?? 0L);
            return bytes / (1024f * 1024f);
        }

        void WarnOnDegradation()
        {
            if (Time.frameCount - lastWarningFrame < 120)
                return;
            if (droppedStaticBricks > 0 || droppedDynamicBricks > 0)
            {
                lastWarningFrame = Time.frameCount;
                Debug.LogWarning(
                    $"[RealtimeGI] Clipmap pool exhausted. Dropped static={droppedStaticBricks}, " +
                    $"dynamic={droppedDynamicBricks}. Increase capacity or reduce GI bounds.", this);
            }
            else if (invalidGeometryCount > 0)
            {
                lastWarningFrame = Time.frameCount;
                Debug.LogWarning(
                    $"[RealtimeGI] {invalidGeometryCount} geometry sources could not build a triangle stream.", this);
            }
        }

        public bool TryGetGpuView(out GIClipmapGpuView view)
        {
            view = new GIClipmapGpuView(
                levelDataBuffer, staticLayer, dynamicLayer, localLightBuffer, geometryCache,
                preparedNaniteViewValid ? preparedNaniteView.pageTable : naniteFallbackPageTable,
                preparedNaniteViewValid ? preparedNaniteView.residentPageTable : naniteFallbackResidentPageTable,
                preparedNaniteViewValid ? preparedNaniteView.residencyBits : naniteFallbackResidencyBits,
                preparedNaniteViewValid ? preparedNaniteView.residentVertices : naniteFallbackVertices,
                preparedNaniteViewValid ? preparedNaniteView.residentIndices : naniteFallbackIndices,
                preparedNaniteViewValid ? preparedNaniteView.residentTriangleSubMeshes : naniteFallbackTriangleSubMeshes,
                preparedNaniteViewValid ? preparedNaniteView.pageCount : 0,
                preparedNaniteViewValid ? preparedNaniteView.poolGeneration : 0u,
                preparedNaniteViewValid ? preparedNaniteView.geometryReadyFlag : 0u,
                materialTextureCache?.BaseColorHandle,
                materialTextureCache?.EmissionHandle,
                materialTextureCache?.MaskHandle,
                materialTextureCache?.SliceCount ?? 0,
                activeLocalLightCount, generation, lightingRevision,
                Mathf.Max(0f, skyIrradianceScale), Mathf.Max(0f, mainLightBounceScale));
            return initialized && view.IsValid;
        }

        void OnDisable()
        {
            if (Active == this)
                Active = null;
            ReleaseResources();
        }

        void OnDestroy()
        {
            if (Active == this)
                Active = null;
            ReleaseResources();
        }

        void ReleaseResources()
        {
            preparedValid = false;
            preparedSceneView = default;
            preparedFrame = -1;
            recordedFrame = -1;
            geometryCache?.Dispose();
            materialTextureCache?.Dispose();
            staticLayer?.Dispose();
            dynamicLayer?.Dispose();
            levelDataBuffer?.Release();
            localLightBuffer?.Release();
            radianceScratchBuffer?.Release();
            validityScratchBuffer?.Release();
            naniteFallbackPageTable?.Release();
            naniteFallbackResidentPageTable?.Release();
            naniteFallbackResidencyBits?.Release();
            naniteFallbackVertices?.Release();
            naniteFallbackIndices?.Release();
            naniteFallbackTriangleSubMeshes?.Release();
            geometryCache = null;
            materialTextureCache = null;
            staticLayer = null;
            dynamicLayer = null;
            levelDataBuffer = null;
            localLightBuffer = null;
            radianceScratchBuffer = null;
            validityScratchBuffer = null;
            naniteFallbackPageTable = null;
            naniteFallbackResidentPageTable = null;
            naniteFallbackResidencyBits = null;
            naniteFallbackVertices = null;
            naniteFallbackIndices = null;
            naniteFallbackTriangleSubMeshes = null;
            preparedNaniteView = default;
            preparedNaniteViewValid = false;
            initialized = false;
            originsInitialized = false;
            lastUpdateFrame = -1;
            lightingSignature = 0;
            lightingRevision = 0;
            allocatedStaticCapacity = 0;
            allocatedDynamicCapacity = 0;
            localLightCapacity = 0;
            radianceScratchCapacity = 0;
            validityScratchCapacity = 0;
            activeLocalLightCount = 0;
            remainingStaticBounceSweeps = 0;
        }

    }
}
