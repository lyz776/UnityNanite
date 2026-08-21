[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateCount(2, 100)]
    [string[]] $InputJson,

    [Parameter(Mandatory = $true)]
    [string] $OutputJson,

    [string] $OutputCsv,

    [switch] $AllowNonD3D12
)

$ErrorActionPreference = 'Stop'

function Get-Distribution {
    param([double[]] $Values)

    $sorted = @($Values | Sort-Object)
    if ($sorted.Count -eq 0) {
        throw 'Cannot summarize an empty distribution.'
    }
    $middle = [math]::Floor($sorted.Count / 2)
    if (($sorted.Count % 2) -eq 0) {
        $median = ($sorted[$middle - 1] + $sorted[$middle]) * 0.5
    }
    else {
        $median = $sorted[$middle]
    }
    $p95Index = [math]::Max(
        0,
        [math]::Min($sorted.Count - 1, [math]::Ceiling($sorted.Count * 0.95) - 1))

    [ordered]@{
        count = $sorted.Count
        min = $sorted[0]
        median = $median
        mean = ($sorted | Measure-Object -Average).Average
        p95 = $sorted[$p95Index]
        max = $sorted[-1]
    }
}

$resolvedInputs = @($InputJson | ForEach-Object {
    (Resolve-Path -LiteralPath $_).Path
})
$documents = @($resolvedInputs | ForEach-Object {
    Get-Content -LiteralPath $_ -Raw | ConvertFrom-Json
})

$graphicsApis = @($documents | Select-Object -ExpandProperty graphicsAPI -Unique)
if ($graphicsApis.Count -ne 1) {
    throw "Input captures use different graphics APIs: $($graphicsApis -join ', ')"
}
if ($graphicsApis[0] -ne 'GraphicsAPI.D3D12') {
    if (-not $AllowNonD3D12) {
        throw "Summary input is '$($graphicsApis[0])', expected GraphicsAPI.D3D12."
    }
    Write-Warning "Summary input is '$($graphicsApis[0])', not the required GraphicsAPI.D3D12."
}

$badDocuments = @($documents | Where-Object {
    $_.durationResultCount -le 0 -or
    $_.matchedActionCount -le 0 -or
    $_.matchedActionSumMs -le 0.0 -or
    $_.counter.unit -ne 'CounterUnit.Seconds' -or
    $_.replay.degraded
})
if ($badDocuments.Count -gt 0) {
    throw "$($badDocuments.Count) timing exports failed timestamp/replay validity checks."
}

$commonPaths = @($documents[0].markers | Select-Object -ExpandProperty path -Unique)
foreach ($document in $documents | Select-Object -Skip 1) {
    $documentPaths = @($document.markers | Select-Object -ExpandProperty path -Unique)
    $commonPaths = @($commonPaths | Where-Object { $_ -in $documentPaths })
}

$markers = @()
foreach ($path in $commonPaths) {
    $samples = @($documents | ForEach-Object {
        @($_.markers | Where-Object path -eq $path)[0]
    })
    if ($samples.Count -ne $documents.Count -or $null -in $samples) {
        continue
    }
    $markers += [pscustomobject][ordered]@{
        name = $samples[0].name
        path = $path
        depth = $samples[0].depth
        samples = $samples.Count
        inclusiveGpuMs = Get-Distribution @(
            $samples | ForEach-Object { [double] $_.inclusiveGpuMs })
        exclusiveGpuMs = Get-Distribution @(
            $samples | ForEach-Object { [double] $_.exclusiveGpuMs })
        timedActionCount = @(
            $samples | Select-Object -ExpandProperty timedActionCount -Unique)
    }
}

$markers = @($markers | Sort-Object {
    -[double] $_.exclusiveGpuMs.median
})
$allPathCount = @(
    $documents | ForEach-Object { $_.markers.path } | Select-Object -Unique
).Count

$summary = [ordered]@{
    schemaVersion = 1
    graphicsAPI = $graphicsApis[0]
    sampleCount = $documents.Count
    captures = @($documents | Select-Object -ExpandProperty capture)
    replayVendors = @($documents | ForEach-Object { $_.replay.vendor } | Select-Object -Unique)
    replayGPUs = @(
        $documents |
            ForEach-Object { $_.replay.availableGPUs.name } |
            Select-Object -Unique)
    matchedActionSumMs = Get-Distribution @(
        $documents | ForEach-Object { [double] $_.matchedActionSumMs })
    commonMarkerCount = $markers.Count
    omittedConditionalMarkerCount = $allPathCount - $markers.Count
    markers = $markers
    warnings = @(
        'Use median for the reported pass result; min/max expose replay or workload instability.',
        'Marker values are unique action-duration sums, not multi-queue wall-clock spans.'
    )
}

$outputJsonPath = [IO.Path]::GetFullPath($OutputJson)
$outputParent = Split-Path -Parent $outputJsonPath
if ($outputParent) {
    New-Item -ItemType Directory -Path $outputParent -Force | Out-Null
}
$summary | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $outputJsonPath -Encoding UTF8

if ([string]::IsNullOrWhiteSpace($OutputCsv)) {
    $OutputCsv = [IO.Path]::ChangeExtension($outputJsonPath, '.csv')
}
$outputCsvPath = [IO.Path]::GetFullPath($OutputCsv)
$outputCsvParent = Split-Path -Parent $outputCsvPath
if ($outputCsvParent) {
    New-Item -ItemType Directory -Path $outputCsvParent -Force | Out-Null
}
$markers | ForEach-Object {
    [pscustomobject]@{
        name = $_.name
        samples = $_.samples
        exclusiveMedianMs = $_.exclusiveGpuMs.median
        exclusiveMinMs = $_.exclusiveGpuMs.min
        exclusiveP95Ms = $_.exclusiveGpuMs.p95
        exclusiveMaxMs = $_.exclusiveGpuMs.max
        inclusiveMedianMs = $_.inclusiveGpuMs.median
        inclusiveMinMs = $_.inclusiveGpuMs.min
        inclusiveP95Ms = $_.inclusiveGpuMs.p95
        inclusiveMaxMs = $_.inclusiveGpuMs.max
        path = $_.path
    }
} | Export-Csv -LiteralPath $outputCsvPath -NoTypeInformation -Encoding UTF8

Write-Host ("RenderDoc median: {0:N3} ms from {1} captures ({2})" -f `
    $summary.matchedActionSumMs.median, $summary.sampleCount, $summary.graphicsAPI)
$markers |
    Where-Object { $_.exclusiveGpuMs.median -gt 0.0 } |
    Select-Object -First 24 `
        name,
        @{ Name = 'medianMs'; Expression = { $_.exclusiveGpuMs.median } },
        @{ Name = 'p95Ms'; Expression = { $_.exclusiveGpuMs.p95 } },
        samples |
    Format-Table -AutoSize

Write-Output $outputJsonPath
Write-Output $outputCsvPath
