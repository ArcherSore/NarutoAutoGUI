[CmdletBinding()]
param(
    [ValidateSet('Current', 'RegisterLogon', 'Logon')]
    [string]$Mode = 'Current',
    [switch]$NoBuild
)

# Diagnostic only: no production task, credential, Child Session or application setting is changed.
# RegisterLogon must be run from an already elevated terminal BEFORE signing out.
# The one-shot task runs after the next user logon and removes itself in finally.
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$taskName = 'Bunshin-CredentialImpersonationProbe'
$project = Join-Path $root 'src\CredentialProbe'
$binary = Join-Path $project 'bin\Release\net10.0-windows\win-x64\CredentialProbe.exe'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
try {
    $isAdmin = ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
    $userId = $identity.User.Value
} finally {
    $identity.Dispose()
}

if ($Mode -eq 'RegisterLogon' -and -not $isAdmin) {
    throw 'Run RegisterLogon from an elevated terminal before signing out. This script never requests UAC.'
}
if ($Mode -ne 'Logon' -and -not $NoBuild) {
    & dotnet build $project -c Release --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "Probe build failed: $LASTEXITCODE" }
}
if (-not (Test-Path -LiteralPath $binary)) { throw 'Build the credential probe first.' }

if ($Mode -eq 'RegisterLogon') {
    if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
        throw "Diagnostic task already exists: $taskName. Inspect it before removing or replacing it."
    }
    $powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $arguments = '-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "{0}" -Mode Logon'
    $action = New-ScheduledTaskAction -Execute $powershell -Argument ($arguments -f $PSCommandPath)
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User $userId
    $trigger.Delay = 'PT30S'
    $principal = New-ScheduledTaskPrincipal -UserId $userId -LogonType Interactive -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 2) `
        -MultipleInstances IgnoreNew -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
    Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal `
        -Settings $settings -Description 'One-shot credential impersonation diagnostic; deletes itself after run.' |
        Out-Null
    Write-Output 'Registered. Sign out and sign in; do not accept any UAC prompt after signing in.'
    Write-Output "Results: $root\artifacts\permission-probe (wait at least 45 seconds after logon)."
    return
}

$directory = Join-Path $root ('artifacts\permission-probe\' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $directory -Force | Out-Null
try {
    $result = Join-Path $directory 'result.json'
    $start = [Diagnostics.ProcessStartInfo]::new($binary)
    $start.Arguments = '"' + $result + '"'
    $start.WorkingDirectory = $root
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(60000)) {
            $process.Kill()
            throw 'Probe exceeded 60 seconds; temporary test credential cleanup may be incomplete.'
        }
        [IO.File]::WriteAllText((Join-Path $directory 'stdout.log'), $stdout.GetAwaiter().GetResult())
        [IO.File]::WriteAllText((Join-Path $directory 'stderr.log'), $stderr.GetAwaiter().GetResult())
        if ($process.ExitCode -ne 0) { throw "Probe failed: exit=$($process.ExitCode)" }
    } finally {
        $process.Dispose()
    }
    Get-Content -LiteralPath $result
    Write-Output "Result: $result"
} finally {
    if ($Mode -eq 'Logon') {
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
    }
}
