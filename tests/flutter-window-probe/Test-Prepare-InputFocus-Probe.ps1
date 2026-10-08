#requires -Version 7.0
param([string]$OutputParent = (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'temp'))
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'Prepare-InputFocus-Probe.ps1')
if (!(Get-Item -LiteralPath $OutputParent -Force -ErrorAction SilentlyContinue)) {
    [void](Resolve-OldModPath $OutputParent -NewDirectory)
    New-Item -ItemType Directory -Path $OutputParent -ErrorAction Stop | Out-Null
}
$parent = Resolve-OldModPath $OutputParent
$testRoot = Join-Path $parent ('flutter-input-focus-prepare-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
Write-Host "Preparation fixture evidence: $testRoot"
$productionPins = Get-InputFocusPinnedFiles
$fixtureDll = Join-Path $testRoot 'fixture-mod.dll'
Add-Type -OutputAssembly $fixtureDll -TypeDefinition @'
namespace MystiaStewardCompanion.Plugin {
    public class MystiaStewardCompanionPlugin {
        public const string PluginVersion = "1.3.1";
    }
}
'@
$results = [Collections.Generic.List[object]]::new()
$gitSha = '1234567890abcdef1234567890abcdef12345678'
$cooperatorBundle = Join-Path $testRoot 'cooperator-bundle'
New-Item -ItemType Directory -Path $cooperatorBundle | Out-Null
$cooperatorDll = Join-Path $cooperatorBundle 'MystiaStewardCompanion.FocusProbe.dll'
[IO.File]::WriteAllText($cooperatorDll, 'Non-executable cooperative focus fixture; never launch this DLL.')
$cooperatorManifest = [ordered]@{
    schemaVersion = 1; kind = 'flutter-focus-cooperator-bundle'; commit = $gitSha; compiledGitSha = $gitSha; cleanCheckout = $true
    target = 'net6.0-windows-in-game'; entrypoint = 'MystiaStewardCompanion.FocusProbe.dll'
    tools = @{ dotnetSdk = $inputFocusToolchain.dotnetSdk; node = $inputFocusToolchain.node }
    toolchainLockSha256 = Get-InputFocusHash $inputFocusToolchainPath
    referencesLockSha256 = Get-InputFocusHash (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'mods/bepinex/References/references.lock.json')
    files = @(@{ path = 'MystiaStewardCompanion.FocusProbe.dll'; size = (Get-Item $cooperatorDll).Length; sha256 = Get-InputFocusHash $cooperatorDll })
    checks = @{ lockedReferences = 'passed'; releaseBuild = 'passed'; gameRuntime = 'not-run' }
}
Write-OldModNewJson (Join-Path $cooperatorBundle 'build-evidence.json') $cooperatorManifest
$cooperatorEvidenceHash = Get-InputFocusHash (Join-Path $cooperatorBundle 'build-evidence.json')
# A model of environmental listener enumeration makes fixture tests independent
# of CI host ports. Production still reads the actual IPv4/IPv6 TCP listeners.
$fixtureListeners = @()
function Get-InputFocusListeners { return $fixtureListeners }

function Require-InputFocusTest([bool]$Value, [string]$Message) { if (!$Value) { throw $Message } }
function Test-InputFocusCase([string]$Name, [scriptblock]$Action) {
    & $Action
    $results.Add([ordered]@{ name = $Name; passed = $true })
}
function Expect-InputFocusFailure([scriptblock]$Action, [string]$Pattern) {
    $message = ''
    try { & $Action | Out-Null } catch { $message = $_.Exception.Message }
    Require-InputFocusTest ($message -match $Pattern) "Expected failure /$Pattern/, got: $message"
}
function New-InputFocusSource([string]$Name) {
    $source = Join-Path $testRoot "$Name/steamapps/common/Touhou Mystia Izakaya"
    foreach ($relative in $productionPins.Keys) {
        $file = Join-Path $source $relative
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($file)) | Out-Null
        if ($relative -like '*/MystiaStewardCompanion.BepInEx.dll') {
            [IO.File]::Copy($fixtureDll, $file)
        } else { [IO.File]::WriteAllText($file, "Non-executable input-focus fixture: $relative") }
    }
    $config = Join-Path $source 'BepInEx/config'
    [IO.Directory]::CreateDirectory((Join-Path $config 'MystiaStewardCompanion/updates')) | Out-Null
    [IO.File]::WriteAllText((Join-Path $config 'com.tyukki.mystia-steward-companion.cfg'), "[LocalApi]`nToken = original-fixture-secret`n")
    [IO.File]::WriteAllText((Join-Path $config 'MystiaStewardCompanion/updates/update-state.json'), '{"state":"waiting"}')
    [IO.File]::WriteAllText((Join-Path $config 'MystiaStewardCompanion/user-state.json'), 'original-fixture-secret')
    [IO.File]::WriteAllText((Join-Path $config 'BepInEx.cfg'), "[IL2CPP]`nUnityBaseLibrariesSource = https://unity.bepinex.dev/libraries/{VERSION}.zip`n")
    [IO.File]::WriteAllText((Join-Path $source 'doorstop_config.ini'), "[UnityDoorstop]`ntarget_assembly = BepInEx/core/BepInEx.Unity.IL2CPP.dll`n")
    [IO.Directory]::CreateDirectory((Join-Path $source 'empty-fixture-directory')) | Out-Null
    $manifest = Join-Path ([IO.Path]::GetDirectoryName([IO.Path]::GetDirectoryName($source))) 'appmanifest_1584090.acf'
    [IO.File]::WriteAllText($manifest, '"AppState" { "appid" "1584090" "installdir" "Touhou Mystia Izakaya" "buildid" "23158340" }')
    return @{ source = $source; manifest = $manifest }
}
function New-InputFocusRun([string]$Name) {
    $path = Join-Path $testRoot $Name
    New-Item -ItemType Directory -Path $path | Out-Null
    return $path
}
function Invoke-InputFocusFixture($Source, [string]$Run, [int]$FixturePort = 32755) {
    Invoke-InputFocusPreparation -RunDirectory $Run -RunId ([IO.Path]::GetFileName($Run)) -GitSha $gitSha `
        -SourceGameDirectory $Source.source -SteamAppManifestPath $Source.manifest -Port $FixturePort `
        -CooperatorBundleDirectory $cooperatorBundle -CooperatorEvidenceSha256 $cooperatorEvidenceHash
}

