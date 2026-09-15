[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Baseline,
    [Parameter(Mandatory)][string]$MaaNopPayload,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$ProductVersion
)
$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $OutputDirectory) { throw '完整包输出目录必须是新的隔离目录。' }
& (Join-Path $PSScriptRoot 'validate-package.ps1') -PackageDirectory $Baseline
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
Get-ChildItem -LiteralPath $Baseline | Copy-Item -Destination $OutputDirectory -Recurse
# Copy only release payload, never a user's config/logs/debug/cache or admission state.
foreach ($name in @('interface.json', 'resource', 'agent', 'python')) {
    Copy-Item -LiteralPath (Join-Path $MaaNopPayload $name) -Destination $OutputDirectory -Recurse
}
foreach ($name in @('LICENSE', 'README.md', 'THIRD_PARTY_NOTICES.md', 'licenses')) {
    $source = Join-Path $MaaNopPayload $name
    if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination $OutputDirectory -Recurse -Force }
}
$piPath = Join-Path $OutputDirectory 'interface.json'
$pi = Get-Content -LiteralPath $piPath -Raw | ConvertFrom-Json
$pi.version = $ProductVersion
$pi | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $piPath -Encoding utf8NoBOM
Write-Host '已组装本地完整包。打 ZIP 后使用 prepare_local_package 验证生产 check/prepare 契约。'
Write-Host '此操作没有创建 tag、Release，也未修改 MaaNOP 源仓库。'
