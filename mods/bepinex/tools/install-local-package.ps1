#requires -Version 7.0
<#
.SYNOPSIS
成套安装已构建的本机插件；不带 -Execute 时只读校验，不创建备份或暂存目录。
.DESCRIPTION
安装边界仅为 BepInEx/plugins/mystia-steward-companion。五个组件与摘要覆盖原件，其余文件原样保留；
整个原插件先备份到仓库 temp 独占记录目录。同卷事务目录位于游戏目录同级，避免 BepInEx 扫描到重复 DLL。
配置、核心、存档及游戏根文件只读记录前后摘要，不修改。任何验证失败都恢复原插件并核验完整原树。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$GameRoot,
    [Parameter(Mandatory)][string]$PackageRoot,
    [Parameter(Mandatory)][string]$SaveRoot,
    [Parameter(Mandatory)][string]$RecordRoot,
    [switch]$Execute,
    # 仅供仓库 temp 内的全人工夹具验证回退，不允许对实际游戏启用。
    [switch]$InjectPostInstallFailure
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..')).TrimEnd('\', '/')
$workspaceTemp = Join-Path $repository 'temp'
$components = @('MystiaStewardCompanion.BepInEx.dll', 'MystiaStewardCompanion.Contracts.dll',
    'MystiaStewardCompanion.Business.dll', 'companion/mystia-steward-companion.exe', 'mystia-steward-companion-updater.exe')
$bundleFiles = $components + @('business-bundle.sha256')
$comparison = [StringComparison]::OrdinalIgnoreCase

