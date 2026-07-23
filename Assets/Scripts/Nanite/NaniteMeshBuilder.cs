using System;
using System.Collections.Generic;
using UnityEngine;

namespace Nanite
{
    /// <summary>
    /// Nanite 离线 Build DAG：打组 → 合并 → 减面 → 再 clusterize，逐 Mip 构建层级。
    /// </summary>
    public static class NaniteMeshBuilder
    {
        public const bool kUseLocks = true;
        // 略放宽：锁边导致达不到 50% 时，只要有一定减面仍晋升为成功层，避免大量卡住→parent=MaxValue→永不退化。
        public const float kSimplifyThreshold = 0.92f;
        public const int kPartitionSize = 16;
        public const float kSimplifyRatio = 0.5f;
        public const float kSimplifyErrorMergePrevious = 1.0f;
        public const float kSimplifyErrorMergeAdditive = 1.0f;
        // parent/self 误差下限（相对 group 半径）。
        // 过小则几乎任何距离 parent 都「不可感知」→近处也被迫用粗层，看起来永远不够精细。
        // 0.02 ≈ 半径 2%：在 lodErrorPixels≈1~2、数米视距下仍能保住叶子细节。
        public const float kMinErrorRelativeToRadius = 0.02f;

        static readonly float[] kNormalWeights = { 1f, 1f, 1f };
        static readonly int kVertexStride = sizeof(float) * 3;

        public static NaniteSubMesh Build(Vector3[] vertices, Vector3[] normals, int[] indices, Func<bool> shouldCancel = null)
        {
            if (shouldCancel != null && shouldCancel())
                throw new OperationCanceledException("Build DAG cancelled.");

            if (vertices == null || indices == null)
                throw new ArgumentNullException();
            if (normals != null && normals.Length != vertices.Length)
                throw new ArgumentException("normals 长度须与 vertices 一致。");
            if (indices.Length % 3 != 0)
                throw new ArgumentException("indices 长度须为 3 的倍数。");

            var result = new NaniteSubMesh();
            var clusters = MeshClusterizer.Clusterize(vertices, indices);
            result.clusterList = clusters;

            for (int i = 0; i < clusters.Count; i++)
            {
                var c = clusters[i];
                c.self = ComputeClusterBounds(vertices, c.indices, 0f);
                c.mip = 0;
                c.parent.error = float.MaxValue;
            }

            var remap = new uint[vertices.Length];
            MeshOptimizerNative.NativeGeneratePositionRemap(
                remap, vertices, (UIntPtr)(uint)vertices.Length, (UIntPtr)(uint)kVertexStride);

            var pending = new List<int>(clusters.Count);
            for (int i = 0; i < clusters.Count; i++)
                pending.Add(i);

            var locks = new byte[vertices.Length];
            int curMip = 1;
            int safetyIteration = 0;
            const int maxIterations = 64;

            while (pending.Count > 1 && safetyIteration++ < maxIterations)
            {
                if (shouldCancel != null && shouldCancel())
                    throw new OperationCanceledException("Build DAG cancelled.");

                var groups = Partition(clusters, pending, remap, vertices);
                if (kUseLocks)
                    LockBoundary(locks, groups, clusters, remap);

                pending.Clear();
                int producedNextLevelClusters = 0;

                for (int g = 0; g < groups.Count; g++)
                {
                    if (shouldCancel != null && shouldCancel())
                        throw new OperationCanceledException("Build DAG cancelled.");

                    var groupClusterIndices = groups[g];
                    if (groupClusterIndices.Count == 0)
                        continue;

                    var merged = new List<int>();
                    for (int j = 0; j < groupClusterIndices.Count; j++)
                        merged.AddRange(clusters[groupClusterIndices[j]].indices);

                    var groupBounds = BoundsMerge(clusters, groupClusterIndices);
                    float minChildError = MinChildError(clusters, groupClusterIndices);

                    int targetSize = (merged.Count / 3 / 2) * 3;
                    float simplifyError = 0f;
                    var simplified = Simplify(
                        vertices,
                        normals ?? vertices,
                        merged,
                        kUseLocks ? locks : null,
                        targetSize,
                        ref simplifyError);

                    float maxChildSelf = MaxChildSelfError(clusters, groupClusterIndices);

                    if (simplified.Count > merged.Count * kSimplifyThreshold)
                    {
                        // 卡住：不再产生更粗层。parent=MaxValue 保留（Bevy/Nanite 根/终端语义），
                        // 但把子树 self 误差记入 group 元数据，避免审计/后续层误用 0。
                        groupBounds.error = float.MaxValue;

                        for (int j = 0; j < groupClusterIndices.Count; j++)
                        {
                            int clusterIndex = groupClusterIndices[j];
                            var t = clusters[clusterIndex];
                            t.parent = groupBounds;
                            clusters[clusterIndex] = t;
                        }

                        var terminalGroup = new ClusterGroup
                        {
                            boundsCenter = groupBounds.center,
                            maxParentLodError = groupBounds.error,
                            radius = groupBounds.radius,
                            mipLevel = curMip - 1,
                            // 向 LOD0 累加可观察误差，供 Audit/调试（终端仍用 MaxValue parent）。
                            minLodError = Mathf.Max(minChildError, maxChildSelf)
                        };
                        terminalGroup.children.AddRange(groupClusterIndices);
                        result.clusterGroupList.Add(terminalGroup);
                        continue;
                    }

                    // Bevy：group_error += max(child.self_lod)，再叠简化误差，保持单调可退化。
                    groupBounds.error = maxChildSelf +
                        Mathf.Max(
                            groupBounds.error * kSimplifyErrorMergePrevious,
                            simplifyError) +
                        simplifyError * kSimplifyErrorMergeAdditive;
                    groupBounds.error = Mathf.Max(
                        groupBounds.error,
                        groupBounds.radius * kMinErrorRelativeToRadius);

                    for (int j = 0; j < groupClusterIndices.Count; j++)
                        clusters[groupClusterIndices[j]].parent = groupBounds;

                    var clusterGroup = new ClusterGroup
                    {
                        boundsCenter = groupBounds.center,
                        maxParentLodError = groupBounds.error,
                        radius = groupBounds.radius,
                        mipLevel = curMip - 1,
                        minLodError = minChildError
                    };
                    clusterGroup.children.AddRange(groupClusterIndices);
                    result.clusterGroupList.Add(clusterGroup);

                    var split = MeshClusterizer.Clusterize(vertices, simplified.ToArray());
                    for (int j = 0; j < split.Count; j++)
                    {
                        var child = split[j];
                        child.self = groupBounds;
                        child.mip = curMip;
                        child.parent.error = float.MaxValue;
                        clusters.Add(child);
                        pending.Add(clusters.Count - 1);
                        producedNextLevelClusters++;
                    }
                }

                // 没有任何新簇产生，说明本层全部终止，避免空转。
                if (producedNextLevelClusters == 0)
                    break;

                curMip++;
            }

            if (pending.Count == 1)
            {
                var leaf = clusters[pending[0]];
                var terminalBounds = leaf.self;
                terminalBounds.error = float.MaxValue;
                result.clusterGroupList.Add(new ClusterGroup
                {
                    boundsCenter = terminalBounds.center,
                    maxParentLodError = terminalBounds.error,
                    radius = terminalBounds.radius,
                    minLodError = leaf.self.error,
                    mipLevel = curMip - 1,
                    children = { pending[0] }
                });

                leaf.parent = terminalBounds;
                clusters[pending[0]] = leaf;
            }

            result.maxMipLevel = curMip - 1;
            return result;
        }

