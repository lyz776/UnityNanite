Shader "Nanite/VBufferDepthWrite"
{
    // 仅写深度，不写颜色。用于 Nanite 当前帧深度贡献，供 BuildHZB 使用。
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry+49" }
        Pass
        {
            Cull Back
            ZTest LEqual
            ZWrite On
            ColorMask 0

            HLSLPROGRAM
            #pragma target 4.5
            #pragma multi_compile_local _ NANITE_PACKED_DIRECT_DIAGNOSTIC
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "NaniteCompactDraw.hlsl"

            StructuredBuffer<float> _VertexData;
            StructuredBuffer<int> _Indices;
            StructuredBuffer<int> _TriangleCluster;
            StructuredBuffer<int> _TrianglePage;
            StructuredBuffer<int> _TriangleInstance;
            StructuredBuffer<float4x4> _InstanceLocalToWorld;
            StructuredBuffer<uint> _ClusterVisible;
            #include "NanitePackedPage.hlsl"
            int _VertexStride;
            int _InstanceId;
            float4x4 _LocalToWorld;
            int _UseSceneInstanceBuffer;
            int _UseIndexedClusterRaster;
            int _GeometryVertexCount;

            struct Attributes
            {
                uint vertexID : SV_VertexID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            float3 DecodePositionOS(int logicalVertex)
            {
                int baseOffset = logicalVertex * _VertexStride;
                return float3(
                    _VertexData[baseOffset + 0],
                    _VertexData[baseOffset + 1],
                    _VertexData[baseOffset + 2]);
            }

            Varyings vert(Attributes input)
            {
                Varyings o;
                if (_UseIndexedClusterRaster != 0 && _GeometryVertexCount > 0)
                {
                    int instanceId = (int)(input.vertexID / (uint)_GeometryVertexCount);
                    int logicalVertex = (int)(input.vertexID % (uint)_GeometryVertexCount);
                    float4x4 indexedLocalToWorld = _InstanceLocalToWorld[instanceId];
                    float3 indexedPositionOS;
                    if (NaniteUsePackedPageGeometry())
                        indexedPositionOS = _NaniteResidentVertices[logicalVertex].positionOS;
                    else
                        indexedPositionOS = DecodePositionOS(logicalVertex);
                    float3 indexedPositionWS = mul(indexedLocalToWorld, float4(indexedPositionOS, 1.0)).xyz;
                    o.positionCS = TransformWorldToHClip(indexedPositionWS);
                    return o;
                }
                if (!NaniteCompactedTriangleValid(input.vertexID))
                {
                    float nan = asfloat(0x7FC00000u);
                    o.positionCS = float4(nan, nan, nan, nan);
                    return o;
                }
                int triId = NaniteResolveTriangleId(input.vertexID);

                // compact 开启时列表已过滤；未开启时仍做 VS 剔除兜底。
                if (_UseCompactedTriIds < 0.5)
                {
                    int page = _TrianglePage[triId];
                    int cluster = _TriangleCluster[triId];
                    if (page < 0 || cluster < 0 || _ClusterVisible[cluster] == 0)
                    {
                        float nan = asfloat(0x7FC00000u);
                        o.positionCS = float4(nan, nan, nan, nan);
                        return o;
                    }
                }

                float4x4 localToWorld = _LocalToWorld;
                if (_UseSceneInstanceBuffer != 0)
                {
                    int instanceId = _UseCompactedTriIds > 0.5
                        ? NaniteResolveInstanceId(input.vertexID, 0)
                        : _TriangleInstance[triId];
                    localToWorld = _InstanceLocalToWorld[instanceId];
                }
                float3 posOS;
                if (NaniteUsePackedPageGeometry())
                {
                    if (!NanitePackedLoadPosition((uint)triId, (uint)NaniteResolveCorner(input.vertexID), posOS))
                        posOS = asfloat(0x7FC00000u).xxx;
                }
                else
                {
                    int logicalIndex = NaniteFetchLogicalIndex(_Indices, input.vertexID);
                    posOS = DecodePositionOS(logicalIndex);
                }
                float3 posWS = mul(localToWorld, float4(posOS, 1.0)).xyz;
                o.positionCS = TransformWorldToHClip(posWS);
                return o;
            }

            float4 frag(Varyings i) : SV_Target
            {
                return float4(0, 0, 0, 0);
            }
            ENDHLSL
        }
    }
}
