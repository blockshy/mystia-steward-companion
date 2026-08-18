#requires -Version 7.0

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$CommonPath = Join-Path $RepoRoot "mods/bepinex/tools/release-common.ps1"
$PreparePath = Join-Path $RepoRoot "mods/bepinex/tools/prepare-release-assets.ps1"
. $CommonPath

$Tokens = $null
$Errors = $null
$PrepareAst = [System.Management.Automation.Language.Parser]::ParseFile(
    $PreparePath,
    [ref]$Tokens,
    [ref]$Errors
)
if ($Errors.Count -ne 0) {
    throw "PowerShell parser errors in $PreparePath`: $($Errors.Message -join '; ')"
}
$CommitAst = $PrepareAst.Find(
    {
        param($Node)
        $Node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
            $Node.Name -ceq "Commit-MystiaPreparedMetadata"
    },
    $true
)
if ($null -eq $CommitAst) {
    throw "Missing PowerShell function: Commit-MystiaPreparedMetadata"
}
Invoke-Expression $CommitAst.Extent.Text

function Write-TestFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Content
    )

    [System.IO.File]::WriteAllText($Path, $Content, [System.Text.UTF8Encoding]::new($false))
}

function New-TestTransaction {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][bool]$ValidChecksums,
        [switch]$OmitChecksums
    )

    $DistRoot = Join-Path $Root "dist"
    $TransactionRoot = Join-Path $DistRoot ".release-metadata-test"
    $StagingRoot = Join-Path $TransactionRoot "staging"
    [void](New-Item -ItemType Directory -Path $StagingRoot -Force)

    Write-TestFile -Path (Join-Path $DistRoot $script:MystiaReleasePackageName) -Content "package"
    Write-TestFile -Path (Join-Path $DistRoot $script:MystiaReleaseCompanionName) -Content "companion"
    foreach ($Name in @(
        $script:MystiaReleaseCatalogName,
        $script:MystiaReleaseManifestName,
        $script:MystiaReleaseChecksumsName
    )) {
        Write-TestFile -Path (Join-Path $DistRoot $Name) -Content "old-$Name"
    }

    Write-TestFile -Path (Join-Path $StagingRoot $script:MystiaReleaseCatalogName) -Content '{"new":true}'
    Write-TestFile -Path (Join-Path $StagingRoot $script:MystiaReleaseManifestName) -Content '{"new":true}'
    if (-not $OmitChecksums) {
        $Lines = @(
            Get-MystiaPayloadAssetNames -IncludeAndroid $false |
                Sort-Object -CaseSensitive |
                ForEach-Object {
                    $AssetRoot = if ($_ -cin @(
                        $script:MystiaReleaseCatalogName,
                        $script:MystiaReleaseManifestName
                    )) { $StagingRoot } else { $DistRoot }
                    $Hash = if ($ValidChecksums) {
                        (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $AssetRoot $_)).Hash.ToLowerInvariant()
                    }
                    else {
                        "0" * 64
                    }
                    "$Hash  $_"
                }
        )
        Write-TestFile `
            -Path (Join-Path $StagingRoot $script:MystiaReleaseChecksumsName) `
            -Content (($Lines -join "`n") + "`n")
    }

    return @{
        DistRoot = $DistRoot
        TransactionRoot = $TransactionRoot
    }
}

function Assert-OldMetadataPreserved {
    param([Parameter(Mandatory = $true)][string]$DistRoot)

    foreach ($Name in @(
        $script:MystiaReleaseCatalogName,
        $script:MystiaReleaseManifestName,
        $script:MystiaReleaseChecksumsName
    )) {
        $Actual = [System.IO.File]::ReadAllText((Join-Path $DistRoot $Name))
        if ($Actual -cne "old-$Name") {
            throw "Old metadata was not restored exactly after failed preparation: $Name"
        }
    }
}

$AuditRoot = Join-Path ([System.IO.Path]::GetTempPath()) "mystia-prepare-transaction-$([Guid]::NewGuid().ToString('N'))"
try {
    [void](New-Item -ItemType Directory -Path $AuditRoot)

    $Success = New-TestTransaction -Root (Join-Path $AuditRoot "success") -ValidChecksums $true
    Commit-MystiaPreparedMetadata `
        -DistRoot $Success.DistRoot `
        -TransactionRoot $Success.TransactionRoot `
        -IncludeAndroid $false
    foreach ($Name in @($script:MystiaReleaseCatalogName, $script:MystiaReleaseManifestName)) {
        $Actual = [System.IO.File]::ReadAllText((Join-Path $Success.DistRoot $Name))
        if ($Actual -cne '{"new":true}') {
            throw "Prepared metadata was not committed: $Name"
        }
    }
    Assert-MystiaPreparedChecksums -DistRoot $Success.DistRoot -IncludeAndroid $false
    if (Test-Path -LiteralPath (Join-Path $Success.TransactionRoot "backup")) {
        throw "Validated metadata commit retained the rollback backup."
    }

    $Invalid = New-TestTransaction -Root (Join-Path $AuditRoot "invalid") -ValidChecksums $false
    try {
        Commit-MystiaPreparedMetadata `
            -DistRoot $Invalid.DistRoot `
            -TransactionRoot $Invalid.TransactionRoot `
            -IncludeAndroid $false
        throw "Expected invalid staged checksums to fail after commit and trigger rollback."
    }
    catch {
        if ($_.Exception.Message -ceq "Expected invalid staged checksums to fail after commit and trigger rollback.") {
            throw
        }
    }
    Assert-OldMetadataPreserved -DistRoot $Invalid.DistRoot

    $Incomplete = New-TestTransaction `
        -Root (Join-Path $AuditRoot "incomplete") `
        -ValidChecksums $true `
        -OmitChecksums
    try {
        Commit-MystiaPreparedMetadata `
            -DistRoot $Incomplete.DistRoot `
            -TransactionRoot $Incomplete.TransactionRoot `
            -IncludeAndroid $false
        throw "Expected incomplete staged metadata to fail before commit."
    }
    catch {
        if ($_.Exception.Message -ceq "Expected incomplete staged metadata to fail before commit.") {
            throw
        }
    }
    Assert-OldMetadataPreserved -DistRoot $Incomplete.DistRoot
}
finally {
    if (Test-Path -LiteralPath $AuditRoot) {
        Remove-Item -LiteralPath $AuditRoot -Recurse -Force
    }
}

Write-Host "Prepare release metadata transaction audit passed."
