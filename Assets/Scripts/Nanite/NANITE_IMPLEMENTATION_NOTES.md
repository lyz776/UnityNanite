# Unity Nanite 实施记录

## 2026-07-31：多实例 CSM 空 cut / 条带缺失修复

- 根因不是 shadow bias。生产路径使用 spatial Part traversal，但 spatial node 和
  Part admission 仍按 geometric error 提前拒绝；随后 cluster admission 又按 projected
  surface texel density 拒绝过粗 producer，形成“粗层不要、细层进不来”的空 cut。
  100 实例复现中四级 shadow queue 全为 0，接收面完全无影。
- `CSTraverseShadowSpatialNodes` 现在只做保守 bounds traversal；没有 density aggregate
  的 spatial node 禁止 error-only subtree early-out。`CullResolvedShadowPart` 改为通过
  `consumerGroupIndex` 调用与 cluster 完全相同的
  `ShadowHierarchyGroupNeedsRefinement`，避免两个阶段的 refinement 判据分裂。
- Shadow root producer 不再按单个 root group 的 1-texel 阈值独立消失；bounded 和
  persistent hierarchy shadow traversal 也统一使用 surface-density refinement。
- 1080p/100、distanceScale=0.7 的同场景 A/B：修复前 Nanite 四级为 `0/0/0/0`；
  修复后为 `33400/5300/1800/800 clusters`，接收面逐列阴影连续，方向与普通
  MeshRenderer 参考一致。证据：`Logs/shadow-unified-cut-nanite-1080p-100.log`、
  `D:/UnityNanite_CodexVerify/Validation/shadow-unified-cut-nanite-1080p-100` 和
  `D:/UnityNanite_CodexVerify/Validation/shadow-ab-mesh-1080p-100`。
- 4K/432 回归：camera `18,026 clusters / 1,091,578 triangles`；四级 shadow 为
  `3456/3456/1296/432 clusters`、`204336/204336/63504/17712 triangles`；
  `326.70 FPS / 3.061 ms wall`，无 queue overflow、shader 或 RenderGraph 错误。
  证据：`Logs/shadow-unified-cut-4k-432.log` 与
  `D:/UnityNanite_CodexVerify/Validation/shadow-unified-cut-4k-432`。

## 2026-07-31：远景 root floor / far-field proxy 闭环

- 旧 Bake 的远景下限不是 traversal 阈值问题，而是严格 LOD 链把不可继续化简的
  disconnected shell/UV island 标为永久 root；Toyota resident root 为 `36,626 triangles`。
- `TerminalDisappear` 原先用 root 的 `FLT_MAX parentError` 判断，条件永远不成立。
  现在 camera/shadow 都按完整 producer group 的 pixel/texel footprint 原子退出，
  hierarchy emit 禁止逐 cluster terminal cull，避免局部破洞。
- Bake 为高误差或不可约的严格 replacement 生成独立 topology-independent far-field
  replacement；far chain 让 projected-surface density 成为三角预算，近景仍由密度门保真。
- 重 Bake mip triangles：`312269,155900,79488,42449,22513,12566,6793,3863,1583,714,294,130,37`；
  resident root `36,626 -> 37`，DAG fatal counters 为 0。
- 五方向 audit 无单调回退。代表性 density cut：`32R=14,365`、`64R=3,863`、
  `128R=714`、`256R=130`、`512R=37` triangles。
- D3D12 单车实拍：贴脸 `190,290` triangles，随后为
  `149,670 -> 59,563 -> 6,656 -> 1,583 -> 130`，未见旧的近景局部空洞。
- 4K/432/四级阴影：camera `9,832 clusters / 1,141,296 triangles`；每 cascade
  `432 clusters / 15,984 triangles`；`325.28 FPS / 3.07 ms wall`。同场景
  MeshRenderer 为 `21.90 FPS / 45.67 ms`。旧 Nanite 基线为
  `213,511 clusters / 23,940,452 triangles / 55.55 FPS`。
- 证据：`Logs/far-field-proxy128-*.log` 与
  `D:/UnityNanite_CodexVerify/Validation/far-field-proxy128-*`。

### Far-field UV atlas 修复

- 用户 432 实例距离梯度图显示同一 LOD 分界处车身黑色区域变白。原因不是材质实例
  或 Page UV 量化，而是 position-only `simplifySloppy` 把来自不同源三角形/atlas 岛的
  三个角组成新三角形，光栅时会跨不相关 UV 岛插值。
- Bake 现在保留 sloppy 输出角对应的源 index occurrence。若新三角形的三个角来自不同
  源三角形，则选择空间中心和法线最匹配的源三角形，并为整个 primitive 生成稳定的
  单源纹理采样顶点；禁止黑色车身到白色贴花/灯区的跨岛插值。
