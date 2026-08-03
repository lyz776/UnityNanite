# RealtimeGI 交接说明

本文只描述当前代码状态。产品范围暂时是 Nanite 与普通不透明 Mesh；植被、水、半透明、粒子和精确 Skinned Mesh 不在当前闭环内。

## 1. 当前系统

```text
Nanite coarse GI geometry / 普通 Mesh
                    ↓
             Unified GI Scene
                    ↓
       8 级相机跟随 Sparse Clipmap
          Static + Dynamic Overlay
                    ↓
 Occupancy + Surface + Conservative Distance
                    ↓
      Directional Surface Radiance Cache
                    ↓
 旧版 Diffuse Reservoir + 软件 GGX Specular
                    ↓
        URP Deferred additive injection
```

它不是 NRC、DDGI、PRT，也不依赖 SSR、Planar Reflection 或 Reflection Probe。离屏信息来自世界空间 Clipmap；屏幕路径只负责 final gather、重用与过滤。

## 2. 本次回退的准确边界

当前 diffuse/specular 主链已回到实施“1–6 项”之前的版本：

- 保留当时已有的 Diffuse reservoir。
- 保留 persistent spatial reservoir、luminance moments/history length、方差驱动过滤与自发光 alias importance sampling（最多 32 项）。
- 保留 P0 的 radiance/irradiance/Lo 能量契约、sky visibility、Spatial W/M 偏差修复、disocclusion 额外射线硬预算和未就绪几何的 analytic fallback。
- 保留 P1 的 1 spp diffuse、quarter-resolution diffuse、0.375 初始分辨率 specular、2×2 resolve/upscale，以及粗糙度不高于 0.35 的 virtual-hit reprojection。
- 保留当时已有的 Specular Trace → Resolve → Temporal → Spatial 链路。
- 移除后来新增的全分辨率 Specular Reservoir 调度和 Buffer。
- 不包含后来增加的完整 path payload/visibility、完整 reconnection、材质虚拟分页、reliable object/surface ID 改造。
- 不包含 Ramp 灯语义；项目尚未提供 Ramp 灯的正式光照函数和 ABI。

不能把当前系统宣称为完整 ReSTIR GI、ReGIR 或通用 GRIS。旧链路使用了 RIS/reservoir、时空重用和有限 reconnection 思路，但缺少完整路径状态与严格可见性重连。

## 3. 必须保留的 Surface Cache 稳定化

文件：`Resources/RealtimeGI/GIRadianceCache.compute`

- 启用 secondary bounce 时，新 Cell 使用 4 条 cosine hemisphere 射线。
- 已有匹配历史的稳定 Cell 至少使用 2 条射线。
- 射线采用确定性 R2 低差异序列，并使用 per-Cell Cranley-Patterson rotation；frame 只推进序列，不恢复逐帧白噪声。
- 有效历史权重下限为 `0.65`，避免单次红色/青色命中覆盖整个 Cell。
- Validity 低 8 位保存 confidence，中间 8 位保存累计 sky visibility，高 16 位保存 material revision。
- 六方向 lobe 仍沿用回退阶段的正半球写法，没有把六个 lobe 全写成同一份 radiance。
- `secondaryBounceRays = 0` 仍是明确关闭开关。

`GIClipmapSystem.RadianceAlgorithmVersion` 已递增；首次运行会重新排队旧 Radiance Cache，这是一次性正确性成本。

## 4. 局部灯光

文件：

- `Runtime/GILocalLight.cs`
- `Runtime/GILightTypes.cs`
- `Shaders/GILightCommon.hlsl`
- `Resources/RealtimeGI/GIRadianceCache.compute`

当前只支持 Point 与 Spot：

1. CPU 收集显式注册的 `GILocalLight` 并上传安全上限，默认 2048；不按相机距离挑 16 盏灯。
2. GPU 为本帧待更新 Brick 做影响范围测试与 Top-K，默认每 Brick 32 盏，可配置 8–64。
3. 每个 Surface Cell、离屏 diffuse fallback 与 specular world hit 都只遍历命中 Brick 的灯表。
4. 局部阴影射线仍受 `maxShadowedLocalLightsPerSurface` 硬预算限制。
5. C#/HLSL 灯光 ABI 均为 4 个 `float4`，stride 固定 64 bytes。

这不是 ReGIR，也不是 tiled deferred 直接光列表。它是 Surface Cache 更新使用的 world-space Brick light list。未来 Ramp Point/Spot 必须先由项目定义正式参数和直接光函数，再扩展统一 ABI；当前代码不会猜测 Ramp 行为。

## 5. 世界覆盖与数据

