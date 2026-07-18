#requires -Version 7.0

param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$RootDir = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$RepoRoot = (Resolve-Path (Join-Path $RootDir "../..")).Path
$OutputDir = Join-Path (Join-Path $RootDir "bin") $Configuration
$DistRoot = Join-Path $RootDir "dist"
$PackageDirName = "mystia-steward-companion"
$ZipName = "mystia-steward-companion-bepinex.zip"
$CompanionStandaloneExeName = "mystia-steward-companion-companion-windows-x64.exe"
$DllPath = Join-Path $OutputDir "MystiaStewardCompanion.BepInEx.dll"
$TransactionId = [Guid]::NewGuid().ToString("N")
$StageRoot = "$DistRoot.staging-$TransactionId"
$BackupRoot = "$DistRoot.backup-$TransactionId"

function Assert-InputFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Description
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Missing $Description`: $Path"
    }

    $Item = Get-Item -LiteralPath $Path
    if ($Item.Length -le 0) {
        throw "$Description is empty: $Path"
    }
}

function Find-InputFile {
    param(
        [Parameter(Mandatory = $true)][string[]]$RelativeCandidates,
        [Parameter(Mandatory = $true)][string]$Description,
        [Parameter(Mandatory = $true)][string]$BuildHint
    )

    foreach ($RelativePath in $RelativeCandidates) {
        $CandidatePath = Join-Path $RepoRoot $RelativePath
        if (Test-Path -LiteralPath $CandidatePath -PathType Leaf) {
            Assert-InputFile -Path $CandidatePath -Description $Description
            return (Resolve-Path -LiteralPath $CandidatePath).Path
        }
    }

    throw "Missing $Description. $BuildHint"
}

function Assert-ManagedSwapPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    $FullPath = [System.IO.Path]::GetFullPath($Path)
    $ParentPath = [System.IO.Directory]::GetParent($FullPath).FullName
    $ExpectedParent = [System.IO.Path]::GetFullPath($RootDir)
    $LeafName = [System.IO.Path]::GetFileName($FullPath)

    if (-not $ParentPath.Equals($ExpectedParent, [System.StringComparison]::OrdinalIgnoreCase) -or
        $LeafName -notmatch '^dist\.(staging|backup)-[0-9a-f]{32}$') {
        throw "Refusing to manage unexpected release path: $FullPath"
    }
}

function Remove-ManagedSwapDirectory {
    param([Parameter(Mandatory = $true)][string]$Path)

    Assert-ManagedSwapPath -Path $Path
    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
}

function Copy-ValidatedFile {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    Copy-Item -LiteralPath $Source -Destination $Destination
    $SourceLength = (Get-Item -LiteralPath $Source).Length
    $DestinationLength = (Get-Item -LiteralPath $Destination).Length
    if ($SourceLength -ne $DestinationLength) {
        throw "Copied file size mismatch: $Source -> $Destination"
    }
}

function Assert-ZipContents {
    param(
        [Parameter(Mandatory = $true)][string]$ArchivePath,
        [Parameter(Mandatory = $true)][string[]]$ExpectedEntries
    )

    if (-not (Test-Path -LiteralPath $ArchivePath -PathType Leaf) -or
        (Get-Item -LiteralPath $ArchivePath).Length -le 0) {
        throw "Release archive was not created: $ArchivePath"
    }

    $Archive = [System.IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $EntryNames = [System.Collections.Generic.HashSet[string]]::new(
            [System.StringComparer]::OrdinalIgnoreCase
        )
        foreach ($Entry in $Archive.Entries) {
            [void]$EntryNames.Add($Entry.FullName.Replace("\", "/"))
        }

        foreach ($ExpectedEntry in $ExpectedEntries) {
            if (-not $EntryNames.Contains($ExpectedEntry)) {
                throw "Release archive is missing expected entry: $ExpectedEntry"
            }
        }
    }
    finally {
        $Archive.Dispose()
    }
}

function Assert-NoPendingReleaseTransactions {
    $PendingPaths = @(
        Get-ChildItem -LiteralPath $RootDir -Force |
            Where-Object { $_.Name -match '^dist\.(staging|backup)-' } |
            Sort-Object -Property Name
    )
    if ($PendingPaths.Count -eq 0) {
        return
    }

    $Details = ($PendingPaths | ForEach-Object { "  - $($_.FullName)" }) -join [Environment]::NewLine
    throw @(
        "A previous release packaging transaction was not completed.",
        "Inspect and remove or restore these paths before packaging again:",
        $Details
    ) -join [Environment]::NewLine
}

Assert-NoPendingReleaseTransactions
Assert-InputFile -Path $DllPath -Description "built Mod DLL"

$CompanionPath = Find-InputFile `
    -RelativeCandidates @(
        "apps/companion/src-tauri/target/release/mystia-steward-companion.exe",
        "apps/companion/src-tauri/target/release/mystia-steward-companion"
    ) `
    -Description "companion executable" `
    -BuildHint "Run: pnpm tauri:build"

