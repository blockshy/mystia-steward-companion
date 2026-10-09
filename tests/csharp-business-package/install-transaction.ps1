#requires -Version 7.0
<#
在本仓库 temp 创建全人工游戏、核心、配置、存档及包，运行真实公共安装脚本。
保留证据目录供审查；绝不读取或写入实际游戏目录，也不启动任何游戏/伴随程序。
#>
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$temp = Join-Path $repository 'temp'
$fixture = Join-Path $temp ('csharp-install-test-' + [Guid]::NewGuid().ToString('N'))
$installer = Join-Path $repository 'mods/bepinex/tools/install-local-package.ps1'
$components = @('MystiaStewardCompanion.BepInEx.dll', 'MystiaStewardCompanion.Contracts.dll', 'MystiaStewardCompanion.Business.dll',
    'companion/mystia-steward-companion.exe', 'mystia-steward-companion-updater.exe')
$checks = 0
function Check([bool]$Condition, [string]$Message) { $script:checks++; if (-not $Condition) { throw $Message } }
function Write-Fixture([string]$Path, [string]$Value) {
    if (-not ([IO.Path]::GetFullPath($Path).StartsWith($fixture + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase))) { throw '拒绝写入夹具目录之外。' }
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path))
    [IO.File]::WriteAllText($Path, $Value, [Text.UTF8Encoding]::new($false))
}
function Snapshot([string]$Path) {
    $rows = @(Get-ChildItem -LiteralPath $Path -File -Recurse -Force | Sort-Object FullName | ForEach-Object {
        [IO.Path]::GetRelativePath($Path, $_.FullName).Replace('\', '/') + ':' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    })
    return $rows -join "`n"
}
function Build-Package([string]$Name) {
    $package = Join-Path $fixture $Name
    foreach ($relative in $components) { Write-Fixture (Join-Path $package $relative) ('new-' + $relative) }
    $lines = @($components | ForEach-Object { (Get-FileHash -LiteralPath (Join-Path $package $_) -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $_ })
    Write-Fixture (Join-Path $package 'business-bundle.sha256') (($lines -join "`n") + "`n")
    return $package
}
function Build-Game([string]$Name) {
    $game = Join-Path $fixture $Name
    foreach ($relative in @('fixture-game.exe', 'GameAssembly.dll', 'UnityPlayer.dll', 'doorstop_config.ini',
        'BepInEx/core/BepInEx.Core.dll', 'BepInEx/config/MystiaStewardCompanion.cfg', 'BepInEx/config/MystiaStewardCompanion/favorites.json',
        'BepInEx/plugins/other-plugin/keep.dll', 'BepInEx/plugins/mystia-steward-companion/MystiaStewardCompanion.BepInEx.dll',
        'BepInEx/plugins/mystia-steward-companion/companion/mystia-steward-companion.exe',
        'BepInEx/plugins/mystia-steward-companion/mystia-steward-companion-updater.exe',
        'BepInEx/plugins/mystia-steward-companion/update-status.json', 'BepInEx/plugins/mystia-steward-companion/extra/note.txt')) {
        Write-Fixture (Join-Path $game $relative) ('old-' + $relative)
    }
    return $game
}
function New-Record { return Join-Path $temp ('csharp-install-' + [Guid]::NewGuid().ToString('N')) }

$package = Build-Package 'package'
$saves = Join-Path $fixture 'saves'
Write-Fixture (Join-Path $saves 'slot1.save') 'valuable-save-content'
$saveBefore = Snapshot $saves
$game = Build-Game 'game-success'
$plugin = Join-Path $game 'BepInEx/plugins/mystia-steward-companion'
$before = Snapshot $game; $beforePlugin = Snapshot $plugin; $record = New-Record
$preview = & $installer -GameRoot $game -PackageRoot $package -SaveRoot $saves -RecordRoot $record
Check ($preview.Mode -eq 'ValidatedOnly' -and -not $preview.WritesPerformed) '默认模式没有报告只读校验。'
Check (-not (Test-Path -LiteralPath $record)) '默认校验创建了记录目录。'
Check ((Snapshot $game) -ceq $before) '默认校验修改了假游戏。'
Check (@(Get-ChildItem -LiteralPath $fixture -Directory -Force | Where-Object Name -Like '*.mystia-install-*').Count -eq 0) '默认校验创建了事务目录。'
$installed = & $installer -GameRoot $game -PackageRoot $package -SaveRoot $saves -RecordRoot $record -Execute
Check ($installed.Mode -eq 'Installed' -and $installed.ProtectedUnchanged -and $installed.ExtraFilesPreserved) '成功安装未完成全部验证。'
foreach ($relative in $components + @('business-bundle.sha256')) {
    Check ((Get-FileHash -LiteralPath (Join-Path $plugin $relative)).Hash -eq (Get-FileHash -LiteralPath (Join-Path $package $relative)).Hash) "安装组件不一致：$relative"
}
Check ([IO.File]::ReadAllText((Join-Path $plugin 'update-status.json')) -ceq 'old-BepInEx/plugins/mystia-steward-companion/update-status.json') 'update-status 未保留。'
Check ([IO.File]::ReadAllText((Join-Path $plugin 'extra/note.txt')) -ceq 'old-BepInEx/plugins/mystia-steward-companion/extra/note.txt') '原插件额外文件未保留。'
Check ((Snapshot (Join-Path $record 'plugin-backup')) -ceq $beforePlugin) '工作区原件备份与完整原插件不一致。'
Check ((Snapshot $saves) -ceq $saveBefore) '成功安装修改了存档。'

$missingPackage = Build-Package 'package-missing'
Remove-Item -LiteralPath (Join-Path $missingPackage 'MystiaStewardCompanion.Business.dll')
$missingGame = Build-Game 'game-missing'; $missingBefore = Snapshot $missingGame; $missingRecord = New-Record
$rejected = $false
try { & $installer -GameRoot $missingGame -PackageRoot $missingPackage -SaveRoot $saves -RecordRoot $missingRecord -Execute | Out-Null }
catch { $rejected = $_.Exception.Message -like '*六个文件*' }
Check ($rejected -and -not (Test-Path -LiteralPath $missingRecord)) '缺少业务DLL未在任何写入前拒绝。'
Check ((Snapshot $missingGame) -ceq $missingBefore) '缺包拒绝修改了假游戏。'

$rollbackGame = Build-Game 'game-rollback'; $rollbackBefore = Snapshot $rollbackGame; $rollbackRecord = New-Record
$rejected = $false
try { & $installer -GameRoot $rollbackGame -PackageRoot $package -SaveRoot $saves -RecordRoot $rollbackRecord -Execute -InjectPostInstallFailure | Out-Null }
catch { $rejected = $_.Exception.Message -like '*安装后摘要验证失败*' }
Check $rejected '注入的安装后摘要错误没有触发回退。'
Check ((Snapshot $rollbackGame) -ceq $rollbackBefore) '失败回退后假游戏完整内容不一致。'
$rollbackReport = Get-Content -LiteralPath (Join-Path $rollbackRecord 'result.json') -Raw | ConvertFrom-Json
Check ($rollbackReport.Restored -and $rollbackReport.ProtectedUnchanged -and $rollbackReport.RollbackError -eq '') '回退报告未确认完整原件及保护区。'
Check ((Snapshot $saves) -ceq $saveBefore) '失败回退修改了存档。'
Check (Test-Path -LiteralPath (Join-Path $rollbackRecord 'plugin-backup/MystiaStewardCompanion.BepInEx.dll')) '失败后未保留工作区备份。'
Check (@(Get-ChildItem -LiteralPath $fixture -Directory -Force | Where-Object Name -Like '*.mystia-install-*').Count -eq 0) '已验证事务未清理同级暂存目录。'

# 摘要变造和额外文件必须拒绝；不能以“包含所有必需文件”代替精确六文件集合。
$tampered = Build-Package 'package-tampered'
Write-Fixture (Join-Path $tampered 'MystiaStewardCompanion.Business.dll') 'tampered'
$rejected = $false
try { & $installer -GameRoot $missingGame -PackageRoot $tampered -SaveRoot $saves -RecordRoot (New-Record) | Out-Null } catch { $rejected = $_.Exception.Message -like '*摘要不匹配*' }
Check $rejected '组件内容变造没有拒绝。'
$extra = Build-Package 'package-extra'; Write-Fixture (Join-Path $extra 'unexpected.dll') 'must-not-install'
$rejected = $false
try { & $installer -GameRoot $missingGame -PackageRoot $extra -SaveRoot $saves -RecordRoot (New-Record) | Out-Null } catch { $rejected = $_.Exception.Message -like '*六个文件*' }
Check $rejected '安装包额外DLL没有拒绝。'
$rejected = $false
try { & $installer -GameRoot $missingGame -PackageRoot $package -SaveRoot $saves -RecordRoot $fixture | Out-Null } catch { $rejected = $_.Exception.Message -like '*RecordRoot*' }
Check $rejected '非独占记录路径没有拒绝。'

# 只构造链接指向另一个人工目录，不涉及真实路径；拒绝后仅移除精确链接本身。
$link = Join-Path $missingGame 'BepInEx/plugins/mystia-steward-companion/linked-save'
New-Item -ItemType Junction -Path $link -Target $saves | Out-Null
try {
    $rejected = $false; $linkRecord = New-Record
    try { & $installer -GameRoot $missingGame -PackageRoot $package -SaveRoot $saves -RecordRoot $linkRecord -Execute | Out-Null }
    catch { $rejected = $_.Exception.Message -like '*重解析*' }
    Check ($rejected -and -not (Test-Path -LiteralPath $linkRecord)) '原插件重解析点未在写入前拒绝。'
    Check ((Snapshot $saves) -ceq $saveBefore) '拒绝链接时修改了其目标目录。'
}
finally {
    $item = Get-Item -LiteralPath $link -Force
    if (-not $item.FullName.StartsWith($fixture + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) { throw '测试链接清理归属验证失败。' }
    Remove-Item -LiteralPath $link -Force
}
$pending = Join-Path $fixture ('.game-missing.mystia-install-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($pending)
$rejected = $false
try { & $installer -GameRoot $missingGame -PackageRoot $package -SaveRoot $saves -RecordRoot (New-Record) | Out-Null }
catch { $rejected = $_.Exception.Message -like '*未收尾安装事务*' }
Check $rejected '旧事务尚未审查时允许了新事务。'
# 此人工残留为空目录，单目录删除不递归，不调用任何真实事务清理。
if (-not $pending.StartsWith($fixture + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw '测试残留目录归属校验失败。' }
[IO.Directory]::Delete($pending, $false)
$runningGame = Build-Game 'game-process'
Write-Fixture (Join-Path $runningGame 'pwsh.exe') 'fake-name-only-never-executed'
$rejected = $false
try { & $installer -GameRoot $runningGame -PackageRoot $package -SaveRoot $saves -RecordRoot (New-Record) | Out-Null }
catch { $rejected = $_.Exception.Message -like '*仍在运行*' }
Check $rejected '与当前PowerShell同名的人工游戏EXE未触发进程拒绝。'

$summary = [ordered]@{ PassedAssertions = $checks; FixtureRoot = $fixture; SuccessfulInstallRecord = $record; RollbackRecord = $rollbackRecord; RealGameTouched = $false }
[IO.File]::WriteAllText((Join-Path $fixture 'test-result.json'), ($summary | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
Write-Output ($summary | ConvertTo-Json -Compress)
