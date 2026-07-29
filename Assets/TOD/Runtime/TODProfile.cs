using System;
using UnityEngine;

namespace UnityNanite.TOD
{
    [Serializable]
    public sealed class TODFloatParameter
    {
        public bool useCurve;
        public float constant;
        public AnimationCurve curve = AnimationCurve.Linear(0f, 0f, 24f, 0f);

        public TODFloatParameter() { }

        public TODFloatParameter(float value, bool animated = false, AnimationCurve defaultCurve = null)
        {
            constant = value;
            useCurve = animated;
            curve = defaultCurve ?? AnimationCurve.Linear(0f, value, 24f, value);
        }

        public float Evaluate(float hour)
        {
            return useCurve && curve != null ? curve.Evaluate(TODProfile.WrapHour(hour)) : constant;
        }
    }

    [Serializable]
    public sealed class TODColorParameter
    {
        public bool useGradient;
        [ColorUsage(true, true)] public Color color = Color.white;
        [GradientUsage(true, ColorSpace.Linear)] public Gradient gradient = new Gradient();

        public TODColorParameter() { }

        public TODColorParameter(Color value, bool animated = false, Gradient defaultGradient = null)
        {
            color = value;
            useGradient = animated;
            gradient = defaultGradient ?? TODProfile.ConstantGradient(value);
        }

        public Color Evaluate(float hour)
        {
            return useGradient && gradient != null
                ? gradient.Evaluate(TODProfile.WrapHour(hour) / 24f)
                : color;
        }
    }

    [Serializable]
    public sealed class TODSkySettings
    {
        public TODColorParameter lightBottom = new TODColorParameter(
            new Color(0.42f, 0.62f, 0.88f),
            true,
            TODProfile.DayGradient(
                new Color(0.006f, 0.009f, 0.025f),
                new Color(0.95f, 0.34f, 0.12f),
                new Color(0.42f, 0.62f, 0.88f)));

        public TODColorParameter lightMiddle = new TODColorParameter(
            new Color(0.22f, 0.52f, 0.95f),
            true,
            TODProfile.DayGradient(
                new Color(0.004f, 0.008f, 0.03f),
                new Color(0.48f, 0.18f, 0.28f),
                new Color(0.22f, 0.52f, 0.95f)));

        public TODColorParameter lightTop = new TODColorParameter(
            new Color(0.06f, 0.25f, 0.72f),
            true,
            TODProfile.DayGradient(
                new Color(0.002f, 0.004f, 0.018f),
                new Color(0.08f, 0.08f, 0.22f),
                new Color(0.06f, 0.25f, 0.72f)));

        public TODColorParameter horizonColor = new TODColorParameter(
            new Color(0.58f, 0.76f, 1f),
            true,
            TODProfile.DayGradient(
                new Color(0.012f, 0.018f, 0.05f),
                new Color(1.35f, 0.18f, 0.035f),
                new Color(0.58f, 0.76f, 1f)));

        public TODFloatParameter middleHeight = new TODFloatParameter(0.35f);
        public TODFloatParameter horizonWidth = new TODFloatParameter(0.12f);
        public TODFloatParameter horizonIntensity = new TODFloatParameter(1f);
        public TODFloatParameter exposure = new TODFloatParameter(1f);
        public TODColorParameter groundColor = new TODColorParameter(new Color(0.015f, 0.018f, 0.025f));

        public TODColorParameter artisticTint = new TODColorParameter(Color.white);
        public TODFloatParameter saturation = new TODFloatParameter(1f);
        public TODFloatParameter contrast = new TODFloatParameter(1f);
        public TODFloatParameter horizonSunGlow = new TODFloatParameter(0.8f);
        public TODFloatParameter horizonSunGlowPower = new TODFloatParameter(6f);
        public TODColorParameter sunScatterColor = new TODColorParameter(new Color(1.35f, 0.26f, 0.045f));
        public TODFloatParameter sunScatterIntensity = new TODFloatParameter(0.65f);
        public TODFloatParameter sunScatterPower = new TODFloatParameter(12f);
    }