$source = New-InputFocusSource 'source-library'
$baseline = Get-OldModSnapshot $source.source
Test-InputFocusCase 'consumer-rejects-unpinned-synthetic-game-before-copy' {
    $run = New-InputFocusRun 'real-pins-reject-fixture'
    Expect-InputFocusFailure { Invoke-InputFocusFixture $source $run } 'Pinned installed-build bytes'
    Require-InputFocusTest (!(Test-Path (Join-Path $run 'workspace'))) 'Unpinned bytes created a workspace.'
}

# Only this test scope substitutes the immutable identity baseline. The public
# preparation script has no fixture switch, alternate-hash input, or test bypass.
# All filesystem/Steam parsing/copy/hash/metadata/config/publication work remains real.
$fixturePins = [ordered]@{}
foreach ($relative in $productionPins.Keys) { $fixturePins[$relative] = Get-InputFocusHash (Join-Path $source.source $relative) }
function Get-InputFocusPinnedFiles { return $fixturePins }

Test-InputFocusCase 'complete-fixture-transaction-preserves-source-and-publishes-bound-sidecar' {
    $run = New-InputFocusRun 'prepared-fixture'
    Invoke-InputFocusFixture $source $run *> (Join-Path $testRoot 'prepare.log')
    $workspace = Join-Path $run 'workspace'
    $sidecar = Get-Content (Join-Path $run 'input-probe.json') -Raw | ConvertFrom-Json -AsHashtable
    $evidencePath = Join-Path $workspace 'prepared-evidence.json'
    $evidence = Get-Content $evidencePath -Raw | ConvertFrom-Json -AsHashtable
    Require-InputFocusTest ($sidecar.Count -eq 13 -and $sidecar.schemaVersion -eq 2 -and $sidecar.runId -ceq 'prepared-fixture' -and $sidecar.gitSha -ceq $gitSha) 'Sidecar identity/schema is wrong.'
    Require-InputFocusTest ($sidecar.expectedCooperatorSha256 -ceq (Get-InputFocusHash $evidence.cooperation.dllPath) -and
        $sidecar.cooperatorBuildEvidenceSha256 -ceq (Get-InputFocusHash (Join-Path $workspace 'cooperator-build-evidence.json')) -and
        $evidence.cooperation.originalModPreserved -ceq $true) 'Cooperator DLL/build evidence is not bound to the copy.'
    Require-InputFocusTest ($sidecar.gameExecutable -ceq (ConvertTo-InputFocusPath (Join-Path $workspace 'game/Touhou Mystia Izakaya.exe'))) 'Game path is not this run copy.'
    Require-InputFocusTest ($sidecar.preparedEvidenceSha256 -ceq (Get-InputFocusHash $evidencePath)) 'Sidecar does not bind exact evidence bytes.'
    foreach ($pair in @(@('expectedExeSha256', 'Touhou Mystia Izakaya.exe'), @('expectedUnityPlayerSha256', 'UnityPlayer.dll'),
        @('expectedGameAssemblySha256', 'GameAssembly.dll'), @('expectedMetadataSha256', 'Touhou Mystia Izakaya_Data/il2cpp_data/Metadata/global-metadata.dat'))) {
        Require-InputFocusTest ($sidecar[$pair[0]] -ceq (Get-InputFocusHash (Join-Path $evidence.gameDirectory $pair[1]))) 'A binary hash does not bind the copied file.'
    }
    Require-InputFocusTest ($evidence.result -ceq 'prepared' -and $evidence.sourceUnchanged -ceq $true -and $evidence.copiedFilesVerified -ceq $true) 'Prepared transaction was not recorded.'
    Require-InputFocusTest ($evidence.sourceSnapshotSha256 -ceq (Get-InputFocusHash $evidence.sourceSnapshotPath)) 'Source snapshot hash differs.'
    Require-InputFocusTest ($sidecar.steamAppId -ceq '1584090' -and $sidecar.steamBuildId -ceq '23158340') 'Steam identity differs.'
    Require-InputFocusTest ([Convert]::ToHexString([IO.File]::ReadAllBytes($evidence.steamIdentity.developmentFilePath)) -ceq '313538343039300A') 'AppID file bytes differ.'
    Require-InputFocusTest (!(Test-Path (Join-Path $source.source 'steam_appid.txt'))) 'Source AppID file was created.'
    Require-InputFocusTest (!(Test-Path (Join-Path $workspace 'staging')) -and !(Test-Path (Join-Path $evidence.gameDirectory 'BepInEx/config/MystiaStewardCompanion'))) 'Update or old user state was prepared/inherited.'
    $cfg = Get-Content $evidence.config.path -Raw
    Require-InputFocusTest ($cfg -match '\[Updates\]\nEnabled = false\nAutoCheck = false' -and $cfg -match 'AutoLaunch = false' -and $cfg -match 'AllowLanConnections = false') 'Isolation settings are missing.'
    Require-InputFocusTest ($cfg -match '(?m)^Token = ([a-f0-9]{64})$') 'Random token is missing.'
    $token = $Matches[1]
    foreach ($json in Get-ChildItem $run -Filter '*.json' -Recurse) {
        $text = Get-Content $json.FullName -Raw
        Require-InputFocusTest (!$text.Contains($token) -and !$text.Contains('original-fixture-secret')) 'A token was leaked into evidence.'
        Require-InputFocusTest (!$text.Contains('\\')) 'Evidence paths are not normalized to forward slashes.'
    }
    Require-InputFocusTest ($evidence.config.sha256 -ceq (Get-InputFocusHash $evidence.config.path) -and $evidence.config.port -eq 32755) 'Configuration hash/port differ.'
    Require-InputFocusTest (@($evidence.verifiedFiles).Count -eq 7 -and $evidence.gameStarted -ceq $false -and $evidence.stagingPrepared -ceq $false) 'Preparation evidence overclaims work.'
    Assert-OldModSnapshotEqual $baseline (Get-OldModSnapshot $source.source) 'Source after preparation'
    $before = Get-OldModSnapshot $run
    Expect-InputFocusFailure { Invoke-InputFocusFixture $source $run } 'already exists'
    Assert-OldModSnapshotEqual $before (Get-OldModSnapshot $run) 'Existing run must not change'
}

