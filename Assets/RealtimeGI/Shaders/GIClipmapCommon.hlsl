#ifndef REALTIME_GI_CLIPMAP_COMMON_INCLUDED
#define REALTIME_GI_CLIPMAP_COMMON_INCLUDED

#define GI_CLIPMAP_LEVEL_COUNT 8
#define GI_CLIPMAP_RESOLUTION 128
#define GI_BRICK_SIZE 8
#define GI_BRICKS_PER_AXIS 16
#define GI_BRICKS_PER_LEVEL 4096
#define GI_CELLS_PER_BRICK 512
#define GI_OCCUPANCY_WORDS_PER_BRICK 16
#define GI_SURFACE_WORDS_PER_BRICK 512
#define GI_SURFACE_UV_WORDS_PER_BRICK 512
#define GI_DISTANCE_WORDS_PER_BRICK 512
#define GI_RADIANCE_LOBE_COUNT 6
#define GI_RADIANCE_WORDS_PER_BRICK 3072

struct GIClipmapLevelData
{
    float3 worldOrigin;
    float cellSize;
    int3 originBrick;
    uint pageTableOffset;
};

struct GIGpuClipmapBrickData
{
    int3 worldBrick;
    uint level;
};

int GIFloorDiv8(int value)
{
    // Signed arithmetic shift is floor division for this power-of-two brick size.
    return value >> 3;
}

int3 GIFloorDiv8(int3 value)
{
    return int3(GIFloorDiv8(value.x), GIFloorDiv8(value.y), GIFloorDiv8(value.z));
}

bool GIResolvePhysicalBrick(
    GIClipmapLevelData level,
    StructuredBuffer<int> pageTable,
    int3 worldCell,
    out int physicalBrick,
    out uint localCellIndex)
{
    int3 worldBrick = GIFloorDiv8(worldCell);
    int3 localBrick = worldBrick - level.originBrick;
    if (any(localBrick < 0) || any(localBrick >= GI_BRICKS_PER_AXIS))
    {
        physicalBrick = -1;
        localCellIndex = 0;
        return false;
    }
    uint address = level.pageTableOffset +
        (uint)(localBrick.x + localBrick.y * GI_BRICKS_PER_AXIS +
               localBrick.z * GI_BRICKS_PER_AXIS * GI_BRICKS_PER_AXIS);
    physicalBrick = pageTable[address];
    int3 localCell = worldCell - worldBrick * GI_BRICK_SIZE;
    localCellIndex = (uint)(localCell.x + localCell.y * GI_BRICK_SIZE +
        localCell.z * GI_BRICK_SIZE * GI_BRICK_SIZE);
    return physicalBrick >= 0;
}

bool GIReadOccupancy(
    GIClipmapLevelData level,
    StructuredBuffer<int> pageTable,
    StructuredBuffer<uint> occupancy,
    int3 worldCell,
    out int physicalBrick,
    out uint localCellIndex)
{
    if (!GIResolvePhysicalBrick(level, pageTable, worldCell, physicalBrick, localCellIndex))
        return false;
    uint word = occupancy[(uint)physicalBrick * GI_OCCUPANCY_WORDS_PER_BRICK + localCellIndex / 32u];
    return (word & (1u << (localCellIndex & 31u))) != 0u;
}

uint GIReadConservativeDistance(
    int physicalBrick,
    uint localCellIndex,
    StructuredBuffer<uint> distanceWords)
{
    return distanceWords[(uint)physicalBrick * GI_DISTANCE_WORDS_PER_BRICK + localCellIndex] & 0xffu;
}

