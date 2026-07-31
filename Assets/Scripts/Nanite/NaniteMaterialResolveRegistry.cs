using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Nanite
{
    /// <summary>
    /// Contract for a shader family that can resolve Nanite visibility IDs into
    /// the active URP GBuffer. The resolve material must expose a pass named
    /// "GBufferMerge" and consume the common Nanite VBuffer globals. Family
    /// implementations bind their source-material textures and parameters through
    /// the provided command buffer immediately before the indirect resolve draw.
    /// </summary>
    public interface INaniteMaterialResolveFamily
    {
        string Name { get; }
        Material ResolveMaterial { get; }
        bool Supports(Material sourceMaterial);
        void Bind(
            RasterCommandBuffer commandBuffer,
            Material sourceMaterial,
            MaterialPropertyBlock properties);
    }

    /// <summary>
    /// Optional extension for shader families whose scalar/texture layout is not
    /// URP/Lit-compatible. It translates source properties into the common
    /// per-pixel material record and contributes every draw-bound property to the
    /// compatibility hash. Without this contract, two custom materials could be
    /// incorrectly merged into one resolve bin.
    /// </summary>
    public interface INaniteMaterialResolveDataProvider
    {
        void Populate(
            Material sourceMaterial,
            ref NaniteSceneVisibilityBufferBackend.GpuMaterialData materialData);
        int GetCompatibilityHash(Material sourceMaterial);
    }

    /// <summary>
    /// Runtime registry for non-URP/Lit material programs. Registration is explicit:
    /// shaders are never admitted merely because they happen to use similarly named
    /// properties. Register during subsystem/scene initialization and keep the token
    /// alive for as long as the family is available. A registry revision invalidates
    /// material bins without changing immutable geometry or Page residency.
    /// </summary>
    public static class NaniteMaterialResolveRegistry
    {
        sealed class Entry
        {
            internal int id;
            internal INaniteMaterialResolveFamily family;
        }

        sealed class Registration : IDisposable
        {
            int id;

            internal Registration(int id) => this.id = id;

            public void Dispose()
            {
                int release = id;
                id = 0;
                if (release != 0)
                    Unregister(release);
            }
        }

        static readonly List<Entry> entries = new List<Entry>(8);
        static int nextId = 1;
        static int revision;

        public static int Revision => revision;

        public static IDisposable Register(INaniteMaterialResolveFamily family)
        {
            if (family == null)
                throw new ArgumentNullException(nameof(family));
            if (family.ResolveMaterial == null)
                throw new ArgumentException("A Nanite resolve family requires a resolve Material.", nameof(family));

            int id = nextId++;
            entries.Add(new Entry { id = id, family = family });
            unchecked { revision++; }
            NaniteRuntimeRegistry.NotifyMaterialFamiliesChanged();
            return new Registration(id);
        }

        public static bool TryResolve(Material sourceMaterial, out int familyId)
        {
            for (int index = 0; index < entries.Count; index++)
            {
                Entry entry = entries[index];
                if (entry == null || entry.family == null || entry.family.ResolveMaterial == null)
                    continue;
                try
                {
                    if (!entry.family.Supports(sourceMaterial))
                        continue;
                    familyId = entry.id;
                    return true;
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                }
            }

            familyId = 0;
            return false;
        }

        public static bool TryGet(int familyId, out INaniteMaterialResolveFamily family)
        {
            if (familyId > 0)
            {
                for (int index = 0; index < entries.Count; index++)
                {
                    Entry entry = entries[index];
                    if (entry != null && entry.id == familyId && entry.family != null &&
                        entry.family.ResolveMaterial != null)
                    {
                        family = entry.family;
                        return true;
                    }
                }
            }

            family = null;
            return false;
        }

        static void Unregister(int familyId)
        {
            for (int index = 0; index < entries.Count; index++)
            {
                if (entries[index] == null || entries[index].id != familyId)
                    continue;
                entries.RemoveAt(index);
                unchecked { revision++; }
                NaniteRuntimeRegistry.NotifyMaterialFamiliesChanged();
                return;
            }
        }
    }
}
