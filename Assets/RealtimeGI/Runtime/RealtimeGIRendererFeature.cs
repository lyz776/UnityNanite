using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;
using UnityEngine.Serialization;

namespace RealtimeGI
{
    public sealed class RealtimeGIRendererFeature : ScriptableRendererFeature
    {
        [Serializable]
        public sealed class Settings
        {
            public RenderPassEvent injectionPoint = RenderPassEvent.BeforeRenderingTransparents;
            public bool enableInSceneView = true;
            public bool enableDiffuse = true;
            [FormerlySerializedAs("enableRoughSpecular")]
            public bool enableSpecular = true;
            [Range(0.125f, 0.5f)] public float diffuseResolutionScale = 0.25f;
            [Range(0.25f, 1f)] public float specularResolutionScale = 0.5f;
            [Range(1, 4)] public int diffuseRaysPerProbe = 2;
            [Range(0f, 0.98f)] public float diffuseHistoryWeight = 0.9f;
            [Range(0f, 0.98f)] public float specularHistoryWeight = 0.82f;
            [Min(0f)] public float diffuseIntensity = 1f;
            [Min(0f)] public float specularIntensity = 1f;
            [Range(0f, 0.5f)] public float minSpecularRoughness = 0f;
            [Range(0.2f, 1f)] public float maxSpecularRoughness = 1f;
            [Range(1, 8)] public int specularSpatialSamples = 4;
            [Min(1f)] public float maxTraceDistance = 150f;
            [Range(0f, 50f)] public float screenTraceDistance = 12f;
            public bool enableTraceCounters;
            [Range(10, 240)] public int traceCounterReadbackInterval = 30;
            public ComputeShader screenLightingShader;
            public Shader compositeShader;
        }

        [SerializeField] Settings settings = new Settings();

        Material compositeMaterial;
        ScreenLightingPass pass;

        public readonly struct TraceCounterSnapshot
        {
            public readonly uint rays;
            public readonly uint screenHits;
            public readonly uint worldSteps;
            public readonly uint worldHits;
            public readonly uint worldMisses;

            internal TraceCounterSnapshot(uint[] values)
            {
                rays = values[0];
                screenHits = values[1];
                worldSteps = values[2];
                worldHits = values[3];
                worldMisses = values[4];
            }

            public float AverageWorldSteps => worldHits + worldMisses > 0
                ? (float)worldSteps / (worldHits + worldMisses)
                : 0f;
        }

        public static TraceCounterSnapshot LatestTraceCounters { get; private set; }

        public override void Create()
        {
            if (settings.screenLightingShader == null)
                settings.screenLightingShader = Resources.Load<ComputeShader>("RealtimeGI/GIScreenLighting");
            if (settings.compositeShader == null)
                settings.compositeShader = Shader.Find("Hidden/RealtimeGI/Composite");
            CoreUtils.Destroy(compositeMaterial);
            compositeMaterial = settings.compositeShader != null
                ? CoreUtils.CreateEngineMaterial(settings.compositeShader)
                : null;
            pass?.Dispose();
            pass = new ScreenLightingPass
            {
                renderPassEvent = settings.injectionPoint,
                requiresIntermediateTexture = true
            };
        }

        protected override void Dispose(bool disposing)
        {
            pass?.Dispose();
            pass = null;
            CoreUtils.Destroy(compositeMaterial);
            compositeMaterial = null;
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (pass == null || settings.screenLightingShader == null || compositeMaterial == null ||
                !SystemInfo.supportsComputeShaders)
                return;
            CameraType cameraType = renderingData.cameraData.cameraType;
            if (cameraType != CameraType.Game &&
                !(settings.enableInSceneView && cameraType == CameraType.SceneView))
                return;
            GIClipmapSystem clipmaps = GIClipmapSystem.Active;
            if (clipmaps == null || clipmaps.Scene == null)
                return;
            clipmaps.UpdateClipmaps();
            if (!clipmaps.TryGetGpuView(out GIClipmapGpuView clipmapView) ||
                !clipmaps.Scene.TryGetGpuView(out GISceneGpuView sceneView))
                return;
            pass.Setup(settings, compositeMaterial, clipmapView, sceneView);
            renderer.EnqueuePass(pass);
        }

        sealed class CameraHistory : IDisposable
        {
            public readonly RTHandle[] diffuse = new RTHandle[2];
            public readonly RTHandle[] specular = new RTHandle[2];
            public readonly RTHandle[] geometry = new RTHandle[2];
            public int index;
            public int width;
            public int height;
            public int diffuseWidth;
            public int diffuseHeight;
            public int specularWidth;
            public int specularHeight;
            public bool valid;
            public uint frameIndex;
            public Vector3 previousPosition;
            public Quaternion previousRotation;
            public Matrix4x4 previousViewProjection;

            public void Ensure(int fullWidth, int fullHeight, int nextDiffuseWidth, int nextDiffuseHeight,
                int nextSpecularWidth, int nextSpecularHeight, string cameraName)
            {
                if (width == fullWidth && height == fullHeight &&
                    diffuseWidth == nextDiffuseWidth && diffuseHeight == nextDiffuseHeight &&
                    specularWidth == nextSpecularWidth && specularHeight == nextSpecularHeight &&
                    diffuse[0] != null)
                    return;
                DisposeTextures();
                width = fullWidth;
                height = fullHeight;
                diffuseWidth = nextDiffuseWidth;
                diffuseHeight = nextDiffuseHeight;
                specularWidth = nextSpecularWidth;
                specularHeight = nextSpecularHeight;
                for (int i = 0; i < 2; i++)
                {
                    diffuse[i] = Allocate(diffuseWidth, diffuseHeight, $"GI Diffuse History {cameraName} {i}");
                    specular[i] = Allocate(fullWidth, fullHeight, $"GI Specular History {cameraName} {i}");
                    geometry[i] = Allocate(specularWidth, specularHeight, $"GI Geometry History {cameraName} {i}");
                }
                index = 0;
                valid = false;
            }

