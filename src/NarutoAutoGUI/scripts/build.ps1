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
$projectPath = Join-Path $projectDirectory 'NarutoAutoGUI.csproj'
$workerProjectPath = Join-Path $repositoryRoot 'src\NarutoAutoWorker\NarutoAutoWorker.csproj'

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot 'artifacts\NarutoAutoGUI\win-x64'
}

dotnet restore $projectPath -r $Runtime
if ($LASTEXITCODE -ne 0) { throw "dotnet restore 失败，退出码 $LASTEXITCODE" }

dotnet restore $workerProjectPath -r $Runtime
if ($LASTEXITCODE -ne 0) { throw "Worker dotnet restore 失败，退出码 $LASTEXITCODE" }

dotnet build $projectPath -c $Configuration -p:Platform=x64 -r $Runtime --no-restore
if ($LASTEXITCODE -ne 0) { throw "dotnet build 失败，退出码 $LASTEXITCODE" }

dotnet build $workerProjectPath -c $Configuration -p:Platform=x64 -r $Runtime --no-restore
if ($LASTEXITCODE -ne 0) { throw "Worker dotnet build 失败，退出码 $LASTEXITCODE" }

dotnet publish $projectPath `
    -c $Configuration `
    -p:Platform=x64 `
    -r $Runtime `
    --self-contained true `
    -o $OutputDirectory `
    --no-restore
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败，退出码 $LASTEXITCODE" }

$workerOutputDirectory = Join-Path $OutputDirectory 'worker'
dotnet publish $workerProjectPath `
    -c $Configuration `
    -p:Platform=x64 `
    -r $Runtime `
    --self-contained true `
    -o $workerOutputDirectory `
    --no-restore
if ($LASTEXITCODE -ne 0) { throw "Worker dotnet publish 失败，退出码 $LASTEXITCODE" }

Write-Host "发布完成: $OutputDirectory (GUI + fixed Worker runtime)"
