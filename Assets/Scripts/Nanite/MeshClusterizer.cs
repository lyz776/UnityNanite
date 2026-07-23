using System;
using System.Collections.Generic;
using UnityEngine;

namespace Nanite
{
    /// <summary>
    /// 离线阶段：用 meshoptimizer 将网格切成 Cluster（Nanite 管线第一步）。
    /// 依赖自建 native 插件 <see cref="MeshOptimizerNative.DllName"/>。
    /// </summary>
    public static class MeshClusterizer
    {
        public const int kClusterSize = 128;

        /// <summary>
        /// 生成簇列表：<paramref name="indices"/> 为三角形列表（长度须为 3 的倍数），索引指向 <paramref name="vertices"/>。
        /// </summary>
        public static unsafe List<Cluster> Clusterize(ReadOnlySpan<Vector3> vertices, ReadOnlySpan<int> indices)
        {
            const int maxVertices = 192;
            const int maxTriangles = kClusterSize;
            int minTriangles = (kClusterSize / 3) & ~3;
            const float splitFactor = 2.0f;
            const float coneWeight = 0f;

            if (indices.Length % 3 != 0)
                throw new ArgumentException("indices.Length 必须是 3 的倍数。", nameof(indices));

            var vertexArray = vertices.Length == 0 ? Array.Empty<Vector3>() : vertices.ToArray();
            var indexArray = indices.Length == 0 ? Array.Empty<int>() : indices.ToArray();

            var indexU = new uint[indexArray.Length];
            for (int i = 0; i < indexArray.Length; i++)
            {
                if (indexArray[i] < 0 || indexArray[i] >= vertices.Length)
                    throw new ArgumentOutOfRangeException(nameof(indices), $"索引越界：{indexArray[i]}");
                indexU[i] = (uint)indexArray[i];
            }

            // Flex/Spatial：Bound 必须用 min_triangles，见 meshoptimizer.h
            UIntPtr meshletBound = MeshOptimizerNative.NativeBuildMeshletsBound(
                (UIntPtr)(uint)indexU.Length,
                (UIntPtr)(uint)maxVertices,
                (UIntPtr)(uint)minTriangles);
            int maxMeshlets = (int)meshletBound;

            var meshlets = new Meshlet[maxMeshlets];
            var meshletVertices = new uint[indexU.Length];
            var meshletTriangles = new byte[indexU.Length];

            UIntPtr meshletCountPtr = MeshOptimizerNative.NativeBuildMeshletsFlex(
                meshlets,
                meshletVertices,
                meshletTriangles,
                indexU,
                (UIntPtr)(uint)indexU.Length,
                vertexArray,
                (UIntPtr)(uint)vertexArray.Length,
                (UIntPtr)(uint)(sizeof(float) * 3),
                (UIntPtr)(uint)maxVertices,
                (UIntPtr)(uint)minTriangles,
                (UIntPtr)(uint)maxTriangles,
                coneWeight,
                splitFactor);

            int meshletCount = (int)meshletCountPtr;
            var clusters = new List<Cluster>(meshletCount);

            for (int i = 0; i < meshletCount; i++)
            {
                ref readonly Meshlet m = ref meshlets[i];

                fixed (uint* mv = meshletVertices)
                fixed (byte* mt = meshletTriangles)
                {
                    uint* vBase = mv + m.vertex_offset;
                    byte* tBase = mt + m.triangle_offset;
                    MeshOptimizerNative.NativeOptimizeMeshlet(
                        (IntPtr)vBase,
                        (IntPtr)tBase,
                        (UIntPtr)m.triangle_count,
                        (UIntPtr)m.vertex_count);
                }

                var cluster = new Cluster
                {
                    mip = 0,
                    indices = new int[m.triangle_count * 3]
                };
                cluster.parent.error = float.MaxValue;

                for (int j = 0; j < m.triangle_count * 3; j++)
                    cluster.indices[j] = (int)meshletVertices[m.vertex_offset +
                                                              meshletTriangles[m.triangle_offset + j]];

                clusters.Add(cluster);
            }

            return clusters;
        }

        /// <summary>非 Span 的重载。</summary>
        public static unsafe List<Cluster> Clusterize(Vector3[] vertices, int[] indices) =>
            Clusterize(vertices.AsSpan(), indices.AsSpan());
    }
}
