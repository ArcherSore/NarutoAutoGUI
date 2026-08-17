[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$Runtime = 'win-x64',

    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'

$projectDirectory = Split-Path -Parent $PSScriptRoot
$repositoryRoot = (Resolve-Path (Join-Path $projectDirectory '..\..')).Path
$projectPath = Join-Path $projectDirectory 'MaaNOP.ChildSessionLauncher.csproj'

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot 'artifacts\child-session-launcher\win-x64'
}

dotnet restore $projectPath -r $Runtime
if ($LASTEXITCODE -ne 0) { throw "dotnet restore 失败，退出码 $LASTEXITCODE" }

dotnet build $projectPath -c $Configuration -p:Platform=x64 -r $Runtime --no-restore
if ($LASTEXITCODE -ne 0) { throw "dotnet build 失败，退出码 $LASTEXITCODE" }

dotnet publish $projectPath `
    -c $Configuration `
    -p:Platform=x64 `
    -r $Runtime `
    --self-contained true `
    -o $OutputDirectory `
    --no-restore
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败，退出码 $LASTEXITCODE" }

Write-Host "发布完成: $OutputDirectory"
