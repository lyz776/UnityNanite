using UnityEngine;

namespace RealtimeGI
{
    static class GIMaterialUtility
    {
        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        static readonly int ColorId = Shader.PropertyToID("_Color");
        static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");
        static readonly int SmoothnessId = Shader.PropertyToID("_Smoothness");
        static readonly int MetallicId = Shader.PropertyToID("_Metallic");
        static readonly int CutoffId = Shader.PropertyToID("_Cutoff");
        static readonly int SurfaceId = Shader.PropertyToID("_Surface");
        static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");
        static readonly int MainTexId = Shader.PropertyToID("_MainTex");
        static readonly int EmissionMapId = Shader.PropertyToID("_EmissionMap");

        public static GIGpuMaterialData Build(
            Material material,
            GIMaterialBridge bridge = null,
            int subMesh = 0)
        {
            if (material == null)
            {
                GIGpuMaterialData fallback = new GIGpuMaterialData
                {
                    baseColor = Vector4.one,
                    emissive = Vector4.zero,
                    surface = new Vector4(0.5f, 0f, 1f, 0.5f),
                    baseMapST = new Vector4(1f, 1f, 0f, 0f),
                    emissionMapST = new Vector4(1f, 1f, 0f, 0f),
                    materialId = 0,
                    revision = 1,
                    textureIndex = uint.MaxValue
                };
                bridge?.ApplyPropertyBlock(ref fallback, subMesh);
                bridge?.ApplyOverrides(ref fallback);
                FinalizeFlagsAndRevision(ref fallback, bridge);
                return fallback;
            }

            Color baseColor = material.HasProperty(BaseColorId)
                ? material.GetColor(BaseColorId)
                : material.HasProperty(ColorId) ? material.GetColor(ColorId) : Color.white;
            Color emission = material.HasProperty(EmissionColorId)
                ? material.GetColor(EmissionColorId)
                : Color.black;
            float smoothness = material.HasProperty(SmoothnessId)
                ? material.GetFloat(SmoothnessId)
                : 0.5f;
            float metallic = material.HasProperty(MetallicId)
                ? material.GetFloat(MetallicId)
                : 0f;
            float cutoff = material.HasProperty(CutoffId) ? material.GetFloat(CutoffId) : 0.5f;
            float opacity = material.HasProperty(SurfaceId) && material.GetFloat(SurfaceId) > 0.5f
                ? Mathf.Clamp01(baseColor.a)
                : 1f;

            uint flags = 0;
            if (material.doubleSidedGI) flags |= 1u;
            if (emission.maxColorComponent > 1e-5f) flags |= 2u;
            if (material.IsKeywordEnabled("_ALPHATEST_ON")) flags |= 4u;
            Texture baseMap = material.HasProperty(BaseMapId) ? material.GetTexture(BaseMapId) :
                material.HasProperty(MainTexId) ? material.GetTexture(MainTexId) : null;
            Texture emissionMap = material.HasProperty(EmissionMapId) ? material.GetTexture(EmissionMapId) : null;
            if (baseMap != null) flags |= 8u;
            if (emissionMap != null) flags |= 16u;
            Vector2 baseScale = material.HasProperty(BaseMapId) ? material.GetTextureScale(BaseMapId) :
                material.HasProperty(MainTexId) ? material.GetTextureScale(MainTexId) : Vector2.one;
            Vector2 baseOffset = material.HasProperty(BaseMapId) ? material.GetTextureOffset(BaseMapId) :
                material.HasProperty(MainTexId) ? material.GetTextureOffset(MainTexId) : Vector2.zero;
            Vector2 emissionScale = material.HasProperty(EmissionMapId)
                ? material.GetTextureScale(EmissionMapId) : Vector2.one;
            Vector2 emissionOffset = material.HasProperty(EmissionMapId)
                ? material.GetTextureOffset(EmissionMapId) : Vector2.zero;

            GIGpuMaterialData data = new GIGpuMaterialData
            {
                baseColor = baseColor,
                emissive = emission,
                surface = new Vector4(1f - Mathf.Clamp01(smoothness), Mathf.Clamp01(metallic), opacity, cutoff),
                baseMapST = new Vector4(baseScale.x, baseScale.y, baseOffset.x, baseOffset.y),
                emissionMapST = new Vector4(emissionScale.x, emissionScale.y, emissionOffset.x, emissionOffset.y),
                materialId = (uint)material.GetInstanceID(),
                flags = flags,
                textureIndex = (uint)((baseMap != null ? baseMap.GetInstanceID() : 0) * 397 ^
                                      (emissionMap != null ? emissionMap.GetInstanceID() : 0))
            };
            bridge?.ApplyPropertyBlock(ref data, subMesh);
            bridge?.ApplyOverrides(ref data);
            FinalizeFlagsAndRevision(ref data, bridge);
            return data;
        }

        static void FinalizeFlagsAndRevision(ref GIGpuMaterialData data, GIMaterialBridge bridge)
        {
            if (data.emissive.x > 1e-5f || data.emissive.y > 1e-5f || data.emissive.z > 1e-5f)
                data.flags |= 2u;
            else
                data.flags &= ~2u;
            unchecked
            {
                int hash = (int)data.materialId;
                hash = hash * 31 + data.baseColor.GetHashCode();
                hash = hash * 31 + data.emissive.GetHashCode();
                hash = hash * 31 + data.surface.GetHashCode();
                hash = hash * 31 + data.baseMapST.GetHashCode();
                hash = hash * 31 + data.emissionMapST.GetHashCode();
                hash = hash * 31 + (int)data.textureIndex;
                hash = hash * 31 + (int)data.flags;
                hash = hash * 31 + (bridge != null ? bridge.RuntimeRevision : 0);
                data.revision = (uint)hash;
            }
        }

        public static bool HasEmission(Material[] materials)
        {
            if (materials == null)
                return false;
            for (int i = 0; i < materials.Length; i++)
            {
                Material material = materials[i];
                if (material != null && material.HasProperty(EmissionColorId) &&
                    material.GetColor(EmissionColorId).maxColorComponent > 1e-5f)
                    return true;
            }
            return false;
        }

        public static bool HasEmission(Material[] materials, GIMaterialBridge bridge)
        {
            if (bridge != null && bridge.overrideEmission && bridge.emission.maxColorComponent > 1e-5f)
                return true;
            return HasEmission(materials);
        }
    }
}
