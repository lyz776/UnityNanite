#ifndef NANITE_VBUFFER_COMMON_INCLUDED
#define NANITE_VBUFFER_COMMON_INCLUDED

// VBuffer ID 编码模式:
// 0 = raw (+1 整数, 三角形数 <= 65535 时精度足够)
// 1 = normalized (legacy, 大场景会丢精度)
// 2 = split triangleId: B=low16+1, A=high16+1 (支持超大场景)

// 与 URP DepthOnlyPass / GBufferOutput 一致：positionCS.z 即 depth buffer 存储值（非 z/w）。
float NanitePackDepth01(float4 positionCS)
{
#if defined(SHADER_API_GLCORE) || defined(SHADER_API_GLES3) || defined(SHADER_API_GLES)
    float depth01 = saturate(positionCS.z / max(abs(positionCS.w), 1e-6));
    return depth01 * 0.5 + 0.5;
#else
    return positionCS.z;
#endif
}

float2 NaniteScreenUvFromPositionCS(float4 positionCS, float2 viewInvSize)
{
    return (positionCS.xy + 0.5) * viewInvSize;
}

float2 NaniteScreenUvFromPixelCoord(int2 pixelCoord, float2 viewInvSize)
{
    return (float2(pixelCoord) + 0.5) * viewInvSize;
}

float2 NaniteNdcFromScreenUv(float2 screenUv)
{
    float2 ndc = screenUv * 2.0 - 1.0;
    ndc.y = -ndc.y;
    return ndc;
}

int2 NanitePixelCoordFromScreenUv(float2 screenUv, float2 viewInvSize)
{
    float2 screenSize = rcp(max(viewInvSize, 1e-8));
    return int2(screenUv * screenSize - 0.5);
}

struct NaniteDecodedVBufferIds
{
    int instanceId;
    int triangleId;
    int valid;
};

float4 NaniteEncodeVBufferIds(
    float depth01,
    int instanceId,
    int triangleId,
    int subMeshId,
    int useNormalizedIds,
    int instanceCount,
    int triangleCount,
    int maxSubMeshCount)
{
    float encInstance;
    float encB;
    float encA;

    if (useNormalizedIds == 2)
    {
        encInstance = (float)instanceId + 1.0;
        encB = (float)((triangleId & 0xFFFF) + 1);
        encA = (float)(((triangleId >> 16) & 0xFFFF) + 1);
    }
    else if (useNormalizedIds != 0)
    {
        float invInst = 1.0 / max(1.0, (float)instanceCount);
        float invTri = 1.0 / max(1.0, (float)triangleCount);
        float invSub = 1.0 / max(1.0, (float)maxSubMeshCount);
        encInstance = ((float)instanceId + 0.5) * invInst;
        encB = ((float)triangleId + 0.5) * invTri;
        encA = ((float)subMeshId + 0.5) * invSub;
    }
    else
    {
        encInstance = (float)instanceId + 1.0;
        encB = (float)triangleId + 1.0;
        encA = (float)subMeshId + 1.0;
    }

    return float4(depth01, encInstance, encB, encA);
}

NaniteDecodedVBufferIds NaniteDecodeVBufferIds(
    float4 encoded,
    int useNormalizedIds,
    int instanceCount,
    int triangleCount)
{
    NaniteDecodedVBufferIds decoded;
    decoded.instanceId = -1;
    decoded.triangleId = -1;
    decoded.valid = 0;

    if (encoded.y <= 0.0 || encoded.z <= 0.0)
        return decoded;

    if (useNormalizedIds == 2)
    {
        decoded.instanceId = (int)round(encoded.y) - 1;
        int triLow = ((int)round(encoded.z) - 1) & 0xFFFF;
        int triHigh = (((int)round(encoded.w) - 1) & 0xFFFF) << 16;
        decoded.triangleId = triHigh | triLow;
    }
    else if (useNormalizedIds != 0)
    {
        int safeInstanceCount = max(1, instanceCount);
        int safeTriangleCount = max(1, triangleCount);
        decoded.instanceId = clamp((int)floor(encoded.y * (float)safeInstanceCount), 0, safeInstanceCount - 1);
        decoded.triangleId = clamp((int)floor(encoded.z * (float)safeTriangleCount), 0, safeTriangleCount - 1);
    }
    else
    {
        decoded.instanceId = (int)round(encoded.y) - 1;
        decoded.triangleId = (int)round(encoded.z) - 1;
    }

    if (decoded.instanceId >= 0 && decoded.instanceId < instanceCount &&
        decoded.triangleId >= 0 && decoded.triangleId < triangleCount)
    {
        decoded.valid = 1;
    }

    return decoded;
}

