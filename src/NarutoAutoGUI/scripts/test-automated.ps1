[CmdletBinding()]
param(
    [string]$GuiDll,
    [string]$WorkerDll,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

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
# initialize RDP/COM, load MaaFramework native runtime, or show windows.
dotnet $GuiDll --self-test
if ($LASTEXITCODE -ne 0) { throw "自动自检失败，退出码 $LASTEXITCODE" }

dotnet $WorkerDll --self-test
if ($LASTEXITCODE -ne 0) { throw "Worker 自动自检失败，退出码 $LASTEXITCODE" }

Write-Host "自动自检通过。RDP/UAC/托盘等交互流程仍需按文档手动回归。"
