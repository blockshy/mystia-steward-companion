# Compatible with Windows PowerShell 5.1. Run as a normal user.
param([string]$EvidenceParent = $env:TEMP)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function ConvertFrom-ProbeStatus {
    param([Parameter(Mandatory = $true)][string]$Json)
    # This is the fixed, flat three-field status schema, not a general JSON parser.
    # PS 5.1 ConvertFrom-Json can discard duplicate keys, so check tokens first.
    $stringToken = '"(?:[^"\\\x00-\x1f]|\\(?:["\\/bfnrt]|u[0-9a-fA-F]{4}))*"'
    $space = '[ \t\r\n]*'
    $member = '"(?<key>state|message|progress)"' + $space + ':' + $space + '(?<value>' + $stringToken + '|0|[1-9][0-9]*)'
    $pattern = '\A' + $space + '\{' + $space + $member + $space + '(?:,' + $space + $member + $space + '){2}\}' + $space + '\z'
    $match = [regex]::Match($Json, $pattern, [Text.RegularExpressions.RegexOptions]::CultureInvariant, [TimeSpan]::FromSeconds(1))
    if (!$match.Success) { throw 'Status must be a JSON object with exactly state, message and integer progress.' }
    $keys = @($match.Groups['key'].Captures | ForEach-Object { $_.Value })
    if (@($keys | Sort-Object -Unique).Count -ne 3) { throw 'Status contains a duplicate field.' }
    for ($index = 0; $index -lt 3; $index++) {
        if ($keys[$index] -ceq 'progress' -and $match.Groups['value'].Captures[$index].Value -notmatch '\A(?:0|[1-9][0-9]?|100)\z') {
            throw 'Status progress must be an integer from 0 to 100.'
        }
    }
    $value = ConvertFrom-Json -InputObject $Json
    if ($value.state -isnot [string] -or $value.message -isnot [string]) { throw 'Status state and message must be strings.' }
    if ($value.message.Contains([string][char]0) -or [Text.Encoding]::UTF8.GetByteCount($value.message) -gt 32768) {
        throw 'Status message is invalid.'
    }
    return $value
}

