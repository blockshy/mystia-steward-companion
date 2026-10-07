# Preparation only: this script never starts a game, updater, or network request.
#requires -Version 7.0
param(
    [string]$SourceGameDirectory = 'E:\SteamLibrary\steamapps\common\Touhou Mystia Izakaya',
    [Parameter(Mandatory = $true)][string]$WorkspaceDirectory,
    [Parameter(Mandatory = $true)][string]$ProbeDirectory,
    [ValidateRange(1024, 65535)][int]$Port = 32155,
    [string]$SteamAppManifestPath = ''
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'Prepare-OldMod-Probe.Common.psm1') -Force -DisableNameChecking
if ($Port -eq 32146) { throw 'The dedicated API port must differ from the fixed companion control port 32146.' }

$source = Resolve-OldModPath $SourceGameDirectory
$workspace = Resolve-OldModPath $WorkspaceDirectory -NewDirectory
$probe = Resolve-OldModPath $ProbeDirectory
foreach ($pair in @(@($source, $workspace), @($source, $probe), @($workspace, $probe))) {
    if (Test-OldModOverlap $pair[0] $pair[1]) { throw 'Source, probe package and workspace paths must be disjoint.' }
}
$accepted = Assert-OldModAcceptedProbe $probe
$steamIdentity = $null
if ($SteamAppManifestPath -cne '') { $steamIdentity = Read-OldModSteamIdentity $SteamAppManifestPath $source }
$sourceAppIdFiles = @(Get-ChildItem -LiteralPath $source -Force | Where-Object Name -IEQ 'steam_appid.txt')
if ($sourceAppIdFiles.Count -gt 1) { throw 'Source Steam development file identity is ambiguous.' }
if ($sourceAppIdFiles.Count -eq 1) {
    if (!$steamIdentity) { throw 'An existing source steam_appid.txt requires an explicit -SteamAppManifestPath.' }
    if ($sourceAppIdFiles[0].Name -cne 'steam_appid.txt') { throw 'Source Steam development filename must use canonical casing.' }
    Assert-OldModSteamAppIdFile $sourceAppIdFiles[0].FullName
}
$sourcePlugin = Join-Path $source 'BepInEx/plugins/mystia-steward-companion'
$dll = Join-Path $sourcePlugin 'MystiaStewardCompanion.BepInEx.dll'
if ((Get-OldModPluginVersion $dll) -cne '1.3.1') { throw 'The installed plugin must declare PluginVersion 1.3.1.' }
$version = [Diagnostics.FileVersionInfo]::GetVersionInfo($dll)
[void](Assert-OldModFile (Join-Path $source 'Touhou Mystia Izakaya.exe'))
[void](Assert-OldModFile (Join-Path $source 'doorstop_config.ini'))
[void](Assert-OldModFile (Join-Path $source 'winhttp.dll'))
[void](Assert-OldModFile (Join-Path $source 'BepInEx/core/BepInEx.Unity.IL2CPP.dll'))
[void](Assert-OldModFile (Join-Path $sourcePlugin 'companion/mystia-steward-companion.exe'))
[void](Assert-OldModFile (Join-Path $sourcePlugin 'mystia-steward-companion-updater.exe'))
Write-Host 'Close the source game before preparation. Source files are only read; no program will be started.'
Write-Host 'Freezing source hashes; changes during preparation will fail and preserve the workspace.'
$frozen = Get-OldModSnapshot $source
Assert-OldModLaunchConfiguration $source $frozen
$gameSnapshot = Select-OldModGameSnapshot $frozen
$pluginSnapshot = Get-OldModSnapshot $sourcePlugin
[long]$copyBytes = $gameSnapshot.totalBytes + $pluginSnapshot.totalBytes + (Get-Item -LiteralPath $accepted.executable).Length
if ($steamIdentity -and @($gameSnapshot.files | Where-Object path -IEQ 'steam_appid.txt').Count -eq 0) { $copyBytes += 8 }
[long]$reserve = [Math]::Max(1GB, [Math]::Ceiling($copyBytes * 0.1))
[long]$requiredBytes = $copyBytes + $reserve
$drive = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($workspace))
Write-Host "Copy budget: $copyBytes bytes; reserved headroom: $reserve bytes; free space: $($drive.AvailableFreeSpace) bytes."
Assert-OldModDiskBudget $drive.AvailableFreeSpace $requiredBytes
[void](Resolve-OldModPath $workspace -NewDirectory)
New-Item -ItemType Directory -Path $workspace -ErrorAction Stop | Out-Null
$evidence = Join-Path $workspace 'evidence'
New-Item -ItemType Directory -Path $evidence -ErrorAction Stop | Out-Null
$started = [DateTime]::UtcNow.ToString('o')
try {
    Write-OldModNewJson (Join-Path $evidence 'source-snapshot.json') $frozen
    $game = Join-Path $workspace 'game'
    Copy-OldModSnapshot $source $game $gameSnapshot
    $copiedAppIdFiles = @(Get-ChildItem -LiteralPath $game -Force | Where-Object Name -IEQ 'steam_appid.txt')
    if (($copiedAppIdFiles.Count -gt 0 -and !$steamIdentity) -or $copiedAppIdFiles.Count -gt 1 -or
        ($copiedAppIdFiles.Count -eq 1 -and $copiedAppIdFiles[0].Name -cne 'steam_appid.txt')) {
        throw 'Frozen Steam development file requires an explicit manifest and unambiguous canonical filename.'
    }
    if ($steamIdentity) {
        $appIdFile = Join-Path $game 'steam_appid.txt'
        if (Test-Path -LiteralPath $appIdFile) {
            Assert-OldModSteamAppIdFile $appIdFile
        } else {
            # Copy/hash checks completed first. Only the new isolated copy gets
            # Steam's documented development hint; the source is never changed.
            $appIdBytes = [Text.Encoding]::ASCII.GetBytes("1584090`n")
            $appIdStream = [IO.File]::Open($appIdFile, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            try { $appIdStream.Write($appIdBytes, 0, $appIdBytes.Length); $appIdStream.Flush($true) } finally { $appIdStream.Dispose() }
        }
        $steamIdentity.developmentFilePath = $appIdFile
        $steamIdentity.developmentFileSha256 = (Get-FileHash -LiteralPath $appIdFile -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    # Validate the frozen copy itself, not only preflight reads made before hashing.
    $copiedDll = Join-Path $game 'BepInEx/plugins/mystia-steward-companion/MystiaStewardCompanion.BepInEx.dll'
    if ((Get-OldModPluginVersion $copiedDll) -cne '1.3.1') { throw 'Frozen plugin does not declare PluginVersion 1.3.1.' }
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($copiedDll)
    Assert-OldModLaunchConfiguration $game $gameSnapshot
    $stagingParent = Join-Path $workspace 'staging'
    New-Item -ItemType Directory -Path $stagingParent -ErrorAction Stop | Out-Null
    $staged = Join-Path $stagingParent 'mystia-steward-companion'
    Copy-OldModSnapshot $sourcePlugin $staged $pluginSnapshot
    # Only this newly copied staging file is replaced; game/plugin bytes stay frozen.
    $stagedUpdater = Join-Path $staged 'mystia-steward-companion-updater.exe'
    [void](Assert-OldModFile $stagedUpdater)
    [IO.File]::Copy($accepted.executable, $stagedUpdater, $true)
    if ((Get-FileHash -LiteralPath $stagedUpdater -Algorithm SHA256).Hash -ine $accepted.sha256) { throw 'Staged bootstrap changed during copy.' }
    $plugin = Join-Path $game 'BepInEx/plugins/mystia-steward-companion'
    $config = Join-Path $game 'BepInEx/config/com.tyukki.mystia-steward-companion.cfg'
    $updates = Join-Path $game 'BepInEx/config/MystiaStewardCompanion/updates'
    [IO.Directory]::CreateDirectory($updates) | Out-Null
    $random = [byte[]]::new(32)
    [Security.Cryptography.RandomNumberGenerator]::Fill($random)
    $token = [BitConverter]::ToString($random).Replace('-', '').ToLowerInvariant()
    $configuration = @"
[Companion]
AutoLaunch = false

[LocalApi]
Enabled = true
AllowLanConnections = false
Port = $Port
Token = $token

[Updates]
Enabled = true
AutoCheck = false
"@
    $configBytes = [Text.UTF8Encoding]::new($false).GetBytes($configuration + "`n")
    $configStream = [IO.File]::Open($config, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $configStream.Write($configBytes, 0, $configBytes.Length); $configStream.Flush($true) } finally { $configStream.Dispose() }
    $token = $null
    $configuration = $null
    Write-OldModNewJson (Join-Path $updates 'update-state.json') ([ordered]@{
        state = 'downloaded'; downloadedVersion = '1.3.2-preview.1'; stagedDirectory = $staged
    })
    Assert-OldModSnapshotEqual $frozen (Get-OldModSnapshot $source) 'Source game'
    Assert-OldModSnapshotEqual $pluginSnapshot (Get-OldModSnapshot $plugin) 'Copied plugin'
    if ($steamIdentity) {
        Assert-OldModSteamManifestUnchanged $steamIdentity
        Assert-OldModSteamAppIdFile $steamIdentity.developmentFilePath
        if ((Get-FileHash -LiteralPath $steamIdentity.developmentFilePath -Algorithm SHA256).Hash -ine $steamIdentity.developmentFileSha256) {
            throw 'Copied Steam development file changed during preparation.'
        }
    }
    $dllEntry = @($pluginSnapshot.files | Where-Object path -CEQ 'MystiaStewardCompanion.BepInEx.dll')[0]
    $gameEntry = @($gameSnapshot.files | Where-Object path -CEQ 'Touhou Mystia Izakaya.exe')[0]
    $manifest = [ordered]@{
        schemaVersion = 1; kind = 'old-mod-launch-fixture'
        sourceGameDirectory = $source; gameDirectory = $game; gameExecutable = Join-Path $game 'Touhou Mystia Izakaya.exe'
        pluginDirectory = $plugin; stagedDirectory = $staged; updatesDirectory = $updates
        configPath = $config; evidenceDirectory = $evidence
        expectedModVersion = '1.3.1'; testDownloadedVersion = '1.3.2-preview.1'; port = $Port
        bootstrapSha256 = $accepted.sha256; bootstrapBuildCommit = $accepted.commit
        pluginFiles = @($pluginSnapshot.files); originalPluginDllSha256 = $dllEntry.sha256; gameExecutableSha256 = $gameEntry.sha256
        originalPluginDllFileVersion = $version.FileVersion; originalPluginDllProductVersion = $version.ProductVersion
        steamIdentity = $steamIdentity
        sourceSnapshotSha256 = (Get-FileHash -LiteralPath (Join-Path $evidence 'source-snapshot.json') -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    Write-OldModNewJson (Join-Path $evidence 'prepare-report.json') ([ordered]@{
        result = 'prepared'; startedUtc = $started; completedUtc = [DateTime]::UtcNow.ToString('o')
        copyBudgetBytes = $copyBytes; reservedHeadroomBytes = $reserve
        sourceUnchanged = $true; copiedPluginUnchanged = $true; sourceFileCount = $frozen.files.Count
        steamIdentity = $steamIdentity
        excludedPaths = @('BepInEx/config/com.tyukki.mystia-steward-companion.cfg', 'BepInEx/config/MystiaStewardCompanion/')
        fixtureVersionMeaning = '1.3.2-preview.1 is only a seeded cache gate value, not a release or installed product version.'
    })
    # Publication is last. Consumers must reject any workspace without this file.
    Write-OldModNewJson (Join-Path $workspace 'probe-workspace.json') $manifest
    Write-Host "PREPARED: $workspace"
    Write-Host 'No game was started. Test cache version 1.3.2-preview.1 is a fixture gate, not a published release.'
} catch {
    $failure = $_
    try {
        Write-OldModNewJson (Join-Path $evidence 'prepare-failure.json') ([ordered]@{
            result = 'failed'; startedUtc = $started; error = $failure.Exception.Message
        })
    } catch { Write-Warning ('Unable to write preparation failure report: ' + $_.Exception.Message) }
    Write-Host "FAILED. Do not run this workspace; retain evidence at: $workspace"
    throw $failure
}
