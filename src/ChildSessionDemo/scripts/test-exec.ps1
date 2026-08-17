[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$TargetPath,

    [string]$LauncherPath
)

$ErrorActionPreference = 'Stop'

$projectDirectory = Split-Path -Parent $PSScriptRoot
$repositoryRoot = (Resolve-Path (Join-Path $projectDirectory '..\..')).Path
if ([string]::IsNullOrWhiteSpace($LauncherPath)) {
    $LauncherPath = Join-Path $repositoryRoot 'artifacts\child-session-launcher\win-x64\MaaNOP.ChildSessionLauncher.exe'
}

if (-not (Test-Path -LiteralPath $LauncherPath -PathType Leaf)) {
    throw "未找到 Launcher: $LauncherPath。请先运行 scripts\build.ps1。"
}

$resolvedTarget = (Resolve-Path -LiteralPath $TargetPath).Path
if ([System.IO.Path]::GetExtension($resolvedTarget) -ne '.exe') {
    throw "TargetPath 必须指向 .exe: $resolvedTarget"
}

Write-Host "即将在 Child Session 中启动: $resolvedTarget"
Write-Host '测试完成后请关闭预览窗口，以断开并注销 Child Session。'
& $LauncherPath --exec $resolvedTarget
