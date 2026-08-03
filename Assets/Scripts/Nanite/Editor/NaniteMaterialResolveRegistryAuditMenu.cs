#if UNITY_EDITOR
using System;
using System.Reflection;
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

        [MenuItem("Nanite/Audit/Material Resolve Registry Contract")]
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

        public static void AuditMaterialRefreshBatch()
        {
            NaniteMesh mesh = AssetDatabase.LoadAssetAtPath<NaniteMesh>("Assets/toyota_ft1_mesh.asset");
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (mesh == null || shader == null)
                throw new InvalidOperationException("Material refresh audit prerequisites are unavailable.");

            var sourceMaterial = new Material(shader);
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            var host = new GameObject("Nanite material refresh audit");
            var backend = new NaniteSceneVisibilityBufferBackend();
            try
            {
                host.AddComponent<MeshFilter>().sharedMesh = mesh.sourceMesh;
                host.AddComponent<MeshRenderer>().sharedMaterial = sourceMaterial;
                var proxy = host.AddComponent<NaniteRuntimeProxy>();
                proxy.naniteMesh = mesh;
                proxy.resolveMaterials = new[] { sourceMaterial };
                proxy.renderingMode = NaniteRenderingMode.Nanite;
                proxy.MarkRenderDataDirty();
                NaniteRuntimeProxy[] proxies = { proxy };

                if (!backend.EnsureInitialized(proxies))
                    throw new InvalidOperationException("Initial GPU Scene build failed.");
                int initialGeneration = backend.GeometryGeneration;
                int initialDataSignature = PrivateInt(backend, "materialDataSignature");

                sourceMaterial.SetColor("_BaseColor", new Color(0.17f, 0.43f, 0.79f, 1f));
                proxy.MarkMaterialsDirty();
                if (!backend.EnsureInitialized(proxies))
                    throw new InvalidOperationException("Scalar material refresh failed.");
                int scalarGeneration = backend.GeometryGeneration;
                int scalarDataSignature = PrivateInt(backend, "materialDataSignature");
                if (scalarGeneration != initialGeneration || scalarDataSignature == initialDataSignature)
                    throw new InvalidOperationException(
                        "Scalar material change rebuilt geometry or failed to update material data.");

                sourceMaterial.SetTexture("_BaseMap", texture);
                proxy.MarkMaterialsDirty();
                if (!backend.EnsureInitialized(proxies))
                    throw new InvalidOperationException("Texture material refresh failed.");
                if (backend.GeometryGeneration <= scalarGeneration)
                    throw new InvalidOperationException(
                        "Texture binding change did not rebuild compatibility bins.");

                Debug.Log(
                    "[Nanite][MaterialRefreshAudit] default Nanite mode, scalar buffer-only refresh " +
                    "and texture/bin rebuild passed.");
            }
            finally
            {
                backend.Dispose();
                UnityEngine.Object.DestroyImmediate(host);
                UnityEngine.Object.DestroyImmediate(sourceMaterial);
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        static int PrivateInt(object instance, string fieldName)
        {
            FieldInfo field = instance.GetType().GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null)
                throw new MissingFieldException(instance.GetType().FullName, fieldName);
            return (int)field.GetValue(instance);
        }
    }
}
#endif
