# Nanite Runtime UE 约束与执行准则

本文件用于固定后续实现边界，避免执行多轮后偏离目标。除非你明确允许，后续所有 Nanite Runtime 改动都必须遵守本约束。

## 1. 运行时主流程约束

1. 必须保留 **两阶段 HZB culling**：
   - Pass1：上一帧 HZB 粗剔（instance + cluster）
   - Pass2：当前帧重建 HZB 后 second-chance 复测（只加回，不误删）
2. 最终输出的是“保守可见集合”：`FinalVisibleSelection`
3. 该集合必须直接可供后续光栅化/VBuffer 消费（不能依赖额外人工转换）

## 2. LOD 选择约束（不可改语义）

LOD cut 规则固定为：

`Projected(parentError) > lodErrorPixels && Projected(selfError) <= lodErrorPixels`

补充约束：
- 不能用仅 `selfError` 的简化判断替代
- 需要保持离线 DAG 的单调误差语义
- 依赖“同组同时切换”来避免裂缝

## 3. BVH 与并行剔除约束

1. cluster 全展开 O(n) 仅用于验证，不可作为最终运行时主路径
2. 运行时必须存在层级加速（node/part/cluster 分层或等价结构）
3. 统计项统一输出并长期保持：
   - `testedInstances`
   - `testedNodes`
   - `testedParts`
   - `testedClusters`
   - `visibleClusters`

## 4. VBuffer 与后续渲染约束

1. Runtime culling 的最终输出必须可转为 VBuffer 输入包
2. VBuffer 至少应具备：
   - 可标识可见几何归属（instance/cluster/triangle 之一或组合）
   - 与深度一致的可见性结果
3. Nanite 路径与传统管线并存：
   - 传统管线输出 GBuffer 后，Nanite 路径补充/合并
   - 后续继续走统一光照流程

## 5. 软/硬光栅分流约束（后续阶段）

1. 保留按屏幕投影面积做软硬光栅分流的设计位
2. 软硬路径都必须消费同一份 `NaniteRuntimeSelection`
3. 在未完成软硬合流前，先确保可验证的 VBuffer 输出链路稳定

## 6. URP 接入策略约束

1. 短期：RendererFeature 低侵入落地，便于回滚与验证
2. 中期：迁移到 URP 包内同位时序（已保留 A/B 开关）
3. 必须保留 A/B 回退：
   - `RendererFeature` 路径
   - `URP Core Timing` 路径

## 7. 验证与回归约束

1. CPU(BVH) vs GPU 可见集合长期对拍
2. Two-pass 前后集合检查：
   - second-chance 只能加回，不得误删 Pass1 可见
3. 关键场景回归至少覆盖：
   - 近景遮挡
   - 远景超大模型
   - 跨 mip 边界

## 8. 当前阶段完成判定

以下条件同时满足才算“当前阶段可用”：

1. RenderGraph 模式无“pass 未实现 RecordRenderGraph”警告
2. 运行时可观察到 cluster culling 与 LOD 选择结果变化
3. 可视化看到 VBuffer 预览（至少 debug 级别）
4. 验证菜单输出统计项完整，且能用于回归
