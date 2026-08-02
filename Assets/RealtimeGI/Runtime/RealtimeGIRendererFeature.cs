using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;

namespace RealtimeGI
{
    public sealed class RealtimeGIRendererFeature : ScriptableRendererFeature
    {
        public enum DiffuseDebugStage
        {
            Final = 0,
            SpatialRaw = 1,
            TemporalAccumulated = 2,
            ReprojectionValidity = 3,
            MotionVectors = 4
        }

        [Serializable]
        public sealed class Settings
        {
            public bool enableInSceneView = true;
            public bool enableDiffuse = true;
            [Tooltip("Linear diffuse buffer scale. 0.25 is the quality default; 0.125 is the performance fallback for 4K software tracing.")]
            [Range(0.125f, 0.5f)] public float diffuseResolutionScale = 0.25f;
            [Tooltip("Fresh path candidates per low-resolution pixel. ReSTIR is designed around one candidate; use resolution before increasing this value.")]
            [Range(1, 4)] public int diffuseRaysPerProbe = 1;
            [Tooltip("Final denoiser history blend only. It no longer throttles candidate generation or changes reservoir M.")]
            [Range(0f, 0.98f)] public float diffuseHistoryWeight = 0.9f;
            [Min(0f)] public float diffuseIntensity = 1f;
            [Min(1f)] public float maxTraceDistance = 150f;
            [Tooltip("Maximum distance for the HZB screen-space pre-trace. World clipmap misses still use Max Trace Distance.")]
            [Min(0f)] public float screenTraceDistance = 12f;
            [Tooltip("Neighbour reservoirs considered by diffuse spatial reuse. Zero disables spatial reuse without changing temporal sampling.")]
            [Range(0, 8)] public int diffuseSpatialSamples = 8;
            [Tooltip("Maximum spatial reuse radius in low-resolution diffuse pixels.")]
            [Range(1f, 16f)] public float diffuseSpatialRadius = 6f;
            [Tooltip("Reuse previous lit scene colour at secondary screen hits. Disabled by default because feeding the composited GI result back into the next frame is not a bounded path estimator.")]
            public bool enableSceneColorHistory = false;
            [Header("Software specular indirect")]
            [Tooltip("AKGI-style hybrid software GGX final gather. This replaces reflection-probe IBL; hardware RT is not required.")]
            public bool enableSpecular = true;
            [Tooltip("Initial GGX trace resolution. Resolve, temporal and spatial reconstruction remain full resolution.")]
            [Range(0.25f, 0.5f)] public float specularResolutionScale = 0.5f;
            [Range(0f, 0.98f)] public float specularHistoryWeight = 0.9f;
            [Min(0f)] public float specularIntensity = 1f;
            [Tooltip("Minimum traced perceptual roughness. Zero keeps perfectly smooth surfaces eligible; the GGX math applies its own numerical floor.")]
            [Range(0f, 1f)] public float minSpecularRoughness = 0f;
            [Range(0f, 1f)] public float maxSpecularRoughness = 1f;
            [Range(1, 8)] public int specularSpatialSamples = 4;
            [Tooltip("Diagnostic only: show clipmap-hit material base color instead of cached radiance.")]
            public bool debugHitMaterialColor;
            [Tooltip("Stage-isolation view. Final is the production output; other values identify the first pass that introduces an artifact.")]
            public DiffuseDebugStage diffuseDebugStage = DiffuseDebugStage.Final;
            public bool enableTraceCounters;
            [Range(10, 240)] public int traceCounterReadbackInterval = 30;
            [HideInInspector] public int pipelineVersion;
            public ComputeShader screenLightingShader;
            public Shader compositeShader;
        }

        [SerializeField] Settings settings = new Settings();

        Material compositeMaterial;
        GIInjectionStatePass prepareInjectionPass;
        GIInjectionStatePass resetInjectionPass;
        GIKeywordStatePass disableNativeReflectionsPass;
        GIKeywordStatePass restoreNativeReflectionsPass;
        ScreenLightingPass pass;
        SceneColorHistoryPass sceneColorHistoryPass;

        public readonly struct TraceCounterSnapshot
        {
            public readonly uint rays;
            public readonly uint screenHits;
            public readonly uint worldSteps;
            public readonly uint worldHits;
            public readonly uint worldMisses;
            public readonly uint validCacheHits;
            public readonly uint invalidCacheHits;
            public readonly uint screenGeometryHits;
            public readonly uint temporalMatches;
            public readonly uint temporalReuseCandidates;
            public readonly uint spatialReuseCandidates;
            public readonly uint spatialVisibilityRejected;

            internal TraceCounterSnapshot(uint[] values)
            {
                rays = values[0];
                screenHits = values[1];
                worldSteps = values[2];
                worldHits = values[3];
                worldMisses = values[4];
                // A pending AsyncGPUReadback issued by an older domain can complete after a
                // script reload. Keep the snapshot backwards-compatible with the former
                // five-counter layout instead of letting that stale callback throw.
                validCacheHits = values.Length > 5 ? values[5] : 0u;
                invalidCacheHits = values.Length > 6 ? values[6] : 0u;
                screenGeometryHits = values.Length > 7 ? values[7] : 0u;
                temporalMatches = values.Length > 8 ? values[8] : 0u;
                temporalReuseCandidates = values.Length > 9 ? values[9] : 0u;
                spatialReuseCandidates = values.Length > 10 ? values[10] : 0u;
                spatialVisibilityRejected = values.Length > 11 ? values[11] : 0u;
            }

            public float AverageWorldSteps => worldHits + worldMisses > 0
                ? (float)worldSteps / (worldHits + worldMisses)
                : 0f;

            public float ValidCachePercent => worldHits > 0
                ? validCacheHits * 100f / worldHits
                : 0f;
        }

        public static TraceCounterSnapshot LatestTraceCounters { get; private set; }

        public override void Create()
        {
            if (settings.pipelineVersion < 3)
            {
                settings.diffuseRaysPerProbe = 1;
                settings.diffuseHistoryWeight = Mathf.Min(settings.diffuseHistoryWeight, 0.9f);
                settings.pipelineVersion = 3;
#if UNITY_EDITOR
                UnityEditor.EditorUtility.SetDirty(this);
#endif
            }
            if (settings.pipelineVersion < 4)
            {
                settings.diffuseSpatialSamples = 8;
                settings.diffuseSpatialRadius = 6f;
                settings.pipelineVersion = 4;
#if UNITY_EDITOR
                UnityEditor.EditorUtility.SetDirty(this);
#endif
            }
            if (settings.pipelineVersion < 5)
            {
                settings.diffuseSpatialSamples = Mathf.Min(settings.diffuseSpatialSamples, 4);
                settings.diffuseSpatialRadius = Mathf.Min(settings.diffuseSpatialRadius, 4f);
                settings.diffuseDebugStage = DiffuseDebugStage.Final;
                settings.pipelineVersion = 5;
#if UNITY_EDITOR
                UnityEditor.EditorUtility.SetDirty(this);
#endif
            }
            if (settings.pipelineVersion < 6)
            {
                settings.enableSpecular = true;
                settings.specularResolutionScale = 0.5f;
                settings.specularHistoryWeight = 0.9f;
                settings.specularSpatialSamples = 4;
                settings.pipelineVersion = 6;
#if UNITY_EDITOR
                UnityEditor.EditorUtility.SetDirty(this);
#endif
            }
            if (settings.pipelineVersion < 7)
            {
                // Version 6 accidentally excluded smoothness=1 materials because their
                // perceptual roughness is exactly zero.  Keep them in the software
                // specular path and clamp only inside the GGX evaluation.
                settings.minSpecularRoughness = 0f;
                settings.pipelineVersion = 7;
#if UNITY_EDITOR
                UnityEditor.EditorUtility.SetDirty(this);
#endif
            }
            if (settings.pipelineVersion < 8)
            {
                // 0.98 retains roughly fifty frames of final colour.  That is useful
                // only for a locked-off convergence capture; it is not a substitute for
                // ReSTIR reservoir reuse and makes any remaining disocclusion visibly
                // smear while the camera moves.  Keep the production default bounded
                // to the already documented 0.9, without changing deliberately lower
                // user values.
                settings.diffuseHistoryWeight = Mathf.Min(settings.diffuseHistoryWeight, 0.9f);
                settings.specularHistoryWeight = Mathf.Min(settings.specularHistoryWeight, 0.9f);
                settings.pipelineVersion = 8;
#if UNITY_EDITOR
                UnityEditor.EditorUtility.SetDirty(this);
#endif
            }
            if (settings.pipelineVersion < 9)
            {
                // The captured scene colour contains the prior frame's composited GI.
                // It is useful for experimentation, but cannot be fed back as a generic
                // secondary-light source without an explicit path throughput estimator.
                settings.enableSceneColorHistory = false;
                settings.pipelineVersion = 9;
#if UNITY_EDITOR
                UnityEditor.EditorUtility.SetDirty(this);
#endif
            }
            if (settings.screenLightingShader == null)
                settings.screenLightingShader = Resources.Load<ComputeShader>("RealtimeGI/GIScreenLighting");
            if (settings.compositeShader == null)
                settings.compositeShader = Shader.Find("Hidden/RealtimeGI/Composite");
            CoreUtils.Destroy(compositeMaterial);
            compositeMaterial = settings.compositeShader != null
                ? CoreUtils.CreateEngineMaterial(settings.compositeShader)
                : null;
            prepareInjectionPass = new GIInjectionStatePass(
                "RealtimeGI/Prepare Deferred Injection", 1f)
            {
                renderPassEvent = RenderPassEvent.BeforeRenderingGbuffer
            };
            resetInjectionPass = new GIInjectionStatePass(
                "RealtimeGI/Reset Deferred Injection", 0f)
            {
                renderPassEvent = RenderPassEvent.AfterRenderingDeferredLights
            };
            disableNativeReflectionsPass = new GIKeywordStatePass(
                "RealtimeGI/Disable Native Environment Reflections", true)
            {
                renderPassEvent = RenderPassEvent.BeforeRenderingGbuffer
            };
            restoreNativeReflectionsPass = new GIKeywordStatePass(
                "RealtimeGI/Restore Native Environment Reflections", false)
            {
                renderPassEvent = RenderPassEvent.AfterRendering
            };
            pass?.Dispose();
            pass = new ScreenLightingPass
            {
                renderPassEvent = RenderPassEvent.BeforeRenderingDeferredLights,
                requiresIntermediateTexture = true
            };
            sceneColorHistoryPass = new SceneColorHistoryPass(pass)
            {
                // Capture the fully lit opaque scene plus transparents.  The next frame's
                // screen-hit reflection then sees the same transparent relationship as the
                // visible image instead of an opaque-only surrogate.
                renderPassEvent = RenderPassEvent.AfterRenderingTransparents,
                requiresIntermediateTexture = true
            };
        }

