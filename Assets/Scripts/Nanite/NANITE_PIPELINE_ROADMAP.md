# Unity Nanite 管线计划与性能门槛

阶段实施记录、运行证据和参考依据见 [`NANITE_IMPLEMENTATION_NOTES.md`](NANITE_IMPLEMENTATION_NOTES.md)。

目标不是让所有模型都强制走 Nanite，而是在大场景中按成本自动选择普通 SRP Batcher/GPU Instancing 或虚拟几何路径，并让实例数、源三角形数和常驻显存解耦。

## 当前基线与已完成的第一阶段

- 场景提交已经是单次 `DrawProceduralIndirect`，但旧实现仍在每帧逐实例 CPU BVH 遍历并上传候选。
- 旧 Scene VBuffer 按实例复制完整 48-byte 顶点流；相同 `NaniteMesh` 的每个实例都会重新占用一份顶点显存。
- Formal 配置曾同时启用双次 VBuffer 光栅、同步统计回读以及 4 次相机可见集合的级联阴影重绘。
- 现已加入大量实例自动路径：GPU Instance Cull -> GPU Part Cull -> visible-part Cluster expansion -> Cluster fine cull。
- CPU BVH 只保留给小于等于 `cpuBvhMaxInstances` 的小规模高复杂度场景；大量实例不再执行每帧 CPU 候选 `SetData`。
- 相同 `NaniteMesh` 的不可变顶点流只上传一份；实例仅保留 transform、材质、SH 和虚拟 triangle/cluster 元数据。
- 同一帧只扫描/上传一次实例变换；静态实例的 Light Probe 从每 2 帧全量刷新降为可配置的 30 帧，并只在移动时立即更新。
- Formal 双光栅与同步统计回读已在 PC Renderer 基线中关闭。
- 已修复 tile mask 生命周期错误：Resolve 只会读取本视图、本帧实际执行过 classify 的 mask，避免未初始化/上一帧数据造成规则 tile 马赛克。
- PC 基线现在使用 16x16 material tile。Classify 每帧覆盖 mask；GBuffer Resolve 由逐材质全屏三角形改为 tile quad indirect draw，无当前材质的 tile 在顶点阶段退化，不再启动整屏无效片元。
- 已取消“只画 1 个 cascade”的临时性能妥协：它会让 Nanite 阴影距离短于普通 Mesh。PC 正式配置恢复与 URP 一致的 4 cascades；在 shadow-frustum 专用 compact 完成前，性能数据必须明确包含这项真实成本。
- 已确认 Nanite-only 场景此前不会触发 URP 的真实主光 shadow atlas：URP 只检查普通 Renderer 的 `CullingResults.GetShadowCasterBounds`。现已通过嵌入式 URP 的外部 caster registry 修复调度；加入普通 Mesh 不应再改变 Nanite 是否绘制阴影。

## 性能验收规则

每一阶段都必须同时记录普通 Mesh 与 Nanite 两条路径，至少包含 1、10、100、1,000、10,000 个实例档位：

1. CPU：Render Thread、Main Thread、culling、buffer upload；禁止常态 `GetData`/同步 readback。
2. GPU：instance/part/cluster cull、compact、raster、resolve、shadow、HZB 各自耗时。
3. 内存：磁盘 cache、CPU 常驻、GPU 静态几何、GPU 实例/可见性、流式池峰值。
4. 提交：SetPass、draw/dispatch 数必须与材质桶和视图数量相关，不能与对象数量线性相关。
5. 低复杂度准入：Nanite 预计成本高于 SRP Batcher/instancing 时必须自动回退，不能负优化。

目标门槛：静态相机下不产生每帧 GC；1,000 个相同实例只保留一份几何顶点；GPU readback 延迟至少 2~3 帧；任何新功能若让 100 实例档位回退超过 10%，必须先解释并提供关闭/自动策略。

## 当前 4K 性能阶段：先消除固定屏幕成本

