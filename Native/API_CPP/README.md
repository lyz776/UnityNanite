# API_CPP.dll 是什么？从哪来？

## DLL 是什么

**DLL（Dynamic Link Library）** 是 Windows 上的一种**已编译好的原生程序库**（里面是机器码），扩展名通常是 `.dll`。

- Unity 里的 C# 在 **托管环境**（.NET/IL2CPP）里跑。
- **meshoptimizer** 是 **C/C++** 写的，不能直接被 C# “include”。
- 做法：先把 meshoptimizer **编译进一个 DLL**，再在 C# 里用 `[DllImport("API_CPP")]` **按函数名加载**并调用——这就是你们项目里的 `MeshOptimizerNative.cs`。

**`API_CPP` 不是网上固定下载的名**，只是我们约定的 **DLL 文件名**（不带 `.dll`）。你也可以改成 `MyMeshOpt.dll`，同时改 C# 里的 `MeshOptimizerNative.DllName`。

---

## 为什么找不到 meshoptimizer.c？

**meshoptimizer 新版本（例如 1.1）已经把实现拆成 `src` 目录下许多个 `.cpp` 文件**，**不再提供单一的 `meshoptimizer.c`**。

你本机完整源码里应当是类似下面这样（示例路径）：

`...\meshoptimizer-master\meshoptimizer-master\src\`

其中有 **`meshoptimizer.h`**，以及 **`allocator.cpp`、`clusterizer.cpp`、`partition.cpp`……** 等一堆 `.cpp`，**唯独没有** `meshoptimizer.c`。**这是正常现象**，按下面步骤编即可。

若你的目录里 **只有 `.h` 没有 `.cpp`**，说明压缩包不完整或没下载完整源码，请到官方仓库重新获取：<https://github.com/zeux/meshoptimizer>

---

## DLL 从哪里来

1. **不是** Unity 包里自带。
2. 用 **Visual Studio**（或下面的 **CMake**）把：**本文件夹的 `api_cpp.cpp`** + **官方 `src` 里全部 `.cpp`** 编成 **`API_CPP.dll`**。
3. 编好后拷贝到：`你的Unity工程/Assets/Plugins/x86_64/API_CPP.dll`。

---

## 方式一：只用 Visual Studio（适合不熟悉 CMake）

### 准备

从你解压的 meshoptimizer 里记住 **`src` 文件夹的完整路径**（里面有 `meshoptimizer.h` + 一堆 `.cpp`）。**不要把整个仓库拷进 Assets**，避免 Unity 去解析 C++。

### 建 DLL 工程

1. VS → **创建新项目** → **动态链接库 (DLL)** → 平台 **x64**。
2. 删掉向导里多余的示例 `.cpp`（若有）。
3. **右键项目 → 属性 → C/C++ → 常规 → 附加包含目录**：填 **meshoptimizer 的 `src` 文件夹路径**。
4. **右键项目 → 添加 → 现有项**，多选 **`src` 目录下所有的 `.cpp` 文件**，再加上本仓库里的 **`api_cpp.cpp`**（路径例如 `...\Unity\NN\Native\API_CPP\api_cpp.cpp`）。
5. **常规 → C++ 语言标准**：**ISO C++17**（或更新）。
6. **常规 → 目标文件名**：**`API_CPP`** → 这样会生成 **`API_CPP.dll`**。
7. **C/C++ → 预编译头**：**不使用预编译头**（若某项无法编译，可对单个文件再在文件属性里设一次）。
8. **Release | x64** → **生成解决方案**。
9. 在输出目录取 **`API_CPP.dll`** → 复制到 **`Assets/Plugins/x86_64/`**。

---

## 方式二：用本目录的 CMake（一条命令链好依赖）

在 **`Native\API_CPP`** 打开终端（PowerShell / cmd），把 `MESHOPT_SRC_DIR` 改成你机器上真实的 `src` 路径：

```bat
cmake -B build -A x64 -DMESHOPT_SRC_DIR="../meshoptimizer/src"
cmake --build build --config Release
```

成功后一般在 **`build\Release\API_CPP.dll`**，复制到 Unity 的 **`Assets\Plugins\x86_64\`**。

---

## 导出函数名要和 C# 一致

C# 里写的是类似：

```csharp
[DllImport("API_CPP", EntryPoint = "BuildMeshletFlex")]
```

那么 DLL 里**必须**导出名为 **`BuildMeshletFlex`** 的 C 函数（本仓库的 `api_cpp.cpp` 已按 `MeshOptimizerNative.cs` 里的名字对齐）。

检查导出（VS **Developer Command Prompt**）：

```bat
dumpbin /EXPORTS API_CPP.dll
```

应能看到 `BuildMeshletsBound`、`BuildMeshletFlex`、`OptimizeMeshlet`、`PartitionClusters`、`ComputeSphereBounds` 等。

---

## 常见问题

- **找不到 meshoptimizer.c**：新版没有该文件，请按上文加入 **`src` 下全部 `.cpp`**。
- **DllNotFoundException**：检查 DLL 是否在 **`Plugins/x86_64`**，且为 **x64**。
- **BadImageFormatException**：常见是 **32 位 DLL** 进了 **64 位** Unity 编辑器，请用 **x64** 重新编译。

---

## 和纯 C# 的关系

meshoptimizer 也可以 **逐行移植/绑定**到 C#，但工作量大；**DLL 方案**是：官方 C++ 实现编进一个文件，Unity 只负责 **P/Invoke 调用**。
