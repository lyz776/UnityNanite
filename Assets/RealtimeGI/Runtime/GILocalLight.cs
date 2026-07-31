using System.Collections.Generic;
using UnityEngine;

namespace RealtimeGI
{
    /// <summary>Explicit Point/Spot light registration for Surface Radiance injection.</summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Light))]
    public sealed class GILocalLight : MonoBehaviour
    {
        public bool contribute = true;
        public bool castGIShadows = true;
        [Min(0f)] public float indirectMultiplier = 1f;

        Light cachedLight;
        public Light Source
        {
            get
            {
                if (cachedLight == null) cachedLight = GetComponent<Light>();
                return cachedLight;
            }
        }

        public bool IsSupported => contribute && isActiveAndEnabled && Source != null &&
                                   Source.enabled && Source.intensity > 0f && Source.range > 0f &&
                                   (Source.type == LightType.Point || Source.type == LightType.Spot);

        void OnEnable()
        {
            cachedLight = GetComponent<Light>();
            GILocalLightRegistry.Register(this);
        }

        void OnDisable() => GILocalLightRegistry.Unregister(this);
        void OnDestroy() => GILocalLightRegistry.Unregister(this);
        void OnValidate()
        {
            indirectMultiplier = Mathf.Max(0f, indirectMultiplier);
            cachedLight = GetComponent<Light>();
            GILocalLightRegistry.NotifyChanged();
        }
    }

    static class GILocalLightRegistry
    {
        static readonly List<GILocalLight> lights = new List<GILocalLight>(64);
        static int revision = 1;

        public static IReadOnlyList<GILocalLight> Lights => lights;
        public static int Revision => revision;

        public static void Register(GILocalLight light)
        {
            if (light == null || lights.Contains(light))
                return;
            lights.Add(light);
            revision++;
        }

        public static void Unregister(GILocalLight light)
        {
            if (light != null && lights.Remove(light))
                revision++;
        }

        public static void NotifyChanged() => revision++;
    }
}
