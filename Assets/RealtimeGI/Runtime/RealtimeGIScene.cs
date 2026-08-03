using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace RealtimeGI
{
    /// <summary>
    /// Builds the unified Nanite + ordinary Mesh scene snapshot. It intentionally owns no
    /// lighting or Clipmap policy; consumers obtain a transient GPU view via TryGetGpuView.
    /// </summary>
    [ExecuteAlways]
    [DefaultExecutionOrder(10000)]
    [DisallowMultipleComponent]
    public sealed class RealtimeGIScene : MonoBehaviour
    {
        const int MaxEmissiveAliasCount = 32;
        [Tooltip("Build and upload the scene once per LateUpdate. Disable when an external renderer feature drives BuildNow.")]
        public bool automaticUpdate = true;
        public bool includeOrdinaryMeshes = true;
        public bool includeSkinnedMeshes = true;
        public bool includeNaniteMeshes = true;

        [Header("Read-only statistics")]
        [SerializeField] int sceneRevision;
        [SerializeField] int instanceCount;
        [SerializeField] int geometryCount;
        [SerializeField] int materialCount;
        [SerializeField] int materialBindingCount;
        [SerializeField] int emissiveAliasCount;
        [SerializeField] int materialSlotOverflowCount;

        readonly Dictionary<int, Matrix4x4> previousTransforms = new Dictionary<int, Matrix4x4>(256);
        readonly HashSet<int> observedObjects = new HashSet<int>();
        readonly List<int> staleObjects = new List<int>(64);
        readonly List<Object> geometrySources = new List<Object>(256);
        readonly List<Material> materialSources = new List<Material>(256);
        readonly List<GIMaterialBridge> materialBridges = new List<GIMaterialBridge>(256);
        readonly List<GIGpuInstanceData> cpuInstances = new List<GIGpuInstanceData>(256);
        readonly List<GIGpuGeometryData> cpuGeometries = new List<GIGpuGeometryData>(256);
        readonly List<GIGpuMaterialData> cpuMaterials = new List<GIGpuMaterialData>(256);
        readonly List<GIGpuMaterialBindingData> cpuMaterialBindings = new List<GIGpuMaterialBindingData>(512);
        readonly List<GIGpuEmissiveAliasData> emissiveAliases = new List<GIGpuEmissiveAliasData>(64);
        readonly List<float> emissiveWeights = new List<float>(64);
        readonly List<float> aliasScaledWeights = new List<float>(64);
        readonly List<int> aliasSmall = new List<int>(64);
        readonly List<int> aliasLarge = new List<int>(64);
        GIMaterialSlotTable materialSlots;

        GraphicsBuffer instanceBuffer;
        GraphicsBuffer geometryBuffer;
        GraphicsBuffer materialBuffer;
        GraphicsBuffer materialBindingBuffer;
        GraphicsBuffer emissiveAliasBuffer;
        int instanceCapacity;
        int geometryCapacity;
        int materialCapacity;
        int materialBindingCapacity;
        int emissiveAliasCapacity;
        int lastBuildFrame = -1;
        int lastMaterialWarningFrame = -10000;

        public int SceneRevision => sceneRevision;
        public int InstanceCount => instanceCount;
        public int GeometryCount => geometryCount;
        public int MaterialCount => materialCount;
        public int MaterialBindingCount => materialBindingCount;
        public IReadOnlyList<GIGpuInstanceData> CpuInstances => cpuInstances;
        public IReadOnlyList<GIGpuGeometryData> CpuGeometries => cpuGeometries;
        public IReadOnlyList<GIGpuMaterialData> CpuMaterials => cpuMaterials;
        public IReadOnlyList<GIGpuMaterialBindingData> CpuMaterialBindings => cpuMaterialBindings;

        void LateUpdate()
        {
            if (automaticUpdate)
                BuildNow();
        }

        public void BuildNow()
        {
            if (!isActiveAndEnabled)
                return;
            GISceneAbi.Validate();
            // A RendererFeature and LateUpdate may both request a build. Upload at most once per frame.
            if (Application.isPlaying && lastBuildFrame == Time.frameCount)
                return;
            lastBuildFrame = Time.frameCount;

            observedObjects.Clear();
            if (materialSlots == null)
                materialSlots = new GIMaterialSlotTable();
            materialSlots.BeginFrame();
            var builder = new GISceneSnapshotBuilder(previousTransforms, observedObjects, materialSlots);
            if (includeOrdinaryMeshes) builder.GatherOrdinaryMeshes();
            if (includeSkinnedMeshes) builder.GatherSkinnedMeshes();
            if (includeNaniteMeshes) builder.GatherNaniteMeshes();
            RemoveStalePreviousTransforms();

            EnsureBuffer(ref instanceBuffer, ref instanceCapacity, builder.instances.Count, GISceneAbi.InstanceStride, "GI Instances");
            EnsureBuffer(ref geometryBuffer, ref geometryCapacity, builder.geometries.Count, GISceneAbi.GeometryStride, "GI Geometries");
            bool materialBufferReallocated = EnsureBuffer(
                ref materialBuffer, ref materialCapacity, builder.materials.Count,
                GISceneAbi.MaterialStride, "GI Materials");
            EnsureBuffer(ref materialBindingBuffer, ref materialBindingCapacity, builder.materialBindings.Count, GISceneAbi.MaterialBindingStride, "GI Material Bindings");
            BuildEmissiveAliasTable(builder.instances, builder.materials, builder.materialBindings);
            EnsureBuffer(ref emissiveAliasBuffer, ref emissiveAliasCapacity, emissiveAliases.Count,
                GISceneAbi.EmissiveAliasStride, "GI Emissive Instance Alias Table");

            if (builder.instances.Count > 0) instanceBuffer.SetData(builder.instances, 0, 0, builder.instances.Count);
            if (builder.geometries.Count > 0) geometryBuffer.SetData(builder.geometries, 0, 0, builder.geometries.Count);
            UploadChangedMaterials(builder.materials, materialBufferReallocated);
            if (builder.materialBindings.Count > 0) materialBindingBuffer.SetData(builder.materialBindings, 0, 0, builder.materialBindings.Count);
            if (emissiveAliases.Count > 0)
                emissiveAliasBuffer.SetData(emissiveAliases, 0, 0, emissiveAliases.Count);

            geometrySources.Clear();
            geometrySources.AddRange(builder.geometrySources);
            materialSources.Clear();
            materialSources.AddRange(builder.materialSources);
            materialBridges.Clear();
            materialBridges.AddRange(builder.materialBridges);
            cpuInstances.Clear();
            cpuInstances.AddRange(builder.instances);
            cpuGeometries.Clear();
            cpuGeometries.AddRange(builder.geometries);
            cpuMaterials.Clear();
            cpuMaterials.AddRange(builder.materials);
            cpuMaterialBindings.Clear();
            cpuMaterialBindings.AddRange(builder.materialBindings);

            instanceCount = builder.instances.Count;
            geometryCount = builder.geometries.Count;
            materialCount = builder.materials.Count;
            materialBindingCount = builder.materialBindings.Count;
            emissiveAliasCount = emissiveAliases.Count;
            materialSlotOverflowCount = materialSlots.OverflowCount;
            if (materialSlotOverflowCount > 0 && Time.frameCount - lastMaterialWarningFrame >= 120)
            {
                lastMaterialWarningFrame = Time.frameCount;
                Debug.LogWarning(
                    $"[RealtimeGI] Stable material slots exhausted ({GIMaterialSlotTable.MaxSlotCount}). " +
                    $"{materialSlotOverflowCount} bindings use fallback slot 0. Reduce unique per-renderer overrides.", this);
            }
            sceneRevision++;
        }

        public bool TryGetGpuView(out GISceneGpuView view)
        {
            if (!isActiveAndEnabled)
            {
                view = default;
                return false;
            }
            if (lastBuildFrame < 0)
                BuildNow();
            view = new GISceneGpuView(
                instanceBuffer,
                geometryBuffer,
                materialBuffer,
                materialBindingBuffer,
                emissiveAliasBuffer,
                instanceCount,
                geometryCount,
                materialCount,
                materialBindingCount,
                emissiveAliasCount,
                sceneRevision);
            return view.IsValid;
        }

        public Object GetGeometrySource(int geometryIndex)
        {
            return geometryIndex >= 0 && geometryIndex < geometrySources.Count
                ? geometrySources[geometryIndex]
                : null;
        }

        public Material GetMaterialSource(int materialIndex)
        {
            return materialIndex >= 0 && materialIndex < materialSources.Count
                ? materialSources[materialIndex]
                : null;
        }

        public GIMaterialBridge GetMaterialBridgeSource(int materialIndex)
        {
            return materialIndex >= 0 && materialIndex < materialBridges.Count
                ? materialBridges[materialIndex]
                : null;
        }

        void RemoveStalePreviousTransforms()
        {
            staleObjects.Clear();
            foreach (int objectId in previousTransforms.Keys)
            {
                if (!observedObjects.Contains(objectId))
                    staleObjects.Add(objectId);
            }
            for (int i = 0; i < staleObjects.Count; i++)
                previousTransforms.Remove(staleObjects[i]);
        }

        void UploadChangedMaterials(List<GIGpuMaterialData> next, bool uploadAll)
        {
            if (next.Count == 0)
                return;
            if (uploadAll)
            {
                materialBuffer.SetData(next, 0, 0, next.Count);
                return;
            }

            int index = 0;
            while (index < next.Count)
            {
                bool changed = index >= cpuMaterials.Count || !MaterialEquals(cpuMaterials[index], next[index]);
                if (!changed)
                {
                    index++;
                    continue;
                }
                int first = index++;
                while (index < next.Count &&
                       (index >= cpuMaterials.Count || !MaterialEquals(cpuMaterials[index], next[index])))
                    index++;
                materialBuffer.SetData(next, first, first, index - first);
            }
        }

        static bool MaterialEquals(GIGpuMaterialData a, GIGpuMaterialData b) =>
            a.revision == b.revision && a.materialId == b.materialId && a.flags == b.flags &&
            a.textureIndex == b.textureIndex && a.baseColor.Equals(b.baseColor) &&
            a.emissive.Equals(b.emissive) && a.surface.Equals(b.surface) &&
            a.baseMapST.Equals(b.baseMapST) && a.emissionMapST.Equals(b.emissionMapST);

        void BuildEmissiveAliasTable(
            List<GIGpuInstanceData> instances,
            List<GIGpuMaterialData> materials,
            List<GIGpuMaterialBindingData> bindings)
        {
            emissiveAliases.Clear();
            emissiveWeights.Clear();
            float totalWeight = 0f;
            for (int instanceIndex = 0; instanceIndex < instances.Count; instanceIndex++)
            {
                GIGpuInstanceData instance = instances[instanceIndex];
                if ((instance.flags & (uint)GIInstanceFlags.Emissive) == 0u)
                    continue;
                float emission = 0f;
                int first = (int)instance.firstMaterialBinding;
                int end = Mathf.Min(bindings.Count, first + (int)instance.materialBindingCount);
                for (int bindingIndex = first; bindingIndex < end; bindingIndex++)
                {
                    int materialIndex = (int)bindings[bindingIndex].materialIndex;
                    if (materialIndex < 0 || materialIndex >= materials.Count)
                        continue;
                    Vector4 e = materials[materialIndex].emissive;
                    emission = Mathf.Max(emission, Mathf.Max(0f,
                        e.x * 0.2126f + e.y * 0.7152f + e.z * 0.0722f));
                }
                if (emission <= 1e-5f)
                    continue;
                float radius = Mathf.Max(0.05f, instance.worldBoundingSphere.w);
                float weight = Mathf.Max(1e-4f, emission * radius * radius);
                emissiveAliases.Add(new GIGpuEmissiveAliasData
                {
                    worldBoundingSphere = instance.worldBoundingSphere,
                    probability = 0f,
                    aliasProbability = 1f,
                    aliasIndex = (uint)emissiveAliases.Count,
                    objectId = instance.objectId
                });
                emissiveWeights.Add(weight);
                totalWeight += weight;
            }

            // The screen gather evaluates the complete mixture PDF. Keep that bounded and
            // retain the sources with the largest projected-power proxy.
            while (emissiveAliases.Count > MaxEmissiveAliasCount)
            {
                int weakest = 0;
                for (int i = 1; i < emissiveWeights.Count; i++)
                    if (emissiveWeights[i] < emissiveWeights[weakest]) weakest = i;
                totalWeight -= emissiveWeights[weakest];
                emissiveWeights.RemoveAt(weakest);
                emissiveAliases.RemoveAt(weakest);
            }

            int count = emissiveAliases.Count;
            if (count == 0 || totalWeight <= 0f)
                return;
            aliasScaledWeights.Clear();
            aliasSmall.Clear();
            aliasLarge.Clear();
            for (int i = 0; i < count; i++)
            {
                float probability = emissiveWeights[i] / totalWeight;
                GIGpuEmissiveAliasData entry = emissiveAliases[i];
                entry.probability = probability;
                emissiveAliases[i] = entry;
                float scaled = probability * count;
                aliasScaledWeights.Add(scaled);
                (scaled < 1f ? aliasSmall : aliasLarge).Add(i);
            }
            while (aliasSmall.Count > 0 && aliasLarge.Count > 0)
            {
                int smallLast = aliasSmall.Count - 1;
                int largeLast = aliasLarge.Count - 1;
                int small = aliasSmall[smallLast];
                int large = aliasLarge[largeLast];
                aliasSmall.RemoveAt(smallLast);
                aliasLarge.RemoveAt(largeLast);
                GIGpuEmissiveAliasData entry = emissiveAliases[small];
                entry.aliasProbability = aliasScaledWeights[small];
                entry.aliasIndex = (uint)large;
                emissiveAliases[small] = entry;
                aliasScaledWeights[large] = aliasScaledWeights[large] +
                    aliasScaledWeights[small] - 1f;
                (aliasScaledWeights[large] < 1f ? aliasSmall : aliasLarge).Add(large);
            }
            for (int i = 0; i < aliasSmall.Count; i++)
            {
                int index = aliasSmall[i];
                GIGpuEmissiveAliasData entry = emissiveAliases[index];
                entry.aliasProbability = 1f;
                entry.aliasIndex = (uint)index;
                emissiveAliases[index] = entry;
            }
            for (int i = 0; i < aliasLarge.Count; i++)
            {
                int index = aliasLarge[i];
                GIGpuEmissiveAliasData entry = emissiveAliases[index];
                entry.aliasProbability = 1f;
                entry.aliasIndex = (uint)index;
                emissiveAliases[index] = entry;
            }
        }

        static bool EnsureBuffer(
            ref GraphicsBuffer buffer,
            ref int capacity,
            int requiredCount,
            int stride,
            string name)
        {
            int requiredCapacity = Mathf.NextPowerOfTwo(Mathf.Max(1, requiredCount));
            if (buffer != null && capacity >= requiredCapacity)
                return false;
            buffer?.Release();
            capacity = requiredCapacity;
            buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity, stride)
            {
                name = name
            };
            return true;
        }

        void OnDisable()
        {
            ReleaseBuffers();
            lastBuildFrame = -1;
        }

        void OnDestroy()
        {
            ReleaseBuffers();
        }

        void ReleaseBuffers()
        {
            instanceBuffer?.Release();
            geometryBuffer?.Release();
            materialBuffer?.Release();
            materialBindingBuffer?.Release();
            emissiveAliasBuffer?.Release();
            instanceBuffer = null;
            geometryBuffer = null;
            materialBuffer = null;
            materialBindingBuffer = null;
            emissiveAliasBuffer = null;
            geometrySources.Clear();
            materialSources.Clear();
            materialBridges.Clear();
            cpuInstances.Clear();
            cpuGeometries.Clear();
            cpuMaterials.Clear();
            cpuMaterialBindings.Clear();
            emissiveAliases.Clear();
            instanceCapacity = geometryCapacity = materialCapacity = materialBindingCapacity = 0;
            emissiveAliasCapacity = 0;
            materialSlots?.Clear();
            materialSlots = null;
        }
    }
}
