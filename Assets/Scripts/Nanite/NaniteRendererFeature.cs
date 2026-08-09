using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using Unity.Profiling;
using UnityEngine.Profiling;
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
        public static NaniteRendererFeature ActiveInstance { get; private set; }
        static readonly ProfilerMarker kFirstCullMarker = new ProfilerMarker("Nanite.CPU.FirstCull");
        static readonly ProfilerMarker kScenePrepareMarker = new ProfilerMarker("Nanite.CPU.ScenePrepare");
        static readonly ProfilerMarker kResolveSubmitMarker = new ProfilerMarker("Nanite.CPU.ResolveSubmit");
        static readonly ProfilerMarker kShadowSubmitMarker = new ProfilerMarker("Nanite.CPU.ShadowSubmit");
        static readonly ProfilerMarker kShadowCullPrepareMarker = new ProfilerMarker("Nanite.CPU.ShadowCullPrepare");
        static readonly ProfilerMarker kShadowCullRecordMarker = new ProfilerMarker("Nanite.CPU.ShadowCullRecord");
        enum FormalRasterizationMode
        {
            HardwareOnly = 0,
            HybridSoftwareHardware = 1
        }

        enum NaniteRenderPath
        {
            Unsupported = 0,
            HardwareCompat = 1,
            HardwareFast = 2,
            Hybrid = 3
        }

        enum CompactVBufferProbeState
        {
            NotStarted,
            Pending,
            Passed,
            Failed
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

        [System.Serializable]
        public class Settings
        {
            public bool enable = true;
            [Tooltip("Negotiate one validated DX12 render path at runtime. Legacy feature switches become preferences; missing kernels/resources automatically fall back to the last complete path.")]
            public bool automaticPathNegotiation = true;
            [Tooltip("SecondCull 启用本帧 HZB 遮挡剔除。移动相机时若仍破洞，先关掉此项验证。")]
            public bool useHzbCulling = false;
            [Tooltip("按场景 virtual cluster 规模自动准入 HZB。小场景跳过 Depth/Copy/HZB/Cull2 的固定成本。关闭后 useHzbCulling=true 表示始终启用。")]
            public bool adaptiveHzbCulling = true;
            [Tooltip("自动 HZB 的最小 virtual cluster 数。低于此值只执行 GPU 视锥/LOD；0 表示不设门槛。")]
            [Min(0)] public int hzbMinClusterCount = 50000;
            [Tooltip("Previous-frame HZB traversal is quarantined until conservative atomic-cut occlusion passes image-difference validation.")]
            public bool usePreviousHzbOnFirstCull = false;
            [Tooltip("调试/兼容回退：允许 CPU BVH 生成 cluster 候选。正式 GPU-driven 配置应关闭。")]
            public bool useBvhCandidates = true;
            [Tooltip("CPU BVH 允许的最大实例数。小场景只由 CPU 生成保守候选，Cluster 剔除与绘制仍在 GPU；0 表示始终使用 GPU Instance -> Part -> Cluster。")]
            [Min(0)] public int cpuBvhMaxInstances = 8;
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
            [Tooltip("Nanite has a non-trivial fixed VBuffer/resolve cost. When the whole active scene can be submitted by only a few native draws, hand it back to URP even if the source mesh is dense. forceNaniteRendering still wins.")]
            public bool enableLowSubmissionRasterFallback = true;
            [Tooltip("Maximum active fallback-capable instances for the low-submission native path.")]
            [Min(0)] public int lowSubmissionMaxInstances = 48;
            [Tooltip("Maximum aggregate source submesh draws for the low-submission native path.")]
            [Min(0)] public int lowSubmissionMaxDraws = 64;
            [Tooltip("Maximum aggregate source triangles for the low-submission native path. This prevents a few extreme meshes from bypassing virtual geometry.")]
            [Min(0)] public int lowSubmissionMaxTriangles = 4000000;
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
            [Header("Formal VisibilityBuffer")]
            [Tooltip("正式 GPU-driven Visibility Buffer 链路；仅在原生渲染对照或诊断时关闭。")]
            public bool enableFormalVisibilityBuffer = true;
            [Tooltip("请求 RG32UI 完整 ID 路径；启动时必须通过真实 render/load 探针，否则自动使用已验证的 RG32F normalized32（再回退 RGBA32F split16）。")]
            public bool enableCompactFormalVBuffer = false;
            [Tooltip("按材质 bin 压缩 resolve tile；仅在材质数量达到成本门槛时启用，少材质场景保持关闭。")]
            public bool enableMaterialTileCulling = false;
            [Tooltip("Only register the tile classify pass when enough distinct materials can amortize its fixed dispatch cost.")]
            public bool adaptiveMaterialTileCulling = true;
            [Tooltip("Minimum executed shader/texture bin count for adaptive tile classification. Materials folded into one compatible bin count once.")]
            [Min(1)] public int materialTileMinMaterialCount = 2;
            [Tooltip("实例未移动时刷新 Light Probe SH 的间隔。0=只在移动时刷新；大场景建议 30~120。")]
            [Min(0)] public int lightProbeRefreshInterval = 30;
            [Header("GPU Page Pool")]
            [Tooltip("Use NPG1/NZC1 storage and transcode only resident Pages into the aligned GPU geometry cache. Root Pages stay pinned; finer Pages stream on GPU demand.")]
            public bool enablePackedPageRaster = true;
            [Tooltip("Diagnostic only: compile and select the direct NPG1 Page decode variant. Production keeps this disabled and reads only the resident geometry cache.")]
            public bool enablePackedDirectDiagnostic = false;
            [Tooltip("One-time GPU transcode from resident NPG1 Pages into aligned draw-time geometry. Required by the production packed Page path.")]
            public ComputeShader pageTranscodeShader;
            [Tooltip("Asynchronously read GPU Page requests and upload non-root Pages. Required by demand-resident packed raster; no synchronous GetData is used.")]
            public bool enablePageStreamingUploads = true;
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
            [Tooltip("Upper diameter guard for the HW/SW cost model. Software raster also requires conservative projected coverage <= one pixel per triangle; this Unity two-pass compatibility backend has a lower crossover than UE's native 64-bit atomic path.")]
            [Range(1f, 16f)] public float hybridSoftwareMaxEdgePixels = 1f;
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
            [Tooltip("把 visible-cluster queue 展开为精确 32-bit triangle packet；主视图和阴影均由 GPU 直接生成 DrawIndirect。超出预算时才回退 padded procedural tail。")]
            public bool enableIndexedClusterRaster = false;
            [Tooltip("主视图与每级阴影按 build->raster 顺序复用全部 packet scratch；默认 128 MiB 可容纳约 262K 个 128-triangle cluster。")]
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
            public bool historyViewValid;
            public Matrix4x4 historyViewProjection;
            public NaniteGpuBatchedCullingBackend.HzbViewParameters historyHzbView;
            public int historyTransformGeneration = -1;
            public int historyGeometryGeneration = -1;
            public int historyRegistryRevision = -1;
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
            public static readonly int ReversedZ = Shader.PropertyToID("_ReversedZ");
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
            public static readonly int InstanceMaterialRange = Shader.PropertyToID("_InstanceMaterialRange");
            public static readonly int MaterialData = Shader.PropertyToID("_NaniteMaterialData");
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
            public static readonly int CompactedClusterOffset = Shader.PropertyToID("_CompactedClusterOffset");
            public static readonly int UseIndexedShadowDynamicSlice = Shader.PropertyToID("_UseIndexedShadowDynamicSlice");
            public static readonly int IndexedShadowCascadeIndex = Shader.PropertyToID("_IndexedShadowCascadeIndex");
            public static readonly int IndexedShadowSliceData = Shader.PropertyToID("_IndexedShadowSliceData");
            public static readonly int DebugVizMode = Shader.PropertyToID("_DebugVizMode");
            public static readonly int TileMaterialMask = Shader.PropertyToID("_TileMaterialMask");
            public static readonly int TileMaterialBinList = Shader.PropertyToID("_TileMaterialBinList");
            public static readonly int TileMaterialBinArgs = Shader.PropertyToID("_TileMaterialBinArgs");
            public static readonly int UseCompactedTileBins = Shader.PropertyToID("_UseCompactedTileBins");
            public static readonly int ResolveMaterialId = Shader.PropertyToID("_ResolveMaterialId");
            public static readonly int ResolveMaterialMode = Shader.PropertyToID("_ResolveMaterialMode");
            public static readonly int ResolveMaterialFamily = Shader.PropertyToID("_ResolveMaterialFamily");
            public static readonly int ResolveAbsorbedFamily = Shader.PropertyToID("_ResolveAbsorbedFamily");
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
            public static readonly int IndexedTrianglePackets = Shader.PropertyToID("_IndexedTrianglePackets");
            public static readonly int IndexedTrianglePacketBase = Shader.PropertyToID("_IndexedTrianglePacketBase");
            public static readonly int IndexedPacketInstanceBits = Shader.PropertyToID("_IndexedPacketInstanceBits");
            public static readonly int IndexedPacketInstanceMask = Shader.PropertyToID("_IndexedPacketInstanceMask");
            public static readonly int IndexedCameraPacketSlice = Shader.PropertyToID("_IndexedCameraPacketSlice");
            public static readonly int UseIndexedPacketDynamicBase = Shader.PropertyToID("_UseIndexedPacketDynamicBase");
            public static readonly int IndexedOverflowClusterIndices = Shader.PropertyToID("_IndexedOverflowClusterIndices");
            public static readonly int UseIndexedOverflowClusterIndices = Shader.PropertyToID("_UseIndexedOverflowClusterIndices");
            public static readonly int NaniteSoftwareDepth = Shader.PropertyToID("_NaniteSoftwareDepth");
            public static readonly int NaniteSoftwareWinner = Shader.PropertyToID("_NaniteSoftwareWinner");
            public static readonly int NaniteSoftwareClusters = Shader.PropertyToID("_NaniteSoftwareClusters");
            public static readonly int NaniteSoftwareTileList = Shader.PropertyToID("_NaniteSoftwareTileList");
            public static readonly int NaniteSoftwareScreenWidth = Shader.PropertyToID("_NaniteSoftwareScreenWidth");
            public static readonly int NaniteSoftwareScreenHeight = Shader.PropertyToID("_NaniteSoftwareScreenHeight");
            public static readonly int NaniteSoftwareTileCountX = Shader.PropertyToID("_NaniteSoftwareTileCountX");
            public static readonly int NaniteSoftwareTileSize = Shader.PropertyToID("_NaniteSoftwareTileSize");
            public static readonly int NaniteHybridFloat2VBuffer = Shader.PropertyToID("_NaniteHybridFloat2VBuffer");
            public static readonly int NaniteSoftwareViewportOrigin = Shader.PropertyToID("_NaniteSoftwareViewportOrigin");
        }

        public Settings settings = new Settings();

        // Non-serialized acceptance-harness override. Production still uses the
        // measured admission state; this exists only so strict HW/SW A/B runs do
        // not silently switch back to hardware while being labelled "hybrid".
        internal bool ForceHybridForDiagnostics { get; set; }
        internal bool DisableShadowHybridForDiagnostics { get; set; }
        internal float ShadowLodMinTexelsForDiagnostics { get; set; } = 1f;

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
        int kernelPrepareIndexedDrawQueueAppend = -1;
        int kernelBuildIndexedDrawQueueAppend = -1;
        int kernelPrepareIndexedShadowDrawQueue = -1;
        int kernelBuildIndexedShadowDrawQueue = -1;
        int kernelPrepareHybridDispatch = -1;
        int kernelClearHybridTileHeads = -1;
        int kernelClearHybridTileWork = -1;
        int kernelBuildHybridTileWork = -1;
        int kernelSoftwareRasterTiles = -1;
        int kernelClassifyHybridClusters = -1;
        int kernelCaptureHybridView = -1;
        int kernelPrepareRuntimeTelemetry = -1;
        int kernelAccumulateRuntimeTelemetry = -1;

        const int kCompactSelectionFirst = 1;
        const int kCompactSelectionMerged = 2;
        const int kCompactSelectionPass2 = 3;
        int lastCompactFrame = -1;
        int lastCompactCameraId;
        int lastCompactSelectionKey;
        int lastCompactGeometryGeneration = -1;
        bool lastCompactSucceeded;
        bool lastCompactUsedDirectQueue;
        int lastIndexedDrawFrame = -1;
        int lastIndexedDrawSelectionKey;
        int lastIndexedDrawGeometryGeneration = -1;
        int lastIndexedDrawCameraId;
        bool lastIndexedDrawAppendedPass2;
        bool loggedPacketAppendOnce;
        bool loggedCompactStatsOnce;
        bool loggedExecutionOnce;
        bool loggedVBufferOnce;
        bool loggedDebugVizOnce;
        bool loggedFormalRasterDiagnostics;
        Material runtimeVBufferPreviewMaterial;
        Material runtimeVBufferDecodeMaterial;
        Material runtimeVBufferDebugResolveMaterial;
        Material runtimeDepthWriteMaterial;
        Material runtimeShadowCasterMaterial;
        Material runtimeVBufferLitResolveMaterial;
        Material runtimeCopyDepthMaterial;
        bool loggedFormalRasterStats;
        bool loggedPass1VBufferFusion;
        TextureHandle recordedHzbDepthSource;
        // Current-camera RenderGraph handle. When HZB is active, WriteDepth can
        // populate Pass1 visibility IDs while producing the HZB depth source.
        TextureHandle recordedPass1VBuffer;
        TextureHandle recordedFormalVBuffer;

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct RasterDiagDrawCluster
        {
            public uint firstTriangle;
            public uint triangleCount;
            public uint instanceIndex;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct RasterDiagTrianglePageRef
        {
            public uint localIndexOffset;
            public uint pageId;
        }
        bool loggedPassOrderWarning;
        bool loggedFormalOrderWarning;
        bool loggedDispatchStats;
        bool loggedBatchedDispatchStats;
        bool loggedFormalOnce;
        bool loggedFormalDebugMeshWarning;
        bool loggedHybridRasterWarning;
        bool loggedFormalSkipReason;
        bool loggedFormalRecordSuccess;
        bool loggedUnsupportedGraphicsApi;
        NaniteRenderPath negotiatedRenderPath = NaniteRenderPath.Unsupported;
        string negotiatedRenderPathReason = "not evaluated";
        int negotiatedCapabilitySignature = int.MinValue;
        bool indexedKernelContractValid;
        bool hybridKernelContractValid;
        bool hzbKernelContractValid;
        bool kernelInitializationComplete;
        CompactVBufferProbeState compactVBufferProbeState;
        string compactVBufferProbeReason = "not started";
        RenderTexture compactVBufferProbeTexture;
        Material compactVBufferProbeMaterial;
        int compactVBufferProbeGeneration;
        NaniteGpuBatchedCullingBackend batchedCulling;
        NaniteSceneVisibilityBufferBackend sceneVisibilityBackend;

        /// <summary>Frame-scoped read-only export of the published live resident-page front table.</summary>
        public bool TryGetResidentPageReadOnlyView(out NaniteResidentPageReadOnlyView view)
        {
            if (sceneVisibilityBackend != null)
                return sceneVisibilityBackend.TryGetResidentPageReadOnlyView(out view);
            view = default;
            return false;
        }

        public bool TryGetResidentMeshIndex(NaniteMesh mesh, out int meshIndex)
        {
            if (sceneVisibilityBackend != null)
                return sceneVisibilityBackend.TryGetResidentMeshIndex(mesh, out meshIndex);
            meshIndex = -1;
            return false;
        }

        public bool TryGetResidentMeshPageRange(
            NaniteMesh mesh, out int firstPageId, out int pageCount, out int meshIndex)
        {
            if (sceneVisibilityBackend != null)
                return sceneVisibilityBackend.TryGetResidentMeshPageRange(
                    mesh, out firstPageId, out pageCount, out meshIndex);
            firstPageId = -1;
            pageCount = 0;
            meshIndex = -1;
            return false;
        }

        public bool TryGetResidentPageId(
            NaniteMesh mesh, int localPageIndex, out int pageId)
        {
            if (sceneVisibilityBackend != null)
                return sceneVisibilityBackend.TryGetResidentPageId(
                    mesh, localPageIndex, out pageId);
            pageId = -1;
            return false;
        }
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
        bool indexedAdmissionProbeRequested;
        int indexedAdmissionProbeVersion;
        int indexedAdmissionProbeWarmupVersion;
        int indexedAdmissionProbeWarmupFrame;
        int indexedAdmissionProbePending;
        readonly uint[] indexedAdmissionProbeCounts = new uint[7];
        int indexedAdmissionProbeShadowMask;
        bool indexedAdmissionProbeFailed;
        int indexedCameraChunkHint = 1;
        int indexedHybridChunkHint = 1;
        int lastAutomaticCullStatsFrame = -1;
        // HZB is a conditional accelerator. A large scene does not justify an extra
        // depth pass, pyramid build and second traversal when they reject nothing.
        const int kHzbAdmissionProbeInterval = 10;
        const int kHzbAdmissionSteadyInterval = 120;
        const int kHzbAdmissionNoBenefitSamples = 3;
        const int kHzbAdmissionCooldownFrames = 600;
        // Measured DX12 crossover: a few hundred/thousand rejections do not
        // repay the pyramid and recovery pass when GPU timing is unavailable.
        const float kHzbAdmissionMinRejectRatio = 0.20f;
        const int kHzbAdmissionMinRejectedClusters = 12000;
        bool hzbAdmissionAllowed = true;
        bool hzbAdmissionProbeMode = true;
        int hzbAdmissionCameraId;
        int hzbAdmissionRegistryRevision = -1;
        int hzbAdmissionGeometryGeneration = -1;
        int hzbAdmissionWidth;
        int hzbAdmissionHeight;
        int hzbAdmissionResetFrame = -1;
        int hzbAdmissionLastStatsFrame = -1;
        int hzbAdmissionNoBenefitCount;
        int hzbAdmissionDisabledUntilFrame = -1;
        enum HzbCostProbePhase
        {
            MeasureEnabled,
            MeasureDisabled,
            SteadyEnabled,
            SteadyDisabled
        }
        const int kHzbCostWarmupFrames = 8;
        const int kHzbCostSampleCount = 12;
        const int kHzbCostReprobeFrames = 600;
        const int kGpuTimingUnavailableFrames = 60;
        const double kHzbCostDecisionMarginMs = 0.05;
        Recorder hzbWriteDepthGpuRecorder;
        Recorder hzbBuildGpuRecorder;
        Recorder hzbSecondCullGpuRecorder;
        Recorder hzbFormalGpuRecorder;
        HzbCostProbePhase hzbCostProbePhase = HzbCostProbePhase.MeasureEnabled;
        int hzbCostPhaseStartFrame = -1;
        int hzbCostSamples;
        double hzbCostEnabledTotalMs;
        double hzbCostEnabledFormalMs;
        double hzbCostDisabledFormalMs;
        double hzbCostLastEnabledMs;
        double hzbCostLastDisabledMs;
        int hzbMissingGpuTimingFrames;
        int hzbGpuTimingRetryFrame;
        bool hzbGpuTimingUnavailable;
        bool loggedHzbGpuTimingUnavailable;
        sealed class HybridCostAdmissionState
        {
            internal bool steady;
            internal bool testingEnabled = true;
            internal bool allowed = true;
            internal int phaseStartFrame = -1;
            internal int samples;
            internal double enabledTotalMs;
            internal double disabledTotalMs;
            internal double lastEnabledMs;
            internal double lastDisabledMs;
            internal int nextProbeFrame;
            internal int missingTimingFrames;

            internal bool UseHybrid => steady ? allowed : testingEnabled;

            internal void Reset(int frame)
            {
                steady = false;
                testingEnabled = true;
                allowed = true;
                phaseStartFrame = frame;
                samples = 0;
                enabledTotalMs = 0.0;
                disabledTotalMs = 0.0;
                nextProbeFrame = 0;
                missingTimingFrames = 0;
            }

            internal void DisableWithoutTiming(int frame)
            {
                steady = true;
                testingEnabled = false;
                allowed = false;
                samples = 0;
                missingTimingFrames = 0;
                phaseStartFrame = frame;
                nextProbeFrame = frame + kHybridCostReprobeFrames;
            }
        }
        const int kHybridCostWarmupFrames = 8;
        const int kHybridCostSampleCount = 12;
        const int kHybridCostReprobeFrames = 600;
        const double kHybridCostDecisionMarginMs = 0.05;
        readonly HybridCostAdmissionState cameraHybridCost = new HybridCostAdmissionState();
        readonly HybridCostAdmissionState shadowHybridCost = new HybridCostAdmissionState();
        Recorder hybridFormalGpuRecorder;
        Recorder hybridShadowCullGpuRecorder;
        Recorder hybridShadowDrawGpuRecorder;
        int hybridCostCameraId;
        int hybridCostRegistryRevision = -1;
        int hybridCostGeometryGeneration = -1;
        int hybridCostWidth;
        int hybridCostHeight;
        int hybridCostLastUpdateFrame = -1;
        bool loggedHybridGpuTimingUnavailable;
        ComputeBuffer runtimeTelemetryStatsBuffer;
        ComputeBuffer runtimeTelemetryDispatchArgsBuffer;
        bool runtimeTelemetryReadbackPending;
        int runtimeTelemetryEpoch;
        bool visibleLodAuditRequested;
        int visibleLodAuditVersion;
        ComputeBuffer hybridHardwareClusterBuffer;
        ComputeBuffer hybridSoftwareClusterBuffer;
        ComputeBuffer hybridHardwareCountArgsBuffer;
        ComputeBuffer hybridSoftwareCountArgsBuffer;
        ComputeBuffer hybridClassifyDispatchArgsBuffer;
        ComputeBuffer hybridSoftwareDispatchArgsBuffer;
        GraphicsBuffer hybridDependencyBuffer;
        GraphicsBuffer hybridViewConstantsBuffer;
        bool hybridSoftwareAuditPending;
        bool hybridSoftwareAuditComplete;
        GraphicsBuffer hybridTraversalDependencyBuffer;
        GraphicsBuffer hybridSoftwareTileHeadBuffer;
        GraphicsBuffer hybridSoftwareTileNodeBuffer;
        GraphicsBuffer hybridSoftwareTileNodeCounterBuffer;
        GraphicsBuffer hybridSoftwareFallbackFlagsBuffer;
        GraphicsBuffer hybridSoftwareTileListBuffer;
        GraphicsBuffer hybridSoftwareTileCountArgsBuffer;
        GraphicsBuffer hybridSoftwareTileDispatchArgsBuffer;
        GraphicsBuffer hybridSoftwareTileDrawArgsBuffer;
        int hybridSoftwareTileCapacity;
        int hybridSoftwareTileNodeCapacity;
        int hybridSoftwareFallbackCapacity;
        RenderTexture hybridShadowSoftwareDepth;
        RenderTexture hybridShadowWinnerFallback;
        int hybridShadowTextureSize;
        int hybridShadowReadyFrame = -1;
        int hybridShadowReadyCascade = -1;
        const int kHybridSoftwareTileSize = 16;
        const int kHybridSoftwareTileNodesPerTile = 128;
        sealed class RetiredHybridBuffers
        {
            internal ComputeBuffer hardwareClusters;
            internal ComputeBuffer softwareClusters;
            internal ComputeBuffer hardwareCountArgs;
            internal ComputeBuffer softwareCountArgs;
            internal ComputeBuffer classifyDispatchArgs;
            internal ComputeBuffer softwareDispatchArgs;
            internal GraphicsBuffer dependency;
            internal GraphicsBuffer viewConstants;
            internal int releaseFrame;

            internal void Release()
            {
                hardwareClusters?.Release();
                softwareClusters?.Release();
                hardwareCountArgs?.Release();
                softwareCountArgs?.Release();
                classifyDispatchArgs?.Release();
                softwareDispatchArgs?.Release();
                dependency?.Dispose();
                viewConstants?.Dispose();
            }
        }
        readonly List<RetiredHybridBuffers> retiredHybridBuffers = new List<RetiredHybridBuffers>(4);
        sealed class RetiredHybridTileBuffers
        {
            internal GraphicsBuffer heads;
            internal GraphicsBuffer nodes;
            internal GraphicsBuffer nodeCounter;
            internal GraphicsBuffer fallbackFlags;
            internal GraphicsBuffer list;
            internal GraphicsBuffer countArgs;
            internal GraphicsBuffer dispatchArgs;
            internal GraphicsBuffer drawArgs;
            internal int releaseFrame;

            internal void Release()
            {
                heads?.Dispose();
                nodes?.Dispose();
                nodeCounter?.Dispose();
                fallbackFlags?.Dispose();
                list?.Dispose();
                countArgs?.Dispose();
                dispatchArgs?.Dispose();
                drawArgs?.Dispose();
            }
        }
        readonly List<RetiredHybridTileBuffers> retiredHybridTileBuffers = new List<RetiredHybridTileBuffers>(2);
        int hybridClusterCapacity;
        int hybridGeometryGeneration = -1;
        int hybridQueueFrame = -1;
        int hybridQueueSelectionKey;
        bool hybridUsesTraversalBins;
        int hybridWorkspaceOwnerCameraId;
        Dictionary<int, CameraHzbState> cameraHzb = new Dictionary<int, CameraHzbState>();
        Dictionary<int, NaniteRuntimeSelection> firstSelections = new Dictionary<int, NaniteRuntimeSelection>();
        Dictionary<int, NaniteRuntimeSelection> secondSelections = new Dictionary<int, NaniteRuntimeSelection>();
        Dictionary<int, NaniteRuntimeSelection> mergedSelections = new Dictionary<int, NaniteRuntimeSelection>();
        Dictionary<int, List<NaniteVisibleClusterRef>> mergedVisibleScratch = new Dictionary<int, List<NaniteVisibleClusterRef>>();
        Dictionary<int, HashSet<ulong>> mergedKeyScratch = new Dictionary<int, HashSet<ulong>>();
        GraphicsBuffer tileMaterialMaskBuffer;
        GraphicsBuffer tileMaterialBinListBuffer;
        GraphicsBuffer tileIndirectArgsBuffer;
        const int kMaterialTileBinCount = 64;
        int tileMaskCapacity;
        int tileCountX;
        int tileCountY;
        int tileCount;
        int lastGpuScenePrepareFrame = -1;
        int lastGpuScenePrepareCameraId;
        int lastProxyStatsWritebackFrame = -1;
        int lastPipelineAdmissionFrame = -1;
        int lastPageStreamingUpdateFrame = -1;
        readonly HashSet<int> warnedUnsupportedMaterialProxies = new HashSet<int>();
        readonly HashSet<int> warnedResolveFamilyFailures = new HashSet<int>();
        readonly List<MaterialPropertyBlock> formalResolvePropertyBlocks =
            new List<MaterialPropertyBlock>(16);
        // RenderGraph records Material references, not snapshots of their local
        // keywords. One stable object per URP/Lit state prevents later draws
        // from overwriting the pipeline state of commands already recorded.
        readonly Material[] formalResolveStateMaterials = new Material[8];
        int formalResolveStateSourceId;
        int kernelMaterialTileClassify = -1;
        int kernelClearMaterialTileBins = -1;
        LocalKeyword keywordCompactVBufferClassify;
        LocalKeyword keywordFloat2VBufferClassify;
        bool keywordCompactVBufferClassifyInitialized;
        bool keywordFloat2VBufferClassifyInitialized;
        bool loggedCompactVBufferUnsupported;
        GlobalKeyword keywordDepthMsaa2;
        GlobalKeyword keywordDepthMsaa4;
        GlobalKeyword keywordDepthMsaa8;
        GlobalKeyword keywordOutputDepth;
        bool copyDepthKeywordsInitialized;
        Func<Camera, Light, bool> externalMainLightShadowProvider;
        Action<ExternalMainLightShadowCullContext> externalMainLightShadowCullProvider;
        Action<ExternalMainLightShadowCullBatchContext> externalMainLightShadowCullBatchProvider;
        Action<ExternalMainLightShadowDrawContext> externalMainLightShadowDrawProvider;
        readonly Matrix4x4[] shadowBatchViewMatrices = new Matrix4x4[4];
        readonly Matrix4x4[] shadowBatchProjectionMatrices = new Matrix4x4[4];
        readonly int[] shadowBatchResolutions = new int[4];
        const int kMaxShadowCasterPlanesPerCascade = 10;
        readonly Vector4[] shadowBatchCasterPlanes =
            new Vector4[4 * kMaxShadowCasterPlanesPerCascade];
        readonly int[] shadowBatchCasterPlaneCounts = new int[4];
        readonly Vector4[] shadowBatchCullingSpheres = new Vector4[4];
        bool loggedExternalShadowSchedulingOnce;
        bool loggedShadowExecutionOnce;
        bool loggedShadowCullScalesOnce;
        bool featureDriverRegistered;
        public override void Create()
        {
            ActiveInstance = this;
            if (!featureDriverRegistered)
            {
                NaniteRuntimeRegistry.RegisterFeatureDriver();
                featureDriverRegistered = true;
            }
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
            passDebugVisualization = new NaniteFeaturePass(
                this,
                PassKind.DrawDebugVisualization,
                RenderPassEvent.AfterRendering);
            passFormalVisibility = new NaniteFeaturePass(this, PassKind.FormalVisibility, settings.formalVBufferEvent);
            passPageRetirementFence = new NaniteFeaturePass(
                this,
                PassKind.PageRetirementFence,
                RenderPassEvent.AfterRendering);
            SyncPassEvents();
            kernelInitializationComplete = false;
            InitBuilderKernels();
            InitFormalKernels();
            InitCompactKernels();
            kernelInitializationComplete = true;
            RefreshNegotiatedRenderPath();
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
            NaniteRuntimeComputeResources runtimeResources =
                NaniteRuntimeComputeResources.Load();
            if (runtimeResources != null)
            {
                settings.gpuCullingShader ??= runtimeResources.gpuCullingShader;
                settings.materialTileClassifyShader ??= runtimeResources.materialTileClassifyShader;
                settings.visibleTriangleCompactShader ??= runtimeResources.visibleTriangleCompactShader;
                settings.pageTranscodeShader ??= runtimeResources.pageTranscodeShader;
                settings.hzbBuilderShader ??= runtimeResources.hzbBuilderShader;
            }
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
            kernelPrepareIndexedDrawQueueAppend = -1;
            kernelBuildIndexedDrawQueueAppend = -1;
            kernelPrepareIndexedShadowDrawQueue = -1;
            kernelBuildIndexedShadowDrawQueue = -1;
            kernelPrepareHybridDispatch = -1;
            kernelClearHybridTileHeads = -1;
            kernelClearHybridTileWork = -1;
            kernelBuildHybridTileWork = -1;
            kernelSoftwareRasterTiles = -1;
            kernelClassifyHybridClusters = -1;
            kernelCaptureHybridView = -1;
            kernelPrepareRuntimeTelemetry = -1;
            kernelAccumulateRuntimeTelemetry = -1;
            if (settings.visibleTriangleCompactShader == null)
            {
                indexedKernelContractValid = false;
                hybridKernelContractValid = false;
                RefreshNegotiatedRenderPath();
                return;
            }

            ComputeShader shader = settings.visibleTriangleCompactShader;
            kernelCompactClear = TryFindKernel(shader, "CSClear");
            kernelCompactTris = TryFindKernel(shader, "CSCompactClusters");
            kernelCompactFinalize = TryFindKernel(shader, "CSFinalizeArgs");
            kernelCompactFinalizeVisibleQueue = TryFindKernel(shader, "CSFinalizeVisibleDrawQueueArgs");
            kernelCompactFinalizeShadowQueues = TryFindKernel(shader, "CSFinalizeVisibleDrawQueueArgs4");

            kernelPrepareIndexedDrawQueue = TryFindKernel(shader, "CSPrepareIndexedDrawQueue");
            kernelBuildIndexedDrawQueue = TryFindKernel(shader, "CSBuildIndexedDrawQueue");
            kernelPrepareIndexedDrawQueueAppend = TryFindKernel(shader, "CSPrepareIndexedDrawQueueAppend");
            kernelBuildIndexedDrawQueueAppend = TryFindKernel(shader, "CSBuildIndexedDrawQueueAppend");
            kernelPrepareIndexedShadowDrawQueue = TryFindKernel(shader, "CSPrepareIndexedShadowDrawQueue");
            kernelBuildIndexedShadowDrawQueue = TryFindKernel(shader, "CSBuildIndexedShadowDrawQueue");
            indexedKernelContractValid =
                kernelPrepareIndexedDrawQueue >= 0 &&
                kernelBuildIndexedDrawQueue >= 0 &&
                kernelPrepareIndexedDrawQueueAppend >= 0 &&
                kernelBuildIndexedDrawQueueAppend >= 0 &&
                kernelPrepareIndexedShadowDrawQueue >= 0 &&
                kernelBuildIndexedShadowDrawQueue >= 0;

            kernelPrepareHybridDispatch = TryFindKernel(shader, "CSPrepareHybridDispatch");
            kernelClearHybridTileHeads = TryFindKernel(shader, "CSClearHybridTileHeads");
            kernelClearHybridTileWork = TryFindKernel(shader, "CSClearHybridTileWork");
            kernelBuildHybridTileWork = TryFindKernel(shader, "CSBuildHybridTileWork");
            kernelSoftwareRasterTiles = TryFindKernel(shader, "CSSoftwareRasterTiles");
            kernelClassifyHybridClusters = TryFindKernel(shader, "CSClassifyHybridClusters");
            kernelCaptureHybridView = TryFindKernel(shader, "CSCaptureHybridView");
            kernelPrepareRuntimeTelemetry = TryFindKernel(shader, "CSPrepareRuntimeTelemetry");
            kernelAccumulateRuntimeTelemetry = TryFindKernel(shader, "CSAccumulateRuntimeTelemetry");
            hybridKernelContractValid =
                kernelPrepareHybridDispatch >= 0 &&
                kernelClearHybridTileHeads >= 0 &&
                kernelClearHybridTileWork >= 0 &&
                kernelBuildHybridTileWork >= 0 &&
                kernelSoftwareRasterTiles >= 0 &&
                kernelClassifyHybridClusters >= 0 &&
                kernelCaptureHybridView >= 0;

            RefreshNegotiatedRenderPath();
        }

        static int TryFindKernel(ComputeShader shader, string kernelName)
        {
            if (shader == null || string.IsNullOrEmpty(kernelName))
                return -1;
            try
            {
                return shader.FindKernel(kernelName);
            }
            catch (Exception)
            {
                return -1;
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

        bool HasValidCompactForSelection(int selectionKey, Camera camera)
        {
            return IsVisibleTriangleCompactReady() &&
                   camera != null &&
                   lastCompactSucceeded &&
                   lastCompactFrame == Time.frameCount &&
                   lastCompactCameraId == camera.GetInstanceID() &&
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

            if (HasValidCompactForSelection(selectionKey, camera))
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
            lastCompactCameraId = camera.GetInstanceID();
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

            if (HasValidCompactForSelection(selectionKey, camera))
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
            lastCompactCameraId = camera.GetInstanceID();
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

        void BindCompactDrawState(CommandBuffer cmd, int selectionKey, Camera camera)
        {
            bool use = HasValidCompactForSelection(selectionKey, camera) &&
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

        void BindCompactDrawState(RasterCommandBuffer cmd, int selectionKey, Camera camera)
        {
            bool use = HasValidCompactForSelection(selectionKey, camera) &&
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

        void BindCompactDrawState(MaterialPropertyBlock mpb, int selectionKey, Camera camera)
        {
            bool use = HasValidCompactForSelection(selectionKey, camera) &&
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
            internal int clusterOffset;
            internal bool allowProceduralFallback;
            internal ComputeBuffer drawClusters;
            internal ComputeBuffer drawCountArgs;
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
            ProfilingSampler profilingSampler,
            int clusterOffset = 0,
            bool allowProceduralFallback = true,
            ComputeBuffer drawClusters = null,
            ComputeBuffer drawCountArgs = null,
            GraphicsBuffer dependencyBuffer = null)
        {
            if (!IsIndexedRasterRequested() || camera == null)
                return;

            using (var builder = renderGraph.AddUnsafePass<CompactRgPassData>(
                       "Nanite/BuildIndexedClusterQueue",
                       out var passData,
                       profilingSampler))
            {
                passData.camera = camera;
                passData.selectionKey = selectionKey;
                passData.clusterOffset = clusterOffset;
                passData.allowProceduralFallback = allowProceduralFallback;
                passData.drawClusters = drawClusters;
                passData.drawCountArgs = drawCountArgs;
                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);
                if (dependencyBuffer != null)
                    builder.UseBuffer(renderGraph.ImportBuffer(dependencyBuffer), AccessFlags.Read);
                if (sceneVisibilityBackend != null)
                {
                    if (sceneVisibilityBackend.IndexedTrianglePacketBuffer != null)
                    {
                        builder.UseBuffer(
                            renderGraph.ImportBuffer(sceneVisibilityBackend.IndexedTrianglePacketBuffer),
                            AccessFlags.ReadWrite);
                    }
                    if (sceneVisibilityBackend.IndexedDrawArgsBuffer != null)
                    {
                        builder.UseBuffer(
                            renderGraph.ImportBuffer(sceneVisibilityBackend.IndexedDrawArgsBuffer),
                            AccessFlags.ReadWrite);
                    }
                    if (sceneVisibilityBackend.IndexedAppendDrawArgsBuffer != null)
                    {
                        builder.UseBuffer(
                            renderGraph.ImportBuffer(sceneVisibilityBackend.IndexedAppendDrawArgsBuffer),
                            AccessFlags.ReadWrite);
                    }
                    if (sceneVisibilityBackend.IndexedFallbackDrawArgsBuffer != null)
                    {
                        builder.UseBuffer(
                            renderGraph.ImportBuffer(sceneVisibilityBackend.IndexedFallbackDrawArgsBuffer),
                            AccessFlags.Write);
                    }
                    if (sceneVisibilityBackend.IndexedOverflowClusterBuffer != null)
                    {
                        builder.UseBuffer(
                            renderGraph.ImportBuffer(sceneVisibilityBackend.IndexedOverflowClusterBuffer),
                            AccessFlags.ReadWrite);
                    }
                    if (sceneVisibilityBackend.IndexedCameraPacketSliceBuffer != null)
                    {
                        builder.UseBuffer(
                            renderGraph.ImportBuffer(sceneVisibilityBackend.IndexedCameraPacketSliceBuffer),
                            AccessFlags.ReadWrite);
                    }
                }
                builder.SetRenderFunc((CompactRgPassData data, UnsafeGraphContext context) =>
                {
                    TryBuildCameraIndexedDraw(
                        context.cmd,
                        data.camera,
                        data.selectionKey,
                        data.clusterOffset,
                        data.allowProceduralFallback,
                        data.drawClusters,
                        data.drawCountArgs);
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

            UpdateHzbBenefitAdmission(renderingData.cameraData.camera);
            UpdateHybridCostAdmission(renderingData.cameraData.camera);
            SyncPassEvents();
            recordedHzbDepthSource = TextureHandle.nullHandle;
            recordedPass1VBuffer = TextureHandle.nullHandle;
            NaniteRuntimeRegistry.MarkFeatureDrivenFrame();
            SuppressProxyDebugRenderersIfNeeded();
            // Publish one immutable Page Table epoch before any shadow/camera
            // cull or raster pass is recorded. Updating after cull allowed Formal
            // raster to read a newer table than the cut that produced its queue.
            if (lastPageStreamingUpdateFrame != Time.frameCount &&
                settings.enablePageStreamingUploads &&
                sceneVisibilityBackend != null &&
                sceneVisibilityBackend.IsPagePoolReady)
            {
                lastPageStreamingUpdateFrame = Time.frameCount;
                sceneVisibilityBackend.UpdatePageStreaming(
                    Time.frameCount,
                    settings.pageRequestReadbackInterval,
                    settings.pageUploadsPerPoll);
            }
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
            if (settings.enableVBufferPreview)
                renderer.EnqueuePass(passVBufferPreview);
            if (NaniteDebugVisualization.IsEnabled)
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
            passDebugVisualization.renderPassEvent = RenderPassEvent.AfterRendering;
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

        bool IsFormalVisibilityEnabled() => settings.enableFormalVisibilityBuffer;

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
            int selectionKey,
            int clusterOffset = 0,
            bool allowProceduralFallback = true,
            ComputeBuffer drawClusters = null,
            ComputeBuffer drawCountArgs = null)
        {
            bool useOverrideQueue = drawClusters != null && drawCountArgs != null;
            if (!IsIndexedRasterRequested() || cmd == null || camera == null ||
                sceneVisibilityBackend == null || !sceneVisibilityBackend.IndexedDrawAvailable ||
                batchedCulling == null ||
                (!useOverrideQueue &&
                 (!lastCompactUsedDirectQueue ||
                  !HasValidCompactForSelection(selectionKey, camera) ||
                  !batchedCulling.IsVisibleDrawQueueReady(camera))) ||
                kernelPrepareIndexedDrawQueue < 0 || kernelBuildIndexedDrawQueue < 0)
                return false;

            drawClusters ??= batchedCulling.VisibleDrawClusterBuffer;
            drawCountArgs ??= batchedCulling.VisibleDrawCountArgsBuffer;

            bool canAppendPass2 = !useOverrideQueue &&
                                  clusterOffset == 0 &&
                                  indexedCameraChunkHint <= 1 &&
                                  selectionKey == kCompactSelectionMerged &&
                                  lastIndexedDrawFrame == Time.frameCount &&
                                  lastIndexedDrawCameraId == camera.GetInstanceID() &&
                                  lastIndexedDrawSelectionKey == kCompactSelectionFirst &&
                                  lastIndexedDrawGeometryGeneration == sceneVisibilityBackend.GeometryGeneration &&
                                  kernelPrepareIndexedDrawQueueAppend >= 0 &&
                                  kernelBuildIndexedDrawQueueAppend >= 0;
            bool built = canAppendPass2 && sceneVisibilityBackend.DispatchIndexedDrawQueueAppend(
                cmd,
                settings.visibleTriangleCompactShader,
                kernelPrepareIndexedDrawQueueAppend,
                kernelBuildIndexedDrawQueueAppend,
                kernelCompactFinalizeVisibleQueue,
                drawClusters,
                drawCountArgs,
                false);
            bool appendedPass2 = built;
            if (!built)
            {
                built = sceneVisibilityBackend.DispatchIndexedDrawQueue(
                    cmd,
                    settings.visibleTriangleCompactShader,
                    kernelPrepareIndexedDrawQueue,
                    kernelBuildIndexedDrawQueue,
                    kernelCompactFinalizeVisibleQueue,
                    drawClusters,
                    drawCountArgs,
                    -1,
                    clusterOffset,
                    allowProceduralFallback);
            }
            if (built)
            {
                lastIndexedDrawFrame = Time.frameCount;
                lastIndexedDrawCameraId = camera.GetInstanceID();
                lastIndexedDrawSelectionKey = selectionKey;
                lastIndexedDrawGeometryGeneration = sceneVisibilityBackend.GeometryGeneration;
                lastIndexedDrawAppendedPass2 = appendedPass2;
                if (appendedPass2 && !loggedPacketAppendOnce)
                {
                    loggedPacketAppendOnce = true;
                    Debug.Log(
                        "[Nanite][PacketReuse] Formal reuses WriteDepth packets and appends only Pass2 recovery clusters.");
                }
                RequestIndexedAdmissionProbeOnce(cmd);
            }
            return built;
        }

        void RequestRuntimeTelemetry(
            UnsafeCommandBuffer cmd,
            Camera camera,
            ComputeBuffer hardwareCountOverride = null,
            ComputeBuffer softwareCountOverride = null)
        {
            if (!NaniteRuntimeTelemetry.Enabled || runtimeTelemetryReadbackPending ||
                cmd == null || camera == null || batchedCulling == null ||
                sceneVisibilityBackend == null || settings.visibleTriangleCompactShader == null ||
                kernelPrepareRuntimeTelemetry < 0 || kernelAccumulateRuntimeTelemetry < 0)
                return;

            ComputeBuffer cameraQueue = batchedCulling.VisibleDrawClusterBuffer;
            ComputeBuffer cameraCount = batchedCulling.VisibleDrawCountArgsBuffer;
            // Report the queues consumed by this frame's raster path. Camera Hybrid
            // currently owns a post-cull classifier in RendererFeature; traversal
            // bins are a different producer and remain empty when that producer is
            // disabled. Reading the backend buffers unconditionally made a healthy
            // Hybrid frame appear as hw:0,sw:0 and invalidated cost admission.
            ComputeBuffer hardwareCount = hardwareCountOverride;
            ComputeBuffer softwareCount = softwareCountOverride;
            if (hardwareCount == null || softwareCount == null)
                GetActiveRasterTelemetryCounts(out hardwareCount, out softwareCount);
            if (cameraQueue == null || cameraCount == null || hardwareCount == null || softwareCount == null)
                return;
            for (int cascade = 0; cascade < 4; cascade++)
            {
                if (batchedCulling.GetShadowDrawClusterBuffer(cascade) == null ||
                    batchedCulling.GetShadowDrawCountArgsBuffer(cascade) == null)
                    return;
            }

            runtimeTelemetryStatsBuffer ??= new ComputeBuffer(12, sizeof(uint), ComputeBufferType.Structured);
            runtimeTelemetryDispatchArgsBuffer ??= new ComputeBuffer(
                3,
                sizeof(uint),
                ComputeBufferType.IndirectArguments);

            ComputeShader shader = settings.visibleTriangleCompactShader;
            int prepare = kernelPrepareRuntimeTelemetry;
            int accumulate = kernelAccumulateRuntimeTelemetry;
            cmd.SetComputeBufferParam(shader, prepare, "_TelemetryCameraCountArgs", cameraCount);
            cmd.SetComputeBufferParam(shader, prepare, "_TelemetryShadowCountArgs0", batchedCulling.GetShadowDrawCountArgsBuffer(0));
            cmd.SetComputeBufferParam(shader, prepare, "_TelemetryShadowCountArgs1", batchedCulling.GetShadowDrawCountArgsBuffer(1));
            cmd.SetComputeBufferParam(shader, prepare, "_TelemetryShadowCountArgs2", batchedCulling.GetShadowDrawCountArgsBuffer(2));
            cmd.SetComputeBufferParam(shader, prepare, "_TelemetryShadowCountArgs3", batchedCulling.GetShadowDrawCountArgsBuffer(3));
            cmd.SetComputeBufferParam(shader, prepare, "_TelemetryHardwareCountArgs", hardwareCount);
            cmd.SetComputeBufferParam(shader, prepare, "_TelemetrySoftwareCountArgs", softwareCount);
            cmd.SetComputeBufferParam(shader, prepare, "_RuntimeTelemetryStats", runtimeTelemetryStatsBuffer);
            cmd.SetComputeBufferParam(shader, prepare, "_RuntimeTelemetryDispatchArgs", runtimeTelemetryDispatchArgsBuffer);
            cmd.DispatchCompute(shader, prepare, 1, 1, 1);

            cmd.SetComputeBufferParam(shader, accumulate, "_TelemetryCameraClusters", cameraQueue);
            cmd.SetComputeBufferParam(shader, accumulate, "_TelemetryCameraCountArgs", cameraCount);
            for (int cascade = 0; cascade < 4; cascade++)
            {
                cmd.SetComputeBufferParam(
                    shader,
                    accumulate,
                    $"_TelemetryShadowClusters{cascade}",
                    batchedCulling.GetShadowDrawClusterBuffer(cascade));
                cmd.SetComputeBufferParam(
                    shader,
                    accumulate,
                    $"_TelemetryShadowCountArgs{cascade}",
                    batchedCulling.GetShadowDrawCountArgsBuffer(cascade));
            }
            cmd.SetComputeBufferParam(shader, accumulate, "_RuntimeTelemetryStats", runtimeTelemetryStatsBuffer);
            cmd.DispatchCompute(shader, accumulate, runtimeTelemetryDispatchArgsBuffer, 0u);

            runtimeTelemetryReadbackPending = true;
            int epoch = runtimeTelemetryEpoch;
            int requestFrame = Time.frameCount;
            int cameraId = camera.GetInstanceID();
            int width = camera.pixelWidth;
            int height = camera.pixelHeight;
            int instances = batchedCulling.InstanceCount;
            int virtualClusters = batchedCulling.VirtualClusterCount;
            int residentPages = sceneVisibilityBackend.ResidentPageCount;
            int totalPages = sceneVisibilityBackend.GlobalPageCount;
            int pinnedPages = sceneVisibilityBackend.PinnedPageCount;
            int rootPages = sceneVisibilityBackend.RootPageCount;
            int requestedPages = sceneVisibilityBackend.LastRequestedPageCount;
            int queuedPages = sceneVisibilityBackend.LastQueuedPageCount;
            uint maxPageRequestPriority = sceneVisibilityBackend.LastMaxPageRequestPriority;
            int streamedPages = sceneVisibilityBackend.TotalStreamedPageCount;
            int evictedPages = sceneVisibilityBackend.TotalEvictedPageCount;
            int recycledPageAllocations = sceneVisibilityBackend.TotalRecycledAllocationCount;
            int retiredPageAllocations = sceneVisibilityBackend.RetiredPageAllocationCount;
            long pageStorageBytesRead = sceneVisibilityBackend.TotalPageStorageBytesRead;
            int pageStorageReadOperations = sceneVisibilityBackend.TotalPageStorageReadOperations;
            int streamingFilePages = sceneVisibilityBackend.StreamingFilePageCount;
            EnsureHybridCostRecorders();
            cmd.RequestAsyncReadback(runtimeTelemetryStatsBuffer, request =>
            {
                if (epoch != runtimeTelemetryEpoch)
                    return;
                runtimeTelemetryReadbackPending = false;
                if (request.hasError)
                    return;
                var data = request.GetData<uint>();
                if (data.Length < 12)
                    return;
                var values = new uint[12];
                for (int index = 0; index < values.Length; index++)
                    values[index] = data[index];
                var snapshot = new NaniteRuntimeTelemetry.Snapshot(
                    requestFrame,
                    cameraId,
                    width,
                    height,
                    values,
                    instances,
                    virtualClusters,
                    residentPages,
                    totalPages,
                    pinnedPages,
                    rootPages,
                    requestedPages,
                    queuedPages,
                    maxPageRequestPriority,
                    streamedPages,
                    evictedPages,
                    recycledPageAllocations,
                    retiredPageAllocations,
                    pageStorageBytesRead,
                    pageStorageReadOperations,
                    streamingFilePages,
                    RecorderGpuMs(hybridFormalGpuRecorder),
                    RecorderGpuMs(hybridShadowCullGpuRecorder),
                    RecorderGpuMs(hybridShadowDrawGpuRecorder),
                    batchedCulling != null ? batchedCulling.LastCullStatsReadbackFrame : -1,
                    batchedCulling != null ? batchedCulling.LastCull1Drawn : 0,
                    batchedCulling != null ? batchedCulling.LastCull2Candidates : 0,
                    batchedCulling != null ? batchedCulling.LastCull2Drawn : 0);
                NaniteRuntimeTelemetry.Publish(snapshot);
            });
        }

        void GetActiveRasterTelemetryCounts(
            out ComputeBuffer hardwareCount,
            out ComputeBuffer softwareCount)
        {
            bool cameraHybridProducedThisFrame =
                IsHybridRasterRequested() &&
                ActiveHybridHardwareCountArgs != null &&
                ActiveHybridSoftwareCountArgs != null;
            if (cameraHybridProducedThisFrame)
            {
                hardwareCount = ActiveHybridHardwareCountArgs;
                softwareCount = ActiveHybridSoftwareCountArgs;
                return;
            }

            // Hardware-only consumes the complete camera queue; traversal raster
            // bins are optional and are not the raster ABI in that mode.
            hardwareCount = batchedCulling?.VisibleDrawCountArgsBuffer;
            softwareCount = batchedCulling?.SoftwareRasterCountArgsBuffer;
        }

        void RecordRuntimeTelemetryPass(
            RenderGraph renderGraph,
            Camera camera,
            bool hybridRaster,
            ProfilingSampler profilingSampler)
        {
            if (!NaniteRuntimeTelemetry.Enabled || runtimeTelemetryReadbackPending ||
                renderGraph == null || camera == null || batchedCulling == null ||
                sceneVisibilityBackend == null || settings.visibleTriangleCompactShader == null ||
                kernelPrepareRuntimeTelemetry < 0 || kernelAccumulateRuntimeTelemetry < 0)
                return;

            ComputeBuffer cameraQueue = batchedCulling.VisibleDrawClusterBuffer;
            ComputeBuffer cameraCount = batchedCulling.VisibleDrawCountArgsBuffer;
            ComputeBuffer hardwareCount = hybridRaster
                ? ActiveHybridHardwareCountArgs
                : cameraCount;
            ComputeBuffer softwareCount = hybridRaster
                ? ActiveHybridSoftwareCountArgs
                : batchedCulling.SoftwareRasterCountArgsBuffer;
            if (cameraQueue == null || cameraCount == null ||
                hardwareCount == null || softwareCount == null)
                return;
            for (int cascade = 0; cascade < 4; cascade++)
            {
                if (batchedCulling.GetShadowDrawClusterBuffer(cascade) == null ||
                    batchedCulling.GetShadowDrawCountArgsBuffer(cascade) == null)
                    return;
            }

            runtimeTelemetryStatsBuffer ??= new ComputeBuffer(12, sizeof(uint), ComputeBufferType.Structured);
            runtimeTelemetryDispatchArgsBuffer ??= new ComputeBuffer(
                3,
                sizeof(uint),
                ComputeBufferType.IndirectArguments);

            using (var builder = renderGraph.AddUnsafePass<RuntimeTelemetryRgPassData>(
                       "Nanite/RuntimeTelemetry",
                       out var passData,
                       profilingSampler))
            {
                passData.camera = camera;
                passData.hardwareCount = hardwareCount;
                passData.softwareCount = softwareCount;
                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);
                // ComputeBuffer is an external legacy resource and cannot be
                // imported into Unity 6 RenderGraph. This is deliberately an
                // unsafe pass; the GraphicsBuffer fence below carries the Hybrid
                // producer dependency while pass order covers the cull queues.
                if (hybridRaster && ActiveHybridDependency != null)
                {
                    builder.UseBuffer(
                        renderGraph.ImportBuffer(ActiveHybridDependency),
                        AccessFlags.Read);
                }
                builder.SetRenderFunc((RuntimeTelemetryRgPassData data, UnsafeGraphContext context) =>
                {
                    RequestRuntimeTelemetry(
                        context.cmd,
                        data.camera,
                        data.hardwareCount,
                        data.softwareCount);
                });
            }
        }

        void RequestIndexedAdmissionProbeOnce(UnsafeCommandBuffer cmd)
        {
            const int probeVersion = 18;
            if (indexedAdmissionProbeWarmupVersion != probeVersion)
            {
                indexedAdmissionProbeWarmupVersion = probeVersion;
                indexedAdmissionProbeWarmupFrame = Time.frameCount + 32;
                return;
            }
            if (Time.frameCount < indexedAdmissionProbeWarmupFrame)
                return;
            if ((indexedAdmissionProbeRequested && indexedAdmissionProbeVersion == probeVersion) ||
                cmd == null || batchedCulling == null ||
                sceneVisibilityBackend == null || batchedCulling.InstanceCount < 100)
                return;

            ComputeBuffer cameraCount = batchedCulling.VisibleDrawCountArgsBuffer;
            if (cameraCount == null)
                return;
            indexedAdmissionProbeRequested = true;
            indexedAdmissionProbeVersion = probeVersion;
            indexedAdmissionProbePending = 1;
            indexedAdmissionProbeShadowMask = 0;
            indexedAdmissionProbeFailed = false;
            RequestIndexedAdmissionCount(cmd, cameraCount, 0);
            for (int cascade = 0; cascade < 4; cascade++)
            {
                ComputeBuffer shadowCount = batchedCulling.GetShadowDrawCountArgsBuffer(cascade);
                if (shadowCount == null)
                    continue;
                indexedAdmissionProbePending++;
                indexedAdmissionProbeShadowMask |= 1 << cascade;
                RequestIndexedAdmissionCount(cmd, shadowCount, cascade + 1);
            }
            GetActiveRasterTelemetryCounts(
                out ComputeBuffer hardwareRasterCount,
                out ComputeBuffer softwareRasterCount);
            if (hardwareRasterCount != null)
            {
                indexedAdmissionProbePending++;
                RequestIndexedAdmissionCount(cmd, hardwareRasterCount, 5);
            }
            if (softwareRasterCount != null)
            {
                indexedAdmissionProbePending++;
                RequestIndexedAdmissionCount(cmd, softwareRasterCount, 6);
            }
        }

        void RequestIndexedAdmissionCount(UnsafeCommandBuffer cmd, ComputeBuffer buffer, int slot)
        {
            cmd.RequestAsyncReadback(buffer, sizeof(uint), 0, request =>
            {
                if (request.hasError || request.GetData<uint>().Length == 0)
                    indexedAdmissionProbeFailed = true;
                else
                    indexedAdmissionProbeCounts[slot] = request.GetData<uint>()[0];

                indexedAdmissionProbePending--;
                if (indexedAdmissionProbePending != 0)
                    return;

                if (indexedAdmissionProbeFailed || sceneVisibilityBackend == null)
                {
                    Debug.LogWarning("[Nanite][PacketAdmissionProbe] asynchronous count readback failed.");
                    return;
                }

                int cameraCapacity = sceneVisibilityBackend.IndexedDrawClusterCapacity;
                indexedCameraChunkHint = Mathf.Clamp(
                    Mathf.CeilToInt(indexedAdmissionProbeCounts[0] / (float)Mathf.Max(1, cameraCapacity)),
                    1,
                    16);
                indexedHybridChunkHint = Mathf.Clamp(
                    Mathf.CeilToInt(indexedAdmissionProbeCounts[5] / (float)Mathf.Max(1, cameraCapacity)),
                    1,
                    16);
                uint shadowMax = 0;
                for (int cascade = 0; cascade < 4; cascade++)
                {
                    if ((indexedAdmissionProbeShadowMask & (1 << cascade)) != 0)
                        shadowMax = Math.Max(shadowMax, indexedAdmissionProbeCounts[cascade + 1]);
                }
                string message =
                    "[Nanite][PacketAdmissionProbe] " +
                    $"camera={indexedAdmissionProbeCounts[0]}/{cameraCapacity}:" +
                    $"packetChunks={indexedCameraChunkHint}, " +
                    $"shadowReuseMax={shadowMax}/{cameraCapacity}:" +
                    $"{(shadowMax <= (uint)cameraCapacity ? "all-packet" : "packet+procedural-tail")}, " +
                    $"hybrid=hw:{indexedAdmissionProbeCounts[5]},sw:{indexedAdmissionProbeCounts[6]}";
                for (int cascade = 0; cascade < 4; cascade++)
                {
                    if ((indexedAdmissionProbeShadowMask & (1 << cascade)) == 0)
                        continue;
                    uint count = indexedAdmissionProbeCounts[cascade + 1];
                    message += $", shadow{cascade}={count}";
                }
                Debug.Log(message + ".");
                RequestVisibleLodAuditOnce(indexedAdmissionProbeCounts[0]);
            });
        }

        void RequestVisibleLodAuditOnce(uint expectedVisibleClusters)
        {
            const int auditVersion = 10;
            if (!settings.logStats ||
                (visibleLodAuditRequested && visibleLodAuditVersion == auditVersion) ||
                expectedVisibleClusters == 0u ||
                batchedCulling == null || sceneVisibilityBackend == null)
                return;
            ComputeBuffer queue = batchedCulling.VisibleDrawClusterBuffer;
            if (queue == null)
                return;

            visibleLodAuditRequested = true;
            visibleLodAuditVersion = auditVersion;
            AsyncGPUReadback.Request(queue, request =>
            {
                if (request.hasError || sceneVisibilityBackend == null)
                {
                    Debug.LogWarning("[Nanite][VisibleLodAudit] asynchronous queue readback failed.");
                    return;
                }

                var data = request.GetData<uint>();
                int availableEntries = data.Length / 3;
                int entryCount = Mathf.Min(availableEntries, checked((int)expectedVisibleClusters));
                const int binCount = 32;
                var clusterBins = new int[binCount];
                var triangleBins = new ulong[binCount];
                var selectedClusters = new HashSet<ulong>(entryCount);
                int unmapped = 0;
                int duplicateSelections = 0;
                ulong totalTriangles = 0;
                for (int entry = 0; entry < entryCount; entry++)
                {
                    int baseIndex = entry * 3;
                    uint firstTriangle = data[baseIndex];
                    uint triangleCount = data[baseIndex + 1];
                    uint instanceIndex = data[baseIndex + 2];
                    ulong selectionKey = ((ulong)instanceIndex << 32) | firstTriangle;
                    if (!selectedClusters.Add(selectionKey))
                        duplicateSelections++;
                    if (!sceneVisibilityBackend.TryGetGeometryMipForFirstTriangle(firstTriangle, out int mip))
                    {
                        unmapped++;
                        continue;
                    }

                    int bin = Mathf.Clamp(mip, 0, binCount - 1);
                    clusterBins[bin]++;
                    triangleBins[bin] += triangleCount;
                    totalTriangles += triangleCount;
                }

                var message = new StringBuilder(512);
                message.Append("[Nanite][VisibleLodAudit] camera clusters=")
                    .Append(entryCount)
                    .Append(" triangles=")
                    .Append(totalTriangles)
                    .Append(" unmapped=")
                    .Append(unmapped)
                    .Append(" duplicates=")
                    .Append(duplicateSelections)
                    .Append(" | ");
                for (int mip = 0; mip < binCount; mip++)
                {
                    if (clusterBins[mip] == 0)
                        continue;
                    message.Append("mip").Append(mip)
                        .Append('=')
                        .Append(clusterBins[mip])
                        .Append('c').Append('/')
                        .Append(triangleBins[mip]).Append("t; ");
                }
                Debug.Log(message.ToString());
            });
        }

        bool HasValidIndexedDraw(int selectionKey, Camera camera)
        {
            return IsIndexedRasterRequested() &&
                   camera != null &&
                   sceneVisibilityBackend != null &&
                   sceneVisibilityBackend.IndexedDrawAvailable &&
                   lastIndexedDrawFrame == Time.frameCount &&
                   lastIndexedDrawCameraId == camera.GetInstanceID() &&
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
            if (!IsHzbConfigured())
                return false;
            if (!settings.adaptiveHzbCulling)
                return true;
            if (sceneVisibilityBackend == null || !sceneVisibilityBackend.IsReady)
                return false;

            int minClusters = Mathf.Max(0, settings.hzbMinClusterCount);
            bool largeEnough = minClusters <= 0 || sceneVisibilityBackend.ClusterCount >= minClusters;
            return largeEnough && hzbAdmissionAllowed;
        }

        bool EnsureHzbCostRecorders()
        {
            hzbWriteDepthGpuRecorder ??= Recorder.Get("Nanite/WriteDepth");
            hzbBuildGpuRecorder ??= Recorder.Get("Nanite/BuildHzb");
            hzbSecondCullGpuRecorder ??= Recorder.Get("Nanite/SecondCull");
            hzbFormalGpuRecorder ??= Recorder.Get("Nanite/FormalVisibility");
            Recorder[] recorders =
            {
                hzbWriteDepthGpuRecorder,
                hzbBuildGpuRecorder,
                hzbSecondCullGpuRecorder,
                hzbFormalGpuRecorder
            };
            for (int i = 0; i < recorders.Length; i++)
            {
                Recorder recorder = recorders[i];
                if (recorder == null || !recorder.isValid)
                    return false;
                recorder.enabled = true;
            }
            return true;
        }

        static double RecorderGpuMs(Recorder recorder)
        {
            if (recorder == null || !recorder.isValid || recorder.gpuSampleBlockCount <= 0)
                return -1.0;
            return recorder.gpuElapsedNanoseconds * 1e-6;
        }

        bool EnsureHybridCostRecorders()
        {
            hybridFormalGpuRecorder ??= Recorder.Get("Nanite/FormalVisibility");
            hybridShadowCullGpuRecorder ??= Recorder.Get("Nanite Main Light Shadow Cull");
            hybridShadowDrawGpuRecorder ??= Recorder.Get("Draw Main Light Shadowmap");
            Recorder[] recorders =
            {
                hybridFormalGpuRecorder,
                hybridShadowCullGpuRecorder,
                hybridShadowDrawGpuRecorder
            };
            for (int i = 0; i < recorders.Length; i++)
            {
                if (recorders[i] == null || !recorders[i].isValid)
                    return false;
                recorders[i].enabled = true;
            }
            return true;
        }

        static void AdvanceHybridCostProbe(
            HybridCostAdmissionState state,
            double gpuMs,
            string label)
        {
            int frame = Time.frameCount;
            if (state.phaseStartFrame < 0)
                state.Reset(frame);
            if (state.steady)
            {
                if (frame < state.nextProbeFrame)
                    return;
                state.Reset(frame);
                return;
            }
            if (frame - state.phaseStartFrame < kHybridCostWarmupFrames)
                return;

            if (gpuMs < 0.0)
            {
                state.missingTimingFrames++;
                if (state.missingTimingFrames >= kGpuTimingUnavailableFrames)
                    state.DisableWithoutTiming(frame);
                return;
            }
            state.missingTimingFrames = 0;

            if (state.testingEnabled)
                state.enabledTotalMs += gpuMs;
            else
                state.disabledTotalMs += gpuMs;
            state.samples++;
            if (state.samples < kHybridCostSampleCount)
                return;

            if (state.testingEnabled)
            {
                state.lastEnabledMs = state.enabledTotalMs / state.samples;
                state.testingEnabled = false;
                state.samples = 0;
                state.phaseStartFrame = frame;
                return;
            }

            state.lastDisabledMs = state.disabledTotalMs / state.samples;
            state.allowed = state.lastEnabledMs + kHybridCostDecisionMarginMs < state.lastDisabledMs;
            state.steady = true;
            state.nextProbeFrame = frame + kHybridCostReprobeFrames;
            Debug.Log(
                $"[Nanite][HybridCostProbe] {label}: hybrid={state.lastEnabledMs:F3} ms, " +
                $"hardwareOnly={state.lastDisabledMs:F3} ms, " +
                $"decision={(state.allowed ? "hybrid" : "hardware-only")}; " +
                "production will reprobe after 600 frames.");
        }

        void UpdateHybridCostAdmission(Camera camera)
        {
            if (camera == null || camera.cameraType != CameraType.Game ||
                !IsFormalVisibilityEnabled() || !IsHybridConfigured() ||
                hybridCostLastUpdateFrame == Time.frameCount)
                return;
            if (!EnsureHybridCostRecorders())
            {
                cameraHybridCost.DisableWithoutTiming(Time.frameCount);
                shadowHybridCost.DisableWithoutTiming(Time.frameCount);
                LogGpuTimingUnavailableOnce(ref loggedHybridGpuTimingUnavailable, "Hybrid");
                return;
            }
            bool hzbCostStable = hzbCostProbePhase == HzbCostProbePhase.SteadyEnabled ||
                                 hzbCostProbePhase == HzbCostProbePhase.SteadyDisabled;
            if (IsHzbConfigured() && settings.adaptiveHzbCulling && !hzbCostStable)
                return;
            hybridCostLastUpdateFrame = Time.frameCount;

            int registryRevision = NaniteRuntimeRegistry.Revision;
            int geometryGeneration = sceneVisibilityBackend != null
                ? sceneVisibilityBackend.GeometryGeneration
                : -1;
            int cameraId = camera.GetInstanceID();
            int width = Mathf.Max(1, camera.pixelWidth);
            int height = Mathf.Max(1, camera.pixelHeight);
            bool reset = hybridCostCameraId != cameraId ||
                         hybridCostRegistryRevision != registryRevision ||
                         hybridCostGeometryGeneration != geometryGeneration ||
                         hybridCostWidth != width || hybridCostHeight != height;
            if (reset)
            {
                hybridCostCameraId = cameraId;
                hybridCostRegistryRevision = registryRevision;
                hybridCostGeometryGeneration = geometryGeneration;
                hybridCostWidth = width;
                hybridCostHeight = height;
                cameraHybridCost.Reset(Time.frameCount);
                shadowHybridCost.Reset(Time.frameCount);
            }

            AdvanceHybridCostProbe(
                cameraHybridCost,
                RecorderGpuMs(hybridFormalGpuRecorder),
                "camera");
            double shadowCullMs = RecorderGpuMs(hybridShadowCullGpuRecorder);
            double shadowDrawMs = RecorderGpuMs(hybridShadowDrawGpuRecorder);
            AdvanceHybridCostProbe(
                shadowHybridCost,
                shadowCullMs >= 0.0 && shadowDrawMs >= 0.0
                    ? shadowCullMs + shadowDrawMs
                    : -1.0,
                "four-cascade-shadow");
            if ((!cameraHybridCost.UseHybrid && cameraHybridCost.lastEnabledMs <= 0.0) ||
                (!shadowHybridCost.UseHybrid && shadowHybridCost.lastEnabledMs <= 0.0))
                LogGpuTimingUnavailableOnce(ref loggedHybridGpuTimingUnavailable, "Hybrid");
        }

        static void LogGpuTimingUnavailableOnce(ref bool logged, string owner)
        {
            if (logged)
                return;
            logged = true;
            Debug.LogWarning(
                $"[Nanite][GpuTiming] {owner} pass-level GPU markers are unavailable. " +
                "Automatic cost probing is disabled and the production-safe fixed path is used; " +
                "capture a connected GPU Profiler/PIX trace for pass attribution.");
        }

        void ResetHzbCostProbe()
        {
            hzbCostProbePhase = HzbCostProbePhase.MeasureEnabled;
            hzbCostPhaseStartFrame = Time.frameCount;
            hzbCostSamples = 0;
            hzbCostEnabledTotalMs = 0.0;
            hzbCostEnabledFormalMs = 0.0;
            hzbCostDisabledFormalMs = 0.0;
            hzbAdmissionAllowed = true;
            hzbAdmissionProbeMode = true;
            hzbMissingGpuTimingFrames = 0;
        }

        void BeginHzbCostPhase(HzbCostProbePhase phase, bool enabled)
        {
            hzbCostProbePhase = phase;
            hzbCostPhaseStartFrame = Time.frameCount;
            hzbCostSamples = 0;
            hzbAdmissionAllowed = enabled;
            hzbAdmissionProbeMode = phase == HzbCostProbePhase.MeasureEnabled ||
                                    phase == HzbCostProbePhase.MeasureDisabled;
        }

        bool UpdateHzbCostAdmission()
        {
            if (!IsFormalVisibilityEnabled())
                return false;

            if (hzbGpuTimingUnavailable)
            {
                if (Time.frameCount < hzbGpuTimingRetryFrame)
                    return false;
                hzbGpuTimingUnavailable = false;
                ResetHzbCostProbe();
            }
            if (!EnsureHzbCostRecorders())
            {
                hzbGpuTimingUnavailable = true;
                hzbGpuTimingRetryFrame = Time.frameCount + kHzbCostReprobeFrames;
                LogGpuTimingUnavailableOnce(ref loggedHzbGpuTimingUnavailable, "HZB");
                return false;
            }

            if (hzbCostPhaseStartFrame < 0)
                ResetHzbCostProbe();

            if (hzbCostProbePhase == HzbCostProbePhase.SteadyEnabled ||
                hzbCostProbePhase == HzbCostProbePhase.SteadyDisabled)
            {
                bool enabled = hzbCostProbePhase == HzbCostProbePhase.SteadyEnabled;
                hzbAdmissionAllowed = enabled;
                hzbAdmissionProbeMode = false;
                if (Time.frameCount - hzbCostPhaseStartFrame >= kHzbCostReprobeFrames)
                    ResetHzbCostProbe();
                return true;
            }

            bool measuringEnabled = hzbCostProbePhase == HzbCostProbePhase.MeasureEnabled;
            hzbAdmissionAllowed = measuringEnabled;
            hzbAdmissionProbeMode = true;
            // Recorder GPU values arrive three frames late. Waiting eight stable
            // frames after every mode switch prevents cross-contamination.
            if (Time.frameCount - hzbCostPhaseStartFrame < kHzbCostWarmupFrames)
                return true;

            double formalMs = RecorderGpuMs(hzbFormalGpuRecorder);
            if (formalMs < 0.0)
            {
                hzbMissingGpuTimingFrames++;
                if (hzbMissingGpuTimingFrames < kGpuTimingUnavailableFrames)
                    return true;
                hzbGpuTimingUnavailable = true;
                hzbGpuTimingRetryFrame = Time.frameCount + kHzbCostReprobeFrames;
                LogGpuTimingUnavailableOnce(ref loggedHzbGpuTimingUnavailable, "HZB");
                return false;
            }
            hzbMissingGpuTimingFrames = 0;

            if (measuringEnabled)
            {
                double writeDepthMs = RecorderGpuMs(hzbWriteDepthGpuRecorder);
                double buildMs = RecorderGpuMs(hzbBuildGpuRecorder);
                double secondCullMs = RecorderGpuMs(hzbSecondCullGpuRecorder);
                if (writeDepthMs < 0.0 || buildMs < 0.0 || secondCullMs < 0.0)
                    return true;
                hzbCostEnabledTotalMs += formalMs + writeDepthMs + buildMs + secondCullMs;
                hzbCostEnabledFormalMs += formalMs;
            }
            else
            {
                hzbCostDisabledFormalMs += formalMs;
            }

            hzbCostSamples++;
            if (hzbCostSamples < kHzbCostSampleCount)
                return true;

            if (measuringEnabled)
            {
                BeginHzbCostPhase(HzbCostProbePhase.MeasureDisabled, false);
                return true;
            }

            hzbCostLastEnabledMs = hzbCostEnabledTotalMs / kHzbCostSampleCount;
            hzbCostLastDisabledMs = hzbCostDisabledFormalMs / kHzbCostSampleCount;
            bool netWin = hzbCostLastEnabledMs + kHzbCostDecisionMarginMs < hzbCostLastDisabledMs;
            BeginHzbCostPhase(
                netWin ? HzbCostProbePhase.SteadyEnabled : HzbCostProbePhase.SteadyDisabled,
                netWin);
            Debug.Log(
                $"[Nanite][HZBCost] enabledTotal={hzbCostLastEnabledMs:0.###}ms " +
                $"(formal={hzbCostEnabledFormalMs / kHzbCostSampleCount:0.###}ms), " +
                $"disabledFormal={hzbCostLastDisabledMs:0.###}ms, " +
                $"decision={(netWin ? "enabled" : "disabled")}. " +
                "GPU timings are sampled asynchronously; no readback stall is used.");
            return true;
        }

        void UpdateHzbBenefitAdmission(Camera camera)
        {
            if (camera == null || camera.cameraType != CameraType.Game ||
                !IsHzbConfigured() || !settings.adaptiveHzbCulling)
                return;

            int cameraId = camera.GetInstanceID();
            int registryRevision = NaniteRuntimeRegistry.Revision;
            int geometryGeneration = sceneVisibilityBackend != null
                ? sceneVisibilityBackend.GeometryGeneration
                : -1;
            int width = Mathf.Max(1, camera.pixelWidth);
            int height = Mathf.Max(1, camera.pixelHeight);
            bool resolutionChanged = hzbAdmissionWidth <= 0 || hzbAdmissionHeight <= 0 ||
                                     Mathf.Abs(width - hzbAdmissionWidth) > Mathf.Max(32, hzbAdmissionWidth / 8) ||
                                     Mathf.Abs(height - hzbAdmissionHeight) > Mathf.Max(32, hzbAdmissionHeight / 8);
            bool identityChanged = hzbAdmissionCameraId != cameraId ||
                                   hzbAdmissionRegistryRevision != registryRevision ||
                                   hzbAdmissionGeometryGeneration != geometryGeneration ||
                                   resolutionChanged;
            if (identityChanged)
            {
                hzbAdmissionCameraId = cameraId;
                hzbAdmissionRegistryRevision = registryRevision;
                hzbAdmissionGeometryGeneration = geometryGeneration;
                hzbAdmissionWidth = width;
                hzbAdmissionHeight = height;
                hzbAdmissionResetFrame = Time.frameCount;
                hzbAdmissionLastStatsFrame = batchedCulling != null
                    ? batchedCulling.LastCullStatsReadbackFrame
                    : -1;
                hzbAdmissionNoBenefitCount = 0;
                hzbAdmissionDisabledUntilFrame = -1;
                hzbAdmissionAllowed = true;
                hzbAdmissionProbeMode = true;
                ResetHzbCostProbe();
                InvalidateHzbHistory(cameraId);
                return;
            }

            // Prefer an actual end-to-end GPU cost comparison over a rejected
            // cluster count. The latter is only a fallback on platforms where
            // GPU marker timing is unavailable.
            if (UpdateHzbCostAdmission())
                return;

            if (!hzbAdmissionAllowed)
            {
                if (Time.frameCount < hzbAdmissionDisabledUntilFrame)
                    return;

                // Periodically re-enable the full path long enough for fresh asynchronous
                // Pass2 samples, so entering an occluded view can re-admit HZB.
                hzbAdmissionAllowed = true;
                hzbAdmissionProbeMode = true;
                hzbAdmissionNoBenefitCount = 0;
                hzbAdmissionResetFrame = Time.frameCount;
                hzbAdmissionLastStatsFrame = batchedCulling != null
                    ? batchedCulling.LastCullStatsReadbackFrame
                    : -1;
                InvalidateHzbHistory(cameraId);
                return;
            }

            if (batchedCulling == null || batchedCulling.LastCullStatsCameraId != cameraId)
                return;

            int sampleFrame = batchedCulling.LastCullStatsReadbackFrame;
            if (sampleFrame < hzbAdmissionResetFrame || sampleFrame == hzbAdmissionLastStatsFrame)
                return;
            hzbAdmissionLastStatsFrame = sampleFrame;

            int pass1Drawn = Mathf.Max(0, batchedCulling.LastCull1Drawn);
            int pass2Candidates = Mathf.Max(0, batchedCulling.LastCull2Candidates);
            int pass2Recovered = Mathf.Max(0, batchedCulling.LastCull2Drawn);
            int finallyRejected = Mathf.Max(0, pass2Candidates - pass2Recovered);
            int testedPopulation = Mathf.Max(1, pass1Drawn + pass2Candidates);
            float rejectRatio = finallyRejected / (float)testedPopulation;
            bool hasMaterialBenefit = finallyRejected >= kHzbAdmissionMinRejectedClusters &&
                                      rejectRatio >= kHzbAdmissionMinRejectRatio;
            if (hasMaterialBenefit)
            {
                hzbAdmissionNoBenefitCount = 0;
                hzbAdmissionProbeMode = false;
            }
            else
            {
                hzbAdmissionNoBenefitCount++;
                hzbAdmissionProbeMode = true;
            }

            if (hzbAdmissionNoBenefitCount < kHzbAdmissionNoBenefitSamples)
                return;

            hzbAdmissionAllowed = false;
            hzbAdmissionProbeMode = false;
            hzbAdmissionDisabledUntilFrame = Time.frameCount + kHzbAdmissionCooldownFrames;
        }

        void InvalidateHzbHistory(int cameraId)
        {
            if (cameraHzb == null || !cameraHzb.TryGetValue(cameraId, out var state) || state == null)
                return;
            state.hasPrevious = false;
            state.builtFrame = -1;
            state.currentHzbValid = false;
            state.historyViewValid = false;
            state.historyHzbView = default;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (ReferenceEquals(ActiveInstance, this))
                ActiveInstance = null;
            if (featureDriverRegistered)
            {
                NaniteRuntimeRegistry.UnregisterFeatureDriver();
                featureDriverRegistered = false;
            }
            DisableGpuRecorder(ref hzbWriteDepthGpuRecorder);
            DisableGpuRecorder(ref hzbBuildGpuRecorder);
            DisableGpuRecorder(ref hzbSecondCullGpuRecorder);
            DisableGpuRecorder(ref hzbFormalGpuRecorder);
            DisableGpuRecorder(ref hybridFormalGpuRecorder);
            DisableGpuRecorder(ref hybridShadowCullGpuRecorder);
            DisableGpuRecorder(ref hybridShadowDrawGpuRecorder);
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
            if (externalMainLightShadowCullProvider != null)
            {
                ExternalShadowCasterRegistry.UnregisterMainLightCullProvider(externalMainLightShadowCullProvider);
                externalMainLightShadowCullProvider = null;
            }
            if (cameraHzb != null)
            {
                foreach (var kv in cameraHzb)
                    ReleaseState(kv.Value);
                cameraHzb.Clear();
            }
            compactVBufferProbeGeneration++;
            ReleaseCompactVBufferProbeResources();
            ReleaseRuntimeMaterial();
            ReleaseFormalResolveStateMaterials();
            batchedCulling?.Dispose();
            batchedCulling = null;
            sceneVisibilityBackend?.Dispose();
            sceneVisibilityBackend = null;
            ReleaseHybridBuffers();
            ReleaseFormalBuffers();
            runtimeTelemetryEpoch++;
            runtimeTelemetryReadbackPending = false;
            runtimeTelemetryStatsBuffer?.Release();
            runtimeTelemetryStatsBuffer = null;
            runtimeTelemetryDispatchArgsBuffer?.Release();
            runtimeTelemetryDispatchArgsBuffer = null;
            loggedExecutionOnce = false;
            loggedVBufferOnce = false;
            loggedBatchedDispatchStats = false;
            loggedFormalOnce = false;
            loggedFormalOrderWarning = false;
            loggedHybridRasterWarning = false;
            loggedFormalSkipReason = false;
            loggedFormalRecordSuccess = false;
            loggedFormalRasterStats = false;
            loggedFormalRasterDiagnostics = false;
            loggedExternalShadowSchedulingOnce = false;
            loggedShadowExecutionOnce = false;
            loggedShadowCullScalesOnce = false;
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
            if (!settings.enable)
                return false;
            RefreshNegotiatedRenderPath();
            if (negotiatedRenderPath == NaniteRenderPath.Unsupported ||
                !IsRequiredGraphicsApiActive())
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

        static void DisableGpuRecorder(ref Recorder recorder)
        {
            if (recorder != null && recorder.isValid)
                recorder.enabled = false;
            recorder = null;
        }

        void EnsureExternalShadowProviderRegistered()
        {
            externalMainLightShadowProvider ??= HasExternalMainLightShadowCasters;
            ExternalShadowCasterRegistry.RegisterMainLightProvider(externalMainLightShadowProvider);
            externalMainLightShadowCullBatchProvider ??= CullExternalMainLightShadowCastersBatch;
            ExternalShadowCasterRegistry.RegisterMainLightCullBatchProvider(externalMainLightShadowCullBatchProvider);
            externalMainLightShadowCullProvider ??= BuildExternalMainLightShadowCascade;
            ExternalShadowCasterRegistry.RegisterMainLightCullProvider(externalMainLightShadowCullProvider);
            externalMainLightShadowDrawProvider ??= DrawExternalMainLightShadowCasters;
            ExternalShadowCasterRegistry.RegisterMainLightDrawProvider(externalMainLightShadowDrawProvider);
        }

        void CullExternalMainLightShadowCastersBatch(ExternalMainLightShadowCullBatchContext context)
        {
            if (!settings.enable || !settings.enableShadowCasting || settings.gpuCullingShader == null)
                return;
            if (!IsRequiredGraphicsApiActive())
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
                ShadowSplitData splitData = slice.splitData;
                shadowBatchCullingSpheres[cascadeIndex] = splitData.cullingSphere;
                int planeCount = Mathf.Clamp(
                    splitData.cullingPlaneCount,
                    0,
                    kMaxShadowCasterPlanesPerCascade);
                shadowBatchCasterPlaneCounts[cascadeIndex] = planeCount;
                int planeBase = cascadeIndex * kMaxShadowCasterPlanesPerCascade;
                for (int planeIndex = 0; planeIndex < kMaxShadowCasterPlanesPerCascade; planeIndex++)
                {
                    if (planeIndex < planeCount)
                    {
                        Plane plane = splitData.GetCullingPlane(planeIndex);
                        shadowBatchCasterPlanes[planeBase + planeIndex] = new Vector4(
                            plane.normal.x,
                            plane.normal.y,
                            plane.normal.z,
                            plane.distance);
                    }
                    else
                    {
                        shadowBatchCasterPlanes[planeBase + planeIndex] = Vector4.zero;
                    }
                }
            }
            for (int cascadeIndex = activeCascadeCount; cascadeIndex < 4; cascadeIndex++)
            {
                shadowBatchCasterPlaneCounts[cascadeIndex] = 0;
                shadowBatchCullingSpheres[cascadeIndex] = Vector4.zero;
            }
            if (!loggedShadowCullScalesOnce)
            {
                loggedShadowCullScalesOnce = true;
                Debug.Log(
                    $"[Nanite][Shadow] cascade projections: count={activeCascadeCount}, " +
                    $"c0={shadowBatchResolutions[0]}/{shadowBatchProjectionMatrices[0].m00:G5}/{shadowBatchProjectionMatrices[0].m11:G5}, " +
                    $"c1={shadowBatchResolutions[1]}/{shadowBatchProjectionMatrices[1].m00:G5}/{shadowBatchProjectionMatrices[1].m11:G5}, " +
                    $"c2={shadowBatchResolutions[2]}/{shadowBatchProjectionMatrices[2].m00:G5}/{shadowBatchProjectionMatrices[2].m11:G5}, " +
                    $"c3={shadowBatchResolutions[3]}/{shadowBatchProjectionMatrices[3].m00:G5}/{shadowBatchProjectionMatrices[3].m11:G5}, " +
                    $"casterPlanes={shadowBatchCasterPlaneCounts[0]}/" +
                    $"{shadowBatchCasterPlaneCounts[1]}/" +
                    $"{shadowBatchCasterPlaneCounts[2]}/" +
                    $"{shadowBatchCasterPlaneCounts[3]}.");
            }
            Vector3 shadowRayDirection = context.shadowLight.localToWorldMatrix.GetColumn(2);

            using (kShadowCullRecordMarker.Auto())
            {
                bool recorded = batchedCulling.RecordShadowCullBatch(
                    context.commandBuffer,
                    context.camera,
                    activeCascadeCount,
                    shadowBatchViewMatrices,
                    shadowBatchProjectionMatrices,
                    shadowBatchResolutions,
                    shadowBatchCasterPlanes,
                    shadowBatchCasterPlaneCounts,
                    shadowBatchCullingSpheres,
                    shadowRayDirection);
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
                                shadowBatchResolutions[cascadeIndex],
                                shadowRayDirection))
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

            }
        }

        bool IsAutomaticPathNegotiationEnabled() => settings.automaticPathNegotiation;

        bool IsIndexedRasterRequested()
        {
            bool requested = IsAutomaticPathNegotiationEnabled() || settings.enableIndexedClusterRaster;
            return requested && indexedKernelContractValid &&
                   negotiatedRenderPath >= NaniteRenderPath.HardwareFast;
        }

        bool IsHzbConfigured()
        {
            const bool previousHzbTraversalValidated = true;
            bool requested = settings.useHzbCulling && previousHzbTraversalValidated;
            return requested && hzbKernelContractValid &&
                   negotiatedRenderPath >= NaniteRenderPath.HardwareCompat;
        }

        bool IsHybridPathConfigured()
        {
            bool requested =
                (FormalRasterizationMode)Mathf.Clamp(
                    settings.formalRasterizationMode,
                    0,
                    (int)FormalRasterizationMode.HybridSoftwareHardware) ==
                FormalRasterizationMode.HybridSoftwareHardware;
            return requested && hybridKernelContractValid &&
                   !CanUseCompactFormalVBuffer() &&
                   CanUsePortableFloat2FormalVBuffer() &&
                   negotiatedRenderPath == NaniteRenderPath.Hybrid;
        }

        void RefreshNegotiatedRenderPath()
        {
            if (!kernelInitializationComplete)
                return;
            bool dx12 = SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D12;
            bool compute = SystemInfo.supportsComputeShaders;
            bool baseKernels =
                settings.gpuCullingShader != null &&
                settings.visibleTriangleCompactShader != null &&
                kernelCompactClear >= 0 &&
                kernelCompactTris >= 0 &&
                kernelCompactFinalize >= 0 &&
                kernelCompactFinalizeVisibleQueue >= 0;
            bool indexed = baseKernels && indexedKernelContractValid;
            bool hybrid = indexed && hybridKernelContractValid &&
                          !CanUseCompactFormalVBuffer() &&
                          CanUsePortableFloat2FormalVBuffer();

            NaniteRenderPath nextPath;
            string reason;
            if (!dx12 || !compute || !baseKernels)
            {
                nextPath = NaniteRenderPath.Unsupported;
                reason = !dx12
                    ? $"graphics-api={SystemInfo.graphicsDeviceType}"
                    : (!compute ? "compute-shaders=unsupported" : "base-kernel-contract=incomplete");
            }
            else if (hybrid &&
                     settings.formalRasterizationMode ==
                     (int)FormalRasterizationMode.HybridSoftwareHardware)
            {
                nextPath = NaniteRenderPath.Hybrid;
                reason = "explicit hybrid request and all kernel contracts complete";
            }
            else if (indexed)
            {
                nextPath = NaniteRenderPath.HardwareFast;
                reason = "exact triangle-packet kernel contract complete";
            }
            else
            {
                nextPath = NaniteRenderPath.HardwareCompat;
                reason = "procedural indirect compatibility path";
            }

            int signature =
                ((int)nextPath * 397) ^
                (dx12 ? 1 << 1 : 0) ^
                (compute ? 1 << 2 : 0) ^
                (hzbKernelContractValid ? 1 << 3 : 0) ^
                (indexedKernelContractValid ? 1 << 4 : 0) ^
                (hybridKernelContractValid ? 1 << 5 : 0) ^
                ((int)compactVBufferProbeState << 8);
            negotiatedRenderPath = nextPath;
            negotiatedRenderPathReason = reason;
            if (signature != negotiatedCapabilitySignature)
            {
                negotiatedCapabilitySignature = signature;
                Debug.Log(
                    $"[Nanite][Platform] path={nextPath}, reason={reason}, " +
                    $"api={SystemInfo.graphicsDeviceType}, compute={compute}, " +
                    $"kernels=base:{baseKernels},indexed:{indexedKernelContractValid}," +
                    $"hybrid:{hybridKernelContractValid},hzb:{hzbKernelContractValid}, " +
                    $"vbuffer={FormalVBufferFormatName(compactVBufferProbeState == CompactVBufferProbeState.Passed)}, " +
                    $"compactProbe={compactVBufferProbeState}:{compactVBufferProbeReason}, " +
                    "winding=double-sided-correctness-path, triangleIdentity=packet, uavContract=<=8.");
            }

            if (nextPath != NaniteRenderPath.Unsupported &&
                (IsAutomaticPathNegotiationEnabled() || settings.enableCompactFormalVBuffer))
            {
                EnsureCompactVBufferProbeStarted();
            }
        }

        void EnsureCompactVBufferProbeStarted()
        {
            if (compactVBufferProbeState != CompactVBufferProbeState.NotStarted)
                return;

            const GraphicsFormat format = GraphicsFormat.R32G32_UInt;
            bool formatSupported =
                SystemInfo.IsFormatSupported(format, GraphicsFormatUsage.Render) &&
                SystemInfo.IsFormatSupported(format, GraphicsFormatUsage.Sample);
            if (!formatSupported || !SystemInfo.supportsAsyncGPUReadback)
            {
                compactVBufferProbeState = CompactVBufferProbeState.Failed;
                compactVBufferProbeReason = !formatSupported
                    ? "R32G32_UINT render/load unsupported"
                    : "async GPU readback unsupported";
                return;
            }

            Shader probeShader = Resources.Load<Shader>("NaniteCompactVBufferProbe");
            if (probeShader == null || !probeShader.isSupported)
            {
                compactVBufferProbeState = CompactVBufferProbeState.Failed;
                compactVBufferProbeReason = "probe shader missing or unsupported";
                return;
            }

            try
            {
                compactVBufferProbeMaterial = new Material(probeShader)
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                compactVBufferProbeTexture = new RenderTexture(1, 1, 0)
                {
                    name = "Nanite Compact VBuffer ABI Probe",
                    graphicsFormat = format,
                    enableRandomWrite = false,
                    useMipMap = false,
                    autoGenerateMips = false,
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp,
                    hideFlags = HideFlags.HideAndDontSave
                };
                if (!compactVBufferProbeTexture.Create())
                    throw new InvalidOperationException("failed to create R32G32_UINT render target");

                CommandBuffer cmd = CommandBufferPool.Get("Nanite/CompactVBufferProbe");
                try
                {
                    cmd.SetRenderTarget(compactVBufferProbeTexture);
                    cmd.ClearRenderTarget(false, true, Color.clear);
                    cmd.DrawProcedural(
                        Matrix4x4.identity,
                        compactVBufferProbeMaterial,
                        0,
                        MeshTopology.Triangles,
                        3,
                        1);
                    Graphics.ExecuteCommandBuffer(cmd);
                }
                finally
                {
                    CommandBufferPool.Release(cmd);
                }

                compactVBufferProbeState = CompactVBufferProbeState.Pending;
                compactVBufferProbeReason = "GPU write/read verification pending";
                int generation = ++compactVBufferProbeGeneration;
                AsyncGPUReadback.Request(compactVBufferProbeTexture, 0, request =>
                {
                    if (generation != compactVBufferProbeGeneration)
                        return;
                    bool valid = !request.hasError;
                    if (valid)
                    {
                        var data = request.GetData<uint>();
                        valid = data.Length >= 2 &&
                                data[0] == 0x13579BDFu &&
                                data[1] == 0x2468ACE0u;
                    }
                    compactVBufferProbeState = valid
                        ? CompactVBufferProbeState.Passed
                        : CompactVBufferProbeState.Failed;
                    compactVBufferProbeReason = valid
                        ? "integer RTV IDs round-tripped"
                        : "integer RTV ID round-trip failed";
                    ReleaseCompactVBufferProbeResources();
                    negotiatedCapabilitySignature = int.MinValue;
                    RefreshNegotiatedRenderPath();
                });
            }
            catch (Exception e)
            {
                compactVBufferProbeState = CompactVBufferProbeState.Failed;
                compactVBufferProbeReason = e.Message;
                ReleaseCompactVBufferProbeResources();
            }
        }

        void ReleaseCompactVBufferProbeResources()
        {
            if (compactVBufferProbeTexture != null)
            {
                compactVBufferProbeTexture.Release();
                DestroyObject(compactVBufferProbeTexture);
                compactVBufferProbeTexture = null;
            }
            if (compactVBufferProbeMaterial != null)
            {
                DestroyObject(compactVBufferProbeMaterial);
                compactVBufferProbeMaterial = null;
            }
        }

        bool IsRequiredGraphicsApiActive()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D12)
                return true;

            if (!loggedUnsupportedGraphicsApi)
            {
                loggedUnsupportedGraphicsApi = true;
                Debug.LogWarning(
                    $"[Nanite][RF] 当前 Graphics API 为 {SystemInfo.graphicsDeviceType}，Nanite GPU 路径需要 Direct3D12。" +
                    "请在 Player Settings > Other Settings > Graphics APIs for Windows 关闭 Auto Graphics API 并只保留 Direct3D12，然后重启 Unity 重新导入 compute shader。");
            }

            return false;
        }

        bool EnsureHybridShadowResources(int shadowResolution)
        {
            if (!IsShadowHybridRequested() || shadowResolution <= 0 ||
                !EnsureHybridClusterBuffers() ||
                !EnsureHybridSoftwareTileBuffers(shadowResolution, shadowResolution))
                return false;

            int requiredSize = Mathf.NextPowerOfTwo(Mathf.Max(1, shadowResolution));
            if (hybridShadowSoftwareDepth != null &&
                hybridShadowSoftwareDepth.IsCreated() &&
                hybridShadowWinnerFallback != null &&
                hybridShadowWinnerFallback.IsCreated() &&
                hybridShadowTextureSize >= requiredSize)
                return true;

            ReleaseHybridShadowTextures();
            hybridShadowTextureSize = requiredSize;
            hybridShadowSoftwareDepth = new RenderTexture(requiredSize, requiredSize, 0)
            {
                name = "_NaniteHybridShadowSoftwareDepth",
                graphicsFormat = GraphicsFormat.R32_UInt,
                enableRandomWrite = true,
                useMipMap = false,
                autoGenerateMips = false,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            hybridShadowSoftwareDepth.Create();
            hybridShadowWinnerFallback = new RenderTexture(1, 1, 0)
            {
                name = "_NaniteHybridShadowWinnerFallback",
                graphicsFormat = GraphicsFormat.R32_UInt,
                enableRandomWrite = true,
                useMipMap = false,
                autoGenerateMips = false,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            hybridShadowWinnerFallback.Create();
            return hybridShadowSoftwareDepth.IsCreated() &&
                   hybridShadowWinnerFallback.IsCreated();
        }

        void ReleaseHybridShadowTextures()
        {
            if (hybridShadowSoftwareDepth != null)
            {
                hybridShadowSoftwareDepth.Release();
                DestroyObject(hybridShadowSoftwareDepth);
                hybridShadowSoftwareDepth = null;
            }
            if (hybridShadowWinnerFallback != null)
            {
                hybridShadowWinnerFallback.Release();
                DestroyObject(hybridShadowWinnerFallback);
                hybridShadowWinnerFallback = null;
            }
            hybridShadowTextureSize = 0;
            hybridShadowReadyFrame = -1;
            hybridShadowReadyCascade = -1;
        }

        bool DispatchHybridShadowQueue(ExternalMainLightShadowCullContext context)
        {
            if (!EnsureHybridShadowResources(context.shadowResolution) ||
                settings.visibleTriangleCompactShader == null ||
                hybridHardwareClusterBuffer == null ||
                hybridSoftwareClusterBuffer == null ||
                hybridHardwareCountArgsBuffer == null ||
                hybridSoftwareCountArgsBuffer == null ||
                hybridClassifyDispatchArgsBuffer == null ||
                hybridSoftwareDispatchArgsBuffer == null)
                return false;

            ComputeBuffer inputClusters = batchedCulling.GetShadowDrawClusterBuffer(context.cascadeIndex);
            ComputeBuffer inputCountArgs = batchedCulling.GetShadowDrawCountArgsBuffer(context.cascadeIndex);
            if (inputClusters == null || inputCountArgs == null ||
                !sceneVisibilityBackend.TryGetSceneBuffers(
                    out var vertexData,
                    out var indices,
                    out var triangleCluster,
                    out _,
                    out _,
                    out _,
                    out _,
                    out var instanceLocalToWorld,
                    out _,
                    out _,
                    out int vertexStride,
                    out _))
                return false;

            UnsafeCommandBuffer cmd = context.commandBuffer;
            ComputeShader cs = settings.visibleTriangleCompactShader;
            Matrix4x4 worldToClip =
                GL.GetGPUProjectionMatrix(context.projectionMatrix, true) * context.viewMatrix;
            int resolution = Mathf.Max(1, context.shadowResolution);
            int tileCountX = Mathf.Max(1, (resolution + kHybridSoftwareTileSize - 1) / kHybridSoftwareTileSize);
            int tileCountY = tileCountX;
            int tileCount = tileCountX * tileCountY;

            cmd.SetBufferCounterValue(hybridHardwareClusterBuffer, 0u);
            cmd.SetBufferCounterValue(hybridSoftwareClusterBuffer, 0u);
            PrepareHybridDispatch(
                cmd,
                cs,
                kernelPrepareHybridDispatch,
                inputCountArgs,
                hybridClassifyDispatchArgsBuffer);
            cmd.SetComputeBufferParam(cs, kernelClassifyHybridClusters, "_HybridInputClusters", inputClusters);
            cmd.SetComputeBufferParam(cs, kernelClassifyHybridClusters, "_HybridInputCountArgs", inputCountArgs);
            cmd.SetComputeBufferParam(cs, kernelClassifyHybridClusters, "_HybridHardwareClusters", hybridHardwareClusterBuffer);
            cmd.SetComputeBufferParam(cs, kernelClassifyHybridClusters, "_HybridSoftwareClusters", hybridSoftwareClusterBuffer);
            BindHybridGeometry(
                cmd,
                cs,
                kernelClassifyHybridClusters,
                worldToClip,
                vertexData,
                indices,
                instanceLocalToWorld,
                triangleCluster,
                sceneVisibilityBackend.GeometryClusterBoundsBuffer,
                sceneVisibilityBackend.GeometryClusterLongestEdgeBuffer,
                vertexStride);
            SetHybridScreenParams(cmd, cs, resolution, resolution);
            cmd.SetComputeFloatParam(
                cs,
                "_HybridSoftwareMaxEdgePixels",
                Mathf.Max(1f, settings.hybridSoftwareMaxEdgePixels));
            cmd.SetComputeIntParam(cs, "_HybridDispatchGroupsX", 65535);
            cmd.DispatchCompute(cs, kernelClassifyHybridClusters, hybridClassifyDispatchArgsBuffer, 0u);
            cmd.CopyCounterValue(hybridHardwareClusterBuffer, hybridHardwareCountArgsBuffer, 0u);
            cmd.CopyCounterValue(hybridSoftwareClusterBuffer, hybridSoftwareCountArgsBuffer, 0u);
            PrepareHybridDispatch(
                cmd,
                cs,
                kernelPrepareHybridDispatch,
                hybridSoftwareCountArgsBuffer,
                hybridSoftwareDispatchArgsBuffer);

            SetHybridTileParams(cmd, cs, tileCountX, tileCountY, tileCount);
            cmd.SetBufferCounterValue(hybridSoftwareTileListBuffer, 0u);
            cmd.SetComputeBufferParam(cs, kernelClearHybridTileHeads, "_HybridSoftwareTileHeads", hybridSoftwareTileHeadBuffer);
            cmd.DispatchCompute(cs, kernelClearHybridTileHeads, Mathf.Max(1, (tileCount + 255) / 256), 1, 1);
            cmd.SetComputeBufferParam(cs, kernelClearHybridTileWork, "_HybridSoftwareTileNodeCounter", hybridSoftwareTileNodeCounterBuffer);
            cmd.DispatchCompute(cs, kernelClearHybridTileWork, 1, 1, 1);

            cmd.SetComputeBufferParam(cs, kernelBuildHybridTileWork, "_HybridSoftwareClustersRead", hybridSoftwareClusterBuffer);
            cmd.SetComputeBufferParam(cs, kernelBuildHybridTileWork, "_HybridSoftwareCountArgs", hybridSoftwareCountArgsBuffer);
            BindHybridGeometry(
                cmd,
                cs,
                kernelBuildHybridTileWork,
                worldToClip,
                vertexData,
                indices,
                instanceLocalToWorld,
                triangleCluster,
                sceneVisibilityBackend.GeometryClusterBoundsBuffer,
                sceneVisibilityBackend.GeometryClusterLongestEdgeBuffer,
                vertexStride);
            cmd.SetComputeBufferParam(cs, kernelBuildHybridTileWork, "_HybridSoftwareTileHeads", hybridSoftwareTileHeadBuffer);
            cmd.SetComputeBufferParam(cs, kernelBuildHybridTileWork, "_HybridSoftwareTileNodes", hybridSoftwareTileNodeBuffer);
            cmd.SetComputeBufferParam(cs, kernelBuildHybridTileWork, "_HybridSoftwareTileNodeCounter", hybridSoftwareTileNodeCounterBuffer);
            cmd.SetComputeBufferParam(cs, kernelBuildHybridTileWork, "_HybridSoftwareFallbackFlags", hybridSoftwareFallbackFlagsBuffer);
            cmd.SetComputeBufferParam(cs, kernelBuildHybridTileWork, "_HybridSoftwareTileList", hybridSoftwareTileListBuffer);
            cmd.SetComputeBufferParam(cs, kernelBuildHybridTileWork, "_HybridHardwareClusters", hybridHardwareClusterBuffer);
            cmd.SetComputeIntParam(cs, "_HybridTileNodeCapacity", Mathf.Max(1, hybridSoftwareTileNodeCapacity));
            cmd.SetComputeIntParam(cs, "_HybridDepthOnly", 1);
            cmd.SetComputeIntParam(cs, "_HybridShadowMode", 1);
            Vector3 lightDirection = -context.shadowLight.localToWorldMatrix.GetColumn(2);
            cmd.SetComputeVectorParam(cs, "_HybridShadowLightDirection", new Vector4(
                lightDirection.x,
                lightDirection.y,
                lightDirection.z,
                0f));
            cmd.SetComputeVectorParam(cs, "_HybridShadowBias", context.shadowBias);
            cmd.SetComputeIntParam(cs, "_HybridDispatchGroupsX", 65535);
            cmd.DispatchCompute(cs, kernelBuildHybridTileWork, hybridSoftwareDispatchArgsBuffer, 0u);

            cmd.CopyCounterValue(hybridHardwareClusterBuffer, hybridHardwareCountArgsBuffer, 0u);
            cmd.CopyCounterValue(hybridSoftwareTileListBuffer, hybridSoftwareTileCountArgsBuffer, 0u);
            cmd.CopyCounterValue(hybridSoftwareTileListBuffer, hybridSoftwareTileDrawArgsBuffer, sizeof(uint));
            PrepareHybridDispatch(
                cmd,
                cs,
                kernelPrepareHybridDispatch,
                hybridSoftwareTileCountArgsBuffer,
                hybridSoftwareTileDispatchArgsBuffer);

            cmd.SetComputeBufferParam(cs, kernelSoftwareRasterTiles, "_HybridSoftwareClustersRead", hybridSoftwareClusterBuffer);
            cmd.SetComputeBufferParam(cs, kernelSoftwareRasterTiles, "_HybridSoftwareCountArgs", hybridSoftwareCountArgsBuffer);
            BindHybridGeometry(
                cmd,
                cs,
                kernelSoftwareRasterTiles,
                worldToClip,
                vertexData,
                indices,
                instanceLocalToWorld,
                triangleCluster,
                sceneVisibilityBackend.GeometryClusterBoundsBuffer,
                sceneVisibilityBackend.GeometryClusterLongestEdgeBuffer,
                vertexStride);
            cmd.SetComputeBufferParam(cs, kernelSoftwareRasterTiles, "_HybridSoftwareTileHeads", hybridSoftwareTileHeadBuffer);
            cmd.SetComputeBufferParam(cs, kernelSoftwareRasterTiles, "_HybridSoftwareTileNodes", hybridSoftwareTileNodeBuffer);
            cmd.SetComputeBufferParam(cs, kernelSoftwareRasterTiles, "_HybridSoftwareFallbackFlags", hybridSoftwareFallbackFlagsBuffer);
            cmd.SetComputeBufferParam(cs, kernelSoftwareRasterTiles, "_HybridSoftwareTileListRead", hybridSoftwareTileListBuffer);
            cmd.SetComputeBufferParam(cs, kernelSoftwareRasterTiles, "_HybridSoftwareTileCountArgs", hybridSoftwareTileCountArgsBuffer);
            cmd.SetComputeTextureParam(cs, kernelSoftwareRasterTiles, "_HybridSoftwareDepth", hybridShadowSoftwareDepth);
            cmd.SetComputeTextureParam(cs, kernelSoftwareRasterTiles, "_HybridSoftwareWinner", hybridShadowWinnerFallback);
            cmd.SetComputeIntParam(cs, "_HybridDepthOnly", 1);
            cmd.SetComputeIntParam(cs, "_HybridShadowMode", 1);
            cmd.SetComputeVectorParam(cs, "_HybridShadowLightDirection", new Vector4(
                lightDirection.x,
                lightDirection.y,
                lightDirection.z,
                0f));
            cmd.SetComputeVectorParam(cs, "_HybridShadowBias", context.shadowBias);
            cmd.SetComputeIntParam(cs, "_HybridDispatchGroupsX", 65535);
            cmd.DispatchCompute(cs, kernelSoftwareRasterTiles, hybridSoftwareTileDispatchArgsBuffer, 0u);

            bool indexedBuilt = sceneVisibilityBackend.DispatchIndexedShadowDrawQueueReuse(
                cmd,
                cs,
                kernelPrepareIndexedShadowDrawQueue,
                kernelBuildIndexedShadowDrawQueue,
                kernelCompactFinalizeVisibleQueue,
                hybridHardwareClusterBuffer,
                hybridHardwareCountArgsBuffer,
                context.cascadeIndex);
            if (!indexedBuilt)
                return false;

            hybridShadowReadyFrame = Time.frameCount;
            hybridShadowReadyCascade = context.cascadeIndex;
            return true;
        }

        void BuildExternalMainLightShadowCascade(ExternalMainLightShadowCullContext context)
        {
            if (!settings.enable || !settings.enableShadowCasting ||
                !IsIndexedRasterRequested() ||
                context.commandBuffer == null || context.camera == null ||
                context.cascadeIndex < 0 || context.cascadeIndex >= Mathf.Clamp(settings.maxNaniteShadowCascades, 1, 4) ||
                sceneVisibilityBackend == null || !sceneVisibilityBackend.IndexedDrawAvailable ||
                batchedCulling == null ||
                !batchedCulling.IsShadowDrawQueueReady(context.camera, context.cascadeIndex))
                return;

            if (kernelPrepareIndexedShadowDrawQueue < 0 || kernelBuildIndexedShadowDrawQueue < 0)
                InitCompactKernels();
            if (settings.visibleTriangleCompactShader == null ||
                kernelPrepareIndexedShadowDrawQueue < 0 || kernelBuildIndexedShadowDrawQueue < 0)
                return;

            if (DispatchHybridShadowQueue(context))
                return;

            sceneVisibilityBackend.DispatchIndexedShadowDrawQueueReuse(
                context.commandBuffer,
                settings.visibleTriangleCompactShader,
                kernelPrepareIndexedShadowDrawQueue,
                kernelBuildIndexedShadowDrawQueue,
                kernelCompactFinalizeVisibleQueue,
                batchedCulling.GetShadowDrawClusterBuffer(context.cascadeIndex),
                batchedCulling.GetShadowDrawCountArgsBuffer(context.cascadeIndex),
                context.cascadeIndex);
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
                        ? (batchedCulling.LastShadowUsedHierarchyQueue
                            ? "shadowFrustum/fusedHierarchyQueue"
                            : (batchedCulling.LastShadowUsedSpatialHierarchy
                                ? "shadowFrustum/fusedSpatialQueue"
                                : "shadowFrustum/fusedDirect"))
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
            DrawHybridSoftwareShadowMerge(context.commandBuffer, material, context);
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
            int fallbackCapableInstances = 0;
            long fallbackDraws = 0;
            long fallbackTriangles = 0;
            for (int i = 0; i < proxies.Count; i++)
            {
                var candidate = proxies[i];
                if (candidate == null || !candidate.isActiveAndEnabled ||
                    candidate.naniteMesh == null || candidate.ForceNaniteRequested ||
                    !candidate.CanUseRasterFallback)
                    continue;
                fallbackCapableInstances++;
                fallbackDraws += Mathf.Max(1, candidate.RasterFallbackDrawCount);
                fallbackTriangles += Mathf.Max(0, candidate.RasterFallbackTriangleCount);
            }
            bool lowSubmissionRaster = settings.enableLowSubmissionRasterFallback &&
                                       fallbackCapableInstances > 0 &&
                                       fallbackCapableInstances <= Mathf.Max(0, settings.lowSubmissionMaxInstances) &&
                                       fallbackDraws <= Mathf.Max(0, settings.lowSubmissionMaxDraws) &&
                                       fallbackTriangles <= Mathf.Max(0, settings.lowSubmissionMaxTriangles);
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
                bool unsupportedShader = ProxyUsesUnsupportedResolveShader(proxy);
                bool useRaster = !proxy.ForceNaniteRequested &&
                                  proxy.CanUseRasterFallback &&
                                  (proxy.NativeRendererRequested || autoRaster ||
                                   lowSubmissionRaster || unsupportedShader);
                proxy.SetRasterFallbackActive(useRaster);
                if (unsupportedShader && !proxy.CanUseRasterFallback &&
                    warnedUnsupportedMaterialProxies.Add(proxy.GetInstanceID()))
                {
                    Debug.LogWarning(
                        $"[Nanite][Material] '{proxy.name}' uses a shader outside the URP/Lit resolve family " +
                        "and has no native MeshRenderer fallback. Forced Nanite fails closed and does not " +
                        "shade it with incorrect URP/Lit semantics; author a Nanite shader family or " +
                        "provide the source MeshRenderer and use Raster/Auto mode.",
                        proxy);
                }
            }
        }

        static bool ProxyUsesUnsupportedResolveShader(NaniteRuntimeProxy proxy)
        {
            if (proxy == null)
                return false;
            Material[] materials = proxy.resolveMaterials;
            if (materials == null || materials.Length == 0)
            {
                var renderer = proxy.GetComponent<Renderer>();
                if (renderer == null)
                    renderer = proxy.GetComponentInChildren<Renderer>();
                materials = renderer != null ? renderer.sharedMaterials : null;
            }
            if (materials == null || materials.Length == 0)
                materials = proxy.naniteMesh != null ? proxy.naniteMesh.sourceMaterials : null;
            if (materials == null)
                return false;
            for (int i = 0; i < materials.Length; i++)
            {
                if (!NaniteSceneVisibilityBufferBackend.SupportsFormalResolveMaterial(materials[i]))
                    return true;
            }
            return false;
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
            {
                hzbKernelContractValid = false;
                RefreshNegotiatedRenderPath();
                return;
            }

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
            hzbKernelContractValid =
                settings.hzbBuilderShader != null &&
                kernelCopyDepth >= 0 &&
                kernelDownsample >= 0;
            RefreshNegotiatedRenderPath();
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
            internal TextureHandle depth;
        }

        class VBufferCompositeRgPassData
        {
            internal Material material;
            internal TextureHandle vbuffer;
        }

        class VBufferDebugResolveRgPassData
        {
            internal Material material;
            internal Camera camera;
            internal TextureHandle vbuffer;
            internal int screenWidth;
            internal int screenHeight;
        }

        class DepthWriteRgPassData
        {
            internal Material material;
            internal Camera camera;
            internal bool writeFormalVBuffer;
            internal int screenWidth;
            internal int screenHeight;
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
            internal int compactClusterOffset;
            internal bool hybridHardwareQueue;
            internal bool drawProceduralFallback;
            internal bool preferAppendOnly;
        }

        class HybridClassifyRgPassData
        {
            internal Camera camera;
            internal Matrix4x4 worldToClip;
            internal ComputeBuffer inputClusters;
            internal ComputeBuffer inputCountArgs;
            internal ComputeBuffer hardwareClusters;
            internal ComputeBuffer softwareClusters;
            internal ComputeBuffer hardwareCountArgs;
            internal ComputeBuffer softwareCountArgs;
            internal ComputeBuffer classifyDispatchArgs;
            internal ComputeBuffer softwareDispatchArgs;
            internal ComputeBuffer vertexData;
            internal ComputeBuffer indices;
            internal ComputeBuffer instanceLocalToWorld;
            internal ComputeBuffer triangleSubMesh;
            internal ComputeBuffer triangleCluster;
            internal ComputeBuffer clusterBounds;
            internal ComputeBuffer clusterLongestEdge;
            internal int vertexStride;
            internal int selectionKey;
            internal GraphicsBuffer dependencyBuffer;
            internal GraphicsBuffer viewConstants;
        }

        class HybridSoftwareRasterRgPassData
        {
            internal TextureHandle softwareDepth;
            internal TextureHandle softwareWinner;
            internal ComputeBuffer hardwareClusters;
            internal ComputeBuffer hardwareCountArgs;
            internal ComputeBuffer softwareClusters;
            internal ComputeBuffer softwareCountArgs;
            internal ComputeBuffer softwareDispatchArgs;
            internal ComputeBuffer vertexData;
            internal ComputeBuffer indices;
            internal ComputeBuffer instanceLocalToWorld;
            internal ComputeBuffer triangleSubMesh;
            internal Matrix4x4 worldToClip;
            internal int vertexStride;
            internal int screenWidth;
            internal int screenHeight;
            internal int tileCountX;
            internal int tileCountY;
            internal int tileCount;
            internal GraphicsBuffer tileHeads;
            internal GraphicsBuffer tileNodes;
            internal GraphicsBuffer tileNodeCounter;
            internal GraphicsBuffer fallbackFlags;
            internal int tileNodeCapacity;
            internal GraphicsBuffer tileList;
            internal GraphicsBuffer tileCountArgs;
            internal GraphicsBuffer tileDispatchArgs;
            internal GraphicsBuffer tileDrawArgs;
            internal GraphicsBuffer dependencyBuffer;
            internal GraphicsBuffer viewConstants;
        }

        class HybridMergeRgPassData
        {
            internal Material material;
            internal TextureHandle softwareDepth;
            internal TextureHandle softwareWinner;
            internal ComputeBuffer softwareClusters;
            internal GraphicsBuffer tileList;
            internal GraphicsBuffer tileDrawArgs;
            internal int screenWidth;
            internal int screenHeight;
            internal int tileCountX;
            internal int triangleCount;
            internal int instanceCount;
            internal int maxSubMeshCount;
            internal bool useCompactVBuffer;
            internal bool useFloat2VBuffer;
        }

        class HybridAuditRgPassData
        {
            internal GraphicsBuffer stats;
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
            internal BufferHandle tileMaterialBinList;
            internal BufferHandle tileIndirectArgs;
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
            internal BufferHandle tileMaterialBinList;
            internal BufferHandle tileIndirectArgs;
            internal int screenWidth;
            internal int screenHeight;
            internal int vbufferWidth;
            internal int vbufferHeight;
        }

        class PageRetirementFenceRgPassData
        {
        }

        class RuntimeTelemetryRgPassData
        {
            internal Camera camera;
            internal ComputeBuffer hardwareCount;
            internal ComputeBuffer softwareCount;
        }

        const int kGBufferDepthSliceIndex = 4;

        void RecordRenderGraphPass(
            RenderGraph renderGraph,
            ContextContainer frameData,
            PassKind passKind,
            string passName,
            ProfilingSampler profilingSampler)
        {
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            if (cameraData == null || cameraData.camera == null)
                return;

            if (passKind == PassKind.FirstCull)
            {
                recordedHzbDepthSource = TextureHandle.nullHandle;
                recordedPass1VBuffer = TextureHandle.nullHandle;
                recordedFormalVBuffer = TextureHandle.nullHandle;
            }

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
                bool fusePass1VBuffer = IsFormalVisibilityEnabled() &&
                                        IsHzbActiveForScene() &&
                                        ShouldEnqueueSecondCull() &&
                                        IsIndexedRasterRequested();
                var depthMaterial = fusePass1VBuffer
                    ? EnsureVBufferPreviewMaterial()
                    : EnsureDepthWriteMaterial();
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

                TextureHandle pass1VBuffer = TextureHandle.nullHandle;
                if (fusePass1VBuffer && resourceData.activeColorTexture.IsValid())
                {
                    var pass1Desc = renderGraph.GetTextureDesc(resourceData.activeColorTexture);
                    pass1Desc.name = "_NaniteFormalVBufferPass1";
                    pass1Desc.format = CanUseCompactFormalVBuffer()
                        ? GraphicsFormat.R32G32_UInt
                        : (CanUsePortableFloat2FormalVBuffer()
                            ? GraphicsFormat.R32G32_SFloat
                            : GraphicsFormat.R32G32B32A32_SFloat);
                    pass1Desc.filterMode = FilterMode.Point;
                    pass1Desc.clearBuffer = true;
                    pass1Desc.clearColor = Color.clear;
                    pass1VBuffer = renderGraph.CreateTexture(pass1Desc);
                    recordedPass1VBuffer = pass1VBuffer;
                }

                using (var builder = renderGraph.AddRasterRenderPass<DepthWriteRgPassData>("Nanite/WriteDepth", out var passData, profilingSampler))
                {
                    passData.material = depthMaterial;
                    passData.camera = cameraData.camera;
                    passData.writeFormalVBuffer = pass1VBuffer.IsValid();
                    passData.screenWidth = Mathf.Max(1, cameraData.cameraTargetDescriptor.width);
                    passData.screenHeight = Mathf.Max(1, cameraData.cameraTargetDescriptor.height);
                    builder.AllowPassCulling(false);
                    builder.AllowGlobalStateModification(true);
                    if (sceneVisibilityBackend != null)
                    {
                        if (sceneVisibilityBackend.IndexedTrianglePacketBuffer != null)
                        {
                            builder.UseBuffer(
                                renderGraph.ImportBuffer(sceneVisibilityBackend.IndexedTrianglePacketBuffer),
                                AccessFlags.Read);
                        }
                        if (sceneVisibilityBackend.IndexedDrawArgsBuffer != null)
                        {
                            builder.UseBuffer(
                                renderGraph.ImportBuffer(sceneVisibilityBackend.IndexedDrawArgsBuffer),
                                AccessFlags.Read);
                        }
                        if (sceneVisibilityBackend.IndexedFallbackDrawArgsBuffer != null)
                        {
                            builder.UseBuffer(
                                renderGraph.ImportBuffer(sceneVisibilityBackend.IndexedFallbackDrawArgsBuffer),
                                AccessFlags.Read);
                        }
                        if (sceneVisibilityBackend.IndexedOverflowClusterBuffer != null)
                        {
                            builder.UseBuffer(
                                renderGraph.ImportBuffer(sceneVisibilityBackend.IndexedOverflowClusterBuffer),
                                AccessFlags.Read);
                        }
                        if (sceneVisibilityBackend.IndexedCameraPacketSliceBuffer != null)
                        {
                            builder.UseBuffer(
                                renderGraph.ImportBuffer(sceneVisibilityBackend.IndexedCameraPacketSliceBuffer),
                                AccessFlags.Read);
                        }
                    }
                    if (pass1VBuffer.IsValid())
                        builder.SetRenderAttachment(pass1VBuffer, 0, AccessFlags.Write);
                    builder.SetRenderAttachmentDepth(resourceData.activeDepthTexture, AccessFlags.ReadWrite);

                    builder.SetRenderFunc((DepthWriteRgPassData data, RasterGraphContext context) =>
                    {
                        if (data.writeFormalVBuffer)
                        {
                            ExecuteFormalVisibilityRaster(
                                context.cmd,
                                data.material,
                                data.camera,
                                data.screenWidth,
                                data.screenHeight,
                                kCompactSelectionFirst,
                                0,
                                false,
                                true,
                                false,
                                true);
                            // The fused pass is the depth producer for HZB. The old
                            // standalone depth path set this flag, but fusion did not,
                            // so CopyDepth returned early and previous HZB was never
                            // published (hzbRejected stayed zero in dense scenes).
                            var hzbState = GetOrCreateState(
                                data.camera.pixelWidth,
                                data.camera.pixelHeight,
                                data.camera.GetInstanceID());
                            hzbState.depthWrittenThisFrame = true;
                        }
                        else
                        {
                            ExecuteWriteDepth(context.cmd, data.material, data.camera);
                        }
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
                UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
                if (!resourceData.activeColorTexture.IsValid())
                    return;

                var debugResolveMaterial = EnsureVBufferDebugResolveMaterial();
                if (debugResolveMaterial != null && recordedFormalVBuffer.IsValid())
                {
                    int screenWidth = Mathf.Max(1, cameraData.scaledWidth);
                    int screenHeight = Mathf.Max(1, cameraData.scaledHeight);
                    using (var builder = renderGraph.AddRasterRenderPass<VBufferDebugResolveRgPassData>(
                               "Nanite/DebugVisualization",
                               out var passData,
                               profilingSampler))
                    {
                        passData.material = debugResolveMaterial;
                        passData.camera = cameraData.camera;
                        passData.vbuffer = recordedFormalVBuffer;
                        passData.screenWidth = screenWidth;
                        passData.screenHeight = screenHeight;
                        builder.AllowPassCulling(false);
                        builder.AllowGlobalStateModification(true);
                        builder.UseTexture(passData.vbuffer, AccessFlags.Read);
                        builder.SetRenderAttachment(resourceData.activeColorTexture, 0, AccessFlags.ReadWrite);
                        builder.SetRenderFunc((VBufferDebugResolveRgPassData data, RasterGraphContext context) =>
                        {
                            ExecuteDebugVBufferResolve(
                                context.cmd,
                                data.material,
                                data.camera,
                                data.vbuffer,
                                data.screenWidth,
                                data.screenHeight);
                        });
                    }
                    return;
                }

                var material = EnsureVBufferPreviewMaterial();
                if (material == null)
                    return;
                RecordVisibleTriangleCompactPass(renderGraph, cameraData.camera, kCompactSelectionMerged, profilingSampler);

                using (var builder = renderGraph.AddRasterRenderPass<VBufferPreviewRgPassData>("Nanite/DebugVisualization", out var passData, profilingSampler))
                {
                    passData.material = material;
                    passData.camera = cameraData.camera;
                    passData.depth = resourceData.activeDepthTexture;
                    builder.AllowPassCulling(false);
                    builder.AllowGlobalStateModification(true);
                    builder.SetRenderAttachment(resourceData.activeColorTexture, 0, AccessFlags.ReadWrite);
                    if (passData.depth.IsValid())
                        builder.SetRenderAttachmentDepth(passData.depth, AccessFlags.Read);

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
                // Both cull passes can append to the traversal-produced HW/SW
                // queues. Track the same dependency through Pass2 so async SW
                // raster cannot start while the merged cut is still changing.
                if (IsHybridRasterRequested())
                {
                    EnsureHybridTraversalDependency();
                    if (hybridTraversalDependencyBuffer != null)
                    {
                        builder.UseBuffer(
                            renderGraph.ImportBuffer(hybridTraversalDependencyBuffer),
                            AccessFlags.Write);
                    }
                }

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
                            sceneVisibilityBackend.IsGpuVisibleMaskReadyFor(data.camera))
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
            kernelClearMaterialTileBins = -1;
            keywordCompactVBufferClassifyInitialized = false;
            keywordFloat2VBufferClassifyInitialized = false;
            if (settings.materialTileClassifyShader == null)
                return;

            try
            {
                kernelMaterialTileClassify = settings.materialTileClassifyShader.FindKernel("CSTileClassify");
                kernelClearMaterialTileBins = settings.materialTileClassifyShader.FindKernel("CSClearTileBins");
                keywordCompactVBufferClassify = new LocalKeyword(
                    settings.materialTileClassifyShader,
                    kCompactVBufferKeyword);
                keywordCompactVBufferClassifyInitialized = keywordCompactVBufferClassify.isValid;
                keywordFloat2VBufferClassify = new LocalKeyword(
                    settings.materialTileClassifyShader,
                    kFloat2VBufferKeyword);
                keywordFloat2VBufferClassifyInitialized = keywordFloat2VBufferClassify.isValid;
            }
            catch
            {
                kernelMaterialTileClassify = -1;
                kernelClearMaterialTileBins = -1;
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
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            // TextureDesc reports the RTHandle's persistent physical allocation. After a
            // 4K Game view has increased the RTHandle reference size, a smaller Scene/Game
            // viewport still renders only scaledWidth x scaledHeight pixels in its upper-left
            // region. Using the physical allocation here made VBuffer Texture.Load work by
            // accident, while barycentrics, UV gradients and normals were reconstructed from
            // a folded screen coordinate. Always size the formal buffers and inverse viewport
            // from the current camera's logical render area.
            int fullScreenWidth = Mathf.Max(1, cameraData.scaledWidth);
            int fullScreenHeight = Mathf.Max(1, cameraData.scaledHeight);
            // 半分辨率路径尚未稳定（采样/深度 upsample 易花屏）；强制走全分辨率恢复正确画面。
            bool halfResVBuffer = false;
            int formalScreenWidth = halfResVBuffer ? Mathf.Max(1, fullScreenWidth / 2) : fullScreenWidth;
            int formalScreenHeight = halfResVBuffer ? Mathf.Max(1, fullScreenHeight / 2) : fullScreenHeight;

            var vbufferDesc = colorDesc;
            vbufferDesc.name = "_NaniteFormalVBuffer";
            bool useCompactVBuffer = CanUseCompactFormalVBuffer();
            vbufferDesc.format = useCompactVBuffer
                ? GraphicsFormat.R32G32_UInt
                : (CanUsePortableFloat2FormalVBuffer()
                    ? GraphicsFormat.R32G32_SFloat
                    : GraphicsFormat.R32G32B32A32_SFloat);
            vbufferDesc.width = formalScreenWidth;
            vbufferDesc.height = formalScreenHeight;
            vbufferDesc.filterMode = FilterMode.Point;
            vbufferDesc.clearBuffer = true;
            vbufferDesc.clearColor = Color.clear;
            bool reusePass1VBuffer = recordedPass1VBuffer.IsValid() &&
                                     ShouldEnqueueSecondCull() &&
                                     !halfResVBuffer;
            var vbuffer = reusePass1VBuffer
                ? recordedPass1VBuffer
                : renderGraph.CreateTexture(vbufferDesc);
            recordedFormalVBuffer = vbuffer;

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
                                   kernelClearMaterialTileBins >= 0 &&
                                   (!useCompactVBuffer || keywordCompactVBufferClassifyInitialized);
            BufferHandle tileMaterialMaskHandle = default;
            BufferHandle tileMaterialBinListHandle = default;
            BufferHandle tileIndirectArgsHandle = default;
            if (canTileClassify)
            {
                EnsureTileBuffers(formalScreenWidth, formalScreenHeight);
                canTileClassify = tileMaterialMaskBuffer != null &&
                                  tileMaterialBinListBuffer != null &&
                                  tileIndirectArgsBuffer != null;
                if (canTileClassify)
                {
                    tileMaterialMaskHandle = renderGraph.ImportBuffer(tileMaterialMaskBuffer);
                    tileMaterialBinListHandle = renderGraph.ImportBuffer(tileMaterialBinListBuffer);
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
                                  sceneVisibilityBackend.Pass1ClusterVisibleBuffer != null &&
                                  sceneVisibilityBackend.Pass2ClusterVisibleBuffer != null &&
                                  batchedCulling != null;

            int formalCompactSelection = ShouldEnqueueSecondCull()
                ? kCompactSelectionMerged
                : kCompactSelectionFirst;
            bool hybridRaster = false;
            TextureHandle softwareDepth = default;
            TextureHandle softwareWinner = default;
            // Classification only needs a conservative screen-space estimate. Use
            // explicit camera matrices here so recording is independent of mutable
            // URP global view state. Software raster vertices consume the GPU view
            // snapshot captured at execution time by CSCaptureHybridView below.
            Matrix4x4 hybridWorldToClip =
                GL.GetGPUProjectionMatrix(camera.projectionMatrix, true) *
                camera.worldToCameraMatrix;
            if (!bevyDualRaster && IsHybridRasterRequestedForCamera(camera) &&
                EnsureHybridSoftwareTileBuffers(formalScreenWidth, formalScreenHeight) &&
                sceneVisibilityBackend != null &&
                sceneVisibilityBackend.TryGetSceneBuffers(
                    out var hybridVertexData,
                    out var hybridIndices,
                    out var hybridTriangleCluster,
                    out _,
                    out _,
                    out var hybridTriangleSubMesh,
                    out _,
                    out var hybridInstanceLocalToWorld,
                    out _,
                    out _,
                    out int hybridVertexStride,
                    out _))
            {
                var softwareDepthDesc = vbufferDesc;
                softwareDepthDesc.name = "_NaniteHybridSoftwareDepth";
                softwareDepthDesc.format = GraphicsFormat.R32_UInt;
                softwareDepthDesc.enableRandomWrite = true;
                // Hybrid merge may sample outside the compact software tile list
                // (the portable correctness path is a full-screen triangle).  A
                // transient RenderGraph texture has undefined contents unless it
                // is explicitly cleared, so stale winner bits were decoded as
                // valid triangle references and painted most of the SW region
                // with one unrelated material.  Zero is the ABI's invalid winner
                // sentinel and is also a valid far-depth clear bit pattern for the
                // reversed-Z path; every actually covered pixel is overwritten by
                // CSSoftwareRasterTiles below.
                softwareDepthDesc.clearBuffer = true;
                softwareDepthDesc.clearColor = Color.clear;
                softwareDepth = renderGraph.CreateTexture(softwareDepthDesc);

                var softwareWinnerDesc = softwareDepthDesc;
                softwareWinnerDesc.name = "_NaniteHybridSoftwareWinner";
                softwareWinner = renderGraph.CreateTexture(softwareWinnerDesc);

                hybridRaster = RecordHybridClassificationPass(
                    renderGraph,
                    camera,
                    hybridWorldToClip,
                    formalCompactSelection,
                    hybridVertexData,
                    hybridIndices,
                    hybridInstanceLocalToWorld,
                    hybridTriangleCluster,
                    sceneVisibilityBackend.GeometryClusterBoundsBuffer,
                    sceneVisibilityBackend.GeometryClusterLongestEdgeBuffer,
                    hybridVertexStride,
                    profilingSampler);
                if (hybridRaster)
                {
                    RecordHybridSoftwareRasterPass(
                        renderGraph,
                        camera,
                        softwareDepth,
                        softwareWinner,
                        hybridWorldToClip,
                        hybridVertexData,
                        hybridIndices,
                        hybridInstanceLocalToWorld,
                        hybridTriangleSubMesh,
                        hybridVertexStride,
                        formalScreenWidth,
                        formalScreenHeight,
                        profilingSampler);
                }
            }

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
                    clearColorHint: true,
                    hybridHardwareQueue: hybridRaster,
                    preferAppendOnly: reusePass1VBuffer);
            }

            if (hybridRaster)
            {
                RecordHybridMergePass(
                    renderGraph,
                    rasterMaterial,
                    vbuffer,
                    rasterDepth,
                    softwareDepth,
                    softwareWinner,
                    formalScreenWidth,
                    formalScreenHeight,
                    profilingSampler);
            }

            // Telemetry is a real RenderGraph consumer, scheduled after the
            // classifier/fallback queues are finalized. Requesting it from the
            // earlier WriteDepth indexed build sampled cleared or stale bins.
            RecordRuntimeTelemetryPass(renderGraph, camera, hybridRaster, profilingSampler);

            if (canTileClassify)
            {
                using (var builder = renderGraph.AddComputePass<FormalTileClassifyRgPassData>("Nanite/MaterialTileClassify", out var passData, profilingSampler))
                {
                    passData.camera = camera;
                    passData.vbuffer = vbuffer;
                    passData.screenWidth = formalScreenWidth;
                    passData.screenHeight = formalScreenHeight;
                    passData.tileMaterialMask = tileMaterialMaskHandle;
                    passData.tileMaterialBinList = tileMaterialBinListHandle;
                    passData.tileIndirectArgs = tileIndirectArgsHandle;
                    builder.AllowPassCulling(false);
                    builder.AllowGlobalStateModification(true);
                    builder.UseTexture(vbuffer, AccessFlags.Read);
                    builder.UseBuffer(passData.tileMaterialMask, AccessFlags.Write);
                    builder.UseBuffer(passData.tileMaterialBinList, AccessFlags.Write);
                    builder.UseBuffer(passData.tileIndirectArgs, AccessFlags.Write);
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
                passData.tileMaterialBinList = tileMaterialBinListHandle;
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
                    builder.UseBuffer(passData.tileMaterialBinList, AccessFlags.Read);
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

        bool IsHybridConfigured() => IsHybridPathConfigured();

        bool IsHybridRasterRequested() =>
            IsHybridConfigured() && (ForceHybridForDiagnostics || cameraHybridCost.UseHybrid);

        bool IsHybridRasterRequestedForCamera(Camera camera)
        {
            if (!IsHybridRasterRequested() || camera == null)
                return false;

            // The camera Hybrid path currently owns one shared async queue/tile
            // workspace. Let the primary camera own it and keep every secondary
            // camera on the complete hardware path; otherwise RenderGraph can
            // record main/interference/main before execution and the secondary
            // classifier overwrites the primary SW queue and view snapshot.
            // This is an internal resource-ownership rule, not a quality switch.
            int cameraId = camera.GetInstanceID();
            if (hybridWorkspaceOwnerCameraId == 0)
                hybridWorkspaceOwnerCameraId = cameraId;
            return hybridWorkspaceOwnerCameraId == cameraId;
        }

        bool IsShadowHybridRequested() =>
            IsHybridConfigured() &&
            !DisableShadowHybridForDiagnostics &&
            shadowHybridCost.UseHybrid;

        bool EnsureHybridClusterBuffers()
        {
            CollectRetiredHybridBuffers();
            if (!IsHybridConfigured() ||
                settings.visibleTriangleCompactShader == null ||
                batchedCulling == null || sceneVisibilityBackend == null ||
                kernelClearHybridTileHeads < 0 || kernelClearHybridTileWork < 0 ||
                kernelBuildHybridTileWork < 0 || kernelSoftwareRasterTiles < 0 ||
                kernelClassifyHybridClusters < 0 ||
                kernelCaptureHybridView < 0 ||
                kernelPrepareHybridDispatch < 0)
                return false;

            int requiredCapacity = Mathf.Max(1, batchedCulling.VirtualClusterCount);
            int geometryGeneration = sceneVisibilityBackend.GeometryGeneration;
            if (hybridHardwareClusterBuffer != null &&
                hybridSoftwareClusterBuffer != null &&
                hybridHardwareCountArgsBuffer != null &&
                hybridSoftwareCountArgsBuffer != null &&
                hybridClassifyDispatchArgsBuffer != null &&
                hybridSoftwareDispatchArgsBuffer != null &&
                hybridDependencyBuffer != null &&
                hybridViewConstantsBuffer != null &&
                hybridClusterCapacity >= requiredCapacity &&
                hybridGeometryGeneration == geometryGeneration)
                return true;

            RetireHybridBuffers();
            hybridClusterCapacity = requiredCapacity;
            hybridGeometryGeneration = geometryGeneration;
            hybridHardwareClusterBuffer = new ComputeBuffer(
                hybridClusterCapacity,
                sizeof(uint) * 3,
                ComputeBufferType.Append);
            hybridSoftwareClusterBuffer = new ComputeBuffer(
                hybridClusterCapacity,
                sizeof(uint) * 3,
                ComputeBufferType.Append);
            hybridHardwareCountArgsBuffer = new ComputeBuffer(
                4,
                sizeof(uint),
                ComputeBufferType.IndirectArguments);
            hybridSoftwareCountArgsBuffer = new ComputeBuffer(
                4,
                sizeof(uint),
                ComputeBufferType.IndirectArguments);
            hybridClassifyDispatchArgsBuffer = new ComputeBuffer(
                4,
                sizeof(uint),
                ComputeBufferType.IndirectArguments);
            hybridSoftwareDispatchArgsBuffer = new ComputeBuffer(
                4,
                sizeof(uint),
                ComputeBufferType.IndirectArguments);
            hybridDependencyBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                1,
                sizeof(uint));
            hybridViewConstantsBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                1,
                sizeof(float) * 16);
            hybridHardwareCountArgsBuffer.SetData(new uint[] { 0u, 1u, 1u, 0u });
            hybridSoftwareCountArgsBuffer.SetData(new uint[] { 0u, 1u, 1u, 0u });
            hybridClassifyDispatchArgsBuffer.SetData(new uint[] { 0u, 1u, 1u, 0u });
            hybridSoftwareDispatchArgsBuffer.SetData(new uint[] { 0u, 1u, 1u, 0u });
            return true;
        }

        bool EnsureHybridSoftwareTileBuffers(int width, int height)
        {
            CollectRetiredHybridBuffers();
            if (kernelClearHybridTileHeads < 0 || kernelClearHybridTileWork < 0 ||
                kernelBuildHybridTileWork < 0 || kernelSoftwareRasterTiles < 0)
                InitCompactKernels();
            if (kernelClearHybridTileHeads < 0 || kernelClearHybridTileWork < 0 ||
                kernelBuildHybridTileWork < 0 || kernelSoftwareRasterTiles < 0)
                return false;

            int tileCountX = Mathf.Max(1, (Mathf.Max(1, width) + kHybridSoftwareTileSize - 1) / kHybridSoftwareTileSize);
            int tileCountY = Mathf.Max(1, (Mathf.Max(1, height) + kHybridSoftwareTileSize - 1) / kHybridSoftwareTileSize);
            int required = Mathf.Max(1, tileCountX * tileCountY);
            int requiredFallback = Mathf.Max(1, batchedCulling != null ? batchedCulling.VirtualClusterCount : 1);
            if (hybridSoftwareTileHeadBuffer != null &&
                hybridSoftwareTileNodeBuffer != null &&
                hybridSoftwareTileNodeCounterBuffer != null &&
                hybridSoftwareFallbackFlagsBuffer != null &&
                hybridSoftwareTileListBuffer != null &&
                hybridSoftwareTileCountArgsBuffer != null &&
                hybridSoftwareTileDispatchArgsBuffer != null &&
                hybridSoftwareTileDrawArgsBuffer != null &&
                hybridSoftwareTileCapacity >= required &&
                hybridSoftwareFallbackCapacity >= requiredFallback)
                return true;

            RetireHybridSoftwareTileBuffers();
            // The linked work queue is resolution bounded instead of geometry
            // bounded. This keeps the production allocation proportional to the
            // number of pixels while a GPU overflow path returns complete clusters
            // to the indexed hardware queue without dropping geometry.
            hybridSoftwareTileCapacity = Mathf.NextPowerOfTwo(required);
            hybridSoftwareTileNodeCapacity = checked(
                hybridSoftwareTileCapacity * kHybridSoftwareTileNodesPerTile);
            hybridSoftwareFallbackCapacity = Mathf.NextPowerOfTwo(requiredFallback);
            hybridSoftwareTileHeadBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                hybridSoftwareTileCapacity,
                sizeof(uint));
            hybridSoftwareTileNodeBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                hybridSoftwareTileNodeCapacity,
                sizeof(uint) * 2);
            hybridSoftwareTileNodeCounterBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                4,
                sizeof(uint));
            hybridSoftwareFallbackFlagsBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                hybridSoftwareFallbackCapacity,
                sizeof(uint));
            hybridSoftwareTileListBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Append,
                hybridSoftwareTileCapacity,
                sizeof(uint));
            GraphicsBuffer.Target argsTarget =
                GraphicsBuffer.Target.IndirectArguments | GraphicsBuffer.Target.Structured;
            hybridSoftwareTileCountArgsBuffer = new GraphicsBuffer(argsTarget, 4, sizeof(uint));
            hybridSoftwareTileDispatchArgsBuffer = new GraphicsBuffer(argsTarget, 4, sizeof(uint));
            hybridSoftwareTileDrawArgsBuffer = new GraphicsBuffer(argsTarget, 4, sizeof(uint));
            hybridSoftwareTileCountArgsBuffer.SetData(new uint[] { 0u, 1u, 1u, 0u });
            hybridSoftwareTileDispatchArgsBuffer.SetData(new uint[] { 0u, 1u, 1u, 0u });
            hybridSoftwareTileDrawArgsBuffer.SetData(new uint[] { 6u, 0u, 0u, 0u });
            hybridSoftwareTileNodeCounterBuffer.SetData(new uint[] { 0u, 0u, 0u, 0u });
            return true;
        }

        void RetireHybridSoftwareTileBuffers()
        {
            if (hybridSoftwareTileHeadBuffer == null &&
                hybridSoftwareTileNodeBuffer == null &&
                hybridSoftwareTileNodeCounterBuffer == null &&
                hybridSoftwareFallbackFlagsBuffer == null &&
                hybridSoftwareTileListBuffer == null &&
                hybridSoftwareTileCountArgsBuffer == null &&
                hybridSoftwareTileDispatchArgsBuffer == null &&
                hybridSoftwareTileDrawArgsBuffer == null)
                return;

            retiredHybridTileBuffers.Add(new RetiredHybridTileBuffers
            {
                heads = hybridSoftwareTileHeadBuffer,
                nodes = hybridSoftwareTileNodeBuffer,
                nodeCounter = hybridSoftwareTileNodeCounterBuffer,
                fallbackFlags = hybridSoftwareFallbackFlagsBuffer,
                list = hybridSoftwareTileListBuffer,
                countArgs = hybridSoftwareTileCountArgsBuffer,
                dispatchArgs = hybridSoftwareTileDispatchArgsBuffer,
                drawArgs = hybridSoftwareTileDrawArgsBuffer,
                releaseFrame = Time.frameCount + 4
            });
            hybridSoftwareTileHeadBuffer = null;
            hybridSoftwareTileNodeBuffer = null;
            hybridSoftwareTileNodeCounterBuffer = null;
            hybridSoftwareFallbackFlagsBuffer = null;
            hybridSoftwareTileListBuffer = null;
            hybridSoftwareTileCountArgsBuffer = null;
            hybridSoftwareTileDispatchArgsBuffer = null;
            hybridSoftwareTileDrawArgsBuffer = null;
            hybridSoftwareTileCapacity = 0;
            hybridSoftwareTileNodeCapacity = 0;
            hybridSoftwareFallbackCapacity = 0;
        }

        void RetireHybridBuffers()
        {
            if (hybridHardwareClusterBuffer == null &&
                hybridSoftwareClusterBuffer == null &&
                hybridHardwareCountArgsBuffer == null &&
                hybridSoftwareCountArgsBuffer == null &&
                hybridClassifyDispatchArgsBuffer == null &&
                hybridSoftwareDispatchArgsBuffer == null &&
                hybridDependencyBuffer == null &&
                hybridViewConstantsBuffer == null)
                return;

            // RenderGraph executes after recording and Scene/Game cameras may rebuild the
            // shared geometry generation in the same editor frame. Releasing immediately
            // invalidated buffers captured by already-recorded hybrid kernels, producing
            // "Property ... is not set" and partially empty raster queues.
            retiredHybridBuffers.Add(new RetiredHybridBuffers
            {
                hardwareClusters = hybridHardwareClusterBuffer,
                softwareClusters = hybridSoftwareClusterBuffer,
                hardwareCountArgs = hybridHardwareCountArgsBuffer,
                softwareCountArgs = hybridSoftwareCountArgsBuffer,
                classifyDispatchArgs = hybridClassifyDispatchArgsBuffer,
                softwareDispatchArgs = hybridSoftwareDispatchArgsBuffer,
                dependency = hybridDependencyBuffer,
                viewConstants = hybridViewConstantsBuffer,
                releaseFrame = Time.frameCount + 4
            });
            ClearHybridBufferReferences();
        }

        void CollectRetiredHybridBuffers()
        {
            int frame = Time.frameCount;
            for (int i = retiredHybridBuffers.Count - 1; i >= 0; i--)
            {
                if (frame < retiredHybridBuffers[i].releaseFrame)
                    continue;
                retiredHybridBuffers[i].Release();
                retiredHybridBuffers.RemoveAt(i);
            }
            for (int i = retiredHybridTileBuffers.Count - 1; i >= 0; i--)
            {
                if (frame < retiredHybridTileBuffers[i].releaseFrame)
                    continue;
                retiredHybridTileBuffers[i].Release();
                retiredHybridTileBuffers.RemoveAt(i);
            }
        }

        void ReleaseHybridBuffers()
        {
            ReleaseHybridShadowTextures();
            hybridHardwareClusterBuffer?.Release();
            hybridSoftwareClusterBuffer?.Release();
            hybridHardwareCountArgsBuffer?.Release();
            hybridSoftwareCountArgsBuffer?.Release();
            hybridClassifyDispatchArgsBuffer?.Release();
            hybridSoftwareDispatchArgsBuffer?.Release();
            hybridDependencyBuffer?.Dispose();
            hybridViewConstantsBuffer?.Dispose();
            hybridTraversalDependencyBuffer?.Dispose();
            hybridSoftwareTileHeadBuffer?.Dispose();
            hybridSoftwareTileNodeBuffer?.Dispose();
            hybridSoftwareTileNodeCounterBuffer?.Dispose();
            hybridSoftwareFallbackFlagsBuffer?.Dispose();
            hybridSoftwareTileListBuffer?.Dispose();
            hybridSoftwareTileCountArgsBuffer?.Dispose();
            hybridSoftwareTileDispatchArgsBuffer?.Dispose();
            hybridSoftwareTileDrawArgsBuffer?.Dispose();
            hybridTraversalDependencyBuffer = null;
            for (int i = 0; i < retiredHybridBuffers.Count; i++)
                retiredHybridBuffers[i].Release();
            retiredHybridBuffers.Clear();
            for (int i = 0; i < retiredHybridTileBuffers.Count; i++)
                retiredHybridTileBuffers[i].Release();
            retiredHybridTileBuffers.Clear();
            hybridSoftwareTileHeadBuffer = null;
            hybridSoftwareTileNodeBuffer = null;
            hybridSoftwareTileNodeCounterBuffer = null;
            hybridSoftwareFallbackFlagsBuffer = null;
            hybridSoftwareTileListBuffer = null;
            hybridSoftwareTileCountArgsBuffer = null;
            hybridSoftwareTileDispatchArgsBuffer = null;
            hybridSoftwareTileDrawArgsBuffer = null;
            hybridSoftwareTileCapacity = 0;
            hybridSoftwareTileNodeCapacity = 0;
            hybridSoftwareFallbackCapacity = 0;
            ClearHybridBufferReferences();
        }

        void ClearHybridBufferReferences()
        {
            hybridHardwareClusterBuffer = null;
            hybridSoftwareClusterBuffer = null;
            hybridHardwareCountArgsBuffer = null;
            hybridSoftwareCountArgsBuffer = null;
            hybridClassifyDispatchArgsBuffer = null;
            hybridSoftwareDispatchArgsBuffer = null;
            hybridDependencyBuffer = null;
            hybridViewConstantsBuffer = null;
            hybridClusterCapacity = 0;
            hybridGeometryGeneration = -1;
            hybridQueueFrame = -1;
            hybridUsesTraversalBins = false;
        }

        ComputeBuffer ActiveHybridHardwareClusters => hybridUsesTraversalBins
            ? batchedCulling?.HardwareRasterClusterBuffer
            : hybridHardwareClusterBuffer;
        ComputeBuffer ActiveHybridSoftwareClusters => hybridUsesTraversalBins
            ? batchedCulling?.SoftwareRasterClusterBuffer
            : hybridSoftwareClusterBuffer;
        ComputeBuffer ActiveHybridHardwareCountArgs => hybridUsesTraversalBins
            ? batchedCulling?.HardwareRasterCountArgsBuffer
            : hybridHardwareCountArgsBuffer;
        ComputeBuffer ActiveHybridSoftwareCountArgs => hybridUsesTraversalBins
            ? batchedCulling?.SoftwareRasterCountArgsBuffer
            : hybridSoftwareCountArgsBuffer;
        ComputeBuffer ActiveHybridSoftwareDispatchArgs => hybridUsesTraversalBins
            ? batchedCulling?.SoftwareRasterDispatchArgsBuffer
            : hybridSoftwareDispatchArgsBuffer;
        GraphicsBuffer ActiveHybridDependency => hybridUsesTraversalBins
            ? hybridTraversalDependencyBuffer
            : hybridDependencyBuffer;

        void EnsureHybridTraversalDependency()
        {
            hybridTraversalDependencyBuffer ??= new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                1,
                sizeof(uint));
        }

        bool RecordHybridClassificationPass(
            RenderGraph renderGraph,
            Camera camera,
            Matrix4x4 worldToClip,
            int selectionKey,
            ComputeBuffer vertexData,
            ComputeBuffer indices,
            ComputeBuffer instanceLocalToWorld,
            ComputeBuffer triangleCluster,
            ComputeBuffer clusterBounds,
            ComputeBuffer clusterLongestEdge,
            int vertexStride,
            ProfilingSampler profilingSampler)
        {
            // RenderGraph recording happens before FirstCull executes. Querying
            // execution-time frame stamps here necessarily selects the fallback
            // classifier every frame. Admit the recorded producer contract and
            // let the RG dependency order the actual queue writes before SW/HW
            // consumers.
            // Traversal raster bins currently expose queue buffers but not the
            // complete classify/tile resource contract consumed below. Treating
            // that partial producer as a ready Hybrid frame caused unbound SRVs
            // on large instance-count scenes. Until traversal owns the complete
            // producer contract, always run the fully-bound post-cull classifier.
            hybridUsesTraversalBins = false;

            if (!EnsureHybridClusterBuffers() || camera == null ||
                batchedCulling.VisibleDrawClusterBuffer == null ||
                batchedCulling.VisibleDrawCountArgsBuffer == null ||
                vertexData == null || indices == null || instanceLocalToWorld == null ||
                triangleCluster == null || clusterBounds == null || clusterLongestEdge == null)
                return false;

            using (var builder = renderGraph.AddComputePass<HybridClassifyRgPassData>(
                       "Nanite/HybridClassifyClusters",
                       out var passData,
                       profilingSampler))
            {
                passData.camera = camera;
                passData.worldToClip = worldToClip;
                passData.inputClusters = batchedCulling.VisibleDrawClusterBuffer;
                passData.inputCountArgs = batchedCulling.VisibleDrawCountArgsBuffer;
                passData.hardwareClusters = hybridHardwareClusterBuffer;
                passData.softwareClusters = hybridSoftwareClusterBuffer;
                passData.hardwareCountArgs = hybridHardwareCountArgsBuffer;
                passData.softwareCountArgs = hybridSoftwareCountArgsBuffer;
                passData.classifyDispatchArgs = hybridClassifyDispatchArgsBuffer;
                passData.softwareDispatchArgs = hybridSoftwareDispatchArgsBuffer;
                passData.vertexData = vertexData;
                passData.indices = indices;
                passData.instanceLocalToWorld = instanceLocalToWorld;
                passData.triangleCluster = triangleCluster;
                passData.clusterBounds = clusterBounds;
                passData.clusterLongestEdge = clusterLongestEdge;
                passData.vertexStride = vertexStride;
                passData.selectionKey = selectionKey;
                passData.dependencyBuffer = hybridDependencyBuffer;
                passData.viewConstants = hybridViewConstantsBuffer;
                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);
                builder.UseBuffer(renderGraph.ImportBuffer(passData.dependencyBuffer), AccessFlags.Write);
                builder.UseBuffer(renderGraph.ImportBuffer(passData.viewConstants), AccessFlags.Read);
                builder.SetRenderFunc((HybridClassifyRgPassData data, ComputeGraphContext context) =>
                {
                    ComputeShader cs = settings.visibleTriangleCompactShader;
                    context.cmd.SetBufferCounterValue(data.hardwareClusters, 0u);
                    context.cmd.SetBufferCounterValue(data.softwareClusters, 0u);
                    PrepareHybridDispatch(
                        context.cmd,
                        cs,
                        kernelPrepareHybridDispatch,
                        data.inputCountArgs,
                        data.classifyDispatchArgs);
                    context.cmd.SetComputeBufferParam(cs, kernelClassifyHybridClusters, "_HybridInputClusters", data.inputClusters);
                    context.cmd.SetComputeBufferParam(cs, kernelClassifyHybridClusters, "_HybridInputCountArgs", data.inputCountArgs);
                    context.cmd.SetComputeBufferParam(cs, kernelClassifyHybridClusters, "_HybridHardwareClusters", data.hardwareClusters);
                    context.cmd.SetComputeBufferParam(cs, kernelClassifyHybridClusters, "_HybridSoftwareClusters", data.softwareClusters);
                    BindHybridGeometry(
                        context.cmd,
                        cs,
                        kernelClassifyHybridClusters,
                        data.worldToClip,
                        data.vertexData,
                        data.indices,
                        data.instanceLocalToWorld,
                        data.triangleCluster,
                        data.clusterBounds,
                        data.clusterLongestEdge,
                        data.vertexStride);
                    context.cmd.SetComputeIntParam(cs, "_HybridShadowMode", 0);
                    context.cmd.SetComputeIntParam(cs, "_HybridUseCapturedView", 0);
                    context.cmd.SetComputeBufferParam(
                        cs,
                        kernelClassifyHybridClusters,
                        "_HybridViewConstants",
                        data.viewConstants);
                    context.cmd.SetComputeFloatParam(
                        cs,
                        "_HybridSoftwareMaxEdgePixels",
                        Mathf.Max(1f, settings.hybridSoftwareMaxEdgePixels));
                    context.cmd.SetComputeIntParam(cs, "_HybridDispatchGroupsX", 65535);
                    context.cmd.DispatchCompute(cs, kernelClassifyHybridClusters, data.classifyDispatchArgs, 0u);
                    context.cmd.CopyCounterValue(data.hardwareClusters, data.hardwareCountArgs, 0u);
                    context.cmd.CopyCounterValue(data.softwareClusters, data.softwareCountArgs, 0u);
                    PrepareHybridDispatch(
                        context.cmd,
                        cs,
                        kernelPrepareHybridDispatch,
                        data.softwareCountArgs,
                        data.softwareDispatchArgs);
                    hybridQueueFrame = Time.frameCount;
                    hybridQueueSelectionKey = data.selectionKey;
                });
            }
            return true;
        }

        void RecordHybridSoftwareRasterPass(
            RenderGraph renderGraph,
            Camera camera,
            TextureHandle softwareDepth,
            TextureHandle softwareWinner,
            Matrix4x4 worldToClip,
            ComputeBuffer vertexData,
            ComputeBuffer indices,
            ComputeBuffer instanceLocalToWorld,
            ComputeBuffer triangleSubMesh,
            int vertexStride,
            int screenWidth,
            int screenHeight,
            ProfilingSampler profilingSampler)
        {
            using (var builder = renderGraph.AddComputePass<HybridSoftwareRasterRgPassData>(
                       "Nanite/HybridSoftwareSetup",
                       out var passData,
                       profilingSampler))
            {
                passData.softwareDepth = softwareDepth;
                passData.softwareWinner = softwareWinner;
                passData.hardwareClusters = ActiveHybridHardwareClusters;
                passData.hardwareCountArgs = ActiveHybridHardwareCountArgs;
                passData.softwareClusters = ActiveHybridSoftwareClusters;
                passData.softwareCountArgs = ActiveHybridSoftwareCountArgs;
                passData.softwareDispatchArgs = ActiveHybridSoftwareDispatchArgs;
                passData.vertexData = vertexData;
                passData.indices = indices;
                passData.instanceLocalToWorld = instanceLocalToWorld;
                passData.triangleSubMesh = triangleSubMesh;
                passData.worldToClip = worldToClip;
                passData.vertexStride = vertexStride;
                passData.screenWidth = screenWidth;
                passData.screenHeight = screenHeight;
                passData.tileCountX = Mathf.Max(1, (screenWidth + kHybridSoftwareTileSize - 1) / kHybridSoftwareTileSize);
                passData.tileCountY = Mathf.Max(1, (screenHeight + kHybridSoftwareTileSize - 1) / kHybridSoftwareTileSize);
                passData.tileCount = passData.tileCountX * passData.tileCountY;
                passData.tileHeads = hybridSoftwareTileHeadBuffer;
                passData.tileNodes = hybridSoftwareTileNodeBuffer;
                passData.tileNodeCounter = hybridSoftwareTileNodeCounterBuffer;
                passData.fallbackFlags = hybridSoftwareFallbackFlagsBuffer;
                passData.tileNodeCapacity = hybridSoftwareTileNodeCapacity;
                passData.tileList = hybridSoftwareTileListBuffer;
                passData.tileCountArgs = hybridSoftwareTileCountArgsBuffer;
                passData.tileDispatchArgs = hybridSoftwareTileDispatchArgsBuffer;
                passData.tileDrawArgs = hybridSoftwareTileDrawArgsBuffer;
                passData.dependencyBuffer = ActiveHybridDependency;
                passData.viewConstants = hybridViewConstantsBuffer;
                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);
                builder.EnableAsyncCompute(false);
                builder.UseBuffer(renderGraph.ImportBuffer(passData.tileHeads), AccessFlags.ReadWrite);
                builder.UseBuffer(renderGraph.ImportBuffer(passData.tileNodes), AccessFlags.ReadWrite);
                builder.UseBuffer(renderGraph.ImportBuffer(passData.tileNodeCounter), AccessFlags.ReadWrite);
                builder.UseBuffer(renderGraph.ImportBuffer(passData.fallbackFlags), AccessFlags.ReadWrite);
                builder.UseBuffer(renderGraph.ImportBuffer(passData.tileList), AccessFlags.ReadWrite);
                builder.UseBuffer(renderGraph.ImportBuffer(passData.tileCountArgs), AccessFlags.ReadWrite);
                builder.UseBuffer(renderGraph.ImportBuffer(passData.tileDispatchArgs), AccessFlags.ReadWrite);
                builder.UseBuffer(renderGraph.ImportBuffer(passData.tileDrawArgs), AccessFlags.ReadWrite);
                if (passData.dependencyBuffer != null)
                    builder.UseBuffer(renderGraph.ImportBuffer(passData.dependencyBuffer), AccessFlags.ReadWrite);
                builder.UseBuffer(renderGraph.ImportBuffer(passData.viewConstants), AccessFlags.ReadWrite);
                builder.SetRenderFunc((HybridSoftwareRasterRgPassData data, ComputeGraphContext context) =>
                {
                    ComputeShader cs = settings.visibleTriangleCompactShader;
                    context.cmd.SetComputeBufferParam(
                        cs,
                        kernelCaptureHybridView,
                        "_HybridViewConstantsWrite",
                        data.viewConstants);
                    context.cmd.DispatchCompute(cs, kernelCaptureHybridView, 1, 1, 1);
                    SetHybridScreenParams(context.cmd, cs, data.screenWidth, data.screenHeight);
                    SetHybridTileParams(context.cmd, cs, data.tileCountX, data.tileCountY, data.tileCount);
                    context.cmd.SetBufferCounterValue(data.tileList, 0u);
                    context.cmd.SetComputeBufferParam(
                        cs,
                        kernelClearHybridTileHeads,
                        "_HybridSoftwareTileHeads",
                        data.tileHeads);
                    context.cmd.DispatchCompute(
                        cs,
                        kernelClearHybridTileHeads,
                        Mathf.Max(1, (data.tileCount + 255) / 256),
                        1,
                        1);

                    context.cmd.SetComputeBufferParam(
                        cs,
                        kernelClearHybridTileWork,
                        "_HybridSoftwareTileNodeCounter",
                        data.tileNodeCounter);
                    context.cmd.DispatchCompute(cs, kernelClearHybridTileWork, 1, 1, 1);

                    BindHybridSoftwareGeometryKernel(context.cmd, cs, kernelBuildHybridTileWork, data);
                    context.cmd.SetComputeBufferParam(cs, kernelBuildHybridTileWork, "_HybridSoftwareTileHeads", data.tileHeads);
                    context.cmd.SetComputeBufferParam(cs, kernelBuildHybridTileWork, "_HybridSoftwareTileNodes", data.tileNodes);
                    context.cmd.SetComputeBufferParam(cs, kernelBuildHybridTileWork, "_HybridSoftwareTileNodeCounter", data.tileNodeCounter);
                    context.cmd.SetComputeBufferParam(cs, kernelBuildHybridTileWork, "_HybridSoftwareFallbackFlags", data.fallbackFlags);
                    context.cmd.SetComputeBufferParam(cs, kernelBuildHybridTileWork, "_HybridSoftwareTileList", data.tileList);
                    context.cmd.SetComputeBufferParam(cs, kernelBuildHybridTileWork, "_HybridHardwareClusters", data.hardwareClusters);
                    context.cmd.SetComputeIntParam(cs, "_HybridTileNodeCapacity", Mathf.Max(1, data.tileNodeCapacity));
                    context.cmd.SetComputeIntParam(cs, "_HybridDispatchGroupsX", 65535);
                    context.cmd.DispatchCompute(cs, kernelBuildHybridTileWork, data.softwareDispatchArgs, 0u);

                    // Overflow is correctness preserving: complete clusters are
                    // appended back to the HW queue and its indirect count is
                    // refreshed before the indexed build consumes it.
                    context.cmd.CopyCounterValue(data.hardwareClusters, data.hardwareCountArgs, 0u);
                    context.cmd.CopyCounterValue(data.tileList, data.tileCountArgs, 0u);
                    context.cmd.CopyCounterValue(data.tileList, data.tileDrawArgs, sizeof(uint));
                    PrepareHybridDispatch(
                        context.cmd,
                        cs,
                        kernelPrepareHybridDispatch,
                        data.tileCountArgs,
                        data.tileDispatchArgs);
                });
            }

            // Once setup has finalized the overflow-safe HW queue, software
            // tile resolve and indexed HW build/raster become independent
            // consumers of the same fence and can overlap on async compute and
            // graphics queues, matching the production Nanite scheduling model.
            using (var builder = renderGraph.AddComputePass<HybridSoftwareRasterRgPassData>(
                       "Nanite/HybridSoftwareRaster",
                       out var passData,
                       profilingSampler))
            {
                passData.softwareDepth = softwareDepth;
                passData.softwareWinner = softwareWinner;
                passData.softwareClusters = ActiveHybridSoftwareClusters;
                passData.softwareCountArgs = ActiveHybridSoftwareCountArgs;
                passData.vertexData = vertexData;
                passData.indices = indices;
                passData.instanceLocalToWorld = instanceLocalToWorld;
                passData.triangleSubMesh = triangleSubMesh;
                passData.worldToClip = worldToClip;
                passData.vertexStride = vertexStride;
                passData.screenWidth = screenWidth;
                passData.screenHeight = screenHeight;
                passData.tileCountX = Mathf.Max(1, (screenWidth + kHybridSoftwareTileSize - 1) / kHybridSoftwareTileSize);
                passData.tileCountY = Mathf.Max(1, (screenHeight + kHybridSoftwareTileSize - 1) / kHybridSoftwareTileSize);
                passData.tileCount = passData.tileCountX * passData.tileCountY;
                passData.tileHeads = hybridSoftwareTileHeadBuffer;
                passData.tileNodes = hybridSoftwareTileNodeBuffer;
                passData.tileNodeCounter = hybridSoftwareTileNodeCounterBuffer;
                passData.fallbackFlags = hybridSoftwareFallbackFlagsBuffer;
                passData.tileList = hybridSoftwareTileListBuffer;
                passData.tileCountArgs = hybridSoftwareTileCountArgsBuffer;
                passData.tileDispatchArgs = hybridSoftwareTileDispatchArgsBuffer;
                passData.dependencyBuffer = ActiveHybridDependency;
                passData.viewConstants = hybridViewConstantsBuffer;
                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);
                builder.EnableAsyncCompute(true);
                builder.UseTexture(softwareDepth, AccessFlags.Write);
                builder.UseTexture(softwareWinner, AccessFlags.Write);
                builder.UseBuffer(renderGraph.ImportBuffer(passData.tileHeads), AccessFlags.Read);
                builder.UseBuffer(renderGraph.ImportBuffer(passData.tileNodes), AccessFlags.Read);
                builder.UseBuffer(renderGraph.ImportBuffer(passData.tileNodeCounter), AccessFlags.ReadWrite);
                builder.UseBuffer(renderGraph.ImportBuffer(passData.fallbackFlags), AccessFlags.Read);
                builder.UseBuffer(renderGraph.ImportBuffer(passData.tileList), AccessFlags.Read);
                builder.UseBuffer(renderGraph.ImportBuffer(passData.tileCountArgs), AccessFlags.Read);
                builder.UseBuffer(renderGraph.ImportBuffer(passData.tileDispatchArgs), AccessFlags.Read);
                if (passData.dependencyBuffer != null)
                    builder.UseBuffer(renderGraph.ImportBuffer(passData.dependencyBuffer), AccessFlags.Read);
                builder.UseBuffer(renderGraph.ImportBuffer(passData.viewConstants), AccessFlags.Read);
                builder.SetRenderFunc((HybridSoftwareRasterRgPassData data, ComputeGraphContext context) =>
                {
                    ComputeShader cs = settings.visibleTriangleCompactShader;
                    BindHybridSoftwareRasterKernel(context.cmd, cs, kernelSoftwareRasterTiles, data);
                    context.cmd.SetComputeBufferParam(cs, kernelSoftwareRasterTiles, "_HybridSoftwareTileHeads", data.tileHeads);
                    context.cmd.SetComputeBufferParam(cs, kernelSoftwareRasterTiles, "_HybridSoftwareTileNodes", data.tileNodes);
                    context.cmd.SetComputeBufferParam(cs, kernelSoftwareRasterTiles, "_HybridSoftwareTileNodeCounter", data.tileNodeCounter);
                    context.cmd.SetComputeBufferParam(cs, kernelSoftwareRasterTiles, "_HybridSoftwareFallbackFlags", data.fallbackFlags);
                    context.cmd.SetComputeBufferParam(cs, kernelSoftwareRasterTiles, "_HybridSoftwareTileListRead", data.tileList);
                    context.cmd.SetComputeBufferParam(cs, kernelSoftwareRasterTiles, "_HybridSoftwareTileCountArgs", data.tileCountArgs);
                    context.cmd.SetComputeIntParam(cs, "_HybridDispatchGroupsX", 65535);
                    bool audit = ForceHybridForDiagnostics && !hybridSoftwareAuditComplete;
                    context.cmd.SetComputeIntParam(cs, "_HybridTelemetryEnabled", audit ? 1 : 0);
                    context.cmd.DispatchCompute(cs, kernelSoftwareRasterTiles, data.tileDispatchArgs, 0u);
                });
            }

            if (ForceHybridForDiagnostics &&
                !hybridSoftwareAuditPending &&
                !hybridSoftwareAuditComplete)
            {
                using (var builder = renderGraph.AddUnsafePass<HybridAuditRgPassData>(
                           "Nanite/HybridSoftwareAudit",
                           out var passData,
                           profilingSampler))
                {
                    passData.stats = hybridSoftwareTileNodeCounterBuffer;
                    builder.AllowPassCulling(false);
                    builder.UseBuffer(renderGraph.ImportBuffer(passData.stats), AccessFlags.Read);
                    builder.SetRenderFunc((HybridAuditRgPassData data, UnsafeGraphContext context) =>
                    {
                        hybridSoftwareAuditPending = true;
                        context.cmd.RequestAsyncReadback(data.stats, request =>
                        {
                            hybridSoftwareAuditPending = false;
                            if (request.hasError || request.GetData<uint>().Length < 4)
                            {
                                Debug.LogWarning("[Nanite][HybridAudit] asynchronous tile/winner readback failed.");
                                return;
                            }
                            var values = request.GetData<uint>();
                            hybridSoftwareAuditComplete = values[0] != 0u || values[3] != 0u;
                            Debug.Log(
                                $"[Nanite][HybridAudit] tileNodes={values[0]}, " +
                                $"winnerPixels={values[1]}, maxWinner={values[2]}, " +
                                $"rasterTiles={values[3]}, " +
                                $"settled={hybridSoftwareAuditComplete}.");
                        });
                    });
                }
            }
        }

        static void SetHybridScreenParams(
            IComputeCommandBuffer cmd,
            ComputeShader cs,
            int screenWidth,
            int screenHeight)
        {
            cmd.SetComputeIntParam(cs, "_HybridScreenWidth", Mathf.Max(1, screenWidth));
            cmd.SetComputeIntParam(cs, "_HybridScreenHeight", Mathf.Max(1, screenHeight));
            cmd.SetComputeIntParam(cs, "_HybridReversedZ", SystemInfo.usesReversedZBuffer ? 1 : 0);
            cmd.SetComputeIntParam(cs, "_HybridDepthOnly", 0);
            cmd.SetComputeIntParam(cs, "_HybridShadowMode", 0);
        }

        static void SetHybridTileParams(
            IComputeCommandBuffer cmd,
            ComputeShader cs,
            int tileCountX,
            int tileCountY,
            int tileCount)
        {
            cmd.SetComputeIntParam(cs, "_HybridTileCountX", Mathf.Max(1, tileCountX));
            cmd.SetComputeIntParam(cs, "_HybridTileCountY", Mathf.Max(1, tileCountY));
            cmd.SetComputeIntParam(cs, "_HybridTileCount", Mathf.Max(1, tileCount));
            cmd.SetComputeIntParam(cs, "_HybridTileSize", kHybridSoftwareTileSize);
        }

        static void PrepareHybridDispatch(
            IComputeCommandBuffer cmd,
            ComputeShader cs,
            int kernel,
            ComputeBuffer countArgs,
            ComputeBuffer dispatchArgs)
        {
            cmd.SetComputeBufferParam(cs, kernel, "_HybridPrepareCountArgs", countArgs);
            cmd.SetComputeBufferParam(cs, kernel, "_HybridDispatchArgs", dispatchArgs);
            cmd.SetComputeIntParam(cs, "_HybridDispatchGroupsX", 65535);
            cmd.DispatchCompute(cs, kernel, 1, 1, 1);
        }

        static void PrepareHybridDispatch(
            IComputeCommandBuffer cmd,
            ComputeShader cs,
            int kernel,
            GraphicsBuffer countArgs,
            GraphicsBuffer dispatchArgs)
        {
            cmd.SetComputeBufferParam(cs, kernel, "_HybridPrepareCountArgs", countArgs);
            cmd.SetComputeBufferParam(cs, kernel, "_HybridDispatchArgs", dispatchArgs);
            cmd.SetComputeIntParam(cs, "_HybridDispatchGroupsX", 65535);
            cmd.DispatchCompute(cs, kernel, 1, 1, 1);
        }

        void BindHybridGeometry(
            IComputeCommandBuffer cmd,
            ComputeShader cs,
            int kernel,
            Camera camera,
            ComputeBuffer vertexData,
            ComputeBuffer indices,
            ComputeBuffer instanceLocalToWorld,
            ComputeBuffer triangleCluster,
            ComputeBuffer clusterBounds,
            ComputeBuffer clusterLongestEdge,
            int vertexStride)
        {
            Matrix4x4 worldToClip = GL.GetGPUProjectionMatrix(camera.projectionMatrix, true) * camera.worldToCameraMatrix;
            BindHybridGeometry(
                cmd,
                cs,
                kernel,
                worldToClip,
                vertexData,
                indices,
                instanceLocalToWorld,
                triangleCluster,
                clusterBounds,
                clusterLongestEdge,
                vertexStride);
        }

        void BindHybridGeometry(
            IComputeCommandBuffer cmd,
            ComputeShader cs,
            int kernel,
            Matrix4x4 worldToClip,
            ComputeBuffer vertexData,
            ComputeBuffer indices,
            ComputeBuffer instanceLocalToWorld,
            ComputeBuffer triangleCluster,
            ComputeBuffer clusterBounds,
            ComputeBuffer clusterLongestEdge,
            int vertexStride)
        {
            cmd.SetComputeBufferParam(cs, kernel, "_HybridVertexData", vertexData);
            cmd.SetComputeBufferParam(cs, kernel, "_HybridIndices", indices);
            cmd.SetComputeBufferParam(cs, kernel, "_HybridInstanceLocalToWorld", instanceLocalToWorld);
            cmd.SetComputeBufferParam(cs, kernel, "_HybridTriangleCluster", triangleCluster);
            cmd.SetComputeBufferParam(cs, kernel, "_HybridClusterBounds", clusterBounds);
            cmd.SetComputeBufferParam(cs, kernel, "_HybridClusterLongestEdge", clusterLongestEdge);
            cmd.SetComputeMatrixParam(cs, "_HybridWorldToClip", worldToClip);
            cmd.SetComputeIntParam(cs, "_HybridVertexStride", Mathf.Max(3, vertexStride));
            BindHybridPackedGeometry(cmd, cs, kernel);
        }

        void BindHybridSoftwareRasterKernel(
            IComputeCommandBuffer cmd,
            ComputeShader cs,
            int kernel,
            HybridSoftwareRasterRgPassData data)
        {
            BindHybridSoftwareGeometryKernel(cmd, cs, kernel, data);
            cmd.SetComputeTextureParam(cs, kernel, "_HybridSoftwareDepth", data.softwareDepth);
            cmd.SetComputeTextureParam(cs, kernel, "_HybridSoftwareWinner", data.softwareWinner);
        }

        void BindHybridSoftwareGeometryKernel(
            IComputeCommandBuffer cmd,
            ComputeShader cs,
            int kernel,
            HybridSoftwareRasterRgPassData data)
        {
            cmd.SetComputeBufferParam(cs, kernel, "_HybridSoftwareClustersRead", data.softwareClusters);
            cmd.SetComputeBufferParam(cs, kernel, "_HybridSoftwareCountArgs", data.softwareCountArgs);
            cmd.SetComputeBufferParam(cs, kernel, "_HybridVertexData", data.vertexData);
            cmd.SetComputeBufferParam(cs, kernel, "_HybridIndices", data.indices);
            cmd.SetComputeBufferParam(cs, kernel, "_HybridInstanceLocalToWorld", data.instanceLocalToWorld);
            cmd.SetComputeBufferParam(cs, kernel, "_HybridTriangleSubMesh", data.triangleSubMesh);
            cmd.SetComputeBufferParam(cs, kernel, "_HybridViewConstants", data.viewConstants);
            cmd.SetComputeIntParam(cs, "_HybridUseCapturedView", 1);
            cmd.SetComputeIntParam(
                cs,
                "_HybridMaxSubMeshCount",
                Mathf.Max(1, sceneVisibilityBackend.MaxSubMeshCount));
            cmd.SetComputeMatrixParam(cs, "_HybridWorldToClip", data.worldToClip);
            cmd.SetComputeIntParam(cs, "_HybridVertexStride", Mathf.Max(3, data.vertexStride));
            BindHybridPackedGeometry(cmd, cs, kernel);
            SetHybridScreenParams(cmd, cs, data.screenWidth, data.screenHeight);
            SetHybridTileParams(cmd, cs, data.tileCountX, data.tileCountY, data.tileCount);
        }

        void BindHybridPackedGeometry(
            IComputeCommandBuffer cmd,
            ComputeShader cs,
            int kernel)
        {
            bool usePacked = CanBindPackedPageGeometry();
            cmd.SetComputeIntParam(cs, "_UsePackedPageGeometry", usePacked ? 1 : 0);
            cmd.SetComputeBufferParam(
                cs,
                kernel,
                "_TrianglePageRefs",
                sceneVisibilityBackend.TrianglePageRefBuffer);
            if (usePacked)
            {
                cmd.SetComputeBufferParam(
                    cs,
                    kernel,
                    "_NaniteResidentPageTable",
                    sceneVisibilityBackend.ResidentPageTableBuffer);
                cmd.SetComputeBufferParam(
                    cs,
                    kernel,
                    "_NaniteResidentVertices",
                    sceneVisibilityBackend.ResidentVertexBuffer);
                cmd.SetComputeBufferParam(
                    cs,
                    kernel,
                    "_NaniteResidentIndices",
                    sceneVisibilityBackend.ResidentIndexBuffer);
            }
            else
            {
                // Unity validates every resource referenced by a compute kernel even
                // when the runtime branch selects compatibility geometry. Keep typed,
                // one-element fallbacks bound so Hybrid never inherits stale Page SRVs.
                cmd.SetComputeBufferParam(
                    cs,
                    kernel,
                    "_NaniteResidentPageTable",
                    sceneVisibilityBackend.FallbackResidentPageTableBuffer);
                cmd.SetComputeBufferParam(
                    cs,
                    kernel,
                    "_NaniteResidentVertices",
                    sceneVisibilityBackend.FallbackResidentVertexBuffer);
                cmd.SetComputeBufferParam(
                    cs,
                    kernel,
                    "_NaniteResidentIndices",
                    sceneVisibilityBackend.FallbackResidentIndexBuffer);
            }
        }

        void RecordHybridMergePass(
            RenderGraph renderGraph,
            Material rasterMaterial,
            TextureHandle vbuffer,
            TextureHandle rasterDepth,
            TextureHandle softwareDepth,
            TextureHandle softwareWinner,
            int screenWidth,
            int screenHeight,
            ProfilingSampler profilingSampler)
        {
            using (var builder = renderGraph.AddRasterRenderPass<HybridMergeRgPassData>(
                       "Nanite/HybridSoftwareMerge",
                       out var passData,
                       profilingSampler))
            {
                passData.material = rasterMaterial;
                passData.softwareDepth = softwareDepth;
                passData.softwareWinner = softwareWinner;
                passData.softwareClusters = ActiveHybridSoftwareClusters;
                passData.tileList = hybridSoftwareTileListBuffer;
                passData.tileDrawArgs = hybridSoftwareTileDrawArgsBuffer;
                passData.screenWidth = screenWidth;
                passData.screenHeight = screenHeight;
                passData.tileCountX = Mathf.Max(1, (screenWidth + kHybridSoftwareTileSize - 1) / kHybridSoftwareTileSize);
                passData.triangleCount = sceneVisibilityBackend.TriangleCount;
                passData.instanceCount = sceneVisibilityBackend.InstanceCount;
                passData.maxSubMeshCount = sceneVisibilityBackend.MaxSubMeshCount;
                passData.useCompactVBuffer = CanUseCompactFormalVBuffer();
                passData.useFloat2VBuffer =
                    !passData.useCompactVBuffer && CanUsePortableFloat2FormalVBuffer();
                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);
                builder.UseTexture(softwareDepth, AccessFlags.Read);
                builder.UseTexture(softwareWinner, AccessFlags.Read);
                builder.UseBuffer(renderGraph.ImportBuffer(passData.tileList), AccessFlags.Read);
                builder.UseBuffer(renderGraph.ImportBuffer(passData.tileDrawArgs), AccessFlags.Read);
                builder.SetRenderAttachment(vbuffer, 0, AccessFlags.ReadWrite);
                if (rasterDepth.IsValid())
                    builder.SetRenderAttachmentDepth(rasterDepth, AccessFlags.ReadWrite);
                builder.SetRenderFunc((HybridMergeRgPassData data, RasterGraphContext context) =>
                {
                    // The merge is a separate ShaderLab pass and may execute
                    // after another view changed this shared Material's local
                    // keyword state. Reassert the exact VBuffer ABI here; using
                    // the float4 variant against RG32F stores depth/instance in
                    // place of instance/triangle and resolves the whole SW
                    // region through the wrong material family.
                    CoreUtils.SetKeyword(
                        data.material,
                        kCompactVBufferKeyword,
                        data.useCompactVBuffer);
                    CoreUtils.SetKeyword(
                        data.material,
                        kFloat2VBufferKeyword,
                        data.useFloat2VBuffer);
                    context.cmd.SetGlobalFloat(ShaderIds.HasTriangleSubMesh, 1f);
                    context.cmd.SetGlobalFloat(
                        ShaderIds.UseNormalizedIds,
                        ComputeFormalVBufferIdMode(data.triangleCount));
                    context.cmd.SetGlobalFloat(ShaderIds.TriangleCount, data.triangleCount);
                    context.cmd.SetGlobalFloat(ShaderIds.InstanceCount, data.instanceCount);
                    context.cmd.SetGlobalFloat(ShaderIds.MaxSubMeshCount, data.maxSubMeshCount);
                    context.cmd.SetGlobalTexture(ShaderIds.NaniteSoftwareDepth, data.softwareDepth);
                    context.cmd.SetGlobalTexture(ShaderIds.NaniteSoftwareWinner, data.softwareWinner);
                    context.cmd.SetGlobalBuffer(ShaderIds.NaniteSoftwareClusters, data.softwareClusters);
                    context.cmd.SetGlobalBuffer(ShaderIds.NaniteSoftwareTileList, data.tileList);
                    context.cmd.SetGlobalInt(ShaderIds.NaniteSoftwareScreenWidth, data.screenWidth);
                    context.cmd.SetGlobalInt(ShaderIds.NaniteSoftwareScreenHeight, data.screenHeight);
                    context.cmd.SetGlobalInt(ShaderIds.NaniteSoftwareTileCountX, data.tileCountX);
                    context.cmd.SetGlobalInt(ShaderIds.NaniteSoftwareTileSize, kHybridSoftwareTileSize);
                    context.cmd.SetGlobalFloat(
                        ShaderIds.NaniteHybridFloat2VBuffer,
                        data.useFloat2VBuffer ? 1f : 0f);
                    context.cmd.DrawProcedural(
                        Matrix4x4.identity,
                        data.material,
                        3,
                        MeshTopology.Triangles,
                        3,
                        1);
                });
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
            bool clearColorHint,
            bool hybridHardwareQueue = false,
            bool preferAppendOnly = false)
        {
            _ = clearColorHint;
            // Exact packet admission now dispatches the complete cluster queue
            // once and moves whole clusters that exceed the packet arena into an
            // explicit overflow index queue. Replaying prefix chunks would only
            // rebuild the same packets and reintroduce the old admission cliff.
            int chunkCount = 1;
            for (int chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
            {
                int clusterOffset = 0;
                bool finalChunk = chunkIndex == chunkCount - 1;
                RecordIndexedDrawBuildPass(
                    renderGraph,
                    camera,
                    compactSelectionKey,
                    profilingSampler,
                    clusterOffset,
                    allowProceduralFallback: finalChunk,
                    drawClusters: hybridHardwareQueue ? ActiveHybridHardwareClusters : null,
                    drawCountArgs: hybridHardwareQueue ? ActiveHybridHardwareCountArgs : null,
                    dependencyBuffer: hybridHardwareQueue ? ActiveHybridDependency : null);
                string chunkPassName = chunkCount > 1 ? $"{passName}/Chunk{chunkIndex}" : passName;
                using (var builder = renderGraph.AddRasterRenderPass<FormalRasterRgPassData>(chunkPassName, out var passData, profilingSampler))
                {
                    passData.material = rasterMaterial;
                    passData.camera = camera;
                    passData.screenWidth = formalScreenWidth;
                    passData.screenHeight = formalScreenHeight;
                    passData.compactSelectionKey = compactSelectionKey;
                    passData.compactClusterOffset = clusterOffset;
                    passData.hybridHardwareQueue = hybridHardwareQueue;
                    passData.drawProceduralFallback = finalChunk;
                    passData.preferAppendOnly = preferAppendOnly && chunkIndex == 0;
                    builder.AllowPassCulling(false);
                    builder.AllowGlobalStateModification(true);
                    if (sceneVisibilityBackend != null)
                    {
                        if (sceneVisibilityBackend.IndexedTrianglePacketBuffer != null)
                        {
                            builder.UseBuffer(
                                renderGraph.ImportBuffer(sceneVisibilityBackend.IndexedTrianglePacketBuffer),
                                AccessFlags.Read);
                        }
                        if (sceneVisibilityBackend.IndexedDrawArgsBuffer != null)
                        {
                            builder.UseBuffer(
                                renderGraph.ImportBuffer(sceneVisibilityBackend.IndexedDrawArgsBuffer),
                                AccessFlags.Read);
                        }
                        if (sceneVisibilityBackend.IndexedAppendDrawArgsBuffer != null)
                        {
                            builder.UseBuffer(
                                renderGraph.ImportBuffer(sceneVisibilityBackend.IndexedAppendDrawArgsBuffer),
                                AccessFlags.Read);
                        }
                        if (sceneVisibilityBackend.IndexedFallbackDrawArgsBuffer != null)
                        {
                            builder.UseBuffer(
                                renderGraph.ImportBuffer(sceneVisibilityBackend.IndexedFallbackDrawArgsBuffer),
                                AccessFlags.Read);
                        }
                        if (sceneVisibilityBackend.IndexedOverflowClusterBuffer != null)
                        {
                            builder.UseBuffer(
                                renderGraph.ImportBuffer(sceneVisibilityBackend.IndexedOverflowClusterBuffer),
                                AccessFlags.Read);
                        }
                        if (sceneVisibilityBackend.IndexedCameraPacketSliceBuffer != null)
                        {
                            builder.UseBuffer(
                                renderGraph.ImportBuffer(sceneVisibilityBackend.IndexedCameraPacketSliceBuffer),
                                AccessFlags.Read);
                        }
                    }
                    builder.SetRenderAttachment(
                        vbuffer,
                        0,
                        (chunkIndex > 0 || preferAppendOnly) ? AccessFlags.ReadWrite : AccessFlags.Write);
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
                            data.compactSelectionKey,
                            data.compactClusterOffset,
                            data.hybridHardwareQueue,
                            data.drawProceduralFallback,
                            data.preferAppendOnly);
                    });
                }
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
            int compactSelectionKey = kCompactSelectionMerged,
            int compactClusterOffset = 0,
            bool hybridHardwareQueue = false,
            bool drawProceduralFallback = true,
            bool preferAppendOnly = false,
            bool pass1Prefill = false)
        {
            if (cmd == null || material == null || camera == null)
                return;
            if (hybridHardwareQueue &&
                !loggedHybridRasterWarning)
            {
                loggedHybridRasterWarning = true;
                string binSource = hybridUsesTraversalBins
                    ? "traversal-produced raster bins"
                    : "post-cull compatibility classification";
                Debug.Log(
                    $"[Nanite][RF] Hybrid cluster raster active: {binSource} -> " +
                    "single-setup linked tile queue -> async tile/wave software raster || " +
                    $"indexed hardware raster -> visibility merge; tileNodes={hybridSoftwareTileNodeCapacity}, " +
                    "overflow=GPU whole-cluster HW fallback.");
            }
            if (!TryPrepareSceneVisibility(camera))
                return;
            if (!HasValidCompactForSelection(compactSelectionKey, camera))
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
            BindCompactDrawState(cmd, compactSelectionKey, camera);
            if (hybridHardwareQueue &&
                ActiveHybridHardwareClusters != null)
            {
                cmd.SetGlobalFloat(ShaderIds.UseDirectVisibleDrawQueue, 1f);
                cmd.SetGlobalBuffer(ShaderIds.CompactedDrawClusters, ActiveHybridHardwareClusters);
            }
            if (!pass1Prefill && settings.logStats &&
                !loggedFormalRasterDiagnostics &&
                lastCompactUsedDirectQueue &&
                sceneVisibilityBackend.AllPagesResident)
            {
                bool indexedDiagnostic = HasValidIndexedDraw(compactSelectionKey, camera);
                LogFormalRasterDiagnosticsOnce(
                    indexedDiagnostic
                        ? sceneVisibilityBackend.IndexedDrawArgsBuffer
                        : drawArgsBuffer,
                    indexedDiagnostic,
                    compactClusterOffset);
            }
            bool useCompactVBuffer = CanUseCompactFormalVBuffer();
            CoreUtils.SetKeyword(material, kCompactVBufferKeyword, useCompactVBuffer);
            CoreUtils.SetKeyword(
                material,
                kFloat2VBufferKeyword,
                !useCompactVBuffer && CanUsePortableFloat2FormalVBuffer());
            const int kFormalRasterPass = 1;
            var indexedMpb = GetOrCreateVBufferPropertyBlock();
            indexedMpb.Clear();
            if (!TryDrawIndexedCameraQueue(
                    cmd,
                    material,
                    kFormalRasterPass,
                    compactSelectionKey,
                    camera,
                    indexedMpb,
                    compactClusterOffset,
                    drawProceduralFallback,
                    preferAppendOnly))
            {
                // A multi-chunk frame is only valid when every chunk was built.
                // If indexed submission becomes unavailable unexpectedly, draw
                // the complete compatibility queue once instead of replaying it
                // once per recorded chunk.
                if (compactClusterOffset != 0)
                    return;
                indexedMpb.SetInt(ShaderIds.UseIndexedClusterRaster, 0);
                indexedMpb.SetFloat(ShaderIds.CompactedClusterOffset, 0.0f);
                cmd.DrawProceduralIndirect(
                    Matrix4x4.identity,
                    material,
                    kFormalRasterPass,
                    MeshTopology.Triangles,
                    drawArgsBuffer,
                    0,
                    indexedMpb);
            }

            if (pass1Prefill && !loggedPass1VBufferFusion)
            {
                loggedPass1VBufferFusion = true;
                Debug.Log(
                    "[Nanite][Pass1VBuffer] WriteDepth now emits depth + formal visibility IDs; " +
                    "Formal raster will submit only Pass2 recovery packets.");
            }
            if (!pass1Prefill && !loggedFormalRasterStats)
            {
                loggedFormalRasterStats = true;
                string sel = compactSelectionKey == kCompactSelectionPass2
                    ? "pass2"
                    : (compactSelectionKey == kCompactSelectionFirst ? "pass1" : "merged");
                Debug.Log(
                    $"[Nanite][RF] Formal VBuffer raster: geometryTriangles={sceneVisibilityBackend.TriangleCount}, " +
                    $"sceneInstances={sceneVisibilityBackend.InstanceCount}, " +
                    $"submit={(HasValidIndexedDraw(compactSelectionKey, camera) ? "exactTrianglePacketIndirect" : (lastCompactUsedDirectQueue ? "cullQueueIndirect" : "compactIndirect"))}, selection={sel}, " +
                    $"format={FormalVBufferFormatName(useCompactVBuffer)}, " +
                    $"geometrySource={(sceneVisibilityBackend.UsePackedPageRaster ? "residentCache" : "compatDecoded")}, " +
                    $"residentAddress={(sceneVisibilityBackend.UsePackedPageRaster ? "pageId+localIndex" : "n/a")}, " +
                    $"clusterQueueCapacity={sceneVisibilityBackend.IndexedDrawClusterCapacity}," +
                    $"packetCapacity={sceneVisibilityBackend.IndexedTrianglePacketCapacity}," +
                    $"packet=uint32(instanceBits:{sceneVisibilityBackend.IndexedPacketInstanceBits})," +
                    $"pass2Build={(lastIndexedDrawAppendedPass2 ? "append-only" : "full")}," +
                    $"bufferMiB:{sceneVisibilityBackend.IndexedDrawBufferBytes / (1024f * 1024f):F1}, " +
                    $"bevyDual={settings.enableBevyFormalDualRaster}, pass=VBufferFormal");
            }
        }

        void LogFormalRasterDiagnosticsOnce(
            GraphicsBuffer drawArgsBuffer,
            bool exactPacketDraw,
            int compactClusterOffset)
        {
            loggedFormalRasterDiagnostics = true;
            try
            {
                var args = new uint[4];
                drawArgsBuffer.GetData(args);

                int queueCount = batchedCulling != null &&
                                 batchedCulling.VisibleDrawCountArgsBuffer != null
                    ? ReadFirstUint(batchedCulling.VisibleDrawCountArgsBuffer)
                    : 0;
                int queueStart = Mathf.Clamp(compactClusterOffset, 0, Mathf.Max(0, queueCount));
                int availableClusters = Mathf.Max(0, queueCount - queueStart);
                int queueReadCount = Mathf.Min(
                    availableClusters,
                    exactPacketDraw
                        ? sceneVisibilityBackend.IndexedDrawClusterCapacity
                        : batchedCulling.VisibleDrawClusterBuffer.count);
                var draws = new RasterDiagDrawCluster[queueReadCount];
                if (queueReadCount > 0)
                    batchedCulling.VisibleDrawClusterBuffer.GetData(
                        draws,
                        0,
                        queueStart,
                        queueReadCount);

                ComputeBuffer triangleRefBuffer = sceneVisibilityBackend.TrianglePageRefBuffer;
                var triangleRefs = new RasterDiagTrianglePageRef[triangleRefBuffer.count];
                triangleRefBuffer.GetData(triangleRefs);

                ComputeBuffer pageTableBuffer = sceneVisibilityBackend.ResidentPageTableBuffer;
                var pageTable = new NaniteGpuPagePool.GpuResidentPageEntry[pageTableBuffer.count];
                pageTableBuffer.GetData(pageTable);

                int referencedTriangles = 0;
                int validTriangles = 0;
                int invalidTriangleRange = 0;
                int invalidPageId = 0;
                int nonResidentPage = 0;
                int invalidLocalIndex = 0;
                for (int drawIndex = 0; drawIndex < draws.Length; drawIndex++)
                {
                    RasterDiagDrawCluster draw = draws[drawIndex];
                    ulong triangleEnd = (ulong)draw.firstTriangle + draw.triangleCount;
                    if (draw.firstTriangle >= (uint)triangleRefs.Length ||
                        triangleEnd > (ulong)triangleRefs.Length)
                    {
                        invalidTriangleRange += checked((int)draw.triangleCount);
                        continue;
                    }

                    for (uint triangleOffset = 0; triangleOffset < draw.triangleCount; triangleOffset++)
                    {
                        referencedTriangles++;
                        RasterDiagTrianglePageRef triangleRef =
                            triangleRefs[draw.firstTriangle + triangleOffset];
                        if (triangleRef.pageId >= (uint)pageTable.Length)
                        {
                            invalidPageId++;
                            continue;
                        }

                        NaniteGpuPagePool.GpuResidentPageEntry page = pageTable[triangleRef.pageId];
                        if ((page.flags & 1u) == 0u ||
                            page.indexBase == NaniteGpuPagePool.InvalidSlot)
                        {
                            nonResidentPage++;
                            continue;
                        }
                        if (page.indexCount < 3u ||
                            triangleRef.localIndexOffset > page.indexCount - 3u)
                        {
                            invalidLocalIndex++;
                            continue;
                        }
                        validTriangles++;
                    }
                }

                uint expectedVertices = exactPacketDraw
                    ? checked((uint)referencedTriangles * 3u)
                    : checked(
                        (uint)queueReadCount *
                        (uint)Mathf.Max(1, sceneVisibilityBackend.CompactedClusterTriangleSlots) *
                        3u);
                Debug.Log(
                    $"[Nanite][RasterDiag] settled {(exactPacketDraw ? "exact-packet" : "direct")} raster input: " +
                    $"argsVertices={args[0]}, expectedVertices={expectedVertices}, " +
                    $"drawInstances={args[1]}, queuedClusters={queueCount}, slice={queueStart}+{queueReadCount}, " +
                    $"triangleRefsValid={validTriangles}/{referencedTriangles}, " +
                    $"invalidTriangleRange={invalidTriangleRange}, invalidPageId={invalidPageId}, " +
                    $"nonResidentPage={nonResidentPage}, invalidLocalIndex={invalidLocalIndex}.");
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    $"[Nanite][RasterDiag] one-shot draw/address readback failed: {exception.Message}");
            }
        }

        static int ReadFirstUint(ComputeBuffer buffer)
        {
            var value = new uint[1];
            buffer.GetData(value, 0, 0, 1);
            return checked((int)value[0]);
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
            if (tileMaterialMaskBuffer == null ||
                tileMaterialBinListBuffer == null ||
                tileIndirectArgsBuffer == null ||
                tileCount <= 0)
                return;

            cmd.SetComputeBufferParam(
                settings.materialTileClassifyShader,
                kernelClearMaterialTileBins,
                ShaderIds.TileMaterialBinArgs,
                tileIndirectArgsBuffer);
            cmd.DispatchCompute(settings.materialTileClassifyShader, kernelClearMaterialTileBins, 1, 1, 1);

            cmd.SetComputeIntParam(settings.materialTileClassifyShader, ShaderIds.MaxSubMeshCount, sceneVisibilityBackend.MaxSubMeshCount);
            cmd.SetComputeIntParam(settings.materialTileClassifyShader, ShaderIds.TriangleCount, sceneVisibilityBackend.TriangleCount);
            cmd.SetComputeIntParam(settings.materialTileClassifyShader, ShaderIds.InstanceCount, sceneVisibilityBackend.InstanceCount);
            cmd.SetComputeIntParam(settings.materialTileClassifyShader, ShaderIds.MaterialCount, sceneVisibilityBackend.MaterialCount);
            cmd.SetComputeIntParam(settings.materialTileClassifyShader, ShaderIds.TileCountX, tileCountX);
            cmd.SetComputeIntParam(settings.materialTileClassifyShader, ShaderIds.TileCountY, tileCountY);
            cmd.SetComputeIntParam(settings.materialTileClassifyShader, ShaderIds.TileCount, tileCount);
            cmd.SetComputeIntParam(settings.materialTileClassifyShader, ShaderIds.TileSize, Mathf.Max(1, settings.materialTileSize));
            cmd.SetComputeIntParam(settings.materialTileClassifyShader, ShaderIds.UseNormalizedIds, ComputeFormalVBufferIdMode(sceneVisibilityBackend.TriangleCount));
            cmd.SetComputeVectorParam(settings.materialTileClassifyShader, ShaderIds.ScreenSize, new Vector4(screenWidth, screenHeight, 0, 0));
            cmd.SetComputeTextureParam(settings.materialTileClassifyShader, kernelMaterialTileClassify, ShaderIds.NaniteVBufferTex, vbuffer);
            cmd.SetComputeBufferParam(settings.materialTileClassifyShader, kernelMaterialTileClassify, ShaderIds.TriangleSubMesh, triangleSubMeshBuffer);
            cmd.SetComputeBufferParam(settings.materialTileClassifyShader, kernelMaterialTileClassify, ShaderIds.InstanceSubMeshMaterial, instanceSubMeshMaterialBuffer);
            cmd.SetComputeBufferParam(settings.materialTileClassifyShader, kernelMaterialTileClassify, ShaderIds.InstanceMaterialRange, sceneVisibilityBackend.InstanceMaterialRangeBuffer);
            cmd.SetComputeBufferParam(settings.materialTileClassifyShader, kernelMaterialTileClassify, ShaderIds.MaterialData, sceneVisibilityBackend.MaterialDataBuffer);
            cmd.SetComputeBufferParam(settings.materialTileClassifyShader, kernelMaterialTileClassify, ShaderIds.TileMaterialMask, tileMaterialMaskBuffer);
            cmd.SetComputeBufferParam(settings.materialTileClassifyShader, kernelMaterialTileClassify, ShaderIds.TileMaterialBinList, tileMaterialBinListBuffer);
            cmd.SetComputeBufferParam(settings.materialTileClassifyShader, kernelMaterialTileClassify, ShaderIds.TileMaterialBinArgs, tileIndirectArgsBuffer);
            if (keywordCompactVBufferClassifyInitialized)
            {
                cmd.SetKeyword(
                    settings.materialTileClassifyShader,
                    keywordCompactVBufferClassify,
                    CanUseCompactFormalVBuffer());
            }
            if (keywordFloat2VBufferClassifyInitialized)
            {
                cmd.SetKeyword(
                    settings.materialTileClassifyShader,
                    keywordFloat2VBufferClassify,
                    !CanUseCompactFormalVBuffer() && CanUsePortableFloat2FormalVBuffer());
            }
            cmd.DispatchCompute(settings.materialTileClassifyShader, kernelMaterialTileClassify, tileCountX, tileCountY, 1);
        }

        const string kCompactVBufferKeyword = "NANITE_COMPACT_VBUFFER";
        const string kFloat2VBufferKeyword = "NANITE_FLOAT2_VBUFFER";
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

            int minBins = Mathf.Max(1, settings.materialTileMinMaterialCount);
            return ComputeFormalResolveExecutionBinCount() >= minBins;
        }

        int ComputeFormalResolveExecutionBinCount()
        {
            if (sceneVisibilityBackend == null || !sceneVisibilityBackend.IsReady)
                return 0;

            int familyMask = 0;
            for (int materialId = 0; materialId < sceneVisibilityBackend.MaterialCount; materialId++)
            {
                int family = sceneVisibilityBackend.GetMaterialResolveFamily(materialId);
                if (family > 0 && family < 31)
                    familyMask |= 1 << family;
            }

            int absorbedFamilyMask = 0;
            int compatibilityBinCount = 0;
            for (int compatibilityBin = 1;
                 compatibilityBin <= sceneVisibilityBackend.CompatibilityBinCount;
                 compatibilityBin++)
            {
                if (sceneVisibilityBackend.GetCompatibilityBinRepresentativeMaterialId(compatibilityBin) < 0)
                    continue;
                compatibilityBinCount++;
                int family = sceneVisibilityBackend.GetCompatibilityBinStateFamily(compatibilityBin);
                if (family > 0 && family < 31 && (familyMask & (1 << family)) != 0)
                    absorbedFamilyMask |= 1 << family;
            }

            int familyBinCount = 0;
            for (int family = 1; family <= 8; family++)
            {
                if ((familyMask & (1 << family)) != 0 &&
                    (absorbedFamilyMask & (1 << family)) == 0)
                {
                    familyBinCount++;
                }
            }
            return familyBinCount + compatibilityBinCount;
        }

        bool CanUseCompactFormalVBuffer()
        {
            bool requested = settings.enableCompactFormalVBuffer;
            if (!requested || compactVBufferProbeState != CompactVBufferProbeState.Passed)
                return false;

            const GraphicsFormat format = GraphicsFormat.R32G32_UInt;
            bool supported =
                SystemInfo.IsFormatSupported(format, GraphicsFormatUsage.Render) &&
                SystemInfo.IsFormatSupported(format, GraphicsFormatUsage.Sample);
            if (!supported && !loggedCompactVBufferUnsupported)
            {
                loggedCompactVBufferUnsupported = true;
                Debug.LogWarning(
                    "[Nanite][RF] R32G32_UINT 未通过 Render/Load 能力，Formal VBuffer 回退 RG32F/RGBA32F。 ");
            }

            return supported;
        }

        static bool CanUsePortableFloat2FormalVBuffer()
        {
            const GraphicsFormat format = GraphicsFormat.R32G32_SFloat;
            return SystemInfo.IsFormatSupported(format, GraphicsFormatUsage.Render) &&
                   SystemInfo.IsFormatSupported(format, GraphicsFormatUsage.Sample);
        }

        static string FormalVBufferFormatName(bool compact) => compact
            ? "RG32UI/full32"
            : (CanUsePortableFloat2FormalVBuffer()
                ? "RG32F/normalized32"
                : "RGBA32F/split16");

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
                tileMaterialBinListBuffer,
                triangleClusterBuffer,
                trianglePageBuffer,
                clusterVisibleBuffer);
            cmd.SetGlobalBuffer(ShaderIds.InstanceMaterialRange, sceneVisibilityBackend.InstanceMaterialRangeBuffer);
            cmd.SetGlobalBuffer(ShaderIds.MaterialData, sceneVisibilityBackend.MaterialDataBuffer);
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
                tileMaterialBinListBuffer != null &&
                tileIndirectArgsBuffer != null &&
                tileCount > 0;
            cmd.SetGlobalFloat(ShaderIds.UseTileMaterialMask, useTileMask ? 1f : 0f);
            cmd.SetGlobalFloat(ShaderIds.UseCompactedTileBins, useTileMask ? 1f : 0f);
            if (useTileMask)
                cmd.SetGlobalBuffer(ShaderIds.TileMaterialBinList, tileMaterialBinListBuffer);

            int drawCount = 0;
            int familyDrawCount = 0;
            int compatibilityDrawCount = 0;
            int resolvePass = GetFormalResolvePassIndex(material, kPassGBufferMerge, kResolvePassGBufferMergeFallback);
            int familyMask = 0;
            for (int materialId = 0; materialId < sceneVisibilityBackend.MaterialCount; materialId++)
            {
                int family = sceneVisibilityBackend.GetMaterialResolveFamily(materialId);
                if (family > 0 && family < 31)
                    familyMask |= 1 << family;
            }

            int absorbedFamilyMask = 0;
            for (int compatibilityBin = 1;
                 compatibilityBin <= sceneVisibilityBackend.CompatibilityBinCount;
                 compatibilityBin++)
            {
                int family = sceneVisibilityBackend.GetCompatibilityBinStateFamily(compatibilityBin);
                if (family > 0 && family < 31 && (familyMask & (1 << family)) != 0)
                    absorbedFamilyMask |= 1 << family;
            }

            for (int family = 1; family <= 8; family++)
            {
                if ((familyMask & (1 << family)) == 0 ||
                    (absorbedFamilyMask & (1 << family)) != 0)
                    continue;
                Material familyMaterial = GetFormalResolveStateMaterial(
                    material,
                    family - 1,
                    useGBufferDepthSlice,
                    useCompactVBuffer);
                BindFormalResolveFamilyBatch(cmd, familyMaterial, family);
                cmd.SetGlobalFloat(ShaderIds.UseTileMaterialMask, useTileMask ? 1f : 0f);
                if (useTileMask)
                {
                    cmd.DrawProceduralIndirect(
                        Matrix4x4.identity,
                        familyMaterial,
                        resolvePass,
                        MeshTopology.Triangles,
                        tileIndirectArgsBuffer,
                        family * sizeof(uint) * 4);
                }
                else
                {
                    cmd.DrawProcedural(
                        Matrix4x4.identity,
                        familyMaterial,
                        resolvePass,
                        MeshTopology.Triangles,
                        3,
                        1);
                }
                drawCount++;
                familyDrawCount++;
            }

            int emittedAbsorbedFamilyMask = 0;
            for (int compatibilityBin = 1;
                 compatibilityBin <= sceneVisibilityBackend.CompatibilityBinCount;
                 compatibilityBin++)
            {
                int materialId = sceneVisibilityBackend.GetCompatibilityBinRepresentativeMaterialId(compatibilityBin);
                if (materialId < 0)
                    continue;

                var sourceMaterial = sceneVisibilityBackend.GetMaterial(materialId);
                MaterialPropertyBlock drawProperties =
                    GetFormalResolvePropertyBlock(compatibilityDrawCount);
                int resolvePipeline = sceneVisibilityBackend.GetMaterialResolvePipeline(materialId);
                Material batchResolveMaterial = material;
                int batchResolvePass = resolvePass;
                int stateFamily = sceneVisibilityBackend.GetCompatibilityBinStateFamily(compatibilityBin);
                int absorbedFamily = stateFamily > 0 && stateFamily < 31 &&
                                     (absorbedFamilyMask & (1 << stateFamily)) != 0 &&
                                     (emittedAbsorbedFamilyMask & (1 << stateFamily)) == 0
                    ? stateFamily
                    : 0;
                if (absorbedFamily != 0)
                    emittedAbsorbedFamilyMask |= 1 << absorbedFamily;
                if (resolvePipeline == 0)
                {
                    batchResolveMaterial = GetFormalResolveStateMaterial(
                        material,
                        Mathf.Clamp(stateFamily - 1, 0, 7),
                        useGBufferDepthSlice,
                        useCompactVBuffer);
                    BindFormalResolveCompatibilityBatch(
                        cmd,
                        batchResolveMaterial,
                        sourceMaterial,
                        materialId,
                        compatibilityBin,
                        absorbedFamily,
                        drawProperties);
                }
                else if (resolvePipeline > 0 &&
                         NaniteMaterialResolveRegistry.TryGet(resolvePipeline, out INaniteMaterialResolveFamily family))
                {
                    batchResolveMaterial = family.ResolveMaterial;
                    if (batchResolveMaterial == null)
                        continue;
                    ConfigureFormalResolveMaterialKeywords(
                        batchResolveMaterial,
                        useGBufferDepthSlice,
                        useCompactVBuffer);
                    cmd.SetGlobalFloat(ShaderIds.ResolveMaterialId, materialId);
                    cmd.SetGlobalFloat(ShaderIds.ResolveMaterialMode, 2f);
                    cmd.SetGlobalFloat(ShaderIds.ResolveMaterialFamily, compatibilityBin);
                    cmd.SetGlobalFloat(ShaderIds.ResolveAbsorbedFamily, 0f);
                    try
                    {
                        family.Bind(cmd, sourceMaterial, drawProperties);
                    }
                    catch (Exception exception)
                    {
                        if (warnedResolveFamilyFailures.Add(resolvePipeline))
                            Debug.LogException(exception);
                        continue;
                    }
                    batchResolvePass = GetFormalResolvePassIndex(
                        batchResolveMaterial,
                        kPassGBufferMerge,
                        kResolvePassGBufferMergeFallback);
                }
                else
                {
                    // Unsupported programs should normally have been admitted to
                    // their native Renderer. Forced-Nanite diagnostics fail closed
                    // here instead of silently shading with URP/Lit semantics.
                    continue;
                }
                // Compatibility bins group all parameter variants sharing shader state and
                // texture bindings. Bloom collisions only admit extra tiles; pixels still
                // compare the exact bin stored in GpuMaterialData.
                cmd.SetGlobalFloat(ShaderIds.UseTileMaterialMask, useTileMask ? 1f : 0f);
                if (useTileMask)
                {
                    cmd.DrawProceduralIndirect(
                        Matrix4x4.identity,
                        batchResolveMaterial,
                        batchResolvePass,
                        MeshTopology.Triangles,
                        tileIndirectArgsBuffer,
                        (32 + (compatibilityBin & 31)) * sizeof(uint) * 4,
                        drawProperties);
                }
                else
                {
                    cmd.DrawProcedural(
                        Matrix4x4.identity,
                        batchResolveMaterial,
                        batchResolvePass,
                        MeshTopology.Triangles,
                        3,
                        1,
                        drawProperties);
                }
                drawCount++;
                compatibilityDrawCount++;
            }
            cmd.SetGlobalFloat(ShaderIds.ResolveMaterialMode, 0f);
            cmd.SetGlobalFloat(ShaderIds.ResolveAbsorbedFamily, 0f);
            cmd.SetGlobalFloat(ShaderIds.UseCompactedTileBins, 0f);

            RecordFormalPerfSample(1, drawCount, sceneVisibilityBackend.MaterialCount, 1f);

            if (!loggedFormalOnce && drawCount > 0)
            {
                loggedFormalOnce = true;
                Debug.Log(
                    $"[Nanite][RF] Formal Resolve: depthFill={(settings.enableFormalDepthFill ? 1 : 0)} gBufferBatches={drawCount} " +
                    $"familyBins={familyDrawCount} compatibilityBins={compatibilityDrawCount} " +
                    $"uniqueGeometryTriangles={sceneVisibilityBackend.TriangleCount} inst={sceneVisibilityBackend.InstanceCount} " +
                    $"uniqueMeshes={sceneVisibilityBackend.UniqueMeshCount} " +
                    $"sharedVertices={sceneVisibilityBackend.GeometryVertexCount}/{sceneVisibilityBackend.VirtualVertexCount} " +
                    $"sharedTriangles={sceneVisibilityBackend.TriangleCount}/{sceneVisibilityBackend.VirtualTriangleCount} " +
                    $"geometrySource={(sceneVisibilityBackend.UsePackedPageRaster ? "residentCache" : "compatDecoded")} " +
                    $"residentAddress={(sceneVisibilityBackend.UsePackedPageRaster ? "pageId+localIndex" : "n/a")} " +
                    $"geometryMiB=compat:{sceneVisibilityBackend.CompatibilityGeometryBytes / (1024f * 1024f):F2}," +
                    $"resident:{sceneVisibilityBackend.ResidentGeometryBytes / (1024f * 1024f):F2}," +
                    $"pool:{sceneVisibilityBackend.PagePoolBytes / (1024f * 1024f):F2}," +
                    $"payload:{sceneVisibilityBackend.ResidentPagePayloadBytes / (1024f * 1024f):F2}," +
                    $"triRef:{sceneVisibilityBackend.TrianglePageRefBytes / (1024f * 1024f):F2} " +
                    $"tileResolve={(useTileMask ? $"on({tileCountX}x{tileCountY}@{Mathf.Max(1, settings.materialTileSize)})" : "off")} " +
                    $"vbuffer={FormalVBufferFormatName(useCompactVBuffer)} " +
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
                sceneVisibilityBackend.IsGpuVisibleMaskReadyFor(camera) &&
                lastGpuScenePrepareFrame == Time.frameCount &&
                lastGpuScenePrepareCameraId == cameraId)
            {
                return true;
            }

            var proxies = NaniteRuntimeRegistry.ActiveProxies;
            sceneVisibilityBackend ??= new NaniteSceneVisibilityBufferBackend();
            sceneVisibilityBackend.LightProbeRefreshInterval = settings.lightProbeRefreshInterval;
            sceneVisibilityBackend.PagePoolMaxMiB = settings.pagePoolMaxMiB;
            sceneVisibilityBackend.IndexedDrawRequested = IsIndexedRasterRequested();
            sceneVisibilityBackend.IndexedDrawBufferMaxMiB = settings.indexedClusterBufferMaxMiB;
            sceneVisibilityBackend.PackedPageRasterRequested = settings.enablePackedPageRaster;
            sceneVisibilityBackend.PageTranscodeShader = settings.pageTranscodeShader;
            sceneVisibilityBackend.PageStreamingRequestsEnabled = settings.enablePageStreamingUploads;
            if (!sceneVisibilityBackend.EnsureInitialized(proxies))
                return false;
            // A GPU mask belongs to exactly one camera. Treating another camera's
            // mask as prepared makes the compact args and draw queue disagree.
            if (sceneVisibilityBackend.GpuVisibleMaskReady &&
                !sceneVisibilityBackend.IsGpuVisibleMaskReadyFor(camera))
                return false;
            if (!sceneVisibilityBackend.UpdateVisibleClusters(selectionsByProxyId))
                return false;
            if (sceneVisibilityBackend.IsGpuVisibleMaskReadyFor(camera))
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
            tileCountX = nextTileX;
            tileCountY = nextTileY;
            tileCount = nextTileCount;

            if (tileMaterialMaskBuffer == null || tileMaskCapacity < nextTileCount)
            {
                tileMaterialMaskBuffer?.Release();
                tileMaterialBinListBuffer?.Release();
                tileMaskCapacity = Mathf.NextPowerOfTwo(nextTileCount);
                tileMaterialMaskBuffer = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured,
                    tileMaskCapacity,
                    sizeof(uint) * 2);
                tileMaterialBinListBuffer = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured,
                    checked(tileMaskCapacity * kMaterialTileBinCount),
                    sizeof(uint));
            }
            else if (tileMaterialBinListBuffer == null)
            {
                tileMaterialBinListBuffer = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured,
                    checked(tileMaskCapacity * kMaterialTileBinCount),
                    sizeof(uint));
            }

            if (tileIndirectArgsBuffer == null)
                tileIndirectArgsBuffer = new GraphicsBuffer(
                    GraphicsBuffer.Target.IndirectArguments | GraphicsBuffer.Target.Structured,
                    kMaterialTileBinCount * 4,
                    sizeof(uint));
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
            GraphicsBuffer tileBinListBuffer,
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
            cmd.SetGlobalBuffer(ShaderIds.TileMaterialBinList, tileBinListBuffer);
            cmd.SetGlobalBuffer(ShaderIds.TriangleCluster, triangleClusterBuffer);
            cmd.SetGlobalBuffer(ShaderIds.TrianglePage, trianglePageBuffer);
            cmd.SetGlobalBuffer(ShaderIds.ClusterVisible, clusterVisibleBuffer);
        }

        bool CanBindPackedPageGeometry() =>
            sceneVisibilityBackend != null &&
            sceneVisibilityBackend.UsePackedPageRaster &&
            sceneVisibilityBackend.ResidentVertexBuffer != null &&
            sceneVisibilityBackend.ResidentIndexBuffer != null &&
            sceneVisibilityBackend.ResidentPageTableBuffer != null &&
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
            cmd.SetGlobalBuffer(ShaderIds.ResidentPageTable, sceneVisibilityBackend.ResidentPageTableBuffer);
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
            cmd.SetGlobalBuffer(ShaderIds.ResidentPageTable, sceneVisibilityBackend.ResidentPageTableBuffer);
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
            properties.SetBuffer(ShaderIds.ResidentPageTable, sceneVisibilityBackend.ResidentPageTableBuffer);
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
            cmd.SetGlobalFloat(ShaderIds.UseCompactedTileBins, 0f);
        }

        void BindFormalResolveCompatibilityBatch(
            RasterCommandBuffer cmd,
            Material resolveMaterial,
            Material sourceMaterial,
            int representativeMaterialId,
            int compatibilityBin,
            int absorbedFamily,
            MaterialPropertyBlock properties)
        {
            cmd.SetGlobalFloat(ShaderIds.ResolveMaterialId, representativeMaterialId);
            cmd.SetGlobalFloat(ShaderIds.ResolveMaterialMode, 2f);
            cmd.SetGlobalFloat(ShaderIds.ResolveMaterialFamily, compatibilityBin);
            cmd.SetGlobalFloat(ShaderIds.ResolveAbsorbedFamily, absorbedFamily);

            var baseMap = ResolveTexture(
                sourceMaterial,
                "_BaseMap",
                ResolveTexture(sourceMaterial, "_MainTex", Texture2D.whiteTexture));
            var normalMap = ResolveTexture(sourceMaterial, "_BumpMap", Texture2D.normalTexture != null ? Texture2D.normalTexture : Texture2D.whiteTexture);
            var metallicGlossMap = ResolveTexture(sourceMaterial, "_MetallicGlossMap", Texture2D.blackTexture);
            var occlusionMap = ResolveTexture(sourceMaterial, "_OcclusionMap", Texture2D.whiteTexture);
            var emissionMap = ResolveTexture(sourceMaterial, "_EmissionMap", Texture2D.blackTexture);

            if (resolveMaterial != null)
            {
                CoreUtils.SetKeyword(resolveMaterial, "_ENVIRONMENTREFLECTIONS_OFF",
                    sourceMaterial != null && sourceMaterial.IsKeywordEnabled("_ENVIRONMENTREFLECTIONS_OFF"));
                CoreUtils.SetKeyword(resolveMaterial, "_SPECULARHIGHLIGHTS_OFF",
                    sourceMaterial != null && sourceMaterial.IsKeywordEnabled("_SPECULARHIGHLIGHTS_OFF"));
                CoreUtils.SetKeyword(resolveMaterial, "_RECEIVE_SHADOWS_OFF",
                    sourceMaterial != null && sourceMaterial.IsKeywordEnabled("_RECEIVE_SHADOWS_OFF"));

                properties.SetTexture(ShaderIds.BaseMap, baseMap);
                properties.SetTexture(ShaderIds.BumpMap, normalMap);
                properties.SetTexture(ShaderIds.MetallicGlossMap, metallicGlossMap);
                properties.SetTexture(ShaderIds.OcclusionMap, occlusionMap);
                properties.SetTexture(ShaderIds.EmissionMap, emissionMap);
            }
        }

        MaterialPropertyBlock GetFormalResolvePropertyBlock(int index)
        {
            while (formalResolvePropertyBlocks.Count <= index)
                formalResolvePropertyBlocks.Add(new MaterialPropertyBlock());
            MaterialPropertyBlock properties = formalResolvePropertyBlocks[index];
            properties.Clear();
            return properties;
        }

        Material GetFormalResolveStateMaterial(
            Material source,
            int keywordState,
            bool useGBufferDepthSlice,
            bool useCompactVBuffer)
        {
            if (source == null)
                return null;

            int sourceId = source.GetInstanceID();
            if (formalResolveStateSourceId != sourceId)
            {
                ReleaseFormalResolveStateMaterials();
                formalResolveStateSourceId = sourceId;
            }

            int state = Mathf.Clamp(keywordState, 0, formalResolveStateMaterials.Length - 1);
            Material variant = formalResolveStateMaterials[state];
            if (variant == null)
            {
                variant = new Material(source)
                {
                    name = $"{source.name}_State{state}",
                    hideFlags = HideFlags.HideAndDontSave
                };
                formalResolveStateMaterials[state] = variant;
            }

            ConfigureFormalResolveMaterialKeywords(
                variant,
                useGBufferDepthSlice,
                useCompactVBuffer);
            CoreUtils.SetKeyword(variant, "_ENVIRONMENTREFLECTIONS_OFF", (state & 1) != 0);
            CoreUtils.SetKeyword(variant, "_SPECULARHIGHLIGHTS_OFF", (state & 2) != 0);
            CoreUtils.SetKeyword(variant, "_RECEIVE_SHADOWS_OFF", (state & 4) != 0);
            return variant;
        }

        void ReleaseFormalResolveStateMaterials()
        {
            for (int i = 0; i < formalResolveStateMaterials.Length; i++)
            {
                if (formalResolveStateMaterials[i] == null)
                    continue;
                DestroyObject(formalResolveStateMaterials[i]);
                formalResolveStateMaterials[i] = null;
            }
            formalResolveStateSourceId = 0;
        }

        void BindFormalResolveFamilyBatch(RasterCommandBuffer cmd, Material resolveMaterial, int familyId)
        {
            cmd.SetGlobalFloat(ShaderIds.ResolveMaterialId, -1f);
            cmd.SetGlobalFloat(ShaderIds.ResolveMaterialMode, 1f);
            cmd.SetGlobalFloat(ShaderIds.ResolveMaterialFamily, familyId);
            cmd.SetGlobalFloat(ShaderIds.ResolveAbsorbedFamily, 0f);
            int state = Mathf.Max(0, familyId - 1);
            CoreUtils.SetKeyword(resolveMaterial, "_ENVIRONMENTREFLECTIONS_OFF", (state & 1) != 0);
            CoreUtils.SetKeyword(resolveMaterial, "_SPECULARHIGHLIGHTS_OFF", (state & 2) != 0);
            CoreUtils.SetKeyword(resolveMaterial, "_RECEIVE_SHADOWS_OFF", (state & 4) != 0);
            resolveMaterial.SetTexture(ShaderIds.BaseMap, Texture2D.whiteTexture);
            resolveMaterial.SetTexture(
                ShaderIds.BumpMap,
                Texture2D.normalTexture != null ? Texture2D.normalTexture : Texture2D.whiteTexture);
            resolveMaterial.SetTexture(ShaderIds.MetallicGlossMap, Texture2D.blackTexture);
            resolveMaterial.SetTexture(ShaderIds.OcclusionMap, Texture2D.whiteTexture);
            resolveMaterial.SetTexture(ShaderIds.EmissionMap, Texture2D.blackTexture);
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
            CoreUtils.SetKeyword(
                resolveMaterial,
                kFloat2VBufferKeyword,
                !useCompactVBuffer && CanUsePortableFloat2FormalVBuffer());
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


        void ExecuteFirstCull(Camera camera)
        {
            using var profilerScope = kFirstCullMarker.Auto();
            if (camera == null)
                return;
            LogAutomaticWorkloadStats(camera);
            EnsureInternalCollections();
            LogExecutionOnce(camera, "first-cull");
            double startMs = Time.realtimeSinceStartupAsDouble * 1000.0;

            var state = GetOrCreateState(camera.pixelWidth, camera.pixelHeight, camera.GetInstanceID());
            state.depthWrittenThisFrame = false;
            state.depthCopiedForHzb = false;
            state.currentHzbValid = false;
            sceneVisibilityBackend?.RefreshInstanceTransforms();
            bool requestPreviousHzb = settings.usePreviousHzbOnFirstCull;
            bool stableHzbHistory = IsHzbHistoryCompatible(state, camera);
            Texture prevHzb = (IsHzbActiveForScene() &&
                               requestPreviousHzb &&
                               state.hasPrevious &&
                               stableHzbHistory)
                ? state.previous
                : null;
            int prevMipCount = state.mipCount;
            NaniteGpuBatchedCullingBackend.HzbViewParameters prevHzbView =
                prevHzb != null ? state.historyHzbView : default;

            var proxies = NaniteRuntimeRegistry.ActiveProxies;
            int activeProxies = 0;
            var cullMode = IsBevyTwoPhaseEnabled()
                ? NaniteGpuBatchedCullingBackend.CullPassMode.Pass1PrevVisible
                : NaniteGpuBatchedCullingBackend.CullPassMode.Legacy;
            // Pass 1 may sample previous depth only with the view that authored it.
            bool firstUseHzb = prevHzb != null;
            bool gpuMask = TryRunBatchedCullGpuMask(
                camera, prevHzb, prevMipCount, firstUseHzb, prevHzbView,
                clearMask: true, cullMode, out int batchableProxies);
            bool batched = gpuMask;
            if (gpuMask)
            {
                LogBatchedDispatchStatsOnce(batchableProxies, "first-cull-gpuMask", firstUseHzb);
                LogBevyCullStatsIfNeeded("cull1", camera);
            }
            else
            {
                sceneVisibilityBackend?.ClearGpuVisibleMaskReady();
                batched = TryRunBatchedCull(
                    camera, prevHzb, prevMipCount, prevHzb != null, prevHzbView,
                    firstSelections, out batchableProxies);
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
                    // The legacy CPU selector has no independent historical view.
                    // Fail open instead of sampling previous depth with current matrices.
                    proxy.TryComputeSelectionForCamera(camera, null, 0, false, sel);
                }

                LogDispatchStatsOnce(legacyProxies, "first-cull");
                activeProxies = legacyProxies;
            }
            else
                activeProxies = batchableProxies;

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
            Camera camera,
            MaterialPropertyBlock mpb,
            int compactClusterOffset = 0,
            bool drawProceduralFallback = true,
            bool appendOnly = false)
        {
            if (cmd == null || material == null || camera == null || mpb == null ||
                !HasValidIndexedDraw(selectionKey, camera))
                return false;
            GraphicsBuffer packets = sceneVisibilityBackend.IndexedTrianglePacketBuffer;
            GraphicsBuffer indexedArgs = sceneVisibilityBackend.IndexedDrawArgsBuffer;
            GraphicsBuffer appendArgs = sceneVisibilityBackend.IndexedAppendDrawArgsBuffer;
            GraphicsBuffer fallbackArgs = sceneVisibilityBackend.IndexedFallbackDrawArgsBuffer;
            GraphicsBuffer overflowClusters = sceneVisibilityBackend.IndexedOverflowClusterBuffer;
            bool hasAppend = lastIndexedDrawAppendedPass2 &&
                             selectionKey == kCompactSelectionMerged &&
                             appendArgs != null &&
                             sceneVisibilityBackend.IndexedCameraPacketSliceBuffer != null;
            if (packets == null || indexedArgs == null || fallbackArgs == null || overflowClusters == null)
                return false;

            mpb.SetInt(ShaderIds.UseIndexedClusterRaster, 1);
            mpb.SetInt(ShaderIds.GeometryVertexCount, sceneVisibilityBackend.IndexedVertexDomainCount);
            mpb.SetBuffer(ShaderIds.IndexedTrianglePackets, packets);
            mpb.SetInt(ShaderIds.IndexedTrianglePacketBase, 0);
            mpb.SetInt(ShaderIds.IndexedPacketInstanceBits, sceneVisibilityBackend.IndexedPacketInstanceBits);
            mpb.SetInt(ShaderIds.IndexedPacketInstanceMask, unchecked((int)sceneVisibilityBackend.IndexedPacketInstanceMask));
            mpb.SetBuffer(ShaderIds.IndexedCameraPacketSlice, sceneVisibilityBackend.IndexedCameraPacketSliceBuffer);
            mpb.SetInt(ShaderIds.UseIndexedPacketDynamicBase, 0);
            mpb.SetBuffer(ShaderIds.IndexedOverflowClusterIndices, overflowClusters);
            mpb.SetFloat(ShaderIds.UseIndexedOverflowClusterIndices, 0.0f);
            mpb.SetFloat(ShaderIds.CompactedClusterOffset, Mathf.Max(0, compactClusterOffset));
            bool drawAppendOnly = appendOnly && hasAppend;
            if (!drawAppendOnly)
            {
                cmd.DrawProceduralIndirect(
                    Matrix4x4.identity,
                    material,
                    shaderPass,
                    MeshTopology.Triangles,
                    indexedArgs,
                    0,
                    mpb);
            }
            if (hasAppend)
            {
                mpb.SetInt(ShaderIds.UseIndexedPacketDynamicBase, 1);
                cmd.DrawProceduralIndirect(
                    Matrix4x4.identity,
                    material,
                    shaderPass,
                    MeshTopology.Triangles,
                    appendArgs,
                    0,
                    mpb);
                mpb.SetInt(ShaderIds.UseIndexedPacketDynamicBase, 0);
            }
            if (!drawProceduralFallback)
            {
                mpb.SetInt(ShaderIds.UseIndexedClusterRaster, 0);
                return true;
            }
            mpb.SetInt(ShaderIds.UseIndexedClusterRaster, 0);
            mpb.SetFloat(ShaderIds.UseIndexedOverflowClusterIndices, 1.0f);
            mpb.SetFloat(ShaderIds.CompactedClusterOffset, 0.0f);
            cmd.DrawProceduralIndirect(
                Matrix4x4.identity,
                material,
                shaderPass,
                MeshTopology.Triangles,
                fallbackArgs,
                0,
                mpb);
            mpb.SetFloat(ShaderIds.UseIndexedOverflowClusterIndices, 0.0f);
            return true;
        }

        bool TryDrawIndexedShadowQueue(
            RasterCommandBuffer cmd,
            Material material,
            int shaderPass,
            int cascadeIndex,
            MaterialPropertyBlock mpb)
        {
            if (!IsIndexedRasterRequested() || cmd == null || material == null || mpb == null ||
                sceneVisibilityBackend == null || !sceneVisibilityBackend.IndexedDrawAvailable ||
                !sceneVisibilityBackend.IsIndexedShadowCascadeReady(cascadeIndex))
                return false;
            GraphicsBuffer packets = sceneVisibilityBackend.IndexedTrianglePacketBuffer;
            GraphicsBuffer indexedArgs = sceneVisibilityBackend.GetIndexedShadowDrawArgsBuffer(cascadeIndex);
            GraphicsBuffer fallbackArgs = sceneVisibilityBackend.GetIndexedShadowFallbackDrawArgsBuffer(cascadeIndex);
            GraphicsBuffer overflowClusters = sceneVisibilityBackend.IndexedOverflowClusterBuffer;
            if (packets == null || indexedArgs == null || fallbackArgs == null || overflowClusters == null)
                return false;

            mpb.SetBuffer(
                ShaderIds.IndexedShadowSliceData,
                sceneVisibilityBackend.IndexedShadowSliceDataBuffer);
            mpb.SetInt(ShaderIds.IndexedShadowCascadeIndex, cascadeIndex);
            mpb.SetInt(ShaderIds.UseIndexedShadowDynamicSlice, 1);
            mpb.SetInt(ShaderIds.UseIndexedClusterRaster, 1);
            mpb.SetInt(ShaderIds.GeometryVertexCount, sceneVisibilityBackend.IndexedVertexDomainCount);
            mpb.SetBuffer(ShaderIds.IndexedTrianglePackets, packets);
            mpb.SetInt(ShaderIds.IndexedTrianglePacketBase, 0);
            mpb.SetInt(ShaderIds.IndexedPacketInstanceBits, sceneVisibilityBackend.IndexedPacketInstanceBits);
            mpb.SetInt(ShaderIds.IndexedPacketInstanceMask, unchecked((int)sceneVisibilityBackend.IndexedPacketInstanceMask));
            mpb.SetBuffer(ShaderIds.IndexedOverflowClusterIndices, overflowClusters);
            mpb.SetFloat(ShaderIds.UseIndexedOverflowClusterIndices, 0.0f);
            mpb.SetFloat(ShaderIds.CompactedClusterOffset, 0.0f);
            cmd.DrawProceduralIndirect(
                Matrix4x4.identity,
                material,
                shaderPass,
                MeshTopology.Triangles,
                indexedArgs,
                0,
                mpb);
            mpb.SetInt(ShaderIds.UseIndexedClusterRaster, 0);
            // The GPU allocates four cascade slices from one shared scratch
            // buffer using their actual queue counts. The procedural tail begins
            // after this cascade's admitted slice; the shadow shader reads that
            // offset without a CPU count readback.
            mpb.SetInt(ShaderIds.UseIndexedShadowDynamicSlice, 1);
            mpb.SetFloat(ShaderIds.UseIndexedOverflowClusterIndices, 1.0f);
            mpb.SetFloat(ShaderIds.CompactedClusterOffset, 0.0f);
            cmd.DrawProceduralIndirect(
                Matrix4x4.identity,
                material,
                shaderPass,
                MeshTopology.Triangles,
                fallbackArgs,
                0,
                mpb);
            mpb.SetFloat(ShaderIds.UseIndexedOverflowClusterIndices, 0.0f);
            mpb.SetInt(ShaderIds.UseIndexedShadowDynamicSlice, 0);
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
            mpb.SetInt(ShaderIds.UseIndexedShadowDynamicSlice, 0);
            if (sceneVisibilityBackend.IndexedShadowSliceDataBuffer != null)
            {
                mpb.SetBuffer(
                    ShaderIds.IndexedShadowSliceData,
                    sceneVisibilityBackend.IndexedShadowSliceDataBuffer);
            }
            GraphicsBuffer activeDrawArgs = drawArgsBuffer;
            bool hybridShadowActive =
                hybridShadowReadyFrame == Time.frameCount &&
                hybridShadowReadyCascade == cascadeIndex &&
                hybridHardwareClusterBuffer != null;
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
                    hybridShadowActive
                        ? hybridHardwareClusterBuffer
                        : batchedCulling.GetShadowDrawClusterBuffer(cascadeIndex));
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
                BindCompactDrawState(mpb, kCompactSelectionFirst, camera);
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

        void DrawHybridSoftwareShadowMerge(
            RasterCommandBuffer cmd,
            Material material,
            ExternalMainLightShadowDrawContext context)
        {
            if (cmd == null || material == null ||
                hybridShadowReadyFrame != Time.frameCount ||
                hybridShadowReadyCascade != context.cascadeIndex ||
                hybridShadowSoftwareDepth == null ||
                hybridSoftwareTileListBuffer == null ||
                hybridSoftwareTileDrawArgsBuffer == null)
                return;

            int resolution = Mathf.Max(1, Mathf.RoundToInt(context.viewport.width));
            var mpb = GetOrCreateVBufferPropertyBlock();
            mpb.Clear();
            mpb.SetTexture(ShaderIds.NaniteSoftwareDepth, hybridShadowSoftwareDepth);
            mpb.SetBuffer(ShaderIds.NaniteSoftwareTileList, hybridSoftwareTileListBuffer);
            mpb.SetInt(ShaderIds.NaniteSoftwareScreenWidth, resolution);
            mpb.SetInt(ShaderIds.NaniteSoftwareScreenHeight, resolution);
            mpb.SetInt(
                ShaderIds.NaniteSoftwareTileCountX,
                Mathf.Max(1, (resolution + kHybridSoftwareTileSize - 1) / kHybridSoftwareTileSize));
            mpb.SetInt(ShaderIds.NaniteSoftwareTileSize, kHybridSoftwareTileSize);
            mpb.SetVector(
                ShaderIds.NaniteSoftwareViewportOrigin,
                new Vector4(context.viewport.x, context.viewport.y, 0f, 0f));
            cmd.DrawProceduralIndirect(
                Matrix4x4.identity,
                material,
                1,
                MeshTopology.Triangles,
                hybridSoftwareTileDrawArgsBuffer,
                0,
                mpb);
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
            BindCompactDrawState(mpb, kCompactSelectionFirst, camera);
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
            if (!HasValidCompactForSelection(kCompactSelectionFirst, camera))
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
            BindCompactDrawState(mpb, kCompactSelectionFirst, camera);
            if (!TryDrawIndexedCameraQueue(cmd, material, 0, kCompactSelectionFirst, camera, mpb))
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
            NaniteGpuBatchedCullingBackend.HzbViewParameters currentHzbView =
                NaniteGpuBatchedCullingBackend.HzbViewParameters.FromCamera(camera);

            var proxies = NaniteRuntimeRegistry.ActiveProxies;
            bool usedGpuMaskFirst = sceneVisibilityBackend != null &&
                                    sceneVisibilityBackend.IsGpuVisibleMaskReadyFor(camera);
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
                    camera, currentHzb, mipCount, secondUseHzb, currentHzbView,
                    clearMask: false, cullMode, out batchableProxies);
                if (gpuMask)
                {
                    LogBatchedDispatchStatsOnce(batchableProxies, "second-cull-gpuMask", secondUseHzb);
                    LogBevyCullStatsIfNeeded("cull2", camera);
                    // Pass2 appends recovered clusters directly into ClusterVisible and
                    // the HW/SW queues; a full-scene mask merge/copy is redundant.
                }
                else if (settings.logStats)
                    Debug.LogWarning("[Nanite][RF] SecondCull GPU mask 失败，本帧保持 FirstCull mask（跳过 CPU readback 回退）。");
            }

            bool batched = gpuMask;
            if (!usedGpuMaskFirst)
            {
                batched = TryRunBatchedCull(
                    camera, currentHzb, mipCount, currentHzb != null, currentHzbView,
                    secondSelections, out batchableProxies);
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
            CommitHzbHistory(state, camera);
        }

        bool TryRunBatchedCullGpuMask(
            Camera camera,
            Texture hzbTexture,
            int hzbMipCount,
            bool useHzb,
            NaniteGpuBatchedCullingBackend.HzbViewParameters hzbView,
            bool clearMask,
            out int batchableProxyCount)
        {
            return TryRunBatchedCullGpuMask(
                camera,
                hzbTexture,
                hzbMipCount,
                useHzb,
                hzbView,
                clearMask,
                NaniteGpuBatchedCullingBackend.CullPassMode.Legacy,
                out batchableProxyCount);
        }

        bool TryRunBatchedCullGpuMask(
            Camera camera,
            Texture hzbTexture,
            int hzbMipCount,
            bool useHzb,
            NaniteGpuBatchedCullingBackend.HzbViewParameters hzbView,
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
            int telemetryInterval = settings.adaptiveHzbCulling
                ? (hzbAdmissionProbeMode
                    ? kHzbAdmissionProbeInterval
                    : kHzbAdmissionSteadyInterval)
                : 120;
            bool automaticTelemetryFrame = camera.cameraType == CameraType.Game &&
                                           Time.frameCount > 0 &&
                                           Time.frameCount % telemetryInterval == 0;
            bool enableStats = (settings.logStats && (Time.frameCount % 30 == 0)) ||
                               automaticTelemetryFrame;
            bool ok = batchedCulling.RunWriteClusterVisible(
                camera,
                hzbTexture,
                hzbMipCount,
                useHzb,
                hzbView,
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
                sceneVisibilityBackend.MarkGpuVisibleMaskReady(camera);
                if (cullPassMode == NaniteGpuBatchedCullingBackend.CullPassMode.Pass1PrevVisible &&
                    IsBevyFormalDualRasterActive() &&
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
            NaniteGpuBatchedCullingBackend.HzbViewParameters hzbView,
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
            return batchedCulling.Run(
                camera, hzbTexture, hzbMipCount, useHzb, hzbView, outputsByProxyId);
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
            batchedCulling.EnableTraversalRasterBins =
                IsHybridRasterRequested() &&
                !IsBevyFormalDualRasterActive() &&
                CanUseCompactFormalVBuffer();
            batchedCulling.EnableClusterBackfaceCulling =
                sceneVisibilityBackend != null &&
                !sceneVisibilityBackend.RequiresTwoSidedRaster;
            batchedCulling.SoftwareRasterThresholdPixels = Mathf.Max(1f, settings.hybridSoftwareMaxEdgePixels);
            batchedCulling.ShadowLodMinTexels = Mathf.Max(1f, ShadowLodMinTexelsForDiagnostics);
        }

        bool IsBevyFormalDualRasterActive() =>
            settings.enableBevyFormalDualRaster &&
            IsBevyTwoPhaseEnabled() &&
            IsHzbActiveForScene() &&
            settings.enablePrevVisiblePass1Filter;

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
            string candidateWork = batchedCulling != null && batchedCulling.LastUsedHierarchyQueue
                ? "GPU-selected-cut"
                : (batchedCulling != null && batchedCulling.LastUsedSpatialHierarchy
                    ? "GPU-spatial-cut"
                : (batchedCulling != null && batchedCulling.LastUsedVisiblePartQueue
                    ? "GPU-queued"
                    : candidates.ToString()));
            string hierarchy = batchedCulling != null && batchedCulling.LastUsedCpuCandidates
                ? "CPU-BVH"
                : (batchedCulling != null && batchedCulling.LastUsedHierarchyQueue
                    ? (batchedCulling.HierarchyTraversalPassCount == 1
                        ? "GPU-RootGroup-PersistentQueue-DrawIndirect"
                        : "GPU-RootGroup-RefineQueue-DrawIndirect")
                    : (batchedCulling != null && batchedCulling.LastUsedSpatialHierarchy
                        ? "GPU-InstanceQueue-SpatialNode-PartQueue-ClusterIndirect"
                    : (batchedCulling != null && batchedCulling.LastUsedPartDrivenClusterCull
                    ? (batchedCulling.LastUsedVisibleInstanceQueue
                        ? "GPU-InstanceQueue-PartQueue-ClusterIndirect"
                        : (batchedCulling.LastUsedVisiblePartQueue
                            ? "GPU-Instance-PartQueue-ClusterIndirect"
                            : "GPU-Instance-Part-Cluster"))
                    : "GPU-FullCluster")));
            Debug.Log(
                $"[Nanite][RF] Batched GPU culling ({stage}): proxies={batchableProxies}, " +
                $"candidates={candidateWork} / clusters={clusters}, hierarchy={hierarchy}, " +
                $"gpuScene=mesh:{(batchedCulling != null ? batchedCulling.UniqueGeometryCount : 0)}," +
                $"part:{(batchedCulling != null ? batchedCulling.GeometryPartCount : 0)}/" +
                $"{(batchedCulling != null ? batchedCulling.VirtualPartCount : 0)}," +
                $"cluster:{(batchedCulling != null ? batchedCulling.GeometryClusterCount : 0)}/" +
                $"{(batchedCulling != null ? batchedCulling.VirtualClusterCount : 0)}, " +
                $"dag:{(batchedCulling != null ? batchedCulling.HierarchyGroupCount : 0)}/" +
                $"{(batchedCulling != null ? batchedCulling.HierarchyRefCount : 0)} " +
                $"passes={(batchedCulling != null ? batchedCulling.HierarchyTraversalPassCount : 0)}, " +
                $"spatial:{(batchedCulling != null ? batchedCulling.SpatialNodeCount : 0)} " +
                $"passes={(batchedCulling != null ? batchedCulling.SpatialTraversalPassCount : 0)} " +
                $"queue={(batchedCulling != null ? batchedCulling.SpatialQueueCapacity : 0)}, " +
                $"drawCapacity={(batchedCulling != null ? batchedCulling.DrawQueueCapacity : 0)}, " +
                $"refs={(batchedCulling != null && batchedCulling.UsesExpandedVirtualRefs ? "expanded" : "direct")}, " +
                $"pagePool={(sceneVisibilityBackend != null && sceneVisibilityBackend.IsPagePoolReady ? "ready" : "compat")}:" +
                $"{(sceneVisibilityBackend != null ? sceneVisibilityBackend.ResidentPageCount : 0)}/" +
                $"{(sceneVisibilityBackend != null ? sceneVisibilityBackend.GlobalPageCount : 0)} " +
                $"pinned={(sceneVisibilityBackend != null ? sceneVisibilityBackend.PinnedPageCount : 0)}, " +
                $"requests={(sceneVisibilityBackend != null ? sceneVisibilityBackend.LastRequestedPageCount : 0)}/" +
                $"{(sceneVisibilityBackend != null ? sceneVisibilityBackend.LastQueuedPageCount : 0)} " +
                $"priority={(sceneVisibilityBackend != null ? sceneVisibilityBackend.LastMaxPageRequestPriority : 0)}, " +
                $"bevyTwoPhase={IsBevyTwoPhaseEnabled()}, " +
                $"readback={(gpuMask ? 0 : 1)}, hzbConfigured={IsHzbActiveForScene()}, " +
                $"previousHzbThisDispatch={hzbActive}.");
        }

        void LogBevyCullStatsIfNeeded(string stage, Camera camera)
        {
            if (!settings.logStats || batchedCulling == null || Time.frameCount % 30 != 0)
                return;
            int compactTris = -1;
            int meshTris = sceneVisibilityBackend != null ? sceneVisibilityBackend.TriangleCount : -1;
            if (sceneVisibilityBackend != null && HasValidCompactForSelection(kCompactSelectionMerged, camera))
                compactTris = sceneVisibilityBackend.TryGetCompactedTriangleCount();
            else if (sceneVisibilityBackend != null && HasValidCompactForSelection(kCompactSelectionFirst, camera))
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

        void LogAutomaticWorkloadStats(Camera camera)
        {
            if (camera == null || camera.cameraType != CameraType.Game || batchedCulling == null)
                return;
            int sampleFrame = batchedCulling.LastCullStatsReadbackFrame;
            if (sampleFrame < 0 || sampleFrame == lastAutomaticCullStatsFrame)
                return;
            lastAutomaticCullStatsFrame = sampleFrame;

            int mainClusters = Mathf.Max(0, batchedCulling.LastCull1Drawn);
            int hzbRejected = Mathf.Max(0, batchedCulling.LastCull2Candidates);
            int postRecovered = Mathf.Max(0, batchedCulling.LastCull2Drawn);
            Debug.Log(
                $"[Nanite][Workload] frame={sampleFrame} " +
                $"cameraMainClusters={mainClusters}, hzbRejected={hzbRejected}, " +
                $"postRecovered={postRecovered}, finalClusters={mainClusters + postRecovered}, " +
                $"hzbAdmission={(hzbAdmissionAllowed ? "active" : "cooldown")}.");
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
            if (!NaniteDebugVisualization.IsEnabled || camera == null)
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
            if (!NaniteDebugVisualization.IsEnabled || cmd == null || material == null || camera == null)
                return;

            int pass = GetFormalResolvePassIndex(material, kPassDebugColorViz, kDebugColorVizPassFallback);
            float mode = (float)NaniteDebugVisualization.ActiveMode;
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
                BindCompactDrawState(cmd, kCompactSelectionMerged, camera);
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
                Debug.Log($"[Nanite][RF] Debug visualization active: mode={NaniteDebugVisualization.ActiveMode}, draws={drawCalls}");
            }
        }

        void ExecuteDebugVisualization(RasterCommandBuffer cmd, Material material, Camera camera)
        {
            if (!NaniteDebugVisualization.IsEnabled || cmd == null || material == null || camera == null)
                return;

            int pass = GetFormalResolvePassIndex(material, kPassDebugColorViz, kDebugColorVizPassFallback);
            float mode = (float)NaniteDebugVisualization.ActiveMode;
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
                if (!HasValidCompactForSelection(kCompactSelectionMerged, camera))
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
                BindCompactDrawState(cmd, kCompactSelectionMerged, camera);
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
                Debug.Log($"[Nanite][RF] Debug visualization active: mode={NaniteDebugVisualization.ActiveMode}, draws={drawCalls}");
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

        void ExecuteDebugVBufferResolve(
            RasterCommandBuffer cmd,
            Material material,
            Camera camera,
            TextureHandle vbuffer,
            int screenWidth,
            int screenHeight)
        {
            if (material == null ||
                !TryBindFormalResolveScene(
                    cmd,
                    camera,
                    vbuffer,
                    screenWidth,
                    screenHeight,
                    screenWidth,
                    screenHeight,
                    out _))
                return;

            ConfigureFormalResolveMaterialKeywords(
                material,
                false,
                CanUseCompactFormalVBuffer());
            cmd.SetGlobalFloat(ShaderIds.DebugVizMode, (float)NaniteDebugVisualization.ActiveMode);
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
            cmd.SetComputeIntParam(settings.hzbBuilderShader, ShaderIds.ReversedZ, SystemInfo.usesReversedZBuffer ? 1 : 0);
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
            cmd.SetComputeIntParam(settings.hzbBuilderShader, ShaderIds.ReversedZ, SystemInfo.usesReversedZBuffer ? 1 : 0);
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

        static void Dispatch2D(ComputeCommandBuffer cmd, ComputeShader cs, int kernel, int width, int height, int tx, int ty)
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

        bool IsHzbHistoryCompatible(CameraHzbState state, Camera camera)
        {
            if (state == null || camera == null || !state.historyViewValid)
                return false;
            if (sceneVisibilityBackend == null || !sceneVisibilityBackend.IsReady)
                return false;
            if (state.historyGeometryGeneration != sceneVisibilityBackend.GeometryGeneration ||
                state.historyTransformGeneration != sceneVisibilityBackend.InstanceTransformGeneration ||
                state.historyRegistryRevision != NaniteRuntimeRegistry.Revision)
                return false;

            // Camera motion is valid: Pass 1 uses the stored previous view and
            // Pass 2 recovers disocclusions against current depth/current view.
            return state.historyHzbView.valid;
        }

        void CommitHzbHistory(CameraHzbState state, Camera camera)
        {
            if (state == null || camera == null ||
                !state.currentHzbValid || state.builtFrame != Time.frameCount ||
                sceneVisibilityBackend == null || !sceneVisibilityBackend.IsReady)
            {
                if (state != null)
                {
                    state.hasPrevious = false;
                    state.historyViewValid = false;
                    state.historyHzbView = default;
                }
                return;
            }

            state.historyViewProjection =
                GL.GetGPUProjectionMatrix(camera.projectionMatrix, true) * camera.worldToCameraMatrix;
            state.historyHzbView =
                NaniteGpuBatchedCullingBackend.HzbViewParameters.FromCamera(camera);
            state.historyTransformGeneration = sceneVisibilityBackend.InstanceTransformGeneration;
            state.historyGeometryGeneration = sceneVisibilityBackend.GeometryGeneration;
            state.historyRegistryRevision = NaniteRuntimeRegistry.Revision;
            state.historyViewValid = true;
            SwapHistory(state);
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
            state.historyViewValid = false;
            state.historyHzbView = default;
            state.historyTransformGeneration = -1;
            state.historyGeometryGeneration = -1;
            state.historyRegistryRevision = -1;
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
            {
                ConfigureRasterCullMode(settings.vbufferPreviewMaterial);
                return settings.vbufferPreviewMaterial;
            }

            if (runtimeVBufferPreviewMaterial != null)
            {
                ConfigureRasterCullMode(runtimeVBufferPreviewMaterial);
                return runtimeVBufferPreviewMaterial;
            }

            var shader = Shader.Find("Nanite/VBufferPacketRaster");
            if (shader == null)
                return null;
            runtimeVBufferPreviewMaterial = new Material(shader) { name = "Nanite_VBufferPacketRaster_Runtime" };
            ConfigurePackedDirectDiagnosticKeyword(runtimeVBufferPreviewMaterial);
            ConfigureRasterCullMode(runtimeVBufferPreviewMaterial);
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

        Material EnsureVBufferDebugResolveMaterial()
        {
            if (runtimeVBufferDebugResolveMaterial != null)
                return runtimeVBufferDebugResolveMaterial;

            var shader = Shader.Find("Nanite/VBufferDebugResolve");
            if (shader == null)
                return null;
            runtimeVBufferDebugResolveMaterial = new Material(shader)
            {
                name = "Nanite_VBufferDebugResolve_Runtime"
            };
            return runtimeVBufferDebugResolveMaterial;
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
            {
                ConfigureRasterCullMode(runtimeShadowCasterMaterial);
                return runtimeShadowCasterMaterial;
            }

            var shader = Shader.Find("Nanite/VBufferShadowCaster");
            if (shader == null)
                return null;
            runtimeShadowCasterMaterial = new Material(shader) { name = "Nanite_VBufferShadowCaster_Runtime" };
            ConfigurePackedDirectDiagnosticKeyword(runtimeShadowCasterMaterial);
            ConfigureRasterCullMode(runtimeShadowCasterMaterial);
            return runtimeShadowCasterMaterial;
        }

        void ConfigureRasterCullMode(Material material)
        {
            if (material == null || !material.HasProperty("_NaniteCullMode"))
                return;

            bool requiresTwoSided = sceneVisibilityBackend == null ||
                                    sceneVisibilityBackend.RequiresTwoSidedRaster;
            material.SetFloat(
                "_NaniteCullMode",
                (float)(requiresTwoSided ? CullMode.Off : CullMode.Back));
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
            if (runtimeVBufferDebugResolveMaterial != null)
            {
                DestroyObject(runtimeVBufferDebugResolveMaterial);
                runtimeVBufferDebugResolveMaterial = null;
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
            tileMaterialBinListBuffer?.Release();
            tileIndirectArgsBuffer?.Release();
            tileMaterialMaskBuffer = null;
            tileMaterialBinListBuffer = null;
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
