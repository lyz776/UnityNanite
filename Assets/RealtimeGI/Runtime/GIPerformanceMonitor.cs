using UnityEngine;
using UnityEngine.Profiling;

namespace RealtimeGI
{
    /// <summary>
    /// Lightweight Development Player/Editor view over GPU profiler markers. A value of zero
    /// means the active graphics backend or profiler connection did not expose GPU timestamps.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public sealed class GIPerformanceMonitor : MonoBehaviour
    {
        [Range(0.01f, 1f)] public float smoothing = 0.15f;

        [Header("GPU milliseconds (read-only)")]
        [SerializeField] float clipmapTotalMs;
        [SerializeField] float distancePropagationMs;
        [SerializeField] float radianceUpdateMs;
        [SerializeField] float diffuseTraceMs;
        [SerializeField] float specularTraceMs;
        [SerializeField] float specularFilterMs;
        [SerializeField] float upsampleMs;
        [SerializeField] float measuredSubtotalMs;

        [Header("Capacity (read-only)")]
        [SerializeField] int staticBricks;
        [SerializeField] int dynamicBricks;
        [SerializeField] int pendingRadianceBricks;
        [SerializeField] float poolMiB;
        [SerializeField] uint tracedRays;
        [SerializeField] float screenHitPercent;
        [SerializeField] float averageWorldSteps;
        [SerializeField] float validRadianceCacheHitPercent;
        [SerializeField] uint invalidRadianceCacheHits;

        Recorder clipmapRecorder;
        Recorder distanceRecorder;
        Recorder radianceRecorder;
        Recorder diffuseRecorder;
        Recorder specularRecorder;
        Recorder reuseRecorder;
        Recorder temporalRecorder;
        Recorder spatialRecorder;
        Recorder upsampleRecorder;

        void OnEnable()
        {
            clipmapRecorder = Enable("RealtimeGI/Clipmap Total");
            distanceRecorder = Enable("RealtimeGI/Distance Propagation");
            radianceRecorder = Enable("RealtimeGI/Radiance Update");
            diffuseRecorder = Enable("RealtimeGI/Diffuse Trace");
            specularRecorder = Enable("RealtimeGI/Specular Trace");
            reuseRecorder = Enable("RealtimeGI/Specular Reuse");
            temporalRecorder = Enable("RealtimeGI/Specular Temporal");
            spatialRecorder = Enable("RealtimeGI/Specular Spatial");
            upsampleRecorder = Enable("RealtimeGI/Upsample");
        }

        static Recorder Enable(string marker)
        {
            Recorder recorder = Recorder.Get(marker);
            if (recorder != null) recorder.enabled = true;
            return recorder;
        }

        void LateUpdate()
        {
            clipmapTotalMs = Smooth(clipmapTotalMs, GpuMs(clipmapRecorder));
            distancePropagationMs = Smooth(distancePropagationMs, GpuMs(distanceRecorder));
            radianceUpdateMs = Smooth(radianceUpdateMs, GpuMs(radianceRecorder));
            diffuseTraceMs = Smooth(diffuseTraceMs, GpuMs(diffuseRecorder));
            specularTraceMs = Smooth(specularTraceMs, GpuMs(specularRecorder));
            float filter = GpuMs(reuseRecorder) + GpuMs(temporalRecorder) + GpuMs(spatialRecorder);
            specularFilterMs = Smooth(specularFilterMs, filter);
            upsampleMs = Smooth(upsampleMs, GpuMs(upsampleRecorder));
            measuredSubtotalMs = clipmapTotalMs + diffuseTraceMs + specularTraceMs +
                                 specularFilterMs + upsampleMs;

            GIClipmapSystem clipmaps = GIClipmapSystem.Active;
            if (clipmaps == null)
                return;
            staticBricks = clipmaps.StaticBrickCount;
            dynamicBricks = clipmaps.DynamicBrickCount;
            pendingRadianceBricks = clipmaps.PendingRadianceBrickCount;
            poolMiB = clipmaps.EstimatedPoolMiB;
            RealtimeGIRendererFeature.TraceCounterSnapshot counters =
                RealtimeGIRendererFeature.LatestTraceCounters;
            tracedRays = counters.rays;
            screenHitPercent = counters.rays > 0 ? counters.screenHits * 100f / counters.rays : 0f;
            averageWorldSteps = counters.AverageWorldSteps;
            validRadianceCacheHitPercent = counters.ValidCachePercent;
            invalidRadianceCacheHits = counters.invalidCacheHits;
        }

        float Smooth(float previous, float next)
        {
            if (next <= 0f)
                return previous;
            return previous <= 0f ? next : Mathf.Lerp(previous, next, smoothing);
        }

        static float GpuMs(Recorder recorder) => recorder != null && recorder.enabled
            ? (float)(recorder.gpuElapsedNanoseconds * 1e-6)
            : 0f;

        void OnDisable()
        {
            Disable(clipmapRecorder);
            Disable(distanceRecorder);
            Disable(radianceRecorder);
            Disable(diffuseRecorder);
            Disable(specularRecorder);
            Disable(reuseRecorder);
            Disable(temporalRecorder);
            Disable(spatialRecorder);
            Disable(upsampleRecorder);
        }

        static void Disable(Recorder recorder)
        {
            if (recorder != null) recorder.enabled = false;
        }
    }
}