- UV 稳定版 Bake：mip triangles
  `312269,155900,79346,40265,20109,10454,5593,2548,1081,473,147,41`，
  root `41 triangles`，UV Page 量化误差 0，五方向 monotonic regressions 为 0。
- 4K/432：`1,091,578 triangles / 18,026 clusters / 334.42 FPS / 2.99 ms`；
  四级阴影仍各 `432 clusters`。距离截图与阵列截图未再出现规则白色 LOD 分界。
- 证据：`Logs/far-field-uv-stable-*.log`、
  `D:/UnityNanite_CodexVerify/Validation/far-field-uv-stable-*`。

### 四级 ShadowMap caster cut 修复

- 原 shadow hierarchy 只按 geometric error 选 LOD，并在已经包含 cascade texel density
  的 `ShadowProjectionScale` 之外再次施加 `1/2/4/8` bias；覆盖大量 texel 的车辆因此
  直接使用 terminal proxy，Frame Debugger 中表现为巨大粗三角和 cascade 间断层。
- shadow cut 现在同时使用 projected surface texel density，默认误差预算恢复为 1 texel，
  不再重复施加 cascade bias。单车 1280x720 四级实际选择分别为
  `101/175/110/0 clusters`、`8724/15795/8801/0 triangles`，而不是每级一个 root cluster。
- cascade projection matrix 只描述 receiver slice，不等于 URP directional caster extrusion
  volume。用其六平面提前裁 caster 会造成整块/整级缺失；在 URP 向 external provider
  提供原生 split caster planes 前，GPU hierarchy 使用保守 admission，最终交给 atlas
  viewport/depth clip 精确拒绝，禁止 false negative。
- D3D12 单车 receiver 截图显示连续、可辨识的车辆阴影轮廓，未再出现 terminal proxy
  的巨大块状投影。证据：`Logs/shadow-scale-1.log` 与
  `D:/UnityNanite_CodexVerify/Validation/shadow-scale-1`。

更新时间：2026-07-31

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

### 暂停前 capture 归因

对 `ProfilerCaptures/NN_2026-07-27_17-06-11.data` 的 2,000 帧做 RawFrameData 离线汇总：CPU frame p50 5.620 ms、p95 12.083 ms、平均 6.291 ms。Main Thread 的 `GfxDeviceD3D12.WaitForLastPresentation.WaitForGPU` 平均 1.446 ms；Render Thread 的 `GfxDeviceD3D12.WaitForGPU` 平均 3.958 ms。Nanite CPU 热标记很小：`GpuCullDispatch` inclusive 0.071 ms、`FirstCull` 0.082 ms、`ResolveSubmit` 0.043 ms、`ShadowSubmit` 0.021 ms，`ScenePrepare` 约 0.001 ms。

因此截图里“CPU latency 高”主要是 CPU/Render Thread 等待 GPU 或 Present，不是 GPU Scene 的 CPU 遍历、同步 readback 或 Page 通信。该 capture 的 GPU frame counter 全为 0，未包含可用的逐 pass GPU timestamp；Render Thread 上的 `Nanite/FormalVisibility` 等数字仅是命令提交成本，不能当作 GPU 执行时间。下一次唯一有判别力的验收是开启 GPU Profiler 的 Development Player A/B capture；在此之前不根据 Wait marker 继续修改 CPU 管线。

## 3.14 Indexed Cluster Raster 已验收（2026-07-27）

- 保持 GPU visible-cluster queue 和 VBuffer `(instanceId, triangleId)` ABI 不变；新 compute 把当前视图的可见 cluster 展开为临时 32-bit index buffer，索引编码为 `instance * geometryVertexCount + geometryVertex`。
- Formal VBuffer、WriteDepth 与四级 shadow 均使用带 `GraphicsBuffer indexBuffer` 的 `DrawProceduralIndirect`。VS 从 `SV_VertexID` 恢复实例与唯一几何顶点，PS 从 `SV_PrimitiveID` 恢复原 triangle ID，因此 Resolve、材质映射和 Page ABI 无需迁移。
- 128 MiB 为默认临时 index 预算；阴影四个 cascade 使用互不重叠的四分之一区间，shadow 完成后 camera 复用整块。若任何 view 超出容量，GPU 将 indexed draw 参数置零并仅执行原 procedural fallback，不发生 CPU readback，也不会漏绘几何。
- 同一 12 车、4K、四级主光阴影的实机 A/B：从相近视角约 236.7 FPS（Main 4.2 ms、Render 3.8 ms）提升至 309.2 FPS（Main 3.2 ms、Render 2.1 ms）。日志确认 `submit=indexedClusterIndirect`、`geometrySource=residentCache`、`cascades=4/4`，且无 warning/error。说明此前主要瓶颈确为 procedural vertex amplification，indexed cluster raster 保留为 D3D12/Unity 6 正式路径。
- 尝试跨 Depth 与 Formal pass 复用 camera index slice 反而将性能降至约 215 FPS，已撤销；正式路径在每个消费点就地重建 index/args，避免拉长动态 buffer 的 RenderGraph 资源生命周期。