foreach ($portValue in @(32145, 32146)) {
    Test-InputFocusCase "production-port-$portValue-rejected" {
        $run = New-InputFocusRun "port-$portValue"
        Expect-InputFocusFailure { Invoke-InputFocusFixture $source $run $portValue } 'exclude production ports'
        Require-InputFocusTest (!(Test-Path (Join-Path $run 'workspace'))) 'Invalid port created a workspace.'
    }
}
Test-InputFocusCase 'existing-ipv6-listener-rejects-port-before-copy' {
    $fixtureListeners = @([Net.IPEndPoint]::new([Net.IPAddress]::IPv6Any, 32755))
    $run = New-InputFocusRun 'occupied-port'
    try {
        Expect-InputFocusFailure { Invoke-InputFocusFixture $source $run } 'already has a listener'
        Require-InputFocusTest (!(Test-Path (Join-Path $run 'workspace'))) 'Occupied port created a workspace.'
    } finally { $fixtureListeners = @() }
}
Test-InputFocusCase 'mismatched-run-id-and-preexisting-sidecar-rejected' {
    $run = New-InputFocusRun 'invalid-run-identity'
    Expect-InputFocusFailure {
        Invoke-InputFocusPreparation -RunDirectory $run -RunId 'other' -GitSha $gitSha -SourceGameDirectory $source.source -SteamAppManifestPath $source.manifest
    } 'leaf must equal'
    [IO.File]::WriteAllText((Join-Path $run 'input-probe.json'), 'preserve this existing evidence')
    Expect-InputFocusFailure { Invoke-InputFocusFixture $source $run } 'already contains'
    Require-InputFocusTest ((Get-Content (Join-Path $run 'input-probe.json') -Raw) -ceq 'preserve this existing evidence') 'Existing evidence was overwritten.'
}
Test-InputFocusCase 'source-and-run-overlap-rejected' {
    $run = Join-Path $source.source 'nested-run'
    New-Item -ItemType Directory -Path $run | Out-Null
    Expect-InputFocusFailure { Invoke-InputFocusFixture $source $run } 'disjoint'
    Require-InputFocusTest (!(Test-Path (Join-Path $run 'workspace'))) 'Nested workspace was created.'
}
Test-InputFocusCase 'wrong-build-and-wrong-source-appid-rejected-without-overwrite' {
    $other = New-InputFocusSource 'wrong-steam-library'
    $text = [IO.File]::ReadAllText($other.manifest)
    [IO.File]::WriteAllText($other.manifest, $text.Replace('23158340', '23158341'))
    $run = New-InputFocusRun 'wrong-build'
    Expect-InputFocusFailure { Invoke-InputFocusFixture $other $run } 'pinned installed build'
    [IO.File]::WriteAllText($other.manifest, $text)
    [IO.File]::WriteAllText((Join-Path $other.source 'steam_appid.txt'), '480')
    $before = Get-OldModSnapshot $other.source
    Expect-InputFocusFailure { Invoke-InputFocusFixture $other $run } 'real App ID'
    Assert-OldModSnapshotEqual $before (Get-OldModSnapshot $other.source) 'Wrong source Steam file'
    Require-InputFocusTest (!(Test-Path (Join-Path $run 'workspace'))) 'Invalid Steam identity created a copy.'
}
Test-InputFocusCase 'source-link-and-external-loader-rejected' {
    $other = New-InputFocusSource 'unsafe-loader-library'
    [IO.File]::WriteAllText((Join-Path $other.source 'doorstop_config.ini'), 'target_assembly = E:outside\core.dll')
    $run = New-InputFocusRun 'unsafe-loader'
    Expect-InputFocusFailure { Invoke-InputFocusFixture $other $run } 'non-local path'
    $linked = New-InputFocusSource 'linked-library'
    $type = if ($IsWindows) { 'Junction' } else { 'SymbolicLink' }
    New-Item -ItemType $type -Path (Join-Path $linked.source 'external-directory') -Target $source.source | Out-Null
    Expect-InputFocusFailure { Invoke-InputFocusFixture $linked $run } 'links/reparse'
    Require-InputFocusTest (!(Test-Path (Join-Path $run 'workspace'))) 'Unsafe source created a copy.'
}
Test-InputFocusCase 'changed-core-binary-rejected' {
    $other = New-InputFocusSource 'changed-unity-library'
    [IO.File]::AppendAllText((Join-Path $other.source 'UnityPlayer.dll'), 'changed')
    $run = New-InputFocusRun 'changed-unity'
    Expect-InputFocusFailure { Invoke-InputFocusFixture $other $run } 'Pinned installed-build bytes.*UnityPlayer'
    Require-InputFocusTest (!(Test-Path (Join-Path $run 'workspace'))) 'Changed Unity bytes created a copy.'
}

