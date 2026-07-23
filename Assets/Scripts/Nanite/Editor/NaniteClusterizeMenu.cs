using System.Linq;
using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEngine;

namespace Nanite.Editor
{
    /// <summary>
    /// 在编辑器里验证第一步 Clusterize：选中带 MeshFilter 的物体后运行菜单。
    /// </summary>
    public static class NaniteClusterizeMenu
    {
        [MenuItem("Nanite/Cancel Current Bake")]
        private static void CancelCurrentBake()
        {
            if (!NaniteAssetBaker.IsBakeRunning)
            {
                Debug.Log("[Nanite] 当前没有进行中的 Bake。");
                return;
            }

            NaniteAssetBaker.RequestCancel();
        }

        [Shortcut("Nanite/Cancel Current Bake", KeyCode.F8)]
        private static void CancelCurrentBakeShortcut()
        {
            CancelCurrentBake();
        }

        [MenuItem("Nanite/Clusterize Selected Mesh")]
        private static void ClusterizeSelectedMesh()
        {
            var go = Selection.activeGameObject;
            if (go == null)
            {
                Debug.LogWarning("[Nanite] 请先在 Hierarchy 里选中一个带 MeshFilter 的物体。");
                return;
            }

            var meshFilter = go.GetComponent<MeshFilter>();
            if (meshFilter == null || meshFilter.sharedMesh == null)
            {
                Debug.LogWarning("[Nanite] 选中物体没有 MeshFilter 或 sharedMesh 为空。");
                return;
            }

            var mesh = meshFilter.sharedMesh;
            if (!mesh.isReadable)
            {
                Debug.LogError(
                    "[Nanite] Mesh 不可读。请在 Model Import Settings 里勾选 Read/Write，或对该 Mesh 启用 Read/Write Enabled。");
                return;
            }

            var vertices = mesh.vertices;
            var indices = mesh.triangles;

            try
            {
                var clusters = MeshClusterizer.Clusterize(vertices, indices);

                int totalTris = clusters.Sum(c => c.indices.Length / 3);
                int maxTris = clusters.Max(c => c.indices.Length / 3);
                int minTris = clusters.Min(c => c.indices.Length / 3);

                Debug.Log(
                    $"[Nanite] Clusterize 成功\n" +
                    $"  网格: {mesh.name}\n" +
                    $"  顶点: {vertices.Length}  三角形: {indices.Length / 3}\n" +
                    $"  Cluster 数量: {clusters.Count}\n" +
                    $"  每簇三角形: min={minTris} max={maxTris} (上限 {MeshClusterizer.kClusterSize})\n" +
                    $"  索引总数校验: {totalTris} (应等于原三角形数 {indices.Length / 3})");
            }
            catch (System.DllNotFoundException)
            {
                Debug.LogError("[Nanite] 找不到 API_CPP.dll。请确认 Assets/Plugins/x86_64/API_CPP.dll 存在且为 x64。");
            }
            catch (System.EntryPointNotFoundException e)
            {
                Debug.LogError($"[Nanite] DLL 导出函数不匹配: {e.Message}");
            }
            catch (System.Exception e)
            {
                Debug.LogException(e);
            }
        }

        [MenuItem("Nanite/Build DAG (Full Offline)")]
        private static void BuildNaniteDag()
        {
            var go = Selection.activeGameObject;
            if (go == null)
            {
                Debug.LogWarning("[Nanite] 请先在 Hierarchy 里选中一个带 MeshFilter 的物体。");
                return;
            }

            var meshFilter = go.GetComponent<MeshFilter>();
            if (meshFilter == null || meshFilter.sharedMesh == null)
            {
                Debug.LogWarning("[Nanite] 选中物体没有 MeshFilter 或 sharedMesh 为空。");
                return;
            }

            var mesh = meshFilter.sharedMesh;
            if (!mesh.isReadable)
            {
                Debug.LogError("[Nanite] Mesh 不可读。请在 Import Settings 里勾选 Read/Write Enabled。");
                return;
            }

            var vertices = mesh.vertices;
            var normals = mesh.normals;
            var indices = mesh.triangles;

            try
            {
                EditorUtility.DisplayProgressBar("Nanite", "Build DAG…", 0.5f);
                var subMesh = NaniteMeshBuilder.Build(vertices, normals, indices);

                int mip0 = subMesh.clusterList.FindAll(c => c.mip == 0).Count;
                Debug.Log(
                    $"[Nanite] Build DAG 完成\n" +
                    $"  网格: {mesh.name}\n" +
                    $"  总 Cluster 数: {subMesh.clusterList.Count} (Mip0: {mip0})\n" +
                    $"  Cluster Group 数: {subMesh.clusterGroupList.Count}\n" +
                    $"  maxMipLevel: {subMesh.maxMipLevel}");
            }
            catch (System.DllNotFoundException)
            {
                Debug.LogError("[Nanite] 找不到 API_CPP.dll。");
            }
            catch (System.EntryPointNotFoundException e)
            {
                Debug.LogError(
                    $"[Nanite] DLL 导出函数缺失: {e.Message}\n" +
                    "请用更新后的 api_cpp.cpp 重新编译 API_CPP.dll（需含 SimplifyWithAttributes、ComputeClusterBounds 等）。");
            }
            catch (System.Exception e)
            {
                Debug.LogException(e);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        [MenuItem("Nanite/Bake Nanite Asset (Part + Page)")]
        private static void BakeNaniteAsset()
        {
            Mesh mesh = null;
            if (Selection.activeObject is Mesh selectedMesh)
                mesh = selectedMesh;
            else if (Selection.activeGameObject != null)
            {
                var mf = Selection.activeGameObject.GetComponent<MeshFilter>();
                mesh = mf != null ? mf.sharedMesh : null;
            }

            if (mesh == null)
            {
                Debug.LogWarning("[Nanite] 请在 Project 选中 Mesh，或 Hierarchy 选中带 MeshFilter 的物体。");
                return;
            }

            try
            {
                EditorUtility.DisplayProgressBar("Nanite", "Bake Part + Page…", 0.3f);
                NaniteAssetBaker.Bake(mesh);
            }
            catch (System.OperationCanceledException)
            {
                Debug.LogWarning("[Nanite] Bake 已取消。");
            }
            catch (System.Exception e)
            {
                Debug.LogException(e);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        [MenuItem("Nanite/Bake Nanite Asset (Part + Page)", true)]
        private static bool BakeNaniteAssetValidate() =>
            Selection.activeObject is Mesh ||
            (Selection.activeGameObject != null &&
             Selection.activeGameObject.GetComponent<MeshFilter>() != null);

        [MenuItem("Nanite/Build DAG (Full Offline)", true)]
        private static bool BuildNaniteDagValidate() =>
            Selection.activeGameObject != null;

        [MenuItem("Nanite/Clusterize Selected Mesh", true)]
        private static bool ClusterizeSelectedMeshValidate() =>
            Selection.activeGameObject != null;
    }
}
