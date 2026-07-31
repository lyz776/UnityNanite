using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Nanite
{
    /// <summary>
    /// Production adapter for opaque Lit-like shaders that use project-specific
    /// property names. Geometry and material IDs remain in the shared VBuffer;
    /// this adapter only translates the source material into the common GBuffer
    /// resolve contract. Unsupported shader programs still stay on their native
    /// Renderer.
    /// </summary>
    public sealed class NaniteLitAliasResolveFamily :
        INaniteMaterialResolveFamily,
        INaniteMaterialResolveDataProvider,
        IDisposable
    {
        sealed class Lease : IDisposable
        {
            IDisposable registration;
            NaniteLitAliasResolveFamily family;

            internal Lease(IDisposable registration, NaniteLitAliasResolveFamily family)
            {
                this.registration = registration;
                this.family = family;
            }

            public void Dispose()
            {
                registration?.Dispose();
                registration = null;
                family?.Dispose();
                family = null;
            }
        }

        readonly Shader sourceShader;
        readonly string baseMapProperty;
        readonly string baseColorProperty;
        readonly string normalMapProperty;
        readonly string metallicProperty;
        readonly string smoothnessProperty;
        readonly string roughnessProperty;
        readonly string emissionMapProperty;
        readonly string emissionColorProperty;
        readonly Material resolveMaterial;

        public string Name { get; }
        public Material ResolveMaterial => resolveMaterial;

        NaniteLitAliasResolveFamily(
            Shader sourceShader,
            string name,
            string baseMapProperty,
            string baseColorProperty,
            string normalMapProperty,
            string metallicProperty,
            string smoothnessProperty,
            string roughnessProperty,
            string emissionMapProperty,
            string emissionColorProperty)
        {
            this.sourceShader = sourceShader != null
                ? sourceShader
                : throw new ArgumentNullException(nameof(sourceShader));
            Name = string.IsNullOrWhiteSpace(name) ? sourceShader.name : name;
            this.baseMapProperty = baseMapProperty;
            this.baseColorProperty = baseColorProperty;
            this.normalMapProperty = normalMapProperty;
            this.metallicProperty = metallicProperty;
            this.smoothnessProperty = smoothnessProperty;
            this.roughnessProperty = roughnessProperty;
            this.emissionMapProperty = emissionMapProperty;
            this.emissionColorProperty = emissionColorProperty;
            Shader resolveShader = Shader.Find("Nanite/VBufferLitResolve");
            if (resolveShader == null)
                throw new InvalidOperationException("Nanite/VBufferLitResolve is unavailable.");
            resolveMaterial = new Material(resolveShader)
            {
                name = $"Nanite Resolve ({Name})",
                hideFlags = HideFlags.HideAndDontSave
            };
        }

        public static IDisposable Register(
            Shader sourceShader,
            string name = null,
            string baseMapProperty = "_BaseMap",
            string baseColorProperty = "_BaseColor",
            string normalMapProperty = "_BumpMap",
            string metallicProperty = "_Metallic",
            string smoothnessProperty = "_Smoothness",
            string roughnessProperty = null,
            string emissionMapProperty = "_EmissionMap",
            string emissionColorProperty = "_EmissionColor")
        {
            var family = new NaniteLitAliasResolveFamily(
                sourceShader,
                name,
                baseMapProperty,
                baseColorProperty,
                normalMapProperty,
                metallicProperty,
                smoothnessProperty,
                roughnessProperty,
                emissionMapProperty,
                emissionColorProperty);
            try
            {
                return new Lease(NaniteMaterialResolveRegistry.Register(family), family);
            }
            catch
            {
                family.Dispose();
                throw;
            }
        }

        public bool Supports(Material sourceMaterial) =>
            sourceMaterial != null && sourceMaterial.shader == sourceShader;

        public void Bind(
            RasterCommandBuffer commandBuffer,
            Material sourceMaterial,
            MaterialPropertyBlock properties)
        {
            if (properties == null)
                throw new ArgumentNullException(nameof(properties));
            properties.SetTexture(
                "_BaseMap",
                Texture(sourceMaterial, baseMapProperty, Texture2D.whiteTexture));
            properties.SetTexture(
                "_BumpMap",
                Texture(
                    sourceMaterial,
                    normalMapProperty,
                    Texture2D.normalTexture != null ? Texture2D.normalTexture : Texture2D.whiteTexture));
            properties.SetTexture("_MetallicGlossMap", Texture2D.blackTexture);
            properties.SetTexture("_OcclusionMap", Texture2D.whiteTexture);
            properties.SetTexture(
                "_EmissionMap",
                Texture(sourceMaterial, emissionMapProperty, Texture2D.blackTexture));
        }

        public void Populate(
            Material sourceMaterial,
            ref NaniteSceneVisibilityBufferBackend.GpuMaterialData data)
        {
            data.baseColor = ColorValue(sourceMaterial, baseColorProperty, Color.white);
            data.emissionColor = ColorValue(sourceMaterial, emissionColorProperty, Color.black);
            if (Has(sourceMaterial, baseMapProperty))
            {
                Vector2 scale = sourceMaterial.GetTextureScale(baseMapProperty);
                Vector2 offset = sourceMaterial.GetTextureOffset(baseMapProperty);
                data.baseMapST = new Vector4(scale.x, scale.y, offset.x, offset.y);
            }
            float smoothness = FloatValue(sourceMaterial, smoothnessProperty, float.NaN);
            if (float.IsNaN(smoothness))
                smoothness = 1f - FloatValue(sourceMaterial, roughnessProperty, 0.5f);
            data.surface0.y = Mathf.Clamp01(smoothness);
            data.surface0.z = Mathf.Clamp01(FloatValue(sourceMaterial, metallicProperty, 0f));
            data.surface1.z = HasTexture(sourceMaterial, baseMapProperty) ? 1f : 0f;
            data.surface1.w = HasTexture(sourceMaterial, normalMapProperty) ? 1f : 0f;
            data.surface2.z = HasTexture(sourceMaterial, emissionMapProperty) ? 1f : 0f;
            data.surface2.w =
                data.surface2.z > 0.5f ||
                Mathf.Max(data.emissionColor.x, Mathf.Max(data.emissionColor.y, data.emissionColor.z)) > 0.0001f
                    ? 1f
                    : 0f;
        }

        public int GetCompatibilityHash(Material sourceMaterial)
        {
            unchecked
            {
                int hash = sourceShader.GetInstanceID();
                hash = hash * 397 ^ TextureId(sourceMaterial, baseMapProperty);
                hash = hash * 397 ^ TextureId(sourceMaterial, normalMapProperty);
                hash = hash * 397 ^ TextureId(sourceMaterial, emissionMapProperty);
                return hash;
            }
        }

        public void Dispose()
        {
            if (resolveMaterial == null)
                return;
            if (Application.isPlaying)
                UnityEngine.Object.Destroy(resolveMaterial);
            else
                UnityEngine.Object.DestroyImmediate(resolveMaterial);
        }

        static bool Has(Material material, string property) =>
            material != null && !string.IsNullOrEmpty(property) && material.HasProperty(property);

        static bool HasTexture(Material material, string property) =>
            Has(material, property) && material.GetTexture(property) != null;

        static int TextureId(Material material, string property)
        {
            Texture texture = Has(material, property) ? material.GetTexture(property) : null;
            return texture != null ? texture.GetInstanceID() : 0;
        }

        static Texture Texture(Material material, string property, Texture fallback)
        {
            Texture texture = Has(material, property) ? material.GetTexture(property) : null;
            return texture != null ? texture : fallback;
        }

        static float FloatValue(Material material, string property, float fallback) =>
            Has(material, property) ? material.GetFloat(property) : fallback;

        static Color ColorValue(Material material, string property, Color fallback) =>
            Has(material, property) ? material.GetColor(property) : fallback;
    }
}
