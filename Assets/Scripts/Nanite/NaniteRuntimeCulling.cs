using System.Collections.Generic;
using UnityEngine;

namespace Nanite
{
    /// <summary>一次可见簇引用（Page + Cluster 本地下标）。</summary>
    public struct NaniteVisibleClusterRef
    {
        public int pageIndex;
        public int clusterIndex;
    }

    /// <summary>剔除统计信息，用于验证/调优。</summary>
    public struct NaniteCullingStats
    {
        public int testedInstances;
        public int testedNodes;
        public int testedParts;
        public int testedClusters;
        public int visibleClusters;
    }

    /// <summary>
    /// 仿 UE Nanite 的 Node+Cluster 两级剔除：
    /// - 节点(BVH node)做层级过滤
    /// - 叶子 part 做粗过滤
    /// - part 内 cluster 做最终 LOD+视锥判断
    /// </summary>
    public static class NaniteRuntimeCulling
    {
        public static void BuildSelection(
            NaniteMesh mesh,
            Camera camera,
            float lodErrorPixels,
            bool useBvh,
            Matrix4x4 localToWorld,
            float maxScale,
            NaniteRuntimeSelection selection)
        {
            var visible = new List<NaniteVisibleClusterRef>(4096);

            CullVisibleClusters(
                mesh,
                camera,
                lodErrorPixels,
                useBvh,
                localToWorld,
                maxScale,
                visible,
                out var stats);

            BuildSelectionFromVisibleClusters(mesh, visible, stats, selection);
        }

        public static void BuildSelectionFromVisibleClusters(
            NaniteMesh mesh,
            List<NaniteVisibleClusterRef> visibleClusters,
            NaniteCullingStats stats,
            NaniteRuntimeSelection selection)
        {
            selection.Clear();
            selection.stats = stats;

            if (visibleClusters == null || visibleClusters.Count == 0)
                return;
            var dedup = new HashSet<long>();
            for (int i = 0; i < visibleClusters.Count; i++)
            {
                var vr = visibleClusters[i];
                long key = ((long)vr.pageIndex << 32) | (uint)vr.clusterIndex;
                if (dedup.Add(key))
                    selection.visibleClusters.Add(vr);
            }
            selection.stats.visibleClusters = selection.visibleClusters.Count;

            if (mesh == null || mesh.pageArray == null)
                return;

            var pageToClusterIndices = new Dictionary<int, List<int>>();
            for (int i = 0; i < selection.visibleClusters.Count; i++)
            {
                var vr = selection.visibleClusters[i];
                if (!pageToClusterIndices.TryGetValue(vr.pageIndex, out var clusterIndices))
                {
                    clusterIndices = new List<int>();
                    pageToClusterIndices.Add(vr.pageIndex, clusterIndices);
                }

                clusterIndices.Add(vr.clusterIndex);
            }

            var orderedPages = new List<int>(pageToClusterIndices.Keys);
            orderedPages.Sort();

            for (int p = 0; p < orderedPages.Count; p++)
            {
                int pageIndex = orderedPages[p];
                if (pageIndex < 0 || pageIndex >= mesh.pageArray.Length)
                    continue;

                var page = mesh.pageArray[pageIndex];
                if (page == null || page.clusterArray == null)
                    continue;

                var clusterIndices = pageToClusterIndices[pageIndex];
                clusterIndices.Sort();
                int packetStart = selection.packets.Count;
                int lastCluster = -1;

                for (int i = 0; i < clusterIndices.Count; i++)
                {
                    int ci = clusterIndices[i];
                    if (ci == lastCluster)
                        continue;
                    lastCluster = ci;
                    if (ci < 0 || ci >= page.clusterArray.Length)
                        continue;

                    var cluster = page.clusterArray[ci];
                    int mip = (page.clusterMip != null && ci < page.clusterMip.Length) ? page.clusterMip[ci] : -1;
                    selection.packets.Add(new NaniteVisibleClusterPacket
                    {
                        pageIndex = pageIndex,
                        clusterIndex = ci,
                        instanceId = -1,
                        subMeshId = cluster.subMeshId,
                        mipLevel = mip,
                        indexOffset = cluster.indiceIndex,
                        indexCount = cluster.indiceCount,
                        vertexOffset = cluster.vertexOffset
                    });
                }

                selection.pageRanges.Add(new NaniteVisiblePageRange
                {
                    pageIndex = pageIndex,
                    start = packetStart,
                    count = selection.packets.Count - packetStart
                });
            }
        }