接下来的唯一性能决策门槛是 Development Player 的 GPU Profiler A/B：固定同一相机、4K、阴影和场景，分别采集 Nanite 与普通 Mesh，记录 GPU VBuffer raster、material resolve、四级 shadow、cull/HZB 以及 CPU Main/Render。取得这些 timestamp 前，不继续扩展 Page traversal、软件光栅、Mesh Shader 或 RT。

为保证该验收可复现，`Nanite/Performance/Build GPU Profiler Development Player` 菜单会基于当前启用的 Build Settings scene 构建 Windows Development Player（自动连接 Profiler、StrictMode），但不修改项目的默认分辨率。使用 `-screen-width 3840 -screen-height 2160 -screen-fullscreen 0` 启动 Player 后，在 Profiler 启用 GPU Usage，再分别采集 Nanite 与普通 Mesh 的固定相机窗口。

### Windows Player 回归修复（2026-07-28）

- 首个 Development Player 暴露 `CSClusterCullVisibleParts` 使用 9 个 UAV、而 Windows Player 仅支持 8 个的兼容问题。该 kernel 现只负责 visible Part 到 direct draw queue 的展开；legacy mask、two-pass 候选和统计输出不再被其资源反射带入。`PartVisible` 与 indirect dispatch args 均拆为 PartCull 的 RW writer 和 ClusterCull 的只读 SRV，保证该路径处于 UAV 上限内。
- Player 中 `Shader.Find("Nanite/VBufferPacketRaster")`、`VBufferLitResolve` 等运行时创建材质所需 shader 曾被 build stripping 移除，导致 Formal VBuffer pass 被跳过。2026-07-28 的 `UnityNanite` Player 日志确认 9-UAV 报错已消失，但仍因 `PacketRaster=MISSING, LitResolve=MISSING` 跳过 Formal pass；根因是 `GraphicsSettings` 尚未真正保存 Nanite shader 引用。五个运行时 Nanite shader 现显式写入 Always Included Shaders，GPU profiler 构建菜单也会在 Build 前自动补齐并保存这些引用，后续 Player 不依赖 Editor 的已导入 shader 状态。
- 项目 Player Settings 的旧 Product Name 为 `NN`，这只影响窗口标题和 Profiler connection 名称，不代表构建来自其他项目；现已统一为 `UnityNanite`。

## 3.15 生产基线重置：真实几何误差、成本准入和异构材质（2026-07-31）

- 运行时 LOD 的硬条件恢复为 NVIDIA/meshoptimizer continuous cluster LOD 的投影几何误差。旧的 projected-area / triangle-density 条件曾覆盖该硬条件，造成近处过快退化、中距离破面、远处又停止退化；该覆盖逻辑已删除。屏幕密度只能用于调度/成本估计，不能删除一个已经选中的原子 producer cut。
- Bake 不再把 meshoptimizer 的 attribute-weighted appearance error 当作对象空间米制误差。UV、法线、切线仍参与候选、seam/topology gate；运行时只投影独立测量的 geometric distance。当前 Toyota 的最大有限误差约为模型半径的 `0.399x`，不再出现数百对象单位的伪误差。
- disconnected component coverage 已成为 Bake 合同：简化丢失组件时注入代表三角形，封闭组件保留小型支撑集；vertex-update 合并 disconnected shell 时恢复原位置/属性。新资产的 DAG membership、mip、error monotonicity、containment、triangle reduction 和 Page round-trip fatal 计数均为零。
- 低提交成本准入来自相同 Player 的正式 A/B，而不是 Inspector 猜测。默认在 `<=12` 个可回退实例、`<=16` 个源 draw、总源三角形 `<=4,000,000` 时交还 URP；24 个以上高复杂度实例进入 Nanite。单车不再承担 VBuffer/Resolve 固定成本，大规模场景仍走 GPU Scene。
- Formal VisibilityBuffer 现为一个生产开关；旧的 `enableFormalVisibilityBufferExperimentalGate` 双开关已删除。可选 Compact VBuffer、HZB、Hybrid 仍由能力探测和实测成本门控，不会因序列化资产残留被强行启用。
- URP/Lit 的参数、纹理和 keyword state 继续通过 GPU material data + compatibility bin 执行。新增 `NaniteMaterialResolveRegistry`：非 URP/Lit shader 只有注册了明确的 resolve family 才能进入 Formal 路径，否则自动留在原生 Renderer。注册、准入、scene revision 失效和注销均有 Editor audit；不再按相似属性名猜测任意 shader 兼容性。
- 参考实现固定到：meshoptimizer `a6ecc73c094bdd5d09644f9286bbf25134853679`、NVIDIA `vk_lod_clusters` `70506fdc7ace33295b76df00e81f3b700043932a`、Nyx `bc7e5b1`。采用的是 producer-group 原子替换、请求在正 traversal decision 后产生、可见 cluster 间接列表、材质/PSO bucketing 和能力/成本门控；没有照搬 Mesh Shader 或 Vulkan/DX12 专用调用。

