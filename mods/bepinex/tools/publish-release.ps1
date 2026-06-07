#requires -Version 7.0

param(
    [Parameter(Mandatory = $true)]
    [string]$Tag,
    [string]$Title = "",
    [string]$Notes = "",
    [switch]$Prerelease,
    [switch]$SkipBuild,
    [switch]$Clobber,
    [string]$ReferenceDir = "",
    [string]$Repo = "blockshy/mystia-steward-companion"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$ToolDir = $PSScriptRoot
$RootDir = (Resolve-Path (Join-Path $ToolDir "..")).Path
$RepoRoot = (Resolve-Path (Join-Path $RootDir "../..")).Path
$BuildScript = Join-Path $ToolDir "build-release.ps1"
$DistRoot = Join-Path $RootDir "dist"
$ModZip = Join-Path $DistRoot "mystia-steward-companion-bepinex.zip"
$ChecksumPath = Join-Path $DistRoot "checksums.txt"

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    Write-Host "    $FilePath $($Arguments -join ' ')"
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code $LASTEXITCODE`: $FilePath $($Arguments -join ' ')"
    }
}

function Get-GhCommand {
    $Gh = Get-Command "gh" -ErrorAction SilentlyContinue
    if ($null -eq $Gh) {
        throw "GitHub CLI was not found. Install gh and login with: gh auth login"
    }

    return $Gh.Source
}

function Get-PwshCommand {
    $Pwsh = Get-Command "pwsh" -ErrorAction SilentlyContinue
    if ($null -eq $Pwsh) {
        throw "PowerShell 7 was not found. Install PowerShell 7 and run this script with: pwsh -ExecutionPolicy Bypass -File $PSCommandPath"
    }

    return $Pwsh.Source
}

function Test-GhReleaseExists {
    param(
        [Parameter(Mandatory = $true)][string]$Gh,
        [Parameter(Mandatory = $true)][string]$Tag,
        [Parameter(Mandatory = $true)][string]$Repo
    )

    try {
        $Output = & $Gh release view $Tag --repo $Repo --json tagName 2>$null
        return $LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($Output)
    }
    catch {
        return $false
    }
}

Push-Location $RepoRoot
try {
    if (-not $SkipBuild) {
        [string[]]$BuildArgs = @(
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            $BuildScript
        )

        if (-not [string]::IsNullOrWhiteSpace($ReferenceDir)) {
            $BuildArgs += "-ReferenceDir"
            $BuildArgs += $ReferenceDir
        }

        $Pwsh = Get-PwshCommand
        Invoke-Checked -FilePath $Pwsh -Arguments $BuildArgs
    }

    if (-not (Test-Path -LiteralPath $ModZip -PathType Leaf)) {
        throw "Missing Mod package: $ModZip"
    }

    $AssetPaths = @($ModZip)

    New-Item -ItemType Directory -Force -Path $DistRoot | Out-Null
    $ChecksumLines = foreach ($Asset in $AssetPaths) {
        $Hash = Get-FileHash -Algorithm SHA256 -LiteralPath $Asset
        "$($Hash.Hash.ToLowerInvariant())  $($Hash.Path)"
    }
    $ChecksumLines | Set-Content -Encoding UTF8 -LiteralPath $ChecksumPath
    $AssetPaths += $ChecksumPath

    $Gh = Get-GhCommand
    $ReleaseExists = Test-GhReleaseExists -Gh $Gh -Tag $Tag -Repo $Repo

    if ($ReleaseExists) {
        [string[]]$UploadArgs = @("release", "upload", $Tag)
        foreach ($AssetPath in $AssetPaths) {
            $UploadArgs += $AssetPath
        }
        $UploadArgs += "--repo"
        $UploadArgs += $Repo
        if ($Clobber) {
            $UploadArgs += "--clobber"
        }

        Invoke-Checked -FilePath $Gh -Arguments $UploadArgs
    }
    else {
        if ([string]::IsNullOrWhiteSpace($Title)) {
            $Title = $Tag
        }
        if ([string]::IsNullOrWhiteSpace($Notes)) {
            $Notes = "Built locally and uploaded with GitHub CLI."
        }

        [string[]]$CreateArgs = @("release", "create", $Tag)
        foreach ($AssetPath in $AssetPaths) {
            $CreateArgs += $AssetPath
        }
        $CreateArgs += "--repo"
        $CreateArgs += $Repo
        $CreateArgs += "--title"
        $CreateArgs += $Title
        $CreateArgs += "--notes"
        $CreateArgs += $Notes

        if ($Prerelease) {
            $CreateArgs += "--prerelease"
        }

        Invoke-Checked -FilePath $Gh -Arguments $CreateArgs
    }
}
finally {
    Pop-Location
}

Write-Host ""
Write-Host "Release published: $Tag" -ForegroundColor Green
