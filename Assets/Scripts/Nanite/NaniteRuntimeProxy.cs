using System;
using System.Collections.Generic;
using UnityEngine;

namespace Nanite
{
    public enum NaniteDebugColorMode
    {
        Single = 0,
        ByCluster = 1,
        ByPart = 2,
        ByMip = 3,
        ByPage = 4,
        VBuffer = 5
    }

    /// <summary>
    /// 运行时 Nanite 资源代理：直接引用 NaniteMesh 资源并执行 BVH 剔除。
    /// 这里只做可见性计算与统计，渲染管线可在此基础上接入 Compute/Draw。
    /// </summary>
    [ExecuteAlways]
    public class NaniteRuntimeProxy : MonoBehaviour
    {
        [Header("Runtime Load")]
        public bool autoLoadFromResources = false;
        public string resourcesPath = "";

        [Header("Runtime Culling Backend")]
        [Tooltip("仅 Proxy 自驱路径生效。RendererFeature 驱动时由 Feature.gpuCullingShader / 全局 GPU mask 决定，本开关无效。")]
        public bool useGpuCulling = false;
        public ComputeShader gpuCullingShader;
        public bool externalCullingDriven = false;
        public bool uploadSelectionToGpu = true;

        public NaniteMesh naniteMesh;
        public Camera targetCamera;
        [Tooltip("屏幕空间误差阈值（约像素）。\n" +
                 "更小(0.5~2)→近处更细；更大(4~8)→整体变糙、cluster 掉更快。\n" +
                 "用 8 测远处掉 cluster 时，近处变糙是预期现象。")]
        [Min(0.01f)] public float lodErrorPixels = 1.0f;
        [Tooltip("仅 Proxy 自驱路径生效。Feature 驱动时用 Feature.useBvhCandidates，本开关无效。")]
        public bool useBvh = true;

        [Header("Pipeline Admission")]
        [Tooltip("始终进入虚拟几何管线；用于低面模型对比或强制 Nanite。")]
        public bool forceNaniteRendering = false;
        [Tooltip("始终交给普通 MeshRenderer/SRP 路径。两个 Force 同时开启属于冲突配置，运行时按自动准入处理。")]
        public bool forceRasterRendering = false;
        [Tooltip("低复杂度回退使用的原始 Mesh。留空时优先使用 NaniteMesh.sourceMesh，其次捕获当前 MeshFilter。")]
        public Mesh rasterFallbackMesh;

        [Header("Resolve Materials")]
        [Tooltip("可选：覆盖 VBuffer→GBuffer Resolve 使用的材质。留空则从 Renderer.sharedMaterials 读取；仍无则使用 URP Lit 回退。")]
        public Material[] resolveMaterials;

        [Header("Debug Render")]
        [Tooltip("仅调试用。Formal/RendererFeature 驱动时应关闭，否则每帧 RebuildDebugMesh 会严重卡顿。")]
        public bool renderVisibleMesh = false;
        public bool showAllClusters = false;
        public Material debugMaterial;
        public NaniteDebugColorMode debugColorMode = NaniteDebugColorMode.ByCluster;
        [Range(0f, 1f)] public float debugColorSaturation = 0.72f;
        [Range(0f, 1f)] public float debugColorValue = 0.95f;
        public bool drawPartBounds = false;
        public bool drawClusterBounds = false;
        public bool drawOnlyVisibleClusterBounds = true;
        public float boundsSphereScale = 1.0f;

        [Header("Debug")]
        [Tooltip("Proxy 自驱路径的可见数。Feature 驱动时本地 selection 常为空，请看 lastGpuVisibleApprox。")]
        public int visibleClusterCount;
        [Tooltip("Feature/GPU mask 回写的场景级近似可见 cluster 数（非本 Proxy 独占）。")]
        public int lastGpuVisibleApprox;
        public int visiblePageCount;
        public int testedInstanceCount;
        public int testedNodeCount;
        public int testedPartCount;
        public int testedClusterCount;

        public void SetFeatureDrivenVisibleApprox(int approx)
        {
            lastGpuVisibleApprox = approx;
            // Inspector 友好：Feature 驱动时把近似值映到 visibleClusterCount，避免误读为 0=BVH 没工作。
            if (NaniteRuntimeRegistry.IsFeatureDriving)
                visibleClusterCount = approx;
        }

        readonly List<NaniteVisibleClusterRef> visible = new List<NaniteVisibleClusterRef>(4096);
        readonly List<NaniteVisibleClusterRef> gpuVisible = new List<NaniteVisibleClusterRef>(4096);
        readonly NaniteRuntimeSelection runtimeSelection = new NaniteRuntimeSelection();
        readonly List<Vector3> debugVertices = new List<Vector3>(65536);
        readonly List<int> debugIndices = new List<int>(65536);
        readonly List<Vector2> debugUv = new List<Vector2>(65536);
        readonly List<Vector4> debugUv1 = new List<Vector4>(65536);
        readonly List<Color32> debugColors = new List<Color32>(65536);

        Mesh debugMesh;
        MeshFilter debugMeshFilter;
        MeshRenderer debugMeshRenderer;
        Mesh capturedRasterFallbackMesh;
        Mesh admissionCountMesh;
        int admissionTriangleCount;
        int admissionDrawCount;
        bool rasterFallbackActive;
        NaniteGpuCullingBackend gpuBackend;
        NaniteMesh gpuBackendMesh;
        ComputeShader gpuBackendShader;
        NaniteMesh failedGpuBackendMesh;
        ComputeShader failedGpuBackendShader;
        NaniteMesh pageGpuMesh;
        PageGpuData[] pageGpuData;
        NaniteMesh mergedGpuMesh;
        MergedGpuData mergedGpuData;
        ComputeBuffer selectionPacketBuffer;
        ComputeBuffer selectionPageRangeBuffer;
        int selectionPacketCapacity;
        int selectionPageRangeCapacity;
        int externalSelectionFrame = -100000;

        class PageGpuData
        {
            public ComputeBuffer vertexData;
            public ComputeBuffer indices;
            public ComputeBuffer triangleCluster;
            public ComputeBuffer clusterVisible;
            public uint[] clusterVisibleCpu;
            public int vertexStride;
            public int vertexCount;
            public int indexCount;
            public int triangleCount;
            public int clusterCount;
        }

        class MergedGpuData
        {
            public ComputeBuffer vertexData;
            public ComputeBuffer indices;
            public ComputeBuffer triangleCluster;
            public ComputeBuffer trianglePage;
            public ComputeBuffer triangleSubMesh;
            public ComputeBuffer clusterVisible;
            public ComputeBuffer drawArgs;
            public uint[] clusterVisibleCpu;
            public int[] pageClusterBase;
            public int[] pageClusterCount;
            public int vertexStride;
            public int vertexCount;
            public int indexCount;
            public int triangleCount;
            public int clusterCount;
        }

