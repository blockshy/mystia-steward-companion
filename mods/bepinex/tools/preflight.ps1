#requires -Version 7.0

param(
    [string]$ReferenceDir = ""
)

$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest

$RootDir = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$RepoRoot = (Resolve-Path (Join-Path $RootDir "../..")).Path
$ToolchainLockPath = Join-Path $RepoRoot "toolchain.lock.json"
$ReferenceVerifierPath = Join-Path $RepoRoot "scripts/restore-build-references.mjs"
$ToolchainLock = Get-Content -LiteralPath $ToolchainLockPath -Raw | ConvertFrom-Json
$ExpectedDotnetSdk = [string]$ToolchainLock.dotnetSdk
$EffectiveReferenceDir = if ([string]::IsNullOrWhiteSpace($ReferenceDir)) {
    Join-Path $RootDir "References"
} else {
    $ReferenceDir
}
$Failed = $false

Write-Host "Checking .NET SDK"
$Dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if ($null -eq $Dotnet) {
    Write-Host "MISS dotnet"
    $Failed = $true
}
else {
    Push-Location $RepoRoot
    try {
        $ActualDotnetSdk = ((& $Dotnet.Source --version) | Out-String).Trim()
        $DotnetExitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }

    if ($DotnetExitCode -ne 0 -or $ActualDotnetSdk -cne $ExpectedDotnetSdk) {
        Write-Host "MISMATCH dotnet expected=$ExpectedDotnetSdk actual=$ActualDotnetSdk"
        $Failed = $true
    }
    else {
        Write-Host "OK   dotnet $ActualDotnetSdk"
    }
}

Write-Host ""
Write-Host "Checking build references: $EffectiveReferenceDir"
$Node = Get-Command node -ErrorAction SilentlyContinue
if ($null -eq $Node) {
    Write-Host "MISS node (required for strict reference verification)"
    $Failed = $true
}
elseif (-not (Test-Path -LiteralPath $ReferenceVerifierPath -PathType Leaf)) {
    Write-Host "MISS $ReferenceVerifierPath"
    $Failed = $true
}
else {
    & $Node.Source $ReferenceVerifierPath --verify --output $EffectiveReferenceDir
    if ($LASTEXITCODE -ne 0) {
        $Failed = $true
    }
}

if ($Failed) {
    Write-Host ""
    throw "Preflight failed. Install the locked .NET SDK and restore the exact reference bundle described by mods/bepinex/References/references.lock.json."
}

Write-Host ""
Write-Host "Preflight passed."
