using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace RealtimeGI
{
    /// <summary>
    /// Phase 1/2 sparse Clipmap builder. Static world Bricks persist across camera scrolling;
    /// Dynamic Overlay Bricks are cleared and rebuilt from the current instance list every frame.
    /// </summary>
    [ExecuteAlways]
    [DefaultExecutionOrder(11000)]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(RealtimeGIScene))]
    public sealed class GIClipmapSystem : MonoBehaviour
    {
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
        [Range(1, 7)] public int distancePropagationPasses = 7;
        [Min(1)] public int staticRadianceBricksPerFrame = 64;
        [Min(1)] public int dynamicRadianceBricksPerFrame = 128;
        public bool enableRadianceShadows = true;
        [Min(1f)] public float radianceShadowDistance = 80f;
        [Header("Local lights and multi-bounce")]
        [Range(0, 64)] public int maxLocalLights = 16;
        [Range(0, 8)] public int maxShadowedLocalLightsPerSurface = 2;
        [Range(0, 4)] public int secondaryBounceRays = 1;
        [Range(0f, 1f)] public float secondaryBounceIntensity = 0.55f;
        [Range(1, 8)] public int staticBounceSweeps = 3;
        [Range(0f, 0.95f)] public float radianceHistoryWeight = 0.35f;
        [Min(0.1f)] public float radianceClamp = 32f;
        [Range(32, 512)] public int materialTextureResolution = 128;
        public bool automaticUpdate = true;

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
        [SerializeField] int pendingStaticRadianceBricks;
        [SerializeField] int pendingDynamicRadianceBricks;
        [SerializeField] int updatedRadianceBricks;
        [SerializeField] int activeLocalLightCount;
        [SerializeField] int remainingStaticBounceSweeps;
        [SerializeField] float estimatedPoolMiB;

        readonly Vector3Int[] levelOrigins = new Vector3Int[GIClipmapConstants.LevelCount];
        readonly Vector3Int[] previousLevelOrigins = new Vector3Int[GIClipmapConstants.LevelCount];
        readonly GIGpuClipmapLevelData[] levelData = new GIGpuClipmapLevelData[GIClipmapConstants.LevelCount];
        readonly HashSet<GIClipmapBrickKey> staticRequired = new HashSet<GIClipmapBrickKey>();
        readonly HashSet<GIClipmapBrickKey> dynamicRequired = new HashSet<GIClipmapBrickKey>();
        readonly HashSet<uint> activeGeometryIndices = new HashSet<uint>();
        readonly HashSet<GIClipmapBrickKey> invalidatedStaticBricks = new HashSet<GIClipmapBrickKey>();
        readonly Dictionary<uint, StaticInstanceState> previousStaticInstances =
            new Dictionary<uint, StaticInstanceState>(256);
        readonly HashSet<uint> observedStaticInstances = new HashSet<uint>();
        readonly List<uint> removedStaticInstances = new List<uint>(64);
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
        int clearKernel = -1;
        int voxelizeKernel = -1;
        int initializeDistanceKernel = -1;
        int propagateDistanceKernel = -1;
        int updateRadianceKernel = -1;
        int commitRadianceKernel = -1;
        int lightingSignature;
        bool initialized;
        bool originsInitialized;
        int lastUpdateFrame = -1;
        int lastWarningFrame = -10000;
        int allocatedStaticCapacity;
        int allocatedDynamicCapacity;
        int localLightCapacity;
        int radianceScratchCapacity;
        int validityScratchCapacity;

        static readonly int DirtyBricksId = Shader.PropertyToID("_GIDirtyBricks");
        static readonly int DirtyBrickCountId = Shader.PropertyToID("_GIDirtyBrickCount");
        static readonly int OccupancyId = Shader.PropertyToID("_GIOccupancy");
        static readonly int SurfaceId = Shader.PropertyToID("_GISurface");
        static readonly int SurfaceUvId = Shader.PropertyToID("_GISurfaceUV");
        static readonly int DistanceId = Shader.PropertyToID("_GIDistance");
        static readonly int InstancesId = Shader.PropertyToID("_GIInstances");
        static readonly int MaterialBindingsId = Shader.PropertyToID("_GIMaterialBindings");
        static readonly int GeometryRangesId = Shader.PropertyToID("_GIGeometryRanges");
        static readonly int VerticesId = Shader.PropertyToID("_GIVertices");
        static readonly int UvsId = Shader.PropertyToID("_GIUVs");
        static readonly int IndicesId = Shader.PropertyToID("_GIIndices");
        static readonly int TriangleSubMeshesId = Shader.PropertyToID("_GITriangleSubMeshes");
        static readonly int LevelsId = Shader.PropertyToID("_GIClipmapLevels");
        static readonly int PageTableId = Shader.PropertyToID("_GIPageTable");
        static readonly int InstanceIndexId = Shader.PropertyToID("_GIInstanceIndex");
        static readonly int TriangleBaseId = Shader.PropertyToID("_GITriangleBase");
        static readonly int MaxCellsId = Shader.PropertyToID("_GIMaxCellsPerTriangle");
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
        static readonly int SkyColorId = Shader.PropertyToID("_GISkyColor");
        static readonly int TargetBrickDataId = Shader.PropertyToID("_GITargetBrickData");
        static readonly int TargetOccupancyId = Shader.PropertyToID("_GITargetOccupancy");
        static readonly int TargetSurfaceId = Shader.PropertyToID("_GITargetSurface");
        static readonly int TargetSurfaceUvId = Shader.PropertyToID("_GITargetSurfaceUV");
        static readonly int TargetRadianceId = Shader.PropertyToID("_GITargetRadiance");
        static readonly int TargetValidityId = Shader.PropertyToID("_GITargetValidity");
        static readonly int StaticPageTableId = Shader.PropertyToID("_GIStaticPageTable");
        static readonly int StaticOccupancyId = Shader.PropertyToID("_GIStaticOccupancy");
        static readonly int StaticSurfaceId = Shader.PropertyToID("_GIStaticSurface");
        static readonly int StaticDistanceId = Shader.PropertyToID("_GIStaticDistance");
        static readonly int DynamicPageTableId = Shader.PropertyToID("_GIDynamicPageTable");
        static readonly int DynamicOccupancyId = Shader.PropertyToID("_GIDynamicOccupancy");
        static readonly int DynamicSurfaceId = Shader.PropertyToID("_GIDynamicSurface");
        static readonly int DynamicDistanceId = Shader.PropertyToID("_GIDynamicDistance");
        static readonly int RadianceShadowDistanceId = Shader.PropertyToID("_GIRadianceShadowDistance");
        static readonly int EnableRadianceShadowsId = Shader.PropertyToID("_GIEnableRadianceShadows");
        static readonly int LocalLightsId = Shader.PropertyToID("_GILocalLights");
        static readonly int LocalLightCountId = Shader.PropertyToID("_GILocalLightCount");
        static readonly int MaxShadowedLocalLightsId = Shader.PropertyToID("_GIMaxShadowedLocalLights");
        static readonly int StaticRadianceId = Shader.PropertyToID("_GIStaticRadiance");
        static readonly int StaticValidityId = Shader.PropertyToID("_GIStaticValidity");
        static readonly int DynamicRadianceId = Shader.PropertyToID("_GIDynamicRadiance");
        static readonly int DynamicValidityId = Shader.PropertyToID("_GIDynamicValidity");
        static readonly int TargetRadianceScratchId = Shader.PropertyToID("_GITargetRadianceScratch");
        static readonly int TargetValidityScratchId = Shader.PropertyToID("_GITargetValidityScratch");
        static readonly int BaseColorTexturesId = Shader.PropertyToID("_GIBaseColorTextures");
        static readonly int EmissionTexturesId = Shader.PropertyToID("_GIEmissionTextures");
        static readonly int TextureSliceCountId = Shader.PropertyToID("_GITextureSliceCount");
        static readonly int SecondaryBounceRaysId = Shader.PropertyToID("_GISecondaryBounceRays");
        static readonly int SecondaryBounceIntensityId = Shader.PropertyToID("_GISecondaryBounceIntensity");
        static readonly int RadianceHistoryWeightId = Shader.PropertyToID("_GIRadianceHistoryWeight");
        static readonly int RadianceClampId = Shader.PropertyToID("_GIRadianceClamp");
        static readonly int RadianceFrameIndexId = Shader.PropertyToID("_GIRadianceFrameIndex");
        static readonly int TargetLayerId = Shader.PropertyToID("_GITargetLayer");

        public int Generation => generation;
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

        void LateUpdate()
        {
            if (automaticUpdate)
                UpdateClipmaps();
        }

        public void UpdateClipmaps()
        {
            if (!isActiveAndEnabled || !SystemInfo.supportsComputeShaders ||
                SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                return;
            if (Application.isPlaying && lastUpdateFrame == Time.frameCount)
                return;
            lastUpdateFrame = Time.frameCount;

            if (!EnsureInitialized())
                return;
            scene.BuildNow();
            if (!scene.TryGetGpuView(out GISceneGpuView sceneView))
                return;
            materialTextureCache.Update(scene, materialTextureResolution);

            Vector3 focusPosition = ResolveFocusPosition();
            BuildLocalLightList(focusPosition);
            bool originsChanged = UpdateLevelOrigins(focusPosition);
            BuildRequiredBrickSets();
            bool geometryRebuilt = geometryCache.RebuildIfNeeded(scene, activeGeometryIndices);
            streamedVertexCount = geometryCache.VertexCount;
            streamedTriangleCount = geometryCache.TriangleCount;
            invalidGeometryCount = geometryCache.InvalidGeometryCount;
            BuildStaticInvalidationSet();

            staticLayer.UpdateRequired(staticRequired, levelOrigins, false);
            staticLayer.MarkDirty(invalidatedStaticBricks);
            dynamicLayer.UpdateRequired(dynamicRequired, levelOrigins, true);

            Light sun = RenderSettings.sun;
            Vector3 lightDirection = sun != null
                ? sun.transform.forward
                : new Vector3(0.3f, -0.8f, 0.2f).normalized;
            Color lightColorValue = sun != null ? sun.color.linear * sun.intensity : Color.white;
            Color skyColorValue = RenderSettings.ambientSkyColor.linear;
            int nextLightingSignature = ComputeLightingSignature(
                lightDirection, lightColorValue, skyColorValue);
            if (nextLightingSignature != lightingSignature)
            {
                lightingSignature = nextLightingSignature;
                staticLayer.EnqueueAllRadiance();
                dynamicLayer.EnqueueAllRadiance();
                remainingStaticBounceSweeps = Mathf.Max(0, staticBounceSweeps - 1);
            }
            else if (staticLayer.DirtyBrickCount > 0)
                remainingStaticBounceSweeps = Mathf.Max(
                    remainingStaticBounceSweeps, Mathf.Max(0, staticBounceSweeps - 1));

            // A sweep consumes the previous sweep's cache. Requeue only after its backlog is
            // completely drained so the iteration is deterministic under a per-frame budget.
            if (staticLayer.PendingRadianceCount == 0 && remainingStaticBounceSweeps > 0)
            {
                staticLayer.EnqueueAllRadiance();
                remainingStaticBounceSweeps--;
            }
            staticLayer.PrepareRadianceUpdates(staticRadianceBricksPerFrame);
            dynamicLayer.PrepareRadianceUpdates(dynamicRadianceBricksPerFrame);

            CommandBuffer cmd = CommandBufferPool.Get("RealtimeGI/Clipmap Update");
            cmd.BeginSample("RealtimeGI/Clipmap Total");
            cmd.BeginSample("RealtimeGI/Clipmap Clear");
            ClearDirtyBricks(cmd, staticLayer);
            ClearDirtyBricks(cmd, dynamicLayer);
            cmd.EndSample("RealtimeGI/Clipmap Clear");

            if (staticLayer.DirtyBrickCount > 0)
            {
                cmd.BeginSample("RealtimeGI/Voxelize Static");
                VoxelizeLayer(cmd, sceneView, staticLayer, false);
                cmd.EndSample("RealtimeGI/Voxelize Static");
            }
            if (dynamicLayer.DirtyBrickCount > 0)
            {
                cmd.BeginSample("RealtimeGI/Voxelize Dynamic");
                VoxelizeLayer(cmd, sceneView, dynamicLayer, true);
                cmd.EndSample("RealtimeGI/Voxelize Dynamic");
            }
            cmd.BeginSample("RealtimeGI/Distance Propagation");
            BuildDistance(cmd, staticLayer);
            BuildDistance(cmd, dynamicLayer);
            cmd.EndSample("RealtimeGI/Distance Propagation");
            cmd.BeginSample("RealtimeGI/Radiance Update");
            UpdateRadiance(cmd, sceneView, staticLayer, lightDirection, lightColorValue, skyColorValue);
            UpdateRadiance(cmd, sceneView, dynamicLayer, lightDirection, lightColorValue, skyColorValue);
            cmd.EndSample("RealtimeGI/Radiance Update");
            cmd.EndSample("RealtimeGI/Clipmap Total");
            Graphics.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);

            staticBrickCount = staticLayer.AllocatedCount;
            dynamicBrickCount = dynamicLayer.AllocatedCount;
            droppedStaticBricks = staticLayer.DroppedBrickCount;
            droppedDynamicBricks = dynamicLayer.DroppedBrickCount;
            pendingStaticRadianceBricks = staticLayer.PendingRadianceCount;
            pendingDynamicRadianceBricks = dynamicLayer.PendingRadianceCount;
            updatedRadianceBricks = staticLayer.RadianceDirtyCount + dynamicLayer.RadianceDirtyCount;
            generation++;
            if (originsChanged || geometryRebuilt || invalidatedStaticBricks.Count > 0)
                estimatedPoolMiB = EstimatePoolMiB();
            WarnOnDegradation();
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
                voxelizeKernel = clipmapBuildShader.FindKernel("VoxelizeInstance");
                initializeDistanceKernel = clipmapBuildShader.FindKernel("InitializeDistance");
                propagateDistanceKernel = clipmapBuildShader.FindKernel("PropagateDistance");
                updateRadianceKernel = radianceCacheShader.FindKernel("UpdateSurfaceRadiance");
                commitRadianceKernel = radianceCacheShader.FindKernel("CommitSurfaceRadiance");
                geometryCache = new GIGeometryStreamCache();
                materialTextureCache = new GIMaterialTextureCache();
                staticLayer = new GIClipmapLayer(staticBrickCapacity, "GI Static");
                dynamicLayer = new GIClipmapLayer(dynamicBrickCapacity, "GI Dynamic");
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
            int requiredLights = Mathf.NextPowerOfTwo(Mathf.Max(1, maxLocalLights));
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
            localLightScratch.Sort((a, b) =>
                LocalLightScore(b, focusPosition).CompareTo(LocalLightScore(a, focusPosition)));

            gpuLocalLights.Clear();
            int count = Mathf.Min(Mathf.Max(0, maxLocalLights), localLightScratch.Count);
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

        static float LocalLightScore(GILocalLight adapter, Vector3 focusPosition)
        {
            Light light = adapter.Source;
            float distance = Vector3.Distance(light.transform.position, focusPosition);
            float coverage = Mathf.Clamp01(1f - distance / Mathf.Max(0.01f, light.range));
            Color color = light.color.linear;
            return light.intensity * color.maxColorComponent *
                   Mathf.Max(0.05f, adapter.indirectMultiplier) * (0.1f + coverage);
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

        void BuildRequiredBrickSets()
        {
            staticRequired.Clear();
            dynamicRequired.Clear();
            activeGeometryIndices.Clear();
            IReadOnlyList<GIGpuInstanceData> instances = scene.CpuInstances;
            for (int i = 0; i < instances.Count; i++)
            {
                GIGpuInstanceData instance = instances[i];
                GIInstanceFlags flags = (GIInstanceFlags)instance.flags;
                if ((flags & (GIInstanceFlags.Occluder | GIInstanceFlags.Contributor)) == 0)
                    continue;
                HashSet<GIClipmapBrickKey> target = (flags & GIInstanceFlags.Dynamic) != 0
                    ? dynamicRequired
                    : staticRequired;
                if (AddSphereBricks(instance.worldBoundingSphere, target))
                    activeGeometryIndices.Add(instance.geometryIndex);
            }
        }

        bool AddSphereBricks(Vector4 sphere, HashSet<GIClipmapBrickKey> target)
        {
            bool overlapsAnyLevel = false;
            Vector3 center = new Vector3(sphere.x, sphere.y, sphere.z);
            float radius = Mathf.Max(0.01f, sphere.w);
            for (int level = 0; level < GIClipmapConstants.LevelCount; level++)
            {
                float brickWorldSize = GIClipmapConstants.CellSizes[level] * GIClipmapConstants.BrickSize;
                Vector3Int min = FloorToBrick(center - Vector3.one * radius, brickWorldSize);
                Vector3Int max = FloorToBrick(center + Vector3.one * radius, brickWorldSize);
                Vector3Int clipMin = levelOrigins[level];
                Vector3Int clipMax = clipMin + Vector3Int.one * (GIClipmapConstants.BricksPerAxis - 1);
                min = Vector3Int.Max(min, clipMin);
                max = Vector3Int.Min(max, clipMax);
                if (min.x > max.x || min.y > max.y || min.z > max.z)
                    continue;
                overlapsAnyLevel = true;
                for (int z = min.z; z <= max.z; z++)
                for (int y = min.y; y <= max.y; y++)
                for (int x = min.x; x <= max.x; x++)
                    target.Add(new GIClipmapBrickKey(level, new Vector3Int(x, y, z)));
            }
            return overlapsAnyLevel;
        }

        static Vector3Int FloorToBrick(Vector3 position, float brickWorldSize) => new Vector3Int(
            Mathf.FloorToInt(position.x / brickWorldSize),
            Mathf.FloorToInt(position.y / brickWorldSize),
            Mathf.FloorToInt(position.z / brickWorldSize));

        void BuildStaticInvalidationSet()
        {
            invalidatedStaticBricks.Clear();
            observedStaticInstances.Clear();
            IReadOnlyList<GIGpuInstanceData> instances = scene.CpuInstances;
            IReadOnlyList<GIGpuGeometryData> geometries = scene.CpuGeometries;
            IReadOnlyList<GIGpuMaterialBindingData> bindings = scene.CpuMaterialBindings;
            for (int i = 0; i < instances.Count; i++)
            {
                GIGpuInstanceData instance = instances[i];
                GIInstanceFlags flags = (GIInstanceFlags)instance.flags;
                if ((flags & GIInstanceFlags.Dynamic) != 0 ||
                    (flags & (GIInstanceFlags.Occluder | GIInstanceFlags.Contributor)) == 0)
                    continue;
                StaticInstanceState next = BuildStaticState(instance, geometries, bindings);
                observedStaticInstances.Add(instance.objectId);
                if (!previousStaticInstances.TryGetValue(instance.objectId, out StaticInstanceState previous))
                    AddSphereBricks(instance.worldBoundingSphere, invalidatedStaticBricks);
                else if (previous.signature != next.signature || previous.sphere != next.sphere)
                {
                    AddSphereBricks(previous.sphere, invalidatedStaticBricks);
                    AddSphereBricks(next.sphere, invalidatedStaticBricks);
                }
                previousStaticInstances[instance.objectId] = next;
            }

            removedStaticInstances.Clear();
            foreach (KeyValuePair<uint, StaticInstanceState> pair in previousStaticInstances)
            {
                if (observedStaticInstances.Contains(pair.Key))
                    continue;
                AddSphereBricks(pair.Value.sphere, invalidatedStaticBricks);
                removedStaticInstances.Add(pair.Key);
            }
            for (int i = 0; i < removedStaticInstances.Count; i++)
                previousStaticInstances.Remove(removedStaticInstances[i]);
            invalidatedStaticBrickCount = invalidatedStaticBricks.Count;
        }

        static StaticInstanceState BuildStaticState(
            GIGpuInstanceData instance,
            IReadOnlyList<GIGpuGeometryData> geometries,
            IReadOnlyList<GIGpuMaterialBindingData> bindings)
        {
            unchecked
            {
                int hash = 17;
                if (instance.geometryIndex < geometries.Count)
                {
                    GIGpuGeometryData geometry = geometries[(int)instance.geometryIndex];
                    hash = hash * 31 + (int)geometry.sourceObjectId;
                    hash = hash * 31 + (int)geometry.sourceRevision;
                    hash = hash * 31 + (int)geometry.kind;
                }
                hash = hash * 31 + (int)instance.revision;
                hash = hash * 31 + instance.localToWorld.GetHashCode();
                const GIInstanceFlags geometryFlags = GIInstanceFlags.Occluder |
                                                      GIInstanceFlags.Contributor |
                                                      GIInstanceFlags.TwoSided |
                                                      GIInstanceFlags.AlphaTested;
                hash = hash * 31 + (int)((GIInstanceFlags)instance.flags & geometryFlags);
                int first = (int)instance.firstMaterialBinding;
                int count = (int)instance.materialBindingCount;
                for (int binding = 0; binding < count && first + binding < bindings.Count; binding++)
                    hash = hash * 31 + (int)bindings[first + binding].materialIndex;
                return new StaticInstanceState(hash, instance.worldBoundingSphere);
            }
        }

        void ClearDirtyBricks(CommandBuffer cmd, GIClipmapLayer layer)
        {
            if (layer.DirtyBrickCount <= 0)
                return;
            cmd.SetComputeIntParam(clipmapBuildShader, DirtyBrickCountId, layer.DirtyBrickCount);
            cmd.SetComputeBufferParam(clipmapBuildShader, clearKernel, DirtyBricksId, layer.DirtyBrickBuffer);
            cmd.SetComputeBufferParam(clipmapBuildShader, clearKernel, OccupancyId, layer.OccupancyBuffer);
            cmd.SetComputeBufferParam(clipmapBuildShader, clearKernel, SurfaceId, layer.SurfaceBuffer);
            cmd.SetComputeBufferParam(clipmapBuildShader, clearKernel, SurfaceUvId, layer.SurfaceUvBuffer);
            cmd.SetComputeBufferParam(clipmapBuildShader, clearKernel, DistanceId, layer.DistanceBuffer);
            cmd.SetComputeBufferParam(clipmapBuildShader, clearKernel, RadianceId, layer.RadianceBuffer);
            cmd.SetComputeBufferParam(clipmapBuildShader, clearKernel, ValidityId, layer.ValidityBuffer);
            cmd.DispatchCompute(clipmapBuildShader, clearKernel, layer.DirtyBrickCount, 1, 1);
        }

        void VoxelizeLayer(CommandBuffer cmd, GISceneGpuView view, GIClipmapLayer layer, bool dynamic)
        {
            if (geometryCache.TriangleCount <= 0)
                return;
            cmd.SetComputeIntParam(clipmapBuildShader, MaxCellsId, maxCellsPerTriangle);
            cmd.SetComputeBufferParam(clipmapBuildShader, voxelizeKernel, InstancesId, view.instances);
            cmd.SetComputeBufferParam(clipmapBuildShader, voxelizeKernel, MaterialBindingsId, view.materialBindings);
            cmd.SetComputeBufferParam(clipmapBuildShader, voxelizeKernel, GeometryRangesId, geometryCache.RangeBuffer);
            cmd.SetComputeBufferParam(clipmapBuildShader, voxelizeKernel, VerticesId, geometryCache.VertexBuffer);
            cmd.SetComputeBufferParam(clipmapBuildShader, voxelizeKernel, UvsId, geometryCache.UvBuffer);
            cmd.SetComputeBufferParam(clipmapBuildShader, voxelizeKernel, IndicesId, geometryCache.IndexBuffer);
            cmd.SetComputeBufferParam(clipmapBuildShader, voxelizeKernel, TriangleSubMeshesId, geometryCache.TriangleSubMeshBuffer);
            cmd.SetComputeBufferParam(clipmapBuildShader, voxelizeKernel, LevelsId, levelDataBuffer);
            cmd.SetComputeBufferParam(clipmapBuildShader, voxelizeKernel, PageTableId, layer.PageTableBuffer);
            cmd.SetComputeBufferParam(clipmapBuildShader, voxelizeKernel, OccupancyId, layer.OccupancyBuffer);
            cmd.SetComputeBufferParam(clipmapBuildShader, voxelizeKernel, SurfaceId, layer.SurfaceBuffer);
            cmd.SetComputeBufferParam(clipmapBuildShader, voxelizeKernel, SurfaceUvId, layer.SurfaceUvBuffer);
            cmd.SetComputeBufferParam(clipmapBuildShader, voxelizeKernel, DistanceId, layer.DistanceBuffer);

            IReadOnlyList<GIGpuInstanceData> instances = scene.CpuInstances;
            IReadOnlyList<GIGpuGeometryStreamData> ranges = geometryCache.Ranges;
            for (int instanceIndex = 0; instanceIndex < instances.Count; instanceIndex++)
            {
                GIGpuInstanceData instance = instances[instanceIndex];
                bool isDynamic = ((GIInstanceFlags)instance.flags & GIInstanceFlags.Dynamic) != 0;
                if (isDynamic != dynamic || instance.geometryIndex >= ranges.Count)
                    continue;
                if (!SphereTouchesDirtyBrick(instance.worldBoundingSphere, layer))
                    continue;
                uint triangleCount = ranges[(int)instance.geometryIndex].triangleCount;
                if (triangleCount == 0)
                    continue;
                cmd.SetComputeIntParam(clipmapBuildShader, InstanceIndexId, instanceIndex);
                const uint maxTrianglesPerDispatch = 65535u * 64u;
                uint triangleBase = 0;
                while (triangleBase < triangleCount)
                {
                    uint batchCount = Math.Min(maxTrianglesPerDispatch, triangleCount - triangleBase);
                    cmd.SetComputeIntParam(clipmapBuildShader, TriangleBaseId, (int)triangleBase);
                    cmd.DispatchCompute(clipmapBuildShader, voxelizeKernel, Mathf.CeilToInt(batchCount / 64f), 1, 1);
                    triangleBase += batchCount;
                }
            }
        }

        void BuildDistance(CommandBuffer cmd, GIClipmapLayer layer)
        {
            if (layer.DirtyBrickCount <= 0)
                return;
            cmd.SetComputeIntParam(clipmapBuildShader, DirtyBrickCountId, layer.DirtyBrickCount);
            cmd.SetComputeBufferParam(clipmapBuildShader, initializeDistanceKernel, DirtyBricksId, layer.DirtyBrickBuffer);
            cmd.SetComputeBufferParam(clipmapBuildShader, initializeDistanceKernel, OccupancyId, layer.OccupancyBuffer);
            cmd.SetComputeBufferParam(clipmapBuildShader, initializeDistanceKernel, DistanceId, layer.DistanceBuffer);
            cmd.DispatchCompute(clipmapBuildShader, initializeDistanceKernel, layer.DirtyBrickCount, 1, 1);

            cmd.SetComputeIntParam(clipmapBuildShader, PropagationStepId, 1);
            cmd.SetComputeBufferParam(clipmapBuildShader, propagateDistanceKernel, DirtyBricksId, layer.DirtyBrickBuffer);
            cmd.SetComputeBufferParam(clipmapBuildShader, propagateDistanceKernel, DistanceId, layer.DistanceBuffer);
            int passCount = Mathf.Clamp(distancePropagationPasses, 1, 7);
            for (int pass = 0; pass < passCount; pass++)
                cmd.DispatchCompute(clipmapBuildShader, propagateDistanceKernel, layer.DirtyBrickCount, 1, 1);
        }

        void UpdateRadiance(
            CommandBuffer cmd,
            GISceneGpuView view,
            GIClipmapLayer layer,
            Vector3 lightDirection,
            Color lightColor,
            Color skyColor)
        {
            if (layer.RadianceDirtyCount <= 0)
                return;
            cmd.SetComputeIntParam(radianceCacheShader, RadianceDirtyBrickCountId, layer.RadianceDirtyCount);
            cmd.SetComputeIntParam(radianceCacheShader, MaterialCountId, view.materialCount);
            cmd.SetComputeIntParam(radianceCacheShader, TextureSliceCountId,
                materialTextureCache != null ? materialTextureCache.SliceCount : 0);
            cmd.SetComputeIntParam(radianceCacheShader, EnableRadianceShadowsId, enableRadianceShadows ? 1 : 0);
            cmd.SetComputeIntParam(radianceCacheShader, LocalLightCountId, activeLocalLightCount);
            cmd.SetComputeIntParam(radianceCacheShader, MaxShadowedLocalLightsId,
                Mathf.Clamp(maxShadowedLocalLightsPerSurface, 0, 8));
            cmd.SetComputeIntParam(radianceCacheShader, SecondaryBounceRaysId,
                Mathf.Clamp(secondaryBounceRays, 0, 4));
            cmd.SetComputeIntParam(radianceCacheShader, RadianceFrameIndexId, generation);
            cmd.SetComputeIntParam(radianceCacheShader, TargetLayerId, layer == staticLayer ? 0 : 1);
            cmd.SetComputeFloatParam(radianceCacheShader, RadianceShadowDistanceId, radianceShadowDistance);
            cmd.SetComputeFloatParam(radianceCacheShader, SecondaryBounceIntensityId,
                Mathf.Clamp01(secondaryBounceIntensity));
            cmd.SetComputeFloatParam(radianceCacheShader, RadianceHistoryWeightId,
                Mathf.Clamp(radianceHistoryWeight, 0f, 0.95f));
            cmd.SetComputeFloatParam(radianceCacheShader, RadianceClampId,
                Mathf.Max(0.1f, radianceClamp));
            cmd.SetComputeVectorParam(radianceCacheShader, MainLightDirectionId, lightDirection);
            cmd.SetComputeVectorParam(radianceCacheShader, MainLightColorId,
                new Vector4(lightColor.r, lightColor.g, lightColor.b, 0f));
            cmd.SetComputeVectorParam(radianceCacheShader, SkyColorId,
                new Vector4(skyColor.r, skyColor.g, skyColor.b, 0f));
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
                TargetRadianceScratchId, radianceScratchBuffer);
            cmd.SetComputeBufferParam(radianceCacheShader, updateRadianceKernel,
                TargetValidityScratchId, validityScratchBuffer);
            cmd.SetComputeBufferParam(radianceCacheShader, updateRadianceKernel,
                MaterialsId, view.materials);
            cmd.SetComputeBufferParam(radianceCacheShader, updateRadianceKernel,
                LocalLightsId, localLightBuffer);
            cmd.SetComputeTextureParam(radianceCacheShader, updateRadianceKernel,
                BaseColorTexturesId, materialTextureCache.BaseColorArray);
            cmd.SetComputeTextureParam(radianceCacheShader, updateRadianceKernel,
                EmissionTexturesId, materialTextureCache.EmissionArray);
            cmd.SetComputeBufferParam(radianceCacheShader, updateRadianceKernel,
                LevelsId, levelDataBuffer);
            cmd.SetComputeBufferParam(radianceCacheShader, updateRadianceKernel,
                StaticPageTableId, staticLayer.PageTableBuffer);
            cmd.SetComputeBufferParam(radianceCacheShader, updateRadianceKernel,
                StaticOccupancyId, staticLayer.OccupancyBuffer);
            cmd.SetComputeBufferParam(radianceCacheShader, updateRadianceKernel,
                StaticSurfaceId, staticLayer.SurfaceBuffer);
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
                DynamicDistanceId, dynamicLayer.DistanceBuffer);
            cmd.SetComputeBufferParam(radianceCacheShader, updateRadianceKernel,
                DynamicRadianceId, dynamicLayer.RadianceBuffer);
            cmd.SetComputeBufferParam(radianceCacheShader, updateRadianceKernel,
                DynamicValidityId, dynamicLayer.ValidityBuffer);
            cmd.DispatchCompute(radianceCacheShader, updateRadianceKernel,
                layer.RadianceDirtyCount, 1, 1);

            cmd.SetComputeIntParam(radianceCacheShader, RadianceDirtyBrickCountId,
                layer.RadianceDirtyCount);
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
                layer.RadianceDirtyCount, 1, 1);
        }

        int ComputeLightingSignature(Vector3 lightDirection, Color lightColor, Color skyColor)
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + lightDirection.GetHashCode();
                hash = hash * 31 + lightColor.GetHashCode();
                hash = hash * 31 + skyColor.GetHashCode();
                hash = hash * 31 + secondaryBounceRays;
                hash = hash * 31 + secondaryBounceIntensity.GetHashCode();
                hash = hash * 31 + radianceShadowDistance.GetHashCode();
                hash = hash * 31 + (enableRadianceShadows ? 1 : 0);
                hash = hash * 31 + maxShadowedLocalLightsPerSurface;
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

        bool SphereTouchesDirtyBrick(Vector4 sphere, GIClipmapLayer layer)
        {
            Vector3 center = new Vector3(sphere.x, sphere.y, sphere.z);
            float radius = Mathf.Max(0.01f, sphere.w);
            for (int level = 0; level < GIClipmapConstants.LevelCount; level++)
            {
                float brickWorldSize = GIClipmapConstants.CellSizes[level] * GIClipmapConstants.BrickSize;
                Vector3Int min = FloorToBrick(center - Vector3.one * radius, brickWorldSize);
                Vector3Int max = FloorToBrick(center + Vector3.one * radius, brickWorldSize);
                for (int z = min.z; z <= max.z; z++)
                for (int y = min.y; y <= max.y; y++)
                for (int x = min.x; x <= max.x; x++)
                {
                    if (layer.IsDirty(new GIClipmapBrickKey(level, new Vector3Int(x, y, z))))
                        return true;
                }
            }
            return false;
        }

        float EstimatePoolMiB()
        {
            long bytesPerBrick =
                GIClipmapConstants.OccupancyWordsPerBrick * 4L +
                GIClipmapConstants.SurfaceWordsPerBrick * 4L +
                GIClipmapConstants.SurfaceUvWordsPerBrick * 4L +
                GIClipmapConstants.DistanceWordsPerBrick * 4L +
                GIClipmapConstants.RadianceWordsPerBrick * 4L +
                GIClipmapConstants.ValidityWordsPerBrick * 4L +
                GIClipmapConstants.BrickDataStride;
            long bytes = bytesPerBrick * (staticBrickCapacity + dynamicBrickCapacity) +
                         GIClipmapConstants.PageTableEntries * 4L * 2L +
                         GIClipmapConstants.LevelCount * GIClipmapConstants.LevelStride +
                         Math.Max(staticBrickCapacity, dynamicBrickCapacity) *
                         (GIClipmapConstants.RadianceWordsPerBrick +
                          GIClipmapConstants.ValidityWordsPerBrick) * 4L +
                         Math.Max(1, maxLocalLights) * GILightAbi.LocalLightStride;
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
            view = new GIClipmapGpuView(levelDataBuffer, staticLayer, dynamicLayer, generation);
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
            geometryCache?.Dispose();
            materialTextureCache?.Dispose();
            staticLayer?.Dispose();
            dynamicLayer?.Dispose();
            levelDataBuffer?.Release();
            localLightBuffer?.Release();
            radianceScratchBuffer?.Release();
            validityScratchBuffer?.Release();
            geometryCache = null;
            materialTextureCache = null;
            staticLayer = null;
            dynamicLayer = null;
            levelDataBuffer = null;
            localLightBuffer = null;
            radianceScratchBuffer = null;
            validityScratchBuffer = null;
            initialized = false;
            originsInitialized = false;
            lastUpdateFrame = -1;
            previousStaticInstances.Clear();
            observedStaticInstances.Clear();
            removedStaticInstances.Clear();
            invalidatedStaticBricks.Clear();
            lightingSignature = 0;
            allocatedStaticCapacity = 0;
            allocatedDynamicCapacity = 0;
            localLightCapacity = 0;
            radianceScratchCapacity = 0;
            validityScratchCapacity = 0;
            activeLocalLightCount = 0;
            remainingStaticBounceSweeps = 0;
        }

        readonly struct StaticInstanceState
        {
            public readonly int signature;
            public readonly Vector4 sphere;

            public StaticInstanceState(int signature, Vector4 sphere)
            {
                this.signature = signature;
                this.sphere = sphere;
            }
        }
    }
}
