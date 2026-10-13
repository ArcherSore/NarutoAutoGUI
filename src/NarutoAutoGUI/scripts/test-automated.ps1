[CmdletBinding()]
param(
    [string]$GuiDll,
    [string]$WorkerDll,
    [string]$Configuration = 'Release',
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)

$projectDirectory = Split-Path -Parent $PSScriptRoot
$repositoryRoot = (Resolve-Path (Join-Path $projectDirectory '..\..')).Path

if ([string]::IsNullOrWhiteSpace($GuiDll)) {
    $guiBin = Join-Path $repositoryRoot "src\NarutoAutoGUI\bin"
    $candidates = @(
        (Join-Path $guiBin "x64\$Configuration\net10.0-windows\win-x64\NarutoAutoGUI.dll"),
        (Join-Path $guiBin "$Configuration\net10.0-windows\win-x64\NarutoAutoGUI.dll")
    )
    $GuiDll = $candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    if (-not $GuiDll) {
        throw "未找到 GUI 构建产物。请先运行 build.ps1 或 dotnet build。"
    }
}

if ([string]::IsNullOrWhiteSpace($WorkerDll)) {
    $workerBin = Join-Path $repositoryRoot "src\NarutoAutoWorker\bin"
    $candidates = @(
        (Join-Path $workerBin "x64\$Configuration\net10.0-windows\win-x64\NarutoAutoWorker.dll"),
        (Join-Path $workerBin "$Configuration\net10.0-windows\win-x64\NarutoAutoWorker.dll")
    )
    $WorkerDll = $candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    if (-not $WorkerDll) {
        throw "未找到 Worker 构建产物。请先运行 build.ps1 或 dotnet build。"
    }
}

if (-not (Test-Path -LiteralPath $GuiDll -PathType Leaf)) {
    throw "未找到 GUI DLL: $GuiDll"
}
if (-not (Test-Path -LiteralPath $WorkerDll -PathType Leaf)) {
    throw "未找到 Worker DLL: $WorkerDll"
}

# Running the DLLs through dotnet bypasses the elevated apphost manifests. The self-tests do not
# initialize RDP/COM or load MaaFramework native runtime. GUI layout tests use offscreen windows.
foreach ($entry in @($GuiDll, $WorkerDll)) {
    $entryPath = (Resolve-Path -LiteralPath $entry).Path
    Push-Location (Split-Path $entryPath)
    try {
        dotnet $entryPath --self-test
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    } finally { Pop-Location }
}

Write-Host "自动自检通过。RDP/UAC/托盘等交互流程仍需按文档手动回归。"

$updaterArgs = @('run', '--project', (Join-Path $repositoryRoot 'src\NarutoAutoUpdater.Tests'),
    '-c', $Configuration, '-p:BuildInParallel=false')
if ($NoBuild) { $updaterArgs += '--no-build' }
dotnet @updaterArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$cargo = (Get-Command cargo -ErrorAction SilentlyContinue).Source
if (-not $cargo) {
    $cargo = Join-Path $env:USERPROFILE '.cargo\bin\cargo.exe'
    $env:PATH = (Split-Path $cargo) + ';' + $env:PATH
}
Push-Location $repositoryRoot
try {
    & $cargo test --locked --manifest-path src/MaaNOP.UpdateEngine/Cargo.toml
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & $cargo clippy --locked --manifest-path src/MaaNOP.UpdateEngine/Cargo.toml --all-targets -- -D warnings
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
} finally { Pop-Location }
