#requires -Version 7.0
# Preparation only. Never starts a process, requests an update, or writes the source.
param(
    [string]$RunDirectory,
    [string]$RunId,
    [string]$GitSha,
    [string]$SourceGameDirectory,
    [string]$SteamAppManifestPath,
    [string]$CooperatorBundleDirectory,
    [string]$CooperatorEvidenceSha256,
    [ValidateRange(1024, 65535)][int]$Port = 32755
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$inputFocusCommon = Join-Path $PSScriptRoot '../flutter-old-mod-probe/Prepare-OldMod-Probe.Common.psm1'
Import-Module $inputFocusCommon -Force -DisableNameChecking
$inputFocusToolchainPath = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'toolchain.lock.json'
[void](Assert-OldModFile $inputFocusToolchainPath)
$inputFocusToolchain = Get-Content -LiteralPath $inputFocusToolchainPath -Raw | ConvertFrom-Json -AsHashtable
if ($inputFocusToolchain.powershell -isnot [string] -or $PSVersionTable.PSVersion.ToString() -cne $inputFocusToolchain.powershell) {
    throw 'Preparation requires the exact PowerShell version from toolchain.lock.json.'
}

function Get-InputFocusPinnedFiles {
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

function ConvertTo-InputFocusPath([string]$Path) {
    return [IO.Path]::GetFullPath($Path).Replace('\', '/')
}

function Get-InputFocusHash([string]$Path) {
    [void](Assert-OldModFile $Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-InputFocusListeners {
    return [Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners()
}

function Read-InputFocusCooperator([string]$Directory, [string]$ExpectedEvidenceHash, [string]$ExpectedCommit) {
    if ($ExpectedEvidenceHash -cnotmatch '\A[a-f0-9]{64}\z') { throw 'Cooperator requires an externally verified evidence SHA-256.' }
    $bundle = Resolve-OldModPath $Directory
    $names = @(Get-ChildItem -LiteralPath $bundle -Force | ForEach-Object { [void](Assert-OldModFile $_.FullName); $_.Name })
    if (@(Compare-Object ($names | Sort-Object) @('build-evidence.json', 'MystiaStewardCompanion.FocusProbe.dll')).Count -ne 0) { throw 'Cooperator bundle must contain exactly its DLL and evidence.' }
    $evidencePath = Join-Path $bundle 'build-evidence.json'
    if ((Get-InputFocusHash $evidencePath) -cne $ExpectedEvidenceHash) { throw 'Cooperator evidence SHA-256 differs.' }
    if ((Get-Item $evidencePath).Length -gt 1MB) { throw 'Cooperator evidence exceeds 1 MiB.' }
    $value = Get-Content -LiteralPath $evidencePath -Raw | ConvertFrom-Json -AsHashtable
    if ($value.schemaVersion -ne 1 -or $value.kind -cne 'flutter-focus-cooperator-bundle' -or
        $value.commit -cne $ExpectedCommit -or $value.compiledGitSha -cne $ExpectedCommit -or !$value.cleanCheckout -or
        $value.target -cne 'net6.0-windows-in-game' -or $value.entrypoint -cne 'MystiaStewardCompanion.FocusProbe.dll' -or
        $value.tools.dotnetSdk -cne $inputFocusToolchain.dotnetSdk -or $value.tools.node -cne $inputFocusToolchain.node -or
        $value.checks.lockedReferences -cne 'passed' -or $value.checks.releaseBuild -cne 'passed' -or $value.checks.gameRuntime -cne 'not-run') {
        throw 'Cooperator build identity/checks differ.'
    }
    $referenceLock = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'mods/bepinex/References/references.lock.json'
    if ($value.toolchainLockSha256 -cne (Get-InputFocusHash $inputFocusToolchainPath) -or
        $value.referencesLockSha256 -cne (Get-InputFocusHash $referenceLock)) { throw 'Cooperator lock identity differs.' }
    if ($value.files.Count -ne 1 -or $value.files[0].path -cne $value.entrypoint) { throw 'Cooperator DLL manifest differs.' }
    $dll = Join-Path $bundle $value.entrypoint
    $hash = Get-InputFocusHash $dll
    if ($value.files[0].sha256 -cne $hash -or $value.files[0].size -ne (Get-Item $dll).Length) { throw 'Cooperator DLL hash/size differs.' }
    return @{ dll = $dll; sha256 = $hash; evidence = $evidencePath; evidenceSha256 = $ExpectedEvidenceHash }
}

function Assert-InputFocusPortFree([int]$Value) {
    if ($Value -lt 1024 -or $Value -gt 65535 -or $Value -in @(32145, 32146)) {
        throw 'The isolated API port must exclude production ports 32145 and 32146.'
    }
    # Read only; preparing a fixture does not reserve or bind any listener.
    $listeners = @(Get-InputFocusListeners)
    if (@($listeners | Where-Object Port -EQ $Value).Count -ne 0) {
        throw 'The requested isolated API port already has a listener.'
    }
}

function Assert-InputFocusFrozenIdentity($Snapshot, $Pinned) {
    foreach ($relative in $Pinned.Keys) {
        $matches = @($Snapshot.files | Where-Object path -IEQ $relative)
        if ($matches.Count -ne 1 -or $matches[0].path -cne $relative -or $matches[0].sha256 -cne $Pinned[$relative]) {
            throw "Pinned installed-build bytes do not match: $relative"
        }
    }
}

function Invoke-InputFocusPreparation {
    param(
        [string]$RunDirectory, [string]$RunId, [string]$GitSha,
        [string]$SourceGameDirectory, [string]$SteamAppManifestPath,
        [string]$CooperatorBundleDirectory, [string]$CooperatorEvidenceSha256,
        [int]$Port = 32755
    )
    if ($RunId -cnotmatch '\A[A-Za-z0-9][A-Za-z0-9_-]{0,79}\z' -or $GitSha -cnotmatch '\A[a-f0-9]{40}\z') {
        throw 'Preparation requires an explicit run ID and full lowercase Git SHA.'
    }
    $run = Resolve-OldModPath $RunDirectory
    if ([IO.Path]::GetFileName($run) -cne $RunId) { throw 'Run directory leaf must equal the exact run ID.' }
    $source = Resolve-OldModPath $SourceGameDirectory
    if (Test-OldModOverlap $source $run) { throw 'Source game and run directory must be disjoint.' }
    $workspace = Resolve-OldModPath (Join-Path $run 'workspace') -NewDirectory
    $sidecar = Join-Path $run 'input-probe.json'
    foreach ($name in @('input-probe.json', 'probe-result.json', 'result.json', 'status.json', 'foreground-session.json', 'game-foreground-evidence.json')) {
        if (Get-Item -LiteralPath (Join-Path $run $name) -Force -ErrorAction SilentlyContinue) {
            throw "Run already contains preparation or execution evidence: $name"
        }
    }
    Assert-InputFocusPortFree $Port
    $steam = Read-OldModSteamIdentity $SteamAppManifestPath $source
    if ($steam.buildId -cne '23158340') { throw 'Steam build must match the pinned installed build 23158340.' }
    $appidFiles = @(Get-ChildItem -LiteralPath $source -Force | Where-Object Name -IEQ 'steam_appid.txt')
    if ($appidFiles.Count -gt 1 -or ($appidFiles.Count -eq 1 -and $appidFiles[0].Name -cne 'steam_appid.txt')) {
        throw 'Source Steam development filename is ambiguous or noncanonical.'
    }
    if ($appidFiles.Count -eq 1) { Assert-OldModSteamAppIdFile $appidFiles[0].FullName }
    $pinned = Get-InputFocusPinnedFiles
    Write-Host 'Freezing the source game. Keep it closed; preparation never starts a game.'
    $frozen = Get-OldModSnapshot $source
    Assert-InputFocusFrozenIdentity $frozen $pinned
    $cooperator = Read-InputFocusCooperator $CooperatorBundleDirectory $CooperatorEvidenceSha256 $GitSha
    $cooperatorRelative = 'BepInEx/plugins/mystia-steward-companion-focus-probe/MystiaStewardCompanion.FocusProbe.dll'
    if (Test-Path -LiteralPath (Join-Path $source 'BepInEx/plugins/mystia-steward-companion-focus-probe')) { throw 'The original game already contains a focus probe; source must remain original.' }
    Assert-OldModLaunchConfiguration $source $frozen
    $dllRelative = 'BepInEx/plugins/mystia-steward-companion/MystiaStewardCompanion.BepInEx.dll'
    if ((Get-OldModPluginVersion (Join-Path $source $dllRelative)) -cne '1.3.1') { throw 'Expected the exact Mod PluginVersion constant 1.3.1.' }
    [void](Assert-OldModFile (Join-Path $source 'doorstop_config.ini'))
    $selected = Select-OldModGameSnapshot $frozen
    [long]$copyBytes = $selected.totalBytes + 8 + (Get-Item $cooperator.dll).Length + (Get-Item $cooperator.evidence).Length
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
        Assert-InputFocusFrozenIdentity (Get-OldModSnapshot $game) $pinned
        Assert-OldModLaunchConfiguration $game $selected
        if ((Get-OldModPluginVersion (Join-Path $game $dllRelative)) -cne '1.3.1') { throw 'Copied Mod version does not match 1.3.1.' }
        $cooperatorDestination = Join-Path $game $cooperatorRelative
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($cooperatorDestination))
        [IO.File]::Copy($cooperator.dll, $cooperatorDestination, $false)
        $cooperatorEvidence = Join-Path $workspace 'cooperator-build-evidence.json'
        [IO.File]::Copy($cooperator.evidence, $cooperatorEvidence, $false)
        if ((Get-InputFocusHash $cooperatorDestination) -cne $cooperator.sha256 -or
            (Get-InputFocusHash $cooperatorEvidence) -cne $cooperator.evidenceSha256) { throw 'Cooperator changed during copying.' }
        $appid = Join-Path $game 'steam_appid.txt'
        if (Test-Path -LiteralPath $appid) {
            Assert-OldModSteamAppIdFile $appid
        } else {
            $bytes = [Text.Encoding]::ASCII.GetBytes("1584090`n")
            $stream = [IO.File]::Open($appid, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            try { $stream.Write($bytes); $stream.Flush($true) } finally { $stream.Dispose() }
        }
        $steam.developmentFilePath = ConvertTo-InputFocusPath $appid
        $steam.developmentFileSha256 = Get-InputFocusHash $appid
        $steam.sourceManifestPath = ConvertTo-InputFocusPath $steam.sourceManifestPath
        $config = Join-Path $game 'BepInEx/config/com.tyukki.mystia-steward-companion.cfg'
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($config))
        $random = [byte[]]::new(32)
        [Security.Cryptography.RandomNumberGenerator]::Fill($random)
        $token = [Convert]::ToHexString($random).ToLowerInvariant()
        $configuration = "[Companion]`nAutoLaunch = false`n`n[LocalApi]`nEnabled = true`nAllowLanConnections = false`nPort = $Port`nToken = $token`n`n[Updates]`nEnabled = false`nAutoCheck = false`n"
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes($configuration)
        $stream = [IO.File]::Open($config, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $stream.Write($bytes); $stream.Flush($true) } finally { $stream.Dispose() }
        $configuration = $null
        Assert-OldModSnapshotEqual $frozen (Get-OldModSnapshot $source) 'Source game'
        Assert-OldModSteamManifestUnchanged $steam
        Assert-OldModSteamAppIdFile $appid
        Assert-InputFocusPortFree $Port
        $verifiedFiles = @(
            foreach ($relative in $pinned.Keys) {
                $actual = Get-InputFocusHash (Join-Path $game $relative)
                if ($actual -cne $pinned[$relative]) { throw "Prepared game identity changed: $relative" }
                [ordered]@{ path = $relative; sha256 = $actual }
            }
        )
        $exe = ConvertTo-InputFocusPath (Join-Path $game 'Touhou Mystia Izakaya.exe')
        $evidencePath = Join-Path $workspace 'prepared-evidence.json'
        $evidence = [ordered]@{
            schemaVersion = 1; kind = 'input-focus-prepared'; result = 'prepared'; runId = $RunId; gitSha = $GitSha
            startedUtc = $started; completedUtc = [DateTime]::UtcNow.ToString('o')
            powershellVersion = $PSVersionTable.PSVersion.ToString(); toolchainLockSha256 = Get-InputFocusHash $inputFocusToolchainPath
            sourceGameDirectory = ConvertTo-InputFocusPath $source
            gameDirectory = ConvertTo-InputFocusPath $game; gameExecutable = $exe
            sourceSnapshotPath = ConvertTo-InputFocusPath $snapshotPath; sourceSnapshotSha256 = Get-InputFocusHash $snapshotPath
            sourceFileCount = $frozen.files.Count; sourceUnchanged = $true; copiedFilesVerified = $true
            copyBytes = $copyBytes; reservedHeadroomBytes = $reserve; modVersion = '1.3.1'; verifiedFiles = $verifiedFiles
            steamIdentity = $steam
            config = [ordered]@{
                path = ConvertTo-InputFocusPath $config; sha256 = Get-InputFocusHash $config; port = $Port
                autoLaunch = $false; localApiEnabled = $true; allowLanConnections = $false; updatesEnabled = $false; autoCheck = $false
            }
            excludedPaths = @('BepInEx/config/com.tyukki.mystia-steward-companion.cfg', 'BepInEx/config/MystiaStewardCompanion/')
            gameStarted = $false; stagingPrepared = $false
            cooperation = [ordered]@{ mode = 'test-plugin'; dllPath = ConvertTo-InputFocusPath $cooperatorDestination; dllSha256 = $cooperator.sha256; buildEvidenceSha256 = $cooperator.evidenceSha256; originalModPreserved = $true }
        }
        Write-OldModNewJson $evidencePath $evidence
        # Only this final publication authorizes the later, separate consumer.
        Write-OldModNewJson $sidecar ([ordered]@{
            schemaVersion = 2; runId = $RunId; gitSha = $GitSha; gameExecutable = $exe
            expectedExeSha256 = $pinned['Touhou Mystia Izakaya.exe']; expectedUnityPlayerSha256 = $pinned['UnityPlayer.dll']
            expectedGameAssemblySha256 = $pinned['GameAssembly.dll']
            expectedMetadataSha256 = $pinned['Touhou Mystia Izakaya_Data/il2cpp_data/Metadata/global-metadata.dat']
            steamAppId = '1584090'; steamBuildId = '23158340'; preparedEvidenceSha256 = Get-InputFocusHash $evidencePath
            expectedCooperatorSha256 = $cooperator.sha256; cooperatorBuildEvidenceSha256 = $cooperator.evidenceSha256
        })
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
    Invoke-InputFocusPreparation -RunDirectory $RunDirectory -RunId $RunId -GitSha $GitSha `
        -SourceGameDirectory $SourceGameDirectory -SteamAppManifestPath $SteamAppManifestPath -Port $Port `
        -CooperatorBundleDirectory $CooperatorBundleDirectory -CooperatorEvidenceSha256 $CooperatorEvidenceSha256
}
