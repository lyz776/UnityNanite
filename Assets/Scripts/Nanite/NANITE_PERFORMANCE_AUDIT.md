# Nanite 性能审计（2026-07-27）

## 当前结论

本阶段暂停 Page traversal、软件光栅、Mesh Shader 与 RT 功能扩展，先处理现有热路径。

最新 capture：`ProfilerCaptures/NN_2026-07-27_18-12-52.data`，共 1532 帧。

- Render Thread `GfxDeviceD3D12.WaitForGPU`：平均 4.075 ms，1525/1532 帧出现。
- Main Thread `WaitForLastPresentation.WaitForGPU`：平均 1.764 ms。
- `Nanite.CPU.FirstCull`、`GpuCullDispatch`、`ResolveSubmit`、`ShadowSubmit` 均低于 0.1 ms。
- `BufferD3D12.UpdateInternal` 合计约 0.026 ms/帧。
- `GraphicsFence`、同步 `GetData`、Page readback 没有出现在主要等待调用链中。

这些等待由 Editor 的帧末/present fence 观察到，虽然计入 CPU Active，但不是 C# Nanite 逻辑耗时。它们表示 CPU/Render Thread 已经提交完并等待本帧 GPU 工作完成；capture 本身没有有效 GPU timestamp，不能仅按 CPU Active 将瓶颈判为 CPU。

## GPU 队列实测

使用一次性 `AsyncGPUReadback` 探针读取 camera 与四个 shadow cascade 的 indirect count/cluster queue；探针没有同步阻塞热路径，验证后已删除。

修复前：

| View | Visible clusters | 有效三角形 | 实际提交三角形 |
|---|---:|---:|---:|
| Camera | 24,244 | 2,965,656 | 3,103,232 |
| Shadow 0 | 20,932 | 2,566,006 | 2,679,296 |
| Shadow 1 | 26,622 | 3,260,518 | 3,407,616 |
| Shadow 2 | 26,673 | 3,267,046 | 3,414,144 |
| Shadow 3 | 26,673 | 3,267,046 | 3,414,144 |
| 合计 |  | 15,326,272 | 16,018,432 |

固定 128-triangle cluster slot 只造成约 4.5% padding；主要问题是四级阴影重复使用 camera-space LOD，尤其 Shadow 2/3 完全提交同一批高细节 cluster。Procedural fallback 每个三角形执行三个顶点线程且没有普通 indexed mesh 的 post-transform cache 收益，修复前约有 4805 万次 VS invocation/帧。

## 已实施修复

`NaniteRuntimeCulling.compute` 与 `NaniteGpuBatchedCullingBackend.cs` 已改为：

- 每个 directional cascade 使用自己的正交投影矩阵与 tile resolution 计算 texel density。
- Part/Cluster LOD 分别按各 cascade 的 shadow texel error 选择，不再复用主相机像素误差。
- 每个 cascade 独立执行 sub-pixel cluster rejection。
- 普通 camera LOD 计算补上 instance `maxScale`；大工作集的逐 cascade fallback 同样使用正交 shadow LOD。

修复后同场景异步复测：

| View | Visible clusters | 有效三角形 | 实际提交三角形 |
|---|---:|---:|---:|
| Camera | 24,244 | 2,965,656 | 3,103,232 |
| Shadow 0 | 15,596 | 1,829,596 | 1,996,288 |
| Shadow 1 | 15,815 | 1,796,615 | 2,024,320 |
| Shadow 2 | 11,268 | 1,263,924 | 1,442,304 |
| Shadow 3 | 8,604 | 963,912 | 1,101,312 |
| 合计 |  | 8,819,703 | 9,667,456 |

总有效三角形下降 42.5%，shadow 有效三角形从 12,360,616 降至 5,854,047（下降 52.6%）；camera 几何量未变化。

## 下一步门槛

1. 同一 12 车、4K 视角验收阴影边界、远级稳定性与 FPS/Main/Render。
2. 若三角形下降能转化为明显帧率收益，继续针对 procedural vertex amplification：优先 Mesh Shader/cluster-local vertex reuse，软件光栅只接管微三角形。
3. 若帧率仍无明显变化，必须取得 Development Player GPU timestamp 或 PIX/RenderDoc pass timing，再在 VBuffer raster、material resolve、shadow raster 中定位；不继续优化 CPU 小项或扩展 Page/RT 功能。

## 级联 LOD 验收

用户已确认近、中、远级阴影均正常。12 车 4K 截图约为 236.7 FPS、Main 4.2 ms、Render Thread 3.8 ms；此前相近视角约 220～225 FPS。级联独立 LOD 因此保留，但 42.5% 总三角形下降只转化为约 5～8% 帧率收益，剩余热区不能再归因于 CPU culling 或三角形 padding。

## Indexed Cluster Raster（实机 A/B 通过）

审查 Unity 6 RenderGraph API 后确认，`RasterCommandBuffer` 与 `UnsafeCommandBuffer` 已提供带 `GraphicsBuffer indexBuffer` 的 `DrawProceduralIndirect`，不需要在 RenderGraph 外调用即时 `Graphics.*`。

本轮实现：

- 保留现有 GPU visible-cluster queue 与 VBuffer `(instanceId, triangleId)` ABI。
- compute 把每个可见 cluster 展开到临时 32-bit index buffer；索引编码为 `instance * geometryVertexCount + geometryVertex`。
- VS 从 `SV_VertexID` 恢复 instance 与共享 geometry vertex，使固定功能 post-transform cache 能在 cluster 内复用顶点。
- Formal VBuffer PS 用 `SV_PrimitiveID` 恢复原 triangle ID，Resolve、材质映射和 Page ABI 不变。
- Depth、Formal VBuffer、四级 Shadow 均接入 indexed draw。
- 默认总临时索引预算 128 MiB；四级阴影各占四分之一切片，阴影完成后 camera 复用整块。
- 当前实测 queue（camera 24,244 clusters，最大 shadow 15,815 clusters）低于默认容量（128-triangle cluster 时 camera 约 87,381、每级 shadow 约 21,845）。
- 超容量时 compute 会令 indexed args 为 0、procedural fallback args 非 0；不读回 CPU，也不会因预算不足漏几何。

预期收益来自把原来“每三角形三个 procedural VS invocation”改为 indexed vertex reuse；新增代价是每视图生成索引的 compute/显存写入。是否净赚必须以相同 12 车视角的 FPS 与 GPU capture 验收，不能仅凭提交三角形数判断。

### 2026-07-27 实机结果

- 场景：12 个 Nanite proxy，4K（3840×2160），四级主光阴影。
- Console：`submit=indexedClusterIndirect`、`cascades=4/4`、`instances=12`、`geometrySource=residentCache`，0 warning / 0 error。
- Game Stats：309.2 FPS（3.2 ms），CPU Main 3.2 ms，Render Thread 2.1 ms，43 batches / 42 SetPass。
- 上一轮相近 4K 视角：236.7 FPS、CPU Main 4.2 ms、Render Thread 3.8 ms。
- 帧率提升约 30.6%（+72.5 FPS）；帧时间从约 4.23 ms 降至约 3.23 ms（下降约 23.5%）。
- Profiler 稳定帧：CPU Active 4.433 ms、GPU 3.421 ms；`GfxDeviceD3D12.WaitForGPU` 2.713 ms，presentation wait 1.646 ms。另有少量 Editor/Profiler 峰值帧，不作为 steady-state 基线。

结论：此前主要热区确实是 procedural vertex amplification，而不是 C# culling、CPU/GPU 通信或 128-triangle slot padding。Indexed cluster raster 保留为 D3D12/Unity 6 当前正式硬件路径。

### 被否决的跨 Pass 索引复用

曾尝试让 Formal `pass1` 直接复用 Depth 生成的 camera index slice，以省去约 310 万个索引的第二次写入。实机结果从约 309 FPS 降至约 215 FPS；即便关闭 indexed 路径仍更低，且 capture 中出现 GPU 9～12 ms 与更长的 RenderLoop 峰值。该变更已立即撤销。

结论：当前 Unity 6 / D3D12 RenderGraph 下，跨 Depth 与 Formal raster pass 延长动态 index/indirect buffer 生命周期会造成比重建更重的资源状态/队列依赖。正式路径保持“消费前就地重建”，不再凭静态重复工作量推断性能。

## Windows Player GPU Profiler（2026-07-28）

有效 capture：`ProfilerCaptures/UnityNanite_2026-07-28_00-55-41.data`，Player 连接为 `lyz-pc - UnityNanite`，目标 4K、12 车、四级主光阴影。配套文件包括 `.png` 和 `.highlights`。

本次 Player 日志确认 Windows 回归已解除：无 `CSClusterCullVisibleParts` 9-UAV 报错，无 runtime shader `MISSING`；`Formal VBuffer raster` 与 `Formal Resolve` 均入图，`submit=indexedClusterIndirect`、`geometrySource=residentCache`、`cascades=4/4`。

Profiler 截图中的代表帧约为 CPU 6.56 ms、GPU 2.38 ms；选区 median frame time 约 2.62 ms，max 13.13 ms。GPU Hierarchy 代表帧显示主要 Nanite/URP pass：

| Marker | GPU ms |
|---|---:|
| `Nanite/FormalVisibility` | 0.808 |
| `Graphics.DrawProcedural` under FormalVisibility | 0.702 |
| `Draw Main Light Shadowmap` | 0.485 |
| `Nanite/WriteDepth` | 0.465 |
| `Nanite Main Light Shadow Cull` | 0.191 |
| `Bloom` | 0.071 |
| `RG_UberPost` | 0.053 |
| `Draw GBuffer` | 0.044 |
| `Render Deferred Lighting` | 0.038 |
| `Nanite/BuildHzb` | 0.034 |
| `CopyDepth` | 0.022 |

初步结论：该 Player capture 首次提供了有效 GPU timestamp。当前热区集中在 Formal VBuffer raster、WriteDepth 和主光阴影，cull/HZB 很小；CPU Timeline 的长条主要仍是 `DXGI.WaitOnSwapChain` / render-thread 等待，不应作为 Nanite C# 热点处理。下一步需要同一视角普通 Mesh A/B capture，判断 indexed Nanite 相对 Unity Mesh renderer 的真实 GPU 成本差距；若继续优化 Nanite，优先看如何合并或减少 Depth/Formal 的重复 raster，以及 shadow/depth 的 indexed draw 成本，而不是扩展 Page traversal 或 CPU 管线。

### Ordinary Mesh A/B（2026-07-28）

普通 Mesh 对照 capture：`ProfilerCaptures/UnityNanite_2026-07-28_01-04-22.data`，同为 4K、12 车、四级主光阴影。Player 日志显示 `externalNanite=False`，说明该 capture 没有 Nanite 外部 shadow caster 与 Formal VBuffer pass 介入。

普通 Mesh Editor/Game Stats 约为 521 FPS（1.9 ms），CPU main 1.9 ms、render thread 1.1 ms，64 batches、27 SetPass，Triangles 12.8M、Vertices 9.6M。Profiler 选区 median frame time 约 1.470 ms、max 6.492 ms、min 1.182 ms；代表帧 CPU 1.34 ms、GPU 1.32 ms。GPU Hierarchy 代表帧：

| Marker | GPU ms |
|---|---:|
| `Draw Main Light Shadowmap` | 0.676 |
| `Draw GBuffer` | 0.432 |
| `Bloom` | 0.069 |
| `Render Deferred Lighting` | 0.037 |
| `CopyDepth` | 0.020 |
| `DrawSkybox` | 0.014 |
| `CopyColor` | 0.007 |
| `Blit Color LUT` | 0.003 |

A/B 结论：

| Capture | Median frame | Representative GPU | Camera geometry | Shadow |
|---|---:|---:|---:|---:|
| Nanite indexed | 2.621 ms | 2.38 ms | `FormalVisibility` 0.808 ms + `WriteDepth` 0.465 ms | 0.485 ms + Nanite shadow cull 0.191 ms |
| Ordinary Mesh | 1.470 ms | 1.32 ms | `Draw GBuffer` 0.432 ms | 0.676 ms |

普通 Mesh 尽管提交的统计三角形更多，但 SRP Batcher + 常规 indexed mesh 的 camera path 只需一次 GBuffer raster；Nanite 当前要执行 depth prepass、Formal VBuffer raster 与 resolve/后续 GBuffer 合成，单 camera 几何成本已经接近或超过普通 Mesh 全帧 GPU。主光阴影方面，Nanite 的纯 shadow raster 比普通 Mesh 低，但加上 Nanite shadow cull 后差距变小，整体瓶颈仍主要是 camera path 的重复 raster/resolve，而不是 shadow 或 cull。

下一步优化优先级：

1. 优先减少 camera path 的重复几何工作：评估是否能让 Formal VBuffer 同时产出可用于后续深度测试的 depth，或在 URP Deferred 下跳过独立 `Nanite/WriteDepth`，避免 Depth + Formal 双 raster。
2. 评估 Formal Resolve/GBuffer merge 成本是否能并入更少的 full-screen pass；普通 Mesh 的优势来自直接写 GBuffer，而 Nanite 现在多了一次 VBuffer 解码/材质恢复。
3. Shadow 只作为第二优先级：当前 ordinary mesh shadow 0.676 ms，Nanite shadow raster 0.485 ms、cull 0.191 ms，收益空间小于 camera path。
4. 暂停 Page traversal、RT、软件光栅与 CPU 侧管线扩展；这些不解决本次 A/B 暴露的主要 1.0 ms 级差距。

已切换下一轮验证配置：`Assets/Settings/PC_Renderer.asset` 将 `useHzbCulling` 设为 `0`。现有准入逻辑会因此跳过 `Nanite/WriteDepth`、`Nanite/BuildHzb` 与 `Nanite/SecondCull`，Formal raster 仍写入 VBuffer/Depth，作为“Formal-only camera path”A/B。下一次 Nanite Player capture 需确认日志不再出现 `hzbActive=True` 或 `Nanite/WriteDepth`，并比较 GPU 是否从约 2.38 ms 接近普通 Mesh 的 1.32 ms。

Game View 快速回归（非最终 Profiler 数据）：关 HZB 后同 4K / 12 车视角约 364.1 FPS（2.7 ms），CPU main 2.7 ms、render thread 1.7 ms、40 batches / 40 SetPass，Stats 面板 GPU 延迟落入 1.x ms 区间。相对 HZB 开启时约 309 FPS 小幅提升，说明无 HZB 的 Formal-only camera path 方向成立；仍需 Player GPU Profiler capture 确认 `Nanite/WriteDepth`、`Nanite/BuildHzb`、`Nanite/SecondCull` 已消失，并记录 Formal raster/resolve 的真实 GPU ms。

## 大实例场景 indexed overflow 诊断与否决实验（2026-07-28）

154 辆车、4K 的压力测试暴露出固定 128 MiB 临时索引预算的性能断崖。该场景日志中的 camera 容量仅为 87,381 clusters、每级 shadow 容量为 21,845 clusters；旧版 `CSPrepareIndexedDrawQueue` 只要可见 cluster 数超过任一容量，就把对应 indexed args 清零，并让 procedural fallback 重画整个队列。日志仍显示 `submit=indexedClusterIndirect`，因为它只表示 indexed build pass 已执行，并不表示 GPU args 最终选择了 indexed draw。

该退化与 capture 一致：Nanite `FormalVisibility` 约 15.745 ms，其中 procedural draw 约 15.621 ms；主光 shadow 约 4.352 ms，其中 procedural draw 约 4.244 ms。此时 CPU/Render Thread 的长等待是 GPU 饱和后的 `WaitForLastPresentation/WaitForGPU`，不是 GPU-driven 提交产生了 19 ms C# 工作。

曾实验把容量内前缀保留为 indexed、仅将溢出尾部 procedural，并尝试用 procedural indirect args 的 `startVertex` 偏移 compact queue。实机立即出现大量破面与闪烁，且帧率仍约 50 FPS。这说明该 Unity `DrawProceduralIndirect` 路径下的 `startVertex -> SV_VertexID`/primitive ABI 不能按未经验证的假设使用；实验已完整撤回，恢复此前画面正确的 all-or-nothing fallback。

