[CmdletBinding()]
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$cargo = (Get-Command cargo -ErrorAction SilentlyContinue).Source
if (-not $cargo) {
    $cargo = Join-Path $env:USERPROFILE '.cargo\bin\cargo.exe'
    if (-not (Test-Path -LiteralPath $cargo)) { throw '需要 Rust MSVC 工具链和 Visual Studio C++ Build Tools。' }
    $env:PATH = (Split-Path $cargo) + ';' + $env:PATH
}
$cargoArgs = @('build', '--locked', '--manifest-path', (Join-Path $repositoryRoot 'src\MaaNOP.UpdateEngine\Cargo.toml'))
$profile = 'debug'
if ($Configuration -eq 'Release') { $cargoArgs += '--release'; $profile = 'release' }
Push-Location $repositoryRoot
try {
    & $cargo @cargoArgs | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Rust Engine build failed: $LASTEXITCODE" }
} finally { Pop-Location }
Join-Path $repositoryRoot "src\MaaNOP.UpdateEngine\target\$profile\maanop-update-engine.exe"
