#requires -Version 7.0

param(
    [string]$ReferenceDir = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$RootDir = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$RepoRoot = (Resolve-Path (Join-Path $RootDir "../..")).Path
$ToolchainLockPath = Join-Path $RepoRoot "toolchain.lock.json"
$ToolchainLock = Get-Content -LiteralPath $ToolchainLockPath -Raw | ConvertFrom-Json
$ExpectedDotnetSdk = [string]$ToolchainLock.dotnetSdk
$EffectiveReferenceDir = if ([string]::IsNullOrWhiteSpace($ReferenceDir)) {
    Join-Path $RootDir "References"
} else {
    $ReferenceDir
}
$Failed = $false

function Test-RequiredFile {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        Write-Host "OK   $Path"
    } else {
        Write-Host "MISS $Path"
        $script:Failed = $true
    }
}

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

    if ($DotnetExitCode -ne 0 -or $ActualDotnetSdk -ne $ExpectedDotnetSdk) {
        Write-Host "MISMATCH dotnet expected=$ExpectedDotnetSdk actual=$ActualDotnetSdk"
        $Failed = $true
    }
    else {
        Write-Host "OK   dotnet $ActualDotnetSdk"
    }
}

Write-Host ""
Write-Host "Checking build references: $EffectiveReferenceDir"
Test-RequiredFile (Join-Path $EffectiveReferenceDir "BepInEx.Core.dll")
Test-RequiredFile (Join-Path $EffectiveReferenceDir "BepInEx.Unity.IL2CPP.dll")
Test-RequiredFile (Join-Path $EffectiveReferenceDir "0Harmony.dll")
Test-RequiredFile (Join-Path $EffectiveReferenceDir "Il2CppInterop.Runtime.dll")
Test-RequiredFile (Join-Path $EffectiveReferenceDir "Il2Cppmscorlib.dll")
Test-RequiredFile (Join-Path $EffectiveReferenceDir "UnityEngine.CoreModule.dll")
Test-RequiredFile (Join-Path $EffectiveReferenceDir "UnityEngine.InputLegacyModule.dll")

if ($Failed) {
    Write-Host ""
    throw "Preflight failed. Install the locked .NET SDK and copy missing DLLs into mods/bepinex/References, or pass -ReferenceDir to a directory containing them."
}

Write-Host ""
Write-Host "Preflight passed."
