# Unity 地形系统升级预研

> 项目基线：Unity 6000.3.10f1、URP 17.3、自研 Nanite 式虚拟几何 / GPU 剔除 / HZB / Page Streaming、TOD 天空与云影。  
> 预研日期：2026-07-30。

## 1. 结论

不建议把“升级地形”理解为换一个更复杂的 Terrain Shader。对本项目，收益最高的方案是分层建设：

1. **Unity Terrain 作为生产与交互底座**：高度、洞、碰撞、导航、编辑器笔刷、邻接瓦片。
2. **自研地表材质系统提升近中景质感**：宏观色、坡度/高度/曲率自动铺层、近景细节、湿润/积雪、道路与轨迹。
3. **GPU Driven 植被系统替换内置 Tree/Detail 的规模瓶颈**：统一实例数据、GPU 剔除、LOD/Impostor、风场和交互。
4. **大世界瓦片流送管理资源生命周期**：Scene/Addressables 分块，统一地形、植被、物理、导航、APV 的加载环。
5. **把远景地形和岩体逐步接入现有 Nanite 管线**：不是一开始就替换 Unity Terrain，而是先解决最有价值的远景几何、悬崖和扫描岩石。

推荐目标架构是 **Hybrid Terrain**。它比“完全依赖内置 Terrain”有更高的性能上限，也比“一次性重写地形”风险低。

## 2. 当前项目审计

### 已有优势

- Unity 6.3 + URP 17.3，已具备 Render Graph、Forward+、APV、GPU Resident Drawer 等现代能力。
- 自研渲染链已有 GPU 层级剔除、可见性缓冲、HZB、阴影级联剔除、几何 Page Pool 和请求流送。
- TOD 已能提供动态天空、雾和云影，天然适合接入地形湿度、积雪、风场和昼夜材质响应。
- PC 渲染路径当前为 Forward+，SRP Batcher 已开启。

### 明显缺口

- 仓库中还没有 TerrainData、TerrainLayer 或实际地形场景，尚未建立生产规范。
- 没有安装 `com.unity.terrain-tools`。
- PC 的 GPU Resident Drawer 和其遮挡剔除未开启；但它仅直接覆盖 MeshRenderer，不能当成 Terrain/植被系统的完整替代。
- PC 的 APV、磁盘流送和 GPU 流送均未开启。
- SSAO Renderer Feature 存在但未激活；Decal Renderer Feature 未配置。
- QualitySettings 仍使用 legacy detail distribution，纹理 Mip Streaming 全局未开启。
- 地形质量参数尚未分档；当前 PC/Mobile 都没有启用 Terrain Quality Overrides。
- PC 阴影距离仅 50 m。开放场景若不增加远景接触阴影、地形烘焙阴影或其他低频遮蔽，会明显“漂浮”。

## 3. 升级项总览

| 方向 | 升级项 | 主要收益 | 成本 | 优先级 |
|---|---|---:|---:|---:|
| 基础 | Terrain Tools + 地形瓦片规范 | 美术效率、可重复生产 | 低 | P0 |
| 材质 | 自研 Terrain Lit（自动铺层 + 宏微观融合） | 质感 | 中 | P0 |
| 诊断 | 地形预算面板与自动校验 | 性能、效率 | 低 | P0 |
| 植被 | GPU Driven 草木原型 | 性能、密度、质感 | 中 | P0 |
| 流送 | 3×3/5×5 瓦片加载环 | 大世界内存与卡顿 | 中 | P1 |
| 交互 | 路径/车辙/湿润/积雪 Runtime Mask | 质感与玩法 | 中 | P1 |
| 光照 | APV + 天空遮挡 + 分块流送 | 动态 TOD 下的整体感 | 中 | P1 |
| 几何 | 远景地形 / 岩体 Nanite 化 | 远景精度、吞吐 | 高 | P2 |
| 高级 | 虚拟纹理 / Clipmap 地表 | 超大世界材质上限 | 高 | P2/P3 |

## 4. 提升质感

### 4.1 地表材质：从“图层混合”升级为“地貌着色”

第一版自研 URP Terrain Shader 建议包含：

