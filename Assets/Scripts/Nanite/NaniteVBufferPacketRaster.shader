Shader "Nanite/VBufferPacketRaster"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry+50" }
        Pass
        {
            Name "VBufferDepthTest"
            Cull Back
            ZTest LEqual
            ZWrite On
            Blend Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma multi_compile_local _ NANITE_PACKED_DIRECT_DIAGNOSTIC
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "NaniteVBufferCommon.hlsl"
            #include "NaniteCompactDraw.hlsl"

            StructuredBuffer<float> _VertexData;
            StructuredBuffer<int> _Indices;
            StructuredBuffer<int> _TriangleCluster;
            StructuredBuffer<int> _TriangleSubMesh;
            StructuredBuffer<int> _TriangleInstance;
            StructuredBuffer<float4x4> _InstanceLocalToWorld;
            StructuredBuffer<uint> _ClusterVisible;
            #include "NanitePackedPage.hlsl"
            int _VertexStride;
            int _InstanceId;
            float4x4 _LocalToWorld;
            int _UseSceneInstanceBuffer;
            int _HasTriangleSubMesh;
            int _UseNormalizedIds;
            int _TriangleCount;
            int _InstanceCount;
            int _MaxSubMeshCount;

            struct Attributes
            {
                uint vertexID : SV_VertexID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float packedInstance : TEXCOORD0;
                nointerpolation uint origTriId : TEXCOORD1;
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
                if (!NaniteCompactedTriangleValid(input.vertexID))
                {
                    float nan = asfloat(0x7FC00000u);
                    o.positionCS = float4(nan, nan, nan, nan);
                    o.packedInstance = 0.0;
                    o.origTriId = 0u;
                    return o;
                }
                int triId = NaniteResolveTriangleId(input.vertexID);
                o.origTriId = (uint)max(0, triId);
                o.packedInstance = 0.0;

                if (_UseCompactedTriIds < 0.5)
                {
                    int cluster = _TriangleCluster[triId];
                    if (cluster < 0 || _ClusterVisible[cluster] == 0)
                    {
                        float nan = asfloat(0x7FC00000u);
                        o.positionCS = float4(nan, nan, nan, nan);
                        return o;
                    }
                }

                int instance = _InstanceId;
                float4x4 localToWorld = _LocalToWorld;
                if (_UseSceneInstanceBuffer != 0)
                {
                    instance = _UseCompactedTriIds > 0.5
                        ? NaniteResolveInstanceId(input.vertexID, 0)
                        : _TriangleInstance[triId];
                    localToWorld = _InstanceLocalToWorld[instance];
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
                o.packedInstance = (float)instance;
                return o;
            }

            float4 frag(Varyings i) : SV_Target
            {
                int triId = (int)i.origTriId;
                int subMeshId = 0;
                if (_HasTriangleSubMesh != 0)
                    subMeshId = max(0, _TriangleSubMesh[triId]);
                int instanceId = max(0, (int)round(i.packedInstance));

                // 与硬件深度缓冲一致的 NDC 深度（reversed-Z 下近=1 远=0）
                float depth01 = NanitePackDepth01(i.positionCS);
                return NaniteEncodeVBufferIds(
                    depth01,
                    instanceId,
                    triId,
                    subMeshId,
                    _UseNormalizedIds,
                    _InstanceCount,
                    _TriangleCount,
                    _MaxSubMeshCount);
            }
            ENDHLSL
        }

        // Formal VBuffer：写入独立可见性结果时也必须参与 Nanite 自身深度竞争。
        // 否则 ZTest 只对场景深度生效，不同 Nanite 三角之间会按绘制顺序覆盖，导致近处碎片错乱。
        Pass
        {
            Name "VBufferFormal"
            Cull Back
            ZTest LEqual
            ZWrite On
            Blend Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma multi_compile_local _ NANITE_PACKED_DIRECT_DIAGNOSTIC
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_local _ NANITE_COMPACT_VBUFFER
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "NaniteVBufferCommon.hlsl"
            #include "NaniteCompactDraw.hlsl"

            StructuredBuffer<float> _VertexData;
            StructuredBuffer<int> _Indices;
            StructuredBuffer<int> _TriangleCluster;
            StructuredBuffer<int> _TriangleSubMesh;
            StructuredBuffer<int> _TriangleInstance;
            StructuredBuffer<float4x4> _InstanceLocalToWorld;
            StructuredBuffer<uint> _ClusterVisible;
            #include "NanitePackedPage.hlsl"

            CBUFFER_START(NaniteRasterUniforms)
            float _VertexStride;
            float _UseSceneInstanceBuffer;
            float _HasTriangleSubMesh;
            float _UseNormalizedIds;
            float _TriangleCount;
            float _InstanceCount;
            float _MaxSubMeshCount;
            CBUFFER_END

            int VertexStrideInt() { return max(3, (int)_VertexStride); }
            int UseSceneInstanceBufferInt() { return (int)_UseSceneInstanceBuffer; }
            int HasTriangleSubMeshInt() { return (int)_HasTriangleSubMesh; }
            int UseNormalizedIdsInt() { return (int)_UseNormalizedIds; }
            int TriangleCountInt() { return max(0, (int)_TriangleCount); }
            int InstanceCountInt() { return max(0, (int)_InstanceCount); }
            int MaxSubMeshCountInt() { return max(1, (int)_MaxSubMeshCount); }

            struct Attributes
            {
                uint vertexID : SV_VertexID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float packedInstance : TEXCOORD0;
                nointerpolation uint triId : TEXCOORD1;
            };

            float3 DecodePositionOS(int logicalVertex)
            {
                int stride = VertexStrideInt();
                int baseOffset = logicalVertex * stride;
                return float3(
                    _VertexData[baseOffset + 0],
                    _VertexData[baseOffset + 1],
                    _VertexData[baseOffset + 2]);
            }

            Varyings vert(Attributes input)
            {
                Varyings o;
                if (!NaniteCompactedTriangleValid(input.vertexID))
                {
                    float nan = asfloat(0x7FC00000u);
                    o.positionCS = float4(nan, nan, nan, nan);
                    o.packedInstance = 0.0;
                    o.triId = 0u;
                    return o;
                }
                int triId = NaniteResolveTriangleId(input.vertexID);
                o.triId = (uint)max(0, triId);
                o.packedInstance = 0.0;

                if (_UseCompactedTriIds < 0.5)
                {
                    int cluster = _TriangleCluster[triId];
                    if (cluster < 0 || _ClusterVisible[cluster] == 0)
                    {
                        float nan = asfloat(0x7FC00000u);
                        o.positionCS = float4(nan, nan, nan, nan);
                        return o;
                    }
                }

                int instance = 0;
                float4x4 localToWorld = UNITY_MATRIX_M;
                if (UseSceneInstanceBufferInt() != 0)
                {
                    instance = _UseCompactedTriIds > 0.5
                        ? NaniteResolveInstanceId(input.vertexID, 0)
                        : _TriangleInstance[triId];
                    localToWorld = _InstanceLocalToWorld[instance];
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
                o.packedInstance = (float)instance;
                return o;
            }

            #if defined(NANITE_COMPACT_VBUFFER)
            uint2 frag(Varyings i) : SV_Target
            #else
            float4 frag(Varyings i) : SV_Target
            #endif
            {
                int triId = (int)i.triId;
                int subMeshId = 0;
                if (HasTriangleSubMeshInt() != 0)
                    subMeshId = max(0, _TriangleSubMesh[triId]);
                int instanceId = max(0, (int)round(i.packedInstance));

                #if defined(NANITE_COMPACT_VBUFFER)
                return NaniteEncodeCompactVBufferIds(instanceId, triId);
                #else
                float depth01 = NanitePackDepth01(i.positionCS);
                return NaniteEncodeVBufferIds(
                    depth01,
                    instanceId,
                    triId,
                    subMeshId,
                    UseNormalizedIdsInt(),
                    InstanceCountInt(),
                    TriangleCountInt(),
                    MaxSubMeshCountInt());
                #endif
            }
            ENDHLSL
        }

        // Pass2：Debug 可视化，直接输出 Cluster / Triangle / Page 伪彩色到相机颜色缓冲。
        Pass
        {
            Name "DebugColorViz"
            Cull Back
            ZTest LEqual
            ZWrite On
            Blend Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma multi_compile_local _ NANITE_PACKED_DIRECT_DIAGNOSTIC
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "NaniteVBufferCommon.hlsl"
            #include "NaniteCompactDraw.hlsl"

            StructuredBuffer<float> _VertexData;
            StructuredBuffer<int> _Indices;
            StructuredBuffer<int> _TriangleCluster;
            StructuredBuffer<int> _TrianglePage;
            StructuredBuffer<int> _TriangleSubMesh;
            StructuredBuffer<int> _TriangleInstance;
            StructuredBuffer<float4x4> _InstanceLocalToWorld;
            StructuredBuffer<uint> _ClusterVisible;
            #include "NanitePackedPage.hlsl"

            CBUFFER_START(NaniteRasterUniforms)
            float _VertexStride;
            float _UseSceneInstanceBuffer;
            float _HasTriangleSubMesh;
            float _UseNormalizedIds;
            float _TriangleCount;
            float _InstanceCount;
            float _MaxSubMeshCount;
            float _DebugVizMode;
            CBUFFER_END

            float4x4 _LocalToWorld;
            float _InstanceId;

            int VertexStrideInt() { return max(3, (int)_VertexStride); }
            int UseSceneInstanceBufferInt() { return (int)_UseSceneInstanceBuffer; }
            int DebugVizModeInt() { return (int)_DebugVizMode; }

            struct Attributes
            {
                uint vertexID : SV_VertexID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                nointerpolation uint triId : TEXCOORD0;
                nointerpolation uint instanceId : TEXCOORD1;
            };

            float3 DecodePositionOS(int logicalVertex)
            {
                int stride = VertexStrideInt();
                int baseOffset = logicalVertex * stride;
                return float3(
                    _VertexData[baseOffset + 0],
                    _VertexData[baseOffset + 1],
                    _VertexData[baseOffset + 2]);
            }

            Varyings vert(Attributes input)
            {
                Varyings o;
                if (!NaniteCompactedTriangleValid(input.vertexID))
                {
                    float nan = asfloat(0x7FC00000u);
                    o.positionCS = float4(nan, nan, nan, nan);
                    o.triId = 0u;
                    o.instanceId = 0u;
                    return o;
                }
                int triId = NaniteResolveTriangleId(input.vertexID);
                o.triId = (uint)max(0, triId);
                o.instanceId = 0u;

                if (_UseCompactedTriIds < 0.5)
                {
                    int cluster = _TriangleCluster[triId];
                    if (cluster < 0 || _ClusterVisible[cluster] == 0)
                    {
                        float nan = asfloat(0x7FC00000u);
                        o.positionCS = float4(nan, nan, nan, nan);
                        return o;
                    }
                }

                int instance = (int)_InstanceId;
                float4x4 localToWorld = _LocalToWorld;
                if (UseSceneInstanceBufferInt() != 0)
                {
                    instance = _UseCompactedTriIds > 0.5
                        ? NaniteResolveInstanceId(input.vertexID, 0)
                        : _TriangleInstance[triId];
                    localToWorld = _InstanceLocalToWorld[instance];
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
                o.instanceId = (uint)max(0, instance);
                return o;
            }

            float4 frag(Varyings i) : SV_Target
            {
                int triId = (int)i.triId;
                int mode = DebugVizModeInt();
                uint colorId;
                if (mode == 1)
                {
                    // Triangle
                    colorId = (uint)triId;
                }
                else if (mode == 2)
                {
                    // Page
                    colorId = (uint)max(0, _TrianglePage[triId]);
                }
                else
                {
                    // Cluster（默认）
                    colorId = (uint)max(0, _TriangleCluster[triId]);
                }

                // 混入 instance，避免多实例同色块完全重叠难辨。
                colorId ^= i.instanceId * 747796405u + 2891336453u;
                float3 rgb = NaniteIntToColor(colorId);

                // 轻微深度着色，便于看清体积轮廓。
                float depth01 = NanitePackDepth01(i.positionCS);
            #if defined(UNITY_REVERSED_Z)
                depth01 = 1.0 - depth01;
            #endif
                float shade = lerp(1.0, 0.55, saturate(depth01));
                return float4(rgb * shade, 1.0);
            }
            ENDHLSL
        }
    }
}