后续不得继续用 indirect 参数偏移拼接同一个逻辑队列。应先对照成熟 GPU-driven 实现，选择具有显式 ABI 的方案，例如独立 overflow queue、固定容量多批次 queue、mesh shader task/mesh workgroup，或 persistent work queue；并在小规模 correctness test 中验证 triangle/instance/primitive ID 后再进入压力场景。

## 远距离 LOD 仍然过密：Bake 误差审计（2026-07-28）

154 车场景的 debug 视图显示，车辆已经只占很少像素时仍保留大量细三角形。该问题优先级高于 Mesh Shader：Mesh Shader 只能更快地提交现有三角形，不能修复错误的几何层级选择。

对 `toyota_ft1_mesh` 的全部 59 个 Page 做离线统计后，确认旧 Bake 数据存在数量级错误。模型包围球半径约 2.82，旧数据却出现 `selfError=1135.8849`；误差中位数从 mip 1 的 0.0105 逐级膨胀到 mip 8 的 1135.9。运行时把该值投影到屏幕空间后，粗层级要到极端距离才会被判为可接受，因此远处仍选择细层。

| Mip | Clusters | Triangles | selfError median | selfError max | parentError median | parent MaxValue |
|---:|---:|---:|---:|---:|---:|---:|
| 0 | 2503 | 312141 | 0 | 0 | 0.0105 | 0 |
| 1 | 1374 | 155894 | 0.0105 | 0.557 | 0.0519 | 0 |
| 2 | 702 | 79098 | 0.0520 | 10.652 | 0.2107 | 0 |
| 3 | 383 | 41838 | 0.2189 | 21.804 | 0.8813 | 36 |
| 4 | 173 | 19365 | 0.8813 | 53.050 | 20.488 | 0 |
| 5 | 94 | 10065 | 20.488 | 131.696 | 221.494 | 0 |
| 6 | 66 | 6923 | 221.494 | 275.221 | 555.188 | 13 |
| 7 | 46 | 4786 | 555.188 | 559.948 | 1135.885 | 28 |
| 8 | 18 | 1949 | 1135.885 | 1135.885 | n/a | 18 |

meshoptimizer 官方接口明确规定：启用 `meshopt_SimplifyErrorAbsolute` 后，`target_error` 与 `result_error` 都是对象空间绝对误差；当前原生 DLL 只是原样转发 `meshopt_simplifyWithAttributes`，不存在 DLL 层单位转换。

根因位于 `NaniteMeshBuilder` 的累计公式：`BoundsMerge` 已经把最大 child self error 写入 `groupBounds.error`，旧公式随后再次加入 `maxChildSelf`，并把本次 `simplifyError` 也加入两次，导致误差近似指数增长；额外的“半径 2% 下限”也会人为扭曲实际误差。

当前修复：

- 每层误差严格使用 `max(child cumulative error) + current absolute simplify error`，两个量各计一次。
- 删除人为的半径 2% 误差下限。
- meshoptimizer 返回 NaN、Infinity 或负误差时立即终止 Bake，不再生成不可诊断资产。
- `Nanite/Audit LOD Errors` 新增逐 mip 的 cluster、triangle、self/parent min/median/max、`MaxValue` 数量，以及误差/模型半径比值。

该修改只影响新 Bake 数据，不会在运行时偷偷改变旧资产。下一验收门槛是只重烘焙 Toyota：首先确认高 mip error 回到与模型尺寸相符的范围、三角形 debug 随屏幕占比明显退化且无裂缝/闪烁；然后再测 154 车 visible cluster 数和 GPU 时间。若 root 仍停在约 1949 triangles，则下一阶段单独处理 boundary-lock/terminal group 与 disconnected component 的顶层简化，不能重新放大误差掩盖层级质量问题。

### 第一轮重 Bake 结果与第二处误差语义问题

第一轮修复后 Toyota 从 59 Page / maxMip 8 变为 61 Page / maxMip 9，最高误差从 1135.9 降到 33.73，mip 9 本身降到 1000 triangles，说明“重复累计”根因已解除。但完整 root/terminal resident set 并不只有 mip 9：三个 root Page 共有 139 clusters、约 14393 triangles，其中还包含卡在 mip 0～6 的分支。远距离仍会永久携带这些分支。

同时，新误差仍达到模型半径的 11.96 倍。原因是 `meshopt_simplifyWithAttributes` 的 `result_error` 同时包含 position 与 normal attribute error；这个结果适合比较外观质量，却不能作为米制几何位移直接投影到屏幕像素。特别是高 mip 法线变化会把 combined error 推到 5～33，即使实际表面位移远小于该值。

第二轮修改：

- 保持 attribute-aware simplification 选择三角形，避免为了性能直接牺牲法线/接缝质量。
- 新增离线双向 surface-deviation BVH：对 source/simplified 顶点、边中点和三角形中心做双向点到三角形距离查询，仅把对象空间几何位移写入 runtime LOD error；normal error 不再伪装成世界空间距离。
- 层级 group 从 16 meshlets 调到 Nyx 同类路径采用的 32，减少跨 group 锁边与过早 terminal。
- 普通锁边简化卡住时，使用 meshoptimizer 官方 `SimplifyPrune` 做 disconnected-component coarse fallback；被删组件到保留表面的距离会进入上述几何误差，因此只允许在投影到 sub-pixel 后消失。
- LOD Audit 新增完整 root/terminal triangle 数及逐 mip root triangle 分布，避免再把“最高 mip triangles”误当成真正远距离成本。

这一轮仍需重新 Bake 验证。准入条件是：root/terminal triangles 明显低于 14393、几何误差不再达到模型半径十余倍、远景三角形显著退化，并且近中距离无裂缝、部件提前消失或闪烁。

首次执行第二轮 Bake 时，`SimplifyPrune` 对一个完全可裁剪的 disconnected group 合法返回了空 index buffer，而 DAG 尚不支持“零三角形 parent”，导致几何误差测量拒绝输入。现已增加两层防护：每个简化目标至少保留一个三角形；Prune 只有返回非空、三角形对齐且确实更小时才会替换普通结果，否则进入原有 terminal 路径保留几何。

### 第二轮重 Bake：主层级已收敛，补充空 parent 语义

第二轮 Toyota Bake 用时约 46.1 秒，得到 67 Page、5408 clusters、257 groups、maxMip 13。相较上一轮：

- root/terminal triangles：14393 → 2433（下降 83.1%）。
- parent=MaxValue clusters：139 → 24。
- 真正最高层：1 cluster / 58 triangles。
- max finite error：33.73 → 8.23；error/model-radius：11.96x → 2.92x。
- group fanout 从最多 16 提升到 32，boundary vertex duplication 从 8.86% 降到 6.93%。

剩余 root 成本中有 2279 triangles 来自 mip 2 的 22 个分支。这些分支被 `SimplifyPrune` 判定为可以完整删除，之前因为 DAG 只能表达“有 coarse parent”或 `parentError=MaxValue`，只能退回永久绘制。

现在将 root residency 与无限 parent error 解耦：

- `ClusterGroup.isRootSet` 显式表示分支必须常驻并可被 traversal 发现，不再借用 `float.MaxValue` 表示 residency。
- 当 Prune 返回空 parent 时，分支仍是 root set，但 child `parentError=max(childError, groupRadius)`。
- runtime 的投影公式是 `2 * error * projectionScale / distance`，所以 `error=radius` 精确表示整个 component 投影直径小于阈值后消失；默认 1px 下不会留下可见孔洞。
- 普通拓扑卡住且不能完整 Prune 的 terminal 仍保留 `MaxValue`，不会冒险删除主车体。
- Audit 分开报告 resident root-set triangles 与 never-disappearing `parent=MaxValue` triangles。

### 154 车第三轮实测：LOD 修复有效，但 raster admission 仍是当前断崖

第三轮 Bake 保持 5408 clusters / 257 groups / maxMip 13，root set 2433 triangles 中仅 58 triangles 为 `parent=MaxValue` 永久绘制，空 parent 的有限消失语义已经正确写入资产。

但 154 车、4K capture 仍只有约 43 FPS / GPU 23.75 ms：

- `Nanite/FormalVisibility`：14.696 ms，其中 `Graphics.DrawProcedural` 合计 13.969 ms。
- `Draw Main Light Shadowmap`：5.870 ms，其中 Nanite procedural draw 同为 5.870 ms。
- `Nanite Main Light Shadow Cull`：0.355 ms；first cull 约 0.003 ms。

因此 LOD/Bake 的改进没有被 culling 成本吃掉；GPU 时间明确集中在 raster draw。当前 indexed admission 仍是 all-or-nothing：camera capacity 87381 clusters、每级 shadow capacity 21845，任一 queue 超容量就令 indexed args=0、让整个 queue procedural fallback。`submit=indexedClusterIndirect` 只说明 build pass 被记录，不能证明 GPU args 最终选择 indexed draw。

已加入一次性 `AsyncGPUReadback` admission probe，按 GPU command 顺序读取 camera 与四级 shadow count args，输出每个 queue 的 `count/capacity:indexed|PROCEDURAL_FALLBACK`。探针只执行一次且不同步阻塞；其结果同时给 Editor/Player 的后续帧提供分块数 hint。必须取得该数字后才能决定多批 indexed compatibility backend 的批次数与内存预算；在此之前不再盲目扩大 transient index buffer。

### 154 车 Camera indexed 分块兼容后端（待实机验收）

Admission probe 的确定结果为：camera `190157/87381`，shadow0 `17394/21845`，shadow1 `60232/21845`，shadow2 `107639/21845`，shadow3 `92708/21845`。因此 camera 与三路 shadow 并未真正进入 indexed raster，而是整队退回 procedural；这解释了 4K 压测中约 14 ms 的 Formal raster。

Camera 路径现改为显式 compact-cluster offset 的顺序分块：`Build chunk N -> Raster chunk N -> reuse 128 MiB index buffer`。indexed draw 的 primitive ID 使用 chunk 起点；只有最后一块拥有 procedural tail，tail 使用 `chunkOffset + indexedCapacity`，不再依赖 Unity 未保证的 `startVertex -> SV_VertexID` 行为。Compute build 使用二维 dispatch，避免 87,381 groups 超过 D3D 单维 65,535 上限。首帧为一块 indexed 加显式 tail；一次异步 count probe 返回后，当前 154 车数据会记录三块。若可见数临时超过 hint，最后一块 tail 仍保证完整性。

这只是修复 submission cliff 的 Unity compatibility backend，不代表已经达到最终目标。即使全部 indexed，约 190k camera visible clusters（平均约 1235/车）仍明显偏高；正确性与 GPU 时间通过后，下一优先级是 GPU 侧 visible mip/triangle histogram，据此收紧运行时 LOD 成本闭环，而不是继续扩大临时索引内存。

实机初验：154 车、4K、关闭阴影约 `92.9 FPS / 10.8 ms`，开启四级阴影约 `66.0 FPS / 15.2 ms`。相对修改前约 49 FPS，Camera 分块已解除主要 procedural admission cliff，但离 200 FPS 目标仍很远；阴影当前额外约 4.4 ms。

阴影少量破面/闪烁的直接原因不是 shadow bias：通用 prepare kernel 改为“indexed 前缀 + procedural overflow”后，Shadow draw 的 overflow 仍从 compact cluster 0 解码，导致前缀重复而真实尾部丢失。现已令 shadow indexed 前缀使用 offset 0，procedural 尾部使用 `IndexedShadowClusterCapacity`，保持 queue ABI 与 Camera 路径一致。

为定位仍然过高的约 190k Camera clusters，新增一次性异步 `VisibleLodAudit`：不扩大每帧 GPU queue，只回读一次 compact draw queue，并用 `firstTriangle -> baked mip` 的 CPU 诊断映射输出逐 mip 的 cluster/triangle 数。该结果将决定是运行时阈值/投影公式、DAG cut，还是 Bake 的局部分支仍在产生主要成本。

## GPU screen/texel cost closure (2026-07-28)

The previous 154-instance Camera queue contained 190,157 visible clusters and therefore
could not satisfy a screen-space geometry budget. The new path keeps the 2 px geometric
quality threshold as a baseline, but adds a GPU-only cost constraint:

- `GpuInstanceData` stores the finest-cut triangle count of its unique geometry.
- `CSInstanceCull` accumulates projected visible-instance area on GPU (Q16 screen coverage).
- Each instance receives a share of the global screen budget instead of independently
  receiving a full-screen budget. This closes the large-instance overlap/density hole.
- Camera and SceneView use separate persistent GPU feedback states. The actual submitted
  triangle count is accumulated during queue emission; `CSFinalizeSceneTriangleBudget`
  adjusts the next-frame pressure without CPU readback.
- The Camera target is at most one submitted triangle per output pixel. A conservative
  8 covered-pixels/triangle estimator compensates for the fact that geometric deviation
  is not a direct triangle-density metric; actual triangle feedback enforces the final cap.
- Shadow cascades use an independent filtered-shadow budget of 6 texels/triangle and a
  shadow-only error guardrail. Camera feedback never leaks into orthographic shadow LOD.

Measured intermediate Main Camera results at 3840x2160:

| Stage | Camera clusters | Camera triangles | Indexed chunks |
|---|---:|---:|---:|
| Before cost closure | 190,157 | not audited | 3 |
| Instance-area budget, initial calibration | 125,496 | 14,329,109 | 2 |
| Conservative density estimator | 88,529 | 9,968,883 | 2 (1.3% over one-chunk capacity) |

Shadow admission changed from `17k/60k/108k/92k` in the original stress capture, and from
`26k/66k/44k/20k` after the first Camera budget pass, to:

```text
shadow0=9033 indexed
shadow1=21095 indexed
shadow2=13975 indexed
shadow3=4004 indexed
```

All four cascades are now below the 21,845-cluster indexed partition. The procedural shadow
tail is therefore removed for this stress scene. The final Camera acceptance criterion is
`triangles <= width*height` and `clusters <= 87,381` after the GPU feedback warm-up. Visual
acceptance still requires checking silhouette stability and near-hero detail in Game View.

## Coarse-LOD topology and material correctness (2026-07-28)

The first screen-budget acceptance run reached about 244.6 FPS / 4.1 ms, but distant cars
showed missing components and material regions changing from black to white. These are not
normal LOD transitions. Two independent correctness faults were found and fixed:

- A fully pruned disconnected hierarchy branch has finite `parentError=componentRadius` and
  no coarse replacement geometry. The adaptive 64/96 px Camera/Shadow thresholds were also
  being used as its disappearance threshold, so a whole component could vanish while it was
  still tens of pixels wide. GPU Part/Cluster records now carry an exact terminal-disappear
  flag built from `hierarchyGroups` and `hierarchyClusterRefs`. Only the base geometric
  quality threshold (normally 2 px) may remove such a branch; adaptive thresholds still
  choose coarser represented geometry. GPU layout version is now 8.
- The baker previously supplied only normal.xyz to `meshopt_simplifyWithAttributes`. It now
  supplies packed UV.xy + normal.xyz + tangent.xyzw attributes. Duplicate-position vertices
  whose attributes differ use meshoptimizer's `SimplifyVertex_Protect` flag; dynamic
  cross-partition boundaries independently use `SimplifyVertex_Lock`. This is the Nyx/
  meshoptimizer permissive-simplification contract: hard-locking every attribute wedge stalls
  the hierarchy, while omitting `Protect` lets coarse triangles cross UV/hard-normal seams.

The terminal-branch runtime fix works with the existing asset. The attribute/seam fix changes
baked topology and therefore requires rebaking Toyota. Acceptance is near/mid/far camera
movement with no holes, no black/white material-region swap, and no persistent flicker. A
discrete silhouette transition is still expected until temporal LOD dithering/morphing is
implemented, but the geometry and sampled material must remain semantically identical.