            static RTHandle Allocate(int width, int height, string name) => RTHandles.Alloc(
                width, height, 1, DepthBits.None, GraphicsFormat.R16G16B16A16_SFloat,
                FilterMode.Bilinear, TextureWrapMode.Clamp, TextureDimension.Tex2D,
                true, name: name);

            public bool IsCameraCut(Camera camera)
            {
                if (!valid)
                    return true;
                return Vector3.Distance(previousPosition, camera.transform.position) > 5f ||
                       Quaternion.Angle(previousRotation, camera.transform.rotation) > 35f;
            }

            public void Advance(Camera camera, Matrix4x4 viewProjection)
            {
                previousPosition = camera.transform.position;
                previousRotation = camera.transform.rotation;
                previousViewProjection = viewProjection;
                valid = true;
                frameIndex++;
                index ^= 1;
            }

            void DisposeTextures()
            {
                for (int i = 0; i < 2; i++)
                {
                    diffuse[i]?.Release();
                    specular[i]?.Release();
                    geometry[i]?.Release();
                    diffuse[i] = null;
                    specular[i] = null;
                    geometry[i] = null;
                }
                valid = false;
            }

            public void Dispose() => DisposeTextures();
        }

        sealed class ScreenLightingPass : ScriptableRenderPass, IDisposable
        {
            const string PassName = "RealtimeGI/Screen Lighting";
            static readonly int LightingTextureId = Shader.PropertyToID("_GILightingTexture");

            readonly Dictionary<int, CameraHistory> histories = new Dictionary<int, CameraHistory>();
            static readonly uint[] ZeroTraceCounters = new uint[8];
            Settings settings;
            Material compositeMaterial;
            GIClipmapGpuView clipmapView;
            GISceneGpuView sceneView;
            GraphicsBuffer traceCounterBuffer;
            bool traceReadbackPending;

            int traceDiffuseKernel = -1;
            int temporalDiffuseKernel = -1;
            int traceSpecularKernel = -1;
            int resolveSpecularKernel = -1;
            int temporalSpecularKernel = -1;
            int spatialSpecularKernel = -1;
            int updateGeometryKernel = -1;
            int upsampleKernel = -1;

            public ScreenLightingPass()
            {
                ConfigureInput(ScriptableRenderPassInput.Depth |
                               ScriptableRenderPassInput.Normal |
                               ScriptableRenderPassInput.Motion);
            }

            public void Setup(Settings nextSettings, Material material,
                GIClipmapGpuView nextClipmapView, GISceneGpuView nextSceneView)
            {
                settings = nextSettings;
                compositeMaterial = material;
                clipmapView = nextClipmapView;
                sceneView = nextSceneView;
                EnsureTraceCounterBuffer();
                RequestTraceReadbackIfNeeded();
                if (traceDiffuseKernel < 0)
                {
                    ComputeShader shader = settings.screenLightingShader;
                    traceDiffuseKernel = shader.FindKernel("TraceDiffuse");
                    temporalDiffuseKernel = shader.FindKernel("TemporalDiffuse");
                    traceSpecularKernel = shader.FindKernel("TraceSpecular");
                    resolveSpecularKernel = shader.FindKernel("ResolveSpecular");
                    temporalSpecularKernel = shader.FindKernel("TemporalSpecular");
                    spatialSpecularKernel = shader.FindKernel("SpatialSpecular");
                    updateGeometryKernel = shader.FindKernel("UpdateGeometryHistory");
                    upsampleKernel = shader.FindKernel("UpsampleLighting");
                }
            }

            void EnsureTraceCounterBuffer()
            {
                if (traceCounterBuffer != null)
                    return;
                traceCounterBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 8, 4)
                {
                    name = "GI Trace Counters"
                };
                traceCounterBuffer.SetData(ZeroTraceCounters);
            }

            void RequestTraceReadbackIfNeeded()
            {
                if (!settings.enableTraceCounters || traceReadbackPending || traceCounterBuffer == null ||
                    Time.frameCount % Mathf.Max(10, settings.traceCounterReadbackInterval) != 0)
                    return;
                traceReadbackPending = true;
                AsyncGPUReadback.Request(traceCounterBuffer, request =>
                {
                    traceReadbackPending = false;
                    if (request.hasError)
                        return;
                    var data = request.GetData<uint>();
                    if (data.Length < 5)
                        return;
                    var values = new uint[5];
                    for (int i = 0; i < values.Length; i++) values[i] = data[i];
                    LatestTraceCounters = new TraceCounterSnapshot(values);
                });
            }

