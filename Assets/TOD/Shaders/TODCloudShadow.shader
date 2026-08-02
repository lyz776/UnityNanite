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

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            float _TODCloudsEnabled;
            float _TODCloudShadowsEnabled;
            TEXTURE2D(_TODCloudShapeTexture);
            SAMPLER(sampler_TODCloudShapeTexture);
            TEXTURE2D(_TODCloudUnevenTexture);
            SAMPLER(sampler_TODCloudUnevenTexture);
            float _TODCloudTexturesEnabled;
            float _TODDayOrNight;
            float3 _TODSunDir;
            float _TODCloudAltitude;
            float _TODCloudCoverage;
            float _TODCloudScale;
            float _TODCloudDetailScale;
            float _TODCloudErosion;
            float _TODCloudDistortion;
            float2 _TODCloudSpeed;
            float _TODCloudLayer2Enabled;
            float _TODCloudLayer2Opacity;
            float _TODCloudLayer2CoverageOffset;
            float _TODCloudLayer2Scale;
            float2 _TODCloudLayer2Speed;

            float3 _TODCloudShadowTint;
            float _TODCloudShadowScale;
            float _TODCloudShadowSunnyStrength;
            float _TODCloudShadowOvercastStrength;
            float _TODCloudShadowSoftness;
            float _TODCloudShadowMaxDistance;

            float Hash21(float2 value)
            {
                value = frac(value * float2(123.34, 456.21));
                value += dot(value, value + 45.32);
                return frac(value.x * value.y);
            }

            float ValueNoise(float2 value)
            {
                float2 cell = floor(value);
                float2 local = frac(value);
                local = local * local * (3.0 - 2.0 * local);
                float a = Hash21(cell);
                float b = Hash21(cell + float2(1, 0));
                float c = Hash21(cell + float2(0, 1));
                float d = Hash21(cell + 1);
                return lerp(lerp(a, b, local.x), lerp(c, d, local.x), local.y);
            }

            float Fbm(float2 value)
            {
                float result = 0.0;
                float amplitude = 0.5;
                [unroll]
                for (int octave = 0; octave < 4; octave++)
                {
                    result += ValueNoise(value) * amplitude;
                    value = value * 2.03 + 17.17;
                    amplitude *= 0.5;
                }
                return result;
            }

            float CloudShadowMask(float2 position)
            {
                float2 cloudUV =
                    position * _TODCloudShadowScale + _Time.y * _TODCloudSpeed;
                float2 warp = float2(
                    Fbm(cloudUV * 0.52 + 7.13),
                    Fbm(cloudUV * 0.52 + float2(31.7, 19.2))) - 0.5;
                float proceduralShape = Fbm(cloudUV + warp * _TODCloudDistortion);
                float proceduralDetail = Fbm(
                    cloudUV * _TODCloudDetailScale - warp * 0.75 +
                    float2(12.4, -8.1));
                float2 textureWarp = SAMPLE_TEXTURE2D(
                    _TODCloudUnevenTexture,
                    sampler_TODCloudUnevenTexture,
                    cloudUV * 0.55).rg - 0.5;
                float2 shapedUV = cloudUV + textureWarp *
                    (_TODCloudDistortion * 0.035 * _TODCloudTexturesEnabled);
                float textureShape = SAMPLE_TEXTURE2D(
                    _TODCloudShapeTexture,
                    sampler_TODCloudShapeTexture,
                    shapedUV).r;
                float textureDetail = SAMPLE_TEXTURE2D(
                    _TODCloudShapeTexture,
                    sampler_TODCloudShapeTexture,
                    shapedUV * _TODCloudDetailScale + 0.371).r;
                float uneven = SAMPLE_TEXTURE2D(
                    _TODCloudUnevenTexture,
                    sampler_TODCloudUnevenTexture,
                    cloudUV * 2.0).r;
                uneven = pow(saturate(uneven), 4.0);
                float broadShape = lerp(
                    proceduralShape,
                    saturate(textureShape + (uneven - 0.5) * 0.18),
                    _TODCloudTexturesEnabled);
                float fineShape = lerp(
                    proceduralDetail,
                    textureDetail,
                    _TODCloudTexturesEnabled);
                float erodedDetail = lerp(0.5, fineShape, _TODCloudErosion);
                float density =
                    broadShape - (1.0 - erodedDetail) * _TODCloudErosion * 0.42;
                float threshold = lerp(0.64, 0.30, _TODCloudCoverage);
                float edge = max(0.004, _TODCloudShadowSoftness * 0.32);
                float shadow = smoothstep(
                    threshold - edge,
                    threshold + edge,
                    density);
                float2 layer2Direction = float2(
                    -cloudUV.y * 0.573576 + cloudUV.x * 0.819152,
                    cloudUV.x * 0.573576 + cloudUV.y * 0.819152);
                float2 layer2UV = layer2Direction *
                    (_TODCloudLayer2Scale / max(0.01, _TODCloudScale)) +
                    _Time.y * (_TODCloudLayer2Speed - _TODCloudSpeed) +
                    float2(13.71, -8.43);
                float layer2Shape = SAMPLE_TEXTURE2D(
                    _TODCloudShapeTexture,
                    sampler_TODCloudShapeTexture,
                    layer2UV).r;
                float layer2Detail = SAMPLE_TEXTURE2D(
                    _TODCloudShapeTexture,
                    sampler_TODCloudShapeTexture,
                    layer2UV * _TODCloudDetailScale + 0.371).r;
                float layer2Density = layer2Shape -
                    (1.0 - layer2Detail) * _TODCloudErosion * 0.42;
                float layer2Coverage = saturate(
                    _TODCloudCoverage + _TODCloudLayer2CoverageOffset);
                float layer2Threshold = lerp(0.64, 0.30, layer2Coverage);
                float shadow2 = smoothstep(
                    layer2Threshold - edge * 1.35,
                    layer2Threshold + edge * 1.35,
                    layer2Density) * _TODCloudLayer2Enabled *
                    _TODCloudLayer2Opacity;
                shadow = 1.0 - (1.0 - shadow) * (1.0 - shadow2);
                return shadow * smoothstep(0.0, 0.02, _TODCloudCoverage);
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float2 uv = input.texcoord;
                half4 source = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv);

                if (_TODCloudsEnabled < 0.5 || _TODCloudShadowsEnabled < 0.5)
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

                float3 sunDirection = normalize(_TODSunDir);
                float sunHeight = smoothstep(0.015, 0.16, sunDirection.y);
                if (sunHeight <= 0.0001)
                    return source;

                float3 worldPosition =
                    ComputeWorldSpacePosition(uv, rawDepth, UNITY_MATRIX_I_VP);
                float cloudHeightWorld = max(1.0, _TODCloudAltitude * 1000.0);
                float travel = max(
                    0.0,
                    (cloudHeightWorld - worldPosition.y) /
                    max(0.025, sunDirection.y));
                float2 cloudSamplePosition =
                    worldPosition.xz + sunDirection.xz * travel;
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
                float amount = saturate(
                    shadowMask * strength * distanceFade *
                    sunHeight * _TODDayOrNight);
                half3 shadowed = source.rgb * max(0.0, _TODCloudShadowTint);
                return half4(lerp(source.rgb, shadowed, amount), source.a);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
