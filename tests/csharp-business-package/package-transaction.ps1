#requires -Version 7.0
<#
将真实打包入口复制到仓库 temp 内的人工布局，注入固定的源身份元数据，验证全树打包和
旧 dist 保护。载荷只是文本占位，不调用 exe、不读取游戏或正式编译目录。
仅本夹具用文件存在门禁替代真实 PE/VS 检查；该检查由 windows-runtime-audit 和真实包证据独立覆盖。
#>
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$fixture = Join-Path $repository ('temp/flutter-package-' + [Guid]::NewGuid().ToString('N'))
$modRoot = Join-Path $fixture 'mods/bepinex'
$output = Join-Path $modRoot 'bin/Release'
$runner = Join-Path $fixture 'apps/companion-flutter/build/windows/x64/runner/Release'
$updater = Join-Path $fixture 'native/updater/target/release/mystia-steward-companion-updater.exe'
function Write-Fixture([string]$Path, [string]$Content) {
    if (-not [IO.Path]::GetFullPath($Path).StartsWith($fixture + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw '拒绝写入夹具外。' }
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path))
    [IO.File]::WriteAllText($Path, $Content, [Text.UTF8Encoding]::new($false))
}
$script = Join-Path $modRoot 'tools/package-release.ps1'
Write-Fixture $script ([IO.File]::ReadAllText((Join-Path $repository 'mods/bepinex/tools/package-release.ps1')))
$module = ([Uri](Join-Path $repository 'scripts/package-manifest.mjs')).AbsoluteUri
# 仅替换元数据来源，所有树校验、哈希与打包逻辑仍来自生产实现。
$cli = @"
import { createManifest, validatePackage } from '$module';
const [op,root]=process.argv.slice(2);
const metadata={schemaVersion:1,product:'mystia-steward-companion',version:'1.3.1',sourceCommit:'a'.repeat(40),sourceDigest:'b'.repeat(64),toolchainSha256:'c'.repeat(64),businessProtocolVersion:1,clientProtocolVersion:2};
console.log(JSON.stringify(op==='create'?createManifest(root,metadata):validatePackage(root)));
"@
Write-Fixture (Join-Path $fixture 'scripts/package-manifest.mjs') $cli
Write-Fixture (Join-Path $fixture 'scripts/windows-runtime.mjs') @'
import { existsSync } from 'node:fs';
import path from 'node:path';
for(const name of ['msvcp140.dll','vcruntime140.dll','vcruntime140_1.dll','THIRD-PARTY-NOTICES.txt']) {
  if(!existsSync(path.join(process.argv[3],name)))process.exit(1);
}
'@
foreach ($name in @('mystia-steward-companion.exe','flutter_windows.dll','test_plugin.dll','data/icudtl.dat','data/flutter_assets/example.txt','msvcp140.dll','vcruntime140.dll','vcruntime140_1.dll','THIRD-PARTY-NOTICES.txt','data/app.so','data/flutter_assets/AssetManifest.bin','data/flutter_assets/FontManifest.json','data/flutter_assets/NOTICES.Z')) { Write-Fixture (Join-Path $runner $name) "fixture-$name" }
Write-Fixture $updater 'fixture-updater'
Write-Fixture (Join-Path $output 'MystiaStewardCompanion.BepInEx.dll') 'fixture-mod'
$previous = Join-Path $modRoot 'dist/previous.txt'; Write-Fixture $previous 'previous-complete-package'
$rejected = $false
try { & $script -UpdaterPath $updater | Out-Null } catch { $rejected = $true }
if (-not $rejected -or [IO.File]::ReadAllText($previous) -cne 'previous-complete-package') { throw '缺少业务 DLL 时未保留原 dist。' }
foreach ($name in @('MystiaStewardCompanion.Contracts.dll','MystiaStewardCompanion.Business.dll')) { Write-Fixture (Join-Path $output $name) "fixture-$name" }
# CRT、启动数据及许可逐一在清单生成前遗漏时均应拒绝，并保留原 dist。
foreach ($name in @('msvcp140.dll','vcruntime140.dll','vcruntime140_1.dll','data/app.so','data/icudtl.dat','data/flutter_assets/AssetManifest.bin','data/flutter_assets/FontManifest.json','data/flutter_assets/NOTICES.Z','THIRD-PARTY-NOTICES.txt')) {
    Remove-Item -LiteralPath (Join-Path $runner $name)
    $rejected = $false
    try { & $script -UpdaterPath $updater | Out-Null } catch { $rejected = $true }
    if (-not $rejected -or [IO.File]::ReadAllText($previous) -cne 'previous-complete-package') { throw "遗漏 $name 时未拒绝并保留原 dist。" }
    Write-Fixture (Join-Path $runner $name) "fixture-$name"
    Write-Fixture (Join-Path $runner $name) ''
    $rejected = $false
    try { & $script -UpdaterPath $updater | Out-Null } catch { $rejected = $true }
    if (-not $rejected -or [IO.File]::ReadAllText($previous) -cne 'previous-complete-package') { throw "$name 为零长度时未拒绝并保留原 dist。" }
    Write-Fixture (Join-Path $runner $name) "fixture-$name"
}
# 使用真实打包入口证明增量构建遗留的图片及清单都被拒绝；拒绝时不删输入、不替换旧包。
# 夹具只移动自身的旧目录到隔离区，既不清理正式 Release，也不依赖安装目录的图片数量。
foreach ($name in @('images/food-1.png', 'manifest.json')) {
    $retiredRoot = Join-Path $runner 'data/flutter_assets/assets/game-media'
    $retiredFile = Join-Path $retiredRoot $name
    Write-Fixture $retiredFile 'retired-game-media-fixture'
    $rejection = ''
    try { & $script -UpdaterPath $updater | Out-Null } catch { $rejection = $_.Exception.Message }
    if ($rejection -notlike '*已退役的 assets/game-media*' -or
        [IO.File]::ReadAllText($previous) -cne 'previous-complete-package' -or
        [IO.File]::ReadAllText($retiredFile) -cne 'retired-game-media-fixture') {
        throw "旧游戏图片资源 $name 未拒绝，或拒绝时改动了输入/旧 dist。"
    }
    if (@(Get-ChildItem -LiteralPath $modRoot -Directory | Where-Object Name -Match '^dist\.(staging|backup)-').Count) {
        throw '旧游戏图片拒绝后存在打包事务残留。'
    }
    $quarantine = Join-Path $fixture ('retired-' + [Guid]::NewGuid().ToString('N'))
    foreach ($target in @($retiredRoot, $quarantine)) {
        if (-not [IO.Path]::GetFullPath($target).StartsWith($fixture + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw '拒绝移动夹具外旧资源。'
        }
    }
    Move-Item -LiteralPath $retiredRoot -Destination $quarantine
}
& $script -UpdaterPath $updater | Out-Null
if (Test-Path -LiteralPath $previous) { throw '旧 dist 未成套替换。' }
$package = Join-Path $modRoot 'dist/mystia-steward-companion'
$manifest = Get-Content -LiteralPath (Join-Path $package 'package-manifest.json') -Raw | ConvertFrom-Json
if ($manifest.files.Count -ne 17) { throw '完整清单遗漏插件、运行库、许可或资源。' }
foreach ($entry in $manifest.files) {
    if ((Get-FileHash -LiteralPath (Join-Path $package $entry.path)).Hash.ToLowerInvariant() -cne $entry.sha256) { throw "SHA-256错误：$($entry.path)" }
}
foreach ($archiveName in @('mystia-steward-companion-bepinex.zip','mystia-steward-companion-companion-windows-x64.zip')) {
    $zip = [IO.Compression.ZipFile]::OpenRead((Join-Path $modRoot "dist/$archiveName"))
    try {
        $entries = @($zip.Entries | Where-Object Name)
        $expectedCount = if ($archiveName -like '*bepinex.zip') { 18 } else { 13 }
        if ($entries.Count -ne $expectedCount) { throw "ZIP载荷集合错误：$archiveName" }
    } finally { $zip.Dispose() }
}
if (@(Get-ChildItem -LiteralPath $modRoot -Directory | Where-Object Name -Match '^dist\.(staging|backup)-').Count) { throw '打包事务残留。' }
Write-Output "PASS：缺包/CRT及关键资源遗漏和零长度拒绝、旧游戏图片/清单拒绝且输入与原dist保留、17文件清单、完整Mod ZIP和Flutter独立ZIP。证据：$fixture"
