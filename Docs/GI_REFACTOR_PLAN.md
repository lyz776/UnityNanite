# Realtime GI 重构冻结计划

状态：节点 0、节点 1、节点 2 已完成；节点 3 正在分层实现与运行验收。

本文是本轮重构的范围基线。大节点、验收条件和系统边界不得在实现过程中静默扩张。
如果事实证明架构或范围必须改变，应先记录原因、受影响节点和新的验收条件，得到确认后再修改本文。

## 1. 产品目标

第一阶段只交付稳定、低频、可风格化的 Diffuse GI，并优先保证可编译、可观察和功能闭环，不设 GPU 时间目标。

支持范围：

- 静态普通 Mesh；
- 只改变 Transform 的动态刚体普通 Mesh；
- Nanite 静态实例；
- 只改变 Transform 的 Nanite 刚体实例。

明确不在第一阶段范围内：

- Skinned Mesh、顶点动画、风动画；
- 透明、水、粒子；
- 任意拓扑变化；
- Specular/ReSTIR（作为 Diffuse 验收后的独立阶段）；
- 性能档位、自动质量伸缩和多平台兼容层；
- 为旧 GI 数据或组件提供迁移、fallback 或兼容路径。

第一阶段的间接光源包括主方向光、当前场景中的 Point/Spot 局部光与 Emissive。Area Light、Cookie、IES
及项目自定义灯光先不进入 ABI；不会为它们预埋未使用的通用灯光框架。

## 2. 候选实现裁决

| 来源 | 裁决 | 用途 |
|---|---|---|
| ReSTIRDI-Minimal-URP-2022 | 不作为底座 | 它是 Unity 2022 + DXR 的 ReSTIR Direct Illumination 教学样例，没有 Diffuse GI、radiance cache 或软件世界追踪；仅在后续 Specular 阶段参考 reservoir ABI 和重投影组织。仓库根目录未发现明确许可证，因此不复制代码。 |
| Kuan-Mi/UnityPathTracing | 不作为底座 | 它依赖 DX12 原生插件、硬件 RT、Bindless、NRD/RTXDI/RTXPT 和修改后的 Agility SDK。适合硬件路径追踪，但会为当前软件 GI/Nanite 需求引入更大的平台和构建系统。仓库根目录未发现明确许可证，因此只参考集成思路。 |
| AKGI | 主要上游实现，允许直接移植 | 项目仅用于学习；用户确认其使用方式符合 Unreal Engine EULA。优先移植 screen gather、voxel/distance world trace、surface radiance 和后续独立 specular 的已验证算法与 shader 代码。UE Renderer、RDG、GPU Scene 和资源管理部分按 Unity 6 RenderGraph/Nanite 接口重写，不把无关引擎框架一并搬入。 |
| Kajiya | 算法参考 | MIT/Apache-2.0。参考低频 GI 分层、时空稳定性和 debug 方法，不移植其 Vulkan/Rust renderer。 |
| src-dgi | 算法参考 | MIT。参考 surfel/radiance cascade 的低频表示和可视化，不采用其整套 renderer。 |
| NVIDIA RTXDI / NRD | 后续 Specular 参考 | 不进入 Diffuse 基础层；待节点 5 开始时再按各自许可证与本地版本核对可复用文件。 |
| 当前工作区与 `gi3` | 问题样本与接口来源 | 不作为演进根基。只保留已经独立、必要且可验证的场景/材质读取与 Nanite 只读导出能力；旧 allocator、reservoir、BVH refinement、双层 cache 和兼容路径不保留。 |

结论：AKGI 是与目标最接近的主要上游，但无法把 UE Renderer 整体直接编译到 Unity 6 URP + 自定义 Nanite
工程。本实现以 AKGI 的可运行算法和 shader 为起点逐段移植；宿主调度、场景适配、资源生命周期和 Nanite
访问围绕当前工程接口重写。每次移植仍只取当前节点形成端到端闭环所需的最小代码，不提前搬入后续功能。

所有直接移植或实质性改写的文件必须在文件头或相邻文档中记录：

