using System.Collections.Generic;
using UnityEngine;

using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Nanite
{
    /// <summary>
    /// URP 包内同位时序接入（A/B 开关）：
    /// - 开启后通过 UniversalNaniteCullingBridge 在 URP OnMainRendering 同位时机调度 Nanite 选择
    /// - 关闭后回退到 RendererFeature 路径
    /// </summary>
    public static class NaniteUrpCoreTimingDriver
    {
        static bool initialized;
        static bool enabled;
        static bool logStats;

        static readonly Dictionary<int, NaniteRuntimeSelection> firstSelections = new Dictionary<int, NaniteRuntimeSelection>();
        static readonly Dictionary<int, NaniteRuntimeSelection> secondSelections = new Dictionary<int, NaniteRuntimeSelection>();
        static readonly Dictionary<int, NaniteRuntimeSelection> mergedSelections = new Dictionary<int, NaniteRuntimeSelection>();
        static readonly Dictionary<int, List<NaniteVisibleClusterRef>> mergedScratch = new Dictionary<int, List<NaniteVisibleClusterRef>>();
        static readonly Dictionary<int, HashSet<ulong>> mergedKeyScratch = new Dictionary<int, HashSet<ulong>>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        static void Bootstrap()
        {
            EnsureInitialized();
        }

        public static void Configure(bool useCoreTiming, bool enableLog)
        {
            EnsureInitialized();
            enabled = useCoreTiming;
            logStats = enableLog;
            UniversalNaniteCullingBridge.enableCoreTimingHook = enabled;
        }

        static void EnsureInitialized()
        {
            if (initialized)
                return;

            UniversalNaniteCullingBridge.onBeforeOcclusionPass += OnBeforeOcclusionPass;
            UniversalNaniteCullingBridge.onAfterOccluderUpdate += OnAfterOccluderUpdate;
            initialized = true;
        }

        static void OnBeforeOcclusionPass(UnityEngine.Rendering.RenderGraphModule.RenderGraph renderGraph, ContextContainer frameData, int passIndex)
        {
            if (!enabled || passIndex != 0)
                return;

            var camera = frameData.Get<UniversalCameraData>().camera;
            if (camera == null)
                return;

            var proxies = NaniteRuntimeRegistry.ActiveProxies;
            for (int i = 0; i < proxies.Count; i++)
            {
                var proxy = proxies[i];
                if (proxy == null || !proxy.isActiveAndEnabled || proxy.naniteMesh == null)
                    continue;

                var first = GetSelection(firstSelections, proxy.GetInstanceID());
                proxy.TryComputeSelectionForCamera(camera, null, 0, false, first);
            }
        }

        static void OnAfterOccluderUpdate(UnityEngine.Rendering.RenderGraphModule.RenderGraph renderGraph, ContextContainer frameData, int passIndex)
        {
            // 第二次 pass 后做 second-chance 合并，保持保守集合语义。
            if (!enabled || passIndex == 0)
                return;

            var camera = frameData.Get<UniversalCameraData>().camera;
            if (camera == null)
                return;

            var proxies = NaniteRuntimeRegistry.ActiveProxies;
            for (int i = 0; i < proxies.Count; i++)
            {
                var proxy = proxies[i];
                if (proxy == null || !proxy.isActiveAndEnabled || proxy.naniteMesh == null)
                    continue;

                int id = proxy.GetInstanceID();
                var first = GetSelection(firstSelections, id);
                var second = GetSelection(secondSelections, id);
                var merged = GetSelection(mergedSelections, id);

                proxy.TryComputeSelectionForCamera(camera, null, 0, false, second);
                Merge(proxy.naniteMesh, id, first, second, merged);
                merged.instanceLocalToWorld = proxy.transform.localToWorldMatrix;
                proxy.ApplyExternalSelection(merged);

                if (logStats)
                {
                    Debug.Log(
                        $"[Nanite][URP-Core] proxy={proxy.name} first={first.stats.visibleClusters} second={second.stats.visibleClusters} merged={merged.stats.visibleClusters}");
                }
            }
        }

        static NaniteRuntimeSelection GetSelection(Dictionary<int, NaniteRuntimeSelection> dict, int key)
        {
            if (!dict.TryGetValue(key, out var selection))
            {
                selection = new NaniteRuntimeSelection();
                dict[key] = selection;
            }

            return selection;
        }

        static void Merge(
            NaniteMesh mesh,
            int proxyId,
            NaniteRuntimeSelection first,
            NaniteRuntimeSelection second,
            NaniteRuntimeSelection merged)
        {
            merged.Clear();
            if (mesh == null)
                return;

            var visible = GetMergedScratch(proxyId);
            var keys = GetMergedKeyScratch(proxyId);
            visible.Clear();
            keys.Clear();

            for (int i = 0; i < first.visibleClusters.Count; i++)
            {
                var v = first.visibleClusters[i];
                ulong key = ((ulong)(uint)v.pageIndex << 32) | (uint)v.clusterIndex;
                if (keys.Add(key))
                    visible.Add(v);
            }

            for (int i = 0; i < second.visibleClusters.Count; i++)
            {
                var v = second.visibleClusters[i];
                ulong key = ((ulong)(uint)v.pageIndex << 32) | (uint)v.clusterIndex;
                if (keys.Add(key))
                    visible.Add(v);
            }

            var stats = new NaniteCullingStats
            {
                testedInstances = first.stats.testedInstances + second.stats.testedInstances,
                testedNodes = first.stats.testedNodes + second.stats.testedNodes,
                testedParts = first.stats.testedParts + second.stats.testedParts,
                testedClusters = first.stats.testedClusters + second.stats.testedClusters,
                visibleClusters = visible.Count
            };

            NaniteRuntimeCulling.BuildSelectionFromVisibleClusters(mesh, visible, stats, merged);
        }

        static List<NaniteVisibleClusterRef> GetMergedScratch(int proxyId)
        {
            if (!mergedScratch.TryGetValue(proxyId, out var list))
            {
                list = new List<NaniteVisibleClusterRef>(4096);
                mergedScratch[proxyId] = list;
            }
            return list;
        }

        static HashSet<ulong> GetMergedKeyScratch(int proxyId)
        {
            if (!mergedKeyScratch.TryGetValue(proxyId, out var set))
            {
                set = new HashSet<ulong>();
                mergedKeyScratch[proxyId] = set;
            }
            return set;
        }
    }

}
