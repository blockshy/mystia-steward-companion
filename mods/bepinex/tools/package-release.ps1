#requires -Version 7.0

param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$RootDir = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$RepoRoot = (Resolve-Path (Join-Path $RootDir "../..")).Path
$OutputDir = Join-Path (Join-Path $RootDir "bin") $Configuration
$DistRoot = Join-Path $RootDir "dist"
$PackageDirName = "mystia-steward-companion"
$CompanionStandaloneDirName = "mystia-steward-companion-companion-windows-x64"
$DistDir = Join-Path $DistRoot $PackageDirName
$CompanionStandaloneDir = Join-Path $DistRoot $CompanionStandaloneDirName
$ZipPath = Join-Path $DistRoot "mystia-steward-companion-bepinex.zip"
$CompanionZipPath = Join-Path $DistRoot "$CompanionStandaloneDirName.zip"
$DllPath = Join-Path $OutputDir "MystiaStewardCompanion.BepInEx.dll"

if (-not (Test-Path -LiteralPath $DllPath -PathType Leaf)) {
    Write-Error "Missing built DLL: $DllPath`nRun: dotnet build $RootDir/MystiaStewardCompanion.BepInEx.csproj -c $Configuration"
}

if (Test-Path -LiteralPath $DistDir) {
    Remove-Item -LiteralPath $DistDir -Recurse -Force
}
if (Test-Path -LiteralPath $CompanionStandaloneDir) {
    Remove-Item -LiteralPath $CompanionStandaloneDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $DistDir | Out-Null

Copy-Item -LiteralPath $DllPath -Destination $DistDir

$CompanionCandidates = @(
    "apps/companion/src-tauri/target/release/mystia-steward-companion.exe",
    "apps/companion/src-tauri/target/release/mystia-steward-companion"
)

foreach ($RelativePath in $CompanionCandidates) {
    $CompanionPath = Join-Path $RepoRoot $RelativePath
    if (Test-Path -LiteralPath $CompanionPath -PathType Leaf) {
        $CompanionDir = Join-Path $DistDir "companion"
        New-Item -ItemType Directory -Force -Path $CompanionDir | Out-Null
        Copy-Item -LiteralPath $CompanionPath -Destination (Join-Path $CompanionDir (Split-Path $CompanionPath -Leaf))
        Write-Host "Included companion executable: $CompanionPath"

        if ([System.IO.Path]::GetExtension($CompanionPath).Equals(".exe", [System.StringComparison]::OrdinalIgnoreCase)) {
            New-Item -ItemType Directory -Force -Path $CompanionStandaloneDir | Out-Null
            Copy-Item -LiteralPath $CompanionPath -Destination (Join-Path $CompanionStandaloneDir (Split-Path $CompanionPath -Leaf))
            @"
mystia-steward-companion companion window

This package is only the Windows x64 companion window for a second device.
It is not a BepInEx Mod installer.

Typical LAN setup:
1. On device A, install BepInEx #783 and mystia-steward-companion-bepinex.zip, then start the game.
2. On device A, open the companion window, go to Settings -> Connection, and enable LAN access.
3. Copy the LAN address and Token from device A.
4. On device B, run mystia-steward-companion.exe from this package.
5. Enter the LAN address and Token from device A, then click Connect.

Only use this on a trusted LAN. Do not expose the local API through public port forwarding.
"@ | Set-Content -Encoding UTF8 -LiteralPath (Join-Path $CompanionStandaloneDir "README-remote-connection.txt")
        }
        break
    }
}

$UpdaterCandidates = @(
    "apps/companion/src-tauri/target/release/mystia-steward-companion-updater.exe",
    "apps/companion/src-tauri/target/release/mystia-steward-companion-updater"
)

$UpdaterIncluded = $false
foreach ($RelativePath in $UpdaterCandidates) {
    $UpdaterPath = Join-Path $RepoRoot $RelativePath
    if (Test-Path -LiteralPath $UpdaterPath -PathType Leaf) {
        Copy-Item -LiteralPath $UpdaterPath -Destination (Join-Path $DistDir (Split-Path $UpdaterPath -Leaf))
        Write-Host "Included updater executable: $UpdaterPath"
        $UpdaterIncluded = $true
        break
    }
}

if (-not $UpdaterIncluded) {
    Write-Error "Missing updater executable. Run: cargo build --manifest-path apps/companion/src-tauri/Cargo.toml --release --bin mystia-steward-companion-updater"
}

if (Test-Path -LiteralPath $ZipPath) {
    Remove-Item -LiteralPath $ZipPath -Force
}
if (Test-Path -LiteralPath $CompanionZipPath) {
    Remove-Item -LiteralPath $CompanionZipPath -Force
}

Compress-Archive -LiteralPath $DistDir -DestinationPath $ZipPath -Force
Write-Host "Package created: $ZipPath"

if (Test-Path -LiteralPath $CompanionStandaloneDir -PathType Container) {
    Compress-Archive -LiteralPath $CompanionStandaloneDir -DestinationPath $CompanionZipPath -Force
    Write-Host "Companion package created: $CompanionZipPath"
}
