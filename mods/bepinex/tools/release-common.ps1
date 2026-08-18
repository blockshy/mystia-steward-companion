#requires -Version 7.0

Set-StrictMode -Version Latest

$script:MystiaReleasePackageName = "mystia-steward-companion-bepinex.zip"
$script:MystiaReleaseCompanionName = "mystia-steward-companion-companion-windows-x64.exe"
$script:MystiaReleaseAndroidNames = @(
    "mystia-steward-companion-android-arm64-v8a.apk",
    "mystia-steward-companion-android-armeabi-v7a.apk"
)
$script:MystiaReleaseManifestName = "update-manifest.json"
$script:MystiaReleaseCatalogName = "update-catalog.json"
$script:MystiaReleaseChecksumsName = "SHA256SUMS.txt"
$script:MystiaReleaseAssetContentTypes =
    [System.Collections.Generic.Dictionary[string, string]]::new(
        [System.StringComparer]::Ordinal
    )
$script:MystiaReleaseAssetContentTypes.Add(
    $script:MystiaReleasePackageName,
    "application/zip"
)
$script:MystiaReleaseAssetContentTypes.Add(
    $script:MystiaReleaseCompanionName,
    "application/x-msdownload"
)
foreach ($AndroidName in $script:MystiaReleaseAndroidNames) {
    $script:MystiaReleaseAssetContentTypes.Add(
        $AndroidName,
        "application/vnd.android.package-archive"
    )
}
$script:MystiaReleaseAssetContentTypes.Add(
    $script:MystiaReleaseManifestName,
    "application/json"
)
$script:MystiaReleaseAssetContentTypes.Add(
    $script:MystiaReleaseCatalogName,
    "application/json"
)
$script:MystiaReleaseAssetContentTypes.Add(
    $script:MystiaReleaseChecksumsName,
    "text/plain; charset=utf-8"
)
if ($script:MystiaReleaseAssetContentTypes.Count -ne 7) {
    throw "The canonical Release asset content-type map must contain exactly seven entries."
}

function Get-MystiaReleaseAssetContentType {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$AssetName
    )

    if (-not $script:MystiaReleaseAssetContentTypes.ContainsKey($AssetName)) {
        throw "Unknown canonical Release asset name: $AssetName"
    }
    return $script:MystiaReleaseAssetContentTypes[$AssetName]
}

function Invoke-MystiaChecked {
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

function Get-MystiaCommand {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$InstallHint
    )

    $Command = Get-Command $Name -ErrorAction SilentlyContinue
    if ($null -eq $Command) {
        throw "$Name was not found. $InstallHint"
    }

    return $Command.Source
}

function Write-MystiaUtf8WithoutBom {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Content
    )

    [System.IO.File]::WriteAllText(
        $Path,
        $Content,
        [System.Text.UTF8Encoding]::new($false)
    )
}

