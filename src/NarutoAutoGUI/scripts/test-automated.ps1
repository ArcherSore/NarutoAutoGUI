[CmdletBinding()]
param(
    [string]$PublishedDirectory
)

$ErrorActionPreference = 'Stop'

$projectDirectory = Split-Path -Parent $PSScriptRoot
$repositoryRoot = (Resolve-Path (Join-Path $projectDirectory '..\..')).Path
if ([string]::IsNullOrWhiteSpace($PublishedDirectory)) {
    $PublishedDirectory = Join-Path $repositoryRoot 'artifacts\NarutoAutoGUI\win-x64'
}

$applicationDll = Join-Path $PublishedDirectory 'NarutoAutoGUI.dll'
if (-not (Test-Path -LiteralPath $applicationDll -PathType Leaf)) {
    throw "未找到发布产物: $applicationDll。请先运行 scripts\build.ps1。"
}
$workerDll = Join-Path $PublishedDirectory 'worker\NarutoAutoWorker.dll'
if (-not (Test-Path -LiteralPath $workerDll -PathType Leaf)) {
    throw "未找到 Worker 发布产物: $workerDll。请先运行 scripts\build.ps1。"
}

# Running the DLLs through dotnet bypasses the elevated apphost manifests. The self-tests do not
# initialize RDP/COM, load MaaFramework native runtime, or show windows.
dotnet $applicationDll --self-test
if ($LASTEXITCODE -ne 0) { throw "自动自检失败，退出码 $LASTEXITCODE" }

dotnet $workerDll --self-test
if ($LASTEXITCODE -ne 0) { throw "Worker 自动自检失败，退出码 $LASTEXITCODE" }

Write-Host '自动自检通过。RDP/UAC/托盘等交互流程仍需按文档手动回归。'
