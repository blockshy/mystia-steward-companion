param(
    [Parameter(Mandatory = $true)][string]$SdkRoot,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [switch]$InstallFixture
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path $PSScriptRoot -Parent
Set-Location $repo
$commit = (& git rev-parse HEAD)
if ($LASTEXITCODE -ne 0 -or $commit -cnotmatch '\A[a-f0-9]{40}\z') { throw 'Cannot bind build commit.' }
if ($InstallFixture -and @(& git status --porcelain --untracked-files=normal).Count -ne 0) { throw 'Install fixture requires a clean committed checkout.' }
$lock = Get-Content -LiteralPath (Join-Path $repo 'toolchain.lock.json') -Raw | ConvertFrom-Json
if ($PSVersionTable.PSVersion.ToString() -ne $lock.powershell) { throw 'PowerShell version differs from toolchain.lock.json.' }
if ((& node --version) -ne "v$($lock.node)") { throw 'Node version differs from toolchain.lock.json.' }
if ((& rustc --version) -notmatch "^rustc $([regex]::Escape($lock.rust)) ") { throw 'Rust version differs from toolchain.lock.json.' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw "Output already exists: $output" }
$preparedText = & node scripts/flutter-windows-toolchain.mjs --prepare
if ($LASTEXITCODE -ne 0) { throw 'Windows build toolchain preflight failed.' }
$prepared = ($preparedText -join "`n") | ConvertFrom-Json
foreach ($property in $prepared.environment.PSObject.Properties) {
    [Environment]::SetEnvironmentVariable($property.Name, [string]$property.Value, 'Process')
}
# Both Cargo and its build scripts use this exact x64 toolchain; inherited flags
# cannot choose a different linker. The process-local vcvars environment binds SDK libraries.
$env:CARGO_ENCODED_RUSTFLAGS = $null
$env:CARGO_BUILD_RUSTFLAGS = $null
$rustTarget = 'x86_64-pc-windows-msvc'
& node scripts/run-flutter-platform-probe.mjs --sdk-root $SdkRoot --build-windows
if ($LASTEXITCODE -ne 0) { throw 'Flutter probe checks/build failed.' }

$bundle = Join-Path $repo 'tests/flutter-platform-probe/build/windows/x64/runner/Release'
if (!(Test-Path -LiteralPath (Join-Path $bundle 'mystia-steward-companion-updater-ui.exe'))) { throw 'Missing Flutter runner.' }
foreach ($entry in @((Get-Item -LiteralPath $bundle)) + @(Get-ChildItem -LiteralPath $bundle -Force -Recurse)) {
    if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Bundle links are forbidden.' }
}
# Ship the redistributable runtime app-locally, as required on machines without VS.
$windowsTools = Get-Content -LiteralPath (Join-Path $repo 'tests/flutter-platform-probe/build/windows/x64/windows-toolchain-evidence.json') -Raw | ConvertFrom-Json
if (!$windowsTools.actualFlutterBuildVerified -or ($windowsTools.identity | ConvertTo-Json -Compress) -cne ($prepared.identity | ConvertTo-Json -Compress)) { throw 'Updater UI and Rust preparation selected different toolchains.' }
$vs = $windowsTools.identity.visualStudioPath
$redistVersion = $windowsTools.identity.vcRuntimeVersion
$crtRoot = Join-Path $vs "VC/Redist/MSVC/$redistVersion/x64"
$crtDirectories = @(Get-ChildItem -LiteralPath $crtRoot -Directory -Filter 'Microsoft.VC*.CRT')
if ($crtDirectories.Count -ne 1) { throw 'Expected exactly one x64 VC CRT directory.' }
Copy-Item -Path (Join-Path $crtDirectories[0].FullName '*.dll') -Destination $bundle

$work = Join-Path $repo ('temp/flutter-updater-probe-build-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
$archive = Join-Path $work 'bundle.zip'
$manifestPath = Join-Path $work 'bundle-manifest.json'
$files = @(Get-ChildItem -LiteralPath $bundle -File -Recurse | Sort-Object FullName | ForEach-Object {
    if ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Bundle links are forbidden.' }
    [ordered]@{
        path = [IO.Path]::GetRelativePath($bundle, $_.FullName).Replace('\', '/')
        size = $_.Length
        sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
})
$version = (Get-Content -LiteralPath (Join-Path $repo 'package.json') -Raw | ConvertFrom-Json).version
$manifest = [ordered]@{schemaVersion = 1; product = 'mystia-steward-companion'; version = $version; entrypoint = 'mystia-steward-companion-updater-ui.exe'; files = $files}
[IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
$zip = [IO.Compression.ZipFile]::Open($archive, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in $files) {
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Join-Path $bundle $file.path), $file.path, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $zip.Dispose() }
$env:MYSTIA_UPDATER_PROBE_BUNDLE_ZIP = $archive
$env:MYSTIA_UPDATER_PROBE_BUNDLE_MANIFEST = $manifestPath
$env:MYSTIA_UPDATER_PROBE_PRODUCT_VERSION = $version
$env:MYSTIA_UPDATER_PROBE_GIT_SHA = $commit
$env:RUSTFLAGS = '-C target-feature=+crt-static'
$target = Join-Path $work 'target'
& cargo test --locked --manifest-path tests/flutter-updater-probe/Cargo.toml --target-dir $target --target $rustTarget --features install-fixture
if ($LASTEXITCODE -ne 0) { throw 'Updater probe tests failed.' }
$binary = if ($InstallFixture) { 'mystia-steward-companion-updater-install-probe' } else { 'mystia-steward-companion-updater' }
$buildArguments = @('build', '--release', '--locked', '--manifest-path', 'tests/flutter-updater-probe/Cargo.toml', '--target-dir', $target, '--target', $rustTarget, '--bin', $binary)
if ($InstallFixture) { $buildArguments += @('--features', 'install-fixture') }
& cargo @buildArguments
if ($LASTEXITCODE -ne 0) { throw 'Updater probe build failed.' }
New-Item -ItemType Directory -Path $output | Out-Null
Copy-Item -LiteralPath (Join-Path $target "$rustTarget/release/$binary.exe") -Destination (Join-Path $output 'mystia-steward-companion-updater.exe')
if ($InstallFixture) {
    & cargo build --release --locked --manifest-path tests/flutter-updater-probe/Cargo.toml --target-dir $target --target $rustTarget --features install-fixture --bin mystia-updater-install-node-driver
    if ($LASTEXITCODE -ne 0) { throw 'Fixed interactive node driver build failed.' }
    Copy-Item -LiteralPath (Join-Path $target "$rustTarget/release/mystia-updater-install-node-driver.exe") -Destination $output
    Copy-Item -LiteralPath (Join-Path $repo 'tests/flutter-updater-probe/Start-Install-Fixture.ps1') -Destination $output
    Copy-Item -LiteralPath (Join-Path $repo 'tests/flutter-updater-probe/Node-Install-Fixture.ps1') -Destination $output
    Copy-Item -LiteralPath (Join-Path $repo 'tests/flutter-updater-probe/INSTALL-FIXTURE.md') -Destination $output
} else {
    Copy-Item -LiteralPath (Join-Path $repo 'tests/flutter-platform-probe/Start-Probe.ps1') -Destination $output
}
Copy-Item -LiteralPath (Join-Path $repo 'tests/flutter-platform-probe/README.md') -Destination $output
Copy-Item -LiteralPath $manifestPath -Destination $output
[long]$expandedBytes = 0
foreach ($file in $files) { $expandedBytes += [long]$file['size'] }
$evidence = [ordered]@{
    schemaVersion = 1
    product = 'mystia-steward-companion'; kind = $(if ($InstallFixture) { 'p0-isolated-install-updater-probe' } else { 'p0-read-only-updater-probe' }); version = $version
    commit = $commit; cleanCheckout = $InstallFixture.IsPresent; flutter = $lock.flutter; rust = (& rustc --version)
    visualStudio = $vs; vcRuntime = $redistVersion; windowsBuildTools = $windowsTools
    rustTarget = $rustTarget; rustLinker = $prepared.identity.linkerPath; rustWindowsSdk = $prepared.identity.windowsSdkVersion
    bootstrapSha256 = (Get-FileHash -LiteralPath (Join-Path $output 'mystia-steward-companion-updater.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
    bootstrapBytes = (Get-Item -LiteralPath (Join-Path $output 'mystia-steward-companion-updater.exe')).Length
    bundleZipBytes = (Get-Item -LiteralPath $archive).Length
    bundleExpandedBytes = $expandedBytes
}
if ($InstallFixture) {
    $evidence.nodeDriverSha256 = (Get-FileHash -LiteralPath (Join-Path $output 'mystia-updater-install-node-driver.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
    $evidence.nodeAdapterSha256 = (Get-FileHash -LiteralPath (Join-Path $output 'Node-Install-Fixture.ps1') -Algorithm SHA256).Hash.ToLowerInvariant()
    $evidence.fixtureScriptSha256 = (Get-FileHash -LiteralPath (Join-Path $output 'Start-Install-Fixture.ps1') -Algorithm SHA256).Hash.ToLowerInvariant()
}
if ($InstallFixture) {
    $afterCommit = (& git rev-parse HEAD)
    $afterStatus = @(& git status --porcelain=v1 --untracked-files=all)
    if ($afterCommit -cne $commit -or $afterStatus.Count -ne 0) {
        $diagnostic = [ordered]@{expectedCommit = $commit; actualCommit = $afterCommit; status = $afterStatus; diffStat = @(& git diff --stat)}
        [IO.File]::WriteAllText((Join-Path $output 'build-source-mismatch.json'), ($diagnostic | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
        throw "Install fixture checkout changed during build: $($afterStatus -join '; '). Source mismatch evidence was preserved."
    }
}
[IO.File]::WriteAllText((Join-Path $output 'build-evidence.json'), ($evidence | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
Write-Host "Updater probe package ($($evidence.kind)): $output"
