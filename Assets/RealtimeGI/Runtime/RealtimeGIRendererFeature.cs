using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace RealtimeGI
{
    /// <summary>
    /// Default product GI path.  It deliberately has no screen-space history, reservoirs,
    /// scene-colour input, HZB, or stochastic per-pixel visibility trace.  Screen pixels only
    /// query the persistent world radiance cache prepared by <see cref="GIClipmapSystem"/>.
    /// </summary>
    public sealed class RealtimeGIRendererFeature : ScriptableRendererFeature
    {
        [Serializable]
        public sealed class Settings
        {
            public bool enableInSceneView = true;
            [Min(0f)] public float diffuseIntensity = 1f;
            // Kept deliberately separate from diffuse.  The first world-cache milestone writes
            // zero glossy; the GGX cone implementation will consume this setting in stage two.
            public bool enableGlossy = true;
            [Min(0f)] public float glossyIntensity = 1f;
            public ComputeShader screenLightingShader;
            public Shader compositeShader;
        }

        [SerializeField] Settings settings = new Settings();

        Material compositeMaterial;
        ClipmapUpdatePass clipmapUpdatePass;
        GlobalFloatPass enableInjectionPass;
        GlobalFloatPass resetInjectionPass;
        EnvironmentReflectionPass disableEnvironmentReflectionsPass;
        EnvironmentReflectionPass restoreEnvironmentReflectionsPass;
        WorldLightingPass worldLightingPass;

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

            clipmapUpdatePass = new ClipmapUpdatePass
            {
                renderPassEvent = RenderPassEvent.BeforeRenderingGbuffer
            };
            disableEnvironmentReflectionsPass = new EnvironmentReflectionPass(true)
            {
                renderPassEvent = RenderPassEvent.BeforeRenderingGbuffer
            };
            enableInjectionPass = new GlobalFloatPass(ShaderIds.DeferredInjectionActive, 1f)
            {
                renderPassEvent = RenderPassEvent.BeforeRenderingGbuffer
            };
            worldLightingPass = new WorldLightingPass
            {
                renderPassEvent = RenderPassEvent.BeforeRenderingDeferredLights,
                requiresIntermediateTexture = true
            };
            resetInjectionPass = new GlobalFloatPass(ShaderIds.DeferredInjectionActive, 0f)
            {
                renderPassEvent = RenderPassEvent.AfterRenderingDeferredLights
            };
            restoreEnvironmentReflectionsPass = new EnvironmentReflectionPass(false)
            {
                // Transparent materials have no matching GI injection path yet.  Keeping the
                // native environment disabled through this point is intentional: GI enabled
                // means no hidden probe/Unity-sky contribution, not "opaque only" replacement.
                renderPassEvent = RenderPassEvent.AfterRendering
            };
        }

        protected override void Dispose(bool disposing)
        {
            CoreUtils.Destroy(compositeMaterial);
            compositeMaterial = null;
            clipmapUpdatePass = null;
            enableInjectionPass = null;
            resetInjectionPass = null;
            disableEnvironmentReflectionsPass = null;
            restoreEnvironmentReflectionsPass = null;
            worldLightingPass = null;
            Shader.SetGlobalFloat(ShaderIds.DeferredInjectionActive, 0f);
            Shader.DisableKeyword(EnvironmentReflectionsOff);
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (worldLightingPass == null || clipmapUpdatePass == null ||
                enableInjectionPass == null || resetInjectionPass == null ||
                disableEnvironmentReflectionsPass == null || restoreEnvironmentReflectionsPass == null ||
                compositeMaterial == null || settings.screenLightingShader == null ||
                !SystemInfo.supportsComputeShaders)
                return;

            CameraType cameraType = renderingData.cameraData.cameraType;
            if (cameraType != CameraType.Game &&
                !(settings.enableInSceneView && cameraType == CameraType.SceneView))
                return;

            GIClipmapSystem clipmaps = GIClipmapSystem.Active;
            if (clipmaps == null || clipmaps.Scene == null ||
                !clipmaps.PrepareClipmaps(renderingData.cameraData.camera) ||
                !clipmaps.TryGetGpuView(out GIClipmapGpuView clipmapView))
                return;

            clipmapUpdatePass.Setup(clipmaps);
            worldLightingPass.Setup(settings, compositeMaterial, clipmapView);
            renderer.EnqueuePass(clipmapUpdatePass);
            renderer.EnqueuePass(disableEnvironmentReflectionsPass);
            renderer.EnqueuePass(enableInjectionPass);
            renderer.EnqueuePass(worldLightingPass);
            renderer.EnqueuePass(resetInjectionPass);
            renderer.EnqueuePass(restoreEnvironmentReflectionsPass);
        }

        sealed class ClipmapUpdatePass : ScriptableRenderPass
        {
            GIClipmapSystem owner;

            public void Setup(GIClipmapSystem clipmaps) => owner = clipmaps;

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData) =>
                owner?.RecordRenderGraph(renderGraph);
        }

        sealed class GlobalFloatPass : ScriptableRenderPass
        {
            sealed class PassData
            {
                public int id;
                public float value;
            }

            readonly int id;
            readonly float value;

            public GlobalFloatPass(int id, float value)
            {
                this.id = id;
                this.value = value;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                using var builder = renderGraph.AddUnsafePass<PassData>(
                    "RealtimeGI/Set Deferred Injection State", out PassData data);
                data.id = id;
                data.value = value;
                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc(static (PassData passData, UnsafeGraphContext context) =>
                    context.cmd.SetGlobalFloat(passData.id, passData.value));
            }
        }

        sealed class EnvironmentReflectionPass : ScriptableRenderPass
        {
            sealed class PassData { public bool disabled; }
            readonly bool disabled;

            public EnvironmentReflectionPass(bool disabled) => this.disabled = disabled;

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                using var builder = renderGraph.AddUnsafePass<PassData>(
                    disabled ? "RealtimeGI/Disable Native Environment Reflections" :
                               "RealtimeGI/Restore Native Environment Reflections",
                    out PassData data);
                data.disabled = disabled;
                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc(static (PassData passData, UnsafeGraphContext context) =>
                {
                    if (passData.disabled)
                        context.cmd.EnableShaderKeyword(EnvironmentReflectionsOff);
                    else
                        context.cmd.DisableShaderKeyword(EnvironmentReflectionsOff);
                });
            }
        }

        sealed class WorldLightingPass : ScriptableRenderPass
        {
            sealed class ComputePassData
            {
                public ComputeShader shader;
                public int kernel;
                public TextureHandle depth;
                public TextureHandle normals;
                public TextureHandle gBuffer0;
                public TextureHandle gBuffer1;
                public TextureHandle gBuffer2;
                public TextureHandle diffuse;
                public TextureHandle glossy;
                public GraphicsBuffer levelData;
                public GraphicsBuffer staticPageTable;
                public GraphicsBuffer staticOccupancy;
                public GraphicsBuffer staticSurface;
                public GraphicsBuffer staticRadiance;
                public GraphicsBuffer staticValidity;
                public GraphicsBuffer staticIrradiance;
                public GraphicsBuffer staticIrradianceValidity;
                public GraphicsBuffer dynamicPageTable;
                public GraphicsBuffer dynamicOccupancy;
                public GraphicsBuffer dynamicSurface;
                public GraphicsBuffer dynamicRadiance;
                public GraphicsBuffer dynamicValidity;
                public GraphicsBuffer dynamicIrradiance;
                public GraphicsBuffer dynamicIrradianceValidity;
                public Matrix4x4 inverseViewProjection;
                public int width;
                public int height;
                public float diffuseIntensity;
                public float glossyIntensity;
                public int glossyEnabled;
            }

            sealed class InjectionPassData
            {
                public Material material;
                public TextureHandle diffuse;
                public TextureHandle glossy;
                public TextureHandle gBuffer0;
                public TextureHandle gBuffer1;
                public float glossyIntensity;
            }

            Settings settings;
            Material compositeMaterial;
            GIClipmapGpuView clipmapView;
            int gatherKernel = -1;

            public WorldLightingPass()
            {
                ConfigureInput(ScriptableRenderPassInput.Depth | ScriptableRenderPassInput.Normal);
            }

            public void Setup(Settings nextSettings, Material nextCompositeMaterial,
                GIClipmapGpuView nextClipmapView)
            {
                settings = nextSettings;
                compositeMaterial = nextCompositeMaterial;
                clipmapView = nextClipmapView;
                gatherKernel = settings.screenLightingShader.FindKernel("GatherWorldDiffuse");
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                if (settings == null || compositeMaterial == null || gatherKernel < 0)
                    return;

                UniversalResourceData resources = frameData.Get<UniversalResourceData>();
                UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
                if (resources.isActiveTargetBackBuffer || !resources.cameraDepthTexture.IsValid() ||
                    !resources.activeDepthTexture.IsValid() || resources.gBuffer == null ||
                    resources.gBuffer.Length < 3 || !resources.gBuffer[0].IsValid() ||
                    !resources.gBuffer[1].IsValid() || !resources.gBuffer[2].IsValid())
                    return;

                int width = Mathf.Max(1, cameraData.scaledWidth);
                int height = Mathf.Max(1, cameraData.scaledHeight);
                TextureDesc outputDesc = new TextureDesc(width, height)
                {
                    colorFormat = GraphicsFormat.R16G16B16A16_SFloat,
                    enableRandomWrite = true,
                    clearBuffer = false,
                    filterMode = FilterMode.Point
                };
                outputDesc.name = "GI World Diffuse Irradiance";
                TextureHandle diffuse = renderGraph.CreateTexture(outputDesc);
                outputDesc.name = "GI World Glossy (stage two placeholder)";
                TextureHandle glossy = renderGraph.CreateTexture(outputDesc);

                Matrix4x4 projection = GL.GetGPUProjectionMatrix(cameraData.GetProjectionMatrix(), true);
                Matrix4x4 inverseViewProjection = (projection * cameraData.GetViewMatrix()).inverse;
                using (var builder = renderGraph.AddUnsafePass<ComputePassData>(
                           "RealtimeGI/World Cache Diffuse", out ComputePassData data))
                {
                    data.shader = settings.screenLightingShader;
                    data.kernel = gatherKernel;
                    data.depth = resources.cameraDepthTexture;
                    data.normals = resources.gBuffer[2];
                    data.gBuffer0 = resources.gBuffer[0];
                    data.gBuffer1 = resources.gBuffer[1];
                    data.gBuffer2 = resources.gBuffer[2];
                    data.diffuse = diffuse;
                    data.glossy = glossy;
                    data.levelData = clipmapView.levelData;
                    data.staticPageTable = clipmapView.staticPageTable;
                    data.staticOccupancy = clipmapView.staticOccupancy;
                    data.staticSurface = clipmapView.staticSurface;
                    data.staticRadiance = clipmapView.staticRadiance;
                    data.staticValidity = clipmapView.staticValidity;
                    data.staticIrradiance = clipmapView.staticIrradiance;
                    data.staticIrradianceValidity = clipmapView.staticIrradianceValidity;
                    data.dynamicPageTable = clipmapView.dynamicPageTable;
                    data.dynamicOccupancy = clipmapView.dynamicOccupancy;
                    data.dynamicSurface = clipmapView.dynamicSurface;
                    data.dynamicRadiance = clipmapView.dynamicRadiance;
                    data.dynamicValidity = clipmapView.dynamicValidity;
                    data.dynamicIrradiance = clipmapView.dynamicIrradiance;
                    data.dynamicIrradianceValidity = clipmapView.dynamicIrradianceValidity;
                    data.inverseViewProjection = inverseViewProjection;
                    data.width = width;
                    data.height = height;
                    data.diffuseIntensity = settings.diffuseIntensity;
                    data.glossyIntensity = settings.glossyIntensity;
                    data.glossyEnabled = settings.enableGlossy ? 1 : 0;

                    builder.UseTexture(data.depth, AccessFlags.Read);
                    builder.UseTexture(data.normals, AccessFlags.Read);
                    builder.UseTexture(data.gBuffer0, AccessFlags.Read);
                    builder.UseTexture(data.gBuffer1, AccessFlags.Read);
                    builder.UseTexture(data.gBuffer2, AccessFlags.Read);
                    builder.UseTexture(data.diffuse, AccessFlags.Write);
                    builder.UseTexture(data.glossy, AccessFlags.Write);
                    builder.UseBuffer(renderGraph.ImportBuffer(data.levelData), AccessFlags.Read);
                    builder.UseBuffer(renderGraph.ImportBuffer(data.staticPageTable), AccessFlags.Read);
                    builder.UseBuffer(renderGraph.ImportBuffer(data.staticOccupancy), AccessFlags.Read);
                    builder.UseBuffer(renderGraph.ImportBuffer(data.staticSurface), AccessFlags.Read);
                    builder.UseBuffer(renderGraph.ImportBuffer(data.staticRadiance), AccessFlags.Read);
                    builder.UseBuffer(renderGraph.ImportBuffer(data.staticValidity), AccessFlags.Read);
                    builder.UseBuffer(renderGraph.ImportBuffer(data.staticIrradiance), AccessFlags.Read);
                    builder.UseBuffer(renderGraph.ImportBuffer(data.staticIrradianceValidity), AccessFlags.Read);
                    builder.UseBuffer(renderGraph.ImportBuffer(data.dynamicPageTable), AccessFlags.Read);
                    builder.UseBuffer(renderGraph.ImportBuffer(data.dynamicOccupancy), AccessFlags.Read);
                    builder.UseBuffer(renderGraph.ImportBuffer(data.dynamicSurface), AccessFlags.Read);
                    builder.UseBuffer(renderGraph.ImportBuffer(data.dynamicRadiance), AccessFlags.Read);
                    builder.UseBuffer(renderGraph.ImportBuffer(data.dynamicValidity), AccessFlags.Read);
                    builder.UseBuffer(renderGraph.ImportBuffer(data.dynamicIrradiance), AccessFlags.Read);
                    builder.UseBuffer(renderGraph.ImportBuffer(data.dynamicIrradianceValidity), AccessFlags.Read);
                    builder.AllowPassCulling(false);
                    builder.SetRenderFunc(static (ComputePassData passData, UnsafeGraphContext context) =>
                        Execute(passData, context));
                }

                using (var builder = renderGraph.AddRasterRenderPass<InjectionPassData>(
                           "RealtimeGI/Inject World Cache Indirect", out InjectionPassData data))
                {
                    data.material = compositeMaterial;
                    data.diffuse = diffuse;
                    data.glossy = glossy;
                    data.gBuffer0 = resources.gBuffer[0];
                    data.gBuffer1 = resources.gBuffer[1];
                    data.glossyIntensity = settings.enableGlossy ? settings.glossyIntensity : 0f;
                    builder.UseTexture(data.diffuse, AccessFlags.Read);
                    builder.UseTexture(data.glossy, AccessFlags.Read);
                    builder.UseTexture(data.gBuffer0, AccessFlags.Read);
                    builder.UseTexture(data.gBuffer1, AccessFlags.Read);
                    builder.SetRenderAttachment(resources.activeColorTexture, 0, AccessFlags.ReadWrite);
                    builder.SetRenderAttachmentDepth(resources.activeDepthTexture, AccessFlags.Read);
                    builder.AllowPassCulling(false);
                    builder.AllowGlobalStateModification(true);
                    builder.SetRenderFunc(static (InjectionPassData passData, RasterGraphContext context) =>
                    {
                        context.cmd.SetGlobalTexture(ShaderIds.LightingTexture, passData.diffuse);
                        context.cmd.SetGlobalTexture(ShaderIds.SpecularLightingTexture, passData.glossy);
                        context.cmd.SetGlobalTexture(ShaderIds.GBuffer0, passData.gBuffer0);
                        context.cmd.SetGlobalTexture(ShaderIds.GBuffer1, passData.gBuffer1);
                        context.cmd.SetGlobalFloat(ShaderIds.SpecularIntensity, passData.glossyIntensity);
                        context.cmd.SetGlobalFloat(ShaderIds.InjectionBlend, 1f);
                        context.cmd.DrawProcedural(Matrix4x4.identity, passData.material, 0,
                            MeshTopology.Triangles, 3, 1);
                    });
                }
            }

            static void Execute(ComputePassData data, UnsafeGraphContext context)
            {
                var cmd = context.cmd;
                ComputeShader shader = data.shader;
                int kernel = data.kernel;
                cmd.SetComputeMatrixParam(shader, ShaderIds.InverseViewProjection,
                    data.inverseViewProjection);
                cmd.SetComputeVectorParam(shader, ShaderIds.ViewportSize,
                    new Vector4(data.width, data.height, 1f / data.width, 1f / data.height));
                cmd.SetComputeFloatParam(shader, ShaderIds.DiffuseIntensity, data.diffuseIntensity);
                cmd.SetComputeFloatParam(shader, ShaderIds.SpecularIntensity, data.glossyIntensity);
                cmd.SetComputeIntParam(shader, ShaderIds.EnableSpecular, data.glossyEnabled);
                cmd.SetComputeTextureParam(shader, kernel, ShaderIds.DepthTexture, data.depth);
                cmd.SetComputeTextureParam(shader, kernel, ShaderIds.NormalsTexture, data.normals);
                cmd.SetComputeTextureParam(shader, kernel, ShaderIds.GBuffer0, data.gBuffer0);
                cmd.SetComputeTextureParam(shader, kernel, ShaderIds.GBuffer1, data.gBuffer1);
                cmd.SetComputeTextureParam(shader, kernel, ShaderIds.GBuffer2, data.gBuffer2);
                cmd.SetComputeTextureParam(shader, kernel, ShaderIds.FullLighting, data.diffuse);
                cmd.SetComputeTextureParam(shader, kernel, ShaderIds.SpecularLightingTexture, data.glossy);
                cmd.SetComputeBufferParam(shader, kernel, ShaderIds.Levels, data.levelData);
                cmd.SetComputeBufferParam(shader, kernel, ShaderIds.StaticPageTable, data.staticPageTable);
                cmd.SetComputeBufferParam(shader, kernel, ShaderIds.StaticOccupancy, data.staticOccupancy);
                cmd.SetComputeBufferParam(shader, kernel, ShaderIds.StaticSurface, data.staticSurface);
                cmd.SetComputeBufferParam(shader, kernel, ShaderIds.StaticRadiance, data.staticRadiance);
                cmd.SetComputeBufferParam(shader, kernel, ShaderIds.StaticValidity, data.staticValidity);
                cmd.SetComputeBufferParam(shader, kernel, ShaderIds.StaticIrradiance, data.staticIrradiance);
                cmd.SetComputeBufferParam(shader, kernel, ShaderIds.StaticIrradianceValidity, data.staticIrradianceValidity);
                cmd.SetComputeBufferParam(shader, kernel, ShaderIds.DynamicPageTable, data.dynamicPageTable);
                cmd.SetComputeBufferParam(shader, kernel, ShaderIds.DynamicOccupancy, data.dynamicOccupancy);
                cmd.SetComputeBufferParam(shader, kernel, ShaderIds.DynamicSurface, data.dynamicSurface);
                cmd.SetComputeBufferParam(shader, kernel, ShaderIds.DynamicRadiance, data.dynamicRadiance);
                cmd.SetComputeBufferParam(shader, kernel, ShaderIds.DynamicValidity, data.dynamicValidity);
                cmd.SetComputeBufferParam(shader, kernel, ShaderIds.DynamicIrradiance, data.dynamicIrradiance);
                cmd.SetComputeBufferParam(shader, kernel, ShaderIds.DynamicIrradianceValidity, data.dynamicIrradianceValidity);
                cmd.BeginSample("RealtimeGI/World Diffuse Query");
                cmd.DispatchCompute(shader, kernel, DivRoundUp(data.width, 8), DivRoundUp(data.height, 8), 1);
                cmd.EndSample("RealtimeGI/World Diffuse Query");
            }

            static int DivRoundUp(int value, int divisor) => (value + divisor - 1) / divisor;
        }

        const string EnvironmentReflectionsOff = "_ENVIRONMENTREFLECTIONS_OFF";

        static class ShaderIds
        {
            public static readonly int DeferredInjectionActive = Shader.PropertyToID("_RealtimeGIDeferredInjection");
            public static readonly int DepthTexture = Shader.PropertyToID("_GIDepthTexture");
            public static readonly int NormalsTexture = Shader.PropertyToID("_GINormalsTexture");
            public static readonly int GBuffer0 = Shader.PropertyToID("_GIGBuffer0");
            public static readonly int GBuffer1 = Shader.PropertyToID("_GIGBuffer1");
            public static readonly int GBuffer2 = Shader.PropertyToID("_GIGBuffer2");
            public static readonly int FullLighting = Shader.PropertyToID("_GIFullLighting");
            public static readonly int LightingTexture = Shader.PropertyToID("_GILightingTexture");
            public static readonly int SpecularLightingTexture = Shader.PropertyToID("_GISpecularLightingTexture");
            public static readonly int InverseViewProjection = Shader.PropertyToID("_GIInverseViewProjection");
            public static readonly int ViewportSize = Shader.PropertyToID("_GIViewportSize");
            public static readonly int DiffuseIntensity = Shader.PropertyToID("_GIDiffuseIntensity");
            public static readonly int SpecularIntensity = Shader.PropertyToID("_GISpecularIntensity");
            public static readonly int EnableSpecular = Shader.PropertyToID("_GIEnableSpecular");
            public static readonly int Levels = Shader.PropertyToID("_GIClipmapLevels");
            public static readonly int StaticPageTable = Shader.PropertyToID("_GIStaticPageTable");
            public static readonly int StaticOccupancy = Shader.PropertyToID("_GIStaticOccupancy");
            public static readonly int StaticSurface = Shader.PropertyToID("_GIStaticSurface");
            public static readonly int StaticRadiance = Shader.PropertyToID("_GIStaticRadiance");
            public static readonly int StaticValidity = Shader.PropertyToID("_GIStaticValidity");
            public static readonly int StaticIrradiance = Shader.PropertyToID("_GIStaticIrradiance");
            public static readonly int StaticIrradianceValidity = Shader.PropertyToID("_GIStaticIrradianceValidity");
            public static readonly int DynamicPageTable = Shader.PropertyToID("_GIDynamicPageTable");
            public static readonly int DynamicOccupancy = Shader.PropertyToID("_GIDynamicOccupancy");
            public static readonly int DynamicSurface = Shader.PropertyToID("_GIDynamicSurface");
            public static readonly int DynamicRadiance = Shader.PropertyToID("_GIDynamicRadiance");
            public static readonly int DynamicValidity = Shader.PropertyToID("_GIDynamicValidity");
            public static readonly int DynamicIrradiance = Shader.PropertyToID("_GIDynamicIrradiance");
            public static readonly int DynamicIrradianceValidity = Shader.PropertyToID("_GIDynamicIrradianceValidity");
            public static readonly int InjectionBlend = Shader.PropertyToID("_GIInjectionBlend");
        }
    }
}
