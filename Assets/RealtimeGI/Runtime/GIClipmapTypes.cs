using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

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
        // uint2(objectId, surfaceKey) per cell.  The transient instance index remains in
        // SurfaceIdentity for same-frame triangle reconstruction; this pair is safe to persist.
        public const int SurfaceStableIdEntriesPerBrick = CellsPerBrick;
        public const int SurfaceKeyWordsPerBrick = CellsPerBrick;
        public const int RadianceLobeCount = 6;
        public const int RadianceWordsPerBrick = CellsPerBrick * RadianceLobeCount;
        public const int ValidityWordsPerBrick = CellsPerBrick;
        public const int IrradianceProbeAxis = 2;
        public const int IrradianceProbesPerBrick = IrradianceProbeAxis * IrradianceProbeAxis * IrradianceProbeAxis;
        public const int IrradianceWordsPerBrick = IrradianceProbesPerBrick * RadianceLobeCount;
        public const int IrradianceProbeValidityWordsPerBrick = IrradianceProbesPerBrick;
        // One uint per cell. The low byte stores conservative Chebyshev distance in cells.
        // Keeping cells independently writable makes iterative GPU propagation race-free.
        public const int DistanceWordsPerBrick = CellsPerBrick;
        public const int LevelStride = 32;
        public const int BrickDataStride = 16;
        public const int AllocatorStateWords = 8;
        // dirty dispatch xyz + radiance dispatch xyz.
        public const int PageDispatchArgumentWords = 6;

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
        public readonly GraphicsBuffer staticSurfaceStableId;
        public readonly GraphicsBuffer staticSurfacePrimitive;
        public readonly GraphicsBuffer staticDistance;
        public readonly GraphicsBuffer staticRadiance;
        public readonly GraphicsBuffer staticValidity;
        public readonly GraphicsBuffer staticIrradiance;
        public readonly GraphicsBuffer staticIrradianceValidity;
        public readonly GraphicsBuffer staticLightCounts;
        public readonly GraphicsBuffer staticLightIndices;
        public readonly GraphicsBuffer dynamicPageTable;
        public readonly GraphicsBuffer dynamicOccupancy;
        public readonly GraphicsBuffer dynamicSurface;
        public readonly GraphicsBuffer dynamicSurfaceUv;
        public readonly GraphicsBuffer dynamicSurfaceIdentity;
        public readonly GraphicsBuffer dynamicSurfaceStableId;
        public readonly GraphicsBuffer dynamicSurfacePrimitive;
        public readonly GraphicsBuffer dynamicDistance;
        public readonly GraphicsBuffer dynamicRadiance;
        public readonly GraphicsBuffer dynamicValidity;
        public readonly GraphicsBuffer dynamicIrradiance;
        public readonly GraphicsBuffer dynamicIrradianceValidity;
        public readonly GraphicsBuffer dynamicLightCounts;
        public readonly GraphicsBuffer dynamicLightIndices;
        public readonly GraphicsBuffer localLights;
        public readonly GraphicsBuffer geometryRanges;
        public readonly GraphicsBuffer vertices;
        public readonly GraphicsBuffer uvs;
        public readonly GraphicsBuffer indices;
        public readonly GraphicsBuffer triangleSubMeshes;
        public readonly GraphicsBuffer bvhNodes;
        public readonly GraphicsBuffer bvhPrimitives;
        public readonly GraphicsBuffer bvhRanges;
        public readonly int tlasNodeCount;
        public readonly int tlasNodeOffset;
        public readonly GraphicsBuffer nanitePageTable;
        public readonly GraphicsBuffer naniteResidentPageTable;
        public readonly GraphicsBuffer naniteResidencyBits;
        public readonly GraphicsBuffer naniteResidentVertices;
        public readonly GraphicsBuffer naniteResidentIndices;
        public readonly GraphicsBuffer naniteResidentTriangleSubMeshes;
        public readonly int nanitePageCount;
        public readonly uint nanitePoolGeneration;
        public readonly uint naniteGeometryReadyFlag;
        public readonly RTHandle baseColorTextures;
        public readonly RTHandle emissionTextures;
        public readonly RTHandle maskTextures;
        public readonly int textureSliceCount;
        public readonly int localLightCount;
        public readonly int lightsPerBrick;
        public readonly int staticBrickCount;
        public readonly int dynamicBrickCount;
        public readonly int generation;
        // Changes only when the lighting input/cache contents are invalidated.
        // Unlike generation, this is safe for deciding whether temporal screen history can be reused.
        public readonly int lightingRevision;

        internal GIClipmapGpuView(
            GraphicsBuffer levelData,
            GIClipmapLayer staticLayer,
            GIClipmapLayer dynamicLayer,
            GraphicsBuffer localLights,
            GIGeometryStreamCache geometryCache,
            GraphicsBuffer nanitePageTable,
            GraphicsBuffer naniteResidentPageTable,
            GraphicsBuffer naniteResidencyBits,
            GraphicsBuffer naniteResidentVertices,
            GraphicsBuffer naniteResidentIndices,
            GraphicsBuffer naniteResidentTriangleSubMeshes,
            int nanitePageCount,
            uint nanitePoolGeneration,
            uint naniteGeometryReadyFlag,
            RTHandle baseColorTextures,
            RTHandle emissionTextures,
            RTHandle maskTextures,
            int textureSliceCount,
            int localLightCount,
            int generation,
            int lightingRevision)
        {
            this.levelData = levelData;
            staticPageTable = staticLayer?.PageTableBuffer;
            staticOccupancy = staticLayer?.OccupancyBuffer;
            staticSurface = staticLayer?.SurfaceBuffer;
            staticSurfaceUv = staticLayer?.SurfaceUvBuffer;
            staticSurfaceIdentity = staticLayer?.SurfaceIdentityBuffer;
            staticSurfaceStableId = staticLayer?.SurfaceStableIdBuffer;
            staticSurfacePrimitive = staticLayer?.SurfaceKeyBuffer;
            staticDistance = staticLayer?.DistanceBuffer;
            staticRadiance = staticLayer?.RadianceBuffer;
            staticValidity = staticLayer?.ValidityBuffer;
            staticIrradiance = staticLayer?.IrradianceBuffer;
            staticIrradianceValidity = staticLayer?.IrradianceValidityBuffer;
            staticLightCounts = staticLayer?.LightCountBuffer;
            staticLightIndices = staticLayer?.LightIndexBuffer;
            dynamicPageTable = dynamicLayer?.PageTableBuffer;
            dynamicOccupancy = dynamicLayer?.OccupancyBuffer;
            dynamicSurface = dynamicLayer?.SurfaceBuffer;
            dynamicSurfaceUv = dynamicLayer?.SurfaceUvBuffer;
            dynamicSurfaceIdentity = dynamicLayer?.SurfaceIdentityBuffer;
            dynamicSurfaceStableId = dynamicLayer?.SurfaceStableIdBuffer;
            dynamicSurfacePrimitive = dynamicLayer?.SurfaceKeyBuffer;
            dynamicDistance = dynamicLayer?.DistanceBuffer;
            dynamicRadiance = dynamicLayer?.RadianceBuffer;
            dynamicValidity = dynamicLayer?.ValidityBuffer;
            dynamicIrradiance = dynamicLayer?.IrradianceBuffer;
            dynamicIrradianceValidity = dynamicLayer?.IrradianceValidityBuffer;
            dynamicLightCounts = dynamicLayer?.LightCountBuffer;
            dynamicLightIndices = dynamicLayer?.LightIndexBuffer;
            this.localLights = localLights;
            geometryRanges = geometryCache?.RangeBuffer;
            vertices = geometryCache?.VertexBuffer;
            uvs = geometryCache?.UvBuffer;
            indices = geometryCache?.IndexBuffer;
            triangleSubMeshes = geometryCache?.TriangleSubMeshBuffer;
            bvhNodes = geometryCache?.BvhNodeBuffer;
            bvhPrimitives = geometryCache?.BvhPrimitiveBuffer;
            bvhRanges = geometryCache?.BvhRangeBuffer;
            tlasNodeCount = geometryCache?.TlasNodeCount ?? 0;
            tlasNodeOffset = geometryCache?.TlasNodeOffset ?? 0;
            this.nanitePageTable = nanitePageTable;
            this.naniteResidentPageTable = naniteResidentPageTable;
            this.naniteResidencyBits = naniteResidencyBits;
            this.naniteResidentVertices = naniteResidentVertices;
            this.naniteResidentIndices = naniteResidentIndices;
            this.naniteResidentTriangleSubMeshes = naniteResidentTriangleSubMeshes;
            this.nanitePageCount = nanitePageCount;
            this.nanitePoolGeneration = nanitePoolGeneration;
            this.naniteGeometryReadyFlag = naniteGeometryReadyFlag;
            this.baseColorTextures = baseColorTextures;
            this.emissionTextures = emissionTextures;
            this.maskTextures = maskTextures;
            this.textureSliceCount = textureSliceCount;
            this.localLightCount = localLightCount;
            lightsPerBrick = staticLayer?.LightsPerBrick ?? 0;
            // Allocation counts are GPU-owned. They stay zero here unless an optional
            // asynchronous diagnostics readback publishes a snapshot.
            staticBrickCount = 0;
            dynamicBrickCount = 0;
            this.generation = generation;
            this.lightingRevision = lightingRevision;
        }

        public bool IsValid => levelData != null &&
                               staticPageTable != null && staticOccupancy != null &&
                               staticSurface != null && staticSurfaceUv != null && staticDistance != null &&
                               staticSurfaceIdentity != null &&
                               staticRadiance != null && staticValidity != null &&
                               staticIrradiance != null && staticIrradianceValidity != null &&
                               staticLightCounts != null && staticLightIndices != null &&
                               dynamicPageTable != null && dynamicOccupancy != null &&
                               dynamicSurface != null && dynamicSurfaceUv != null && dynamicDistance != null &&
                               dynamicSurfaceIdentity != null &&
                               dynamicRadiance != null && dynamicValidity != null &&
                               dynamicIrradiance != null && dynamicIrradianceValidity != null &&
                               dynamicLightCounts != null && dynamicLightIndices != null &&
                               staticSurfaceStableId != null && staticSurfacePrimitive != null &&
                               dynamicSurfaceStableId != null && dynamicSurfacePrimitive != null &&
                               localLights != null && geometryRanges != null && vertices != null &&
                               uvs != null && indices != null && triangleSubMeshes != null &&
                               bvhNodes != null && bvhPrimitives != null && bvhRanges != null &&
                               nanitePageTable != null && naniteResidentPageTable != null &&
                               naniteResidencyBits != null && naniteResidentVertices != null &&
                               naniteResidentIndices != null &&
                               naniteResidentTriangleSubMeshes != null &&
                               baseColorTextures != null &&
                               emissionTextures != null && maskTextures != null;
    }
}
