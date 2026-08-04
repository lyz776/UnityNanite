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

    public enum GIMaterialFamily : uint
    {
        Auto = 0,
        UrpLit = 1,
        ExplicitStylizedProxy = 2
    }

    public enum GIMaterialMaskChannel : uint
    {
        Red = 0,
        Green = 1,
        Blue = 2,
        Alpha = 3,
        Constant = 4
    }

    public enum GIAlphaSource : uint
    {
        BaseMapAlpha = 0,
        MaskRed = 1,
        MaskGreen = 2,
        MaskBlue = 3,
        MaskAlpha = 4,
        ConstantOpacity = 5
    }

    [Flags]
    public enum GIMaterialFlags : uint
    {
        None = 0,
        DoubleSided = 1u << 0,
        Emissive = 1u << 1,
        AlphaTested = 1u << 2,
        HasBaseMap = 1u << 3,
        HasEmissionMap = 1u << 4,
        HasNormalMap = 1u << 5,
        HasMaskMap = 1u << 6,
        ExplicitProxy = 1u << 7
    }

    /// <summary>
    /// Version shared by C# producers and future compute consumers. Increment whenever a GPU
    /// structure changes; never silently reinterpret an old stride.
    /// </summary>
    public static class GISceneAbi
    {
        public const uint Version = 4;
        public const int InstanceStride = 176;
        public const int GeometryStride = 48;
        public const int MaterialStride = 176;
        public const int MaterialBindingStride = 16;
        public const int GeometryStreamStride = 32;
        public const int EmissiveAliasStride = 32;
        public const int BvhNodeStride = 32;
        public const int BvhPrimitiveStride = 16;
        public const int BvhRangeStride = 16;
        public const uint GeometryStreamNaniteProxy = 1u << 0;
        public const uint GeometryStreamNaniteResidentPages = 1u << 1;

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
            ValidateStride<GIGpuBvhNode>(BvhNodeStride, nameof(GIGpuBvhNode));
            ValidateStride<GIGpuBvhPrimitive>(BvhPrimitiveStride, nameof(GIGpuBvhPrimitive));
            ValidateStride<GIGpuBvhRange>(BvhRangeStride, nameof(GIGpuBvhRange));
            ValidateStride<GIGpuEmissiveAliasData>(EmissiveAliasStride, nameof(GIGpuEmissiveAliasData));
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
        public uint transformSignature;
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
        public Vector4 normalMapST;
        public Vector4 maskMapST;
        // xy=metallic scale/bias, zw=roughness scale/bias.
        public Vector4 maskRemap0;
        // xy=opacity scale/bias, z=normal scale, w=reserved.
        public Vector4 maskRemap1;
        public uint materialId;
        public uint flags;
        public uint revision;
        public uint geometryRevision;
        // 0..2 metallic channel, 3..5 roughness, 6..8 opacity,
        // 9..12 alpha source, 16..23 material family.
        public uint channels;
        public uint padding0;
        public uint padding1;
        public uint padding2;
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

    [StructLayout(LayoutKind.Sequential)]
    public struct GIGpuBvhNode
    {
        public Vector3 boundsMin;
        // Internal: left child (right is left+1). Leaf: first primitive reference.
        public uint leftFirst;
        public Vector3 boundsMax;
        // Zero denotes an internal node.
        public uint primitiveCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GIGpuBvhPrimitive
    {
        // Triangle index for ordinary Mesh, global Page ID for Nanite.
        public uint primitiveKey;
        public uint firstTriangle;
        public uint triangleCount;
        // bit 0: Nanite resident Page leaf; bit 1: TLAS instance leaf.
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GIGpuBvhRange
    {
        public uint rootNode;
        public uint nodeCount;
        public uint primitiveOffset;
        public uint primitiveCount;
    }

    /// <summary>
    /// One instance-level emissive proposal. probability is the normalized source PMF;
    /// aliasProbability/aliasIndex form a Walker alias table for O(1) GPU selection.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct GIGpuEmissiveAliasData
    {
        public Vector4 worldBoundingSphere;
        public float probability;
        public float aliasProbability;
        public uint aliasIndex;
        public uint objectId;
    }

    /// <summary>Transient view. Valid until the producing RealtimeGIScene rebuilds or disables.</summary>
    public readonly struct GISceneGpuView
    {
        public readonly GraphicsBuffer instances;
        public readonly GraphicsBuffer geometries;
        public readonly GraphicsBuffer materials;
        public readonly GraphicsBuffer materialBindings;
        public readonly GraphicsBuffer emissiveAliases;
        public readonly int instanceCount;
        public readonly int geometryCount;
        public readonly int materialCount;
        public readonly int materialBindingCount;
        public readonly int emissiveAliasCount;
        public readonly uint abiVersion;
        public readonly int sceneRevision;

        internal GISceneGpuView(
            GraphicsBuffer instances,
            GraphicsBuffer geometries,
            GraphicsBuffer materials,
            GraphicsBuffer materialBindings,
            GraphicsBuffer emissiveAliases,
            int instanceCount,
            int geometryCount,
            int materialCount,
            int materialBindingCount,
            int emissiveAliasCount,
            int sceneRevision)
        {
            this.instances = instances;
            this.geometries = geometries;
            this.materials = materials;
            this.materialBindings = materialBindings;
            this.emissiveAliases = emissiveAliases;
            this.instanceCount = instanceCount;
            this.geometryCount = geometryCount;
            this.materialCount = materialCount;
            this.materialBindingCount = materialBindingCount;
            this.emissiveAliasCount = emissiveAliasCount;
            abiVersion = GISceneAbi.Version;
            this.sceneRevision = sceneRevision;
        }

        public bool IsValid =>
            instances != null && geometries != null && materials != null &&
            materialBindings != null && emissiveAliases != null;
    }
}
