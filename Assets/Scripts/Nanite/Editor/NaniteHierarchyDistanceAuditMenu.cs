#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Nanite.Editor
{
    /// <summary>
    /// Validates the baked producer-group DAG without rendering it.  The distance
    /// sweep deliberately mirrors the GPU hierarchy traversal, while the topology
    /// audit compares every fine/coarse replacement in object space.
    /// </summary>
    public static class NaniteHierarchyDistanceAuditMenu
    {
        const float kMaxFiniteError = 1e20f;

        struct VertexKey : IEquatable<VertexKey>, IComparable<VertexKey>
        {
            public int x;
            public int y;
            public int z;

            public bool Equals(VertexKey other) => x == other.x && y == other.y && z == other.z;
            public override bool Equals(object obj) => obj is VertexKey other && Equals(other);
            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = x;
                    hash = hash * 397 ^ y;
                    return hash * 397 ^ z;
                }
            }

            public int CompareTo(VertexKey other)
            {
                int c = x.CompareTo(other.x);
                if (c != 0) return c;
                c = y.CompareTo(other.y);
                return c != 0 ? c : z.CompareTo(other.z);
            }
        }

        struct EdgeKey : IEquatable<EdgeKey>
        {
            public VertexKey a;
            public VertexKey b;

            public EdgeKey(VertexKey v0, VertexKey v1)
            {
                if (v0.CompareTo(v1) <= 0)
                {
                    a = v0;
                    b = v1;
                }
                else
                {
                    a = v1;
                    b = v0;
                }
            }

            public bool Equals(EdgeKey other) => a.Equals(other.a) && b.Equals(other.b);
            public override bool Equals(object obj) => obj is EdgeKey other && Equals(other);
            public override int GetHashCode()
            {
                unchecked { return a.GetHashCode() * 397 ^ b.GetHashCode(); }
            }
        }

        sealed class TopologyStats
        {
            public int triangles;
            public int degenerateTriangles;
            public int nonManifoldEdges;
            public int components;
            public float maxUvStretch;
            // Must match NaniteMeshBuilder.RobustUvEdgeStretch. The maximum is
            // retained for diagnosis only: one degenerate UV wedge must not make
            // the audit reject a producer that the bake intentionally accepts.
            public float robustUvStretch995;
            public readonly HashSet<EdgeKey> boundary = new HashSet<EdgeKey>();
        }

        sealed class CutStats
        {
            public int clusters;
            public long triangles;
            public int duplicateClusters;
            public readonly SortedDictionary<int, int> mipClusters = new SortedDictionary<int, int>();
            public readonly HashSet<int> selectedGeometry = new HashSet<int>();
            public readonly HashSet<int> refinedGroups = new HashSet<int>();
            public readonly HashSet<int> coarseGroups = new HashSet<int>();
        }

        [MenuItem("Nanite/Diagnostics/Audit Hierarchy Distance + Topology (Selected NaniteMesh)")]
        static void AuditSelected()
        {
            NaniteMesh mesh = Selection.activeObject as NaniteMesh;
            if (mesh == null)
            {
                Debug.LogWarning("[Nanite][HierarchyDistanceAudit] Select a NaniteMesh asset first.");
                return;
            }
            Debug.Log(AuditMesh(mesh));
        }

        public static string AuditMesh(NaniteMesh mesh)
        {
            if (mesh == null)
                throw new ArgumentNullException(nameof(mesh));
            if (mesh.hierarchyGroups == null || mesh.hierarchyClusterRefs == null ||
                mesh.hierarchyRootGroups == null)
                throw new InvalidOperationException("Hierarchy arrays are missing.");

            var sb = new StringBuilder(16384);
            sb.AppendLine(
                $"[Nanite][HierarchyDistanceAudit] mesh={mesh.name}, version={mesh.hierarchyVersion}, " +
                $"groups={mesh.hierarchyGroups.Length}, refs={mesh.hierarchyClusterRefs.Length}, " +
                $"roots={mesh.hierarchyRootGroups.Length}");

            AuditGroupContracts(mesh, sb);
            AuditRootSets(mesh, sb);
            AuditDistanceSweep(mesh, sb, 2160, 60f, 1f);
            return sb.ToString();
        }

        static void AuditRootSets(NaniteMesh mesh, StringBuilder sb)
        {
            sb.AppendLine("--- root sets ---");
            long totalTriangles = 0;
            for (int rootIndex = 0; rootIndex < mesh.hierarchyRootGroups.Length; rootIndex++)
            {
                int groupIndex = mesh.hierarchyRootGroups[rootIndex];
                if ((uint)groupIndex >= (uint)mesh.hierarchyGroups.Length)
                    continue;
                NaniteHierarchyGroup group = mesh.hierarchyGroups[groupIndex];
                int triangles = CountTriangles(mesh, group.fineClusterStart, group.fineClusterCount);
                totalTriangles += triangles;
                sb.AppendLine(
                    $"  root[{rootIndex}] g{groupIndex}/m{group.mipLevel}: " +
                    $"clusters={group.fineClusterCount}, triangles={triangles}, " +
                    $"radius={F(group.boundingSphere.w)}, disappearError={F(group.maxParentLodError)}");
            }
            sb.AppendLine($"rootTriangles={totalTriangles}");
        }

        static int CountTriangles(NaniteMesh mesh, int start, int count)
        {
            int triangles = 0;
            int end = Mathf.Min(mesh.hierarchyClusterRefs.Length, start + count);
            for (int refIndex = Mathf.Max(0, start); refIndex < end; refIndex++)
            {
                if (TryGetCluster(
                        mesh,
                        mesh.hierarchyClusterRefs[refIndex],
                        out _,
                        out NaniteCluster cluster))
                    triangles += cluster.indiceCount / 3;
            }
            return triangles;
        }

        static void AuditGroupContracts(NaniteMesh mesh, StringBuilder sb)
        {
            int invalidRanges = 0;
            int nonReducing = 0;
            int boundaryMismatch = 0;
            int nonManifold = 0;
            int componentGrowth = 0;
            int authorizedTerminalComponentGrowth = 0;
            int componentLoss = 0;
            int uvStretchOutlier = 0;
            long fineTriangles = 0;
            long coarseTriangles = 0;
            var worst = new List<string>();
            var componentGrowthGroups = new List<int>();
            var authorizedGrowthGroups = new List<int>();
            var componentLossGroups = new List<int>();
            var uvStretchGroups = new List<int>();
            var structuralOutliers = new List<string>();

            for (int groupIndex = 0; groupIndex < mesh.hierarchyGroups.Length; groupIndex++)
            {
                NaniteHierarchyGroup group = mesh.hierarchyGroups[groupIndex];
                if (!ValidRange(group.fineClusterStart, group.fineClusterCount, mesh.hierarchyClusterRefs.Length) ||
                    !ValidRange(group.coarseClusterStart, group.coarseClusterCount, mesh.hierarchyClusterRefs.Length))
                {
                    invalidRanges++;
                    continue;
                }
                if (group.coarseClusterCount == 0)
                    continue;

                TopologyStats fine = GatherTopology(
                    mesh,
                    group.fineClusterStart,
                    group.fineClusterCount);
                TopologyStats coarse = GatherTopology(
                    mesh,
                    group.coarseClusterStart,
                    group.coarseClusterCount);
                fineTriangles += fine.triangles;
                coarseTriangles += coarse.triangles;

                if (coarse.triangles >= fine.triangles)
                    nonReducing++;

                int missingBoundary = 0;
                int addedBoundary = 0;
                foreach (EdgeKey edge in fine.boundary)
                    if (!coarse.boundary.Contains(edge)) missingBoundary++;
                foreach (EdgeKey edge in coarse.boundary)
                    if (!fine.boundary.Contains(edge)) addedBoundary++;
                if (missingBoundary != 0 || addedBoundary != 0)
                {
                    boundaryMismatch++;
                    if (worst.Count < 24)
                    {
                        worst.Add(
                            $"g{groupIndex}/m{group.mipLevel}: tri={fine.triangles}->{coarse.triangles}, " +
                            $"boundary=-{missingBoundary}/+{addedBoundary}, " +
                            $"components={fine.components}->{coarse.components}, " +
                            $"uvP995={F(fine.robustUvStretch995)}->{F(coarse.robustUvStretch995)}, " +
                            $"uvMax={F(fine.maxUvStretch)}->{F(coarse.maxUvStretch)}, " +
                            $"error={F(group.maxParentLodError)}");
                    }
                }
                if (fine.nonManifoldEdges != 0 || coarse.nonManifoldEdges != 0)
                    nonManifold++;
                if (coarse.components > fine.components && group.UsesRobustUvGate)
                {
                    authorizedTerminalComponentGrowth++;
                    authorizedGrowthGroups.Add(groupIndex);
                    structuralOutliers.Add(
                        $"g{groupIndex}/m{group.mipLevel}: authorized-components={fine.components}->{coarse.components}, " +
                        $"tri={fine.triangles}->{coarse.triangles}, error={F(group.maxParentLodError)}");
                }
                else if (coarse.components > fine.components)
                {
                    componentGrowth++;
                    componentGrowthGroups.Add(groupIndex);
                    structuralOutliers.Add(
                        $"g{groupIndex}/m{group.mipLevel}: components={fine.components}->{coarse.components}, " +
                        $"tri={fine.triangles}->{coarse.triangles}, error={F(group.maxParentLodError)}");
                }
                if (coarse.components < fine.components)
                {
                    componentLoss++;
                    componentLossGroups.Add(groupIndex);
                    structuralOutliers.Add(
                        $"g{groupIndex}/m{group.mipLevel}: component-loss={fine.components}->{coarse.components}, " +
                        $"tri={fine.triangles}->{coarse.triangles}, error={F(group.maxParentLodError)}");
                }
                float fineUvGate = group.UsesRobustUvGate
                    ? fine.robustUvStretch995
                    : fine.maxUvStretch;
                float coarseUvGate = group.UsesRobustUvGate
                    ? coarse.robustUvStretch995
                    : coarse.maxUvStretch;
                if (coarseUvGate > Mathf.Max(32f, fineUvGate * 4f))
                {
                    uvStretchOutlier++;
                    uvStretchGroups.Add(groupIndex);
                    structuralOutliers.Add(
                        $"g{groupIndex}/m{group.mipLevel}: " +
                        $"uvP995={F(fine.robustUvStretch995)}->{F(coarse.robustUvStretch995)}, " +
                        $"uvMax={F(fine.maxUvStretch)}->{F(coarse.maxUvStretch)}, " +
                        $"gate={(group.UsesRobustUvGate ? "p995" : "max")}, " +
                        $"tri={fine.triangles}->{coarse.triangles}, error={F(group.maxParentLodError)}");
                }
            }

            sb.AppendLine("--- producer replacement contracts ---");
            sb.AppendLine(
                $"invalidRanges={invalidRanges}, nonReducing={nonReducing}, " +
                $"boundaryMismatch={boundaryMismatch}, nonManifold={nonManifold}, " +
                $"componentGrowth={componentGrowth}, terminalComponentGrowth={authorizedTerminalComponentGrowth}, " +
                $"componentLoss={componentLoss}, " +
                $"uvStretchOutlier={uvStretchOutlier}, " +
                $"triangles={fineTriangles}->{coarseTriangles}");
            sb.AppendLine($"componentGrowthGroups={Join(componentGrowthGroups)}");
            sb.AppendLine($"terminalComponentGrowthGroups={Join(authorizedGrowthGroups)}");
            sb.AppendLine($"componentLossGroups={Join(componentLossGroups)}");
            sb.AppendLine($"uvStretchOutlierGroups={Join(uvStretchGroups)}");
            for (int i = 0; i < structuralOutliers.Count; i++)
                sb.AppendLine("  structural: " + structuralOutliers[i]);
            for (int i = 0; i < worst.Count; i++)
                sb.AppendLine("  " + worst[i]);
        }

        static void AuditDistanceSweep(
            NaniteMesh mesh,
            StringBuilder sb,
            int screenHeight,
            float verticalFovDegrees,
            float thresholdPixels)
        {
            float radius = Mathf.Max(1e-4f, mesh.boundingSphere.w);
            Vector3 center = new Vector3(
                mesh.boundingSphere.x,
                mesh.boundingSphere.y,
                mesh.boundingSphere.z);
            float projectionScale = screenHeight * 0.5f /
                                    Mathf.Tan(verticalFovDegrees * Mathf.Deg2Rad * 0.5f);
            float[] surfaceDistances =
            {
                radius * 0.02f, radius * 0.05f, radius * 0.1f, radius * 0.2f,
                radius * 0.5f, radius, radius * 2f, radius * 4f,
                radius * 8f, radius * 16f, radius * 32f, radius * 64f,
                radius * 128f, radius * 256f, radius * 512f
            };
            Vector3[] directions =
            {
                Vector3.forward, Vector3.back, Vector3.right, Vector3.left, Vector3.up
            };
            int[] fineTriangleCounts = BuildFineTriangleCounts(mesh);
            float[] fineSurfaceArea = BuildFineSurfaceArea(mesh);

            sb.AppendLine("--- distance sweep (all geometry, no frustum/HZB) ---");
            sb.AppendLine(
                $"screen={screenHeight}p, fovY={F(verticalFovDegrees)}, threshold={F(thresholdPixels)}px; " +
                "uses meshoptimizer error*projectionScale/distance (the production GPU formula).");

            for (int directionIndex = 0; directionIndex < directions.Length; directionIndex++)
            {
                Vector3 direction = directions[directionIndex];
                long previousReferenceTriangles = long.MaxValue;
                CutStats previousReference = null;
                int referenceRegressions = 0;
                sb.AppendLine($"direction={DirectionName(direction)}");
                for (int distanceIndex = 0; distanceIndex < surfaceDistances.Length; distanceIndex++)
                {
                    float surfaceDistance = surfaceDistances[distanceIndex];
                    Vector3 camera = center + direction * (radius + surfaceDistance);
                    CutStats reference = SelectCut(
                        mesh,
                        camera,
                        projectionScale,
                        thresholdPixels,
                        1f,
                        fineTriangleCounts,
                        fineSurfaceArea,
                        false);
                    CutStats density = SelectCut(
                        mesh,
                        camera,
                        projectionScale,
                        thresholdPixels,
                        1f,
                        fineTriangleCounts,
                        fineSurfaceArea,
                        true);
                    if (reference.triangles > previousReferenceTriangles)
                    {
                        referenceRegressions++;
                        AppendRegressionDetails(
                            mesh,
                            sb,
                            previousReference,
                            reference,
                            previousReferenceTriangles,
                            surfaceDistance);
                    }
                    previousReferenceTriangles = reference.triangles;
                    previousReference = reference;
                    sb.AppendLine(
                        $"  surface={surfaceDistance,8:0.###} ({surfaceDistance / radius,6:0.##}R) " +
                        $"error={reference.clusters,5}cl/{reference.triangles,7}tri {MipText(reference)} " +
                        $"density={density.clusters,5}cl/{density.triangles,7}tri {MipText(density)} " +
                        $"dup={reference.duplicateClusters}/{density.duplicateClusters}");
                }
                sb.AppendLine($"  monotonic regressions: {referenceRegressions}");
            }
        }

        static CutStats SelectCut(
            NaniteMesh mesh,
            Vector3 camera,
            float projectionScale,
            float thresholdPixels,
            float projectionMultiplier,
            int[] fineTriangleCounts,
            float[] fineSurfaceArea,
            bool densityBudget)
        {
            var result = new CutStats();
            var selectedGeometry = new HashSet<int>();
            // clusterlod produces a DAG because newly generated clusters are
            // regrouped at every depth. It must not be traversed as a tree from
            // the root set: one producer group's coarse clusters can belong to
            // several later consumer groups. The official visibility contract is
            // evaluated for every cluster in its consumer group:
            //   consumer.simplified > threshold &&
            //   (producer == none || producer.simplified <= threshold)
            // This is also the self/parent predicate used by the direct GPU cut.
            for (int groupIndex = 0; groupIndex < mesh.hierarchyGroups.Length; groupIndex++)
            {
                NaniteHierarchyGroup consumer = mesh.hierarchyGroups[groupIndex];
                if (!ValidRange(
                        consumer.fineClusterStart,
                        consumer.fineClusterCount,
                        mesh.hierarchyClusterRefs.Length))
                    continue;

                bool consumerVisible = GroupNeedsRefinement(
                    mesh,
                    consumer,
                    groupIndex,
                    camera,
                    projectionScale,
                    projectionMultiplier,
                    thresholdPixels,
                    fineTriangleCounts,
                    fineSurfaceArea,
                    densityBudget);
                if (!consumerVisible)
                    continue;
                result.refinedGroups.Add(groupIndex);

                int end = consumer.fineClusterStart + consumer.fineClusterCount;
                for (int refIndex = consumer.fineClusterStart; refIndex < end; refIndex++)
                {
                    NaniteHierarchyClusterRef clusterRef = mesh.hierarchyClusterRefs[refIndex];
                    int producerIndex = clusterRef.refinementGroupIndex;
                    bool producerReady = (uint)producerIndex >= (uint)mesh.hierarchyGroups.Length ||
                                         !GroupNeedsRefinement(
                                             mesh,
                                             mesh.hierarchyGroups[producerIndex],
                                             producerIndex,
                                             camera,
                                             projectionScale,
                                             projectionMultiplier,
                                             thresholdPixels,
                                             fineTriangleCounts,
                                             fineSurfaceArea,
                                             densityBudget);
                    if (producerReady)
                        AddSelected(mesh, clusterRef, selectedGeometry, result);
                }
            }
            return result;
        }

        static bool GroupNeedsRefinement(
            NaniteMesh mesh,
            NaniteHierarchyGroup group,
            int groupIndex,
            Vector3 camera,
            float projectionScale,
            float projectionMultiplier,
            float thresholdPixels,
            int[] fineTriangleCounts,
            float[] fineSurfaceArea,
            bool densityBudget)
        {
            bool errorVisible = ProjectedGroupError(
                group,
                camera,
                projectionScale,
                projectionMultiplier) > thresholdPixels;
            if (!densityBudget ||
                group.coarseClusterCount <= 0 ||
                fineTriangleCounts == null ||
                (uint)groupIndex >= (uint)fineTriangleCounts.Length ||
                fineSurfaceArea == null ||
                (uint)groupIndex >= (uint)fineSurfaceArea.Length ||
                fineTriangleCounts[groupIndex] <= 0)
                return errorVisible;
            float distance = Mathf.Max(
                Vector3.Distance(camera, new Vector3(
                    group.boundingSphere.x,
                    group.boundingSphere.y,
                    group.boundingSphere.z)) - group.boundingSphere.w,
                1e-4f);
            float radiusPixelScale = projectionScale / distance;
            // Match the runtime density contract. fineSurfaceArea is unsigned
            // two-sided surface area; Cauchy's projection formula converts it
            // to mean one-sided projected coverage for a closed surface.
            const float meanOneSidedProjection = 0.25f;
            float conservativePixels = Mathf.Max(
                1f,
                fineSurfaceArea[groupIndex] * meanOneSidedProjection *
                radiusPixelScale * radiusPixelScale);
            int coarseTriangles = CountRangeTriangles(
                mesh,
                group.coarseClusterStart,
                group.coarseClusterCount);
            return errorVisible || coarseTriangles < conservativePixels;
        }

        static int CountRangeTriangles(NaniteMesh mesh, int start, int count)
        {
            int triangles = 0;
            int end = Mathf.Min(mesh.hierarchyClusterRefs.Length, start + count);
            for (int refIndex = Mathf.Max(0, start); refIndex < end; refIndex++)
            {
                if (TryGetCluster(mesh, mesh.hierarchyClusterRefs[refIndex], out _, out NaniteCluster cluster))
                    triangles += cluster.indiceCount / 3;
            }
            return triangles;
        }

        static int[] BuildFineTriangleCounts(NaniteMesh mesh)
        {
            var result = new int[mesh.hierarchyGroups.Length];
            for (int groupIndex = 0; groupIndex < result.Length; groupIndex++)
            {
                NaniteHierarchyGroup group = mesh.hierarchyGroups[groupIndex];
                int end = Mathf.Min(
                    mesh.hierarchyClusterRefs.Length,
                    group.fineClusterStart + group.fineClusterCount);
                for (int refIndex = group.fineClusterStart; refIndex < end; refIndex++)
                {
                    NaniteHierarchyClusterRef clusterRef = mesh.hierarchyClusterRefs[refIndex];
                    if (TryGetCluster(
                            mesh,
                            clusterRef,
                            out NaniteMeshPage _,
                            out NaniteCluster cluster))
                        result[groupIndex] += cluster.indiceCount / 3;
                }
            }
            return result;
        }

        static float[] BuildFineSurfaceArea(NaniteMesh mesh)
        {
            var result = new float[mesh.hierarchyGroups.Length];
            for (int groupIndex = 0; groupIndex < result.Length; groupIndex++)
            {
                NaniteHierarchyGroup group = mesh.hierarchyGroups[groupIndex];
                int end = Mathf.Min(
                    mesh.hierarchyClusterRefs.Length,
                    group.fineClusterStart + group.fineClusterCount);
                double sum = 0.0;
                for (int refIndex = group.fineClusterStart; refIndex < end; refIndex++)
                {
                    NaniteHierarchyClusterRef clusterRef = mesh.hierarchyClusterRefs[refIndex];
                    if (TryGetCluster(
                            mesh,
                            clusterRef,
                            out NaniteMeshPage page,
                            out NaniteCluster cluster))
                    {
                        int stride = Mathf.Max(3, page.vertexStride);
                        int indexEnd = Mathf.Min(page.indiceArray.Length, cluster.indiceIndex + cluster.indiceCount);
                        for (int index = Mathf.Max(0, cluster.indiceIndex); index + 2 < indexEnd; index += 3)
                        {
                            int i0 = (page.indiceArray[index + 0] + cluster.vertexOffset) * stride;
                            int i1 = (page.indiceArray[index + 1] + cluster.vertexOffset) * stride;
                            int i2 = (page.indiceArray[index + 2] + cluster.vertexOffset) * stride;
                            if (i0 < 0 || i1 < 0 || i2 < 0 ||
                                i0 + 2 >= page.vertexData.Length || i1 + 2 >= page.vertexData.Length || i2 + 2 >= page.vertexData.Length)
                                continue;
                            Vector3 p0 = new Vector3(page.vertexData[i0], page.vertexData[i0 + 1], page.vertexData[i0 + 2]);
                            Vector3 p1 = new Vector3(page.vertexData[i1], page.vertexData[i1 + 1], page.vertexData[i1 + 2]);
                            Vector3 p2 = new Vector3(page.vertexData[i2], page.vertexData[i2 + 1], page.vertexData[i2 + 2]);
                            sum += 0.5 * Vector3.Cross(p1 - p0, p2 - p0).magnitude;
                        }
                    }
                }
                result[groupIndex] = (float)sum;
            }
            return result;
        }

        static float ProjectedGroupError(
            NaniteHierarchyGroup group,
            Vector3 camera,
            float projectionScale,
            float projectionMultiplier)
        {
            bool finite = group.maxParentLodError < kMaxFiniteError &&
                          !float.IsInfinity(group.maxParentLodError) &&
                          !float.IsNaN(group.maxParentLodError);
            if (!finite)
                return float.MaxValue;
            float distance = Mathf.Max(
                Vector3.Distance(
                    camera,
                    new Vector3(group.boundingSphere.x, group.boundingSphere.y, group.boundingSphere.z)) -
                group.boundingSphere.w,
                1e-3f);
            return projectionMultiplier * group.maxParentLodError * projectionScale / distance;
        }

        static void ProcessRef(
            NaniteMesh mesh,
            int refIndex,
            Vector3 camera,
            float projectionScale,
            float thresholdPixels,
            float projectionMultiplier,
            HashSet<int> processedGroups,
            HashSet<int> selectedGeometry,
            CutStats result)
        {
            if ((uint)refIndex >= (uint)mesh.hierarchyClusterRefs.Length)
                return;
            NaniteHierarchyClusterRef clusterRef = mesh.hierarchyClusterRefs[refIndex];
            int groupIndex = clusterRef.refinementGroupIndex;
            if ((uint)groupIndex >= (uint)mesh.hierarchyGroups.Length)
            {
                AddSelected(mesh, clusterRef, selectedGeometry, result);
                return;
            }
            if (!processedGroups.Add(groupIndex))
                return;

            NaniteHierarchyGroup group = mesh.hierarchyGroups[groupIndex];
            bool finite = group.maxParentLodError < kMaxFiniteError &&
                          !float.IsInfinity(group.maxParentLodError) &&
                          !float.IsNaN(group.maxParentLodError);
            float distance = Mathf.Max(
                Vector3.Distance(
                    camera,
                    new Vector3(group.boundingSphere.x, group.boundingSphere.y, group.boundingSphere.z)) -
                group.boundingSphere.w,
                1e-3f);
            float errorPixels = finite
                ? projectionMultiplier * group.maxParentLodError * projectionScale / distance
                : float.MaxValue;
            if (errorPixels > thresholdPixels && group.fineClusterCount > 0)
            {
                result.refinedGroups.Add(groupIndex);
                int fineEnd = Mathf.Min(
                    mesh.hierarchyClusterRefs.Length,
                    group.fineClusterStart + group.fineClusterCount);
                for (int child = Mathf.Max(0, group.fineClusterStart); child < fineEnd; child++)
                {
                    ProcessRef(
                        mesh,
                        child,
                        camera,
                        projectionScale,
                        thresholdPixels,
                        projectionMultiplier,
                        processedGroups,
                        selectedGeometry,
                        result);
                }
                return;
            }

            result.coarseGroups.Add(groupIndex);
            int coarseEnd = Mathf.Min(
                mesh.hierarchyClusterRefs.Length,
                group.coarseClusterStart + group.coarseClusterCount);
            for (int coarse = Mathf.Max(0, group.coarseClusterStart); coarse < coarseEnd; coarse++)
                AddSelected(mesh, mesh.hierarchyClusterRefs[coarse], selectedGeometry, result);
        }

        static void AddSelected(
            NaniteMesh mesh,
            NaniteHierarchyClusterRef clusterRef,
            HashSet<int> selectedGeometry,
            CutStats result)
        {
            if (!selectedGeometry.Add(clusterRef.geometryClusterIndex))
            {
                result.duplicateClusters++;
                return;
            }
            if (!TryGetCluster(mesh, clusterRef, out NaniteMeshPage page, out NaniteCluster cluster))
                return;
            result.clusters++;
            result.triangles += cluster.indiceCount / 3;
            result.selectedGeometry.Add(clusterRef.geometryClusterIndex);
            int mip = page.clusterMip != null &&
                      (uint)clusterRef.pageClusterIndex < (uint)page.clusterMip.Length
                ? page.clusterMip[clusterRef.pageClusterIndex]
                : ((uint)cluster.partIndex < (uint)page.parts.Length
                    ? page.parts[cluster.partIndex].mipLevel
                    : -1);
            result.mipClusters.TryGetValue(mip, out int count);
            result.mipClusters[mip] = count + 1;
        }

        static void AppendRegressionDetails(
            NaniteMesh mesh,
            StringBuilder sb,
            CutStats previous,
            CutStats current,
            long previousTriangles,
            float surfaceDistance)
        {
            if (previous == null)
                return;

            long addedTriangles = 0;
            long removedTriangles = 0;
            var addedByMip = new SortedDictionary<int, int>();
            var removedByMip = new SortedDictionary<int, int>();
            foreach (int geometryIndex in current.selectedGeometry)
            {
                if (previous.selectedGeometry.Contains(geometryIndex))
                    continue;
                AccumulateGeometry(mesh, geometryIndex, addedByMip, ref addedTriangles);
            }
            foreach (int geometryIndex in previous.selectedGeometry)
            {
                if (current.selectedGeometry.Contains(geometryIndex))
                    continue;
                AccumulateGeometry(mesh, geometryIndex, removedByMip, ref removedTriangles);
            }

            var becameCoarse = new List<int>();
            var becameRefined = new List<int>();
            foreach (int group in current.coarseGroups)
                if (previous.refinedGroups.Contains(group)) becameCoarse.Add(group);
            foreach (int group in current.refinedGroups)
                if (previous.coarseGroups.Contains(group)) becameRefined.Add(group);
            becameCoarse.Sort();
            becameRefined.Sort();

            sb.AppendLine(
                $"    REGRESSION at {F(surfaceDistance)}: {previousTriangles}->{current.triangles}, " +
                $"added={addedTriangles}tri{MipTriangleText(addedByMip)}, " +
                $"removed={removedTriangles}tri{MipTriangleText(removedByMip)}, " +
                $"becameCoarse={Join(becameCoarse)}, becameRefined={Join(becameRefined)}");
            for (int index = 0; index < becameCoarse.Count; index++)
            {
                int groupIndex = becameCoarse[index];
                NaniteHierarchyGroup group = mesh.hierarchyGroups[groupIndex];
                CountRangeSelection(mesh, group.fineClusterStart, group.fineClusterCount, previous, out int fineRaw, out int fineSelected);
                CountRangeSelection(mesh, group.coarseClusterStart, group.coarseClusterCount, current, out int coarseRaw, out int coarseSelected);
                sb.AppendLine(
                    $"      g{groupIndex}/m{group.mipLevel}: immediateFine={fineRaw}tri(selected {fineSelected}), " +
                    $"coarse={coarseRaw}tri(selected {coarseSelected}), error={F(group.maxParentLodError)}, " +
                    $"sphere=({F(group.boundingSphere.x)},{F(group.boundingSphere.y)},{F(group.boundingSphere.z)},{F(group.boundingSphere.w)})");
            }
        }

        static void CountRangeSelection(
            NaniteMesh mesh,
            int start,
            int count,
            CutStats cut,
            out int rawTriangles,
            out int selectedTriangles)
        {
            rawTriangles = 0;
            selectedTriangles = 0;
            int end = Mathf.Min(mesh.hierarchyClusterRefs.Length, start + count);
            for (int refIndex = Mathf.Max(0, start); refIndex < end; refIndex++)
            {
                NaniteHierarchyClusterRef clusterRef = mesh.hierarchyClusterRefs[refIndex];
                if (!TryGetCluster(mesh, clusterRef, out _, out NaniteCluster cluster))
                    continue;
                int triangles = cluster.indiceCount / 3;
                rawTriangles += triangles;
                if (cut.selectedGeometry.Contains(clusterRef.geometryClusterIndex))
                    selectedTriangles += triangles;
            }
        }

        static void AccumulateGeometry(
            NaniteMesh mesh,
            int geometryIndex,
            SortedDictionary<int, int> trianglesByMip,
            ref long triangleTotal)
        {
            if (!TryGetClusterByGeometryIndex(mesh, geometryIndex, out NaniteMeshPage page, out NaniteCluster cluster, out int pageClusterIndex))
                return;
            int triangles = cluster.indiceCount / 3;
            int mip = page.clusterMip != null && (uint)pageClusterIndex < (uint)page.clusterMip.Length
                ? page.clusterMip[pageClusterIndex]
                : ((uint)cluster.partIndex < (uint)page.parts.Length ? page.parts[cluster.partIndex].mipLevel : -1);
            triangleTotal += triangles;
            trianglesByMip.TryGetValue(mip, out int previous);
            trianglesByMip[mip] = previous + triangles;
        }

        static bool TryGetClusterByGeometryIndex(
            NaniteMesh mesh,
            int geometryIndex,
            out NaniteMeshPage page,
            out NaniteCluster cluster,
            out int pageClusterIndex)
        {
            page = null;
            cluster = default;
            pageClusterIndex = -1;
            if (mesh.pageArray == null || geometryIndex < 0)
                return false;
            int cursor = 0;
            for (int pageIndex = 0; pageIndex < mesh.pageArray.Length; pageIndex++)
            {
                NaniteMeshPage candidate = mesh.pageArray[pageIndex];
                int count = candidate?.clusterArray?.Length ?? 0;
                if (geometryIndex < cursor + count)
                {
                    page = candidate;
                    pageClusterIndex = geometryIndex - cursor;
                    cluster = candidate.clusterArray[pageClusterIndex];
                    return true;
                }
                cursor += count;
            }
            return false;
        }

        static string MipTriangleText(SortedDictionary<int, int> values)
        {
            var text = new StringBuilder(48).Append('[');
            bool first = true;
            foreach (KeyValuePair<int, int> pair in values)
            {
                if (!first) text.Append(',');
                first = false;
                text.Append(pair.Key).Append(':').Append(pair.Value);
            }
            return text.Append(']').ToString();
        }

        static TopologyStats GatherTopology(NaniteMesh mesh, int start, int count)
        {
            var result = new TopologyStats();
            var edgeCounts = new Dictionary<EdgeKey, int>();
            var adjacency = new Dictionary<VertexKey, HashSet<VertexKey>>();
            var uvStretchSamples = new List<float>(Mathf.Min(Mathf.Max(0, count) * 384, 32768));
            int end = Mathf.Min(mesh.hierarchyClusterRefs.Length, start + count);
            for (int refIndex = Mathf.Max(0, start); refIndex < end; refIndex++)
            {
                NaniteHierarchyClusterRef clusterRef = mesh.hierarchyClusterRefs[refIndex];
                if (!TryGetCluster(mesh, clusterRef, out NaniteMeshPage page, out NaniteCluster cluster))
                    continue;
                int[] indices = page.indiceArray;
                float[] vertices = page.vertexData;
                int stride = Mathf.Max(1, page.vertexStride);
                int clusterEnd = Mathf.Min(indices.Length, cluster.indiceIndex + cluster.indiceCount);
                for (int index = cluster.indiceIndex; index + 2 < clusterEnd; index += 3)
                {
                    int i0 = indices[index + 0] + cluster.vertexOffset;
                    int i1 = indices[index + 1] + cluster.vertexOffset;
                    int i2 = indices[index + 2] + cluster.vertexOffset;
                    if (!TryReadVertex(vertices, stride, i0, out Vector3 p0, out Vector2 uv0) ||
                        !TryReadVertex(vertices, stride, i1, out Vector3 p1, out Vector2 uv1) ||
                        !TryReadVertex(vertices, stride, i2, out Vector3 p2, out Vector2 uv2))
                        continue;
                    result.triangles++;
                    VertexKey v0 = Quantize(p0);
                    VertexKey v1 = Quantize(p1);
                    VertexKey v2 = Quantize(p2);
                    if (v0.Equals(v1) || v1.Equals(v2) || v2.Equals(v0) ||
                        Vector3.Cross(p1 - p0, p2 - p0).sqrMagnitude <= 1e-16f)
                    {
                        result.degenerateTriangles++;
                    }
                    RegisterEdge(edgeCounts, adjacency, v0, v1);
                    RegisterEdge(edgeCounts, adjacency, v1, v2);
                    RegisterEdge(edgeCounts, adjacency, v2, v0);
                    AddUvStretchSample(p0, p1, uv0, uv1, uvStretchSamples, result);
                    AddUvStretchSample(p1, p2, uv1, uv2, uvStretchSamples, result);
                    AddUvStretchSample(p2, p0, uv2, uv0, uvStretchSamples, result);
                }
            }

            if (uvStretchSamples.Count != 0)
            {
                uvStretchSamples.Sort();
                int percentileIndex = Mathf.Clamp(
                    Mathf.CeilToInt((uvStretchSamples.Count - 1) * 0.995f),
                    0,
                    uvStretchSamples.Count - 1);
                result.robustUvStretch995 = uvStretchSamples[percentileIndex];
            }

            foreach (KeyValuePair<EdgeKey, int> edge in edgeCounts)
            {
                if (edge.Value == 1)
                    result.boundary.Add(edge.Key);
                else if (edge.Value > 2)
                    result.nonManifoldEdges++;
            }
            result.components = CountComponents(adjacency);
            return result;
        }

        static void AddUvStretchSample(
            Vector3 p0,
            Vector3 p1,
            Vector2 uv0,
            Vector2 uv1,
            List<float> samples,
            TopologyStats result)
        {
            float stretch = UvStretch(p0, p1, uv0, uv1);
            if (!float.IsFinite(stretch))
                return;
            samples.Add(stretch);
            result.maxUvStretch = Mathf.Max(result.maxUvStretch, stretch);
        }

        static bool TryGetCluster(
            NaniteMesh mesh,
            NaniteHierarchyClusterRef clusterRef,
            out NaniteMeshPage page,
            out NaniteCluster cluster)
        {
            page = null;
            cluster = default;
            if (mesh.pageArray == null ||
                (uint)clusterRef.pageIndex >= (uint)mesh.pageArray.Length)
                return false;
            page = mesh.pageArray[clusterRef.pageIndex];
            if (page == null || page.clusterArray == null ||
                (uint)clusterRef.pageClusterIndex >= (uint)page.clusterArray.Length)
                return false;
            cluster = page.clusterArray[clusterRef.pageClusterIndex];
            return true;
        }

        static bool TryReadVertex(
            float[] vertices,
            int stride,
            int index,
            out Vector3 position,
            out Vector2 uv)
        {
            int offset = index * stride;
            if (vertices == null || index < 0 || offset < 0 || offset + 2 >= vertices.Length)
            {
                position = default;
                uv = default;
                return false;
            }
            position = new Vector3(vertices[offset], vertices[offset + 1], vertices[offset + 2]);
            uv = stride >= 5 && offset + 4 < vertices.Length
                ? new Vector2(vertices[offset + 3], vertices[offset + 4])
                : Vector2.zero;
            return true;
        }

        static VertexKey Quantize(Vector3 position) => new VertexKey
        {
            // Match meshoptimizer's position-remap/topology contract exactly.
            // Rounding unrelated vertices into a 1e-4 cell can invent a bridge
            // in the fine mesh and then report "component growth" when a valid
            // simplification keeps only one of those nearby surfaces.
            x = FloatPositionKey(position.x),
            y = FloatPositionKey(position.y),
            z = FloatPositionKey(position.z)
        };

        static int FloatPositionKey(float value) =>
            value == 0f ? 0 : BitConverter.SingleToInt32Bits(value);

        static void RegisterEdge(
            Dictionary<EdgeKey, int> edgeCounts,
            Dictionary<VertexKey, HashSet<VertexKey>> adjacency,
            VertexKey a,
            VertexKey b)
        {
            if (a.Equals(b))
                return;
            var edge = new EdgeKey(a, b);
            edgeCounts.TryGetValue(edge, out int count);
            edgeCounts[edge] = count + 1;
            if (!adjacency.TryGetValue(a, out HashSet<VertexKey> aNeighbours))
            {
                aNeighbours = new HashSet<VertexKey>();
                adjacency.Add(a, aNeighbours);
            }
            if (!adjacency.TryGetValue(b, out HashSet<VertexKey> bNeighbours))
            {
                bNeighbours = new HashSet<VertexKey>();
                adjacency.Add(b, bNeighbours);
            }
            aNeighbours.Add(b);
            bNeighbours.Add(a);
        }

        static int CountComponents(Dictionary<VertexKey, HashSet<VertexKey>> adjacency)
        {
            int components = 0;
            var visited = new HashSet<VertexKey>();
            var queue = new Queue<VertexKey>();
            foreach (VertexKey root in adjacency.Keys)
            {
                if (!visited.Add(root))
                    continue;
                components++;
                queue.Enqueue(root);
                while (queue.Count > 0)
                {
                    VertexKey vertex = queue.Dequeue();
                    foreach (VertexKey neighbour in adjacency[vertex])
                    {
                        if (visited.Add(neighbour))
                            queue.Enqueue(neighbour);
                    }
                }
            }
            return components;
        }

        static float UvStretch(Vector3 a, Vector3 b, Vector2 uvA, Vector2 uvB) =>
            Vector2.Distance(uvA, uvB) / Mathf.Max(1e-6f, Vector3.Distance(a, b));

        static bool ValidRange(int start, int count, int length) =>
            start >= 0 && count >= 0 && start <= length && count <= length - start;

        static string MipText(CutStats stats)
        {
            var sb = new StringBuilder(48);
            sb.Append('[');
            bool first = true;
            foreach (KeyValuePair<int, int> mip in stats.mipClusters)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append(mip.Key).Append(':').Append(mip.Value);
            }
            return sb.Append(']').ToString();
        }

        static string DirectionName(Vector3 direction)
        {
            if (direction == Vector3.forward) return "+Z";
            if (direction == Vector3.back) return "-Z";
            if (direction == Vector3.right) return "+X";
            if (direction == Vector3.left) return "-X";
            return "+Y";
        }

        static string F(float value) =>
            value.ToString("G6", CultureInfo.InvariantCulture);

        static string Join(List<int> values) =>
            values == null || values.Count == 0 ? "none" : string.Join(",", values);
    }
}
#endif
