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

bool GIRayBoxInterval(float3 rayOrigin, float3 rayDirection, float3 boundsMin, float3 boundsMax,
                      float maxDistance, out float entryT, out float exitT)
{
    float3 safeDirection = float3(
        abs(rayDirection.x) > 1e-7 ? rayDirection.x : (rayDirection.x >= 0.0 ? 1e-7 : -1e-7),
        abs(rayDirection.y) > 1e-7 ? rayDirection.y : (rayDirection.y >= 0.0 ? 1e-7 : -1e-7),
        abs(rayDirection.z) > 1e-7 ? rayDirection.z : (rayDirection.z >= 0.0 ? 1e-7 : -1e-7));
    float3 t0 = (boundsMin - rayOrigin) / safeDirection;
    float3 t1 = (boundsMax - rayOrigin) / safeDirection;
    float3 nearT = min(t0, t1);
    float3 farT = max(t0, t1);
    entryT = max(0.0, max(nearT.x, max(nearT.y, nearT.z)));
    exitT = min(maxDistance, min(farT.x, min(farT.y, farT.z)));
    return exitT >= entryT;
}

// Per-Brick Chebyshev distance is an isotropic lower bound. A ray never jumps past the current
// Brick boundary, so an occupied neighbour Brick cannot be skipped. The initial occupied run is
// treated as the emitting surface and escaped before a hit can be reported.
bool GITraceClipmapLayer(
    StructuredBuffer<GIClipmapLevelData> levels,
    StructuredBuffer<int> pageTable,
    StructuredBuffer<uint> occupancy,
    StructuredBuffer<uint> surface,
    StructuredBuffer<uint> surfaceIdentity,
    StructuredBuffer<uint> distanceWords,
    float3 rayOrigin,
    float3 rayDirection,
    float maxDistance,
    float minDistance,
    uint levelIndex,
    uint sourceIdentity,
    out float hitT,
    out uint packedSurface,
    out uint hitIdentity,
    out int3 hitWorldCell,
    out int hitPhysicalBrick,
    out uint hitLocalCell,
    out uint traceSteps,
    out float traceEndT)
{
    GIClipmapLevelData level = levels[levelIndex];
    float3 boundsMin = (float3)(level.originBrick * GI_BRICK_SIZE) * level.cellSize;
    float3 boundsMax = boundsMin + GI_CLIPMAP_RESOLUTION * level.cellSize;
    float entryT;
    float exitT;
    bool intersectsLevel = GIRayBoxInterval(
        rayOrigin, rayDirection, boundsMin, boundsMax, maxDistance, entryT, exitT);
    float minimumStep = level.cellSize * 0.75;
    float t = max(minDistance, entryT);
    float segmentLength = max(0.0, exitT - t);
    uint maxSteps = min(1024u, (uint)ceil(segmentLength / max(minimumStep, 1e-4)) + 1u);
    // Initialise every out value before the trace loop. Besides satisfying the HLSL compiler's
    // definite-assignment analysis, this keeps a degenerate zero-step trace deterministic.
    hitT = maxDistance;
    packedSurface = 0u;
    hitIdentity = 0u;
    hitWorldCell = 0;
    hitPhysicalBrick = -1;
    hitLocalCell = 0u;
    traceSteps = 0u;
    traceEndT = intersectsLevel ? max(t, exitT) : minDistance;
    if (!intersectsLevel || t > exitT)
        return false;

    uint escapedIdentity = sourceIdentity;
    bool canInferSource = sourceIdentity == 0u && entryT <= level.cellSize * 0.25 &&
                          minDistance <= level.cellSize * 0.25;
    bool sourceRunSeen = false;
    bool escapedSource = false;
    [loop]
    for (uint step = 0; step <= maxSteps && t <= exitT; step++)
    {
        traceSteps++;
        float3 position = rayOrigin + rayDirection * t;
        int3 worldCell = (int3)floor(position / level.cellSize);
        int physicalBrick;
        uint localCell;
        if (GIReadOccupancy(level, pageTable, occupancy, worldCell, physicalBrick, localCell))
        {
            uint cellIdentity = surfaceIdentity[
                (uint)physicalBrick * GI_SURFACE_WORDS_PER_BRICK + localCell];
            if (!escapedSource)
            {
                if (!sourceRunSeen && canInferSource)
                    escapedIdentity = cellIdentity;
                bool isSource = escapedIdentity != 0u && cellIdentity == escapedIdentity;
                if (isSource)
                {
                    sourceRunSeen = true;
                    t += minimumStep;
                    continue;
                }
                // If the ray started in empty space, a later occupied cell is a real hit.
                escapedSource = true;
            }
            hitT = t;
            packedSurface = surface[(uint)physicalBrick * GI_SURFACE_WORDS_PER_BRICK + localCell];
            hitIdentity = cellIdentity;
            hitWorldCell = worldCell;
            hitPhysicalBrick = physicalBrick;
            hitLocalCell = localCell;
            return true;
        }
        if (!sourceRunSeen)
            escapedSource = true;
        else
            escapedSource = true;
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
    hitIdentity = 0u;
    hitWorldCell = 0;
    hitPhysicalBrick = -1;
    hitLocalCell = 0u;
    return false;
}

bool GITraceUnifiedClipmaps(
    StructuredBuffer<GIClipmapLevelData> levels,
    StructuredBuffer<int> staticPageTable,
    StructuredBuffer<uint> staticOccupancy,
    StructuredBuffer<uint> staticSurface,
    StructuredBuffer<uint> staticSurfaceIdentity,
    StructuredBuffer<uint> staticDistance,
    StructuredBuffer<int> dynamicPageTable,
    StructuredBuffer<uint> dynamicOccupancy,
    StructuredBuffer<uint> dynamicSurface,
    StructuredBuffer<uint> dynamicSurfaceIdentity,
    StructuredBuffer<uint> dynamicDistance,
    float3 rayOrigin,
    float3 rayDirection,
    float maxDistance,
    uint preferredLevel,
    uint sourceIdentity,
    out float hitT,
    out uint packedSurface,
    out uint hitIdentity,
    out uint hitLayer,
    out uint hitLevel,
    out int3 hitWorldCell,
    out int hitPhysicalBrick,
    out uint hitLocalCell,
    out uint traceSteps)
{
    hitT = maxDistance;
    packedSurface = 0u;
    hitIdentity = 0u;
    hitLayer = 0u;
    hitLevel = 0u;
    hitWorldCell = 0;
    hitPhysicalBrick = -1;
    hitLocalCell = 0u;
    traceSteps = 0u;
    bool hit = false;
    float continuationT = 0.0;
    uint firstLevel = min(preferredLevel, (uint)(GI_CLIPMAP_LEVEL_COUNT - 1));
    [loop]
    for (uint level = firstLevel; level < GI_CLIPMAP_LEVEL_COUNT; level++)
    {
        float staticT;
        uint staticPacked;
        uint staticIdentity;
        int3 staticCell;
        int staticPhysicalBrick;
        uint staticLocalCell;
        uint staticSteps;
        float staticEndT;
        bool staticHit = GITraceClipmapLayer(
            levels, staticPageTable, staticOccupancy, staticSurface, staticSurfaceIdentity,
            staticDistance, rayOrigin, rayDirection, hitT, continuationT, level, sourceIdentity,
            staticT, staticPacked, staticIdentity, staticCell,
            staticPhysicalBrick, staticLocalCell, staticSteps, staticEndT);
        float dynamicT;
        uint dynamicPacked;
        uint dynamicIdentity;
        int3 dynamicCell;
        int dynamicPhysicalBrick;
        uint dynamicLocalCell;
        uint dynamicSteps;
        float dynamicEndT;
        bool dynamicHit = GITraceClipmapLayer(
            levels, dynamicPageTable, dynamicOccupancy, dynamicSurface, dynamicSurfaceIdentity,
            dynamicDistance, rayOrigin, rayDirection, hitT, continuationT, level, sourceIdentity,
            dynamicT, dynamicPacked, dynamicIdentity, dynamicCell,
            dynamicPhysicalBrick, dynamicLocalCell, dynamicSteps, dynamicEndT);
        traceSteps += staticSteps + dynamicSteps;
        if (staticHit && staticT <= hitT)
        {
            hit = true;
            hitT = staticT;
            packedSurface = staticPacked;
            hitIdentity = staticIdentity;
            hitLayer = 0u;
            hitLevel = level;
            hitWorldCell = staticCell;
            hitPhysicalBrick = staticPhysicalBrick;
            hitLocalCell = staticLocalCell;
        }
        if (dynamicHit && dynamicT <= hitT)
        {
            hit = true;
            hitT = dynamicT;
            packedSurface = dynamicPacked;
            hitIdentity = dynamicIdentity;
            hitLayer = 1u;
            hitLevel = level;
            hitWorldCell = dynamicCell;
            hitPhysicalBrick = dynamicPhysicalBrick;
            hitLocalCell = dynamicLocalCell;
        }
        if (hit)
            return true;
        // The finer level has authoritatively covered this ray segment. Coarser levels begin
        // where it ended instead of restarting at the source and rediscovering the same voxel.
        continuationT = max(continuationT, max(staticEndT, dynamicEndT));
        if (continuationT >= maxDistance)
            break;
    }
    return false;
}

#endif
