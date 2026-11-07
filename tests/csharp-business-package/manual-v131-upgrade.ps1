#requires -Version 7.0
<#
.SYNOPSIS
验证公开 v1.3.1 的手动程序替换及首次配置迁移，只操作本仓库 temp 下的新建人工游戏目录。
.DESCRIPTION
旧 ZIP 仅作为程序文件输入，必须匹配已核验的公开摘要；新版使用本次实际构建的完整包。
脚本不执行旧程序、不启动 Unity、不访问真实存档或客户端数据。配置阶段调用真实 BepInEx
ConfigFile 与生产迁移代码的隔离宿主，因此验证结果不等于真实游戏加载或双机局域网验收。
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

# 先检查路径各级，再递归枚举；不能先跟随链接收集文件、随后才声称测试目录隔离。
function Assert-OrdinaryTree([string]$Path) {
    $cursor = [IO.Path]::GetFullPath($Path)
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            if ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw '隔离验证拒绝经过重解析点的路径。'
            }
        }
        $parent = [IO.Directory]::GetParent($cursor)
        $cursor = if ($parent) { $parent.FullName } else { '' }
    }
    if (Test-Path -LiteralPath $Path -PathType Container) {
        $queue = [Collections.Generic.Queue[string]]::new()
        $queue.Enqueue([IO.Path]::GetFullPath($Path))
        while ($queue.Count) {
            foreach ($entry in Get-ChildItem -LiteralPath $queue.Dequeue() -Force) {
                if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw '隔离验证拒绝树内链接。' }
                if ($entry.PSIsContainer) { $queue.Enqueue($entry.FullName) }
            }
        }
    }
}
function Get-TreeDigest([string]$Path) {
    Assert-OrdinaryTree $Path
    $items = [ordered]@{}
    foreach ($entry in Get-ChildItem -LiteralPath $Path -Recurse -Force | Sort-Object FullName) {
        $name = [IO.Path]::GetRelativePath($Path, $entry.FullName).Replace('\', '/')
        $items[$name] = if ($entry.PSIsContainer) { 'directory' }
            else { (Get-FileHash -LiteralPath $entry.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
    return ConvertTo-Json -InputObject $items -Compress
}
function Put-SyntheticFile([string]$Path, [string]$Text) {
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path))
    [IO.File]::WriteAllText($Path, $Text, [Text.UTF8Encoding]::new($false))
}
foreach ($inputPath in @($LegacyReleaseZip, $NewPackageDirectory)) {
    if (-not [IO.Path]::IsPathFullyQualified($inputPath)) { throw '输入必须使用绝对路径。' }
    Assert-OrdinaryTree $inputPath
}
if ((Get-FileHash -LiteralPath $LegacyReleaseZip -Algorithm SHA256).Hash.ToLowerInvariant() -cne $expectedZip) {
    throw '旧 ZIP 不匹配公开 v1.3.1 摘要；未创建测试目录。'
}
$expectedEntries = @('mystia-steward-companion/MystiaStewardCompanion.BepInEx.dll',
    'mystia-steward-companion/mystia-steward-companion-updater.exe',
    'mystia-steward-companion/companion/mystia-steward-companion.exe')
$archive = [IO.Compression.ZipFile]::OpenRead($LegacyReleaseZip)
try {
    $names = @($archive.Entries.FullName)
    if ($names.Count -ne 3 -or @(Compare-Object ($names | Sort-Object) ($expectedEntries | Sort-Object)).Count) {
        throw '旧 ZIP 条目集合与公开三文件包不符。'
    }
} finally { $archive.Dispose() }
& node (Join-Path $repository 'scripts/package-manifest.mjs') validate $NewPackageDirectory | Out-Null
if ($LASTEXITCODE -ne 0) { throw '新版完整包预校验失败。' }
$inputDigest = Get-TreeDigest $NewPackageDirectory

# 所有可写根由脚本创建，不提供游戏目录参数；保留现场便于复核，不清理其他测试目录。
$run = Join-Path $repository ('temp/manual-v131-' + [Guid]::NewGuid().ToString('N'))
Assert-OrdinaryTree $run
$game = Join-Path $run 'game'
$plugin = Join-Path $game 'BepInEx/plugins/mystia-steward-companion'
$config = Join-Path $game 'BepInEx/config'
$core = Join-Path $game 'BepInEx/core'
$save = Join-Path $run 'synthetic-save'
$data = Join-Path $config 'MystiaStewardCompanion'
[void][IO.Directory]::CreateDirectory((Split-Path -Parent $plugin))
[IO.Compression.ZipFile]::ExtractToDirectory($LegacyReleaseZip, (Split-Path -Parent $plugin))
# 模拟用户把公开旧客户端独立放置后配置了外部路径；只复制已验证旧 ZIP 内的程序，不执行。
# 使用正斜线写入 BepInEx 文本值，避免 PowerShell 路径和 TOML 反斜线转义混淆。
$externalClient = (Join-Path $run 'external-legacy-client/mystia-steward-companion.exe').Replace('\', '/')
[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($externalClient))
Copy-Item -LiteralPath (Join-Path $plugin 'companion/mystia-steward-companion.exe') -Destination $externalClient
$externalDigest = (Get-FileHash -LiteralPath $externalClient -Algorithm SHA256).Hash
Put-SyntheticFile (Join-Path $config '.synthetic-mod-config-fixture') 'mystia-synthetic-config-v1'
Put-SyntheticFile (Join-Path $config 'com.tyukki.mystia-steward-companion.cfg') "[LocalApi]`nToken = synthetic-manual-upgrade-token`n[Runtime]`nAutoRefreshSeconds = 3`n[Companion]`nExecutablePath = $externalClient`n"
Put-SyntheticFile (Join-Path $data 'favorites.json') '{"version":1,"favorites":[]}'
Put-SyntheticFile (Join-Path $data 'custom-recipes.json') '{"version":1,"enabled":false,"recipes":[]}'
Put-SyntheticFile (Join-Path $data 'companion-devices.json') '{"schemaVersion":1}'
Put-SyntheticFile (Join-Path $core 'fixture.dll') 'synthetic core; never executed'
Put-SyntheticFile (Join-Path $save 'slot.memory') 'synthetic save; not a game save'
$beforeConfig = Get-TreeDigest $config
$beforeData = Get-TreeDigest $data
$beforeCore = Get-TreeDigest $core
$beforeSave = Get-TreeDigest $save

# 模拟用户已核对的精确目录删除。删除前再次验证绝对目标、父级和整个树，绝不把输入目录
# 或通过另一个 shell 拼接的路径交给递归删除命令。正式旧包只有程序，不含本夹具配置。
$expectedPlugin = [IO.Path]::GetFullPath((Join-Path $run 'game/BepInEx/plugins/mystia-steward-companion'))
$resolvedPlugin = (Resolve-Path -LiteralPath $plugin).Path
if (-not $resolvedPlugin.Equals($expectedPlugin, [StringComparison]::OrdinalIgnoreCase) -or
    -not $expectedPlugin.StartsWith([IO.Path]::GetFullPath($run) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw '拒绝删除不属于本次新建隔离根的目录。'
}
Assert-OrdinaryTree $resolvedPlugin
Remove-Item -LiteralPath $resolvedPlugin -Recurse -Force
Copy-Item -LiteralPath $NewPackageDirectory -Destination $plugin -Recurse
& node (Join-Path $repository 'scripts/package-manifest.mjs') validate $plugin | Out-Null
if ($LASTEXITCODE -ne 0) { throw '手动替换后的完整包验证失败。' }
if ((Get-TreeDigest $plugin) -cne $inputDigest -or (Get-TreeDigest $config) -cne $beforeConfig -or
    (Get-TreeDigest $core) -cne $beforeCore -or (Get-TreeDigest $save) -cne $beforeSave) {
    throw '手动程序替换改变保护区或未保留完整新版载荷。'
}

# 宿主自身还会检查 cwd/temp 前缀与固定哨兵；普通测试参数不能被误用于真实游戏配置。
Push-Location -LiteralPath $repository
try {
    & dotnet run --project tests/mod-data-layout/ModDataLayoutSmoke.csproj -c Release -- --manual-upgrade-fixture $config
    if ($LASTEXITCODE -ne 0) { throw '首次启动配置迁移/删除/重复启动验证失败。' }
} finally { Pop-Location }
foreach ($name in @('favorites.json', 'custom-recipes.json', 'companion-devices.json')) {
    $before = $beforeData | ConvertFrom-Json -AsHashtable
    if ((Get-FileHash -LiteralPath (Join-Path $data $name) -Algorithm SHA256).Hash.ToLowerInvariant() -cne $before[$name]) {
        throw '仅 CFG 迁移不应改写业务 JSON。'
    }
}
if ((Get-TreeDigest $core) -cne $beforeCore -or (Get-TreeDigest $save) -cne $beforeSave -or
    (Get-TreeDigest $plugin) -cne $inputDigest -or (Get-TreeDigest $NewPackageDirectory) -cne $inputDigest -or
    (Get-FileHash -LiteralPath $externalClient -Algorithm SHA256).Hash -cne $externalDigest -or
    (Get-FileHash -LiteralPath $LegacyReleaseZip -Algorithm SHA256).Hash.ToLowerInvariant() -cne $expectedZip) {
    throw '配置迁移改变了程序、输入包或其他保护区。'
}
[ordered]@{
    Result = 'passed'; LegacyZipSha256 = $expectedZip; NewPackage = $NewPackageDirectory
    ProgramReplacement = 'manual-directory-replacement'; Configuration = 'real-production-migration-and-cleanup'
    BusinessJsonUnchanged = $true; InputsUnchanged = $true; RealGameTouched = $false
    ExternalLegacyClientIdentified = $true; ExternalClientUnchanged = $true
    UnityExecuted = $false; RealClientStorageTouched = $false
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $run 'result.json') -Encoding utf8
Write-Output "公开 v1.3.1 手动升级与自动配置清理隔离验证通过；记录保留于 $run"