        protected override void Dispose(bool disposing)
        {
            pass?.Dispose();
            pass = null;
            prepareInjectionPass = null;
            resetInjectionPass = null;
            disableNativeReflectionsPass = null;
            restoreNativeReflectionsPass = null;
            sceneColorHistoryPass = null;
            CoreUtils.Destroy(compositeMaterial);
            compositeMaterial = null;
            Shader.SetGlobalFloat(ShaderIds.DeferredInjectionActive, 0f);
            Shader.DisableKeyword("_ENVIRONMENTREFLECTIONS_OFF");
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if ((!settings.enableDiffuse && !settings.enableSpecular) || pass == null || prepareInjectionPass == null ||
                resetInjectionPass == null || disableNativeReflectionsPass == null ||
                restoreNativeReflectionsPass == null || sceneColorHistoryPass == null ||
                settings.screenLightingShader == null ||
                compositeMaterial == null ||
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
            renderer.EnqueuePass(disableNativeReflectionsPass);
            renderer.EnqueuePass(prepareInjectionPass);
            pass.Setup(settings, compositeMaterial, clipmapView, sceneView);
            renderer.EnqueuePass(pass);
            // Always restore the global state, including frames where the main pass discovers
            // an invalid/missing RenderGraph resource and records no compute work.
            renderer.EnqueuePass(resetInjectionPass);
            renderer.EnqueuePass(sceneColorHistoryPass);
            renderer.EnqueuePass(restoreNativeReflectionsPass);
        }

        sealed class GIKeywordStatePass : ScriptableRenderPass
        {
            const string EnvironmentReflectionsOff = "_ENVIRONMENTREFLECTIONS_OFF";

            sealed class PassData
            {
                public bool enabled;
            }

            readonly string graphPassName;
            readonly bool enabled;

            public GIKeywordStatePass(string passName, bool enabled)
            {
                graphPassName = passName;
                this.enabled = enabled;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                using var builder = renderGraph.AddUnsafePass<PassData>(graphPassName, out PassData data);
                data.enabled = enabled;
                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc(static (PassData passData, UnsafeGraphContext context) =>
                {
                    if (passData.enabled)
                        context.cmd.EnableShaderKeyword(EnvironmentReflectionsOff);
                    else
                        context.cmd.DisableShaderKeyword(EnvironmentReflectionsOff);
                });
            }
        }

        sealed class GIInjectionStatePass : ScriptableRenderPass
        {
            sealed class PassData
            {
                public float value;
            }

            readonly string graphPassName;
            readonly float value;

            public GIInjectionStatePass(string passName, float value)
            {
                graphPassName = passName;
                this.value = value;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                using var builder = renderGraph.AddUnsafePass<PassData>(graphPassName, out PassData data);
                data.value = value;
                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc(static (PassData passData, UnsafeGraphContext context) =>
                    context.cmd.SetGlobalFloat(ShaderIds.DeferredInjectionActive, passData.value));
            }
        }

        sealed class SceneColorHistoryPass : ScriptableRenderPass
        {
            const string PassName = "RealtimeGI/Capture Previous Scene Color";
            const string GenerateMipsPassName = "RealtimeGI/Generate Previous Scene Color Mips";
            readonly ScreenLightingPass owner;

            sealed class GenerateMipsPassData
            {
                public TextureHandle destination;
            }

            public SceneColorHistoryPass(ScreenLightingPass owner)
            {
                this.owner = owner;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                UniversalResourceData resources = frameData.Get<UniversalResourceData>();
                UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
                if (resources.isActiveTargetBackBuffer || !resources.activeColorTexture.IsValid() ||
                    !owner.TryGetHistory(cameraData.camera.GetInstanceID(), out CameraHistory history) ||
                    history.sceneColor == null)
                    return;

                TextureHandle destination = renderGraph.ImportTexture(history.sceneColor);
                using (IBaseRenderGraphBuilder blitBuilder = renderGraph.AddBlitPass(
                    resources.activeColorTexture, destination, Vector2.one, Vector2.zero,
                    passName: PassName, returnBuilder: true))
                {
                    // This is an imported cross-frame history.  Nothing reads it again in
                    // the current graph, so allowing culling can silently remove the copy.
                    blitBuilder.AllowPassCulling(false);
                }

                using (var builder = renderGraph.AddUnsafePass<GenerateMipsPassData>(
                           GenerateMipsPassName, out GenerateMipsPassData data))
                {
                    data.destination = destination;
                    builder.UseTexture(destination, AccessFlags.ReadWrite);
                    builder.AllowPassCulling(false);
                    builder.SetRenderFunc(static (GenerateMipsPassData passData,
                        UnsafeGraphContext context) =>
                    {
                        // Rough AKGI-style screen hits explicitly sample this mip chain.
                        // Do not rely on implicit RenderTexture auto-generation at an
                        // imported RenderGraph boundary.
                        context.cmd.GenerateMips((RenderTargetIdentifier)passData.destination);
                    });
                }
                history.sceneColorValid = true;
            }
        }

        sealed class CameraHistory : IDisposable
        {
            public readonly RTHandle[] diffuse = new RTHandle[2];
            public readonly RTHandle[] geometry = new RTHandle[2];
            public readonly RTHandle[] reservoirRadiance = new RTHandle[2];
            public readonly RTHandle[] reservoirRay = new RTHandle[2];
            public readonly RTHandle[] reservoirStats = new RTHandle[2];
            public readonly RTHandle[] reservoirHit = new RTHandle[2];
            public readonly RTHandle[] specular = new RTHandle[2];
            public RTHandle hzb;
            public RTHandle sceneColor;
            public bool sceneColorValid;
            public int index;
            public int width;
            public int height;
            public int diffuseWidth;
            public int diffuseHeight;
            public int specularWidth;
            public int specularHeight;
            public int hzbWidth;
            public int hzbHeight;
            public int hzbMipCount;
            public bool valid;
            public uint frameIndex;
            public int lightingRevision = -1;
            public Vector3 previousPosition;
            public Quaternion previousRotation;
            public Matrix4x4 previousViewProjection;

            public void Ensure(int fullWidth, int fullHeight, int nextDiffuseWidth, int nextDiffuseHeight,
                int nextSpecularWidth, int nextSpecularHeight,
                string cameraName)
            {
                if (width == fullWidth && height == fullHeight &&
                    diffuseWidth == nextDiffuseWidth && diffuseHeight == nextDiffuseHeight &&
                    specularWidth == nextSpecularWidth && specularHeight == nextSpecularHeight &&
                    diffuse[0] != null && reservoirHit[0] != null && specular[0] != null &&
                    hzb != null && sceneColor != null)
                    return;
                DisposeTextures();
                width = fullWidth;
                height = fullHeight;
                diffuseWidth = nextDiffuseWidth;
                diffuseHeight = nextDiffuseHeight;
                specularWidth = nextSpecularWidth;
                specularHeight = nextSpecularHeight;
                hzbWidth = Mathf.Max(1, fullWidth >> 1);
                hzbHeight = Mathf.Max(1, fullHeight >> 1);
                hzbMipCount = Mathf.FloorToInt(Mathf.Log(Mathf.Max(hzbWidth, hzbHeight), 2f)) + 1;
                for (int i = 0; i < 2; i++)
                {
                    diffuse[i] = Allocate(diffuseWidth, diffuseHeight, $"GI Diffuse History {cameraName} {i}");
                    geometry[i] = Allocate(diffuseWidth, diffuseHeight, $"GI Geometry History {cameraName} {i}");
                    reservoirRadiance[i] = Allocate(
                        diffuseWidth, diffuseHeight, $"GI Diffuse Reservoir Radiance {cameraName} {i}");
                    reservoirRay[i] = Allocate(
                        diffuseWidth, diffuseHeight, $"GI Diffuse Reservoir Ray {cameraName} {i}");
                    reservoirStats[i] = Allocate(
                        diffuseWidth, diffuseHeight, $"GI Diffuse Reservoir Stats {cameraName} {i}");
                    reservoirHit[i] = Allocate(
                        diffuseWidth, diffuseHeight, $"GI Diffuse Reservoir Hit Normal {cameraName} {i}");
                    specular[i] = RTHandles.Alloc(
                        fullWidth, fullHeight, 1, DepthBits.None,
                        GraphicsFormat.B10G11R11_UFloatPack32, FilterMode.Bilinear,
                        TextureWrapMode.Clamp, TextureDimension.Tex2D, true,
                        name: $"GI Specular History {cameraName} {i}");
                }
                hzb = RTHandles.Alloc(
                    hzbWidth, hzbHeight, 1, DepthBits.None, GraphicsFormat.R32_SFloat,
                    FilterMode.Point, TextureWrapMode.Clamp, TextureDimension.Tex2D,
                    true, true, false, name: $"GI Screen HZB {cameraName}");
                sceneColor = RTHandles.Alloc(
                    specularWidth, specularHeight, 1, DepthBits.None,
                    GraphicsFormat.B10G11R11_UFloatPack32, FilterMode.Trilinear,
                    TextureWrapMode.Clamp, TextureDimension.Tex2D,
                    enableRandomWrite: false, useMipMap: true, autoGenerateMips: false,
                    name: $"GI Previous Scene Color {cameraName}");
                index = 0;
                valid = false;
                sceneColorValid = false;
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

            public void Advance(Camera camera, Matrix4x4 viewProjection, int nextLightingRevision)
            {
                previousPosition = camera.transform.position;
                previousRotation = camera.transform.rotation;
                previousViewProjection = viewProjection;
                lightingRevision = nextLightingRevision;
                valid = true;
                frameIndex++;
                index ^= 1;
            }

            void DisposeTextures()
            {
                for (int i = 0; i < 2; i++)
                {
                    diffuse[i]?.Release();
                    geometry[i]?.Release();
                    reservoirRadiance[i]?.Release();
                    reservoirRay[i]?.Release();
                    reservoirStats[i]?.Release();
                    reservoirHit[i]?.Release();
                    specular[i]?.Release();
                    diffuse[i] = null;
                    geometry[i] = null;
                    reservoirRadiance[i] = null;
                    reservoirRay[i] = null;
                    reservoirStats[i] = null;
                    reservoirHit[i] = null;
                    specular[i] = null;
                }
                hzb?.Release();
                sceneColor?.Release();
                hzb = null;
                sceneColor = null;
                valid = false;
                sceneColorValid = false;
                lightingRevision = -1;
            }

            public void Dispose() => DisposeTextures();
        }

        sealed class ScreenLightingPass : ScriptableRenderPass, IDisposable
        {
            const string PassName = "RealtimeGI/Screen Lighting";
            readonly Dictionary<int, CameraHistory> histories = new Dictionary<int, CameraHistory>();
            static readonly uint[] ZeroTraceCounters = new uint[12];
            Settings settings;
            Material compositeMaterial;
            GIClipmapGpuView clipmapView;
            GISceneGpuView sceneView;
            GraphicsBuffer traceCounterBuffer;
            GraphicsBuffer diffuseMissQueue;
            GraphicsBuffer diffuseMissDispatchArgs;
            int diffuseMissCapacity;
            bool traceReadbackPending;
            int lastTraceLogFrame = -10000;

            int buildHzbMip0Kernel = -1;
            int buildHzbMipKernel = -1;
            int traceDiffuseScreenKernel = -1;
            int buildDiffuseMissArgsKernel = -1;
            int traceDiffuseWorldMissesKernel = -1;
            int temporalDiffuseKernel = -1;
            int spatialDiffuseKernel = -1;
            int denoiseDiffuseKernel = -1;
            int filterDiffuseKernel = -1;
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

            public bool TryGetHistory(int cameraId, out CameraHistory history) =>
                histories.TryGetValue(cameraId, out history);

            public void Setup(Settings nextSettings, Material nextCompositeMaterial,
                GIClipmapGpuView nextClipmapView, GISceneGpuView nextSceneView)
            {
                settings = nextSettings;
                compositeMaterial = nextCompositeMaterial;
                clipmapView = nextClipmapView;
                sceneView = nextSceneView;
                EnsureTraceCounterBuffer();
                RequestTraceReadbackIfNeeded();
                // Compute-shader hot reload invalidates cached kernel indices in the Editor.
                // Refresh there so a successful reimport can recover without restarting Play.
                if (traceDiffuseScreenKernel < 0
#if UNITY_EDITOR
                    || true
#endif
                    )
                {
                    ComputeShader shader = settings.screenLightingShader;
                    buildHzbMip0Kernel = shader.FindKernel("BuildHzbMip0");
                    buildHzbMipKernel = shader.FindKernel("BuildHzbMip");
                    traceDiffuseScreenKernel = shader.FindKernel("TraceDiffuseScreen");
                    buildDiffuseMissArgsKernel = shader.FindKernel("BuildDiffuseMissArgs");
                    traceDiffuseWorldMissesKernel = shader.FindKernel("TraceDiffuseWorldMisses");
                    temporalDiffuseKernel = shader.FindKernel("TemporalDiffuse");
                    spatialDiffuseKernel = shader.FindKernel("SpatialDiffuse");
                    denoiseDiffuseKernel = shader.FindKernel("DenoiseDiffuse");
                    filterDiffuseKernel = shader.FindKernel("FilterDiffuse");
                    traceSpecularKernel = shader.FindKernel("TraceSpecular");
                    resolveSpecularKernel = shader.FindKernel("ResolveSpecular");
                    temporalSpecularKernel = shader.FindKernel("TemporalSpecular");
                    spatialSpecularKernel = shader.FindKernel("SpatialSpecular");
                    updateGeometryKernel = shader.FindKernel("UpdateGeometryHistory");
                    upsampleKernel = shader.FindKernel("UpsampleDiffuseIrradiance");
                }
            }

            void EnsureTraceCounterBuffer()
            {
                if (traceCounterBuffer != null && traceCounterBuffer.count == ZeroTraceCounters.Length)
                    return;
                traceCounterBuffer?.Release();
                traceCounterBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 12, 4)
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
                    var values = new uint[12];
                    int count = Mathf.Min(values.Length, data.Length);
                    for (int i = 0; i < count; i++) values[i] = data[i];
                    LatestTraceCounters = new TraceCounterSnapshot(values);
#if UNITY_EDITOR
                    if (Time.frameCount - lastTraceLogFrame >= 60)
                    {
                        lastTraceLogFrame = Time.frameCount;
                        Debug.Log($"[RealtimeGI][Trace] rays={LatestTraceCounters.rays}, " +
                                  $"screenHit={LatestTraceCounters.screenHits}, " +
                                  $"screenGeometryHit={LatestTraceCounters.screenGeometryHits}, " +
                                  $"worldHit={LatestTraceCounters.worldHits}, " +
                                  $"worldMiss={LatestTraceCounters.worldMisses}, " +
                                  $"avgWorldSteps={LatestTraceCounters.AverageWorldSteps:F1}, " +
                                  $"validCache={LatestTraceCounters.validCacheHits}, " +
                                  $"invalidCache={LatestTraceCounters.invalidCacheHits}, " +
                                  $"temporalMatch={LatestTraceCounters.temporalMatches}, " +
                                  $"temporalReuse={LatestTraceCounters.temporalReuseCandidates}, " +
                                  $"spatialReuse={LatestTraceCounters.spatialReuseCandidates}, " +
                                  $"visibilityReject={LatestTraceCounters.spatialVisibilityRejected}");
                    }
#endif
                });
            }

