param(
    [string]$Configuration = "Release",
    [switch]$SkipInstall,
    [switch]$SkipPreflight,
    [switch]$SkipDataSync,
    [Alias("SkipWebBuild")]
    [switch]$SkipFrontendBuild,
    [switch]$SkipTauriBuild,
    [switch]$SkipPackage,
    [switch]$NoFrozenLockfile
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$ToolDir = $PSScriptRoot
$RootDir = (Resolve-Path (Join-Path $ToolDir "..")).Path
$RepoRoot = (Resolve-Path (Join-Path $RootDir "../..")).Path
$ProjectPath = Join-Path $RootDir "MystiaSteward.BepInEx.csproj"
$PreflightScript = Join-Path $ToolDir "preflight.ps1"
$PackageScript = Join-Path $ToolDir "package-release.ps1"

function Write-Step {
    param([Parameter(Mandatory = $true)][string]$Title)

    Write-Host ""
    Write-Host "==> $Title" -ForegroundColor Cyan
}

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)][string]$Title,
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    Write-Step $Title
    Write-Host "    $FilePath $($Arguments -join ' ')"
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code $LASTEXITCODE`: $FilePath $($Arguments -join ' ')"
    }
}

function Get-PnpmCommand {
    $Corepack = Get-Command "corepack" -ErrorAction SilentlyContinue
    if ($null -ne $Corepack) {
        return @{
            FilePath = $Corepack.Source
            Prefix = @("pnpm")
        }
    }

    $Pnpm = Get-Command "pnpm" -ErrorAction SilentlyContinue
    if ($null -ne $Pnpm) {
        return @{
            FilePath = $Pnpm.Source
            Prefix = @()
        }
    }

    throw "Neither corepack nor pnpm was found. Install Node.js 20+ and run: corepack enable"
}

function Invoke-Pnpm {
    param(
        [Parameter(Mandatory = $true)][string]$Title,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    $Command = Get-PnpmCommand
    Invoke-Checked -Title $Title -FilePath $Command.FilePath -Arguments @($Command.Prefix + $Arguments)
}

function Sync-ModData {
    Write-Step "Sync Mod data"

    $SourceDir = Join-Path $RepoRoot "apps/companion/src/data"
    $TargetDir = Join-Path $RootDir "Data"
    $Files = @(
        "recipes.json",
        "beverages.json",
        "ingredients.json",
        "customer_normal.json",
        "customer_rare.json",
        "food-tag-id-map.json"
    )

    New-Item -ItemType Directory -Force -Path $TargetDir | Out-Null
    foreach ($File in $Files) {
        $Source = Join-Path $SourceDir $File
        $Target = Join-Path $TargetDir $File
        if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) {
            throw "Missing data file: $Source"
        }

        Copy-Item -LiteralPath $Source -Destination $Target -Force
        Write-Host "    $File"
    }
}

Push-Location $RepoRoot
try {
    if (-not $SkipInstall) {
        $InstallArgs = @("install")
        if (-not $NoFrozenLockfile) {
            $InstallArgs += "--frozen-lockfile"
        }

        Invoke-Pnpm -Title "Install companion frontend dependencies" -Arguments $InstallArgs
    }

    if (-not $SkipPreflight) {
        Write-Step "Run Mod preflight"
        & $PreflightScript
    }

    if (-not $SkipDataSync) {
        Sync-ModData
    }

    if (-not $SkipFrontendBuild) {
        Invoke-Pnpm -Title "Build companion frontend" -Arguments @("build")
    }

    if (-not $SkipTauriBuild) {
        Invoke-Pnpm -Title "Build companion window" -Arguments @("tauri:build")
    }

    $Dotnet = Get-Command "dotnet" -ErrorAction SilentlyContinue
    if ($null -eq $Dotnet) {
        throw "dotnet was not found. Install .NET 6 SDK or newer."
    }

    Invoke-Checked `
        -Title "Build BepInEx plugin" `
        -FilePath $Dotnet.Source `
        -Arguments @("build", $ProjectPath, "-c", $Configuration)

    if (-not $SkipPackage) {
        Write-Step "Create release package"
        & $PackageScript -Configuration $Configuration
    }
}
finally {
    Pop-Location
}

Write-Host ""
Write-Host "Build completed." -ForegroundColor Green
