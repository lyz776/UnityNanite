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
            float _TODDayOrNight;
            float3 _TODSunDir;
            float _TODCloudAltitude;
            float _TODCloudCoverage;
            float _TODCloudDetailScale;
            float _TODCloudErosion;
            float _TODCloudDistortion;
            float2 _TODCloudSpeed;

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
                float broadShape = Fbm(cloudUV + warp * _TODCloudDistortion);
                float fineShape = Fbm(
                    cloudUV * _TODCloudDetailScale - warp * 0.75 +
                    float2(12.4, -8.1));
                float erodedDetail = lerp(0.5, fineShape, _TODCloudErosion);
                float density =
                    broadShape - (1.0 - erodedDetail) * _TODCloudErosion * 0.42;
                float threshold = lerp(0.64, 0.30, _TODCloudCoverage);
                float edge = max(0.004, _TODCloudShadowSoftness * 0.32);
                return smoothstep(threshold - edge, threshold + edge, density);
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
