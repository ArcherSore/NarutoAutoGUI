[CmdletBinding()]
param(
    [ValidateSet('Gui', 'Project', 'Support', 'RuntimeControls', 'Worker', 'Updater', 'Engine', 'All')]
    [string]$Suite = 'Gui',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$NoBuild,
    # Runs only the checks whose name contains this text; for iterating on a GUI or Worker self-test.
    [string]$Only,
    # Runs as if the display were at 100% scale, to compare with this machine's real scale.
    [switch]$Dpi96
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$runName = '{0}-{1}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), [Guid]::NewGuid().ToString('N').Substring(0, 8)
$logDirectory = Join-Path $repositoryRoot "artifacts\validation\$runName"
New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
$total = [Diagnostics.Stopwatch]::StartNew()

function Invoke-VerificationStep
{
    param([string]$Name, [string]$Executable, [string[]]$Arguments, [string]$WorkingDirectory = $repositoryRoot)

    $stdout = Join-Path $logDirectory "$Name.stdout.log"
    $stderr = Join-Path $logDirectory "$Name.stderr.log"
    # Every argument is supplied by this script; quoting preserves repository paths containing spaces.
    $argumentLine = ($Arguments | ForEach-Object { '"' + $_ + '"' }) -join ' '
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $start = [Diagnostics.ProcessStartInfo]::new((Get-Command $Executable -ErrorAction Stop).Source)
    $start.Arguments = $argumentLine
    $start.WorkingDirectory = $WorkingDirectory
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    # Set on the child alone: this script can run inside the caller's shell, which must keep its real scale.
    if ($Dpi96) { $start.EnvironmentVariables['__COMPAT_LAYER'] = 'DPIUNAWARE' }
    $outputFile = [IO.File]::Create($stdout)
    $errorFile = [IO.File]::Create($stderr)
    $process = $null
    try {
        $process = [Diagnostics.Process]::Start($start)
        # Drain both streams concurrently, preserving the native bytes and avoiding pipe buffer deadlocks.
        $outputCopy = $process.StandardOutput.BaseStream.CopyToAsync($outputFile)
        $errorCopy = $process.StandardError.BaseStream.CopyToAsync($errorFile)
        $process.WaitForExit()
        [void]$outputCopy.GetAwaiter().GetResult()
        [void]$errorCopy.GetAwaiter().GetResult()
        $code = $process.ExitCode
    } finally {
        if ($process) { $process.Dispose() }
        $outputFile.Dispose()
        $errorFile.Dispose()
    }
    $timer.Stop()
    $seconds = $timer.Elapsed.TotalSeconds.ToString('F2', [Globalization.CultureInfo]::InvariantCulture)
    if ($code -ne 0) {
        Write-Host "FAIL $Name (${seconds}s, exit=$code)"
        $lines = @(Get-Content -LiteralPath $stdout, $stderr -Encoding UTF8 |
            Where-Object { $_.Trim() -and $_ -notmatch '^\s+at ' } | Select-Object -Unique)
        $errors = @($lines | Where-Object { $_ -cmatch '\berror\b|^FAIL |SELF-TEST FAIL|Exception' } |
            Select-Object -First 8)
        @($errors + @($lines | Select-Object -Last 12)) | Select-Object -Unique | ForEach-Object {
            if ($_.Length -gt 500) { Write-Host ($_.Substring(0, 500) + ' ...') } else { Write-Host $_ }
        }
        Write-Host "Logs: $logDirectory"
        exit $code
    }
    Write-Host "PASS $Name (${seconds}s)"
}

function Build-ManagedProject
{
    param([string]$Name, [string]$Project)

    if ($NoBuild) { return }
    # Keep incremental restore enabled: dependency changes and a clean checkout must also work.
    Invoke-VerificationStep "$Name-build" 'dotnet' @(
        'build', (Join-Path $repositoryRoot "src\$Project\$Project.csproj"), '-c', $Configuration,
        '-p:Platform=x64', '-p:BuildInParallel=false', '-p:RestoreLockedMode=true'
    )
}

function Invoke-SelfTest
{
    param([string]$Name, [string]$Project, [string]$Assembly, [string[]]$Selection = @())

    $binary = Join-Path $repositoryRoot "src\$Project\bin\x64\$Configuration\net10.0-windows\win-x64\$Assembly.dll"
    if (-not (Test-Path -LiteralPath $binary -PathType Leaf)) { throw "Missing build output: $binary" }
    if ($Only) { $Selection += @('--only', $Only) }
    Invoke-VerificationStep $Name 'dotnet' (@($binary, '--self-test') + $Selection) (Split-Path $binary)
}

try {
    switch ($Suite) {
        { $_ -in @('Gui', 'Project', 'Support', 'RuntimeControls') } {
            Build-ManagedProject 'gui' 'Gui'
            $selection = @(switch ($Suite) {
                'Project' { '--project-only' }
                'Support' { '--support-only' }
                'RuntimeControls' { '--runtime-controls-only' }
            })
            Invoke-SelfTest "gui-$Suite" 'Gui' 'Bunshin' @($selection)
        }
        'Worker' {
            Build-ManagedProject 'gui' 'Gui'
            Build-ManagedProject 'worker' 'Worker'
            Invoke-SelfTest 'worker' 'Worker' 'Bunshin.Worker'
            # The Worker's other half of the pipe lives in the GUI, so its real connection is checked from there.
            if (-not $Only) {
                $workerExe = Join-Path $repositoryRoot `
                    "src\Worker\bin\x64\$Configuration\net10.0-windows\win-x64\Bunshin.Worker.exe"
                Invoke-SelfTest 'gui-real-worker' 'Gui' 'Bunshin' @(
                    '--only', 'VerifyRealWorker', '--worker-exe', $workerExe
                )
            }
        }
        'Updater' {
            $arguments = @('run', '--project', (Join-Path $repositoryRoot 'src\Updates.Tests'),
                '-c', $Configuration, '-p:BuildInParallel=false')
            if ($NoBuild) { $arguments += '--no-build' }
            Invoke-VerificationStep 'updater' 'dotnet' $arguments
        }
        'Engine' {
            $cargo = (Get-Command cargo -ErrorAction SilentlyContinue).Source
            if (-not $cargo) { $cargo = Join-Path $env:USERPROFILE '.cargo\bin\cargo.exe' }
            $env:PATH = (Split-Path $cargo) + ';' + $env:PATH
            $manifest = Join-Path $repositoryRoot 'src\MaaNOP.UpdateEngine\Cargo.toml'
            Invoke-VerificationStep 'engine-test' $cargo @('test', '--locked', '--manifest-path', $manifest)
            Invoke-VerificationStep 'engine-clippy' $cargo @(
                'clippy', '--locked', '--manifest-path', $manifest, '--all-targets', '--', '-D', 'warnings'
            )
        }
        'All' {
            Build-ManagedProject 'gui' 'Gui'
            Build-ManagedProject 'worker' 'Worker'
            $shell = (Get-Process -Id $PID).Path
            $arguments = @(
                '-NoProfile', '-File', (Join-Path $PSScriptRoot 'test-automated.ps1'),
                '-Configuration', $Configuration
            )
            if ($NoBuild) { $arguments += '-NoBuild' }
            Invoke-VerificationStep 'automated' $shell $arguments
        }
    }
    $seconds = $total.Elapsed.TotalSeconds.ToString('F2', [Globalization.CultureInfo]::InvariantCulture)
    Write-Host "PASS $Suite (${seconds}s total); Logs: $logDirectory"
    exit 0
} catch {
    Write-Host "FAIL $Suite : $($_.Exception.Message)"
    Write-Host "Logs: $logDirectory"
    exit 1
}
