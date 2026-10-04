[CmdletBinding()]
param([string]$PackageDirectory, [string]$OutputDirectory)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
if (-not $PackageDirectory) {
    $PackageDirectory = Join-Path $repositoryRoot 'artifacts\NarutoAutoGUI\win-x64'
}
$worker = Join-Path (Resolve-Path -LiteralPath $PackageDirectory).Path 'worker\NarutoAutoWorker.exe'
if (-not (Test-Path -LiteralPath $worker -PathType Leaf)) { throw "未找到打包后的 Worker: $worker" }
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repositoryRoot "artifacts\framework-diagnostics-$([Guid]::NewGuid().ToString('N'))"
}
$output = [IO.Path]::GetFullPath($OutputDirectory)

# Use an unrelated working directory with Unicode/spaces to catch relative LogDir regressions.
New-Item -ItemType Directory -Path (Join-Path $output '独立 工作目录') -Force | Out-Null
Push-Location (Join-Path $output '独立 工作目录')
try {
    foreach ($scenario in @('defaults', 'disabled', 'draw-only', 'debug-only', 'all')) {
        $scenarioRoot = Join-Path $output "框架 图片 $scenario"
        $stdout = Join-Path $output "$scenario.stdout.log"
        $stderr = Join-Path $output "$scenario.stderr.log"
        $process = Start-Process -FilePath $worker -WindowStyle Hidden -Wait -PassThru `
            -ArgumentList @('--framework-diagnostics-self-test', "`"$scenarioRoot`"", $scenario) `
            -RedirectStandardOutput $stdout -RedirectStandardError $stderr
        if ($process.ExitCode -ne 0) {
            Get-Content -LiteralPath $stdout | Write-Host
            Get-Content -LiteralPath $stderr | Write-Host
            throw "打包后的框架诊断测试失败：$scenario，退出码 $($process.ExitCode)"
        }
        Select-String -LiteralPath $stdout -Pattern '^NATIVE FRAMEWORK TEST PASS:' |
            ForEach-Object { Write-Host $_.Line }
    }
} finally { Pop-Location }
Write-Host "打包后的框架诊断测试通过，日志与图片保留于: $output"
