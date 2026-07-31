using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Nanite
{
    /// <summary>
    /// 与 <c>meshopt_Bounds</c> 布局一致（48 字节，Pack=4），供 <c>meshopt_computeSphereBounds</c> 等返回。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct MeshoptBounds
    {
        public float centerX, centerY, centerZ;
        public float radius;
        public float coneApexX, coneApexY, coneApexZ;
        public float coneAxisX, coneAxisY, coneAxisZ;
        public float coneCutoff;
        public sbyte coneAxisS8X, coneAxisS8Y, coneAxisS8Z;
        public sbyte coneCutoffS8;
    }

    /// <summary>
    /// 对自建 C++ 封装 DLL 的 P/Invoke。将编译好的 DLL 放到 <c>Assets/Plugins/x86_64</c>（或对应平台）。
    /// 导出函数名需与下列 <see cref="DllImportAttribute.EntryPoint"/> 一致（或由 C++ <c>extern &quot;C&quot;</c> 包装）。
    /// </summary>
    public static class MeshOptimizerNative
    {
        public const string DllName = "API_CPP";

        /// <summary>
        /// 对应 <c>meshopt_buildMeshletsBound</c>。<br/>
        /// 若使用 <c>meshopt_buildMeshletsFlex</c> / <c>meshopt_buildMeshletsSpatial</c>，此处第三个参数必须为 <b>min_triangles</b>（不是 max），见 meshoptimizer.h 注释。
        /// </summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "BuildMeshletsBound")]
        public static extern UIntPtr NativeBuildMeshletsBound(UIntPtr indexCount, UIntPtr maxVertices, UIntPtr minOrMaxTriangles);

        /// <summary>对应 <c>meshopt_buildMeshletsFlex</c>。</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "BuildMeshletFlex")]
        public static extern UIntPtr NativeBuildMeshletsFlex(
            [Out] Meshlet[] meshlets,
            [Out] uint[] meshletVertices,
            byte[] meshletTriangles,
            uint[] indices,
            UIntPtr indexCount,
            Vector3[] vertexPositions,
            UIntPtr vertexCount,
            UIntPtr vertexPositionsStride,
            UIntPtr maxVertices,
            UIntPtr minTriangles,
            UIntPtr maxTriangles,
            float coneWeight,
            float splitFactor);

        /// <summary>
        /// 对应 <c>meshopt_optimizeMeshlet</c>，对单个 meshlet 的局部顶点表与 8-bit 三角索引重排以改善局部性。
        /// </summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "OptimizeMeshlet")]
        public static extern void NativeOptimizeMeshlet(
            IntPtr meshletVertices,
            IntPtr meshletTriangles,
            UIntPtr triangleCount,
            UIntPtr vertexCount);

        /// <summary>对应 <c>meshopt_partitionClusters</c>（第二步 DAG/Partition 用）。</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "PartitionClusters")]
        public static extern UIntPtr NativePartitionClusters(
            [Out] uint[] destination,
            uint[] clusterIndices,
            UIntPtr totalIndexCount,
            uint[] clusterIndexCounts,
            UIntPtr clusterCount,
            Vector3[] vertexPositions,
            UIntPtr vertexCount,
            UIntPtr vertexPositionsStride,
            UIntPtr targetPartitionSize);

        /// <summary>对应 <c>meshopt_computeSphereBounds</c>（合并子包围球等；仅前 4 个 float 有意义时其余字段为 0）。</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "ComputeSphereBounds")]
        public static extern MeshoptBounds NativeComputeSphereBounds(
            [In] Vector3[] positions,
            UIntPtr count,
            UIntPtr positionsStride,
            [In] float[] radii,
            UIntPtr radiiStride);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "ComputeClusterBounds")]
        public static extern MeshoptBounds NativeComputeClusterBounds(
            [In] uint[] indices,
            UIntPtr indexCount,
            [In] Vector3[] vertexPositions,
            UIntPtr vertexCount,
            UIntPtr vertexPositionsStride);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "GeneratePositionRemap")]
        public static extern void NativeGeneratePositionRemap(
            [Out] uint[] destination,
            [In] Vector3[] vertexPositions,
            UIntPtr vertexCount,
            UIntPtr vertexPositionsStride);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "SimplifyWithAttributes")]
        public static extern UIntPtr NativeSimplifyWithAttributes(
            [Out] uint[] destination,
            [In] uint[] indices,
            UIntPtr indexCount,
            [In] Vector3[] vertexPositions,
            UIntPtr vertexCount,
            UIntPtr vertexPositionsStride,
            [In] float[] vertexAttributes,
            UIntPtr vertexAttributesStride,
            [In] float[] attributeWeights,
            UIntPtr attributeCount,
            [In] byte[] vertexLock,
            UIntPtr targetIndexCount,
            float targetError,
            uint options,
            out float resultError);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "SimplifyWithUpdate")]
        public static extern UIntPtr NativeSimplifyWithUpdate(
            [In, Out] uint[] indices,
            UIntPtr indexCount,
            [In, Out] Vector3[] vertexPositions,
            UIntPtr vertexCount,
            UIntPtr vertexPositionsStride,
            [In, Out] float[] vertexAttributes,
            UIntPtr vertexAttributesStride,
            [In] float[] attributeWeights,
            UIntPtr attributeCount,
            [In] byte[] vertexLock,
            UIntPtr targetIndexCount,
            float targetError,
            uint options,
            out float resultError);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "SimplifySloppy")]
        public static extern UIntPtr NativeSimplifySloppy(
            [Out] uint[] destination,
            [In] uint[] indices,
            UIntPtr indexCount,
            [In] Vector3[] vertexPositions,
            UIntPtr vertexCount,
            UIntPtr vertexPositionsStride,
            [In] byte[] vertexLock,
            UIntPtr targetIndexCount,
            float targetError,
            out float resultError);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "SimplifyScale")]
        public static extern float NativeSimplifyScale(
            [In] Vector3[] vertexPositions,
            UIntPtr vertexCount,
            UIntPtr vertexPositionsStride);
    }

    internal static class MeshoptSimplifyFlags
    {
        public const uint Sparse = 1u << 1;
        public const uint ErrorAbsolute = 1u << 2;
        public const uint Permissive = 1u << 5;
    }

    internal static class MeshoptSimplifyVertexFlags
    {
        public const byte Lock = 1 << 0;
        // meshoptimizer 0.22+: preserve attribute wedges without turning them
        // into topological boundary locks. Requires SimplifyPermissive.
        public const byte Protect = 1 << 1;
    }
}
