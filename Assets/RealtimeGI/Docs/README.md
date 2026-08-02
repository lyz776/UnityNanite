# RealtimeGI 系统交接

本文是 `Assets/RealtimeGI` 的唯一设计与实现说明。目标是让接手者快速理解：系统应该是什么、当前做到了什么、还缺什么，以及从哪里继续。

## 1. 系统应该是什么

这不是 NRC、DDGI、PRT，也不是固定范围的 Probe Volume。目标架构是：

```text
Nanite / 普通 Mesh
        ↓
Unified GI Scene（稳定实例、几何、材质 ABI）
        ↓
8 级相机跟随 Sparse Clipmap
  ├─ Static Layer
  └─ Dynamic Overlay
        ↓
Occupancy + Surface + Conservative Distance
        ↓
Directional Surface Radiance Cache
        ↓
Screen Probe Diffuse + GGX Specular Final Gather
        ↓
Temporal / Spatial Reuse + Bilateral Filter
        ↓
Deferred Diffuse Injection

RTX RT Reflection（可选近场高频补偿，不是主路径）
```

它必须满足以下原则：

- 世界空间 Cache 负责离屏信息，屏幕空间只负责 Final Gather、历史复用和可选加速。
- 漫反射与镜面主路径都使用软件追踪：Diffuse 做 cosine sampling，Specular 做截断 GGX importance sampling；RTX RT Reflection 只作为可选高频补偿。
- 相机附近精细、远处逐级变粗；公里级覆盖不等于全地图厘米级精度。
- 静态数据持久化，动态对象写入独立 Overlay，移动后直接清除旧 Brick，不使用 DDGI 式 Probe 恢复。
- 材质、灯光变化只更新 Radiance；几何或 Transform 变化才重新 voxelize。
- 所有更新都有 Brick、射线和灯光预算，目标平台为 RTX 4060，GI 总预算约 3 ms，但当前没有可承诺的实测结果。

当前产品范围是 Nanite 与普通不透明 Mesh。植被、水、半透明、粒子、精确 Skinned Mesh 暂不属于本闭环。

## 2. 当前已经实现

### Unified GI Scene

- `RealtimeGIScene` 收集 Nanite 与普通 Mesh，输出统一实例、几何、材质和绑定 Buffer。
- `GIMeshInstance` / `GIObjectSettings` 控制 Occluder、Contributor、Receiver、Mobility 等语义。
- 材质使用稳定槽位；数值变化只上传 dirty range，不会让旧 Surface Cell 的材质索引失效。
- 静态对象保存旧/新 Bounds，几何、Transform 或绑定变化只失效覆盖到的 Brick。
- `GIMaterialBridge` 可提供 MaterialPropertyBlock、动态颜色、自发光、溶解参数和实时纹理代理。

### Nanite 与普通 Mesh 几何

- 普通 Mesh 上传 position、UV0、index 和 submesh。
- Nanite 优先读取稳定 root page；不可用时回退 `sourceMesh`。
- GI 不持有 Nanite 当前正在修改的瞬态 draw/culling Buffer，避免两个系统生命周期冲突。
- 当前会建立一份 GI 自有紧凑几何流，尚未直接消费 Nanite live resident page pool。

### 8 级 Sparse Clipmap

- 每级逻辑分辨率 `128³`，Brick 为 `8³`，每轴 16 Brick。
- Cell size：`0.2 / 0.4 / 0.8 / 1.6 / 3.2 / 6.4 / 12.8 / 25.6 m`。
- C0 单边覆盖 25.6 m，C7 单边覆盖 3276.8 m；全部层级随相机按 Brick 滚动。
- 默认物理池：8192 Static Brick + 2048 Dynamic Brick。
- Page Table 将逻辑世界 Brick 映射到有限物理池；超预算时优先近层和相机附近。

### Surface、距离与追踪

每个有效 Surface Cell 当前保存：

- 八面体编码几何法线与稳定材质槽；
- half2 UV0；
- 六个 ±X/±Y/±Z RGB9E5 Radiance lobe；
- confidence、age 和 Material Revision。

每个 Brick 执行 7 轮 `3×3×3` Chebyshev 距离传播。射线在空区跳跃，但步长不会越过当前 Brick 边界。这是保守的 unsigned distance，不是全局 SDF。

### 材质与实时 Shader 代理

- 支持 BaseColor、Emission 常量与纹理，以及各自 UV scale/offset。
- 普通 Mesh 和 Nanite root page 都会把 UV0 写入 Surface Cell。
- 不同尺寸纹理被重采样为以稳定材质槽索引的 Texture2DArray，默认每 slice `128×128`。
- `GIMaterialBridge.runtimeBaseMap/runtimeEmissionMap` 可接 RenderTexture；RenderTexture 每帧刷新。
- 任意 Shader Graph 不会被自动翻译。非标准程序化结果必须通过 Bridge 暴露等价的 GI 参数或纹理。

### Surface Radiance Cache

- 注入低频天空、主方向光、Point/Spot、材质自发光和 Cache 多次反弹。
- Point/Spot 必须挂 `GILocalLight`；默认最多 16 灯，每 Surface 最多 2 条局部灯阴影射线。
- 六方向 lobe 保存正半球 Diffuse/Emission，并按 roughness、metallic 和反射方向注入低频镜面项。
- Static Cache 默认执行 3 个跨帧 sweep；不是单帧递归路径追踪。
- 更新严格分成 `Previous SRV → Scratch UAV → Commit UAV`，避免目标 Buffer 同时读写。
- confidence 每次有效更新增长；材质 revision 不匹配时重置历史。
- 世界命中采用中心 Cell + 六轴邻居过滤，只接受材质一致、法线相近、位于同一切平面且方向在正半球的样本。

### 屏幕光照

