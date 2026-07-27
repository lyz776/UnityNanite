using System;
using System.Collections.Generic;
using UnityEngine;
using Unity.Profiling;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Nanite
{
    /// <summary>
    /// Nanite Runtime 双阶段 HZB 剔除接入（RendererFeature 版）：
    /// PassA: 上一帧 HZB 粗剔
    /// PassB: 用 PassA 候选写当前帧 Nanite 深度
    /// PassC: 当前帧深度构建 HZB
    /// PassD: 当前帧 HZB second-chance 复测并合并最终可见集合
    /// PassE: VBuffer 预览（可选）
    /// </summary>
    public class NaniteRendererFeature : ScriptableRendererFeature
    {
        static readonly ProfilerMarker kFirstCullMarker = new ProfilerMarker("Nanite.CPU.FirstCull");
        static readonly ProfilerMarker kScenePrepareMarker = new ProfilerMarker("Nanite.CPU.ScenePrepare");
        static readonly ProfilerMarker kResolveSubmitMarker = new ProfilerMarker("Nanite.CPU.ResolveSubmit");
        static readonly ProfilerMarker kShadowSubmitMarker = new ProfilerMarker("Nanite.CPU.ShadowSubmit");
        static readonly ProfilerMarker kShadowCullPrepareMarker = new ProfilerMarker("Nanite.CPU.ShadowCullPrepare");
        static readonly ProfilerMarker kShadowCullRecordMarker = new ProfilerMarker("Nanite.CPU.ShadowCullRecord");
        enum FormalRasterizationMode
        {
            HardwareOnly = 0,
            HybridReserved = 1
        }

        enum PassKind
        {
            FirstCull,
            WriteDepth,
            WriteShadow,
            BuildHzb,
            SecondCull,
            DrawVBufferPreview,
            DrawDebugVisualization,
            FormalVisibility,
            PageRetirementFence
        }

        public enum DebugVisualizationMode
        {
            Cluster = 0,
            Triangle = 1,
            Page = 2
        }

        [System.Serializable]
        public class Settings
        {
            public bool enable = true;
            [Tooltip("SecondCull 启用本帧 HZB 遮挡剔除。移动相机时若仍破洞，先关掉此项验证。")]
            public bool useHzbCulling = false;
            [Tooltip("按场景 virtual cluster 规模自动准入 HZB。小场景跳过 Depth/Copy/HZB/Cull2 的固定成本。关闭后 useHzbCulling=true 表示始终启用。")]
            public bool adaptiveHzbCulling = true;
            [Tooltip("自动 HZB 的最小 virtual cluster 数。低于此值只执行 GPU 视锥/LOD；0 表示不设门槛。")]
            [Min(0)] public int hzbMinClusterCount = 50000;
            [Tooltip("FirstCull 使用上一帧 HZB（推进相机时极易误杀→近距破洞/闪烁）。默认关闭。")]
            public bool usePreviousHzbOnFirstCull = false;
            [Tooltip("调试/兼容回退：允许 CPU BVH 生成 cluster 候选。正式 GPU-driven 配置应关闭。")]
            public bool useBvhCandidates = true;
            [Tooltip("CPU BVH 允许的最大实例数。0 表示始终使用 GPU Instance -> Part -> Cluster；正式配置默认 0。")]
            [Min(0)] public int cpuBvhMaxInstances = 0;
            [Tooltip("虚拟 Part 达到该数量后，使用可见 Part append + 间接 Cluster 派发，避免再次扫描被 Instance/Part 剔除的工作。")]
            [Min(1)] public int gpuPartQueueMinVirtualParts = 4096;
            [Tooltip("实例达到该数量后，InstanceCull 将可见实例写入队列，每个可见实例由一个 64-lane group 间接展开其 Part。小场景保留直接 PartCull。")]
            [Min(1)] public int gpuInstanceQueueMinInstances = 64;
            [Tooltip("Shadow cascade 达到该 virtual Part 数量后才启用可见 Part append + 间接 Cluster 派发。阴影每帧最多执行四次，小场景需要更高的准入门槛。")]
            [Min(1)] public int shadowPartQueueMinVirtualParts = 32768;
            [Tooltip("Shadow cascade 达到该实例数后才启用可见 Instance 队列。低于此值直接扫描 Part，避免每个 cascade 的 CopyCount/indirect 固定成本。")]
            [Min(1)] public int shadowInstanceQueueMinInstances = 64;
            [Header("Hybrid Admission")]
            [Tooltip("低复杂度原始 Mesh 自动交回普通 URP，由 SRP Batcher/GPU Instancing 处理，避免 VBuffer 固定开销造成负优化。")]
            public bool enableSmallMeshRasterFallback = true;
            [Tooltip("原始三角形数不高于该值时优先普通 URP。Proxy.forceNaniteRendering 可逐对象强制进入 Nanite。")]
            [Min(0)] public int smallMeshTriangleThreshold = 2048;
            [Tooltip("Bevy 式双阶段编排。无 HZB 时不启用 prevVisible 滤波（避免 LOD 切换破洞）；有 HZB 时 Pass1=prev、Pass2=补洞。")]
            public bool enableBevyTwoPhaseOcclusion = true;
            [Tooltip("仅当 useHzbCulling=true 时用上一帧可见做 Pass1 滤波。无 HZB 时强制关闭（否则必破洞）。")]
            public bool enablePrevVisiblePass1Filter = true;
            [Tooltip("Formal 双次 VisBuffer 光栅。默认关：多一次 Compact+Raster，易破洞且更慢；合并 mask 单次光栅即可。")]
            public bool enableBevyFormalDualRaster = false;
            [Tooltip("无 HZB 需求时跳过独立 WriteDepth（由 Formal Raster 写深度）。")]
            public bool skipWriteDepthWhenNoHzb = true;
            [Tooltip(">0 时覆盖所有 Proxy 的 lodErrorPixels。\n" +
                     "更小→近处更细、远处更晚才退化；更大→整体更糙、cluster 掉得更快。\n" +
                     "要看近处精细请用 1~2，不要用 8。0=用 Proxy 自身值。")]
            [Min(0f)] public float overrideLodErrorPixels = 0f;
            public bool logStats = false;
            public bool enableVBufferPreview = false;
            [Tooltip("仅限制 VBuffer 预览绘制数量，不影响 culling 结果。<=0 表示不限制。")]
            [Min(-1)] public int vbufferPreviewMaxPackets = 2048;
            [Header("Debug Visualization")]
            [Tooltip("直接在画面上把当前可见 Nanite 几何按 Cluster/Triangle/Page 伪彩色预览。")]
            public bool enableDebugVisualization = false;
            [Tooltip("Cluster：每块 cluster 异色；Triangle：每个三角形异色；Page：每个 streaming page 异色。")]
            public DebugVisualizationMode debugVisualizationMode = DebugVisualizationMode.Cluster;
            [Tooltip("Debug 可视化绘制时机（建议 AfterRenderingOpaques）。")]
            public RenderPassEvent debugVisualizationEvent = RenderPassEvent.AfterRenderingOpaques;
            [Header("Formal VisibilityBuffer")]
            [Tooltip("正式 Formal VBuffer 链路（实验中，默认关闭）。")]
            public bool enableFormalVisibilityBuffer = false;
            [Tooltip("实验总开关：避免意外开启导致性能骤降。")]
            public bool enableFormalVisibilityBufferExperimentalGate = false;
            [Tooltip("Formal VBuffer 使用 R32G32_UINT 保存完整 instance/triangle ID，带宽为旧 RGBA32F 的一半；不支持整数 RT 的平台自动回退。")]
            public bool enableCompactFormalVBuffer = true;
            [Tooltip("Tile 分类 early-out（需配合 resolve 使用）。未修好前默认关闭，避免纯浪费 ~0.7ms。")]
            public bool enableMaterialTileCulling = false;
            [Tooltip("Only register the tile classify pass when enough distinct materials can amortize its fixed dispatch cost.")]
            public bool adaptiveMaterialTileCulling = true;
            [Tooltip("Minimum scene material count for adaptive tile classification. One or two materials are normally cheaper as direct fullscreen resolves.")]
            [Min(1)] public int materialTileMinMaterialCount = 3;
            [Tooltip("实例未移动时刷新 Light Probe SH 的间隔。0=只在移动时刷新；大场景建议 30~120。")]
            [Min(0)] public int lightProbeRefreshInterval = 30;
            [Header("GPU Page Pool")]
            [Tooltip("Use NPG1/NZC1 for storage, then transcode each resident Page once into the aligned GPU geometry cache. Admission currently requires the complete Page set to fit; compatibility geometry remains the fallback.")]
            public bool enablePackedPageRaster = true;
            [Tooltip("Diagnostic only: compile and select the direct NPG1 Page decode variant. Production keeps this disabled and reads only the resident geometry cache.")]
            public bool enablePackedDirectDiagnostic = false;
            [Tooltip("One-time GPU transcode from resident NPG1 Pages into aligned draw-time geometry. Required by the production packed Page path.")]
            public ComputeShader pageTranscodeShader;
            [Tooltip("允许异步读回 Page 请求并上传非 root Page。Packed Page 尚未直接供光栅消费时应保持关闭，避免上传重复几何的纯开销。")]
            public bool enablePageStreamingUploads = false;
            [Tooltip("Packed Page Pool hard budget. The runtime allocates only enough 256 KiB slots for the current scene up to this cap; root pages are permanently pinned.")]
            [Min(1)] public int pagePoolMaxMiB = 128;
            [Tooltip("Frames between asynchronous GPU Page request readbacks. No synchronous GetData is used.")]
            [Min(1)] public int pageRequestReadbackInterval = 3;
            [Tooltip("Maximum packed Pages uploaded after one request-mask readback.")]
            [Min(1)] public int pageUploadsPerPoll = 8;
            [Range(4, 16)] public int materialTileSize = 8;
            [Tooltip("HZB 半分辨率构建，显著降低 CopyDepth + mip 链带宽。")]
            public bool hzbHalfResolution = true;
            [Tooltip("HZB mip 链在最短边降到此尺寸后停止（不必建到 1x1）。")]
            [Range(1, 32)] public int hzbMinMipSize = 8;
            [Tooltip("Formal VBuffer 半分辨率光栅（实验，默认关）。开启后 Resolve 从半分辨率 ID 缓冲采样，边缘可能锯齿。")]
            public bool formalVBufferHalfResolution = false;
            [Tooltip("全屏 DepthFill。全分辨率时 Raster 已写深度且 Merge 写 Stencil，默认关闭以省一次全屏 Pass。")]
            public bool enableFormalDepthFill = false;
            [Tooltip("软/硬光栅统一入口。当前仅硬光栅可用，Hybrid 为预留。")]
            public int formalRasterizationMode = (int)FormalRasterizationMode.HardwareOnly;
            public ComputeShader hzbBuilderShader;
            [Tooltip("全局批量剔除 Compute；留空则使用第一个启用 GPU Culling 的 Proxy 上的 shader。")]
            public ComputeShader gpuCullingShader;
            public ComputeShader materialTileClassifyShader;
            [Tooltip("MVP：在兼容路径下把 VBuffer/Depth 预览绘制切到 DrawProceduralIndirect。")]
            public bool enableIndirectPreviewDraw = true;
            [Tooltip("WriteDepth 优先走场景级单次间接绘制；失败时回退 per-proxy。")]
            public bool enableSceneIndirectDepthWrite = true;
            [Tooltip("Nanite 几何通过 URP native atlas hook 写入主光 ShadowMap。")]
            public bool enableShadowCasting = true;
            [Tooltip("Nanite 写入的主光级联数。正式配置必须与 URP cascade 数一致；降低该值会截断远级阴影，仅可用于显式性能诊断。")]
            [Range(1, 4)] public int maxNaniteShadowCascades = 4;
            [Header("Visible Triangle Compact")]
            [Tooltip("GPU 压实可见三角形后再 DrawIndirect，避免全量三角进 VS。")]
            public bool enableVisibleTriangleCompact = true;
            public ComputeShader visibleTriangleCompactShader;
            [Tooltip("把 visible-cluster queue 展开为临时硬件索引缓冲，利用 post-transform vertex cache。超出预算的视图在 GPU 上自动回退 procedural。")]
            public bool enableIndexedClusterRaster = true;
            [Tooltip("主视图复用全部预算；四级阴影各使用四分之一预算。默认 128 MiB 可覆盖当前 12 车基准。")]
            [Range(16, 512)] public int indexedClusterBufferMaxMiB = 128;
            public Material vbufferPreviewMaterial;
            public Material vbufferDecodeMaterial;
            public Material vbufferLitResolveMaterial;
            [Tooltip("仅约束 BuildHzb 不早于 WriteDepth，不修改 WriteDepth 及其他 Pass 的事件。")]
            public bool enforcePassOrdering = true;
            public RenderPassEvent firstCullEvent = RenderPassEvent.BeforeRenderingShadows;
            public RenderPassEvent writeDepthEvent = RenderPassEvent.BeforeRenderingPrePasses;
            public RenderPassEvent buildHzbEvent = RenderPassEvent.AfterRenderingPrePasses;
            [Tooltip("在 buildHzbEvent 所选队列内向后偏移（URP 支持 RenderPassEvent + offset，0~9 安全）。")]
            [Range(0, 9)] public int buildHzbQueueOffset = 4;
            public RenderPassEvent secondCullEvent = RenderPassEvent.BeforeRenderingOpaques;
            public RenderPassEvent vbufferPreviewEvent = RenderPassEvent.AfterRenderingOpaques;
            public RenderPassEvent formalVBufferEvent = RenderPassEvent.AfterRenderingGbuffer;
            [Range(0, 9)] public int formalVBufferQueueOffset = 1;
        }

        class CameraHzbState
        {
            public RenderTexture previous;
            public RenderTexture current;
            public RenderTexture depthSample;
            public int width;
            public int height;
            public int mipCount;
            public bool hasPrevious;
            public int builtFrame = -1;
            public bool depthWrittenThisFrame;
            public bool depthCopiedForHzb;
            public bool currentHzbValid;
        }

        class NaniteFeaturePass : ScriptableRenderPass
        {
            readonly NaniteRendererFeature owner;
            readonly PassKind kind;

            public NaniteFeaturePass(NaniteRendererFeature owner, PassKind kind, RenderPassEvent evt)
            {
                this.owner = owner;
                this.kind = kind;
                renderPassEvent = evt;
                profilingSampler = new ProfilingSampler($"Nanite/{kind}");
            }

            public override void Configure(CommandBuffer cmd, RenderTextureDescriptor cameraTextureDescriptor)
            {
                if (kind == PassKind.WriteDepth ||
                    kind == PassKind.BuildHzb ||
                    kind == PassKind.FormalVisibility ||
                    kind == PassKind.DrawDebugVisualization)
                    ConfigureInput(ScriptableRenderPassInput.Depth);
            }

            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                switch (kind)
                {
                    case PassKind.FirstCull:
                        owner.ExecuteFirstCull(renderingData.cameraData.camera);
                        break;
                    case PassKind.WriteDepth:
                        owner.ExecuteWriteDepth(context, renderingData.cameraData.camera);
                        break;
                    case PassKind.WriteShadow:
                        owner.ExecuteWriteShadow(context, ref renderingData);
                        break;
                    case PassKind.BuildHzb:
                        owner.ExecuteBuildHzb(context, renderingData.cameraData.camera);
                        break;
                    case PassKind.SecondCull:
                        owner.ExecuteSecondCull(renderingData.cameraData.camera);
                        break;
                    case PassKind.DrawVBufferPreview:
                        owner.ExecuteVBufferPreview(context, renderingData.cameraData.camera);
                        break;
                    case PassKind.DrawDebugVisualization:
                        owner.ExecuteDebugVisualization(context, renderingData.cameraData.camera);
                        break;
                    case PassKind.FormalVisibility:
                        owner.ExecuteFormalVisibility(context, renderingData.cameraData.camera);
                        break;
                    case PassKind.PageRetirementFence:
                        owner.ExecutePageRetirementFence(context);
                        break;
                }
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                owner.RecordRenderGraphPass(renderGraph, frameData, kind, $"Nanite/{kind}", profilingSampler);
            }
        }

        static class ShaderIds
        {
            public static readonly int CameraDepthTexture = Shader.PropertyToID("_CameraDepthTexture");
            public static readonly int CameraDepthAttachment = Shader.PropertyToID("_CameraDepthAttachment");
            public static readonly int ZWrite = Shader.PropertyToID("_ZWrite");
            public static readonly int SourceDepth = Shader.PropertyToID("_SourceDepth");
            public static readonly int SourceMip = Shader.PropertyToID("_SourceMip");
            public static readonly int DestMip = Shader.PropertyToID("_DestMip");
            public static readonly int SourceSize = Shader.PropertyToID("_SourceSize");
            public static readonly int VertexData = Shader.PropertyToID("_VertexData");
            public static readonly int Indices = Shader.PropertyToID("_Indices");
            public static readonly int VertexStride = Shader.PropertyToID("_VertexStride");
            public static readonly int InstanceId = Shader.PropertyToID("_InstanceId");
            public static readonly int LocalToWorld = Shader.PropertyToID("_LocalToWorld");
            public static readonly int NaniteVBufferTex = Shader.PropertyToID("_NaniteVBufferTex");
            public static readonly int TriangleCluster = Shader.PropertyToID("_TriangleCluster");
            public static readonly int TrianglePage = Shader.PropertyToID("_TrianglePage");
            public static readonly int TrianglePageRefs = Shader.PropertyToID("_TrianglePageRefs");
            public static readonly int PackedPagePool = Shader.PropertyToID("_NanitePackedPagePool");
            public static readonly int PageDecodeTable = Shader.PropertyToID("_NanitePageDecodeTable");
            public static readonly int ResidentPageTable = Shader.PropertyToID("_NaniteResidentPageTable");
            public static readonly int ResidentVertices = Shader.PropertyToID("_NaniteResidentVertices");
            public static readonly int ResidentIndices = Shader.PropertyToID("_NaniteResidentIndices");
            public static readonly int UsePackedPageGeometry = Shader.PropertyToID("_UsePackedPageGeometry");
            public static readonly int PackedDirectDiagnostic = Shader.PropertyToID("_NanitePackedDirectDiagnostic");
            public static readonly int TriangleSubMesh = Shader.PropertyToID("_TriangleSubMesh");
            public static readonly int TriangleInstance = Shader.PropertyToID("_TriangleInstance");
            public static readonly int ClusterVisible = Shader.PropertyToID("_ClusterVisible");
            public static readonly int InstanceLocalToWorld = Shader.PropertyToID("_InstanceLocalToWorld");
            public static readonly int InstanceSubMeshMaterial = Shader.PropertyToID("_InstanceSubMeshMaterial");
            public static readonly int InstanceSh = Shader.PropertyToID("_InstanceSH");
            public static readonly int MaxSubMeshCount = Shader.PropertyToID("_MaxSubMeshCount");
            public static readonly int UseSceneInstanceBuffer = Shader.PropertyToID("_UseSceneInstanceBuffer");
            public static readonly int HasTriangleSubMesh = Shader.PropertyToID("_HasTriangleSubMesh");
            public static readonly int UseNormalizedIds = Shader.PropertyToID("_UseNormalizedIds");
            public static readonly int UseTileMaterialMask = Shader.PropertyToID("_UseTileMaterialMask");
            public static readonly int UseCompactedTriIds = Shader.PropertyToID("_UseCompactedTriIds");
            public static readonly int CompactedTriIds = Shader.PropertyToID("_CompactedTriIds");
            public static readonly int CompactedTriInstances = Shader.PropertyToID("_CompactedTriInstances");
            public static readonly int CompactedTriCounts = Shader.PropertyToID("_CompactedTriCounts");
            public static readonly int CompactedDrawClusters = Shader.PropertyToID("_CompactedDrawClusters");
            public static readonly int UseDirectVisibleDrawQueue = Shader.PropertyToID("_UseDirectVisibleDrawQueue");
            public static readonly int CompactedClusterTriangleSlots = Shader.PropertyToID("_CompactedClusterTriangleSlots");
            public static readonly int DebugVizMode = Shader.PropertyToID("_DebugVizMode");
            public static readonly int TileMaterialMask = Shader.PropertyToID("_TileMaterialMask");
            public static readonly int ResolveMaterialId = Shader.PropertyToID("_ResolveMaterialId");
            public static readonly int TileCountX = Shader.PropertyToID("_TileCountX");
            public static readonly int TileCountY = Shader.PropertyToID("_TileCountY");
            public static readonly int TileCount = Shader.PropertyToID("_TileCount");
            public static readonly int TileSize = Shader.PropertyToID("_TileSize");
            public static readonly int TriangleCount = Shader.PropertyToID("_TriangleCount");
            public static readonly int InstanceCount = Shader.PropertyToID("_InstanceCount");
            public static readonly int MaterialCount = Shader.PropertyToID("_MaterialCount");
            public static readonly int ScreenSize = Shader.PropertyToID("_ScreenSize");
            public static readonly int NaniteViewInvSize = Shader.PropertyToID("_NaniteViewInvSize");
            public static readonly int NaniteVBufferSize = Shader.PropertyToID("_NaniteVBufferSize");
            public static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
            public static readonly int EmissionColor = Shader.PropertyToID("_EmissionColor");
            public static readonly int Cutoff = Shader.PropertyToID("_Cutoff");
            public static readonly int AlphaClip = Shader.PropertyToID("_AlphaClip");
            public static readonly int Smoothness = Shader.PropertyToID("_Smoothness");
            public static readonly int Metallic = Shader.PropertyToID("_Metallic");
            public static readonly int BumpScale = Shader.PropertyToID("_BumpScale");
            public static readonly int OcclusionStrength = Shader.PropertyToID("_OcclusionStrength");
            public static readonly int HasBaseMap = Shader.PropertyToID("_HasBaseMap");
            public static readonly int HasNormalMap = Shader.PropertyToID("_HasNormalMap");
            public static readonly int HasMetallicGlossMap = Shader.PropertyToID("_HasMetallicGlossMap");
            public static readonly int HasOcclusionMap = Shader.PropertyToID("_HasOcclusionMap");
            public static readonly int HasEmissionMap = Shader.PropertyToID("_HasEmissionMap");
            public static readonly int EmissionEnabled = Shader.PropertyToID("_EmissionEnabled");
            public static readonly int SmoothnessFromAlbedoAlpha = Shader.PropertyToID("_SmoothnessFromAlbedoAlpha");
            public static readonly int BaseMapST = Shader.PropertyToID("_BaseMap_ST");
            public static readonly int BaseMap = Shader.PropertyToID("_BaseMap");
            public static readonly int BumpMap = Shader.PropertyToID("_BumpMap");
            public static readonly int MetallicGlossMap = Shader.PropertyToID("_MetallicGlossMap");
            public static readonly int OcclusionMap = Shader.PropertyToID("_OcclusionMap");
            public static readonly int EmissionMap = Shader.PropertyToID("_EmissionMap");
            public static readonly int ShadowBias = Shader.PropertyToID("_ShadowBias");
            public static readonly int LightDirection = Shader.PropertyToID("_LightDirection");
            public static readonly int LightPosition = Shader.PropertyToID("_LightPosition");
            public static readonly int NaniteShadowViewProj = Shader.PropertyToID("_NaniteShadowViewProj");
            public static readonly int UseIndexedClusterRaster = Shader.PropertyToID("_UseIndexedClusterRaster");
            public static readonly int GeometryVertexCount = Shader.PropertyToID("_GeometryVertexCount");
        }

        public Settings settings = new Settings();

        NaniteFeaturePass passFirstCull;
        NaniteFeaturePass passWriteDepth;
        NaniteFeaturePass passWriteShadow;
        NaniteFeaturePass passBuildHzb;
        NaniteFeaturePass passSecondCull;
        NaniteFeaturePass passVBufferPreview;
        NaniteFeaturePass passDebugVisualization;
        NaniteFeaturePass passFormalVisibility;
        NaniteFeaturePass passPageRetirementFence;

        const string kPassDebugColorViz = "DebugColorViz";
        const int kDebugColorVizPassFallback = 2;
        const string kPackedDirectDiagnosticKeyword = "NANITE_PACKED_DIRECT_DIAGNOSTIC";

        int kernelCopyDepth = -1;
        int kernelCopyDepthHalf = -1;
        int kernelDownsample = -1;
        int kernelCompactClear = -1;
        int kernelCompactTris = -1;
        int kernelCompactFinalize = -1;
        int kernelCompactFinalizeVisibleQueue = -1;
        int kernelCompactFinalizeShadowQueues = -1;
        int kernelPrepareIndexedDrawQueue = -1;
        int kernelBuildIndexedDrawQueue = -1;

        const int kCompactSelectionFirst = 1;
        const int kCompactSelectionMerged = 2;
        const int kCompactSelectionPass2 = 3;
        int lastCompactFrame = -1;
        int lastCompactSelectionKey;
        int lastCompactGeometryGeneration = -1;
        bool lastCompactSucceeded;
        bool lastCompactUsedDirectQueue;
        int lastIndexedDrawFrame = -1;
        int lastIndexedDrawSelectionKey;
        int lastIndexedDrawGeometryGeneration = -1;
        bool loggedCompactStatsOnce;
        bool loggedExecutionOnce;
        bool loggedVBufferOnce;
        bool loggedDebugVizOnce;
        Material runtimeVBufferPreviewMaterial;
        Material runtimeVBufferDecodeMaterial;
        Material runtimeDepthWriteMaterial;
        Material runtimeShadowCasterMaterial;
        Material runtimeVBufferLitResolveMaterial;
        Material runtimeCopyDepthMaterial;
        bool loggedFormalRasterStats;
        TextureHandle recordedHzbDepthSource;
        bool loggedPassOrderWarning;
        bool loggedFormalOrderWarning;
        bool loggedDispatchStats;
        bool loggedBatchedDispatchStats;
        bool loggedFormalOnce;
        bool loggedFormalDebugMeshWarning;
        bool loggedHybridRasterWarning;
        bool loggedFormalGateWarning;
        bool loggedFormalSkipReason;
        bool loggedFormalRecordSuccess;
        NaniteGpuBatchedCullingBackend batchedCulling;
        NaniteSceneVisibilityBufferBackend sceneVisibilityBackend;
        MaterialPropertyBlock vbufferMpb;
        int perfSampleCount;
        double perfFirstCullCpuMsAccum;
        double perfSecondCullCpuMsAccum;
        int perfFirstCullDispatchAccum;
        int perfSecondCullDispatchAccum;
        int perfFirstCullReadbackAccum;
        int perfSecondCullReadbackAccum;
        int perfFormalSampleCount;
        int perfVisibilityDrawAccum;
        int perfResolveDrawAccum;
        int perfMaterialBatchAccum;
        double perfTileCoverageAccum;
        int perfTileCoverageSampleCount;
        Dictionary<int, CameraHzbState> cameraHzb = new Dictionary<int, CameraHzbState>();
        Dictionary<int, NaniteRuntimeSelection> firstSelections = new Dictionary<int, NaniteRuntimeSelection>();
        Dictionary<int, NaniteRuntimeSelection> secondSelections = new Dictionary<int, NaniteRuntimeSelection>();
        Dictionary<int, NaniteRuntimeSelection> mergedSelections = new Dictionary<int, NaniteRuntimeSelection>();
        Dictionary<int, List<NaniteVisibleClusterRef>> mergedVisibleScratch = new Dictionary<int, List<NaniteVisibleClusterRef>>();
        Dictionary<int, HashSet<ulong>> mergedKeyScratch = new Dictionary<int, HashSet<ulong>>();
        GraphicsBuffer tileMaterialMaskBuffer;
        GraphicsBuffer tileIndirectArgsBuffer;
        int tileMaskCapacity;
        int tileCountX;
        int tileCountY;
        int tileCount;
        int lastGpuScenePrepareFrame = -1;
        int lastGpuScenePrepareCameraId;
        int lastProxyStatsWritebackFrame = -1;
        int lastPipelineAdmissionFrame = -1;
        int kernelMaterialTileClassify = -1;
        LocalKeyword keywordCompactVBufferClassify;
        bool keywordCompactVBufferClassifyInitialized;
        bool loggedCompactVBufferUnsupported;
        GlobalKeyword keywordDepthMsaa2;
        GlobalKeyword keywordDepthMsaa4;
        GlobalKeyword keywordDepthMsaa8;
        GlobalKeyword keywordOutputDepth;
        bool copyDepthKeywordsInitialized;
        Func<Camera, Light, bool> externalMainLightShadowProvider;
        Action<ExternalMainLightShadowCullBatchContext> externalMainLightShadowCullBatchProvider;
        Action<ExternalMainLightShadowDrawContext> externalMainLightShadowDrawProvider;
        readonly Matrix4x4[] shadowBatchViewMatrices = new Matrix4x4[4];
        readonly Matrix4x4[] shadowBatchProjectionMatrices = new Matrix4x4[4];
        readonly int[] shadowBatchResolutions = new int[4];
        bool loggedExternalShadowSchedulingOnce;
        bool loggedShadowExecutionOnce;
        public override void Create()
        {
            EnsureDefaultSerializedAssets();
            ConfigurePackedDirectDiagnosticKeywords();
            // 序列化资源可能仍勾着实验半分辨率；强制关掉避免花屏。
            settings.formalVBufferHalfResolution = false;
            InitCopyDepthKeywords();
            passFirstCull = new NaniteFeaturePass(this, PassKind.FirstCull, settings.firstCullEvent);
            passWriteDepth = new NaniteFeaturePass(this, PassKind.WriteDepth, settings.writeDepthEvent);
            passWriteShadow = new NaniteFeaturePass(this, PassKind.WriteShadow, RenderPassEvent.AfterRenderingShadows);
            passBuildHzb = new NaniteFeaturePass(this, PassKind.BuildHzb, settings.buildHzbEvent);
            passSecondCull = new NaniteFeaturePass(this, PassKind.SecondCull, settings.secondCullEvent);
            passVBufferPreview = new NaniteFeaturePass(this, PassKind.DrawVBufferPreview, settings.vbufferPreviewEvent);
            passDebugVisualization = new NaniteFeaturePass(this, PassKind.DrawDebugVisualization, settings.debugVisualizationEvent);
            passFormalVisibility = new NaniteFeaturePass(this, PassKind.FormalVisibility, settings.formalVBufferEvent);
            passPageRetirementFence = new NaniteFeaturePass(
                this,
                PassKind.PageRetirementFence,
                RenderPassEvent.AfterRendering);
            SyncPassEvents();
            InitBuilderKernels();
            InitFormalKernels();
            InitCompactKernels();
            EnsureExternalShadowProviderRegistered();
            LogFormalAssetReadiness();
        }

#if UNITY_EDITOR
        void OnValidate()
        {
            EnsureDefaultSerializedAssets();
            ConfigurePackedDirectDiagnosticKeywords();
            settings.formalVBufferHalfResolution = false;
        }
#endif

        void EnsureDefaultSerializedAssets()
        {
#if UNITY_EDITOR
            if (settings.gpuCullingShader == null)
            {
                settings.gpuCullingShader = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                    "Assets/Scripts/Nanite/NaniteRuntimeCulling.compute");
            }

            if (settings.materialTileClassifyShader == null)
            {
                settings.materialTileClassifyShader = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                    "Assets/Scripts/Nanite/NaniteMaterialTileClassify.compute");
            }

            if (settings.visibleTriangleCompactShader == null)
            {
                settings.visibleTriangleCompactShader = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                    "Assets/Scripts/Nanite/NaniteVisibleTriangleCompact.compute");
            }

            if (settings.pageTranscodeShader == null)
            {
                settings.pageTranscodeShader = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                    "Assets/Scripts/Nanite/NanitePageTranscode.compute");
            }
