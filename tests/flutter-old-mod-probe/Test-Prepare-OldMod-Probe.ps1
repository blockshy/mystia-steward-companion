#requires -Version 7.0
param(
    [Parameter(Mandatory = $true)][string]$ProbeDirectory,
    [string]$OutputParent = (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'temp')
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'Prepare-OldMod-Probe.Common.psm1') -Force -DisableNameChecking
$probe = Resolve-OldModPath $ProbeDirectory
[void](Assert-OldModAcceptedProbe $probe)
$parent = Resolve-OldModPath $OutputParent
$testRoot = Join-Path $parent ('flutter-old-mod-prepare-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
Write-Host "Prepare verifier evidence: $testRoot"
$scriptPath = Join-Path $PSScriptRoot 'Prepare-OldMod-Probe.ps1'
$fixtureDll = Join-Path $testRoot 'fixture-plugin.dll'
Add-Type -OutputAssembly $fixtureDll -TypeDefinition @'
using System.Reflection;
[assembly: AssemblyFileVersion("1.0.0.0")]
namespace MystiaStewardCompanion.Plugin {
    public class MystiaStewardCompanionPlugin {
        public const string PluginVersion = "1.3.1";
    }
}
'@
# Add-Type does not promise a Win32 version-resource block on every platform.
# FileVersionInfo may expose the metadata attribute on Linux and no resource
# version on Windows. Preparation must preserve the actual observed values.
$fixtureResourceVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($fixtureDll)
$results = [Collections.Generic.List[object]]::new()

function Require-PrepareTest {
    param([bool]$Value, [string]$Message)
    if (!$Value) { throw $Message }
}
function Invoke-PrepareCheck {
    param([string]$Name, [scriptblock]$Check)
    & $Check
    $results.Add([ordered]@{ name = $Name; passed = $true })
}
function Expect-PrepareFailure {
    param([scriptblock]$Action, [string]$Pattern)
    $failure = $null
    try { & $Action | Out-Null } catch { $failure = $_.Exception.Message }
    Require-PrepareTest ($null -ne $failure -and $failure -match $Pattern) "Expected failure /$Pattern/; received: $failure"
}
function New-PrepareSource {
    param([string]$Name)
    $root = Join-Path $testRoot $Name
    $paths = @('BepInEx/plugins/mystia-steward-companion/companion', 'BepInEx/config/MystiaStewardCompanion/updates', 'BepInEx/core', 'empty-directory')
    foreach ($relative in $paths) { [IO.Directory]::CreateDirectory((Join-Path $root $relative)) | Out-Null }
    foreach ($relative in @('Touhou Mystia Izakaya.exe', 'winhttp.dll', 'BepInEx/core/BepInEx.Unity.IL2CPP.dll',
        'BepInEx/plugins/mystia-steward-companion/companion/mystia-steward-companion.exe',
        'BepInEx/plugins/mystia-steward-companion/mystia-steward-companion-updater.exe')) {
        [IO.File]::WriteAllText((Join-Path $root $relative), "Non-executable fixture file: $relative")
    }
    [IO.File]::Copy($fixtureDll, (Join-Path $root 'BepInEx/plugins/mystia-steward-companion/MystiaStewardCompanion.BepInEx.dll'))
    [IO.File]::WriteAllText((Join-Path $root 'doorstop_config.ini'), "[UnityDoorstop]`ntarget_assembly = BepInEx/core/BepInEx.Unity.IL2CPP.dll`n")
    [IO.File]::WriteAllText((Join-Path $root 'BepInEx/config/BepInEx.cfg'), "[IL2CPP]`nUnityBaseLibrariesSource = https://unity.bepinex.dev/libraries/{VERSION}.zip`n")
    [IO.File]::WriteAllText((Join-Path $root 'BepInEx/config/com.tyukki.mystia-steward-companion.cfg'), "[LocalApi]`nToken = old-secret-fixture`n")
    [IO.File]::WriteAllText((Join-Path $root 'BepInEx/config/MystiaStewardCompanion/old-user-state.json'), '{"fixture":"old-secret-fixture"}')
    [IO.File]::WriteAllText((Join-Path $root 'BepInEx/config/MystiaStewardCompanion/updates/install-status.json'), '{"state":"succeeded"}')
    return $root
}

$source = New-PrepareSource 'source'
$sourceBefore = Get-OldModSnapshot $source
$workspace = Join-Path $testRoot 'prepared'
Invoke-PrepareCheck 'full-consumer-prepare-uses-approved-binary' {
    & $scriptPath -SourceGameDirectory $source -WorkspaceDirectory $workspace -ProbeDirectory $probe *> (Join-Path $testRoot 'prepare.log')
    $manifest = Get-Content -LiteralPath (Join-Path $workspace 'probe-workspace.json') -Raw | ConvertFrom-Json
    Require-PrepareTest ($manifest.kind -ceq 'old-mod-launch-fixture' -and $manifest.schemaVersion -eq 1) 'Wrong workspace contract.'
    Require-PrepareTest ($null -eq $manifest.steamIdentity -and !(Test-Path -LiteralPath (Join-Path $manifest.gameDirectory 'steam_appid.txt'))) 'Steam development identity was created without an explicit manifest.'
    Require-PrepareTest ($manifest.originalPluginDllFileVersion -ceq $fixtureResourceVersion.FileVersion) 'PE resource file version was misrepresented.'
    Require-PrepareTest ($manifest.originalPluginDllProductVersion -ceq $fixtureResourceVersion.ProductVersion) 'PE resource product version was misrepresented.'
    Require-PrepareTest ($manifest.expectedModVersion -ceq '1.3.1' -and $manifest.testDownloadedVersion -ceq '1.3.2-preview.1') 'Wrong source/fixture versions.'
    Require-PrepareTest ((Get-OldModPluginVersion (Join-Path $manifest.pluginDirectory 'MystiaStewardCompanion.BepInEx.dll')) -ceq '1.3.1') 'Metadata version changed.'
    Require-PrepareTest ((Get-FileHash -LiteralPath (Join-Path $manifest.stagedDirectory 'mystia-steward-companion-updater.exe')).Hash -ieq $manifest.bootstrapSha256) 'Wrong staged bootstrap.'
    $sourcePlugin = Get-OldModSnapshot (Join-Path $source 'BepInEx/plugins/mystia-steward-companion')
    Assert-OldModSnapshotEqual $sourcePlugin (Get-OldModSnapshot $manifest.pluginDirectory) 'Plugin copy'
    Require-PrepareTest (!(Test-Path -LiteralPath (Join-Path $manifest.updatesDirectory 'install-status.json'))) 'Old install status was inherited.'
    Require-PrepareTest (!(Test-Path -LiteralPath (Join-Path $manifest.gameDirectory 'BepInEx/config/MystiaStewardCompanion/old-user-state.json'))) 'Old user state was inherited.'
    $state = Get-Content -LiteralPath (Join-Path $manifest.updatesDirectory 'update-state.json') -Raw | ConvertFrom-Json
    Require-PrepareTest (@($state.PSObject.Properties).Count -eq 3 -and $state.state -ceq 'downloaded' -and $state.stagedDirectory -ceq $manifest.stagedDirectory) 'Seeded state has extra or incorrect fields.'
    $cfg = Get-Content -LiteralPath $manifest.configPath -Raw
    Require-PrepareTest ($cfg -match '(?m)^Token = [a-f0-9]{64}$' -and $cfg -notmatch 'old-secret') 'Token is missing or inherited.'
    Require-PrepareTest ($cfg -match 'AutoLaunch = false' -and $cfg -match 'AllowLanConnections = false' -and $cfg -match 'AutoCheck = false') 'Isolation settings missing.'
    foreach ($file in Get-ChildItem -LiteralPath $workspace -Filter '*.json' -Recurse) {
        $text = Get-Content -LiteralPath $file.FullName -Raw
        Require-PrepareTest ($text -notmatch 'old-secret-fixture') 'Source token/state was leaked.'
        Require-PrepareTest ($text -notmatch '(?i)"token"') 'Token was written into a JSON report.'
    }
    Assert-OldModSnapshotEqual $sourceBefore (Get-OldModSnapshot $source) 'Source after successful prepare'
}
Invoke-PrepareCheck 'existing-workspace-is-never-reused' {
    $before = Get-OldModSnapshot $workspace
    Expect-PrepareFailure { & $scriptPath -SourceGameDirectory $source -WorkspaceDirectory $workspace -ProbeDirectory $probe } 'already exists'
    Assert-OldModSnapshotEqual $before (Get-OldModSnapshot $workspace) 'Existing workspace'
}
Invoke-PrepareCheck 'overlapping-source-workspace-is-rejected' {
    $nested = Join-Path $source 'new-workspace'
    Expect-PrepareFailure { & $scriptPath -SourceGameDirectory $source -WorkspaceDirectory $nested -ProbeDirectory $probe } 'disjoint'
    Require-PrepareTest (!(Test-Path -LiteralPath $nested)) 'Nested workspace was created.'
}
Invoke-PrepareCheck 'missing-workspace-parent-is-not-created' {
    Expect-PrepareFailure { Resolve-OldModPath (Join-Path $testRoot 'missing-parent/workspace') -NewDirectory } 'does not exist|Cannot find'
    Require-PrepareTest (!(Test-Path -LiteralPath (Join-Path $testRoot 'missing-parent'))) 'Missing parent was created.'
}
Invoke-PrepareCheck 'prefix-neighbor-is-not-an-overlap' {
    Require-PrepareTest (!(Test-OldModOverlap (Join-Path $testRoot 'game') (Join-Path $testRoot 'game-copy'))) 'Prefix neighbor rejected.'
    Require-PrepareTest (Test-OldModOverlap ([IO.Path]::GetPathRoot($testRoot)) $testRoot) 'Filesystem root overlap missed.'
}
Invoke-PrepareCheck 'fake-bootstrap-cannot-self-authorize-with-manifest' {
    $fake = Join-Path $testRoot 'fake-probe'
    New-Item -ItemType Directory -Path $fake | Out-Null
    foreach ($name in @('mystia-steward-companion-updater.exe', 'build-evidence.json', 'bundle-manifest.json', 'Start-Probe.ps1', 'README.md')) {
        [IO.File]::WriteAllText((Join-Path $fake $name), 'fake authorized-looking package')
    }
    Expect-PrepareFailure { & $scriptPath -SourceGameDirectory $source -WorkspaceDirectory (Join-Path $testRoot 'must-not-authorize') -ProbeDirectory $fake } 'not the accepted'
    Require-PrepareTest (!(Test-Path -LiteralPath (Join-Path $testRoot 'must-not-authorize'))) 'Untrusted package created a workspace.'
}
Invoke-PrepareCheck 'duplicate-plugin-dll-is-rejected' {
    $duplicate = New-PrepareSource 'duplicate-plugin-source'
    [IO.File]::Copy($fixtureDll, (Join-Path $duplicate 'BepInEx/plugins/MystiaStewardCompanion.BepInEx.dll'))
    Expect-PrepareFailure { Assert-OldModLaunchConfiguration $duplicate (Get-OldModSnapshot $duplicate) } 'exactly one'
}
Invoke-PrepareCheck 'absolute-loader-path-is-rejected' {
    $absolute = New-PrepareSource 'absolute-path-source'
    [IO.File]::WriteAllText((Join-Path $absolute 'doorstop_config.ini'), 'target_assembly = E:\original\BepInEx\core\BepInEx.Unity.IL2CPP.dll')
    Expect-PrepareFailure { Assert-OldModLaunchConfiguration $absolute (Get-OldModSnapshot $absolute) } 'non-local path'
}
Invoke-PrepareCheck 'parent-traversal-loader-path-is-rejected' {
    $traversal = New-PrepareSource 'traversal-path-source'
    [IO.File]::WriteAllText((Join-Path $traversal 'doorstop_config.ini'), 'target_assembly = ../original/core.dll')
    Expect-PrepareFailure { Assert-OldModLaunchConfiguration $traversal (Get-OldModSnapshot $traversal) } 'parent traversal'
}
foreach ($badPath in @(
    @{ name = 'drive-relative'; value = 'E:outside\core.dll'; error = 'non-local path' },
    @{ name = 'root-relative'; value = '\outside\core.dll'; error = 'non-local path' },
    @{ name = 'list-parent-traversal'; value = 'a;..\outside'; error = 'parent traversal' }
)) {
    Invoke-PrepareCheck ($badPath.name + '-loader-path-is-rejected') {
        $badSource = New-PrepareSource ($badPath.name + '-source')
        [IO.File]::WriteAllText((Join-Path $badSource 'doorstop_config.ini'), ('target_assembly = ' + $badPath.value))
        Expect-PrepareFailure { Assert-OldModLaunchConfiguration $badSource (Get-OldModSnapshot $badSource) } $badPath.error
    }
}
Invoke-PrepareCheck 'control-port-is-rejected-before-copy' {
    $destination = Join-Path $testRoot 'control-port-workspace'
    Expect-PrepareFailure { & $scriptPath -SourceGameDirectory $source -WorkspaceDirectory $destination -ProbeDirectory $probe -Port 32146 } 'control port'
    Require-PrepareTest (!(Test-Path -LiteralPath $destination)) 'Invalid port created a workspace.'
}
Invoke-PrepareCheck 'directory-links-and-link-ancestors-are-rejected' {
    $linkedSource = New-PrepareSource 'link-source'
    $link = Join-Path $linkedSource 'linked-user-data'
    $type = if ($IsWindows) { 'Junction' } else { 'SymbolicLink' }
    New-Item -ItemType $type -Path $link -Target $source | Out-Null
    Expect-PrepareFailure { Get-OldModSnapshot $linkedSource } 'links/reparse'
    Expect-PrepareFailure { Resolve-OldModPath (Join-Path $link 'BepInEx') } 'not a real directory'
}
Invoke-PrepareCheck 'insufficient-space-is-fatal' {
    Expect-PrepareFailure { Assert-OldModDiskBudget 99 100 } 'Insufficient free space'
    Assert-OldModDiskBudget 100 100
}
Invoke-PrepareCheck 'source-change-aborts-copy-and-retains-partial-files' {
    $changing = New-PrepareSource 'changing-source'
    $snapshot = Get-OldModSnapshot $changing
    [IO.File]::WriteAllText((Join-Path $changing 'Touhou Mystia Izakaya.exe'), 'changed after source freeze')
    $partial = Join-Path $testRoot 'partial-copy'
    Expect-PrepareFailure { Copy-OldModSnapshot $changing $partial $snapshot } 'differs from the frozen source'
    Require-PrepareTest (Test-Path -LiteralPath $partial -PathType Container) 'Partial evidence was deleted.'
}
Invoke-PrepareCheck 'version-is-read-without-loading-target-assembly' {
    $loadedBefore = @([AppDomain]::CurrentDomain.GetAssemblies() | ForEach-Object Location)
    Require-PrepareTest ((Get-OldModPluginVersion $fixtureDll) -ceq '1.3.1') 'Wrong metadata constant.'
    $loadedAfter = @([AppDomain]::CurrentDomain.GetAssemblies() | ForEach-Object Location)
    Require-PrepareTest (!($loadedAfter -contains $fixtureDll)) 'Target fixture DLL was loaded into the process.'
    Require-PrepareTest (!($loadedBefore -contains $fixtureDll)) 'Test construction loaded target fixture DLL.'
    Expect-PrepareFailure { Get-OldModPluginVersion (Join-Path $source 'Touhou Mystia Izakaya.exe') } 'PE|DOS|image|metadata|small'
}

function New-PrepareSteamFixture([string]$Name) {
    $game = New-PrepareSource ($Name + '/steamapps/common/Touhou Mystia Izakaya')
    $manifestPath = Join-Path ([IO.Path]::GetDirectoryName([IO.Path]::GetDirectoryName($game))) 'appmanifest_1584090.acf'
    $manifestText = @'
"AppState"
{
    "appid" "1584090"
    "name" "Touhou Mystia's Izakaya"
    "installdir" "Touhou Mystia Izakaya"
    "buildid" "23158340"
    "LauncherPath" "C:\\Program Files (x86)\\Steam\\steam.exe"
    "InstalledDepots"
    {
        "1584091" { "manifest" "123456789" }
    }
}
'@
    [IO.File]::WriteAllText($manifestPath, $manifestText, [Text.UTF8Encoding]::new($false))
    return @{ game = $game; path = $manifestPath; text = $manifestText }
}

$steamFixture = New-PrepareSteamFixture 'steam-library'
Invoke-PrepareCheck 'steam-development-file-is-created-only-in-frozen-copy' {
    $before = Get-OldModSnapshot $steamFixture.game
    $manifestHash = (Get-FileHash -LiteralPath $steamFixture.path -Algorithm SHA256).Hash.ToLowerInvariant()
    $destination = Join-Path $testRoot 'steam-prepared'
    & $scriptPath -SourceGameDirectory $steamFixture.game -WorkspaceDirectory $destination -ProbeDirectory $probe -SteamAppManifestPath $steamFixture.path *> (Join-Path $testRoot 'steam-prepare.log')
    $manifest = Get-Content -LiteralPath (Join-Path $destination 'probe-workspace.json') -Raw | ConvertFrom-Json
    $prepared = Get-Content -LiteralPath (Join-Path $destination 'evidence/prepare-report.json') -Raw | ConvertFrom-Json
    $identity = $manifest.steamIdentity
    Require-PrepareTest ($identity.appId -ceq '1584090' -and $identity.buildId -ceq '23158340' -and $identity.installDirectory -ceq 'Touhou Mystia Izakaya') 'Wrong Steam identity.'
    Require-PrepareTest ($identity.sourceManifestPath -ceq $steamFixture.path -and $identity.sourceManifestSha256 -ceq $manifestHash) 'Steam source evidence changed.'
    Require-PrepareTest ($identity.developmentFilePath -ceq (Join-Path $manifest.gameDirectory 'steam_appid.txt')) 'Steam development file escaped the game copy.'
    Require-PrepareTest ([Convert]::ToBase64String([IO.File]::ReadAllBytes($identity.developmentFilePath)) -ceq [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("1584090`n"))) 'New development file is not exact ASCII App ID plus LF.'
    Require-PrepareTest ((Get-FileHash -LiteralPath $identity.developmentFilePath -Algorithm SHA256).Hash -ieq $identity.developmentFileSha256) 'Steam development file hash is wrong.'
    Require-PrepareTest (($prepared.steamIdentity | ConvertTo-Json -Compress) -ceq ($identity | ConvertTo-Json -Compress)) 'Prepare report and workspace Steam identities differ.'
    Require-PrepareTest (!(Test-Path -LiteralPath (Join-Path $steamFixture.game 'steam_appid.txt'))) 'Source Steam development file was created.'
    Assert-OldModSteamManifestUnchanged $identity
    Assert-OldModSnapshotEqual $before (Get-OldModSnapshot $steamFixture.game) 'Steam source'
}
Invoke-PrepareCheck 'existing-correct-steam-appid-bytes-are-preserved' {
    $index = 0
    foreach ($value in @('1584090', "1584090`n", "1584090`r`n")) {
        $fixture = New-PrepareSteamFixture ('steam-existing-' + $index)
        $originalFile = Join-Path $fixture.game 'steam_appid.txt'
        [IO.File]::WriteAllBytes($originalFile, [Text.Encoding]::ASCII.GetBytes($value))
        $before = Get-OldModSnapshot $fixture.game
        $destination = Join-Path $testRoot ('steam-existing-prepared-' + $index++)
        & $scriptPath -SourceGameDirectory $fixture.game -WorkspaceDirectory $destination -ProbeDirectory $probe -SteamAppManifestPath $fixture.path *> (Join-Path $testRoot ('steam-existing-' + $index + '.log'))
        $copiedFile = Join-Path $destination 'game/steam_appid.txt'
        Require-PrepareTest ([Convert]::ToBase64String([IO.File]::ReadAllBytes($copiedFile)) -ceq [Convert]::ToBase64String([IO.File]::ReadAllBytes($originalFile))) 'Existing Steam development bytes were normalized or replaced.'
        Assert-OldModSnapshotEqual $before (Get-OldModSnapshot $fixture.game) 'Source with existing Steam file'
    }
}
Invoke-PrepareCheck 'existing-steam-appid-requires-explicit-manifest' {
    $fixture = New-PrepareSteamFixture 'steam-needs-manifest'
    [IO.File]::WriteAllText((Join-Path $fixture.game 'steam_appid.txt'), '1584090')
    $destination = Join-Path $testRoot 'steam-unrequested'
    Expect-PrepareFailure { & $scriptPath -SourceGameDirectory $fixture.game -WorkspaceDirectory $destination -ProbeDirectory $probe } 'explicit -SteamAppManifestPath'
    Require-PrepareTest (!(Test-Path -LiteralPath $destination)) 'Unrequested Steam identity created a workspace.'
}
Invoke-PrepareCheck 'wrong-existing-steam-appid-is-not-overwritten' {
    $fixture = New-PrepareSteamFixture 'steam-wrong-existing'
    $originalFile = Join-Path $fixture.game 'steam_appid.txt'
    [IO.File]::WriteAllText($originalFile, '480')
    $before = Get-OldModSnapshot $fixture.game
    $destination = Join-Path $testRoot 'steam-wrong-existing-prepared'
    Expect-PrepareFailure { & $scriptPath -SourceGameDirectory $fixture.game -WorkspaceDirectory $destination -ProbeDirectory $probe -SteamAppManifestPath $fixture.path } 'real App ID'
    Require-PrepareTest (!(Test-Path -LiteralPath $destination)) 'Wrong Steam App ID created a workspace.'
    Assert-OldModSnapshotEqual $before (Get-OldModSnapshot $fixture.game) 'Source with incorrect Steam file'
}
foreach ($case in @(
    @{ name = 'wrong-appid'; text = $steamFixture.text.Replace('"1584090"', '"480"'); error = 'expected installed game' },
    @{ name = 'wrong-installdir'; text = $steamFixture.text.Replace('"Touhou Mystia Izakaya"', '"Other Game"'); error = 'expected installed game' },
    @{ name = 'invalid-buildid'; text = $steamFixture.text.Replace('"23158340"', '"unknown"'); error = 'expected installed game' },
    @{ name = 'duplicate-identity'; text = $steamFixture.text.Replace('"appid" "1584090"', '"appid" "1584090" "AppId" "480"'); error = 'duplicate|ambiguous' },
    @{ name = 'trailing-root'; text = $steamFixture.text + ' "AppState" {}'; error = 'expected installed game' },
    @{ name = 'unsupported-syntax'; text = $steamFixture.text + ' unexpected'; error = 'unsupported|ambiguous' }
)) {
    Invoke-PrepareCheck ('steam-manifest-' + $case.name + '-is-rejected') {
        $fixture = New-PrepareSteamFixture ('steam-' + $case.name)
        [IO.File]::WriteAllText($fixture.path, $case.text)
        Expect-PrepareFailure { Read-OldModSteamIdentity $fixture.path $fixture.game } $case.error
    }
}
Invoke-PrepareCheck 'steam-manifest-cannot-authorize-another-source' {
    Expect-PrepareFailure { Read-OldModSteamIdentity $steamFixture.path $source } 'does not match the source'
}
Invoke-PrepareCheck 'steam-manifest-change-is-detected' {
    $fixture = New-PrepareSteamFixture 'steam-manifest-changed'
    $identity = Read-OldModSteamIdentity $fixture.path $fixture.game
    [IO.File]::AppendAllText($fixture.path, "`n")
    Expect-PrepareFailure { Assert-OldModSteamManifestUnchanged $identity } 'changed during preparation'
}
Invoke-PrepareCheck 'steam-manifest-reparse-parent-is-rejected' {
    $root = Join-Path $testRoot 'steam-reparse'
    New-Item -ItemType Directory -Path $root | Out-Null
    $link = Join-Path $root 'steamapps'
    $type = if ($IsWindows) { 'Junction' } else { 'SymbolicLink' }
    New-Item -ItemType $type -Path $link -Target ([IO.Path]::GetDirectoryName($steamFixture.path)) | Out-Null
    Expect-PrepareFailure { Read-OldModSteamIdentity (Join-Path $link 'appmanifest_1584090.acf') $steamFixture.game } 'not a real directory'
}
Assert-OldModSnapshotEqual $sourceBefore (Get-OldModSnapshot $source) 'Source after all checks'
Write-OldModNewJson (Join-Path $testRoot 'verifier-report.json') ([ordered]@{
    kind = 'prepare-real-files-and-approved-bootstrap-fixture-no-game-execution'
    powershell = $PSVersionTable.PSVersion.ToString(); passed = $true; checks = @($results.ToArray())
    fixtureFileVersion = $fixtureResourceVersion.FileVersion; fixtureProductVersion = $fixtureResourceVersion.ProductVersion
})
Write-Host "$($results.Count) Prepare checks passed. Game/BepInEx files were fixtures; no program was executed."
