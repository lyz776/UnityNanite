# RealtimeGI 当前实现说明

本文只描述当前代码，不把规划项写成已完成。目标平台是 RTX 4060，GI 预算约 3 ms；正式性能数字必须以 Development Player 的 GPU Profiler/RenderDoc 捕获为准。

当前产品范围仅包括 Nanite 物体与普通不透明 Mesh。植被、草、水、半透明、粒子和精确 Skinned Mesh 尚未进入闭环。

## 1. 系统是什么

```text
普通 Mesh triangle stream / Nanite live resident pages
                 -> Unified GI Scene
                 -> 8 级相机跟随 Sparse Clipmap
                 -> Static Layer + Dynamic Overlay
                 -> Occupancy / Surface / Conservative Distance
                 -> Directional Surface Radiance Cache
                 -> Diffuse reservoir final gather
                 -> Persistent Specular reservoir final gather
                 -> URP Deferred additive injection
```

这不是 NRC、DDGI 或 PRT，也不依赖 SSR、Planar Reflection、Reflection Probe。屏幕 HZB 只加速 diffuse 候选；镜面反射使用 world-space Clipmap trace。系统也不能宣称为完整 ReSTIR GI、ReGIR 或通用 GRIS。

## 2. 世界表示

- 8 级 Clipmap，逻辑分辨率均为 `128^3`，Brick 为 `8^3` Cell。
- Cell size 为 `0.2 / 0.4 / 0.8 / 1.6 / 3.2 / 6.4 / 12.8 / 25.6 m`。
- C0 单边覆盖 25.6 m，C7 单边覆盖 3276.8 m；所有层随相机按 Brick 滚动。
- 默认物理池为 8192 Static Bricks + 2048 Dynamic Bricks。
- Surface Cell 保存材质/法线、UV、当前实例加速索引、稳定 object/surface ID、winning triangle、六方向 RGB9E5 radiance 与 validity。
- Distance 是 Brick 内保守 unsigned distance，不是全局精确 SDF。

Static 与 Dynamic 分层。移动 contributor/occluder 会清理旧、新 footprint 并重建相关 Dynamic Bricks；receiver-only 物体不需要写入 Clipmap。

## 3. Clipmap 构建和 GPU 调度

`GIClipmapSystem.PrepareClipmaps()` 在 CPU 只完成场景快照、Clipmap origin、材质纹理和只读资源发布。逻辑页需求、物理页所有权、free-list、内容 hash、dirty/radiance queue 和 indirect args 全部由 GPU 生成。新增、删除、材质、Geometry revision 与 Transform 改变都进入逻辑页 content hash，不再保留 CPU required set、CPU footprint invalidation 或 CPU radiance queue。

GPU 完成：

- 8 级 required-page mask/hash、resident page 重投影和按细到粗的物理页分配；
- 由 content hash 驱动的 dirty physical-brick generation 标记；
- 静态/动态实例筛选；
- `uint2(instanceIndex, triangleBase)` voxel work queue；
- bounded indirect dispatch；
- 两阶段确定性 voxel winner：Phase 0 选 candidate key，Phase 1 提交完整 Surface payload；
- occupancy、surface、distance propagation 与 radiance update。

待重建物理页现在以 page-table 的负值编码为“已拥有但不可见”。只有进入本帧 `staticGeometryBricksPerFrame` / `dynamicGeometryBricksPerFrame` 硬预算的页才临时发布、清空并体素化；queue overflow 时会重新隐藏。旧实现每帧用 128 线程组扫描整个物理池，并在相机滚动时一次性清空所有 pending 页，实际绕过了 dirty budget，是移动时灾难性掉帧的来源。

完整 build 已进入 RenderGraph compute pass；材质数组本帧未更新且平台支持时可放到 Async Compute。Screen GI 读取相同 buffer，RenderGraph 负责插入依赖和 fence。

CPU 逐实例 voxel fallback 已移除，避免形成一个没有 GPU allocator 正确性保证的伪兼容分支。GPU queue 默认上限 65535 个 cooperative work item，每项由 64 线程分 16 轮处理最多 1024 个三角形，同样显存可覆盖约 6700 万三角形；indirect dispatch 支持 XY 展开，因此容量也可继续提高。第 4 个 indirect-args word 记录 overflow，第 5 个保存 accepted linear count，尾行不会读取陈旧任务；一次性 GPU forensics 会报告真实丢弃量。

## 4. 薄墙、细结构和三角形命中细化

