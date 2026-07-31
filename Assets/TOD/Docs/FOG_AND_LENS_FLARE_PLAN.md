# TOD Lens Flare 与雾系统预研 / 实施计划

## 目标与当前结论

本轮把雾拆成三条互不混淆的技术路线：

1. **常规距离雾**：低成本、稳定、负责大尺度空气透视和基础色调。
2. **屏幕空间散射柔化**：美术型后处理，负责远景柔化与偏电影的朦胧感。
3. **贴地体积雾**：真正读取地形高度、带形态、受光和体积阴影的晨雾。

前两项已经进入现有 `TODFogRendererFeature`，第三项本轮建立 Profile、全局参数和
地形高度图场景入口。贴地体积雾尚未假装成“已完成效果”；它需要独立的低分辨率
体积积分和时域重建，直接塞进当前单次全屏雾 Pass 会得到错误的光照和不可接受的成本。

Lens Flare 使用 Unity URP 自带的 `LensFlareComponentSRP`。太阳和月亮挂点分别持有
一个方向光类型的 Flare 发射器，TOD Profile 负责按 0–24 小时驱动强度、尺寸、
深度遮挡半径和采样数。Flare 的具体光斑、鬼影、环和彩虹形状仍由
`LensFlareDataSRP` 资产负责，方便美术单独制作而不污染 TOD 数据。

## 已落地参数

### Lens Flare

- 模块启用
- 太阳 / 月亮 Flare 强度曲线
- 太阳 / 月亮 Flare 尺寸曲线
- 深度遮挡开关、遮挡半径与采样数
- 环境效果遮挡开关
- 允许屏幕外光源产生光斑
- 最大衰减距离

默认 TOD Rig 使用 URP 包内置的 Default Lens Flare Data，保证安装后即可看到效果。
要实现参考图中明显的彩虹弧、多个鬼影和横向拉丝，应在
`Assets/TOD/LensFlare` 新建自定义 `LensFlareDataSRP`，再替换太阳或月亮挂点上的
数据资产。TOD 不复制这类形状数据，只做时间与遮挡控制。

### 常规距离雾

- 顶部颜色 / 强度
- 底部颜色 / 强度
- 天空参与强度
- 雾曲线 Power
- 线性到指数的连续混合
- 起始距离、结束距离、浓度
- 雾高度、高度范围

雾颜色沿世界高度在 Bottom / Top 之间过渡。几何体高于雾高度后按高度范围指数衰减；
天空使用观察方向计算上下色，不再把整张 Skybox 粗暴覆盖成同一个颜色。

### 屏幕空间散射柔化

- 强度
- 采样半径（像素）
- 起始 / 结束距离
- 相对深度边缘阈值
- 天空参与量

当前是 8 邻域的深度感知滤波：只混合深度相近的像素，再按观察距离淡入。
它是风格化画面处理，不等价于参与介质中的多次散射，也不负责丁达尔光。
默认关闭，避免旧场景升级后立刻变糊。

### 贴地体积雾预研参数

- 介质反照率、消光密度
- 离地高度、雾层厚度、最大追踪距离
- 贴地形程度
- 2D 天气噪声尺度、3D 形态噪声尺度、侵蚀
- X/Z 流动速度
- Henyey-Greenstein 相位函数 G
- 体积阴影强度、丁达尔光强度
- 视线步进数

地形高度图、世界原点和世界尺寸放在 TOD Controller 场景引用中，而不是 Profile。
原因是同一个天气 Profile 应能在不同关卡复用，而每个关卡的地形范围不同。

## 贴地体积雾实现路线

### 阶段 A：高度数据

1. 为 Unity Terrain 增加 R16 / RFloat 高度图烘焙器。
2. 记录高度图对应的世界 XZ 范围与 Y 高度范围。
3. 非 Terrain 地面通过指定的顶视正交相机写入一张可选高度图。
4. Shader 用世界 XZ 采样地面高度，在
   `groundHeight + fogHeight ± heightRange` 内构造基础密度。

高度图应按地块流送或使用 Clipmap；大世界不能依赖一张无限大的常驻纹理。

### 阶段 B：密度形态

每个采样点的密度由以下部分相乘：

`高度层 × 2D 天气覆盖 × 3D 基础形态 × 侵蚀细节 × 区域遮罩`

