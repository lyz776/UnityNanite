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