Test-InputFocusCase 'cooperator-wrong-external-hash-rejected' {
    Expect-InputFocusFailure { Read-InputFocusCooperator $cooperatorBundle ('0' * 64) $gitSha } 'evidence SHA-256 differs'
}
Test-InputFocusCase 'cooperator-wrong-commit-and-changed-dll-rejected' {
    Expect-InputFocusFailure { Read-InputFocusCooperator $cooperatorBundle $cooperatorEvidenceHash ('0' * 40) } 'build identity/checks differ'
    $otherBundle = Join-Path $testRoot 'changed-cooperator'
    New-Item -ItemType Directory -Path $otherBundle | Out-Null
    [IO.File]::Copy((Join-Path $cooperatorBundle 'build-evidence.json'), (Join-Path $otherBundle 'build-evidence.json'))
    [IO.File]::WriteAllText((Join-Path $otherBundle 'MystiaStewardCompanion.FocusProbe.dll'), 'changed fixture DLL')
    Expect-InputFocusFailure { Read-InputFocusCooperator $otherBundle $cooperatorEvidenceHash $gitSha } 'DLL hash/size differs'
}
Test-InputFocusCase 'cooperator-extra-file-and-lock-mismatch-rejected' {
    $otherBundle = Join-Path $testRoot 'wrong-cooperator-lock'
    New-Item -ItemType Directory -Path $otherBundle | Out-Null
    [IO.File]::Copy($cooperatorDll, (Join-Path $otherBundle 'MystiaStewardCompanion.FocusProbe.dll'))
    $wrong = [ordered]@{}
    foreach ($key in $cooperatorManifest.Keys) { $wrong[$key] = $cooperatorManifest[$key] }
    $wrong.referencesLockSha256 = '0' * 64
    Write-OldModNewJson (Join-Path $otherBundle 'build-evidence.json') $wrong
    $wrongHash = Get-InputFocusHash (Join-Path $otherBundle 'build-evidence.json')
    Expect-InputFocusFailure { Read-InputFocusCooperator $otherBundle $wrongHash $gitSha } 'lock identity differs'
    [IO.File]::WriteAllText((Join-Path $otherBundle 'unexpected.dll'), 'extra')
    Expect-InputFocusFailure { Read-InputFocusCooperator $otherBundle $wrongHash $gitSha } 'exactly its DLL and evidence'
}
Test-InputFocusCase 'source-with-existing-cooperator-rejected' {
    $other = New-InputFocusSource 'already-cooperating-library'
    New-Item -ItemType Directory -Path (Join-Path $other.source 'BepInEx/plugins/mystia-steward-companion-focus-probe') | Out-Null
    $run = New-InputFocusRun 'already-cooperating-run'
    Expect-InputFocusFailure { Invoke-InputFocusFixture $other $run } 'already contains a focus probe'
    Require-InputFocusTest (!(Test-Path (Join-Path $run 'workspace'))) 'Existing probe source created a copy.'
}

