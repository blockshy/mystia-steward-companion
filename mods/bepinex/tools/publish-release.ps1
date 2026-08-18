#requires -Version 7.5

<#
.SYNOPSIS
    创建一次不可覆盖、绑定精确提交的 mystia-steward-companion GitHub Release。

.DESCRIPTION
    默认先调用本地构建和元数据准备流程；-SkipBuild 只接受已完整准备并通过
    SHA256SUMS.txt 校验的资产。脚本拒绝任何既有同名 tag 或 Release，不提供覆盖、编辑、
    删除或兼容旧资产的路径。-OfficialRelease 仅允许 main 上的稳定版本，并要求两个 APK。
#>
param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [string]$Title = "",
    [Parameter(Mandatory = $true)][string]$NotesFile,
    [Parameter(Mandatory = $true)][string]$TargetCommitSha,
    [switch]$OfficialRelease,
    [switch]$SkipBuild,
    [switch]$BuildAndroidApk,
    [ValidateRange(1, 1024)][int]$BuildCacheLimitGiB = 12,
    [ValidateRange(1, 1024)][int]$BuildCacheTargetGiB = 8,
    [switch]$SkipBuildCacheCleanup,
    [string]$ReferenceDir = "",
    [string]$Repo = "blockshy/mystia-steward-companion"
)

$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest

$ToolDir = $PSScriptRoot
$ModRoot = (Resolve-Path (Join-Path $ToolDir "..")).Path
$RepoRoot = (Resolve-Path (Join-Path $ModRoot "../..")).Path
$DistRoot = Join-Path $ModRoot "dist"
$BuildScript = Join-Path $ToolDir "build-release.ps1"
$PrepareScript = Join-Path $ToolDir "prepare-release-assets.ps1"
$CommonScript = Join-Path $ToolDir "release-common.ps1"
$UploadRoot = $null
. $CommonScript

function Get-MystiaRepositoryReleases {
    param(
        [Parameter(Mandatory = $true)][string]$Gh
    )

    $Raw = @(& $Gh api --paginate --slurp "repos/$Repo/releases?per_page=100")
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to read the complete GitHub Release list for $Repo."
    }
    try {
        $Pages = ($Raw -join [Environment]::NewLine) |
            ConvertFrom-Json -Depth 100 -NoEnumerate -DateKind String
    }
    catch {
        throw "GitHub returned an invalid Release list for $Repo`: $($_.Exception.Message)"
    }
    if ($null -eq $Pages -or
        $Pages -isnot [System.Array] -or
        $Pages.Count -eq 0) {
        throw "GitHub returned an invalid zero-page Release-list document for $Repo."
    }

    $Releases = [System.Collections.Generic.List[object]]::new()
    foreach ($Page in @($Pages)) {
        if ($null -eq $Page -or $Page -is [string] -or $Page -isnot [System.Collections.IEnumerable]) {
            throw "GitHub returned an invalid paginated Release-list shape for $Repo."
        }
        foreach ($Release in @($Page)) {
            if ($null -eq $Release -or
                $null -eq $Release.PSObject.Properties["tag_name"] -or
                $null -eq $Release.PSObject.Properties["id"] -or
                $null -eq $Release.PSObject.Properties["draft"] -or
                $null -eq $Release.PSObject.Properties["prerelease"] -or
                $null -eq $Release.PSObject.Properties["immutable"] -or
                $null -eq $Release.PSObject.Properties["assets"] -or
                $Release.tag_name -isnot [string] -or
                $Release.assets -isnot [System.Array]) {
                throw "GitHub returned an invalid Release entry for $Repo."
            }
            [void](Get-MystiaJsonPositiveInt64 -Value $Release.id -Label "GitHub Release id")
            Assert-MystiaJsonBoolean -Value $Release.draft -Label "GitHub Release draft"
            Assert-MystiaJsonBoolean -Value $Release.prerelease -Label "GitHub Release prerelease"
            Assert-MystiaJsonBoolean -Value $Release.immutable -Label "GitHub Release immutable"
            $Releases.Add($Release)
        }
    }
    return $Releases.ToArray()
}

function Get-MystiaReleaseByTag {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$Releases,
        [Parameter(Mandatory = $true)][string]$ReleaseTag
    )

    $Matches = @($Releases | Where-Object { [string]$_.tag_name -ceq $ReleaseTag })
    if ($Matches.Count -gt 1) {
        throw "GitHub returned duplicate Release entries for $ReleaseTag."
    }
    if ($Matches.Count -eq 0) {
        return $null
    }
    return $Matches[0]
}

function Assert-MystiaRemoteReleaseIdentity {
    param(
        [Parameter(Mandatory = $true)][object]$Release,
        [Parameter(Mandatory = $true)][long]$ExpectedReleaseId
    )

    if ($null -eq $Release -or
        $Release -is [System.Array] -or
        $null -eq $Release.PSObject.Properties["id"] -or
        $null -eq $Release.PSObject.Properties["upload_url"] -or
        $Release.upload_url -isnot [string]) {
        throw "Remote Release has an invalid transaction identity shape."
    }
    $ActualReleaseId = Get-MystiaJsonPositiveInt64 `
        -Value $Release.id `
        -Label "Remote Release id"
    $ExpectedUploadUrl =
        "https://uploads.github.com/repos/$Repo/releases/$ExpectedReleaseId/assets{?name,label}"
    if ($ActualReleaseId -ne $ExpectedReleaseId -or
        [string]$Release.upload_url -cne $ExpectedUploadUrl) {
        throw "Remote Release numeric id or upload URL does not match the created transaction."
    }
}

function Get-MystiaRemoteReleaseById {
    param(
        [Parameter(Mandatory = $true)][string]$Gh,
        [Parameter(Mandatory = $true)][long]$ReleaseId,
        [switch]$AllowUnavailable
    )

    $Raw = @(& $Gh api "repos/$Repo/releases/$ReleaseId" 2>&1)
    if ($LASTEXITCODE -ne 0) {
        $Diagnostic = (($Raw | ForEach-Object { $_.ToString() }) -join " ").Trim()
        if ($AllowUnavailable -and $Diagnostic -cmatch '\bHTTP 404\b') {
            return $null
        }
        throw "Unable to read GitHub Release id $ReleaseId for $Repo. Diagnostic: $Diagnostic"
    }
    try {
        $Release = ($Raw -join [Environment]::NewLine) |
            ConvertFrom-Json -Depth 100 -DateKind String -NoEnumerate
    }
    catch {
        throw "GitHub returned invalid data for Release id $ReleaseId`: $($_.Exception.Message)"
    }
    Assert-MystiaRemoteReleaseIdentity `
        -Release $Release `
        -ExpectedReleaseId $ReleaseId
    return $Release
}

function Get-MystiaRemoteTagRef {
    param(
        [Parameter(Mandatory = $true)][string]$Gh,
        [Parameter(Mandatory = $true)][string]$ReleaseTag
    )

    $EncodedTag = [System.Uri]::EscapeDataString($ReleaseTag)
    $Raw = @(& $Gh api "repos/$Repo/git/matching-refs/tags/$EncodedTag")
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to prove whether Git tag $ReleaseTag exists in $Repo."
    }
    try {
        $Refs = ($Raw -join [Environment]::NewLine) |
            ConvertFrom-Json -Depth 20 -NoEnumerate -DateKind String
    }
    catch {
        throw "GitHub returned invalid tag-reference data for $ReleaseTag`: $($_.Exception.Message)"
    }
    if ($null -eq $Refs) {
        throw "GitHub returned an empty tag-reference document for $ReleaseTag."
    }
    if ($Refs -isnot [System.Array]) {
        throw "GitHub returned a non-array tag-reference document for $ReleaseTag."
    }

    foreach ($Ref in $Refs) {
        if ($null -eq $Ref -or
            $Ref -is [System.Array] -or
            $Ref.ref -isnot [string] -or
            $null -eq $Ref.PSObject.Properties["object"] -or
            $null -eq $Ref.object -or
            $Ref.object -is [System.Array] -or
            $Ref.object.type -isnot [string] -or
            $Ref.object.sha -isnot [string]) {
            throw "GitHub returned an invalid tag-reference entry for $ReleaseTag."
        }
    }

    $ExpectedRef = "refs/tags/$ReleaseTag"
    $Matches = @($Refs | Where-Object { [string]$_.ref -ceq $ExpectedRef })
    if ($Matches.Count -gt 1) {
        throw "GitHub returned duplicate refs for $ExpectedRef."
    }
    if ($Matches.Count -eq 0) {
        return $null
    }
    $ObjectType = [string]$Matches[0].object.type
    $Sha = ([string]$Matches[0].object.sha).ToLowerInvariant()
    if ($ObjectType -cnotin @("commit", "tag") -or $Sha -cnotmatch '^[0-9a-f]{40}$') {
        throw "GitHub returned an invalid target for $ExpectedRef."
    }
    return @{
        Ref = $ExpectedRef
        ObjectType = $ObjectType
        Sha = $Sha
    }
}