Voxel Cell 现在保存 Phase 1 winning triangle index。World trace 得到 coarse hit 后，满足以下条件时执行受预算限制的 Möller-Trumbore refinement：

- 命中 C0/C1；
- receiver perceptual roughness 不高于 0.35；
- 本帧 `triangleRefinementRayBudget` 尚未耗尽。

细化优先遍历全场景二级 BVH：TLAS 是实例，普通 Mesh BLAS 的叶是 triangle，Nanite BLAS 的叶是 live resident page 内的 cluster/meshlet。Nanite 叶命中后直接读取 resident vertex/index/material-slot buffer 展开三角形，并验证：

- 当前实例与三角形范围；
- 单/双面规则；
- barycentric；
- Alpha Test；
- 最近合法 `t`。

成功后使用精确 triangle normal、barycentric、material slot 和 triangle UV 读取材质，避免近镜面仍使用 coarse Cell UV。失败的“本应细化”路径会被标记为 blocked，不退化成未经验证的天空样本。

Clipmap coarse hit 仍用于低成本定位距离区间；BVH 在该区间给出精确最近命中。若 BVH 没有可用叶，才回退到命中 Cell 的 3x3x3 winning-triangle 邻域。严格 temporal/spatial reconnection 也会先用 BVH 检查线段上的最近三角形及 exact stable ID。

## 5. 可靠 object/surface ID

每个 Surface Cell 保存精确 `uint2`：

```text
x = runtime object ID
y = hash(source Mesh/Nanite asset ID,
         local triangle index,
         geometry revision,
         instance content revision,
         Transform signature)
```

不再使用易因场景表重排而变化的 geometry table index。动态物体 Transform 改变会生成新 surface ID，从而拒绝连接到旧世界位置的历史路径。

Cell 仍保留 transient instance index 以快速读取 triangle stream。GPU page content hash 包含 object ID、当前 instance 内容/Transform revision、Geometry revision、材质 geometry revision 与 Nanite pool generation；场景表压缩或内容变化会让对应逻辑页变 dirty 并重建，避免旧 Cell 解引用到另一个对象。

这里的“稳定”指同一次运行、同一内容/Transform revision 内稳定。它不是跨 Editor 重启或跨重新导入资产的永久 GUID。

## 6. Path payload 与严格 reconnection

`GIPathPayload` 是统一的完整规范表示，包含：

- radiance、throughput、path length；
- solid-angle/proposal/target PDF；
- incoming/outgoing direction；
- hit position/distance、barycentric；
- source geometry/triangle/material/material-slot；
- geometric/shading normal；
- object/surface ID、instance/geometry revision；
- path flags/depth；
- reservoir W、M、age；
- visibility state/age。

当前 specular proposal 先构造该完整 payload，再序列化到 persistent reservoir。为控制 3 ms 预算，跨帧纹理只保存可重建的最小充分子集，不是把整个 struct 原样写入显存：

- radiance + source PDF；
- direction + distance（由上一帧 receiver origin 重建 hit position）；
- hit normal + path flags；
- W、M、target、age；
- exact `uint2` object/surface ID。

Diffuse 与 Specular 的所有有限 temporal/spatial reconnection 都必须：

1. 从当前 receiver origin 重新追踪 world Clipmap；
2. 命中相同 exact object/surface ID；
3. 到达原 hit distance 容差内；
4. 对 C0/C1 低粗糙度路径，在预算允许时再次做 triangle refinement。

预算耗尽时不把“没有拿到预算”误判为遮挡：receiver、endpoint stable ID 与最近一次验证仍匹配时，有限 diffuse 路径会有界延后复验；真正执行验证但失败、几何失配或 ID 失配才拒绝。天空路径不需要有限段 reconnection。`diffuseVisibilityRayBudget`、`specularVisibilityRayBudget` 与 `triangleRefinementRayBudget` 是独立硬预算；Trace Counter readback 会报告实际 attempt 数。

### 可见性预算与 4K 稳定性

- finite path 首次命中或重连后保存 exact stable ID，并缓存已验证 visibility；receiver 基本静止时先复用 4 帧，之后进入全屏均匀的预算化复验。未抽中预算的路径最多把验证 age 延长到 12，移动、已执行验证失败或 ID 失配会立即失效。
- visibility/refinement 候选先按像素、candidate 与 frame 做全屏均匀低差异门控，再由原子计数器执行最终硬上限。禁止按 dispatch 行顺序抢完预算，否则会形成随低分辨率宽度移动的水平明暗分割线。
- 4060/4K 当前基线为 diffuse visibility 16384、specular visibility 32768、triangle refinement 8192；Trace Counter 的 13/14/15 项用于确认实际开销。
- Screen diffuse finite hit 必须从包含它的 Clipmap Cell 取得 exact object/surface ID。拿不到 ID 时只允许作为本帧贡献，不能进入 persistent temporal/spatial reconnection。

