#requires -Version 7.6
param(
    [Parameter(Mandatory)][string]$ToolchainLock,
    [Parameter(Mandatory)][string]$Directory,
    [string]$ArchiveDirectory = ''
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$lock = Get-Content -LiteralPath $ToolchainLock -Raw | ConvertFrom-Json
if ($PSVersionTable.PSVersion.ToString() -cne $lock.powershell) { throw 'PowerShell differs from root lock' }
$profile = $lock.flutterAndroidProbe.api37Windows
if (!$profile -or $profile.image.package -cne 'system-images;android-37.0;google_apis;x86_64' -or
    $profile.image.api -cne '37.0' -or $profile.image.abi -cne 'x86_64' -or
    [version]$profile.emulator.version -lt [version]$profile.image.minEmulator -or
    $profile.memoryMb -ne 4096 -or $profile.cores -ne 2) { throw 'Invalid isolated API37 test profile' }
if (![IO.Path]::IsPathFullyQualified($Directory) -or (Test-Path -LiteralPath $Directory)) { throw 'A new absolute environment directory is required' }
foreach ($name in @('emulator','image')) {
    $entry = $profile.$name
    if ($entry.url -cnotmatch '^https://dl\.google\.com/android/repository/[A-Za-z0-9_./-]+\.zip$' -or
        $entry.sha1 -cnotmatch '^[a-f0-9]{40}$' -or $entry.size -le 0 -or $entry.size -gt 3GB) { throw 'Invalid official archive identity' }
}
New-Item -ItemType Directory -Path $Directory | Out-Null
$lockHash = (Get-FileHash -LiteralPath $ToolchainLock -Algorithm SHA256).Hash.ToLowerInvariant()
$evidence = [ordered]@{ schemaVersion=1; kind='isolated-api37-environment'; rootLockSha256=$lockHash; installerSha256=(Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant(); profile=$profile; archives=@(); startedAt=[DateTime]::UtcNow.ToString('o') }
foreach ($name in @('emulator','image')) {
    $entry = $profile.$name
    $archive = Join-Path $Directory "$name.zip"
    if ($ArchiveDirectory) {
        Copy-Item -LiteralPath (Join-Path $ArchiveDirectory "$name.zip") -Destination $archive
    } else {
        & curl.exe --fail --location --retry 2 --retry-delay 2 --connect-timeout 20 --max-time 1800 --output $archive $entry.url
        if ($LASTEXITCODE -ne 0) { throw "Official $name archive download failed; partial evidence retained" }
    }
    if ((Get-Item -LiteralPath $archive).Length -ne $entry.size -or
        (Get-FileHash -LiteralPath $archive -Algorithm SHA1).Hash.ToLowerInvariant() -cne $entry.sha1) { throw "Official $name archive differs from root lock" }
    $sha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    $destination = Join-Path $Directory $name
    $prefix = [IO.Path]::GetFullPath($destination).TrimEnd('\') + '\'
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($file in $zip.Entries) {
            $target = [IO.Path]::GetFullPath((Join-Path $destination $file.FullName))
            if (!$target.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or !$names.Add($target) -or
                (($file.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000) { throw 'Unsafe archive entry' }
        }
    } finally { $zip.Dispose() }
    Expand-Archive -LiteralPath $archive -DestinationPath $destination
    $evidence.archives += [ordered]@{name=$name; path=$archive; size=$entry.size; sha1=$entry.sha1; sha256=$sha256; extractedDirectory=$destination}
    $evidence | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $Directory 'installation-progress.json') -Encoding utf8NoBOM
}
$emulatorProperties = Get-Content -LiteralPath (Join-Path $Directory 'emulator/emulator/source.properties') -Raw
$imageProperties = Get-Content -LiteralPath (Join-Path $Directory 'image/x86_64/source.properties') -Raw
if ($emulatorProperties -notmatch "(?m)^Pkg\.Revision=$([regex]::Escape($profile.emulator.version))\s*$" -or
    $imageProperties -notmatch "(?m)^AndroidVersion\.ApiLevel=$([regex]::Escape($profile.image.api))\s*$" -or
    $imageProperties -notmatch "(?m)^Pkg\.Revision=$([regex]::Escape($profile.image.revision))\s*$" -or
    $imageProperties -notmatch '(?m)^SystemImage\.Abi=x86_64\s*$') { throw 'Extracted tool/image version differs from root lock' }
if ((Get-FileHash -LiteralPath $ToolchainLock -Algorithm SHA256).Hash.ToLowerInvariant() -cne $lockHash) { throw 'Root lock changed during preparation' }
$evidence.emulatorProperties = $emulatorProperties
$evidence.imageProperties = $imageProperties
$evidence.completedAt = [DateTime]::UtcNow.ToString('o')
$evidence | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $Directory 'environment-evidence.json') -Encoding utf8NoBOM
Get-Content -LiteralPath (Join-Path $Directory 'environment-evidence.json')
