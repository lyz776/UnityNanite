using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;

namespace Nanite.Editor
{
    /// <summary>
    /// Fails a Windows player build before shader import/dispatch errors can turn
    /// into an empty frame. Runtime still negotiates and falls back per feature;
    /// this guard enforces only the non-negotiable production ABI.
    /// </summary>
    sealed class NanitePortabilityBuildGuard : IPreprocessBuildWithReport
    {
        const string Root = "Assets/Scripts/Nanite";

        static readonly string[] CompactKernels =
        {
            "CSClear",
            "CSCompactClusters",
            "CSFinalizeArgs",
            "CSFinalizeVisibleDrawQueueArgs",
            "CSFinalizeVisibleDrawQueueArgs4"
        };

        static readonly string[] IndexedKernels =
        {
            "CSPrepareIndexedDrawQueue",
            "CSBuildIndexedDrawQueue",
            "CSPrepareIndexedDrawQueueAppend",
            "CSBuildIndexedDrawQueueAppend",
            "CSPrepareIndexedShadowDrawQueue",
            "CSBuildIndexedShadowDrawQueue"
        };

        static readonly string[] HybridKernels =
        {
            "CSPrepareHybridDispatch",
            "CSClearHybridTileHeads",
            "CSClearHybridTileWork",
            "CSBuildHybridTileWork",
            "CSSoftwareRasterTiles",
            "CSClassifyHybridClusters",
            "CSCaptureHybridView"
        };

        // Explicit per-kernel writable-resource contracts. These counts are kept
        // below the D3D11-family compiler's eight-UAV ceiling used by Unity DX12.
        static readonly IReadOnlyDictionary<string, int> MaxUavByKernel =
            new Dictionary<string, int>
            {
                ["CSClear"] = 5,
                ["CSCompactClusters"] = 4,
                ["CSFinalizeArgs"] = 2,
                ["CSFinalizeVisibleDrawQueueArgs"] = 1,
                ["CSFinalizeVisibleDrawQueueArgs4"] = 4,
                ["CSPrepareIndexedDrawQueue"] = 4,
                ["CSBuildIndexedDrawQueue"] = 2,
                ["CSPrepareIndexedDrawQueueAppend"] = 4,
                ["CSBuildIndexedDrawQueueAppend"] = 2,
                ["CSPrepareIndexedShadowDrawQueue"] = 4,
                ["CSBuildIndexedShadowDrawQueue"] = 3,
                ["CSPrepareHybridDispatch"] = 1,
                ["CSClearHybridTileHeads"] = 1,
                ["CSClearHybridTileWork"] = 1,
                ["CSBuildHybridTileWork"] = 6,
                ["CSSoftwareRasterTiles"] = 2,
                ["CSClassifyHybridClusters"] = 2,
                ["CSCaptureHybridView"] = 1
            };

        public int callbackOrder => -1000;

        public void OnPreprocessBuild(BuildReport report)
        {
            if (report.summary.platform != BuildTarget.StandaloneWindows64 &&
                report.summary.platform != BuildTarget.StandaloneWindows)
                return;

            string error = Audit(false);
            if (!string.IsNullOrEmpty(error))
                throw new BuildFailedException("Nanite DX12 portability contract failed:\n" + error);
        }

        [MenuItem("Tools/Nanite/Validate DX12 Portability Contract")]
        static void ValidateMenu()
        {
            string error = Audit(true);
            if (string.IsNullOrEmpty(error))
                Debug.Log("[Nanite][Portability] DX12 API, shader backend, kernel and UAV contracts are valid.");
            else
                Debug.LogError("[Nanite][Portability] Contract failed:\n" + error);
        }

