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
        [SerializeField] float diffuseQueryMs;
        [SerializeField] float glossyQueryMs;
        [SerializeField] float measuredSubtotalMs;

        [Header("Capacity (read-only)")]
        [SerializeField] int staticBricks;
        [SerializeField] int dynamicBricks;
        [SerializeField] int pendingRadianceBricks;
        [SerializeField] float poolMiB;

        Recorder clipmapRecorder;
        Recorder distanceRecorder;
        Recorder radianceRecorder;
        Recorder diffuseQueryRecorder;
        Recorder glossyQueryRecorder;

        void OnEnable()
        {
            clipmapRecorder = Enable("RealtimeGI/Clipmap Total");
            distanceRecorder = Enable("RealtimeGI/Distance Propagation");
            radianceRecorder = Enable("RealtimeGI/Radiance Update");
            diffuseQueryRecorder = Enable("RealtimeGI/World Diffuse Query");
            glossyQueryRecorder = Enable("RealtimeGI/World Glossy Query");
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
            diffuseQueryMs = Smooth(diffuseQueryMs, GpuMs(diffuseQueryRecorder));
            glossyQueryMs = Smooth(glossyQueryMs, GpuMs(glossyQueryRecorder));
            measuredSubtotalMs = clipmapTotalMs + diffuseQueryMs + glossyQueryMs;


            GIClipmapSystem clipmaps = GIClipmapSystem.Active;
            if (clipmaps == null)
                return;
            staticBricks = clipmaps.StaticBrickCount;
            dynamicBricks = clipmaps.DynamicBrickCount;
            pendingRadianceBricks = clipmaps.PendingRadianceBrickCount;
            poolMiB = clipmaps.EstimatedPoolMiB;
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
            Disable(diffuseQueryRecorder);
            Disable(glossyQueryRecorder);
        }

        static void Disable(Recorder recorder)
        {
            if (recorder != null) recorder.enabled = false;
        }
    }
}
