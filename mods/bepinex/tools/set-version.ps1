#requires -Version 7.0
<# 将产品版本与显式 Android 构建号一起调整；先验证所有输入，再写入，禁止旧 Tauri 版本源。 #>
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][ValidateRange(1003002, 2100000000)][int]$AndroidBuildNumber
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$versionValue = $Version.Trim().TrimStart('v')
if ($versionValue -cnotmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-preview\.[1-9][0-9]*)?$') { throw '版本必须是规范 X.Y.Z 或 X.Y.Z-preview.N。' }
$pubspec = Join-Path $repository 'apps/companion-flutter/pubspec.yaml'
$current = [regex]::Match([IO.File]::ReadAllText($pubspec), '(?m)^version:\s*[^+\s]+\+([0-9]+)\s*$')
if (-not $current.Success -or $AndroidBuildNumber -le [int]$current.Groups[1].Value) { throw 'AndroidBuildNumber 必须高于当前 pubspec 构建号。' }
$definitions = @(
    @{ Path='package.json'; Pattern='("version"\s*:\s*")[^"]+(")'; Value=$versionValue },
    @{ Path='apps/companion-flutter/pubspec.yaml'; Pattern='(?m)(^version:\s*)[^\r\n]+()'; Value="$versionValue+$AndroidBuildNumber" },
    @{ Path='native/updater/Cargo.toml'; Pattern='(?m)(^version = ")[^"]+(")'; Value=$versionValue },
    @{ Path='native/updater/Cargo.lock'; Pattern='(name = "mystia-steward-companion-updater"\s+version = ")[^"]+(")'; Value=$versionValue },
    @{ Path='mods/bepinex/src/Plugin/MystiaStewardCompanionPlugin.cs'; Pattern='(public const string PluginVersion = ")[^"]+(";)'; Value=$versionValue }
)
$updates = @()
foreach ($entry in $definitions) {
    $file = Join-Path $repository $entry.Path
    $before = [IO.File]::ReadAllText($file)
    $regex = [regex]::new($entry.Pattern)
    if (-not $regex.IsMatch($before)) { throw "无法定位版本：$file" }
    $after = $regex.Replace($before, ('${1}' + $entry.Value + '${2}'), 1)
    $updates += @{ Path=$file; Before=$before; After=$after }
}
$written = @()
try {
    foreach ($item in $updates) {
        # 每次写入前复核原字节，避免同时版本编辑被静默覆盖。
        if ([IO.File]::ReadAllText($item.Path) -cne $item.Before) { throw "版本源并发变化：$($item.Path)" }
        [IO.File]::WriteAllText($item.Path, $item.After, [Text.UTF8Encoding]::new($false)); $written += $item
    }
} catch {
    foreach ($item in $written) { [IO.File]::WriteAllText($item.Path, $item.Before, [Text.UTF8Encoding]::new($false)) }
    throw
}
Write-Output "版本已同步：$versionValue；Android 构建号：$AndroidBuildNumber"