- 8 级 Clipmap，逻辑分辨率 `128³`，Brick 为 `8³`。
- Cell size：`0.2 / 0.4 / 0.8 / 1.6 / 3.2 / 6.4 / 12.8 / 25.6 m`。
- C0 单边覆盖 25.6 m，C7 单边覆盖 3276.8 m，并随相机按 Brick 滚动。
- 默认物理池：8192 Static Bricks + 2048 Dynamic Bricks。
- Surface 保存法线/材质、UV、六方向 RGB9E5 radiance 和 validity。
- 距离场是 Brick 内保守 unsigned distance，不是全局 SDF。

## 6. 材质和更新语义

当前基础材质路径支持 BaseColor/BaseMap、Emission/EmissionMap、UV ST、常量 roughness/metallic。自定义 Shader Graph 不会被自动解释，必须通过有限 Shader Family 或显式 GI proxy 参数接入。

- 颜色、贴图、自发光、灯光、TOD 变化：失效并更新 Radiance，不重建 geometry。
- Transform、Mesh topology、真实 dissolve hole 或遮挡形状变化：重建覆盖到的 geometry Bricks。
- receiver-only 物体可只写 Deferred GBuffer/motion vector，不写 Dynamic Clipmap。
- contributor/occluder 移动物体写 Dynamic Overlay；旧区域清除后重建，不走 DDGI probe 的慢恢复路径。

## 7. 当前明确未完成

1. 完整材质虚拟化/Shader Family：Normal、Mask、Alpha Test、项目风格化节点。
2. 完整 path payload、严格 visibility/reconnection 与可靠 object/surface ID。
3. 新的 persistent Specular Reservoir；当前使用回退阶段的旧镜面链。
4. ReGIR、完整 ReSTIR DI、通用 GRIS。
5. Cookie、IES、Area Light、彩色透射和 Ramp 灯。
6. 薄墙/细结构/近镜面的三角形级命中细化。
7. Clipmap build 的 RenderGraph/Async Compute 化与 GPU-driven dirty compaction。
8. RTX 4060、约 3 ms 目标的正式场景压力曲线；目前仍需 Development Player GPU capture 验证。

## 8. 关键入口

| 责任 | 文件 |
|---|---|
| 场景收集/ABI | `Runtime/RealtimeGIScene.cs`, `Runtime/GISceneTypes.cs` |
| Mesh/Nanite 适配 | `Runtime/GISceneSnapshotBuilder.cs`, `Runtime/GIGeometryStreamCache.cs` |
| Clipmap 调度 | `Runtime/GIClipmapSystem.cs`, `Runtime/GIClipmapLayer.cs` |
| Voxel/Distance | `Resources/RealtimeGI/GIClipmapBuild.compute` |
| Light/Surface Cache | `Resources/RealtimeGI/GIRadianceCache.compute`, `Shaders/GILightCommon.hlsl` |
| Diffuse/Specular final gather | `Resources/RealtimeGI/GIScreenLighting.compute` |
| URP 调度/合成 | `Runtime/RealtimeGIRendererFeature.cs`, `Shaders/GIComposite.shader` |

GI 修改不得直接覆盖 `Assets/Scripts/Nanite`。Nanite 若提供新的稳定只读数据接口，应由 GI 侧适配消费。

## 9. 参考资料

- Müller et al., *Real-time Neural Radiance Caching for Path Tracing*, 2021：`mueller21realtime.pdf`
- NVIDIA RTXGI / DDGI：<https://github.com/NVIDIAGameWorks/RTXGI-DDGI>
- NVIDIA RTXDI：<https://github.com/NVIDIAGameWorks/RTXDI>
- NVIDIA NRD：<https://github.com/NVIDIAGameWorks/RayTracingDenoiser>
- NVIDIA Falcor：<https://github.com/NVIDIAGameWorks/Falcor>
- ReSTIR GI 与 GRIS 论文
- Unreal Engine Lumen/Nanite 公开文档与源码发行版
- AKG4e3 UnrealEngine 4.27 AKGI：<https://github.com/AKGWSB/UnrealEngine/tree/4.27-akgi>
- RealtimeGI 实战篇（上）：<https://zhuanlan.zhihu.com/p/12632657244>
- RealtimeGI 实战篇（下）：<https://zhuanlan.zhihu.com/p/12636727339>
- Neural Radiance Cache：<https://zhuanlan.zhihu.com/p/388120500>
- Surfel-based Radiance Cascade GI：<https://zhuanlan.zhihu.com/p/2062594335728784841>

参考代码仅用于公司预研、个人学习和技术实验；进入产品前需单独完成许可证审查。