用户基线为普通 Mesh 单车约 600 FPS、Nanite 单车约 300 FPS、6 车约 150 FPS。多实例下降不是主要由 GameObject 提交造成，而是当前每个视图都要承担 VBuffer raster、材质重建、阴影和 HZB 等固定/半固定成本；4K 的 `R32G32B32A32` VBuffer 单张约 126.6 MiB，每次完整写入和读取都会产生显著带宽。

本阶段按以下顺序验收：

1. 已实现 tiled indirect material resolve，并修复 tile mask 跨帧/跨视图误读。
2. 用 GPU Profiler 分别记录 `MaterialTileClassify`、`MaterialResolveGBufferMerge`、VBuffer raster、shadow、HZB；比较 tile on/off 的 1/6/100 车数据。
3. 已接入紧凑 VBuffer：支持平台默认使用 `R32G32_UINT` 保存完整 32-bit instance/triangle ID，Resolve 从透视正确 world position 重建 device depth；旧 `R32G32B32A32_SFloat` 由 keyword 保留为自动回退。4K VBuffer 从约 126.6 MiB 降到约 63.3 MiB。
4. classify/resolve 进一步升级为每材质 compact tile list + 每材质 indirect args，避免材质数乘以全屏 tile 数的顶点扫描；材质桶多时再评估 bindless/texture array。
5. 为 shadow view 建独立 cull/compact，停止把同一份相机可见队列无差别重复绘制到 4 个 cascade；正式输出始终保持与 URP 一致的 cascade 覆盖。

阶段门槛：马赛克必须为零；tiled resolve 不得改变 GBuffer、深度或 stencil；单车固定成本明显下降；6 车耗时增长主要跟可见 cluster/覆盖像素相关，而不是实例数或整屏材质 pass 线性增长。

## 当前 CPU/提交阶段：解除伪 GPU-driven 路径

1080p 与 4K 帧率几乎一致、CPU latency 高于 GPU，说明当前不是像素/带宽瓶颈。运行日志进一步确认 1～6 辆车均命中 `hierarchy=CPU-BVH`：旧策略按实例数选择 CPU 路径，导致每帧遍历 BVH、构造 cluster candidate，并通过 `SetData` 上传 part mask/candidate list。

- PC 正式配置已把 `cpuBvhMaxInstances` 设为 0；CPU BVH 仅保留为显式调试回退。
- 正式路径固定为 GPU Instance Cull -> GPU Part Cull -> part-driven Cluster Cull，GPU-resident visible mask 不执行 `GetData`。
- cull 相机常量从每个 kernel 重复上传改为每轮 cull 一次；视锥平面使用预分配数组，不再每轮产生 `Plane[6]` GC。
- GPU mask 生成后，Compact/Raster/Classify/Resolve 共用本帧 ScenePrepare 结果，避免重复 registry/transform/visibility 检查。
- GPU mask 成功后 SecondCull 不再逐实例创建/查询 first、second、merged selection；Proxy Inspector 统计改为低频回写，避免对象数线性侵入正式帧。
- Batched backend 使用 Registry revision 缓存不可变场景签名；成员和渲染数据不变时只检查动态实例数据，不再每个 cull pass 重扫并哈希整个场景。
- HZB 按 virtual cluster 数自适应准入；当前 5,359 clusters 的车辆测试跳过 WriteDepth、Copy/HZB 和 SecondCull，只保留 GPU 视锥/LOD。
- 无 SecondCull 时 Formal 直接复用 FirstCull 已生成的 compact list，不再为同一个 visible mask 重复执行 Compact。
- Material tile classify 按材质数自适应准入；默认少于 3 个材质时跳过独立 classify pass，避免低材质场景支付固定 dispatch/RenderGraph 成本。
- 7 个独立实例 SH Buffer 已合并为一个 `InstanceSH` 结构化 Buffer；每次 Resolve 少 6 次资源绑定，Probe 刷新由 7 次 `SetData` 降为 1 次。
- Profiler 增加 `Nanite.CPU.FirstCull`、`Nanite.CPU.GpuCullDispatch`、`Nanite.CPU.CpuBvhCandidates`、`Nanite.CPU.ScenePrepare`、`Nanite.CPU.ResolveSubmit` 标记。

