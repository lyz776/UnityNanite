#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Nanite.Editor
{
    public static class NaniteSmokeAutomation
    {
        public static void BakeToyota()
        {
            Mesh mesh = AssetDatabase.LoadAllAssetsAtPath("Assets/toyota_ft1.obj")
                .OfType<Mesh>()
                .OrderByDescending(candidate => candidate.triangles.Length)
                .FirstOrDefault();
            if (mesh == null)
                throw new InvalidOperationException("Toyota source mesh was not found.");
            Debug.Log($"[Nanite][SmokeAutomation] Baking {mesh.name}: triangles={mesh.triangles.Length / 3}.");
            NaniteAssetBaker.Bake(mesh);
            AssetDatabase.SaveAssets();
        }

        public static void BuildSmokePlayer()
        {
            NanitePortabilityBuildGuard.BuildSmokePlayer();
        }

        public static void BakeToyotaFourSubMeshes()
        {
            Mesh source = AssetDatabase.LoadAllAssetsAtPath("Assets/toyota_ft1.obj")
                .OfType<Mesh>()
                .OrderByDescending(candidate => candidate.triangles.Length)
                .FirstOrDefault();
            if (source == null)
                throw new InvalidOperationException("Toyota source mesh was not found.");

            const string resourcesFolder = "Assets/Resources";
            const string testsFolder = "Assets/Resources/NaniteTests";
            if (!AssetDatabase.IsValidFolder(resourcesFolder))
                AssetDatabase.CreateFolder("Assets", "Resources");
            if (!AssetDatabase.IsValidFolder(testsFolder))
                AssetDatabase.CreateFolder(resourcesFolder, "NaniteTests");

            const string meshPath = "Assets/Resources/NaniteTests/Toyota4Sub.asset";
            Mesh split = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
            if (split == null)
            {
                split = UnityEngine.Object.Instantiate(source);
                AssetDatabase.CreateAsset(split, meshPath);
            }
            else
            {
                EditorUtility.CopySerialized(source, split);
            }
            split.name = "Toyota4Sub";

            Vector3[] vertices = split.vertices;
            int[] triangles = source.triangles;
            var bins = new List<int>[4];
            for (int index = 0; index < bins.Length; index++) bins[index] = new List<int>();
            Vector3 center = source.bounds.center;
            for (int triangle = 0; triangle + 2 < triangles.Length; triangle += 3)
            {
                int a = triangles[triangle + 0];
                int b = triangles[triangle + 1];
                int c = triangles[triangle + 2];
                Vector3 centroid = (vertices[a] + vertices[b] + vertices[c]) / 3f;
                int bin = (centroid.x >= center.x ? 1 : 0) |
                          (centroid.z >= center.z ? 2 : 0);
                bins[bin].Add(a);
                bins[bin].Add(b);
                bins[bin].Add(c);
            }
            split.subMeshCount = bins.Length;
            for (int bin = 0; bin < bins.Length; bin++)
                split.SetTriangles(bins[bin], bin, false);
            split.RecalculateBounds();
            EditorUtility.SetDirty(split);
            AssetDatabase.SaveAssets();

            Debug.Log(
                $"[Nanite][SmokeAutomation] Baking {split.name}: subMeshes={split.subMeshCount}, " +
                $"triangles={triangles.Length / 3}.");
            NaniteAssetBaker.Bake(split);
            AssetDatabase.SaveAssets();
        }

        public static void BakeUniqueMeshStressAssets()
        {
            const string resourcesFolder = "Assets/Resources";
            const string testsFolder = "Assets/Resources/NaniteTests";
            const string uniqueFolder = "Assets/Resources/NaniteTests/Unique";
            if (!AssetDatabase.IsValidFolder(resourcesFolder))
                AssetDatabase.CreateFolder("Assets", "Resources");
            if (!AssetDatabase.IsValidFolder(testsFolder))
                AssetDatabase.CreateFolder(resourcesFolder, "NaniteTests");
            if (!AssetDatabase.IsValidFolder(uniqueFolder))
                AssetDatabase.CreateFolder(testsFolder, "Unique");

            string[] sourcePaths =
            {
                "Assets/simple_dragon.obj",
                "Assets/dragon.obj",
                "Assets/dragon_87k.obj"
            };
            for (int sourceIndex = 0; sourceIndex < sourcePaths.Length; sourceIndex++)
            {
                string sourcePath = sourcePaths[sourceIndex];
                if (AssetImporter.GetAtPath(sourcePath) is ModelImporter importer &&
                    !importer.isReadable)
                {
                    importer.isReadable = true;
                    importer.SaveAndReimport();
                }
                Mesh source = AssetDatabase.LoadAllAssetsAtPath(sourcePath)
                    .OfType<Mesh>()
                    .OrderByDescending(candidate => candidate.triangles.Length)
                    .FirstOrDefault();
                if (source == null)
                    throw new InvalidOperationException(
                        $"Unique-mesh stress source was not found: {sourcePath}");

                string safeName = Path.GetFileNameWithoutExtension(sourcePath);
                string meshPath = $"{uniqueFolder}/{safeName}.asset";
                // This directory is generated acceptance data. Recreate the Mesh
                // after enabling source readback; CopySerialized would preserve the
                // previous non-readable CPU payload and make the Bake fail again.
                if (AssetDatabase.LoadAssetAtPath<Mesh>(meshPath) != null)
                    AssetDatabase.DeleteAsset(meshPath);
                Mesh persistent = UnityEngine.Object.Instantiate(source);
                AssetDatabase.CreateAsset(persistent, meshPath);
                persistent.name = safeName;
                EditorUtility.SetDirty(persistent);
                AssetDatabase.SaveAssets();

                Debug.Log(
                    $"[Nanite][SmokeAutomation] Baking unique stress mesh {persistent.name}: " +
                    $"triangles={persistent.triangles.Length / 3}.");
                NaniteAssetBaker.Bake(persistent);
                AssetDatabase.SaveAssets();
            }
        }

        public static void AuditToyota()
        {
            var mesh = AssetDatabase.LoadAssetAtPath<NaniteMesh>("Assets/toyota_ft1_mesh.asset");
            if (mesh == null)
                throw new InvalidOperationException("Baked Toyota NaniteMesh was not found.");
            Debug.Log(NaniteLodErrorAuditMenu.AuditMesh(mesh));
        }
    }
}
#endif