- **按高度、坡度、朝向、曲率自动铺层**：手绘控制图只做覆盖和艺术指导，不再逐块刷满。
- **宏观颜色图 / 地貌图**：1–4 km 尺度抑制重复纹理；中景保留山体走势。
- **近景 Detail Albedo + Detail Normal**：约 0.1–2 m 尺度补充砂石、裂纹、泥土颗粒。
- **距离分段采样**：近景完整 PBR，中景减少法线/高度采样，远景使用烘焙 basemap 或宏观图。
- **高度混合而非纯权重线性混合**：改善泥土填入岩缝、雪落在表面等层次关系。
- **坡面 Triplanar 作为局部补救**：只在陡坡开启，避免全地形三向采样。
- **统一材质参数**：BaseColor、Normal、AO、Smoothness、Height 按 Texture2DArray 组织，减少状态和变体。
- **随机化**：世界空间旋转、镜像、UV 偏移、色相轻扰动，打散规则重复。

建议把每像素高成本层数硬限制为 4；更多逻辑层在离线或 Compute 阶段合成为 4 个运行时权重。不要把“支持 16 层”误解为每个像素实时采样 16 层。

### 4.2 地形与岩石融合

- 悬崖、洞穴、挑檐、巨石继续用 Mesh 表达，不强迫高度场承担无法表达的几何。
- 岩体优先接入现有 Nanite 虚拟几何；地形与岩体共享世界空间材质参数。
- 使用 RVT/缓存式地表采样，或先用轻量的地形颜色/法线捕获图，让岩石底部继承地表颜色、湿度和积雪。
- 用 Mesh Decal 或 DBuffer Decal 覆盖路面边缘、泥迹、落叶、裂缝；大面积道路不要堆叠大量 Projector。

### 4.3 动态天气与地表状态

建立低分辨率、可流送的 `Surface State Mask`：

- R：湿润
- G：积雪
- B：泥泞 / 受扰动
- A：玩法自定义

TOD 提供太阳方向、降水、温度、风和云影；地形、岩体、道路与植被共享同一套状态。近玩家区域用 Runtime RenderTexture 更新，远处使用静态或低频模拟。

### 4.4 光照与空间感

- 启用并验证 URP APV，给植被、岩石、建筑等动态/非 Lightmap 对象提供一致的逐像素间接光。
- APV 的天空遮挡适合现有昼夜系统：天空颜色变化时，遮蔽区域仍能保持空间层次。
- 开放世界启用 APV Disk/GPU Streaming；其单元与地形瓦片加载环对齐。
- SSAO 只作为近景接触增强，并配置半分辨率/质量档；不能用它代替地形材质 AO、植被阴影和烘焙低频遮蔽。
- 云影继续作为大尺度动态明暗，但应按地形世界坐标稳定采样，并避免与级联阴影产生过强叠乘。

## 5. 提升性能

### 5.1 地形瓦片与 LOD

建议从 **1 km × 1 km 瓦片、每瓦 513 或 1025 高度分辨率**开始验证，而不是直接选择最大精度。

- 513/1 km：约 1.95 m 顶点间距，适合远景或车辆高速区域。
- 1025/1 km：约 0.98 m，适合玩家可步行的中近景。
- 小于 0.5 m 的轮廓细节应由 Mesh、视差/法线或交互贴图表达，不应无限提高 Heightmap。
- 相邻瓦片统一尺寸、分辨率、原点和边界采样，自动执行 seam 检查。

Unity Terrain 首期配置：

- 开启 Draw Instanced。
- 质量档覆盖 Pixel Error、Basemap Distance、Detail/Tree Distance 和密度。
- PC、主机、移动端分别配置，不共用一套极端参数。
- 用 Frame Debugger、RenderDoc 和 GPU Profiler 测量 TerrainLit pass、附加 pass、阴影和植被，不能只看总 FPS。

### 5.2 GPU Driven 植被

内置 Detail/Tree 可作为原型或低端回退，主路径建议自研：

1. 离线把散布规则烘焙为紧凑实例数据，按地形 Cell 分页。
2. Compute 执行视锥、距离、HZB 和可选密度裁剪。
3. 每物种 / LOD 输出 Indirect draw 参数。
4. 近景 Mesh，中景简化 Mesh，远景 Impostor；草在远端逐渐减密而不是只缩短距离。
5. 阴影使用独立、更激进的 LOD 和距离；小草通常不进入远级联。
6. 风场、压草、燃烧等交互用低分辨率世界空间场驱动，不逐实例挂 MonoBehaviour。

可以复用现有 Nanite HZB、实例队列、Page Pool、GPU 统计与预算闸门。植被不需要强行转成 Nanite 三角形管线，但应共享可见性基础设施。

### 5.3 世界流送

定义以玩家为中心的加载环：

- **物理环**：地形 Collider、道路、近景玩法对象。
- **渲染环**：Terrain / Nanite 岩体 / 植被实例页。
- **背景环**：低精度远景地形、Impostor、宏观纹理。
- **数据环**：NavMesh tile、APV cell、音频/生态数据。