            sealed class PassData
            {
                public ComputeShader shader;
                public int buildHzbMip0Kernel;
                public int buildHzbMipKernel;
                public int traceDiffuseScreenKernel;
                public int buildDiffuseMissArgsKernel;
                public int traceDiffuseWorldMissesKernel;
                public int temporalDiffuseKernel;
                public int spatialDiffuseKernel;
                public int denoiseDiffuseKernel;
                public int filterDiffuseKernel;
                public int traceSpecularKernel;
                public int resolveSpecularKernel;
                public int temporalSpecularKernel;
                public int spatialSpecularKernel;
                public int updateGeometryKernel;
                public int upsampleKernel;
                public TextureHandle depth;
                public TextureHandle normals;
                public TextureHandle motion;
                public TextureHandle gBuffer0;
                public TextureHandle gBuffer1;
                public TextureHandle gBuffer2;
                public TextureHandle currentDiffuse;
                public TextureHandle currentDiffuseRay;
                public TextureHandle currentDiffuseStats;
                public TextureHandle currentDiffuseHit;
                public TextureHandle diffuseRead;
                public TextureHandle diffuseWrite;
                public TextureHandle diffuseDenoised;
                public TextureHandle diffuseAtrous;
                public TextureHandle currentSpecular;
                public TextureHandle currentSpecularRay;
                public TextureHandle resolvedSpecular;
                public TextureHandle resolvedSpecularRay;
                public TextureHandle specularRead;
                public TextureHandle specularWrite;
                public TextureHandle specularSpatial;
                public TextureHandle reservoirRadianceRead;
                public TextureHandle reservoirRadianceWrite;
                public TextureHandle reservoirRayRead;
                public TextureHandle reservoirRayWrite;
                public TextureHandle reservoirStatsRead;
                public TextureHandle reservoirStatsWrite;
                public TextureHandle reservoirHitRead;
                public TextureHandle reservoirHitWrite;
                public TextureHandle geometryRead;
                public TextureHandle geometryWrite;
                public TextureHandle hzb;
                public TextureHandle sceneColorHistory;
                public TextureHandle fullLighting;
                public GraphicsBuffer levelData;
                public GraphicsBuffer staticPageTable;
                public GraphicsBuffer staticOccupancy;
                public GraphicsBuffer staticSurface;
                public GraphicsBuffer staticSurfaceIdentity;
                public GraphicsBuffer staticDistance;
                public GraphicsBuffer staticRadiance;
                public GraphicsBuffer staticValidity;
                public GraphicsBuffer dynamicPageTable;
                public GraphicsBuffer dynamicOccupancy;
                public GraphicsBuffer dynamicSurface;
                public GraphicsBuffer dynamicSurfaceIdentity;
                public GraphicsBuffer dynamicDistance;
                public GraphicsBuffer dynamicRadiance;
                public GraphicsBuffer dynamicValidity;
                public GraphicsBuffer materials;
                public GraphicsBuffer traceCounters;
                public GraphicsBuffer diffuseMissQueue;
                public GraphicsBuffer diffuseMissDispatchArgs;
                public Matrix4x4 inverseViewProjection;
                public Matrix4x4 viewProjection;
                public Matrix4x4 previousViewProjection;
                public Matrix4x4 previousInverseViewProjection;
                public Vector3 cameraPosition;
                public Vector3 mainLightDirection;
                public Vector3 mainLightColor;
                public int fullWidth;
                public int fullHeight;
                public int diffuseWidth;
                public int diffuseHeight;
                public int specularWidth;
                public int specularHeight;
                public int hzbWidth;
                public int hzbHeight;
                public int hzbMipCount;
                public int diffuseRays;
                public int materialCount;
                public int historyValid;
                public int sceneColorHistoryValid;
                public bool enableSceneColorHistory;
                public int frameIndex;
                public float maxTraceDistance;
                public float screenTraceDistance;
                public float diffuseHistoryWeight;
                public float diffuseIntensity;
                public int diffuseSpatialSamples;
                public float diffuseSpatialRadius;
                public float specularHistoryWeight;
                public float specularIntensity;
                public float minSpecularRoughness;
                public float maxSpecularRoughness;
                public int specularSpatialSamples;
                public bool enableDiffuse;
                public bool enableSpecular;
                public bool debugHitMaterialColor;
                public int diffuseDebugStage;
                public bool enableTraceCounters;
            }

            void EnsureDiffuseMissBuffers(int capacity)
            {
                int required = Mathf.NextPowerOfTwo(Mathf.Max(1, capacity));
                if (diffuseMissQueue != null && diffuseMissCapacity >= required)
                    return;
                diffuseMissQueue?.Release();
                diffuseMissDispatchArgs?.Release();
                diffuseMissCapacity = required;
                diffuseMissQueue = new GraphicsBuffer(
                    GraphicsBuffer.Target.Append, diffuseMissCapacity, 4)
                {
                    name = "GI Diffuse Screen Miss Queue"
                };
                diffuseMissDispatchArgs = new GraphicsBuffer(
                    GraphicsBuffer.Target.IndirectArguments | GraphicsBuffer.Target.Structured, 4, 4)
                {
                    name = "GI Diffuse Miss Dispatch Args"
                };
                diffuseMissDispatchArgs.SetData(new uint[] { 0u, 1u, 1u, 0u });
            }

            sealed class DeferredInjectionPassData
            {
                public Material material;
                public TextureHandle irradiance;
                public TextureHandle specular;
                public TextureHandle gBuffer0;
                public TextureHandle gBuffer1;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                if (settings == null || settings.screenLightingShader == null || compositeMaterial == null)
                    return;
                UniversalResourceData resources = frameData.Get<UniversalResourceData>();
                UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
                if (resources.isActiveTargetBackBuffer || !resources.cameraDepthTexture.IsValid() ||
                    !resources.activeDepthTexture.IsValid() ||
                    !resources.motionVectorColor.IsValid() || resources.gBuffer == null ||
                    resources.gBuffer.Length < 3 || !resources.gBuffer[0].IsValid() ||
                    !resources.gBuffer[1].IsValid() || !resources.gBuffer[2].IsValid())
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
                EnsureDiffuseMissBuffers(diffuseWidth * diffuseHeight);
                // Slow TOD changes continuously refresh the cache. They must not reset the
                // screen history every frame: with one diffuse ray that turns temporally
                // stable irradiance into visible per-voxel noise.
                bool historyValid = history.valid && !history.IsCameraCut(camera);
                int readIndex = history.index;
                int writeIndex = readIndex ^ 1;

