[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$Runtime = 'win-x64',

    [string]$OutputDirectory,

    [string]$Version,

    [switch]$Locked
)

$ErrorActionPreference = 'Stop'

$projectDirectory = Split-Path -Parent $PSScriptRoot
$repositoryRoot = (Resolve-Path (Join-Path $projectDirectory '..\..')).Path
$projectPath = Join-Path $projectDirectory 'NarutoAutoGUI.csproj'
$workerProjectPath = Join-Path $repositoryRoot 'src\NarutoAutoWorker\NarutoAutoWorker.csproj'

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot 'artifacts\NarutoAutoGUI\win-x64'
}

# Release version forwarded to MSBuild as -p:Version; overrides the <Version> in each .csproj
# without editing project files. The SDK derives AssemblyVersion/FileVersion/PackageVersion
# from this single property, including prerelease suffixes (e.g. 0.1.0-rc.1).
$versionArgs = @()
if ($Version) { $versionArgs += "-p:Version=$Version" }

# Restore against the committed packages.lock.json (RestorePackagesWithLockFile is enabled in
# Directory.Build.props). Local builds omit -Locked and stay unconstrained.
$restoreArgs = @('-r', $Runtime)
if ($Locked) { $restoreArgs += '--locked-mode' }

dotnet restore $projectPath @restoreArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet restore 失败，退出码 $LASTEXITCODE" }

dotnet restore $workerProjectPath @restoreArgs
if ($LASTEXITCODE -ne 0) { throw "Worker dotnet restore 失败，退出码 $LASTEXITCODE" }

dotnet build $projectPath -c $Configuration -p:Platform=x64 -r $Runtime --no-restore @versionArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet build 失败，退出码 $LASTEXITCODE" }

dotnet build $workerProjectPath -c $Configuration -p:Platform=x64 -r $Runtime --no-restore @versionArgs
if ($LASTEXITCODE -ne 0) { throw "Worker dotnet build 失败，退出码 $LASTEXITCODE" }

dotnet publish $projectPath `
    -c $Configuration `
    -p:Platform=x64 `
    -r $Runtime `
    --self-contained true `
    -o $OutputDirectory `
    --no-restore `
    @versionArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败，退出码 $LASTEXITCODE" }

$workerOutputDirectory = Join-Path $OutputDirectory 'worker'
dotnet publish $workerProjectPath `
    -c $Configuration `
    -p:Platform=x64 `
    -r $Runtime `
    --self-contained true `
    -o $workerOutputDirectory `
    --no-restore `
    @versionArgs
if ($LASTEXITCODE -ne 0) { throw "Worker dotnet publish 失败，退出码 $LASTEXITCODE" }

Write-Host "发布完成: $OutputDirectory (GUI + fixed Worker runtime)"
