using System;
using System.Collections.Generic;
using Nanite;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityNanite.GI
{
    /// <summary>
    /// Ordinary-Mesh geometry and instances for the unified world cache. Mesh/submesh
    /// topology is stored once in local space; rigid motion only changes an instance.
    /// </summary>
    public sealed class GIMeshGeometryCache : IDisposable
    {
        readonly List<GIWorldTriangle> triangles = new List<GIWorldTriangle>(32768);
        readonly List<GIMeshSceneInstance> instances = new List<GIMeshSceneInstance>(512);
        readonly List<int> trackedInstanceIndices = new List<int>(128);
        readonly Dictionary<GeometryKey, GeometryRange> geometryRanges =
            new Dictionary<GeometryKey, GeometryRange>();
        GraphicsBuffer triangleBuffer;

        public GraphicsBuffer TriangleBuffer => triangleBuffer;
        public IReadOnlyList<GIWorldTriangle> Triangles => triangles;
        public IReadOnlyList<GIMeshSceneInstance> Instances => instances;

        public bool BuildScene()
        {
            triangles.Clear();
            instances.Clear();
            trackedInstanceIndices.Clear();
            geometryRanges.Clear();
            int candidateCount = 0;
            int naniteCount = 0;
            int unreadableCount = 0;
            int transparentCount = 0;

            MeshRenderer[] renderers = UnityEngine.Object.FindObjectsByType<MeshRenderer>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            uint sourceId = 1;
            foreach (MeshRenderer renderer in renderers)
            {
                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy ||
                    !renderer.TryGetComponent(out MeshFilter filter) || filter.sharedMesh == null)
                    continue;
                candidateCount++;
                if (renderer.TryGetComponent(out NaniteRuntimeProxy _))
                {
                    naniteCount++;
                    continue;
                }

                Mesh mesh = filter.sharedMesh;
                if (!mesh.isReadable)
                {
                    unreadableCount++;
                    continue;
                }

                Material[] materials = renderer.sharedMaterials;
                int subMeshCount = Mathf.Min(mesh.subMeshCount, materials.Length);
                for (int subMesh = 0; subMesh < subMeshCount; subMesh++)
                {
                    Material material = materials[subMesh];
                    if (!IsOpaque(material))
                    {
                        transparentCount++;
                        continue;
                    }

                    var key = new GeometryKey(mesh.GetInstanceID(), subMesh);
                    if (!geometryRanges.TryGetValue(key, out GeometryRange range))
                    {
                        range = AppendGeometry(mesh, subMesh);
                        geometryRanges.Add(key, range);
                    }
                    if (range.count == 0)
                        continue;

                    var instance = new GIMeshSceneInstance(
                        renderer, range.start, range.count,
                        GIWorldMaterialPacking.PackBaseColor(material),
                        GIWorldMaterialPacking.PackEmission(material), sourceId,
                        material != null && material.doubleSidedGI ? 1u : 0u);
                    instances.Add(instance);
                    if (RequiresTransformTracking(renderer.transform))
                        trackedInstanceIndices.Add(instances.Count - 1);
                }
                sourceId++;
            }

            ReleaseBuffer();
            int bufferCount = Mathf.Max(1, triangles.Count);
            triangleBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured, bufferCount, GIWorldConstants.TriangleStride)
            {
                name = triangles.Count > 0 ? "GI Mesh Local Triangles" : "GI Empty Mesh Triangle Buffer"
            };
            triangleBuffer.SetData(triangles.Count > 0 ? triangles : new List<GIWorldTriangle> { default });
            Debug.Log(
                $"[GI][Node2] Ordinary Mesh scene: instances={instances.Count}, " +
                $"trackedTransforms={trackedInstanceIndices.Count}, " +
                $"staticInstances={instances.Count - trackedInstanceIndices.Count}, " +
                $"sharedGeometry={geometryRanges.Count}, " +
                $"triangles={triangles.Count}, candidates={candidateCount}, naniteSkipped={naniteCount}, " +
                $"unreadableSkipped={unreadableCount}, transparentSubmeshes={transparentCount}.");
            return true;
        }

        GeometryRange AppendGeometry(Mesh mesh, int subMesh)
        {
            int start = triangles.Count;
            Vector3[] vertices = mesh.vertices;
            int[] indices = mesh.GetIndices(subMesh, true);
            for (int index = 0; index + 2 < indices.Length; index += 3)
            {
                Vector3 p0 = vertices[indices[index]];
                Vector3 p1 = vertices[indices[index + 1]];
                Vector3 p2 = vertices[indices[index + 2]];
                Vector3 cross = Vector3.Cross(p1 - p0, p2 - p0);
                if (cross.sqrMagnitude < 1e-12f)
                    continue;
                triangles.Add(new GIWorldTriangle
                {
                    position0 = p0,
                    position1 = p1,
                    position2 = p2,
                    normal = cross.normalized
                });
            }
            return new GeometryRange(start, triangles.Count - start);
        }

        public bool PollTransformChanges(List<GIInstanceTransformChange> changes)
        {
            changes.Clear();
            for (int trackedIndex = 0; trackedIndex < trackedInstanceIndices.Count; trackedIndex++)
            {
                int instanceIndex = trackedInstanceIndices[trackedIndex];
                GIMeshSceneInstance instance = instances[instanceIndex];
                bool active = instance.renderer != null && instance.renderer.enabled &&
                              instance.renderer.gameObject.activeInHierarchy;
                Matrix4x4 matrix = active ? instance.renderer.localToWorldMatrix : instance.localToWorld;
                Bounds bounds = active ? instance.renderer.bounds : instance.worldBounds;
                if (active == instance.active && (!active || MatrixApproximatelyEqual(matrix, instance.localToWorld)))
                    continue;

                Bounds oldBounds = instance.worldBounds;
                bool hadOldBounds = instance.active;
                instance.localToWorld = matrix;
                instance.worldBounds = bounds;
                instance.active = active;
                changes.Add(new GIInstanceTransformChange(
                    instanceIndex, oldBounds, bounds, hadOldBounds, active));
            }
            return changes.Count > 0;
        }

        static bool RequiresTransformTracking(Transform transform) =>
            transform.GetComponentInParent<Rigidbody>() != null ||
            transform.GetComponentInParent<GITransformTracked>() != null;

        static bool MatrixApproximatelyEqual(Matrix4x4 a, Matrix4x4 b)
        {
            for (int index = 0; index < 16; index++)
            {
                if (Mathf.Abs(a[index] - b[index]) > 1e-5f)
                    return false;
            }
            return true;
        }

        static bool IsOpaque(Material material) =>
            material != null && (!material.HasProperty("_Surface") || material.GetFloat("_Surface") < 0.5f);

        void ReleaseBuffer()
        {
            triangleBuffer?.Dispose();
            triangleBuffer = null;
        }

        public void Dispose() => ReleaseBuffer();

        readonly struct GeometryKey : IEquatable<GeometryKey>
        {
            readonly int meshId;
            readonly int subMesh;
            public GeometryKey(int meshId, int subMesh)
            {
                this.meshId = meshId;
                this.subMesh = subMesh;
            }
            public bool Equals(GeometryKey other) => meshId == other.meshId && subMesh == other.subMesh;
            public override bool Equals(object obj) => obj is GeometryKey other && Equals(other);
            public override int GetHashCode() => HashCode.Combine(meshId, subMesh);
        }

        readonly struct GeometryRange
        {
            public readonly int start;
            public readonly int count;
            public GeometryRange(int start, int count)
            {
                this.start = start;
                this.count = count;
            }
        }
    }

    public sealed class GIMeshSceneInstance
    {
        public readonly MeshRenderer renderer;
        public readonly int triangleStart;
        public readonly int triangleCount;
        public readonly uint material;
        public readonly uint emission;
        public readonly uint sourceId;
        public readonly uint flags;
        public Matrix4x4 localToWorld;
        public Bounds worldBounds;
        public bool active;

        public GIMeshSceneInstance(
            MeshRenderer renderer, int triangleStart, int triangleCount,
            uint material, uint emission, uint sourceId, uint flags)
        {
            this.renderer = renderer;
            this.triangleStart = triangleStart;
            this.triangleCount = triangleCount;
            this.material = material;
            this.emission = emission;
            this.sourceId = sourceId;
            this.flags = flags;
            localToWorld = renderer.localToWorldMatrix;
            worldBounds = renderer.bounds;
            active = true;
        }

        public GIMeshGpuInstance ToGpu() => new GIMeshGpuInstance
        {
            localToWorld = localToWorld,
            material = material,
            sourceId = sourceId,
            flags = flags,
            reserved = emission
        };
    }

    public readonly struct GIInstanceTransformChange
    {
        public readonly int instanceIndex;
        public readonly Bounds oldBounds;
        public readonly Bounds newBounds;
        public readonly bool hadOldBounds;
        public readonly bool hasNewBounds;

        public GIInstanceTransformChange(
            int instanceIndex, Bounds oldBounds, Bounds newBounds,
            bool hadOldBounds, bool hasNewBounds)
        {
            this.instanceIndex = instanceIndex;
            this.oldBounds = oldBounds;
            this.newBounds = newBounds;
            this.hadOldBounds = hadOldBounds;
            this.hasNewBounds = hasNewBounds;
        }
    }
}