    [Serializable]
    public sealed class TODStarsSettings
    {
        public bool enabled = true;
        public TODColorParameter color = new TODColorParameter(new Color(0.62f, 0.72f, 1.2f));
        public TODFloatParameter intensity = new TODFloatParameter(
            1f,
            true,
            new AnimationCurve(
                new Keyframe(0f, 1.2f), new Keyframe(5f, 1f), new Keyframe(7f, 0f),
                new Keyframe(17.5f, 0f), new Keyframe(19f, 1f), new Keyframe(24f, 1.2f)));
        public TODFloatParameter density = new TODFloatParameter(0.965f);
        public TODFloatParameter size = new TODFloatParameter(0.18f);
        public TODFloatParameter twinkle = new TODFloatParameter(0.35f);
        public TODFloatParameter twinkleSpeed = new TODFloatParameter(1.4f);
        public TODFloatParameter horizonFade = new TODFloatParameter(0.08f);
        public TODFloatParameter rotation = new TODFloatParameter(0f);
    }

    [Serializable]
    public sealed class TODCloudSettings
    {
        public bool enabled = true;
        public TODColorParameter color = new TODColorParameter(
            Color.white,
            true,
            TODProfile.DayGradient(
                new Color(0.08f, 0.1f, 0.18f),
                new Color(1.1f, 0.32f, 0.16f),
                new Color(0.92f, 0.96f, 1f)));
        public TODColorParameter shadowColor = new TODColorParameter(new Color(0.2f, 0.27f, 0.38f));
        public TODFloatParameter opacity = new TODFloatParameter(0.35f);
        public TODFloatParameter coverage = new TODFloatParameter(0.5f);
        public TODFloatParameter scale = new TODFloatParameter(2.2f);
        public TODFloatParameter softness = new TODFloatParameter(0.18f);
        public TODFloatParameter speedX = new TODFloatParameter(0.006f);
        public TODFloatParameter speedY = new TODFloatParameter(0.002f);
        public TODFloatParameter horizonFade = new TODFloatParameter(0.12f);
        public TODFloatParameter sunLighting = new TODFloatParameter(0.7f);
    }

    [Serializable]
    public sealed class TODSunSettings
    {
        public TODColorParameter color = new TODColorParameter(
            new Color(1f, 0.95f, 0.84f),
            true,
            TODProfile.DayGradient(
                new Color(0.12f, 0.12f, 0.18f),
                new Color(1.5f, 0.22f, 0.025f),
                new Color(1f, 0.95f, 0.84f)));

        public TODFloatParameter intensity = new TODFloatParameter(
            1.2f,
            true,
            new AnimationCurve(
                new Keyframe(0f, 0f), new Keyframe(5.5f, 0f),
                new Keyframe(7f, 0.6f), new Keyframe(12f, 1.2f),
                new Keyframe(17f, 0.7f), new Keyframe(18.5f, 0f), new Keyframe(24f, 0f)));

        public TODFloatParameter diskSize = new TODFloatParameter(0.012f);
        public TODFloatParameter diskSoftness = new TODFloatParameter(0.003f);
        public TODColorParameter haloColor = new TODColorParameter(new Color(1.2f, 0.42f, 0.08f));
        public TODFloatParameter haloSize = new TODFloatParameter(0.12f);
        public TODFloatParameter haloIntensity = new TODFloatParameter(0.4f);
    }

    [Serializable]
    public sealed class TODMoonSettings
    {
        public TODColorParameter color = new TODColorParameter(new Color(0.62f, 0.72f, 1f));
        public TODFloatParameter intensity = new TODFloatParameter(
            0.25f,
            true,
            new AnimationCurve(
                new Keyframe(0f, 0.3f), new Keyframe(5f, 0.28f), new Keyframe(7f, 0f),
                new Keyframe(17.5f, 0f), new Keyframe(19f, 0.25f), new Keyframe(24f, 0.3f)));

        public TODFloatParameter diskSize = new TODFloatParameter(0.018f);
        public TODFloatParameter diskSoftness = new TODFloatParameter(0.002f);
        public TODColorParameter haloColor = new TODColorParameter(new Color(0.18f, 0.3f, 0.75f));
        public TODFloatParameter haloSize = new TODFloatParameter(0.1f);
        public TODFloatParameter haloIntensity = new TODFloatParameter(0.25f);
        public TODFloatParameter phase = new TODFloatParameter(0.72f);
    }

    [Serializable]
    public sealed class TODLightingSettings
    {
        public TODColorParameter mainLightColor = new TODColorParameter(
            new Color(1f, 0.94f, 0.82f),
            true,
            TODProfile.DayGradient(
                new Color(0.24f, 0.31f, 0.58f),
                new Color(1.4f, 0.28f, 0.08f),
                new Color(1f, 0.94f, 0.82f)));