function ConvertTo-MystiaExactTagRef {
    param(
        [Parameter(Mandatory = $true)][object]$Response,
        [Parameter(Mandatory = $true)][string]$ReleaseTag
    )

    $ExpectedRef = "refs/tags/$ReleaseTag"
    if ($null -eq $Response -or
        $Response -is [System.Array] -or
        $Response.ref -isnot [string] -or
        [string]$Response.ref -cne $ExpectedRef -or
        $null -eq $Response.PSObject.Properties["object"] -or
        $null -eq $Response.object -or
        $Response.object -is [System.Array] -or
        $Response.object.type -isnot [string] -or
        $Response.object.sha -isnot [string]) {
        throw "GitHub returned an invalid exact tag-reference object for $ExpectedRef."
    }
    $ObjectType = [string]$Response.object.type
    $Sha = ([string]$Response.object.sha).ToLowerInvariant()
    if ($ObjectType -cnotin @("commit", "tag") -or $Sha -cnotmatch '^[0-9a-f]{40}$') {
        throw "GitHub returned an invalid target for $ExpectedRef."
    }
    return @{
        Ref = $ExpectedRef
        ObjectType = $ObjectType
        Sha = $Sha
    }
}

function Get-MystiaExactRemoteTagRef {
    param(
        [Parameter(Mandatory = $true)][string]$Gh,
        [Parameter(Mandatory = $true)][string]$ReleaseTag,
        [switch]$AllowUnavailable
    )

    $EncodedTag = [System.Uri]::EscapeDataString($ReleaseTag)
    $Raw = @(& $Gh api "repos/$Repo/git/ref/tags/$EncodedTag" 2>&1)
    if ($LASTEXITCODE -ne 0) {
        $Diagnostic = (($Raw | ForEach-Object { $_.ToString() }) -join " ").Trim()
        if ($AllowUnavailable -and $Diagnostic -cmatch '\bHTTP 404\b') {
            return $null
        }
        throw "Unable to read the exact Git tag $ReleaseTag in $Repo. Diagnostic: $Diagnostic"
    }
    try {
        $Response = ($Raw -join [Environment]::NewLine) |
            ConvertFrom-Json -Depth 20 -NoEnumerate -DateKind String
    }
    catch {
        throw "GitHub returned invalid exact tag-reference data for $ReleaseTag`: $($_.Exception.Message)"
    }
    return ConvertTo-MystiaExactTagRef -Response $Response -ReleaseTag $ReleaseTag
}

function Wait-MystiaExactRemoteTagRef {
    param(
        [Parameter(Mandatory = $true)][string]$Gh,
        [Parameter(Mandatory = $true)][string]$ReleaseTag,
        [Parameter(Mandatory = $true)][string]$ExpectedSha,
        [ValidateRange(1, 30)][int]$MaxAttempts = 20,
        [ValidateRange(0, 5000)][int]$RetryDelayMilliseconds = 1000
    )

    for ($Attempt = 1; $Attempt -le $MaxAttempts; $Attempt++) {
        $TagRef = Get-MystiaExactRemoteTagRef `
            -Gh $Gh `
            -ReleaseTag $ReleaseTag `
            -AllowUnavailable
        if ($null -ne $TagRef) {
            if ($TagRef.ObjectType -cne "commit" -or $TagRef.Sha -cne $ExpectedSha) {
                throw "Exact Git tag $ReleaseTag does not point directly to the locked release commit."
            }
            return $TagRef
        }
        if ($Attempt -lt $MaxAttempts -and $RetryDelayMilliseconds -gt 0) {
            Start-Sleep -Milliseconds $RetryDelayMilliseconds
        }
    }
    throw "Exact Git tag $ReleaseTag was not observable after $MaxAttempts bounded direct-ref reads."
}

function Assert-MystiaRemoteReleaseIdentityAbsent {
    param(
        [Parameter(Mandatory = $true)][string]$Gh,
        [Parameter(Mandatory = $true)][string]$ReleaseTag
    )

    $Releases = @(Get-MystiaRepositoryReleases -Gh $Gh)
    if ($null -ne (Get-MystiaReleaseByTag -Releases $Releases -ReleaseTag $ReleaseTag)) {
        throw "Release already exists and will not be overwritten: $ReleaseTag"
    }
    $TagRef = Get-MystiaRemoteTagRef -Gh $Gh -ReleaseTag $ReleaseTag
    if ($null -ne $TagRef) {
        throw "Git tag already exists and will not be reused or overwritten: $ReleaseTag ($($TagRef.Sha))"
    }
}

function Assert-MystiaOfficialTarget {
    param(
        [Parameter(Mandatory = $true)][string]$Gh,
        [Parameter(Mandatory = $true)][string]$ExpectedSha
    )

    $DefaultBranchRaw = @(& $Gh repo view $Repo --json defaultBranchRef --jq '.defaultBranchRef.name')
    $GhExitCode = $LASTEXITCODE
    if ($GhExitCode -ne 0 -or $DefaultBranchRaw.Count -ne 1 -or $DefaultBranchRaw[0] -isnot [string]) {
        throw "Unable to verify the GitHub default branch for $Repo."
    }
    $DefaultBranch = ([string]$DefaultBranchRaw[0]).Trim()
    if ($DefaultBranch -cne "main") {
        throw "Official releases require the GitHub default branch to be main. Actual: $DefaultBranch"
    }
    $MainShaRaw = @(& $Gh api "repos/$Repo/commits/main" --jq '.sha')
    $GhExitCode = $LASTEXITCODE
    if ($GhExitCode -ne 0 -or $MainShaRaw.Count -ne 1 -or $MainShaRaw[0] -isnot [string]) {
        throw "Unable to verify the current main commit for $Repo."
    }
    $MainSha = ([string]$MainShaRaw[0]).Trim().ToLowerInvariant()
    if ($MainSha -cnotmatch '^[0-9a-f]{40}$' -or $MainSha -cne $ExpectedSha) {
        throw "origin/main moved or does not match the locked release commit. main=$MainSha, target=$ExpectedSha"
    }
}

function Assert-MystiaOfficialActionsContext {
    param([Parameter(Mandatory = $true)][string]$ExpectedSha)

    if ($env:GITHUB_ACTIONS -cne "true" -or
        $env:GITHUB_REF -cne "refs/heads/main" -or
        $env:GITHUB_SHA -cne $ExpectedSha -or
        $env:GITHUB_REPOSITORY -cne $Repo) {
        throw "OfficialRelease may only run in the locked GitHub Actions main context for $Repo."
    }
}

function Assert-MystiaRemoteRelease {
    param(
        [Parameter(Mandatory = $true)][object]$Release,
        [Parameter(Mandatory = $true)][long]$ExpectedReleaseId,
        [Parameter(Mandatory = $true)][string]$AssetRoot,
        [Parameter(Mandatory = $true)][bool]$ExpectedDraft,
        [Parameter(Mandatory = $true)][bool]$ExpectedPrerelease,
        [Parameter(Mandatory = $true)][string]$ExpectedTitle,
        [Parameter(Mandatory = $true)][string]$ExpectedNotes,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][string[]]$ExpectedAssetNames,
        [Parameter(Mandatory = $true)][bool]$RequireImmutable
    )

    Assert-MystiaRemoteReleaseMetadata `
        -Release $Release `
        -ExpectedReleaseId $ExpectedReleaseId `
        -ExpectedDraft $ExpectedDraft `
        -ExpectedPrerelease $ExpectedPrerelease `
        -ExpectedTitle $ExpectedTitle `
        -ExpectedNotes $ExpectedNotes `
        -RequireImmutable $RequireImmutable
    [void](Assert-MystiaRemoteReleaseAssets `
        -Release $Release `
        -AssetRoot $AssetRoot `
        -ExpectedAssetNames $ExpectedAssetNames `
        -AllowSubset $false)
}

function Assert-MystiaRemoteReleaseMetadata {
    param(
        [Parameter(Mandatory = $true)][object]$Release,
        [Parameter(Mandatory = $true)][long]$ExpectedReleaseId,
        [Parameter(Mandatory = $true)][bool]$ExpectedDraft,
        [Parameter(Mandatory = $true)][bool]$ExpectedPrerelease,
        [Parameter(Mandatory = $true)][string]$ExpectedTitle,
        [Parameter(Mandatory = $true)][string]$ExpectedNotes,
        [Parameter(Mandatory = $true)][bool]$RequireImmutable
    )

    Assert-MystiaRemoteReleaseIdentity `
        -Release $Release `
        -ExpectedReleaseId $ExpectedReleaseId
    if ($null -eq $Release.PSObject.Properties["draft"] -or
        $null -eq $Release.PSObject.Properties["prerelease"] -or
        $null -eq $Release.PSObject.Properties["immutable"] -or
        $null -eq $Release.PSObject.Properties["assets"] -or
        $Release.tag_name -isnot [string] -or
        $Release.name -isnot [string] -or
        $Release.body -isnot [string] -or
        $Release.assets -isnot [System.Array]) {
        throw "Remote Release has an invalid JSON shape."
    }
    Assert-MystiaJsonBoolean -Value $Release.draft -Label "Remote Release draft"
    Assert-MystiaJsonBoolean -Value $Release.prerelease -Label "Remote Release prerelease"
    Assert-MystiaJsonBoolean -Value $Release.immutable -Label "Remote Release immutable"

    if (
        [string]$Release.tag_name -cne $Tag -or
        [string]$Release.name -cne $ExpectedTitle -or
        [string]$Release.body -cne $ExpectedNotes -or
        $Release.draft -ne $ExpectedDraft -or
        $Release.prerelease -ne $ExpectedPrerelease) {
        throw "Remote Release metadata does not exactly match this release request."
    }
    if ($RequireImmutable -and $Release.immutable -ne $true) {
        throw "The published official Release is not immutable."
    }
    if ($ExpectedDraft -and $Release.immutable -ne $false) {
        throw "A draft Release must remain mutable until publication."
    }
}

function Assert-MystiaRemoteReleaseAssets {
    param(
        [Parameter(Mandatory = $true)][object]$Release,
        [Parameter(Mandatory = $true)][string]$AssetRoot,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][string[]]$ExpectedAssetNames,
        [Parameter(Mandatory = $true)][bool]$AllowSubset
    )

    $ExpectedPaths = [System.Collections.Generic.Dictionary[string, string]]::new(
        [System.StringComparer]::Ordinal
    )
    $ExpectedContentTypes = [System.Collections.Generic.Dictionary[string, string]]::new(
        [System.StringComparer]::Ordinal
    )
    foreach ($Name in $ExpectedAssetNames) {
        $ExpectedContentType = Get-MystiaReleaseAssetContentType -AssetName $Name
        if (-not $ExpectedPaths.TryAdd(
            $Name,
            (Resolve-MystiaRequiredFile -Path (Join-Path $AssetRoot $Name) -Label "release asset")
        )) {
            throw "Duplicate expected Release asset: $Name"
        }
        $ExpectedContentTypes.Add($Name, $ExpectedContentType)
    }

    $RemoteAssets = @($Release.assets)
    if ($RemoteAssets.Count -gt $ExpectedPaths.Count -or
        (-not $AllowSubset -and $RemoteAssets.Count -ne $ExpectedPaths.Count)) {
        throw "Remote Release asset count mismatch. Expected $($ExpectedPaths.Count), actual $($RemoteAssets.Count)."
    }
    $Seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    $AllAssetsComplete = $true
    foreach ($Asset in $RemoteAssets) {
        if ($null -eq $Asset -or
            $Asset -is [System.Array] -or
            $Asset.name -isnot [string] -or
            $Asset.state -isnot [string] -or
            $null -eq $Asset.PSObject.Properties["digest"] -or
            ($null -ne $Asset.digest -and $Asset.digest -isnot [string]) -or
            $Asset.content_type -isnot [string]) {
            throw "Remote Release contains an asset with an invalid JSON shape."
        }
        $Name = [string]$Asset.name
        if (-not $ExpectedPaths.ContainsKey($Name) -or -not $Seen.Add($Name)) {
            throw "Remote Release contains an unexpected or duplicate asset: $Name"
        }
        $Path = $ExpectedPaths[$Name]
        $ExpectedContentType = $ExpectedContentTypes[$Name]
        $Item = Get-Item -LiteralPath $Path
        $ExpectedDigest = "sha256:$((Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant())"
        if ($null -eq $Asset.PSObject.Properties["size"]) {
            throw "Remote Release asset has no size: $Name"
        }
        $RemoteSize = Get-MystiaJsonPositiveInt64 -Value $Asset.size -Label "Remote Release asset size"
        if ([string]$Asset.state -cne "uploaded" -or
            [string]$Asset.content_type -cne $ExpectedContentType -or
            $RemoteSize -ne $Item.Length) {
            throw "Remote Release asset content type, size, state, or digest mismatch: $Name"
        }
        if ($null -eq $Asset.digest) {
            if (-not $AllowSubset) {
                throw "Remote Release asset digest has not converged: $Name"
            }
            $AllAssetsComplete = $false
            continue
        }
        if ([string]$Asset.digest -cne $ExpectedDigest) {
            throw "Remote Release asset content type, size, state, or digest mismatch: $Name"
        }
    }
    return $RemoteAssets.Count -eq $ExpectedPaths.Count -and $AllAssetsComplete
}

