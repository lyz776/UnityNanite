Shader "Hidden/Unity Nanite/TOD Cloud Shadow"
{
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" }
        ZWrite Off
        ZTest Always
        Cull Off

        Pass
        {
            Name "TOD High Cloud Shadow Composite"

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            float _TODCloudsEnabled;
            float _TODCloudShadowsEnabled;
            TEXTURE2D(_TODCloudShapeTexture);
            SAMPLER(sampler_TODCloudShapeTexture);
            float _TODCloudTexturesEnabled;
            float _TODCloudTime;
            float _TODDayOrNight;
            float3 _TODMainLightDir;
            float _TODCloudAltitude;
            float _TODCloudCoverage;
            float _TODCloudScale;
            float2 _TODCloudSpeed;
            float _TODCloudLayer2Enabled;
            float _TODCloudLayer2Opacity;
            float _TODCloudLayer2Coverage;
            float _TODCloudLayer2Scale;
            float2 _TODCloudLayer2Speed;

            float3 _TODCloudShadowTint;
            float _TODCloudShadowScale;
            float _TODCloudShadowSunnyStrength;
            float _TODCloudShadowOvercastStrength;
            float _TODCloudShadowSoftness;
            float _TODCloudShadowMaxDistance;

            float CloudCoverageThreshold(float coverage)
            {
                return lerp(1.01, 0.28, pow(saturate(coverage), 0.65));
            }

            float CloudShadowLayer(
                float2 position,
                float coverage,
                float scaleMultiplier,
                float2 speed,
                float2 offset,
                float edgeMultiplier)
            {
                float2 cloudUV = position * _TODCloudShadowScale *
                    scaleMultiplier + _TODCloudTime * speed + offset;
                float density = SAMPLE_TEXTURE2D(
                    _TODCloudShapeTexture,
                    sampler_TODCloudShapeTexture,
                    cloudUV).r;
                float threshold = CloudCoverageThreshold(coverage);
                float edge = max(0.004, _TODCloudShadowSoftness * 0.32) *
                    edgeMultiplier;
                return smoothstep(threshold - edge, threshold + edge, density) *
                    step(0.0001, coverage);
            }

            float CloudShadowMask(float2 position)
            {
                // One texture fetch per enabled layer. The old pass evaluated
                // two four-octave FBMs plus six texture fetches per screen pixel.
                float shadow = CloudShadowLayer(
                    position,
                    _TODCloudCoverage,
                    1.0,
                    _TODCloudSpeed,
                    0.0,
                    1.0);
                float shadow2 = 0.0;
                if (_TODCloudLayer2Enabled > 0.5 &&
                    _TODCloudLayer2Coverage > 0.0001 &&
                    _TODCloudLayer2Opacity > 0.0001)
                {
                    float2 layer2Position = float2(
                        position.x * 0.819152 - position.y * 0.573576,
                        position.x * 0.573576 + position.y * 0.819152);
                    shadow2 = CloudShadowLayer(
                        layer2Position,
                        _TODCloudLayer2Coverage,
                        _TODCloudLayer2Scale / max(0.01, _TODCloudScale),
                        _TODCloudLayer2Speed,
                        float2(13.71, -8.43),
                        1.35) * _TODCloudLayer2Opacity;
                }
                return 1.0 - (1.0 - shadow) * (1.0 - shadow2);
            }

            float3 ReconstructSurfaceNormal(float3 worldPosition)
            {
                float3 normal = normalize(cross(ddy(worldPosition), ddx(worldPosition)));
                float3 toCamera = _WorldSpaceCameraPos - worldPosition;
                return dot(normal, toCamera) < 0.0 ? -normal : normal;
            }

            float2 StableSurfaceProjection(
                float3 worldPosition,
                float3 cloudPosition,
                float3 worldNormal)
            {
                // xz is correct on terrain. On a steep receiver xz loses one
                // surface axis, which caused X-facing walls to become stripes.
                // Dominant-face projection keeps two varying coordinates while
                // retaining the physical cloud-plane hit along the light ray.
                float3 normalWeight = abs(worldNormal);
                if (normalWeight.y >= max(normalWeight.x, normalWeight.z))
                    return cloudPosition.xz;
                if (normalWeight.x >= normalWeight.z)
                    return float2(
                        cloudPosition.z,
                        worldPosition.y + cloudPosition.x * 0.35);
                return float2(
                    cloudPosition.x,
                    worldPosition.y + cloudPosition.z * 0.35);
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float2 uv = input.texcoord;
                half4 source = SAMPLE_TEXTURE2D_X(
                    _BlitTexture,
                    sampler_LinearClamp,
                    uv);

                if (_TODCloudsEnabled < 0.5 ||
                    _TODCloudShadowsEnabled < 0.5 ||
                    _TODCloudTexturesEnabled < 0.5)
                    return source;

                float rawDepth = SampleSceneDepth(uv);
                float eyeDepth = LinearEyeDepth(rawDepth, _ZBufferParams);
                #if UNITY_REVERSED_Z
                    bool isSky = rawDepth <= 0.0001;
                #else
                    bool isSky = rawDepth >= 0.9999;
                #endif
                isSky = isSky || eyeDepth >= _ProjectionParams.z * 0.995;
                if (isSky)
                    return source;

                float3 sunDirection = normalize(_TODMainLightDir);
                float sunHeight = smoothstep(0.015, 0.16, sunDirection.y);
                if (sunHeight <= 0.0001)
                    return source;

                float3 worldPosition = ComputeWorldSpacePosition(
                    uv,
                    rawDepth,
                    UNITY_MATRIX_I_VP);
                float3 worldNormal = ReconstructSurfaceNormal(worldPosition);

                // Only modulate visible direct main-light contribution. Pixels
                // already in the light's own shadow receive no additional cloud
                // shadow, avoiding the former double-darkening of shaded areas.
                float4 shadowCoord = TransformWorldToShadowCoord(worldPosition);
                Light mainLight = GetMainLight(shadowCoord);
                float normalLight = saturate(dot(worldNormal, mainLight.direction));
                float directVisibility = normalLight * mainLight.shadowAttenuation *
                    mainLight.distanceAttenuation;
                if (directVisibility <= 0.0001)
                    return source;

                float cloudHeightWorld = max(1.0, _TODCloudAltitude * 1000.0);
                float travel = max(
                    0.0,
                    (cloudHeightWorld - worldPosition.y) /
                    max(0.025, sunDirection.y));
                float3 cloudPosition = worldPosition + sunDirection * travel;
                float2 cloudSamplePosition = StableSurfaceProjection(
                    worldPosition,
                    cloudPosition,
                    worldNormal);
                float shadowMask = CloudShadowMask(cloudSamplePosition);

                float distanceToCamera = distance(worldPosition, _WorldSpaceCameraPos);
                float distanceFade = 1.0 - smoothstep(
                    _TODCloudShadowMaxDistance * 0.72,
                    _TODCloudShadowMaxDistance,
                    distanceToCamera);
                float overcast = smoothstep(0.48, 0.92, _TODCloudCoverage);
                float strength = lerp(
                    _TODCloudShadowSunnyStrength,
                    _TODCloudShadowOvercastStrength,
                    overcast);
                float sourceLuminance = dot(
                    source.rgb,
                    float3(0.2126, 0.7152, 0.0722));
                float lightLuminance = dot(
                    mainLight.color,
                    float3(0.2126, 0.7152, 0.0722));
                float directShare = saturate(
                    lightLuminance * directVisibility /
                    max(0.08, sourceLuminance + lightLuminance * directVisibility));
                float amount = saturate(
                    shadowMask * strength * distanceFade * directVisibility *
                    directShare * sunHeight * _TODDayOrNight);
                half3 shadowed = source.rgb * max(0.0, _TODCloudShadowTint);
                return half4(lerp(source.rgb, shadowed, amount), source.a);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
