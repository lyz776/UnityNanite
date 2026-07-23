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
            #pragma vertex vertFullscreen
            #pragma fragment fragDepthOnly
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/ShaderVariablesFunctions.hlsl"
            #include "NaniteVBufferCommon.hlsl"

            CBUFFER_START(NaniteResolveUniforms)
            float _ResolveMaterialId;
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

            TEXTURE2D_FLOAT(_NaniteVBufferTex);

            StructuredBuffer<int> _TriangleSubMesh;
            StructuredBuffer<int> _InstanceSubMeshMaterial;

            struct Attributes { uint vertexID : SV_VertexID; };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            int UseNormalizedIdsInt() { return (int)_UseNormalizedIds; }
            int TriangleCountInt() { return max(0, (int)_TriangleCount); }
            int InstanceCountInt() { return max(0, (int)_InstanceCount); }

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
                float4 encoded = _NaniteVBufferTex.Load(int3(pixelCoord, 0));
                NaniteDecodedVBufferIds decoded = NaniteDecodeVBufferIds(
                    encoded, UseNormalizedIdsInt(), InstanceCountInt(), TriangleCountInt());
                if (decoded.valid == 0)
                    discard;

                outputDepth = encoded.x;
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
            #pragma vertex vertFullscreen
            #pragma fragment frag
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
            float _VertexStride;
            float _MaxSubMeshCount;
            float _TriangleCount;
            float _InstanceCount;
            float _TileCountX;
            float _TileSize;
            float _TileCount;
            float _UseNormalizedIds;
            float _UseTileMaterialMask;
            float2 _NaniteViewInvSize;
            float4 _NaniteVBufferSize; // xy=size, zw=invSize
            CBUFFER_END

            TEXTURE2D_FLOAT(_NaniteVBufferTex);
            SAMPLER(sampler_NaniteVBufferTex);
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
            StructuredBuffer<float4> _InstanceSHAr;
            StructuredBuffer<float4> _InstanceSHAg;
            StructuredBuffer<float4> _InstanceSHAb;
            StructuredBuffer<float4> _InstanceSHBr;
            StructuredBuffer<float4> _InstanceSHBg;
            StructuredBuffer<float4> _InstanceSHBb;
            StructuredBuffer<float4> _InstanceSHC;
            StructuredBuffer<uint> _TileMaterialMask;

            struct Attributes { uint vertexID : SV_VertexID; };
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
                half3 x1 = half3(
                    dot(_InstanceSHAr[instanceId], n),
                    dot(_InstanceSHAg[instanceId], n),
                    dot(_InstanceSHAb[instanceId], n));
                half3 x2 = half3(
                    dot(_InstanceSHBr[instanceId], vB),
                    dot(_InstanceSHBg[instanceId], vB),
                    dot(_InstanceSHBb[instanceId], vB));
                half vC = n.x * n.x - n.y * n.y;
                half3 x3 = _InstanceSHC[instanceId].rgb * vC;
                return x1 + x2 + x3;
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

            uint2 NaniteVBufferCoord(float2 screenUv)
            {
                float2 size = _NaniteVBufferSize.x > 1.5 ? _NaniteVBufferSize.xy : _ScreenParams.xy;
                size = max(size, 1.0);
                return uint2(min(floor(screenUv * size), size - 1.0));
            }

            GBufferFragOutput frag(Varyings input)
            {
                float2 screenUv = GetNormalizedScreenSpaceUV(input.positionCS);
                float2 screenSize = _ScreenParams.xy;
                uint2 fullPixelCoord = uint2(screenUv * screenSize);
                uint2 pixelCoord = NaniteVBufferCoord(screenUv);
                int resolveMaterialId = ResolveMaterialIdInt();

                // Tile mask early-out：该 tile 根本不含当前材质时，跳过昂贵的 VBuffer decode / 属性重建。
                if (_UseTileMaterialMask > 0.5)
                {
                    uint tileSize = (uint)max(1, (int)_TileSize);
                    uint tileCountX = (uint)max(1, (int)_TileCountX);
                    uint2 tileCoord = fullPixelCoord / tileSize;
                    uint tileIndex = tileCoord.y * tileCountX + tileCoord.x;
                    uint mask = _TileMaterialMask[tileIndex];
                    if (resolveMaterialId < 0 || resolveMaterialId >= 32 || (mask & (1u << resolveMaterialId)) == 0u)
                        discard;
                }

                float4 encoded = _NaniteVBufferTex.Load(int3(pixelCoord, 0));
                NaniteDecodedVBufferIds decoded = NaniteDecodeVBufferIds(
                    encoded,
                    UseNormalizedIdsInt(),
                    InstanceCountInt(),
                    TriangleCountInt());
                if (decoded.valid == 0)
                    discard;

                int instanceId = decoded.instanceId;
                int triangleId = decoded.triangleId;
                int maxSubMeshCount = MaxSubMeshCountInt();

                int subMeshId = _TriangleSubMesh[triangleId];
                subMeshId = clamp(subMeshId, 0, max(0, maxSubMeshCount - 1));
                int materialId = _InstanceSubMeshMaterial[instanceId * maxSubMeshCount + subMeshId];
                if (materialId != resolveMaterialId)
                    discard;

                int i0 = _Indices[triangleId * 3 + 0];
                int i1 = _Indices[triangleId * 3 + 1];
                int i2 = _Indices[triangleId * 3 + 2];
                float3 p0OS = DecodePositionOS(i0);
                float3 p1OS = DecodePositionOS(i1);
                float3 p2OS = DecodePositionOS(i2);
                float2 uv0 = DecodeUv(i0);
                float2 uv1 = DecodeUv(i1);
                float2 uv2 = DecodeUv(i2);
                float3 n0OS = DecodeNormalOS(i0);
                float3 n1OS = DecodeNormalOS(i1);
                float3 n2OS = DecodeNormalOS(i2);
                float4 t0OS = DecodeTangentOS(i0);
                float4 t1OS = DecodeTangentOS(i1);
                float4 t2OS = DecodeTangentOS(i2);

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
                float2 surfaceUv = meshUv * _BaseMap_ST.xy + _BaseMap_ST.zw;
                // 全屏 resolve 的邻像素常属不同三角形，ddx/ddy(uv) 会跨三角爆炸 → 远处 mip 拉满看起来“没贴图”。
                // 必须始终使用同一三角形的解析重心导数。
                float2 uvDx = meshUvDx * _BaseMap_ST.xy;
                float2 uvDy = meshUvDy * _BaseMap_ST.xy;
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
                // RT4/GBuffer depth 必须使用 VBuffer 光栅阶段已经决议后的像素深度。
                // 这里若用 clip-space z 做线性插值，在透视下会失真，常表现为“只有近处少量碎片正常”。
                float deviceDepth = encoded.x;

                float4 baseTex = float4(1.0, 1.0, 1.0, 1.0);
                if (_HasBaseMap > 0.5)
                    baseTex = SAMPLE_TEXTURE2D_GRAD(_BaseMap, sampler_BaseMap, surfaceUv, uvDx, uvDy);
                float alpha = baseTex.a * _BaseColor.a;
                if (_AlphaClip > 0.5 && alpha < _Cutoff)
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
                if (_HasNormalMap > 0.5)
                {
                    float4 normalPacked = SAMPLE_TEXTURE2D_GRAD(_BumpMap, sampler_BumpMap, surfaceUv, uvDx, uvDy);
                    normalTS = UnpackNormalScale(normalPacked, _BumpScale);
                }
                float3 normalWS = NormalizeNormalPerPixel(NaniteBuildNormalWS(normalTS, normalVertexWS, tangentVertexWS, tangentSignWS));

                float4 metallicGloss = float4(_Metallic, 0.0, 0.0, _Smoothness);
                if (_HasMetallicGlossMap > 0.5)
                {
                    metallicGloss = SAMPLE_TEXTURE2D_GRAD(_MetallicGlossMap, sampler_MetallicGlossMap, surfaceUv, uvDx, uvDy);
                    if (_SmoothnessFromAlbedoAlpha > 0.5)
                        metallicGloss.a = baseTex.a * _Smoothness;
                    else
                        metallicGloss.a *= _Smoothness;
                }
                float metallic = metallicGloss.r;
                float smoothness = metallicGloss.a;

                float occTex = SAMPLE_TEXTURE2D_GRAD(_OcclusionMap, sampler_OcclusionMap, surfaceUv, uvDx, uvDy).g;
                float occlusion = 1.0;
                if (_HasOcclusionMap > 0.5)
                    occlusion = LerpWhiteTo(occTex, _OcclusionStrength);

                float3 emission = float3(0.0, 0.0, 0.0);
                if (_EmissionEnabled > 0.5)
                {
                    float3 emissionTex = SAMPLE_TEXTURE2D_GRAD(_EmissionMap, sampler_EmissionMap, surfaceUv, uvDx, uvDy).rgb;
                    emission = _EmissionColor.rgb * lerp(float3(1.0, 1.0, 1.0), emissionTex, saturate(_HasEmissionMap));
                }

                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo = baseTex.rgb * _BaseColor.rgb;
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
