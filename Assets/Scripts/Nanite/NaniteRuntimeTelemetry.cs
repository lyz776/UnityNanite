using System;

namespace Nanite
{
    /// <summary>
    /// Opt-in smoke/CI telemetry bridge. Production never enables this class,
    /// so queue reduction and asynchronous readback have zero shipping cost.
    /// </summary>
    public static class NaniteRuntimeTelemetry
    {
        public readonly struct Snapshot
        {
            public readonly int frame;
            public readonly int cameraId;
            public readonly int width;
            public readonly int height;
            public readonly uint cameraClusters;
            public readonly uint cameraTriangles;
            public readonly uint hardwareClusters;
            public readonly uint softwareClusters;
            public readonly uint shadow0Clusters;
            public readonly uint shadow0Triangles;
            public readonly uint shadow1Clusters;
            public readonly uint shadow1Triangles;
            public readonly uint shadow2Clusters;
            public readonly uint shadow2Triangles;
            public readonly uint shadow3Clusters;
            public readonly uint shadow3Triangles;
            public readonly int instanceCount;
            public readonly int virtualClusterCount;
            public readonly int residentPages;
            public readonly int totalPages;
            public readonly int pinnedPages;
            public readonly int rootPages;
            public readonly int requestedPages;
            public readonly int queuedPages;
            public readonly uint maxPageRequestPriority;
            public readonly int streamedPages;
            public readonly int evictedPages;
            public readonly int recycledPageAllocations;
            public readonly int retiredPageAllocations;
            public readonly long pageStorageBytesRead;
            public readonly int pageStorageReadOperations;
            public readonly int streamingFilePages;
            public readonly double formalGpuMs;
            public readonly double shadowCullGpuMs;
            public readonly double shadowDrawGpuMs;
            public readonly int hzbStatsFrame;
            public readonly int hzbPass1Drawn;
            public readonly int hzbPass1Rejected;
            public readonly int hzbPass2Recovered;
            public readonly int hzbFinalRejected;

            public Snapshot(
                int frame,
                int cameraId,
                int width,
                int height,
                uint[] values,
                int instanceCount,
                int virtualClusterCount,
                int residentPages,
                int totalPages,
                int pinnedPages,
                int rootPages,
                int requestedPages,
                int queuedPages,
                uint maxPageRequestPriority,
                int streamedPages,
                int evictedPages,
                int recycledPageAllocations,
                int retiredPageAllocations,
                long pageStorageBytesRead,
                int pageStorageReadOperations,
                int streamingFilePages,
                double formalGpuMs,
                double shadowCullGpuMs,
                double shadowDrawGpuMs,
                int hzbStatsFrame,
                int hzbPass1Drawn,
                int hzbPass1Rejected,
                int hzbPass2Recovered)
            {
                this.frame = frame;
                this.cameraId = cameraId;
                this.width = width;
                this.height = height;
                cameraClusters = Value(values, 0);
                cameraTriangles = Value(values, 1);
                hardwareClusters = Value(values, 2);
                softwareClusters = Value(values, 3);
                shadow0Clusters = Value(values, 4);
                shadow0Triangles = Value(values, 5);
                shadow1Clusters = Value(values, 6);
                shadow1Triangles = Value(values, 7);
                shadow2Clusters = Value(values, 8);
                shadow2Triangles = Value(values, 9);
                shadow3Clusters = Value(values, 10);
                shadow3Triangles = Value(values, 11);
                this.instanceCount = instanceCount;
                this.virtualClusterCount = virtualClusterCount;
                this.residentPages = residentPages;
                this.totalPages = totalPages;
                this.pinnedPages = pinnedPages;
                this.rootPages = rootPages;
                this.requestedPages = requestedPages;
                this.queuedPages = queuedPages;
                this.maxPageRequestPriority = maxPageRequestPriority;
                this.streamedPages = streamedPages;
                this.evictedPages = evictedPages;
                this.recycledPageAllocations = recycledPageAllocations;
                this.retiredPageAllocations = retiredPageAllocations;
                this.pageStorageBytesRead = pageStorageBytesRead;
                this.pageStorageReadOperations = pageStorageReadOperations;
                this.streamingFilePages = streamingFilePages;
                this.formalGpuMs = formalGpuMs;
                this.shadowCullGpuMs = shadowCullGpuMs;
                this.shadowDrawGpuMs = shadowDrawGpuMs;
                this.hzbStatsFrame = hzbStatsFrame;
                this.hzbPass1Drawn = Math.Max(0, hzbPass1Drawn);
                this.hzbPass1Rejected = Math.Max(0, hzbPass1Rejected);
                this.hzbPass2Recovered = Math.Max(0, hzbPass2Recovered);
                hzbFinalRejected = Math.Max(0, this.hzbPass1Rejected - this.hzbPass2Recovered);
            }

            static uint Value(uint[] values, int index) =>
                values != null && (uint)index < (uint)values.Length ? values[index] : 0u;
        }

        static readonly object Gate = new object();
        static Snapshot latest;
        static long sequence;

        public static bool Enabled { get; set; }

        internal static void Publish(in Snapshot snapshot)
        {
            if (!Enabled)
                return;
            lock (Gate)
            {
                latest = snapshot;
                sequence++;
            }
        }

        public static bool TryGetLatest(ref long lastSequence, out Snapshot snapshot)
        {
            lock (Gate)
            {
                if (sequence == 0 || sequence == lastSequence)
                {
                    snapshot = default;
                    return false;
                }
                lastSequence = sequence;
                snapshot = latest;
                return true;
            }
        }
    }
}