function Full-Path([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path) -or -not [IO.Path]::IsPathFullyQualified($Path)) { throw "必须提供绝对路径：$Path" }
    return [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
}
function Is-Within([string]$Path, [string]$Parent) {
    return $Path.StartsWith($Parent.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, $comparison)
}
function Assert-NoReparseAncestors([string]$Path) {
    $cursor = $Path
    while (-not [string]::IsNullOrEmpty($cursor)) {
        # Get-Item 同时识别悬空链接；不能以目标不存在的 Test-Path 结果跳过链接检查。
        $item = $null
        try { $item = Get-Item -LiteralPath $cursor -Force -ErrorAction Stop }
        catch [Management.Automation.ItemNotFoundException] { }
        if ($null -ne $item) {
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "拒绝符号链接或重解析路径：$cursor" }
        }
        $parent = [IO.Directory]::GetParent($cursor)
        if ($null -eq $parent) { break }
        $cursor = $parent.FullName
    }
}
function Assert-Directory([string]$Path) {
    Assert-NoReparseAncestors $Path
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { throw "目录不存在：$Path" }
}
function File-Fingerprint([IO.FileInfo]$Item) {
    return "F:$($Item.Length):$((Get-FileHash -LiteralPath $Item.FullName -Algorithm SHA256).Hash.ToLowerInvariant())"
}
function Read-Tree([string]$Root) {
    # 逐层检查再进入，禁止先用 -Recurse 遍历尚未验证的链接。
    Assert-NoReparseAncestors $Root
    $state = [ordered]@{}
    if (-not (Test-Path -LiteralPath $Root)) { $state['.'] = 'absent'; return $state }
    Assert-Directory $Root
    $state['.'] = 'D'
    $queue = [Collections.Generic.Queue[string]]::new(); $queue.Enqueue($Root)
    while ($queue.Count -gt 0) {
        foreach ($item in @(Get-ChildItem -LiteralPath $queue.Dequeue() -Force | Sort-Object Name)) {
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "目录树含重解析点，拒绝继续：$($item.FullName)" }
            $relative = [IO.Path]::GetRelativePath($Root, $item.FullName).Replace('\', '/')
            if ($item.PSIsContainer) { $state[$relative] = 'D'; $queue.Enqueue($item.FullName) }
            else { $state[$relative] = File-Fingerprint $item }
        }
    }
    return $state
}
function Assert-SameTree($Expected, $Actual, [string]$Label) {
    if ($Expected.Count -ne $Actual.Count) { throw "$Label 的文件或目录数量已变化。" }
    foreach ($key in $Expected.Keys) {
        if (-not $Actual.Contains($key) -or $Expected[$key] -cne $Actual[$key]) { throw "$Label 不一致：$key" }
    }
}
function Copy-Tree([string]$Source, [string]$Destination) {
    $state = Read-Tree $Source
    Assert-NoReparseAncestors $Destination
    if (Test-Path -LiteralPath $Destination) { throw "复制目标必须不存在：$Destination" }
    [void][IO.Directory]::CreateDirectory($Destination)
    foreach ($key in $state.Keys) {
        if ($key -eq '.') { continue }
        $target = Join-Path $Destination $key
        if ($state[$key] -eq 'D') { [void][IO.Directory]::CreateDirectory($target) }
        else { Copy-Item -LiteralPath (Join-Path $Source $key) -Destination $target }
    }
    Assert-SameTree $state (Read-Tree $Destination) '复制验证'
}
function Read-Protection {
    $result = [ordered]@{}
    foreach ($pair in @(@('config', (Join-Path $GameRoot 'BepInEx/config')), @('core', (Join-Path $GameRoot 'BepInEx/core')), @('save', $SaveRoot))) {
        $tree = Read-Tree $pair[1]
        foreach ($key in $tree.Keys) { $result["$($pair[0])/$key"] = $tree[$key] }
    }
    # 根目录所有文件均保护，涵盖游戏 EXE、GameAssembly/UnityPlayer、doorstop 等关键加载文件。
    foreach ($item in @(Get-ChildItem -LiteralPath $GameRoot -File -Force | Sort-Object Name)) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "游戏根文件为重解析点：$($item.FullName)" }
        $result['game-root/' + $item.Name] = File-Fingerprint $item
    }
    return $result
}
function Assert-ProcessesStopped {
    $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($name in @('mystia-steward-companion', 'mystia-steward-companion-updater')) { [void]$names.Add($name) }
    foreach ($file in @(Get-ChildItem -LiteralPath $GameRoot -File -Filter '*.exe')) { [void]$names.Add([IO.Path]::GetFileNameWithoutExtension($file.Name)) }
    if ($names.Count -lt 3) { throw '游戏根目录没有 EXE，无法确认目标或检查游戏进程。' }
    $active = @(Get-Process -ErrorAction Stop | Where-Object { $names.Contains($_.ProcessName) })
    if ($active.Count -gt 0) { throw "游戏、伴随客户端或更新程序仍在运行：$(($active | ForEach-Object { "$($_.ProcessName) PID=$($_.Id)" }) -join '；')" }
}
function Read-PackageManifest {
    $tree = Read-Tree $PackageRoot
    $actual = @($tree.Keys | Where-Object { $tree[$_] -like 'F:*' })
    if ($actual.Count -ne 6 -or @($actual | Where-Object { $_ -cnotin $bundleFiles }).Count -gt 0) { throw '安装包必须精确包含五个组件和 business-bundle.sha256 六个文件。' }
    if (@($tree.Keys | Where-Object { $tree[$_] -eq 'D' -and $_ -notin @('.', 'companion') }).Count -gt 0) { throw '安装包含非预期目录。' }
    $lines = @(Get-Content -LiteralPath (Join-Path $PackageRoot 'business-bundle.sha256'))
    if ($lines.Count -ne 5) { throw '成套摘要必须恰好有五行。' }
    $hashes = [ordered]@{}
    foreach ($line in $lines) {
        if ($line -cnotmatch '^([0-9a-f]{64})  ([A-Za-z0-9./-]+)$') { throw "成套摘要格式无效：$line" }
        $relative = $Matches[2]
        if ($relative -cnotin $components -or $hashes.Contains($relative)) { throw "成套摘要包含未知或重复组件：$relative" }
        $hashes[$relative] = $Matches[1]
        $file = Get-Item -LiteralPath (Join-Path $PackageRoot $relative)
        if ($file.Length -le 0 -or (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -ine $hashes[$relative]) { throw "安装包组件摘要不匹配或为空：$relative" }
    }
    return $hashes
}
function Assert-InstalledBundle([string]$Root) {
    [void](Read-Tree $Root)
    foreach ($relative in $bundleFiles) {
        $path = Join-Path $Root $relative
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "安装后缺少文件：$relative" }
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine $packageHashes[$relative]) { throw "安装后摘要验证失败：$relative" }
    }
}
function Assert-ManagedMutationPath([string]$Path) {
    # 每次目录移动/递归删除前重新验证绝对归属与全部重解析点，不能只依赖初始字符串检查。
    $full = Full-Path $Path
    $transactionParent = [IO.Directory]::GetParent($transactionRoot).FullName
    if (-not $transactionParent.Equals($gameParent, $comparison) -or [IO.Path]::GetFileName($transactionRoot) -cne $transactionLeaf) { throw '事务目录归属校验失败。' }
    if (-not $full.Equals($pluginRoot, $comparison) -and -not $full.Equals($transactionRoot, $comparison) -and -not (Is-Within $full $transactionRoot)) { throw "拒绝管理非本事务目录：$full" }
    Assert-NoReparseAncestors $full
    if (Test-Path -LiteralPath $full) { [void](Read-Tree $full) }
}
function Move-ManagedDirectory([string]$Source, [string]$Destination) {
    Assert-ManagedMutationPath $Source; Assert-ManagedMutationPath $Destination
    if (Test-Path -LiteralPath $Destination) { throw "目录移动目标已经存在：$Destination" }
    Move-Item -LiteralPath $Source -Destination $Destination
}
function Remove-Transaction {
    Assert-ManagedMutationPath $transactionRoot
    if (Test-Path -LiteralPath $transactionRoot) { Remove-Item -LiteralPath $transactionRoot -Recurse -Force }
}
function Save-Record([string]$Name, $Value) {
    [IO.File]::WriteAllText((Join-Path $RecordRoot $Name), ($Value | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
}

$GameRoot = Full-Path $GameRoot; $PackageRoot = Full-Path $PackageRoot
$SaveRoot = Full-Path $SaveRoot; $RecordRoot = Full-Path $RecordRoot
foreach ($path in @($GameRoot, $PackageRoot, $SaveRoot)) { Assert-Directory $path }
Assert-NoReparseAncestors $RecordRoot
if (-not [IO.Directory]::GetParent($RecordRoot).FullName.Equals($workspaceTemp, $comparison) -or
    [IO.Path]::GetFileName($RecordRoot) -cnotmatch '^csharp-install-[0-9a-f]{32}$' -or (Test-Path -LiteralPath $RecordRoot)) {
    throw 'RecordRoot 必须是本仓库 temp 下不存在的 csharp-install-<32位小写GUID> 直接子目录。'
}
$pluginRoot = Join-Path $GameRoot 'BepInEx/plugins/mystia-steward-companion'
Assert-Directory $pluginRoot
if ($PackageRoot.Equals($GameRoot, $comparison) -or (Is-Within $PackageRoot $GameRoot) -or
    $SaveRoot.Equals($pluginRoot, $comparison) -or (Is-Within $SaveRoot $pluginRoot) -or (Is-Within $pluginRoot $SaveRoot) -or
    (Is-Within $RecordRoot $GameRoot) -or (Is-Within $RecordRoot $SaveRoot) -or (Is-Within $RecordRoot $PackageRoot)) { throw '包、记录、存档和插件路径存在不允许的重叠。' }
if ($InjectPostInstallFailure -and (-not $Execute -or -not (Is-Within $GameRoot $workspaceTemp) -or -not (Is-Within $SaveRoot $workspaceTemp) -or -not (Is-Within $PackageRoot $workspaceTemp))) {
    throw '故障注入仅允许 -Execute 且游戏、包、存档均位于本仓库 temp 的人工夹具。'
}
$packageOriginal = Read-Tree $PackageRoot
$manifestHashes = Read-PackageManifest
Assert-SameTree $packageOriginal (Read-Tree $PackageRoot) '读取期间安装包'
$packageHashes = [ordered]@{}
foreach ($relative in $components) { $packageHashes[$relative] = $manifestHashes[$relative] }
$packageHashes['business-bundle.sha256'] = ($packageOriginal['business-bundle.sha256'] -split ':')[2]
Assert-ProcessesStopped
$original = Read-Tree $pluginRoot; $protected = Read-Protection
$gameParent = [IO.Directory]::GetParent($GameRoot).FullName
$transactionLeaf = '.' + [IO.Path]::GetFileName($GameRoot) + '.mystia-install-' + [Guid]::NewGuid().ToString('N')
$transactionRoot = Join-Path $gameParent $transactionLeaf
$stage = Join-Path $transactionRoot 'stage'; $old = Join-Path $transactionRoot 'original'; $failed = Join-Path $transactionRoot 'failed'
Assert-ManagedMutationPath $transactionRoot
if (Test-Path -LiteralPath $transactionRoot) { throw '事务目录已存在。' }
$pendingPrefix = '.' + [IO.Path]::GetFileName($GameRoot) + '.mystia-install-'
$pending = @(Get-ChildItem -LiteralPath $gameParent -Force | Where-Object { $_.Name.StartsWith($pendingPrefix, [StringComparison]::OrdinalIgnoreCase) })
if ($pending.Count -gt 0) { throw "发现未收尾安装事务，必须先审查原件和记录再重试：$(($pending.FullName) -join '；')" }
if (-not $Execute) {
    return [pscustomobject]@{ Mode = 'ValidatedOnly'; GameRoot = $GameRoot; PackageRoot = $PackageRoot; PluginRoot = $pluginRoot;
        SaveRoot = $SaveRoot; RecordRoot = $RecordRoot; PackageFiles = 6; VerifiedComponentHashes = $manifestHashes.Count;
        OriginalEntries = $original.Count; ProtectedEntries = $protected.Count; WritesPerformed = $false }
}

$originalMoved = $false; $restored = $false
[void][IO.Directory]::CreateDirectory($RecordRoot)
Save-Record 'before-plugin.json' $original; Save-Record 'before-protected.json' $protected; Save-Record 'package-hashes.json' $packageHashes
$backup = Join-Path $RecordRoot 'plugin-backup'
Copy-Tree $pluginRoot $backup
Assert-SameTree $original (Read-Tree $backup) '工作区备份'
try {
    [void][IO.Directory]::CreateDirectory($transactionRoot)
    Copy-Tree $pluginRoot $stage
    foreach ($relative in $bundleFiles) {
        $destination = Join-Path $stage $relative
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
        Copy-Item -LiteralPath (Join-Path $PackageRoot $relative) -Destination $destination -Force
    }
    Assert-InstalledBundle $stage
    Assert-SameTree $original (Read-Tree $pluginRoot) '替换前原插件'
    Assert-SameTree $protected (Read-Protection) '替换前保护区'
    Assert-ProcessesStopped
    Move-ManagedDirectory $pluginRoot $old; $originalMoved = $true
    Move-ManagedDirectory $stage $pluginRoot
    if ($InjectPostInstallFailure) { [IO.File]::WriteAllText((Join-Path $pluginRoot 'MystiaStewardCompanion.Business.dll'), 'test-only-forced-corruption') }
    Assert-InstalledBundle $pluginRoot
    $after = Read-Tree $pluginRoot
    foreach ($relative in $original.Keys) {
        if ($relative -notin $bundleFiles -and (-not $after.Contains($relative) -or $original[$relative] -cne $after[$relative])) { throw "原插件附加文件未保留：$relative" }
    }
    Assert-SameTree $protected (Read-Protection) '安装后保护区'
    Save-Record 'after-plugin.json' $after; Save-Record 'after-protected.json' (Read-Protection)
    $result = [ordered]@{ Mode = 'Installed'; GameRoot = $GameRoot; PluginRoot = $pluginRoot; RecordRoot = $RecordRoot;
        BackupRoot = $backup; PackageFiles = 6; VerifiedComponentHashes = 5; ProtectedUnchanged = $true; ExtraFilesPreserved = $true }
    Save-Record 'result.json' $result
}
catch {
    $failure = $_.Exception.Message; $rollbackFailure = ''; $protectedUnchanged = $false
    try {
        if ($originalMoved) {
            if (Test-Path -LiteralPath $pluginRoot) { Move-ManagedDirectory $pluginRoot $failed }
            Move-ManagedDirectory $old $pluginRoot
        }
        Assert-SameTree $original (Read-Tree $pluginRoot) '回退后完整原插件'
        Assert-SameTree $original (Read-Tree $backup) '保留的工作区备份'
        $restored = $true
        Assert-SameTree $protected (Read-Protection) '回退后保护区'; $protectedUnchanged = $true
    }
    catch { $rollbackFailure = $_.Exception.Message }
    Save-Record 'result.json' ([ordered]@{ Mode = 'Failed'; Error = $failure; Restored = $restored; ProtectedUnchanged = $protectedUnchanged;
        RollbackError = $rollbackFailure; GameRoot = $GameRoot; PluginRoot = $pluginRoot; BackupRoot = $backup; TransactionRoot = $transactionRoot })
    if ($restored -and $protectedUnchanged) { Remove-Transaction }
    throw "安装失败：$failure；原插件已验证恢复=$restored；保护区未变=$protectedUnchanged；回退错误=$rollbackFailure；记录=$RecordRoot"
}
# 安装提交已完成，清理旧事务失败不得再对可能已部分清理的原件执行回退；完整工作区备份始终保留。
try { Remove-Transaction }
catch {
    $result['CleanupPending'] = $true; $result['TransactionRoot'] = $transactionRoot; $result['CleanupError'] = $_.Exception.Message
    Save-Record 'result.json' $result
    Write-Warning "新安装和保护区已验证，但事务清理需检查：$transactionRoot。完整原插件备份保留在 $backup。"
}
return [pscustomobject]$result