// Per-Brick Chebyshev distance is an isotropic lower bound. A ray never jumps past the current
// Brick boundary, so an occupied neighbour Brick cannot be skipped.
bool GITraceClipmapLayer(
    StructuredBuffer<GIClipmapLevelData> levels,
    StructuredBuffer<int> pageTable,
    StructuredBuffer<uint> occupancy,
    StructuredBuffer<uint> surface,
    StructuredBuffer<uint> distanceWords,
    float3 rayOrigin,
    float3 rayDirection,
    float maxDistance,
    uint levelIndex,
    out float hitT,
    out uint packedSurface,
    out uint traceSteps)
{
    GIClipmapLevelData level = levels[levelIndex];
    float minimumStep = level.cellSize * 0.75;
    uint maxSteps = min(1024u, (uint)ceil(maxDistance / max(minimumStep, 1e-4)));
    float t = 0.0;
    traceSteps = 0u;
    [loop]
    for (uint step = 0; step <= maxSteps && t <= maxDistance; step++)
    {
        traceSteps++;
        float3 position = rayOrigin + rayDirection * t;
        int3 worldCell = (int3)floor(position / level.cellSize);
        int physicalBrick;
        uint localCell;
        if (GIReadOccupancy(level, pageTable, occupancy, worldCell, physicalBrick, localCell))
        {
            hitT = t;
            packedSurface = surface[(uint)physicalBrick * GI_SURFACE_WORDS_PER_BRICK + localCell];
            return true;
        }
        // An unallocated sparse Brick is empty, so advance directly to its boundary.
        float safeCells = (float)GI_BRICK_SIZE;
        if (physicalBrick >= 0)
        {
            uint distanceCells = GIReadConservativeDistance(physicalBrick, localCell, distanceWords);
            safeCells = max(0.75, (float)distanceCells - 1.0);
        }
        int3 worldBrick = GIFloorDiv8(worldCell);
        float3 brickMin = (float3)(worldBrick * GI_BRICK_SIZE) * level.cellSize;
        float3 brickMax = brickMin + GI_BRICK_SIZE * level.cellSize;
        float3 safeDirection = float3(
            abs(rayDirection.x) > 1e-6 ? rayDirection.x : 1e-6,
            abs(rayDirection.y) > 1e-6 ? rayDirection.y : 1e-6,
            abs(rayDirection.z) > 1e-6 ? rayDirection.z : 1e-6);
        float3 boundary = float3(
            rayDirection.x >= 0.0 ? brickMax.x : brickMin.x,
            rayDirection.y >= 0.0 ? brickMax.y : brickMin.y,
            rayDirection.z >= 0.0 ? brickMax.z : brickMin.z);
        float3 boundaryT3 = (boundary - position) / safeDirection;
        float boundaryT = min(boundaryT3.x, min(boundaryT3.y, boundaryT3.z));
        float conservativeStep = min(safeCells * level.cellSize,
                                     max(minimumStep, boundaryT + level.cellSize * 0.01));
        t += max(minimumStep, conservativeStep);
    }
    hitT = maxDistance;
    packedSurface = 0u;
    return false;
}

bool GITraceUnifiedClipmaps(
    StructuredBuffer<GIClipmapLevelData> levels,
    StructuredBuffer<int> staticPageTable,
    StructuredBuffer<uint> staticOccupancy,
    StructuredBuffer<uint> staticSurface,
    StructuredBuffer<uint> staticDistance,
    StructuredBuffer<int> dynamicPageTable,
    StructuredBuffer<uint> dynamicOccupancy,
    StructuredBuffer<uint> dynamicSurface,
    StructuredBuffer<uint> dynamicDistance,
    float3 rayOrigin,
    float3 rayDirection,
    float maxDistance,
    uint preferredLevel,
    out float hitT,
    out uint packedSurface,
    out uint hitLayer,
    out uint hitLevel,
    out uint traceSteps)
{
    hitT = maxDistance;
    packedSurface = 0u;
    hitLayer = 0u;
    hitLevel = 0u;
    traceSteps = 0u;
    bool hit = false;
    uint firstLevel = min(preferredLevel, (uint)(GI_CLIPMAP_LEVEL_COUNT - 1));
    [loop]
    for (uint level = firstLevel; level < GI_CLIPMAP_LEVEL_COUNT; level++)
    {
        float staticT;
        uint staticPacked;
        uint staticSteps;
        bool staticHit = GITraceClipmapLayer(
            levels, staticPageTable, staticOccupancy, staticSurface, staticDistance,
            rayOrigin, rayDirection, hitT, level, staticT, staticPacked, staticSteps);
        float dynamicT;
        uint dynamicPacked;
        uint dynamicSteps;
        bool dynamicHit = GITraceClipmapLayer(
            levels, dynamicPageTable, dynamicOccupancy, dynamicSurface, dynamicDistance,
            rayOrigin, rayDirection, hitT, level, dynamicT, dynamicPacked, dynamicSteps);
        traceSteps += staticSteps + dynamicSteps;
        if (staticHit && staticT <= hitT)
        {
            hit = true;
            hitT = staticT;
            packedSurface = staticPacked;
            hitLayer = 0u;
            hitLevel = level;
        }
        if (dynamicHit && dynamicT <= hitT)
        {
            hit = true;
            hitT = dynamicT;
            packedSurface = dynamicPacked;
            hitLayer = 1u;
            hitLevel = level;
        }
        if (hit)
            return true;
    }
    return false;
}

#endif
