using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace RealtimeGI
{
    [Flags]
    public enum GIInstanceFlags : uint
    {
        None = 0,
        Occluder = 1u << 0,
        Contributor = 1u << 1,
        Receiver = 1u << 2,
        Dynamic = 1u << 3,
        TwoSided = 1u << 4,
        AlphaTested = 1u << 5,
        Emissive = 1u << 6
    }

    public enum GIMobility
    {
        Auto = 0,
        Static = 1,
        Dynamic = 2
    }

    public enum GIGeometryKind : uint
    {
        UnityMesh = 0,
        NaniteMesh = 1,
        AnalyticSphere = 2,
        AnalyticBox = 3,
        AnalyticCapsule = 4
    }

    /// <summary>
    /// Version shared by C# producers and future compute consumers. Increment whenever a GPU
    /// structure changes; never silently reinterpret an old stride.
    /// </summary>
    public static class GISceneAbi
    {
        public const uint Version = 2;
        public const int InstanceStride = 176;
        public const int GeometryStride = 48;
        public const int MaterialStride = 96;
        public const int MaterialBindingStride = 16;
        public const int GeometryStreamStride = 32;

        static bool validated;

        public static void Validate()
        {
            if (validated)
                return;
            ValidateStride<GIGpuInstanceData>(InstanceStride, nameof(GIGpuInstanceData));
            ValidateStride<GIGpuGeometryData>(GeometryStride, nameof(GIGpuGeometryData));
            ValidateStride<GIGpuMaterialData>(MaterialStride, nameof(GIGpuMaterialData));
            ValidateStride<GIGpuMaterialBindingData>(MaterialBindingStride, nameof(GIGpuMaterialBindingData));
            ValidateStride<GIGpuGeometryStreamData>(GeometryStreamStride, nameof(GIGpuGeometryStreamData));
            validated = true;
        }

        static void ValidateStride<T>(int expected, string typeName) where T : struct
        {
            int actual = Marshal.SizeOf<T>();
            if (actual != expected)
                throw new InvalidOperationException(
                    $"GI Scene ABI mismatch for {typeName}: C#={actual}, expected={expected}.");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GIGpuInstanceData
    {
        public Matrix4x4 localToWorld;
        public Matrix4x4 previousLocalToWorld;
        public Vector4 worldBoundingSphere;
        public uint objectId;
        public uint geometryIndex;
        public uint firstMaterialBinding;
        public uint materialBindingCount;
        public uint flags;
        public uint adapterType;
        public uint revision;
        public uint padding;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GIGpuGeometryData
    {
        public uint geometryId;
        public uint kind;
        public uint sourceObjectId;
        public uint flags;
        public Vector4 localBoundingSphere;
        public uint vertexCount;
        public uint indexCount;
        public uint subMeshCount;
        public uint sourceRevision;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GIGpuMaterialData
    {
        public Vector4 baseColor;
        public Vector4 emissive;
        // x=roughness, y=metallic, z=opacity, w=alpha cutoff
        public Vector4 surface;
        public Vector4 baseMapST;
        public Vector4 emissionMapST;
        public uint materialId;
        public uint flags;
        public uint revision;
        public uint textureIndex;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GIGpuMaterialBindingData
    {
        public uint instanceIndex;
        public uint subMeshIndex;
        public uint materialIndex;
        public uint padding;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GIGpuGeometryStreamData
    {
        public uint vertexOffset;
        public uint indexOffset;
        public uint triangleOffset;
        public uint indexCount;
        public uint triangleCount;
        public uint flags;
        public uint padding0;
        public uint padding1;
    }

    /// <summary>Transient view. Valid until the producing RealtimeGIScene rebuilds or disables.</summary>
    public readonly struct GISceneGpuView
    {
        public readonly GraphicsBuffer instances;
        public readonly GraphicsBuffer geometries;
        public readonly GraphicsBuffer materials;
        public readonly GraphicsBuffer materialBindings;
        public readonly int instanceCount;
        public readonly int geometryCount;
        public readonly int materialCount;
        public readonly int materialBindingCount;
        public readonly uint abiVersion;
        public readonly int sceneRevision;

        internal GISceneGpuView(
            GraphicsBuffer instances,
            GraphicsBuffer geometries,
            GraphicsBuffer materials,
            GraphicsBuffer materialBindings,
            int instanceCount,
            int geometryCount,
            int materialCount,
            int materialBindingCount,
            int sceneRevision)
        {
            this.instances = instances;
            this.geometries = geometries;
            this.materials = materials;
            this.materialBindings = materialBindings;
            this.instanceCount = instanceCount;
            this.geometryCount = geometryCount;
            this.materialCount = materialCount;
            this.materialBindingCount = materialBindingCount;
            abiVersion = GISceneAbi.Version;
            this.sceneRevision = sceneRevision;
        }

        public bool IsValid =>
            instances != null && geometries != null && materials != null && materialBindings != null;
    }
}