function Get-MystiaJsonPositiveInt64 {
    param(
        [Parameter(Mandatory = $true)][object]$Value,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if ($Value -isnot [int] -and $Value -isnot [long]) {
        throw "$Label must be a JSON integer."
    }
    $Result = [long]$Value
    if ($Result -le 0) {
        throw "$Label must be a positive JSON integer."
    }
    return $Result
}

function Assert-MystiaJsonBoolean {
    param(
        [Parameter(Mandatory = $true)][object]$Value,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if ($Value -isnot [bool]) {
        throw "$Label must be a JSON boolean."
    }
}

function Assert-MystiaJsonString {
    param(
        [Parameter(Mandatory = $true)][object]$Value,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if ($Value -isnot [string]) {
        throw "$Label must be a JSON string."
    }
}

function Assert-MystiaCanonicalUtcTimestamp {
    param(
        [Parameter(Mandatory = $true)][object]$Value,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if ($Value -isnot [string] -or
        $Value -cnotmatch '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$') {
        throw "$Label must be a canonical UTC timestamp with millisecond precision."
    }
    $Parsed = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParseExact(
        $Value,
        "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
        [System.Globalization.CultureInfo]::InvariantCulture,
        [System.Globalization.DateTimeStyles]::AssumeUniversal -bor
            [System.Globalization.DateTimeStyles]::AdjustToUniversal,
        [ref]$Parsed
    )) {
        throw "$Label is not a valid UTC timestamp."
    }
}

function Assert-MystiaCatalogEntryShape {
    param(
        [Parameter(Mandatory = $true)][object]$Entry,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if ($null -eq $Entry -or $Entry -is [System.Array]) {
        throw "$Label must be a JSON object."
    }
    foreach ($PropertyName in @(
        "version",
        "tag",
        "title",
        "channel",
        "publishedAtUtc",
        "releaseUrl",
        "notesMarkdown"
    )) {
        if ($null -eq $Entry.PSObject.Properties[$PropertyName]) {
            throw "$Label is missing $PropertyName."
        }
        Assert-MystiaJsonString -Value $Entry.$PropertyName -Label "$Label $PropertyName"
    }
    Assert-MystiaCanonicalUtcTimestamp `
        -Value $Entry.publishedAtUtc `
        -Label "$Label publishedAtUtc"
}

function Get-MystiaVersionFromTag {
    param([Parameter(Mandatory = $true)][string]$Tag)

    if ($Tag -cnotmatch '^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-preview\.([1-9]\d*))?$') {
        throw "Release tag must be canonical stable vX.Y.Z or preview vX.Y.Z-preview.N. Actual: $Tag"
    }

    return $Tag.Substring(1)
}

function Get-MystiaReleaseChannel {
    param([Parameter(Mandatory = $true)][string]$Version)

    if ($Version -cmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') {
        return "stable"
    }
    if ($Version -cmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)-preview\.([1-9]\d*)$') {
        return "preview"
    }

    throw "Unsupported release version: $Version"
}

function Get-MystiaFirstMatch {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Pattern
    )

    $Content = Get-Content -Raw -LiteralPath $Path
    $Match = [regex]::Match(
        $Content,
        $Pattern,
        [System.Text.RegularExpressions.RegexOptions]::Multiline
    )
    if (-not $Match.Success) {
        throw "Version pattern not found in $Path"
    }

    return $Match.Groups[1].Value
}

function Assert-MystiaProjectVersion {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$ExpectedVersion
    )

    $Sources = @(
        @{
            Name = "package.json"
            Path = Join-Path $RepoRoot "package.json"
            Pattern = '"version"\s*:\s*"([^"]+)"'
        },
        @{
            Name = "tauri.conf.json"
            Path = Join-Path $RepoRoot "apps/companion/src-tauri/tauri.conf.json"
            Pattern = '"version"\s*:\s*"([^"]+)"'
        },
        @{
            Name = "Cargo.toml"
            Path = Join-Path $RepoRoot "apps/companion/src-tauri/Cargo.toml"
            Pattern = '^version = "([^"]+)"'
        },
        @{
            Name = "Cargo.lock"
            Path = Join-Path $RepoRoot "apps/companion/src-tauri/Cargo.lock"
            Pattern = '(?s)name = "mystia-steward-companion"\s+version = "([^"]+)"'
        },
        @{
            Name = "PluginVersion"
            Path = Join-Path $RepoRoot "mods/bepinex/src/Plugin/MystiaStewardCompanionPlugin.cs"
            Pattern = 'public const string PluginVersion = "([^"]+)";'
        }
    )

    $Mismatches = @()
    foreach ($Source in $Sources) {
        $Actual = Get-MystiaFirstMatch -Path $Source.Path -Pattern $Source.Pattern
        if ($Actual -cne $ExpectedVersion) {
            $Mismatches += "  - $($Source.Name): $Actual ($($Source.Path))"
        }
    }

    if ($Mismatches.Count -gt 0) {
        throw @(
            "Project version does not match the release tag.",
            "Expected: $ExpectedVersion",
            "Mismatches:",
            ($Mismatches -join [Environment]::NewLine)
        ) -join [Environment]::NewLine
    }
}

function Assert-MystiaTargetCommit {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$TargetCommitSha
    )

    if ($TargetCommitSha -cnotmatch '^[0-9a-f]{40}$') {
        throw "TargetCommitSha must be a full lowercase 40-character Git commit SHA. Actual: $TargetCommitSha"
    }

    $Git = Get-MystiaCommand -Name "git" -InstallHint "Install Git before preparing a release."
    $ActualRaw = @(& $Git -C $RepoRoot rev-parse HEAD)
    $GitExitCode = $LASTEXITCODE
    if ($GitExitCode -ne 0 -or $ActualRaw.Count -ne 1 -or $ActualRaw[0] -isnot [string]) {
        throw "Failed to resolve the checked-out Git commit."
    }
    $Actual = ([string]$ActualRaw[0]).Trim().ToLowerInvariant()
    if ($Actual -cnotmatch '^[0-9a-f]{40}$') {
        throw "Git returned an invalid checked-out commit identity."
    }

    if ($Actual -cne $TargetCommitSha) {
        throw "Checked-out commit does not match TargetCommitSha. HEAD=$Actual, target=$TargetCommitSha"
    }

    return $TargetCommitSha
}

