# Fixture-only verifier; compatible with Windows PowerShell 5.1 and PowerShell 7.
param([string]$OutputParent = (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'temp'))

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (!(Test-Path -LiteralPath $OutputParent -PathType Container)) { throw 'OutputParent must already exist.' }
$testRoot = Join-Path $OutputParent ('flutter-start-probe-tests-' + [guid]::NewGuid().ToString('N'))
$package = Join-Path $testRoot 'fixture-package'
New-Item -ItemType Directory -Path $package | Out-Null
$scriptPath = Join-Path $package 'Start-Probe.ps1'
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Start-Probe.ps1') -Destination $scriptPath
[IO.File]::WriteAllText((Join-Path $package 'mystia-steward-companion-updater.exe'), 'fixture only; never executed')
Write-Host "Start-Probe verifier evidence: $testRoot"

function Assert-ProbeTest {
    param([bool]$Condition, [string]$Message)
    if (!$Condition) { throw $Message }
}

# These functions shadow only commands invoked by the fixture copy of the script.
# No bootstrap/UI process is launched and no antivirus settings are queried/changed.
$probeTestState = @{ calls = 0; scenario = $null }
function Get-MpComputerStatus { return [pscustomobject]@{ AntivirusEnabled = $true } }
function Start-Process {
    param([string]$FilePath, [string[]]$ArgumentList, [switch]$PassThru)
    $probeTestState.calls++
    Assert-ProbeTest ($FilePath -like '*mystia-steward-companion-updater.exe') 'Unexpected fixture executable.'
    $values = @{}
    for ($index = 0; $index -lt $ArgumentList.Count; $index += 2) {
        $values[$ArgumentList[$index].Trim('"')] = $ArgumentList[$index + 1].Trim('"')
    }
    if ($probeTestState.scenario.launchThrows) { throw 'Fixture launch failure.' }
    $status = $values['--status-file']
    if ($probeTestState.scenario.invalidUtf8) {
        [IO.File]::WriteAllBytes($status, [byte[]]@(0xc3, 0x28))
    } elseif ($null -ne $probeTestState.scenario.status) {
        [IO.File]::WriteAllText($status, $probeTestState.scenario.status, [Text.UTF8Encoding]::new($false))
    }
    $plugin = $values['--plugin-dir']
    switch ($probeTestState.scenario.mutation) {
        'content' { [IO.File]::WriteAllText((Join-Path $plugin 'probe-sentinel.txt'), 'changed fixture') }
        'extra-file' { [IO.File]::WriteAllText((Join-Path $plugin 'unexpected.txt'), 'extra fixture file') }
        'backup' { New-Item -ItemType Directory -Path $values['--backup-dir'] | Out-Null }
    }
    $process = [pscustomobject]@{
        ExitCode = $probeTestState.scenario.exitCode
        WaitResult = $probeTestState.scenario.waitResult
        WaitThrows = $probeTestState.scenario.waitThrows
    }
    $process | Add-Member -MemberType ScriptMethod -Name WaitForExit -Value {
        param([int]$Milliseconds)
        if ($this.WaitThrows) { throw 'Fixture wait failure.' }
        return $this.WaitResult
    }
    return $process
}