## 12. 2026-07-31：流式容量与多唯一几何验收

- terminal/root 的 `FLT_MAX` 不再被编码为无限流式优先级，而按投影 footprint 排序；实测最大优先级为有限值 `4,106,921`。
- fly-through 现有明确的近/远端驻留段，自动输出相同视角 cut 范围、streamed/evicted Pages/s 和端点 CSV，不再把首轮加载当作闪烁判断。
- 单资产容量曲线：4 MiB 为 16/75 Pages，556 次加载、542 次驱逐，三次回到近端的 cut 不一致；16 MiB 加载实际需要的 51/75 Pages，0 驱逐，近/远 cut 与 32 MiB 全驻留完全一致。
- 16/32 MiB 三轮结果均为：近端 1,084 clusters / 121,682 triangles，远端 210 / 22,968。生产准入因此要求“工作集可容纳、0 eviction、重复端点 cut 一致”；4 MiB 只保留为故障压力档。
- 新增 3 个不同复杂度 Dragon 的自动 Bake/轮转压力测试。154 实例日志为 `gpuScene=mesh:3,part:550/28369,cluster:3516/181351`，Nanite 为 675 FPS，对照 MeshRenderer 为 424 FPS；53 Pages、3 root 全驻留，无运行错误。
- 自定义材质族可选实现 `INaniteMaterialResolveDataProvider`，把非 URP 属性布局写入 GPU material record，并把自定义绑定状态加入 compatibility hash，避免不同 shader 参数被错误合 bin。`NaniteLitAliasResolveFamily.Register` 已提供 Lit-like 项目 shader 的可交付适配器；注册、数据映射、hash、revision 与释放 audit 已通过。

## 13. 2026-07-31: exact packet arena and production Proxy ownership

- Replaced the fixed 128-triangle reservation per visible Cluster with a 4-byte exact triangle packet arena. A single atomic allocator reserves a contiguous whole-Cluster range; a Cluster that does not fit is appended once to a compact procedural overflow index queue.
- Removed the bounded 64-attempt CAS loop. Under a large queue it admitted only a small, scheduling-dependent prefix and incorrectly sent the rest to fallback. The new allocator performs one atomic reservation and is deterministic with respect to capacity.
- Corrected an O(instance^2) allocation: totalClusters was already expanded across GPU Scene instances but was multiplied by the instance count again. At 154 instances the packet working set fell from 504.2 MiB to 35.1 MiB; at 432 it is 40.6 MiB.
- A forced 1,048,576-packet tier rendered the 432-instance / 5.86M-triangle camera cut through packet plus overflow fallback without holes or invalid resource/UAV errors. It ran at 507 FPS versus 537 FPS with the normal 8,388,608-packet arena.
- Shadow overflow no longer inherits the old admitted-slice Cluster offset. Camera and each shadow cascade reset and reuse the same scratch arena in RenderGraph build-then-raster order.
- RendererFeature registers central GPU Scene ownership at creation. Production Proxies never execute the legacy per-instance culling backend; it remains only for explicit debug-mesh generation.
- This follows UE Nanite's bounded transient visibility work plus fallback principle and NVIDIA's visible-cluster indirect submission model; packet capacity is not exposed as another runtime quality switch.

## 14. Delivery-candidate closure: Hybrid, shadows, streaming and materials (2026-07-31)

The Hybrid visibility defect was not a material-table or VBuffer-ID failure. Hardware deliberately
uses `Cull Off`, while software accepted only one signed screen-space area and pre-swapped corners
from the instance determinant. On closed meshes this selected the opposite shell for part of the
software queue: IDs remained valid, but resolve read back-face normals and attributes. Software
raster now loads immutable source order and normalizes either non-degenerate signed area before
edge testing. This matches the two-sided hardware contract, including negative-scale instances.

Isolated Unity 6000.3.10f1 DX12 Player evidence:

- 52-frame Hybrid 2 px versus HardwareOnly dolly: worst RGB MAE `0.00110/255`; worst pixels over
  8 levels `0.00022%`.
- 432 instances at 1280x720 without shadows: HardwareOnly `589.82 FPS / 1.695 ms`; Hybrid
  `454.01 FPS / 2.203 ms`. Hybrid is correct but slower on RTX 4500 Ada, so HardwareOnly remains
  the production default and Hybrid remains an explicit/cost-gated capability.
