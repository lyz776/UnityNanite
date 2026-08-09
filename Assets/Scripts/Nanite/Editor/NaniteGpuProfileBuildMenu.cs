#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;

namespace Nanite.Editor
{
    /// <summary>
    /// Produces a reproducible Development Player for the GPU-timing decision gate
    /// documented in NANITE_PIPELINE_ROADMAP.md.  Deliberately does not change
    /// PlayerSettings resolution: launch arguments keep the A/B measurement local
    /// to the capture instead of mutating the project default.
    /// </summary>
    public static class NaniteGpuProfileBuildMenu
    {
        const string kOutputRelativePath = "Builds/NaniteGpuProfile/UnityNaniteGpuProfile.exe";
        static readonly (string Name, string Path)[] kRequiredRuntimeShaders =
        {
            ("Nanite/VBufferPacketRaster", "Assets/Scripts/Nanite/NaniteVBufferPacketRaster.shader"),
            ("Nanite/VBufferLitResolve", "Assets/Scripts/Nanite/NaniteVBufferLitResolve.shader"),
            ("Nanite/VBufferDepthWrite", "Assets/Scripts/Nanite/NaniteVBufferDepthWrite.shader"),
            ("Nanite/VBufferShadowCaster", "Assets/Scripts/Nanite/NaniteVBufferShadowCaster.shader"),
            ("Nanite/VBufferDecode", "Assets/Scripts/Nanite/NaniteVBufferDecode.shader")
        };

        [MenuItem("Nanite/Diagnostics/Performance/Build GPU Profiler Development Player")]
        public static void BuildGpuProfilerDevelopmentPlayer()
        {
            string[] scenes = EditorBuildSettings.scenes
                .Where(scene => scene != null && scene.enabled && !string.IsNullOrEmpty(scene.path))
                .Select(scene => scene.path)
                .ToArray();
            if (scenes.Length == 0)
            {
                Debug.LogError("[Nanite][ProfileBuild] No enabled build scenes. Enable the 12-car benchmark scene before building.");
                return;
            }

            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string outputPath = Path.Combine(projectRoot, kOutputRelativePath);
            string outputDirectory = Path.GetDirectoryName(outputPath);
            if (string.IsNullOrEmpty(outputDirectory))
            {
                Debug.LogError("[Nanite][ProfileBuild] Failed to resolve the build output directory.");
                return;
            }

            EnsureStandaloneD3D12Only();
            Directory.CreateDirectory(outputDirectory);
            BuildPlayerOptions options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = outputPath,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.Development |
                          BuildOptions.ConnectWithProfiler |
                          BuildOptions.AllowDebugging |
                          BuildOptions.StrictMode
            };

            if (!EnsureRuntimeShadersAlwaysIncluded())
                return;

            // These shaders are constructed with Shader.Find at runtime. The build
            // helper keeps them in GraphicsSettings/Always Included Shaders and then
            // fails early if the Editor itself cannot resolve a required source.
            string[] missingShaders = kRequiredRuntimeShaders
                .Where(shader => Shader.Find(shader.Name) == null)
                .Select(shader => shader.Name)
                .ToArray();
            if (missingShaders.Length > 0)
            {
                Debug.LogError(
                    "[Nanite][ProfileBuild] Required runtime shader is missing in the Editor: " +
                    string.Join(", ", missingShaders));
                return;
            }

            bool previousFrameTimingStats = PlayerSettings.enableFrameTimingStats;
            BuildReport report;
            try
            {
                PlayerSettings.enableFrameTimingStats = true;
                report = BuildPipeline.BuildPlayer(options);
            }
            finally
            {
                PlayerSettings.enableFrameTimingStats = previousFrameTimingStats;
            }
            if (report.summary.result != BuildResult.Succeeded)
            {
                Debug.LogError(
                    $"[Nanite][ProfileBuild] Development Player build failed: {report.summary.result}; " +
                    $"errors={report.summary.totalErrors}, warnings={report.summary.totalWarnings}.");
                return;
            }

            Debug.Log(
                "[Nanite][ProfileBuild] Development Player ready. In the Unity Profiler enable GPU Usage, " +
                "then capture fixed-camera A/B runs with:\n" +
                $"  \"{outputPath}\" -screen-width 3840 -screen-height 2160 -screen-fullscreen 0\n" +
                "Record one Nanite run and one ordinary-Mesh run with identical camera, light, shadow and quality settings. " +
                "Compare GPU VBuffer raster, resolve, shadow and cull/HZB markers; do not use Editor Game View timing as the decision metric.");
        }

        static bool EnsureRuntimeShadersAlwaysIncluded()
        {
            UnityEngine.Object[] graphicsSettingsAssets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset");
            UnityEngine.Object graphicsSettings = graphicsSettingsAssets.FirstOrDefault();
            if (graphicsSettings == null)
            {
                Debug.LogError("[Nanite][ProfileBuild] Failed to load ProjectSettings/GraphicsSettings.asset.");
                return false;
            }

            var serializedSettings = new SerializedObject(graphicsSettings);
            SerializedProperty alwaysIncludedShaders = serializedSettings.FindProperty("m_AlwaysIncludedShaders");
            if (alwaysIncludedShaders == null || !alwaysIncludedShaders.isArray)
            {
                Debug.LogError("[Nanite][ProfileBuild] GraphicsSettings.m_AlwaysIncludedShaders was not found.");
                return false;
            }

            bool changed = false;
            foreach ((string shaderName, string shaderPath) in kRequiredRuntimeShaders)
            {
                var shader = AssetDatabase.LoadAssetAtPath<Shader>(shaderPath);
                if (shader == null)
                {
                    Debug.LogError($"[Nanite][ProfileBuild] Required runtime shader asset is missing: {shaderName} at {shaderPath}.");
                    return false;
                }

                bool alreadyIncluded = false;
                for (int i = 0; i < alwaysIncludedShaders.arraySize; ++i)
                {
                    if (alwaysIncludedShaders.GetArrayElementAtIndex(i).objectReferenceValue == shader)
                    {
                        alreadyIncluded = true;
                        break;
                    }
                }

                if (alreadyIncluded)
                    continue;

                int index = alwaysIncludedShaders.arraySize;
                alwaysIncludedShaders.InsertArrayElementAtIndex(index);
                alwaysIncludedShaders.GetArrayElementAtIndex(index).objectReferenceValue = shader;
                changed = true;
            }

            if (changed)
            {
                serializedSettings.ApplyModifiedPropertiesWithoutUndo();
                AssetDatabase.SaveAssets();
                Debug.Log("[Nanite][ProfileBuild] Added Nanite runtime shaders to GraphicsSettings/Always Included Shaders.");
            }

            return true;
        }

        static void EnsureStandaloneD3D12Only()
        {
            var apis = PlayerSettings.GetGraphicsAPIs(BuildTarget.StandaloneWindows64);
            if (apis.Length == 1 && apis[0] == GraphicsDeviceType.Direct3D12)
                return;

            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
            PlayerSettings.SetGraphicsAPIs(
                BuildTarget.StandaloneWindows64,
                new[] { GraphicsDeviceType.Direct3D12 });
            Debug.Log("[Nanite][ProfileBuild] Standalone Windows Graphics API forced to Direct3D12 for Nanite GPU paths.");
        }
    }
}
#endif
