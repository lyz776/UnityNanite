using System.Collections.Generic;
using UnityEngine;

namespace Nanite
{
    /// <summary>跟踪场景内激活的 NaniteRuntimeProxy，供渲染管线 Pass 调度。</summary>
    public static class NaniteRuntimeRegistry
    {
        static readonly List<NaniteRuntimeProxy> proxies = new List<NaniteRuntimeProxy>(64);
        static int featureDrivenFrame = -100000;
        static int revision = 1;

        public static IReadOnlyList<NaniteRuntimeProxy> ActiveProxies => proxies;
        public static bool IsFeatureDriving => featureDrivenFrame >= (Time.frameCount - 1);
        /// <summary>Only changes when proxy membership or immutable render data changes.</summary>
        public static int Revision => revision;

        public static void MarkFeatureDrivenFrame()
        {
            featureDrivenFrame = Time.frameCount;
        }

        public static void Register(NaniteRuntimeProxy proxy)
        {
            if (proxy == null || proxies.Contains(proxy))
                return;
            proxies.Add(proxy);
            revision++;
        }

        public static void Unregister(NaniteRuntimeProxy proxy)
        {
            if (proxy == null)
                return;
            if (proxies.Remove(proxy))
                revision++;
        }

        /// <summary>
        /// Notify scene backends that mesh/material bindings changed. Transforms do not need this;
        /// they are streamed through the instance buffer.
        /// </summary>
        public static void NotifyRenderDataChanged(NaniteRuntimeProxy proxy)
        {
            if (proxy != null && proxies.Contains(proxy))
                revision++;
        }
    }
}
