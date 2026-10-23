#requires -Version 7.0
<#
.SYNOPSIS
构建 Flutter Windows、独立 updater、三个 C# DLL 和可选正式签名 Android APK。
.DESCRIPTION
所有 SDK 均按根锁校验；Windows 包先在暂存区全量验证，再替换 dist。不会安装游戏、
创建标签或发布 Release。Flutter SDK 必须明确指定，无法从 PATH 悄悄选择其他版本。
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$FlutterSdkRoot = $env:MYSTIA_FLUTTER_SDK_ROOT,
    [string]$ReferenceDir = $env:MYSTIA_REFERENCE_DIR,
    [switch]$SkipInstall,
    [switch]$SkipFlutterBuild,
    [switch]$SkipPackage,
    [switch]$BuildAndroidApk,
    [ValidateRange(1, 1024)][int]$BuildCacheLimitGiB = 12,
    [ValidateRange(1, 1024)][int]$BuildCacheTargetGiB = 8,
    [switch]$SkipBuildCacheCleanup
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
if (-not $FlutterSdkRoot) { throw '必须提供 -FlutterSdkRoot 或 MYSTIA_FLUTTER_SDK_ROOT。' }
if (-not $ReferenceDir) { $ReferenceDir = Join-Path $repository 'mods/bepinex/References' }
function Invoke-Checked([string]$Command, [string[]]$Arguments) {
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) { throw "构建命令失败，退出码 $LASTEXITCODE ：$Command" }
}
Push-Location -LiteralPath $repository
try {
    Invoke-Checked 'node' @('scripts/check-build-toolchain.mjs', 'full')
    if (-not $SkipBuildCacheCleanup) {
        Invoke-Checked 'node' @('scripts/manage-build-artifacts.mjs', 'prune', '--limit-gib', $BuildCacheLimitGiB.ToString(), '--target-gib', $BuildCacheTargetGiB.ToString())
    }
    & (Join-Path $PSScriptRoot 'preflight.ps1') -ReferenceDir $ReferenceDir
    if ($LASTEXITCODE -ne 0) { throw 'BepInEx 引用预检失败。' }
    $flutter = @('scripts/flutter-workspace.mjs', '--sdk-root', $FlutterSdkRoot, '--task')
    if (-not $SkipInstall) { Invoke-Checked 'node' ($flutter + 'get') }
    # 格式检查不修改源码；Pigeon 在独占临时目录重新生成三端接口并逐字节核验。
    # 避免本机构建成功却交付无法从锁定定义重现的原生通道文件。
    Invoke-Checked 'node' ($flutter + 'format-check')
    Invoke-Checked 'node' ($flutter + 'generated-check')
    Invoke-Checked 'node' @('tests/build-artifacts/windows-runtime-audit.mjs')
    # 真实 HTTP 测试链接当前 Mod 源码，必须在严格参考包预检后生成宿主，不允许拿旧DLL充数。
    Invoke-Checked 'dotnet' @('build', 'tests/flutter-contracts/host/FlutterContractsHost.csproj', '-c', 'Release', "-p:ReferenceDir=$ReferenceDir", '-p:NuGetAudit=false')
    Invoke-Checked 'node' ($flutter + 'verify')
    Invoke-Checked 'node' ($flutter + 'contracts-test')
    if (-not $SkipFlutterBuild) { Invoke-Checked 'node' ($flutter + 'build-windows') }
    Invoke-Checked 'cargo' @('test', '--locked', '--manifest-path', 'native/updater/Cargo.toml')
    Invoke-Checked 'cargo' @('build', '--locked', '--manifest-path', 'native/updater/Cargo.toml', '--release')
    Invoke-Checked 'dotnet' @('run', '--project', 'tests/csharp-business-package/CSharpBusinessPackageSmoke.csproj', '-c', 'Release', '-p:NuGetAudit=false')
    $cargoRoot = if ($env:CARGO_TARGET_DIR) { $env:CARGO_TARGET_DIR } else { Join-Path $repository 'native/updater/target' }
    # 三种语言读取同一个临时目录，验证真实二进制校验器的一致性；不会执行任何夹具载荷。
    Invoke-Checked 'node' @('tests/csharp-business-package/package-manifest-audit.mjs', '--rust', (Join-Path $cargoRoot 'release/mystia-steward-companion-updater.exe'), '--dotnet', 'tests/csharp-business-package/bin/Release/net6.0/CSharpBusinessPackageSmoke.dll')
    Invoke-Checked 'dotnet' @('build', 'mods/bepinex/MystiaStewardCompanion.BepInEx.csproj', '-c', $Configuration, "-p:ReferenceDir=$ReferenceDir", '-p:NuGetAudit=false')
    if (-not $SkipPackage) { & (Join-Path $PSScriptRoot 'package-release.ps1') -Configuration $Configuration }
    if ($BuildAndroidApk) {
        # 签名材料仅由调用方或 CI 提供。签名指纹/ABI/applicationId/版本逐项验证后才进入 dist。
        $previousSdk = $env:MYSTIA_FLUTTER_SDK_ROOT
        try { $env:MYSTIA_FLUTTER_SDK_ROOT = $FlutterSdkRoot; Invoke-Checked 'node' @('scripts/build-android-signed-apk.mjs') }
        finally { $env:MYSTIA_FLUTTER_SDK_ROOT = $previousSdk }
    }
} finally { Pop-Location }
