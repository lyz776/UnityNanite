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
            new Color(0.48f, 0.68f, 0.92f),
            true,
            TODProfile.DayGradient(
                new Color(0.006f, 0.01f, 0.028f),
                new Color(0.52f, 0.20f, 0.16f),
                new Color(0.48f, 0.68f, 0.92f)));

        public TODColorParameter lightMiddle = new TODColorParameter(
            new Color(0.25f, 0.52f, 0.88f),
            true,
            TODProfile.DayGradient(
                new Color(0.008f, 0.015f, 0.045f),
                new Color(0.35f, 0.22f, 0.38f),
                new Color(0.25f, 0.52f, 0.88f)));

        public TODColorParameter lightTop = new TODColorParameter(
            new Color(0.08f, 0.30f, 0.70f),
            true,
            TODProfile.DayGradient(
                new Color(0.003f, 0.008f, 0.03f),
                new Color(0.08f, 0.10f, 0.25f),
                new Color(0.08f, 0.30f, 0.70f)));

        public TODColorParameter horizonColor = new TODColorParameter(
            new Color(0.68f, 0.78f, 0.92f),
            true,
            TODProfile.DayGradient(
                new Color(0.025f, 0.04f, 0.085f),
                new Color(0.95f, 0.38f, 0.18f),
                new Color(0.68f, 0.78f, 0.92f)));

        public TODFloatParameter middleHeight = new TODFloatParameter(0.35f);
        public TODFloatParameter horizonWidth = new TODFloatParameter(0.085f);
        public TODFloatParameter horizonIntensity = new TODFloatParameter(0.68f);
        public TODFloatParameter exposure = new TODFloatParameter(1f);
        public TODColorParameter groundColor = new TODColorParameter(
            new Color(0.08f, 0.12f, 0.16f), true,
            TODProfile.DayGradient(
                new Color(0.008f, 0.012f, 0.022f),
                new Color(0.12f, 0.065f, 0.055f),
                new Color(0.08f, 0.12f, 0.16f)));

        public TODColorParameter artisticTint = new TODColorParameter(Color.white);
        public TODFloatParameter saturation = new TODFloatParameter(1f);
        public TODFloatParameter contrast = new TODFloatParameter(1f);
        public TODFloatParameter horizonSunGlow = new TODFloatParameter(0.42f);
        public TODFloatParameter horizonSunGlowPower = new TODFloatParameter(10f);
        public TODColorParameter sunScatterColor = new TODColorParameter(new Color(1f, 0.72f, 0.45f));
        public TODFloatParameter sunScatterIntensity = new TODFloatParameter(0.16f);
        public TODFloatParameter sunScatterPower = new TODFloatParameter(34f);
        public TODFloatParameter mieAnisotropy = new TODFloatParameter(0.76f);
        public TODFloatParameter mieOpticalDepth = new TODFloatParameter(0.85f);
        public TODFloatParameter mieHorizonBoost = new TODFloatParameter(1.35f);
        public TODFloatParameter mieMoonAmount = new TODFloatParameter(0.18f);
        public TODColorParameter sunWashColor = new TODColorParameter(
            new Color(0.92f, 0.72f, 0.52f),
            true,
            TODProfile.DayGradient(
                new Color(0.08f, 0.11f, 0.2f),
                new Color(1.1f, 0.34f, 0.12f),
                new Color(0.72f, 0.82f, 1f)));
        public TODFloatParameter sunWashIntensity = new TODFloatParameter(0.28f);
        public TODFloatParameter sunWashPower = new TODFloatParameter(2.4f);
        public TODFloatParameter sunWashHorizonWeight = new TODFloatParameter(0.65f);
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
        public TODFloatParameter density = new TODFloatParameter(0.982f);
        public TODFloatParameter size = new TODFloatParameter(0.12f);
        public TODFloatParameter sizeVariation = new TODFloatParameter(0.78f);
        public TODFloatParameter brightnessVariation = new TODFloatParameter(0.45f);
        public TODFloatParameter twinkle = new TODFloatParameter(0.35f);
        public TODFloatParameter twinkleSpeed = new TODFloatParameter(1.4f);
        public TODFloatParameter horizonFade = new TODFloatParameter(0.08f);
        public TODFloatParameter rotation = new TODFloatParameter(0f);
    }

    [Serializable]
    public sealed class TODCloudShadowSettings
    {
        public bool enabled = true;
        public TODColorParameter color =
            new TODColorParameter(new Color(0.52f, 0.60f, 0.72f));
        public TODFloatParameter scale = new TODFloatParameter(0.0015f);
        public TODFloatParameter sunnyStrength = new TODFloatParameter(0.22f);
        public TODFloatParameter overcastStrength = new TODFloatParameter(0.48f);
        public TODFloatParameter softness = new TODFloatParameter(0.12f);
        public TODFloatParameter maxDistance = new TODFloatParameter(1800f);
    }

    [Serializable]
    public sealed class TODCloudSecondaryLayerSettings
    {
        public bool enabled = true;
        public TODFloatParameter opacity = new TODFloatParameter(0.42f);
        public TODFloatParameter coverage = new TODFloatParameter(0.35f);
        // 仅为旧 Profile 保留，不再参与运行时计算。
        public TODFloatParameter coverageOffset = new TODFloatParameter(-0.08f);
        public TODFloatParameter altitude = new TODFloatParameter(9.5f);
        public TODFloatParameter scale = new TODFloatParameter(0.52f);
        public TODFloatParameter speedX = new TODFloatParameter(-0.0025f);
        public TODFloatParameter speedY = new TODFloatParameter(0.0012f);
        public TODFloatParameter fogBlend = new TODFloatParameter(0.3f);
    }

    [Serializable]
    public sealed class TODCloudLightningSettings
    {
        public bool enabled;
        public Texture2D glowTexture;
        public TODColorParameter color =
            new TODColorParameter(new Color(0.55f, 0.72f, 1.4f));
        public TODFloatParameter intensity = new TODFloatParameter(4f);
        public TODFloatParameter frequency = new TODFloatParameter(2f);
        public TODFloatParameter duration = new TODFloatParameter(0.42f);
        public TODFloatParameter scale = new TODFloatParameter(1.2f);
        public TODFloatParameter glowSpeed = new TODFloatParameter(0.004f);
    }

    [Serializable]
    public sealed class TODCloudSettings
    {
        public bool enabled = true;
        public Texture2D shapeTexture;
        public Texture2D unevenTexture;
        public TODColorParameter color = new TODColorParameter(
            new Color(0.92f, 0.96f, 1f),
            true,
            TODProfile.DayGradient(
                new Color(0.045f, 0.06f, 0.11f),
                new Color(0.72f, 0.33f, 0.25f),
                new Color(0.92f, 0.96f, 1f)));
        public TODColorParameter shadowColor = new TODColorParameter(new Color(0.28f, 0.34f, 0.44f));
        public TODColorParameter frontLitColor =
            new TODColorParameter(new Color(1.02f, 1.04f, 1.08f));
        public TODColorParameter frontDarkColor =
            new TODColorParameter(new Color(0.38f, 0.45f, 0.58f));
        public TODColorParameter backLitColor =
            new TODColorParameter(new Color(1.18f, 0.82f, 0.52f));
        public TODColorParameter backDarkColor =
            new TODColorParameter(new Color(0.24f, 0.29f, 0.40f));
        // Kept serialized for old profiles. The editor now always treats the
        // four directional colors as the authoritative palette.
        public TODFloatParameter directionalColorAmount = new TODFloatParameter(1f);
        public TODColorParameter rimColor =
            new TODColorParameter(new Color(1.2f, 0.82f, 0.5f));
        public TODFloatParameter rimIntensity = new TODFloatParameter(0.62f);
        public TODFloatParameter rimPower = new TODFloatParameter(1f);
        public TODFloatParameter rimWidth = new TODFloatParameter(0.025f);
        public TODFloatParameter opacity = new TODFloatParameter(0.68f);
        public TODFloatParameter coverage = new TODFloatParameter(0.58f);
        public TODFloatParameter scale = new TODFloatParameter(1f);
        public TODFloatParameter detailScale = new TODFloatParameter(12f);
        public TODFloatParameter softness = new TODFloatParameter(0.1f);
        public TODFloatParameter erosion = new TODFloatParameter(0.44f);
        public TODFloatParameter distortion = new TODFloatParameter(1.2f);
        public TODFloatParameter speedX = new TODFloatParameter(0.006f);
        public TODFloatParameter speedY = new TODFloatParameter(0.002f);
        public TODFloatParameter altitude = new TODFloatParameter(6f);
        public TODFloatParameter thickness = new TODFloatParameter(1.25f);
        public TODFloatParameter densityMultiplier = new TODFloatParameter(1f);
        public TODFloatParameter horizonDensity = new TODFloatParameter(0.55f);
        public TODFloatParameter zenithDensity = new TODFloatParameter(1f);
        public TODFloatParameter latitudePosition = new TODFloatParameter(0.3f);
        public TODFloatParameter latitudeWidth = new TODFloatParameter(0.35f);
        public TODFloatParameter horizonFade = new TODFloatParameter(0.12f);
        public TODColorParameter scatteringCoefficient = new TODColorParameter(new Color(0.7f, 0.72f, 0.76f));
        public TODColorParameter absorptionCoefficient = new TODColorParameter(new Color(0.015f, 0.02f, 0.03f));
        public TODFloatParameter phaseForward = new TODFloatParameter(0.72f);
        public TODFloatParameter phaseBackward = new TODFloatParameter(-0.28f);
        public TODFloatParameter phaseBlend = new TODFloatParameter(0.78f);
        public TODFloatParameter sunLighting = new TODFloatParameter(0.85f);
        public TODFloatParameter moonLighting = new TODFloatParameter(0.22f);
        public TODColorParameter ambientColor = new TODColorParameter(new Color(0.32f, 0.42f, 0.58f));
        public TODFloatParameter ambientIntensity = new TODFloatParameter(0.3f);
        public TODFloatParameter multipleScattering = new TODFloatParameter(0.34f);
        public TODFloatParameter aerialPerspective = new TODFloatParameter(0.55f);
        public TODFloatParameter lightWrap = new TODFloatParameter(0.36f);
        public TODFloatParameter selfShadowStrength = new TODFloatParameter(1.8f);
        public TODFloatParameter selfShadowDistance = new TODFloatParameter(0.4f);
        public TODFloatParameter stylization = new TODFloatParameter(0.62f);
        public TODFloatParameter lightSteps = new TODFloatParameter(4f);
        public TODFloatParameter lightStepSoftness = new TODFloatParameter(0.27f);
        public TODFloatParameter sunTransmission = new TODFloatParameter(0.75f);
        public TODFloatParameter sunTransmissionPower = new TODFloatParameter(3.2f);
        public TODFloatParameter undersideStrength = new TODFloatParameter(0.56f);
        public TODCloudSecondaryLayerSettings layer2 = new TODCloudSecondaryLayerSettings();
        public TODCloudLightningSettings lightning = new TODCloudLightningSettings();
        public TODCloudShadowSettings shadows = new TODCloudShadowSettings();
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

        public TODFloatParameter diskSize = new TODFloatParameter(0.016f);
        public TODFloatParameter diskSoftness = new TODFloatParameter(0.0015f);
        public TODColorParameter haloColor = new TODColorParameter(new Color(1f, 0.62f, 0.32f));
        public TODFloatParameter haloSize = new TODFloatParameter(0.065f);
        public TODFloatParameter haloIntensity = new TODFloatParameter(0.16f);
    }

    [Serializable]
    public sealed class TODMoonSettings
    {
        public TODColorParameter color = new TODColorParameter(new Color(0.62f, 0.72f, 1f));
        public TODFloatParameter intensity = new TODFloatParameter(
            0.35f,
            true,
            new AnimationCurve(
                new Keyframe(0f, 0.4f), new Keyframe(5f, 0.35f), new Keyframe(7f, 0f),
                new Keyframe(17.5f, 0f), new Keyframe(19f, 0.35f), new Keyframe(24f, 0.4f)));

        public TODFloatParameter diskSize = new TODFloatParameter(0.02f);
        public TODFloatParameter diskSoftness = new TODFloatParameter(0.0012f);
        public TODColorParameter haloColor = new TODColorParameter(new Color(0.18f, 0.3f, 0.75f));
        public TODFloatParameter haloSize = new TODFloatParameter(0.1f);
        public TODFloatParameter haloIntensity = new TODFloatParameter(0.25f);
        public TODFloatParameter phase = new TODFloatParameter(0.72f);
        public TODFloatParameter phaseSoftness = new TODFloatParameter(0.055f);
        public TODFloatParameter phaseRotation = new TODFloatParameter(0f);
        public TODFloatParameter earthshine = new TODFloatParameter(0.16f);
        public TODFloatParameter surfaceDetail = new TODFloatParameter(0.32f);
        public TODFloatParameter surfaceScale = new TODFloatParameter(7f);
        public TODFloatParameter atmosphereBlend = new TODFloatParameter(0.28f);
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
    public sealed class TODLensFlareSettings
    {
        public bool enabled = true;
        public TODFloatParameter sunIntensity = new TODFloatParameter(
            0.35f,
            true,
            new AnimationCurve(
                new Keyframe(0f, 0f), new Keyframe(5.5f, 0f), new Keyframe(7f, 0.16f),
                new Keyframe(12f, 0.35f), new Keyframe(17f, 0.18f), new Keyframe(18.5f, 0f),
                new Keyframe(24f, 0f)));
        public TODFloatParameter sunScale = new TODFloatParameter(0.7f);
        public TODFloatParameter moonIntensity = new TODFloatParameter(
            0.12f,
            true,
            new AnimationCurve(
                new Keyframe(0f, 0.12f), new Keyframe(5f, 0.1f), new Keyframe(7f, 0f),
                new Keyframe(17.5f, 0f), new Keyframe(19f, 0.1f), new Keyframe(24f, 0.12f)));
        public TODFloatParameter moonScale = new TODFloatParameter(0.28f);
        public bool useOcclusion = true;
        public bool environmentOcclusion;
        public bool allowOffScreen;
        public TODFloatParameter occlusionRadius = new TODFloatParameter(0.18f);
        public TODFloatParameter occlusionSamples = new TODFloatParameter(24f);
        public TODFloatParameter maxAttenuationDistance = new TODFloatParameter(10000f);
    }

    [Serializable]
    public sealed class TODScreenSpaceFogSettings
    {
        public bool enabled = false;
        public TODFloatParameter intensity = new TODFloatParameter(0.28f);
        public TODFloatParameter radius = new TODFloatParameter(1.75f);
        public TODFloatParameter startDistance = new TODFloatParameter(120f);
        public TODFloatParameter endDistance = new TODFloatParameter(900f);
        public TODFloatParameter depthThreshold = new TODFloatParameter(0.025f);
        public TODFloatParameter skyContribution = new TODFloatParameter(0.08f);
    }

    [Serializable]
    public sealed class TODGroundVolumeFogSettings
    {
        // 第一阶段仅建立稳定的数据、全局参数与地形高度入口。
        // 实际 froxel / raymarch 渲染将在专用 Renderer Feature 中实现。
        public bool enabled = false;
        public TODColorParameter albedo = new TODColorParameter(new Color(0.82f, 0.88f, 0.9f));
        public TODFloatParameter density = new TODFloatParameter(0.035f);
        public TODFloatParameter fogHeight = new TODFloatParameter(2.5f);
        public TODFloatParameter heightRange = new TODFloatParameter(8f);
        public TODFloatParameter maxDistance = new TODFloatParameter(280f);
        public TODFloatParameter terrainConformity = new TODFloatParameter(1f);
        public TODFloatParameter noise2DScale = new TODFloatParameter(0.018f);
        public TODFloatParameter noise3DScale = new TODFloatParameter(0.045f);
        public TODFloatParameter erosion = new TODFloatParameter(0.42f);
        public TODFloatParameter windX = new TODFloatParameter(0.8f);
        public TODFloatParameter windZ = new TODFloatParameter(0.25f);
        public TODFloatParameter anisotropy = new TODFloatParameter(0.45f);
        public TODFloatParameter shadowStrength = new TODFloatParameter(0.75f);
        public TODFloatParameter shaftIntensity = new TODFloatParameter(0.65f);
        public TODFloatParameter stepCount = new TODFloatParameter(48f);
    }

    [Serializable]
    public sealed class TODFogSettings
    {
        public bool enabled = true;
        // color 与旧高度雾字段暂时保留，以保证早期 Profile 能无损反序列化。
        public TODColorParameter color = new TODColorParameter(
            new Color(0.5f, 0.62f, 0.72f),
            true,
            TODProfile.DayGradient(
                new Color(0.008f, 0.012f, 0.025f),
                new Color(0.65f, 0.16f, 0.08f),
                new Color(0.5f, 0.62f, 0.72f)));
        public TODColorParameter topColor = new TODColorParameter(
            new Color(0.58f, 0.69f, 0.78f),
            true,
            TODProfile.DayGradient(
                new Color(0.012f, 0.018f, 0.04f),
                new Color(0.72f, 0.24f, 0.13f),
                new Color(0.58f, 0.69f, 0.78f)));
        public TODColorParameter bottomColor = new TODColorParameter(
            new Color(0.42f, 0.52f, 0.58f),
            true,
            TODProfile.DayGradient(
                new Color(0.008f, 0.012f, 0.025f),
                new Color(0.58f, 0.18f, 0.1f),
                new Color(0.42f, 0.52f, 0.58f)));
        public TODFloatParameter topIntensity = new TODFloatParameter(0.8f);
        public TODFloatParameter bottomIntensity = new TODFloatParameter(1f);
        public TODFloatParameter skyIntensity = new TODFloatParameter(0.18f);
        public TODFloatParameter power = new TODFloatParameter(1f);
        public TODFloatParameter exponentialBlend = new TODFloatParameter(0f);
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
        public TODScreenSpaceFogSettings screenSpace = new TODScreenSpaceFogSettings();
        public TODGroundVolumeFogSettings groundVolume = new TODGroundVolumeFogSettings();
    }

    [CreateAssetMenu(fileName = "TOD Profile", menuName = "Unity Nanite/TOD/Profile")]
    public sealed class TODProfile : ScriptableObject
    {
        [HideInInspector] public int generatedPresetVersion;
        public TODSkySettings sky = new TODSkySettings();
        public TODStarsSettings stars = new TODStarsSettings();
        public TODCloudSettings clouds = new TODCloudSettings();
        public TODSunSettings sun = new TODSunSettings();
        public TODMoonSettings moon = new TODMoonSettings();
        public TODLightingSettings lighting = new TODLightingSettings();
        public TODLensFlareSettings lensFlare = new TODLensFlareSettings();
        public TODFogSettings fog = new TODFogSettings();

        [Header("Procedural Celestial Orbit (not keyframed)")]
        [Range(0f, 360f)] public float orbitAzimuth = 0f;
        [Range(-89f, 89f)] public float orbitTilt = 23.5f;
        [Range(0f, 24f)] public float solarNoon = 12f;
        [Range(-45f, 45f)] public float moonOrbitOffset = 5f;
        [Min(1f)] public float gizmoRadius = 25f;
        [Range(0f, 0.25f)] public float twilightWidth = 0.06f;

        private void OnEnable()
        {
            clouds ??= new TODCloudSettings();
            clouds.shadows ??= new TODCloudShadowSettings();
            lensFlare ??= new TODLensFlareSettings();
            fog ??= new TODFogSettings();
            fog.screenSpace ??= new TODScreenSpaceFogSettings();
            fog.groundVolume ??= new TODGroundVolumeFogSettings();
        }

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