加载必须是分阶段且有帧预算的：

`IO → 解压 → CPU 发布 → GPU 上传 → 激活 → 旧瓦片 Fence 后回收`

建议延续现有 Page Streaming 的设计，增加统一 `WorldTileId` 和预算调度器，避免每个子系统各自争抢 IO 和上传带宽。

### 5.4 纹理与显存

- 开启并审计 Texture Mip Streaming；当前全局质量档未开启。
- Terrain Layer 统一分辨率和打包规范，优先使用平台压缩格式。
- 控制贴图按瓦片加载；空层不能产生无意义 splat/control texture。
- 宏观图、状态图、权重图建立独立预算。
- 只有当实际世界规模和材质数量证明普通分块纹理不够时，再进入虚拟纹理/Clipmap；它不是首期必需品。

### 5.5 与 GPU Resident Drawer 的关系

Unity 6 的 GPU Resident Drawer 可以降低大量兼容 MeshRenderer 的 CPU 提交开销，但官方限定其核心对象为 MeshRenderer，并要求 Forward+、Compute-capable 平台。因此：

- 可在 PC 上作为普通岩石、道具、建筑碎片的低成本补充。
- Terrain、草、树和现有 Nanite Proxy 仍由各自主路径负责。
- 开启前需验证自研 Renderer Feature、材质兼容性、动态对象更新和显存变化，避免与已有 GPU Driven 系统重复维护数据。

## 6. 提升工作效率

### 6.1 安装 Terrain Tools

Unity 6 官方 released 版本 Terrain Tools 5.3.1 提供侵蚀、噪声、笔刷 Mask/Filter、批量 Terrain Toolbox、Heightmap/Splatmap 导入导出。它适合作为首期编辑底座。

### 6.2 非破坏式地形生产

建立项目自己的 `TerrainRecipe`（ScriptableObject）：

- 输入：Heightmap、道路样条、河流样条、生物群系 Mask、禁布区、人工笔刷覆盖。
- 过程：基础高度 → 侵蚀 → 道路/河流压平 → 自动材质 → 植被散布 → 手工覆盖。
- 输出：TerrainData、Control Map、植被实例页、Collider、NavMesh tile、预览图和统计报告。

所有昂贵步骤支持：

- 只重建脏瓦片；
- 可复现随机种子；
- 批处理 / CI；
- 中间产物缓存；
- 构建版本与参数 Hash；
- 一键比较前后统计。

### 6.3 美术工具

建议制作一个 `Terrain World` 窗口：

- 世界瓦片网格、加载状态和接缝热力图。
- 多瓦片笔刷和样条编辑。
- 生物群系规则预览。
- 纹理采样数、控制图数量、植被实例数、阴影实例数热力图。
- 一键跳转最重瓦片。
- 一键生成近/中/远质量截图。

### 6.4 自动化校验

每次提交或构建检查：

- 瓦片尺寸、分辨率、坐标是否符合规范。
- 邻接 Height/Normal 是否有 seam。
- Terrain Layer 数量、贴图尺寸、格式和空层。
- 每 Cell 植被数量、物种数量和阴影预算。
- Collider 与可视地形误差。
- 场景/Addressable 引用是否越过瓦片边界。
- 是否存在未压缩、无 Mip 或错误 sRGB 的地表贴图。
- 是否超过 PC / Mobile 分档预算。

## 7. 三种技术路线

### A. 强化 Unity Terrain

适合中小地图、上线时间紧、团队重视编辑效率。

- 优点：最快；编辑器、碰撞、邻接和 API 成熟。
- 缺点：大规模植被、材质层数、超远景和特殊几何上限较低。
- 结论：可作为第一阶段，但不应是项目最终上限。

### B. Hybrid Terrain（推荐）

Unity Terrain 承担近中景高度场 / 物理 / 编辑；Nanite Mesh 承担悬崖岩体和远景；自研 GPU 植被与统一流送。

- 优点：复用现有技术最多，风险与上限平衡最好。
- 缺点：需要解决 Terrain、Mesh、植被之间的材质和 LOD 接缝。
- 结论：本项目最匹配。

### C. 全自研 Clipmap / Virtual Geometry Terrain

- 优点：可统一几何、材质、剔除与流送，理论上限最高。
- 缺点：编辑、碰撞、NavMesh、洞、样条、植被吸附和工具链都要重建。
- 结论：只有验证 Hybrid 被明确瓶颈卡住后再做；不建议直接立项替换。

## 8. 推荐实施路线

