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
        static readonly int BumpMapId = Shader.PropertyToID("_BumpMap");
        static readonly int NormalMapId = Shader.PropertyToID("_NormalMap");
        static readonly int BumpScaleId = Shader.PropertyToID("_BumpScale");
        static readonly int MetallicGlossMapId = Shader.PropertyToID("_MetallicGlossMap");
        static readonly int MaskMapId = Shader.PropertyToID("_MaskMap");
        static readonly int ProxyEnabledId = Shader.PropertyToID("_GIProxyEnabled");
        static readonly int ProxyBaseColorId = Shader.PropertyToID("_GIProxyBaseColor");
        static readonly int ProxyEmissionId = Shader.PropertyToID("_GIProxyEmission");
        static readonly int ProxySurfaceId = Shader.PropertyToID("_GIProxySurface");
        static readonly int ProxyBaseMapId = Shader.PropertyToID("_GIProxyBaseMap");
        static readonly int ProxyEmissionMapId = Shader.PropertyToID("_GIProxyEmissionMap");
        static readonly int ProxyNormalMapId = Shader.PropertyToID("_GIProxyNormalMap");
        static readonly int ProxyMaskMapId = Shader.PropertyToID("_GIProxyMaskMap");
        static readonly int ProxyNormalScaleId = Shader.PropertyToID("_GIProxyNormalScale");
        static readonly int ProxyMetallicChannelId = Shader.PropertyToID("_GIProxyMetallicChannel");
        static readonly int ProxyRoughnessChannelId = Shader.PropertyToID("_GIProxyRoughnessChannel");
        static readonly int ProxyOpacityChannelId = Shader.PropertyToID("_GIProxyOpacityChannel");
        static readonly int ProxyAlphaSourceId = Shader.PropertyToID("_GIProxyAlphaSource");
        static readonly int ProxyMetallicRemapId = Shader.PropertyToID("_GIProxyMetallicRemap");
        static readonly int ProxyRoughnessRemapId = Shader.PropertyToID("_GIProxyRoughnessRemap");
        static readonly int ProxyOpacityRemapId = Shader.PropertyToID("_GIProxyOpacityRemap");
        static readonly int ProxyAlphaTestId = Shader.PropertyToID("_GIProxyAlphaTest");
        static readonly int ProxyDoubleSidedId = Shader.PropertyToID("_GIProxyDoubleSided");

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
                    normalMapST = new Vector4(1f, 1f, 0f, 0f),
                    maskMapST = new Vector4(1f, 1f, 0f, 0f),
                    maskRemap0 = new Vector4(1f, 0f, 1f, 0f),
                    maskRemap1 = new Vector4(1f, 0f, 1f, 0f),
                    materialId = 0,
                    revision = 1,
                    channels = PackChannels(
                        GIMaterialMaskChannel.Constant,
                        GIMaterialMaskChannel.Constant,
                        GIMaterialMaskChannel.Constant,
                        GIAlphaSource.BaseMapAlpha,
                        GIMaterialFamily.Auto)
                };
                bridge?.ApplyPropertyBlock(ref fallback, subMesh);
                bridge?.ApplyOverrides(ref fallback);
                int fallbackTextureHash = ComputeBridgeTextureHash(bridge);
                FinalizeFlagsAndRevision(
                    ref fallback, bridge, fallbackTextureHash, fallbackTextureHash);
                return fallback;
            }

            Color baseColor = material.HasProperty(BaseColorId)
                ? material.GetColor(BaseColorId)
                : material.HasProperty(ColorId) ? material.GetColor(ColorId) : Color.white;
            // URP serializes _EmissionColor even while emission is disabled. In particular,
            // Lit materials commonly retain a white value without the _EMISSION keyword.
            // Treating that dormant value as radiance turns the whole surface cache white.
            bool materialEmissionEnabled = material.IsKeywordEnabled("_EMISSION");
            Color emission = materialEmissionEnabled && material.HasProperty(EmissionColorId)
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
            if (material.doubleSidedGI) flags |= (uint)GIMaterialFlags.DoubleSided;
            if (emission.maxColorComponent > 1e-5f) flags |= (uint)GIMaterialFlags.Emissive;
            if (material.IsKeywordEnabled("_ALPHATEST_ON")) flags |= (uint)GIMaterialFlags.AlphaTested;
            Texture baseMap = material.HasProperty(BaseMapId) ? material.GetTexture(BaseMapId) :
                material.HasProperty(MainTexId) ? material.GetTexture(MainTexId) : null;
            Texture emissionMap = materialEmissionEnabled && material.HasProperty(EmissionMapId)
                ? material.GetTexture(EmissionMapId)
                : null;
            Texture normalMap = material.HasProperty(BumpMapId) ? material.GetTexture(BumpMapId) :
                material.HasProperty(NormalMapId) ? material.GetTexture(NormalMapId) : null;
            Texture maskMap = material.HasProperty(MetallicGlossMapId)
                ? material.GetTexture(MetallicGlossMapId)
                : material.HasProperty(MaskMapId) ? material.GetTexture(MaskMapId) : null;
            if (baseMap != null) flags |= (uint)GIMaterialFlags.HasBaseMap;
            if (emissionMap != null) flags |= (uint)GIMaterialFlags.HasEmissionMap;
            if (normalMap != null) flags |= (uint)GIMaterialFlags.HasNormalMap;
            if (maskMap != null) flags |= (uint)GIMaterialFlags.HasMaskMap;
            Vector2 baseScale = material.HasProperty(BaseMapId) ? material.GetTextureScale(BaseMapId) :
                material.HasProperty(MainTexId) ? material.GetTextureScale(MainTexId) : Vector2.one;
            Vector2 baseOffset = material.HasProperty(BaseMapId) ? material.GetTextureOffset(BaseMapId) :
                material.HasProperty(MainTexId) ? material.GetTextureOffset(MainTexId) : Vector2.zero;
            Vector2 emissionScale = material.HasProperty(EmissionMapId)
                ? material.GetTextureScale(EmissionMapId) : Vector2.one;
            Vector2 emissionOffset = material.HasProperty(EmissionMapId)
                ? material.GetTextureOffset(EmissionMapId) : Vector2.zero;
            int normalPropertyId = material.HasProperty(BumpMapId) ? BumpMapId : NormalMapId;
            Vector2 normalScaleUv = material.HasProperty(normalPropertyId)
                ? material.GetTextureScale(normalPropertyId) : Vector2.one;
            Vector2 normalOffsetUv = material.HasProperty(normalPropertyId)
                ? material.GetTextureOffset(normalPropertyId) : Vector2.zero;
            int maskPropertyId = material.HasProperty(MetallicGlossMapId) ? MetallicGlossMapId : MaskMapId;
            Vector2 maskScale = material.HasProperty(maskPropertyId)
                ? material.GetTextureScale(maskPropertyId) : Vector2.one;
            Vector2 maskOffset = material.HasProperty(maskPropertyId)
                ? material.GetTextureOffset(maskPropertyId) : Vector2.zero;
            float normalStrength = material.HasProperty(BumpScaleId)
                ? Mathf.Max(0f, material.GetFloat(BumpScaleId)) : 1f;
            bool urpMetallicMask = maskMap != null && material.HasProperty(MetallicGlossMapId);

            GIGpuMaterialData data = new GIGpuMaterialData
            {
                baseColor = baseColor,
                emissive = emission,
                surface = new Vector4(1f - Mathf.Clamp01(smoothness), Mathf.Clamp01(metallic), opacity, cutoff),
                baseMapST = new Vector4(baseScale.x, baseScale.y, baseOffset.x, baseOffset.y),
                emissionMapST = new Vector4(emissionScale.x, emissionScale.y, emissionOffset.x, emissionOffset.y),
                normalMapST = new Vector4(normalScaleUv.x, normalScaleUv.y, normalOffsetUv.x, normalOffsetUv.y),
                maskMapST = new Vector4(maskScale.x, maskScale.y, maskOffset.x, maskOffset.y),
                maskRemap0 = urpMetallicMask
                    ? new Vector4(Mathf.Clamp01(metallic), 0f, -Mathf.Clamp01(smoothness), 1f)
                    : new Vector4(1f, 0f, 1f, 0f),
                maskRemap1 = new Vector4(1f, 0f, normalStrength, 0f),
                materialId = (uint)material.GetInstanceID(),
                flags = flags,
                channels = PackChannels(
                    urpMetallicMask ? GIMaterialMaskChannel.Red : GIMaterialMaskChannel.Constant,
                    urpMetallicMask ? GIMaterialMaskChannel.Alpha : GIMaterialMaskChannel.Constant,
                    GIMaterialMaskChannel.Constant,
                    GIAlphaSource.BaseMapAlpha,
                    GIMaterialFamily.UrpLit)
            };
            ApplyShaderProxyPass(
                material, ref data, ref baseMap, ref emissionMap, ref normalMap, ref maskMap);
            bridge?.ApplyPropertyBlock(ref data, subMesh);
            bridge?.ApplyOverrides(ref data);
            if (bridge != null && bridge.materialFamily == GIMaterialFamily.ExplicitStylizedProxy)
            {
                baseMap = bridge.RuntimeBaseMap;
                emissionMap = bridge.RuntimeEmissionMap;
                normalMap = bridge.RuntimeNormalMap;
                maskMap = bridge.RuntimeMaskMap;
            }
            int textureHash = ComputeTextureHash(
                bridge != null && bridge.RuntimeBaseMap != null ? bridge.RuntimeBaseMap : baseMap,
                bridge != null && bridge.RuntimeEmissionMap != null ? bridge.RuntimeEmissionMap : emissionMap,
                bridge != null && bridge.RuntimeNormalMap != null ? bridge.RuntimeNormalMap : normalMap,
                bridge != null && bridge.RuntimeMaskMap != null ? bridge.RuntimeMaskMap : maskMap);
            int geometryTextureHash = ComputeTextureHash(
                bridge != null && bridge.RuntimeBaseMap != null ? bridge.RuntimeBaseMap : baseMap,
                null,
                bridge != null && bridge.RuntimeNormalMap != null ? bridge.RuntimeNormalMap : normalMap,
                bridge != null && bridge.RuntimeMaskMap != null ? bridge.RuntimeMaskMap : maskMap);
            FinalizeFlagsAndRevision(ref data, bridge, textureHash, geometryTextureHash);
            return data;
        }

        static void ApplyShaderProxyPass(
            Material material,
            ref GIGpuMaterialData data,
            ref Texture baseMap,
            ref Texture emissionMap,
            ref Texture normalMap,
            ref Texture maskMap)
        {
            if (!HasExplicitShaderProxy(material))
                return;

            data.flags = (uint)GIMaterialFlags.ExplicitProxy;
            data.baseColor = Vector4.one;
            data.emissive = Vector4.zero;
            data.surface = new Vector4(0.5f, 0f, 1f, 0.5f);
            if (material.HasProperty(ProxyBaseColorId))
                data.baseColor = material.GetColor(ProxyBaseColorId);
            if (material.HasProperty(ProxyEmissionId))
                data.emissive = material.GetColor(ProxyEmissionId);
            if (material.HasProperty(ProxySurfaceId))
                data.surface = material.GetVector(ProxySurfaceId);

            baseMap = material.HasProperty(ProxyBaseMapId) ? material.GetTexture(ProxyBaseMapId) : null;
            emissionMap = material.HasProperty(ProxyEmissionMapId) ? material.GetTexture(ProxyEmissionMapId) : null;
            normalMap = material.HasProperty(ProxyNormalMapId) ? material.GetTexture(ProxyNormalMapId) : null;
            maskMap = material.HasProperty(ProxyMaskMapId) ? material.GetTexture(ProxyMaskMapId) : null;
            data.baseMapST = GetTextureST(material, ProxyBaseMapId);
            data.emissionMapST = GetTextureST(material, ProxyEmissionMapId);
            data.normalMapST = GetTextureST(material, ProxyNormalMapId);
            data.maskMapST = GetTextureST(material, ProxyMaskMapId);
            if (baseMap != null) data.flags |= (uint)GIMaterialFlags.HasBaseMap;
            if (emissionMap != null) data.flags |= (uint)GIMaterialFlags.HasEmissionMap;
            if (normalMap != null) data.flags |= (uint)GIMaterialFlags.HasNormalMap;
            if (maskMap != null) data.flags |= (uint)GIMaterialFlags.HasMaskMap;
            if (material.HasProperty(ProxyAlphaTestId) && material.GetFloat(ProxyAlphaTestId) > 0.5f)
                data.flags |= (uint)GIMaterialFlags.AlphaTested;
            if (material.HasProperty(ProxyDoubleSidedId) && material.GetFloat(ProxyDoubleSidedId) > 0.5f)
                data.flags |= (uint)GIMaterialFlags.DoubleSided;

            Vector4 metallicRemap = GetVectorOrDefault(material, ProxyMetallicRemapId, new Vector4(0f, 1f, 0f, 0f));
            Vector4 roughnessRemap = GetVectorOrDefault(material, ProxyRoughnessRemapId, new Vector4(0f, 1f, 0f, 0f));
            Vector4 opacityRemap = GetVectorOrDefault(material, ProxyOpacityRemapId, new Vector4(0f, 1f, 0f, 0f));
            float normalStrength = material.HasProperty(ProxyNormalScaleId)
                ? Mathf.Max(0f, material.GetFloat(ProxyNormalScaleId)) : 1f;
            data.maskRemap0 = new Vector4(
                metallicRemap.y - metallicRemap.x, metallicRemap.x,
                roughnessRemap.y - roughnessRemap.x, roughnessRemap.x);
            data.maskRemap1 = new Vector4(
                opacityRemap.y - opacityRemap.x, opacityRemap.x, normalStrength, 0f);
            data.channels = PackChannels(
                ReadEnum(material, ProxyMetallicChannelId, GIMaterialMaskChannel.Constant),
                ReadEnum(material, ProxyRoughnessChannelId, GIMaterialMaskChannel.Constant),
                ReadEnum(material, ProxyOpacityChannelId, GIMaterialMaskChannel.Constant),
                ReadEnum(material, ProxyAlphaSourceId, GIAlphaSource.BaseMapAlpha),
                GIMaterialFamily.ExplicitStylizedProxy);
        }

        internal static bool HasExplicitShaderProxy(Material material) =>
            material != null && material.HasProperty(ProxyEnabledId) &&
            material.GetFloat(ProxyEnabledId) > 0.5f;

        internal static Texture GetExplicitProxyTexture(Material material, string propertyName)
        {
            if (!HasExplicitShaderProxy(material) || !material.HasProperty(propertyName))
                return null;
            return material.GetTexture(propertyName);
        }

        static Vector4 GetTextureST(Material material, int propertyId)
        {
            if (!material.HasProperty(propertyId))
                return new Vector4(1f, 1f, 0f, 0f);
            Vector2 scale = material.GetTextureScale(propertyId);
            Vector2 offset = material.GetTextureOffset(propertyId);
            return new Vector4(scale.x, scale.y, offset.x, offset.y);
        }

        static Vector4 GetVectorOrDefault(Material material, int propertyId, Vector4 fallback) =>
            material.HasProperty(propertyId) ? material.GetVector(propertyId) : fallback;

        static T ReadEnum<T>(Material material, int propertyId, T fallback) where T : struct
        {
            if (!material.HasProperty(propertyId))
                return fallback;
            int value = Mathf.RoundToInt(material.GetFloat(propertyId));
            return (T)System.Enum.ToObject(typeof(T), value);
        }

        static void FinalizeFlagsAndRevision(
            ref GIGpuMaterialData data,
            GIMaterialBridge bridge,
            int textureHash,
            int geometryTextureHash)
        {
            if (data.emissive.x > 1e-5f || data.emissive.y > 1e-5f || data.emissive.z > 1e-5f)
                data.flags |= (uint)GIMaterialFlags.Emissive;
            else
                data.flags &= ~(uint)GIMaterialFlags.Emissive;
            unchecked
            {
                int hash = (int)data.materialId;
                hash = hash * 31 + data.baseColor.GetHashCode();
                hash = hash * 31 + data.emissive.GetHashCode();
                hash = hash * 31 + data.surface.GetHashCode();
                hash = hash * 31 + data.baseMapST.GetHashCode();
                hash = hash * 31 + data.emissionMapST.GetHashCode();
                hash = hash * 31 + data.normalMapST.GetHashCode();
                hash = hash * 31 + data.maskMapST.GetHashCode();
                hash = hash * 31 + data.maskRemap0.GetHashCode();
                hash = hash * 31 + data.maskRemap1.GetHashCode();
                hash = hash * 31 + (int)data.channels;
                hash = hash * 31 + (int)data.flags;
                hash = hash * 31 + textureHash;
                hash = hash * 31 + (bridge != null ? bridge.RuntimeRevision : 0);
                data.revision = (uint)hash;

                int geometryHash = (int)data.materialId;
                const GIMaterialFlags geometryFlags = GIMaterialFlags.AlphaTested |
                                                      GIMaterialFlags.HasBaseMap |
                                                      GIMaterialFlags.HasNormalMap |
                                                      GIMaterialFlags.HasMaskMap;
                geometryHash = geometryHash * 31 + (int)((GIMaterialFlags)data.flags & geometryFlags);
                geometryHash = geometryHash * 31 + data.baseColor.w.GetHashCode();
                geometryHash = geometryHash * 31 + data.surface.z.GetHashCode();
                geometryHash = geometryHash * 31 + data.surface.w.GetHashCode();
                geometryHash = geometryHash * 31 + data.baseMapST.GetHashCode();
                geometryHash = geometryHash * 31 + data.normalMapST.GetHashCode();
                geometryHash = geometryHash * 31 + data.maskMapST.GetHashCode();
                geometryHash = geometryHash * 31 + data.maskRemap1.GetHashCode();
                geometryHash = geometryHash * 31 + (int)((data.channels >> 9) & 15u);
                geometryHash = geometryHash * 31 + geometryTextureHash;
                geometryHash = geometryHash * 31 +
                               (bridge != null ? bridge.RuntimeGeometryRevision : 0);
                data.geometryRevision = (uint)geometryHash;
            }
        }

        static int ComputeBridgeTextureHash(GIMaterialBridge bridge) => bridge == null
            ? 0
            : ComputeTextureHash(
                bridge.RuntimeBaseMap,
                bridge.RuntimeEmissionMap,
                bridge.RuntimeNormalMap,
                bridge.RuntimeMaskMap);

        static int ComputeTextureHash(Texture baseMap, Texture emissionMap, Texture normalMap, Texture maskMap)
        {
            unchecked
            {
                int hash = baseMap != null ? baseMap.GetInstanceID() : 0;
                hash = hash * 397 ^ (emissionMap != null ? emissionMap.GetInstanceID() : 0);
                hash = hash * 397 ^ (normalMap != null ? normalMap.GetInstanceID() : 0);
                return hash * 397 ^ (maskMap != null ? maskMap.GetInstanceID() : 0);
            }
        }

        static uint PackChannels(
            GIMaterialMaskChannel metallic,
            GIMaterialMaskChannel roughness,
            GIMaterialMaskChannel opacity,
            GIAlphaSource alpha,
            GIMaterialFamily family) =>
            ((uint)metallic & 7u) |
            (((uint)roughness & 7u) << 3) |
            (((uint)opacity & 7u) << 6) |
            (((uint)alpha & 15u) << 9) |
            (((uint)family & 255u) << 16);

        public static bool HasEmission(Material[] materials)
        {
            if (materials == null)
                return false;
            for (int i = 0; i < materials.Length; i++)
            {
                Material material = materials[i];
                if (HasExplicitShaderProxy(material) && material.HasProperty(ProxyEmissionId) &&
                    material.GetColor(ProxyEmissionId).maxColorComponent > 1e-5f)
                    return true;
                if (material != null && material.IsKeywordEnabled("_EMISSION") &&
                    material.HasProperty(EmissionColorId) &&
                    material.GetColor(EmissionColorId).maxColorComponent > 1e-5f)
                    return true;
            }
            return false;
        }

        public static bool HasEmission(Material[] materials, GIMaterialBridge bridge)
        {
            if (bridge != null &&
                (bridge.overrideEmission || bridge.materialFamily == GIMaterialFamily.ExplicitStylizedProxy) &&
                bridge.emission.maxColorComponent > 1e-5f)
                return true;
            return HasEmission(materials);
        }
    }
}
