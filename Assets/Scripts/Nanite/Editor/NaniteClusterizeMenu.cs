#if UNITY_EDITOR
using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Nanite.Editor
{
    static class NaniteBakeMenu
    {
        [MenuItem("Nanite/Bake Nanite...")]
        static void OpenBakeWindow() => NaniteBakeWindow.Open();

        [MenuItem("Assets/Nanite/Quick Bake", true)]
        static bool ValidateQuickBake()
        {
            string path = AssetDatabase.GetAssetPath(Selection.activeObject);
            return !string.IsNullOrEmpty(path) && AssetImporter.GetAtPath(path) is ModelImporter;
        }

        [MenuItem("Assets/Nanite/Quick Bake")]
        static void QuickBake()
        {
            string path = AssetDatabase.GetAssetPath(Selection.activeObject);
            GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            Mesh mesh = FindLargestStaticMesh(model);
            if (mesh == null)
            {
                Debug.LogError("[Nanite] Quick Bake requires a project model containing a static MeshFilter.");
                return;
            }

            if (AssetImporter.GetAtPath(path) is ModelImporter importer && !importer.isReadable)
            {
                importer.isReadable = true;
                importer.SaveAndReimport();
                model = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                mesh = FindLargestStaticMesh(model);
                if (mesh == null)
                {
                    Debug.LogError("[Nanite] Model no longer contains a static MeshFilter after reimport.");
                    return;
                }
            }

            try
            {
                NaniteAssetBaker.Bake(mesh);
                Selection.activeObject = AssetDatabase.LoadAssetAtPath<NaniteMesh>(
                    path.Substring(0, path.Length - System.IO.Path.GetExtension(path).Length) + "_mesh.asset");
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
        }

        static long TriangleCount(Mesh mesh)
        {
            if (mesh == null)
                return 0;
            long count = 0;
            for (int index = 0; index < mesh.subMeshCount; index++)
                count += mesh.GetIndexCount(index) / 3;
            return count;
        }

        static Mesh FindLargestStaticMesh(GameObject model) => model == null
            ? null
            : model.GetComponentsInChildren<MeshFilter>(true)
                .Where(filter => filter.sharedMesh != null)
                .OrderByDescending(filter => TriangleCount(filter.sharedMesh))
                .Select(filter => filter.sharedMesh)
                .FirstOrDefault();
    }
}
#endif
