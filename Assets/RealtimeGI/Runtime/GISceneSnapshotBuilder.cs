using System;
using System.Collections.Generic;
using Nanite;
using UnityEngine;

namespace RealtimeGI
{
    sealed class GISceneSnapshotBuilder
    {
        const uint MeshAdapterType = 0;
        const uint NaniteAdapterType = 1;
        const uint SkinnedMeshAdapterType = 2;

        readonly Dictionary<int, Matrix4x4> previousTransforms;
        readonly HashSet<int> observedObjects;
        readonly Dictionary<int, uint> geometryLookup = new Dictionary<int, uint>(256);
        readonly GIMaterialSlotTable materialTable;

        public readonly List<GIGpuInstanceData> instances = new List<GIGpuInstanceData>(256);
        public readonly List<GIGpuGeometryData> geometries = new List<GIGpuGeometryData>(256);
        public readonly List<GIGpuMaterialData> materials;
        public readonly List<GIGpuMaterialBindingData> materialBindings = new List<GIGpuMaterialBindingData>(512);
        public readonly List<UnityEngine.Object> geometrySources = new List<UnityEngine.Object>(256);
        public readonly List<Material> materialSources;
        public readonly List<GIMaterialBridge> materialBridges;

        public GISceneSnapshotBuilder(
            Dictionary<int, Matrix4x4> previousTransforms,
            HashSet<int> observedObjects,
            GIMaterialSlotTable materialTable)
        {
            this.previousTransforms = previousTransforms;
            this.observedObjects = observedObjects;
            this.materialTable = materialTable;
            materials = materialTable.Materials;
            materialSources = materialTable.Sources;
            materialBridges = materialTable.Bridges;
        }

        public void GatherOrdinaryMeshes()
        {
            IReadOnlyList<GIMeshInstance> sources = GISceneRegistry.MeshInstances;
            for (int i = 0; i < sources.Count; i++)
            {
                GIMeshInstance source = sources[i];
                if (source == null || !source.ShouldGather)
                    continue;

                Mesh mesh = source.SharedMesh;
                Material[] sourceMaterials = source.ResolveMaterials();
                GIObjectSettings settings = source.Settings;
                GIMaterialBridge materialBridge = source.GetComponent<GIMaterialBridge>();
                bool hasEmission = GIMaterialUtility.HasEmission(sourceMaterials, materialBridge);
                GIInstanceFlags flags = settings != null
                    ? settings.ResolveFlags(hasEmission)
                    : ResolveDefaultFlags(source.gameObject, hasEmission);

                uint geometryIndex = GetOrAddMeshGeometry(mesh, source.geometryRevision);
                AddInstance(
                    source.GetInstanceID(),
                    source.transform.localToWorldMatrix,
                    TransformSphere(mesh.bounds, source.transform.localToWorldMatrix),
                    geometryIndex,
                    flags,
                    MeshAdapterType,
                    settings != null ? settings.contentRevision : source.geometryRevision,
                    mesh.subMeshCount,
                    sourceMaterials,
                    materialBridge);
            }
        }

        public void GatherNaniteMeshes()
        {
            IReadOnlyList<NaniteRuntimeProxy> proxies = NaniteRuntimeRegistry.ActiveProxies;
            for (int i = 0; i < proxies.Count; i++)
            {
                NaniteRuntimeProxy proxy = proxies[i];
                if (proxy == null || !proxy.isActiveAndEnabled || proxy.naniteMesh == null)
                    continue;

                NaniteMesh mesh = proxy.naniteMesh;
                Material[] sourceMaterials = ResolveNaniteMaterials(proxy);
                GIObjectSettings settings = proxy.GetComponent<GIObjectSettings>();
                GIMaterialBridge materialBridge = proxy.GetComponent<GIMaterialBridge>();
                bool hasEmission = GIMaterialUtility.HasEmission(sourceMaterials, materialBridge);
                GIInstanceFlags flags = settings != null
                    ? settings.ResolveFlags(hasEmission)
                    : ResolveDefaultFlags(proxy.gameObject, hasEmission);

                uint geometryIndex = GetOrAddNaniteGeometry(mesh);
                AddInstance(
                    proxy.GetInstanceID(),
                    proxy.transform.localToWorldMatrix,
                    TransformSphere(mesh.boundingSphere, proxy.transform.localToWorldMatrix),
                    geometryIndex,
                    flags,
                    NaniteAdapterType,
                    settings != null ? settings.contentRevision : mesh.hierarchyVersion,
                    Mathf.Max(1, mesh.subMeshCount),
                    sourceMaterials,
                    materialBridge);
            }
        }

