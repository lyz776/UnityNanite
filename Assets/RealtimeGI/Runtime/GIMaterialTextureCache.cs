using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace RealtimeGI
{
    /// <summary>
    /// Resamples heterogeneous material textures into fixed-resolution arrays indexed by the
    /// stable material slot. RenderTexture inputs are refreshed every frame; ordinary textures
    /// refresh only when their material revision or reference changes.
    /// </summary>
    sealed class GIMaterialTextureCache : IDisposable
    {
        RenderTexture baseColorArray;
        RenderTexture emissionArray;
        int sliceCapacity;
        int resolution;
        int signature;

        public Texture BaseColorArray => baseColorArray;
        public Texture EmissionArray => emissionArray;
        public int SliceCount { get; private set; }
        public int Revision { get; private set; }

        public void Update(RealtimeGIScene scene, int requestedResolution)
        {
            if (scene == null)
                return;
            int materialCount = Mathf.Max(1, scene.MaterialCount);
            int targetResolution = Mathf.Clamp(Mathf.NextPowerOfTwo(requestedResolution), 32, 512);
            bool reallocated = baseColorArray == null || materialCount > sliceCapacity ||
                               targetResolution != resolution;
            if (reallocated)
                Allocate(materialCount, targetResolution);

            int nextSignature = ComputeSignature(scene, out bool hasLiveTextures);
            SliceCount = materialCount;
            if (!reallocated && nextSignature == signature && !hasLiveTextures)
                return;

            for (int slot = 0; slot < materialCount; slot++)
            {
                Material material = scene.GetMaterialSource(slot);
                GIMaterialBridge bridge = scene.GetMaterialBridgeSource(slot);
                Texture baseMap = bridge != null && bridge.RuntimeBaseMap != null
                    ? bridge.RuntimeBaseMap : FindBaseMap(material);
                Texture emissionMap = bridge != null && bridge.RuntimeEmissionMap != null
                    ? bridge.RuntimeEmissionMap : FindEmissionMap(material);
                Graphics.Blit(baseMap != null ? baseMap : Texture2D.whiteTexture,
                    baseColorArray, 0, slot);
                Graphics.Blit(emissionMap != null ? emissionMap : Texture2D.blackTexture,
                    emissionArray, 0, slot);
            }
            baseColorArray.GenerateMips();
            emissionArray.GenerateMips();
            signature = nextSignature;
            unchecked { Revision++; }
        }

        static Texture FindBaseMap(Material material)
        {
            if (material == null)
                return null;
            if (material.HasProperty("_BaseMap"))
                return material.GetTexture("_BaseMap");
            return material.HasProperty("_MainTex") ? material.GetTexture("_MainTex") : null;
        }

        static Texture FindEmissionMap(Material material) =>
            material != null && material.HasProperty("_EmissionMap")
                ? material.GetTexture("_EmissionMap") : null;

        static int ComputeSignature(RealtimeGIScene scene, out bool hasLiveTextures)
        {
            unchecked
            {
                int hash = 17;
                hasLiveTextures = false;
                var materials = scene.CpuMaterials;
                hash = hash * 31 + materials.Count;
                for (int slot = 0; slot < materials.Count; slot++)
                {
                    Material material = scene.GetMaterialSource(slot);
                    GIMaterialBridge bridge = scene.GetMaterialBridgeSource(slot);
                    Texture baseMap = bridge != null && bridge.RuntimeBaseMap != null
                        ? bridge.RuntimeBaseMap : FindBaseMap(material);
                    Texture emissionMap = bridge != null && bridge.RuntimeEmissionMap != null
                        ? bridge.RuntimeEmissionMap : FindEmissionMap(material);
                    hash = hash * 31 + (int)materials[slot].revision;
                    hash = hash * 31 + (baseMap != null ? baseMap.GetInstanceID() : 0);
                    hash = hash * 31 + (emissionMap != null ? emissionMap.GetInstanceID() : 0);
                    hasLiveTextures |= baseMap is RenderTexture || emissionMap is RenderTexture;
                }
                return hash;
            }
        }

        void Allocate(int requiredSlices, int targetResolution)
        {
            ReleaseTextures();
            sliceCapacity = Mathf.NextPowerOfTwo(Mathf.Max(1, requiredSlices));
            resolution = targetResolution;
            baseColorArray = CreateArray("GI BaseColor Array", GraphicsFormat.R8G8B8A8_SRGB);
            emissionArray = CreateArray("GI Emission Array", GraphicsFormat.R16G16B16A16_SFloat);
            signature = 0;
            Revision = 0;
        }

        RenderTexture CreateArray(string name, GraphicsFormat format)
        {
            var descriptor = new RenderTextureDescriptor(resolution, resolution, format, 0)
            {
                dimension = TextureDimension.Tex2DArray,
                volumeDepth = sliceCapacity,
                msaaSamples = 1,
                useMipMap = true,
                autoGenerateMips = false,
                enableRandomWrite = false
            };
            var texture = new RenderTexture(descriptor)
            {
                name = name,
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Bilinear
            };
            texture.Create();
            return texture;
        }

        void ReleaseTextures()
        {
            if (baseColorArray != null)
            {
                baseColorArray.Release();
                CoreUtils.Destroy(baseColorArray);
            }
            if (emissionArray != null)
            {
                emissionArray.Release();
                CoreUtils.Destroy(emissionArray);
            }
            baseColorArray = null;
            emissionArray = null;
        }

        public void Dispose()
        {
            ReleaseTextures();
            sliceCapacity = 0;
            SliceCount = 0;
            signature = 0;
        }
    }
}
