using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Profiling;
using UnityEditorInternal;
using UnityEngine;

namespace Nanite.Editor
{
    // Temporary evidence exporter. It is inert unless Temp/CodexProfilerExport.trigger exists.
    [InitializeOnLoad]
    static class CodexProfilerCaptureExporter
    {
        readonly struct MarkerKey : IEquatable<MarkerKey>
        {
            public readonly string thread;
            public readonly string marker;

            public MarkerKey(string thread, string marker)
            {
                this.thread = thread;
                this.marker = marker;
            }

            public bool Equals(MarkerKey other) => thread == other.thread && marker == other.marker;
            public override bool Equals(object obj) => obj is MarkerKey other && Equals(other);
            public override int GetHashCode() => unchecked((thread?.GetHashCode() ?? 0) * 397 ^ (marker?.GetHashCode() ?? 0));
        }

        sealed class FrameMarker
        {
            public double inclusive;
            public double self;
            public int samples;
        }

        sealed class MarkerAggregate
        {
            public readonly MarkerKey key;
            public readonly List<double> positiveFrameInclusive = new List<double>();
            public readonly List<double> positiveFrameSelf = new List<double>();
            public double totalInclusive;
            public double totalSelf;
            public int samples;
            public int frames;

            public MarkerAggregate(MarkerKey key) => this.key = key;
        }

        static readonly string TriggerPath = Path.GetFullPath(
            Path.Combine(Application.dataPath, "../Temp/CodexProfilerExport.trigger"));
        static string capturePath;
        static bool running;

        static CodexProfilerCaptureExporter() => EditorApplication.delayCall += TryStart;

        static void TryStart()
        {
            if (running || !File.Exists(TriggerPath))
                return;

            try
            {
                capturePath = File.ReadAllText(TriggerPath).Trim();
                File.Delete(TriggerPath);
                if (!Path.IsPathRooted(capturePath))
                    capturePath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", capturePath));
                if (!File.Exists(capturePath))
                    throw new FileNotFoundException("Profiler capture not found.", capturePath);

                running = true;
                ProfilerDriver.profileLoaded += OnProfileLoaded;
                if (!ProfilerDriver.LoadProfile(capturePath, false))
                    throw new InvalidDataException("ProfilerDriver.LoadProfile returned false.");
            }
            catch (Exception ex)
            {
                ProfilerDriver.profileLoaded -= OnProfileLoaded;
                running = false;
                Debug.LogError($"[CodexProfilerExport] Start failed: {ex}");
            }
        }

        static void OnProfileLoaded()
        {
            ProfilerDriver.profileLoaded -= OnProfileLoaded;
            EditorApplication.delayCall += Export;
        }

        static void Export()
        {
            try
            {
                int first = ProfilerDriver.firstFrameIndex;
                int last = ProfilerDriver.lastFrameIndex;
                if (first < 0 || last < first)
                    throw new InvalidDataException($"Capture has no frames ({first}..{last}).");

                var aggregates = new Dictionary<MarkerKey, MarkerAggregate>();
                var cpuFrames = new List<double>();
                var gpuFrames = new List<double>();
                var frameIds = new List<int>();

                int frame = first;
                while (frame >= 0 && frame <= last)
                {
                    var frameMarkers = new Dictionary<MarkerKey, FrameMarker>();
                    double cpuMs = 0.0;
                    double gpuMs = 0.0;
                    bool gotFrameTiming = false;

                    for (int threadIndex = 0; threadIndex < 512; threadIndex++)
                    {
                        using (RawFrameDataView view = ProfilerDriver.GetRawFrameDataView(frame, threadIndex))
                        {
                            if (!view.valid)
                                break;

                            string threadName = string.IsNullOrEmpty(view.threadName)
                                ? $"Thread {threadIndex}"
                                : view.threadName;
                            string thread = string.IsNullOrEmpty(view.threadGroupName)
                                ? threadName
                                : $"{view.threadGroupName}/{threadName}";

                            if (!gotFrameTiming || threadName == "Main Thread")
                            {
                                cpuMs = view.frameTimeMs;
                                gpuMs = view.frameGpuTimeMs;
                                gotFrameTiming = true;
                            }

                            int sampleIndex = 0;
                            while (sampleIndex < view.sampleCount)
                                AccumulateSampleTree(view, ref sampleIndex, thread, frameMarkers);
                        }
                    }

                    foreach (KeyValuePair<MarkerKey, FrameMarker> pair in frameMarkers)
                    {
                        if (!aggregates.TryGetValue(pair.Key, out MarkerAggregate aggregate))
                        {
                            aggregate = new MarkerAggregate(pair.Key);
                            aggregates.Add(pair.Key, aggregate);
                        }

                        FrameMarker value = pair.Value;
                        aggregate.totalInclusive += value.inclusive;
                        aggregate.totalSelf += value.self;
                        aggregate.samples += value.samples;
                        aggregate.frames++;
                        if (value.inclusive > 0.0)
                            aggregate.positiveFrameInclusive.Add(value.inclusive);
                        if (value.self > 0.0)
                            aggregate.positiveFrameSelf.Add(value.self);
                    }

                    frameIds.Add(frame);
                    cpuFrames.Add(cpuMs);
                    gpuFrames.Add(gpuMs);
                    if (frame == last)
                        break;
                    int next = ProfilerDriver.GetNextFrameIndex(frame);
                    if (next <= frame)
                        break;
                    frame = next;
                }

                string prefix = capturePath + ".codex";
                WriteFrames(prefix + ".frames.csv", frameIds, cpuFrames, gpuFrames);
                WriteMarkers(prefix + ".markers.csv", aggregates.Values, frameIds.Count);
                WriteSummary(prefix + ".summary.txt", capturePath, first, last, cpuFrames, gpuFrames, aggregates.Values);
                Debug.Log($"[CodexProfilerExport] Complete: frames={frameIds.Count}, output={prefix}.summary.txt");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CodexProfilerExport] Export failed: {ex}");
            }
            finally
            {
                running = false;
            }
        }

