#requires -Version 7.0
# Preparation only. Never starts a process, requests an update, or writes the source.
param(
    [string]$RunDirectory,
    [string]$RunId,
    [string]$GitSha,
    [string]$SourceGameDirectory,
    [string]$SteamAppManifestPath,
    [string]$ModBundleDirectory,
    [string]$ModEvidenceSha256,
    [switch]$Lifecycle,
    [switch]$LegacyClient,
    [switch]$ExitDiagnostic,
    [ValidateSet(32755)][int]$Port = 32755
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$controlCommon = Join-Path $PSScriptRoot '../flutter-old-mod-probe/Prepare-OldMod-Probe.Common.psm1'
Import-Module $controlCommon -Force -DisableNameChecking
$controlToolchainPath = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'toolchain.lock.json'
[void](Assert-OldModFile $controlToolchainPath)
$controlToolchain = Get-Content -LiteralPath $controlToolchainPath -Raw | ConvertFrom-Json -AsHashtable
if ($controlToolchain.powershell -isnot [string] -or $PSVersionTable.PSVersion.ToString() -cne $controlToolchain.powershell) {
    throw 'Preparation requires the exact PowerShell version from toolchain.lock.json.'
}

function Get-ControlPinnedFiles {
    # The user's installed build, frozen and verified in old-Mod run02. These
    # values are not caller-selectable and do not claim an official Release SHA.
    return [ordered]@{
        'Touhou Mystia Izakaya.exe' = '208dd4627a0bb98acdb61d0b5c8b9a2ea1de6921669f52a8c77ced38101858a2'
        'UnityPlayer.dll' = '6673961406f3fc693ad80a5987b02eb4fe859ac198baa15dc8fe848d4f6f5ec4'
        'GameAssembly.dll' = '91ce5ae3dad5da07dfed63bab4c9e454f67b6e50f9a6e8ec498ef9b0b806a789'
        'Touhou Mystia Izakaya_Data/il2cpp_data/Metadata/global-metadata.dat' = '995d1a08cac7a784d397927cf73ae71a8ce47cc8637cc4dd7ea534a3368b31e7'
        'BepInEx/plugins/mystia-steward-companion/MystiaStewardCompanion.BepInEx.dll' = 'e1e3603ccb3a35e17f8ade9f70178d4f82f710b9ffb0df1cbbbddc901ec9bc33'
        'BepInEx/core/BepInEx.Unity.IL2CPP.dll' = '1227bd4e73e9d48bc5879e3b000c43aadcd1638594d6ec6287a67d2479acb79e'
        'winhttp.dll' = '8c6cdbc38836dee87e3368f5de1994d7c0ccebf29e4ce7aba3c0981f9375412c'
    }
}

function ConvertTo-ControlPath([string]$Path) {
    return [IO.Path]::GetFullPath($Path).Replace('\', '/')
}

function Get-ControlHash([string]$Path) {
    [void](Assert-OldModFile $Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-ControlListeners {
    return [Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners()
}

function Read-ControlMod([string]$Directory, [string]$ExpectedEvidenceHash, [string]$ExpectedCommit) {
    if ($ExpectedEvidenceHash -cnotmatch '\A[a-f0-9]{64}\z') { throw 'Control Mod requires an externally verified evidence SHA-256.' }
    $bundle = Resolve-OldModPath $Directory
    $names = @(Get-ChildItem -LiteralPath $bundle -Force | ForEach-Object { [void](Assert-OldModFile $_.FullName); $_.Name })
    if (@(Compare-Object ($names | Sort-Object) @('build-evidence.json', 'MystiaStewardCompanion.BepInEx.dll') -CaseSensitive).Count -ne 0) { throw 'Control Mod bundle must contain exactly its DLL and evidence.' }
    $evidencePath = Join-Path $bundle 'build-evidence.json'
    if ((Get-ControlHash $evidencePath) -cne $ExpectedEvidenceHash) { throw 'Control Mod evidence SHA-256 differs.' }
    if ((Get-Item $evidencePath).Length -gt 1MB) { throw 'Control Mod evidence exceeds 1 MiB.' }
    $value = Get-Content -LiteralPath $evidencePath -Raw | ConvertFrom-Json -AsHashtable
    if ($value.schemaVersion -ne 1 -or $value.kind -cne 'flutter-control-mod-bundle' -or
        $value.commit -cne $ExpectedCommit -or $value.compiledGitSha -cne $ExpectedCommit -or ($value.cleanCheckout -isnot [bool] -or $value.cleanCheckout -cne $true) -or
        $value.target -cne 'net6.0-windows-in-game' -or $value.entrypoint -cne 'MystiaStewardCompanion.BepInEx.dll' -or
        $value.tools.dotnetSdk -cne $controlToolchain.dotnetSdk -or $value.tools.node -cne $controlToolchain.node -or
        $value.checks.lockedReferences -cne 'passed' -or $value.checks.releaseBuild -cne 'passed' -or $value.checks.gameRuntime -cne 'not-run') {
        throw 'Control Mod build identity/checks differ.'
    }
    $referenceLock = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'mods/bepinex/References/references.lock.json'
    if ($value.toolchainLockSha256 -cne (Get-ControlHash $controlToolchainPath) -or
        $value.referencesLockSha256 -cne (Get-ControlHash $referenceLock)) { throw 'Control Mod lock identity differs.' }
    if ($value.files -isnot [array] -or $value.files.Count -ne 1 -or $value.files[0].path -cne $value.entrypoint) { throw 'Control Mod DLL manifest differs.' }
    if ($value.sourceFiles -isnot [array] -or $value.sourceFiles.Count -eq 0) { throw 'Control Mod source manifest is missing.' }
    $sourceNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($record in $value.sourceFiles) {
        if ($record.path -isnot [string] -or $record.path -cnotmatch '\A[A-Za-z0-9_.-]+(?:/[A-Za-z0-9_.-]+)*\z' -or
            ($record.path -split '/') -contains '..' -or ($record.path -split '/') -contains '.' -or
            !$sourceNames.Add($record.path) -or $record.sha256 -cnotmatch '\A[a-f0-9]{64}\z' -or
            $record.size -isnot [long] -or $record.size -lt 0) { throw 'Control Mod source manifest record is invalid.' }
    }
    if (@($value.sourceFiles | Where-Object path -CLike 'mods/bepinex/src/*').Count -eq 0) { throw 'Control Mod source manifest omits Mod sources.' }
    $dll = Join-Path $bundle $value.entrypoint
    $hash = Get-ControlHash $dll
    if ($value.files[0].sha256 -cne $hash -or $value.files[0].size -ne (Get-Item $dll).Length) { throw 'Control Mod DLL hash/size differs.' }
    if ($value.ContainsKey('exitDiagnostic') -and ($value.exitDiagnostic -isnot [bool] -or $value.exitDiagnostic -cne $true)) {
        throw 'Control Mod diagnostic flag must be explicit true or absent.'
    }
    return @{ dll = $dll; sha256 = $hash; evidence = $evidencePath; evidenceSha256 = $ExpectedEvidenceHash; exitDiagnostic = $value.ContainsKey('exitDiagnostic') }
}

function Assert-ControlPortFree([int]$Value) {
    if ($Value -ne 32755) { throw 'The isolated API port must be exactly 32755.' }
    # Read only; preparing a fixture does not reserve or bind any listener.
    $listeners = @(Get-ControlListeners)
    if (@($listeners | Where-Object Port -EQ 32146).Count -ne 0) {
        throw 'Control port 32146 already has a listener; keep the existing instance untouched.'
    }
    if (@($listeners | Where-Object Port -EQ $Value).Count -ne 0) {
        throw 'The requested isolated API port already has a listener.'
    }
}

function Assert-ControlFrozenIdentity($Snapshot, $Pinned) {
    foreach ($relative in $Pinned.Keys) {
        $matches = @($Snapshot.files | Where-Object path -IEQ $relative)
        if ($matches.Count -ne 1 -or $matches[0].path -cne $relative -or $matches[0].sha256 -cne $Pinned[$relative]) {
            throw "Pinned installed-build bytes do not match: $relative"
        }
    }
}

function Invoke-ControlPreparation {
    param(
        [string]$RunDirectory, [string]$RunId, [string]$GitSha,
        [string]$SourceGameDirectory, [string]$SteamAppManifestPath,
        [string]$ModBundleDirectory, [string]$ModEvidenceSha256,
        [switch]$Lifecycle,
        [switch]$LegacyClient,
        [switch]$ExitDiagnostic,
        [int]$Port = 32755
    )
    if ($RunId -cnotmatch '\A[A-Za-z0-9][A-Za-z0-9_-]{0,79}\z' -or $GitSha -cnotmatch '\A[a-f0-9]{40}\z') {
        throw 'Preparation requires an explicit run ID and full lowercase Git SHA.'
    }
    if ($Lifecycle -and $LegacyClient) { throw 'Choose exactly one lifecycle scenario.' }
    if ($ExitDiagnostic -and (!$Lifecycle -or $LegacyClient)) { throw 'Exit diagnostic requires only the new-Mod Lifecycle scenario.' }
    if ($LegacyClient -and ($ModBundleDirectory -or $ModEvidenceSha256)) { throw 'Legacy client preserves the original Mod; a replacement bundle is forbidden.' }
    $run = Resolve-OldModPath $RunDirectory
    if ([IO.Path]::GetFileName($run) -cne $RunId) { throw 'Run directory leaf must equal the exact run ID.' }
    $source = Resolve-OldModPath $SourceGameDirectory
    if (Test-OldModOverlap $source $run) { throw 'Source game and run directory must be disjoint.' }
    $workspace = Resolve-OldModPath (Join-Path $run 'workspace') -NewDirectory
    $sidecar = Join-Path $run 'control-probe.json'
    $windowExe = Join-Path $run 'payload/mystia-steward-companion-window-probe.exe'
    [void](Assert-OldModFile $windowExe)
    $windowPath = ConvertTo-ControlPath $windowExe
    if ($windowPath -cmatch '[^\x20-\x7e]') { throw 'Control probe paths must contain printable ASCII only.' }
    $windowHash = Get-ControlHash $windowExe
    foreach ($name in @('control-probe.json', 'control-lifecycle.json', 'control-exit-diagnostic.json', 'control-exit-diagnostic-result.json', 'control-launch-1.json', 'control-launch-2.json', 'input-probe.json', 'probe-result.json', 'result.json', 'status.json', 'foreground-session.json', 'game-foreground-evidence.json', 'control-native-evidence.json', 'native-cleanup.json')) {
        if (Get-Item -LiteralPath (Join-Path $run $name) -Force -ErrorAction SilentlyContinue) {
            throw "Run already contains preparation or execution evidence: $name"
        }
    }
    Assert-ControlPortFree $Port
    $steam = Read-OldModSteamIdentity $SteamAppManifestPath $source
    if ($steam.buildId -cne '23158340') { throw 'Steam build must match the pinned installed build 23158340.' }
    $appidFiles = @(Get-ChildItem -LiteralPath $source -Force | Where-Object Name -IEQ 'steam_appid.txt')
    if ($appidFiles.Count -gt 1 -or ($appidFiles.Count -eq 1 -and $appidFiles[0].Name -cne 'steam_appid.txt')) {
        throw 'Source Steam development filename is ambiguous or noncanonical.'
    }
    if ($appidFiles.Count -eq 1) { Assert-OldModSteamAppIdFile $appidFiles[0].FullName }
    $pinned = Get-ControlPinnedFiles
    Write-Host 'Freezing the source game. Keep it closed; preparation never starts a game.'
    $frozen = Get-OldModSnapshot $source
    Assert-ControlFrozenIdentity $frozen $pinned
    $controlMod = if ($LegacyClient) { $null } else { Read-ControlMod $ModBundleDirectory $ModEvidenceSha256 $GitSha }
    if (!$LegacyClient -and $controlMod.exitDiagnostic -cne [bool]$ExitDiagnostic) { throw 'Diagnostic bundle and explicit ExitDiagnostic preparation must match.' }
    if ((Test-Path -LiteralPath (Join-Path $source 'BepInEx/plugins/mystia-steward-companion-focus-probe')) -or
        @($frozen.files | Where-Object path -IMatch '^BepInEx/plugins/(?:.*/)?MystiaStewardCompanion\.FocusProbe\.dll$').Count -ne 0) {
        throw 'The original game already contains a focus probe; source must remain original.'
    }
    Assert-OldModLaunchConfiguration $source $frozen
    $dllRelative = 'BepInEx/plugins/mystia-steward-companion/MystiaStewardCompanion.BepInEx.dll'
    if ((Get-OldModPluginVersion (Join-Path $source $dllRelative)) -cne '1.3.1') { throw 'Expected the exact Mod PluginVersion constant 1.3.1.' }
    [void](Assert-OldModFile (Join-Path $source 'doorstop_config.ini'))
    $selected = Select-OldModGameSnapshot $frozen
    [long]$copyBytes = $selected.totalBytes + 8
    if (!$LegacyClient) { $copyBytes += (Get-Item $controlMod.dll).Length + (Get-Item $controlMod.evidence).Length }
    [long]$reserve = [Math]::Max(1GB, [Math]::Ceiling($copyBytes * 0.1))
    $drive = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($workspace))
    Write-Host "Game copy: $copyBytes bytes; reserved headroom: $reserve bytes; free: $($drive.AvailableFreeSpace) bytes."
    Assert-OldModDiskBudget $drive.AvailableFreeSpace ($copyBytes + $reserve)
    [void](Resolve-OldModPath $workspace -NewDirectory)
    New-Item -ItemType Directory -Path $workspace -ErrorAction Stop | Out-Null
    $started = [DateTime]::UtcNow.ToString('o')
    $token = ''
    try {
        $snapshotPath = Join-Path $workspace 'source-snapshot.json'
        Write-OldModNewJson $snapshotPath $frozen
        $game = Join-Path $workspace 'game'
        Copy-OldModSnapshot $source $game $selected
        Assert-ControlFrozenIdentity (Get-OldModSnapshot $game) $pinned
        Assert-OldModLaunchConfiguration $game $selected
        if ((Get-OldModPluginVersion (Join-Path $game $dllRelative)) -cne '1.3.1') { throw 'Copied Mod version does not match 1.3.1.' }
        # The legacy scenario preserves the verified original DLL. Only the
        # new-Mod scenarios replace it inside this newly created copy.
        # Never write the original game or install a separate focus cooperator.
        $controlModDestination = Join-Path $game $dllRelative
        [void](Assert-OldModFile $controlModDestination)
        $controlModEvidence = Join-Path $workspace 'mod-build-evidence.json'
        if ($LegacyClient) {
            Write-OldModNewJson $controlModEvidence ([ordered]@{
                schemaVersion = 1; kind = 'original-mod-identity'; pluginVersion = '1.3.1'
                dllSha256 = $pinned[$dllRelative]; replacementPerformed = $false; gameRuntime = 'not-run'
            })
            $controlMod = @{ sha256 = $pinned[$dllRelative]; evidenceSha256 = Get-ControlHash $controlModEvidence }
        } else {
            [IO.File]::Copy($controlMod.dll, $controlModDestination, $true)
            [IO.File]::Copy($controlMod.evidence, $controlModEvidence, $false)
        }
        if ((Get-ControlHash $controlModDestination) -cne $controlMod.sha256 -or
            (Get-ControlHash $controlModEvidence) -cne $controlMod.evidenceSha256) { throw 'Control Mod changed during copying.' }
        $appid = Join-Path $game 'steam_appid.txt'
        if (Test-Path -LiteralPath $appid) {
            Assert-OldModSteamAppIdFile $appid
        } else {
            $bytes = [Text.Encoding]::ASCII.GetBytes("1584090`n")
            $stream = [IO.File]::Open($appid, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            try { $stream.Write($bytes); $stream.Flush($true) } finally { $stream.Dispose() }
        }
        $steam.developmentFilePath = ConvertTo-ControlPath $appid
        $steam.developmentFileSha256 = Get-ControlHash $appid
        $steam.sourceManifestPath = ConvertTo-ControlPath $steam.sourceManifestPath
        $config = Join-Path $game 'BepInEx/config/com.tyukki.mystia-steward-companion.cfg'
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($config))
        $random = [byte[]]::new(32)
        [Security.Cryptography.RandomNumberGenerator]::Fill($random)
        $token = [Convert]::ToHexString($random).ToLowerInvariant()
        $autoLaunch = if ($Lifecycle -or $LegacyClient) { 'true' } else { 'false' }
        $protocol = if ($LegacyClient) { 'LegacyTcp' } else { 'IdentityPipeV1' }
        $configuration = "[Companion]`nAutoLaunch = $autoLaunch`nControlProtocol = $protocol`nExecutablePath = $windowPath`n`n[LocalApi]`nEnabled = true`nAllowLanConnections = false`nPort = $Port`nToken = $token`n`n[Updates]`nEnabled = false`nAutoCheck = false`n"
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes($configuration)
        $stream = [IO.File]::Open($config, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $stream.Write($bytes); $stream.Flush($true) } finally { $stream.Dispose() }
        $configuration = $null
        Assert-OldModSnapshotEqual $frozen (Get-OldModSnapshot $source) 'Source game'
        Assert-OldModSteamManifestUnchanged $steam
        Assert-OldModSteamAppIdFile $appid
        Assert-ControlPortFree $Port
        $verifiedFiles = @(
            foreach ($relative in $pinned.Keys) {
                $actual = Get-ControlHash (Join-Path $game $relative)
                $expected = if ($relative -ceq $dllRelative) { $controlMod.sha256 } else { $pinned[$relative] }
                if ($actual -cne $expected) { throw "Prepared game identity changed: $relative" }
                [ordered]@{ path = $relative; sha256 = $actual }
            }
        )
        if ((Get-ControlHash $windowExe) -cne $windowHash) { throw 'Window payload changed during preparation.' }
        $exe = ConvertTo-ControlPath (Join-Path $game 'Touhou Mystia Izakaya.exe')
        $evidencePath = Join-Path $workspace 'prepared-evidence.json'
        $evidence = [ordered]@{
            schemaVersion = 1; kind = 'control-prepared'; result = 'prepared'; runId = $RunId; gitSha = $GitSha
            startedUtc = $started; completedUtc = [DateTime]::UtcNow.ToString('o')
            powershellVersion = $PSVersionTable.PSVersion.ToString(); toolchainLockSha256 = Get-ControlHash $controlToolchainPath
            sourceGameDirectory = ConvertTo-ControlPath $source
            gameDirectory = ConvertTo-ControlPath $game; gameExecutable = $exe
            windowExecutable = $windowPath; windowExecutableSha256 = $windowHash
            controlPort = 32146; apiPort = $Port; controlPortFreeAtPreparation = $true
            sourceSnapshotPath = ConvertTo-ControlPath $snapshotPath; sourceSnapshotSha256 = Get-ControlHash $snapshotPath
            sourceFileCount = $frozen.files.Count; sourceUnchanged = $true; copiedFilesVerified = $true
            copyBytes = $copyBytes; reservedHeadroomBytes = $reserve; sourceModVersion = '1.3.1'; verifiedFiles = $verifiedFiles
            steamIdentity = $steam
            config = [ordered]@{
                path = ConvertTo-ControlPath $config; sha256 = Get-ControlHash $config; port = $Port
                autoLaunch = [bool]($Lifecycle -or $LegacyClient); controlProtocol = $protocol; executablePath = $windowPath; localApiEnabled = $true; allowLanConnections = $false; updatesEnabled = $false; autoCheck = $false
            }
            excludedPaths = @('BepInEx/config/com.tyukki.mystia-steward-companion.cfg', 'BepInEx/config/MystiaStewardCompanion/')
            gameStarted = $false; stagingPrepared = $false
            modReplacement = [ordered]@{
                path = $dllRelative; dllPath = ConvertTo-ControlPath $controlModDestination
                originalSha256 = $pinned[$dllRelative]; sha256 = $controlMod.sha256
                buildEvidencePath = ConvertTo-ControlPath $controlModEvidence; buildEvidenceSha256 = $controlMod.evidenceSha256
                scope = $(if ($LegacyClient) { 'original-mod-preserved' } else { 'new-game-copy-only' }); originalGameModPreserved = $true; focusCooperatorInstalled = $false
            }
        }
        Write-OldModNewJson $evidencePath $evidence
        # Only this final publication authorizes the later, separate consumer.
        Write-OldModNewJson $sidecar ([ordered]@{
            schemaVersion = 1; runId = $RunId; gitSha = $GitSha; gameExecutable = $exe
            expectedExeSha256 = $pinned['Touhou Mystia Izakaya.exe']; expectedUnityPlayerSha256 = $pinned['UnityPlayer.dll']
            expectedGameAssemblySha256 = $pinned['GameAssembly.dll']
            expectedMetadataSha256 = $pinned['Touhou Mystia Izakaya_Data/il2cpp_data/Metadata/global-metadata.dat']
            steamAppId = '1584090'; steamBuildId = '23158340'; preparedEvidenceSha256 = Get-ControlHash $evidencePath
            expectedModSha256 = $controlMod.sha256; modBuildEvidenceSha256 = $controlMod.evidenceSha256
            expectedBepInExSha256 = $pinned['BepInEx/core/BepInEx.Unity.IL2CPP.dll']
        })
        if ($Lifecycle -or $LegacyClient) {
            Write-OldModNewJson (Join-Path $run 'control-lifecycle.json') ([ordered]@{
                schemaVersion = 1; kind = 'control-lifecycle-authorization'; runId = $RunId; gitSha = $GitSha
                scenario = $(if ($LegacyClient) { 'old-mod-legacy-client' } else { 'new-mod-cold-restart' }); preparedEvidenceSha256 = Get-ControlHash $evidencePath
                tokenSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::ASCII.GetBytes($token))).ToLowerInvariant()
            })
        }
        if ($ExitDiagnostic) {
            Write-OldModNewJson (Join-Path $run 'control-exit-diagnostic.json') ([ordered]@{
                schemaVersion = 1; kind = 'control-exit-diagnostic-authorization'; runId = $RunId; gitSha = $GitSha
                diagnosticOnly = $true; modBuildEvidenceSha256 = $controlMod.evidenceSha256; preparedEvidenceSha256 = Get-ControlHash $evidencePath
            })
        }
        Write-Host "PREPARED: $sidecar"
        Write-Host 'No game started. Preserve the workspace; do not upload the generated configuration.'
    } catch {
        $failure = $_
        $message = $failure.Exception.Message
        if ($token) { $message = $message.Replace($token, '[redacted]') }
        try {
            Write-OldModNewJson (Join-Path $workspace 'prepare-failure.json') ([ordered]@{
                schemaVersion = 1; result = 'failed'; runId = $RunId; gitSha = $GitSha; error = $message
            })
        } catch { Write-Warning 'Cannot publish the preparation failure report; preserve this workspace.' }
        Write-Host "FAILED: retain $workspace; do not start this copy."
        throw $failure
    } finally { $token = '' }
}

if ($MyInvocation.InvocationName -ne '.') {
    Invoke-ControlPreparation -RunDirectory $RunDirectory -RunId $RunId -GitSha $GitSha `
        -SourceGameDirectory $SourceGameDirectory -SteamAppManifestPath $SteamAppManifestPath -Port $Port `
        -ModBundleDirectory $ModBundleDirectory -ModEvidenceSha256 $ModEvidenceSha256 -Lifecycle:$Lifecycle -LegacyClient:$LegacyClient -ExitDiagnostic:$ExitDiagnostic
}
