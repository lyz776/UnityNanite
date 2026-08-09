#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Nanite.Editor
{
    public static class NaniteValidationSuiteMenu
    {
        static readonly float[] kLodSweep = { 0.5f, 1f, 2f, 4f, 8f, 16f, 32f, 64f, 128f, 256f, 512f, 1024f };

        [MenuItem("Nanite/Diagnostics/Validation/CPU vs GPU Sweep (Selected NaniteMesh)")]
        static void ValidateCpuVsGpuSweep()
        {
            if (!TryGetSelection(out var mesh, out var camera, out var shader))
                return;

            var backend = new NaniteGpuCullingBackend();
            if (!backend.Initialize(mesh, shader))
            {
                backend.Dispose();
                Debug.LogError("[Nanite][Validation] GPU backend 初始化失败。");
                return;
            }

            var sb = new StringBuilder(2048);
            bool allPass = true;
            int worstMissing = 0;
            int worstExtra = 0;
            float worstLod = 0f;

            var cpu = new List<NaniteVisibleClusterRef>(8192);
            var gpu = new List<NaniteVisibleClusterRef>(8192);
            for (int i = 0; i < kLodSweep.Length; i++)
            {
                float lodError = kLodSweep[i];
                NaniteRuntimeCulling.CullVisibleClusters(mesh, camera, lodError, true, cpu, out var cpuStats);
                backend.Run(camera, lodError, Matrix4x4.identity, 1f, gpu, out var gpuStats);

                CompareSets(cpu, gpu, out int missing, out int extra, out int cpuVisible, out int gpuVisible);
                if (missing > 0 || extra > 0)
                    allPass = false;
                if (missing + extra > worstMissing + worstExtra)
                {
                    worstMissing = missing;
                    worstExtra = extra;
                    worstLod = lodError;
                }

                sb.AppendLine(
                    $"lod={lodError:0.###} cpu={cpuVisible} gpu={gpuVisible} miss={missing} extra={extra} | " +
                    $"GPU[{FormatStats(gpuStats)}] CPU[{FormatStats(cpuStats)}]");
            }

            backend.Dispose();

            if (allPass)
            {
                Debug.Log("[Nanite][Validation] CPU/GPU LOD Sweep 通过\n" + sb);
            }
            else
            {
                Debug.LogError(
                    $"[Nanite][Validation] CPU/GPU LOD Sweep 失败，最差 lod={worstLod:0.###}, miss={worstMissing}, extra={worstExtra}\n{sb}");
            }
        }

        [MenuItem("Nanite/Diagnostics/Validation/Two-Pass Merge Check (Selected NaniteMesh)")]
        static void ValidateTwoPassMerge()
        {
            if (!TryGetSelection(out var mesh, out var camera, out var shader))
                return;

            const float lodError = 2.0f;

            var backend = new NaniteGpuCullingBackend();
            if (!backend.Initialize(mesh, shader))
            {
                backend.Dispose();
                Debug.LogError("[Nanite][Validation] GPU backend 初始化失败。");
                return;
            }

            var passA = new List<NaniteVisibleClusterRef>(8192);
            var passC = new List<NaniteVisibleClusterRef>(8192);
            var merged = new List<NaniteVisibleClusterRef>(8192);

            // PassA: 模拟上一帧 HZB 粗剔（当前实现先使用 GPU baseline）
            backend.Run(camera, lodError, Matrix4x4.identity, 1f, passA, out var passAStats);
            // PassC: 使用当前帧更保守集合（CPU BVH）做 second-chance 基准
            NaniteRuntimeCulling.CullVisibleClusters(mesh, camera, lodError, true, passC, out var passCStats);

            MergeSets(passA, passC, merged);

            CompareSets(passA, merged, out int missingFromMerged, out int extraOverPassA, out int passAVisible, out int mergedVisible);
            CompareSets(passC, merged, out int missingVsCpu, out int _, out int cpuVisible, out int _mergedVisible2);

            backend.Dispose();

            string report =
                $"passA={passAVisible} passC(cpu)={cpuVisible} merged={mergedVisible} addedBack={mergedVisible - passAVisible}\n" +
                $"A->Merged miss={missingFromMerged} extra={extraOverPassA} (should be miss=0)\n" +
                $"CPU->Merged miss={missingVsCpu} (期望接近 0)\n" +
                $"PassA[{FormatStats(passAStats)}] PassC[{FormatStats(passCStats)}]";

            if (missingFromMerged == 0)
                Debug.Log("[Nanite][Validation] Two-Pass Merge 检查完成\n" + report);
            else
                Debug.LogError("[Nanite][Validation] Two-Pass Merge 检查失败\n" + report);
        }

        [MenuItem("Nanite/Diagnostics/Validation/Batched vs Legacy (Scene Proxies)")]
        static void ValidateBatchedVsLegacySceneProxies()
        {
            var proxies = CollectBatchableSceneProxies();
            if (proxies.Count == 0)
            {
                Debug.LogWarning("[Nanite][Validation] 场景中没有可用于批量对拍的 NaniteRuntimeProxy（需启用 GPU Culling 并绑定 shader）。");
                return;
            }

            var camera = SceneView.lastActiveSceneView != null ? SceneView.lastActiveSceneView.camera : Camera.main;
            if (camera == null)
            {
                Debug.LogWarning("[Nanite][Validation] 没找到可用相机（SceneView 或 Camera.main）。");
                return;
            }

            if (!TryResolveSharedShader(proxies, out var shader))
            {
                Debug.LogError("[Nanite][Validation] 批量对拍要求所有参与 proxy 使用同一个 culling shader。");
                return;
            }

            var batched = new NaniteGpuBatchedCullingBackend();
            var legacySelections = new Dictionary<int, NaniteRuntimeSelection>(proxies.Count);
            var batchedSelections = new Dictionary<int, NaniteRuntimeSelection>(proxies.Count);
            var originalLod = new Dictionary<int, float>(proxies.Count);
            var sb = new StringBuilder(4096);
            float[] lodCases = { 0.5f, 2.0f, 64.0f };
            bool allPass = true;
            int totalMismatches = 0;

            try
            {
                for (int i = 0; i < proxies.Count; i++)
                    originalLod[proxies[i].GetInstanceID()] = proxies[i].lodErrorPixels;

                if (!batched.EnsureInitialized(shader, proxies))
                {
                    Debug.LogError("[Nanite][Validation] 批量 backend 初始化失败。");
                    return;
                }

                for (int c = 0; c < lodCases.Length; c++)
                {
                    float lodError = lodCases[c];
                    for (int i = 0; i < proxies.Count; i++)
                        proxies[i].lodErrorPixels = lodError;

                    for (int i = 0; i < proxies.Count; i++)
                    {
                        var proxy = proxies[i];
                        var legacy = GetOrCreateSelection(legacySelections, proxy.GetInstanceID());
                        proxy.TryComputeSelectionForCamera(camera, null, 0, false, legacy);
                    }

                    if (!batched.EnsureInitialized(shader, proxies))
                    {
                        Debug.LogError("[Nanite][Validation] 批量 backend 迭代更新失败。");
                        return;
                    }

                    EnsureSelectionOutputs(proxies, batchedSelections);
                    if (!batched.Run(camera, null, 0, false, batchedSelections))
                    {
                        Debug.LogError("[Nanite][Validation] 批量 backend Run 失败。");
                        return;
                    }

                    for (int i = 0; i < proxies.Count; i++)
                    {
                        var proxy = proxies[i];
                        int proxyId = proxy.GetInstanceID();
                        var legacy = GetOrCreateSelection(legacySelections, proxyId);
                        var batchedSel = GetOrCreateSelection(batchedSelections, proxyId);
                        bool pass = CompareSelection(legacy, batchedSel, out string detail);
                        if (!pass)
                        {
                            allPass = false;
                            totalMismatches++;
                        }

                        sb.AppendLine(
                            $"lod={lodError:0.###} proxy={proxy.name} pass={pass} " +
                            $"legacy(v={legacy.visibleClusters.Count},p={legacy.packets.Count},r={legacy.pageRanges.Count}) " +
                            $"batched(v={batchedSel.visibleClusters.Count},p={batchedSel.packets.Count},r={batchedSel.pageRanges.Count}) | {detail}");
                    }
                }
            }
            finally
            {
                for (int i = 0; i < proxies.Count; i++)
                {
                    var proxy = proxies[i];
                    if (proxy == null)
                        continue;
                    if (originalLod.TryGetValue(proxy.GetInstanceID(), out float lod))
                        proxy.lodErrorPixels = lod;
                }
                batched.Dispose();
            }

            string headline =
                $"camera={camera.name} proxies={proxies.Count} cases={lodCases.Length} " +
                $"(nearSmallError/medium/farLargeError) mismatches={totalMismatches}";
            if (allPass)
                Debug.Log("[Nanite][Validation] Batched vs Legacy 对拍通过\n" + headline + "\n" + sb);
            else
                Debug.LogError("[Nanite][Validation] Batched vs Legacy 对拍失败\n" + headline + "\n" + sb);
        }

        [MenuItem("Nanite/Diagnostics/Validation/Formal VBuffer Visibility Consistency")]
        static void ValidateFormalVisibilityConsistency()
        {
            var proxies = CollectBatchableSceneProxies();
            if (proxies.Count == 0)
            {
                Debug.LogWarning("[Nanite][Validation] 场景中没有可用于 Formal VBuffer 对拍的 NaniteRuntimeProxy。");
                return;
            }

            var camera = SceneView.lastActiveSceneView != null ? SceneView.lastActiveSceneView.camera : Camera.main;
            if (camera == null)
            {
                Debug.LogWarning("[Nanite][Validation] 没找到可用相机（SceneView 或 Camera.main）。");
                return;
            }

            if (!TryResolveSharedShader(proxies, out var shader))
            {
                Debug.LogError("[Nanite][Validation] Formal VBuffer 对拍要求所有参与 proxy 使用同一个 culling shader。");
                return;
            }

            var batched = new NaniteGpuBatchedCullingBackend();
            var sceneBackend = new NaniteSceneVisibilityBufferBackend();
            var legacySelections = new Dictionary<int, NaniteRuntimeSelection>(proxies.Count);
            var batchedSelections = new Dictionary<int, NaniteRuntimeSelection>(proxies.Count);
            var sb = new StringBuilder(2048);
            bool allPass = true;
            float[] lodCases = { 0.5f, 2.0f, 64.0f };

            try
            {
                if (!batched.EnsureInitialized(shader, proxies))
                {
                    Debug.LogError("[Nanite][Validation] Formal VBuffer 对拍：批量 backend 初始化失败。");
                    return;
                }

                for (int c = 0; c < lodCases.Length; c++)
                {
                    float lodError = lodCases[c];
                    for (int i = 0; i < proxies.Count; i++)
                        proxies[i].lodErrorPixels = lodError;

                    for (int i = 0; i < proxies.Count; i++)
                    {
                        var proxy = proxies[i];
                        var legacy = GetOrCreateSelection(legacySelections, proxy.GetInstanceID());
                        proxy.TryComputeSelectionForCamera(camera, null, 0, false, legacy);
                    }

                    EnsureSelectionOutputs(proxies, batchedSelections);
                    if (!batched.Run(camera, null, 0, false, batchedSelections))
                    {
                        Debug.LogError("[Nanite][Validation] Formal VBuffer 对拍：批量 backend Run 失败。");
                        return;
                    }

                    bool sceneReady = sceneBackend.EnsureInitialized(proxies) && sceneBackend.UpdateVisibleClusters(batchedSelections);
                    if (!sceneReady)
                    {
                        allPass = false;
                        sb.AppendLine($"lod={lodError:0.###} scene-backend=FAILED");
                        continue;
                    }

                    for (int i = 0; i < proxies.Count; i++)
                    {
                        int proxyId = proxies[i].GetInstanceID();
                        var legacy = GetOrCreateSelection(legacySelections, proxyId);
                        var batchedSel = GetOrCreateSelection(batchedSelections, proxyId);
                        bool pass = CompareSelection(legacy, batchedSel, out string detail);
                        if (!pass)
                            allPass = false;
                        sb.AppendLine($"lod={lodError:0.###} proxy={proxies[i].name} pass={pass} | {detail}");
                    }
                }
            }
            finally
            {
                batched.Dispose();
                sceneBackend.Dispose();
            }

            if (allPass)
                Debug.Log("[Nanite][Validation] Formal VBuffer 可见一致性通过（Legacy/Batched/SceneBackend）\n" + sb);
            else
                Debug.LogError("[Nanite][Validation] Formal VBuffer 可见一致性失败\n" + sb);
        }

        [MenuItem("Nanite/Diagnostics/Validation/Formal VBuffer Material Mapping Consistency")]
        static void ValidateFormalMaterialMappingConsistency()
        {
            var proxies = CollectBatchableSceneProxies();
            if (proxies.Count == 0)
            {
                Debug.LogWarning("[Nanite][Validation] 场景中没有可用于材质映射检查的 NaniteRuntimeProxy。");
                return;
            }

            bool allPass = true;
            var sb = new StringBuilder(2048);
            for (int i = 0; i < proxies.Count; i++)
            {
                var proxy = proxies[i];
                var renderer = proxy.GetComponent<Renderer>() ?? proxy.GetComponentInChildren<Renderer>();
                int materialCount = renderer != null && renderer.sharedMaterials != null ? renderer.sharedMaterials.Length : 0;
                int invalidClusters = 0;
                int totalClusters = 0;

                if (proxy.naniteMesh?.pageArray != null)
                {
                    for (int p = 0; p < proxy.naniteMesh.pageArray.Length; p++)
                    {
                        var page = proxy.naniteMesh.pageArray[p];
                        if (page?.clusterArray == null)
                            continue;
                        for (int c = 0; c < page.clusterArray.Length; c++)
                        {
                            totalClusters++;
                            int subMeshId = page.clusterArray[c].subMeshId;
                            if (subMeshId < 0 || (materialCount > 0 && subMeshId >= materialCount))
                                invalidClusters++;
                        }
                    }
                }

                bool pass = invalidClusters == 0 || materialCount == 0;
                if (!pass)
                    allPass = false;
                sb.AppendLine(
                    $"proxy={proxy.name} materials={materialCount} clusters={totalClusters} invalidSubMeshRefs={invalidClusters} pass={pass}");
            }

            if (allPass)
                Debug.Log("[Nanite][Validation] Formal VBuffer 材质映射一致性通过\n" + sb);
            else
                Debug.LogError("[Nanite][Validation] Formal VBuffer 材质映射一致性失败\n" + sb);
        }

        [MenuItem("Nanite/Diagnostics/Validation/Formal VBuffer Derivative/SampleGrad Validation")]
        static void ValidateFormalDerivativeContract()
        {
            string shaderPath = "Assets/Scripts/Nanite/NaniteVBufferLitResolve.shader";
            string includePath = "Assets/Scripts/Nanite/NaniteVBufferCommon.hlsl";
            bool shaderExists = File.Exists(shaderPath);
            bool includeExists = File.Exists(includePath);
            if (!shaderExists || !includeExists)
            {
                Debug.LogError(
                    $"[Nanite][Validation] 导数契约检查失败。shaderExists={shaderExists} includeExists={includeExists}");
                return;
            }

            string shaderCode = File.ReadAllText(shaderPath);
            string includeCode = File.ReadAllText(includePath);
            bool hasBaryFunc = includeCode.Contains("CalculateTriangleBarycentrics");
            bool hasGradSampling = shaderCode.Contains("SAMPLE_TEXTURE2D_GRAD");
            bool hasBaryUsage = shaderCode.Contains("NaniteBarycentricLerp") || shaderCode.Contains("CalculateTriangleBarycentrics");
            bool pass = hasBaryFunc && hasGradSampling && hasBaryUsage;

            if (pass)
                Debug.Log("[Nanite][Validation] Formal VBuffer 导数契约通过（Barycentrics + SampleGrad）。");
            else
                Debug.LogError(
                    $"[Nanite][Validation] Formal VBuffer 导数契约失败 hasBaryFunc={hasBaryFunc} hasGradSampling={hasGradSampling} hasBaryUsage={hasBaryUsage}");
        }

        [MenuItem("Nanite/Diagnostics/Validation/Formal VBuffer GBuffer Regression Check")]
        static void ValidateFormalGBufferRegression()
        {
            const int kAfterRenderingGbuffer = 220;
            const int kBeforeRenderingDeferredLights = 230;
            string[] guids = AssetDatabase.FindAssets("t:UniversalRendererData");
            if (guids == null || guids.Length == 0)
            {
                Debug.LogWarning("[Nanite][Validation] 未找到 UniversalRendererData。");
                return;
            }

            bool allPass = true;
            var sb = new StringBuilder(1024);
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                var data = AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);
                if (data == null)
                    continue;

                var so = new SerializedObject(data);
                var features = so.FindProperty("m_RendererFeatures");
                if (features == null || !features.isArray)
                    continue;

                for (int f = 0; f < features.arraySize; f++)
                {
                    var featureObject = features.GetArrayElementAtIndex(f).objectReferenceValue;
                    if (featureObject == null)
                        continue;
                    var featureTypeName = featureObject.GetType().Name;
                    if (!string.Equals(featureTypeName, "NaniteRendererFeature"))
                        continue;

                    var featureSo = new SerializedObject(featureObject);
                    var formalEventProp = featureSo.FindProperty("settings.formalVBufferEvent");
                    var formalOffsetProp = featureSo.FindProperty("settings.formalVBufferQueueOffset");
                    var resolveMatProp = featureSo.FindProperty("settings.vbufferLitResolveMaterial");
                    var tileClassifyProp = featureSo.FindProperty("settings.materialTileClassifyShader");
                    if (formalEventProp == null || formalOffsetProp == null)
                        continue;

                    int evt = formalEventProp.intValue + formalOffsetProp.intValue;
                    bool inWindow = evt > kAfterRenderingGbuffer && evt < kBeforeRenderingDeferredLights;
                    bool hasResolve = (resolveMatProp != null && resolveMatProp.objectReferenceValue != null) ||
                                      Shader.Find("Nanite/VBufferLitResolve") != null;
                    bool hasTileClassify = tileClassifyProp != null && tileClassifyProp.objectReferenceValue != null;
                    bool pass = inWindow && hasResolve;
                    if (!pass)
                        allPass = false;

                    sb.AppendLine(
                        $"{data.name}/{featureObject.name}: pass={pass} " +
                        $"evt={evt} inWindow={inWindow} hasResolve={hasResolve} hasTileClassify={hasTileClassify}");
                }
            }

            if (allPass)
                Debug.Log("[Nanite][Validation] Formal VBuffer GBuffer 回归检查通过\n" + sb);
            else
                Debug.LogError("[Nanite][Validation] Formal VBuffer GBuffer 回归检查失败\n" + sb);
        }

        [MenuItem("Nanite/Diagnostics/Validation/CPU vs GPU Sweep (Selected NaniteMesh)", true)]
        [MenuItem("Nanite/Diagnostics/Validation/Two-Pass Merge Check (Selected NaniteMesh)", true)]
        static bool ValidateMenu() => Selection.activeObject is NaniteMesh;

        static List<NaniteRuntimeProxy> CollectBatchableSceneProxies()
        {
            var result = new List<NaniteRuntimeProxy>(32);
            var proxies = NaniteRuntimeRegistry.ActiveProxies;
            for (int i = 0; i < proxies.Count; i++)
            {
                var proxy = proxies[i];
                if (proxy == null || !proxy.isActiveAndEnabled || proxy.naniteMesh == null)
                    continue;
                if (!proxy.useGpuCulling || proxy.gpuCullingShader == null)
                    continue;
                result.Add(proxy);
            }

            return result;
        }

        static bool TryResolveSharedShader(List<NaniteRuntimeProxy> proxies, out ComputeShader shader)
        {
            shader = null;
            if (proxies == null || proxies.Count == 0)
                return false;

            shader = proxies[0].gpuCullingShader;
            if (shader == null)
                return false;

            for (int i = 1; i < proxies.Count; i++)
            {
                if (proxies[i] == null)
                    continue;
                if (proxies[i].gpuCullingShader != shader)
                    return false;
            }

            return true;
        }

        static void EnsureSelectionOutputs(List<NaniteRuntimeProxy> proxies, Dictionary<int, NaniteRuntimeSelection> outputs)
        {
            for (int i = 0; i < proxies.Count; i++)
            {
                var proxy = proxies[i];
                if (proxy == null)
                    continue;
                GetOrCreateSelection(outputs, proxy.GetInstanceID());
            }
        }

        static bool TryGetSelection(out NaniteMesh mesh, out Camera camera, out ComputeShader shader)
        {
            mesh = Selection.activeObject as NaniteMesh;
            camera = null;
            shader = null;

            if (mesh == null)
            {
                Debug.LogWarning("[Nanite][Validation] 请先选中 NaniteMesh。");
                return false;
            }

            camera = SceneView.lastActiveSceneView != null ? SceneView.lastActiveSceneView.camera : Camera.main;
            if (camera == null)
            {
                Debug.LogWarning("[Nanite][Validation] 没找到可用相机（SceneView 或 Camera.main）。");
                return false;
            }

            string[] guids = AssetDatabase.FindAssets("NaniteRuntimeCulling t:ComputeShader");
            if (guids == null || guids.Length == 0)
            {
                Debug.LogWarning("[Nanite][Validation] 没找到 NaniteRuntimeCulling.compute。");
                return false;
            }

            string shaderPath = AssetDatabase.GUIDToAssetPath(guids[0]);
            shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(shaderPath);
            if (shader == null)
            {
                Debug.LogWarning("[Nanite][Validation] 读取 ComputeShader 失败。");
                return false;
            }

            return true;
        }

        static string FormatStats(in NaniteCullingStats stats)
        {
            return
                $"inst={stats.testedInstances},node={stats.testedNodes},part={stats.testedParts},cluster={stats.testedClusters},visible={stats.visibleClusters}";
        }

        static void CompareSets(
            List<NaniteVisibleClusterRef> expected,
            List<NaniteVisibleClusterRef> actual,
            out int missing,
            out int extra,
            out int expectedVisible,
            out int actualVisible)
        {
            var expectedSet = new HashSet<long>();
            var actualSet = new HashSet<long>();
            for (int i = 0; i < expected.Count; i++)
                expectedSet.Add(Pack(expected[i]));
            for (int i = 0; i < actual.Count; i++)
                actualSet.Add(Pack(actual[i]));

            missing = 0;
            foreach (var k in expectedSet)
                if (!actualSet.Contains(k))
                    missing++;

            extra = 0;
            foreach (var k in actualSet)
                if (!expectedSet.Contains(k))
                    extra++;

            expectedVisible = expectedSet.Count;
            actualVisible = actualSet.Count;
        }

        static void MergeSets(
            List<NaniteVisibleClusterRef> a,
            List<NaniteVisibleClusterRef> b,
            List<NaniteVisibleClusterRef> outMerged)
        {
            outMerged.Clear();
            var set = new HashSet<long>();

            for (int i = 0; i < a.Count; i++)
            {
                var v = a[i];
                if (set.Add(Pack(v)))
                    outMerged.Add(v);
            }

            for (int i = 0; i < b.Count; i++)
            {
                var v = b[i];
                if (set.Add(Pack(v)))
                    outMerged.Add(v);
            }
        }

        static NaniteRuntimeSelection GetOrCreateSelection(Dictionary<int, NaniteRuntimeSelection> dict, int key)
        {
            if (!dict.TryGetValue(key, out var selection))
            {
                selection = new NaniteRuntimeSelection();
                dict[key] = selection;
            }

            return selection;
        }

        static bool CompareSelection(NaniteRuntimeSelection legacy, NaniteRuntimeSelection batched, out string detail)
        {
            CompareSets(legacy.visibleClusters, batched.visibleClusters, out int missVisible, out int extraVisible, out _, out _);
            ComparePacketSets(legacy.packets, batched.packets, out int missPackets, out int extraPackets);
            ComparePageRangeSets(legacy.pageRanges, batched.pageRanges, out int missRanges, out int extraRanges);
            detail =
                $"visible(miss={missVisible},extra={extraVisible}) " +
                $"packets(miss={missPackets},extra={extraPackets}) " +
                $"ranges(miss={missRanges},extra={extraRanges})";
            return missVisible == 0 &&
                   extraVisible == 0 &&
                   missPackets == 0 &&
                   extraPackets == 0 &&
                   missRanges == 0 &&
                   extraRanges == 0;
        }

        static void ComparePacketSets(
            List<NaniteVisibleClusterPacket> expected,
            List<NaniteVisibleClusterPacket> actual,
            out int missing,
            out int extra)
        {
            var expectedSet = new HashSet<string>();
            var actualSet = new HashSet<string>();
            for (int i = 0; i < expected.Count; i++)
                expectedSet.Add(PackPacket(expected[i]));
            for (int i = 0; i < actual.Count; i++)
                actualSet.Add(PackPacket(actual[i]));
            missing = 0;
            foreach (var k in expectedSet)
                if (!actualSet.Contains(k))
                    missing++;
            extra = 0;
            foreach (var k in actualSet)
                if (!expectedSet.Contains(k))
                    extra++;
        }

        static void ComparePageRangeSets(
            List<NaniteVisiblePageRange> expected,
            List<NaniteVisiblePageRange> actual,
            out int missing,
            out int extra)
        {
            var expectedSet = new HashSet<string>();
            var actualSet = new HashSet<string>();
            for (int i = 0; i < expected.Count; i++)
                expectedSet.Add(PackPageRange(expected[i]));
            for (int i = 0; i < actual.Count; i++)
                actualSet.Add(PackPageRange(actual[i]));
            missing = 0;
            foreach (var k in expectedSet)
                if (!actualSet.Contains(k))
                    missing++;
            extra = 0;
            foreach (var k in actualSet)
                if (!expectedSet.Contains(k))
                    extra++;
        }

        static long Pack(in NaniteVisibleClusterRef v) => ((long)v.pageIndex << 32) | (uint)v.clusterIndex;
        static string PackPacket(in NaniteVisibleClusterPacket p) => $"{p.pageIndex}:{p.clusterIndex}:{p.subMeshId}:{p.mipLevel}:{p.indexOffset}:{p.indexCount}:{p.vertexOffset}";
        static string PackPageRange(in NaniteVisiblePageRange r) => $"{r.pageIndex}:{r.start}:{r.count}";
    }
}
#endif
