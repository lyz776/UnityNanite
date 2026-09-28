# 24 小时 TOD 使用指南 / 24-Hour TOD Guide

[返回首页 / Back to README](../README.md) · [中文](#中文) · [English](#english)

![项目验证帧中的昼夜天空 / Time-of-day sky from a project validation capture](Images/tod-sky.jpg)

## 中文

### 1. 在一个场景中配置 TOD

1. 打开目标场景。首次使用时执行 **Tools → Unity Nanite → TOD → Install Project Defaults**。安装器会确认默认 `TODProfile`、动态天空材质、`TOD Rig` Prefab，并为 `Assets` 下的 URP Renderer Data 添加缺失的雾与云影 Renderer Feature。
2. 执行 **GameObject → Unity Nanite → Create 24 Hour TOD Rig**。该命令也会先运行安装器，然后在当前场景实例化 Rig，并把默认动态天空材质指定为场景 Skybox。一个场景通常保留一个 TOD Rig；如果场景原本有另一盏主方向光，请检查是否需要关闭它，以免出现两套太阳光。
3. 在 Hierarchy 选中 **TOD Rig**。Inspector 的 **当前 TOD Profile** 应指向 `Assets/TOD/Profiles/Default TOD Profile.asset`；**主方向光**、太阳/月亮挂点和 **自动设置天空盒** 应保持有效。需要使用自定义天空材质时，可关闭自动设置并自行管理场景 Skybox。
4. 在 Inspector 的 **运行时预览 / 挂点临时控制** 中拖动 **当前时间** 滑块，即可在编辑模式预览天空和光照。进入 Play Mode 后，**播放/暂停**与 **每秒经过小时数** 控制时间推进。满意后保存场景。
5. 需要不同场景或天气方案时，点击 Inspector 的 **打开 TOD 编辑器**，或使用 **Window → Unity Nanite → 24 Hour TOD**。在左栏点 **新建 Profile**，编辑中间的曲线、渐变和其他参数，然后双击该 Profile 或点 **加载所选 Profile** 将其应用到当前 Rig。仅单击左栏 Profile 只是选择，并不会加载。

### 2. 功能与调试

- Rig 驱动太阳/月亮方向、主方向光与天空盒。默认 Profile 包含动态天空、云和星空；雾与云影通过 URP Renderer Feature 工作。
- 在 Inspector 中调整 **当前时间** 检查日出、正午、黄昏和夜晚。**打开 TOD 编辑器**可检查 0–24 小时曲线与颜色渐变；更改 Profile 会影响所有引用该资产的场景。
- 若看不到天空变化，先检查 Rig 的 Profile、天空盒材质与 **自动设置天空盒**，再检查当前 Camera 使用的 URP Renderer Data 中是否有 `TODFogRendererFeature` 和 `TODCloudShadowRendererFeature`。云影与雾还需要场景深度和适当的 Profile 参数，不应仅凭没有云影判断整个 TOD 未运行。
- `贴地体积雾场景数据` 的高度图字段目前是预研接口；填写它不会自动产生完整体积雾积分。

### 3. 如何判断成功

1. 运行 **Tools → Unity Nanite → TOD → Validate Installation**，Console 应显示 `[TOD][Validation] PASS: profile, prefab, shaders, material and all URP renderer features are installed.`
2. 移动 TOD Rig 的 **当前时间**，天空颜色、太阳/月亮位置及主方向光应随之改变；进入 Play Mode 并开启时间推进后，变化应连续发生。
3. 若验证报 FAIL，Console 会列出缺失的 Profile、Prefab、材质、Renderer Feature 或 Shader。重新运行安装器，再按报错项检查场景引用和 Renderer。

详细参数见 [TOD 模块说明](../Assets/TOD/README.md)。

## English

### 1. Configure TOD in a scene

1. Open the target scene. On first use, run **Tools → Unity Nanite → TOD → Install Project Defaults**. The installer ensures the default `TODProfile`, dynamic sky material, `TOD Rig` Prefab, and missing fog and cloud-shadow Renderer Features on URP Renderer Data under `Assets`.
2. Run **GameObject → Unity Nanite → Create 24 Hour TOD Rig**. This command also runs the installer, instantiates the Rig in the active scene, and assigns the default dynamic sky material as the scene Skybox. Keep one TOD Rig per scene in a typical setup. If the scene already has a separate main directional light, check whether it should be disabled to avoid two suns.
3. Select **TOD Rig** in the Hierarchy. In the Inspector, **当前 TOD Profile** should reference `Assets/TOD/Profiles/Default TOD Profile.asset`; **主方向光**, the sun/moon anchors, and **自动设置天空盒** should be valid. Turn off automatic skybox assignment if you manage a custom Skybox yourself.
4. Drag **当前时间** (Current Time) in the Inspector's temporary Rig controls to preview sky and lighting in Edit Mode. In Play Mode, **播放/暂停** (Play/Pause) and **每秒经过小时数** (Hours Per Second) control time progression. Save the scene when satisfied.
5. For another scene or weather setup, click **打开 TOD 编辑器** in the Inspector or use **Window → Unity Nanite → 24 Hour TOD**. Choose **新建 Profile** in the left column, edit curves, gradients, and other parameters, then double-click the Profile or choose **加载所选 Profile** to apply it to the current Rig. A single click selects a Profile without loading it.

### 2. Features and troubleshooting

- The Rig drives sun/moon directions, the main directional light, and the Skybox. The default Profile includes sky, clouds, and stars; fog and cloud shadows use URP Renderer Features.
- Move **当前时间** to inspect sunrise, noon, dusk, and night. The TOD editor shows 0–24-hour curves and color gradients. Editing a Profile affects every scene that references that asset.
- If the sky does not change, check the Rig's Profile, skybox material, and **Assign Skybox**, then confirm that the camera's URP Renderer Data contains `TODFogRendererFeature` and `TODCloudShadowRendererFeature`. Cloud shadows and fog also depend on scene depth and Profile settings, so their absence alone does not mean all of TOD is inactive.
- The **Ground Fog Scene Data** height-texture fields are currently a research interface; filling them does not enable full volumetric fog integration.

### 3. Check the result

1. Run **Tools → Unity Nanite → TOD → Validate Installation**. The Console should show `[TOD][Validation] PASS: profile, prefab, shaders, material and all URP renderer features are installed.`
2. Move the Rig's **当前时间** slider. Sky color, sun/moon positions, and main-light direction should change. In Play Mode, they should continue changing when time progression is enabled.
3. On FAIL, the Console names the missing Profile, Prefab, material, Renderer Feature, or shader. Rerun the installer and inspect the reported scene reference or Renderer.

For detailed parameters, see the [TOD module documentation](../Assets/TOD/README.md).
