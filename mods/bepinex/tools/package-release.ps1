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
$DistDir = Join-Path $DistRoot $PackageDirName
$ZipPath = Join-Path $DistRoot "mystia-steward-companion-bepinex.zip"
$DllPath = Join-Path $OutputDir "MystiaStewardCompanion.BepInEx.dll"

if (-not (Test-Path -LiteralPath $DllPath -PathType Leaf)) {
    Write-Error "Missing built DLL: $DllPath`nRun: dotnet build $RootDir/MystiaStewardCompanion.BepInEx.csproj -c $Configuration"
}

if (Test-Path -LiteralPath $DistDir) {
    Remove-Item -LiteralPath $DistDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $DistDir | Out-Null

Copy-Item -LiteralPath $DllPath -Destination $DistDir
Copy-Item -LiteralPath (Join-Path $RootDir "Data") -Destination (Join-Path $DistDir "Data") -Recurse

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
        break
    }
}

if (Test-Path -LiteralPath $ZipPath) {
    Remove-Item -LiteralPath $ZipPath -Force
}

Compress-Archive -LiteralPath $DistDir -DestinationPath $ZipPath -Force
Write-Host "Package created: $ZipPath"