        public IReadOnlyList<NaniteVisibleClusterRef> VisibleClusters => visible;
        public NaniteRuntimeSelection RuntimeSelection => runtimeSelection;
        public ComputeBuffer VisiblePacketBuffer => selectionPacketBuffer;
        public ComputeBuffer VisiblePageRangeBuffer => selectionPageRangeBuffer;
        public int VisiblePacketCount => runtimeSelection.packets.Count;
        public int VisiblePageRangeCount => runtimeSelection.pageRanges.Count;
        public bool NaniteRenderingActive => !rasterFallbackActive;
        public bool RasterFallbackActive => rasterFallbackActive;
        public bool ForceNaniteRequested => forceNaniteRendering && !forceRasterRendering;
        public bool ForceRasterRequested => forceRasterRendering && !forceNaniteRendering;

        public int RasterFallbackTriangleCount
        {
            get
            {
                Mesh mesh = ResolveRasterFallbackMesh();
                if (mesh == null)
                    return naniteMesh != null ? Mathf.Max(0, naniteMesh.sourceTriangleCount) : 0;
                if (admissionCountMesh == mesh)
                    return admissionTriangleCount;

                long indexCount = 0;
                for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
                    indexCount += (long)mesh.GetIndexCount(subMesh);
                admissionCountMesh = mesh;
                admissionTriangleCount = (int)Math.Min(int.MaxValue, indexCount / 3L);
                admissionDrawCount = Mathf.Max(1, mesh.subMeshCount);
                return admissionTriangleCount;
            }
        }

        public int RasterFallbackDrawCount
        {
            get
            {
                Mesh mesh = ResolveRasterFallbackMesh();
                if (mesh == null)
                    return 0;
                if (admissionCountMesh != mesh)
                    _ = RasterFallbackTriangleCount;
                return Mathf.Max(1, admissionDrawCount);
            }
        }

        public bool CanUseRasterFallback =>
            ResolveRasterFallbackMesh() != null &&
            GetComponent<MeshFilter>() != null &&
            GetComponent<MeshRenderer>() != null;

        public bool NativeRendererRequested
        {
            get
            {
                if (ForceNaniteRequested)
                    return false;
                if (ForceRasterRequested)
                    return true;
                // The admission system itself enables this renderer. Do not
                // interpret that state as a persistent user request, otherwise
                // a proxy can never return to Nanite when scene density grows.
                if (rasterFallbackActive)
                    return false;
                if (renderVisibleMesh)
                    return false;
                MeshRenderer renderer = GetComponent<MeshRenderer>();
                return renderer != null && renderer.enabled;
            }
        }

        void Awake()
        {
            TryAutoLoadNaniteMesh();
            CaptureRasterFallbackMesh();
            EnsureDebugRenderer();
        }

        void OnEnable()
        {
            TryAutoLoadNaniteMesh();
            CaptureRasterFallbackMesh();
            EnsureDebugRenderer();
            NaniteRuntimeRegistry.Register(this);
        }

        void LateUpdate()
        {
            // Once the RendererFeature owns the scene, production proxies are
            // passive registry records. Per-object maintenance here would turn
            // the GPU-driven scene back into O(instanceCount) script work.
            if (NaniteRuntimeRegistry.IsFeatureDriving && !renderVisibleMesh)
                return;

            TryAutoLoadNaniteMesh();

            if (rasterFallbackActive)
            {
                visible.Clear();
                runtimeSelection.Clear();
                visibleClusterCount = 0;
                visiblePageCount = 0;
                testedNodeCount = 0;
                testedPartCount = 0;
                testedClusterCount = 0;
                testedInstanceCount = 0;
                return;
            }

            // Feature 驱动时必须最先返回：禁止 UploadSelectionBuffers / 重复剔除。
            // Proxy 上 Use Bvh / Use Gpu Culling 此时不参与（由 RendererFeature 全局路径决定）。
            if (NaniteRuntimeRegistry.IsFeatureDriving)
            {
                // Feature 驱动：Use Bvh / Use Gpu Culling / 本地 VisibleClusterCount 不参与真路径。
                // lastGpuVisibleApprox 由 Feature 回写；勿用空 selection 覆盖。
                if (lastGpuVisibleApprox > 0)
                    visibleClusterCount = lastGpuVisibleApprox;
                visiblePageCount = runtimeSelection.pageRanges.Count;
                testedNodeCount = runtimeSelection.stats.testedNodes;
                testedPartCount = runtimeSelection.stats.testedParts;
                testedClusterCount = runtimeSelection.stats.testedClusters;
                testedInstanceCount = runtimeSelection.stats.testedInstances;

                if (renderVisibleMesh)
                {
                    visible.Clear();
                    visible.AddRange(runtimeSelection.visibleClusters);
                    RebuildDebugMesh();
                }
                else
                    ClearDebugMesh();
                return;
            }

            // The per-Proxy culling backend is a debug-mesh producer, not a
            // production fallback. Running it while the central GPU Scene is
            // still being created duplicates traversal and buffer uploads once
            // per instance. Production proxies therefore stay passive unless
            // their explicit debug mesh is requested.
            if (!renderVisibleMesh)
            {
                visible.Clear();
                runtimeSelection.Clear();
                visibleClusterCount = 0;
                visiblePageCount = 0;
                testedNodeCount = 0;
                testedPartCount = 0;
                testedClusterCount = 0;
                testedInstanceCount = 0;
                ReleaseSelectionBuffers();
                return;
            }

            bool externalFresh = externalCullingDriven && externalSelectionFrame >= (Time.frameCount - 2);
            if (externalFresh)
            {
                visible.Clear();
                visible.AddRange(runtimeSelection.visibleClusters);

                visibleClusterCount = runtimeSelection.stats.visibleClusters;
                visiblePageCount = runtimeSelection.pageRanges.Count;
                testedNodeCount = runtimeSelection.stats.testedNodes;
                testedPartCount = runtimeSelection.stats.testedParts;
                testedClusterCount = runtimeSelection.stats.testedClusters;
                testedInstanceCount = runtimeSelection.stats.testedInstances;

                // ApplyExternalSelection 已上传过；此处不再 SetData。
                if (renderVisibleMesh)
                    RebuildDebugMesh();
                else
                    ClearDebugMesh();
                return;
            }
            if (externalCullingDriven && !externalFresh)
                externalCullingDriven = false;

            var cam = targetCamera != null ? targetCamera : Camera.main;
            if (naniteMesh == null || cam == null)
            {
                visible.Clear();
                runtimeSelection.Clear();
                ClearDebugMesh();
                visibleClusterCount = 0;
                visiblePageCount = 0;
                testedNodeCount = 0;
                testedPartCount = 0;
                testedClusterCount = 0;
                testedInstanceCount = 0;
                ReleaseSelectionBuffers();
                return;
            }

            float maxScale = Mathf.Max(
                Mathf.Abs(transform.lossyScale.x),
                Mathf.Abs(transform.lossyScale.y),
                Mathf.Abs(transform.lossyScale.z));

            bool usedGpu = false;
            if (useGpuCulling && gpuCullingShader != null && EnsureGpuBackend())
            {
                if (gpuBackend.Run(
                        cam,
                        lodErrorPixels,
                        transform.localToWorldMatrix,
                        maxScale,
                        gpuVisible,
                        out var gpuStats))
                {
                    NaniteRuntimeCulling.BuildSelectionFromVisibleClusters(
                        naniteMesh,
                        gpuVisible,
                        gpuStats,
                        runtimeSelection);
                    usedGpu = true;
                }
            }

            if (!usedGpu)
            {
                NaniteRuntimeCulling.BuildSelection(
                    naniteMesh,
                    cam,
                    lodErrorPixels,
                    useBvh,
                    transform.localToWorldMatrix,
                    maxScale,
                    runtimeSelection);
            }
            runtimeSelection.instanceLocalToWorld = transform.localToWorldMatrix;

            visible.Clear();
            visible.AddRange(runtimeSelection.visibleClusters);

            visibleClusterCount = runtimeSelection.stats.visibleClusters;
            visiblePageCount = runtimeSelection.pageRanges.Count;
            testedNodeCount = runtimeSelection.stats.testedNodes;
            testedPartCount = runtimeSelection.stats.testedParts;
            testedClusterCount = runtimeSelection.stats.testedClusters;
            testedInstanceCount = runtimeSelection.stats.testedInstances;

            StampPacketInstanceId();
            UploadSelectionBuffers();

            if (renderVisibleMesh)
                RebuildDebugMesh();
            else
                ClearDebugMesh();
        }