                TextureDesc lowDesc = new TextureDesc(diffuseWidth, diffuseHeight)
                {
                    name = "GI Current Diffuse Incident Radiance",
                    colorFormat = GraphicsFormat.R16G16B16A16_SFloat,
                    enableRandomWrite = true,
                    clearBuffer = false,
                    filterMode = FilterMode.Bilinear
                };
                TextureHandle currentDiffuse = renderGraph.CreateTexture(lowDesc);
                lowDesc.name = "GI Diffuse Denoised";
                TextureHandle diffuseDenoised = renderGraph.CreateTexture(lowDesc);
                lowDesc.name = "GI Diffuse A-Trous Scratch";
                TextureHandle diffuseAtrous = renderGraph.CreateTexture(lowDesc);
                lowDesc.name = "GI Current Diffuse Reservoir Ray";
                TextureHandle currentDiffuseRay = renderGraph.CreateTexture(lowDesc);
                lowDesc.name = "GI Current Diffuse Reservoir Stats";
                TextureHandle currentDiffuseStats = renderGraph.CreateTexture(lowDesc);
                lowDesc.name = "GI Current Diffuse Reservoir Hit Normal";
                TextureHandle currentDiffuseHit = renderGraph.CreateTexture(lowDesc);
                TextureDesc specularLowDesc = new TextureDesc(specularWidth, specularHeight)
                {
                    name = "GI Current Specular Radiance",
                    colorFormat = GraphicsFormat.R16G16B16A16_SFloat,
                    enableRandomWrite = true,
                    clearBuffer = false,
                    filterMode = FilterMode.Bilinear
                };
                TextureHandle currentSpecular = renderGraph.CreateTexture(specularLowDesc);
                specularLowDesc.name = "GI Current Specular Ray";
                TextureHandle currentSpecularRay = renderGraph.CreateTexture(specularLowDesc);
                TextureDesc fullDesc = new TextureDesc(fullWidth, fullHeight)
                {
                    name = "GI Full Resolution Irradiance",
                    colorFormat = GraphicsFormat.R16G16B16A16_SFloat,
                    enableRandomWrite = true,
                    clearBuffer = false,
                    filterMode = FilterMode.Bilinear
                };
                TextureHandle fullLighting = renderGraph.CreateTexture(fullDesc);
                fullDesc.name = "GI Resolved Specular";
                fullDesc.colorFormat = GraphicsFormat.B10G11R11_UFloatPack32;
                TextureHandle resolvedSpecular = renderGraph.CreateTexture(fullDesc);
                fullDesc.name = "GI Resolved Specular Ray";
                fullDesc.colorFormat = GraphicsFormat.R16G16B16A16_SFloat;
                TextureHandle resolvedSpecularRay = renderGraph.CreateTexture(fullDesc);
                fullDesc.name = "GI Spatial Specular";
                fullDesc.colorFormat = GraphicsFormat.B10G11R11_UFloatPack32;
                TextureHandle specularSpatial = renderGraph.CreateTexture(fullDesc);
                TextureHandle diffuseRead = renderGraph.ImportTexture(history.diffuse[readIndex]);
                TextureHandle diffuseWrite = renderGraph.ImportTexture(history.diffuse[writeIndex]);
                TextureHandle reservoirRadianceRead = renderGraph.ImportTexture(
                    history.reservoirRadiance[readIndex]);
                TextureHandle reservoirRadianceWrite = renderGraph.ImportTexture(
                    history.reservoirRadiance[writeIndex]);
                TextureHandle reservoirRayRead = renderGraph.ImportTexture(history.reservoirRay[readIndex]);
                TextureHandle reservoirRayWrite = renderGraph.ImportTexture(history.reservoirRay[writeIndex]);
                TextureHandle reservoirStatsRead = renderGraph.ImportTexture(history.reservoirStats[readIndex]);
                TextureHandle reservoirStatsWrite = renderGraph.ImportTexture(history.reservoirStats[writeIndex]);
                TextureHandle reservoirHitRead = renderGraph.ImportTexture(history.reservoirHit[readIndex]);
                TextureHandle reservoirHitWrite = renderGraph.ImportTexture(history.reservoirHit[writeIndex]);
                TextureHandle geometryRead = renderGraph.ImportTexture(history.geometry[readIndex]);
                TextureHandle geometryWrite = renderGraph.ImportTexture(history.geometry[writeIndex]);
                TextureHandle specularRead = renderGraph.ImportTexture(history.specular[readIndex]);
                TextureHandle specularWrite = renderGraph.ImportTexture(history.specular[writeIndex]);
                TextureHandle hzb = renderGraph.ImportTexture(history.hzb);
                TextureHandle sceneColorHistory = renderGraph.ImportTexture(history.sceneColor);

                // The deferred renderer always shades an intermediate render texture here.
                // Use the render-texture GPU convention so depth reconstruction matches it.
                Matrix4x4 projection = GL.GetGPUProjectionMatrix(cameraData.GetProjectionMatrix(), true);
                Matrix4x4 viewProjection = projection * cameraData.GetViewMatrix();
                Light sun = RenderSettings.sun;
                Vector3 lightDirection = sun != null ? sun.transform.forward : new Vector3(0.3f, -0.8f, 0.2f).normalized;
                Color lightColor = sun != null ? sun.color * sun.intensity : Color.white;

