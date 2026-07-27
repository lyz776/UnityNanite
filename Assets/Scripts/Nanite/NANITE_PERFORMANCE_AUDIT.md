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
