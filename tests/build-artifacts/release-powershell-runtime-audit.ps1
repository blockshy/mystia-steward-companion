#requires -Version 7.5

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$ReleaseCommonPath = Join-Path $RepoRoot "mods/bepinex/tools/release-common.ps1"
$PublishPath = Join-Path $RepoRoot "mods/bepinex/tools/publish-release.ps1"
$PowerShellSources = @(
    $ReleaseCommonPath,
    (Join-Path $RepoRoot "mods/bepinex/tools/prepare-release-assets.ps1"),
    $PublishPath,
    (Join-Path $RepoRoot "mods/bepinex/tools/build-release.ps1"),
    (Join-Path $RepoRoot "mods/bepinex/tools/preflight.ps1")
)

foreach ($Path in $PowerShellSources) {
    $Tokens = $null
    $Errors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile(
        $Path,
        [ref]$Tokens,
        [ref]$Errors
    )
    if ($Errors.Count -ne 0) {
        throw "PowerShell parser errors in $Path`: $($Errors.Message -join '; ')"
    }
}

. $ReleaseCommonPath
foreach ($InvalidTag in @("V1.4.0", "v1.4.0-PREVIEW.1", "v01.4.0")) {
    try {
        [void](Get-MystiaVersionFromTag -Tag $InvalidTag)
        throw "Expected a non-canonical release tag to be rejected: $InvalidTag"
    }
    catch {
        if ($_.Exception.Message -like "Expected a non-canonical release tag*") {
            throw
        }
    }
}

$Tokens = $null
$Errors = $null
$PublishAst = [System.Management.Automation.Language.Parser]::ParseFile(
    $PublishPath,
    [ref]$Tokens,
    [ref]$Errors
)
foreach ($Name in @(
    "Get-MystiaRepositoryReleases",
    "Get-MystiaReleaseByTag",
    "Get-MystiaRemoteTagRef",
    "ConvertTo-MystiaExactTagRef",
    "Get-MystiaExactRemoteTagRef",
    "Wait-MystiaExactRemoteTagRef",
    "Assert-MystiaOfficialTarget",
    "Assert-MystiaPreviewTarget",
    "Assert-MystiaRemoteReleaseIdentity",
    "Assert-MystiaRemoteRelease",
    "Assert-MystiaRemoteReleaseMetadata",
    "Assert-MystiaRemoteReleaseAssets",
    "Get-MystiaRemoteReleaseById",
    "Wait-MystiaRemoteReleaseState",
    "Get-MystiaLatestRelease",
    "Wait-MystiaLatestRelease",
    "Invoke-MystiaGitHubJsonRequest",
    "New-MystiaRemoteTag",
    "Wait-MystiaGitHubMutationInterval",
    "New-MystiaDraftRelease",
    "Publish-MystiaDraftRelease",
    "Invoke-MystiaReleaseAssetUpload",
    "Assert-MystiaImmutableReleasesEnabled",
    "Assert-MystiaPreparedMetadata",
    "Invoke-MystiaReleasePublicationTransaction"
)) {
    $FunctionAst = $PublishAst.Find(
        {
            param($Node)
            $Node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                $Node.Name -ceq $Name
        },
        $true
    )
    if ($null -eq $FunctionAst) {
        throw "Missing PowerShell function: $Name"
    }
    Invoke-Expression $FunctionAst.Extent.Text
}

$script:Tag = "v9.9.9"
$script:Repo = "blockshy/mystia-steward-companion"
$script:RepoRoot = $RepoRoot

function Assert-ThrowsLike {
    param(
        [Parameter(Mandatory = $true)][scriptblock]$Action,
        [Parameter(Mandatory = $true)][string]$Pattern,
        [Parameter(Mandatory = $true)][string]$Label
    )

    try {
        & $Action
    }
    catch {
        if ($_.Exception.Message -notlike $Pattern) {
            throw "$Label threw an unexpected error: $($_.Exception.Message)"
        }
        return
    }
    throw "$Label did not fail closed."
}

function Get-TestReleaseAssetContentType {
    param([Parameter(Mandatory = $true)][string]$AssetName)

    switch -CaseSensitive ($AssetName) {
        "mystia-steward-companion-bepinex.zip" {
            return "application/zip"
        }
        "mystia-steward-companion-companion-windows-x64.exe" {
            return "application/x-msdownload"
        }
        "mystia-steward-companion-android-arm64-v8a.apk" {
            return "application/vnd.android.package-archive"
        }
        "mystia-steward-companion-android-armeabi-v7a.apk" {
            return "application/vnd.android.package-archive"
        }
        "update-manifest.json" {
            return "application/json"
        }
        "update-catalog.json" {
            return "application/json"
        }
        "SHA256SUMS.txt" {
            return "text/plain; charset=utf-8"
        }
        default {
            throw "The test MIME oracle does not recognize: $AssetName"
        }
    }
}

function Write-TestJson {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][object]$Value
    )

    Write-MystiaUtf8WithoutBom `
        -Path $Path `
        -Content (($Value | ConvertTo-Json -Depth 20) + [Environment]::NewLine)
}

function Write-TestChecksums {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [bool]$IncludeAndroid = $false
    )

    $Lines = @(
        Get-MystiaPayloadAssetNames -IncludeAndroid $IncludeAndroid |
            Sort-Object -CaseSensitive |
            ForEach-Object {
                $Path = Resolve-MystiaRequiredFile `
                    -Path (Join-Path $Root $_) `
                    -Label "test release asset"
                $Hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant()
                "$Hash  $_"
            }
    )
    Write-MystiaUtf8WithoutBom `
        -Path (Join-Path $Root $script:MystiaReleaseChecksumsName) `
        -Content (($Lines -join "`n") + "`n")
}

function Write-TestMetadataFixture {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][object]$Fixture,
        [bool]$IncludeAndroid = $false
    )

    $CatalogPath = Join-Path $Root $script:MystiaReleaseCatalogName
    $ManifestPath = Join-Path $Root $script:MystiaReleaseManifestName
    Write-TestJson -Path $CatalogPath -Value $Fixture.Catalog
    $PackagePath = Join-Path $Root $script:MystiaReleasePackageName
    $Fixture.Manifest.packageSha256 =
        (Get-FileHash -Algorithm SHA256 -LiteralPath $PackagePath).Hash.ToLowerInvariant()
    $Fixture.Manifest.packageSize = (Get-Item -LiteralPath $PackagePath).Length
    $Fixture.Manifest.catalogSha256 =
        (Get-FileHash -Algorithm SHA256 -LiteralPath $CatalogPath).Hash.ToLowerInvariant()
    $Fixture.Manifest.catalogSize = (Get-Item -LiteralPath $CatalogPath).Length
    Write-TestJson -Path $ManifestPath -Value $Fixture.Manifest
    Write-TestChecksums -Root $Root -IncludeAndroid $IncludeAndroid
}

function New-TestMetadataFixture {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [bool]$IncludeAndroid = $false
    )

    if (Test-Path -LiteralPath $Root) {
        Remove-Item -LiteralPath $Root -Recurse -Force
    }
    [void](New-Item -ItemType Directory -Path $Root)
    Write-MystiaUtf8WithoutBom `
        -Path (Join-Path $Root $script:MystiaReleasePackageName) `
        -Content "package"
    Write-MystiaUtf8WithoutBom `
        -Path (Join-Path $Root $script:MystiaReleaseCompanionName) `
        -Content "companion"
    if ($IncludeAndroid) {
        foreach ($AndroidName in $script:MystiaReleaseAndroidNames) {
            Write-MystiaUtf8WithoutBom `
                -Path (Join-Path $Root $AndroidName) `
                -Content "android:$AndroidName"
        }
    }
    $Timestamp = "2026-08-18T01:02:03.004Z"
    $Owner = [ordered]@{
        version = "9.9.9"
        tag = $script:Tag
        title = $script:Tag
        channel = "stable"
        publishedAtUtc = $Timestamp
        releaseUrl = "https://github.com/$script:Repo/releases/tag/$script:Tag"
        notesMarkdown = "notes"
    }
    $Fixture = [pscustomobject]@{
        Catalog = [ordered]@{
            schemaVersion = 1
            generatedAtUtc = $Timestamp
            repository = $script:Repo
            ownerVersion = "9.9.9"
            ownerTag = $script:Tag
            releases = [object[]]@($Owner)
        }
        Manifest = [ordered]@{
            schemaVersion = 1
            version = "9.9.9"
            tag = $script:Tag
            channel = "stable"
            targetCommitSha = "9" * 40
            packageAsset = $script:MystiaReleasePackageName
            packageSha256 = ""
            packageSize = 0
            catalogAsset = $script:MystiaReleaseCatalogName
            catalogSha256 = ""
            catalogSize = 0
            releaseUrl = "https://github.com/$script:Repo/releases/tag/$script:Tag"
            publishedAtUtc = $Timestamp
        }
    }
    Write-TestMetadataFixture `
        -Root $Root `
        -Fixture $Fixture `
        -IncludeAndroid $IncludeAndroid
    return $Fixture
}

function Assert-TestMetadataFixture {
    param([Parameter(Mandatory = $true)][string]$Root)

    Assert-MystiaPreparedMetadata `
        -AssetRoot $Root `
        -RemoteReleases ([object[]]@()) `
        -Version "9.9.9" `
        -Channel "stable" `
        -ResolvedTitle $script:Tag `
        -Notes "notes" `
        -ExpectedTargetSha ("9" * 40) `
        -IncludeAndroid $false
}

$CanonicalMimeAssetNames = @(Get-MystiaReleaseAssetNames -IncludeAndroid $true)
if ($CanonicalMimeAssetNames.Count -ne 7) {
    throw "The canonical Release MIME contract must contain exactly seven assets."
}
foreach ($CanonicalAssetName in $CanonicalMimeAssetNames) {
    $ExpectedContentType = Get-TestReleaseAssetContentType -AssetName $CanonicalAssetName
    $ActualContentType = Get-MystiaReleaseAssetContentType -AssetName $CanonicalAssetName
    if ($ActualContentType -cne $ExpectedContentType) {
        throw "Canonical Release MIME mismatch for $CanonicalAssetName."
    }
}
foreach ($UnknownAssetName in @(
    "unknown-release-asset.bin",
    "MYSTIA-STEWARD-COMPANION-BEPINEX.ZIP"
)) {
    Assert-ThrowsLike `
        -Action {
            [void](Get-MystiaReleaseAssetContentType -AssetName $UnknownAssetName)
        } `
        -Pattern "*Unknown canonical Release asset name*" `
        -Label "Unknown Release MIME asset $UnknownAssetName"
}