- AKGI 上游仓库与分支 `AKGWSB/UnrealEngine:4.27-akgi`；
- 对应的 UE 源文件相对路径；
- 本地为 Unity/HLSL/坐标系/资源 ABI 所做的关键修改；
- 若发现疑似上游 bug，保存独立于本项目架构的最小复现、预期/实际结果和修复差异，便于反馈。

## 3. 冻结架构

```text
普通 Mesh Adapter -------+
                         +--> GI Scene (instance / geometry / material)
Nanite Read-only Adapter-+                 |
                                           v
                              Unified Sparse Brick Clipmap
                              occupancy / distance / surface
                                           |
                                World Surface Radiance
                                main/local light + emissive
                                           ^
                                           |
完整 GBuffer --> Screen Surface Lighting   |
      |          current frame only         |
      |          main/local light + emissive |
      v                                     |
Primary + Adaptive Screen Probes            |
      |                                     |
      +--> Screen HZB Trace -- hit ----------+--> sample Screen Surface Lighting
                    |
                    | compacted miss
                    v
              World Distance Trace ------------> World Surface Radiance / sky
                    |
                    v
       Directional Screen Radiance Cache
       probe geometry + octahedral radiance
                    |
       Probe Temporal Validation + Spatial Reconstruction
                    |
       Per-pixel Diffuse Irradiance Integration
                    |
       Stylized Frequency Resolve + Composite
```

最终画质由屏幕空间 probe/radiance cache 决定。World Clipmap 是屏幕外可见性和 radiance fallback，不能直接
作为逐像素最终 GI。这样相同屏幕 footprint 获得相近的方向采样预算，不会因为接收点处于不同 Clipmap
层级而直接改变显示质量。

### 3.1 GI Scene

GI Scene 只负责把受支持对象规范化为三类稳定数据：instance、geometry、material。

- 普通 Mesh 的顶点/索引按 Mesh 共享，刚体移动只更新 instance transform。
- Nanite 不复制一套长期持有的几何；Adapter 读取 Nanite 发布的只读 resident page/table/buffer view。
- 每个 instance 保存当前与上一次 world bounds。Transform 变化时产生旧、新 AABB dirty region。
- Transform 轮询只覆盖带 `Rigidbody` 或显式 `GITransformTracked` 标记的实例；其余普通 Mesh/Nanite 在本次 GI Scene 收集周期内视为静态，不承担逐帧矩阵比较成本。
- 不注册受支持范围之外的 Renderer，不提供隐式 proxy 或降级路径。

### 3.2 Unified Sparse Brick Clipmap

世界表示采用相机跟随的多级 clipmap。页表把逻辑 brick 映射到固定容量物理池；每个物理 brick 固定包含
`8 x 8 x 8` cells。首版固定 4 个层级，分辨率与覆盖距离属于可调参数，不改变数据流。

每个 cell 只保存 world trace 和 radiance 查询需要的最小信息：

- occupancy；
- 保守 unsigned distance；
- packed world normal；
- packed base color / emissive；
- material flags；
- packed surface radiance 与 validity。

不再区分 Static Pool 与 Dynamic Pool。动态物体移动时，旧、新 AABB 涉及的 brick 都标脏；dirty brick
先完整清空，再从所有与它相交的当前实例重建。这使移动、删除和遮挡变化共享同一条正确路径。

首版由 CPU 维护逻辑页映射、dirty AABB 和 brick-instance 工作列表，GPU 完成清空、体素化、距离生成和
radiance 更新。这是正式调度边界，不是兼容 fallback；未来若性能要求改为 GPU 生成工作列表，World Cache ABI
和下游 pass 不需要改变。

Clipmap 只作为世界 fallback 仍必须满足以下稳定性规则：

- cell 坐标锚定世界网格；相机移动只滚动新进入的 brick slab，不重新量化仍在覆盖内的世界位置；
- 相邻层级存在重叠区，不以单一距离阈值硬切；
- trace 根据 ray footprint 连续选择目标 LOD，并在相邻层级都有效、surface normal/material 相容时混合 radiance；
- 更细层级未完成更新时显式使用有效父层级，绝不读取半构建 brick；
- 若相邻层级命中不相容，不插值虚假的 hit position：选择更细的有效命中并降低 confidence，由 Screen Probe
  history 控制替换速度；
