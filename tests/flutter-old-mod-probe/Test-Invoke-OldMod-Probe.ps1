#requires -Version 7.0
# Mock game/network with real files and one real PowerShell early-exit child.
param([string]$OutputParent = (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'temp'))
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'Invoke-OldMod-Probe.ps1')

function Assert-Test([bool]$Value, [string]$Message) { if (!$Value) { throw $Message } }
function Write-TestJson([string]$Path, $Value) { [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 16), [Text.UTF8Encoding]::new($false)) }
$testRoot = Join-Path $OutputParent ('old-mod-invoke-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$realHash = ${function:Get-OldModSha256}
$collectorTest = @{}
$acceptedHash = '18c3a381e6f363150209605f65da553680e1e13b250a50eb90c6298d7438357b'

# Shadow operating-system boundaries; the collector's validation/state machine is unchanged.
function Assert-OldModPlatform {}
function Get-OldModExecutionContext { return @{ elevated = $false; sessionId = 1 } }
function Get-OldModSha256([string]$File) {
    if ([IO.File]::ReadAllText($File) -ceq 'READ-ONLY-BOOTSTRAP-MOCK') { return $acceptedHash }
    return & $realHash $File
}
function New-TestProcess([int]$ProcessId, [string]$Executable, [bool]$Updater = $false) {
    $value = [pscustomobject]@{
        Id = $ProcessId; Handle = [IntPtr]1; HasExited = $false; ExitCode = 0
        MainModule = [pscustomobject]@{ FileName = $Executable }; IsUpdater = $Updater
    }
    $value | Add-Member ScriptMethod Refresh {}
    $value | Add-Member ScriptMethod Dispose {}
    $value | Add-Member ScriptMethod WaitForExit {
        param([int]$Milliseconds)
        $collectorTest.waits++
        if ($collectorTest.case -ceq 'timeout') { Start-Sleep -Milliseconds 50; return $false }
        $this.HasExited = $true
        if ($collectorTest.case -ceq 'bootstrap-crash') { $this.ExitCode = 1 }
        if ($collectorTest.case -ceq 'game-exits') { $collectorTest.game.HasExited = $true }
        $diskStatus = @{ state = 'cancelled'; message = 'fixture cancellation'; progress = 0 }
        if ($collectorTest.case -ceq 'false-success') { $diskStatus.state = 'succeeded' }
        if ($collectorTest.case -ceq 'string-progress') { $diskStatus.progress = '0' }
        Write-TestJson $collectorTest.statusPath $diskStatus
        if ($collectorTest.case -ceq 'plugin-mutated') { [IO.File]::WriteAllText((Join-Path $collectorTest.manifest.pluginDirectory 'MystiaStewardCompanion.BepInEx.dll'), 'changed') }
        if ($collectorTest.case -ceq 'extra-plugin-file') { [IO.File]::WriteAllText((Join-Path $collectorTest.manifest.pluginDirectory 'extra.txt'), 'extra') }
        if ($collectorTest.case -ceq 'backup-created') { New-Item -ItemType Directory -Path $collectorTest.backup | Out-Null }
        return $true
    }
    return $value
}
function Start-OldModGame([string]$Executable, [string]$Directory) {
    $collectorTest.launches++
    if ($collectorTest.case -ceq 'early-child-exit') {
        # Real OS process/handle/exit code; this child never loads the fixture game.
        $powershellName = if ($IsWindows) { 'pwsh.exe' } else { 'pwsh' }
        $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $PSHOME $powershellName))
        $start.UseShellExecute = $false
        foreach ($argument in @('-NoLogo', '-NoProfile', '-NonInteractive', '-Command', 'exit 17')) { $start.ArgumentList.Add($argument) }
        $collectorTest.game = [Diagnostics.Process]::Start($start)
        Assert-Test ($collectorTest.game.WaitForExit(10000)) 'The real early-exit fixture did not finish.'
        $collectorTest.realChildId = $collectorTest.game.Id
        return $collectorTest.game
    }
    $collectorTest.game = New-TestProcess 4100 $Executable
    if ($collectorTest.case -ceq 'steam-restart') { $collectorTest.game.HasExited = $true }
    if ($collectorTest.case -ceq 'wrong-game-path') { $collectorTest.game.MainModule.FileName = Join-Path $testRoot 'original-game.exe' }
    return $collectorTest.game
}
function Get-OldModListeners([int]$Port) {
    if ($collectorTest.launches -eq 0) {
        if ($collectorTest.case -ceq 'occupied-port') { return @([pscustomobject]@{ LocalPort = $Port; LocalAddress = '127.0.0.1'; OwningProcess = 999 }) }
        if ($collectorTest.case -ceq 'steam-file-before-launch') {
            [IO.File]::WriteAllText($collectorTest.manifest.steamIdentity.developmentFilePath, '480')
        }
        return @()
    }
    $owner = if ($collectorTest.case -ceq 'wrong-listener') { 999 } else { 4100 }
    $address = if ($collectorTest.case -ceq 'wildcard-listener') { '0.0.0.0' } else { '127.0.0.1' }
    $listener = [pscustomobject]@{ LocalPort = $Port; LocalAddress = $address; OwningProcess = $owner }
    if ($collectorTest.case -ceq 'duplicate-listener') { return @($listener, $listener) }
    return @($listener)
}
function New-OldModHttpClient {
    $value = [pscustomobject]@{ kind = 'mock-http' }
    $value | Add-Member ScriptMethod Dispose {}
    return $value
}
function New-TestStatus([string]$InstallState) {
    return @{ ok = $true; currentVersion = '1.3.1'; enabled = $true; autoCheck = $false
        downloadedVersion = '1.3.2-preview.1'; staged = $true; installState = $InstallState; error = $null }
}
function Send-OldModHttp($Client, [int]$Port, [string]$Route, [string]$Token) {
    $collectorTest.routes.Add($Route)
    if ($Route -ceq '/health') {
        if ($collectorTest.case -ceq 'health-transient' -and @($collectorTest.routes | Where-Object { $_ -ceq '/health' }).Count -eq 1) {
            throw [Net.Http.HttpRequestException]::new('Mock API listener not ready yet.')
        }
        if ($collectorTest.case -ceq 'plugin-changed-before-install') {
            [IO.File]::WriteAllText((Join-Path $collectorTest.manifest.pluginDirectory 'MystiaStewardCompanion.BepInEx.dll'), 'changed during startup')
        }
        $version = if ($collectorTest.case -ceq 'wrong-mod-version') { '1.3.2' } else { '1.3.1' }
        return @{ ok = $true; pluginVersion = $version; port = $Port; bindAddress = '127.0.0.1'; authRequired = $true; lanEnabled = $false }
    }
    Assert-Test ($Token -ceq ('a' * 64)) 'Collector did not read the isolated token.'
    if ($Route -ceq '/updates/install-on-exit') {
        $collectorTest.posts++
        if ($collectorTest.case -ceq 'install-timeout') { throw "Mock transport unknown result; private token $Token" }
        $runnerDirectory = Join-Path $collectorTest.manifest.updatesDirectory 'runner/20261007123456'
        New-Item -ItemType Directory -Path $runnerDirectory, (Join-Path $collectorTest.manifest.updatesDirectory 'backups') | Out-Null
        $collectorTest.runner = Join-Path $runnerDirectory 'mystia-steward-companion-updater.exe'
        Copy-Item -LiteralPath (Join-Path $collectorTest.manifest.stagedDirectory 'mystia-steward-companion-updater.exe') -Destination $collectorTest.runner
        $collectorTest.backup = Join-Path $collectorTest.manifest.updatesDirectory 'backups/mystia-steward-companion-1.3.1-20261007123456'
        $collectorTest.disk.installProcessId = 4200
        $collectorTest.disk.installState = 'waiting'
        Write-TestJson $collectorTest.statePath $collectorTest.disk
        Write-TestJson $collectorTest.statusPath @{ state = 'waiting'; message = 'fixture waiting'; progress = 0 }
        $collectorTest.updater = New-TestProcess 4200 $collectorTest.runner $true
        if ($collectorTest.case -ceq 'wrong-runner-hash') { [IO.File]::WriteAllText($collectorTest.runner, 'REAL-UPDATER-NOT-ALLOWED') }
        return New-TestStatus 'waiting'
    }
    if ($collectorTest.posts -eq 0) {
        if ($collectorTest.case -ceq 'status-timeout' -or
            ($collectorTest.case -ceq 'status-transient' -and @($collectorTest.routes | Where-Object { $_ -ceq '/updates/status' }).Count -eq 1)) {
            throw [Net.Http.HttpRequestException]::new('Mock read-only update status is temporarily unavailable.')
        }
        $status = New-TestStatus ''
        if ($collectorTest.case -ceq 'string-ok') { $status.ok = 'True' }
        return $status
    }
    if ($collectorTest.waits -eq 0) { return New-TestStatus 'waiting' }
    if ($collectorTest.case -cne 'uncleared-pid') { $collectorTest.disk.installProcessId = 0 }
    $collectorTest.disk.installState = 'cancelled'
    Write-TestJson $collectorTest.statePath $collectorTest.disk
    return New-TestStatus 'cancelled'
}
function Get-OldModProcess([int]$ProcessId) { Assert-Test ($ProcessId -eq 4200) 'Unexpected PID lookup.'; return $collectorTest.updater }
function Get-OldModCimProcess([int]$ProcessId) {
    return @([pscustomobject]@{
        ParentProcessId = if ($collectorTest.case -ceq 'wrong-parent') { 999 } else { 4100 }
        ExecutablePath = $collectorTest.runner; CommandLine = 'mock command line, decoded by the mock native boundary'
    })
}
function ConvertFrom-OldModCommandLine([string]$CommandLine) {
    $plugin = if ($collectorTest.case -ceq 'foreign-plugin-argument') { Join-Path $testRoot 'original-plugin' } else { $collectorTest.manifest.pluginDirectory }
    return @($collectorTest.runner, '--game-pid', '4100', '--plugin-dir', $plugin, '--staged-dir', $collectorTest.manifest.stagedDirectory,
        '--backup-dir', $collectorTest.backup, '--status-file', $collectorTest.statusPath, '--control-port', '32146')
}

