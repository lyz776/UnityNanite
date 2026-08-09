Shader "Nanite/VBufferDebugResolve"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Overlay" }

        Pass
        {
            Name "DebugResolve"
            Cull Off
            ZTest Always
            ZWrite Off
            Blend Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_local _ NANITE_COMPACT_VBUFFER
            #pragma multi_compile_local _ NANITE_FLOAT2_VBUFFER
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "NaniteVBufferCommon.hlsl"

            CBUFFER_START(NaniteDebugResolveUniforms)
            float _DebugVizMode;
            float _TriangleCount;
            float _InstanceCount;
            float _UseNormalizedIds;
            float2 _NaniteViewInvSize;
            float4 _NaniteVBufferSize;
            CBUFFER_END

            #if defined(NANITE_COMPACT_VBUFFER)
            Texture2D<uint2> _NaniteVBufferTex;
            #elif defined(NANITE_FLOAT2_VBUFFER)
            Texture2D<float2> _NaniteVBufferTex;
            #else
            TEXTURE2D_FLOAT(_NaniteVBufferTex);
            #endif

            StructuredBuffer<int> _TriangleCluster;
            StructuredBuffer<int> _TrianglePage;

            struct Attributes
            {
                uint vertexID : SV_VertexID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                float2 uv = float2((input.vertexID << 1) & 2u, input.vertexID & 2u);
                float2 ndc = uv * 2.0 - 1.0;
                ndc.y = -ndc.y;
                output.positionCS = float4(ndc, UNITY_RAW_FAR_CLIP_VALUE, 1.0);
                return output;
            }

            uint2 VBufferCoord(float2 rawScreenUv)
            {
                float2 size = _NaniteVBufferSize.x > 1.5
                    ? _NaniteVBufferSize.xy
                    : _ScreenParams.xy;
                size = max(size, 1.0);
                return uint2(clamp(floor(rawScreenUv * size), 0.0, size - 1.0));
            }

            float4 frag(Varyings input) : SV_Target
            {
                float2 rawScreenUv = NaniteScreenUvFromPositionCS(
                    input.positionCS,
                    _NaniteViewInvSize);
                uint2 pixelCoord = VBufferCoord(rawScreenUv);

                #if defined(NANITE_COMPACT_VBUFFER)
                uint2 encoded = _NaniteVBufferTex.Load(int3(pixelCoord, 0));
                NaniteDecodedVBufferIds decoded = NaniteDecodeCompactVBufferIds(
                    encoded,
                    max(1, (int)_InstanceCount),
                    max(1, (int)_TriangleCount));
                #elif defined(NANITE_FLOAT2_VBUFFER)
                float2 encoded = _NaniteVBufferTex.Load(int3(pixelCoord, 0));
                NaniteDecodedVBufferIds decoded = NaniteDecodeFloat2VBufferIds(
                    encoded,
                    max(1, (int)_InstanceCount),
                    max(1, (int)_TriangleCount));
                #else
                float4 encoded = _NaniteVBufferTex.Load(int3(pixelCoord, 0));
                NaniteDecodedVBufferIds decoded = NaniteDecodeVBufferIds(
                    encoded,
                    (int)_UseNormalizedIds,
                    max(1, (int)_InstanceCount),
                    max(1, (int)_TriangleCount));
                #endif

                if (decoded.valid == 0)
                    discard;

                uint colorId;
                if ((int)_DebugVizMode == 1)
                    colorId = (uint)decoded.triangleId;
                else if ((int)_DebugVizMode == 2)
                    colorId = (uint)max(0, _TrianglePage[decoded.triangleId]);
                else
                    colorId = (uint)max(0, _TriangleCluster[decoded.triangleId]);

                colorId ^= (uint)decoded.instanceId * 747796405u + 2891336453u;
                return float4(NaniteIntToColor(colorId), 1.0);
            }
            ENDHLSL
        }
    }
}