- world trace 输出 level、generation、fallback、transition weight 和 confidence，供 debug 与 temporal validation；
- World Clipmap 的内容永远不直接显示到最终像素，避免放大层级块状结构。

### 3.3 两类 radiance source

World Surface Radiance 只保存确定性的局部出射 radiance：

```text
emissive + baseColor * visible main/local direct lights
```

它不读取屏幕历史，不递归读取自身，也不存最终 irradiance。光照、材质或阴影 revision 变化只让受影响
radiance 更新；几何/Transform 变化才重建 brick。

Screen Surface Lighting 是由当前完整 GBuffer 生成的当前帧高质量 hit source：

```text
current emissive + current baseColor * visible main directional light
```

- screen trace 命中时读取它，不回头读取粗粒度 World Clipmap；
- 它不包含 indirect diffuse、scene color 或上一帧结果，因此不会形成屏幕反馈回路；
- 普通 Mesh 与 Nanite 在最终 GBuffer 合并后共享同一生成 pass；
- world trace miss 才读取 World Surface Radiance 或 sky。

### 3.4 Screen Probe Gather 与屏幕 radiance cache

- 固定 screen tile 产生 primary probe；深度/法线不连续处通过紧凑列表追加 adaptive probe，避免一个 probe
  跨越前景与背景；
- 每个 probe 保存 receiver world position、几何法线、lighting normal、validity 和 octahedral directional radiance；
- probe ray 优先使用当前深度 HZB；screen hit 读取 Screen Surface Lighting；
- screen miss 在 group 内压紧，再执行 world distance trace；world hit 读取 World Surface Radiance，真正离开
  Clipmap 才返回 analytic sky；
- 每帧只更新方向图的一部分，使用低差异序列覆盖方向；方向分层与 history 共同提供均匀收敛；
- history 重投影在邻域中寻找最接近的 receiver，并以世界位置、几何法线、切平面距离、material signature、
  lighting revision 和 world trace confidence 验证；
- 最终像素只插值几何相容的 primary/adaptive probes，再按 lighting normal 对方向 radiance 做 hemisphere integration；
- disocclusion 或没有相容 probe 时使用本帧 trace/sky 的明确低 confidence 结果，不无限延长旧 history；
- Diffuse 与未来 Specular 不共享 reservoir、history 或 denoiser。

具体 tile 尺寸、方向图分辨率和每帧更新比例属于节点 3 的参数调优，不改变上述数据流或验收项。

### 3.5 风格化光照与法线职责

风格化是正式表示层，不是最后附加的颜色乘法。系统同时保留 raw irradiance 与 stylized irradiance debug，
以便区分 transport 错误和美术变换。

本节参考并核对了 Yu-ki016《【UE5】卡通渲染着色篇4：环境光与GI》
（https://zhuanlan.zhihu.com/p/702842735）：可迁移的是“环境/GI 法线低频化、多级 downsample 后小卷积、
Lab 空间独立控制亮度与色彩、AO 强度可调”这组有界操作，而不是照搬其 UE 后处理插入点或固定 6 次降采样参数。

法线严格分工：

1. **Geometry normal**：用于 ray origin bias、正反面、world/screen visibility、薄墙保护、probe 几何相容性和
   temporal rejection。任何风格化参数都不能改变它。
2. **Lighting normal**：低频法线，不包含高频 normal-map 细节。用于主光/后续局部光的 `N dot L`、diffuse
   ray lobe 与 probe hemisphere integration。屏幕路径从 edge-aware normal pyramid 得到，world path 从
   voxel surface 的低频顶点/几何法线得到。
3. **Display shading normal**：保留当前 GBuffer 法线，供材质表面、直接光和未来 Specular 使用，不控制 GI
   visibility。

