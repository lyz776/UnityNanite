# Unity Nanite 实施记录

更新时间：2026-07-27

本文记录当前管线已经做过的修改、采用这些设计的原因、运行证据和后续计划。总体目标不是强制所有模型进入 Nanite，而是建立一套成本驱动的 GPU-driven 虚拟几何管线：复杂几何和大场景获得收益，低复杂度物体自动走普通 Mesh/GPU Instancing，不能产生稳定的负优化。

详细性能门槛和阶段路线见 [`NANITE_PIPELINE_ROADMAP.md`](NANITE_PIPELINE_ROADMAP.md)。

## 1. 已确认的原始问题

- 普通 Mesh 单车约 600 FPS，早期 Nanite 单车约 300 FPS，多车继续明显下降。
- 1080p 与 4K 差距一度很小，并且 CPU latency 高于 GPU，说明主要瓶颈不在像素填充，而在 CPU 遍历、资源上传、RenderGraph/driver 提交和固定 pass。
- 旧 batched culling 会按实例在 CPU 遍历 BVH、生成候选并 `SetData`，不是真正的 GPU-driven。
- 相同模型的多个实例曾复制顶点、Part、Cluster 和 triangle metadata，磁盘/CPU/GPU 常驻空间随实例数增长。
- Formal VBuffer、HZB、双阶段遮挡、材质 resolve 和 shadow 存在不必要的固定成本。
- Tile mask 生命周期错误曾造成规则马赛克；该问题已经修复。

## 2. 当前正式管线

```text
GPU Scene（唯一 Mesh 几何 + 轻量实例数据）
    ↓
InstanceCull
    ↓ append visible instances
InstanceQueue → DispatchIndirect PartCull
    ↓ append visible parts
PartQueue → DispatchIndirect ClusterCull
    ↓ append render-ready visible clusters
VisibleDrawQueue(firstTriangle, triangleCount, instance)
    ↓ 1-thread finalize indirect args
DrawProceduralIndirect VBuffer Raster
    ↓
Material Resolve / Depth / Shadow consumers
```

小工作集保留直接 Part/Cluster 路径；兼容路径仍可使用 CPU selection 和旧 mask compact，但 PC 正式配置不主动进入 CPU BVH。

## 3. 已完成内容

### 3.1 正确性和固定屏幕成本

- 修复 tile material mask 跨帧/跨视图读取旧数据导致的马赛克。
- Formal VBuffer 优先使用 `R32G32_UINT` 保存完整 instance/triangle ID；4K 容量约为旧 `RGBA32F` 的一半。
- HZB 根据场景 Cluster 数量自适应，小场景跳过 Depth/HZB/Cull2。
- 无 Cull2 时复用 First compact，不重复执行相同 compact。
- Material tile classify 根据材质数量准入，少材质时跳过固定 dispatch。
- 临时限制相机可见集合重复绘制的 shadow cascade 数量，等待独立 shadow view cull/compact。

### 3.2 解除 CPU BVH 和同步通信

- PC 正式配置 `cpuBvhMaxInstances=0`，固定使用 GPU Instance → Part → Cluster 层级剔除。
- GPU visible mask 路径不执行每帧 `GetData`；统计 readback 默认关闭。
- Registry revision 缓存静态场景签名，静态成员不再每个 cull pass 重扫和重新哈希。
- 相机、frustum 和实例数据使用缓存数组，减少临时分配和重复参数上传。
- GPU mask 成功后不再为每个 Proxy 构造 First/Second/Merged CPU selection。

### 3.3 GPU Scene 去重

- 相同 `NaniteMesh` 的 vertex/index/triangle metadata 只保留一份。
- `GpuPartData/GpuClusterData` 由每实例复制改为每唯一 Mesh 一份。
- 每实例使用 16-byte virtual Part/Cluster ref 关联 instance、geometry index 和 range。
- 12 个相同实例的实测日志：

```text
gpuScene=mesh:1
part:673/8076
cluster:5359/64308
```

即 673/5,359 是唯一几何数据，8,076/64,308 是虚拟实例视图，不再复制 48-byte 静态记录。

### 3.4 GPU 层级工作队列

