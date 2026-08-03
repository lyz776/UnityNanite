#ifndef REALTIME_GI_LIGHT_COMMON_INCLUDED
#define REALTIME_GI_LIGHT_COMMON_INCLUDED

#define GI_MAX_LIGHTS_PER_BRICK 64u

struct GILocalLightData
{
    float4 positionRange;
    float4 colorIntensity;       // rgb linear intensity, w unused
    float4 directionOuterCos;
    // x type (0 point, 1 spot), y inner cos, z casts GI shadow, w indirect multiplier
    float4 parameters;
};

float GILocalLightAttenuation(
    GILocalLightData light,
    float3 worldPosition,
    out float3 lightDirection,
    out float lightDistance)
{
    float3 toLight = light.positionRange.xyz - worldPosition;
    float distanceSquared = dot(toLight, toLight);
    lightDistance = sqrt(max(distanceSquared, 1e-8));
    lightDirection = toLight / lightDistance;
    float normalizedDistance = lightDistance / max(light.positionRange.w, 1e-4);
    float rangeWindow = saturate(1.0 - normalizedDistance * normalizedDistance);
    float attenuation = lightDistance < light.positionRange.w
        ? rangeWindow * rangeWindow / max(distanceSquared, 0.01) : 0.0;
    if (light.parameters.x > 0.5)
    {
        float coneCos = dot(light.directionOuterCos.xyz, -lightDirection);
        attenuation *= smoothstep(light.directionOuterCos.w, light.parameters.y, coneCos);
    }
    return attenuation;
}

float3 GILocalLightRadiance(GILocalLightData light)
{
    return max(0.0, light.colorIntensity.rgb);
}

#endif
