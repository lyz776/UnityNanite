using System;
using System.Collections.Generic;
using UnityEngine;

namespace RealtimeGI
{
    readonly struct GIMaterialSlotKey : IEquatable<GIMaterialSlotKey>
    {
        readonly int materialId;
        readonly int ownerId;
        readonly int subMesh;

        public GIMaterialSlotKey(int materialId, int ownerId, int subMesh)
        {
            this.materialId = materialId;
            this.ownerId = ownerId;
            this.subMesh = subMesh;
        }

        public bool Equals(GIMaterialSlotKey other) =>
            materialId == other.materialId && ownerId == other.ownerId && subMesh == other.subMesh;
        public override bool Equals(object obj) => obj is GIMaterialSlotKey other && Equals(other);
        public override int GetHashCode()
        {
            unchecked
            {
                int hash = materialId;
                hash = hash * 397 ^ ownerId;
                return hash * 397 ^ subMesh;
            }
        }
    }

    /// <summary>Stable indirection for material indices stored in persistent Clipmap Bricks.</summary>
    sealed class GIMaterialSlotTable
    {
        public const int MaxSlotCount = 1 << 15;

        readonly Dictionary<GIMaterialSlotKey, uint> lookup = new Dictionary<GIMaterialSlotKey, uint>(256);
        readonly HashSet<GIMaterialSlotKey> updatedThisFrame = new HashSet<GIMaterialSlotKey>();
        readonly List<GIGpuMaterialData> materials = new List<GIGpuMaterialData>(256);
        readonly List<Material> sources = new List<Material>(256);
        readonly List<GIMaterialBridge> bridges = new List<GIMaterialBridge>(256);

        public List<GIGpuMaterialData> Materials => materials;
        public List<Material> Sources => sources;
        public List<GIMaterialBridge> Bridges => bridges;
        public int OverflowCount { get; private set; }

        public GIMaterialSlotTable()
        {
            lookup.Add(new GIMaterialSlotKey(0, 0, 0), 0);
            sources.Add(null);
            bridges.Add(null);
            materials.Add(GIMaterialUtility.Build(null));
        }

        public uint GetOrAdd(Material material, GIMaterialBridge bridge, int ownerId, int subMesh)
        {
            int materialId = material != null ? material.GetInstanceID() : 0;
            bool unique = bridge != null && bridge.RequiresUniqueMaterialSlot;
            var key = new GIMaterialSlotKey(materialId, unique ? ownerId : 0, unique ? subMesh : 0);
            if (lookup.TryGetValue(key, out uint existing))
            {
                if (updatedThisFrame.Add(key))
                    materials[(int)existing] = GIMaterialUtility.Build(material, bridge, subMesh);
                return existing;
            }
            if (materials.Count >= MaxSlotCount)
            {
                OverflowCount++;
                return 0;
            }
            uint slot = (uint)materials.Count;
            lookup.Add(key, slot);
            updatedThisFrame.Add(key);
            materials.Add(GIMaterialUtility.Build(material, bridge, subMesh));
            sources.Add(material);
            bridges.Add(bridge);
            return slot;
        }

        public void BeginFrame()
        {
            updatedThisFrame.Clear();
            OverflowCount = 0;
        }

        public void Clear()
        {
            lookup.Clear();
            materials.Clear();
            sources.Clear();
            bridges.Clear();
            updatedThisFrame.Clear();
            OverflowCount = 0;
        }
    }
}