$EmptyHistoryResult = Get-MystiaReleaseByTag `
    -Releases ([object[]]@()) `
    -ReleaseTag $script:Tag
if ($null -ne $EmptyHistoryResult) {
    throw "An empty repository Release history must return null."
}

$EmptyDraft = [pscustomobject]@{
    id = 1L
    upload_url = "https://uploads.github.com/repos/$script:Repo/releases/1/assets{?name,label}"
    tag_name = $script:Tag
    name = $script:Tag
    body = "notes"
    draft = $true
    prerelease = $false
    immutable = $false
    assets = @()
}
Assert-MystiaRemoteRelease `
    -Release $EmptyDraft `
    -ExpectedReleaseId 1 `
    -AssetRoot $RepoRoot `
    -ExpectedDraft $true `
    -ExpectedPrerelease $false `
    -ExpectedTitle $script:Tag `
    -ExpectedNotes "notes" `
    -ExpectedAssetNames ([string[]]@()) `
    -RequireImmutable $false

$StringDraft = $EmptyDraft.PSObject.Copy()
$StringDraft.draft = "false"
Assert-ThrowsLike `
    -Action {
        Assert-MystiaRemoteRelease `
            -Release $StringDraft `
            -ExpectedReleaseId 1 `
            -AssetRoot $RepoRoot `
            -ExpectedDraft $true `
            -ExpectedPrerelease $false `
            -ExpectedTitle $script:Tag `
            -ExpectedNotes "notes" `
            -ExpectedAssetNames ([string[]]@()) `
            -RequireImmutable $false
    } `
    -Pattern "*Remote Release draft must be a JSON boolean*" `
    -Label "String draft flag"

$StringImmutable = $EmptyDraft.PSObject.Copy()
$StringImmutable.immutable = "false"
Assert-ThrowsLike `
    -Action {
        Assert-MystiaRemoteRelease `
            -Release $StringImmutable `
            -ExpectedReleaseId 1 `
            -AssetRoot $RepoRoot `
            -ExpectedDraft $true `
            -ExpectedPrerelease $false `
            -ExpectedTitle $script:Tag `
            -ExpectedNotes "notes" `
            -ExpectedAssetNames ([string[]]@()) `
            -RequireImmutable $true
    } `
    -Pattern "*Remote Release immutable must be a JSON boolean*" `
    -Label "String immutable flag"

$WrongUploadUrl = $EmptyDraft.PSObject.Copy()
$WrongUploadUrl.upload_url =
    "https://uploads.github.com/repos/$script:Repo/releases/2/assets{?name,label}"
Assert-ThrowsLike `
    -Action {
        Assert-MystiaRemoteRelease `
            -Release $WrongUploadUrl `
            -ExpectedReleaseId 1 `
            -AssetRoot $RepoRoot `
            -ExpectedDraft $true `
            -ExpectedPrerelease $false `
            -ExpectedTitle $script:Tag `
            -ExpectedNotes "notes" `
            -ExpectedAssetNames ([string[]]@()) `
            -RequireImmutable $false
    } `
    -Pattern "*numeric id or upload URL does not match*" `
    -Label "Mismatched upload URL"

$TimestampDocument = '{"publishedAtUtc":"2026-08-18T01:02:03.004Z"}' |
    ConvertFrom-Json -DateKind String
if ($TimestampDocument.publishedAtUtc -isnot [string]) {
    throw "ConvertFrom-Json -DateKind String did not preserve timestamp text."
}
Assert-MystiaCanonicalUtcTimestamp `
    -Value $TimestampDocument.publishedAtUtc `
    -Label "Fixture timestamp"

$AuditRoot = Join-Path `
    ([System.IO.Path]::GetTempPath()) `
    "mystia-release-runtime-$([Guid]::NewGuid().ToString('N'))"
try {
    [void](New-Item -ItemType Directory -Path $AuditRoot)
    $FailedCommand = Join-Path $AuditRoot "failed-command.ps1"
    $ObjectCommand = Join-Path $AuditRoot "object-command.ps1"
    $ZeroPageCommand = Join-Path $AuditRoot "zero-page-command.ps1"
    $NestedTagCommand = Join-Path $AuditRoot "nested-tag-command.ps1"
    $StringPolicyCommand = Join-Path $AuditRoot "string-policy-command.ps1"
    $FakeGhCommand = Join-Path $AuditRoot "fake-gh.ps1"
    $TransactionGhCommand = Join-Path $AuditRoot "transaction-gh.ps1"
    Write-MystiaUtf8WithoutBom -Path $FailedCommand -Content "exit 17`n"
    Write-MystiaUtf8WithoutBom -Path $ObjectCommand -Content "Write-Output '{}'`nexit 0`n"
    Write-MystiaUtf8WithoutBom -Path $ZeroPageCommand -Content "Write-Output '[]'`nexit 0`n"
    Write-MystiaUtf8WithoutBom `
        -Path $NestedTagCommand `
        -Content @"
Write-Output '[{"ref":"refs/tags/v9.9.9","object":[{"type":"commit","sha":"$("a" * 40)"}]}]'
exit 0
"@
    Write-MystiaUtf8WithoutBom `
        -Path $StringPolicyCommand `
        -Content "Write-Output '{`"enabled`":`"false`"}'`nexit 0`n"
    Write-MystiaUtf8WithoutBom `
        -Path $FakeGhCommand `
        -Content @'
$StatePath = $env:MYSTIA_FAKE_GH_STATE
$Count = if (Test-Path -LiteralPath $StatePath) {
    [int](Get-Content -Raw -LiteralPath $StatePath)
}
else {
    0
}
$Count++
[System.IO.File]::WriteAllText($StatePath, $Count.ToString())

function New-ReleaseDocument {
    param(
        [long]$Id,
        [string]$Tag,
        [bool]$Draft,
        [bool]$Immutable,
        [bool]$IncludeAsset
    )

    $Assets = [object[]]@()
    if ($IncludeAsset) {
        $Assets = [object[]]@([ordered]@{
            id = 700L
            name = $env:MYSTIA_FAKE_GH_ASSET_NAME
            state = "uploaded"
            content_type = "application/zip"
            size = [long]$env:MYSTIA_FAKE_GH_ASSET_SIZE
            digest = $env:MYSTIA_FAKE_GH_ASSET_DIGEST
            url = "https://api.github.com/repos/$env:MYSTIA_FAKE_GH_REPO/releases/assets/700"
        })
    }
    return [ordered]@{
        id = $Id
        upload_url = "https://uploads.github.com/repos/$env:MYSTIA_FAKE_GH_REPO/releases/$Id/assets{?name,label}"
        tag_name = $Tag
        name = $Tag
        body = "notes"
        draft = $Draft
        prerelease = $false
        immutable = $Immutable
        assets = $Assets
    }
}

$Mode = $env:MYSTIA_FAKE_GH_MODE
if ($Mode -like "upload-*") {
    if (-not [string]::IsNullOrWhiteSpace($env:MYSTIA_FAKE_GH_LOG)) {
        [System.IO.File]::WriteAllText(
            $env:MYSTIA_FAKE_GH_LOG,
            ($args | ConvertTo-Json -Compress)
        )
    }
    if ($Mode -ceq "upload-fail") {
        exit 17
    }
    if ($Mode -ceq "upload-invalid") {
        Write-Output '[]'
        exit 0
    }
    $Response = [ordered]@{
        id = 700L
        name = $env:MYSTIA_FAKE_GH_ASSET_NAME
        state = "uploaded"
        content_type = $(if ($Mode -ceq "upload-wrong-mime") {
            "application/octet-stream"
        }
        else {
            "application/zip"
        })
        size = [long]$env:MYSTIA_FAKE_GH_ASSET_SIZE
        digest = $env:MYSTIA_FAKE_GH_ASSET_DIGEST
        url = "https://api.github.com/repos/$env:MYSTIA_FAKE_GH_REPO/releases/assets/700"
    }
    Write-Output ($Response | ConvertTo-Json -Compress)
    exit 0
}

$Endpoint = if ($args.Count -ge 2) { [string]$args[$args.Count - 1] } else { "" }
if ($Mode -like "tag-*") {
    if ($Count -eq 1 -and $Mode -ceq "tag-delay") {
        Write-Output 'gh: Not Found (HTTP 404)'
        exit 1
    }
    $Sha = if ($Mode -ceq "tag-mismatch") { "8" * 40 } else { $env:MYSTIA_FAKE_GH_SHA }
    $Response = [ordered]@{
        ref = "refs/tags/$env:MYSTIA_FAKE_GH_TAG"
        object = [ordered]@{ type = "commit"; sha = $Sha }
    }
    Write-Output ($Response | ConvertTo-Json -Compress)
    exit 0
}

if ($Mode -ceq "malformed") {
    Write-Output '[]'
    exit 0
}
if ($Mode -ceq "read-forbidden") {
    Write-Output 'gh: Resource not accessible (HTTP 403)'
    exit 1
}
if ($Mode -ceq "created-delay" -and $Count -lt 3) {
    Write-Output 'gh: Not Found (HTTP 404)'
    exit 1
}
if ($Mode -ceq "latest-delay" -or
    $Mode -ceq "latest-state-delay" -or
    $Mode -ceq "latest-never") {
    if (($Mode -ceq "latest-delay" -and $Count -ge 2) -or
        $Mode -ceq "latest-state-delay") {
        $Response = New-ReleaseDocument `
            -Id 42 `
            -Tag $env:MYSTIA_FAKE_GH_TAG `
            -Draft $false `
            -Immutable ($Mode -cne "latest-state-delay" -or $Count -ge 2) `
            -IncludeAsset $true
    }
    else {
        $Response = New-ReleaseDocument `
            -Id 41 `
            -Tag "v9.9.8" `
            -Draft $false `
            -Immutable $true `
            -IncludeAsset $false
    }
    Write-Output ($Response | ConvertTo-Json -Depth 10 -Compress)
    exit 0
}

$Draft = $true
$Immutable = $false
$IncludeAsset = $false
switch ($Mode) {
    "uploaded-delay" {
        $IncludeAsset = $Count -ge 2
    }
    "uploaded-digest-delay" {
        $IncludeAsset = $true
    }
    "uploaded-wrong-mime" {
        $IncludeAsset = $true
    }
    "published-delay" {
        $IncludeAsset = $true
        if ($Count -ge 2) {
            $Draft = $false
        }
        if ($Count -ge 3) {
            $Immutable = $true
        }
    }
    "published-never" {
        $IncludeAsset = $true
        $Draft = $false
    }
}
$Response = New-ReleaseDocument `
    -Id 42 `
    -Tag $env:MYSTIA_FAKE_GH_TAG `
    -Draft $Draft `
    -Immutable $Immutable `
    -IncludeAsset $IncludeAsset
if ($Mode -ceq "uploaded-digest-delay" -and $Count -eq 1) {
    $Response.assets[0].digest = $null
}
if ($Mode -ceq "uploaded-wrong-mime") {
    $Response.assets[0].content_type = "application/octet-stream"
}
Write-Output ($Response | ConvertTo-Json -Depth 10 -Compress)
exit 0
'@
    Write-MystiaUtf8WithoutBom `
        -Path $TransactionGhCommand `
        -Content @'
$ErrorActionPreference = "Stop"
$StatePath = $env:MYSTIA_TRANSACTION_GH_STATE
$State = Get-Content -Raw -LiteralPath $StatePath | ConvertFrom-Json -Depth 100
$State.sequence = [int]$State.sequence + 1
$CommandArguments = [object[]]@($args)

function Save-State {
    [System.IO.File]::WriteAllText(
        $StatePath,
        (($State | ConvertTo-Json -Depth 100 -Compress) + [Environment]::NewLine),
        [System.Text.UTF8Encoding]::new($false)
    )
}

function Get-ArgumentValue {
    param([Parameter(Mandatory = $true)][string]$Name)

    for ($Index = 0; $Index -lt $CommandArguments.Count - 1; $Index++) {
        if ([string]$CommandArguments[$Index] -ceq $Name) {
            return [string]$CommandArguments[$Index + 1]
        }
    }
    return $null
}

function Get-OracleContentType {
    param([Parameter(Mandatory = $true)][string]$AssetName)

    switch -CaseSensitive ($AssetName) {
        "mystia-steward-companion-bepinex.zip" {
            return "application/zip"
        }
        "mystia-steward-companion-companion-windows-x64.exe" {
            return "application/x-msdownload"
        }
        "mystia-steward-companion-android-arm64-v8a.apk" {
            return "application/vnd.android.package-archive"
        }
        "mystia-steward-companion-android-armeabi-v7a.apk" {
            return "application/vnd.android.package-archive"
        }
        "update-manifest.json" {
            return "application/json"
        }
        "update-catalog.json" {
            return "application/json"
        }
        "SHA256SUMS.txt" {
            return "text/plain; charset=utf-8"
        }
        default {
            throw "Unknown fake-gh MIME oracle asset: $AssetName"
        }
    }
}

function Add-Mutation {
    param(
        [Parameter(Mandatory = $true)][string]$Kind,
        [Parameter(Mandatory = $true)][string]$Method,
        [Parameter(Mandatory = $true)][string]$Endpoint,
        [AllowNull()][object]$Body,
        [AllowNull()][string]$AssetName,
        [AllowNull()][string]$InputPath,
        [bool]$Failed = $false
    )

    $Mutation = [ordered]@{
        kind = $Kind
        method = $Method
        endpoint = $Endpoint
        body = $Body
        assetName = $AssetName
        inputPath = $InputPath
        failed = $Failed
        arguments = $CommandArguments
    }
    $State.mutations = [object[]]@($State.mutations) + [object]$Mutation
}

function Write-JsonAndExit {
    param([Parameter(Mandatory = $true)][object]$Value)

    Save-State
    Write-Output ($Value | ConvertTo-Json -Depth 100 -Compress)
    exit 0
}

function Write-ErrorAndExit {
    param(
        [Parameter(Mandatory = $true)][string]$Message,
        [Parameter(Mandatory = $true)][int]$Code
    )

    Save-State
    Write-Output $Message
    exit $Code
}

function New-ReleaseDocument {
    param(
        [long]$Id = 42L,
        [string]$ReleaseTag = $env:MYSTIA_TRANSACTION_GH_TAG,
        [bool]$Draft,
        [bool]$Immutable,
        [int]$VisibleAssetCount = -1,
        [bool]$NullLastDigest = $false
    )

    $AllAssets = [object[]]@($State.assets)
    if ($VisibleAssetCount -lt 0 -or $VisibleAssetCount -gt $AllAssets.Count) {
        $VisibleAssetCount = $AllAssets.Count
    }
    $VisibleAssets = [System.Collections.Generic.List[object]]::new()
    for ($Index = 0; $Index -lt $VisibleAssetCount; $Index++) {
        $Asset = $AllAssets[$Index].PSObject.Copy()
        if ($NullLastDigest -and $Index -eq $VisibleAssetCount - 1) {
            $Asset.digest = $null
        }
        $VisibleAssets.Add($Asset)
    }
    return [ordered]@{
        id = $Id
        upload_url = "https://uploads.github.com/repos/$env:MYSTIA_TRANSACTION_GH_REPO/releases/$Id/assets{?name,label}"
        tag_name = $ReleaseTag
        name = $(if ($Id -eq 42L) { $env:MYSTIA_TRANSACTION_GH_TITLE } else { $ReleaseTag })
        body = $(if ($Id -eq 42L) { $env:MYSTIA_TRANSACTION_GH_NOTES } else { "old notes" })
        draft = $Draft
        prerelease = $false
        immutable = $Immutable
        assets = $VisibleAssets.ToArray()
    }
}

$Method = "GET"
for ($Index = 0; $Index -lt $args.Count - 1; $Index++) {
    if ([string]$args[$Index] -ceq "--method") {
        $Method = [string]$args[$Index + 1]
    }
}
$Endpoint = ""
foreach ($Argument in $args) {
    $Candidate = [string]$Argument
    if ($Candidate -cmatch '^repos/' -or
        $Candidate -cmatch '^https://uploads\.github\.com/') {
        $Endpoint = $Candidate
        break
    }
}
$Call = [ordered]@{
    sequence = [int]$State.sequence
    method = $Method
    endpoint = $Endpoint
    arguments = $CommandArguments
}
$State.calls = [object[]]@($State.calls) + [object]$Call

if ($args.Count -ge 2 -and
    [string]$args[0] -ceq "repo" -and
    [string]$args[1] -ceq "view") {
    $State.officialTargetReads = [int]$State.officialTargetReads + 1
    Save-State
    Write-Output "main"
    exit 0
}
if ($args.Count -eq 0 -or [string]$args[0] -cne "api") {
    Write-ErrorAndExit -Message "unexpected fake-gh command" -Code 91
}
if ($Endpoint -cmatch '/releases/tags/' -or
    $Endpoint -cmatch '/git/matching-refs/' -or
    $CommandArguments -contains "graphql") {
    $State.forbiddenDiscoveryReads = [int]$State.forbiddenDiscoveryReads + 1
    Write-ErrorAndExit -Message "forbidden current-transaction discovery" -Code 92
}

if ($Endpoint -ceq "repos/$env:MYSTIA_TRANSACTION_GH_REPO/commits/main") {
    $State.officialTargetReads = [int]$State.officialTargetReads + 1
    Save-State
    Write-Output $env:MYSTIA_TRANSACTION_GH_SHA
    exit 0
}
if ($Endpoint -ceq "repos/$env:MYSTIA_TRANSACTION_GH_REPO/immutable-releases") {
    $State.immutablePolicyReads = [int]$State.immutablePolicyReads + 1
    Write-JsonAndExit -Value ([ordered]@{ enabled = $true })
}
if ($Endpoint -ceq "repos/$env:MYSTIA_TRANSACTION_GH_REPO/releases?per_page=100") {
    $State.releaseListReads = [int]$State.releaseListReads + 1
    if ($env:MYSTIA_TRANSACTION_GH_MODE -ceq "history-drift") {
        $HistoricalRelease = New-ReleaseDocument `
            -Id 41L `
            -ReleaseTag "v9.9.8" `
            -Draft $false `
            -Immutable $true `
            -VisibleAssetCount 0
        $HistoricalRelease.published_at = "2026-08-17T01:02:03Z"
        $HistoricalJson = $HistoricalRelease | ConvertTo-Json -Depth 100 -Compress
        Save-State
        Write-Output "[[$HistoricalJson]]"
        exit 0
    }
    Save-State
    Write-Output '[[]]'
    exit 0
}