After this correctness gate, the next implementation step is a hybrid raster backend, not a
second full software renderer: projected micro triangles are classified on GPU, removed from
the indexed hardware queue, and rasterized into the same visibility ABI with atomic depth;
larger triangles stay on the current indexed path. Page work will extend the existing
NPG1/NZC1, root residency, Page Pool, Page Table, decode table and resident cache rather than
creating a parallel format. The next storage milestone is for compressed resident pages to be
consumed directly by GPU transcode/streaming while compatibility decoded geometry can be
retired under a measured memory budget.

## Unreal-style concurrent hybrid raster backend (2026-07-28)

This stage follows the public Nanite implementation rather than another LOD-threshold patch.
The primary references are *A Deep Dive into Nanite Virtualized Geometry* (Karis et al.,
SIGGRAPH Advances in Real-Time Rendering 2021, especially slides 77 and 84–91) and Unreal's
`NaniteRasterizer.usf` / `NaniteWritePixel.ush`:

- GPU classification is per visible cluster. A 128-thread group transforms the cluster's
  triangles and measures projected edge length. Clusters containing a triangle edge at or
  above 32 pixels, or requiring homogeneous clipping, remain on fixed-function hardware
  raster. Micro-triangle clusters enter the software queue.
- The hardware queue keeps the existing indexed-indirect backend. The software queue runs on
  RenderGraph async compute. Both branches depend only on classification and can overlap;
  they meet at the visibility merge.
- Software setup uses 8-bit subpixel coordinates and the same one-sided determinant rule as
  Unreal `SetupTriangle` (`DetXY >= 0` is rejected). It does not intentionally render hidden
  back faces to conceal cracks.
- Unreal writes one shared 64-bit atomic visibility value. Unity 6000.3 ShaderLab exposes no
  Shader Model 6.6 typed 64-bit UAV atomics, so the compatibility backend uses two compute
  passes: atomic `R32_UINT` depth, then an exact-depth atomic winner payload. A fixed-function
  depth-tested merge writes the existing `R32G32_UINT` instance/triangle ABI. This limitation
  is explicit; it is not presented as identical to UE's single-atomic path.
- Triangles touching a clip plane stay on hardware raster until the software path gains UE's
  full clip handling. Shadow cascades also remain hardware-only for this first correctness
  gate.

## Source-aligned Bake hierarchy correction (2026-07-28)

The post-hybrid 154-instance test reached roughly 169 FPS at 4K, but stable detached body
panels and severe coarse-mip texture distortion remained. The GPU capture put the main
`Nanite/FormalVisibility` work around 1.69 ms and the indexed draw itself around 0.23 ms, so
this visual failure was treated as a Bake hierarchy fault rather than hidden with raster
thresholds.

Comparison with Nyx `MeshletBuilder.cpp` found two concrete deviations and both were removed:

- Persistent attribute discontinuities now set bit 1 (`meshopt_SimplifyVertex_Protect`) on
  both position-equivalent wedges. Per-mip partition boundaries set bit 0 (`Lock`) without
  erasing bit 1. `Protect` is intentionally used only with `SimplifyPermissive`.
- The experimental `SimplifyByConnectedComponents` path was deleted because Nyx simplifies
  one complete partition group in one meshoptimizer invocation. The first validation after
  this change showed that component allocation was not the primary crack cause; see the
  correction below.
- Attribute weights now match Nyx: normal xyz 0.5, tangent xyz 0.1, tangent handedness 0.5,
  and UV xy 0.1. The comparison is exact, matching Nyx's packed-attribute equality check and
  avoiding a guessed seam epsilon.

This changes the hierarchy topology and requires a fresh Bake. The immediate acceptance gate
is correctness, not FPS: near/mid/far cuts must retain every represented component and keep
UV/material regions stable. Once that gate passes, early degradation/popping is addressed at
the temporal cut-selection level; it must not be compensated by restoring the invalid
component-wise simplifier.

### Validation correction: persistent refinement boundaries

The following Bake produced 63 pages / maxMip 6 and 39,623 resident root-set triangles, with
visible cracks worse than before and about 143.7 FPS at 4K. Inspection of Nyx's
`BuildVertexLocksByGroups` found a more fundamental mismatch: it accumulates partition locks
with `|= meshopt_SimplifyVertex_Lock`, while this implementation cleared every previous Lock
on each mip. That allowed a coarser generation to move a boundary already used by a finer
refinement edge, so mixed-mip cuts could not remain watertight.

Boundary Lock bits are now permanent for the rest of the Build. Attribute Protect bits remain
orthogonal. LOD error propagation also now follows Nyx: `max(current simplifyWithAttributes
error, inherited child error)`. The former geometry-only estimator ignored UV/tangent error,
selected visibly corrupted parents too early, and has been removed rather than left as dead
code. This correction may expose hierarchy branches that cannot meet the target reduction;
those must be addressed through grouping/topology and not by unlocking an established edge.

The first Protect implementation compared Unity source floats exactly, whereas Nyx compares
the already packed vertex payload. It consequently protected sub-storage-precision noise and
inflated the terminal root forest. Seam classification now compares UV as R16G16 half and
normal/tangent as R10G10B10A2, matching the cited Nyx input contract. The Bake emits one
`[Nanite][BuildDAG]` summary containing simplification success, protected vertices, cumulative
boundary locks and resident-root triangle count so the next acceptance run can distinguish
real seam pressure from hierarchy logic failure.

### Free active-cluster repartition (2026-07-28)

The persistent-boundary validation removed visible cracks, confirming that cumulative Locks
are required. It also produced only maxMip 4, 16,590 boundary-locked vertices and 48,926
resident-root triangles, with the 154-instance 4K test falling to about 133.9 FPS.

The remaining hierarchy-depth fault came from a local constraint absent from Nyx: all coarse
clusters generated by one refinement group were wrapped in a `PartitionAtom` and forced into
one consumer group on the next level. Nyx instead feeds every active meshlet directly to
`meshopt_partitionClusters` at every level. The atom constraint defeated the partitioner's
connectivity result, increased cut boundaries and terminated the DAG early.

An experiment removed `PreserveProducerGroups` and its single-consumer Bake validation. It
reduced boundary locks from 16,590 to 11,773 and root triangles from 48,926 to 45,382, but
visible cracks returned and maxMip remained 4. The gain was too small and the topology was
invalid for the current atomic refinement ABI: splitting one producer's coarse clusters among
different consumers permits only part of its fine replacement to activate. The experiment is
reverted; producer siblings remain one partition atom and Bake rejects split consumers.

The same audit reported maximum LOD error 9.68 for a model radius of 2.82. That value included
UV/normal/tangent quadric units and cannot be projected as an object-space distance. Bake now
runs a companion geometry-only meshoptimizer QEM evaluation with identical source, topology
locks and target count. The attribute-aware simplification still supplies the emitted topology;
only the companion absolute positional error drives screen-space LOD, with Nyx's 1.5x inherited
error guard band. This removes the false “UV units are metres” residency pressure without
weakening seam protection.

Hybrid compute errors observed immediately after the asset rebuild came from releasing shared
classification/dispatch buffers while RenderGraph commands recorded by another Scene/Game
camera still referenced them. Geometry-generation changes now retire the old buffer set for
four frames before release. This prevents `_HybridPrepareCountArgs`, `_HybridInputClusters`
and `_HybridVertexData` from becoming invalid between graph recording and execution.

`PC_Renderer.asset` now selects hybrid mode with the source-backed 32 px threshold. Acceptance
must verify three things separately: no near/mid/far holes or material changes; the Frame
Debugger contains `HybridClassifyClusters`, `HybridSoftwareRaster`, indexed hardware raster,
and `HybridSoftwareMerge`; and the GPU profiler shows async software work overlapping the
hardware branch.

## Root-group GPU selected-cut queue (2026-07-28)

The 154-instance baseline still expanded 110,572 virtual Parts even though the final cut used
only a small subset of the 779,856 virtual Clusters. The main-camera direct draw path now
consumes the serialized cross-Page DAG instead of scanning every mip-level Part:

1. instance frustum cull appends visible instances;
2. root groups seed a GPU-resident `uint2(instance, hierarchyRef)` queue;
3. fixed indirect ping-pong passes evaluate screen error and complete fine-Page residency;
4. a producer group is replaced atomically by its fine refs, or its coarse refs are emitted
   directly into the existing indirect draw queue;
5. missing fine working sets request their Pages and retain the resident coarse cut.

Only the canonical first coarse ref evaluates a producer group. This is required by the
current Bake ABI: all siblings produced by that group enter and leave the selected cut
together, so traversal cannot recreate the partial-refinement cracks seen during the free
repartition experiment. Terminal root branches retain the dedicated 1/8-pixel disappearance
rule. Shadows deliberately stay on the validated fused Part path until the camera traversal
passes its correctness/performance gate.

The traversal kernel reflects exactly eight UAVs on DX12 (queue, draw queue, visibility and
two-phase masks/stats, Page requests, triangle-budget feedback), matching the Windows limit
that previously broke the direct Part kernel. C# compilation and all affected compute entry
points pass standalone DXC validation. Runtime identifies this path as
`GPU-RootGroup-RefineQueue-DrawIndirect` and reports unique DAG group/ref counts and the fixed
pass count; no CPU readback is introduced. This replaces the previous
Instance-to-Part-to-Cluster dispatch chain.

## Persistent traversal, hierarchy shadows and real Page residency (2026-07-28)

The first root-group validation reported the new hierarchy label but no FPS change. The log
still showed roughly 600,925 submitted triangles, while the capture showed about 2.21 ms in
four-cascade shadow raster and about 1.435 ms in the compatibility hybrid compute path. This
confirmed that reducing traversal work alone cannot remove downstream raster/shadow cost.

The fixed six-pass queue has therefore been replaced by a persistent queue following the
global-task-queue structure used by Nyx `DAGCull.slang`. Its allocation and publication
frontiers are intentionally separate: producers reserve a range, publish generation-tagged
payloads, then cooperatively advance one contiguous `publishedWrite` frontier. Consumers
never claim from `reservedWrite`. Termination requires no active producer and equality of
read, published and reserved frontiers. This removes both the six CPU-recorded hierarchy
passes and the deadlock class where all resident workers wait on unpublished slots.

Main-light shadows now use a separate persistent arena and one four-view DAG traversal.
Each task carries a cascade mask, so near and far cascades can choose different cuts while
sharing frustum tests, residency checks and Page requests. Missing fine Pages retain the
resident ancestor cut. The old fused Part scan remains only as a compatibility fallback;
the production log is `shadowFrustum/fusedHierarchyPersistent`.

The Page system is no longer admitted only after `EnsureAllPagesResidentForPackedRaster`:

- startup uploads and transcodes only pinned root Pages;
- GPU hierarchy traversal requests missing fine working sets asynchronously;
- triangle metadata stores `(localIndex, globalPageId)`, not a physical resident address;
- raster/resolve resolves `indexBase` through the double-buffered resident Page table;
- indexed-cluster construction reads resident indices and emits an encoded
  `(instance, residentVertex)` hardware index;
- the duplicate full decoded vertex/index buffers are reduced to one-element legacy SRVs in
  the packed production path.

This is the required address indirection for fence-safe eviction: a Page may move to another
slot/range without rebuilding triangle metadata. Page table, decode table, resident table,
request masks and root pinning are also the intended shared spatial ABI for later BLAS/TLAS
residency decisions.

The previous Unity compatibility hybrid raster (two 4K R32 targets, two software triangle
passes and a fullscreen merge) is not equivalent to Unreal's shared 64-bit visibility atomic
and is no longer the production default. `PC_Renderer.asset` uses indexed hardware raster
until a native/SM6.6 shared-atomic backend is available; enabling the old mode is diagnostic
only and is rejected while the demand-resident cache is active.

Static validation for this stage: 23 runtime-culling kernels, 12 compact/index kernels and
2 Page-transcode kernels compile with DXC; camera traversal reflects 8 UAVs and four-view
shadow traversal reflects 6 UAVs; the Nanite C# assembly compiles with Unity Roslyn.

### Persistent-queue TDR postmortem and quarantine

The first Unity runtime validation exposed a wave-level deadlock that standalone DXC cannot
detect. An idle lane spun inside `ClaimPersistentHierarchyTask` while another lane in the same
wave owned the task needed to make progress. Since a wave executes in lockstep, the owner could
not leave the claim function to publish or finish its task. DX12 consequently reported
`DXGI_ERROR_DEVICE_HUNG (887a0006)` and Unity crashed while waiting on a fence.

Idle lanes now return immediately; the active owner loops back after completion and drains any
children it published. Both traversal kernels also have a hard 4096-task per-lane watchdog, so
a malformed DAG or future protocol regression drops remaining work instead of hanging DX12.

The saved `UnityNanite_2026-07-29_12-26-32` capture validated the bounded rollback at 154
instances: GPU frame time was about 6.68 ms (roughly 166 FPS), split primarily between Formal
Visibility (2.206 ms), four-cascade main-light shadows (1.773 ms), and FirstCull (1.619 ms).
The overlay reported only about 17K submitted triangles, so Bake/LOD density is no longer the
dominant cost. FirstCull still recorded 18 compute dispatches because the bounded hierarchy
required six refinement rounds.

The attempted camera-only re-admission failed at runtime. Although it produced `passes=1`, the
154-instance scene dropped most of its selected cut and rose to about 146.7 ms / 6.8 FPS. The
reason is fundamental to this scalar protocol: making idle lanes return prevents a wave
deadlock, but those lanes can all terminate before the last active producer publishes its
children, leaving queued work with no consumers. Making them spin recreates the original wave
deadlock. A correct single-dispatch implementation needs cooperative wave/group scheduling or
a native work-graph/mesh-shader path, not another atomic threshold adjustment.

Both persistent gates are therefore disabled in `PC_Renderer.asset` and are experimental-only.
The production path remains the bounded six-pass DAG traversal plus `fusedDirect` shadows.
Demand-resident Page addressing and packed raster remain active. Do not request further user
validation of the scalar persistent implementation.

## 2026-07-29 architecture reset: heterogeneous GPU Scene

The 154-copy Toyota result is not an acceptance result for a general GPU-driven renderer. It
contains one geometry asset and nearly one material layout, so it does not exercise the costs
that dominate a real heterogeneous scene: unique meshes, variable SubMesh counts, material
parameter/texture diversity, shader families, Page churn and raster-bin fan-out.

### Retraction: Screen-Space Geometry Budget

The project-specific `Screen-Space Geometry Budget` has been removed in full. It allocated a
triangle budget from the projected area of an entire instance bound and fed the previous
frame's aggregate triangle count back into the LOD error. This is not the graph-cut criterion
used by the inspected UE/NVIDIA/Nyx implementations. It caused both observed failure modes:
near instances could become coarse abruptly, while distant instances could stop refining the
cut even when their micro triangles still aliased.

Production LOD selection is again based only on projected object-space geometric error and
the generating-group/self-group conditions of a unique DAG cut. "One triangle per pixel" is
an efficiency target used after cut selection and for HW/SW raster classification; it is not
an instance-area triangle allocator.

The removed path includes its Inspector fields, serialized Renderer fields, two compute
kernels, feedback buffers, shader parameters and the `finestLodTriangleCount` instance ABI
member. The unsafe scalar persistent traversal switches were also removed from the production
Inspector and quarantined behind a compile-time false gate.

### Source comparison used for the reset

| Reference | Inspected implementation | Contract adopted here |
|---|---|---|
| Unreal Nanite | `NaniteRasterizer.usf`, `NaniteWritePixel.ush` | selected cluster first; then raster-bin/HW-SW classification into one visibility contract |
| Nyx | `MiniEngine/Model/Shaders/DAGCull.slang`, `VBufferMesh.slang`, `ResolveVBufferToGBuffer.slang`, `GeometryStreaming.cpp` | GPU hierarchy queues, VBuffer resolve separation, explicit Page residency |
| NVIDIA `vk_lod_clusters` (`70506fd`) | `traversal.glsl`, `traversal_run.comp.glsl`, `traversal_run_groups.comp.glsl`, `docs/lod_generation.md` | projected group error and generating/self-group unique graph cut |
| `nanite-webgpu` (`b9cd33f`) | `nanite.wgsl.ts`, `cullMeshletsPass.wgsl.ts` | compact mesh/instance tables and GPU-created visible work lists |

