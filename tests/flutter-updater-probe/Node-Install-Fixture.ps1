param(
    [Parameter(Mandatory = $true)][string]$RunId,
    [Parameter(Mandatory = $true)][string]$ResultFile,
    [Parameter(Mandatory = $true)][string]$ExpectedGitSha
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (!$IsWindows -or $PSVersionTable.PSVersion.ToString() -cne '7.6.4' -or
    $RunId -cnotmatch '\A[A-Za-z0-9][A-Za-z0-9_-]{0,79}\z' -or $ExpectedGitSha -cnotmatch '\A[a-f0-9]{40}\z') { throw 'Node fixture invocation differs from the fixed contract.' }
$runRoot = Join-Path 'D:/dev/mystia-node/runs' $RunId
$payload = Join-Path $runRoot 'payload'
if (![IO.Path]::GetFullPath($PSScriptRoot).Equals([IO.Path]::GetFullPath($payload), [StringComparison]::OrdinalIgnoreCase) -or
    ![IO.Path]::GetFullPath($ResultFile).Equals([IO.Path]::GetFullPath((Join-Path $runRoot 'probe-result.json')), [StringComparison]::OrdinalIgnoreCase) -or
    (Test-Path -LiteralPath $ResultFile)) { throw 'Node payload/result must belong to this unused run.' }
. (Join-Path $payload 'Start-Install-Fixture.ps1') -FunctionsOnly
[void](Assert-FixturePath $runRoot -Directory)
$report = [ordered]@{schemaVersion = 1; kind = 'updater-install-node-adapter'; runId = $RunId; gitSha = $ExpectedGitSha; status = 'FAIL'; p0Verified = $false; processSessionId = [Diagnostics.Process]::GetCurrentProcess().SessionId; fixtureReport = $null; errors = @()}
try {
    $build = Get-Content -LiteralPath (Join-Path $payload 'build-evidence.json') -Raw | ConvertFrom-Json -AsHashtable
    if ($build.commit -cne $ExpectedGitSha -or $build.kind -cne 'p0-isolated-install-updater-probe' -or
        (Get-FixtureHash (Join-Path $payload 'Node-Install-Fixture.ps1')) -cne $build.nodeAdapterSha256 -or
        (Get-FixtureHash (Join-Path $payload 'Start-Install-Fixture.ps1')) -cne $build.fixtureScriptSha256 -or
        (Get-FixtureHash (Join-Path $payload 'mystia-updater-install-node-driver.exe')) -cne $build.nodeDriverSha256) { throw 'Fixed node adapter/build identity differs.' }
    $workspace = Join-Path $runRoot 'workspace'
    if (Test-Path -LiteralPath $workspace) { throw 'Installation node workspace must be newly owned.' }
    [IO.Directory]::CreateDirectory($workspace) | Out-Null
    $parameters = @{CompanionBundleDirectory = (Join-Path $payload 'components/window'); ModBundleDirectory = (Join-Path $payload 'components/control-mod'); FixtureParent = $workspace; Automate = $true}
    # Optional read-only old-tree sampling is limited to the exact game location
    # supplied by the user; no caller can redirect writes into that source.
    $oldSource = 'E:/SteamLibrary/steamapps/common/Touhou Mystia Izakaya/BepInEx/plugins/mystia-steward-companion'
    if (Test-Path -LiteralPath $oldSource) { $parameters.OldPluginDirectory = $oldSource }
    & (Join-Path $payload 'Start-Install-Fixture.ps1') @parameters
    $fixtures = @(Get-ChildItem -LiteralPath $workspace -Directory -Force)
    if ($fixtures.Count -ne 1 -or $fixtures[0].Name -cnotmatch '\Amystia-steward-companion-install-p0-[a-f0-9]{32}\z') { throw 'Expected exactly one installation fixture.' }
    $path = Join-Path $fixtures[0].FullName 'probe-report.json'
    [void](Assert-FixturePath $path)
    $business = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -AsHashtable
    if ($business.schemaVersion -ne 1 -or $business.kind -cne 'isolated-updater-install-fixture' -or $business.gitSha -cne $ExpectedGitSha -or
        $business.result -cne 'PASS' -or $null -ne $business.error -or !$business.sourceFilesUnchanged -or
        $business.bootstrapExitCode -ne 0 -or $business.waiterExitCode -ne 0 -or $business.bootstrapAliveAtReport -or $business.waiterAliveAtReport) { throw 'Business report does not prove installation completion.' }
    $report.fixtureReport = $path; $report.fixtureReportSha256 = Get-FixtureHash $path
    $report.status = 'PASS'
} catch { $report.errors += $_.Exception.Message }
Write-FixtureNew $ResultFile ($report | ConvertTo-Json -Depth 8)
if ($report.status -cne 'PASS') { throw ($report.errors -join '; ') }
