Shader "Nanite/VBufferLitResolve"
{
    Properties
    {
        _BaseColor ("Base Color", Color) = (1, 1, 1, 1)
        _EmissionColor ("Emission Color", Color) = (0, 0, 0, 1)
        _Cutoff ("Alpha Cutoff", Range(0, 1)) = 0
        [HideInInspector] _AlphaClip ("Alpha Clip", Float) = 0
        _Smoothness ("Smoothness", Range(0, 1)) = 0.5
        _Metallic ("Metallic", Range(0, 1)) = 0
        _BumpScale ("Normal Scale", Float) = 1
        _OcclusionStrength ("Occlusion Strength", Range(0, 1)) = 1
        [NoScaleOffset] _BaseMap ("Base Map", 2D) = "white" {}
        [NoScaleOffset] _BumpMap ("Normal Map", 2D) = "bump" {}
        [NoScaleOffset] _MetallicGlossMap ("Metallic Gloss Map", 2D) = "black" {}
        [NoScaleOffset] _OcclusionMap ("Occlusion Map", 2D) = "white" {}
        [NoScaleOffset] _EmissionMap ("Emission Map", 2D) = "black" {}
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry+110" }

        // Pass0：Nanite 像素写 depth + MaterialLit stencil
        Pass
        {
            Name "NaniteDepthFill"
            Cull Off
            ZTest Always
            ZWrite On
            ColorMask 0
            Blend Off
            Stencil
            {
                Ref 32
                WriteMask 96
                Comp Always
                Pass Replace
                Fail Keep
                ZFail Keep
            }

            HLSLPROGRAM
            #pragma target 4.5
            #pragma multi_compile_local _ NANITE_PACKED_DIRECT_DIAGNOSTIC
            #pragma vertex vertFullscreen
            #pragma fragment fragDepthOnly
            #pragma multi_compile_local _ NANITE_COMPACT_VBUFFER
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/ShaderVariablesFunctions.hlsl"
            #include "NaniteVBufferCommon.hlsl"

            CBUFFER_START(NaniteResolveUniforms)
            float _ResolveMaterialId;
            float _ResolveMaterialMode;
            float _ResolveMaterialFamily;
            float _ResolveAbsorbedFamily;
            float _VertexStride;
            float _MaxSubMeshCount;
            float _TriangleCount;
            float _InstanceCount;
            float _TileCountX;
            float _TileSize;
            float _TileCount;
            float _UseNormalizedIds;
            float2 _NaniteViewInvSize;
            float4 _NaniteVBufferSize; // xy=size, zw=invSize
            CBUFFER_END

            #if defined(NANITE_COMPACT_VBUFFER)
            Texture2D<uint2> _NaniteVBufferTex;
            #else
            TEXTURE2D_FLOAT(_NaniteVBufferTex);
            #endif

            StructuredBuffer<float> _VertexData;
            StructuredBuffer<int> _Indices;
            StructuredBuffer<int> _TriangleSubMesh;
            StructuredBuffer<float4x4> _InstanceLocalToWorld;
            StructuredBuffer<int> _InstanceSubMeshMaterial;
            StructuredBuffer<uint2> _InstanceMaterialRange;
            #include "NanitePackedPage.hlsl"

            struct Attributes { uint vertexID : SV_VertexID; };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            int UseNormalizedIdsInt() { return (int)_UseNormalizedIds; }
            int TriangleCountInt() { return max(0, (int)_TriangleCount); }
            int InstanceCountInt() { return max(0, (int)_InstanceCount); }

            float3 DecodePositionOS(int logicalVertex)
            {
                int stride = max(3, (int)_VertexStride);
                int baseOffset = logicalVertex * stride;
                return float3(
                    _VertexData[baseOffset + 0],
                    _VertexData[baseOffset + 1],
                    _VertexData[baseOffset + 2]);
            }

            uint2 NaniteVBufferCoord(float2 screenUv)
            {
                // 未绑定或非法时回退全屏尺寸，避免 (0,0)→钳成 1x1 导致全屏采同一 texel。
                float2 size = _NaniteVBufferSize.x > 1.5 ? _NaniteVBufferSize.xy : _ScreenParams.xy;
                size = max(size, 1.0);
                return uint2(min(floor(screenUv * size), size - 1.0));
            }

            Varyings vertFullscreen(Attributes input)
            {
                Varyings o;
                float2 uv = float2((input.vertexID << 1) & 2u, input.vertexID & 2u);
                float2 ndc = uv * 2.0 - 1.0;
                ndc.y = -ndc.y;
                o.positionCS = float4(ndc, UNITY_RAW_FAR_CLIP_VALUE, 1.0);
                return o;
            }

            void fragDepthOnly(Varyings input, out float outputDepth : SV_Depth)
            {
                outputDepth = UNITY_RAW_FAR_CLIP_VALUE;

                float2 screenUv = GetNormalizedScreenSpaceUV(input.positionCS);
                uint2 pixelCoord = NaniteVBufferCoord(screenUv);
                #if defined(NANITE_COMPACT_VBUFFER)
                uint2 encoded = _NaniteVBufferTex.Load(int3(pixelCoord, 0));
                NaniteDecodedVBufferIds decoded = NaniteDecodeCompactVBufferIds(
                    encoded, InstanceCountInt(), TriangleCountInt());
                #else
                float4 encoded = _NaniteVBufferTex.Load(int3(pixelCoord, 0));
                NaniteDecodedVBufferIds decoded = NaniteDecodeVBufferIds(
                    encoded, UseNormalizedIdsInt(), InstanceCountInt(), TriangleCountInt());
                #endif
                if (decoded.valid == 0)
                    discard;

                #if defined(NANITE_COMPACT_VBUFFER)
                int triangleId = decoded.triangleId;
                float4x4 localToWorld = _InstanceLocalToWorld[decoded.instanceId];
                float3 p0OS;
                float3 p1OS;
                float3 p2OS;
                if (NaniteUsePackedPageGeometry())
                {
                    if (!NanitePackedLoadTrianglePositions(
                            (uint)triangleId,
                            p0OS,
                            p1OS,
                            p2OS))
                        discard;
                }
                else
                {
                    int i0 = _Indices[triangleId * 3 + 0];
                    int i1 = _Indices[triangleId * 3 + 1];
                    int i2 = _Indices[triangleId * 3 + 2];
                    p0OS = DecodePositionOS(i0);
                    p1OS = DecodePositionOS(i1);
                    p2OS = DecodePositionOS(i2);
                }
                float3 p0WS = mul(localToWorld, float4(p0OS, 1.0)).xyz;
                float3 p1WS = mul(localToWorld, float4(p1OS, 1.0)).xyz;
                float3 p2WS = mul(localToWorld, float4(p2OS, 1.0)).xyz;
                float4 p0CS = TransformWorldToHClip(p0WS);
                float4 p1CS = TransformWorldToHClip(p1WS);
                float4 p2CS = TransformWorldToHClip(p2WS);
                float2 pixelNdc = NaniteNdcFromScreenUv(screenUv);
                NaniteBarycentrics bary = CalculateTriangleBarycentricsNdc(pixelNdc, p0CS, p1CS, p2CS, _NaniteViewInvSize);
                float barySum = bary.value.x + bary.value.y + bary.value.z;
                if (abs(barySum) < 1e-5)
                    discard;
                bary.value /= barySum;
                float3 positionWS = p0WS * bary.value.x + p1WS * bary.value.y + p2WS * bary.value.z;
                outputDepth = NaniteDeviceDepthFromClip(TransformWorldToHClip(positionWS));
                #else
                outputDepth = encoded.x;
                #endif
            }
            ENDHLSL
        }

        // Pass1：仅写本帧 Nanite 像素；非 Nanite discard 保留本帧已有 GBuffer
        Pass
        {
            Name "NaniteGBufferMerge"
            Cull Off
            ZTest Always
            ZWrite Off
            Blend Off
            Stencil
            {
                Ref 32
                WriteMask 96
                Comp Always
                Pass Replace
                Fail Keep
                ZFail Keep
            }

            HLSLPROGRAM
            #pragma target 4.5
            #pragma multi_compile_local _ NANITE_PACKED_DIRECT_DIAGNOSTIC
            #pragma vertex vertResolve
            #pragma fragment frag
            #pragma multi_compile_local _ NANITE_COMPACT_VBUFFER
            #pragma multi_compile _ NANITE_GBUFFER_DEPTH_SLICE
            #pragma multi_compile _ _LIGHT_LAYERS
            #pragma multi_compile _ SHADOWS_SHADOWMASK
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            #pragma shader_feature_local_fragment _ENVIRONMENTREFLECTIONS_OFF
            #pragma shader_feature_local_fragment _SPECULARHIGHLIGHTS_OFF
            #pragma shader_feature_local _RECEIVE_SHADOWS_OFF

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/ShaderVariablesFunctions.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
            #if defined(NANITE_GBUFFER_DEPTH_SLICE)
            #define GBUFFER_FEATURE_DEPTH 1
            #endif
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/GBufferOutput.hlsl"
            #include "NaniteVBufferCommon.hlsl"

            CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            float4 _BaseColor;
            float4 _EmissionColor;
            float _Cutoff;
            float _AlphaClip;
            float _Smoothness;
            float _Metallic;
            float _BumpScale;
            float _OcclusionStrength;
            float _HasBaseMap;
            float _HasNormalMap;
            float _HasMetallicGlossMap;
            float _HasOcclusionMap;
            float _HasEmissionMap;
            float _EmissionEnabled;
            float _SmoothnessFromAlbedoAlpha;
            CBUFFER_END

            CBUFFER_START(NaniteResolveUniforms)
            float _ResolveMaterialId;
            float _ResolveMaterialMode;
            float _ResolveMaterialFamily;
            float _ResolveAbsorbedFamily;
            float _VertexStride;
            float _MaxSubMeshCount;
            float _TriangleCount;
            float _InstanceCount;
            float _TileCountX;
            float _TileSize;
            float _TileCount;
            float _UseNormalizedIds;
            float _UseTileMaterialMask;
            float _UseCompactedTileBins;
            float2 _NaniteViewInvSize;
            float4 _NaniteVBufferSize; // xy=size, zw=invSize
            CBUFFER_END

            #if defined(NANITE_COMPACT_VBUFFER)
            Texture2D<uint2> _NaniteVBufferTex;
            #else
            TEXTURE2D_FLOAT(_NaniteVBufferTex);
            #endif
            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);
            TEXTURE2D(_BumpMap);
            SAMPLER(sampler_BumpMap);
            TEXTURE2D(_MetallicGlossMap);
            SAMPLER(sampler_MetallicGlossMap);
            TEXTURE2D(_OcclusionMap);
            SAMPLER(sampler_OcclusionMap);
            TEXTURE2D(_EmissionMap);
            SAMPLER(sampler_EmissionMap);

            StructuredBuffer<float> _VertexData;
            StructuredBuffer<int> _Indices;
            StructuredBuffer<int> _TriangleInstance;
            StructuredBuffer<int> _TriangleSubMesh;
            StructuredBuffer<float4x4> _InstanceLocalToWorld;
            StructuredBuffer<int> _InstanceSubMeshMaterial;
            StructuredBuffer<uint2> _InstanceMaterialRange;
            struct NaniteMaterialData
            {
                float4 baseColor;
                float4 emissionColor;
                float4 baseMapST;
                float4 surface0;
                float4 surface1;
                float4 surface2;
                float4 feature0;
            };
            StructuredBuffer<NaniteMaterialData> _NaniteMaterialData;
            struct NaniteInstanceSH
            {
                float4 shAr;
                float4 shAg;
                float4 shAb;
                float4 shBr;
                float4 shBg;
                float4 shBb;
                float4 shC;
            };
            StructuredBuffer<NaniteInstanceSH> _InstanceSH;
            StructuredBuffer<uint2> _TileMaterialMask;
            StructuredBuffer<uint> _TileMaterialBinList;
            #include "NanitePackedPage.hlsl"

            struct Attributes
            {
                uint vertexID : SV_VertexID;
                uint instanceID : SV_InstanceID;
            };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            int ResolveMaterialIdInt() { return (int)_ResolveMaterialId; }
            int VertexStrideInt() { return max(3, (int)_VertexStride); }
            int MaxSubMeshCountInt() { return max(1, (int)_MaxSubMeshCount); }
            int TriangleCountInt() { return max(0, (int)_TriangleCount); }
            int InstanceCountInt() { return max(0, (int)_InstanceCount); }
            int UseNormalizedIdsInt() { return (int)_UseNormalizedIds; }

            float3 DecodePositionOS(int logicalVertex)
            {
                int stride = VertexStrideInt();
                int baseOffset = logicalVertex * stride;
                return float3(
                    _VertexData[baseOffset + 0],
                    _VertexData[baseOffset + 1],
                    _VertexData[baseOffset + 2]);
            }

            float2 DecodeUv(int logicalVertex)
            {
                int stride = VertexStrideInt();
                if (stride < 5)
                    return 0.0.xx;
                int baseOffset = logicalVertex * stride;
                return float2(_VertexData[baseOffset + 3], _VertexData[baseOffset + 4]);
            }

            float3 DecodeNormalOS(int logicalVertex)
            {
                int stride = VertexStrideInt();
                if (stride < 8)
                    return float3(0.0, 0.0, 0.0);
                int baseOffset = logicalVertex * stride;
                float3 n = float3(
                    _VertexData[baseOffset + 5],
                    _VertexData[baseOffset + 6],
                    _VertexData[baseOffset + 7]);
                return normalize(n);
            }

            float4 DecodeTangentOS(int logicalVertex)
            {
                int stride = VertexStrideInt();
                if (stride < 12)
                    return float4(0.0, 0.0, 0.0, 1.0);
                int baseOffset = logicalVertex * stride;
                float3 t = float3(
                    _VertexData[baseOffset + 8],
                    _VertexData[baseOffset + 9],
                    _VertexData[baseOffset + 10]);
                float w = _VertexData[baseOffset + 11];
                if (dot(t, t) < 1e-8)
                    t = float3(1.0, 0.0, 0.0);
                t = normalize(t);
                return float4(t, w >= 0.0 ? 1.0 : -1.0);
            }

            float3 TransformNormalOSInverseTranspose(float3 normalOS, float4x4 localToWorld)
            {
                float3x3 m = (float3x3)localToWorld;
                float3 c0 = mul(m, float3(1.0, 0.0, 0.0));
                float3 c1 = mul(m, float3(0.0, 1.0, 0.0));
                float3 c2 = mul(m, float3(0.0, 0.0, 1.0));

                float3 cof0 = cross(c1, c2);
                float3 cof1 = cross(c2, c0);
                float3 cof2 = cross(c0, c1);
                float det = dot(c0, cof0);

                float3 transformed;
                if (abs(det) > 1e-10)
                {
                    float invDet = rcp(det);
                    // 对齐 URP TransformObjectToWorldNormal: mul(normalOS, worldToObject)
                    // worldToObject 的行向量即 cof0/cof1/cof2 / det。
                    transformed =
                        (normalOS.x * cof0 +
                         normalOS.y * cof1 +
                         normalOS.z * cof2) * invDet;
                }
                else
                {
                    transformed = mul(m, normalOS);
                }

                return normalize(transformed);
            }

            float NaniteGetOddNegativeScale(float4x4 localToWorld)
            {
                float3x3 m = (float3x3)localToWorld;
                float3 c0 = mul(m, float3(1.0, 0.0, 0.0));
                float3 c1 = mul(m, float3(0.0, 1.0, 0.0));
                float3 c2 = mul(m, float3(0.0, 0.0, 1.0));
                return dot(cross(c0, c1), c2) < 0.0 ? -1.0 : 1.0;
            }

            float3 NaniteTransformTangentOS(float3 tangentOS, float4x4 localToWorld)
            {
                return normalize(mul((float3x3)localToWorld, tangentOS));
            }

            float3 NaniteBuildNormalWS(float3 normalTS, float3 normalVertexWS, float3 tangentVertexWS, float tangentSignWS)
            {
                float3 bitangentWS = tangentSignWS * cross(normalVertexWS, tangentVertexWS);
                return TransformTangentToWorld(normalTS, half3x3(tangentVertexWS, bitangentWS, normalVertexWS));
            }

            half3 SampleInstanceProbeSH(int instanceId, half3 normalWS)
            {
                half4 n = half4(normalWS, 1.0h);
                half4 vB = n.xyzz * n.yzzx;
                NaniteInstanceSH instanceSH = _InstanceSH[instanceId];
                half3 x1 = half3(
                    dot(instanceSH.shAr, n),
                    dot(instanceSH.shAg, n),
                    dot(instanceSH.shAb, n));
                half3 x2 = half3(
                    dot(instanceSH.shBr, vB),
                    dot(instanceSH.shBg, vB),
                    dot(instanceSH.shBb, vB));
                half vC = n.x * n.x - n.y * n.y;
                half3 x3 = instanceSH.shC.rgb * vC;
                return x1 + x2 + x3;
            }

            Varyings vertResolve(Attributes input)
            {
                Varyings o;

                if (_UseTileMaterialMask > 0.5)
                {
                    uint tileCount = (uint)max(0, (int)_TileCount);
                    int resolveMaterialId = ResolveMaterialIdInt();
                    bool compatibilityMode = _ResolveMaterialMode > 1.5;
                    bool familyMode = _ResolveMaterialMode > 0.5 && !compatibilityMode;
                    int resolveBinId = familyMode || compatibilityMode
                        ? (int)_ResolveMaterialFamily
                        : (resolveMaterialId >= 0
                            ? (int)round(_NaniteMaterialData[resolveMaterialId].feature0.w)
                            : 0);
                    uint binIndex = familyMode
                        ? (uint)max(0, resolveBinId)
                        : 32u + ((uint)max(0, resolveBinId) & 31u);
                    uint tileIndex = input.instanceID;
                    if (_UseCompactedTileBins > 0.5 && input.instanceID < tileCount)
                        tileIndex = _TileMaterialBinList[binIndex * tileCount + input.instanceID];
                    uint2 tileMask = tileIndex < tileCount
                        ? _TileMaterialMask[tileIndex]
                        : 0u.xx;
                    bool compatibilityContainsAbsorbedFamily = compatibilityMode &&
                        _ResolveAbsorbedFamily > 0.5 && _ResolveAbsorbedFamily < 32.0 &&
                        (tileMask.x & (1u << (uint)_ResolveAbsorbedFamily)) != 0u;
                    bool tileContainsMaterial = familyMode
                        ? (resolveBinId > 0 && resolveBinId < 32 &&
                           (tileMask.x & (1u << resolveBinId)) != 0u)
                        : ((resolveBinId >= 0 &&
                            (tileMask.y & (1u << ((uint)resolveBinId & 31u))) != 0u) ||
                           compatibilityContainsAbsorbedFamily);

                    if (!tileContainsMaterial)
                    {
                        // Degenerate the two triangles before rasterization. This avoids relying on
                        // NaN clip positions, whose handling differs between graphics backends.
                        o.positionCS = float4(-2.0, -2.0, UNITY_RAW_FAR_CLIP_VALUE, 1.0);
                        return o;
                    }

                    uint cornerIndex = input.vertexID % 6u;
                    float2 corner = float2(
                        (cornerIndex == 1u || cornerIndex >= 4u) ? 1.0 : 0.0,
                        (cornerIndex == 2u || cornerIndex == 3u || cornerIndex == 5u) ? 1.0 : 0.0);

                    uint tileCountX = (uint)max(1, (int)_TileCountX);
                    uint2 tileCoord = uint2(tileIndex % tileCountX, tileIndex / tileCountX);
                    float tileSize = max(1.0, _TileSize);
                    float2 screenSize = max(_ScreenParams.xy, 1.0.xx);
                    float2 pixelMin = float2(tileCoord) * tileSize;
                    float2 pixelMax = min(pixelMin + tileSize, screenSize);
                    float2 uv = lerp(pixelMin, pixelMax, corner) / screenSize;
                    float2 ndc = uv * 2.0 - 1.0;
                    ndc.y = -ndc.y;
                    o.positionCS = float4(ndc, UNITY_RAW_FAR_CLIP_VALUE, 1.0);
                    return o;
                }

                float2 uv = float2((input.vertexID << 1) & 2u, input.vertexID & 2u);
                float2 ndc = uv * 2.0 - 1.0;
                ndc.y = -ndc.y;
                o.positionCS = float4(ndc, UNITY_RAW_FAR_CLIP_VALUE, 1.0);
                return o;
            }

            uint2 NaniteVBufferCoord(float2 screenUv)
            {
                float2 size = _NaniteVBufferSize.x > 1.5 ? _NaniteVBufferSize.xy : _ScreenParams.xy;
                size = max(size, 1.0);
                return uint2(min(floor(screenUv * size), size - 1.0));
            }

            GBufferFragOutput frag(Varyings input)
            {
                float2 screenUv = GetNormalizedScreenSpaceUV(input.positionCS);
                uint2 pixelCoord = NaniteVBufferCoord(screenUv);
                int resolveMaterialId = ResolveMaterialIdInt();

                #if defined(NANITE_COMPACT_VBUFFER)
                uint2 encoded = _NaniteVBufferTex.Load(int3(pixelCoord, 0));
                NaniteDecodedVBufferIds decoded = NaniteDecodeCompactVBufferIds(
                    encoded,
                    InstanceCountInt(),
                    TriangleCountInt());
                #else
                float4 encoded = _NaniteVBufferTex.Load(int3(pixelCoord, 0));
                NaniteDecodedVBufferIds decoded = NaniteDecodeVBufferIds(
                    encoded,
                    UseNormalizedIdsInt(),
                    InstanceCountInt(),
                    TriangleCountInt());
                #endif
                if (decoded.valid == 0)
                    discard;

                int instanceId = decoded.instanceId;
                int triangleId = decoded.triangleId;
                uint2 materialRange = _InstanceMaterialRange[instanceId];

                int subMeshId = _TriangleSubMesh[triangleId];
                subMeshId = clamp(subMeshId, 0, max(0, (int)materialRange.y - 1));
                int materialId = _InstanceSubMeshMaterial[materialRange.x + (uint)subMeshId];
                NaniteMaterialData materialData = _NaniteMaterialData[materialId];
                if (_ResolveMaterialMode > 1.5)
                {
                    bool exactCompatibility =
                        (int)round(materialData.feature0.w) == (int)_ResolveMaterialFamily;
                    bool absorbedTexturelessFamily = _ResolveAbsorbedFamily > 0.5 &&
                        (int)round(materialData.feature0.z) == (int)_ResolveAbsorbedFamily;
                    if (!exactCompatibility && !absorbedTexturelessFamily)
                        discard;
                }
                else if (_ResolveMaterialMode > 0.5)
                {
                    if ((int)round(materialData.feature0.z) != (int)_ResolveMaterialFamily)
                        discard;
                }
                else if (materialId != resolveMaterialId)
                {
                    discard;
                }

                float3 p0OS;
                float3 p1OS;
                float3 p2OS;
                float2 uv0;
                float2 uv1;
                float2 uv2;
                float3 n0OS;
                float3 n1OS;
                float3 n2OS;
                float4 t0OS;
                float4 t1OS;
                float4 t2OS;
                if (NaniteUsePackedPageGeometry())
                {
                    NanitePackedVertex v0;
                    NanitePackedVertex v1;
                    NanitePackedVertex v2;
                    if (!NanitePackedLoadTriangle((uint)triangleId, v0, v1, v2))
                        discard;
                    p0OS = v0.positionOS;
                    p1OS = v1.positionOS;
                    p2OS = v2.positionOS;
                    uv0 = v0.uv;
                    uv1 = v1.uv;
                    uv2 = v2.uv;
                    n0OS = v0.normalOS;
                    n1OS = v1.normalOS;
                    n2OS = v2.normalOS;
                    t0OS = v0.tangentOS;
                    t1OS = v1.tangentOS;
                    t2OS = v2.tangentOS;
                }
                else
                {
                    int i0 = _Indices[triangleId * 3 + 0];
                    int i1 = _Indices[triangleId * 3 + 1];
                    int i2 = _Indices[triangleId * 3 + 2];
                    p0OS = DecodePositionOS(i0);
                    p1OS = DecodePositionOS(i1);
                    p2OS = DecodePositionOS(i2);
                    uv0 = DecodeUv(i0);
                    uv1 = DecodeUv(i1);
                    uv2 = DecodeUv(i2);
                    n0OS = DecodeNormalOS(i0);
                    n1OS = DecodeNormalOS(i1);
                    n2OS = DecodeNormalOS(i2);
                    t0OS = DecodeTangentOS(i0);
                    t1OS = DecodeTangentOS(i1);
                    t2OS = DecodeTangentOS(i2);
                }

                float4x4 l2w = _InstanceLocalToWorld[instanceId];
                float3 p0WS = mul(l2w, float4(p0OS, 1.0)).xyz;
                float3 p1WS = mul(l2w, float4(p1OS, 1.0)).xyz;
                float3 p2WS = mul(l2w, float4(p2OS, 1.0)).xyz;
                float4 p0CS = TransformWorldToHClip(p0WS);
                float4 p1CS = TransformWorldToHClip(p1WS);
                float4 p2CS = TransformWorldToHClip(p2WS);

                float2 pixelNdc = NaniteNdcFromScreenUv(screenUv);
                NaniteBarycentrics bary = CalculateTriangleBarycentricsNdc(pixelNdc, p0CS, p1CS, p2CS, _NaniteViewInvSize);
                float barySum = bary.value.x + bary.value.y + bary.value.z;
                if (abs(barySum) < 1e-5)
                    discard;
                // 仅吸收浮点误差带来的微小负分量；不可在 h 符号错误时把重心整体清零。
                bary.value = max(bary.value, 0.0);
                float baryClampedSum = bary.value.x + bary.value.y + bary.value.z;
                if (baryClampedSum < 1e-5)
                    discard;
                bary.value /= baryClampedSum;

                float2 meshUvDx;
                float2 meshUvDy;
                float2 meshUv = NaniteBarycentricLerp(uv0, uv1, uv2, bary, meshUvDx, meshUvDy);
                float2 surfaceUv = meshUv * materialData.baseMapST.xy + materialData.baseMapST.zw;
                // 全屏 resolve 的邻像素常属不同三角形，ddx/ddy(uv) 会跨三角爆炸 → 远处 mip 拉满看起来“没贴图”。
                // 必须始终使用同一三角形的解析重心导数。
                float2 uvDx = meshUvDx * materialData.baseMapST.xy;
                float2 uvDy = meshUvDy * materialData.baseMapST.xy;
                float maxGrad2 = max(dot(uvDx, uvDx), dot(uvDy, uvDy));
                // 亚像素三角数值尖峰时钳制梯度，避免 SampleGrad 选到无效高 mip。
                const float kMaxUvGrad = 1.0;
                if (maxGrad2 > kMaxUvGrad * kMaxUvGrad)
                {
                    float scale = kMaxUvGrad * rsqrt(maxGrad2);
                    uvDx *= scale;
                    uvDy *= scale;
                }
                float3 positionWS = p0WS * bary.value.x + p1WS * bary.value.y + p2WS * bary.value.z;
                #if defined(NANITE_COMPACT_VBUFFER)
                // 由透视正确的 world position 重建，不能直接线性插值三个顶点的 clip z。
                float deviceDepth = NaniteDeviceDepthFromClip(TransformWorldToHClip(positionWS));
                #else
                // Legacy VBuffer 保存了光栅阶段已经决议后的像素深度。
                float deviceDepth = encoded.x;
                #endif

                float4 baseTex = float4(1.0, 1.0, 1.0, 1.0);
                if (materialData.surface1.z > 0.5)
                    baseTex = SAMPLE_TEXTURE2D_GRAD(_BaseMap, sampler_BaseMap, surfaceUv, uvDx, uvDy);
                float alpha = baseTex.a * materialData.baseColor.a;
                if (materialData.surface1.y > 0.5 && alpha < materialData.surface0.x)
                    discard;

                float3 edge1 = p1WS - p0WS;
                float3 edge2 = p2WS - p0WS;
                float3 normalGeo = normalize(cross(edge1, edge2));

                float3 n0WS = TransformNormalOSInverseTranspose(n0OS, l2w);
                float3 n1WS = TransformNormalOSInverseTranspose(n1OS, l2w);
                float3 n2WS = TransformNormalOSInverseTranspose(n2OS, l2w);
                float3 normalVertexWS = normalGeo;
                if (VertexStrideInt() >= 8)
                {
                    normalVertexWS = n0WS * bary.value.x + n1WS * bary.value.y + n2WS * bary.value.z;
                    if (dot(normalVertexWS, normalVertexWS) < 1e-8)
                        normalVertexWS = normalGeo;
                }
                normalVertexWS = NormalizeNormalPerPixel(normalVertexWS);

                float tangentSignWS = NaniteGetOddNegativeScale(l2w);
                float3 tangentVertexWS = abs(normalVertexWS.y) < 0.999
                    ? normalize(cross(normalVertexWS, float3(0.0, 1.0, 0.0)))
                    : normalize(cross(normalVertexWS, float3(1.0, 0.0, 0.0)));
                if (VertexStrideInt() >= 12)
                {
                    float3 t0WS = NaniteTransformTangentOS(t0OS.xyz, l2w);
                    float3 t1WS = NaniteTransformTangentOS(t1OS.xyz, l2w);
                    float3 t2WS = NaniteTransformTangentOS(t2OS.xyz, l2w);
                    tangentVertexWS = t0WS * bary.value.x + t1WS * bary.value.y + t2WS * bary.value.z;
                    if (dot(tangentVertexWS, tangentVertexWS) < 1e-8)
                    {
                        tangentVertexWS = abs(normalVertexWS.y) < 0.999
                            ? normalize(cross(normalVertexWS, float3(0.0, 1.0, 0.0)))
                            : normalize(cross(normalVertexWS, float3(1.0, 0.0, 0.0)));
                    }

                    float oddNegativeScale = NaniteGetOddNegativeScale(l2w);
                    tangentSignWS = (t0OS.w * bary.value.x + t1OS.w * bary.value.y + t2OS.w * bary.value.z) * oddNegativeScale;
                }
                tangentSignWS = tangentSignWS < 0.0 ? -1.0 : 1.0;
                tangentVertexWS = tangentVertexWS - normalVertexWS * dot(normalVertexWS, tangentVertexWS);
                if (dot(tangentVertexWS, tangentVertexWS) < 1e-8)
                {
                    tangentVertexWS = abs(normalVertexWS.y) < 0.999
                        ? normalize(cross(normalVertexWS, float3(0.0, 1.0, 0.0)))
                        : normalize(cross(normalVertexWS, float3(1.0, 0.0, 0.0)));
                }
                tangentVertexWS = normalize(tangentVertexWS);

                float3 normalTS = float3(0.0, 0.0, 1.0);
                if (materialData.surface1.w > 0.5)
                {
                    float4 normalPacked = SAMPLE_TEXTURE2D_GRAD(_BumpMap, sampler_BumpMap, surfaceUv, uvDx, uvDy);
                    normalTS = UnpackNormalScale(normalPacked, materialData.surface0.w);
                }
                float3 normalWS = NormalizeNormalPerPixel(NaniteBuildNormalWS(normalTS, normalVertexWS, tangentVertexWS, tangentSignWS));

                float4 metallicGloss = float4(materialData.surface0.z, 0.0, 0.0, materialData.surface0.y);
                if (materialData.surface2.x > 0.5)
                {
                    metallicGloss = SAMPLE_TEXTURE2D_GRAD(_MetallicGlossMap, sampler_MetallicGlossMap, surfaceUv, uvDx, uvDy);
                    if (materialData.feature0.x > 0.5)
                        metallicGloss.a = baseTex.a * materialData.surface0.y;
                    else
                        metallicGloss.a *= materialData.surface0.y;
                }
                float metallic = metallicGloss.r;
                float smoothness = metallicGloss.a;

                float occTex = SAMPLE_TEXTURE2D_GRAD(_OcclusionMap, sampler_OcclusionMap, surfaceUv, uvDx, uvDy).g;
                float occlusion = 1.0;
                if (materialData.surface2.y > 0.5)
                    occlusion = LerpWhiteTo(occTex, materialData.surface1.x);

                float3 emission = float3(0.0, 0.0, 0.0);
                if (materialData.surface2.w > 0.5)
                {
                    float3 emissionTex = SAMPLE_TEXTURE2D_GRAD(_EmissionMap, sampler_EmissionMap, surfaceUv, uvDx, uvDy).rgb;
                    emission = materialData.emissionColor.rgb * lerp(float3(1.0, 1.0, 1.0), emissionTex, saturate(materialData.surface2.z));
                }

                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo = baseTex.rgb * materialData.baseColor.rgb;
                surfaceData.specular = float3(0.0, 0.0, 0.0);
                surfaceData.metallic = saturate(metallic);
                surfaceData.smoothness = saturate(smoothness);
                surfaceData.normalTS = normalTS;
                surfaceData.emission = emission;
                surfaceData.occlusion = saturate(occlusion);
                surfaceData.alpha = alpha;
                surfaceData.clearCoatMask = 0.0;
                surfaceData.clearCoatSmoothness = 0.0;

                InputData inputData = (InputData)0;
                inputData.positionWS = positionWS;
                inputData.positionCS = float4(input.positionCS.xy, deviceDepth, 1.0);
                inputData.normalWS = NormalizeNormalPerPixel(normalWS);
                inputData.viewDirectionWS = SafeNormalize(_WorldSpaceCameraPos - positionWS);
                inputData.shadowCoord = TransformWorldToShadowCoord(positionWS);
                inputData.fogCoord = 0.0;
                inputData.vertexLighting = float3(0.0, 0.0, 0.0);
                inputData.normalizedScreenSpaceUV = screenUv;
                float4 probeOcclusion = float4(1.0, 1.0, 1.0, 1.0);
                #if defined(_SCREEN_SPACE_IRRADIANCE)
                    inputData.bakedGI = SAMPLE_GI(_ScreenSpaceIrradiance, input.positionCS.xy);
                #else
                    // Fullscreen procedural resolve 没有 Renderer per-draw CBUFFER；
                    // 改为显式读取每实例 SH，避免 RT3 在 dielectric 材质下掉黑。
                    inputData.bakedGI = SampleInstanceProbeSH(instanceId, inputData.normalWS);
                    if (dot(inputData.bakedGI, inputData.bakedGI) < 1e-6h)
                    {
                        half3 vertexShTerm = SampleSHVertex(inputData.normalWS);
                        inputData.bakedGI = SampleSHPixel(vertexShTerm, inputData.normalWS);
                    }
                #endif
                inputData.shadowMask = probeOcclusion;
                float3 bitangentForTbn = tangentSignWS * normalize(cross(normalVertexWS, tangentVertexWS));
                inputData.tangentToWorld = half3x3((half3)tangentVertexWS, (half3)bitangentForTbn, (half3)normalVertexWS);

                BRDFData brdfData;
                half alphaHalf = (half)alpha;
                InitializeBRDFData(
                    (half3)surfaceData.albedo,
                    (half)surfaceData.metallic,
                    (half3)surfaceData.specular,
                    (half)surfaceData.smoothness,
                    alphaHalf,
                    brdfData);

                Light mainLight = GetMainLight(inputData.shadowCoord, inputData.positionWS, inputData.shadowMask);
                MixRealtimeAndBakedGI(mainLight, inputData.normalWS, inputData.bakedGI, inputData.shadowMask);
                half3 gi = GlobalIllumination(
                    brdfData,
                    (BRDFData)0,
                    half(0.0),
                    inputData.bakedGI,
                    surfaceData.occlusion,
                    inputData.positionWS,
                    inputData.normalWS,
                    inputData.viewDirectionWS,
                    inputData.normalizedScreenSpaceUV);
                half3 reflectVector = reflect(-inputData.viewDirectionWS, inputData.normalWS);
                half NoV = saturate(dot(inputData.normalWS, inputData.viewDirectionWS));
                half fresnelTerm = Pow4(1.0h - NoV);
                half mip = PerceptualRoughnessToMipmapLevel(brdfData.perceptualRoughness);

                // Procedural resolve 下，unity_SpecCube0 可能未被正确绑定。
                // 此时 GlobalIllumination 的 specular 分量会丢失，表现为 metallic 从 0->1 越来越黑。
                half4 encodedProbe = half4(SAMPLE_TEXTURECUBE_LOD(unity_SpecCube0, samplerunity_SpecCube0, reflectVector, mip));
                half3 probeSpecular = DecodeHDREnvironment(encodedProbe, unity_SpecCube0_HDR);
                if (dot(probeSpecular, probeSpecular) < 1e-6h)
                {
                    half4 encodedEnv = half4(SAMPLE_TEXTURECUBE_LOD(_GlossyEnvironmentCubeMap, sampler_GlossyEnvironmentCubeMap, reflectVector, mip));
                    half3 envSpecular = DecodeHDREnvironment(encodedEnv, _GlossyEnvironmentCubeMap_HDR);
                    gi += envSpecular * EnvironmentBRDFSpecular(brdfData, fresnelTerm) * surfaceData.occlusion;
                }

                // 兜底：极端情况下 GI 全黑时，回退到环境BRDF整项。
                if (dot(gi, gi) < 1e-6h)
                {
                    half4 encodedEnv = half4(SAMPLE_TEXTURECUBE_LOD(_GlossyEnvironmentCubeMap, sampler_GlossyEnvironmentCubeMap, reflectVector, mip));
                    half3 envSpecular = DecodeHDREnvironment(encodedEnv, _GlossyEnvironmentCubeMap_HDR);
                    gi = EnvironmentBRDF(brdfData, inputData.bakedGI, envSpecular, fresnelTerm) * surfaceData.occlusion;
                }

                GBufferFragOutput output = PackGBuffersBRDFData(
                    brdfData,
                    inputData,
                    surfaceData.smoothness,
                    surfaceData.emission + gi,
                    surfaceData.occlusion);

                #if defined(GBUFFER_FEATURE_RENDERING_LAYERS)
                // Fullscreen procedural resolve 没有 mesh renderer 上下文，强制写全层避免被 Deferred Light Layers 过滤成黑色。
                output.meshRenderingLayers = 0xFFFFFFFFu;
                #endif

                return output;
            }
            ENDHLSL
        }
    }
}
