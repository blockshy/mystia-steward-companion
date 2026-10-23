#requires -Version 7.0
<#
.SYNOPSIS
从当前三 DLL、独立 Rust updater 和完整 Flutter Windows Release 树生成唯一规范成套包。
.DESCRIPTION
本脚本仅在仓库 mods/bepinex/dist 同级执行带备份的目录事务。任何输入缺失、链接或清单
错误都在替换旧 dist 前拒绝；不执行载荷、不安装到游戏、不发布 GitHub Release。
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$CompanionDirectory,
    [string]$UpdaterPath
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$modRoot = Join-Path $repository 'mods/bepinex'
$dist = Join-Path $modRoot 'dist'
$stage = Join-Path $modRoot ('dist.staging-' + [Guid]::NewGuid().ToString('N'))
$backup = Join-Path $modRoot ('dist.backup-' + [Guid]::NewGuid().ToString('N'))
$packageName = 'mystia-steward-companion'
$package = Join-Path $stage $packageName
$manifestTool = Join-Path $repository 'scripts/package-manifest.mjs'
if (-not $CompanionDirectory) { $CompanionDirectory = Join-Path $repository 'apps/companion-flutter/build/windows/x64/runner/Release' }
if (-not $UpdaterPath) {
    $cargoOutput = if ($env:CARGO_TARGET_DIR) { $env:CARGO_TARGET_DIR } else { Join-Path $repository 'native/updater/target' }
    $UpdaterPath = Join-Path $cargoOutput 'release/mystia-steward-companion-updater.exe'
}
function Assert-RealTree([string]$Root) {
    $full = [IO.Path]::GetFullPath($Root)
    $cursor = $full
    while ($cursor) {
        $item = Get-Item -LiteralPath $cursor -Force
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "拒绝重解析路径：$cursor" }
        $parent = [IO.Directory]::GetParent($cursor); $cursor = if ($parent) { $parent.FullName } else { '' }
    }
    $queue = [Collections.Generic.Queue[string]]::new(); $queue.Enqueue($full)
    while ($queue.Count -gt 0) {
        foreach ($item in @(Get-ChildItem -LiteralPath $queue.Dequeue() -Force)) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "拒绝包内链接：$($item.FullName)" }
            if ($item.PSIsContainer) { $queue.Enqueue($item.FullName) }
        }
    }
}
function Assert-Managed([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if (-not [IO.Directory]::GetParent($full).FullName.Equals($modRoot,[StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($full) -cnotmatch '^dist(?:\.(?:staging|backup)-[0-9a-f]{32})?$') { throw "拒绝管理非打包事务路径：$full" }
    if (Test-Path -LiteralPath $full) { Assert-RealTree $full }
}
function Validate-Package([string]$Root) {
    & node $manifestTool validate $Root | Out-Null
    if ($LASTEXITCODE -ne 0) { throw '完整包清单验证失败。' }
}
function Assert-NoBundledGameMedia([string]$Root) {
    # 游戏图片已改为 Mod 运行时生成并通过本地 API 读取；旧 Flutter 增量构建目录可能仍留下
    # 已从 pubspec 删除的 PNG/清单。必须在复制前拒绝整个旧目录（包括空目录），不能静默删除
    # 用户指定的构建输入，也不能让完整包清单把过期资源重新认定为合法载荷。
    $full = [IO.Path]::GetFullPath($Root)
    $retired = 'data/flutter_assets/assets/game-media'
    foreach ($item in Get-ChildItem -LiteralPath $full -Recurse -Force) {
        $relative = [IO.Path]::GetRelativePath($full, $item.FullName).Replace('\', '/')
        if ($relative.Equals($retired, [StringComparison]::OrdinalIgnoreCase) -or
            $relative.StartsWith($retired + '/', [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Windows Release 含已退役的 assets/game-media 打包资源；请清理并重新构建客户端后再打包，当前输入和旧 dist 均未主动删除。'
        }
    }
}
$pending = @(Get-ChildItem -LiteralPath $modRoot -Force | Where-Object Name -Match '^dist\.(staging|backup)-')
if ($pending.Count) { throw "存在未收尾打包事务，需先审查：$($pending.FullName -join '；')" }
Assert-RealTree $CompanionDirectory
Assert-NoBundledGameMedia $CompanionDirectory
foreach ($artifactInput in @($UpdaterPath, (Join-Path $CompanionDirectory 'mystia-steward-companion.exe'), (Join-Path $CompanionDirectory 'flutter_windows.dll'))) {
    $item = Get-Item -LiteralPath $artifactInput -Force
    if ($item.PSIsContainer -or $item.Length -le 0 -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "组件不可用：$artifactInput" }
}
# 在创建 staging 前只读检查完整 VC 运行库、许可声明及真实 PE 导入。打包不静默修复输入，
# updater 必须静态链接 CRT，避免复制到独立 runner 后依赖恰好安装过 VS 的开发机。
& node (Join-Path $repository 'scripts/windows-runtime.mjs') verify $CompanionDirectory $UpdaterPath | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Windows 应用本地运行库或独立 updater CRT 验证失败。' }
foreach ($target in @($dist,$stage,$backup)) { Assert-Managed $target }
$oldMoved = $false
try {
    [void][IO.Directory]::CreateDirectory($package)
    foreach ($name in @('MystiaStewardCompanion.BepInEx.dll','MystiaStewardCompanion.Contracts.dll','MystiaStewardCompanion.Business.dll')) {
        # 只交付明确的三个业务组件，禁止误打包 BepInEx/Unity 编译参考 DLL。
        $source = Join-Path $modRoot "bin/$Configuration/$name"
        $item = Get-Item -LiteralPath $source -Force
        if ($item.Length -le 0 -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "业务 DLL 无效：$source" }
        Copy-Item -LiteralPath $source -Destination (Join-Path $package $name)
    }
    Copy-Item -LiteralPath $UpdaterPath -Destination (Join-Path $package 'mystia-steward-companion-updater.exe')
    Copy-Item -LiteralPath $CompanionDirectory -Destination (Join-Path $package 'companion') -Recurse
    # 再检查实际复制结果，避免预检之后输入目录变化使旧资源进入最终 ZIP。
    Assert-NoBundledGameMedia (Join-Path $package 'companion')
    # 构建配置的 Release 运行树是受管理载荷，清单记录全部数据文件及插件 DLL。
    & node $manifestTool create $package | Out-Null
    if ($LASTEXITCODE -ne 0) { throw '完整包清单生成失败。' }
    Validate-Package $package
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($package, (Join-Path $stage 'mystia-steward-companion-bepinex.zip'), [IO.Compression.CompressionLevel]::Optimal, $true)
    [IO.Compression.ZipFile]::CreateFromDirectory((Join-Path $package 'companion'), (Join-Path $stage 'mystia-steward-companion-companion-windows-x64.zip'), [IO.Compression.CompressionLevel]::Optimal, $false)
    # ZIP 必须恰好包含清单声明的载荷与清单自身，禁止只检查几个必需文件。
    $manifest = Get-Content -LiteralPath (Join-Path $package 'package-manifest.json') -Raw | ConvertFrom-Json
    $expected = @($manifest.files.path | ForEach-Object { "$packageName/$_" }) + @("$packageName/package-manifest.json")
    $zip = [IO.Compression.ZipFile]::OpenRead((Join-Path $stage 'mystia-steward-companion-bepinex.zip'))
    try {
        $actual = @($zip.Entries | Where-Object { -not $_.FullName.EndsWith('/') } | ForEach-Object FullName)
        if ($actual.Count -ne $expected.Count -or @(Compare-Object ($actual | Sort-Object) ($expected | Sort-Object)).Count) { throw 'ZIP 精确载荷集合不符。' }
        foreach ($entry in @($zip.Entries | Where-Object { -not $_.FullName.EndsWith('/') })) {
            # 同时验证压缩后再次读取的字节，不能只凭条目名称证明 ZIP 完整。
            $relative = $entry.FullName.Substring($packageName.Length + 1)
            $source = Join-Path $package $relative
            $stream = $entry.Open(); $hash = [Security.Cryptography.SHA256]::Create()
            try { $digest = [Convert]::ToHexString($hash.ComputeHash($stream)).ToLowerInvariant() }
            finally { $stream.Dispose(); $hash.Dispose() }
            if ($entry.Length -ne (Get-Item -LiteralPath $source).Length -or $digest -cne (Get-FileHash -LiteralPath $source).Hash.ToLowerInvariant()) { throw "ZIP字节校验失败：$relative" }
        }
    } finally { $zip.Dispose() }
    if (Test-Path -LiteralPath $dist) { Assert-Managed $dist; Assert-Managed $backup; Move-Item -LiteralPath $dist -Destination $backup; $oldMoved=$true }
    try { Assert-Managed $stage; Move-Item -LiteralPath $stage -Destination $dist }
    catch { if ($oldMoved -and -not (Test-Path -LiteralPath $dist)) { Assert-Managed $backup; Move-Item -LiteralPath $backup -Destination $dist }; throw }
    if (Test-Path -LiteralPath $backup) { Assert-Managed $backup; Remove-Item -LiteralPath $backup -Recurse -Force }
    Write-Output "完整包：$(Join-Path $dist 'mystia-steward-companion-bepinex.zip')；文件数=$($manifest.files.Count)"
}
finally {
    if (Test-Path -LiteralPath $stage) { Assert-Managed $stage; Remove-Item -LiteralPath $stage -Recurse -Force }
}
