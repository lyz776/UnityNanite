#ifndef NANITE_COMPACT_DRAW_INCLUDED
#define NANITE_COMPACT_DRAW_INCLUDED

// compact 后保存 VisibleCluster(firstTriangle, triangleCount, instance)，避免按虚拟三角形预留列表。
StructuredBuffer<uint> _CompactedTriIds;
StructuredBuffer<uint> _CompactedTriInstances;
StructuredBuffer<uint> _CompactedTriCounts;
StructuredBuffer<uint3> _CompactedDrawClusters;
StructuredBuffer<uint> _IndexedOverflowClusterIndices;
float _UseCompactedTriIds;
float _UseDirectVisibleDrawQueue;
float _UseIndexedOverflowClusterIndices;
float _CompactedClusterTriangleSlots;
float _CompactedClusterOffset;

#ifndef NANITE_COMPACT_CLUSTER_EXTRA_OFFSET
#define NANITE_COMPACT_CLUSTER_EXTRA_OFFSET 0u
#endif

uint NaniteCompactedTriangleSlots()
{
    return (uint)max(1, (int)_CompactedClusterTriangleSlots);
}

uint NaniteCompactedClusterIndex(uint vertexID)
{
    return (uint)max(0, (int)_CompactedClusterOffset) +
        NANITE_COMPACT_CLUSTER_EXTRA_OFFSET +
        (vertexID / 3u) / NaniteCompactedTriangleSlots();
}

uint NaniteCompactedTriangleInCluster(uint vertexID)
{
    return (vertexID / 3u) % NaniteCompactedTriangleSlots();
}

uint NaniteDirectClusterIndex(uint compactedClusterIndex)
{
    return _UseIndexedOverflowClusterIndices > 0.5
        ? _IndexedOverflowClusterIndices[compactedClusterIndex]
        : compactedClusterIndex;
}

uint3 NaniteLoadDirectCluster(uint compactedClusterIndex)
{
    return _CompactedDrawClusters[NaniteDirectClusterIndex(compactedClusterIndex)];
}

bool NaniteCompactedTriangleValid(uint vertexID)
{
    if (_UseCompactedTriIds <= 0.5)
        return true;
    uint cluster = NaniteCompactedClusterIndex(vertexID);
    uint triangleCount = _UseDirectVisibleDrawQueue > 0.5
        ? NaniteLoadDirectCluster(cluster).y
        : _CompactedTriCounts[cluster];
    return NaniteCompactedTriangleInCluster(vertexID) < triangleCount;
}

int NaniteResolveTriangleId(uint vertexID)
{
    uint localTri = vertexID / 3u;
    if (_UseCompactedTriIds > 0.5)
    {
        uint cluster = NaniteCompactedClusterIndex(vertexID);
        uint firstTriangle = _UseDirectVisibleDrawQueue > 0.5
            ? NaniteLoadDirectCluster(cluster).x
            : _CompactedTriIds[cluster];
        return (int)(firstTriangle + NaniteCompactedTriangleInCluster(vertexID));
    }
    return (int)localTri;
}

int NaniteResolveInstanceId(uint vertexID, int fallbackInstanceId)
{
    if (_UseCompactedTriIds > 0.5)
    {
        uint cluster = NaniteCompactedClusterIndex(vertexID);
        return _UseDirectVisibleDrawQueue > 0.5
            ? (int)NaniteLoadDirectCluster(cluster).z
            : (int)_CompactedTriInstances[cluster];
    }
    return fallbackInstanceId;
}

uint NaniteCompactedClusterIndexFromPrimitive(uint primitiveID)
{
    return (uint)max(0, (int)_CompactedClusterOffset) +
        NANITE_COMPACT_CLUSTER_EXTRA_OFFSET +
        primitiveID / NaniteCompactedTriangleSlots();
}

uint NaniteCompactedTriangleInClusterFromPrimitive(uint primitiveID)
{
    return primitiveID % NaniteCompactedTriangleSlots();
}

int NaniteResolveTriangleIdFromPrimitive(uint primitiveID)
{
    uint cluster = NaniteCompactedClusterIndexFromPrimitive(primitiveID);
    uint firstTriangle = _UseDirectVisibleDrawQueue > 0.5
        ? NaniteLoadDirectCluster(cluster).x
        : _CompactedTriIds[cluster];
    return (int)(firstTriangle + NaniteCompactedTriangleInClusterFromPrimitive(primitiveID));
}

int NaniteResolveInstanceIdFromPrimitive(uint primitiveID, int fallbackInstanceId)
{
    if (_UseCompactedTriIds <= 0.5)
        return fallbackInstanceId;
    uint cluster = NaniteCompactedClusterIndexFromPrimitive(primitiveID);
    return _UseDirectVisibleDrawQueue > 0.5
        ? (int)NaniteLoadDirectCluster(cluster).z
        : (int)_CompactedTriInstances[cluster];
}

int NaniteResolveCorner(uint vertexID)
{
    return (int)(vertexID % 3u);
}

// The baked triangle stream already uses the graphics API front-face
// convention. Procedural raster only needs to compensate mirrored instances;
// applying an unconditional import flip turns exterior surfaces into backfaces.
int NaniteResolveWindingCorner(uint vertexID, float4x4 localToWorld)
{
    int corner = NaniteResolveCorner(vertexID);
    bool mirrored = determinant((float3x3)localToWorld) < 0.0;
    bool flip = mirrored;
    if (flip && corner != 0)
        corner = 3 - corner;
    return corner;
}

int NaniteFetchLogicalIndex(StructuredBuffer<int> indices, uint vertexID)
{
    int triId = NaniteResolveTriangleId(vertexID);
    int corner = NaniteResolveCorner(vertexID);
    return indices[triId * 3 + corner];
}

int NaniteFetchLogicalIndexWinding(
    StructuredBuffer<int> indices,
    uint vertexID,
    float4x4 localToWorld)
{
    int triId = NaniteResolveTriangleId(vertexID);
    int corner = NaniteResolveWindingCorner(vertexID, localToWorld);
    return indices[triId * 3 + corner];
}

#endif
