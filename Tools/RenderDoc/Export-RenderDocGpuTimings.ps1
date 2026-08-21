[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Capture,

    [string] $OutputJson,

    [string] $OutputCsv,

    [string[]] $MarkerPrefixes = @('RealtimeGI/'),

    [switch] $AllowMissingMarkers,

    [switch] $AllowNonD3D12,

    [string] $ExpectedGpuName = 'NVIDIA RTX 4500 Ada Generation',

    [string] $RenderDocDirectory = 'C:\Program Files\RenderDoc'
)

$ErrorActionPreference = 'Stop'

$capturePath = (Resolve-Path -LiteralPath $Capture).Path
$qrenderdoc = Join-Path $RenderDocDirectory 'qrenderdoc.exe'
$pythonScript = Join-Path $PSScriptRoot 'ExportRenderDocGpuTimings.py'

if (-not (Test-Path -LiteralPath $qrenderdoc -PathType Leaf)) {
    throw "qrenderdoc.exe was not found at '$qrenderdoc'."
}
if (-not (Test-Path -LiteralPath $pythonScript -PathType Leaf)) {
    throw "RenderDoc export script was not found at '$pythonScript'."
}

if ([string]::IsNullOrWhiteSpace($OutputJson)) {
    $OutputJson = [IO.Path]::ChangeExtension($capturePath, '.gpu.json')
}
$OutputJson = [IO.Path]::GetFullPath($OutputJson)

if ([string]::IsNullOrWhiteSpace($OutputCsv)) {
    $OutputCsv = [IO.Path]::ChangeExtension($OutputJson, '.csv')
}
$OutputCsv = [IO.Path]::GetFullPath($OutputCsv)
$exportResult = $OutputJson + '.export-result.json'
$runToken = [guid]::NewGuid().ToString('N')

$savedEnvironment = @{}
$environmentNames = @(
    'GI_RDC_CAPTURE',
    'GI_RDC_OUTPUT_JSON',
    'GI_RDC_OUTPUT_CSV',
    'GI_RDC_MARKER_PREFIXES',
    'GI_RDC_EXPORT_RESULT',
    'GI_RDC_RUN_TOKEN'
)
foreach ($name in $environmentNames) {
    $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

try {
    $env:GI_RDC_CAPTURE = $capturePath
    $env:GI_RDC_OUTPUT_JSON = $OutputJson
    $env:GI_RDC_OUTPUT_CSV = $OutputCsv
    $env:GI_RDC_MARKER_PREFIXES = $MarkerPrefixes -join ';'
    $env:GI_RDC_EXPORT_RESULT = $exportResult
    $env:GI_RDC_RUN_TOKEN = $runToken

    $process = Start-Process `
        -FilePath $qrenderdoc `
        -ArgumentList @('--python', ('"{0}"' -f $pythonScript)) `
        -PassThru `
        -Wait
}
finally {
    foreach ($name in $environmentNames) {
        [Environment]::SetEnvironmentVariable(
            $name,
            $savedEnvironment[$name],
            'Process')
    }
}

if (-not (Test-Path -LiteralPath $exportResult -PathType Leaf)) {
    throw "qrenderdoc exited without creating '$exportResult'."
}
$exportStatus = Get-Content -LiteralPath $exportResult -Raw | ConvertFrom-Json
if ($exportStatus.runToken -ne $runToken) {
    throw "qrenderdoc did not update '$exportResult' for this export run."
}
if (-not $exportStatus.success) {
    throw "qrenderdoc GPU timing export failed: $($exportStatus.error)"
}
if (-not (Test-Path -LiteralPath $OutputJson -PathType Leaf)) {
    throw "qrenderdoc exited without creating '$OutputJson'."
}

$result = Get-Content -LiteralPath $OutputJson -Raw | ConvertFrom-Json
if (-not $AllowNonD3D12 -and $result.graphicsAPI -ne 'GraphicsAPI.D3D12') {
    throw "Capture API is '$($result.graphicsAPI)', expected GraphicsAPI.D3D12."
}
if ($result.replay.degraded) {
    throw 'RenderDoc replay is degraded; timing data is not acceptable.'
}
if (-not [string]::IsNullOrWhiteSpace($ExpectedGpuName)) {
    $gpuNames = @($result.replay.availableGPUs | Select-Object -ExpandProperty name)
    if ($ExpectedGpuName -notin $gpuNames) {
        throw ("Expected replay GPU '{0}' was not available. Found: {1}" -f `
            $ExpectedGpuName, ($gpuNames -join ', '))
    }
}
if (-not $AllowMissingMarkers -and (
        $result.matchedActionCount -eq 0 -or
        $result.matchedActionSumMs -le 0.0)) {
    $candidateNames = $result.groupCandidates |
        Select-Object -ExpandProperty name -Unique |
        Select-Object -First 20
    throw ("No positive timestamped GPU work matched '{0}'. Candidate GPU groups: {1}" -f `
        ($MarkerPrefixes -join ';'), ($candidateNames -join ', '))
}
Write-Host ("RenderDoc: {0} timestamped actions; matched marker union {1:N3} ms" -f `
    $result.matchedActionCount, $result.matchedActionSumMs)
$result.markers |
    Where-Object { $_.exclusiveTimedActionCount -gt 0 } |
    Sort-Object exclusiveGpuMs -Descending |
    Select-Object -First 24 name, exclusiveGpuMs, inclusiveGpuMs, timedActionCount |
    Format-Table -AutoSize

Write-Output $OutputJson
Write-Output $OutputCsv
