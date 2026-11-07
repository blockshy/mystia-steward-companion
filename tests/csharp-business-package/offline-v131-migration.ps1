#requires -Version 7.0
<#
.SYNOPSIS
以真实公开 v1.3.1 ZIP 验证离线迁移；只在本仓库 temp 构建人工游戏目录，不访问真实配置或存档。
.DESCRIPTION
ZIP 仅作为旧载荷身份来源。整包 SHA-256 和三个条目路径均固定；Rust 故障矩阵使用人工新版包。
可选 NewPackageDirectory 额外验证实际完整新版目录的发布 CLI，但始终安装到人工游戏目录。
通过不代表真实游戏运行验收。故障注入只存在于 Rust 测试回调，不暴露给发布 CLI。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$LegacyReleaseZip,
    [string]$NewPackageDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$expectedZip = 'e916a73a0ef9c692ea628b499b08087f44c8b1e8c0c0296b8a0391cf02511389'
if (-not [IO.Path]::IsPathFullyQualified($LegacyReleaseZip) -or
    (Get-FileHash -LiteralPath $LegacyReleaseZip -Algorithm SHA256).Hash.ToLowerInvariant() -cne $expectedZip) {
    throw '输入不是已核对的公开 v1.3.1 ZIP；不尝试解压或执行未知旧载荷。'
}
$expectedEntries = @('mystia-steward-companion/MystiaStewardCompanion.BepInEx.dll',
    'mystia-steward-companion/mystia-steward-companion-updater.exe',
    'mystia-steward-companion/companion/mystia-steward-companion.exe')
$archive = [IO.Compression.ZipFile]::OpenRead($LegacyReleaseZip)
try {
    $names = @($archive.Entries.FullName)
    if ($names.Count -ne 3 -or @($names | Where-Object { $_ -cnotin $expectedEntries }).Count -ne 0) { throw '公开 ZIP 条目结构不符合三文件基线。' }
} finally { $archive.Dispose() }

# 每次运行使用新目录，保留现场和摘要供人工复核，不递归删除其他测试或开发资料。
$run = Join-Path $repository ('temp/offline-v131-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($run)
$extract = Join-Path $run 'release'
[IO.Compression.ZipFile]::ExtractToDirectory($LegacyReleaseZip, $extract)
$oldSource = $env:MYSTIA_TEST_V131_ROOT; $oldRoot = $env:MYSTIA_TEST_MIGRATION_ROOT
try {
    $env:MYSTIA_TEST_V131_ROOT = Join-Path $extract 'mystia-steward-companion'
    $env:MYSTIA_TEST_MIGRATION_ROOT = Join-Path $run ('mystia-v131-fixture-' + [Guid]::NewGuid().ToString('N'))
    & cargo test --locked --manifest-path (Join-Path $repository 'native/updater/Cargo.toml') offline_migration::tests::real_public_package_migration -- --ignored --exact --nocapture
    if ($LASTEXITCODE -ne 0) { throw '公开旧包隔离迁移测试失败，现场已保留。' }
    [ordered]@{ LegacyZipSha256 = $expectedZip; FixtureRoot = $env:MYSTIA_TEST_MIGRATION_ROOT;
        RealGameTouched = $false; NewPackage = 'synthetic manifest fixture'; Result = 'passed' } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $run 'result.json') -Encoding utf8
} finally {
    $env:MYSTIA_TEST_V131_ROOT = $oldSource; $env:MYSTIA_TEST_MIGRATION_ROOT = $oldRoot
}

