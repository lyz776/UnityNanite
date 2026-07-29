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
        public const float kSimplifyThreshold = 0.85f;
        // meshoptimizer/NVIDIA clodDefaultConfig: groups are deliberately regrouped
        // every generation so later groups can cross and eliminate older borders.
        public const int kPartitionSize = 16;
        public const float kSimplifyRatio = 0.5f;
        // Packed attribute order: UV.xy, normal.xyz, tangent.xyzw.
        static readonly float[] kAttributeWeights =
        {
            0.1f, 0.1f,
            0.5f, 0.5f, 0.5f,
            0.1f, 0.1f, 0.1f, 0.5f
        };
        static readonly int kVertexStride = sizeof(float) * 3;
        static readonly int kAttributeStride = sizeof(float) * 9;
        static readonly float[] kNoAttributes = new float[1];
        static readonly float[] kNoAttributeWeights = new float[1];

        public static NaniteSubMesh Build(Vector3[] vertices, Vector3[] normals, int[] indices, Func<bool> shouldCancel = null)
        {
            return Build(vertices, normals, null, null, indices, shouldCancel);
        }

        public static NaniteSubMesh Build(
            Vector3[] vertices,
            Vector3[] normals,
            Vector2[] uvs,
            Vector4[] tangents,
            int[] indices,
            Func<bool> shouldCancel = null)
        {
            if (shouldCancel != null && shouldCancel())
                throw new OperationCanceledException("Build DAG cancelled.");

            if (vertices == null || indices == null)
                throw new ArgumentNullException();
            if (normals != null && normals.Length != 0 && normals.Length != vertices.Length)
                throw new ArgumentException("normals 长度须与 vertices 一致。");
            if (indices.Length % 3 != 0)
                throw new ArgumentException("indices 长度须为 3 的倍数。");

            if (uvs != null && uvs.Length != 0 && uvs.Length != vertices.Length)
                throw new ArgumentException("uvs length must match vertices.");
            if (tangents != null && tangents.Length != 0 && tangents.Length != vertices.Length)
                throw new ArgumentException("tangents length must match vertices.");

            bool hasNormals = normals != null && normals.Length == vertices.Length;
            bool hasUvs = uvs != null && uvs.Length == vertices.Length;
            bool hasTangents = tangents != null && tangents.Length == vertices.Length;
            float[] attributes = BuildSimplificationAttributes(
                vertices.Length,
                hasNormals ? normals : null,
                hasUvs ? uvs : null,
                hasTangents ? tangents : null);

            var result = new NaniteSubMesh();
            var clusters = MeshClusterizer.Clusterize(vertices, indices);
            result.clusterList = clusters;

            for (int i = 0; i < clusters.Count; i++)
            {
                var c = clusters[i];
                c.geometry = ComputeClusterBounds(vertices, c.indices, 0f, out uint packedCone);
                c.longestEdge = ComputeLongestEdge(vertices, c.indices);
                c.packedCone = packedCone;
                c.self = c.geometry;
                c.mip = 0;
                c.parent.error = float.MaxValue;
            }

            var remap = new uint[vertices.Length];
            MeshOptimizerNative.NativeGeneratePositionRemap(
                remap, vertices, (UIntPtr)(uint)vertices.Length, (UIntPtr)(uint)kVertexStride);

            var pending = new List<int>(clusters.Count);
            for (int i = 0; i < clusters.Count; i++)
            {
                pending.Add(i);
            }

            // Match meshoptimizer/Nyx permissive simplification semantics: attribute
            // discontinuities are Protect-ed (not hard Lock-ed), while partition
            // boundaries receive the dynamic Lock bit each hierarchy iteration.
            var locks = BuildAttributeProtectFlags(remap, attributes);
            int protectedVertexCount = CountVertexFlags(
                locks,
                MeshoptSimplifyVertexFlags.Protect);
            int simplifyAttempts = 0;
            int simplifyFailures = 0;
            int curMip = 1;
            int safetyIteration = 0;
            const int maxIterations = 64;

            while (pending.Count > 1 && safetyIteration++ < maxIterations)
            {
                if (shouldCancel != null && shouldCancel())
                    throw new OperationCanceledException("Build DAG cancelled.");

                var groups = Partition(
                    clusters,
                    pending,
                    remap,
                    vertices);
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

                    // A DAG node must always contain at least one triangle. A one-triangle
                    // group previously generated targetIndexCount=0, which meshoptimizer
                    // can legally satisfy with an empty result.
                    int targetSize = Mathf.Max(3, (merged.Count / 3 / 2) * 3);
                    float simplifyError = 0f;
                    float geometricError = 0f;
                    // Simplify the complete hierarchy group in one operation, as in
                    // Nyx/meshoptimizer. The former per-component 50% pass caused tiny
                    // disconnected vehicle trims to collapse independently and become
                    // detached coarse fragments.
                    simplifyAttempts++;
                    List<int> simplified = Simplify(
                        vertices,
                        attributes,
                        merged,
                        locks,
                        targetSize,
                        ref simplifyError,
                        out geometricError);
                    if (simplified.Count < 3 || simplified.Count % 3 != 0)
                    {
                        // Treat an unrepresentable native result as "could not simplify";
                        // the existing terminal-group path will preserve the source.
                        simplified = new List<int>(merged);
                        simplifyError = 0f;
                        geometricError = 0f;
                    }

                    if (float.IsNaN(simplifyError) ||
                        float.IsInfinity(simplifyError) ||
                        simplifyError < 0f ||
                        float.IsNaN(geometricError) ||
                        float.IsInfinity(geometricError) ||
                        geometricError < 0f)
                    {
                        throw new InvalidOperationException(
                            $"meshoptimizer returned invalid absolute errors: " +
                            $"appearance={simplifyError}, geometry={geometricError}.");
                    }

                    float maxChildSelf = MaxChildSelfError(clusters, groupClusterIndices);

                    if (simplified.Count > merged.Count * kSimplifyThreshold)
                    {
                        simplifyFailures++;
                        // This branch has no valid coarse replacement. Keep it in the root
                        // set and encode its whole-component radius; runtime applies the
                        // dedicated 1/8-pixel terminal threshold and never drops its member
                        // Clusters independently. This permits only genuinely sub-pixel
                        // disappearance without turning trim/panels into visible holes.
                        groupBounds.error = Mathf.Max(maxChildSelf, groupBounds.radius);

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
                            isRootSet = true,
                            // 向 LOD0 累加可观察误差，供 Audit/调试。
                            minLodError = Mathf.Max(minChildError, maxChildSelf)
                        };
                        terminalGroup.children.AddRange(groupClusterIndices);
                        result.clusterGroupList.Add(terminalGroup);
                        continue;
                    }

                    // Bevy：group_error += max(child.self_lod)，再叠简化误差，保持单调可退化。
                    // NVIDIA meshopt_clusterlod default propagation keeps the maximum of
                    // inherited and current absolute geometric error. Attribute error
                    // chooses topology but is never projected as metres.
                    groupBounds.error = Mathf.Max(
                        geometricError,
                        Mathf.Max(groupBounds.error, maxChildSelf));

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
                        child.geometry = ComputeClusterBounds(
                            vertices,
                            child.indices,
                            0f,
                            out uint packedCone);
                        child.longestEdge = ComputeLongestEdge(vertices, child.indices);
                        child.packedCone = packedCone;
                        child.self = groupBounds;
                        child.mip = curMip;
                        child.parent.error = float.MaxValue;
                        clusters.Add(child);
                        int parentClusterIndex = clusters.Count - 1;
                        pending.Add(parentClusterIndex);
                        clusterGroup.parents.Add(parentClusterIndex);
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
                    isRootSet = true,
                    children = { pending[0] }
                });

                leaf.parent = terminalBounds;
                clusters[pending[0]] = leaf;
            }

            result.maxMipLevel = curMip - 1;
            int currentBoundaryLocks = CountVertexFlags(
                locks,
                MeshoptSimplifyVertexFlags.Lock);
            long residentRootTriangles = CountRootSetTriangles(result);
            Debug.Log(
                $"[Nanite][BuildDAG] clusters={result.clusterList.Count}, maxMip={result.maxMipLevel}, " +
                $"simplify={simplifyAttempts - simplifyFailures}/{simplifyAttempts}, " +
                $"attributeProtect={protectedVertexCount}/{vertices.Length}, " +
                $"currentBoundaryLock={currentBoundaryLocks}/{vertices.Length}, " +
                $"residentRootTriangles={residentRootTriangles}.");
            return result;
        }

        static int CountVertexFlags(byte[] flags, byte mask)
        {
            int count = 0;
            for (int i = 0; i < flags.Length; i++)
            {
                if ((flags[i] & mask) != 0)
                    count++;
            }
            return count;
        }

        static long CountRootSetTriangles(NaniteSubMesh subMesh)
        {
            long triangles = 0;
            for (int groupIndex = 0; groupIndex < subMesh.clusterGroupList.Count; groupIndex++)
            {
                ClusterGroup group = subMesh.clusterGroupList[groupIndex];
                if (group == null || !group.isRootSet || group.children == null)
                    continue;
                for (int childIndex = 0; childIndex < group.children.Count; childIndex++)
                {
                    int clusterIndex = group.children[childIndex];
                    if ((uint)clusterIndex >= (uint)subMesh.clusterList.Count)
                        continue;
                    int[] clusterIndices = subMesh.clusterList[clusterIndex].indices;
                    triangles += clusterIndices != null ? clusterIndices.Length / 3 : 0;
                }
            }
            return triangles;
        }

        static LODBounds ComputeClusterBounds(
            Vector3[] vertices,
            int[] indices,
            float error,
            out uint packedCone)
        {
            var indicesU = ToUInt(indices);
            var b = MeshOptimizerNative.NativeComputeClusterBounds(
                indicesU,
                (UIntPtr)(uint)indicesU.Length,
                vertices,
                (UIntPtr)(uint)vertices.Length,
                (UIntPtr)(uint)kVertexStride);

            packedCone = PackNormalCone(b);
            return new LODBounds
            {
                center = new Vector3(b.centerX, b.centerY, b.centerZ),
                radius = b.radius,
                error = error
            };
        }

        static uint PackNormalCone(MeshoptBounds bounds)
        {
            unchecked
            {
                return (uint)(byte)bounds.coneAxisS8X |
                       ((uint)(byte)bounds.coneAxisS8Y << 8) |
                       ((uint)(byte)bounds.coneAxisS8Z << 16) |
                       ((uint)(byte)bounds.coneCutoffS8 << 24);
            }
        }

        static float ComputeLongestEdge(Vector3[] vertices, int[] indices)
        {
            if (vertices == null || indices == null)
                return 0f;
            float maxEdgeSq = 0f;
            int triangleIndexCount = indices.Length - indices.Length % 3;
            for (int i = 0; i < triangleIndexCount; i += 3)
            {
                int i0 = indices[i];
                int i1 = indices[i + 1];
                int i2 = indices[i + 2];
                if ((uint)i0 >= (uint)vertices.Length ||
                    (uint)i1 >= (uint)vertices.Length ||
                    (uint)i2 >= (uint)vertices.Length)
                    continue;
                Vector3 p0 = vertices[i0];
                Vector3 p1 = vertices[i1];
                Vector3 p2 = vertices[i2];
                maxEdgeSq = Mathf.Max(
                    maxEdgeSq,
                    Mathf.Max(
                        (p1 - p0).sqrMagnitude,
                        Mathf.Max((p2 - p1).sqrMagnitude, (p0 - p2).sqrMagnitude)));
            }
            return Mathf.Sqrt(maxEdgeSq);
        }

        public static LODBounds MergeClusterBounds(List<Cluster> clusters, IReadOnlyList<int> groupIndices)
        {
            var group = groupIndices as List<int> ?? new List<int>(groupIndices);
            return BoundsMerge(clusters, group);
        }

        public static LODBounds MergeClusterGeometryBounds(
            List<Cluster> clusters,
            IReadOnlyList<int> groupIndices)
        {
            var group = groupIndices as List<int> ?? new List<int>(groupIndices);
            var centers = new Vector3[group.Count];
            var radii = new float[group.Count];
            for (int i = 0; i < group.Count; i++)
            {
                LODBounds bounds = clusters[group[i]].geometry;
                centers[i] = bounds.center;
                radii[i] = bounds.radius;
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
                error = 0f
            };
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
            float[] attributes,
            List<int> merged,
            byte[] locks,
            int targetIndexCount,
            ref float error,
            out float geometricError)
        {
            geometricError = 0f;
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
                (UIntPtr)(uint)kAttributeStride,
                kAttributeWeights,
                (UIntPtr)(uint)kAttributeWeights.Length,
                vertexLock,
                (UIntPtr)(uint)targetIndexCount,
                float.MaxValue,
                options,
                out error);

            // Run the same meshoptimizer QEM with the same topology locks and target but
            // without dimensionless attributes. Its result geometry is discarded; only
            // the absolute positional error is used by the runtime screen-space metric.
            var geometricDestination = new uint[indicesU.Length];
            MeshOptimizerNative.NativeSimplifyWithAttributes(
                geometricDestination,
                indicesU,
                (UIntPtr)(uint)indicesU.Length,
                vertices,
                (UIntPtr)(uint)vertices.Length,
                (UIntPtr)(uint)kVertexStride,
                kNoAttributes,
                UIntPtr.Zero,
                kNoAttributeWeights,
                UIntPtr.Zero,
                vertexLock,
                (UIntPtr)(uint)targetIndexCount,
                float.MaxValue,
                options,
                out geometricError);

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

            int groupCount = Mathf.Max(1, (int)partitionCount);
            var result = new List<List<int>>(groupCount);
            for (int groupIndex = 0; groupIndex < groupCount; groupIndex++)
                result.Add(new List<int>(kPartitionSize));
            for (int pendingIndex = 0; pendingIndex < pending.Count; pendingIndex++)
            {
                int groupIndex = Mathf.Clamp((int)clusterPart[pendingIndex], 0, groupCount - 1);
                result[groupIndex].Add(pending[pendingIndex]);
            }
            return result;
        }

        static void LockBoundary(
            byte[] locks,
            List<List<int>> groups,
            List<Cluster> clusters,
            uint[] remap)
        {
            // meshoptimizer clusterlod rebuilds boundary locks for every generation.
            // Protect survives; an old partition Lock must be released so the next
            // regrouping can cross and eliminate that border.
            for (int i = 0; i < locks.Length; i++)
                locks[i] &= unchecked((byte)~MeshoptSimplifyVertexFlags.Lock);

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
                if (groupMap[r] == -2)
                    locks[i] |= MeshoptSimplifyVertexFlags.Lock;
            }
        }

        static byte[] BuildAttributeProtectFlags(uint[] positionRemap, float[] attributes)
        {
            int vertexCount = positionRemap.Length;
            var result = new byte[vertexCount];
            int attributeCount = kAttributeWeights.Length;

            // NativeGeneratePositionRemap maps every position-equivalent wedge to one
            // representative. Nyx compares the packed attributes against that
            // representative and Protects both endpoints when they differ.
            for (int vertexIndex = 0; vertexIndex < vertexCount; vertexIndex++)
            {
                int representative = (int)positionRemap[vertexIndex];
                if (representative == vertexIndex ||
                    (uint)representative >= (uint)vertexCount)
                {
                    continue;
                }

                int attributeBase = vertexIndex * attributeCount;
                int representativeBase = representative * attributeCount;
                if (!PackedAttributesDiffer(
                        attributes,
                        attributeBase,
                        representativeBase))
                    continue;

                result[vertexIndex] |= MeshoptSimplifyVertexFlags.Protect;
                result[representative] |= MeshoptSimplifyVertexFlags.Protect;
            }

            return result;
        }

        static bool PackedAttributesDiffer(float[] attributes, int a, int b)
        {
            // Nyx compares its actual packed vertex payload: UV R16G16_FLOAT and
            // normal/tangent R10G10B10A2_UNORM. Compare the same representation here
            // so sub-quantization float noise does not turn into a permanent seam.
            if (FloatToHalfBits(attributes[a + 0]) != FloatToHalfBits(attributes[b + 0]) ||
                FloatToHalfBits(attributes[a + 1]) != FloatToHalfBits(attributes[b + 1]))
            {
                return true;
            }

            if (PackSignedUnitVector10(attributes, a + 2) !=
                PackSignedUnitVector10(attributes, b + 2))
            {
                return true;
            }

            uint tangentA = PackSignedUnitVector10(attributes, a + 5) |
                            (PackTangentHandedness(attributes[a + 8]) << 30);
            uint tangentB = PackSignedUnitVector10(attributes, b + 5) |
                            (PackTangentHandedness(attributes[b + 8]) << 30);
            return tangentA != tangentB;
        }

        static uint PackSignedUnitVector10(float[] attributes, int offset)
        {
            uint x = PackSignedUnit10(attributes[offset + 0]);
            uint y = PackSignedUnit10(attributes[offset + 1]);
            uint z = PackSignedUnit10(attributes[offset + 2]);
            return x | (y << 10) | (z << 20);
        }

        static uint PackSignedUnit10(float value) =>
            (uint)Mathf.RoundToInt(Mathf.Clamp01(value * 0.5f + 0.5f) * 1023f);

        static uint PackTangentHandedness(float value) => value < 0f ? 0u : 3u;

        static ushort FloatToHalfBits(float value)
        {
            uint bits = unchecked((uint)BitConverter.SingleToInt32Bits(value));
            uint sign = (bits >> 16) & 0x8000u;
            uint mantissa = bits & 0x007FFFFFu;
            int exponent = (int)((bits >> 23) & 0xFFu);

            if (exponent == 255)
                return (ushort)(sign | (mantissa == 0 ? 0x7C00u : 0x7E00u));

            int halfExponent = exponent - 127 + 15;
            if (halfExponent >= 31)
                return (ushort)(sign | 0x7C00u);
            if (halfExponent <= 0)
            {
                if (halfExponent < -10)
                    return (ushort)sign;
                mantissa = (mantissa | 0x00800000u) >> (1 - halfExponent);
                if ((mantissa & 0x00001000u) != 0)
                    mantissa += 0x00002000u;
                return (ushort)(sign | (mantissa >> 13));
            }

            if ((mantissa & 0x00001000u) != 0)
            {
                mantissa += 0x00002000u;
                if ((mantissa & 0x00800000u) != 0)
                {
                    mantissa = 0;
                    halfExponent++;
                    if (halfExponent >= 31)
                        return (ushort)(sign | 0x7C00u);
                }
            }

            return (ushort)(sign | ((uint)halfExponent << 10) | (mantissa >> 13));
        }

        static float[] BuildSimplificationAttributes(
            int vertexCount,
            Vector3[] normals,
            Vector2[] uvs,
            Vector4[] tangents)
        {
            var result = new float[vertexCount * kAttributeWeights.Length];
            for (int vertexIndex = 0; vertexIndex < vertexCount; vertexIndex++)
            {
                int attributeIndex = vertexIndex * kAttributeWeights.Length;
                Vector2 uv = uvs != null ? uvs[vertexIndex] : Vector2.zero;
                Vector3 normal = normals != null ? normals[vertexIndex] : Vector3.zero;
                Vector4 tangent = tangents != null ? tangents[vertexIndex] : Vector4.zero;
                result[attributeIndex + 0] = uv.x;
                result[attributeIndex + 1] = uv.y;
                result[attributeIndex + 2] = normal.x;
                result[attributeIndex + 3] = normal.y;
                result[attributeIndex + 4] = normal.z;
                result[attributeIndex + 5] = tangent.x;
                result[attributeIndex + 6] = tangent.y;
                result[attributeIndex + 7] = tangent.z;
                result[attributeIndex + 8] = tangent.w;
            }
            return result;
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
