#ifndef NANITE_PACKED_PAGE_INCLUDED
#define NANITE_PACKED_PAGE_INCLUDED

// NPG1 direct decode is a diagnostic-only variant. Production raster consumes only the
// aligned resident cache produced once by NanitePageTranscode.compute.
#if defined(NANITE_PACKED_DIRECT_DIAGNOSTIC)
struct NaniteGpuPageDecodeEntry
{
    uint byteAddress;
    uint packedBytes;
    uint vertexSectionAddress;
    uint indexSectionAddress;

    uint flags;
    uint vertexCount;
    uint indexCount;
    uint vertexRecordBytes;

    float3 positionMin;
    uint generation;

    float3 positionExtent;
    uint reserved;
};
#endif

#if defined(NANITE_PACKED_DIRECT_DIAGNOSTIC)
struct NaniteGpuResidentPageEntry
{
    uint vertexBase;
    uint indexBase;
    uint vertexCount;
    uint indexCount;
    uint flags;
    uint generation;
    uint reserved0;
    uint reserved1;
};
#endif

struct NaniteResidentVertex
{
    float3 positionOS;
    float2 uv;
    float3 normalOS;
    float4 tangentOS;
};

struct NanitePackedTriangleContext
{
    uint firstResidentIndex;

#if defined(NANITE_PACKED_DIRECT_DIAGNOSTIC)
    uint vertexCount;
    uint indexCount;
    uint localTriangleIndex;
    uint pageBase;

    uint packedBytes;
    uint vertexSectionAddress;
    uint indexSectionAddress;
    uint flags;

    uint vertexRecordBytes;
    uint useResident;
    uint diagnosticReserved0;
    uint diagnosticReserved1;

    float3 positionMin;
    uint diagnosticReserved2;

    float3 positionExtent;
    uint diagnosticReserved3;
#endif
};

struct NanitePackedVertex
{
    float3 positionOS;
    float2 uv;
    float3 normalOS;
    float4 tangentOS;
};

void NanitePackedInitializeVertex(out NanitePackedVertex vertex)
{
    vertex.positionOS = 0.0.xxx;
    vertex.uv = 0.0.xx;
    vertex.normalOS = float3(0.0, 0.0, 1.0);
    vertex.tangentOS = float4(1.0, 0.0, 0.0, 1.0);
}

#if defined(NANITE_PACKED_DIRECT_DIAGNOSTIC)
ByteAddressBuffer _NanitePackedPagePool;
StructuredBuffer<NaniteGpuPageDecodeEntry> _NanitePageDecodeTable;
float _NanitePackedDirectDiagnostic;
StructuredBuffer<NaniteGpuResidentPageEntry> _NaniteResidentPageTable;
#endif
StructuredBuffer<NaniteResidentVertex> _NaniteResidentVertices;
StructuredBuffer<uint> _NaniteResidentIndices;
StructuredBuffer<uint2> _TrianglePageRefs;
float _UsePackedPageGeometry;

static const uint NANITE_PAGE_INVALID = 0xFFFFFFFFu;
#if defined(NANITE_PACKED_DIRECT_DIAGNOSTIC)
static const uint NANITE_NPG1_FLAG_INDEX16 = 1u << 4u;
static const uint NANITE_NPG1_FLAG_FLOAT_UV = 1u << 5u;
static const uint NANITE_RESIDENT_GEOMETRY_READY = 1u << 0u;
#endif

bool NaniteUsePackedPageGeometry()
{
    return _UsePackedPageGeometry > 0.5;
}

#if defined(NANITE_PACKED_DIRECT_DIAGNOSTIC)
uint NanitePackedLoadU32(uint byteAddress)
{
    uint alignedAddress = byteAddress & ~3u;
    uint bitShift = (byteAddress & 3u) * 8u;
    uint low = _NanitePackedPagePool.Load(alignedAddress);
    if (bitShift == 0u)
        return low;
    uint high = _NanitePackedPagePool.Load(alignedAddress + 4u);
    return (low >> bitShift) | (high << (32u - bitShift));
}

