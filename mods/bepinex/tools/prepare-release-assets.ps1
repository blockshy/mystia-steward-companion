#requires -Version 7.5

<#
.SYNOPSIS
    为一次不可覆盖的 GitHub Release 生成最终元数据和校验和。

.DESCRIPTION
    该脚本只读取已构建的发布产物和既有 GitHub Release 历史，生成
    update-catalog.json、update-manifest.json 与 SHA256SUMS.txt。三份元数据先在同一
    文件系统的唯一 staging 中生成并自检，再以可回滚事务替换既有文件。它不会创建
    tag、Release 或上传资产。正式发布应在构建完成后调用本脚本，再对最终七项资产做证明。
#>
param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [string]$Title = "",
    [Parameter(Mandatory = $true)][string]$NotesFile,
    [Parameter(Mandatory = $true)][string]$TargetCommitSha,
    [switch]$RequireAndroid,
    [string]$Repo = "blockshy/mystia-steward-companion"
)

$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest

$ToolDir = $PSScriptRoot
$ModRoot = (Resolve-Path (Join-Path $ToolDir "..")).Path
$RepoRoot = (Resolve-Path (Join-Path $ModRoot "../..")).Path
$DistRoot = Join-Path $ModRoot "dist"
$CatalogGenerator = Join-Path $RepoRoot "scripts/generate-update-catalog.mjs"
$CommonScript = Join-Path $ToolDir "release-common.ps1"
. $CommonScript

function New-MystiaUpdateCatalog {
    param(
        [Parameter(Mandatory = $true)][string]$Gh,
        [Parameter(Mandatory = $true)][string]$Node,
        [Parameter(Mandatory = $true)][string]$OutputPath,
        [Parameter(Mandatory = $true)][string]$Version,
        [Parameter(Mandatory = $true)][string]$Channel,
        [Parameter(Mandatory = $true)][string]$ResolvedTitle,
        [Parameter(Mandatory = $true)][string]$Notes,
        [Parameter(Mandatory = $true)][string]$PublishedAtUtc
    )

    [void](Resolve-MystiaRequiredFile -Path $CatalogGenerator -Label "update catalog generator")
    $InputPath = [System.IO.Path]::GetTempFileName()
    $TitlePath = [System.IO.Path]::GetTempFileName()
    $NotesPath = [System.IO.Path]::GetTempFileName()
    try {
        Write-Host "    $Gh api --paginate --slurp repos/$Repo/releases?per_page=100"
        $ReleasePages = & $Gh api --paginate --slurp "repos/$Repo/releases?per_page=100"
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to read the complete GitHub Release history for $Repo."
        }

        Write-MystiaUtf8WithoutBom -Path $InputPath -Content ($ReleasePages -join [Environment]::NewLine)
        Write-MystiaUtf8WithoutBom -Path $TitlePath -Content $ResolvedTitle
        Write-MystiaUtf8WithoutBom -Path $NotesPath -Content $Notes
        Invoke-MystiaChecked -FilePath $Node -Arguments @(
            $CatalogGenerator,
            "--input", $InputPath,
            "--output", $OutputPath,
            "--repository", $Repo,
            "--tag", $Tag,
            "--version", $Version,
            "--channel", $Channel,
            "--title-file", $TitlePath,
            "--notes-file", $NotesPath,
            "--published-at", $PublishedAtUtc,
            "--generated-at", $PublishedAtUtc
        )
    }
    finally {
        Remove-Item -LiteralPath $InputPath, $TitlePath, $NotesPath -Force -ErrorAction SilentlyContinue
    }
}

