[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$engineRoot = Join-Path $repositoryRoot 'src\MaaNOP.UpdateEngine'
$cargoCommand = Get-Command cargo -ErrorAction SilentlyContinue
if ($cargoCommand) {
    $cargo = $cargoCommand.Source
} else {
    $cargo = Join-Path $env:USERPROFILE '.cargo\bin\cargo.exe'
    if (-not (Test-Path -LiteralPath $cargo -PathType Leaf)) {
        throw '开发构建需要 Rust MSVC 工具链和 Visual Studio C++ build tools。'
    }
    $env:PATH = (Split-Path -Parent $cargo) + [IO.Path]::PathSeparator + $env:PATH
}

$cargoArgs = @('build', '--locked', '--manifest-path', (Join-Path $engineRoot 'Cargo.toml'))
$profile = 'debug'
if ($Configuration -eq 'Release') {
    $cargoArgs += '--release'
    $profile = 'release'
}
& $cargo @cargoArgs
if ($LASTEXITCODE -ne 0) { throw "Rust Engine 构建失败，退出码 $LASTEXITCODE" }

dotnet build (Join-Path $repositoryRoot 'src\NarutoAutoGUI') -c $Configuration -p:Platform=x64 `
    -p:BuildInParallel=false -p:RestoreLockedMode=true
if ($LASTEXITCODE -ne 0) { throw "GUI 构建失败，退出码 $LASTEXITCODE" }

$guiOutput = Join-Path $repositoryRoot "src\NarutoAutoGUI\bin\x64\$Configuration\net10.0-windows\win-x64"
Copy-Item -LiteralPath (Join-Path $engineRoot "target\$profile\maanop-update-engine.exe") -Destination $guiOutput -Force
Write-Host "开发检查入口已就绪：$guiOutput"
Write-Host '此脚本不组装 MaaNOP 完整发布包。正式发布集成属于 Updater V2 工单 05。'
