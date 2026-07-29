# Unity Nanite：24 小时 TOD

`Assets/TOD` 是一个独立的 URP 昼夜循环模块，不依赖
`Assets/Scripts/Nanite` 下的 GPU Driven/Nanite 实现。

## 快速开始

1. 首次使用执行 **Tools > Unity Nanite > TOD > Install Project Defaults**。
2. 执行 **GameObject > Unity Nanite > Create 24 Hour TOD Rig** 创建挂点。
3. 选中 TOD Rig，在 Inspector 的“运行时预览 / 挂点临时控制”中调节时间、
   播放状态和流速。
4. 点击“打开 TOD 编辑器”，或执行
   **Window > Unity Nanite > 24 Hour TOD** 打开三栏编辑窗口。

安装器会创建或更新：

- `Assets/TOD/Profiles/Default TOD Profile.asset`
- `Assets/TOD/Generated/TOD Dynamic Sky.mat`
- `Assets/TOD/Prefabs/TOD Rig.prefab`
- `Assets` 内每个 URP Renderer Data 上的 `TODFogRendererFeature`

TOD Rig 会自动把默认动态天空材质设为当前场景的 Skybox；也可以在挂点上关闭
“自动指定天空盒”并使用自定义材质。

## Profile 与编辑窗口

- 左栏单击 Profile 只进行选择；双击会立即加载到当前 TOD 挂点。
- 加载新 Profile 时，中间参数、节点选择和滚动状态都会刷新，避免继续显示旧数据。
- 新建 Profile 默认保存到 `Assets/TOD/Profiles`。
- 中间曲线面板使用 0–24 小时时间轴，节点直接显示时间和值。
- 双击节点跳转到节点时间；拖动节点可同时修改时间和值。
- 按住 Shift 拖动会锁定先判定出的水平或垂直方向；拖动中按 Esc 可撤销本次拖动。
- Gradient 使用 Unity 原生 HDR Gradient 编辑器，并提供 0–24 小时色键编辑带。
- 所有编辑均通过 Unity Undo 入栈，可使用 Ctrl+Z / Ctrl+Y。

## 数据规则

- 每个可调 Float 均可选择“曲线”或“数值”。
- 每个可调颜色均可选择“渐变”或“颜色”，并使用 HDR 颜色空间。
- 曲线键时间直接以小时（0–24）保存。
- Gradient 仍按 Unity 的 0–1 规则保存，编辑器负责显示为 0–24 小时。
- 曲线和 Gradient 键直接序列化在 ScriptableObject 中，不生成 Ramp 贴图，也不需要
  运行时磁盘读写。

## 天空表现

动态天空 Shader 包含：

- 天空底部、中部、顶部、地平线和地面颜色；
- 太阳/月亮本体、柔边、光晕与月相；
- 面向太阳的地平线辉光和方向性散射；
- 美术色调、饱和度、对比度和曝光；
- 程序化星空、密度、大小、闪烁、旋转与地平线淡出；
- 程序化双层噪声云、覆盖率、软硬度、速度、日照染色与地平线淡出。

这些项主要吸收了 AC 天空中“方向性太阳散射、程序化星空、噪声云、地平线淡出和
颜色校正”的表现思路，但 TOD 的 Profile、编辑器和 Shader 均为本模块独立实现。

## 雾与全局参数

控制器每次应用 Profile 时会写入：

- `_TODTime`、`_TODTime01`、`_TODDayOrNight`
- `_TODMainLightDir`、`_TODSunDir`、`_TODMoonDir`
- `_TODLightBottom`、`_TODLightMiddle`、`_TODLightTop`、`_TODHorizonColor`
- 太阳、月亮、环境光、星空、云层、线性雾和高度雾的 `_TOD*` 参数

`_TODMainLightDir` 指向当前主天体光源，`_TODDayOrNight` 为由太阳高度平滑计算的
0（夜）到 1（日）。

全屏雾 Pass 插入在 `BeforeRenderingTransparents`。它先对不透明场景与天空进行统一
雾化；需要逐像素正确雾化的透明材质，应自行采样同一组 `_TODFog*` 全局参数，以免
透明层被重复雾化。

## 当前边界

高度雾是基于深度重建的轻量解析效果，不是 Froxel 体积散射系统。它支持基础高度、
高度范围、密度以及起止距离。当前程序化云是适合风格化远景的 Skybox 云层，不包含
体积云自阴影、天气图或局部云体。