            sealed class PassData
            {
                public ComputeShader shader;
                public int traceDiffuseKernel;
                public int temporalDiffuseKernel;
                public int traceSpecularKernel;
                public int resolveSpecularKernel;
                public int temporalSpecularKernel;
                public int spatialSpecularKernel;
                public int updateGeometryKernel;
                public int upsampleKernel;
                public TextureHandle depth;
                public TextureHandle normals;
                public TextureHandle motion;
                public TextureHandle sourceColor;
                public TextureHandle gBuffer0;
                public TextureHandle gBuffer1;
                public TextureHandle gBuffer2;
                public TextureHandle currentDiffuse;
                public TextureHandle currentSpecular;
                public TextureHandle currentSpecularRay;
                public TextureHandle resolvedSpecular;
                public TextureHandle resolvedSpecularRay;
                public TextureHandle spatialSpecular;
                public TextureHandle diffuseRead;
                public TextureHandle diffuseWrite;
                public TextureHandle specularRead;
                public TextureHandle specularWrite;
                public TextureHandle geometryRead;
                public TextureHandle geometryWrite;
                public TextureHandle fullLighting;
                public GraphicsBuffer levelData;
                public GraphicsBuffer staticPageTable;
                public GraphicsBuffer staticOccupancy;
                public GraphicsBuffer staticSurface;
                public GraphicsBuffer staticDistance;
                public GraphicsBuffer staticRadiance;
                public GraphicsBuffer staticValidity;
                public GraphicsBuffer dynamicPageTable;
                public GraphicsBuffer dynamicOccupancy;
                public GraphicsBuffer dynamicSurface;
                public GraphicsBuffer dynamicDistance;
                public GraphicsBuffer dynamicRadiance;
                public GraphicsBuffer dynamicValidity;
                public GraphicsBuffer materials;
                public GraphicsBuffer traceCounters;
                public Matrix4x4 inverseViewProjection;
                public Matrix4x4 viewProjection;
                public Matrix4x4 previousViewProjection;
                public Vector3 cameraPosition;
                public Vector3 mainLightDirection;
                public Vector3 mainLightColor;
                public Vector3 skyColor;
                public int fullWidth;
                public int fullHeight;
                public int diffuseWidth;
                public int diffuseHeight;
                public int specularWidth;
                public int specularHeight;
                public int diffuseRays;
                public int specularSpatialSamples;
                public int materialCount;
                public int historyValid;
                public int frameIndex;
                public float maxTraceDistance;
                public float screenTraceDistance;
                public float diffuseHistoryWeight;
                public float specularHistoryWeight;
                public float diffuseIntensity;
                public float specularIntensity;
                public float minSpecularRoughness;
                public float maxSpecularRoughness;
                public bool enableDiffuse;
                public bool enableSpecular;
                public bool enableTraceCounters;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                if (settings == null || settings.screenLightingShader == null || compositeMaterial == null)
                    return;
                UniversalResourceData resources = frameData.Get<UniversalResourceData>();
                UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
                if (resources.isActiveTargetBackBuffer || !resources.cameraDepthTexture.IsValid() ||
                    !resources.cameraNormalsTexture.IsValid() || !resources.motionVectorColor.IsValid() ||
                    resources.gBuffer == null || resources.gBuffer.Length < 3 ||
                    !resources.gBuffer[0].IsValid() || !resources.gBuffer[1].IsValid() ||
                    !resources.gBuffer[2].IsValid())
                    return;

                Camera camera = cameraData.camera;
                int fullWidth = Mathf.Max(1, cameraData.scaledWidth);
                int fullHeight = Mathf.Max(1, cameraData.scaledHeight);
                int diffuseWidth = Mathf.Max(1, Mathf.CeilToInt(fullWidth * settings.diffuseResolutionScale));
                int diffuseHeight = Mathf.Max(1, Mathf.CeilToInt(fullHeight * settings.diffuseResolutionScale));
                int specularWidth = Mathf.Max(1, Mathf.CeilToInt(fullWidth * settings.specularResolutionScale));
                int specularHeight = Mathf.Max(1, Mathf.CeilToInt(fullHeight * settings.specularResolutionScale));

                int cameraId = camera.GetInstanceID();
                if (!histories.TryGetValue(cameraId, out CameraHistory history))
                {
                    history = new CameraHistory();
                    histories.Add(cameraId, history);
                }
                history.Ensure(fullWidth, fullHeight, diffuseWidth, diffuseHeight,
                    specularWidth, specularHeight, camera.name);
                bool historyValid = history.valid && !history.IsCameraCut(camera);
                int readIndex = history.index;
                int writeIndex = readIndex ^ 1;

                TextureDesc lowDesc = new TextureDesc(diffuseWidth, diffuseHeight)
                {
                    name = "GI Current Diffuse",
                    colorFormat = GraphicsFormat.R16G16B16A16_SFloat,
                    enableRandomWrite = true,
                    clearBuffer = false,
                    filterMode = FilterMode.Bilinear
                };
                TextureHandle currentDiffuse = renderGraph.CreateTexture(lowDesc);
                lowDesc.width = specularWidth;
                lowDesc.height = specularHeight;
                lowDesc.name = "GI Current Rough Specular";
                TextureHandle currentSpecular = renderGraph.CreateTexture(lowDesc);
                lowDesc.name = "GI Current Specular Ray";
                TextureHandle currentSpecularRay = renderGraph.CreateTexture(lowDesc);
                TextureDesc fullDesc = new TextureDesc(fullWidth, fullHeight)
                {
                    name = "GI Full Resolution Lighting",
                    colorFormat = GraphicsFormat.R16G16B16A16_SFloat,
                    enableRandomWrite = true,
                    clearBuffer = false,
                    filterMode = FilterMode.Bilinear
                };
                TextureHandle fullLighting = renderGraph.CreateTexture(fullDesc);
                fullDesc.name = "GI Resolved Specular";
                TextureHandle resolvedSpecular = renderGraph.CreateTexture(fullDesc);
                fullDesc.name = "GI Resolved Specular Ray";
                TextureHandle resolvedSpecularRay = renderGraph.CreateTexture(fullDesc);
                fullDesc.name = "GI Spatial Specular";
                TextureHandle spatialSpecular = renderGraph.CreateTexture(fullDesc);

                TextureHandle diffuseRead = renderGraph.ImportTexture(history.diffuse[readIndex]);
                TextureHandle diffuseWrite = renderGraph.ImportTexture(history.diffuse[writeIndex]);
                TextureHandle specularRead = renderGraph.ImportTexture(history.specular[readIndex]);
                TextureHandle specularWrite = renderGraph.ImportTexture(history.specular[writeIndex]);
                TextureHandle geometryRead = renderGraph.ImportTexture(history.geometry[readIndex]);
                TextureHandle geometryWrite = renderGraph.ImportTexture(history.geometry[writeIndex]);

