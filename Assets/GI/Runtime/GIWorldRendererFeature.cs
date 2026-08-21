using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace UnityNanite.GI
{
    public sealed class GIWorldRendererFeature : ScriptableRendererFeature
    {
        public enum DebugMode
        {
            Surface = 0,
            Level = 1,
            GeometryNormal = 2,
            BaseColor = 3,
            Confidence = 4,
            Source = 5,
            DirtyRebuilt = 6,
            ScreenSurfaceRadiance = 7,
            ScreenSurfaceValidity = 8,
            PrimaryProbePlacement = 9,
            PrimaryProbeNormal = 10,
            PrimaryProbeSource = 11
            ,SpecularSource = 12
            ,SpecularWeight = 13
            ,SpecularResult = 14
        }

        [Serializable]
        public sealed class Settings
        {
            public bool enableInSceneView = true;
            public bool enableDebug;
            public DebugMode debugMode = DebugMode.Surface;
            [Min(0f)] public float diffuseIntensity = 1f;
            public bool stylizedDiffuse;
            [Range(0f, 1f)] public float stylizedShadowThreshold = 0.45f;
            [Range(0.001f, 1f)] public float stylizedShadowSoftness = 0.18f;
            [Min(1f)] public float diffuseTraceDistance = 80f;
            [Min(1f)] public float maxTraceDistance = 200f;
            public ComputeShader worldBuildShader;
            public ComputeShader worldDebugShader;
            public ComputeShader screenSurfaceShader;
            public ComputeShader screenProbeShader;
            public Shader diffuseCompositeShader;
            [Range(8, 32)] public int screenProbeTileSize = 8;
        }

        [SerializeField] Settings settings = new Settings();
        GIWorldCache world = new GIWorldCache();
        WorldBuildPass buildPass;
        ScreenSurfacePass screenSurfacePass;
        GIScreenProbeGatherPass screenProbePass;
        DiffuseOverridePass diffuseOverridePass;
        GIDiffuseCompositePass diffuseCompositePass;
        WorldDebugPass debugPass;
        bool loggedUnavailable;
        Camera ownerCamera;
        bool ownerPlaying;

        public override void Create()
        {
            screenProbePass?.Dispose();
            diffuseCompositePass?.Dispose();
            settings.worldBuildShader ??= Resources.Load<ComputeShader>("GI/GIWorldBuild");
            settings.worldDebugShader ??= Resources.Load<ComputeShader>("GI/GIWorldDebug");
            settings.screenSurfaceShader ??= Resources.Load<ComputeShader>("GI/GIScreenSurface");
            settings.screenProbeShader ??= Resources.Load<ComputeShader>("GI/GIScreenProbeGather");
            settings.diffuseCompositeShader ??= Shader.Find("Hidden/Unity Nanite/GI/Diffuse Composite");
            ownerCamera = null;
            ownerPlaying = Application.isPlaying;
            buildPass = new WorldBuildPass(world, settings)
            {
                renderPassEvent = RenderPassEvent.BeforeRenderingPrePasses
            };
            screenSurfacePass = new ScreenSurfacePass(settings)
            {
                renderPassEvent = (RenderPassEvent)((int)RenderPassEvent.BeforeRenderingDeferredLights - 1)
            };
            diffuseOverridePass = new DiffuseOverridePass
            {
                renderPassEvent = (RenderPassEvent)((int)RenderPassEvent.BeforeRenderingGbuffer - 1)
            };
            screenProbePass = new GIScreenProbeGatherPass(world, settings)
            {
                renderPassEvent = (RenderPassEvent)((int)RenderPassEvent.BeforeRenderingDeferredLights - 1)
            };
            diffuseCompositePass = new GIDiffuseCompositePass(settings)
            {
                renderPassEvent = (RenderPassEvent)((int)RenderPassEvent.BeforeRenderingDeferredLights - 1)
            };
            debugPass = new WorldDebugPass(world, settings)
            {
                renderPassEvent = RenderPassEvent.AfterRenderingTransparents,
                requiresIntermediateTexture = true
            };
            Debug.Log(
                $"[GI][Node1] Renderer feature ready: build={settings.worldBuildShader != null}, " +
                $"debug={settings.worldDebugShader != null}, composite=URP-Blitter. " +
                $"[Node3] screenSurface={settings.screenSurfaceShader != null}, " +
                $"screenProbe={settings.screenProbeShader != null}.");
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            CameraType type = renderingData.cameraData.cameraType;
            // One world cache has one camera owner. Alternating Game and SceneView anchors
            // would rebuild every frame and make clipmap generations meaningless.
            if (Application.isPlaying)
            {
                if (type != CameraType.Game)
                    return;
            }
            else if (!settings.enableInSceneView || type != CameraType.SceneView)
            {
                return;
            }
            Camera camera = renderingData.cameraData.camera;
            if (ownerCamera == null || ownerPlaying != Application.isPlaying)
            {
                if (ownerPlaying != Application.isPlaying)
                {
                    screenProbePass?.Dispose();
                    world.Dispose();
                    world = new GIWorldCache();
                    buildPass = new WorldBuildPass(world, settings)
                    {
                        renderPassEvent = RenderPassEvent.BeforeRenderingPrePasses
                    };
                    screenSurfacePass = new ScreenSurfacePass(settings)
                    {
                        renderPassEvent = (RenderPassEvent)((int)RenderPassEvent.BeforeRenderingDeferredLights - 1)
                    };
                    diffuseOverridePass = new DiffuseOverridePass
                    {
                        renderPassEvent = (RenderPassEvent)((int)RenderPassEvent.BeforeRenderingGbuffer - 1)
                    };
                    screenProbePass = new GIScreenProbeGatherPass(world, settings)
                    {
                        renderPassEvent = (RenderPassEvent)((int)RenderPassEvent.BeforeRenderingDeferredLights - 1)
                    };
                    diffuseCompositePass = new GIDiffuseCompositePass(settings)
                    {
                        renderPassEvent = (RenderPassEvent)((int)RenderPassEvent.BeforeRenderingDeferredLights - 1)
                    };
                    debugPass = new WorldDebugPass(world, settings)
                    {
                        renderPassEvent = RenderPassEvent.AfterRenderingTransparents,
                        requiresIntermediateTexture = true
                    };
                }
                ownerCamera = camera;
                ownerPlaying = Application.isPlaying;
                Debug.Log($"[GI][Node1] Camera owner: name={camera.name}, id={camera.GetInstanceID()}, play={ownerPlaying}.");
            }
            else if (ownerCamera != camera)
            {
                return;
            }
            bool diffuseReady = settings.screenSurfaceShader != null &&
                                settings.screenProbeShader != null &&
                                diffuseCompositePass != null && diffuseCompositePass.IsReady;
            bool debugReady = !settings.enableDebug || settings.worldDebugShader != null;
            if (!SystemInfo.supportsComputeShaders || settings.worldBuildShader == null ||
                !diffuseReady || !debugReady)
            {
                if (!loggedUnavailable)
                {
                    loggedUnavailable = true;
                    Debug.LogError(
                        $"[GI][Node1] Renderer feature unavailable: compute={SystemInfo.supportsComputeShaders}, " +
                        $"build={settings.worldBuildShader != null}, diffuse={diffuseReady}, " +
                        $"debug={debugReady}.");
                }
                return;
            }
            if (!world.Prepare(camera))
                return;
            renderer.EnqueuePass(buildPass);
            renderer.EnqueuePass(diffuseOverridePass);
            renderer.EnqueuePass(screenSurfacePass);
            renderer.EnqueuePass(screenProbePass);
            renderer.EnqueuePass(diffuseCompositePass);
            if (settings.enableDebug)
                renderer.EnqueuePass(debugPass);
        }

        protected override void Dispose(bool disposing)
        {
            screenProbePass?.Dispose();
            screenProbePass = null;
            diffuseCompositePass?.Dispose();
            diffuseCompositePass = null;
            world.Dispose();
        }

        sealed class DiffuseOverridePass : ScriptableRenderPass
        {
            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
                UniversalResourceData resources = frameData.Get<UniversalResourceData>();
                resources.realtimeGIDiffuseEnabled = cameraData.camera != null &&
                    cameraData.renderType == CameraRenderType.Base &&
                    cameraData.renderer is UniversalRenderer renderer && renderer.usesDeferredLighting;
            }
        }

        sealed class WorldBuildPass : ScriptableRenderPass
        {
            readonly GIWorldCache world;
            readonly Settings settings;
            public WorldBuildPass(GIWorldCache world, Settings settings)
            {
                this.world = world;
                this.settings = settings;
            }
            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData) =>
                world.RecordBuild(renderGraph, settings.worldBuildShader);
        }

        sealed class ScreenSurfacePass : ScriptableRenderPass
        {
            readonly Settings settings;
            bool loggedReady;

            sealed class PassData
            {
                public ComputeShader shader;
                public int kernel;
                public TextureHandle gBuffer0;
                public TextureHandle gBuffer1;
                public TextureHandle gBuffer2;
                public TextureHandle depth;
                public TextureHandle surfaceLighting;
                public TextureHandle output;
                public Vector3 mainLightDirection;
                public Color mainLightColor;
                public bool hasMainLight;
                public int width;
                public int height;
            }

            public ScreenSurfacePass(Settings settings)
            {
                this.settings = settings;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                UniversalResourceData resources = frameData.Get<UniversalResourceData>();
                UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
                if (resources.gBuffer == null || resources.gBuffer.Length < 3 ||
                    !resources.gBuffer[0].IsValid() || !resources.gBuffer[1].IsValid() ||
                    !resources.gBuffer[2].IsValid() || !resources.cameraDepthTexture.IsValid() ||
                    !resources.activeColorTexture.IsValid())
                    return;

                int width = Mathf.Max(1, cameraData.scaledWidth);
                int height = Mathf.Max(1, cameraData.scaledHeight);
                TextureHandle output = renderGraph.CreateTexture(new TextureDesc(width, height)
                {
                    colorFormat = GraphicsFormat.R16G16B16A16_SFloat,
                    enableRandomWrite = true,
                    clearBuffer = false,
                    filterMode = FilterMode.Point,
                    name = "GI Screen Surface Radiance"
                });
                GIScreenSurfaceData surfaceData = frameData.GetOrCreate<GIScreenSurfaceData>();
                surfaceData.radiance = output;

                Vector3 mainDirection = Vector3.up;
                Color mainColor = Color.black;
                bool hasMainLight = false;
                UniversalLightData lightData = frameData.Get<UniversalLightData>();
                if (lightData.mainLightIndex >= 0 &&
                    lightData.mainLightIndex < lightData.visibleLights.Length)
                {
                    VisibleLight light = lightData.visibleLights[lightData.mainLightIndex];
                    if (light.lightType == LightType.Directional)
                    {
                        Vector4 forward = light.localToWorldMatrix.GetColumn(2);
                        mainDirection = new Vector3(-forward.x, -forward.y, -forward.z).normalized;
                        mainColor = light.finalColor;
                        hasMainLight = true;
                    }
                }

                using var builder = renderGraph.AddUnsafePass<PassData>(
                    "GI/Screen Surface Radiance", out var pass);
                pass.shader = settings.screenSurfaceShader;
                pass.kernel = settings.screenSurfaceShader.FindKernel("BuildScreenSurfaceRadiance");
                pass.gBuffer0 = resources.gBuffer[0];
                pass.gBuffer1 = resources.gBuffer[1];
                pass.gBuffer2 = resources.gBuffer[2];
                pass.depth = resources.cameraDepthTexture;
                pass.surfaceLighting = resources.activeColorTexture;
                pass.output = output;
                pass.mainLightDirection = mainDirection;
                pass.mainLightColor = mainColor;
                pass.hasMainLight = hasMainLight;
                pass.width = width;
                pass.height = height;
                builder.UseTexture(pass.gBuffer0, AccessFlags.Read);
                builder.UseTexture(pass.gBuffer1, AccessFlags.Read);
                builder.UseTexture(pass.gBuffer2, AccessFlags.Read);
                builder.UseTexture(pass.depth, AccessFlags.Read);
                builder.UseTexture(pass.surfaceLighting, AccessFlags.Read);
                builder.UseTexture(output, AccessFlags.Write);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (PassData data, UnsafeGraphContext context) =>
                {
                    var cmd = context.cmd;
                    cmd.SetComputeTextureParam(data.shader, data.kernel, ShaderIds.GBuffer0, data.gBuffer0);
                    cmd.SetComputeTextureParam(data.shader, data.kernel, ShaderIds.GBuffer1, data.gBuffer1);
                    cmd.SetComputeTextureParam(data.shader, data.kernel, ShaderIds.GBuffer2, data.gBuffer2);
                    cmd.SetComputeTextureParam(data.shader, data.kernel, ShaderIds.CameraDepthTexture, data.depth);
                    cmd.SetComputeTextureParam(data.shader, data.kernel, ShaderIds.SurfaceLightingTexture, data.surfaceLighting);
                    cmd.SetComputeTextureParam(data.shader, data.kernel, ShaderIds.ScreenSurfaceRadiance, data.output);
                    cmd.SetComputeVectorParam(data.shader, ShaderIds.OutputSize,
                        new Vector4(data.width, data.height, 0f, 0f));
                    cmd.SetComputeVectorParam(data.shader, ShaderIds.MainLightDirection, data.mainLightDirection);
                    cmd.SetComputeVectorParam(data.shader, ShaderIds.MainLightColor, data.mainLightColor);
                    cmd.SetComputeIntParam(data.shader, ShaderIds.HasMainLight, data.hasMainLight ? 1 : 0);
                    cmd.DispatchCompute(data.shader, data.kernel,
                        (data.width + 7) / 8, (data.height + 7) / 8, 1);
                });

                if (!loggedReady)
                {
                    loggedReady = true;
                    Debug.Log(
                        $"[GI][Node3] Screen Surface Radiance ready: {width}x{height}, " +
                        $"mainDirectional={(hasMainLight ? "on" : "off")}, " +
                        $"source=direct-diffuse+emission.");
                }
            }
        }

        sealed class WorldDebugPass : ScriptableRenderPass
        {
            readonly GIWorldCache world;
            readonly Settings settings;

            sealed class TraceData
            {
                public ComputeShader shader;
                public int kernel;
                public TextureHandle output;
                public GIWorldGpuView world;
                public Matrix4x4 inverseViewProjection;
                public Vector3 cameraPosition;
                public int width, height;
                public float maxDistance;
                public int debugMode;
                public uint worldGeneration;
            }

            sealed class CompositeData
            {
                public TextureHandle debugTexture;
            }

            sealed class ScreenDebugData
            {
                public ComputeShader shader;
                public int kernel;
                public TextureHandle source;
                public TextureHandle output;
                public int mode;
                public int width;
                public int height;
            }

            public WorldDebugPass(GIWorldCache world, Settings settings)
            {
                this.world = world;
                this.settings = settings;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                if ((int)settings.debugMode >= (int)DebugMode.PrimaryProbePlacement)
                {
                    RecordPrimaryProbeDebug(renderGraph, frameData);
                    return;
                }
                if ((int)settings.debugMode >= (int)DebugMode.ScreenSurfaceRadiance)
                {
                    RecordScreenSurfaceDebug(renderGraph, frameData);
                    return;
                }
                if (!world.TryGetGpuView(out GIWorldGpuView worldView))
                    return;
                UniversalResourceData resources = frameData.Get<UniversalResourceData>();
                UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
                if (resources.isActiveTargetBackBuffer || !resources.activeColorTexture.IsValid())
                    return;

                int width = Mathf.Max(1, cameraData.scaledWidth);
                int height = Mathf.Max(1, cameraData.scaledHeight);
                TextureHandle output = renderGraph.CreateTexture(new TextureDesc(width, height)
                {
                    colorFormat = GraphicsFormat.R16G16B16A16_SFloat,
                    enableRandomWrite = true,
                    clearBuffer = false,
                    filterMode = FilterMode.Bilinear,
                    name = "GI World Debug"
                });
                Matrix4x4 projection = GL.GetGPUProjectionMatrix(cameraData.GetProjectionMatrix(), true);
                Matrix4x4 inverseViewProjection = (projection * cameraData.GetViewMatrix()).inverse;

                using (var builder = renderGraph.AddUnsafePass<TraceData>("GI/World Debug Trace", out var data))
                {
                    data.shader = settings.worldDebugShader;
                    data.kernel = settings.worldDebugShader.FindKernel("TraceWorldDebug");
                    data.output = output;
                    data.world = worldView;
                    data.inverseViewProjection = inverseViewProjection;
                    data.cameraPosition = cameraData.camera.transform.position;
                    data.width = width;
                    data.height = height;
                    data.maxDistance = settings.maxTraceDistance;
                    data.debugMode = (int)settings.debugMode;
                    data.worldGeneration = worldView.generation;
                    builder.UseTexture(output, AccessFlags.Write);
                    builder.UseBuffer(renderGraph.ImportBuffer(worldView.levels), AccessFlags.Read);
                    builder.UseBuffer(renderGraph.ImportBuffer(worldView.pageTable), AccessFlags.Read);
                    builder.UseBuffer(renderGraph.ImportBuffer(worldView.bricks), AccessFlags.Read);
                    builder.UseBuffer(renderGraph.ImportBuffer(worldView.occupancy), AccessFlags.Read);
                    builder.UseBuffer(renderGraph.ImportBuffer(worldView.surface), AccessFlags.Read);
                    builder.UseBuffer(renderGraph.ImportBuffer(worldView.distance), AccessFlags.Read);
                    builder.AllowPassCulling(false);
                    builder.SetRenderFunc(static (TraceData pass, UnsafeGraphContext context) => ExecuteTrace(pass, context));
                }

                using (var builder = renderGraph.AddRasterRenderPass<CompositeData>("GI/World Debug Composite", out var data))
                {
                    data.debugTexture = output;
                    builder.UseTexture(output, AccessFlags.Read);
                    builder.SetRenderAttachment(resources.activeColorTexture, 0, AccessFlags.Write);
                    builder.AllowPassCulling(false);
                    builder.SetRenderFunc(static (CompositeData pass, RasterGraphContext context) =>
                    {
                        Blitter.BlitTexture(context.cmd, pass.debugTexture,
                            // Compute UAV rows and the URP color target use opposite
                            // vertical origins. Flip only at the presentation boundary;
                            // world-space tracing and cache data remain unchanged.
                            new Vector4(1f, -1f, 0f, 1f), 0f, false);
                    });
                }
            }

            void RecordScreenSurfaceDebug(RenderGraph renderGraph, ContextContainer frameData)
            {
                if (!frameData.Contains<GIScreenSurfaceData>())
                    return;
                TextureHandle radiance = frameData.Get<GIScreenSurfaceData>().radiance;
                UniversalResourceData resources = frameData.Get<UniversalResourceData>();
                UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
                if (!radiance.IsValid() || resources.isActiveTargetBackBuffer ||
                    !resources.activeColorTexture.IsValid())
                    return;

                int width = Mathf.Max(1, cameraData.scaledWidth);
                int height = Mathf.Max(1, cameraData.scaledHeight);
                TextureHandle visualized = renderGraph.CreateTexture(new TextureDesc(width, height)
                {
                    colorFormat = GraphicsFormat.R16G16B16A16_SFloat,
                    enableRandomWrite = true,
                    clearBuffer = false,
                    filterMode = FilterMode.Point,
                    name = "GI Screen Surface Debug"
                });
                using (var computeBuilder = renderGraph.AddUnsafePass<ScreenDebugData>(
                           "GI/Visualize Screen Surface", out var computeData))
                {
                    computeData.shader = settings.screenSurfaceShader;
                    computeData.kernel = settings.screenSurfaceShader.FindKernel("VisualizeScreenSurface");
                    computeData.source = radiance;
                    computeData.output = visualized;
                    computeData.mode = settings.debugMode == DebugMode.ScreenSurfaceValidity ? 1 : 0;
                    computeData.width = width;
                    computeData.height = height;
                    computeBuilder.UseTexture(radiance, AccessFlags.Read);
                    computeBuilder.UseTexture(visualized, AccessFlags.Write);
                    computeBuilder.AllowPassCulling(false);
                    computeBuilder.SetRenderFunc(static (ScreenDebugData data, UnsafeGraphContext context) =>
                    {
                        var cmd = context.cmd;
                        cmd.SetComputeTextureParam(data.shader, data.kernel,
                            ShaderIds.ScreenSurfaceInput, data.source);
                        cmd.SetComputeTextureParam(data.shader, data.kernel,
                            ShaderIds.ScreenSurfaceDebug, data.output);
                        cmd.SetComputeVectorParam(data.shader, ShaderIds.OutputSize,
                            new Vector4(data.width, data.height, 0f, 0f));
                        cmd.SetComputeIntParam(data.shader, ShaderIds.ScreenSurfaceDebugMode, data.mode);
                        cmd.DispatchCompute(data.shader, data.kernel,
                            (data.width + 7) / 8, (data.height + 7) / 8, 1);
                    });
                }

                using var builder = renderGraph.AddRasterRenderPass<CompositeData>(
                    "GI/Screen Surface Debug", out var data);
                data.debugTexture = visualized;
                builder.UseTexture(visualized, AccessFlags.Read);
                builder.SetRenderAttachment(resources.activeColorTexture, 0, AccessFlags.Write);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (CompositeData pass, RasterGraphContext context) =>
                {
                    Blitter.BlitTexture(context.cmd, pass.debugTexture,
                        new Vector4(1f, 1f, 0f, 0f), 0f, false);
                });
            }

            void RecordPrimaryProbeDebug(RenderGraph renderGraph, ContextContainer frameData)
            {
                if (!frameData.Contains<GIScreenProbeData>())
                    return;
                TextureHandle debug = frameData.Get<GIScreenProbeData>().debug;
                UniversalResourceData resources = frameData.Get<UniversalResourceData>();
                if (!debug.IsValid() || resources.isActiveTargetBackBuffer ||
                    !resources.activeColorTexture.IsValid())
                    return;

                using var builder = renderGraph.AddRasterRenderPass<CompositeData>(
                    "GI/Primary Probe Debug Composite", out var data);
                data.debugTexture = debug;
                builder.UseTexture(debug, AccessFlags.Read);
                builder.SetRenderAttachment(resources.activeColorTexture, 0, AccessFlags.Write);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (CompositeData pass, RasterGraphContext context) =>
                {
                    Blitter.BlitTexture(context.cmd, pass.debugTexture,
                        new Vector4(1f, 1f, 0f, 0f), 0f, false);
                });
            }

            static void ExecuteTrace(TraceData data, UnsafeGraphContext context)
            {
                var cmd = context.cmd;
                cmd.SetComputeMatrixParam(data.shader, ShaderIds.InverseViewProjection, data.inverseViewProjection);
                cmd.SetComputeVectorParam(data.shader, ShaderIds.CameraPosition, data.cameraPosition);
                cmd.SetComputeFloatParam(data.shader, ShaderIds.MaxDistance, data.maxDistance);
                cmd.SetComputeVectorParam(data.shader, ShaderIds.OutputSize,
                    new Vector4(data.width, data.height, 0f, 0f));
                cmd.SetComputeIntParam(data.shader, ShaderIds.DebugMode, data.debugMode);
                cmd.SetComputeIntParam(data.shader, ShaderIds.WorldGeneration,
                    unchecked((int)data.worldGeneration));
                cmd.SetComputeBufferParam(data.shader, data.kernel, ShaderIds.Levels, data.world.levels);
                cmd.SetComputeBufferParam(data.shader, data.kernel, ShaderIds.PageTable, data.world.pageTable);
                cmd.SetComputeBufferParam(data.shader, data.kernel, ShaderIds.Bricks, data.world.bricks);
                cmd.SetComputeBufferParam(data.shader, data.kernel, ShaderIds.Occupancy, data.world.occupancy);
                cmd.SetComputeBufferParam(data.shader, data.kernel, ShaderIds.Surface, data.world.surface);
                cmd.SetComputeBufferParam(data.shader, data.kernel, ShaderIds.Distance, data.world.distance);
                cmd.SetComputeTextureParam(data.shader, data.kernel, ShaderIds.DebugOutput, data.output);
                cmd.DispatchCompute(data.shader, data.kernel,
                    (data.width + 7) / 8, (data.height + 7) / 8, 1);
            }
        }

        static class ShaderIds
        {
            public static readonly int Levels = Shader.PropertyToID("_GIWorldLevels");
            public static readonly int PageTable = Shader.PropertyToID("_GIWorldPageTable");
            public static readonly int Bricks = Shader.PropertyToID("_GIWorldBricks");
            public static readonly int Occupancy = Shader.PropertyToID("_GIWorldOccupancy");
            public static readonly int Surface = Shader.PropertyToID("_GIWorldSurface");
            public static readonly int Distance = Shader.PropertyToID("_GIWorldDistance");
            public static readonly int DebugOutput = Shader.PropertyToID("_GIWorldDebugOutput");
            public static readonly int InverseViewProjection = Shader.PropertyToID("_GIInverseViewProjection");
            public static readonly int CameraPosition = Shader.PropertyToID("_GICameraPosition");
            public static readonly int MaxDistance = Shader.PropertyToID("_GIMaxTraceDistance");
            public static readonly int OutputSize = Shader.PropertyToID("_GIOutputSize");
            public static readonly int DebugMode = Shader.PropertyToID("_GIDebugMode");
            public static readonly int WorldGeneration = Shader.PropertyToID("_GIWorldCurrentGeneration");
            public static readonly int GBuffer0 = Shader.PropertyToID("_GBuffer0");
            public static readonly int GBuffer1 = Shader.PropertyToID("_GBuffer1");
            public static readonly int GBuffer2 = Shader.PropertyToID("_GBuffer2");
            public static readonly int CameraDepthTexture = Shader.PropertyToID("_CameraDepthTexture");
            public static readonly int SurfaceLightingTexture = Shader.PropertyToID("_GISurfaceLightingTexture");
            public static readonly int ScreenSurfaceRadiance = Shader.PropertyToID("_GIScreenSurfaceRadiance");
            public static readonly int MainLightDirection = Shader.PropertyToID("_GIMainLightDirection");
            public static readonly int MainLightColor = Shader.PropertyToID("_GIMainLightColor");
            public static readonly int HasMainLight = Shader.PropertyToID("_GIHasMainLight");
            public static readonly int ScreenSurfaceInput = Shader.PropertyToID("_GIScreenSurfaceInput");
            public static readonly int ScreenSurfaceDebug = Shader.PropertyToID("_GIScreenSurfaceDebug");
            public static readonly int ScreenSurfaceDebugMode = Shader.PropertyToID("_GIScreenSurfaceDebugMode");
        }
    }
}