        public static void CullVisibleClusters(
            NaniteMesh mesh,
            Camera camera,
            float lodErrorPixels,
            bool useBvh,
            List<NaniteVisibleClusterRef> output,
            out NaniteCullingStats stats)
        {
            CullVisibleClusters(
                mesh,
                camera,
                lodErrorPixels,
                useBvh,
                Matrix4x4.identity,
                1f,
                output,
                out stats);
        }

        public static void CullVisibleClusters(
            NaniteMesh mesh,
            Camera camera,
            float lodErrorPixels,
            bool useBvh,
            Matrix4x4 localToWorld,
            float maxScale,
            List<NaniteVisibleClusterRef> output,
            out NaniteCullingStats stats)
        {
            output.Clear();
            stats = default;
            if (mesh == null || camera == null || mesh.pageArray == null)
                return;
            stats.testedInstances = 1;

            var planes = GeometryUtility.CalculateFrustumPlanes(camera);
            Vector3 cameraPosition = camera.transform.position;
            float projectionScale = camera.projectionMatrix.m11 * 0.5f * Mathf.Max(1, camera.pixelHeight);
            float zNear = Mathf.Max(1e-3f, camera.nearClipPlane);

            for (int pageIndex = 0; pageIndex < mesh.pageArray.Length; pageIndex++)
            {
                var page = mesh.pageArray[pageIndex];
                if (page == null)
                    continue;

                if (useBvh && page.bvhNodes != null && page.bvhNodes.Length > 0 && page.bvhRoot >= 0)
                {
                    CullPageBvh(page, pageIndex, planes, cameraPosition, projectionScale, zNear, lodErrorPixels, localToWorld, maxScale, output, ref stats);
                }
                else
                {
                    CullPageBrute(page, pageIndex, planes, cameraPosition, projectionScale, zNear, lodErrorPixels, localToWorld, maxScale, output, ref stats);
                }
            }

            stats.visibleClusters = output.Count;
        }

        public static bool ValidateBvhAgainstBruteForce(
            NaniteMesh mesh,
            Camera camera,
            float lodErrorPixels,
            out string report)
        {
            var bvh = new List<NaniteVisibleClusterRef>(4096);
            var brute = new List<NaniteVisibleClusterRef>(4096);
            CullVisibleClusters(mesh, camera, lodErrorPixels, true, bvh, out var bvhStats);
            CullVisibleClusters(mesh, camera, lodErrorPixels, false, brute, out var bruteStats);

            var bvhSet = new HashSet<long>();
            var bruteSet = new HashSet<long>();
            for (int i = 0; i < bvh.Count; i++)
                bvhSet.Add(PackKey(bvh[i]));
            for (int i = 0; i < brute.Count; i++)
                bruteSet.Add(PackKey(brute[i]));

            int missing = 0;
            foreach (long key in bruteSet)
                if (!bvhSet.Contains(key))
                    missing++;

            int extra = 0;
            foreach (long key in bvhSet)
                if (!bruteSet.Contains(key))
                    extra++;

            bool ok = missing == 0 && extra == 0;
            report =
                $"BVH可见簇={bvhSet.Count}, Brute可见簇={bruteSet.Count}, 缺失={missing}, 额外={extra}\n" +
                $"BVH测试: inst={bvhStats.testedInstances} node={bvhStats.testedNodes} part={bvhStats.testedParts} cluster={bvhStats.testedClusters}\n" +
                $"Brute测试: inst={bruteStats.testedInstances} cluster={bruteStats.testedClusters}";
            return ok;
        }