# Inject a source edit exactly after the real frozen copy, before the real final
# snapshot. This is an explicit fixture fault, not a production preparation hook.
$realCopy = (Get-Command Copy-OldModSnapshot).ScriptBlock
function Copy-OldModSnapshot($Source, $Destination, $Snapshot) {
    & $realCopy $Source $Destination $Snapshot
    [IO.File]::WriteAllText((Join-Path $Source 'change-during-prepare.txt'), 'fixture concurrent source edit')
}
Test-InputFocusCase 'late-source-change-retains-failed-workspace-and-never-publishes-sidecar' {
    $other = New-InputFocusSource 'changing-library'
    $run = New-InputFocusRun 'failed-copy'
    Expect-InputFocusFailure { Invoke-InputFocusFixture $other $run } 'Source game changed'
    Require-InputFocusTest (Test-Path (Join-Path $run 'workspace/game/UnityPlayer.dll')) 'Failed copy was deleted.'
    Require-InputFocusTest (Test-Path (Join-Path $run 'workspace/prepare-failure.json')) 'Failure evidence is missing.'
    Require-InputFocusTest (!(Test-Path (Join-Path $run 'input-probe.json')) -and !(Test-Path (Join-Path $run 'workspace/prepared-evidence.json'))) 'Failed workspace was published.'
    Require-InputFocusTest (Test-Path (Join-Path $other.source 'change-during-prepare.txt')) 'Concurrent source edit was rolled back.'
}
Write-OldModNewJson (Join-Path $testRoot 'test-report.json') ([ordered]@{
    schemaVersion = 1; result = 'passed'; scope = 'synthetic-files-and-metadata-only'
    productionIdentityGateTested = $true; fixtureIdentitySubstitution = $true; listenerEnumeration = 'model'; gameStarted = $false
    tests = $results.ToArray(); passed = $results.Count
})
Write-Host "PASS: $($results.Count) preparation fixture checks. No game started."
