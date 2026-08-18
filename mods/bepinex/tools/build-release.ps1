#requires -Version 7.0

<#
.SYNOPSIS
    构建本地发布包所需的前端、Tauri 窗口、独立 updater、BepInEx 插件和可选 Android APK。

.DESCRIPTION
    该脚本是 Windows 本地发布流程的构建入口。它会先检查 Unity/BepInEx 引用 DLL，
    再按参数选择安装依赖、运行预检、构建前端/Tauri、构建插件并调用 package-release.ps1 打包。
    如传入 -BuildAndroidApk，会额外调用签名 APK 构建脚本，并把 APK 放在 dist 根目录。
    脚本不会创建 Git tag 或 GitHub Release；上传发布由 publish-release.ps1 负责。
#>
param(
    [string]$Configuration = "Release",
    [switch]$SkipInstall,
    [Alias("SkipWebBuild")]
    [switch]$SkipFrontendBuild,
    [switch]$SkipTauriBuild,
    [switch]$SkipPackage,
    [switch]$BuildAndroidApk,
    [switch]$NoFrozenLockfile,
    [ValidateRange(1, 1024)]
    [int]$BuildCacheLimitGiB = 12,
    [ValidateRange(1, 1024)]
    [int]$BuildCacheTargetGiB = 8,
    [switch]$SkipBuildCacheCleanup,
    [string]$ReferenceDir = $env:MYSTIA_REFERENCE_DIR
)

$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest

$ToolDir = $PSScriptRoot
$RootDir = (Resolve-Path (Join-Path $ToolDir "..")).Path
$RepoRoot = (Resolve-Path (Join-Path $RootDir "../..")).Path
$ProjectPath = Join-Path $RootDir "MystiaStewardCompanion.BepInEx.csproj"
$PreflightScript = Join-Path $ToolDir "preflight.ps1"
$PackageScript = Join-Path $ToolDir "package-release.ps1"
$BuildArtifactManager = Join-Path $RepoRoot "scripts/manage-build-artifacts.mjs"
$ToolchainCheckScript = Join-Path $RepoRoot "scripts/check-build-toolchain.mjs"
$EffectiveReferenceDir = if ([string]::IsNullOrWhiteSpace($ReferenceDir)) {
    Join-Path $RootDir "References"
} else {
    (Resolve-Path -LiteralPath $ReferenceDir).Path
}

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
    <#
    .SYNOPSIS
        取得仓库唯一允许的 Corepack pnpm 入口。

    .DESCRIPTION
        只通过 Corepack 调用 pnpm，保证包管理器版本和完整性哈希遵循 packageManager 字段。
        不接受全局 pnpm 回退。
    #>
    $Corepack = Get-Command "corepack" -ErrorAction SilentlyContinue
    if ($null -eq $Corepack) {
        throw "corepack was not found. Install the exact Node.js and Corepack versions declared in toolchain.lock.json, then run: corepack enable"
    }

    return @{
        FilePath = $Corepack.Source
        Prefix = @("pnpm")
    }
}