$cases = @('passed', 'health-transient', 'status-transient', 'status-timeout', 'prepare-failed', 'missing-prepare-report', 'changed-source-snapshot', 'initial-active-pid',
    'plugin-changed-before-install', 'string-ok', 'manifest-outside', 'wrong-bootstrap', 'changed-frozen-plugin', 'occupied-port', 'steam-restart', 'early-child-exit',
    'wrong-game-path', 'wrong-listener', 'wildcard-listener', 'duplicate-listener', 'wrong-mod-version', 'install-timeout',
    'wrong-parent', 'wrong-runner-hash', 'foreign-plugin-argument', 'bootstrap-crash', 'false-success', 'string-progress',
    'plugin-mutated', 'extra-plugin-file', 'backup-created', 'uncleared-pid', 'game-exits', 'timeout',
    'steam-identity-lf', 'steam-identity-bare', 'steam-identity-crlf', 'steam-file-mutated', 'steam-file-before-launch',
    'steam-wrong-appid', 'steam-wrong-source', 'steam-wrong-steamapps', 'steam-prepare-mismatch', 'steam-wrong-build-type', 'steam-wrong-file-path',
    'steam-content-appid', 'steam-missing-identity', 'steam-unrecorded-file')
$checks = [Collections.Generic.List[object]]::new()
$savedAutomation = [Environment]::GetEnvironmentVariable('MYSTIA_UPDATER_PROBE_AUTOMATION')
try {
    [Environment]::SetEnvironmentVariable('MYSTIA_UPDATER_PROBE_AUTOMATION', $null)
    foreach ($case in $cases) {
        $root = Join-Path $testRoot $case
        $game = Join-Path $root 'game'
        $plugin = Join-Path $game 'BepInEx/plugins/mystia-steward-companion'
        $updates = Join-Path $game 'BepInEx/config/MystiaStewardCompanion/updates'
        $staged = Join-Path $root 'staging/mystia-steward-companion'
        New-Item -ItemType Directory -Path $plugin, $updates, $staged, (Join-Path $root 'evidence') | Out-Null
        $exe = Join-Path $game 'Touhou Mystia Izakaya.exe'
        $dll = Join-Path $plugin 'MystiaStewardCompanion.BepInEx.dll'
        [IO.File]::WriteAllText($exe, 'game fixture, never executed')
        [IO.File]::WriteAllText($dll, 'old Mod fixture, never loaded')
        [IO.File]::WriteAllText((Join-Path $staged 'mystia-steward-companion-updater.exe'), 'READ-ONLY-BOOTSTRAP-MOCK')
        $config = Join-Path $game 'BepInEx/config/com.tyukki.mystia-steward-companion.cfg'
        [IO.File]::WriteAllText($config, "[LocalApi]`nEnabled = true`nAllowLanConnections = false`nPort = 32155`nToken = $('a' * 64)`n[Companion]`nAutoLaunch = false`n[Updates]`nEnabled = true`nAutoCheck = false`n")
        $manifest = @{
            schemaVersion = 1; kind = 'old-mod-launch-fixture'; sourceGameDirectory = (Join-Path $testRoot 'steamapps/common/Touhou Mystia Izakaya')
            gameDirectory = $game; gameExecutable = $exe; pluginDirectory = $plugin; stagedDirectory = $staged
            updatesDirectory = $updates; configPath = $config; evidenceDirectory = (Join-Path $root 'evidence')
            expectedModVersion = '1.3.1'; testDownloadedVersion = '1.3.2-preview.1'; port = 32155
            bootstrapSha256 = $acceptedHash; bootstrapBuildCommit = '283bd56cd10564d64169a8ea521f9fdffe0019b4'
            pluginFiles = @(@{ path = 'MystiaStewardCompanion.BepInEx.dll'; size = (Get-Item $dll).Length; sha256 = (& $realHash $dll) })
            originalPluginDllSha256 = (& $realHash $dll); gameExecutableSha256 = (& $realHash $exe); steamIdentity = $null
        }
        $prepared = @{ result = 'prepared'; sourceUnchanged = $true; copiedPluginUnchanged = $true; steamIdentity = $null }
        if ($case.StartsWith('steam-', [StringComparison]::Ordinal) -and $case -cne 'steam-restart') {
            $steamFile = Join-Path $game 'steam_appid.txt'
            $steamText = switch ($case) {
                'steam-identity-bare' { '1584090' }
                'steam-identity-crlf' { "1584090`r`n" }
                'steam-content-appid' { "480`n" }
                default { "1584090`n" }
            }
            [IO.File]::WriteAllText($steamFile, $steamText, [Text.Encoding]::ASCII)
            $manifest.steamIdentity = @{
                appId = '1584090'; buildId = '23316742'; installDirectory = 'Touhou Mystia Izakaya'
                sourceManifestPath = (Join-Path $testRoot 'steamapps/appmanifest_1584090.acf'); sourceManifestSha256 = ('b' * 64)
                developmentFilePath = $steamFile; developmentFileSha256 = (& $realHash $steamFile)
            }
            if ($case -ceq 'steam-wrong-appid') { $manifest.steamIdentity.appId = '480' }
            if ($case -ceq 'steam-wrong-source') { $manifest.steamIdentity.sourceManifestPath = Join-Path $testRoot 'other/appmanifest_1584090.acf' }
            if ($case -ceq 'steam-wrong-steamapps') {
                $manifest.steamIdentity.sourceManifestPath = Join-Path $testRoot 'other/appmanifest_1584090.acf'
                $manifest.sourceGameDirectory = Join-Path $testRoot 'other/common/Touhou Mystia Izakaya'
            }
            if ($case -ceq 'steam-wrong-build-type') { $manifest.steamIdentity.buildId = 23316742 }
            if ($case -ceq 'steam-wrong-file-path') { $manifest.steamIdentity.developmentFilePath = $exe }
            $prepared.steamIdentity = $manifest.steamIdentity.Clone()
            if ($case -ceq 'steam-prepare-mismatch') { $prepared.steamIdentity.buildId = '1' }
            if ($case -ceq 'steam-file-mutated') { [IO.File]::WriteAllText($steamFile, '1584090', [Text.Encoding]::ASCII) }
            if ($case -ceq 'steam-missing-identity') { $manifest.Remove('steamIdentity') }
            if ($case -ceq 'steam-unrecorded-file') { $manifest.steamIdentity = $null; $prepared.steamIdentity = $null }
        }
        Write-TestJson (Join-Path $root 'evidence/prepare-report.json') $prepared
        Write-TestJson (Join-Path $root 'evidence/source-snapshot.json') @{ kind = 'mock-source-snapshot' }
        $manifest.sourceSnapshotSha256 = & $realHash (Join-Path $root 'evidence/source-snapshot.json')
        $disk = @{ state = 'downloaded'; downloadedVersion = '1.3.2-preview.1'; stagedDirectory = $staged }
        $collectorTest.Clear()
        $collectorTest.case = $case; $collectorTest.launches = 0; $collectorTest.posts = 0; $collectorTest.waits = 0
        $collectorTest.manifest = $manifest; $collectorTest.disk = $disk; $collectorTest.routes = [Collections.Generic.List[string]]::new()
        $collectorTest.statePath = Join-Path $updates 'update-state.json'; $collectorTest.statusPath = Join-Path $updates 'install-status.json'
        if ($case -ceq 'manifest-outside') { $manifest.pluginDirectory = Join-Path $testRoot 'original' }
        if ($case -ceq 'wrong-bootstrap') { [IO.File]::WriteAllText((Join-Path $staged 'mystia-steward-companion-updater.exe'), 'REAL-UPDATER-NOT-ALLOWED') }
        if ($case -ceq 'changed-frozen-plugin') { [IO.File]::WriteAllText($dll, 'changed') }
        Write-TestJson (Join-Path $root 'probe-workspace.json') $manifest
        if ($case -ceq 'prepare-failed') { Write-TestJson (Join-Path $root 'evidence/prepare-failure.json') @{ result = 'failed' } }
        if ($case -ceq 'missing-prepare-report') { Remove-Item -LiteralPath (Join-Path $root 'evidence/prepare-report.json') }
        if ($case -ceq 'changed-source-snapshot') { Write-TestJson (Join-Path $root 'evidence/source-snapshot.json') @{ kind = 'changed' } }
        if ($case -ceq 'initial-active-pid') { $disk.installProcessId = 999 }
        Write-TestJson $collectorTest.statePath $disk
        $caught = $null
        $terminalPath = Join-Path $root 'terminal.log'
        $nodeId = if ($case -ceq 'passed') { 'node_20261007-1' } else { '' }
        try { Invoke-OldModProbeMain $root 1 1 $nodeId *> $terminalPath } catch { $caught = $_.Exception.Message }
        $report = Get-Content -LiteralPath (Join-Path $root 'evidence/old-mod-probe-report.json') -Raw -Encoding UTF8 | ConvertFrom-Json -AsHashtable
        $expectedPass = $case -in @('passed', 'health-transient', 'status-transient', 'steam-identity-lf', 'steam-identity-bare', 'steam-identity-crlf')
        Assert-Test ($expectedPass -eq ($null -eq $caught)) "$case returned an unexpected result: $caught / $($report.errors -join '; ')"
        Assert-Test ($expectedPass -eq ($report.result -ceq 'passed')) "$case has an incorrect report result."
        Assert-Test ($collectorTest.posts -le 1) "$case replayed installation."
        $allEvidence = (Get-Content -LiteralPath (Join-Path $root 'evidence/old-mod-probe-report.json') -Raw) + (Get-Content -LiteralPath $terminalPath -Raw)
        Assert-Test (!$allEvidence.Contains('a' * 64)) "$case leaked an API token."
        if ($case -in @('steam-restart', 'early-child-exit', 'wrong-game-path', 'wrong-listener', 'wildcard-listener', 'duplicate-listener', 'wrong-mod-version',
                'prepare-failed', 'missing-prepare-report', 'changed-source-snapshot', 'initial-active-pid', 'plugin-changed-before-install', 'string-ok', 'status-timeout')) {
            Assert-Test ($collectorTest.posts -eq 0) "$case sent an install request without game identity/readiness."
        }
        if ($case -in @('steam-restart', 'early-child-exit')) {
            $expectedExit = if ($case -ceq 'early-child-exit') { 17 } else { 0 }
            Assert-Test ($report.gameHasExited -ceq $true -and $report.gameExitCode -eq $expectedExit -and !$report.gameStillRunning) 'Early game exit lost its exact exit code or was reported as running.'
            Assert-Test ($collectorTest.routes.Count -eq 0 -and $collectorTest.posts -eq 0) 'Early exit reached an API operation.'
            if ($case -ceq 'early-child-exit') { Assert-Test ($report.gameProcessId -eq $collectorTest.realChildId) 'Real child exit evidence was not tied to the retained launch PID.' }
        }
        if ($case.StartsWith('steam-', [StringComparison]::Ordinal) -and !$expectedPass -and $case -cne 'steam-restart') {
            Assert-Test ($collectorTest.launches -eq 0 -and $collectorTest.posts -eq 0 -and $collectorTest.routes.Count -eq 0) 'Invalid Steam identity reached process launch or HTTP.'
            Assert-Test ($report.errors -join ' ' -match 'Steam') 'Steam identity fixture failed for an unrelated reason.'
        }
        if ($expectedPass) {
            Assert-Test ($report.gameHasExited -ceq $false -and $null -eq $report.gameExitCode) 'A running game was assigned an exit code.'
            if ($case.StartsWith('steam-identity-', [StringComparison]::Ordinal)) {
                Assert-Test ($report.steamIdentity.appId -ceq '1584090' -and $report.steamIdentity.developmentFileSha256 -ceq $manifest.steamIdentity.developmentFileSha256) 'Steam identity was not preserved in execution evidence.'
            }
        }
        if ($case -ceq 'install-timeout') { Assert-Test ($collectorTest.posts -eq 1) 'Unknown write result was replayed or never attempted.' }
        if ($case -ceq 'passed') {
            Assert-Test ($report.bootstrapExitCode -eq 0 -and $report.modInstallProcessId -eq 0 -and $report.gameStillRunning -and $report.pluginUnchanged -and $report.backupAbsent) 'Pass report omitted required evidence.'
            $observed = Get-Content -LiteralPath (Join-Path $root 'evidence/waiting-observed.json') -Raw | ConvertFrom-Json -AsHashtable
            Assert-Test ($observed.kind -ceq 'old-mod-waiting-observed' -and $observed.nodeRunId -ceq $nodeId -and $observed.gamePid -eq 4100 -and $observed.bootstrapPid -eq 4200) 'Observed waiting marker is not bound to this run and its exact processes.'
            Assert-Test ($report.nodeRunId -ceq $nodeId -and $report.executionMode -ceq 'controlled-desktop-automation') 'Controlled automation is not labeled in the report.'
            $second = $null
            try { Invoke-OldModProbeMain $root 1 1 *> $null } catch { $second = $_.Exception.Message }
            Assert-Test ($second -match 'already has probe evidence' -and $collectorTest.launches -eq 1 -and $collectorTest.posts -eq 1) 'A used workspace could be invoked again.'
        }
        $checks.Add(@{ name = $case; passed = $true })
    }
    [Environment]::SetEnvironmentVariable('MYSTIA_UPDATER_PROBE_AUTOMATION', 'cancel-after-ready')
    $errorText = $null
    try { Invoke-OldModProbeMain (Join-Path $testRoot 'unused') 1 1 } catch { $errorText = $_.Exception.Message }
    Assert-Test ($errorText -match 'requires a manual cancellation') 'Inherited automation was accepted.'
    $checks.Add(@{ name = 'inherited-automation-rejected'; passed = $true })
    foreach ($invalidJson in @('{"state":"waiting","state":"cancelled"}', '{"state":"waiting","State":"cancelled"}')) {
        $rejected = $false
        try { $null = ConvertFrom-OldModJson $invalidJson } catch { $rejected = $true }
        Assert-Test $rejected 'Duplicate JSON state field was accepted.'
    }
    $checks.Add(@{ name = 'duplicate-json-rejected'; passed = $true })
} finally { [Environment]::SetEnvironmentVariable('MYSTIA_UPDATER_PROBE_AUTOMATION', $savedAutomation) }
Write-TestJson (Join-Path $testRoot 'test-report.json') @{ kind = 'mock-game-network-real-files-and-child-exit'; passed = $true; checks = $checks.ToArray() }
Write-Host "$($checks.Count) collector checks passed (mock game/network, real files and PowerShell child exit). No game, updater, TCP connection or installation was started. Evidence: $testRoot"
