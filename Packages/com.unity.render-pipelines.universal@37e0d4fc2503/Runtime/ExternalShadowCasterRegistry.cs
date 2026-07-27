using System;
using System.Collections.Generic;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule.Util;

namespace UnityEngine.Rendering.Universal
{
    /// <summary>
    /// Compute state supplied before the main-light shadow atlas raster pass. External
    /// renderers use it to build a cascade-local GPU draw queue without a CPU readback.
    /// </summary>
    public readonly struct ExternalMainLightShadowCullContext
    {
        public readonly UnsafeCommandBuffer commandBuffer;
        public readonly Camera camera;
        public readonly VisibleLight shadowLight;
        public readonly int cascadeIndex;
        public readonly int cascadeCount;
        public readonly Matrix4x4 viewMatrix;
        public readonly Matrix4x4 projectionMatrix;
        public readonly Vector4 cullingSphere;
        public readonly int shadowResolution;

        internal ExternalMainLightShadowCullContext(
            UnsafeCommandBuffer commandBuffer,
            Camera camera,
            VisibleLight shadowLight,
            int cascadeIndex,
            int cascadeCount,
            Matrix4x4 viewMatrix,
            Matrix4x4 projectionMatrix,
            Vector4 cullingSphere,
            int shadowResolution)
        {
            this.commandBuffer = commandBuffer;
            this.camera = camera;
            this.shadowLight = shadowLight;
            this.cascadeIndex = cascadeIndex;
            this.cascadeCount = cascadeCount;
            this.viewMatrix = viewMatrix;
            this.projectionMatrix = projectionMatrix;
            this.cullingSphere = cullingSphere;
            this.shadowResolution = shadowResolution;
        }
    }

    /// <summary>
    /// All main-light cascade slices supplied in one callback. GPU-driven renderers can
    /// build independent per-cascade queues with one fused dispatch instead of paying the
    /// managed callback and dispatch setup cost once per cascade.
    /// </summary>
    public readonly struct ExternalMainLightShadowCullBatchContext
    {
        public readonly UnsafeCommandBuffer commandBuffer;
        public readonly Camera camera;
        public readonly VisibleLight shadowLight;
        public readonly ShadowSliceData[] cascadeSlices;
        public readonly int cascadeCount;

        internal ExternalMainLightShadowCullBatchContext(
            UnsafeCommandBuffer commandBuffer,
            Camera camera,
            VisibleLight shadowLight,
            ShadowSliceData[] cascadeSlices,
            int cascadeCount)
        {
            this.commandBuffer = commandBuffer;
            this.camera = camera;
            this.shadowLight = shadowLight;
            this.cascadeSlices = cascadeSlices;
            this.cascadeCount = cascadeCount;
        }
    }

    /// <summary>
    /// State supplied while URP has the main-light shadow atlas bound for a cascade.
    /// </summary>
    public readonly struct ExternalMainLightShadowDrawContext
    {
        public readonly RasterCommandBuffer commandBuffer;
        public readonly Camera camera;
        public readonly VisibleLight shadowLight;
        public readonly int cascadeIndex;
        public readonly int cascadeCount;
        public readonly Matrix4x4 viewMatrix;
        public readonly Matrix4x4 projectionMatrix;
        public readonly Vector4 shadowBias;
        public readonly Rect viewport;
        public readonly int atlasWidth;
        public readonly int atlasHeight;

        internal ExternalMainLightShadowDrawContext(
            RasterCommandBuffer commandBuffer,
            Camera camera,
            VisibleLight shadowLight,
            int cascadeIndex,
            int cascadeCount,
            Matrix4x4 viewMatrix,
            Matrix4x4 projectionMatrix,
            Vector4 shadowBias,
            Rect viewport,
            int atlasWidth,
            int atlasHeight)
        {
            this.commandBuffer = commandBuffer;
            this.camera = camera;
            this.shadowLight = shadowLight;
            this.cascadeIndex = cascadeIndex;
            this.cascadeCount = cascadeCount;
            this.viewMatrix = viewMatrix;
            this.projectionMatrix = projectionMatrix;
            this.shadowBias = shadowBias;
            this.viewport = viewport;
            this.atlasWidth = atlasWidth;
            this.atlasHeight = atlasHeight;
        }
    }