function Assert-MystiaMetadataDestinations {
    param([Parameter(Mandatory = $true)][string]$DistRoot)

    foreach ($Name in @(
        $script:MystiaReleaseCatalogName,
        $script:MystiaReleaseManifestName,
        $script:MystiaReleaseChecksumsName
    )) {
        $Path = Join-Path $DistRoot $Name
        if (-not (Test-Path -LiteralPath $Path)) {
            continue
        }

        $Item = Get-Item -LiteralPath $Path -Force
        if (-not $Item.PSIsContainer -and
            ($Item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0) {
            continue
        }

        throw "Existing release metadata destination must be a regular file: $Path"
    }
}

function Assert-NoMystiaMetadataTransactions {
    param([Parameter(Mandatory = $true)][string]$DistRoot)

    $Pending = @(
        Get-ChildItem -LiteralPath $DistRoot -Force |
            Where-Object { $_.Name -clike ".release-metadata-*" }
    )
    if ($Pending.Count -gt 0) {
        throw "Pending release metadata transaction requires manual inspection: $($Pending[0].FullName)"
    }
}

function Resolve-MystiaPreparedAssetPath {
    param(
        [Parameter(Mandatory = $true)][string]$DistRoot,
        [Parameter(Mandatory = $true)][string]$StagingRoot,
        [Parameter(Mandatory = $true)][string]$Name
    )

    if ($Name -cin @(
        $script:MystiaReleaseCatalogName,
        $script:MystiaReleaseManifestName,
        $script:MystiaReleaseChecksumsName
    )) {
        return Join-Path $StagingRoot $Name
    }

    return Join-Path $DistRoot $Name
}

function Assert-MystiaStagedMetadata {
    param(
        [Parameter(Mandatory = $true)][string]$DistRoot,
        [Parameter(Mandatory = $true)][string]$StagingRoot,
        [Parameter(Mandatory = $true)][bool]$IncludeAndroid,
        [Parameter(Mandatory = $true)][string]$ExpectedVersion,
        [Parameter(Mandatory = $true)][string]$ExpectedTag,
        [Parameter(Mandatory = $true)][string]$ExpectedChannel,
        [Parameter(Mandatory = $true)][string]$ExpectedTargetCommitSha
    )

    $CatalogPath = Resolve-MystiaRequiredFile `
        -Path (Join-Path $StagingRoot $script:MystiaReleaseCatalogName) `
        -Label "staged update catalog"
    $ManifestPath = Resolve-MystiaRequiredFile `
        -Path (Join-Path $StagingRoot $script:MystiaReleaseManifestName) `
        -Label "staged update manifest"
    $ChecksumsPath = Resolve-MystiaRequiredFile `
        -Path (Join-Path $StagingRoot $script:MystiaReleaseChecksumsName) `
        -Label "staged release checksum list"

    try {
        $Catalog = Get-Content -Raw -LiteralPath $CatalogPath |
            ConvertFrom-Json -DateKind String -NoEnumerate
        $Manifest = Get-Content -Raw -LiteralPath $ManifestPath |
            ConvertFrom-Json -DateKind String -NoEnumerate
    }
    catch {
        throw "Staged release metadata is not valid JSON: $($_.Exception.Message)"
    }
    if ($null -eq $Catalog -or
        $Catalog -is [System.Array] -or
        $null -eq $Catalog.PSObject.Properties["schemaVersion"] -or
        $null -eq $Catalog.PSObject.Properties["generatedAtUtc"] -or
        $null -eq $Catalog.PSObject.Properties["releases"] -or
        $Catalog.repository -isnot [string] -or
        $Catalog.ownerVersion -isnot [string] -or
        $Catalog.ownerTag -isnot [string] -or
        $Catalog.generatedAtUtc -isnot [string] -or
        $Catalog.releases -isnot [System.Array]) {
        throw "Staged update catalog has an invalid JSON shape."
    }
    $CatalogSchemaVersion = Get-MystiaJsonPositiveInt64 `
        -Value $Catalog.schemaVersion `
        -Label "Staged update catalog schemaVersion"
    Assert-MystiaCanonicalUtcTimestamp `
        -Value $Catalog.generatedAtUtc `
        -Label "Staged update catalog generatedAtUtc"
    foreach ($Entry in $Catalog.releases) {
        Assert-MystiaCatalogEntryShape -Entry $Entry -Label "Staged update catalog release entry"
    }
    $OwnerEntries = @($Catalog.releases | Where-Object { $_.tag -ceq $ExpectedTag })
    if ($CatalogSchemaVersion -ne 1 -or
        $Catalog.repository -cne $Repo -or
        $Catalog.ownerVersion -cne $ExpectedVersion -or
        $Catalog.ownerTag -cne $ExpectedTag -or
        $OwnerEntries.Count -ne 1 -or
        $OwnerEntries[0].version -cne $ExpectedVersion -or
        $OwnerEntries[0].channel -cne $ExpectedChannel) {
        throw "Staged update catalog identity does not match the requested release."
    }

    if ($null -eq $Manifest -or
        $Manifest -is [System.Array] -or
        $null -eq $Manifest.PSObject.Properties["schemaVersion"] -or
        $null -eq $Manifest.PSObject.Properties["packageSize"] -or
        $null -eq $Manifest.PSObject.Properties["catalogSize"] -or
        $Manifest.version -isnot [string] -or
        $Manifest.tag -isnot [string] -or
        $Manifest.channel -isnot [string] -or
        $Manifest.targetCommitSha -isnot [string] -or
        $Manifest.packageAsset -isnot [string] -or
        $Manifest.packageSha256 -isnot [string] -or
        $Manifest.catalogAsset -isnot [string] -or
        $Manifest.catalogSha256 -isnot [string] -or
        $Manifest.releaseUrl -isnot [string] -or
        $Manifest.publishedAtUtc -isnot [string]) {
        throw "Staged update manifest has an invalid JSON shape."
    }
    $ManifestSchemaVersion = Get-MystiaJsonPositiveInt64 `
        -Value $Manifest.schemaVersion `
        -Label "Staged update manifest schemaVersion"
    $ManifestPackageSize = Get-MystiaJsonPositiveInt64 `
        -Value $Manifest.packageSize `
        -Label "Staged update manifest packageSize"
    $ManifestCatalogSize = Get-MystiaJsonPositiveInt64 `
        -Value $Manifest.catalogSize `
        -Label "Staged update manifest catalogSize"
    Assert-MystiaCanonicalUtcTimestamp `
        -Value $Manifest.publishedAtUtc `
        -Label "Staged update manifest publishedAtUtc"
    if ($ManifestSchemaVersion -ne 1 -or
        $Manifest.version -cne $ExpectedVersion -or
        $Manifest.tag -cne $ExpectedTag -or
        $Manifest.channel -cne $ExpectedChannel -or
        $Manifest.targetCommitSha -cne $ExpectedTargetCommitSha -or
        $Manifest.releaseUrl -cne "https://github.com/$Repo/releases/tag/$ExpectedTag" -or
        $Manifest.publishedAtUtc -cne $Catalog.generatedAtUtc -or
        $OwnerEntries[0].publishedAtUtc -cne $Manifest.publishedAtUtc) {
        throw "Staged update manifest identity does not match the requested release."
    }

    $PackagePath = Resolve-MystiaRequiredFile `
        -Path (Join-Path $DistRoot $script:MystiaReleasePackageName) `
        -Label "Mod package"
    $PackageItem = Get-Item -LiteralPath $PackagePath
    $CatalogItem = Get-Item -LiteralPath $CatalogPath
    if ([string]$Manifest.packageAsset -cne $script:MystiaReleasePackageName -or
        [string]$Manifest.packageSha256 -cne (Get-FileHash -Algorithm SHA256 -LiteralPath $PackagePath).Hash.ToLowerInvariant() -or
        $ManifestPackageSize -ne $PackageItem.Length -or
        [string]$Manifest.catalogAsset -cne $script:MystiaReleaseCatalogName -or
        [string]$Manifest.catalogSha256 -cne (Get-FileHash -Algorithm SHA256 -LiteralPath $CatalogPath).Hash.ToLowerInvariant() -or
        $ManifestCatalogSize -ne $CatalogItem.Length) {
        throw "Staged update manifest hashes or sizes do not match the release payload."
    }

    $PayloadNames = @(Get-MystiaPayloadAssetNames -IncludeAndroid $IncludeAndroid)
    $Lines = @(
        Get-Content -LiteralPath $ChecksumsPath |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    )
    if ($Lines.Count -ne $PayloadNames.Count) {
        throw "Staged checksum entry count mismatch. Expected $($PayloadNames.Count), actual $($Lines.Count)."
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
            throw "Invalid staged SHA256SUMS entry: $Line"
        }
        $ExpectedHash = $Matches[1]
        $Name = $Matches[2]
        if (-not $ExpectedNames.Contains($Name) -or -not $Seen.Add($Name)) {
            throw "Unexpected or duplicate staged checksum asset: $Name"
        }
        $AssetPath = Resolve-MystiaPreparedAssetPath `
            -DistRoot $DistRoot `
            -StagingRoot $StagingRoot `
            -Name $Name
        $AssetPath = Resolve-MystiaRequiredFile -Path $AssetPath -Label "staged release asset"
        $ActualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $AssetPath).Hash.ToLowerInvariant()
        if ($ActualHash -cne $ExpectedHash) {
            throw "Staged checksum mismatch for $Name. Expected $ExpectedHash, actual $ActualHash."
        }
    }
    if ($Seen.Count -ne $ExpectedNames.Count) {
        throw "Staged checksum list does not cover every release payload."
    }
}

function Commit-MystiaPreparedMetadata {
    param(
        [Parameter(Mandatory = $true)][string]$DistRoot,
        [Parameter(Mandatory = $true)][string]$TransactionRoot,
        [Parameter(Mandatory = $true)][bool]$IncludeAndroid
    )

    $StagingRoot = Join-Path $TransactionRoot "staging"
    $BackupRoot = Join-Path $TransactionRoot "backup"
    [void](New-Item -ItemType Directory -Path $BackupRoot)
    $Names = @(
        $script:MystiaReleaseCatalogName,
        $script:MystiaReleaseManifestName,
        $script:MystiaReleaseChecksumsName
    )
    foreach ($Name in $Names) {
        [void](Resolve-MystiaRequiredFile `
            -Path (Join-Path $StagingRoot $Name) `
            -Label "staged release metadata")
    }

    $BackedUpNames = [System.Collections.Generic.List[string]]::new()
    $CommittedNames = [System.Collections.Generic.List[string]]::new()
    $CommitValidated = $false
    try {
        foreach ($Name in $Names) {
            $DestinationPath = Join-Path $DistRoot $Name
            if (Test-Path -LiteralPath $DestinationPath) {
                [System.IO.File]::Move($DestinationPath, (Join-Path $BackupRoot $Name))
                $BackedUpNames.Add($Name)
            }
            [System.IO.File]::Move((Join-Path $StagingRoot $Name), $DestinationPath)
            $CommittedNames.Add($Name)
        }

        Assert-MystiaPreparedChecksums -DistRoot $DistRoot -IncludeAndroid $IncludeAndroid
        $CommitValidated = $true
    }
    catch {
        $PreparationError = $_
        $RollbackErrors = [System.Collections.Generic.List[string]]::new()
        for ($Index = $CommittedNames.Count - 1; $Index -ge 0; $Index--) {
            $Name = $CommittedNames[$Index]
            $DestinationPath = Join-Path $DistRoot $Name
            try {
                if (Test-Path -LiteralPath $DestinationPath) {
                    Remove-Item -LiteralPath $DestinationPath -Force
                }
            }
            catch {
                $RollbackErrors.Add("remove new $Name`: $($_.Exception.Message)")
            }
        }
        for ($Index = $BackedUpNames.Count - 1; $Index -ge 0; $Index--) {
            $Name = $BackedUpNames[$Index]
            try {
                [System.IO.File]::Move(
                    (Join-Path $BackupRoot $Name),
                    (Join-Path $DistRoot $Name)
                )
            }
            catch {
                $RollbackErrors.Add("restore old $Name`: $($_.Exception.Message)")
            }
        }
        if ($RollbackErrors.Count -gt 0) {
            throw @(
                "Release metadata commit failed and rollback was incomplete.",
                "Preparation error: $($PreparationError.Exception.Message)",
                "Rollback errors:",
                ($RollbackErrors -join [Environment]::NewLine)
            ) -join [Environment]::NewLine
        }

        throw $PreparationError
    }

    if ($CommitValidated) {
        Remove-Item -LiteralPath $BackupRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Push-Location $RepoRoot
try {
    if ($Repo -cnotmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') {
        throw "Invalid GitHub repository slug: $Repo"
    }

    $Node = Get-MystiaCommand -Name "node" -InstallHint "Install the repository-locked Node.js version."
    Invoke-MystiaChecked -FilePath $Node -Arguments @(
        (Join-Path $RepoRoot "scripts/check-build-toolchain.mjs"),
        "release-tools"
    )

    $Version = Get-MystiaVersionFromTag -Tag $Tag
    $Channel = Get-MystiaReleaseChannel -Version $Version
    if ($RequireAndroid -and $Channel -cne "stable") {
        throw "RequireAndroid is reserved for canonical stable releases. Actual channel: $Channel"
    }
    Assert-MystiaProjectVersion -RepoRoot $RepoRoot -ExpectedVersion $Version
    $TargetCommitSha = Assert-MystiaTargetCommit -RepoRoot $RepoRoot -TargetCommitSha $TargetCommitSha

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

    if (-not (Test-Path -LiteralPath $DistRoot -PathType Container)) {
        throw "Release dist directory does not exist: $DistRoot"
    }
    $DistItem = Get-Item -LiteralPath $DistRoot -Force
    if (($DistItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Release dist must be a real directory, not a symlink or reparse point: $DistRoot"
    }

    [void](Resolve-MystiaRequiredFile -Path (Join-Path $DistRoot $script:MystiaReleasePackageName) -Label "Mod package")
    [void](Resolve-MystiaRequiredFile -Path (Join-Path $DistRoot $script:MystiaReleaseCompanionName) -Label "Windows companion executable")

    $PresentAndroid = @(
        $script:MystiaReleaseAndroidNames |
            Where-Object { Test-Path -LiteralPath (Join-Path $DistRoot $_) -PathType Leaf }
    )
    $UnexpectedAndroid = @(
        Get-ChildItem -LiteralPath $DistRoot -Filter "mystia-steward-companion-android-*.apk" -File |
            Where-Object { $script:MystiaReleaseAndroidNames -notcontains $_.Name }
    )
    if ($UnexpectedAndroid.Count -gt 0) {
        throw "Unexpected canonical Android APK asset: $($UnexpectedAndroid[0].Name)"
    }
    if ($RequireAndroid -and $PresentAndroid.Count -ne $script:MystiaReleaseAndroidNames.Count) {
        throw "Official releases require exactly the arm64-v8a and armeabi-v7a APK assets."
    }
    if (-not $RequireAndroid -and $PresentAndroid.Count -notin @(0, $script:MystiaReleaseAndroidNames.Count)) {
        throw "Android release assets must be absent or contain both canonical ABI APKs."
    }
    $IncludeAndroid = $PresentAndroid.Count -eq $script:MystiaReleaseAndroidNames.Count
    foreach ($Name in $PresentAndroid) {
        [void](Resolve-MystiaRequiredFile -Path (Join-Path $DistRoot $Name) -Label "Android APK")
    }

    Assert-MystiaMetadataDestinations -DistRoot $DistRoot
    Assert-NoMystiaMetadataTransactions -DistRoot $DistRoot
    $Gh = Get-MystiaCommand -Name "gh" -InstallHint "Install GitHub CLI and authenticate before preparing release metadata."
    $PublishedAtUtc = [DateTimeOffset]::UtcNow.ToString(
        "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
        [System.Globalization.CultureInfo]::InvariantCulture
    )
    $TransactionRoot = Join-Path `
        $DistRoot `
        ".release-metadata-$([Guid]::NewGuid().ToString('N'))"
    $StagingRoot = Join-Path $TransactionRoot "staging"
    try {
        [void](New-Item -ItemType Directory -Path $StagingRoot)
        $TransactionItem = Get-Item -LiteralPath $TransactionRoot -Force
        if (($TransactionItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Release metadata transaction root must be a real directory: $TransactionRoot"
        }

        $CatalogPath = Join-Path $StagingRoot $script:MystiaReleaseCatalogName
        New-MystiaUpdateCatalog `
            -Gh $Gh `
            -Node $Node `
            -OutputPath $CatalogPath `
            -Version $Version `
            -Channel $Channel `
            -ResolvedTitle $Title `
            -Notes $Notes.Text `
            -PublishedAtUtc $PublishedAtUtc

        $PackagePath = Join-Path $DistRoot $script:MystiaReleasePackageName
        $CatalogPath = Resolve-MystiaRequiredFile -Path $CatalogPath -Label "staged update catalog"
        $PackageItem = Get-Item -LiteralPath $PackagePath
        $CatalogItem = Get-Item -LiteralPath $CatalogPath
        $Manifest = [ordered]@{
            schemaVersion = 1
            version = $Version
            tag = $Tag
            channel = $Channel
            targetCommitSha = $TargetCommitSha
            packageAsset = $script:MystiaReleasePackageName
            packageSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $PackagePath).Hash.ToLowerInvariant()
            packageSize = $PackageItem.Length
            catalogAsset = $script:MystiaReleaseCatalogName
            catalogSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $CatalogPath).Hash.ToLowerInvariant()
            catalogSize = $CatalogItem.Length
            releaseUrl = "https://github.com/$Repo/releases/tag/$Tag"
            publishedAtUtc = $PublishedAtUtc
        }
        Write-MystiaUtf8WithoutBom `
            -Path (Join-Path $StagingRoot $script:MystiaReleaseManifestName) `
            -Content (($Manifest | ConvertTo-Json -Depth 4) + [Environment]::NewLine)

        $PayloadNames = @(Get-MystiaPayloadAssetNames -IncludeAndroid $IncludeAndroid)
        $ChecksumLines = @(
            $PayloadNames |
                Sort-Object -CaseSensitive |
                ForEach-Object {
                    $Path = Resolve-MystiaPreparedAssetPath `
                        -DistRoot $DistRoot `
                        -StagingRoot $StagingRoot `
                        -Name $_
                    $Path = Resolve-MystiaRequiredFile -Path $Path -Label "staged release asset"
                    $Hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant()
                    "$Hash  $_"
                }
        )
        Write-MystiaUtf8WithoutBom `
            -Path (Join-Path $StagingRoot $script:MystiaReleaseChecksumsName) `
            -Content (($ChecksumLines -join "`n") + "`n")

        Assert-MystiaStagedMetadata `
            -DistRoot $DistRoot `
            -StagingRoot $StagingRoot `
            -IncludeAndroid $IncludeAndroid `
            -ExpectedVersion $Version `
            -ExpectedTag $Tag `
            -ExpectedChannel $Channel `
            -ExpectedTargetCommitSha $TargetCommitSha
        Assert-MystiaMetadataDestinations -DistRoot $DistRoot
        Commit-MystiaPreparedMetadata `
            -DistRoot $DistRoot `
            -TransactionRoot $TransactionRoot `
            -IncludeAndroid $IncludeAndroid
    }
    finally {
        if (Test-Path -LiteralPath $TransactionRoot) {
            $BackupRoot = Join-Path $TransactionRoot "backup"
            $HasRetainedBackup =
                (Test-Path -LiteralPath $BackupRoot -PathType Container) -and
                @(Get-ChildItem -LiteralPath $BackupRoot -Force).Count -gt 0
            if ($HasRetainedBackup) {
                Write-Warning "Release metadata rollback backup was retained for manual recovery: $BackupRoot"
            }
            else {
                Remove-Item -LiteralPath $TransactionRoot -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    }

    Write-Host ""
    Write-Host "Release assets prepared for $Tag at commit $TargetCommitSha" -ForegroundColor Green
    foreach ($Name in (Get-MystiaReleaseAssetNames -IncludeAndroid $IncludeAndroid)) {
        Write-Host "  - $Name"
    }
}
finally {
    Pop-Location
}
