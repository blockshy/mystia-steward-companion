#requires -Version 7.0

<#
.SYNOPSIS
    构建并发布 mystia-steward-companion 的 GitHub Release 资产。

.DESCRIPTION
    该脚本用于正式发布阶段：校验项目版本与 tag 一致、按需调用 build-release.ps1、
    生成 update-manifest.json，并通过 GitHub CLI 创建或上传 Release 资产。
    脚本不会自动修改版本号，也不会推送 dev/main 分支。
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$Tag,
    [string]$Title = "",
    [string]$Notes = "",
    [switch]$Prerelease,
    [switch]$SkipBuild,
    [switch]$SkipVersionCheck,
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
$ManifestPath = Join-Path $DistRoot "update-manifest.json"

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

function Get-VersionFromTag {
    param([Parameter(Mandatory = $true)][string]$Tag)

    $Version = $Tag.Trim()
    if ($Version.StartsWith("v", [StringComparison]::OrdinalIgnoreCase)) {
        $Version = $Version.Substring(1)
    }

    if ($Version -notmatch '^\d+\.\d+\.\d+([\-+][0-9A-Za-z.-]+)?$') {
        throw "Release tag must be SemVer-like, for example v1.0.1. Actual: $Tag"
    }

    return $Version
}

function Get-FirstMatch {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Pattern,
        [int]$Group = 1
    )

    $Content = Get-Content -Raw -LiteralPath $Path
    $Match = [regex]::Match($Content, $Pattern, [System.Text.RegularExpressions.RegexOptions]::Multiline)
    if (-not $Match.Success) {
        throw "Version pattern not found in $Path"
    }

    return $Match.Groups[$Group].Value
}

function Assert-ProjectVersion {
    <#
    .SYNOPSIS
        校验所有发布版本来源是否与目标 tag 一致。

    .DESCRIPTION
        自动更新和用户可见版本依赖 package.json、Tauri、Cargo 和 PluginVersion 同步。
        任一来源不一致都会停止发布，避免生成 manifest 后出现版本识别错误。
    #>
    param([Parameter(Mandatory = $true)][string]$ExpectedVersion)

    $PackageJson = Join-Path $RepoRoot "package.json"
    $TauriConfig = Join-Path $RepoRoot "apps/companion/src-tauri/tauri.conf.json"
    $CargoToml = Join-Path $RepoRoot "apps/companion/src-tauri/Cargo.toml"
    $CargoLock = Join-Path $RepoRoot "apps/companion/src-tauri/Cargo.lock"
    $PluginSource = Join-Path $RepoRoot "mods/bepinex/src/Plugin/MystiaStewardCompanionPlugin.cs"

    $VersionSources = @(
        @{
            Name = "package.json"
            Path = $PackageJson
            Version = (Get-FirstMatch -Path $PackageJson -Pattern '"version"\s*:\s*"([^"]+)"')
        },
        @{
            Name = "tauri.conf.json"
            Path = $TauriConfig
            Version = (Get-FirstMatch -Path $TauriConfig -Pattern '"version"\s*:\s*"([^"]+)"')
        },
        @{
            Name = "Cargo.toml"
            Path = $CargoToml
            Version = (Get-FirstMatch -Path $CargoToml -Pattern '^version = "([^"]+)"')
        },
        @{
            Name = "Cargo.lock"
            Path = $CargoLock
            Version = (Get-FirstMatch -Path $CargoLock -Pattern '(?s)name = "mystia-steward-companion"\s+version = "([^"]+)"')
        },
        @{
            Name = "PluginVersion"
            Path = $PluginSource
            Version = (Get-FirstMatch -Path $PluginSource -Pattern 'public const string PluginVersion = "([^"]+)";')
        }
    )

    $Mismatched = @($VersionSources | Where-Object { $_.Version -ne $ExpectedVersion })
    if ($Mismatched.Count -gt 0) {
        $Details = ($Mismatched | ForEach-Object { "  - $($_.Name): $($_.Version) ($($_.Path))" }) -join [Environment]::NewLine
        $SetVersionScript = Join-Path $ToolDir "set-version.ps1"
        throw @(
            "Project version does not match release tag $Tag.",
            "Expected version: $ExpectedVersion",
            "Mismatched files:",
            $Details,
            "Run before publishing:",
            "  pwsh -ExecutionPolicy Bypass -File $SetVersionScript -Version $ExpectedVersion",
            "Then commit and push the version bump."
        ) -join [Environment]::NewLine
    }
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
    $ExpectedVersion = Get-VersionFromTag -Tag $Tag
    if (-not $SkipVersionCheck) {
        Assert-ProjectVersion -ExpectedVersion $ExpectedVersion
    }

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

    New-Item -ItemType Directory -Force -Path $DistRoot | Out-Null
    $ModZipHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $ModZip).Hash.ToLowerInvariant()
    $ModZipItem = Get-Item -LiteralPath $ModZip
    $Manifest = [ordered]@{
        schemaVersion = 1
        version = $ExpectedVersion
        tag = $Tag
        channel = if ($Prerelease) { "prerelease" } else { "stable" }
        packageAsset = (Split-Path $ModZip -Leaf)
        packageSha256 = $ModZipHash
        packageSize = $ModZipItem.Length
        releaseUrl = "https://github.com/$Repo/releases/tag/$Tag"
        publishedAtUtc = [DateTime]::UtcNow.ToString("O")
    }
    $Manifest | ConvertTo-Json -Depth 4 | Set-Content -Encoding UTF8 -LiteralPath $ManifestPath

    $AssetPaths = @($ModZip, $ManifestPath)

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