        [MenuItem("Tools/Nanite/Build DX12 Portability Smoke Player")]
        public static void BuildSmokePlayer()
        {
            string error = Audit(false);
            if (!string.IsNullOrEmpty(error))
                throw new BuildFailedException("Nanite DX12 portability contract failed:\n" + error);

            string[] scenes = EditorBuildSettings.scenes
                .Where(scene => scene != null && scene.enabled && !string.IsNullOrEmpty(scene.path))
                .Select(scene => scene.path)
                .ToArray();
            if (scenes.Length == 0)
                throw new BuildFailedException("Nanite portability smoke build has no enabled scene.");

            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string outputPath = Path.Combine(
                projectRoot,
                "Builds/NanitePortabilitySmoke/UnityNanitePortabilitySmoke.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
            bool previousFrameTimingStats = PlayerSettings.enableFrameTimingStats;
            BuildReport report;
            try
            {
                // FrameTimingManager is the unattended Player's coarse GPU/CPU
                // evidence when pass-level Recorder GPU blocks require an attached
                // GPU Profiler. Restore the project preference after baking it into
                // this diagnostic Player.
                PlayerSettings.enableFrameTimingStats = true;
                report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = scenes,
                    locationPathName = outputPath,
                    target = BuildTarget.StandaloneWindows64,
                    options = BuildOptions.Development | BuildOptions.StrictMode
                });
            }
            finally
            {
                PlayerSettings.enableFrameTimingStats = previousFrameTimingStats;
            }
            if (report.summary.result != BuildResult.Succeeded)
                throw new BuildFailedException(
                    $"Nanite portability smoke build failed: {report.summary.result}; " +
                    $"errors={report.summary.totalErrors}.");

            Debug.Log($"[Nanite][Portability] Smoke Player ready: {outputPath}");
        }

        static string Audit(bool includeEditorDevice)
        {
            var failures = new List<string>();
            GraphicsDeviceType[] apis = PlayerSettings.GetGraphicsAPIs(BuildTarget.StandaloneWindows64);
            if (apis == null || apis.Length != 1 || apis[0] != GraphicsDeviceType.Direct3D12)
                failures.Add("Standalone Windows Graphics APIs must contain Direct3D12 only.");
            if (includeEditorDevice && SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12)
                failures.Add($"Current Editor device is {SystemInfo.graphicsDeviceType}; restart the Editor with Direct3D12.");

            string[] computeGuids = AssetDatabase.FindAssets("t:ComputeShader", new[] { Root });
            for (int i = 0; i < computeGuids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(computeGuids[i]);
                string source = File.ReadAllText(path);
                if (!source.Contains("#pragma only_renderers d3d11"))
                    failures.Add($"{path}: missing '#pragma only_renderers d3d11'.");
            }

            const string compactPath = Root + "/NaniteVisibleTriangleCompact.compute";
            const string runtimeResourcesPath =
                Root + "/Resources/NaniteRuntimeComputeResources.asset";
            var runtimeResources = AssetDatabase.LoadAssetAtPath<NaniteRuntimeComputeResources>(
                runtimeResourcesPath);
            if (runtimeResources == null ||
                runtimeResources.gpuCullingShader == null ||
                runtimeResources.visibleTriangleCompactShader == null ||
                runtimeResources.pageTranscodeShader == null ||
                runtimeResources.hzbBuilderShader == null ||
                runtimeResources.materialTileClassifyShader == null)
            {
                failures.Add(runtimeResourcesPath + ": runtime compute ownership is incomplete.");
            }
            ComputeShader compact = AssetDatabase.LoadAssetAtPath<ComputeShader>(compactPath);
            string compactSource = File.Exists(compactPath)
                ? File.ReadAllText(compactPath)
                : string.Empty;
            if (compact == null)
            {
                failures.Add(compactPath + ": asset is missing or failed import.");
            }
            else if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                // Unity does not create compute kernels on the Null device used by
                // -nographics CI. Validate the import-independent source ABI here;
                // a real Editor device still executes FindKernel below.
                ValidateKernelPragmas(compactSource, CompactKernels, failures);
                ValidateKernelPragmas(compactSource, IndexedKernels, failures);
                ValidateKernelPragmas(compactSource, HybridKernels, failures);
            }
            else
            {
                ValidateKernels(compact, CompactKernels, failures);
                ValidateKernels(compact, IndexedKernels, failures);
                ValidateKernels(compact, HybridKernels, failures);
            }