#endif
        }

        void ConfigurePackedDirectDiagnosticKeywords()
        {
            ConfigurePackedDirectDiagnosticKeyword(settings.vbufferPreviewMaterial);
            ConfigurePackedDirectDiagnosticKeyword(settings.vbufferLitResolveMaterial);
            ConfigurePackedDirectDiagnosticKeyword(runtimeVBufferPreviewMaterial);
            ConfigurePackedDirectDiagnosticKeyword(runtimeDepthWriteMaterial);
            ConfigurePackedDirectDiagnosticKeyword(runtimeShadowCasterMaterial);
            ConfigurePackedDirectDiagnosticKeyword(runtimeVBufferLitResolveMaterial);
        }

        void ConfigurePackedDirectDiagnosticKeyword(Material material)
        {
            if (material == null)
                return;
            bool enabled = material.IsKeywordEnabled(kPackedDirectDiagnosticKeyword);
            if (enabled == settings.enablePackedDirectDiagnostic)
                return;
            if (settings.enablePackedDirectDiagnostic)
                material.EnableKeyword(kPackedDirectDiagnosticKeyword);
            else
                material.DisableKeyword(kPackedDirectDiagnosticKeyword);
        }

        void InitCompactKernels()
        {
            kernelCompactClear = -1;
            kernelCompactTris = -1;
            kernelCompactFinalize = -1;
            kernelCompactFinalizeVisibleQueue = -1;
            kernelCompactFinalizeShadowQueues = -1;
            kernelPrepareIndexedDrawQueue = -1;
            kernelBuildIndexedDrawQueue = -1;
            if (settings.visibleTriangleCompactShader == null)
                return;
            try
            {
                kernelCompactClear = settings.visibleTriangleCompactShader.FindKernel("CSClear");
                kernelCompactTris = settings.visibleTriangleCompactShader.FindKernel("CSCompactClusters");
                kernelCompactFinalize = settings.visibleTriangleCompactShader.FindKernel("CSFinalizeArgs");
                try
                {
                    kernelCompactFinalizeVisibleQueue = settings.visibleTriangleCompactShader.FindKernel("CSFinalizeVisibleDrawQueueArgs");
                }
                catch
                {
                    kernelCompactFinalizeVisibleQueue = -1;
                }
                try
                {
                    kernelCompactFinalizeShadowQueues = settings.visibleTriangleCompactShader.FindKernel("CSFinalizeVisibleDrawQueueArgs4");
                }
                catch
                {
                    kernelCompactFinalizeShadowQueues = -1;
                }
                try
                {
                    kernelPrepareIndexedDrawQueue = settings.visibleTriangleCompactShader.FindKernel("CSPrepareIndexedDrawQueue");
                    kernelBuildIndexedDrawQueue = settings.visibleTriangleCompactShader.FindKernel("CSBuildIndexedDrawQueue");
                }
                catch
                {
                    kernelPrepareIndexedDrawQueue = -1;
                    kernelBuildIndexedDrawQueue = -1;
                }
            }
            catch
            {
                kernelCompactClear = -1;
                kernelCompactTris = -1;
                kernelCompactFinalize = -1;
                kernelCompactFinalizeVisibleQueue = -1;
                kernelCompactFinalizeShadowQueues = -1;
                kernelPrepareIndexedDrawQueue = -1;
                kernelBuildIndexedDrawQueue = -1;
            }
        }

        bool IsVisibleTriangleCompactReady()
        {
            if (!settings.enableVisibleTriangleCompact)
                return false;
            if (settings.visibleTriangleCompactShader == null)
                return false;
            if (kernelCompactClear < 0 || kernelCompactTris < 0 || kernelCompactFinalize < 0)
                InitCompactKernels();
            return kernelCompactClear >= 0 && kernelCompactTris >= 0 && kernelCompactFinalize >= 0;
        }

        bool HasValidCompactForSelection(int selectionKey)
        {
            return IsVisibleTriangleCompactReady() &&
                   lastCompactSucceeded &&
                   lastCompactFrame == Time.frameCount &&
                   lastCompactSelectionKey == selectionKey &&
                   sceneVisibilityBackend != null &&
                   lastCompactGeometryGeneration == sceneVisibilityBackend.GeometryGeneration;
        }

        bool CanUseDirectVisibleDrawQueue(Camera camera, int selectionKey)
        {
            return kernelCompactFinalizeVisibleQueue >= 0 &&
                   !settings.enableBevyFormalDualRaster &&
                   selectionKey != kCompactSelectionPass2 &&
                   batchedCulling != null &&
                   batchedCulling.IsVisibleDrawQueueReady(camera);
        }

        bool EnsureVisibleTrianglesCompacted(
            CommandBuffer cmd,
            Camera camera,
            Dictionary<int, NaniteRuntimeSelection> selections,
            int selectionKey)
        {
            if (!TryPrepareSceneVisibility(camera, selections))
                return false;

            if (!IsVisibleTriangleCompactReady())
            {
                sceneVisibilityBackend.ResetDrawArgsToFullMesh();
                lastCompactSucceeded = false;
                lastCompactUsedDirectQueue = false;
                return true;
            }

            if (HasValidCompactForSelection(selectionKey))
                return true;

            LogCompactStatsOnce(selectionKey);
            bool useDirectQueue = CanUseDirectVisibleDrawQueue(camera, selectionKey);
            lastCompactSucceeded = useDirectQueue
                ? sceneVisibilityBackend.DispatchVisibleDrawQueueFinalize(
                    cmd,
                    settings.visibleTriangleCompactShader,
                    kernelCompactFinalizeVisibleQueue,
                    batchedCulling.VisibleDrawCountArgsBuffer)
                : sceneVisibilityBackend.DispatchVisibleTriangleCompact(
                    cmd,
                    settings.visibleTriangleCompactShader,
                    kernelCompactClear,
                    kernelCompactTris,
                    kernelCompactFinalize);
            lastCompactUsedDirectQueue = useDirectQueue && lastCompactSucceeded;
            lastCompactFrame = Time.frameCount;
            lastCompactSelectionKey = selectionKey;
            lastCompactGeometryGeneration = sceneVisibilityBackend.GeometryGeneration;
            if (!lastCompactSucceeded)
                sceneVisibilityBackend.ResetDrawArgsToFullMesh();
            return true;
        }

        bool EnsureVisibleTrianglesCompacted(
            UnsafeCommandBuffer cmd,
            Camera camera,
            Dictionary<int, NaniteRuntimeSelection> selections,
            int selectionKey)
        {
            if (!TryPrepareSceneVisibility(camera, selections))
                return false;

            if (!IsVisibleTriangleCompactReady())
            {
                sceneVisibilityBackend.ResetDrawArgsToFullMesh();
                lastCompactSucceeded = false;
                lastCompactUsedDirectQueue = false;
                return true;
            }

            if (HasValidCompactForSelection(selectionKey))
                return true;

            LogCompactStatsOnce(selectionKey);
            bool useDirectQueue = CanUseDirectVisibleDrawQueue(camera, selectionKey);
            lastCompactSucceeded = useDirectQueue
                ? sceneVisibilityBackend.DispatchVisibleDrawQueueFinalize(
                    cmd,
                    settings.visibleTriangleCompactShader,
                    kernelCompactFinalizeVisibleQueue,
                    batchedCulling.VisibleDrawCountArgsBuffer)
                : sceneVisibilityBackend.DispatchVisibleTriangleCompact(
                    cmd,
                    settings.visibleTriangleCompactShader,
                    kernelCompactClear,
                    kernelCompactTris,
                    kernelCompactFinalize);
            lastCompactUsedDirectQueue = useDirectQueue && lastCompactSucceeded;
            lastCompactFrame = Time.frameCount;
            lastCompactSelectionKey = selectionKey;
            lastCompactGeometryGeneration = sceneVisibilityBackend.GeometryGeneration;
            if (!lastCompactSucceeded)
                sceneVisibilityBackend.ResetDrawArgsToFullMesh();
            return true;
        }

        void LogCompactStatsOnce(int selectionKey)
        {
            if (loggedCompactStatsOnce || sceneVisibilityBackend == null)
                return;
            loggedCompactStatsOnce = true;
            int visible = sceneVisibilityBackend.CountVisibleClustersCpu();
            int total = sceneVisibilityBackend.ClusterCount;
            string sel = selectionKey == kCompactSelectionMerged ? "merged" : "first";
            Debug.Log(
                $"[Nanite][RF] VisibleTriangleCompact: selection={sel}, " +
                $"visibleClusters={visible}/{total}, triangles={sceneVisibilityBackend.TriangleCount}. " +
                "若 visibleClusters≈total，则 compact 几乎无收益（剔除偏松/全可见）。");
        }

        void BindCompactDrawState(CommandBuffer cmd, int selectionKey)
        {
            bool use = HasValidCompactForSelection(selectionKey) &&
                       sceneVisibilityBackend != null &&
                       sceneVisibilityBackend.CompactedTriIdsBuffer != null &&
                       sceneVisibilityBackend.CompactedTriInstancesBuffer != null &&
                       sceneVisibilityBackend.CompactedTriCountsBuffer != null;
            bool direct = use &&
                          lastCompactUsedDirectQueue &&
                          batchedCulling != null &&
                          batchedCulling.VisibleDrawClusterBuffer != null;
            cmd.SetGlobalFloat(ShaderIds.UseCompactedTriIds, use ? 1f : 0f);
            cmd.SetGlobalFloat(ShaderIds.UseDirectVisibleDrawQueue, direct ? 1f : 0f);
            if (use)
            {
                cmd.SetGlobalBuffer(ShaderIds.CompactedTriIds, sceneVisibilityBackend.CompactedTriIdsBuffer);
                cmd.SetGlobalBuffer(ShaderIds.CompactedTriInstances, sceneVisibilityBackend.CompactedTriInstancesBuffer);
                cmd.SetGlobalBuffer(ShaderIds.CompactedTriCounts, sceneVisibilityBackend.CompactedTriCountsBuffer);
                if (direct)
                    cmd.SetGlobalBuffer(ShaderIds.CompactedDrawClusters, batchedCulling.VisibleDrawClusterBuffer);
                cmd.SetGlobalFloat(ShaderIds.CompactedClusterTriangleSlots, sceneVisibilityBackend.CompactedClusterTriangleSlots);
            }
        }

        void BindCompactDrawState(RasterCommandBuffer cmd, int selectionKey)
        {
            bool use = HasValidCompactForSelection(selectionKey) &&
                       sceneVisibilityBackend != null &&
                       sceneVisibilityBackend.CompactedTriIdsBuffer != null &&
                       sceneVisibilityBackend.CompactedTriInstancesBuffer != null &&
                       sceneVisibilityBackend.CompactedTriCountsBuffer != null;
            bool direct = use &&
                          lastCompactUsedDirectQueue &&
                          batchedCulling != null &&
                          batchedCulling.VisibleDrawClusterBuffer != null;
            cmd.SetGlobalFloat(ShaderIds.UseCompactedTriIds, use ? 1f : 0f);
            cmd.SetGlobalFloat(ShaderIds.UseDirectVisibleDrawQueue, direct ? 1f : 0f);
            if (use)
            {
                cmd.SetGlobalBuffer(ShaderIds.CompactedTriIds, sceneVisibilityBackend.CompactedTriIdsBuffer);
                cmd.SetGlobalBuffer(ShaderIds.CompactedTriInstances, sceneVisibilityBackend.CompactedTriInstancesBuffer);
                cmd.SetGlobalBuffer(ShaderIds.CompactedTriCounts, sceneVisibilityBackend.CompactedTriCountsBuffer);
                if (direct)
                    cmd.SetGlobalBuffer(ShaderIds.CompactedDrawClusters, batchedCulling.VisibleDrawClusterBuffer);
                cmd.SetGlobalFloat(ShaderIds.CompactedClusterTriangleSlots, sceneVisibilityBackend.CompactedClusterTriangleSlots);
            }
        }

        void BindCompactDrawState(MaterialPropertyBlock mpb, int selectionKey)
        {
            bool use = HasValidCompactForSelection(selectionKey) &&
                       sceneVisibilityBackend != null &&
                       sceneVisibilityBackend.CompactedTriIdsBuffer != null &&
                       sceneVisibilityBackend.CompactedTriInstancesBuffer != null &&
                       sceneVisibilityBackend.CompactedTriCountsBuffer != null;
            bool direct = use &&
                          lastCompactUsedDirectQueue &&
                          batchedCulling != null &&
                          batchedCulling.VisibleDrawClusterBuffer != null;
            mpb.SetFloat(ShaderIds.UseCompactedTriIds, use ? 1f : 0f);
            mpb.SetFloat(ShaderIds.UseDirectVisibleDrawQueue, direct ? 1f : 0f);
            if (use)
            {
                mpb.SetBuffer(ShaderIds.CompactedTriIds, sceneVisibilityBackend.CompactedTriIdsBuffer);
                mpb.SetBuffer(ShaderIds.CompactedTriInstances, sceneVisibilityBackend.CompactedTriInstancesBuffer);
                mpb.SetBuffer(ShaderIds.CompactedTriCounts, sceneVisibilityBackend.CompactedTriCountsBuffer);
                if (direct)
                    mpb.SetBuffer(ShaderIds.CompactedDrawClusters, batchedCulling.VisibleDrawClusterBuffer);
                mpb.SetFloat(ShaderIds.CompactedClusterTriangleSlots, sceneVisibilityBackend.CompactedClusterTriangleSlots);
            }
        }

        class CompactRgPassData
        {
            internal Camera camera;
            internal int selectionKey;
        }

        void RecordVisibleTriangleCompactPass(
            RenderGraph renderGraph,
            Camera camera,
            int selectionKey,
            ProfilingSampler profilingSampler)
        {
            if (!IsVisibleTriangleCompactReady() || camera == null)
                return;

            using (var builder = renderGraph.AddUnsafePass<CompactRgPassData>("Nanite/CompactVisibleTris", out var passData, profilingSampler))
            {
                passData.camera = camera;
                passData.selectionKey = selectionKey;
                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc((CompactRgPassData data, UnsafeGraphContext context) =>
                {
                    // GPU mask 路径不依赖 CPU selections；Pass1/Pass2/Merged 共用 scene clusterVisible。
                    var selections = data.selectionKey == kCompactSelectionMerged ? mergedSelections : firstSelections;
                    EnsureVisibleTrianglesCompacted(context.cmd, data.camera, selections, data.selectionKey);
                });
            }
        }

        void RecordIndexedDrawBuildPass(
            RenderGraph renderGraph,
            Camera camera,
            int selectionKey,
            ProfilingSampler profilingSampler)
        {
            if (!settings.enableIndexedClusterRaster || camera == null)
                return;

            using (var builder = renderGraph.AddUnsafePass<CompactRgPassData>(
                       "Nanite/BuildIndexedClusterQueue",
                       out var passData,
                       profilingSampler))
            {
                passData.camera = camera;
                passData.selectionKey = selectionKey;
                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc((CompactRgPassData data, UnsafeGraphContext context) =>
                {
                    TryBuildCameraIndexedDraw(context.cmd, data.camera, data.selectionKey);
                });
            }
        }

        void LogFormalAssetReadiness()
        {
            if (!IsFormalVisibilityEnabled())
                return;

            var rasterShader = Shader.Find("Nanite/VBufferPacketRaster");
            var resolveShader = Shader.Find("Nanite/VBufferLitResolve");
            if (rasterShader == null || resolveShader == null)
            {
                Debug.LogWarning(
                    "[Nanite][RF] Formal 已开启，但关键 Shader 未找到：" +
                    $"PacketRaster={(rasterShader != null ? "OK" : "MISSING")}, " +
                    $"LitResolve={(resolveShader != null ? "OK" : "MISSING")}。" +
                    "请确认 shader 无编译错误且已导入。");
            }

#if UNITY_EDITOR
            if (resolveShader != null)
            {
                var messages = ShaderUtil.GetShaderMessages(resolveShader);
                if (messages != null && messages.Length > 0)
                {
                    for (int i = 0; i < messages.Length; i++)
                    {
                        var msg = messages[i];
                        Debug.LogError(
                            $"[Nanite][RF] Nanite/VBufferLitResolve: {msg.message} (line {msg.line}, {msg.file})");
                    }

                    Debug.LogWarning("[Nanite][RF] Nanite/VBufferLitResolve 存在编译错误，Formal Resolve 不会入图。");
                }
            }
#endif
        }

        void LogFormalSkipOnce(string reason)
        {
            if (loggedFormalSkipReason || string.IsNullOrEmpty(reason))
                return;
            loggedFormalSkipReason = true;
            Debug.LogWarning($"[Nanite][RF] Formal VisibilityBuffer 未注册 RenderGraph 子 Pass：{reason}");
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (!CanRunForCamera(ref renderingData))
                return;

            SyncPassEvents();
            recordedHzbDepthSource = TextureHandle.nullHandle;
            NaniteRuntimeRegistry.MarkFeatureDrivenFrame();
            SuppressProxyDebugRenderersIfNeeded();
            renderer.EnqueuePass(passFirstCull);
            if (ShouldEnqueueWriteDepth())
                renderer.EnqueuePass(passWriteDepth);
            if (IsHzbActiveForScene())
                renderer.EnqueuePass(passBuildHzb);
            // 无 HZB 时 Pass1 已画全量 LOD 切割，再跑 Cull2 等于双倍 ClusterCull（更慢且无收益）。
            if (ShouldEnqueueSecondCull())
                renderer.EnqueuePass(passSecondCull);
            // URP invokes the external Nanite draw while its main-light atlas is bound.
            // A separate AfterRenderingShadows pass can address a different RenderGraph
            // texture version when the native renderer list is empty, so it is not enqueued.
            if (IsFormalVisibilityEnabled())
                renderer.EnqueuePass(passFormalVisibility);
            else if (settings.enableFormalVisibilityBuffer &&
                     !settings.enableFormalVisibilityBufferExperimentalGate &&
                     !loggedFormalGateWarning)
            {
                loggedFormalGateWarning = true;
                Debug.LogWarning("[Nanite][RF] enableFormalVisibilityBuffer=true，但 experimental gate 未开启，Formal pass 不会执行。");
            }
            if (settings.enableVBufferPreview)
                renderer.EnqueuePass(passVBufferPreview);
            if (settings.enableDebugVisualization)
                renderer.EnqueuePass(passDebugVisualization);
            if (sceneVisibilityBackend != null &&
                sceneVisibilityBackend.PagePoolRequiresEviction &&
                sceneVisibilityBackend.NeedsPageRetirementFence)
            {
                renderer.EnqueuePass(passPageRetirementFence);
            }
        }

        void SyncPassEvents()
        {
            passFirstCull.renderPassEvent = settings.firstCullEvent;
            passWriteDepth.renderPassEvent = settings.writeDepthEvent;
            int eHzb = (int)settings.buildHzbEvent + settings.buildHzbQueueOffset;
            if (settings.enforcePassOrdering && eHzb <= (int)settings.writeDepthEvent)
                eHzb = (int)settings.writeDepthEvent + 1;

            passBuildHzb.renderPassEvent = (RenderPassEvent)eHzb;
            passSecondCull.renderPassEvent = settings.secondCullEvent;
            passVBufferPreview.renderPassEvent = settings.vbufferPreviewEvent;
            passDebugVisualization.renderPassEvent = settings.debugVisualizationEvent;
            int eFormal = (int)settings.formalVBufferEvent + settings.formalVBufferQueueOffset;
            eFormal = Mathf.Clamp(
                eFormal,
                (int)RenderPassEvent.AfterRenderingGbuffer + 1,
                (int)RenderPassEvent.BeforeRenderingDeferredLights - 1);
            passFormalVisibility.renderPassEvent = (RenderPassEvent)eFormal;

            if (!loggedPassOrderWarning &&
                settings.enforcePassOrdering &&
                eHzb >= (int)passSecondCull.renderPassEvent)
            {
                loggedPassOrderWarning = true;
                Debug.LogWarning(
                    "[Nanite][RF] BuildHzb 事件（含 offset）不早于 SecondCull，HZB 可能无效。" +
                    "请把 Build Hzb Event/Offset 设在 SecondCull 之前，或把 SecondCull 后移。");
            }

            if (!loggedFormalOrderWarning &&
                IsFormalVisibilityEnabled() &&
                eFormal >= (int)RenderPassEvent.BeforeRenderingDeferredLights)
            {
                loggedFormalOrderWarning = true;
                Debug.LogWarning(
                    "[Nanite][RF] Formal VisibilityBuffer 事件不在 DeferredLights 之前，GBuffer 合并可能无效。建议设为 AfterRenderingGbuffer(220)+1~9。");
            }
        }

        bool IsFormalVisibilityEnabled() =>
            settings.enableFormalVisibilityBuffer &&
            settings.enableFormalVisibilityBufferExperimentalGate;

        bool ShouldEnqueueWriteDepth()
        {
            // M3：无 HZB 时独立 WriteDepth 与 Formal Raster 双重深度；默认跳过。
            if (settings.skipWriteDepthWhenNoHzb && !IsHzbActiveForScene())
                return false;
            return true;
        }

        bool TryBuildCameraIndexedDraw(
            UnsafeCommandBuffer cmd,
            Camera camera,
            int selectionKey)
        {
            if (!settings.enableIndexedClusterRaster || cmd == null || camera == null ||
                sceneVisibilityBackend == null || !sceneVisibilityBackend.IndexedDrawAvailable ||
                batchedCulling == null || !lastCompactUsedDirectQueue ||
                !HasValidCompactForSelection(selectionKey) ||
                !batchedCulling.IsVisibleDrawQueueReady(camera) ||
                kernelPrepareIndexedDrawQueue < 0 || kernelBuildIndexedDrawQueue < 0)
                return false;

            bool built = sceneVisibilityBackend.DispatchIndexedDrawQueue(
                cmd,
                settings.visibleTriangleCompactShader,
                kernelPrepareIndexedDrawQueue,
                kernelBuildIndexedDrawQueue,
                batchedCulling.VisibleDrawClusterBuffer,
                batchedCulling.VisibleDrawCountArgsBuffer);
            if (built)
            {
                lastIndexedDrawFrame = Time.frameCount;
                lastIndexedDrawSelectionKey = selectionKey;
                lastIndexedDrawGeometryGeneration = sceneVisibilityBackend.GeometryGeneration;
            }
            return built;
        }

        bool HasValidIndexedDraw(int selectionKey)
        {
            return settings.enableIndexedClusterRaster &&
                   sceneVisibilityBackend != null &&
                   sceneVisibilityBackend.IndexedDrawAvailable &&
                   lastIndexedDrawFrame == Time.frameCount &&
                   lastIndexedDrawSelectionKey == selectionKey &&
                   lastIndexedDrawGeometryGeneration == sceneVisibilityBackend.GeometryGeneration;
        }

        bool ShouldEnqueueSecondCull()
        {
            // Legacy 双 cull：Second 用 HZB OR 补洞。Bevy 无 HZB 时 Pass1 已完整，跳过 Cull2。
            if (IsBevyTwoPhaseEnabled() && !IsHzbActiveForScene())
                return false;
            return true;
        }

        bool IsBevyTwoPhaseEnabled() => settings.enableBevyTwoPhaseOcclusion;

        bool IsHzbActiveForScene()
        {
            if (!settings.useHzbCulling)
                return false;
            if (!settings.adaptiveHzbCulling)
                return true;
            if (sceneVisibilityBackend == null || !sceneVisibilityBackend.IsReady)
                return false;

            int minClusters = Mathf.Max(0, settings.hzbMinClusterCount);
            return minClusters <= 0 || sceneVisibilityBackend.ClusterCount >= minClusters;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (externalMainLightShadowProvider != null)
            {
                ExternalShadowCasterRegistry.UnregisterMainLightProvider(externalMainLightShadowProvider);
                externalMainLightShadowProvider = null;
            }
            if (externalMainLightShadowDrawProvider != null)
            {
                ExternalShadowCasterRegistry.UnregisterMainLightDrawProvider(externalMainLightShadowDrawProvider);
                externalMainLightShadowDrawProvider = null;
            }
            if (externalMainLightShadowCullBatchProvider != null)
            {
                ExternalShadowCasterRegistry.UnregisterMainLightCullBatchProvider(externalMainLightShadowCullBatchProvider);
                externalMainLightShadowCullBatchProvider = null;
            }
            if (cameraHzb != null)
            {
                foreach (var kv in cameraHzb)
                    ReleaseState(kv.Value);
                cameraHzb.Clear();
            }
            ReleaseRuntimeMaterial();
            batchedCulling?.Dispose();
            batchedCulling = null;
            sceneVisibilityBackend?.Dispose();
            sceneVisibilityBackend = null;
            ReleaseFormalBuffers();
            loggedExecutionOnce = false;
            loggedVBufferOnce = false;
            loggedBatchedDispatchStats = false;
            loggedFormalOnce = false;
            loggedFormalOrderWarning = false;
            loggedHybridRasterWarning = false;
            loggedFormalGateWarning = false;
            loggedFormalSkipReason = false;
            loggedFormalRecordSuccess = false;
            loggedFormalRasterStats = false;
            loggedExternalShadowSchedulingOnce = false;
            loggedShadowExecutionOnce = false;
            lastGpuScenePrepareFrame = -1;
            lastGpuScenePrepareCameraId = 0;
            lastProxyStatsWritebackFrame = -1;
            lastPipelineAdmissionFrame = -1;
            perfSampleCount = 0;
            perfFirstCullCpuMsAccum = 0;
            perfSecondCullCpuMsAccum = 0;
            perfFirstCullDispatchAccum = 0;
            perfSecondCullDispatchAccum = 0;
            perfFirstCullReadbackAccum = 0;
            perfSecondCullReadbackAccum = 0;
            perfFormalSampleCount = 0;
            perfVisibilityDrawAccum = 0;
            perfResolveDrawAccum = 0;
            perfMaterialBatchAccum = 0;
            perfTileCoverageAccum = 0;
            perfTileCoverageSampleCount = 0;
        }

        bool CanRunForCamera(ref RenderingData renderingData)
        {
            if (!settings.enable || settings.hzbBuilderShader == null)
                return false;
            if (UniversalNaniteCullingBridge.enableCoreTimingHook)
                return false;

            var cameraData = renderingData.cameraData;
            var camera = cameraData.camera;
            if (camera == null)
                return false;
            if (cameraData.cameraType == CameraType.Preview || cameraData.cameraType == CameraType.Reflection)
                return false;

            var proxies = NaniteRuntimeRegistry.ActiveProxies;
            UpdatePipelineAdmission(proxies);
            for (int i = 0; i < proxies.Count; i++)
            {
                var p = proxies[i];
                if (p != null && p.isActiveAndEnabled && p.NaniteRenderingActive && p.naniteMesh != null)
                    return true;
            }

            return false;
        }

        void EnsureExternalShadowProviderRegistered()
        {
            externalMainLightShadowProvider ??= HasExternalMainLightShadowCasters;
            ExternalShadowCasterRegistry.RegisterMainLightProvider(externalMainLightShadowProvider);
            externalMainLightShadowCullBatchProvider ??= CullExternalMainLightShadowCastersBatch;
            ExternalShadowCasterRegistry.RegisterMainLightCullBatchProvider(externalMainLightShadowCullBatchProvider);
            externalMainLightShadowDrawProvider ??= DrawExternalMainLightShadowCasters;
            ExternalShadowCasterRegistry.RegisterMainLightDrawProvider(externalMainLightShadowDrawProvider);
        }

        void CullExternalMainLightShadowCastersBatch(ExternalMainLightShadowCullBatchContext context)
        {
            if (!settings.enable || !settings.enableShadowCasting || settings.gpuCullingShader == null)
                return;
            if (UniversalNaniteCullingBridge.enableCoreTimingHook)
                return;
            if (context.commandBuffer == null || context.camera == null || context.shadowLight.light == null ||
                context.cascadeSlices == null)
                return;
            if (context.camera.cameraType == CameraType.Preview || context.camera.cameraType == CameraType.Reflection)
                return;
            int activeCascadeCount = Mathf.Min(
                context.cascadeCount,
                Mathf.Clamp(settings.maxNaniteShadowCascades, 1, 4));
            if (activeCascadeCount <= 0 || context.cascadeSlices.Length < activeCascadeCount)
                return;

            using (kShadowCullPrepareMarker.Auto())
            {
                IReadOnlyList<NaniteRuntimeProxy> proxies = NaniteRuntimeRegistry.ActiveProxies;
                UpdatePipelineAdmission(proxies);
                ComputeShader cullingShader = ResolveCullingShader(proxies);
                int batchableProxyCount = CountBatchableProxies(proxies);
                if (cullingShader == null || batchableProxyCount <= 0)
                    return;

                sceneVisibilityBackend ??= new NaniteSceneVisibilityBufferBackend();
                sceneVisibilityBackend.LightProbeRefreshInterval = settings.lightProbeRefreshInterval;
                sceneVisibilityBackend.PagePoolMaxMiB = settings.pagePoolMaxMiB;
                sceneVisibilityBackend.PackedPageRasterRequested = settings.enablePackedPageRaster;
                sceneVisibilityBackend.PageTranscodeShader = settings.pageTranscodeShader;
                sceneVisibilityBackend.PageStreamingRequestsEnabled = settings.enablePageStreamingUploads;
                if (!sceneVisibilityBackend.EnsureInitialized(proxies))
                    return;

                batchedCulling ??= new NaniteGpuBatchedCullingBackend();
                batchedCulling.LodErrorPixelsOverride = settings.overrideLodErrorPixels;
                if (!batchedCulling.EnsureInitialized(cullingShader, proxies))
                    return;
                ConfigureBatchedCullingPolicy(batchableProxyCount);
                if (!batchedCulling.EnsureClusterSceneIndex(sceneVisibilityBackend))
                    return;

                if (kernelCompactFinalizeVisibleQueue < 0 || kernelCompactFinalizeShadowQueues < 0)
                    InitCompactKernels();
                if (settings.visibleTriangleCompactShader == null || kernelCompactFinalizeVisibleQueue < 0)
                    return;
            }

            for (int cascadeIndex = 0; cascadeIndex < activeCascadeCount; cascadeIndex++)
            {
                ShadowSliceData slice = context.cascadeSlices[cascadeIndex];
                shadowBatchViewMatrices[cascadeIndex] = slice.viewMatrix;
                shadowBatchProjectionMatrices[cascadeIndex] = slice.projectionMatrix;
                shadowBatchResolutions[cascadeIndex] = slice.resolution;
            }

            using (kShadowCullRecordMarker.Auto())
            {
                bool recorded = batchedCulling.RecordShadowCullBatch(
                    context.commandBuffer,
                    context.camera,
                    activeCascadeCount,
                    shadowBatchViewMatrices,
                    shadowBatchProjectionMatrices,
                    shadowBatchResolutions);
                if (!recorded)
                {
                    recorded = true;
                    for (int cascadeIndex = 0; cascadeIndex < activeCascadeCount; cascadeIndex++)
                    {
                        if (!batchedCulling.RecordShadowCull(
                                context.commandBuffer,
                                context.camera,
                                cascadeIndex,
                                shadowBatchViewMatrices[cascadeIndex],
                                shadowBatchProjectionMatrices[cascadeIndex],
                                shadowBatchResolutions[cascadeIndex]))
                        {
                            recorded = false;
                            break;
                        }
                    }
                }
                if (!recorded)
                    return;

                bool finalized = kernelCompactFinalizeShadowQueues >= 0 &&
                    batchedCulling.DispatchShadowDrawQueueFinalizeBatch(
                        context.commandBuffer,
                        settings.visibleTriangleCompactShader,
                        kernelCompactFinalizeShadowQueues,
                        sceneVisibilityBackend.CompactedClusterTriangleSlots);
                if (!finalized)
                {
                    for (int cascadeIndex = 0; cascadeIndex < activeCascadeCount; cascadeIndex++)
                    {
                        sceneVisibilityBackend.DispatchVisibleDrawQueueFinalize(
                            context.commandBuffer,
                            settings.visibleTriangleCompactShader,
                            kernelCompactFinalizeVisibleQueue,
                            batchedCulling.GetShadowDrawCountArgsBuffer(cascadeIndex),
                            batchedCulling.GetShadowDrawArgsBuffer(cascadeIndex));
                    }
                }

                if (settings.enableIndexedClusterRaster &&
                    sceneVisibilityBackend.IndexedDrawAvailable &&
                    kernelPrepareIndexedDrawQueue >= 0 &&
                    kernelBuildIndexedDrawQueue >= 0)
                {
                    for (int cascadeIndex = 0; cascadeIndex < activeCascadeCount; cascadeIndex++)
                    {
                        sceneVisibilityBackend.DispatchIndexedDrawQueue(
                            context.commandBuffer,
                            settings.visibleTriangleCompactShader,
                            kernelPrepareIndexedDrawQueue,
                            kernelBuildIndexedDrawQueue,
                            batchedCulling.GetShadowDrawClusterBuffer(cascadeIndex),
                            batchedCulling.GetShadowDrawCountArgsBuffer(cascadeIndex),
                            cascadeIndex);
                    }
                }
            }
        }

        bool HasExternalMainLightShadowCasters(Camera camera, Light light)
        {
            if (!settings.enable || !settings.enableShadowCasting || settings.hzbBuilderShader == null)
                return false;
            if (UniversalNaniteCullingBridge.enableCoreTimingHook)
                return false;
            if (camera == null || light == null || light.shadows == LightShadows.None)
                return false;
            if (camera.cameraType == CameraType.Preview || camera.cameraType == CameraType.Reflection)
                return false;

            IReadOnlyList<NaniteRuntimeProxy> proxies = NaniteRuntimeRegistry.ActiveProxies;
            for (int i = 0; i < proxies.Count; i++)
            {
                NaniteRuntimeProxy proxy = proxies[i];
                if (proxy == null || !proxy.isActiveAndEnabled || !proxy.NaniteRenderingActive || proxy.naniteMesh == null)
                    continue;

                if (!loggedExternalShadowSchedulingOnce)
                {
                    loggedExternalShadowSchedulingOnce = true;
                    Debug.Log(
                        "[Nanite][Shadow] external GPU-driven caster registered for the native main-light atlas; " +
                        "it also keeps the atlas alive when URP native caster bounds are empty.");
                }
                return true;
            }

            return false;
        }

        void DrawExternalMainLightShadowCasters(ExternalMainLightShadowDrawContext context)
        {
            if (!settings.enable || !settings.enableShadowCasting || settings.hzbBuilderShader == null)
                return;
            if (UniversalNaniteCullingBridge.enableCoreTimingHook)
                return;
            if (context.commandBuffer == null || context.camera == null || context.shadowLight.light == null)
                return;
            if (context.camera.cameraType == CameraType.Preview || context.camera.cameraType == CameraType.Reflection)
                return;
            if (context.cascadeIndex >= Mathf.Clamp(settings.maxNaniteShadowCascades, 1, 4))
                return;

            Material material = EnsureShadowCasterMaterial();
            bool dedicatedQueue =
                batchedCulling != null &&
                batchedCulling.IsShadowDrawQueueReady(context.camera, context.cascadeIndex) &&
                sceneVisibilityBackend != null &&
                sceneVisibilityBackend.IsReady;
            if (material == null ||
                (!dedicatedQueue && !TryPrepareSceneVisibility(context.camera, firstSelections)))
                return;

            using var shadowSubmitScope = kShadowSubmitMarker.Auto();
            if (!loggedShadowExecutionOnce)
            {
                loggedShadowExecutionOnce = true;
                int activeCascadeCount = Mathf.Min(
                    context.cascadeCount,
                    Mathf.Clamp(settings.maxNaniteShadowCascades, 1, 4));
                string queueName = dedicatedQueue
                    ? (batchedCulling.LastShadowUsedFusedBatch
                        ? "shadowFrustum/fusedDirect"
                        : (batchedCulling.LastShadowUsedVisibleInstanceQueue
                        ? "shadowFrustum/instancePartIndirect"
                        : (batchedCulling.LastShadowUsedVisiblePartQueue
                            ? "shadowFrustum/partIndirect"
                            : "shadowFrustum/direct")))
                    : "cameraFirstCull/fallback";
                Debug.Log(
                    $"[Nanite][Shadow] native atlas hook active: atlas={context.atlasWidth}x{context.atlasHeight}, " +
                    $"cascades={activeCascadeCount}/{context.cascadeCount}, " +
                    $"queue={queueName}, " +
                    $"instances={(sceneVisibilityBackend != null ? sceneVisibilityBackend.InstanceCount : 0)}.");
            }

            VisibleLight shadowLight = context.shadowLight;
            SetupShadowCasterGlobals(context.commandBuffer, ref shadowLight, context.shadowBias);
            context.commandBuffer.SetGlobalDepthBias(1.0f, 2.5f);
            context.commandBuffer.SetViewport(context.viewport);
            Matrix4x4 shadowProj = GL.GetGPUProjectionMatrix(context.projectionMatrix, true);
            context.commandBuffer.SetGlobalMatrix(
                ShaderIds.NaniteShadowViewProj,
                shadowProj * context.viewMatrix);
            DrawNaniteShadowGeometry(
                context.commandBuffer,
                material,
                context.camera,
                context.cascadeIndex);
            context.commandBuffer.DisableScissorRect();
            context.commandBuffer.SetGlobalDepthBias(0.0f, 0.0f);
        }

        void UpdatePipelineAdmission(IReadOnlyList<NaniteRuntimeProxy> proxies)
        {
            if (proxies == null)
                return;
            if (lastPipelineAdmissionFrame == Time.frameCount)
                return;
            lastPipelineAdmissionFrame = Time.frameCount;

            int triangleThreshold = Mathf.Max(0, settings.smallMeshTriangleThreshold);
            for (int i = 0; i < proxies.Count; i++)
            {
                var proxy = proxies[i];
                if (proxy == null || !proxy.isActiveAndEnabled || proxy.naniteMesh == null)
                    continue;

                int triangleCount = proxy.RasterFallbackTriangleCount;
                bool autoRaster = settings.enableSmallMeshRasterFallback &&
                                  triangleThreshold > 0 &&
                                  triangleCount > 0 &&
                                  triangleCount <= triangleThreshold;
                bool useRaster = !proxy.forceNaniteRendering &&
                                 proxy.CanUseRasterFallback &&
                                 (proxy.NativeRendererRequested || autoRaster);
                proxy.SetRasterFallbackActive(useRaster);
            }
        }

        void InitCopyDepthKeywords()
        {
            if (copyDepthKeywordsInitialized)
                return;

            keywordDepthMsaa2 = GlobalKeyword.Create(ShaderKeywordStrings.DepthMsaa2);
            keywordDepthMsaa4 = GlobalKeyword.Create(ShaderKeywordStrings.DepthMsaa4);
            keywordDepthMsaa8 = GlobalKeyword.Create(ShaderKeywordStrings.DepthMsaa8);
            keywordOutputDepth = GlobalKeyword.Create(ShaderKeywordStrings._OUTPUT_DEPTH);
            copyDepthKeywordsInitialized = true;
        }

        void ConfigureCopyDepthShaderKeywords(RasterCommandBuffer cmd)
        {
            InitCopyDepthKeywords();
            cmd.SetKeyword(keywordDepthMsaa2, false);
            cmd.SetKeyword(keywordDepthMsaa4, false);
            cmd.SetKeyword(keywordDepthMsaa8, false);
            cmd.SetKeyword(keywordOutputDepth, false);
        }

        void ConfigureCopyDepthShaderKeywords(CommandBuffer cmd)
        {
            InitCopyDepthKeywords();
            cmd.SetKeyword(keywordDepthMsaa2, false);
            cmd.SetKeyword(keywordDepthMsaa4, false);
            cmd.SetKeyword(keywordDepthMsaa8, false);
            cmd.SetKeyword(keywordOutputDepth, false);
        }

        void InitBuilderKernels()
        {
            kernelCopyDepth = -1;
            kernelCopyDepthHalf = -1;
            kernelDownsample = -1;
            if (settings.hzbBuilderShader == null)
                return;

            try
            {
                kernelCopyDepth = settings.hzbBuilderShader.FindKernel("CSCopyDepth");
                kernelCopyDepthHalf = settings.hzbBuilderShader.FindKernel("CSCopyDepthHalf");
                kernelDownsample = settings.hzbBuilderShader.FindKernel("CSDownsample");
            }
            catch
            {
                kernelCopyDepth = -1;
                kernelCopyDepthHalf = -1;
                kernelDownsample = -1;
            }
        }

        int ResolveHzbCopyKernel()
        {
            if (settings.hzbHalfResolution && kernelCopyDepthHalf >= 0)
                return kernelCopyDepthHalf;
            return kernelCopyDepth;
        }

        class CullRgPassData
        {
            internal Camera camera;
        }

        class BuildHzbRgPassData
        {
            internal Camera camera;
            internal TextureHandle depth;
        }

        class VBufferPreviewRgPassData
        {
            internal Material material;
            internal Camera camera;
        }

        class VBufferCompositeRgPassData
        {
            internal Material material;
            internal TextureHandle vbuffer;
        }

        class DepthWriteRgPassData
        {
            internal Material material;
            internal Camera camera;
        }

        class ShadowWriteRgPassData
        {
            internal Material material;
            internal Camera camera;
            internal CullingResults cullResults;
            internal UniversalLightData lightData;
            internal UniversalShadowData shadowData;
        }

        class CopyDepthRgPassData
        {
            internal Material copyMaterial;
            internal TextureHandle source;
            internal TextureHandle destination;
            internal UniversalCameraData cameraData;
        }

        class FormalRasterRgPassData
        {
            internal Material material;
            internal Camera camera;
            internal int screenWidth;
            internal int screenHeight;
            internal int compactSelectionKey;
        }

        class CopyClusterMaskRgPassData
        {
            internal ComputeBuffer src;
            internal ComputeBuffer dst;
            internal int count;
        }

        class FormalTileClassifyRgPassData
        {
            internal Camera camera;
            internal TextureHandle vbuffer;
            internal int screenWidth;
            internal int screenHeight;
            internal BufferHandle tileMaterialMask;
        }

        class FormalResolveRgPassData
        {
            internal Material material;
            internal Camera camera;
            internal TextureHandle vbuffer;
            internal bool writeToGBuffer;
            internal bool useGBufferDepthSlice;
            internal bool useTileMaterialMask;
            internal BufferHandle tileMaterialMask;
            internal BufferHandle tileIndirectArgs;
            internal int screenWidth;
            internal int screenHeight;
            internal int vbufferWidth;
            internal int vbufferHeight;
        }

        class PageRetirementFenceRgPassData
        {
        }

        const int kGBufferDepthSliceIndex = 4;

        void RecordRenderGraphPass(
            RenderGraph renderGraph,
            ContextContainer frameData,
            PassKind passKind,
            string passName,
            ProfilingSampler profilingSampler)
        {
            if (passKind == PassKind.FirstCull)
                recordedHzbDepthSource = TextureHandle.nullHandle;

            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            if (cameraData == null || cameraData.camera == null)
                return;

            if (passKind == PassKind.PageRetirementFence)
            {
                if (sceneVisibilityBackend == null ||
                    !sceneVisibilityBackend.NeedsPageRetirementFence)
                {
                    return;
                }

                using (var builder = renderGraph.AddUnsafePass<PageRetirementFenceRgPassData>(
                           "Nanite/PageRetirementFence",
                           out _,
                           profilingSampler))
                {
                    builder.AllowPassCulling(false);
                    builder.SetRenderFunc((PageRetirementFenceRgPassData data, UnsafeGraphContext context) =>
                    {
                        if (sceneVisibilityBackend == null ||
                            !sceneVisibilityBackend.NeedsPageRetirementFence)
                        {
                            return;
                        }

                        CommandBuffer nativeCommandBuffer =
                            CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                        GraphicsFence fence = nativeCommandBuffer.CreateGraphicsFence(
                            GraphicsFenceType.AsyncQueueSynchronisation,
                            SynchronisationStageFlags.AllGPUOperations);
                        sceneVisibilityBackend.AttachPageRetirementFence(fence);
                    });
                }
                return;
            }

            if (passKind == PassKind.WriteDepth)
            {
                var depthMaterial = EnsureDepthWriteMaterial();
                if (depthMaterial == null)
                    return;

                UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
                if (!resourceData.activeDepthTexture.IsValid())
                    return;

                // Rebuild immediately before raster. The same transient index allocation
                // is also used by the earlier shadow atlas, so recording this in FirstCull
                // would allow an intervening queue to overwrite it.
                RecordIndexedDrawBuildPass(
                    renderGraph,
                    cameraData.camera,
                    kCompactSelectionFirst,
                    profilingSampler);

                using (var builder = renderGraph.AddRasterRenderPass<DepthWriteRgPassData>("Nanite/WriteDepth", out var passData, profilingSampler))
                {
                    passData.material = depthMaterial;
                    passData.camera = cameraData.camera;
                    builder.AllowPassCulling(false);
                    builder.AllowGlobalStateModification(true);
                    builder.SetRenderAttachmentDepth(resourceData.activeDepthTexture, AccessFlags.ReadWrite);

                    builder.SetRenderFunc((DepthWriteRgPassData data, RasterGraphContext context) =>
                    {
                        ExecuteWriteDepth(context.cmd, data.material, data.camera);
                    });
                }

                RecordCopyDepthForHzb(renderGraph, frameData, cameraData, profilingSampler);
                return;
            }

            if (passKind == PassKind.WriteShadow)
            {
                var shadowMaterial = EnsureShadowCasterMaterial();
                if (shadowMaterial == null)
                    return;

                UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
                UniversalShadowData shadowData = frameData.Get<UniversalShadowData>();
                UniversalLightData lightData = frameData.Get<UniversalLightData>();
                UniversalRenderingData renderingData = frameData.Get<UniversalRenderingData>();
                if (!resourceData.mainShadowsTexture.IsValid())
                    return;
                if (shadowData == null || !shadowData.supportsMainLightShadows)
                    return;
                if (lightData == null || lightData.mainLightIndex < 0)
                    return;

                using (var builder = renderGraph.AddRasterRenderPass<ShadowWriteRgPassData>("Nanite/WriteShadow", out var passData, profilingSampler))
                {
                    passData.material = shadowMaterial;
                    passData.camera = cameraData.camera;
                    passData.cullResults = renderingData.cullResults;
                    passData.lightData = lightData;
                    passData.shadowData = shadowData;
                    builder.AllowPassCulling(false);
                    builder.AllowGlobalStateModification(true);
                    builder.SetRenderAttachmentDepth(resourceData.mainShadowsTexture, AccessFlags.ReadWrite);
                    builder.SetRenderFunc((ShadowWriteRgPassData data, RasterGraphContext context) =>
                    {
                        ExecuteWriteNaniteShadows(
                            context.cmd,
                            data.material,
                            data.camera,
                            ref data.cullResults,
                            data.lightData,
                            data.shadowData);
                    });
                }

                return;
            }

            if (passKind == PassKind.BuildHzb)
            {
                UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
                var state = GetOrCreateState(
                    cameraData.camera.pixelWidth,
                    cameraData.camera.pixelHeight,
                    cameraData.camera.GetInstanceID());
                TextureHandle depthSource = ResolveHzbDepthSource(resourceData, state);
                using (var builder = renderGraph.AddUnsafePass<BuildHzbRgPassData>(passName, out var passData, profilingSampler))
                {
                    passData.camera = cameraData.camera;
                    passData.depth = depthSource;

                    builder.AllowPassCulling(false);
                    builder.AllowGlobalStateModification(true);
                    if (passData.depth.IsValid())
                        builder.UseTexture(passData.depth, AccessFlags.Read);

                    builder.SetRenderFunc((BuildHzbRgPassData data, UnsafeGraphContext context) =>
                    {
                        ExecuteBuildHzb(context.cmd, data.camera, data.depth);
                    });
                }

                return;
            }

            if (passKind == PassKind.FormalVisibility)
            {
                RecordFormalVisibilityPasses(renderGraph, frameData, profilingSampler, cameraData.camera);
                return;
            }

            if (passKind == PassKind.DrawVBufferPreview)
            {
                var rasterMaterial = EnsureVBufferPreviewMaterial();
                var decodeMaterial = EnsureVBufferDecodeMaterial();
                if (rasterMaterial == null || decodeMaterial == null)
                    return;

                UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
                if (!resourceData.activeColorTexture.IsValid())
                    return;

                var vbufferDesc = renderGraph.GetTextureDesc(resourceData.activeColorTexture);
                vbufferDesc.name = "_NaniteVBufferPreview";
                vbufferDesc.format = GraphicsFormat.R32G32B32A32_SFloat;
                vbufferDesc.clearBuffer = true;
                vbufferDesc.clearColor = Color.clear;
                var vbuffer = renderGraph.CreateTexture(vbufferDesc);

                using (var builder = renderGraph.AddRasterRenderPass<VBufferPreviewRgPassData>("Nanite/VBufferRaster", out var passData, profilingSampler))
                {
                    passData.material = rasterMaterial;
                    passData.camera = cameraData.camera;
                    builder.AllowPassCulling(false);
                    builder.AllowGlobalStateModification(true);
                    builder.SetRenderAttachment(vbuffer, 0, AccessFlags.Write);
                    if (resourceData.activeDepthTexture.IsValid())
                        builder.SetRenderAttachmentDepth(resourceData.activeDepthTexture, AccessFlags.ReadWrite);

                    builder.SetRenderFunc((VBufferPreviewRgPassData data, RasterGraphContext context) =>
                    {
                        ExecuteVBufferPreview(context.cmd, data.material, data.camera);
                    });
                }

                using (var builder = renderGraph.AddRasterRenderPass<VBufferCompositeRgPassData>("Nanite/VBufferComposite", out var passData, profilingSampler))
                {
                    passData.material = decodeMaterial;
                    passData.vbuffer = vbuffer;
                    builder.AllowPassCulling(false);
                    builder.AllowGlobalStateModification(true);
                    builder.UseTexture(vbuffer, AccessFlags.Read);
                    builder.SetRenderAttachment(resourceData.activeColorTexture, 0, AccessFlags.Write);
                    if (resourceData.activeDepthTexture.IsValid())
                        builder.SetRenderAttachmentDepth(resourceData.activeDepthTexture, AccessFlags.Read);

                    builder.SetRenderFunc((VBufferCompositeRgPassData data, RasterGraphContext context) =>
                    {
                        ExecuteVBufferComposite(context.cmd, data.material, data.vbuffer);
                    });
                }

                return;
            }

            if (passKind == PassKind.DrawDebugVisualization)
            {
                var material = EnsureVBufferPreviewMaterial();
                if (material == null)
                    return;

                UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
                if (!resourceData.activeColorTexture.IsValid())
                    return;

                RecordVisibleTriangleCompactPass(renderGraph, cameraData.camera, kCompactSelectionMerged, profilingSampler);

                using (var builder = renderGraph.AddRasterRenderPass<VBufferPreviewRgPassData>("Nanite/DebugVisualization", out var passData, profilingSampler))
                {
                    passData.material = material;
                    passData.camera = cameraData.camera;
                    builder.AllowPassCulling(false);
                    builder.AllowGlobalStateModification(true);
                    builder.SetRenderAttachment(resourceData.activeColorTexture, 0, AccessFlags.Write);
                    if (resourceData.activeDepthTexture.IsValid())
                        builder.SetRenderAttachmentDepth(resourceData.activeDepthTexture, AccessFlags.ReadWrite);

                    builder.SetRenderFunc((VBufferPreviewRgPassData data, RasterGraphContext context) =>
                    {
                        ExecuteDebugVisualization(context.cmd, data.material, data.camera);
                    });
                }

                return;
            }

            bool isFirstCull = passKind == PassKind.FirstCull;
            using (var builder = renderGraph.AddUnsafePass<CullRgPassData>(passName, out var passData, profilingSampler))
            {
                passData.camera = cameraData.camera;
                builder.AllowPassCulling(false);
                // Cull 通过 ComputeShader 全局绑定；无法完全去掉。
                builder.AllowGlobalStateModification(true);

                builder.SetRenderFunc((CullRgPassData data, UnsafeGraphContext context) =>
                {
                    if (isFirstCull)
                    {
                        ExecuteFirstCull(data.camera);
                        // First visibility and its indirect draw list are consumed by shadow/depth.
                        // Build both in the same RG pass to avoid extra empty Compact passes.
                        if (IsVisibleTriangleCompactReady() && sceneVisibilityBackend != null)
                        {
                            EnsureVisibleTrianglesCompacted(
                                context.cmd,
                                data.camera,
                                firstSelections,
                                kCompactSelectionFirst);
                        }
                    }
                    else
                    {
                        ExecuteSecondCull(data.camera);
                        // M3：Compact 并入 Cull2，供后续 WriteDepth/Shadow/单次 Formal 复用。
                        if (IsVisibleTriangleCompactReady() &&
                            sceneVisibilityBackend != null &&
                            sceneVisibilityBackend.GpuVisibleMaskReady)
                        {
                            EnsureVisibleTrianglesCompacted(
                                context.cmd,
                                data.camera,
                                mergedSelections,
                                kCompactSelectionMerged);
                        }
                    }
                });
            }
        }

        void InitFormalKernels()
        {
            kernelMaterialTileClassify = -1;
            keywordCompactVBufferClassifyInitialized = false;
            if (settings.materialTileClassifyShader == null)
                return;

            try
            {
                kernelMaterialTileClassify = settings.materialTileClassifyShader.FindKernel("CSTileClassify");
                keywordCompactVBufferClassify = new LocalKeyword(
                    settings.materialTileClassifyShader,
                    kCompactVBufferKeyword);
                keywordCompactVBufferClassifyInitialized = keywordCompactVBufferClassify.isValid;
            }
            catch
            {
                kernelMaterialTileClassify = -1;
            }
        }

        void RecordFormalVisibilityPasses(
            RenderGraph renderGraph,
            ContextContainer frameData,
            ProfilingSampler profilingSampler,
            Camera camera)
        {
            if (!IsFormalVisibilityEnabled() || camera == null)
            {
                LogFormalSkipOnce("开关未满足或 camera 为空。");
                return;
            }

            var rasterMaterial = EnsureVBufferPreviewMaterial();
            var resolveMaterial = EnsureVBufferLitResolveMaterial();
            if (rasterMaterial == null || resolveMaterial == null)
            {
                LogFormalSkipOnce(
                    "材质创建失败（Shader.Find 未命中或 shader 编译失败）。" +
                    $"PacketRaster={(Shader.Find("Nanite/VBufferPacketRaster") != null ? "found" : "missing")}, " +
                    $"LitResolve={(Shader.Find("Nanite/VBufferLitResolve") != null ? "found" : "missing")}。" +
                    "Inspector 里 Material 槽位为 None 时会自动 Shader.Find；若仍失败请检查 Console shader 报错。");
                return;
            }

            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
            if (!resourceData.activeColorTexture.IsValid())
            {
                LogFormalSkipOnce("activeColorTexture 无效（需 URP RenderGraph + 中间 Color 纹理）。");
                return;
            }

            bool hasGBuffer =
                resourceData.gBuffer != null &&
                resourceData.gBuffer.Length >= 3 &&
                resourceData.gBuffer[0].IsValid() &&
                resourceData.gBuffer[1].IsValid() &&
                resourceData.gBuffer[2].IsValid();
            if (!hasGBuffer)
            {
                LogFormalSkipOnce(
                    "GBuffer 句柄无效。Formal 仅支持 Deferred/Deferred+ 且事件须在 GBuffer Pass 之后、" +
                    "DeferredLights 之前（建议 AfterRenderingGbuffer + offset 1~9）。");
                return;
            }

            if (!loggedFormalRecordSuccess)
            {
                loggedFormalRecordSuccess = true;
                Debug.Log(
                    $"[Nanite][RF] Formal RenderGraph 子 Pass 已注册：event={(int)passFormalVisibility.renderPassEvent}, " +
                    $"tileClassify={(IsMaterialTileCullingAdmitted() && settings.materialTileClassifyShader != null ? "on" : "off")}。");
            }

            var colorDesc = renderGraph.GetTextureDesc(resourceData.activeColorTexture);
            int fullScreenWidth = Mathf.Max(1, colorDesc.width);
            int fullScreenHeight = Mathf.Max(1, colorDesc.height);
            // 半分辨率路径尚未稳定（采样/深度 upsample 易花屏）；强制走全分辨率恢复正确画面。
            bool halfResVBuffer = false;
            int formalScreenWidth = halfResVBuffer ? Mathf.Max(1, fullScreenWidth / 2) : fullScreenWidth;
            int formalScreenHeight = halfResVBuffer ? Mathf.Max(1, fullScreenHeight / 2) : fullScreenHeight;

            var vbufferDesc = colorDesc;
            vbufferDesc.name = "_NaniteFormalVBuffer";
            bool useCompactVBuffer = CanUseCompactFormalVBuffer();
            vbufferDesc.format = useCompactVBuffer
                ? GraphicsFormat.R32G32_UInt
                : GraphicsFormat.R32G32B32A32_SFloat;
            vbufferDesc.width = formalScreenWidth;
            vbufferDesc.height = formalScreenHeight;
            vbufferDesc.filterMode = FilterMode.Point;
            vbufferDesc.clearBuffer = true;
            vbufferDesc.clearColor = Color.clear;
            var vbuffer = renderGraph.CreateTexture(vbufferDesc);

            TextureHandle rasterDepth = resourceData.activeDepthTexture;
            if (halfResVBuffer && resourceData.activeDepthTexture.IsValid())
            {
                var halfDepthDesc = renderGraph.GetTextureDesc(resourceData.activeDepthTexture);
                halfDepthDesc.name = "_NaniteFormalHalfDepth";
                halfDepthDesc.width = formalScreenWidth;
                halfDepthDesc.height = formalScreenHeight;
                halfDepthDesc.clearBuffer = true;
                rasterDepth = renderGraph.CreateTexture(halfDepthDesc);
            }

            if (kernelMaterialTileClassify < 0)
                InitFormalKernels();
            // 半分辨率下 tile 尺寸与全屏不一致，暂禁用。
            bool canTileClassify = !halfResVBuffer &&
                                   IsMaterialTileCullingAdmitted() &&
                                   settings.materialTileClassifyShader != null &&
                                   kernelMaterialTileClassify >= 0 &&
                                   (!useCompactVBuffer || keywordCompactVBufferClassifyInitialized);
            BufferHandle tileMaterialMaskHandle = default;
            BufferHandle tileIndirectArgsHandle = default;
            if (canTileClassify)
            {
                EnsureTileBuffers(formalScreenWidth, formalScreenHeight);
                canTileClassify = tileMaterialMaskBuffer != null && tileIndirectArgsBuffer != null;
                if (canTileClassify)
                {
                    tileMaterialMaskHandle = renderGraph.ImportBuffer(tileMaterialMaskBuffer);
                    tileIndirectArgsHandle = renderGraph.ImportBuffer(tileIndirectArgsBuffer);
                }
            }

            WarnFormalDebugMeshConflictOnce();

            bool hasGBufferDepthSlice =
                resourceData.gBuffer != null &&
                resourceData.gBuffer.Length > kGBufferDepthSliceIndex &&
                resourceData.gBuffer[kGBufferDepthSliceIndex].IsValid();

            // 半分辨率必须 DepthFill upsample 到相机深度；全分辨率默认跳过（Raster 已写深度，Merge 写 Stencil）。
            bool needDepthFill = resourceData.activeDepthTexture.IsValid() &&
                                 (halfResVBuffer || settings.enableFormalDepthFill);

            // 双次 Formal 仅在「HZB + prev 滤波」真正启用时才有意义；否则纯增 Compact/Raster 成本且易破洞。
            bool bevyDualRaster = settings.enableBevyFormalDualRaster &&
                                  IsBevyTwoPhaseEnabled() &&
                                  IsHzbActiveForScene() &&
                                  settings.enablePrevVisiblePass1Filter &&
                                  sceneVisibilityBackend != null &&
                                  sceneVisibilityBackend.GpuVisibleMaskReady &&
                                  sceneVisibilityBackend.Pass1ClusterVisibleBuffer != null &&
                                  sceneVisibilityBackend.Pass2ClusterVisibleBuffer != null &&
                                  batchedCulling != null;

            if (bevyDualRaster)
            {
                RecordCopyClusterMaskPass(
                    renderGraph,
                    sceneVisibilityBackend.Pass1ClusterVisibleBuffer,
                    sceneVisibilityBackend.ClusterVisibleBuffer,
                    sceneVisibilityBackend.ClusterCount,
                    profilingSampler,
                    "Nanite/CopyPass1Mask");
                RecordVisibleTriangleCompactPass(renderGraph, camera, kCompactSelectionFirst, profilingSampler);
                RecordFormalRasterPass(
                    renderGraph,
                    profilingSampler,
                    "Nanite/VisibilityBufferRaster1",
                    rasterMaterial,
                    camera,
                    vbuffer,
                    rasterDepth,
                    formalScreenWidth,
                    formalScreenHeight,
                    kCompactSelectionFirst,
                    clearColorHint: true);

                RecordCopyClusterMaskPass(
                    renderGraph,
                    sceneVisibilityBackend.Pass2ClusterVisibleBuffer,
                    sceneVisibilityBackend.ClusterVisibleBuffer,
                    sceneVisibilityBackend.ClusterCount,
                    profilingSampler,
                    "Nanite/CopyPass2Mask");
                RecordVisibleTriangleCompactPass(renderGraph, camera, kCompactSelectionPass2, profilingSampler);
                RecordFormalRasterPass(
                    renderGraph,
                    profilingSampler,
                    "Nanite/VisibilityBufferRaster2",
                    rasterMaterial,
                    camera,
                    vbuffer,
                    rasterDepth,
                    formalScreenWidth,
                    formalScreenHeight,
                    kCompactSelectionPass2,
                    clearColorHint: false);

                RecordRestoreMergedVisibleMaskPass(renderGraph, profilingSampler);
            }
            else
            {
                // With no Cull2, FirstCull's mask is already the final mask. Reuse its
                // compact list instead of relabelling and dispatching the same compact job twice.
                int formalCompactSelection = ShouldEnqueueSecondCull()
                    ? kCompactSelectionMerged
                    : kCompactSelectionFirst;
                RecordFormalRasterPass(
                    renderGraph,
                    profilingSampler,
                    "Nanite/VisibilityBufferRaster",
                    rasterMaterial,
                    camera,
                    vbuffer,
                    rasterDepth,
                    formalScreenWidth,
                    formalScreenHeight,
                    formalCompactSelection,
                    clearColorHint: true);
            }

            if (canTileClassify)
            {
                using (var builder = renderGraph.AddComputePass<FormalTileClassifyRgPassData>("Nanite/MaterialTileClassify", out var passData, profilingSampler))
                {
                    passData.camera = camera;
                    passData.vbuffer = vbuffer;
                    passData.screenWidth = formalScreenWidth;
                    passData.screenHeight = formalScreenHeight;
                    passData.tileMaterialMask = tileMaterialMaskHandle;
                    builder.AllowPassCulling(false);
                    builder.AllowGlobalStateModification(true);
                    builder.UseTexture(vbuffer, AccessFlags.Read);
                    builder.UseBuffer(passData.tileMaterialMask, AccessFlags.Write);
                    builder.SetRenderFunc((FormalTileClassifyRgPassData data, ComputeGraphContext context) =>
                    {
                        ExecuteFormalTileClassify(context.cmd, data.camera, data.vbuffer, data.screenWidth, data.screenHeight);
                    });
                }
            }

            if (needDepthFill)
            {
                using (var builder = renderGraph.AddRasterRenderPass<FormalResolveRgPassData>("Nanite/DepthFill", out var passData, profilingSampler))
                {
                    passData.material = resolveMaterial;
                    passData.camera = camera;
                    passData.vbuffer = vbuffer;
                    passData.useGBufferDepthSlice = false;
                    passData.screenWidth = fullScreenWidth;
                    passData.screenHeight = fullScreenHeight;
                    passData.vbufferWidth = formalScreenWidth;
                    passData.vbufferHeight = formalScreenHeight;
                    builder.AllowPassCulling(false);
                    builder.AllowGlobalStateModification(true);
                    builder.UseTexture(vbuffer, AccessFlags.Read);
                    builder.SetRenderAttachmentDepth(resourceData.activeDepthTexture, AccessFlags.ReadWrite);
                    builder.SetRenderFunc((FormalResolveRgPassData data, RasterGraphContext context) =>
                    {
                        ExecuteFormalDepthFill(
                            context.cmd,
                            data.material,
                            data.camera,
                            data.vbuffer,
                            data.screenWidth,
                            data.screenHeight,
                            data.vbufferWidth,
                            data.vbufferHeight);
                    });
                }
            }

            using (var builder = renderGraph.AddRasterRenderPass<FormalResolveRgPassData>("Nanite/MaterialResolveGBufferMerge", out var passData, profilingSampler))
            {
                passData.material = resolveMaterial;
                passData.camera = camera;
                passData.vbuffer = vbuffer;
                passData.writeToGBuffer = true;
                passData.useGBufferDepthSlice = hasGBufferDepthSlice;
                // 只能读取本视图、本帧确实执行过 classify 的 mask；仅检查 buffer 是否存在会读到陈旧数据。
                passData.useTileMaterialMask = canTileClassify;
                passData.tileMaterialMask = tileMaterialMaskHandle;
                passData.tileIndirectArgs = tileIndirectArgsHandle;
                passData.screenWidth = fullScreenWidth;
                passData.screenHeight = fullScreenHeight;
                passData.vbufferWidth = formalScreenWidth;
                passData.vbufferHeight = formalScreenHeight;
                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);
                builder.UseTexture(vbuffer, AccessFlags.Read);
                if (passData.useTileMaterialMask)
                {
                    builder.UseBuffer(passData.tileMaterialMask, AccessFlags.Read);
                    builder.UseBuffer(passData.tileIndirectArgs, AccessFlags.Read);
                }

                for (int i = 0; i < resourceData.gBuffer.Length; i++)
                {
                    if (resourceData.gBuffer[i].IsValid())
                        builder.SetRenderAttachment(resourceData.gBuffer[i], i, AccessFlags.Write);
                }
                // Merge pass 的 Shader 会写 Stencil(MaterialLit=32)，需要绑定 depth-stencil 附件才能真正生效。
                // 否则 Deferred Lighting 会把像素当 Unlit，仅保留 RT3(emission/GI)。
                if (resourceData.activeDepthTexture.IsValid())
                    builder.SetRenderAttachmentDepth(resourceData.activeDepthTexture, AccessFlags.ReadWrite);

                builder.SetRenderFunc((FormalResolveRgPassData data, RasterGraphContext context) =>
                {
                    ExecuteFormalMaterialResolve(
                        context.cmd,
                        data.material,
                        data.camera,
                        data.vbuffer,
                        data.useGBufferDepthSlice,
                        data.useTileMaterialMask,
                        data.screenWidth,
                        data.screenHeight,
                        data.vbufferWidth,
                        data.vbufferHeight);
                });
            }

            // 不能 SetGlobalTextureAfterPass(activeDepthTexture)：与后续 Forward Only 等 Pass 的 depth attachment 冲突。
            // 必须拷到独立的 cameraDepthTexture（URP 在 GBuffer 后已 Copy 一次，Resolve 更新 depth 后需再同步）。
            RecordPostResolveDepthCopy(renderGraph, frameData, profilingSampler);
        }

        void RecordPostResolveDepthCopy(RenderGraph renderGraph, ContextContainer frameData, ProfilingSampler profilingSampler)
        {
            var copyMaterial = EnsureCopyDepthMaterial();
            if (copyMaterial == null)
                return;

            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
            if (!resourceData.activeDepthTexture.IsValid())
                return;

            TextureHandle destination = resourceData.cameraDepthTexture;
            if (!destination.IsValid())
            {
                var depthCopyDesc = renderGraph.GetTextureDesc(resourceData.activeDepthTexture);
                depthCopyDesc.name = "Nanite_PostResolveDepthCopy";
                depthCopyDesc.format = GraphicsFormat.R32_SFloat;
                depthCopyDesc.depthBufferBits = 0;
                depthCopyDesc.clearBuffer = false;
                destination = renderGraph.CreateTexture(depthCopyDesc);
            }

            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            using (var builder = renderGraph.AddRasterRenderPass<CopyDepthRgPassData>("Nanite/CopyDepthAfterResolve", out var passData, profilingSampler))
            {
                passData.copyMaterial = copyMaterial;
                passData.source = resourceData.activeDepthTexture;
                passData.destination = destination;
                passData.cameraData = cameraData;
                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);
                builder.UseTexture(passData.source, AccessFlags.Read);
                builder.SetRenderAttachment(passData.destination, 0, AccessFlags.WriteAll);
                if (destination.IsValid())
                    builder.SetGlobalTextureAfterPass(destination, ShaderIds.CameraDepthTexture);

                builder.SetRenderFunc((CopyDepthRgPassData data, RasterGraphContext context) =>
                {
                    ExecuteCopyDepthBlit(context.cmd, data);
                });
            }
        }

        void ExecuteCopyDepthBlit(RasterCommandBuffer cmd, CopyDepthRgPassData data)
        {
            if (cmd == null || data.copyMaterial == null || !data.source.IsValid())
                return;

            ConfigureCopyDepthShaderKeywords(cmd);
            data.copyMaterial.SetTexture(ShaderIds.CameraDepthAttachment, data.source);
            data.copyMaterial.SetFloat(ShaderIds.ZWrite, 0f);
            Blitter.BlitTexture(cmd, new Vector4(1f, 1f, 0f, 0f), data.copyMaterial, 0);
        }

        void ExecuteFormalVisibility(ScriptableRenderContext context, Camera camera)
        {
            if (!IsFormalVisibilityEnabled() || camera == null)
                return;

            if (!loggedFormalOnce)
            {
                loggedFormalOnce = true;
                Debug.Log("[Nanite][RF] Formal VisibilityBuffer 路径启用（推荐 RenderGraph Deferred）。");
            }
        }

        void ExecutePageRetirementFence(ScriptableRenderContext context)
        {
            if (sceneVisibilityBackend == null ||
                !sceneVisibilityBackend.NeedsPageRetirementFence)
            {
                return;
            }

            CommandBuffer cmd = CommandBufferPool.Get("Nanite Page Retirement Fence");
            GraphicsFence fence = cmd.CreateGraphicsFence(
                GraphicsFenceType.AsyncQueueSynchronisation,
                SynchronisationStageFlags.AllGPUOperations);
            sceneVisibilityBackend.AttachPageRetirementFence(fence);
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        void WarnFormalDebugMeshConflictOnce()
        {
            if (loggedFormalDebugMeshWarning)
                return;
            var proxies = NaniteRuntimeRegistry.ActiveProxies;
            for (int i = 0; i < proxies.Count; i++)
            {
                var proxy = proxies[i];
                if (proxy != null && proxy.isActiveAndEnabled && proxy.renderVisibleMesh)
                {
                    loggedFormalDebugMeshWarning = true;
                    Debug.LogWarning(
                        "[Nanite][RF] Formal 路径下建议关闭 Proxy 的 renderVisibleMesh。" +
                        "Debug Mesh 会写入 GBuffer 深度，与 VBufferFormal 的 LEqual 深度测试不一致，导致 VBuffer/Resolve 只在近处/边缘偶现。");
                    return;
                }
            }
        }

        void SuppressProxyDebugRenderersIfNeeded()
        {
            if (!IsFormalVisibilityEnabled())
                return;

            var proxies = NaniteRuntimeRegistry.ActiveProxies;
            for (int i = 0; i < proxies.Count; i++)
            {
                var proxy = proxies[i];
                if (proxy == null || !proxy.isActiveAndEnabled || proxy.RasterFallbackActive)
                    continue;

                // Formal 已接管着色；关掉 Debug MeshRenderer，避免 URP GBuffer/阴影再画一遍。
                if (proxy.renderVisibleMesh)
                    proxy.renderVisibleMesh = false;

                var mr = proxy.GetComponent<MeshRenderer>();
                if (mr != null && mr.enabled)
                {
                    mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    mr.enabled = false;
                }
            }
        }

        void RecordFormalRasterPass(
            RenderGraph renderGraph,
            ProfilingSampler profilingSampler,
            string passName,
            Material rasterMaterial,
            Camera camera,
            TextureHandle vbuffer,
            TextureHandle rasterDepth,
            int formalScreenWidth,
            int formalScreenHeight,
            int compactSelectionKey,
            bool clearColorHint)
        {
            _ = clearColorHint;
            RecordIndexedDrawBuildPass(renderGraph, camera, compactSelectionKey, profilingSampler);
            using (var builder = renderGraph.AddRasterRenderPass<FormalRasterRgPassData>(passName, out var passData, profilingSampler))
            {
                passData.material = rasterMaterial;
                passData.camera = camera;
                passData.screenWidth = formalScreenWidth;
                passData.screenHeight = formalScreenHeight;
                passData.compactSelectionKey = compactSelectionKey;
                builder.AllowPassCulling(false);
                // Raster 用 MPB/全局 buffer 绑定；仍需改全局 shader 属性。
                builder.AllowGlobalStateModification(true);
                // 禁止 WriteAll：WriteAll = Write|Discard，未覆盖像素不会 Clear，VBuffer 会跨帧残留。
                // Raster2 追加写时依赖 Load（首用 clearBuffer 清屏）。
                builder.SetRenderAttachment(vbuffer, 0, AccessFlags.Write);
                if (rasterDepth.IsValid())
                    builder.SetRenderAttachmentDepth(rasterDepth, AccessFlags.ReadWrite);

                builder.SetRenderFunc((FormalRasterRgPassData data, RasterGraphContext context) =>
                {
                    ExecuteFormalVisibilityRaster(
                        context.cmd,
                        data.material,
                        data.camera,
                        data.screenWidth,
                        data.screenHeight,
                        data.compactSelectionKey);
                });
            }
        }

        void RecordCopyClusterMaskPass(
            RenderGraph renderGraph,
            ComputeBuffer src,
            ComputeBuffer dst,
            int count,
            ProfilingSampler profilingSampler,
            string passName)
        {
            if (src == null || dst == null || count <= 0 || batchedCulling == null)
                return;

            using (var builder = renderGraph.AddUnsafePass<CopyClusterMaskRgPassData>(passName, out var passData, profilingSampler))
            {
                passData.src = src;
                passData.dst = dst;
                passData.count = count;
                builder.AllowPassCulling(false);
                // ComputeShader.SetBuffer 仍属全局状态。
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc((CopyClusterMaskRgPassData data, UnsafeGraphContext context) =>
                {
                    batchedCulling.DispatchCopyUintBuffer(data.src, data.dst, data.count);
                });
            }
        }

        void RecordRestoreMergedVisibleMaskPass(RenderGraph renderGraph, ProfilingSampler profilingSampler)
        {
            if (sceneVisibilityBackend == null || batchedCulling == null)
                return;

            using (var builder = renderGraph.AddUnsafePass<CopyClusterMaskRgPassData>("Nanite/RestoreMergedMask", out var passData, profilingSampler))
            {
                passData.src = sceneVisibilityBackend.Pass1ClusterVisibleBuffer;
                passData.dst = sceneVisibilityBackend.ClusterVisibleBuffer;
                passData.count = sceneVisibilityBackend.ClusterCount;
                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc((CopyClusterMaskRgPassData data, UnsafeGraphContext context) =>
                {
                    batchedCulling.DispatchOrMasksToVisible(
                        sceneVisibilityBackend.Pass1ClusterVisibleBuffer,
                        sceneVisibilityBackend.Pass2ClusterVisibleBuffer,
                        sceneVisibilityBackend.ClusterVisibleBuffer,
                        sceneVisibilityBackend.ClusterCount);
                });
            }
        }

        void ExecuteFormalVisibilityRaster(
            RasterCommandBuffer cmd,
            Material material,
            Camera camera,
            int screenWidth,
            int screenHeight,
            int compactSelectionKey = kCompactSelectionMerged)
        {
            if (cmd == null || material == null || camera == null)
                return;
            var rasterMode = (FormalRasterizationMode)Mathf.Clamp(settings.formalRasterizationMode, 0, (int)FormalRasterizationMode.HybridReserved);
            if (rasterMode != FormalRasterizationMode.HardwareOnly && !loggedHybridRasterWarning)
            {
                loggedHybridRasterWarning = true;
                Debug.LogWarning("[Nanite][RF] Hybrid soft/hard 光栅尚未打通，当前回退 HardwareOnly。");
            }
            if (!TryPrepareSceneVisibility(camera))
                return;
            if (!HasValidCompactForSelection(compactSelectionKey))
                sceneVisibilityBackend.ResetDrawArgsToFullMesh();
            if (!sceneVisibilityBackend.TryGetSceneBuffers(
                    out var vertexDataBuffer,
                    out var indexBuffer,
                    out var triangleClusterBuffer,
                    out var trianglePageBuffer,
                    out var triangleInstanceBuffer,
                    out var triangleSubMeshBuffer,
                    out var clusterVisibleBuffer,
                    out var instanceLocalToWorldBuffer,
                    out var instanceSubMeshMaterialBuffer,
                    out var drawArgsBuffer,
                    out int vertexStride,
                    out int _))
                return;

            BindFormalRasterSceneBuffers(
                cmd,
                vertexDataBuffer,
                indexBuffer,
                triangleClusterBuffer,
                triangleSubMeshBuffer,
                triangleInstanceBuffer,
                instanceLocalToWorldBuffer,
                clusterVisibleBuffer);
            BindPackedPageGeometry(cmd);
            BindFormalRasterUniforms(
                cmd,
                vertexStride,
                sceneVisibilityBackend.TriangleCount,
                sceneVisibilityBackend.InstanceCount,
                sceneVisibilityBackend.MaxSubMeshCount);
            BindCompactDrawState(cmd, compactSelectionKey);
            bool useCompactVBuffer = CanUseCompactFormalVBuffer();
            CoreUtils.SetKeyword(material, kCompactVBufferKeyword, useCompactVBuffer);
            const int kFormalRasterPass = 1;
            var indexedMpb = GetOrCreateVBufferPropertyBlock();
            indexedMpb.Clear();
            if (!TryDrawIndexedCameraQueue(
                    cmd,
                    material,
                    kFormalRasterPass,
                    compactSelectionKey,
                    indexedMpb))
            {
                indexedMpb.SetInt(ShaderIds.UseIndexedClusterRaster, 0);
                cmd.DrawProceduralIndirect(
                    Matrix4x4.identity,
                    material,
                    kFormalRasterPass,
                    MeshTopology.Triangles,
                    drawArgsBuffer,
                    0,
                    indexedMpb);
            }

            if (!loggedFormalRasterStats)
            {
                loggedFormalRasterStats = true;
                string sel = compactSelectionKey == kCompactSelectionPass2
                    ? "pass2"
                    : (compactSelectionKey == kCompactSelectionFirst ? "pass1" : "merged");
                Debug.Log(
                    $"[Nanite][RF] Formal VBuffer raster: geometryTriangles={sceneVisibilityBackend.TriangleCount}, " +
                    $"sceneInstances={sceneVisibilityBackend.InstanceCount}, " +
                    $"submit={(HasValidIndexedDraw(compactSelectionKey) ? "indexedClusterIndirect" : (lastCompactUsedDirectQueue ? "cullQueueIndirect" : "compactIndirect"))}, selection={sel}, " +
                    $"format={(useCompactVBuffer ? "RG32UI" : "RGBA32F")}, " +
                    $"geometrySource={(sceneVisibilityBackend.UsePackedPageRaster ? "residentCache" : "compatDecoded")}, " +
                    $"residentAddress={(sceneVisibilityBackend.UsePackedPageRaster ? "directAbsoluteIndex" : "n/a")}, " +
                    $"indexedCapacity=camera:{sceneVisibilityBackend.IndexedDrawClusterCapacity},shadow:{sceneVisibilityBackend.IndexedShadowClusterCapacity}," +
                    $"bufferMiB:{sceneVisibilityBackend.IndexedDrawBufferBytes / (1024f * 1024f):F1}, " +
                    $"bevyDual={settings.enableBevyFormalDualRaster}, pass=VBufferFormal");
            }
        }

        void ExecuteFormalTileClassify(ComputeCommandBuffer cmd, Camera camera, TextureHandle vbuffer, int screenWidth, int screenHeight)
        {
            if (!settings.enableMaterialTileCulling || cmd == null || camera == null || !vbuffer.IsValid())
                return;
            if (settings.materialTileClassifyShader == null)
                return;
            if (kernelMaterialTileClassify < 0)
                InitFormalKernels();
            if (kernelMaterialTileClassify < 0)
                return;
            if (!TryPrepareSceneVisibility(camera))
                return;
            if (!sceneVisibilityBackend.TryGetSceneBuffers(
                    out _,
                    out _,
                    out _,
                    out _,
                    out _,
                    out var triangleSubMeshBuffer,
                    out _,
                    out _,
                    out var instanceSubMeshMaterialBuffer,
                    out _,
                    out _,
                    out _))
                return;

            EnsureTileBuffers(screenWidth, screenHeight);
            if (tileMaterialMaskBuffer == null || tileCount <= 0)
                return;

            cmd.SetComputeIntParam(settings.materialTileClassifyShader, ShaderIds.MaxSubMeshCount, sceneVisibilityBackend.MaxSubMeshCount);
            cmd.SetComputeIntParam(settings.materialTileClassifyShader, ShaderIds.TriangleCount, sceneVisibilityBackend.TriangleCount);
            cmd.SetComputeIntParam(settings.materialTileClassifyShader, ShaderIds.InstanceCount, sceneVisibilityBackend.InstanceCount);
            cmd.SetComputeIntParam(settings.materialTileClassifyShader, ShaderIds.MaterialCount, sceneVisibilityBackend.MaterialCount);
            cmd.SetComputeIntParam(settings.materialTileClassifyShader, ShaderIds.TileCountX, tileCountX);
            cmd.SetComputeIntParam(settings.materialTileClassifyShader, ShaderIds.TileCountY, tileCountY);
            cmd.SetComputeIntParam(settings.materialTileClassifyShader, ShaderIds.TileSize, Mathf.Max(1, settings.materialTileSize));
            cmd.SetComputeIntParam(settings.materialTileClassifyShader, ShaderIds.UseNormalizedIds, ComputeFormalVBufferIdMode(sceneVisibilityBackend.TriangleCount));
            cmd.SetComputeVectorParam(settings.materialTileClassifyShader, ShaderIds.ScreenSize, new Vector4(screenWidth, screenHeight, 0, 0));
            cmd.SetComputeTextureParam(settings.materialTileClassifyShader, kernelMaterialTileClassify, ShaderIds.NaniteVBufferTex, vbuffer);
            cmd.SetComputeBufferParam(settings.materialTileClassifyShader, kernelMaterialTileClassify, ShaderIds.TriangleSubMesh, triangleSubMeshBuffer);
            cmd.SetComputeBufferParam(settings.materialTileClassifyShader, kernelMaterialTileClassify, ShaderIds.InstanceSubMeshMaterial, instanceSubMeshMaterialBuffer);
            cmd.SetComputeBufferParam(settings.materialTileClassifyShader, kernelMaterialTileClassify, ShaderIds.TileMaterialMask, tileMaterialMaskBuffer);
            if (keywordCompactVBufferClassifyInitialized)
            {
                cmd.SetKeyword(
                    settings.materialTileClassifyShader,
                    keywordCompactVBufferClassify,
                    CanUseCompactFormalVBuffer());
            }
            cmd.DispatchCompute(settings.materialTileClassifyShader, kernelMaterialTileClassify, tileCountX, tileCountY, 1);
        }

        const string kCompactVBufferKeyword = "NANITE_COMPACT_VBUFFER";
        const string kPassDepthFill = "NaniteDepthFill";
        const string kPassGBufferMerge = "NaniteGBufferMerge";

        const int kResolvePassDepthFillFallback = 0;
        const int kResolvePassGBufferMergeFallback = 1;

        bool IsMaterialTileCullingAdmitted()
        {
            if (!settings.enableMaterialTileCulling)
                return false;
            if (!settings.adaptiveMaterialTileCulling)
                return true;
            if (sceneVisibilityBackend == null || !sceneVisibilityBackend.IsReady)
                return false;

            int minMaterials = Mathf.Max(1, settings.materialTileMinMaterialCount);
            return sceneVisibilityBackend.MaterialCount >= minMaterials;
        }

        bool CanUseCompactFormalVBuffer()
        {
            if (!settings.enableCompactFormalVBuffer)
                return false;

            const GraphicsFormat format = GraphicsFormat.R32G32_UInt;
            // Integer VBuffer reads use Texture2D<uint2>.Load, not filtered sampling.
            // Requiring GraphicsFormatUsage.Sample rejects valid DX12 integer RTV/SRV formats.
            bool supported = SystemInfo.IsFormatSupported(format, GraphicsFormatUsage.Render);
            if (!supported && !loggedCompactVBufferUnsupported)
            {
                loggedCompactVBufferUnsupported = true;
                Debug.LogWarning(
                    "[Nanite][RF] R32G32_UINT 不支持 Render，Formal VBuffer 回退 RGBA32F。");
            }

            return supported;
        }

        static int GetFormalResolvePassIndex(Material material, string passName, int fallback)
        {
            if (material == null || material.shader == null)
                return fallback;
            int passIndex = material.FindPass(passName);
            return passIndex >= 0 ? passIndex : fallback;
        }

        bool TryBindFormalResolveScene(
            RasterCommandBuffer cmd,
            Camera camera,
            TextureHandle vbuffer,
            int screenWidth,
            int screenHeight,
            int vbufferWidth,
            int vbufferHeight,
            out int vertexStride)
        {
            vertexStride = 0;
            if (cmd == null || camera == null || !vbuffer.IsValid())
                return false;
            if (!TryPrepareSceneVisibility(camera))
                return false;
            if (!sceneVisibilityBackend.TryGetSceneBuffers(
                    out var vertexDataBuffer,
                    out var indexBuffer,
                    out var triangleClusterBuffer,
                    out var trianglePageBuffer,
                    out var triangleInstanceBuffer,
                    out var triangleSubMeshBuffer,
                    out var clusterVisibleBuffer,
                    out var instanceLocalToWorldBuffer,
                    out var instanceSubMeshMaterialBuffer,
                    out _,
                    out vertexStride,
                    out _))
                return false;

            EnsureTileBuffers(screenWidth, screenHeight);
            cmd.SetGlobalTexture(ShaderIds.NaniteVBufferTex, vbuffer);
            BindFormalResolveSceneBuffers(
                cmd,
                vertexDataBuffer,
                indexBuffer,
                triangleInstanceBuffer,
                triangleSubMeshBuffer,
                instanceLocalToWorldBuffer,
                instanceSubMeshMaterialBuffer,
                sceneVisibilityBackend.InstanceShBuffer,
                tileMaterialMaskBuffer,
                triangleClusterBuffer,
                trianglePageBuffer,
                clusterVisibleBuffer);
            BindPackedPageGeometry(cmd);
            BindFormalResolveSharedUniforms(cmd, screenWidth, screenHeight, vbufferWidth, vbufferHeight, vertexStride);
            return true;
        }

        void ExecuteFormalDepthFill(
            RasterCommandBuffer cmd,
            Material material,
            Camera camera,
            TextureHandle vbuffer,
            int screenWidth,
            int screenHeight,
            int vbufferWidth,
            int vbufferHeight)
        {
            if (material == null ||
                !TryBindFormalResolveScene(cmd, camera, vbuffer, screenWidth, screenHeight, vbufferWidth, vbufferHeight, out _))
                return;

            ConfigureFormalResolveMaterialKeywords(material, false, CanUseCompactFormalVBuffer());
            cmd.SetGlobalFloat(ShaderIds.ResolveMaterialId, 0f);
            cmd.DrawProcedural(
                Matrix4x4.identity,
                material,
                GetFormalResolvePassIndex(material, kPassDepthFill, kResolvePassDepthFillFallback),
                MeshTopology.Triangles,
                3,
                1);
        }

        void ExecuteFormalMaterialResolve(
            RasterCommandBuffer cmd,
            Material material,
            Camera camera,
            TextureHandle vbuffer,
            bool useGBufferDepthSlice,
            bool useTileMaterialMask,
            int screenWidth,
            int screenHeight,
            int vbufferWidth,
            int vbufferHeight)
        {
            using var profilerScope = kResolveSubmitMarker.Auto();
            if (material == null ||
                !TryBindFormalResolveScene(cmd, camera, vbuffer, screenWidth, screenHeight, vbufferWidth, vbufferHeight, out _))
                return;

            bool useCompactVBuffer = CanUseCompactFormalVBuffer();
            ConfigureFormalResolveMaterialKeywords(
                material,
                useGBufferDepthSlice,
                useCompactVBuffer);

            bool useTileMask =
                useTileMaterialMask &&
                settings.enableMaterialTileCulling &&
                settings.materialTileClassifyShader != null &&
                tileMaterialMaskBuffer != null &&
                tileIndirectArgsBuffer != null &&
                tileCount > 0;
            cmd.SetGlobalFloat(ShaderIds.UseTileMaterialMask, useTileMask ? 1f : 0f);

            int drawCount = 0;
            int resolvePass = GetFormalResolvePassIndex(material, kPassGBufferMerge, kResolvePassGBufferMergeFallback);
            for (int materialId = 0; materialId < sceneVisibilityBackend.MaterialCount; materialId++)
            {
                if (materialId >= sceneVisibilityBackend.MaxMaterialCount)
                    break;

                var sourceMaterial = sceneVisibilityBackend.GetMaterial(materialId);
                BindFormalResolveMaterialBatch(cmd, material, sourceMaterial, materialId);
                if (useTileMask)
                {
                    cmd.DrawProceduralIndirect(
                        Matrix4x4.identity,
                        material,
                        resolvePass,
                        MeshTopology.Triangles,
                        tileIndirectArgsBuffer,
                        0);
                }
                else
                {
                    cmd.DrawProcedural(
                        Matrix4x4.identity,
                        material,
                        resolvePass,
                        MeshTopology.Triangles,
                        3,
                        1);
                }
                drawCount++;
            }

            RecordFormalPerfSample(1, drawCount, sceneVisibilityBackend.MaterialCount, 1f);

            if (!loggedFormalOnce && drawCount > 0)
            {
                loggedFormalOnce = true;
                Debug.Log(
                    $"[Nanite][RF] Formal Resolve: depthFill={(settings.enableFormalDepthFill ? 1 : 0)} gBufferBatches={drawCount} " +
                    $"tri={sceneVisibilityBackend.TriangleCount} inst={sceneVisibilityBackend.InstanceCount} " +
                    $"uniqueMeshes={sceneVisibilityBackend.UniqueMeshCount} " +
                    $"sharedVertices={sceneVisibilityBackend.GeometryVertexCount}/{sceneVisibilityBackend.VirtualVertexCount} " +
                    $"sharedTriangles={sceneVisibilityBackend.TriangleCount}/{sceneVisibilityBackend.VirtualTriangleCount} " +
                    $"geometrySource={(sceneVisibilityBackend.UsePackedPageRaster ? "residentCache" : "compatDecoded")} " +
                    $"residentAddress={(sceneVisibilityBackend.UsePackedPageRaster ? "directAbsoluteIndex" : "n/a")} " +
                    $"geometryMiB=compat:{sceneVisibilityBackend.CompatibilityGeometryBytes / (1024f * 1024f):F2}," +
                    $"resident:{sceneVisibilityBackend.ResidentGeometryBytes / (1024f * 1024f):F2}," +
                    $"pool:{sceneVisibilityBackend.PagePoolBytes / (1024f * 1024f):F2}," +
                    $"payload:{sceneVisibilityBackend.ResidentPagePayloadBytes / (1024f * 1024f):F2}," +
                    $"triRef:{sceneVisibilityBackend.TrianglePageRefBytes / (1024f * 1024f):F2} " +
                    $"tileResolve={(useTileMask ? $"on({tileCountX}x{tileCountY}@{Mathf.Max(1, settings.materialTileSize)})" : "off")} " +
                    $"vbuffer={(useCompactVBuffer ? "RG32UI/full32" : $"RGBA32F/idMode{ComputeFormalVBufferIdMode(sceneVisibilityBackend.TriangleCount)}")} " +
                    $"gBufferDepthSlice={(useGBufferDepthSlice ? "on" : "off")}");
            }
        }

        bool TryPrepareSceneVisibility(Camera camera)
        {
            return TryPrepareSceneVisibility(camera, mergedSelections);
        }

        bool TryPrepareSceneVisibility(Camera camera, Dictionary<int, NaniteRuntimeSelection> selectionsByProxyId)
        {
            using var profilerScope = kScenePrepareMarker.Auto();
            if (camera == null)
                return false;
            if (selectionsByProxyId == null)
                return false;

            int cameraId = camera.GetInstanceID();
            if (sceneVisibilityBackend != null &&
                sceneVisibilityBackend.IsReady &&
                sceneVisibilityBackend.GpuVisibleMaskReady &&
                lastGpuScenePrepareFrame == Time.frameCount &&
                lastGpuScenePrepareCameraId == cameraId)
            {
                return true;
            }

            var proxies = NaniteRuntimeRegistry.ActiveProxies;
            sceneVisibilityBackend ??= new NaniteSceneVisibilityBufferBackend();
            sceneVisibilityBackend.LightProbeRefreshInterval = settings.lightProbeRefreshInterval;
            sceneVisibilityBackend.PagePoolMaxMiB = settings.pagePoolMaxMiB;
            sceneVisibilityBackend.IndexedDrawRequested = settings.enableIndexedClusterRaster;
            sceneVisibilityBackend.IndexedDrawBufferMaxMiB = settings.indexedClusterBufferMaxMiB;
            sceneVisibilityBackend.PackedPageRasterRequested = settings.enablePackedPageRaster;
            sceneVisibilityBackend.PageTranscodeShader = settings.pageTranscodeShader;
            sceneVisibilityBackend.PageStreamingRequestsEnabled = settings.enablePageStreamingUploads;
            if (!sceneVisibilityBackend.EnsureInitialized(proxies))
                return false;
            if (!sceneVisibilityBackend.UpdateVisibleClusters(selectionsByProxyId))
                return false;
            if (sceneVisibilityBackend.GpuVisibleMaskReady)
            {
                lastGpuScenePrepareFrame = Time.frameCount;
                lastGpuScenePrepareCameraId = cameraId;
            }
            return sceneVisibilityBackend.IsReady;
        }

        void EnsureTileBuffers(int width, int height)
        {
            int tileSize = Mathf.Max(1, settings.materialTileSize);
            int nextTileX = Mathf.Max(1, (Mathf.Max(1, width) + tileSize - 1) / tileSize);
            int nextTileY = Mathf.Max(1, (Mathf.Max(1, height) + tileSize - 1) / tileSize);
            int nextTileCount = Mathf.Max(1, nextTileX * nextTileY);
            bool updateIndirectArgs = tileIndirectArgsBuffer == null || tileCount != nextTileCount;

            tileCountX = nextTileX;
            tileCountY = nextTileY;
            tileCount = nextTileCount;

            if (tileMaterialMaskBuffer == null || tileMaskCapacity < nextTileCount)
            {
                tileMaterialMaskBuffer?.Release();
                tileMaskCapacity = Mathf.NextPowerOfTwo(nextTileCount);
                tileMaterialMaskBuffer = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured,
                    tileMaskCapacity,
                    sizeof(uint));
            }

            if (tileIndirectArgsBuffer == null)
                tileIndirectArgsBuffer = new GraphicsBuffer(
                    GraphicsBuffer.Target.IndirectArguments,
                    4,
                    sizeof(uint));

            if (updateIndirectArgs)
            {
                uint[] drawArgs = { 6u, (uint)nextTileCount, 0u, 0u };
                tileIndirectArgsBuffer.SetData(drawArgs);
            }
        }

        /// <summary>
        /// Formal VBuffer ID 模式：0=raw, 1=normalized(legacy), 2=split triangle high/low。
        /// 大场景（>65535 三角形）必须用 split，否则 float32 归一化/大整数会解错 triangleId。
        /// </summary>
        static int ComputeFormalVBufferIdMode(int triangleCount)
        {
            return triangleCount > 65535 ? 2 : 0;
        }

        static void BindFormalRasterSceneBuffers(
            RasterCommandBuffer cmd,
            ComputeBuffer vertexDataBuffer,
            ComputeBuffer indexBuffer,
            ComputeBuffer triangleClusterBuffer,
            ComputeBuffer triangleSubMeshBuffer,
            ComputeBuffer triangleInstanceBuffer,
            ComputeBuffer instanceLocalToWorldBuffer,
            ComputeBuffer clusterVisibleBuffer)
        {
            cmd.SetGlobalBuffer(ShaderIds.VertexData, vertexDataBuffer);
            cmd.SetGlobalBuffer(ShaderIds.Indices, indexBuffer);
            cmd.SetGlobalBuffer(ShaderIds.TriangleCluster, triangleClusterBuffer);
            cmd.SetGlobalBuffer(ShaderIds.TriangleSubMesh, triangleSubMeshBuffer);
            cmd.SetGlobalBuffer(ShaderIds.TriangleInstance, triangleInstanceBuffer);
            cmd.SetGlobalBuffer(ShaderIds.InstanceLocalToWorld, instanceLocalToWorldBuffer);
            cmd.SetGlobalBuffer(ShaderIds.ClusterVisible, clusterVisibleBuffer);
        }

        static void BindFormalRasterUniforms(
            RasterCommandBuffer cmd,
            int vertexStride,
            int triangleCount,
            int instanceCount,
            int maxSubMeshCount)
        {
            cmd.SetGlobalFloat(ShaderIds.VertexStride, Mathf.Max(3, vertexStride));
            cmd.SetGlobalFloat(ShaderIds.UseSceneInstanceBuffer, 1f);
            cmd.SetGlobalFloat(ShaderIds.HasTriangleSubMesh, 1f);
            cmd.SetGlobalFloat(ShaderIds.UseNormalizedIds, ComputeFormalVBufferIdMode(triangleCount));
            cmd.SetGlobalFloat(ShaderIds.MaxSubMeshCount, maxSubMeshCount);
            cmd.SetGlobalFloat(ShaderIds.TriangleCount, triangleCount);
            cmd.SetGlobalFloat(ShaderIds.InstanceCount, instanceCount);
        }

        static void BindFormalResolveSceneBuffers(
            RasterCommandBuffer cmd,
            ComputeBuffer vertexDataBuffer,
            ComputeBuffer indexBuffer,
            ComputeBuffer triangleInstanceBuffer,
            ComputeBuffer triangleSubMeshBuffer,
            ComputeBuffer instanceLocalToWorldBuffer,
            ComputeBuffer instanceSubMeshMaterialBuffer,
            ComputeBuffer instanceShBuffer,
            GraphicsBuffer tileMaskBuffer,
            ComputeBuffer triangleClusterBuffer,
            ComputeBuffer trianglePageBuffer,
            ComputeBuffer clusterVisibleBuffer)
        {
            cmd.SetGlobalBuffer(ShaderIds.VertexData, vertexDataBuffer);
            cmd.SetGlobalBuffer(ShaderIds.Indices, indexBuffer);
            cmd.SetGlobalBuffer(ShaderIds.TriangleInstance, triangleInstanceBuffer);
            cmd.SetGlobalBuffer(ShaderIds.TriangleSubMesh, triangleSubMeshBuffer);
            cmd.SetGlobalBuffer(ShaderIds.InstanceLocalToWorld, instanceLocalToWorldBuffer);
            cmd.SetGlobalBuffer(ShaderIds.InstanceSubMeshMaterial, instanceSubMeshMaterialBuffer);
            cmd.SetGlobalBuffer(ShaderIds.InstanceSh, instanceShBuffer);
            cmd.SetGlobalBuffer(ShaderIds.TileMaterialMask, tileMaskBuffer);
            cmd.SetGlobalBuffer(ShaderIds.TriangleCluster, triangleClusterBuffer);
            cmd.SetGlobalBuffer(ShaderIds.TrianglePage, trianglePageBuffer);
            cmd.SetGlobalBuffer(ShaderIds.ClusterVisible, clusterVisibleBuffer);
        }

        bool CanBindPackedPageGeometry() =>
            sceneVisibilityBackend != null &&
            sceneVisibilityBackend.UsePackedPageRaster &&
            sceneVisibilityBackend.ResidentVertexBuffer != null &&
            sceneVisibilityBackend.ResidentIndexBuffer != null &&
            sceneVisibilityBackend.TrianglePageRefBuffer != null &&
            (!settings.enablePackedDirectDiagnostic ||
             (sceneVisibilityBackend.PackedPagePoolBuffer != null &&
              sceneVisibilityBackend.PageDecodeBuffer != null &&
              sceneVisibilityBackend.ResidentPageTableBuffer != null));

        void BindPackedPageGeometry(RasterCommandBuffer cmd)
        {
            bool usePacked = CanBindPackedPageGeometry();
            cmd.SetGlobalFloat(ShaderIds.UsePackedPageGeometry, usePacked ? 1f : 0f);
            if (!usePacked)
                return;
            if (settings.enablePackedDirectDiagnostic)
            {
                cmd.SetGlobalFloat(ShaderIds.PackedDirectDiagnostic, 1f);
                cmd.SetGlobalBuffer(ShaderIds.PackedPagePool, sceneVisibilityBackend.PackedPagePoolBuffer);
                cmd.SetGlobalBuffer(ShaderIds.PageDecodeTable, sceneVisibilityBackend.PageDecodeBuffer);
                cmd.SetGlobalBuffer(ShaderIds.ResidentPageTable, sceneVisibilityBackend.ResidentPageTableBuffer);
            }
            cmd.SetGlobalBuffer(ShaderIds.ResidentVertices, sceneVisibilityBackend.ResidentVertexBuffer);
            cmd.SetGlobalBuffer(ShaderIds.ResidentIndices, sceneVisibilityBackend.ResidentIndexBuffer);
            cmd.SetGlobalBuffer(ShaderIds.TrianglePageRefs, sceneVisibilityBackend.TrianglePageRefBuffer);
        }

        void BindPackedPageGeometry(CommandBuffer cmd)
        {
            bool usePacked = CanBindPackedPageGeometry();
            cmd.SetGlobalFloat(ShaderIds.UsePackedPageGeometry, usePacked ? 1f : 0f);
            if (!usePacked)
                return;
            if (settings.enablePackedDirectDiagnostic)
            {
                cmd.SetGlobalFloat(ShaderIds.PackedDirectDiagnostic, 1f);
                cmd.SetGlobalBuffer(ShaderIds.PackedPagePool, sceneVisibilityBackend.PackedPagePoolBuffer);
                cmd.SetGlobalBuffer(ShaderIds.PageDecodeTable, sceneVisibilityBackend.PageDecodeBuffer);
                cmd.SetGlobalBuffer(ShaderIds.ResidentPageTable, sceneVisibilityBackend.ResidentPageTableBuffer);
            }
            cmd.SetGlobalBuffer(ShaderIds.ResidentVertices, sceneVisibilityBackend.ResidentVertexBuffer);
            cmd.SetGlobalBuffer(ShaderIds.ResidentIndices, sceneVisibilityBackend.ResidentIndexBuffer);
            cmd.SetGlobalBuffer(ShaderIds.TrianglePageRefs, sceneVisibilityBackend.TrianglePageRefBuffer);
        }

        void BindPackedPageGeometry(MaterialPropertyBlock properties)
        {
            bool usePacked = CanBindPackedPageGeometry();
            properties.SetFloat(ShaderIds.UsePackedPageGeometry, usePacked ? 1f : 0f);
            if (!usePacked)
                return;
            if (settings.enablePackedDirectDiagnostic)
            {
                properties.SetFloat(ShaderIds.PackedDirectDiagnostic, 1f);
                properties.SetBuffer(ShaderIds.PackedPagePool, sceneVisibilityBackend.PackedPagePoolBuffer);
                properties.SetBuffer(ShaderIds.PageDecodeTable, sceneVisibilityBackend.PageDecodeBuffer);
                properties.SetBuffer(ShaderIds.ResidentPageTable, sceneVisibilityBackend.ResidentPageTableBuffer);
            }
            properties.SetBuffer(ShaderIds.ResidentVertices, sceneVisibilityBackend.ResidentVertexBuffer);
            properties.SetBuffer(ShaderIds.ResidentIndices, sceneVisibilityBackend.ResidentIndexBuffer);
            properties.SetBuffer(ShaderIds.TrianglePageRefs, sceneVisibilityBackend.TrianglePageRefBuffer);
        }

        void BindFormalResolveSharedUniforms(
            RasterCommandBuffer cmd,
            int screenWidth,
            int screenHeight,
            int vbufferWidth,
            int vbufferHeight,
            int vertexStride)
        {
            float invW = screenWidth > 0 ? 1f / screenWidth : 0f;
            float invH = screenHeight > 0 ? 1f / screenHeight : 0f;
            int vbW = Mathf.Max(1, vbufferWidth > 0 ? vbufferWidth : screenWidth);
            int vbH = Mathf.Max(1, vbufferHeight > 0 ? vbufferHeight : screenHeight);
            cmd.SetGlobalVector(ShaderIds.NaniteViewInvSize, new Vector4(invW, invH, 0f, 0f));
            cmd.SetGlobalVector(ShaderIds.NaniteVBufferSize, new Vector4(vbW, vbH, 1f / vbW, 1f / vbH));
            cmd.SetGlobalFloat(ShaderIds.VertexStride, Mathf.Max(3, vertexStride));
            cmd.SetGlobalFloat(ShaderIds.MaxSubMeshCount, sceneVisibilityBackend.MaxSubMeshCount);
            cmd.SetGlobalFloat(ShaderIds.TriangleCount, sceneVisibilityBackend.TriangleCount);
            cmd.SetGlobalFloat(ShaderIds.InstanceCount, sceneVisibilityBackend.InstanceCount);
            cmd.SetGlobalFloat(ShaderIds.TileCountX, tileCountX);
            cmd.SetGlobalFloat(ShaderIds.TileSize, Mathf.Max(1, settings.materialTileSize));
            cmd.SetGlobalFloat(ShaderIds.TileCount, tileCount);
            cmd.SetGlobalFloat(ShaderIds.UseNormalizedIds, ComputeFormalVBufferIdMode(sceneVisibilityBackend.TriangleCount));
            // 具体 pass 是否有同帧有效 mask 由 ExecuteFormalMaterialResolve 显式决定。
            cmd.SetGlobalFloat(ShaderIds.UseTileMaterialMask, 0f);
        }

        void BindFormalResolveMaterialBatch(RasterCommandBuffer cmd, Material resolveMaterial, Material sourceMaterial, int materialId)
        {
            cmd.SetGlobalFloat(ShaderIds.ResolveMaterialId, materialId);

            var baseMap = ResolveTexture(
                sourceMaterial,
                "_BaseMap",
                ResolveTexture(sourceMaterial, "_MainTex", Texture2D.whiteTexture));
            var normalMap = ResolveTexture(sourceMaterial, "_BumpMap", Texture2D.normalTexture != null ? Texture2D.normalTexture : Texture2D.whiteTexture);
            var metallicGlossMap = ResolveTexture(sourceMaterial, "_MetallicGlossMap", Texture2D.blackTexture);
            var occlusionMap = ResolveTexture(sourceMaterial, "_OcclusionMap", Texture2D.whiteTexture);
            var emissionMap = ResolveTexture(sourceMaterial, "_EmissionMap", Texture2D.blackTexture);

            Color baseColor = ResolveColor(sourceMaterial, "_BaseColor", "_Color", Color.white);
            Color emissionColor = ResolveColor(sourceMaterial, "_EmissionColor", "_Color", Color.black);
            float cutoff = ResolveFloat(sourceMaterial, "_Cutoff", 0f);
            float smoothness = ResolveFloat(sourceMaterial, "_Smoothness", 0.5f);
            float metallic = ResolveFloat(sourceMaterial, "_Metallic", 0f);
            float bumpScale = ResolveFloat(sourceMaterial, "_BumpScale", 1f);
            float occlusionStrength = ResolveFloat(sourceMaterial, "_OcclusionStrength", 1f);
            float alphaClip = IsMaterialAlphaClipEnabled(sourceMaterial) ? 1f : 0f;
            float hasBaseMap = (HasMeaningfulTexture(sourceMaterial, "_BaseMap") || HasMeaningfulTexture(sourceMaterial, "_MainTex")) ? 1f : 0f;
            float hasNormalMap = HasMeaningfulTexture(sourceMaterial, "_BumpMap") ? 1f : 0f;
            float hasMetallicGlossMap = HasMeaningfulTexture(sourceMaterial, "_MetallicGlossMap") ? 1f : 0f;
            float hasOcclusionMap = HasMeaningfulTexture(sourceMaterial, "_OcclusionMap") ? 1f : 0f;
            float hasEmissionMap = HasMeaningfulTexture(sourceMaterial, "_EmissionMap") ? 1f : 0f;
            float emissionEnabled = IsMaterialEmissionEnabled(sourceMaterial) ? 1f : 0f;
            float smoothnessFromAlbedoAlpha =
                sourceMaterial != null &&
                sourceMaterial.IsKeywordEnabled("_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A")
                    ? 1f
                    : 0f;
            Vector4 baseMapST = ResolveVector(
                sourceMaterial,
                "_BaseMap_ST",
                ResolveVector(sourceMaterial, "_MainTex_ST", new Vector4(1f, 1f, 0f, 0f)));
            if (emissionEnabled < 0.5f)
                hasEmissionMap = 0f;

            if (resolveMaterial != null)
            {
                CoreUtils.SetKeyword(resolveMaterial, "_ENVIRONMENTREFLECTIONS_OFF",
                    sourceMaterial != null && sourceMaterial.IsKeywordEnabled("_ENVIRONMENTREFLECTIONS_OFF"));
                CoreUtils.SetKeyword(resolveMaterial, "_SPECULARHIGHLIGHTS_OFF",
                    sourceMaterial != null && sourceMaterial.IsKeywordEnabled("_SPECULARHIGHLIGHTS_OFF"));
                CoreUtils.SetKeyword(resolveMaterial, "_RECEIVE_SHADOWS_OFF",
                    sourceMaterial != null && sourceMaterial.IsKeywordEnabled("_RECEIVE_SHADOWS_OFF"));

                resolveMaterial.SetTexture(ShaderIds.BaseMap, baseMap);
                resolveMaterial.SetTexture(ShaderIds.BumpMap, normalMap);
                resolveMaterial.SetTexture(ShaderIds.MetallicGlossMap, metallicGlossMap);
                resolveMaterial.SetTexture(ShaderIds.OcclusionMap, occlusionMap);
                resolveMaterial.SetTexture(ShaderIds.EmissionMap, emissionMap);
                resolveMaterial.SetColor(ShaderIds.BaseColor, baseColor);
                resolveMaterial.SetColor(ShaderIds.EmissionColor, emissionColor);
                resolveMaterial.SetFloat(ShaderIds.Cutoff, cutoff);
                resolveMaterial.SetFloat(ShaderIds.AlphaClip, alphaClip);
                resolveMaterial.SetFloat(ShaderIds.Smoothness, smoothness);
                resolveMaterial.SetFloat(ShaderIds.Metallic, metallic);
                resolveMaterial.SetFloat(ShaderIds.BumpScale, bumpScale);
                resolveMaterial.SetFloat(ShaderIds.OcclusionStrength, occlusionStrength);
                resolveMaterial.SetFloat(ShaderIds.HasBaseMap, hasBaseMap);
                resolveMaterial.SetFloat(ShaderIds.HasNormalMap, hasNormalMap);
                resolveMaterial.SetFloat(ShaderIds.HasMetallicGlossMap, hasMetallicGlossMap);
                resolveMaterial.SetFloat(ShaderIds.HasOcclusionMap, hasOcclusionMap);
                resolveMaterial.SetFloat(ShaderIds.HasEmissionMap, hasEmissionMap);
                resolveMaterial.SetFloat(ShaderIds.EmissionEnabled, emissionEnabled);
                resolveMaterial.SetFloat(ShaderIds.SmoothnessFromAlbedoAlpha, smoothnessFromAlbedoAlpha);
                resolveMaterial.SetVector(ShaderIds.BaseMapST, baseMapST);
            }
        }

        static Vector4 ResolveVector(Material material, string propertyName, Vector4 fallback)
        {
            if (material != null && material.HasProperty(propertyName))
                return material.GetVector(propertyName);
            return fallback;
        }

        static bool IsMaterialEmissionEnabled(Material material)
        {
            if (material == null)
                return false;
            if (material.IsKeywordEnabled("_EMISSION"))
                return true;
            if (material.HasProperty("_EmissionEnabled") && material.GetFloat("_EmissionEnabled") > 0.5f)
                return true;
            return false;
        }

        static bool IsMaterialAlphaClipEnabled(Material material)
        {
            if (material == null)
                return false;

            if (material.IsKeywordEnabled("_ALPHATEST_ON") || material.IsKeywordEnabled("_ALPHACLIP_ON"))
                return true;

            if (material.HasProperty("_AlphaClip") && material.GetFloat("_AlphaClip") > 0.5f)
                return true;

            return false;
        }

        static void ConfigureFormalResolveMaterialKeywords(
            Material resolveMaterial,
            bool useGBufferDepthSlice,
            bool useCompactVBuffer)
        {
            if (resolveMaterial == null)
                return;

            const string depthSliceKeyword = "NANITE_GBUFFER_DEPTH_SLICE";
            const string renderPassEnabled = "_RENDER_PASS_ENABLED";
            resolveMaterial.DisableKeyword(renderPassEnabled);
            CoreUtils.SetKeyword(resolveMaterial, kCompactVBufferKeyword, useCompactVBuffer);
            if (useGBufferDepthSlice)
                resolveMaterial.EnableKeyword(depthSliceKeyword);
            else
                resolveMaterial.DisableKeyword(depthSliceKeyword);
        }

        static Texture ResolveTexture(Material material, string propertyName, Texture fallback)
        {
            if (material != null && material.HasProperty(propertyName))
            {
                var tex = material.GetTexture(propertyName);
                if (tex != null)
                    return tex;
            }

            return fallback;
        }

        static bool HasMeaningfulTexture(Material material, string propertyName)
        {
            if (material == null || !material.HasProperty(propertyName))
                return false;

            Texture texture = material.GetTexture(propertyName);
            if (texture == null)
                return false;

            if (texture == Texture2D.whiteTexture ||
                texture == Texture2D.blackTexture ||
                texture == Texture2D.grayTexture ||
                (Texture2D.normalTexture != null && texture == Texture2D.normalTexture))
            {
                return false;
            }

            return true;
        }

        static Color ResolveColor(Material material, string propertyA, string propertyB, Color fallback)
        {
            if (material != null && material.HasProperty(propertyA))
                return material.GetColor(propertyA);
            if (material != null && material.HasProperty(propertyB))
                return material.GetColor(propertyB);
            return fallback;
        }

        static float ResolveFloat(Material material, string propertyName, float fallback)
        {
            if (material != null && material.HasProperty(propertyName))
                return material.GetFloat(propertyName);
            return fallback;
        }

        void ExecuteFirstCull(Camera camera)
        {
            using var profilerScope = kFirstCullMarker.Auto();
            if (camera == null)
                return;
            EnsureInternalCollections();
            LogExecutionOnce(camera, "first-cull");
            double startMs = Time.realtimeSinceStartupAsDouble * 1000.0;

            var state = GetOrCreateState(camera.pixelWidth, camera.pixelHeight, camera.GetInstanceID());
            state.depthWrittenThisFrame = false;
            state.depthCopiedForHzb = false;
            state.currentHzbValid = false;
            // 默认不用上一帧 HZB：镜头推进时旧深度会把近处 cluster 误判遮挡，表现为破洞+闪烁。
            Texture prevHzb = (IsHzbActiveForScene() &&
                               settings.usePreviousHzbOnFirstCull &&
                               state.hasPrevious)
                ? state.previous
                : null;
            int prevMipCount = state.mipCount;

            var proxies = NaniteRuntimeRegistry.ActiveProxies;
            int activeProxies = 0;
            var cullMode = IsBevyTwoPhaseEnabled()
                ? NaniteGpuBatchedCullingBackend.CullPassMode.Pass1PrevVisible
                : NaniteGpuBatchedCullingBackend.CullPassMode.Legacy;
            // Bevy Pass1：不用 HZB（即便 usePreviousHzbOnFirstCull）。
            bool firstUseHzb = !IsBevyTwoPhaseEnabled() && prevHzb != null;
            bool gpuMask = TryRunBatchedCullGpuMask(
                camera, prevHzb, prevMipCount, firstUseHzb, clearMask: true, cullMode, out int batchableProxies);
            bool batched = gpuMask;
            if (gpuMask)
            {
                LogBatchedDispatchStatsOnce(batchableProxies, "first-cull-gpuMask", firstUseHzb);
                LogBevyCullStatsIfNeeded("cull1");
            }
            else
            {
                sceneVisibilityBackend?.ClearGpuVisibleMaskReady();
                batched = TryRunBatchedCull(camera, prevHzb, prevMipCount, prevHzb != null, firstSelections, out batchableProxies);
                if (batched)
                    LogBatchedDispatchStatsOnce(batchableProxies, "first-cull", prevHzb != null);
            }

            if (!batched)
            {
                int legacyProxies = 0;
                for (int i = 0; i < proxies.Count; i++)
                {
                    var proxy = proxies[i];
                    if (proxy == null || !proxy.isActiveAndEnabled || !proxy.NaniteRenderingActive || proxy.naniteMesh == null)
                        continue;

                    legacyProxies++;
                    var sel = GetOrCreateSelection(firstSelections, proxy.GetInstanceID());
                    proxy.TryComputeSelectionForCamera(camera, prevHzb, prevMipCount, prevHzb != null, sel);
                }

                LogDispatchStatsOnce(legacyProxies, "first-cull");
                activeProxies = legacyProxies;
            }
            else
                activeProxies = batchableProxies;

            if (settings.enablePageStreamingUploads &&
                sceneVisibilityBackend != null &&
                sceneVisibilityBackend.IsPagePoolReady)
            {
                sceneVisibilityBackend.UpdatePageStreaming(
                    Time.frameCount,
                    settings.pageRequestReadbackInterval,
                    settings.pageUploadsPerPoll);
            }

            double cpuMs = Time.realtimeSinceStartupAsDouble * 1000.0 - startMs;
            RecordPerfSample(cpuMs, true, batched, activeProxies);
        }

        void ExecuteWriteDepth(ScriptableRenderContext context, Camera camera)
        {
            if (camera == null)
                return;

            var material = EnsureDepthWriteMaterial();
            if (material == null)
                return;

            var state = GetOrCreateState(camera.pixelWidth, camera.pixelHeight, camera.GetInstanceID());
            var cmd = CommandBufferPool.Get("Nanite Write Depth");
            int drawCalls = DrawDepthFromFirstSelection(cmd, material, camera);
            if (drawCalls > 0)
            {
                state.depthWrittenThisFrame = true;
                CopyDepthForHzbLegacy(cmd, camera, state);
            }
            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();
            CommandBufferPool.Release(cmd);
        }

        void ExecuteWriteDepth(RasterCommandBuffer cmd, Material material, Camera camera)
        {
            if (camera == null || material == null)
                return;

            var state = GetOrCreateState(camera.pixelWidth, camera.pixelHeight, camera.GetInstanceID());
            int drawCalls = DrawDepthFromFirstSelection(cmd, material, camera);
            if (drawCalls > 0)
            {
                state.depthWrittenThisFrame = true;
                // RenderGraph 路径在独立 CopyDepth pass 中处理；此处仅兼容无 RG 子图时的全局深度绑定。
            }
        }

        void ExecuteWriteShadow(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            // RenderGraph 模式由 RecordRenderGraphPass(PassKind.WriteShadow) 处理。
            // 兼容路径若在 RG 相机上误触发，会污染当前 RT/全局状态，导致主视角异常。
            return;
        }

        void ExecuteWriteNaniteShadows(
            RasterCommandBuffer cmd,
            Material material,
            Camera camera,
            ref CullingResults cullResults,
            ref LightData lightData,
            ref ShadowData shadowData)
        {
            if (cmd == null || material == null || camera == null)
                return;
            if (!shadowData.supportsMainLightShadows)
                return;

            int shadowLightIndex = lightData.mainLightIndex;
            if (shadowLightIndex < 0)
                return;
            if (shadowLightIndex >= lightData.visibleLights.Length)
                return;

            VisibleLight shadowLight = lightData.visibleLights[shadowLightIndex];
            if (shadowLight.light == null || shadowLight.light.shadows == LightShadows.None)
                return;

            if (!TryPrepareSceneVisibility(camera, firstSelections))
                return;

            int renderTargetWidth = shadowData.mainLightShadowmapWidth;
            int renderTargetHeight = shadowData.mainLightShadowCascadesCount == 2
                ? shadowData.mainLightShadowmapHeight >> 1
                : shadowData.mainLightShadowmapHeight;
            int shadowResolution = ShadowUtils.GetMaxTileResolutionInAtlas(
                shadowData.mainLightShadowmapWidth,
                shadowData.mainLightShadowmapHeight,
                shadowData.mainLightShadowCascadesCount);
            float shadowNearPlane = shadowLight.light.shadowNearPlane;

            int cascadeCount = Mathf.Min(
                Mathf.Max(1, shadowData.mainLightShadowCascadesCount),
                Mathf.Clamp(settings.maxNaniteShadowCascades, 1, 4));
            for (int cascadeIndex = 0; cascadeIndex < cascadeCount; cascadeIndex++)
            {
                if (!ShadowUtils.ExtractDirectionalLightMatrix(
                        ref cullResults,
                        ref shadowData,
                        shadowLightIndex,
                        cascadeIndex,
                        renderTargetWidth,
                        renderTargetHeight,
                        shadowResolution,
                        shadowNearPlane,
                        out _,
                        out ShadowSliceData sliceData))
                    continue;

                Vector4 shadowBias = ShadowUtils.GetShadowBias(
                    ref shadowLight,
                    shadowLightIndex,
                    ref shadowData,
                    sliceData.projectionMatrix,
                    sliceData.resolution);
                SetupShadowCasterGlobals(cmd, ref shadowLight, shadowBias);

                cmd.SetGlobalDepthBias(1.0f, 2.5f);
                cmd.SetViewport(new Rect(sliceData.offsetX, sliceData.offsetY, sliceData.resolution, sliceData.resolution));
                Matrix4x4 shadowProj = GL.GetGPUProjectionMatrix(sliceData.projectionMatrix, true);
                cmd.SetGlobalMatrix(ShaderIds.NaniteShadowViewProj, shadowProj * sliceData.viewMatrix);
                DrawNaniteShadowGeometry(cmd, material, camera, cascadeIndex);
                cmd.DisableScissorRect();
                cmd.SetGlobalDepthBias(0.0f, 0.0f);
            }
        }

        void ExecuteWriteNaniteShadows(
            RasterCommandBuffer cmd,
            Material material,
            Camera camera,
            ref CullingResults cullResults,
            UniversalLightData lightData,
            UniversalShadowData shadowData)
        {
            if (cmd == null || material == null || camera == null)
                return;
            if (shadowData == null || !shadowData.supportsMainLightShadows)
                return;

            using var shadowSubmitScope = kShadowSubmitMarker.Auto();

            int shadowLightIndex = lightData.mainLightIndex;
            if (shadowLightIndex < 0 || shadowLightIndex >= lightData.visibleLights.Length)
                return;

            VisibleLight shadowLight = lightData.visibleLights[shadowLightIndex];
            if (shadowLight.light == null || shadowLight.light.shadows == LightShadows.None)
                return;

            if (!TryPrepareSceneVisibility(camera, firstSelections))
                return;

            int renderTargetWidth = shadowData.mainLightShadowmapWidth;
            int renderTargetHeight = shadowData.mainLightShadowCascadesCount == 2
                ? shadowData.mainLightShadowmapHeight >> 1
                : shadowData.mainLightShadowmapHeight;
            int shadowResolution = ShadowUtils.GetMaxTileResolutionInAtlas(
                shadowData.mainLightShadowmapWidth,
                shadowData.mainLightShadowmapHeight,
                shadowData.mainLightShadowCascadesCount);
            float shadowNearPlane = shadowLight.light.shadowNearPlane;
            int cascadeCount = Mathf.Min(
                Mathf.Max(1, shadowData.mainLightShadowCascadesCount),
                Mathf.Clamp(settings.maxNaniteShadowCascades, 1, 4));
            if (!loggedShadowExecutionOnce)
            {
                loggedShadowExecutionOnce = true;
                Debug.Log(
                    $"[Nanite][Shadow] draw active: atlas={shadowData.mainLightShadowmapWidth}x{shadowData.mainLightShadowmapHeight}, " +
                    $"cascades={cascadeCount}/{shadowData.mainLightShadowCascadesCount}, " +
                    $"queue=cameraFirstCull/{(lastCompactUsedDirectQueue ? "direct" : "compact")}, " +
                    $"instances={(sceneVisibilityBackend != null ? sceneVisibilityBackend.InstanceCount : 0)}. " +
                    "Dedicated shadow-frustum queue is the next optimization gate.");
            }
            for (int cascadeIndex = 0; cascadeIndex < cascadeCount; cascadeIndex++)
            {
                if (!ShadowUtils.ExtractDirectionalLightMatrix(
                        ref cullResults,
                        shadowData,
                        shadowLightIndex,
                        cascadeIndex,
                        renderTargetWidth,
                        renderTargetHeight,
                        shadowResolution,
                        shadowNearPlane,
                        out _,
                        out ShadowSliceData sliceData))
                    continue;

                Vector4 shadowBias = ShadowUtils.GetShadowBias(
                    ref shadowLight,
                    shadowLightIndex,
                    shadowData,
                    sliceData.projectionMatrix,
                    sliceData.resolution);
                SetupShadowCasterGlobals(cmd, ref shadowLight, shadowBias);

                cmd.SetGlobalDepthBias(1.0f, 2.5f);
                cmd.SetViewport(new Rect(sliceData.offsetX, sliceData.offsetY, sliceData.resolution, sliceData.resolution));
                Matrix4x4 shadowProj = GL.GetGPUProjectionMatrix(sliceData.projectionMatrix, true);
                cmd.SetGlobalMatrix(ShaderIds.NaniteShadowViewProj, shadowProj * sliceData.viewMatrix);
                DrawNaniteShadowGeometry(cmd, material, camera, cascadeIndex);
                cmd.DisableScissorRect();
                cmd.SetGlobalDepthBias(0.0f, 0.0f);
            }
        }

        static void SetupShadowCasterGlobals(RasterCommandBuffer cmd, ref VisibleLight shadowLight, Vector4 shadowBias)
        {
            cmd.SetGlobalVector(ShaderIds.ShadowBias, shadowBias);
            Vector3 lightDirection = -shadowLight.localToWorldMatrix.GetColumn(2);
            cmd.SetGlobalVector(ShaderIds.LightDirection, new Vector4(lightDirection.x, lightDirection.y, lightDirection.z, 0.0f));
            Vector3 lightPosition = shadowLight.localToWorldMatrix.GetColumn(3);
            cmd.SetGlobalVector(ShaderIds.LightPosition, new Vector4(lightPosition.x, lightPosition.y, lightPosition.z, 1.0f));
        }

        bool TryDrawIndexedCameraQueue(
            RasterCommandBuffer cmd,
            Material material,
            int shaderPass,
            int selectionKey,
            MaterialPropertyBlock mpb)
        {
            if (cmd == null || material == null || mpb == null || !HasValidIndexedDraw(selectionKey))
                return false;
            GraphicsBuffer indices = sceneVisibilityBackend.IndexedDrawIndexBuffer;
            GraphicsBuffer indexedArgs = sceneVisibilityBackend.IndexedDrawArgsBuffer;
            GraphicsBuffer fallbackArgs = sceneVisibilityBackend.IndexedFallbackDrawArgsBuffer;
            if (indices == null || indexedArgs == null || fallbackArgs == null)
                return false;

            mpb.SetInt(ShaderIds.UseIndexedClusterRaster, 1);
            mpb.SetInt(ShaderIds.GeometryVertexCount, sceneVisibilityBackend.GeometryVertexCount);
            cmd.DrawProceduralIndirect(
                indices,
                Matrix4x4.identity,
                material,
                shaderPass,
                MeshTopology.Triangles,
                indexedArgs,
                0,
                mpb);
            mpb.SetInt(ShaderIds.UseIndexedClusterRaster, 0);
            cmd.DrawProceduralIndirect(
                Matrix4x4.identity,
                material,
                shaderPass,
                MeshTopology.Triangles,
                fallbackArgs,
                0,
                mpb);
            return true;
        }

        bool TryDrawIndexedShadowQueue(
            RasterCommandBuffer cmd,
            Material material,
            int shaderPass,
            int cascadeIndex,
            MaterialPropertyBlock mpb)
        {
            if (!settings.enableIndexedClusterRaster || cmd == null || material == null || mpb == null ||
                sceneVisibilityBackend == null || !sceneVisibilityBackend.IndexedDrawAvailable)
                return false;
            GraphicsBuffer indices = sceneVisibilityBackend.IndexedDrawIndexBuffer;
            GraphicsBuffer indexedArgs = sceneVisibilityBackend.GetIndexedShadowDrawArgsBuffer(cascadeIndex);
            GraphicsBuffer fallbackArgs = sceneVisibilityBackend.GetIndexedShadowFallbackDrawArgsBuffer(cascadeIndex);
            if (indices == null || indexedArgs == null || fallbackArgs == null)
                return false;

            mpb.SetInt(ShaderIds.UseIndexedClusterRaster, 1);
            mpb.SetInt(ShaderIds.GeometryVertexCount, sceneVisibilityBackend.GeometryVertexCount);
            cmd.DrawProceduralIndirect(
                indices,
                Matrix4x4.identity,
                material,
                shaderPass,
                MeshTopology.Triangles,
                indexedArgs,
                0,
                mpb);
            mpb.SetInt(ShaderIds.UseIndexedClusterRaster, 0);
            cmd.DrawProceduralIndirect(
                Matrix4x4.identity,
                material,
                shaderPass,
                MeshTopology.Triangles,
                fallbackArgs,
                0,
                mpb);
            return true;
        }

        void DrawNaniteShadowGeometry(
            RasterCommandBuffer cmd,
            Material material,
            Camera camera,
            int cascadeIndex)
        {
            if (!sceneVisibilityBackend.TryGetSceneBuffers(
                    out var vertexDataBuffer,
                    out var indexBuffer,
                    out var triangleClusterBuffer,
                    out var trianglePageBuffer,
                    out var triangleInstanceBuffer,
                    out var triangleSubMeshBuffer,
                    out var clusterVisibleBuffer,
                    out var instanceLocalToWorldBuffer,
                    out var instanceSubMeshMaterialBuffer,
                    out var drawArgsBuffer,
                    out int vertexStride,
                    out _))
                return;

            var mpb = GetOrCreateVBufferPropertyBlock();
            mpb.Clear();
            mpb.SetBuffer(ShaderIds.VertexData, vertexDataBuffer);
            mpb.SetBuffer(ShaderIds.Indices, indexBuffer);
            mpb.SetBuffer(ShaderIds.TriangleCluster, triangleClusterBuffer);
            mpb.SetBuffer(ShaderIds.TrianglePage, trianglePageBuffer);
            mpb.SetBuffer(ShaderIds.TriangleInstance, triangleInstanceBuffer);
            mpb.SetBuffer(ShaderIds.TriangleSubMesh, triangleSubMeshBuffer);
            mpb.SetBuffer(ShaderIds.ClusterVisible, clusterVisibleBuffer);
            mpb.SetBuffer(ShaderIds.InstanceLocalToWorld, instanceLocalToWorldBuffer);
            mpb.SetBuffer(ShaderIds.InstanceSubMeshMaterial, instanceSubMeshMaterialBuffer);
            BindPackedPageGeometry(mpb);
            mpb.SetInt(ShaderIds.VertexStride, Mathf.Max(3, vertexStride));
            mpb.SetInt(ShaderIds.UseSceneInstanceBuffer, 1);
            mpb.SetInt(ShaderIds.HasTriangleSubMesh, 1);
            mpb.SetInt(ShaderIds.InstanceId, 0);
            mpb.SetMatrix(ShaderIds.LocalToWorld, Matrix4x4.identity);
            GraphicsBuffer activeDrawArgs = drawArgsBuffer;
            bool useShadowQueue =
                batchedCulling != null &&
                batchedCulling.IsShadowDrawQueueReady(camera, cascadeIndex) &&
                batchedCulling.GetShadowDrawClusterBuffer(cascadeIndex) != null &&
                batchedCulling.GetShadowDrawArgsBuffer(cascadeIndex) != null;
            if (useShadowQueue)
            {
                mpb.SetFloat(ShaderIds.UseCompactedTriIds, 1f);
                mpb.SetFloat(ShaderIds.UseDirectVisibleDrawQueue, 1f);
                mpb.SetBuffer(
                    ShaderIds.CompactedDrawClusters,
                    batchedCulling.GetShadowDrawClusterBuffer(cascadeIndex));
                mpb.SetBuffer(ShaderIds.CompactedTriIds, sceneVisibilityBackend.CompactedTriIdsBuffer);
                mpb.SetBuffer(
                    ShaderIds.CompactedTriInstances,
                    sceneVisibilityBackend.CompactedTriInstancesBuffer);
                mpb.SetBuffer(
                    ShaderIds.CompactedTriCounts,
                    sceneVisibilityBackend.CompactedTriCountsBuffer);
                mpb.SetFloat(
                    ShaderIds.CompactedClusterTriangleSlots,
                    sceneVisibilityBackend.CompactedClusterTriangleSlots);
                activeDrawArgs = batchedCulling.GetShadowDrawArgsBuffer(cascadeIndex);
            }
            else
            {
                BindCompactDrawState(mpb, kCompactSelectionFirst);
            }

            if (!(useShadowQueue && TryDrawIndexedShadowQueue(cmd, material, 0, cascadeIndex, mpb)))
            {
                mpb.SetInt(ShaderIds.UseIndexedClusterRaster, 0);
                cmd.DrawProceduralIndirect(
                    Matrix4x4.identity,
                    material,
                    0,
                    MeshTopology.Triangles,
                    activeDrawArgs,
                    0,
                    mpb);
            }
        }

        int DrawDepthFromFirstSelection(CommandBuffer cmd, Material material, Camera camera)
        {
            int sceneDraw = DrawDepthFromFirstSelectionScene(cmd, material, camera);
            if (sceneDraw > 0)
                return sceneDraw;

            var mpb = GetOrCreateVBufferPropertyBlock();
            var proxies = NaniteRuntimeRegistry.ActiveProxies;
            int drawCalls = 0;
            for (int i = 0; i < proxies.Count; i++)
            {
                var proxy = proxies[i];
                if (proxy == null || !proxy.isActiveAndEnabled || !proxy.NaniteRenderingActive || proxy.naniteMesh == null)
                    continue;

                var first = GetOrCreateSelection(firstSelections, proxy.GetInstanceID());
                if (first.visibleClusters == null || first.visibleClusters.Count == 0)
                    continue;
                if (!proxy.EnsurePageGpuBuffers())
                    continue;

                proxy.UpdateMergedClusterVisibilityBuffer(first);
                if (!proxy.TryGetMergedGpuData(
                        out var vertexDataBuffer,
                        out var indexBuffer,
                        out var triangleClusterBuffer,
                        out var trianglePageBuffer,
                        out var clusterVisibleBuffer,
                        out int vertexStride,
                        out int indexCount,
                        out _,
                        out int clusterCount))
                    continue;
                if (indexCount <= 0 || clusterCount <= 0)
                    continue;

                int instanceId = first.packets != null && first.packets.Count > 0 ? first.packets[0].instanceId : proxy.GetInstanceID();
                mpb.Clear();
                mpb.SetBuffer(ShaderIds.VertexData, vertexDataBuffer);
                mpb.SetBuffer(ShaderIds.Indices, indexBuffer);
                mpb.SetBuffer(ShaderIds.TriangleCluster, triangleClusterBuffer);
                mpb.SetBuffer(ShaderIds.TrianglePage, trianglePageBuffer);
                mpb.SetBuffer(ShaderIds.ClusterVisible, clusterVisibleBuffer);
                mpb.SetInt(ShaderIds.VertexStride, Mathf.Max(3, vertexStride));
                mpb.SetInt(ShaderIds.InstanceId, instanceId);
                mpb.SetMatrix(ShaderIds.LocalToWorld, proxy.transform.localToWorldMatrix);
                mpb.SetInt(ShaderIds.UseSceneInstanceBuffer, 0);
                mpb.SetFloat(ShaderIds.UsePackedPageGeometry, 0f);
                mpb.SetInt(ShaderIds.UseNormalizedIds, 0);
                mpb.SetInt(ShaderIds.TriangleCount, Mathf.Max(1, indexCount / 3));
                mpb.SetInt(ShaderIds.InstanceCount, 1);
                mpb.SetInt(ShaderIds.MaxSubMeshCount, Mathf.Max(1, proxy.naniteMesh != null ? proxy.naniteMesh.subMeshCount : 1));
                mpb.SetFloat(ShaderIds.UseCompactedTriIds, 0f);
                if (proxy.TryGetMergedTriangleSubMeshBuffer(out var triangleSubMeshBuffer))
                {
                    mpb.SetBuffer(ShaderIds.TriangleSubMesh, triangleSubMeshBuffer);
                    mpb.SetInt(ShaderIds.HasTriangleSubMesh, 1);
                }
                else
                {
                    mpb.SetInt(ShaderIds.HasTriangleSubMesh, 0);
                }

                if (settings.enableIndirectPreviewDraw && proxy.TryGetMergedIndirectArgsBuffer(out var indirectArgs))
                    cmd.DrawProceduralIndirect(Matrix4x4.identity, material, 0, MeshTopology.Triangles, indirectArgs, 0, mpb);
                else
                    cmd.DrawProcedural(Matrix4x4.identity, material, 0, MeshTopology.Triangles, indexCount, 1, mpb);
                drawCalls++;
            }

            return drawCalls;
        }

        int DrawDepthFromFirstSelection(RasterCommandBuffer cmd, Material material, Camera camera)
        {
            int sceneDraw = DrawDepthFromFirstSelectionScene(cmd, material, camera);
            if (sceneDraw > 0)
                return sceneDraw;

            var mpb = GetOrCreateVBufferPropertyBlock();
            var proxies = NaniteRuntimeRegistry.ActiveProxies;
            int drawCalls = 0;
            for (int i = 0; i < proxies.Count; i++)
            {
                var proxy = proxies[i];
                if (proxy == null || !proxy.isActiveAndEnabled || !proxy.NaniteRenderingActive || proxy.naniteMesh == null)
                    continue;

                var first = GetOrCreateSelection(firstSelections, proxy.GetInstanceID());
                if (first.visibleClusters == null || first.visibleClusters.Count == 0)
                    continue;
                if (!proxy.EnsurePageGpuBuffers())
                    continue;

                proxy.UpdateMergedClusterVisibilityBuffer(first);
                if (!proxy.TryGetMergedGpuData(
                        out var vertexDataBuffer,
                        out var indexBuffer,
                        out var triangleClusterBuffer,
                        out var trianglePageBuffer,
                        out var clusterVisibleBuffer,
                        out int vertexStride,
                        out int indexCount,
                        out _,
                        out int clusterCount))
                    continue;
                if (indexCount <= 0 || clusterCount <= 0)
                    continue;

                int instanceId = first.packets != null && first.packets.Count > 0 ? first.packets[0].instanceId : proxy.GetInstanceID();
                mpb.Clear();
                mpb.SetBuffer(ShaderIds.VertexData, vertexDataBuffer);
                mpb.SetBuffer(ShaderIds.Indices, indexBuffer);
                mpb.SetBuffer(ShaderIds.TriangleCluster, triangleClusterBuffer);
                mpb.SetBuffer(ShaderIds.TrianglePage, trianglePageBuffer);
                mpb.SetBuffer(ShaderIds.ClusterVisible, clusterVisibleBuffer);
                mpb.SetInt(ShaderIds.VertexStride, Mathf.Max(3, vertexStride));
                mpb.SetInt(ShaderIds.InstanceId, instanceId);
                mpb.SetMatrix(ShaderIds.LocalToWorld, proxy.transform.localToWorldMatrix);
                mpb.SetInt(ShaderIds.UseSceneInstanceBuffer, 0);
                mpb.SetFloat(ShaderIds.UsePackedPageGeometry, 0f);
                mpb.SetInt(ShaderIds.UseNormalizedIds, 0);
                mpb.SetInt(ShaderIds.TriangleCount, Mathf.Max(1, indexCount / 3));
                mpb.SetInt(ShaderIds.InstanceCount, 1);
                mpb.SetInt(ShaderIds.MaxSubMeshCount, Mathf.Max(1, proxy.naniteMesh != null ? proxy.naniteMesh.subMeshCount : 1));
                mpb.SetFloat(ShaderIds.UseCompactedTriIds, 0f);
                if (proxy.TryGetMergedTriangleSubMeshBuffer(out var triangleSubMeshBuffer))
                {
                    mpb.SetBuffer(ShaderIds.TriangleSubMesh, triangleSubMeshBuffer);
                    mpb.SetInt(ShaderIds.HasTriangleSubMesh, 1);
                }
                else
                {
                    mpb.SetInt(ShaderIds.HasTriangleSubMesh, 0);
                }

                if (settings.enableIndirectPreviewDraw && proxy.TryGetMergedIndirectArgsBuffer(out var indirectArgs))
                    cmd.DrawProceduralIndirect(Matrix4x4.identity, material, 0, MeshTopology.Triangles, indirectArgs, 0, mpb);
                else
                    cmd.DrawProcedural(Matrix4x4.identity, material, 0, MeshTopology.Triangles, indexCount, 1, mpb);
                drawCalls++;
            }

            return drawCalls;
        }

        int DrawDepthFromFirstSelectionScene(CommandBuffer cmd, Material material, Camera camera)
        {
            if (!settings.enableSceneIndirectDepthWrite || cmd == null || material == null || camera == null)
                return 0;
            if (!EnsureVisibleTrianglesCompacted(cmd, camera, firstSelections, kCompactSelectionFirst))
                return 0;
            if (!sceneVisibilityBackend.TryGetSceneBuffers(
                    out var vertexDataBuffer,
                    out var indexBuffer,
                    out var triangleClusterBuffer,
                    out var trianglePageBuffer,
                    out var triangleInstanceBuffer,
                    out var triangleSubMeshBuffer,
                    out var clusterVisibleBuffer,
                    out var instanceLocalToWorldBuffer,
                    out var instanceSubMeshMaterialBuffer,
                    out var drawArgsBuffer,
                    out int vertexStride,
                    out _))
                return 0;

            var mpb = GetOrCreateVBufferPropertyBlock();
            mpb.Clear();
            mpb.SetBuffer(ShaderIds.VertexData, vertexDataBuffer);
            mpb.SetBuffer(ShaderIds.Indices, indexBuffer);
            mpb.SetBuffer(ShaderIds.TriangleCluster, triangleClusterBuffer);
            mpb.SetBuffer(ShaderIds.TrianglePage, trianglePageBuffer);
            mpb.SetBuffer(ShaderIds.TriangleInstance, triangleInstanceBuffer);
            mpb.SetBuffer(ShaderIds.TriangleSubMesh, triangleSubMeshBuffer);
            mpb.SetBuffer(ShaderIds.ClusterVisible, clusterVisibleBuffer);
            mpb.SetBuffer(ShaderIds.InstanceLocalToWorld, instanceLocalToWorldBuffer);
            mpb.SetBuffer(ShaderIds.InstanceSubMeshMaterial, instanceSubMeshMaterialBuffer);
            BindPackedPageGeometry(mpb);
            mpb.SetInt(ShaderIds.VertexStride, Mathf.Max(3, vertexStride));
            mpb.SetInt(ShaderIds.UseSceneInstanceBuffer, 1);
            mpb.SetInt(ShaderIds.HasTriangleSubMesh, 1);
            mpb.SetInt(ShaderIds.UseNormalizedIds, 0);
            mpb.SetInt(ShaderIds.MaxSubMeshCount, sceneVisibilityBackend.MaxSubMeshCount);
            mpb.SetInt(ShaderIds.TriangleCount, sceneVisibilityBackend.TriangleCount);
            mpb.SetInt(ShaderIds.InstanceCount, sceneVisibilityBackend.InstanceCount);
            mpb.SetInt(ShaderIds.InstanceId, 0);
            mpb.SetMatrix(ShaderIds.LocalToWorld, Matrix4x4.identity);
            BindCompactDrawState(mpb, kCompactSelectionFirst);
            cmd.DrawProceduralIndirect(Matrix4x4.identity, material, 0, MeshTopology.Triangles, drawArgsBuffer, 0, mpb);
            return 1;
        }

        int DrawDepthFromFirstSelectionScene(RasterCommandBuffer cmd, Material material, Camera camera)
        {
            if (!settings.enableSceneIndirectDepthWrite || cmd == null || material == null || camera == null)
                return 0;
            // RG 路径：compact 已在前置 UnsafePass 完成；此处仅确保可见性就绪并绑定。
            if (!TryPrepareSceneVisibility(camera, firstSelections))
                return 0;
            if (!HasValidCompactForSelection(kCompactSelectionFirst))
                sceneVisibilityBackend.ResetDrawArgsToFullMesh();
            if (!sceneVisibilityBackend.TryGetSceneBuffers(
                    out var vertexDataBuffer,
                    out var indexBuffer,
                    out var triangleClusterBuffer,
                    out var trianglePageBuffer,
                    out var triangleInstanceBuffer,
                    out var triangleSubMeshBuffer,
                    out var clusterVisibleBuffer,
                    out var instanceLocalToWorldBuffer,
                    out var instanceSubMeshMaterialBuffer,
                    out var drawArgsBuffer,
                    out int vertexStride,
                    out _))
                return 0;

            var mpb = GetOrCreateVBufferPropertyBlock();
            mpb.Clear();
            mpb.SetBuffer(ShaderIds.VertexData, vertexDataBuffer);
            mpb.SetBuffer(ShaderIds.Indices, indexBuffer);
            mpb.SetBuffer(ShaderIds.TriangleCluster, triangleClusterBuffer);
            mpb.SetBuffer(ShaderIds.TrianglePage, trianglePageBuffer);
            mpb.SetBuffer(ShaderIds.TriangleInstance, triangleInstanceBuffer);
            mpb.SetBuffer(ShaderIds.TriangleSubMesh, triangleSubMeshBuffer);
            mpb.SetBuffer(ShaderIds.ClusterVisible, clusterVisibleBuffer);
            mpb.SetBuffer(ShaderIds.InstanceLocalToWorld, instanceLocalToWorldBuffer);
            mpb.SetBuffer(ShaderIds.InstanceSubMeshMaterial, instanceSubMeshMaterialBuffer);
            BindPackedPageGeometry(mpb);
            mpb.SetInt(ShaderIds.VertexStride, Mathf.Max(3, vertexStride));
            mpb.SetInt(ShaderIds.UseSceneInstanceBuffer, 1);
            mpb.SetInt(ShaderIds.HasTriangleSubMesh, 1);
            mpb.SetInt(ShaderIds.UseNormalizedIds, 0);
            mpb.SetInt(ShaderIds.MaxSubMeshCount, sceneVisibilityBackend.MaxSubMeshCount);
            mpb.SetInt(ShaderIds.TriangleCount, sceneVisibilityBackend.TriangleCount);
            mpb.SetInt(ShaderIds.InstanceCount, sceneVisibilityBackend.InstanceCount);
            mpb.SetInt(ShaderIds.InstanceId, 0);
            mpb.SetMatrix(ShaderIds.LocalToWorld, Matrix4x4.identity);
            BindCompactDrawState(mpb, kCompactSelectionFirst);
            if (!TryDrawIndexedCameraQueue(cmd, material, 0, kCompactSelectionFirst, mpb))
            {
                mpb.SetInt(ShaderIds.UseIndexedClusterRaster, 0);
                cmd.DrawProceduralIndirect(
                    Matrix4x4.identity,
                    material,
                    0,
                    MeshTopology.Triangles,
                    drawArgsBuffer,
                    0,
                    mpb);
            }
            return 1;
        }

        void ExecuteBuildHzb(ScriptableRenderContext context, Camera camera)
        {
            if (camera == null)
                return;

            var state = GetOrCreateState(camera.pixelWidth, camera.pixelHeight, camera.GetInstanceID());
            if (state.current == null || settings.hzbBuilderShader == null || kernelCopyDepth < 0 || kernelDownsample < 0)
                return;

            var cmd = CommandBufferPool.Get("Nanite Build HZB");
            BuildCurrentHzb(cmd, state);
            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();
            CommandBufferPool.Release(cmd);
        }

        void ExecuteBuildHzb(UnsafeCommandBuffer cmd, Camera camera, TextureHandle depthTexture)
        {
            if (camera == null)
                return;

            var state = GetOrCreateState(camera.pixelWidth, camera.pixelHeight, camera.GetInstanceID());
            if (state.current == null || settings.hzbBuilderShader == null || kernelCopyDepth < 0 || kernelDownsample < 0)
                return;

            BuildCurrentHzb(cmd, state, depthTexture);
        }

        void ExecuteSecondCull(Camera camera)
        {
            if (camera == null)
                return;
            EnsureInternalCollections();
            LogExecutionOnce(camera, "second-cull");
            double startMs = Time.realtimeSinceStartupAsDouble * 1000.0;

            var state = GetOrCreateState(camera.pixelWidth, camera.pixelHeight, camera.GetInstanceID());
            bool useCurrentHzb = IsHzbActiveForScene() &&
                                 state.current != null &&
                                 state.builtFrame == Time.frameCount &&
                                 state.currentHzbValid;
            Texture currentHzb = useCurrentHzb ? state.current : null;
            int mipCount = state.mipCount;

            var proxies = NaniteRuntimeRegistry.ActiveProxies;
            bool usedGpuMaskFirst = sceneVisibilityBackend != null && sceneVisibilityBackend.GpuVisibleMaskReady;
            bool gpuMask = false;
            int batchableProxies = 0;
            if (usedGpuMaskFirst)
            {
                // Bevy Pass2：仅 secondPassCandidates + HZB；legacy：clearMask=false OR 全量。
                var cullMode = IsBevyTwoPhaseEnabled()
                    ? NaniteGpuBatchedCullingBackend.CullPassMode.Pass2CandidatesHzb
                    : NaniteGpuBatchedCullingBackend.CullPassMode.Legacy;
                bool secondUseHzb = currentHzb != null && IsHzbActiveForScene();
                gpuMask = TryRunBatchedCullGpuMask(
                    camera, currentHzb, mipCount, secondUseHzb, clearMask: false, cullMode, out batchableProxies);
                if (gpuMask)
                {
                    LogBatchedDispatchStatsOnce(batchableProxies, "second-cull-gpuMask", secondUseHzb);
                    LogBevyCullStatsIfNeeded("cull2");
                    if (IsBevyTwoPhaseEnabled() &&
                        sceneVisibilityBackend.Pass1ClusterVisibleBuffer != null &&
                        sceneVisibilityBackend.Pass2ClusterVisibleBuffer != null)
                    {
                        // 合并 Pass1∪Pass2 → clusterVisible（Pass2 已 OR 进 visible；再 OR 确保与 pass1 备份一致）。
                        batchedCulling.DispatchOrMasksToVisible(
                            sceneVisibilityBackend.Pass1ClusterVisibleBuffer,
                            sceneVisibilityBackend.Pass2ClusterVisibleBuffer,
                            sceneVisibilityBackend.ClusterVisibleBuffer,
                            sceneVisibilityBackend.ClusterCount);
                    }

                    SwapPrevVisibleAfterFrame();
                }
                else if (settings.logStats)
                    Debug.LogWarning("[Nanite][RF] SecondCull GPU mask 失败，本帧保持 FirstCull mask（跳过 CPU readback 回退）。");
            }

            bool batched = gpuMask;
            if (!usedGpuMaskFirst)
            {
                batched = TryRunBatchedCull(camera, currentHzb, mipCount, currentHzb != null, secondSelections, out batchableProxies);
                if (batched)
                    LogBatchedDispatchStatsOnce(batchableProxies, "second-cull", currentHzb != null);
            }

            int activeProxies = usedGpuMaskFirst
                ? (batchableProxies > 0
                    ? batchableProxies
                    : (batchedCulling != null ? batchedCulling.InstanceCount : 0))
                : 0;
            if (!usedGpuMaskFirst)
            {
                for (int i = 0; i < proxies.Count; i++)
                {
                    var proxy = proxies[i];
                    if (proxy == null || !proxy.isActiveAndEnabled || !proxy.NaniteRenderingActive || proxy.naniteMesh == null)
                        continue;

                    activeProxies++;
                    int id = proxy.GetInstanceID();
                    var first = GetOrCreateSelection(firstSelections, id);
                    var second = GetOrCreateSelection(secondSelections, id);
                    var merged = GetOrCreateSelection(mergedSelections, id);

                if (usedGpuMaskFirst)
                {
                    // GPU mask 已写入 scene clusterVisible；不再 ApplyExternalSelection（避免 RenderGraph 中途 SetData）。
                }
                else
                {
                    if (!batched)
                        proxy.TryComputeSelectionForCamera(camera, currentHzb, mipCount, currentHzb != null, second);

                    MergeSelections(proxy.naniteMesh, id, first, second, merged);
                    merged.instanceLocalToWorld = proxy.transform.localToWorldMatrix;
                    proxy.ApplyExternalSelection(merged);
                }

                    if (settings.logStats && Time.frameCount % 30 == 0)
                    {
                        Debug.Log(
                            $"[Nanite][RF] cam={camera.name} proxy={proxy.name} " +
                            $"first={first.stats.visibleClusters} second={second.stats.visibleClusters} merged={merged.stats.visibleClusters} | " +
                            $"batched={batched} gpuMask={gpuMask} hzbValid={state.currentHzbValid} depthWritten={state.depthWrittenThisFrame} useHzb={currentHzb != null} | " +
                            $"first[test inst={first.stats.testedInstances},node={first.stats.testedNodes},part={first.stats.testedParts},cluster={first.stats.testedClusters}] " +
                            $"second[test inst={second.stats.testedInstances},node={second.stats.testedNodes},part={second.stats.testedParts},cluster={second.stats.testedClusters}] " +
                            $"merged[test inst={merged.stats.testedInstances},node={merged.stats.testedNodes},part={merged.stats.testedParts},cluster={merged.stats.testedClusters}]");
                    }
                }
            }

            if (!batched)
                LogDispatchStatsOnce(activeProxies, "second-cull");
            double cpuMs = Time.realtimeSinceStartupAsDouble * 1000.0 - startMs;
            RecordPerfSample(cpuMs, false, batched, activeProxies);
            SwapHistory(state);
        }

        bool TryRunBatchedCullGpuMask(
            Camera camera,
            Texture hzbTexture,
            int hzbMipCount,
            bool useHzb,
            bool clearMask,
            out int batchableProxyCount)
        {
            return TryRunBatchedCullGpuMask(
                camera,
                hzbTexture,
                hzbMipCount,
                useHzb,
                clearMask,
                NaniteGpuBatchedCullingBackend.CullPassMode.Legacy,
                out batchableProxyCount);
        }

        bool TryRunBatchedCullGpuMask(
            Camera camera,
            Texture hzbTexture,
            int hzbMipCount,
            bool useHzb,
            bool clearMask,
            NaniteGpuBatchedCullingBackend.CullPassMode cullPassMode,
            out int batchableProxyCount)
        {
            batchableProxyCount = 0;
            if (camera == null)
                return false;

            var proxies = NaniteRuntimeRegistry.ActiveProxies;
            var shader = ResolveCullingShader(proxies);
            if (shader == null)
                return false;

            batchableProxyCount = CountBatchableProxies(proxies);
            if (batchableProxyCount <= 0)
                return false;

            sceneVisibilityBackend ??= new NaniteSceneVisibilityBufferBackend();
            sceneVisibilityBackend.LightProbeRefreshInterval = settings.lightProbeRefreshInterval;
            sceneVisibilityBackend.PagePoolMaxMiB = settings.pagePoolMaxMiB;
            sceneVisibilityBackend.PackedPageRasterRequested = settings.enablePackedPageRaster;
            sceneVisibilityBackend.PageTranscodeShader = settings.pageTranscodeShader;
            sceneVisibilityBackend.PageStreamingRequestsEnabled = settings.enablePageStreamingUploads;
            if (!sceneVisibilityBackend.EnsureInitialized(proxies))
                return false;

            batchedCulling ??= new NaniteGpuBatchedCullingBackend();
            batchedCulling.LodErrorPixelsOverride = settings.overrideLodErrorPixels;
            if (!batchedCulling.EnsureInitialized(shader, proxies))
                return false;
            ConfigureBatchedCullingPolicy(batchableProxyCount);
            if (!batchedCulling.EnsureClusterSceneIndex(sceneVisibilityBackend))
                return false;
            if (!batchedCulling.SupportsGpuVisibleMask)
                return false;

            bool bevy = IsBevyTwoPhaseEnabled() &&
                        cullPassMode != NaniteGpuBatchedCullingBackend.CullPassMode.Legacy;
            // 无 HZB 时禁止 prev 滤波：Pass2 无法可靠遮挡测试，LOD 切换必破洞。
            bool allowPrevFilter = bevy &&
                                   settings.enablePrevVisiblePass1Filter &&
                                   IsHzbActiveForScene() &&
                                   sceneVisibilityBackend.HasPrevVisible;
            bool enableStats = settings.logStats && (Time.frameCount % 30 == 0);
            bool ok = batchedCulling.RunWriteClusterVisible(
                camera,
                hzbTexture,
                hzbMipCount,
                useHzb,
                sceneVisibilityBackend.ClusterVisibleBuffer,
                sceneVisibilityBackend.ClusterCount,
                clearMask,
                cullPassMode,
                bevy ? sceneVisibilityBackend.PrevClusterVisibleBuffer : null,
                bevy ? sceneVisibilityBackend.SecondPassCandidateBuffer : null,
                bevy ? sceneVisibilityBackend.Pass2ClusterVisibleBuffer : null,
                allowPrevFilter,
                enableStats);
            if (ok)
            {
                sceneVisibilityBackend.MarkGpuVisibleMaskReady();
                if (cullPassMode == NaniteGpuBatchedCullingBackend.CullPassMode.Pass1PrevVisible &&
                    sceneVisibilityBackend.Pass1ClusterVisibleBuffer != null)
                {
                    batchedCulling.DispatchCopyUintBuffer(
                        sceneVisibilityBackend.ClusterVisibleBuffer,
                        sceneVisibilityBackend.Pass1ClusterVisibleBuffer,
                        sceneVisibilityBackend.ClusterCount);
                }

                // Pass2/Legacy 结束后，或 Bevy 且跳过 Cull2（Pass1 已是最终 mask）时回写近似可见数。
                if (cullPassMode == NaniteGpuBatchedCullingBackend.CullPassMode.Pass2CandidatesHzb ||
                    cullPassMode == NaniteGpuBatchedCullingBackend.CullPassMode.Legacy ||
                    (cullPassMode == NaniteGpuBatchedCullingBackend.CullPassMode.Pass1PrevVisible &&
                     !ShouldEnqueueSecondCull()))
                {
                    int approx = batchedCulling.LastCull1Drawn + batchedCulling.LastCull2Drawn;
                    if (approx <= 0)
                        approx = batchedCulling.LastClusterCandidateCount;
                    sceneVisibilityBackend.LastGpuVisibleApprox = approx;
                    WriteBackProxyFeatureStats(approx);
                }
            }

            return ok;
        }

        void WriteBackProxyFeatureStats(int gpuVisibleApprox)
        {
            int frame = Time.frameCount;
            if (lastProxyStatsWritebackFrame >= 0 && frame - lastProxyStatsWritebackFrame < 30)
                return;
            lastProxyStatsWritebackFrame = frame;

            var proxies = NaniteRuntimeRegistry.ActiveProxies;
            for (int i = 0; i < proxies.Count; i++)
            {
                var proxy = proxies[i];
                if (proxy == null || !proxy.isActiveAndEnabled || !proxy.NaniteRenderingActive)
                    continue;
                proxy.SetFeatureDrivenVisibleApprox(gpuVisibleApprox);
            }
        }

        void SwapPrevVisibleAfterFrame()
        {
            // 仅 HZB 路径需要 prev；无 HZB 时不维护，避免误开滤波。
            if (!IsBevyTwoPhaseEnabled() ||
                !IsHzbActiveForScene() ||
                sceneVisibilityBackend == null ||
                !sceneVisibilityBackend.GpuVisibleMaskReady ||
                batchedCulling == null)
                return;

            if (!batchedCulling.DispatchCopyUintBuffer(
                    sceneVisibilityBackend.ClusterVisibleBuffer,
                    sceneVisibilityBackend.PrevClusterVisibleBuffer,
                    sceneVisibilityBackend.ClusterCount))
                return;
            sceneVisibilityBackend.MarkPrevVisibleReady();
        }

        bool TryRunBatchedCull(
            Camera camera,
            Texture hzbTexture,
            int hzbMipCount,
            bool useHzb,
            Dictionary<int, NaniteRuntimeSelection> outputsByProxyId,
            out int batchableProxyCount)
        {
            batchableProxyCount = 0;
            if (camera == null || outputsByProxyId == null)
                return false;

            var proxies = NaniteRuntimeRegistry.ActiveProxies;
            var shader = ResolveCullingShader(proxies);
            if (shader == null)
                return false;

            batchableProxyCount = CountBatchableProxies(proxies);
            if (batchableProxyCount <= 0)
                return false;

            batchedCulling ??= new NaniteGpuBatchedCullingBackend();
            batchedCulling.LodErrorPixelsOverride = settings.overrideLodErrorPixels;
            if (!batchedCulling.EnsureInitialized(shader, proxies))
                return false;
            ConfigureBatchedCullingPolicy(batchableProxyCount);

            EnsureSelectionOutputs(proxies, outputsByProxyId);
            return batchedCulling.Run(camera, hzbTexture, hzbMipCount, useHzb, outputsByProxyId);
        }

        void ConfigureBatchedCullingPolicy(int proxyCount)
        {
            if (batchedCulling == null)
                return;

            int cpuLimit = Mathf.Max(0, settings.cpuBvhMaxInstances);
            bool useCpuBvh = settings.useBvhCandidates && cpuLimit > 0 && proxyCount <= cpuLimit;
            batchedCulling.PreferBvhCandidates = useCpuBvh;
            batchedCulling.PreferPartDrivenClusterCull = !useCpuBvh;
            batchedCulling.PartQueueMinVirtualParts = Mathf.Max(1, settings.gpuPartQueueMinVirtualParts);
            batchedCulling.InstanceQueueMinInstances = Mathf.Max(1, settings.gpuInstanceQueueMinInstances);
            batchedCulling.ShadowPartQueueMinVirtualParts = Mathf.Max(1, settings.shadowPartQueueMinVirtualParts);
            batchedCulling.ShadowInstanceQueueMinInstances = Mathf.Max(1, settings.shadowInstanceQueueMinInstances);
        }

        static void EnsureSelectionOutputs(IReadOnlyList<NaniteRuntimeProxy> proxies, Dictionary<int, NaniteRuntimeSelection> outputsByProxyId)
        {
            for (int i = 0; i < proxies.Count; i++)
            {
                var proxy = proxies[i];
                if (proxy == null || !proxy.isActiveAndEnabled || !proxy.NaniteRenderingActive || proxy.naniteMesh == null)
                    continue;
                if (!proxy.useGpuCulling || proxy.gpuCullingShader == null)
                    continue;
                GetOrCreateSelection(outputsByProxyId, proxy.GetInstanceID());
            }
        }

        static int CountBatchableProxies(IReadOnlyList<NaniteRuntimeProxy> proxies)
        {
            // 与 NaniteGpuBatchedCullingBackend.IsBatchableProxy 对齐：有 mesh 即可批（shader 由 Feature 提供）。
            int count = 0;
            for (int i = 0; i < proxies.Count; i++)
            {
                var proxy = proxies[i];
                if (proxy == null || !proxy.isActiveAndEnabled || !proxy.NaniteRenderingActive || proxy.naniteMesh == null)
                    continue;
                count++;
            }

            return count;
        }

        ComputeShader ResolveCullingShader(IReadOnlyList<NaniteRuntimeProxy> proxies)
        {
            if (settings.gpuCullingShader != null)
                return settings.gpuCullingShader;

            for (int i = 0; i < proxies.Count; i++)
            {
                var proxy = proxies[i];
                if (proxy == null || !proxy.useGpuCulling || proxy.gpuCullingShader == null)
                    continue;
                return proxy.gpuCullingShader;
            }

            return null;
        }

        void LogBatchedDispatchStatsOnce(int batchableProxies, string stage, bool hzbActive)
        {
            if (loggedBatchedDispatchStats || batchableProxies <= 0)
                return;
            loggedBatchedDispatchStats = true;
            bool gpuMask = stage != null && stage.IndexOf("gpuMask", StringComparison.Ordinal) >= 0;
            int candidates = batchedCulling != null ? batchedCulling.LastClusterCandidateCount : -1;
            int clusters = batchedCulling != null ? batchedCulling.LastClusterCount : -1;
            string candidateWork = batchedCulling != null && batchedCulling.LastUsedVisiblePartQueue
                ? "GPU-queued"
                : candidates.ToString();
            string hierarchy = batchedCulling != null && batchedCulling.LastUsedCpuCandidates
                ? "CPU-BVH"
                : (batchedCulling != null && batchedCulling.LastUsedPartDrivenClusterCull
                    ? (batchedCulling.LastUsedVisibleInstanceQueue
                        ? "GPU-InstanceQueue-PartQueue-ClusterIndirect"
                        : (batchedCulling.LastUsedVisiblePartQueue
                            ? "GPU-Instance-PartQueue-ClusterIndirect"
                            : "GPU-Instance-Part-Cluster"))
                    : "GPU-FullCluster");
            Debug.Log(
                $"[Nanite][RF] Batched GPU culling ({stage}): proxies={batchableProxies}, " +
                $"candidates={candidateWork} / clusters={clusters}, hierarchy={hierarchy}, " +
                $"gpuScene=mesh:{(batchedCulling != null ? batchedCulling.UniqueGeometryCount : 0)}," +
                $"part:{(batchedCulling != null ? batchedCulling.GeometryPartCount : 0)}/" +
                $"{(batchedCulling != null ? batchedCulling.VirtualPartCount : 0)}," +
                $"cluster:{(batchedCulling != null ? batchedCulling.GeometryClusterCount : 0)}/" +
                $"{(batchedCulling != null ? batchedCulling.VirtualClusterCount : 0)}, " +
                $"pagePool={(sceneVisibilityBackend != null && sceneVisibilityBackend.IsPagePoolReady ? "ready" : "compat")}:" +
                $"{(sceneVisibilityBackend != null ? sceneVisibilityBackend.ResidentPageCount : 0)}/" +
                $"{(sceneVisibilityBackend != null ? sceneVisibilityBackend.GlobalPageCount : 0)} " +
                $"pinned={(sceneVisibilityBackend != null ? sceneVisibilityBackend.PinnedPageCount : 0)}, " +
                $"bevyTwoPhase={IsBevyTwoPhaseEnabled()}, " +
                $"readback={(gpuMask ? 0 : 1)}, hzbActive={hzbActive}.");
        }

        void LogBevyCullStatsIfNeeded(string stage)
        {
            if (!settings.logStats || batchedCulling == null || Time.frameCount % 30 != 0)
                return;
            int compactTris = -1;
            int meshTris = sceneVisibilityBackend != null ? sceneVisibilityBackend.TriangleCount : -1;
            if (sceneVisibilityBackend != null && HasValidCompactForSelection(kCompactSelectionMerged))
                compactTris = sceneVisibilityBackend.TryGetCompactedTriangleCount();
            else if (sceneVisibilityBackend != null && HasValidCompactForSelection(kCompactSelectionFirst))
                compactTris = sceneVisibilityBackend.TryGetCompactedTriangleCount();

            int drawn = batchedCulling.LastCull1Drawn + batchedCulling.LastCull2Drawn;
            Debug.Log(
                $"[Nanite][RF][{stage}] frame={Time.frameCount} " +
                $"cull1Drawn={batchedCulling.LastCull1Drawn} " +
                $"cull2Candidates={batchedCulling.LastCull2Candidates} " +
                $"cull2Drawn={batchedCulling.LastCull2Drawn} " +
                $"visibleClusters≈{drawn} " +
                $"compactTriSlots={compactTris} (uniqueMeshTris={meshTris}) " +
                $"candidates={batchedCulling.LastClusterCandidateCount}/{batchedCulling.LastClusterCount} " +
                $"lodOverride={settings.overrideLodErrorPixels} " +
                $"useHzb={IsHzbActiveForScene()} useBvh={settings.useBvhCandidates}");
        }

        void RecordPerfSample(double cpuMs, bool isFirstCull, bool batched, int proxyCount)
        {
            int dispatch = proxyCount <= 0 ? 0 : (batched ? 2 : proxyCount * 2);
            // GPU-resident mask 路径无 GetData；仅旧 readback 路径计 1。
            bool gpuMask = sceneVisibilityBackend != null && sceneVisibilityBackend.GpuVisibleMaskReady;
            int readback = proxyCount <= 0 ? 0 : (batched ? (gpuMask ? 0 : 1) : proxyCount);
            if (isFirstCull)
            {
                perfFirstCullCpuMsAccum += cpuMs;
                perfFirstCullDispatchAccum += dispatch;
                perfFirstCullReadbackAccum += readback;
            }
            else
            {
                perfSecondCullCpuMsAccum += cpuMs;
                perfSecondCullDispatchAccum += dispatch;
                perfSecondCullReadbackAccum += readback;
            }

            if (!isFirstCull)
                perfSampleCount++;

            if (!settings.logStats || perfSampleCount <= 0 || Time.frameCount % 120 != 0)
                return;

            double inv = 1.0 / perfSampleCount;
            Debug.Log(
                "[Nanite][PerfBaseline] " +
                $"samples={perfSampleCount} " +
                $"first[cpuMs={perfFirstCullCpuMsAccum * inv:0.###},dispatch={perfFirstCullDispatchAccum * inv:0.##},readback={perfFirstCullReadbackAccum * inv:0.##}] " +
                $"second[cpuMs={perfSecondCullCpuMsAccum * inv:0.###},dispatch={perfSecondCullDispatchAccum * inv:0.##},readback={perfSecondCullReadbackAccum * inv:0.##}]");
        }

        void RecordFormalPerfSample(int visibilityDrawCount, int resolveDrawCount, int materialBatches, float tileCoverage)
        {
            perfFormalSampleCount++;
            perfVisibilityDrawAccum += visibilityDrawCount;
            perfResolveDrawAccum += resolveDrawCount;
            perfMaterialBatchAccum += materialBatches;
            if (tileCoverage >= 0f)
            {
                perfTileCoverageAccum += tileCoverage;
                perfTileCoverageSampleCount++;
            }

            if (!settings.logStats || perfFormalSampleCount <= 0 || Time.frameCount % 120 != 0)
                return;

            double inv = 1.0 / perfFormalSampleCount;
            double tileInv = perfTileCoverageSampleCount > 0 ? (1.0 / perfTileCoverageSampleCount) : 0.0;
            double tileCoverageAvg = perfTileCoverageSampleCount > 0 ? perfTileCoverageAccum * tileInv : -1.0;
            string tileText = tileCoverageAvg >= 0.0 ? $"{tileCoverageAvg * 100.0:0.#}%" : "n/a";
            Debug.Log(
                "[Nanite][PerfBaseline][Formal] " +
                $"samples={perfFormalSampleCount} " +
                $"visibilityDraw={perfVisibilityDrawAccum * inv:0.##} " +
                $"resolveDraw={perfResolveDrawAccum * inv:0.##} " +
                $"materialBatches={perfMaterialBatchAccum * inv:0.##} " +
                $"tileCoverage={tileText}");
        }

        void ExecuteDebugVisualization(ScriptableRenderContext context, Camera camera)
        {
            if (!settings.enableDebugVisualization || camera == null)
                return;

            var material = EnsureVBufferPreviewMaterial();
            if (material == null)
                return;

            var cmd = CommandBufferPool.Get("Nanite Debug Visualization");
            ExecuteDebugVisualization(cmd, material, camera);
            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();
            CommandBufferPool.Release(cmd);
        }

        void ExecuteDebugVisualization(CommandBuffer cmd, Material material, Camera camera)
        {
            if (!settings.enableDebugVisualization || cmd == null || material == null || camera == null)
                return;

            int pass = GetFormalResolvePassIndex(material, kPassDebugColorViz, kDebugColorVizPassFallback);
            float mode = (float)settings.debugVisualizationMode;
            int drawCalls = 0;

            if (EnsureVisibleTrianglesCompacted(cmd, camera, mergedSelections, kCompactSelectionMerged) &&
                sceneVisibilityBackend.TryGetSceneBuffers(
                    out var vertexDataBuffer,
                    out var indexBuffer,
                    out var triangleClusterBuffer,
                    out var trianglePageBuffer,
                    out var triangleInstanceBuffer,
                    out var triangleSubMeshBuffer,
                    out var clusterVisibleBuffer,
                    out var instanceLocalToWorldBuffer,
                    out _,
                    out var drawArgsBuffer,
                    out int vertexStride,
                    out _))
            {
                cmd.SetGlobalBuffer(ShaderIds.VertexData, vertexDataBuffer);
                cmd.SetGlobalBuffer(ShaderIds.Indices, indexBuffer);
                cmd.SetGlobalBuffer(ShaderIds.TriangleCluster, triangleClusterBuffer);
                cmd.SetGlobalBuffer(ShaderIds.TrianglePage, trianglePageBuffer);
                cmd.SetGlobalBuffer(ShaderIds.TriangleSubMesh, triangleSubMeshBuffer);
                cmd.SetGlobalBuffer(ShaderIds.TriangleInstance, triangleInstanceBuffer);
                cmd.SetGlobalBuffer(ShaderIds.InstanceLocalToWorld, instanceLocalToWorldBuffer);
                cmd.SetGlobalBuffer(ShaderIds.ClusterVisible, clusterVisibleBuffer);
                BindPackedPageGeometry(cmd);
                cmd.SetGlobalFloat(ShaderIds.VertexStride, Mathf.Max(3, vertexStride));
                cmd.SetGlobalFloat(ShaderIds.UseSceneInstanceBuffer, 1f);
                cmd.SetGlobalFloat(ShaderIds.HasTriangleSubMesh, 1f);
                cmd.SetGlobalFloat(ShaderIds.UseNormalizedIds, ComputeFormalVBufferIdMode(sceneVisibilityBackend.TriangleCount));
                cmd.SetGlobalFloat(ShaderIds.MaxSubMeshCount, sceneVisibilityBackend.MaxSubMeshCount);
                cmd.SetGlobalFloat(ShaderIds.TriangleCount, sceneVisibilityBackend.TriangleCount);
                cmd.SetGlobalFloat(ShaderIds.InstanceCount, sceneVisibilityBackend.InstanceCount);
                cmd.SetGlobalFloat(ShaderIds.DebugVizMode, mode);
                BindCompactDrawState(cmd, kCompactSelectionMerged);
                cmd.DrawProceduralIndirect(Matrix4x4.identity, material, pass, MeshTopology.Triangles, drawArgsBuffer, 0);
                drawCalls = 1;
            }
            else
            {
                drawCalls = DrawDebugVisualizationPerProxy(cmd, material, pass, mode);
            }

            if (!loggedDebugVizOnce && drawCalls > 0)
            {
                loggedDebugVizOnce = true;
                Debug.Log($"[Nanite][RF] Debug visualization active: mode={settings.debugVisualizationMode}, draws={drawCalls}");
            }
        }

        void ExecuteDebugVisualization(RasterCommandBuffer cmd, Material material, Camera camera)
        {
            if (!settings.enableDebugVisualization || cmd == null || material == null || camera == null)
                return;

            int pass = GetFormalResolvePassIndex(material, kPassDebugColorViz, kDebugColorVizPassFallback);
            float mode = (float)settings.debugVisualizationMode;
            int drawCalls = 0;

            if (TryPrepareSceneVisibility(camera, mergedSelections) &&
                sceneVisibilityBackend.TryGetSceneBuffers(
                    out var vertexDataBuffer,
                    out var indexBuffer,
                    out var triangleClusterBuffer,
                    out var trianglePageBuffer,
                    out var triangleInstanceBuffer,
                    out var triangleSubMeshBuffer,
                    out var clusterVisibleBuffer,
                    out var instanceLocalToWorldBuffer,
                    out _,
                    out var drawArgsBuffer,
                    out int vertexStride,
                    out _))
            {
                if (!HasValidCompactForSelection(kCompactSelectionMerged))
                    sceneVisibilityBackend.ResetDrawArgsToFullMesh();

                BindFormalRasterSceneBuffers(
                    cmd,
                    vertexDataBuffer,
                    indexBuffer,
                    triangleClusterBuffer,
                    triangleSubMeshBuffer,
                    triangleInstanceBuffer,
                    instanceLocalToWorldBuffer,
                    clusterVisibleBuffer);
                BindPackedPageGeometry(cmd);
                cmd.SetGlobalBuffer(ShaderIds.TrianglePage, trianglePageBuffer);
                BindFormalRasterUniforms(
                    cmd,
                    vertexStride,
                    sceneVisibilityBackend.TriangleCount,
                    sceneVisibilityBackend.InstanceCount,
                    sceneVisibilityBackend.MaxSubMeshCount);
                cmd.SetGlobalFloat(ShaderIds.DebugVizMode, mode);
                BindCompactDrawState(cmd, kCompactSelectionMerged);
                cmd.DrawProceduralIndirect(Matrix4x4.identity, material, pass, MeshTopology.Triangles, drawArgsBuffer, 0);
                drawCalls = 1;
            }
            else
            {
                drawCalls = DrawDebugVisualizationPerProxy(cmd, material, pass, mode);
            }

            if (!loggedDebugVizOnce && drawCalls > 0)
            {
                loggedDebugVizOnce = true;
                Debug.Log($"[Nanite][RF] Debug visualization active: mode={settings.debugVisualizationMode}, draws={drawCalls}");
            }
        }

        int DrawDebugVisualizationPerProxy(CommandBuffer cmd, Material material, int pass, float mode)
        {
            var mpb = GetOrCreateVBufferPropertyBlock();
            var proxies = NaniteRuntimeRegistry.ActiveProxies;
            int drawCalls = 0;
            int drawBudget = settings.vbufferPreviewMaxPackets <= 0 ? int.MaxValue : Mathf.Max(1, settings.vbufferPreviewMaxPackets);
            for (int i = 0; i < proxies.Count; i++)
            {
                var proxy = proxies[i];
                if (proxy == null || !proxy.isActiveAndEnabled || !proxy.NaniteRenderingActive || proxy.naniteMesh == null)
                    continue;
                if (!proxy.EnsurePageGpuBuffers())
                    continue;

                var selection = proxy.RuntimeSelection;
                if (selection == null || selection.packets == null || selection.packets.Count == 0)
                    continue;
                proxy.UpdateMergedClusterVisibilityBuffer(selection);
                if (!proxy.TryGetMergedGpuData(
                        out var vertexDataBuffer,
                        out var indexBuffer,
                        out var triangleClusterBuffer,
                        out var trianglePageBuffer,
                        out var clusterVisibleBuffer,
                        out int vertexStride,
                        out int indexCount,
                        out _,
                        out int clusterCount))
                    continue;
                if (indexCount <= 0 || clusterCount <= 0)
                    continue;

                int instanceId = selection.packets[0].instanceId;
                mpb.Clear();
                mpb.SetBuffer(ShaderIds.VertexData, vertexDataBuffer);
                mpb.SetBuffer(ShaderIds.Indices, indexBuffer);
                mpb.SetBuffer(ShaderIds.TriangleCluster, triangleClusterBuffer);
                mpb.SetBuffer(ShaderIds.TrianglePage, trianglePageBuffer);
                mpb.SetBuffer(ShaderIds.ClusterVisible, clusterVisibleBuffer);
                mpb.SetFloat(ShaderIds.VertexStride, Mathf.Max(3, vertexStride));
                mpb.SetFloat(ShaderIds.InstanceId, instanceId);
                mpb.SetMatrix(ShaderIds.LocalToWorld, selection.instanceLocalToWorld);
                mpb.SetFloat(ShaderIds.UseSceneInstanceBuffer, 0f);
                mpb.SetFloat(ShaderIds.UsePackedPageGeometry, 0f);
                mpb.SetFloat(ShaderIds.UseNormalizedIds, 0f);
                mpb.SetFloat(ShaderIds.TriangleCount, Mathf.Max(1, indexCount / 3));
                mpb.SetFloat(ShaderIds.InstanceCount, 1f);
                mpb.SetFloat(ShaderIds.MaxSubMeshCount, Mathf.Max(1, proxy.naniteMesh != null ? proxy.naniteMesh.subMeshCount : 1));
                mpb.SetFloat(ShaderIds.UseCompactedTriIds, 0f);
                mpb.SetFloat(ShaderIds.DebugVizMode, mode);
                if (proxy.TryGetMergedTriangleSubMeshBuffer(out var triangleSubMeshBuffer))
                {
                    mpb.SetBuffer(ShaderIds.TriangleSubMesh, triangleSubMeshBuffer);
                    mpb.SetFloat(ShaderIds.HasTriangleSubMesh, 1f);
                }
                else
                {
                    mpb.SetFloat(ShaderIds.HasTriangleSubMesh, 0f);
                }

                if (settings.enableIndirectPreviewDraw && proxy.TryGetMergedIndirectArgsBuffer(out var indirectArgs))
                    cmd.DrawProceduralIndirect(Matrix4x4.identity, material, pass, MeshTopology.Triangles, indirectArgs, 0, mpb);
                else
                    cmd.DrawProcedural(Matrix4x4.identity, material, pass, MeshTopology.Triangles, indexCount, 1, mpb);
                drawCalls++;
                if (drawCalls >= drawBudget)
                    break;
            }

            return drawCalls;
        }

        int DrawDebugVisualizationPerProxy(RasterCommandBuffer cmd, Material material, int pass, float mode)
        {
            var mpb = GetOrCreateVBufferPropertyBlock();
            var proxies = NaniteRuntimeRegistry.ActiveProxies;
            int drawCalls = 0;
            int drawBudget = settings.vbufferPreviewMaxPackets <= 0 ? int.MaxValue : Mathf.Max(1, settings.vbufferPreviewMaxPackets);
            for (int i = 0; i < proxies.Count; i++)
            {
                var proxy = proxies[i];
                if (proxy == null || !proxy.isActiveAndEnabled || !proxy.NaniteRenderingActive || proxy.naniteMesh == null)
                    continue;
                if (!proxy.EnsurePageGpuBuffers())
                    continue;

                var selection = proxy.RuntimeSelection;
                if (selection == null || selection.packets == null || selection.packets.Count == 0)
                    continue;
                proxy.UpdateMergedClusterVisibilityBuffer(selection);
                if (!proxy.TryGetMergedGpuData(
                        out var vertexDataBuffer,
                        out var indexBuffer,
                        out var triangleClusterBuffer,
                        out var trianglePageBuffer,
                        out var clusterVisibleBuffer,
                        out int vertexStride,
                        out int indexCount,
                        out _,
                        out int clusterCount))
                    continue;
                if (indexCount <= 0 || clusterCount <= 0)
                    continue;

                int instanceId = selection.packets[0].instanceId;
                mpb.Clear();
                mpb.SetBuffer(ShaderIds.VertexData, vertexDataBuffer);
                mpb.SetBuffer(ShaderIds.Indices, indexBuffer);
                mpb.SetBuffer(ShaderIds.TriangleCluster, triangleClusterBuffer);
                mpb.SetBuffer(ShaderIds.TrianglePage, trianglePageBuffer);
                mpb.SetBuffer(ShaderIds.ClusterVisible, clusterVisibleBuffer);
                mpb.SetFloat(ShaderIds.VertexStride, Mathf.Max(3, vertexStride));
                mpb.SetFloat(ShaderIds.InstanceId, instanceId);
                mpb.SetMatrix(ShaderIds.LocalToWorld, selection.instanceLocalToWorld);
                mpb.SetFloat(ShaderIds.UseSceneInstanceBuffer, 0f);
                mpb.SetFloat(ShaderIds.UsePackedPageGeometry, 0f);
                mpb.SetFloat(ShaderIds.UseNormalizedIds, 0f);
                mpb.SetFloat(ShaderIds.TriangleCount, Mathf.Max(1, indexCount / 3));
                mpb.SetFloat(ShaderIds.InstanceCount, 1f);
                mpb.SetFloat(ShaderIds.MaxSubMeshCount, Mathf.Max(1, proxy.naniteMesh != null ? proxy.naniteMesh.subMeshCount : 1));
                mpb.SetFloat(ShaderIds.UseCompactedTriIds, 0f);
                mpb.SetFloat(ShaderIds.DebugVizMode, mode);
                if (proxy.TryGetMergedTriangleSubMeshBuffer(out var triangleSubMeshBuffer))
                {
                    mpb.SetBuffer(ShaderIds.TriangleSubMesh, triangleSubMeshBuffer);
                    mpb.SetFloat(ShaderIds.HasTriangleSubMesh, 1f);
                }
                else
                {
                    mpb.SetFloat(ShaderIds.HasTriangleSubMesh, 0f);
                }

                if (settings.enableIndirectPreviewDraw && proxy.TryGetMergedIndirectArgsBuffer(out var indirectArgs))
                    cmd.DrawProceduralIndirect(Matrix4x4.identity, material, pass, MeshTopology.Triangles, indirectArgs, 0, mpb);
                else
                    cmd.DrawProcedural(Matrix4x4.identity, material, pass, MeshTopology.Triangles, indexCount, 1, mpb);
                drawCalls++;
                if (drawCalls >= drawBudget)
                    break;
            }

            return drawCalls;
        }

        void ExecuteVBufferPreview(ScriptableRenderContext context, Camera camera)
        {
            if (!settings.enableVBufferPreview || camera == null)
                return;

            var material = EnsureVBufferPreviewMaterial();
            if (material == null)
                return;

            var cmd = CommandBufferPool.Get("Nanite VBuffer Preview");
            ExecuteVBufferPreview(cmd, material, camera);
            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();
            CommandBufferPool.Release(cmd);
        }

        void ExecuteVBufferPreview(CommandBuffer cmd, Material material, Camera camera)
        {
            if (!settings.enableVBufferPreview || material == null || camera == null)
                return;

            var mpb = GetOrCreateVBufferPropertyBlock();
            var proxies = NaniteRuntimeRegistry.ActiveProxies;
            int drawCalls = 0;
            int drawBudget = settings.vbufferPreviewMaxPackets <= 0 ? int.MaxValue : Mathf.Max(1, settings.vbufferPreviewMaxPackets);
            for (int i = 0; i < proxies.Count; i++)
            {
                var proxy = proxies[i];
                if (proxy == null || !proxy.isActiveAndEnabled || !proxy.NaniteRenderingActive || proxy.naniteMesh == null)
                    continue;
                if (!proxy.EnsurePageGpuBuffers())
                    continue;

                var selection = proxy.RuntimeSelection;
                if (selection == null || selection.packets == null || selection.packets.Count == 0)
                    continue;
                proxy.UpdateMergedClusterVisibilityBuffer(selection);
                if (!proxy.TryGetMergedGpuData(
                        out var vertexDataBuffer,
                        out var indexBuffer,
                        out var triangleClusterBuffer,
                        out var trianglePageBuffer,
                        out var clusterVisibleBuffer,
                        out int vertexStride,
                        out int indexCount,
                        out _,
                        out int clusterCount))
                    continue;
                if (indexCount <= 0 || clusterCount <= 0)
                    continue;

                int instanceId = selection.packets[0].instanceId;
                mpb.Clear();
                mpb.SetBuffer(ShaderIds.VertexData, vertexDataBuffer);
                mpb.SetBuffer(ShaderIds.Indices, indexBuffer);
                mpb.SetBuffer(ShaderIds.TriangleCluster, triangleClusterBuffer);
                mpb.SetBuffer(ShaderIds.TrianglePage, trianglePageBuffer);
                mpb.SetBuffer(ShaderIds.ClusterVisible, clusterVisibleBuffer);
                mpb.SetInt(ShaderIds.VertexStride, Mathf.Max(3, vertexStride));
                mpb.SetInt(ShaderIds.InstanceId, instanceId);
                mpb.SetMatrix(ShaderIds.LocalToWorld, selection.instanceLocalToWorld);
                mpb.SetInt(ShaderIds.UseSceneInstanceBuffer, 0);
                mpb.SetFloat(ShaderIds.UsePackedPageGeometry, 0f);
                mpb.SetInt(ShaderIds.UseNormalizedIds, 0);
                mpb.SetInt(ShaderIds.TriangleCount, Mathf.Max(1, indexCount / 3));
                mpb.SetInt(ShaderIds.InstanceCount, 1);
                mpb.SetInt(ShaderIds.MaxSubMeshCount, Mathf.Max(1, proxy.naniteMesh != null ? proxy.naniteMesh.subMeshCount : 1));
                mpb.SetFloat(ShaderIds.UseCompactedTriIds, 0f);
                if (proxy.TryGetMergedTriangleSubMeshBuffer(out var triangleSubMeshBuffer))
                {
                    mpb.SetBuffer(ShaderIds.TriangleSubMesh, triangleSubMeshBuffer);
                    mpb.SetInt(ShaderIds.HasTriangleSubMesh, 1);
                }
                else
                {
                    mpb.SetInt(ShaderIds.HasTriangleSubMesh, 0);
                }

                if (settings.enableIndirectPreviewDraw && proxy.TryGetMergedIndirectArgsBuffer(out var indirectArgs))
                    cmd.DrawProceduralIndirect(Matrix4x4.identity, material, 0, MeshTopology.Triangles, indirectArgs, 0, mpb);
                else
                    cmd.DrawProcedural(Matrix4x4.identity, material, 0, MeshTopology.Triangles, indexCount, 1, mpb);
                drawCalls++;
                if (drawCalls >= drawBudget)
                    break;
            }

            if (!loggedVBufferOnce && drawCalls > 0)
            {
                loggedVBufferOnce = true;
                Debug.Log($"[Nanite][RF] VBuffer preview active: camera={camera.name}, draws={drawCalls}");
            }
        }

        void ExecuteVBufferPreview(RasterCommandBuffer cmd, Material material, Camera camera)
        {
            if (!settings.enableVBufferPreview || material == null || camera == null)
                return;

            var mpb = GetOrCreateVBufferPropertyBlock();
            var proxies = NaniteRuntimeRegistry.ActiveProxies;
            int drawCalls = 0;
            int drawBudget = settings.vbufferPreviewMaxPackets <= 0 ? int.MaxValue : Mathf.Max(1, settings.vbufferPreviewMaxPackets);
            for (int i = 0; i < proxies.Count; i++)
            {
                var proxy = proxies[i];
                if (proxy == null || !proxy.isActiveAndEnabled || !proxy.NaniteRenderingActive || proxy.naniteMesh == null)
                    continue;
                if (!proxy.EnsurePageGpuBuffers())
                    continue;

                var selection = proxy.RuntimeSelection;
                if (selection == null || selection.packets == null || selection.packets.Count == 0)
                    continue;
                proxy.UpdateMergedClusterVisibilityBuffer(selection);
                if (!proxy.TryGetMergedGpuData(
                        out var vertexDataBuffer,
                        out var indexBuffer,
                        out var triangleClusterBuffer,
                        out var trianglePageBuffer,
                        out var clusterVisibleBuffer,
                        out int vertexStride,
                        out int indexCount,
                        out _,
                        out int clusterCount))
                    continue;
                if (indexCount <= 0 || clusterCount <= 0)
                    continue;

                int instanceId = selection.packets[0].instanceId;
                mpb.Clear();
                mpb.SetBuffer(ShaderIds.VertexData, vertexDataBuffer);
                mpb.SetBuffer(ShaderIds.Indices, indexBuffer);
                mpb.SetBuffer(ShaderIds.TriangleCluster, triangleClusterBuffer);
                mpb.SetBuffer(ShaderIds.TrianglePage, trianglePageBuffer);
                mpb.SetBuffer(ShaderIds.ClusterVisible, clusterVisibleBuffer);
                mpb.SetInt(ShaderIds.VertexStride, Mathf.Max(3, vertexStride));
                mpb.SetInt(ShaderIds.InstanceId, instanceId);
                mpb.SetMatrix(ShaderIds.LocalToWorld, selection.instanceLocalToWorld);
                mpb.SetInt(ShaderIds.UseSceneInstanceBuffer, 0);
                mpb.SetFloat(ShaderIds.UsePackedPageGeometry, 0f);
                mpb.SetInt(ShaderIds.UseNormalizedIds, 0);
                mpb.SetInt(ShaderIds.TriangleCount, Mathf.Max(1, indexCount / 3));
                mpb.SetInt(ShaderIds.InstanceCount, 1);
                mpb.SetInt(ShaderIds.MaxSubMeshCount, Mathf.Max(1, proxy.naniteMesh != null ? proxy.naniteMesh.subMeshCount : 1));
                mpb.SetFloat(ShaderIds.UseCompactedTriIds, 0f);
                if (proxy.TryGetMergedTriangleSubMeshBuffer(out var triangleSubMeshBuffer))
                {
                    mpb.SetBuffer(ShaderIds.TriangleSubMesh, triangleSubMeshBuffer);
                    mpb.SetInt(ShaderIds.HasTriangleSubMesh, 1);
                }
                else
                {
                    mpb.SetInt(ShaderIds.HasTriangleSubMesh, 0);
                }

                if (settings.enableIndirectPreviewDraw && proxy.TryGetMergedIndirectArgsBuffer(out var indirectArgs))
                    cmd.DrawProceduralIndirect(Matrix4x4.identity, material, 0, MeshTopology.Triangles, indirectArgs, 0, mpb);
                else
                    cmd.DrawProcedural(Matrix4x4.identity, material, 0, MeshTopology.Triangles, indexCount, 1, mpb);
                drawCalls++;
                if (drawCalls >= drawBudget)
                    break;
            }

            if (!loggedVBufferOnce && drawCalls > 0)
            {
                loggedVBufferOnce = true;
                Debug.Log($"[Nanite][RF] VBuffer preview active: camera={camera.name}, draws={drawCalls}");
            }
        }

        void ExecuteVBufferComposite(RasterCommandBuffer cmd, Material material, TextureHandle vbuffer)
        {
            if (material == null || !vbuffer.IsValid())
                return;
            cmd.SetGlobalTexture(ShaderIds.NaniteVBufferTex, vbuffer);
            cmd.DrawProcedural(Matrix4x4.identity, material, 0, MeshTopology.Triangles, 3, 1);
        }

        TextureHandle ResolveHzbDepthSource(UniversalResourceData resourceData, CameraHzbState state)
        {
            if (recordedHzbDepthSource.IsValid())
                return recordedHzbDepthSource;
            if (resourceData.cameraDepthTexture.IsValid())
                return resourceData.cameraDepthTexture;
            return resourceData.activeDepthTexture;
        }

        void RecordCopyDepthForHzb(
            RenderGraph renderGraph,
            ContextContainer frameData,
            UniversalCameraData cameraData,
            ProfilingSampler profilingSampler)
        {
            var copyMaterial = EnsureCopyDepthMaterial();
            if (copyMaterial == null || cameraData?.camera == null)
                return;

            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
            if (!resourceData.activeDepthTexture.IsValid())
                return;

            TextureHandle destination = resourceData.cameraDepthTexture;
            if (!destination.IsValid())
            {
                var depthCopyDesc = renderGraph.GetTextureDesc(resourceData.activeDepthTexture);
                depthCopyDesc.name = "Nanite_DepthSampleForHzb";
                depthCopyDesc.format = GraphicsFormat.R32_SFloat;
                depthCopyDesc.depthBufferBits = 0;
                depthCopyDesc.clearBuffer = false;
                destination = renderGraph.CreateTexture(depthCopyDesc);
            }

            recordedHzbDepthSource = destination;

            using (var builder = renderGraph.AddRasterRenderPass<CopyDepthRgPassData>("Nanite/CopyDepthForHzb", out var passData, profilingSampler))
            {
                passData.copyMaterial = copyMaterial;
                passData.source = resourceData.activeDepthTexture;
                passData.destination = destination;
                passData.cameraData = cameraData;

                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);
                builder.UseTexture(passData.source, AccessFlags.Read);
                builder.SetRenderAttachment(passData.destination, 0, AccessFlags.WriteAll);
                builder.SetGlobalTextureAfterPass(passData.destination, ShaderIds.CameraDepthTexture);

                builder.SetRenderFunc((CopyDepthRgPassData data, RasterGraphContext context) =>
                {
                    var hzbState = GetOrCreateState(
                        data.cameraData.camera.pixelWidth,
                        data.cameraData.camera.pixelHeight,
                        data.cameraData.camera.GetInstanceID());
                    if (!hzbState.depthWrittenThisFrame)
                        return;

                    ExecuteCopyDepthForHzb(context.cmd, data);
                    hzbState.depthCopiedForHzb = true;
                });
            }
        }

        void ExecuteCopyDepthForHzb(RasterCommandBuffer cmd, CopyDepthRgPassData data)
        {
            if (cmd == null || data.copyMaterial == null || !data.source.IsValid())
                return;

            ConfigureCopyDepthShaderKeywords(cmd);

            data.copyMaterial.SetTexture(ShaderIds.CameraDepthAttachment, data.source);
            data.copyMaterial.SetFloat(ShaderIds.ZWrite, 0f);
            Blitter.BlitTexture(cmd, new Vector4(1f, 1f, 0f, 0f), data.copyMaterial, 0);
        }

        void CopyDepthForHzbLegacy(CommandBuffer cmd, Camera camera, CameraHzbState state)
        {
            var copyMaterial = EnsureCopyDepthMaterial();
            if (cmd == null || copyMaterial == null || camera == null || state == null)
                return;

            EnsureDepthSampleRt(state, state.width, state.height);
            if (state.depthSample == null)
                return;

            ConfigureCopyDepthShaderKeywords(cmd);

            copyMaterial.SetFloat(ShaderIds.ZWrite, 0f);
            cmd.Blit(BuiltinRenderTextureType.CurrentActive, state.depthSample, copyMaterial, 0);
            cmd.SetGlobalTexture(ShaderIds.CameraDepthTexture, state.depthSample);
            state.depthCopiedForHzb = true;
        }

        void EnsureDepthSampleRt(CameraHzbState state, int width, int height)
        {
            if (state == null)
                return;

            int w = Mathf.Max(1, width);
            int h = Mathf.Max(1, height);
            if (state.depthSample != null && state.depthSample.width == w && state.depthSample.height == h)
                return;

            if (state.depthSample != null)
            {
                state.depthSample.Release();
                DestroyObject(state.depthSample);
                state.depthSample = null;
            }

            state.depthSample = new RenderTexture(w, h, 0, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear)
            {
                name = "Nanite_DepthSampleForHzb",
                enableRandomWrite = false,
                useMipMap = false,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            state.depthSample.Create();
        }

        void BuildCurrentHzb(CommandBuffer cmd, CameraHzbState state)
        {
            int width = state.width;
            int height = state.height;
            int mipCount = state.mipCount;
            int copyKernel = ResolveHzbCopyKernel();
            if (width <= 0 || height <= 0 || mipCount <= 0 || state.current == null || copyKernel < 0)
                return;

            cmd.SetComputeVectorParam(settings.hzbBuilderShader, ShaderIds.SourceSize, new Vector4(width, height, 0, 0));
            if (state.depthCopiedForHzb && state.depthSample != null)
                cmd.SetComputeTextureParam(settings.hzbBuilderShader, copyKernel, ShaderIds.SourceDepth, state.depthSample);
            else
                cmd.SetComputeTextureParam(settings.hzbBuilderShader, copyKernel, ShaderIds.SourceDepth, ShaderIds.CameraDepthTexture);
            cmd.SetComputeTextureParam(settings.hzbBuilderShader, copyKernel, ShaderIds.DestMip, state.current, 0);
            Dispatch2D(cmd, settings.hzbBuilderShader, copyKernel, width, height, 8, 8);

            int srcW = width;
            int srcH = height;
            for (int mip = 1; mip < mipCount; mip++)
            {
                cmd.SetComputeVectorParam(settings.hzbBuilderShader, ShaderIds.SourceSize, new Vector4(srcW, srcH, 0, 0));
                cmd.SetComputeTextureParam(settings.hzbBuilderShader, kernelDownsample, ShaderIds.SourceMip, state.current, mip - 1);
                cmd.SetComputeTextureParam(settings.hzbBuilderShader, kernelDownsample, ShaderIds.DestMip, state.current, mip);
                Dispatch2D(cmd, settings.hzbBuilderShader, kernelDownsample, Mathf.Max(1, srcW >> 1), Mathf.Max(1, srcH >> 1), 8, 8);
                srcW = Mathf.Max(1, srcW >> 1);
                srcH = Mathf.Max(1, srcH >> 1);
            }
            state.builtFrame = Time.frameCount;
            state.currentHzbValid = state.depthWrittenThisFrame;
        }

        void BuildCurrentHzb(UnsafeCommandBuffer cmd, CameraHzbState state, TextureHandle depthTexture)
        {
            int width = state.width;
            int height = state.height;
            int mipCount = state.mipCount;
            int copyKernel = ResolveHzbCopyKernel();
            if (width <= 0 || height <= 0 || mipCount <= 0 || state.current == null || !depthTexture.IsValid() || copyKernel < 0)
                return;

            cmd.SetComputeVectorParam(settings.hzbBuilderShader, ShaderIds.SourceSize, new Vector4(width, height, 0, 0));
            cmd.SetComputeTextureParam(settings.hzbBuilderShader, copyKernel, ShaderIds.SourceDepth, depthTexture);
            cmd.SetComputeTextureParam(settings.hzbBuilderShader, copyKernel, ShaderIds.DestMip, state.current, 0);
            Dispatch2D(cmd, settings.hzbBuilderShader, copyKernel, width, height, 8, 8);

            int srcW = width;
            int srcH = height;
            for (int mip = 1; mip < mipCount; mip++)
            {
                cmd.SetComputeVectorParam(settings.hzbBuilderShader, ShaderIds.SourceSize, new Vector4(srcW, srcH, 0, 0));
                cmd.SetComputeTextureParam(settings.hzbBuilderShader, kernelDownsample, ShaderIds.SourceMip, state.current, mip - 1);
                cmd.SetComputeTextureParam(settings.hzbBuilderShader, kernelDownsample, ShaderIds.DestMip, state.current, mip);
                Dispatch2D(cmd, settings.hzbBuilderShader, kernelDownsample, Mathf.Max(1, srcW >> 1), Mathf.Max(1, srcH >> 1), 8, 8);
                srcW = Mathf.Max(1, srcW >> 1);
                srcH = Mathf.Max(1, srcH >> 1);
            }
            state.builtFrame = Time.frameCount;
            state.currentHzbValid = state.depthWrittenThisFrame && depthTexture.IsValid();
        }

        void LogDispatchStatsOnce(int activeProxies, string stage)
        {
            if (loggedDispatchStats || activeProxies <= 0)
                return;
            loggedDispatchStats = true;
            int kernelsPerProxy = 2;
            int dispatchesPerPass = activeProxies * kernelsPerProxy;
            Debug.Log(
                $"[Nanite][RF] GPU culling dispatch ({stage}): proxies={activeProxies}, " +
                $"kernels/dispatch={kernelsPerProxy} (PartCull+ClusterCull), total={dispatchesPerPass} per pass. " +
                "这不是 UE 做法（UE 批量 persistent threads），后续需合并为 1-2 次 dispatch。");
        }

        static void Dispatch2D(CommandBuffer cmd, ComputeShader cs, int kernel, int width, int height, int tx, int ty)
        {
            int gx = Mathf.Max(1, (width + tx - 1) / tx);
            int gy = Mathf.Max(1, (height + ty - 1) / ty);
            cmd.DispatchCompute(cs, kernel, gx, gy, 1);
        }

        static void Dispatch2D(UnsafeCommandBuffer cmd, ComputeShader cs, int kernel, int width, int height, int tx, int ty)
        {
            int gx = Mathf.Max(1, (width + tx - 1) / tx);
            int gy = Mathf.Max(1, (height + ty - 1) / ty);
            cmd.DispatchCompute(cs, kernel, gx, gy, 1);
        }

        void LogExecutionOnce(Camera camera, string stage)
        {
            if (loggedExecutionOnce)
                return;
            loggedExecutionOnce = true;
            Debug.Log($"[Nanite][RF] RenderGraph pass active: camera={camera.name}, stage={stage}");
        }

        void ResolveHzbDimensions(int cameraWidth, int cameraHeight, out int hzbWidth, out int hzbHeight)
        {
            hzbWidth = Mathf.Max(1, cameraWidth);
            hzbHeight = Mathf.Max(1, cameraHeight);
            if (settings.hzbHalfResolution)
            {
                hzbWidth = Mathf.Max(1, hzbWidth >> 1);
                hzbHeight = Mathf.Max(1, hzbHeight >> 1);
            }
        }

        CameraHzbState GetOrCreateState(int width, int height, int cameraId)
        {
            EnsureInternalCollections();
            if (!cameraHzb.TryGetValue(cameraId, out var state))
            {
                state = new CameraHzbState();
                cameraHzb[cameraId] = state;
            }

            ResolveHzbDimensions(width, height, out int hzbWidth, out int hzbHeight);
            int mipCount = ComputeMipCount(hzbWidth, hzbHeight, settings.hzbMinMipSize);
            if (state.current == null || state.previous == null || state.width != hzbWidth || state.height != hzbHeight || state.mipCount != mipCount)
            {
                ReleaseState(state);
                state.width = hzbWidth;
                state.height = hzbHeight;
                state.mipCount = mipCount;
                state.current = CreateHzbTexture(state.width, state.height, state.mipCount, $"NaniteHZB_Current_{cameraId}");
                state.previous = CreateHzbTexture(state.width, state.height, state.mipCount, $"NaniteHZB_Previous_{cameraId}");
                state.hasPrevious = false;
                state.builtFrame = -1;
            }

            return state;
        }

        static void SwapHistory(CameraHzbState state)
        {
            if (state == null || state.current == null || state.previous == null)
                return;

            (state.current, state.previous) = (state.previous, state.current);
            state.hasPrevious = true;
        }

        static int ComputeMipCount(int width, int height, int minMipSize = 1)
        {
            int size = Mathf.Max(Mathf.Max(1, width), Mathf.Max(1, height));
            int stopSize = Mathf.Max(1, minMipSize);
            int mip = 1;
            while (size > stopSize && mip < 16)
            {
                size >>= 1;
                mip++;
            }

            return mip;
        }

        static RenderTexture CreateHzbTexture(int width, int height, int mipCount, string name)
        {
            var rt = new RenderTexture(width, height, 0, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear)
            {
                name = name,
                enableRandomWrite = true,
                useMipMap = true,
                autoGenerateMips = false,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                anisoLevel = 0,
                antiAliasing = 1
            };
            rt.Create();
            return rt;
        }

        static void ReleaseState(CameraHzbState state)
        {
            if (state == null)
                return;
            if (state.current != null)
            {
                state.current.Release();
                DestroyObject(state.current);
                state.current = null;
            }

            if (state.previous != null)
            {
                state.previous.Release();
                DestroyObject(state.previous);
                state.previous = null;
            }
            state.hasPrevious = false;
            state.width = 0;
            state.height = 0;
            state.mipCount = 0;
            state.builtFrame = -1;
            state.depthWrittenThisFrame = false;
            state.depthCopiedForHzb = false;
            state.currentHzbValid = false;
            if (state.depthSample != null)
            {
                state.depthSample.Release();
                DestroyObject(state.depthSample);
                state.depthSample = null;
            }
        }

        Material EnsureCopyDepthMaterial()
        {
            if (runtimeCopyDepthMaterial != null)
                return runtimeCopyDepthMaterial;

            var shader = Shader.Find("Hidden/Universal Render Pipeline/CopyDepth");
            if (shader == null)
                return null;
            runtimeCopyDepthMaterial = new Material(shader) { name = "Nanite_CopyDepth_Runtime" };
            return runtimeCopyDepthMaterial;
        }

        Material EnsureVBufferPreviewMaterial()
        {
            if (settings.vbufferPreviewMaterial != null)
                return settings.vbufferPreviewMaterial;

            if (runtimeVBufferPreviewMaterial != null)
                return runtimeVBufferPreviewMaterial;

            var shader = Shader.Find("Nanite/VBufferPacketRaster");
            if (shader == null)
                return null;
            runtimeVBufferPreviewMaterial = new Material(shader) { name = "Nanite_VBufferPacketRaster_Runtime" };
            ConfigurePackedDirectDiagnosticKeyword(runtimeVBufferPreviewMaterial);
            return runtimeVBufferPreviewMaterial;
        }

        Material EnsureVBufferDecodeMaterial()
        {
            if (settings.vbufferDecodeMaterial != null)
                return settings.vbufferDecodeMaterial;

            if (runtimeVBufferDecodeMaterial != null)
                return runtimeVBufferDecodeMaterial;

            var shader = Shader.Find("Nanite/VBufferDecode");
            if (shader == null)
                return null;
            runtimeVBufferDecodeMaterial = new Material(shader) { name = "Nanite_VBufferDecode_Runtime" };
            return runtimeVBufferDecodeMaterial;
        }

        Material EnsureDepthWriteMaterial()
        {
            if (runtimeDepthWriteMaterial != null)
                return runtimeDepthWriteMaterial;

            var shader = Shader.Find("Nanite/VBufferDepthWrite");
            if (shader == null)
                return null;
            runtimeDepthWriteMaterial = new Material(shader) { name = "Nanite_VBufferDepthWrite_Runtime" };
            ConfigurePackedDirectDiagnosticKeyword(runtimeDepthWriteMaterial);
            return runtimeDepthWriteMaterial;
        }

        Material EnsureShadowCasterMaterial()
        {
            if (runtimeShadowCasterMaterial != null)
                return runtimeShadowCasterMaterial;

            var shader = Shader.Find("Nanite/VBufferShadowCaster");
            if (shader == null)
                return null;
            runtimeShadowCasterMaterial = new Material(shader) { name = "Nanite_VBufferShadowCaster_Runtime" };
            ConfigurePackedDirectDiagnosticKeyword(runtimeShadowCasterMaterial);
            return runtimeShadowCasterMaterial;
        }

        Material EnsureVBufferLitResolveMaterial()
        {
            if (settings.vbufferLitResolveMaterial != null)
                return settings.vbufferLitResolveMaterial;

            if (runtimeVBufferLitResolveMaterial != null)
                return runtimeVBufferLitResolveMaterial;

            var shader = Shader.Find("Nanite/VBufferLitResolve");
            if (shader == null)
                return null;
            runtimeVBufferLitResolveMaterial = new Material(shader) { name = "Nanite_VBufferLitResolve_Runtime" };
            ConfigurePackedDirectDiagnosticKeyword(runtimeVBufferLitResolveMaterial);
            return runtimeVBufferLitResolveMaterial;
        }

        void ReleaseRuntimeMaterial()
        {
            if (runtimeVBufferPreviewMaterial != null)
            {
                DestroyObject(runtimeVBufferPreviewMaterial);
                runtimeVBufferPreviewMaterial = null;
            }
            if (runtimeVBufferDecodeMaterial != null)
            {
                DestroyObject(runtimeVBufferDecodeMaterial);
                runtimeVBufferDecodeMaterial = null;
            }
            if (runtimeDepthWriteMaterial != null)
            {
                DestroyObject(runtimeDepthWriteMaterial);
                runtimeDepthWriteMaterial = null;
            }
            if (runtimeShadowCasterMaterial != null)
            {
                DestroyObject(runtimeShadowCasterMaterial);
                runtimeShadowCasterMaterial = null;
            }
            if (runtimeVBufferLitResolveMaterial != null)
            {
                DestroyObject(runtimeVBufferLitResolveMaterial);
                runtimeVBufferLitResolveMaterial = null;
            }
            if (runtimeCopyDepthMaterial != null)
            {
                DestroyObject(runtimeCopyDepthMaterial);
                runtimeCopyDepthMaterial = null;
            }
        }

        void ReleaseFormalBuffers()
        {
            tileMaterialMaskBuffer?.Release();
            tileIndirectArgsBuffer?.Release();
            tileMaterialMaskBuffer = null;
            tileIndirectArgsBuffer = null;
            tileMaskCapacity = 0;
            tileCountX = 0;
            tileCountY = 0;
            tileCount = 0;
        }

        static void DestroyObject(UnityEngine.Object obj)
        {
            if (obj == null)
                return;
            if (Application.isPlaying)
                UnityEngine.Object.Destroy(obj);
            else
                UnityEngine.Object.DestroyImmediate(obj);
        }

        void EnsureInternalCollections()
        {
            cameraHzb ??= new Dictionary<int, CameraHzbState>();
            firstSelections ??= new Dictionary<int, NaniteRuntimeSelection>();
            secondSelections ??= new Dictionary<int, NaniteRuntimeSelection>();
            mergedSelections ??= new Dictionary<int, NaniteRuntimeSelection>();
            mergedVisibleScratch ??= new Dictionary<int, List<NaniteVisibleClusterRef>>();
            mergedKeyScratch ??= new Dictionary<int, HashSet<ulong>>();
        }

        MaterialPropertyBlock GetOrCreateVBufferPropertyBlock()
        {
            if (vbufferMpb == null)
                vbufferMpb = new MaterialPropertyBlock();
            return vbufferMpb;
        }

        static NaniteRuntimeSelection GetOrCreateSelection(Dictionary<int, NaniteRuntimeSelection> dict, int key)
        {
            if (dict == null)
                return new NaniteRuntimeSelection();
            if (!dict.TryGetValue(key, out var sel))
            {
                sel = new NaniteRuntimeSelection();
                dict[key] = sel;
            }

            return sel;
        }

        List<NaniteVisibleClusterRef> GetOrCreateMergedVisibleScratch(int proxyId)
        {
            if (mergedVisibleScratch == null)
                mergedVisibleScratch = new Dictionary<int, List<NaniteVisibleClusterRef>>();
            if (!mergedVisibleScratch.TryGetValue(proxyId, out var list))
            {
                list = new List<NaniteVisibleClusterRef>(4096);
                mergedVisibleScratch[proxyId] = list;
            }

            return list;
        }

        HashSet<ulong> GetOrCreateMergedKeyScratch(int proxyId)
        {
            if (mergedKeyScratch == null)
                mergedKeyScratch = new Dictionary<int, HashSet<ulong>>();
            if (!mergedKeyScratch.TryGetValue(proxyId, out var set))
            {
                set = new HashSet<ulong>();
                mergedKeyScratch[proxyId] = set;
            }

            return set;
        }

        void MergeSelections(
            NaniteMesh mesh,
            int proxyId,
            NaniteRuntimeSelection first,
            NaniteRuntimeSelection second,
            NaniteRuntimeSelection merged)
        {
            merged.Clear();
            if (mesh == null)
                return;

            var mergedVisible = GetOrCreateMergedVisibleScratch(proxyId);
            var mergedKeys = GetOrCreateMergedKeyScratch(proxyId);
            mergedVisible.Clear();
            mergedKeys.Clear();

            for (int i = 0; i < first.visibleClusters.Count; i++)
            {
                var vr = first.visibleClusters[i];
                ulong key = ((ulong)(uint)vr.pageIndex << 32) | (uint)vr.clusterIndex;
                if (mergedKeys.Add(key))
                    mergedVisible.Add(vr);
            }

            for (int i = 0; i < second.visibleClusters.Count; i++)
            {
                var vr = second.visibleClusters[i];
                ulong key = ((ulong)(uint)vr.pageIndex << 32) | (uint)vr.clusterIndex;
                if (mergedKeys.Add(key))
                    mergedVisible.Add(vr);
            }

            var mergedStats = new NaniteCullingStats
            {
                testedInstances = first.stats.testedInstances + second.stats.testedInstances,
                testedNodes = first.stats.testedNodes + second.stats.testedNodes,
                testedParts = first.stats.testedParts + second.stats.testedParts,
                testedClusters = first.stats.testedClusters + second.stats.testedClusters,
                visibleClusters = mergedVisible.Count
            };
            NaniteRuntimeCulling.BuildSelectionFromVisibleClusters(mesh, mergedVisible, mergedStats, merged);
        }
    }
}
