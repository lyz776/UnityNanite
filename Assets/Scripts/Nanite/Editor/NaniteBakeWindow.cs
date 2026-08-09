#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace Nanite.Editor
{
    enum NaniteBakeKind
    {
        StaticBase,
        Gadget
    }

    /// <summary>Single authoring entry point for packaged Nanite static assets.</summary>
    sealed class NaniteBakeWindow : EditorWindow
    {
        const string DefaultOutputFolder = "Assets";
        const float PreviewHeight = 320f;

        [SerializeField] GameObject sourceFbx;
        [SerializeField] string outputFolderPath = DefaultOutputFolder;
        [SerializeField] string outputName;
        [SerializeField] List<Shader> shaders = new List<Shader>();
        [SerializeField] NaniteBakeKind bakeKind;
        [SerializeField] Vector2 previewOrbit = new Vector2(-28f, 26f);
        [SerializeField] Vector2 previewLightOrbit = new Vector2(32f, -38f);
        [SerializeField] float previewZoom = 1.7f;
        [SerializeField] string lastSourceName;

        SerializedObject windowState;
        ReorderableList shaderList;
        PreviewRenderUtility preview;
        readonly Dictionary<string, Material> previewMaterials = new Dictionary<string, Material>();
        Material highlightMaterial;
        int selectedShaderIndex = -1;
        int previewControlId;
        Vector2 previewMouseDown;
        bool previewDragging;

        sealed class ModelMeshDescriptor
        {
            public Mesh mesh;
            public Material[] materials;
            public string transformPath;
            public MeshFilter filter;
        }

        public static void Open()
        {
            var window = GetWindow<NaniteBakeWindow>();
            window.titleContent = new GUIContent("Bake Nanite");
            window.minSize = new Vector2(460f, 620f);
            window.Show();
        }

        void OnEnable()
        {
            if (string.IsNullOrWhiteSpace(outputFolderPath))
                outputFolderPath = DefaultOutputFolder;
            if (shaders == null)
                shaders = new List<Shader>();
            if (shaders.Count == 0)
                shaders.Add(FindDefaultShader());
            windowState = new SerializedObject(this);
            BuildShaderList();
            EnsurePreview();
        }

        void OnDisable()
        {
            DisposePreviewMaterials();
            if (highlightMaterial != null)
                DestroyImmediate(highlightMaterial);
            if (preview != null)
                preview.Cleanup();
            preview = null;
        }

        void OnGUI()
        {
            if (windowState == null)
            {
                windowState = new SerializedObject(this);
                BuildShaderList();
            }

            windowState.Update();
            DrawSourceFields();
            DrawShaderList();
            DrawPreview();
            DrawBakeButton();
        }

        void DrawSourceFields()
        {
            EditorGUILayout.LabelField("Bake Nanite", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "选择项目内模型、输出目录与材质 Shader。生成会复制模型、材质和贴图；Static Base 额外生成 Nanite 资产与 Prefab。",
                MessageType.Info);

            SerializedProperty sourceProperty = windowState.FindProperty("sourceFbx");
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(sourceProperty, new GUIContent("Model (FBX)"));
            if (EditorGUI.EndChangeCheck())
            {
                windowState.ApplyModifiedProperties();
                SourceChanged();
                windowState.Update();
            }

            DrawOutputFolderField();
            EditorGUILayout.PropertyField(windowState.FindProperty("outputName"), new GUIContent("Name"));
            EditorGUILayout.PropertyField(windowState.FindProperty("bakeKind"), new GUIContent("Type"));
            if (windowState.ApplyModifiedProperties())
            {
                DisposePreviewMaterials();
                Repaint();
            }

            if (sourceFbx != null && !IsProjectModel(sourceFbx))
                EditorGUILayout.HelpBox("请选择项目内的模型资源（FBX/OBJ 等由 ModelImporter 导入的文件）。", MessageType.Error);

            if (TryGetPrimaryMesh(sourceFbx, out ModelMeshDescriptor descriptor))
            {
                int meshCount = sourceFbx.GetComponentsInChildren<MeshFilter>(true)
                    .Count(filter => filter.sharedMesh != null);
                EditorGUILayout.LabelField(
                    "Bake Mesh",
                    $"{descriptor.mesh.name} · {descriptor.mesh.subMeshCount} submeshes" +
                    (meshCount > 1 ? $" · selects the largest of {meshCount} static meshes" : string.Empty));
                if (meshCount > 1)
                {
                    EditorGUILayout.HelpBox(
                        "当前一次 Bake 生成一个 NaniteMesh；此模型会选择三角面最多的静态 Mesh。请将需要整体 Nanite 化的多节点模型先合并导出。",
                        MessageType.Warning);
                }
            }
        }

        void DrawOutputFolderField()
        {
            SerializedProperty pathProperty = windowState.FindProperty("outputFolderPath");
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PropertyField(pathProperty, new GUIContent("Output Folder"));
            if (GUILayout.Button("Select...", GUILayout.Width(72f)))
            {
                windowState.ApplyModifiedProperties();
                SelectOutputFolder();
                windowState.Update();
            }
            EditorGUILayout.EndHorizontal();
        }

        void SelectOutputFolder()
        {
            string startingFolder = AbsoluteAssetPath(OutputFolderPath());
            if (!Directory.Exists(startingFolder))
                startingFolder = Application.dataPath;
            string selected = EditorUtility.OpenFolderPanel("Select Nanite output folder", startingFolder, string.Empty);
            if (string.IsNullOrEmpty(selected))
                return;

            string relative = ProjectRelativePath(selected);
            if (!IsAssetFolder(relative))
            {
                EditorUtility.DisplayDialog(
                    "Invalid output folder",
                    "Nanite output must be located inside this project's Assets folder.",
                    "OK");
                return;
            }

            Undo.RecordObject(this, "Set Nanite Output Folder");
            outputFolderPath = relative;
            EditorUtility.SetDirty(this);
        }

        void DrawShaderList()
        {
            EditorGUILayout.Space(4f);
            shaderList?.DoLayoutList();
            if (selectedShaderIndex >= 0)
            {
                string hint = selectedShaderIndex == shaders.Count - 1
                    ? $"正在高亮 SubMesh {selectedShaderIndex + 1} 及其后的 SubMesh。点击预览空白处可取消。"
                    : $"正在高亮 SubMesh {selectedShaderIndex + 1}。点击预览空白处可取消。";
                EditorGUILayout.LabelField(hint, EditorStyles.miniLabel);
            }
            if (windowState.ApplyModifiedProperties())
            {
                DisposePreviewMaterials();
                Repaint();
            }
        }

        void DrawPreview()
        {
            Rect rect = GUILayoutUtility.GetRect(1f, PreviewHeight, GUILayout.ExpandWidth(true));
            GUI.Box(rect, GUIContent.none, EditorStyles.helpBox);
            if (!TryGetPrimaryMesh(sourceFbx, out _))
            {
                GUI.Label(rect, "选择含 MeshFilter 的项目内模型后显示预览", CenteredLabel());
                return;
            }

            HandlePreviewInput(rect);
            if (Event.current.type != EventType.Repaint)
                return;

            EnsurePreview();
            preview.BeginPreview(rect, GUIStyle.none);
            RenderPreviewModel();
            preview.camera.Render();
            Texture result = preview.EndPreview();
            GUI.DrawTexture(rect, result, ScaleMode.StretchToFill, false);
            GUI.Label(
                new Rect(rect.x + 8f, rect.y + 6f, rect.width - 16f, 20f),
                "拖动：旋转模型   Ctrl + 拖动：旋转灯光   滚轮：缩放",
                EditorStyles.whiteMiniLabel);
        }

        void DrawBakeButton()
        {
            EditorGUILayout.Space(8f);
            bool canBake = CanBake(out string reason);
            using (new EditorGUI.DisabledScope(!canBake))
            {
                if (GUILayout.Button(bakeKind == NaniteBakeKind.StaticBase ? "Generate Static Base" : "Generate Gadget", GUILayout.Height(34f)))
                    Generate(false);
            }
            if (!string.IsNullOrEmpty(reason))
                EditorGUILayout.HelpBox(reason, MessageType.Warning);
        }

        void BuildShaderList()
        {
            if (windowState == null)
                return;
            SerializedProperty shadersProperty = windowState.FindProperty("shaders");
            shaderList = new ReorderableList(windowState, shadersProperty, true, true, true, true)
            {
                drawHeaderCallback = rect => EditorGUI.LabelField(rect, "Material Shaders (last entry applies to remaining SubMeshes)"),
                drawElementCallback = (rect, index, active, focused) =>
                {
                    int subMeshCount = TryGetPrimaryMesh(sourceFbx, out ModelMeshDescriptor descriptor)
                        ? descriptor.mesh.subMeshCount
                        : 0;
                    string mapping = index == shaders.Count - 1 && index + 1 < subMeshCount
                        ? $"SubMesh {index + 1}+"
                        : $"SubMesh {index + 1}";
                    rect.y += 2f;
                    EditorGUI.PropertyField(rect, shadersProperty.GetArrayElementAtIndex(index), new GUIContent(mapping));
                },
                onSelectCallback = list =>
                {
                    selectedShaderIndex = list.index;
                    Repaint();
                },
                onAddCallback = list =>
                {
                    Undo.RecordObject(this, "Add Nanite Material Shader");
                    int index = shadersProperty.arraySize;
                    shadersProperty.arraySize++;
                    shadersProperty.GetArrayElementAtIndex(index).objectReferenceValue = FindDefaultShader();
                    windowState.ApplyModifiedProperties();
                    selectedShaderIndex = index;
                    DisposePreviewMaterials();
                },
                onRemoveCallback = list =>
                {
                    if (shadersProperty.arraySize <= 1)
                        return;
                    Undo.RecordObject(this, "Remove Nanite Material Shader");
                    ReorderableList.defaultBehaviours.DoRemoveButton(list);
                    windowState.ApplyModifiedProperties();
                    selectedShaderIndex = -1;
                    DisposePreviewMaterials();
                }
            };
        }

        void SourceChanged()
        {
            Undo.RecordObject(this, "Set Nanite Bake FBX");
            string nextName = sourceFbx != null ? Path.GetFileNameWithoutExtension(AssetDatabase.GetAssetPath(sourceFbx)) : string.Empty;
            if (string.IsNullOrEmpty(outputName) || string.Equals(outputName, lastSourceName, StringComparison.Ordinal))
                outputName = nextName;
            lastSourceName = nextName;

            int desiredCount = TryGetPrimaryMesh(sourceFbx, out ModelMeshDescriptor descriptor)
                ? Mathf.Max(1, descriptor.mesh.subMeshCount)
                : 1;
            Shader defaultShader = FindDefaultShader();
            while (shaders.Count < desiredCount)
                shaders.Add(defaultShader);
            while (shaders.Count > desiredCount)
                shaders.RemoveAt(shaders.Count - 1);
            if (shaders.Count == 0)
                shaders.Add(defaultShader);

            selectedShaderIndex = -1;
            DisposePreviewMaterials();
            EditorUtility.SetDirty(this);
            BuildShaderList();
        }

        void HandlePreviewInput(Rect rect)
        {
            Event current = Event.current;
            if (current.type == EventType.MouseUp && previewDragging && GUIUtility.hotControl == previewControlId)
            {
                if ((current.mousePosition - previewMouseDown).sqrMagnitude < 16f)
                    selectedShaderIndex = -1;
                previewDragging = false;
                GUIUtility.hotControl = 0;
                Repaint();
                current.Use();
                return;
            }
            if (!rect.Contains(current.mousePosition))
                return;

            if (current.type == EventType.MouseDown && current.button == 0)
            {
                Undo.RecordObject(this, "Adjust Nanite Preview");
                previewControlId = GUIUtility.GetControlID(FocusType.Passive);
                GUIUtility.hotControl = previewControlId;
                previewMouseDown = current.mousePosition;
                previewDragging = true;
                current.Use();
            }
            else if (current.type == EventType.MouseDrag && previewDragging && GUIUtility.hotControl == previewControlId)
            {
                if (current.control || current.command)
                {
                    previewLightOrbit.x += current.delta.x;
                    previewLightOrbit.y = Mathf.Clamp(previewLightOrbit.y + current.delta.y, -85f, 85f);
                }
                else
                {
                    previewOrbit.x += current.delta.x;
                    previewOrbit.y = Mathf.Clamp(previewOrbit.y + current.delta.y, -85f, 85f);
                }
                EditorUtility.SetDirty(this);
                Repaint();
                current.Use();
            }
            else if (current.type == EventType.ScrollWheel)
            {
                Undo.RecordObject(this, "Zoom Nanite Preview");
                previewZoom = Mathf.Clamp(previewZoom + current.delta.y * 0.06f, 0.5f, 5f);
                EditorUtility.SetDirty(this);
                Repaint();
                current.Use();
            }
        }

        void EnsurePreview()
        {
            if (preview != null)
                return;
            preview = new PreviewRenderUtility();
            preview.camera.clearFlags = CameraClearFlags.SolidColor;
            preview.camera.backgroundColor = new Color(0.16f, 0.16f, 0.17f, 1f);
            preview.lights[0].type = LightType.Directional;
            preview.lights[0].intensity = 1.25f;
            preview.lights[1].type = LightType.Directional;
            preview.lights[1].intensity = 0.35f;
        }

        void RenderPreviewModel()
        {
            if (preview == null || sourceFbx == null)
                return;
            Bounds bounds = CalculateModelBounds(sourceFbx);
            float radius = Mathf.Max(0.25f, bounds.extents.magnitude);
            float distance = radius * (2.0f + previewZoom);
            Quaternion orbit = Quaternion.Euler(previewOrbit.y, previewOrbit.x, 0f);
            preview.camera.transform.position = bounds.center + orbit * new Vector3(0f, 0f, -distance);
            preview.camera.transform.LookAt(bounds.center);
            preview.camera.nearClipPlane = Mathf.Max(0.01f, radius * 0.01f);
            preview.camera.farClipPlane = distance + radius * 4f;
            preview.lights[0].transform.rotation = Quaternion.Euler(previewLightOrbit.y, previewLightOrbit.x, 0f);
            preview.lights[1].transform.rotation = Quaternion.Euler(340f, 210f, 0f);
            preview.ambientColor = new Color(0.28f, 0.28f, 0.3f, 1f);

            int globalSubMeshIndex = 0;
            foreach (MeshFilter filter in sourceFbx.GetComponentsInChildren<MeshFilter>(true))
            {
                Mesh mesh = filter.sharedMesh;
                if (mesh == null)
                    continue;
                Material[] materials = filter.GetComponent<MeshRenderer>()?.sharedMaterials;
                Matrix4x4 matrix = filter.transform.localToWorldMatrix;
                for (int subMeshIndex = 0; subMeshIndex < mesh.subMeshCount; subMeshIndex++, globalSubMeshIndex++)
                {
                    Material source = materials != null && subMeshIndex < materials.Length ? materials[subMeshIndex] : null;
                    Material material = GetPreviewMaterial(source, ShaderForSubMesh(globalSubMeshIndex));
                    preview.DrawMesh(mesh, matrix, material, subMeshIndex);
                    if (IsHighlighted(globalSubMeshIndex))
                        preview.DrawMesh(mesh, matrix, GetHighlightMaterial(), subMeshIndex);
                }
            }
        }

        bool IsHighlighted(int subMeshIndex) =>
            selectedShaderIndex >= 0 &&
            (subMeshIndex == selectedShaderIndex ||
             (selectedShaderIndex == shaders.Count - 1 && subMeshIndex >= selectedShaderIndex));

        Material GetPreviewMaterial(Material source, Shader shader)
        {
            shader ??= source != null ? source.shader : FindDefaultShader();
            if (shader == null)
                shader = Shader.Find("Unlit/Color");
            string key = (source != null ? source.GetInstanceID().ToString() : "none") + ":" + shader.GetInstanceID();
            if (previewMaterials.TryGetValue(key, out Material cached) && cached != null)
                return cached;

            var material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            if (source != null)
                material.CopyPropertiesFromMaterial(source);
            previewMaterials.Add(key, material);
            return material;
        }

        Material GetHighlightMaterial()
        {
            if (highlightMaterial != null)
                return highlightMaterial;
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            highlightMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            if (highlightMaterial.HasProperty("_BaseColor"))
                highlightMaterial.SetColor("_BaseColor", new Color(1f, 0.78f, 0.1f, 1f));
            if (highlightMaterial.HasProperty("_Color"))
                highlightMaterial.SetColor("_Color", new Color(1f, 0.78f, 0.1f, 1f));
            return highlightMaterial;
        }

        void DisposePreviewMaterials()
        {
            foreach (Material material in previewMaterials.Values)
            {
                if (material != null)
                    DestroyImmediate(material);
            }
            previewMaterials.Clear();
        }

        Shader ShaderForSubMesh(int index)
        {
            if (shaders == null || shaders.Count == 0)
                return FindDefaultShader();
            return shaders[Mathf.Clamp(index, 0, shaders.Count - 1)] ?? FindDefaultShader();
        }

        bool CanBake(out string reason)
        {
            if (!IsProjectModel(sourceFbx))
            {
                reason = "请选择项目内的模型资源（FBX/OBJ 等）。";
                return false;
            }
            if (!TryGetPrimaryMesh(sourceFbx, out _))
            {
                reason = "模型中没有可 Bake 的静态 MeshFilter。";
                return false;
            }
            string folderPath = OutputFolderPath();
            if (!IsAssetFolder(folderPath))
            {
                reason = "Output Folder 必须是 Assets 下的文件夹。";
                return false;
            }
            if (string.IsNullOrEmpty(SanitizeName(outputName)))
            {
                reason = "请输入有效的输出名称。";
                return false;
            }
            if (shaders == null || shaders.Count == 0 || shaders.Any(shader => shader == null))
            {
                reason = "每个 Shader 槽位都需要有效 Shader。";
                return false;
            }
            reason = string.Empty;
            return true;
        }

        void Generate(bool overwriteWithoutPrompt)
        {
            if (!CanBake(out string reason))
            {
                ShowNotification(new GUIContent(reason));
                return;
            }

            string targetName = SanitizeName(outputName);
            string parentPath = OutputFolderPath();
            string outputRoot = parentPath + "/" + targetName;
            if (AssetDatabase.IsValidFolder(outputRoot))
            {
                if (!overwriteWithoutPrompt)
                {
                    int decision = EditorUtility.DisplayDialogComplex(
                        "Nanite Bake output already exists",
                        $"{outputRoot} already exists.",
                        "Overwrite",
                        "Append suffix",
                        "Cancel");
                    if (decision == 2)
                        return;
                    if (decision == 1)
                    {
                        targetName = NextAvailableName(parentPath, targetName);
                        outputRoot = parentPath + "/" + targetName;
                    }
                    else
                    {
                        DeleteGeneratedOutput(outputRoot);
                    }
                }
                else
                {
                    DeleteGeneratedOutput(outputRoot);
                }
            }

            try
            {
                CreateAssetFolder(outputRoot);
                string materialsFolder = outputRoot + "/Materials";
                string texturesFolder = outputRoot + "/Textures";
                string meshFolder = outputRoot + "/Mesh";
                CreateAssetFolder(materialsFolder);
                CreateAssetFolder(texturesFolder);
                CreateAssetFolder(meshFolder);

                string sourcePath = AssetDatabase.GetAssetPath(sourceFbx);
                EnableReadWrite(sourcePath);
                string sourceExtension = Path.GetExtension(sourcePath);
                string copiedModelPath = meshFolder + "/" + targetName + sourceExtension;
                if (!AssetDatabase.CopyAsset(sourcePath, copiedModelPath))
                    throw new InvalidOperationException($"Could not copy model to {copiedModelPath}.");
                EnableReadWrite(copiedModelPath);

                GameObject copiedModel = AssetDatabase.LoadAssetAtPath<GameObject>(copiedModelPath);
                if (!TryGetPrimaryMesh(sourceFbx, out ModelMeshDescriptor sourceDescriptor) ||
                    !TryFindCopiedMesh(copiedModel, sourceDescriptor.transformPath, out Mesh copiedMesh))
                {
                    throw new InvalidOperationException("The copied model does not contain the selected source Mesh.");
                }

                Material[] generatedMaterials = CreateMaterials(sourceDescriptor, materialsFolder, texturesFolder);
                if (bakeKind == NaniteBakeKind.StaticBase)
                {
                    string naniteFolder = outputRoot + "/Nanite";
                    string prefabFolder = outputRoot + "/Prefab";
                    CreateAssetFolder(naniteFolder);
                    CreateAssetFolder(prefabFolder);

                    string nanitePath = naniteFolder + "/" + targetName + ".asset";
                    string streamPath = "Nanite/" + Hash128.Compute(outputRoot).ToString() + "/" + targetName + ".npages";
                    NaniteAssetBaker.Bake(copiedMesh, nanitePath, streamPath, generatedMaterials);
                    NaniteMesh naniteMesh = AssetDatabase.LoadAssetAtPath<NaniteMesh>(nanitePath);
                    if (naniteMesh == null)
                        throw new InvalidOperationException("Nanite baker finished without creating its NaniteMesh asset.");

                    string prefabPath = prefabFolder + "/" + targetName + ".prefab";
                    CreateStaticBasePrefab(targetName, prefabPath, copiedMesh, generatedMaterials, naniteMesh);
                    Selection.activeObject = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                }
                else
                {
                    Selection.activeObject = AssetDatabase.LoadAssetAtPath<GameObject>(copiedModelPath);
                }

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                EditorGUIUtility.PingObject(Selection.activeObject);
                ShowNotification(new GUIContent($"Generated {targetName}"));
            }
            catch (OperationCanceledException)
            {
                Debug.LogWarning("[Nanite] Bake cancelled.");
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("Nanite Bake failed", exception.Message, "OK");
            }
        }

        Material[] CreateMaterials(ModelMeshDescriptor descriptor, string materialsFolder, string texturesFolder)
        {
            int subMeshCount = descriptor.mesh.subMeshCount;
            var materials = new Material[subMeshCount];
            var textureMap = new Dictionary<Texture, Texture>();
            var skippedProperties = new List<string>();
            for (int index = 0; index < subMeshCount; index++)
            {
                Material source = descriptor.materials != null && index < descriptor.materials.Length
                    ? descriptor.materials[index]
                    : null;
                Shader shader = ShaderForSubMesh(index);
                var material = new Material(shader) { name = source != null ? source.name : $"{descriptor.mesh.name}_{index + 1}" };
                if (source != null)
                    material.CopyPropertiesFromMaterial(source);

                if (source != null)
                {
                    foreach (string propertyName in source.GetTexturePropertyNames())
                    {
                        Texture sourceTexture = source.GetTexture(propertyName);
                        if (sourceTexture == null)
                            continue;
                        Texture copiedTexture = CopyTexture(sourceTexture, texturesFolder, textureMap);
                        if (!material.HasProperty(propertyName))
                        {
                            skippedProperties.Add($"{material.name}.{propertyName}");
                            continue;
                        }
                        material.SetTexture(propertyName, copiedTexture);
                    }
                }

                string path = AssetDatabase.GenerateUniqueAssetPath(
                    materialsFolder + "/" + SanitizeName(material.name) + ".mat");
                AssetDatabase.CreateAsset(material, path);
                materials[index] = material;
            }
            if (skippedProperties.Count > 0)
            {
                Debug.LogWarning(
                    "[Nanite] The selected Shader has no matching texture property for: " +
                    string.Join(", ", skippedProperties.Distinct()) + ".");
            }
            return materials;
        }

        static Texture CopyTexture(Texture source, string destinationFolder, IDictionary<Texture, Texture> copied)
        {
            if (copied.TryGetValue(source, out Texture result) && result != null)
                return result;

            string sourcePath = AssetDatabase.GetAssetPath(source);
            string extension = Path.GetExtension(sourcePath);
            bool canCopyAsset = !string.IsNullOrEmpty(sourcePath) &&
                                !(AssetImporter.GetAtPath(sourcePath) is ModelImporter);
            string targetPath = AssetDatabase.GenerateUniqueAssetPath(
                destinationFolder + "/" + SanitizeName(source.name) + (canCopyAsset ? extension : ".asset"));
            if (canCopyAsset && AssetDatabase.CopyAsset(sourcePath, targetPath))
            {
                result = AssetDatabase.LoadAllAssetsAtPath(targetPath).OfType<Texture>().FirstOrDefault();
            }
            else
            {
                result = Instantiate(source);
                result.name = source.name;
                AssetDatabase.CreateAsset(result, targetPath);
            }

            if (result == null)
                throw new InvalidOperationException($"Could not copy texture {source.name}.");
            copied.Add(source, result);
            return result;
        }

        static void CreateStaticBasePrefab(
            string name,
            string prefabPath,
            Mesh sourceMesh,
            Material[] materials,
            NaniteMesh naniteMesh)
        {
            EnsureStaticBaseTag();
            var root = new GameObject(name);
            try
            {
                root.isStatic = true;
                root.tag = "static base";
                MeshFilter filter = root.AddComponent<MeshFilter>();
                filter.sharedMesh = sourceMesh;
                MeshRenderer renderer = root.AddComponent<MeshRenderer>();
                renderer.sharedMaterials = materials;
                NaniteRuntimeProxy proxy = root.AddComponent<NaniteRuntimeProxy>();
                proxy.naniteMesh = naniteMesh;
                proxy.rasterFallbackMesh = sourceMesh;
                proxy.resolveMaterials = materials;
                proxy.renderingMode = NaniteRenderingMode.Nanite;
                renderer.enabled = false;
                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            }
            finally
            {
                DestroyImmediate(root);
            }
        }

        static void EnsureStaticBaseTag()
        {
            UnityEngine.Object tagManager = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset").FirstOrDefault();
            if (tagManager == null)
                throw new InvalidOperationException("Project TagManager.asset is unavailable.");
            var serialized = new SerializedObject(tagManager);
            SerializedProperty tags = serialized.FindProperty("tags");
            for (int index = 0; index < tags.arraySize; index++)
            {
                if (string.Equals(tags.GetArrayElementAtIndex(index).stringValue, "static base", StringComparison.Ordinal))
                    return;
            }
            tags.InsertArrayElementAtIndex(tags.arraySize);
            tags.GetArrayElementAtIndex(tags.arraySize - 1).stringValue = "static base";
            serialized.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.SaveAssets();
        }

        static void DeleteGeneratedOutput(string outputRoot)
        {
            string streamFolder = "Assets/StreamingAssets/Nanite/" + Hash128.Compute(outputRoot).ToString();
            if (AssetDatabase.IsValidFolder(outputRoot))
                AssetDatabase.DeleteAsset(outputRoot);
            if (AssetDatabase.IsValidFolder(streamFolder))
                AssetDatabase.DeleteAsset(streamFolder);
            AssetDatabase.Refresh();
        }

        static string NextAvailableName(string parentPath, string proposedName)
        {
            int suffix = 1;
            string candidate;
            do
            {
                candidate = proposedName + "_" + suffix++;
            } while (AssetDatabase.IsValidFolder(parentPath + "/" + candidate));
            return candidate;
        }

        static void CreateAssetFolder(string assetPath)
        {
            string[] parts = assetPath.Replace('\\', '/').Split('/');
            if (parts.Length == 0 || !string.Equals(parts[0], "Assets", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Output must be located under Assets.");
            string current = "Assets";
            for (int index = 1; index < parts.Length; index++)
            {
                string next = current + "/" + parts[index];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    if (string.IsNullOrEmpty(AssetDatabase.CreateFolder(current, parts[index])))
                        throw new IOException($"Could not create folder {next}.");
                }
                current = next;
            }
        }

        static void EnableReadWrite(string assetPath)
        {
            if (AssetImporter.GetAtPath(assetPath) is ModelImporter importer && !importer.isReadable)
            {
                importer.isReadable = true;
                importer.SaveAndReimport();
            }
        }

        static bool TryFindCopiedMesh(GameObject model, string transformPath, out Mesh mesh)
        {
            mesh = null;
            if (model == null)
                return false;
            Transform transform = string.IsNullOrEmpty(transformPath)
                ? model.transform
                : model.transform.Find(transformPath);
            MeshFilter filter = transform != null ? transform.GetComponent<MeshFilter>() : null;
            mesh = filter != null ? filter.sharedMesh : null;
            return mesh != null;
        }

        static bool TryGetPrimaryMesh(GameObject model, out ModelMeshDescriptor descriptor)
        {
            descriptor = null;
            if (model == null)
                return false;
            long bestTriangleCount = -1;
            foreach (MeshFilter filter in model.GetComponentsInChildren<MeshFilter>(true))
            {
                Mesh mesh = filter.sharedMesh;
                if (mesh == null)
                    continue;
                long triangleCount = 0;
                for (int index = 0; index < mesh.subMeshCount; index++)
                    triangleCount += mesh.GetIndexCount(index) / 3;
                if (triangleCount <= bestTriangleCount)
                    continue;
                bestTriangleCount = triangleCount;
                descriptor = new ModelMeshDescriptor
                {
                    mesh = mesh,
                    materials = filter.GetComponent<MeshRenderer>()?.sharedMaterials ?? Array.Empty<Material>(),
                    transformPath = TransformPath(filter.transform, model.transform),
                    filter = filter
                };
            }
            return descriptor != null;
        }

        static string TransformPath(Transform transform, Transform root)
        {
            if (transform == root)
                return string.Empty;
            var names = new Stack<string>();
            for (Transform current = transform; current != null && current != root; current = current.parent)
                names.Push(current.name);
            return string.Join("/", names);
        }

        static Bounds CalculateModelBounds(GameObject model)
        {
            bool hasBounds = false;
            Bounds bounds = default;
            foreach (MeshFilter filter in model.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter.sharedMesh == null)
                    continue;
                Bounds meshBounds = filter.sharedMesh.bounds;
                Vector3 center = filter.transform.localToWorldMatrix.MultiplyPoint3x4(meshBounds.center);
                Vector3 extents = Vector3.Scale(meshBounds.extents, Abs(filter.transform.lossyScale));
                Bounds transformed = new Bounds(center, extents * 2f);
                if (hasBounds)
                    bounds.Encapsulate(transformed);
                else
                {
                    bounds = transformed;
                    hasBounds = true;
                }
            }
            return hasBounds ? bounds : new Bounds(Vector3.zero, Vector3.one);
        }

        static Vector3 Abs(Vector3 value) => new Vector3(Mathf.Abs(value.x), Mathf.Abs(value.y), Mathf.Abs(value.z));

        string OutputFolderPath()
        {
            string path = string.IsNullOrWhiteSpace(outputFolderPath)
                ? DefaultOutputFolder
                : outputFolderPath.Trim().Replace('\\', '/');
            return path.TrimEnd('/');
        }

        static string AbsoluteAssetPath(string assetPath)
        {
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            return string.IsNullOrEmpty(projectRoot)
                ? Application.dataPath
                : Path.GetFullPath(Path.Combine(projectRoot, assetPath));
        }

        static string ProjectRelativePath(string absolutePath)
        {
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            if (string.IsNullOrEmpty(projectRoot))
                return string.Empty;
            string fullRoot = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string fullPath = Path.GetFullPath(absolutePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return string.Empty;
            return fullPath.Substring(fullRoot.Length + 1).Replace('\\', '/');
        }

        static bool IsAssetFolder(string path) =>
            !string.IsNullOrEmpty(path) && path.StartsWith("Assets", StringComparison.OrdinalIgnoreCase) && AssetDatabase.IsValidFolder(path);

        static bool IsProjectModel(GameObject model)
        {
            if (model == null)
                return false;
            string path = AssetDatabase.GetAssetPath(model);
            return !string.IsNullOrEmpty(path) && AssetImporter.GetAtPath(path) is ModelImporter;
        }

        static string SanitizeName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;
            string result = value.Trim();
            foreach (char invalid in Path.GetInvalidFileNameChars())
                result = result.Replace(invalid, '_');
            return result.Replace('/', '_').Replace('\\', '_');
        }

        static Shader FindDefaultShader() =>
            Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard") ?? Shader.Find("Unlit/Color");

        static GUIStyle CenteredLabel()
        {
            return new GUIStyle(EditorStyles.centeredGreyMiniLabel)
            {
                alignment = TextAnchor.MiddleCenter
            };
        }
    }
}
#endif
