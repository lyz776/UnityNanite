Shader "Nanite/VBufferPacketRaster"
{
    Properties
    {
        [HideInInspector] _NaniteCullMode ("Nanite Cull Mode", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry+50" }
        Pass
        {
            Name "VBufferDepthTest"
            Cull [_NaniteCullMode]
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
                    if (!NanitePackedLoadPosition(
                            (uint)triId,
                            (uint)NaniteResolveWindingCorner(input.vertexID, localToWorld),
                            posOS))
                        posOS = asfloat(0x7FC00000u).xxx;
                }
                else
                {
                    int logicalIndex = NaniteFetchLogicalIndexWinding(
                        _Indices,
                        input.vertexID,
                        localToWorld);
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
            Cull [_NaniteCullMode]
            ZTest LEqual
            ZWrite On
            Blend Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma multi_compile_local _ NANITE_PACKED_DIRECT_DIAGNOSTIC
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_local _ NANITE_COMPACT_VBUFFER
            #pragma multi_compile_local _ NANITE_FLOAT2_VBUFFER
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
            StructuredBuffer<uint> _IndexedTrianglePackets;
            StructuredBuffer<uint> _IndexedCameraPacketSlice;
            #include "NanitePackedPage.hlsl"

            CBUFFER_START(NaniteRasterUniforms)
            float _VertexStride;
            float _UseSceneInstanceBuffer;
            float _HasTriangleSubMesh;
            float _UseNormalizedIds;
            float _TriangleCount;
            float _InstanceCount;
            float _MaxSubMeshCount;
            int _UseIndexedClusterRaster;
            int _GeometryVertexCount;
            int _IndexedTrianglePacketBase;
            int _IndexedPacketInstanceBits;
            uint _IndexedPacketInstanceMask;
            int _UseIndexedPacketDynamicBase;
            CBUFFER_END

            int VertexStrideInt() { return max(3, (int)_VertexStride); }
            int UseSceneInstanceBufferInt() { return (int)_UseSceneInstanceBuffer; }
            int HasTriangleSubMeshInt() { return (int)_HasTriangleSubMesh; }
            int UseNormalizedIdsInt() { return (int)_UseNormalizedIds; }
            int TriangleCountInt() { return max(0, (int)_TriangleCount); }
            int InstanceCountInt() { return max(0, (int)_InstanceCount); }
            int MaxSubMeshCountInt() { return max(1, (int)_MaxSubMeshCount); }

            float4 ApplyDeterministicDepthTieBreak(float4 positionCS, int triId)
            {
                // Visible clusters are produced by GPU append queues, whose
                // completion order is intentionally undefined.  With LEqual,
                // exactly coplanar trim/material triangles otherwise choose a
                // different VBuffer owner from frame to frame.  Preserve the
                // ordinary MeshRenderer submesh ordering, then use the immutable
                // triangle id as a stable tie break inside one material.  The
                // offset is below a pixel's geometric depth resolution and only
                // changes winners that are effectively equal-depth.
                int subMesh = HasTriangleSubMeshInt() != 0
                    ? max(0, _TriangleSubMesh[triId])
                    : 0;
                // A monotonic 16-bit triangle fraction leaves adjacent/copied
                // triangle ids closer than one depth ULP, so a few coplanar
                // pixels can still alternate with GPU queue order. Hash the full
                // immutable id into resolvable buckets; material order remains
                // the primary key and the result is independent of append order.
                uint triangleRank = asuint(triId);
                triangleRank = (triangleRank ^ 61u) ^ (triangleRank >> 16u);
                triangleRank *= 9u;
                triangleRank ^= triangleRank >> 4u;
                triangleRank *= 0x27d4eb2du;
                triangleRank ^= triangleRank >> 15u;
                triangleRank &= 1023u;
                float materialRank = ((float)subMesh +
                    ((float)triangleRank + 0.5) / 1024.0) /
                    (float)(MaxSubMeshCountInt() + 1);
                const float kTieBreakDepth = 1.0e-5;
                #if UNITY_REVERSED_Z
                positionCS.z += positionCS.w * kTieBreakDepth * materialRank;
                #else
                positionCS.z -= positionCS.w * kTieBreakDepth * materialRank;
                #endif
                return positionCS;
            }

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
                if (_UseIndexedClusterRaster > 0.5)
                {
                    uint packetBase = _UseIndexedPacketDynamicBase != 0
                        ? _IndexedCameraPacketSlice[0]
                        : (uint)max(0, _IndexedTrianglePacketBase);
                    uint packet = _IndexedTrianglePackets[packetBase + input.vertexID / 3u];
                    int triId = (int)(packet >> (uint)_IndexedPacketInstanceBits);
                    int instance = (int)(packet & _IndexedPacketInstanceMask);
                    float4x4 localToWorld = _InstanceLocalToWorld[instance];
                    float3 posOS;
                    if (NaniteUsePackedPageGeometry())
                    {
                        if (!NanitePackedLoadPosition(
                                (uint)triId,
                                (uint)NaniteResolveWindingCorner(input.vertexID, localToWorld),
                                posOS))
                            posOS = asfloat(0x7FC00000u).xxx;
                    }
                    else
                    {
                        int corner = NaniteResolveWindingCorner(input.vertexID, localToWorld);
                        int logicalVertex = _Indices[triId * 3 + corner];
                        posOS = DecodePositionOS(logicalVertex);
                    }
                    float3 posWS = mul(localToWorld, float4(posOS, 1.0)).xyz;
                    o.positionCS = TransformWorldToHClip(posWS);
                    o.positionCS = ApplyDeterministicDepthTieBreak(o.positionCS, triId);
                    o.packedInstance = (float)instance;
                    o.triId = (uint)triId;
                    return o;
                }
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
                    if (!NanitePackedLoadPosition(
                            (uint)triId,
                            (uint)NaniteResolveWindingCorner(input.vertexID, localToWorld),
                            posOS))
                        posOS = asfloat(0x7FC00000u).xxx;
                }
                else
                {
                    int logicalIndex = NaniteFetchLogicalIndexWinding(
                        _Indices,
                        input.vertexID,
                        localToWorld);
                    posOS = DecodePositionOS(logicalIndex);
                }
                float3 posWS = mul(localToWorld, float4(posOS, 1.0)).xyz;
                o.positionCS = TransformWorldToHClip(posWS);
                o.positionCS = ApplyDeterministicDepthTieBreak(o.positionCS, triId);
                o.packedInstance = (float)instance;
                return o;
            }

            #if defined(NANITE_COMPACT_VBUFFER)
            uint2 frag(Varyings i, uint primitiveId : SV_PrimitiveID) : SV_Target
            #elif defined(NANITE_FLOAT2_VBUFFER)
            float2 frag(Varyings i, uint primitiveId : SV_PrimitiveID) : SV_Target
            #else
            float4 frag(Varyings i, uint primitiveId : SV_PrimitiveID) : SV_Target
            #endif
            {
                int triId = (int)i.triId;
                int subMeshId = 0;
                if (HasTriangleSubMeshInt() != 0)
                    subMeshId = max(0, _TriangleSubMesh[triId]);
                int instanceId = max(0, (int)round(i.packedInstance));

                #if defined(NANITE_COMPACT_VBUFFER)
                return NaniteEncodeCompactVBufferIds(instanceId, triId);
                #elif defined(NANITE_FLOAT2_VBUFFER)
                return NaniteEncodeFloat2VBufferIds(
                    instanceId,
                    triId,
                    InstanceCountInt(),
                    TriangleCountInt());
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
            Cull Off
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
                    if (!NanitePackedLoadPosition(
                            (uint)triId,
                            (uint)NaniteResolveWindingCorner(input.vertexID, localToWorld),
                            posOS))
                        posOS = asfloat(0x7FC00000u).xxx;
                }
                else
                {
                    int logicalIndex = NaniteFetchLogicalIndexWinding(
                        _Indices,
                        input.vertexID,
                        localToWorld);
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

        // Unity does not expose SM 6.6 64-bit typed UAV atomics through
        // ShaderLab. The async software rasterizer therefore produces an
        // independent depth/winner pair. The merge draws only software-covered
        // tiles and uses the fixed-function depth test to combine both paths while
        // preserving the exact compact VBuffer ABI consumed by resolve.
        Pass
        {
            Name "HybridSoftwareMerge"
            Cull Off
            ZTest LEqual
            ZWrite On
            Blend Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vertHybridTiles
            #pragma fragment fragHybridMerge

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "NaniteVBufferCommon.hlsl"

            Texture2D<uint> _NaniteSoftwareDepth;
            Texture2D<uint> _NaniteSoftwareWinner;
            StructuredBuffer<uint3> _NaniteSoftwareClusters;
            StructuredBuffer<uint> _NaniteSoftwareTileList;
            StructuredBuffer<int> _TriangleSubMesh;
            uint _NaniteSoftwareScreenWidth;
            uint _NaniteSoftwareScreenHeight;
            uint _NaniteSoftwareTileCountX;
            uint _NaniteSoftwareTileSize;
            float _NaniteHybridFloat2VBuffer;
            float _UseNormalizedIds;
            float _TriangleCount;
            float _InstanceCount;
            float _MaxSubMeshCount;
            float _HasTriangleSubMesh;

            struct Attributes
            {
                uint vertexID : SV_VertexID;
                uint instanceID : SV_InstanceID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            Varyings vertHybridTiles(Attributes input)
            {
                Varyings output;
                // A full-screen merge is the portable correctness baseline. The
                // old instanced tile-quad merge consumed an indirect instance
                // count that Unity could leave at zero across async-compute /
                // graphics queues, so SW depth existed but no visibility IDs
                // reached resolve. Empty pixels discard below; the expensive
                // barycentric work remains confined to the compute tile queue.
                output.positionCS = GetFullScreenTriangleVertexPosition(input.vertexID);
                return output;
            }

            float2 fragHybridMerge(Varyings input, out float outputDepth : SV_Depth) : SV_Target
            {
                uint2 pixel = uint2(input.positionCS.xy);
                if (pixel.x >= _NaniteSoftwareScreenWidth ||
                    pixel.y >= _NaniteSoftwareScreenHeight)
                    discard;

                uint packedWinner = _NaniteSoftwareWinner.Load(int3(pixel, 0));
                if (packedWinner == 0u)
                    discard;

                uint payload = packedWinner - 1u;
                uint softwareClusterIndex = payload >> 7u;
                uint triangleInCluster = payload & 127u;
                uint3 draw = _NaniteSoftwareClusters[softwareClusterIndex];
                if (triangleInCluster >= draw.y)
                    discard;

                outputDepth = asfloat(_NaniteSoftwareDepth.Load(int3(pixel, 0)));
                uint triangleId = draw.x + triangleInCluster;
                // Hybrid is negotiated only for the portable RG32F VBuffer.
                // Keep this pass on one fixed return ABI: a shared material's
                // local keyword state is not a safe render-graph contract when
                // camera, shadow and resolve passes record/execute independently.
                return NaniteEncodeFloat2VBufferIds(
                    (int)draw.z,
                    (int)triangleId,
                    max(1, (int)_InstanceCount),
                    max(1, (int)_TriangleCount));
            }
            ENDHLSL
        }
    }
}