- PartCull 把命中项写入 visible Part append queue。
- `CopyCount + finalize` 生成 Cluster `DispatchIndirect` 参数，被 Instance/Part 剔除的范围不再进入 Cluster 工作集。
- InstanceCull 把可见实例写入 append queue。
- 一个 64-lane group 协作展开一个实例的 Part；append counter 直接成为 indirect dispatch X，不需要 CPU readback、prefix sum 或额外 finalize kernel。
- 相机视图当前正式阈值：`gpuPartQueueMinVirtualParts=4096`、`gpuInstanceQueueMinInstances=8`。
- 阴影视图拥有独立准入门槛：`shadowPartQueueMinVirtualParts=32768`、`shadowInstanceQueueMinInstances=64`。四个 cascade 会把 append、`CopyCount`、finalize 和 indirect dispatch 的固定成本放大四次，因此 12 实例/8,076 virtual Parts 的当前场景改走 `shadowFrustum/direct`；实现仍保留给更大的工作集。

### 3.5 Culling 实例数据合并

- 原先独立的 matrix、bounds、max scale、LOD error 和 Part range 合并为一个 96-byte `GpuInstanceData`。
- Culling 只需绑定一个只读实例 Buffer，加一个 RW visibility Buffer。
- 动态实例只在 transform/scale/LOD 参数变化时更新合并数据。

### 3.6 Cluster 到 Raster 直连队列（已验收）

- ClusterCull 直接 append `(firstTriangle, triangleCount, instance)`。
- Raster 可以直接读取该队列，不再扫描全部 scene Cluster mask，也不再清理 compact counter 或执行二次可见 Cluster 原子 append。
- 只保留一个 1-thread kernel，把 visible Cluster 数乘以固定 Cluster triangle slots，生成 `DrawProceduralIndirect` 参数。
- 旧 mask compact 保留给 dual-raster 和兼容回退。

### 3.7 Page Binary V1（格式已验收，256 KiB 切分待验收）

- `NaniteMeshPage` 新增独立 `.bytes` payload 引用和轻量统计；旧 YAML 数组暂时保留，旧资产无需迁移即可继续运行。
- V1 header 固定记录 magic、版本、flags、元素计数、page AABB、各 section 的 offset/size 和 blob CRC32；解码前检查长度、计数、section 边界、规范顺序与 header/payload CRC。
- Position 按 page AABB 量化为 3×UNORM16；normal/tangent 使用 octahedral 2×SNORM16；tangent sign 独立保存。
- UV 默认 half2，遇到 half 无法表示的范围自动改为 float2；局部顶点数不超过 65,535 时索引自动使用 16 bit，否则保留 32 bit。
- Cluster、Part、cluster mip、BVH 和 mip roots 首版保持无损，避免把格式迁移和层级正确性修改混在一起。
- Bake 会先编码再完整解码，对索引/层级逐项精确比较，对顶点记录 position/UV/normal/tangent 最大误差；任何越界、CRC 或误差门槛失败都会中止 Bake。
- `EnsureLegacyPayload` 提供二进制到旧数组的兼容解码，但只是迁移 fallback；后续 GPU page pool 会直接消费 packed section，不能依赖它做常态全量解码。
- 独立合成 Page 已通过 encode/decode、量化误差和 CRC 损坏拒绝测试；runtime/editor C# 编译通过，仅保留工程原有 3 条 warning。

2026-07-27 车辆真实资产首次验收：233,886 个源顶点、312,269 个源三角形，DAG 生成 5,359 Cluster、322 Group、673 Part、maxMip 8。原 6 个大 Page 的 binary/raw 比例为 45.4%～58.0%，binary 合计约 15.3 MiB、raw 合计约 27.9 MiB，所有逐页 round-trip 均通过。该测试同时发现旧的 `128 Part/Page` 使单页达到 0.4～3.4 MiB，不能直接作为流式粒度。

据此完成第二个切片：

- Page 规划改为累计唯一顶点、索引、Cluster、Part 和保守 BVH 成本，目标 256 KiB；编码后再次检查真实 blob 字节数，超限就沿 Part 边界递归拆分。
- 单个 Part 是不可拆原子；若单 Part 本身超出 256 KiB 会保留并明确计入 `oversized`，后续 Bake 分组阶段再处理。
- Bake 汇总增加 page min/max、oversized 和 root page 数；`NaniteMesh.pageStreamingInfo` 常驻记录每页大小、section 大小、mip 范围、bounds 和 root 标记。
- 新 Page 的顶点/索引不再重复写入 YAML；`.asset` 保留层级/地址元数据，几何只在 `.bytes` 中。现有渲染首次访问时懒解码，待 GPU Page Pool 接管后可主动释放兼容缓存。
- 覆盖已有 Page/NaniteMesh 资产时保留原 GUID，不再通过删除并重建导致场景引用失效；只清理上一版清单中不再使用的生成页。
- `Build DAG (Full Offline)` 只是独立诊断入口；完整 Bake 已经执行同一 DAG 构建，正常流程无需额外运行。