验收时大工作集 Console 必须显示 `hierarchy=GPU-Instance-PartQueue-ClusterIndirect, readback=0`（低于阈值时为 `GPU-Instance-Part-Cluster`）；Profiler 中 `Nanite.CPU.CpuBvhCandidates` 不应出现。随后按 CPU Timeline 判断剩余瓶颈属于脚本准备、RenderGraph/driver 提交还是 Editor 自身开销。

## 阶段 2：真正的 GPU Scene 与混合准入

- 已完成基础版：静态 vertex/index/triangle metadata 按唯一 `NaniteMesh` 驻留；compact 输出 `VisibleCluster(firstTri, triCount, instance)`，重复实例不再复制三角形索引与元数据，也不再按虚拟三角形预留 compact 列表。
- Part/Cluster culling 元数据已提升为唯一 Mesh 几何表：48-byte `GpuPartData/GpuClusterData` 每个 `NaniteMesh` 只存一份；实例侧使用 16-byte virtual ref 关联 instance、geometry index 与 part/cluster range。运行日志以 `gpuScene=mesh:N,part:geometry/virtual,cluster:geometry/virtual` 验证共享是否生效。
- 当前实例只保留 transform、材质/SH、virtual ref 与 virtual cluster 可见性；已加入 visible-part append queue 与 Cluster `DispatchIndirect`，被 Instance/Part 剔除的范围不再进入 Cluster 阶段。达到 `gpuPartQueueMinVirtualParts` 才启用，较小工作集保留直接 Part-driven 路径。
- 已加入低复杂度混合准入：默认源三角形数 `<= 2048` 且存在原始 Mesh 时交回普通 URP；可用 `forceNaniteRendering` 对单个 Proxy 强制 Nanite。旧资产需重 Bake 或手动指定 `rasterFallbackMesh`。
- 后续将普通小模型从“交回 MeshRenderer”升级为按 mesh/material bucket 的 `RenderMeshIndirect` GPU-instanced fast path，并纳入屏幕覆盖、可见实例数、材质数和最近 GPU timing。
- 已加入 visible-instance append 与 instance->part `DispatchIndirect`：一个 64-lane group 协作展开一个实例的 Part，`CopyCount` 直接写入 dispatch X，不需要 CPU readback、prefix sum 或额外 finalize kernel；达到 `gpuInstanceQueueMinInstances` 后，视锥外实例连 Part 阶段都不再扫描。
- ClusterCull 已直接输出 `(firstTriangle, triangleCount, instance)` draw queue；正式单光栅路径只用一次 1-thread finalize 生成 `DrawProceduralIndirect` 参数，不再全量扫描 scene cluster mask、清 compact counter 或为每个可见 cluster 执行二次原子 append。旧 mask compact 只保留给 dual-raster/兼容回退。
- 2026-07-27 GPU queue 验收：12 个实例、约 11 个可见、4K Editor Game View 曾为 373.1 FPS，Main 2.7 ms、Render Thread 1.7 ms；日志为 `submit=cullQueueIndirect`、`readback=0`、`gpuScene=mesh:1,part:673/8076,cluster:5359/64308`。该数据随后确认没有调度 Nanite shadow atlas，故只验收 GPU Scene/提交结构，完整渲染性能基线作废，必须以修复后的 shadow-on 数据重测。
- 按 material/pipeline key 做 GPU command bucketing，SetPass 与材质桶数量相关。
- 为 Game、Scene、shadow、reflection view 分离 ViewState 和历史 HZB，静态 SceneDB 共享。

## 阶段 3：Page 格式、压缩与流式

当前 `.asset` 使用 YAML 序列化的 `float[12]` 顶点和 `int` 索引，并把所有 LOD page 常驻；这是磁盘和内存膨胀的主因。目标格式：