function Wait-MystiaRemoteReleaseState {
    param(
        [Parameter(Mandatory = $true)][string]$Gh,
        [Parameter(Mandatory = $true)][long]$ReleaseId,
        [Parameter(Mandatory = $true)][ValidateSet("Created", "Uploaded", "Published")][string]$Phase,
        [Parameter(Mandatory = $true)][string]$AssetRoot,
        [Parameter(Mandatory = $true)][bool]$ExpectedPrerelease,
        [Parameter(Mandatory = $true)][string]$ExpectedTitle,
        [Parameter(Mandatory = $true)][string]$ExpectedNotes,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][string[]]$ExpectedAssetNames,
        [Parameter(Mandatory = $true)][bool]$RequireImmutable,
        [ValidateRange(1, 30)][int]$MaxAttempts = 20,
        [ValidateRange(0, 5000)][int]$RetryDelayMilliseconds = 1000
    )

    $LastAllowedState = "the exact numeric Release endpoint was unavailable"
    for ($Attempt = 1; $Attempt -le $MaxAttempts; $Attempt++) {
        $Release = Get-MystiaRemoteReleaseById `
            -Gh $Gh `
            -ReleaseId $ReleaseId `
            -AllowUnavailable
        if ($null -ne $Release) {
            if ($Phase -ceq "Created") {
                Assert-MystiaRemoteRelease `
                    -Release $Release `
                    -ExpectedReleaseId $ReleaseId `
                    -AssetRoot $AssetRoot `
                    -ExpectedDraft $true `
                    -ExpectedPrerelease $ExpectedPrerelease `
                    -ExpectedTitle $ExpectedTitle `
                    -ExpectedNotes $ExpectedNotes `
                    -ExpectedAssetNames $ExpectedAssetNames `
                    -RequireImmutable $false
                return $Release
            }

            if ($Phase -ceq "Uploaded") {
                Assert-MystiaRemoteReleaseMetadata `
                    -Release $Release `
                    -ExpectedReleaseId $ReleaseId `
                    -ExpectedDraft $true `
                    -ExpectedPrerelease $ExpectedPrerelease `
                    -ExpectedTitle $ExpectedTitle `
                    -ExpectedNotes $ExpectedNotes `
                    -RequireImmutable $false
                $AssetsComplete = Assert-MystiaRemoteReleaseAssets `
                    -Release $Release `
                    -AssetRoot $AssetRoot `
                    -ExpectedAssetNames $ExpectedAssetNames `
                    -AllowSubset $true
                if ($AssetsComplete) {
                    return $Release
                }
                $LastAllowedState = "only a valid incomplete uploaded asset state was visible"
            }
            else {
                Assert-MystiaJsonBoolean -Value $Release.draft -Label "Remote Release draft"
                if ($Release.draft) {
                    Assert-MystiaRemoteRelease `
                        -Release $Release `
                        -ExpectedReleaseId $ReleaseId `
                        -AssetRoot $AssetRoot `
                        -ExpectedDraft $true `
                        -ExpectedPrerelease $ExpectedPrerelease `
                        -ExpectedTitle $ExpectedTitle `
                        -ExpectedNotes $ExpectedNotes `
                        -ExpectedAssetNames $ExpectedAssetNames `
                        -RequireImmutable $false
                    $LastAllowedState = "the exact pre-publication draft was still visible"
                }
                else {
                    Assert-MystiaRemoteRelease `
                        -Release $Release `
                        -ExpectedReleaseId $ReleaseId `
                        -AssetRoot $AssetRoot `
                        -ExpectedDraft $false `
                        -ExpectedPrerelease $ExpectedPrerelease `
                        -ExpectedTitle $ExpectedTitle `
                        -ExpectedNotes $ExpectedNotes `
                        -ExpectedAssetNames $ExpectedAssetNames `
                        -RequireImmutable $false
                    if (-not $RequireImmutable -or $Release.immutable) {
                        return $Release
                    }
                    $LastAllowedState = "the published Release was not yet reported immutable"
                }
            }
        }

        if ($Attempt -lt $MaxAttempts -and $RetryDelayMilliseconds -gt 0) {
            Start-Sleep -Milliseconds $RetryDelayMilliseconds
        }
    }
    throw "Release $ReleaseId did not reach the exact $Phase state after $MaxAttempts direct-id reads; last allowed transient state: $LastAllowedState."
}

function Get-MystiaLatestRelease {
    param(
        [Parameter(Mandatory = $true)][string]$Gh,
        [switch]$AllowUnavailable
    )

    $Raw = @(& $Gh api "repos/$Repo/releases/latest" 2>&1)
    if ($LASTEXITCODE -ne 0) {
        $Diagnostic = (($Raw | ForEach-Object { $_.ToString() }) -join " ").Trim()
        if ($AllowUnavailable -and $Diagnostic -cmatch '\bHTTP 404\b') {
            return $null
        }
        throw "Unable to verify the Latest Release for $Repo. Diagnostic: $Diagnostic"
    }
    try {
        $Latest = ($Raw -join [Environment]::NewLine) |
            ConvertFrom-Json -Depth 100 -DateKind String -NoEnumerate
    }
    catch {
        throw "GitHub returned invalid Latest Release data: $($_.Exception.Message)"
    }
    if ($null -eq $Latest -or
        $Latest -is [System.Array] -or
        $Latest.tag_name -isnot [string] -or
        $null -eq $Latest.PSObject.Properties["id"]) {
        throw "GitHub returned an invalid Latest Release shape."
    }
    [void](Get-MystiaJsonPositiveInt64 -Value $Latest.id -Label "Latest Release id")
    return $Latest
}

function Wait-MystiaLatestRelease {
    param(
        [Parameter(Mandatory = $true)][string]$Gh,
        [Parameter(Mandatory = $true)][long]$ExpectedReleaseId,
        [Parameter(Mandatory = $true)][string]$AssetRoot,
        [Parameter(Mandatory = $true)][string]$ExpectedTitle,
        [Parameter(Mandatory = $true)][string]$ExpectedNotes,
        [Parameter(Mandatory = $true)][string[]]$ExpectedAssetNames,
        [ValidateRange(1, 30)][int]$MaxAttempts = 20,
        [ValidateRange(0, 5000)][int]$RetryDelayMilliseconds = 1000
    )

    $LastObserved = "unavailable"
    for ($Attempt = 1; $Attempt -le $MaxAttempts; $Attempt++) {
        $Latest = Get-MystiaLatestRelease -Gh $Gh -AllowUnavailable
        if ($null -ne $Latest) {
            $LatestId = Get-MystiaJsonPositiveInt64 `
                -Value $Latest.id `
                -Label "Latest Release id"
            $LatestTag = [string]$Latest.tag_name
            if ($LatestId -eq $ExpectedReleaseId -or $LatestTag -ceq $Tag) {
                if ($LatestId -ne $ExpectedReleaseId -or $LatestTag -cne $Tag) {
                    throw "Latest Release id and tag identify different release transactions."
                }
                Assert-MystiaRemoteReleaseMetadata `
                    -Release $Latest `
                    -ExpectedReleaseId $ExpectedReleaseId `
                    -ExpectedDraft $false `
                    -ExpectedPrerelease $false `
                    -ExpectedTitle $ExpectedTitle `
                    -ExpectedNotes $ExpectedNotes `
                    -RequireImmutable $false
                $LatestAssetsComplete = Assert-MystiaRemoteReleaseAssets `
                    -Release $Latest `
                    -AssetRoot $AssetRoot `
                    -ExpectedAssetNames $ExpectedAssetNames `
                    -AllowSubset $true
                if ($Latest.immutable -and $LatestAssetsComplete) {
                    return
                }
                $LastObserved =
                    "the exact Latest id was visible before immutable/assets converged"
            }
            else {
                $LastObserved = "id=$LatestId tag=$LatestTag"
            }
        }
        if ($Attempt -lt $MaxAttempts -and $RetryDelayMilliseconds -gt 0) {
            Start-Sleep -Milliseconds $RetryDelayMilliseconds
        }
    }
    throw "The published official stable Release was not set as Latest after $MaxAttempts bounded reads. Last observed: $LastObserved"
}

