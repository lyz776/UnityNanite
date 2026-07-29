using UnityEditor;
using UnityEngine;

namespace UnityNanite.TOD.Editor
{
    [CustomEditor(typeof(TODController))]
    public sealed class TODControllerEditor : UnityEditor.Editor
    {
        private SerializedProperty profile;
        private SerializedProperty currentTime;
        private SerializedProperty advanceTime;
        private SerializedProperty hoursPerSecond;
        private SerializedProperty mainLight;
        private SerializedProperty sunVisual;
        private SerializedProperty moonVisual;
        private SerializedProperty assignSkybox;
        private SerializedProperty skyboxMaterial;
        private SerializedObject profileObject;

        private void OnEnable()
        {
            profile = serializedObject.FindProperty("profile");
            currentTime = serializedObject.FindProperty("currentTime");
            advanceTime = serializedObject.FindProperty("advanceTime");
            hoursPerSecond = serializedObject.FindProperty("hoursPerSecond");
            mainLight = serializedObject.FindProperty("mainLight");
            sunVisual = serializedObject.FindProperty("sunVisual");
            moonVisual = serializedObject.FindProperty("moonVisual");
            assignSkybox = serializedObject.FindProperty("assignSkybox");
            skyboxMaterial = serializedObject.FindProperty("skyboxMaterial");
            RebuildProfileObject();
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            if (profileObject == null ||
                profileObject.targetObject != profile.objectReferenceValue)
            {
                RebuildProfileObject();
            }

            EditorGUILayout.Space(4f);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            DrawTitle("运行时预览 / 挂点临时控制");
            EditorGUILayout.HelpBox("本区内容属于场景挂点，不会写入 TOD Profile。", MessageType.None);
            EditorGUILayout.BeginHorizontal();
            GUIStyle playStyle = new GUIStyle(GUI.skin.button) { fontStyle = FontStyle.Bold };
            if (GUILayout.Button(advanceTime.boolValue ? "Ⅱ  暂停" : "▶  播放", playStyle, GUILayout.Width(86f), GUILayout.Height(25f)))
                advanceTime.boolValue = !advanceTime.boolValue;
            currentTime.floatValue = EditorGUILayout.Slider("当前时间", currentTime.floatValue, 0f, 24f);
            GUILayout.Label(FormatTime(currentTime.floatValue), EditorStyles.boldLabel, GUILayout.Width(48f));
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.PropertyField(hoursPerSecond, new GUIContent("每秒经过小时数"));
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(6f);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            DrawTitle("Profile 与场景引用");
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(profile, new GUIContent("当前 TOD Profile"));
            if (EditorGUI.EndChangeCheck())
            {
                serializedObject.ApplyModifiedProperties();
                RebuildProfileObject();
                serializedObject.Update();
            }

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("打开 TOD 编辑器", GUILayout.Height(25f)))
                TODWindow.Open((TODController)target);
            if (GUILayout.Button("立即应用", GUILayout.Height(25f)))
                ((TODController)target).Apply(true);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(4f);
            EditorGUILayout.PropertyField(mainLight, new GUIContent("主方向光"));
            EditorGUILayout.PropertyField(sunVisual, new GUIContent("太阳可视化挂点"));
            EditorGUILayout.PropertyField(moonVisual, new GUIContent("月亮可视化挂点"));
            EditorGUILayout.PropertyField(assignSkybox, new GUIContent("自动设置天空盒"));
            if (assignSkybox.boolValue)
                EditorGUILayout.PropertyField(skyboxMaterial, new GUIContent("天空盒材质"));
            EditorGUILayout.EndVertical();

            if (serializedObject.ApplyModifiedProperties())
            {
                ((TODController)target).Apply(true);
                SceneView.RepaintAll();
            }

            if (profileObject == null || profileObject.targetObject == null)
                return;

            EditorGUILayout.Space(8f);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            DrawTitle("TOD Profile 可调参数");
            EditorGUILayout.HelpBox("以下修改直接写入 Profile 资产，并被所有使用该 Profile 的 TOD 挂点共享。", MessageType.Info);
            TODProfileEditorGUI.DrawProfile(profileObject, null);
            EditorGUILayout.EndVertical();
        }

        private void RebuildProfileObject()
        {
            profileObject = profile != null && profile.objectReferenceValue != null
                ? new SerializedObject(profile.objectReferenceValue)
                : null;
        }

        private static void DrawTitle(string title)
        {
            Rect rect = EditorGUILayout.GetControlRect(false, 28f);
            EditorGUI.DrawRect(rect, new Color(0.11f, 0.16f, 0.22f, 0.85f));
            GUI.Label(new Rect(rect.x + 8f, rect.y, rect.width - 8f, rect.height), title,
                new GUIStyle(EditorStyles.boldLabel)
                {
                    fontSize = 14,
                    alignment = TextAnchor.MiddleLeft
                });
        }

        private static string FormatTime(float hour)
        {
            int minutes = Mathf.RoundToInt(TODProfile.WrapHour(hour) * 60f) % 1440;
            return $"{minutes / 60:00}:{minutes % 60:00}";
        }
    }
}
