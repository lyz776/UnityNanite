Shader "Hidden/Unity Nanite/GI/Diffuse Composite"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off ZTest Always Cull Off

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/BRDF.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/GBufferCommon.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

        TEXTURE2D_X(_GIIrradiance);
        TEXTURE2D_X(_GISpecular);
        TEXTURE2D_X(_GIGBuffer0);
        TEXTURE2D_X(_GIGBuffer1);
        TEXTURE2D_X(_GIGBuffer2);
        float _GIDiffuseIntensity;
        float _GIStylizedDiffuse;
        float _GIStylizedShadowThreshold;
        float _GIStylizedShadowSoftness;

        half3 ApplyStylizedDiffuse(half3 irradiance)
        {
            if (_GIStylizedDiffuse < 0.5) return irradiance;
            half luminance = dot(irradiance, half3(0.2126h, 0.7152h, 0.0722h));
            half ramp = smoothstep(
                (half)(_GIStylizedShadowThreshold - _GIStylizedShadowSoftness),
                (half)(_GIStylizedShadowThreshold + _GIStylizedShadowSoftness),
                luminance);
            // Keep chroma from the physical transport and only quantize its
            // low-frequency brightness. Specular remains untouched.
            half3 chroma = luminance > 1e-4h ? irradiance / luminance : 0.0h;
            return chroma * lerp(luminance * 0.55h, luminance, ramp);
        }

        half3 LoadDiffuse(uint2 pixel)
        {
            half3 irradiance = ApplyStylizedDiffuse(
                max(0.0h, LOAD_TEXTURE2D_X(_GIIrradiance, pixel).rgb));
            half4 gBuffer0 = LOAD_TEXTURE2D_X(_GIGBuffer0, pixel);
            half4 gBuffer1 = LOAD_TEXTURE2D_X(_GIGBuffer1, pixel);
            uint flags = UnpackGBufferMaterialFlags(gBuffer0.a);
            half reflectivity = (flags & kMaterialFlagSpecularSetup) != 0u
                ? ReflectivitySpecular(gBuffer1.rgb) : saturate(gBuffer1.r);
            return irradiance * gBuffer0.rgb * (1.0h - reflectivity) *
                   (1.0h / PI) * gBuffer1.a * _GIDiffuseIntensity;
        }

        half3 LoadSpecular(uint2 pixel)
        {
            return max(0.0h, LOAD_TEXTURE2D_X(_GISpecular, pixel).rgb);
        }

        half3 LoadSpecularF0(uint2 pixel)
        {
            half4 baseColor = LOAD_TEXTURE2D_X(_GIGBuffer0, pixel);
            half4 material = LOAD_TEXTURE2D_X(_GIGBuffer1, pixel);
            uint flags = UnpackGBufferMaterialFlags(baseColor.a);
            if ((flags & kMaterialFlagSpecularSetup) != 0u)
                return max(0.0h, material.rgb);
            return lerp(0.04h.xxx, baseColor.rgb, saturate(material.r));
        }

        half4 FragLit(Varyings input) : SV_Target
        {
            uint2 pixel = (uint2)input.positionCS.xy;
            half4 gBuffer2 = LOAD_TEXTURE2D_X(_GIGBuffer2, pixel);
            half3 f0 = LoadSpecularF0(pixel);
            half reflectionEnergy = dot(f0, half3(0.333h, 0.333h, 0.333h));
            half smoothness = saturate(gBuffer2.a);
            half3 specular = LoadSpecular(pixel);
            return half4(LoadDiffuse(pixel) + specular, 0.0h);
        }

        half4 FragSimpleLit(Varyings input) : SV_Target
        {
            uint2 pixel = (uint2)input.positionCS.xy;
            half3 irradiance = ApplyStylizedDiffuse(
                max(0.0h, LOAD_TEXTURE2D_X(_GIIrradiance, pixel).rgb));
            half3 albedo = LOAD_TEXTURE2D_X(_GIGBuffer0, pixel).rgb;
            half4 gBuffer2 = LOAD_TEXTURE2D_X(_GIGBuffer2, pixel);
            half3 f0 = LoadSpecularF0(pixel);
            half3 specular = LoadSpecular(pixel);
            return half4(irradiance * albedo * (1.0h / PI) * _GIDiffuseIntensity + specular, 0.0h);
        }
        ENDHLSL

        Pass
        {
            Blend One One
            ColorMask RGB
            Stencil { Ref 32 ReadMask 96 Comp Equal }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragLit
            ENDHLSL
        }

        Pass
        {
            Blend One One
            ColorMask RGB
            Stencil { Ref 64 ReadMask 96 Comp Equal }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragSimpleLit
            ENDHLSL
        }
    }
}