function Invoke-MystiaGitHubJsonRequest {
    param(
        [Parameter(Mandatory = $true)][string]$Gh,
        [Parameter(Mandatory = $true)][ValidateSet("POST", "PATCH")][string]$Method,
        [Parameter(Mandatory = $true)][string]$Endpoint,
        [Parameter(Mandatory = $true)][object]$Body,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $InputPath = [System.IO.Path]::GetTempFileName()
    try {
        Write-MystiaUtf8WithoutBom `
            -Path $InputPath `
            -Content (($Body | ConvertTo-Json -Depth 10 -Compress) + [Environment]::NewLine)
        $Raw = @(& $Gh api --method $Method $Endpoint --input $InputPath)
        if ($LASTEXITCODE -ne 0) {
            throw "$Label failed. The operation must not be retried without inspecting the remote tag and draft."
        }
        try {
            $Response = (($Raw -join [Environment]::NewLine) |
                ConvertFrom-Json -Depth 100 -DateKind String -NoEnumerate)
            if ($null -eq $Response -or $Response -is [System.Array]) {
                throw "$Label returned a non-object JSON response."
            }
            return $Response
        }
        catch {
            throw @(
                "$Label returned invalid JSON after the GitHub mutation request.",
                "The remote operation may already have succeeded.",
                "Do not retry this release; inspect the exact tag, draft, and assets first.",
                "Diagnostic: $($_.Exception.Message)"
            ) -join " "
        }
    }
    finally {
        Remove-Item -LiteralPath $InputPath -Force -ErrorAction SilentlyContinue
    }
}

function Assert-MystiaImmutableReleasesEnabled {
    param([Parameter(Mandatory = $true)][string]$Gh)

    $PolicyToken = $env:MYSTIA_RELEASE_POLICY_TOKEN
    if ([string]::IsNullOrWhiteSpace($PolicyToken)) {
        throw "Official releases require a read-only administration token for immutable-policy verification."
    }
    $ReleaseToken = $env:GH_TOKEN
    try {
        $env:GH_TOKEN = $PolicyToken
        $Raw = @(& $Gh api "repos/$Repo/immutable-releases")
        if ($LASTEXITCODE -ne 0) {
            throw "Unable to verify the immutable Release policy for $Repo."
        }
        try {
            $Policy = ($Raw -join [Environment]::NewLine) |
                ConvertFrom-Json -Depth 10 -DateKind String -NoEnumerate
        }
        catch {
            throw "GitHub returned invalid immutable Release policy data: $($_.Exception.Message)"
        }
    }
    finally {
        $env:GH_TOKEN = $ReleaseToken
    }
    if ($null -eq $Policy -or
        $Policy -is [System.Array] -or
        $null -eq $Policy.PSObject.Properties["enabled"]) {
        throw "GitHub returned an invalid immutable Release policy shape for $Repo."
    }
    Assert-MystiaJsonBoolean -Value $Policy.enabled -Label "Immutable Release policy enabled"
    if ($Policy.enabled -ne $true) {
        throw "Official releases require GitHub immutable releases to be enabled for $Repo."
    }
}

function New-MystiaRemoteTag {
    param(
        [Parameter(Mandatory = $true)][string]$Gh,
        [Parameter(Mandatory = $true)][string]$ExpectedSha,
        [ValidateRange(1, 30)][int]$ReadMaxAttempts = 20,
        [ValidateRange(0, 5000)][int]$ReadRetryDelayMilliseconds = 1000
    )

    $CreatedTag = Invoke-MystiaGitHubJsonRequest `
        -Gh $Gh `
        -Method "POST" `
        -Endpoint "repos/$Repo/git/refs" `
        -Body ([ordered]@{ ref = "refs/tags/$Tag"; sha = $ExpectedSha }) `
        -Label "Exact Git tag creation"
    $CreatedTagRef = ConvertTo-MystiaExactTagRef `
        -Response $CreatedTag `
        -ReleaseTag $Tag
    if ($CreatedTagRef.ObjectType -cne "commit" -or $CreatedTagRef.Sha -cne $ExpectedSha) {
        throw "Created Git tag response does not point directly to the locked release commit."
    }
    [void](Wait-MystiaExactRemoteTagRef `
        -Gh $Gh `
        -ReleaseTag $Tag `
        -ExpectedSha $ExpectedSha `
        -MaxAttempts $ReadMaxAttempts `
        -RetryDelayMilliseconds $ReadRetryDelayMilliseconds)
}

function Wait-MystiaGitHubMutationInterval {
    Start-Sleep -Milliseconds 1000
}

