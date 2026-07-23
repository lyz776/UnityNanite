Shader "Nanite/VBufferDecode"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Transparent+100" }
        Pass
        {
            Cull Off
            ZTest Always
            ZWrite Off
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_NaniteVBufferTex);
            SAMPLER(sampler_NaniteVBufferTex);

            struct Attributes
            {
                uint vertexID : SV_VertexID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings vert(Attributes input)
            {
                Varyings o;
                o.uv = float2((input.vertexID << 1) & 2, input.vertexID & 2);
                o.positionCS = float4(o.uv * 2.0 - 1.0, 0.0, 1.0);
                o.uv.y = 1.0 - o.uv.y;
                return o;
            }

            float3 HashToColor(uint id)
            {
                uint h = id * 1664525u + 1013904223u;
                uint h2 = h ^ (h >> 16);
                uint h3 = h2 * 2246822519u;
                return frac(float3(h, h2, h3) / 65535.0);
            }

            float4 frag(Varyings i) : SV_Target
            {
                float4 encoded = SAMPLE_TEXTURE2D(_NaniteVBufferTex, sampler_NaniteVBufferTex, i.uv);
                float instancePlusOne = encoded.y;
                if (instancePlusOne < 0.5)
                    return float4(0, 0, 0, 0);

                uint triangleId = (uint)max(0.0, round(encoded.z - 1.0));
                uint subMeshId = (uint)max(0.0, round(encoded.w - 1.0));
                uint instance = (uint)max(0.0, round(instancePlusOne - 1.0));
                uint id = (instance * 83492791u) ^ (triangleId * 73856093u) ^ (subMeshId * 19349663u);

                float3 color = HashToColor(id);
                float depth01 = saturate(encoded.x);
                float shade = lerp(1.0, 0.4, depth01);
                return float4(color * shade, 0.85);
            }
            ENDHLSL
        }
    }
}