                using (var builder = renderGraph.AddUnsafePass<PassData>(PassName, out PassData data))
                {
                    data.shader = settings.screenLightingShader;
                    data.buildHzbMip0Kernel = buildHzbMip0Kernel;
                    data.buildHzbMipKernel = buildHzbMipKernel;
                    data.traceDiffuseScreenKernel = traceDiffuseScreenKernel;
                    data.buildDiffuseMissArgsKernel = buildDiffuseMissArgsKernel;
                    data.traceDiffuseWorldMissesKernel = traceDiffuseWorldMissesKernel;
                    data.temporalDiffuseKernel = temporalDiffuseKernel;
                    data.spatialDiffuseKernel = spatialDiffuseKernel;
                    data.denoiseDiffuseKernel = denoiseDiffuseKernel;
                    data.filterDiffuseKernel = filterDiffuseKernel;
                    data.traceSpecularKernel = traceSpecularKernel;
                    data.resolveSpecularKernel = resolveSpecularKernel;
                    data.temporalSpecularKernel = temporalSpecularKernel;
                    data.spatialSpecularKernel = spatialSpecularKernel;
                    data.updateGeometryKernel = updateGeometryKernel;
                    data.upsampleKernel = upsampleKernel;
                    // Nanite records CopyDepthAfterResolve immediately after FormalVisibility.
                    // This R32 copy contains the final Nanite depth and is safe for compute sampling.
                    data.depth = resources.cameraDepthTexture;
                    data.normals = resources.gBuffer[2];
                    data.motion = resources.motionVectorColor;
                    data.gBuffer0 = resources.gBuffer[0];
                    data.gBuffer1 = resources.gBuffer[1];
                    data.gBuffer2 = resources.gBuffer[2];
                    data.currentDiffuse = currentDiffuse;
                    data.currentDiffuseRay = currentDiffuseRay;
                    data.currentDiffuseStats = currentDiffuseStats;
                    data.currentDiffuseHit = currentDiffuseHit;
                    data.diffuseRead = diffuseRead;
                    data.diffuseWrite = diffuseWrite;
                    data.diffuseDenoised = diffuseDenoised;
                    data.diffuseAtrous = diffuseAtrous;
                    data.currentSpecular = currentSpecular;
                    data.currentSpecularRay = currentSpecularRay;
                    data.resolvedSpecular = resolvedSpecular;
                    data.resolvedSpecularRay = resolvedSpecularRay;
                    data.specularRead = specularRead;
                    data.specularWrite = specularWrite;
                    data.specularSpatial = specularSpatial;
                    data.reservoirRadianceRead = reservoirRadianceRead;
                    data.reservoirRadianceWrite = reservoirRadianceWrite;
                    data.reservoirRayRead = reservoirRayRead;
                    data.reservoirRayWrite = reservoirRayWrite;
                    data.reservoirStatsRead = reservoirStatsRead;
                    data.reservoirStatsWrite = reservoirStatsWrite;
                    data.reservoirHitRead = reservoirHitRead;
                    data.reservoirHitWrite = reservoirHitWrite;
                    data.geometryRead = geometryRead;
                    data.geometryWrite = geometryWrite;
                    data.hzb = hzb;
                    data.sceneColorHistory = sceneColorHistory;
                    data.fullLighting = fullLighting;
                    data.levelData = clipmapView.levelData;
                    data.staticPageTable = clipmapView.staticPageTable;
                    data.staticOccupancy = clipmapView.staticOccupancy;
                    data.staticSurface = clipmapView.staticSurface;
                    data.staticSurfaceIdentity = clipmapView.staticSurfaceIdentity;
                    data.staticDistance = clipmapView.staticDistance;
                    data.staticRadiance = clipmapView.staticRadiance;
                    data.staticValidity = clipmapView.staticValidity;
                    data.dynamicPageTable = clipmapView.dynamicPageTable;
                    data.dynamicOccupancy = clipmapView.dynamicOccupancy;
                    data.dynamicSurface = clipmapView.dynamicSurface;
                    data.dynamicSurfaceIdentity = clipmapView.dynamicSurfaceIdentity;
                    data.dynamicDistance = clipmapView.dynamicDistance;
                    data.dynamicRadiance = clipmapView.dynamicRadiance;
                    data.dynamicValidity = clipmapView.dynamicValidity;
                    data.materials = sceneView.materials;
                    data.traceCounters = traceCounterBuffer;
                    data.diffuseMissQueue = diffuseMissQueue;
                    data.diffuseMissDispatchArgs = diffuseMissDispatchArgs;
                    data.inverseViewProjection = viewProjection.inverse;
                    data.viewProjection = viewProjection;
                    data.previousViewProjection = historyValid
                        ? history.previousViewProjection : viewProjection;
                    data.previousInverseViewProjection = data.previousViewProjection.inverse;
                    data.cameraPosition = camera.transform.position;
                    data.mainLightDirection = lightDirection;
                    data.mainLightColor = new Vector3(lightColor.r, lightColor.g, lightColor.b);
                    data.fullWidth = fullWidth;
                    data.fullHeight = fullHeight;
                    data.diffuseWidth = diffuseWidth;
                    data.diffuseHeight = diffuseHeight;
                    data.specularWidth = specularWidth;
                    data.specularHeight = specularHeight;
                    data.hzbWidth = history.hzbWidth;
                    data.hzbHeight = history.hzbHeight;
                    data.hzbMipCount = history.hzbMipCount;
                    data.diffuseRays = settings.enableDiffuse ? settings.diffuseRaysPerProbe : 0;
                    data.materialCount = sceneView.materialCount;
                    data.historyValid = historyValid ? 1 : 0;
                    data.sceneColorHistoryValid = history.sceneColorValid ? 1 : 0;
                    data.enableSceneColorHistory = settings.enableSceneColorHistory;
                    data.frameIndex = (int)history.frameIndex;
                    data.maxTraceDistance = settings.maxTraceDistance;
                    data.screenTraceDistance = Mathf.Min(
                        settings.maxTraceDistance, Mathf.Max(0f, settings.screenTraceDistance));
                    data.diffuseHistoryWeight = settings.diffuseHistoryWeight;
                    data.diffuseIntensity = settings.enableDiffuse ? settings.diffuseIntensity : 0f;
                    data.diffuseSpatialSamples = settings.diffuseSpatialSamples;
                    data.diffuseSpatialRadius = settings.diffuseSpatialRadius;
                    data.specularHistoryWeight = settings.specularHistoryWeight;
                    data.specularIntensity = settings.enableSpecular ? settings.specularIntensity : 0f;
                    data.minSpecularRoughness = settings.minSpecularRoughness;
                    data.maxSpecularRoughness = settings.maxSpecularRoughness;
                    data.specularSpatialSamples = settings.specularSpatialSamples;
                    data.enableDiffuse = settings.enableDiffuse;
                    data.enableSpecular = settings.enableSpecular;
                    data.debugHitMaterialColor = settings.debugHitMaterialColor;
                    data.diffuseDebugStage = (int)settings.diffuseDebugStage;
                    data.enableTraceCounters = settings.enableTraceCounters;

                    builder.UseTexture(data.depth, AccessFlags.Read);
                    builder.UseTexture(data.normals, AccessFlags.Read);
                    builder.UseTexture(data.motion, AccessFlags.Read);
                    builder.UseTexture(data.gBuffer0, AccessFlags.Read);
                    builder.UseTexture(data.gBuffer1, AccessFlags.Read);
                    builder.UseTexture(data.gBuffer2, AccessFlags.Read);
                    builder.UseTexture(data.currentDiffuse, AccessFlags.ReadWrite);
                    builder.UseTexture(data.currentDiffuseRay, AccessFlags.ReadWrite);
                    builder.UseTexture(data.currentDiffuseStats, AccessFlags.ReadWrite);
                    builder.UseTexture(data.currentDiffuseHit, AccessFlags.ReadWrite);
                    builder.UseTexture(data.diffuseRead, AccessFlags.Read);
                    builder.UseTexture(data.diffuseWrite, AccessFlags.ReadWrite);
                    builder.UseTexture(data.diffuseDenoised, AccessFlags.ReadWrite);
                    builder.UseTexture(data.diffuseAtrous, AccessFlags.ReadWrite);
                    builder.UseTexture(data.currentSpecular, AccessFlags.ReadWrite);
                    builder.UseTexture(data.currentSpecularRay, AccessFlags.ReadWrite);
                    builder.UseTexture(data.resolvedSpecular, AccessFlags.ReadWrite);
                    builder.UseTexture(data.resolvedSpecularRay, AccessFlags.ReadWrite);
                    builder.UseTexture(data.specularRead, AccessFlags.Read);
                    builder.UseTexture(data.specularWrite, AccessFlags.ReadWrite);
                    builder.UseTexture(data.specularSpatial, AccessFlags.ReadWrite);
                    builder.UseTexture(data.reservoirRadianceRead, AccessFlags.Read);
                    builder.UseTexture(data.reservoirRadianceWrite, AccessFlags.ReadWrite);
                    builder.UseTexture(data.reservoirRayRead, AccessFlags.Read);
                    builder.UseTexture(data.reservoirRayWrite, AccessFlags.ReadWrite);
                    builder.UseTexture(data.reservoirStatsRead, AccessFlags.Read);
                    builder.UseTexture(data.reservoirStatsWrite, AccessFlags.ReadWrite);
                    builder.UseTexture(data.reservoirHitRead, AccessFlags.Read);
                    builder.UseTexture(data.reservoirHitWrite, AccessFlags.ReadWrite);
                    builder.UseTexture(data.geometryRead, AccessFlags.Read);
                    builder.UseTexture(data.geometryWrite, AccessFlags.Write);
                    builder.UseTexture(data.hzb, AccessFlags.ReadWrite);
                    builder.UseTexture(data.sceneColorHistory, AccessFlags.Read);
                    builder.UseTexture(data.fullLighting, AccessFlags.Write);
                    builder.UseBuffer(renderGraph.ImportBuffer(data.levelData), AccessFlags.Read);
                    builder.UseBuffer(renderGraph.ImportBuffer(data.staticPageTable), AccessFlags.Read);
                    builder.UseBuffer(renderGraph.ImportBuffer(data.staticOccupancy), AccessFlags.Read);
                    builder.UseBuffer(renderGraph.ImportBuffer(data.staticSurface), AccessFlags.Read);
                    builder.UseBuffer(renderGraph.ImportBuffer(data.staticSurfaceIdentity), AccessFlags.Read);
                    builder.UseBuffer(renderGraph.ImportBuffer(data.staticDistance), AccessFlags.Read);
                    builder.UseBuffer(renderGraph.ImportBuffer(data.staticRadiance), AccessFlags.Read);
                    builder.UseBuffer(renderGraph.ImportBuffer(data.staticValidity), AccessFlags.Read);
                    builder.UseBuffer(renderGraph.ImportBuffer(data.dynamicPageTable), AccessFlags.Read);
                    builder.UseBuffer(renderGraph.ImportBuffer(data.dynamicOccupancy), AccessFlags.Read);
                    builder.UseBuffer(renderGraph.ImportBuffer(data.dynamicSurface), AccessFlags.Read);
                    builder.UseBuffer(renderGraph.ImportBuffer(data.dynamicSurfaceIdentity), AccessFlags.Read);
                    builder.UseBuffer(renderGraph.ImportBuffer(data.dynamicDistance), AccessFlags.Read);
                    builder.UseBuffer(renderGraph.ImportBuffer(data.dynamicRadiance), AccessFlags.Read);
                    builder.UseBuffer(renderGraph.ImportBuffer(data.dynamicValidity), AccessFlags.Read);
                    builder.UseBuffer(renderGraph.ImportBuffer(data.materials), AccessFlags.Read);
                    builder.UseBuffer(renderGraph.ImportBuffer(data.traceCounters), AccessFlags.ReadWrite);
                    builder.UseBuffer(renderGraph.ImportBuffer(data.diffuseMissQueue), AccessFlags.ReadWrite);
                    builder.UseBuffer(renderGraph.ImportBuffer(data.diffuseMissDispatchArgs), AccessFlags.ReadWrite);
                    builder.AllowPassCulling(false);
                    builder.AllowGlobalStateModification(true);
                    builder.SetRenderFunc(static (PassData passData, UnsafeGraphContext context) => Execute(passData, context));
                }

                using (var builder = renderGraph.AddRasterRenderPass<DeferredInjectionPassData>(
                           "RealtimeGI/Inject Deferred Diffuse", out var injectionData))
                {
                    injectionData.material = compositeMaterial;
                    injectionData.irradiance = fullLighting;
                    injectionData.specular = specularSpatial;
                    injectionData.gBuffer0 = resources.gBuffer[0];
                    injectionData.gBuffer1 = resources.gBuffer[1];
                    builder.UseTexture(injectionData.irradiance, AccessFlags.Read);
                    builder.UseTexture(injectionData.specular, AccessFlags.Read);
                    builder.UseTexture(injectionData.gBuffer0, AccessFlags.Read);
                    builder.UseTexture(injectionData.gBuffer1, AccessFlags.Read);
                    builder.SetRenderAttachment(resources.activeColorTexture, 0, AccessFlags.ReadWrite);
                    builder.SetRenderAttachmentDepth(resources.activeDepthTexture, AccessFlags.Read);
                    builder.AllowPassCulling(false);
                    builder.AllowGlobalStateModification(true);
                    builder.SetRenderFunc(static (DeferredInjectionPassData data, RasterGraphContext context) =>
                    {
                        context.cmd.SetGlobalTexture(ShaderIds.LightingTexture, data.irradiance);
                        context.cmd.SetGlobalTexture(ShaderIds.SpecularLightingTexture, data.specular);
                        context.cmd.SetGlobalTexture(ShaderIds.GBuffer0, data.gBuffer0);
                        context.cmd.SetGlobalTexture(ShaderIds.GBuffer1, data.gBuffer1);
                        context.cmd.DrawProcedural(Matrix4x4.identity, data.material, 0,
                            MeshTopology.Triangles, 3, 1);
                    });
                }
                history.Advance(camera, viewProjection, clipmapView.lightingRevision);
            }