    /// <summary>
    /// Allows GPU-driven renderer features whose geometry is absent from CullingResults to
    /// keep the URP main-light shadow atlas alive and append draws while that atlas is bound.
    /// </summary>
    public static class ExternalShadowCasterRegistry
    {
        static readonly List<Func<Camera, Light, bool>> s_MainLightProviders =
            new List<Func<Camera, Light, bool>>(4);
        static readonly List<Action<ExternalMainLightShadowCullContext>> s_MainLightCullProviders =
            new List<Action<ExternalMainLightShadowCullContext>>(4);
        static readonly List<Action<ExternalMainLightShadowCullBatchContext>> s_MainLightCullBatchProviders =
            new List<Action<ExternalMainLightShadowCullBatchContext>>(4);
        static readonly List<Action<ExternalMainLightShadowDrawContext>> s_MainLightDrawProviders =
            new List<Action<ExternalMainLightShadowDrawContext>>(4);

        public static void RegisterMainLightProvider(Func<Camera, Light, bool> provider)
        {
            if (provider == null || s_MainLightProviders.Contains(provider))
                return;
            s_MainLightProviders.Add(provider);
        }

        public static void UnregisterMainLightProvider(Func<Camera, Light, bool> provider)
        {
            if (provider != null)
                s_MainLightProviders.Remove(provider);
        }

        public static void RegisterMainLightDrawProvider(Action<ExternalMainLightShadowDrawContext> provider)
        {
            if (provider == null || s_MainLightDrawProviders.Contains(provider))
                return;
            s_MainLightDrawProviders.Add(provider);
        }

        public static void RegisterMainLightCullProvider(Action<ExternalMainLightShadowCullContext> provider)
        {
            if (provider == null || s_MainLightCullProviders.Contains(provider))
                return;
            s_MainLightCullProviders.Add(provider);
        }

        public static void RegisterMainLightCullBatchProvider(Action<ExternalMainLightShadowCullBatchContext> provider)
        {
            if (provider == null || s_MainLightCullBatchProviders.Contains(provider))
                return;
            s_MainLightCullBatchProviders.Add(provider);
        }

        public static void UnregisterMainLightCullProvider(Action<ExternalMainLightShadowCullContext> provider)
        {
            if (provider != null)
                s_MainLightCullProviders.Remove(provider);
        }

        public static void UnregisterMainLightCullBatchProvider(Action<ExternalMainLightShadowCullBatchContext> provider)
        {
            if (provider != null)
                s_MainLightCullBatchProviders.Remove(provider);
        }

        public static void UnregisterMainLightDrawProvider(Action<ExternalMainLightShadowDrawContext> provider)
        {
            if (provider != null)
                s_MainLightDrawProviders.Remove(provider);
        }

        internal static bool HasMainLightShadowCasters(Camera camera, Light light)
        {
            for (int i = s_MainLightProviders.Count - 1; i >= 0; i--)
            {
                Func<Camera, Light, bool> provider = s_MainLightProviders[i];
                if (provider == null)
                {
                    s_MainLightProviders.RemoveAt(i);
                    continue;
                }

                try
                {
                    if (provider(camera, light))
                        return true;
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                }
            }

            return false;
        }

        internal static void DrawMainLightShadowCasters(ExternalMainLightShadowDrawContext context)
        {
            for (int i = s_MainLightDrawProviders.Count - 1; i >= 0; i--)
            {
                Action<ExternalMainLightShadowDrawContext> provider = s_MainLightDrawProviders[i];
                if (provider == null)
                {
                    s_MainLightDrawProviders.RemoveAt(i);
                    continue;
                }

                try
                {
                    provider(context);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                }
            }
        }

        internal static void CullMainLightShadowCasters(ExternalMainLightShadowCullContext context)
        {
            for (int i = s_MainLightCullProviders.Count - 1; i >= 0; i--)
            {
                Action<ExternalMainLightShadowCullContext> provider = s_MainLightCullProviders[i];
                if (provider == null)
                {
                    s_MainLightCullProviders.RemoveAt(i);
                    continue;
                }

                try
                {
                    provider(context);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                }
            }
        }

        internal static void CullMainLightShadowCastersBatch(ExternalMainLightShadowCullBatchContext context)
        {
            for (int i = s_MainLightCullBatchProviders.Count - 1; i >= 0; i--)
            {
                Action<ExternalMainLightShadowCullBatchContext> provider = s_MainLightCullBatchProviders[i];
                if (provider == null)
                {
                    s_MainLightCullBatchProviders.RemoveAt(i);
                    continue;
                }

                try
                {
                    provider(context);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                }
            }
        }
    }
}