- Four independent shadow queues added about `0.287 ms` at 154 instances and `0.262 ms` at 432.
- Three unique Bakes in an 8 MiB pool: 3 pinned roots, 32/53 maximum resident, 205 streamed and
  177 evicted Pages during 720 moving-camera frames; repeated endpoint cuts remained stable.
- External `.npages` in a deliberately undersized 2 MiB pool: 1 pinned root, 8/69 maximum
  resident, 47,703,433 bytes in 258 reads, 243 streamed and 238 evicted Pages. Root fallback kept
  geometry valid; a changing near cut is expected when the working set cannot fit in seven slots.
- Four-SubMesh/eight-material stress used four compatibility bins at `627.23 FPS`. A registered
  shader alias used one explicit family plus four compatibility bins at `712.08 FPS`; unregistered
  programs continue to fail closed to their native Renderer.
- Default 300-frame DX12 smoke negotiated `HardwareFast`, used
  `GPU-InstanceQueue-SpatialNode-PartQueue-ClusterIndirect`, kept `readback=0`, and reported no
  invalid kernel, missing binding, UAV-limit or Page error.

Pinned reference checkpoints: NVIDIA `vk_lod_clusters`
`70506fdc7ace33295b76df00e81f3b700043932a` (`traversal_run_groups.comp.glsl`,
`traversal.glsl`, `render_raster_clusters_sw.comp.glsl`), meshoptimizer
`a6ecc73c094bdd5d09644f9286bbf25134853679`, Nyx
`bc7e5b1e51f6b3b8af4771db81ffaa714fcbe64b`, and NVIDIA meshlet CAD sample
`4f6f7f19f34482a5c4c0424116bdfe3f5baa9db8`. Cluster traversal, raster binning, bounded
transient queues, Page residency and material-family admission remain separate contracts; no
color, LOD-threshold or missing-page patch hides a failed contract.

## 15. Direct geometry queues and unique Page addressing (2026-07-31)

The production large-scene path no longer materializes per-instance Part or Cluster reference
arrays. Spatial traversal emits `(instanceId, geometryPartId)`, camera and shadow leaf kernels
read immutable geometry directly, and every draw record carries its instance explicitly. A
single-instance/small-scene compatibility path retains expanded references for diagnostics.

Page residency now uses one `geometryCluster -> globalPageId` table. Camera direct culling,
hierarchy working-set tests and four-cascade shadow culling no longer recover Page addresses from
an instance-expanded virtual Cluster. The table follows the Scene Page Table generation and is
explicitly bound on all camera, residency and shadow command-buffer paths.

Transient draw queues use the legal terminal-frontier bound of each valid replacement DAG rather
than all historical Clusters at every LOD. If an asset reaches that compact bound, the one-shot
diagnostic schedules a full-geometry-bound rebuild; it does not silently truncate the cut.
Persistent traversal remains quarantined; production follows NVIDIA's bounded multi-dispatch
queue model.

## 16. Hybrid override-queue ownership fix and clean acceptance (2026-07-31)

The close-camera flat silhouette was not a VBuffer encoding, barycentric reconstruction or
material-resolve failure. A temporary GPU audit proved that every readable software winner ID
decoded to the exact expected instance/triangle pair (`29,238/29,238` with normal depth and
`62,635/62,635` under diagnostic `ZTest Always`). The diagnostic kernels, readbacks, forced
float VBuffer defines and debug resolve colors were removed after isolation.

The actual loss happened before hardware raster. Hybrid passes an explicit post-classification
hardware Cluster queue to `TryBuildCameraIndexedDraw`, but that helper also required readiness
stamps owned by the normal global compact queue. Another recorded camera may legitimately change
those stamps before RenderGraph executes, so indexed packet construction returned `false` even
though the explicit queue, count buffer and graph dependency were valid. Override queues now
validate their own buffers/count/dependency; the global compact/direct-queue readiness contract is
applied only when no override queue is supplied.

The current async tile workspace is still shared rather than per-camera. The first production
camera recorded by the feature owns Hybrid for the feature lifetime; secondary cameras use the
complete HardwareOnly path. This internal ownership rule prevents a secondary classifier from
overwriting the primary queue or execution-time view snapshot. It is a correctness boundary, not
an Inspector quality switch; fully per-camera workspaces remain the long-term concurrency design.

Clean Unity 6000.3.10f1 DX12 Player acceptance at 1280x720, one Toyota, threshold 2 px:

- HardwareOnly and Hybrid each completed 180 camera renders and produced 52 captures with all
  `68/68` Pages resident and no invalid-kernel, missing-resource, UAV, Page or overflow error.
- Fixed-camera maximum adjacent RGB MAE was `0.000486/255` for HardwareOnly and `0.000565/255`
  for Hybrid; neither path had any pixel differing by more than 8 levels between adjacent frames.