            static void Execute(PassData data, UnsafeGraphContext context)
            {
                var cmd = context.cmd;
                ComputeShader shader = data.shader;
                cmd.SetComputeMatrixParam(shader, ShaderIds.InverseViewProjection, data.inverseViewProjection);
                cmd.SetComputeMatrixParam(shader, ShaderIds.ViewProjection, data.viewProjection);
                cmd.SetComputeMatrixParam(shader, ShaderIds.PreviousViewProjection, data.previousViewProjection);
                cmd.SetComputeMatrixParam(shader, ShaderIds.PreviousInverseViewProjection,
                    data.previousInverseViewProjection);
                cmd.SetComputeVectorParam(shader, ShaderIds.ViewportSize, new Vector4(
                    data.fullWidth, data.fullHeight,
                    1f / Mathf.Max(1, data.fullWidth), 1f / Mathf.Max(1, data.fullHeight)));
                cmd.SetComputeVectorParam(shader, ShaderIds.SpecularBufferSize, new Vector4(
                    data.specularWidth, data.specularHeight,
                    1f / Mathf.Max(1, data.specularWidth), 1f / Mathf.Max(1, data.specularHeight)));
                cmd.SetComputeVectorParam(shader, ShaderIds.CameraPosition, data.cameraPosition);
                cmd.SetComputeVectorParam(shader, ShaderIds.MainLightDirection, data.mainLightDirection);
                cmd.SetComputeVectorParam(shader, ShaderIds.MainLightColor, data.mainLightColor);
                cmd.SetComputeFloatParam(shader, ShaderIds.MaxTraceDistance, data.maxTraceDistance);
                cmd.SetComputeFloatParam(shader, ShaderIds.ScreenTraceDistance, data.screenTraceDistance);
                cmd.SetComputeFloatParam(shader, ShaderIds.DiffuseHistoryWeight, data.diffuseHistoryWeight);
                cmd.SetComputeFloatParam(shader, ShaderIds.DiffuseIntensity, data.diffuseIntensity);
                cmd.SetComputeIntParam(shader, ShaderIds.DiffuseSpatialSamples, data.diffuseSpatialSamples);
                cmd.SetComputeFloatParam(shader, ShaderIds.DiffuseSpatialRadius, data.diffuseSpatialRadius);
                cmd.SetComputeFloatParam(shader, ShaderIds.SpecularHistoryWeight, data.specularHistoryWeight);
                cmd.SetComputeFloatParam(shader, ShaderIds.SpecularIntensity, data.specularIntensity);
                cmd.SetComputeFloatParam(shader, ShaderIds.MinSpecularRoughness, data.minSpecularRoughness);
                cmd.SetComputeFloatParam(shader, ShaderIds.MaxSpecularRoughness, data.maxSpecularRoughness);
                cmd.SetComputeIntParam(shader, ShaderIds.SpecularSpatialSamples, data.specularSpatialSamples);
                cmd.SetComputeIntParam(shader, ShaderIds.FrameIndex, data.frameIndex);
                cmd.SetComputeIntParam(shader, ShaderIds.DiffuseRayCount, data.enableDiffuse ? data.diffuseRays : 1);
                cmd.SetComputeIntParam(shader, ShaderIds.MaterialCount, data.materialCount);
                cmd.SetComputeIntParam(shader, ShaderIds.HistoryValid, data.historyValid);
                cmd.SetComputeIntParam(shader, ShaderIds.SceneColorHistoryValid,
                    data.sceneColorHistoryValid);
                cmd.SetComputeIntParam(shader, ShaderIds.EnableSceneColorHistory,
                    data.enableSceneColorHistory ? 1 : 0);
                cmd.SetComputeIntParam(shader, ShaderIds.EnableDiffuse, data.enableDiffuse ? 1 : 0);
                cmd.SetComputeIntParam(shader, ShaderIds.EnableSpecular, data.enableSpecular ? 1 : 0);
                cmd.SetComputeIntParam(shader, ShaderIds.DebugHitMaterialColor, data.debugHitMaterialColor ? 1 : 0);
                cmd.SetComputeIntParam(shader, ShaderIds.DiffuseDebugStage, data.diffuseDebugStage);
                cmd.SetComputeIntParam(shader, ShaderIds.EnableTraceCounters, data.enableTraceCounters ? 1 : 0);
                cmd.SetComputeIntParam(shader, ShaderIds.HzbMipCount, data.hzbMipCount);
                cmd.SetComputeIntParam(shader, ShaderIds.UseHzb, 1);
                if (data.enableTraceCounters)
                    cmd.SetBufferData(data.traceCounters, ZeroTraceCounters);

                cmd.BeginSample("RealtimeGI/Build Screen HZB");
                cmd.SetComputeVectorParam(shader, ShaderIds.HzbSourceSize,
                    new Vector4(data.fullWidth, data.fullHeight, 0f, 0f));
                cmd.SetComputeTextureParam(shader, data.buildHzbMip0Kernel,
                    ShaderIds.DepthTexture, data.depth);
                cmd.SetComputeTextureParam(shader, data.buildHzbMip0Kernel,
                    ShaderIds.HzbMip, data.hzb, 0);
                cmd.DispatchCompute(shader, data.buildHzbMip0Kernel,
                    DivRoundUp(data.hzbWidth, 8), DivRoundUp(data.hzbHeight, 8), 1);
                int sourceWidth = data.hzbWidth;
                int sourceHeight = data.hzbHeight;
                for (int mip = 1; mip < data.hzbMipCount; mip++)
                {
                    int targetWidth = Mathf.Max(1, sourceWidth >> 1);
                    int targetHeight = Mathf.Max(1, sourceHeight >> 1);
                    cmd.SetComputeVectorParam(shader, ShaderIds.HzbSourceSize,
                        new Vector4(sourceWidth, sourceHeight, 0f, 0f));
                    cmd.SetComputeTextureParam(shader, data.buildHzbMipKernel,
                        ShaderIds.HzbSource, data.hzb, mip - 1);
                    cmd.SetComputeTextureParam(shader, data.buildHzbMipKernel,
                        ShaderIds.HzbMip, data.hzb, mip);
                    cmd.DispatchCompute(shader, data.buildHzbMipKernel,
                        DivRoundUp(targetWidth, 8), DivRoundUp(targetHeight, 8), 1);
                    sourceWidth = targetWidth;
                    sourceHeight = targetHeight;
                }
                cmd.EndSample("RealtimeGI/Build Screen HZB");

                cmd.BeginSample("RealtimeGI/Diffuse Screen Trace");
                cmd.SetBufferCounterValue(data.diffuseMissQueue, 0u);
                BindSceneInputs(cmd, shader, data, data.traceDiffuseScreenKernel);
                cmd.SetComputeTextureParam(shader, data.traceDiffuseScreenKernel,
                    ShaderIds.HzbTexture, data.hzb);
                cmd.SetComputeTextureParam(shader, data.traceDiffuseScreenKernel,
                    ShaderIds.CurrentDiffuse, data.currentDiffuse);
                cmd.SetComputeTextureParam(shader, data.traceDiffuseScreenKernel,
                    ShaderIds.CurrentDiffuseRay, data.currentDiffuseRay);
                cmd.SetComputeTextureParam(shader, data.traceDiffuseScreenKernel,
                    ShaderIds.CurrentDiffuseStats, data.currentDiffuseStats);
                cmd.SetComputeTextureParam(shader, data.traceDiffuseScreenKernel,
                    ShaderIds.CurrentDiffuseHit, data.currentDiffuseHit);
                cmd.SetComputeTextureParam(shader, data.traceDiffuseScreenKernel,
                    ShaderIds.DiffuseHistoryRead, data.diffuseRead);
                cmd.SetComputeTextureParam(shader, data.traceDiffuseScreenKernel,
                    ShaderIds.GeometryHistoryRead, data.geometryRead);
                cmd.SetComputeTextureParam(shader, data.traceDiffuseScreenKernel,
                    ShaderIds.DiffuseReservoirStatsRead, data.reservoirStatsRead);
                cmd.SetComputeBufferParam(shader, data.traceDiffuseScreenKernel,
                    ShaderIds.DiffuseMissQueue, data.diffuseMissQueue);
                cmd.DispatchCompute(shader, data.traceDiffuseScreenKernel,
                    DivRoundUp(data.diffuseWidth, 8), DivRoundUp(data.diffuseHeight, 8), 1);
                cmd.EndSample("RealtimeGI/Diffuse Screen Trace");

                cmd.BeginSample("RealtimeGI/Diffuse Compact World Misses");
                cmd.CopyCounterValue(data.diffuseMissQueue, data.diffuseMissDispatchArgs, 0u);
                cmd.SetComputeBufferParam(shader, data.buildDiffuseMissArgsKernel,
                    ShaderIds.DiffuseMissDispatchArgs, data.diffuseMissDispatchArgs);
                cmd.DispatchCompute(shader, data.buildDiffuseMissArgsKernel, 1, 1, 1);
                BindSceneInputs(cmd, shader, data, data.traceDiffuseWorldMissesKernel);
                cmd.SetComputeBufferParam(shader, data.traceDiffuseWorldMissesKernel,
                    ShaderIds.DiffuseMissQueueRead, data.diffuseMissQueue);
                cmd.SetComputeBufferParam(shader, data.traceDiffuseWorldMissesKernel,
                    ShaderIds.DiffuseMissDispatchArgs, data.diffuseMissDispatchArgs);
                cmd.SetComputeTextureParam(shader, data.traceDiffuseWorldMissesKernel,
                    ShaderIds.CurrentDiffuse, data.currentDiffuse);
                cmd.SetComputeTextureParam(shader, data.traceDiffuseWorldMissesKernel,
                    ShaderIds.CurrentDiffuseRay, data.currentDiffuseRay);
                cmd.SetComputeTextureParam(shader, data.traceDiffuseWorldMissesKernel,
                    ShaderIds.CurrentDiffuseStats, data.currentDiffuseStats);
                cmd.SetComputeTextureParam(shader, data.traceDiffuseWorldMissesKernel,
                    ShaderIds.CurrentDiffuseHit, data.currentDiffuseHit);
                cmd.DispatchCompute(shader, data.traceDiffuseWorldMissesKernel,
                    data.diffuseMissDispatchArgs, 0u);
                cmd.EndSample("RealtimeGI/Diffuse Compact World Misses");

                cmd.BeginSample("RealtimeGI/Diffuse Temporal");
                BindScreenInputs(cmd, shader, data, data.temporalDiffuseKernel);
                cmd.SetComputeBufferParam(shader, data.temporalDiffuseKernel,
                    ShaderIds.Levels, data.levelData);
                cmd.SetComputeBufferParam(shader, data.temporalDiffuseKernel,
                    ShaderIds.TraceCounters, data.traceCounters);
                cmd.SetComputeTextureParam(shader, data.temporalDiffuseKernel, ShaderIds.CurrentDiffuseRead, data.currentDiffuse);
                cmd.SetComputeTextureParam(shader, data.temporalDiffuseKernel,
                    ShaderIds.CurrentDiffuseRayRead, data.currentDiffuseRay);
                cmd.SetComputeTextureParam(shader, data.temporalDiffuseKernel,
                    ShaderIds.CurrentDiffuseStatsRead, data.currentDiffuseStats);
                cmd.SetComputeTextureParam(shader, data.temporalDiffuseKernel,
                    ShaderIds.CurrentDiffuseHitRead, data.currentDiffuseHit);
                cmd.SetComputeTextureParam(shader, data.temporalDiffuseKernel, ShaderIds.GeometryHistoryRead, data.geometryRead);
                cmd.SetComputeTextureParam(shader, data.temporalDiffuseKernel,
                    ShaderIds.DiffuseReservoirRadianceRead, data.reservoirRadianceRead);
                cmd.SetComputeTextureParam(shader, data.temporalDiffuseKernel,
                    ShaderIds.DiffuseReservoirRayRead, data.reservoirRayRead);
                cmd.SetComputeTextureParam(shader, data.temporalDiffuseKernel,
                    ShaderIds.DiffuseReservoirStatsRead, data.reservoirStatsRead);
                cmd.SetComputeTextureParam(shader, data.temporalDiffuseKernel,
                    ShaderIds.DiffuseReservoirHitRead, data.reservoirHitRead);
                cmd.SetComputeTextureParam(shader, data.temporalDiffuseKernel,
                    ShaderIds.DiffuseReservoirRadianceWrite, data.reservoirRadianceWrite);
                cmd.SetComputeTextureParam(shader, data.temporalDiffuseKernel,
                    ShaderIds.DiffuseReservoirRayWrite, data.reservoirRayWrite);
                cmd.SetComputeTextureParam(shader, data.temporalDiffuseKernel,
                    ShaderIds.DiffuseReservoirStatsWrite, data.reservoirStatsWrite);
                cmd.SetComputeTextureParam(shader, data.temporalDiffuseKernel,
                    ShaderIds.DiffuseReservoirHitWrite, data.reservoirHitWrite);
                cmd.DispatchCompute(shader, data.temporalDiffuseKernel,
                    DivRoundUp(data.diffuseWidth, 8), DivRoundUp(data.diffuseHeight, 8), 1);
                cmd.EndSample("RealtimeGI/Diffuse Temporal");

                BindScreenInputs(cmd, shader, data, data.updateGeometryKernel);
                cmd.SetComputeTextureParam(shader, data.updateGeometryKernel, ShaderIds.CurrentDiffuse, data.currentDiffuse);
                cmd.SetComputeTextureParam(shader, data.updateGeometryKernel, ShaderIds.GeometryHistoryWrite, data.geometryWrite);
                cmd.DispatchCompute(shader, data.updateGeometryKernel,
                    DivRoundUp(data.diffuseWidth, 8), DivRoundUp(data.diffuseHeight, 8), 1);

                cmd.BeginSample("RealtimeGI/Diffuse Spatial");
                BindScreenInputs(cmd, shader, data, data.spatialDiffuseKernel);
                cmd.SetComputeBufferParam(shader, data.spatialDiffuseKernel,
                    ShaderIds.Levels, data.levelData);
                cmd.SetComputeBufferParam(shader, data.spatialDiffuseKernel,
                    ShaderIds.TraceCounters, data.traceCounters);
                cmd.SetComputeTextureParam(shader, data.spatialDiffuseKernel,
                    ShaderIds.CurrentDiffuse, data.currentDiffuse);
                cmd.SetComputeTextureParam(shader, data.spatialDiffuseKernel,
                    ShaderIds.CurrentDiffuseRay, data.currentDiffuseRay);
                cmd.SetComputeTextureParam(shader, data.spatialDiffuseKernel,
                    ShaderIds.CurrentDiffuseStats, data.currentDiffuseStats);
                cmd.SetComputeTextureParam(shader, data.spatialDiffuseKernel,
                    ShaderIds.CurrentDiffuseHit, data.currentDiffuseHit);
                cmd.SetComputeTextureParam(shader, data.spatialDiffuseKernel,
                    ShaderIds.DiffuseReservoirRadianceRead, data.reservoirRadianceWrite);
                cmd.SetComputeTextureParam(shader, data.spatialDiffuseKernel,
                    ShaderIds.DiffuseReservoirRayRead, data.reservoirRayWrite);
                cmd.SetComputeTextureParam(shader, data.spatialDiffuseKernel,
                    ShaderIds.DiffuseReservoirStatsRead, data.reservoirStatsWrite);
                cmd.SetComputeTextureParam(shader, data.spatialDiffuseKernel,
                    ShaderIds.DiffuseReservoirHitRead, data.reservoirHitWrite);
                cmd.DispatchCompute(shader, data.spatialDiffuseKernel,
                    DivRoundUp(data.diffuseWidth, 8), DivRoundUp(data.diffuseHeight, 8), 1);
                cmd.EndSample("RealtimeGI/Diffuse Spatial");

                cmd.BeginSample("RealtimeGI/Diffuse Denoise");
                BindScreenInputs(cmd, shader, data, data.denoiseDiffuseKernel);
                cmd.SetComputeTextureParam(shader, data.denoiseDiffuseKernel,
                    ShaderIds.CurrentDiffuseRead, data.currentDiffuse);
                cmd.SetComputeTextureParam(shader, data.denoiseDiffuseKernel,
                    ShaderIds.DiffuseHistoryRead, data.diffuseRead);
                cmd.SetComputeTextureParam(shader, data.denoiseDiffuseKernel,
                    ShaderIds.GeometryHistoryRead, data.geometryRead);
                cmd.SetComputeTextureParam(shader, data.denoiseDiffuseKernel,
                    ShaderIds.DiffuseHistoryWrite, data.diffuseWrite);
                cmd.DispatchCompute(shader, data.denoiseDiffuseKernel,
                    DivRoundUp(data.diffuseWidth, 8), DivRoundUp(data.diffuseHeight, 8), 1);
                cmd.EndSample("RealtimeGI/Diffuse Denoise");

                cmd.BeginSample("RealtimeGI/Diffuse Geometry Filter");
                BindScreenInputs(cmd, shader, data, data.filterDiffuseKernel);
                cmd.SetComputeIntParam(shader, ShaderIds.DiffuseAtrousStep, 1);
                cmd.SetComputeTextureParam(shader, data.filterDiffuseKernel,
                    ShaderIds.DiffuseFiltered, data.diffuseWrite);
                cmd.SetComputeTextureParam(shader, data.filterDiffuseKernel,
                    ShaderIds.DiffuseDenoised, data.diffuseDenoised);
                cmd.DispatchCompute(shader, data.filterDiffuseKernel,
                    DivRoundUp(data.diffuseWidth, 8), DivRoundUp(data.diffuseHeight, 8), 1);
                cmd.SetComputeIntParam(shader, ShaderIds.DiffuseAtrousStep, 2);
                cmd.SetComputeTextureParam(shader, data.filterDiffuseKernel,
                    ShaderIds.DiffuseFiltered, data.diffuseDenoised);
                cmd.SetComputeTextureParam(shader, data.filterDiffuseKernel,
                    ShaderIds.DiffuseDenoised, data.diffuseAtrous);
                cmd.DispatchCompute(shader, data.filterDiffuseKernel,
                    DivRoundUp(data.diffuseWidth, 8), DivRoundUp(data.diffuseHeight, 8), 1);
                cmd.SetComputeIntParam(shader, ShaderIds.DiffuseAtrousStep, 4);
                cmd.SetComputeTextureParam(shader, data.filterDiffuseKernel,
                    ShaderIds.DiffuseFiltered, data.diffuseAtrous);
                cmd.SetComputeTextureParam(shader, data.filterDiffuseKernel,
                    ShaderIds.DiffuseDenoised, data.diffuseDenoised);
                cmd.DispatchCompute(shader, data.filterDiffuseKernel,
                    DivRoundUp(data.diffuseWidth, 8), DivRoundUp(data.diffuseHeight, 8), 1);
                cmd.EndSample("RealtimeGI/Diffuse Geometry Filter");

                cmd.BeginSample("RealtimeGI/Specular Trace");
                BindSceneInputs(cmd, shader, data, data.traceSpecularKernel);
                cmd.SetComputeTextureParam(shader, data.traceSpecularKernel,
                    ShaderIds.HzbTexture, data.hzb);
                cmd.SetComputeTextureParam(shader, data.traceSpecularKernel,
                    ShaderIds.DiffuseHistoryRead, data.diffuseRead);
                cmd.SetComputeTextureParam(shader, data.traceSpecularKernel,
                    ShaderIds.GeometryHistoryRead, data.geometryRead);
                cmd.SetComputeTextureParam(shader, data.traceSpecularKernel,
                    ShaderIds.CurrentSpecular, data.currentSpecular);
                cmd.SetComputeTextureParam(shader, data.traceSpecularKernel,
                    ShaderIds.CurrentSpecularRay, data.currentSpecularRay);
                cmd.DispatchCompute(shader, data.traceSpecularKernel,
                    DivRoundUp(data.specularWidth, 8), DivRoundUp(data.specularHeight, 8), 1);
                cmd.EndSample("RealtimeGI/Specular Trace");

                cmd.BeginSample("RealtimeGI/Specular Ray Resolve");
                BindScreenInputs(cmd, shader, data, data.resolveSpecularKernel);
                cmd.SetComputeTextureParam(shader, data.resolveSpecularKernel,
                    ShaderIds.CurrentSpecularRead, data.currentSpecular);
                cmd.SetComputeTextureParam(shader, data.resolveSpecularKernel,
                    ShaderIds.CurrentSpecularRayRead, data.currentSpecularRay);
                cmd.SetComputeTextureParam(shader, data.resolveSpecularKernel,
                    ShaderIds.ResolvedSpecular, data.resolvedSpecular);
                cmd.SetComputeTextureParam(shader, data.resolveSpecularKernel,
                    ShaderIds.ResolvedSpecularRay, data.resolvedSpecularRay);
                cmd.DispatchCompute(shader, data.resolveSpecularKernel,
                    DivRoundUp(data.fullWidth, 8), DivRoundUp(data.fullHeight, 8), 1);
                cmd.EndSample("RealtimeGI/Specular Ray Resolve");

                cmd.BeginSample("RealtimeGI/Specular Temporal");
                BindScreenInputs(cmd, shader, data, data.temporalSpecularKernel);
                cmd.SetComputeTextureParam(shader, data.temporalSpecularKernel,
                    ShaderIds.ResolvedSpecularRead, data.resolvedSpecular);
                cmd.SetComputeTextureParam(shader, data.temporalSpecularKernel,
                    ShaderIds.ResolvedSpecularRayRead, data.resolvedSpecularRay);
                cmd.SetComputeTextureParam(shader, data.temporalSpecularKernel,
                    ShaderIds.SpecularHistoryRead, data.specularRead);
                cmd.SetComputeTextureParam(shader, data.temporalSpecularKernel,
                    ShaderIds.GeometryHistoryRead, data.geometryRead);
                cmd.SetComputeTextureParam(shader, data.temporalSpecularKernel,
                    ShaderIds.SpecularHistoryWrite, data.specularWrite);
                cmd.DispatchCompute(shader, data.temporalSpecularKernel,
                    DivRoundUp(data.fullWidth, 8), DivRoundUp(data.fullHeight, 8), 1);
                cmd.EndSample("RealtimeGI/Specular Temporal");

                cmd.BeginSample("RealtimeGI/Specular Spatial");
                BindScreenInputs(cmd, shader, data, data.spatialSpecularKernel);
                cmd.SetComputeTextureParam(shader, data.spatialSpecularKernel,
                    ShaderIds.SpecularTemporalRead, data.specularWrite);
                cmd.SetComputeTextureParam(shader, data.spatialSpecularKernel,
                    ShaderIds.SpecularSpatial, data.specularSpatial);
                cmd.DispatchCompute(shader, data.spatialSpecularKernel,
                    DivRoundUp(data.fullWidth, 8), DivRoundUp(data.fullHeight, 8), 1);
                cmd.EndSample("RealtimeGI/Specular Spatial");

                cmd.BeginSample("RealtimeGI/Upsample");
                BindScreenInputs(cmd, shader, data, data.upsampleKernel);
                cmd.SetComputeTextureParam(shader, data.upsampleKernel,
                    ShaderIds.DiffuseFiltered, data.diffuseDenoised);
                cmd.SetComputeTextureParam(shader, data.upsampleKernel,
                    ShaderIds.CurrentDiffuseRead, data.currentDiffuse);
                cmd.SetComputeTextureParam(shader, data.upsampleKernel,
                    ShaderIds.DiffuseHistoryRead, data.diffuseWrite);
                cmd.SetComputeTextureParam(shader, data.upsampleKernel,
                    ShaderIds.GeometryHistoryRead, data.geometryRead);
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
                cmd.SetComputeTextureParam(shader, kernel, ShaderIds.SceneColorHistory,
                    data.sceneColorHistory);
            }