- 已完成首个兼容切片：外置 `.bytes` Page Binary V1，包含版本、flags、计数、section address、量化 AABB 和 CRC32；Bake 自动 round-trip 验证并记录原始/packed 字节数与最大量化误差。
- V1 已实现 position UNORM16、normal/tangent oct16、UV half/float fallback、16/32-bit local index；层级元数据暂时无损，旧数组暂时共存，真实资产验收后再剥离 YAML payload。
- 真实车辆首次 Bake 的 binary/raw 为 45.4%～58.0%，但旧 Page 达到 0.4～3.4 MiB；现已改为 256 KiB packed-byte 预算、编码后硬拆分，并从新 Page YAML 中剥离重复 vertex/index。下一次 Bake 必须确认 `max<=256 KiB`（单 Part 超限会明确标记）且运行画面无变化。
- `NaniteMesh.pageStreamingInfo` 已形成常驻页清单：packed/section 大小、mip 范围、bounds 和 root 标记；这是固定 GPU pool/page table 的输入，不与 Raster BVH 或未来 RT BLAS 节点布局绑定。
- 已实现固定 256 KiB slot GPU Page Pool、全局 page ID、Page Table、residency/request bitset 和 root page 永久 pin；池大小按场景页数自适应并受显存预算限制。
- Cluster cull 已能对缺页写 request bit，CPU 端仅通过低频 `AsyncGPUReadback` 服务请求；兼容几何暂不因缺页被拒绝，直到 parent/root fallback 完成。
- PC 基线新增 `enablePageStreamingUploads=0`：在 packed Page 尚未直接供 Raster/RT 消费前，root Page/Pool/Table 保持就绪，但禁用 request 原子写、readback、解压和非 root 上传，避免为重复兼容几何付费。直接消费完成后才重新开启。
- 已实现 `NZC1` LZ4 block 存储 wrapper、解压安全上限、NPG1 CRC 复验和 Bake 逐字节 storage round-trip；GPU slot 内保持未压缩 NPG1 ABI。
- Bake 已按 mip 从粗到细稳定装页，使 root working set 集中；Toyota 重 Bake 验收为 59 pages、max=260,956 bytes、oversized=0、rootPages=1，packed/storage 分别为 13.91/10.95 MiB。
- Page Table/residency 和 GPU request mask 已双缓冲；受限池具有 resident touch、LRU retirement 与 slot 隔离基础。显式 GPU fence、请求优先级及 parent/root fallback 仍是开启 packed-page 直接消费前的硬门槛。
- Packed NPG1 已由 Formal Raster、Depth、Resolve 和 Shadow 直接消费并完成正确性验收；59/59 Pages 全驻留、画面/材质/四级 shadow 正常。但 4K Editor 从约 230 FPS 回退到 164.9 FPS，compressed-direct 尚未通过成本门槛，compatibility geometry 暂不删除。
- 已增加 64-byte 双缓冲 Page Decode Table，并把 pixel resolve 合并为一次 triangle context 解析。若复测仍回退超过 10%，改用 GPU 上传时一次性 transcode 到 resident geometry cache；磁盘、chunk、LZ4 与 residency ABI 不变，避免因追求约 11 MiB 空间收益而稳定损失帧率。
- 固定大小 page（先以 128~256 KB 实测），若干 page 组成 128~256 MB chunk。
- 位置按 page AABB 量化到 16 bit；normal/tangent oct 编码；UV half/量化；局部索引使用 8/16 bit。
- 元数据与 payload 分离：root page、hierarchy、bounds/address table 常驻，叶 page 按需驻留。
- page blob 使用 LZ4（吞吐优先）并记录 CRC/version；Unity `ScriptableObject` 只保存 header 和 blob 引用，不再保存巨型 float YAML。
- GPU request bitmask 延迟 readback，后台 IO/解压，固定显存池、页表双缓冲、fence 后驱逐；root pages 永久 pin。
- 参考 Nyx 的模块边界：`HierarchyNodesGPU`、geometry chunks、group/page address table、request mask、async load、pin root、sync address table、eviction 分开实现。

## 阶段 4：Bake 分组与层级质量