- Across all 52 matching HW/Hybrid frames, worst RGB MAE was `0.001908/255`; at worst only two
  pixels differed by more than 8 levels. The fixed close-camera pair had zero such pixels.
- The restored asynchronous software pass reported `winnerPixels=251,760`, `rasterTiles=1,267`
  and `settled=True`; captures contain the complete textured car rather than the former HW-missing
  silhouette.

Evidence: `Logs/codex-hybrid-override-clean-build.log`,
`Logs/codex-hybrid-override-clean-hw.log`,
`Logs/codex-hybrid-override-clean-hybrid2.log`, and the corresponding
`Validation/hybrid-override-clean-{hw,hybrid2}` capture directories in the isolated smoke project.

## 17. Main-project delivery candidate acceptance (2026-07-31)

The project-owned Toyota was re-Baked rather than replaced with the isolation asset. The result is
5,122 Clusters, 316 groups, 69 compressed Pages, six root Pages and mip 0–7. All fatal DAG,
containment, Page, component growth/loss and UV-stretch counters are zero; five directional
distance sweeps have zero monotonic regressions. The native plugin lacks `SimplifySloppy`, so the
documented topology-preserving terminal fallback was retained only after those contracts passed.

A clean main-project DX12 Player reproduced the isolated Hybrid fix. HW and Hybrid each completed
180 renders/52 captures with 69/69 Pages resident. Fixed-camera adjacent MAE stayed below
`0.000549/255`; the all-frame HW/Hybrid worst MAE was `0.002776/255` with at most three pixels over
eight levels. No invalid kernel, resource, UAV, Page or device error was logged.

The 432-instance closure keeps exact packet submission admitted. HardwareOnly measures 277.37 FPS
without shadows and 71.35 FPS with four cascades; Hybrid measures 63.23 FPS with four cascades.
The remaining measured cost is the correctness-preserving root floor repeated across cascades,
not CPU submission, readback, Page residency or packet overflow. HardwareOnly remains default and
Hybrid stays cost gated. Full evidence is in
`Validation/delivery-20260731_141158/DELIVERY_REPORT.md`.

## 18. Far-LOD terminal repair and resolve edge coverage (2026-07-31)

The main native plugin was replaced with the validated meshoptimizer build that exports both
`SimplifySloppy` and `SimplifyWithUpdate`. The previously dormant guarded vertex-update path is
now attempted only for mip 3+ groups that would otherwise become permanent roots. Generated
position/normal/UV/tangent payload is appended to the shared geometry arena, then all arrays,
position remaps, quantized remaps and seam flags are refreshed before clusterization. Rejected
candidates roll the arena back, including repaired component-support triangles.

The structural contract is evaluated in the persisted UNORM16 position domain, matching the Page
format and the post-Bake hierarchy audit. This exposed an important distinction: several native
candidates preserved exact-float component count but changed quantized connectivity. Those are
rejected; the accepted delivery Bake reports `componentGrowth=0`,
`terminalComponentGrowth=0`, `componentLoss=0` and `uvStretchOutlier=0`. Resident root triangles
fell from 41,883 to 38,165 without relaxing the fatal gates. Mip triangle counts are
`312269,155314,79166,35341,16445,6382,4651`.

The reported shaded-only holes were not alpha clipping (`New Material` has `_AlphaClip=0`). The
VBuffer resolve instead used a fixed 0.01 barycentric tolerance and discarded pixels where HW
coverage/TAA and fullscreen resolve evaluated nearby sample positions. Resolve now converts the
analytic barycentric derivatives to a bounded 1.5-pixel edge allowance and projects only those
near-edge samples back to the triangle; larger triangle-identity disagreement is still discarded.

Clean main-project acceptance is in `Validation/delivery-20260731_145550`: HardwareOnly and
Hybrid2 each completed 180 DX12 renders and 52 captures; all hierarchy fatal counters and five
directional monotonic regressions are zero. The worst all-frame HW/Hybrid RGB MAE is
`0.00349/255`; no invalid kernel, missing binding/resource, Page or device error was logged.

Isolated Unity 6000.3.10f1 DX12 Player acceptance at 1280x720:

- 1,000 Toyota instances, four cascades: 111,000 camera Clusters / 12.593M triangles, 1,000
  Clusters per cascade, 354.24 FPS. Geometry is 5,219 Clusters while the logical instance product
  is 5,219,000; runtime reports `refs=direct` and a 2,504,000 terminal-frontier capacity.
- Three unique meshes, 154 instances: 1,965 camera Clusters, 773.62 FPS.
- Four SubMeshes/eight material variants, 154 instances: 50,596 camera Clusters / 5.690M
  triangles, 659.53 FPS.
- Single-instance dolly retains `refs=expanded`; captured near/far frames completed without holes,
  invalid kernels, missing resources or queue-capacity fallback.