                Matrix4x4 projection = GL.GetGPUProjectionMatrix(cameraData.GetProjectionMatrix(), true);
                Matrix4x4 viewProjection = projection * cameraData.GetViewMatrix();
                Light sun = RenderSettings.sun;
                Vector3 lightDirection = sun != null ? sun.transform.forward : new Vector3(0.3f, -0.8f, 0.2f).normalized;
                Color lightColor = sun != null ? sun.color * sun.intensity : Color.white;
                Color ambient = RenderSettings.ambientSkyColor.linear;

                using (var builder = renderGraph.AddUnsafePass<PassData>(PassName, out PassData data))
                {
                    data.shader = settings.screenLightingShader;
                    data.traceDiffuseKernel = traceDiffuseKernel;
                    data.temporalDiffuseKernel = temporalDiffuseKernel;
                    data.traceSpecularKernel = traceSpecularKernel;
                    data.resolveSpecularKernel = resolveSpecularKernel;
                    data.temporalSpecularKernel = temporalSpecularKernel;
                    data.spatialSpecularKernel = spatialSpecularKernel;
                    data.updateGeometryKernel = updateGeometryKernel;
                    data.upsampleKernel = upsampleKernel;
                    data.depth = resources.cameraDepthTexture;
                    data.normals = resources.cameraNormalsTexture;
                    data.motion = resources.motionVectorColor;
                    data.sourceColor = resources.activeColorTexture;
                    data.gBuffer0 = resources.gBuffer[0];
                    data.gBuffer1 = resources.gBuffer[1];
                    data.gBuffer2 = resources.gBuffer[2];
                    data.currentDiffuse = currentDiffuse;
                    data.currentSpecular = currentSpecular;
                    data.currentSpecularRay = currentSpecularRay;
                    data.resolvedSpecular = resolvedSpecular;
                    data.resolvedSpecularRay = resolvedSpecularRay;
                    data.spatialSpecular = spatialSpecular;
                    data.diffuseRead = diffuseRead;
                    data.diffuseWrite = diffuseWrite;
                    data.specularRead = specularRead;
                    data.specularWrite = specularWrite;
                    data.geometryRead = geometryRead;
                    data.geometryWrite = geometryWrite;
                    data.fullLighting = fullLighting;
                    data.levelData = clipmapView.levelData;
                    data.staticPageTable = clipmapView.staticPageTable;
                    data.staticOccupancy = clipmapView.staticOccupancy;
                    data.staticSurface = clipmapView.staticSurface;
                    data.staticDistance = clipmapView.staticDistance;
                    data.staticRadiance = clipmapView.staticRadiance;
                    data.staticValidity = clipmapView.staticValidity;
                    data.dynamicPageTable = clipmapView.dynamicPageTable;
                    data.dynamicOccupancy = clipmapView.dynamicOccupancy;
                    data.dynamicSurface = clipmapView.dynamicSurface;
                    data.dynamicDistance = clipmapView.dynamicDistance;
                    data.dynamicRadiance = clipmapView.dynamicRadiance;
                    data.dynamicValidity = clipmapView.dynamicValidity;
                    data.materials = sceneView.materials;
                    data.traceCounters = traceCounterBuffer;
                    data.inverseViewProjection = viewProjection.inverse;
                    data.viewProjection = viewProjection;
                    data.previousViewProjection = historyValid
                        ? history.previousViewProjection : viewProjection;
                    data.cameraPosition = camera.transform.position;
                    data.mainLightDirection = lightDirection;
                    data.mainLightColor = new Vector3(lightColor.r, lightColor.g, lightColor.b);
                    data.skyColor = new Vector3(ambient.r, ambient.g, ambient.b);
                    data.fullWidth = fullWidth;
                    data.fullHeight = fullHeight;
                    data.diffuseWidth = diffuseWidth;
                    data.diffuseHeight = diffuseHeight;
                    data.specularWidth = specularWidth;
                    data.specularHeight = specularHeight;
                    data.diffuseRays = settings.enableDiffuse ? settings.diffuseRaysPerProbe : 0;
                    data.specularSpatialSamples = settings.specularSpatialSamples;
                    data.materialCount = sceneView.materialCount;
                    data.historyValid = historyValid ? 1 : 0;
                    data.frameIndex = (int)history.frameIndex;
                    data.maxTraceDistance = settings.maxTraceDistance;
                    data.screenTraceDistance = settings.screenTraceDistance;
                    data.diffuseHistoryWeight = settings.diffuseHistoryWeight;
                    data.specularHistoryWeight = settings.specularHistoryWeight;
                    data.diffuseIntensity = settings.enableDiffuse ? settings.diffuseIntensity : 0f;
                    data.specularIntensity = settings.enableSpecular ? settings.specularIntensity : 0f;
                    data.minSpecularRoughness = settings.minSpecularRoughness;
                    data.maxSpecularRoughness = settings.maxSpecularRoughness;
                    data.enableDiffuse = settings.enableDiffuse;
                    data.enableSpecular = settings.enableSpecular;
                    data.enableTraceCounters = settings.enableTraceCounters;

                    builder.UseTexture(data.depth, AccessFlags.Read);
                    builder.UseTexture(data.normals, AccessFlags.Read);
                    builder.UseTexture(data.motion, AccessFlags.Read);
                    builder.UseTexture(data.sourceColor, AccessFlags.Read);
                    builder.UseTexture(data.gBuffer0, AccessFlags.Read);
                    builder.UseTexture(data.gBuffer1, AccessFlags.Read);
                    builder.UseTexture(data.gBuffer2, AccessFlags.Read);
                    builder.UseTexture(data.currentDiffuse, AccessFlags.ReadWrite);
                    builder.UseTexture(data.currentSpecular, AccessFlags.ReadWrite);
                    builder.UseTexture(data.currentSpecularRay, AccessFlags.ReadWrite);
                    builder.UseTexture(data.resolvedSpecular, AccessFlags.ReadWrite);
                    builder.UseTexture(data.resolvedSpecularRay, AccessFlags.ReadWrite);
                    builder.UseTexture(data.spatialSpecular, AccessFlags.ReadWrite);
                    builder.UseTexture(data.diffuseRead, AccessFlags.Read);
                    builder.UseTexture(data.diffuseWrite, AccessFlags.ReadWrite);
                    builder.UseTexture(data.specularRead, AccessFlags.Read);
                    builder.UseTexture(data.specularWrite, AccessFlags.ReadWrite);
                    builder.UseTexture(data.geometryRead, AccessFlags.Read);
                    builder.UseTexture(data.geometryWrite, AccessFlags.Write);
                    builder.UseTexture(data.fullLighting, AccessFlags.Write);
                    builder.UseBuffer(renderGraph.ImportBuffer(data.levelData), AccessFlags.Read);
                    builder.UseBuffer(renderGraph.ImportBuffer(data.staticPageTable), AccessFlags.Read);
                    builder.UseBuffer(renderGraph.ImportBuffer(data.staticOccupancy), AccessFlags.Read);
                    builder.UseBuffer(renderGraph.ImportBuffer(data.staticSurface), AccessFlags.Read);
                    builder.UseBuffer(renderGraph.ImportBuffer(data.staticDistance), AccessFlags.Read);
                    builder.UseBuffer(renderGraph.ImportBuffer(data.staticRadiance), AccessFlags.Read);
                    builder.UseBuffer(renderGraph.ImportBuffer(data.staticValidity), AccessFlags.Read);
                    builder.UseBuffer(renderGraph.ImportBuffer(data.dynamicPageTable), AccessFlags.Read);
                    builder.UseBuffer(renderGraph.ImportBuffer(data.dynamicOccupancy), AccessFlags.Read);
                    builder.UseBuffer(renderGraph.ImportBuffer(data.dynamicSurface), AccessFlags.Read);
                    builder.UseBuffer(renderGraph.ImportBuffer(data.dynamicDistance), AccessFlags.Read);
                    builder.UseBuffer(renderGraph.ImportBuffer(data.dynamicRadiance), AccessFlags.Read);
                    builder.UseBuffer(renderGraph.ImportBuffer(data.dynamicValidity), AccessFlags.Read);
                    builder.UseBuffer(renderGraph.ImportBuffer(data.materials), AccessFlags.Read);
                    builder.UseBuffer(renderGraph.ImportBuffer(data.traceCounters), AccessFlags.ReadWrite);
                    builder.AllowPassCulling(false);
                    builder.AllowGlobalStateModification(true);
                    builder.SetGlobalTextureAfterPass(fullLighting, LightingTextureId);
                    builder.SetRenderFunc(static (PassData passData, UnsafeGraphContext context) => Execute(passData, context));
                }

