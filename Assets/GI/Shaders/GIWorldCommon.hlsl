// Porting reference:
// AKGWSB/UnrealEngine:4.27-akgi
// Engine/Shaders/Private/RealtimeGI/VoxelRaytracing.ush
// Rewritten for Unity compute shaders, a single page table and world-anchored bricks.
#ifndef UNITY_NANITE_GI_WORLD_COMMON_INCLUDED
#define UNITY_NANITE_GI_WORLD_COMMON_INCLUDED

#define GI_WORLD_LEVEL_COUNT 4
#define GI_WORLD_BRICK_SIZE 8
#define GI_WORLD_CELLS_PER_BRICK 512
#define GI_WORLD_SURFACE_WORDS_PER_CELL 3
#define GI_WORLD_OCCUPANCY_WORDS_PER_BRICK 16
#define GI_WORLD_PAGE_AXIS 16
#define GI_WORLD_PAGES_PER_LEVEL 4096

struct GIWorldLevelData
{
    float3 worldOrigin;
    float cellSize;
    int3 originBrick;
    uint pageTableOffset;
};

struct GIWorldBrickData
{
    int3 worldBrick;
    uint level;
    uint generation;
    uint valid;
    uint reserved0;
    uint reserved1;
};

int GIFloorDiv8(int value) { return value >> 3; }
int3 GIFloorDiv8(int3 value)
{
    return int3(GIFloorDiv8(value.x), GIFloorDiv8(value.y), GIFloorDiv8(value.z));
}

uint GILocalCellIndex(int3 cell)
{
    return (uint)(cell.x + cell.y * GI_WORLD_BRICK_SIZE +
                  cell.z * GI_WORLD_BRICK_SIZE * GI_WORLD_BRICK_SIZE);
}

bool GIResolveWorldCell(
    GIWorldLevelData level,
    StructuredBuffer<int> pageTable,
    StructuredBuffer<GIWorldBrickData> bricks,
    int3 worldCell,
    out int physicalBrick,
    out uint localCell)
{
    int3 worldBrick = GIFloorDiv8(worldCell);
    int3 localBrick = worldBrick - level.originBrick;
    if (any(localBrick < 0) || any(localBrick >= GI_WORLD_PAGE_AXIS))
    {
        physicalBrick = -1;
        localCell = 0;
        return false;
    }
    uint page = level.pageTableOffset + (uint)(localBrick.x +
        localBrick.y * GI_WORLD_PAGE_AXIS +
        localBrick.z * GI_WORLD_PAGE_AXIS * GI_WORLD_PAGE_AXIS);
    physicalBrick = pageTable[page];
    if (physicalBrick < 0 || bricks[physicalBrick].valid == 0u)
    {
        physicalBrick = -1;
        localCell = 0;
        return false;
    }
    localCell = GILocalCellIndex(worldCell - worldBrick * GI_WORLD_BRICK_SIZE);
    return true;
}

bool GIReadOccupied(
    GIWorldLevelData level,
    StructuredBuffer<int> pageTable,
    StructuredBuffer<GIWorldBrickData> bricks,
    StructuredBuffer<uint> occupancy,
    int3 worldCell,
    out int physicalBrick,
    out uint localCell)
{
    if (!GIResolveWorldCell(level, pageTable, bricks, worldCell, physicalBrick, localCell))
        return false;
    uint word = occupancy[(uint)physicalBrick * GI_WORLD_OCCUPANCY_WORDS_PER_BRICK + localCell / 32u];
    return (word & (1u << (localCell & 31u))) != 0u;
}

float3 GIUnpackOctNormal(uint packed)
{
    float2 oct = float2(packed & 255u, (packed >> 8u) & 255u) / 255.0 * 2.0 - 1.0;
    float3 normal = float3(oct, 1.0 - abs(oct.x) - abs(oct.y));
    if (normal.z < 0.0)
    {
        float2 signValue = float2(normal.x >= 0.0 ? 1.0 : -1.0,
                                  normal.y >= 0.0 ? 1.0 : -1.0);
        normal.xy = (1.0 - abs(normal.yx)) * signValue;
    }
    return normalize(normal);
}

float3 GIUnpackRgb8(uint packed)
{
    return float3(packed & 255u, (packed >> 8u) & 255u, (packed >> 16u) & 255u) / 255.0;
}

float3 GIUnpackRgbe8(uint packed)
{
    if (packed == 0u)
        return 0.0.xxx;
    float exponent = (float)((packed >> 24u) & 255u) - 128.0;
    return float3(packed & 255u, (packed >> 8u) & 255u,
                  (packed >> 16u) & 255u) * (exp2(exponent) / 255.0);
}

bool GIRayBox(float3 origin, float3 direction, float3 boundsMin, float3 boundsMax,
              float maxDistance, out float entryT, out float exitT)
{
    float3 safeDirection = float3(
        abs(direction.x) > 1e-6 ? direction.x : (direction.x >= 0.0 ? 1e-6 : -1e-6),
        abs(direction.y) > 1e-6 ? direction.y : (direction.y >= 0.0 ? 1e-6 : -1e-6),
        abs(direction.z) > 1e-6 ? direction.z : (direction.z >= 0.0 ? 1e-6 : -1e-6));
    float3 t0 = (boundsMin - origin) / safeDirection;
    float3 t1 = (boundsMax - origin) / safeDirection;
    float3 nearT = min(t0, t1);
    float3 farT = max(t0, t1);
    entryT = max(0.0, max(nearT.x, max(nearT.y, nearT.z)));
    exitT = min(maxDistance, min(farT.x, min(farT.y, farT.z)));
    return exitT >= entryT;
}

#endif