uint NanitePackedLoadU16(uint byteAddress)
{
    return NanitePackedLoadU32(byteAddress) & 0xFFFFu;
}

int NanitePackedLoadS16(uint byteAddress)
{
    uint value = NanitePackedLoadU16(byteAddress);
    return (int)(value << 16u) >> 16;
}

float NanitePackedLoadF32(uint byteAddress)
{
    return asfloat(NanitePackedLoadU32(byteAddress));
}

float NanitePackedSignNotZero(float value)
{
    return value < 0.0 ? -1.0 : 1.0;
}

float3 NanitePackedDecodeOct16(int x, int y)
{
    float2 oct = clamp(float2(x, y) / 32767.0, -1.0, 1.0);
    float3 value = float3(oct, 1.0 - abs(oct.x) - abs(oct.y));
    if (value.z < 0.0)
    {
        float oldX = value.x;
        value.x = (1.0 - abs(value.y)) * NanitePackedSignNotZero(oldX);
        value.y = (1.0 - abs(oldX)) * NanitePackedSignNotZero(value.y);
    }
    float lengthSquared = dot(value, value);
    return lengthSquared > 1e-20
        ? value * rsqrt(lengthSquared)
        : float3(0.0, 0.0, 1.0);
}
#endif

bool NanitePackedResolveTriangleContext(
    uint triangleId,
    out NanitePackedTriangleContext context)
{
    context = (NanitePackedTriangleContext)0;

    uint2 triangleRef = _TrianglePageRefs[triangleId];
    if (triangleRef.x == NANITE_PAGE_INVALID)
        return false;
    context.firstResidentIndex = triangleRef.x;

    #if defined(NANITE_PACKED_DIRECT_DIAGNOSTIC)
    if (triangleRef.y == NANITE_PAGE_INVALID)
        return false;
    NaniteGpuResidentPageEntry residentPage = _NaniteResidentPageTable[triangleRef.y];
    if ((residentPage.flags & NANITE_RESIDENT_GEOMETRY_READY) == 0u ||
        residentPage.indexCount < 3u ||
        triangleRef.x < residentPage.indexBase ||
        triangleRef.x - residentPage.indexBase > residentPage.indexCount - 3u)
        return false;
    if (_NanitePackedDirectDiagnostic < 0.5)
    {
        context.useResident = 1u;
        return true;
    }

    uint localTriangleIndex = (triangleRef.x - residentPage.indexBase) / 3u;
    NaniteGpuPageDecodeEntry page = _NanitePageDecodeTable[triangleRef.y];
    if (page.byteAddress == NANITE_PAGE_INVALID ||
        page.vertexSectionAddress == NANITE_PAGE_INVALID ||
        page.indexSectionAddress == NANITE_PAGE_INVALID ||
        page.packedBytes < 136u ||
        page.vertexRecordBytes == 0u ||
        localTriangleIndex >= page.indexCount / 3u)
        return false;

    context.pageBase = page.byteAddress;
    context.packedBytes = page.packedBytes;
    context.vertexSectionAddress = page.vertexSectionAddress;
    context.indexSectionAddress = page.indexSectionAddress;
    context.flags = page.flags;
    context.vertexCount = page.vertexCount;
    context.indexCount = page.indexCount;
    context.vertexRecordBytes = page.vertexRecordBytes;
    context.positionMin = page.positionMin;
    context.localTriangleIndex = localTriangleIndex;
    context.positionExtent = page.positionExtent;
    return true;
    #else
    return true;
    #endif
}

