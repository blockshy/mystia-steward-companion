#requires -Version 7.0

param(
    [Parameter(Mandatory = $true)]
    [string]$Version
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$ToolDir = $PSScriptRoot
$RootDir = (Resolve-Path (Join-Path $ToolDir "..")).Path
$RepoRoot = (Resolve-Path (Join-Path $RootDir "../..")).Path

$NormalizedVersion = $Version.Trim()
if ($NormalizedVersion.StartsWith("v", [StringComparison]::OrdinalIgnoreCase)) {
    $NormalizedVersion = $NormalizedVersion.Substring(1)
}

if ($NormalizedVersion -notmatch '^\d+\.\d+\.\d+([\-+][0-9A-Za-z.-]+)?$') {
    throw "Invalid version: $Version. Expected SemVer like 1.0.1 or v1.0.1."
}

function Update-FirstMatch {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Pattern,
        [Parameter(Mandatory = $true)][string]$Replacement
    )

    $Content = Get-Content -Raw -LiteralPath $Path
    $Regex = [regex]::new($Pattern, [System.Text.RegularExpressions.RegexOptions]::Multiline)
    if (-not $Regex.IsMatch($Content)) {
        throw "Version pattern not found in $Path"
    }

    $Updated = $Regex.Replace($Content, $Replacement, 1)
    if ($Updated -ne $Content) {
        Set-Content -Encoding UTF8 -NoNewline -LiteralPath $Path -Value $Updated
        Write-Host "Updated $Path"
    }
}

$PackageJson = Join-Path $RepoRoot "package.json"
$TauriConfig = Join-Path $RepoRoot "apps/companion/src-tauri/tauri.conf.json"
$CargoToml = Join-Path $RepoRoot "apps/companion/src-tauri/Cargo.toml"
$CargoLock = Join-Path $RepoRoot "apps/companion/src-tauri/Cargo.lock"
$PluginSource = Join-Path $RepoRoot "mods/bepinex/src/Plugin/MystiaStewardCompanionPlugin.cs"

Update-FirstMatch -Path $PackageJson -Pattern '"version"\s*:\s*"[^"]+"' -Replacement "`"version`": `"$NormalizedVersion`""
Update-FirstMatch -Path $TauriConfig -Pattern '"version"\s*:\s*"[^"]+"' -Replacement "`"version`": `"$NormalizedVersion`""
Update-FirstMatch -Path $CargoToml -Pattern '^version = "[^"]+"' -Replacement "version = `"$NormalizedVersion`""
Update-FirstMatch -Path $CargoLock -Pattern '(?s)(name = "mystia-steward-companion"\s+version = ")[^"]+(")' -Replacement "`${1}$NormalizedVersion`${2}"
Update-FirstMatch -Path $PluginSource -Pattern '(public const string PluginVersion = ")[^"]+(";)' -Replacement "`${1}$NormalizedVersion`${2}"

Write-Host ""
Write-Host "Project version synchronized to $NormalizedVersion" -ForegroundColor Green