### 3.8 主光阴影调度与独立视图队列

- Nanite-only 场景不再依赖普通 `MeshRenderer` 才创建主光 shadow atlas：嵌入式 URP 通过 `ExternalShadowCasterRegistry` 把外部 GPU-driven caster 纳入 shadow pass 存活判断。
- 混合场景保留 URP 原生 `RendererList`；每个 cascade 先绘制普通 Renderer，再追加 Nanite indirect draw。`forceRasterRendering` 可显式把单个 Proxy 交回原生 Mesh 路径，`forceNaniteRendering` 优先级更高。
- 无原生 caster 时补建与相机、主光和 URP cascade split 一致的 directional slice，避免纯 Nanite 场景的阴影距离短于混合场景。
- 每个 cascade 已有独立 `InstanceCull -> PartCull -> ClusterCull -> draw queue -> indirect args`。该队列直接使用 shadow view/projection，不复用相机 HZB，能够包含相机外但会投影进当前 shadow frustum 的 caster。
- 大工作集的 shadow cull 已进一步使用 visible-instance 与 visible-part append queue：Instance miss 不再展开 Part，Part miss 不再进入 Cluster；两级计数直接驱动 `DispatchIndirect`。日志分别标记 `shadowFrustum/instancePartIndirect`、`partIndirect` 或小工作集 `direct`。
- URP external shadow registry 现支持一次提供全部 cascade slice。小工作集用单个 `CSShadowCullMultiCascadeByPart` 同时填充四个独立 draw queue，并用一次 kernel 批量生成四份 indirect args；原来每 cascade 的 Instance/Part/Cluster/finalize 提交被合并为一次 fused cull + 一次 finalize。大工作集仍回退到层级队列。
- RenderGraph 在主光 shadow raster 前记录 unsafe compute cull，并用显式 buffer fence 建立依赖；当前 12 车正式日志应显示 `queue=shadowFrustum/fusedDirect`。
- 诊断日志 `main-light caster ownership` 会报告 `nativeRendererList`、`externalNanite` 和 cascade 数。Frame Debugger 中普通 Mesh 是前置 `DrawRendererList`，选中后续 `Draw Procedural Indirect` 不能单独证明普通 Mesh 被漏画。

### 3.9 GPU Page Pool、Page Table 与存储压缩基础

- 固定 256 KiB slot 的 raw GPU Page Pool、全局 `(NaniteMesh, localPage) -> page ID`、结构化 Page Table、residency/request bitset 已接入 GPU Scene。
- root page 永久 pin；池实际分配量按当前场景页数自适应并受 `pagePoolMaxMiB` 硬预算约束，不为小场景预留完整上限。
- Cluster cull 对缺页设置 GPU request bit。当前兼容几何仍继续渲染，避免在 parent/root fallback 完成前产生空洞；request mask 使用 `AsyncGPUReadback` 低频轮询，无同步 `GetData`。
- 磁盘 payload 增加独立 `NZC1` LZ4 block wrapper；GPU slot 内仍是未经压缩的 `NPG1`，因此压缩容器不污染 Page ABI。Bake 会执行压缩后逐字节 round-trip 和 NPG1 CRC 校验。
- Part 在装页前按 mip 从粗到细稳定排序，使最高 mip 集中在少量 root pages，而不是泄漏到几乎每个 page。
- 2026-07-27 Toyota 完整重 Bake 已通过 Page 合同：59 pages，packed 合计 13.91 MiB、最大 260,956 bytes（小于 256 KiB），LZ4 storage 合计 10.95 MiB，root pages=1，root payload=231.8 KiB。
- Page Table、residency mask 与 request mask 均已改为双缓冲；异步回读旧 request mask 时 GPU 写入另一个已清零 mask，避免读写同一资源。
- 受限显存池已具备 LRU retirement 基础：当前 working set 的 page touch 会保护活跃页，非活跃非 root 页先从表中撤销并隔离 4 帧后才允许复用 slot。packed-page raster/RT 真正消费 pool 前会把隔离窗口替换为显式 `GraphicsFence`。
- 修复首帧旧单 Proxy culling 回退未绑定 `_PageResidency/_PageRequests` 的错误；该回退明确绑定零页占位资源，且不参与 streaming。
- 增加 `enablePageStreamingUploads` 成本闸门，PC 基线默认关闭。root Page、Page Pool 和 Page Table 继续创建并常驻，但在 packed Page 尚未直接被 Raster/RT 消费前，不向 Cluster cull 暴露 request/residency buffer，不执行 request 原子写、异步读回、LZ4 解压或重复 Page 上传。