        static long PackKey(NaniteVisibleClusterRef r) => ((long)r.pageIndex << 32) | (uint)r.clusterIndex;

        static void CullPageBrute(
            NaniteMeshPage page,
            int pageIndex,
            Plane[] planes,
            Vector3 cameraPosition,
            float projectionScale,
            float zNear,
            float lodErrorPixels,
            Matrix4x4 localToWorld,
            float maxScale,
            List<NaniteVisibleClusterRef> output,
            ref NaniteCullingStats stats)
        {
            if (page.clusterArray == null)
                return;

            for (int i = 0; i < page.clusterArray.Length; i++)
            {
                ref readonly var cluster = ref page.clusterArray[i];
                Vector4 worldGeometry = TransformSphere(
                    cluster.geometrySphere.w > 0f ? cluster.geometrySphere : cluster.selfSphere,
                    localToWorld,
                    maxScale);
                Vector4 worldSelf = TransformSphere(cluster.selfSphere, localToWorld, maxScale);
                Vector4 worldParent = TransformSphere(cluster.parentSphere, localToWorld, maxScale);
                stats.testedClusters++;
                if (!SphereVisible(worldGeometry, planes))
                    continue;
                if (!ShouldRenderCluster(cluster, worldSelf, worldParent, cameraPosition, projectionScale, zNear, lodErrorPixels))
                    continue;

                output.Add(new NaniteVisibleClusterRef { pageIndex = pageIndex, clusterIndex = i });
            }
        }

        static void CullPageBvh(
            NaniteMeshPage page,
            int pageIndex,
            Plane[] planes,
            Vector3 cameraPosition,
            float projectionScale,
            float zNear,
            float lodErrorPixels,
            Matrix4x4 localToWorld,
            float maxScale,
            List<NaniteVisibleClusterRef> output,
            ref NaniteCullingStats stats)
        {
            var stack = new List<int>(64) { page.bvhRoot };
            while (stack.Count > 0)
            {
                int last = stack.Count - 1;
                int nodeIndex = stack[last];
                stack.RemoveAt(last);

                ref readonly var node = ref page.bvhNodes[nodeIndex];
                Vector4 nodeLodSphereLocal = node.lodSphere.w > 0f ? node.lodSphere : node.sphere;
                Vector4 worldNodeSphere = TransformSphere(node.sphere, localToWorld, maxScale);
                Vector4 worldNodeLodSphere = TransformSphere(nodeLodSphereLocal, localToWorld, maxScale);
                stats.testedNodes++;

                if (!SphereVisible(worldNodeSphere, planes))
                    continue;

                float nodeError = ProjectedErrorPixels(node.maxParentLodError, worldNodeLodSphere, cameraPosition, projectionScale, zNear);
                if (nodeError <= lodErrorPixels)
                    continue;

                if (node.partIndex >= 0)
                {
                    CullPart(page, pageIndex, node.partIndex, planes, cameraPosition, projectionScale, zNear, lodErrorPixels, localToWorld, maxScale, output, ref stats);
                }
                else
                {
                    if (node.child0 >= 0) stack.Add(node.child0);
                    if (node.child1 >= 0) stack.Add(node.child1);
                    if (node.child2 >= 0) stack.Add(node.child2);
                    if (node.child3 >= 0) stack.Add(node.child3);
                }
            }
        }

