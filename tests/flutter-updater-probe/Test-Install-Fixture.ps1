$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'Start-Install-Fixture.ps1') -FunctionsOnly
$root = Join-Path ([IO.Path]::GetTempPath()) ('mystia-install-ps-test-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
$count = 0
function Assert-Test([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message }; $script:count++ }
function Assert-Rejected([scriptblock]$Action) { $rejected = $false; try { & $Action | Out-Null } catch { $rejected = $true }; Assert-Test $rejected 'Expected invalid fixture input to be rejected.' }
try {
    foreach ($invalid in @('../escape', 'a/../b', 'CON.txt', 'a/NUL', 'a.', '/absolute', 'a\b', 'a//b', 'space name', 'unicodé', ('a' * 241))) { Assert-Rejected { Assert-FixtureRelative $invalid } }
    $source = Join-Path $root 'source'; [IO.Directory]::CreateDirectory((Join-Path $source 'data/assets')) | Out-Null
    Write-FixtureNew (Join-Path $source 'runner.exe') 'fake test executable'
    Write-FixtureNew (Join-Path $source 'data/assets/test.json') '{}'
    $files = Get-FixtureFiles $source
    Assert-Test ($files.Count -eq 2) 'Full nested manifest missing files.'
    Assert-FixtureManifest $source $files; $count++
    $copy = Join-Path $root 'copy'; [IO.Directory]::CreateDirectory($copy) | Out-Null
    Copy-FixtureRecords $source $copy $files
    Assert-FixtureManifest $copy $files; $count++
    Assert-Rejected { Write-FixtureNew (Join-Path $source 'runner.exe') 'overwrite forbidden' }
    Assert-Rejected { Copy-FixtureRecords $source $copy $files }
    [IO.File]::WriteAllText((Join-Path $copy 'data/assets/test.json'), 'tampered')
    Assert-Rejected { Assert-FixtureManifest $copy $files }
    Write-FixtureNew (Join-Path $source 'extra.dll') 'extra'
    Assert-Rejected { Assert-FixtureManifest $source $files }
    [IO.Directory]::CreateDirectory((Join-Path $copy 'empty')) | Out-Null
    Assert-Rejected { Get-FixtureFiles $copy }
    $bundle = Join-Path $root 'bundle'; [IO.Directory]::CreateDirectory($bundle) | Out-Null
    Write-FixtureNew (Join-Path $bundle 'runner.exe') 'content'
    $bundleFiles = Get-FixtureFiles $bundle
    $commit = 'a' * 40
    $evidence = [ordered]@{schemaVersion = 1; kind = 'test-build'; commit = $commit; cleanCheckout = $true; files = $bundleFiles}
    Write-FixtureNew (Join-Path $bundle 'build-evidence.json') ($evidence | ConvertTo-Json -Depth 6)
    $read = Read-FixtureBuild $bundle 'test-build' $commit
    Assert-Test ($read.commit -ceq $commit) 'Valid build identity rejected.'
    Assert-Rejected { Read-FixtureBuild $bundle 'other-build' $commit }
    Assert-Rejected { Read-FixtureBuild $bundle 'test-build' ('b' * 40) }
    [IO.File]::WriteAllText((Join-Path $bundle 'runner.exe'), 'modified')
    Assert-Rejected { Read-FixtureBuild $bundle 'test-build' $commit }
    $budget = Get-FixtureBudget 37500000 1000
    Assert-Test ($budget.minimumPayloadBytesPerSecond -eq 125000 -and $budget.minimumPayloadMegabitsPerSecond -eq 1) 'Five-minute payload budget differs.'
    Assert-Test ($budget.transferScenarios[0].withinFiveMinutes -eq $true -and $budget.transferScenarios[0].transferSeconds -eq 300) 'Exact deadline boundary differs.'
    Assert-Rejected { Get-FixtureBudget 0 0 }
    Assert-Rejected { Get-FixtureBudget 1 -1 }
    $zipPath = Join-Path $root 'test.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($bundle, $zipPath)
    $expanded = Join-Path $root 'expanded'
    [IO.Compression.ZipFile]::ExtractToDirectory($zipPath, $expanded)
    Assert-FixtureManifest $expanded (Get-FixtureFiles $bundle); $count++
    Write-Host "$count portable installation preparation checks passed. No Windows runtime claimed."
} finally { Remove-Item -LiteralPath $root -Recurse -Force }