### 3.10 其他常驻和提交优化

- 7 个 Light Probe SH Buffer 合并为一个结构化 Buffer。
- 静态 Probe 不再高频全量上传，移动时立即刷新，静止时按配置低频刷新。
- 日志区分 geometry capacity、scene instance capacity 和实际 GPU queue submission，避免把共享几何容量误认为实际绘制量。
- 低复杂度模型已有普通 Mesh fallback 基础准入；后续会升级为成本模型和 GPU-instanced fast path。

## 4. 当前运行证据

测试场景：4K、12 个 Nanite 车实例，其中约 11 个在视锥内；Editor Game View Stats，仅用于同机同视角方向性对比。

| 阶段 | FPS | Main Thread | Render Thread | 关键日志 |
|---|---:|---:|---:|---|
| Visible Part Queue | 约 308.9 | 3.2 ms | 2.1 ms | `GPU-Instance-PartQueue-ClusterIndirect` |
| Visible Instance Queue + 合并实例 Buffer | 约 337.6 | 3.0 ms | 2.0 ms | `GPU-InstanceQueue-PartQueue-ClusterIndirect` |
| ClusterCull → Raster Direct Queue | 约 373.1 | 2.7 ms | 1.7 ms | `submit=cullQueueIndirect, readback=0` |
| 修复完整 4-cascade shadow 后基线 | 约 274 | 3.6 ms | 2.8 ms | `queue=shadowFrustum/direct`（目标恢复基线） |
| 阴影层级队列 + 未消费 Page 上传误启用 | 220.7 | 4.5 ms | 3.4 ms | `queue=shadowFrustum/instancePartIndirect`、`uploaded=8` |
| 成本闸门验收 | 225.2 | 4.4 ms | 3.2 ms | `shadowFrustum/direct`、无 Page 上传；仅小幅恢复 |
| Packed NPG1 首次运行验收 | 164.9 | 6.1 ms | 4.6 ms | `pages=59/59`、`geometrySource=packedNPG1`；正确性通过但成本未达标 |

最终一项相对 Visible Part Queue 提升约 20.8%，相对上一项提升约 10.5%。验收日志同时显示 `hierarchy=GPU-InstanceQueue-PartQueue-ClusterIndirect`、`gpuScene=mesh:1,part:673/8076,cluster:5359/64308`。这不是最终基准：Editor Stats 不等于 GPU Profiler，后续仍需在 Development Player 中记录 CPU Timeline、GPU pass timing 和显存。

当前仍明显低于普通 Mesh 的用户基线，因此不会把现阶段视为性能目标已经完成。

Packed NPG1 首次运行已经确认画面、材质和四级 shadow 正确，但相对启用前约 230 FPS 回退到 164.9 FPS。当前实现节省的 packed payload 不能抵消顶点/像素阶段重复读取 triangle ref、Page Table、NPG1 header 与量化解码的代价，因此暂不删除约 30.94 MiB 的 compatibility geometry。先通过常驻 Decode Table 消除 header 热读；若复测仍回退超过 10%，正式方案切换为“磁盘 NPG1/LZ4 + GPU 上传时一次转码 + GPU-friendly resident geometry cache”，compressed-direct 只保留为按成本准入的可选路径。

220.7 FPS 的回退不是像素成本：12 实例/8,076 virtual Parts 误过了相机队列阈值，同时 Page streaming 上传了当前光栅尚不读取的兼容几何副本。成本闸门已正确生效，但 225.2 FPS 验收说明它们只占次要部分；主要固定成本仍是四次 cascade cull/command setup。现已进一步合并为四 cascade fused cull，等待运行验收。