- 漫反射：低分辨率每像素 1 个 cosine candidate，先做 12 m HZB Screen Trace，screen miss 压入 compact queue 后再查世界 Cache；随后执行 temporal reservoir、时域去噪、几何约束空间滤波和双边上采样。
- `Diffuse History Weight` 只控制最终颜色历史，不控制 candidate 生成，也不衰减 reservoir `M`；默认 `0.9`。`0.98` 的理论时间常数本来就约为 50 帧，只适合静态质量检查。
- Temporal reservoir 的 `M` 固定上限为 10。当前空间阶段不交换 reservoir：现有 payload 没有 secondary hit normal/visibility，邻居路径无法做有效 shift/可见性修正，旧实现会产生红/青分类盐噪。
- 当前修复阶段先恢复 Diffuse 正确性；GGX Specular 的 compute kernels 仍在，但调度链暂时关闭，下一步按“Screen Trace → compact miss queue → Clipmap trace → temporal/spatial reuse”重新接回，不能恢复旧的逐像素固定步进版本。
- Diffuse 在 Nanite 最终 GBuffer 之后、Deferred Lights 之前注入，不再把未参与当前帧 GBuffer 的纹理发布成 `irradianceTexture`。
- `RealtimeGIRendererFeature` 已接入 URP RenderGraph；Clipmap build 仍由 `GIClipmapSystem` immediate CommandBuffer 驱动。

### Deferred 注入顺序（固定约束）

1. `BeforeRenderingGBuffer`：设置 `_RealtimeGIDeferredInjection=1`，只暂停 Lit/ComplexLit 原有 baked diffuse；环境镜面暂时保留，直到 RT Reflection 完成接管。
2. URP GBuffer 与 Nanite Formal Resolve：写最终 GBuffer、depth 和 MaterialLit stencil。
3. Nanite `CopyDepthAfterResolve`：把最终 depth 同步到可供 compute 采样的 R32 `cameraDepthTexture`。
4. `BeforeRenderingDeferredLights`：Trace Diffuse → Temporal → Geometry History → Bilateral Upsample。
5. 依据 GBuffer0/1 重建 `brdfDiffuse × occlusion`，只对 MaterialLit stencil 做 additive 注入。
6. 恢复 `_RealtimeGIDeferredInjection=0`，ForwardOnly 与透明物体继续走各自正常路径。

早于 Nanite Formal Resolve 会读到旧 depth/normal；晚于 Deferred Lights 才计算则无法替换 GBuffer 中已经写入的间接光。注入点不再暴露为可选 Inspector 参数。

### 诊断

- GPU Marker 已覆盖 Clipmap clear、voxelize、distance、radiance、diffuse、specular、temporal、spatial 与 composite。
- 可选 Trace Counter 统计世界射线步数与命中率；原子计数只用于诊断。
- `GIPerformanceMonitor` 显示 GPU marker、Brick 数、Radiance backlog 和池显存估算。

## 3. 当前数据成本

每个 Brick 大约 20.08 KiB：

| 数据 | 字节 |
|---|---:|
| Occupancy | 64 |
| Surface normal/material | 2048 |
| UV0 | 2048 |
| Conservative distance | 2048 |
| 6×RGB9E5 Radiance | 12288 |
| Validity | 2048 |
| Metadata | 16 |

默认 Static/Dynamic 核心池约 200 MiB，共享 Radiance/Validity Scratch 约 112 MiB。材质数组、场景 Buffer 和屏幕 history 不包含在这两个数字中。六方向 Cache 是当前最大的显存和带宽成本。

## 4. 关键源码入口

| 责任 | 文件 |
|---|---|
| 场景收集与 GPU ABI | `Runtime/RealtimeGIScene.cs`, `Runtime/GISceneTypes.cs` |
| 普通 Mesh / Nanite 适配 | `Runtime/GISceneSnapshotBuilder.cs`, `Runtime/GIGeometryStreamCache.cs` |
| 稳定材质与实时代理 | `Runtime/GIMaterialSlotTable.cs`, `Runtime/GIMaterialBridge.cs`, `Runtime/GIMaterialTextureCache.cs` |
| Clipmap 生命周期与调度 | `Runtime/GIClipmapSystem.cs`, `Runtime/GIClipmapLayer.cs` |
| 体素化与距离传播 | `Resources/RealtimeGI/GIClipmapBuild.compute` |
| 灯光与 Radiance 更新 | `Resources/RealtimeGI/GIRadianceCache.compute`, `Runtime/GILocalLight.cs` |
| 通用世界追踪 | `Shaders/GIClipmapCommon.hlsl` |
| Diffuse Final Gather | `Resources/RealtimeGI/GIScreenLighting.compute` |
| URP 调度与合成 | `Runtime/RealtimeGIRendererFeature.cs`, `Shaders/GIComposite.shader` |

不要直接修改 `Assets/Scripts/Nanite` 来修 GI。Nanite 若提供新稳定接口，应由 GI 侧适配后消费。

## 5. 尚未完成，以及应该怎么做

以下按基础性排序，不包含植被、水和透明等已排除内容。

### P0：恢复轻量软件 Specular 主路径

参考 AKG RealtimeGI 的组合方式，但复用本项目 Nanite 已有数据：

1. 半分辨率约 1 spp，以截断 GGX 分布生成镜面方向；截断必须同步修正 PDF/归一化，不能只把随机数硬推向镜面方向。
2. 优先复用 Nanite HZB 做 Screen Trace；屏幕命中读取上一帧 Scene Color mip，mip 由 roughness、锥角和屏幕命中距离决定。
3. 每个 8×8 tile 只把 screen miss 压入 compact queue，再做 indirect Clipmap trace，避免同一 wave 内两种追踪造成 divergence。
4. 保存光线起点、命中点/方向、radiance、PDF 与 reservoir 权重；完成 temporal reservoir、spatial reservoir、Jacobian 与几何相似性拒绝。
5. 最终恢复预积分 BRDF FG 项，再执行轻量 temporal/spatial filter；RT Reflection 不参与这条主路径。

### P1：可选 RTX 高频补偿

Project-3 RT Reflection 只在支持硬件 RT 且用户开启时运行。默认半分辨率、1 bounce；玻璃 dual-depth、多次反射和额外 denoise 都应按材质/Tile 需求派发。它不能成为无 RTX 平台的正确性依赖。

### P0：材质 Shader Family

当前只有 BaseColor/Emission 与常量 roughness/metallic。缺少 Normal、Mask、Alpha Test 和项目自定义风格化节点。