## 7. Persistent Specular Reservoir

新的 specular 链路为：

```text
low-resolution GGX world proposal
 -> temporal reservoir + motion/world-space geometry validation
 -> strict finite-path visibility + exact stable ID
 -> spatial reservoir + strict reconnection
 -> full-resolution ratio resolve
 -> full-resolution temporal + deterministic five-tap rough reconstruction
 -> deferred specular injection
```

默认初始分辨率为 full resolution 的 0.375。每个 proposal 保存 source PDF、target、W/M 和完整 hit identity；temporal/spatial 合并限制有效 M、target ratio 与 merged weight，降低单个高能样本长期霸占 reservoir 的风险。roughness 大于 0.2 的 world proposal 使用两相 checkerboard，由 persistent reservoir 跨帧补齐；跳过的相位显式标记为“无新样本”，不会增加 M，也不会把 TOD sky 当成真实 proposal。近镜面保持逐像素逐帧追踪。原先额外的 full-resolution 随机 SpatialSpecular pass 已移除，其四邻域读取与 temporal 中已有的 five-tap neighbourhood 重复。

`specularIntensity` 在最终 Deferred 注入阶段应用，不写入 persistent history；因此调节强度立即生效，不会重新收敛或污染 reservoir。材质仍遵循 URP 的 occlusion 与 `SpecularHighlightsOff` 语义。

粗略显存量：在 4K、0.375 scale 下，specular reservoir 的双缓冲历史、current 与 spatial payload 合计约 187 MB；1080p 下约 47 MB。这里未包含 full-resolution resolve/history。数字按纹理格式理论计算，不等于实际驱动分配。

这仍不是完整多 bounce ReSTIR GI。当前 reservoir 对一跳 final-gather path 做时空重用；没有通用 path vertex chain、任意深度 reconnection 或 GRIS 域转换。

## 8. Diffuse 与 Surface Radiance Cache

默认 diffuse 不再使用 screen trace、reservoir、随机二次反弹或屏幕 history。它有两层明确分工：

- **Surface source cache**：只保存 `emissive + 材质颜色 × 可见方向光/点光/聚光` 的一跳出射 radiance；不采样
  TOD sky，不递归读取别的 cell，不执行随机多 bounce。
- **Free-space irradiance probes**：每 Brick 固定 `2×2×2` probe。每个 probe 沿六个固定轴向做 unified
  static/dynamic Clipmap trace；命中时读取上述 source cache，真正 world miss 才读取 analytic TOD sky。
  屏幕端只查询这个 field，完全不读 Surface source cache。

删除这些旧路径的理由是可验证的：它们让同一 Brick 在 round-robin 更新时得到不同随机 sky/multi-bounce 值，再被直接
扩散成可见的白块、闪烁和 cluster 分界。`ClearBrickRadiance` 同时清空 source radiance 与 free-space probe payload/validity，
因此物理 Brick 被重新分配到新世界位置时不会显示旧位置的 irradiance。

本阶段仍未达到“零漏光”验收：probe relocation 尚未持久化，且没有 directional distance moments/receiver-to-probe
visibility。它们只能建立在本节的确定性 source/probe 数据链通过稳定性 review 后；不能重新引入随机屏幕 history 作为补丁。

## 9. 材质与更新语义

GI Scene ABI 当前为 v5；`GIInstanceData` 仍为 176 bytes，原 padding word 改为 Transform signature。材质数据固定为 176 bytes，支持：

- BaseColor/BaseMap；
- Emission/EmissionMap；
- Normal Map；
- Mask Map；
- metallic/roughness/opacity channel 与 remap；
- Alpha Test、Double Sided、独立 UV ST；
- URP Lit 自动适配；
- `GIMaterialBridge.ExplicitStylizedProxy`。

项目自定义 Shader 应显式提供 `_GIProxyEnabled` 与 `_GIProxy*` 属性，或使用 `GIMaterialBridge`。GI 不会猜测未知 stylized/Ramp 光照语义。

更新分类：