`lightingNormalFlatten` 只在 Display shading normal 与低频 Lighting normal 之间混合；它不会把所有模型法线
统一推向 world-up，也不会修改 Geometry normal。主光与以后加入的局部光，在写入 Screen/World radiance source
时使用相同 Lighting normal 语义。这样可以主动减少角色暗部的塑料体积感，同时不制造漏光。

风格化频率变换发生在 probe resolve 之后、材质 diffuse color 合成之前：

1. 从 resolved linear HDR irradiance 生成明确低分辨率的 downsample chain；
2. 在最低必要层级做一次小卷积/双边重建，不用超大 full-resolution kernel；
3. 将 raw 与 low-frequency irradiance 转到 CIE Lab，分别以 `luminanceFrequencyBlend` 和
   `chromaFrequencyBlend` 混合亮度与色彩；相比直接 RGB lerp，它对应参考文章中独立控制 Lab `L/ab` 的目标；
4. `occlusionStrength` 使用 `lerp(1, AO, strength)` 控制暗角体积感；AO 同时可作为空间重建 guide；
5. 转回 linear RGB 后再乘 receiver diffuse color，并以唯一 composite 写入 lighting buffer。

这些是有界、可单独关闭并有 raw 对照的非物理 trick。禁止通过过长 temporal history、向 world cache 回写
风格化颜色或无几何约束的全屏 blur 来伪造稳定性。

### 3.6 URP / Nanite 集成边界

- GI 只支持当前 URP Deferred 路径；Forward 不实现 fallback。
- GI pass 在普通 Mesh 和 Nanite 都完成最终 GBuffer 后运行。
- Diffuse 只通过一个 composite 出口进入 camera lighting buffer。
- 只保留抑制 URP 原生 baked/SH diffuse 所必需的最小 hook；URP 原生 direct light、emission 和 reflection
  probe/specular 在第一阶段继续工作。
- Nanite 只发布只读几何 view 和完成 GBuffer 的时序信号；GI 不获得 Nanite page pool 所有权。

## 4. 模块复杂度边界

正式运行时代码只允许存在以下职责模块：

1. `GIScene`：对象注册、revision 和统一 GPU 表；
2. `GIMeshGeometryCache`：普通 Mesh 共享几何；
3. `GINaniteAdapter`：Nanite 只读 view；
4. `GIWorldCache`：clipmap、dirty brick、资源生命周期；
5. `GIRadianceSources`：Screen/World 主光与 emissive source；
6. `GIScreenProbeGather`：probe placement、screen/world trace、directional history 与 per-pixel integration；
7. `GIStylizedResolve`：normal pyramid、低频重建、CIE Lab 亮度/色彩变换与 AO；
8. `GIDiffuseRendererFeature`：上述 pass 的 RenderGraph 编排和唯一 composite；
9. `GIDebug`：debug mode、计数器和日志。

禁止重新引入：

- Legacy/modern 两套 command 路径；
- 静态/动态两套世界 cache；
- CPU/GPU 两套 voxel fallback；
- Diffuse/Specular 统一 mega payload；
- 节点 1-4 未使用的 reservoir、BVH triangle refinement、free-space probe field；
- 为未知材质猜测语义的大型适配器。

Screen Probe directional cache 是最终 gather 的必要表示，不属于被禁止的 free-space world probe field。

## 5. 固定大节点与验收条件

Diffuse 第一阶段共 5 个大节点。进度只按已通过的大节点和节点内验收项报告，不按耗时估算百分比。

### 节点 0：选型与架构冻结（已完成）

- 审计候选仓库、许可证和适配成本；
- 审计 `gi3`、当前工作区和 Nanite 导出面；
- 冻结支持范围、数据流、复杂度边界和节点定义。
- 完成 Clipmap 稳定性复审，将 Clipmap 限定为 world fallback，并冻结 Screen Probe Gather；
- 完成 Toon Lumen 参考复审，冻结三法线职责和独立亮度/色彩降频边界。

### 节点 1：静态普通 Mesh 的 world trace 闭环

验收项固定为：

