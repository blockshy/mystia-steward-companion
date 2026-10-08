#requires -Version 7.0
param([string]$OutputParent = (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'temp'))
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'Prepare-Control-Probe.ps1')
if (!(Get-Item -LiteralPath $OutputParent -Force -ErrorAction SilentlyContinue)) {
    [void](Resolve-OldModPath $OutputParent -NewDirectory)
    New-Item -ItemType Directory -Path $OutputParent -ErrorAction Stop | Out-Null
}
$parent = Resolve-OldModPath $OutputParent
$testRoot = Join-Path $parent ('flutter-control-prepare-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
Write-Host "Preparation fixture evidence: $testRoot"
$productionPins = Get-ControlPinnedFiles
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
$controlModBundle = Join-Path $testRoot 'controlMod-bundle'
New-Item -ItemType Directory -Path $controlModBundle | Out-Null
$controlModDll = Join-Path $controlModBundle 'MystiaStewardCompanion.BepInEx.dll'
[IO.File]::WriteAllText($controlModDll, 'Non-executable replacement Mod fixture; never launch this DLL.')
$controlModManifest = [ordered]@{
    schemaVersion = 1; kind = 'flutter-control-mod-bundle'; commit = $gitSha; compiledGitSha = $gitSha; cleanCheckout = $true
    target = 'net6.0-windows-in-game'; entrypoint = 'MystiaStewardCompanion.BepInEx.dll'
    tools = @{ dotnetSdk = $controlToolchain.dotnetSdk; node = $controlToolchain.node }
    toolchainLockSha256 = Get-ControlHash $controlToolchainPath
    referencesLockSha256 = Get-ControlHash (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'mods/bepinex/References/references.lock.json')
    sourceFiles = @(@{ path = 'mods/bepinex/src/Plugin/CompanionProcessLauncher.cs'; size = 12; sha256 = 'a' * 64 })
    files = @(@{ path = 'MystiaStewardCompanion.BepInEx.dll'; size = (Get-Item $controlModDll).Length; sha256 = Get-ControlHash $controlModDll })
    checks = @{ lockedReferences = 'passed'; releaseBuild = 'passed'; gameRuntime = 'not-run' }
}
Write-OldModNewJson (Join-Path $controlModBundle 'build-evidence.json') $controlModManifest
$controlModEvidenceHash = Get-ControlHash (Join-Path $controlModBundle 'build-evidence.json')
# A model of environmental listener enumeration makes fixture tests independent
# of CI host ports. Production still reads the actual IPv4/IPv6 TCP listeners.
$fixtureListeners = @()
function Get-ControlListeners { return $fixtureListeners }

function Require-ControlTest([bool]$Value, [string]$Message) { if (!$Value) { throw $Message } }
function Test-ControlCase([string]$Name, [scriptblock]$Action) {
    & $Action
    $results.Add([ordered]@{ name = $Name; passed = $true })
}
function Expect-ControlFailure([scriptblock]$Action, [string]$Pattern) {
    $message = ''
    try { & $Action | Out-Null } catch { $message = $_.Exception.Message }
    Require-ControlTest ($message -match $Pattern) "Expected failure /$Pattern/, got: $message"
}
function New-ControlSource([string]$Name) {
    $source = Join-Path $testRoot "$Name/steamapps/common/Touhou Mystia Izakaya"
    foreach ($relative in $productionPins.Keys) {
        $file = Join-Path $source $relative
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($file)) | Out-Null
        if ($relative -like '*/MystiaStewardCompanion.BepInEx.dll') {
            [IO.File]::Copy($fixtureDll, $file)
        } else { [IO.File]::WriteAllText($file, "Non-executable control fixture: $relative") }
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
function New-ControlRun([string]$Name) {
    $path = Join-Path $testRoot $Name
    New-Item -ItemType Directory -Path $path | Out-Null
    $payload = Join-Path $path 'payload'
    New-Item -ItemType Directory -Path $payload | Out-Null
    [IO.File]::WriteAllText((Join-Path $payload 'mystia-steward-companion-window-probe.exe'), 'Non-executable Flutter host fixture.')
    return $path
}
function Invoke-ControlFixture($Source, [string]$Run, [int]$FixturePort = 32755, [switch]$Lifecycle) {
    Invoke-ControlPreparation -RunDirectory $Run -RunId ([IO.Path]::GetFileName($Run)) -GitSha $gitSha `
        -SourceGameDirectory $Source.source -SteamAppManifestPath $Source.manifest -Port $FixturePort `
        -ModBundleDirectory $controlModBundle -ModEvidenceSha256 $controlModEvidenceHash -Lifecycle:$Lifecycle
}

$source = New-ControlSource 'source-library'
$baseline = Get-OldModSnapshot $source.source
Test-ControlCase 'consumer-rejects-unpinned-synthetic-game-before-copy' {
    $run = New-ControlRun 'real-pins-reject-fixture'
    Expect-ControlFailure { Invoke-ControlFixture $source $run } 'Pinned installed-build bytes'
    Require-ControlTest (!(Test-Path (Join-Path $run 'workspace'))) 'Unpinned bytes created a workspace.'
}

# Only this test scope substitutes the immutable identity baseline. The public
# preparation script has no fixture switch, alternate-hash input, or test bypass.
# All filesystem/Steam parsing/copy/hash/metadata/config/publication work remains real.
$fixturePins = [ordered]@{}
foreach ($relative in $productionPins.Keys) { $fixturePins[$relative] = Get-ControlHash (Join-Path $source.source $relative) }
function Get-ControlPinnedFiles { return $fixturePins }

Test-ControlCase 'complete-fixture-transaction-preserves-source-and-publishes-bound-sidecar' {
    $run = New-ControlRun 'prepared-fixture'
    Invoke-ControlFixture $source $run *> (Join-Path $testRoot 'prepare.log')
    $workspace = Join-Path $run 'workspace'
    $sidecar = Get-Content (Join-Path $run 'control-probe.json') -Raw | ConvertFrom-Json -AsHashtable
    $evidencePath = Join-Path $workspace 'prepared-evidence.json'
    $evidence = Get-Content $evidencePath -Raw | ConvertFrom-Json -AsHashtable
    Require-ControlTest ($sidecar.Count -eq 14 -and $sidecar.schemaVersion -eq 1 -and $sidecar.runId -ceq 'prepared-fixture' -and $sidecar.gitSha -ceq $gitSha) 'Sidecar identity/schema is wrong.'
    Require-ControlTest ($sidecar.expectedModSha256 -ceq (Get-ControlHash $evidence.modReplacement.dllPath) -and
        $sidecar.modBuildEvidenceSha256 -ceq (Get-ControlHash (Join-Path $workspace 'mod-build-evidence.json')) -and
        $evidence.modReplacement.originalGameModPreserved -ceq $true -and
        $evidence.modReplacement.originalSha256 -ceq $fixturePins['BepInEx/plugins/mystia-steward-companion/MystiaStewardCompanion.BepInEx.dll'] -and
        $evidence.modReplacement.sha256 -ceq $sidecar.expectedModSha256 -and
        $evidence.modReplacement.originalSha256 -cne $evidence.modReplacement.sha256 -and
        $evidence.modReplacement.focusCooperatorInstalled -ceq $false) 'Replacement Mod DLL/build evidence is not bound to the copy.'
    Require-ControlTest ($sidecar.expectedBepInExSha256 -ceq (Get-ControlHash (Join-Path $evidence.gameDirectory 'BepInEx/core/BepInEx.Unity.IL2CPP.dll'))) 'BepInEx hash differs.'
    Require-ControlTest ($evidence.controlPort -eq 32146 -and $evidence.apiPort -eq 32755 -and $evidence.controlPortFreeAtPreparation -ceq $true) 'Control/API port evidence differs.'
    Require-ControlTest ($evidence.windowExecutableSha256 -ceq (Get-ControlHash $evidence.windowExecutable)) 'Flutter host is not bound.'
    Require-ControlTest (!(Test-Path (Join-Path $evidence.gameDirectory 'BepInEx/plugins/mystia-steward-companion-focus-probe'))) 'A focus cooperator was installed.'
    Require-ControlTest ($sidecar.gameExecutable -ceq (ConvertTo-ControlPath (Join-Path $workspace 'game/Touhou Mystia Izakaya.exe'))) 'Game path is not this run copy.'
    Require-ControlTest ($sidecar.preparedEvidenceSha256 -ceq (Get-ControlHash $evidencePath)) 'Sidecar does not bind exact evidence bytes.'
    foreach ($pair in @(@('expectedExeSha256', 'Touhou Mystia Izakaya.exe'), @('expectedUnityPlayerSha256', 'UnityPlayer.dll'),
        @('expectedGameAssemblySha256', 'GameAssembly.dll'), @('expectedMetadataSha256', 'Touhou Mystia Izakaya_Data/il2cpp_data/Metadata/global-metadata.dat'))) {
        Require-ControlTest ($sidecar[$pair[0]] -ceq (Get-ControlHash (Join-Path $evidence.gameDirectory $pair[1]))) 'A binary hash does not bind the copied file.'
    }
    Require-ControlTest ($evidence.result -ceq 'prepared' -and $evidence.sourceUnchanged -ceq $true -and $evidence.copiedFilesVerified -ceq $true) 'Prepared transaction was not recorded.'
    Require-ControlTest ($evidence.sourceSnapshotSha256 -ceq (Get-ControlHash $evidence.sourceSnapshotPath)) 'Source snapshot hash differs.'
    Require-ControlTest ($sidecar.steamAppId -ceq '1584090' -and $sidecar.steamBuildId -ceq '23158340') 'Steam identity differs.'
    Require-ControlTest ([Convert]::ToHexString([IO.File]::ReadAllBytes($evidence.steamIdentity.developmentFilePath)) -ceq '313538343039300A') 'AppID file bytes differ.'
    Require-ControlTest (!(Test-Path (Join-Path $source.source 'steam_appid.txt'))) 'Source AppID file was created.'
    Require-ControlTest (!(Test-Path (Join-Path $workspace 'staging')) -and !(Test-Path (Join-Path $evidence.gameDirectory 'BepInEx/config/MystiaStewardCompanion'))) 'Update or old user state was prepared/inherited.'
    $cfg = Get-Content $evidence.config.path -Raw
    Require-ControlTest ($cfg -match '\[Updates\]\nEnabled = false\nAutoCheck = false' -and $cfg -match 'AutoLaunch = false' -and $cfg -match 'AllowLanConnections = false') 'Isolation settings are missing.'
    Require-ControlTest ($cfg.Contains("ControlProtocol = IdentityPipeV1`nExecutablePath = $($evidence.windowExecutable)`n")) 'Identity protocol or fixed Flutter executable is missing.'
    Require-ControlTest ($cfg -match '(?m)^Token = ([a-f0-9]{64})$') 'Random token is missing.'
    $token = $Matches[1]
    foreach ($json in Get-ChildItem $run -Filter '*.json' -Recurse) {
        $text = Get-Content $json.FullName -Raw
        Require-ControlTest (!$text.Contains($token) -and !$text.Contains('original-fixture-secret')) 'A token was leaked into evidence.'
        Require-ControlTest (!$text.Contains('\\')) 'Evidence paths are not normalized to forward slashes.'
    }
    Require-ControlTest ($evidence.config.sha256 -ceq (Get-ControlHash $evidence.config.path) -and $evidence.config.port -eq 32755) 'Configuration hash/port differ.'
    Require-ControlTest (@($evidence.verifiedFiles).Count -eq 7 -and $evidence.gameStarted -ceq $false -and $evidence.stagingPrepared -ceq $false) 'Preparation evidence overclaims work.'
    Assert-OldModSnapshotEqual $baseline (Get-OldModSnapshot $source.source) 'Source after preparation'
    $before = Get-OldModSnapshot $run
    Expect-ControlFailure { Invoke-ControlFixture $source $run } 'already exists'
    Assert-OldModSnapshotEqual $before (Get-OldModSnapshot $run) 'Existing run must not change'
}

Test-ControlCase 'lifecycle-opt-in-binds-autolaunch-and-hashed-token-without-changing-source' {
    $run = New-ControlRun 'lifecycle-fixture'
    Invoke-ControlFixture $source $run -Lifecycle *> (Join-Path $testRoot 'lifecycle-prepare.log')
    $authorizationPath = Join-Path $run 'control-lifecycle.json'
    $authorization = Get-Content $authorizationPath -Raw | ConvertFrom-Json -AsHashtable
    $evidencePath = Join-Path $run 'workspace/prepared-evidence.json'
    $evidence = Get-Content $evidencePath -Raw | ConvertFrom-Json -AsHashtable
    $cfg = Get-Content $evidence.config.path -Raw
    Require-ControlTest ($cfg -match '(?m)^AutoLaunch = true$' -and $evidence.config.autoLaunch -ceq $true) 'Lifecycle preparation did not enable cold launch.'
    Require-ControlTest ($cfg -match '(?m)^Token = ([a-f0-9]{64})$') 'Lifecycle random token is missing.'
    $token = $Matches[1]
    $tokenHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($token))).ToLowerInvariant()
    Require-ControlTest ($authorization.Count -eq 7 -and $authorization.schemaVersion -eq 1 -and
        $authorization.kind -ceq 'control-lifecycle-authorization' -and $authorization.scenario -ceq 'new-mod-cold-restart' -and
        $authorization.runId -ceq 'lifecycle-fixture' -and $authorization.gitSha -ceq $gitSha -and
        $authorization.tokenSha256 -ceq $tokenHash -and $authorization.preparedEvidenceSha256 -ceq (Get-ControlHash $evidencePath)) 'Lifecycle authorization is not bound to the prepared run.'
    foreach ($json in Get-ChildItem $run -Filter '*.json' -Recurse) {
        Require-ControlTest (!(Get-Content $json.FullName -Raw).Contains($token)) 'Lifecycle token leaked into evidence.'
    }
    Assert-OldModSnapshotEqual $baseline (Get-OldModSnapshot $source.source) 'Lifecycle source after preparation'
    $before = Get-OldModSnapshot $run
    Expect-ControlFailure { Invoke-ControlFixture $source $run -Lifecycle } 'already exists'
    Assert-OldModSnapshotEqual $before (Get-OldModSnapshot $run) 'Lifecycle authorization cannot be overwritten'
}