$UpdaterPath = Find-InputFile `
    -RelativeCandidates @(
        "apps/companion/src-tauri/target/release/mystia-steward-companion-updater.exe",
        "apps/companion/src-tauri/target/release/mystia-steward-companion-updater"
    ) `
    -Description "updater executable" `
    -BuildHint "Run: cargo build --manifest-path apps/companion/src-tauri/Cargo.toml --release --bin mystia-steward-companion-updater"

if ($null -eq (Get-Command "Compress-Archive" -ErrorAction SilentlyContinue)) {
    throw "Compress-Archive is unavailable. Run this script with PowerShell 7 or newer."
}

Add-Type -AssemblyName System.IO.Compression.FileSystem

if (Test-Path -LiteralPath $DistRoot) {
    $DistItem = Get-Item -LiteralPath $DistRoot -Force
    if (-not $DistItem.PSIsContainer -or ($DistItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
        throw "Release dist must be a real directory, not a file, symlink, or junction: $DistRoot"
    }
}

Assert-ManagedSwapPath -Path $StageRoot
Assert-ManagedSwapPath -Path $BackupRoot

try {
    $StagePackageDir = Join-Path $StageRoot $PackageDirName
    $StageZipPath = Join-Path $StageRoot $ZipName
    $CompanionName = Split-Path $CompanionPath -Leaf
    $UpdaterName = Split-Path $UpdaterPath -Leaf

    New-Item -ItemType Directory -Path (Join-Path $StagePackageDir "companion") | Out-Null
    Copy-ValidatedFile -Source $DllPath -Destination (Join-Path $StagePackageDir (Split-Path $DllPath -Leaf))
    Copy-ValidatedFile -Source $CompanionPath -Destination (Join-Path (Join-Path $StagePackageDir "companion") $CompanionName)
    Copy-ValidatedFile -Source $UpdaterPath -Destination (Join-Path $StagePackageDir $UpdaterName)

    $HasStandaloneCompanion = [System.IO.Path]::GetExtension($CompanionPath).Equals(
        ".exe",
        [System.StringComparison]::OrdinalIgnoreCase
    )
    if ($HasStandaloneCompanion) {
        Copy-ValidatedFile `
            -Source $CompanionPath `
            -Destination (Join-Path $StageRoot $CompanionStandaloneExeName)
    }

    Compress-Archive -LiteralPath $StagePackageDir -DestinationPath $StageZipPath
    Assert-ZipContents `
        -ArchivePath $StageZipPath `
        -ExpectedEntries @(
            "$PackageDirName/$(Split-Path $DllPath -Leaf)",
            "$PackageDirName/companion/$CompanionName",
            "$PackageDirName/$UpdaterName"
        )

    if (Test-Path -LiteralPath $DistRoot) {
        Move-Item -LiteralPath $DistRoot -Destination $BackupRoot
    }

    try {
        Move-Item -LiteralPath $StageRoot -Destination $DistRoot
    }
    catch {
        if (-not (Test-Path -LiteralPath $DistRoot) -and
            (Test-Path -LiteralPath $BackupRoot)) {
            Move-Item -LiteralPath $BackupRoot -Destination $DistRoot
        }
        throw
    }

    if (Test-Path -LiteralPath $BackupRoot) {
        try {
            Remove-ManagedSwapDirectory -Path $BackupRoot
        }
        catch {
            throw "The new release is active, but the previous dist backup could not be removed: $BackupRoot. $($_.Exception.Message)"
        }
    }

    Write-Host "Included companion executable: $CompanionPath"
    Write-Host "Included updater executable: $UpdaterPath"
    if ($HasStandaloneCompanion) {
        Write-Host "Companion executable created: $(Join-Path $DistRoot $CompanionStandaloneExeName)"
    }
    Write-Host "Package created: $(Join-Path $DistRoot $ZipName)"
}
finally {
    if (Test-Path -LiteralPath $StageRoot) {
        try {
            Remove-ManagedSwapDirectory -Path $StageRoot
        }
        catch {
            Write-Warning "Failed to remove release staging directory: $StageRoot"
        }
    }

    if (-not (Test-Path -LiteralPath $DistRoot) -and
        (Test-Path -LiteralPath $BackupRoot)) {
        Move-Item -LiteralPath $BackupRoot -Destination $DistRoot
    }
}