        public void GatherSkinnedMeshes()
        {
            IReadOnlyList<GISkinnedMeshInstance> sources = GISceneRegistry.SkinnedMeshInstances;
            for (int i = 0; i < sources.Count; i++)
            {
                GISkinnedMeshInstance source = sources[i];
                if (source == null || !source.TryGetBakedMesh(out Mesh mesh))
                    continue;
                Material[] sourceMaterials = source.ResolveMaterials();
                GIMaterialBridge materialBridge = source.GetComponent<GIMaterialBridge>();
                bool hasEmission = GIMaterialUtility.HasEmission(sourceMaterials, materialBridge);
                GIObjectSettings settings = source.Settings;
                GIInstanceFlags flags = settings != null
                    ? settings.ResolveFlags(hasEmission)
                    : ResolveDefaultFlags(source.gameObject, hasEmission);
                flags |= GIInstanceFlags.Dynamic;

                uint geometryIndex = GetOrAddMeshGeometry(mesh, source.GeometryRevision);
                AddInstance(
                    source.GetInstanceID(),
                    source.transform.localToWorldMatrix,
                    TransformSphere(mesh.bounds, source.transform.localToWorldMatrix),
                    geometryIndex,
                    flags,
                    SkinnedMeshAdapterType,
                    source.GeometryRevision,
                    mesh.subMeshCount,
                    sourceMaterials,
                    materialBridge);
            }
        }

        uint GetOrAddMeshGeometry(Mesh mesh, int revision)
        {
            int sourceId = mesh.GetInstanceID();
            if (geometryLookup.TryGetValue(sourceId, out uint existing))
                return existing;

            uint index = (uint)geometries.Count;
            geometryLookup.Add(sourceId, index);
            geometrySources.Add(mesh);
            Bounds bounds = mesh.bounds;
            geometries.Add(new GIGpuGeometryData
            {
                geometryId = index,
                kind = (uint)GIGeometryKind.UnityMesh,
                sourceObjectId = (uint)sourceId,
                localBoundingSphere = BoundsToSphere(bounds),
                vertexCount = (uint)Mathf.Max(0, mesh.vertexCount),
                indexCount = CountMeshIndices(mesh),
                subMeshCount = (uint)Mathf.Max(0, mesh.subMeshCount),
                sourceRevision = (uint)Mathf.Max(0, revision)
            });
            return index;
        }

        uint GetOrAddNaniteGeometry(NaniteMesh mesh)
        {
            int sourceId = mesh.GetInstanceID();
            if (geometryLookup.TryGetValue(sourceId, out uint existing))
                return existing;

            long indexCount = Math.Max(0L, (long)mesh.sourceTriangleCount * 3L);
            uint index = (uint)geometries.Count;
            geometryLookup.Add(sourceId, index);
            geometrySources.Add(mesh);
            geometries.Add(new GIGpuGeometryData
            {
                geometryId = index,
                kind = (uint)GIGeometryKind.NaniteMesh,
                sourceObjectId = (uint)sourceId,
                localBoundingSphere = mesh.boundingSphere,
                vertexCount = mesh.sourceMesh != null ? (uint)Mathf.Max(0, mesh.sourceMesh.vertexCount) : 0,
                indexCount = (uint)Math.Min(uint.MaxValue, indexCount),
                subMeshCount = (uint)Mathf.Max(0, mesh.subMeshCount),
                sourceRevision = (uint)Mathf.Max(0, mesh.hierarchyVersion)
            });
            return index;
        }