## 19. Screen-density continuous LOD and 4K closure (2026-07-31)

Production hierarchy traversal previously refined only from projected simplification error even
though the GPU group ABI already contained fine/coarse triangle counts and fine surface area. The
camera cut now refines when either the geometric-error bound is visible or the selected coarse
triangle count is below projected one-sided surface coverage. The latter uses `surfaceArea / 4`,
the Cauchy mean-projection relation for a closed surface, rather than treating unsigned two-sided
triangle area as screen coverage. Both ordinary and persistent GPU hierarchy traversal, the
offline directional audit and the build guard use the same contract. Shadow traversal deliberately
remains error/texel driven.

Terminal vertex-update repair is now available at every mip only after the normal and sloppy
paths would form a permanent root. True producer boundary Locks remain; UV Protect flags may be
released only for that guarded terminal attempt. The final persisted UNORM16 topology, component
coverage and UV-stretch gates remain mandatory. An experiment permitting native-float component
growth was rejected: it raised resident roots from 31,537 to 38,331 and reduced maximum mip from
8 to 7. The retained strict Bake has 10 accepted terminal repairs, 31,537 resident-root triangles,
69 compressed Pages, mip triangles
`312269,157361,80212,36564,17455,9849,5890,2160,1687`, and all fatal DAG counters zero.

At 2160p/60 degrees the retained five-direction audit has zero monotonic regressions and zero
persisted component growth/loss or UV outliers. A representative direction keeps 307,760 triangles
at 0.02R (98.5% of source), 300,479 at 0.5R, then decreases to 223,371/157,964/83,501 at
4R/8R/16R. It continues through 52,402/47,881/45,676 at 64R/128R/256R and reaches the real
31,537-triangle root floor at 512R.

The valid DX12 3840x2160, 432-instance HardwareOnly run (four cascades) submits 195,725 camera
Clusters / 21.813M triangles and measures a final sampled GPU frame of 13.03 ms. With shadows
disabled the same workload is 6.05 ms, proving that repeating the correctness-preserving root cut
in four cascades is the remaining dominant cost. Hybrid is slower on this workload (18.79 ms) and
HardwareOnly remains the delivery default. A half-shadow-texel terminal raster rejection was also
rejected after it removed zero clusters from every cascade. Evidence:
`Logs/codex-density-terminal-strict-final-bake.log`,
`Logs/codex-density-quarter-projection-audit.log`, and
`Validation/density-quarter-4k-432-{hardware,hardware-noshadow,hybrid}`.
## 20. Runtime Part-admission regression correction (2026-07-31)

The prior screen-density closure was incomplete: the offline hierarchy audit exercised the final
group predicate, while production large scenes first passed through legacy error-only spatial-node
and Part early-outs. Those stages discarded density-required fine Parts before cluster selection
could recover them. A deterministic single-car Player exposed the contradiction directly: at
0.02R runtime submitted only 35,528 triangles and at 64R submitted 51,873, despite the offline
audit predicting a dense near cut. This also explains the close-view missing body panels.

Part admission now resolves its immutable `consumerGroupIndex` and calls the same
`HierarchyGroupNeedsRefinement` error-or-density predicate as cluster selection. The hierarchy
buffers/count are explicitly bound to Part and spatial traversal kernels. Spatial nodes retain
frustum rejection but no longer perform an error-only LOD subtree rejection because they do not
carry the surface-area/triangle aggregates required to prove density-imperceptibility. The build
guard rejects either regression.

In an isolated DX12 Player, the repaired 1280x720 single-car dolly submits 251,464 triangles at
0.02R, 217,786 at 0.84R, 156,462 at 2.29R, 70,863 at 6.29R and 51,873 at the far endpoint. The
fixed close frame visually matches the original MeshRenderer reference; detached panels and empty
body regions are gone. Disabling normal-cone culling changed the near cut by only nine Clusters
and was rejected as the cause.

The correction increases real work rather than hiding it: the 4K/432/four-cascade Player now
measures 23.94M camera triangles and 55.55 FPS. Raising geometric error to two pixels produced
little gain. Enabling the current shared-mask HZB was harmful because one instance's rejection
aliases every instance of the same immutable geometry (`11,628` rejected but `89,340` spuriously
recovered). HZB therefore remains disabled until second-pass candidates carry
`(instance, geometryCluster)` identity. UE/NVIDIA performance is not yet matched; the remaining
work is the per-instance HZB queue plus the terminal-root floor repeated in four CSM cascades.

## 21. Conservative shadow admission and HZB release gate (2026-07-31)

