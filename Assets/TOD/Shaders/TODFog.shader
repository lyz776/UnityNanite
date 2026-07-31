Shader "Hidden/Unity Nanite/TOD Fog"
{
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" }
        ZWrite Off
        ZTest Always
        Cull Off

        Pass
        {
            Name "TOD Fog Composite"

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            float _TODFogEnabled;
            float3 _TODFogTopColor;
            float3 _TODFogBottomColor;
            // x: start, y: end, z: density, w: exponential blend
            float4 _TODFogDistance;
            // x: top intensity, y: bottom intensity, z: sky amount, w: power
            float4 _TODFogShape;
            // x: fog height, y: height range
            float4 _TODFogHeight;

            float _TODScreenFogEnabled;
            // x: intensity, y: radius in pixels, z: start, w: end
            float4 _TODScreenFogParams;
            // x: relative depth threshold, y: sky contribution
            float4 _TODScreenFogDepth;

            bool IsSkyDepth(float rawDepth)
            {
                #if UNITY_REVERSED_Z
                    return rawDepth <= 0.00001;
                #else
                    return rawDepth >= 0.99999;
                #endif
            }

            void AddDepthAwareSample(
                float2 uv,
                float2 offset,
                float centerDepth,
                bool centerIsSky,
                inout half3 colorSum,
                inout float weightSum)
            {
                float2 sampleUV = saturate(uv + offset);
                float rawDepth = SampleSceneDepth(sampleUV);
                bool sampleIsSky = IsSkyDepth(rawDepth);
                if (sampleIsSky != centerIsSky)
                    return;

                float sampleDepth = LinearEyeDepth(rawDepth, _ZBufferParams);
                float relativeDifference = centerIsSky
                    ? 0.0
                    : abs(sampleDepth - centerDepth) / max(1.0, centerDepth);
                float weight = exp(
                    -relativeDifference / max(0.0001, _TODScreenFogDepth.x));
                colorSum += SAMPLE_TEXTURE2D_X(
                    _BlitTexture, sampler_LinearClamp, sampleUV).rgb * weight;
                weightSum += weight;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float2 uv = input.texcoord;
                half4 source = SAMPLE_TEXTURE2D_X(
                    _BlitTexture, sampler_LinearClamp, uv);

                float rawDepth = SampleSceneDepth(uv);
                bool isSky = IsSkyDepth(rawDepth);
                float eyeDepth = LinearEyeDepth(rawDepth, _ZBufferParams);

                // 屏幕空间散射层：只在相近深度内取样，避免前景轮廓向远景泄漏。
                if (_TODScreenFogEnabled > 0.5 && _TODScreenFogParams.x > 0.0001)
                {
                    float2 texel = _BlitTexture_TexelSize.xy * _TODScreenFogParams.y;
                    half3 colorSum = source.rgb;
                    float weightSum = 1.0;
                    AddDepthAwareSample(uv, float2( texel.x, 0.0), eyeDepth, isSky, colorSum, weightSum);
                    AddDepthAwareSample(uv, float2(-texel.x, 0.0), eyeDepth, isSky, colorSum, weightSum);
                    AddDepthAwareSample(uv, float2(0.0,  texel.y), eyeDepth, isSky, colorSum, weightSum);
                    AddDepthAwareSample(uv, float2(0.0, -texel.y), eyeDepth, isSky, colorSum, weightSum);
                    AddDepthAwareSample(uv, float2( texel.x,  texel.y), eyeDepth, isSky, colorSum, weightSum);
                    AddDepthAwareSample(uv, float2(-texel.x,  texel.y), eyeDepth, isSky, colorSum, weightSum);
                    AddDepthAwareSample(uv, float2( texel.x, -texel.y), eyeDepth, isSky, colorSum, weightSum);
                    AddDepthAwareSample(uv, float2(-texel.x, -texel.y), eyeDepth, isSky, colorSum, weightSum);

                    float distanceMask = isSky
                        ? _TODScreenFogDepth.y
                        : smoothstep(
                            _TODScreenFogParams.z,
                            _TODScreenFogParams.w,
                            eyeDepth);
                    source.rgb = lerp(
                        source.rgb,
                        colorSum / max(0.0001, weightSum),
                        saturate(distanceMask * _TODScreenFogParams.x));
                }

                float linearFog = saturate(
                    (eyeDepth - _TODFogDistance.x) /
                    max(0.001, _TODFogDistance.y - _TODFogDistance.x));
                float exponentialFog =
                    1.0 - exp(-linearFog * max(0.0, _TODFogDistance.z) * 4.0);
                float distanceFog = lerp(
                    linearFog * max(0.0, _TODFogDistance.z),
                    exponentialFog,
                    saturate(_TODFogDistance.w));

                float3 worldPosition;
                float heightColorBlend;
                float verticalDensity;
                if (isSky)
                {
                    #if UNITY_REVERSED_Z
                        const float farDepth = 0.0001;
                    #else
                        const float farDepth = 0.9999;
                    #endif
                    worldPosition = ComputeWorldSpacePosition(
                        uv, farDepth, UNITY_MATRIX_I_VP);
                    float3 viewDirection = normalize(
                        worldPosition - _WorldSpaceCameraPos);
                    heightColorBlend = saturate(viewDirection.y * 0.5 + 0.5);
                    verticalDensity = _TODFogShape.z;
                    distanceFog = 1.0;
                }
                else
                {
                    worldPosition = ComputeWorldSpacePosition(
                        uv, rawDepth, UNITY_MATRIX_I_VP);
                    float heightRange = max(0.001, _TODFogHeight.y);
                    heightColorBlend = saturate(
                        (worldPosition.y - (_TODFogHeight.x - heightRange)) /
                        (heightRange * 2.0));
                    verticalDensity = exp(
                        -max(0.0, worldPosition.y - _TODFogHeight.x) /
                        heightRange);
                }

                float fogAmount = pow(
                    saturate(distanceFog * verticalDensity),
                    max(0.01, _TODFogShape.w));
                fogAmount *= _TODFogEnabled;

                half3 bottomColor = _TODFogBottomColor * _TODFogShape.y;
                half3 topColor = _TODFogTopColor * _TODFogShape.x;
                half3 fogColor = lerp(bottomColor, topColor, heightColorBlend);
                half3 color = lerp(source.rgb, fogColor, saturate(fogAmount));
                return half4(color, source.a);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