        public bool TryComputeSelectionForCamera(
            Camera cam,
            Texture hzbTexture,
            int hzbMipCount,
            bool useHzb,
            NaniteRuntimeSelection outputSelection)
        {
            outputSelection.Clear();
            if (naniteMesh == null || cam == null)
                return false;

            outputSelection.instanceLocalToWorld = transform.localToWorldMatrix;

            float maxScale = Mathf.Max(
                Mathf.Abs(transform.lossyScale.x),
                Mathf.Abs(transform.lossyScale.y),
                Mathf.Abs(transform.lossyScale.z));

            bool usedGpu = false;
            if (useGpuCulling && gpuCullingShader != null && EnsureGpuBackend())
            {
                if (gpuBackend.Run(
                        cam,
                        lodErrorPixels,
                        transform.localToWorldMatrix,
                        maxScale,
                        gpuVisible,
                        out var gpuStats,
                        hzbTexture,
                        hzbMipCount,
                        useHzb))
                {
                    NaniteRuntimeCulling.BuildSelectionFromVisibleClusters(
                        naniteMesh,
                        gpuVisible,
                        gpuStats,
                        outputSelection);
                    usedGpu = true;
                }
            }

            if (!usedGpu)
            {
                NaniteRuntimeCulling.BuildSelection(
                    naniteMesh,
                    cam,
                    lodErrorPixels,
                    useBvh,
                    transform.localToWorldMatrix,
                    maxScale,
                    outputSelection);
            }

            return true;
        }

        public void ApplyExternalSelection(NaniteRuntimeSelection selection)
        {
            runtimeSelection.Clear();
            runtimeSelection.stats = selection.stats;
            runtimeSelection.visibleClusters.AddRange(selection.visibleClusters);
            runtimeSelection.packets.AddRange(selection.packets);
            runtimeSelection.pageRanges.AddRange(selection.pageRanges);
            runtimeSelection.instanceLocalToWorld = selection.instanceLocalToWorld;

            visible.Clear();
            visible.AddRange(runtimeSelection.visibleClusters);

            visibleClusterCount = runtimeSelection.stats.visibleClusters;
            visiblePageCount = runtimeSelection.pageRanges.Count;
            testedNodeCount = runtimeSelection.stats.testedNodes;
            testedPartCount = runtimeSelection.stats.testedParts;
            testedClusterCount = runtimeSelection.stats.testedClusters;
            testedInstanceCount = runtimeSelection.stats.testedInstances;

            externalCullingDriven = true;
            externalSelectionFrame = Time.frameCount;

            // Scene VBuffer / Feature 路径不需要 per-proxy selection buffer；渲染中途 SetData 会制造 GPU sync。
            if (NaniteRuntimeRegistry.IsFeatureDriving)
                return;

            StampPacketInstanceId();
            UploadSelectionBuffers();
        }

        void OnValidate()
        {
            TryAutoLoadNaniteMesh();
            CaptureRasterFallbackMesh();
            if (!useGpuCulling || gpuCullingShader == null)
                DisposeGpuBackend();
            NaniteRuntimeRegistry.NotifyRenderDataChanged(this);
        }

        public bool SetRasterFallbackActive(bool enabled)
        {
            CaptureRasterFallbackMesh();
            Mesh source = ResolveRasterFallbackMesh();
            bool next = enabled && !ForceNaniteRequested && source != null &&
                        debugMeshFilter != null && debugMeshRenderer != null;
            if (rasterFallbackActive == next)
            {
                if (next)
                {
                    debugMeshFilter.sharedMesh = source;
                    debugMeshRenderer.enabled = true;
                }
                return next;
            }

            rasterFallbackActive = next;
            if (next)
            {
                ClearDebugMesh();
                debugMeshFilter.sharedMesh = source;
                debugMeshRenderer.enabled = true;
                debugMeshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            }
            else if (debugMeshRenderer != null && !renderVisibleMesh)
            {
                debugMeshRenderer.enabled = false;
            }

            NaniteRuntimeRegistry.NotifyRenderDataChanged(this);
            return rasterFallbackActive;
        }

        /// <summary>Call after changing naniteMesh or resolveMaterials from script.</summary>
        public void MarkRenderDataDirty()
        {
            NaniteRuntimeRegistry.NotifyRenderDataChanged(this);
        }

        void OnDisable()
        {
            ClearDebugMesh();
            DisposeGpuBackend();
            ReleasePageGpuBuffers();
            ReleaseMergedGpuBuffers();
            ReleaseSelectionBuffers();
            NaniteRuntimeRegistry.Unregister(this);
        }

        void OnDestroy()
        {
            DisposeGpuBackend();
            ReleasePageGpuBuffers();
            ReleaseMergedGpuBuffers();
            ReleaseSelectionBuffers();
            NaniteRuntimeRegistry.Unregister(this);
        }

