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
Screen Probe Diffuse + World-space Rough Specular
        ↓
Temporal / Spatial Filter + Deferred Composite
```

它必须满足以下原则：

- 世界空间 Cache 负责离屏信息，屏幕空间只负责 Final Gather、历史复用和可选加速。
- 漫反射与镜面共用同一套世界可见性和 Radiance，不再配置 Reflection Probe、Planar Reflection 或另一套镜面 RT。
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

- 漫反射：低分辨率 Screen Probe、cosine ray、世界 Cache 查询、时域累积和双边上采样。
- 镜面：半分辨率 GGX initial sample、全分辨率 ray reuse/ratio estimator、命中距离虚像重投影、时域与粗糙度空间过滤。
- Diffuse/Specular 最终在 Deferred 路径合成，不依赖 Reflection Probe 或 Planar Reflection。
- `RealtimeGIRendererFeature` 已接入 URP RenderGraph；Clipmap build 仍由 `GIClipmapSystem` immediate CommandBuffer 驱动。

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
| Diffuse/Specular Final Gather | `Resources/RealtimeGI/GIScreenLighting.compute` |
| URP 调度与合成 | `Runtime/RealtimeGIRendererFeature.cs`, `Shaders/GIComposite.shader` |

不要直接修改 `Assets/Scripts/Nanite` 来修 GI。Nanite 若提供新稳定接口，应由 GI 侧适配后消费。

## 5. 尚未完成，以及应该怎么做

以下按基础性排序，不包含植被、水和透明等已排除内容。

### P0：移除镜面对 SSR 的残留依赖

当前 `GIScreenLighting.compute` 的 `GITraceRadianceDetailed()` 仍先调用 `GIScreenTrace()`，然后才走世界 Clipmap。它不是 Reflection Probe，但严格意义上仍是屏幕空间追踪，与“不使用 SSR”的目标有偏差。

应该：

1. 增加明确的 `enableScreenTrace`，默认关闭；或直接删除 `GIScreenTrace` 分支。
2. Initial Sample 始终使用统一世界追踪；命中后读取方向性 Surface Cache。
3. 保留 full-resolution ray reuse、ratio estimator、虚像重投影和时空滤波，这些不是 SSR。
4. Screen color 只能作为可选近场高频补偿，不能成为镜面正确性的前提。

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
2. 先解决 P0 的 SSR 残留、Shader Family 和追踪稳健性。
3. 再压缩方向性 Cache，之后迁移 RenderGraph 和稳定 GPU Scene。
4. 最后按项目需求逐类加入地形、植被、Skinned、水与透明；不要一次设计一个“万能适配器”。

## 7. 不应破坏的约束

- Static 与 Dynamic 必须是独立 Layer。
- 目标 Radiance 不能在同一 kernel 同时作为 SRV/UAV。
- 几何清除时必须同时清 Surface、UV、Radiance 和 Validity。
- Material slot 必须稳定；修改 GPU struct 必须同步 C# stride 与 HLSL ABI version。
- 世界 Cache 未就绪时必须有显式 fallback。
- 局部灯、反弹、过滤和更新必须有硬预算。
- 不以修改 Nanite 内部瞬态 Buffer 的方式完成 GI 功能。

## 8. 参考资料

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

### 项目内 Nanite 资料

- `Assets/Scripts/Nanite/NANITE_PERFORMANCE_AUDIT.md`
- `Assets/Scripts/Nanite/NANITE_PIPELINE_ROADMAP.md`
- `Assets/Scripts/Nanite/NANITE_IMPLEMENTATION_NOTES.md`
- `Assets/Scripts/Nanite/NaniteSceneVisibilityBufferBackend.cs`
- `Assets/Scripts/Nanite/NaniteGpuPagePool.cs`
- `Assets/Scripts/Nanite/NaniteRendererFeature.cs`

外部实现只用于学习和算法对照。实际移植代码前必须单独核对许可证；论文或其他硬件上的毫秒数不能直接当作本项目结果。
