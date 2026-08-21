# RenderDoc GPU pass timing

Unity 的 `Recorder.gpuElapsedNanoseconds` 在当前 Development Player 中返回 0，已有
binary profiler capture 也只有 Main/Render CPU thread。因此这些 0 不是 GPU 成绩，不能用于
3 ms 验收。本工具完全绕开该路径：RenderDoc 捕获一帧，再用
`GPUCounter.EventGPUDuration` 对 D3D action 做 GPU timestamp replay。
RenderDoc 1.41 的 D3D12 实现把每个 event 前后两个 timestamp 的 delta 除以 queue frequency，
直接返回 seconds：[`d3d12_counters.cpp`](https://github.com/baldurk/renderdoc/blob/v1.41/renderdoc/driver/d3d12/d3d12_counters.cpp)。

Unity 6000.3 文档也要求先由 `SystemInfo.supportsGpuRecorder` 确认平台支持，且 Recorder 的
GPU 值固定滞后三帧；Player profiler 命令行只提供 `-profiler-enable`、raw log 和 frame count，
没有可强制补出 GPU thread 的 `-profiler-enable-gpu` 参数：

- [Recorder.gpuElapsedNanoseconds](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Profiling.Recorder-gpuElapsedNanoseconds.html)
- [Profiler command line arguments](https://docs.unity3d.com/6000.3/Documentation/Manual/profiler-command-line-arguments.html)

## 一条命令捕获并导出

```powershell
$player = 'D:\path\to\UnityNanite.exe'
.\Tools\RenderDoc\Invoke-RenderDocGpuCapture.ps1 `
  -Executable $player `
  -WorkingDirectory (Split-Path $player) `
  -PlayerArguments '-force-d3d12 -screen-width 1920 -screen-height 1080 -screen-fullscreen 0' `
  -CaptureTemplate 'D:\UnityNanite\tmp\renderdoc\gi-steady' `
  -WarmupSeconds 5
```

输出包括：

- `.rdc`：原始单帧捕获；
- `.gpu.json`：机器可读的 marker/action duration；
- `.gpu.csv`：marker inclusive/exclusive duration；
- `.capture-result.json`：实际帧号、API 和捕获文件路径。

`-CaptureFrame 300` 会在 D3D device 创建后的第 300 个 frame boundary 捕获，适合跨版本做
同一帧比较；未指定时则在 `-WarmupSeconds` 后触发下一帧。若 Player 已由 RenderDoc 启动，
可用 `-TargetIdent <port>` 参数组连接，不能对已经初始化 D3D12 的普通进程事后注入。
包装脚本在捕获完成后不会强杀 Player；自动验收请让 Player 自己带退出参数，或在结果返回后正常关闭。

建议首次用 `-Executable` 启动；结果中的 `targetIdent` 可供后续四次使用
`-TargetIdent <ident>`，从同一个静止 Player 分别捕获，避免重复启动。五份 `.gpu.json` 用：

```powershell
.\Tools\RenderDoc\Summarize-RenderDocGpuTimings.ps1 `
  -InputJson (Get-ChildItem 'D:\UnityNanite\tmp\renderdoc\steady-*.gpu.json').FullName `
  -OutputJson 'D:\UnityNanite\tmp\renderdoc\steady-summary.json'
```

汇总文件给出 GI 去重总和以及每个 marker 的 min/median/p95/max；验收值取 median，
min/max 用来暴露回放或 workload 不稳定。

## 只导出现有捕获

```powershell
.\Tools\RenderDoc\Export-RenderDocGpuTimings.ps1 `
  -Capture 'D:\capture\frame.rdc' `
  -OutputJson 'D:\capture\frame.gpu.json' `
  -OutputCsv 'D:\capture\frame.gpu.csv' `
  -MarkerPrefixes 'RealtimeGI/'
```

导出器会硬性检查：存在 `EventGPUDuration`、值为 float64、单位为 seconds、至少有一个
`RealtimeGI/` marker 包含 timestamped action、API 为 D3D12、replay 非 degraded，并能枚举到
RTX 4500 Ada。条件不满足会失败，并在 JSON 中留下候选 GPU group，避免把 0 ms 当成成功。
仅分析诊断用的旧 D3D11 捕获时必须显式传 `-AllowNonD3D12`。

## 验证标准

```powershell
$j = Get-Content 'D:\capture\frame.gpu.json' -Raw | ConvertFrom-Json
$j | Select-Object graphicsAPI,durationResultCount,matchedMarkerCount,matchedActionCount,matchedActionSumMs
$j.markers | Where-Object exclusiveTimedActionCount -gt 0 |
  Sort-Object exclusiveGpuMs -Descending |
  Select-Object name,exclusiveGpuMs,inclusiveGpuMs,timedActionCount
```

正式数据必须满足：

1. `graphicsAPI` 为 `GraphicsAPI.D3D12`；
2. `durationResultCount` 和 `matchedActionCount` 都大于 0；
3. JSON 的 counter unit 为 `CounterUnit.Seconds`；
4. `.capture-result.json` 的 `nvidiaSmi.gpus[].name` 为
   `NVIDIA RTX 4500 Ada Generation`，`.gpu.json` 的 replay vendor 为 NVIDIA 且不是 degraded；
5. 相同静止场景至少捕获 5 次，报告每个 pass 的 median，并保留 `.rdc`。

`inclusiveGpuMs` 是 marker 下所有唯一 leaf action 的和；`exclusiveGpuMs` 会扣除嵌套的
匹配 marker。它们是 replay 时 GPU timestamp 得到的 action workload，不是多队列 wall-clock
span，也不包含没有 timestamp 的 barrier/idle。GI 的总数请使用 JSON 中去重后的
`matchedActionSumMs`，不要直接把嵌套 marker 的 inclusive 值相加。

## 已验证的导出链

RenderDoc 1.41 的一份现有 Unity D3D11 捕获成功返回 368 个非零 timestamp result，唯一 action
和为 3.920096 ms；用该捕获的 `RenderPrePass.` marker 验证聚合后，176 个 action 的去重和为
2.405024 ms。这个样本只用于证明捕获解析链工作，不是本项目 D3D12/RTX 4500 Ada 的 GI
性能成绩。最终验收仍须按上面的 D3D12 命令重新捕获。
