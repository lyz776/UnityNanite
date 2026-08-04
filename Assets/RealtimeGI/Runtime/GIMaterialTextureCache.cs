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
        RenderTexture normalArray;
        RenderTexture maskArray;
        RTHandle baseColorHandle;
        RTHandle emissionHandle;
        RTHandle normalHandle;
        RTHandle maskHandle;
        int sliceCapacity;
        int resolution;
        int signature;

        public Texture BaseColorArray => baseColorArray;
        public Texture EmissionArray => emissionArray;
        public Texture NormalArray => normalArray;
        public Texture MaskArray => maskArray;
        public RTHandle BaseColorHandle => baseColorHandle;
        public RTHandle EmissionHandle => emissionHandle;
        public RTHandle NormalHandle => normalHandle;
        public RTHandle MaskHandle => maskHandle;
        public int SliceCount { get; private set; }
        public int Revision { get; private set; }
        public bool UpdatedThisFrame { get; private set; }

        public void Update(RealtimeGIScene scene, int requestedResolution)
        {
            UpdatedThisFrame = false;
            if (scene == null)
                return;
            int materialCount = Mathf.Max(1, scene.MaterialCount);
            int targetResolution = Mathf.Clamp(Mathf.NextPowerOfTwo(requestedResolution), 32, 512);
            bool reallocated = baseColorArray == null || materialCount > sliceCapacity ||
                               targetResolution != resolution;
            if (reallocated)
                Allocate(materialCount, targetResolution);

            int nextSignature = ComputeSignature(scene, out bool hasLiveTextures);
            bool contentContractChanged = reallocated || nextSignature != signature;
            SliceCount = materialCount;
            if (!reallocated && nextSignature == signature && !hasLiveTextures)
                return;

            for (int slot = 0; slot < materialCount; slot++)
            {
                Material material = scene.GetMaterialSource(slot);
                GIMaterialBridge bridge = scene.GetMaterialBridgeSource(slot);
                bool explicitBridge = bridge != null &&
                                      bridge.materialFamily == GIMaterialFamily.ExplicitStylizedProxy;
                Texture baseMap = explicitBridge ? bridge.RuntimeBaseMap :
                    bridge != null && bridge.RuntimeBaseMap != null ? bridge.RuntimeBaseMap : FindBaseMap(material);
                Texture emissionMap = explicitBridge ? bridge.RuntimeEmissionMap :
                    bridge != null && bridge.RuntimeEmissionMap != null ? bridge.RuntimeEmissionMap : FindEmissionMap(material);
                Texture normalMap = explicitBridge ? bridge.RuntimeNormalMap :
                    bridge != null && bridge.RuntimeNormalMap != null ? bridge.RuntimeNormalMap : FindNormalMap(material);
                Texture maskMap = explicitBridge ? bridge.RuntimeMaskMap :
                    bridge != null && bridge.RuntimeMaskMap != null ? bridge.RuntimeMaskMap : FindMaskMap(material);
                Graphics.Blit(baseMap != null ? baseMap : Texture2D.whiteTexture,
                    baseColorArray, 0, slot);
                Graphics.Blit(emissionMap != null ? emissionMap : Texture2D.blackTexture,
                    emissionArray, 0, slot);
                Graphics.Blit(normalMap != null ? normalMap : Texture2D.grayTexture,
                    normalArray, 0, slot);
                Graphics.Blit(maskMap != null ? maskMap : Texture2D.whiteTexture,
                    maskArray, 0, slot);
            }
            baseColorArray.GenerateMips();
            emissionArray.GenerateMips();
            normalArray.GenerateMips();
            maskArray.GenerateMips();
            signature = nextSignature;
            // A RenderTexture is refreshed every frame, but its semantic GI revision is
            // explicit: procedural producers call GIMaterialBridge.MarkDirty(). Otherwise a
            // live texture would requeue the complete Surface Cache every frame.
            if (contentContractChanged)
                unchecked { Revision++; }
            UpdatedThisFrame = true;
        }

        static Texture FindBaseMap(Material material)
        {
            if (material == null)
                return null;
            if (GIMaterialUtility.HasExplicitShaderProxy(material))
                return GIMaterialUtility.GetExplicitProxyTexture(material, "_GIProxyBaseMap");
            if (material.HasProperty("_BaseMap"))
                return material.GetTexture("_BaseMap");
            return material.HasProperty("_MainTex") ? material.GetTexture("_MainTex") : null;
        }

        static Texture FindEmissionMap(Material material)
        {
            if (GIMaterialUtility.HasExplicitShaderProxy(material))
                return GIMaterialUtility.GetExplicitProxyTexture(material, "_GIProxyEmissionMap");
            return material != null && material.HasProperty("_EmissionMap")
                ? material.GetTexture("_EmissionMap") : null;
        }

        static Texture FindNormalMap(Material material)
        {
            if (material == null)
                return null;
            if (GIMaterialUtility.HasExplicitShaderProxy(material))
                return GIMaterialUtility.GetExplicitProxyTexture(material, "_GIProxyNormalMap");
            if (material.HasProperty("_BumpMap"))
                return material.GetTexture("_BumpMap");
            return material.HasProperty("_NormalMap") ? material.GetTexture("_NormalMap") : null;
        }

        static Texture FindMaskMap(Material material)
        {
            if (material == null)
                return null;
            if (GIMaterialUtility.HasExplicitShaderProxy(material))
                return GIMaterialUtility.GetExplicitProxyTexture(material, "_GIProxyMaskMap");
            if (material.HasProperty("_MetallicGlossMap"))
                return material.GetTexture("_MetallicGlossMap");
            return material.HasProperty("_MaskMap") ? material.GetTexture("_MaskMap") : null;
        }

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
                    bool explicitBridge = bridge != null &&
                                          bridge.materialFamily == GIMaterialFamily.ExplicitStylizedProxy;
                    Texture baseMap = explicitBridge ? bridge.RuntimeBaseMap :
                        bridge != null && bridge.RuntimeBaseMap != null ? bridge.RuntimeBaseMap : FindBaseMap(material);
                    Texture emissionMap = explicitBridge ? bridge.RuntimeEmissionMap :
                        bridge != null && bridge.RuntimeEmissionMap != null ? bridge.RuntimeEmissionMap : FindEmissionMap(material);
                    Texture normalMap = explicitBridge ? bridge.RuntimeNormalMap :
                        bridge != null && bridge.RuntimeNormalMap != null ? bridge.RuntimeNormalMap : FindNormalMap(material);
                    Texture maskMap = explicitBridge ? bridge.RuntimeMaskMap :
                        bridge != null && bridge.RuntimeMaskMap != null ? bridge.RuntimeMaskMap : FindMaskMap(material);
                    hash = hash * 31 + (int)materials[slot].revision;
                    hash = hash * 31 + (baseMap != null ? baseMap.GetInstanceID() : 0);
                    hash = hash * 31 + (emissionMap != null ? emissionMap.GetInstanceID() : 0);
                    hash = hash * 31 + (normalMap != null ? normalMap.GetInstanceID() : 0);
                    hash = hash * 31 + (maskMap != null ? maskMap.GetInstanceID() : 0);
                    hasLiveTextures |= baseMap is RenderTexture || emissionMap is RenderTexture ||
                                       normalMap is RenderTexture || maskMap is RenderTexture;
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
            normalArray = CreateArray("GI Normal Array", GraphicsFormat.R8G8B8A8_UNorm);
            maskArray = CreateArray("GI Mask Array", GraphicsFormat.R8G8B8A8_UNorm);
            baseColorHandle = RTHandles.Alloc(baseColorArray);
            emissionHandle = RTHandles.Alloc(emissionArray);
            normalHandle = RTHandles.Alloc(normalArray);
            maskHandle = RTHandles.Alloc(maskArray);
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
            baseColorHandle?.Release();
            emissionHandle?.Release();
            normalHandle?.Release();
            maskHandle?.Release();
            baseColorHandle = null;
            emissionHandle = null;
            normalHandle = null;
            maskHandle = null;
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
            if (normalArray != null)
            {
                normalArray.Release();
                CoreUtils.Destroy(normalArray);
            }
            if (maskArray != null)
            {
                maskArray.Release();
                CoreUtils.Destroy(maskArray);
            }
            baseColorArray = null;
            emissionArray = null;
            normalArray = null;
            maskArray = null;
        }

        public void Dispose()
        {
            ReleaseTextures();
            sliceCapacity = 0;
            SliceCount = 0;
            signature = 0;
            UpdatedThisFrame = false;
        }
    }
}
