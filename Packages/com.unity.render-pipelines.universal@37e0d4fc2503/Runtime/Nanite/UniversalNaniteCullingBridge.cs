using System;


namespace UnityEngine.Rendering.Universal
{
    /// <summary>
    /// 可选 Nanite 桥接点：
    /// - 允许项目侧在 URP 主循环同位时序接入自定义两阶段 culling
    /// - 默认关闭，不影响 URP 原有路径
    /// </summary>
    public static class UniversalNaniteCullingBridge
    {
        public static bool enableCoreTimingHook;

        public static Action<UnityEngine.Rendering.RenderGraphModule.RenderGraph, ContextContainer, int> onBeforeOcclusionPass;
        public static Action<UnityEngine.Rendering.RenderGraphModule.RenderGraph, ContextContainer, int> onAfterOccluderUpdate;

        internal static void InvokeBeforeOcclusionPass(UnityEngine.Rendering.RenderGraphModule.RenderGraph renderGraph, ContextContainer frameData, int passIndex)
        {
            if (!enableCoreTimingHook)
                return;
            onBeforeOcclusionPass?.Invoke(renderGraph, frameData, passIndex);
        }

        internal static void InvokeAfterOccluderUpdate(UnityEngine.Rendering.RenderGraphModule.RenderGraph renderGraph, ContextContainer frameData, int passIndex)
        {
            if (!enableCoreTimingHook)
                return;
            onAfterOccluderUpdate?.Invoke(renderGraph, frameData, passIndex);
        }
    }
}
