#requires -Version 7.0
<#
.SYNOPSIS
使用真实公开旧包与本次完整新包，在独立夹具中验收旧版残留清理 CLI。
.DESCRIPTION
仅从输入目录读取载荷，在仓库 temp 下创建人工游戏、配置、存档与备份。
不启用真实用户客户端目录选项，不执行游戏，不删除输入 ZIP、新包或其他测试资料。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$LegacyReleaseZip,
    [Parameter(Mandatory)][string]$NewPackageDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$expectedZip = 'e916a73a0ef9c692ea628b499b08087f44c8b1e8c0c0296b8a0391cf02511389'
if (-not [IO.Path]::IsPathFullyQualified($LegacyReleaseZip) -or
    (Get-FileHash -LiteralPath $LegacyReleaseZip -Algorithm SHA256).Hash.ToLowerInvariant() -cne $expectedZip) {
    throw '旧 ZIP 必须是已核验的公开 v1.3.1 原包。'
}
if (-not [IO.Path]::IsPathFullyQualified($NewPackageDirectory)) { throw '新版完整包须使用绝对路径。' }
$package = (Resolve-Path -LiteralPath $NewPackageDirectory).Path
$updater = Join-Path $package 'mystia-steward-companion-updater.exe'
if (-not (Test-Path -LiteralPath $updater -PathType Leaf)) { throw '新版包缺少更新器。' }
$run = Join-Path $repository ('temp/legacy-cleanup-' + [Guid]::NewGuid().ToString('N'))
$game = Join-Path $run 'game'
$plugin = Join-Path $game 'BepInEx/plugins/mystia-steward-companion'
$config = Join-Path $game 'BepInEx/config'
$data = Join-Path $config 'MystiaStewardCompanion'
$save = Join-Path $run 'save'
$backup = Join-Path $run 'upgrade-record'
$working = Join-Path $run 'working'
foreach ($directory in @($plugin, $config, (Join-Path $game 'BepInEx/core'), $save, $working)) {
    [void][IO.Directory]::CreateDirectory($directory)
}

function Write-FixtureText([string]$Path, [string]$Value) {
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path))
    [IO.File]::WriteAllText($Path, $Value, [Text.UTF8Encoding]::new($false))
}
function Get-Hash([string]$Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Get-Tree([string]$Directory) {
    $entries = [ordered]@{}
    foreach ($item in Get-ChildItem -LiteralPath $Directory -Recurse -Force | Sort-Object FullName) {
        $name = [IO.Path]::GetRelativePath($Directory, $item.FullName)
        $entries[$name] = if ($item.PSIsContainer) { 'directory' } else { Get-Hash $item.FullName }
    }
    return ConvertTo-Json -InputObject $entries -Compress
}
function Invoke-Cli([string[]]$Arguments, [int]$ExpectedCode) {
    # GUI 子系统的命令行路径使用重定向句柄，不依赖真实控制台，也不弹交互窗口。
    $start = [Diagnostics.ProcessStartInfo]::new($updater)
    $start.WorkingDirectory = $working
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(120000)) { throw '清理 CLI 验证超时，现场已保留。' }
        $output = $stdout.GetAwaiter().GetResult()
        $errorOutput = $stderr.GetAwaiter().GetResult()
        if ($process.ExitCode -ne $ExpectedCode) { throw "CLI 退出码 $($process.ExitCode)，预期 $ExpectedCode：$errorOutput" }
        return [pscustomobject]@{ Output = $output; Error = $errorOutput }
    } finally { $process.Dispose() }
}

