[CmdletBinding()]
param([Parameter(Mandatory)][string]$PackageDirectory)
$ErrorActionPreference = 'Stop'
$probeRoot = Join-Path ([IO.Path]::GetTempPath()) "maanop-engine-check-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $probeRoot | Out-Null
try {
    Copy-Item -LiteralPath (Join-Path $PackageDirectory 'maanop-update-engine.exe') -Destination $probeRoot
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $probeRoot 'maanop-update-engine.exe'))
    $start.WorkingDirectory = $probeRoot
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($start)
    $request = @{protocolVersion=1; operation='check'; installation=$probeRoot} | ConvertTo-Json -Compress
    $process.StandardInput.WriteLine($request)
    $process.StandardInput.Close()
    if (-not $process.WaitForExit(15000)) { $process.Kill(); throw '独立 Engine check 超时。' }
    $result = $process.StandardOutput.ReadToEnd() | ConvertFrom-Json
    if ($process.ExitCode -ne 1 -or $result.protocolVersion -ne 1 -or $result.code -ne 'invalid_source') {
        throw '独立 Engine 未返回预期的缺失 PI 错误。'
    }
    $process.Dispose()
    Write-Host '  [ok] standalone Rust check without GUI/runtime files'
} finally {
    $resolved = (Resolve-Path -LiteralPath $probeRoot).Path
    if ($resolved -ne [IO.Path]::GetFullPath($probeRoot)) { throw 'Unexpected test directory.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