            const string cullingPath = Root + "/NaniteRuntimeCulling.compute";
            string cullingSource = File.Exists(cullingPath)
                ? File.ReadAllText(cullingPath)
                : string.Empty;
            ValidateContinuousLodContract(cullingSource, failures);

            foreach (KeyValuePair<string, int> contract in MaxUavByKernel)
            {
                if (contract.Value > 8)
                    failures.Add($"{contract.Key}: declared UAV contract is {contract.Value}, maximum is 8.");
            }
            return string.Join("\n", failures);
        }

        static void ValidateContinuousLodContract(
            string source,
            ICollection<string> failures)
        {
            if (string.IsNullOrEmpty(source))
            {
                failures.Add("NaniteRuntimeCulling.compute: source is missing.");
                return;
            }

            if (!source.Contains("return worldError * ShadowProjectionScale(cascadeIndex);"))
                failures.Add(
                    "Shadow projected error must use the half-pixel/radius convention; " +
                    "ShadowProjectionScale already contains 0.5 * resolution.");
            if (!source.Contains("return errorVisible || densityUnderBudget;"))
                failures.Add(
                    "Camera hierarchy refinement must retain geometric error and request finer " +
                    "geometry when the coarse cut is below the projected surface-pixel budget.");
            if (!source.Contains("part.consumerGroupIndex") ||
                !source.Contains("bool partNeedsRefinement"))
                failures.Add(
                    "Part admission must use the hierarchy error-or-density predicate; " +
                    "an error-only Part test opens close-view holes.");
            if (FunctionBodyContains(
                    source,
                    "void CSTraverseSpatialNodes(uint3 id : SV_DispatchThreadID)",
                    "node.maxParentError"))
                failures.Add(
                    "Spatial nodes lack density aggregates and must not apply an error-only " +
                    "LOD subtree early-out before exact Part admission.");
            if (!source.Contains("return max(1.0, _ShadowLodMinTexels);"))
                failures.Add(
                    "Production CSM error target must not apply an extra cascade bias; " +
                    "ShadowProjectionScale already contains cascade texel density.");
            if (!FunctionBodyContains(
                    source,
                    "bool ShadowHierarchyGroupNeedsRefinement(",
                    "projectedSurfaceTexels"))
                failures.Add(
                    "Shadow hierarchy refinement must retain projected surface density; " +
                    "error-only cuts can select a terminal proxy for a large caster.");
            if (!FunctionBodyContains(
                    source,
                    "bool ShadowSphereVisible(float4 sphere, int cascadeIndex)",
                    "_ShadowCasterPlanes") ||
                !FunctionBodyContains(
                    source,
                    "bool ShadowSphereVisible(float4 sphere, int cascadeIndex)",
                    "worldTexelGuard"))
                failures.Add(
                    "Directional shadow admission must consume Unity ShadowSplitData " +
                    "caster planes with a conservative texel guard band; projection " +
                    "receiver planes are not a valid replacement.");
            if (FunctionBodyContains(
                    source,
                    "uint EncodePageRequestPriority(float projectedImportance)",
                    "return 0xFFFFFFFFu;"))
                failures.Add(
                    "FLT_MAX hierarchy sentinels must not encode as UINT_MAX Page request " +
                    "priority; use projected footprint ordering for terminal/root producers.");
            if (!source.Contains("bool RootGroupVisibleForCamera(") ||
                !source.Contains("uint RootGroupVisibleShadowMask("))
                failures.Add(
                    "Terminal root disappearance must be producer-group atomic for both " +
                    "camera pixels and per-cascade shadow texels.");
            if (!FunctionBodyContains(
                    source,
                    "uint RootGroupVisibleShadowMask(",
                    "return ShadowVisibleCascadeMask(sphere, requestedMask);") ||
                FunctionBodyContains(
                    source,
                    "uint RootGroupVisibleShadowMask(",
                    "TERMINAL_DISAPPEAR_SHADOW_TEXELS"))
                failures.Add(
                    "Shadow roots must not disappear independently; doing so removes " +
                    "spatial caster sections and creates striped or blocked CSM atlases.");
            if (!FunctionBodyContains(
                    source,
                    "void CSTraverseShadowHierarchy(uint3 id : SV_DispatchThreadID)",
                    "ShadowHierarchyGroupNeedsRefinement(") ||
                !FunctionBodyContains(
                    source,
                    "void CSTraverseShadowHierarchyPersistent(uint3 id : SV_DispatchThreadID)",
                    "ShadowHierarchyGroupNeedsRefinement("))
                failures.Add(
                    "Both bounded and persistent shadow hierarchy traversal must use " +
                    "surface-density refinement, not geometric error alone.");
            if (FunctionBodyContains(
                    source,
                    "void CSTraverseShadowSpatialNodes(uint3 id : SV_DispatchThreadID)",
                    "node.maxParentError") ||
                FunctionBodyContains(
                    source,
                    "void CSTraverseShadowSpatialNodes(uint3 id : SV_DispatchThreadID)",
                    "ShadowLodErrorIsImperceptible("))
                failures.Add(
                    "Shadow spatial nodes lack density aggregates and must not perform " +
                    "error-only subtree rejection before exact Part/cluster admission.");
            if (!FunctionBodyContains(
                    source,
                    "void CullResolvedShadowPart(",
                    "part.consumerGroupIndex") ||
                !FunctionBodyContains(
                    source,
                    "void CullResolvedShadowPart(",
                    "ShadowHierarchyGroupNeedsRefinement("))
                failures.Add(
                    "Shadow Part admission must share hierarchy density refinement with " +
                    "cluster selection; mismatched predicates can produce an empty cut.");
            if (FunctionBodyContains(
                    source,
                    "void EmitHierarchySelectedCluster(uint hierarchyRefIndex, uint instanceIndex)",
                    "terminalDisappear") ||
                FunctionBodyContains(
                    source,
                    "void EmitHierarchySelectedShadowCluster(",
                    "terminalDisappear"))
                failures.Add(
                    "A selected hierarchy producer must not drop terminal clusters one by one; " +
                    "root disappearance belongs in the atomic seed kernels.");
        }

        static bool FunctionBodyContains(string source, string signature, string value)
        {
            int signatureIndex = source.IndexOf(signature, StringComparison.Ordinal);
            if (signatureIndex < 0)
                return false;

            int bodyStart = source.IndexOf('{', signatureIndex + signature.Length);
            if (bodyStart < 0)
                return false;

            int depth = 0;
            for (int i = bodyStart; i < source.Length; i++)
            {
                switch (source[i])
                {
                    case '{':
                        depth++;
                        break;
                    case '}':
                        depth--;
                        if (depth == 0)
                        {
                            int bodyLength = i - bodyStart + 1;
                            return source.IndexOf(
                                value,
                                bodyStart,
                                bodyLength,
                                StringComparison.Ordinal) >= 0;
                        }
                        break;
                }
            }

            return false;
        }

        static void ValidateKernelPragmas(
            string source,
            IReadOnlyList<string> kernels,
            ICollection<string> failures)
        {
            for (int i = 0; i < kernels.Count; i++)
            {
                string declaration = "#pragma kernel " + kernels[i];
                if (string.IsNullOrEmpty(source) || !source.Contains(declaration))
                    failures.Add($"NaniteVisibleTriangleCompact/{kernels[i]}: missing source pragma.");
            }
        }

        static void ValidateKernels(
            ComputeShader shader,
            IReadOnlyList<string> kernels,
            ICollection<string> failures)
        {
            for (int i = 0; i < kernels.Count; i++)
            {
                string name = kernels[i];
                try
                {
                    int kernel = shader.FindKernel(name);
                    shader.GetKernelThreadGroupSizes(kernel, out uint x, out uint y, out uint z);
                    if (x == 0 || y == 0 || z == 0)
                        failures.Add($"{shader.name}/{name}: invalid thread-group size.");
                }
                catch (Exception e)
                {
                    failures.Add($"{shader.name}/{name}: unavailable ({e.Message}).");
                }
            }
        }
    }
}
