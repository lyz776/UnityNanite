Shader "Hidden/RealtimeGI/Composite"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off ZTest Always Cull Off
        Pass
        {
            Name "Realtime GI Composite"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            TEXTURE2D_X(_GILightingTexture);
            SAMPLER(sampler_GILightingTexture);

            half4 Frag(Varyings input) : SV_Target
            {
                float2 uv = input.texcoord;
                half4 source = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv);
                half3 gi = SAMPLE_TEXTURE2D_X(_GILightingTexture, sampler_GILightingTexture, uv).rgb;
                return half4(source.rgb + gi, source.a);
            }
            ENDHLSL
        }
    }
}