        static LODBounds ComputeClusterBounds(Vector3[] vertices, int[] indices, float error)
        {
            var indicesU = ToUInt(indices);
            var b = MeshOptimizerNative.NativeComputeClusterBounds(
                indicesU,
                (UIntPtr)(uint)indicesU.Length,
                vertices,
                (UIntPtr)(uint)vertices.Length,
                (UIntPtr)(uint)kVertexStride);

            return new LODBounds
            {
                center = new Vector3(b.centerX, b.centerY, b.centerZ),
                radius = b.radius,
                error = error
            };
        }

        public static LODBounds MergeClusterBounds(List<Cluster> clusters, IReadOnlyList<int> groupIndices)
        {
            var group = groupIndices as List<int> ?? new List<int>(groupIndices);
            return BoundsMerge(clusters, group);
        }

        static LODBounds BoundsMerge(List<Cluster> clusters, List<int> group)
        {
            var centers = new Vector3[group.Count];
            var radii = new float[group.Count];
            float maxError = 0f;

            for (int j = 0; j < group.Count; j++)
            {
                var b = clusters[group[j]].self;
                centers[j] = b.center;
                radii[j] = b.radius;
                if (b.error > maxError)
                    maxError = b.error;
            }

            var merged = MeshOptimizerNative.NativeComputeSphereBounds(
                centers,
                (UIntPtr)(uint)centers.Length,
                (UIntPtr)(uint)(sizeof(float) * 3),
                radii,
                (UIntPtr)(uint)sizeof(float));

            return new LODBounds
            {
                center = new Vector3(merged.centerX, merged.centerY, merged.centerZ),
                radius = merged.radius,
                error = maxError
            };
        }

