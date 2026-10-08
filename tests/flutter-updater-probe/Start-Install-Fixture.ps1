param(
    [string]$CompanionBundleDirectory,
    [string]$ModBundleDirectory,
    [string]$OldPluginDirectory,
    [string]$FixtureParent = $env:TEMP,
    [switch]$Automate,
    [switch]$FunctionsOnly
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-FixturePath([string]$Path, [switch]$Directory) {
    $full = [IO.Path]::GetFullPath($Path)
    $item = Get-Item -LiteralPath $full -Force
    if ($Directory -and !$item.PSIsContainer) { throw 'Expected a real directory.' }
    if (!$Directory -and $item.PSIsContainer) { throw 'Expected a regular file.' }
    $cursor = $item
    while ($null -ne $cursor) {
        if ($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Links/reparse points are forbidden: $full" }
        $cursor = if ($cursor -is [IO.DirectoryInfo]) { $cursor.Parent } else { $cursor.Directory }
    }
    return $full
}
function Assert-FixtureRelative([string]$Path) {
    if ($Path.Length -gt 240 -or $Path -cnotmatch '\A[A-Za-z0-9_.-]+(?:/[A-Za-z0-9_.-]+)*\z') { throw 'Nonportable bundle path.' }
    $parts = $Path.Split('/')
    if ($parts.Count -gt 16) { throw 'Bundle nesting exceeds limit.' }
    foreach ($part in $parts) {
        if ($part -in @('.', '..') -or $part.EndsWith('.') -or $part -imatch '\A(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|\z)') { throw 'Ambiguous/reserved bundle path.' }
    }
}
function Get-FixtureHash([string]$Path) {
    [void](Assert-FixturePath $Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}
function Write-FixtureNew([string]$Path, [string]$Text) {
    [void](Assert-FixturePath ([IO.Path]::GetDirectoryName($Path)) -Directory)
    $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    try { $bytes = [Text.UTF8Encoding]::new($false).GetBytes($Text); $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
}
function Get-FixtureFiles([string]$Directory) {
    $directoryPath = Assert-FixturePath $Directory -Directory
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $records = @()
    foreach ($item in @(Get-ChildItem -LiteralPath $directoryPath -Recurse -Force | Sort-Object FullName)) {
        [void](Assert-FixturePath $item.FullName -Directory:$item.PSIsContainer)
        $relative = [IO.Path]::GetRelativePath($directoryPath, $item.FullName).Replace('\', '/')
        Assert-FixtureRelative $relative
        if (!$seen.Add($relative)) { throw 'Case-ambiguous bundle entry.' }
        if ($item.PSIsContainer) {
            if (@(Get-ChildItem -LiteralPath $item.FullName -Force).Count -eq 0) { throw 'Empty bundle directories are not allowed.' }
        } else {
            if ($item.Length -gt 512MB) { throw 'Bundle file exceeds limit.' }
            $records += [pscustomobject][ordered]@{ path = $relative; size = $item.Length; sha256 = Get-FixtureHash $item.FullName }
        }
    }
    if ($records.Count -eq 0 -or $records.Count -gt 8192 -or ($records | Measure-Object size -Sum).Sum -gt 1GB) { throw 'Bundle file count/size exceeds limit.' }
    return ,$records
}
function Assert-FixtureManifest([string]$Directory, $Files) {
    $actual = Get-FixtureFiles $Directory
    if (($actual | Sort-Object { $_.path } | ConvertTo-Json -Depth 5 -Compress) -cne ($Files | Sort-Object { $_.path } | ConvertTo-Json -Depth 5 -Compress)) { throw "Full file manifest differs: $Directory" }
}
function Copy-FixtureRecords([string]$Source, [string]$Destination, $Files) {
    foreach ($record in $Files) {
        Assert-FixtureRelative $record.path
        $inputPath = Join-Path $Source $record.path
        if ((Get-FixtureHash $inputPath) -cne $record.sha256 -or (Get-Item -LiteralPath $inputPath).Length -ne $record.size) { throw 'Source changed before copy.' }
        $outputPath = Join-Path $Destination $record.path
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($outputPath)) | Out-Null
        [IO.File]::Copy($inputPath, $outputPath, $false)
        if ((Get-FixtureHash $outputPath) -cne $record.sha256) { throw 'Copy integrity differs.' }
    }
}
function Read-FixtureBuild([string]$Directory, [string]$Kind, [string]$Commit) {
    $evidencePath = Join-Path (Assert-FixturePath $Directory -Directory) 'build-evidence.json'
    [void](Assert-FixturePath $evidencePath)
    if ((Get-Item -LiteralPath $evidencePath).Length -gt 1MB) { throw 'Build evidence exceeds limit.' }
    $value = Get-Content -LiteralPath $evidencePath -Raw | ConvertFrom-Json -AsHashtable
    if ($value.schemaVersion -ne 1 -or $value.kind -cne $Kind -or $value.commit -cne $Commit -or $value.cleanCheckout -cne $true) { throw 'Input build provenance differs from the fixture bootstrap.' }
    $files = Get-FixtureFiles $Directory
    $delivered = @($files | Where-Object path -CNE 'build-evidence.json')
    if (($delivered | Sort-Object { $_.path } | ConvertTo-Json -Depth 5 -Compress) -cne ($value.files | Sort-Object { $_.path } | ConvertTo-Json -Depth 5 -Compress)) { throw 'Delivered build files differ from their evidence.' }
    return $value
}
function New-FixtureManifest([string]$Directory, [string]$Version) {
    return [ordered]@{schemaVersion = 1; product = 'mystia-steward-companion'; version = $Version; entrypoint = 'mystia-steward-companion-updater.exe'; files = Get-FixtureFiles $Directory}
}
function Start-FixtureProcess([string]$Executable, [string[]]$Arguments, [string]$WorkingDirectory) {
    $info = [Diagnostics.ProcessStartInfo]::new($Executable)
    $info.UseShellExecute = $false; $info.WorkingDirectory = $WorkingDirectory
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new(); $process.StartInfo = $info
    if (!$process.Start()) { $process.Dispose(); throw 'Cannot start owned fixture process.' }
    return $process
}
function Get-FixtureBudget([long]$ZipBytes, [long]$LocalMillis) {
    if ($ZipBytes -le 0 -or $LocalMillis -lt 0) { throw 'Invalid measured budget inputs.' }
    $seconds = 300.0
    return [ordered]@{
        downloadDeadlineSeconds = 300; measuredPackageZipBytes = $ZipBytes
        minimumPayloadBytesPerSecond = [Math]::Ceiling($ZipBytes / $seconds)
        minimumPayloadMegabitsPerSecond = $ZipBytes * 8 / $seconds / 1000000
        measuredLocalFixtureWallMillis = $LocalMillis
        transferScenarios = @(1, 5, 10, 20 | ForEach-Object { [ordered]@{ payloadMegabitsPerSecond = $_; transferSeconds = $ZipBytes * 8 / ($_ * 1000000.0); withinFiveMinutes = ($ZipBytes * 8 / ($_ * 1000000.0)) -le $seconds } })
        scope = 'Measured package bytes and local work; calculated payload-only transfer, not a public-network or old-Mod download success claim. The old five-minute deadline covers its download, not subsequent installation.'
    }
}
function Get-FixtureAllocation([string]$Directory) {
    if (!('MystiaInstallFixtureAllocation' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public static class MystiaInstallFixtureAllocation {
    [StructLayout(LayoutKind.Sequential)]
    struct StandardInfo { public long AllocationSize; public long EndOfFile; public uint NumberOfLinks; public byte DeletePending; public byte Directory; }
    [DllImport("kernel32.dll", SetLastError=true)]
    static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, out StandardInfo info, uint size);
    public static ulong Read(string path) {
        using (SafeFileHandle file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
            StandardInfo info;
            if (!GetFileInformationByHandleEx(file, 1, out info, (uint)Marshal.SizeOf<StandardInfo>())) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (info.Directory != 0 || info.AllocationSize < 0) throw new IOException("Invalid regular-file allocation.");
            return (ulong)info.AllocationSize;
        }
    }
}
'@
    }
    [long]$logical = 0; [ulong]$allocated = 0; $files = @()
    foreach ($file in @(Get-ChildItem -LiteralPath $Directory -File -Recurse -Force | Sort-Object FullName)) {
        [void](Assert-FixturePath $file.FullName)
        $allocation = [MystiaInstallFixtureAllocation]::Read($file.FullName)
        $logical += $file.Length; $allocated += $allocation
        $files += [ordered]@{path = [IO.Path]::GetRelativePath($Directory, $file.FullName).Replace('\', '/'); logicalBytes = $file.Length; allocatedBytes = $allocation}
    }
    return [ordered]@{schemaVersion = 1; phase = 'waiting-game-before-release'; logicalFileBytes = $logical; allocatedFileBytes = $allocated; files = $files;
        scope = 'GetFileInformationByHandleEx FileStandardInfo.AllocationSize while ZIP/old/staged/external runner/expanded UI coexist; includes current small metadata, excludes directory filesystem metadata, sources outside the fixture and subsequently written evidence. This is an observed allocation, not ambient volume free-space subtraction.'}
}
if ($FunctionsOnly) { return }
if (!$IsWindows -or $PSVersionTable.PSVersion.Major -lt 7) { throw 'This install fixture requires PowerShell 7 on Windows.' }
if (!$CompanionBundleDirectory -or !$ModBundleDirectory -or !$FixtureParent) { throw 'CompanionBundleDirectory, ModBundleDirectory and FixtureParent are required.' }
$bootstrapName = 'mystia-steward-companion-updater.exe'
$bootstrap = Join-Path $PSScriptRoot $bootstrapName
$build = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'build-evidence.json') -Raw | ConvertFrom-Json -AsHashtable
if ($build.schemaVersion -ne 1 -or $build.kind -cne 'p0-isolated-install-updater-probe' -or $build.cleanCheckout -cne $true -or $build.commit -cnotmatch '\A[a-f0-9]{40}\z' -or
    (Get-FixtureHash $bootstrap) -cne $build.bootstrapSha256 -or (Get-Item -LiteralPath $bootstrap).Length -ne $build.bootstrapBytes) { throw 'Wrong updater package; the read-only probe cannot install.' }
$companion = Read-FixtureBuild $CompanionBundleDirectory 'flutter-window-probe-bundle' $build.commit
$mod = Read-FixtureBuild $ModBundleDirectory 'flutter-control-mod-bundle' $build.commit
if ($companion.dartBuildGitSha -cne $build.commit -or $companion.nativeBuildGitSha -cne $build.commit -or $mod.compiledGitSha -cne $build.commit -or
    $mod.entrypoint -cne 'MystiaStewardCompanion.BepInEx.dll') { throw 'Compiled identities differ.' }
$sourceCompanionFiles = Get-FixtureFiles $CompanionBundleDirectory
$sourceModFiles = Get-FixtureFiles $ModBundleDirectory
$oldSourceFiles = if ($OldPluginDirectory) { Get-FixtureFiles $OldPluginDirectory } else { $null }
$fixtureId = [guid]::NewGuid().ToString('N')
$root = Join-Path (Assert-FixturePath $FixtureParent -Directory) "mystia-steward-companion-install-p0-$fixtureId"
if (Test-Path -LiteralPath $root) { throw 'Fresh fixture already exists.' }
[IO.Directory]::CreateDirectory($root) | Out-Null
$plugin = Join-Path $root 'mystia-steward-companion'
$staging = Join-Path $root 'staging'
$backup = Join-Path $root 'backups/previous'
$runner = Join-Path $root "runner/$bootstrapName"
$statePath = Join-Path $root 'state/install-status.json'
foreach ($directory in @($plugin, $staging, (Join-Path $root 'backups'), (Join-Path $root 'runner'), (Join-Path $root 'state'))) { [IO.Directory]::CreateDirectory($directory) | Out-Null }
$report = [ordered]@{schemaVersion = 1; kind = 'isolated-updater-install-fixture'; fixtureId = $fixtureId; gitSha = $build.commit; result = 'FAIL'; root = $root; p0Verified = $false; error = $null; automatedUi = $Automate.IsPresent; cleanMachine = $false}
$waiter = $null; $process = $null; $oldAutomation = $env:MYSTIA_UPDATER_PROBE_AUTOMATION
try {
    if ($OldPluginDirectory) { Copy-FixtureRecords $OldPluginDirectory $plugin $oldSourceFiles; $report.oldTreeScope = 'Read-only byte-for-byte copy of supplied existing plugin tree.' }
    else { [IO.File]::Copy($bootstrap, (Join-Path $plugin $bootstrapName), $false); Write-FixtureNew (Join-Path $plugin 'old-sentinel.txt') 'isolated synthetic previous installation'; $report.oldTreeScope = 'Synthetic previous tree; measured old/backup size is not a production 1.3.1 estimate.' }
    # Construct one complete package directly from verified source files. No
    # extra package-source copy remains to distort the coexistence measurement.
    $zipPath = Join-Path $root 'package.zip'
    $packageInputs = @([pscustomobject][ordered]@{path = $bootstrapName; source = $bootstrap}, [pscustomobject][ordered]@{path = $mod.entrypoint; source = (Join-Path $ModBundleDirectory $mod.entrypoint)})
    foreach ($record in $companion.files) {
        if ($record.path -ceq $companion.entrypoint -or $record.path -cmatch '\A[^/]+\.dll\z' -or $record.path.StartsWith('data/')) {
            $relative = if ($record.path -ceq $companion.entrypoint) { 'mystia-steward-companion.exe' } else { $record.path }
            $packageInputs += [pscustomobject][ordered]@{path = "companion/$relative"; source = (Join-Path $CompanionBundleDirectory $record.path)}
        }
    }
    if (@($packageInputs | Where-Object path -CEQ 'companion/flutter_windows.dll').Count -ne 1 -or @($packageInputs | Where-Object path -CLike 'companion/data/*').Count -eq 0) { throw 'Complete main Flutter bundle missing.' }
    $packageClock = [Diagnostics.Stopwatch]::StartNew()
    $zip = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
    try { foreach ($record in $packageInputs) { Assert-FixtureRelative $record.path; [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $record.source, $record.path, [IO.Compression.CompressionLevel]::Optimal) | Out-Null } } finally { $zip.Dispose() }
    $report.packageCompressionMillis = $packageClock.ElapsedMilliseconds
    if ((Get-Item -LiteralPath $zipPath).Length -gt 512MB) { throw 'Package ZIP exceeds the P0 limit.' }
    $localClock = [Diagnostics.Stopwatch]::StartNew()
    [IO.Compression.ZipFile]::ExtractToDirectory($zipPath, $staging)
    $report.packageExpansionMillis = $localClock.ElapsedMilliseconds
    $newManifest = New-FixtureManifest $staging $build.version
    foreach ($record in $newManifest.files) {
        $source = @($packageInputs | Where-Object path -CEQ $record.path)
        if ($source.Count -ne 1 -or (Get-FixtureHash $source[0].source) -cne $record.sha256) { throw 'ZIP expansion differs from verified source.' }
    }
    if ($newManifest.files.Count -ne $packageInputs.Count) { throw 'Expanded ZIP omits package files.' }
    $oldManifest = New-FixtureManifest $plugin $build.version
    Write-FixtureNew (Join-Path $root 'old-manifest.json') ($oldManifest | ConvertTo-Json -Depth 8)
    Write-FixtureNew (Join-Path $root 'new-manifest.json') ($newManifest | ConvertTo-Json -Depth 8)
    [IO.File]::Copy($bootstrap, $runner, $false)
    Write-FixtureNew (Join-Path $root 'waiter-token.txt') $fixtureId
    $waiter = Start-FixtureProcess $runner @('--fixture-waiter', $root) (Split-Path $runner -Parent)
    $readyPath = Join-Path $root 'waiter-ready.json'
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while (!(Test-Path -LiteralPath $readyPath)) {
        if ($waiter.HasExited -or [DateTime]::UtcNow -ge $deadline) { throw 'Owned retained-wait fixture did not become ready.' }
        Start-Sleep -Milliseconds 25
    }
    $waiterReady = Get-Content -LiteralPath $readyPath -Raw | ConvertFrom-Json -AsHashtable
    $creationHex = $waiter.StartTime.ToUniversalTime().ToFileTimeUtc().ToString('x')
    if ($waiterReady.pid -ne $waiter.Id -or $waiterReady.creationHex -cne $creationHex) { throw 'Waiter process identity mismatch.' }
    $descriptor = [ordered]@{schemaVersion = 1; kind = 'mystia-updater-install-fixture'; id = $fixtureId; gitSha = $build.commit; gamePid = $waiter.Id; gameCreationHex = $creationHex;
        oldManifestSha256 = Get-FixtureHash (Join-Path $root 'old-manifest.json'); newManifestSha256 = Get-FixtureHash (Join-Path $root 'new-manifest.json'); packageZipSha256 = Get-FixtureHash $zipPath; packageZipBytes = (Get-Item -LiteralPath $zipPath).Length}
    Write-FixtureNew (Join-Path $root 'fixture.json') ($descriptor | ConvertTo-Json -Depth 5)
    $env:MYSTIA_UPDATER_PROBE_AUTOMATION = if ($Automate) { 'install-fixture-after-ready' } else { '' }
    $process = Start-FixtureProcess $runner @('--game-pid', "$($waiter.Id)", '--plugin-dir', $plugin, '--staged-dir', $staging, '--backup-dir', $backup, '--status-file', $statePath, '--control-port', '32756', '--wait-timeout-seconds', '300') (Split-Path $runner -Parent)
    $report.bootstrapPid = $process.Id; $report.waiterPid = $waiter.Id
    $deadline = [DateTime]::UtcNow.AddSeconds(300)
    $released = $false; $runtimeRecorded = $false
    while (!$process.HasExited) {
        if ([DateTime]::UtcNow -ge $deadline) { throw 'Owned bootstrap exceeded the bounded fixture deadline.' }
        if (Test-Path -LiteralPath $statePath) {
            $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json -AsHashtable
            if ($state.state -ceq 'waiting-game' -and !$released) {
                # Sampling modules of the exact child while the retained waiter
                # is alive prevents a fast transaction from erasing this evidence.
                $children = @(Get-CimInstance Win32_Process -Filter "ParentProcessId = $($process.Id)" | Where-Object Name -CEQ 'mystia-steward-companion-updater-ui.exe')
                if ($children.Count -ne 1) { throw 'Cannot identify the sole updater UI child.' }
                $ui = [Diagnostics.Process]::GetProcessById([int]$children[0].ProcessId)
                try {
                    $uiPath = Assert-FixturePath $ui.MainModule.FileName
                    if (![IO.Path]::GetFullPath($uiPath).StartsWith((Join-Path $root 'runner') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Updater UI is outside the owned runner.' }
                    $uiDirectory = Split-Path $uiPath -Parent
                    $modules = @($ui.Modules | ForEach-Object { [pscustomobject][ordered]@{name = $_.ModuleName; path = $_.FileName; sha256 = Get-FixtureHash $_.FileName} })
                    $runtime = @($modules | Where-Object name -IMatch '\A(?:flutter_windows|msvcp140.*|vcruntime140.*)\.dll\z')
                    if (@($runtime | Where-Object name -IEQ 'flutter_windows.dll').Count -ne 1 -or @($runtime | Where-Object name -IMatch '\A(?:msvcp140|vcruntime140)').Count -eq 0) { throw 'Expected loaded Flutter/VC runtime modules missing.' }
                    foreach ($module in $runtime) { if (!(Split-Path $module.path -Parent).Equals($uiDirectory, [StringComparison]::OrdinalIgnoreCase)) { throw 'Flutter/VC runtime was loaded from outside the app-local bundle.' } }
                    Write-FixtureNew (Join-Path $root 'runtime-modules.json') ([ordered]@{schemaVersion = 1; uiPid = $ui.Id; creationUtc = $ui.StartTime.ToUniversalTime().ToString('O'); executable = $uiPath; modules = $modules; appLocalFlutterAndCrt = $true; cleanOsVerified = $false} | ConvertTo-Json -Depth 6)
                    $runtimeRecorded = $true
                } finally { $ui.Dispose() }
                $allocation = Get-FixtureAllocation $root
                Write-FixtureNew (Join-Path $root 'disk-observation.json') ($allocation | ConvertTo-Json -Depth 6)
                $report.observedWaitingAllocatedFileBytes = $allocation.allocatedFileBytes
                $report.observedWaitingLogicalFileBytes = $allocation.logicalFileBytes
                Write-FixtureNew (Join-Path $root 'release-waiter.txt') $fixtureId
                $released = $true
            }
        }
        Start-Sleep -Milliseconds 25
    }
    $process.WaitForExit()
    if (!$waiter.WaitForExit(5000)) { throw 'Retained fixture waiter did not exit.' }
    $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json -AsHashtable
    $native = Get-Content -LiteralPath (Join-Path $root 'install-evidence.json') -Raw | ConvertFrom-Json -AsHashtable
    if ($process.ExitCode -ne 0 -or $waiter.ExitCode -ne 0 -or !$released -or !$runtimeRecorded -or $state.state -cne 'succeeded' -or $state.progress -ne 100 -or
        $native.gitSha -cne $build.commit -or $native.fixtureId -cne $fixtureId -or $native.bootstrapPid -ne $process.Id -or $native.waiterPid -ne $waiter.Id -or
        $null -ne $native.exchangeError -or $null -ne $native.installError -or $native.state.status.state -cne 'succeeded' -or !$native.state.terminal) { throw 'Runtime exit/status/evidence does not prove a successful installation.' }
    Assert-FixtureManifest $plugin $newManifest.files
    Assert-FixtureManifest $backup $oldManifest.files
    if (Test-Path -LiteralPath $staging) { throw 'Successful installation unexpectedly retained staging.' }
    Assert-FixtureManifest $CompanionBundleDirectory $sourceCompanionFiles
    Assert-FixtureManifest $ModBundleDirectory $sourceModFiles
    if ($OldPluginDirectory) { Assert-FixtureManifest $OldPluginDirectory $oldSourceFiles }
    $report.bootstrapExitCode = $process.ExitCode; $report.waiterExitCode = $waiter.ExitCode
    $report.result = 'PASS'; $report.budget = Get-FixtureBudget $descriptor.packageZipBytes $localClock.ElapsedMilliseconds
    $report.logicalCoexistencePeakBytes = $native.logicalCoexistencePeakBytes
    $report.peakScope = 'Logical payload bytes: ZIP + full old tree (later backup) + full staged tree (later installed) + external bootstrap + expanded updater bundle. NTFS allocation/metadata, generated evidence, original source artifacts and ambient volume usage are separate.'
    $report.sourceFilesUnchanged = $true; $report.installedFiles = $newManifest.files.Count; $report.backupFiles = $oldManifest.files.Count
} catch { $report.error = $_.Exception.Message }
finally {
    $env:MYSTIA_UPDATER_PROBE_AUTOMATION = $oldAutomation
    # Never force-kill a transaction after admission. Release only our own
    # waiter, retain evidence and let its fixed process deadline report failure.
    if ($null -ne $waiter -and !$waiter.HasExited -and !(Test-Path -LiteralPath (Join-Path $root 'release-waiter.txt'))) { Write-FixtureNew (Join-Path $root 'release-waiter.txt') $fixtureId }
    if ($null -ne $process) { $report.bootstrapAliveAtReport = !$process.HasExited; $process.Dispose() }
    if ($null -ne $waiter) { $report.waiterAliveAtReport = !$waiter.HasExited; $waiter.Dispose() }
    Write-FixtureNew (Join-Path $root 'probe-report.json') ($report | ConvertTo-Json -Depth 12)
}
Write-Host "Fixture result: $($report.result). Preserved evidence: $root"
if ($report.result -cne 'PASS') { throw $report.error }
