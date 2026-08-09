#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Nanite.Editor
{
    static class NaniteMaterialResolveRegistryAuditMenu
    {
        sealed class TestFamily : INaniteMaterialResolveFamily
        {
            public string Name => "Nanite registry contract test";
            public Material ResolveMaterial { get; }

            internal TestFamily(Material resolveMaterial) => ResolveMaterial = resolveMaterial;

            public bool Supports(Material sourceMaterial) =>
                sourceMaterial != null && sourceMaterial.shader != null &&
                sourceMaterial.shader.name == "Universal Render Pipeline/Unlit";

            public void Bind(
                RasterCommandBuffer commandBuffer,
                Material sourceMaterial,
                MaterialPropertyBlock properties) { }
        }

        [MenuItem("Nanite/Diagnostics/Material Resolve Registry Contract")]
        public static void Audit()
        {
            Shader sourceShader = Shader.Find("Universal Render Pipeline/Unlit");
            Shader resolveShader = Shader.Find("Nanite/VBufferLitResolve");
            if (sourceShader == null || resolveShader == null)
                throw new InvalidOperationException("Material registry audit shaders are unavailable.");

            var source = new Material(sourceShader);
            var resolve = new Material(resolveShader);
            try
            {
                bool unsupportedBefore =
                    !NaniteSceneVisibilityBufferBackend.SupportsFormalResolveMaterial(source);
                int sceneRevisionBefore = NaniteRuntimeRegistry.Revision;
                int familyRevisionBefore = NaniteMaterialResolveRegistry.Revision;
                IDisposable registration = NaniteMaterialResolveRegistry.Register(new TestFamily(resolve));
                bool supportedDuring =
                    NaniteSceneVisibilityBufferBackend.SupportsFormalResolveMaterial(source);
                bool registrationPublished =
                    NaniteRuntimeRegistry.Revision > sceneRevisionBefore &&
                    NaniteMaterialResolveRegistry.Revision > familyRevisionBefore;
                registration.Dispose();
                bool unsupportedAfter =
                    !NaniteSceneVisibilityBufferBackend.SupportsFormalResolveMaterial(source);

                IDisposable aliasRegistration = NaniteLitAliasResolveFamily.Register(
                    sourceShader,
                    "Registry alias contract test");
                INaniteMaterialResolveFamily aliasFamily = null;
                bool aliasResolved =
                    NaniteMaterialResolveRegistry.TryResolve(source, out int aliasFamilyId) &&
                    NaniteMaterialResolveRegistry.TryGet(
                        aliasFamilyId,
                        out aliasFamily);
                var aliasProvider = aliasFamily as INaniteMaterialResolveDataProvider;
                bool aliasSupported =
                    NaniteSceneVisibilityBufferBackend.SupportsFormalResolveMaterial(source) &&
                    aliasResolved &&
                    aliasProvider != null;
                var materialData = new NaniteSceneVisibilityBufferBackend.GpuMaterialData();
                if (aliasSupported)
                {
                    aliasProvider.Populate(source, ref materialData);
                    _ = aliasProvider.GetCompatibilityHash(source);
                }
                aliasRegistration.Dispose();
                bool aliasDisposed =
                    !NaniteSceneVisibilityBufferBackend.SupportsFormalResolveMaterial(source);

                if (!unsupportedBefore || !supportedDuring || !registrationPublished ||
                    !unsupportedAfter || !aliasSupported || !aliasDisposed)
                    throw new InvalidOperationException(
                        "Nanite material resolve registry lifecycle contract failed.");

                Debug.Log(
                    "[Nanite][MaterialRegistryAudit] registration, alias data mapping, compatibility hash, " +
                    "revision invalidation and disposal passed.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(source);
                UnityEngine.Object.DestroyImmediate(resolve);
            }
        }

    }
}
#endif