if ($Method -ceq "POST" -and
    $Endpoint -ceq "repos/$env:MYSTIA_TRANSACTION_GH_REPO/git/refs") {
    $InputPath = Get-ArgumentValue -Name "--input"
    $Body = Get-Content -Raw -LiteralPath $InputPath | ConvertFrom-Json -Depth 20
    Add-Mutation `
        -Kind "tag-create" `
        -Method $Method `
        -Endpoint $Endpoint `
        -Body $Body `
        -InputPath $InputPath
    $State.tagCreated = $true
    Write-JsonAndExit -Value ([ordered]@{
        ref = "refs/tags/$env:MYSTIA_TRANSACTION_GH_TAG"
        object = [ordered]@{ type = "commit"; sha = $env:MYSTIA_TRANSACTION_GH_SHA }
    })
}
if ($Method -ceq "GET" -and
    $Endpoint -ceq "repos/$env:MYSTIA_TRANSACTION_GH_REPO/git/ref/tags/$env:MYSTIA_TRANSACTION_GH_TAG") {
    $State.tagReads = [int]$State.tagReads + 1
    if ([int]$State.tagReads -eq 1) {
        Write-ErrorAndExit -Message "gh: Not Found (HTTP 404)" -Code 1
    }
    Write-JsonAndExit -Value ([ordered]@{
        ref = "refs/tags/$env:MYSTIA_TRANSACTION_GH_TAG"
        object = [ordered]@{ type = "commit"; sha = $env:MYSTIA_TRANSACTION_GH_SHA }
    })
}

if ($Method -ceq "POST" -and
    $Endpoint -ceq "repos/$env:MYSTIA_TRANSACTION_GH_REPO/releases") {
    $InputPath = Get-ArgumentValue -Name "--input"
    $Body = Get-Content -Raw -LiteralPath $InputPath | ConvertFrom-Json -Depth 20
    Add-Mutation `
        -Kind "draft-create" `
        -Method $Method `
        -Endpoint $Endpoint `
        -Body $Body `
        -InputPath $InputPath
    $State.draftCreated = $true
    Write-JsonAndExit -Value (New-ReleaseDocument -Draft $true -Immutable $false -VisibleAssetCount 0)
}

