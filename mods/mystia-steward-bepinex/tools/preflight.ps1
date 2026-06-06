$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$RootDir = Resolve-Path (Join-Path $PSScriptRoot "..")
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
Write-Host "Checking build references"
Test-RequiredFile (Join-Path $RootDir "References/BepInEx.Core.dll")
Test-RequiredFile (Join-Path $RootDir "References/BepInEx.Unity.IL2CPP.dll")
Test-RequiredFile (Join-Path $RootDir "References/0Harmony.dll")
Test-RequiredFile (Join-Path $RootDir "References/Il2CppInterop.Runtime.dll")
Test-RequiredFile (Join-Path $RootDir "References/Il2Cppmscorlib.dll")
Test-RequiredFile (Join-Path $RootDir "References/UnityEngine.CoreModule.dll")
Test-RequiredFile (Join-Path $RootDir "References/UnityEngine.IMGUIModule.dll")
Test-RequiredFile (Join-Path $RootDir "References/UnityEngine.InputLegacyModule.dll")

if ($Failed) {
    Write-Host ""
    Write-Host "Preflight failed. See References/README.md and README.md for setup steps."
    exit 1
}

Write-Host ""
Write-Host "Preflight passed."
