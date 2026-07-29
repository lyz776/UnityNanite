Shader "Nanite/VBufferShadowCaster"
{
    Properties
    {
        _Cutoff ("Alpha Cutoff", Range(0, 1)) = 0
        [NoScaleOffset] _BaseMap ("Base Map", 2D) = "white" {}
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" }

        Pass
        {
            Name "NaniteShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            // This is a procedural draw, so Unity cannot apply the automatic
            // winding correction used by MeshRenderer shadow casters. Match the
            // Formal VBuffer path and keep Page geometry two-sided.
            Cull Off
            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma target 4.5
            #pragma multi_compile_local _ NANITE_PACKED_DIRECT_DIAGNOSTIC
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #pragma shader_feature_local_fragment _ALPHATEST_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/SurfaceInput.hlsl"
            StructuredBuffer<uint2> _IndexedShadowSliceData;
            int _UseIndexedShadowDynamicSlice;
            int _IndexedShadowCascadeIndex;
            #define NANITE_COMPACT_CLUSTER_EXTRA_OFFSET (_UseIndexedShadowDynamicSlice != 0 ? _IndexedShadowSliceData[min((uint)_IndexedShadowCascadeIndex, 3u)].y : 0u)
            #include "NaniteCompactDraw.hlsl"

            // 与 URP ShadowCasterPass 一致：法线偏移需要当前阴影光方向/位置。
            float3 _LightDirection;
            float3 _LightPosition;
            float4x4 _NaniteShadowViewProj;

            CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            float4 _BaseColor;
            float _Cutoff;
            CBUFFER_END

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
                #if defined(_ALPHATEST_ON)
                float2 uv : TEXCOORD0;
                #endif
            };

            float3 DecodePositionOS(int logicalVertex)
            {
                int baseOffset = logicalVertex * _VertexStride;
                return float3(
                    _VertexData[baseOffset + 0],
                    _VertexData[baseOffset + 1],
                    _VertexData[baseOffset + 2]);
            }

            float2 DecodeUv(int logicalVertex)
            {
                if (_VertexStride < 5)
                    return 0.0.xx;
                int baseOffset = logicalVertex * _VertexStride;
                return float2(_VertexData[baseOffset + 3], _VertexData[baseOffset + 4]);
            }

            float3 DecodeNormalOS(int logicalVertex)
            {
                if (_VertexStride < 8)
                    return float3(0.0, 1.0, 0.0);
                int baseOffset = logicalVertex * _VertexStride;
                float3 n = float3(
                    _VertexData[baseOffset + 5],
                    _VertexData[baseOffset + 6],
                    _VertexData[baseOffset + 7]);
                return dot(n, n) > 1e-8 ? normalize(n) : float3(0.0, 1.0, 0.0);
            }

            float4 GetShadowPositionHClip(float3 positionOS, float3 normalOS, float4x4 localToWorld)
            {
                float3 positionWS = mul(localToWorld, float4(positionOS, 1.0)).xyz;
                float3x3 m = (float3x3)localToWorld;
                float3 c0 = mul(m, float3(1.0, 0.0, 0.0));
                float3 c1 = mul(m, float3(0.0, 1.0, 0.0));
                float3 c2 = mul(m, float3(0.0, 0.0, 1.0));
                float3 normalWS = normalize(
                    normalOS.x * cross(c1, c2) +
                    normalOS.y * cross(c2, c0) +
                    normalOS.z * cross(c0, c1));

#if _CASTING_PUNCTUAL_LIGHT_SHADOW
                float3 lightDirectionWS = normalize(_LightPosition - positionWS);
#else
                float3 lightDirectionWS = _LightDirection;
#endif

                float3 biasedPositionWS = ApplyShadowBias(positionWS, normalWS, lightDirectionWS);
                float4 positionCS = mul(_NaniteShadowViewProj, float4(biasedPositionWS, 1.0));
                positionCS = ApplyShadowClamping(positionCS);
                return positionCS;
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
                    float3 indexedNormalOS;
                    float2 indexedUvOS;
                    if (NaniteUsePackedPageGeometry())
                    {
                        NaniteResidentVertex indexedResidentVertex =
                            _NaniteResidentVertices[logicalVertex];
                        indexedPositionOS = indexedResidentVertex.positionOS;
                        indexedNormalOS = indexedResidentVertex.normalOS;
                        indexedUvOS = indexedResidentVertex.uv;
                    }
                    else
                    {
                        indexedPositionOS = DecodePositionOS(logicalVertex);
                        indexedNormalOS = DecodeNormalOS(logicalVertex);
                        indexedUvOS = DecodeUv(logicalVertex);
                    }
                    o.positionCS = GetShadowPositionHClip(
                        indexedPositionOS,
                        indexedNormalOS,
                        indexedLocalToWorld);
                    #if defined(_ALPHATEST_ON)
                    o.uv = TRANSFORM_TEX(indexedUvOS, _BaseMap);
                    #endif
                    return o;
                }
                if (!NaniteCompactedTriangleValid(input.vertexID))
                {
                    float nan = asfloat(0x7FC00000u);
                    o.positionCS = float4(nan, nan, nan, nan);
                    #if defined(_ALPHATEST_ON)
                    o.uv = 0.0.xx;
                    #endif
                    return o;
                }
                int triId = NaniteResolveTriangleId(input.vertexID);

                if (_UseCompactedTriIds < 0.5)
                {
                    int cluster = _TriangleCluster[triId];
                    if (cluster < 0 || _ClusterVisible[cluster] == 0)
                    {
                        float nan = asfloat(0x7FC00000u);
                        o.positionCS = float4(nan, nan, nan, nan);
                        #if defined(_ALPHATEST_ON)
                        o.uv = 0.0.xx;
                        #endif
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
                float3 normalOS;
                float2 uvOS;
                if (NaniteUsePackedPageGeometry())
                {
                    NanitePackedVertex packedVertex;
                    if (!NanitePackedLoadVertex(
                            (uint)triId,
                            (uint)NaniteResolveCorner(input.vertexID),
                            packedVertex))
                    {
                        packedVertex.positionOS = asfloat(0x7FC00000u).xxx;
                    }
                    posOS = packedVertex.positionOS;
                    normalOS = packedVertex.normalOS;
                    uvOS = packedVertex.uv;
                }
                else
                {
                    int logicalIndex = NaniteFetchLogicalIndex(_Indices, input.vertexID);
                    posOS = DecodePositionOS(logicalIndex);
                    normalOS = DecodeNormalOS(logicalIndex);
                    uvOS = DecodeUv(logicalIndex);
                }
                o.positionCS = GetShadowPositionHClip(posOS, normalOS, localToWorld);

                #if defined(_ALPHATEST_ON)
                o.uv = TRANSFORM_TEX(uvOS, _BaseMap);
                #endif

                return o;
            }

            half4 frag(Varyings input) : SV_Target
            {
                #if defined(_ALPHATEST_ON)
                Alpha(SampleAlbedoAlpha(input.uv, TEXTURE2D_ARGS(_BaseMap, sampler_BaseMap)).a, _BaseColor, _Cutoff);
                #endif
                return 0;
            }
            ENDHLSL
        }

        // Depth-only merge for the compute software raster queue. URP already
        // has the cascade viewport and native shadow atlas bound when this pass
        // executes, so one indirect quad per covered tile is sufficient.
        Pass
        {
            Name "NaniteHybridSoftwareShadowMerge"
            Tags { "LightMode" = "ShadowCaster" }

            Cull Off
            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vertSoftwareShadowTile
            #pragma fragment fragSoftwareShadowTile

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            Texture2D<uint> _NaniteSoftwareDepth;
            StructuredBuffer<uint> _NaniteSoftwareTileList;
            uint _NaniteSoftwareScreenWidth;
            uint _NaniteSoftwareScreenHeight;
            uint _NaniteSoftwareTileCountX;
            uint _NaniteSoftwareTileSize;
            float4 _NaniteSoftwareViewportOrigin;

            struct SoftwareShadowAttributes
            {
                uint vertexID : SV_VertexID;
                uint instanceID : SV_InstanceID;
            };

            struct SoftwareShadowVaryings
            {
                float4 positionCS : SV_POSITION;
            };

            SoftwareShadowVaryings vertSoftwareShadowTile(SoftwareShadowAttributes input)
            {
                SoftwareShadowVaryings output;
                uint tileIndex = _NaniteSoftwareTileList[input.instanceID];
                uint tileCountX = max(1u, _NaniteSoftwareTileCountX);
                uint2 tileCoord = uint2(tileIndex % tileCountX, tileIndex / tileCountX);
                uint cornerIndex = input.vertexID % 6u;
                float2 corner = float2(
                    (cornerIndex == 1u || cornerIndex >= 4u) ? 1.0 : 0.0,
                    (cornerIndex == 2u || cornerIndex == 3u || cornerIndex == 5u) ? 1.0 : 0.0);
                float2 screenSize = max(
                    float2(_NaniteSoftwareScreenWidth, _NaniteSoftwareScreenHeight),
                    1.0.xx);
                float2 pixelMin = float2(tileCoord) * max(1.0, (float)_NaniteSoftwareTileSize);
                float2 pixelMax = min(
                    pixelMin + max(1.0, (float)_NaniteSoftwareTileSize),
                    screenSize);
                float2 ndc = lerp(pixelMin, pixelMax, corner) / screenSize * 2.0 - 1.0;
                ndc.y = -ndc.y;
                output.positionCS = float4(ndc, UNITY_RAW_FAR_CLIP_VALUE, 1.0);
                return output;
            }

            float fragSoftwareShadowTile(SoftwareShadowVaryings input) : SV_Depth
            {
                int2 localPixel = int2(input.positionCS.xy) -
                    int2(_NaniteSoftwareViewportOrigin.xy);
                if (any(localPixel < 0) ||
                    localPixel.x >= (int)_NaniteSoftwareScreenWidth ||
                    localPixel.y >= (int)_NaniteSoftwareScreenHeight)
                    discard;

                uint depthBits = _NaniteSoftwareDepth.Load(int3(localPixel, 0));
            #if defined(UNITY_REVERSED_Z)
                if (depthBits == 0u)
                    discard;
            #else
                if (depthBits == asuint(1.0))
                    discard;
            #endif
                return asfloat(depthBits);
            }
            ENDHLSL
        }
    }
}
