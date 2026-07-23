#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace Nanite.Editor
{
    public static class NaniteCullingValidationMenu
    {
        [MenuItem("Nanite/Validate BVH Culling (Selected NaniteMesh)")]
        static void ValidateSelectedNaniteMesh()
        {
            var mesh = Selection.activeObject as NaniteMesh;
            if (mesh == null)
            {
                Debug.LogWarning("[Nanite] 请在 Project 窗口先选中一个 NaniteMesh 资源。");
                return;
            }

            Camera camera = null;
            if (SceneView.lastActiveSceneView != null)
                camera = SceneView.lastActiveSceneView.camera;
            if (camera == null)
                camera = Camera.main;

            if (camera == null)
            {
                Debug.LogWarning("[Nanite] 没找到可用相机（SceneView 或 Camera.main）。");
                return;
            }

            bool ok = NaniteRuntimeCulling.ValidateBvhAgainstBruteForce(mesh, camera, 2.0f, out string report);
            if (ok)
            {
                Debug.Log($"[Nanite] BVH 剔除验证通过\n{report}");
            }
            else
            {
                Debug.LogError($"[Nanite] BVH 剔除验证失败\n{report}");
            }
        }

        [MenuItem("Nanite/Validate BVH Culling (Selected NaniteMesh)", true)]
        static bool ValidateSelectedNaniteMeshValidate() => Selection.activeObject is NaniteMesh;
    }
}
#endif
