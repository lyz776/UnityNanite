#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Nanite.Editor
{
    public static class NaniteGpuCullingValidationMenu
    {
        [MenuItem("Nanite/Validate GPU Culling (Selected NaniteMesh)")]
        static void ValidateGpuCulling()
        {
            var mesh = Selection.activeObject as NaniteMesh;
            if (mesh == null)
            {
                Debug.LogWarning("[Nanite] 请先在 Project 窗口选中 NaniteMesh 资源。");
                return;
            }

            Camera camera = SceneView.lastActiveSceneView != null ? SceneView.lastActiveSceneView.camera : Camera.main;
            if (camera == null)
            {
                Debug.LogWarning("[Nanite] 没找到可用相机（SceneView 或 Camera.main）。");
                return;
            }

            string[] guids = AssetDatabase.FindAssets("NaniteRuntimeCulling t:ComputeShader");
            if (guids == null || guids.Length == 0)
            {
                Debug.LogWarning("[Nanite] 没找到 NaniteRuntimeCulling.compute。");
                return;
            }

            string shaderPath = AssetDatabase.GUIDToAssetPath(guids[0]);
            var shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(shaderPath);
            if (shader == null)
            {
                Debug.LogWarning("[Nanite] 读取 ComputeShader 失败。");
                return;
            }

            var cpuVisible = new List<NaniteVisibleClusterRef>(8192);
            NaniteRuntimeCulling.CullVisibleClusters(mesh, camera, 2.0f, true, cpuVisible, out var cpuStats);

            var gpuVisible = new List<NaniteVisibleClusterRef>(8192);
            var backend = new NaniteGpuCullingBackend();
            if (!backend.Initialize(mesh, shader))
            {
                backend.Dispose();
                Debug.LogError("[Nanite] GPU culling backend 初始化失败。");
                return;
            }

            if (!backend.Run(camera, 2.0f, Matrix4x4.identity, 1f, gpuVisible, out var gpuStats))
            {
                backend.Dispose();
                Debug.LogError("[Nanite] GPU culling 执行失败。");
                return;
            }

            backend.Dispose();

            var cpuSet = new HashSet<long>();
            var gpuSet = new HashSet<long>();
            for (int i = 0; i < cpuVisible.Count; i++)
                cpuSet.Add(((long)cpuVisible[i].pageIndex << 32) | (uint)cpuVisible[i].clusterIndex);
            for (int i = 0; i < gpuVisible.Count; i++)
                gpuSet.Add(((long)gpuVisible[i].pageIndex << 32) | (uint)gpuVisible[i].clusterIndex);

            int missing = 0;
            foreach (long k in cpuSet)
                if (!gpuSet.Contains(k))
                    missing++;

            int extra = 0;
            foreach (long k in gpuSet)
                if (!cpuSet.Contains(k))
                    extra++;

            bool ok = missing == 0 && extra == 0;
            string report =
                $"GPU可见簇={gpuSet.Count}, CPU可见簇={cpuSet.Count}, 缺失={missing}, 额外={extra}\n" +
                $"GPU测试: inst={gpuStats.testedInstances} node={gpuStats.testedNodes} part={gpuStats.testedParts} cluster={gpuStats.testedClusters}\n" +
                $"CPU测试: inst={cpuStats.testedInstances} node={cpuStats.testedNodes} part={cpuStats.testedParts} cluster={cpuStats.testedClusters}";

            if (ok)
                Debug.Log("[Nanite] GPU 剔除验证通过\n" + report);
            else
                Debug.LogError("[Nanite] GPU 剔除验证失败\n" + report);
        }

        [MenuItem("Nanite/Validate GPU Culling (Selected NaniteMesh)", true)]
        static bool ValidateGpuCullingValidate() => Selection.activeObject is NaniteMesh;
    }
}
#endif
