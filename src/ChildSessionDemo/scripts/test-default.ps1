[CmdletBinding()]
param(
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

Write-Host '即将启动交互式 RDP Child Session 预览并运行 MaaNOP；游戏请在子桌面中手动启动。'
Write-Host '测试完成后请关闭预览窗口，以断开并注销 Child Session。'
& $LauncherPath
