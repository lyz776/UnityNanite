using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;

namespace UnityNanite.TOD
{
    public sealed class TODFogRendererFeature : ScriptableRendererFeature
    {
        [SerializeField] private RenderPassEvent injectionPoint = RenderPassEvent.BeforeRenderingTransparents;
        [SerializeField] private bool sceneView = true;

        private Material material;
        private TODFogPass pass;

        public override void Create()
        {
            Shader shader = Shader.Find("Hidden/Unity Nanite/TOD Fog");
            if (shader != null)
                material = CoreUtils.CreateEngineMaterial(shader);

            pass = new TODFogPass
            {
                renderPassEvent = injectionPoint,
                SceneViewEnabled = sceneView
            };
        }

        protected override void Dispose(bool disposing)
        {
            CoreUtils.Destroy(material);
            material = null;
            pass?.Dispose();
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (material == null || pass == null)
                return;

            CameraType cameraType = renderingData.cameraData.cameraType;
            if (cameraType != CameraType.Game &&
                !(sceneView && cameraType == CameraType.SceneView))
                return;

            pass.Setup(material);
            renderer.EnqueuePass(pass);
        }

        private sealed class TODFogPass : ScriptableRenderPass
        {
            private const string PassName = "TOD Fog Before Transparents";
            private Material material;
            private RTHandle compatibilityTemp;

            public bool SceneViewEnabled { get; set; }

            public TODFogPass()
            {
                requiresIntermediateTexture = true;
                ConfigureInput(ScriptableRenderPassInput.Depth);
            }

            public void Setup(Material value)
            {
                material = value;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                if (material == null)
                    return;

                UniversalResourceData resources = frameData.Get<UniversalResourceData>();
                if (resources.isActiveTargetBackBuffer)
                    return;

                TextureHandle source = resources.activeColorTexture;
                TextureDesc descriptor = renderGraph.GetTextureDesc(source);
                descriptor.name = "TOD Fog Camera Color";
                descriptor.clearBuffer = false;
                TextureHandle destination = renderGraph.CreateTexture(descriptor);

                var parameters = new RenderGraphUtils.BlitMaterialParameters(source, destination, material, 0);
                renderGraph.AddBlitPass(parameters, PassName);
                resources.cameraColor = destination;
            }

#pragma warning disable CS0618
            [System.Obsolete("Compatibility path for URP RenderGraph-disabled projects.")]
            public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
            {
                RenderTextureDescriptor descriptor = renderingData.cameraData.cameraTargetDescriptor;
                descriptor.depthBufferBits = 0;
                RenderingUtils.ReAllocateHandleIfNeeded(
                    ref compatibilityTemp,
                    descriptor,
                    FilterMode.Bilinear,
                    TextureWrapMode.Clamp,
                    name: "_TODFogTemp");
            }

            [System.Obsolete("Compatibility path for URP RenderGraph-disabled projects.")]
            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                if (material == null || compatibilityTemp == null)
                    return;

                RTHandle source = renderingData.cameraData.renderer.cameraColorTargetHandle;
                CommandBuffer cmd = CommandBufferPool.Get(PassName);
                Blitter.BlitCameraTexture(cmd, source, compatibilityTemp, material, 0);
                Blitter.BlitCameraTexture(cmd, compatibilityTemp, source);
                context.ExecuteCommandBuffer(cmd);
                CommandBufferPool.Release(cmd);
            }
#pragma warning restore CS0618

            public void Dispose()
            {
                compatibilityTemp?.Release();
                compatibilityTemp = null;
            }
        }
    }
}