$UploadEndpoint =
    "https://uploads.github.com/repos/$env:MYSTIA_TRANSACTION_GH_REPO/releases/42/assets"
if ($Method -ceq "POST" -and $Endpoint -ceq $UploadEndpoint) {
    $InputPath = Get-ArgumentValue -Name "--input"
    $RawName = Get-ArgumentValue -Name "--raw-field"
    $AssetName = if ($RawName -cmatch '^name=(.+)$') { $Matches[1] } else { "" }
    $OracleContentType = Get-OracleContentType -AssetName $AssetName
    $ExpectedContentTypeHeader = "Content-Type: $OracleContentType"
    $ContentTypeHeaders = @(
        $CommandArguments | Where-Object { [string]$_ -clike "Content-Type:*" }
    )
    $State.uploadMutations = [int]$State.uploadMutations + 1
    $ShouldFail =
        $env:MYSTIA_TRANSACTION_GH_MODE -ceq "fail-upload-4" -and
        [int]$State.uploadMutations -eq 4
    Add-Mutation `
        -Kind "upload" `
        -Method $Method `
        -Endpoint $Endpoint `
        -AssetName $AssetName `
        -InputPath $InputPath `
        -Failed $ShouldFail
    if ($ContentTypeHeaders.Count -ne 1 -or
        [string]$ContentTypeHeaders[0] -cne $ExpectedContentTypeHeader) {
        Write-ErrorAndExit -Message "unexpected asset Content-Type request header" -Code 94
    }
    if ($ShouldFail) {
        Write-ErrorAndExit -Message "simulated asset upload failure" -Code 17
    }
    $Item = Get-Item -LiteralPath $InputPath
    $AssetId = 700L + [long]$State.uploadMutations
    $Asset = [ordered]@{
        id = $AssetId
        name = $AssetName
        state = "uploaded"
        content_type = $(if (
            $env:MYSTIA_TRANSACTION_GH_MODE -ceq "wrong-mime-response" -and
            [int]$State.uploadMutations -eq 1
        ) {
            "application/octet-stream"
        }
        else {
            $OracleContentType
        })
        size = [long]$Item.Length
        digest = "sha256:$((Get-FileHash -Algorithm SHA256 -LiteralPath $InputPath).Hash.ToLowerInvariant())"
        url = "https://api.github.com/repos/$env:MYSTIA_TRANSACTION_GH_REPO/releases/assets/$AssetId"
    }
    $State.assets = [object[]]@($State.assets) + [object]$Asset
    Write-JsonAndExit -Value $Asset
}

if ($Method -ceq "GET" -and
    $Endpoint -ceq "repos/$env:MYSTIA_TRANSACTION_GH_REPO/releases/42") {
    if (-not $State.patched) {
        if (@($State.assets).Count -eq 0) {
            $State.createdReads = [int]$State.createdReads + 1
            if ([int]$State.createdReads -eq 1) {
                Write-ErrorAndExit -Message "gh: Not Found (HTTP 404)" -Code 1
            }
            Write-JsonAndExit -Value (New-ReleaseDocument -Draft $true -Immutable $false -VisibleAssetCount 0)
        }
        $State.uploadedReads = [int]$State.uploadedReads + 1
        if ([int]$State.uploadedReads -eq 1) {
            Write-JsonAndExit -Value (
                New-ReleaseDocument -Draft $true -Immutable $false -VisibleAssetCount 3
            )
        }
        if ([int]$State.uploadedReads -eq 2) {
            Write-JsonAndExit -Value (
                New-ReleaseDocument -Draft $true -Immutable $false -NullLastDigest $true
            )
        }
        Write-JsonAndExit -Value (New-ReleaseDocument -Draft $true -Immutable $false)
    }

    $State.publishedReads = [int]$State.publishedReads + 1
    if ([int]$State.publishedReads -eq 1) {
        Write-JsonAndExit -Value (New-ReleaseDocument -Draft $true -Immutable $false)
    }
    if ([int]$State.publishedReads -eq 2) {
        Write-JsonAndExit -Value (New-ReleaseDocument -Draft $false -Immutable $false)
    }
    Write-JsonAndExit -Value (New-ReleaseDocument -Draft $false -Immutable $true)
}

if ($Method -ceq "PATCH" -and
    $Endpoint -ceq "repos/$env:MYSTIA_TRANSACTION_GH_REPO/releases/42") {
    $InputPath = Get-ArgumentValue -Name "--input"
    $Body = Get-Content -Raw -LiteralPath $InputPath | ConvertFrom-Json -Depth 20
    Add-Mutation `
        -Kind "publish" `
        -Method $Method `
        -Endpoint $Endpoint `
        -Body $Body `
        -InputPath $InputPath
    $State.patchMutations = [int]$State.patchMutations + 1
    $State.patched = $true
    Write-JsonAndExit -Value (New-ReleaseDocument -Draft $false -Immutable $false)
}

if ($Method -ceq "GET" -and
    $Endpoint -ceq "repos/$env:MYSTIA_TRANSACTION_GH_REPO/releases/latest") {
    $State.latestReads = [int]$State.latestReads + 1
    if ([int]$State.latestReads -eq 1) {
        Write-JsonAndExit -Value (
            New-ReleaseDocument `
                -Id 41L `
                -ReleaseTag "v9.9.8" `
                -Draft $false `
                -Immutable $true `
                -VisibleAssetCount 0
        )
    }
    if ([int]$State.latestReads -eq 2) {
        Write-JsonAndExit -Value (New-ReleaseDocument -Draft $false -Immutable $false)
    }
    Write-JsonAndExit -Value (New-ReleaseDocument -Draft $false -Immutable $true)
}

