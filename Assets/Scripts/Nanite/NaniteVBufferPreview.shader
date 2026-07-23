Shader "Nanite/VBufferPreview"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        Pass
        {
            ZWrite On
            ZTest LEqual
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float3 positionOS : POSITION;
                float4 clusterMeta : TEXCOORD1; // x=cluster, y=page, z=triangleLocal
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 meta : TEXCOORD0;
            };

            Varyings vert(Attributes input)
            {
                Varyings o;
                float3 positionWS = TransformObjectToWorld(input.positionOS);
                o.positionCS = TransformWorldToHClip(positionWS);
                o.meta = input.clusterMeta.xyz;
                return o;
            }

            float3 HashToColor(uint id)
            {
                uint h1 = id * 747796405u + 2891336453u;
                uint h2 = h1 ^ (h1 >> 16);
                uint h3 = h2 * 2246822519u;
                return frac(float3(h1, h2, h3) / 65535.0);
            }

            float4 frag(Varyings input) : SV_Target
            {
                uint cluster = (uint)max(0.0, round(input.meta.x));
                uint page = (uint)max(0.0, round(input.meta.y));
                uint tri = (uint)max(0.0, round(input.meta.z));
                uint packedId = (page * 73856093u) ^ (cluster * 19349663u) ^ (tri * 83492791u);

                float3 idColor = HashToColor(packedId);

                float depth01 = input.positionCS.z / max(input.positionCS.w, 1e-6);
            #if defined(UNITY_REVERSED_Z)
                depth01 = 1.0 - depth01;
            #endif
                depth01 = saturate(depth01);

                // RGB: visibility-id hash, A: depth(0-1)
                return float4(idColor, depth01);
            }
            ENDHLSL
        }
    }
}
