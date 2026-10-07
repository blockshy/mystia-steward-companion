#requires -Version 7.0
param(
    [string]$Workspace,
    [ValidateRange(1, 600)][int]$ReadyTimeoutSeconds = 180,
    [ValidateRange(1, 600)][int]$CancelTimeoutSeconds = 360,
    [string]$NodeRunId = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-OldModPlatform {
    if (!$IsWindows) { throw 'This collector requires Windows and PowerShell 7.' }
}

function Get-OldModExecutionContext {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $process = [Diagnostics.Process]::GetCurrentProcess()
    try {
        return @{
            elevated = [Security.Principal.WindowsPrincipal]::new($identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
            sessionId = $process.SessionId
        }
    } finally { $process.Dispose(); $identity.Dispose() }
}

function Get-OldModFullPath([string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value) -or ![IO.Path]::IsPathFullyQualified($Value) -or $Value -match '[\x00-\x1f]') {
        throw 'Expected an explicit absolute path without control characters.'
    }
    return [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Value))
}

function Assert-OldModPlainPath([string]$Value, [switch]$Directory, [switch]$MayBeAbsent) {
    $full = Get-OldModFullPath $Value
    $current = $full
    $first = $true
    while ($current) {
        $item = $null
        try { $item = Get-Item -LiteralPath $current -Force -ErrorAction Stop }
        catch [Management.Automation.ItemNotFoundException] { }
        if ($null -ne $item) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'A probe path contains a symbolic link or reparse point.' }
            if ((!$first -or $Directory) -and !$item.PSIsContainer) { throw 'A required probe directory is not a directory.' }
            if ($first -and !$Directory -and $item.PSIsContainer) { throw 'A required probe file is a directory.' }
        } elseif (!$first -or !$MayBeAbsent) { throw 'A required probe path does not exist.' }
        $first = $false
        $parent = [IO.Path]::GetDirectoryName($current)
        if ($parent -eq $current) { break }
        $current = $parent
    }
    return $full
}