        void OnDrawGizmosSelected()
        {
            if (naniteMesh == null || naniteMesh.pageArray == null)
                return;

            float maxScale = Mathf.Max(
                Mathf.Abs(transform.lossyScale.x),
                Mathf.Abs(transform.lossyScale.y),
                Mathf.Abs(transform.lossyScale.z));

            if (drawPartBounds)
            {
                Gizmos.color = new Color(1f, 0.8f, 0.2f, 0.75f);
                for (int p = 0; p < naniteMesh.pageArray.Length; p++)
                {
                    var page = naniteMesh.pageArray[p];
                    if (page == null || page.parts == null)
                        continue;

                    for (int i = 0; i < page.parts.Length; i++)
                    {
                        Vector4 s = TransformSphere(page.parts[i].selfSphere, maxScale);
                        Gizmos.DrawWireSphere(new Vector3(s.x, s.y, s.z), s.w * boundsSphereScale);
                    }
                }
            }

            if (drawClusterBounds)
            {
                Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.45f);
                if (drawOnlyVisibleClusterBounds)
                {
                    for (int i = 0; i < visible.Count; i++)
                    {
                        var vr = visible[i];
                        if (vr.pageIndex < 0 || vr.pageIndex >= naniteMesh.pageArray.Length)
                            continue;

                        var page = naniteMesh.pageArray[vr.pageIndex];
                        if (page == null || page.clusterArray == null || vr.clusterIndex < 0 || vr.clusterIndex >= page.clusterArray.Length)
                            continue;

                        NaniteCluster cluster = page.clusterArray[vr.clusterIndex];
                        Vector4 s = TransformSphere(
                            cluster.geometrySphere.w > 0f ? cluster.geometrySphere : cluster.selfSphere,
                            maxScale);
                        Gizmos.DrawWireSphere(new Vector3(s.x, s.y, s.z), s.w * boundsSphereScale);
                    }
                }
                else
                {
                    for (int p = 0; p < naniteMesh.pageArray.Length; p++)
                    {
                        var page = naniteMesh.pageArray[p];
                        if (page == null || page.clusterArray == null)
                            continue;

                        for (int i = 0; i < page.clusterArray.Length; i++)
                        {
                            NaniteCluster cluster = page.clusterArray[i];
                            Vector4 s = TransformSphere(
                                cluster.geometrySphere.w > 0f ? cluster.geometrySphere : cluster.selfSphere,
                                maxScale);
                            Gizmos.DrawWireSphere(new Vector3(s.x, s.y, s.z), s.w * boundsSphereScale);
                        }
                    }
                }
            }
        }

        void RebuildDebugMesh()
        {
            EnsureDebugRenderer();
            if (debugMesh == null || debugMeshFilter == null)
                return;

            debugVertices.Clear();
            debugIndices.Clear();
            debugUv.Clear();
            debugUv1.Clear();
            debugColors.Clear();

            if (showAllClusters)
            {
                for (int p = 0; p < naniteMesh.pageArray.Length; p++)
                {
                    var page = naniteMesh.pageArray[p];
                    if (page == null || page.clusterArray == null)
                        continue;

                    var partLookup = BuildPartLookup(page);
                    for (int ci = 0; ci < page.clusterArray.Length; ci++)
                    {
                        int key = ColorKeyForCluster(page, p, ci, partLookup);
                        AppendClusterTriangles(page, p, ci, ColorFromKey(key));
                    }
                }
            }
            else
            {
                var pagePartLookups = debugColorMode == NaniteDebugColorMode.ByPart
                    ? new Dictionary<int, int[]>()
                    : null;

                for (int i = 0; i < visible.Count; i++)
                {
                    var vr = visible[i];
                    if (vr.pageIndex < 0 || vr.pageIndex >= naniteMesh.pageArray.Length)
                        continue;

                    var page = naniteMesh.pageArray[vr.pageIndex];
                    if (page == null || page.clusterArray == null)
                        continue;
                    if (vr.clusterIndex < 0 || vr.clusterIndex >= page.clusterArray.Length)
                        continue;

                    int[] partLookup = null;
                    if (pagePartLookups != null)
                    {
                        if (!pagePartLookups.TryGetValue(vr.pageIndex, out partLookup))
                        {
                            partLookup = BuildPartLookup(page);
                            pagePartLookups.Add(vr.pageIndex, partLookup);
                        }
                    }
                    int key = ColorKeyForCluster(page, vr.pageIndex, vr.clusterIndex, partLookup);
                    AppendClusterTriangles(page, vr.pageIndex, vr.clusterIndex, ColorFromKey(key));
                }
            }

            debugMesh.Clear();
            if (debugVertices.Count == 0)
            {
                debugMeshFilter.sharedMesh = debugMesh;
                return;
            }

            if (debugVertices.Count > 65535)
                debugMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            else
                debugMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt16;

            debugMesh.SetVertices(debugVertices);
            debugMesh.SetUVs(0, debugUv);
            debugMesh.SetUVs(1, debugUv1);
            debugMesh.SetColors(debugColors);
            debugMesh.SetTriangles(debugIndices, 0);
            debugMesh.RecalculateBounds();
            debugMeshFilter.sharedMesh = debugMesh;
        }

        void AppendClusterTriangles(NaniteMeshPage page, int pageIndex, int clusterIndex, Color32 color)
        {
            if (page == null || page.clusterArray == null || page.indiceArray == null || page.vertexData == null)
                return;
            if (clusterIndex < 0 || clusterIndex >= page.clusterArray.Length)
                return;

            ref readonly var cluster = ref page.clusterArray[clusterIndex];
            int start = cluster.indiceIndex;
            int end = start + cluster.indiceCount;
            int stride = page.vertexStride;
            if (stride < 3)
                return;

            for (int k = start; k < end; k++)
            {
                int vi = page.indiceArray[k];
                int vo = vi * stride;
                if (vo + 2 >= page.vertexData.Length)
                    continue;

                debugVertices.Add(new Vector3(
                    page.vertexData[vo + 0],
                    page.vertexData[vo + 1],
                    page.vertexData[vo + 2]));

                if (vo + 4 < page.vertexData.Length)
                    debugUv.Add(new Vector2(page.vertexData[vo + 3], page.vertexData[vo + 4]));
                else
                    debugUv.Add(Vector2.zero);

                int triLocal = (k - start) / 3;
                debugUv1.Add(new Vector4(clusterIndex, pageIndex, triLocal, 0f));
                debugColors.Add(color);
                debugIndices.Add(debugVertices.Count - 1);
            }
        }

        int[] BuildPartLookup(NaniteMeshPage page)
        {
            if (debugColorMode != NaniteDebugColorMode.ByPart || page.clusterArray == null || page.parts == null)
                return null;

            var lookup = new int[page.clusterArray.Length];
            for (int i = 0; i < lookup.Length; i++)
                lookup[i] = -1;

            for (int p = 0; p < page.parts.Length; p++)
            {
                int start = page.parts[p].clusterStart;
                int end = start + page.parts[p].clusterCount;
                if (start < 0 || end > lookup.Length)
                    continue;

                for (int i = start; i < end; i++)
                    lookup[i] = p;
            }

            return lookup;
        }