应该：

1. 给 `GIMaterialData` 增加 Shader Family ID 和纹理 slice/通道约定。
2. 在体素化阶段保存 tangent frame，或保存可重建 tangent 的压缩信息。
3. 在 Radiance 更新中按 Family 分支采样 Normal/Mask；不要尝试解释任意 Shader Graph。
4. 每个项目 Shader 必须声明一个便宜、稳定的 GI proxy pass。

### P0：追踪几何稳健性

当前距离只在 Brick 内传播，命中位置是 occupied Cell，薄墙和 Clipmap level 切换仍可能漏光或自相交。

应该：

1. 为薄墙提供保守加厚或专用 GI proxy。
2. 命中后在 Cell 内用三角形/局部平面做位置细化。
3. 增加 front/back face、法线方向和 ray bias 的统一规则。
4. 明确跨 level 的选择与 fallback，避免同一射线在层级边界跳变。
5. 未分配 Brick、Cache 未就绪和超出 C7 时使用稳定 sky/父层 fallback，不能偶发读黑。

### P1：降低方向性 Cache 成本

六 lobe 质量直观，但默认池加 Scratch 已超过 300 MiB，且世界命中过滤需要多次 Buffer load。

应该比较：

- 6 lobe RGB9E5；
- 4 个四面体 lobe；
- 主方向 + 各向同性残差；
- 分层策略：C0–C2 方向性，远层只存低频 RGB。

修改时必须同时更新 `GIClipmapConstants`、Layer 分配、Scratch/Commit、Screen 读取和显存估算，不能只改 Shader 索引。

### P1：Clipmap 调度产品化

当前 Clipmap update 在 RenderGraph 外立即执行，难以正式安排 Async Compute 和资源生命周期。

应该：

1. 将 clear、voxelize、distance、radiance 和 commit 拆成 RenderGraph compute pass。
2. 为 Static 与 Dynamic 建立显式依赖和 UAV barrier。
3. 保留现有 GPU Marker 与独立 Brick budget。
4. Renderer Feature 只消费当帧已完成的 `GIClipmapGpuView`。

### P1：持久 GPU Scene 与 Nanite resident 数据复用

当前 Instance/Binding 仍会按帧上传，GI 还复制一份 root geometry。

应该：

1. 将 Instance/Binding 改为稳定槽 + dirty range。
2. 与 Nanite 约定只读的 resident page export：Buffer、stride、page generation、lifetime fence。
3. GI 只消费稳定 root/coarse pages；Page eviction 前必须等待 GI dispatch 完成。
4. 普通小 Mesh 保留独立紧凑流，不强行 Nanite 化。

### P2：灯光与 Cache 质量

尚未支持 Area Light、Cookie、IES、彩色透射和高阶 BRDF。六 lobe 只能表达低频方向性。

应该先扩展 Local Light ABI，再在固定 shadow-ray budget 下筛选最重要灯；不要让每个 Surface 遍历全部场景灯。更尖锐的镜面应依靠 ray reuse 和命中材质求值，而不是无限增加 Cache lobe。

### P2：内容类型

Skinned adapter 已有实验代码，但不在当前交付范围。植被、草、地形、水、透明特效应分别设计：

- 植被/草：简化静态或低频动态 proxy，避免逐叶体素化。
- 地形：高度场/虚拟纹理专用注入，不复制完整三角网格。
- Skinned：低频动态 proxy 或硬件 RT fallback。
- 水/透明：通常只接收 GI；需要贡献时使用独立 radiance injection，不写成不透明 occupancy。

## 6. 接手顺序

1. 先阅读本文件和上述关键源码，不要从旧 Phase 文档推断状态。
2. 先验收 Deferred Diffuse 注入，再恢复软件 GGX Specular 主路径；RTX 高频补偿后置，然后处理 Shader Family 和追踪稳健性。
3. 再压缩方向性 Cache，之后迁移 RenderGraph 和稳定 GPU Scene。
4. 最后按项目需求逐类加入地形、植被、Skinned、水与透明；不要一次设计一个“万能适配器”。

## 7. 不应破坏的约束

- Static 与 Dynamic 必须是独立 Layer。
- 目标 Radiance 不能在同一 kernel 同时作为 SRV/UAV。
- 几何清除时必须同时清 Surface、UV、Radiance 和 Validity。
- Material slot 必须稳定；修改 GPU struct 必须同步 C# stride 与 HLSL ABI version。
- 世界 Cache 未就绪时必须有显式 fallback。
- 局部灯、反弹、过滤和更新必须有硬预算。
- 不消费或修改 Nanite 内部瞬态 Buffer；允许在稳定的 Formal Resolve 边界增加明确的 lighting integration hook。

## 8. 当前版本验收

- Frame Debugger 顺序必须出现：Nanite Formal Resolve → Nanite CopyDepthAfterResolve → RealtimeGI Screen Lighting → RealtimeGI Inject Deferred Diffuse → Render Deferred Lighting。
- `TraceDiffuse` 的 `_GIDepthTexture` 必须是最终 R32 depth copy，`_GINormalsTexture` 必须是最终 GBuffer2；两者尺寸可与低分辨率 diffuse 不同。
- 首帧或相机切换时 `TemporalDiffuse` 直接输出 current，不允许读取未初始化历史；停机画面不得闪烁或隔帧变黑。
- 禁用 RealtimeGI 后必须恢复 URP 原间接漫反射，而不是变黑。
- 用白色 sky、无有效 radiance cache 测试时，命中未就绪 cache 必须回退 sky；不能把 current diffuse 整屏写黑。
- MaterialLit 接收注入；SimpleLit 暂时保留原 GI，避免错误套用 PBR reflectivity 重建。
- `GI Current Diffuse` 是入射 radiance 的低样本估计，不应乘 receiver albedo；receiver albedo 只允许在最终 BRDF 合成时应用一次。
- 4K 性能以 Development Player + GPU Profiler 验收，不以 Editor Game View/Frame Debugger 数字作为结论；分别记录 Trace、Temporal、Upsample、Injection 的 GPU ms。

## 9. 参考资料

### 核心方案来源