            static void BindSceneInputs(UnsafeCommandBuffer cmd, ComputeShader shader, PassData data, int kernel)
            {
                BindScreenInputs(cmd, shader, data, kernel);
                cmd.SetComputeBufferParam(shader, kernel, ShaderIds.Levels, data.levelData);
                cmd.SetComputeBufferParam(shader, kernel, ShaderIds.StaticPageTable, data.staticPageTable);
                cmd.SetComputeBufferParam(shader, kernel, ShaderIds.StaticOccupancy, data.staticOccupancy);
                cmd.SetComputeBufferParam(shader, kernel, ShaderIds.StaticSurface, data.staticSurface);
                cmd.SetComputeBufferParam(shader, kernel, ShaderIds.StaticSurfaceIdentity, data.staticSurfaceIdentity);
                cmd.SetComputeBufferParam(shader, kernel, ShaderIds.StaticDistance, data.staticDistance);
                cmd.SetComputeBufferParam(shader, kernel, ShaderIds.StaticRadiance, data.staticRadiance);
                cmd.SetComputeBufferParam(shader, kernel, ShaderIds.StaticValidity, data.staticValidity);
                cmd.SetComputeBufferParam(shader, kernel, ShaderIds.DynamicPageTable, data.dynamicPageTable);
                cmd.SetComputeBufferParam(shader, kernel, ShaderIds.DynamicOccupancy, data.dynamicOccupancy);
                cmd.SetComputeBufferParam(shader, kernel, ShaderIds.DynamicSurface, data.dynamicSurface);
                cmd.SetComputeBufferParam(shader, kernel, ShaderIds.DynamicSurfaceIdentity, data.dynamicSurfaceIdentity);
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
                diffuseMissQueue?.Release();
                diffuseMissDispatchArgs?.Release();
                traceCounterBuffer = null;
                diffuseMissQueue = null;
                diffuseMissDispatchArgs = null;
                diffuseMissCapacity = 0;
            }
        }