function Invoke-Pnpm {
    param(
        [Parameter(Mandatory = $true)][string]$Title,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    $Command = Get-PnpmCommand
    Invoke-Checked -Title $Title -FilePath $Command.FilePath -Arguments @($Command.Prefix + $Arguments)
}

function Invoke-BuildCachePrune {
    param([Parameter(Mandatory = $true)][string]$Title)

    if (-not (Test-Path -LiteralPath $BuildArtifactManager -PathType Leaf)) {
        throw "Build artifact manager was not found: $BuildArtifactManager"
    }

    $Node = Get-Command "node" -ErrorAction SilentlyContinue
    if ($null -eq $Node) {
        throw "node was not found. Install the exact Node.js version declared in toolchain.lock.json."
    }

    Invoke-Checked `
        -Title $Title `
        -FilePath $Node.Source `
        -Arguments @(
            $BuildArtifactManager,
            "prune",
            "--limit-gib",
            $BuildCacheLimitGiB.ToString(),
            "--target-gib",
            $BuildCacheTargetGiB.ToString()
        )
}

function Invoke-AndroidApkBuild {
    $PreviousLimit = $env:MYSTIA_BUILD_CACHE_LIMIT_GIB
    $PreviousTarget = $env:MYSTIA_BUILD_CACHE_TARGET_GIB
    $PreviousSkipCleanup = $env:MYSTIA_SKIP_BUILD_CACHE_CLEANUP

    try {
        $env:MYSTIA_BUILD_CACHE_LIMIT_GIB = $BuildCacheLimitGiB.ToString()
        $env:MYSTIA_BUILD_CACHE_TARGET_GIB = $BuildCacheTargetGiB.ToString()
        # -SkipPackage means the caller intentionally keeps raw Tauri outputs for later use.
        $env:MYSTIA_SKIP_BUILD_CACHE_CLEANUP = if ($SkipBuildCacheCleanup -or $SkipPackage) { "1" } else { "0" }
        Invoke-Pnpm -Title "Build signed Android APK" -Arguments @("tauri:android:apk:signed")
    }
    finally {
        if ($null -eq $PreviousLimit) {
            Remove-Item Env:MYSTIA_BUILD_CACHE_LIMIT_GIB -ErrorAction SilentlyContinue
        }
        else {
            $env:MYSTIA_BUILD_CACHE_LIMIT_GIB = $PreviousLimit
        }

        if ($null -eq $PreviousTarget) {
            Remove-Item Env:MYSTIA_BUILD_CACHE_TARGET_GIB -ErrorAction SilentlyContinue
        }
        else {
            $env:MYSTIA_BUILD_CACHE_TARGET_GIB = $PreviousTarget
        }

        if ($null -eq $PreviousSkipCleanup) {
            Remove-Item Env:MYSTIA_SKIP_BUILD_CACHE_CLEANUP -ErrorAction SilentlyContinue
        }
        else {
            $env:MYSTIA_SKIP_BUILD_CACHE_CLEANUP = $PreviousSkipCleanup
        }
    }
}

if ($BuildCacheTargetGiB -ge $BuildCacheLimitGiB) {
    throw "BuildCacheTargetGiB must be less than BuildCacheLimitGiB. Actual: target=$BuildCacheTargetGiB, limit=$BuildCacheLimitGiB"
}

$PreviousBuildCacheSkipEnvironment = $env:MYSTIA_SKIP_BUILD_CACHE_CLEANUP
$env:MYSTIA_SKIP_BUILD_CACHE_CLEANUP = if ($SkipBuildCacheCleanup) { "1" } else { "0" }

Push-Location $RepoRoot
try {
    $Node = Get-Command "node" -ErrorAction SilentlyContinue
    if ($null -eq $Node) {
        throw "node was not found. Install the exact Node.js version declared in toolchain.lock.json."
    }

    Invoke-Checked `
        -Title "Validate locked build toolchain" `
        -FilePath $Node.Source `
        -Arguments @($ToolchainCheckScript, "full")

    # A new Tauri build does not depend on a reused frontend build, so it is safe to prune stale buckets first.
    if (-not $SkipBuildCacheCleanup -and -not $SkipTauriBuild) {
        Invoke-BuildCachePrune -Title "Prune stale build artifacts before compilation"
    }

    if (-not $SkipInstall) {
        $InstallArgs = @("install")
        if (-not $NoFrozenLockfile) {
            $InstallArgs += "--frozen-lockfile"
        }

        Invoke-Pnpm -Title "Install companion frontend dependencies" -Arguments $InstallArgs
    }

    Write-Step "Run Mod preflight"
    & $PreflightScript -ReferenceDir $EffectiveReferenceDir

    if (-not $SkipFrontendBuild) {
        Invoke-Pnpm -Title "Build companion frontend" -Arguments @("build")
    }

    if (-not $SkipTauriBuild) {
        Invoke-Pnpm -Title "Build companion window" -Arguments @("tauri:build")

        $Cargo = Get-Command "cargo" -ErrorAction SilentlyContinue
        if ($null -eq $Cargo) {
            throw "cargo was not found. Install the exact Rust toolchain declared in toolchain.lock.json."
        }

        $UpdaterManifest = Join-Path $RepoRoot "apps/companion/src-tauri/Cargo.toml"
        Invoke-Checked `
            -Title "Build companion updater" `
            -FilePath $Cargo.Source `
            -Arguments @("build", "--manifest-path", $UpdaterManifest, "--release", "--bin", "mystia-steward-companion-updater")
    }

    $Dotnet = Get-Command "dotnet" -ErrorAction SilentlyContinue
    if ($null -eq $Dotnet) {
        throw "dotnet was not found. Install the exact .NET SDK declared in toolchain.lock.json."
    }

    $DotnetBuildArgs = @("build", $ProjectPath, "-c", $Configuration, "/p:ReferenceDir=$EffectiveReferenceDir")

    Invoke-Checked `
        -Title "Build BepInEx plugin" `
        -FilePath $Dotnet.Source `
        -Arguments $DotnetBuildArgs

    if (-not $SkipPackage) {
        Write-Step "Create release package"
        & $PackageScript -Configuration $Configuration
    }

    if ($BuildAndroidApk) {
        Invoke-AndroidApkBuild
    }

    if (-not $SkipBuildCacheCleanup -and -not $SkipPackage) {
        Invoke-BuildCachePrune -Title "Enforce build cache limit after packaging"
    }
}
finally {
    Pop-Location
    if ($null -eq $PreviousBuildCacheSkipEnvironment) {
        Remove-Item Env:MYSTIA_SKIP_BUILD_CACHE_CLEANUP -ErrorAction SilentlyContinue
    }
    else {
        $env:MYSTIA_SKIP_BUILD_CACHE_CLEANUP = $PreviousBuildCacheSkipEnvironment
    }
}

Write-Host ""
Write-Host "Build completed." -ForegroundColor Green
