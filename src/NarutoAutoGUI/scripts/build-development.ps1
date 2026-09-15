[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$engine = & (Join-Path $PSScriptRoot 'build-engine.ps1') -Configuration $Configuration

dotnet build (Join-Path $repositoryRoot 'src\NarutoAutoGUI') -c $Configuration -p:Platform=x64 `
    -p:BuildInParallel=false -p:RestoreLockedMode=true
if ($LASTEXITCODE -ne 0) { throw "GUI 构建失败，退出码 $LASTEXITCODE" }

$guiOutput = Join-Path $repositoryRoot "src\NarutoAutoGUI\bin\x64\$Configuration\net10.0-windows\win-x64"
Copy-Item -LiteralPath $engine -Destination $guiOutput -Force
Write-Host "开发更新入口已就绪：$guiOutput"
Write-Host '此脚本不组装 MaaNOP 完整发布包。正式发布集成属于 Updater V2 工单 05。'
