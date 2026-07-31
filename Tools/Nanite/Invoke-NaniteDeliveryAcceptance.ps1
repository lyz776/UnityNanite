param(
    [string] $ProjectRoot = "D:\UnityNanite",
    [string] $UnityExe = "C:\Program Files\Unity\Hub\Editor\6000.3.10f1\Editor\Unity.exe",
    [int] $Width = 1280,
    [int] $Height = 720,
    [int] $HybridMaxEdge = 2,
    [switch] $SkipBake,
    [switch] $SkipBuild
)

$ErrorActionPreference = "Stop"

function Assert-PathInsideProject([string] $Path, [string] $Description) {
    $root = [IO.Path]::GetFullPath($ProjectRoot).TrimEnd('\') + '\'
    $resolved = [IO.Path]::GetFullPath($Path)
    if (-not $resolved.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Description is outside the project: $resolved"
    }
    return $resolved
}

function Invoke-UnityStep([string] $Name, [string] $Method, [string] $LogPath) {
    Write-Host "[$Name] $Method"
    $arguments = @(
        '-batchmode', '-nographics', '-quit',
        '-projectPath', $ProjectRoot,
        '-executeMethod', $Method,
        '-logFile', $LogPath
    )
    $process = Start-Process -FilePath $UnityExe -ArgumentList $arguments -PassThru -Wait -WindowStyle Hidden
    if ($process.ExitCode -ne 0) {
        $tail = if (Test-Path -LiteralPath $LogPath) {
            (Get-Content -LiteralPath $LogPath -Tail 80) -join [Environment]::NewLine
        } else { '<log not created>' }
        throw "$Name failed with exit code $($process.ExitCode).`n$tail"
    }
}

function Invoke-PlayerStep(
    [string] $Name,
    [string] $Executable,
    [string[]] $ModeArguments,
    [string] $CapturePath,
    [string] $LogPath) {
    New-Item -ItemType Directory -Force -Path $CapturePath | Out-Null
    $arguments = @(
        '-force-d3d12',
        '-nanite-lod-single-smoke',
        '-nanite-smoke-width', $Width,
        '-nanite-smoke-height', $Height,
        '-nanite-smoke-output', $CapturePath,
        '-screen-fullscreen', '0',
        '-screen-width', $Width,
        '-screen-height', $Height,
        '-logFile', $LogPath
    ) + $ModeArguments
    Write-Host "[$Name] $Executable"
    $process = Start-Process -FilePath $Executable -ArgumentList $arguments -PassThru -Wait -WindowStyle Hidden
    if ($process.ExitCode -ne 0) {
        throw "$Name failed with exit code $($process.ExitCode). See $LogPath"
    }
    $log = Get-Content -LiteralPath $LogPath -Raw
    if ($log -notmatch '\[Nanite\]\[Portability\] Runtime smoke completed; cameraRenders=180\.') {
        throw "$Name did not reach the 180-render completion contract. See $LogPath"
    }
    $fatalPattern = '(?im)(invalid kernel|kernel[^\r\n]*not found|missing[^\r\n]*(resource|buffer|texture)|resource[^\r\n]*not bound|page[^\r\n]*(error|failed)|device removed|d3d12[^\r\n]*error)'
    if ($log -match $fatalPattern) {
        throw "$Name logged a fatal GPU/Page contract failure: $($Matches[0])"
    }
    $frames = @(Get-ChildItem -LiteralPath $CapturePath -Filter 'frame-*.png')
    if ($frames.Count -ne 52) {
        throw "$Name produced $($frames.Count) captures; expected 52."
    }
}

$ProjectRoot = [IO.Path]::GetFullPath($ProjectRoot).TrimEnd('\')
if (-not (Test-Path -LiteralPath $UnityExe -PathType Leaf)) { throw "Unity executable not found: $UnityExe" }
if (-not (Test-Path -LiteralPath $ProjectRoot -PathType Container)) { throw "Project not found: $ProjectRoot" }

$lockPath = Join-Path $ProjectRoot 'Temp\UnityLockfile'
if (Test-Path -LiteralPath $lockPath) {
    throw "The project is open in Unity. Close/release the Editor before delivery acceptance: $lockPath"
}

$stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
$logDirectory = Assert-PathInsideProject (Join-Path $ProjectRoot 'Logs') 'Log directory'
$validationRoot = Assert-PathInsideProject (Join-Path $ProjectRoot "Validation\delivery-$stamp") 'Validation directory'
New-Item -ItemType Directory -Force -Path $logDirectory,$validationRoot | Out-Null

if (-not $SkipBake) {
    $bakeLog = Join-Path $logDirectory "delivery-$stamp-bake.log"
    Invoke-UnityStep 'Bake' 'Nanite.Editor.NaniteSmokeAutomation.BakeToyota' $bakeLog
    if ((Get-Content -LiteralPath $bakeLog -Raw) -notmatch '\[Nanite\] Bake 完成') {
        throw "Bake exited successfully but did not emit its completion contract. See $bakeLog"
    }
}

$lodAuditLog = Join-Path $logDirectory "delivery-$stamp-lod-audit.log"
$hierarchyAuditLog = Join-Path $logDirectory "delivery-$stamp-hierarchy-audit.log"
Invoke-UnityStep 'LOD audit' 'Nanite.Editor.NaniteSmokeAutomation.AuditToyota' $lodAuditLog
Invoke-UnityStep 'Hierarchy audit' 'Nanite.Editor.NaniteHierarchyDistanceAuditMenu.AuditToyotaBatch' $hierarchyAuditLog

$lodAudit = Get-Content -LiteralPath $lodAuditLog -Raw
if ($lodAudit -notmatch '\[Nanite\]\[LOD Audit\][^\r\n]*pages=\d+[^\r\n]*maxMip=\d+' -or
    $lodAudit -notmatch 'max finite self/model radius=') {
    throw "LOD audit did not emit its Page/mip and finite-error contracts. See $lodAuditLog"
}
$hierarchyAudit = Get-Content -LiteralPath $hierarchyAuditLog -Raw
$requiredStructuralContract = 'invalidRanges=0, nonReducing=0[^\r\n]*componentGrowth=0, terminalComponentGrowth=0, componentLoss=0, uvStretchOutlier=0'
if ($hierarchyAudit -notmatch $requiredStructuralContract) {
    throw "Hierarchy audit did not prove the zero-fatal structural contract. See $hierarchyAuditLog"
}
$monotonicResults = [regex]::Matches($hierarchyAudit, 'monotonic regressions:\s*(\d+)')
if ($monotonicResults.Count -lt 5 -or @($monotonicResults | Where-Object { $_.Groups[1].Value -ne '0' }).Count -ne 0) {
    throw "Hierarchy audit did not prove at least five zero-regression directional sweeps. See $hierarchyAuditLog"
}

if (-not $SkipBuild) {
    $buildDirectory = Join-Path $ProjectRoot 'Builds\NanitePortabilitySmoke'
    if (Test-Path -LiteralPath $buildDirectory) {
        $buildDirectory = Assert-PathInsideProject $buildDirectory 'Build directory'
        $backupDirectory = Assert-PathInsideProject (
            Join-Path $ProjectRoot "Builds\NanitePortabilitySmoke_pre_delivery_$stamp") 'Build backup'
        Move-Item -LiteralPath $buildDirectory -Destination $backupDirectory
        Write-Host "Previous build moved to $backupDirectory"
    }
    Invoke-UnityStep 'Build' 'Nanite.Editor.NaniteSmokeAutomation.BuildSmokePlayer' (
        Join-Path $logDirectory "delivery-$stamp-build.log")
}

$player = Join-Path $ProjectRoot 'Builds\NanitePortabilitySmoke\UnityNanitePortabilitySmoke.exe'
if (-not (Test-Path -LiteralPath $player -PathType Leaf)) { throw "Smoke Player not found: $player" }

$hardwareCapture = Join-Path $validationRoot 'hardware'
$hybridCapture = Join-Path $validationRoot 'hybrid2'
$hardwareLog = Join-Path $logDirectory "delivery-$stamp-hardware.log"
$hybridLog = Join-Path $logDirectory "delivery-$stamp-hybrid2.log"
Invoke-PlayerStep 'HardwareOnly' $player @('-nanite-force-hardware') $hardwareCapture $hardwareLog
Invoke-PlayerStep 'Hybrid2' $player @('-nanite-force-hybrid', '-nanite-hybrid-max-edge', $HybridMaxEdge) $hybridCapture $hybridLog

$analyzer = Join-Path $ProjectRoot 'Tools\Nanite\Analyze-NaniteDolly.ps1'
$comparisonCsv = Join-Path $validationRoot 'hardware-vs-hybrid.csv'
& $analyzer -NaniteCapture $hybridCapture -RasterCapture $hardwareCapture -OutputCsv $comparisonCsv -ChannelThreshold 8
if (-not $?) { throw "Frame analysis failed. See $comparisonCsv" }

Write-Host "Delivery acceptance completed."
Write-Host "Validation: $validationRoot"
Write-Host "Comparison: $comparisonCsv"