# 固定 ZIP 摘要已经约束条目，但仍使用明确的三个条目，不解压任何任意路径。
$archive = [IO.Compression.ZipFile]::OpenRead($LegacyReleaseZip)
$oldFiles = @('MystiaStewardCompanion.BepInEx.dll', 'mystia-steward-companion-updater.exe', 'companion/mystia-steward-companion.exe')
try {
    foreach ($name in $oldFiles) {
        $entry = $archive.GetEntry('mystia-steward-companion/' + $name)
        if (-not $entry) { throw '公开 ZIP 缺少预期载荷。' }
        $destination = Join-Path $plugin $name
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
        $inputStream = $entry.Open()
        try {
            $outputStream = [IO.File]::Open($destination, [IO.FileMode]::CreateNew)
            try { $inputStream.CopyTo($outputStream) } finally { $outputStream.Dispose() }
        } finally { $inputStream.Dispose() }
    }
} finally { $archive.Dispose() }
$oldConfig = Join-Path $config 'com.tyukki.mystia-steward-companion.cfg'
$newConfig = Join-Path $data 'com.tyukki.mystia-steward-companion.cfg'
$configText = "[LocalApi]`nToken = synthetic-cleanup-token`n[Diagnostics]`nAggregateModLogPath = `n"
Write-FixtureText $oldConfig $configText
Write-FixtureText (Join-Path $game 'mystia-cleanup-fixture-game.exe') 'synthetic; never execute'
Write-FixtureText (Join-Path $game 'BepInEx/core/fixture.dll') 'protected core'
Write-FixtureText (Join-Path $save 'slot.memory') 'protected save'
Write-FixtureText (Join-Path $plugin 'user-data/personal-note.txt') 'user data; retain in installed and backup trees'
$beforePackage = Get-Tree $package
$beforeSave = Get-Tree $save
$beforeCore = Get-Tree (Join-Path $game 'BepInEx/core')

# 由正式离线安装命令生成真实结构的成功记录，再测试清理，不伪造程序备份的证明文件。
$installArgs = @('--migrate-v1-3-1', '--game-dir', $game, '--package-dir', $package,
    '--save-dir', $save, '--backup-dir', $backup, '--execute')
$installed = (Invoke-Cli $installArgs 0).Output | ConvertFrom-Json
if ($installed.mode -ne 'installed') { throw '隔离旧版升级未成功。' }

# 清理入口应支持直接运行已安装的新 updater；不能把自己的进程误认为旧程序占用。
$updater = Join-Path $plugin 'mystia-steward-companion-updater.exe'

# 游戏未启动，因此构造与生产布局协议一致的合成配置证明；不声称经过真实 Mod 运行迁移。
$layoutName = 'layout-v1-' + [Guid]::NewGuid().ToString('N')
$layoutBackup = Join-Path $data ('migration-backups/' + $layoutName)
Write-FixtureText (Join-Path $layoutBackup 'legacy-plugin.cfg') $configText
Write-FixtureText (Join-Path $layoutBackup 'prepared-plugin.cfg') $configText
Write-FixtureText $newConfig $configText
$legacyEntry = [ordered]@{ key = 'legacy-plugin.cfg'; length = (Get-Item -LiteralPath $oldConfig).Length; sha256 = Get-Hash $oldConfig }
$state = [ordered]@{ layoutVersion = 1; status = 'prepared'; backupDirectory = $layoutName;
    configurationSha256 = Get-Hash (Join-Path $layoutBackup 'prepared-plugin.cfg'); files = @($legacyEntry) }
Write-FixtureText (Join-Path $layoutBackup 'manifest.json') (ConvertTo-Json -InputObject $state -Depth 5)
$state.status = 'complete'
Write-FixtureText (Join-Path $data 'layout-state.json') (ConvertTo-Json -InputObject $state -Depth 5)
foreach ($name in @('favorites.json', 'custom-recipes.json', 'companion-devices.json')) {
    Write-FixtureText (Join-Path $data $name) '{"fixture":"protected user data"}'
}
$runner = Join-Path $data 'updates/runner/20000101000000'
[void][IO.Directory]::CreateDirectory($runner)
Copy-Item -LiteralPath (Join-Path $backup 'original-copy/mystia-steward-companion-updater.exe') -Destination $runner
Write-FixtureText (Join-Path $runner 'personal-note.txt') 'unknown sibling; retain'
$newRunner = Join-Path $data 'updates/runner/20000102000000'
[void][IO.Directory]::CreateDirectory($newRunner)
Copy-Item -LiteralPath $updater -Destination $newRunner
$zipCopy = Join-Path $run 'legacy-package.zip'
Copy-Item -LiteralPath $LegacyReleaseZip -Destination $zipCopy

$protectedFiles = @($newConfig, (Join-Path $data 'layout-state.json'), (Join-Path $data 'favorites.json'),
    (Join-Path $data 'custom-recipes.json'), (Join-Path $data 'companion-devices.json'),
    (Join-Path $plugin 'user-data/personal-note.txt'), (Join-Path $runner 'personal-note.txt'),
    (Join-Path $newRunner 'mystia-steward-companion-updater.exe'))