$valid = '{"state":"cancelled","message":"P0 fixture","progress":0}'
$cases = @(
    @{ name = 'manual-cancelled'; passed = $true },
    @{ name = 'automatic-cancelled'; passed = $true; mode = 'cancel-after-ready'; expectedMode = 'automatic' },
    @{ name = 'reordered-and-escaped-message'; passed = $true; status = '{ "progress": 0, "message": "\u53d6\u6d88 \"fixture\"", "state": "cancelled" }' },
    @{ name = 'unknown-automation'; mode = 'other'; expectedMode = 'invalid'; expectedCalls = 0 },
    @{ name = 'wrong-case-automation'; mode = 'CANCEL-AFTER-READY'; expectedMode = 'invalid'; expectedCalls = 0 },
    @{ name = 'nonzero-bootstrap'; exitCode = 1 },
    @{ name = 'missing-exit-code'; exitCode = $null },
    @{ name = 'string-exit-code'; exitCode = '0' },
    @{ name = 'reported-ui-crash'; exitCode = 1; status = '{"state":"failed","message":"UI exited with 0xc000041d","progress":0}' },
    @{ name = 'failed-with-zero-exit'; status = '{"state":"failed","message":"fixture failure","progress":0}' },
    @{ name = 'succeeded-is-not-cancelled'; status = '{"state":"succeeded","message":"fixture","progress":0}' },
    @{ name = 'wrong-case-state'; status = '{"state":"Cancelled","message":"fixture","progress":0}' },
    @{ name = 'progress-one'; status = '{"state":"cancelled","message":"fixture","progress":1}' },
    @{ name = 'progress-string'; status = '{"state":"cancelled","message":"fixture","progress":"0"}' },
    @{ name = 'progress-fraction'; status = '{"state":"cancelled","message":"fixture","progress":0.0}' },
    @{ name = 'progress-boolean'; status = '{"state":"cancelled","message":"fixture","progress":false}' },
    @{ name = 'missing-status'; status = $null },
    @{ name = 'malformed-status'; status = '{"state":' },
    @{ name = 'duplicate-status-key'; status = '{"state":"failed","state":"cancelled","message":"fixture","progress":0}' },
    @{ name = 'duplicate-key-three-fields'; status = '{"state":"cancelled","state":"cancelled","progress":0}' },
    @{ name = 'extra-status-field'; status = '{"state":"cancelled","message":"fixture","progress":0,"extra":0}' },
    @{ name = 'array-status'; status = '[{"state":"cancelled","message":"fixture","progress":0}]' },
    @{ name = 'nonstring-message'; status = '{"state":"cancelled","message":0,"progress":0}' },
    @{ name = 'nul-message'; status = '{"state":"cancelled","message":"\u0000","progress":0}' },
    @{ name = 'invalid-utf8'; invalidUtf8 = $true },
    @{ name = 'changed-plugin'; mutation = 'content' },
    @{ name = 'extra-plugin-file'; mutation = 'extra-file' },
    @{ name = 'created-backup'; mutation = 'backup' },
    @{ name = 'launch-exception'; launchThrows = $true },
    @{ name = 'wait-timeout'; waitResult = $false },
    @{ name = 'wait-exception'; waitThrows = $true }
)
$results = New-Object 'System.Collections.Generic.List[object]'
$originalAutomation = [Environment]::GetEnvironmentVariable('MYSTIA_UPDATER_PROBE_AUTOMATION')
try {
    foreach ($case in $cases) {
        $probeTestState.scenario = @{
            passed = $false; mode = $null; expectedMode = 'manual'; expectedCalls = 1
            status = $valid; exitCode = 0; waitResult = $true; waitThrows = $false
            launchThrows = $false; invalidUtf8 = $false; mutation = ''
        }
        foreach ($key in $case.Keys) { $probeTestState.scenario[$key] = $case[$key] }
        [Environment]::SetEnvironmentVariable('MYSTIA_UPDATER_PROBE_AUTOMATION', $probeTestState.scenario.mode)
        $caseRoot = Join-Path $testRoot $case.name
        New-Item -ItemType Directory -Path $caseRoot | Out-Null
        $log = Join-Path $caseRoot 'terminal.log'
        $probeTestState.calls = 0
        $caught = $null
        try { & $scriptPath -EvidenceParent $caseRoot *> $log } catch { $caught = $_.Exception.Message }
        $directories = @(Get-ChildItem -LiteralPath $caseRoot -Directory)
        Assert-ProbeTest ($directories.Count -eq 1) "$($case.name): expected one evidence directory."
        $reportPath = Join-Path $directories[0].FullName 'probe-report.json'
        Assert-ProbeTest (Test-Path -LiteralPath $reportPath -PathType Leaf) "$($case.name): report was lost."
        $report = Get-Content -LiteralPath $reportPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $terminal = Get-Content -LiteralPath $log -Raw
        Assert-ProbeTest ($report.executionMode -ceq $probeTestState.scenario.expectedMode) "$($case.name): wrong execution mode."
        Assert-ProbeTest ($probeTestState.calls -eq $probeTestState.scenario.expectedCalls) "$($case.name): unexpected launch count."
        if ($probeTestState.scenario.passed) {
            Assert-ProbeTest ($null -eq $caught) "$($case.name): valid fixture threw: $caught"
            Assert-ProbeTest ($report.result -ceq 'passed' -and @($report.errors).Count -eq 0) "$($case.name): valid fixture did not pass."
            Assert-ProbeTest ($terminal -match '(?m)^PASS \(' -and $terminal -notmatch '(?m)^FAIL') "$($case.name): missing PASS terminal result."
        } else {
            Assert-ProbeTest ($null -ne $caught) "$($case.name): failure did not throw after saving evidence."
            Assert-ProbeTest ($report.result -ceq 'failed' -and @($report.errors).Count -gt 0) "$($case.name): failure was accepted."
            Assert-ProbeTest ($terminal -match '(?m)^FAIL \(' -and $terminal -notmatch '(?m)^PASS') "$($case.name): misleading terminal result."
        }
        if ($case.name -ceq 'reported-ui-crash') {
            Assert-ProbeTest (($report.errors -join ' ') -match '0xc000041d') 'UI crash detail was lost.'
        }
        $results.Add([ordered]@{ name = $case.name; passed = $true; report = $reportPath })
    }
} finally {
    [Environment]::SetEnvironmentVariable('MYSTIA_UPDATER_PROBE_AUTOMATION', $originalAutomation)
}
$summary = [ordered]@{
    kind = 'mock-process-and-real-fixture-files'; powershell = $PSVersionTable.PSVersion.ToString()
    passed = $true; checks = @($results.ToArray())
}
[IO.File]::WriteAllText((Join-Path $testRoot 'verifier-report.json'), ($summary | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
Write-Host "$($results.Count) Start-Probe checks passed. No real bootstrap or UI process was launched."
