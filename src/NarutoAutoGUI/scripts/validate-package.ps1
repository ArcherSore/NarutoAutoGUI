[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackageDirectory
)

$ErrorActionPreference = 'Stop'

function Assert-PackageFile {
    param([string]$Name, [string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Package validation failed: $Name not found at '$Path'."
    }
    Write-Host "  [ok] $Name"
}

function Assert-PackageDirectory {
    param([string]$Name, [string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        throw "Package validation failed: $Name directory not found at '$Path'."
    }
    $files = @(Get-ChildItem -LiteralPath $Path -Recurse -File -ErrorAction SilentlyContinue)
    if ($files.Count -eq 0) {
        throw "Package validation failed: $Name directory '$Path' is empty."
    }
    Write-Host "  [ok] $Name ($($files.Count) files)"
}

if (-not (Test-Path -LiteralPath $PackageDirectory -PathType Container)) {
    throw "Package directory not found: $PackageDirectory"
}

Write-Host "Validating package layout: $PackageDirectory"

# GUI at package root.
Assert-PackageFile -Name 'NarutoAutoGUI.exe' -Path (Join-Path $PackageDirectory 'NarutoAutoGUI.exe')
Assert-PackageFile -Name 'NarutoAutoGUI.dll' -Path (Join-Path $PackageDirectory 'NarutoAutoGUI.dll')

# Worker under worker/.
$workerDir = Join-Path $PackageDirectory 'worker'
Assert-PackageFile -Name 'worker/NarutoAutoWorker.exe' -Path (Join-Path $workerDir 'NarutoAutoWorker.exe')
Assert-PackageFile -Name 'worker/NarutoAutoWorker.dll' -Path (Join-Path $workerDir 'NarutoAutoWorker.dll')

# Maa.Framework native runtime is copied by its buildTransitive targets into
# worker/runtimes/win-x64/native/. MaaFramework.dll is the core runtime and
# MaaWin32ControlUnit.dll is the Win32 controller the Worker relies on.
$nativeDir = Join-Path $workerDir 'runtimes\win-x64\native'
Assert-PackageDirectory -Name 'worker/runtimes/win-x64/native' -Path $nativeDir
Assert-PackageFile -Name 'worker/runtimes/win-x64/native/MaaFramework.dll' -Path (Join-Path $nativeDir 'MaaFramework.dll')
Assert-PackageFile -Name 'worker/runtimes/win-x64/native/MaaWin32ControlUnit.dll' -Path (Join-Path $nativeDir 'MaaWin32ControlUnit.dll')

Write-Host 'Package validation passed.'
