#requires -Version 7.0
# Fixed payload adapter. Invoke only through the node's interactive task/driver.
param(
    [Parameter(Mandatory = $true)][string]$RunId,
    [Parameter(Mandatory = $true)][string]$ResultFile,
    [Parameter(Mandatory = $true)][string]$ExpectedGitSha
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$lockedPowerShell = 'D:\dev\mystia-node\tools\powershell-7.6.4\pwsh.exe'
$expectedUiHash = '01169d145e4d7addf56428e281c21accb843b1991354c27be0b8f625c3da1220'
$collectorScript = Join-Path $PSScriptRoot 'Invoke-OldMod-Probe.ps1'
# This sibling has a dot-source guard. Reuse its strict JSON/path/process checks;
# the actual business collector runs separately with its own process and report.
. $collectorScript

function Assert-NodeInteger($Value, [string]$Label) {
    if (($Value -isnot [int] -and $Value -isnot [long]) -or $Value -le 0 -or $Value -gt [int]::MaxValue) {
        throw "$Label must be a positive process identifier."
    }
}

function Test-NodeIntegerValue($Value, [long]$Expected) {
    return ($Value -is [int] -or $Value -is [long]) -and $Value -eq $Expected
}

function Assert-NodeBusinessEvidence($Business, [int]$GamePid, [int]$BootstrapPid, [string]$BootstrapExecutable) {
    if (!(Test-NodeIntegerValue $Business.schemaVersion 1) -or $Business.kind -cne 'old-mod-real-launch-collector' -or
        $Business.nodeRunId -cne $RunId -or $Business.executionMode -cne 'controlled-desktop-automation' -or
        $Business.result -cne 'passed' -or !(Test-NodeIntegerValue $Business.bootstrapExitCode 0) -or
        $Business.modInstallState -cne 'cancelled' -or !(Test-NodeIntegerValue $Business.modInstallProcessId 0) -or
        !(Test-NodeIntegerValue $Business.installRequestCount 1) -or @($Business.errors).Count -ne 0 -or
        !(Test-NodeIntegerValue $Business.gameProcessId $GamePid) -or
        !(Test-NodeIntegerValue $Business.updaterProcessId $BootstrapPid) -or
        !(Test-NodeIntegerValue $Business.updaterParentProcessId $GamePid) -or
        (Get-OldModFullPath $Business.updaterExecutable) -ine $BootstrapExecutable -or
        $Business.bootstrapSha256 -cne '18c3a381e6f363150209605f65da553680e1e13b250a50eb90c6298d7438357b') {
        throw 'Business report does not prove one old Mod launch and normal cancelled exit.'
    }
    foreach ($field in @('waitingVerified', 'pluginUnchanged', 'gameStillRunning', 'backupAbsent')) {
        if ($Business[$field] -isnot [bool] -or $Business[$field] -cne $true) { throw "Business evidence failed: $field" }
    }
}

function Assert-NodeRunContract {
    if (!$IsWindows -or $PSVersionTable.PSVersion.ToString() -cne '7.6.4') {
        throw 'The node adapter requires Windows and locked PowerShell 7.6.4.'
    }
    if ($RunId -cnotmatch '\A[A-Za-z0-9][A-Za-z0-9_-]{0,79}\z' -or $ExpectedGitSha -cnotmatch '\A[a-f0-9]{40}\z') {
        throw 'Invalid fixed run ID or compiled Git SHA.'
    }
    $root = Assert-OldModPlainPath (Join-Path 'D:\dev\mystia-node\runs' $RunId) -Directory
    if ((Get-OldModFullPath $PSScriptRoot) -ine (Join-Path $root 'payload') -or
        (Get-OldModFullPath $ResultFile) -ine (Join-Path $root 'probe-result.json')) {
        throw 'Adapter and result must belong to this run root.'
    }
    $null = Assert-OldModPlainPath $PSScriptRoot -Directory
    $null = Assert-OldModPlainPath $lockedPowerShell
    $null = Assert-OldModOwnedPath $root $collectorScript
    $null = Assert-OldModOwnedPath $root $ResultFile -MayBeAbsent
    if (Test-Path -LiteralPath $ResultFile) { throw 'The run already has a probe result.' }
    $input = Read-OldModJson $root (Join-Path $root 'old-mod-probe-input.json')
    $keys = @('schemaVersion', 'runId', 'gitSha', 'workspace')
    if ($input.Count -ne $keys.Count -or @($input.Keys | Where-Object { $_ -cnotin $keys }).Count -ne 0 -or
        $input.schemaVersion -isnot [long] -or $input.schemaVersion -ne 1 -or
        $input.runId -cne $RunId -or $input.gitSha -cne $ExpectedGitSha -or $input.workspace -isnot [string]) {
        throw 'Run input must match the compiled driver identity and exact schema.'
    }
    $workspacePath = Assert-OldModOwnedPath $root $input.workspace -Directory
    if ($workspacePath -ine (Join-Path $root 'workspace')) { throw 'Workspace must be this run\workspace.' }
    if (![string]::IsNullOrEmpty([Environment]::GetEnvironmentVariable('MYSTIA_UPDATER_PROBE_AUTOMATION'))) {
        throw 'Dart auto-cancel must be disabled for the physical UI Automation probe.'
    }
    return @{ root = $root; workspace = $workspacePath }
}

function Start-NodeCollector([string]$WorkspacePath) {
    $start = [Diagnostics.ProcessStartInfo]::new($lockedPowerShell)
    $start.UseShellExecute = $false
    $start.WorkingDirectory = $PSScriptRoot
    foreach ($argument in @('-NoLogo', '-NoProfile', '-NonInteractive', '-File', $collectorScript,
        '-Workspace', $WorkspacePath, '-NodeRunId', $RunId, '-ReadyTimeoutSeconds', '180', '-CancelTimeoutSeconds', '180')) {
        $start.ArgumentList.Add($argument)
    }
    $process = [Diagnostics.Process]::Start($start)
    Assert-OldModProcess $process $lockedPowerShell
    return $process
}

function Wait-NodeMarker([string]$Root, [string]$Path, $Collector) {
    $deadline = [DateTime]::UtcNow.AddSeconds(240)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($Collector.HasExited) { throw 'Business collector exited before verified waiting state.' }
        if (Test-Path -LiteralPath $Path) {
            try { return Read-OldModJson $Root $Path }
            # Marker uses CreateNew with FileShare.None. A concurrent observer
            # may briefly see its name before its complete JSON is readable.
            catch [IO.IOException] { }
        }
        Start-Sleep -Milliseconds 100
    }
    throw 'No verified waiting marker before the bounded deadline.'
}