Directional shadow traversal now receives the native URP `ShadowSplitData` caster planes for
each cascade. Spatial roots, spatial nodes, Parts and Clusters all use those planes before they
enter a shadow queue, with a two-shadow-texel guard band for bias, PCF reach and quantized bounds.
When a scene contains only external GPU-driven casters and URP cannot supply native planes, the
fallback projects the receiver sphere and candidate sphere onto the plane perpendicular to the
directional-light ray. The resulting conservative swept-cylinder test rejects only casters whose
shadow cannot overlap the receiver volume; it does not guess a finite extrusion distance.

The 1920x1080 / 100-instance DX12 A/B kept the final PNG SHA256 exactly unchanged while changing
the four queues from `33400/5300/1800/800` to `0/5300/1800/800`. Wall time improved from
`1.661 ms` to `1.576 ms` (`601.91` to `634.64 FPS`), and sampled frame time changed from
`1.507 ms` to `1.264 ms`. The 3840x2160 / 432-instance distanceScale=1 gate retained
`3456/3456/1296/432` queues and a complete image. Evidence is in
`Logs/shadow-cylinder-*.log` and `Validation/shadow-cylinder-*` in the isolated verification
project.

HZB Pass2 no longer indexes rejected work only by immutable geometry Cluster. Pass1 appends the
exact `(geometryCluster, instance, sceneCluster)` tuple and Pass2 uses an indirect recovery kernel
over that queue instead of retraversing the scene. Static and moving occlusion-stack captures are
pixel-identical to HZB-off, including real disocclusion recovery. However, the close 4K gate still
reproduces false-positive self-occlusion (`41904` rejected Clusters and visible holes), even after
correcting D3D `Texture2D.Load` Y orientation and making reversed-Z reduction explicit. HZB also
costs more than it saves in the accepted occlusion stack (`6.085 ms` versus `5.048 ms`). Therefore
the production asset and code default remain `useHzbCulling=false`; the repaired queue is retained
as experimental infrastructure but is not part of the stable shipping path.

This is a fail-closed release decision: no LOD threshold, dither, enlarged bound or reduced shadow
quality is used to hide either a visibility error or a cost regression.

## 22. Far-field UV/material preservation closure (2026-07-31)

The bright bands reported on coarse vehicle LODs were a Bake payload error, not a runtime material
lookup error. `meshopt_simplifySloppy` is allowed to create a proxy triangle from occurrences that
belong to different source UV charts. The former repair assigned all three proxy corners one source
triangle-center UV. This stopped cross-atlas interpolation, but an accidentally selected lamp/decal
texel could then cover a complete body proxy triangle. A first replacement that copied the complete
matched source UV triangle removed that single-texel collapse but was also rejected: it produced
three hierarchy UV-stretch outliers and extreme texture gradients, and the 4K/432 far-field run
regressed to 169.57 FPS.

The accepted Bake now builds source position components and continuous position+UV edge charts.
Triangles already contained in one chart retain their exact payload. A cross-chart proxy chooses the
surface supported by the most selected corners, weighted by connected-component area so a small
decal cannot paint the main body. UV, normal and tangent are then barycentrically reprojected onto
the nearest chart. If nearest-point projection collapses at a chart boundary, a local
position-to-UV Jacobian constructs the footprint and scales it about the matched source UV centroid
until every corner lies inside that source triangle. Degenerate Jacobians fail closed to the
selected main-surface centroid instead of extrapolating into another atlas island.

The retained isolated Bake has 5,491 Clusters, 336 groups, 77 compressed Pages and a 12-triangle
root. Mip triangle counts are
`312269,155900,79590,40930,21841,11704,6189,3459,1390,564,209,80,12`.
It reprojected 19,496 cross-chart proxy triangles and used 3,894 bounded collapse fallbacks. Page UV
quantization error is zero. The hierarchy audit reports zero invalid/non-reducing ranges, zero
component growth, zero UV-stretch outliers and zero monotonic regressions in all five directions.

A 1920x1080 single-car DX12 Dolly compared 52 matching Nanite and MeshRenderer frames from 0.02R
through 64R. The worst full-frame RGB MAE was 0.02643 levels, with no enlarged bright decal, UV
band or missing panel in the inspected close, transition and far samples. At 4K/432 instances with
shadows disabled, Nanite measured 219.89/231.47/261.29/314.43/352.37 FPS at distance scales
0.20/0.35/0.50/0.70/1.00; matching MeshRenderer runs measured
122.84/85.37/72.52/71.55/72.11 FPS. The far-field performance regression therefore disappeared
without retaining extra geometry or weakening the image contract.

Evidence: `Logs/far-field-uv-bounded-{bake,audit,build}.log`,
`Validation/far-field-uv-bounded-dolly-1080`,
`Validation/far-field-uv-bounded-dolly-raster-1080`, and the
`Validation/far-field-uv-bounded-{close,mid035,mid050,mid070}-*` 4K A/B captures in the isolated
verification project. The pre-replacement main-project Bake is recoverable from
`Logs/far-field-uv-prebounded-backup-20260731`.
