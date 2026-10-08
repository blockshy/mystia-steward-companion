param([Parameter(Mandatory = $true)][string]$ArchiveDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path $PSScriptRoot -Parent
Set-Location $repo
$toolchain = Get-Content toolchain.lock.json -Raw | ConvertFrom-Json -AsHashtable
if ($PSVersionTable.PSVersion.ToString() -cne $toolchain.powershell -or (& node --version) -cne "v$($toolchain.node)") { throw 'Restore requires locked Node/PowerShell.' }
$lock = Get-Content mods/bepinex/References/references.lock.json -Raw | ConvertFrom-Json -AsHashtable
if ($lock.bundle.repository -cne 'blockshy/mystia-steward-build-assets' -or
    $lock.bundle.tag -cnotmatch '\A[A-Za-z0-9][A-Za-z0-9_.-]*\z' -or $lock.bundle.asset -cnotmatch '\A[A-Za-z0-9][A-Za-z0-9_.-]*\.zip\z') { throw 'Reference coordinates differ from the scoped repository.' }
$releaseJson = & gh api "repos/blockshy/mystia-steward-build-assets/releases/tags/$([Uri]::EscapeDataString($lock.bundle.tag))"
if ($LASTEXITCODE -ne 0) { throw 'Read-only reference release lookup failed.' }
$release = $releaseJson | ConvertFrom-Json -AsHashtable
if ($release.tag_name -cne $lock.bundle.tag -or $release.draft -cne $false -or $release.prerelease -cne $false -or $release.immutable -cne $true -or
    $release.assets.Count -ne 1 -or $release.assets[0].name -cne $lock.bundle.asset -or $release.assets[0].state -cne 'uploaded' -or
    $release.assets[0].size -ne $lock.bundle.size -or $release.assets[0].digest -cne "sha256:$($lock.bundle.sha256)") { throw 'Immutable reference release identity differs from the lock.' }
$archiveRoot = [IO.Path]::GetFullPath($ArchiveDirectory)
if (Test-Path -LiteralPath $archiveRoot) { throw 'Reference download directory must be new.' }
[IO.Directory]::CreateDirectory($archiveRoot) | Out-Null
& gh release download $lock.bundle.tag --repo 'blockshy/mystia-steward-build-assets' --pattern $lock.bundle.asset --dir $archiveRoot
if ($LASTEXITCODE -ne 0) { throw 'Reference asset download failed.' }
$officialArchive = Join-Path $archiveRoot $lock.source.bepInEx.asset
& node scripts/download-bepinex-reference.mjs --output $officialArchive
if ($LASTEXITCODE -ne 0) { throw 'Official BepInEx archive download/verification failed.' }
& node scripts/restore-build-references.mjs --archive (Join-Path $archiveRoot $lock.bundle.asset) --bepinex-archive $officialArchive --output mods/bepinex/References
if ($LASTEXITCODE -ne 0) { throw 'Locked archive/file verification failed.' }