- 2D 噪声控制大片晨雾在哪里出现。
- 3D Perlin-Worley / Worley FBM 产生厚薄和断裂。
- 高频噪声只做侵蚀，不直接决定大轮廓。
- 风向只移动噪声坐标，不移动高度边界。
- 地形附近增加柔和贴地权重，避免雾层穿山后仍保持绝对水平。

### 阶段 C：光学与丁达尔光

采用单次散射和 Beer-Lambert 透射：

`T = exp(-sigmaE * distance)`

`L += T * sigmaS * phase(view, light) * light * stepLength`

- 主方向光使用 Henyey-Greenstein 相位函数。
- 天空 / 环境光作为低频补光，避免背光区纯黑。
- 每隔若干步采样主光阴影图，未写入阴影的物体不会凭空产生光柱。
- 丁达尔光是“带阴影的方向光入射散射”，不是额外画一张径向光贴图。
- 先支持一盏主方向光；点光和聚光以后按可见光列表注入。

### 阶段 D：性能与稳定

推荐从四分之一分辨率的视线 Ray March 开始，而不是立刻做完整 3D Froxel：

- 32 / 48 / 64 步三级质量；
- 蓝噪声抖动采样起点；
- 深度和法线感知的双边上采样；
- 带速度矢量或上一帧 VP 的时域重投影；
- 相机切换、时间跳变和 Profile 切换时清除历史；
- 输出独立的 `FogScattering` 与 `FogTransmittance`，供透明材质正确合成。

如果以后需要大量局部体积、多个动态光源和统一体积光照，再升级为相机对齐的
Froxel 网格。Epic 的公开方案也是在相机视锥体体素中计算参与介质密度与光照，并指出
体积纹理 Z 切片与观察距离直接影响采样不足和成本。

## Renderer Feature 规划

建议最终顺序：

1. 不透明物体与天空
2. 云阴影
3. 贴地体积雾积分（低分辨率）
4. 体积雾时域重建与上采样
5. 常规距离雾 + 屏幕空间柔化
6. 半透明物体

半透明 Master Shader 后续应采样 `FogScattering / FogTransmittance`。继续只在半透明前
做一次全屏合成，会导致透明物体像贴在雾前面；而在透明后再次全屏合成又无法按透明
片元深度得到正确结果。

## 美术参考方向

- **清晨贴地雾**：低处有大块空洞，近地厚、向上快速消散；轮廓来自天气图和侵蚀，
  不是均匀白色平面。
- **逆光树林 / 建筑丁达尔光**：光柱宽度应由遮挡物与雾密度决定，避免无限长、
  等宽的“手电筒线”。
- **电影感远景**：屏幕空间柔化只在中远景逐渐介入，角色和近景交互物保持清晰。
- **风格化颜色**：底部可偏暖或偏脏，顶部接近天空散射色；不要用单一灰白覆盖全画面。
- **Lens Flare**：太阳允许明显的横向鬼影和彩虹弧；月亮只保留弱光晕或极轻微鬼影，
  避免和太阳共用同样强烈的镜头语言。

## 依据与边界

- Unity URP Lens Flare (SRP) 原生提供 Data 资产、强度、缩放、深度遮挡、采样数和
  Allow Off Screen，本模块只在其上增加 TOD 驱动。
- Epic UE 5.8 的公开 Volumetric Fog 文档描述的是视锥体体积纹理、参与介质光照、
  体积阴影和时域重投影；没有把“屏幕空间散射雾”作为同名独立物理雾模块说明。
  因此本项目将用户描述的远景柔化明确归类为美术后处理，避免概念混用。
- Frostbite 的统一体积渲染强调消光体积、体积阴影与物理一致的合成，适合作为贴地雾
  的长期架构参考。

参考：

- Unity URP Lens Flare (SRP)：
  https://docs.unity3d.com/cn/6000.0/Manual/urp/shared/lens-flare/lens-flare-srp-reference.html
- Unreal Engine 5.8 Volumetric Fog：
  https://dev.epicgames.com/documentation/en-us/unreal-engine/volumetric-fog-in-unreal-engine
- Unreal Engine 5.8 Local Fog Volumes：
  https://dev.epicgames.com/documentation/unreal-engine/local-fog-volumes-in-unreal-engine
- Frostbite Physically-based & Unified Volumetric Rendering：
  https://www.ea.com/news/physically-based-unified-volumetric-rendering-in-frostbite
