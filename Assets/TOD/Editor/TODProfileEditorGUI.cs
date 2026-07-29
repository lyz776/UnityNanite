using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityNanite.TOD.Editor
{
    internal readonly struct TODParameterDescriptor
    {
        public readonly string Section;
        public readonly string Path;
        public readonly string Label;
        public readonly bool HasRange;
        public readonly float Min;
        public readonly float Max;
        public readonly bool IsColor;

        public TODParameterDescriptor(string section, string path, string label, bool isColor = false)
        {
            Section = section;
            Path = path;
            Label = label;
            IsColor = isColor;
            HasRange = false;
            Min = 0f;
            Max = 0f;
        }

        public TODParameterDescriptor(string section, string path, string label, float min, float max)
        {
            Section = section;
            Path = path;
            Label = label;
            IsColor = false;
            HasRange = true;
            Min = min;
            Max = max;
        }
    }

    internal static class TODProfileEditorGUI
    {
        internal static readonly TODParameterDescriptor[] Parameters =
        {
            new TODParameterDescriptor("天空色调", "sky.lightBottom", "底部颜色", true),
            new TODParameterDescriptor("天空色调", "sky.lightMiddle", "中部颜色", true),
            new TODParameterDescriptor("天空色调", "sky.lightTop", "顶部颜色", true),
            new TODParameterDescriptor("天空色调", "sky.horizonColor", "地平线颜色", true),
            new TODParameterDescriptor("天空色调", "sky.middleHeight", "中部高度", 0f, 1f),
            new TODParameterDescriptor("天空色调", "sky.horizonWidth", "地平线宽度", 0.001f, 0.5f),
            new TODParameterDescriptor("天空色调", "sky.horizonIntensity", "地平线强度", 0f, 8f),
            new TODParameterDescriptor("天空色调", "sky.exposure", "曝光", 0f, 8f),
            new TODParameterDescriptor("天空色调", "sky.groundColor", "地面颜色", true),

            new TODParameterDescriptor("风格化", "sky.artisticTint", "整体染色", true),
            new TODParameterDescriptor("风格化", "sky.saturation", "饱和度", 0f, 3f),
            new TODParameterDescriptor("风格化", "sky.contrast", "对比度", 0f, 3f),
            new TODParameterDescriptor("风格化", "sky.horizonSunGlow", "日照地平线光", 0f, 8f),
            new TODParameterDescriptor("风格化", "sky.horizonSunGlowPower", "地平线横向聚焦", 0.1f, 32f),
            new TODParameterDescriptor("风格化", "sky.sunScatterColor", "米氏散射色", true),
            new TODParameterDescriptor("风格化", "sky.sunScatterIntensity", "米氏散射强度", 0f, 8f),
            new TODParameterDescriptor("风格化", "sky.sunScatterPower", "米氏高光核心聚焦", 0.1f, 64f),
            new TODParameterDescriptor("风格化", "sky.mieAnisotropy", "米氏各向异性 G", 0f, 0.95f),
            new TODParameterDescriptor("风格化", "sky.mieOpticalDepth", "米氏光学厚度", 0f, 8f),
            new TODParameterDescriptor("风格化", "sky.mieHorizonBoost", "地平线气溶胶增强", 0f, 8f),
            new TODParameterDescriptor("风格化", "sky.mieMoonAmount", "月光米氏散射", 0f, 2f),

            new TODParameterDescriptor("星空", "stars.color", "星星颜色", true),
            new TODParameterDescriptor("星空", "stars.intensity", "星空强度", 0f, 8f),
            new TODParameterDescriptor("星空", "stars.density", "星星稀疏度", 0.8f, 0.999f),
            new TODParameterDescriptor("星空", "stars.size", "星星尺寸", 0.02f, 0.5f),
            new TODParameterDescriptor("星空", "stars.sizeVariation", "尺寸随机变化", 0f, 1f),
            new TODParameterDescriptor("星空", "stars.brightnessVariation", "亮度随机变化", 0f, 1f),
            new TODParameterDescriptor("星空", "stars.twinkle", "闪烁幅度", 0f, 1f),
            new TODParameterDescriptor("星空", "stars.twinkleSpeed", "闪烁速度", 0f, 8f),
            new TODParameterDescriptor("星空", "stars.horizonFade", "地平线淡出", 0.001f, 0.5f),
            new TODParameterDescriptor("星空", "stars.rotation", "星空旋转"),

            new TODParameterDescriptor("云层 / 外观", "clouds.color", "云受光颜色", true),
            new TODParameterDescriptor("云层 / 外观", "clouds.shadowColor", "云背光颜色", true),
            new TODParameterDescriptor("云层 / 外观", "clouds.opacity", "不透明度", 0f, 1f),
            new TODParameterDescriptor("云层 / 外观", "clouds.coverage", "覆盖率", 0f, 1f),
            new TODParameterDescriptor("云层 / 外观", "clouds.scale", "基础云尺度", 0.05f, 20f),
            new TODParameterDescriptor("云层 / 外观", "clouds.detailScale", "细节噪声尺度", 0.5f, 12f),
            new TODParameterDescriptor("云层 / 外观", "clouds.softness", "边缘柔和", 0.001f, 0.5f),
            new TODParameterDescriptor("云层 / 外观", "clouds.erosion", "细节侵蚀", 0f, 1f),
            new TODParameterDescriptor("云层 / 外观", "clouds.distortion", "云形扭曲", 0f, 4f),

            new TODParameterDescriptor("云层 / 分布", "clouds.altitude", "高空云高度（km）", 0.1f, 50f),
            new TODParameterDescriptor("云层 / 分布", "clouds.thickness", "光学厚度", 0.01f, 12f),
            new TODParameterDescriptor("云层 / 分布", "clouds.densityMultiplier", "密度倍率", 0f, 8f),
            new TODParameterDescriptor("云层 / 分布", "clouds.horizonDensity", "地平线密度", 0f, 4f),
            new TODParameterDescriptor("云层 / 分布", "clouds.zenithDensity", "天顶密度", 0f, 4f),
            new TODParameterDescriptor("云层 / 分布", "clouds.latitudePosition", "纬度渐变位置", 0f, 1f),
            new TODParameterDescriptor("云层 / 分布", "clouds.latitudeWidth", "纬度渐变宽度", 0.001f, 1f),
            new TODParameterDescriptor("云层 / 分布", "clouds.speedX", "水平速度 X"),
            new TODParameterDescriptor("云层 / 分布", "clouds.speedY", "水平速度 Y"),
            new TODParameterDescriptor("云层 / 分布", "clouds.horizonFade", "地平线淡出", 0.001f, 0.5f),

            new TODParameterDescriptor("云层 / 光学", "clouds.scatteringCoefficient", "散射系数 RGB", true),
            new TODParameterDescriptor("云层 / 光学", "clouds.absorptionCoefficient", "吸收系数 RGB", true),
            new TODParameterDescriptor("云层 / 光学", "clouds.phaseForward", "前向相位 G", 0f, 0.95f),
            new TODParameterDescriptor("云层 / 光学", "clouds.phaseBackward", "后向相位 G", -0.9f, 0f),
            new TODParameterDescriptor("云层 / 光学", "clouds.phaseBlend", "双瓣相位混合", 0f, 1f),
            new TODParameterDescriptor("云层 / 光学", "clouds.multipleScattering", "多重散射近似", 0f, 2f),

            new TODParameterDescriptor("云层 / 光照", "clouds.sunLighting", "太阳光照", 0f, 8f),
            new TODParameterDescriptor("云层 / 光照", "clouds.moonLighting", "月亮光照", 0f, 4f),
            new TODParameterDescriptor("云层 / 光照", "clouds.ambientColor", "环境补光颜色", true),
            new TODParameterDescriptor("云层 / 光照", "clouds.ambientIntensity", "环境补光强度", 0f, 4f),
            new TODParameterDescriptor("云层 / 光照", "clouds.aerialPerspective", "空气透视", 0f, 1f),

            new TODParameterDescriptor("太阳", "sun.color", "太阳颜色", true),
            new TODParameterDescriptor("太阳", "sun.intensity", "太阳强度", 0f, 8f),
            new TODParameterDescriptor("太阳", "sun.diskSize", "圆盘尺寸", 0.0001f, 0.1f),
            new TODParameterDescriptor("太阳", "sun.diskSoftness", "圆盘柔边", 0.00001f, 0.15f),
            new TODParameterDescriptor("太阳", "sun.haloColor", "光晕颜色", true),
            new TODParameterDescriptor("太阳", "sun.haloSize", "光晕尺寸", 0.001f, 0.5f),
            new TODParameterDescriptor("太阳", "sun.haloIntensity", "光晕强度", 0f, 8f),

            new TODParameterDescriptor("月亮", "moon.color", "月亮颜色", true),
            new TODParameterDescriptor("月亮", "moon.intensity", "月亮强度", 0f, 8f),
            new TODParameterDescriptor("月亮", "moon.diskSize", "圆盘尺寸", 0.0001f, 0.1f),
            new TODParameterDescriptor("月亮", "moon.diskSoftness", "圆盘柔边", 0.00001f, 0.12f),
            new TODParameterDescriptor("月亮", "moon.haloColor", "光晕颜色", true),
            new TODParameterDescriptor("月亮", "moon.haloSize", "光晕尺寸", 0.001f, 0.5f),
            new TODParameterDescriptor("月亮", "moon.haloIntensity", "光晕强度", 0f, 8f),
            new TODParameterDescriptor("月亮", "moon.phase", "月相", 0f, 1f),
            new TODParameterDescriptor("月亮", "moon.phaseSoftness", "月相明暗柔边", 0.001f, 0.3f),
            new TODParameterDescriptor("月亮", "moon.phaseRotation", "月相旋转", -180f, 180f),
            new TODParameterDescriptor("月亮", "moon.earthshine", "地照暗部亮度", 0f, 1f),
            new TODParameterDescriptor("月亮", "moon.surfaceDetail", "月面细节", 0f, 1f),
            new TODParameterDescriptor("月亮", "moon.surfaceScale", "月面细节尺度", 1f, 24f),
            new TODParameterDescriptor("月亮", "moon.atmosphereBlend", "地平线融合", 0f, 1f),

            new TODParameterDescriptor("主方向光", "lighting.mainLightColor", "主光颜色", true),
            new TODParameterDescriptor("主方向光", "lighting.mainLightIntensity", "主光强度", 0f, 8f),
            new TODParameterDescriptor("主方向光", "lighting.shadowStrength", "阴影强度", 0f, 1f),

            new TODParameterDescriptor("线性雾", "fog.color", "雾颜色", true),
            new TODParameterDescriptor("线性雾", "fog.startDistance", "起始距离", 0f, 10000f),
            new TODParameterDescriptor("线性雾", "fog.endDistance", "结束距离", 0f, 10000f),
            new TODParameterDescriptor("线性雾", "fog.density", "浓度", 0f, 4f),

            new TODParameterDescriptor("高度雾", "fog.heightColor", "高度雾颜色", true),
            new TODParameterDescriptor("高度雾", "fog.baseHeight", "基准高度"),
            new TODParameterDescriptor("高度雾", "fog.heightRange", "高度范围", 0.01f, 2000f),
            new TODParameterDescriptor("高度雾", "fog.heightDensity", "高度雾浓度", 0f, 8f),
            new TODParameterDescriptor("高度雾", "fog.heightStartDistance", "起始距离", 0f, 10000f),
            new TODParameterDescriptor("高度雾", "fog.heightEndDistance", "结束距离", 0f, 10000f)
        };

        private static readonly Dictionary<string, bool> Foldouts = new Dictionary<string, bool>();
        private static int draggedLabelControl;
        private static float draggedLabelStartValue;
        private static float draggedLabelStartMouse;
        private static bool draggedLabelMoved;
        private static GUIStyle sectionStyle;

        public static void DrawProfile(
            SerializedObject profileObject,
            Action<TODParameterDescriptor> onSelect,
            bool compact = false)
        {
            profileObject.Update();
            string activeSection = null;

            foreach (TODParameterDescriptor descriptor in Parameters)
            {
                if (activeSection != descriptor.Section)
                {
                    if (activeSection != null)
                        EditorGUILayout.EndVertical();
                    activeSection = descriptor.Section;
                    if (!Foldouts.ContainsKey(activeSection))
                        Foldouts[activeSection] = true;
                    EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                    Rect header = EditorGUILayout.GetControlRect(false, 27f);
                    EditorGUI.DrawRect(new Rect(header.x - 3f, header.y - 2f, header.width + 6f, header.height + 2f),
                        new Color(0.12f, 0.16f, 0.21f, 0.72f));
                    Foldouts[activeSection] = EditorGUI.Foldout(
                        header, Foldouts[activeSection], activeSection, true, GetSectionStyle());
                    DrawSectionEnable(profileObject, activeSection, header);
                }

                if (!Foldouts[activeSection])
                    continue;

                SerializedProperty parameter = profileObject.FindProperty(descriptor.Path);
                if (parameter == null)
                    continue;

                if (descriptor.IsColor)
                    DrawColorParameter(parameter, descriptor, onSelect, compact);
                else
                    DrawFloatParameter(parameter, descriptor, onSelect, compact);
            }

            if (activeSection != null)
                EditorGUILayout.EndVertical();

            EditorGUILayout.Space(6f);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("程序化天体轨道（不使用关键帧）", GetSectionStyle(), GUILayout.Height(24f));
            EditorGUILayout.PropertyField(profileObject.FindProperty("orbitAzimuth"), new GUIContent("轨道方位角"));
            EditorGUILayout.PropertyField(profileObject.FindProperty("orbitTilt"), new GUIContent("轨道倾角"));
            EditorGUILayout.PropertyField(profileObject.FindProperty("solarNoon"), new GUIContent("太阳正午"));
            EditorGUILayout.PropertyField(profileObject.FindProperty("moonOrbitOffset"), new GUIContent("月亮轨道偏移"));
            EditorGUILayout.PropertyField(profileObject.FindProperty("gizmoRadius"), new GUIContent("Gizmo 半径"));
            EditorGUILayout.PropertyField(profileObject.FindProperty("twilightWidth"), new GUIContent("晨昏过渡宽度"));
            EditorGUILayout.EndVertical();

            if (profileObject.ApplyModifiedProperties())
            {
                TODController.RefreshAll((TODProfile)profileObject.targetObject);
                SceneView.RepaintAll();
            }
        }

        private static void DrawFloatParameter(
            SerializedProperty parameter,
            TODParameterDescriptor descriptor,
            Action<TODParameterDescriptor> onSelect,
            bool compact)
        {
            SerializedProperty useCurve = parameter.FindPropertyRelative("useCurve");
            SerializedProperty constant = parameter.FindPropertyRelative("constant");
            SerializedProperty curve = parameter.FindPropertyRelative("curve");

            Rect row = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight);
            float labelWidth = compact ? 108f : 145f;
            Rect labelRect = new Rect(row.x, row.y, labelWidth, row.height);
            Rect modeRect = new Rect(labelRect.xMax + 2f, row.y, 54f, row.height);
            Rect valueRect = new Rect(modeRect.xMax + 4f, row.y, row.xMax - modeRect.xMax - 4f, row.height);
            DrawDraggableLabel(labelRect, descriptor, onSelect, useCurve.boolValue ? null : constant);
            useCurve.boolValue = GUI.Toggle(
                modeRect, useCurve.boolValue, useCurve.boolValue ? "曲线" : "数值", EditorStyles.miniButton);

            if (useCurve.boolValue)
            {
                EditorGUI.PropertyField(valueRect, curve, GUIContent.none);
            }
            else if (descriptor.HasRange)
            {
                constant.floatValue = EditorGUI.Slider(
                    valueRect, GUIContent.none, constant.floatValue, descriptor.Min, descriptor.Max);
            }
            else
            {
                constant.floatValue = EditorGUI.FloatField(valueRect, constant.floatValue);
            }
        }

        private static void DrawColorParameter(
            SerializedProperty parameter,
            TODParameterDescriptor descriptor,
            Action<TODParameterDescriptor> onSelect,
            bool compact)
        {
            SerializedProperty useGradient = parameter.FindPropertyRelative("useGradient");
            SerializedProperty color = parameter.FindPropertyRelative("color");
            SerializedProperty gradient = parameter.FindPropertyRelative("gradient");

            Rect row = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight);
            float labelWidth = compact ? 108f : 145f;
            Rect labelRect = new Rect(row.x, row.y, labelWidth, row.height);
            Rect modeRect = new Rect(labelRect.xMax + 2f, row.y, 68f, row.height);
            Rect valueRect = new Rect(modeRect.xMax + 4f, row.y, row.xMax - modeRect.xMax - 4f, row.height);
            DrawDraggableLabel(labelRect, descriptor, onSelect, null);
            useGradient.boolValue = GUI.Toggle(
                modeRect, useGradient.boolValue, useGradient.boolValue ? "渐变" : "颜色", EditorStyles.miniButton);
            EditorGUI.PropertyField(valueRect, useGradient.boolValue ? gradient : color, GUIContent.none);
        }

        private static void DrawDraggableLabel(
            Rect rect,
            TODParameterDescriptor descriptor,
            Action<TODParameterDescriptor> onSelect,
            SerializedProperty value)
        {
            EditorGUI.LabelField(rect, descriptor.Label);
            EditorGUIUtility.AddCursorRect(rect, value != null ? MouseCursor.SlideArrow : MouseCursor.Link);
            int id = GUIUtility.GetControlID((descriptor.Path + ".drag").GetHashCode(), FocusType.Passive, rect);
            Event current = Event.current;
            if (current.type == EventType.MouseDown && current.button == 0 && rect.Contains(current.mousePosition))
            {
                GUIUtility.hotControl = id;
                draggedLabelControl = id;
                draggedLabelStartMouse = current.mousePosition.x;
                draggedLabelStartValue = value != null ? value.floatValue : 0f;
                draggedLabelMoved = false;
                current.Use();
            }
            else if (current.type == EventType.MouseDrag && GUIUtility.hotControl == id && value != null)
            {
                float pixels = current.mousePosition.x - draggedLabelStartMouse;
                float sensitivity = descriptor.HasRange
                    ? (descriptor.Max - descriptor.Min) / 240f
                    : Mathf.Max(1f, Mathf.Abs(draggedLabelStartValue)) * 0.01f;
                if (current.shift)
                    sensitivity *= 0.1f;
                float result = draggedLabelStartValue + pixels * sensitivity;
                value.floatValue = descriptor.HasRange
                    ? Mathf.Clamp(result, descriptor.Min, descriptor.Max)
                    : result;
                draggedLabelMoved |= Mathf.Abs(pixels) > 1f;
                GUI.changed = true;
                current.Use();
            }
            else if (current.type == EventType.MouseUp && GUIUtility.hotControl == id)
            {
                GUIUtility.hotControl = 0;
                draggedLabelControl = 0;
                if (!draggedLabelMoved)
                    onSelect?.Invoke(descriptor);
                current.Use();
            }
        }

        private static GUIStyle GetSectionStyle()
        {
            if (sectionStyle != null)
                return sectionStyle;
            sectionStyle = new GUIStyle(EditorStyles.foldoutHeader)
            {
                fontSize = 13,
                fontStyle = FontStyle.Bold,
                fixedHeight = 0f
            };
            return sectionStyle;
        }

        private static void DrawSectionEnable(SerializedObject profileObject, string section, Rect header)
        {
            string path = section == "星空" ? "stars.enabled" :
                section == "云层 / 外观" ? "clouds.enabled" :
                section == "线性雾" ? "fog.enabled" :
                section == "高度雾" ? "fog.heightFogEnabled" : null;
            if (path == null)
                return;
            SerializedProperty enabled = profileObject.FindProperty(path);
            Rect toggleRect = new Rect(header.xMax - 48f, header.y + 3f, 46f, header.height - 6f);
            enabled.boolValue = GUI.Toggle(toggleRect, enabled.boolValue, "启用", EditorStyles.miniButton);
        }

        public static TODParameterDescriptor? FindDescriptor(string path)
        {
            foreach (TODParameterDescriptor descriptor in Parameters)
            {
                if (descriptor.Path == path)
                    return descriptor;
            }
            return null;
        }

        public static object ResolveObject(object root, string path)
        {
            object current = root;
            string[] members = path.Split('.');
            foreach (string member in members)
            {
                if (current == null)
                    return null;
                var field = current.GetType().GetField(member);
                if (field == null)
                    return null;
                current = field.GetValue(current);
            }
            return current;
        }
    }
}