- 现有实现已经调用 meshoptimizer 的 cluster partition，不应退回简单顺序分组。
- 增加显式 adjacency graph，权重同时考虑共享边、材质边界、空间距离、法线锥和 page locality；提供 METIS 可选 native backend，meshoptimizer partition 作为无依赖回退。
- group/page 必须控制边界重复率、简化误差单调性、父子覆盖、page 跨边和材质桶数量。
- Bake 输出审计：每级 triangles、重复顶点率、边界锁比例、压缩率、page fill rate、DAG fanout、最大/平均 traversal depth。
- 首版 `HierarchyQualityAudit` 已实现 fanout、singleton、triangles/mip、partition boundary duplication、membership、mip、bounds、误差单调性和 parent containment 合同；致命合同失败会中止 Bake。下一次 Toyota 重 Bake 用审计日志验证分组与 Morton Page locality 修改。

## 阶段 5：软件光栅与 Mesh Shader

- 先按 projected triangle/cluster size 分流：大三角形走硬件光栅，微三角形走 compute/software raster；两路写同一 VBuffer 并使用原子深度/ID 竞争。
- 软件光栅采用 tile/binning、wave ballot、8x8 或 16x16 tile，避免逐三角形全屏包围盒；只处理真正的微多边形。
- DX12 Mesh Shader 路径使用可见 meshlet 列表和 indirect `DispatchMesh`；不支持的平台保留现有 procedural fallback。
- Unity 公共 API 若无法稳定暴露 Mesh Shader/SM6.6，需要把该路径放入 native rendering plugin，并保持相同 SceneDB/page table ABI。

## 阶段 6：阴影、RTX 反射与 RTGI 空间复用

- 当前正确性修复：`ExternalShadowCasterRegistry` 既让不在 URP `CullingResults` 中的 GPU-driven caster 保持主光 atlas 有效，也在 native cascade slice 因零原生 caster 而 invalid 时生成 camera/light fallback CSM matrices；URP `MainLightShadowCasterPass` 在真实 atlas attachment 内直接回调 Nanite draw，并禁止 external-only pass 被 RenderGraph 剔除。旧的独立 `AfterRenderingShadows` pass 已停用。
- 每个主光 cascade 保持独立 shadow-frustum draw queue，不读取相机 HZB，并包含有效的 off-camera caster；URP 现在一次传入全部 cascade slice。低于 32,768 virtual Parts 时，单个 fused kernel 同时填四个队列并一次生成四份 indirect args，日志为 `shadowFrustum/fusedDirect`；大工作集才回退到 instance/part 层级 `DispatchIndirect`。
- 混合场景中每个 cascade 都保留 URP 原生 `RendererList`，随后追加 Nanite draw；`main-light caster ownership` 日志用于区分 native/external 归属。
- SceneDB 的唯一 mesh bounds/geometry range 作为 BLAS cache key；实例 transform、mask、material offset 直接生成 TLAS instance records。
- Raster BVH 与 RT BLAS 不能强行共用节点格式，但应共享 mesh/instance ID、page residency、bounds、material/address table 和 dirty generation。
- RT 请求可以提高相关 geometry page 的驻留优先级；驱逐前等待 raster/RT fence。动态物体独立进入 refit/update 队列。
- RTGI/反射应采用独立预算与降级策略，不能让相机主视图的 root/visible pages 被驱逐。

## 推荐实施顺序

1. 完成 4K tiled resolve 验证、紧凑 VBuffer 和 GPU timing 闭环。
2. GPU Scene 去除剩余的 per-instance part/cluster/BVH 展开，并把普通小模型升级为 GPU-instanced fast path。
3. shadow view 专用 cull/compact 已完成基础实现；以混合/纯 Nanite 的最小与最远 cascade、off-camera caster 和 GPU timing 做运行验收。
4. 固定 GPU page pool/page table/root residency/request mask 已完成基础实现；真实资产 Bake 验收后继续 fence-safe eviction、双缓冲表和真实 chunk IO。
5. page-aware/METIS 分组与 bake 审计。
6. software micro-raster；随后再做 native Mesh Shader 路径。
7. 基于同一 SceneDB/page table 接 BLAS/TLAS、RTX reflection/RTGI。