bool NanitePackedResolveLogicalVertex(
    NanitePackedTriangleContext context,
    uint corner,
    out uint logicalVertex)
{
    logicalVertex = 0u;
    #if defined(NANITE_PACKED_DIRECT_DIAGNOSTIC)
    if (context.useResident != 0u)
    {
        logicalVertex = _NaniteResidentIndices[
            context.firstResidentIndex + min(corner, 2u)];
        return true;
    }

    uint pageIndex = context.localTriangleIndex * 3u + min(corner, 2u);
    if (pageIndex >= context.indexCount)
        return false;
    if (context.indexSectionAddress < context.pageBase)
        return false;
    uint indexRecordBytes =
        (context.flags & NANITE_NPG1_FLAG_INDEX16) != 0u ? 2u : 4u;
    uint indexSectionOffset = context.indexSectionAddress - context.pageBase;
    if (indexRecordBytes > context.packedBytes ||
        indexSectionOffset > context.packedBytes - indexRecordBytes ||
        pageIndex >= (context.packedBytes - indexSectionOffset) / indexRecordBytes)
        return false;

    uint indexAddress = context.indexSectionAddress + pageIndex * indexRecordBytes;
    logicalVertex = indexRecordBytes == 2u
        ? NanitePackedLoadU16(indexAddress)
        : NanitePackedLoadU32(indexAddress);
    return logicalVertex < context.vertexCount;
    #else
    logicalVertex = _NaniteResidentIndices[
        context.firstResidentIndex + min(corner, 2u)];
    return true;
    #endif
}

#if defined(NANITE_PACKED_DIRECT_DIAGNOSTIC)
bool NanitePackedResolveVertexAddress(
    NanitePackedTriangleContext context,
    uint corner,
    out uint vertexAddress)
{
    vertexAddress = 0u;
    if (context.useResident != 0u || context.vertexSectionAddress < context.pageBase)
        return false;

    uint logicalVertex;
    if (!NanitePackedResolveLogicalVertex(context, corner, logicalVertex) ||
        context.vertexRecordBytes > context.packedBytes)
        return false;

    uint vertexSectionOffset = context.vertexSectionAddress - context.pageBase;
    uint availableVertexBytes = context.packedBytes - context.vertexRecordBytes;
    if (vertexSectionOffset > availableVertexBytes ||
        logicalVertex >=
            (context.packedBytes - vertexSectionOffset) / context.vertexRecordBytes)
        return false;

    vertexAddress = context.vertexSectionAddress +
                    logicalVertex * context.vertexRecordBytes;
    return true;
}
#endif

bool NanitePackedLoadPosition(
    NanitePackedTriangleContext context,
    uint corner,
    out float3 positionOS)
{
    positionOS = 0.0.xxx;
    #if defined(NANITE_PACKED_DIRECT_DIAGNOSTIC)
    if (context.useResident != 0u)
    {
        uint logicalVertex;
        if (!NanitePackedResolveLogicalVertex(context, corner, logicalVertex))
            return false;
        positionOS = _NaniteResidentVertices[logicalVertex].positionOS;
        return true;
    }
    uint vertexAddress;
    if (!NanitePackedResolveVertexAddress(context, corner, vertexAddress))
        return false;

    float3 quantized = float3(
        NanitePackedLoadU16(vertexAddress + 0u),
        NanitePackedLoadU16(vertexAddress + 2u),
        NanitePackedLoadU16(vertexAddress + 4u));
    positionOS = context.positionMin + quantized * (context.positionExtent / 65535.0);
    return true;
    #else
    uint logicalVertex;
    if (!NanitePackedResolveLogicalVertex(context, corner, logicalVertex))
        return false;
    positionOS = _NaniteResidentVertices[logicalVertex].positionOS;
    return true;
    #endif
}

bool NanitePackedLoadPosition(
    uint triangleId,
    uint corner,
    out float3 positionOS)
{
    NanitePackedTriangleContext context;
    if (!NanitePackedResolveTriangleContext(triangleId, context))
    {
        positionOS = 0.0.xxx;
        return false;
    }
    return NanitePackedLoadPosition(context, corner, positionOS);
}

bool NanitePackedLoadTrianglePositions(
    uint triangleId,
    out float3 position0OS,
    out float3 position1OS,
    out float3 position2OS)
{
    NanitePackedTriangleContext context;
    if (!NanitePackedResolveTriangleContext(triangleId, context))
    {
        position0OS = 0.0.xxx;
        position1OS = 0.0.xxx;
        position2OS = 0.0.xxx;
        return false;
    }

    bool valid0 = NanitePackedLoadPosition(context, 0u, position0OS);
    bool valid1 = NanitePackedLoadPosition(context, 1u, position1OS);
    bool valid2 = NanitePackedLoadPosition(context, 2u, position2OS);
    return valid0 && valid1 && valid2;
}

