using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace UnityNanite.GI
{
    internal sealed class GIDiffuseCompositePass : ScriptableRenderPass, System.IDisposable
    {
        readonly GIWorldRendererFeature.Settings settings;
        Material material;

        sealed class PassData
        {
            public Material material;
            public TextureHandle irradiance;
            public TextureHandle specular;
            public TextureHandle gBuffer0;
            public TextureHandle gBuffer1;
            public TextureHandle gBuffer2;
            public float intensity;
            public bool stylizedDiffuse;
            public float stylizedShadowThreshold;
            public float stylizedShadowSoftness;
        }

        public GIDiffuseCompositePass(GIWorldRendererFeature.Settings settings)
        {
            this.settings = settings;
            requiresIntermediateTexture = true;
            if (settings.diffuseCompositeShader != null)
                material = CoreUtils.CreateEngineMaterial(settings.diffuseCompositeShader);
        }

        public bool IsReady => material != null;

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (material == null || !frameData.Contains<GIScreenProbeData>())
                return;
            GIScreenProbeData probes = frameData.Get<GIScreenProbeData>();
            UniversalResourceData resources = frameData.Get<UniversalResourceData>();
            if (!resources.realtimeGIDiffuseEnabled || !probes.resolvedIrradiance.IsValid() ||
                !probes.resolvedSpecular.IsValid() ||
                resources.gBuffer == null || resources.gBuffer.Length < 3 ||
                !resources.gBuffer[0].IsValid() || !resources.gBuffer[1].IsValid() ||
                !resources.gBuffer[2].IsValid() ||
                !resources.activeColorTexture.IsValid() || !resources.activeDepthTexture.IsValid())
                return;

            using var builder = renderGraph.AddRasterRenderPass<PassData>(
                "GI/Diffuse Composite", out var data);
            data.material = material;
            data.irradiance = probes.resolvedIrradiance;
            data.specular = probes.resolvedSpecular;
            data.gBuffer0 = resources.gBuffer[0];
            data.gBuffer1 = resources.gBuffer[1];
            data.gBuffer2 = resources.gBuffer[2];
            data.intensity = settings.diffuseIntensity;
            data.stylizedDiffuse = settings.stylizedDiffuse;
            data.stylizedShadowThreshold = settings.stylizedShadowThreshold;
            data.stylizedShadowSoftness = settings.stylizedShadowSoftness;
            builder.UseTexture(data.irradiance, AccessFlags.Read);
            builder.UseTexture(data.specular, AccessFlags.Read);
            builder.UseTexture(data.gBuffer0, AccessFlags.Read);
            builder.UseTexture(data.gBuffer1, AccessFlags.Read);
            builder.UseTexture(data.gBuffer2, AccessFlags.Read);
            builder.SetRenderAttachment(resources.activeColorTexture, 0, AccessFlags.ReadWrite);
            builder.SetRenderAttachmentDepth(resources.activeDepthTexture, AccessFlags.Read);
            builder.AllowPassCulling(false);
            builder.AllowGlobalStateModification(true);
            builder.SetRenderFunc(static (PassData pass, RasterGraphContext context) =>
            {
                context.cmd.SetGlobalTexture(Ids.Irradiance, pass.irradiance);
                context.cmd.SetGlobalTexture(Ids.Specular, pass.specular);
                context.cmd.SetGlobalTexture(Ids.GBuffer0, pass.gBuffer0);
                context.cmd.SetGlobalTexture(Ids.GBuffer1, pass.gBuffer1);
                context.cmd.SetGlobalTexture(Ids.GBuffer2, pass.gBuffer2);
                context.cmd.SetGlobalFloat(Ids.Intensity, pass.intensity);
                context.cmd.SetGlobalFloat(Ids.StylizedDiffuse, pass.stylizedDiffuse ? 1f : 0f);
                context.cmd.SetGlobalFloat(Ids.StylizedShadowThreshold, pass.stylizedShadowThreshold);
                context.cmd.SetGlobalFloat(Ids.StylizedShadowSoftness, pass.stylizedShadowSoftness);
                context.cmd.DrawProcedural(
                    Matrix4x4.identity, pass.material, 0, MeshTopology.Triangles, 3, 1);
                context.cmd.DrawProcedural(
                    Matrix4x4.identity, pass.material, 1, MeshTopology.Triangles, 3, 1);
            });
        }

        public void Dispose()
        {
            CoreUtils.Destroy(material);
            material = null;
        }

        static class Ids
        {
            public static readonly int Irradiance = Shader.PropertyToID("_GIIrradiance");
            public static readonly int Specular = Shader.PropertyToID("_GISpecular");
            public static readonly int GBuffer0 = Shader.PropertyToID("_GIGBuffer0");
            public static readonly int GBuffer1 = Shader.PropertyToID("_GIGBuffer1");
            public static readonly int GBuffer2 = Shader.PropertyToID("_GIGBuffer2");
            public static readonly int Intensity = Shader.PropertyToID("_GIDiffuseIntensity");
            public static readonly int StylizedDiffuse = Shader.PropertyToID("_GIStylizedDiffuse");
            public static readonly int StylizedShadowThreshold = Shader.PropertyToID("_GIStylizedShadowThreshold");
            public static readonly int StylizedShadowSoftness = Shader.PropertyToID("_GIStylizedShadowSoftness");
        }
    }
}