        static float MinChildError(List<Cluster> clusters, List<int> group)
        {
            float min = float.MaxValue;
            for (int j = 0; j < group.Count; j++)
            {
                float e = clusters[group[j]].self.error;
                if (e < min)
                    min = e;
            }

            return min == float.MaxValue ? 0f : min;
        }

        static float MaxChildSelfError(List<Cluster> clusters, List<int> group)
        {
            float max = 0f;
            for (int j = 0; j < group.Count; j++)
            {
                float e = clusters[group[j]].self.error;
                if (e < float.MaxValue * 0.5f && e > max)
                    max = e;
            }

            return max;
        }

        static List<int> Simplify(
            Vector3[] vertices,
            Vector3[] attributes,
            List<int> merged,
            byte[] locks,
            int targetIndexCount,
            ref float error)
        {
            if (targetIndexCount >= merged.Count)
                return merged;

            var indicesU = ToUInt(merged);
            var destination = new uint[indicesU.Length];
            var vertexLock = locks ?? new byte[vertices.Length];

            uint options = MeshoptSimplifyFlags.Sparse
                           | MeshoptSimplifyFlags.ErrorAbsolute
                           | MeshoptSimplifyFlags.Permissive;

            UIntPtr outCount = MeshOptimizerNative.NativeSimplifyWithAttributes(
                destination,
                indicesU,
                (UIntPtr)(uint)indicesU.Length,
                vertices,
                (UIntPtr)(uint)vertices.Length,
                (UIntPtr)(uint)kVertexStride,
                attributes,
                (UIntPtr)(uint)kVertexStride,
                kNormalWeights,
                (UIntPtr)3u,
                vertexLock,
                (UIntPtr)(uint)targetIndexCount,
                float.MaxValue,
                options,
                out error);

            int count = (int)outCount;
            var result = new List<int>(count);
            for (int i = 0; i < count; i++)
                result.Add((int)destination[i]);
            return result;
        }

        static List<List<int>> Partition(
            List<Cluster> clusters,
            List<int> pending,
            uint[] remap,
            Vector3[] vertices)
        {
            if (pending.Count <= kPartitionSize)
                return new List<List<int>> { new List<int>(pending) };

            int totalIndexCount = 0;
            for (int i = 0; i < pending.Count; i++)
                totalIndexCount += clusters[pending[i]].indices.Length;

            var clusterIndices = new uint[totalIndexCount];
            var clusterCounts = new uint[pending.Count];
            int write = 0;

            for (int i = 0; i < pending.Count; i++)
            {
                var cluster = clusters[pending[i]];
                clusterCounts[i] = (uint)cluster.indices.Length;
                for (int j = 0; j < cluster.indices.Length; j++)
                    clusterIndices[write++] = remap[cluster.indices[j]];
            }

            var clusterPart = new uint[pending.Count];
            UIntPtr partitionCount = MeshOptimizerNative.NativePartitionClusters(
                clusterPart,
                clusterIndices,
                (UIntPtr)(uint)clusterIndices.Length,
                clusterCounts,
                (UIntPtr)(uint)clusterCounts.Length,
                vertices,
                (UIntPtr)(uint)vertices.Length,
                (UIntPtr)(uint)kVertexStride,
                (UIntPtr)(uint)kPartitionSize);

            var partitions = new List<List<int>>((int)partitionCount);
            for (int i = 0; i < (int)partitionCount; i++)
                partitions.Add(new List<int>());

            for (int i = 0; i < pending.Count; i++)
                partitions[(int)clusterPart[i]].Add(pending[i]);

            return partitions;
        }

        static void LockBoundary(byte[] locks, List<List<int>> groups, List<Cluster> clusters, uint[] remap)
        {
            var groupMap = new int[locks.Length];
            for (int i = 0; i < groupMap.Length; i++)
                groupMap[i] = -1;

            for (int gi = 0; gi < groups.Count; gi++)
            {
                var group = groups[gi];
                for (int j = 0; j < group.Count; j++)
                {
                    var indices = clusters[group[j]].indices;
                    for (int k = 0; k < indices.Length; k++)
                    {
                        int r = (int)remap[indices[k]];
                        if (groupMap[r] == -1 || groupMap[r] == gi)
                            groupMap[r] = gi;
                        else
                            groupMap[r] = -2;
                    }
                }
            }

            for (int i = 0; i < locks.Length; i++)
            {
                int r = (int)remap[i];
                locks[i] = (byte)(groupMap[r] == -2 ? MeshoptSimplifyVertexFlags.Lock : (byte)0);
            }
        }

        static uint[] ToUInt(IReadOnlyList<int> indices)
        {
            var result = new uint[indices.Count];
            for (int i = 0; i < indices.Count; i++)
                result[i] = (uint)indices[i];
            return result;
        }
    }
}