        int ColorKeyForCluster(NaniteMeshPage page, int pageIndex, int clusterIndex, int[] partLookup)
        {
            switch (debugColorMode)
            {
                case NaniteDebugColorMode.Single:
                    return 0;
                case NaniteDebugColorMode.ByCluster:
                    return (pageIndex + 1) * 73856093 ^ (clusterIndex + 1) * 19349663;
                case NaniteDebugColorMode.ByPart:
                    {
                        int partIndex = (partLookup != null && clusterIndex >= 0 && clusterIndex < partLookup.Length)
                            ? partLookup[clusterIndex]
                            : -1;
                        return (pageIndex + 1) * 83492791 ^ (partIndex + 1) * 689287499;
                    }
                case NaniteDebugColorMode.ByMip:
                    {
                        if (page.clusterMip != null && clusterIndex >= 0 && clusterIndex < page.clusterMip.Length)
                            return page.clusterMip[clusterIndex];
                        return 0;
                    }
                case NaniteDebugColorMode.ByPage:
                    return pageIndex;
                case NaniteDebugColorMode.VBuffer:
                    return (pageIndex + 1) * 83492791 ^ (clusterIndex + 1) * 19349663;
                default:
                    return clusterIndex;
            }
        }

        Color32 ColorFromKey(int key)
        {
            if (debugColorMode == NaniteDebugColorMode.Single)
                return new Color(0.7f, 0.85f, 1f, 1f);

            unchecked
            {
                uint hash = (uint)key * 2654435761u;
                float hue = (hash & 0x00ffffffu) / 16777215f;
                Color c = Color.HSVToRGB(hue, Mathf.Clamp01(debugColorSaturation), Mathf.Clamp01(debugColorValue));
                return c;
            }
        }

        void EnsureDebugRenderer()
        {
            if (debugMeshFilter == null)
                debugMeshFilter = GetComponent<MeshFilter>();
            if (debugMeshRenderer == null)
                debugMeshRenderer = GetComponent<MeshRenderer>();

            if (renderVisibleMesh)
            {
                if (debugMeshFilter == null)
                    debugMeshFilter = gameObject.AddComponent<MeshFilter>();
                if (debugMeshRenderer == null)
                    debugMeshRenderer = gameObject.AddComponent<MeshRenderer>();

                if (debugMesh == null)
                {
                    debugMesh = new Mesh { name = "NaniteRuntimeVisibleMesh" };
                    debugMesh.MarkDynamic();
                }

                if (debugMaterial == null)
                {
                    var shader = ResolveDebugShader();
                    if (shader == null)
                        shader = Shader.Find("Sprites/Default");
                    if (shader == null)
                        shader = Shader.Find("Unlit/Color");
                    if (shader != null)
                        debugMaterial = new Material(shader) { color = new Color(0.7f, 0.85f, 1f, 1f) };
                }
                else if (debugMaterial.shader != null && debugMaterial.shader.name == "Unlit/Color")
                {
                    var preferredShader = ResolveDebugShader();
                    if (preferredShader != null)
                        debugMaterial = new Material(preferredShader) { color = Color.white };
                }
                else if (debugColorMode == NaniteDebugColorMode.VBuffer && (debugMaterial.shader == null || debugMaterial.shader.name != "Nanite/VBufferPreview"))
                {
                    var vbufferShader = Shader.Find("Nanite/VBufferPreview");
                    if (vbufferShader != null)
                        debugMaterial = new Material(vbufferShader) { color = Color.white };
                }
                else if (debugColorMode != NaniteDebugColorMode.VBuffer && debugMaterial.shader != null && debugMaterial.shader.name == "Nanite/VBufferPreview")
                {
                    var vertexColorShader = Shader.Find("Nanite/DebugVertexColor");
                    if (vertexColorShader != null)
                        debugMaterial = new Material(vertexColorShader) { color = Color.white };
                }

                if (debugMaterial != null)
                    debugMeshRenderer.sharedMaterial = debugMaterial;
            }
            else if (debugMeshFilter != null && debugMeshFilter.sharedMesh == debugMesh)
            {
                debugMeshFilter.sharedMesh = null;
            }
        }

        void ClearDebugMesh()
        {
            // 已清空时不要每帧 Mesh.Clear()，否则 Profiler 会出现持续 UpdateBufferData_Request。
            if (debugMeshFilter != null && debugMeshFilter.sharedMesh == debugMesh)
                debugMeshFilter.sharedMesh = null;
            if (debugMesh != null && debugMesh.vertexCount > 0)
                debugMesh.Clear();
        }

        void CaptureRasterFallbackMesh()
        {
            if (debugMeshFilter == null)
                debugMeshFilter = GetComponent<MeshFilter>();
            if (debugMeshRenderer == null)
                debugMeshRenderer = GetComponent<MeshRenderer>();
            if (capturedRasterFallbackMesh == null && debugMeshFilter != null &&
                debugMeshFilter.sharedMesh != null && debugMeshFilter.sharedMesh != debugMesh)
                capturedRasterFallbackMesh = debugMeshFilter.sharedMesh;
        }

        Mesh ResolveRasterFallbackMesh()
        {
            if (rasterFallbackMesh != null)
                return rasterFallbackMesh;
            if (naniteMesh != null && naniteMesh.sourceMesh != null)
                return naniteMesh.sourceMesh;
            return capturedRasterFallbackMesh;
        }

        Vector4 TransformSphere(in Vector4 localSphere, float maxScale)
        {
            Vector3 worldCenter = transform.localToWorldMatrix.MultiplyPoint3x4(new Vector3(localSphere.x, localSphere.y, localSphere.z));
            float worldRadius = localSphere.w * Mathf.Max(1e-6f, maxScale);
            return new Vector4(worldCenter.x, worldCenter.y, worldCenter.z, worldRadius);
        }

        void TryAutoLoadNaniteMesh()
        {
            if (naniteMesh != null || !autoLoadFromResources || string.IsNullOrWhiteSpace(resourcesPath))
                return;

            naniteMesh = Resources.Load<NaniteMesh>(resourcesPath);
            if (naniteMesh == null)
                Debug.LogWarning($"[Nanite] Resources.Load 失败: {resourcesPath}");
        }

        bool EnsureGpuBackend()
        {
            // The shipping Nanite path is authored and validated for DX12. In a Null,
            // WebGPU or other unsupported device, FindKernel can report every stripped
            // variant once per proxy before the renderer feature's global gate runs.
            // Fail before touching the compute asset; CPU/debug fallback remains valid.
            if (SystemInfo.graphicsDeviceType !=
                    UnityEngine.Rendering.GraphicsDeviceType.Direct3D12 ||
                !SystemInfo.supportsComputeShaders)
            {
                DisposeGpuBackend();
                return false;
            }

            if (!useGpuCulling || gpuCullingShader == null || naniteMesh == null)
            {
                DisposeGpuBackend();
                return false;
            }

            if (gpuBackend == null)
                gpuBackend = new NaniteGpuCullingBackend();

            if (gpuBackendMesh != naniteMesh || gpuBackendShader != gpuCullingShader || !gpuBackend.IsReady)
            {
                // A missing/stripped compute variant is deterministic for the current
                // mesh/shader pair. Retrying FindKernel every LateUpdate produced an
                // unbounded log storm on unsupported or -nographics devices. A changed
                // asset reference or component lifecycle reset still permits recovery.
                if (failedGpuBackendMesh == naniteMesh &&
                    failedGpuBackendShader == gpuCullingShader)
                {
                    return false;
                }

                bool ok = gpuBackend.Initialize(naniteMesh, gpuCullingShader);
                if (!ok)
                {
                    gpuBackendMesh = null;
                    gpuBackendShader = null;
                    failedGpuBackendMesh = naniteMesh;
                    failedGpuBackendShader = gpuCullingShader;
                    return false;
                }

                gpuBackendMesh = naniteMesh;
                gpuBackendShader = gpuCullingShader;
                failedGpuBackendMesh = null;
                failedGpuBackendShader = null;
            }

            return true;
        }

