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
        Renderer cachedRenderer;
        MaterialPropertyBlock propertyBlock;

        internal bool RequiresUniqueMaterialSlot => readMaterialPropertyBlock || overrideBaseColor ||
            overrideEmission || overrideSurface || runtimeBaseMap != null ||
            runtimeEmissionMap != null || dissolveAmount > 0f;
        internal Texture RuntimeBaseMap => runtimeBaseMap;
        internal Texture RuntimeEmissionMap => runtimeEmissionMap;
        internal bool RequiresDynamicGeometry =>
            dissolveMode == GIDissolveMode.DynamicGeometryProxy && dissolveAmount > 0f;
        internal int RuntimeRevision => runtimeRevision;

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
            if (overrideBaseColor) data.baseColor = baseColor;
            if (overrideEmission) data.emissive = emission;
            if (runtimeBaseMap != null) data.flags |= 8u;
            if (runtimeEmissionMap != null) data.flags |= 16u;
            if (overrideSurface) data.surface = new Vector4(roughness, metallic, opacity, alphaCutoff);
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

        public void SetEmission(Color value)
        {
            overrideEmission = true;
            emission = value;
            MarkDirty();
        }

        public void SetDissolve(float value)
        {
            dissolveAmount = Mathf.Clamp01(value);
            MarkDirty();
        }

        void OnValidate()
        {
            roughness = Mathf.Clamp01(roughness);
            metallic = Mathf.Clamp01(metallic);
            opacity = Mathf.Clamp01(opacity);
            alphaCutoff = Mathf.Clamp01(alphaCutoff);
            dissolveAmount = Mathf.Clamp01(dissolveAmount);
            MarkDirty();
        }
    }
}
