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
                UpgradeGeneratedDefaultProfile(profile);
                // 让旧资源在新增序列化字段后也能稳定写回当前结构。
                EditorUtility.SetDirty(profile);
                return profile;
            }

            profile = ScriptableObject.CreateInstance<TODProfile>();
            profile.name = "Default TOD Profile";
            UpgradeGeneratedDefaultProfile(profile);
            AssetDatabase.CreateAsset(profile, DefaultProfilePath);
            return profile;
        }

        private static void UpgradeGeneratedDefaultProfile(TODProfile profile)
        {
            const int currentVersion = 5;
            if (profile.generatedPresetVersion >= currentVersion)
                return;

            profile.sky.lightBottom = new TODColorParameter(
                new Color(0.48f, 0.68f, 0.92f), true,
                TODProfile.DayGradient(
                    new Color(0.006f, 0.01f, 0.028f),
                    new Color(0.52f, 0.20f, 0.16f),
                    new Color(0.48f, 0.68f, 0.92f)));
            profile.sky.lightMiddle = new TODColorParameter(
                new Color(0.25f, 0.52f, 0.88f), true,
                TODProfile.DayGradient(
                    new Color(0.008f, 0.015f, 0.045f),
                    new Color(0.35f, 0.22f, 0.38f),
                    new Color(0.25f, 0.52f, 0.88f)));
            profile.sky.lightTop = new TODColorParameter(
                new Color(0.08f, 0.30f, 0.70f), true,
                TODProfile.DayGradient(
                    new Color(0.003f, 0.008f, 0.03f),
                    new Color(0.08f, 0.10f, 0.25f),
                    new Color(0.08f, 0.30f, 0.70f)));
            profile.sky.horizonColor = new TODColorParameter(
                new Color(0.68f, 0.78f, 0.92f), true,
                TODProfile.DayGradient(
                    new Color(0.025f, 0.04f, 0.085f),
                    new Color(0.95f, 0.38f, 0.18f),
                    new Color(0.68f, 0.78f, 0.92f)));
            profile.sky.groundColor = new TODColorParameter(
                new Color(0.08f, 0.12f, 0.16f), true,
                TODProfile.DayGradient(
                    new Color(0.008f, 0.012f, 0.022f),
                    new Color(0.12f, 0.065f, 0.055f),
                    new Color(0.08f, 0.12f, 0.16f)));
            profile.sky.horizonWidth = new TODFloatParameter(0.085f);
            profile.sky.horizonIntensity = new TODFloatParameter(0.68f);
            profile.sky.horizonSunGlow = new TODFloatParameter(0.42f);
            profile.sky.horizonSunGlowPower = new TODFloatParameter(10f);
            profile.sky.sunScatterColor = new TODColorParameter(new Color(1f, 0.72f, 0.45f));
            profile.sky.sunScatterIntensity = new TODFloatParameter(0.16f);
            profile.sky.sunScatterPower = new TODFloatParameter(34f);
            profile.sky.mieAnisotropy = new TODFloatParameter(0.76f);
            profile.sky.mieOpticalDepth = new TODFloatParameter(0.85f);
            profile.sky.mieHorizonBoost = new TODFloatParameter(1.35f);
            profile.sky.mieMoonAmount = new TODFloatParameter(0.18f);

            profile.sun.diskSize = new TODFloatParameter(0.016f);
            profile.sun.diskSoftness = new TODFloatParameter(0.0015f);
            profile.sun.haloColor = new TODColorParameter(new Color(1f, 0.62f, 0.32f));
            profile.sun.haloSize = new TODFloatParameter(0.065f);
            profile.sun.haloIntensity = new TODFloatParameter(0.16f);

            profile.moon.diskSize = new TODFloatParameter(0.02f);
            profile.moon.diskSoftness = new TODFloatParameter(0.0012f);
            profile.moon.phaseSoftness = new TODFloatParameter(0.055f);
            profile.moon.phaseRotation = new TODFloatParameter(0f);
            profile.moon.earthshine = new TODFloatParameter(0.16f);
            profile.moon.surfaceDetail = new TODFloatParameter(0.32f);
            profile.moon.surfaceScale = new TODFloatParameter(7f);
            profile.moon.atmosphereBlend = new TODFloatParameter(0.28f);
            profile.moon.intensity = new TODFloatParameter(
                0.35f, true,
                new AnimationCurve(
                    new Keyframe(0f, 0.4f), new Keyframe(5f, 0.35f), new Keyframe(7f, 0f),
                    new Keyframe(17.5f, 0f), new Keyframe(19f, 0.35f), new Keyframe(24f, 0.4f)));

            profile.stars.density = new TODFloatParameter(0.982f);
            profile.stars.size = new TODFloatParameter(0.12f);
            profile.stars.sizeVariation = new TODFloatParameter(0.78f);
            profile.stars.brightnessVariation = new TODFloatParameter(0.45f);
            profile.stars.intensity = new TODFloatParameter(
                1f, true,
                new AnimationCurve(
                    new Keyframe(0f, 0.75f), new Keyframe(5f, 0.65f), new Keyframe(6.5f, 0f),
                    new Keyframe(18f, 0f), new Keyframe(19.5f, 0.65f), new Keyframe(24f, 0.75f)));

            profile.clouds.color = new TODColorParameter(
                new Color(0.92f, 0.96f, 1f), true,
                TODProfile.DayGradient(
                    new Color(0.045f, 0.06f, 0.11f),
                    new Color(0.72f, 0.33f, 0.25f),
                    new Color(0.92f, 0.96f, 1f)));
            profile.clouds.shadowColor = new TODColorParameter(new Color(0.28f, 0.34f, 0.44f));
            profile.clouds.opacity = new TODFloatParameter(0.42f);
            profile.clouds.coverage = new TODFloatParameter(0.6f);
            profile.clouds.scale = new TODFloatParameter(3.2f);
            profile.clouds.detailScale = new TODFloatParameter(2.85f);
            profile.clouds.softness = new TODFloatParameter(0.14f);
            profile.clouds.erosion = new TODFloatParameter(0.32f);
            profile.clouds.distortion = new TODFloatParameter(1.35f);
            profile.clouds.altitude = new TODFloatParameter(6f);
            profile.clouds.thickness = new TODFloatParameter(0.9f);
            profile.clouds.densityMultiplier = new TODFloatParameter(1f);
            profile.clouds.horizonDensity = new TODFloatParameter(0.55f);
            profile.clouds.zenithDensity = new TODFloatParameter(1f);
            profile.clouds.latitudePosition = new TODFloatParameter(0.3f);
            profile.clouds.latitudeWidth = new TODFloatParameter(0.35f);
            profile.clouds.scatteringCoefficient =
                new TODColorParameter(new Color(0.7f, 0.72f, 0.76f));
            profile.clouds.absorptionCoefficient =
                new TODColorParameter(new Color(0.015f, 0.02f, 0.03f));
            profile.clouds.phaseForward = new TODFloatParameter(0.72f);
            profile.clouds.phaseBackward = new TODFloatParameter(-0.28f);
            profile.clouds.phaseBlend = new TODFloatParameter(0.78f);
            profile.clouds.sunLighting = new TODFloatParameter(0.85f);
            profile.clouds.moonLighting = new TODFloatParameter(0.22f);
            profile.clouds.ambientColor =
                new TODColorParameter(new Color(0.32f, 0.42f, 0.58f));
            profile.clouds.ambientIntensity = new TODFloatParameter(0.38f);
            profile.clouds.multipleScattering = new TODFloatParameter(0.28f);
            profile.clouds.aerialPerspective = new TODFloatParameter(0.55f);

            profile.generatedPresetVersion = currentVersion;
            EditorUtility.SetDirty(profile);
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
