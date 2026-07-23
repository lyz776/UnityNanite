#ifndef NANITE_COMPACT_DRAW_INCLUDED
#define NANITE_COMPACT_DRAW_INCLUDED

// compact 后：SV_VertexID 落在 [0, visibleTris*3)，需经 _CompactedTriIds 还原全局 triangleId。
StructuredBuffer<uint> _CompactedTriIds;
float _UseCompactedTriIds;

int NaniteResolveTriangleId(uint vertexID)
{
    uint localTri = vertexID / 3u;
    if (_UseCompactedTriIds > 0.5)
        return (int)_CompactedTriIds[localTri];
    return (int)localTri;
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
