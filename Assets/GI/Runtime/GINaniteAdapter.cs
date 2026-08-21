using System;
using System.Collections.Generic;
using Nanite;
using UnityEngine;

namespace UnityNanite.GI
{
    /// <summary>
    /// Read-only access to Nanite's currently published resident geometry.
    /// The adapter never owns or retains Nanite buffers beyond the current frame.
    /// </summary>
    public sealed class GINaniteAdapter
    {
        readonly List<GINaniteSceneInstance> instances = new List<GINaniteSceneInstance>(512);
        readonly List<int> trackedInstanceIndices = new List<int>(128);
        int cachedProxyCount;
        int cachedInstancePageCount;
        uint loggedPoolGeneration = uint.MaxValue;
        ulong loggedTableEpoch = ulong.MaxValue;

        public int SceneRevision => NaniteRuntimeRegistry.Revision;

        public bool TryAcquire(out GINaniteFrameView frame)
        {
            frame = default;
            NaniteRendererFeature feature = NaniteRendererFeature.ActiveInstance;
            if (feature == null ||
                !feature.TryGetResidentPageReadOnlyView(out NaniteResidentPageReadOnlyView resident) ||
                !resident.IsValid)
            {
                return false;
            }

            frame = new GINaniteFrameView(
                resident, cachedProxyCount, cachedInstancePageCount);
            if (resident.poolGeneration != loggedPoolGeneration || resident.tableEpoch != loggedTableEpoch)
            {
                loggedPoolGeneration = resident.poolGeneration;
                loggedTableEpoch = resident.tableEpoch;
                Debug.Log(
                    $"[GI][Node2] Nanite read-only view: proxies={cachedProxyCount}, " +
                    $"instancePages={cachedInstancePageCount}, resident={resident.residentPageCount}/{resident.pageCount}, " +
                    $"poolGeneration={resident.poolGeneration}, tableEpoch={resident.tableEpoch}, " +
                    $"publishedFrame={resident.publishedFrame}.");
            }
            return true;
        }

        public bool TryCollectScene(out GINaniteFrameView frame, out IReadOnlyList<GINaniteSceneInstance> sceneInstances)
        {
            sceneInstances = instances;
            instances.Clear();
            trackedInstanceIndices.Clear();
            if (!TryAcquire(out frame))
                return false;

            NaniteRendererFeature feature = NaniteRendererFeature.ActiveInstance;
            var proxies = NaniteRuntimeRegistry.ActiveProxies;
            int rootPageCount = 0;
            int instancePageCount = 0;
            long coarseTriangleCount = 0;
            for (int proxyIndex = 0; proxyIndex < proxies.Count; proxyIndex++)
            {
                NaniteRuntimeProxy proxy = proxies[proxyIndex];
                NaniteMesh mesh = proxy != null ? proxy.naniteMesh : null;
                if (proxy == null || !proxy.isActiveAndEnabled || mesh == null)
                    continue;

                var rootPages = new List<GINaniteRootPage>(4);
                NanitePageStreamingInfo[] streaming = mesh.pageStreamingInfo;
                for (int localPage = 0; localPage < streaming.Length; localPage++)
                {
                    if (!streaming[localPage].IsRootPage ||
                        !feature.TryGetResidentPageId(mesh, localPage, out int pageId))
                        continue;
                    NaniteMeshPage page = mesh.pageArray != null && localPage < mesh.pageArray.Length
                        ? mesh.pageArray[localPage]
                        : null;
                    int indexCount = page != null ? page.BinaryIndexCount : 0;
                    if (indexCount <= 0 && page != null)
                        indexCount = page.indiceArray.Length;
                    if (indexCount >= 3)
                    {
                        rootPages.Add(new GINaniteRootPage((uint)pageId, indexCount / 3));
                        rootPageCount++;
                        coarseTriangleCount += indexCount / 3;
                    }
                }
                if (rootPages.Count == 0)
                    continue;

                if (feature.TryGetResidentMeshPageRange(
                        mesh, out _, out int pageCount, out _) && pageCount > 0)
                {
                    instancePageCount += pageCount;
                }

                Material material = ResolveMaterial(proxy, mesh);
                var instance = new GINaniteSceneInstance(
                    proxy, CalculateBounds(proxy, mesh),
                    GIWorldMaterialPacking.PackBaseColor(material),
                    GIWorldMaterialPacking.PackEmission(material),
                    (uint)proxy.GetInstanceID(), rootPages.ToArray());
                instances.Add(instance);
                if (RequiresTransformTracking(proxy.transform))
                    trackedInstanceIndices.Add(instances.Count - 1);
            }
            cachedProxyCount = instances.Count;
            cachedInstancePageCount = instancePageCount;
            frame = new GINaniteFrameView(
                frame.resident, cachedProxyCount, cachedInstancePageCount);
            Debug.Log(
                $"[GI][Node2] Nanite topology: instances={instances.Count}, " +
                $"trackedTransforms={trackedInstanceIndices.Count}, " +
                $"staticInstances={instances.Count - trackedInstanceIndices.Count}, " +
                $"rootPages={rootPageCount}, coarseTriangles={coarseTriangleCount}, " +
                $"registryRevision={NaniteRuntimeRegistry.Revision}.");
            // An empty collection is still a valid topology update: it removes the last
            // proxy's previous brick coverage instead of leaving stale Nanite surfaces.
            return true;
        }