Reference implementations are kept outside the repository; cited upstream URLs and commit
identifiers provide reproducible provenance without recording a contributor's local path.

### Replacement matrix

| Subsystem | Previous/current limitation | Required production replacement | Status |
|---|---|---|---|
| GPU Scene material mapping | rectangular `instances * sceneMaxSubMeshes` table | per-instance `materialSlotOffset/count` plus compact slot stream | implemented, layout v7 |
| Material execution | one full-screen draw per unique Unity Material; hard cap 32 | GPU material/raster bins; one indirect tile list per shader family; parameter table and texture indirection | next blocker |
| Arbitrary shaders | silently copied a small URP-Lit property subset | explicit shader-family ABI; unsupported shaders use a declared fallback/ordinary renderer | not implemented |
| Geometry Scene | geometry shared per unique mesh, instances separate | retain; move all mutable instance records to a persistent dirty-range GPU Scene | partial |
| LOD selection | bounded six-round root-group refinement | unique generating/self-group graph cut; cooperative queue or mesh/task shader scheduler | bounded path correct, scheduler incomplete |
| Raster | indexed HW path plus quarantined compatibility SW path | cluster footprint/classification, concurrent HW/SW queues, shared visibility atomic/merge | incomplete |
| Pages | Page ID/table/root pinning exists; payload still oversized | compact cluster-local encoding, meshopt/LZ4 blocks, dependency/fixup metadata, strict upload/eviction budget | partial |
| Shadows | four-view cull and HW raster | independent shadow cuts and raster bins; no camera-cut reuse | partial |
| Mesh Shader | absent | task/mesh work emission from the same selected-cluster queue | absent |
| RT | no production bridge | resident cluster list -> shared BLAS cache; instance table -> TLAS; Page residency gates BLAS lifetime | planned |

### Material/shader scalability boundary

No renderer can execute an unbounded number of unrelated shader programs in one draw merely
by calling it GPU-driven. UE also classifies work into material/raster bins with compatible
shader code. The production contract for this Unity implementation will therefore be:

1. SubMesh count and material-instance count may be large without rectangular CPU/GPU tables.
2. Materials using the same supported shader family share one GPU parameter/texture table and
   one indirect bin sequence; different parameter values do not create SetPass calls.
3. A genuinely different shader program creates a shader-family bin. The number of shader
   families, not the number of objects/material instances, bounds CPU submission.
4. Unsupported custom shaders are explicit compatibility draws or rejected at import; they
   are never silently rendered as white/default Lit and called supported.

The first ABI step is now in source: `_InstanceMaterialRange[instance] = (offset,count)` and
`_InstanceSubMeshMaterial[offset + subMesh]`. This removes the worst heterogeneous SubMesh
memory multiplier and is the stable lookup consumed by the upcoming material-bin builder.

### Unique-mesh spatial traversal (2026-07-29)

After restoring the correct continuous-LOD cut, the 154-instance capture exposed a separate
submission multiplier: immutable geometry was shared, but camera and shadow culling still
expanded every visible instance across every Part (`154 * 718 = 110,572` virtual Parts for
one Toyota mesh). That is GPU Scene indirection without GPU Scene traversal.

The production Part candidate path now uses an immutable eight-way spatial hierarchy per
unique mesh. Instances store only `spatialRootOffset/count`; nodes contain conservative
unions of descendant bounds, parent-error spheres and maximum parent error. A bounded
breadth-first indirect queue rejects whole subtrees by frustum and projected error before
leaf Parts enter the existing unique graph-cut test. This follows the accumulated traversal
metric used in NVIDIA `vk_lod_clusters/shaders/instance_classify_lod.comp.glsl` (lines
176-245), while deliberately remaining independent of the obsolete producer-group atomic
replacement graph.

The same nodes drive a separate four-cascade shadow queue. Tasks carry a cascade mask, so a
node rejected by one shadow frustum stops expanding for that cascade without repeating the
tree walk four times. Camera and shadow queues have separate storage because their
RenderGraph command streams may overlap. Neither path spins, reads a count back to the CPU,
or duplicates nodes per instance. The old flat Part kernels remain compatibility fallbacks.

This stage changes traversal cost, not LOD policy, and needs no rebake. Static admission:

- Nanite runtime C# compiles with Unity 6000.3.10f1 Roslyn (only the three pre-existing API
  warnings remain);
- camera seed/traverse, fused shadow seed/traverse, and flat shadow fallback compile with
  DXC SM6;
- runtime logging reports `GPU-InstanceQueue-SpatialNode-PartQueue-ClusterIndirect` plus
  unique spatial node/pass counts;
- the scalar persistent path and atomic producer-group traversal remain quarantined.

### Material family bin foundation (2026-07-29)

The former tile path encoded `1 << materialId`, which imposed a hard scene limit of 32
Materials and still submitted one full-screen GBuffer resolve per Material. That is a
material loop, not the raster/shader-family binning used by Nanite.

Material identity is now independent of execution family. The compact material table admits
up to 65,535 stable IDs, while its `shaderFamily` field selects compatible code/state.
Textureless URP-Lit parameter variants are grouped into at most eight state families
(environment reflection, specular highlight and receive-shadow state) and resolve directly
from `_NaniteMaterialData` in one pass per present family. The tile classifier writes family
bits rather than Material bits, so its fixed 32-bit mask limits shader families, not scene
material count. CPU family lookup is cached at scene rebuild and no longer re-inspects Unity
Material properties every frame.

Materials with unique sampled textures or unsupported shader programs deliberately remain
family zero and use an exact compatibility bin. This is an explicit boundary, not the final
texture solution: the next material milestone is a virtual texture/descriptor cache whose
stable handles live in `GpuMaterialData`, after which textured variants of the same shader
family can use the same single screen resolve. A fixed oversized Texture2DArray is rejected
because it would resample arbitrary formats/sizes, reserve worst-case memory and merely move
the heterogeneous-scene limit.

Static admission for this increment: all 25 runtime-culling kernels, both float and compact
material-classification variants, and the Nanite C# assembly compile successfully. ShaderLab
import remains the authoritative validation for the modified GBuffer pass.

### Traversal-produced HW/SW raster bins and packed Page decode (2026-07-29)

The first Hybrid implementation was structurally wrong for production: after culling had
already produced the selected cluster cut, another compute pass scanned the complete draw
queue, decoded every triangle to estimate edge size, and appended the cluster to separate HW
or SW buffers. Packed Page raster disabled this path entirely, so the production Page route
could never exercise software rasterization.

The current spatial production path now classifies at the point where
`CSClusterCullVisibleParts` accepts a cluster. It retains the complete selected-cluster list
for depth/debug/compatibility consumers and additionally writes exactly one raster bin:

- a cluster entirely inside all six camera planes whose conservative projected bounding-
  sphere diameter is at most the SW threshold enters the software bin;
- clipping cases, large clusters, and clusters exceeding the 7-bit triangle-range payload
  enter the fixed-function indexed bin;
- both bins receive GPU counters; the SW counter is converted to a bounded 2D indirect
  dispatch without CPU readback;
- a RenderGraph dependency token releases indexed HW raster and async-compute SW raster from
  the same cull result, allowing them to overlap on separate visibility targets.

This follows the decision placement in NVIDIA
`vk_lod_clusters/shaders/traversal_run_groups.comp.glsl` (SW selection during traversal) and
the bin ABI in UE `NaniteRasterizer.usf` (`RasterizerBinHeaders` and `RasterizerBinData`).
NPG1 V3 now carries the baked object-space longest triangle edge used by that decision; V1/V2
assets conservatively substitute the geometry-sphere diameter until they are re-Baked.

Hybrid vertex fetch now resolves the same stable `(Page ID, local index)` address as indexed
HW raster and reads the resident Page Table/vertex/index cache directly. Compatibility mode
binds correctly typed one-element fallbacks for every Page SRV, preventing Unity compute
resource-validation failures and stale bindings. Cluster admission reads one uploaded
object-space sphere instead of reparsing Page headers or decoding all cluster triangles.

Unity ShaderLab does not currently expose the same portable shared 64-bit visibility atomic
contract used by UE and NVIDIA. HW and SW therefore still write separate targets and merge
afterward; this is an explicit remaining cost, not claimed parity. The obsolete post-cull
classifier remains only as a fallback for non-spatial compatibility paths. Runtime-culling
UAV reflection was checked after bin integration: the production visible-Part kernel uses
four UAVs, while generic and hierarchy fallbacks remain at seven, below the existing eight-
UAV compatibility limit.

Static admission for this increment: Unity Roslyn compiles the Nanite assembly with only the
three pre-existing API warnings; all 26 runtime-culling kernels and all 12 compact/indexed/
hybrid raster kernels compile with DXC SM6.0.

### Budgeted asynchronous Page publication (2026-07-29)

The demand-resident path previously used asynchronous GPU request readback but performed the
expensive work inside its callback: it fetched a Page range, decompressed LZ4, uploaded the
packed slot, published tables and dispatched resident-cache transcode in one main-thread
burst. That made the readback asynchronous in name only and recreated the CPU active-time
spikes this renderer is intended to remove.

Streaming is now a staged pipeline:

1. the double-buffered GPU request mask is read back without a wait;
2. the callback only updates Page touches, deduplicates Page IDs and captures immutable bulk
   byte ranges on the main thread;
3. bounded batches validate and unpack NZC1/LZ4 Pages on ThreadPool workers;
4. completed Pages enter a generation-tagged main-thread queue;
5. each frame consumes that queue under both Page-count and byte budgets, uploads packed
   slots, publishes only dirty Page-table ranges, dispatches GPU transcode, then publishes
   the resident-ready generation;
6. evicted slots and decoded vertex/index ranges remain quarantined until their graphics
   fence passes.

The generation tag is part of the pending-request identity. A late worker result from a
disposed/rebuilt scene can neither publish stale bytes nor clear a new request that happens
to reuse the same Page ID. Outstanding decode work is capped independently from the upload
budget, preventing a constrained pool from accumulating the whole virtual asset while it is
waiting for retired slots.

The disk side now also uses one bulk TextAsset per Nanite mesh. Every Page stores an offset
and length into that blob, compressed Pages decode directly from the shared range, and the
Page Pool caches `TextAsset.bytes` once per bulk asset. Runtime upload reuses one fixed
256-KiB staging array and resident transcode reuses its task array. Together these remove the
former per-Page bulk copy, repeated whole-blob materialization and per-request upload arrays.

This follows the same separation of request, IO/decode, upload, fixup/publication and safe
retirement visible in Nyx `GeometryStreaming.cpp` and UE's Nanite streaming manager. The
current Unity `TextAsset` source is memory-backed, so this stage removes main-thread decode
and publication spikes but is not yet true file/Addressables IO. Replacing the byte-range
source does not change the Page Table or GPU transcode ABI.

Static admission for this increment: the Nanite C# assembly compiles with Unity Roslyn with
only the three pre-existing API warnings; all 41 kernels across runtime culling, compact/
hybrid raster, Page transcode and material tile classification compile with DXC SM6.0; the
modified Nanite sources pass `git diff --check` (line-ending notices only).

### GPU Scene dirty-range instance publication (2026-07-29)

The spatial traversal backend and visibility/resolve backend previously detected transform
changes incrementally but uploaded the complete instance tables whenever one object moved.
That scales CPU-to-GPU traffic with total scene population rather than the number of changed
objects. Both backends now coalesce their already sorted dirty instance IDs into contiguous
`ComputeBuffer.SetData` ranges. Transform matrices, traversal scale/LOD parameters and
per-instance probe SH use the same policy; the periodic all-probe refresh remains a deliberate
full upload. Repeated `Transform.lossyScale` native-property reads were also collapsed to one
read per inspected instance.

This closes the publication half of the persistent GPU Scene contract: immutable geometry is
shared per unique mesh, material-slot tables are compact, and sparse mutable instance changes
no longer rewrite every record. Discovering changed GameObject Transforms still requires a
linear comparison because standard Unity `Transform` has no renderer-owned change stream.
An ECS/Entities integration can feed the same range uploader directly without changing the
GPU instance ABI.

## 2026-07-29 production closure: sparse Hybrid, material bins and resident cuts

This increment closes three costs/correctness gaps that were still hidden by the homogeneous
Toyota test.

### Sparse HW/SW visibility merge

The compatibility Hybrid backend no longer clears two full-resolution `R32_UINT` images and
runs a fullscreen merge. Software clusters mark 16x16 coverage tiles, the mask is compacted
to an indirect tile list, and only those pixels are cleared and merged. Hardware and software
raster still start from the same traversal-produced cut and can run concurrently; clipping
and large clusters stay on indexed hardware raster. This preserves the shared VBuffer ABI
without pretending that Unity ShaderLab exposes Unreal's 64-bit visibility atomic.

### Heterogeneous material execution contract

URP/Lit materials are now split into two GPU execution levels:

- textureless parameter variants share a shader/state family draw;
- textured variants sharing shader state and the same Base/Normal/Metallic/Occlusion/Emission
  bindings share one compatibility bin, while per-material color, scalar and UV-ST values
  remain in `GpuMaterialData`.

The tile classifier stores shader-family bits and a compatibility-bin Bloom mask; the resolve
fragment performs the exact bin comparison, so Bloom collisions can only add work, not shade
the wrong pixels. A genuinely unrelated shader program is no longer silently approximated as
white Lit: it is explicitly returned to its native `MeshRenderer` when available. Adding a
new GPU shader family requires an explicit resolve implementation, matching Unreal's material
bin contract rather than an impossible "arbitrary shaders in one draw" promise.

### Demand-resident direct spatial cut

The fast `GPU-spatial-cut` path now has the same resident-ancestor guarantee as the slower DAG
scheduler. A nonresident fine Page is requested but never submitted to raster; its resident
producer-group coarse set stays selected until every Page in the fine working set is decoded
and its Page Table generation is published.

The initial implementation checked that working set once per coarse cluster, which multiplied
Page-table traffic by visible cluster count. Production now builds a compact
`(instance, refinement-group)` residency cache once per view and cluster admission reads one
`uint`. The cache is linear in the groups belonging to each instance's mesh; it does not form
an `all instances x all scene groups` Cartesian product, which is essential for heterogeneous
scenes. GPU layout version is 14.

Static gate for this closure:

- all 27 runtime traversal/shadow kernels compile with DXC SM6.0 `-Ges -WX`;
- all 47 compute variants across traversal, Hybrid/index construction, Page transcode and
  material classification compile;
- runtime and Editor Nanite assemblies compile with Unity 6000.3.10f1 Roslyn (only the three
  pre-existing obsolete/hiding warnings remain in the runtime assembly);
- scoped `git diff --check` passes.

### Completion boundary before runtime acceptance

| Requested subsystem | Production state | Remaining runtime gate |
|---|---|---|
| GPU Scene | shared immutable geometry, compact material ranges, dirty-range instances, linear per-mesh residency metadata | heterogeneous mesh/material stress capture |
| GPU traversal | instance + unique-mesh spatial hierarchy + indirect Part/Cluster cut, no readback | verify Page churn does not increase FirstCull |
| Indirect raster | chunked indexed camera/shadow queues with procedural overflow safety | verify Hybrid keeps indexed path admitted |
| Four-cascade shadows | fused spatial queue, cascade masks, independent texel LOD and resident fallback | cascade stability and total shadow GPU ms |
| Page format/compression | NPG1 quantized payload, NZC1 LZ4, one bulk asset, Page/decode tables | payload/resident/pool counters under motion |
| Residency/streaming | root pinning, async request/decode, budgeted upload, double-buffer publication and fence retirement | sustained camera-motion churn test |
| Bake/continuous LOD | meshoptimizer attributes, cumulative locks, free active-cluster repartition, group error cut | asset-specific quality remains a Bake acceptance item |
| HW/SW Hybrid | traversal bins, async SW raster, sparse tile clear/merge, shared VBuffer resolve | ShaderLab import plus GPU pass timings |
| Heterogeneous materials/textures | family bins, texture/state compatibility bins, exact fallback for unsupported shader programs | multi-texture/multi-shader scene batch count |