        static double AccumulateSampleTree(
            RawFrameDataView view,
            ref int sampleIndex,
            string thread,
            Dictionary<MarkerKey, FrameMarker> frameMarkers)
        {
            int current = sampleIndex++;
            double inclusive = Math.Max(0.0, view.GetSampleTimeMs(current));
            int childCount = Math.Max(0, view.GetSampleChildrenCount(current));
            double directChildren = 0.0;
            for (int child = 0; child < childCount && sampleIndex < view.sampleCount; child++)
                directChildren += AccumulateSampleTree(view, ref sampleIndex, thread, frameMarkers);

            string marker = view.GetSampleName(current);
            if (!string.IsNullOrEmpty(marker))
            {
                var key = new MarkerKey(thread, marker);
                if (!frameMarkers.TryGetValue(key, out FrameMarker value))
                {
                    value = new FrameMarker();
                    frameMarkers.Add(key, value);
                }
                value.inclusive += inclusive;
                value.self += Math.Max(0.0, inclusive - directChildren);
                value.samples++;
            }
            return inclusive;
        }

        static void WriteFrames(string path, List<int> frameIds, List<double> cpu, List<double> gpu)
        {
            var text = new StringBuilder(frameIds.Count * 32);
            text.AppendLine("frame,cpu_ms,gpu_ms");
            for (int i = 0; i < frameIds.Count; i++)
                text.Append(frameIds[i]).Append(',').Append(F(cpu[i])).Append(',').Append(F(gpu[i])).AppendLine();
            File.WriteAllText(path, text.ToString(), Encoding.UTF8);
        }

        static void WriteMarkers(string path, IEnumerable<MarkerAggregate> source, int frameCount)
        {
            var rows = source.OrderByDescending(value => value.totalSelf).ToArray();
            var text = new StringBuilder(rows.Length * 96);
            text.AppendLine("thread,marker,samples,frames,total_inclusive_ms,avg_inclusive_ms,total_self_ms,avg_self_ms,p50_self_ms,p95_self_ms,max_self_ms");
            foreach (MarkerAggregate row in rows)
            {
                text.Append(Csv(row.key.thread)).Append(',')
                    .Append(Csv(row.key.marker)).Append(',')
                    .Append(row.samples).Append(',')
                    .Append(row.frames).Append(',')
                    .Append(F(row.totalInclusive)).Append(',')
                    .Append(F(frameCount > 0 ? row.totalInclusive / frameCount : 0.0)).Append(',')
                    .Append(F(row.totalSelf)).Append(',')
                    .Append(F(frameCount > 0 ? row.totalSelf / frameCount : 0.0)).Append(',')
                    .Append(F(PercentileWithImplicitZeros(row.positiveFrameSelf, frameCount, 0.50))).Append(',')
                    .Append(F(PercentileWithImplicitZeros(row.positiveFrameSelf, frameCount, 0.95))).Append(',')
                    .Append(F(row.positiveFrameSelf.Count > 0 ? row.positiveFrameSelf.Max() : 0.0))
                    .AppendLine();
            }
            File.WriteAllText(path, text.ToString(), Encoding.UTF8);
        }