- Müller et al., *Real-time Neural Radiance Caching for Path Tracing*：`C:/Users/yuanzhe.li/Downloads/mueller21realtime.pdf`
- [Neural Radiance Cache：椎名深雪](https://zhuanlan.zhihu.com/p/388120500)
- [AKGI Unreal Engine 4.27，固定提交 2cfcd5e](https://github.com/AKGWSB/UnrealEngine/tree/2cfcd5ee0fd6faeb514e7f33c33bd2af021cb6b9)
- [RealtimeGI 实战篇（上）：稀疏体素与距离场](https://zhuanlan.zhihu.com/p/12632657244)
- [RealtimeGI 实战篇（下）：ReSTIR 时空重采样降噪](https://zhuanlan.zhihu.com/p/12636727339)
- [Surfel-based Radiance Cascade GI](https://zhuanlan.zhihu.com/p/2062594335728784841)

### 论文与实现

- [EA SEED: Global Illumination Based on Surfels](https://www.ea.com/seed/news/siggraph21-global-illumination-surfels)
- [GIBS SIGGRAPH 2021 Slides](https://advances.realtimerendering.com/s2021/SIGGRAPH%20Advances%202021%20-%20Surfel%20GI.pdf)
- [src-dgi](https://github.com/mxcop/src-dgi)
- [Dynamic Diffuse Global Illumination with Ray-Traced Irradiance Fields](https://jcgt.org/published/0008/02/01/paper-lowres.pdf)
- [ReSTIR GI: Path Resampling for Real-Time Path Tracing](https://research.nvidia.com/publication/2021-06_restir-gi-path-resampling-real-time-path-tracing)
- [Kajiya GI overview](https://github.com/EmbarkStudios/kajiya/blob/main/docs/gi-overview.md)
- [NVIDIA HSGI developer summit session](https://www.gdcvault.com/play/1029169/LIGHTSPEED-STUDIOS-Developer-Summit-HSGI)

### 项目内 Nanite 资料

- `Assets/Scripts/Nanite/NANITE_PERFORMANCE_AUDIT.md`
- `Assets/Scripts/Nanite/NANITE_PIPELINE_ROADMAP.md`
- `Assets/Scripts/Nanite/NANITE_IMPLEMENTATION_NOTES.md`
- `Assets/Scripts/Nanite/NaniteSceneVisibilityBufferBackend.cs`
- `Assets/Scripts/Nanite/NaniteGpuPagePool.cs`
- `Assets/Scripts/Nanite/NaniteRendererFeature.cs`

外部实现只用于学习和算法对照。实际移植代码前必须单独核对许可证；论文或其他硬件上的毫秒数不能直接当作本项目结果。

## 10. 2026-08-01 Diffuse Final Gather 审计与改动记录

### 触发问题

- 4K、`0.25` 线性分辨率、`2 rays` 时约 30–40 FPS；
- 噪声长期保持红/青/白离散颗粒，几乎不收敛；
- `Diffuse History Weight=0.98` 时出现固定在屏幕中线的黑横线；
- 结果曾被误称为 ReSTIR GI，但 path payload 和空间复用条件并不完整。

### 审计结论

1. `0.98` 同时被用于最终颜色 blend、temporal reservoir 权重和 trace cadence。稳定像素每帧仅刷新 `1/16`，然后再次混入 `98%` 历史；这不是 ReSTIR 的 `M`，会把响应时间放大到几十帧。
2. 主表面 temporal match 自行用 previous VP 做 clip→UV，而同一 pass 已有 URP motion vector。矩阵路径不包含对象运动，也重复维护 render-target/jitter 坐标约定；高历史权重会把任何不一致固化为屏幕空间接缝。
3. screen hit 在历史无效时最多搜索 `5 levels × 7 cells × 2 layers`，命中后还执行 8-cell cache gather。这个 fallback 位于每条 candidate ray 内，是当前 shader 最明显的乘法级开销。
4. 空间 reservoir 只有 direction/distance/radiance，没有 secondary hit normal、source PDF、visibility token，也不做中心点到邻居 hit 的可见性检查。该操作不是有效的 ReSTIR-GI path shift，分类色噪和漏光属于预期失败模式。
5. Screen Trace 的独立 12 m 上限在 C# 重构时丢失，shader 实际使用 150 m 世界追踪距离；大量 HZB 步进没有收益。
6. 当前 Nanite 已提供最终 depth、GBuffer normal/material 和 motion vector，这是本轮实际复用的稳定边界。日志显示该场景 `hzbConfigured=False`，因此本轮不能安全复用 Nanite HZB，GI 仍构建自己的半分辨率 HZB。

### 已执行改动

- renderer asset 从 `2 rays` 改为 `1 ray`，history 从 `0.98` 改为 `0.9`，pipeline version 升到 3；保留 `0.25` 线性分辨率。
- 删除 history-driven interleaved trace skip：每个有效低分辨率像素每帧产生一个 fresh candidate。
- reservoir temporal reuse 不再乘 history weight，`M` clamp 从 32 改为 10；history weight 只用于最终 denoised color。
- 主表面 temporal match 改用 URP motion vector，并继续用上一帧 depth/normal/material/world position 做 3×3 几何验证。
- screen hit 只有成功重投影上一帧 radiance 才算 screen hit；否则立即进入 compact world miss queue，不再执行多层 cache 搜索。
- 恢复 `Screen Trace Distance=12 m`，世界 miss 仍可追踪到 `Max Trace Distance=150 m`。
- 暂停无可见性保证的 spatial reservoir exchange，替换为 12-tap、5 个低分辨率像素半径的 spiral geometry-aware radiance filter；spatial 结果不反馈 temporal reservoir。
- trace counter 关闭时不再每帧清零 GPU counter buffer。

这些改动的确定性预算变化是：4K、0.25 线性分辨率下 fresh candidate 从 `960×540×2=1,036,800` 降为 `518,400`；同时删除 candidate 内最坏 70 次 occupancy lookup 的 screen fallback。实际 GPU ms 必须按下面流程测量，不能由静态计数代替。

### 尚未冒充“已完成”的部分

当前是“temporal RIS + geometry-aware denoiser”，不是完整 ReSTIR GI。下一阶段必须先扩展 path payload，再恢复空间 reservoir：

1. 保存 source origin、secondary hit position/normal、incident radiance、proposal PDF、target、`W/M` 和 validation age；
2. candidate 生成改为 reference 路径的 uniform hemisphere，并在 resolve/spatial target 中正确引入 Lambert BRDF/PDF；
3. temporal target 使用 luminance，`M<=10`；空间 target 使用 luminance×BRDF；
4. 两次 spiral spatial reuse 分别约 8/5 samples，并至少做短程 depth visibility；未经可见性验证的 spatial reservoir 不反馈 temporal；
5. 每三帧将 candidate frame 替换为 validation frame，对所有存量 sample 重新追踪；disocclusion 仍生成 fresh candidate；
6. 近场使用 raw candidate、远场使用 reservoir，避免空间复用抹掉 Nanite 微细节；
7. full-resolution resolve 读取多个低分辨率 reservoir，而不是只做 2×2 颜色双边采样。

### 验收流程

必须关闭 Frame Debugger，在 Development Player 中测；Editor Game View 只做功能检查。

1. 固定 4K、相同相机和内容，分别采集 GI Off、`0.25/1 ray/0.9`、`0.125/1 ray/0.9` 三组；预热 120 帧，记录随后 300 帧的 median/P95 CPU、GPU frame time。
2. GPU Profiler 分开记录 `Clipmap Total`、`Build Screen HZB`、`Diffuse Screen Trace`、`Diffuse Compact World Misses`、`Diffuse Temporal`、`Diffuse Spatial`、`Upsample`、`Inject Deferred Diffuse`，不能只报总 FPS。
3. 临时开启 Trace Counter 120 帧，记录 rays、screen hit、world hit/miss、average world steps、valid cache。完成后关闭，避免原子与 readback 污染正式数据。
4. 静态相机清空历史后录制 60 帧：第 8–16 帧应明显降低分类盐噪；第 30–60 帧固定 ROI 的亮度方差不得继续维持首 8 帧的水平。若不降，先判 temporal reprojection/reservoir 失败，不增加 ray 数。
5. 分别用 history `0/0.9/0.98` 平移与旋转相机。`0.98` 可以更滞后，但屏幕 `y=0.5` 不得出现与相邻行不连续的固定黑线；disocclusion 必须在当前帧产生 candidate。
6. 固定相机阶跃修改 emissive 或太阳颜色。`0.9` 应在约 10–20 帧进入新稳态；若 cache backlog 尚未归零，必须同时记录 backlog，不能把 cache 更新延迟误判为 screen denoiser 延迟。
7. 质量验收至少包含：纯白材质+彩色 emissive、红/青相邻材质、薄几何、屏幕外 emissive、Nanite cluster 边界、近距离微细节和快速相机切换。Cluster/instance ID 不得形成照度边界。

## 11. 2026-08-01 第二阶段：Path Reservoir 闭环

### 11.1 数据契约

Diffuse reservoir 现在显式保存：

- 次级顶点的 incident radiance；
- primary-to-secondary direction 与 distance；
- secondary hit normal 与 sky/hit 标记；
- source PDF、selected target、weight sum、`M` 和 age。

Primary origin 不额外存储，由当前或历史 VBuffer/GBuffer 的 depth、normal 和 motion vector 重建；有限路径的 secondary position 由 `origin + direction * distance` 重建。这样只新增一张低分辨率当前纹理和双缓冲历史纹理，不复制 Nanite cluster 数据。

### 11.2 RIS 数学

新候选采用 cosine-weighted hemisphere：`q=max(dot(N,Wi),0)/π`。这仍保持下面的 RIS
估计无偏，但对 Lambertian final gather 去除了 uniform hemisphere 中可避免的 cosine 方差。
对 diffuse irradiance：

`f(x)=Li(x)*max(dot(N,Wi),0)`

`target(x)=luminance(f(x))`

`w(x)=target(x)/q(x)`

最终向量估计为：

`L = f(y) * weightSum / (M * target(y))`

Temporal/Spatial 合并历史 reservoir 时使用 `weightSum_prev * target_new / target_old * J`，其中有限路径的 reconnection Jacobian `J` 由 secondary normal 和新旧距离的 solid-angle measure 计算，sky 路径取 1；候选数量使用被合并 reservoir 的 `M`。当前 temporal reservoir 固定 `M<=20`，一次 4-neighbour spatial merge 最多保留 `20×(4+1)=100` 个候选的等效样本数。`Diffuse History Weight` 不进入 reservoir 数学。

### 11.3 Pass 顺序

1. HZB screen pre-trace；
2. compact world misses；
3. temporal path reconnect；
4. 4-tap rotating spatial reservoir reuse；
5. 对最终选中的有限路径做 3-tap screen-depth visibility；失败时回退中心 temporal reservoir；
6. spatial 之后执行独立 temporal denoiser；
7. 对时序结果执行 9-tap geometry-aware filter；
8. full-resolution resolve 从 2x2 低分辨率 footprint 选择几何兼容结果；
9. deferred diffuse injection。

Spatial 结果不写回 persistent temporal reservoir，避免未经完整 world visibility 验证的路径污染后续帧。当前只启用一轮 spatial reuse；第二轮 5-tap reuse 必须在第一轮 GPU 时间和 visibility reject 率通过验收后再决定，避免为了形式上贴近参考实现而无条件增加 4K 成本。

### 11.4 可观测性

开启 `Enable Trace Counters` 后，日志新增：

- `temporalMatch`：找到几何匹配的历史 reservoir；
- `temporalReuse`：重连后 target 有效的历史路径；
- `spatialReuse`：通过 primary/secondary 几何门控的空间候选；
- `visibilityReject`：最终选中但被 screen-depth visibility 拒绝的空间路径。

建议首先测试 `Spatial Samples=0/4/8`。若 `visibilityReject/spatialReuse` 很高，应先修 visibility 或缩小 radius；不要靠提高 history weight 隐藏错误。若空间 pass 超过预算，4 taps 是首选降级，不能削减 temporal 新候选。

性能门槛分两级：先要求本轮配置相对旧 `0.25/2 rays/0.98` 的 `Diffuse Screen Trace + World Misses` GPU 时间显著下降且无中线；再以 RTX 4060 4K Development Player 的 RealtimeGI 总 GPU 时间 `<=5 ms` 为可用门槛，`~3 ms` 为后续优化目标。未取得 GPU capture 前不得在文档中写“已达到”。

## 12. 2026-08-02 黑带、收敛与 world-miss 定点修复

### 12.1 本轮证据，不再用“命中率正常”代替画质验收

用户捕获中的 `rays≈435922`、`temporalMatch≈435800`、`temporalReuse≈430000` 说明主表面历史匹配率和 reservoir 重用率已经很高。因此固定在屏幕中部的暗带不能继续归因于“历史没匹配上”。同一捕获还显示：

- `screenGeometryHit≈382000`，但 `screenHit≈9000`；
- 约 249k world hit 与 177k world miss 平均执行约 57.5 步；
- GPU Profiler 中 RealtimeGI Screen Lighting 约 1.849 ms，其中 `Diffuse Compact World Misses≈0.685 ms` 是最大单项；Spatial≈0.250 ms、Temporal≈0.172 ms、Upsample≈0.315 ms。

这证明上一轮确实把 GI GPU 时间降到了约 1.85 ms，而不是仍处于最初的 30 FPS 成本；但质量仍未通过，且 screen hit 的辐射获取失败造成了可以继续消除的 world-trace 开销。Profiler 同时开启了 counter atomic、AsyncGPUReadback、Console 日志和 Editor，不能作为最终 Player 验收数据。

### 12.2 确认的收敛错误

旧 `GIClampHistory` 每帧把历史 RGB 严格 clamp 到当前单帧 3x3 的 min/max。对单 ray Monte-Carlo 输入，这等价于让高历史权重不断服从当前噪声包围盒；后续的 luminance response 又会把 0.98 降至最低约 0.343。结果是 Inspector 显示 0.98，实际却没有形成 50 帧量级的稳定积累，局部零/暗样本还会固化成横向暗带。

现改为：

- 在 `color/(1+luminance)` 的压缩辐射空间统计同材质、同法线 3x3 均值和方差；
- 用 `mean ± max(2.5σ, floor)` 裁剪历史，不再 clamp 到单帧 RGB 极值；
- 只有超过 2 stops 的阶跃变化才逐渐降低 history weight，普通采样噪声保持用户配置的权重；
- 时序之后增加三轮 9-tap、step=`1/2/4` 的 material/normal/world-plane 约束 A-Trous filter；它只作用于最终显示，不反馈 persistent reservoir。

新 candidate 改为 cosine-weighted hemisphere，并使用对应 `pdf=cos/π`。RIS 的 `target/pdf` 与最终 `W/(M*target)` 公式保持一致；这一项降低方差，不是通过增加 ray 数掩盖问题。

### 12.3 确认的追踪浪费

screen trace 已经得到精确的 hit position/normal，但旧路径在上一帧 radiance 重投影失败后直接进入 world-miss queue。重复世界追踪平均约 57.5 步。现在先按 hit position 查询每级 clipmap 的 containing cell，并读取其连续 8-cell radiance reconstruction；只有该 cell 没有有效 cache 时才进行 world trace。screen path 记录的距离也改为重建 hit position 到 origin 的实际距离，而不是 HZB crossing 参数，避免 temporal/spatial reconnection 使用错误次级顶点。

空间邻居默认由 8 降为 4、半径由 6 降为 4。新增 filter 会承担显示端降噪，避免同时为 reservoir exchange 和颜色平滑支付 8 邻居成本。正式性能测试默认关闭 Trace Counters。

### 12.4 一次定位模式

Renderer Feature 新增 `Diffuse Debug Stage`：

- `Final`：生产输出；
- `SpatialRaw`：空间 reservoir 后、时序颜色积累前；
- `TemporalAccumulated`：方差裁剪和时序积累后、最终 filter 前；
- `ReprojectionValidity`：绿色为有效历史匹配，红色为拒绝；
- `MotionVectors`：`motion*32+0.5` 可视化。

如果黑带仍存在，只需在同一静态镜头依次截取这五档：首次出现于 `SpatialRaw` 表示 tracing/reservoir 问题；只在 `TemporalAccumulated` 出现表示 reprojection/clipping 问题；只在 `Final` 出现表示 filter/upsample 问题。禁止再通过反复改 sky、emissive 或 history weight 猜来源。

### 12.5 本轮验收

1. 退出并重新进入 Play，确保 pipeline version 5 清空旧 reservoir/history；保持 `Final`。
2. 静止相机 60 帧，分别截取第 1、8、30、60 帧的同一 ROI；30→60 帧亮度方差应继续下降，不能保持盐噪。
3. 将 History Weight 临时设为 0.98，平移相机；屏幕 `y=0.5` 不得出现固定暗带。若出现，按 12.4 一次采齐五档，不再零散试值。
4. 性能测试关闭 Frame Debugger、Trace Counters 和 Console Collapse 日志，在 Development Player 预热 120 帧后记录 300 帧。单列 `Screen Trace / Compact World Misses / Temporal / Spatial / Geometry Filter / Upsample`。
5. 临时开启 counter 只采 120 帧：预期 `screenHit/screenGeometryHit` 显著高于本轮前约 2.4%，且 world-hit+miss 数显著低于约 426k；否则 containing-cell 快速路径没有生效，需要检查 clipmap coverage，而不是增加 trace steps。

## 13. 2026-08-02 TOD 环境、软件镜面与中线根因修复

### 13.1 中线的确定根因

本项目构造 view-projection 时使用 `GL.GetGPUProjectionMatrix(..., true)`。在 D3D render texture 上，Y flip 已烘入 GPU projection；HLSL 从 clip space 转回纹理 UV 时必须遵循 Unity 的 `UNITY_UV_STARTS_AT_TOP` 约定。

旧代码在 screen trace、screen-hit history、短程 visibility 和 specular virtual-hit reprojection 中直接使用：

`uv = clip.xy / clip.w * 0.5 + 0.5`

该表达式只在屏幕水平中线 `y=0.5` 与正确结果重合。因此中线能读取正确历史，呈现为更浓、更稳定、噪声更小；上下区域则查询镜像/错误位置并反复拒绝历史。现在所有 world→screen 映射统一使用 `ComputeNormalizedDeviceCoordinates(positionWS, gpuVP)`。这项修复同时覆盖：

- HZB screen trace 和二分 refinement；
- screen-hit radiance 的上一帧重投影；
- spatial reservoir 选中路径的短程 depth visibility；
- specular virtual hit temporal reprojection。

### 13.2 Reservoir 收敛修正

旧实现虽然合并了多个 neighbour reservoir，但 temporal 和 spatial 末尾都再次执行 `GIReservoirLimit(..., 10)`。空间复用产生的等效样本几乎全部被立即丢弃，是 emissive 高方差长期不降的直接原因之一。

当前契约为：

- temporal reservoir：`M<=20`；
- spatial candidate：每个 neighbour 最多携带 20 个 temporal candidates；
- spatial 输出：最多 `20×(spatialSamples+1)`，默认 4 neighbours 即 `M<=100`；
- spatial 结果仍不反馈 persistent temporal reservoir，避免可见性近似跨帧扩散。

显示端采用三轮 step=`1/2/4` 的 9-tap A-Trous；full-resolution resolve 改为对称 3×3 几何兼容 footprint，删除旧 2×2 相位差。新 filter 增加固定成本，是否保留第三轮必须由 Development Player 的 GPU capture 决定；若超预算，首先降为 step=`1/2`，不能再次削减 temporal fresh candidate。

### 13.3 TOD 天光唯一来源

新增 `Assets/RealtimeGI/Shaders/GITODSky.hlsl`，radiance cache、world miss、URP glossy fallback 共用同一套解析环境。输入直接来自 `TODController` 写入的 `_TODLightBottom/Middle/Top`、horizon/ground、sun scatter/wash、moon/halo、exposure/tint/saturation/contrast globals。

- 不再读取 `RenderSettings.ambientSkyColor` 或 `_GISkyColor`；
- TOD color grade 与 `TODDynamicSky` 使用相同的 saturation → contrast(0.5 pivot) → tint/exposure 顺序；
- diffuse cache 使用方向性 TOD hemisphere 近似；
- glossy miss 使用 roughness-dependent TOD broadening；
- URP `GlossyEnvironmentReflection` 在 RealtimeGI 启用时不采 reflection probe/Unity sky cubemap，traced specular 由独立 additive pass 注入。

解析 TOD fallback 不包含动态云纹理的高频细节；这是有意的稳定性/成本折中。云影仍由直接光路径处理，后续若需要云层进入间接环境，应提供低频 TOD environment LUT，而不是在每条 ray 中运行完整天空 shader。

### 13.4 软件 GGX 镜面路径

新增可选 `Software specular indirect`：

1. 默认 half-resolution，每像素一条 truncated-GGX importance sample；
2. HZB screen trace 优先，miss 再走现有 sparse clipmap distance trace；
3. full-resolution Ray Resolve 从邻近四条低分辨率 ray 重连，并用 GGX target weight 选择/归一；
4. compact split-sum FG 作为 ratio-estimator 分离项乘回；
5. smooth surface 比较 virtual-hit reprojection，rough surface 使用 primary surface reprojection；
6. tone-mapped、roughness-dependent spatial filter 输出到独立 specular texture；
7. deferred composite 将 diffuse irradiance×receiver BRDF 与 specular lighting 相加。

当前 screen-hit radiance 使用上一帧 diffuse outgoing radiance/clipmap cache，而不是 AKGI 原实现的 previous scene-color mip chain。这保留了软件路径和 TOD 一致性，也避免新增一套 4K mip history，但会少一部分直接光高频。若后续画质确需补足，应新增“deferred lighting 后拷贝 scene color → 下一帧 mip history”的独立 late pass；不要把 feature 整体移动到 GBuffer 尚未完成的位置。

### 13.5 本轮验收

1. 重新进入 Play 让 history 清空；固定相机等待 60 帧。`Final` 中屏幕 `y=0.5` 不得比相邻区域更浓或更干净。
2. 平移、俯仰和横滚相机分别测试。中线若复现，截取 `ReprojectionValidity`；若 validity 本身连续而 Final 不连续，再检查 A-Trous/resolve，不回退投影修复。
3. 固定彩色 emissive ROI，保存第 1/8/20/60 帧；用同一线性 HDR exposure 比较方差。20→60 帧仍无下降时，分别测试 `Spatial Samples=0/4`，并读取 visibility reject，不直接增加 rays。
4. 将 TOD 从白天阶跃到黄昏/夜晚：无 emissive 区域的 diffuse miss 和粗糙镜面 fallback 必须随 TOD 的 top/horizon/ground、sun/moon 改色；禁用 Unity skybox 或删除 Reflection Probe 后结果不得消失。
5. 镜面验收使用 smoothness=`0.2/0.5/0.9` 三材质：粗糙度越高空间滤波半径越大；移动相机时 smooth 材质不得出现固定屏幕拖影，rough 材质允许更慢但稳定的积累。
6. 性能以 4K Development Player、Frame Debugger/Trace Counters/Console spam 全关为准，预热 120 帧后采 300 帧。单列 `Specular Trace / Ray Resolve / Temporal / Spatial`；若 specular 超预算，优先将 initial scale 从 `0.5` 降到 `0.25` 或 spatial samples 从 `4` 降到 `2`。

## 14. 2026-08-02 黑区、彩色污染、移动重积累与镜面接管取证

### 14.1 本轮确认的四条根因

这轮没有再通过调曝光、sky 或 history 猜结果，而是从最终 Nanite GBuffer 写入一直追到 deferred additive composite：

1. `NaniteVBufferLitResolve.shader` 在 `GlobalIllumination` 之后又手工采样
   `unity_SpecCube0` / `_GlossyEnvironmentCubeMap`，并在 GI 近黑时把 cubemap 当 fallback。
   这条路径绕开 `_RealtimeGIDeferredInjection`，所以即使 RealtimeGI 已启用，Nanite 仍会保留
   Reflection Probe / Unity skybox IBL。
2. software specular 用 `roughness=1-smoothness` 做 eligibility，而默认
   `Min Specular Roughness=0.02`。因此 `smoothness=1` 恰好被判为“不参加追踪”并写零；这不是
   GGX 数值稳定性 clamp，而是错误地丢掉了最需要镜面路径的像素。
3. radiance cache 将人为构造的 directional specular lobes 加进 diffuse cache，同时把
   `baseColor*(direct+indirect)` 当作 outgoing diffuse radiance，漏掉 Lambert `1/π`。每次 bounce
   可多出约 π 的能量，彩色 emissive/specular 会跨帧扩散成红/青雾。所有材质还统一使用
   `abs(N·L)`，使单面物体的背面也被当作受光面。
4. Nanite procedural VBuffer 没有通过 URP 常规 MotionVectors draw 写出可靠对象 motion。旧
   temporal path 把零 motion 当“仍在原像素”，所以相机稍动就用错误位置验证历史，表现为重新积累。

Renderer Data 已核对：当前只有一份 `RealtimeGIRendererFeature`，不是重复 feature 双重注入。

### 14.2 已执行的接管与能量修复

- 删除 Nanite resolve 中的 `unity_SpecCube0` / `_GlossyEnvironmentCubeMap` 手工 fallback。
- URP `GlossyEnvironmentReflection` 在 deferred GI 接管期间返回零，由独立 traced specular additive
  pass 唯一注入；接管期之外也只使用 analytic TOD glossy sky，不读取 Reflection Probe。
- `Min Specular Roughness` 默认和 renderer asset 都改为 `0`。GGX 分布求值内部仍保留非零 alpha
  clamp，避免除零，但不再把完美光滑表面排除。
- diffuse cache 只保存 outgoing diffuse radiance：
  `Lo = emission + baseColor*(1-metallic)*(direct+indirect)/π`。删除 cache 内的伪 specular lobes，
  单面材质用 `saturate(N·L)`，只有显式 double-sided material 才使用双面响应。
- screen-hit specular 采用 previous fully-lit scene color；按 roughness 和屏幕传播距离选择 mip。
  world miss 使用 `GIEvaluateTODGlossySky`，不再依赖 Unity cubemap。

### 14.3 Previous scene-color 不是“声明了纹理就算落地”

场景色 history 是跨帧 imported RenderGraph texture。本轮补齐三个容易造成黑镜面的执行条件：

1. copy pass 禁止 RenderGraph culling；否则当前 frame graph 没有消费者时，写历史可能被静默删除；
2. copy 后显式 `GenerateMips`，不能依赖 imported RTHandle 边界上的隐式 mip 生成；
3. capture 移到 `AfterRenderingTransparents`，让下一帧 reflection 能看到透明物体关系。

运行日志进一步确认 RTHandle 曾同时启用 `autoGenerateMips` 和显式 `GenerateMips`，Unity 因此每帧
拒绝手工命令。现在 history 使用 `useMipMap=true, autoGenerateMips=false`，copy 后只有一条显式
生成链路；这条修改是粗糙镜面能够稳定读取有效 mip 的必要条件。

shader 采样前还会读取实际 mip count 并 clamp LOD，避免小窗口或动态分辨率下越界采样黑色。
捕获分辨率跟随 specular trace resolution，不新增一份 4K RGBA16F history。

### 14.4 移动相机的历史重投影

temporal match 现在同时生成两套 candidate：

- reconstructed world position × previous GPU view-projection：负责静态 Nanite；
- `uv-motionVector`：负责能写出运动矢量的动态普通网格。

两套 candidate 都在上一帧 3×3 footprint 内用 material、normal、world position 和 plane error
验证，再按最小几何误差选中。零 Nanite motion 不再被解释为“同屏幕像素”。相机 cut 仍会清空
history，但普通平移/旋转不应从零开始积累。

### 14.5 验收与故障分流

1. 退出再进入 Play 一次，让新 pipeline version、cache 和 scene-color history 从干净状态开始。
2. Frame Debugger 中确认顺序为：`Prepare Deferred Injection` → Nanite/GBuffer →
   `RealtimeGI/Screen Lighting` → `Render Deferred Lighting` → `Reset Deferred Injection` →
   transparents → `Capture Previous Scene Color` → `Generate Previous Scene Color Mips`。
3. 检查 `GI Current Specular`：smoothness=`1/0.9/0.5` 都必须有输出；完美光滑像素不能因
   roughness eligibility 写黑。镜面射线离屏时结果应随 TOD 改色。
4. 临时禁用所有 Reflection Probe，并把 Unity Environment Reflections Intensity 设为 0；GI 开启时
   Nanite 间接镜面仍应存在。若消失，先检查 `_RealtimeGIDeferredInjection` 的 prepare/reset 顺序，
   不得恢复 cubemap fallback。
5. 静态相机记录第 1/8/30/60 帧；随后只做小幅平移。静态 Nanite 的历史命中应连续，画面不能
   整体回到第 1 帧噪声。真正 camera cut 仍应重置。
6. 彩色 emissive 测试中，用线性 HDR 固定曝光比较 ROI。若红/青影响范围仍随时间单调扩大，先看
   cache `validity/confidence` 和 diffuse debug stage；本轮之后不允许 specular cache 或缺少 `1/π`
   再成为能量来源。
7. 性能仍以 Development Player 采样。新增 late scene-color copy/mip 是明确的固定成本，radiance
   cache 则删除了每 cell × 6 lobes 的 specular 累加。是否净下降必须由 GPU capture 判定，文档不
   冒充已达到预算。

参考实现边界：AKGI 的 previous scene-color mip screen hit 用来恢复中高频；ReSTIR GI/RTXDI 的
temporal/spatial reuse、重投影验证和 boiling/firefly 控制用于降低稀疏样本方差。当前实现复用
Nanite depth/GBuffer 与 sparse clipmap world trace，但不把 Reflection Probe 混回 traced result。