Mesh Shader and RT reuse remain intentionally outside this scope as requested. True on-demand
disk IO is also source-provider dependent: the current bulk `TextAsset` removes per-Page files
and main-thread decompression but is memory-backed by Unity. The Page Table and staged
streaming ABI are ready for an Addressables/AssetBundle range provider without changing
traversal or raster.

## 2026-07-29 capture-directed raster closure

Capture `UnityNanite_2026-07-29_15-04-18` moved the optimization target away from traversal:
`Nanite/FirstCull` was about 0.003 ms, while four-cascade shadow raster was 4.616 ms and Formal
Visibility was 2.926 ms. The saved admission probe also identified the concrete shadow
overflow: camera `68995/87381`, shadow queues `0, 144, 62439, 69454`, while the old fixed
quarter slices admitted only 21845 clusters per cascade.

The production changes are therefore raster-directed:

- traversal-produced HW/SW bins are enabled whenever dual Formal raster is not *actually*
  active. Merely leaving its Inspector option enabled while HZB is inactive no longer forces
  the post-cull compatibility classifier;
- both FirstCull and SecondCull write the RenderGraph dependency used by async software
  raster, so the merged cut cannot race the SW queue;
- raster-bin readiness is stamped with frame and camera identity instead of a global boolean;
- indexed scratch sizing uses instance-expanded GPU Scene demand rather than unique mesh
  cluster count. The configured 128-MiB ceiling is now usable in an instanced scene instead of
  silently allocating only the unique-geometry fraction;
- the four shadow queues allocate slices on the GPU from their actual counts. A greedy
  near-to-far prefix shares the complete 87381-cluster scratch, builds each admitted slice via
  indirect dispatch, and exposes the per-cascade admitted count to the procedural tail shader.
  This halves the known overflow for the captured queue distribution without a readback or a
  larger buffer;
- CSM geometry uses a two-shadow-texel minimum error target. Camera visibility retains its
  authored pixel threshold; the shadow bias is per-cascade because its projection scale is
  already expressed in that cascade's texels.

The HW/SW split follows NVIDIA `vk_lod_clusters`: bin during traversal, require a clip-safe
cluster for SW raster, and submit HW and SW lists independently. The shadow change follows the
same Nanite view principle: shadows receive an independent texel-space cut rather than
replaying the camera cut. Static admission compiles all 51 Nanite compute entry points with
DXC SM6.0 `-Ges -WX`, compiles the Nanite runtime C# assembly with Unity's Roslyn response
file (only the three pre-existing Unity API warnings), and passes scoped `git diff --check`.

### Runtime acceptance: `UnityNanite_2026-07-29_15-45-57`

At 3840x2160 with 154 instances and four cascades, shadow raster dropped from the previous
4.616 ms to 2.607 ms. The representative GPU frame was 9.39 ms and the capture median was
8.438 ms; Formal remained 2.737 ms. The new probe reported camera `115886/87381` and a
four-cascade sum of `231486/87381`, confirming both that the shared shadow allocator is active
and that the remaining cost is raster admission rather than traversal.

The same run exposed a RenderGraph lifetime bug: a successful traversal-bin producer was
occasionally followed by an auxiliary dispatch that reset its global ready flag, returning
Formal to `post-cull compatibility classification`. Only successful producers now update the
frame/camera stamp; non-producing work cannot erase it. Camera and Hybrid indexed construction
also use an exact GPU-written indirect dispatch instead of launching the full 87381-cluster
capacity for partial or empty chunks. Hybrid can consume the probe-selected second indexed
chunk, eliminating the procedural suffix of the 115886-cluster camera queue while preserving
the asynchronous software bin.

Terminal/disconnected geometry previously survived until 0.125 camera pixels and 0.25 shadow
texels, contradicting the pipeline's pixel budget and producing distant moire. The production
cut now retires terminal detail below 0.5 camera pixels and one shadow texel. Shadow geometric
error starts at two texels and rises conservatively to four in the far cascade, matching CSM
filtering/world-texel density without changing the near-camera LOD threshold.

Formal material resolve also folds each textureless URP/Lit state family into the first
textured compatibility bin with the same shader state. Those pixels retain exact per-material
parameters and skip texture samples by their material flags; textured pixels retain the exact
compatibility-bin comparison. This removes one resolve batch from the homogeneous Toyota
acceptance scene and prevents textureless parameter variants from adding SetPass work in a
heterogeneous scene.

### Runtime acceptance: `UnityNanite_2026-07-29_16-06-19`

The next 154-instance 4K capture improved again without changing the rendered scene: shadow
raster was 2.431 ms, Formal was 2.286 ms, the representative GPU frame was 8.31 ms and the
1621-frame median was 8.633 ms. This is a real reduction from the 15:04 GPU frame of 10.11 ms,
but the Editor Statistics counter stayed near 115 FPS. Raw thread data explains the apparent
plateau: the Main Thread averaged 9.884 ms, including 4.391 ms waiting for presentation and
1.787 ms collecting Profiler GPU samples. URP single-camera CPU work averaged 1.423 ms,
RenderGraph recording 0.292 ms, and the Nanite GPU-cull dispatch marker 0.093 ms. There is no
multi-millisecond Nanite CPU traversal hotspot in this capture; Editor/Profiler presentation
and the 8.3-ms GPU frame are the current bounds.

The Hybrid log still reported `post-cull compatibility classification` even though the spatial
traversal produced valid raster bins. RenderGraph records the consumer before FirstCull
executes, so querying execution-time frame stamps at record time can never admit that queue.
Hybrid now uses a record-time producer capability contract and RenderGraph dependency, while
the frame/camera stamp remains an execution-time diagnostic. The contract is based on the
actual direct Part/Cluster producer resources and no longer depends on the optional CPU-BVH
preference. The separate classification pass is retained only for genuine compatibility
paths.

The probe also measured shadow queues `9917, 41130, 82388, 85470`. With four equal atlas tiles,
the distant cascades were spending roughly four to eight times their useful texel coverage on
sub-texel geometry. Shadow error allocation now scales geometrically by cascade footprint
(`1,2,4,8` over the two-texel base target), while terminal disconnected detail still survives
to one texel. This is the shadow-space counterpart of the camera pixel budget and targets the
remaining 2.431-ms shadow raster cost without weakening near-cascade detail.

Finally, adaptive material-tile classification is admitted by the number of executed
shader/texture bins rather than the number of Unity `Material` objects. Parameter variants
folded into one family/compatibility bin no longer cause a redundant 4K classification pass;
heterogeneous scenes with multiple real execution bins still use tile rejection.

Material tile rejection is now a true GPU execution queue rather than a full tile grid with
off-screen vertices. Classification appends each occupied tile to one of 32 shader-family and
32 compatibility-Bloom queues and writes 64 indirect draw records. Resolve submits the exact
queue for the current bin; Bloom collisions only add fragment comparisons and cannot select a
wrong material. Textureless families folded into a textured state bin publish that bin in
`GpuMaterialData`, keeping CPU batching, tile compaction and fragment exact-match semantics
identical. Consequently heterogeneous material cost scales with the tiles touched by each
execution bin instead of `screen tiles * material bins`.

### Runtime acceptance: `UnityNanite_2026-07-29_16-38-44`

Traversal-produced bins were correct and the editor counter reached 134.4 FPS at 4K, but the
representative GPU frame regressed to 9.005 ms. Formal indexed HW raster was 2.211 ms while the
async compatibility SW path cost another 1.136 ms; compared with the 16:06 hardware-only
Formal cost of 2.286 ms, moving work at the public 32-pixel UE threshold saved only 0.075 ms
and added over one millisecond. Four-cascade shadow raster was also the largest GPU marker at
3.274 ms. Raw CPU data moved in the opposite direction: FirstCull record/submit fell from
0.2815 to 0.2148 ms and RenderGraph execution recording from 0.7145 to 0.5567 ms. This capture
therefore rejects CPU traversal and feature accumulation as the active optimization target.

The HW/SW split now uses a backend-specific cost model instead of copying UE's native-atomic
threshold as a universal constant. A clip-safe cluster must both fit the 16-pixel upper guard
and have conservative projected sphere coverage no greater than one pixel per triangle. This
keeps genuinely dense micropolygons on async SW raster while returning ordinary small clusters
to indexed HW, whose measured crossover is much better in Unity's two-atomic-pass plus sparse
merge implementation.

Shadow indexed construction now packs only real triangles. Previously every admitted cluster
reserved and drew the Bake maximum of 128 triangles, filling unused slots with degenerates;
this wasted shadow vertex work precisely inside the 3.274-ms draw marker. Each cluster group
now reserves `triangleCount * 3` indices from the cascade's existing safe fixed region and the
indirect index count is the exact atomic sum. Camera Formal keeps its fixed layout because its
fragment stage still needs deterministic `SV_PrimitiveID -> cluster/triangle` addressing;
shadow depth does not, so the optimization is exact and requires no new ID buffer or readback.

Static admission after this change compiles all 52 compute entry points with DXC SM6.0
`-Ges -WX`, compiles the runtime C# assembly with Unity Roslyn, and passes scoped
`git diff --check`. Runtime acceptance should compare `HybridSoftwareRaster`, Formal HW and
`Draw Main Light Shadowmap` against this capture; a frame-rate-only comparison is insufficient
because profiler collection and presentation wait dominate the Editor CPU graph.

## 2026-07-29 16:55 capture: restore the mature visibility/streaming contracts

Capture `UnityNanite_2026-07-29_16-55-17` establishes the real baseline for this closure:

- representative GPU frame 9.453 ms, median frame 7.613 ms;
- Formal visibility 2.964 ms (`HW=2.032`, async compatibility SW `=1.060`);
- four-cascade shadow raster 2.631 ms, shadow cull 0.488 ms;
- actual camera cut 115,886 clusters / 13,020,752 triangles;
- shadow cuts 9,917 / 39,657 / 74,089 / 70,686 clusters.

`Formal Resolve tri=600925` was only unique source geometry and has been renamed
`uniqueGeometryTriangles`; it must not be interpreted as per-frame raster work.

### Geometry bounds are not LOD error bounds

The previous Bake stored one group-level error sphere in every coarse meshlet's
`selfSphere`. Frustum, HZB, spatial BVH, shadow culling and HW/SW admission then treated that
large shared sphere as the meshlet's geometry. This defeats exactly the hierarchy that a
GPU-driven renderer depends on.

The ABI now carries three distinct values:

- `geometrySphere`: tight per-meshlet geometry bound, used only by visibility/raster routing;
- `selfSphere`: generating-group error sphere, used only to project the self LOD error;
- `parentSphere`: parent-group error sphere, used only to project the parent LOD error.

Initial and generated meshlets obtain `geometrySphere` and the meshoptimizer normal cone from
`meshopt_computeClusterBounds`; Part bounds and spatial BVH nodes merge tight geometry bounds.
Page NPG1 V2 stores the additional sphere and packed cone in an 80-byte cluster record while
remaining able to read V1 (`geometrySphere=selfSphere`, cone fail-open). Camera culling uses
the official meshoptimizer sphere-cone equation. Four-cascade directional shadows use the
infinite-light form only for positive, near-uniform transforms and one-sided materials.

### Conservative main/post HZB, not prev-visible filtering

The old implementation mixed a previous-visible bit mask with occlusion and still copied or
ORed roughly 780k uint entries three times per frame even though the shader no longer consumed
that history. The production contract is now:

1. main pass tests the selected cut against the previous-frame HZB;
2. rejected clusters enter the post-pass candidate mask;
3. the main pass raster builds the current HZB;
4. the post pass retests only candidates and appends disocclusions directly to the same
   visibility and HW/SW queues.

The redundant full-scene mask merge and history copy are removed. HZB reduction now stores
the farthest depth of every tile (min for reversed-Z, max for conventional Z). Occlusion uses
a projected sphere rectangle, a mip covering that rectangle and four conservative samples;
the earlier nearest-depth/center-only test was not a valid occlusion proof.

This follows NVIDIA `vk_lod_clusters/shaders/culling.glsl` (`intersectHiz`, far HIZ and a
rectangle-derived mip) and its two-pass build setup rather than adding another Unity-specific
visibility heuristic.

### Priority Page residency and compact Bake products

The request UAV is now one uint priority per virtual Page. Traversal performs an atomic max of
projected geometric error, and asynchronous readback sorts missing Pages by that priority
before applying decode/upload budgets. Resident writes remain usage touches for LRU. This
implements the near/high-error ordering called out in NVIDIA `docs/streaming.md` while
retaining root pinning, generation-safe double tables and fence-quarantined eviction.

Bake still produces one LZ4-compressed bulk payload, but Page metadata is now stored as
sub-assets of the single `NaniteMesh` asset. A re-Bake removes the legacy `_pN.asset` and
per-Page `.bytes` products, leaving one mesh asset plus one bulk payload instead of dozens of
top-level files. Runtime draw paths continue to consume the 64-byte decode table and decoded
resident cache; no NPG header parsing returns to a triangle or pixel hot path.

### Source correspondence and static gate

The implementation was checked against:

- NVIDIA `vk_lod_clusters/docs/lod_generation.md` and `docs/streaming.md`;
- NVIDIA `shaders/culling.glsl`, `traversal_run_groups.comp.glsl` and
  `render_raster_clusters_sw.comp.glsl`;
- Nyx `MiniEngine/Model/MeshletBuilder.cpp` for permissive attribute simplification,
  per-generation locks and packed-attribute protection;
- meshoptimizer `meshoptimizer.h` for the normal-cone contract.

All 52 Nanite compute entry points compile with DXC SM6.0 `-Ges -WX`. Runtime and Editor
assemblies compile with Unity 6000.3.10f1 Roslyn; only the three pre-existing Unity API
warnings remain. Runtime acceptance requires a V2 re-Bake because V1 assets deliberately
fail open to the old broad geometry sphere and cannot demonstrate the new culling result.

## 2026-07-29 18:03 experiment: retained main/post VBuffer (rejected)

> This experiment is not the current production path. Capture
> `UnityNanite_2026-07-29_18-26-38` proved that its Pass2 ownership contract was invalid in
> this implementation, so the code was fully rolled back. The description below records the
> rejected design and must not be read as an accepted optimization.

The 154-instance, 4K capture measured 7.436 ms on the GPU. Formal Visibility was 2.137 ms,
the four-cascade shadow raster was 1.403 ms and asynchronous software raster was 0.601 ms.
The remaining structural waste was `Nanite/WriteDepth` at 1.042 ms: the main cut was first
drawn into depth for current-frame HZB, then the merged main/post cut was drawn again into the
formal VBuffer. HZB construction and post culling themselves cost only 0.051 and 0.032 ms.

The rejected RenderGraph path attempted a main/post visibility contract:

1. FirstCull emits the main HW/SW bins using previous-frame HZB.
2. The former WriteDepth event rasterizes those bins directly into the retained formal
   VBuffer and camera depth. There is no separate depth-only geometry draw.
3. Current HZB is built from that formal main depth.
4. Before post traversal, the already-consumed append storage is reset in place. SecondCull
   therefore emits only disocclusions, without allocating a second full-scene queue.
5. The post HW/SW raster preserves the main VBuffer and appends only recovered geometry.
6. Material resolve consumes the combined retained VBuffer once.

This matches the two-pass ownership in NVIDIA `build_setup.comp.glsl` and
`traversal_run_groups.comp.glsl`: pass 1 work is consumed before `setupSecondPass`, pass 2
rejects already-rendered work and emits only clusters visible against current HIZ. HW/SW
selection remains traversal-time binning as in `rasterBinning` and the clipped-size test in
`traversal_run_groups.comp.glsl`; it is not reclassified per object on the CPU.

