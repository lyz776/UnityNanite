using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Rendering.Universal;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace UnityNanite.TOD.Editor
{
    public static class TODProjectInstaller
    {
        public const string DefaultProfilePath = "Assets/TOD/Profiles/Default TOD Profile.asset";
        public const string DefaultSkyMaterialPath = "Assets/TOD/Generated/TOD Dynamic Sky.mat";
        public const string DefaultPrefabPath = "Assets/TOD/Prefabs/TOD Rig.prefab";

        [MenuItem("Tools/Unity Nanite/TOD/Install Project Defaults")]
        public static void InstallProjectDefaults()
        {
            EnsureFolders();
            TODProfile profile = EnsureDefaultProfile();
            Material skyMaterial = EnsureSkyMaterial();
            EnsurePrefab(profile, skyMaterial);
            int installedFeatures = EnsureRendererFeatures();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log(
                $"[TOD] Project defaults ready. Profile={DefaultProfilePath}, " +
                $"Sky={DefaultSkyMaterialPath}, Prefab={DefaultPrefabPath}, " +
                $"new renderer features={installedFeatures}.");
        }

        [MenuItem("GameObject/Unity Nanite/Create 24 Hour TOD Rig", false, 10)]
        private static void CreateRig(MenuCommand command)
        {
            InstallProjectDefaults();
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(DefaultPrefabPath);
            if (prefab == null)
            {
                Debug.LogError("[TOD] Default TOD prefab could not be loaded.");
                return;
            }

            GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            Undo.RegisterCreatedObjectUndo(instance, "Create 24 Hour TOD Rig");
            GameObjectUtility.SetParentAndAlign(instance, command.context as GameObject);
            Selection.activeGameObject = instance;

            Material sky = AssetDatabase.LoadAssetAtPath<Material>(DefaultSkyMaterialPath);
            if (sky != null)
                RenderSettings.skybox = sky;

            EditorSceneManagerBridge.MarkActiveSceneDirty();
            Debug.Log("[TOD] Created TOD Rig and assigned the dynamic TOD skybox to the active scene.");
        }

        [MenuItem("Tools/Unity Nanite/TOD/Open TOD Editor")]
        private static void OpenEditor()
        {
            TODWindow.Open();
        }

        private static void EnsureFolders()
        {
            EnsureFolder("Assets", "TOD");
            EnsureFolder("Assets/TOD", "Profiles");
            EnsureFolder("Assets/TOD", "Generated");
            EnsureFolder("Assets/TOD", "Prefabs");
        }

        private static void EnsureFolder(string parent, string name)
        {
            string path = $"{parent}/{name}";
            if (!AssetDatabase.IsValidFolder(path))
                AssetDatabase.CreateFolder(parent, name);
        }

        private static TODProfile EnsureDefaultProfile()
        {
            TODProfile profile = AssetDatabase.LoadAssetAtPath<TODProfile>(DefaultProfilePath);
            if (profile != null)
            {
                // 让旧资源在新增序列化字段后也能稳定写回当前结构。
                EditorUtility.SetDirty(profile);
                return profile;
            }

            profile = ScriptableObject.CreateInstance<TODProfile>();
            profile.name = "Default TOD Profile";
            AssetDatabase.CreateAsset(profile, DefaultProfilePath);
            return profile;
        }

        private static Material EnsureSkyMaterial()
        {
            Material material = AssetDatabase.LoadAssetAtPath<Material>(DefaultSkyMaterialPath);
            if (material != null)
                return material;

            Shader shader = Shader.Find("Skybox/Unity Nanite TOD Dynamic Sky");
            if (shader == null)
            {
                Debug.LogWarning("[TOD] Dynamic sky shader is not imported yet; sky material creation is deferred.");
                return null;
            }

            material = new Material(shader) { name = "TOD Dynamic Sky" };
            AssetDatabase.CreateAsset(material, DefaultSkyMaterialPath);
            return material;
        }

        private static void EnsurePrefab(TODProfile profile, Material skyMaterial)
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(DefaultPrefabPath) != null)
            {
                GameObject contents = PrefabUtility.LoadPrefabContents(DefaultPrefabPath);
                try
                {
                    TODController existingController = contents.GetComponent<TODController>();
                    if (existingController != null)
                    {
                        existingController.Profile = profile;
                        existingController.AssignSkybox = true;
                        existingController.SkyboxMaterial = skyMaterial;
                        PrefabUtility.SaveAsPrefabAsset(contents, DefaultPrefabPath);
                    }
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(contents);
                }
                return;
            }

            GameObject root = new GameObject("TOD Rig");
            try
            {
                TODController controller = root.AddComponent<TODController>();
                controller.Profile = profile;
                controller.CurrentTime = 12f;
                controller.AdvanceTime = true;
                controller.HoursPerSecond = 0.1f;
                controller.AssignSkybox = true;
                controller.SkyboxMaterial = skyMaterial;

                GameObject lightObject = new GameObject("TOD Main Directional Light");
                lightObject.transform.SetParent(root.transform, false);
                Light light = lightObject.AddComponent<Light>();
                light.type = LightType.Directional;
                light.shadows = LightShadows.Soft;
                controller.MainLight = light;

                GameObject sunAnchor = new GameObject("Sun Visual Anchor");
                sunAnchor.transform.SetParent(root.transform, false);
                controller.SunVisual = sunAnchor.transform;

                GameObject moonAnchor = new GameObject("Moon Visual Anchor");
                moonAnchor.transform.SetParent(root.transform, false);
                controller.MoonVisual = moonAnchor.transform;

                PrefabUtility.SaveAsPrefabAsset(root, DefaultPrefabPath);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        private static int EnsureRendererFeatures()
        {
            int count = 0;
            string[] guids = AssetDatabase.FindAssets("t:UniversalRendererData", new[] { "Assets" });
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                UniversalRendererData rendererData = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(path);
                if (rendererData == null)
                    continue;

                if (rendererData.rendererFeatures.Any(feature => feature is TODFogRendererFeature))
                    continue;

                TODFogRendererFeature feature = ScriptableObject.CreateInstance<TODFogRendererFeature>();
                feature.name = nameof(TODFogRendererFeature);
                AssetDatabase.AddObjectToAsset(feature, rendererData);
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out _, out long localId);

                SerializedObject serializedData = new SerializedObject(rendererData);
                SerializedProperty features = serializedData.FindProperty("m_RendererFeatures");
                SerializedProperty featureMap = serializedData.FindProperty("m_RendererFeatureMap");
                features.arraySize++;
                features.GetArrayElementAtIndex(features.arraySize - 1).objectReferenceValue = feature;
                featureMap.arraySize++;
                featureMap.GetArrayElementAtIndex(featureMap.arraySize - 1).longValue = localId;
                serializedData.ApplyModifiedPropertiesWithoutUndo();

                EditorUtility.SetDirty(feature);
                EditorUtility.SetDirty(rendererData);
                count++;
            }
            return count;
        }

        [MenuItem("Tools/Unity Nanite/TOD/Validate Installation")]
        public static void ValidateInstallation()
        {
            bool profile = AssetDatabase.LoadAssetAtPath<TODProfile>(DefaultProfilePath) != null;
            bool material = AssetDatabase.LoadAssetAtPath<Material>(DefaultSkyMaterialPath) != null;
            bool prefab = AssetDatabase.LoadAssetAtPath<GameObject>(DefaultPrefabPath) != null;
            UniversalRendererData[] renderers = AssetDatabase.FindAssets("t:UniversalRendererData", new[] { "Assets" })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Select(AssetDatabase.LoadAssetAtPath<UniversalRendererData>)
                .Where(value => value != null)
                .ToArray();
            bool fog = renderers.Length > 0 &&
                       renderers.All(value => value.rendererFeatures.Any(feature => feature is TODFogRendererFeature));
            Shader skyShader = Shader.Find("Skybox/Unity Nanite TOD Dynamic Sky");
            Shader fogShader = Shader.Find("Hidden/Unity Nanite/TOD Fog");

            if (profile && material && prefab && fog && skyShader != null && fogShader != null)
            {
                Debug.Log("[TOD][Validation] PASS: profile, prefab, shaders, material and all URP renderer features are installed.");
            }
            else
            {
                Debug.LogError(
                    $"[TOD][Validation] FAIL profile={profile}, material={material}, prefab={prefab}, " +
                    $"fogFeatures={fog}, skyShader={skyShader != null}, fogShader={fogShader != null}.");
            }
        }

        // Isolated to keep the installer readable and avoid relying on internal scene APIs.
        private static class EditorSceneManagerBridge
        {
            public static void MarkActiveSceneDirty()
            {
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
                    UnityEngine.SceneManagement.SceneManager.GetActiveScene());
            }
        }
    }
}