                TextureHandle source = resources.activeColorTexture;
                TextureDesc compositeDesc = source.GetDescriptor(renderGraph);
                compositeDesc.name = "Realtime GI Camera Color";
                compositeDesc.clearBuffer = false;
                compositeDesc.msaaSamples = MSAASamples.None;
                TextureHandle destination = renderGraph.CreateTexture(compositeDesc);
                using (IBaseRenderGraphBuilder compositeBuilder = renderGraph.AddBlitPass(
                           new RenderGraphUtils.BlitMaterialParameters(source, destination, compositeMaterial, 0),
                           "RealtimeGI/Composite", true))
                {
                    // The material samples this texture through a global binding. Declaring the read keeps
                    // RenderGraph scheduling and transient lifetime tracking correct.
                    compositeBuilder.UseTexture(fullLighting, AccessFlags.Read);
                }
                resources.cameraColor = destination;
                history.Advance(camera, viewProjection);
            }

            static void Execute(PassData data, UnsafeGraphContext context)
            {
                var cmd = context.cmd;
                ComputeShader shader = data.shader;
                cmd.SetComputeMatrixParam(shader, ShaderIds.InverseViewProjection, data.inverseViewProjection);
                cmd.SetComputeMatrixParam(shader, ShaderIds.ViewProjection, data.viewProjection);
                cmd.SetComputeMatrixParam(shader, ShaderIds.PreviousViewProjection, data.previousViewProjection);
                cmd.SetComputeVectorParam(shader, ShaderIds.CameraPosition, data.cameraPosition);
                cmd.SetComputeVectorParam(shader, ShaderIds.MainLightDirection, data.mainLightDirection);
                cmd.SetComputeVectorParam(shader, ShaderIds.MainLightColor, data.mainLightColor);
                cmd.SetComputeVectorParam(shader, ShaderIds.SkyColor, data.skyColor);
                cmd.SetComputeFloatParam(shader, ShaderIds.MaxTraceDistance, data.maxTraceDistance);
                cmd.SetComputeFloatParam(shader, ShaderIds.ScreenTraceDistance, data.screenTraceDistance);
                cmd.SetComputeFloatParam(shader, ShaderIds.DiffuseHistoryWeight, data.diffuseHistoryWeight);
                cmd.SetComputeFloatParam(shader, ShaderIds.SpecularHistoryWeight, data.specularHistoryWeight);
                cmd.SetComputeFloatParam(shader, ShaderIds.DiffuseIntensity, data.diffuseIntensity);
                cmd.SetComputeFloatParam(shader, ShaderIds.SpecularIntensity, data.specularIntensity);
                cmd.SetComputeFloatParam(shader, ShaderIds.MinSpecularRoughness, data.minSpecularRoughness);
                cmd.SetComputeFloatParam(shader, ShaderIds.MaxSpecularRoughness, data.maxSpecularRoughness);
                cmd.SetComputeIntParams(shader, ShaderIds.FullSize, data.fullWidth, data.fullHeight);
                cmd.SetComputeIntParams(shader, ShaderIds.DiffuseSize, data.diffuseWidth, data.diffuseHeight);
                cmd.SetComputeIntParams(shader, ShaderIds.SpecularSize, data.specularWidth, data.specularHeight);
                cmd.SetComputeIntParam(shader, ShaderIds.FrameIndex, data.frameIndex);
                cmd.SetComputeIntParam(shader, ShaderIds.DiffuseRayCount, data.enableDiffuse ? data.diffuseRays : 1);
                cmd.SetComputeIntParam(shader, ShaderIds.MaterialCount, data.materialCount);
                cmd.SetComputeIntParam(shader, ShaderIds.HistoryValid, data.historyValid);
                cmd.SetComputeIntParam(shader, ShaderIds.EnableDiffuse, data.enableDiffuse ? 1 : 0);
                cmd.SetComputeIntParam(shader, ShaderIds.EnableSpecular, data.enableSpecular ? 1 : 0);
                cmd.SetComputeIntParam(shader, ShaderIds.SpecularSpatialSamples, data.specularSpatialSamples);
                cmd.SetComputeIntParam(shader, ShaderIds.EnableTraceCounters, data.enableTraceCounters ? 1 : 0);
                cmd.SetBufferData(data.traceCounters, ZeroTraceCounters);

                cmd.BeginSample("RealtimeGI/Diffuse Trace");
                BindSceneInputs(cmd, shader, data, data.traceDiffuseKernel);
                cmd.SetComputeTextureParam(shader, data.traceDiffuseKernel, ShaderIds.CurrentDiffuse, data.currentDiffuse);
                cmd.DispatchCompute(shader, data.traceDiffuseKernel,
                    DivRoundUp(data.diffuseWidth, 8), DivRoundUp(data.diffuseHeight, 8), 1);
                cmd.EndSample("RealtimeGI/Diffuse Trace");

                cmd.BeginSample("RealtimeGI/Diffuse Temporal");
                BindScreenInputs(cmd, shader, data, data.temporalDiffuseKernel);
                cmd.SetComputeTextureParam(shader, data.temporalDiffuseKernel, ShaderIds.CurrentDiffuseRead, data.currentDiffuse);
                cmd.SetComputeTextureParam(shader, data.temporalDiffuseKernel, ShaderIds.DiffuseHistoryRead, data.diffuseRead);
                cmd.SetComputeTextureParam(shader, data.temporalDiffuseKernel, ShaderIds.GeometryHistoryRead, data.geometryRead);
                cmd.SetComputeTextureParam(shader, data.temporalDiffuseKernel, ShaderIds.DiffuseHistoryWrite, data.diffuseWrite);
                cmd.DispatchCompute(shader, data.temporalDiffuseKernel,
                    DivRoundUp(data.diffuseWidth, 8), DivRoundUp(data.diffuseHeight, 8), 1);
                cmd.EndSample("RealtimeGI/Diffuse Temporal");

                cmd.BeginSample("RealtimeGI/Specular Trace");
                BindSceneInputs(cmd, shader, data, data.traceSpecularKernel);
                cmd.SetComputeTextureParam(shader, data.traceSpecularKernel, ShaderIds.CurrentSpecular, data.currentSpecular);
                cmd.SetComputeTextureParam(shader, data.traceSpecularKernel, ShaderIds.CurrentSpecularRay, data.currentSpecularRay);
                cmd.DispatchCompute(shader, data.traceSpecularKernel,
                    DivRoundUp(data.specularWidth, 8), DivRoundUp(data.specularHeight, 8), 1);
                cmd.EndSample("RealtimeGI/Specular Trace");

                cmd.BeginSample("RealtimeGI/Specular Reuse");
                BindScreenInputs(cmd, shader, data, data.resolveSpecularKernel);
                cmd.SetComputeTextureParam(shader, data.resolveSpecularKernel, ShaderIds.CurrentSpecularRead, data.currentSpecular);
                cmd.SetComputeTextureParam(shader, data.resolveSpecularKernel, ShaderIds.CurrentSpecularRayRead, data.currentSpecularRay);
                cmd.SetComputeTextureParam(shader, data.resolveSpecularKernel, ShaderIds.ResolvedSpecular, data.resolvedSpecular);
                cmd.SetComputeTextureParam(shader, data.resolveSpecularKernel, ShaderIds.ResolvedSpecularRay, data.resolvedSpecularRay);
                cmd.DispatchCompute(shader, data.resolveSpecularKernel,
                    DivRoundUp(data.fullWidth, 8), DivRoundUp(data.fullHeight, 8), 1);
                cmd.EndSample("RealtimeGI/Specular Reuse");

                cmd.BeginSample("RealtimeGI/Specular Temporal");
                BindScreenInputs(cmd, shader, data, data.temporalSpecularKernel);
                cmd.SetComputeTextureParam(shader, data.temporalSpecularKernel, ShaderIds.ResolvedSpecularRead, data.resolvedSpecular);
                cmd.SetComputeTextureParam(shader, data.temporalSpecularKernel, ShaderIds.ResolvedSpecularRayRead, data.resolvedSpecularRay);
                cmd.SetComputeTextureParam(shader, data.temporalSpecularKernel, ShaderIds.SpecularHistoryRead, data.specularRead);
                cmd.SetComputeTextureParam(shader, data.temporalSpecularKernel, ShaderIds.GeometryHistoryRead, data.geometryRead);
                cmd.SetComputeTextureParam(shader, data.temporalSpecularKernel, ShaderIds.SpecularHistoryWrite, data.specularWrite);
                cmd.DispatchCompute(shader, data.temporalSpecularKernel,
                    DivRoundUp(data.fullWidth, 8), DivRoundUp(data.fullHeight, 8), 1);
                cmd.EndSample("RealtimeGI/Specular Temporal");

                cmd.BeginSample("RealtimeGI/Specular Spatial");
                BindScreenInputs(cmd, shader, data, data.spatialSpecularKernel);
                cmd.SetComputeTextureParam(shader, data.spatialSpecularKernel, ShaderIds.SpecularTemporalRead, data.specularWrite);
                cmd.SetComputeTextureParam(shader, data.spatialSpecularKernel, ShaderIds.SpecularSpatial, data.spatialSpecular);
                cmd.DispatchCompute(shader, data.spatialSpecularKernel,
                    DivRoundUp(data.fullWidth, 8), DivRoundUp(data.fullHeight, 8), 1);
                cmd.EndSample("RealtimeGI/Specular Spatial");

                BindScreenInputs(cmd, shader, data, data.updateGeometryKernel);
                cmd.SetComputeTextureParam(shader, data.updateGeometryKernel, ShaderIds.GeometryHistoryWrite, data.geometryWrite);
                cmd.DispatchCompute(shader, data.updateGeometryKernel,
                    DivRoundUp(data.specularWidth, 8), DivRoundUp(data.specularHeight, 8), 1);

                cmd.BeginSample("RealtimeGI/Upsample");
                BindScreenInputs(cmd, shader, data, data.upsampleKernel);
                cmd.SetComputeTextureParam(shader, data.upsampleKernel, ShaderIds.DiffuseFiltered, data.diffuseWrite);
                cmd.SetComputeTextureParam(shader, data.upsampleKernel, ShaderIds.SpecularFiltered, data.spatialSpecular);
                cmd.SetComputeTextureParam(shader, data.upsampleKernel, ShaderIds.FullLighting, data.fullLighting);
                cmd.DispatchCompute(shader, data.upsampleKernel,
                    DivRoundUp(data.fullWidth, 8), DivRoundUp(data.fullHeight, 8), 1);
                cmd.EndSample("RealtimeGI/Upsample");
            }

