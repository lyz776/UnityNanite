Shader "Hidden/RealtimeGI/Composite"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off ZTest Always Cull Off
        Pass
        {
            Name "Realtime GI Deferred Diffuse Injection"
            Blend One One
            ColorMask RGB
            // URP deferred marks PBR Lit/ComplexLit pixels with MaterialLit (bit 5).
            // SimpleLit keeps its original GI until it has a matching reconstruction path.
            Stencil
            {
                Ref 32
                ReadMask 96
                Comp Equal
            }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            TEXTURE2D_X(_GILightingTexture);
            SAMPLER(sampler_GILightingTexture);
            TEXTURE2D_X(_GISpecularLightingTexture);
            TEXTURE2D_X(_GIGBuffer0);
            TEXTURE2D_X(_GIGBuffer1);
            half _GISpecularIntensity;
            half _GIInjectionBlend;

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/GBufferCommon.hlsl"

            half4 Frag(Varyings input) : SV_Target
            {
                uint2 pixel = (uint2)input.positionCS.xy;
                half3 irradiance = LOAD_TEXTURE2D_X(_GILightingTexture, pixel).rgb;
                half3 indirectSpecular = LOAD_TEXTURE2D_X(_GISpecularLightingTexture, pixel).rgb;
                half4 gBuffer0 = LOAD_TEXTURE2D_X(_GIGBuffer0, pixel);
                half4 gBuffer1 = LOAD_TEXTURE2D_X(_GIGBuffer1, pixel);
                uint materialFlags = UnpackGBufferMaterialFlags(gBuffer0.a);
                half reflectivity = (materialFlags & kMaterialFlagSpecularSetup) != 0u
                    ? max(gBuffer1.r, max(gBuffer1.g, gBuffer1.b))
                    : gBuffer1.r;
                half3 brdfDiffuse = gBuffer0.rgb * (1.0h - saturate(reflectivity));
                // The final gather estimates irradiance. Apply the Lambert BRDF exactly once
                // here; the cache itself stores outgoing radiance.
                half3 indirectDiffuse = irradiance * brdfDiffuse * (1.0h / PI) * gBuffer1.a;
                half receivesSpecular = (materialFlags & kMaterialFlagSpecularHighlightsOff) == 0u
                    ? 1.0h : 0.0h;
                half3 injectedSpecular = indirectSpecular * gBuffer1.a *
                    max(_GISpecularIntensity, 0.0h) * receivesSpecular;
                half takeover = saturate(_GIInjectionBlend);
                return half4(max(0.0h, indirectDiffuse + injectedSpecular) * takeover, 0.0h);
            }
            ENDHLSL
        }
    }
}
