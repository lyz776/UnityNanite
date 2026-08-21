[CmdletBinding(DefaultParameterSetName = 'Launch')]
param(
    [Parameter(Mandatory = $true, ParameterSetName = 'Launch')]
    [string] $Executable,

    [Parameter(ParameterSetName = 'Launch')]
    [string] $PlayerArguments = '',

    [Parameter(ParameterSetName = 'Launch')]
    [string] $WorkingDirectory,

    [Parameter(Mandatory = $true, ParameterSetName = 'Connect')]
    [uint32] $TargetIdent,

    [Parameter(Mandatory = $true)]
    [string] $CaptureTemplate,

    [double] $WarmupSeconds = 5.0,

    [Nullable[int]] $CaptureFrame,

    [int] $TimeoutSeconds = 90,

    [string[]] $MarkerPrefixes = @('RealtimeGI/'),

    [switch] $AllowVSync,

    [switch] $SkipExport,

    [string] $RenderDocDirectory = 'C:\Program Files\RenderDoc'
)

$ErrorActionPreference = 'Stop'

$qrenderdoc = Join-Path $RenderDocDirectory 'qrenderdoc.exe'
$captureScript = Join-Path $PSScriptRoot 'CaptureRenderDocGpuFrame.py'
$exportScript = Join-Path $PSScriptRoot 'Export-RenderDocGpuTimings.ps1'
if (-not (Test-Path -LiteralPath $qrenderdoc -PathType Leaf)) {
    throw "qrenderdoc.exe was not found at '$qrenderdoc'."
}
if (-not (Test-Path -LiteralPath $captureScript -PathType Leaf)) {
    throw "Capture script was not found at '$captureScript'."
}
if ($TimeoutSeconds -le 0) {
    throw 'TimeoutSeconds must be positive.'
}

$captureTemplatePath = [IO.Path]::GetFullPath($CaptureTemplate)
$captureParent = Split-Path -Parent $captureTemplatePath
if ($captureParent) {
    New-Item -ItemType Directory -Path $captureParent -Force | Out-Null
}
$captureResultPath = $captureTemplatePath + '.capture-result.json'
if (Test-Path -LiteralPath $captureResultPath -PathType Leaf) {
    Remove-Item -LiteralPath $captureResultPath -Force
}

if ($PSCmdlet.ParameterSetName -eq 'Launch') {
    $executablePath = (Resolve-Path -LiteralPath $Executable).Path
    if ([string]::IsNullOrWhiteSpace($WorkingDirectory)) {
        $WorkingDirectory = Split-Path -Parent $executablePath
    }
    $workingDirectoryPath = (Resolve-Path -LiteralPath $WorkingDirectory).Path
}

$environmentNames = @(
    'GI_RDC_EXECUTABLE',
    'GI_RDC_ARGUMENTS',
    'GI_RDC_WORKING_DIRECTORY',
    'GI_RDC_TARGET_IDENT',
    'GI_RDC_CAPTURE_TEMPLATE',
    'GI_RDC_CAPTURE_RESULT',
    'GI_RDC_WARMUP_SECONDS',
    'GI_RDC_FRAME',
    'GI_RDC_TIMEOUT_SECONDS',
    'GI_RDC_ALLOW_VSYNC'
)
$savedEnvironment = @{}
foreach ($name in $environmentNames) {
    $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

try {
    if ($PSCmdlet.ParameterSetName -eq 'Launch') {
        $env:GI_RDC_EXECUTABLE = $executablePath
        $env:GI_RDC_ARGUMENTS = $PlayerArguments
        $env:GI_RDC_WORKING_DIRECTORY = $workingDirectoryPath
        $env:GI_RDC_TARGET_IDENT = ''
    }
    else {
        $env:GI_RDC_EXECUTABLE = ''
        $env:GI_RDC_ARGUMENTS = ''
        $env:GI_RDC_WORKING_DIRECTORY = ''
        $env:GI_RDC_TARGET_IDENT = [string] $TargetIdent
    }
    $env:GI_RDC_CAPTURE_TEMPLATE = $captureTemplatePath
    $env:GI_RDC_CAPTURE_RESULT = $captureResultPath
    $env:GI_RDC_WARMUP_SECONDS = [string]::Format(
        [Globalization.CultureInfo]::InvariantCulture,
        '{0}',
        $WarmupSeconds)
    $env:GI_RDC_FRAME = if ($null -eq $CaptureFrame) { '' } else { [string] $CaptureFrame }
    $env:GI_RDC_TIMEOUT_SECONDS = [string] $TimeoutSeconds
    $env:GI_RDC_ALLOW_VSYNC = if ($AllowVSync) { '1' } else { '0' }

    $process = Start-Process `
        -FilePath $qrenderdoc `
        -ArgumentList @('--python', ('"{0}"' -f $captureScript)) `
        -PassThru `
        -Wait
    if ($process.ExitCode -ne 0) {
        if (Test-Path -LiteralPath $captureResultPath -PathType Leaf) {
            $failure = Get-Content -LiteralPath $captureResultPath -Raw | ConvertFrom-Json
            throw "RenderDoc capture failed: $($failure.error)"
        }
        throw "qrenderdoc capture failed with exit code $($process.ExitCode)."
    }
}
finally {
    foreach ($name in $environmentNames) {
        [Environment]::SetEnvironmentVariable(
            $name,
            $savedEnvironment[$name],
            'Process')
    }
}

if (-not (Test-Path -LiteralPath $captureResultPath -PathType Leaf)) {
    throw "qrenderdoc exited without creating '$captureResultPath'."
}
$captureResult = Get-Content -LiteralPath $captureResultPath -Raw | ConvertFrom-Json
if ($captureResult.error) {
    throw "RenderDoc capture failed: $($captureResult.error)"
}
if (-not (Test-Path -LiteralPath $captureResult.capture -PathType Leaf)) {
    throw "Captured file was not found at '$($captureResult.capture)'."
}

Write-Host ("Captured {0} frame {1}: {2}" -f `
    $captureResult.graphicsAPI, $captureResult.frameNumber, $captureResult.capture)

if (-not $SkipExport) {
    $timingJson = [IO.Path]::ChangeExtension($captureResult.capture, '.gpu.json')
    $timingCsv = [IO.Path]::ChangeExtension($captureResult.capture, '.gpu.csv')
    & $exportScript `
        -Capture $captureResult.capture `
        -OutputJson $timingJson `
        -OutputCsv $timingCsv `
        -MarkerPrefixes $MarkerPrefixes `
        -RenderDocDirectory $RenderDocDirectory
}

Write-Output $captureResultPath