foreach ($portValue in @(32145, 32146, 32756)) {
    Test-ControlCase "non-isolated-api-port-$portValue-rejected" {
        $run = New-ControlRun "port-$portValue"
        Expect-ControlFailure { Invoke-ControlFixture $source $run $portValue } 'exactly 32755'
        Require-ControlTest (!(Test-Path (Join-Path $run 'workspace'))) 'Invalid port created a workspace.'
    }
}
Test-ControlCase 'existing-ipv6-listener-rejects-port-before-copy' {
    $fixtureListeners = @([Net.IPEndPoint]::new([Net.IPAddress]::IPv6Any, 32755))
    $run = New-ControlRun 'occupied-port'
    try {
        Expect-ControlFailure { Invoke-ControlFixture $source $run } 'already has a listener'
        Require-ControlTest (!(Test-Path (Join-Path $run 'workspace'))) 'Occupied port created a workspace.'
    } finally { $fixtureListeners = @() }
}
foreach ($address in @([Net.IPAddress]::Any, [Net.IPAddress]::Loopback, [Net.IPAddress]::IPv6Any, [Net.IPAddress]::IPv6Loopback)) {
    Test-ControlCase "control-port-occupied-$address-rejects-without-touching-existing-instance" {
        $fixtureListeners = @([Net.IPEndPoint]::new($address, 32146))
        $run = New-ControlRun ('occupied-control-' + [guid]::NewGuid().ToString('N'))
        $before = Get-OldModSnapshot $run
        try {
            Expect-ControlFailure { Invoke-ControlFixture $source $run } 'Control port 32146 already has a listener'
            Require-ControlTest (!(Test-Path (Join-Path $run 'workspace'))) 'Occupied control port created a workspace.'
            Assert-OldModSnapshotEqual $before (Get-OldModSnapshot $run) 'Occupied control port must leave the run untouched'
        } finally { $fixtureListeners = @() }
    }
}
Test-ControlCase 'mismatched-run-id-and-preexisting-sidecar-rejected' {
    $run = New-ControlRun 'invalid-run-identity'
    Expect-ControlFailure {
        Invoke-ControlPreparation -RunDirectory $run -RunId 'other' -GitSha $gitSha -SourceGameDirectory $source.source -SteamAppManifestPath $source.manifest
    } 'leaf must equal'
    [IO.File]::WriteAllText((Join-Path $run 'control-probe.json'), 'preserve this existing evidence')
    Expect-ControlFailure { Invoke-ControlFixture $source $run } 'already contains'
    Require-ControlTest ((Get-Content (Join-Path $run 'control-probe.json') -Raw) -ceq 'preserve this existing evidence') 'Existing evidence was overwritten.'
}
Test-ControlCase 'source-and-run-overlap-rejected' {
    $run = Join-Path $source.source 'nested-run'
    New-Item -ItemType Directory -Path $run | Out-Null
    Expect-ControlFailure { Invoke-ControlFixture $source $run } 'disjoint'
    Require-ControlTest (!(Test-Path (Join-Path $run 'workspace'))) 'Nested workspace was created.'
}
Test-ControlCase 'wrong-build-and-wrong-source-appid-rejected-without-overwrite' {
    $other = New-ControlSource 'wrong-steam-library'
    $text = [IO.File]::ReadAllText($other.manifest)
    [IO.File]::WriteAllText($other.manifest, $text.Replace('23158340', '23158341'))
    $run = New-ControlRun 'wrong-build'
    Expect-ControlFailure { Invoke-ControlFixture $other $run } 'pinned installed build'
    [IO.File]::WriteAllText($other.manifest, $text)
    [IO.File]::WriteAllText((Join-Path $other.source 'steam_appid.txt'), '480')
    $before = Get-OldModSnapshot $other.source
    Expect-ControlFailure { Invoke-ControlFixture $other $run } 'real App ID'
    Assert-OldModSnapshotEqual $before (Get-OldModSnapshot $other.source) 'Wrong source Steam file'
    Require-ControlTest (!(Test-Path (Join-Path $run 'workspace'))) 'Invalid Steam identity created a copy.'
}
Test-ControlCase 'source-link-and-external-loader-rejected' {
    $other = New-ControlSource 'unsafe-loader-library'
    [IO.File]::WriteAllText((Join-Path $other.source 'doorstop_config.ini'), 'target_assembly = E:outside\core.dll')
    $run = New-ControlRun 'unsafe-loader'
    Expect-ControlFailure { Invoke-ControlFixture $other $run } 'non-local path'
    $linked = New-ControlSource 'linked-library'
    $type = if ($IsWindows) { 'Junction' } else { 'SymbolicLink' }
    New-Item -ItemType $type -Path (Join-Path $linked.source 'external-directory') -Target $source.source | Out-Null
    Expect-ControlFailure { Invoke-ControlFixture $linked $run } 'links/reparse'
    Require-ControlTest (!(Test-Path (Join-Path $run 'workspace'))) 'Unsafe source created a copy.'
}
Test-ControlCase 'changed-core-binary-rejected' {
    $other = New-ControlSource 'changed-unity-library'
    [IO.File]::AppendAllText((Join-Path $other.source 'UnityPlayer.dll'), 'changed')
    $run = New-ControlRun 'changed-unity'
    Expect-ControlFailure { Invoke-ControlFixture $other $run } 'Pinned installed-build bytes.*UnityPlayer'
    Require-ControlTest (!(Test-Path (Join-Path $run 'workspace'))) 'Changed Unity bytes created a copy.'
}

