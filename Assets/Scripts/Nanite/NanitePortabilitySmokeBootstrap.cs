using System;
using System.IO;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Nanite
{
    /// <summary>
    /// Opt-in Standalone smoke harness. It has no effect in normal Editor or
    /// Player runs; CI passes -nanite-portability-smoke to keep a hidden window
    /// rendering long enough to exercise real DX12 dispatches.
    /// </summary>
    sealed class NanitePortabilitySmokeBootstrap : MonoBehaviour
    {
        const string CommandLineSwitch = "-nanite-portability-smoke";
        const string DollyCommandLineSwitch = "-nanite-lod-dolly-smoke";
        const string SingleDollyCommandLineSwitch = "-nanite-lod-single-smoke";
        const string GeometryDollyCommandLineSwitch = "-nanite-lod-geometry-smoke";
        const string RasterReferenceCommandLineSwitch = "-nanite-lod-raster-reference";
        const string StreamingFlythroughCommandLineSwitch = "-nanite-streaming-flythrough";
        const string StressCommandLineSwitch = "-nanite-stress-smoke";
        const string ForceHybridCommandLineSwitch = "-nanite-force-hybrid";
        const string ForceHardwareCommandLineSwitch = "-nanite-force-hardware";
        const string MaterialStressCommandLineSwitch = "-nanite-material-stress";
        const string AliasMaterialStressCommandLineSwitch = "-nanite-alias-material-stress";
        const string MultiSubMeshStressCommandLineSwitch = "-nanite-multisubmesh-stress";
        const string UniqueMeshStressCommandLineSwitch = "-nanite-unique-mesh-stress";
        const string RasterStressCommandLineSwitch = "-nanite-raster-stress";
        const string OcclusionStackStressCommandLineSwitch = "-nanite-occlusion-stack-stress";
        const string OverlapStressCommandLineSwitch = "-nanite-overlap-stress";
        const string ForceHzbCommandLineSwitch = "-nanite-force-hzb";
        const string HzbMotionCommandLineSwitch = "-nanite-hzb-motion";
        const string AutoAdmissionCommandLineSwitch = "-nanite-auto-admission";
        const string DisableShadowsCommandLineSwitch = "-nanite-disable-shadows";
        const string ShadowQualityCommandLineSwitch = "-nanite-shadow-quality";
        const string OutputArgument = "-nanite-smoke-output";
        const string WidthArgument = "-nanite-smoke-width";
        const string HeightArgument = "-nanite-smoke-height";
        const string StressInstancesArgument = "-nanite-stress-instances";
        const string StressDistanceScaleArgument = "-nanite-stress-distance-scale";
        const string PagePoolMiBArgument = "-nanite-page-pool-mib";
        const string HybridMaxEdgeArgument = "-nanite-hybrid-max-edge";
        const string ShadowLodTexelsArgument = "-nanite-shadow-lod-texels";
        const int DefaultFrames = 300;
        const int StressWarmupFrames = 120;
        const int StressMeasureFrames = 300;
        const int StreamingWarmupFrames = 60;
        const int StreamingNearDwellFrames = 30;
        const int StreamingTravelFrames = 80;
        const int StreamingFarDwellFrames = 30;
        const int StreamingCycleFrames =
            StreamingNearDwellFrames + StreamingTravelFrames +
            StreamingFarDwellFrames + StreamingTravelFrames;
        int remainingFrames = DefaultFrames;
        int elapsedFrames;
        int renderedCameraCount;
        int lastSmokeCameraRenderFrame = -1;
        Camera smokeCamera;
        RenderTexture smokeTarget;
        bool createdSmokeCamera;
        bool dollyCapture;
        bool dollySceneStabilized;
        bool singleDollyCapture;
        bool geometryDollyCapture;
        bool rasterReferenceCapture;
        bool streamingFlythrough;
        bool stressBenchmark;
        bool stressSceneConfigured;
        bool forceHybrid;
        bool materialStress;
        bool aliasMaterialStress;
        bool multiSubMeshStress;
        bool uniqueMeshStress;
        bool rasterStress;
        bool occlusionStackStress;
        bool overlapStress;
        bool forceHzb;
        bool hzbMotion;
        bool autoAdmission;
        bool disableShadows;
        bool shadowQuality;
        int targetWidth = 640;
        int targetHeight = 360;
        int stressInstanceLimit = int.MaxValue;
        float stressDistanceScale = 1f;
        int pagePoolMiBOverride = -1;
        float hybridMaxEdgeOverride = -1f;
        float shadowLodTexelsOverride = -1f;
        readonly System.Diagnostics.Stopwatch benchmarkClock = new System.Diagnostics.Stopwatch();
        int benchmarkFrames;
        NaniteRuntimeTelemetry.Snapshot latestTelemetry;
        bool hasLatestTelemetry;
        int minimumResidentPages = int.MaxValue;
        int maximumResidentPages;
        int maximumRequestedPages;
        int maximumQueuedPages;
        uint maximumPageRequestPriority;
        double streamingStartRealtime = -1.0;
        readonly List<StreamingEndpointSample> streamingEndpointSamples =
            new List<StreamingEndpointSample>(96);
        Vector3 dollyTarget;
        Vector3 dollyDirection;
        Vector3 dollyUp;
        float dollyRadius = 1f;
        Material dollyDiagnosticMaterial;
        readonly List<Material> stressMaterials = new List<Material>();
        readonly List<Texture2D> stressTextures = new List<Texture2D>();
        GameObject shadowQualityReceiver;
        Mesh shadowQualityReceiverMesh;
        Material shadowQualityReceiverMaterial;
        IDisposable aliasFamilyLease;
        string captureDirectory;
        Texture2D captureTexture;
        StreamWriter telemetryWriter;
        long telemetrySequence;
        readonly FrameTiming[] frameTimings = new FrameTiming[4];
        bool frameTimingAvailable;
        double latestGpuFrameMs = -1.0;
        double latestCpuFrameMs = -1.0;
        double latestCpuMainMs = -1.0;
        double latestCpuRenderMs = -1.0;
        float currentSurfaceDistanceR = 0.02f;
        Vector3 hzbMotionBasePosition;
        Vector3 hzbMotionTarget;
        Vector3 hzbMotionRight = Vector3.right;
        float hzbMotionAmplitude;
        readonly Dictionary<int, float> telemetryDistanceByFrame = new Dictionary<int, float>();

        readonly struct StreamingEndpointSample
        {
            internal readonly int cycle;
            internal readonly string endpoint;
            internal readonly int smokeFrame;
            internal readonly uint clusters;
            internal readonly uint triangles;
            internal readonly int residentPages;
            internal readonly int streamedPages;
            internal readonly int evictedPages;

            internal StreamingEndpointSample(
                int cycle,
                string endpoint,
                int smokeFrame,
                in NaniteRuntimeTelemetry.Snapshot sample)
            {
                this.cycle = cycle;
                this.endpoint = endpoint;
                this.smokeFrame = smokeFrame;
                clusters = sample.cameraClusters;
                triangles = sample.cameraTriangles;
                residentPages = sample.residentPages;
                streamedPages = sample.streamedPages;
                evictedPages = sample.evictedPages;
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void InstallIfRequested()
        {
            string[] args = Environment.GetCommandLineArgs();
            bool requested = Array.Exists(
                args,
                arg => string.Equals(arg, CommandLineSwitch, StringComparison.OrdinalIgnoreCase));
            bool dollyRequested = Array.Exists(
                args,
                arg => string.Equals(arg, DollyCommandLineSwitch, StringComparison.OrdinalIgnoreCase));
            bool singleDollyRequested = Array.Exists(
                args,
                arg => string.Equals(arg, SingleDollyCommandLineSwitch, StringComparison.OrdinalIgnoreCase));
            bool geometryDollyRequested = Array.Exists(
                args,
                arg => string.Equals(arg, GeometryDollyCommandLineSwitch, StringComparison.OrdinalIgnoreCase));
            bool rasterReferenceRequested = Array.Exists(
                args,
                arg => string.Equals(arg, RasterReferenceCommandLineSwitch, StringComparison.OrdinalIgnoreCase));
            bool streamingFlythroughRequested = Array.Exists(
                args,
                arg => string.Equals(arg, StreamingFlythroughCommandLineSwitch, StringComparison.OrdinalIgnoreCase));
            bool uniqueMeshStressRequested = Array.Exists(
                args,
                arg => string.Equals(arg, UniqueMeshStressCommandLineSwitch, StringComparison.OrdinalIgnoreCase));
            dollyRequested |= singleDollyRequested ||
                              geometryDollyRequested ||
                              rasterReferenceRequested ||
                              streamingFlythroughRequested;
            bool stressRequested = Array.Exists(
                args,
                arg => string.Equals(arg, StressCommandLineSwitch, StringComparison.OrdinalIgnoreCase));
            if (!requested && !dollyRequested && !stressRequested)
                return;

            Application.runInBackground = true;
            Application.targetFrameRate = -1;
            // Hidden unattended Players have no presentation consumer. Keeping
            // v-sync enabled can stall scene activation before the first smoke
            // frame on some DX12 drivers, so every automation mode owns pacing.
            QualitySettings.vSyncCount = 0;
            var host = new GameObject("Nanite Portability Smoke Bootstrap")
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            DontDestroyOnLoad(host);
            var bootstrap = host.AddComponent<NanitePortabilitySmokeBootstrap>();
            NaniteRuntimeTelemetry.Enabled = true;
            bootstrap.dollyCapture = dollyRequested;
            // A streaming capacity run measures one geometry working set. Leaving
            // hundreds of scene clones active changes the screen-space cut while
            // sharing exactly the same Pages, which obscures the capacity result.
            bootstrap.singleDollyCapture =
                singleDollyRequested ||
                rasterReferenceRequested ||
                (streamingFlythroughRequested && !uniqueMeshStressRequested);
            bootstrap.geometryDollyCapture = geometryDollyRequested;
            bootstrap.rasterReferenceCapture = rasterReferenceRequested;
            bootstrap.streamingFlythrough = streamingFlythroughRequested;
            bootstrap.stressBenchmark = stressRequested;
            bootstrap.forceHybrid = Array.Exists(
                args,
                arg => string.Equals(arg, ForceHybridCommandLineSwitch, StringComparison.OrdinalIgnoreCase));
            bootstrap.materialStress = Array.Exists(
                args,
                arg => string.Equals(arg, MaterialStressCommandLineSwitch, StringComparison.OrdinalIgnoreCase));
            bootstrap.aliasMaterialStress = Array.Exists(
                args,
                arg => string.Equals(arg, AliasMaterialStressCommandLineSwitch, StringComparison.OrdinalIgnoreCase));
            bootstrap.multiSubMeshStress = Array.Exists(
                args,
                arg => string.Equals(arg, MultiSubMeshStressCommandLineSwitch, StringComparison.OrdinalIgnoreCase));
            bootstrap.uniqueMeshStress = uniqueMeshStressRequested;
            bootstrap.rasterStress = Array.Exists(
                args,
                arg => string.Equals(arg, RasterStressCommandLineSwitch, StringComparison.OrdinalIgnoreCase));
            bootstrap.occlusionStackStress = Array.Exists(
                args,
                arg => string.Equals(arg, OcclusionStackStressCommandLineSwitch, StringComparison.OrdinalIgnoreCase));
            bootstrap.overlapStress = Array.Exists(
                args,
                arg => string.Equals(arg, OverlapStressCommandLineSwitch, StringComparison.OrdinalIgnoreCase));
            bootstrap.forceHzb = Array.Exists(
                args,
                arg => string.Equals(arg, ForceHzbCommandLineSwitch, StringComparison.OrdinalIgnoreCase));
            bootstrap.hzbMotion = Array.Exists(
                args,
                arg => string.Equals(arg, HzbMotionCommandLineSwitch, StringComparison.OrdinalIgnoreCase));
            bootstrap.autoAdmission = Array.Exists(
                args,
                arg => string.Equals(arg, AutoAdmissionCommandLineSwitch, StringComparison.OrdinalIgnoreCase));
            bootstrap.disableShadows = Array.Exists(
                args,
                arg => string.Equals(arg, DisableShadowsCommandLineSwitch, StringComparison.OrdinalIgnoreCase));
            bootstrap.shadowQuality = Array.Exists(
                args,
                arg => string.Equals(arg, ShadowQualityCommandLineSwitch, StringComparison.OrdinalIgnoreCase));
            bool forceHardware = Array.Exists(
                args,
                arg => string.Equals(arg, ForceHardwareCommandLineSwitch, StringComparison.OrdinalIgnoreCase));
            if (bootstrap.forceHybrid && forceHardware)
                throw new ArgumentException("Nanite smoke cannot force hardware and hybrid simultaneously.");
            bootstrap.captureDirectory = ResolvePathArgument(args, OutputArgument);
            bootstrap.targetWidth = ResolveIntArgument(args, WidthArgument, stressRequested ? 1280 : 640, 64, 7680);
            bootstrap.targetHeight = ResolveIntArgument(args, HeightArgument, stressRequested ? 720 : 360, 64, 4320);
            bootstrap.stressInstanceLimit = ResolveIntArgument(
                args, StressInstancesArgument, int.MaxValue, 1, 1 << 20);
            bootstrap.stressDistanceScale = ResolveFloatArgument(
                args, StressDistanceScaleArgument, 1f, 0.2f, 8f);
            bootstrap.pagePoolMiBOverride = ResolveIntArgument(
                args, PagePoolMiBArgument, -1, -1, 16384);
            bootstrap.hybridMaxEdgeOverride = ResolveFloatArgument(
                args, HybridMaxEdgeArgument, -1f, -1f, 64f);
            bootstrap.shadowLodTexelsOverride = ResolveFloatArgument(
                args, ShadowLodTexelsArgument, -1f, -1f, 64f);
            if (bootstrap.dollyCapture)
            {
                bootstrap.remainingFrames = bootstrap.streamingFlythrough ? 720 : 180;
                if (string.IsNullOrEmpty(bootstrap.captureDirectory))
                    bootstrap.captureDirectory = Path.Combine(Application.persistentDataPath, "NaniteDollyCapture");
                Directory.CreateDirectory(bootstrap.captureDirectory);
            }
            else if (bootstrap.stressBenchmark)
            {
                bootstrap.remainingFrames = StressWarmupFrames + StressMeasureFrames;
                if (string.IsNullOrEmpty(bootstrap.captureDirectory))
                    bootstrap.captureDirectory = Path.Combine(Application.persistentDataPath, "NaniteStressSmoke");
                Directory.CreateDirectory(bootstrap.captureDirectory);
            }
            Debug.Log(
                bootstrap.dollyCapture
                    ? $"[Nanite][Portability] Deterministic {(bootstrap.streamingFlythrough ? "streaming fly-through" : "LOD dolly")} capture started: {bootstrap.captureDirectory}"
                    : bootstrap.stressBenchmark
                        ? $"[Nanite][Stress] deterministic benchmark started: {bootstrap.targetWidth}x{bootstrap.targetHeight}, " +
                          $"raster={(bootstrap.forceHybrid ? "hybrid" : "hardware")}, " +
                          $"owner={(bootstrap.rasterStress ? "mesh-renderer" : "nanite")}, " +
                          $"instances={bootstrap.stressInstanceLimit}, distanceScale={bootstrap.stressDistanceScale:G4}, " +
                          $"pagePoolMiB={bootstrap.pagePoolMiBOverride}, " +
                          $"shadowLodTexels={bootstrap.shadowLodTexelsOverride:G4}, " +
                          $"shadowQuality={bootstrap.shadowQuality}, output={bootstrap.captureDirectory}."
                        : "[Nanite][Portability] Runtime smoke started; DX12 render paths will run for 300 frames.");
        }

        static string ResolveArgument(string[] args, string key)
        {
            for (int index = 0; index + 1 < args.Length; index++)
            {
                if (string.Equals(args[index], key, StringComparison.OrdinalIgnoreCase))
                    return args[index + 1];
            }
            return null;
        }

        static string ResolvePathArgument(string[] args, string key)
        {
            string value = ResolveArgument(args, key);
            return string.IsNullOrWhiteSpace(value) ? null : Path.GetFullPath(value);
        }

        static int ResolveIntArgument(string[] args, string key, int fallback, int minimum, int maximum)
        {
            string value = ResolveArgument(args, key);
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                ? Mathf.Clamp(parsed, minimum, maximum)
                : fallback;
        }

        static float ResolveFloatArgument(string[] args, string key, float fallback, float minimum, float maximum)
        {
            string value = ResolveArgument(args, key);
            return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
                ? Mathf.Clamp(parsed, minimum, maximum)
                : fallback;
        }

        void OnEnable()
        {
            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
        }

        void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
            ReleaseSmokeTarget();
            telemetryWriter?.Dispose(); telemetryWriter = null;
            for (int index = 0; index < stressMaterials.Count; index++)
                if (stressMaterials[index] != null) Destroy(stressMaterials[index]);
            for (int index = 0; index < stressTextures.Count; index++)
                if (stressTextures[index] != null) Destroy(stressTextures[index]);
            stressMaterials.Clear();
            stressTextures.Clear();
            if (shadowQualityReceiver != null) Destroy(shadowQualityReceiver);
            if (shadowQualityReceiverMesh != null) Destroy(shadowQualityReceiverMesh);
            if (shadowQualityReceiverMaterial != null) Destroy(shadowQualityReceiverMaterial);
            aliasFamilyLease?.Dispose();
            aliasFamilyLease = null;
            NaniteRuntimeTelemetry.Enabled = false;
        }

        void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            renderedCameraCount++;
            if (camera == smokeCamera)
                lastSmokeCameraRenderFrame = Time.frameCount;
        }

        void LateUpdate()
        {
            FrameTimingManager.CaptureFrameTimings();
            EnsureSmokeTarget();
            // A hidden Standalone window can suppress Unity's automatic camera
            // loop even when runInBackground is true. Explicit rendering keeps
            // this opt-in harness a real graphics/dispatch test.
            if (smokeCamera != null)
            {
                // Standalone normally rendered this target earlier in the frame.
                // Render explicitly only when a hidden/background player skipped
                // it; rendering twice in one Time.frameCount aliases the feature's
                // per-camera append queues and is not a valid determinism test.
                if (lastSmokeCameraRenderFrame != Time.frameCount)
                    smokeCamera.Render();
                if (dollyCapture)
                    CaptureDollyFrame();
                else if (stressBenchmark && elapsedFrames == StressWarmupFrames + 30)
                    CaptureStressFrame();
                else if (stressBenchmark && hzbMotion && elapsedFrames == StressWarmupFrames + 31)
                    CaptureStressFrame("stress-frame-next.png");
            }
            UpdateFrameTimingStats();
            CaptureTelemetry();
            if (stressBenchmark && elapsedFrames == StressWarmupFrames)
                benchmarkClock.Restart();
            else if (stressBenchmark && elapsedFrames > StressWarmupFrames)
                benchmarkFrames++;
            elapsedFrames++;
            if (--remainingFrames > 0)
                return;
            if (stressBenchmark)
                WriteBenchmarkSummary();
            if (streamingFlythrough)
                WriteStreamingSummary();
            Debug.Log(
                $"[Nanite][Portability] Runtime smoke completed; " +
                $"cameraRenders={renderedCameraCount}.");
            ReleaseSmokeTarget();
            Application.Quit(0);
        }

        void UpdateFrameTimingStats()
        {
            uint count = FrameTimingManager.GetLatestTimings(
                (uint)frameTimings.Length,
                frameTimings);
            if (count == 0)
                return;

            FrameTiming timing = frameTimings[0];
            latestGpuFrameMs = timing.gpuFrameTime > 0.0 ? timing.gpuFrameTime : -1.0;
            latestCpuFrameMs = timing.cpuFrameTime > 0.0 ? timing.cpuFrameTime : -1.0;
            latestCpuMainMs = timing.cpuMainThreadFrameTime > 0.0
                ? timing.cpuMainThreadFrameTime
                : -1.0;
            latestCpuRenderMs = timing.cpuRenderThreadFrameTime > 0.0
                ? timing.cpuRenderThreadFrameTime
                : -1.0;
            frameTimingAvailable = latestGpuFrameMs > 0.0 || latestCpuFrameMs > 0.0;
        }

        void Update()
        {
            if (stressBenchmark && !stressSceneConfigured)
            {
                EnsureSmokeTarget();
                ConfigureStressScene();
                stressSceneConfigured = true;
            }
            if (stressBenchmark && stressSceneConfigured && occlusionStackStress && hzbMotion && smokeCamera != null)
            {
                float phase = Mathf.Max(0, elapsedFrames - StressWarmupFrames) * 0.047f;
                smokeCamera.transform.position =
                    hzbMotionBasePosition + hzbMotionRight * (Mathf.Sin(phase) * hzbMotionAmplitude);
                smokeCamera.transform.LookAt(hzbMotionTarget, Vector3.up);
            }
            if (!dollyCapture)
                return;

            // Scene isolation must happen before URP records this frame's graph.
            // Mutating hundreds of renderer/proxy objects from LateUpdate after
            // the camera rendered leaves deferred RenderGraph passes holding dead
            // references and makes the supposedly deterministic capture invalid.
            if (!dollySceneStabilized)
            {
                EnsureSmokeTarget();
                dollySceneStabilized = true;
                Light[] lights = FindObjectsByType<Light>(
                    FindObjectsInactive.Include,
                    FindObjectsSortMode.None);
                for (int lightIndex = 0; lightIndex < lights.Length; lightIndex++)
                    lights[lightIndex].shadows = LightShadows.None;
                ConfigureDollySubject();
                return;
            }

            if (smokeCamera == null || elapsedFrames < 60)
                return;

            float t = streamingFlythrough
                ? EvaluateStreamingPosition(elapsedFrames, out _, out _, out _)
                : Mathf.Clamp01((elapsedFrames - 60f) / 80f);
            float surfaceDistance = dollyRadius * Mathf.Exp(Mathf.Lerp(
                Mathf.Log(0.02f),
                Mathf.Log(64f),
                t));
            currentSurfaceDistanceR = surfaceDistance / dollyRadius;
            smokeCamera.transform.position =
                dollyTarget + dollyDirection * (dollyRadius + surfaceDistance) + dollyUp * (dollyRadius * 0.12f);
            smokeCamera.transform.LookAt(dollyTarget, dollyUp);
            telemetryDistanceByFrame[Time.frameCount] = currentSurfaceDistanceR;
        }

        void CaptureTelemetry()
        {
            if (!NaniteRuntimeTelemetry.TryGetLatest(ref telemetrySequence, out var sample)) return;
            latestTelemetry = sample;
            hasLatestTelemetry = true;
            minimumResidentPages = Mathf.Min(minimumResidentPages, sample.residentPages);
            maximumResidentPages = Mathf.Max(maximumResidentPages, sample.residentPages);
            maximumRequestedPages = Mathf.Max(maximumRequestedPages, sample.requestedPages);
            maximumQueuedPages = Mathf.Max(maximumQueuedPages, sample.queuedPages);
            maximumPageRequestPriority = Math.Max(maximumPageRequestPriority, sample.maxPageRequestPriority);
            if (streamingFlythrough)
            {
                if (streamingStartRealtime < 0.0)
                    streamingStartRealtime = Time.realtimeSinceStartupAsDouble;
                EvaluateStreamingPosition(elapsedFrames, out int cycle, out string endpoint, out bool settledEndpoint);
                if (settledEndpoint)
                    streamingEndpointSamples.Add(new StreamingEndpointSample(
                        cycle, endpoint, elapsedFrames, sample));
            }
            if (telemetryWriter == null)
            {
                string directory = !string.IsNullOrEmpty(captureDirectory) ? captureDirectory : Application.persistentDataPath;
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "nanite-telemetry.csv");
                telemetryWriter = new StreamWriter(path, false);
                telemetryWriter.WriteLine("unityFrame,smokeFrame,surfaceDistanceR,width,height,instances,virtualClusters,cameraClusters,cameraTriangles,hardwareClusters,softwareClusters,shadow0Clusters,shadow0Triangles,shadow1Clusters,shadow1Triangles,shadow2Clusters,shadow2Triangles,shadow3Clusters,shadow3Triangles,residentPages,totalPages,pinnedPages,rootPages,requestedPages,queuedPages,maxPageRequestPriority,streamedPages,evictedPages,recycledPageAllocations,retiredPageAllocations,pageStorageBytesRead,pageStorageReadOperations,streamingFilePages,hzbStatsFrame,hzbPass1Drawn,hzbPass1Rejected,hzbPass2Recovered,hzbFinalRejected,passGpuTimingAvailable,formalGpuMs,shadowCullGpuMs,shadowDrawGpuMs,frameTimingAvailable,gpuFrameMs,cpuFrameMs,cpuMainMs,cpuRenderMs");
                Debug.Log($"[Nanite][Telemetry] writing {path}.");
            }
            float distanceR = telemetryDistanceByFrame.TryGetValue(sample.frame, out float exact) ? exact : currentSurfaceDistanceR;
            string F(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
            telemetryWriter.WriteLine(string.Join(",", new[] { sample.frame.ToString(), elapsedFrames.ToString(), F(distanceR),
                sample.width.ToString(), sample.height.ToString(), sample.instanceCount.ToString(), sample.virtualClusterCount.ToString(),
                sample.cameraClusters.ToString(), sample.cameraTriangles.ToString(), sample.hardwareClusters.ToString(), sample.softwareClusters.ToString(),
                sample.shadow0Clusters.ToString(), sample.shadow0Triangles.ToString(), sample.shadow1Clusters.ToString(), sample.shadow1Triangles.ToString(),
                sample.shadow2Clusters.ToString(), sample.shadow2Triangles.ToString(), sample.shadow3Clusters.ToString(), sample.shadow3Triangles.ToString(),
                sample.residentPages.ToString(), sample.totalPages.ToString(), sample.pinnedPages.ToString(), sample.rootPages.ToString(),
                sample.requestedPages.ToString(), sample.queuedPages.ToString(), sample.maxPageRequestPriority.ToString(),
                sample.streamedPages.ToString(), sample.evictedPages.ToString(),
                sample.recycledPageAllocations.ToString(), sample.retiredPageAllocations.ToString(),
                sample.pageStorageBytesRead.ToString(), sample.pageStorageReadOperations.ToString(), sample.streamingFilePages.ToString(),
                sample.hzbStatsFrame.ToString(), sample.hzbPass1Drawn.ToString(), sample.hzbPass1Rejected.ToString(),
                sample.hzbPass2Recovered.ToString(), sample.hzbFinalRejected.ToString(),
                (sample.formalGpuMs >= 0.0 && sample.shadowCullGpuMs >= 0.0 && sample.shadowDrawGpuMs >= 0.0 ? "1" : "0"),
                F(sample.formalGpuMs), F(sample.shadowCullGpuMs), F(sample.shadowDrawGpuMs),
                frameTimingAvailable ? "1" : "0", F(latestGpuFrameMs), F(latestCpuFrameMs),
                F(latestCpuMainMs), F(latestCpuRenderMs) }));
            telemetryWriter.Flush();
            int oldest = Time.frameCount - 64; var stale = new List<int>();
            foreach (var pair in telemetryDistanceByFrame) if (pair.Key < oldest) stale.Add(pair.Key);
            for (int index = 0; index < stale.Count; index++) telemetryDistanceByFrame.Remove(stale[index]);
        }

        void ConfigureStressScene()
        {
            NaniteRuntimeProxy[] proxies = FindObjectsByType<NaniteRuntimeProxy>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None);
            // The checked-in fixture intentionally stays small.  For scale gates,
            // synthesize additional identical instances at startup so 1K/10K
            // tests exercise the real registry and GPU Scene instead of requiring
            // enormous serialized scenes.  This is outside the measured window.
            if (stressInstanceLimit != int.MaxValue &&
                stressInstanceLimit > proxies.Length &&
                proxies.Length > 0)
            {
                GameObject template = proxies[0].gameObject;
                int originalCount = proxies.Length;
                for (int index = originalCount; index < stressInstanceLimit; index++)
                {
                    GameObject clone = Instantiate(template);
                    clone.name = $"Nanite Stress Instance {index:D6}";
                }
                proxies = FindObjectsByType<NaniteRuntimeProxy>(
                    FindObjectsInactive.Exclude,
                    FindObjectsSortMode.None);
                Debug.Log(
                    $"[Nanite][Stress] synthesized instances: {originalCount}->{proxies.Length}.");
            }
            Array.Sort(proxies, (left, right) => string.CompareOrdinal(left.name, right.name));
            int selectedCount = Mathf.Min(proxies.Length, stressInstanceLimit);
            var selected = new List<NaniteRuntimeProxy>(selectedCount);
            for (int index = 0; index < proxies.Length; index++)
            {
                NaniteRuntimeProxy proxy = proxies[index];
                if (index < selectedCount)
                    selected.Add(proxy);
                else if (proxy != null)
                    proxy.gameObject.SetActive(false);
            }
            NaniteMesh multiSubMesh = multiSubMeshStress
                ? Resources.Load<NaniteMesh>("NaniteTests/Toyota4Sub_mesh")
                : null;
            if (multiSubMeshStress && multiSubMesh == null)
                Debug.LogError("[Nanite][MaterialStress] Four-SubMesh Nanite test asset is missing.");
            if (multiSubMesh != null)
            {
                for (int index = 0; index < selected.Count; index++)
                {
                    selected[index].naniteMesh = multiSubMesh;
                    selected[index].MarkRenderDataDirty();
                }
            }
            NaniteMesh[] uniqueMeshes = uniqueMeshStress
                ? Resources.LoadAll<NaniteMesh>("NaniteTests/Unique")
                : Array.Empty<NaniteMesh>();
            if (uniqueMeshStress && uniqueMeshes.Length == 0)
                Debug.LogError(
                    "[Nanite][UniqueMeshStress] No baked assets were found under Resources/NaniteTests/Unique.");
            if (uniqueMeshes.Length > 0)
            {
                Array.Sort(uniqueMeshes, (left, right) => string.CompareOrdinal(left.name, right.name));
                float referenceDiameter = selected.Count > 0 &&
                                          selected[0] != null &&
                                          selected[0].naniteMesh != null &&
                                          selected[0].naniteMesh.sourceMesh != null
                    ? Mathf.Max(0.01f, selected[0].naniteMesh.sourceMesh.bounds.extents.magnitude * 2f)
                    : 1f;
                for (int index = 0; index < selected.Count; index++)
                {
                    NaniteRuntimeProxy proxy = selected[index];
                    NaniteMesh mesh = uniqueMeshes[index % uniqueMeshes.Length];
                    if (proxy == null || mesh == null || mesh.sourceMesh == null)
                        continue;
                    proxy.naniteMesh = mesh;
                    float diameter = Mathf.Max(0.0001f, mesh.sourceMesh.bounds.extents.magnitude * 2f);
                    proxy.transform.localScale = Vector3.one * (referenceDiameter / diameter);
                    proxy.MarkRenderDataDirty();
                }
                Debug.Log(
                    $"[Nanite][UniqueMeshStress] instances={selected.Count}, uniqueMeshes={uniqueMeshes.Length}, " +
                    $"normalizedDiameter={referenceDiameter:G6}.");
            }
            float maximumDiameter = 1f;
            for (int index = 0; index < selected.Count; index++)
            {
                NaniteRuntimeProxy proxy = selected[index];
                Mesh mesh = proxy != null && proxy.naniteMesh != null ? proxy.naniteMesh.sourceMesh : null;
                if (mesh == null)
                    continue;
                Vector3 scale = proxy.transform.lossyScale;
                float maxScale = Mathf.Max(Mathf.Abs(scale.x), Mathf.Max(Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
                maximumDiameter = Mathf.Max(maximumDiameter, mesh.bounds.extents.magnitude * maxScale * 2f);
            }

            int columns = Mathf.Max(1, Mathf.CeilToInt(Mathf.Sqrt(selected.Count * 16f / 9f)));
            int rows = Mathf.Max(1, (selected.Count + columns - 1) / columns);
            float spacing = maximumDiameter * 1.08f;
            const int occlusionColumns = 4;
            const int occlusionRows = 3;
            const int occlusionStacks = occlusionColumns * occlusionRows;
            Vector3 stackCameraOffset = new Vector3(0f, 0.62f, -1f).normalized;
            Vector3 stackForward = -stackCameraOffset;
            Vector3 stackRight = Vector3.right;
            Vector3 stackUp = Vector3.Cross(stackForward, stackRight).normalized;
            float stackHorizontalSpacing = maximumDiameter * 0.44f;
            float stackVerticalSpacing = maximumDiameter * 0.34f;
            float stackDepthSpacing = maximumDiameter * 1.08f;
            Bounds sceneBounds = default;
            bool hasBounds = false;
            Material[] materialVariants = aliasMaterialStress
                ? BuildAliasMaterialVariants()
                : (materialStress ? BuildHeterogeneousMaterialVariants() : null);
            for (int index = 0; index < selected.Count; index++)
            {
                NaniteRuntimeProxy proxy = selected[index];
                if (proxy == null || proxy.naniteMesh == null || proxy.naniteMesh.sourceMesh == null)
                    continue;
                int column = index % columns;
                int row = index / columns;
                int stackIndex = index % occlusionStacks;
                int stackLayer = index / occlusionStacks;
                int stackColumn = stackIndex % occlusionColumns;
                int stackRow = stackIndex / occlusionColumns;
                Vector3 stackScreenOffset =
                    stackRight * ((stackColumn - (occlusionColumns - 1) * 0.5f) * stackHorizontalSpacing) +
                    stackUp * ((stackRow - (occlusionRows - 1) * 0.5f) * stackVerticalSpacing);
                proxy.transform.position = overlapStress
                    ? Vector3.zero
                    : occlusionStackStress
                        ? stackScreenOffset + stackForward * (stackLayer * stackDepthSpacing)
                    : new Vector3(
                        (column - (columns - 1) * 0.5f) * spacing,
                        0f,
                        (row - (rows - 1) * 0.5f) * spacing);
                proxy.forceNaniteRendering = !autoAdmission && !rasterStress;
                proxy.forceRasterRendering = !autoAdmission && rasterStress;
                if (rasterStress)
                    proxy.rasterFallbackMesh = proxy.naniteMesh.sourceMesh;
                Material[] instanceMaterials = null;
                if (materialVariants != null && materialVariants.Length > 0)
                {
                    int materialSlots = Mathf.Max(1, proxy.naniteMesh.subMeshCount);
                    instanceMaterials = new Material[materialSlots];
                    for (int slot = 0; slot < materialSlots; slot++)
                        instanceMaterials[slot] = materialVariants[(index + slot) % materialVariants.Length];
                    proxy.resolveMaterials = instanceMaterials;
                }
                proxy.SetRasterFallbackActive(!autoAdmission && rasterStress);
                if (rasterStress)
                {
                    // The multi-submesh stress asset is assigned to the Nanite proxy at
                    // runtime. Keep the native A/B owner on the exact same source Mesh;
                    // otherwise MeshRenderer silently keeps the original one-submesh
                    // Toyota and the color/material reference is invalid.
                    MeshFilter meshFilter = proxy.GetComponent<MeshFilter>();
                    if (meshFilter != null)
                        meshFilter.sharedMesh = proxy.naniteMesh.sourceMesh;
                    MeshRenderer meshRenderer = proxy.GetComponent<MeshRenderer>();
                    if (meshRenderer != null && instanceMaterials != null)
                        meshRenderer.sharedMaterials = instanceMaterials;
                }
                proxy.MarkRenderDataDirty();
                EncapsulateTransformedBounds(
                    proxy.transform,
                    proxy.naniteMesh.sourceMesh.bounds,
                    ref sceneBounds,
                ref hasBounds);
            }
            if (materialVariants != null)
            {
                Debug.Log(
                    $"[Nanite][MaterialStress] instances={selected.Count}, variants={materialVariants.Length}, " +
                    $"subMeshes={(multiSubMesh != null ? multiSubMesh.subMeshCount : 1)}, " +
                    $"textureBins={stressTextures.Count}, shader=" +
                    (aliasMaterialStress ? "Nanite/Tests/LitAliasSource." : "Universal Render Pipeline/Lit."));
            }

            ConfigureRasterOverride();
            if (!hasBounds || smokeCamera == null)
            {
                Debug.LogError("[Nanite][Stress] no active Nanite geometry was available for framing.");
                return;
            }

            if (shadowQuality)
                CreateShadowQualityReceiver(sceneBounds, maximumDiameter);

            // This is a camera offset, not a forward direction. Positive Y
            // keeps the stress view above the cars so cone culling measures the
            // intended exterior surfaces instead of their undersides.
            Vector3 cameraOffset = stackCameraOffset;
            float verticalHalf = smokeCamera.fieldOfView * Mathf.Deg2Rad * 0.5f;
            float horizontalHalf = Mathf.Atan(Mathf.Tan(verticalHalf) * ((float)targetWidth / targetHeight));
            float limitingHalf = Mathf.Max(0.1f, Mathf.Min(verticalHalf, horizontalHalf));
            float radius = occlusionStackStress
                ? Mathf.Max(
                    1f,
                    new Vector2(
                        (occlusionColumns - 1) * stackHorizontalSpacing * 0.5f,
                        (occlusionRows - 1) * stackVerticalSpacing * 0.5f).magnitude +
                    maximumDiameter * 0.55f)
                : Mathf.Max(1f, sceneBounds.extents.magnitude);
            float distance = radius / Mathf.Sin(limitingHalf) * 1.05f * stressDistanceScale;
            Vector3 cameraTarget = occlusionStackStress ? Vector3.zero : sceneBounds.center;
            smokeCamera.transform.position = cameraTarget + cameraOffset * distance;
            smokeCamera.transform.LookAt(cameraTarget, occlusionStackStress ? stackUp : Vector3.up);
            smokeCamera.nearClipPlane = Mathf.Max(0.05f, distance - radius * 1.5f);
            smokeCamera.farClipPlane = occlusionStackStress
                ? distance + sceneBounds.extents.magnitude * 2f + radius * 4f
                : distance + radius * 4f;
            if (occlusionStackStress)
            {
                hzbMotionBasePosition = smokeCamera.transform.position;
                hzbMotionTarget = cameraTarget;
                hzbMotionRight = stackRight;
                hzbMotionAmplitude = maximumDiameter * 0.22f;
            }
            if (GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset shadowPipeline)
            {
                shadowPipeline.shadowCascadeCount = 4;
                shadowPipeline.shadowDistance = Mathf.Max(
                    shadowPipeline.shadowDistance,
                    distance + radius * 1.25f);
            }

            Plane[] cameraPlanes = GeometryUtility.CalculateFrustumPlanes(smokeCamera);
            int cpuVisibleInstances = 0;
            for (int index = 0; index < selected.Count; index++)
            {
                NaniteRuntimeProxy proxy = selected[index];
                if (proxy == null || proxy.naniteMesh == null || proxy.naniteMesh.sourceMesh == null)
                    continue;
                Bounds worldBounds = default;
                bool hasWorldBounds = false;
                EncapsulateTransformedBounds(
                    proxy.transform,
                    proxy.naniteMesh.sourceMesh.bounds,
                    ref worldBounds,
                    ref hasWorldBounds);
                if (hasWorldBounds && GeometryUtility.TestPlanesAABB(cameraPlanes, worldBounds))
                    cpuVisibleInstances++;
            }

            Light[] lights = FindObjectsByType<Light>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            int shadowDirectionalLights = 0;
            bool configuredQualityLight = false;
            for (int index = 0; index < lights.Length; index++)
            {
                if (lights[index].type != LightType.Directional)
                    continue;
                if (shadowQuality)
                {
                    if (configuredQualityLight)
                    {
                        lights[index].shadows = LightShadows.None;
                        continue;
                    }
                    lights[index].transform.rotation = Quaternion.Euler(48f, -32f, 0f);
                    lights[index].intensity = 1f;
                    configuredQualityLight = true;
                }
                lights[index].shadows = disableShadows ? LightShadows.None : LightShadows.Hard;
                lights[index].shadowStrength = 1f;
                if (!disableShadows)
                    shadowDirectionalLights++;
            }
            Debug.Log(
                $"[Nanite][Stress] scene configured: instances={selected.Count}/{proxies.Length}, " +
                $"layout={(overlapStress ? "overlap" : (occlusionStackStress ? "occlusion-stack" : $"grid:{columns}x{rows}"))}, " +
                $"cpuFrustumVisible={cpuVisibleInstances}, bounds={sceneBounds.size}, " +
                $"cameraDistance={distance:G6}, near={smokeCamera.nearClipPlane:G6}, far={smokeCamera.farClipPlane:G6}, " +
                $"shadowDistance={(GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset activePipeline ? activePipeline.shadowDistance : 0f):G6}, " +
                $"directionalShadowLights={shadowDirectionalLights}, " +
                $"owner={(rasterStress ? "mesh-renderer" : "nanite")}, " +
                $"raster={(forceHybrid ? "hybrid" : "hardware")}.");
        }

        void CreateShadowQualityReceiver(Bounds sceneBounds, float maximumDiameter)
        {
            float padding = Mathf.Max(maximumDiameter * 2f, sceneBounds.extents.magnitude * 0.35f);
            float halfX = Mathf.Max(1f, sceneBounds.extents.x + padding);
            float halfZ = Mathf.Max(1f, sceneBounds.extents.z + padding);
            float receiverY = sceneBounds.min.y - Mathf.Max(0.01f, maximumDiameter * 0.025f);

            shadowQualityReceiver = new GameObject("Nanite Shadow Quality Receiver")
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            shadowQualityReceiver.transform.position = new Vector3(sceneBounds.center.x, receiverY, sceneBounds.center.z);
            shadowQualityReceiverMesh = new Mesh
            {
                name = "Nanite Shadow Quality Receiver Mesh",
                hideFlags = HideFlags.HideAndDontSave,
                vertices = new[]
                {
                    new Vector3(-halfX, 0f, -halfZ),
                    new Vector3( halfX, 0f, -halfZ),
                    new Vector3( halfX, 0f,  halfZ),
                    new Vector3(-halfX, 0f,  halfZ)
                },
                normals = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up },
                uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up },
                triangles = new[] { 0, 2, 1, 0, 3, 2 }
            };
            shadowQualityReceiverMesh.RecalculateBounds();

            MeshFilter filter = shadowQualityReceiver.AddComponent<MeshFilter>();
            filter.sharedMesh = shadowQualityReceiverMesh;
            MeshRenderer renderer = shadowQualityReceiver.AddComponent<MeshRenderer>();
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = true;
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader != null)
            {
                shadowQualityReceiverMaterial = new Material(shader)
                {
                    name = "Nanite Shadow Quality Receiver Material",
                    hideFlags = HideFlags.HideAndDontSave,
                    color = new Color(0.62f, 0.62f, 0.62f, 1f)
                };
                renderer.sharedMaterial = shadowQualityReceiverMaterial;
            }
            Debug.Log(
                $"[Nanite][ShadowQuality] receiver={halfX * 2f:G6}x{halfZ * 2f:G6}, " +
                $"y={receiverY:G6}, casts=False, receives=True.");
        }

        void ConfigureRasterOverride()
        {
            if (!(GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset pipelineAsset))
                return;
            ReadOnlySpan<ScriptableRendererData> rendererData = pipelineAsset.rendererDataList;
            for (int dataIndex = 0; dataIndex < rendererData.Length; dataIndex++)
            {
                ScriptableRendererData data = rendererData[dataIndex];
                if (data == null)
                    continue;
                for (int featureIndex = 0; featureIndex < data.rendererFeatures.Count; featureIndex++)
                {
                    if (!(data.rendererFeatures[featureIndex] is NaniteRendererFeature feature))
                        continue;
                    feature.settings.formalRasterizationMode = forceHybrid ? 1 : 0;
                    feature.ForceHybridForDiagnostics = forceHybrid;
                    feature.DisableShadowHybridForDiagnostics = forceHybrid;
                    feature.settings.enableIndexedClusterRaster = false;
                    feature.settings.enableMaterialTileCulling = materialStress || aliasMaterialStress;
                    if (forceHzb)
                    {
                        feature.settings.useHzbCulling = true;
                        feature.settings.adaptiveHzbCulling = false;
                        feature.settings.usePreviousHzbOnFirstCull = true;
                    }
                    if (pagePoolMiBOverride > 0)
                        feature.settings.pagePoolMaxMiB = pagePoolMiBOverride;
                    if (hybridMaxEdgeOverride > 0f)
                        feature.settings.hybridSoftwareMaxEdgePixels = hybridMaxEdgeOverride;
                    if (shadowLodTexelsOverride > 0f)
                        feature.ShadowLodMinTexelsForDiagnostics = shadowLodTexelsOverride;
                    Debug.Log(
                        $"[Nanite][Stress] runtime override applied: " +
                        $"raster={(forceHybrid ? "hybrid" : "hardware-only")}, " +
                        $"pagePoolMiB={feature.settings.pagePoolMaxMiB}, " +
                        $"shadowLodTexels={feature.ShadowLodMinTexelsForDiagnostics:G4}, hzb={forceHzb}.");
                    return;
                }
            }
            Debug.LogError("[Nanite][Stress] NaniteRendererFeature was not found in the active URP asset.");
        }

        Material[] BuildHeterogeneousMaterialVariants()
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                Debug.LogError("[Nanite][MaterialStress] URP/Lit shader was not found.");
                return Array.Empty<Material>();
            }

            const int variantCount = 8;
            var variants = new Material[variantCount];
            Color[] colors =
            {
                new Color(0.9f, 0.14f, 0.12f), new Color(0.12f, 0.65f, 0.95f),
                new Color(0.16f, 0.82f, 0.32f), new Color(0.95f, 0.68f, 0.12f)
            };
            for (int textureIndex = 0; textureIndex < 4; textureIndex++)
            {
                var texture = new Texture2D(4, 4, TextureFormat.RGBA32, false, true)
                {
                    name = $"Nanite Material Stress Texture {textureIndex}",
                    wrapMode = TextureWrapMode.Repeat,
                    filterMode = FilterMode.Point,
                    hideFlags = HideFlags.HideAndDontSave
                };
                var pixels = new Color[16];
                for (int pixel = 0; pixel < pixels.Length; pixel++)
                    pixels[pixel] = ((pixel + pixel / 4 + textureIndex) & 1) == 0
                        ? colors[textureIndex]
                        : Color.Lerp(colors[textureIndex], Color.white, 0.35f);
                texture.SetPixels(pixels);
                texture.Apply(false, true);
                stressTextures.Add(texture);
            }

            for (int index = 0; index < variantCount; index++)
            {
                var material = new Material(shader)
                {
                    name = $"Nanite Material Stress {index}",
                    hideFlags = HideFlags.HideAndDontSave,
                    enableInstancing = true
                };
                material.SetColor("_BaseColor", index < 4 ? colors[index] : Color.white);
                material.SetFloat("_Metallic", (index & 1) == 0 ? 0.05f : 0.8f);
                material.SetFloat("_Smoothness", 0.18f + 0.1f * index);
                if (index >= 4)
                    material.SetTexture("_BaseMap", stressTextures[index - 4]);
                if ((index & 1) != 0)
                    material.EnableKeyword("_SPECULARHIGHLIGHTS_OFF");
                if ((index & 2) != 0)
                    material.EnableKeyword("_RECEIVE_SHADOWS_OFF");
                variants[index] = material;
                stressMaterials.Add(material);
            }
            return variants;
        }

        Material[] BuildAliasMaterialVariants()
        {
            Shader shader = Resources.Load<Shader>("NaniteLitAliasSource");
            if (shader == null)
            {
                Debug.LogError("[Nanite][AliasMaterialStress] Source shader was not found in Resources.");
                return Array.Empty<Material>();
            }

            aliasFamilyLease ??= NaniteLitAliasResolveFamily.Register(
                shader,
                "Smoke Lit Alias",
                baseMapProperty: "_AlbedoTex",
                baseColorProperty: "_Tint",
                normalMapProperty: "_NormalTex",
                metallicProperty: "_Metalness",
                smoothnessProperty: null,
                roughnessProperty: "_Roughness",
                emissionMapProperty: "_GlowTex",
                emissionColorProperty: "_GlowColor");

            Color[] colors =
            {
                new Color(0.88f, 0.10f, 0.08f), new Color(0.08f, 0.55f, 0.95f),
                new Color(0.10f, 0.78f, 0.24f), new Color(0.95f, 0.62f, 0.08f)
            };
            for (int textureIndex = 0; textureIndex < 4; textureIndex++)
            {
                var texture = new Texture2D(4, 4, TextureFormat.RGBA32, false, true)
                {
                    name = $"Nanite Alias Texture {textureIndex}",
                    wrapMode = TextureWrapMode.Repeat,
                    filterMode = FilterMode.Point,
                    hideFlags = HideFlags.HideAndDontSave
                };
                var pixels = new Color[16];
                for (int pixel = 0; pixel < pixels.Length; pixel++)
                    pixels[pixel] = ((pixel + pixel / 4 + textureIndex) & 1) == 0
                        ? colors[textureIndex]
                        : Color.Lerp(colors[textureIndex], Color.white, 0.28f);
                texture.SetPixels(pixels);
                texture.Apply(false, true);
                stressTextures.Add(texture);
            }

            const int variantCount = 8;
            var variants = new Material[variantCount];
            for (int index = 0; index < variantCount; index++)
            {
                var material = new Material(shader)
                {
                    name = $"Nanite Alias Material {index}",
                    hideFlags = HideFlags.HideAndDontSave,
                    enableInstancing = true
                };
                material.SetTexture("_AlbedoTex", stressTextures[index & 3]);
                material.SetColor("_Tint", index < 4 ? Color.white : colors[index & 3]);
                material.SetFloat("_Metalness", (index & 1) == 0 ? 0.08f : 0.78f);
                material.SetFloat("_Roughness", 0.12f + 0.1f * index);
                variants[index] = material;
                stressMaterials.Add(material);
            }
            Debug.Log(
                $"[Nanite][AliasMaterialStress] registered family=Smoke Lit Alias, " +
                $"materials={variantCount}, textureCompatibilityBins={stressTextures.Count}.");
            return variants;
        }

        static void EncapsulateTransformedBounds(
            Transform transform,
            Bounds localBounds,
            ref Bounds worldBounds,
            ref bool hasBounds)
        {
            Vector3 center = localBounds.center;
            Vector3 extents = localBounds.extents;
            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 local = center + new Vector3(
                    (corner & 1) == 0 ? -extents.x : extents.x,
                    (corner & 2) == 0 ? -extents.y : extents.y,
                    (corner & 4) == 0 ? -extents.z : extents.z);
                Vector3 world = transform.TransformPoint(local);
                if (!hasBounds)
                {
                    worldBounds = new Bounds(world, Vector3.zero);
                    hasBounds = true;
                }
                else
                {
                    worldBounds.Encapsulate(world);
                }
            }
        }

        void WriteBenchmarkSummary()
        {
            benchmarkClock.Stop();
            double seconds = Math.Max(benchmarkClock.Elapsed.TotalSeconds, 1e-6);
            double fps = benchmarkFrames / seconds;
            int admittedNaniteInstances = 0;
            int admittedRasterInstances = 0;
            NaniteRuntimeProxy[] admissionProxies = FindObjectsByType<NaniteRuntimeProxy>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None);
            for (int index = 0; index < admissionProxies.Length; index++)
            {
                if (admissionProxies[index].RasterFallbackActive)
                    admittedRasterInstances++;
                else if (admissionProxies[index].NaniteRenderingActive)
                    admittedNaniteInstances++;
            }
            string path = Path.Combine(captureDirectory, "benchmark-summary.csv");
            using (var writer = new StreamWriter(path, false))
            {
                writer.WriteLine("owner,mode,width,height,distanceScale,pagePoolMiB,warmupFrames,measuredFrames,seconds,fps,averageFrameMs,admittedNaniteInstances,admittedRasterInstances,instances,virtualClusters,cameraClusters,cameraTriangles,hardwareClusters,softwareClusters,shadow0Clusters,shadow1Clusters,shadow2Clusters,shadow3Clusters,residentPages,totalPages,pageStorageBytesRead,pageStorageReadOperations,streamingFilePages,hzbStatsFrame,hzbPass1Drawn,hzbPass1Rejected,hzbPass2Recovered,hzbFinalRejected,passGpuTimingAvailable,formalGpuMs,shadowCullGpuMs,shadowDrawGpuMs,frameTimingAvailable,gpuFrameMs,cpuFrameMs,cpuMainMs,cpuRenderMs");
                NaniteRuntimeTelemetry.Snapshot sample = latestTelemetry;
                string F(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
                writer.WriteLine(string.Join(",", new[]
                {
                    autoAdmission ? "auto" : (rasterStress ? "mesh-renderer" : "nanite"),
                    forceHybrid ? "hybrid" : "hardware", targetWidth.ToString(), targetHeight.ToString(),
                    F(stressDistanceScale), pagePoolMiBOverride.ToString(),
                    StressWarmupFrames.ToString(), benchmarkFrames.ToString(), F(seconds), F(fps), F(seconds * 1000.0 / Math.Max(1, benchmarkFrames)),
                    admittedNaniteInstances.ToString(), admittedRasterInstances.ToString(),
                    (hasLatestTelemetry ? sample.instanceCount : 0).ToString(), (hasLatestTelemetry ? sample.virtualClusterCount : 0).ToString(),
                    (hasLatestTelemetry ? sample.cameraClusters : 0).ToString(), (hasLatestTelemetry ? sample.cameraTriangles : 0).ToString(),
                    (hasLatestTelemetry ? sample.hardwareClusters : 0).ToString(), (hasLatestTelemetry ? sample.softwareClusters : 0).ToString(),
                    (hasLatestTelemetry ? sample.shadow0Clusters : 0).ToString(), (hasLatestTelemetry ? sample.shadow1Clusters : 0).ToString(),
                    (hasLatestTelemetry ? sample.shadow2Clusters : 0).ToString(), (hasLatestTelemetry ? sample.shadow3Clusters : 0).ToString(),
                    (hasLatestTelemetry ? sample.residentPages : 0).ToString(), (hasLatestTelemetry ? sample.totalPages : 0).ToString(),
                    (hasLatestTelemetry ? sample.pageStorageBytesRead : 0L).ToString(),
                    (hasLatestTelemetry ? sample.pageStorageReadOperations : 0).ToString(),
                    (hasLatestTelemetry ? sample.streamingFilePages : 0).ToString(),
                    (hasLatestTelemetry ? sample.hzbStatsFrame : -1).ToString(),
                    (hasLatestTelemetry ? sample.hzbPass1Drawn : 0).ToString(),
                    (hasLatestTelemetry ? sample.hzbPass1Rejected : 0).ToString(),
                    (hasLatestTelemetry ? sample.hzbPass2Recovered : 0).ToString(),
                    (hasLatestTelemetry ? sample.hzbFinalRejected : 0).ToString(),
                    hasLatestTelemetry && sample.formalGpuMs >= 0.0 && sample.shadowCullGpuMs >= 0.0 && sample.shadowDrawGpuMs >= 0.0 ? "1" : "0",
                    F(hasLatestTelemetry ? sample.formalGpuMs : -1.0), F(hasLatestTelemetry ? sample.shadowCullGpuMs : -1.0),
                    F(hasLatestTelemetry ? sample.shadowDrawGpuMs : -1.0), frameTimingAvailable ? "1" : "0",
                    F(latestGpuFrameMs), F(latestCpuFrameMs), F(latestCpuMainMs), F(latestCpuRenderMs)
                }));
            }
            Debug.Log($"[Nanite][Stress] benchmark complete: frames={benchmarkFrames}, seconds={seconds:F3}, " +
                      $"fps={fps:F2}, average={seconds * 1000.0 / Math.Max(1, benchmarkFrames):F3} ms, summary={path}.");
        }

        void CaptureStressFrame(string fileName = "stress-frame.png")
        {
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = smokeTarget;
            captureTexture ??= new Texture2D(
                smokeTarget.width,
                smokeTarget.height,
                TextureFormat.RGBA32,
                false,
                false);
            captureTexture.ReadPixels(
                new Rect(0, 0, smokeTarget.width, smokeTarget.height),
                0,
                0,
                false);
            captureTexture.Apply(false, false);
            RenderTexture.active = previous;
            string path = Path.Combine(captureDirectory, fileName);
            File.WriteAllBytes(path, captureTexture.EncodeToPNG());
            Debug.Log($"[Nanite][Stress] validation frame captured: {path}.");
        }

        void CaptureDollyFrame()
        {
            // The normal dolly keeps its initial fixed pair. Streaming uses the
            // settled tail of each near/far dwell instead: initial loading is not
            // a raster race, and comparing equal endpoints across full cycles is
            // the meaningful residency/cut determinism test.
            if (!dollySceneStabilized)
                return;
            EvaluateStreamingPosition(elapsedFrames, out _, out _, out bool settledEndpoint);
            bool fixedPair = streamingFlythrough
                ? settledEndpoint && IsStreamingEndpointCaptureFrame(elapsedFrames)
                : elapsedFrames >= 40 && elapsedFrames <= 50;
            bool dollySample = streamingFlythrough
                ? elapsedFrames >= 60 && elapsedFrames % 15 == 0
                : elapsedFrames >= 60 && elapsedFrames <= 140 && (elapsedFrames & 1) == 0;
            if (!fixedPair && !dollySample)
                return;

            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = smokeTarget;
            captureTexture ??= new Texture2D(
                smokeTarget.width,
                smokeTarget.height,
                TextureFormat.RGBA32,
                false,
                false);
            captureTexture.ReadPixels(
                new Rect(0, 0, smokeTarget.width, smokeTarget.height),
                0,
                0,
                false);
            captureTexture.Apply(false, false);
            RenderTexture.active = previous;
            string path = Path.Combine(captureDirectory, $"frame-{elapsedFrames:D4}.png");
            File.WriteAllBytes(path, captureTexture.EncodeToPNG());
            Debug.Log($"[Nanite][DollyCapture] frame={elapsedFrames}, path={path}.");
        }

        void WriteStreamingSummary()
        {
            WriteStreamingEndpointSummary();
            string path = Path.Combine(captureDirectory, "streaming-summary.csv");
            double elapsedSeconds = streamingStartRealtime >= 0.0
                ? Math.Max(0.001, Time.realtimeSinceStartupAsDouble - streamingStartRealtime)
                : Math.Max(0.001, elapsedFrames / 60.0);
            ComputeEndpointRanges(
                out uint nearClusterMin, out uint nearClusterMax,
                out uint nearTriangleMin, out uint nearTriangleMax,
                out uint farClusterMin, out uint farClusterMax,
                out uint farTriangleMin, out uint farTriangleMax);
            using (var writer = new StreamWriter(path, false))
            {
                writer.WriteLine(
                    "frames,pagePoolMiB,minResidentPages,maxResidentPages,totalPages," +
                    "pinnedPages,rootPages,maxRequestedPages,maxQueuedPages,maxRequestPriority," +
                    "streamedPages,evictedPages,recycledPageAllocations,retiredPageAllocations," +
                    "pageStorageBytesRead,pageStorageReadOperations,streamingFilePages," +
                    "elapsedSeconds,streamedPagesPerSecond,evictedPagesPerSecond," +
                    "nearClusterMin,nearClusterMax,nearTriangleMin,nearTriangleMax," +
                    "farClusterMin,farClusterMax,farTriangleMin,farTriangleMax");
                NaniteRuntimeTelemetry.Snapshot sample = latestTelemetry;
                writer.WriteLine(string.Join(",", new[]
                {
                    elapsedFrames.ToString(), pagePoolMiBOverride.ToString(),
                    (minimumResidentPages == int.MaxValue ? 0 : minimumResidentPages).ToString(),
                    maximumResidentPages.ToString(),
                    (hasLatestTelemetry ? sample.totalPages : 0).ToString(),
                    (hasLatestTelemetry ? sample.pinnedPages : 0).ToString(),
                    (hasLatestTelemetry ? sample.rootPages : 0).ToString(),
                    maximumRequestedPages.ToString(), maximumQueuedPages.ToString(),
                    maximumPageRequestPriority.ToString(),
                    (hasLatestTelemetry ? sample.streamedPages : 0).ToString(),
                    (hasLatestTelemetry ? sample.evictedPages : 0).ToString(),
                    (hasLatestTelemetry ? sample.recycledPageAllocations : 0).ToString(),
                    (hasLatestTelemetry ? sample.retiredPageAllocations : 0).ToString(),
                    (hasLatestTelemetry ? sample.pageStorageBytesRead : 0L).ToString(),
                    (hasLatestTelemetry ? sample.pageStorageReadOperations : 0).ToString(),
                    (hasLatestTelemetry ? sample.streamingFilePages : 0).ToString(),
                    elapsedSeconds.ToString("0.######", CultureInfo.InvariantCulture),
                    ((hasLatestTelemetry ? sample.streamedPages : 0) / elapsedSeconds).ToString("0.######", CultureInfo.InvariantCulture),
                    ((hasLatestTelemetry ? sample.evictedPages : 0) / elapsedSeconds).ToString("0.######", CultureInfo.InvariantCulture),
                    nearClusterMin.ToString(), nearClusterMax.ToString(),
                    nearTriangleMin.ToString(), nearTriangleMax.ToString(),
                    farClusterMin.ToString(), farClusterMax.ToString(),
                    farTriangleMin.ToString(), farTriangleMax.ToString()
                }));
            }
            Debug.Log(
                $"[Nanite][StreamingAudit] frames={elapsedFrames}, resident=" +
                $"{(minimumResidentPages == int.MaxValue ? 0 : minimumResidentPages)}..{maximumResidentPages}, " +
                $"requests={maximumRequestedPages}/{maximumQueuedPages}, summary={path}.");
        }

        static float EvaluateStreamingPosition(
            int smokeFrame,
            out int cycle,
            out string endpoint,
            out bool settledEndpoint)
        {
            int relative = Mathf.Max(0, smokeFrame - StreamingWarmupFrames);
            cycle = relative / StreamingCycleFrames;
            int phase = relative % StreamingCycleFrames;
            endpoint = string.Empty;
            settledEndpoint = false;
            if (phase < StreamingNearDwellFrames)
            {
                endpoint = "near";
                settledEndpoint = phase >= StreamingNearDwellFrames - 10;
                return 0f;
            }
            phase -= StreamingNearDwellFrames;
            if (phase < StreamingTravelFrames)
                return Mathf.SmoothStep(0f, 1f, phase / (float)(StreamingTravelFrames - 1));
            phase -= StreamingTravelFrames;
            if (phase < StreamingFarDwellFrames)
            {
                endpoint = "far";
                settledEndpoint = phase >= StreamingFarDwellFrames - 10;
                return 1f;
            }
            phase -= StreamingFarDwellFrames;
            return 1f - Mathf.SmoothStep(0f, 1f, phase / (float)(StreamingTravelFrames - 1));
        }

        static bool IsStreamingEndpointCaptureFrame(int smokeFrame)
        {
            int phase = Mathf.Max(0, smokeFrame - StreamingWarmupFrames) % StreamingCycleFrames;
            int nearStart = StreamingNearDwellFrames - 10;
            int farStart = StreamingNearDwellFrames + StreamingTravelFrames + StreamingFarDwellFrames - 10;
            return phase == nearStart || phase == nearStart + 1 ||
                   phase == farStart || phase == farStart + 1;
        }

        void WriteStreamingEndpointSummary()
        {
            string path = Path.Combine(captureDirectory, "streaming-endpoints.csv");
            using var writer = new StreamWriter(path, false);
            writer.WriteLine("cycle,endpoint,smokeFrame,cameraClusters,cameraTriangles,residentPages,streamedPages,evictedPages");
            for (int index = 0; index < streamingEndpointSamples.Count; index++)
            {
                StreamingEndpointSample sample = streamingEndpointSamples[index];
                writer.WriteLine(string.Join(",", new[]
                {
                    sample.cycle.ToString(), sample.endpoint, sample.smokeFrame.ToString(),
                    sample.clusters.ToString(), sample.triangles.ToString(), sample.residentPages.ToString(),
                    sample.streamedPages.ToString(), sample.evictedPages.ToString()
                }));
            }
        }

        void ComputeEndpointRanges(
            out uint nearClusterMin, out uint nearClusterMax,
            out uint nearTriangleMin, out uint nearTriangleMax,
            out uint farClusterMin, out uint farClusterMax,
            out uint farTriangleMin, out uint farTriangleMax)
        {
            nearClusterMin = nearTriangleMin = farClusterMin = farTriangleMin = uint.MaxValue;
            nearClusterMax = nearTriangleMax = farClusterMax = farTriangleMax = 0u;
            for (int index = 0; index < streamingEndpointSamples.Count; index++)
            {
                StreamingEndpointSample sample = streamingEndpointSamples[index];
                if (sample.endpoint == "near")
                {
                    nearClusterMin = Math.Min(nearClusterMin, sample.clusters);
                    nearClusterMax = Math.Max(nearClusterMax, sample.clusters);
                    nearTriangleMin = Math.Min(nearTriangleMin, sample.triangles);
                    nearTriangleMax = Math.Max(nearTriangleMax, sample.triangles);
                }
                else if (sample.endpoint == "far")
                {
                    farClusterMin = Math.Min(farClusterMin, sample.clusters);
                    farClusterMax = Math.Max(farClusterMax, sample.clusters);
                    farTriangleMin = Math.Min(farTriangleMin, sample.triangles);
                    farTriangleMax = Math.Max(farTriangleMax, sample.triangles);
                }
            }
            if (nearClusterMin == uint.MaxValue) nearClusterMin = 0u;
            if (nearTriangleMin == uint.MaxValue) nearTriangleMin = 0u;
            if (farClusterMin == uint.MaxValue) farClusterMin = 0u;
            if (farTriangleMin == uint.MaxValue) farTriangleMin = 0u;
        }

        void ConfigureDollySubject()
        {
            // Reuse the same negotiated path/Page Pool override as the stress
            // benchmark so a dolly capture can validate streaming fallback, not
            // only the all-resident asset.
            ConfigureRasterOverride();
            NaniteRuntimeProxy[] proxies = FindObjectsByType<NaniteRuntimeProxy>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None);
            if (uniqueMeshStress)
            {
                NaniteMesh[] uniqueMeshes = Resources.LoadAll<NaniteMesh>("NaniteTests/Unique");
                Array.Sort(uniqueMeshes, (left, right) => string.CompareOrdinal(left.name, right.name));
                if (uniqueMeshes.Length == 0)
                {
                    Debug.LogError(
                        "[Nanite][UniqueMeshStress] Streaming fly-through has no baked unique meshes.");
                }
                else
                {
                    float referenceDiameter = 1f;
                    for (int index = 0; index < proxies.Length; index++)
                    {
                        if (proxies[index] == null ||
                            proxies[index].naniteMesh == null ||
                            proxies[index].naniteMesh.sourceMesh == null)
                            continue;
                        referenceDiameter = Mathf.Max(
                            0.01f,
                            proxies[index].naniteMesh.sourceMesh.bounds.extents.magnitude * 2f);
                        break;
                    }
                    for (int index = 0; index < proxies.Length; index++)
                    {
                        NaniteRuntimeProxy proxy = proxies[index];
                        NaniteMesh uniqueMesh = uniqueMeshes[index % uniqueMeshes.Length];
                        if (proxy == null || uniqueMesh == null || uniqueMesh.sourceMesh == null)
                            continue;
                        proxy.naniteMesh = uniqueMesh;
                        float diameter = Mathf.Max(
                            0.0001f,
                            uniqueMesh.sourceMesh.bounds.extents.magnitude * 2f);
                        proxy.transform.localScale = Vector3.one * (referenceDiameter / diameter);
                        proxy.MarkRenderDataDirty();
                    }
                    Debug.Log(
                        $"[Nanite][UniqueMeshStress] streaming instances={proxies.Length}, " +
                        $"uniqueMeshes={uniqueMeshes.Length}.");
                }
            }
            NaniteRuntimeProxy subject = null;
            float bestDistanceSq = float.MaxValue;
            for (int index = 0; index < proxies.Length; index++)
            {
                NaniteRuntimeProxy proxy = proxies[index];
                Mesh sourceMesh = proxy != null && proxy.naniteMesh != null
                    ? proxy.naniteMesh.sourceMesh
                    : null;
                if (sourceMesh == null)
                    continue;
                Vector3 center = proxy.transform.TransformPoint(sourceMesh.bounds.center);
                float distanceSq = (center - smokeCamera.transform.position).sqrMagnitude;
                if (distanceSq < bestDistanceSq)
                {
                    bestDistanceSq = distanceSq;
                    subject = proxy;
                }
            }
            if (subject == null)
            {
                Debug.LogWarning("[Nanite][DollyCapture] No Nanite subject with a source Mesh was found.");
                dollyTarget = smokeCamera.transform.position + smokeCamera.transform.forward * 10f;
                dollyDirection = -smokeCamera.transform.forward;
                dollyUp = smokeCamera.transform.up;
                return;
            }

            if (singleDollyCapture)
            {
                for (int index = 0; index < proxies.Length; index++)
                {
                    if (proxies[index] != subject)
                        // Disabling only the component can leave a stale proxy in the
                        // RendererFeature registry until its next rebuild.  Deactivate
                        // the complete object so OnDisable unregisters it before the
                        // next camera render; otherwise a "single car" capture still
                        // exercises every cloned instance in the scene.
                        proxies[index].gameObject.SetActive(false);
                }
            }

            Mesh mesh = subject.naniteMesh.sourceMesh;
            Bounds bounds = mesh.bounds;
            Vector3 scale = subject.transform.lossyScale;
            float maxScale = Mathf.Max(Mathf.Abs(scale.x), Mathf.Max(Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
            dollyTarget = subject.transform.TransformPoint(bounds.center);
            dollyRadius = Mathf.Max(0.01f, bounds.extents.magnitude * maxScale);
            dollyDirection = subject.transform.forward;
            dollyUp = subject.transform.up;

            if (rasterReferenceCapture)
            {
                subject.forceNaniteRendering = false;
                subject.forceRasterRendering = true;
                subject.SetRasterFallbackActive(true);
                subject.MarkRenderDataDirty();
            }
            else
            {
                // A single vehicle may be intentionally admitted to Unity's
                // ordinary MeshRenderer by the production cost model.  This
                // harness is specifically validating the virtual-geometry cut,
                // so make the subject unambiguously Nanite-owned.
                subject.forceNaniteRendering = true;
                subject.forceRasterRendering = false;
                subject.SetRasterFallbackActive(false);
                subject.MarkRenderDataDirty();
            }

            if (geometryDollyCapture && !rasterReferenceCapture)
            {
                Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
                if (shader != null)
                {
                    dollyDiagnosticMaterial = new Material(shader)
                    {
                        name = "Nanite Dolly Geometry Diagnostic",
                        hideFlags = HideFlags.HideAndDontSave
                    };
                    dollyDiagnosticMaterial.SetColor("_BaseColor", new Color(0.72f, 0.72f, 0.72f, 1f));
                    int materialCount = Mathf.Max(1, mesh.subMeshCount);
                    var materials = new Material[materialCount];
                    for (int materialIndex = 0; materialIndex < materialCount; materialIndex++)
                        materials[materialIndex] = dollyDiagnosticMaterial;
                    subject.resolveMaterials = materials;
                    subject.MarkRenderDataDirty();
                }
            }

            smokeCamera.transform.position =
                dollyTarget + dollyDirection * (dollyRadius * 1.02f) + dollyUp * (dollyRadius * 0.12f);
            currentSurfaceDistanceR = 0.02f;
            telemetryDistanceByFrame[Time.frameCount] = currentSurfaceDistanceR;
            smokeCamera.transform.LookAt(dollyTarget, dollyUp);
            Debug.Log(
                $"[Nanite][DollyCapture] subject={subject.name}, single={singleDollyCapture}, " +
                $"geometryMaterial={geometryDollyCapture}, rasterReference={rasterReferenceCapture}, " +
                $"radius={dollyRadius:G6}.");
        }

        void EnsureSmokeTarget()
        {
            if (smokeTarget != null)
                return;
            smokeCamera = Camera.main;
            if (smokeCamera == null)
            {
                var cameraHost = new GameObject("Nanite Portability Smoke Camera")
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                DontDestroyOnLoad(cameraHost);
                smokeCamera = cameraHost.AddComponent<Camera>();
                smokeCamera.transform.position = new Vector3(0f, 2f, -10f);
                smokeCamera.transform.LookAt(Vector3.zero);
                createdSmokeCamera = true;
            }
            smokeTarget = new RenderTexture(targetWidth, targetHeight, 24)
            {
                name = "Nanite Portability Smoke Target",
                hideFlags = HideFlags.HideAndDontSave
            };
            smokeTarget.Create();
            smokeCamera.targetTexture = smokeTarget;
        }

        void ReleaseSmokeTarget()
        {
            if (smokeCamera != null && smokeCamera.targetTexture == smokeTarget)
                smokeCamera.targetTexture = null;
            if (createdSmokeCamera && smokeCamera != null)
                Destroy(smokeCamera.gameObject);
            createdSmokeCamera = false;
            smokeCamera = null;
            if (smokeTarget == null)
                return;
            smokeTarget.Release();
            Destroy(smokeTarget);
            smokeTarget = null;
            if (captureTexture != null)
                Destroy(captureTexture);
            captureTexture = null;
            if (dollyDiagnosticMaterial != null)
                Destroy(dollyDiagnosticMaterial);
            dollyDiagnosticMaterial = null;
        }
    }
}