## 5. 设计依据

这些修改遵循以下原则，而不是机械复刻 Unreal 的类结构：

1. **GPU Scene 数据与 View 工作分离**：唯一 Mesh 几何长期驻留；每个视图只生成 instance/part/cluster 可见工作队列。
2. **CPU 只提交常量和少量 indirect 命令**：不逐对象遍历、不构造大候选数组、不同步读取 GPU 结果。
3. **层级剔除必须减少下游工作**：Instance miss 不能继续扫描全部 Part，Part miss 不能继续扫描 Cluster。
4. **按成本准入**：append、HZB、tile classify、软件光栅和 streaming 都有固定成本，必须按规模和覆盖率启用。
5. **Raster 与 RT 共享 SceneDB，不强行共享节点格式**：共享 mesh/instance ID、bounds、material、page residency 和 dirty generation；Raster BVH 与 BLAS/TLAS 保持各自适合的布局。

主要参考：

- Unreal Engine：Nanite Virtualized Geometry 文档  
  <https://dev.epicgames.com/documentation/en-us/unreal-engine/nanite-virtualized-geometry-in-unreal-engine>
- Brian Karis 等：*A Deep Dive into Nanite Virtualized Geometry*，Advances in Real-Time Rendering 2021  
  <https://advances.realtimerendering.com/s2021/index.html>
- Nyx 虚拟几何工程：GPU hierarchy、page/chunk、request mask、streaming pool 的模块划分  
  <https://github.com/moonlovelj/Nyx>
- meshoptimizer：meshlet、simplification、cluster partition 和 vertex/index 编码  
  <https://github.com/zeux/meshoptimizer>
- METIS：后续 page-aware adjacency graph partition 的可选后端  
  <https://github.com/KarypisLab/METIS>

## 6. 后续计划

### 近期：完成 GPU Scene 和成本闭环

1. Cluster → Raster direct queue 已完成运行验收，正式路径保持 `cullQueueIndirect`。
2. 用 GPU timestamp/Profiler 分离 Instance、Part、Cluster、Raster、Resolve、Shadow、HZB 成本。
3. 普通小模型升级为 mesh/material bucket 的 `RenderMeshIndirect` fast path。
4. 混合准入纳入 triangle 数、屏幕覆盖、可见实例数、材质桶和最近 GPU timing。
5. 为 shadow cascade、Scene View、reflection view 建独立 ViewState/cull/compact，SceneDB 共享。

### Page 格式、压缩与流式

1. 版本化 Page Binary V1、CRC、量化、局部索引和真实资产 round-trip 已验收。
2. 已按 256 KiB 实际 blob 上限重做 Page 边界，并剥离新资产的旧 YAML 顶点/索引；等待重新 Bake 验证 page 分布、磁盘体积和懒解码画面。
3. 固定 GPU Page Pool、全局 page ID/address table、root page 永久 resident、GPU request bitmask 与 LZ4 storage wrapper 已完成基础接线。
4. Page Table/request 双缓冲已完成；下一步在 packed-page raster 直接消费后重新打开 `enablePageStreamingUploads`，用显式 `GraphicsFence` 完成安全 LRU eviction，并用真实 chunk/异步文件 IO 替换已导入 `TextAsset` 字节源。
5. 完成 residency-aware parent/root fallback 后，移除兼容 decoded geometry 的常驻重复。
6. SceneDB/page table 同时服务 Raster、软件光栅和后续 RT geometry residency。

### Bake 分组与层级质量

1. 显式 adjacency graph，权重包含共享边、材质边界、空间距离、normal cone 和 page locality。
2. meshoptimizer partition 为默认后端，METIS 作为可选高质量 native backend。
3. 审计每级 triangle 数、边界重复率、page fill、压缩率、误差单调性、DAG fanout 和 traversal depth。

### 软件光栅、Mesh Shader 与 RT

1. 按 projected size 分流：大三角形硬件光栅，微三角形进入 tile/binning compute raster。
2. 两路写统一 VBuffer，并用一致的 depth/ID 竞争规则。
3. DX12 Mesh Shader 路径消费相同 visible meshlet/page ABI；Unity API 不足时使用 native rendering plugin。
4. 唯一 Mesh 作为 BLAS cache key，实例 transform/material/mask 生成 TLAS records；与 Raster 共享 residency、bounds 和 dirty generation。