        void AddInstance(
            int objectId,
            Matrix4x4 currentTransform,
            Vector4 worldSphere,
            uint geometryIndex,
            GIInstanceFlags flags,
            uint adapterType,
            int revision,
            int subMeshCount,
            Material[] sourceMaterials,
            GIMaterialBridge materialBridge)
        {
            if (materialBridge != null && materialBridge.RequiresDynamicGeometry)
                flags |= GIInstanceFlags.Dynamic;
            uint instanceIndex = (uint)instances.Count;
            uint firstBinding = (uint)materialBindings.Count;
            int bindingCount = Mathf.Max(1, subMeshCount);
            for (int subMesh = 0; subMesh < bindingCount; subMesh++)
            {
                Material material = sourceMaterials != null && sourceMaterials.Length > 0
                    ? sourceMaterials[Mathf.Min(subMesh, sourceMaterials.Length - 1)]
                    : null;
                uint materialIndex = materialTable.GetOrAdd(material, materialBridge, objectId, subMesh);
                materialBindings.Add(new GIGpuMaterialBindingData
                {
                    instanceIndex = instanceIndex,
                    subMeshIndex = (uint)subMesh,
                    materialIndex = materialIndex
                });
            }

            Matrix4x4 previous = previousTransforms.TryGetValue(objectId, out Matrix4x4 value)
                ? value
                : currentTransform;
            observedObjects.Add(objectId);
            previousTransforms[objectId] = currentTransform;

            instances.Add(new GIGpuInstanceData
            {
                localToWorld = currentTransform,
                previousLocalToWorld = previous,
                worldBoundingSphere = worldSphere,
                objectId = (uint)objectId,
                geometryIndex = geometryIndex,
                firstMaterialBinding = firstBinding,
                materialBindingCount = (uint)bindingCount,
                flags = (uint)flags,
                adapterType = adapterType,
                revision = (uint)Mathf.Max(0, revision),
                // Persistent reservoirs must not reconnect a triangle at its old world
                // position after a dynamic instance moves. This occupies the former ABI
                // padding word, so the GPU stride remains unchanged.
                transformSignature = unchecked((uint)currentTransform.GetHashCode())
            });
        }

        static Material[] ResolveNaniteMaterials(NaniteRuntimeProxy proxy)
        {
            if (proxy.resolveMaterials != null && proxy.resolveMaterials.Length > 0)
                return proxy.resolveMaterials;
            Renderer renderer = proxy.GetComponent<Renderer>();
            return renderer != null ? renderer.sharedMaterials : Array.Empty<Material>();
        }

        static GIInstanceFlags ResolveDefaultFlags(GameObject owner, bool hasEmission)
        {
            GIInstanceFlags flags = GIInstanceFlags.Occluder |
                                    GIInstanceFlags.Contributor |
                                    GIInstanceFlags.Receiver;
            if (!owner.isStatic) flags |= GIInstanceFlags.Dynamic;
            if (hasEmission) flags |= GIInstanceFlags.Emissive;
            return flags;
        }

        static uint CountMeshIndices(Mesh mesh)
        {
            ulong count = 0;
            for (int i = 0; i < mesh.subMeshCount; i++)
                count += mesh.GetIndexCount(i);
            return (uint)Math.Min(uint.MaxValue, count);
        }

        static Vector4 BoundsToSphere(Bounds bounds)
        {
            float radius = bounds.extents.magnitude;
            return new Vector4(bounds.center.x, bounds.center.y, bounds.center.z, radius);
        }

        static Vector4 TransformSphere(Bounds localBounds, Matrix4x4 localToWorld)
        {
            Vector3 center = localToWorld.MultiplyPoint3x4(localBounds.center);
            Vector3 x = localToWorld.MultiplyVector(Vector3.right);
            Vector3 y = localToWorld.MultiplyVector(Vector3.up);
            Vector3 z = localToWorld.MultiplyVector(Vector3.forward);
            float maxScale = Mathf.Max(x.magnitude, Mathf.Max(y.magnitude, z.magnitude));
            float radius = localBounds.extents.magnitude * maxScale;
            return new Vector4(center.x, center.y, center.z, radius);
        }

        static Vector4 TransformSphere(Vector4 localSphere, Matrix4x4 localToWorld)
        {
            Vector3 center = localToWorld.MultiplyPoint3x4(
                new Vector3(localSphere.x, localSphere.y, localSphere.z));
            Vector3 x = localToWorld.MultiplyVector(Vector3.right);
            Vector3 y = localToWorld.MultiplyVector(Vector3.up);
            Vector3 z = localToWorld.MultiplyVector(Vector3.forward);
            float maxScale = Mathf.Max(x.magnitude, Mathf.Max(y.magnitude, z.magnitude));
            float radius = Mathf.Max(0f, localSphere.w) * maxScale;
            return new Vector4(center.x, center.y, center.z, radius);
        }
    }
}