$source = Join-Path $PSScriptRoot 'mystia-steward-companion-updater.exe'
if (!(Test-Path -LiteralPath $source -PathType Leaf)) { throw 'Probe bootstrap is missing.' }
if ([string]::IsNullOrWhiteSpace($EvidenceParent) -or !(Test-Path -LiteralPath $EvidenceParent -PathType Container)) {
    throw 'EvidenceParent must be an existing directory.'
}
$root = Join-Path (Get-Item -LiteralPath $EvidenceParent).FullName ('mystia-steward-companion-p0-' + [guid]::NewGuid().ToString('N'))
$plugin = Join-Path $root 'mystia-steward-companion'
$staged = Join-Path $root 'staging'
$runner = Join-Path $root 'runner'
$backup = Join-Path $root 'backups/probe-old'
$state = Join-Path $root 'state'
$status = Join-Path $state 'install-status.json'
New-Item -ItemType Directory -Path $plugin, $staged, $runner, $state, (Split-Path $backup -Parent) | Out-Null
$sentinel = Join-Path $plugin 'probe-sentinel.txt'
[IO.File]::WriteAllText($sentinel, 'Read-only updater probe. This file must remain unchanged.')
$before = (Get-FileHash -LiteralPath $sentinel -Algorithm SHA256).Hash
Copy-Item -LiteralPath $source -Destination $staged
Copy-Item -LiteralPath $source -Destination $runner
$exe = Join-Path $runner 'mystia-steward-companion-updater.exe'
$report = [ordered]@{
    kind = 'fixture-only-no-game-install'; startedUtc = [DateTime]::UtcNow.ToString('o')
    osVersion = [Environment]::OSVersion.Version.ToString(); is64Bit = [Environment]::Is64BitOperatingSystem
    bootstrapSha256 = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
    sourceZone = 'absent-or-unavailable'; defender = $null; result = 'not-started'
    executionMode = 'manual'; exitCode = $null; statusState = $null; statusProgress = $null
    pluginUnchanged = $false; backupAbsent = $false
}
try { $report.sourceZone = Get-Content -LiteralPath $source -Stream Zone.Identifier -Raw -ErrorAction Stop } catch { $report.sourceZoneError = $_.Exception.Message }
try {
    $report.defender = Get-MpComputerStatus -ErrorAction Stop | Select-Object AMProductVersion, AMEngineVersion, AntivirusSignatureVersion, AntivirusSignatureLastUpdated, AntivirusEnabled, RealTimeProtectionEnabled, SmartAppControlState
} catch { $report.defender = 'query-unavailable'; $report.defenderError = $_.Exception.Message }
Write-Host 'This probe uses a new temporary fixture. It does not install updates or access the game.'
Write-Host "Evidence directory: $root"
$watch = [Diagnostics.Stopwatch]::StartNew()
$failures = New-Object 'System.Collections.Generic.List[string]'
try {
    $automation = [Environment]::GetEnvironmentVariable('MYSTIA_UPDATER_PROBE_AUTOMATION')
    if ($automation -ceq 'cancel-after-ready') {
        $report.executionMode = 'automatic'
    } elseif (![string]::IsNullOrEmpty($automation)) {
        $report.executionMode = 'invalid'
        throw 'Unsupported MYSTIA_UPDATER_PROBE_AUTOMATION value.'
    }
    $argsList = @('--game-pid', "$PID", '--plugin-dir', $plugin, '--staged-dir', $staged,
        '--backup-dir', $backup, '--status-file', $status, '--control-port', '32146', '--wait-timeout-seconds', '300')
    # All arguments are generated locally; reject quote ambiguity before Windows joins argv.
    $quoted = @($argsList | ForEach-Object {
        if ($_ -match '["\r\n]') { throw 'Unsupported quote in probe path.' }
        '"' + $_ + '"'
    })
    $process = Start-Process -FilePath $exe -ArgumentList $quoted -PassThru
    if (!$process.WaitForExit(600000)) { throw 'Probe timeout; collect evidence and close its window manually.' }
    $report.exitCode = $process.ExitCode
    if ($report.exitCode -isnot [int] -or $report.exitCode -ne 0) {
        $failures.Add("Bootstrap exit code is not zero: $($report.exitCode)")
    }
} catch {
    $failures.Add('Launch/wait failed: ' + $_.Exception.Message)
} finally {
    $watch.Stop()
    $report.elapsedMilliseconds = $watch.ElapsedMilliseconds
}
try {
    $pluginItem = Get-Item -LiteralPath $plugin -Force
    $sentinelItem = Get-Item -LiteralPath $sentinel -Force
    $report.pluginUnchanged = $pluginItem.PSIsContainer -and !$sentinelItem.PSIsContainer -and
        !($pluginItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -and
        !($sentinelItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -and
        ((Get-FileHash -LiteralPath $sentinel -Algorithm SHA256).Hash -eq $before) -and
        (@(Get-ChildItem -LiteralPath $plugin -Force).Count -eq 1)
    if (!$report.pluginUnchanged) { $failures.Add('Plugin fixture changed.') }
} catch { $failures.Add('Plugin fixture check failed: ' + $_.Exception.Message) }
try {
    $report.backupAbsent = !(Test-Path -LiteralPath $backup)
    if (!$report.backupAbsent) { $failures.Add('Backup fixture must remain absent.') }
} catch { $failures.Add('Backup fixture check failed: ' + $_.Exception.Message) }
try {
    $statusItem = Get-Item -LiteralPath $status -Force
    if ($statusItem.PSIsContainer -or ($statusItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $statusItem.Length -gt 65536) {
        throw 'Status must be a regular file of at most 64 KiB.'
    }
    $report.status = [Text.UTF8Encoding]::new($false, $true).GetString([IO.File]::ReadAllBytes($status))
    $parsedStatus = ConvertFrom-ProbeStatus -Json $report.status
    $report.statusState = $parsedStatus.state
    $report.statusProgress = $parsedStatus.progress
    if ($parsedStatus.state -cne 'cancelled' -or $parsedStatus.progress -ne 0) {
        $failures.Add("Expected cancelled with progress 0; got $($parsedStatus.state)/$($parsedStatus.progress). $($parsedStatus.message)")
    }
} catch { $failures.Add('Status check failed: ' + $_.Exception.Message) }
try {
    $report.runnerBytes = (Get-ChildItem -LiteralPath $runner -File -Recurse | Measure-Object Length -Sum).Sum
} catch { $failures.Add('Runner evidence check failed: ' + $_.Exception.Message) }
$report.result = if ($failures.Count -eq 0) { 'passed' } else { 'failed' }
$report.errors = @($failures.ToArray())
$report.finishedUtc = [DateTime]::UtcNow.ToString('o')
$reportPath = Join-Path $root 'probe-report.json'
try {
    [IO.File]::WriteAllText($reportPath, ($report | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
} catch {
    Write-Host "FAIL: Cannot write probe-report.json. Preserve evidence at: $root"
    throw
}
if ($report.result -ceq 'passed') {
    Write-Host "PASS ($($report.executionMode)): bootstrap exit 0; status cancelled/0; fixture unchanged; backup absent."
} else {
    Write-Host "FAIL ($($report.executionMode)): bootstrap exit '$($report.exitCode)'; status '$($report.statusState)/$($report.statusProgress)'."
    foreach ($failure in $failures) {
        $summary = $failure -replace '\s+', ' '
        if ($summary.Length -gt 320) { $summary = $summary.Substring(0, 320) + '...' }
        Write-Host ('  - ' + $summary)
    }
}
Write-Host "Return probe-report.json and state/install-status.json (if present) from: $root"
Write-Host 'Preserve this directory. Also describe the window behavior and any Defender/SmartScreen prompt.'
if ($report.result -cne 'passed') { throw "Updater probe FAILED. Report: $reportPath" }