        static class ShaderIds
        {
            public static readonly int DepthTexture = Shader.PropertyToID("_GIDepthTexture");
            public static readonly int NormalsTexture = Shader.PropertyToID("_GINormalsTexture");
            public static readonly int MotionTexture = Shader.PropertyToID("_GIMotionTexture");
            public static readonly int GBuffer0 = Shader.PropertyToID("_GIGBuffer0");
            public static readonly int GBuffer1 = Shader.PropertyToID("_GIGBuffer1");
            public static readonly int GBuffer2 = Shader.PropertyToID("_GIGBuffer2");
            public static readonly int SceneColorHistory = Shader.PropertyToID("_GISceneColorHistory");
            public static readonly int CurrentDiffuse = Shader.PropertyToID("_GICurrentDiffuse");
            public static readonly int CurrentDiffuseRay = Shader.PropertyToID("_GICurrentDiffuseRay");
            public static readonly int CurrentDiffuseStats = Shader.PropertyToID("_GICurrentDiffuseStats");
            public static readonly int CurrentDiffuseHit = Shader.PropertyToID("_GICurrentDiffuseHit");
            public static readonly int CurrentSpecular = Shader.PropertyToID("_GICurrentSpecular");
            public static readonly int CurrentSpecularRay = Shader.PropertyToID("_GICurrentSpecularRay");
            public static readonly int CurrentDiffuseRead = Shader.PropertyToID("_GICurrentDiffuseRead");
            public static readonly int CurrentDiffuseRayRead = Shader.PropertyToID("_GICurrentDiffuseRayRead");
            public static readonly int CurrentDiffuseStatsRead = Shader.PropertyToID("_GICurrentDiffuseStatsRead");
            public static readonly int CurrentDiffuseHitRead = Shader.PropertyToID("_GICurrentDiffuseHitRead");
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
            public static readonly int DiffuseDenoised = Shader.PropertyToID("_GIDiffuseDenoised");
            public static readonly int DiffuseReservoirRadianceRead =
                Shader.PropertyToID("_GIDiffuseReservoirRadianceRead");
            public static readonly int DiffuseReservoirRadianceWrite =
                Shader.PropertyToID("_GIDiffuseReservoirRadianceWrite");
            public static readonly int DiffuseReservoirRayRead =
                Shader.PropertyToID("_GIDiffuseReservoirRayRead");
            public static readonly int DiffuseReservoirRayWrite =
                Shader.PropertyToID("_GIDiffuseReservoirRayWrite");
            public static readonly int DiffuseReservoirStatsRead =
                Shader.PropertyToID("_GIDiffuseReservoirStatsRead");
            public static readonly int DiffuseReservoirStatsWrite =
                Shader.PropertyToID("_GIDiffuseReservoirStatsWrite");
            public static readonly int DiffuseReservoirHitRead =
                Shader.PropertyToID("_GIDiffuseReservoirHitRead");
            public static readonly int DiffuseReservoirHitWrite =
                Shader.PropertyToID("_GIDiffuseReservoirHitWrite");
            public static readonly int SpecularHistoryRead = Shader.PropertyToID("_GISpecularHistoryRead");
            public static readonly int SpecularHistoryWrite = Shader.PropertyToID("_GISpecularHistoryWrite");
            public static readonly int GeometryHistoryRead = Shader.PropertyToID("_GIGeometryHistoryRead");
            public static readonly int GeometryHistoryWrite = Shader.PropertyToID("_GIGeometryHistoryWrite");
            public static readonly int DiffuseFiltered = Shader.PropertyToID("_GIDiffuseFiltered");
            public static readonly int SpecularFiltered = Shader.PropertyToID("_GISpecularFiltered");
            public static readonly int FullLighting = Shader.PropertyToID("_GIFullLighting");
            public static readonly int LightingTexture = Shader.PropertyToID("_GILightingTexture");
            public static readonly int SpecularLightingTexture = Shader.PropertyToID("_GISpecularLightingTexture");
            public static readonly int Levels = Shader.PropertyToID("_GIClipmapLevels");
            public static readonly int StaticPageTable = Shader.PropertyToID("_GIStaticPageTable");
            public static readonly int StaticOccupancy = Shader.PropertyToID("_GIStaticOccupancy");
            public static readonly int StaticSurface = Shader.PropertyToID("_GIStaticSurface");
            public static readonly int StaticSurfaceIdentity = Shader.PropertyToID("_GIStaticSurfaceIdentity");
            public static readonly int StaticDistance = Shader.PropertyToID("_GIStaticDistance");
            public static readonly int StaticRadiance = Shader.PropertyToID("_GIStaticRadiance");
            public static readonly int StaticValidity = Shader.PropertyToID("_GIStaticValidity");
            public static readonly int DynamicPageTable = Shader.PropertyToID("_GIDynamicPageTable");
            public static readonly int DynamicOccupancy = Shader.PropertyToID("_GIDynamicOccupancy");
            public static readonly int DynamicSurface = Shader.PropertyToID("_GIDynamicSurface");
            public static readonly int DynamicSurfaceIdentity = Shader.PropertyToID("_GIDynamicSurfaceIdentity");
            public static readonly int DynamicDistance = Shader.PropertyToID("_GIDynamicDistance");
            public static readonly int DynamicRadiance = Shader.PropertyToID("_GIDynamicRadiance");
            public static readonly int DynamicValidity = Shader.PropertyToID("_GIDynamicValidity");
            public static readonly int Materials = Shader.PropertyToID("_GIMaterials");
            public static readonly int InverseViewProjection = Shader.PropertyToID("_GIInverseViewProjection");
            public static readonly int ViewProjection = Shader.PropertyToID("_GIViewProjection");
            public static readonly int PreviousViewProjection = Shader.PropertyToID("_GIPreviousViewProjection");
            public static readonly int PreviousInverseViewProjection =
                Shader.PropertyToID("_GIPreviousInverseViewProjection");
            public static readonly int CameraPosition = Shader.PropertyToID("_GICameraPosition");
            public static readonly int MainLightDirection = Shader.PropertyToID("_GIMainLightDirection");
            public static readonly int MainLightColor = Shader.PropertyToID("_GIMainLightColor");
            public static readonly int MaxTraceDistance = Shader.PropertyToID("_GIMaxTraceDistance");
            public static readonly int ScreenTraceDistance = Shader.PropertyToID("_GIScreenTraceDistance");
            public static readonly int DiffuseHistoryWeight = Shader.PropertyToID("_GIDiffuseHistoryWeight");
            public static readonly int SpecularHistoryWeight = Shader.PropertyToID("_GISpecularHistoryWeight");
            public static readonly int DiffuseIntensity = Shader.PropertyToID("_GIDiffuseIntensity");
            public static readonly int DiffuseSpatialSamples = Shader.PropertyToID("_GIDiffuseSpatialSamples");
            public static readonly int DiffuseSpatialRadius = Shader.PropertyToID("_GIDiffuseSpatialRadius");
            public static readonly int SpecularIntensity = Shader.PropertyToID("_GISpecularIntensity");
            public static readonly int MinSpecularRoughness = Shader.PropertyToID("_GIMinSpecularRoughness");
            public static readonly int MaxSpecularRoughness = Shader.PropertyToID("_GIMaxSpecularRoughness");
            public static readonly int ViewportSize = Shader.PropertyToID("_GIViewportSize");
            public static readonly int DiffuseSize = Shader.PropertyToID("_GIDiffuseSize");
            public static readonly int SpecularBufferSize = Shader.PropertyToID("_GISpecularBufferSize");
            public static readonly int FrameIndex = Shader.PropertyToID("_GIFrameIndex");
            public static readonly int DiffuseRayCount = Shader.PropertyToID("_GIDiffuseRayCount");
            public static readonly int MaterialCount = Shader.PropertyToID("_GIMaterialCount");
            public static readonly int HistoryValid = Shader.PropertyToID("_GIHistoryValid");
            public static readonly int SceneColorHistoryValid =
                Shader.PropertyToID("_GISceneColorHistoryValid");
            public static readonly int EnableSceneColorHistory =
                Shader.PropertyToID("_GIEnableSceneColorHistory");
            public static readonly int EnableDiffuse = Shader.PropertyToID("_GIEnableDiffuse");
            public static readonly int DebugHitMaterialColor = Shader.PropertyToID("_GIDebugHitMaterialColor");
            public static readonly int DiffuseDebugStage = Shader.PropertyToID("_GIDiffuseDebugStage");
            public static readonly int DiffuseAtrousStep = Shader.PropertyToID("_GIDiffuseAtrousStep");
            public static readonly int EnableSpecular = Shader.PropertyToID("_GIEnableSpecular");
            public static readonly int SpecularSpatialSamples = Shader.PropertyToID("_GISpecularSpatialSamples");
            public static readonly int TraceCounters = Shader.PropertyToID("_GITraceCounters");
            public static readonly int EnableTraceCounters = Shader.PropertyToID("_GIEnableTraceCounters");
            public static readonly int HzbTexture = Shader.PropertyToID("_GIHzbTexture");
            public static readonly int HzbSource = Shader.PropertyToID("_GIHzbSource");
            public static readonly int HzbMip = Shader.PropertyToID("_GIHzbMip");
            public static readonly int HzbMipCount = Shader.PropertyToID("_GIHzbMipCount");
            public static readonly int UseHzb = Shader.PropertyToID("_GIUseHzb");
            public static readonly int HzbSourceSize = Shader.PropertyToID("_GIHzbSourceSize");
            public static readonly int DiffuseMissQueue = Shader.PropertyToID("_GIDiffuseMissQueue");
            public static readonly int DiffuseMissQueueRead = Shader.PropertyToID("_GIDiffuseMissQueueRead");
            public static readonly int DiffuseMissDispatchArgs =
                Shader.PropertyToID("_GIDiffuseMissDispatchArgs");
            public static readonly int DeferredInjectionActive = Shader.PropertyToID("_RealtimeGIDeferredInjection");
        }
    }
}
