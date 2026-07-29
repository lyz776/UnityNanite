using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityNanite.TOD.Editor
{
    public sealed class TODWindow : EditorWindow
    {
        private const float ToolbarHeight = 42f;
        private const float LeftWidth = 225f;
        private const float RightWidth = 390f;
        private const string DefaultProfileFolder = "Assets/TOD/Profiles";

        private readonly List<TODProfile> profiles = new List<TODProfile>();
        private Vector2 profileScroll;
        private Vector2 parameterScroll;
        private Vector2 detailScroll;
        private TODProfile selectedProfile;
        private TODController previewController;
        private SerializedObject selectedObject;
        private string selectedPath;

        private int selectedCurveKey = -1;
        private int selectedGradientKey = -1;
        private int draggingKey = -1;
        private int canvasHotControl;
        private Vector2 dragStartMouse;
        private Keyframe[] curveDragSnapshot;
        private GradientColorKey[] gradientDragColors;
        private GradientAlphaKey[] gradientDragAlphas;
        private bool dragHorizontalLock;
        private bool dragVerticalLock;

        [MenuItem("Window/Unity Nanite/24 Hour TOD")]
        public static void Open()
        {
            TODWindow window = GetWindow<TODWindow>("24 Hour TOD");
            window.EnsureWindowSize();
            window.Show();
        }

        public static void Open(TODController controller)
        {
            TODWindow window = GetWindow<TODWindow>("24 Hour TOD");
            window.previewController = controller;
            window.SelectProfile(controller != null ? controller.Profile : null, true);
            window.EnsureWindowSize();
            window.Show();
        }

        private void OnEnable()
        {
            minSize = new Vector2(1080f, 680f);
            TryResolvePreviewController();
            RefreshProfiles();
            Undo.undoRedoPerformed += OnUndoRedo;
            EditorApplication.projectChanged += RefreshProfiles;
        }

        private void OnDisable()
        {
            Undo.undoRedoPerformed -= OnUndoRedo;
            EditorApplication.projectChanged -= RefreshProfiles;
        }

        private void EnsureWindowSize()
        {
            minSize = new Vector2(1080f, 680f);
            if (position.width >= 1080f && position.height >= 680f)
                return;
            Vector2 size = new Vector2(1320f, 800f);
            position = new Rect(position.position, size);
        }

        private void OnSelectionChange()
        {
            TODController controller = Selection.activeGameObject != null
                ? Selection.activeGameObject.GetComponent<TODController>()
                : null;
            if (controller != null && controller != previewController)
            {
                previewController = controller;
                SelectProfile(controller.Profile, true);
            }
            Repaint();
        }

        private void TryResolvePreviewController()
        {
            if (previewController != null)
                return;
            if (Selection.activeGameObject != null)
                previewController = Selection.activeGameObject.GetComponent<TODController>();
            if (previewController == null)
                previewController = FindFirstObjectByType<TODController>();
        }

        private void OnGUI()
        {
            DrawToolbar();

            Rect body = new Rect(0f, ToolbarHeight, position.width, position.height - ToolbarHeight);
            float rightWidth = Mathf.Min(RightWidth, Mathf.Max(340f, position.width * 0.31f));
            Rect left = new Rect(body.x, body.y, LeftWidth, body.height);
            Rect center = new Rect(left.xMax, body.y, body.width - LeftWidth - rightWidth, body.height);
            Rect right = new Rect(center.xMax, body.y, rightWidth, body.height);

            GUILayout.BeginArea(left, EditorStyles.helpBox);
            DrawProfileList();
            GUILayout.EndArea();

            GUILayout.BeginArea(center, EditorStyles.helpBox);
            DrawSelectedParameter();
            GUILayout.EndArea();

            GUILayout.BeginArea(right, EditorStyles.helpBox);
            DrawParameterList();
            GUILayout.EndArea();
        }

        private void DrawToolbar()
        {
            Rect toolbar = new Rect(0f, 0f, position.width, ToolbarHeight);
            EditorGUI.DrawRect(toolbar, new Color(0.10f, 0.13f, 0.17f, 1f));
            GUILayout.BeginArea(toolbar);
            EditorGUILayout.BeginHorizontal(GUILayout.Height(ToolbarHeight));
            GUILayout.Space(8f);
            EditorGUILayout.LabelField("TOD 实时预览", EditorStyles.boldLabel, GUILayout.Width(92f));
            TODController newController = (TODController)EditorGUILayout.ObjectField(
                previewController, typeof(TODController), true, GUILayout.Width(220f));
            if (newController != previewController)
            {
                previewController = newController;
                if (previewController != null)
                    SelectProfile(previewController.Profile, true);
            }

            using (new EditorGUI.DisabledScope(previewController == null))
            {
                GUIStyle playStyle = new GUIStyle(GUI.skin.button) { fontStyle = FontStyle.Bold };
                if (GUILayout.Button(
                        previewController != null && previewController.AdvanceTime ? "Ⅱ  暂停" : "▶  播放",
                        playStyle, GUILayout.Width(82f), GUILayout.Height(26f)))
                {
                    Undo.RecordObject(previewController, "切换 TOD 时间");
                    previewController.AdvanceTime = !previewController.AdvanceTime;
                    EditorUtility.SetDirty(previewController);
                }

                if (previewController != null)
                {
                    EditorGUI.BeginChangeCheck();
                    float hour = EditorGUILayout.Slider(previewController.CurrentTime, 0f, 24f, GUILayout.MinWidth(260f));
                    if (EditorGUI.EndChangeCheck())
                        SetPreviewTime(hour, null, false);
                    GUILayout.Label(FormatTime(previewController.CurrentTime), EditorStyles.boldLabel, GUILayout.Width(62f));
                }
            }

            GUILayout.FlexibleSpace();
            if (GUILayout.Button("刷新资源", GUILayout.Width(74f), GUILayout.Height(24f)))
                RefreshProfiles();
            GUILayout.Space(8f);
            EditorGUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        private void DrawProfileList()
        {
            EditorGUILayout.LabelField("TOD Profiles", LargeTitleStyle(), GUILayout.Height(30f));
            EditorGUILayout.HelpBox("单击查看；双击立即加载到预览挂点。", MessageType.Info);
            profileScroll = EditorGUILayout.BeginScrollView(profileScroll);

            foreach (TODProfile profile in profiles)
            {
                bool selected = profile == selectedProfile;
                bool loaded = previewController != null && previewController.Profile == profile;
                Rect row = EditorGUILayout.GetControlRect(false, 32f);
                Rect pingRect = new Rect(row.xMax - 44f, row.y + 6f, 40f, 20f);
                Rect mainRect = new Rect(row.x, row.y, row.width - 48f, row.height);
                EditorGUI.DrawRect(row, selected
                    ? new Color(0.18f, 0.38f, 0.58f, 0.55f)
                    : new Color(0.16f, 0.17f, 0.19f, 0.6f));

                Event current = Event.current;
                if (current.type == EventType.MouseDown && current.button == 0 &&
                    current.clickCount == 2 && mainRect.Contains(current.mousePosition))
                {
                    LoadProfile(profile);
                    current.Use();
                }
                else if (GUI.Button(mainRect, loaded ? $"● {profile.name}" : profile.name, EditorStyles.label))
                {
                    SelectProfile(profile, true);
                }

                if (GUI.Button(pingRect, "定位", EditorStyles.miniButton))
                {
                    EditorGUIUtility.PingObject(profile);
                    Selection.activeObject = profile;
                }
            }
            EditorGUILayout.EndScrollView();

            using (new EditorGUI.DisabledScope(selectedProfile == null || previewController == null))
            {
                if (GUILayout.Button("加载所选 Profile", GUILayout.Height(29f)))
                    LoadProfile(selectedProfile);
            }

            if (GUILayout.Button("新建 Profile", GUILayout.Height(27f)))
                CreateProfile();
        }

        private void LoadProfile(TODProfile profile)
        {
            TryResolvePreviewController();
            SelectProfile(profile, true);
            if (previewController == null)
            {
                ShowNotification(new GUIContent("场景中没有 TODController，无法加载 Profile。"));
                return;
            }

            Undo.RecordObject(previewController, "加载 TOD Profile");
            previewController.Profile = profile;
            PrefabUtility.RecordPrefabInstancePropertyModifications(previewController);
            EditorUtility.SetDirty(previewController);
            Selection.activeGameObject = previewController.gameObject;
            EditorApplication.delayCall += () =>
            {
                ActiveEditorTracker.sharedTracker.ForceRebuild();
                RepaintEverything();
            };
            RepaintEverything();
        }

        private void CreateProfile()
        {
            EnsureProfileFolder();
            string path = EditorUtility.SaveFilePanelInProject(
                "新建 TOD Profile",
                "New TOD Profile",
                "asset",
                "请选择 TOD Profile 的保存位置。",
                DefaultProfileFolder);
            if (string.IsNullOrEmpty(path))
                return;

            TODProfile profile = CreateInstance<TODProfile>();
            AssetDatabase.CreateAsset(profile, path);
            AssetDatabase.SaveAssets();
            RefreshProfiles();
            SelectProfile(profile, true);
            Selection.activeObject = profile;
        }

        private static void EnsureProfileFolder()
        {
            if (!AssetDatabase.IsValidFolder("Assets/TOD"))
                AssetDatabase.CreateFolder("Assets", "TOD");
            if (!AssetDatabase.IsValidFolder(DefaultProfileFolder))
                AssetDatabase.CreateFolder("Assets/TOD", "Profiles");
        }

        private void DrawParameterList()
        {
            EditorGUILayout.LabelField(
                selectedProfile != null ? $"Profile 参数：{selectedProfile.name}" : "未选择 Profile",
                LargeTitleStyle(), GUILayout.Height(30f));

            if (selectedObject == null || selectedObject.targetObject == null)
            {
                EditorGUILayout.HelpBox("请从左侧选择一个 TOD Profile。", MessageType.Info);
                return;
            }

            parameterScroll = EditorGUILayout.BeginScrollView(parameterScroll);
            TODProfileEditorGUI.DrawProfile(
                selectedObject,
                descriptor =>
                {
                    selectedPath = descriptor.Path;
                    ResetNodeSelection();
                    GUI.FocusControl(null);
                    Repaint();
                },
                true);
            EditorGUILayout.EndScrollView();
        }

        private void DrawSelectedParameter()
        {
            if (selectedProfile == null)
            {
                EditorGUILayout.HelpBox("选择或创建 Profile 后开始编辑。", MessageType.Info);
                return;
            }

            TODParameterDescriptor? descriptorValue = TODProfileEditorGUI.FindDescriptor(selectedPath);
            if (!descriptorValue.HasValue)
            {
                EditorGUILayout.HelpBox("从右侧点击一个参数名以打开节点编辑器。", MessageType.Info);
                return;
            }

            TODParameterDescriptor descriptor = descriptorValue.Value;
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField($"{descriptor.Section}  /  {descriptor.Label}", LargeTitleStyle(), GUILayout.Height(30f));
            GUILayout.FlexibleSpace();
            if (previewController != null)
                GUILayout.Label($"预览时间  {FormatTime(previewController.CurrentTime)}", EditorStyles.boldLabel);
            EditorGUILayout.EndHorizontal();

            detailScroll = EditorGUILayout.BeginScrollView(detailScroll);
            object parameter = TODProfileEditorGUI.ResolveObject(selectedProfile, selectedPath);
            if (parameter is TODFloatParameter floatParameter)
                DrawFloatDetail(floatParameter, descriptor);
            else if (parameter is TODColorParameter colorParameter)
                DrawColorDetail(colorParameter, descriptor);
            EditorGUILayout.EndScrollView();
        }

        private void DrawFloatDetail(TODFloatParameter parameter, TODParameterDescriptor descriptor)
        {
            Undo.RecordObject(selectedProfile, $"编辑 {descriptor.Label}");
            EditorGUI.BeginChangeCheck();
            parameter.useCurve = EditorGUILayout.ToggleLeft("使用 0–24 小时曲线", parameter.useCurve);
            if (!parameter.useCurve)
            {
                parameter.constant = DrawValue("数值", parameter.constant, descriptor);
                EditorGUILayout.HelpBox("拖动“数值”文字标签可快速调整；按住 Shift 可精细调整。", MessageType.None);
                if (EditorGUI.EndChangeCheck())
                    CommitProfileEdit();
                return;
            }

            if (parameter.curve == null)
                parameter.curve = AnimationCurve.Linear(0f, parameter.constant, 24f, parameter.constant);

            Rect canvas = GUILayoutUtility.GetRect(320f, Mathf.Max(360f, position.height - 250f), GUILayout.ExpandWidth(true));
            DrawCurveCanvas(canvas, parameter, descriptor);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("＋ 在当前时间创建节点", GUILayout.Height(27f)))
                AddCurveKey(parameter);
            using (new EditorGUI.DisabledScope(selectedCurveKey < 0 || parameter.curve.length <= 1))
            {
                if (GUILayout.Button("删除选中节点", GUILayout.Height(27f)))
                    DeleteCurveKey(parameter);
            }
            EditorGUILayout.EndHorizontal();

            DrawSelectedCurveKeyEditor(parameter, descriptor);
            EditorGUILayout.HelpBox(
                "双击节点：跳转到节点时间；拖动节点：只改节点时间和值，预览时间保持固定；按 Shift 后拖动：锁定水平或垂直方向；Esc：取消本次拖动。跳时不进入 Undo。",
                MessageType.Info);

            if (EditorGUI.EndChangeCheck())
                CommitProfileEdit();
        }

        private void DrawCurveCanvas(Rect canvas, TODFloatParameter parameter, TODParameterDescriptor descriptor)
        {
            Rect timeline = new Rect(canvas.x + 12f, canvas.y + 8f, canvas.width - 24f, 40f);
            Rect plot = new Rect(canvas.x + 48f, timeline.yMax + 8f, canvas.width - 66f, canvas.height - 70f);
            EditorGUI.DrawRect(canvas, new Color(0.075f, 0.085f, 0.10f, 1f));
            EditorGUI.DrawRect(plot, new Color(0.105f, 0.12f, 0.145f, 1f));

            GetCurveViewRange(parameter.curve, out float min, out float max);
            DrawTimeline(timeline);
            DrawGraphGrid(plot, min, max);
            HandleTimelineScrub(timeline);
            HandleCurveInput(plot, parameter, descriptor, min, max);

            Handles.BeginGUI();
            Color oldColor = Handles.color;
            Handles.color = new Color(0.25f, 0.85f, 1f, 1f);
            Vector3 previous = Vector3.zero;
            const int samples = 160;
            for (int i = 0; i <= samples; i++)
            {
                float time = 24f * i / samples;
                Vector3 point = new Vector3(TimeToX(plot, time), ValueToY(plot, parameter.curve.Evaluate(time), min, max));
                if (i > 0)
                    Handles.DrawAAPolyLine(2.5f, previous, point);
                previous = point;
            }

            if (previewController != null)
            {
                float previewX = TimeToX(plot, previewController.CurrentTime);
                Handles.color = new Color(1f, 0.76f, 0.1f, 0.9f);
                Handles.DrawAAPolyLine(2f, new Vector3(previewX, timeline.y), new Vector3(previewX, plot.yMax));
            }

            Keyframe[] keys = parameter.curve.keys;
            for (int i = 0; i < keys.Length; i++)
            {
                Vector2 point = new Vector2(TimeToX(plot, keys[i].time), ValueToY(plot, keys[i].value, min, max));
                Handles.color = i == selectedCurveKey ? new Color(1f, 0.72f, 0.08f) : new Color(0.2f, 0.86f, 1f);
                Handles.DrawSolidDisc(point, Vector3.forward, i == selectedCurveKey ? 7f : 5f);
                Handles.color = Color.black;
                Handles.DrawWireDisc(point, Vector3.forward, i == selectedCurveKey ? 7f : 5f);
                DrawNodeLabel(point, keys[i].time, keys[i].value);
            }
            Handles.color = oldColor;
            Handles.EndGUI();
        }

        private void HandleCurveInput(
            Rect plot,
            TODFloatParameter parameter,
            TODParameterDescriptor descriptor,
            float min,
            float max)
        {
            Event current = Event.current;
            int id = GUIUtility.GetControlID((selectedPath + ".curveCanvas").GetHashCode(), FocusType.Passive, plot);
            Keyframe[] keys = parameter.curve.keys;

            if (current.type == EventType.KeyDown && current.keyCode == KeyCode.Escape &&
                GUIUtility.hotControl == canvasHotControl && curveDragSnapshot != null)
            {
                parameter.curve.keys = curveDragSnapshot;
                CancelCanvasDrag();
                CommitProfileEdit();
                current.Use();
                return;
            }

            if (current.type == EventType.MouseDown && current.button == 0 && plot.Contains(current.mousePosition))
            {
                int nearest = FindNearestCurveKey(plot, keys, current.mousePosition, min, max);
                if (nearest < 0)
                    return;

                selectedCurveKey = nearest;
                selectedGradientKey = -1;
                if (current.clickCount == 2)
                {
                    SetPreviewTime(keys[nearest].time, null, false);
                    current.Use();
                    return;
                }

                Undo.RecordObject(selectedProfile, $"拖动 {descriptor.Label} 节点");
                draggingKey = nearest;
                curveDragSnapshot = (Keyframe[])keys.Clone();
                dragStartMouse = current.mousePosition;
                dragHorizontalLock = false;
                dragVerticalLock = false;
                canvasHotControl = id;
                GUIUtility.hotControl = id;
                current.Use();
            }
            else if (current.type == EventType.MouseDrag && GUIUtility.hotControl == id &&
                     draggingKey >= 0 && curveDragSnapshot != null)
            {
                Vector2 delta = current.mousePosition - dragStartMouse;
                if (current.shift && !dragHorizontalLock && !dragVerticalLock && delta.sqrMagnitude > 9f)
                {
                    dragHorizontalLock = Mathf.Abs(delta.x) >= Mathf.Abs(delta.y);
                    dragVerticalLock = !dragHorizontalLock;
                }

                Vector2 mouse = current.mousePosition;
                Vector2 originalPoint = new Vector2(
                    TimeToX(plot, curveDragSnapshot[draggingKey].time),
                    ValueToY(plot, curveDragSnapshot[draggingKey].value, min, max));
                if (current.shift && dragHorizontalLock)
                    mouse.y = originalPoint.y;
                if (current.shift && dragVerticalLock)
                    mouse.x = originalPoint.x;

                Keyframe[] moved = (Keyframe[])curveDragSnapshot.Clone();
                float time = Mathf.Clamp(XToTime(plot, mouse.x), 0f, 24f);
                float value = YToValue(plot, mouse.y, min, max);
                if (descriptor.HasRange)
                    value = Mathf.Clamp(value, descriptor.Min, descriptor.Max);
                Keyframe key = moved[draggingKey];
                key.time = time;
                key.value = value;
                moved[draggingKey] = key;
                parameter.curve.keys = moved;
                selectedCurveKey = FindClosestTime(parameter.curve.keys, time);
                CommitProfileEdit();
                current.Use();
            }
            else if (current.type == EventType.MouseUp && GUIUtility.hotControl == id)
            {
                CancelCanvasDrag();
                current.Use();
            }
        }

        private void DrawSelectedCurveKeyEditor(TODFloatParameter parameter, TODParameterDescriptor descriptor)
        {
            Keyframe[] keys = parameter.curve.keys;
            if (selectedCurveKey < 0 || selectedCurveKey >= keys.Length)
                return;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField($"选中节点 #{selectedCurveKey + 1}", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            float time = EditorGUILayout.Slider("时间", keys[selectedCurveKey].time, 0f, 24f);
            float value = DrawValue("数值", keys[selectedCurveKey].value, descriptor);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(selectedProfile, $"编辑 {descriptor.Label} 节点");
                Keyframe key = keys[selectedCurveKey];
                key.time = time;
                key.value = value;
                keys[selectedCurveKey] = key;
                parameter.curve.keys = keys;
                selectedCurveKey = FindClosestTime(parameter.curve.keys, time);
                CommitProfileEdit();
            }
            EditorGUILayout.EndVertical();
        }

        private void DrawColorDetail(TODColorParameter parameter, TODParameterDescriptor descriptor)
        {
            Undo.RecordObject(selectedProfile, $"编辑 {descriptor.Label}");
            EditorGUI.BeginChangeCheck();
            parameter.useGradient = EditorGUILayout.ToggleLeft("使用 0–24 小时 Gradient", parameter.useGradient);
            if (!parameter.useGradient)
            {
                parameter.color = EditorGUILayout.ColorField(
                    new GUIContent("HDR 颜色"), parameter.color, true, true, true);
                if (EditorGUI.EndChangeCheck())
                    CommitProfileEdit();
                return;
            }

            Rect gradientField = GUILayoutUtility.GetRect(100f, 38f, GUILayout.ExpandWidth(true));
            parameter.gradient = EditorGUI.GradientField(
                gradientField, new GUIContent("HDR Gradient"), parameter.gradient, true);

            Rect canvas = GUILayoutUtility.GetRect(320f, 210f, GUILayout.ExpandWidth(true));
            DrawGradientCanvas(canvas, parameter, descriptor);

            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(parameter.gradient.colorKeys.Length >= 8))
            {
                if (GUILayout.Button("＋ 在当前时间创建颜色节点", GUILayout.Height(27f)))
                    AddGradientKey(parameter);
            }
            using (new EditorGUI.DisabledScope(selectedGradientKey < 0 || parameter.gradient.colorKeys.Length <= 2))
            {
                if (GUILayout.Button("删除选中颜色节点", GUILayout.Height(27f)))
                    DeleteGradientKey(parameter);
            }
            EditorGUILayout.EndHorizontal();

            DrawSelectedGradientKeyEditor(parameter, descriptor);
            EditorGUILayout.HelpBox("双击颜色节点可跳转时间；拖动节点只修改节点时间，预览时间保持固定；Esc 取消拖动。跳时不进入 Undo；颜色与 Alpha 仍可点击上方 Gradient 使用 Unity 原生编辑器调整。", MessageType.Info);

            if (EditorGUI.EndChangeCheck())
                CommitProfileEdit();
        }

        private void DrawGradientCanvas(Rect canvas, TODColorParameter parameter, TODParameterDescriptor descriptor)
        {
            Rect timeline = new Rect(canvas.x + 12f, canvas.y + 8f, canvas.width - 24f, 40f);
            Rect bar = new Rect(canvas.x + 28f, timeline.yMax + 28f, canvas.width - 56f, 58f);
            EditorGUI.DrawRect(canvas, new Color(0.075f, 0.085f, 0.10f, 1f));
            DrawTimeline(timeline);
            HandleTimelineScrub(timeline);

            const int slices = 160;
            for (int i = 0; i < slices; i++)
            {
                float t = i / (float)(slices - 1);
                Rect slice = new Rect(bar.x + bar.width * i / slices, bar.y, bar.width / slices + 1f, bar.height);
                EditorGUI.DrawRect(slice, parameter.gradient.Evaluate(t));
            }
            GUI.Box(bar, GUIContent.none);

            HandleGradientInput(bar, parameter, descriptor);
            Handles.BeginGUI();
            GradientColorKey[] keys = parameter.gradient.colorKeys;
            for (int i = 0; i < keys.Length; i++)
            {
                Vector2 point = new Vector2(bar.x + keys[i].time * bar.width, bar.yMax + 14f);
                Handles.color = i == selectedGradientKey ? new Color(1f, 0.72f, 0.08f) : keys[i].color;
                Handles.DrawSolidDisc(point, Vector3.forward, i == selectedGradientKey ? 7f : 5f);
                Handles.color = Color.black;
                Handles.DrawWireDisc(point, Vector3.forward, i == selectedGradientKey ? 7f : 5f);
                GUI.Label(new Rect(point.x - 28f, point.y + 8f, 56f, 18f), FormatTime(keys[i].time * 24f), CenterMiniStyle());
            }
            Handles.EndGUI();
        }

        private void HandleGradientInput(Rect bar, TODColorParameter parameter, TODParameterDescriptor descriptor)
        {
            Rect hitRect = new Rect(bar.x - 10f, bar.yMax, bar.width + 20f, 34f);
            Event current = Event.current;
            int id = GUIUtility.GetControlID((selectedPath + ".gradientCanvas").GetHashCode(), FocusType.Passive, hitRect);
            GradientColorKey[] keys = parameter.gradient.colorKeys;

            if (current.type == EventType.KeyDown && current.keyCode == KeyCode.Escape &&
                GUIUtility.hotControl == canvasHotControl && gradientDragColors != null)
            {
                parameter.gradient.SetKeys(gradientDragColors, gradientDragAlphas);
                CancelCanvasDrag();
                CommitProfileEdit();
                current.Use();
                return;
            }

            if (current.type == EventType.MouseDown && current.button == 0 && hitRect.Contains(current.mousePosition))
            {
                int nearest = FindNearestGradientKey(bar, keys, current.mousePosition);
                if (nearest < 0)
                    return;
                selectedGradientKey = nearest;
                selectedCurveKey = -1;
                if (current.clickCount == 2)
                {
                    SetPreviewTime(keys[nearest].time * 24f, null, false);
                    current.Use();
                    return;
                }

                Undo.RecordObject(selectedProfile, $"拖动 {descriptor.Label} 节点");
                draggingKey = nearest;
                gradientDragColors = (GradientColorKey[])keys.Clone();
                gradientDragAlphas = (GradientAlphaKey[])parameter.gradient.alphaKeys.Clone();
                dragStartMouse = current.mousePosition;
                canvasHotControl = id;
                GUIUtility.hotControl = id;
                current.Use();
            }
            else if (current.type == EventType.MouseDrag && GUIUtility.hotControl == id &&
                     draggingKey >= 0 && gradientDragColors != null)
            {
                GradientColorKey[] moved = (GradientColorKey[])gradientDragColors.Clone();
                float time01 = Mathf.Clamp01((current.mousePosition.x - bar.x) / bar.width);
                moved[draggingKey].time = time01;
                parameter.gradient.SetKeys(moved, gradientDragAlphas);
                selectedGradientKey = FindClosestGradientTime(parameter.gradient.colorKeys, time01);
                CommitProfileEdit();
                current.Use();
            }
            else if (current.type == EventType.MouseUp && GUIUtility.hotControl == id)
            {
                CancelCanvasDrag();
                current.Use();
            }
        }

        private void DrawSelectedGradientKeyEditor(TODColorParameter parameter, TODParameterDescriptor descriptor)
        {
            GradientColorKey[] keys = parameter.gradient.colorKeys;
            if (selectedGradientKey < 0 || selectedGradientKey >= keys.Length)
                return;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField($"选中颜色节点 #{selectedGradientKey + 1}", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            float hour = EditorGUILayout.Slider("时间", keys[selectedGradientKey].time * 24f, 0f, 24f);
            Color color = EditorGUILayout.ColorField(
                new GUIContent("HDR 颜色"), keys[selectedGradientKey].color, true, true, true);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(selectedProfile, $"编辑 {descriptor.Label} 节点");
                keys[selectedGradientKey] = new GradientColorKey(color, hour / 24f);
                parameter.gradient.SetKeys(keys, parameter.gradient.alphaKeys);
                selectedGradientKey = FindClosestGradientTime(parameter.gradient.colorKeys, hour / 24f);
                CommitProfileEdit();
            }
            EditorGUILayout.EndVertical();
        }

        private void DrawTimeline(Rect rect)
        {
            EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - 2f, rect.width, 2f), new Color(0.45f, 0.52f, 0.62f));
            for (int hour = 0; hour <= 24; hour += 2)
            {
                float x = rect.x + rect.width * hour / 24f;
                float height = hour % 6 == 0 ? 15f : 8f;
                EditorGUI.DrawRect(new Rect(x, rect.yMax - height, 1f, height), new Color(0.58f, 0.65f, 0.74f));
                if (hour % 6 == 0)
                    GUI.Label(new Rect(x - 22f, rect.y, 44f, 18f), $"{hour:00}:00", CenterMiniStyle());
            }
        }

        private void HandleTimelineScrub(Rect rect)
        {
            if (previewController == null)
                return;
            Event current = Event.current;
            if ((current.type == EventType.MouseDown || current.type == EventType.MouseDrag) &&
                current.button == 0 && rect.Contains(current.mousePosition))
            {
                float hour = Mathf.Clamp01((current.mousePosition.x - rect.x) / rect.width) * 24f;
                SetPreviewTime(hour, null, false);
                current.Use();
            }
        }

        private static void DrawGraphGrid(Rect plot, float min, float max)
        {
            for (int hour = 0; hour <= 24; hour += 3)
            {
                float x = TimeToX(plot, hour);
                EditorGUI.DrawRect(new Rect(x, plot.y, 1f, plot.height), new Color(0.24f, 0.28f, 0.34f, 0.6f));
            }
            for (int row = 0; row <= 4; row++)
            {
                float y = Mathf.Lerp(plot.yMax, plot.y, row / 4f);
                EditorGUI.DrawRect(new Rect(plot.x, y, plot.width, 1f), new Color(0.24f, 0.28f, 0.34f, 0.6f));
                float value = Mathf.Lerp(min, max, row / 4f);
                GUI.Label(new Rect(plot.x - 44f, y - 9f, 40f, 18f), value.ToString("0.###"), RightMiniStyle());
            }
        }

        private static void DrawNodeLabel(Vector2 point, float time, float value)
        {
            Rect label = new Rect(point.x - 40f, point.y - 42f, 80f, 34f);
            EditorGUI.DrawRect(label, new Color(0.06f, 0.07f, 0.08f, 0.88f));
            GUI.Label(new Rect(label.x, label.y + 1f, label.width, 16f), FormatTime(time), CenterMiniStyle());
            GUI.Label(new Rect(label.x, label.y + 16f, label.width, 16f), value.ToString("0.###"), CenterMiniStyle());
        }

        private static void GetCurveViewRange(AnimationCurve curve, out float min, out float max)
        {
            min = float.PositiveInfinity;
            max = float.NegativeInfinity;
            if (curve != null)
            {
                foreach (Keyframe key in curve.keys)
                {
                    min = Mathf.Min(min, key.value);
                    max = Mathf.Max(max, key.value);
                }
            }
            if (float.IsInfinity(min) || float.IsInfinity(max))
            {
                min = -1f;
                max = 1f;
            }
            float span = max - min;
            if (span < 0.0001f)
                span = Mathf.Max(1f, Mathf.Abs(max) * 0.5f);
            min -= span * 0.18f;
            max += span * 0.18f;
        }

        private static int FindNearestCurveKey(Rect plot, Keyframe[] keys, Vector2 mouse, float min, float max)
        {
            int result = -1;
            float distance = 13f;
            for (int i = 0; i < keys.Length; i++)
            {
                Vector2 point = new Vector2(TimeToX(plot, keys[i].time), ValueToY(plot, keys[i].value, min, max));
                float current = Vector2.Distance(point, mouse);
                if (current < distance)
                {
                    distance = current;
                    result = i;
                }
            }
            return result;
        }

        private static int FindNearestGradientKey(Rect bar, GradientColorKey[] keys, Vector2 mouse)
        {
            int result = -1;
            float distance = 14f;
            for (int i = 0; i < keys.Length; i++)
            {
                Vector2 point = new Vector2(bar.x + keys[i].time * bar.width, bar.yMax + 14f);
                float current = Vector2.Distance(point, mouse);
                if (current < distance)
                {
                    distance = current;
                    result = i;
                }
            }
            return result;
        }

        private void AddCurveKey(TODFloatParameter parameter)
        {
            Undo.RecordObject(selectedProfile, "创建 TOD 曲线节点");
            float time = previewController != null ? previewController.CurrentTime : 12f;
            parameter.curve.AddKey(new Keyframe(time, parameter.curve.Evaluate(time)));
            selectedCurveKey = FindClosestTime(parameter.curve.keys, time);
            CommitProfileEdit();
        }

        private void DeleteCurveKey(TODFloatParameter parameter)
        {
            Undo.RecordObject(selectedProfile, "删除 TOD 曲线节点");
            parameter.curve.RemoveKey(selectedCurveKey);
            selectedCurveKey = Mathf.Clamp(selectedCurveKey - 1, -1, parameter.curve.length - 1);
            CommitProfileEdit();
        }

        private void AddGradientKey(TODColorParameter parameter)
        {
            Undo.RecordObject(selectedProfile, "创建 TOD Gradient 节点");
            float time = previewController != null ? previewController.CurrentTime / 24f : 0.5f;
            var keys = new List<GradientColorKey>(parameter.gradient.colorKeys)
            {
                new GradientColorKey(parameter.gradient.Evaluate(time), time)
            };
            parameter.gradient.SetKeys(keys.ToArray(), parameter.gradient.alphaKeys);
            selectedGradientKey = FindClosestGradientTime(parameter.gradient.colorKeys, time);
            CommitProfileEdit();
        }

        private void DeleteGradientKey(TODColorParameter parameter)
        {
            Undo.RecordObject(selectedProfile, "删除 TOD Gradient 节点");
            var keys = new List<GradientColorKey>(parameter.gradient.colorKeys);
            keys.RemoveAt(selectedGradientKey);
            parameter.gradient.SetKeys(keys.ToArray(), parameter.gradient.alphaKeys);
            selectedGradientKey = Mathf.Clamp(selectedGradientKey - 1, -1, parameter.gradient.colorKeys.Length - 1);
            CommitProfileEdit();
        }

        private static int FindClosestTime(Keyframe[] keys, float time)
        {
            int index = -1;
            float distance = float.PositiveInfinity;
            for (int i = 0; i < keys.Length; i++)
            {
                float candidate = Mathf.Abs(keys[i].time - time);
                if (candidate < distance)
                {
                    distance = candidate;
                    index = i;
                }
            }
            return index;
        }

        private static int FindClosestGradientTime(GradientColorKey[] keys, float time)
        {
            int index = -1;
            float distance = float.PositiveInfinity;
            for (int i = 0; i < keys.Length; i++)
            {
                float candidate = Mathf.Abs(keys[i].time - time);
                if (candidate < distance)
                {
                    distance = candidate;
                    index = i;
                }
            }
            return index;
        }

        private void CancelCanvasDrag()
        {
            if (GUIUtility.hotControl == canvasHotControl)
                GUIUtility.hotControl = 0;
            canvasHotControl = 0;
            draggingKey = -1;
            curveDragSnapshot = null;
            gradientDragColors = null;
            gradientDragAlphas = null;
            dragHorizontalLock = false;
            dragVerticalLock = false;
        }

        private void ResetNodeSelection()
        {
            selectedCurveKey = -1;
            selectedGradientKey = -1;
            CancelCanvasDrag();
        }

        private static float TimeToX(Rect plot, float time) => plot.x + Mathf.Clamp01(time / 24f) * plot.width;
        private static float XToTime(Rect plot, float x) => Mathf.InverseLerp(plot.x, plot.xMax, x) * 24f;
        private static float ValueToY(Rect plot, float value, float min, float max) =>
            Mathf.Lerp(plot.yMax, plot.y, Mathf.InverseLerp(min, max, value));
        private static float YToValue(Rect plot, float y, float min, float max) =>
            Mathf.Lerp(min, max, Mathf.InverseLerp(plot.yMax, plot.y, y));

        private static float DrawValue(string label, float value, TODParameterDescriptor descriptor)
        {
            return descriptor.HasRange
                ? EditorGUILayout.Slider(label, value, descriptor.Min, descriptor.Max)
                : EditorGUILayout.FloatField(label, value);
        }

        private void CommitProfileEdit()
        {
            if (selectedProfile == null)
                return;
            EditorUtility.SetDirty(selectedProfile);
            TODController.RefreshAll(selectedProfile);
            RepaintEverything();
        }

        private void SetPreviewTime(float hour, string undoName, bool recordUndo)
        {
            if (previewController == null)
                return;
            if (recordUndo && !string.IsNullOrEmpty(undoName))
                Undo.RecordObject(previewController, undoName);
            previewController.CurrentTime = hour;
            EditorUtility.SetDirty(previewController);
            RepaintEverything();
        }

        private void SelectProfile(TODProfile profile, bool resetCenter)
        {
            selectedProfile = profile;
            selectedObject = profile != null ? new SerializedObject(profile) : null;
            if (resetCenter)
            {
                selectedPath = profile != null && TODProfileEditorGUI.Parameters.Length > 0
                    ? TODProfileEditorGUI.Parameters[0].Path
                    : null;
                detailScroll = Vector2.zero;
                parameterScroll = Vector2.zero;
                ResetNodeSelection();
            }
            Repaint();
        }

        private void RefreshProfiles()
        {
            profiles.Clear();
            foreach (string guid in AssetDatabase.FindAssets("t:TODProfile"))
            {
                TODProfile profile = AssetDatabase.LoadAssetAtPath<TODProfile>(AssetDatabase.GUIDToAssetPath(guid));
                if (profile != null)
                    profiles.Add(profile);
            }
            profiles.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));

            if (selectedProfile == null && profiles.Count > 0)
                SelectProfile(previewController != null && previewController.Profile != null
                    ? previewController.Profile
                    : profiles[0], true);
            Repaint();
        }

        private void OnUndoRedo()
        {
            selectedObject?.Update();
            ResetNodeSelection();
            TODController.RefreshAll();
            RepaintEverything();
        }

        private void RepaintEverything()
        {
            Repaint();
            SceneView.RepaintAll();
            UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
        }

        private static string FormatTime(float hour)
        {
            hour = TODProfile.WrapHour(hour);
            int totalMinutes = Mathf.RoundToInt(hour * 60f) % (24 * 60);
            return $"{totalMinutes / 60:00}:{totalMinutes % 60:00}";
        }

        private static GUIStyle LargeTitleStyle()
        {
            return new GUIStyle(EditorStyles.boldLabel) { fontSize = 14, fontStyle = FontStyle.Bold };
        }

        private static GUIStyle CenterMiniStyle()
        {
            return new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleCenter };
        }

        private static GUIStyle RightMiniStyle()
        {
            return new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleRight };
        }
    }
}