$protectedHashes = @{}
foreach ($path in $protectedFiles) { $protectedHashes[$path] = Get-Hash $path }
$beforeLayoutBackup = Get-Tree $layoutBackup
$beforePreviewGame = Get-Tree $game
$beforePreviewBackup = Get-Tree $backup
$cleanupArgs = @('--cleanup-v1-3-1', '--game-dir', $game, '--migration-backup-dir', $backup, '--legacy-package-zip', $zipCopy)
$preview = (Invoke-Cli $cleanupArgs 0).Output | ConvertFrom-Json
if ($preview.mode -ne 'preview' -or $preview.planSha256 -cnotmatch '^[0-9a-f]{64}$' -or $preview.totalBytes -le 0) { throw '清理预览未给出有效清单。' }
if ((Get-Tree $game) -cne $beforePreviewGame -or (Get-Tree $backup) -cne $beforePreviewBackup -or (Get-Hash $zipCopy) -cne $expectedZip) { throw '只读清理预览产生写入。' }

# 两个独立执行条件都须满足，任一不符时不能创建日志目录或删除候选。
$null = Invoke-Cli ($cleanupArgs + @('--execute', '--plan-sha256', $preview.planSha256)) 1
$null = Invoke-Cli ($cleanupArgs + @('--execute', '--confirm-migrated', '--plan-sha256', ('0' * 64))) 1
if ((Get-Tree $game) -cne $beforePreviewGame -or (Get-Tree $backup) -cne $beforePreviewBackup) { throw '被拒绝的清理请求产生副作用。' }
$cleaned = (Invoke-Cli ($cleanupArgs + @('--execute', '--confirm-migrated', '--plan-sha256', $preview.planSha256)) 0).Output | ConvertFrom-Json
if ($cleaned.mode -ne 'cleaned' -or $cleaned.bytesDeleted -le 0) { throw '清理未报告实际释放字节。' }
foreach ($path in @($oldConfig, $zipCopy, (Join-Path $runner 'mystia-steward-companion-updater.exe'))) {
    if (Test-Path -LiteralPath $path) { throw "旧候选未删除：$path" }
}
foreach ($copy in @('original-copy', 'plugin-backup')) {
    foreach ($name in $oldFiles) {
        if (Test-Path -LiteralPath (Join-Path $backup ($copy + '/' + $name))) { throw '旧程序备份载荷未删除。' }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $backup ($copy + '/user-data/personal-note.txt')))) { throw '误删了备份中的额外用户文件。' }
}
foreach ($path in $protectedFiles) {
    if ((Get-Hash $path) -cne $protectedHashes[$path]) { throw '当前数据、新版程序或未知兄弟文件被修改。' }
}
if ((Get-Tree $layoutBackup) -cne $beforeLayoutBackup -or (Get-Tree $save) -cne $beforeSave -or
    (Get-Tree (Join-Path $game 'BepInEx/core')) -cne $beforeCore -or (Get-Tree $package) -cne $beforePackage) { throw '清理影响了保护区或新版输入。' }

# 已删除的显式旧 ZIP 应按“已不存在”处理；再次预览应可完成，并允许剩余目录已被移除。
$again = (Invoke-Cli $cleanupArgs 0).Output | ConvertFrom-Json
if ($again.mode -ne 'preview' -or $again.totalBytes -ne 0 -or @($again.candidates).Count -ne 0) { throw '重复清理未收敛到无剩余候选。' }
$beforeNoOp = Get-Tree $game
$noOp = (Invoke-Cli ($cleanupArgs + @('--execute', '--confirm-migrated', '--plan-sha256', $again.planSha256)) 0).Output | ConvertFrom-Json
if ($noOp.mode -ne 'cleaned' -or $null -ne $noOp.journalDirectory -or (Get-Tree $game) -cne $beforeNoOp) { throw '无剩余候选的执行不应制造新记录或改写目录。' }
if ((Get-FileHash -LiteralPath $LegacyReleaseZip -Algorithm SHA256).Hash.ToLowerInvariant() -cne $expectedZip) { throw '输入旧 ZIP 被修改。' }
[ordered]@{ Result = 'passed'; RealGameTouched = $false; RealClientDataTouched = $false;
    LegacyZipSha256 = $expectedZip; Preview = $preview; Cleaned = $cleaned; RepeatedPreview = $again; NoOp = $noOp } |
    ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $run 'result.json') -Encoding utf8
Write-Output "旧版残留清理 CLI 隔离验收通过；现场保留于 $run"
