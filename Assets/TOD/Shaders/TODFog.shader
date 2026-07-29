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
            float3 _TODFogColor;
            float4 _TODFogDistance;

            float _TODHeightFogEnabled;
            float3 _TODHeightFogColor;
            float4 _TODHeightFogParams;
            float4 _TODHeightFogDistance;

            half4 Frag(Varyings input) : SV_Target
            {
                float2 uv = input.texcoord;
                half4 source = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv);

                float rawDepth = SampleSceneDepth(uv);
                float eyeDepth = LinearEyeDepth(rawDepth, _ZBufferParams);
                float linearDistanceFog = saturate(
                    (eyeDepth - _TODFogDistance.x) /
                    max(0.001, _TODFogDistance.y - _TODFogDistance.x));
                linearDistanceFog = saturate(linearDistanceFog * _TODFogDistance.z) * _TODFogEnabled;

                #if UNITY_REVERSED_Z
                    bool isSky = rawDepth <= 0.00001;
                #else
                    bool isSky = rawDepth >= 0.99999;
                #endif

                float heightFog = 0.0;
                if (_TODHeightFogEnabled > 0.5 && !isSky)
                {
                    float3 worldPosition = ComputeWorldSpacePosition(uv, rawDepth, UNITY_MATRIX_I_VP);
                    float heightWeight = saturate(
                        1.0 + (_TODHeightFogParams.x - worldPosition.y) /
                        max(0.001, _TODHeightFogParams.y));
                    float distanceWeight = saturate(
                        (eyeDepth - _TODHeightFogDistance.x) /
                        max(0.001, _TODHeightFogDistance.y - _TODHeightFogDistance.x));
                    float extinction = eyeDepth / max(1.0, _TODHeightFogParams.y);
                    heightFog = (1.0 - exp(-extinction * _TODHeightFogParams.z));
                    heightFog = saturate(heightFog * heightWeight * distanceWeight);
                }

                half3 color = lerp(source.rgb, _TODFogColor, linearDistanceFog);
                color = lerp(color, _TODHeightFogColor, heightFog);
                return half4(color, source.a);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