## 7. 验收约束

- 静态相机不产生每帧 GC。
- 正式路径无常态同步 `GetData`。
- 1,000 个相同实例只保留一份静态几何。
- 新阶段若让代表性场景回退超过 10%，必须提供自动门槛或关闭路径。
- 所有优化同时检查正确性：缺面、LOD 裂缝、遮挡误杀、跨帧残影、VBuffer ID、depth/stencil 和材质结果。
- 最终性能以 Player + CPU/GPU Profiler 为准，Game View FPS 只作为快速回归信号。

## 8. 2026-07-27：Packed Page 直接消费检查点

- 新增 `NanitePackedPage.hlsl`，GPU 直接读取 Page Pool 中未压缩的 NPG1/V1 字节；实现非对齐 little-endian load、16/32-bit local index、UNORM16 position、oct16 normal/tangent、half/float UV 和 tangent sign 解码。
- GPU Scene 为每个唯一 geometry triangle 建立临时 `(globalPageId, pageLocalTriangleId)` 引用，保持现有全局 triangle ID、VBuffer ID、材质映射和可见 Cluster 队列不变。
- Formal VBuffer raster、DepthWrite、ShadowCaster、DepthFill 和 GBuffer Resolve 已接入相同 packed Page ABI；旧 float vertex/int index 缓冲仍作为兼容回退。
- `enablePackedPageRaster` 采用严格全驻留准入：完整 Page working set 必须能放入固定池，且所有 Page 都是 NPG1/V1 并上传成功。超预算、旧 Bake、缺 payload 或任一上传失败都会保持兼容路径，不允许半驻留 Page 直接参与光栅。
- Toyota 的 59 Pages 需要 59 个 256 KiB slot，即 14.75 MiB pool，低于当前 128 MiB 预算。预期日志包含 `packed-raster working set resident: pages=59/59` 和 `geometrySource=packedNPG1`。
- 画面、材质与四级 shadow 已通过运行验收；内存日志为 `compat:30.94,pool:14.75,payload:13.91,triRef:4.82 MiB`。性能从约 230 FPS 回退到 164.9 FPS，超过 10% 成本门槛，因此禁止立即删除旧 vertex/index 缓冲。
- 新增与 Page Table 同步双缓冲发布的 64-byte `GpuPageDecodeEntry`：预解析绝对 vertex/index section address、flags、count、record stride、position min/extent。流式上传时建立，淘汰时原子版本发布无效地址；Raster/Resolve 不再反复读取 NPG1 header。
- `NanitePackedTriangleContext` 让 Formal DepthFill/GBuffer Resolve 每个三角形只读取一次 `(pageId, localTriangleId)` 和一次 Decode Table，再解码三个角点；vertex/shadow invocation 也由一次 Decode Table 读取替代 Page Table + header 解析。
- 下一次 12 车 4K 复测是成本决策点：若仍比 compatibility 基线慢超过 10%，实现 GPU resident transcode cache，磁盘与流式仍保持 NPG1/NZC1，不在 CPU 常态展开 float geometry。
- Unity 6000.3.10f1 的 URP Runtime、Assembly-CSharp 与 Editor Roslyn response-file 编译已通过；Shader 导入和真实 GPU 解码仍需 Unity 退出当前 Play 状态并刷新后验收。
- `NanitePackedPage.hlsl` 已额外通过独立 DXC 的 `cs_6_0`、`vs_6_0` 与 `ps_6_0` 编译，覆盖 Raw Page 读取在 compute、vertex 和 pixel 三类阶段的语法与资源模型。Unity/URP 变体仍以 Editor 实际导入结果为最终依据。
- Formal Resolve 一次性日志新增 `geometryMiB=compat/pool/payload/triRef`，用于在删除兼容副本前后做显存成本闭环，避免只看 FPS 而没有确认空间收益。

## 9. 2026-07-27：Bake 层级质量审计

