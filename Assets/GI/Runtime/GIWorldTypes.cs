using System.Runtime.InteropServices;
using UnityEngine;

namespace UnityNanite.GI
{
    public static class GIWorldConstants
    {
        public const int LevelCount = 4;
        public const int BrickSize = 8;
        public const int CellsPerBrick = BrickSize * BrickSize * BrickSize;
        public const int SurfaceWordsPerCell = 3;
        public const int OccupancyWordsPerBrick = CellsPerBrick / 32;
        public const int PageTableAxis = 16;
        public const int PagesPerLevel = PageTableAxis * PageTableAxis * PageTableAxis;
        public const int PageTableEntries = LevelCount * PagesPerLevel;
        public const int LevelStride = 32;
        public const int BrickStride = 32;
        public const int TriangleStride = 64;
        public const int TriangleWorkStride = 12;
        public const int MeshInstanceStride = 80;
        public const int NaniteInstanceStride = 80;
        public const int NaniteTriangleWorkStride = 24;
        public static readonly float[] CellSizes = { 0.25f, 0.5f, 1f, 2f };
    }

    public static class GIWorldMaterialPacking
    {
        public static uint PackBaseColor(Material material)
        {
            Color color = material != null && material.HasProperty("_BaseColor")
                ? material.GetColor("_BaseColor")
                : material != null && material.HasProperty("_Color")
                    ? material.GetColor("_Color")
                    : Color.white;
            Color linear = QualitySettings.activeColorSpace == ColorSpace.Linear ? color : color.linear;
            uint r = (uint)Mathf.RoundToInt(Mathf.Clamp01(linear.r) * 255f);
            uint g = (uint)Mathf.RoundToInt(Mathf.Clamp01(linear.g) * 255f);
            uint b = (uint)Mathf.RoundToInt(Mathf.Clamp01(linear.b) * 255f);
            return r | (g << 8) | (b << 16) | 0xff000000u;
        }

        public static uint PackEmission(Material material)
        {
            if (material == null || !material.HasProperty("_EmissionColor"))
                return 0u;

            Color color = material.GetColor("_EmissionColor");
            Color linear = QualitySettings.activeColorSpace == ColorSpace.Linear ? color : color.linear;
            float maximum = Mathf.Max(0f, Mathf.Max(linear.r, Mathf.Max(linear.g, linear.b)));
            bool enabled = material.IsKeywordEnabled("_EMISSION") ||
                           (material.HasProperty("_EmissionEnabled") &&
                            material.GetFloat("_EmissionEnabled") > 0.5f);
            if (!enabled || maximum <= 0f)
                return 0u;

            // RGBE8 preserves the HDR intensity needed by emissive GI in one surface word.
            int exponent = Mathf.Clamp(Mathf.CeilToInt(Mathf.Log(maximum, 2f)), -127, 127);
            float scale = 255f / Mathf.Pow(2f, exponent);
            uint r = (uint)Mathf.RoundToInt(Mathf.Clamp(Mathf.Max(0f, linear.r) * scale, 0f, 255f));
            uint g = (uint)Mathf.RoundToInt(Mathf.Clamp(Mathf.Max(0f, linear.g) * scale, 0f, 255f));
            uint b = (uint)Mathf.RoundToInt(Mathf.Clamp(Mathf.Max(0f, linear.b) * scale, 0f, 255f));
            return r | (g << 8) | (b << 16) | ((uint)(exponent + 128) << 24);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GIWorldLevelData
    {
        public Vector3 worldOrigin;
        public float cellSize;
        public Vector3Int originBrick;
        public uint pageTableOffset;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GIWorldBrickData
    {
        public Vector3Int worldBrick;
        public uint level;
        public uint generation;
        public uint valid;
        public uint reserved0;
        public uint reserved1;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GIWorldTriangle
    {
        public Vector3 position0;
        public uint material;
        public Vector3 position1;
        public uint sourceId;
        public Vector3 position2;
        public uint flags;
        public Vector3 normal;
        public uint reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GIWorldTriangleWork
    {
        public uint triangleIndex;
        public uint physicalBrick;
        public uint instanceIndex;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GIMeshGpuInstance
    {
        public Matrix4x4 localToWorld;
        public uint material;
        public uint sourceId;
        public uint flags;
        public uint reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GINaniteGpuInstance
    {
        public Matrix4x4 localToWorld;
        public uint material;
        public uint sourceId;
        public uint reserved0;
        public uint reserved1;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GINaniteTriangleWork
    {
        public uint pageId;
        public uint triangleCount;
        public uint physicalBrick;
        public uint instanceIndex;
        public uint keyBase;
        public uint reserved;
    }

    public readonly struct GIWorldGpuView
    {
        public readonly GraphicsBuffer levels;
        public readonly GraphicsBuffer pageTable;
        public readonly GraphicsBuffer bricks;
        public readonly GraphicsBuffer occupancy;
        public readonly GraphicsBuffer surface;
        public readonly GraphicsBuffer distance;
        public readonly int brickCount;
        public readonly uint generation;

        internal GIWorldGpuView(
            GraphicsBuffer levels,
            GraphicsBuffer pageTable,
            GraphicsBuffer bricks,
            GraphicsBuffer occupancy,
            GraphicsBuffer surface,
            GraphicsBuffer distance,
            int brickCount,
            uint generation)
        {
            this.levels = levels;
            this.pageTable = pageTable;
            this.bricks = bricks;
            this.occupancy = occupancy;
            this.surface = surface;
            this.distance = distance;
            this.brickCount = brickCount;
            this.generation = generation;
        }

        public bool IsValid => levels != null && pageTable != null && bricks != null &&
                               occupancy != null && surface != null && distance != null &&
                               brickCount > 0;
    }
}