Test-ControlCase 'controlMod-wrong-external-hash-rejected' {
    Expect-ControlFailure { Read-ControlMod $controlModBundle ('0' * 64) $gitSha } 'evidence SHA-256 differs'
}
Test-ControlCase 'controlMod-wrong-commit-and-changed-dll-rejected' {
    Expect-ControlFailure { Read-ControlMod $controlModBundle $controlModEvidenceHash ('0' * 40) } 'build identity/checks differ'
    $otherBundle = Join-Path $testRoot 'changed-controlMod'
    New-Item -ItemType Directory -Path $otherBundle | Out-Null
    [IO.File]::Copy((Join-Path $controlModBundle 'build-evidence.json'), (Join-Path $otherBundle 'build-evidence.json'))
    [IO.File]::WriteAllText((Join-Path $otherBundle 'MystiaStewardCompanion.BepInEx.dll'), 'changed fixture DLL')
    Expect-ControlFailure { Read-ControlMod $otherBundle $controlModEvidenceHash $gitSha } 'DLL hash/size differs'
}
Test-ControlCase 'controlMod-extra-file-and-lock-mismatch-rejected' {
    $otherBundle = Join-Path $testRoot 'wrong-controlMod-lock'
    New-Item -ItemType Directory -Path $otherBundle | Out-Null
    [IO.File]::Copy($controlModDll, (Join-Path $otherBundle 'MystiaStewardCompanion.BepInEx.dll'))
    $wrong = [ordered]@{}
    foreach ($key in $controlModManifest.Keys) { $wrong[$key] = $controlModManifest[$key] }
    $wrong.referencesLockSha256 = '0' * 64
    Write-OldModNewJson (Join-Path $otherBundle 'build-evidence.json') $wrong
    $wrongHash = Get-ControlHash (Join-Path $otherBundle 'build-evidence.json')
    Expect-ControlFailure { Read-ControlMod $otherBundle $wrongHash $gitSha } 'lock identity differs'
    [IO.File]::WriteAllText((Join-Path $otherBundle 'unexpected.dll'), 'extra')
    Expect-ControlFailure { Read-ControlMod $otherBundle $wrongHash $gitSha } 'exactly its DLL and evidence'
}
Test-ControlCase 'controlMod-dirty-and-invalid-source-manifests-rejected' {
    foreach ($fault in @('dirty', 'string-clean-flag', 'missing-sources', 'duplicate-source', 'traversal', 'invalid-size', 'omitted-mod-sources')) {
        $otherBundle = Join-Path $testRoot "manifest-$fault"
        New-Item -ItemType Directory -Path $otherBundle | Out-Null
        [IO.File]::Copy($controlModDll, (Join-Path $otherBundle 'MystiaStewardCompanion.BepInEx.dll'))
        $wrong = $controlModManifest | ConvertTo-Json -Depth 10 | ConvertFrom-Json -AsHashtable
        $pattern = 'source manifest'
        switch ($fault) {
            'dirty' { $wrong.cleanCheckout = $false; $pattern = 'build identity/checks differ' }
            'string-clean-flag' { $wrong.cleanCheckout = 'true'; $pattern = 'build identity/checks differ' }
            'missing-sources' { $wrong.sourceFiles = @() }
            'duplicate-source' { $wrong.sourceFiles = @($wrong.sourceFiles[0], $wrong.sourceFiles[0]) }
            'traversal' { $wrong.sourceFiles[0].path = 'mods/../outside.cs' }
            'invalid-size' { $wrong.sourceFiles[0].size = '12' }
            'omitted-mod-sources' { $wrong.sourceFiles[0].path = 'scripts/unrelated.mjs' }
        }
        $wrongPath = Join-Path $otherBundle 'build-evidence.json'
        Write-OldModNewJson $wrongPath $wrong
        Expect-ControlFailure { Read-ControlMod $otherBundle (Get-ControlHash $wrongPath) $gitSha } $pattern
    }
}
Test-ControlCase 'source-with-existing-focus-cooperator-rejected' {
    $other = New-ControlSource 'already-cooperating-library'
    New-Item -ItemType Directory -Path (Join-Path $other.source 'BepInEx/plugins/mystia-steward-companion-focus-probe') | Out-Null
    $run = New-ControlRun 'already-cooperating-run'
    Expect-ControlFailure { Invoke-ControlFixture $other $run } 'already contains a focus probe'
    Require-ControlTest (!(Test-Path (Join-Path $run 'workspace'))) 'Existing probe source created a copy.'
}
Test-ControlCase 'focus-cooperator-in-another-plugin-directory-rejected' {
    $other = New-ControlSource 'other-cooperator-library'
    [IO.File]::WriteAllText((Join-Path $other.source 'BepInEx/plugins/MystiaStewardCompanion.FocusProbe.dll'), 'Synthetic focus cooperator in another location.')
    $run = New-ControlRun 'other-cooperator-run'
    Expect-ControlFailure { Invoke-ControlFixture $other $run } 'already contains a focus probe'
    Require-ControlTest (!(Test-Path (Join-Path $run 'workspace'))) 'Alternate cooperator source created a copy.'
}