- Bake 新增 fanout min/avg/max、singleton group、每 mip triangle、partition boundary vertex duplication、membership missing/duplicate、group mip mismatch、invalid bounds/error、误差单调性与 parent sphere containment 审计。
- membership、mip、bounds、误差合同等致命 DAG 错误会直接中止 Bake，不再把结构错误留给运行时兜底。
- `BuildPart` 不再跨两个 ClusterGroup 填充；同 mip Part 按中心 Morton 顺序装 Page，降低 page locality 随输入顺序漂移。
- 下一次重 Bake Toyota 时检查 `[Nanite][BakeAudit] Hierarchy/partition quality`：所有 DAG contract 计数必须为 0，并与旧版比较 Page 数、root Pages、fill 与 boundary vertex duplication。

## 10. 2026-07-27：Decode Table 复测与 GPU Resident Transcode

本次 12 车 4K 复测中，64-byte 双缓冲 `GpuPageDecodeEntry` 与每三角形一次 context 解析把帧率从约 164.9 FPS 提升到 195.4 FPS，Main Thread 约 5.1 ms、Render Thread 约 3.8 ms。相对约 230 FPS 的 compatibility 基线仍回退约 15%，没有通过 10% 成本门槛，因此 compressed-direct 不再作为正式默认路径。

保存的 `ProfilerCaptures/NN_2026-07-27_16-00-11.data` 最近 180/2000 帧分析结果：CPU frame 平均 6.559 ms、p50 5.951 ms、p95 13.078 ms；capture 没有 GPU timing stream。Main Thread 的 `GfxDeviceD3D12.WaitForLastPresentation.WaitForGPU` 平均 2.319 ms/frame，Render Thread 的 `GfxDeviceD3D12.WaitForGPU` 平均 4.5485 ms/frame。Nanite CPU 标记均很小：FirstCull 0.0823 ms、GpuCullDispatch 0.0707 ms、ResolveSubmit 0.0435 ms、ShadowSubmit 0.0208 ms、ScenePrepare 0.0008 ms。该版本瓶颈应按 GPU hot path 处理，而不是继续增加 CPU 通信优化。

正式方案已切换为：

- `NPG1/NZC1` 继续作为磁盘、chunk 和 Page Pool 格式，不改变 CRC、LZ4、Page Table、residency/request ABI。
- Page 首次驻留时，用 `NanitePageTranscode.compute` 两个批量 kernel 一次性转码顶点和索引；Page 作为 dispatch Y 维，避免每页一次 CPU dispatch。
- resident geometry 使用 48-byte 绘制记录（position/UV/normal/tangent）和对齐 `uint` index。position 量化、half/float UV、oct normal/tangent 和 16/32-bit index 只在驻留事件解析一次；Formal Depth/Resolve/Shadow 热路径不再执行 oct normalize 或非对齐 NPG1 load。
- 新增 32-byte 双缓冲 Resident Page Table，记录 vertex/index base、count、ready flag 和 generation。转码 dispatch 提交后才发布 ready generation；淘汰时清除 ready，且每个 Page 使用固定 cache range，不会覆盖尚在飞行中的另一 Page 数据。
- compatibility vertex/index 暂时保留。只有 resident cache 在相同 12 车 4K 场景通过画面、四级阴影和不超过 10% 的成本门槛后，才允许删除常态兼容副本。

Bake 安全修正：terminal ClusterGroup 现在由 `maxParentLodError == float.MaxValue` 标成 root；所有 root Group block 在保持 mip/Morton/group 相对顺序的前提下稳定分区到 Page 前缀；Page planner 禁止 root 与 non-root 混页；Page locality audit 新增 `rootPages`。这避免新 Bake 产生 0 个正式 root flag 后依赖运行时“固定最后一页”的兜底。

静态验收：`Nanite`、`Nanite.Editor` Roslyn 编译通过；`CSTranscodeVertices`、`CSTranscodeIndices` 以及调用 packed/resident ABI 的 VS/PS/CS 均通过 DXC SM6 严格编译（`-Ges -WX`）。原有三个无关 C# warning 保持不变，`vertex0/1/2 potentially uninitialized` Shader warning已消除。

下一运行验收只需同一 12 车 4K 视角：确认日志为 `geometrySource=residentCache`，记录 FPS/Main/Render，检查车身、材质、Depth、四级 shadow；同时记录日志中的 `residentCache` MiB。若仍比 compatibility 基线慢超过 10%，下一步把 full triangle corner load 改为屏幕空间导数/重心插值或增加 vertex decode reuse，而不是回到 compressed-direct。

