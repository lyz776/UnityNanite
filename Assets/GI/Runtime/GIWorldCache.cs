using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace UnityNanite.GI
{
    public sealed class GIWorldCache : IDisposable
    {
        readonly GIMeshGeometryCache geometry = new GIMeshGeometryCache();
        readonly GINaniteAdapter nanite = new GINaniteAdapter();
        readonly GIWorldLevelData[] levels = new GIWorldLevelData[GIWorldConstants.LevelCount];
        readonly int[] pageTable = new int[GIWorldConstants.PageTableEntries];
        readonly GIWorldBrickData[] bricks = new GIWorldBrickData[GIWorldConstants.PageTableEntries];
        readonly HashSet<WorldBrickKey>[] requiredBricks = new HashSet<WorldBrickKey>[GIWorldConstants.LevelCount];
        readonly Dictionary<WorldBrickKey, List<int>> meshInstancesByBrick =
            new Dictionary<WorldBrickKey, List<int>>();
        readonly Dictionary<WorldBrickKey, List<int>> naniteInstancesByBrick =
            new Dictionary<WorldBrickKey, List<int>>();
        readonly Dictionary<WorldBrickKey, int> physicalByWorldBrick =
            new Dictionary<WorldBrickKey, int>(GIWorldConstants.PageTableEntries);
        readonly HashSet<WorldBrickKey> desiredBricks =
            new HashSet<WorldBrickKey>();
        readonly List<WorldBrickKey> releasedBricks =
            new List<WorldBrickKey>(GIWorldConstants.PageTableEntries);
        readonly List<int> freePhysicalBricks =
            new List<int>(GIWorldConstants.PageTableEntries);
        readonly List<int> dirtyPhysicalBricks =
            new List<int>(GIWorldConstants.PageTableEntries);
        readonly HashSet<int> dirtyPhysicalSet = new HashSet<int>();
        readonly HashSet<WorldBrickKey> transformDirtyBricks = new HashSet<WorldBrickKey>();
        readonly List<GIInstanceTransformChange> meshTransformChanges =
            new List<GIInstanceTransformChange>(64);
        readonly List<GIInstanceTransformChange> naniteTransformChanges =
            new List<GIInstanceTransformChange>(64);
        readonly List<GIWorldTriangleWork> triangleWork = new List<GIWorldTriangleWork>(32768);
        readonly List<GIMeshGpuInstance> meshGpuInstances = new List<GIMeshGpuInstance>(512);
        readonly List<GINaniteGpuInstance> naniteGpuInstances = new List<GINaniteGpuInstance>(512);
        readonly List<GINaniteTriangleWork> naniteTriangleWork = new List<GINaniteTriangleWork>(32768);

        GraphicsBuffer levelBuffer;
        GraphicsBuffer pageTableBuffer;
        GraphicsBuffer brickBuffer;
        GraphicsBuffer winnerBuffer;
        GraphicsBuffer occupancyBuffer;
        GraphicsBuffer surfaceBuffer;
        GraphicsBuffer distanceBuffer;
        GraphicsBuffer dirtyBrickBuffer;
        GraphicsBuffer triangleWorkBuffer;
        GraphicsBuffer meshInstanceBuffer;
        GraphicsBuffer naniteInstanceBuffer;
        GraphicsBuffer naniteTriangleWorkBuffer;
        GraphicsBuffer naniteDiagnosticsBuffer;
        int triangleWorkCapacity;
        int meshInstanceCapacity;
        int naniteInstanceCapacity;
        int naniteTriangleWorkCapacity;
        GINaniteFrameView naniteFrame;
        IReadOnlyList<GINaniteSceneInstance> naniteSceneInstances;
        Vector3Int[] previousOrigins = new Vector3Int[GIWorldConstants.LevelCount];
        bool initialized;
        bool buildPending;
        uint generation;
        uint nanitePoolGeneration;
        ulong naniteTableEpoch;
        bool naniteTopologyReady;
        int naniteRegistryRevision = -1;
        float lastMappingLogTime = float.NegativeInfinity;
        float lastNaniteDiagnosticsTime = float.NegativeInfinity;
        float lastRigidLogTime = float.NegativeInfinity;
        bool naniteDiagnosticsPending;

        public bool Prepare(Camera camera)
        {
            if (camera == null)
                return false;
            bool naniteTopologyChanged = false;
            bool naniteResidencyChanged = false;
            transformDirtyBricks.Clear();
            if (!initialized)
            {
                naniteTopologyReady = nanite.TryCollectScene(
                    out naniteFrame, out naniteSceneInstances);
                if (naniteTopologyReady)
                    naniteRegistryRevision = nanite.SceneRevision;
                if (!geometry.BuildScene())
                    return false;
                BuildMeshBrickIndex();
                BuildNaniteBrickIndex();
                InitializePhysicalPool();
                initialized = true;
                for (int index = 0; index < previousOrigins.Length; index++)
                    previousOrigins[index] = new Vector3Int(int.MinValue, int.MinValue, int.MinValue);
            }
            else
            {
                bool hasNaniteView = nanite.TryAcquire(out naniteFrame);
                // Nanite normally publishes its resident front table after GI's first
                // AddRenderPasses call. Collect topology as soon as that table becomes ready,
                // and again only when proxy membership/render data changes.
                if (hasNaniteView &&
                    (!naniteTopologyReady || naniteRegistryRevision != nanite.SceneRevision) &&
                    nanite.TryCollectScene(out naniteFrame, out naniteSceneInstances))
                {
                    foreach (WorldBrickKey key in naniteInstancesByBrick.Keys)
                        transformDirtyBricks.Add(key);
                    BuildNaniteBrickIndex();
                    foreach (WorldBrickKey key in naniteInstancesByBrick.Keys)
                        transformDirtyBricks.Add(key);
                    RefreshRequiredBricks(transformDirtyBricks);
                    naniteTopologyReady = true;
                    naniteRegistryRevision = nanite.SceneRevision;
                    naniteTopologyChanged = true;
                }
                else if (!naniteTopologyChanged && naniteFrame.IsValid &&
                    (naniteFrame.resident.poolGeneration != nanitePoolGeneration ||
                     naniteFrame.resident.tableEpoch != naniteTableEpoch))
                {
                    naniteResidencyChanged = true;
                }
            }
            bool meshTransformsChanged = geometry.PollTransformChanges(meshTransformChanges);
            if (meshTransformsChanged)
                ApplyMeshTransformChanges();
            bool naniteTransformsChanged = naniteTopologyReady &&
                                           nanite.PollTransformChanges(naniteTransformChanges);
            if (naniteTransformsChanged)
                ApplyNaniteTransformChanges();
            bool transformsChanged = meshTransformsChanged || naniteTransformsChanged ||
                                     transformDirtyBricks.Count > 0;
            if (naniteFrame.IsValid)
            {
                nanitePoolGeneration = naniteFrame.resident.poolGeneration;
                naniteTableEpoch = naniteFrame.resident.tableEpoch;
            }

            bool originsChanged = false;
            for (int level = 0; level < GIWorldConstants.LevelCount; level++)
            {
                float cellSize = GIWorldConstants.CellSizes[level];
                float brickSize = cellSize * GIWorldConstants.BrickSize;
                Vector3 position = camera.transform.position / brickSize;
                Vector3Int center = new Vector3Int(
                    Mathf.FloorToInt(position.x),
                    Mathf.FloorToInt(position.y),
                    Mathf.FloorToInt(position.z));
                Vector3Int origin = center - Vector3Int.one * (GIWorldConstants.PageTableAxis / 2);
                levels[level] = new GIWorldLevelData
                {
                    originBrick = origin,
                    worldOrigin = (Vector3)(origin * GIWorldConstants.BrickSize) * cellSize,
                    cellSize = cellSize,
                    pageTableOffset = (uint)(level * GIWorldConstants.PagesPerLevel)
                };
                if (origin != previousOrigins[level])
                {
                    originsChanged = true;
                    previousOrigins[level] = origin;
                }
            }

            if (!originsChanged && !naniteTopologyChanged && !naniteResidencyChanged &&
                !transformsChanged && BuffersValid())
                return true;

            generation++;
            long rebuildStart = System.Diagnostics.Stopwatch.GetTimestamp();
            RebuildPageMapping(transformDirtyBricks);
            AllocateBuffersOnce();
            // A newly published Nanite topology may overlap already-retained ordinary-Mesh
            // bricks. Rebuild every active brick once so those cells receive both sources.
            if (naniteTopologyChanged || naniteResidencyChanged)
                MarkAllActiveBricksDirty();
            UploadMapping(rebuildStart);
            buildPending = dirtyPhysicalBricks.Count > 0;
            double rigidMilliseconds =
                (System.Diagnostics.Stopwatch.GetTimestamp() - rebuildStart) * 1000.0 /
                System.Diagnostics.Stopwatch.Frequency;
            bool rigidTransformsChanged = meshTransformsChanged || naniteTransformsChanged;
            if (rigidTransformsChanged && Time.realtimeSinceStartup - lastRigidLogTime >= 0.5f)
            {
                lastRigidLogTime = Time.realtimeSinceStartup;
                Debug.Log(
                    $"[GI][Node2] Rigid update: mesh={meshTransformChanges.Count}, " +
                    $"nanite={naniteTransformChanges.Count}, dirtyBricks={dirtyPhysicalBricks.Count}, " +
                    $"generation={generation}, cpu={rigidMilliseconds:F2}ms.");
            }
            return BuffersValid();
        }

        void BuildMeshBrickIndex()
        {
            for (int level = 0; level < requiredBricks.Length; level++)
                requiredBricks[level] = new HashSet<WorldBrickKey>();
            meshInstancesByBrick.Clear();
            meshGpuInstances.Clear();
            IReadOnlyList<GIMeshSceneInstance> instances = geometry.Instances;
            for (int instanceIndex = 0; instanceIndex < instances.Count; instanceIndex++)
            {
                GIMeshSceneInstance instance = instances[instanceIndex];
                meshGpuInstances.Add(instance.ToGpu());
                if (instance.active)
                    AddInstanceCoverage(meshInstancesByBrick, instanceIndex, instance.worldBounds, null);
            }
            foreach (WorldBrickKey key in meshInstancesByBrick.Keys)
                requiredBricks[key.level].Add(key);
        }

        void InitializePhysicalPool()
        {
            Array.Fill(pageTable, -1);
            freePhysicalBricks.Clear();
            for (int physical = GIWorldConstants.PageTableEntries - 1; physical >= 0; physical--)
                freePhysicalBricks.Add(physical);
        }

        void MarkAllActiveBricksDirty()
        {
            dirtyPhysicalBricks.Clear();
            dirtyPhysicalSet.Clear();
            foreach (int physical in physicalByWorldBrick.Values)
            {
                GIWorldBrickData brick = bricks[physical];
                brick.generation = generation;
                bricks[physical] = brick;
                AddDirtyPhysical(physical);
            }
            if (dirtyPhysicalBricks.Count == 0)
                return;
            RebuildWorkForDirtyBricks();
            brickBuffer.SetData(bricks);
            dirtyBrickBuffer.SetData(dirtyPhysicalBricks);
            EnsureTriangleWorkBuffer();
            if (triangleWork.Count > 0)
                triangleWorkBuffer.SetData(triangleWork);
            EnsureMeshInstanceBuffer();
            if (meshGpuInstances.Count > 0)
                meshInstanceBuffer.SetData(meshGpuInstances);
            EnsureNaniteBuffers();
            if (naniteTriangleWork.Count > 0)
                naniteTriangleWorkBuffer.SetData(naniteTriangleWork);
        }

        void BuildNaniteBrickIndex()
        {
            // requiredBricks is the union of immutable ordinary-Mesh coverage and the
            // current Nanite topology. Remove the previous Nanite contribution first.
            for (int level = 0; level < requiredBricks.Length; level++)
                requiredBricks[level].Clear();
            foreach (WorldBrickKey key in meshInstancesByBrick.Keys)
                requiredBricks[key.level].Add(key);
            naniteInstancesByBrick.Clear();
            naniteGpuInstances.Clear();
            if (naniteSceneInstances == null)
                return;
            for (int instanceIndex = 0; instanceIndex < naniteSceneInstances.Count; instanceIndex++)
            {
                GINaniteSceneInstance instance = naniteSceneInstances[instanceIndex];
                naniteGpuInstances.Add(new GINaniteGpuInstance
                {
                    localToWorld = instance.localToWorld,
                    material = instance.material,
                    sourceId = instance.sourceId,
                    reserved0 = instance.emission
                });
                AddInstanceCoverage(naniteInstancesByBrick, instanceIndex, instance.worldBounds, null);
            }
            foreach (WorldBrickKey key in naniteInstancesByBrick.Keys)
                requiredBricks[key.level].Add(key);
        }

        void ApplyMeshTransformChanges()
        {
            IReadOnlyList<GIMeshSceneInstance> instances = geometry.Instances;
            for (int index = 0; index < meshTransformChanges.Count; index++)
            {
                GIInstanceTransformChange change = meshTransformChanges[index];
                if (change.hadOldBounds)
                    RemoveInstanceCoverage(
                        meshInstancesByBrick, change.instanceIndex, change.oldBounds, transformDirtyBricks);
                if (change.hasNewBounds)
                    AddInstanceCoverage(
                        meshInstancesByBrick, change.instanceIndex, change.newBounds, transformDirtyBricks);
                meshGpuInstances[change.instanceIndex] = instances[change.instanceIndex].ToGpu();
            }
            RefreshRequiredBricks(transformDirtyBricks);
        }

        void ApplyNaniteTransformChanges()
        {
            for (int index = 0; index < naniteTransformChanges.Count; index++)
            {
                GIInstanceTransformChange change = naniteTransformChanges[index];
                RemoveInstanceCoverage(
                    naniteInstancesByBrick, change.instanceIndex, change.oldBounds, transformDirtyBricks);
                AddInstanceCoverage(
                    naniteInstancesByBrick, change.instanceIndex, change.newBounds, transformDirtyBricks);
                GINaniteSceneInstance instance = naniteSceneInstances[change.instanceIndex];
                naniteGpuInstances[change.instanceIndex] = new GINaniteGpuInstance
                {
                    localToWorld = instance.localToWorld,
                    material = instance.material,
                    sourceId = instance.sourceId,
                    reserved0 = instance.emission
                };
            }
            RefreshRequiredBricks(transformDirtyBricks);
        }

        static void AddInstanceCoverage(
            Dictionary<WorldBrickKey, List<int>> index, int instanceIndex,
            Bounds bounds, HashSet<WorldBrickKey> changed)
        {
            for (int level = 0; level < GIWorldConstants.LevelCount; level++)
            {
                float cellSize = GIWorldConstants.CellSizes[level];
                float brickSize = cellSize * GIWorldConstants.BrickSize;
                Vector3Int minBrick = FloorToInt((bounds.min - Vector3.one * cellSize) / brickSize);
                Vector3Int maxBrick = FloorToInt((bounds.max + Vector3.one * cellSize) / brickSize);
                for (int z = minBrick.z; z <= maxBrick.z; z++)
                for (int y = minBrick.y; y <= maxBrick.y; y++)
                for (int x = minBrick.x; x <= maxBrick.x; x++)
                {
                    var key = new WorldBrickKey(level, x, y, z);
                    if (!index.TryGetValue(key, out List<int> instances))
                    {
                        instances = new List<int>();
                        index.Add(key, instances);
                    }
                    if (!instances.Contains(instanceIndex))
                        instances.Add(instanceIndex);
                    changed?.Add(key);
                }
            }
        }

        static void RemoveInstanceCoverage(
            Dictionary<WorldBrickKey, List<int>> index, int instanceIndex,
            Bounds bounds, HashSet<WorldBrickKey> changed)
        {
            for (int level = 0; level < GIWorldConstants.LevelCount; level++)
            {
                float cellSize = GIWorldConstants.CellSizes[level];
                float brickSize = cellSize * GIWorldConstants.BrickSize;
                Vector3Int minBrick = FloorToInt((bounds.min - Vector3.one * cellSize) / brickSize);
                Vector3Int maxBrick = FloorToInt((bounds.max + Vector3.one * cellSize) / brickSize);
                for (int z = minBrick.z; z <= maxBrick.z; z++)
                for (int y = minBrick.y; y <= maxBrick.y; y++)
                for (int x = minBrick.x; x <= maxBrick.x; x++)
                {
                    var key = new WorldBrickKey(level, x, y, z);
                    if (index.TryGetValue(key, out List<int> instances))
                    {
                        instances.Remove(instanceIndex);
                        if (instances.Count == 0)
                            index.Remove(key);
                    }
                    changed?.Add(key);
                }
            }
        }

        void RefreshRequiredBricks(IEnumerable<WorldBrickKey> changed)
        {
            foreach (WorldBrickKey key in changed)
            {
                bool required = meshInstancesByBrick.ContainsKey(key) ||
                                naniteInstancesByBrick.ContainsKey(key);
                if (required)
                    requiredBricks[key.level].Add(key);
                else
                    requiredBricks[key.level].Remove(key);
            }
        }

        void RebuildPageMapping(IEnumerable<WorldBrickKey> changedBricks)
        {
            Array.Fill(pageTable, -1);
            desiredBricks.Clear();
            dirtyPhysicalBricks.Clear();
            dirtyPhysicalSet.Clear();

            for (int level = 0; level < GIWorldConstants.LevelCount; level++)
            {
                Vector3Int origin = levels[level].originBrick;
                for (int z = 0; z < GIWorldConstants.PageTableAxis; z++)
                for (int y = 0; y < GIWorldConstants.PageTableAxis; y++)
                for (int x = 0; x < GIWorldConstants.PageTableAxis; x++)
                {
                    var key = new WorldBrickKey(level, origin.x + x, origin.y + y, origin.z + z);
                    if (requiredBricks[level].Contains(key))
                        desiredBricks.Add(key);
                }
            }

            releasedBricks.Clear();
            foreach (KeyValuePair<WorldBrickKey, int> pair in physicalByWorldBrick)
            {
                if (!desiredBricks.Contains(pair.Key))
                    releasedBricks.Add(pair.Key);
            }
            for (int index = 0; index < releasedBricks.Count; index++)
            {
                WorldBrickKey key = releasedBricks[index];
                int physical = physicalByWorldBrick[key];
                physicalByWorldBrick.Remove(key);
                bricks[physical] = default;
                freePhysicalBricks.Add(physical);
            }

            foreach (WorldBrickKey key in desiredBricks)
            {
                if (physicalByWorldBrick.ContainsKey(key))
                    continue;
                if (freePhysicalBricks.Count == 0)
                    throw new InvalidOperationException("GI world physical brick pool exhausted.");
                int last = freePhysicalBricks.Count - 1;
                int physical = freePhysicalBricks[last];
                freePhysicalBricks.RemoveAt(last);
                physicalByWorldBrick.Add(key, physical);
                bricks[physical] = new GIWorldBrickData
                {
                    worldBrick = new Vector3Int(key.x, key.y, key.z),
                    level = (uint)key.level,
                    generation = generation,
                    valid = 1
                };
                AddDirtyPhysical(physical);
            }

            foreach (WorldBrickKey key in changedBricks)
            {
                if (!desiredBricks.Contains(key) || !physicalByWorldBrick.TryGetValue(key, out int physical))
                    continue;
                GIWorldBrickData brick = bricks[physical];
                brick.generation = generation;
                bricks[physical] = brick;
                AddDirtyPhysical(physical);
            }

            foreach (KeyValuePair<WorldBrickKey, int> pair in physicalByWorldBrick)
            {
                WorldBrickKey key = pair.Key;
                Vector3Int local = new Vector3Int(key.x, key.y, key.z) - levels[key.level].originBrick;
                int logical = key.level * GIWorldConstants.PagesPerLevel + local.x +
                    local.y * GIWorldConstants.PageTableAxis +
                    local.z * GIWorldConstants.PageTableAxis * GIWorldConstants.PageTableAxis;
                pageTable[logical] = pair.Value;
            }

            RebuildWorkForDirtyBricks();
        }

        void RebuildWorkForDirtyBricks()
        {
            triangleWork.Clear();
            naniteTriangleWork.Clear();
            for (int index = 0; index < dirtyPhysicalBricks.Count; index++)
            {
                int physical = dirtyPhysicalBricks[index];
                uint naniteKeyIndex = 0u;
                GIWorldBrickData brick = bricks[physical];
                var key = new WorldBrickKey(
                    (int)brick.level, brick.worldBrick.x, brick.worldBrick.y, brick.worldBrick.z);
                if (meshInstancesByBrick.TryGetValue(key, out List<int> meshIndices))
                {
                    for (int item = 0; item < meshIndices.Count; item++)
                    {
                        int instanceIndex = meshIndices[item];
                        GIMeshSceneInstance instance = geometry.Instances[instanceIndex];
                        for (int localTriangle = 0; localTriangle < instance.triangleCount; localTriangle++)
                        {
                            triangleWork.Add(new GIWorldTriangleWork
                            {
                                triangleIndex = (uint)(instance.triangleStart + localTriangle),
                                physicalBrick = (uint)physical,
                                instanceIndex = (uint)instanceIndex
                            });
                        }
                    }
                }

                if (!naniteInstancesByBrick.TryGetValue(key, out List<int> naniteIndices))
                    continue;
                for (int item = 0; item < naniteIndices.Count; item++)
                {
                    int instanceIndex = naniteIndices[item];
                    GINaniteRootPage[] rootPages = naniteSceneInstances[instanceIndex].rootPages;
                    for (int pageIndex = 0; pageIndex < rootPages.Length; pageIndex++)
                    {
                        GINaniteRootPage page = rootPages[pageIndex];
                        naniteTriangleWork.Add(new GINaniteTriangleWork
                        {
                            pageId = page.pageId,
                            triangleCount = (uint)page.triangleCount,
                            physicalBrick = (uint)physical,
                            instanceIndex = (uint)instanceIndex,
                            // Winner keys only compete inside one physical brick.
                            keyBase = (naniteKeyIndex++ & 0x7fffu) << 16
                        });
                    }
                }
            }
        }

        static Vector3Int FloorToInt(Vector3 value) => new Vector3Int(
            Mathf.FloorToInt(value.x), Mathf.FloorToInt(value.y), Mathf.FloorToInt(value.z));

        void AddDirtyPhysical(int physical)
        {
            if (dirtyPhysicalSet.Add(physical))
                dirtyPhysicalBricks.Add(physical);
        }

        void AllocateBuffersOnce()
        {
            if (levelBuffer != null)
                return;
            int brickCapacity = GIWorldConstants.PageTableEntries;
            levelBuffer = NewBuffer(GIWorldConstants.LevelCount, GIWorldConstants.LevelStride, "GI World Levels");
            pageTableBuffer = NewBuffer(GIWorldConstants.PageTableEntries, 4, "GI World Page Table");
            brickBuffer = NewBuffer(brickCapacity, GIWorldConstants.BrickStride, "GI World Bricks");
            winnerBuffer = NewBuffer(brickCapacity * GIWorldConstants.CellsPerBrick, 4, "GI World Winner");
            occupancyBuffer = NewBuffer(brickCapacity * GIWorldConstants.OccupancyWordsPerBrick, 4, "GI World Occupancy");
            surfaceBuffer = NewBuffer(
                brickCapacity * GIWorldConstants.CellsPerBrick * GIWorldConstants.SurfaceWordsPerCell,
                4, "GI World Surface");
            distanceBuffer = NewBuffer(brickCapacity * GIWorldConstants.CellsPerBrick, 4, "GI World Distance");
            naniteDiagnosticsBuffer = NewBuffer(5, 4, "GI Nanite Diagnostics");
            dirtyBrickBuffer = NewBuffer(brickCapacity, 4, "GI World Dirty Bricks");
            EnsureTriangleWorkBuffer();
            EnsureMeshInstanceBuffer();
            EnsureNaniteBuffers();
        }

        void EnsureTriangleWorkBuffer()
        {
            int required = Mathf.Max(1, triangleWork.Count);
            if (triangleWorkBuffer != null && triangleWorkCapacity >= required)
                return;
            triangleWorkBuffer?.Dispose();
            triangleWorkCapacity = Mathf.NextPowerOfTwo(required);
            triangleWorkBuffer = NewBuffer(
                triangleWorkCapacity, GIWorldConstants.TriangleWorkStride, "GI World Triangle Work");
        }

        void EnsureMeshInstanceBuffer()
        {
            int required = Mathf.Max(1, meshGpuInstances.Count);
            if (meshInstanceBuffer != null && meshInstanceCapacity >= required)
                return;
            meshInstanceBuffer?.Dispose();
            meshInstanceCapacity = Mathf.NextPowerOfTwo(required);
            meshInstanceBuffer = NewBuffer(
                meshInstanceCapacity, GIWorldConstants.MeshInstanceStride, "GI Mesh Instances");
        }

        void EnsureNaniteBuffers()
        {
            int instanceRequired = Mathf.Max(1, naniteGpuInstances.Count);
            if (naniteInstanceBuffer == null || naniteInstanceCapacity < instanceRequired)
            {
                naniteInstanceBuffer?.Dispose();
                naniteInstanceCapacity = Mathf.NextPowerOfTwo(instanceRequired);
                naniteInstanceBuffer = NewBuffer(
                    naniteInstanceCapacity, GIWorldConstants.NaniteInstanceStride, "GI Nanite Instances");
            }
            int workRequired = Mathf.Max(1, naniteTriangleWork.Count);
            if (naniteTriangleWorkBuffer == null || naniteTriangleWorkCapacity < workRequired)
            {
                naniteTriangleWorkBuffer?.Dispose();
                naniteTriangleWorkCapacity = Mathf.NextPowerOfTwo(workRequired);
                naniteTriangleWorkBuffer = NewBuffer(
                    naniteTriangleWorkCapacity, GIWorldConstants.NaniteTriangleWorkStride,
                    "GI Nanite Triangle Work");
            }
        }

        void UploadMapping(long rebuildStart)
        {
            levelBuffer.SetData(levels);
            pageTableBuffer.SetData(pageTable);
            brickBuffer.SetData(bricks);
            if (dirtyPhysicalBricks.Count > 0)
                dirtyBrickBuffer.SetData(dirtyPhysicalBricks);
            EnsureTriangleWorkBuffer();
            if (triangleWork.Count > 0)
                triangleWorkBuffer.SetData(triangleWork);
            EnsureMeshInstanceBuffer();
            if (meshGpuInstances.Count > 0)
                meshInstanceBuffer.SetData(meshGpuInstances);
            EnsureNaniteBuffers();
            if (naniteGpuInstances.Count > 0)
                naniteInstanceBuffer.SetData(naniteGpuInstances);
            if (naniteTriangleWork.Count > 0)
                naniteTriangleWorkBuffer.SetData(naniteTriangleWork);
            double rebuildMilliseconds =
                (System.Diagnostics.Stopwatch.GetTimestamp() - rebuildStart) * 1000.0 /
                System.Diagnostics.Stopwatch.Frequency;
            bool significantUpdate = dirtyPhysicalBricks.Count >= 512 ||
                                     rebuildMilliseconds >= 4.0;
            if (significantUpdate || Time.realtimeSinceStartup - lastMappingLogTime >= 1f)
            {
                lastMappingLogTime = Time.realtimeSinceStartup;
                Debug.Log(
                    $"[GI][Node2] World mapping: active={physicalByWorldBrick.Count}, " +
                    $"dirty={dirtyPhysicalBricks.Count}, retained={physicalByWorldBrick.Count - dirtyPhysicalBricks.Count}, " +
                    $"meshWork={triangleWork.Count}, naniteWork={naniteTriangleWork.Count}, " +
                    $"generation={generation}, " +
                    $"cpu={rebuildMilliseconds:F2}ms.");
            }
        }

        static GraphicsBuffer NewBuffer(int count, int stride, string name) =>
            new GraphicsBuffer(GraphicsBuffer.Target.Structured, Mathf.Max(1, count), stride) { name = name };

        bool BuffersValid() => geometry.TriangleBuffer != null && levelBuffer != null &&
                               pageTableBuffer != null && brickBuffer != null && winnerBuffer != null &&
                               occupancyBuffer != null && surfaceBuffer != null && distanceBuffer != null &&
                               dirtyBrickBuffer != null && triangleWorkBuffer != null &&
                               meshInstanceBuffer != null &&
                               naniteInstanceBuffer != null && naniteTriangleWorkBuffer != null &&
                               naniteDiagnosticsBuffer != null &&
                               physicalByWorldBrick.Count > 0;

        public bool TryGetGpuView(out GIWorldGpuView view)
        {
            view = new GIWorldGpuView(levelBuffer, pageTableBuffer, brickBuffer,
                occupancyBuffer, surfaceBuffer, distanceBuffer,
                GIWorldConstants.PageTableEntries, generation);
            return view.IsValid;
        }

        public void RecordBuild(RenderGraph renderGraph, ComputeShader shader)
        {
            if (!buildPending || shader == null || !BuffersValid())
                return;
            buildPending = false;
            using var builder = renderGraph.AddUnsafePass<BuildPassData>("GI/World Build", out var data);
            data.shader = shader;
            data.clear = shader.FindKernel("ClearCells");
            data.select = shader.FindKernel("SelectSurface");
            data.commit = shader.FindKernel("CommitSurface");
            data.naniteSelect = shader.FindKernel("SelectNaniteSurface");
            data.naniteCommit = shader.FindKernel("CommitNaniteSurface");
            data.naniteDiagnosticsClear = shader.FindKernel("ClearNaniteDiagnostics");
            data.distance = shader.FindKernel("BuildConservativeDistance");
            data.triangles = geometry.TriangleBuffer;
            data.triangleWork = triangleWorkBuffer;
            data.meshInstances = meshInstanceBuffer;
            data.triangleWorkCount = triangleWork.Count;
            data.naniteInstances = naniteInstanceBuffer;
            data.naniteTriangleWork = naniteTriangleWorkBuffer;
            data.naniteDiagnostics = naniteDiagnosticsBuffer;
            data.naniteTriangleWorkCount = naniteTriangleWork.Count;
            data.nanite = naniteFrame;
            data.levels = levelBuffer;
            data.pageTable = pageTableBuffer;
            data.bricks = brickBuffer;
            data.winner = winnerBuffer;
            data.occupancy = occupancyBuffer;
            data.surface = surfaceBuffer;
            data.distanceBuffer = distanceBuffer;
            data.dirtyBricks = dirtyBrickBuffer;
            data.dirtyBrickCount = dirtyPhysicalBricks.Count;
            data.buildGeneration = generation;
            data.owner = this;
            data.requestNaniteDiagnostics = data.nanite.IsValid &&
                                            data.naniteTriangleWorkCount > 0 &&
                                            !naniteDiagnosticsPending &&
                                            Time.realtimeSinceStartup - lastNaniteDiagnosticsTime >= 1f;
            if (data.requestNaniteDiagnostics)
                naniteDiagnosticsPending = true;
            builder.UseBuffer(renderGraph.ImportBuffer(data.triangles), AccessFlags.Read);
            builder.UseBuffer(renderGraph.ImportBuffer(data.triangleWork), AccessFlags.Read);
            builder.UseBuffer(renderGraph.ImportBuffer(data.meshInstances), AccessFlags.Read);
            builder.UseBuffer(renderGraph.ImportBuffer(data.naniteInstances), AccessFlags.Read);
            builder.UseBuffer(renderGraph.ImportBuffer(data.naniteTriangleWork), AccessFlags.Read);
            builder.UseBuffer(renderGraph.ImportBuffer(data.naniteDiagnostics), AccessFlags.ReadWrite);
            if (data.nanite.IsValid)
            {
                builder.UseBuffer(renderGraph.ImportBuffer(data.nanite.resident.residentVertices), AccessFlags.Read);
                builder.UseBuffer(renderGraph.ImportBuffer(data.nanite.resident.residentIndices), AccessFlags.Read);
                builder.UseBuffer(renderGraph.ImportBuffer(data.nanite.resident.residentPageTable), AccessFlags.Read);
                builder.UseBuffer(renderGraph.ImportBuffer(data.nanite.resident.residencyBits), AccessFlags.Read);
            }
            builder.UseBuffer(renderGraph.ImportBuffer(data.levels), AccessFlags.Read);
            builder.UseBuffer(renderGraph.ImportBuffer(data.pageTable), AccessFlags.Read);
            builder.UseBuffer(renderGraph.ImportBuffer(data.bricks), AccessFlags.Read);
            builder.UseBuffer(renderGraph.ImportBuffer(data.winner), AccessFlags.ReadWrite);
            builder.UseBuffer(renderGraph.ImportBuffer(data.occupancy), AccessFlags.ReadWrite);
            builder.UseBuffer(renderGraph.ImportBuffer(data.surface), AccessFlags.ReadWrite);
            builder.UseBuffer(renderGraph.ImportBuffer(data.distanceBuffer), AccessFlags.ReadWrite);
            builder.UseBuffer(renderGraph.ImportBuffer(data.dirtyBricks), AccessFlags.Read);
            builder.AllowPassCulling(false);
            builder.SetRenderFunc(static (BuildPassData pass, UnsafeGraphContext context) => ExecuteBuild(pass, context));
        }

        static void ExecuteBuild(BuildPassData data, UnsafeGraphContext context)
        {
            var cmd = context.cmd;
            int cells = data.dirtyBrickCount * GIWorldConstants.CellsPerBrick;
            Bind(data, cmd, data.clear);
            cmd.DispatchCompute(data.shader, data.clear, DivRoundUp(cells, 64), 1, 1);
            Bind(data, cmd, data.select);
            if (data.triangleWorkCount > 0)
                cmd.DispatchCompute(data.shader, data.select, DivRoundUp(data.triangleWorkCount, 64), 1, 1);
            if (data.nanite.IsValid && data.naniteTriangleWorkCount > 0)
            {
                Bind(data, cmd, data.naniteDiagnosticsClear);
                cmd.DispatchCompute(data.shader, data.naniteDiagnosticsClear, 1, 1, 1);
                Bind(data, cmd, data.naniteSelect);
                int dispatchWidth = Mathf.Min(65535, data.naniteTriangleWorkCount);
                int dispatchHeight = DivRoundUp(data.naniteTriangleWorkCount, dispatchWidth);
                cmd.SetComputeIntParam(data.shader, ShaderIds.NaniteDispatchWidth, dispatchWidth);
                cmd.DispatchCompute(data.shader, data.naniteSelect,
                    dispatchWidth, dispatchHeight, 1);
            }
            Bind(data, cmd, data.commit);
            if (data.triangleWorkCount > 0)
                cmd.DispatchCompute(data.shader, data.commit, DivRoundUp(data.triangleWorkCount, 64), 1, 1);
            if (data.nanite.IsValid && data.naniteTriangleWorkCount > 0)
            {
                Bind(data, cmd, data.naniteCommit);
                int dispatchWidth = Mathf.Min(65535, data.naniteTriangleWorkCount);
                int dispatchHeight = DivRoundUp(data.naniteTriangleWorkCount, dispatchWidth);
                cmd.SetComputeIntParam(data.shader, ShaderIds.NaniteDispatchWidth, dispatchWidth);
                cmd.DispatchCompute(data.shader, data.naniteCommit,
                    dispatchWidth, dispatchHeight, 1);
            }
            if (data.requestNaniteDiagnostics)
                cmd.RequestAsyncReadback(data.naniteDiagnostics, data.owner.OnNaniteDiagnosticsReadback);
            Bind(data, cmd, data.distance);
            cmd.DispatchCompute(data.shader, data.distance, DivRoundUp(cells, 64), 1, 1);
        }

        static void Bind(BuildPassData data, UnsafeCommandBuffer cmd, int kernel)
        {
            cmd.SetComputeIntParam(data.shader, ShaderIds.DirtyBrickCount, data.dirtyBrickCount);
            cmd.SetComputeIntParam(data.shader, ShaderIds.BuildGeneration, unchecked((int)data.buildGeneration));
            cmd.SetComputeIntParam(data.shader, ShaderIds.TriangleWorkCount, data.triangleWorkCount);
            cmd.SetComputeIntParam(data.shader, ShaderIds.NaniteTriangleWorkCount, data.naniteTriangleWorkCount);
            cmd.SetComputeIntParam(data.shader, ShaderIds.NanitePoolGeneration,
                unchecked((int)data.nanite.resident.poolGeneration));
            cmd.SetComputeIntParam(data.shader, ShaderIds.NaniteGeometryReadyFlag,
                unchecked((int)data.nanite.resident.geometryReadyFlag));
            cmd.SetComputeBufferParam(data.shader, kernel, ShaderIds.Levels, data.levels);
            cmd.SetComputeBufferParam(data.shader, kernel, ShaderIds.PageTable, data.pageTable);
            cmd.SetComputeBufferParam(data.shader, kernel, ShaderIds.Bricks, data.bricks);
            cmd.SetComputeBufferParam(data.shader, kernel, ShaderIds.Triangles, data.triangles);
            cmd.SetComputeBufferParam(data.shader, kernel, ShaderIds.TriangleWork, data.triangleWork);
            cmd.SetComputeBufferParam(data.shader, kernel, ShaderIds.MeshInstances, data.meshInstances);
            cmd.SetComputeBufferParam(data.shader, kernel, ShaderIds.NaniteInstances, data.naniteInstances);
            cmd.SetComputeBufferParam(data.shader, kernel, ShaderIds.NaniteTriangleWork, data.naniteTriangleWork);
            cmd.SetComputeBufferParam(data.shader, kernel, ShaderIds.NaniteDiagnostics, data.naniteDiagnostics);
            if (data.nanite.IsValid)
            {
                cmd.SetComputeBufferParam(data.shader, kernel, ShaderIds.NaniteResidentVertices,
                    data.nanite.resident.residentVertices);
                cmd.SetComputeBufferParam(data.shader, kernel, ShaderIds.NaniteResidentIndices,
                    data.nanite.resident.residentIndices);
                cmd.SetComputeBufferParam(data.shader, kernel, ShaderIds.NaniteResidentPageTable,
                    data.nanite.resident.residentPageTable);
                cmd.SetComputeBufferParam(data.shader, kernel, ShaderIds.NaniteResidencyBits,
                    data.nanite.resident.residencyBits);
            }
            cmd.SetComputeBufferParam(data.shader, kernel, ShaderIds.Winner, data.winner);
            cmd.SetComputeBufferParam(data.shader, kernel, ShaderIds.Occupancy, data.occupancy);
            cmd.SetComputeBufferParam(data.shader, kernel, ShaderIds.Surface, data.surface);
            cmd.SetComputeBufferParam(data.shader, kernel, ShaderIds.Distance, data.distanceBuffer);
            cmd.SetComputeBufferParam(data.shader, kernel, ShaderIds.DirtyBricks, data.dirtyBricks);
        }

        static int DivRoundUp(int value, int divisor) => (value + divisor - 1) / divisor;

        void OnNaniteDiagnosticsReadback(AsyncGPUReadbackRequest request)
        {
            naniteDiagnosticsPending = false;
            lastNaniteDiagnosticsTime = Time.realtimeSinceStartup;
            if (request.hasError || request.GetData<uint>().Length < 5)
            {
                Debug.LogWarning("[GI][Node2] Nanite GPU diagnostic readback failed.");
                return;
            }
            var values = request.GetData<uint>();
            Debug.Log(
                $"[GI][Node2] Nanite GPU: pageValid={values[0]}, " +
                $"triangleDecoded={values[1]}, brickOverlap={values[2]}, " +
                $"cellTouched={values[3]}, cellCommitted={values[4]}.");
        }

        sealed class BuildPassData
        {
            public ComputeShader shader;
            public int clear, select, commit, naniteSelect, naniteCommit, naniteDiagnosticsClear, distance;
            public GraphicsBuffer triangles, triangleWork, meshInstances, levels, pageTable, bricks, winner, occupancy, surface, distanceBuffer;
            public GraphicsBuffer naniteInstances, naniteTriangleWork;
            public GraphicsBuffer naniteDiagnostics;
            public GraphicsBuffer dirtyBricks;
            public int triangleWorkCount, naniteTriangleWorkCount, dirtyBrickCount;
            public uint buildGeneration;
            public GINaniteFrameView nanite;
            public GIWorldCache owner;
            public bool requestNaniteDiagnostics;
        }

        static class ShaderIds
        {
            public static readonly int Levels = Shader.PropertyToID("_GIWorldLevels");
            public static readonly int PageTable = Shader.PropertyToID("_GIWorldPageTable");
            public static readonly int Bricks = Shader.PropertyToID("_GIWorldBricks");
            public static readonly int Triangles = Shader.PropertyToID("_GIWorldTriangles");
            public static readonly int TriangleWork = Shader.PropertyToID("_GIWorldTriangleWork");
            public static readonly int MeshInstances = Shader.PropertyToID("_GIMeshInstances");
            public static readonly int Winner = Shader.PropertyToID("_GIWorldWinner");
            public static readonly int Occupancy = Shader.PropertyToID("_GIWorldOccupancy");
            public static readonly int Surface = Shader.PropertyToID("_GIWorldSurface");
            public static readonly int Distance = Shader.PropertyToID("_GIWorldDistance");
            public static readonly int DirtyBricks = Shader.PropertyToID("_GIWorldDirtyBricks");
            public static readonly int DirtyBrickCount = Shader.PropertyToID("_GIWorldDirtyBrickCount");
            public static readonly int BuildGeneration = Shader.PropertyToID("_GIWorldBuildGeneration");
            public static readonly int TriangleWorkCount = Shader.PropertyToID("_GIWorldTriangleWorkCount");
            public static readonly int NaniteInstances = Shader.PropertyToID("_GINaniteInstances");
            public static readonly int NaniteTriangleWork = Shader.PropertyToID("_GINaniteTriangleWork");
            public static readonly int NaniteTriangleWorkCount = Shader.PropertyToID("_GINaniteTriangleWorkCount");
            public static readonly int NaniteDiagnostics = Shader.PropertyToID("_GINaniteDiagnostics");
            public static readonly int NaniteResidentVertices = Shader.PropertyToID("_GINaniteResidentVertices");
            public static readonly int NaniteResidentIndices = Shader.PropertyToID("_GINaniteResidentIndices");
            public static readonly int NaniteResidentPageTable = Shader.PropertyToID("_GINaniteResidentPageTable");
            public static readonly int NaniteResidencyBits = Shader.PropertyToID("_GINaniteResidencyBits");
            public static readonly int NanitePoolGeneration = Shader.PropertyToID("_GINanitePoolGeneration");
            public static readonly int NaniteGeometryReadyFlag = Shader.PropertyToID("_GINaniteGeometryReadyFlag");
            public static readonly int NaniteDispatchWidth = Shader.PropertyToID("_GINaniteDispatchWidth");
        }

        void ReleaseWorldBuffers()
        {
            levelBuffer?.Dispose(); levelBuffer = null;
            pageTableBuffer?.Dispose(); pageTableBuffer = null;
            brickBuffer?.Dispose(); brickBuffer = null;
            winnerBuffer?.Dispose(); winnerBuffer = null;
            occupancyBuffer?.Dispose(); occupancyBuffer = null;
            surfaceBuffer?.Dispose(); surfaceBuffer = null;
            distanceBuffer?.Dispose(); distanceBuffer = null;
            dirtyBrickBuffer?.Dispose(); dirtyBrickBuffer = null;
            triangleWorkBuffer?.Dispose(); triangleWorkBuffer = null;
            meshInstanceBuffer?.Dispose(); meshInstanceBuffer = null;
            naniteInstanceBuffer?.Dispose(); naniteInstanceBuffer = null;
            naniteTriangleWorkBuffer?.Dispose(); naniteTriangleWorkBuffer = null;
            naniteDiagnosticsBuffer?.Dispose(); naniteDiagnosticsBuffer = null;
            triangleWorkCapacity = 0;
            meshInstanceCapacity = 0;
            naniteInstanceCapacity = 0;
            naniteTriangleWorkCapacity = 0;
        }

        public void Dispose()
        {
            geometry.Dispose();
            ReleaseWorldBuffers();
        }

        readonly struct WorldBrickKey : IEquatable<WorldBrickKey>
        {
            public readonly int level, x, y, z;

            public WorldBrickKey(int level, int x, int y, int z)
            {
                this.level = level;
                this.x = x;
                this.y = y;
                this.z = z;
            }

            public bool Equals(WorldBrickKey other) =>
                level == other.level && x == other.x && y == other.y && z == other.z;
            public override bool Equals(object obj) => obj is WorldBrickKey other && Equals(other);
            public override int GetHashCode() => HashCode.Combine(level, x, y, z);
        }
    }
}