# Faults occur after the real frozen copy. These are explicit fixture injections,
# not production preparation hooks. The generated failure evidence is retained.
$realCopy = (Get-Command Copy-OldModSnapshot).ScriptBlock
$fixtureCopyFault = {}
function Copy-OldModSnapshot($Source, $Destination, $Snapshot) {
    & $realCopy $Source $Destination $Snapshot
    & $fixtureCopyFault $Source $Destination
}
Test-ControlCase 'late-source-change-retains-failed-workspace-and-never-publishes-sidecar' {
    $fixtureCopyFault = { param($Source, $Destination)
        [IO.File]::WriteAllText((Join-Path $Source 'change-during-prepare.txt'), 'fixture concurrent source edit')
    }
    $other = New-ControlSource 'changing-library'
    $run = New-ControlRun 'failed-copy'
    Expect-ControlFailure { Invoke-ControlFixture $other $run } 'Source game changed'
    Require-ControlTest (Test-Path (Join-Path $run 'workspace/game/UnityPlayer.dll')) 'Failed copy was deleted.'
    Require-ControlTest (Test-Path (Join-Path $run 'workspace/prepare-failure.json')) 'Failure evidence is missing.'
    Require-ControlTest (!(Test-Path (Join-Path $run 'control-probe.json')) -and !(Test-Path (Join-Path $run 'workspace/prepared-evidence.json'))) 'Failed workspace was published.'
    Require-ControlTest (Test-Path (Join-Path $other.source 'change-during-prepare.txt')) 'Concurrent source edit was rolled back.'
}
Test-ControlCase 'control-port-became-occupied-during-copy-withholds-sidecar' {
    $fixtureCopyFault = { param($Source, $Destination)
        $script:fixtureListeners = @([Net.IPEndPoint]::new([Net.IPAddress]::IPv6Any, 32146))
    }
    $run = New-ControlRun 'late-control-listener'
    try {
        Expect-ControlFailure { Invoke-ControlFixture $source $run } 'Control port 32146 already has a listener'
        Require-ControlTest (Test-Path (Join-Path $run 'workspace/prepare-failure.json')) 'Late port conflict has no retained failure evidence.'
        Require-ControlTest (!(Test-Path (Join-Path $run 'control-probe.json')) -and !(Test-Path (Join-Path $run 'workspace/prepared-evidence.json'))) 'Late occupied port published preparation.'
    } finally { $script:fixtureListeners = @() }
}
Test-ControlCase 'window-payload-changed-during-copy-withholds-sidecar' {
    $fixtureCopyFault = { param($Source, $Destination)
        $runPath = Split-Path (Split-Path $Destination -Parent) -Parent
        [IO.File]::AppendAllText((Join-Path $runPath 'payload/mystia-steward-companion-window-probe.exe'), 'fixture concurrent payload change')
    }
    $run = New-ControlRun 'late-window-change'
    Expect-ControlFailure { Invoke-ControlFixture $source $run } 'Window payload changed during preparation'
    Require-ControlTest (Test-Path (Join-Path $run 'workspace/prepare-failure.json')) 'Changed window payload has no retained failure evidence.'
    Require-ControlTest (!(Test-Path (Join-Path $run 'control-probe.json')) -and !(Test-Path (Join-Path $run 'workspace/prepared-evidence.json'))) 'Changed window payload published preparation.'
}
Write-OldModNewJson (Join-Path $testRoot 'test-report.json') ([ordered]@{
    schemaVersion = 1; result = 'passed'; scope = 'synthetic-files-and-metadata-only'
    productionIdentityGateTested = $true; fixtureIdentitySubstitution = $true; listenerEnumeration = 'model'; gameStarted = $false
    tests = $results.ToArray(); passed = $results.Count
})
Write-Host "PASS: $($results.Count) preparation fixture checks. No game started."