## 11. 2026-07-27：Resident 热路径收口与受限池安全回收

最新截图确认 12 车、4K、`geometrySource=residentCache`、四级阴影与材质正确；同视角瞬时值在 116.8–206.0 FPS 间波动。Profiler Overview 的 3837 帧统计为 median 5.792 ms、max 55.174 ms、CPU over target 11%、GPU over target 1%。该 3837 帧 capture 尚未导出到 `ProfilerCaptures`；目录内仍只有 16:00 的 2000 帧 capture，因此不能用瞬时 Stats 或旧 capture 解释全部长尾。

本轮完成：

- 受限 Page Pool 使用 best-fit resident vertex/index range allocator；容量按最大的 `SlotCount` 个顶点范围和索引范围分别求和，全驻留固定地址布局保持不变。
- Page 淘汰先在双缓冲 Page/Decode/Resident 表中发布 invalid/ready=0，再把 packed slot、vertex range 和 index range一起送入 retirement queue；支持平台在帧末 RenderGraph pass 记录 `GraphicsFence`，fence 通过后才复用地址，不支持平台保留四帧保守回退。
- `compressed-direct` 移入显式 `NANITE_PACKED_DIRECT_DIAGNOSTIC` 本地变体，默认生产变体不声明/读取 Packed Page Pool、Decode Table 或 Resident Page Table；Inspector 诊断开关默认关闭。
- resident index 在一次性 transcode 时写成绝对 resident vertex index；每个唯一三角形记录直接的 resident index 起点。Formal Raster、Depth、Shadow 和 Resolve 的生产路径变为 `triangle -> first index -> absolute vertex`，不再执行 `TrianglePageRef -> Resident Page Table -> page-local index -> vertexBase` 链。
- C# 生产绑定相应移除 Page Table、Decode Table、Packed Pool 和 Resident Page Table，只绑定 direct triangle ref、resident index 与 resident vertex；诊断变体仍保留完整资源。

静态验收：`Nanite` 与 `Nanite.Editor` Roslyn 编译通过；resident/diagnostic VS、PS、CS 以及两个 transcode kernel 均通过 DXC SM6 `-Ges -WX`。独立 harness 的 resident DXIL 从前一版约 6.1/5.5/5.6 KiB（CS/PS/VS）进一步降到约 4.7/4.7/4.7 KiB；诊断变体仍完整保留 NPG1 直解。下一步必须在 Unity 完成重新导入后做同一 12 车 4K 运行验收，确认 direct resident index 没有画面/阴影回归，并比较稳定窗口而非单帧 Stats。

## 3.13 跨 Page 层级 ABI 收尾与暂停点（2026-07-27）

- `ClusterGroup` 现在持久记录一次简化产生的 coarse/parent Cluster；Bake 不再只保留 children。
- meshoptimizer 的空间分区仍决定 locality，但同一次简化产生的 sibling Cluster 被视为不可拆原子，防止它们在下一层被多个 Group 消费，保证 refinement 替换边界无歧义。
- `NaniteMesh` 新增版本化的 `hierarchyGroups`、`hierarchyClusterRefs` 和 `hierarchyRootGroups`。引用同时记录扁平 geometry Cluster、Page、Page-local Cluster 和下一条 refinement edge。
- Bake 在写资产前验证：每个 Cluster 只属于一个消费 Group、每个 coarse Cluster 只由一个 Group 产生、同批 coarse siblings 进入同一上层 Group、root Cluster 位于 root Page、Page-local 顺序和编码后的 Cluster 数一致。违反任一合同都会中止 Bake。
- 当前运行时尚未消费这组新元数据，旧资产和全驻留渲染路径不变。该切片只提供后续缺页 fallback 的正确性前提，不应改变当前 12 车帧率。
- Unity 已重新编译 `Nanite.dll` 与 `Nanite.Editor.dll`，无新增 C# 错误；仍只有项目原有 3 条 warning。

恢复开发时应先做 Development Player 的 CPU/GPU Timeline 与逐 pass GPU timing，确认 VBuffer raster、material resolve、四级 shadow、Editor/driver wait 各自成本。只有证据表明受限显存流式是当前目标，才继续接入 root-to-leaf GPU traversal、resident ancestor fallback 和请求优先级；不要把继续增加基础设施本身当作性能优化。