        static void WriteSummary(
            string path,
            string capture,
            int first,
            int last,
            List<double> cpu,
            List<double> gpu,
            IEnumerable<MarkerAggregate> source)
        {
            int frameCount = cpu.Count;
            var all = source.ToArray();
            var text = new StringBuilder(16384);
            text.AppendLine($"capture={capture}");
            text.AppendLine($"frames={frameCount} range={first}..{last}");
            text.AppendLine($"cpu ms p50={F(Percentile(cpu, 0.50))} p95={F(Percentile(cpu, 0.95))} avg={F(cpu.Count > 0 ? cpu.Average() : 0.0)} max={F(cpu.Count > 0 ? cpu.Max() : 0.0)}");
            text.AppendLine($"gpu ms p50={F(Percentile(gpu, 0.50))} p95={F(Percentile(gpu, 0.95))} avg={F(gpu.Count > 0 ? gpu.Average() : 0.0)} max={F(gpu.Count > 0 ? gpu.Max() : 0.0)}");
            text.AppendLine();

            foreach (IGrouping<string, MarkerAggregate> thread in all
                         .GroupBy(value => value.key.thread)
                         .OrderByDescending(group => group.Sum(value => value.totalSelf)))
            {
                text.AppendLine($"[{thread.Key}] top self markers");
                foreach (MarkerAggregate row in thread.OrderByDescending(value => value.totalSelf).Take(30))
                {
                    text.Append(F(frameCount > 0 ? row.totalSelf / frameCount : 0.0)).Append(" ms avg | ")
                        .Append(F(PercentileWithImplicitZeros(row.positiveFrameSelf, frameCount, 0.95))).Append(" ms p95 | ")
                        .Append(F(frameCount > 0 ? row.totalInclusive / frameCount : 0.0)).Append(" ms incl | ")
                        .AppendLine(row.key.marker);
                }
                text.AppendLine();
            }

            text.AppendLine("[Nanite and render evidence]");
            foreach (MarkerAggregate row in all
                         .Where(value =>
                             value.key.marker.StartsWith("Nanite", StringComparison.Ordinal) ||
                             value.key.marker.IndexOf("Shadow", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             value.key.marker.IndexOf("WaitForGPU", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             value.key.marker.IndexOf("FinishRendering", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             value.key.marker.IndexOf("ExecuteCommandList", StringComparison.OrdinalIgnoreCase) >= 0)
                         .OrderByDescending(value => value.totalInclusive))
            {
                text.Append(row.key.thread).Append(" | ")
                    .Append(F(frameCount > 0 ? row.totalSelf / frameCount : 0.0)).Append(" self | ")
                    .Append(F(frameCount > 0 ? row.totalInclusive / frameCount : 0.0)).Append(" incl | ")
                    .AppendLine(row.key.marker);
            }
            File.WriteAllText(path, text.ToString(), Encoding.UTF8);
        }

        static double Percentile(List<double> source, double percentile)
        {
            if (source == null || source.Count == 0)
                return 0.0;
            double[] values = source.OrderBy(value => value).ToArray();
            int index = Mathf.Clamp((int)Math.Ceiling(percentile * values.Length) - 1, 0, values.Length - 1);
            return values[index];
        }

        static double PercentileWithImplicitZeros(List<double> positives, int totalCount, double percentile)
        {
            if (totalCount <= 0 || positives == null || positives.Count == 0)
                return 0.0;
            int rank = Mathf.Clamp((int)Math.Ceiling(percentile * totalCount) - 1, 0, totalCount - 1);
            int zeroCount = Math.Max(0, totalCount - positives.Count);
            if (rank < zeroCount)
                return 0.0;
            double[] values = positives.OrderBy(value => value).ToArray();
            return values[Mathf.Clamp(rank - zeroCount, 0, values.Length - 1)];
        }

        static string F(double value) => value.ToString("0.000000", CultureInfo.InvariantCulture);

        static string Csv(string value) =>
            value == null ? string.Empty : $"\"{value.Replace("\"", "\"\"")}\"";
    }
}