### Phase 0：基线与指标（2–3 天）

- 建立 4×4 km、16 瓦片的代表性测试世界。
- 选定步行、驾驶、俯瞰三个 Camera Path。
- 采集 CPU Main/Render、GPU 各 Pass、显存、加载尖峰、Draw/SetPass、可见实例数。
- 固化高 / 中 / 低三档画质与目标硬件。

### Phase 1：P0 可玩原型（1–2 周）

- Terrain Tools 与瓦片规范。
- 第一版自研 Terrain Shader：4 层、自动铺层、宏观图、近景细节、距离降级。
- 地形预算/校验窗口。
- GPU 草原型：一种草、两级 LOD、Frustum + HZB + Indirect。

**Go/No-Go**：在代表场景达到目标帧预算，近景无明显平铺重复，16 瓦片可稳定编辑与保存。

### Phase 2：生产管线（2–4 周）

- TerrainRecipe 增量构建。
- 道路 / 河流样条和局部覆盖。
- 多物种 GPU 植被、阴影 LOD、交互风场。
- 3×3/5×5 世界加载环；纹理、实例、Collider、NavMesh 对齐。
- APV 与天空遮挡验证，启用流送。

### Phase 3：差异化能力（3–6 周）

- 岩体 / 悬崖 Nanite 化和地表材质融合。
- 远景地形 Mesh 烘焙并接入 Nanite，解决 Terrain 到远景 Mesh 的无缝切换。
- Runtime Surface State：车辙、湿润、积雪。
- 根据 Profile 决定是否投入虚拟纹理或 Geometry Clipmap。

## 9. 验收指标建议

以下是建议初值，最终需按目标平台修订：

| 指标 | PC 高画质目标 |
|---|---:|
| 地形本体 GPU | ≤ 1.5 ms @ 1440p |
| 植被 GPU（含近景阴影） | ≤ 2.5 ms |
| 地形 + 植被 CPU 提交 | ≤ 1.0 ms |
| 相机移动流送 P99 主线程尖峰 | ≤ 2 ms |
| 单帧 GPU 上传预算 | 8–16 MiB，可配置 |
| 活跃 Terrain Shader 每像素物理层 | ≤ 4 |
| 地形边界高度 seam | 0 |
| 地形边界法线可见跳变 | 自动截图测试无可见接缝 |
| 高速移动下缺瓦 / 闪烁 | 0；允许先显示父级 / 远景代理 |

质量验收不要只看静态截图，至少覆盖：

- 日出、正午、日落、夜晚；
- 晴天、强云影、湿润、积雪；
- 贴地 1.7 m、车辆高速、200 m 俯瞰；
- 原地转向、快速横移、跨瓦片和瞬移。

## 10. 风险与决策点

- **自研 Terrain Shader 与 URP 升级耦合**：封装公共 HLSL 接口，减少直接复制包内 TerrainLit。
- **自研 Nanite 与 Terrain 深度/阴影顺序冲突**：先固定 Render Graph pass contract，再做融合。
- **植被 Alpha Overdraw**：GPU 剔除不能解决像素过绘；需网格卡片、Alpha coverage、距离减密共同处理。
- **APV 与动态高度变形**：APV 适合静态几何布局；大范围运行时改地形需明确降级策略。
- **虚拟纹理过早投入**：先用分块纹理与 Mip Streaming 量化瓶颈。
- **工具链欠债**：每增加一种运行时数据，必须同时补可视化、统计、增量构建和版本迁移。

## 11. 官方依据

- [Unity 6 Terrain 概览](https://docs.unity3d.com/cn/current/Manual/script-Terrain.html)
- [Unity 6 Terrain Tools 5.3.1](https://docs.unity3d.com/current/Manual/com.unity.terrain-tools.html)
- [Unity 6 URP GPU Resident Drawer](https://docs.unity3d.com/cn/6000.0/Manual/urp/gpu-resident-drawer.html)
- [Unity 6 URP Adaptive Probe Volumes](https://docs.unity3d.com/cn/6000.0/Manual/urp/probevolumes.html)
- [APV 大世界流送与 Addressables / AssetBundle 加载](https://docs.unity3d.com/cn/6000.0/Manual/urp/probevolumes-streaming.html)
- [APV 天空遮挡与运行时天空光变化](https://docs.unity3d.com/cn/6000.0/Manual/urp/probevolumes-skyocclusion.html)
- [URP Decal Renderer Feature](https://docs.unity3d.com/Packages/com.unity.render-pipelines.universal@14.0/manual/renderer-feature-decal.html)

