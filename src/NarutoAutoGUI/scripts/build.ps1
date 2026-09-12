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

function Copy-CleanTree {
    param(
        [string]$Source,
        [string]$Destination,
        [string[]]$ExcludedExtensions = @('.pdb', '.bak')
    )
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    $sourcePath = (Resolve-Path -LiteralPath $Source).Path.TrimEnd('\', '/')
    Get-ChildItem -LiteralPath $sourcePath -Recurse -File | Where-Object {
        $_.Extension.ToLowerInvariant() -notin $ExcludedExtensions
    } | ForEach-Object {
        $rel = $_.FullName.Substring($sourcePath.Length + 1)
        $targetFile = Join-Path $Destination $rel
        $targetDir = Split-Path -Parent $targetFile
        if (-not (Test-Path -LiteralPath $targetDir)) {
            New-Item -ItemType Directory -Force -Path $targetDir | Out-Null
        }
        Copy-Item -LiteralPath $_.FullName -Destination $targetFile -Force
    }
}

$projectDirectory = Split-Path -Parent $PSScriptRoot
$repositoryRoot = (Resolve-Path (Join-Path $projectDirectory '..\..')).Path
$projectPath = Join-Path $projectDirectory 'NarutoAutoGUI.csproj'
$workerProjectPath = Join-Path $repositoryRoot 'src\NarutoAutoWorker\NarutoAutoWorker.csproj'
$updaterProjectPath = Join-Path $repositoryRoot 'src\NarutoAutoUpdater\NarutoAutoUpdater.csproj'

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot 'artifacts\NarutoAutoGUI\win-x64'
}

$versionArgs = @()
if ($Version) { $versionArgs += "-p:Version=$Version" }

$restoreArgs = @('-r', $Runtime)
if ($Locked) { $restoreArgs += '--locked-mode' }

dotnet restore $projectPath @restoreArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet restore 失败，退出码 $LASTEXITCODE" }

dotnet restore $workerProjectPath @restoreArgs
if ($LASTEXITCODE -ne 0) { throw "Worker dotnet restore 失败，退出码 $LASTEXITCODE" }

dotnet restore $updaterProjectPath @restoreArgs
if ($LASTEXITCODE -ne 0) { throw "Updater dotnet restore 失败，退出码 $LASTEXITCODE" }

dotnet build $projectPath -c $Configuration -p:Platform=x64 -r $Runtime --no-restore @versionArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet build 失败，退出码 $LASTEXITCODE" }

dotnet build $workerProjectPath -c $Configuration -p:Platform=x64 -r $Runtime --no-restore @versionArgs
if ($LASTEXITCODE -ne 0) { throw "Worker dotnet build 失败，退出码 $LASTEXITCODE" }

# Clean internal staging directories.
$stagingRoot = Join-Path $repositoryRoot 'artifacts\.staging'
$guiPublishDir = Join-Path $stagingRoot 'gui'
$workerPublishDir = Join-Path $stagingRoot 'worker'
$updaterPublishDir = Join-Path $stagingRoot 'updater'
$packageStagingDir = Join-Path $stagingRoot 'package'

if (Test-Path -LiteralPath $stagingRoot) {
    $resolvedStaging = (Resolve-Path -LiteralPath $stagingRoot).Path
    if ($resolvedStaging -ne (Join-Path $repositoryRoot 'artifacts\.staging')) {
        throw 'Unexpected staging directory.'
    }
    Remove-Item -LiteralPath $stagingRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $guiPublishDir | Out-Null
New-Item -ItemType Directory -Force -Path $workerPublishDir | Out-Null
New-Item -ItemType Directory -Force -Path $packageStagingDir | Out-Null

try {
    dotnet publish $projectPath `
        -c $Configuration `
        -p:Platform=x64 `
        -r $Runtime `
        --self-contained true `
        -o $guiPublishDir `
        --no-restore `
        @versionArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败，退出码 $LASTEXITCODE" }

    dotnet publish $workerProjectPath `
        -c $Configuration `
        -p:Platform=x64 `
        -r $Runtime `
        --self-contained true `
        -o $workerPublishDir `
        --no-restore `
        @versionArgs
    if ($LASTEXITCODE -ne 0) { throw "Worker dotnet publish 失败，退出码 $LASTEXITCODE" }

    # The updater is published with the same app-local runtime layout as the GUI, but only
    # its entry files are copied. The shared runtime comes from the GUI publish below.
    dotnet publish $updaterProjectPath -c $Configuration -r $Runtime --self-contained true `
        -p:PublishSingleFile=false -o $updaterPublishDir --no-restore @versionArgs
    if ($LASTEXITCODE -ne 0) { throw "Updater publish 失败，退出码 $LASTEXITCODE" }

    # Assemble distribution package in package staging.
    Copy-CleanTree -Source $guiPublishDir -Destination $packageStagingDir
    Copy-CleanTree -Source $workerPublishDir -Destination (Join-Path $packageStagingDir 'worker')
    foreach ($fileName in @(
        'NarutoAutoUpdater.exe', 'NarutoAutoUpdater.dll', 'NarutoAutoUpdater.deps.json',
        'NarutoAutoUpdater.runtimeconfig.json'
    )) {
        $sourceFile = Join-Path $updaterPublishDir $fileName
        if (-not (Test-Path -LiteralPath $sourceFile -PathType Leaf)) {
            throw "Updater publish 缺少入口文件：$fileName"
        }
        Copy-Item -LiteralPath $sourceFile -Destination $packageStagingDir
    }

    # Deploy assembled package to target OutputDirectory safely.
    $resolvedOutput = (New-Item -ItemType Directory -Force -Path $OutputDirectory).FullName
    $artifactsPath = (Join-Path $repositoryRoot 'artifacts') + [IO.Path]::DirectorySeparatorChar
    if ($resolvedOutput.StartsWith($artifactsPath, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedOutput)) {
        Get-ChildItem -LiteralPath $resolvedOutput -Force | Remove-Item -Recurse -Force
    }
    Copy-CleanTree -Source $packageStagingDir -Destination $resolvedOutput
} finally {
    if (Test-Path -LiteralPath $stagingRoot) {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host "发布完成: $OutputDirectory (GUI + libs/ + fixed Worker runtime)"