        static void CullPart(
            NaniteMeshPage page,
            int pageIndex,
            int partIndex,
            Plane[] planes,
            Vector3 cameraPosition,
            float projectionScale,
            float zNear,
            float lodErrorPixels,
            Matrix4x4 localToWorld,
            float maxScale,
            List<NaniteVisibleClusterRef> output,
            ref NaniteCullingStats stats)
        {
            ref readonly var part = ref page.parts[partIndex];
            Vector4 partLodSphereLocal = part.parentSphere.w > 0f ? part.parentSphere : part.selfSphere;
            Vector4 worldPart = TransformSphere(part.selfSphere, localToWorld, maxScale);
            Vector4 worldPartLod = TransformSphere(partLodSphereLocal, localToWorld, maxScale);
            stats.testedParts++;
            if (!SphereVisible(worldPart, planes))
                return;

            float partError = ProjectedErrorPixels(part.maxParentLodError, worldPartLod, cameraPosition, projectionScale, zNear);
            if (partError <= lodErrorPixels)
                return;

            int clusterEnd = part.clusterStart + part.clusterCount;
            for (int ci = part.clusterStart; ci < clusterEnd; ci++)
            {
                ref readonly var cluster = ref page.clusterArray[ci];
                Vector4 worldGeometry = TransformSphere(
                    cluster.geometrySphere.w > 0f ? cluster.geometrySphere : cluster.selfSphere,
                    localToWorld,
                    maxScale);
                Vector4 worldSelf = TransformSphere(cluster.selfSphere, localToWorld, maxScale);
                Vector4 worldParent = TransformSphere(cluster.parentSphere, localToWorld, maxScale);
                stats.testedClusters++;
                if (!SphereVisible(worldGeometry, planes))
                    continue;
                if (!ShouldRenderCluster(cluster, worldSelf, worldParent, cameraPosition, projectionScale, zNear, lodErrorPixels))
                    continue;

                // 与 GPU 一致：屏幕直径 < 1px 的整簇丢弃。
                float dist = Mathf.Max(
                    Vector3.Distance(new Vector3(worldGeometry.x, worldGeometry.y, worldGeometry.z), cameraPosition) - worldGeometry.w,
                    zNear);
                float diameterPixels = (2f * worldGeometry.w * projectionScale) / dist;
                if (diameterPixels < 1f)
                    continue;

                output.Add(new NaniteVisibleClusterRef { pageIndex = pageIndex, clusterIndex = ci });
            }
        }

        static bool ShouldRenderCluster(
            in NaniteCluster cluster,
            in Vector4 worldSelf,
            in Vector4 worldParent,
            Vector3 cameraPosition,
            float projectionScale,
            float zNear,
            float lodErrorPixels)
        {
            // 互斥 DAG 切割：parent/self 分球投影。禁止宽重叠带（会双选父子→浮动碎块）。
            float parentError = ProjectedErrorPixels(cluster.parentError, worldParent, cameraPosition, projectionScale, zNear);
            if (parentError <= lodErrorPixels)
                return false;

            float selfError = ProjectedErrorPixels(cluster.selfError, worldSelf, cameraPosition, projectionScale, zNear);
            return selfError <= lodErrorPixels;
        }

        static bool SphereVisible(in Vector4 sphere, Plane[] planes)
        {
            var center = new Vector3(sphere.x, sphere.y, sphere.z);
            float radius = sphere.w;

            for (int i = 0; i < planes.Length; i++)
            {
                if (planes[i].GetDistanceToPoint(center) < -radius)
                    return false;
            }

            return true;
        }

        static float ProjectedErrorPixels(
            float error,
            in Vector4 sphere,
            Vector3 cameraPosition,
            float projectionScale,
            float zNear)
        {
            if (float.IsPositiveInfinity(error) || error >= float.MaxValue * 0.5f)
                return float.PositiveInfinity;

            var center = new Vector3(sphere.x, sphere.y, sphere.z);
            float distance = Vector3.Distance(center, cameraPosition) - sphere.w;
            distance = Mathf.Max(distance, zNear);
            // 与 GPU 一致：按屏幕直径（像素）计。
            return (error * projectionScale) / distance;
        }

        static Vector4 TransformSphere(in Vector4 localSphere, Matrix4x4 localToWorld, float maxScale)
        {
            Vector3 worldCenter = localToWorld.MultiplyPoint3x4(new Vector3(localSphere.x, localSphere.y, localSphere.z));
            float worldRadius = localSphere.w * Mathf.Max(1e-6f, maxScale);
            return new Vector4(worldCenter.x, worldCenter.y, worldCenter.z, worldRadius);
        }
    }
}