Because URP native GBuffer geometry can overwrite camera depth after the early main raster,
the resolve path takes one R32 final-depth snapshot and conservatively rejects retained
Nanite IDs that no longer own the depth sample. This is a screen copy rather than another
geometry raster and preserves mixed ordinary/Nanite occlusion correctness.

Low-frequency telemetry is now automatic and asynchronous. Every 120 Game-camera frames the
GPU publishes main clusters, previous-HZB rejects and post recoveries; the admission audit
also publishes main HW/SW queue counts and a one-shot mip/triangle distribution. No
synchronous `GetData` or per-frame full queue readback is added.

Its acceptance criterion was the disappearance of the old ~1.042 ms depth-only geometry
draw. Runtime evidence rejected that hypothesis before the path was admitted.

## 2026-07-29 18:26 rejection: restore merged raster and gate HZB by measured benefit

Capture `UnityNanite_2026-07-29_18-26-38` regressed from roughly 142 FPS to 105 FPS. GPU time
rose from 7.436 ms to 8.89 ms and Formal Visibility rose from 2.137 ms to 5.128 ms, while the
new `Nanite/MainVisibility` draw was only 0.117 ms. Telemetry simultaneously reported
`cameraMainClusters=92924`, `hzbRejected=0`, `postRecovered=0`.

The cause was deterministic: `kCompactSelectionPass2` is deliberately ineligible for the
direct visible draw queue. The post compact therefore scanned the merged visibility mask;
when indexed state was invalid, Formal reset indirect arguments to the full mesh and redrew
the approximately 92,924-cluster cut. The retained main/post VBuffer experiment, depth
snapshot validation and post queue reset have been removed. Formal again compacts and
rasters one merged selection using the previously validated path.

HZB admission now uses asynchronous Pass2 telemetry instead of scene cluster count alone.
The measured useful reject count is `max(0, pass2Candidates - pass2Recovered)`. Three probe
samples below both 128 clusters and 0.2% reject ratio suppress the complete WriteDepth,
BuildHzb and SecondCull chain for 600 frames. A short periodic probe re-admits it when a view
becomes meaningfully occluded. Registry, camera, geometry-generation and material resolution
changes reset the controller and invalidate stale HZB history. No synchronous readback or
new Inspector switch was added.

## 2026-07-29 18:40 acceptance and NPG1 V3 raster metric closure

Capture `UnityNanite_2026-07-29_18-40-51` accepts the rollback and benefit admission. At 4K
with 154 instances, the representative GPU frame is 6.727 ms, Formal Visibility is 1.863 ms,
the four-cascade shadow raster is 1.392 ms, and the Editor counter is about 154 FPS. Workload
telemetry reports 92,924 main clusters, zero HZB rejects/recoveries, enters cooldown on frame
30, and periodically re-probes without retaining the rejected main/post VBuffer path.

The next completed feature is the formal HW/SW raster cost input. The previous traversal bin
treated a cluster's complete bounding-sphere diameter as its longest triangle edge. That can
keep genuinely dense, spatially broad meshlets on HW and is not the NVIDIA algorithm.

- Bake computes the exact object-space longest edge of every generated meshlet.
- NPG1 V3 adds the scalar to its cluster record (84 bytes); V1/V2 decode with the old sphere-
  diameter behavior, so migration is conservative rather than destructive.
- GPU Scene layout 15 publishes the scalar once per unique mesh cluster. Instance scale and
  camera projection turn it into pixels during spatial traversal; no vertex decode or CPU
  per-instance classification is introduced.
- Clip-plane safety and the measured Unity compatibility-backend density gate remain intact.
  Only dense clusters whose actual longest edge is below the SW threshold move to the async
  queue; sparse or clipped clusters remain on indexed HW.
- The non-spatial compatibility classifier consumes the same unique-geometry metric buffer,
  so fallback and production paths no longer disagree.

This implements the `bbox.longestEdge / bbox extent` decision in NVIDIA
`vk_lod_clusters/shaders/traversal_run_groups.comp.glsl` and its cluster-bounds construction
in `scene_cluster_lod.cpp`, adapted to the existing sphere projection and non-uniform instance
scale. Static admission compiles all 45 affected compute kernels with DXC SM6.0 `-Ges -WX`,
compiles the Unity C# assembly, and passes scoped `git diff --check`. Runtime acceptance
requires one V3 re-Bake and compares the automatic `hybrid=hw:...,sw:...` probe plus Formal HW
and async SW timings against the 18:40 capture.

## 2026-07-29 shadow scratch lifetime and end-to-end HZB admission

The V3 re-Bake changed the HW/SW split but did not improve the complete frame. Telemetry made
both masking costs explicit: the camera cut was 63,029 clusters and HZB rejected 29,895, while
the four shadow cuts totalled 150,699 clusters. The old shadow index scratch admitted only
87,381 simultaneous cluster slots, so roughly 63k shadow clusters still used the procedural
vertex path. HZB also paid about 0.84 ms for WriteDepth, BuildHzb and SecondCull before its
Formal saving was known.

The native URP shadow graph now gives the shared index allocation the correct lifetime:

1. one fused traversal emits all four cascade-local queues;
2. cascade N builds its compact real-triangle index stream into the complete scratch buffer;
3. cascade N raster consumes it immediately;
4. only then may cascade N+1 overwrite the scratch.

RenderGraph ordering fences encode `build0 -> draw0 -> build1 -> draw1 ...`; normal URP shadow
renderer lists remain in the matching cascade raster pass. The buffer stays bounded at the
existing budget and no longer needs four simultaneous slices. Admission telemetry therefore
compares the largest individual cascade with capacity (`shadowReuseMax`), not the meaningless
sum of four non-overlapping lifetimes. A procedural tail remains only as a correctness fallback
when one individual cascade alone exceeds the whole allocation.

Adaptive HZB admission now measures the counterfactual instead of inferring it from reject
counts. Unity GPU recorders asynchronously sample:

- HZB-on total = WriteDepth + BuildHzb + SecondCull + Formal;
- HZB-off total = Formal without the occlusion chain.

After GPU-delay-safe warm-up, twelve samples of each mode are compared with a 0.05 ms margin.
The faster mode remains active for 600 frames and is then re-probed, so camera motion or a new
occluder layout can change the decision. This adds no synchronous GPU readback and no Inspector
switch. Reject-count admission remains only as the fallback on platforms without GPU marker
timings.

## 2026-07-29 production HW/SW tile raster module

The former compatibility software path was not a production rasterizer. One lane owned one
triangle, serially walked its complete pixel bounding box, and then repeated that walk in a
second kernel to recover the winning primitive. Its tile list only reduced clear and merge
coverage. This explains why moving clusters from HW to SW often added roughly one millisecond
instead of reducing the complete frame.

The camera path now uses a bounded GPU linked tile-work queue:

1. traversal still emits HW/SW cluster bins directly;
2. each SW triangle is transformed and set up once, then linked into only the 16x16 tiles it
   overlaps;
3. one workgroup owns one covered tile, cooperatively loads triangle batches, and keeps each
   pixel's best depth plus primitive in registers;
4. every covered pixel writes depth and winner exactly once;
5. queue overflow atomically marks the complete cluster, appends it back to the HW bin once,
   and invalidates its partial SW work. Geometry is never dropped to meet a budget.

Setup and raster are separate RenderGraph passes. After setup finalizes overflow and the HW
indirect count, async SW tile resolve and indexed HW build/raster consume the same fence in
parallel. The sparse tile merge retains the existing RG32UI VBuffer ABI. This is the Unity
fallback for the packed 64-bit visibility atomic used by Unreal Nanite and NVIDIA's
`vk_lod_clusters/render_raster_clusters_sw.comp.glsl`; it avoids pretending that ShaderLab
exposes an unsupported typed 64-bit UAV atomic while preserving one-winner correctness.

The same classifier, linked queue, node-overflow rule and scratch allocation now service all
four directional shadow cascades. The existing lifetime remains
`build0 -> draw0 -> build1 -> draw1 ...`, so neither index scratch nor tile work is multiplied
by four. Shadow SW setup applies the same directional depth and normal bias inputs as URP's
hardware `ShadowCaster`, and a depth-only sparse tile pass merges into the native cascade
viewport. The normal Mesh renderer list remains untouched.

Camera and four-cascade shadow have independent end-to-end GPU admission. Each measures
hybrid and hardware-only for eight warm-up plus twelve sample frames, keeps hybrid only when
the complete measured stage wins by at least 0.05 ms, and re-probes after 600 frames. The
controller is reset by camera, resolution, registry or geometry-generation changes. It adds
no synchronous readback and no serialized Inspector tuning switch. HZB admission settles
first so its own A/B switch cannot contaminate the hybrid comparison.

Static acceptance completed:

- all new compute entry points compile with DXC SM6.0, strict syntax and warnings-as-errors;
- Nanite runtime C# compiles against the modified URP runtime;
- URP runtime compiles with the shadow-bias context extension;
- scoped `git diff --check` passes.

Runtime acceptance is intentionally one combined checkpoint. Wait for both
`[Nanite][HybridCostProbe] camera` and `four-cascade-shadow`, then capture an unprofiled FPS
and one GPU Profiler frame. The production result is valid whether a stage selects `hybrid`
or `hardware-only`: admission must retain the faster complete path, preserve all four shadow
cascades, and show no missing surfaces or UV/material regression.

## 2026-07-29 20:04 HW/SW module runtime acceptance

Capture `UnityNanite_2026-07-29_20-04-55` validates the complete controller and fallback:

- camera hybrid: 2.340 ms;
- camera hardware-only: 2.040 ms;
- four-cascade hybrid shadow: 5.586 ms;
- four-cascade hardware-only shadow: 1.556 ms;
- both decisions: `hardware-only`, with a scheduled 600-frame re-probe;
- HZB independently selected enabled: 3.115 ms versus 3.349 ms;
- unprofiled 3840x2160 Game view: about 186 FPS (5.4 ms), Main Thread about 4.3 ms;
- GPU capture: Formal Visibility 3.270 ms, main-light shadow atlas 0.918 ms;
- visual submission: no reported holes, UV regression or cascade failure.

This accepts the module's correctness, scheduling, bounded-memory behavior and production
cost closure. It does not prove a positive SW crossover on the tested DX12 GPU. In this
workload HW raster remains substantially faster, especially for four 1024-pixel shadow
cascades, so the production result correctly pays neither SW path in steady state. A future
native 64-bit visibility atomic/subgroup-specialized backend can improve the crossover, but
is not allowed to regress this accepted hardware-only floor.

### Project completion after this checkpoint

| Module | Architecture implemented | Runtime accepted |
|---|---:|---:|
| GPU Scene | 90% | 75% |
| GPU traversal / indirect queue | 92% | 82% |
| Camera VBuffer / indirect raster | 90% | 82% |
| Four independent shadow cascades | 93% | 90% |
| Page format and compression | 88% | 78% |
| Residency / streaming | 82% | 65% |
| Bake / continuous LOD / hierarchy quality | 78% | 68% |
| HW/SW hybrid raster | 92% | 72% |
| Heterogeneous materials and textures | 78% | 45% |

The HW/SW runtime percentage is intentionally lower than its architecture percentage: both
paths execute correctly and self-select, but only the HW floor wins on this capture. The
largest remaining product gap is heterogeneous shader/texture execution; the largest
geometry-quality gap is a higher-quality continuous hierarchy bake. Streaming needs eviction
pressure, camera teleport and multi-asset stress acceptance rather than more nominal code.

## Atomic producer-group traversal correction (2026-07-30)

The 154-instance stress scene exposed that per-cluster screen-density rejection is not a
valid Nanite LOD operation. A producer group is the atomic replacement edge in the DAG:
either its complete coarse set or its complete refined set is emitted. Removing individual
members reduced the camera queue from 152,784 to 87,312 clusters, but created holes,
flicker and invalid material coverage. That experiment was fully removed.

Production camera traversal now uses the bounded breadth-first hierarchy queue
(`GPU-RootGroup-RefineQueue-DrawIndirect`). Four-cascade shadow traversal uses the same
atomic rule with a cascade mask (`shadowFrustum/fusedHierarchyQueue`). The persistent
queue remains disabled because bounded dispatch is the validated non-TDR path. Formal,
depth and shadow raster remain `Cull Off` until bake metadata stores and validates a stable
front-face convention.

Latest cold DX12 Player smoke (300 rendered frames, exit code 0):

- camera atomic cut: 129,874 clusters;
- shadow maximum: 139,968 clusters, down from the spatial path's 155,088;
- no invalid kernel, missing resource, UAV overflow or RenderGraph error.

The remaining queue cost must be removed in the bake/root hierarchy, not by punching
individual clusters out of a valid cut.

## meshoptimizer clusterlod conformance: seam protection (2026-07-30)

The bake previously marked UV, normal and tangent discontinuities as `Protect`, and also
marked both the non-canonical wedge and its canonical position representative. On the Toyota
asset this protected roughly 42% of all vertices and made hard-surface branches become
terminal root sets.

The implementation now follows meshoptimizer's official `demo/clusterlod.h` contract:

- permissive simplification protects UV discontinuities only;
- normal and tangent variation stays in the attribute error metric instead of becoming a
  topological constraint;
- only the non-canonical UV wedge receives `SimplifyVertex_Protect`;
- dynamic inter-group `Lock` bits are still rebuilt every hierarchy generation and propagated
  by position, while `Protect` remains per wedge.

References used for this correction:

- `meshoptimizer/demo/clusterlod.h`, `clodDefaultConfig`, `lockBoundary`, and `clodBuild`;
- meshoptimizer README sections "Attribute-aware simplification" and "Permissive
  simplification";
- UE Nanite's invariant that a hierarchy node/group is the indivisible refinement unit.

This change only affects newly baked assets. Its acceptance gates are lower resident root
triangles and lower camera/shadow queues with unchanged UV seams, no holes and no flicker.

### Isolated full-bake and DX12 Player result

The Toyota asset was fully rebuilt in `D:\UnityNanite_CodexSmoke_20260730`; no user scene or
main-project baked asset was overwritten. The hierarchy audit passed every fatal contract:
membership, mip ownership, error monotonicity, parent/group containment and triangle
reduction all reported zero violations.

- UV seam Protect vertices: 98,791 -> 8,500;
- resident root triangles: 48,926 -> 17,067 (-65.1%);
- hierarchy: 5,269 clusters, 322 groups, max mip 6;
- packed pages: 63, 13.99 MiB packed / 10.98 MiB LZ4 storage;
- camera atomic cut: 129,874 -> 83,036 clusters (-36.1%);
- maximum four-cascade shadow queue: 139,968 -> 64,204 clusters (-54.1%);
- all camera/shadow queues fit one packet chunk; 300-frame DX12 Player exited cleanly.

The renderer still uses the complete producer-group cut and `Cull Off`; the queue reduction
comes entirely from a less over-constrained hierarchy. This is the first current-stage
optimization that materially lowers both camera and shadow geometry without deleting an
individual selected cluster.

### UV precision correction

Page V3 previously selected FloatUV only for non-finite values or values outside the half
range. Half remains representable at large tiled UV coordinates but loses absolute precision;
the Toyota bake passed the former tolerance with a measured maximum UV error of `0.4421`.
This explains material samples moving to visibly different texture regions even when triangle
identity and residency were correct.

The encoder now round-trips every page UV through half before choosing its format. A page
keeps HalfUV only when the absolute error is at most `1/4096`, otherwise it uses the already
supported FloatUV flag and decode path. Isolated full-bake results:

- maximum UV decode error: 0.4421 -> 0;
- LZ4 page storage: 10.98 MiB -> 12.29 MiB;
- format-aware Page planning: 72 pages at 88.8% average fill (the old HalfUV-only
  estimate would late-split the same data into 105 pages);
- hierarchy/root counts unchanged;
- all page codec validation and hierarchy contracts passed.

The approximately 1.31 MiB storage increase is intentional correctness data, localized to
pages whose tiled UVs cannot meet 4K sub-texel precision in half. Ordinary 0..1 UV pages
remain compact.

## Formal raster/resolve and LOD correctness closure (2026-07-30)

