#ifndef NANITE_COMPACT_DRAW_INCLUDED
#define NANITE_COMPACT_DRAW_INCLUDED

// compact 后保存 VisibleCluster(firstTriangle, triangleCount, instance)，避免按虚拟三角形预留列表。
StructuredBuffer<uint> _CompactedTriIds;
StructuredBuffer<uint> _CompactedTriInstances;
StructuredBuffer<uint> _CompactedTriCounts;
StructuredBuffer<uint3> _CompactedDrawClusters;
float _UseCompactedTriIds;
float _UseDirectVisibleDrawQueue;
float _CompactedClusterTriangleSlots;

uint NaniteCompactedTriangleSlots()
{
    return (uint)max(1, (int)_CompactedClusterTriangleSlots);
}

uint NaniteCompactedClusterIndex(uint vertexID)
{
    return (vertexID / 3u) / NaniteCompactedTriangleSlots();
}

uint NaniteCompactedTriangleInCluster(uint vertexID)
{
    return (vertexID / 3u) % NaniteCompactedTriangleSlots();
}

bool NaniteCompactedTriangleValid(uint vertexID)
{
    if (_UseCompactedTriIds <= 0.5)
        return true;
    uint cluster = NaniteCompactedClusterIndex(vertexID);
    uint triangleCount = _UseDirectVisibleDrawQueue > 0.5
        ? _CompactedDrawClusters[cluster].y
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
            ? _CompactedDrawClusters[cluster].x
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
            ? (int)_CompactedDrawClusters[cluster].z
            : (int)_CompactedTriInstances[cluster];
    }
    return fallbackInstanceId;
}

uint NaniteCompactedClusterIndexFromPrimitive(uint primitiveID)
{
    return primitiveID / NaniteCompactedTriangleSlots();
}

uint NaniteCompactedTriangleInClusterFromPrimitive(uint primitiveID)
{
    return primitiveID % NaniteCompactedTriangleSlots();
}

int NaniteResolveTriangleIdFromPrimitive(uint primitiveID)
{
    uint cluster = NaniteCompactedClusterIndexFromPrimitive(primitiveID);
    uint firstTriangle = _UseDirectVisibleDrawQueue > 0.5
        ? _CompactedDrawClusters[cluster].x
        : _CompactedTriIds[cluster];
    return (int)(firstTriangle + NaniteCompactedTriangleInClusterFromPrimitive(primitiveID));
}

int NaniteResolveInstanceIdFromPrimitive(uint primitiveID, int fallbackInstanceId)
{
    if (_UseCompactedTriIds <= 0.5)
        return fallbackInstanceId;
    uint cluster = NaniteCompactedClusterIndexFromPrimitive(primitiveID);
    return _UseDirectVisibleDrawQueue > 0.5
        ? (int)_CompactedDrawClusters[cluster].z
        : (int)_CompactedTriInstances[cluster];
}

int NaniteResolveCorner(uint vertexID)
{
    return (int)(vertexID % 3u);
}

int NaniteFetchLogicalIndex(StructuredBuffer<int> indices, uint vertexID)
{
    int triId = NaniteResolveTriangleId(vertexID);
    int corner = NaniteResolveCorner(vertexID);
    return indices[triId * 3 + corner];
}

#endif
