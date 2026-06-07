param(
    [Parameter(Mandatory = $true)]
    [string]$Tag,
    [string]$Title = "",
    [string]$Notes = "",
    [switch]$Prerelease,
    [switch]$SkipBuild,
    [switch]$Clobber,
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
$InstallerDir = Join-Path $RepoRoot "apps\companion\src-tauri\target\release\bundle\nsis"
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

Push-Location $RepoRoot
try {
    if (-not $SkipBuild) {
        Invoke-Checked -FilePath "powershell" -Arguments @(
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            $BuildScript
        )
    }

    if (-not (Test-Path -LiteralPath $ModZip -PathType Leaf)) {
        throw "Missing Mod package: $ModZip"
    }

    if (-not (Test-Path -LiteralPath $InstallerDir -PathType Container)) {
        throw "Missing Tauri NSIS installer directory: $InstallerDir"
    }

    $InstallerFiles = @(Get-ChildItem -LiteralPath $InstallerDir -Filter "*.exe" -File)
    if ($InstallerFiles.Count -eq 0) {
        throw "Missing Tauri NSIS installer (*.exe) in: $InstallerDir"
    }

    $AssetPaths = @($ModZip) + @($InstallerFiles | ForEach-Object { $_.FullName })

    New-Item -ItemType Directory -Force -Path $DistRoot | Out-Null
    $ChecksumLines = foreach ($Asset in $AssetPaths) {
        $Hash = Get-FileHash -Algorithm SHA256 -LiteralPath $Asset
        "$($Hash.Hash.ToLowerInvariant())  $($Hash.Path)"
    }
    $ChecksumLines | Set-Content -Encoding UTF8 -LiteralPath $ChecksumPath
    $AssetPaths += $ChecksumPath

    $Gh = Get-GhCommand
    $ExistingRelease = & $Gh release view $Tag --repo $Repo --json tagName 2>$null
    $ReleaseExists = $LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($ExistingRelease)

    if ($ReleaseExists) {
        $UploadArgs = @("release", "upload", $Tag) + $AssetPaths + @("--repo", $Repo)
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
            $Notes = "本版本由本机构建后通过 GitHub CLI 上传。"
        }

        $CreateArgs = @(
            "release",
            "create",
            $Tag
        ) + $AssetPaths + @(
            "--repo",
            $Repo,
            "--title",
            $Title,
            "--notes",
            $Notes
        )

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