        public bool PollTransformChanges(List<GIInstanceTransformChange> changes)
        {
            changes.Clear();
            for (int trackedIndex = 0; trackedIndex < trackedInstanceIndices.Count; trackedIndex++)
            {
                int instanceIndex = trackedInstanceIndices[trackedIndex];
                GINaniteSceneInstance instance = instances[instanceIndex];
                NaniteRuntimeProxy proxy = instance.proxy;
                if (proxy == null || !proxy.isActiveAndEnabled || proxy.naniteMesh == null)
                    continue; // Registry revision handles membership changes.
                Matrix4x4 matrix = proxy.transform.localToWorldMatrix;
                if (MatrixApproximatelyEqual(matrix, instance.localToWorld))
                    continue;
                Bounds oldBounds = instance.worldBounds;
                Bounds newBounds = CalculateBounds(proxy, proxy.naniteMesh);
                instance.localToWorld = matrix;
                instance.worldBounds = newBounds;
                changes.Add(new GIInstanceTransformChange(
                    instanceIndex, oldBounds, newBounds, true, true));
            }
            return changes.Count > 0;
        }

        static bool RequiresTransformTracking(Transform transform) =>
            transform.GetComponentInParent<Rigidbody>() != null ||
            transform.GetComponentInParent<GITransformTracked>() != null;

        static Bounds CalculateBounds(NaniteRuntimeProxy proxy, NaniteMesh mesh)
        {
            Matrix4x4 localToWorld = proxy.transform.localToWorldMatrix;
            Vector4 sphere = mesh.boundingSphere;
            Vector3 center = localToWorld.MultiplyPoint3x4(new Vector3(sphere.x, sphere.y, sphere.z));
            Vector3 scale = proxy.transform.lossyScale;
            float radius = sphere.w * Mathf.Max(Mathf.Abs(scale.x),
                Mathf.Max(Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
            return new Bounds(center, Vector3.one * (radius * 2f));
        }

        static bool MatrixApproximatelyEqual(Matrix4x4 a, Matrix4x4 b)
        {
            for (int index = 0; index < 16; index++)
            {
                if (Mathf.Abs(a[index] - b[index]) > 1e-5f)
                    return false;
            }
            return true;
        }

        static Material ResolveMaterial(NaniteRuntimeProxy proxy, NaniteMesh mesh)
        {
            if (proxy.resolveMaterials != null && proxy.resolveMaterials.Length > 0)
                return proxy.resolveMaterials[0];
            Renderer renderer = proxy.GetComponent<Renderer>();
            if (renderer != null && renderer.sharedMaterials.Length > 0)
                return renderer.sharedMaterials[0];
            return mesh.sourceMaterials != null && mesh.sourceMaterials.Length > 0
                ? mesh.sourceMaterials[0]
                : null;
        }

    }

    public readonly struct GINaniteRootPage
    {
        public readonly uint pageId;
        public readonly int triangleCount;
        public GINaniteRootPage(uint pageId, int triangleCount)
        {
            this.pageId = pageId;
            this.triangleCount = triangleCount;
        }
    }

    public sealed class GINaniteSceneInstance
    {
        public readonly NaniteRuntimeProxy proxy;
        public Matrix4x4 localToWorld;
        public Bounds worldBounds;
        public readonly uint material;
        public readonly uint emission;
        public readonly uint sourceId;
        public readonly GINaniteRootPage[] rootPages;

        public GINaniteSceneInstance(
            NaniteRuntimeProxy proxy, Bounds worldBounds, uint material, uint emission,
            uint sourceId, GINaniteRootPage[] rootPages)
        {
            this.proxy = proxy;
            localToWorld = proxy.transform.localToWorldMatrix;
            this.worldBounds = worldBounds;
            this.material = material;
            this.emission = emission;
            this.sourceId = sourceId;
            this.rootPages = rootPages ?? Array.Empty<GINaniteRootPage>();
        }
    }

    public readonly struct GINaniteFrameView
    {
        public readonly NaniteResidentPageReadOnlyView resident;
        public readonly int proxyCount;
        public readonly int instancePageCount;

        public GINaniteFrameView(
            NaniteResidentPageReadOnlyView resident,
            int proxyCount,
            int instancePageCount)
        {
            this.resident = resident;
            this.proxyCount = proxyCount;
            this.instancePageCount = instancePageCount;
        }

        public bool IsValid => resident.IsValid && proxyCount > 0;
    }
}
