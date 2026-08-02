using UnityEngine;

namespace RealtimeGI
{
    /// <summary>
    /// Optional non-URP debug tracer. It produces a texture that can be inspected by tools or a
    /// temporary UI without coupling the Phase 2 data path to the final RendererFeature.
    /// </summary>
    [ExecuteAlways]
    [DefaultExecutionOrder(12000)]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(GIClipmapSystem))]
    public sealed class GIClipmapDebugTracer : MonoBehaviour
    {
        public GIClipmapSystem clipmaps;
        public Camera targetCamera;
        public ComputeShader debugTraceShader;
        [Range(0.125f, 1f)] public float resolutionScale = 0.5f;
        [Min(1f)] public float maxTraceDistance = 500f;
        public bool automaticUpdate;

        RenderTexture result;
        int traceKernel = -1;

        static readonly int LevelsId = Shader.PropertyToID("_GIClipmapLevels");
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
        static readonly int ResultId = Shader.PropertyToID("_GIResult");
        static readonly int InverseViewProjectionId = Shader.PropertyToID("_GIInverseViewProjection");
        static readonly int CameraPositionId = Shader.PropertyToID("_GICameraPosition");
        static readonly int MaxTraceDistanceId = Shader.PropertyToID("_GIMaxTraceDistance");
        static readonly int OutputSizeId = Shader.PropertyToID("_GIOutputSize");

        public RenderTexture Result => result;

        void Reset()
        {
            clipmaps = GetComponent<GIClipmapSystem>();
        }

        void LateUpdate()
        {
            if (automaticUpdate)
                TraceNow();
        }

        public bool TraceNow()
        {
            if (!isActiveAndEnabled || !SystemInfo.supportsComputeShaders)
                return false;
            if (clipmaps == null)
                clipmaps = GetComponent<GIClipmapSystem>();
            if (targetCamera == null)
                targetCamera = Camera.main;
            if (clipmaps == null || targetCamera == null)
                return false;
            if (debugTraceShader == null)
                debugTraceShader = Resources.Load<ComputeShader>("RealtimeGI/GIClipmapDebugTrace");
            if (debugTraceShader == null)
                return false;
            if (traceKernel < 0)
                traceKernel = debugTraceShader.FindKernel("TraceDebug");

            clipmaps.UpdateClipmaps();
            if (!clipmaps.TryGetGpuView(out GIClipmapGpuView view))
                return false;

            int width = Mathf.Max(1, Mathf.CeilToInt(targetCamera.pixelWidth * resolutionScale));
            int height = Mathf.Max(1, Mathf.CeilToInt(targetCamera.pixelHeight * resolutionScale));
            EnsureResult(width, height);

            Matrix4x4 projection = GL.GetGPUProjectionMatrix(targetCamera.projectionMatrix, true);
            Matrix4x4 inverseViewProjection = (projection * targetCamera.worldToCameraMatrix).inverse;
            debugTraceShader.SetMatrix(InverseViewProjectionId, inverseViewProjection);
            debugTraceShader.SetVector(CameraPositionId, targetCamera.transform.position);
            debugTraceShader.SetFloat(MaxTraceDistanceId, maxTraceDistance);
            debugTraceShader.SetInts(OutputSizeId, width, height);
            debugTraceShader.SetBuffer(traceKernel, LevelsId, view.levelData);
            debugTraceShader.SetBuffer(traceKernel, StaticPageTableId, view.staticPageTable);
            debugTraceShader.SetBuffer(traceKernel, StaticOccupancyId, view.staticOccupancy);
            debugTraceShader.SetBuffer(traceKernel, StaticSurfaceId, view.staticSurface);
            debugTraceShader.SetBuffer(traceKernel, StaticSurfaceIdentityId, view.staticSurfaceIdentity);
            debugTraceShader.SetBuffer(traceKernel, StaticDistanceId, view.staticDistance);
            debugTraceShader.SetBuffer(traceKernel, DynamicPageTableId, view.dynamicPageTable);
            debugTraceShader.SetBuffer(traceKernel, DynamicOccupancyId, view.dynamicOccupancy);
            debugTraceShader.SetBuffer(traceKernel, DynamicSurfaceId, view.dynamicSurface);
            debugTraceShader.SetBuffer(traceKernel, DynamicSurfaceIdentityId, view.dynamicSurfaceIdentity);
            debugTraceShader.SetBuffer(traceKernel, DynamicDistanceId, view.dynamicDistance);
            debugTraceShader.SetTexture(traceKernel, ResultId, result);
            debugTraceShader.Dispatch(traceKernel, Mathf.CeilToInt(width / 8f), Mathf.CeilToInt(height / 8f), 1);
            return true;
        }

        void EnsureResult(int width, int height)
        {
            if (result != null && result.width == width && result.height == height)
                return;
            ReleaseResult();
            result = new RenderTexture(width, height, 0, RenderTextureFormat.ARGBHalf)
            {
                name = "GI Clipmap Debug Trace",
                enableRandomWrite = true,
                useMipMap = false,
                autoGenerateMips = false
            };
            result.Create();
        }

        void OnDisable()
        {
            ReleaseResult();
        }

        void OnDestroy()
        {
            ReleaseResult();
        }

        void ReleaseResult()
        {
            if (result == null)
                return;
            result.Release();
            if (Application.isPlaying)
                Destroy(result);
            else
                DestroyImmediate(result);
            result = null;
        }
    }
}
