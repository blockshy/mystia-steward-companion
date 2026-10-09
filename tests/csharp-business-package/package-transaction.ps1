#requires -Version 7.0
# 使用真实 PowerShell 打包脚本在工作区临时目录验证缺包拒绝和成套发布，不接触游戏。
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$fixture = Join-Path $repository ('temp/csharp-package-' + [Guid]::NewGuid().ToString('N'))
$modRoot = Join-Path $fixture 'mods/bepinex'
$output = Join-Path $modRoot 'bin/Release'
$native = Join-Path $fixture 'apps/companion/src-tauri/target/release'
foreach ($path in @($output, $native, (Join-Path $modRoot 'tools'))) { [void][IO.Directory]::CreateDirectory($path) }
$script = Join-Path $modRoot 'tools/package-release.ps1'
Copy-Item -LiteralPath (Join-Path $repository 'mods/bepinex/tools/package-release.ps1') -Destination $script
[IO.File]::WriteAllText((Join-Path $output 'MystiaStewardCompanion.BepInEx.dll'), 'fixture-mod')
[IO.File]::WriteAllText((Join-Path $native 'mystia-steward-companion.exe'), 'fixture-client')
[IO.File]::WriteAllText((Join-Path $native 'mystia-steward-companion-updater.exe'), 'fixture-updater')
[void][IO.Directory]::CreateDirectory((Join-Path $modRoot 'dist'))
$old = Join-Path $modRoot 'dist/previous.txt'
[IO.File]::WriteAllText($old, 'previous-complete-package')
$rejected = $false
try { & $script | Out-Null } catch { $rejected = $_.Exception.Message -like '*business DLL*' }
if (!$rejected -or [IO.File]::ReadAllText($old) -ne 'previous-complete-package') { throw '缺少业务 DLL 时未保留原 dist。' }
foreach ($name in @('MystiaStewardCompanion.Contracts.dll', 'MystiaStewardCompanion.Business.dll')) {
    [IO.File]::WriteAllText((Join-Path $output $name), "fixture-$name")
}
& $script | Out-Null
if (Test-Path -LiteralPath $old) { throw '旧 dist 未完整替换。' }
$package = Join-Path $modRoot 'dist/mystia-steward-companion'
$manifest = Get-Content -LiteralPath (Join-Path $package 'business-bundle.sha256')
if ($manifest.Count -ne 5) { throw '成套清单应包含五个组件。' }
foreach ($line in $manifest) {
    $actual = (Get-FileHash -LiteralPath (Join-Path $package $line.Substring(66)) -Algorithm SHA256).Hash
    if ($actual -ine $line.Substring(0, 64)) { throw '打包摘要与原文件不一致。' }
}
$archive = [IO.Compression.ZipFile]::OpenRead((Join-Path $modRoot 'dist/mystia-steward-companion-bepinex.zip'))
try {
    $files = @($archive.Entries | Where-Object { $_.Name })
    if ($files.Count -ne 6) { throw 'ZIP应只含五个组件和一份摘要。' }
} finally { $archive.Dispose() }
if (@(Get-ChildItem -LiteralPath $modRoot -Directory | Where-Object { $_.Name -match '^dist\.(staging|backup)-' }).Count) {
    throw '事务完成后仍有暂存/回退目录。'
}
Write-Output "PASS: 缺包拒绝并保留原件、完整事务替换、五组件SHA-256、六文件ZIP、事务目录清理。证据：$fixture"