if ($NewPackageDirectory) {
    if (-not [IO.Path]::IsPathFullyQualified($NewPackageDirectory)) { throw '新版目录必须使用绝对路径。' }
    $newPackage = (Resolve-Path -LiteralPath $NewPackageDirectory).Path
    $updater = Join-Path $newPackage 'mystia-steward-companion-updater.exe'
    if (-not (Test-Path -LiteralPath $updater -PathType Leaf)) { throw '新版目录缺少更新器。' }
    $cliRoot = Join-Path $run 'actual-package-cli'
    $game = Join-Path $cliRoot 'game'
    $plugin = Join-Path $game 'BepInEx/plugins/mystia-steward-companion'
    $save = Join-Path $cliRoot 'save'
    $working = Join-Path $cliRoot 'unrelated-working-directory'
    $backup = Join-Path $cliRoot 'transaction'
    foreach ($directory in @($plugin, $save, $working, (Join-Path $game 'BepInEx/config'), (Join-Path $game 'BepInEx/core'))) {
        [void][IO.Directory]::CreateDirectory($directory)
    }
    # 逐项复制经过整包哈希验证的三个载荷，不执行这些旧程序。
    Get-ChildItem -LiteralPath (Join-Path $extract 'mystia-steward-companion') | Copy-Item -Destination $plugin -Recurse
    [IO.File]::WriteAllText((Join-Path $game 'mystia-offline-fixture-game.exe'), 'synthetic; never executed')
    [IO.File]::WriteAllText((Join-Path $game 'BepInEx/config/fixture.cfg'), 'synthetic config sentinel')
    [IO.File]::WriteAllText((Join-Path $game 'BepInEx/core/fixture.dll'), 'synthetic core sentinel')
    [IO.File]::WriteAllText((Join-Path $save 'slot.memory'), 'synthetic save sentinel')

    function Get-FixtureTree([string]$Directory) {
        $entries = [ordered]@{}
        foreach ($entry in Get-ChildItem -LiteralPath $Directory -Recurse -Force | Sort-Object FullName) {
            $relative = [IO.Path]::GetRelativePath($Directory, $entry.FullName)
            $entries[$relative] = if ($entry.PSIsContainer) { 'directory' } else { (Get-FileHash -LiteralPath $entry.FullName -Algorithm SHA256).Hash }
        }
        return (ConvertTo-Json -InputObject $entries -Compress)
    }
    function Invoke-IsolatedUpdater([string[]]$Arguments, [int]$ExpectedExitCode) {
        # 显式继承重定向句柄，既验证 GUI 子系统 CLI 输出，也避免无终端时打开原生消息框。
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
            if (-not $process.WaitForExit(120000)) { throw '隔离 CLI 未在两分钟内返回；保留现场，请检查子进程。' }
            $output = $stdout.GetAwaiter().GetResult()
            $errorOutput = $stderr.GetAwaiter().GetResult()
            if ($process.ExitCode -ne $ExpectedExitCode) { throw "CLI 退出码 $($process.ExitCode)，预期 $ExpectedExitCode：$errorOutput" }
            return [pscustomobject]@{ Output = $output; Error = $errorOutput }
        } finally { $process.Dispose() }
    }
    $beforeGame = Get-FixtureTree $game
    $beforeSave = Get-FixtureTree $save
    $beforeConfig = Get-FixtureTree (Join-Path $game 'BepInEx/config')
    $beforeCore = Get-FixtureTree (Join-Path $game 'BepInEx/core')
    $beforeOld = Get-FixtureTree $plugin
    $withoutArguments = Invoke-IsolatedUpdater @() 1
    if (-not $withoutArguments.Error -or @(Get-ChildItem -LiteralPath $working -Force).Count -ne 0) { throw '无参数运行未给出错误，或污染了任意工作目录。' }
    $arguments = @('--migrate-v1-3-1', '--game-dir', $game, '--package-dir', $newPackage, '--save-dir', $save, '--backup-dir', $backup)
    $preview = (Invoke-IsolatedUpdater $arguments 0).Output | ConvertFrom-Json
    if ($preview.mode -ne 'validated-only' -or $preview.writesPerformed -or (Test-Path -LiteralPath $backup) -or (Get-FixtureTree $game) -cne $beforeGame -or (Get-FixtureTree $save) -cne $beforeSave) { throw '预览产生了写入或报告不符。' }
    $installed = (Invoke-IsolatedUpdater ($arguments + '--execute') 0).Output | ConvertFrom-Json
    if ($installed.mode -ne 'installed' -or -not $installed.writesPerformed) { throw '实际新版包 CLI 安装未报告成功。' }
    if ((Get-FixtureTree (Join-Path $game 'BepInEx/config')) -cne $beforeConfig -or (Get-FixtureTree (Join-Path $game 'BepInEx/core')) -cne $beforeCore -or (Get-FixtureTree $save) -cne $beforeSave) { throw '实际新版包 CLI 改变了保护目录。' }
    foreach ($retained in @('original-copy', 'plugin-backup')) {
        if ((Get-FixtureTree (Join-Path $backup $retained)) -cne $beforeOld) { throw "保留的旧树不完整：$retained" }
    }
    [ordered]@{ Result = 'passed'; RealGameTouched = $false; NewPackage = $newPackage; Preview = $preview; Installed = $installed; NoArgumentsWrites = $false } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $run 'actual-package-result.json') -Encoding utf8
}
Write-Output "公开 v1.3.1 隔离迁移验证通过；记录保留于 $run"
