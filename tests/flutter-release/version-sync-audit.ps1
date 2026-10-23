#requires -Version 7.0
# 仅在仓库 temp 的人工五文件布局中执行真实版本同步脚本，不修改产品版本或创建Git提交。
$ErrorActionPreference='Stop'
$repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$fixture=Join-Path $repository ('temp/flutter-version-'+[Guid]::NewGuid().ToString('N'))
$files=@('package.json','apps/companion-flutter/pubspec.yaml','native/updater/Cargo.toml','native/updater/Cargo.lock','mods/bepinex/src/Plugin/MystiaStewardCompanionPlugin.cs')
foreach($file in $files + @('mods/bepinex/tools/set-version.ps1')) {
    $destination=Join-Path $fixture $file
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
    Copy-Item -LiteralPath (Join-Path $repository $file) -Destination $destination
}
$script=Join-Path $fixture 'mods/bepinex/tools/set-version.ps1'
$before=@($files|ForEach-Object{(Get-FileHash -LiteralPath (Join-Path $fixture $_)).Hash}) -join ':'
$rejected=$false
try { & $script -Version 1.3.2 -AndroidBuildNumber 1003002 | Out-Null } catch { $rejected=$true }
$after=@($files|ForEach-Object{(Get-FileHash -LiteralPath (Join-Path $fixture $_)).Hash}) -join ':'
if(-not $rejected -or $before -cne $after){throw '非递增构建号未在写入前拒绝。'}
& $script -Version 9.9.9 -AndroidBuildNumber 999999999 | Out-Null
foreach($file in $files){if(-not [IO.File]::ReadAllText((Join-Path $fixture $file)).Contains('9.9.9')){throw "版本未同步：$file"}}
if(-not [IO.File]::ReadAllText((Join-Path $fixture $files[1])).Contains('9.9.9+999999999')){throw 'Android构建号遗漏。'}
Write-Output "PASS：构建号回退零写入、五个版本源同步。人工证据：$fixture"