FloatUV removed Page payload quantization error, but it could not fix the later screenshot's
camera-dependent fragments and wrong high-detail material regions. Two independent runtime
contract violations remained:

1. hierarchy refinement was AND-ed with a projected bounding-sphere area / coarse-triangle
   density heuristic. That value is not a conservative visual-error bound and can stop on a
   coarse producer group even while its attribute/geometric error is many pixels. Camera and
   all four shadow cascades now select a cut exclusively from the baked absolute error and Page
   residency, matching meshoptimizer clusterlod/Nanite's error-driven refinement contract;
2. Formal Resolve used `GetNormalizedScreenSpaceUV` for both `Texture.Load` and clip-space
   reconstruction. URP applies `_ScaleBiasRt` to that UV, after which the Nanite helper flipped
   Y again. Raster writes and Resolve reads could therefore refer to different RT pixels. The
   VBuffer now loads in native `SV_POSITION` pixel space, barycentrics use that same raw pixel
   centre, and only lighting receives URP's orientation-adjusted normalized UV.

Resolve no longer clamps an invalid barycentric solution onto a triangle edge. A decoded
triangle must cover the current pixel within a 1% numerical edge tolerance or the pixel is
discarded. This makes an ID/address regression fail visibly and locally instead of painting a
large surface with an unrelated triangle's UV. The same validation is used by the depth slice.

The unversioned meshoptimizer cone test is temporarily fail-open for camera and shadow. Bake,
Unity import and procedural raster do not yet store one proven front-face convention; applying
an assumed cone sign was able to reject complete front-facing clusters. Frustum, screen-error,
residency and optional HZB culling remain active.

Hierarchy format V2 records that `maxParentLodError` is the absolute error returned by the
attribute-aware simplification which actually produced the replacement geometry. V1 assets
are rejected from hierarchy traversal and must be re-Baked. Runtime scene construction also
audits the immutable packet address domain: every Page index must be in range and every Page
triangle must have exactly one cluster owner before exact packet raster is admitted.

Isolated acceptance in `D:\UnityNanite_CodexSmoke_20260730` completed with Unity 6000.3.10f1:

- full Toyota V2 Bake: 5,273 clusters, 323 groups, max mip 6, 71 Pages;
- all hierarchy/DAG fatal counters zero; UV decode error zero;
- DX12 Standalone Player build succeeded with all runtime shader variants;
- 300 rendered frames completed without invalid kernels, missing bindings, Page/triangle
  address errors or GPU crash;
- after streaming settled, the hierarchy queue remained stable at 129,715 camera clusters
  and the four shadow queues remained within the exact packet capacity.

## Watertight V3 hierarchy and cumulative error closure (2026-07-30)

The remaining close-camera coarse geometry, popping, holes and flashing were reproduced in
the isolated DX12 Player. A forced `0.01px` leaf-quality run matched an ordinary MeshRenderer,
which isolated the defect to hierarchy replacement rather than Page decode, VBuffer identity
or material Resolve.

Two topology/selection violations were corrected:

1. error propagation now matches meshoptimizer `demo/clusterlod.h` exactly:
   `max(previous_error, simplify_error) + simplify_error`. V2 only retained the maximum and
   therefore under-reported accumulated replacement damage, selecting coarse geometry close
   to the camera;
2. each independently simplified producer group now carries an exact canonical shared-edge
   contract. A replacement that removes a cross-group edge is rejected and becomes terminal,
   preventing T-junctions. After an atomic group cut is selected, camera and shadow code no
   longer delete individual non-terminal clusters merely because their own bound is below one
   pixel/texel; that post-cut optimization was topology-unsafe and directly created holes.

This changes the hierarchy ABI to V3; V1/V2 assets fail closed and require a re-Bake. Isolated
Toyota acceptance at the production `1px` threshold produced:

- 4,924 clusters, 302 groups, max mip 6 and 67 Pages;
- 276/302 successful simplifications; 20 unsafe shared-edge replacements rejected;
- mip triangles `312269 -> 151935 -> 67459 -> 29388 -> 13114 -> 8026 -> 1726`;
- all DAG/hierarchy fatal counters zero, 11.36 MiB LZ4 Page storage;
- 432-instance settled camera cut: 369,615 clusters, versus 653,010 for forced fine geometry;
- the V3 Nanite and forced ordinary-Mesh reference images differ by only about `0.5/255`
  mean absolute RGB at the fixed view;
- two settled fixed-camera captures 25 frames apart changed only 0.298% of pixels, with no
  full-background hole transitions; no invalid kernel, missing binding or Page address error.

The remaining performance work must reduce valid producer-group cuts or raster cost; deleting
members from an already selected cut is no longer an admissible optimization.

## V4 attribute metric and previous-HZB quarantine (2026-07-30)

The V3 hierarchy still mixed dimensionless tiled UV/tangent values into an error later
projected as object-space distance. V4 follows meshoptimizer's official Nanite example:
normal weights are `0.5`, UV/tangent QEM weights are zero, and UV discontinuities remain
topology-Protected. The exact shared-edge contract and cumulative
`max(previous,current) + current` propagation remain mandatory.

The isolated V4 Toyota Bake produced 4,870 clusters, 294 groups, 66 Pages and max mip 5.
Mip triangle counts are
`312269 -> 151018 -> 67756 -> 30828 -> 11196 -> 5667`; 24 replacements which broke a
shared canonical edge were conservatively rejected. The production 1-pixel, 432-instance
camera cut is 365,865 clusters. This is still too expensive, but it is a complete valid cut;
post-cut member deletion is not an acceptable way to reduce it.

The fused Formal WriteDepth pass previously failed to publish `depthWrittenThisFrame`, so
the HZB history was never actually built. Fixing that flag exposed a second issue: existing
previous-frame instance, hierarchy-group and cluster rejection is not conservative under
self-occlusion. It reduced the queue substantially but produced catastrophic holes and
flashing. Production therefore now:

- keeps the fused depth-state publication fix and current-frame HZB infrastructure;
- removes instance and hierarchy-group HZB rejection from both bounded and persistent
  traversal;
- hard-quarantines previous-frame HZB admission regardless of stale Renderer Feature
  serialization;
- preserves the complete atomic producer cut until a conservative two-phase node test has
  its depth convention and image-difference contract proven.

Safe-path isolated DX12 acceptance (Unity 6000.3.10f1, exit code 0):

- all 66 Pages became resident; no invalid kernel, missing binding, UAV overflow or Page
  address error;
- settled camera cut stayed at 365,865 clusters with `previousHzbThisDispatch=False`;
- two fixed-camera captures 25 frames apart had mean RGB delta `0.029/255`, with only
  `0.166%` of pixels changing by more than 8 levels;
- Nanite versus ordinary MeshRenderer reference had mean RGB delta `0.458/255`;
- the validated V4 `.asset` and Page payload were copied to the main project, replacing the
  incompatible V3 runtime asset.

The next performance milestone is conservative hierarchy-node occlusion plus a denser valid
producer hierarchy. It must reduce complete cuts, pass automated reference/temporal image
gates, and never remove individual members after a cut has been selected.

An isolated runtime-derived group-AABB experiment was also rejected rather than shipped. An
8-corner clip-space rectangle lowered the settled camera queue from 365,865 to about 201,000,
but still changed roughly 8% of pixels by more than 8 RGB levels and exposed visible holes.
Increasing the depth guard and testing both render-target Y conventions did not satisfy the
reference gate. The required next design is therefore a UE-style previous-visible first pass
plus an explicit uncertain/disocclusion recovery queue, not direct node deletion during the
first hierarchy traversal.

That recovery queue was prototyped in the isolated project as a second gate. Pass1 retained
each previous-HZB-rejected canonical producer task, current depth omitted those tasks, and
Pass2 traversed them again against the newly built HZB. The queue remained within the eight
UAV contract and reduced the final camera queue to about 247,000, but the reference image
still differed on roughly 6% of pixels by more than 8 RGB levels. This proves the remaining
fault is below queue scheduling: the HZB projection/orientation or hardware-depth comparison
contract itself is not yet trustworthy. The prototype was removed. Before re-enabling node
occlusion, the next implementation must visualize and validate projected bounds and HZB
samples against known occluder/occludee fixtures, then reapply the two-phase queue.

## Camera ownership and capability admission closure (2026-07-30)

The fixed standalone camera was stable while Editor Scene/Game views still flashed or lost
rows of instances. The cause was a camera-transient ownership violation, not another Bake
threshold problem. Compact validity used frame, selection and geometry generation but omitted
camera identity. The second camera in a frame could therefore keep the first camera's indirect
arguments while consuming its own newly-written draw queue. Indexed packet validity had the
same incomplete check at consumption. GPU visibility ownership is now explicit as well:
`GpuVisibleMaskReady` carries its producer camera ID and a different camera cannot relabel the
mask as prepared.

The Page policy is also now budget-driven. If all virtual Pages fit in the configured pool,
they are uploaded and transcoded once; root-only demand streaming is reserved for scenes that
actually exceed the pool. The 66-page Toyota asset therefore settles at `66/66` immediately
inside its 66 allocated slots instead of introducing residency-driven LOD changes in a pool
that already has room for the complete asset.

Finally, platform capability detection no longer silently admits unvalidated optional work.
Previous-HZB consumption remains quarantined, so HZB Depth/Copy/Build/Cull2 is not scheduled.
Hybrid and compact integer VBuffer paths require their explicit production setting; the
validated indexed hardware packet path remains available through negotiated DX12 capability.

Isolated DX12 dual-camera acceptance renders `main -> interference -> main` every frame:

- Player build succeeded with no C# or shader-kernel errors;
- all 66 Pages were resident before the first settled cut;
- 180 camera renders completed without invalid bindings, UAV overflow or Page errors;
- the main cut remained 365,865 clusters and exact indexed packet submission stayed valid;
- two main-camera captures had mean absolute RGB delta `0.077/255`;
- the 60-frame triple-camera smoke wall time fell from about 20 s to 16 s after removing the
  non-consuming HZB chain (startup included, so this is a directional rather than FPS metric).

The remaining visual transition work belongs to hierarchy attribute error and seamless
replacement quality. It must be solved in Bake/producer groups, not by adding another
per-cluster runtime deletion or an Inspector fallback switch.

## Reference hierarchy correction and distance audit (2026-07-30)

The prior V3/V4 notes above are historical results, not the current hierarchy contract.
Fresh comparison with `meshoptimizer/demo/clusterlod.h` at commit
`a6ecc73c094bdd5d09644f9286bbf25134853679` found two deviations which have now been
corrected:

- default clusterlod error propagation is `max(previous_error, current_error)`. The additive
  term is zero in `clodDefaultConfig`; repeatedly adding the current error inflated remote
  levels and prevented useful coarse cuts;
- projected perspective error is `error * (0.5 * screenHeight * cot(fov/2)) / distance`.
  The runtime multiplied this by two even though `_ProjectionScale` already contains the
  half-height factor.

The global exact-edge rejection added in V3 was also removed. It turned 24 producer groups
into terminal roots and raised the resident root set to about 53.8K triangles. Producer
replacement is instead checked for connected-component growth and extreme UV-edge-stretch
growth, with progressively less aggressive retries before a group is made terminal. The
meshoptimizer edge-length error limiter is applied after simplification, matching the
reference implementation's treatment of dense normal-attributed geometry.

`NaniteHierarchyDistanceAuditMenu` now validates hierarchy ranges and producer reduction,
then CPU-simulates the mutually-exclusive cut over six camera directions and twelve
distances. It reports selected clusters/triangles, mip histograms, duplicate geometry,
monotonic regressions, connected-component growth, UV-stretch outliers and resident roots.
The old Toyota V4 asset showed no duplicate traversal but retained roughly 109K-118K
triangles at 180 m and contained intermediate producer topology outliers, proving that the
remaining issue was Bake quality rather than duplicate GPU submission.

The corrected topology-preserving isolation Bake reduced the finite error maximum from 33.7
to 3.70 object units and resident roots from 53.8K to 17.1K triangles, with no UV-stretch
outlier. A UV-weight relaxation and a component-prune experiment were rejected because they
reduced root storage without materially improving the distance cut.

The official `simplifySloppy` terminal fallback is now exported by the native wrapper and
implemented with the same sparse-group deindex/restore procedure and `2x` error amplification
as `clusterlod.h`. Additional gates require all hard producer-boundary positions to survive,
forbid connected-component growth and reject extreme UV stretch. In isolation this produced:

- 5,330 clusters, 327 groups, max mip 12 and five roots;
- mip triangle counts
  `312269 -> 155916 -> 79203 -> 39831 -> 21317 -> 13292 -> 4009 -> 1711 -> 880 -> 696 -> 328 -> 164 -> 82`;
- resident roots reduced to 6,611 triangles;
- all DAG membership, mip, error-monotonicity, containment and reduction fatal counters zero;
- no new component-growth group and no UV-stretch outlier;
- monotonic, duplicate-free distance cuts in all audited camera directions.

This closes the missing reference fallback and makes the hierarchy complete, but it is not
claimed as the final performance or temporal-quality fix: the 4K/1px audit still selects
about 58K triangles at 180 m. The next Bake architecture step is the meshoptimizer-recommended
`meshopt_simplifyWithUpdate` path for aggressive upper levels, with per-level vertex payloads,
locked producer boundaries and UV/normal attribute updates. That change is required to lower
coarse-level geometric error instead of hiding it with runtime thresholds.

The updated native wrapper can be reproduced with `Native/API_CPP/build_zig.ps1`; the tested
source commit is printed by the script. The new DLL was validated in the isolated Unity
6000.3.10f1 DX12 Player build before deployment to the main project.

## Mesh-wide quantization, deterministic visibility and atomic density cut (2026-07-30)

The reported fixed-camera trim flicker and the intermediate-distance hood break were
reproduced in a one-proxy DX12 Player capture. Three independent defects were found instead
of compensating them with another exposed LOD threshold:

- Page V3 encoded positions on a Page-local UNorm16 grid. A shared source vertex therefore
  decoded to different positions on two Pages and opened a real streaming seam. All Pages
  now use one mesh-wide position grid while remaining independently compressed/streamed.
- the old hierarchy audit recursively treated regrouped clusterlod output as a tree. The
  hierarchy is a DAG; the audit and production cut now use the official mutually-exclusive
  consumer/producer group predicate.
- visible clusters arrive through unordered GPU append queues. Equal-depth coplanar material
  triangles could therefore choose a different VBuffer owner each frame. Hardware raster now
  uses a stable material key plus a full immutable-triangle hash depth tie-break.

The rebuilt isolation asset has 5,466 clusters, 328 groups, 74 Pages and a 5,933-triangle
resident root set. All fatal DAG, error, containment, Page-budget and binary round-trip checks
are zero. Mesh-wide quantization reduced exact Page-induced connected-component growth from
roughly 97 cases to one remaining producer-quality case. Ten identical camera frames now have
zero pixels changing by more than four RGB levels; the earlier strip-scale change is gone.

The remote-density problem was also structural. Geometric error alone retained about 47.7K
triangles at 64 model radii because many conservative upper-level group spheres overlap on
screen. Runtime now stores a per-group sum of tight fine-Cluster coverage radii and triangle
count. A group refines only while its geometric error is visible and its complete fine side is
not already over the conservative screen-space triangle budget. Both fine and coarse sides
look up the same group decision through immutable consumer/producer IDs, so the budget cannot
delete individual clusters or recreate a transition gap. The same rule is applied per shadow
cascade.

At 2160p/1px, the CPU mirror audit is unchanged through 0.5 model radii, changes the 1R cut by
less than one percent, and reduces the 64R cut from 47,745 to 7,694 triangles with no monotonic
regression or duplicate geometry. The 432-instance DX12 smoke completed 300 shadowed frames
with all 74 Pages resident, no invalid kernel/resource/UAV errors, and the fixed/dolly captures
showed no break or temporal flash. Main-project geometry must be re-Baked once to receive the
mesh-wide quantization grid; the isolated Toyota assets were deliberately not copied over the
user's project.