        void DisposeGpuBackend()
        {
            gpuBackend?.Dispose();
            gpuBackend = null;
            gpuBackendMesh = null;
            gpuBackendShader = null;
            failedGpuBackendMesh = null;
            failedGpuBackendShader = null;
        }

        public bool EnsurePageGpuBuffers()
        {
            if (naniteMesh == null || naniteMesh.pageArray == null)
            {
                ReleasePageGpuBuffers();
                return false;
            }

            if (pageGpuMesh == naniteMesh && pageGpuData != null)
                return true;

            ReleasePageGpuBuffers();
            pageGpuData = new PageGpuData[naniteMesh.pageArray.Length];

            for (int i = 0; i < naniteMesh.pageArray.Length; i++)
            {
                var page = naniteMesh.pageArray[i];
                if (page == null || page.vertexData == null || page.indiceArray == null)
                    continue;
                if (page.vertexData.Length == 0 || page.indiceArray.Length == 0)
                    continue;

                var data = new PageGpuData
                {
                    vertexStride = Mathf.Max(3, page.vertexStride),
                    vertexCount = page.vertexCount,
                    indexCount = page.indiceArray.Length,
                    triangleCount = page.indiceArray.Length / 3,
                    clusterCount = page.clusterArray != null ? page.clusterArray.Length : 0
                };

                data.vertexData = new ComputeBuffer(page.vertexData.Length, sizeof(float), ComputeBufferType.Structured);
                data.indices = new ComputeBuffer(page.indiceArray.Length, sizeof(int), ComputeBufferType.Structured);
                data.vertexData.SetData(page.vertexData);
                data.indices.SetData(page.indiceArray);

                if (data.triangleCount > 0)
                {
                    var triCluster = BuildTriangleClusterMap(page, data.triangleCount);
                    data.triangleCluster = new ComputeBuffer(data.triangleCount, sizeof(int), ComputeBufferType.Structured);
                    data.triangleCluster.SetData(triCluster);
                }

                int visibleLen = Mathf.Max(1, data.clusterCount);
                data.clusterVisible = new ComputeBuffer(visibleLen, sizeof(uint), ComputeBufferType.Structured);
                data.clusterVisibleCpu = new uint[visibleLen];
                pageGpuData[i] = data;
            }

            pageGpuMesh = naniteMesh;
            return true;
        }

        public bool TryGetPageGpuData(
            int pageIndex,
            out ComputeBuffer vertexDataBuffer,
            out ComputeBuffer indexBuffer,
            out ComputeBuffer triangleClusterBuffer,
            out ComputeBuffer clusterVisibleBuffer,
            out int vertexStride,
            out int vertexCount,
            out int indexCount,
            out int triangleCount,
            out int clusterCount)
        {
            vertexDataBuffer = null;
            indexBuffer = null;
            triangleClusterBuffer = null;
            clusterVisibleBuffer = null;
            vertexStride = 0;
            vertexCount = 0;
            indexCount = 0;
            triangleCount = 0;
            clusterCount = 0;

            if (!EnsurePageGpuBuffers() || pageGpuData == null || pageIndex < 0 || pageIndex >= pageGpuData.Length)
                return false;

            var data = pageGpuData[pageIndex];
            if (data == null || data.vertexData == null || data.indices == null)
                return false;

            vertexDataBuffer = data.vertexData;
            indexBuffer = data.indices;
            triangleClusterBuffer = data.triangleCluster;
            clusterVisibleBuffer = data.clusterVisible;
            vertexStride = data.vertexStride;
            vertexCount = data.vertexCount;
            indexCount = data.indexCount;
            triangleCount = data.triangleCount;
            clusterCount = data.clusterCount;
            return true;
        }

        public void UpdatePageClusterVisibilityBuffers(NaniteRuntimeSelection selection)
        {
            if (!EnsurePageGpuBuffers() || pageGpuData == null)
                return;

            for (int i = 0; i < pageGpuData.Length; i++)
            {
                var data = pageGpuData[i];
                if (data?.clusterVisibleCpu == null)
                    continue;
                System.Array.Clear(data.clusterVisibleCpu, 0, data.clusterVisibleCpu.Length);
            }

            if (selection != null && selection.packets != null)
            {
                for (int i = 0; i < selection.packets.Count; i++)
                {
                    var packet = selection.packets[i];
                    if (packet.pageIndex < 0 || packet.pageIndex >= pageGpuData.Length)
                        continue;
                    var data = pageGpuData[packet.pageIndex];
                    if (data?.clusterVisibleCpu == null)
                        continue;
                    if (packet.clusterIndex < 0 || packet.clusterIndex >= data.clusterVisibleCpu.Length)
                        continue;
                    data.clusterVisibleCpu[packet.clusterIndex] = 1;
                }
            }

            for (int i = 0; i < pageGpuData.Length; i++)
            {
                var data = pageGpuData[i];
                if (data?.clusterVisible == null || data.clusterVisibleCpu == null)
                    continue;
                data.clusterVisible.SetData(data.clusterVisibleCpu);
            }
        }