            static void BindScreenInputs(UnsafeCommandBuffer cmd, ComputeShader shader, PassData data, int kernel)
            {
                cmd.SetComputeTextureParam(shader, kernel, ShaderIds.DepthTexture, data.depth);
                cmd.SetComputeTextureParam(shader, kernel, ShaderIds.NormalsTexture, data.normals);
                cmd.SetComputeTextureParam(shader, kernel, ShaderIds.MotionTexture, data.motion);
                cmd.SetComputeTextureParam(shader, kernel, ShaderIds.GBuffer0, data.gBuffer0);
                cmd.SetComputeTextureParam(shader, kernel, ShaderIds.GBuffer1, data.gBuffer1);
                cmd.SetComputeTextureParam(shader, kernel, ShaderIds.GBuffer2, data.gBuffer2);
            }

            static void BindSceneInputs(UnsafeCommandBuffer cmd, ComputeShader shader, PassData data, int kernel)
            {
                BindScreenInputs(cmd, shader, data, kernel);
                cmd.SetComputeTextureParam(shader, kernel, ShaderIds.SourceColor, data.sourceColor);
                cmd.SetComputeBufferParam(shader, kernel, ShaderIds.Levels, data.levelData);
                cmd.SetComputeBufferParam(shader, kernel, ShaderIds.StaticPageTable, data.staticPageTable);
                cmd.SetComputeBufferParam(shader, kernel, ShaderIds.StaticOccupancy, data.staticOccupancy);
                cmd.SetComputeBufferParam(shader, kernel, ShaderIds.StaticSurface, data.staticSurface);
                cmd.SetComputeBufferParam(shader, kernel, ShaderIds.StaticDistance, data.staticDistance);
                cmd.SetComputeBufferParam(shader, kernel, ShaderIds.StaticRadiance, data.staticRadiance);
                cmd.SetComputeBufferParam(shader, kernel, ShaderIds.StaticValidity, data.staticValidity);
                cmd.SetComputeBufferParam(shader, kernel, ShaderIds.DynamicPageTable, data.dynamicPageTable);
                cmd.SetComputeBufferParam(shader, kernel, ShaderIds.DynamicOccupancy, data.dynamicOccupancy);
                cmd.SetComputeBufferParam(shader, kernel, ShaderIds.DynamicSurface, data.dynamicSurface);
                cmd.SetComputeBufferParam(shader, kernel, ShaderIds.DynamicDistance, data.dynamicDistance);
                cmd.SetComputeBufferParam(shader, kernel, ShaderIds.DynamicRadiance, data.dynamicRadiance);
                cmd.SetComputeBufferParam(shader, kernel, ShaderIds.DynamicValidity, data.dynamicValidity);
                cmd.SetComputeBufferParam(shader, kernel, ShaderIds.Materials, data.materials);
                cmd.SetComputeBufferParam(shader, kernel, ShaderIds.TraceCounters, data.traceCounters);
            }