function Read-NodeBootstrap([string]$Root, [string]$WorkspacePath, $Marker, $Manifest) {
    if ($Marker.schemaVersion -isnot [long] -or $Marker.schemaVersion -ne 1 -or
        $Marker.kind -cne 'old-mod-waiting-observed' -or $Marker.nodeRunId -cne $RunId -or
        (Get-OldModFullPath $Marker.workspace) -ine $WorkspacePath -or
        $Marker.bootstrapSha256 -cne '18c3a381e6f363150209605f65da553680e1e13b250a50eb90c6298d7438357b') {
        throw 'Waiting marker does not belong to the accepted bootstrap and current run.'
    }
    Assert-NodeInteger $Marker.gamePid 'Game PID'
    Assert-NodeInteger $Marker.bootstrapPid 'Bootstrap PID'
    $executable = Assert-OldModOwnedPath $Root $Marker.bootstrapExecutable
    $relative = [IO.Path]::GetRelativePath((Join-Path $Manifest.updatesDirectory 'runner'), $executable).Replace('\', '/')
    if ($relative -cnotmatch '\A[0-9]{14}/mystia-steward-companion-updater\.exe\z') {
        throw 'Bootstrap marker points outside the exact old Mod runner layout.'
    }
    Assert-OldModBootstrap $Root $executable
    $bootstrap = Get-OldModProcess ([int]$Marker.bootstrapPid)
    try {
        Assert-OldModProcess $bootstrap $executable
        $metadata = @(Get-OldModCimProcess $bootstrap.Id)
        if ($metadata.Count -ne 1 -or $metadata[0].ParentProcessId -ne $Marker.gamePid -or
            (Get-OldModFullPath $metadata[0].ExecutablePath) -ine $executable) {
            throw 'Bootstrap parent/path changed before desktop automation.'
        }
        return @{ process = $bootstrap; executable = $executable }
    } catch { $bootstrap.Dispose(); throw }
}

function Wait-NodeFlutterChild([string]$Root, $Bootstrap, $Collector) {
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($Collector.HasExited) { throw 'Business collector exited before Flutter child binding.' }
        Assert-OldModProcess $Bootstrap.process $Bootstrap.executable
        $children = @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$($Bootstrap.process.Id)" -ErrorAction Stop)
        if ($children.Count -gt 1) { throw 'Bootstrap has multiple child processes; automation refused.' }
        if ($children.Count -eq 1) {
            $executable = Assert-OldModOwnedPath $Root $children[0].ExecutablePath
            $relative = [IO.Path]::GetRelativePath([IO.Path]::GetDirectoryName($Bootstrap.executable), $executable).Replace('\', '/')
            if ($relative -cnotmatch '\Ap0-bundle-[a-f0-9]{32}/mystia-steward-companion-updater-ui\.exe\z' -or
                (Get-OldModSha256 $executable) -cne $expectedUiHash) {
                throw 'Bootstrap child is not the accepted Flutter UI executable.'
            }
            $ui = Get-OldModProcess ([int]$children[0].ProcessId)
            try {
                Assert-OldModProcess $ui $executable
                if ($ui.SessionId -ne [Diagnostics.Process]::GetCurrentProcess().SessionId) {
                    throw 'Flutter UI belongs to a different Windows session.'
                }
                Assert-OldModProcess $Bootstrap.process $Bootstrap.executable
                return @{ process = $ui; executable = $executable }
            } catch { $ui.Dispose(); throw }
        }
        Start-Sleep -Milliseconds 100
    }
    throw 'No exact Flutter UI child before the bounded deadline.'
}

function Find-NodeCancelButton($Ui) {
    $processCondition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty, [int]$Ui.process.Id)
    $windows = [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children, $processCondition)
    if ($windows.Count -eq 0) { return $null }
    $conditions = [Windows.Automation.Condition[]]@(
        $processCondition,
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Button),
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, '结束探针'),
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::IsEnabledProperty, $true)
    )
    $button = $null
    foreach ($window in $windows) {
        $buttons = $window.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.AndCondition]::new($conditions))
        if ($buttons.Count -gt 1 -or ($buttons.Count -eq 1 -and $null -ne $button)) {
            throw 'Multiple matching enabled End probe buttons; no action was sent.'
        }
        if ($buttons.Count -eq 1) { $button = $buttons[0] }
    }
    return $button
}

