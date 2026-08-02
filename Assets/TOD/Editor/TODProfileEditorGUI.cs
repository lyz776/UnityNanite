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
            new TODParameterDescriptor("风格化", "sky.sunWashColor", "太阳宽域染色色", true),
            new TODParameterDescriptor("风格化", "sky.sunWashIntensity", "太阳宽域染色强度", 0f, 4f),
            new TODParameterDescriptor("风格化", "sky.sunWashPower", "太阳宽域聚焦", 0.1f, 16f),
            new TODParameterDescriptor("风格化", "sky.sunWashHorizonWeight", "染色地平线权重", 0f, 1f),

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

            new TODParameterDescriptor("高空云 / 颜色", "clouds.color", "散射整体乘色", true),
            // Legacy overall dark color and directional weight are retained in
            // serialization but hidden here: the four directional colors are
            // now the single authoritative artistic palette.
            new TODParameterDescriptor("高空云 / 颜色", "clouds.frontLitColor", "Front Lit 正面亮色", true),
            new TODParameterDescriptor("高空云 / 颜色", "clouds.frontDarkColor", "Front Dark 正面暗色", true),
            new TODParameterDescriptor("高空云 / 颜色", "clouds.backLitColor", "Back Lit 背光亮色", true),
            new TODParameterDescriptor("高空云 / 颜色", "clouds.backDarkColor", "Back Dark 背光暗色", true),
            new TODParameterDescriptor("高空云 / 颜色", "clouds.rimColor", "Rim 边缘光颜色", true),
            new TODParameterDescriptor("高空云 / 颜色", "clouds.rimIntensity", "Rim 强度", 0f, 8f),
            new TODParameterDescriptor("高空云 / 颜色", "clouds.rimWidth", "Rim 受光偏移", 0.001f, 0.1f),

            new TODParameterDescriptor("高空云 / 形态", "clouds.opacity", "不透明度", 0f, 1f),
            new TODParameterDescriptor("高空云 / 形态", "clouds.coverage", "覆盖率", 0f, 1f),
            new TODParameterDescriptor("高空云 / 形态", "clouds.scale", "基础云尺度", 0.05f, 20f),
            new TODParameterDescriptor("高空云 / 形态", "clouds.detailScale", "细节噪声尺度", 0.5f, 12f),
            new TODParameterDescriptor("高空云 / 形态", "clouds.softness", "边缘柔和", 0.001f, 0.5f),
            new TODParameterDescriptor("高空云 / 形态", "clouds.erosion", "细节侵蚀", 0f, 1f),
            new TODParameterDescriptor("高空云 / 形态", "clouds.distortion", "云形扭曲", 0f, 4f),

            new TODParameterDescriptor("高空云 / 分布", "clouds.altitude", "高空云高度（km）", 0.1f, 50f),
            new TODParameterDescriptor("高空云 / 分布", "clouds.thickness", "光学厚度", 0.01f, 12f),
            new TODParameterDescriptor("高空云 / 分布", "clouds.densityMultiplier", "密度倍率", 0f, 8f),
            new TODParameterDescriptor("高空云 / 分布", "clouds.horizonDensity", "地平线密度", 0f, 4f),
            new TODParameterDescriptor("高空云 / 分布", "clouds.zenithDensity", "天顶密度", 0f, 4f),
            new TODParameterDescriptor("高空云 / 分布", "clouds.latitudePosition", "纬度渐变位置", 0f, 1f),
            new TODParameterDescriptor("高空云 / 分布", "clouds.latitudeWidth", "纬度渐变宽度", 0.001f, 1f),
            new TODParameterDescriptor("高空云 / 分布", "clouds.speedX", "水平速度 X"),
            new TODParameterDescriptor("高空云 / 分布", "clouds.speedY", "水平速度 Y"),
            new TODParameterDescriptor("高空云 / 分布", "clouds.horizonFade", "地平线淡出", 0.001f, 0.5f),

            new TODParameterDescriptor("高空云 / 光照", "clouds.sunLighting", "太阳光照", 0f, 8f),
            new TODParameterDescriptor("高空云 / 光照", "clouds.moonLighting", "月亮光照", 0f, 4f),
            new TODParameterDescriptor("高空云 / 光照", "clouds.ambientColor", "环境补光颜色", true),
            new TODParameterDescriptor("高空云 / 光照", "clouds.ambientIntensity", "环境补光强度", 0f, 4f),
            new TODParameterDescriptor("高空云 / 光照", "clouds.aerialPerspective", "空气透视", 0f, 1f),
            new TODParameterDescriptor("高空云 / 光照", "clouds.lightWrap", "包裹光", 0f, 1f),
            new TODParameterDescriptor("高空云 / 光照", "clouds.selfShadowStrength", "云体自遮挡强度", 0f, 8f),
            new TODParameterDescriptor("高空云 / 光照", "clouds.selfShadowDistance", "自遮挡采样距离", 0f, 2f),
            new TODParameterDescriptor("高空云 / 光照", "clouds.stylization", "分段光照权重", 0f, 1f),
            new TODParameterDescriptor("高空云 / 光照", "clouds.lightSteps", "明暗色阶数", 1f, 8f),
            new TODParameterDescriptor("高空云 / 光照", "clouds.lightStepSoftness", "色阶过渡柔度", 0.001f, 0.49f),
            new TODParameterDescriptor("高空云 / 光照", "clouds.sunTransmission", "太阳透光强度", 0f, 8f),
            new TODParameterDescriptor("高空云 / 光照", "clouds.sunTransmissionPower", "透光方向聚焦", 0.1f, 16f),
            new TODParameterDescriptor("高空云 / 光照", "clouds.undersideStrength", "云底暗面强度", 0f, 1f),

            new TODParameterDescriptor("高空云 / 光学", "clouds.scatteringCoefficient", "散射系数 RGB", true),
            new TODParameterDescriptor("高空云 / 光学", "clouds.absorptionCoefficient", "吸收系数 RGB", true),
            new TODParameterDescriptor("高空云 / 光学", "clouds.phaseForward", "前向相位 G", 0f, 0.95f),
            new TODParameterDescriptor("高空云 / 光学", "clouds.phaseBackward", "后向相位 G", -0.9f, 0f),
            new TODParameterDescriptor("高空云 / 光学", "clouds.phaseBlend", "双瓣相位混合", 0f, 1f),
            new TODParameterDescriptor("高空云 / 光学", "clouds.multipleScattering", "多重散射近似", 0f, 2f),

            new TODParameterDescriptor("高空云 / 第二层", "clouds.layer2.opacity", "第二层混合强度", 0f, 1f),
            new TODParameterDescriptor("高空云 / 第二层", "clouds.layer2.coverageOffset", "覆盖率偏移", -1f, 1f),
            new TODParameterDescriptor("高空云 / 第二层", "clouds.layer2.altitude", "第二层高度（km）", 0.1f, 50f),
            new TODParameterDescriptor("高空云 / 第二层", "clouds.layer2.scale", "第二层尺度", 0.01f, 8f),
            new TODParameterDescriptor("高空云 / 第二层", "clouds.layer2.speedX", "第二层速度 X"),
            new TODParameterDescriptor("高空云 / 第二层", "clouds.layer2.speedY", "第二层速度 Y"),
            new TODParameterDescriptor("高空云 / 第二层", "clouds.layer2.fogBlend", "远景雾化", 0f, 1f),

            new TODParameterDescriptor("高空云 / 闪电", "clouds.lightning.color", "闪电云内发光颜色", true),
            new TODParameterDescriptor("高空云 / 闪电", "clouds.lightning.intensity", "闪电强度", 0f, 32f),
            new TODParameterDescriptor("高空云 / 闪电", "clouds.lightning.frequency", "每分钟闪电次数", 0.001f, 12f),
            new TODParameterDescriptor("高空云 / 闪电", "clouds.lightning.duration", "单次持续时间", 0.02f, 3f),
            new TODParameterDescriptor("高空云 / 闪电", "clouds.lightning.scale", "闪电分布尺度", 0.01f, 8f),
            new TODParameterDescriptor("高空云 / 闪电", "clouds.lightning.glowSpeed", "发光纹理流速", -1f, 1f),

            new TODParameterDescriptor("高空云 / 云阴影", "clouds.shadows.color", "阴影染色", true),
            new TODParameterDescriptor("高空云 / 云阴影", "clouds.shadows.scale", "世界空间尺度", 0.00001f, 0.02f),
            new TODParameterDescriptor("高空云 / 云阴影", "clouds.shadows.sunnyStrength", "晴天阴影强度", 0f, 1f),
            new TODParameterDescriptor("高空云 / 云阴影", "clouds.shadows.overcastStrength", "阴天阴影强度", 0f, 1f),
            new TODParameterDescriptor("高空云 / 云阴影", "clouds.shadows.softness", "阴影柔和", 0.001f, 0.5f),
            new TODParameterDescriptor("高空云 / 云阴影", "clouds.shadows.maxDistance", "最大作用距离", 1f, 10000f),

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

            new TODParameterDescriptor("Lens Flare", "lensFlare.sunIntensity", "太阳 Flare 强度", 0f, 8f),
            new TODParameterDescriptor("Lens Flare", "lensFlare.sunScale", "太阳 Flare 尺寸", 0f, 4f),
            new TODParameterDescriptor("Lens Flare", "lensFlare.moonIntensity", "月亮 Flare 强度", 0f, 4f),
            new TODParameterDescriptor("Lens Flare", "lensFlare.moonScale", "月亮 Flare 尺寸", 0f, 4f),
            new TODParameterDescriptor("Lens Flare", "lensFlare.occlusionRadius", "遮挡采样半径", 0f, 2f),
            new TODParameterDescriptor("Lens Flare", "lensFlare.occlusionSamples", "遮挡采样数", 1f, 64f),
            new TODParameterDescriptor("Lens Flare", "lensFlare.maxAttenuationDistance", "最大衰减距离", 1f, 50000f),

            new TODParameterDescriptor("雾 / 常规距离雾", "fog.topColor", "顶部颜色", true),
            new TODParameterDescriptor("雾 / 常规距离雾", "fog.topIntensity", "顶部颜色强度", 0f, 8f),
            new TODParameterDescriptor("雾 / 常规距离雾", "fog.bottomColor", "底部颜色", true),
            new TODParameterDescriptor("雾 / 常规距离雾", "fog.bottomIntensity", "底部颜色强度", 0f, 8f),
            new TODParameterDescriptor("雾 / 常规距离雾", "fog.skyIntensity", "天空参与强度", 0f, 1f),
            new TODParameterDescriptor("雾 / 常规距离雾", "fog.power", "雾曲线 Power", 0.1f, 8f),
            new TODParameterDescriptor("雾 / 常规距离雾", "fog.exponentialBlend", "指数雾混合", 0f, 1f),
            new TODParameterDescriptor("雾 / 常规距离雾", "fog.startDistance", "雾起始距离", 0f, 10000f),
            new TODParameterDescriptor("雾 / 常规距离雾", "fog.endDistance", "雾结束距离", 0f, 10000f),
            new TODParameterDescriptor("雾 / 常规距离雾", "fog.density", "雾浓度", 0f, 4f),
            new TODParameterDescriptor("雾 / 常规距离雾", "fog.baseHeight", "雾高度"),
            new TODParameterDescriptor("雾 / 常规距离雾", "fog.heightRange", "高度范围", 0.01f, 2000f),

            new TODParameterDescriptor("雾 / 屏幕空间散射", "fog.screenSpace.intensity", "柔化强度", 0f, 1f),
            new TODParameterDescriptor("雾 / 屏幕空间散射", "fog.screenSpace.radius", "采样半径（像素）", 0f, 8f),
            new TODParameterDescriptor("雾 / 屏幕空间散射", "fog.screenSpace.startDistance", "柔化起始距离", 0f, 10000f),
            new TODParameterDescriptor("雾 / 屏幕空间散射", "fog.screenSpace.endDistance", "柔化结束距离", 0f, 10000f),
            new TODParameterDescriptor("雾 / 屏幕空间散射", "fog.screenSpace.depthThreshold", "深度边缘阈值", 0.0001f, 0.2f),
            new TODParameterDescriptor("雾 / 屏幕空间散射", "fog.screenSpace.skyContribution", "天空柔化参与", 0f, 1f),

            new TODParameterDescriptor("雾 / 贴地体积雾（预研）", "fog.groundVolume.albedo", "介质反照率", true),
            new TODParameterDescriptor("雾 / 贴地体积雾（预研）", "fog.groundVolume.density", "消光密度", 0f, 1f),
            new TODParameterDescriptor("雾 / 贴地体积雾（预研）", "fog.groundVolume.fogHeight", "离地高度", -100f, 500f),
            new TODParameterDescriptor("雾 / 贴地体积雾（预研）", "fog.groundVolume.heightRange", "雾层厚度", 0.1f, 500f),
            new TODParameterDescriptor("雾 / 贴地体积雾（预研）", "fog.groundVolume.maxDistance", "最大追踪距离", 1f, 5000f),
            new TODParameterDescriptor("雾 / 贴地体积雾（预研）", "fog.groundVolume.terrainConformity", "贴地形程度", 0f, 1f),
            new TODParameterDescriptor("雾 / 贴地体积雾（预研）", "fog.groundVolume.noise2DScale", "2D 天气噪声尺度", 0.00001f, 1f),
            new TODParameterDescriptor("雾 / 贴地体积雾（预研）", "fog.groundVolume.noise3DScale", "3D 形态噪声尺度", 0.00001f, 1f),
            new TODParameterDescriptor("雾 / 贴地体积雾（预研）", "fog.groundVolume.erosion", "噪声侵蚀", 0f, 1f),
            new TODParameterDescriptor("雾 / 贴地体积雾（预研）", "fog.groundVolume.windX", "流动速度 X"),
            new TODParameterDescriptor("雾 / 贴地体积雾（预研）", "fog.groundVolume.windZ", "流动速度 Z"),
            new TODParameterDescriptor("雾 / 贴地体积雾（预研）", "fog.groundVolume.anisotropy", "相位函数 G", -0.9f, 0.9f),
            new TODParameterDescriptor("雾 / 贴地体积雾（预研）", "fog.groundVolume.shadowStrength", "体积阴影强度", 0f, 1f),
            new TODParameterDescriptor("雾 / 贴地体积雾（预研）", "fog.groundVolume.shaftIntensity", "丁达尔光强度", 0f, 8f),
            new TODParameterDescriptor("雾 / 贴地体积雾（预研）", "fog.groundVolume.stepCount", "视线步进数", 8f, 128f)
        };

        private static readonly Dictionary<string, bool> Foldouts = new Dictionary<string, bool>();
        private static int draggedLabelControl;
        private static float draggedLabelStartValue;
        private static float draggedLabelStartMouse;
        private static bool draggedLabelMoved;
        private static GUIStyle sectionStyle;
        private static GUIStyle subsectionStyle;

        public static void DrawProfile(
            SerializedObject profileObject,
            Action<TODParameterDescriptor> onSelect,
            bool compact = false)
        {
            profileObject.Update();
            string activeSection = null;
            bool highCloudDrawn = false;
            bool fogDrawn = false;

            foreach (TODParameterDescriptor descriptor in Parameters)
            {
                if (descriptor.Section.StartsWith("高空云 / ", StringComparison.Ordinal))
                {
                    if (!highCloudDrawn)
                    {
                        if (activeSection != null)
                        {
                            EditorGUILayout.EndVertical();
                            activeSection = null;
                        }
                        DrawNestedGroup(profileObject, onSelect, compact, "高空云");
                        highCloudDrawn = true;
                    }
                    continue;
                }

                if (descriptor.Section.StartsWith("雾 / ", StringComparison.Ordinal))
                {
                    if (!fogDrawn)
                    {
                        if (activeSection != null)
                        {
                            EditorGUILayout.EndVertical();
                            activeSection = null;
                        }
                        DrawNestedGroup(profileObject, onSelect, compact, "雾");
                        fogDrawn = true;
                    }
                    continue;
                }

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
                    if (Foldouts[activeSection])
                        DrawSectionOptions(profileObject, activeSection);
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

        private static void DrawNestedGroup(
            SerializedObject profileObject,
            Action<TODParameterDescriptor> onSelect,
            bool compact,
            string group)
        {
            if (!Foldouts.ContainsKey(group))
                Foldouts[group] = true;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            Rect header = EditorGUILayout.GetControlRect(false, 30f);
            EditorGUI.DrawRect(
                new Rect(header.x - 3f, header.y - 2f, header.width + 6f, header.height + 2f),
                new Color(0.09f, 0.16f, 0.22f, 0.92f));
            Foldouts[group] = EditorGUI.Foldout(
                header, Foldouts[group], group, true, GetSectionStyle());
            DrawSectionEnable(profileObject, group, header);

            if (Foldouts[group])
            {
                string activeSubsection = null;
                foreach (TODParameterDescriptor descriptor in Parameters)
                {
                    if (!descriptor.Section.StartsWith(group + " / ", StringComparison.Ordinal))
                        continue;

                    if (activeSubsection != descriptor.Section)
                    {
                        if (activeSubsection != null)
                            EditorGUILayout.EndVertical();
                        activeSubsection = descriptor.Section;
                        if (!Foldouts.ContainsKey(activeSubsection))
                            Foldouts[activeSubsection] = true;

                        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                        Rect subHeader = EditorGUILayout.GetControlRect(false, 24f);
                        string title = activeSubsection.Substring((group + " / ").Length);
                        Foldouts[activeSubsection] = EditorGUI.Foldout(
                            subHeader,
                            Foldouts[activeSubsection],
                            title,
                            true,
                            GetSubsectionStyle());
                        DrawSectionEnable(profileObject, activeSubsection, subHeader);
                        if (Foldouts[activeSubsection])
                            DrawSectionOptions(profileObject, activeSubsection);
                    }

                    if (!Foldouts[activeSubsection])
                        continue;

                    SerializedProperty parameter = profileObject.FindProperty(descriptor.Path);
                    if (parameter == null)
                        continue;
                    if (descriptor.IsColor)
                        DrawColorParameter(parameter, descriptor, onSelect, compact);
                    else
                        DrawFloatParameter(parameter, descriptor, onSelect, compact);
                }

                if (activeSubsection != null)
                    EditorGUILayout.EndVertical();
            }

            EditorGUILayout.EndVertical();
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

        private static GUIStyle GetSubsectionStyle()
        {
            if (subsectionStyle != null)
                return subsectionStyle;
            subsectionStyle = new GUIStyle(EditorStyles.foldoutHeader)
            {
                fontSize = 12,
                fontStyle = FontStyle.Bold,
                fixedHeight = 0f
            };
            return subsectionStyle;
        }

        private static void DrawSectionEnable(SerializedObject profileObject, string section, Rect header)
        {
            string path = section == "星空" ? "stars.enabled" :
                section == "高空云" ? "clouds.enabled" :
                section == "高空云 / 第二层" ? "clouds.layer2.enabled" :
                section == "高空云 / 闪电" ? "clouds.lightning.enabled" :
                section == "高空云 / 云阴影" ? "clouds.shadows.enabled" :
                section == "Lens Flare" ? "lensFlare.enabled" :
                section == "雾" ? "fog.enabled" :
                section == "雾 / 屏幕空间散射" ? "fog.screenSpace.enabled" :
                section == "雾 / 贴地体积雾（预研）" ? "fog.groundVolume.enabled" : null;
            if (path == null)
                return;
            SerializedProperty enabled = profileObject.FindProperty(path);
            Rect toggleRect = new Rect(header.xMax - 48f, header.y + 3f, 46f, header.height - 6f);
            enabled.boolValue = GUI.Toggle(toggleRect, enabled.boolValue, "启用", EditorStyles.miniButton);
        }

        private static void DrawSectionOptions(SerializedObject profileObject, string section)
        {
            if (section == "高空云 / 形态")
            {
                EditorGUILayout.PropertyField(
                    profileObject.FindProperty("clouds.shapeTexture"),
                    new GUIContent("主形状贴图"));
                EditorGUILayout.PropertyField(
                    profileObject.FindProperty("clouds.unevenTexture"),
                    new GUIContent("细节 / 不均匀贴图"));
                return;
            }

            if (section == "高空云 / 闪电")
            {
                EditorGUILayout.PropertyField(
                    profileObject.FindProperty("clouds.lightning.glowTexture"),
                    new GUIContent("闪电位置 / 发光贴图"));
                return;
            }

            if (section != "Lens Flare")
                return;

            EditorGUILayout.PropertyField(
                profileObject.FindProperty("lensFlare.useOcclusion"),
                new GUIContent("深度遮挡"));
            EditorGUILayout.PropertyField(
                profileObject.FindProperty("lensFlare.environmentOcclusion"),
                new GUIContent("环境效果遮挡"));
            EditorGUILayout.PropertyField(
                profileObject.FindProperty("lensFlare.allowOffScreen"),
                new GUIContent("允许屏幕外产生光斑"));
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