Nyx 的可复用思想是模块边界和数据流，而不是直接照搬 DX12 MiniEngine 代码。Unity 侧必须保留 URP RenderGraph 生命周期、平台 fallback 和低复杂度对象的自动回退。

## 当前执行位置（2026-07-27）

1. GPU Scene 去重、camera/shadow GPU queue、Page Binary V1、NZC1/LZ4、Page Pool、Page Table 与 root residency 已完成基础实现。
2. 64-byte Decode Table 复测为 195.4 FPS，相对约 230 FPS compatibility 基线仍回退约 15%；Profiler 显示 Main/Render Thread 主要在 WaitForGPU，`compressed-direct` 已判定未通过 10% 成本门槛。
3. 已实现 Page 驻留时批量 GPU transcode：NPG1/NZC1 与 Page Pool ABI 不变，48-byte full vertex 与绝对 `uint` resident index 仅生成一次；生产绘制通过每三角形 direct resident index 起点访问顶点，不再读取 Decode/Resident Page Table。`compressed-direct` 仅保留为默认关闭的诊断变体。
4. 受限池的 resident vertex/index range allocator、失效表先发布和帧末 `GraphicsFence` retirement 已完成；全驻留 12 车路径保持固定地址且不注册额外 fence pass。下一流式硬门槛是 residency-aware parent/root fallback 与 request priority，之后才能解除“全部 Page 必须驻留”的生产准入限制。
5. terminal/root Group 标记、root Page 前缀、root/non-root 硬边界与 Page locality root 审计已补完。运行成本验收后重 Bake Toyota，继续 chunk IO、page-aware adjacency/METIS、micro software raster、Mesh Shader/native plugin，以及复用 SceneDB/Page residency 的 BLAS/TLAS/RTX reflection/RTGI。
6. 当前等待同一 12 车 4K 视角验收 direct resident index 热路径：画面、材质、Depth、四级 shadow、稳定 FPS/Main/Render。目录内仍只有旧的 2000 帧 capture；若要分析截图中 3837 帧长尾，需要另存该 capture 后再做逐 marker 归因。

## 暂停点与下一次决策门槛（2026-07-27）

direct resident index 已完成运行验收，新 capture 为 `ProfilerCaptures/NN_2026-07-27_17-06-11.data`。截图中的代表帧约 CPU 5.5 ms / GPU 1.8 ms，CPU 侧主要显示 `EditorLoop`、`RenderLoop` 和 `GfxDeviceD3D12.WaitForGPU`；这不能用来证明 Nanite Compute 本身是主瓶颈，也不能只凭 Editor Stats 继续猜测。

跨 Page hierarchy ABI 与 Bake 完整性审计已落地，但尚未接入 GPU traversal。当前全驻留正式路径继续扫描 Instance/Part/Cluster 并使用 resident absolute index；新增层级元数据不会进入每帧热路径，所以本阶段没有 FPS 提升是预期结果。

在继续软件光栅、Mesh Shader、RT 或动态 Page fallback 前，先停止扩展功能并完成一次可归因性能审计：Development Player、固定相机/分辨率/阴影，Nanite 与普通 Mesh 各一份 capture，分别记录 CPU Main/Render、GPU VBuffer、Resolve、Shadow 和 cull pass。找到占主导的 pass 后，再决定优化现有热路径、降低 shadow 工作量、建立小模型 indirect fast path，还是继续动态 Page traversal。

现有 2,000 帧 capture 已排除 Nanite CPU 准备/通信为主瓶颈：Render Thread `WaitForGPU` 平均 3.958 ms、Main Thread presentation wait 平均 1.446 ms，而 Nanite 各 CPU marker 均低于 0.1 ms。capture 没有有效 GPU timestamp，故尚不能在 VBuffer、Resolve、Shadow 之间归因；下一步仍需 GPU Profiler Development Player A/B，而不是继续堆 CPU/GPU Scene 基础设施。