Write-ErrorAndExit -Message "unexpected fake-gh endpoint: $Method $Endpoint" -Code 93
'@

    Assert-ThrowsLike `
        -Action { Assert-MystiaOfficialTarget -Gh $FailedCommand -ExpectedSha ("9" * 40) } `
        -Pattern "Unable to verify the GitHub default branch*" `
        -Label "Failed official target lookup"
    Assert-ThrowsLike `
        -Action { Assert-MystiaPreviewTarget -Git $FailedCommand -ExpectedSha ("9" * 40) } `
        -Pattern "Unable to resolve the checked-out branch*" `
        -Label "Failed preview branch lookup"
    Assert-ThrowsLike `
        -Action {
            Assert-MystiaTargetCommit `
                -RepoRoot (Join-Path $AuditRoot "missing-repository") `
                -TargetCommitSha ("9" * 40)
        } `
        -Pattern "Failed to resolve the checked-out Git commit*" `
        -Label "Failed HEAD lookup"
    Assert-ThrowsLike `
        -Action { [void](Get-MystiaRemoteTagRef -Gh $ObjectCommand -ReleaseTag $script:Tag) } `
        -Pattern "*non-array tag-reference document*" `
        -Label "Non-array tag response"
    Assert-ThrowsLike `
        -Action { [void](Get-MystiaRemoteTagRef -Gh $NestedTagCommand -ReleaseTag $script:Tag) } `
        -Pattern "*invalid tag-reference entry*" `
        -Label "Nested-array tag target"
    Assert-ThrowsLike `
        -Action { [void](Get-MystiaRepositoryReleases -Gh $ZeroPageCommand) } `
        -Pattern "*invalid zero-page Release-list document*" `
        -Label "Zero-page Release response"

    $FakeState = Join-Path $AuditRoot "fake-gh-state.txt"
    $FakeLog = Join-Path $AuditRoot "fake-gh-arguments.json"
    $FakeAssetRoot = Join-Path $AuditRoot "fake-assets"
    [void](New-Item -ItemType Directory -Path $FakeAssetRoot)
    $FakeAssetName = $script:MystiaReleasePackageName
    $FakeAssetPath = Join-Path $FakeAssetRoot $FakeAssetName
    Write-MystiaUtf8WithoutBom -Path $FakeAssetPath -Content "fake asset payload"
    $FakeAssetItem = Get-Item -LiteralPath $FakeAssetPath
    $FakeAssetDigest =
        "sha256:$((Get-FileHash -Algorithm SHA256 -LiteralPath $FakeAssetPath).Hash.ToLowerInvariant())"
    $env:MYSTIA_FAKE_GH_STATE = $FakeState
    $env:MYSTIA_FAKE_GH_LOG = $FakeLog
    $env:MYSTIA_FAKE_GH_REPO = $script:Repo
    $env:MYSTIA_FAKE_GH_TAG = $script:Tag
    $env:MYSTIA_FAKE_GH_SHA = "9" * 40
    $env:MYSTIA_FAKE_GH_ASSET_NAME = $FakeAssetName
    $env:MYSTIA_FAKE_GH_ASSET_SIZE = $FakeAssetItem.Length.ToString()
    $env:MYSTIA_FAKE_GH_ASSET_DIGEST = $FakeAssetDigest
    $TransactionState = Join-Path $AuditRoot "transaction-gh-state.json"
    $env:MYSTIA_TRANSACTION_GH_STATE = $TransactionState
    $env:MYSTIA_TRANSACTION_GH_REPO = $script:Repo
    $env:MYSTIA_TRANSACTION_GH_TAG = $script:Tag
    $env:MYSTIA_TRANSACTION_GH_SHA = "9" * 40
    $env:MYSTIA_TRANSACTION_GH_TITLE = $script:Tag
    $env:MYSTIA_TRANSACTION_GH_NOTES = "notes"

    function Reset-FakeGhState {
        param([Parameter(Mandatory = $true)][string]$Mode)

        Write-MystiaUtf8WithoutBom -Path $FakeState -Content "0"
        if (Test-Path -LiteralPath $FakeLog) {
            Remove-Item -LiteralPath $FakeLog -Force
        }
        $env:MYSTIA_FAKE_GH_MODE = $Mode
    }

    function Reset-TransactionGhState {
        param([Parameter(Mandatory = $true)][string]$Mode)

        $env:MYSTIA_TRANSACTION_GH_MODE = $Mode
        Write-TestJson -Path $TransactionState -Value ([ordered]@{
            sequence = 0
            calls = [object[]]@()
            mutations = [object[]]@()
            assets = [object[]]@()
            tagCreated = $false
            draftCreated = $false
            patched = $false
            tagReads = 0
            createdReads = 0
            uploadedReads = 0
            publishedReads = 0
            latestReads = 0
            releaseListReads = 0
            officialTargetReads = 0
            immutablePolicyReads = 0
            forbiddenDiscoveryReads = 0
            uploadMutations = 0
            patchMutations = 0
        })
    }

    function Wait-MystiaGitHubMutationInterval {}

    Reset-FakeGhState -Mode "created-delay"
    $CreatedRead = Wait-MystiaRemoteReleaseState `
        -Gh $FakeGhCommand `
        -ReleaseId 42 `
        -Phase "Created" `
        -AssetRoot $FakeAssetRoot `
        -ExpectedPrerelease $false `
        -ExpectedTitle $script:Tag `
        -ExpectedNotes "notes" `
        -ExpectedAssetNames ([string[]]@()) `
        -RequireImmutable $false `
        -MaxAttempts 3 `
        -RetryDelayMilliseconds 0
    if ([long]$CreatedRead.id -ne 42 -or [int](Get-Content -Raw -LiteralPath $FakeState) -ne 3) {
        throw "Direct-id creation read did not retry only the bounded 404 visibility window."
    }

    Reset-FakeGhState -Mode "uploaded-delay"
    $UploadedRead = Wait-MystiaRemoteReleaseState `
        -Gh $FakeGhCommand `
        -ReleaseId 42 `
        -Phase "Uploaded" `
        -AssetRoot $FakeAssetRoot `
        -ExpectedPrerelease $false `
        -ExpectedTitle $script:Tag `
        -ExpectedNotes "notes" `
        -ExpectedAssetNames ([string[]]@($FakeAssetName)) `
        -RequireImmutable $false `
        -MaxAttempts 2 `
        -RetryDelayMilliseconds 0
    if (@($UploadedRead.assets).Count -ne 1 -or
        [int](Get-Content -Raw -LiteralPath $FakeState) -ne 2) {
        throw "Direct-id upload read did not wait for the exact asset set to converge."
    }

    Reset-FakeGhState -Mode "uploaded-digest-delay"
    $UploadedDigestRead = Wait-MystiaRemoteReleaseState `
        -Gh $FakeGhCommand `
        -ReleaseId 42 `
        -Phase "Uploaded" `
        -AssetRoot $FakeAssetRoot `
        -ExpectedPrerelease $false `
        -ExpectedTitle $script:Tag `
        -ExpectedNotes "notes" `
        -ExpectedAssetNames ([string[]]@($FakeAssetName)) `
        -RequireImmutable $false `
        -MaxAttempts 2 `
        -RetryDelayMilliseconds 0
    if ([string]$UploadedDigestRead.assets[0].digest -cne $FakeAssetDigest -or
        [int](Get-Content -Raw -LiteralPath $FakeState) -ne 2) {
        throw "Direct-id upload read did not wait for a nullable digest to converge."
    }

    Reset-FakeGhState -Mode "uploaded-wrong-mime"
    Assert-ThrowsLike `
        -Action {
            [void](Wait-MystiaRemoteReleaseState `
                -Gh $FakeGhCommand `
                -ReleaseId 42 `
                -Phase "Uploaded" `
                -AssetRoot $FakeAssetRoot `
                -ExpectedPrerelease $false `
                -ExpectedTitle $script:Tag `
                -ExpectedNotes "notes" `
                -ExpectedAssetNames ([string[]]@($FakeAssetName)) `
                -RequireImmutable $false `
                -MaxAttempts 3 `
                -RetryDelayMilliseconds 0)
        } `
        -Pattern "*content type, size, state, or digest mismatch*" `
        -Label "Wrong remote asset MIME"
    if ([int](Get-Content -Raw -LiteralPath $FakeState) -ne 1) {
        throw "A wrong remote asset MIME must fail immediately without read retries."
    }

    Reset-FakeGhState -Mode "published-delay"
    $PublishedRead = Wait-MystiaRemoteReleaseState `
        -Gh $FakeGhCommand `
        -ReleaseId 42 `
        -Phase "Published" `
        -AssetRoot $FakeAssetRoot `
        -ExpectedPrerelease $false `
        -ExpectedTitle $script:Tag `
        -ExpectedNotes "notes" `
        -ExpectedAssetNames ([string[]]@($FakeAssetName)) `
        -RequireImmutable $true `
        -MaxAttempts 3 `
        -RetryDelayMilliseconds 0
    if ($PublishedRead.draft -or -not $PublishedRead.immutable -or
        [int](Get-Content -Raw -LiteralPath $FakeState) -ne 3) {
        throw "Direct-id publication read did not wait for draft and immutable state convergence."
    }

    Reset-FakeGhState -Mode "published-never"
    Assert-ThrowsLike `
        -Action {
            [void](Wait-MystiaRemoteReleaseState `
                -Gh $FakeGhCommand `
                -ReleaseId 42 `
                -Phase "Published" `
                -AssetRoot $FakeAssetRoot `
                -ExpectedPrerelease $false `
                -ExpectedTitle $script:Tag `
                -ExpectedNotes "notes" `
                -ExpectedAssetNames ([string[]]@($FakeAssetName)) `
                -RequireImmutable $true `
                -MaxAttempts 2 `
                -RetryDelayMilliseconds 0)
        } `
        -Pattern "*did not reach the exact Published state after 2 direct-id reads*" `
        -Label "Never-converging published Release"

    Reset-FakeGhState -Mode "malformed"
    Assert-ThrowsLike `
        -Action {
            [void](Wait-MystiaRemoteReleaseState `
                -Gh $FakeGhCommand `
                -ReleaseId 42 `
                -Phase "Created" `
                -AssetRoot $FakeAssetRoot `
                -ExpectedPrerelease $false `
                -ExpectedTitle $script:Tag `
                -ExpectedNotes "notes" `
                -ExpectedAssetNames ([string[]]@()) `
                -RequireImmutable $false `
                -MaxAttempts 3 `
                -RetryDelayMilliseconds 0)
        } `
        -Pattern "*invalid transaction identity shape*" `
        -Label "Malformed direct-id Release"
    if ([int](Get-Content -Raw -LiteralPath $FakeState) -ne 1) {
        throw "Malformed direct-id JSON must fail immediately without read retries."
    }

    Reset-FakeGhState -Mode "read-forbidden"
    Assert-ThrowsLike `
        -Action {
            [void](Wait-MystiaRemoteReleaseState `
                -Gh $FakeGhCommand `
                -ReleaseId 42 `
                -Phase "Created" `
                -AssetRoot $FakeAssetRoot `
                -ExpectedPrerelease $false `
                -ExpectedTitle $script:Tag `
                -ExpectedNotes "notes" `
                -ExpectedAssetNames ([string[]]@()) `
                -RequireImmutable $false `
                -MaxAttempts 3 `
                -RetryDelayMilliseconds 0)
        } `
        -Pattern "*HTTP 403*" `
        -Label "Forbidden direct-id Release read"
    if ([int](Get-Content -Raw -LiteralPath $FakeState) -ne 1) {
        throw "Non-404 direct-id failures must fail immediately without read retries."
    }

    Reset-FakeGhState -Mode "latest-delay"
    Wait-MystiaLatestRelease `
        -Gh $FakeGhCommand `
        -ExpectedReleaseId 42 `
        -AssetRoot $FakeAssetRoot `
        -ExpectedTitle $script:Tag `
        -ExpectedNotes "notes" `
        -ExpectedAssetNames ([string[]]@($FakeAssetName)) `
        -MaxAttempts 2 `
        -RetryDelayMilliseconds 0
    if ([int](Get-Content -Raw -LiteralPath $FakeState) -ne 2) {
        throw "Latest verification did not wait for the exact published Release id."
    }

    Reset-FakeGhState -Mode "latest-state-delay"
    Wait-MystiaLatestRelease `
        -Gh $FakeGhCommand `
        -ExpectedReleaseId 42 `
        -AssetRoot $FakeAssetRoot `
        -ExpectedTitle $script:Tag `
        -ExpectedNotes "notes" `
        -ExpectedAssetNames ([string[]]@($FakeAssetName)) `
        -MaxAttempts 2 `
        -RetryDelayMilliseconds 0
    if ([int](Get-Content -Raw -LiteralPath $FakeState) -ne 2) {
        throw "Latest verification did not wait for immutable state on the exact published id."
    }

    Reset-FakeGhState -Mode "latest-never"
    Assert-ThrowsLike `
        -Action {
            Wait-MystiaLatestRelease `
                -Gh $FakeGhCommand `
                -ExpectedReleaseId 42 `
                -AssetRoot $FakeAssetRoot `
                -ExpectedTitle $script:Tag `
                -ExpectedNotes "notes" `
                -ExpectedAssetNames ([string[]]@($FakeAssetName)) `
                -MaxAttempts 2 `
                -RetryDelayMilliseconds 0
        } `
        -Pattern "*was not set as Latest after 2 bounded reads*" `
        -Label "Never-converging Latest Release"

    Reset-FakeGhState -Mode "tag-delay"
    $ExactTag = Wait-MystiaExactRemoteTagRef `
        -Gh $FakeGhCommand `
        -ReleaseTag $script:Tag `
        -ExpectedSha ("9" * 40) `
        -MaxAttempts 2 `
        -RetryDelayMilliseconds 0
    if ($ExactTag.Sha -cne ("9" * 40) -or
        [int](Get-Content -Raw -LiteralPath $FakeState) -ne 2) {
        throw "Exact tag verification did not wait for the direct ref to become visible."
    }

    Reset-FakeGhState -Mode "tag-mismatch"
    Assert-ThrowsLike `
        -Action {
            [void](Wait-MystiaExactRemoteTagRef `
                -Gh $FakeGhCommand `
                -ReleaseTag $script:Tag `
                -ExpectedSha ("9" * 40) `
                -MaxAttempts 3 `
                -RetryDelayMilliseconds 0)
        } `
        -Pattern "*does not point directly to the locked release commit*" `
        -Label "Mismatched exact tag"
    if ([int](Get-Content -Raw -LiteralPath $FakeState) -ne 1) {
        throw "An exact tag identity mismatch must fail immediately without read retries."
    }

    $ExactUploadUrl =
        "https://uploads.github.com/repos/$script:Repo/releases/42/assets{?name,label}"
    Reset-FakeGhState -Mode "upload-success"
    Invoke-MystiaReleaseAssetUpload `
        -Gh $FakeGhCommand `
        -ReleaseId 42 `
        -UploadUrl $ExactUploadUrl `
        -AssetRoot $FakeAssetRoot `
        -AssetNames ([string[]]@($FakeAssetName))
    $LoggedUploadArgs = @(
        Get-Content -Raw -LiteralPath $FakeLog | ConvertFrom-Json
    )
    if ($LoggedUploadArgs[0] -cne "api" -or
        $LoggedUploadArgs[-1] -cne "https://uploads.github.com/repos/$script:Repo/releases/42/assets" -or
        $LoggedUploadArgs -notcontains "--raw-field" -or
        $LoggedUploadArgs -notcontains "name=$FakeAssetName" -or
        $LoggedUploadArgs -notcontains "--input" -or
        $LoggedUploadArgs -notcontains "Content-Type: application/zip" -or
        $LoggedUploadArgs -contains "release" -or
        $LoggedUploadArgs -contains "--clobber") {
        throw "Exact-id raw asset upload did not use the reviewed single-mutation gh api shape."
    }

    Reset-FakeGhState -Mode "upload-success"
    Assert-ThrowsLike `
        -Action {
            Invoke-MystiaReleaseAssetUpload `
                -Gh $FakeGhCommand `
                -ReleaseId 42 `
                -UploadUrl "https://uploads.github.com/repos/$script:Repo/releases/41/assets{?name,label}" `
                -AssetRoot $FakeAssetRoot `
                -AssetNames ([string[]]@($FakeAssetName))
        } `
        -Pattern "*upload URL does not match the created numeric Release transaction*" `
        -Label "Mismatched raw upload URL"
    if ([int](Get-Content -Raw -LiteralPath $FakeState) -ne 0) {
        throw "A mismatched upload URL must fail before any remote mutation."
    }

    $UnknownUploadName = "unknown-release-asset.bin"
    Write-MystiaUtf8WithoutBom `
        -Path (Join-Path $FakeAssetRoot $UnknownUploadName) `
        -Content "unknown"
    Reset-FakeGhState -Mode "upload-success"
    Assert-ThrowsLike `
        -Action {
            Invoke-MystiaReleaseAssetUpload `
                -Gh $FakeGhCommand `
                -ReleaseId 42 `
                -UploadUrl $ExactUploadUrl `
                -AssetRoot $FakeAssetRoot `
                -AssetNames ([string[]]@($UnknownUploadName))
        } `
        -Pattern "*Unknown canonical Release asset name*" `
        -Label "Unknown raw upload asset"
    if ([int](Get-Content -Raw -LiteralPath $FakeState) -ne 0) {
        throw "An unknown Release asset must fail before any remote mutation."
    }
    Assert-ThrowsLike `
        -Action {
            Assert-MystiaRemoteRelease `
                -Release $EmptyDraft `
                -ExpectedReleaseId 1 `
                -AssetRoot $FakeAssetRoot `
                -ExpectedDraft $true `
                -ExpectedPrerelease $false `
                -ExpectedTitle $script:Tag `
                -ExpectedNotes "notes" `
                -ExpectedAssetNames ([string[]]@($UnknownUploadName)) `
                -RequireImmutable $false
        } `
        -Pattern "*Unknown canonical Release asset name*" `
        -Label "Unknown terminal Release asset"

    Reset-FakeGhState -Mode "upload-wrong-mime"
    Assert-ThrowsLike `
        -Action {
            Invoke-MystiaReleaseAssetUpload `
                -Gh $FakeGhCommand `
                -ReleaseId 42 `
                -UploadUrl $ExactUploadUrl `
                -AssetRoot $FakeAssetRoot `
                -AssetNames ([string[]]@($FakeAssetName))
        } `
        -Pattern "*upload response does not exactly match the submitted asset*" `
        -Label "Wrong upload response MIME"
    if ([int](Get-Content -Raw -LiteralPath $FakeState) -ne 1) {
        throw "A wrong upload response MIME must fail after exactly one mutation."
    }

    Reset-FakeGhState -Mode "upload-fail"
    Assert-ThrowsLike `
        -Action {
            Invoke-MystiaReleaseAssetUpload `
                -Gh $FakeGhCommand `
                -ReleaseId 42 `
                -UploadUrl $ExactUploadUrl `
                -AssetRoot $FakeAssetRoot `
                -AssetNames ([string[]]@($FakeAssetName))
        } `
        -Pattern "*Release asset upload failed with exit code 17*" `
        -Label "Failed exact-id raw upload"
    if ([int](Get-Content -Raw -LiteralPath $FakeState) -ne 1) {
        throw "A failed upload mutation must never be retried."
    }

    $TransactionAssetRoot = Join-Path $AuditRoot "transaction-assets"
    [void](New-TestMetadataFixture `
        -Root $TransactionAssetRoot `
        -IncludeAndroid $true)
    $TransactionAssetNames = @(
        Get-MystiaReleaseAssetNames -IncludeAndroid $true
    )
    if ($TransactionAssetNames.Count -ne 7) {
        throw "The official stable transaction fixture must contain exactly seven release assets."
    }

    $UnknownTransactionAssetNames = [string[]]@($TransactionAssetNames)
    $UnknownTransactionAssetNames[0] = $UnknownUploadName
    Reset-TransactionGhState -Mode "success"
    Assert-ThrowsLike `
        -Action {
            [void](Invoke-MystiaReleasePublicationTransaction `
                -Gh $TransactionGhCommand `
                -AssetRoot $TransactionAssetRoot `
                -AssetNames $UnknownTransactionAssetNames `
                -Version "9.9.9" `
                -Channel "stable" `
                -ResolvedTitle $script:Tag `
                -Notes "notes" `
                -ExpectedTargetSha ("9" * 40) `
                -IncludeAndroid $true `
                -OfficialRelease $true `
                -ReadMaxAttempts 5 `
                -ReadRetryDelayMilliseconds 0)
        } `
        -Pattern "*Unknown canonical Release asset name*" `
        -Label "Unknown end-to-end transaction asset"
    $UnknownTransactionState =
        Get-Content -Raw -LiteralPath $TransactionState | ConvertFrom-Json -Depth 100
    if ([int]$UnknownTransactionState.sequence -ne 0 -or
        @($UnknownTransactionState.calls).Count -ne 0 -or
        @($UnknownTransactionState.mutations).Count -ne 0) {
        throw "An unknown transaction asset must fail before the tag mutation or any remote read."
    }

    $PreviousTransactionPolicyToken = $env:MYSTIA_RELEASE_POLICY_TOKEN
    $PreviousTransactionReleaseToken = $env:GH_TOKEN
    try {
        $env:MYSTIA_RELEASE_POLICY_TOKEN = "fixture-policy-token"
        $env:GH_TOKEN = "fixture-release-token"
        Reset-TransactionGhState -Mode "success"
        $TransactionPublished = Invoke-MystiaReleasePublicationTransaction `
            -Gh $TransactionGhCommand `
            -AssetRoot $TransactionAssetRoot `
            -AssetNames $TransactionAssetNames `
            -Version "9.9.9" `
            -Channel "stable" `
            -ResolvedTitle $script:Tag `
            -Notes "notes" `
            -ExpectedTargetSha ("9" * 40) `
            -IncludeAndroid $true `
            -OfficialRelease $true `
            -ReadMaxAttempts 5 `
            -ReadRetryDelayMilliseconds 0
    }
    finally {
        $env:MYSTIA_RELEASE_POLICY_TOKEN = $PreviousTransactionPolicyToken
        $env:GH_TOKEN = $PreviousTransactionReleaseToken
    }
    if ([long]$TransactionPublished.id -ne 42L -or
        $TransactionPublished.draft -or
        -not $TransactionPublished.immutable) {
        throw "The end-to-end transaction did not return the exact immutable published Release."
    }

    $TransactionSuccessState =
        Get-Content -Raw -LiteralPath $TransactionState | ConvertFrom-Json -Depth 100
    $ExpectedMutationKinds = [object[]]@(
        "tag-create",
        "draft-create",
        "upload", "upload", "upload", "upload", "upload", "upload", "upload",
        "publish"
    )
    $ActualMutations = @($TransactionSuccessState.mutations)
    if ($ActualMutations.Count -ne $ExpectedMutationKinds.Count) {
        throw "The end-to-end transaction did not perform exactly ten mutations."
    }
    for ($Index = 0; $Index -lt $ExpectedMutationKinds.Count; $Index++) {
        if ([string]$ActualMutations[$Index].kind -cne [string]$ExpectedMutationKinds[$Index]) {
            throw "The end-to-end mutation order changed at index $Index."
        }
    }
    if ([string]$ActualMutations[0].method -cne "POST" -or
        [string]$ActualMutations[0].endpoint -cne "repos/$script:Repo/git/refs" -or
        [string]$ActualMutations[0].body.ref -cne "refs/tags/$script:Tag" -or
        [string]$ActualMutations[0].body.sha -cne ("9" * 40)) {
        throw "The end-to-end tag mutation did not bind the exact ref and commit."
    }
    $DraftMutation = $ActualMutations[1]
    if ([string]$DraftMutation.method -cne "POST" -or
        [string]$DraftMutation.endpoint -cne "repos/$script:Repo/releases" -or
        [string]$DraftMutation.body.tag_name -cne $script:Tag -or
        [string]$DraftMutation.body.target_commitish -cne ("9" * 40) -or
        [string]$DraftMutation.body.name -cne $script:Tag -or
        [string]$DraftMutation.body.body -cne "notes" -or
        $DraftMutation.body.draft -ne $true -or
        $DraftMutation.body.prerelease -ne $false -or
        [string]$DraftMutation.body.make_latest -cne "false") {
        throw "The end-to-end Draft mutation body no longer matches the locked release request."
    }
    for ($AssetIndex = 0; $AssetIndex -lt $TransactionAssetNames.Count; $AssetIndex++) {
        $UploadMutation = $ActualMutations[$AssetIndex + 2]
        $ExpectedAssetName = $TransactionAssetNames[$AssetIndex]
        $ExpectedAssetContentType =
            Get-TestReleaseAssetContentType -AssetName $ExpectedAssetName
        $UploadArguments = @($UploadMutation.arguments)
        if ([string]$UploadMutation.method -cne "POST" -or
            [string]$UploadMutation.endpoint -cne "https://uploads.github.com/repos/$script:Repo/releases/42/assets" -or
            [string]$UploadMutation.assetName -cne $ExpectedAssetName -or
            [string]$UploadMutation.inputPath -cne (Join-Path $TransactionAssetRoot $ExpectedAssetName) -or
            $UploadArguments -notcontains "Accept: application/vnd.github+json" -or
            $UploadArguments -notcontains "X-GitHub-Api-Version: 2022-11-28" -or
            $UploadArguments -notcontains "Content-Type: $ExpectedAssetContentType" -or
            $UploadArguments -notcontains "--input" -or
            $UploadArguments -notcontains "--raw-field" -or
            $UploadArguments -notcontains "name=$ExpectedAssetName" -or
            $UploadArguments -contains "release" -or
            $UploadArguments -contains "--clobber") {
            throw "Stable asset upload $AssetIndex did not use the exact serialized raw API mutation."
        }
    }
    $PublishMutation = $ActualMutations[-1]
    if ([string]$PublishMutation.method -cne "PATCH" -or
        [string]$PublishMutation.endpoint -cne "repos/$script:Repo/releases/42" -or
        $PublishMutation.body.draft -ne $false -or
        $PublishMutation.body.prerelease -ne $false -or
        [string]$PublishMutation.body.make_latest -cne "true") {
        throw "The end-to-end publication PATCH no longer targets the exact numeric Release."
    }
    if ([int]$TransactionSuccessState.patchMutations -ne 1 -or
        [int]$TransactionSuccessState.uploadMutations -ne 7 -or
        [int]$TransactionSuccessState.releaseListReads -ne 1 -or
        [int]$TransactionSuccessState.createdReads -ne 2 -or
        [int]$TransactionSuccessState.uploadedReads -ne 4 -or
        [int]$TransactionSuccessState.publishedReads -ne 3 -or
        [int]$TransactionSuccessState.latestReads -ne 3 -or
        [int]$TransactionSuccessState.tagReads -ne 6 -or
        [int]$TransactionSuccessState.officialTargetReads -ne 2 -or
        [int]$TransactionSuccessState.immutablePolicyReads -ne 1 -or
        [int]$TransactionSuccessState.forbiddenDiscoveryReads -ne 0) {
        throw "The end-to-end bounded read/write convergence counts changed unexpectedly."
    }
    foreach ($Call in @($TransactionSuccessState.calls)) {
        $Arguments = @($Call.arguments)
        if ([string]$Call.endpoint -cmatch '/releases/tags/' -or
            [string]$Call.endpoint -cmatch '/git/matching-refs/' -or
            $Arguments -contains "graphql") {
            throw "The end-to-end transaction rediscovered its current Draft by tag, list, or GraphQL."
        }
    }

    $PreviousTransactionPolicyToken = $env:MYSTIA_RELEASE_POLICY_TOKEN
    $PreviousTransactionReleaseToken = $env:GH_TOKEN
    try {
        $env:MYSTIA_RELEASE_POLICY_TOKEN = "fixture-policy-token"
        $env:GH_TOKEN = "fixture-release-token"
        Reset-TransactionGhState -Mode "wrong-mime-response"
        Assert-ThrowsLike `
            -Action {
                [void](Invoke-MystiaReleasePublicationTransaction `
                    -Gh $TransactionGhCommand `
                    -AssetRoot $TransactionAssetRoot `
                    -AssetNames $TransactionAssetNames `
                    -Version "9.9.9" `
                    -Channel "stable" `
                    -ResolvedTitle $script:Tag `
                    -Notes "notes" `
                    -ExpectedTargetSha ("9" * 40) `
                    -IncludeAndroid $true `
                    -OfficialRelease $true `
                    -ReadMaxAttempts 5 `
                    -ReadRetryDelayMilliseconds 0)
            } `
            -Pattern "*upload response does not exactly match the submitted asset*" `
            -Label "End-to-end wrong asset MIME response"
    }
    finally {
        $env:MYSTIA_RELEASE_POLICY_TOKEN = $PreviousTransactionPolicyToken
        $env:GH_TOKEN = $PreviousTransactionReleaseToken
    }
    $WrongMimeTransactionState =
        Get-Content -Raw -LiteralPath $TransactionState | ConvertFrom-Json -Depth 100
    if (@($WrongMimeTransactionState.mutations).Count -ne 3 -or
        [int]$WrongMimeTransactionState.uploadMutations -ne 1 -or
        [int]$WrongMimeTransactionState.releaseListReads -ne 0 -or
        [int]$WrongMimeTransactionState.patchMutations -ne 0 -or
        $WrongMimeTransactionState.patched) {
        throw "A wrong upload response MIME must stop the complete transaction before later assets or PATCH."
    }

    $PreviousTransactionPolicyToken = $env:MYSTIA_RELEASE_POLICY_TOKEN
    $PreviousTransactionReleaseToken = $env:GH_TOKEN
    try {
        $env:MYSTIA_RELEASE_POLICY_TOKEN = "fixture-policy-token"
        $env:GH_TOKEN = "fixture-release-token"
        Reset-TransactionGhState -Mode "fail-upload-4"
        Assert-ThrowsLike `
            -Action {
                [void](Invoke-MystiaReleasePublicationTransaction `
                    -Gh $TransactionGhCommand `
                    -AssetRoot $TransactionAssetRoot `
                    -AssetNames $TransactionAssetNames `
                    -Version "9.9.9" `
                    -Channel "stable" `
                    -ResolvedTitle $script:Tag `
                    -Notes "notes" `
                    -ExpectedTargetSha ("9" * 40) `
                    -IncludeAndroid $true `
                    -OfficialRelease $true `
                    -ReadMaxAttempts 5 `
                    -ReadRetryDelayMilliseconds 0)
            } `
            -Pattern "*Release asset upload failed with exit code 17*" `
            -Label "Fourth stable asset upload failure"
    }
    finally {
        $env:MYSTIA_RELEASE_POLICY_TOKEN = $PreviousTransactionPolicyToken
        $env:GH_TOKEN = $PreviousTransactionReleaseToken
    }
    $TransactionFailureState =
        Get-Content -Raw -LiteralPath $TransactionState | ConvertFrom-Json -Depth 100
    $FailureMutations = @($TransactionFailureState.mutations)
    if ($FailureMutations.Count -ne 6 -or
        [string]$FailureMutations[0].kind -cne "tag-create" -or
        [string]$FailureMutations[1].kind -cne "draft-create" -or
        @($FailureMutations | Where-Object { $_.kind -ceq "upload" }).Count -ne 4 -or
        @($FailureMutations | Where-Object { $_.kind -ceq "publish" }).Count -ne 0 -or
        [int]$TransactionFailureState.patchMutations -ne 0 -or
        $TransactionFailureState.patched -or
        [int]$TransactionFailureState.releaseListReads -ne 0 -or
        [int]$TransactionFailureState.latestReads -ne 0) {
        throw "A failed asset mutation must stop the complete transaction before history checks or PATCH."
    }

    $PreviousTransactionPolicyToken = $env:MYSTIA_RELEASE_POLICY_TOKEN
    $PreviousTransactionReleaseToken = $env:GH_TOKEN
    try {
        $env:MYSTIA_RELEASE_POLICY_TOKEN = "fixture-policy-token"
        $env:GH_TOKEN = "fixture-release-token"
        Reset-TransactionGhState -Mode "history-drift"
        Assert-ThrowsLike `
            -Action {
                [void](Invoke-MystiaReleasePublicationTransaction `
                    -Gh $TransactionGhCommand `
                    -AssetRoot $TransactionAssetRoot `
                    -AssetNames $TransactionAssetNames `
                    -Version "9.9.9" `
                    -Channel "stable" `
                    -ResolvedTitle $script:Tag `
                    -Notes "notes" `
                    -ExpectedTargetSha ("9" * 40) `
                    -IncludeAndroid $true `
                    -OfficialRelease $true `
                    -ReadMaxAttempts 5 `
                    -ReadRetryDelayMilliseconds 0)
            } `
            -Pattern "*Published Release history changed after update-catalog.json was prepared*" `
            -Label "Fresh Release history drift"
    }
    finally {
        $env:MYSTIA_RELEASE_POLICY_TOKEN = $PreviousTransactionPolicyToken
        $env:GH_TOKEN = $PreviousTransactionReleaseToken
    }
    $HistoryFailureState =
        Get-Content -Raw -LiteralPath $TransactionState | ConvertFrom-Json -Depth 100
    if (@($HistoryFailureState.mutations).Count -ne 9 -or
        [int]$HistoryFailureState.uploadMutations -ne 7 -or
        [int]$HistoryFailureState.releaseListReads -ne 1 -or
        [int]$HistoryFailureState.patchMutations -ne 0 -or
        $HistoryFailureState.patched -or
        [int]$HistoryFailureState.publishedReads -ne 0 -or
        [int]$HistoryFailureState.latestReads -ne 0) {
        throw "Fresh history drift must stop the complete transaction immediately before PATCH."
    }

    $PreviousPolicyToken = $env:MYSTIA_RELEASE_POLICY_TOKEN
    $PreviousReleaseToken = $env:GH_TOKEN
    try {
        $env:MYSTIA_RELEASE_POLICY_TOKEN = "fixture-policy-token"
        $env:GH_TOKEN = "fixture-release-token"
        Assert-ThrowsLike `
            -Action { Assert-MystiaImmutableReleasesEnabled -Gh $StringPolicyCommand } `
            -Pattern "*Immutable Release policy enabled must be a JSON boolean*" `
            -Label "String immutable policy"
    }
    finally {
        $env:MYSTIA_RELEASE_POLICY_TOKEN = $PreviousPolicyToken
        $env:GH_TOKEN = $PreviousReleaseToken
    }

    $MetadataRoot = Join-Path $AuditRoot "metadata"
    $Fixture = New-TestMetadataFixture -Root $MetadataRoot
    Assert-TestMetadataFixture -Root $MetadataRoot

    $Fixture = New-TestMetadataFixture -Root $MetadataRoot
    $Fixture.Manifest.schemaVersion = "1"
    Write-TestJson `
        -Path (Join-Path $MetadataRoot $script:MystiaReleaseManifestName) `
        -Value $Fixture.Manifest
    Write-TestChecksums -Root $MetadataRoot
    Assert-ThrowsLike `
        -Action { Assert-TestMetadataFixture -Root $MetadataRoot } `
        -Pattern "*schemaVersion must be a JSON integer*" `
        -Label "String manifest schema"

    $Fixture = New-TestMetadataFixture -Root $MetadataRoot
    $Fixture.Manifest.version = [object[]]@("9.9.9")
    Write-TestJson `
        -Path (Join-Path $MetadataRoot $script:MystiaReleaseManifestName) `
        -Value $Fixture.Manifest
    Write-TestChecksums -Root $MetadataRoot
    Assert-ThrowsLike `
        -Action { Assert-TestMetadataFixture -Root $MetadataRoot } `
        -Pattern "*scalar fields have an invalid JSON type*" `
        -Label "Array manifest version"

    $Fixture = New-TestMetadataFixture -Root $MetadataRoot
    $Fixture.Catalog.releases = $Fixture.Catalog.releases[0]
    Write-TestMetadataFixture -Root $MetadataRoot -Fixture $Fixture
    Assert-ThrowsLike `
        -Action { Assert-TestMetadataFixture -Root $MetadataRoot } `
        -Pattern "*invalid JSON shape*" `
        -Label "Object catalog releases"

    $Fixture = New-TestMetadataFixture -Root $MetadataRoot
    $Fixture.Catalog.releases[0].version = "WRONG"
    Write-TestMetadataFixture -Root $MetadataRoot -Fixture $Fixture
    Assert-ThrowsLike `
        -Action { Assert-TestMetadataFixture -Root $MetadataRoot } `
        -Pattern "*owner entry does not match*" `
        -Label "Wrong catalog owner version"

    $Fixture = New-TestMetadataFixture -Root $MetadataRoot
    $Fixture.Catalog.releases[0].releaseUrl = "wrong"
    Write-TestMetadataFixture -Root $MetadataRoot -Fixture $Fixture
    Assert-ThrowsLike `
        -Action { Assert-TestMetadataFixture -Root $MetadataRoot } `
        -Pattern "*owner entry does not match*" `
        -Label "Wrong catalog owner URL"

    $Fixture = New-TestMetadataFixture -Root $MetadataRoot
    $Fixture.Catalog.releases[0].publishedAtUtc = "not-a-date"
    Write-TestMetadataFixture -Root $MetadataRoot -Fixture $Fixture
    Assert-ThrowsLike `
        -Action { Assert-TestMetadataFixture -Root $MetadataRoot } `
        -Pattern "*canonical UTC timestamp*" `
        -Label "Invalid catalog owner timestamp"
}
finally {
    foreach ($Name in @(
        "MYSTIA_FAKE_GH_STATE",
        "MYSTIA_FAKE_GH_LOG",
        "MYSTIA_FAKE_GH_REPO",
        "MYSTIA_FAKE_GH_TAG",
        "MYSTIA_FAKE_GH_SHA",
        "MYSTIA_FAKE_GH_ASSET_NAME",
        "MYSTIA_FAKE_GH_ASSET_SIZE",
        "MYSTIA_FAKE_GH_ASSET_DIGEST",
        "MYSTIA_FAKE_GH_MODE",
        "MYSTIA_TRANSACTION_GH_STATE",
        "MYSTIA_TRANSACTION_GH_REPO",
        "MYSTIA_TRANSACTION_GH_TAG",
        "MYSTIA_TRANSACTION_GH_SHA",
        "MYSTIA_TRANSACTION_GH_TITLE",
        "MYSTIA_TRANSACTION_GH_NOTES",
        "MYSTIA_TRANSACTION_GH_MODE"
    )) {
        Remove-Item -LiteralPath "Env:$Name" -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $AuditRoot) {
        Remove-Item -LiteralPath $AuditRoot -Recurse -Force
    }
}

& (Join-Path $PSScriptRoot "prepare-release-assets-transaction-audit.ps1")

Write-Host "PowerShell release runtime audit passed."
