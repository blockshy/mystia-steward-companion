#requires -Version 7.0
# Tests validation functions only. No adapter main, game, UIA or network is run.
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'Invoke-OldMod-Probe.ps1')
$tokens = $null; $parseErrors = $null
$source = [Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $PSScriptRoot 'Node-OldMod-Probe.ps1'), [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -ne 0) { throw 'Node adapter syntax is invalid.' }
foreach ($name in @('Assert-NodeInteger', 'Test-NodeIntegerValue', 'Assert-NodeBusinessEvidence')) {
    $matches = @($source.FindAll({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name
    }, $false))
    if ($matches.Count -ne 1) { throw "Expected one validation function: $name" }
    . ([scriptblock]::Create($matches[0].Extent.Text))
}

$checks = 0
function Assert-Test([bool]$Value, [string]$Label) {
    if (!$Value) { throw "Test failed: $Label" }
    $script:checks++
}
function Assert-Rejected([scriptblock]$Action, [string]$Label) {
    $rejected = $false
    try { & $Action } catch { $rejected = $true }
    Assert-Test $rejected $Label
}

Assert-NodeInteger ([int]1) 'fixture'
Assert-NodeInteger ([long][int]::MaxValue) 'fixture'
$checks += 2
foreach ($invalid in @($null, $false, '10', [double]10, [long]2147483648, [int]0, [int]-1)) {
    Assert-Rejected { Assert-NodeInteger $invalid 'fixture' } 'PID type/bounds must be exact'
}
Assert-Test (Test-NodeIntegerValue ([long]0) 0) 'zero integer exit code'
Assert-Test (!(Test-NodeIntegerValue '0' 0)) 'string exit code rejected'
Assert-Test (!(Test-NodeIntegerValue $false 0)) 'boolean exit code rejected'

$RunId = 'node-validation-fixture'
$bootstrap = Join-Path $PSScriptRoot 'fixture-updater.exe'
$valid = @{
    schemaVersion = [long]1; kind = 'old-mod-real-launch-collector'; nodeRunId = $RunId
    executionMode = 'controlled-desktop-automation'; result = 'passed'
    bootstrapExitCode = [long]0; modInstallState = 'cancelled'; modInstallProcessId = [long]0
    installRequestCount = [long]1; errors = @(); gameProcessId = [long]41
    updaterProcessId = [long]42; updaterParentProcessId = [long]41; updaterExecutable = $bootstrap
    bootstrapSha256 = '18c3a381e6f363150209605f65da553680e1e13b250a50eb90c6298d7438357b'
    waitingVerified = $true; pluginUnchanged = $true; gameStillRunning = $true; backupAbsent = $true
}
Assert-NodeBusinessEvidence $valid 41 42 $bootstrap
$checks++
$mutations = @(
    @('schemaVersion', '1'), @('kind', 'unrelated'), @('nodeRunId', 'another-run'),
    @('executionMode', 'manual'), @('result', 'failed'), @('bootstrapExitCode', [long]1),
    @('bootstrapExitCode', '0'), @('modInstallState', 'succeeded'), @('modInstallProcessId', [long]42),
    @('installRequestCount', [long]2), @('installRequestCount', $true), @('errors', @('unexpected')),
    @('gameProcessId', [long]43), @('updaterProcessId', [long]43), @('updaterParentProcessId', [long]43),
    @('updaterExecutable', (Join-Path $PSScriptRoot 'other-updater.exe')), @('bootstrapSha256', ('0' * 64)),
    @('waitingVerified', $false), @('pluginUnchanged', 'True'), @('gameStillRunning', [long]1), @('backupAbsent', $null)
)
foreach ($mutation in $mutations) {
    $candidate = $valid.Clone()
    $candidate[$mutation[0]] = $mutation[1]
    Assert-Rejected { Assert-NodeBusinessEvidence $candidate 41 42 $bootstrap } "Invalid business field $($mutation[0])"
}
foreach ($field in @($valid.Keys)) {
    $candidate = $valid.Clone()
    $candidate.Remove($field)
    Assert-Rejected { Assert-NodeBusinessEvidence $candidate 41 42 $bootstrap } "Missing business field $field"
}
Write-Host "PASS: $checks node evidence validation checks (no real process/UI Automation run)."