            static int DivRoundUp(int value, int divisor) => (value + divisor - 1) / divisor;

            public void Dispose()
            {
                foreach (CameraHistory history in histories.Values)
                    history.Dispose();
                histories.Clear();
                traceCounterBuffer?.Release();
                traceCounterBuffer = null;
            }
        }

        static class ShaderIds
        {
            public static readonly int DepthTexture = Shader.PropertyToID("_GIDepthTexture");
            public static readonly int NormalsTexture = Shader.PropertyToID("_GINormalsTexture");
            public static readonly int MotionTexture = Shader.PropertyToID("_GIMotionTexture");
            public static readonly int SourceColor = Shader.PropertyToID("_GISourceColor");
            public static readonly int GBuffer0 = Shader.PropertyToID("_GIGBuffer0");
            public static readonly int GBuffer1 = Shader.PropertyToID("_GIGBuffer1");
            public static readonly int GBuffer2 = Shader.PropertyToID("_GIGBuffer2");
            public static readonly int CurrentDiffuse = Shader.PropertyToID("_GICurrentDiffuse");
            public static readonly int CurrentSpecular = Shader.PropertyToID("_GICurrentSpecular");
            public static readonly int CurrentSpecularRay = Shader.PropertyToID("_GICurrentSpecularRay");
            public static readonly int CurrentDiffuseRead = Shader.PropertyToID("_GICurrentDiffuseRead");
            public static readonly int CurrentSpecularRead = Shader.PropertyToID("_GICurrentSpecularRead");
            public static readonly int CurrentSpecularRayRead = Shader.PropertyToID("_GICurrentSpecularRayRead");
            public static readonly int ResolvedSpecular = Shader.PropertyToID("_GIResolvedSpecular");
            public static readonly int ResolvedSpecularRay = Shader.PropertyToID("_GIResolvedSpecularRay");
            public static readonly int ResolvedSpecularRead = Shader.PropertyToID("_GIResolvedSpecularRead");
            public static readonly int ResolvedSpecularRayRead = Shader.PropertyToID("_GIResolvedSpecularRayRead");
            public static readonly int SpecularTemporalRead = Shader.PropertyToID("_GISpecularTemporalRead");
            public static readonly int SpecularSpatial = Shader.PropertyToID("_GISpecularSpatial");
            public static readonly int DiffuseHistoryRead = Shader.PropertyToID("_GIDiffuseHistoryRead");
            public static readonly int DiffuseHistoryWrite = Shader.PropertyToID("_GIDiffuseHistoryWrite");
            public static readonly int SpecularHistoryRead = Shader.PropertyToID("_GISpecularHistoryRead");
            public static readonly int SpecularHistoryWrite = Shader.PropertyToID("_GISpecularHistoryWrite");
            public static readonly int GeometryHistoryRead = Shader.PropertyToID("_GIGeometryHistoryRead");
            public static readonly int GeometryHistoryWrite = Shader.PropertyToID("_GIGeometryHistoryWrite");
            public static readonly int DiffuseFiltered = Shader.PropertyToID("_GIDiffuseFiltered");
            public static readonly int SpecularFiltered = Shader.PropertyToID("_GISpecularFiltered");
            public static readonly int FullLighting = Shader.PropertyToID("_GIFullLighting");
            public static readonly int Levels = Shader.PropertyToID("_GIClipmapLevels");
            public static readonly int StaticPageTable = Shader.PropertyToID("_GIStaticPageTable");
            public static readonly int StaticOccupancy = Shader.PropertyToID("_GIStaticOccupancy");
            public static readonly int StaticSurface = Shader.PropertyToID("_GIStaticSurface");
            public static readonly int StaticDistance = Shader.PropertyToID("_GIStaticDistance");
            public static readonly int StaticRadiance = Shader.PropertyToID("_GIStaticRadiance");
            public static readonly int StaticValidity = Shader.PropertyToID("_GIStaticValidity");
            public static readonly int DynamicPageTable = Shader.PropertyToID("_GIDynamicPageTable");
            public static readonly int DynamicOccupancy = Shader.PropertyToID("_GIDynamicOccupancy");
            public static readonly int DynamicSurface = Shader.PropertyToID("_GIDynamicSurface");
            public static readonly int DynamicDistance = Shader.PropertyToID("_GIDynamicDistance");
            public static readonly int DynamicRadiance = Shader.PropertyToID("_GIDynamicRadiance");
            public static readonly int DynamicValidity = Shader.PropertyToID("_GIDynamicValidity");
            public static readonly int Materials = Shader.PropertyToID("_GIMaterials");
            public static readonly int InverseViewProjection = Shader.PropertyToID("_GIInverseViewProjection");
            public static readonly int ViewProjection = Shader.PropertyToID("_GIViewProjection");
            public static readonly int PreviousViewProjection = Shader.PropertyToID("_GIPreviousViewProjection");
            public static readonly int CameraPosition = Shader.PropertyToID("_GICameraPosition");
            public static readonly int MainLightDirection = Shader.PropertyToID("_GIMainLightDirection");
            public static readonly int MainLightColor = Shader.PropertyToID("_GIMainLightColor");
            public static readonly int SkyColor = Shader.PropertyToID("_GISkyColor");
            public static readonly int MaxTraceDistance = Shader.PropertyToID("_GIMaxTraceDistance");
            public static readonly int ScreenTraceDistance = Shader.PropertyToID("_GIScreenTraceDistance");
            public static readonly int DiffuseHistoryWeight = Shader.PropertyToID("_GIDiffuseHistoryWeight");
            public static readonly int SpecularHistoryWeight = Shader.PropertyToID("_GISpecularHistoryWeight");
            public static readonly int DiffuseIntensity = Shader.PropertyToID("_GIDiffuseIntensity");
            public static readonly int SpecularIntensity = Shader.PropertyToID("_GISpecularIntensity");
            public static readonly int MinSpecularRoughness = Shader.PropertyToID("_GIMinSpecularRoughness");
            public static readonly int MaxSpecularRoughness = Shader.PropertyToID("_GIMaxSpecularRoughness");
            public static readonly int FullSize = Shader.PropertyToID("_GIFullSize");
            public static readonly int DiffuseSize = Shader.PropertyToID("_GIDiffuseSize");
            public static readonly int SpecularSize = Shader.PropertyToID("_GISpecularSize");
            public static readonly int FrameIndex = Shader.PropertyToID("_GIFrameIndex");
            public static readonly int DiffuseRayCount = Shader.PropertyToID("_GIDiffuseRayCount");
            public static readonly int MaterialCount = Shader.PropertyToID("_GIMaterialCount");
            public static readonly int HistoryValid = Shader.PropertyToID("_GIHistoryValid");
            public static readonly int EnableDiffuse = Shader.PropertyToID("_GIEnableDiffuse");
            public static readonly int EnableSpecular = Shader.PropertyToID("_GIEnableSpecular");
            public static readonly int SpecularSpatialSamples = Shader.PropertyToID("_GISpecularSpatialSamples");
            public static readonly int TraceCounters = Shader.PropertyToID("_GITraceCounters");
            public static readonly int EnableTraceCounters = Shader.PropertyToID("_GIEnableTraceCounters");
        }
    }
}