function New-MystiaDraftRelease {
    param(
        [Parameter(Mandatory = $true)][string]$Gh,
        [Parameter(Mandatory = $true)][string]$ResolvedTitle,
        [Parameter(Mandatory = $true)][string]$Notes,
        [Parameter(Mandatory = $true)][bool]$Prerelease,
        [Parameter(Mandatory = $true)][string]$ExpectedSha
    )

    $Release = Invoke-MystiaGitHubJsonRequest `
        -Gh $Gh `
        -Method "POST" `
        -Endpoint "repos/$Repo/releases" `
        -Body ([ordered]@{
            tag_name = $Tag
            target_commitish = $ExpectedSha
            name = $ResolvedTitle
            body = $Notes
            draft = $true
            prerelease = $Prerelease
            make_latest = "false"
        }) `
        -Label "Draft Release creation"
    return $Release
}

function Publish-MystiaDraftRelease {
    param(
        [Parameter(Mandatory = $true)][string]$Gh,
        [Parameter(Mandatory = $true)][long]$ReleaseId,
        [Parameter(Mandatory = $true)][bool]$Prerelease,
        [Parameter(Mandatory = $true)][bool]$MakeLatest
    )

    $Release = Invoke-MystiaGitHubJsonRequest `
        -Gh $Gh `
        -Method "PATCH" `
        -Endpoint "repos/$Repo/releases/$ReleaseId" `
        -Body ([ordered]@{
            draft = $false
            prerelease = $Prerelease
            make_latest = $(if ($MakeLatest) { "true" } else { "false" })
        }) `
        -Label "Draft Release publication"
    return $Release
}