uint NaniteMurmurMix(uint hash)
{
    hash ^= hash >> 16;
    hash *= 0x85ebca6b;
    hash ^= hash >> 13;
    hash *= 0xc2b2ae35;
    hash ^= hash >> 16;
    return hash;
}

float3 NaniteIntToColor(uint index)
{
    uint hash = NaniteMurmurMix(index);
    float3 color = float3((hash >> 0) & 255u, (hash >> 8) & 255u, (hash >> 16) & 255u);
    return color * (1.0 / 255.0);
}

struct NaniteBarycentrics
{
    float3 value;
    float3 valueDx;
    float3 valueDy;
};

float2 NaniteBarycentricLerp(
    float2 value0,
    float2 value1,
    float2 value2,
    NaniteBarycentrics barycentrics,
    out float2 dPdx,
    out float2 dPdy)
{
    float2 value = value0 * barycentrics.value.x + value1 * barycentrics.value.y + value2 * barycentrics.value.z;
    dPdx = value0 * barycentrics.valueDx.x + value1 * barycentrics.valueDx.y + value2 * barycentrics.valueDx.z;
    dPdy = value0 * barycentrics.valueDy.x + value1 * barycentrics.valueDy.y + value2 * barycentrics.valueDy.z;
    return value;
}

NaniteBarycentrics CalculateTriangleBarycentricsNdc(
    float2 pixelNdc,
    float4 pointClip0,
    float4 pointClip1,
    float4 pointClip2,
    float2 viewInvSize)
{
    NaniteBarycentrics barycentrics;

    float3 rcpW = rcp(float3(pointClip0.w, pointClip1.w, pointClip2.w));
    float3 pos0 = pointClip0.xyz * rcpW.x;
    float3 pos1 = pointClip1.xyz * rcpW.y;
    float3 pos2 = pointClip2.xyz * rcpW.z;

    float3 pos120X = float3(pos1.x, pos2.x, pos0.x);
    float3 pos120Y = float3(pos1.y, pos2.y, pos0.y);
    float3 pos201X = float3(pos2.x, pos0.x, pos1.x);
    float3 pos201Y = float3(pos2.y, pos0.y, pos1.y);

    float3 cDx = pos201Y - pos120Y;
    float3 cDy = pos120X - pos201X;
    float3 c = cDx * (pixelNdc.x - pos120X) + cDy * (pixelNdc.y - pos120Y);

    float3 g = c * rcpW;
    float h = dot(c, rcpW);
    // 必须保留 h 的符号：abs(h) 会把 front-face 重心整体取反，
    // 随后 max(bary,0) 会把 UV/法线插值清零，表现为“远处无贴图/无法线平滑”。
    float hSafe = (abs(h) < 1e-8) ? (h >= 0.0 ? 1e-8 : -1e-8) : h;
    float rcpH = rcp(hSafe);

    barycentrics.value = g * rcpH;

    float3 gDx = cDx * rcpW;
    float3 gDy = cDy * rcpW;
    float hDx = dot(cDx, rcpW);
    float hDy = dot(cDy, rcpW);
    float rcpH2 = rcpH * rcpH;

    // 解析屏幕空间导数（像素差分），供 SAMPLE_*_GRAD 使用；勿再回退到 ddx/ddy。
    barycentrics.valueDx = (gDx * hSafe - g * hDx) * rcpH2 * (2.0 * viewInvSize.x);
    barycentrics.valueDy = (gDy * hSafe - g * hDy) * rcpH2 * (-2.0 * viewInvSize.y);
    return barycentrics;
}

NaniteBarycentrics CalculateTriangleBarycentrics(
    float2 pixelClip,
    float4 pointClip0,
    float4 pointClip1,
    float4 pointClip2,
    float2 viewInvSize)
{
    return CalculateTriangleBarycentricsNdc(NaniteNdcFromScreenUv(pixelClip), pointClip0, pointClip1, pointClip2, viewInvSize);
}

bool NaniteBarycentricsValid(NaniteBarycentrics bary, float tolerance)
{
    float sum = bary.value.x + bary.value.y + bary.value.z;
    return all(bary.value >= -tolerance) && abs(sum - 1.0) <= tolerance * 3.0;
}

#endif