- Base RGB、Emission、roughness/metallic、灯光/TOD：更新 radiance/material，不必重建 geometry；
- Normal、Alpha Test、opacity/cutoff、真实 dissolve hole：提高 geometry revision，重建受影响 Brick；
- Mesh topology、Transform、contributor/occluder 启停：重建旧/新 footprint；
- receiver-only 材质只影响 Deferred 接收，不写 Dynamic Clipmap。

## 10. Nanite 关系

Nanite 提供 `NaniteResidentPageReadOnlyView`，包含 published front table、resident vertex/index、逐 triangle material slot、page/resident table、residency bits 与 generation 等只读信息。

GI voxelization 与 BVH refinement 直接消费该 live resident page；每次读取都验证 page/resident generation、resident flag、geometry-ready flag、residency bit 与预期 mesh index。Nanite 顶点和索引不再复制到 GI-owned stream。Nanite 侧只增加稳定只读 export，所有权、换页和写入仍归 Nanite renderer。

## 11. 当前限制和下一步

尚未完成：

1. RTX 4060、目标场景、约 3 ms 的正式 GPU capture 与压力曲线；
2. BVH 的 GPU build/refit、压缩节点与 wave traversal；当前 BLAS/TLAS 在内容/Transform revision 时由 CPU 构建或 refit，GPU 负责遍历；
3. 跨运行永久 object GUID，以及普通 Deferred receiver 的精确 object ID GBuffer；
4. 植被、水、半透明、粒子、精确 Skinned Mesh；
5. Area Light、Cookie、IES、项目 Ramp Point/Spot 正式 ABI；
6. 完整 ReSTIR GI、ReGIR、通用 GRIS。

验收至少记录：

- `RealtimeGI/Clipmap Total`、Voxelize、Distance、Radiance；
- Diffuse Screen/World/Temporal/Spatial/Denoise；
- Specular Coarse Trace/Triangle Refine/Reservoir Temporal/Reservoir Spatial/Resolve/Temporal；
- 视角快速转动、薄墙两侧、细杆、粗糙度 0/0.1/0.35；
- 动态实例缓慢移动、瞬移、删除/新增实例；
- queue overflow、strict visibility attempts、triangle refinement attempts；
- 1080p、1440p、4K 的 GPU ms 与显存。

## 12. 关键文件

| 职责 | 文件 |
|---|---|
| 场景/ABI | `Runtime/RealtimeGIScene.cs`, `Runtime/GISceneTypes.cs`, `Shaders/GISceneCommon.hlsl` |
| Mesh/Nanite 适配 | `Runtime/GISceneSnapshotBuilder.cs`, `Runtime/GIGeometryStreamCache.cs` |
| Clipmap allocation/调度 | `Runtime/GIClipmapSystem.cs`, `Runtime/GIClipmapLayer.cs` |
| Voxel/distance/GPU work queue | `Resources/RealtimeGI/GIClipmapBuild.compute` |
| Surface Radiance Cache | `Resources/RealtimeGI/GIRadianceCache.compute` |
| Diffuse/specular/path/reservoir | `Resources/RealtimeGI/GIScreenLighting.compute` |
| URP RenderGraph/合成 | `Runtime/RealtimeGIRendererFeature.cs`, `Shaders/GIComposite.shader` |
| Nanite 只读 export | `Assets/Scripts/Nanite/NaniteResidentPageReadOnlyView.cs` |

## 13. 参考资料