        public bool EnsureMergedGpuBuffers()
        {
            if (naniteMesh == null || naniteMesh.pageArray == null)
            {
                ReleaseMergedGpuBuffers();
                return false;
            }

            if (mergedGpuMesh == naniteMesh && mergedGpuData != null)
                return true;

            ReleaseMergedGpuBuffers();

            int stride = -1;
            int totalVertices = 0;
            int totalIndices = 0;
            int totalTriangles = 0;
            int totalClusters = 0;
            var pageClusterBase = new int[naniteMesh.pageArray.Length];
            var pageClusterCount = new int[naniteMesh.pageArray.Length];

            for (int i = 0; i < pageClusterBase.Length; i++)
            {
                pageClusterBase[i] = -1;
                pageClusterCount[i] = 0;
            }

            for (int pageIndex = 0; pageIndex < naniteMesh.pageArray.Length; pageIndex++)
            {
                var page = naniteMesh.pageArray[pageIndex];
                if (page == null || page.vertexData == null || page.indiceArray == null)
                    continue;
                if (page.vertexData.Length == 0 || page.indiceArray.Length == 0 || page.vertexCount <= 0)
                    continue;

                int pageStride = Mathf.Max(3, page.vertexStride);
                if (stride < 0)
                    stride = pageStride;
                if (pageStride != stride)
                {
                    Debug.LogWarning($"[Nanite] Page stride mismatch: page={pageIndex}, stride={pageStride}, expected={stride}. merged draw disabled.");
                    ReleaseMergedGpuBuffers();
                    return false;
                }

                pageClusterBase[pageIndex] = totalClusters;
                int clusters = page.clusterArray != null ? page.clusterArray.Length : 0;
                pageClusterCount[pageIndex] = clusters;
                totalClusters += clusters;

                totalVertices += page.vertexCount;
                totalIndices += page.indiceArray.Length;
                totalTriangles += page.indiceArray.Length / 3;
            }

            if (stride < 0 || totalVertices <= 0 || totalIndices <= 0 || totalTriangles <= 0)
                return false;

            var mergedVertices = new float[totalVertices * stride];
            var mergedIndices = new int[totalIndices];
            var mergedTriCluster = new int[totalTriangles];
            var mergedTriPage = new int[totalTriangles];
            var mergedTriSubMesh = new int[totalTriangles];
            for (int i = 0; i < mergedTriCluster.Length; i++)
                mergedTriCluster[i] = -1;
            for (int i = 0; i < mergedTriPage.Length; i++)
                mergedTriPage[i] = -1;
            for (int i = 0; i < mergedTriSubMesh.Length; i++)
                mergedTriSubMesh[i] = 0;

            int vertexCursor = 0;
            int indexCursor = 0;
            int triangleCursor = 0;

            for (int pageIndex = 0; pageIndex < naniteMesh.pageArray.Length; pageIndex++)
            {
                var page = naniteMesh.pageArray[pageIndex];
                if (page == null || page.vertexData == null || page.indiceArray == null)
                    continue;
                if (page.vertexData.Length == 0 || page.indiceArray.Length == 0 || page.vertexCount <= 0)
                    continue;

                int pageStride = Mathf.Max(3, page.vertexStride);
                int srcVertexCount = page.vertexCount;
                int srcTriangles = page.indiceArray.Length / 3;

                int vertexOffset = vertexCursor;
                for (int v = 0; v < srcVertexCount; v++)
                {
                    int srcBase = v * pageStride;
                    int dstBase = (vertexCursor + v) * stride;
                    for (int c = 0; c < stride; c++)
                    {
                        int si = srcBase + c;
                        mergedVertices[dstBase + c] = (si >= 0 && si < page.vertexData.Length) ? page.vertexData[si] : 0f;
                    }
                }

                for (int i = 0; i < page.indiceArray.Length; i++)
                    mergedIndices[indexCursor + i] = page.indiceArray[i] + vertexOffset;

                var triClusterLocal = BuildTriangleClusterMap(page, srcTriangles);
                int clusterBase = pageClusterBase[pageIndex];
                for (int t = 0; t < srcTriangles; t++)
                {
                    int localCluster = triClusterLocal[t];
                    mergedTriCluster[triangleCursor + t] = (localCluster >= 0 && clusterBase >= 0) ? (clusterBase + localCluster) : -1;
                    mergedTriPage[triangleCursor + t] = pageIndex;
                    if (localCluster >= 0 && page.clusterArray != null && localCluster < page.clusterArray.Length)
                        mergedTriSubMesh[triangleCursor + t] = page.clusterArray[localCluster].subMeshId;
                }

                vertexCursor += srcVertexCount;
                indexCursor += page.indiceArray.Length;
                triangleCursor += srcTriangles;
            }

            mergedGpuData = new MergedGpuData
            {
                vertexStride = stride,
                vertexCount = totalVertices,
                indexCount = totalIndices,
                triangleCount = totalTriangles,
                clusterCount = totalClusters,
                pageClusterBase = pageClusterBase,
                pageClusterCount = pageClusterCount,
                clusterVisibleCpu = new uint[Mathf.Max(1, totalClusters)]
            };

            mergedGpuData.vertexData = new ComputeBuffer(mergedVertices.Length, sizeof(float), ComputeBufferType.Structured);
            mergedGpuData.indices = new ComputeBuffer(mergedIndices.Length, sizeof(int), ComputeBufferType.Structured);
            mergedGpuData.triangleCluster = new ComputeBuffer(mergedTriCluster.Length, sizeof(int), ComputeBufferType.Structured);
            mergedGpuData.trianglePage = new ComputeBuffer(mergedTriPage.Length, sizeof(int), ComputeBufferType.Structured);
            mergedGpuData.triangleSubMesh = new ComputeBuffer(mergedTriSubMesh.Length, sizeof(int), ComputeBufferType.Structured);
            mergedGpuData.clusterVisible = new ComputeBuffer(Mathf.Max(1, totalClusters), sizeof(uint), ComputeBufferType.Structured);
            mergedGpuData.drawArgs = new ComputeBuffer(1, sizeof(uint) * 4, ComputeBufferType.IndirectArguments);

            mergedGpuData.vertexData.SetData(mergedVertices);
            mergedGpuData.indices.SetData(mergedIndices);
            mergedGpuData.triangleCluster.SetData(mergedTriCluster);
            mergedGpuData.trianglePage.SetData(mergedTriPage);
            mergedGpuData.triangleSubMesh.SetData(mergedTriSubMesh);
            uint[] args = { (uint)totalIndices, 1u, 0u, 0u };
            mergedGpuData.drawArgs.SetData(args);

            mergedGpuMesh = naniteMesh;
            return true;
        }

        public bool TryGetMergedGpuData(
            out ComputeBuffer vertexDataBuffer,
            out ComputeBuffer indexBuffer,
            out ComputeBuffer triangleClusterBuffer,
            out ComputeBuffer trianglePageBuffer,
            out ComputeBuffer clusterVisibleBuffer,
            out int vertexStride,
            out int indexCount,
            out int triangleCount,
            out int clusterCount)
        {
            vertexDataBuffer = null;
            indexBuffer = null;
            triangleClusterBuffer = null;
            trianglePageBuffer = null;
            clusterVisibleBuffer = null;
            vertexStride = 0;
            indexCount = 0;
            triangleCount = 0;
            clusterCount = 0;

            if (!EnsureMergedGpuBuffers() || mergedGpuData == null)
                return false;
            if (mergedGpuData.vertexData == null || mergedGpuData.indices == null || mergedGpuData.triangleCluster == null || mergedGpuData.trianglePage == null || mergedGpuData.clusterVisible == null)
                return false;

            vertexDataBuffer = mergedGpuData.vertexData;
            indexBuffer = mergedGpuData.indices;
            triangleClusterBuffer = mergedGpuData.triangleCluster;
            trianglePageBuffer = mergedGpuData.trianglePage;
            clusterVisibleBuffer = mergedGpuData.clusterVisible;
            vertexStride = mergedGpuData.vertexStride;
            indexCount = mergedGpuData.indexCount;
            triangleCount = mergedGpuData.triangleCount;
            clusterCount = mergedGpuData.clusterCount;
            return true;
        }