## Geometric-error production baseline and formal A/B (2026-07-31)

The density override described in the previous historical section was not retained. It changed
the selected cut even when geometric error was still visible, which is why the user observed
rapid near-camera degradation followed by dense remote geometry. The production predicate now
uses projected object-space geometric error as the hard quality bound. Attribute-weighted
meshoptimizer error remains a candidate/topology metric and is never projected as metres.

The corrected isolation Bake produced 5,513 clusters, 17 hierarchy levels, 75 Pages, a
31-triangle resident root, 16.61 MiB packed / 12.94 MiB NZC1-LZ4 storage, and zero fatal DAG
contract counters. Finite geometric error is at most `0.399x` the model radius. A deterministic
52-frame dolly against the native MeshRenderer reference measured `0.224/255` mean RGB error,
`0.430/255` worst-frame MAE and no extra fixed-camera temporal change. A 4 MiB pool retained
16/75 Pages without missing-Page holes; the expected quality loss came from the resident coarse
cut, not random cluster deletion.

Same-player DX12 A/B at 1280x720, four cascades and HardwareOnly:

| Instances | Native MeshRenderer | Nanite |
|---:|---:|---:|
| 1 | 1072 FPS | 754 FPS (forced; production admission uses native) |
| 8 | 1012 FPS | 682 FPS (forced) |
| 24 | 577 FPS | 783 FPS |
| 48 | 330 FPS | 684 FPS |
| 96 | 174 FPS | 683 FPS |
| 154 | 110 FPS | 720 FPS |
| 432 | 40 FPS | 496 FPS |

This establishes the measured low-submission crossover between 8 and 24 instances. Production
defaults therefore return scenes with at most 12 fallback-capable instances / 16 source draws /
4M source triangles to URP, while dense large scenes stay GPU-driven.

The heterogeneous acceptance asset uses four material-boundary-preserving SubMeshes and eight
URP/Lit parameter/texture variants. Its Bake has 5,512 clusters, 357 groups, 76 Pages, one root
Page, no split producer group, no oversized Page and zero fatal DAG counters. At 154 instances
the current geometric-error cut submits about 69,372 clusters / 7.67M triangles and runs at
about 500 FPS; the exact same source Mesh and 616 ordinary SubMesh draws run at about 140 FPS.
Material ownership and texture slots match the native reference. Remaining foreground-level
lighting deviation is the known procedural reflection-probe binding difference, not a material
or UV remap; deleting its fallback was tested and rejected because metallic bins became black.

Forced Hybrid remains a valid but slower path on this GPU: approximately 415 FPS at 154 and
401 FPS at 432 instances, versus HardwareOnly's 720/496 FPS in the corresponding single-material
tests. NVIDIA `vk_lod_clusters` commit `70506fdc...` documents the same current limitation for
its compute raster path. Hybrid remains capability- and timing-gated instead of being forced.

Finally, arbitrary shader compatibility is now explicit. `NaniteMaterialResolveRegistry`
registers a source-program predicate, a VBuffer-to-GBuffer resolve Material and a per-bin binder.
Unregistered programs remain on their native Renderer; forced diagnostics skip unsupported bins
instead of silently applying URP/Lit semantics. The isolated registry lifecycle audit passed
registration, admission, scene-generation invalidation and disposal.

## Reproducible streaming and multi-geometry acceptance (2026-07-31)

The streaming harness now compares settled equal-camera endpoints. A 16 MiB pool loaded the
50 non-root Pages required by the path, performed zero eviction, and produced the exact fully
resident cuts over three cycles. The 4 MiB fault tier evicted 542 Pages in 720 frames and
changed the near cut; it is not a production capacity and is not used to claim temporal quality.

The first multi-unique-mesh A/B uses three baked meshes and 154 instances at 1280x720 with four
shadow cascades. Nanite rendered at 675.38 FPS (1.481 ms average), while MeshRenderer rendered
at 424.19 FPS (2.357 ms). Runtime sharing was
`mesh:3, part:550/28369, cluster:3516/181351`; 53/53 Pages and three roots were resident.

With the same 154-instance three-mesh scene constrained to 8 MiB, the normal framing needs only
7/53 Pages (three pinned roots plus four streamed Pages), performs zero eviction and runs at
604.74 FPS. At `distanceScale=0.5` it needs 14/53 Pages, still performs zero eviction, submits
7,458 clusters / 782,910 triangles and runs at 641.30 FPS. Captured frames contain all three
shapes without missing-Page holes. This validates multiple root/address spaces under pressure;
camera-motion churn across multiple unique working sets remains a separate final test.

The moving multi-geometry test is now complete. With 432 instances and three unique meshes,
8 MiB keeps 32/53 Pages, streams 143, evicts 114 (8.89 evictions/s), and produces identical
cluster/triangle counts at every repeated near and far endpoint. The 16 MiB reference keeps
53/53 Pages with zero streaming or eviction. Adjacent fixed-camera pairs have zero pixels
changing by more than four RGB levels in both tiers. The small cross-cycle image delta is also
present in the fully resident tier (time-of-day/background drift), so it is not attributed to
Page residency. The 8 MiB tier is a valid coarse fallback but remains lower quality than the
16 MiB production working set.

## Exact packet arena acceptance (2026-07-31)

Same-player DX12, 1280x720, four cascades, HardwareOnly:

| Instances | Camera Clusters | Camera Triangles | Packet working set | FPS |
|---:|---:|---:|---:|---:|
| 154 | 19,818 | 2,256,734 | 35.1 MiB | 633.40 |
| 432 | 51,786 | 5,857,606 | 40.6 MiB | 537.23 |

The previous double-expanded overflow capacity allocated 504.2 MiB for the 154-instance case. A forced 1M-packet overflow run at 432 instances produced the same complete validation image at 507.06 FPS with a 12.6 MiB working set. No kernel-invalid, missing binding, UAV-limit or Page errors occurred. This closes packet capacity as a correctness cliff: memory is bounded by a 4K one-triangle-per-pixel arena and excess whole Clusters remain renderable without CPU readback.

### Post-packet heterogeneous delivery checks

- Four-SubMesh/eight-material scene, 154 instances: 50,596 camera Clusters, 5.69M triangles, four compatibility bins, 76/76 Pages, 442.65 FPS.
- Three unique baked meshes, 154 instances: GPU Scene sharing is mesh 3 / geometry Clusters 3,516 / virtual Clusters 181,351; 53/53 Pages, 664.70 FPS.
- Both runs used the 8,388,608-packet production arena, four shadow queues and zero CPU readback. Validation captures retained material variation and complete geometry with no kernel/resource/UAV errors.

### 4K scale gate

The final 8M-packet production build rendered 432 instances at 3840x2160 with four cascades at 231.59 FPS (4.318 ms average). The camera cut contained 85,071 Clusters / 9,713,782 triangles, intentionally exceeding packet capacity, so the accepted overflow path was active in a real 4K workload. The captured frame was complete and the Player reported no invalid kernel, missing resource, UAV or Page error.

## Direct-queue acceptance delta (2026-07-31)

The previous large-scene backend still allocated 16-byte virtual Cluster records for every
instance even though spatial traversal already carried instance identity. The 1,000-instance
Toyota workload therefore described 5,219,000 virtual records for only 5,219 immutable geometry
Clusters. The accepted path replaces those records with one-element compatibility placeholders,
uses a unique geometry Page-address table, and drives camera and shadow queues directly.

Final isolated Player results at 1280x720 are 354.24 FPS for 1,000 instances with four cascades,
773.62 FPS for 154 instances across three unique meshes, and 659.53 FPS for 154 four-SubMesh/eight-
material instances. Counts and captures match the pre-compaction path; no capacity, kernel, UAV or
resource errors occurred. These are unattended Player measurements, not Editor Stats or a
Profiler-attached run.

## Hybrid explicit-queue regression closure (2026-07-31)

The r3 close-camera Hybrid regression was an indexed-submission ownership bug. The explicit
Hybrid HW queue reached packet construction with its own valid Cluster/count buffers and a
RenderGraph dependency, but `TryBuildCameraIndexedDraw` still rejected it unless the unrelated
global compact queue's direct/readiness stamps matched the current camera. This made most of the
Hybrid cut disappear while the small software bin remained visible. The fix scopes those stamps
to the implicit global queue; explicit override queues use their own ownership contract.

An isolated clean rebuild and sequential (non-concurrent) DX12 A/B used identical 1280x720
single-car dolly input and a 2 px Hybrid threshold. Both runs completed 180 renders, captured 52
frames and reached 68/68 resident Pages. No invalid kernel, missing binding/resource, UAV-limit,
Page or queue-overflow error was logged. The Hybrid audit settled at 251,760 winner pixels and
1,267 raster tiles with asynchronous software raster restored.

| Check | HardwareOnly | Hybrid 2 px |
|---|---:|---:|
| Fixed-camera max adjacent MAE (RGB levels) | 0.000486 | 0.000565 |
| Fixed-camera adjacent pixels over 8 levels | 0 | 0 |
| Camera renders / captures | 180 / 52 | 180 / 52 |
| Resident Pages | 68 / 68 | 68 / 68 |

Across every matching frame, HW-versus-Hybrid worst MAE was `0.001908/255`; the worst frame had
only two pixels over 8 levels, while the close fixed-camera range had none. This closes the
missing-HW-geometry and temporal-flicker regression. It does not change the prior performance
decision: on the tested RTX 4500 Ada the compatibility software raster remains slower and stays
capability/cost gated, while HardwareOnly is the production default.

## Main-project final performance closure (2026-07-31)

After the project-owned r3 Bake, a clean 6000.3.10f1 DX12 Player at 1280x720 submitted 198,637
camera Clusters / 21.857M triangles for 432 instances. Exact packet construction stayed within one
8,388,608-packet arena; GPU Scene used one immutable mesh, 5,122 geometry Clusters and direct
instance references. All 69 Pages were resident and runtime readback remained zero.

| Mode | Four cascades | FPS | Frame ms |
|---|---:|---:|---:|
| HardwareOnly | no | 277.37 | 3.605 |
| HardwareOnly | yes | 71.35 | 14.016 |
| Hybrid 2 px | yes | 63.23 | 15.815 |

Each cascade currently reaches the correctness-preserving root floor of 162,000 Clusters / 17.607M
triangles. The approximately 10.41 ms shadow delta is therefore the remaining hotspot and explains
why older measurements made with the invalid 95-triangle root asset are not comparable. This is
an explicit performance limitation, not hidden by a quality switch.

Hybrid classification requested 7.86M sparse tile nodes, exceeding the 524,288-node arena. Whole
Clusters were returned to indexed HW as designed; final stress-frame MAE versus HardwareOnly was
`0.000841/255` with 16 pixels over eight levels and no missing geometry. The 11.4% Hybrid slowdown
confirms the production decision to keep HardwareOnly default.

## Post-closure shadow and HZB audit (2026-07-31)

The remaining shadow cost was first attacked by reducing invalid work, not by changing the shadow
LOD target. URP caster planes are now applied throughout the GPU traversal; external-only scenes
fall back to a conservative directional swept-cylinder receiver test. In the reproducible
1920x1080 / 100-instance run this removed 33,400 first-cascade Cluster submissions, preserved the
final image bit-for-bit, and improved wall time by 5.2%. At 4K / 432 instances the accepted queue
remained `3456/3456/1296/432` and the final image stayed complete. This closes the avoidable
off-volume caster cost while preserving four independent cascade cuts.

The HZB recovery architecture now carries instance-qualified rejected records and dispatches only
that queue. It fixes the former cross-instance alias and passes static/moving occlusion-stack image
parity, but it is not admitted to production:

| 4K / 432 case | HZB off | HZB on | Result |
|---|---:|---:|---|
| Occlusion stack | 5.048 ms | 6.085 ms | Same PNG; cost regression |
| Close field | 4.499 ms / 95,749 Clusters | 4.160 ms / 53,845 Clusters | Visible holes; rejected |

The close-field failure remains after Y-origin and reversed-Z fixes, so the faster number is not a
valid optimization result. Shipping remains `HardwareOnly`, `useHzbCulling=false`, four-cascade
shadow caster-volume admission enabled, and zero synchronous readback. The project remains at the
approximately 90% Windows DX12 delivery checkpoint: the unaccepted HZB experiment is not on the
default path and does not weaken the stable image contract.

## Far-field attribute-preservation acceptance (2026-07-31)

The final coarse-LOD material defect was isolated to offline proxy payload generation. Copying a
whole source UV primitive to a differently sized proxy was explicitly rejected even though it
looked better than the earlier constant-UV workaround: hierarchy audit found three UV-stretch
outliers and 4K/432 far-field throughput fell to 169.57 FPS. The accepted position-to-UV Jacobian
footprint is bounded inside the selected source chart primitive, so it preserves local texture
density while preventing atlas-island bleed and decal magnification.

| 4K / 432 / no shadows | Nanite Clusters | Nanite triangles | Nanite FPS | MeshRenderer FPS |
|---:|---:|---:|---:|---:|
| distanceScale 0.20 | 84,875 | 9,184,183 | 219.89 | 122.84 |
| distanceScale 0.35 | 76,960 | 7,713,308 | 231.47 | 85.37 |
| distanceScale 0.50 | 58,350 | 5,368,372 | 261.29 | 72.52 |
| distanceScale 0.70 | 31,101 | 2,535,564 | 314.43 | 71.55 |
| distanceScale 1.00 | 17,225 | 1,240,017 | 352.37 | 72.11 |

All 77 Pages were resident in each run and no HZB, shadow or software-raster path was enabled.
This isolates the comparison to the production HardwareOnly camera path. The hierarchy continues
to a real 12-triangle root, has no five-direction monotonic regression and reports
`uvStretchOutlier=0`. A separate single-car 0.02R-to-64R Dolly completed 52 paired frames; its
worst full-frame Nanite-versus-MeshRenderer MAE was 0.02643 RGB levels. No valid Player log
contained a compile exception, invalid kernel, missing binding/resource, D3D12 removal or Page
error. These results supersede the discarded full-source-UV footprint experiment.

## Proxy material/editor closure (2026-08-03)

The final Proxy usability work adds no Player-side Scene-picking Renderer or per-frame CPU mesh
raycast. Editor material polling is throttled to 10 Hz and only examines materials referenced by
active Proxies. Runtime scalar/color/ST edits upload the compact material table without changing
`GeometryGeneration`; shader/keyword/texture changes rebuild resolve compatibility bins while Page
storage and residency remain intact.

The follow-up source-binding closure stores FBX material references in the baked asset and synchronizes
an existing MeshFilter/MeshRenderer only during enable/validation or an explicit Inspector change.
Scene outlines are submitted only by Editor `OnSceneGUI`; the click fallback yields to every active
Transform handle. There is no new per-frame Player traversal, draw or material polling cost.

The isolated material audit passed both paths. The isolated Editor audit compiled
`Nanite.Editor.dll` with `LogAssemblyErrors (0ms)` and passed non-rendering selection state, CPU
fallback picking, legacy Force-to-enum migration and disable restoration. The previously completed
DX12 Player gate remained 484.96 FPS / 2.062 ms at 1280x720 with 154 instances, 7,298 Clusters,
555,908 triangles, 77/77 resident Pages, and no invalid kernel, missing resource/binding, device
removal or Page error. The last change after that Player gate is Editor-only selection/audit code
plus serialization compatibility and does not alter the GPU draw path.

The updated Windows smoke Player build completed successfully after the runtime source-material field
and binding precedence were added (`Logs/proxy-source-binding-build.log`).

Material scaling remains bounded and explicit: scalar-only variants are one table lookup; each
distinct shader/keyword/texture compatibility bin can add one resolve binding/draw. Adaptive tile
classification avoids executing every bin over the whole screen, but its compute dispatch is enabled
only above the configured bin threshold. The hard scene material-ID limit is 65,535. Large projects
should consolidate texture diversity with arrays/atlases or a bindless project resolve family rather
than assuming unbounded unique textures have zero cost.
