using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityNanite.TOD
{
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("Unity Nanite/TOD Controller")]
    public sealed class TODController : MonoBehaviour
    {
        private static readonly HashSet<TODController> Instances = new HashSet<TODController>();

        [SerializeField] private TODProfile profile;
        [SerializeField, Range(0f, 24f)] private float currentTime = 12f;
        [SerializeField] private bool advanceTime = true;
        [SerializeField, Min(0f)] private float hoursPerSecond = 0.1f;
        [SerializeField] private Light mainLight;
        [SerializeField] private Transform sunVisual;
        [SerializeField] private Transform moonVisual;
        [SerializeField] private LensFlareComponentSRP sunLensFlare;
        [SerializeField] private LensFlareComponentSRP moonLensFlare;
        [SerializeField] private Texture terrainHeightTexture;
        [SerializeField] private Vector3 terrainHeightOrigin;
        [SerializeField] private Vector3 terrainHeightSize = new Vector3(1000f, 256f, 1000f);
        [SerializeField] private bool assignSkybox = true;
        [SerializeField] private Material skyboxMaterial;

        private int lastAppliedFrame = -1;
        private double nextLightningTime = -1.0;
        private double lightningStartTime = -1.0;
        private int lightningSequence;
        private bool manualLightning;

        public TODProfile Profile
        {
            get => profile;
            set
            {
                profile = value;
                Apply(true);
            }
        }

        public float CurrentTime
        {
            get => currentTime;
            set
            {
                currentTime = TODProfile.WrapHour(value);
                Apply(true);
            }
        }

        public bool AdvanceTime
        {
            get => advanceTime;
            set => advanceTime = value;
        }

        public float HoursPerSecond
        {
            get => hoursPerSecond;
            set => hoursPerSecond = Mathf.Max(0f, value);
        }

        public Light MainLight { get => mainLight; set { mainLight = value; Apply(true); } }
        public Transform SunVisual { get => sunVisual; set { sunVisual = value; Apply(true); } }
        public Transform MoonVisual { get => moonVisual; set { moonVisual = value; Apply(true); } }
        public LensFlareComponentSRP SunLensFlare { get => sunLensFlare; set { sunLensFlare = value; Apply(true); } }
        public LensFlareComponentSRP MoonLensFlare { get => moonLensFlare; set { moonLensFlare = value; Apply(true); } }
        public Texture TerrainHeightTexture { get => terrainHeightTexture; set { terrainHeightTexture = value; Apply(true); } }
        public Vector3 TerrainHeightOrigin { get => terrainHeightOrigin; set { terrainHeightOrigin = value; Apply(true); } }
        public Vector3 TerrainHeightSize { get => terrainHeightSize; set { terrainHeightSize = value; Apply(true); } }
        public bool AssignSkybox { get => assignSkybox; set { assignSkybox = value; Apply(true); } }
        public Material SkyboxMaterial { get => skyboxMaterial; set { skyboxMaterial = value; Apply(true); } }

        private void OnEnable()
        {
            Instances.Add(this);
            Apply(true);
        }

        private void OnDisable()
        {
            Instances.Remove(this);
        }

        private void Update()
        {
            if (Application.isPlaying && advanceTime && hoursPerSecond > 0f)
                currentTime = TODProfile.WrapHour(currentTime + Time.deltaTime * hoursPerSecond);

            Apply(false);
        }

        private void OnValidate()
        {
            currentTime = TODProfile.WrapHour(currentTime);
            hoursPerSecond = Mathf.Max(0f, hoursPerSecond);
            Apply(true);
        }

        public void ToggleTime()
        {
            advanceTime = !advanceTime;
        }

        public void TriggerLightning()
        {
            manualLightning = true;
            lightningStartTime = Time.realtimeSinceStartupAsDouble;
            lightningSequence++;
            Apply(true);
        }

        public void Apply(bool force)
        {
            if (profile == null)
                return;

            if (!force && !Application.isPlaying && lastAppliedFrame == Time.frameCount)
                return;
            lastAppliedFrame = Time.frameCount;

            float hour = TODProfile.WrapHour(currentTime);
            float cloudTime = (float)Time.realtimeSinceStartupAsDouble;
            Shader.SetGlobalFloat("_TODCloudTime", cloudTime);
            if (assignSkybox && skyboxMaterial != null && RenderSettings.skybox != skyboxMaterial)
                RenderSettings.skybox = skyboxMaterial;

            Vector3 sunDirection = CalculateOrbitDirection(
                hour, profile.solarNoon, profile.orbitAzimuth, profile.orbitTilt);
            Vector3 moonDirection = CalculateOrbitDirection(
                TODProfile.WrapHour(hour + 12f), profile.solarNoon,
                profile.orbitAzimuth + profile.moonOrbitOffset, -profile.orbitTilt);

            float twilight = Mathf.Max(0.001f, profile.twilightWidth);
            float dayAmount = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-twilight, twilight, sunDirection.y));
            Vector3 activeLightDirection = dayAmount >= 0.5f ? sunDirection : moonDirection;

            UpdateCelestialTransform(sunVisual, sunDirection, profile.gizmoRadius);
            UpdateCelestialTransform(moonVisual, moonDirection, profile.gizmoRadius);
            ApplyLensFlares(profile, hour, sunDirection, moonDirection, cloudTime);

            if (mainLight != null)
            {
                mainLight.transform.rotation = Quaternion.LookRotation(-activeLightDirection, Vector3.up);
                mainLight.color = profile.lighting.mainLightColor.Evaluate(hour);
                mainLight.intensity = Mathf.Max(0f, profile.lighting.mainLightIntensity.Evaluate(hour));
                mainLight.shadowStrength = Mathf.Clamp01(profile.lighting.shadowStrength.Evaluate(hour));
            }

            Shader.SetGlobalFloat("_TODTime", hour);
            Shader.SetGlobalFloat("_TODTime01", hour / 24f);
            Shader.SetGlobalFloat("_TODDayOrNight", dayAmount);
            Shader.SetGlobalVector("_TODMainLightDir", new Vector4(activeLightDirection.x, activeLightDirection.y, activeLightDirection.z, 0f));
            Shader.SetGlobalVector("_TODSunDir", new Vector4(sunDirection.x, sunDirection.y, sunDirection.z, 0f));
            Shader.SetGlobalVector("_TODMoonDir", new Vector4(moonDirection.x, moonDirection.y, moonDirection.z, 0f));
            Shader.SetGlobalVector("_TODSkyCenterWorldPos", transform.position);

            ApplySkyGlobals(profile, hour);
            ApplyLightningRuntime(profile, hour);
            ApplyFogGlobals(profile, hour);
            ApplyEnvironment(profile, hour);
        }

        private static void ApplySkyGlobals(TODProfile value, float hour)
        {
            SetColor("_TODLightBottom", value.sky.lightBottom.Evaluate(hour));
            SetColor("_TODLightMiddle", value.sky.lightMiddle.Evaluate(hour));
            SetColor("_TODLightTop", value.sky.lightTop.Evaluate(hour));
            SetColor("_TODHorizonColor", value.sky.horizonColor.Evaluate(hour));
            SetColor("_TODGroundColor", value.sky.groundColor.Evaluate(hour));
            Shader.SetGlobalFloat("_TODMiddleHeight", Mathf.Clamp01(value.sky.middleHeight.Evaluate(hour)));
            Shader.SetGlobalFloat("_TODHorizonWidth", Mathf.Max(0.0001f, value.sky.horizonWidth.Evaluate(hour)));
            Shader.SetGlobalFloat("_TODHorizonIntensity", Mathf.Max(0f, value.sky.horizonIntensity.Evaluate(hour)));
            Shader.SetGlobalFloat("_TODSkyExposure", Mathf.Max(0f, value.sky.exposure.Evaluate(hour)));
            SetColor("_TODArtisticTint", value.sky.artisticTint.Evaluate(hour));
            Shader.SetGlobalFloat("_TODSkySaturation", Mathf.Max(0f, value.sky.saturation.Evaluate(hour)));
            Shader.SetGlobalFloat("_TODSkyContrast", Mathf.Max(0f, value.sky.contrast.Evaluate(hour)));
            Shader.SetGlobalFloat("_TODHorizonSunGlow", Mathf.Max(0f, value.sky.horizonSunGlow.Evaluate(hour)));
            Shader.SetGlobalFloat("_TODHorizonSunGlowPower", Mathf.Max(0.01f, value.sky.horizonSunGlowPower.Evaluate(hour)));
            SetColor("_TODSunScatterColor", value.sky.sunScatterColor.Evaluate(hour));
            Shader.SetGlobalFloat("_TODSunScatterIntensity", Mathf.Max(0f, value.sky.sunScatterIntensity.Evaluate(hour)));
            Shader.SetGlobalFloat("_TODSunScatterPower", Mathf.Max(0.01f, value.sky.sunScatterPower.Evaluate(hour)));
            Shader.SetGlobalFloat("_TODMieAnisotropy", Mathf.Clamp(value.sky.mieAnisotropy.Evaluate(hour), 0f, 0.95f));
            Shader.SetGlobalFloat("_TODMieOpticalDepth", Mathf.Max(0f, value.sky.mieOpticalDepth.Evaluate(hour)));
            Shader.SetGlobalFloat("_TODMieHorizonBoost", Mathf.Max(0f, value.sky.mieHorizonBoost.Evaluate(hour)));
            Shader.SetGlobalFloat("_TODMieMoonAmount", Mathf.Max(0f, value.sky.mieMoonAmount.Evaluate(hour)));
            SetColor("_TODSunWashColor", value.sky.sunWashColor.Evaluate(hour));
            Shader.SetGlobalFloat("_TODSunWashIntensity", Mathf.Max(0f, value.sky.sunWashIntensity.Evaluate(hour)));
            Shader.SetGlobalFloat("_TODSunWashPower", Mathf.Max(0.1f, value.sky.sunWashPower.Evaluate(hour)));
            Shader.SetGlobalFloat(
                "_TODSunWashHorizonWeight",
                Mathf.Clamp01(value.sky.sunWashHorizonWeight.Evaluate(hour)));

            Shader.SetGlobalFloat("_TODStarsEnabled", value.stars.enabled ? 1f : 0f);
            SetColor("_TODStarsColor", value.stars.color.Evaluate(hour));
            Shader.SetGlobalFloat("_TODStarsIntensity", Mathf.Max(0f, value.stars.intensity.Evaluate(hour)));
            Shader.SetGlobalFloat("_TODStarsDensity", Mathf.Clamp01(value.stars.density.Evaluate(hour)));
            Shader.SetGlobalFloat("_TODStarsSize", Mathf.Max(0.001f, value.stars.size.Evaluate(hour)));
            Shader.SetGlobalFloat("_TODStarsSizeVariation", Mathf.Clamp01(value.stars.sizeVariation.Evaluate(hour)));
            Shader.SetGlobalFloat("_TODStarsBrightnessVariation", Mathf.Clamp01(value.stars.brightnessVariation.Evaluate(hour)));
            Shader.SetGlobalFloat("_TODStarsTwinkle", Mathf.Clamp01(value.stars.twinkle.Evaluate(hour)));
            Shader.SetGlobalFloat("_TODStarsTwinkleSpeed", Mathf.Max(0f, value.stars.twinkleSpeed.Evaluate(hour)));
            Shader.SetGlobalFloat("_TODStarsHorizonFade", Mathf.Max(0.001f, value.stars.horizonFade.Evaluate(hour)));
            Shader.SetGlobalFloat("_TODStarsRotation", value.stars.rotation.Evaluate(hour));

            Shader.SetGlobalFloat("_TODCloudsEnabled", value.clouds.enabled ? 1f : 0f);
            Shader.SetGlobalTexture("_TODCloudShapeTexture", value.clouds.shapeTexture);
            Shader.SetGlobalTexture("_TODCloudUnevenTexture", value.clouds.unevenTexture);
            Shader.SetGlobalFloat(
                "_TODCloudTexturesEnabled",
                value.clouds.shapeTexture != null && value.clouds.unevenTexture != null ? 1f : 0f);
            SetColor("_TODCloudColor", value.clouds.color.Evaluate(hour));
            SetColor("_TODCloudShadowColor", value.clouds.shadowColor.Evaluate(hour));
            SetColor("_TODCloudFrontLitColor", value.clouds.frontLitColor.Evaluate(hour));
            SetColor("_TODCloudFrontDarkColor", value.clouds.frontDarkColor.Evaluate(hour));
            SetColor("_TODCloudBackLitColor", value.clouds.backLitColor.Evaluate(hour));
            SetColor("_TODCloudBackDarkColor", value.clouds.backDarkColor.Evaluate(hour));
            Shader.SetGlobalFloat(
                "_TODCloudDirectionalColorAmount",
                1f);
            SetColor("_TODCloudRimColor", value.clouds.rimColor.Evaluate(hour));
            Shader.SetGlobalFloat("_TODCloudRimIntensity", Mathf.Max(0f, value.clouds.rimIntensity.Evaluate(hour)));
            Shader.SetGlobalFloat("_TODCloudRimPower", Mathf.Max(0.01f, value.clouds.rimPower.Evaluate(hour)));
            Shader.SetGlobalFloat("_TODCloudRimWidth", Mathf.Max(0.001f, value.clouds.rimWidth.Evaluate(hour)));
            Shader.SetGlobalFloat("_TODCloudOpacity", Mathf.Clamp01(value.clouds.opacity.Evaluate(hour)));
            Shader.SetGlobalFloat("_TODCloudCoverage", Mathf.Clamp01(value.clouds.coverage.Evaluate(hour)));
            Shader.SetGlobalFloat("_TODCloudFarCoverage", Mathf.Clamp01(value.clouds.farCoverage.Evaluate(hour)));
            float farCoverageStart = Mathf.Max(0f, value.clouds.farCoverageStart.Evaluate(hour));
            Shader.SetGlobalFloat("_TODCloudFarCoverageStart", farCoverageStart);
            Shader.SetGlobalFloat(
                "_TODCloudFarCoverageEnd",
                Mathf.Max(farCoverageStart + 0.1f, value.clouds.farCoverageEnd.Evaluate(hour)));
            Shader.SetGlobalFloat("_TODCloudScale", Mathf.Max(0.01f, value.clouds.scale.Evaluate(hour)));
            Shader.SetGlobalFloat("_TODCloudDetailScale", Mathf.Max(0.1f, value.clouds.detailScale.Evaluate(hour)));
            Shader.SetGlobalFloat("_TODCloudSoftness", Mathf.Max(0.001f, value.clouds.softness.Evaluate(hour)));
            Shader.SetGlobalFloat("_TODCloudErosion", Mathf.Clamp01(value.clouds.erosion.Evaluate(hour)));
            Shader.SetGlobalFloat("_TODCloudDistortion", Mathf.Max(0f, value.clouds.distortion.Evaluate(hour)));
            Shader.SetGlobalVector("_TODCloudSpeed", new Vector4(
                value.clouds.speedX.Evaluate(hour), value.clouds.speedY.Evaluate(hour), 0f, 0f));
            Shader.SetGlobalFloat("_TODCloudHorizonFade", Mathf.Max(0.001f, value.clouds.horizonFade.Evaluate(hour)));
            Shader.SetGlobalFloat("_TODCloudAltitude", Mathf.Max(0.1f, value.clouds.altitude.Evaluate(hour)));
            Shader.SetGlobalFloat("_TODCloudCurvature", Mathf.Clamp(value.clouds.curvature.Evaluate(hour), 0f, 32f));
            Shader.SetGlobalFloat("_TODCloudWorldScale", Mathf.Max(0f, value.clouds.worldPositionScale.Evaluate(hour)));
            Shader.SetGlobalFloat("_TODCloudThickness", Mathf.Max(0.001f, value.clouds.thickness.Evaluate(hour)));
            Shader.SetGlobalFloat("_TODCloudDensityMultiplier", Mathf.Max(0f, value.clouds.densityMultiplier.Evaluate(hour)));
            Shader.SetGlobalFloat("_TODCloudHorizonDensity", Mathf.Max(0f, value.clouds.horizonDensity.Evaluate(hour)));
            Shader.SetGlobalFloat("_TODCloudZenithDensity", Mathf.Max(0f, value.clouds.zenithDensity.Evaluate(hour)));
            Shader.SetGlobalFloat("_TODCloudLatitudePosition", Mathf.Clamp01(value.clouds.latitudePosition.Evaluate(hour)));
            Shader.SetGlobalFloat("_TODCloudLatitudeWidth", Mathf.Max(0.001f, value.clouds.latitudeWidth.Evaluate(hour)));
            SetColor("_TODCloudScatteringCoeff", value.clouds.scatteringCoefficient.Evaluate(hour));
            SetColor("_TODCloudAbsorptionCoeff", value.clouds.absorptionCoefficient.Evaluate(hour));
            Shader.SetGlobalFloat("_TODCloudPhaseForward", Mathf.Clamp(value.clouds.phaseForward.Evaluate(hour), 0f, 0.95f));
            Shader.SetGlobalFloat("_TODCloudPhaseBackward", Mathf.Clamp(value.clouds.phaseBackward.Evaluate(hour), -0.9f, 0f));
            Shader.SetGlobalFloat("_TODCloudPhaseBlend", Mathf.Clamp01(value.clouds.phaseBlend.Evaluate(hour)));
            // These artistic gains overlap the directional palette and are no
            // longer profile-facing. Keep stable authored values internally.
            Shader.SetGlobalFloat("_TODCloudSunLighting", 0.85f);
            Shader.SetGlobalFloat("_TODCloudMoonLighting", 0.22f);
            SetColor("_TODCloudAmbientColor", value.clouds.ambientColor.Evaluate(hour));
            Shader.SetGlobalFloat("_TODCloudAmbientIntensity", Mathf.Max(0f, value.clouds.ambientIntensity.Evaluate(hour)));
            Shader.SetGlobalFloat("_TODCloudMultipleScattering", Mathf.Max(0f, value.clouds.multipleScattering.Evaluate(hour)));
            Shader.SetGlobalFloat("_TODCloudAerialPerspective", Mathf.Clamp01(value.clouds.aerialPerspective.Evaluate(hour)));
            Shader.SetGlobalFloat("_TODCloudLightWrap", Mathf.Clamp01(value.clouds.lightWrap.Evaluate(hour)));
            Shader.SetGlobalFloat(
                "_TODCloudSelfShadowStrength",
                Mathf.Max(0f, value.clouds.selfShadowStrength.Evaluate(hour)));
            Shader.SetGlobalFloat(
                "_TODCloudSelfShadowDistance",
                Mathf.Max(0f, value.clouds.selfShadowDistance.Evaluate(hour)));
            Shader.SetGlobalFloat("_TODCloudStylization", 0.62f);
            Shader.SetGlobalFloat(
                "_TODCloudLightSteps",
                Mathf.Clamp(Mathf.Round(value.clouds.lightSteps.Evaluate(hour)), 1f, 8f));
            Shader.SetGlobalFloat(
                "_TODCloudLightStepSoftness",
                Mathf.Clamp(value.clouds.lightStepSoftness.Evaluate(hour), 0.001f, 0.49f));
            Shader.SetGlobalFloat(
                "_TODCloudSunTransmission",
                Mathf.Max(0f, value.clouds.sunTransmission.Evaluate(hour)));
            Shader.SetGlobalFloat(
                "_TODCloudSunTransmissionPower",
                Mathf.Max(0.1f, value.clouds.sunTransmissionPower.Evaluate(hour)));
            Shader.SetGlobalFloat("_TODCloudUndersideStrength", 0.56f);

            TODCloudSecondaryLayerSettings layer2 =
                value.clouds.layer2 ?? (value.clouds.layer2 = new TODCloudSecondaryLayerSettings());
            Shader.SetGlobalFloat("_TODCloudLayer2Enabled", layer2.enabled ? 1f : 0f);
            Shader.SetGlobalFloat("_TODCloudLayer2Opacity", Mathf.Clamp01(layer2.opacity.Evaluate(hour)));
            Shader.SetGlobalFloat(
                "_TODCloudLayer2Coverage",
                Mathf.Clamp01(layer2.coverage.Evaluate(hour)));
            Shader.SetGlobalFloat("_TODCloudLayer2Altitude", Mathf.Max(0.1f, layer2.altitude.Evaluate(hour)));
            Shader.SetGlobalFloat("_TODCloudLayer2Scale", Mathf.Max(0.01f, layer2.scale.Evaluate(hour)));
            Shader.SetGlobalVector("_TODCloudLayer2Speed", new Vector4(
                layer2.speedX.Evaluate(hour), layer2.speedY.Evaluate(hour), 0f, 0f));
            Shader.SetGlobalFloat("_TODCloudLayer2FogBlend", Mathf.Clamp01(layer2.fogBlend.Evaluate(hour)));

            TODCloudLightningSettings lightning =
                value.clouds.lightning ?? (value.clouds.lightning = new TODCloudLightningSettings());
            Shader.SetGlobalFloat("_TODCloudLightningEnabled", lightning.enabled ? 1f : 0f);
            Shader.SetGlobalTexture(
                "_TODCloudLightningTexture",
                lightning.glowTexture != null ? lightning.glowTexture : value.clouds.unevenTexture);
            SetColor("_TODCloudLightningColor", lightning.color.Evaluate(hour));
            Shader.SetGlobalFloat(
                "_TODCloudLightningIntensity",
                Mathf.Max(0f, lightning.intensity.Evaluate(hour)));
            Shader.SetGlobalFloat(
                "_TODCloudLightningFrequency",
                Mathf.Max(0.001f, lightning.frequency.Evaluate(hour)));
            Shader.SetGlobalFloat(
                "_TODCloudLightningDuration",
                Mathf.Max(0.02f, lightning.duration.Evaluate(hour)));
            Shader.SetGlobalFloat("_TODCloudLightningScale", Mathf.Max(0.01f, lightning.scale.Evaluate(hour)));
            Shader.SetGlobalFloat("_TODCloudLightningGlowSpeed", lightning.glowSpeed.Evaluate(hour));

            Shader.SetGlobalFloat(
                "_TODCloudShadowsEnabled",
                value.clouds.enabled && value.clouds.shadows.enabled ? 1f : 0f);
            SetColor("_TODCloudShadowTint", value.clouds.shadows.color.Evaluate(hour));
            Shader.SetGlobalFloat("_TODCloudShadowScale", Mathf.Max(0.000001f, value.clouds.shadows.scale.Evaluate(hour)));
            Shader.SetGlobalFloat(
                "_TODCloudShadowSunnyStrength",
                Mathf.Clamp01(value.clouds.shadows.sunnyStrength.Evaluate(hour)));
            Shader.SetGlobalFloat(
                "_TODCloudShadowOvercastStrength",
                Mathf.Clamp01(value.clouds.shadows.overcastStrength.Evaluate(hour)));
            Shader.SetGlobalFloat(
                "_TODCloudShadowSoftness",
                Mathf.Max(0.001f, value.clouds.shadows.softness.Evaluate(hour)));
            Shader.SetGlobalFloat(
                "_TODCloudShadowMaxDistance",
                Mathf.Max(1f, value.clouds.shadows.maxDistance.Evaluate(hour)));

            SetColor("_TODSunColor", value.sun.color.Evaluate(hour));
            Shader.SetGlobalFloat("_TODSunIntensity", Mathf.Max(0f, value.sun.intensity.Evaluate(hour)));
            Shader.SetGlobalFloat("_TODSunSize", Mathf.Clamp(value.sun.diskSize.Evaluate(hour), 0.0001f, 0.5f));
            Shader.SetGlobalFloat("_TODSunSoftness", Mathf.Max(0.00001f, value.sun.diskSoftness.Evaluate(hour)));
            SetColor("_TODSunHaloColor", value.sun.haloColor.Evaluate(hour));
            Shader.SetGlobalFloat("_TODSunHaloSize", Mathf.Max(0.001f, value.sun.haloSize.Evaluate(hour)));
            Shader.SetGlobalFloat("_TODSunHaloIntensity", Mathf.Max(0f, value.sun.haloIntensity.Evaluate(hour)));

            SetColor("_TODMoonColor", value.moon.color.Evaluate(hour));
            Shader.SetGlobalFloat("_TODMoonIntensity", Mathf.Max(0f, value.moon.intensity.Evaluate(hour)));
            Shader.SetGlobalFloat("_TODMoonSize", Mathf.Clamp(value.moon.diskSize.Evaluate(hour), 0.0001f, 0.5f));
            Shader.SetGlobalFloat("_TODMoonSoftness", Mathf.Max(0.00001f, value.moon.diskSoftness.Evaluate(hour)));
            SetColor("_TODMoonHaloColor", value.moon.haloColor.Evaluate(hour));
            Shader.SetGlobalFloat("_TODMoonHaloSize", Mathf.Max(0.001f, value.moon.haloSize.Evaluate(hour)));
            Shader.SetGlobalFloat("_TODMoonHaloIntensity", Mathf.Max(0f, value.moon.haloIntensity.Evaluate(hour)));
            Shader.SetGlobalFloat("_TODMoonPhase", Mathf.Clamp01(value.moon.phase.Evaluate(hour)));
            Shader.SetGlobalFloat("_TODMoonPhaseSoftness", Mathf.Max(0.001f, value.moon.phaseSoftness.Evaluate(hour)));
            Shader.SetGlobalFloat("_TODMoonPhaseRotation", value.moon.phaseRotation.Evaluate(hour));
            Shader.SetGlobalFloat("_TODMoonEarthshine", Mathf.Clamp01(value.moon.earthshine.Evaluate(hour)));
            Shader.SetGlobalFloat("_TODMoonSurfaceDetail", Mathf.Clamp01(value.moon.surfaceDetail.Evaluate(hour)));
            Shader.SetGlobalFloat("_TODMoonSurfaceScale", Mathf.Max(0.1f, value.moon.surfaceScale.Evaluate(hour)));
            Shader.SetGlobalFloat("_TODMoonAtmosphereBlend", Mathf.Clamp01(value.moon.atmosphereBlend.Evaluate(hour)));
        }

        private void ApplyFogGlobals(TODProfile value, float hour)
        {
            Shader.SetGlobalFloat("_TODFogEnabled", value.fog.enabled ? 1f : 0f);
            SetColor("_TODFogColor", value.fog.color.Evaluate(hour));
            SetColor("_TODFogTopColor", value.fog.topColor.Evaluate(hour));
            SetColor("_TODFogBottomColor", value.fog.bottomColor.Evaluate(hour));
            float start = Mathf.Max(0f, value.fog.startDistance.Evaluate(hour));
            float end = Mathf.Max(start + 0.001f, value.fog.endDistance.Evaluate(hour));
            Shader.SetGlobalVector("_TODFogDistance", new Vector4(
                start,
                end,
                Mathf.Max(0f, value.fog.density.Evaluate(hour)),
                Mathf.Clamp01(value.fog.exponentialBlend.Evaluate(hour))));
            Shader.SetGlobalVector("_TODFogShape", new Vector4(
                Mathf.Max(0f, value.fog.topIntensity.Evaluate(hour)),
                Mathf.Max(0f, value.fog.bottomIntensity.Evaluate(hour)),
                Mathf.Clamp01(value.fog.skyIntensity.Evaluate(hour)),
                Mathf.Max(0.01f, value.fog.power.Evaluate(hour))));
            Shader.SetGlobalVector("_TODFogHeight", new Vector4(
                value.fog.baseHeight.Evaluate(hour),
                Mathf.Max(0.001f, value.fog.heightRange.Evaluate(hour)),
                0f,
                0f));

            Shader.SetGlobalFloat("_TODHeightFogEnabled", value.fog.heightFogEnabled ? 1f : 0f);
            SetColor("_TODHeightFogColor", value.fog.heightColor.Evaluate(hour));
            float heightStart = Mathf.Max(0f, value.fog.heightStartDistance.Evaluate(hour));
            float heightEnd = Mathf.Max(heightStart + 0.001f, value.fog.heightEndDistance.Evaluate(hour));
            Shader.SetGlobalVector("_TODHeightFogParams", new Vector4(
                value.fog.baseHeight.Evaluate(hour),
                Mathf.Max(0.001f, value.fog.heightRange.Evaluate(hour)),
                Mathf.Max(0f, value.fog.heightDensity.Evaluate(hour)),
                0f));
            Shader.SetGlobalVector("_TODHeightFogDistance", new Vector4(heightStart, heightEnd, 0f, 0f));

            TODScreenSpaceFogSettings screen = value.fog.screenSpace;
            float screenStart = Mathf.Max(0f, screen.startDistance.Evaluate(hour));
            float screenEnd = Mathf.Max(screenStart + 0.001f, screen.endDistance.Evaluate(hour));
            Shader.SetGlobalFloat(
                "_TODScreenFogEnabled",
                value.fog.enabled && screen.enabled ? 1f : 0f);
            Shader.SetGlobalVector("_TODScreenFogParams", new Vector4(
                Mathf.Clamp01(screen.intensity.Evaluate(hour)),
                Mathf.Clamp(screen.radius.Evaluate(hour), 0f, 8f),
                screenStart,
                screenEnd));
            Shader.SetGlobalVector("_TODScreenFogDepth", new Vector4(
                Mathf.Max(0.0001f, screen.depthThreshold.Evaluate(hour)),
                Mathf.Clamp01(screen.skyContribution.Evaluate(hour)),
                0f,
                0f));

            TODGroundVolumeFogSettings ground = value.fog.groundVolume;
            Shader.SetGlobalFloat(
                "_TODGroundFogEnabled",
                value.fog.enabled && ground.enabled ? 1f : 0f);
            SetColor("_TODGroundFogAlbedo", ground.albedo.Evaluate(hour));
            Shader.SetGlobalVector("_TODGroundFogParams0", new Vector4(
                Mathf.Max(0f, ground.density.Evaluate(hour)),
                ground.fogHeight.Evaluate(hour),
                Mathf.Max(0.001f, ground.heightRange.Evaluate(hour)),
                Mathf.Max(1f, ground.maxDistance.Evaluate(hour))));
            Shader.SetGlobalVector("_TODGroundFogParams1", new Vector4(
                Mathf.Clamp01(ground.terrainConformity.Evaluate(hour)),
                Mathf.Max(0.00001f, ground.noise2DScale.Evaluate(hour)),
                Mathf.Max(0.00001f, ground.noise3DScale.Evaluate(hour)),
                Mathf.Clamp01(ground.erosion.Evaluate(hour))));
            Shader.SetGlobalVector("_TODGroundFogParams2", new Vector4(
                ground.windX.Evaluate(hour),
                ground.windZ.Evaluate(hour),
                Mathf.Clamp(ground.anisotropy.Evaluate(hour), -0.9f, 0.9f),
                Mathf.Clamp01(ground.shadowStrength.Evaluate(hour))));
            Shader.SetGlobalVector("_TODGroundFogParams3", new Vector4(
                Mathf.Max(0f, ground.shaftIntensity.Evaluate(hour)),
                Mathf.Clamp(Mathf.Round(ground.stepCount.Evaluate(hour)), 8f, 128f),
                Time.realtimeSinceStartup,
                0f));
            Shader.SetGlobalTexture(
                "_TODTerrainHeightTexture",
                terrainHeightTexture != null ? terrainHeightTexture : Texture2D.blackTexture);
            Shader.SetGlobalVector("_TODTerrainHeightOrigin", terrainHeightOrigin);
            Shader.SetGlobalVector("_TODTerrainHeightSize", new Vector4(
                Mathf.Max(0.001f, terrainHeightSize.x),
                Mathf.Max(0.001f, terrainHeightSize.y),
                Mathf.Max(0.001f, terrainHeightSize.z),
                terrainHeightTexture != null ? 1f : 0f));
        }

        private void ApplyLensFlares(
            TODProfile value,
            float hour,
            Vector3 sunDirection,
            Vector3 moonDirection,
            float cloudTime)
        {
            Camera cloudCamera = Camera.current != null ? Camera.current : Camera.main;
            Vector2 cameraWorldPosition = cloudCamera != null
                ? new Vector2(cloudCamera.transform.position.x, cloudCamera.transform.position.z) *
                    Mathf.Max(0f, value.clouds.worldPositionScale.Evaluate(hour))
                : Vector2.zero;
            float sunCloudTransmission = EvaluateCloudTransmission(
                value.clouds, sunDirection, hour, cloudTime, cameraWorldPosition);
            float moonCloudTransmission = EvaluateCloudTransmission(
                value.clouds, moonDirection, hour, cloudTime, cameraWorldPosition);
            ApplyLensFlare(
                sunLensFlare,
                value.lensFlare,
                value.lensFlare.sunIntensity.Evaluate(hour) * sunCloudTransmission,
                value.lensFlare.sunScale.Evaluate(hour),
                value.sun.color.Evaluate(hour),
                hour);
            ApplyLensFlare(
                moonLensFlare,
                value.lensFlare,
                value.lensFlare.moonIntensity.Evaluate(hour) * moonCloudTransmission,
                value.lensFlare.moonScale.Evaluate(hour),
                value.moon.color.Evaluate(hour),
                hour);
        }

        private void ApplyLightningRuntime(TODProfile value, float hour)
        {
            TODCloudLightningSettings lightning = value.clouds.lightning;
            if (lightning == null)
                return;

            double now = Time.realtimeSinceStartupAsDouble;
            float duration = Mathf.Max(0.02f, lightning.duration.Evaluate(hour));
            float frequency = Mathf.Max(0.001f, lightning.frequency.Evaluate(hour));
            bool scheduled = lightning.enabled;

            if (scheduled && nextLightningTime < 0.0)
                nextLightningTime = now + Mathf.Min(1f, 6f / frequency);

            if (scheduled && now >= nextLightningTime)
            {
                lightningStartTime = now;
                lightningSequence++;
                float variation = 0.72f + 0.56f * Hash01(lightningSequence * 19.17f);
                nextLightningTime = now + 60.0 / frequency * variation;
            }

            float elapsed = lightningStartTime < 0.0
                ? float.PositiveInfinity
                : (float)(now - lightningStartTime);
            bool flashing = elapsed >= 0f && elapsed < duration;
            float pulse = 0f;
            if (flashing)
            {
                float envelope = 1f - SmoothStep(0f, duration, elapsed);
                float multiPulse = Mathf.Lerp(
                    0.38f,
                    1f,
                    Mathf.Abs(Mathf.Sin(elapsed * 48f + lightningSequence * 1.73f)));
                pulse = envelope * multiPulse;
            }
            else if (manualLightning)
            {
                manualLightning = false;
            }

            bool active = scheduled || manualLightning || flashing;
            Shader.SetGlobalFloat("_TODCloudLightningEnabled", active ? 1f : 0f);
            Shader.SetGlobalFloat("_TODCloudLightningPulse", pulse);
            Shader.SetGlobalFloat("_TODCloudLightningSeed", lightningSequence);
            float intensity = Mathf.Max(0f, lightning.intensity.Evaluate(hour));
            if (manualLightning || flashing && !scheduled)
                intensity = Mathf.Max(4f, intensity);
            Shader.SetGlobalFloat("_TODCloudLightningIntensity", intensity);

            if (!scheduled && !manualLightning && !flashing)
                nextLightningTime = -1.0;
        }

        private static float EvaluateCloudTransmission(
            TODCloudSettings clouds,
            Vector3 direction,
            float hour,
            float cloudTime,
            Vector2 cameraWorldPosition)
        {
            if (clouds == null || !clouds.enabled || direction.y <= 0.025f)
                return 1f;

            float opacity = Mathf.Clamp01(clouds.opacity.Evaluate(hour));
            if (opacity <= 0f)
                return 1f;

            float coverage = Mathf.Clamp01(clouds.coverage.Evaluate(hour));
            float cloud1 = EvaluateCloudLayer(
                clouds,
                direction,
                clouds.altitude.Evaluate(hour),
                clouds.scale.Evaluate(hour),
                new Vector2(clouds.speedX.Evaluate(hour), clouds.speedY.Evaluate(hour)),
                coverage,
                Mathf.Clamp01(clouds.farCoverage.Evaluate(hour)),
                hour,
                cloudTime,
                cameraWorldPosition,
                false);

            TODCloudSecondaryLayerSettings layer2 = clouds.layer2;
            float cloud2 = 0f;
            if (layer2 != null && layer2.enabled)
            {
                cloud2 = EvaluateCloudLayer(
                    clouds,
                    direction,
                    layer2.altitude.Evaluate(hour),
                    layer2.scale.Evaluate(hour),
                    new Vector2(layer2.speedX.Evaluate(hour), layer2.speedY.Evaluate(hour)),
                    Mathf.Clamp01(layer2.coverage.Evaluate(hour)),
                    Mathf.Clamp01(layer2.coverage.Evaluate(hour)),
                    hour,
                    cloudTime,
                    cameraWorldPosition,
                    true) * Mathf.Clamp01(layer2.opacity.Evaluate(hour));
            }

            float mask = 1f - (1f - cloud1) * (1f - cloud2);
            float latitude = Mathf.Clamp01(direction.y);
            float latitudePosition = clouds.latitudePosition.Evaluate(hour);
            float latitudeWidth = Mathf.Max(0.001f, clouds.latitudeWidth.Evaluate(hour));
            float distribution = Mathf.Lerp(
                Mathf.Max(0f, clouds.horizonDensity.Evaluate(hour)),
                Mathf.Max(0f, clouds.zenithDensity.Evaluate(hour)),
                SmoothStep(
                    latitudePosition - latitudeWidth,
                    latitudePosition + latitudeWidth,
                    latitude));
            float horizonProjectionWidth = Mathf.Max(
                0.003f,
                clouds.horizonFade.Evaluate(hour) * 0.05f);
            float horizonVisibility = clouds.curvature.Evaluate(hour) > 0.0001f
                ? SmoothStep(-0.01f, 0.002f, direction.y)
                : SmoothStep(
                    horizonProjectionWidth * 1.2f,
                    horizonProjectionWidth * 2.8f,
                    Mathf.Max(0f, direction.y));
            mask *= horizonVisibility * distribution *
                Mathf.Max(0f, clouds.densityMultiplier.Evaluate(hour));
            return Mathf.Pow(Mathf.Clamp01(1f - Mathf.Clamp01(mask) * opacity), 4f);
        }

        private static float EvaluateCloudLayer(
            TODCloudSettings clouds,
            Vector3 direction,
            float altitude,
            float scale,
            Vector2 speed,
            float nearCoverage,
            float farCoverage,
            float hour,
            float cloudTime,
            Vector2 cameraWorldPosition,
            bool rotate)
        {
            if (Mathf.Max(nearCoverage, farCoverage) <= 0.0001f ||
                clouds.shapeTexture == null || clouds.unevenTexture == null)
                return 0f;
            if (!clouds.shapeTexture.isReadable || !clouds.unevenTexture.isReadable)
                return Mathf.Max(nearCoverage, farCoverage) *
                    Mathf.Max(nearCoverage, farCoverage);

            float horizonProjectionWidth = Mathf.Max(
                0.003f,
                clouds.horizonFade.Evaluate(hour) * 0.05f);
            float positiveHeight = Mathf.Max(0f, direction.y);
            float safeAltitude = Mathf.Max(0.1f, altitude);
            float flatDistance = safeAltitude /
                Mathf.Max(positiveHeight, horizonProjectionWidth);
            float curvature = Mathf.Clamp(clouds.curvature.Evaluate(hour), 0f, 32f);
            float layerDistance = flatDistance;
            if (curvature > 0.0001f)
            {
                float radius = 6371f / curvature;
                float radialProjection = radius * positiveHeight;
                float shellDelta = safeAltitude * (2f * radius + safeAltitude);
                float shellRoot = Mathf.Sqrt(
                    radialProjection * radialProjection + shellDelta);
                layerDistance = shellDelta /
                    Mathf.Max(0.0001f, shellRoot + radialProjection);
            }

            Vector2 planeDirection = cameraWorldPosition +
                new Vector2(direction.x, direction.z) * layerDistance;
            if (rotate)
                planeDirection = new Vector2(
                    planeDirection.x * 0.819152f - planeDirection.y * 0.573576f,
                    planeDirection.x * 0.573576f + planeDirection.y * 0.819152f);
            float farStart = Mathf.Max(0f, clouds.farCoverageStart.Evaluate(hour));
            float farEnd = Mathf.Max(farStart + 0.1f, clouds.farCoverageEnd.Evaluate(hour));
            float farBlend = SmoothStep(farStart, farEnd, layerDistance);
            float coverage = Mathf.Lerp(
                Mathf.Clamp01(nearCoverage),
                Mathf.Clamp01(farCoverage),
                farBlend);
            Vector2 uv = planeDirection * (Mathf.Max(0.01f, scale) * 0.018f) +
                speed * cloudTime;
            if (rotate)
                uv += new Vector2(13.71f, -8.43f);

            try
            {
                Color warp = SampleRepeat(clouds.unevenTexture, uv * 0.55f);
                Vector2 shapedUv = uv + new Vector2(warp.r - 0.5f, warp.g - 0.5f) *
                    (Mathf.Max(0f, clouds.distortion.Evaluate(hour)) * 0.035f);
                float broad = SampleRepeat(clouds.shapeTexture, shapedUv).r;
                float detailScale = Mathf.Max(0.1f, clouds.detailScale.Evaluate(hour));
                float detail = SampleRepeat(
                    clouds.shapeTexture,
                    shapedUv * detailScale + Vector2.one * 0.371f).r;
                float uneven = SampleRepeat(clouds.unevenTexture, uv * 2f).r;
                broad = Mathf.Clamp01(broad + (Mathf.Pow(Mathf.Clamp01(uneven), 4f) - 0.5f) * 0.18f);
                float erosion = Mathf.Clamp01(clouds.erosion.Evaluate(hour));
                float density = broad - (1f - Mathf.Lerp(0.5f, detail, erosion)) * erosion * 0.42f;
                float edge = Mathf.Max(0.012f, clouds.softness.Evaluate(hour) * 0.38f);
                return SmoothStep(CoverageThreshold(coverage) - edge,
                    CoverageThreshold(coverage) + edge, density);
            }
            catch (UnityException)
            {
                // 自定义贴图若未开启 Read/Write，只退回保守估计，不中断 TOD。
                return coverage * coverage;
            }
        }

        private static Color SampleRepeat(Texture2D texture, Vector2 uv)
        {
            return texture.GetPixelBilinear(Mathf.Repeat(uv.x, 1f), Mathf.Repeat(uv.y, 1f));
        }

        private static float CoverageThreshold(float coverage)
        {
            return Mathf.Lerp(1.01f, 0.28f, Mathf.Pow(Mathf.Clamp01(coverage), 0.65f));
        }

        private static float SmoothStep(float from, float to, float value)
        {
            float t = Mathf.Clamp01((value - from) / Mathf.Max(0.0001f, to - from));
            return t * t * (3f - 2f * t);
        }

        private static float Hash01(float value)
        {
            return Mathf.Repeat(Mathf.Sin(value * 12.9898f) * 43758.5453f, 1f);
        }

        private static void ApplyLensFlare(
            LensFlareComponentSRP flare,
            TODLensFlareSettings settings,
            float intensity,
            float scale,
            Color lightColor,
            float hour)
        {
            if (flare == null)
                return;

            bool active = settings.enabled && flare.lensFlareData != null;
            flare.enabled = active;
            if (!active)
                return;

            flare.intensity = Mathf.Max(0f, intensity);
            flare.scale = Mathf.Max(0f, scale);
            flare.useOcclusion = settings.useOcclusion;
            flare.environmentOcclusion = settings.environmentOcclusion;
            flare.allowOffScreen = settings.allowOffScreen;
            flare.occlusionRadius = Mathf.Max(0f, settings.occlusionRadius.Evaluate(hour));
            flare.sampleCount = (uint)Mathf.Clamp(
                Mathf.RoundToInt(settings.occlusionSamples.Evaluate(hour)), 1, 64);
            flare.maxAttenuationDistance = Mathf.Max(
                0.0001f, settings.maxAttenuationDistance.Evaluate(hour));
            flare.maxAttenuationScale = flare.maxAttenuationDistance;
            flare.attenuationByLightShape = false;

            // 挂点上的零强度方向光只负责让 SRP Flare 使用“无限远天体”定位，
            // 不参与场景照明；它的颜色仍可供 Flare Data 做颜色调制。
            Light anchorLight = flare.GetComponent<Light>();
            if (anchorLight != null)
            {
                anchorLight.type = LightType.Directional;
                anchorLight.color = lightColor;
                anchorLight.intensity = 0f;
            }
        }

        private static void ApplyEnvironment(TODProfile value, float hour)
        {
            // The renderer feature owns opaque fog composition. Keeping Unity's
            // material fog disabled prevents standard URP shaders being fogged twice.
            RenderSettings.fog = false;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = value.fog.bottomColor.Evaluate(hour);
            RenderSettings.fogStartDistance = Mathf.Max(0f, value.fog.startDistance.Evaluate(hour));
            RenderSettings.fogEndDistance = Mathf.Max(RenderSettings.fogStartDistance + 0.001f, value.fog.endDistance.Evaluate(hour));
            Shader.SetGlobalColor("_GIEnvironmentSkyColor", value.sky.lightTop.Evaluate(hour));
            Shader.SetGlobalFloat("_GIEnvironmentIntensity",
                Mathf.Max(0f, value.sky.giIntensity.Evaluate(hour)));

        }

        private static void SetColor(string property, Color value)
        {
            Shader.SetGlobalColor(property, value);
        }

        private static Vector3 CalculateOrbitDirection(float hour, float solarNoon, float azimuth, float tilt)
        {
            float phase = (hour - solarNoon) * 15f;
            Vector3 local = Quaternion.AngleAxis(phase, Vector3.right) * Vector3.up;
            Quaternion orientation =
                Quaternion.AngleAxis(azimuth, Vector3.up) *
                Quaternion.AngleAxis(tilt, Vector3.forward);
            return (orientation * local).normalized;
        }

        private void UpdateCelestialTransform(Transform target, Vector3 direction, float radius)
        {
            if (target == null)
                return;
            target.position = transform.position + direction * Mathf.Max(1f, radius);
            target.rotation = Quaternion.LookRotation(-direction, Vector3.up);
        }

        public Vector3 GetSunDirection(float hour)
        {
            if (profile == null)
                return Vector3.up;
            return CalculateOrbitDirection(hour, profile.solarNoon, profile.orbitAzimuth, profile.orbitTilt);
        }

        public static void RefreshAll(TODProfile changedProfile = null)
        {
            Instances.RemoveWhere(item => item == null);
            foreach (TODController controller in Instances)
            {
                if (changedProfile == null || controller.profile == changedProfile)
                    controller.Apply(true);
            }
        }
    }
}
