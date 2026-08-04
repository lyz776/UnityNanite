using UnityEngine;

namespace RealtimeGI
{
    public enum GIDissolveMode
    {
        MaterialOnly = 0,
        DynamicGeometryProxy = 1
    }

    /// <summary>CPU-visible proxy for MaterialPropertyBlock and GPU-procedural material values.</summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public sealed class GIMaterialBridge : MonoBehaviour
    {
        [Header("Shader family / explicit proxy pass")]
        [Tooltip("ExplicitStylizedProxy ignores shader property naming and publishes the values below as the material's GI pass.")]
        public GIMaterialFamily materialFamily = GIMaterialFamily.Auto;
        [Tooltip("Force alpha testing in the GI proxy even if the visible shader has no _ALPHATEST_ON keyword.")]
        public bool alphaTested;
        public bool doubleSided;

        [Header("Live source")]
        [Tooltip("Read known URP values from this Renderer's MaterialPropertyBlock.")]
        public bool readMaterialPropertyBlock;

        [Header("Explicit GI proxy values")]
        public bool overrideBaseColor;
        public Color baseColor = Color.white;
        public bool overrideEmission;
        [ColorUsage(true, true)] public Color emission = Color.black;
        [Tooltip("Optional live texture proxy for procedural or RenderTexture-driven base color.")]
        public Texture runtimeBaseMap;
        [Tooltip("Optional live texture proxy for procedural or RenderTexture-driven emission.")]
        public Texture runtimeEmissionMap;
        [Tooltip("Tangent-space normal texture used by the GI proxy pass.")]
        public Texture runtimeNormalMap;
        [Tooltip("Packed metallic/roughness/opacity texture used by the GI proxy pass.")]
        public Texture runtimeMaskMap;
        public Vector4 proxyBaseMapST = new Vector4(1f, 1f, 0f, 0f);
        public Vector4 proxyEmissionMapST = new Vector4(1f, 1f, 0f, 0f);
        public Vector4 proxyNormalMapST = new Vector4(1f, 1f, 0f, 0f);
        public Vector4 proxyMaskMapST = new Vector4(1f, 1f, 0f, 0f);
        [Range(0f, 2f)] public float normalScale = 1f;
        public GIMaterialMaskChannel metallicChannel = GIMaterialMaskChannel.Red;
        public GIMaterialMaskChannel roughnessChannel = GIMaterialMaskChannel.Green;
        public GIMaterialMaskChannel opacityChannel = GIMaterialMaskChannel.Alpha;
        public GIAlphaSource alphaSource = GIAlphaSource.BaseMapAlpha;
        public Vector2 metallicRemap = new Vector2(0f, 1f);
        public Vector2 roughnessRemap = new Vector2(0f, 1f);
        public Vector2 opacityRemap = new Vector2(0f, 1f);
        public bool overrideSurface;
        [Range(0f, 1f)] public float roughness = 0.5f;
        [Range(0f, 1f)] public float metallic;
        [Range(0f, 1f)] public float opacity = 1f;
        [Range(0f, 1f)] public float alphaCutoff = 0.5f;

        [Header("Dissolve")]
        [Tooltip("MaterialOnly preserves occupancy. DynamicGeometryProxy requires a dynamic simplified mesh.")]
        public GIDissolveMode dissolveMode = GIDissolveMode.MaterialOnly;
        [Range(0f, 1f)] public float dissolveAmount;

        [SerializeField, Min(0)] int runtimeRevision;
        [SerializeField, Min(0)] int runtimeGeometryRevision;
        Renderer cachedRenderer;
        MaterialPropertyBlock propertyBlock;

        internal bool RequiresUniqueMaterialSlot => readMaterialPropertyBlock || overrideBaseColor ||
            overrideEmission || overrideSurface || runtimeBaseMap != null ||
            runtimeEmissionMap != null || runtimeNormalMap != null || runtimeMaskMap != null ||
            materialFamily == GIMaterialFamily.ExplicitStylizedProxy || alphaTested ||
            doubleSided || dissolveAmount > 0f;
        internal Texture RuntimeBaseMap => runtimeBaseMap;
        internal Texture RuntimeEmissionMap => runtimeEmissionMap;
        internal Texture RuntimeNormalMap => runtimeNormalMap;
        internal Texture RuntimeMaskMap => runtimeMaskMap;
        internal bool RequiresDynamicGeometry =>
            dissolveMode == GIDissolveMode.DynamicGeometryProxy && dissolveAmount > 0f;
        internal int RuntimeRevision => runtimeRevision;
        internal int RuntimeGeometryRevision => runtimeGeometryRevision;

        internal void ApplyPropertyBlock(ref GIGpuMaterialData data, int subMesh)
        {
            if (!readMaterialPropertyBlock)
                return;
            if (cachedRenderer == null)
                cachedRenderer = GetComponent<Renderer>();
            if (cachedRenderer == null)
                return;
            if (propertyBlock == null)
                propertyBlock = new MaterialPropertyBlock();
            propertyBlock.Clear();
            cachedRenderer.GetPropertyBlock(propertyBlock, subMesh);
            if (propertyBlock.isEmpty)
                return;

            int baseColorId = Shader.PropertyToID("_BaseColor");
            int colorId = Shader.PropertyToID("_Color");
            int emissionId = Shader.PropertyToID("_EmissionColor");
            int smoothnessId = Shader.PropertyToID("_Smoothness");
            int metallicId = Shader.PropertyToID("_Metallic");
            int cutoffId = Shader.PropertyToID("_Cutoff");
            if (propertyBlock.HasProperty(baseColorId)) data.baseColor = propertyBlock.GetColor(baseColorId);
            else if (propertyBlock.HasProperty(colorId)) data.baseColor = propertyBlock.GetColor(colorId);
            if (propertyBlock.HasProperty(emissionId)) data.emissive = propertyBlock.GetColor(emissionId);
            if (propertyBlock.HasProperty(smoothnessId)) data.surface.x = 1f - Mathf.Clamp01(propertyBlock.GetFloat(smoothnessId));
            if (propertyBlock.HasProperty(metallicId)) data.surface.y = Mathf.Clamp01(propertyBlock.GetFloat(metallicId));
            if (propertyBlock.HasProperty(cutoffId)) data.surface.w = Mathf.Clamp01(propertyBlock.GetFloat(cutoffId));
        }

        internal void ApplyOverrides(ref GIGpuMaterialData data)
        {
            if (materialFamily != GIMaterialFamily.Auto)
                data.channels = (data.channels & 0xff00ffffu) | ((uint)materialFamily << 16);
            bool explicitProxy = materialFamily == GIMaterialFamily.ExplicitStylizedProxy;
            if (explicitProxy)
            {
                data.flags = (uint)GIMaterialFlags.ExplicitProxy;
                data.baseColor = baseColor;
                data.emissive = emission;
                data.surface = new Vector4(roughness, metallic, opacity, alphaCutoff);
                data.baseMapST = proxyBaseMapST;
                data.emissionMapST = proxyEmissionMapST;
                data.normalMapST = proxyNormalMapST;
                data.maskMapST = proxyMaskMapST;
            }
            if (overrideBaseColor) data.baseColor = baseColor;
            if (overrideEmission) data.emissive = emission;
            if (runtimeBaseMap != null) data.flags |= (uint)GIMaterialFlags.HasBaseMap;
            if (runtimeEmissionMap != null) data.flags |= (uint)GIMaterialFlags.HasEmissionMap;
            if (runtimeNormalMap != null) data.flags |= (uint)GIMaterialFlags.HasNormalMap;
            if (runtimeMaskMap != null) data.flags |= (uint)GIMaterialFlags.HasMaskMap;
            if (alphaTested) data.flags |= (uint)GIMaterialFlags.AlphaTested;
            if (doubleSided) data.flags |= (uint)GIMaterialFlags.DoubleSided;
            if (overrideSurface) data.surface = new Vector4(roughness, metallic, opacity, alphaCutoff);
            data.maskRemap0 = new Vector4(
                metallicRemap.y - metallicRemap.x, metallicRemap.x,
                roughnessRemap.y - roughnessRemap.x, roughnessRemap.x);
            data.maskRemap1 = new Vector4(
                opacityRemap.y - opacityRemap.x, opacityRemap.x,
                Mathf.Max(0f, normalScale), 0f);
            data.channels = (data.channels & 0xffffe000u) |
                            ((uint)metallicChannel & 7u) |
                            (((uint)roughnessChannel & 7u) << 3) |
                            (((uint)opacityChannel & 7u) << 6) |
                            (((uint)alphaSource & 15u) << 9);
            if (dissolveMode == GIDissolveMode.MaterialOnly && dissolveAmount > 0f)
            {
                float visibility = 1f - Mathf.Clamp01(dissolveAmount);
                data.baseColor *= visibility;
                data.emissive *= visibility;
                data.surface.z *= visibility;
            }
        }

        public void MarkDirty()
        {
            unchecked { runtimeRevision++; }
            GISceneRegistry.NotifyChanged();
        }

        public void MarkGeometryDirty()
        {
            unchecked
            {
                runtimeRevision++;
                runtimeGeometryRevision++;
            }
            GISceneRegistry.NotifyChanged();
        }

        public void SetEmission(Color value)
        {
            overrideEmission = true;
            emission = value;
            MarkDirty();
        }

        public void SetDissolve(float value)
        {
            dissolveAmount = Mathf.Clamp01(value);
            if (dissolveMode == GIDissolveMode.DynamicGeometryProxy)
                MarkGeometryDirty();
            else
                MarkDirty();
        }

        void OnValidate()
        {
            roughness = Mathf.Clamp01(roughness);
            metallic = Mathf.Clamp01(metallic);
            opacity = Mathf.Clamp01(opacity);
            alphaCutoff = Mathf.Clamp01(alphaCutoff);
            normalScale = Mathf.Max(0f, normalScale);
            dissolveAmount = Mathf.Clamp01(dissolveAmount);
            MarkGeometryDirty();
        }
    }
}