$context = $null; $collector = $null; $bootstrap = $null; $ui = $null; $game = $null
$result = [ordered]@{
    schemaVersion = 1; runId = $RunId; gitSha = $ExpectedGitSha; status = 'FAIL'; p0Verified = $false
    scope = 'old-mod-launch-and-controlled-uia-cancel'; executionMode = 'automatic-physical-desktop'
    startedUtc = [DateTime]::UtcNow.ToString('o'); errors = @()
    businessReport = $null; businessReportSha256 = $null; collectorExitCode = $null
    uiaAction = 'node-uia-action.json'
    cleanupBoundary = 'Business gameStillRunning is measured before payload exit. The existing node worker then closes its job and cleans up this run process tree.'
}
$action = [ordered]@{
    schemaVersion = 1; runId = $RunId; gitSha = $ExpectedGitSha
    executionMode = 'automatic-physical-desktop'; mechanism = 'UIAutomation.InvokePattern'
    status = 'not-attempted'; invokeCount = 0; invokeReturned = $false
    bootstrapPid = $null; uiPid = $null; uiExecutable = $null; uiSha256 = $null
    name = '结束探针'; controlType = 'Button'; enabled = $null; error = $null
}
try {
    $context = Assert-NodeRunContract
    foreach ($leaf in @('node-uia-action.json', 'node-uia-intent.json')) {
        if (Test-Path -LiteralPath (Join-Path $context.root $leaf)) { throw 'This run already has UI action evidence.' }
    }
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    $validated = Read-OldModWorkspace $context.workspace
    $manifest = $validated.manifest
    $reportPath = Join-Path $manifest.evidenceDirectory 'old-mod-probe-report.json'
    $waitingPath = Join-Path $manifest.evidenceDirectory 'waiting-observed.json'
    if ((Test-Path -LiteralPath $reportPath) -or (Test-Path -LiteralPath $waitingPath)) { throw 'Workspace already has business evidence.' }
    $result.businessReport = [IO.Path]::GetRelativePath($context.root, $reportPath).Replace('\', '/')
    $result.windowsSessionId = [Diagnostics.Process]::GetCurrentProcess().SessionId
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        $result.elevated = [Security.Principal.WindowsPrincipal]::new($identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    } finally { $identity.Dispose() }
    $collector = Start-NodeCollector $context.workspace
    $result.collectorProcessId = $collector.Id
    $marker = Wait-NodeMarker $context.root $waitingPath $collector
    Assert-NodeInteger $marker.gamePid 'Game PID'
    $game = Get-OldModProcess ([int]$marker.gamePid)
    Assert-OldModProcess $game $manifest.gameExecutable
    $bootstrap = Read-NodeBootstrap $context.root $context.workspace $marker $manifest
    $ui = Wait-NodeFlutterChild $context.root $bootstrap $collector
    $action.bootstrapPid = $bootstrap.process.Id
    $action.uiPid = $ui.process.Id
    $action.uiExecutable = $ui.executable
    $action.uiSha256 = $expectedUiHash
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    $button = $null
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($collector.HasExited) { throw 'Business collector exited before UI Automation cancellation.' }
        Assert-OldModProcess $ui.process $ui.executable
        Assert-OldModProcess $bootstrap.process $bootstrap.executable
        $button = Find-NodeCancelButton $ui
        if ($null -ne $button) { break }
        Start-Sleep -Milliseconds 100
    }
    if ($null -eq $button) { throw 'No unique enabled End probe button before the bounded deadline.' }
    # Recheck all retained identities immediately before the only UI operation.
    Assert-OldModProcess $game $manifest.gameExecutable
    Assert-OldModProcess $bootstrap.process $bootstrap.executable
    Assert-OldModProcess $ui.process $ui.executable
    $current = @(Get-OldModCimProcess $ui.process.Id)
    if ($current.Count -ne 1 -or $current[0].ParentProcessId -ne $bootstrap.process.Id -or
        (Get-OldModFullPath $current[0].ExecutablePath) -ine $ui.executable -or
        (Get-OldModSha256 $ui.executable) -cne $expectedUiHash) { throw 'Flutter child identity changed before InvokePattern.' }
    Assert-OldModBootstrap $context.root $bootstrap.executable
    if ($button.Current.ProcessId -ne $ui.process.Id -or $button.Current.Name -cne '结束探针' -or
        $button.Current.ControlType -ne [Windows.Automation.ControlType]::Button -or !$button.Current.IsEnabled) {
        throw 'Button identity changed before InvokePattern.'
    }
    $pattern = $null
    if (!$button.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern, [ref]$pattern) -or
        $pattern -isnot [Windows.Automation.InvokePattern]) { throw 'The exact button does not expose InvokePattern.' }
    $action.enabled = $true
    $action.status = 'prepared'
    $action.preparedUtc = [DateTime]::UtcNow.ToString('o')
    Write-OldModEvidence $context.root (Join-Path $context.root 'node-uia-intent.json') $action
    # No retry: an Invoke exception can have an unknown side-effect outcome.
    $action.status = 'attempted'
    $action.invokeCount = 1
    $action.attemptedUtc = [DateTime]::UtcNow.ToString('o')
    $pattern.Invoke()
    $action.invokeReturned = $true
    $action.status = 'invoked'
    if (!$collector.WaitForExit(180000)) { throw 'Collector did not finish after the one UI Automation action.' }
    $result.collectorExitCode = $collector.ExitCode
    if ($collector.ExitCode -ne 0) { throw 'Business collector exited nonzero after UI Automation.' }
    $business = Read-OldModJson $context.root $reportPath
    Assert-NodeBusinessEvidence $business $game.Id $bootstrap.process.Id $bootstrap.executable
    if (!$bootstrap.process.HasExited -or $bootstrap.process.ExitCode -ne 0 -or
        !$ui.process.HasExited -or $ui.process.ExitCode -ne 0) { throw 'Retained bootstrap/UI handles do not both prove exit zero.' }
    Assert-OldModProcess $game $manifest.gameExecutable
    $result.businessReportSha256 = Get-OldModSha256 $reportPath
    $result.bootstrapExitCode = $bootstrap.process.ExitCode
    $result.uiExitCode = $ui.process.ExitCode
    $result.status = 'PASS'
} catch {
    $message = $_.Exception.Message
    $result.errors += $message
    $action.error = $message
    if ($action.status -ceq 'attempted') { $action.status = 'outcome-unknown' }
} finally {
    $action.finishedUtc = [DateTime]::UtcNow.ToString('o')
    $result.finishedUtc = [DateTime]::UtcNow.ToString('o')
    # No Kill/CloseMainWindow/breakaway here. The fixed worker owns job cleanup.
    if ($game) { $game.Dispose() }
    if ($ui) { $ui.process.Dispose() }
    if ($bootstrap) { $bootstrap.process.Dispose() }
    if ($collector) { $collector.Dispose() }
    if ($context) {
        try {
            Write-OldModEvidence $context.root (Join-Path $context.root 'node-uia-action.json') $action
            Write-OldModEvidence $context.root $ResultFile $result
        } catch {
            [Console]::Error.WriteLine('Cannot publish node evidence: ' + $_.Exception.Message)
            $result.status = 'FAIL'
        }
    }
}
if ($result.status -cne 'PASS') {
    [Console]::Error.WriteLine('Node old Mod probe failed: ' + ($result.errors -join '; '))
    exit 1
}
Write-Host 'PASS: controlled desktop cancellation and independent old Mod business evidence agree.'
exit 0
