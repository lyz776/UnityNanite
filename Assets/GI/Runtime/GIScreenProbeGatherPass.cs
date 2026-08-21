using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace UnityNanite.GI
{
    /// <summary>
    /// Minimal diffuse gather: one low-resolution receiver layer, four cosine
    /// rays, bounded temporal reuse, and geometry-aware reconstruction.
    /// </summary>
    internal sealed class GIScreenProbeGatherPass : ScriptableRenderPass, System.IDisposable
    {
        readonly GIWorldCache world;
        readonly GIWorldRendererFeature.Settings settings;
        readonly RTHandle[] historyIrradiance = new RTHandle[2];
        readonly RTHandle[] historyPosition = new RTHandle[2];
        readonly RTHandle[] historyNormal = new RTHandle[2];
        readonly RTHandle[] historyMoments = new RTHandle[2];
        readonly RTHandle[] historySpecular = new RTHandle[2];
        Matrix4x4 previousViewProjection;
        int historyWidth;
        int historyHeight;
        int historyReadIndex;
        uint frameIndex;
        bool historyValid;
        bool loggedReady;

        sealed class PassData
        {
            public ComputeShader shader;
            public int geometryKernel;
            public int primaryKernel;
            public int tracePrimaryKernel;
            public int specularKernel;
            public int resolveKernel;
            public int temporalPrimaryKernel;
            public int specularHistoryKernel;
            public int probeFilterKernel;
            public int specularFilterKernel;
            public int filterKernel;
            public int specularResolveKernel;
            public int debugKernel;
            public bool writeDebug;
            public TextureHandle depth;
            public TextureHandle displayNormal;
            public TextureHandle cameraColor;
            public TextureHandle specularBase;
            public TextureHandle specularMaterial;
            public TextureHandle surfaceRadiance;
            public TextureHandle geometryNormal;
            public TextureHandle primaryPosition;
            public TextureHandle primaryNormal;
            public TextureHandle primaryPixel;
            public TextureHandle primaryIrradiance;
            public TextureHandle primarySpecular;
            public TextureHandle rawIrradiance;
            public TextureHandle historyIrradianceInput;
            public TextureHandle historyPositionInput;
            public TextureHandle historyNormalInput;
            public TextureHandle historyMomentsInput;
            public TextureHandle historyIrradianceOutput;
            public TextureHandle historyPositionOutput;
            public TextureHandle historyNormalOutput;
            public TextureHandle historyMomentsOutput;
            public TextureHandle specularHistoryInput;
            public TextureHandle specularHistoryOutput;
            public TextureHandle filteredProbeIrradiance;
            public TextureHandle filteredSpecular;
            public TextureHandle resolvedIrradiance;
            public TextureHandle resolvedSpecular;
            public TextureHandle resolvedSpecularInput;
            public TextureHandle debug;
            public GIWorldGpuView world;
            public Matrix4x4 inverseViewProjection;
            public Matrix4x4 viewProjection;
            public Matrix4x4 previousViewProjection;
            public Vector3 cameraPosition;
            public Vector3 mainLightDirection;
            public Color mainLightColor;
            public bool hasMainLight;
            public int screenWidth;
            public int screenHeight;
            public int probeWidth;
            public int probeHeight;
            public int tileSize;
            public int debugMode;
            public float traceDistance;
            public uint frameIndex;
            public bool historyValid;
        }

        public GIScreenProbeGatherPass(
            GIWorldCache world, GIWorldRendererFeature.Settings settings)
        {
            this.world = world;
            this.settings = settings;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (!frameData.Contains<GIScreenSurfaceData>() ||
                !world.TryGetGpuView(out GIWorldGpuView worldView))
                return;
            GIScreenSurfaceData surface = frameData.Get<GIScreenSurfaceData>();
            UniversalResourceData resources = frameData.Get<UniversalResourceData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            if (!surface.radiance.IsValid() || resources.gBuffer == null ||
                resources.gBuffer.Length < 3 || !resources.gBuffer[2].IsValid() ||
                !resources.cameraDepthTexture.IsValid())
                return;

            int screenWidth = Mathf.Max(1, cameraData.scaledWidth);
            int screenHeight = Mathf.Max(1, cameraData.scaledHeight);
            int tileSize = Mathf.Clamp(settings.screenProbeTileSize, 8, 32);
            int probeWidth = (screenWidth + tileSize - 1) / tileSize;
            int probeHeight = (screenHeight + tileSize - 1) / tileSize;
            EnsureHistory(probeWidth, probeHeight);
            int historyWriteIndex = 1 - historyReadIndex;
            TextureHandle historyIrradianceInput = renderGraph.ImportTexture(historyIrradiance[historyReadIndex]);
            TextureHandle historyPositionInput = renderGraph.ImportTexture(historyPosition[historyReadIndex]);
            TextureHandle historyNormalInput = renderGraph.ImportTexture(historyNormal[historyReadIndex]);
            TextureHandle historyMomentsInput = renderGraph.ImportTexture(historyMoments[historyReadIndex]);
            TextureHandle historyIrradianceOutput = renderGraph.ImportTexture(historyIrradiance[historyWriteIndex]);
            TextureHandle historyPositionOutput = renderGraph.ImportTexture(historyPosition[historyWriteIndex]);
            TextureHandle historyNormalOutput = renderGraph.ImportTexture(historyNormal[historyWriteIndex]);
            TextureHandle historyMomentsOutput = renderGraph.ImportTexture(historyMoments[historyWriteIndex]);
            TextureHandle specularHistoryInput = renderGraph.ImportTexture(historySpecular[historyReadIndex]);
            TextureHandle specularHistoryOutput = renderGraph.ImportTexture(historySpecular[historyWriteIndex]);
            TextureHandle geometryNormal = CreateTexture(renderGraph, screenWidth, screenHeight,
                GraphicsFormat.R16G16B16A16_SFloat, "GI Geometry Normal");
            TextureHandle specularBase = resources.gBuffer[0];
            TextureHandle specularMaterial = resources.gBuffer[1];
            TextureHandle primaryPosition = CreateTexture(renderGraph, probeWidth, probeHeight,
                GraphicsFormat.R32G32B32A32_SFloat, "GI Primary Position");
            TextureHandle primaryNormal = CreateTexture(renderGraph, probeWidth, probeHeight,
                GraphicsFormat.R16G16B16A16_SFloat, "GI Primary Normal");
            TextureHandle primaryPixel = CreateTexture(renderGraph, probeWidth, probeHeight,
                GraphicsFormat.R32G32_UInt, "GI Primary Pixel");
            TextureHandle primaryIrradiance = CreateTexture(renderGraph, probeWidth, probeHeight,
                GraphicsFormat.R16G16B16A16_SFloat, "GI Primary Irradiance");
            TextureHandle primarySpecular = CreateTexture(renderGraph, probeWidth, probeHeight,
                GraphicsFormat.R16G16B16A16_SFloat, "GI Primary Specular");
            TextureHandle rawIrradiance = CreateTexture(renderGraph, screenWidth, screenHeight,
                GraphicsFormat.R16G16B16A16_SFloat, "GI Raw Diffuse Irradiance");
            TextureHandle filteredProbeIrradiance = CreateTexture(renderGraph, probeWidth, probeHeight,
                GraphicsFormat.R16G16B16A16_SFloat, "GI Filtered Probe Irradiance");
            TextureHandle filteredSpecular = CreateTexture(renderGraph, probeWidth, probeHeight,
                GraphicsFormat.R16G16B16A16_SFloat, "GI Filtered Specular");
            TextureHandle resolvedIrradiance = CreateTexture(renderGraph, screenWidth, screenHeight,
                GraphicsFormat.R16G16B16A16_SFloat, "GI Resolved Diffuse Irradiance");
            TextureHandle resolvedSpecular = CreateTexture(renderGraph, screenWidth, screenHeight,
                GraphicsFormat.R16G16B16A16_SFloat, "GI Resolved Specular");
            TextureHandle debug = settings.enableDebug &&
                                  (int)settings.debugMode >=
                                  (int)GIWorldRendererFeature.DebugMode.PrimaryProbePlacement
                ? CreateTexture(renderGraph, screenWidth, screenHeight,
                    GraphicsFormat.R16G16B16A16_SFloat, "GI Probe Debug")
                : TextureHandle.nullHandle;

            Matrix4x4 projection = GL.GetGPUProjectionMatrix(cameraData.GetProjectionMatrix(), true);
            Matrix4x4 viewProjection = projection * cameraData.GetViewMatrix();
            ResolveMainLight(frameData.Get<UniversalLightData>(), out Vector3 lightDirection,
                out Color lightColor, out bool hasMainLight);

            using (var builder = renderGraph.AddUnsafePass<PassData>(
                       "GI/Diffuse Screen Probe Gather", out var data))
            {
                data.shader = settings.screenProbeShader;
                data.geometryKernel = data.shader.FindKernel("BuildGeometryNormals");
                data.primaryKernel = data.shader.FindKernel("PlacePrimaryProbes");
                data.tracePrimaryKernel = data.shader.FindKernel("TracePrimaryRays");
                data.resolveKernel = data.shader.FindKernel("ResolveDiffuseIrradiance");
                data.specularKernel = data.shader.FindKernel("TraceSpecularRays");
                data.temporalPrimaryKernel = data.shader.FindKernel("AccumulatePrimaryHistory");
                data.specularHistoryKernel = data.shader.FindKernel("AccumulateSpecularHistory");
                data.probeFilterKernel = data.shader.FindKernel("FilterProbeCache");
                data.specularFilterKernel = data.shader.FindKernel("FilterSpecularCache");
                data.filterKernel = data.shader.FindKernel("FilterDiffuseIrradiance");
                data.specularResolveKernel = data.shader.FindKernel("ResolveSpecularIrradiance");
                data.debugKernel = data.shader.FindKernel("VisualizePrimaryProbes");
                data.writeDebug = debug.IsValid();
                data.depth = resources.cameraDepthTexture;
                data.displayNormal = resources.gBuffer[2];
                data.cameraColor = resources.activeColorTexture;
                data.specularBase = specularBase;
                data.specularMaterial = specularMaterial;
                data.surfaceRadiance = surface.radiance;
                data.geometryNormal = geometryNormal;
                data.primaryPosition = primaryPosition;
                data.primaryNormal = primaryNormal;
                data.primaryPixel = primaryPixel;
                data.primaryIrradiance = primaryIrradiance;
                data.primarySpecular = primarySpecular;
                data.rawIrradiance = rawIrradiance;
                data.historyIrradianceInput = historyIrradianceInput;
                data.historyPositionInput = historyPositionInput;
                data.historyNormalInput = historyNormalInput;
                data.historyMomentsInput = historyMomentsInput;
                data.historyIrradianceOutput = historyIrradianceOutput;
                data.historyPositionOutput = historyPositionOutput;
                data.historyNormalOutput = historyNormalOutput;
                data.historyMomentsOutput = historyMomentsOutput;
                data.specularHistoryInput = specularHistoryInput;
                data.specularHistoryOutput = specularHistoryOutput;
                data.filteredProbeIrradiance = filteredProbeIrradiance;
                data.filteredSpecular = filteredSpecular;
                data.resolvedIrradiance = resolvedIrradiance;
                data.resolvedSpecular = resolvedSpecular;
                data.resolvedSpecularInput = resolvedSpecular;
                data.debug = debug;
                data.world = worldView;
                data.inverseViewProjection = viewProjection.inverse;
                data.viewProjection = viewProjection;
                data.previousViewProjection = previousViewProjection;
                data.cameraPosition = cameraData.camera.transform.position;
                data.mainLightDirection = lightDirection;
                data.mainLightColor = lightColor;
                data.hasMainLight = hasMainLight;
                data.screenWidth = screenWidth;
                data.screenHeight = screenHeight;
                data.probeWidth = probeWidth;
                data.probeHeight = probeHeight;
                data.tileSize = tileSize;
                data.debugMode = (int)settings.debugMode -
                                 (int)GIWorldRendererFeature.DebugMode.PrimaryProbePlacement;
                data.traceDistance = settings.diffuseTraceDistance;
                data.frameIndex = frameIndex;
                data.historyValid = historyValid;

                builder.UseTexture(data.depth, AccessFlags.Read);
                builder.UseTexture(data.displayNormal, AccessFlags.Read);
                builder.UseTexture(data.cameraColor, AccessFlags.Read);
                builder.UseTexture(data.specularBase, AccessFlags.Read);
                builder.UseTexture(data.specularMaterial, AccessFlags.Read);
                builder.UseTexture(data.surfaceRadiance, AccessFlags.Read);
                builder.UseTexture(geometryNormal, AccessFlags.ReadWrite);
                builder.UseTexture(primaryPosition, AccessFlags.ReadWrite);
                builder.UseTexture(primaryNormal, AccessFlags.ReadWrite);
                builder.UseTexture(primaryPixel, AccessFlags.ReadWrite);
                builder.UseTexture(primaryIrradiance, AccessFlags.ReadWrite);
                builder.UseTexture(primarySpecular, AccessFlags.ReadWrite);
                builder.UseTexture(rawIrradiance, AccessFlags.ReadWrite);
                builder.UseTexture(historyIrradianceInput, AccessFlags.Read);
                builder.UseTexture(historyPositionInput, AccessFlags.Read);
                builder.UseTexture(historyNormalInput, AccessFlags.Read);
                builder.UseTexture(historyMomentsInput, AccessFlags.Read);
                builder.UseTexture(historyIrradianceOutput, AccessFlags.ReadWrite);
                builder.UseTexture(historyPositionOutput, AccessFlags.Write);
                builder.UseTexture(historyNormalOutput, AccessFlags.Write);
                builder.UseTexture(historyMomentsOutput, AccessFlags.ReadWrite);
                builder.UseTexture(specularHistoryInput, AccessFlags.Read);
                builder.UseTexture(specularHistoryOutput, AccessFlags.ReadWrite);
                builder.UseTexture(filteredProbeIrradiance, AccessFlags.ReadWrite);
                builder.UseTexture(filteredSpecular, AccessFlags.ReadWrite);
                builder.UseTexture(resolvedIrradiance, AccessFlags.Write);
                builder.UseTexture(resolvedSpecular, AccessFlags.Write);
                if (data.writeDebug)
                    builder.UseTexture(debug, AccessFlags.Write);
                builder.UseBuffer(renderGraph.ImportBuffer(worldView.levels), AccessFlags.Read);
                builder.UseBuffer(renderGraph.ImportBuffer(worldView.pageTable), AccessFlags.Read);
                builder.UseBuffer(renderGraph.ImportBuffer(worldView.bricks), AccessFlags.Read);
                builder.UseBuffer(renderGraph.ImportBuffer(worldView.occupancy), AccessFlags.Read);
                builder.UseBuffer(renderGraph.ImportBuffer(worldView.surface), AccessFlags.Read);
                builder.UseBuffer(renderGraph.ImportBuffer(worldView.distance), AccessFlags.Read);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (PassData pass, UnsafeGraphContext context) => Execute(pass, context));
            }

            GIScreenProbeData probeData = frameData.GetOrCreate<GIScreenProbeData>();
            probeData.receiverPosition = primaryPosition;
            probeData.geometryNormal = geometryNormal;
            probeData.receiverNormal = primaryNormal;
            probeData.receiverPixel = primaryPixel;
            probeData.primaryIrradiance = primaryIrradiance;
            probeData.resolvedSpecular = resolvedSpecular;
            probeData.resolvedIrradiance = resolvedIrradiance;
            probeData.debug = debug;
            probeData.width = probeWidth;
            probeData.height = probeHeight;
            previousViewProjection = viewProjection;
            historyReadIndex = historyWriteIndex;
            historyValid = true;
            frameIndex++;

            if (!loggedReady)
            {
                loggedReady = true;
                Debug.Log(
                    $"[GI][Node3] Diffuse gather active: probes={probeWidth}x{probeHeight}, " +
                    $"tile={tileSize}, rays={probeWidth * probeHeight * 4}, " +
                    $"directions=4-low-discrepancy, temporal=32-frame+moments, probeFilter=5x5, " +
                    $"trace=screen-first/world-fallback, composite=on.");
            }
        }

        static void Execute(PassData data, UnsafeGraphContext context)
        {
            UnsafeCommandBuffer cmd = context.cmd;
            SetFrameParameters(cmd, data);
            BindCommon(cmd, data, data.geometryKernel);
            cmd.DispatchCompute(data.shader, data.geometryKernel,
                DivRoundUp(data.screenWidth, 8), DivRoundUp(data.screenHeight, 8), 1);
            BindCommon(cmd, data, data.primaryKernel);
            cmd.DispatchCompute(data.shader, data.primaryKernel,
                DivRoundUp(data.probeWidth, 8), DivRoundUp(data.probeHeight, 8), 1);
            BindCommon(cmd, data, data.tracePrimaryKernel);
            cmd.DispatchCompute(data.shader, data.tracePrimaryKernel,
                DivRoundUp(data.probeWidth, 8), DivRoundUp(data.probeHeight, 8), 1);
            BindCommon(cmd, data, data.specularKernel);
            cmd.DispatchCompute(data.shader, data.specularKernel,
                DivRoundUp(data.probeWidth, 8), DivRoundUp(data.probeHeight, 8), 1);
            BindCommon(cmd, data, data.temporalPrimaryKernel);
            cmd.DispatchCompute(data.shader, data.temporalPrimaryKernel,
                DivRoundUp(data.probeWidth, 8), DivRoundUp(data.probeHeight, 8), 1);
            BindCommon(cmd, data, data.specularHistoryKernel);
            cmd.DispatchCompute(data.shader, data.specularHistoryKernel,
                DivRoundUp(data.probeWidth, 8), DivRoundUp(data.probeHeight, 8), 1);
            BindCommon(cmd, data, data.probeFilterKernel);
            cmd.DispatchCompute(data.shader, data.probeFilterKernel,
                DivRoundUp(data.probeWidth, 8), DivRoundUp(data.probeHeight, 8), 1);
            BindCommon(cmd, data, data.specularFilterKernel);
            cmd.DispatchCompute(data.shader, data.specularFilterKernel,
                DivRoundUp(data.probeWidth, 8), DivRoundUp(data.probeHeight, 8), 1);
            BindCommon(cmd, data, data.resolveKernel);
            cmd.DispatchCompute(data.shader, data.resolveKernel,
                DivRoundUp(data.screenWidth, 8), DivRoundUp(data.screenHeight, 8), 1);
            BindCommon(cmd, data, data.filterKernel);
            cmd.DispatchCompute(data.shader, data.filterKernel,
                DivRoundUp(data.screenWidth, 8), DivRoundUp(data.screenHeight, 8), 1);
            BindCommon(cmd, data, data.specularResolveKernel);
            cmd.DispatchCompute(data.shader, data.specularResolveKernel,
                DivRoundUp(data.screenWidth, 8), DivRoundUp(data.screenHeight, 8), 1);
            if (data.writeDebug)
            {
                BindCommon(cmd, data, data.debugKernel);
                cmd.SetComputeTextureParam(data.shader, data.debugKernel, Ids.Debug, data.debug);
                cmd.DispatchCompute(data.shader, data.debugKernel,
                    DivRoundUp(data.screenWidth, 8), DivRoundUp(data.screenHeight, 8), 1);
            }
        }

        static void SetFrameParameters(UnsafeCommandBuffer cmd, PassData data)
        {
            cmd.SetComputeVectorParam(data.shader, Ids.ScreenSize,
                new Vector4(data.screenWidth, data.screenHeight, 0f, 0f));
            cmd.SetComputeVectorParam(data.shader, Ids.ProbeGridSize,
                new Vector4(data.probeWidth, data.probeHeight, 0f, 0f));
            cmd.SetComputeIntParam(data.shader, Ids.TileSize, data.tileSize);
            cmd.SetComputeIntParam(data.shader, Ids.DebugMode, data.debugMode);
            cmd.SetComputeFloatParam(data.shader, Ids.TraceDistance, data.traceDistance);
            cmd.SetComputeMatrixParam(data.shader, Ids.InverseViewProjection, data.inverseViewProjection);
            cmd.SetComputeMatrixParam(data.shader, Ids.ViewProjection, data.viewProjection);
            cmd.SetComputeMatrixParam(data.shader, Ids.PreviousViewProjection, data.previousViewProjection);
            cmd.SetComputeVectorParam(data.shader, Ids.CameraPosition, data.cameraPosition);
            cmd.SetComputeVectorParam(data.shader, Ids.MainLightDirection, data.mainLightDirection);
            cmd.SetComputeVectorParam(data.shader, Ids.MainLightColor, data.mainLightColor);
            cmd.SetComputeVectorParam(data.shader, Ids.EnvironmentSkyColor,
                Shader.GetGlobalVector(Ids.EnvironmentSkyColor));
            cmd.SetComputeFloatParam(data.shader, Ids.EnvironmentIntensity,
                Shader.GetGlobalFloat(Ids.EnvironmentIntensity));
            cmd.SetComputeIntParam(data.shader, Ids.HasMainLight, data.hasMainLight ? 1 : 0);
            cmd.SetComputeIntParam(data.shader, Ids.FrameIndex, (int)data.frameIndex);
            cmd.SetComputeIntParam(data.shader, Ids.HistoryValid, data.historyValid ? 1 : 0);
        }

        static void BindCommon(UnsafeCommandBuffer cmd, PassData data, int kernel)
        {
            cmd.SetComputeTextureParam(data.shader, kernel, Ids.Depth, data.depth);
            cmd.SetComputeTextureParam(data.shader, kernel, Ids.DisplayNormal, data.displayNormal);
            cmd.SetComputeTextureParam(data.shader, kernel, Ids.CameraColor, data.cameraColor);
            cmd.SetComputeTextureParam(data.shader, kernel, Ids.SpecularBase, data.specularBase);
            cmd.SetComputeTextureParam(data.shader, kernel, Ids.SpecularMaterial, data.specularMaterial);
            cmd.SetComputeTextureParam(data.shader, kernel, Ids.SurfaceRadiance, data.surfaceRadiance);
            cmd.SetComputeTextureParam(data.shader, kernel, Ids.GeometryNormal, data.geometryNormal);
            cmd.SetComputeTextureParam(data.shader, kernel, Ids.PrimaryPosition, data.primaryPosition);
            cmd.SetComputeTextureParam(data.shader, kernel, Ids.PrimaryNormal, data.primaryNormal);
            cmd.SetComputeTextureParam(data.shader, kernel, Ids.PrimaryPixel, data.primaryPixel);
            cmd.SetComputeTextureParam(data.shader, kernel, Ids.PrimaryIrradiance, data.primaryIrradiance);
            cmd.SetComputeTextureParam(data.shader, kernel, Ids.PrimarySpecular, data.primarySpecular);
            cmd.SetComputeTextureParam(data.shader, kernel, Ids.PrimarySpecularInput, data.primarySpecular);
            cmd.SetComputeTextureParam(data.shader, kernel, Ids.PrimarySpecularDebugInput,
                data.primarySpecular);
            cmd.SetComputeTextureParam(data.shader, kernel, Ids.RawIrradiance, data.rawIrradiance);
            cmd.SetComputeTextureParam(data.shader, kernel, Ids.HistoryIrradianceInput, data.historyIrradianceInput);
            cmd.SetComputeTextureParam(data.shader, kernel, Ids.HistoryPositionInput, data.historyPositionInput);
            cmd.SetComputeTextureParam(data.shader, kernel, Ids.HistoryNormalInput, data.historyNormalInput);
            cmd.SetComputeTextureParam(data.shader, kernel, Ids.HistoryMomentsInput, data.historyMomentsInput);
            cmd.SetComputeTextureParam(data.shader, kernel, Ids.HistoryIrradianceOutput, data.historyIrradianceOutput);
            cmd.SetComputeTextureParam(data.shader, kernel, Ids.HistoryPositionOutput, data.historyPositionOutput);
            cmd.SetComputeTextureParam(data.shader, kernel, Ids.HistoryNormalOutput, data.historyNormalOutput);
            cmd.SetComputeTextureParam(data.shader, kernel, Ids.HistoryMomentsOutput, data.historyMomentsOutput);
            cmd.SetComputeTextureParam(data.shader, kernel, Ids.SpecularHistoryInput, data.specularHistoryInput);
            cmd.SetComputeTextureParam(data.shader, kernel, Ids.SpecularHistoryOutput, data.specularHistoryOutput);
            cmd.SetComputeTextureParam(data.shader, kernel, Ids.SpecularHistoryOutputInput, data.specularHistoryOutput);
            cmd.SetComputeTextureParam(data.shader, kernel, Ids.FilteredProbeIrradiance, data.filteredProbeIrradiance);
            cmd.SetComputeTextureParam(data.shader, kernel, Ids.FilteredSpecular, data.filteredSpecular);
            cmd.SetComputeTextureParam(data.shader, kernel, Ids.FilteredSpecularInput, data.filteredSpecular);
            cmd.SetComputeTextureParam(data.shader, kernel, Ids.ResolvedIrradiance, data.resolvedIrradiance);
            cmd.SetComputeTextureParam(data.shader, kernel, Ids.ResolvedSpecular, data.resolvedSpecular);
            cmd.SetComputeTextureParam(data.shader, kernel, Ids.ResolvedSpecularInput,
                data.resolvedSpecularInput);
            cmd.SetComputeTextureParam(data.shader, kernel, Ids.GeometryNormalInput, data.geometryNormal);
            cmd.SetComputeTextureParam(data.shader, kernel, Ids.PrimaryPositionInput, data.primaryPosition);
            cmd.SetComputeTextureParam(data.shader, kernel, Ids.PrimaryNormalInput, data.primaryNormal);
            cmd.SetComputeTextureParam(data.shader, kernel, Ids.PrimaryPixelInput, data.primaryPixel);
            cmd.SetComputeTextureParam(data.shader, kernel, Ids.PrimaryIrradianceInput, data.primaryIrradiance);
            cmd.SetComputeTextureParam(data.shader, kernel, Ids.FilteredProbeIrradianceInput, data.filteredProbeIrradiance);
            cmd.SetComputeBufferParam(data.shader, kernel, Ids.Levels, data.world.levels);
            cmd.SetComputeBufferParam(data.shader, kernel, Ids.PageTable, data.world.pageTable);
            cmd.SetComputeBufferParam(data.shader, kernel, Ids.Bricks, data.world.bricks);
            cmd.SetComputeBufferParam(data.shader, kernel, Ids.Occupancy, data.world.occupancy);
            cmd.SetComputeBufferParam(data.shader, kernel, Ids.WorldSurface, data.world.surface);
            cmd.SetComputeBufferParam(data.shader, kernel, Ids.Distance, data.world.distance);
        }

        static void ResolveMainLight(
            UniversalLightData lights, out Vector3 direction, out Color color, out bool valid)
        {
            direction = Vector3.up;
            color = Color.black;
            valid = false;
            if (lights.mainLightIndex < 0 || lights.mainLightIndex >= lights.visibleLights.Length)
                return;
            VisibleLight light = lights.visibleLights[lights.mainLightIndex];
            if (light.lightType != LightType.Directional)
                return;
            Vector4 forward = light.localToWorldMatrix.GetColumn(2);
            direction = new Vector3(-forward.x, -forward.y, -forward.z).normalized;
            color = light.finalColor;
            valid = true;
        }

        static TextureHandle CreateTexture(
            RenderGraph graph, int width, int height, GraphicsFormat format, string name) =>
            graph.CreateTexture(new TextureDesc(width, height)
            {
                colorFormat = format,
                enableRandomWrite = true,
                clearBuffer = false,
                filterMode = FilterMode.Point,
                name = name
            });

        static int DivRoundUp(int value, int divisor) => (value + divisor - 1) / divisor;
        void EnsureHistory(int width, int height)
        {
            if (historyIrradiance[0] != null && historyWidth == width && historyHeight == height)
                return;
            ReleaseHistory();
            historyWidth = width;
            historyHeight = height;
            for (int i = 0; i < 2; i++)
            {
                historyIrradiance[i] = AllocateHistory(width, height,
                    GraphicsFormat.R16G16B16A16_SFloat, $"GI Irradiance History {i}");
                historyPosition[i] = AllocateHistory(width, height,
                    GraphicsFormat.R32G32B32A32_SFloat, $"GI Position History {i}");
                historyNormal[i] = AllocateHistory(width, height,
                    GraphicsFormat.R16G16B16A16_SFloat, $"GI Normal History {i}");
                historyMoments[i] = AllocateHistory(width, height,
                    GraphicsFormat.R16G16B16A16_SFloat, $"GI Moments History {i}");
                historySpecular[i] = AllocateHistory(width, height,
                    GraphicsFormat.R16G16B16A16_SFloat, $"GI Specular History {i}");
            }
            historyReadIndex = 0;
            frameIndex = 0;
            historyValid = false;
        }

        static RTHandle AllocateHistory(int width, int height, GraphicsFormat format, string name) =>
            RTHandles.Alloc(width, height, 1, DepthBits.None, format, FilterMode.Point,
                TextureWrapMode.Clamp, TextureDimension.Tex2D, true, name: name);

        void ReleaseHistory()
        {
            for (int i = 0; i < 2; i++)
            {
                historyIrradiance[i]?.Release();
                historyPosition[i]?.Release();
                historyNormal[i]?.Release();
                historyMoments[i]?.Release();
                historySpecular[i]?.Release();
                historyIrradiance[i] = null;
                historyPosition[i] = null;
                historyNormal[i] = null;
                historyMoments[i] = null;
                historySpecular[i] = null;
            }
            historyValid = false;
        }

        public void Dispose() => ReleaseHistory();

        static class Ids
        {
            public static readonly int Depth = Shader.PropertyToID("_GIDepthTexture");
            public static readonly int DisplayNormal = Shader.PropertyToID("_GINormalTexture");
            public static readonly int CameraColor = Shader.PropertyToID("_GICameraColorInput");
            public static readonly int SpecularBase = Shader.PropertyToID("_GISpecularBaseInput");
            public static readonly int SpecularMaterial = Shader.PropertyToID("_GISpecularMaterialInput");
            public static readonly int SurfaceRadiance = Shader.PropertyToID("_GIScreenSurfaceInput");
            public static readonly int GeometryNormal = Shader.PropertyToID("_GIGeometryNormal");
            public static readonly int PrimaryPosition = Shader.PropertyToID("_GIProbeReceiverPosition");
            public static readonly int PrimaryNormal = Shader.PropertyToID("_GIProbeReceiverNormal");
            public static readonly int PrimaryPixel = Shader.PropertyToID("_GIProbeReceiverPixel");
            public static readonly int PrimaryIrradiance = Shader.PropertyToID("_GIPrimaryIrradiance");
            public static readonly int PrimarySpecular = Shader.PropertyToID("_GIPrimarySpecular");
            public static readonly int PrimarySpecularInput = Shader.PropertyToID("_GIPrimarySpecularInput");
            public static readonly int PrimarySpecularDebugInput = Shader.PropertyToID("_GIPrimarySpecularDebugInput");
            public static readonly int RawIrradiance = Shader.PropertyToID("_GIRawIrradiance");
            public static readonly int HistoryIrradianceInput = Shader.PropertyToID("_GIHistoryIrradianceInput");
            public static readonly int HistoryPositionInput = Shader.PropertyToID("_GIHistoryPositionInput");
            public static readonly int HistoryNormalInput = Shader.PropertyToID("_GIHistoryNormalInput");
            public static readonly int HistoryMomentsInput = Shader.PropertyToID("_GIHistoryMomentsInput");
            public static readonly int HistoryIrradianceOutput = Shader.PropertyToID("_GIHistoryIrradianceOutput");
            public static readonly int HistoryPositionOutput = Shader.PropertyToID("_GIHistoryPositionOutput");
            public static readonly int HistoryNormalOutput = Shader.PropertyToID("_GIHistoryNormalOutput");
            public static readonly int HistoryMomentsOutput = Shader.PropertyToID("_GIHistoryMomentsOutput");
            public static readonly int SpecularHistoryInput = Shader.PropertyToID("_GISpecularHistoryInput");
            public static readonly int SpecularHistoryOutput = Shader.PropertyToID("_GISpecularHistoryOutput");
            public static readonly int SpecularHistoryOutputInput = Shader.PropertyToID("_GISpecularHistoryOutputInput");
            public static readonly int FilteredProbeIrradiance = Shader.PropertyToID("_GIFilteredProbeIrradiance");
            public static readonly int FilteredSpecular = Shader.PropertyToID("_GIFilteredSpecular");
            public static readonly int FilteredSpecularInput = Shader.PropertyToID("_GIFilteredSpecularInput");
            public static readonly int ResolvedIrradiance = Shader.PropertyToID("_GIResolvedIrradiance");
            public static readonly int ResolvedSpecular = Shader.PropertyToID("_GIResolvedSpecular");
            public static readonly int ResolvedSpecularInput = Shader.PropertyToID("_GIResolvedSpecularInput");
            public static readonly int GeometryNormalInput = Shader.PropertyToID("_GIGeometryNormalInput");
            public static readonly int PrimaryPositionInput = Shader.PropertyToID("_GIProbeReceiverPositionInput");
            public static readonly int PrimaryNormalInput = Shader.PropertyToID("_GIProbeReceiverNormalInput");
            public static readonly int PrimaryPixelInput = Shader.PropertyToID("_GIProbeReceiverPixelInput");
            public static readonly int PrimaryIrradianceInput = Shader.PropertyToID("_GIPrimaryIrradianceInput");
            public static readonly int FilteredProbeIrradianceInput = Shader.PropertyToID("_GIFilteredProbeIrradianceInput");
            public static readonly int Debug = Shader.PropertyToID("_GIProbeDebug");
            public static readonly int InverseViewProjection = Shader.PropertyToID("_GIInverseViewProjection");
            public static readonly int ViewProjection = Shader.PropertyToID("_GIViewProjection");
            public static readonly int PreviousViewProjection = Shader.PropertyToID("_GIPreviousViewProjection");
            public static readonly int CameraPosition = Shader.PropertyToID("_GICameraPosition");
            public static readonly int ScreenSize = Shader.PropertyToID("_GIScreenSize");
            public static readonly int ProbeGridSize = Shader.PropertyToID("_GIProbeGridSize");
            public static readonly int TileSize = Shader.PropertyToID("_GIProbeTileSize");
            public static readonly int DebugMode = Shader.PropertyToID("_GIProbeDebugMode");
            public static readonly int TraceDistance = Shader.PropertyToID("_GITraceDistance");
            public static readonly int MainLightDirection = Shader.PropertyToID("_GIMainLightDirection");
            public static readonly int MainLightColor = Shader.PropertyToID("_GIMainLightColor");
            public static readonly int EnvironmentSkyColor = Shader.PropertyToID("_GIEnvironmentSkyColor");
            public static readonly int EnvironmentIntensity = Shader.PropertyToID("_GIEnvironmentIntensity");
            public static readonly int HasMainLight = Shader.PropertyToID("_GIHasMainLight");
            public static readonly int FrameIndex = Shader.PropertyToID("_GIFrameIndex");
            public static readonly int HistoryValid = Shader.PropertyToID("_GIHistoryValid");
            public static readonly int Levels = Shader.PropertyToID("_GIWorldLevels");
            public static readonly int PageTable = Shader.PropertyToID("_GIWorldPageTable");
            public static readonly int Bricks = Shader.PropertyToID("_GIWorldBricks");
            public static readonly int Occupancy = Shader.PropertyToID("_GIWorldOccupancy");
            public static readonly int WorldSurface = Shader.PropertyToID("_GIWorldSurface");
            public static readonly int Distance = Shader.PropertyToID("_GIWorldDistance");
        }
    }
}
