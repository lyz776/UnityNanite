Shader "Skybox/Unity Nanite TOD Dynamic Sky"
{
    SubShader
    {
        Tags { "Queue"="Background" "RenderType"="Background" "PreviewType"="Skybox" }
        Cull Off
        ZWrite Off

        Pass
        {
            Name "TOD Sky"

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 viewDirection : TEXCOORD0;
            };

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
            float _TODHorizonSunGlow;
            float _TODHorizonSunGlowPower;
            float3 _TODSunScatterColor;
            float _TODSunScatterIntensity;
            float _TODSunScatterPower;

            float _TODStarsEnabled;
            float3 _TODStarsColor;
            float _TODStarsIntensity;
            float _TODStarsDensity;
            float _TODStarsSize;
            float _TODStarsTwinkle;
            float _TODStarsTwinkleSpeed;
            float _TODStarsHorizonFade;
            float _TODStarsRotation;

            float _TODCloudsEnabled;
            float3 _TODCloudColor;
            float3 _TODCloudShadowColor;
            float _TODCloudOpacity;
            float _TODCloudCoverage;
            float _TODCloudScale;
            float _TODCloudSoftness;
            float2 _TODCloudSpeed;
            float _TODCloudHorizonFade;
            float _TODCloudSunLighting;

            float3 _TODSunDir;
            float3 _TODSunColor;
            float _TODSunIntensity;
            float _TODSunSize;
            float _TODSunSoftness;
            float3 _TODSunHaloColor;
            float _TODSunHaloSize;
            float _TODSunHaloIntensity;

            float3 _TODMoonDir;
            float3 _TODMoonColor;
            float _TODMoonIntensity;
            float _TODMoonSize;
            float _TODMoonSoftness;
            float3 _TODMoonHaloColor;
            float _TODMoonHaloSize;
            float _TODMoonHaloIntensity;
            float _TODMoonPhase;

            Varyings Vert(Attributes input)
            {
                Varyings output;
                float3 worldPosition = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(worldPosition);
                output.viewDirection = normalize(mul((float3x3)unity_ObjectToWorld, input.positionOS.xyz));
                return output;
            }

            float SoftDisk(float directionDot, float size, float softness)
            {
                float angularDistance = 1.0 - saturate(directionDot);
                return 1.0 - smoothstep(size, size + softness, angularDistance);
            }

            float SoftHalo(float directionDot, float size)
            {
                float angularDistance = max(0.0, 1.0 - directionDot);
                return exp(-angularDistance / max(0.0001, size));
            }

            float MoonPhaseMask(float3 viewDirection, float3 moonDirection, float phase)
            {
                float3 referenceAxis = abs(moonDirection.y) > 0.98 ? float3(1, 0, 0) : float3(0, 1, 0);
                float3 tangent = normalize(cross(referenceAxis, moonDirection));
                float side = dot(viewDirection, tangent) / max(0.0001, sqrt(_TODMoonSize));
                float terminator = lerp(1.1, -1.1, phase);
                return smoothstep(terminator - 0.08, terminator + 0.08, side);
            }

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

            float3 RotateAroundY(float3 direction, float degrees)
            {
                float angle = radians(degrees);
                float sineValue;
                float cosineValue;
                sincos(angle, sineValue, cosineValue);
                return float3(
                    direction.x * cosineValue - direction.z * sineValue,
                    direction.y,
                    direction.x * sineValue + direction.z * cosineValue);
            }

            float ProceduralStars(float3 viewDirection)
            {
                float3 rotated = RotateAroundY(viewDirection, _TODStarsRotation);
                float2 sphericalUV = float2(
                    atan2(rotated.z, rotated.x) * 0.15915494 + 0.5,
                    asin(clamp(rotated.y, -1.0, 1.0)) * 0.31830989 + 0.5);
                float2 gridUV = sphericalUV * float2(420.0, 210.0);
                float2 cell = floor(gridUV);
                float2 local = frac(gridUV) - 0.5;
                float randomValue = Hash21(cell);
                float starCell = step(_TODStarsDensity, randomValue);
                float star = 1.0 - smoothstep(
                    _TODStarsSize * 0.35,
                    _TODStarsSize,
                    length(local));
                float twinkle = lerp(
                    1.0,
                    0.55 + 0.45 * sin(_Time.y * _TODStarsTwinkleSpeed + randomValue * 31.4),
                    _TODStarsTwinkle);
                float horizonFade = smoothstep(0.0, _TODStarsHorizonFade, viewDirection.y);
                return star * starCell * twinkle * horizonFade;
            }

            float ProceduralClouds(float3 viewDirection)
            {
                float projectionHeight = max(0.08, viewDirection.y + 0.28);
                float2 cloudUV = viewDirection.xz / projectionHeight;
                cloudUV = cloudUV * (_TODCloudScale * 0.18) + _Time.y * _TODCloudSpeed;
                float cloudNoise = Fbm(cloudUV);
                float threshold = lerp(0.78, 0.28, _TODCloudCoverage);
                float cloud = smoothstep(
                    threshold - _TODCloudSoftness,
                    threshold + _TODCloudSoftness,
                    cloudNoise);
                return cloud * smoothstep(-0.02, _TODCloudHorizonFade, viewDirection.y);
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float3 viewDirection = normalize(input.viewDirection);
                float height = viewDirection.y;
                float upperHeight = saturate(height);
                float middlePoint = clamp(_TODMiddleHeight, 0.02, 0.98);

                float lowerBlend = smoothstep(0.0, middlePoint, upperHeight);
                float upperBlend = smoothstep(middlePoint, 1.0, upperHeight);
                float3 upperSky = lerp(_TODLightBottom, _TODLightMiddle, lowerBlend);
                upperSky = lerp(upperSky, _TODLightTop, upperBlend);

                float groundBlend = smoothstep(-0.15, 0.015, height);
                float3 sky = lerp(_TODGroundColor, upperSky, groundBlend);

                float horizon = exp(-abs(height) / max(0.0001, _TODHorizonWidth));
                sky += _TODHorizonColor * horizon * _TODHorizonIntensity;

                float sunDot = dot(viewDirection, normalize(_TODSunDir));
                float sunScatter = pow(saturate(sunDot), _TODSunScatterPower);
                sky += _TODSunScatterColor * sunScatter * _TODSunScatterIntensity;
                float2 viewHorizontal = viewDirection.xz;
                float2 sunHorizontal = _TODSunDir.xz;
                viewHorizontal /= max(length(viewHorizontal), 0.0001);
                sunHorizontal /= max(length(sunHorizontal), 0.0001);
                float horizonFacingSun = pow(
                    saturate(dot(viewHorizontal, sunHorizontal)),
                    _TODHorizonSunGlowPower);
                sky += _TODHorizonColor * horizon * horizonFacingSun * _TODHorizonSunGlow;

                float sunDisk = SoftDisk(sunDot, _TODSunSize, _TODSunSoftness);
                float sunHalo = SoftHalo(sunDot, _TODSunHaloSize);
                sky += _TODSunHaloColor * sunHalo * _TODSunHaloIntensity * _TODSunIntensity;
                sky = lerp(sky, _TODSunColor * _TODSunIntensity, sunDisk);

                float moonDot = dot(viewDirection, normalize(_TODMoonDir));
                float moonDisk = SoftDisk(moonDot, _TODMoonSize, _TODMoonSoftness);
                float moonHalo = SoftHalo(moonDot, _TODMoonHaloSize);
                float phaseMask = MoonPhaseMask(viewDirection, normalize(_TODMoonDir), saturate(_TODMoonPhase));
                sky += _TODMoonHaloColor * moonHalo * _TODMoonHaloIntensity * _TODMoonIntensity;
                sky = lerp(sky, _TODMoonColor * _TODMoonIntensity, moonDisk * phaseMask);

                if (_TODStarsEnabled > 0.5)
                    sky += _TODStarsColor * ProceduralStars(viewDirection) * _TODStarsIntensity;

                if (_TODCloudsEnabled > 0.5)
                {
                    float cloud = ProceduralClouds(viewDirection) * _TODCloudOpacity;
                    float cloudSun = pow(saturate(sunDot * 0.5 + 0.5), 4.0) * _TODCloudSunLighting;
                    float3 cloudColor = lerp(_TODCloudShadowColor, _TODCloudColor, saturate(0.35 + cloudSun));
                    sky = lerp(sky, cloudColor, saturate(cloud));
                }

                float luminance = dot(sky, float3(0.2126, 0.7152, 0.0722));
                sky = lerp(luminance.xxx, sky, _TODSkySaturation);
                sky = (sky - 0.5) * _TODSkyContrast + 0.5;
                sky *= _TODArtisticTint * _TODSkyExposure;
                return half4(max(0.0, sky), 1.0);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