        public void UpdateMergedClusterVisibilityBuffer(NaniteRuntimeSelection selection)
        {
            if (!EnsureMergedGpuBuffers() || mergedGpuData == null || mergedGpuData.clusterVisible == null || mergedGpuData.clusterVisibleCpu == null)
                return;

            System.Array.Clear(mergedGpuData.clusterVisibleCpu, 0, mergedGpuData.clusterVisibleCpu.Length);

            if (selection != null && selection.packets != null && mergedGpuData.pageClusterBase != null)
            {
                for (int i = 0; i < selection.packets.Count; i++)
                {
                    var packet = selection.packets[i];
                    if (packet.pageIndex < 0 || packet.pageIndex >= mergedGpuData.pageClusterBase.Length)
                        continue;
                    int baseIdx = mergedGpuData.pageClusterBase[packet.pageIndex];
                    if (baseIdx < 0)
                        continue;
                    int count = mergedGpuData.pageClusterCount[packet.pageIndex];
                    if (packet.clusterIndex < 0 || packet.clusterIndex >= count)
                        continue;
                    int globalCluster = baseIdx + packet.clusterIndex;
                    if (globalCluster < 0 || globalCluster >= mergedGpuData.clusterVisibleCpu.Length)
                        continue;
                    mergedGpuData.clusterVisibleCpu[globalCluster] = 1;
                }
            }

            mergedGpuData.clusterVisible.SetData(mergedGpuData.clusterVisibleCpu);
        }

        public bool TryGetMergedIndirectArgsBuffer(out ComputeBuffer indirectArgsBuffer)
        {
            indirectArgsBuffer = null;
            if (!EnsureMergedGpuBuffers() || mergedGpuData == null || mergedGpuData.drawArgs == null)
                return false;
            indirectArgsBuffer = mergedGpuData.drawArgs;
            return true;
        }

        public bool TryGetMergedTriangleSubMeshBuffer(out ComputeBuffer triangleSubMeshBuffer)
        {
            triangleSubMeshBuffer = null;
            if (!EnsureMergedGpuBuffers() || mergedGpuData == null || mergedGpuData.triangleSubMesh == null)
                return false;
            triangleSubMeshBuffer = mergedGpuData.triangleSubMesh;
            return true;
        }

        void ReleasePageGpuBuffers()
        {
            if (pageGpuData != null)
            {
                for (int i = 0; i < pageGpuData.Length; i++)
                {
                    var data = pageGpuData[i];
                    if (data == null)
                        continue;
                    data.vertexData?.Release();
                    data.indices?.Release();
                    data.triangleCluster?.Release();
                    data.clusterVisible?.Release();
                    data.vertexData = null;
                    data.indices = null;
                    data.triangleCluster = null;
                    data.clusterVisible = null;
                    data.clusterVisibleCpu = null;
                }
            }

            pageGpuData = null;
            pageGpuMesh = null;
        }

        void ReleaseMergedGpuBuffers()
        {
            if (mergedGpuData != null)
            {
                mergedGpuData.vertexData?.Release();
                mergedGpuData.indices?.Release();
                mergedGpuData.triangleCluster?.Release();
                mergedGpuData.trianglePage?.Release();
                mergedGpuData.triangleSubMesh?.Release();
                mergedGpuData.clusterVisible?.Release();
                mergedGpuData.drawArgs?.Release();
                mergedGpuData.vertexData = null;
                mergedGpuData.indices = null;
                mergedGpuData.triangleCluster = null;
                mergedGpuData.trianglePage = null;
                mergedGpuData.triangleSubMesh = null;
                mergedGpuData.clusterVisible = null;
                mergedGpuData.drawArgs = null;
                mergedGpuData.clusterVisibleCpu = null;
                mergedGpuData.pageClusterBase = null;
                mergedGpuData.pageClusterCount = null;
            }

            mergedGpuData = null;
            mergedGpuMesh = null;
        }

        static int[] BuildTriangleClusterMap(NaniteMeshPage page, int triangleCount)
        {
            var triCluster = new int[triangleCount];
            for (int i = 0; i < triCluster.Length; i++)
                triCluster[i] = -1;

            if (page == null || page.clusterArray == null)
                return triCluster;

            for (int ci = 0; ci < page.clusterArray.Length; ci++)
            {
                var c = page.clusterArray[ci];
                int startTri = Mathf.Max(0, c.indiceIndex / 3);
                int triCount = Mathf.Max(0, c.indiceCount / 3);
                int endTri = Mathf.Min(triangleCount, startTri + triCount);
                for (int t = startTri; t < endTri; t++)
                    triCluster[t] = ci;
            }

            return triCluster;
        }

        void StampPacketInstanceId()
        {
            int id = GetInstanceID();
            for (int i = 0; i < runtimeSelection.packets.Count; i++)
            {
                var packet = runtimeSelection.packets[i];
                packet.instanceId = id;
                runtimeSelection.packets[i] = packet;
            }
        }

        void UploadSelectionBuffers()
        {
            if (!uploadSelectionToGpu)
            {
                ReleaseSelectionBuffers();
                return;
            }

            int packetCount = runtimeSelection.packets.Count;
            int pageRangeCount = runtimeSelection.pageRanges.Count;

            EnsureSelectionBuffers(packetCount, pageRangeCount);

            if (packetCount > 0)
                selectionPacketBuffer.SetData(runtimeSelection.packets);
            if (pageRangeCount > 0)
                selectionPageRangeBuffer.SetData(runtimeSelection.pageRanges);
        }

        void EnsureSelectionBuffers(int packetCount, int pageRangeCount)
        {
            int packetStride = sizeof(int) * 8;
            int pageRangeStride = sizeof(int) * 3;
            int requiredPackets = Mathf.Max(1, packetCount);
            int requiredPageRanges = Mathf.Max(1, pageRangeCount);

            if (selectionPacketBuffer == null || selectionPacketCapacity < requiredPackets)
            {
                selectionPacketBuffer?.Release();
                selectionPacketCapacity = Mathf.NextPowerOfTwo(requiredPackets);
                selectionPacketBuffer = new ComputeBuffer(selectionPacketCapacity, packetStride);
            }

            if (selectionPageRangeBuffer == null || selectionPageRangeCapacity < requiredPageRanges)
            {
                selectionPageRangeBuffer?.Release();
                selectionPageRangeCapacity = Mathf.NextPowerOfTwo(requiredPageRanges);
                selectionPageRangeBuffer = new ComputeBuffer(selectionPageRangeCapacity, pageRangeStride);
            }
        }

        void ReleaseSelectionBuffers()
        {
            selectionPacketBuffer?.Release();
            selectionPageRangeBuffer?.Release();
            selectionPacketBuffer = null;
            selectionPageRangeBuffer = null;
            selectionPacketCapacity = 0;
            selectionPageRangeCapacity = 0;
        }

        Shader ResolveDebugShader()
        {
            if (debugColorMode == NaniteDebugColorMode.VBuffer)
            {
                var vbufferShader = Shader.Find("Nanite/VBufferPreview");
                if (vbufferShader != null)
                    return vbufferShader;
            }

            var vertexColor = Shader.Find("Nanite/DebugVertexColor");
            if (vertexColor != null)
                return vertexColor;

            return Shader.Find("Unlit/Color");
        }
    }
}
