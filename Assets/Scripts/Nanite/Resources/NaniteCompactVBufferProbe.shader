Shader "Hidden/Nanite/CompactVBufferProbe"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Pass
        {
            Cull Off
            ZTest Always
            ZWrite Off
            Blend Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma only_renderers d3d11
            #pragma vertex Vert
            #pragma fragment Frag

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            Varyings Vert(uint vertexId : SV_VertexID)
            {
                Varyings output;
                float2 position = vertexId == 0u
                    ? float2(-1.0, -1.0)
                    : (vertexId == 1u ? float2(-1.0, 3.0) : float2(3.0, -1.0));
                output.positionCS = float4(position, 0.0, 1.0);
                return output;
            }

            uint2 Frag(Varyings input) : SV_Target
            {
                return uint2(0x13579BDFu, 0x2468ACE0u);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
