# Build and package only. Real window checks run separately on the desktop node.
#requires -Version 7.0
param(
    [Parameter(Mandatory = $true)][string]$SdkRoot,
    [Parameter(Mandatory = $true)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path $PSScriptRoot -Parent
Set-Location $repo
if (!$IsWindows -or [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne 'X64') { throw 'This bundle must be built on Windows x64.' }
$lock = Get-Content -LiteralPath (Join-Path $repo 'toolchain.lock.json') -Raw | ConvertFrom-Json
if ($PSVersionTable.PSVersion.ToString() -cne $lock.powershell) { throw 'PowerShell differs from toolchain.lock.json.' }
$node = (Get-Command node -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$nodeVersion = & $node --version
if ($LASTEXITCODE -ne 0 -or $nodeVersion -cne "v$($lock.node)") { throw 'Node differs from toolchain.lock.json.' }

function Get-WindowProbeBuildCommit {
    $sha = & git rev-parse HEAD
    if ($LASTEXITCODE -ne 0 -or $sha -cnotmatch '\A[a-f0-9]{40}\z') { throw 'Build requires a full Git commit SHA.' }
    $changes = @(& git status --porcelain=v1 --untracked-files=all)
    if ($LASTEXITCODE -ne 0 -or $changes.Count -ne 0) { throw 'Build requires a clean committed checkout, including untracked files.' }
    foreach ($name in @('GITHUB_SHA', 'MYSTIA_WINDOW_PROBE_GIT_SHA')) {
        $value = [Environment]::GetEnvironmentVariable($name)
        if ($value -and $value -cne $sha) { throw "$name differs from checkout HEAD." }
    }
    return $sha
}

function Get-WindowProbeFileRecord([string]$Base, [string]$File) {
    $item = Get-Item -LiteralPath $File -Force
    if ($item.PSIsContainer -or $item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Build inputs and payload files must be regular files.' }
    return [ordered]@{
        path = [IO.Path]::GetRelativePath($Base, $File).Replace('\', '/')
        size = $item.Length; sha256 = (Get-FileHash -LiteralPath $File -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

if (![IO.Path]::IsPathFullyQualified($OutputDirectory) -or $OutputDirectory -match '[\x00-\x1f]') { throw 'Output must be an explicit absolute path.' }
$output = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($OutputDirectory))
if (Get-Item -LiteralPath $output -Force -ErrorAction SilentlyContinue) { throw "Output already exists: $output" }
for ($parent = [IO.Path]::GetDirectoryName($output); $parent; $parent = [IO.Path]::GetDirectoryName($parent)) {
    $item = Get-Item -LiteralPath $parent -Force
    if (!$item.PSIsContainer -or $item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Output parents must be existing real directories.' }
}
$commit = Get-WindowProbeBuildCommit
$sourceScopes = @('tests/flutter-window-probe', 'tests/flutter-focus-cooperator/PROTOCOL.md',
    'mods/bepinex/src/Plugin/CompanionControl',
    'mods/bepinex/References/references.lock.json', 'tests/flutter-old-mod-probe/Prepare-OldMod-Probe.Common.psm1',
    'scripts/run-flutter-window-probe.mjs',
    'scripts/build-flutter-window-probe.ps1', 'scripts/flutter-toolchain.mjs', 'scripts/install-locked-flutter.mjs',
    'scripts/install-locked-release-tools.mjs', '.github/workflows/flutter-window-probe.yml', 'toolchain.lock.json', '.nvmrc', '.gitattributes')
$sourcePaths = @(& git ls-files -- @sourceScopes)
if ($LASTEXITCODE -ne 0 -or $sourcePaths.Count -eq 0) { throw 'Cannot enumerate committed build sources.' }
$sources = @($sourcePaths | Sort-Object | ForEach-Object { Get-WindowProbeFileRecord $repo (Join-Path $repo $_) })
& (Join-Path $repo 'tests/flutter-window-probe/Test-Prepare-InputFocus-Probe.ps1')
if (!$?) { throw 'Input/focus fixture preparation tests failed.' }
& (Join-Path $repo 'tests/flutter-window-probe/Test-Prepare-Control-Probe.ps1')
if (!$?) { throw 'Control fixture preparation tests failed.' }
& $node (Join-Path $repo 'scripts/run-flutter-window-probe.mjs') --sdk-root $SdkRoot --build-windows
if ($LASTEXITCODE -ne 0) { throw 'Flutter window probe checks/build failed.' }

$project = Join-Path $repo 'tests/flutter-window-probe'
$pigeonVersions = [regex]::Matches((Get-Content -LiteralPath (Join-Path $project 'pubspec.yaml') -Raw), '(?m)^  pigeon: ([0-9]+\.[0-9]+\.[0-9]+)\r?$')
if ($pigeonVersions.Count -ne 1) { throw 'Pigeon must have one exact version in the verified project manifest.' }
$pigeonVersion = $pigeonVersions[0].Groups[1].Value
$bundle = Join-Path $project 'build/windows/x64/runner/Release'
$entrypoint = 'mystia-steward-companion-window-probe.exe'
foreach ($required in @($entrypoint, 'flutter_windows.dll', 'data/icudtl.dat', 'data/app.so')) {
    if (!(Test-Path -LiteralPath (Join-Path $bundle $required) -PathType Leaf)) { throw "Missing Windows Release bundle file: $required" }
}
if (!(Test-Path -LiteralPath (Join-Path $bundle 'data/flutter_assets') -PathType Container)) { throw 'Missing Flutter assets directory.' }
foreach ($entry in @((Get-Item -LiteralPath $bundle -Force)) + @(Get-ChildItem -LiteralPath $bundle -Recurse -Force)) {
    if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Release bundle links are forbidden.' }
}
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$vsJson = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -format json
if ($LASTEXITCODE -ne 0) { throw 'Visual Studio C++ tools query failed.' }
$instances = @($vsJson -join "`n" | ConvertFrom-Json)
if ($instances.Count -ne 1) { throw 'Expected one selected Visual Studio C++ installation.' }
$vs = $instances[0]
$redistVersion = (Get-Content -LiteralPath (Join-Path $vs.installationPath 'VC/Auxiliary/Build/Microsoft.VCRedistVersion.default.txt') -Raw).Trim()
$crtDirectories = @(Get-ChildItem -LiteralPath (Join-Path $vs.installationPath "VC/Redist/MSVC/$redistVersion/x64") -Directory -Filter 'Microsoft.VC*.CRT')
if ($crtDirectories.Count -ne 1) { throw 'Expected exactly one x64 VC CRT directory.' }
$crt = $crtDirectories[0]
if ($crt.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'VC CRT source must be a real directory.' }
$crtFiles = @(Get-ChildItem -LiteralPath $crt.FullName -File -Filter '*.dll' | Sort-Object Name)
if ($crtFiles.Count -eq 0) { throw 'The app-local VC runtime DLL set is empty.' }
$crtRecords = @($crtFiles | ForEach-Object { Get-WindowProbeFileRecord $crt.FullName $_.FullName })
$bundleRecords = @(Get-ChildItem -LiteralPath $bundle -File -Recurse -Force | Sort-Object FullName |
    ForEach-Object { Get-WindowProbeFileRecord $bundle $_.FullName })

New-Item -ItemType Directory -Path $output -ErrorAction Stop | Out-Null
foreach ($record in $bundleRecords) {
    $destination = Join-Path $output $record.path
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
    [IO.File]::Copy((Join-Path $bundle $record.path), $destination, $false)
    if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ine $record.sha256) { throw 'Release bundle changed while copying.' }
}
foreach ($record in $crtRecords) {
    $destination = Join-Path $output $record.path
    if (!(Test-Path -LiteralPath $destination)) { [IO.File]::Copy((Join-Path $crt.FullName $record.path), $destination, $false) }
    if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ine $record.sha256) { throw 'VC runtime changed or conflicts with a bundled DLL.' }
}
[IO.File]::Copy((Join-Path $project 'README.md'), (Join-Path $output 'README.md'), $false)
# Preserve the source-relative preparation layout under support. These scripts
# prepare a fresh game copy over SSH; the GUI still runs only through the node.
foreach ($relative in @('tests/flutter-window-probe/Prepare-InputFocus-Probe.ps1',
    'tests/flutter-window-probe/Prepare-Control-Probe.ps1',
    'tests/flutter-old-mod-probe/Prepare-OldMod-Probe.Common.psm1',
    'mods/bepinex/References/references.lock.json', 'toolchain.lock.json')) {
    $source = Join-Path $repo $relative
    $destination = Join-Path $output "support/$relative"
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
    [IO.File]::Copy($source, $destination, $false)
    if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -cne
        (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash) { throw 'Preparation support changed while copying.' }
}
# The packaged EXE must pass the Windows loader before desktop testing. This
# fixed role checks imports/activation context without starting Flutter or GUI.
$loaderInfo = [Diagnostics.ProcessStartInfo]::new((Join-Path $output $entrypoint))
$loaderInfo.UseShellExecute = $false
$loaderInfo.CreateNoWindow = $true
$loaderInfo.WorkingDirectory = $output
$loaderInfo.ArgumentList.Add('--loader-check')
$loaderInfo.ArgumentList.Add($commit)
$loader = [Diagnostics.Process]::new()
$loader.StartInfo = $loaderInfo
try {
    if (!$loader.Start()) { throw 'Cannot start the packaged loader check.' }
    if (!$loader.WaitForExit(10000)) {
        $loader.Kill($true)
        throw 'Packaged loader check timed out.'
    }
    if ($loader.ExitCode -ne 0) { throw "Packaged loader check failed: $($loader.ExitCode)" }
} finally { $loader.Dispose() }
if ((Get-WindowProbeBuildCommit) -cne $commit) { throw 'Build commit changed during verification.' }
$after = @($sourcePaths | Sort-Object | ForEach-Object { Get-WindowProbeFileRecord $repo (Join-Path $repo $_) })
if (($sources | ConvertTo-Json -Depth 8 -Compress) -cne ($after | ConvertTo-Json -Depth 8 -Compress)) { throw 'Build source bytes changed during verification.' }
$files = @(Get-ChildItem -LiteralPath $output -File -Recurse -Force | Sort-Object FullName |
    ForEach-Object { Get-WindowProbeFileRecord $output $_.FullName })
$evidence = [ordered]@{
    schemaVersion = 1; product = 'mystia-steward-companion'; kind = 'flutter-window-probe-bundle'
    commit = $commit; dartBuildGitSha = $commit; nativeBuildGitSha = $commit; cleanCheckout = $true
    builtUtc = [DateTime]::UtcNow.ToString('o'); target = 'windows-x64'; entrypoint = $entrypoint
    tools = [ordered]@{ node = $nodeVersion; powershell = $PSVersionTable.PSVersion.ToString(); flutter = $lock.flutter; dart = $lock.flutter.dartVersion; pigeon = $pigeonVersion; visualStudio = $vs.installationVersion; vcRuntime = $redistVersion }
    vcRuntimeSource = $crt.FullName; vcRuntimeFiles = $crtRecords
    checks = [ordered]@{ lockedDependencies = 'passed'; pigeon = 'passed'; format = 'passed'; analyze = 'passed'; dartTests = 'passed'; fixturePreparation = 'passed'; windowsRelease = 'passed'; packagedLoader = 'passed'; desktopRuntime = 'not-run' }
    sourceFiles = $sources; files = $files
    fileHashScope = 'All delivered files except this build-evidence.json, which cannot contain its own hash.'
}
[IO.File]::WriteAllText((Join-Path $output 'build-evidence.json'), ($evidence | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
Write-Host "Windows window-probe bundle: $output"
Write-Host 'Compilation and Dart tests passed. Physical desktop behavior still requires its own probe report.'
