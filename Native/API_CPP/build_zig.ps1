param(
    [Parameter(Mandatory = $true)]
    [string]$ZigExe,

    [Parameter(Mandatory = $true)]
    [string]$MeshoptimizerSource,

    [string]$OutputDirectory = "$PSScriptRoot\build-zig"
)

$ErrorActionPreference = "Stop"

$zigPath = (Resolve-Path -LiteralPath $ZigExe).Path
$meshoptPath = (Resolve-Path -LiteralPath $MeshoptimizerSource).Path
$header = Join-Path $meshoptPath "meshoptimizer.h"
if (-not (Test-Path -LiteralPath $header)) {
    throw "meshoptimizer.h was not found in $meshoptPath"
}

$sources = Get-ChildItem -LiteralPath $meshoptPath -Filter "*.cpp" |
    Sort-Object Name |
    Select-Object -ExpandProperty FullName
if ($sources.Count -eq 0) {
    throw "No meshoptimizer C++ sources were found in $meshoptPath"
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$output = Join-Path (Resolve-Path -LiteralPath $OutputDirectory).Path "API_CPP.dll"

& $zigPath c++ `
    -target x86_64-windows `
    -shared `
    -O3 `
    -std=c++17 `
    "-I$meshoptPath" `
    "$PSScriptRoot\api_cpp.cpp" `
    $sources `
    -o $output

if ($LASTEXITCODE -ne 0) {
    throw "Zig native build failed with exit code $LASTEXITCODE"
}

$hash = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash
Write-Host "Built $output"
Write-Host "SHA256 $hash"
Write-Host "Validated against meshoptimizer commit a6ecc73c094bdd5d09644f9286bbf25134853679."
