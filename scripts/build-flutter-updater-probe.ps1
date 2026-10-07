param(
    [Parameter(Mandatory = $true)][string]$SdkRoot,
    [Parameter(Mandatory = $true)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path $PSScriptRoot -Parent
Set-Location $repo
$lock = Get-Content -LiteralPath (Join-Path $repo 'toolchain.lock.json') -Raw | ConvertFrom-Json
if ($PSVersionTable.PSVersion.ToString() -ne $lock.powershell) { throw 'PowerShell version differs from toolchain.lock.json.' }
if ((& node --version) -ne "v$($lock.node)") { throw 'Node version differs from toolchain.lock.json.' }
if ((& rustc --version) -notmatch "^rustc $([regex]::Escape($lock.rust)) ") { throw 'Rust version differs from toolchain.lock.json.' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw "Output already exists: $output" }
& node scripts/run-flutter-platform-probe.mjs --sdk-root $SdkRoot --build-windows
if ($LASTEXITCODE -ne 0) { throw 'Flutter probe checks/build failed.' }

$bundle = Join-Path $repo 'tests/flutter-platform-probe/build/windows/x64/runner/Release'
if (!(Test-Path -LiteralPath (Join-Path $bundle 'mystia-steward-companion-updater-ui.exe'))) { throw 'Missing Flutter runner.' }
foreach ($entry in @((Get-Item -LiteralPath $bundle)) + @(Get-ChildItem -LiteralPath $bundle -Force -Recurse)) {
    if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Bundle links are forbidden.' }
}
# Ship the redistributable runtime app-locally, as required on machines without VS.
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$vs -or $LASTEXITCODE -ne 0) { throw 'Visual Studio C++ tools were not found.' }
$redistVersionFile = Join-Path $vs 'VC/Auxiliary/Build/Microsoft.VCRedistVersion.default.txt'
$redistVersion = (Get-Content -LiteralPath $redistVersionFile -Raw).Trim()
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
$env:RUSTFLAGS = '-C target-feature=+crt-static'
$target = Join-Path $work 'target'
& cargo test --locked --manifest-path tests/flutter-updater-probe/Cargo.toml --target-dir $target
if ($LASTEXITCODE -ne 0) { throw 'Updater probe tests failed.' }
& cargo build --release --locked --manifest-path tests/flutter-updater-probe/Cargo.toml --target-dir $target --bin mystia-steward-companion-updater
if ($LASTEXITCODE -ne 0) { throw 'Updater probe build failed.' }
New-Item -ItemType Directory -Path $output | Out-Null
Copy-Item -LiteralPath (Join-Path $target 'release/mystia-steward-companion-updater.exe') -Destination $output
Copy-Item -LiteralPath (Join-Path $repo 'tests/flutter-platform-probe/Start-Probe.ps1') -Destination $output
Copy-Item -LiteralPath (Join-Path $repo 'tests/flutter-platform-probe/README.md') -Destination $output
Copy-Item -LiteralPath $manifestPath -Destination $output
[long]$expandedBytes = 0
foreach ($file in $files) { $expandedBytes += [long]$file['size'] }
$evidence = [ordered]@{
    product = 'mystia-steward-companion'; kind = 'p0-read-only-updater-probe'; version = $version
    commit = (& git rev-parse HEAD); flutter = $lock.flutter; rust = (& rustc --version)
    visualStudio = $vs; vcRuntime = $redistVersion
    bootstrapSha256 = (Get-FileHash -LiteralPath (Join-Path $output 'mystia-steward-companion-updater.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
    bootstrapBytes = (Get-Item -LiteralPath (Join-Path $output 'mystia-steward-companion-updater.exe')).Length
    bundleZipBytes = (Get-Item -LiteralPath $archive).Length
    bundleExpandedBytes = $expandedBytes
}
[IO.File]::WriteAllText((Join-Path $output 'build-evidence.json'), ($evidence | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
Write-Host "Read-only probe package: $output"
