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
        [SerializeField] private bool assignSkybox = true;
        [SerializeField] private Material skyboxMaterial;

        private int lastAppliedFrame = -1;

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

        public void Apply(bool force)
        {
            if (profile == null)
                return;

            if (!force && !Application.isPlaying && lastAppliedFrame == Time.frameCount)
                return;
            lastAppliedFrame = Time.frameCount;

            float hour = TODProfile.WrapHour(currentTime);
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

            Shader.SetGlobalFloat("_TODStarsEnabled", value.stars.enabled ? 1f : 0f);
            SetColor("_TODStarsColor", value.stars.color.Evaluate(hour));
            Shader.SetGlobalFloat("_TODStarsIntensity", Mathf.Max(0f, value.stars.intensity.Evaluate(hour)));
            Shader.SetGlobalFloat("_TODStarsDensity", Mathf.Clamp01(value.stars.density.Evaluate(hour)));
            Shader.SetGlobalFloat("_TODStarsSize", Mathf.Max(0.001f, value.stars.size.Evaluate(hour)));
            Shader.SetGlobalFloat("_TODStarsTwinkle", Mathf.Clamp01(value.stars.twinkle.Evaluate(hour)));
            Shader.SetGlobalFloat("_TODStarsTwinkleSpeed", Mathf.Max(0f, value.stars.twinkleSpeed.Evaluate(hour)));
            Shader.SetGlobalFloat("_TODStarsHorizonFade", Mathf.Max(0.001f, value.stars.horizonFade.Evaluate(hour)));
            Shader.SetGlobalFloat("_TODStarsRotation", value.stars.rotation.Evaluate(hour));

            Shader.SetGlobalFloat("_TODCloudsEnabled", value.clouds.enabled ? 1f : 0f);
            SetColor("_TODCloudColor", value.clouds.color.Evaluate(hour));
            SetColor("_TODCloudShadowColor", value.clouds.shadowColor.Evaluate(hour));
            Shader.SetGlobalFloat("_TODCloudOpacity", Mathf.Clamp01(value.clouds.opacity.Evaluate(hour)));
            Shader.SetGlobalFloat("_TODCloudCoverage", Mathf.Clamp01(value.clouds.coverage.Evaluate(hour)));
            Shader.SetGlobalFloat("_TODCloudScale", Mathf.Max(0.01f, value.clouds.scale.Evaluate(hour)));
            Shader.SetGlobalFloat("_TODCloudSoftness", Mathf.Max(0.001f, value.clouds.softness.Evaluate(hour)));
            Shader.SetGlobalVector("_TODCloudSpeed", new Vector4(
                value.clouds.speedX.Evaluate(hour), value.clouds.speedY.Evaluate(hour), 0f, 0f));
            Shader.SetGlobalFloat("_TODCloudHorizonFade", Mathf.Max(0.001f, value.clouds.horizonFade.Evaluate(hour)));
            Shader.SetGlobalFloat("_TODCloudSunLighting", Mathf.Max(0f, value.clouds.sunLighting.Evaluate(hour)));

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
        }

        private static void ApplyFogGlobals(TODProfile value, float hour)
        {
            Shader.SetGlobalFloat("_TODFogEnabled", value.fog.enabled ? 1f : 0f);
            SetColor("_TODFogColor", value.fog.color.Evaluate(hour));
            float start = Mathf.Max(0f, value.fog.startDistance.Evaluate(hour));
            float end = Mathf.Max(start + 0.001f, value.fog.endDistance.Evaluate(hour));
            Shader.SetGlobalVector("_TODFogDistance", new Vector4(start, end, Mathf.Max(0f, value.fog.density.Evaluate(hour)), 0f));

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
        }

        private static void ApplyEnvironment(TODProfile value, float hour)
        {
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = value.lighting.ambientSky.Evaluate(hour);
            RenderSettings.ambientEquatorColor = value.lighting.ambientEquator.Evaluate(hour);
            RenderSettings.ambientGroundColor = value.lighting.ambientGround.Evaluate(hour);
            RenderSettings.ambientIntensity = Mathf.Max(0f, value.lighting.ambientIntensity.Evaluate(hour));

            // The renderer feature owns opaque fog composition. Keeping Unity's
            // material fog disabled prevents standard URP shaders being fogged twice.
            RenderSettings.fog = false;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = value.fog.color.Evaluate(hour);
            RenderSettings.fogStartDistance = Mathf.Max(0f, value.fog.startDistance.Evaluate(hour));
            RenderSettings.fogEndDistance = Mathf.Max(RenderSettings.fogStartDistance + 0.001f, value.fog.endDistance.Evaluate(hour));
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
