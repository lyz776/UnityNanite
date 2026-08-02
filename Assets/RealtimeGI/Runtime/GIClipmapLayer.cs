using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace RealtimeGI
{
    /// <summary>Persistent sparse world-Brick allocation for one static or dynamic layer.</summary>
    public sealed class GIClipmapLayer : IDisposable
    {
        readonly int capacity;
        readonly Dictionary<GIClipmapBrickKey, int> allocations;
        readonly Stack<int> freePhysicalIds;
        readonly List<GIClipmapBrickKey> removalScratch = new List<GIClipmapBrickKey>(256);
        readonly List<GIClipmapBrickKey> priorityScratch = new List<GIClipmapBrickKey>(1024);
        readonly HashSet<GIClipmapBrickKey> selectedScratch = new HashSet<GIClipmapBrickKey>();
        readonly List<int> dirtyPhysicalIds = new List<int>(256);
        readonly HashSet<int> dirtyPhysicalSet = new HashSet<int>();
        readonly HashSet<GIClipmapBrickKey> dirtyWorldBricks = new HashSet<GIClipmapBrickKey>();
        readonly int[] pageTableCpu = new int[GIClipmapConstants.PageTableEntries];
        readonly GIGpuClipmapBrickData[] brickDataCpu;
        readonly Queue<GIClipmapBrickKey> radianceQueue = new Queue<GIClipmapBrickKey>(256);
        readonly HashSet<GIClipmapBrickKey> radianceQueued = new HashSet<GIClipmapBrickKey>();
        readonly List<int> radiancePhysicalIds = new List<int>(256);

        GraphicsBuffer pageTableBuffer;
        GraphicsBuffer occupancyBuffer;
        GraphicsBuffer surfaceBuffer;
        GraphicsBuffer surfaceUvBuffer;
        GraphicsBuffer surfaceIdentityBuffer;
        GraphicsBuffer surfaceKeyBuffer;
        GraphicsBuffer distanceBuffer;
        GraphicsBuffer dirtyBrickBuffer;
        GraphicsBuffer brickDataBuffer;
        GraphicsBuffer radianceBuffer;
        GraphicsBuffer validityBuffer;
        GraphicsBuffer radianceDirtyBuffer;
        int dirtyCapacity;
        int radianceDirtyCapacity;

        public GraphicsBuffer PageTableBuffer => pageTableBuffer;
        public GraphicsBuffer OccupancyBuffer => occupancyBuffer;
        public GraphicsBuffer SurfaceBuffer => surfaceBuffer;
        public GraphicsBuffer SurfaceUvBuffer => surfaceUvBuffer;
        public GraphicsBuffer SurfaceIdentityBuffer => surfaceIdentityBuffer;
        public GraphicsBuffer SurfaceKeyBuffer => surfaceKeyBuffer;
        public GraphicsBuffer DistanceBuffer => distanceBuffer;
        public GraphicsBuffer DirtyBrickBuffer => dirtyBrickBuffer;
        public GraphicsBuffer BrickDataBuffer => brickDataBuffer;
        public GraphicsBuffer RadianceBuffer => radianceBuffer;
        public GraphicsBuffer ValidityBuffer => validityBuffer;
        public GraphicsBuffer RadianceDirtyBuffer => radianceDirtyBuffer;
        public int DirtyBrickCount => dirtyPhysicalIds.Count;
        public int RadianceDirtyCount => radiancePhysicalIds.Count;
        public int PendingRadianceCount => radianceQueue.Count;
        public int AllocatedCount => allocations.Count;
        public int Capacity => capacity;
        public int DroppedBrickCount { get; private set; }

        public GIClipmapLayer(int capacity, string debugName)
        {
            this.capacity = Mathf.Max(1, capacity);
            allocations = new Dictionary<GIClipmapBrickKey, int>(this.capacity);
            freePhysicalIds = new Stack<int>(this.capacity);
            for (int i = this.capacity - 1; i >= 0; i--)
                freePhysicalIds.Push(i);
            Array.Fill(pageTableCpu, -1);
            brickDataCpu = new GIGpuClipmapBrickData[this.capacity];

            pageTableBuffer = NewBuffer(GIClipmapConstants.PageTableEntries, 4, debugName + " Page Table");
            occupancyBuffer = NewBuffer(
                this.capacity * GIClipmapConstants.OccupancyWordsPerBrick, 4, debugName + " Occupancy");
            surfaceBuffer = NewBuffer(
                this.capacity * GIClipmapConstants.SurfaceWordsPerBrick, 4, debugName + " Surface");
            surfaceUvBuffer = NewBuffer(
                this.capacity * GIClipmapConstants.SurfaceUvWordsPerBrick, 4, debugName + " Surface UV0");
            surfaceIdentityBuffer = NewBuffer(
                this.capacity * GIClipmapConstants.SurfaceIdentityWordsPerBrick, 4, debugName + " Surface Identity");
            surfaceKeyBuffer = NewBuffer(
                this.capacity * GIClipmapConstants.SurfaceKeyWordsPerBrick, 4, debugName + " Surface Candidate Key");
            distanceBuffer = NewBuffer(
                this.capacity * GIClipmapConstants.DistanceWordsPerBrick, 4, debugName + " Distance");
            radianceBuffer = NewBuffer(
                this.capacity * GIClipmapConstants.RadianceWordsPerBrick, 4, debugName + " Directional Radiance RGB9E5");
            validityBuffer = NewBuffer(
                this.capacity * GIClipmapConstants.ValidityWordsPerBrick, 4, debugName + " Radiance Validity");
            brickDataBuffer = NewBuffer(this.capacity, GIClipmapConstants.BrickDataStride, debugName + " Brick Data");
            dirtyBrickBuffer = NewBuffer(1, 4, debugName + " Dirty Bricks");
            radianceDirtyBuffer = NewBuffer(1, 4, debugName + " Radiance Dirty Bricks");
            dirtyCapacity = 1;
            radianceDirtyCapacity = 1;
            pageTableBuffer.SetData(pageTableCpu);
        }

        public void UpdateRequired(
            HashSet<GIClipmapBrickKey> required,
            Vector3Int[] levelOrigins,
            bool invalidateExisting)
        {
            dirtyPhysicalIds.Clear();
            dirtyPhysicalSet.Clear();
            dirtyWorldBricks.Clear();
            SelectRequiredByPriority(required, levelOrigins);
            DroppedBrickCount = Mathf.Max(0, required.Count - selectedScratch.Count);
            removalScratch.Clear();
            foreach (KeyValuePair<GIClipmapBrickKey, int> pair in allocations)
            {
                if (!selectedScratch.Contains(pair.Key))
                    removalScratch.Add(pair.Key);
            }
            for (int i = 0; i < removalScratch.Count; i++)
            {
                GIClipmapBrickKey key = removalScratch[i];
                int physicalId = allocations[key];
                allocations.Remove(key);
                freePhysicalIds.Push(physicalId);
            }

            foreach (GIClipmapBrickKey key in selectedScratch)
            {
                if (allocations.TryGetValue(key, out int existing))
                {
                    if (invalidateExisting)
                        AddDirty(key, existing);
                    continue;
                }
                if (freePhysicalIds.Count == 0)
                {
                    DroppedBrickCount++;
                    continue;
                }
                int physicalId = freePhysicalIds.Pop();
                allocations.Add(key, physicalId);
                brickDataCpu[physicalId] = new GIGpuClipmapBrickData
                {
                    worldBrick = key.worldBrick,
                    level = (uint)key.level
                };
                AddDirty(key, physicalId);
            }

            Array.Fill(pageTableCpu, -1);
            foreach (KeyValuePair<GIClipmapBrickKey, int> pair in allocations)
            {
                GIClipmapBrickKey key = pair.Key;
                Vector3Int local = key.worldBrick - levelOrigins[key.level];
                if ((uint)local.x >= (uint)GIClipmapConstants.BricksPerAxis ||
                    (uint)local.y >= (uint)GIClipmapConstants.BricksPerAxis ||
                    (uint)local.z >= (uint)GIClipmapConstants.BricksPerAxis)
                    continue;
                int address = key.level * GIClipmapConstants.BricksPerLevel +
                              local.x + local.y * GIClipmapConstants.BricksPerAxis +
                              local.z * GIClipmapConstants.BricksPerAxis * GIClipmapConstants.BricksPerAxis;
                pageTableCpu[address] = pair.Value;
            }
            pageTableBuffer.SetData(pageTableCpu);
            brickDataBuffer.SetData(brickDataCpu);
            UploadDirtyList();
        }

        void SelectRequiredByPriority(
            HashSet<GIClipmapBrickKey> required,
            Vector3Int[] levelOrigins)
        {
            selectedScratch.Clear();
            if (required.Count <= capacity)
            {
                selectedScratch.UnionWith(required);
                return;
            }

            priorityScratch.Clear();
            priorityScratch.AddRange(required);
            priorityScratch.Sort((a, b) =>
            {
                int levelOrder = a.level.CompareTo(b.level);
                if (levelOrder != 0)
                    return levelOrder;
                Vector3Int center = levelOrigins[a.level] +
                                    Vector3Int.one * (GIClipmapConstants.BricksPerAxis / 2);
                int aDistance = SquareDistance(a.worldBrick, center);
                int bDistance = SquareDistance(b.worldBrick, center);
                return aDistance.CompareTo(bDistance);
            });
            int count = Mathf.Min(capacity, priorityScratch.Count);
            for (int i = 0; i < count; i++)
                selectedScratch.Add(priorityScratch[i]);
        }

        static int SquareDistance(Vector3Int a, Vector3Int b)
        {
            int x = a.x - b.x;
            int y = a.y - b.y;
            int z = a.z - b.z;
            return x * x + y * y + z * z;
        }

        public void MarkAllDirty()
        {
            dirtyPhysicalIds.Clear();
            dirtyPhysicalSet.Clear();
            dirtyWorldBricks.Clear();
            foreach (KeyValuePair<GIClipmapBrickKey, int> pair in allocations)
                AddDirty(pair.Key, pair.Value);
            UploadDirtyList();
        }

        public void MarkDirty(IEnumerable<GIClipmapBrickKey> worldBricks)
        {
            if (worldBricks == null)
                return;
            foreach (GIClipmapBrickKey key in worldBricks)
            {
                if (allocations.TryGetValue(key, out int physicalId))
                    AddDirty(key, physicalId);
            }
            UploadDirtyList();
        }

        public bool IsDirty(GIClipmapBrickKey key) => dirtyWorldBricks.Contains(key);

        void AddDirty(GIClipmapBrickKey key, int physicalId)
        {
            dirtyWorldBricks.Add(key);
            if (dirtyPhysicalSet.Add(physicalId))
                dirtyPhysicalIds.Add(physicalId);
            EnqueueRadiance(key);
        }

        public void EnqueueAllRadiance()
        {
            priorityScratch.Clear();
            priorityScratch.AddRange(allocations.Keys);
            priorityScratch.Sort((a, b) => a.level.CompareTo(b.level));
            for (int i = 0; i < priorityScratch.Count; i++)
                EnqueueRadiance(priorityScratch[i]);
        }

        void EnqueueRadiance(GIClipmapBrickKey key)
        {
            if (radianceQueued.Add(key))
                radianceQueue.Enqueue(key);
        }

        public void PrepareRadianceUpdates(int budget)
        {
            radiancePhysicalIds.Clear();
            int remaining = Mathf.Max(0, budget);
            while (remaining > 0 && radianceQueue.Count > 0)
            {
                GIClipmapBrickKey key = radianceQueue.Dequeue();
                radianceQueued.Remove(key);
                if (!allocations.TryGetValue(key, out int physicalId))
                    continue;
                radiancePhysicalIds.Add(physicalId);
                remaining--;
            }
            int required = Mathf.NextPowerOfTwo(Mathf.Max(1, radiancePhysicalIds.Count));
            if (radianceDirtyBuffer == null || radianceDirtyCapacity < required)
            {
                radianceDirtyBuffer?.Release();
                radianceDirtyCapacity = required;
                radianceDirtyBuffer = NewBuffer(required, 4, "GI Radiance Dirty Bricks");
            }
            if (radiancePhysicalIds.Count > 0)
                radianceDirtyBuffer.SetData(radiancePhysicalIds, 0, 0, radiancePhysicalIds.Count);
        }

        void UploadDirtyList()
        {
            int required = Mathf.NextPowerOfTwo(Mathf.Max(1, dirtyPhysicalIds.Count));
            if (dirtyBrickBuffer == null || dirtyCapacity < required)
            {
                dirtyBrickBuffer?.Release();
                dirtyCapacity = required;
                dirtyBrickBuffer = NewBuffer(required, 4, "GI Dirty Bricks");
            }
            if (dirtyPhysicalIds.Count > 0)
                dirtyBrickBuffer.SetData(dirtyPhysicalIds, 0, 0, dirtyPhysicalIds.Count);
        }

        static GraphicsBuffer NewBuffer(int count, int stride, string name) =>
            new GraphicsBuffer(GraphicsBuffer.Target.Structured, Mathf.Max(1, count), stride) { name = name };

        public void Dispose()
        {
            pageTableBuffer?.Release();
            occupancyBuffer?.Release();
            surfaceBuffer?.Release();
            surfaceUvBuffer?.Release();
            surfaceIdentityBuffer?.Release();
            surfaceKeyBuffer?.Release();
            distanceBuffer?.Release();
            dirtyBrickBuffer?.Release();
            brickDataBuffer?.Release();
            radianceBuffer?.Release();
            validityBuffer?.Release();
            radianceDirtyBuffer?.Release();
            pageTableBuffer = null;
            occupancyBuffer = null;
            surfaceBuffer = null;
            surfaceUvBuffer = null;
            surfaceIdentityBuffer = null;
            surfaceKeyBuffer = null;
            distanceBuffer = null;
            dirtyBrickBuffer = null;
            brickDataBuffer = null;
            radianceBuffer = null;
            validityBuffer = null;
            radianceDirtyBuffer = null;
            allocations.Clear();
            freePhysicalIds.Clear();
            dirtyPhysicalSet.Clear();
            dirtyWorldBricks.Clear();
            radianceQueue.Clear();
            radianceQueued.Clear();
            radiancePhysicalIds.Clear();
        }
    }
}