function Assert-OldModOwnedPath([string]$Root, [string]$Value, [switch]$Directory, [switch]$MayBeAbsent) {
    $full = Get-OldModFullPath $Value
    if (!$full.StartsWith($Root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'A probe path escapes its workspace.'
    }
    return Assert-OldModPlainPath $full -Directory:$Directory -MayBeAbsent:$MayBeAbsent
}

function ConvertFrom-OldModJson([string]$Json) {
    $document = [Text.Json.JsonDocument]::Parse($Json)
    try {
        function Assert-UniqueJsonKeys($Element) {
            if ($Element.ValueKind -eq [Text.Json.JsonValueKind]::Object) {
                $keys = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
                foreach ($property in $Element.EnumerateObject()) {
                    if (!$keys.Add($property.Name)) { throw 'Probe JSON contains a duplicate or case-ambiguous key.' }
                    Assert-UniqueJsonKeys $property.Value
                }
            } elseif ($Element.ValueKind -eq [Text.Json.JsonValueKind]::Array) {
                foreach ($element in $Element.EnumerateArray()) { Assert-UniqueJsonKeys $element }
            }
        }
        if ($document.RootElement.ValueKind -ne [Text.Json.JsonValueKind]::Object) { throw 'Expected a probe JSON object.' }
        Assert-UniqueJsonKeys $document.RootElement
    } finally { $document.Dispose() }
    return ConvertFrom-Json -InputObject $Json -AsHashtable -Depth 32
}

function Read-OldModJson([string]$Root, [string]$File) {
    $null = Assert-OldModOwnedPath $Root $File
    # The old Mod atomically replaces update-state.json. Keep one bounded read
    # handle, permitting that replacement instead of holding a delete-denying lock.
    $stream = [IO.File]::Open($File, [IO.FileMode]::Open, [IO.FileAccess]::Read,
        ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    try {
        if ($stream.Length -gt 1048576) { throw 'Probe JSON exceeds 1 MiB.' }
        $bytes = [byte[]]::new([int]$stream.Length)
        $offset = 0
        while ($offset -lt $bytes.Length) {
            $read = $stream.Read($bytes, $offset, $bytes.Length - $offset)
            if ($read -eq 0) { throw 'Probe JSON changed or was truncated while reading.' }
            $offset += $read
        }
        if ($stream.ReadByte() -ne -1) { throw 'Probe JSON grew while reading.' }
    } finally { $stream.Dispose() }
    return ConvertFrom-OldModJson ([Text.UTF8Encoding]::new($false, $true).GetString($bytes))
}

function Write-OldModEvidence([string]$Root, [string]$File, $Value, [string]$Secret = '') {
    $null = Assert-OldModOwnedPath $Root $File -MayBeAbsent
    $json = $Value | ConvertTo-Json -Depth 16
    if ($Secret) { $json = $json.Replace($Secret, '[redacted]') }
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($json)
    $stream = [IO.File]::Open($File, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
}

function Get-OldModSha256([string]$File) {
    return (Get-FileHash -LiteralPath $File -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Assert-OldModBootstrap([string]$Root, [string]$File) {
    $null = Assert-OldModOwnedPath $Root $File
    # This is the accepted read-only 283bd56 probe, never a manifest-selected updater.
    if ((Get-OldModSha256 $File) -cne '18c3a381e6f363150209605f65da553680e1e13b250a50eb90c6298d7438357b') {
        throw 'Updater bytes do not match the fixed, accepted read-only bootstrap.'
    }
}

function Get-OldModPluginSnapshot([string]$Root, [string]$Plugin) {
    $null = Assert-OldModOwnedPath $Root $Plugin -Directory
    $snapshot = [Collections.Generic.List[object]]::new()
    foreach ($item in Get-ChildItem -LiteralPath $Plugin -Recurse -Force) {
        $null = Assert-OldModOwnedPath $Root $item.FullName -Directory:$item.PSIsContainer
        $relative = [IO.Path]::GetRelativePath($Plugin, $item.FullName).Replace('\', '/')
        $snapshot.Add([ordered]@{
            path = $relative; directory = [bool]$item.PSIsContainer
            size = if ($item.PSIsContainer) { 0L } else { $item.Length }
            sha256 = if ($item.PSIsContainer) { '' } else { Get-OldModSha256 $item.FullName }
        })
    }
    return @($snapshot.ToArray() | Sort-Object { $_.path })
}

function Assert-OldModSteamDevelopmentFile([string]$Root, $Manifest) {
    if ($null -eq $Manifest.steamIdentity) {
        $file = Assert-OldModOwnedPath $Root (Join-Path $Manifest.gameDirectory 'steam_appid.txt') -MayBeAbsent
        if (Test-Path -LiteralPath $file) { throw 'An unrecorded Steam development identity file was added to the copied game.' }
        return
    }
    $identity = $Manifest.steamIdentity
    $file = Assert-OldModOwnedPath $Root $identity.developmentFilePath
    if ($file -ine (Join-Path $Manifest.gameDirectory 'steam_appid.txt')) { throw 'Steam development identity must belong to the copied game.' }
    if ((Get-Item -LiteralPath $file -Force).Length -gt 9) { throw 'Steam development identity file has unexpected bytes.' }
    $bytes = [IO.File]::ReadAllBytes($file)
    # Preserve the three source forms supported by Prepare, without whitespace,
    # BOM or encoding normalization that could conceal a changed identity.
    if ([Convert]::ToHexString($bytes) -cnotin @('31353834303930', '313538343039300A', '313538343039300D0A') -or
        [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant() -cne $identity.developmentFileSha256) {
        throw 'Steam development identity bytes differ from the prepared AppID 1584090.'
    }
}

function Assert-OldModSteamIdentity([string]$Root, $Manifest, $Prepared) {
    if (!$Manifest.ContainsKey('steamIdentity') -or !$Prepared.ContainsKey('steamIdentity')) {
        throw 'Workspace and preparation report must explicitly record the optional Steam identity.'
    }
    $identity = $Manifest.steamIdentity
    $recorded = $Prepared.steamIdentity
    if ($null -eq $identity) {
        if ($null -ne $recorded) { throw 'Steam identity differs from the preparation report.' }
        Assert-OldModSteamDevelopmentFile $Root $Manifest
        return
    }
    $keys = @('appId', 'buildId', 'installDirectory', 'sourceManifestPath', 'sourceManifestSha256', 'developmentFilePath', 'developmentFileSha256')
    foreach ($entry in @($identity, $recorded)) {
        if ($entry -isnot [Collections.IDictionary] -or $entry.Count -ne $keys.Count -or
            @($entry.Keys | Where-Object { $_ -cnotin $keys }).Count -ne 0) { throw 'Unsupported prepared Steam identity schema.' }
        foreach ($key in $keys) {
            if ($entry[$key] -isnot [string] -or $entry[$key] -cne $identity[$key]) { throw 'Steam identity differs from the preparation report.' }
        }
    }
    if ($identity.appId -cne '1584090' -or $identity.buildId -cnotmatch '\A[1-9][0-9]{0,19}\z' -or
        $identity.installDirectory -cne 'Touhou Mystia Izakaya' -or
        $identity.sourceManifestSha256 -cnotmatch '\A[a-f0-9]{64}\z' -or
        $identity.developmentFileSha256 -cnotmatch '\A[a-f0-9]{64}\z') { throw 'Prepared Steam identity is not the expected installed game.' }
    $sourceManifest = Get-OldModFullPath $identity.sourceManifestPath
    if ([IO.Path]::GetFileName($sourceManifest) -cne 'appmanifest_1584090.acf' -or
        [IO.Path]::GetFileName([IO.Path]::GetDirectoryName($sourceManifest)) -ine 'steamapps' -or
        (Get-OldModFullPath (Join-Path ([IO.Path]::GetDirectoryName($sourceManifest)) ('common/' + $identity.installDirectory))) -ine
        (Get-OldModFullPath $Manifest.sourceGameDirectory)) { throw 'Prepared Steam manifest is not bound to the source game directory.' }
    Assert-OldModSteamDevelopmentFile $Root $Manifest
}

function Read-OldModWorkspace([string]$Root) {
    if (Test-Path -LiteralPath (Join-Path $Root 'evidence/prepare-failure.json')) { throw 'Workspace preparation failed; this copy must not run.' }
    $prepared = Read-OldModJson $Root (Join-Path $Root 'evidence/prepare-report.json')
    if ($prepared.result -cne 'prepared' -or $prepared.sourceUnchanged -cne $true -or $prepared.copiedPluginUnchanged -cne $true) {
        throw 'Workspace does not have a successful preparation report.'
    }
    $manifest = Read-OldModJson $Root (Join-Path $Root 'probe-workspace.json')
    if ($manifest.schemaVersion -ne 1 -or $manifest.kind -cne 'old-mod-launch-fixture' -or
        $manifest.expectedModVersion -cne '1.3.1' -or $manifest.testDownloadedVersion -cne '1.3.2-preview.1' -or
        $manifest.bootstrapSha256 -cne '18c3a381e6f363150209605f65da553680e1e13b250a50eb90c6298d7438357b' -or
        $manifest.bootstrapBuildCommit -cne '283bd56cd10564d64169a8ea521f9fdffe0019b4') {
        throw 'Unsupported workspace contract, old Mod version or accepted bootstrap identity.'
    }
    if ($manifest.port -isnot [long] -and $manifest.port -isnot [int]) { throw 'Workspace port must be an integer.' }
    if ($manifest.port -lt 1024 -or $manifest.port -gt 65535 -or $manifest.port -eq 32146) { throw 'Invalid dedicated loopback API port.' }
    $layout = [ordered]@{
        gameDirectory = 'game'; gameExecutable = 'game/Touhou Mystia Izakaya.exe'
        pluginDirectory = 'game/BepInEx/plugins/mystia-steward-companion'
        stagedDirectory = 'staging/mystia-steward-companion'
        updatesDirectory = 'game/BepInEx/config/MystiaStewardCompanion/updates'
        configPath = 'game/BepInEx/config/com.tyukki.mystia-steward-companion.cfg'
        evidenceDirectory = 'evidence'
    }
    foreach ($field in $layout.Keys) {
        $expected = Get-OldModFullPath (Join-Path $Root $layout[$field])
        if ((Get-OldModFullPath $manifest[$field]) -ine $expected) { throw "Workspace $field does not match the isolated layout." }
        $null = Assert-OldModOwnedPath $Root $expected -Directory:($field -notin @('gameExecutable', 'configPath'))
    }
    $source = Get-OldModFullPath $manifest.sourceGameDirectory
    if ($source -ieq $Root -or $source.StartsWith($Root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        $Root.StartsWith($source + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Source game and isolated workspace must be disjoint.'
    }
    Assert-OldModSteamIdentity $Root $manifest $prepared
    if ($manifest.gameExecutableSha256 -cnotmatch '\A[a-f0-9]{64}\z' -or
        (Get-OldModSha256 $manifest.gameExecutable) -cne $manifest.gameExecutableSha256) { throw 'Copied game executable changed.' }
    $snapshot = @(Get-OldModPluginSnapshot $Root $manifest.pluginDirectory)
    $actualFiles = @($snapshot | Where-Object { !$_.directory })
    $expectedFiles = @($manifest.pluginFiles)
    if ($expectedFiles.Count -eq 0 -or $expectedFiles.Count -ne $actualFiles.Count) { throw 'Frozen plugin file set does not match.' }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $expectedFiles) {
        if ($file.path -isnot [string] -or $file.path -match '\\|\A/|:|[\x00-\x1f]' -or
            @($file.path.Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count -ne 0 -or !$seen.Add($file.path)) {
            throw 'Frozen plugin file list contains an unsafe or duplicate relative path.'
        }
        $actual = @($actualFiles | Where-Object { $_.path -ceq $file.path })
        if ($actual.Count -ne 1 -or $file.sha256 -cnotmatch '\A[a-f0-9]{64}\z' -or
            $file.size -ne $actual[0].size -or $file.sha256 -cne $actual[0].sha256) { throw 'A frozen plugin file changed.' }
    }
    $dll = @($actualFiles | Where-Object { $_.path -ceq 'MystiaStewardCompanion.BepInEx.dll' })
    if ($dll.Count -ne 1 -or $dll[0].sha256 -cne $manifest.originalPluginDllSha256) { throw 'Frozen old Mod DLL identity does not match.' }
    $snapshotFile = Assert-OldModOwnedPath $Root (Join-Path $Root 'evidence/source-snapshot.json')
    if ($manifest.sourceSnapshotSha256 -cnotmatch '\A[a-f0-9]{64}\z' -or (Get-OldModSha256 $snapshotFile) -cne $manifest.sourceSnapshotSha256) {
        throw 'Preparation source snapshot identity does not match.'
    }
    Assert-OldModBootstrap $Root (Join-Path $manifest.stagedDirectory 'mystia-steward-companion-updater.exe')
    return @{ manifest = $manifest; snapshot = $snapshot }
}

function Read-OldModToken([string]$Root, $Manifest) {
    $null = Assert-OldModOwnedPath $Root $Manifest.configPath
    if ((Get-Item -LiteralPath $Manifest.configPath).Length -gt 1048576) { throw 'Configuration exceeds 1 MiB.' }
    $config = [Text.UTF8Encoding]::new($false, $true).GetString([IO.File]::ReadAllBytes($Manifest.configPath))
    $values = @{}
    $section = ''
    foreach ($line in $config -split '\r?\n') {
        if ($line -match '^\s*\[([^\]]+)\]\s*$') { $section = $Matches[1]; continue }
        if ($line -match '^\s*(?:#|;|$)') { continue }
        if ($line -match '^\s*([^=]+?)\s*=\s*(.*?)\s*$') {
            $key = $section + '.' + $Matches[1].Trim()
            if ($values.ContainsKey($key)) { throw 'Configuration contains duplicate entries.' }
            $values[$key] = $Matches[2]
        }
    }
    if ($values['LocalApi.Enabled'] -ine 'true' -or $values['LocalApi.AllowLanConnections'] -ine 'false' -or
        $values['LocalApi.Port'] -cne [string]$Manifest.port -or $values['Companion.AutoLaunch'] -ine 'false' -or
        $values['Updates.Enabled'] -ine 'true' -or $values['Updates.AutoCheck'] -ine 'false') {
        throw 'Isolated configuration no longer has the required API, companion and update settings.'
    }
    if ($values['LocalApi.Token'] -cnotmatch '\A[a-f0-9]{64}\z') { throw 'Isolated API token is missing or invalid.' }
    return [string]$values['LocalApi.Token']
}

function Start-OldModGame([string]$Executable, [string]$Directory) {
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.WorkingDirectory = $Directory
    $start.UseShellExecute = $false
    return [Diagnostics.Process]::Start($start)
}
function Get-OldModProcess([int]$ProcessId) { return [Diagnostics.Process]::GetProcessById($ProcessId) }
function Get-OldModCimProcess([int]$ProcessId) { return @(Get-CimInstance Win32_Process -Filter "ProcessId=$ProcessId" -ErrorAction Stop) }
function Get-OldModListeners([int]$Port) { return @(Get-NetTCPConnection -State Listen -ErrorAction Stop | Where-Object { $_.LocalPort -eq $Port }) }

function Assert-OldModProcess($Process, [string]$Executable) {
    # Force and retain a process handle before consulting PID-based metadata.
    if ($null -eq $Process -or $Process.Handle -eq [IntPtr]::Zero) { throw 'Cannot retain the exact process handle.' }
    $Process.Refresh()
    if ($Process.HasExited) { throw 'The exact launched process exited; no replacement process will be followed.' }
    if ((Get-OldModFullPath $Process.MainModule.FileName) -ine (Get-OldModFullPath $Executable)) {
        throw 'The exact process executable path does not match the isolated copy.'
    }
}

function Test-OldModListener($Game, $Manifest) {
    Assert-OldModProcess $Game $Manifest.gameExecutable
    $listeners = @(Get-OldModListeners $Manifest.port)
    if ($listeners.Count -eq 0) { return $false }
    if ($listeners.Count -ne 1 -or $listeners[0].LocalAddress -cne '127.0.0.1' -or $listeners[0].OwningProcess -ne $Game.Id) {
        throw 'The dedicated API port is not uniquely owned by the exact copied game on 127.0.0.1.'
    }
    Assert-OldModProcess $Game $Manifest.gameExecutable
    return $true
}

function New-OldModHttpClient {
    $handler = [Net.Http.SocketsHttpHandler]::new()
    $handler.UseProxy = $false
    $handler.AllowAutoRedirect = $false
    $handler.UseCookies = $false
    $handler.ConnectTimeout = [TimeSpan]::FromSeconds(3)
    $client = [Net.Http.HttpClient]::new($handler, $true)
    $client.Timeout = [TimeSpan]::FromSeconds(5)
    $client.MaxResponseContentBufferSize = 1048576
    return $client
}

function Send-OldModHttp($Client, [int]$Port, [string]$Route, [string]$Token) {
    if ($Route -notin @('/health', '/updates/status', '/updates/install-on-exit')) { throw 'Unsupported probe API route.' }
    $method = if ($Route -ceq '/health') { [Net.Http.HttpMethod]::Get } else { [Net.Http.HttpMethod]::Post }
    $request = [Net.Http.HttpRequestMessage]::new($method, "http://127.0.0.1:$Port$Route")
    try {
        $request.Version = [Version]::new(1, 1)
        $request.VersionPolicy = [Net.Http.HttpVersionPolicy]::RequestVersionExact
        $request.Headers.ConnectionClose = $true
        if ($Route -cne '/health') {
            $request.Headers.Add('X-Mystia-Steward-Companion-Token', $Token)
            $request.Content = [Net.Http.ByteArrayContent]::new([byte[]]::new(0))
        }
        $response = $Client.SendAsync($request).GetAwaiter().GetResult()
        try {
            if ([int]$response.StatusCode -ne 200) { throw "Probe API returned HTTP $([int]$response.StatusCode)." }
            return ConvertFrom-OldModJson ($response.Content.ReadAsStringAsync().GetAwaiter().GetResult())
        } finally { $response.Dispose() }
    } finally { $request.Dispose() }
}

function Assert-OldModStatus($Status, [string]$InstallState) {
    if ($Status.ok -isnot [bool] -or $Status.enabled -isnot [bool] -or $Status.autoCheck -isnot [bool] -or $Status.staged -isnot [bool] -or
        $Status.ok -cne $true -or $Status.currentVersion -cne '1.3.1' -or $Status.enabled -cne $true -or
        $Status.autoCheck -cne $false -or $Status.downloadedVersion -cne '1.3.2-preview.1' -or $Status.staged -cne $true -or
        ![string]::IsNullOrEmpty($Status.error) -or $Status.installState -cne $InstallState) {
        throw 'Old Mod status does not match the expected version, frozen staging or install phase.'
    }
}

function ConvertFrom-OldModCommandLine([string]$CommandLine) {
    if (!('MystiaOldModProbe.CommandLine' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace MystiaOldModProbe {
  public static class CommandLine {
    [DllImport("shell32.dll", SetLastError=true, CharSet=CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr value);
    public static string[] Parse(string commandLine) {
      int count; IntPtr pointer = CommandLineToArgvW(commandLine, out count);
      if (pointer == IntPtr.Zero) throw new InvalidOperationException("Cannot decode updater command line.");
      try { var args = new string[count]; for (int i=0; i<count; i++) args[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, i*IntPtr.Size)); return args; }
      finally { LocalFree(pointer); }
    }
  }
}
'@
    }
    return [MystiaOldModProbe.CommandLine]::Parse($CommandLine)
}

function Assert-OldModUpdater($Root, $Manifest, $Game, $Updater, [string]$Token) {
    $items = @(Get-OldModCimProcess $Updater.Id)
    if ($items.Count -ne 1 -or $items[0].ParentProcessId -ne $Game.Id -or [string]::IsNullOrWhiteSpace($items[0].CommandLine)) {
        throw 'Updater process parent or command-line identity is unavailable or incorrect.'
    }
    $executable = Get-OldModFullPath $items[0].ExecutablePath
    $runner = Join-Path $Manifest.updatesDirectory 'runner'
    $null = Assert-OldModOwnedPath $Root $runner -Directory
    $relative = [IO.Path]::GetRelativePath($runner, $executable).Replace('\', '/')
    if ($relative -cnotmatch '\A[0-9]{14}/mystia-steward-companion-updater\.exe\z' -or
        @(Get-ChildItem -LiteralPath $runner -Force).Count -ne 1) { throw 'Updater is not the uniquely created old Mod runner.' }
    Assert-OldModProcess $Updater $executable
    Assert-OldModBootstrap $Root $executable
    if ($items[0].CommandLine.Contains($Token, [StringComparison]::Ordinal)) { throw 'Updater command line unexpectedly contains API credentials.' }
    $arguments = @(ConvertFrom-OldModCommandLine $items[0].CommandLine)
    if ($arguments.Count -ne 13 -or (Get-OldModFullPath $arguments[0]) -ine $executable) { throw 'Unexpected old Mod updater argument count or executable.' }
    $values = @{}
    for ($index = 1; $index -lt $arguments.Count; $index += 2) {
        if ($values.ContainsKey($arguments[$index])) { throw 'Duplicate updater argument.' }
        $values[$arguments[$index]] = $arguments[$index + 1]
    }
    $expected = @{
        '--game-pid' = [string]$Game.Id; '--plugin-dir' = $Manifest.pluginDirectory
        '--staged-dir' = $Manifest.stagedDirectory; '--status-file' = (Join-Path $Manifest.updatesDirectory 'install-status.json')
        '--control-port' = '32146'
    }
    foreach ($key in $expected.Keys) {
        if (!$values.ContainsKey($key)) { throw 'Updater launch arguments do not bind to this isolated workspace.' }
        if ($key -in @('--game-pid', '--control-port')) {
            if ($values[$key] -cne $expected[$key]) { throw 'Updater numeric launch arguments do not match.' }
        } elseif ((Get-OldModFullPath $values[$key]) -ine (Get-OldModFullPath $expected[$key])) {
            throw 'Updater path arguments do not bind to this isolated workspace.'
        }
    }
    if (!$values.ContainsKey('--backup-dir')) { throw 'Updater backup argument is missing.' }
    $backup = Assert-OldModOwnedPath $Root $values['--backup-dir'] -Directory -MayBeAbsent
    if ([IO.Path]::GetDirectoryName($backup) -ine (Join-Path $Manifest.updatesDirectory 'backups') -or
        [IO.Path]::GetFileName($backup) -cnotmatch '\Amystia-steward-companion-1\.3\.1-[0-9]{14}\z' -or
        (Test-Path -LiteralPath $backup)) { throw 'Updater backup leaf is unexpected or already exists.' }
    Assert-OldModProcess $Updater $executable
    Assert-OldModProcess $Game $Manifest.gameExecutable
    return @{ executable = $executable; arguments = $arguments; backup = $backup; parentProcessId = $items[0].ParentProcessId }
}

function Invoke-OldModProbeMain([string]$WorkspacePath, [int]$ReadySeconds = 180, [int]$CancelSeconds = 360, [string]$NodeRunId = '') {
    Assert-OldModPlatform
    if ($NodeRunId -cne '' -and $NodeRunId -cnotmatch '\A[A-Za-z0-9][A-Za-z0-9_-]{0,79}\z') { throw 'NodeRunId must contain 1 to 80 portable identifier characters or be empty.' }
    if (![string]::IsNullOrEmpty([Environment]::GetEnvironmentVariable('MYSTIA_UPDATER_PROBE_AUTOMATION'))) {
        throw 'Clear MYSTIA_UPDATER_PROBE_AUTOMATION; this old Mod probe requires a manual cancellation.'
    }
    $root = Assert-OldModPlainPath $WorkspacePath -Directory
    $evidence = Assert-OldModOwnedPath $root (Join-Path $root 'evidence') -Directory
    $reportPath = Join-Path $evidence 'old-mod-probe-report.json'
    $markerPath = Join-Path $evidence 'old-mod-probe-started.json'
    $waitingPath = Join-Path $evidence 'waiting-observed.json'
    if ((Test-Path -LiteralPath $markerPath) -or (Test-Path -LiteralPath $reportPath) -or (Test-Path -LiteralPath $waitingPath)) {
        throw 'This workspace already has probe evidence. Preserve it and prepare a new isolated workspace.'
    }
    $report = [ordered]@{
        schemaVersion = 1; kind = 'old-mod-real-launch-collector'; result = 'failed'
        startedUtc = [DateTime]::UtcNow.ToString('o'); powershell = $PSVersionTable.PSVersion.ToString()
        nodeRunId = $NodeRunId; executionMode = if ($NodeRunId) { 'controlled-desktop-automation' } else { 'manual' }
        bootstrapSha256 = '18c3a381e6f363150209605f65da553680e1e13b250a50eb90c6298d7438357b'
        bootstrapBuildCommit = '283bd56cd10564d64169a8ea521f9fdffe0019b4'
        installRequestCount = 0; gameProcessId = $null; gameHasExited = $null; gameExitCode = $null; updaterProcessId = $null
        bootstrapExitCode = $null; pluginUnchanged = $false; gameStillRunning = $false; backupAbsent = $false
        errors = @()
    }
    $execution = Get-OldModExecutionContext
    $report.elevated = $execution.elevated
    $report.windowsSessionId = $execution.sessionId
    $context = $null; $game = $null; $updater = $null; $client = $null; $token = ''; $binding = $null
    try {
        $context = Read-OldModWorkspace $root
        $manifest = $context.manifest
        $report.originalPluginDllSha256 = $manifest.originalPluginDllSha256
        $report.gameExecutableSha256 = $manifest.gameExecutableSha256
        $report.steamIdentity = $manifest.steamIdentity
        $token = Read-OldModToken $root $manifest
        $statePath = Join-Path $manifest.updatesDirectory 'update-state.json'
        $statusPath = Join-Path $manifest.updatesDirectory 'install-status.json'
        $state = Read-OldModJson $root $statePath
        if ($state.state -cne 'downloaded' -or $state.downloadedVersion -cne '1.3.2-preview.1' -or
            $state.stagedDirectory -cne $manifest.stagedDirectory -or
            ($state.ContainsKey('installProcessId') -and $state.installProcessId -ne 0) -or
            ($state.ContainsKey('installState') -and ![string]::IsNullOrEmpty($state.installState)) -or
            (Test-Path -LiteralPath $statusPath)) { throw 'Initial staged update state is not fresh.' }
        foreach ($leaf in @('runner', 'backups')) {
            if (Test-Path -LiteralPath (Join-Path $manifest.updatesDirectory $leaf)) { throw 'Initial update workspace already has runner or backup state.' }
        }
        if (@(Get-OldModListeners $manifest.port).Count -ne 0) { throw 'The dedicated API port is already in use before launch.' }
        $marker = [IO.File]::Open($markerPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
        try {
            $bytes = [Text.Encoding]::UTF8.GetBytes('{"kind":"single-attempt-old-mod-probe"}')
            $marker.Write($bytes, 0, $bytes.Length)
        } finally { $marker.Dispose() }
        Assert-OldModSteamDevelopmentFile $root $manifest
        $game = Start-OldModGame $manifest.gameExecutable $manifest.gameDirectory
        $null = $game.Handle
        $report.gameProcessId = $game.Id
        $report.gameExecutable = $manifest.gameExecutable
        Assert-OldModProcess $game $manifest.gameExecutable
        $report.port = $manifest.port
        $client = New-OldModHttpClient
        $deadline = [DateTime]::UtcNow.AddSeconds($ReadySeconds)
        $ready = $false
        Write-Host 'Keep the isolated game at its title screen. Do not load or save a game.'
        while ([DateTime]::UtcNow -lt $deadline) {
            try {
                if (Test-OldModListener $game $manifest) {
                    $health = Send-OldModHttp $client $manifest.port '/health' ''
                    if ($health.ok -isnot [bool] -or $health.authRequired -isnot [bool] -or $health.lanEnabled -isnot [bool] -or
                        $health.ok -cne $true -or $health.pluginVersion -cne '1.3.1' -or $health.port -ne $manifest.port -or
                        $health.bindAddress -cne '127.0.0.1' -or $health.authRequired -cne $true -or $health.lanEnabled -cne $false) {
                        throw 'The exact game API health contract or Mod version is incorrect.'
                    }
                    $token = Read-OldModToken $root $manifest
                    if (!(Test-OldModListener $game $manifest)) { throw 'The owned API listener disappeared.' }
                    $status = Send-OldModHttp $client $manifest.port '/updates/status' $token
                    Assert-OldModStatus $status ''
                    $ready = $true
                    break
                }
            } catch [Net.Http.HttpRequestException] {
                Assert-OldModProcess $game $manifest.gameExecutable
            } catch [Threading.Tasks.TaskCanceledException] {
                Assert-OldModProcess $game $manifest.gameExecutable
            }
            Start-Sleep -Milliseconds 250
        }
        if (!$ready) { throw 'Timed out waiting for the exact copied game API; no install request was sent.' }
        Assert-OldModBootstrap $root (Join-Path $manifest.stagedDirectory 'mystia-steward-companion-updater.exe')
        $beforeInstall = @(Get-OldModPluginSnapshot $root $manifest.pluginDirectory)
        if (($context.snapshot | ConvertTo-Json -Depth 8 -Compress) -cne ($beforeInstall | ConvertTo-Json -Depth 8 -Compress)) {
            throw 'Frozen plugin changed before installation was requested.'
        }
        if (!(Test-OldModListener $game $manifest)) { throw 'The owned API listener disappeared before install.' }
        $report.installRequestCount = 1
        # Exactly one attempt. A timeout is an unknown write result, never a retry.
        $scheduled = Send-OldModHttp $client $manifest.port '/updates/install-on-exit' $token
        Assert-OldModStatus $scheduled 'waiting'
        $state = Read-OldModJson $root $statePath
        if ($state.installProcessId -isnot [long] -and $state.installProcessId -isnot [int]) { throw 'Updater PID is not an integer.' }
        if ($state.installProcessId -le 0 -or $state.installProcessId -gt [int]::MaxValue -or $state.installState -cne 'waiting') { throw 'Updater PID or persisted waiting state is invalid.' }
        $updater = Get-OldModProcess ([int]$state.installProcessId)
        $null = $updater.Handle
        $binding = Assert-OldModUpdater $root $manifest $game $updater $token
        $report.updaterProcessId = $updater.Id
        $report.updaterParentProcessId = $binding.parentProcessId
        $report.updaterExecutable = $binding.executable
        $report.updaterArguments = $binding.arguments
        if (!(Test-OldModListener $game $manifest)) { throw 'The owned API listener disappeared during waiting verification.' }
        Assert-OldModStatus (Send-OldModHttp $client $manifest.port '/updates/status' $token) 'waiting'
        Assert-OldModProcess $updater $binding.executable
        $report.waitingVerified = $true
        Write-OldModEvidence $root $waitingPath ([ordered]@{
            schemaVersion = 1; kind = 'old-mod-waiting-observed'; gamePid = $game.Id; bootstrapPid = $updater.Id
            bootstrapExecutable = $binding.executable; bootstrapSha256 = $report.bootstrapSha256
            workspace = $root; observationUtc = [DateTime]::UtcNow.ToString('o'); nodeRunId = $NodeRunId
        })
        Write-Host 'The old Mod launched the verified Flutter probe. Now click End probe in its window; keep the game running.'
        $deadline = [DateTime]::UtcNow.AddSeconds($CancelSeconds)
        $exited = $false
        while ([DateTime]::UtcNow -lt $deadline) {
            Assert-OldModProcess $game $manifest.gameExecutable
            if ($updater.WaitForExit(250)) { $exited = $true; break }
        }
        if (!$exited) { throw 'Timed out waiting for manual probe cancellation. No process was terminated.' }
        $report.bootstrapExitCode = $updater.ExitCode
        if ($updater.ExitCode -isnot [int] -or $updater.ExitCode -ne 0) { throw 'The exact bootstrap did not exit successfully.' }
        $finalStatus = Read-OldModJson $root $statusPath
        $report.installStatus = @{ state = $finalStatus.state; progress = $finalStatus.progress }
        if ($finalStatus.Count -ne 3 -or $finalStatus.state -cne 'cancelled' -or $finalStatus.progress -isnot [long] -or
            $finalStatus.progress -ne 0 -or $finalStatus.message -isnot [string]) { throw 'Final install status must be cancelled with integer progress zero.' }
        if (!(Test-OldModListener $game $manifest)) { throw 'The exact game API is unavailable after cancellation.' }
        $cancelled = Send-OldModHttp $client $manifest.port '/updates/status' $token
        Assert-OldModStatus $cancelled 'cancelled'
        $state = Read-OldModJson $root $statePath
        if ($state.installProcessId -ne 0 -or $state.installState -cne 'cancelled') { throw 'Old Mod did not clear the updater PID after cancellation.' }
        $report.modInstallState = $cancelled.installState
        $report.modInstallProcessId = $state.installProcessId
        $report.result = 'passed'
    } catch {
        $message = $_.Exception.Message
        if ($token) { $message = $message.Replace($token, '[redacted]') }
        $report.errors += $message
    } finally {
        if ($context) {
            try {
                $after = @(Get-OldModPluginSnapshot $root $context.manifest.pluginDirectory)
                $report.pluginUnchanged = ($context.snapshot | ConvertTo-Json -Depth 8 -Compress) -ceq ($after | ConvertTo-Json -Depth 8 -Compress)
                if (!$report.pluginUnchanged) { throw 'Frozen plugin contents or directory entries changed.' }
            } catch { $report.errors += $_.Exception.Message }
        }
        if ($game) {
            try { Assert-OldModProcess $game $context.manifest.gameExecutable; $report.gameStillRunning = $true }
            catch { $report.errors += 'The exact copied game is no longer running with its original path.' }
            try {
                $game.Refresh()
                $report.gameHasExited = $game.HasExited
                if ($report.gameHasExited) {
                    # This is the retained launch handle, never a replacement PID.
                    $report.gameExitCode = $game.ExitCode
                    $report.gameStillRunning = $false
                }
            }
            catch { $report.errors += 'Cannot inspect the retained game process exit state.' }
        }
        if ($binding) {
            try {
                $null = Assert-OldModOwnedPath $root $binding.backup -Directory -MayBeAbsent
                $report.backupAbsent = !(Test-Path -LiteralPath $binding.backup)
                if (!$report.backupAbsent) { throw 'The backup leaf was created.' }
            } catch { $report.errors += $_.Exception.Message }
        }
        if ($report.errors.Count -gt 0 -or !$report.pluginUnchanged -or !$report.gameStillRunning -or !$report.backupAbsent) { $report.result = 'failed' }
        $report.finishedUtc = [DateTime]::UtcNow.ToString('o')
        $null = Assert-OldModOwnedPath $root $evidence -Directory
        Write-OldModEvidence $root $reportPath $report $token
        if ($client) { $client.Dispose() }
        if ($updater) { $updater.Dispose() }
        if ($game) { $game.Dispose() }
        $token = ''
    }
    $label = if ($report.result -ceq 'passed') { 'PASS' } else { 'FAIL' }
    Write-Host "${label}: report at $reportPath"
    Write-Host 'No game process was terminated. Preserve the isolated workspace and report.'
    if ($report.result -cne 'passed') { throw "Old Mod launch probe failed. Read the redacted report: $reportPath" }
}

if ($MyInvocation.InvocationName -ne '.') {
    Invoke-OldModProbeMain -WorkspacePath $Workspace -ReadySeconds $ReadyTimeoutSeconds -CancelSeconds $CancelTimeoutSeconds -NodeRunId $NodeRunId
}
