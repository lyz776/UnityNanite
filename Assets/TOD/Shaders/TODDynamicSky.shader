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
            float _TODMieAnisotropy;
            float _TODMieOpticalDepth;
            float _TODMieHorizonBoost;
            float _TODMieMoonAmount;
            float3 _TODSunWashColor;
            float _TODSunWashIntensity;
            float _TODSunWashPower;
            float _TODSunWashHorizonWeight;

            float _TODStarsEnabled;
            float3 _TODStarsColor;
            float _TODStarsIntensity;
            float _TODStarsDensity;
            float _TODStarsSize;
            float _TODStarsSizeVariation;
            float _TODStarsBrightnessVariation;
            float _TODStarsTwinkle;
            float _TODStarsTwinkleSpeed;
            float _TODStarsHorizonFade;
            float _TODStarsRotation;

            float _TODCloudsEnabled;
            TEXTURE2D(_TODCloudShapeTexture);
            SAMPLER(sampler_TODCloudShapeTexture);
            TEXTURE2D(_TODCloudUnevenTexture);
            SAMPLER(sampler_TODCloudUnevenTexture);
            TEXTURE2D(_TODCloudLightningTexture);
            SAMPLER(sampler_TODCloudLightningTexture);
            float _TODCloudTexturesEnabled;
            float3 _TODCloudColor;
            float3 _TODCloudShadowColor;
            float3 _TODCloudFrontLitColor;
            float3 _TODCloudFrontDarkColor;
            float3 _TODCloudBackLitColor;
            float3 _TODCloudBackDarkColor;
            float _TODCloudDirectionalColorAmount;
            float3 _TODCloudRimColor;
            float _TODCloudRimIntensity;
            float _TODCloudRimPower;
            float _TODCloudRimWidth;
            float _TODCloudOpacity;
            float _TODCloudCoverage;
            float _TODCloudScale;
            float _TODCloudDetailScale;
            float _TODCloudSoftness;
            float _TODCloudErosion;
            float _TODCloudDistortion;
            float2 _TODCloudSpeed;
            float _TODCloudHorizonFade;
            float _TODCloudAltitude;
            float _TODCloudThickness;
            float _TODCloudDensityMultiplier;
            float _TODCloudHorizonDensity;
            float _TODCloudZenithDensity;
            float _TODCloudLatitudePosition;
            float _TODCloudLatitudeWidth;
            float3 _TODCloudScatteringCoeff;
            float3 _TODCloudAbsorptionCoeff;
            float _TODCloudPhaseForward;
            float _TODCloudPhaseBackward;
            float _TODCloudPhaseBlend;
            float _TODCloudSunLighting;
            float _TODCloudMoonLighting;
            float3 _TODCloudAmbientColor;
            float _TODCloudAmbientIntensity;
            float _TODCloudMultipleScattering;
            float _TODCloudAerialPerspective;
            float _TODCloudLightWrap;
            float _TODCloudSelfShadowStrength;
            float _TODCloudSelfShadowDistance;
            float _TODCloudStylization;
            float _TODCloudLightSteps;
            float _TODCloudLightStepSoftness;
            float _TODCloudSunTransmission;
            float _TODCloudSunTransmissionPower;
            float _TODCloudUndersideStrength;
            float _TODCloudLayer2Enabled;
            float _TODCloudLayer2Opacity;
            float _TODCloudLayer2CoverageOffset;
            float _TODCloudLayer2Altitude;
            float _TODCloudLayer2Scale;
            float2 _TODCloudLayer2Speed;
            float _TODCloudLayer2FogBlend;
            float _TODCloudLightningEnabled;
            float3 _TODCloudLightningColor;
            float _TODCloudLightningIntensity;
            float _TODCloudLightningFrequency;
            float _TODCloudLightningDuration;
            float _TODCloudLightningScale;
            float _TODCloudLightningGlowSpeed;

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
            float _TODMoonPhaseSoftness;
            float _TODMoonPhaseRotation;
            float _TODMoonEarthshine;
            float _TODMoonSurfaceDetail;
            float _TODMoonSurfaceScale;
            float _TODMoonAtmosphereBlend;

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
                // Chord distance closely follows angular radius for small sky discs.
                float angularDistance = sqrt(max(0.0, 2.0 * (1.0 - saturate(directionDot))));
                return 1.0 - smoothstep(size, size + softness, angularDistance);
            }

            float SoftHalo(float directionDot, float size)
            {
                float angularDistance = sqrt(max(0.0, 2.0 * (1.0 - saturate(directionDot))));
                return exp(-angularDistance / max(0.0001, size));
            }

            float HenyeyGreenstein(float cosineTheta, float anisotropy)
            {
                float g = clamp(anisotropy, -0.95, 0.95);
                float denominator = max(
                    0.001,
                    pow(1.0 + g * g - 2.0 * g * cosineTheta, 1.5));
                return (1.0 - g * g) / denominator;
            }

            float2 MoonDiscCoordinates(float3 viewDirection, float3 moonDirection)
            {
                float3 referenceAxis = abs(moonDirection.y) > 0.98 ? float3(1, 0, 0) : float3(0, 1, 0);
                float3 tangent = normalize(cross(referenceAxis, moonDirection));
                float3 bitangent = normalize(cross(moonDirection, tangent));
                float radius = max(0.0001, _TODMoonSize);
                float2 discPosition = float2(
                    dot(viewDirection, tangent),
                    dot(viewDirection, bitangent)) / radius;
                float angle = radians(_TODMoonPhaseRotation);
                float sineValue;
                float cosineValue;
                sincos(angle, sineValue, cosineValue);
                return float2(
                    discPosition.x * cosineValue - discPosition.y * sineValue,
                    discPosition.x * sineValue + discPosition.y * cosineValue);
            }

            float MoonPhaseMask(float3 viewDirection, float3 moonDirection, float phase)
            {
                float2 discPosition = MoonDiscCoordinates(viewDirection, moonDirection);
                float surfaceDepth = sqrt(saturate(1.0 - dot(discPosition, discPosition)));

                // phase: 0=new moon, 0.5=half moon, 1=full moon.
                float phaseAngle = (1.0 - phase) * PI;
                float sineValue;
                float cosineValue;
                sincos(phaseAngle, sineValue, cosineValue);
                float lightDot = dot(
                    float3(discPosition.x, discPosition.y, surfaceDepth),
                    float3(sineValue, 0.0, cosineValue));
                float terminatorSoftness = max(0.002, _TODMoonPhaseSoftness);
                return smoothstep(-terminatorSoftness, terminatorSoftness, lightDot);
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

            float StylizedLightSteps(float value)
            {
                float steps = clamp(floor(_TODCloudLightSteps + 0.5), 1.0, 8.0);
                if (steps <= 1.0)
                    return saturate(value);

                float scaledValue = saturate(value) * steps;
                float baseStep = floor(scaledValue);
                float transition = smoothstep(
                    0.5 - _TODCloudLightStepSoftness,
                    0.5 + _TODCloudLightStepSoftness,
                    frac(scaledValue));
                return saturate((baseStep + transition) / steps);
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
                float sizeRandom = Hash21(cell + float2(19.7, 73.1));
                float brightnessRandom = Hash21(cell + float2(91.3, 7.9));
                float starCell = step(_TODStarsDensity, randomValue);
                float rareLargeStar = smoothstep(0.985, 1.0, sizeRandom);
                float variedSize = lerp(
                    1.0,
                    lerp(0.32, 1.65, pow(sizeRandom, 2.4)) + rareLargeStar * 0.85,
                    _TODStarsSizeVariation);
                float starRadius = _TODStarsSize * variedSize;
                float star = 1.0 - smoothstep(
                    starRadius * 0.32,
                    starRadius,
                    length(local));
                float variedBrightness = lerp(
                    1.0,
                    lerp(0.35, 1.35, brightnessRandom),
                    _TODStarsBrightnessVariation);
                float twinkle = lerp(
                    1.0,
                    0.55 + 0.45 * sin(_Time.y * _TODStarsTwinkleSpeed + randomValue * 31.4),
                    _TODStarsTwinkle);
                float horizonFade = smoothstep(0.0, _TODStarsHorizonFade, viewDirection.y);
                return star * starCell * variedBrightness * twinkle * horizonFade;
            }

            float CloudRawDensity(float2 cloudUV, out float fineShape)
            {
                float2 warp = float2(
                    Fbm(cloudUV * 0.52 + 7.13),
                    Fbm(cloudUV * 0.52 + float2(31.7, 19.2))) - 0.5;
                float proceduralShape = Fbm(cloudUV + warp * _TODCloudDistortion);
                float proceduralDetail = Fbm(
                    cloudUV * _TODCloudDetailScale - warp * 0.75 + float2(12.4, -8.1));

                // StylizedWeather uses a dedicated broad cloud texture, the
                // same family again at a much higher frequency for erosion,
                // and a cellular texture only to break up contrast. Preserve
                // that separation instead of asking several overlapping FBM
                // sliders to do the same job.
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
                fineShape = lerp(proceduralDetail, textureDetail, _TODCloudTexturesEnabled);
                float erodedDetail = lerp(0.5, fineShape, _TODCloudErosion);
                float density = broadShape - (1.0 - erodedDetail) * _TODCloudErosion * 0.42;
                return density;
            }

            float4 ProceduralClouds(
                float3 viewDirection,
                out float rimProbe,
                out float2 primaryCloudUV)
            {
                // Intersect the view ray with two horizontal high-cloud planes.
                // The lower bound only caps the virtually infinite distance at
                // the horizon; it does not wrap the layer onto the sky dome.
                float rayHeight = max(0.12, viewDirection.y + 0.08);
                float layerDistance = _TODCloudAltitude / rayHeight;
                float2 wind = _Time.y * _TODCloudSpeed;
                float2 cloudUV = viewDirection.xz * layerDistance *
                    (_TODCloudScale * 0.018) + wind;
                primaryCloudUV = cloudUV;
                float fineShape;
                float density = CloudRawDensity(cloudUV, fineShape);
                float threshold = lerp(0.64, 0.30, _TODCloudCoverage);
                float edge = max(0.012, _TODCloudSoftness * 0.38);
                float coverageGate = smoothstep(0.0, 0.02, _TODCloudCoverage);
                float cloud1 = smoothstep(threshold - edge, threshold + edge, density);
                // Coverage is a semantic amount, not only a threshold bias:
                // zero must be guaranteed to mean a completely clear sky.
                cloud1 *= coverageGate;
                cloud1 *= lerp(0.72, 1.0, smoothstep(0.22, 0.82, fineShape));

                // UE's sky uses a second independently moving cloud layer. A
                // fixed rotation prevents both copies of the same source noise
                // from visibly lining up while keeping the controls compact.
                const float layer2Cos = 0.819152;
                const float layer2Sin = 0.573576;
                float2 layer2Direction = float2(
                    viewDirection.x * layer2Cos - viewDirection.z * layer2Sin,
                    viewDirection.x * layer2Sin + viewDirection.z * layer2Cos);
                float layer2Distance = _TODCloudLayer2Altitude / rayHeight;
                float2 layer2UV = layer2Direction * layer2Distance *
                    (_TODCloudLayer2Scale * 0.018) +
                    _Time.y * _TODCloudLayer2Speed + float2(13.71, -8.43);
                float layer2Fine;
                float layer2Density = CloudRawDensity(layer2UV, layer2Fine);
                float layer2Coverage = saturate(
                    _TODCloudCoverage + _TODCloudLayer2CoverageOffset);
                float layer2Threshold = lerp(0.64, 0.30, layer2Coverage);
                float layer2Edge = edge * 1.35;
                float cloud2 = smoothstep(
                    layer2Threshold - layer2Edge,
                    layer2Threshold + layer2Edge,
                    layer2Density);
                cloud2 *= coverageGate * _TODCloudLayer2Enabled *
                    _TODCloudLayer2Opacity;
                cloud2 *= lerp(0.68, 1.0, smoothstep(0.18, 0.8, layer2Fine));

                float cloud = 1.0 - (1.0 - cloud1) * (1.0 - cloud2);

                float2 lightDirectionUV = _TODSunDir.xz;
                lightDirectionUV /= max(0.0001, length(lightDirectionUV));

                float rimDetail;
                float rimDensity = CloudRawDensity(
                    cloudUV + lightDirectionUV * _TODCloudRimWidth,
                    rimDetail);
                float rimProbe1 = smoothstep(
                    threshold - edge,
                    threshold + edge,
                    rimDensity) * coverageGate;
                float layer2RimDetail;
                float layer2RimDensity = CloudRawDensity(
                    layer2UV + lightDirectionUV * _TODCloudRimWidth,
                    layer2RimDetail);
                float rimProbe2 = smoothstep(
                    layer2Threshold - layer2Edge,
                    layer2Threshold + layer2Edge,
                    layer2RimDensity) * coverageGate *
                    _TODCloudLayer2Enabled * _TODCloudLayer2Opacity;
                rimProbe = 1.0 - (1.0 - rimProbe1) * (1.0 - rimProbe2);

                float grazingShadow = lerp(
                    0.38,
                    1.65,
                    1.0 - saturate(abs(_TODSunDir.y)));
                float shadowDetail;
                float shadowProbe = CloudRawDensity(
                    cloudUV + lightDirectionUV * _TODCloudSelfShadowDistance *
                        grazingShadow * 0.08,
                    shadowDetail);
                float layer2ShadowDetail;
                float layer2ShadowProbe = CloudRawDensity(
                    layer2UV + lightDirectionUV * _TODCloudSelfShadowDistance *
                        grazingShadow * 0.08,
                    layer2ShadowDetail);
                shadowProbe = max(
                    shadowProbe,
                    layer2ShadowProbe * _TODCloudLayer2Enabled *
                        _TODCloudLayer2Opacity);

                float latitude = saturate(viewDirection.y);
                float latitudeBlend = smoothstep(
                    _TODCloudLatitudePosition - _TODCloudLatitudeWidth,
                    _TODCloudLatitudePosition + _TODCloudLatitudeWidth,
                    latitude);
                float distribution = lerp(
                    _TODCloudHorizonDensity,
                    _TODCloudZenithDensity,
                    latitudeBlend);
                cloud *= distribution * _TODCloudDensityMultiplier;
                cloud *= smoothstep(-0.015, _TODCloudHorizonFade, viewDirection.y);
                float layer2Fog = _TODCloudLayer2FogBlend *
                    pow(1.0 - saturate(viewDirection.y), 2.0);
                cloud = lerp(cloud, cloud * 0.72, layer2Fog);
                return float4(
                    saturate(cloud),
                    saturate(max(density, layer2Density * _TODCloudLayer2Opacity)),
                    saturate(lerp(fineShape, layer2Fine, cloud2)),
                    saturate(shadowProbe));
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

                float horizonWidth = max(0.0001, _TODHorizonWidth);
                float groundBlend = smoothstep(-horizonWidth, horizonWidth, height);
                float3 sky = lerp(_TODGroundColor, upperSky, groundBlend);

                // Width expands around y=0 in both directions instead of only
                // appearing to push the lower gradient downwards.
                float horizon = 1.0 - smoothstep(0.0, horizonWidth, abs(height));
                float horizonBlend = saturate(horizon * min(_TODHorizonIntensity, 1.0));
                sky = lerp(sky, _TODHorizonColor, horizonBlend);
                sky += _TODHorizonColor * horizon * max(0.0, _TODHorizonIntensity - 1.0);

                float3 sunDirection = normalize(_TODSunDir);
                float3 moonDirection = normalize(_TODMoonDir);
                float sunDot = dot(viewDirection, sunDirection);
                float moonDot = dot(viewDirection, moonDirection);

                // Art-directable Mie scattering retaining its defining forward
                // lobe and the longer optical path near the horizon.
                float airMass = lerp(
                    1.0,
                    max(1.0, _TODMieHorizonBoost),
                    pow(1.0 - saturate(abs(height)), 2.0));
                float mieOpticalDepth = max(0.0, _TODMieOpticalDepth) * airMass;
                float mieTransmittance = exp(-mieOpticalDepth * 0.18);
                float mieSunPhase = min(
                    12.0,
                    HenyeyGreenstein(sunDot, _TODMieAnisotropy));
                float mieSunCore = pow(saturate(sunDot), _TODSunScatterPower);
                float mieSun = (mieSunPhase * 0.18 + mieSunCore) *
                    (1.0 - mieTransmittance) * _TODSunScatterIntensity;
                float mieMoonPhase = min(
                    8.0,
                    HenyeyGreenstein(moonDot, min(_TODMieAnisotropy, 0.82)));
                float mieMoon = mieMoonPhase * (1.0 - mieTransmittance) *
                    _TODMieMoonAmount * _TODMoonIntensity * 0.12;
                sky += _TODSunScatterColor *
                    (mieSun * max(0.0, _TODSunIntensity) + mieMoon) *
                    smoothstep(-0.08, 0.06, height);

                // A broad sun-facing wash supplies the painterly transition
                // visible around large cloud banks. Unlike the narrow halo it
                // covers a wide sky region and becomes more horizon-weighted
                // around dawn and dusk.
                float sunWashFacing = pow(
                    saturate(sunDot * 0.5 + 0.5),
                    _TODSunWashPower);
                float sunWashHorizon = lerp(
                    1.0,
                    pow(1.0 - saturate(abs(height)), 1.6),
                    _TODSunWashHorizonWeight);
                float sunWashVisibility = smoothstep(-0.24, 0.08, sunDirection.y);
                sky += _TODSunWashColor * sunWashFacing * sunWashHorizon *
                    sunWashVisibility * _TODSunWashIntensity * 0.32;
                float2 viewHorizontal = viewDirection.xz;
                float2 sunHorizontal = _TODSunDir.xz;
                viewHorizontal /= max(length(viewHorizontal), 0.0001);
                float sunHorizontalLength = length(sunHorizontal);
                sunHorizontal /= max(sunHorizontalLength, 0.0001);

                // The old version only focused the XZ azimuth, while its vertical
                // extent still came from the full horizon band. At high focus this
                // collapsed into a visible vertical pillar. Keep the azimuth lobe
                // deliberately wide and give it an independent, narrow vertical
                // falloff so the result reads as a horizontal horizon glow.
                float horizontalSunGlow = pow(
                    saturate(dot(viewHorizontal, sunHorizontal)),
                    max(0.1, _TODHorizonSunGlowPower * 0.35));
                float verticalGlowWidth = max(
                    0.003,
                    min(horizonWidth * 0.28, 0.045));
                float verticalSunGlow = exp(
                    -pow(abs(height) / verticalGlowWidth, 2.0));
                float horizonSunSpot =
                    horizontalSunGlow * verticalSunGlow * saturate(sunHorizontalLength * 8.0);
                sky += _TODHorizonColor * horizonSunSpot * _TODHorizonSunGlow;

                // Keep the atmospheric background separate from discrete celestial
                // objects. Thin clouds may transmit the broad Mie/horizon glow, but
                // their optical depth must suppress stars and the hard sun/moon discs
                // much more strongly or those objects read as being pasted in front.
                float3 atmosphereSky = sky;

                if (_TODStarsEnabled > 0.5)
                    sky += _TODStarsColor * ProceduralStars(viewDirection) * _TODStarsIntensity;

                float sunDisk = SoftDisk(sunDot, _TODSunSize, _TODSunSoftness);
                float sunHalo = SoftHalo(sunDot, _TODSunHaloSize);
                sky += _TODSunHaloColor * sunHalo * _TODSunHaloIntensity * _TODSunIntensity;
                float sunVisibility = saturate(_TODSunIntensity * 4.0);
                float sunAngularDistance = sqrt(max(0.0, 2.0 * (1.0 - saturate(sunDot))));
                float sunCenter = saturate(1.0 - sunAngularDistance / max(0.0001, _TODSunSize));
                float3 hotSunCore = max(_TODSunColor, float3(1.16, 1.03, 0.82));
                float3 sunSurface = lerp(
                    _TODSunColor,
                    hotSunCore,
                    pow(sunCenter, 0.38)) * (0.78 + _TODSunIntensity * 0.82);
                sky = lerp(sky, max(sky, sunSurface), sunDisk * sunVisibility);

                float moonDisk = SoftDisk(moonDot, _TODMoonSize, _TODMoonSoftness);
                float moonHalo = SoftHalo(moonDot, _TODMoonHaloSize);
                float phaseMask = MoonPhaseMask(viewDirection, moonDirection, saturate(_TODMoonPhase));
                sky += _TODMoonHaloColor * moonHalo * _TODMoonHaloIntensity * _TODMoonIntensity;
                float moonAltitudeFade = lerp(
                    1.0,
                    smoothstep(-0.06, 0.14, moonDirection.y),
                    _TODMoonAtmosphereBlend);
                float moonVisibility = saturate(_TODMoonIntensity * 4.0) * moonAltitudeFade;
                float moonAngularDistance = sqrt(max(0.0, 2.0 * (1.0 - saturate(moonDot))));
                float moonLimb = sqrt(saturate(
                    1.0 - pow(moonAngularDistance / max(0.0001, _TODMoonSize), 2.0)));
                float2 moonUV = MoonDiscCoordinates(viewDirection, moonDirection);
                float moonLargeDetail = Fbm(moonUV * _TODMoonSurfaceScale + 37.2);
                float moonFineDetail = ValueNoise(
                    moonUV * (_TODMoonSurfaceScale * 3.7) - 11.8);
                float moonDetail = lerp(
                    1.0,
                    lerp(0.68, 1.12, moonLargeDetail) *
                        lerp(0.86, 1.06, moonFineDetail),
                    _TODMoonSurfaceDetail);
                float3 moonDarkSurface = lerp(
                    sky,
                    _TODMoonColor * _TODMoonEarthshine * moonDetail,
                    0.30 + _TODMoonEarthshine * 0.28);
                float3 moonLitSurface = _TODMoonColor *
                    (0.68 + _TODMoonIntensity * 1.35) *
                    lerp(0.78, 1.04, moonLimb) * moonDetail;
                float moonHorizonBlend = _TODMoonAtmosphereBlend *
                    (1.0 - smoothstep(0.0, 0.22, moonDirection.y));
                moonLitSurface = lerp(
                    moonLitSurface,
                    moonLitSurface * 0.55 + _TODHorizonColor * 0.45,
                    moonHorizonBlend);
                float3 moonSurface = lerp(moonDarkSurface, moonLitSurface, phaseMask);
                sky = lerp(sky, moonSurface, moonDisk * moonVisibility);

                if (_TODCloudsEnabled > 0.5)
                {
                    float rimProbe;
                    float2 primaryCloudUV;
                    float4 cloudData = ProceduralClouds(
                        viewDirection,
                        rimProbe,
                        primaryCloudUV);
                    float cloudDensity = cloudData.x;

                    // Frostbite-inspired single-segment participating media:
                    // Beer-Lambert transmittance plus the analytical integral
                    // S * (1 - exp(-sigmaE * d)) / sigmaE. This remains a cheap
                    // 2D high-cloud layer, not a volumetric ray marcher.
                    float3 sigmaS = max(0.0, _TODCloudScatteringCoeff) * cloudDensity;
                    float3 sigmaA = max(0.0, _TODCloudAbsorptionCoeff) * cloudDensity;
                    float3 sigmaE = sigmaS + sigmaA;
                    float3 safeSigmaE = max(sigmaE, 0.0001);
                    float grazingPath = _TODCloudThickness /
                        max(0.16, viewDirection.y + 0.22);
                    float3 physicalTransmittance = exp(-sigmaE * grazingPath);
                    float3 transmittance = lerp(
                        1.0.xxx,
                        physicalTransmittance,
                        _TODCloudOpacity);
                    float meanTransmittance = dot(
                        transmittance,
                        float3(0.333333, 0.333333, 0.333333));

                    float sunPhase = lerp(
                        HenyeyGreenstein(sunDot, _TODCloudPhaseBackward),
                        HenyeyGreenstein(sunDot, _TODCloudPhaseForward),
                        _TODCloudPhaseBlend) * 0.079577472;
                    float moonPhase = lerp(
                        HenyeyGreenstein(moonDot, _TODCloudPhaseBackward),
                        HenyeyGreenstein(moonDot, _TODCloudPhaseForward),
                        _TODCloudPhaseBlend) * 0.079577472;
                    sunPhase = min(sunPhase, 4.0);
                    moonPhase = min(moonPhase, 3.0);

                    float3 sunIlluminance = _TODSunColor *
                        _TODSunIntensity * _TODCloudSunLighting * sunPhase;
                    float3 moonIlluminance = _TODMoonColor *
                        _TODMoonIntensity * _TODCloudMoonLighting * moonPhase;
                    float3 source = (sunIlluminance + moonIlluminance) * sigmaS;
                    float3 directLuminance =
                        source * (1.0 - physicalTransmittance) /
                        safeSigmaE * _TODCloudOpacity;

                    float cloudThreshold = lerp(0.64, 0.30, _TODCloudCoverage);
                    float shadowEdge = max(0.015, _TODCloudSoftness * 0.45);
                    float shadowOccluder = smoothstep(
                        cloudThreshold - shadowEdge,
                        cloudThreshold + shadowEdge,
                        cloudData.w);
                    float bodyDepth = smoothstep(0.12, 0.92, cloudDensity);
                    float selfShadow = exp(
                        -shadowOccluder * _TODCloudSelfShadowStrength *
                        lerp(0.35, 1.15, bodyDepth));
                    float detailLight = smoothstep(0.16, 0.88, cloudData.z);
                    float internalLightVariation = lerp(0.68, 1.14, detailLight);
                    directLuminance *=
                        lerp(1.0, selfShadow, 0.86) * internalLightVariation;

                    float3 lostLight = 1.0 - physicalTransmittance;
                    float3 singleScatteringAlbedo = sigmaS / safeSigmaE;
                    float3 ambientLuminance = _TODCloudAmbientColor *
                        _TODCloudAmbientIntensity * lostLight * singleScatteringAlbedo *
                        lerp(0.76, 1.08, detailLight) * _TODCloudOpacity;
                    float3 multipleLuminance = _TODCloudColor *
                        _TODCloudMultipleScattering * lostLight *
                        (0.18 + 0.32 * singleScatteringAlbedo) *
                        _TODCloudOpacity;

                    float lightFacing = saturate(
                        sunPhase * _TODSunIntensity * 0.09 +
                        moonPhase * _TODMoonIntensity * 0.06);
                    float3 artisticCloudTint = lerp(
                        _TODCloudShadowColor,
                        _TODCloudColor,
                        lightFacing);
                    float frontLight = saturate(-sunDot * 0.5 + 0.5);
                    float wrappedFrontLight = saturate(
                        (frontLight + _TODCloudLightWrap) /
                        (1.0 + _TODCloudLightWrap));
                    float continuousLight = saturate(
                        wrappedFrontLight * selfShadow *
                        (0.35 + 0.65 * saturate(_TODSunIntensity)));
                    float steppedLight = StylizedLightSteps(continuousLight);
                    float artisticLight = lerp(
                        continuousLight,
                        steppedLight,
                        _TODCloudStylization);
                    float frontLitAmount = saturate(
                        0.18 + artisticLight * 0.92 -
                        cloudData.y * 0.28);
                    float backLitAmount = saturate(
                        pow(saturate(sunDot), 2.0) * _TODSunIntensity +
                        lightFacing * 0.35);
                    float3 frontTint = lerp(
                        _TODCloudFrontDarkColor,
                        _TODCloudFrontLitColor,
                        frontLitAmount);
                    float3 backTint = lerp(
                        _TODCloudBackDarkColor,
                        _TODCloudBackLitColor,
                        backLitAmount);
                    float backView = smoothstep(-0.18, 0.42, sunDot);
                    float3 fourWayTint = lerp(frontTint, backTint, backView);
                    artisticCloudTint = lerp(
                        artisticCloudTint,
                        fourWayTint,
                        _TODCloudDirectionalColorAmount);
                    float underside = bodyDepth *
                        lerp(0.38, 1.0, 1.0 - saturate(viewDirection.y));
                    artisticCloudTint *= lerp(
                        1.0.xxx,
                        max(float3(0.08, 0.08, 0.08), _TODCloudShadowColor),
                        underside * _TODCloudUndersideStrength);
                    // Apply the posterized light result to energy as well as
                    // hue. Previously the four-way colors changed, but the
                    // analytical scattering term stayed almost uniform, which
                    // made large cloud banks read as a single flat cutout.
                    float stylizedEnergy = lerp(
                        1.0,
                        lerp(0.38, 1.16, artisticLight),
                        _TODCloudStylization);
                    float bodySculpt = lerp(
                        1.0,
                        lerp(0.62, 1.08, detailLight) *
                            lerp(1.0, 0.72, bodyDepth),
                        _TODCloudStylization);
                    float3 cloudLuminance =
                        (directLuminance + ambientLuminance + multipleLuminance) *
                        artisticCloudTint * stylizedEnergy * bodySculpt;

                    // Directional shifted-mask rim: only the sun-facing edge
                    // survives. It uses the exact same spherical UVs as the
                    // cloud mask on the same horizontal plane, avoiding the
                    // unrelated vertical threshold outline from the old rim.
                    float rimBand = saturate((cloudDensity - rimProbe) * 5.0);
                    rimBand *= smoothstep(
                        _TODCloudHorizonFade * 0.55,
                        _TODCloudHorizonFade * 1.8,
                        viewDirection.y);
                    float rimFacing = smoothstep(-0.18, 0.72, sunDot);
                    float3 rimLuminance = _TODCloudRimColor *
                        rimBand * rimFacing * _TODCloudRimIntensity *
                        _TODSunIntensity * _TODCloudOpacity;
                    cloudLuminance += rimLuminance;

                    float transmissionWindow =
                        pow(saturate(meanTransmittance), 0.42) *
                        (1.0 - meanTransmittance) * 2.1;
                    float transmissionFacing = pow(
                        saturate(sunDot),
                        _TODCloudSunTransmissionPower);
                    float edgeTransmission = lerp(
                        1.0,
                        0.28,
                        bodyDepth);
                    float3 transmissionLuminance =
                        _TODSunColor * _TODCloudBackLitColor *
                        transmissionWindow * transmissionFacing *
                        edgeTransmission * _TODCloudSunTransmission *
                        _TODSunIntensity * _TODCloudOpacity;
                    cloudLuminance += transmissionLuminance;

                    // Optional weather-event lightning. It illuminates the
                    // cloud body rather than drawing a billboard bolt; the UE
                    // GlowPos texture supplies an irregular spatial mask and a
                    // short multi-pulse envelope supplies the flash behavior.
                    if (_TODCloudLightningEnabled > 0.5)
                    {
                        float strikePeriod = 60.0 /
                            max(0.001, _TODCloudLightningFrequency);
                        float strikeIndex = floor(_Time.y / strikePeriod);
                        float strikeTime = _Time.y - strikeIndex * strikePeriod;
                        float strikeEnvelope = 1.0 - smoothstep(
                            0.0,
                            _TODCloudLightningDuration,
                            strikeTime);
                        float pulseIndex = floor(strikeTime * 28.0);
                        float pulseNoise = Hash21(float2(strikeIndex, pulseIndex));
                        float multiPulse = lerp(0.32, 1.0, step(0.42, pulseNoise));
                        float2 strikeOffset = float2(
                            Hash21(float2(strikeIndex, 17.3)),
                            Hash21(float2(41.7, strikeIndex)));
                        float2 lightningUV = primaryCloudUV *
                            _TODCloudLightningScale + strikeOffset +
                            _Time.y * _TODCloudLightningGlowSpeed;
                        float3 glowSample = SAMPLE_TEXTURE2D(
                            _TODCloudLightningTexture,
                            sampler_TODCloudLightningTexture,
                            lightningUV).rgb;
                        float glowMask = saturate(max(
                            glowSample.r,
                            max(glowSample.g, glowSample.b)));
                        glowMask = smoothstep(0.08, 0.82, glowMask);
                        float internalFlash = strikeEnvelope * multiPulse *
                            lerp(0.22, 1.0, glowMask) * cloudDensity *
                            lerp(0.45, 1.0, bodyDepth);
                        cloudLuminance += _TODCloudLightningColor *
                            internalFlash * _TODCloudLightningIntensity;
                    }

                    float cloudOpacity = saturate((1.0 - meanTransmittance) * 1.35);

                    // The sun, moon and stars are effectively at infinity and sit
                    // behind the complete cloud layer. Re-apply the celestial delta
                    // over the atmosphere with a longer optical path so cloud bodies
                    // can genuinely cover their discs while thin edges still glow.
                    float3 celestialContribution = sky - atmosphereSky;
                    float celestialOpticalTransmission = pow(
                        saturate(meanTransmittance),
                        lerp(3.0, 8.0, _TODCloudOpacity));
                    float celestialCoverage = smoothstep(
                        0.015,
                        0.35,
                        cloudDensity);
                    float celestialCoverageTransmission =
                        1.0 - celestialCoverage *
                        saturate(_TODCloudOpacity * 2.0);
                    float celestialTransmission = min(
                        celestialOpticalTransmission,
                        celestialCoverageTransmission);
                    sky = atmosphereSky +
                        celestialContribution * celestialTransmission;

                    float aerialAmount = _TODCloudAerialPerspective *
                        cloudOpacity * pow(1.0 - saturate(viewDirection.y), 2.0);
                    cloudLuminance = lerp(
                        cloudLuminance,
                        cloudLuminance * 0.62 + _TODHorizonColor * cloudOpacity * 0.38,
                        aerialAmount);
                    sky = sky * transmittance + cloudLuminance;
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
