# Build only the fixed Windows node adapter; this does not run a game or updater.
#requires -Version 7.0
param(
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [Parameter(Mandatory = $true)][string]$EvidenceDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path $PSScriptRoot -Parent
Set-Location $repo
if (!$IsWindows -or [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne 'X64') {
    throw 'This payload must be built on Windows x64.'
}
$lock = Get-Content -LiteralPath (Join-Path $repo 'toolchain.lock.json') -Raw | ConvertFrom-Json
if ($PSVersionTable.PSVersion.ToString() -cne $lock.powershell) { throw 'PowerShell differs from toolchain.lock.json.' }
$powershell = Join-Path $PSHOME 'pwsh.exe'
$node = (Get-Command node -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$nodeVersion = & $node --version
if ($LASTEXITCODE -ne 0 -or $nodeVersion -cne "v$($lock.node)") { throw 'Node differs from toolchain.lock.json.' }
$rustup = (Get-Command rustup -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$toolchain = "$($lock.rust)-x86_64-pc-windows-msvc"
$rustc = & $rustup which --toolchain $toolchain rustc
if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $rustc -PathType Leaf)) { throw 'The locked MSVC Rust compiler is not installed.' }
$rustfmt = & $rustup which --toolchain $toolchain rustfmt
if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $rustfmt -PathType Leaf)) { throw 'The locked Rust formatter is not installed.' }
$rustVersion = @(& $rustc --version --verbose)
if ($LASTEXITCODE -ne 0 -or $rustVersion -cnotcontains "release: $($lock.rust)" -or
    $rustVersion -cnotcontains 'host: x86_64-pc-windows-msvc') { throw 'Rust compiler version or host differs from the lock.' }
$formatVersion = & $rustfmt --version
if ($LASTEXITCODE -ne 0) { throw 'Unable to inspect the locked Rust formatter.' }

function Assert-CleanBuildSource {
    $current = & git rev-parse HEAD
    if ($LASTEXITCODE -ne 0 -or $current -cnotmatch '\A[a-f0-9]{40}\z') { throw 'Build requires a full Git commit SHA.' }
    $changes = @(& git status --porcelain=v1 --untracked-files=all)
    if ($LASTEXITCODE -ne 0 -or $changes.Count -ne 0) { throw 'Build requires a clean committed checkout, including untracked files.' }
    if ($env:GITHUB_SHA -and $env:GITHUB_SHA -cne $current) { throw 'Checkout HEAD differs from the Actions commit.' }
    return $current
}

function Resolve-NewBuildDirectory([string]$Value) {
    if (![IO.Path]::IsPathFullyQualified($Value) -or $Value -match '[\x00-\x1f]') { throw 'Build destinations must be explicit absolute paths.' }
    $full = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Value))
    if (Test-Path -LiteralPath $full) { throw "Build destination already exists: $full" }
    $parent = [IO.Path]::GetDirectoryName($full)
    if (!$parent -or !(Test-Path -LiteralPath $parent -PathType Container)) { throw 'Build destination parent must already exist.' }
    for ($current = $parent; $current; $current = [IO.Path]::GetDirectoryName($current)) {
        if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Build destinations cannot traverse links or reparse points.' }
    }
    return $full
}