function Resolve-MystiaRequiredFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Missing $Label`: $Path"
    }
    $Item = Get-Item -LiteralPath $Path -Force
    if (($Item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$Label must be a real file, not a symlink or reparse point: $Path"
    }
    if ($Item.Length -le 0) {
        throw "$Label must not be empty: $Path"
    }

    return $Item.FullName
}

function Get-MystiaPayloadAssetNames {
    param([Parameter(Mandatory = $true)][bool]$IncludeAndroid)

    $Names = @(
        $script:MystiaReleasePackageName,
        $script:MystiaReleaseCompanionName
    )
    if ($IncludeAndroid) {
        $Names += $script:MystiaReleaseAndroidNames
    }
    $Names += @(
        $script:MystiaReleaseManifestName,
        $script:MystiaReleaseCatalogName
    )
    return $Names
}

function Get-MystiaReleaseAssetNames {
    param([Parameter(Mandatory = $true)][bool]$IncludeAndroid)

    return @(
        (Get-MystiaPayloadAssetNames -IncludeAndroid $IncludeAndroid) +
        $script:MystiaReleaseChecksumsName
    )
}

function Read-MystiaReleaseNotes {
    param([Parameter(Mandatory = $true)][string]$NotesFile)

    $Resolved = Resolve-MystiaRequiredFile -Path $NotesFile -Label "release notes file"
    $Bytes = [System.IO.File]::ReadAllBytes($Resolved)
    if ($Bytes.Length -gt 65536) {
        throw "Release notes exceed 65536 UTF-8 bytes: $Resolved"
    }

    $StrictUtf8 = [System.Text.UTF8Encoding]::new($false, $true)
    try {
        $Notes = $StrictUtf8.GetString($Bytes)
    }
    catch {
        throw "Release notes must be valid UTF-8 without binary data: $Resolved"
    }

    if ([string]::IsNullOrWhiteSpace($Notes)) {
        throw "Release notes must not be empty: $Resolved"
    }

    return @{
        Path = $Resolved
        Text = $Notes
    }
}

function Assert-MystiaPreparedChecksums {
    param(
        [Parameter(Mandatory = $true)][string]$DistRoot,
        [Parameter(Mandatory = $true)][bool]$IncludeAndroid
    )

    $PayloadNames = @(Get-MystiaPayloadAssetNames -IncludeAndroid $IncludeAndroid)
    $ChecksumsPath = Join-Path $DistRoot $script:MystiaReleaseChecksumsName
    [void](Resolve-MystiaRequiredFile -Path $ChecksumsPath -Label "release checksum list")

    $Lines = @(Get-Content -LiteralPath $ChecksumsPath | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($Lines.Count -ne $PayloadNames.Count) {
        throw "Checksum entry count mismatch. Expected $($PayloadNames.Count), actual $($Lines.Count)."
    }

    $ExpectedNames = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::Ordinal
    )
    foreach ($Name in $PayloadNames) {
        [void]$ExpectedNames.Add($Name)
    }

    $Seen = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::Ordinal
    )
    foreach ($Line in $Lines) {
        if ($Line -cnotmatch '^([0-9a-f]{64})  ([A-Za-z0-9._-]+)$') {
            throw "Invalid SHA256SUMS entry: $Line"
        }
        $ExpectedHash = $Matches[1]
        $Name = $Matches[2]
        if (-not $ExpectedNames.Contains($Name) -or -not $Seen.Add($Name)) {
            throw "Unexpected or duplicate SHA256SUMS asset: $Name"
        }

        $Path = Resolve-MystiaRequiredFile -Path (Join-Path $DistRoot $Name) -Label "release asset"
        $ActualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant()
        if ($ActualHash -cne $ExpectedHash) {
            throw "Release asset SHA256 mismatch: $Name"
        }
    }

    if ($Seen.Count -ne $ExpectedNames.Count) {
        throw "SHA256SUMS does not cover the exact release payload."
    }
}