- Müller et al., *Real-time Neural Radiance Caching for Path Tracing*, 2021：`mueller21realtime.pdf`
- [NVIDIA RTXDI / ReSTIR GI](https://github.com/NVIDIA-RTX/RTXDI/blob/main/Doc/RestirGI.md)
- [NVIDIA NRD](https://github.com/NVIDIA-RTX/NRD)
- [NVIDIA RTXGI-DDGI](https://github.com/NVIDIAGameWorks/RTXGI-DDGI)
- [NVIDIA Falcor](https://github.com/NVIDIAGameWorks/Falcor)
- [Unreal Engine Lumen Technical Details](https://dev.epicgames.com/documentation/en-us/unreal-engine/lumen-technical-details-in-unreal-engine)
- [AKGI UE 4.27 实现](https://github.com/AKGWSB/UnrealEngine/tree/4.27-akgi)
- [RealtimeGI 实战篇（上）](https://zhuanlan.zhihu.com/p/12632657244)
- [RealtimeGI 实战篇（下）](https://zhuanlan.zhihu.com/p/12636727339)
- [Neural Radiance Cache](https://zhuanlan.zhihu.com/p/388120500)
- [Surfel-based Radiance Cascade GI](https://zhuanlan.zhihu.com/p/2062594335728784841)

参考代码仅用于公司预研、个人学习与技术实验；进入产品前需单独完成许可证审查。

## 14. 2026-08-09 风格化项目的架构收缩决议（待实施）

目标画面是动态光源驱动、卡通纯净、室内外连续的低频漫反射染色；不追求实时路径追踪的高频镜面。
默认 GI 的首要指标因此是镜头运动稳定、能量受控和固定预算，而不是以每像素随机射线加历史积累换取细节。

### 本地 Lumen-Like 审阅结论

不整合其代码。`LumenLike.cs` 的主体是 SEGI 风格完整稠密体素化：默认 256^3、可选 512^3，维护
3D ARGBHalf mip chain、额外 ping-pong volume、secondary irradiance 和 RInt occupancy，并通过辅助
voxel/shadow camera 的 `RenderWithShader` 工作。仅 256^3 时，这几组主 volume 的静态显存已约 466 MiB
（未计屏幕 RT）；512^3 约为八倍，且 conventional camera draw 不能直接复用 Nanite 的 procedural VBuffer 几何。

它所谓 `SurfaceCache` 是半分辨率的屏幕空间 radiance/normal/depth history，不是世界空间 card cache。更重要的是，
`SurfaceCache.shader` 虽定义 `AccumulateHistorySample`，实际 update fragment 没有调用它，只把 `_BlitTexture`
返回；不能把它视作经过验证的 temporal surface cache。该项目仍有 `doReflections`、`reflectionSteps` 和
`skyReflectionIntensity` 的 SEGI 镜面/天空路径，也不符合“GI 接管后不再依赖 Reflection Probe/Unity sky”的契约。

可借的是产品级取舍：体积表示、半/四分辨率、双边上采样和以低频为目标；不是它的辅助相机、整块 voxel volume 或代码实现。

### 拟替换的默认路径

```text
Nanite geometry/material stream + TOD/local lights
                  │（dirty brick budget）
                  ▼
  sparse world-space radiance / irradiance clipmap
                  │
       ┌──────────┴──────────┐
       ▼                     ▼
 full-res diffuse query   optional quarter-res glossy cone
       │                     │
 trilinear + normal leak  cache hit / TOD analytic miss
       └──────────┬──────────┘
                  ▼
           deferred additive composite
```

- **Diffuse（默认）**：删除默认每像素 world/screen path trace、reservoir、screen history 和空间重采样；
  像素只查询 sparse clipmap 的低频 irradiance，并做 normal/validity-aware trilinear blend。漫反射的收敛
  发生在世界 cache 的 dirty brick budget 内，移动相机不再重置结果。
- **Specular（可选）**：只保留 quarter-resolution 的单一宽锥/短步 cache trace，输出低至中频 glossy 信息；
  miss 只使用 analytic TOD，绝不读 `unity_SpecCube*`、`_GlossyEnvironmentCubeMap` 或 Reflection Probe。
  它不是高频反射替代品；高频 RTX reflection 只能作为单独的质量选项。
- **能量与风格**：cache 只存 diffuse outgoing radiance，固定 `1/pi`、一跳或严格衰减的二跳、每 brick luminance
  clamp；禁止把 specular lobe 写回 diffuse cache。这样红/青 emissive 只在有限、可解释的空间范围内着色。
- **动态性**：光源/几何变化只使相交 brick 失效并分帧重算；静态 cache 保留。TOD 参数变化以量化阈值触发受影响层更新，
  不以屏幕历史承担动态光响应。

### 必须先达到的验收

1. 关闭所有 Reflection Probe 与 Unity Environment Reflections 后，GI enabled 的 Nanite 间接镜面不变；关闭 GI 后只剩 TOD analytic fallback。
2. 固定相机 1/8/30/60 帧及小幅平移后，diffuse 不能回到初帧噪声，也不得形成屏幕中心线或跨屏雾带。
3. 红、青、白三种 emissive/动态 light 的 ROI 色溢出在 cache 更新完成后收敛到稳定半径，60 帧后不能继续扩张或穿过遮挡面。
4. smoothness=0/0.5/1 三材质都得到非黑的可控低频镜面；高 smoothness 不以全黑或 Unity skybox 作为错误 fallback。
5. 性能评估使用 4K Development Player，关闭 frame-debugger/trace readback。默认模式不允许执行 per-pixel diffuse world trace；
   只报告 clipmap update、diffuse query、optional glossy 三项 GPU 时间。

### 删除优先的实现约束

新实现不是在旧 `GIScreenLighting` 管线旁增加一个“Low Frequency”开关。默认路径接管完成后，必须从运行时代码、
renderer settings、RenderGraph pass、history allocation、shader kernel 及调试统计中一起删除下列旧路径：

1. diffuse screen trace、compact world miss、temporal/spatial reservoir、screen history 与 A-trous 收敛链；
2. specular screen hit、triangle refinement、temporal/spatial reservoir、scene-color mip history；
3. `unity_SpecCube*`、`_GlossyEnvironmentCubeMap`、Reflection Probe 和 Unity Environment 作为任何 GI fallback；
4. 为掩盖上述路径失败而返回白/黑/旧 history 的 silent fallback。

允许的失败方式只有显式的：GI resource 或 world cache 无效时输出零 indirect，并通过一次性错误/Frame Debugger pass
显示原因；不得用未经验证的 sky、probe、history 或伪随机颜色替代。旧路径在新 default path 通过本章验收前可以保留在
工作树中以供迁移，但不能同时运行；通过后立即删除，不保留兼容开关。

## 15. 2026-08-09 已确认的产品规格与验收边界

本章是实现的硬约束，不是调参建议。若某个实现无法满足其中任一项，应替换该实现，而不是以更多历史、随机采样、
白/黑 fallback 或隐藏开关掩盖它。

### 平台、预算与相机

- 目标 GPU 为 RTX 4070 Ti，输出 1080p；**默认 GI（clipmap 更新、diffuse query、可选 glossy query 的合计）GPU
  时间上限为 3.0 ms，目标为 2.0 ms**。该数据只在 Development Player 中测量，关闭 Frame Debugger、AsyncGPUReadback
  和 Editor profiling 干扰。
- 摄像机是会频繁移动/自由旋转的第三人称机位，后期主要受限于俯视；没有持续的大位移，但过场可发生镜头切换。
  因此 camera motion 不是 GI history 的失效条件；镜头切换只能重建屏幕临时资源，不能使世界 GI 回到噪声初态。
- 世界同时包含室内外，工作尺度可达 10 x 10 km。全世界不得以一个稠密 volume 常驻；采用围绕活动区域的 sparse
  clipmap，并以空间预算而非屏幕像素数控制更新成本。
- 光源和可动几何的间接光可延迟，但从变化到稳定不得超过约 1 s。该限制由 dirty brick 调度保证，不由无限 history
  weight 伪装。

### 光传输与风格

- 默认目标是一跳、低频 diffuse transport：保持明确的直射亮暗边界和 shadow 关系，GI 只抬升/着色合理的暗部，不能
  把暗面洗到接近亮面。二跳只在有严格能量衰减、固定预算且能使室内更均匀时启用；不是默认承诺。
- 环境贡献、反弹色和亮/暗区的 GI 强度必须能分别美术控制。未来室内/室外 classification 尚未确定，当前 TOD 只能
  作为**有可见天空路径的世界 miss**；不能以 TOD 直接给封闭室内上色。以后可在同一接口接入室内环境 profile。
- emissive 是点缀光源，必须有单材质/单 brick 能量上限和影响半径；不能无限扩散或用极端 luminance 换取可见性。
- 漏光容忍度为零，尤其是墙角、遮挡物后方和动态物体经过的区域。稳定性优先于过快变亮：更新中的 brick 可以保留
  旧的可信解并受连续性限制，但绝不能突然切为黑、白或无关颜色。

### 物体、灯光和镜面

- 方向光、点光、聚光是首要支持对象；设计目标为 20 个以内本地灯固定成本可控。使用 per-brick light list，禁止把
  全灯表逐像素遍历；若 100 灯场景超预算，必须在统计中明确显示溢出/裁剪，而非静默退化。
- 角色和动态物体必须既接收又投放 GI。静态 Nanite 是主要场景输入；动态 Nanite 和一般 Mesh 以 dirty brick 更新接入，
  skinned/transparency 的详细策略在其首个可验收实现时确定，不能假装已经支持。
- 镜面需要金属/磨砂金属的低至中频反射、smoothness=1 时仍保留可辨识的物体/光源轮廓，但不要求清晰镜像。默认
  所有 RTX on/off 设备给出一致的低频结果；RTX 只能额外补高频，不能成为 correctness 的前提。
- GI enabled 时任何间接 diffuse/specular 都不得读取 Reflection Probe、`unity_SpecCube*`、Unity environment 或默认 skybox。
  GI disabled 时，原生反射可以恢复，作为项目临时替代方案。

### 必须提供的调试与三类验收场景

GPU 调试项至少包括 brick occupancy、dirty/update queue、irradiance、leak/occlusion、每灯影响范围、以及 clipmap
update/diffuse/glossy 的分项 GPU 时间。无效资源必须显式显示为 zero indirect 和错误原因。

1. **室内暖光**：颜色在 1 s 内稳定，物体遮挡 probe/field 不得造成局部“啪”地变暗或移开后缓慢回血；墙角无漏光。
2. **室外 TOD**：阳光的明暗交界清楚，暗部可收环境/反弹色但不可被抹平；天空色只可经过可见天空路径进入。
3. **动态灯与动态物体**：更新连续、无块状闪烁或跨屏噪声；镜头移动后不需要重新积累世界 GI。

## 16. 实施状态

### 阶段一：世界缓存 diffuse 默认路径（2026-08-09，待场景 review）

已删除 `RealtimeGIRendererFeature` 中的 screen trace、HZB、world-miss queue、camera history、scene-color mip、
diffuse/specular reservoir、temporal/spatial/A-trous 以及它们的 trace readback。`GIScreenLighting.compute` 现在只有
`GatherWorldDiffuse`：每个可见 receiver 在当前世界位置的 sparse clipmap 邻域作 deterministic trilinear gather，
没有随机数、跨帧屏幕数据或 Unity/Probe/sky fallback。旧 GPU monitor 的 ray/screen-hit 指标也同步删除，改为
clipmap、world diffuse、world glossy 三项时间。

这个阶段**尚未实现 glossy**；compute 明确输出零镜面，不能把现有 Unity IBL 当作替代。它的目标是先验证世界
diffuse 数据链、画面移动稳定性和旧依赖的确实移除，而不是宣称漫反射质量已经达到最终标准。

本地验证：`dotnet build RealtimeGI.csproj --no-restore` 通过（0 warning / 0 error）；Unity Editor 已重新导入 compute
shader，修复过一次缺失的 `GIUnpackSurfaceNormal` 后，日志中没有新的 `GIScreenLighting` shader error。Renderer asset
目前 `m_Active: 0`，未被自动启用，避免未 review 的阶段一改变项目画面。

### 阶段一 review 方式

启用 `PC_Renderer` 内的 `RealtimeGIRendererFeature` 后，固定相机等待 clipmap 工作队列稳定，再缓慢旋转/平移。
预期：没有中心线、没有相机相关的重新积累、没有 Reflect Probe/Unity sky 造成的镜面；当前镜面应为零，diffuse
可能偏弱或过局部，这属于下一阶段要解决的真实性问题。若出现 shader/RenderGraph 错误，需先记录完整 Console 和
Frame Debugger 的 `RealtimeGI/World Cache Diffuse`，不能以开关或 fallback 绕过。

### 阶段一 review 结论（2026-08-10：否决 gather 算法，保留清理成果）

场景 review 证明旧屏幕路径已不再参与：输出没有 screen history 的中心线/重积累，红色 emissive 也确实能让相邻物体
接收红色间接光。但 `GatherWorldDiffuse` 的表示选择错误，不能进入产品。

`GIRadianceCache` 的每个 occupied cell 存的是**表面出射 radiance**：它已包含该表面自己的方向光、local light、
可见 TOD sky、材质颜色和 emissive。`GatherWorldDiffuse` 把 receiver 上方八个 cell 的这些值直接作 trilinear
normalised average，等价于把“一个由某个 winning surface 代表的体素”当成自由空间 probe。结果是：

1. receiver 自身或极近的、直接受光的 cell 可能重新注入其 direct lighting；
2. 八 cell 的主导 surface 在 Nanite cluster/voxel 边界改变，故出现按 cluster 分块的颜色和亮度；
3. gather 没有 receiver 到 sample 的 visibility，遮挡面后仍可能拿到邻近 surface 的能量；
4. 因为 cell radiance 含 TOD/direct 的白色成分，白色材质会被不正确地强化。

这不是调 `_GIDiffuseIntensity`、加 history 或扩大 blur 能修好的问题。下一实现改为每 brick 的 sparse free-space
irradiance probes：probe 只存自身位置的入射光，更新时使用固定世界 trace/visibility；屏幕只 trilinear 查询 probe
field，必要时做 receiver-to-probe leak test。表面 radiance cache 继续作为 probe trace 的 hit radiance source，
但不再直接作为 receiver 的体积数据。

### 阶段一.五：free-space irradiance probe field（2026-08-10，待场景 review）

已删除被否决的 surface-cell gather。每个 dirty physical brick 在 surface radiance commit 后生成固定 2x2x2
free-space probes；probe 先在七个确定候选 cell 中离开 occupied cell，再向六个轴向做 unified static/dynamic
clipmap trace。trace hit 只读取**命中表面**的 outgoing radiance，world miss 才读取 analytic TOD sky。全分辨率 pass
仅对这个 probe field 进行三线性查询和法线方向积分，没有 history、reprojection、screen trace 或随机数。

本步 review 预期是：Nanite cluster 的蓝/白/黑 surface-cache 拼图消失；red emissive 仍以平滑、低频方式染到相邻物体；
移动相机不触发 GI 重积累。它还不是漏光验收完成：探针 relocation 尚未持久化，且尚无 directional depth moments/
receiver-to-probe visibility。若本步通过画面 review，下一步只做这些 leak-control 数据，不会先加入 glossy 或 denoising。

### 阶段一.五 review 修复：去除随机 source 与旧 probe 残留（2026-08-10，待 review）

该版本第一次场景 review **未通过**：画面出现每秒亮度跳变、无白色 emissive 时的局部爆白，以及 free-space field
本身的不均匀。根因不是艺术强度，而是两条数据完整性错误：

1. Surface source cache 仍在执行随机 secondary-bounce 与随机 sky-visibility 估计；同一个 Brick 的 round-robin 更新
   写出不同能量，随后 probe 立即把它传播到画面。
2. `ClearBrickRadiance` 清掉了旧 Surface source，却没有清掉同一物理 Brick 的 probe payload/validity；Brick 重分配时
   新世界位置可能短暂采到旧位置的 irradiance。

修复删除了 secondary-bounce、随机 sky visibility、对应 Inspector 字段、shader 参数、签名项和无效的 static-bounce
遗留字段。Surface source 现为确定性的 `emissive + direct directional/local response`；TOD sky 仅在 probe ray 的真实
world miss 进入。物理 Brick 清理时同步清空 probe 值与有效位。一次性 GPU forensics 额外输出每层有效 free-space
probe 数、首个 probe 的非零 lobe 数与解码后的最大 RGB，以便把后续异常明确归因到 field 数据，而非后处理。

本地 `dotnet build RealtimeGI.csproj --no-restore` 已通过（0 error；3 个现有 Inspector 统计字段的 CS0414 warning）。
下次 review 只检查这三个可证伪结果：固定机位 10 秒无周期跳变；无白 emissive 区不再突然爆白；移动/转动相机不会重置
世界 GI。若仍失败，先读取新的 forensics 数值再定位，不添加 blur、亮度 clamp 或 hidden fallback。

### 阶段一.五 review 修复二：分离几何帧与辐照版本（2026-08-10，待 review）

再次审计 GPU 队列发现了一个独立、确定的持续更新错误：`_GIWorkGeneration` 每帧递增，本来只用于识别本帧的几何 dirty
page，却同时被 `GIAppendRadiance` 当作“该物理 brick 已更新辐照”的版本。因此在无几何、无光照变化的静态场景中，
`BuildRadianceWorkQueue` 仍会按 round-robin 每帧重算所有 resident brick；这既浪费预算，也会把任何 source 误差持续带回
画面。现在它们是两个 token：

1. `_GIWorkGeneration` 仍只管理 page/voxel dirty 生命周期；
2. `_GIRadianceGeneration` 只在量化后的光照签名改变时递增；静态未变化的 brick 不再进入 radiance queue；
3. 新分配或被重新 voxelize 的 brick 会显式清除自身 radiance-generation，并强制更新一次；
4. source cache 的内容 identity 也包含 lighting revision，因此真实灯光改变时不会把旧光和新光按 history 混合。

这一步的预期不是让 GI 变亮或靠 clamp 隐藏异常，而是让固定相机、固定 TOD 下的 `RealtimeGI/Radiance Update` 在初始填充后
降为近零工作量，并且画面不再按循环周期更新。真正的 TOD/灯光变化仍会分批重算 resident bricks；是否在用户给定的 1 秒
更新上限内，由下一轮 GPU 队列统计和场景录像测量后决定。
