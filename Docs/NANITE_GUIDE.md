# Nanite 使用指南 / Nanite Guide

[返回首页 / Back to README](../README.md) · [中文](#中文) · [English](#english)

![Nanite source mesh, cluster hierarchy, GPU culling, and final render / Nanite 模型、簇层级、GPU 剔除与渲染流程](Images/pipeline.svg)

## 中文

### 1. 导入并烘焙 FBX / OBJ

1. 将 `.fbx` 或 `.obj` 文件放进 Unity 项目的 `Assets/`，等模型导入完成。模型需要包含静态 `MeshFilter`；`SkinnedMeshRenderer` 不能用于这条烘焙流程。
2. 选择顶部菜单 **Nanite → Bake Nanite...**。把 Project 窗口中的模型资源拖到 **Model (FBX)** 槽位。这个槽位也接受由 `ModelImporter` 导入的 OBJ。
3. 设置 **Output Folder**（必须在 `Assets/` 下）、**Name** 和 **Type = Static Base**。为每个 SubMesh 检查 Shader 槽位；默认是 URP Lit。**Gadget** 只复制模型、材质和贴图，不生成 Nanite 资源或 Prefab。
4. 在窗口预览区拖动旋转模型，**Ctrl + 拖动**旋转灯光，滚轮缩放。确认模型和材质后点击 **Generate Static Base**。烘焙器会自动为源模型和复制模型开启 Read/Write。
5. 如果导入模型有多个静态 `MeshFilter`，窗口会提示：一次只烘焙三角面最多的那个 Mesh。要把多个节点作为一个 Nanite 对象使用，请先在建模工具里合并并重新导出。

例如输出目录为 `Assets/NaniteContent`、名称为 `Building` 时，会得到 `Assets/NaniteContent/Building/Nanite/Building.asset` 与 `Assets/NaniteContent/Building/Prefab/Building.prefab`，Page 数据位于 `Assets/StreamingAssets/Nanite/`。首次使用建议走窗口流程；Project 窗口右键的 **Assets → Nanite → Quick Bake** 只生成 NaniteMesh 资源，不生成可直接拖入场景的 Prefab。

### 2. 放入场景与预览

1. 从 Project 窗口把 **`Prefab/Building.prefab`** 拖到 Scene 视图或 Hierarchy。不要把原始 FBX 当作已烘焙对象拖入。
2. 选中场景实例，在 `NaniteRuntimeProxy` Inspector 确认 **Nanite Mesh** 指向刚生成的 `.asset`、**Rendering Mode = Nanite**，并检查 **Materials**。Prefab 的普通 `MeshRenderer` 默认关闭，这是 Nanite 渲染路径的预期状态。
3. 使用项目的 `PC_RPAsset` / `PC_Renderer` 和 Windows Direct3D 12。进入 Play Mode，在 **Game** 视图看最终材质结果；也可以在编辑模式的 **Scene** 视图观察。
4. 在 Scene 视图左上角的 **Shading / Draw Mode** 下拉菜单中选择 **Nanite → Clusters / Triangles / Pages**，分别查看 Cluster、三角形和 Page 可视化。返回普通 Shaded 模式即可关闭。不要为正式 VBuffer 路径启用 Proxy 的逐帧 Debug Mesh，因为它可能与正式深度/材质路径冲突。

### 3. 如何判断成功

- 烘焙后同时存在 `Nanite/*.asset`、`Prefab/*.prefab` 和对应的 StreamingAssets Page 文件；Console 没有 Bake 错误。
- 场景中的 Prefab 在 Game 视图可见，并且 Scene 视图的 Nanite 调试模式能显示 Cluster / Page。
- 在 **Project 窗口选中 NaniteMesh `.asset`**，运行 **Nanite → Diagnostics → Validate BVH Culling (Selected NaniteMesh)**；Console 应显示“BVH 剔除验证通过”。在支持 Compute 的 DX12 编辑器中，还可运行 **Validate GPU Culling (Selected NaniteMesh)**，通过时缺失与额外 Cluster 均为 0。
- 若看不到模型，先检查是否拖入了生成的 Prefab、`Nanite Mesh` 引用、PC Renderer / DX12、Console 的 shader 或 Page 错误。若模型只有 `SkinnedMeshRenderer`，或多节点目标不是最大的 Mesh，应调整源模型后重新烘焙。

## English

### 1. Import and bake an FBX / OBJ

1. Place the `.fbx` or `.obj` under `Assets/` and wait for Unity to import it. The model needs a static `MeshFilter`; this bake path does not accept a `SkinnedMeshRenderer`.
2. Open **Nanite → Bake Nanite...** and drag the imported model asset from the Project window into **Model (FBX)**. The field also accepts OBJ files imported by `ModelImporter`.
3. Choose an **Output Folder** under `Assets/`, enter a **Name**, and select **Type = Static Base**. Check the Shader slots for the model's submeshes; URP Lit is the default. **Gadget** only copies the model, materials, and textures and does not create a Nanite asset or Prefab.
4. Drag inside the preview to rotate the model, **Ctrl + drag** to rotate the light, and use the wheel to zoom. Click **Generate Static Base**. The baker enables Read/Write on the source and copied model.
5. If the model contains several static `MeshFilter` components, one bake processes only the mesh with the most triangles. Merge the nodes in your modeling tool first if they must form one Nanite object.

For an output folder of `Assets/NaniteContent` and a name of `Building`, the result includes `Assets/NaniteContent/Building/Nanite/Building.asset` and `Assets/NaniteContent/Building/Prefab/Building.prefab`; page data goes under `Assets/StreamingAssets/Nanite/`. For a first run, use the bake window. **Assets → Nanite → Quick Bake** creates only the NaniteMesh asset, without a scene-ready Prefab.

### 2. Add it to a scene and preview it

1. Drag the generated **`Prefab/Building.prefab`** from the Project window into the Scene or Hierarchy. Dragging the original FBX does not create a baked Nanite instance.
2. Select the instance and check its `NaniteRuntimeProxy`: **Nanite Mesh** should reference the new `.asset`, **Rendering Mode** should be **Nanite**, and **Materials** should be set. The Prefab's regular `MeshRenderer` is disabled by design.
3. Use the project's `PC_RPAsset` / `PC_Renderer` on Windows with Direct3D 12. Enter Play Mode and inspect the final material in the **Game** view; the **Scene** view also works for editor preview.
4. In the Scene view's **Shading / Draw Mode** menu, choose **Nanite → Clusters / Triangles / Pages** to inspect cluster, triangle, and page visualizations. Return to Shaded mode to turn them off. Avoid the Proxy's per-frame Debug Mesh when using the formal VBuffer path.

### 3. Check the result

- Confirm that the `Nanite/*.asset`, `Prefab/*.prefab`, and StreamingAssets page file exist and that the Console reports no bake error.
- The Prefab should appear in the Game view, while the Scene view Nanite modes should show cluster and page output.
- Select the NaniteMesh `.asset` **in the Project window** and run **Nanite → Diagnostics → Validate BVH Culling (Selected NaniteMesh)**. A successful run reports “BVH 剔除验证通过”. On a DX12 editor with compute support, run **Validate GPU Culling (Selected NaniteMesh)** too; missing and extra clusters should both be zero.
- If nothing is visible, check the generated Prefab, the `Nanite Mesh` reference, PC Renderer / DX12, and Console shader or page errors. Re-export and rebake models with only skinned meshes or with the intended mesh hidden among several nodes.
