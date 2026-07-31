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
        static bool sSloppyFallbackUnavailable;
        static bool sVertexUpdateUnavailable;

        readonly struct FarFieldUvVertexKey : IEquatable<FarFieldUvVertexKey>
        {
            readonly Vector3 position;
            readonly Vector2 uv;

            public FarFieldUvVertexKey(Vector3 position, Vector2 uv)
            {
                this.position = position;
                this.uv = uv;
            }

            public bool Equals(FarFieldUvVertexKey other) =>
                position.Equals(other.position) && uv.Equals(other.uv);

            public override bool Equals(object obj) =>
                obj is FarFieldUvVertexKey other && Equals(other);

            public override int GetHashCode() =>
                (position.GetHashCode() * 397) ^ uv.GetHashCode();
        }

        readonly struct FarFieldUvEdgeKey : IEquatable<FarFieldUvEdgeKey>
        {
            readonly FarFieldUvVertexKey a;
            readonly FarFieldUvVertexKey b;

            public FarFieldUvEdgeKey(FarFieldUvVertexKey a, FarFieldUvVertexKey b)
            {
                this.a = a;
                this.b = b;
            }

            public bool Equals(FarFieldUvEdgeKey other) =>
                (a.Equals(other.a) && b.Equals(other.b)) ||
                (a.Equals(other.b) && b.Equals(other.a));

            public override bool Equals(object obj) =>
                obj is FarFieldUvEdgeKey other && Equals(other);

            public override int GetHashCode() => a.GetHashCode() ^ b.GetHashCode();
        }
        public const bool kUseLocks = true;
        // 略放宽：锁边导致达不到 50% 时，只要有一定减面仍晋升为成功层，避免大量卡住→parent=MaxValue→永不退化。
        public const float kSimplifyThreshold = 0.85f;
        // meshoptimizer/NVIDIA clodDefaultConfig: groups are deliberately regrouped
        // every generation so later groups can cross and eliminate older borders.
        public const int kPartitionSize = 16;
        public const float kSimplifyRatio = 0.5f;
        // meshoptimizer clusterlod's edge-error limiter removes triangles that
        // are already sub-pixel even when normals carry a large attribute QEM.
        // A value of one bounds the appearance error by the representative input
        // edge length without changing the simplified topology.
        // meshoptimizer's production default keeps this disabled. Capping the
        // combined position/normal/UV error by one source edge made a visibly
        // damaged textured replacement look "sub-pixel" close to the camera.
        // Vertex updates are only attempted after the ordinary and sloppy paths
        // would make a group a permanent root. Screen-density refinement now keeps
        // these generated replacements out of close views, so the terminal repair
        // is safe and useful at every hierarchy generation, including mip 0->1.
        // Moving vertices in the first replacement layer can put visibly warped
        // geometry into a cut while the object still covers most of the screen.
        // Reserve this terminal-root repair for genuinely coarse generations.
        public const int kVertexUpdateStartMip = 3;
        // A topology-preserving producer can become irreducible when a detailed
        // asset contains thousands of disconnected trim/fastener/UV islands.  It
        // must not become a 30k-triangle permanent root: publish a separate sloppy
        // far-field replacement and give it a conservative screen-space transition.
        // The far chain is admitted early enough for projected surface density to
        // become the primary triangle budget. That gate prevents a proxy with fewer
        // triangles than covered pixels, including for close/full-screen objects.
        public const float kFarFieldTransitionRadiusPixels = 128f;
        // Packed attribute order: UV.xy, normal.xyz, tangent.xyzw. Match the
        // official meshoptimizer Nanite example: only normals contribute to QEM
        // error; UV discontinuities are Protect-ed below. Tiled UV and tangent
        // values are not object-space metres and must not inflate projected LOD
        // error (the Toyota asset otherwise reached error 33 at radius 2.82).
        static readonly float[] kAttributeWeights =
        {
            0f, 0f,
            0.5f, 0.5f, 0.5f,
            0f, 0f, 0f, 0f
        };
        // Updated coarse vertices need UVs in the optimization metric. Keeping
        // UV weight zero is valid for index-only simplification, but lets an
        // in-place vertex update visibly slide an atlas over the surface.
        static readonly int kVertexStride = sizeof(float) * 3;
        static readonly int kAttributeStride = sizeof(float) * 9;

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
            var geometry = new NaniteBuildGeometry(vertices, normals, uvs, tangents);
            return Build(geometry, indices, shouldCancel);
        }

        public static NaniteSubMesh Build(
            NaniteBuildGeometry geometry,
            int[] indices,
            Func<bool> shouldCancel = null)
        {
            if (shouldCancel != null && shouldCancel())
                throw new OperationCanceledException("Build DAG cancelled.");

            if (geometry == null || indices == null)
                throw new ArgumentNullException();
            if (indices.Length % 3 != 0)
                throw new ArgumentException("indices 长度须为 3 的倍数。");

            Vector3[] vertices = geometry.positions.ToArray();
            Vector3[] normals = geometry.normals.ToArray();
            Vector2[] uvs = geometry.uvs.ToArray();
            Vector4[] tangents = geometry.tangents.ToArray();
            if (normals.Length != vertices.Length || uvs.Length != vertices.Length || tangents.Length != vertices.Length)
                throw new ArgumentException("Build geometry attribute arrays must match positions.");
            for (int index = 0; index < indices.Length; index++)
            {
                if ((uint)indices[index] >= (uint)vertices.Length)
                    throw new ArgumentOutOfRangeException(nameof(indices), "Index is outside the build geometry arena.");
            }

            float[] attributes = BuildSimplificationAttributes(
                vertices.Length,
                normals,
                uvs,
                tangents);

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
            uint[] quantizedPositionRemap = BuildQuantizedPositionRemap(vertices);

            var pending = new List<int>(clusters.Count);
            for (int i = 0; i < clusters.Count; i++)
            {
                pending.Add(i);
            }

            // Match meshoptimizer clusterlod permissive semantics: selected UV seams
            // are Protect-ed (not hard Lock-ed), while partition boundaries receive
            // the dynamic Lock bit each hierarchy iteration.
            var locks = BuildUvSeamProtectFlags(remap, attributes);
            int protectedVertexCount = CountVertexFlags(
                locks,
                MeshoptSimplifyVertexFlags.Protect);
            int simplifyAttempts = 0;
            int simplifyFailures = 0;
            int structuralRetries = 0;
            int structuralFailures = 0;
            int sloppyFallbackAttempts = 0;
            int sloppyFallbackSuccesses = 0;
            int sloppyFallbackRejects = 0;
            int vertexUpdateAttempts = 0;
            int vertexUpdateSuccesses = 0;
            int vertexUpdateRetries = 0;
            int vertexUpdateFailures = 0;
            int vertexUpdatePostRejects = 0;
            int farFieldProxyAttempts = 0;
            int farFieldProxySuccesses = 0;
            int farFieldUvReprojectedTriangles = 0;
            int farFieldUvCollapseFallbacks = 0;
            var terminalDiagnostics = new List<string>();
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
                    vertices,
                    kPartitionSize);
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
                    // Simplify the complete hierarchy group in one operation. Match
                    // meshoptimizer clusterlod first: one permissive attribute-aware
                    // pass followed by the official sloppy fallback. Only a group that
                    // would otherwise become a large permanent root may use the guarded
                    // vertex-update path below.
                    simplifyAttempts++;
                    List<int> simplified = SimplifyWithStructuralGuard(
                        vertices,
                        attributes,
                        merged,
                        locks,
                        targetSize,
                        remap,
                        quantizedPositionRemap,
                        ref simplifyError,
                        ref structuralRetries,
                        ref structuralFailures);
                    // clusterlod invokes the topology-independent fallback whenever
                    // the primary simplifier misses the requested 50% target. The
                    // 85% threshold is only the terminal decision after fallback.
                    if (simplified.Count > targetSize)
                    {
                        sloppyFallbackAttempts++;
                        float guardedError = simplifyError;
                        float fallbackError = 0f;
                        List<int> fallback = SimplifySloppyFallback(
                            vertices,
                            attributes,
                            merged,
                            locks,
                            targetSize,
                            remap,
                            ref fallbackError);
                        int sourceComponents = CountPositionComponents(merged, remap);
                        float sourceUvStretch = MaxUvEdgeStretch(merged, vertices, attributes);
                        bool fallbackPreservesStructure = ReplacementPreservesStructure(
                            merged,
                            fallback,
                            vertices,
                            attributes,
                            locks,
                            remap,
                            quantizedPositionRemap,
                            sourceComponents,
                            sourceUvStretch);
                        if (fallbackPreservesStructure &&
                            fallback.Count <= merged.Count * kSimplifyThreshold)
                        {
                            simplified = fallback;
                            simplifyError = EstimateGeometricSimplificationError(
                                vertices,
                                merged,
                                locks,
                                fallback.Count,
                                true);
                            sloppyFallbackSuccesses++;
                        }
                        else
                        {
                            simplifyError = guardedError;
                            sloppyFallbackRejects++;
                        }
                    }
                    if (simplified.Count < 3 || simplified.Count % 3 != 0)
                    {
                        // Treat an unrepresentable native result as "could not simplify";
                        // the existing terminal-group path will preserve the source.
                        simplified = new List<int>(merged);
                        simplifyError = 0f;
                    }

                    bool usedVertexUpdate = false;
                    if (curMip >= kVertexUpdateStartMip &&
                        simplified.Count > merged.Count * kSimplifyThreshold)
                    {
                        vertexUpdateAttempts++;
                        float preVertexUpdateError = simplifyError;
                        int geometryCountBeforeVertexUpdate = geometry.Count;
                        // This path runs only when the ordinary seam-protected and
                        // sloppy candidates would become a permanent root. Keep true
                        // producer boundaries locked, but let updated vertices cross
                        // UV wedges; the generated payload is still rejected by the
                        // strict UV/component/persisted-topology gates below.
                        byte[] terminalUpdateLocks = KeepOnlyBoundaryLocks(locks);
                        List<int> vertexUpdated = SimplifyWithVertexUpdateStructuralGuard(
                            geometry,
                            vertices,
                            attributes,
                            merged,
                            terminalUpdateLocks,
                            targetSize,
                            remap,
                            quantizedPositionRemap,
                            ref simplifyError,
                            ref vertexUpdateRetries,
                            ref vertexUpdateFailures,
                            out usedVertexUpdate,
                            false);
                        if (usedVertexUpdate &&
                            vertexUpdated.Count >= 3 &&
                            vertexUpdated.Count % 3 == 0 &&
                            vertexUpdated.Count <= merged.Count * kSimplifyThreshold)
                        {
                            // Native vertex-update simplification appends optimized
                            // payload to the shared arena. The new indices are consumed
                            // immediately by clusterization and by later hierarchy passes,
                            // so every derived array/remap must be refreshed atomically.
                            RefreshBuildArrays(
                                geometry,
                                ref vertices,
                                ref normals,
                                ref uvs,
                                ref tangents,
                                ref attributes,
                                ref remap,
                                ref locks);
                            quantizedPositionRemap = BuildQuantizedPositionRemap(vertices);
                            int sourcePersistedComponents = CountPositionComponents(
                                merged,
                                quantizedPositionRemap);
                            int candidatePersistedComponents = CountPositionComponents(
                                vertexUpdated,
                                quantizedPositionRemap);
                            if (candidatePersistedComponents == sourcePersistedComponents)
                            {
                                simplified = vertexUpdated;
                                vertexUpdateSuccesses++;
                                Debug.Log(
                                    $"[Nanite][BuildDAG][VertexUpdateAccept] m{curMip - 1}, " +
                                    $"tri={merged.Count / 3}->{simplified.Count / 3}, " +
                                    $"persistedComponents={sourcePersistedComponents}->" +
                                    $"{candidatePersistedComponents}.");
                            }
                            else
                            {
                                usedVertexUpdate = false;
                                vertexUpdatePostRejects++;
                            }
                        }
                        else
                        {
                            usedVertexUpdate = false;
                        }
                        if (!usedVertexUpdate)
                        {
                            // A rejected native candidate may already have appended
                            // payload. Roll the arena and every derived view back so
                            // unused vertices cannot leak into the baked asset.
                            geometry.Truncate(geometryCountBeforeVertexUpdate);
                            RefreshBuildArrays(
                                geometry,
                                ref vertices,
                                ref normals,
                                ref uvs,
                                ref tangents,
                                ref attributes,
                                ref remap,
                                ref locks);
                            quantizedPositionRemap = BuildQuantizedPositionRemap(vertices);
                            // The helper's compatibility fallback re-runs the index-only
                            // simplifier. Keep the error paired with the candidate that
                            // is actually going to be published.
                            simplifyError = preVertexUpdateError;
                        }
                    }

                    if (float.IsNaN(simplifyError) ||
                        float.IsInfinity(simplifyError) ||
                        simplifyError < 0f)
                    {
                        throw new InvalidOperationException(
                            $"meshoptimizer returned invalid absolute errors: " +
                            $"appearance={simplifyError}.");
                    }

                    float maxChildSelf = MaxChildSelfError(clusters, groupClusterIndices);

                    float farFieldTransitionError = groupBounds.radius /
                        kFarFieldTransitionRadiusPixels;
                    bool requiresFarFieldProxy =
                        simplified.Count > merged.Count * kSimplifyThreshold ||
                        simplifyError > farFieldTransitionError;
                    if (requiresFarFieldProxy)
                    {
                        // The normal hierarchy is deliberately strict because these
                        // replacements may be visible close to the camera.  Once that
                        // path is irreducible, try a distinct far-field proxy without
                        // topology locks.  Its conservative error below prevents it
                        // from entering the cut until the whole producer is tiny, while
                        // allowing subsequent generations to collapse to a real low-
                        // triangle root instead of freezing all source components.
                        farFieldProxyAttempts++;
                        float farFieldNativeError = 0f;
                        var farFieldSourceOccurrences = new List<int>();
                        List<int> farFieldProxy = SimplifySloppyFallback(
                            vertices,
                            attributes,
                            merged,
                            new byte[locks.Length],
                            targetSize,
                            remap,
                            ref farFieldNativeError,
                            farFieldSourceOccurrences);
                        if (farFieldProxy.Count >= 3 &&
                            farFieldProxy.Count % 3 == 0 &&
                            farFieldProxy.Count <= merged.Count * kSimplifyThreshold)
                        {
                            farFieldProxy = StabilizeFarFieldUv(
                                geometry,
                                vertices,
                                normals,
                                uvs,
                                tangents,
                                merged,
                                farFieldProxy,
                                farFieldSourceOccurrences,
                                out int reprojectedTriangles,
                                out int uvCollapseFallbacks);
                            farFieldUvReprojectedTriangles += reprojectedTriangles;
                            farFieldUvCollapseFallbacks += uvCollapseFallbacks;
                            RefreshBuildArrays(
                                geometry,
                                ref vertices,
                                ref normals,
                                ref uvs,
                                ref tangents,
                                ref attributes,
                                ref remap,
                                ref locks);
                            quantizedPositionRemap = BuildQuantizedPositionRemap(vertices);
                            simplified = farFieldProxy;
                            // simplifySloppy reports a topology-independent fitting
                            // score which is intentionally conservative for separated
                            // shells and is not a useful continuous-LOD distance. It
                            // previously held every far proxy back until one abrupt
                            // 33k->100 triangle jump. Use an explicit screen transition;
                            // the runtime density predicate remains the triangle budget.
                            simplifyError = farFieldTransitionError;
                            farFieldProxySuccesses++;
                        }
                    }

                    if (simplified.Count > merged.Count * kSimplifyThreshold)
                    {
                        simplifyFailures++;
                        CountGroupVertexFlags(
                            merged,
                            locks,
                            out int groupVertices,
                            out int groupLocked,
                            out int groupProtected);
                        terminalDiagnostics.Add(
                            $"m{curMip - 1}:{merged.Count / 3}tri/{groupVertices}v, " +
                            $"lock={groupLocked}, protect={groupProtected}, radius={groupBounds.radius:G6}");
                        // Official clusterlod terminal contract: a producer that cannot
                        // reach the simplification threshold is a permanent root. FLT_MAX
                        // is not a finite disappearance error and must never make a whole
                        // panel vanish at an intermediate camera distance.
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
                            isRootSet = true,
                            // 向 LOD0 累加可观察误差，供 Audit/调试。
                            minLodError = Mathf.Max(minChildError, maxChildSelf)
                        };
                        terminalGroup.children.AddRange(groupClusterIndices);
                        result.clusterGroupList.Add(terminalGroup);
                        continue;
                    }

                    // Bevy：group_error += max(child.self_lod)，再叠简化误差，保持单调可退化。
                    // meshoptimizer/demo/clusterlod.h default configuration uses
                    // simplify_error_merge_previous=1 and
                    // simplify_error_merge_additive=0:
                    //   max(previous_error, current_error)
                    // Adding current_error at every generation inflates upper-level
                    // errors geometrically. That keeps tens of thousands of triangles
                    // alive after the object is already sub-pixel while doing nothing
                    // to protect the first replacement close to the camera.
                    float previousError = Mathf.Max(groupBounds.error, maxChildSelf);
                    groupBounds.error = Mathf.Max(previousError, simplifyError);

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
                    }

                    // The transition sphere is used before individual coarse
                    // clusters are visited. It must enclose both sides of the
                    // replacement edge. Vertex-update simplification can move a
                    // coarse vertex outside the merged fine sphere; publishing the
                    // old sphere makes that intermediate LOD disappear in frustum
                    // traversal and then reappear at the next coarser level.
                    groupBounds = ExpandBoundsToContainGeometry(groupBounds, split);
                    for (int j = 0; j < groupClusterIndices.Count; j++)
                        clusters[groupClusterIndices[j]].parent = groupBounds;

                    var clusterGroup = new ClusterGroup
                    {
                        boundsCenter = groupBounds.center,
                        maxParentLodError = groupBounds.error,
                        radius = groupBounds.radius,
                        mipLevel = curMip - 1,
                        minLodError = minChildError,
                        usesRobustUvGate = usedVertexUpdate
                    };
                    clusterGroup.children.AddRange(groupClusterIndices);
                    result.clusterGroupList.Add(clusterGroup);

                    for (int j = 0; j < split.Count; j++)
                    {
                        var child = split[j];
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
                $"structuralRetry={structuralRetries}, structuralFail={structuralFailures}, " +
                 $"sloppyFallback={sloppyFallbackSuccesses}/{sloppyFallbackAttempts}, " +
                 $"sloppyRejected={sloppyFallbackRejects}, " +
                 $"vertexUpdate={vertexUpdateSuccesses}/{vertexUpdateAttempts}, " +
                 $"vertexUpdateRetry={vertexUpdateRetries}, vertexUpdateFail={vertexUpdateFailures}, " +
                 $"vertexUpdatePostReject={vertexUpdatePostRejects}, " +
                 $"farFieldProxy={farFieldProxySuccesses}/{farFieldProxyAttempts}, " +
                 $"farFieldUvReprojected={farFieldUvReprojectedTriangles}, " +
                 $"farFieldUvCollapseFallback={farFieldUvCollapseFallbacks}, " +
                $"uvSeamProtect={protectedVertexCount}/{vertices.Length}, " +
                $"currentBoundaryLock={currentBoundaryLocks}/{vertices.Length}, " +
                $"residentRootTriangles={residentRootTriangles}.");
            if (terminalDiagnostics.Count > 0)
                Debug.Log("[Nanite][BuildDAG][Terminal] " + string.Join(" | ", terminalDiagnostics));
            return result;
        }

        static LODBounds ExpandBoundsToContainGeometry(
            LODBounds bounds,
            IReadOnlyList<Cluster> clusters)
        {
            if (clusters == null || clusters.Count == 0)
                return bounds;

            var centers = new Vector3[clusters.Count + 1];
            var radii = new float[clusters.Count + 1];
            centers[0] = bounds.center;
            radii[0] = bounds.radius;
            for (int index = 0; index < clusters.Count; index++)
            {
                centers[index + 1] = clusters[index].geometry.center;
                radii[index + 1] = clusters[index].geometry.radius;
            }

            MeshoptBounds merged = MeshOptimizerNative.NativeComputeSphereBounds(
                centers,
                (UIntPtr)(uint)centers.Length,
                (UIntPtr)(uint)(sizeof(float) * 3),
                radii,
                (UIntPtr)(uint)sizeof(float));
            bounds.center = new Vector3(merged.centerX, merged.centerY, merged.centerZ);
            bounds.radius = merged.radius;
            return bounds;
        }

        static void CountGroupVertexFlags(
            IReadOnlyList<int> indices,
            byte[] flags,
            out int vertexCount,
            out int lockedCount,
            out int protectedCount)
        {
            var unique = new HashSet<int>();
            for (int index = 0; index < indices.Count; index++)
                unique.Add(indices[index]);
            vertexCount = unique.Count;
            lockedCount = 0;
            protectedCount = 0;
            foreach (int vertex in unique)
            {
                if ((uint)vertex >= (uint)flags.Length)
                    continue;
                if ((flags[vertex] & MeshoptSimplifyVertexFlags.Lock) != 0)
                    lockedCount++;
                if ((flags[vertex] & MeshoptSimplifyVertexFlags.Protect) != 0)
                    protectedCount++;
            }
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
            ref float error)
        {
            if (targetIndexCount >= merged.Count)
                return merged;

            var indicesU = ToUInt(merged);
            var destination = new uint[indicesU.Length];
            var vertexLock = locks ?? new byte[vertices.Length];
            // Keep the same fixed normal weights as meshoptimizer's Nanite demo.
            // UV seams are represented by Protect flags; dynamically converting UV
            // scale into the error metric makes the reported error cease to be an
            // object-space distance and inverts the far-field LOD curve.
            float[] attributeWeights = kAttributeWeights;

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
                attributeWeights,
                (UIntPtr)(uint)attributeWeights.Length,
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
            Vector3[] vertices,
            int targetPartitionSize)
        {
            targetPartitionSize = Mathf.Max(2, targetPartitionSize);
            if (pending.Count <= targetPartitionSize)
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
                (UIntPtr)(uint)targetPartitionSize);

            int groupCount = Mathf.Max(1, (int)partitionCount);
            var result = new List<List<int>>(groupCount);
            for (int groupIndex = 0; groupIndex < groupCount; groupIndex++)
                result.Add(new List<int>(targetPartitionSize));
            for (int pendingIndex = 0; pendingIndex < pending.Count; pendingIndex++)
            {
                int groupIndex = Mathf.Clamp((int)clusterPart[pendingIndex], 0, groupCount - 1);
                result[groupIndex].Add(pending[pendingIndex]);
            }
            return result;
        }

        static List<int> SimplifySloppyFallback(
            Vector3[] vertices,
            float[] attributes,
            List<int> merged,
            byte[] locks,
            int targetIndexCount,
            uint[] positionRemap,
            ref float error,
            List<int> sourceOccurrences = null)
        {
            if (sSloppyFallbackUnavailable)
                return new List<int>(merged);

            // meshoptimizer clusterlod.h deindexes a sparse group before the
            // topology-independent fallback, then restores original vertex IDs.
            int subsetCount = merged.Count;
            var subsetPositions = new Vector3[subsetCount];
            var subsetIndices = new uint[subsetCount];
            var subsetLocks = new byte[subsetCount];
            for (int index = 0; index < subsetCount; index++)
            {
                int sourceVertex = merged[index];
                subsetPositions[index] = vertices[sourceVertex];
                subsetIndices[index] = (uint)index;
                subsetLocks[index] = locks[sourceVertex];
            }

            var destination = new uint[subsetCount];
            float normalizedError;
            UIntPtr outCount;
            try
            {
                outCount = MeshOptimizerNative.NativeSimplifySloppy(
                    destination,
                    subsetIndices,
                    (UIntPtr)(uint)subsetCount,
                    subsetPositions,
                    (UIntPtr)(uint)subsetPositions.Length,
                    (UIntPtr)(uint)kVertexStride,
                    subsetLocks,
                    (UIntPtr)(uint)targetIndexCount,
                    float.MaxValue,
                    out normalizedError);
            }
            catch (EntryPointNotFoundException)
            {
                sSloppyFallbackUnavailable = true;
                Debug.LogWarning(
                    "[Nanite][BuildDAG] Native plugin does not export SimplifySloppy; " +
                    "using topology-preserving terminal groups until the plugin is updated.");
                return new List<int>(merged);
            }

            int count = (int)outCount;
            if (count < 3 || count % 3 != 0)
                return new List<int>(merged);

            var fallback = new List<int>(count);
            sourceOccurrences?.Clear();
            for (int index = 0; index < count; index++)
            {
                uint subsetVertex = destination[index];
                if (subsetVertex >= (uint)merged.Count)
                    return new List<int>(merged);
                fallback.Add(merged[(int)subsetVertex]);
                sourceOccurrences?.Add((int)subsetVertex);
            }

            float absoluteError = normalizedError * MeshOptimizerNative.NativeSimplifyScale(
                subsetPositions,
                (UIntPtr)(uint)subsetPositions.Length,
                (UIntPtr)(uint)kVertexStride);
            error = absoluteError * 2f;
            return fallback;
        }

        static List<int> StabilizeFarFieldUv(
            NaniteBuildGeometry geometry,
            Vector3[] vertices,
            Vector3[] normals,
            Vector2[] uvs,
            Vector4[] tangents,
            IReadOnlyList<int> sourceIndices,
            IReadOnlyList<int> proxyIndices,
            IReadOnlyList<int> sourceOccurrences,
            out int reprojectedTriangleCount,
            out int uvCollapseFallbackCount)
        {
            reprojectedTriangleCount = 0;
            uvCollapseFallbackCount = 0;
            if (sourceOccurrences == null || sourceOccurrences.Count != proxyIndices.Count)
                return new List<int>(proxyIndices);

            int sourceTriangleCount = sourceIndices.Count / 3;
            if (sourceTriangleCount == 0)
                return new List<int>(proxyIndices);

            var componentParents = new int[sourceTriangleCount];
            var chartParents = new int[sourceTriangleCount];
            var triangleNormals = new Vector3[sourceTriangleCount];
            var triangleAreas = new float[sourceTriangleCount];
            var positionOwners = new Dictionary<Vector3, int>();
            var uvEdgeOwners = new Dictionary<FarFieldUvEdgeKey, int>();
            for (int triangle = 0; triangle < sourceTriangleCount; triangle++)
            {
                componentParents[triangle] = triangle;
                chartParents[triangle] = triangle;
                int source = triangle * 3;
                int i0 = sourceIndices[source + 0];
                int i1 = sourceIndices[source + 1];
                int i2 = sourceIndices[source + 2];
                Vector3 p0 = vertices[i0];
                Vector3 p1 = vertices[i1];
                Vector3 p2 = vertices[i2];
                Vector3 cross = Vector3.Cross(p1 - p0, p2 - p0);
                float twiceArea = cross.magnitude;
                triangleNormals[triangle] = twiceArea > 1e-12f
                    ? cross / twiceArea
                    : Vector3.up;
                triangleAreas[triangle] = twiceArea * 0.5f;

                ConnectFarFieldPosition(positionOwners, componentParents, p0, triangle);
                ConnectFarFieldPosition(positionOwners, componentParents, p1, triangle);
                ConnectFarFieldPosition(positionOwners, componentParents, p2, triangle);

                var k0 = new FarFieldUvVertexKey(p0, uvs[i0]);
                var k1 = new FarFieldUvVertexKey(p1, uvs[i1]);
                var k2 = new FarFieldUvVertexKey(p2, uvs[i2]);
                ConnectFarFieldUvEdge(uvEdgeOwners, chartParents, new FarFieldUvEdgeKey(k0, k1), triangle);
                ConnectFarFieldUvEdge(uvEdgeOwners, chartParents, new FarFieldUvEdgeKey(k1, k2), triangle);
                ConnectFarFieldUvEdge(uvEdgeOwners, chartParents, new FarFieldUvEdgeKey(k2, k0), triangle);
            }

            var componentAreas = new Dictionary<int, float>();
            var chartTriangles = new Dictionary<int, List<int>>();
            for (int triangle = 0; triangle < sourceTriangleCount; triangle++)
            {
                int component = FindFarFieldRoot(componentParents, triangle);
                componentAreas.TryGetValue(component, out float componentArea);
                componentAreas[component] = componentArea + Mathf.Max(1e-12f, triangleAreas[triangle]);

                int chart = FindFarFieldRoot(chartParents, triangle);
                if (!chartTriangles.TryGetValue(chart, out List<int> triangles))
                {
                    triangles = new List<int>();
                    chartTriangles.Add(chart, triangles);
                }
                triangles.Add(triangle);
            }

            var result = new List<int>(proxyIndices.Count);
            for (int index = 0; index + 2 < proxyIndices.Count; index += 3)
            {
                int occurrence0 = sourceOccurrences[index + 0];
                int occurrence1 = sourceOccurrences[index + 1];
                int occurrence2 = sourceOccurrences[index + 2];
                int sourceTriangle0 = occurrence0 / 3;
                int sourceTriangle1 = occurrence1 / 3;
                int sourceTriangle2 = occurrence2 / 3;

                int chart0 = FindFarFieldRoot(chartParents, sourceTriangle0);
                int chart1 = FindFarFieldRoot(chartParents, sourceTriangle1);
                int chart2 = FindFarFieldRoot(chartParents, sourceTriangle2);
                // Exact payload is already coherent when all selected corners belong
                // to the same connected UV chart. Do not flatten those triangles.
                if (chart0 == chart1 && chart1 == chart2)
                {
                    result.Add(proxyIndices[index + 0]);
                    result.Add(proxyIndices[index + 1]);
                    result.Add(proxyIndices[index + 2]);
                    continue;
                }

                Vector3 a = vertices[proxyIndices[index + 0]];
                Vector3 b = vertices[proxyIndices[index + 1]];
                Vector3 c = vertices[proxyIndices[index + 2]];
                Vector3 proxyCenter = (a + b + c) / 3f;
                Vector3 proxyCross = Vector3.Cross(b - a, c - a);
                Vector3 proxyNormal = proxyCross.sqrMagnitude > 1e-20f
                    ? proxyCross.normalized
                    : triangleNormals[sourceTriangle0];

                int[] candidates = { sourceTriangle0, sourceTriangle1, sourceTriangle2 };
                int selectedComponent = FindFarFieldRoot(componentParents, sourceTriangle0);
                float selectedRank = float.NegativeInfinity;
                for (int candidateIndex = 0; candidateIndex < candidates.Length; candidateIndex++)
                {
                    int component = FindFarFieldRoot(componentParents, candidates[candidateIndex]);
                    int support = 0;
                    for (int supportIndex = 0; supportIndex < candidates.Length; supportIndex++)
                    {
                        if (FindFarFieldRoot(componentParents, candidates[supportIndex]) == component)
                            support++;
                    }
                    float area = componentAreas.TryGetValue(component, out float value) ? value : 0f;
                    // Prefer the surface represented by most corners, weighted by
                    // component area so a tiny decal cannot paint a whole body proxy.
                    float rank = support * Mathf.Sqrt(Mathf.Max(area, 1e-12f));
                    if (rank > selectedRank)
                    {
                        selectedRank = rank;
                        selectedComponent = component;
                    }
                }

                int bestSourceTriangle = -1;
                float bestScore = float.MaxValue;
                float proxyScaleSq = Mathf.Max(
                    (b - a).sqrMagnitude,
                    Mathf.Max((c - b).sqrMagnitude, (a - c).sqrMagnitude));
                for (int triangle = 0; triangle < sourceTriangleCount; triangle++)
                {
                    if (FindFarFieldRoot(componentParents, triangle) != selectedComponent)
                        continue;
                    int source = triangle * 3;
                    Vector3 s0 = vertices[sourceIndices[source + 0]];
                    Vector3 s1 = vertices[sourceIndices[source + 1]];
                    Vector3 s2 = vertices[sourceIndices[source + 2]];
                    float distanceSq = ClosestPointBarycentric(proxyCenter, s0, s1, s2, out _);
                    float normalPenalty = 1f - Mathf.Abs(Vector3.Dot(proxyNormal, triangleNormals[triangle]));
                    float score = distanceSq + normalPenalty * Mathf.Max(proxyScaleSq, 1e-8f);
                    if (score < bestScore)
                    {
                        bestScore = score;
                        bestSourceTriangle = triangle;
                    }
                }

                if (bestSourceTriangle < 0)
                {
                    result.Add(proxyIndices[index + 0]);
                    result.Add(proxyIndices[index + 1]);
                    result.Add(proxyIndices[index + 2]);
                    continue;
                }

                int selectedChart = FindFarFieldRoot(chartParents, bestSourceTriangle);
                List<int> selectedChartTriangles = chartTriangles[selectedChart];
                var mappedUv = new Vector2[3];
                var mappedNormal = new Vector3[3];
                var mappedTangent = new Vector4[3];
                Vector3[] proxyPositions = { a, b, c };
                for (int corner = 0; corner < 3; corner++)
                {
                    ProjectFarFieldAttributes(
                        proxyPositions[corner],
                        proxyNormal,
                        selectedChartTriangles,
                        sourceIndices,
                        vertices,
                        normals,
                        uvs,
                        tangents,
                        out mappedNormal[corner],
                        out mappedUv[corner],
                        out mappedTangent[corner]);
                }

                float mappedUvArea = Mathf.Abs(
                    (mappedUv[1].x - mappedUv[0].x) * (mappedUv[2].y - mappedUv[0].y) -
                    (mappedUv[1].y - mappedUv[0].y) * (mappedUv[2].x - mappedUv[0].x));
                if (mappedUvArea <= 1e-10f && proxyCross.sqrMagnitude > 1e-16f)
                {
                    // Nearest-point projection can clamp a distant proxy to one
                    // chart boundary. Reconstruct a bounded local footprint from the
                    // matched primitive's position->UV Jacobian. Copying the complete
                    // source UV triangle onto a tiny proxy creates extreme derivatives,
                    // magnifies decals and also makes anisotropic sampling expensive.
                    int source = bestSourceTriangle * 3;
                    int s0 = sourceIndices[source + 0];
                    int s1 = sourceIndices[source + 1];
                    int s2 = sourceIndices[source + 2];
                    if (!TryBuildBoundedFarFieldUvFootprint(
                            proxyPositions,
                            proxyCenter,
                            vertices[s0],
                            vertices[s1],
                            vertices[s2],
                            uvs[s0],
                            uvs[s1],
                            uvs[s2],
                            mappedUv))
                    {
                        // A degenerate source Jacobian has no trustworthy texture
                        // direction. Sampling the selected main-surface primitive at
                        // its centroid is safer than expanding an arbitrary texel or
                        // crossing an atlas/chart boundary.
                        Vector2 centroidUv = (uvs[s0] + uvs[s1] + uvs[s2]) / 3f;
                        mappedUv[0] = centroidUv;
                        mappedUv[1] = centroidUv;
                        mappedUv[2] = centroidUv;
                    }
                    uvCollapseFallbackCount++;
                }

                for (int corner = 0; corner < 3; corner++)
                {
                    int sourceVertex = proxyIndices[index + corner];
                    int generatedVertex = geometry.Count;
                    geometry.Append(
                        vertices[sourceVertex],
                        mappedNormal[corner],
                        mappedUv[corner],
                        mappedTangent[corner]);
                    result.Add(generatedVertex);
                }
                reprojectedTriangleCount++;
            }
            return result;
        }

        static bool TryBuildBoundedFarFieldUvFootprint(
            Vector3[] proxyPositions,
            Vector3 proxyCenter,
            Vector3 sourcePosition0,
            Vector3 sourcePosition1,
            Vector3 sourcePosition2,
            Vector2 sourceUv0,
            Vector2 sourceUv1,
            Vector2 sourceUv2,
            Vector2[] result)
        {
            Vector3 sourceEdge1 = sourcePosition1 - sourcePosition0;
            Vector3 sourceEdge2 = sourcePosition2 - sourcePosition0;
            float gram11 = Vector3.Dot(sourceEdge1, sourceEdge1);
            float gram12 = Vector3.Dot(sourceEdge1, sourceEdge2);
            float gram22 = Vector3.Dot(sourceEdge2, sourceEdge2);
            float gramDeterminant = gram11 * gram22 - gram12 * gram12;
            float gramScale = Mathf.Max(gram11 * gram22, 1e-30f);
            float uvDeterminant = Cross2(sourceUv1 - sourceUv0, sourceUv2 - sourceUv0);
            if (gramDeterminant <= gramScale * 1e-10f || Mathf.Abs(uvDeterminant) <= 1e-12f)
                return false;

            Vector2 uvEdge1 = sourceUv1 - sourceUv0;
            Vector2 uvEdge2 = sourceUv2 - sourceUv0;
            Vector2 uvCenter = (sourceUv0 + sourceUv1 + sourceUv2) / 3f;
            for (int corner = 0; corner < 3; corner++)
            {
                Vector3 delta = proxyPositions[corner] - proxyCenter;
                float rhs1 = Vector3.Dot(delta, sourceEdge1);
                float rhs2 = Vector3.Dot(delta, sourceEdge2);
                float coordinate1 = (rhs1 * gram22 - rhs2 * gram12) / gramDeterminant;
                float coordinate2 = (rhs2 * gram11 - rhs1 * gram12) / gramDeterminant;
                result[corner] = uvCenter + uvEdge1 * coordinate1 + uvEdge2 * coordinate2;
                if (!float.IsFinite(result[corner].x) || !float.IsFinite(result[corner].y))
                    return false;
            }

            // Scale the affine footprint about the source UV centroid until all
            // corners remain inside this source triangle. This retains the local
            // texel density and orientation whenever possible, while preventing an
            // extrapolated proxy from sampling a neighbouring atlas island.
            const float minimumBarycentric = 1e-3f;
            const float centroidBarycentric = 1f / 3f;
            float footprintScale = 1f;
            for (int corner = 0; corner < 3; corner++)
            {
                if (!TryGetUvBarycentric(
                        result[corner],
                        sourceUv0,
                        sourceUv1,
                        sourceUv2,
                        out Vector3 barycentric))
                    return false;

                for (int axis = 0; axis < 3; axis++)
                {
                    float value = barycentric[axis];
                    if (value >= minimumBarycentric)
                        continue;
                    float denominator = centroidBarycentric - value;
                    if (denominator > 1e-12f)
                    {
                        footprintScale = Mathf.Min(
                            footprintScale,
                            (centroidBarycentric - minimumBarycentric) / denominator);
                    }
                }
            }

            footprintScale = Mathf.Clamp01(footprintScale) * 0.995f;
            for (int corner = 0; corner < 3; corner++)
                result[corner] = uvCenter + (result[corner] - uvCenter) * footprintScale;
            return true;
        }

        static bool TryGetUvBarycentric(
            Vector2 point,
            Vector2 a,
            Vector2 b,
            Vector2 c,
            out Vector3 barycentric)
        {
            float determinant = Cross2(b - a, c - a);
            if (Mathf.Abs(determinant) <= 1e-12f)
            {
                barycentric = default;
                return false;
            }

            Vector2 relative = point - a;
            float bWeight = Cross2(relative, c - a) / determinant;
            float cWeight = Cross2(b - a, relative) / determinant;
            barycentric = new Vector3(1f - bWeight - cWeight, bWeight, cWeight);
            return float.IsFinite(barycentric.x) &&
                   float.IsFinite(barycentric.y) &&
                   float.IsFinite(barycentric.z);
        }

        static float Cross2(Vector2 a, Vector2 b)
        {
            return a.x * b.y - a.y * b.x;
        }

        static void ConnectFarFieldPosition(
            Dictionary<Vector3, int> owners,
            int[] parents,
            Vector3 position,
            int triangle)
        {
            if (owners.TryGetValue(position, out int owner))
                UnionFarField(parents, owner, triangle);
            else
                owners.Add(position, triangle);
        }

        static void ConnectFarFieldUvEdge(
            Dictionary<FarFieldUvEdgeKey, int> owners,
            int[] parents,
            FarFieldUvEdgeKey edge,
            int triangle)
        {
            if (owners.TryGetValue(edge, out int owner))
                UnionFarField(parents, owner, triangle);
            else
                owners.Add(edge, triangle);
        }

        static int FindFarFieldRoot(int[] parents, int value)
        {
            int root = value;
            while (parents[root] != root)
                root = parents[root];
            while (parents[value] != value)
            {
                int next = parents[value];
                parents[value] = root;
                value = next;
            }
            return root;
        }

        static void UnionFarField(int[] parents, int a, int b)
        {
            int rootA = FindFarFieldRoot(parents, a);
            int rootB = FindFarFieldRoot(parents, b);
            if (rootA != rootB)
                parents[rootB] = rootA;
        }

        static void ProjectFarFieldAttributes(
            Vector3 position,
            Vector3 proxyNormal,
            IReadOnlyList<int> chartTriangles,
            IReadOnlyList<int> sourceIndices,
            Vector3[] vertices,
            Vector3[] normals,
            Vector2[] uvs,
            Vector4[] tangents,
            out Vector3 normal,
            out Vector2 uv,
            out Vector4 tangent)
        {
            int bestTriangle = chartTriangles[0];
            Vector3 bestBarycentric = new Vector3(1f, 0f, 0f);
            float bestScore = float.MaxValue;
            for (int listIndex = 0; listIndex < chartTriangles.Count; listIndex++)
            {
                int triangle = chartTriangles[listIndex];
                int source = triangle * 3;
                Vector3 p0 = vertices[sourceIndices[source + 0]];
                Vector3 p1 = vertices[sourceIndices[source + 1]];
                Vector3 p2 = vertices[sourceIndices[source + 2]];
                float distanceSq = ClosestPointBarycentric(position, p0, p1, p2, out Vector3 barycentric);
                Vector3 sourceCross = Vector3.Cross(p1 - p0, p2 - p0);
                float normalPenalty = sourceCross.sqrMagnitude > 1e-20f
                    ? 1f - Mathf.Abs(Vector3.Dot(proxyNormal, sourceCross.normalized))
                    : 1f;
                float score = distanceSq + normalPenalty * 1e-4f;
                if (score < bestScore)
                {
                    bestScore = score;
                    bestTriangle = triangle;
                    bestBarycentric = barycentric;
                }
            }

            int bestSource = bestTriangle * 3;
            int i0 = sourceIndices[bestSource + 0];
            int i1 = sourceIndices[bestSource + 1];
            int i2 = sourceIndices[bestSource + 2];
            uv = uvs[i0] * bestBarycentric.x +
                 uvs[i1] * bestBarycentric.y +
                 uvs[i2] * bestBarycentric.z;
            normal = normals[i0] * bestBarycentric.x +
                     normals[i1] * bestBarycentric.y +
                     normals[i2] * bestBarycentric.z;
            normal = normal.sqrMagnitude > 1e-20f ? normal.normalized : normals[i0];
            tangent = tangents[i0] * bestBarycentric.x +
                      tangents[i1] * bestBarycentric.y +
                      tangents[i2] * bestBarycentric.z;
            Vector3 tangentDirection = new Vector3(tangent.x, tangent.y, tangent.z);
            if (tangentDirection.sqrMagnitude > 1e-20f)
            {
                tangentDirection.Normalize();
                tangent.x = tangentDirection.x;
                tangent.y = tangentDirection.y;
                tangent.z = tangentDirection.z;
            }
            tangent.w = tangent.w < 0f ? -1f : 1f;
        }

        static float ClosestPointBarycentric(
            Vector3 point,
            Vector3 a,
            Vector3 b,
            Vector3 c,
            out Vector3 barycentric)
        {
            Vector3 ab = b - a;
            Vector3 ac = c - a;
            Vector3 ap = point - a;
            float d1 = Vector3.Dot(ab, ap);
            float d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0f && d2 <= 0f)
            {
                barycentric = new Vector3(1f, 0f, 0f);
                return (point - a).sqrMagnitude;
            }
            Vector3 bp = point - b;
            float d3 = Vector3.Dot(ab, bp);
            float d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0f && d4 <= d3)
            {
                barycentric = new Vector3(0f, 1f, 0f);
                return (point - b).sqrMagnitude;
            }
            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f)
            {
                float v = d1 / Mathf.Max(d1 - d3, 1e-20f);
                barycentric = new Vector3(1f - v, v, 0f);
                return (point - (a + ab * v)).sqrMagnitude;
            }
            Vector3 cp = point - c;
            float d5 = Vector3.Dot(ab, cp);
            float d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0f && d5 <= d6)
            {
                barycentric = new Vector3(0f, 0f, 1f);
                return (point - c).sqrMagnitude;
            }
            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f)
            {
                float w = d2 / Mathf.Max(d2 - d6, 1e-20f);
                barycentric = new Vector3(1f - w, 0f, w);
                return (point - (a + ac * w)).sqrMagnitude;
            }
            float va = d3 * d6 - d5 * d4;
            if (va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f)
            {
                float w = (d4 - d3) / Mathf.Max((d4 - d3) + (d5 - d6), 1e-20f);
                barycentric = new Vector3(0f, 1f - w, w);
                return (point - (b + (c - b) * w)).sqrMagnitude;
            }
            float sum = va + vb + vc;
            if (Mathf.Abs(sum) <= 1e-20f)
            {
                barycentric = new Vector3(1f, 0f, 0f);
                return (point - a).sqrMagnitude;
            }
            float inverse = 1f / sum;
            float insideV = vb * inverse;
            float insideW = vc * inverse;
            barycentric = new Vector3(1f - insideV - insideW, insideV, insideW);
            return (point - (a + ab * insideV + ac * insideW)).sqrMagnitude;
        }

        static List<int> SimplifyWithVertexUpdateStructuralGuard(
            NaniteBuildGeometry geometry,
            Vector3[] vertices,
            float[] attributes,
            List<int> merged,
            byte[] locks,
            int targetIndexCount,
            uint[] positionRemap,
            uint[] quantizedPositionRemap,
            ref float error,
            ref int retryCount,
            ref int failureCount,
            out bool usedVertexUpdate,
            bool allowComponentGrowth = false)
        {
            usedVertexUpdate = false;
            // Page positions are UNORM16. Components separated only below that
            // precision are already one persisted component, so the vertex-update
            // guard must use the same topology domain as raster/runtime audits.
            int sourceComponents = CountPositionComponents(merged, quantizedPositionRemap);
            float sourceUvStretch = allowComponentGrowth
                ? RobustUvEdgeStretch(merged, vertices, attributes)
                : MaxUvEdgeStretch(merged, vertices, attributes);
            float[] ratios = { kSimplifyRatio, 0.625f, 0.75f, 0.8f };

            for (int attempt = 0; attempt < ratios.Length; attempt++)
            {
                int attemptTarget = attempt == 0
                    ? targetIndexCount
                    : Mathf.Max(3, Mathf.FloorToInt(merged.Count / 3f * ratios[attempt]) * 3);
                if (!TrySimplifyWithVertexUpdate(
                        geometry,
                        vertices,
                        attributes,
                        merged,
                        locks,
                        attemptTarget,
                        positionRemap,
                        quantizedPositionRemap,
                        sourceComponents,
                        sourceUvStretch,
                        allowComponentGrowth,
                        out List<int> candidate,
                        out float candidateError))
                {
                    if (sVertexUpdateUnavailable)
                        break;
                    continue;
                }

                if (attempt > 0)
                    retryCount += attempt;
                error = candidateError;
                usedVertexUpdate = true;
                return candidate;
            }

            retryCount += ratios.Length - 1;
            failureCount++;
            error = 0f;
            // Fall back to the proven index-only path. This keeps an old native
            // plugin usable and avoids publishing a malformed generated payload.
            return SimplifyWithStructuralGuard(
                vertices,
                attributes,
                merged,
                locks,
                targetIndexCount,
                positionRemap,
                positionRemap,
                ref error,
                ref retryCount,
                ref failureCount);
        }

        static bool TrySimplifyWithVertexUpdate(
            NaniteBuildGeometry geometry,
            Vector3[] vertices,
            float[] attributes,
            IReadOnlyList<int> merged,
            byte[] locks,
            int targetIndexCount,
            uint[] sourcePositionRemap,
            uint[] sourceQuantizedPositionRemap,
            int sourceComponents,
            float sourceUvStretch,
            bool allowComponentGrowth,
            out List<int> result,
            out float resultError)
        {
            result = null;
            resultError = 0f;
            var globalToLocal = new Dictionary<int, int>();
            var localToGlobal = new List<int>();
            var localIndices = new uint[merged.Count];
            for (int index = 0; index < merged.Count; index++)
            {
                int globalVertex = merged[index];
                if (!globalToLocal.TryGetValue(globalVertex, out int localVertex))
                {
                    localVertex = localToGlobal.Count;
                    globalToLocal.Add(globalVertex, localVertex);
                    localToGlobal.Add(globalVertex);
                }
                localIndices[index] = (uint)localVertex;
            }

            var localPositions = new Vector3[localToGlobal.Count];
            var originalPositions = new Vector3[localToGlobal.Count];
            var localAttributes = new float[localToGlobal.Count * kAttributeWeights.Length];
            var originalAttributes = new float[localAttributes.Length];
            var localLocks = new byte[localToGlobal.Count];
            for (int localVertex = 0; localVertex < localToGlobal.Count; localVertex++)
            {
                int globalVertex = localToGlobal[localVertex];
                localPositions[localVertex] = vertices[globalVertex];
                originalPositions[localVertex] = vertices[globalVertex];
                Array.Copy(
                    attributes,
                    globalVertex * kAttributeWeights.Length,
                    localAttributes,
                    localVertex * kAttributeWeights.Length,
                    kAttributeWeights.Length);
                Array.Copy(
                    localAttributes,
                    localVertex * kAttributeWeights.Length,
                    originalAttributes,
                    localVertex * kAttributeWeights.Length,
                    kAttributeWeights.Length);
                localLocks[localVertex] = locks[globalVertex];
            }

            UIntPtr outCount;
            var localSourceIndices = new List<int>(localIndices.Length);
            for (int index = 0; index < localIndices.Length; index++)
                localSourceIndices.Add((int)localIndices[index]);
            float[] attributeWeights = BuildGroupAttributeWeights(
                originalPositions,
                originalAttributes,
                localSourceIndices);
            try
            {
                outCount = MeshOptimizerNative.NativeSimplifyWithUpdate(
                    localIndices,
                    (UIntPtr)(uint)localIndices.Length,
                    localPositions,
                    (UIntPtr)(uint)localPositions.Length,
                    (UIntPtr)(uint)kVertexStride,
                    localAttributes,
                    (UIntPtr)(uint)kAttributeStride,
                    attributeWeights,
                    (UIntPtr)(uint)attributeWeights.Length,
                    localLocks,
                    (UIntPtr)(uint)targetIndexCount,
                    float.MaxValue,
                    MeshoptSimplifyFlags.ErrorAbsolute | MeshoptSimplifyFlags.Permissive,
                    out resultError);
            }
            catch (EntryPointNotFoundException)
            {
                sVertexUpdateUnavailable = true;
                Debug.LogWarning(
                    "[Nanite][BuildDAG] Native plugin does not export SimplifyWithUpdate; " +
                    "coarse levels remain on the index-only path until the plugin is updated.");
                return false;
            }

            int count = (int)outCount;
            if (count < 3 || count % 3 != 0 || count > localIndices.Length)
            {
                if (allowComponentGrowth)
                    Debug.Log($"[Nanite][BuildDAG][FarFieldReject] native-count={count}/{localIndices.Length}, target={targetIndexCount}.");
                return false;
            }
            var localCandidate = new List<int>(count);
            for (int index = 0; index < count; index++)
            {
                uint localVertex = localIndices[index];
                if (localVertex >= (uint)localPositions.Length)
                {
                    if (allowComponentGrowth)
                        Debug.Log($"[Nanite][BuildDAG][FarFieldReject] invalid-local-index={localVertex}/{localPositions.Length}.");
                    return false;
                }
                localCandidate.Add((int)localVertex);
            }

            // UV/normal wedges that represented one source position must remain
            // geometrically coincident after the in-place update. meshoptimizer can
            // move each attribute wedge independently; leaving those tiny differences
            // turns one connected shell into several exact-position components in the
            // Page topology and can expose cracks at an LOD transition.
            CoalescePersistedPositionClasses(
                localPositions,
                localToGlobal,
                sourceQuantizedPositionRemap);

            if (!AllFinite(localPositions, localAttributes))
            {
                if (allowComponentGrowth)
                    Debug.Log("[Nanite][BuildDAG][FarFieldReject] non-finite updated payload.");
                return false;
            }
            var localRemap = new uint[localPositions.Length];
            MeshOptimizerNative.NativeGeneratePositionRemap(
                localRemap,
                localPositions,
                (UIntPtr)(uint)localPositions.Length,
                (UIntPtr)(uint)kVertexStride);
            if (!RepairSourceComponentCoverage(
                    merged,
                    localCandidate,
                    localToGlobal,
                    sourceQuantizedPositionRemap,
                    out int missingComponents,
                    out int underRepresentedClosedComponents,
                    out HashSet<int> restoredLocalVertices))
            {
                if (allowComponentGrowth)
                {
                    Debug.Log(
                        $"[Nanite][BuildDAG][FarFieldReject] component-coverage=" +
                        $"missing:{missingComponents}, closed-underrepresented:{underRepresentedClosedComponents}.");
                }
                return false;
            }
            foreach (int localVertex in restoredLocalVertices)
            {
                localPositions[localVertex] = originalPositions[localVertex];
                Array.Copy(
                    originalAttributes,
                    localVertex * kAttributeWeights.Length,
                    localAttributes,
                    localVertex * kAttributeWeights.Length,
                    kAttributeWeights.Length);
            }
            MeshOptimizerNative.NativeGeneratePositionRemap(
                localRemap,
                localPositions,
                (UIntPtr)(uint)localPositions.Length,
                (UIntPtr)(uint)kVertexStride);
            int candidateComponents = CountPositionComponents(localCandidate, localRemap);
            if (candidateComponents < sourceComponents && allowComponentGrowth)
            {
                // Vertex-update simplification can move two disconnected shells onto
                // the same position. Keep the reduced topology but restore its source
                // payload, which preserves windows/trim without abandoning the entire
                // producer as a multi-thousand-triangle resident root.
                var usedVertices = new HashSet<int>(localCandidate);
                foreach (int localVertex in usedVertices)
                {
                    localPositions[localVertex] = originalPositions[localVertex];
                    Array.Copy(
                        originalAttributes,
                        localVertex * kAttributeWeights.Length,
                        localAttributes,
                        localVertex * kAttributeWeights.Length,
                        kAttributeWeights.Length);
                }
                MeshOptimizerNative.NativeGeneratePositionRemap(
                    localRemap,
                    localPositions,
                    (UIntPtr)(uint)localPositions.Length,
                    (UIntPtr)(uint)kVertexStride);
                candidateComponents = CountPositionComponents(localCandidate, localRemap);
            }
            if (candidateComponents < sourceComponents ||
                (!allowComponentGrowth && candidateComponents > sourceComponents))
                return false;
            float candidateUvStretch = allowComponentGrowth
                ? RobustUvEdgeStretch(localCandidate, localPositions, localAttributes)
                : MaxUvEdgeStretch(localCandidate, localPositions, localAttributes);
            if (candidateUvStretch > Mathf.Max(32f, sourceUvStretch * 4f))
            {
                if (allowComponentGrowth)
                    Debug.Log($"[Nanite][BuildDAG][FarFieldReject] uv-stretch={candidateUvStretch:G6}, source={sourceUvStretch:G6}.");
                return false;
            }
            if (!PreservesLockedLocalPositions(
                    merged,
                    localCandidate,
                    localToGlobal,
                    localLocks,
                    sourcePositionRemap))
            {
                if (allowComponentGrowth)
                    Debug.Log("[Nanite][BuildDAG][FarFieldReject] producer boundary Lock was removed.");
                return false;
            }

            float geometricCollapseError = EstimateGeometricSimplificationError(
                originalPositions,
                localSourceIndices,
                localLocks,
                count,
                false);
            float movedVertexError = 0f;
            var usedLocalVertices = new HashSet<int>(localCandidate);
            foreach (int localVertex in usedLocalVertices)
            {
                movedVertexError = Mathf.Max(
                    movedVertexError,
                    Vector3.Distance(localPositions[localVertex], originalPositions[localVertex]));
            }
            float geometricError = Mathf.Max(geometricCollapseError, movedVertexError);
            // Runtime projects an object-space distance. The attribute-weighted
            // simplifier error chooses a candidate but is not measured in metres;
            // using it here inflated upper levels hundreds of times past the model
            // radius. UV/topology gates above enforce attribute fidelity.
            resultError = geometricError;

            // Component coverage repair may append a small source support set after
            // the native result. Publish and size against the repaired candidate, not
            // the original native count, otherwise the validated repair is discarded.
            int repairedCount = localCandidate.Count;
            var emittedVertices = new Dictionary<int, int>();
            result = new List<int>(repairedCount);
            for (int index = 0; index < repairedCount; index++)
            {
                int localVertex = localCandidate[index];
                if (!emittedVertices.TryGetValue(localVertex, out int globalVertex))
                {
                    int sourceVertex = localToGlobal[localVertex];
                    if ((localLocks[localVertex] & MeshoptSimplifyVertexFlags.Lock) != 0 ||
                        VertexPayloadUnchanged(
                            localVertex,
                            localPositions,
                            originalPositions,
                            localAttributes,
                            originalAttributes))
                    {
                        globalVertex = sourceVertex;
                    }
                    else
                    {
                        int attributeOffset = localVertex * kAttributeWeights.Length;
                        Vector3 normal = new Vector3(
                            localAttributes[attributeOffset + 2],
                            localAttributes[attributeOffset + 3],
                            localAttributes[attributeOffset + 4]);
                        if (normal.sqrMagnitude > 1e-12f)
                            normal.Normalize();
                        else
                            normal = geometry.normals[sourceVertex];
                        Vector3 tangentDirection = new Vector3(
                            localAttributes[attributeOffset + 5],
                            localAttributes[attributeOffset + 6],
                            localAttributes[attributeOffset + 7]);
                        if (tangentDirection.sqrMagnitude > 1e-12f)
                            tangentDirection.Normalize();
                        else
                        {
                            Vector4 sourceTangent = geometry.tangents[sourceVertex];
                            tangentDirection = new Vector3(sourceTangent.x, sourceTangent.y, sourceTangent.z);
                        }
                        float tangentW = localAttributes[attributeOffset + 8] < 0f ? -1f : 1f;
                        globalVertex = geometry.Count;
                        geometry.Append(
                            localPositions[localVertex],
                            normal,
                            new Vector2(
                                localAttributes[attributeOffset + 0],
                                localAttributes[attributeOffset + 1]),
                            new Vector4(tangentDirection.x, tangentDirection.y, tangentDirection.z, tangentW));
                    }
                    emittedVertices.Add(localVertex, globalVertex);
                }
                result.Add(globalVertex);
            }
            return true;
        }

        static void CoalescePersistedPositionClasses(
            Vector3[] localPositions,
            IReadOnlyList<int> localToGlobal,
            uint[] sourceQuantizedPositionRemap)
        {
            var representativeBySourcePosition = new Dictionary<uint, int>();
            for (int localVertex = 0; localVertex < localPositions.Length; localVertex++)
            {
                int globalVertex = localToGlobal[localVertex];
                uint sourcePosition = CanonicalPosition(globalVertex, sourceQuantizedPositionRemap);
                if (representativeBySourcePosition.TryGetValue(sourcePosition, out int representative))
                    localPositions[localVertex] = localPositions[representative];
                else
                    representativeBySourcePosition.Add(sourcePosition, localVertex);
            }
        }

        static float EstimateGeometricSimplificationError(
            Vector3[] vertices,
            IReadOnlyList<int> sourceIndices,
            byte[] locks,
            int targetIndexCount,
            bool sparse)
        {
            if (vertices == null || sourceIndices == null || sourceIndices.Count < 3)
                return 0f;

            uint[] source = ToUInt(sourceIndices);
            var destination = new uint[source.Length];
            byte[] vertexLocks = locks ?? new byte[vertices.Length];
            uint options = MeshoptSimplifyFlags.ErrorAbsolute | MeshoptSimplifyFlags.Permissive;
            if (sparse)
                options |= MeshoptSimplifyFlags.Sparse;

            MeshOptimizerNative.NativeSimplifyWithAttributes(
                destination,
                source,
                (UIntPtr)(uint)source.Length,
                vertices,
                (UIntPtr)(uint)vertices.Length,
                (UIntPtr)(uint)kVertexStride,
                Array.Empty<float>(),
                UIntPtr.Zero,
                Array.Empty<float>(),
                UIntPtr.Zero,
                vertexLocks,
                (UIntPtr)(uint)Mathf.Clamp(targetIndexCount, 3, source.Length),
                float.MaxValue,
                options,
                out float geometricError);

            if (!float.IsFinite(geometricError) || geometricError < 0f)
                throw new InvalidOperationException("meshoptimizer returned an invalid geometric LOD error.");
            return geometricError;
        }

        static bool PreservesLockedLocalPositions(
            IReadOnlyList<int> source,
            IReadOnlyList<int> localCandidate,
            IReadOnlyList<int> localToGlobal,
            byte[] localLocks,
            uint[] sourcePositionRemap)
        {
            var required = new HashSet<uint>();
            for (int localVertex = 0; localVertex < localToGlobal.Count; localVertex++)
            {
                if ((localLocks[localVertex] & MeshoptSimplifyVertexFlags.Lock) != 0)
                    required.Add(CanonicalPosition(localToGlobal[localVertex], sourcePositionRemap));
            }
            for (int index = 0; index < localCandidate.Count && required.Count > 0; index++)
            {
                int localVertex = localCandidate[index];
                required.Remove(CanonicalPosition(localToGlobal[localVertex], sourcePositionRemap));
            }
            return required.Count == 0;
        }

        static bool VertexPayloadUnchanged(
            int vertex,
            Vector3[] positions,
            Vector3[] originalPositions,
            float[] attributes,
            float[] originalAttributes)
        {
            if (positions[vertex] != originalPositions[vertex])
                return false;
            int offset = vertex * kAttributeWeights.Length;
            for (int attribute = 0; attribute < kAttributeWeights.Length; attribute++)
            {
                if (attributes[offset + attribute] != originalAttributes[offset + attribute])
                    return false;
            }
            return true;
        }

        static bool AllFinite(Vector3[] positions, float[] attributes)
        {
            for (int vertex = 0; vertex < positions.Length; vertex++)
            {
                Vector3 position = positions[vertex];
                if (!float.IsFinite(position.x) || !float.IsFinite(position.y) || !float.IsFinite(position.z))
                    return false;
            }
            for (int attribute = 0; attribute < attributes.Length; attribute++)
            {
                if (!float.IsFinite(attributes[attribute]))
                    return false;
            }
            return true;
        }

        static void RefreshBuildArrays(
            NaniteBuildGeometry geometry,
            ref Vector3[] vertices,
            ref Vector3[] normals,
            ref Vector2[] uvs,
            ref Vector4[] tangents,
            ref float[] attributes,
            ref uint[] remap,
            ref byte[] locks)
        {
            int oldLockCount = locks.Length;
            byte[] oldLocks = locks;
            vertices = geometry.positions.ToArray();
            normals = geometry.normals.ToArray();
            uvs = geometry.uvs.ToArray();
            tangents = geometry.tangents.ToArray();
            attributes = BuildSimplificationAttributes(vertices.Length, normals, uvs, tangents);
            remap = new uint[vertices.Length];
            MeshOptimizerNative.NativeGeneratePositionRemap(
                remap,
                vertices,
                (UIntPtr)(uint)vertices.Length,
                (UIntPtr)(uint)kVertexStride);
            byte[] seamFlags = BuildUvSeamProtectFlags(remap, attributes);
            locks = new byte[vertices.Length];
            Array.Copy(oldLocks, locks, Mathf.Min(oldLockCount, locks.Length));
            for (int vertex = 0; vertex < locks.Length; vertex++)
                locks[vertex] |= seamFlags[vertex];
        }

        static byte[] KeepOnlyBoundaryLocks(byte[] flags)
        {
            var result = new byte[flags.Length];
            for (int vertex = 0; vertex < flags.Length; vertex++)
                result[vertex] = (byte)(flags[vertex] & MeshoptSimplifyVertexFlags.Lock);
            return result;
        }

        static List<int> SimplifyWithStructuralGuard(
            Vector3[] vertices,
            float[] attributes,
            List<int> merged,
            byte[] locks,
            int targetIndexCount,
            uint[] positionRemap,
            uint[] quantizedPositionRemap,
            ref float error,
            ref int retryCount,
            ref int failureCount)
        {
            int sourceComponents = CountPositionComponents(merged, positionRemap);
            float sourceUvStretch = MaxUvEdgeStretch(merged, vertices, attributes);
            // Producer replacement is atomic, but a QEM result can still erase a
            // narrow bridge and split one panel into multiple islands. That result
            // looks like a transient hole at exactly one distance and then happens
            // to recover when the next producer is selected. Retry with progressively
            // more triangles instead of publishing the invalid intermediate mip.
            float[] ratios = { kSimplifyRatio, 0.625f, 0.75f, 0.8f };
            List<int> candidate = null;
            float candidateError = 0f;
            for (int attempt = 0; attempt < ratios.Length; attempt++)
            {
                int attemptTarget = attempt == 0
                    ? targetIndexCount
                    : Mathf.Max(3, Mathf.FloorToInt(merged.Count / 3f * ratios[attempt]) * 3);
                candidateError = 0f;
                candidate = Simplify(
                    vertices,
                    attributes,
                    merged,
                    locks,
                    attemptTarget,
                    ref candidateError);
                if (ReplacementPreservesStructure(
                        merged,
                        candidate,
                        vertices,
                        attributes,
                        locks,
                        positionRemap,
                        quantizedPositionRemap,
                        sourceComponents,
                        sourceUvStretch))
                {
                    if (attempt > 0)
                        retryCount += attempt;
                    float geometricError = EstimateGeometricSimplificationError(
                        vertices,
                        merged,
                        locks,
                        candidate.Count,
                        true);
                    // The camera formula consumes a geometric distance. Attribute
                    // error remains part of candidate generation and validation.
                    error = geometricError;
                    return candidate;
                }
            }

            retryCount += ratios.Length - 1;
            failureCount++;
            error = 0f;
            return new List<int>(merged);
        }

        static bool ReplacementPreservesStructure(
            IReadOnlyList<int> source,
            List<int> candidate,
            Vector3[] vertices,
            float[] attributes,
            byte[] locks,
            uint[] positionRemap,
            uint[] quantizedPositionRemap,
            int sourceComponents,
            float sourceUvStretch)
        {
            if (candidate == null || candidate.Count < 3 || candidate.Count % 3 != 0)
                return false;
            if (CountPositionComponents(candidate, positionRemap) > sourceComponents)
                return false;
            if (!RepairSourceComponentCoverage(
                    source,
                    candidate,
                    null,
                    positionRemap,
                    out _,
                    out _,
                    out _))
                return false;
            // Coverage repair appends source triangles. They must reconnect to the
            // retained island instead of creating a second island inside the same
            // source component; validate the final published topology, not only the
            // pre-repair simplifier result.
            if (CountPositionComponents(candidate, positionRemap) != sourceComponents)
                return false;
            int quantizedSourceComponents = CountPositionComponents(source, quantizedPositionRemap);
            if (CountPositionComponents(candidate, quantizedPositionRemap) != quantizedSourceComponents)
                return false;
            if (!PreservesLockedPositions(source, candidate, locks, positionRemap))
                return false;
            float candidateUvStretch = MaxUvEdgeStretch(candidate, vertices, attributes);
            return candidateUvStretch <= Mathf.Max(32f, sourceUvStretch * 4f);
        }

        static bool RepairSourceComponentCoverage(
            IReadOnlyList<int> sourceGlobalIndices,
            List<int> candidateIndices,
            IReadOnlyList<int> candidateLocalToGlobal,
            uint[] positionRemap,
            out int missingComponents,
            out int underRepresentedClosedComponents,
            out HashSet<int> restoredCandidateVertices)
        {
            missingComponents = 0;
            underRepresentedClosedComponents = 0;
            restoredCandidateVertices = new HashSet<int>();
            if (sourceGlobalIndices == null || sourceGlobalIndices.Count < 3 ||
                candidateIndices == null || candidateIndices.Count < 3)
                return false;

            var parents = new Dictionary<uint, uint>();
            for (int index = 0; index + 2 < sourceGlobalIndices.Count; index += 3)
            {
                uint a = CanonicalPosition(sourceGlobalIndices[index + 0], positionRemap);
                uint b = CanonicalPosition(sourceGlobalIndices[index + 1], positionRemap);
                uint c = CanonicalPosition(sourceGlobalIndices[index + 2], positionRemap);
                EnsureSet(parents, a);
                EnsureSet(parents, b);
                EnsureSet(parents, c);
                Union(parents, a, b);
                Union(parents, b, c);
                Union(parents, c, a);
            }

            var sourceTriangles = new Dictionary<uint, int>();
            var edgeUse = new Dictionary<ulong, int>();
            for (int index = 0; index + 2 < sourceGlobalIndices.Count; index += 3)
            {
                uint a = CanonicalPosition(sourceGlobalIndices[index + 0], positionRemap);
                uint b = CanonicalPosition(sourceGlobalIndices[index + 1], positionRemap);
                uint c = CanonicalPosition(sourceGlobalIndices[index + 2], positionRemap);
                uint root = Find(parents, a);
                sourceTriangles.TryGetValue(root, out int triangleCount);
                sourceTriangles[root] = triangleCount + 1;
                IncrementEdgeUse(edgeUse, a, b);
                IncrementEdgeUse(edgeUse, b, c);
                IncrementEdgeUse(edgeUse, c, a);
            }

            var closed = new Dictionary<uint, bool>();
            foreach (uint root in sourceTriangles.Keys)
                closed[root] = true;
            foreach (var edge in edgeUse)
            {
                uint endpoint = unchecked((uint)(edge.Key >> 32));
                uint root = Find(parents, endpoint);
                if (edge.Value != 2)
                    closed[root] = false;
            }

            var candidateTriangles = new Dictionary<uint, int>();
            for (int index = 0; index + 2 < candidateIndices.Count; index += 3)
            {
                var roots = new HashSet<uint>();
                for (int corner = 0; corner < 3; corner++)
                {
                    int candidateVertex = candidateIndices[index + corner];
                    int globalVertex = candidateLocalToGlobal != null
                        ? ((uint)candidateVertex < (uint)candidateLocalToGlobal.Count
                            ? candidateLocalToGlobal[candidateVertex]
                            : -1)
                        : candidateVertex;
                    uint canonical = CanonicalPosition(globalVertex, positionRemap);
                    if (parents.ContainsKey(canonical))
                        roots.Add(Find(parents, canonical));
                }
                // A permissive collapse may create a triangle whose corners came
                // from different disconnected source shells. Counting that triangle
                // once for every shell hid an actual component merge from the guard.
                if (roots.Count != 1)
                    return false;
                foreach (uint root in roots)
                {
                    candidateTriangles.TryGetValue(root, out int triangleCount);
                    candidateTriangles[root] = triangleCount + 1;
                }
            }

            foreach (var component in sourceTriangles)
            {
                candidateTriangles.TryGetValue(component.Key, out int retainedTriangles);
                if (retainedTriangles == 0)
                {
                    missingComponents++;
                    continue;
                }
                int requiredTriangles = closed[component.Key]
                    ? Mathf.Min(4, component.Value)
                    : 1;
                if (retainedTriangles < requiredTriangles)
                    underRepresentedClosedComponents++;
            }
            if (missingComponents == 0 && underRepresentedClosedComponents == 0)
                return true;

            Dictionary<int, int> globalToCandidate = null;
            if (candidateLocalToGlobal != null)
            {
                globalToCandidate = new Dictionary<int, int>(candidateLocalToGlobal.Count);
                for (int localVertex = 0; localVertex < candidateLocalToGlobal.Count; localVertex++)
                    globalToCandidate[candidateLocalToGlobal[localVertex]] = localVertex;
            }

            foreach (var component in sourceTriangles)
            {
                candidateTriangles.TryGetValue(component.Key, out int retainedTriangles);
                int requiredTriangles = closed[component.Key]
                    ? Mathf.Min(4, component.Value)
                    : 1;
                if (retainedTriangles >= requiredTriangles)
                    continue;

                for (int index = 0;
                     index + 2 < sourceGlobalIndices.Count && retainedTriangles < requiredTriangles;
                     index += 3)
                {
                    int globalA = sourceGlobalIndices[index + 0];
                    if (Find(parents, CanonicalPosition(globalA, positionRemap)) != component.Key)
                        continue;
                    int globalB = sourceGlobalIndices[index + 1];
                    int globalC = sourceGlobalIndices[index + 2];
                    if (globalToCandidate != null)
                    {
                        if (!globalToCandidate.TryGetValue(globalA, out int localA) ||
                            !globalToCandidate.TryGetValue(globalB, out int localB) ||
                            !globalToCandidate.TryGetValue(globalC, out int localC))
                            return false;
                        candidateIndices.Add(localA);
                        candidateIndices.Add(localB);
                        candidateIndices.Add(localC);
                        restoredCandidateVertices.Add(localA);
                        restoredCandidateVertices.Add(localB);
                        restoredCandidateVertices.Add(localC);
                    }
                    else
                    {
                        candidateIndices.Add(globalA);
                        candidateIndices.Add(globalB);
                        candidateIndices.Add(globalC);
                    }
                    retainedTriangles++;
                }
                if (retainedTriangles < requiredTriangles)
                    return false;
            }

            missingComponents = 0;
            underRepresentedClosedComponents = 0;
            return true;
        }

        static void IncrementEdgeUse(Dictionary<ulong, int> edgeUse, uint a, uint b)
        {
            uint minimum = Math.Min(a, b);
            uint maximum = Math.Max(a, b);
            ulong key = ((ulong)minimum << 32) | maximum;
            edgeUse.TryGetValue(key, out int count);
            edgeUse[key] = count + 1;
        }

        static bool PreservesLockedPositions(
            IReadOnlyList<int> source,
            IReadOnlyList<int> candidate,
            byte[] locks,
            uint[] positionRemap)
        {
            var required = new HashSet<uint>();
            for (int index = 0; index < source.Count; index++)
            {
                int vertex = source[index];
                if ((uint)vertex < (uint)locks.Length &&
                    (locks[vertex] & MeshoptSimplifyVertexFlags.Lock) != 0)
                    required.Add(CanonicalPosition(vertex, positionRemap));
            }
            for (int index = 0; index < candidate.Count && required.Count > 0; index++)
                required.Remove(CanonicalPosition(candidate[index], positionRemap));
            return required.Count == 0;
        }

        static int CountPositionComponents(IReadOnlyList<int> indices, uint[] positionRemap)
        {
            var parents = new Dictionary<uint, uint>();
            for (int index = 0; index + 2 < indices.Count; index += 3)
            {
                uint a = CanonicalPosition(indices[index + 0], positionRemap);
                uint b = CanonicalPosition(indices[index + 1], positionRemap);
                uint c = CanonicalPosition(indices[index + 2], positionRemap);
                // A triangle collapsed to one position contributes no edge and is
                // ignored by the persisted hierarchy audit. Counting its lone point
                // as a component here can hide a real split in the non-degenerate
                // replacement topology.
                if (a == b && b == c)
                    continue;
                EnsureSet(parents, a);
                EnsureSet(parents, b);
                EnsureSet(parents, c);
                Union(parents, a, b);
                Union(parents, b, c);
                Union(parents, c, a);
            }

            var roots = new HashSet<uint>();
            var vertices = new List<uint>(parents.Keys);
            for (int vertexIndex = 0; vertexIndex < vertices.Count; vertexIndex++)
                roots.Add(Find(parents, vertices[vertexIndex]));
            return roots.Count;
        }

        static uint[] BuildQuantizedPositionRemap(Vector3[] vertices)
        {
            var minimum = vertices[0];
            var maximum = vertices[0];
            for (int i = 1; i < vertices.Length; i++)
            {
                minimum = Vector3.Min(minimum, vertices[i]);
                maximum = Vector3.Max(maximum, vertices[i]);
            }
            Vector3 extent = maximum - minimum;
            var quantized = new Vector3[vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3 value = vertices[i];
                quantized[i] = new Vector3(
                    QuantizePositionComponent(value.x, minimum.x, extent.x),
                    QuantizePositionComponent(value.y, minimum.y, extent.y),
                    QuantizePositionComponent(value.z, minimum.z, extent.z));
            }
            var remap = new uint[vertices.Length];
            MeshOptimizerNative.NativeGeneratePositionRemap(
                remap,
                quantized,
                (UIntPtr)(uint)quantized.Length,
                (UIntPtr)(uint)kVertexStride);
            return remap;
        }

        static float QuantizePositionComponent(float value, float minimum, float extent) =>
            extent <= 1e-20f
                ? 0f
                : Mathf.RoundToInt(Mathf.Clamp01((value - minimum) / extent) * ushort.MaxValue);

        static uint CanonicalPosition(int vertexIndex, uint[] positionRemap) =>
            (uint)vertexIndex < (uint)positionRemap.Length
                ? positionRemap[vertexIndex]
                : unchecked((uint)vertexIndex);

        static void EnsureSet(Dictionary<uint, uint> parents, uint vertex)
        {
            if (!parents.ContainsKey(vertex))
                parents.Add(vertex, vertex);
        }

        static uint Find(Dictionary<uint, uint> parents, uint vertex)
        {
            uint root = vertex;
            while (parents[root] != root)
                root = parents[root];
            while (parents[vertex] != vertex)
            {
                uint next = parents[vertex];
                parents[vertex] = root;
                vertex = next;
            }
            return root;
        }

        static void Union(Dictionary<uint, uint> parents, uint a, uint b)
        {
            uint rootA = Find(parents, a);
            uint rootB = Find(parents, b);
            if (rootA != rootB)
                parents[rootB] = rootA;
        }

        static float RobustUvEdgeStretch(
            IReadOnlyList<int> indices,
            Vector3[] vertices,
            float[] attributes)
        {
            if (attributes == null || attributes.Length < vertices.Length * kAttributeWeights.Length)
                return 0f;
            // Imported hard-surface meshes commonly contain a few zero-length
            // position edges across distinct UV wedges. A maximum UV/length ratio
            // lets one such edge reject an otherwise valid multi-thousand-triangle
            // producer forever. Use a high percentile to catch widespread atlas
            // stretch while leaving isolated degenerate wedges to the attribute
            // quadric error that controls the actual transition distance.
            var samples = new List<float>(Mathf.Min(indices.Count, 32768));
            for (int index = 0; index + 2 < indices.Count; index += 3)
            {
                int a = indices[index + 0];
                int b = indices[index + 1];
                int c = indices[index + 2];
                AddUvStretchSample(a, b, vertices, attributes, samples);
                AddUvStretchSample(b, c, vertices, attributes, samples);
                AddUvStretchSample(c, a, vertices, attributes, samples);
            }
            if (samples.Count == 0)
                return 0f;
            samples.Sort();
            int percentileIndex = Mathf.Clamp(
                Mathf.CeilToInt((samples.Count - 1) * 0.995f),
                0,
                samples.Count - 1);
            return samples[percentileIndex];
        }

        static float MaxUvEdgeStretch(
            IReadOnlyList<int> indices,
            Vector3[] vertices,
            float[] attributes)
        {
            if (attributes == null || attributes.Length < vertices.Length * kAttributeWeights.Length)
                return 0f;
            float maximum = 0f;
            for (int index = 0; index + 2 < indices.Count; index += 3)
            {
                maximum = Mathf.Max(maximum, UvEdgeStretch(indices[index + 0], indices[index + 1], vertices, attributes));
                maximum = Mathf.Max(maximum, UvEdgeStretch(indices[index + 1], indices[index + 2], vertices, attributes));
                maximum = Mathf.Max(maximum, UvEdgeStretch(indices[index + 2], indices[index + 0], vertices, attributes));
            }
            return maximum;
        }

        static void AddUvStretchSample(
            int a,
            int b,
            Vector3[] vertices,
            float[] attributes,
            List<float> samples)
        {
            float stretch = UvEdgeStretch(a, b, vertices, attributes);
            if (float.IsFinite(stretch))
                samples.Add(stretch);
        }

        static float UvEdgeStretch(
            int a,
            int b,
            Vector3[] vertices,
            float[] attributes)
        {
            if ((uint)a >= (uint)vertices.Length || (uint)b >= (uint)vertices.Length)
                return float.MaxValue;
            int attributeA = a * kAttributeWeights.Length;
            int attributeB = b * kAttributeWeights.Length;
            var uvA = new Vector2(attributes[attributeA], attributes[attributeA + 1]);
            var uvB = new Vector2(attributes[attributeB], attributes[attributeB + 1]);
            return Vector2.Distance(uvA, uvB) /
                   Mathf.Max(1e-6f, Vector3.Distance(vertices[a], vertices[b]));
        }

        static float[] BuildGroupAttributeWeights(
            Vector3[] vertices,
            float[] attributes,
            IReadOnlyList<int> indices)
        {
            var weights = (float[])kAttributeWeights.Clone();
            if (vertices == null || attributes == null ||
                attributes.Length < vertices.Length * kAttributeWeights.Length ||
                indices == null || indices.Count < 3)
            {
                return weights;
            }

            // Translate a UV delta into the same object-space units as the
            // position quadric. A zero UV weight reports a tiny geometric error
            // for a replacement that can still slide a high-frequency atlas by
            // many screen pixels. The median edge ratio is robust to UV seams,
            // tiled islands and the occasional near-zero UV edge.
            var objectUnitsPerUv = new List<float>(Mathf.Min(indices.Count, 6144));
            int triangleStep = Mathf.Max(3, (indices.Count / 6144) * 3);
            for (int triangle = 0; triangle + 2 < indices.Count; triangle += triangleStep)
            {
                AddUvScaleSample(indices[triangle], indices[triangle + 1], vertices, attributes, objectUnitsPerUv);
                AddUvScaleSample(indices[triangle + 1], indices[triangle + 2], vertices, attributes, objectUnitsPerUv);
                AddUvScaleSample(indices[triangle + 2], indices[triangle], vertices, attributes, objectUnitsPerUv);
            }
            if (objectUnitsPerUv.Count == 0)
                return weights;

            objectUnitsPerUv.Sort();
            float median = objectUnitsPerUv[objectUnitsPerUv.Count / 2];
            float uvWeight = Mathf.Clamp(median, 1e-4f, 8f);
            weights[0] = uvWeight;
            weights[1] = uvWeight;
            return weights;
        }

        static void AddUvScaleSample(
            int a,
            int b,
            Vector3[] vertices,
            float[] attributes,
            List<float> samples)
        {
            if ((uint)a >= (uint)vertices.Length || (uint)b >= (uint)vertices.Length || a == b)
                return;
            float objectLength = Vector3.Distance(vertices[a], vertices[b]);
            int attributeA = a * kAttributeWeights.Length;
            int attributeB = b * kAttributeWeights.Length;
            float u = attributes[attributeA + 0] - attributes[attributeB + 0];
            float v = attributes[attributeA + 1] - attributes[attributeB + 1];
            float uvLength = Mathf.Sqrt(u * u + v * v);
            // Large cross-island edges do not describe local texel density.
            if (objectLength <= 1e-7f || uvLength <= 1e-7f || uvLength > 2f)
                return;
            float ratio = objectLength / uvLength;
            if (!float.IsNaN(ratio) && !float.IsInfinity(ratio))
                samples.Add(Mathf.Clamp(ratio, 1e-4f, 8f));
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

        static byte[] BuildUvSeamProtectFlags(uint[] positionRemap, float[] attributes)
        {
            int vertexCount = positionRemap.Length;
            var result = new byte[vertexCount];
            int attributeCount = kAttributeWeights.Length;

            // This follows meshoptimizer's clusterlod reference implementation:
            // permissive simplification protects selected discontinuities only.
            // UV seams affect material addressing and must survive. Normal and
            // tangent discontinuities are already part of the attribute QEM; marking
            // them Protect as well over-constrains hard-surface assets and makes a
            // large fraction of fine clusters terminal.
            //
            // Protect only the non-canonical wedge. lockBoundary later propagates the
            // dynamic Lock bit by position but deliberately retains Protect per wedge,
            // exactly like clusterlod::lockBoundary. Protecting the representative too
            // infects every wedge at that position and is substantially more restrictive.
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
                if (!PackedUvDiffers(attributes, attributeBase, representativeBase))
                    continue;

                result[vertexIndex] |= MeshoptSimplifyVertexFlags.Protect;
            }

            return result;
        }

        static bool PackedUvDiffers(float[] attributes, int a, int b)
        {
            // Match clusterlod's attribute_protect_mask semantics exactly. Pages that
            // exceed the half-UV error budget are stored as FloatUV, therefore comparing
            // half representations here can erase a real seam before the Page format is
            // selected. Signed zero is intentionally treated as the same UV value.
            return attributes[a + 0] != attributes[b + 0] ||
                   attributes[a + 1] != attributes[b + 1];
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