bool NanitePackedLoadVertex(
    NanitePackedTriangleContext context,
    uint corner,
    out NanitePackedVertex vertex)
{
    NanitePackedInitializeVertex(vertex);
    #if defined(NANITE_PACKED_DIRECT_DIAGNOSTIC)
    if (context.useResident != 0u)
    {
        uint logicalVertex;
        if (!NanitePackedResolveLogicalVertex(context, corner, logicalVertex))
            return false;
        NaniteResidentVertex residentVertex = _NaniteResidentVertices[logicalVertex];
        vertex.positionOS = residentVertex.positionOS;
        vertex.uv = residentVertex.uv;
        vertex.normalOS = residentVertex.normalOS;
        vertex.tangentOS = residentVertex.tangentOS;
        return true;
    }
    uint vertexAddress;
    if (!NanitePackedResolveVertexAddress(context, corner, vertexAddress))
        return false;

    float3 quantized = float3(
        NanitePackedLoadU16(vertexAddress + 0u),
        NanitePackedLoadU16(vertexAddress + 2u),
        NanitePackedLoadU16(vertexAddress + 4u));
    vertex.positionOS = context.positionMin + quantized * (context.positionExtent / 65535.0);
    vertex.normalOS = NanitePackedDecodeOct16(
        NanitePackedLoadS16(vertexAddress + 6u),
        NanitePackedLoadS16(vertexAddress + 8u));
    vertex.tangentOS.xyz = NanitePackedDecodeOct16(
        NanitePackedLoadS16(vertexAddress + 10u),
        NanitePackedLoadS16(vertexAddress + 12u));

    if ((context.flags & NANITE_NPG1_FLAG_FLOAT_UV) != 0u)
    {
        vertex.uv = float2(
            NanitePackedLoadF32(vertexAddress + 14u),
            NanitePackedLoadF32(vertexAddress + 18u));
        vertex.tangentOS.w = NanitePackedLoadS16(vertexAddress + 22u) < 0 ? -1.0 : 1.0;
    }
    else
    {
        vertex.uv = float2(
            f16tof32(NanitePackedLoadU16(vertexAddress + 14u)),
            f16tof32(NanitePackedLoadU16(vertexAddress + 16u)));
        vertex.tangentOS.w = NanitePackedLoadS16(vertexAddress + 18u) < 0 ? -1.0 : 1.0;
    }
    return true;
    #else
    uint logicalVertex;
    if (!NanitePackedResolveLogicalVertex(context, corner, logicalVertex))
        return false;
    NaniteResidentVertex residentVertex = _NaniteResidentVertices[logicalVertex];
    vertex.positionOS = residentVertex.positionOS;
    vertex.uv = residentVertex.uv;
    vertex.normalOS = residentVertex.normalOS;
    vertex.tangentOS = residentVertex.tangentOS;
    return true;
    #endif
}

bool NanitePackedLoadVertex(
    uint triangleId,
    uint corner,
    out NanitePackedVertex vertex)
{
    NanitePackedInitializeVertex(vertex);
    NanitePackedTriangleContext context;
    if (!NanitePackedResolveTriangleContext(triangleId, context))
        return false;
    return NanitePackedLoadVertex(context, corner, vertex);
}

bool NanitePackedLoadTriangle(
    uint triangleId,
    out NanitePackedVertex vertex0,
    out NanitePackedVertex vertex1,
    out NanitePackedVertex vertex2)
{
    NanitePackedInitializeVertex(vertex0);
    NanitePackedInitializeVertex(vertex1);
    NanitePackedInitializeVertex(vertex2);
    NanitePackedTriangleContext context;
    if (!NanitePackedResolveTriangleContext(triangleId, context))
        return false;

    bool valid0 = NanitePackedLoadVertex(context, 0u, vertex0);
    bool valid1 = NanitePackedLoadVertex(context, 1u, vertex1);
    bool valid2 = NanitePackedLoadVertex(context, 2u, vertex2);
    return valid0 && valid1 && valid2;
}

#endif
