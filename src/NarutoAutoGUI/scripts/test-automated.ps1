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

# A failed stage must not hide the others; the first failure's exit code is kept for the caller.
$failedStages = [System.Collections.Generic.List[string]]::new()
$firstExitCode = 0
function Complete-Stage
{
    param([string]$Name)
    if ($LASTEXITCODE -eq 0) { return }
    $failedStages.Add($Name)
    if ($script:firstExitCode -eq 0) { $script:firstExitCode = $LASTEXITCODE }
}

# Running the DLLs through dotnet bypasses the apphost, so the GUI never relaunches itself elevated. The self-tests
# do not connect RDP.
# GUI tests create a local RDP control to check input locking, and start the real Worker,
# which probes the native runtime.
$workerExe = [IO.Path]::ChangeExtension((Resolve-Path -LiteralPath $WorkerDll).Path, '.exe')
foreach ($entry in @(@{ Dll = $GuiDll; Extra = @('--worker-exe', $workerExe) }, @{ Dll = $WorkerDll; Extra = @() })) {
    $entryPath = (Resolve-Path -LiteralPath $entry.Dll).Path
    $extra = $entry.Extra
    Push-Location (Split-Path $entryPath)
    try {
        dotnet $entryPath --self-test @extra
        Complete-Stage (Split-Path $entryPath -Leaf)
    } finally { Pop-Location }
}

$updaterArgs = @('run', '--project', (Join-Path $repositoryRoot 'src\NarutoAutoUpdater.Tests'),
    '-c', $Configuration, '-p:BuildInParallel=false')
if ($NoBuild) { $updaterArgs += '--no-build' }
dotnet @updaterArgs
Complete-Stage 'Updater'

$cargo = (Get-Command cargo -ErrorAction SilentlyContinue).Source
if (-not $cargo) {
    $cargo = Join-Path $env:USERPROFILE '.cargo\bin\cargo.exe'
    $env:PATH = (Split-Path $cargo) + ';' + $env:PATH
}
Push-Location $repositoryRoot
try {
    & $cargo test --locked --manifest-path src/MaaNOP.UpdateEngine/Cargo.toml
    Complete-Stage 'Engine tests'
    & $cargo clippy --locked --manifest-path src/MaaNOP.UpdateEngine/Cargo.toml --all-targets -- -D warnings
    Complete-Stage 'Engine clippy'
} finally { Pop-Location }

if ($failedStages.Count -ne 0) {
    Write-Host "自动验证失败：$($failedStages -join '、')"
    exit $firstExitCode
}
Write-Host "自动验证通过。RDP/UAC/托盘等交互流程仍需按文档手动回归。"
