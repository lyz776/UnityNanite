using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace RealtimeGI
{
    public static class GIClipmapConstants
    {
        public const int LevelCount = 8;
        public const int LogicalResolution = 128;
        public const int BrickSize = 8;
        public const int BricksPerAxis = LogicalResolution / BrickSize;
        public const int BricksPerLevel = BricksPerAxis * BricksPerAxis * BricksPerAxis;
        public const int PageTableEntries = LevelCount * BricksPerLevel;
        public const int CellsPerBrick = BrickSize * BrickSize * BrickSize;
        public const int OccupancyWordsPerBrick = CellsPerBrick / 32;
        public const int SurfaceWordsPerBrick = CellsPerBrick;
        public const int SurfaceUvWordsPerBrick = CellsPerBrick;
        public const int SurfaceIdentityWordsPerBrick = CellsPerBrick;
        public const int SurfaceKeyWordsPerBrick = CellsPerBrick;
        public const int RadianceLobeCount = 6;
        public const int RadianceWordsPerBrick = CellsPerBrick * RadianceLobeCount;
        public const int ValidityWordsPerBrick = CellsPerBrick;
        // One uint per cell. The low byte stores conservative Chebyshev distance in cells.
        // Keeping cells independently writable makes iterative GPU propagation race-free.
        public const int DistanceWordsPerBrick = CellsPerBrick;
        public const int LevelStride = 32;
        public const int BrickDataStride = 16;

        public static readonly float[] CellSizes =
        {
            0.2f, 0.4f, 0.8f, 1.6f, 3.2f, 6.4f, 12.8f, 25.6f
        };

        public static void Validate()
        {
            int actualStride = Marshal.SizeOf<GIGpuClipmapLevelData>();
            if (actualStride != LevelStride)
                throw new InvalidOperationException(
                    $"GI Clipmap ABI mismatch: level stride C#={actualStride}, expected={LevelStride}.");
            if (CellSizes.Length != LevelCount)
                throw new InvalidOperationException("GI Clipmap cell-size table does not match LevelCount.");
            int brickStride = Marshal.SizeOf<GIGpuClipmapBrickData>();
            if (brickStride != BrickDataStride)
                throw new InvalidOperationException(
                    $"GI Clipmap ABI mismatch: brick stride C#={brickStride}, expected={BrickDataStride}.");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GIGpuClipmapBrickData
    {
        public Vector3Int worldBrick;
        public uint level;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GIGpuClipmapLevelData
    {
        public Vector3 worldOrigin;
        public float cellSize;
        public Vector3Int originBrick;
        public uint pageTableOffset;
    }

    public readonly struct GIClipmapBrickKey : IEquatable<GIClipmapBrickKey>
    {
        public readonly int level;
        public readonly Vector3Int worldBrick;

        public GIClipmapBrickKey(int level, Vector3Int worldBrick)
        {
            this.level = level;
            this.worldBrick = worldBrick;
        }

        public bool Equals(GIClipmapBrickKey other) =>
            level == other.level && worldBrick == other.worldBrick;

        public override bool Equals(object obj) => obj is GIClipmapBrickKey other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = level;
                hash = hash * 397 ^ worldBrick.x;
                hash = hash * 397 ^ worldBrick.y;
                hash = hash * 397 ^ worldBrick.z;
                return hash;
            }
        }
    }

    public readonly struct GIClipmapGpuView
    {
        public readonly GraphicsBuffer levelData;
        public readonly GraphicsBuffer staticPageTable;
        public readonly GraphicsBuffer staticOccupancy;
        public readonly GraphicsBuffer staticSurface;
        public readonly GraphicsBuffer staticSurfaceUv;
        public readonly GraphicsBuffer staticSurfaceIdentity;
        public readonly GraphicsBuffer staticDistance;
        public readonly GraphicsBuffer staticRadiance;
        public readonly GraphicsBuffer staticValidity;
        public readonly GraphicsBuffer staticLightCounts;
        public readonly GraphicsBuffer staticLightIndices;
        public readonly GraphicsBuffer dynamicPageTable;
        public readonly GraphicsBuffer dynamicOccupancy;
        public readonly GraphicsBuffer dynamicSurface;
        public readonly GraphicsBuffer dynamicSurfaceUv;
        public readonly GraphicsBuffer dynamicSurfaceIdentity;
        public readonly GraphicsBuffer dynamicDistance;
        public readonly GraphicsBuffer dynamicRadiance;
        public readonly GraphicsBuffer dynamicValidity;
        public readonly GraphicsBuffer dynamicLightCounts;
        public readonly GraphicsBuffer dynamicLightIndices;
        public readonly GraphicsBuffer localLights;
        public readonly int localLightCount;
        public readonly int lightsPerBrick;
        public readonly int staticBrickCount;
        public readonly int dynamicBrickCount;
        public readonly int generation;
        public readonly float skyIrradianceScale;
        public readonly float mainLightBounceScale;
        // Changes only when the lighting input/cache contents are invalidated.
        // Unlike generation, this is safe for deciding whether temporal screen history can be reused.
        public readonly int lightingRevision;

        internal GIClipmapGpuView(
            GraphicsBuffer levelData,
            GIClipmapLayer staticLayer,
            GIClipmapLayer dynamicLayer,
            GraphicsBuffer localLights,
            int localLightCount,
            int generation,
            int lightingRevision,
            float skyIrradianceScale,
            float mainLightBounceScale)
        {
            this.levelData = levelData;
            staticPageTable = staticLayer?.PageTableBuffer;
            staticOccupancy = staticLayer?.OccupancyBuffer;
            staticSurface = staticLayer?.SurfaceBuffer;
            staticSurfaceUv = staticLayer?.SurfaceUvBuffer;
            staticSurfaceIdentity = staticLayer?.SurfaceIdentityBuffer;
            staticDistance = staticLayer?.DistanceBuffer;
            staticRadiance = staticLayer?.RadianceBuffer;
            staticValidity = staticLayer?.ValidityBuffer;
            staticLightCounts = staticLayer?.LightCountBuffer;
            staticLightIndices = staticLayer?.LightIndexBuffer;
            dynamicPageTable = dynamicLayer?.PageTableBuffer;
            dynamicOccupancy = dynamicLayer?.OccupancyBuffer;
            dynamicSurface = dynamicLayer?.SurfaceBuffer;
            dynamicSurfaceUv = dynamicLayer?.SurfaceUvBuffer;
            dynamicSurfaceIdentity = dynamicLayer?.SurfaceIdentityBuffer;
            dynamicDistance = dynamicLayer?.DistanceBuffer;
            dynamicRadiance = dynamicLayer?.RadianceBuffer;
            dynamicValidity = dynamicLayer?.ValidityBuffer;
            dynamicLightCounts = dynamicLayer?.LightCountBuffer;
            dynamicLightIndices = dynamicLayer?.LightIndexBuffer;
            this.localLights = localLights;
            this.localLightCount = localLightCount;
            lightsPerBrick = staticLayer?.LightsPerBrick ?? 0;
            staticBrickCount = staticLayer?.AllocatedCount ?? 0;
            dynamicBrickCount = dynamicLayer?.AllocatedCount ?? 0;
            this.generation = generation;
            this.lightingRevision = lightingRevision;
            this.skyIrradianceScale = skyIrradianceScale;
            this.mainLightBounceScale = mainLightBounceScale;
        }

        public bool IsValid => levelData != null &&
                               staticPageTable != null && staticOccupancy != null &&
                               staticSurface != null && staticSurfaceUv != null && staticDistance != null &&
                               staticSurfaceIdentity != null &&
                               staticRadiance != null && staticValidity != null &&
                               staticLightCounts != null && staticLightIndices != null &&
                               dynamicPageTable != null && dynamicOccupancy != null &&
                               dynamicSurface != null && dynamicSurfaceUv != null && dynamicDistance != null &&
                               dynamicSurfaceIdentity != null &&
                               dynamicRadiance != null && dynamicValidity != null &&
                               dynamicLightCounts != null && dynamicLightIndices != null &&
                               localLights != null;
    }
}
