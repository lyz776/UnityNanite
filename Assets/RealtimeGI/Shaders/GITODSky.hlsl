#ifndef REALTIME_GI_TOD_SKY_INCLUDED
#define REALTIME_GI_TOD_SKY_INCLUDED

// Shared, analytic TOD environment used by the radiance cache, final gather and
// URP's glossy-environment fallback.  This deliberately samples TOD globals
// directly; Unity's ambient sky color and reflection probes are not inputs.
float3 _TODLightBottom;
float3 _TODLightMiddle;
float3 _TODLightTop;
float3 _TODHorizonColor;
float3 _TODGroundColor;
float _TODMiddleHeight;
float _TODHorizonWidth;
float _TODHorizonIntensity;
float _TODSkyExposure;
float3 _TODArtisticTint;
float _TODSkySaturation;
float _TODSkyContrast;
float3 _TODSunDir;
float3 _TODMoonDir;
float3 _TODSunScatterColor;
float _TODSunScatterIntensity;
float _TODSunScatterPower;
float3 _TODSunWashColor;
float _TODSunWashIntensity;
float _TODSunWashPower;
float _TODSunWashHorizonWeight;
float3 _TODMoonColor;
float _TODMoonIntensity;
float3 _TODMoonHaloColor;
float _TODMoonHaloSize;
float _TODMoonHaloIntensity;

float3 GITODApplyColorGrade(float3 color)
{
    float luminance = dot(color, float3(0.2126, 0.7152, 0.0722));
    color = lerp(luminance.xxx, color, max(0.0, _TODSkySaturation));
    // Match TODDynamicSky's grading contract exactly, so GI fallback changes in
    // lock-step with the visible TOD sky rather than merely sharing its palette.
    color = (color - 0.5) * max(0.0, _TODSkyContrast) + 0.5;
    color *= max(0.0, _TODArtisticTint) * max(0.0, _TODSkyExposure);
    return max(0.0, color);
}

float3 GIEvaluateTODSky(float3 direction)
{
    direction = normalize(direction);
    float height = direction.y;
    float upperHeight = saturate(height);
    float middlePoint = clamp(_TODMiddleHeight, 0.02, 0.98);
    float3 upperSky = lerp(
        _TODLightBottom, _TODLightMiddle,
        smoothstep(0.0, middlePoint, upperHeight));
    upperSky = lerp(
        upperSky, _TODLightTop,
        smoothstep(middlePoint, 1.0, upperHeight));

    float horizonWidth = max(0.0001, _TODHorizonWidth);
    float3 sky = lerp(
        _TODGroundColor, upperSky,
        smoothstep(-horizonWidth, horizonWidth, height));
    float horizon = 1.0 - smoothstep(0.0, horizonWidth, abs(height));
    float horizonBlend = saturate(horizon * min(_TODHorizonIntensity, 1.0));
    sky = lerp(sky, _TODHorizonColor, horizonBlend);
    sky += _TODHorizonColor * horizon * max(0.0, _TODHorizonIntensity - 1.0);

    float3 sunDirection = normalize(_TODSunDir);
    float sunDot = saturate(dot(direction, sunDirection));
    float sunFacing = pow(sunDot, max(0.1, _TODSunScatterPower));
    sky += _TODSunScatterColor * sunFacing * max(0.0, _TODSunScatterIntensity);
    float sunWash = pow(saturate(dot(direction, sunDirection) * 0.5 + 0.5),
                        max(0.1, _TODSunWashPower));
    float washHorizon = lerp(
        1.0, pow(1.0 - saturate(abs(height)), 1.6),
        saturate(_TODSunWashHorizonWeight));
    sky += _TODSunWashColor * sunWash * washHorizon *
           max(0.0, _TODSunWashIntensity) * 0.32;

    float3 moonDirection = normalize(_TODMoonDir);
    float moonDot = saturate(dot(direction, moonDirection));
    float moonWidth = max(0.002, _TODMoonHaloSize);
    float moonHalo = exp2(-(1.0 - moonDot) / moonWidth);
    sky += (_TODMoonColor * max(0.0, _TODMoonIntensity) +
            _TODMoonHaloColor * max(0.0, _TODMoonHaloIntensity)) * moonHalo;
    return GITODApplyColorGrade(sky);
}

float3 GIEvaluateTODDiffuseSky(float3 normal)
{
    normal = normalize(normal);
    // A compact cosine-hemisphere irradiance approximation. The weighted samples are
    // radiance; PI is the cosine-hemisphere integral for a constant environment.
    float3 vertical = normal.y >= 0.0 ? float3(0.0, 1.0, 0.0) : float3(0.0, -1.0, 0.0);
    // Do not depend on PI being provided by the including shader. GIRadianceCache.compute
    // has a deliberately small include surface and D3D11 otherwise sees PI as undefined.
    return (GIEvaluateTODSky(normal) * 0.60 +
            GIEvaluateTODSky(vertical) * 0.40) * 3.14159265359;
}

float3 GIEvaluateTODGlossySky(float3 reflectionDirection, float roughness)
{
    float3 sharp = GIEvaluateTODSky(reflectionDirection);
    float3 broad = (GIEvaluateTODSky(float3(0.0, 1.0, 0.0)) +
                    GIEvaluateTODSky(float3(0.0, -1.0, 0.0)) +
                    GIEvaluateTODSky(float3(1.0, 0.0, 0.0)) +
                    GIEvaluateTODSky(float3(-1.0, 0.0, 0.0))) * 0.25;
    return lerp(sharp, broad, saturate(roughness * roughness));
}

#endif
