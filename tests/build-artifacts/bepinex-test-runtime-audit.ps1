#requires -Version 7.0
<# 使用真实锁定分发包在独占目录验证恢复；不加载 DLL、不接入游戏、不改已有缓存。 #>
param([Parameter(Mandatory)][string]$ArchivePath)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$restore = Join-Path $repository 'scripts/restore-bepinex-test-runtime.ps1'
$evidence = Join-Path $repository "temp/runtime-restore-audit-$([Guid]::NewGuid().ToString('N'))"
[IO.Directory]::CreateDirectory($evidence) | Out-Null
$output = Join-Path $evidence 'clean'
& $restore -ArchivePath $ArchivePath -OutputDirectory $output
$dll = Join-Path $output 'SemanticVersioning.dll'
$before = (Get-FileHash -LiteralPath $dll).Hash
& $restore -ArchivePath $ArchivePath -OutputDirectory $output
if ((Get-FileHash -LiteralPath $dll).Hash -ne $before) { throw '复用改变了已确认文件。' }

# 错误分发包必须在提取前失败；缓存损坏必须保留原字节，不能静默覆盖掩盖问题。
$badArchive = Join-Path $evidence 'invalid.zip'
[IO.File]::WriteAllText($badArchive, 'synthetic invalid archive')
$rejected = $false
try { & $restore -ArchivePath $badArchive -OutputDirectory (Join-Path $evidence 'bad-source') }
catch { if ($_.Exception.Message -notlike '*分发包摘要不符*') { throw }; $rejected = $true }
if (-not $rejected) { throw '未拒绝错误分发包。' }
[IO.File]::WriteAllText($dll, 'synthetic invalid cache')
$rejected = $false
try { & $restore -ArchivePath $ArchivePath -OutputDirectory $output }
catch { if ($_.Exception.Message -notlike '*已有运行库与锁不符*') { throw }; $rejected = $true }
if (-not $rejected -or [IO.File]::ReadAllText($dll) -cne 'synthetic invalid cache') {
    throw '错误缓存未被拒绝或原文件被覆盖。'
}
if (@(Get-ChildItem -LiteralPath $output -Force -Filter '*.tmp').Count -ne 0) { throw '存在未清理临时提取文件。' }
@{ result = 'passed'; checks = @('clean-restore', 'verified-reuse', 'reject-bad-archive', 'preserve-invalid-cache', 'temporary-cleanup') } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidence 'result.json') -Encoding utf8
Write-Output "5项真实归档恢复检查通过：$evidence"