1. 保存当前未提交旧 GI 的可恢复快照，然后从可导入 Assets 中移除旧实现；
2. 新 GI Scene 能上传静态普通 Mesh、instance 和最小材质数据；
3. 新 unified clipmap 能分配、清空、体素化并生成 conservative distance；
4. 全屏 debug ray 能显示 world hit/miss、命中层级、occupancy、geometry/lighting normal 和 base color；
5. debug 能显示 level generation、父层 fallback、transition/confidence；移动相机时不存在有效层到未构建层的硬切；
6. Editor 编译无错误，SampleScene 运行无 GI 异常日志。

节点 1 不计算 GI 光照，不接动态物体，不接 Nanite。

### 节点 2：刚体动态与 Nanite 世界表示

验收项固定为：

1. 普通 Mesh 刚体移动、旋转、缩放、禁用和删除会重建旧/新 dirty brick；
2. Nanite 静态实例通过只读 resident page view 进入相同 clipmap；
3. Nanite 刚体 Transform 变化使用相同 dirty 规则；
4. residency/generation 变化不会读取失效 page；
5. debug view 能区分普通/Nanite surface 与本帧 dirty/rebuilt brick；
6. Editor 编译无错误，SampleScene 静态与移动测试无 GI 异常日志。

“动态 Nanite”在本阶段严格指 Transform 刚体变化，不包含拓扑或顶点变形。

### 节点 3：低频 Diffuse 完整闭环

验收项固定为：

1. Screen Surface Lighting 与 World Surface Radiance 均能正确显示 emissive、主光/局部光 radiance 和 validity；
2. geometry/display/lighting normal 及 flatten 前后贡献可单独 debug；
3. primary/adaptive probe placement、receiver geometry 与 directional radiance cache 可 debug；
4. screen trace、miss compaction、world fallback、层级 confidence 与 sky miss 全部可单独 debug；
5. probe temporal accept/reject、history age、lighting revision 与 reset 可 debug；
6. raw per-pixel irradiance、low-frequency irradiance、CIE Lab 亮度/色彩混合和 AO 强度可逐级对照；
7. geometry-aware reconstruction、唯一 diffuse composite 完成；普通 Mesh 与 Nanite 接收同一结果，动态刚体
   不会保留旧位置光照；
8. Editor 编译无错误，SampleScene 运行无 GI 异常日志。

### 节点 4：旧路径清除与 Diffuse 最终验收

验收项固定为：

1. 删除旧 allocator、旧 cache、旧 reservoir、旧 composite 和无调用资源；
2. 删除不再需要的 URP/Nanite 修改，只保留冻结集成边界；
3. 没有 Legacy/fallback/compatibility 路径；
4. Development Player 构建通过；
5. 输出一份固定相机、移动相机、动态刚体三组日志与 debug 截图说明，同时包含 raw/stylized、probe、
   Clipmap level/confidence 和三类法线对照；
6. README 只描述实际已完成架构、限制和手工验收步骤。

完成节点 4 后，Diffuse 第一阶段结束。

### 节点 5：独立低频 Specular/ReSTIR（下一阶段）

节点 5 只有在 Diffuse 验收后才展开内部子节点。已冻结的外部边界是：共享 screen/world trace 与 surface
radiance 查询，使用独立 GGX candidate、reservoir、temporal validation、ray reuse 和 roughness-aware denoiser。
完全光滑材质可以进入该路径，但输出允许有明确的低频重建/滤波下限；它不是 Diffuse history 的别名。

## 6. 每次汇报格式

每次阶段汇报固定包含：

```text
当前大节点：N / 5（或 Specular 阶段）
节点内进度：完成项 / 固定验收项
本次改动：文件与行为
验证：编译、运行、日志、debug view
完成本节点还缺：只列本文已有验收项
后续大节点：剩余数量与名称
下一步：一个明确动作
计划变更：无 / 待确认的变更提案
```

Bug 修复、参数调节和为完成既定验收项所需的局部实现不会增加验收项数量。任何新增产品能力都进入节点 4
之后的后续阶段，除非先得到明确的范围变更确认。