        public TODFloatParameter mainLightIntensity = new TODFloatParameter(
            1.25f,
            true,
            new AnimationCurve(
                new Keyframe(0f, 0.08f), new Keyframe(5.5f, 0.05f), new Keyframe(7f, 0.55f),
                new Keyframe(12f, 1.25f), new Keyframe(17f, 0.6f), new Keyframe(18.5f, 0.08f),
                new Keyframe(24f, 0.08f)));

        public TODFloatParameter shadowStrength = new TODFloatParameter(1f);
        public TODColorParameter ambientSky = new TODColorParameter(
            new Color(0.3f, 0.45f, 0.68f),
            true,
            TODProfile.DayGradient(
                new Color(0.008f, 0.012f, 0.035f),
                new Color(0.35f, 0.12f, 0.12f),
                new Color(0.3f, 0.45f, 0.68f)));
        public TODColorParameter ambientEquator = new TODColorParameter(new Color(0.2f, 0.25f, 0.32f));
        public TODColorParameter ambientGround = new TODColorParameter(new Color(0.055f, 0.05f, 0.045f));
        public TODFloatParameter ambientIntensity = new TODFloatParameter(1f);
    }

    [Serializable]
    public sealed class TODFogSettings
    {
        public bool enabled = true;
        public TODColorParameter color = new TODColorParameter(
            new Color(0.5f, 0.62f, 0.72f),
            true,
            TODProfile.DayGradient(
                new Color(0.008f, 0.012f, 0.025f),
                new Color(0.65f, 0.16f, 0.08f),
                new Color(0.5f, 0.62f, 0.72f)));
        public TODFloatParameter startDistance = new TODFloatParameter(20f);
        public TODFloatParameter endDistance = new TODFloatParameter(600f);
        public TODFloatParameter density = new TODFloatParameter(1f);

        public bool heightFogEnabled = true;
        public TODColorParameter heightColor = new TODColorParameter(new Color(0.42f, 0.52f, 0.58f));
        public TODFloatParameter baseHeight = new TODFloatParameter(0f);
        public TODFloatParameter heightRange = new TODFloatParameter(60f);
        public TODFloatParameter heightDensity = new TODFloatParameter(0.7f);
        public TODFloatParameter heightStartDistance = new TODFloatParameter(0f);
        public TODFloatParameter heightEndDistance = new TODFloatParameter(500f);
    }

    [CreateAssetMenu(fileName = "TOD Profile", menuName = "Unity Nanite/TOD/Profile")]
    public sealed class TODProfile : ScriptableObject
    {
        public TODSkySettings sky = new TODSkySettings();
        public TODStarsSettings stars = new TODStarsSettings();
        public TODCloudSettings clouds = new TODCloudSettings();
        public TODSunSettings sun = new TODSunSettings();
        public TODMoonSettings moon = new TODMoonSettings();
        public TODLightingSettings lighting = new TODLightingSettings();
        public TODFogSettings fog = new TODFogSettings();

        [Header("Procedural Celestial Orbit (not keyframed)")]
        [Range(0f, 360f)] public float orbitAzimuth = 0f;
        [Range(-89f, 89f)] public float orbitTilt = 23.5f;
        [Range(0f, 24f)] public float solarNoon = 12f;
        [Range(-45f, 45f)] public float moonOrbitOffset = 5f;
        [Min(1f)] public float gizmoRadius = 25f;
        [Range(0f, 0.25f)] public float twilightWidth = 0.06f;

        public static float WrapHour(float hour)
        {
            hour %= 24f;
            return hour < 0f ? hour + 24f : hour;
        }

        public static Gradient ConstantGradient(Color value)
        {
            var gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(value, 0f), new GradientColorKey(value, 1f) },
                new[] { new GradientAlphaKey(value.a, 0f), new GradientAlphaKey(value.a, 1f) });
            return gradient;
        }

        public static Gradient DayGradient(Color night, Color sunrise, Color day)
        {
            var gradient = new Gradient { mode = GradientMode.Blend };
            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(night, 0f),
                    new GradientColorKey(night, 0.20f),
                    new GradientColorKey(sunrise, 0.27f),
                    new GradientColorKey(day, 0.42f),
                    new GradientColorKey(day, 0.65f),
                    new GradientColorKey(sunrise, 0.75f),
                    new GradientColorKey(night, 0.82f),
                    new GradientColorKey(night, 1f)
                },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
            return gradient;
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            UnityEditor.EditorApplication.delayCall += () =>
            {
                if (this != null)
                    TODController.RefreshAll(this);
            };
        }
#endif
    }
}
