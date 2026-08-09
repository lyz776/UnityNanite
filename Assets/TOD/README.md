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
- 双击节点跳转到节点时间；拖动节点只修改节点的时间和值，不会带动当前预览时间。
- 按住 Shift 拖动会锁定先判定出的水平或垂直方向；拖动中按 Esc 可撤销本次拖动。
- Gradient 使用 Unity 原生 HDR Gradient 编辑器，并提供 0–24 小时色键编辑带。
- Profile 参数编辑均通过 Unity Undo 入栈，可使用 Ctrl+Z / Ctrl+Y；时间轴拖动和
  双击节点跳时属于预览操作，不进入 Undo。

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
- 太阳/月亮本体、宽范围柔边、光晕、可旋转柔边月相、地照暗面和程序化月面细节；
- 带各向异性、光学厚度与地平线空气质量增强的米氏前向散射；
- 美术色调、饱和度、对比度和曝光；
- 程序化星空、密度、随机大小和亮度、闪烁、旋转与地平线淡出；
- 程序化 2D 高空云，支持云高、光学厚度、纬度分布、双层噪声侵蚀、RGB
  散射/吸收系数、太阳/月亮双光源、双瓣相位函数、环境补光与空气透视；
- 高空云在 Inspector 和 TOD 窗口中作为一级目录，内部按颜色、形态、分布、光照、
  光学和云阴影划分二级目录；
- 云颜色支持 Front Lit、Front Dark、Back Lit、Back Dark 四向染色与可调 Rim，
  这些美术颜色作用在物理散射亮度之后，不替代透射率与能量积分；
- 高空云形态可直接使用主形状与不均匀度贴图；默认接入 StylizedWeather 导出的
  `T_Noise001` / `T_Noise002`。Rim 使用太阳方向偏移遮罩差，云和边缘光共享球面 UV，
  地平线不会因平面投影除法而拉伸；
- 风格化云光照支持宽包裹光、低频云体自遮挡、可柔化的明暗色阶、冷色云底以及
  朝太阳方向的暖色透光；天空另有宽范围太阳染色区，使日出和逆光云不只依赖小光晕；
- 云阴影通过独立 URP Renderer Feature 投射到场景不透明物体，支持阴影染色、
  世界空间尺度、晴天/阴天强度、柔和度和最大作用距离。

这些项主要吸收了公开大气/云渲染资料以及美术参考中“方向性太阳散射、程序化星空、
噪声云、地平线淡出和颜色校正”的表现思路，但 TOD 的 Profile、编辑器和 Shader
均为本模块独立实现。

## 雾与全局参数

Profile 中的“雾”现在是一级目录，内部包含：

- “常规距离雾”：上下分色、天空参与量、Power、线性 / 指数混合、距离与高度衰减；
- “屏幕空间散射”：按距离淡入的深度感知柔化，默认关闭；
- “贴地体积雾（预研）”：完整 Profile 参数与地形高度图入口，当前尚未执行体积积分。

Lens Flare 使用太阳和月亮挂点上的 Unity URP `LensFlareComponentSRP`。Profile 可按时间
控制两者强度、尺寸和遮挡采样；光斑、鬼影与彩虹环的具体形状由挂点上的
`LensFlareDataSRP` 资产决定。

完整设计、性能分级和贴地雾实现顺序见
`Assets/TOD/Docs/FOG_AND_LENS_FLARE_PLAN.md`。

控制器每次应用 Profile 时会写入：

- `_TODTime`、`_TODTime01`、`_TODDayOrNight`
- `_TODMainLightDir`、`_TODSunDir`、`_TODMoonDir`
- `_TODLightBottom`、`_TODLightMiddle`、`_TODLightTop`、`_TODHorizonColor`
- 太阳、月亮、主方向光、星空、云层、Lens Flare 与三类雾的 `_TOD*` 参数

`_TODMainLightDir` 指向当前主天体光源，`_TODDayOrNight` 为由太阳高度平滑计算的
0（夜）到 1（日）。

全屏雾 Pass 插入在 `BeforeRenderingTransparents`。它先对不透明场景与天空进行统一
雾化；需要逐像素正确雾化的透明材质，应自行采样同一组 `_TODFog*` 全局参数，以免
透明层被重复雾化。

云阴影 Pass 插入在 `AfterRenderingOpaques`，使用相机深度重建世界坐标，再沿太阳方向
投影到高空云层采样作者云形贴图。它只在像素仍能收到 URP 主光直射时生效，已经处于
主光阴影的区域不会二次变暗；水平面与 X/Z 朝向陡峭表面使用稳定的主导面投影。安装器会同时确保
`TODCloudShadowRendererFeature` 和 `TODFogRendererFeature` 存在于项目的 URP
Renderer Data。

独立时间测试面板位于 `Window/Unity Nanite/TOD Time Control`，也可从 TOD Controller
Inspector 或完整 TOD 编辑器顶部打开。该面板的时间跳转不进入 Undo 栈。

## 当前边界

贴地体积雾当前只有数据接口，不是已经完成的 Froxel 体积散射系统；启用其 Profile
开关不会伪装出一个错误的平面雾效果。当前云仍是适合风格化远景的双层 2D 高空云，不是
体积云 Ray March；它包含沿主光方向的低频自遮挡近似，但不包含完整体积阴影、天气图
或局部云体。

目前 TOD 不再主动覆盖 Unity 的 Ambient Sky / Equator / Ground 设置，环境光请暂时
继续在 Lighting Settings 或项目自身的光照系统中维护。Profile 内原有环境光数据保留，
用于兼容旧资源，但不在编辑界面显示也不在运行时应用。