function Invoke-MystiaReleaseAssetUpload {
    param(
        [Parameter(Mandatory = $true)][string]$Gh,
        [Parameter(Mandatory = $true)][long]$ReleaseId,
        [Parameter(Mandatory = $true)][string]$UploadUrl,
        [Parameter(Mandatory = $true)][string]$AssetRoot,
        [Parameter(Mandatory = $true)][string[]]$AssetNames
    )

    $ExpectedUploadUrl =
        "https://uploads.github.com/repos/$Repo/releases/$ReleaseId/assets{?name,label}"
    if ($UploadUrl -cne $ExpectedUploadUrl) {
        throw "Release asset upload URL does not match the created numeric Release transaction."
    }
    $UploadEndpointBase = $ExpectedUploadUrl.Substring(0, $ExpectedUploadUrl.IndexOf('{'))

    $PreparedAssets = [System.Collections.Generic.List[object]]::new()
    $SeenAssetNames = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::Ordinal
    )
    foreach ($AssetName in $AssetNames) {
        $ExpectedContentType = Get-MystiaReleaseAssetContentType -AssetName $AssetName
        if (-not $SeenAssetNames.Add($AssetName)) {
            throw "Duplicate Release upload asset: $AssetName"
        }
        $AssetPath = Resolve-MystiaRequiredFile `
            -Path (Join-Path $AssetRoot $AssetName) `
            -Label "release upload snapshot"
        $PreparedAssets.Add([pscustomobject]@{
            Name = $AssetName
            Path = $AssetPath
            ContentType = $ExpectedContentType
            Item = (Get-Item -LiteralPath $AssetPath)
        })
    }

    foreach ($PreparedAsset in $PreparedAssets) {
        $AssetName = [string]$PreparedAsset.Name
        $AssetPath = [string]$PreparedAsset.Path
        $ExpectedContentType = [string]$PreparedAsset.ContentType
        $AssetItem = $PreparedAsset.Item
        Wait-MystiaGitHubMutationInterval
        Write-Host "    Upload $AssetName to exact Release id $ReleaseId"
        $Raw = @(& $Gh api `
            --method POST `
            --header "Accept: application/vnd.github+json" `
            --header "X-GitHub-Api-Version: 2022-11-28" `
            --header "Content-Type: $ExpectedContentType" `
            --input $AssetPath `
            --raw-field "name=$AssetName" `
            $UploadEndpointBase)
        if ($LASTEXITCODE -ne 0) {
            throw @(
                "Release asset upload failed with exit code $LASTEXITCODE.",
                "One or more assets may already exist in the exact remote draft id $ReleaseId.",
                "Do not retry this release; inspect the exact tag, draft, and assets first."
            ) -join " "
        }
        try {
            $Response = ($Raw -join [Environment]::NewLine) |
                ConvertFrom-Json -Depth 100 -DateKind String -NoEnumerate
        }
        catch {
            throw @(
                "Release asset upload returned invalid JSON after the GitHub mutation request.",
                "The exact asset upload may already have succeeded.",
                "Do not retry this release; inspect Release id $ReleaseId first.",
                "Diagnostic: $($_.Exception.Message)"
            ) -join " "
        }
        if ($null -eq $Response -or
            $Response -is [System.Array] -or
            $null -eq $Response.PSObject.Properties["id"] -or
            $null -eq $Response.PSObject.Properties["size"] -or
            $null -eq $Response.PSObject.Properties["digest"] -or
            $Response.name -isnot [string] -or
            $Response.state -isnot [string] -or
            $Response.content_type -isnot [string] -or
            ($null -ne $Response.digest -and $Response.digest -isnot [string]) -or
            $Response.url -isnot [string]) {
            throw "Release asset upload returned an invalid response shape after the mutation. Do not retry Release id $ReleaseId."
        }
        $AssetId = Get-MystiaJsonPositiveInt64 `
            -Value $Response.id `
            -Label "Uploaded Release asset id"
        $RemoteSize = Get-MystiaJsonPositiveInt64 `
            -Value $Response.size `
            -Label "Uploaded Release asset size"
        $ExpectedDigest =
            "sha256:$((Get-FileHash -Algorithm SHA256 -LiteralPath $AssetPath).Hash.ToLowerInvariant())"
        $ExpectedApiUrl = "https://api.github.com/repos/$Repo/releases/assets/$AssetId"
        if ([string]$Response.name -cne $AssetName -or
            [string]$Response.state -cne "uploaded" -or
            [string]$Response.content_type -cne $ExpectedContentType -or
            $RemoteSize -ne $AssetItem.Length -or
            ($null -ne $Response.digest -and [string]$Response.digest -cne $ExpectedDigest) -or
            [string]$Response.url -cne $ExpectedApiUrl) {
            throw "Release asset upload response does not exactly match the submitted asset: $AssetName"
        }
    }
}

function Assert-MystiaCleanWorktree {
    param([Parameter(Mandatory = $true)][string]$Git)

    $Status = @(& $Git -C $RepoRoot status --porcelain=v1 --untracked-files=all)
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to verify the release worktree state."
    }
    if ($Status.Count -gt 0) {
        throw "Release builds require a clean tracked and untracked worktree."
    }
}

function Assert-MystiaPreviewTarget {
    param(
        [Parameter(Mandatory = $true)][string]$Git,
        [Parameter(Mandatory = $true)][string]$ExpectedSha
    )

    $BranchRaw = @(& $Git -C $RepoRoot symbolic-ref --quiet --short HEAD)
    $GitExitCode = $LASTEXITCODE
    if ($GitExitCode -ne 0 -or $BranchRaw.Count -ne 1 -or $BranchRaw[0] -isnot [string]) {
        throw "Unable to resolve the checked-out branch for the preview release."
    }
    $Branch = ([string]$BranchRaw[0]).Trim()
    if ($Branch -cne "dev") {
        throw "Preview releases require the checked-out dev branch. Actual: $Branch"
    }
    & $Git -C $RepoRoot fetch --no-tags origin "+refs/heads/dev:refs/remotes/origin/dev"
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to refresh origin/dev before the preview release."
    }
    $RemoteShaRaw = @(& $Git -C $RepoRoot rev-parse refs/remotes/origin/dev)
    $GitExitCode = $LASTEXITCODE
    if ($GitExitCode -ne 0 -or $RemoteShaRaw.Count -ne 1 -or $RemoteShaRaw[0] -isnot [string]) {
        throw "Unable to resolve the refreshed origin/dev commit."
    }
    $RemoteSha = ([string]$RemoteShaRaw[0]).Trim().ToLowerInvariant()
    if ($RemoteSha -cnotmatch '^[0-9a-f]{40}$' -or $RemoteSha -cne $ExpectedSha) {
        throw "Preview release commit is not the current origin/dev head. origin/dev=$RemoteSha, target=$ExpectedSha"
    }
}

function Assert-MystiaPreparedMetadata {
    param(
        [Parameter(Mandatory = $true)][string]$AssetRoot,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$RemoteReleases,
        [Parameter(Mandatory = $true)][string]$Version,
        [Parameter(Mandatory = $true)][string]$Channel,
        [Parameter(Mandatory = $true)][string]$ResolvedTitle,
        [Parameter(Mandatory = $true)][string]$Notes,
        [Parameter(Mandatory = $true)][string]$ExpectedTargetSha,
        [Parameter(Mandatory = $true)][bool]$IncludeAndroid
    )

    Assert-MystiaPreparedChecksums -DistRoot $AssetRoot -IncludeAndroid $IncludeAndroid

    $ManifestPath = Resolve-MystiaRequiredFile `
        -Path (Join-Path $AssetRoot $script:MystiaReleaseManifestName) `
        -Label "update manifest"
    $CatalogPath = Resolve-MystiaRequiredFile `
        -Path (Join-Path $AssetRoot $script:MystiaReleaseCatalogName) `
        -Label "update catalog"
    $Manifest = Get-Content -Raw -LiteralPath $ManifestPath |
        ConvertFrom-Json -DateKind String -NoEnumerate
    $Catalog = Get-Content -Raw -LiteralPath $CatalogPath |
        ConvertFrom-Json -DateKind String -NoEnumerate

    if ($null -eq $Manifest -or $Manifest -is [System.Array] -or
        $null -eq $Catalog -or $Catalog -is [System.Array] -or
        $null -eq $Manifest.PSObject.Properties["schemaVersion"] -or
        $null -eq $Manifest.PSObject.Properties["packageSize"] -or
        $null -eq $Manifest.PSObject.Properties["catalogSize"] -or
        $null -eq $Manifest.PSObject.Properties["publishedAtUtc"] -or
        $null -eq $Catalog.PSObject.Properties["schemaVersion"] -or
        $null -eq $Catalog.PSObject.Properties["generatedAtUtc"] -or
        $null -eq $Catalog.PSObject.Properties["releases"] -or
        $Catalog.releases -isnot [System.Array]) {
        throw "Prepared update metadata has an invalid JSON shape."
    }
    $ManifestSchemaVersion = Get-MystiaJsonPositiveInt64 `
        -Value $Manifest.schemaVersion `
        -Label "Update manifest schemaVersion"
    $CatalogSchemaVersion = Get-MystiaJsonPositiveInt64 `
        -Value $Catalog.schemaVersion `
        -Label "Update catalog schemaVersion"
    $ManifestPackageSize = Get-MystiaJsonPositiveInt64 `
        -Value $Manifest.packageSize `
        -Label "Update manifest packageSize"
    $ManifestCatalogSize = Get-MystiaJsonPositiveInt64 `
        -Value $Manifest.catalogSize `
        -Label "Update manifest catalogSize"
    Assert-MystiaCanonicalUtcTimestamp `
        -Value $Manifest.publishedAtUtc `
        -Label "Update manifest publishedAtUtc"
    Assert-MystiaCanonicalUtcTimestamp `
        -Value $Catalog.generatedAtUtc `
        -Label "Update catalog generatedAtUtc"

    if ($ManifestSchemaVersion -ne 1 -or
        $Manifest.version -isnot [string] -or
        $Manifest.tag -isnot [string] -or
        $Manifest.channel -isnot [string] -or
        $Manifest.targetCommitSha -isnot [string] -or
        $Manifest.packageAsset -isnot [string] -or
        $Manifest.packageSha256 -isnot [string] -or
        $Manifest.catalogAsset -isnot [string] -or
        $Manifest.catalogSha256 -isnot [string] -or
        $Manifest.releaseUrl -isnot [string] -or
        $Manifest.publishedAtUtc -isnot [string] -or
        $Catalog.repository -isnot [string] -or
        $Catalog.ownerVersion -isnot [string] -or
        $Catalog.ownerTag -isnot [string] -or
        $Catalog.generatedAtUtc -isnot [string]) {
        throw "Prepared update metadata scalar fields have an invalid JSON type."
    }

    if ($Manifest.version -cne $Version -or
        $Manifest.tag -cne $Tag -or
        $Manifest.channel -cne $Channel -or
        $Manifest.targetCommitSha -cne $ExpectedTargetSha -or
        $Manifest.packageAsset -cne $script:MystiaReleasePackageName -or
        $Manifest.catalogAsset -cne $script:MystiaReleaseCatalogName -or
        $Manifest.releaseUrl -cne "https://github.com/$Repo/releases/tag/$Tag") {
        throw "Prepared update-manifest.json does not match this release request."
    }
    if ($CatalogSchemaVersion -ne 1 -or
        $Catalog.repository -cne $Repo -or
        $Catalog.ownerVersion -cne $Version -or
        $Catalog.ownerTag -cne $Tag -or
        $Catalog.generatedAtUtc -cne $Manifest.publishedAtUtc) {
        throw "Prepared update-catalog.json does not match this release request."
    }

    foreach ($Entry in $Catalog.releases) {
        Assert-MystiaCatalogEntryShape -Entry $Entry -Label "Update catalog release entry"
    }
    $OwnerEntries = @($Catalog.releases | Where-Object { $_.tag -ceq $Tag })
    if ($OwnerEntries.Count -ne 1) {
        throw "Prepared update catalog must contain exactly one owner entry."
    }
    $OwnerEntry = $OwnerEntries[0]
    if ([string]$OwnerEntry.version -cne $Version -or
        [string]$OwnerEntry.tag -cne $Tag -or
        [string]$OwnerEntry.title -cne $ResolvedTitle -or
        [string]$OwnerEntry.channel -cne $Channel -or
        [string]$OwnerEntry.publishedAtUtc -cne [string]$Manifest.publishedAtUtc -or
        [string]$OwnerEntry.releaseUrl -cne "https://github.com/$Repo/releases/tag/$Tag" -or
        [string]$OwnerEntry.notesMarkdown -cne $Notes) {
        throw "Prepared update catalog owner entry does not match the publish request."
    }

    $CatalogHistory = [System.Collections.Generic.Dictionary[string, object]]::new(
        [System.StringComparer]::Ordinal
    )
    foreach ($Entry in @($Catalog.releases)) {
        $EntryTag = [string]$Entry.tag
        if ($EntryTag -ceq $Tag) {
            continue
        }
        if (-not $CatalogHistory.TryAdd($EntryTag, $Entry)) {
            throw "Prepared update catalog contains a duplicate historical tag: $EntryTag"
        }
    }

    $RemoteHistory = [System.Collections.Generic.Dictionary[string, object]]::new(
        [System.StringComparer]::Ordinal
    )
    foreach ($Release in $RemoteReleases) {
        Assert-MystiaJsonBoolean -Value $Release.draft -Label "Published Release draft"
        Assert-MystiaJsonBoolean -Value $Release.prerelease -Label "Published Release prerelease"
        if ($Release.draft) {
            continue
        }
        $ReleaseTag = [string]$Release.tag_name
        $Stable = $ReleaseTag -cmatch '^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$'
        $Preview = $ReleaseTag -cmatch '^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)-preview\.([1-9]\d*)$'
        if (-not $Stable -and -not $Preview) {
            continue
        }
        if (($null -ne $Release.name -and $Release.name -isnot [string]) -or
            $Release.body -isnot [string] -or
            $Release.published_at -isnot [string]) {
            throw "Published canonical Release has invalid scalar JSON fields: $ReleaseTag"
        }
        if ($Release.prerelease -ne $Preview) {
            throw "Published canonical Release channel mismatch: $ReleaseTag"
        }
        if (-not $RemoteHistory.TryAdd($ReleaseTag, $Release)) {
            throw "GitHub returned a duplicate canonical Release tag: $ReleaseTag"
        }
    }

    if ($RemoteHistory.Count -ne $CatalogHistory.Count) {
        throw "Published Release history changed after update-catalog.json was prepared."
    }
    foreach ($ReleaseTag in $RemoteHistory.Keys) {
        if (-not $CatalogHistory.ContainsKey($ReleaseTag)) {
            throw "Published Release history contains an entry missing from update-catalog.json: $ReleaseTag"
        }
        $Release = $RemoteHistory[$ReleaseTag]
        $Entry = $CatalogHistory[$ReleaseTag]
        $ReleaseName = [string]$Release.name
        $ExpectedTitle = if ([string]::IsNullOrWhiteSpace($ReleaseName)) { $ReleaseTag } else { $ReleaseName.Trim() }
        $ExpectedChannel = if ($Release.prerelease) { "preview" } else { "stable" }
        try {
            $ExpectedPublishedAt = [DateTimeOffset]::Parse(
                [string]$Release.published_at,
                [System.Globalization.CultureInfo]::InvariantCulture,
                [System.Globalization.DateTimeStyles]::AssumeUniversal
            ).ToUniversalTime().ToString(
                "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                [System.Globalization.CultureInfo]::InvariantCulture
            )
        }
        catch {
            throw "Published Release has an invalid published_at value: $ReleaseTag"
        }
        if ([string]$Entry.version -cne $ReleaseTag.Substring(1) -or
            [string]$Entry.title -cne $ExpectedTitle -or
            [string]$Entry.channel -cne $ExpectedChannel -or
            [string]$Entry.publishedAtUtc -cne $ExpectedPublishedAt -or
            [string]$Entry.releaseUrl -cne "https://github.com/$Repo/releases/tag/$([System.Uri]::EscapeDataString($ReleaseTag))" -or
            [string]$Entry.notesMarkdown -cne [string]$Release.body) {
            throw "Published Release history no longer matches update-catalog.json: $ReleaseTag"
        }
    }

    $PackagePath = Resolve-MystiaRequiredFile `
        -Path (Join-Path $AssetRoot $script:MystiaReleasePackageName) `
        -Label "Mod package"
    $PackageItem = Get-Item -LiteralPath $PackagePath
    $CatalogItem = Get-Item -LiteralPath $CatalogPath
    $ActualPackageHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $PackagePath).Hash.ToLowerInvariant()
    $ActualCatalogHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $CatalogPath).Hash.ToLowerInvariant()
    if ([string]$Manifest.packageSha256 -cne $ActualPackageHash -or
        $ManifestPackageSize -ne $PackageItem.Length -or
        [string]$Manifest.catalogSha256 -cne $ActualCatalogHash -or
        $ManifestCatalogSize -ne $CatalogItem.Length) {
        throw "Prepared update manifest hashes or sizes do not match the release payload."
    }
}

function Invoke-MystiaReleasePublicationTransaction {
    param(
        [Parameter(Mandatory = $true)][string]$Gh,
        [Parameter(Mandatory = $true)][string]$AssetRoot,
        [Parameter(Mandatory = $true)][string[]]$AssetNames,
        [Parameter(Mandatory = $true)][string]$Version,
        [Parameter(Mandatory = $true)][ValidateSet("stable", "preview")][string]$Channel,
        [Parameter(Mandatory = $true)][string]$ResolvedTitle,
        [Parameter(Mandatory = $true)][string]$Notes,
        [Parameter(Mandatory = $true)][string]$ExpectedTargetSha,
        [Parameter(Mandatory = $true)][bool]$IncludeAndroid,
        [Parameter(Mandatory = $true)][bool]$OfficialRelease,
        [ValidateRange(1, 30)][int]$ReadMaxAttempts = 20,
        [ValidateRange(0, 5000)][int]$ReadRetryDelayMilliseconds = 1000
    )

    if ($OfficialRelease -and $Channel -cne "stable") {
        throw "The official Release transaction requires the stable channel."
    }
    if (-not $OfficialRelease -and $Channel -cne "preview") {
        throw "The non-official Release transaction requires the preview channel."
    }
    $ExpectedPrerelease = $Channel -ceq "preview"

    foreach ($AssetName in $AssetNames) {
        [void](Get-MystiaReleaseAssetContentType -AssetName $AssetName)
    }
    $CanonicalAssetNames = @(Get-MystiaReleaseAssetNames -IncludeAndroid $IncludeAndroid)
    if ($AssetNames.Count -ne $CanonicalAssetNames.Count) {
        throw "The Release transaction asset allowlist does not match the canonical asset set."
    }
    for ($AssetIndex = 0; $AssetIndex -lt $CanonicalAssetNames.Count; $AssetIndex++) {
        if ($AssetNames[$AssetIndex] -cne $CanonicalAssetNames[$AssetIndex]) {
            throw "The Release transaction asset allowlist does not match the canonical asset set."
        }
    }

    New-MystiaRemoteTag `
        -Gh $Gh `
        -ExpectedSha $ExpectedTargetSha `
        -ReadMaxAttempts $ReadMaxAttempts `
        -ReadRetryDelayMilliseconds $ReadRetryDelayMilliseconds
    Wait-MystiaGitHubMutationInterval

    $CreatedDraft = New-MystiaDraftRelease `
        -Gh $Gh `
        -ResolvedTitle $ResolvedTitle `
        -Notes $Notes `
        -Prerelease $ExpectedPrerelease `
        -ExpectedSha $ExpectedTargetSha
    $DraftReleaseId = Get-MystiaJsonPositiveInt64 `
        -Value $CreatedDraft.id `
        -Label "New draft Release id"
    Assert-MystiaRemoteRelease `
        -Release $CreatedDraft `
        -ExpectedReleaseId $DraftReleaseId `
        -AssetRoot $AssetRoot `
        -ExpectedDraft $true `
        -ExpectedPrerelease $ExpectedPrerelease `
        -ExpectedTitle $ResolvedTitle `
        -ExpectedNotes $Notes `
        -ExpectedAssetNames ([string[]]@()) `
        -RequireImmutable $false
    $DraftUploadUrl = [string]$CreatedDraft.upload_url
    [void](Wait-MystiaRemoteReleaseState `
        -Gh $Gh `
        -ReleaseId $DraftReleaseId `
        -Phase "Created" `
        -AssetRoot $AssetRoot `
        -ExpectedPrerelease $ExpectedPrerelease `
        -ExpectedTitle $ResolvedTitle `
        -ExpectedNotes $Notes `
        -ExpectedAssetNames ([string[]]@()) `
        -RequireImmutable $false `
        -MaxAttempts $ReadMaxAttempts `
        -RetryDelayMilliseconds $ReadRetryDelayMilliseconds)
    [void](Wait-MystiaExactRemoteTagRef `
        -Gh $Gh `
        -ReleaseTag $Tag `
        -ExpectedSha $ExpectedTargetSha `
        -MaxAttempts $ReadMaxAttempts `
        -RetryDelayMilliseconds $ReadRetryDelayMilliseconds)

    Invoke-MystiaReleaseAssetUpload `
        -Gh $Gh `
        -ReleaseId $DraftReleaseId `
        -UploadUrl $DraftUploadUrl `
        -AssetRoot $AssetRoot `
        -AssetNames $AssetNames
    [void](Wait-MystiaRemoteReleaseState `
        -Gh $Gh `
        -ReleaseId $DraftReleaseId `
        -Phase "Uploaded" `
        -AssetRoot $AssetRoot `
        -ExpectedPrerelease $ExpectedPrerelease `
        -ExpectedTitle $ResolvedTitle `
        -ExpectedNotes $Notes `
        -ExpectedAssetNames $AssetNames `
        -RequireImmutable $false `
        -MaxAttempts $ReadMaxAttempts `
        -RetryDelayMilliseconds $ReadRetryDelayMilliseconds)
    [void](Wait-MystiaExactRemoteTagRef `
        -Gh $Gh `
        -ReleaseTag $Tag `
        -ExpectedSha $ExpectedTargetSha `
        -MaxAttempts $ReadMaxAttempts `
        -RetryDelayMilliseconds $ReadRetryDelayMilliseconds)

    if ($OfficialRelease) {
        Assert-MystiaOfficialTarget -Gh $Gh -ExpectedSha $ExpectedTargetSha
        Assert-MystiaImmutableReleasesEnabled -Gh $Gh
    }
    $RemoteReleases = @(Get-MystiaRepositoryReleases -Gh $Gh)
    Assert-MystiaPreparedMetadata `
        -AssetRoot $AssetRoot `
        -RemoteReleases $RemoteReleases `
        -Version $Version `
        -Channel $Channel `
        -ResolvedTitle $ResolvedTitle `
        -Notes $Notes `
        -ExpectedTargetSha $ExpectedTargetSha `
        -IncludeAndroid $IncludeAndroid
    [void](Wait-MystiaRemoteReleaseState `
        -Gh $Gh `
        -ReleaseId $DraftReleaseId `
        -Phase "Uploaded" `
        -AssetRoot $AssetRoot `
        -ExpectedPrerelease $ExpectedPrerelease `
        -ExpectedTitle $ResolvedTitle `
        -ExpectedNotes $Notes `
        -ExpectedAssetNames $AssetNames `
        -RequireImmutable $false `
        -MaxAttempts $ReadMaxAttempts `
        -RetryDelayMilliseconds $ReadRetryDelayMilliseconds)
    [void](Wait-MystiaExactRemoteTagRef `
        -Gh $Gh `
        -ReleaseTag $Tag `
        -ExpectedSha $ExpectedTargetSha `
        -MaxAttempts $ReadMaxAttempts `
        -RetryDelayMilliseconds $ReadRetryDelayMilliseconds)

    Wait-MystiaGitHubMutationInterval
    $PublishedResponse = Publish-MystiaDraftRelease `
        -Gh $Gh `
        -ReleaseId $DraftReleaseId `
        -Prerelease $ExpectedPrerelease `
        -MakeLatest $OfficialRelease
    Assert-MystiaRemoteRelease `
        -Release $PublishedResponse `
        -ExpectedReleaseId $DraftReleaseId `
        -AssetRoot $AssetRoot `
        -ExpectedDraft $false `
        -ExpectedPrerelease $ExpectedPrerelease `
        -ExpectedTitle $ResolvedTitle `
        -ExpectedNotes $Notes `
        -ExpectedAssetNames $AssetNames `
        -RequireImmutable $false
    $Published = Wait-MystiaRemoteReleaseState `
        -Gh $Gh `
        -ReleaseId $DraftReleaseId `
        -Phase "Published" `
        -AssetRoot $AssetRoot `
        -ExpectedPrerelease $ExpectedPrerelease `
        -ExpectedTitle $ResolvedTitle `
        -ExpectedNotes $Notes `
        -ExpectedAssetNames $AssetNames `
        -RequireImmutable $OfficialRelease `
        -MaxAttempts $ReadMaxAttempts `
        -RetryDelayMilliseconds $ReadRetryDelayMilliseconds
    [void](Wait-MystiaExactRemoteTagRef `
        -Gh $Gh `
        -ReleaseTag $Tag `
        -ExpectedSha $ExpectedTargetSha `
        -MaxAttempts $ReadMaxAttempts `
        -RetryDelayMilliseconds $ReadRetryDelayMilliseconds)
    if ($OfficialRelease) {
        Wait-MystiaLatestRelease `
            -Gh $Gh `
            -ExpectedReleaseId $DraftReleaseId `
            -AssetRoot $AssetRoot `
            -ExpectedTitle $ResolvedTitle `
            -ExpectedNotes $Notes `
            -ExpectedAssetNames $AssetNames `
            -MaxAttempts $ReadMaxAttempts `
            -RetryDelayMilliseconds $ReadRetryDelayMilliseconds
    }

    return $Published
}

Push-Location $RepoRoot
try {
    if ($Repo -cnotmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') {
        throw "Invalid GitHub repository slug: $Repo"
    }
    if ($SkipBuild -and $BuildAndroidApk) {
        throw "-SkipBuild and -BuildAndroidApk cannot be used together."
    }
    if ($BuildCacheTargetGiB -ge $BuildCacheLimitGiB) {
        throw "BuildCacheTargetGiB must be less than BuildCacheLimitGiB."
    }

    $Version = Get-MystiaVersionFromTag -Tag $Tag
    $Channel = Get-MystiaReleaseChannel -Version $Version
    if ($OfficialRelease -and $Channel -cne "stable") {
        throw "OfficialRelease accepts stable vX.Y.Z tags only. Actual: $Tag"
    }
    if ($Channel -ceq "stable" -and -not $OfficialRelease) {
        throw "Stable releases may only use the GitHub Actions OfficialRelease path. Actual: $Tag"
    }
    if ([string]::IsNullOrWhiteSpace($Title)) {
        $Title = $Tag
    }
    if ($Title.Trim() -cne $Title) {
        throw "Release title must not contain leading or trailing whitespace."
    }
    if ([System.Text.Encoding]::UTF8.GetByteCount($Title) -gt 512) {
        throw "Release title exceeds 512 UTF-8 bytes."
    }
    $Notes = Read-MystiaReleaseNotes -NotesFile $NotesFile
    Assert-MystiaProjectVersion -RepoRoot $RepoRoot -ExpectedVersion $Version
    $TargetCommitSha = Assert-MystiaTargetCommit -RepoRoot $RepoRoot -TargetCommitSha $TargetCommitSha

    $Git = Get-MystiaCommand -Name "git" -InstallHint "Install Git before publishing."
    Assert-MystiaCleanWorktree -Git $Git
    if (-not $OfficialRelease) {
        Assert-MystiaPreviewTarget -Git $Git -ExpectedSha $TargetCommitSha
    }

    $Node = Get-MystiaCommand -Name "node" -InstallHint "Install the repository-locked Node.js version."
    Invoke-MystiaChecked -FilePath $Node -Arguments @(
        (Join-Path $RepoRoot "scripts/check-build-toolchain.mjs"),
        "release-tools"
    )
    $Gh = Get-MystiaCommand -Name "gh" -InstallHint "Install GitHub CLI and provide GH_TOKEN or authenticate before publishing."
    if ($OfficialRelease) {
        Assert-MystiaOfficialActionsContext -ExpectedSha $TargetCommitSha
        Assert-MystiaOfficialTarget -Gh $Gh -ExpectedSha $TargetCommitSha
        Assert-MystiaImmutableReleasesEnabled -Gh $Gh
    }
    Assert-MystiaRemoteReleaseIdentityAbsent -Gh $Gh -ReleaseTag $Tag

    if (-not $SkipBuild) {
        $Pwsh = Get-MystiaCommand -Name "pwsh" -InstallHint "Install PowerShell 7 before publishing."
        [string[]]$BuildArgs = @(
            "-ExecutionPolicy", "Bypass",
            "-File", $BuildScript,
            "-BuildCacheLimitGiB", $BuildCacheLimitGiB.ToString(),
            "-BuildCacheTargetGiB", $BuildCacheTargetGiB.ToString()
        )
        if (-not [string]::IsNullOrWhiteSpace($ReferenceDir)) {
            $BuildArgs += @("-ReferenceDir", $ReferenceDir)
        }
        if ($BuildAndroidApk) {
            $BuildArgs += "-BuildAndroidApk"
        }
        if ($SkipBuildCacheCleanup) {
            $BuildArgs += "-SkipBuildCacheCleanup"
        }
        Invoke-MystiaChecked -FilePath $Pwsh -Arguments $BuildArgs

        [string[]]$PrepareArgs = @(
            "-ExecutionPolicy", "Bypass",
            "-File", $PrepareScript,
            "-Tag", $Tag,
            "-Title", $Title,
            "-NotesFile", $Notes.Path,
            "-TargetCommitSha", $TargetCommitSha,
            "-Repo", $Repo
        )
        if ($OfficialRelease) {
            $PrepareArgs += "-RequireAndroid"
        }
        Invoke-MystiaChecked -FilePath $Pwsh -Arguments $PrepareArgs
        Assert-MystiaCleanWorktree -Git $Git
    }

    $IncludeAndroid = $OfficialRelease -or @(
        $script:MystiaReleaseAndroidNames |
            Where-Object { Test-Path -LiteralPath (Join-Path $DistRoot $_) -PathType Leaf }
    ).Count -gt 0
    if ($IncludeAndroid) {
        foreach ($AndroidName in $script:MystiaReleaseAndroidNames) {
            [void](Resolve-MystiaRequiredFile -Path (Join-Path $DistRoot $AndroidName) -Label "Android APK")
        }
    }
    $AssetNames = @(Get-MystiaReleaseAssetNames -IncludeAndroid $IncludeAndroid)
    $UploadRoot = Join-Path `
        ([System.IO.Path]::GetTempPath()) `
        "mystia-release-upload-$([Guid]::NewGuid().ToString('N'))"
    [void](New-Item -ItemType Directory -Path $UploadRoot)
    foreach ($AssetName in $AssetNames) {
        $SourcePath = Resolve-MystiaRequiredFile `
            -Path (Join-Path $DistRoot $AssetName) `
            -Label "release asset"
        Copy-Item -LiteralPath $SourcePath -Destination (Join-Path $UploadRoot $AssetName)
    }
    $NotesSnapshotPath = Join-Path $UploadRoot "release-notes.md"
    Copy-Item -LiteralPath $Notes.Path -Destination $NotesSnapshotPath
    $NotesSnapshot = Read-MystiaReleaseNotes -NotesFile $NotesSnapshotPath
    if ($OfficialRelease) {
        Assert-MystiaOfficialTarget -Gh $Gh -ExpectedSha $TargetCommitSha
    }
    Assert-MystiaRemoteReleaseIdentityAbsent -Gh $Gh -ReleaseTag $Tag
    $RemoteReleases = @(Get-MystiaRepositoryReleases -Gh $Gh)
    if ($null -ne (Get-MystiaReleaseByTag -Releases $RemoteReleases -ReleaseTag $Tag)) {
        throw "Release appeared while preparing the upload snapshot: $Tag"
    }
    Assert-MystiaPreparedMetadata `
        -AssetRoot $UploadRoot `
        -RemoteReleases $RemoteReleases `
        -Version $Version `
        -Channel $Channel `
        -ResolvedTitle $Title `
        -Notes $NotesSnapshot.Text `
        -ExpectedTargetSha $TargetCommitSha `
        -IncludeAndroid $IncludeAndroid

    Assert-MystiaRemoteReleaseIdentityAbsent -Gh $Gh -ReleaseTag $Tag
    $RemoteReleases = @(Get-MystiaRepositoryReleases -Gh $Gh)
    Assert-MystiaPreparedMetadata `
        -AssetRoot $UploadRoot `
        -RemoteReleases $RemoteReleases `
        -Version $Version `
        -Channel $Channel `
        -ResolvedTitle $Title `
        -Notes $NotesSnapshot.Text `
        -ExpectedTargetSha $TargetCommitSha `
        -IncludeAndroid $IncludeAndroid

    if ($OfficialRelease) {
        Assert-MystiaImmutableReleasesEnabled -Gh $Gh
    }
    Assert-MystiaCleanWorktree -Git $Git
    if (-not $OfficialRelease) {
        Assert-MystiaPreviewTarget -Git $Git -ExpectedSha $TargetCommitSha
    }
    [void](Invoke-MystiaReleasePublicationTransaction `
        -Gh $Gh `
        -AssetRoot $UploadRoot `
        -AssetNames $AssetNames `
        -Version $Version `
        -Channel $Channel `
        -ResolvedTitle $Title `
        -Notes $NotesSnapshot.Text `
        -ExpectedTargetSha $TargetCommitSha `
        -IncludeAndroid $IncludeAndroid `
        -OfficialRelease $OfficialRelease)

    Write-Host ""
    Write-Host "Release published once at exact commit $TargetCommitSha`: $Tag" -ForegroundColor Green
}
finally {
    if ($null -ne $UploadRoot -and (Test-Path -LiteralPath $UploadRoot)) {
        Remove-Item -LiteralPath $UploadRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
    Pop-Location
}
