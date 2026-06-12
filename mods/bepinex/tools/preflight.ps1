#requires -Version 7.0

param(
    [string]$ReferenceDir = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$RootDir = Resolve-Path (Join-Path $PSScriptRoot "..")
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
if (Get-Command dotnet -ErrorAction SilentlyContinue) {
    dotnet --version
} else {
    Write-Host "MISS dotnet"
    $Failed = $true
}

Write-Host ""
Write-Host "Checking data files"
Test-RequiredFile (Join-Path $RootDir "Data/recipes.json")
Test-RequiredFile (Join-Path $RootDir "Data/beverages.json")
Test-RequiredFile (Join-Path $RootDir "Data/ingredients.json")
Test-RequiredFile (Join-Path $RootDir "Data/customer_normal.json")
Test-RequiredFile (Join-Path $RootDir "Data/customer_rare.json")
Test-RequiredFile (Join-Path $RootDir "Data/food-tag-id-map.json")

Write-Host ""
Write-Host "Checking build references: $EffectiveReferenceDir"
Test-RequiredFile (Join-Path $EffectiveReferenceDir "BepInEx.Core.dll")
Test-RequiredFile (Join-Path $EffectiveReferenceDir "BepInEx.Unity.IL2CPP.dll")
Test-RequiredFile (Join-Path $EffectiveReferenceDir "0Harmony.dll")
Test-RequiredFile (Join-Path $EffectiveReferenceDir "Il2CppInterop.Runtime.dll")
Test-RequiredFile (Join-Path $EffectiveReferenceDir "Il2Cppmscorlib.dll")
Test-RequiredFile (Join-Path $EffectiveReferenceDir "UnityEngine.CoreModule.dll")
Test-RequiredFile (Join-Path $EffectiveReferenceDir "UnityEngine.IMGUIModule.dll")
Test-RequiredFile (Join-Path $EffectiveReferenceDir "UnityEngine.InputLegacyModule.dll")

if ($Failed) {
    Write-Host ""
    throw "Preflight failed. Copy the missing DLLs into mods/bepinex/References, or pass -ReferenceDir to a directory containing them."
}

Write-Host ""
Write-Host "Preflight passed."