function Get-BuildFileRecord([string]$Base, [string]$File) {
    $item = Get-Item -LiteralPath $File -Force
    if ($item.PSIsContainer -or $item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Build inputs and payload files must be regular files.' }
    return [ordered]@{
        path = [IO.Path]::GetRelativePath($Base, $File).Replace('\', '/')
        size = $item.Length
        sha256 = (Get-FileHash -LiteralPath $File -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

$commit = Assert-CleanBuildSource
$output = Resolve-NewBuildDirectory $OutputDirectory
$evidence = Resolve-NewBuildDirectory $EvidenceDirectory
if ($output -ieq $evidence -or $output.StartsWith($evidence + '\', [StringComparison]::OrdinalIgnoreCase) -or
    $evidence.StartsWith($output + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Payload and build evidence directories must be disjoint.' }
$sourceDirectory = Join-Path $repo 'tests/flutter-old-mod-probe'
$required = @('node-driver.rs', 'Prepare-OldMod-Probe.ps1', 'Prepare-OldMod-Probe.Common.psm1',
    'Invoke-OldMod-Probe.ps1', 'Node-OldMod-Probe.ps1', 'Test-Prepare-OldMod-Probe.ps1',
    'Test-Invoke-OldMod-Probe.ps1', 'Test-Node-OldMod-Probe.ps1', 'README.md')
foreach ($name in $required) {
    if (!(Test-Path -LiteralPath (Join-Path $sourceDirectory $name) -PathType Leaf)) { throw "Missing probe source: $name" }
}
$sourceScopes = @('tests/flutter-old-mod-probe', 'scripts/build-flutter-old-mod-probe.ps1',
    '.github/workflows/flutter-old-mod-probe.yml', 'scripts/install-locked-release-tools.mjs', 'toolchain.lock.json', '.nvmrc')
$sourcePaths = @(& git ls-files -- @sourceScopes)
if ($LASTEXITCODE -ne 0 -or $sourcePaths.Count -eq 0) { throw 'Cannot enumerate committed build sources.' }
$sourceFiles = @($sourcePaths | Sort-Object | ForEach-Object { Get-BuildFileRecord $repo (Join-Path $repo $_) })
$deliveryFiles = @(Get-ChildItem -LiteralPath $sourceDirectory -File -Recurse -Force |
    Where-Object { $_.Extension -in @('.ps1', '.psm1') -or $_.Name -ceq 'README.md' } | Sort-Object FullName)
foreach ($item in Get-ChildItem -LiteralPath $sourceDirectory -Recurse -Force) {
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Probe source links are forbidden.' }
}

New-Item -ItemType Directory -Path $evidence -ErrorAction Stop | Out-Null
$source = Join-Path $sourceDirectory 'node-driver.rs'
$savedCommit = [Environment]::GetEnvironmentVariable('MYSTIA_NODE_DRIVER_GIT_SHA')
try {
    $env:MYSTIA_NODE_DRIVER_GIT_SHA = $commit
    & $rustfmt --edition 2021 --check $source
    if ($LASTEXITCODE -ne 0) { throw 'Node driver formatting check failed.' }
    $testExecutable = Join-Path $evidence 'node-driver-tests.exe'
    & $rustc --edition=2021 --crate-name mystia_old_mod_node_driver --test -D warnings -C target-feature=+crt-static $source -o $testExecutable
    if ($LASTEXITCODE -ne 0) { throw 'Node driver test compilation failed.' }
    & $testExecutable 2>&1 | Tee-Object -FilePath (Join-Path $evidence 'node-driver-tests.log')
    if ($LASTEXITCODE -ne 0) { throw 'Node driver tests failed.' }
    & $powershell -NoLogo -NoProfile -NonInteractive -File (Join-Path $sourceDirectory 'Test-Invoke-OldMod-Probe.ps1') -OutputParent $evidence 2>&1 |
        Tee-Object -FilePath (Join-Path $evidence 'collector-tests.log')
    if ($LASTEXITCODE -ne 0) { throw 'Old Mod collector mock tests failed.' }
    & $powershell -NoLogo -NoProfile -NonInteractive -File (Join-Path $sourceDirectory 'Test-Node-OldMod-Probe.ps1') 2>&1 |
        Tee-Object -FilePath (Join-Path $evidence 'adapter-tests.log')
    if ($LASTEXITCODE -ne 0) { throw 'Node adapter tests failed.' }

    New-Item -ItemType Directory -Path $output -ErrorAction Stop | Out-Null
    $driver = Join-Path $output 'old-mod-node-driver.exe'
    & $rustc --edition=2021 --crate-name mystia_old_mod_node_driver -D warnings -C opt-level=2 -C target-feature=+crt-static $source -o $driver
    if ($LASTEXITCODE -ne 0) { throw 'Node driver build failed.' }
    foreach ($file in $deliveryFiles) {
        $destination = Join-Path $output ([IO.Path]::GetRelativePath($sourceDirectory, $file.FullName))
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
        [IO.File]::Copy($file.FullName, $destination, $false)
    }
    if ((Assert-CleanBuildSource) -cne $commit) { throw 'Build commit changed during verification.' }
    $after = @($sourcePaths | Sort-Object | ForEach-Object { Get-BuildFileRecord $repo (Join-Path $repo $_) })
    if (($sourceFiles | ConvertTo-Json -Depth 8 -Compress) -cne ($after | ConvertTo-Json -Depth 8 -Compress)) {
        throw 'Build source bytes changed during verification.'
    }
    $payloadFiles = @(Get-ChildItem -LiteralPath $output -File -Recurse | Sort-Object FullName |
        ForEach-Object { Get-BuildFileRecord $output $_.FullName })
    $build = [ordered]@{
        schemaVersion = 1; product = 'mystia-steward-companion'; kind = 'old-mod-fixed-node-driver-payload'
        commit = $commit; cleanCheckout = $true; builtUtc = [DateTime]::UtcNow.ToString('o')
        target = 'x86_64-pc-windows-msvc'; driver = 'old-mod-node-driver.exe'
        driverBuildGitSha = $commit; driverSha256 = (Get-FileHash -LiteralPath $driver -Algorithm SHA256).Hash.ToLowerInvariant()
        tools = [ordered]@{ node = $nodeVersion; powershell = $PSVersionTable.PSVersion.ToString(); rustToolchain = $toolchain; rustc = $rustVersion; rustfmt = $formatVersion }
        checks = [ordered]@{ rustfmt = 'passed'; driverTests = 'passed'; collectorMockTests = 'passed'; adapterTests = 'passed'; prepareTests = 'not-run-requires-accepted-bootstrap'; gameRuntime = 'not-run' }
        sourceFiles = $sourceFiles; files = $payloadFiles
        fileHashScope = 'All delivered files except this build-evidence.json, which cannot contain its own hash.'
    }
    [IO.File]::WriteAllText((Join-Path $output 'build-evidence.json'), ($build | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
    Write-Host "Fixed node payload: $output"
    Write-Host "Build/test evidence: $evidence"
} finally {
    [Environment]::SetEnvironmentVariable('MYSTIA_NODE_DRIVER_GIT_SHA', $savedCommit)
}
