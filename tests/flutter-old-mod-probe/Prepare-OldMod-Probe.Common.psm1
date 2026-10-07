Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-OldModPath {
    param([string]$Path, [switch]$NewDirectory)
    if ([string]::IsNullOrWhiteSpace($Path) -or $Path -match '[\x00-\x1f]' -or
        ![IO.Path]::IsPathFullyQualified($Path) -or ($Path -split '[\\/]') -contains '..') {
        throw 'An explicit absolute filesystem path without traversal is required.'
    }
    $full = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Path))
    $current = $full
    if ($NewDirectory) {
        if (Get-Item -LiteralPath $full -Force -ErrorAction SilentlyContinue) { throw "Workspace already exists: $full" }
        $current = [IO.Path]::GetDirectoryName($full)
    }
    while (![string]::IsNullOrEmpty($current)) {
        $item = Get-Item -LiteralPath $current -Force
        if (!$item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Directory or ancestor is not a real directory: $current"
        }
        $parent = [IO.Path]::GetDirectoryName($current)
        if ($parent -eq $current) { break }
        $current = $parent
    }
    return $full
}

function Test-OldModOverlap {
    param([string]$Left, [string]$Right)
    $comparison = [StringComparison]::OrdinalIgnoreCase
    $leftPrefix = $Left.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $rightPrefix = $Right.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    return $Left.Equals($Right, $comparison) -or
        $Left.StartsWith($rightPrefix, $comparison) -or $Right.StartsWith($leftPrefix, $comparison)
}

function Assert-OldModFile {
    param([string]$Path)
    [void](Resolve-OldModPath ([IO.Path]::GetDirectoryName($Path)))
    $item = Get-Item -LiteralPath $Path -Force
    if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Expected a regular file without links: $Path"
    }
    return $item
}

function Get-OldModSnapshot {
    param([string]$Root)
    [void](Resolve-OldModPath $Root)
    $files = [Collections.Generic.List[object]]::new()
    $directories = [Collections.Generic.List[string]]::new()
    $pending = [Collections.Generic.Queue[string]]::new()
    $pending.Enqueue($Root)
    [long]$total = 0
    while ($pending.Count -gt 0) {
        $directory = $pending.Dequeue()
        [void](Resolve-OldModPath $directory)
        foreach ($item in Get-ChildItem -LiteralPath $directory -Force) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Source links/reparse points are forbidden: $($item.FullName)" }
            $relative = [IO.Path]::GetRelativePath($Root, $item.FullName).Replace('\', '/')
            if ($item.PSIsContainer) {
                $directories.Add($relative)
                $pending.Enqueue($item.FullName)
            } else {
                $beforeLength = $item.Length
                $beforeTime = $item.LastWriteTimeUtc.Ticks
                $hash = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
                $item.Refresh()
                if ($item.Length -ne $beforeLength -or $item.LastWriteTimeUtc.Ticks -ne $beforeTime) {
                    throw "Source changed while hashing: $relative"
                }
                $files.Add([pscustomobject]@{ path = $relative; size = [long]$beforeLength; sha256 = $hash })
                $total += $beforeLength
            }
            if ($files.Count + $directories.Count -gt 100000 -or $total -gt 64GB) {
                throw 'Source exceeds the probe limit of 100000 entries / 64 GiB.'
            }
        }
    }
    return [pscustomobject]@{
        directories = @($directories | Sort-Object -CaseSensitive)
        files = @($files | Sort-Object -Property path -CaseSensitive)
        totalBytes = $total
    }
}

function Assert-OldModSnapshotEqual {
    param($Expected, $Actual, [string]$Label)
    $left = $Expected | ConvertTo-Json -Depth 5 -Compress
    $right = $Actual | ConvertTo-Json -Depth 5 -Compress
    if ($left -cne $right) { throw "$Label changed; retain the failed workspace and do not run it." }
}

function Test-OldModExcluded {
    param([string]$RelativePath)
    return $RelativePath -ieq 'BepInEx/config/com.tyukki.mystia-steward-companion.cfg' -or
        $RelativePath -ieq 'BepInEx/config/MystiaStewardCompanion' -or
        $RelativePath.StartsWith('BepInEx/config/MystiaStewardCompanion/', [StringComparison]::OrdinalIgnoreCase)
}

function Select-OldModGameSnapshot {
    param($Snapshot)
    $files = @($Snapshot.files | Where-Object { !(Test-OldModExcluded $_.path) })
    return [pscustomobject]@{
        directories = @($Snapshot.directories | Where-Object { !(Test-OldModExcluded $_) })
        files = $files
        totalBytes = [long](($files | Measure-Object -Property size -Sum).Sum)
    }
}

function Copy-OldModSnapshot {
    param([string]$Source, [string]$Destination, $Snapshot)
    if (Get-Item -LiteralPath $Destination -Force -ErrorAction SilentlyContinue) { throw "Copy destination exists: $Destination" }
    [void](Resolve-OldModPath $Source)
    [void](Resolve-OldModPath ([IO.Path]::GetDirectoryName($Destination)))
    New-Item -ItemType Directory -Path $Destination -ErrorAction Stop | Out-Null
    foreach ($directory in $Snapshot.directories) {
        $path = Join-Path $Destination $directory
        [void](Resolve-OldModPath ([IO.Path]::GetDirectoryName($path)))
        New-Item -ItemType Directory -Path $path -ErrorAction Stop | Out-Null
    }
    foreach ($file in $Snapshot.files) {
        $from = Join-Path $Source $file.path
        $to = Join-Path $Destination $file.path
        [void](Assert-OldModFile $from)
        [void](Resolve-OldModPath ([IO.Path]::GetDirectoryName($to)))
        $inputStream = [IO.File]::Open($from, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        try {
            $outputStream = [IO.File]::Open($to, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            try { $inputStream.CopyTo($outputStream); $outputStream.Flush($true) } finally { $outputStream.Dispose() }
        } finally { $inputStream.Dispose() }
        $copied = Get-Item -LiteralPath $to
        if ($copied.Length -ne $file.size -or (Get-FileHash -LiteralPath $to -Algorithm SHA256).Hash -ine $file.sha256) {
            throw "Copied file differs from the frozen source: $($file.path)"
        }
    }
    Assert-OldModSnapshotEqual $Snapshot (Get-OldModSnapshot $Destination) 'Copied directory'
}

function Get-OldModPluginVersion {
    param([string]$Path)
    [void](Assert-OldModFile $Path)
    $stream = [IO.File]::OpenRead($Path)
    try {
        $pe = [Reflection.PortableExecutable.PEReader]::new($stream)
        try {
            if (!$pe.HasMetadata) { throw 'The plugin DLL has no managed metadata.' }
            $reader = [Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
            $versions = [Collections.Generic.List[string]]::new()
            foreach ($handle in $reader.TypeDefinitions) {
                $type = $reader.GetTypeDefinition($handle)
                if ($reader.GetString($type.Namespace) -cne 'MystiaStewardCompanion.Plugin' -or
                    $reader.GetString($type.Name) -cne 'MystiaStewardCompanionPlugin') { continue }
                foreach ($fieldHandle in $type.GetFields()) {
                    $field = $reader.GetFieldDefinition($fieldHandle)
                    if ($reader.GetString($field.Name) -cne 'PluginVersion') { continue }
                    if (!($field.Attributes -band [Reflection.FieldAttributes]::Literal)) { throw 'PluginVersion is not a literal constant.' }
                    $constantHandle = $field.GetDefaultValue()
                    if ($constantHandle.IsNil) { throw 'PluginVersion has no constant value.' }
                    $constant = $reader.GetConstant($constantHandle)
                    if ($constant.TypeCode -ne [Reflection.Metadata.ConstantTypeCode]::String) { throw 'PluginVersion must be a string constant.' }
                    $versions.Add([Text.Encoding]::Unicode.GetString($reader.GetBlobBytes($constant.Value)))
                }
            }
            if ($versions.Count -ne 1) { throw 'The exact plugin type/version constant is missing or ambiguous.' }
            return $versions[0]
        } finally { $pe.Dispose() }
    } finally { $stream.Dispose() }
}

function Assert-OldModLaunchConfiguration {
    param([string]$Source, $Snapshot)
    $dlls = @($Snapshot.files | Where-Object { $_.path -imatch '^BepInEx/plugins/(?:.*/)?MystiaStewardCompanion\.BepInEx\.dll$' })
    if ($dlls.Count -ne 1 -or $dlls[0].path -cne 'BepInEx/plugins/mystia-steward-companion/MystiaStewardCompanion.BepInEx.dll') {
        throw 'Expected exactly one plugin DLL at the canonical product path.'
    }
    foreach ($relative in @('doorstop_config.ini', 'BepInEx/config/BepInEx.cfg')) {
        $path = Join-Path $Source $relative
        if (!(Test-Path -LiteralPath $path -PathType Leaf)) { continue }
        [void](Assert-OldModFile $path)
        foreach ($line in [IO.File]::ReadAllLines($path)) {
            if ($line -match '^\s*[#;]' -or $line -notmatch '^\s*([^=]+?)\s*=\s*(.*?)\s*$') { continue }
            $key = $Matches[1].Trim()
            $value = $Matches[2].Trim().Trim('"', "'")
            if ($value -match '(?i)(?:^|[\s;"''])(?:[a-z]:|\\)|%[^%]+%|\$\{?env[:.]' -or $value.StartsWith('/')) {
                throw "Startup configuration requires review of a non-local path: $relative / $key"
            }
            if (($value -split '[\\/;]') -contains '..') { throw "Startup configuration contains parent traversal: $relative / $key" }
        }
    }
}

function Assert-OldModAcceptedProbe {
    param([string]$Directory)
    $expectedHash = '18c3a381e6f363150209605f65da553680e1e13b250a50eb90c6298d7438357b'
    $expectedCommit = '283bd56cd10564d64169a8ea521f9fdffe0019b4'
    foreach ($name in @('mystia-steward-companion-updater.exe', 'build-evidence.json', 'bundle-manifest.json', 'Start-Probe.ps1', 'README.md')) {
        [void](Assert-OldModFile (Join-Path $Directory $name))
    }
    $exe = Join-Path $Directory 'mystia-steward-companion-updater.exe'
    if ((Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash -ine $expectedHash) { throw 'Bootstrap SHA256 is not the accepted read-only P0 binary.' }
    $build = [IO.File]::ReadAllText((Join-Path $Directory 'build-evidence.json')) | ConvertFrom-Json
    if ($build.commit -cne $expectedCommit -or $build.bootstrapSha256 -cne $expectedHash -or
        $build.product -cne 'mystia-steward-companion' -or $build.kind -cne 'p0-read-only-updater-probe' -or $build.version -cne '1.3.1') {
        throw 'Build evidence does not match the accepted P0 build.'
    }
    return [pscustomobject]@{ executable = $exe; sha256 = $expectedHash; commit = $expectedCommit }
}

function Assert-OldModDiskBudget {
    param([long]$AvailableBytes, [long]$RequiredBytes)
    if ($AvailableBytes -lt $RequiredBytes) { throw "Insufficient free space: need $RequiredBytes bytes, available $AvailableBytes bytes." }
}

function Assert-OldModSteamAppIdFile {
    param([string]$Path)
    $file = Assert-OldModFile $Path
    if ($file.Length -notin @(7, 8, 9)) { throw 'Steam development file must contain only the real App ID 1584090.' }
    $bytes = [IO.File]::ReadAllBytes($Path)
    $value = [Text.Encoding]::ASCII.GetString($bytes)
    if ($value -cnotin @('1584090', "1584090`n", "1584090`r`n")) {
        throw 'Steam development file must contain only the real App ID 1584090.'
    }
}

function Read-OldModSteamIdentity {
    param([string]$ManifestPath, [string]$Source)
    if (![IO.Path]::IsPathFullyQualified($ManifestPath) -or $ManifestPath -match '[\x00-\x1f]' -or
        ($ManifestPath -split '[\\/]') -contains '..') { throw 'Steam manifest requires an absolute path without traversal.' }
    $path = [IO.Path]::GetFullPath($ManifestPath)
    $file = Assert-OldModFile $path
    $steamapps = [IO.Path]::GetDirectoryName($path)
    if ([IO.Path]::GetFileName($path) -cne 'appmanifest_1584090.acf' -or
        [IO.Path]::GetFileName($steamapps) -ine 'steamapps' -or $file.Length -gt 1MB) {
        throw 'Expected the bounded real appmanifest_1584090.acf in steamapps.'
    }
    $bytes = [IO.File]::ReadAllBytes($path)
    if ($bytes.Length -gt 1MB) { throw 'Steam manifest exceeds 1 MiB.' }
    $text = [Text.UTF8Encoding]::new($false, $true).GetString($bytes).TrimStart([char]0xfeff)
    # Read Valve's quoted key/value and brace format, only extracting the three
    # top-level AppState scalars. Reject unknown syntax and duplicate keys.
    $matches = [regex]::Matches($text, '\G(?:\s+|(?<quoted>"(?:\\[\\"]|[^"\\\x00-\x1f])*")|(?<brace>[{}]))')
    $tokens = [Collections.Generic.List[string]]::new()
    $consumed = 0
    foreach ($match in $matches) {
        $consumed += $match.Length
        if ($match.Groups['quoted'].Success -or $match.Groups['brace'].Success) { $tokens.Add($match.Value) }
    }
    if ($consumed -ne $text.Length -or $tokens.Count -lt 3 -or $tokens[0] -cne '"AppState"' -or $tokens[1] -cne '{') {
        throw 'Steam manifest has unsupported or ambiguous AppState syntax.'
    }
    $levels = [Collections.Generic.List[object]]::new()
    $levels.Add([Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase))
    $values = @{}
    $index = 2
    while ($index -lt $tokens.Count -and $levels.Count -gt 0) {
        $key = $tokens[$index++]
        if ($key -ceq '}') { $levels.RemoveAt($levels.Count - 1); continue }
        if (!$key.StartsWith('"') -or $index -ge $tokens.Count) { throw 'Malformed Steam manifest key/value pair.' }
        $key = $key.Substring(1, $key.Length - 2)
        if (!$levels[$levels.Count - 1].Add($key)) { throw 'Steam manifest contains duplicate or case-ambiguous keys.' }
        $value = $tokens[$index++]
        if ($levels.Count -eq 1 -and $key -iin @('appid', 'installdir', 'buildid')) {
            if ($key -cnotin @('appid', 'installdir', 'buildid') -or !$value.StartsWith('"')) {
                throw 'Steam identity fields must be unambiguous canonical scalars.'
            }
            $values[$key] = $value.Substring(1, $value.Length - 2)
        }
        if ($value -ceq '{') {
            if ($levels.Count -ge 32) { throw 'Steam manifest nesting exceeds the probe limit.' }
            $levels.Add([Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase))
        } elseif (!$value.StartsWith('"')) { throw 'Malformed Steam manifest value.' }
    }
    if ($levels.Count -ne 0 -or $index -ne $tokens.Count -or $values.Count -ne 3 -or
        $values.appid -cne '1584090' -or $values.installdir -cne 'Touhou Mystia Izakaya' -or
        $values.buildid -cnotmatch '\A[1-9][0-9]{0,19}\z') { throw 'Steam manifest does not identify the expected installed game and build.' }
    $expectedSource = [IO.Path]::GetFullPath((Join-Path $steamapps ('common/' + $values.installdir)))
    if ($expectedSource -ine $Source) { throw 'Steam manifest installation directory does not match the source game.' }
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $hash = [BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-', '').ToLowerInvariant() } finally { $sha.Dispose() }
    return [ordered]@{
        appId = $values.appid; buildId = $values.buildid; installDirectory = $values.installdir
        sourceManifestPath = $path; sourceManifestSha256 = $hash
        developmentFilePath = $null; developmentFileSha256 = $null
    }
}

function Assert-OldModSteamManifestUnchanged {
    param($Identity)
    [void](Assert-OldModFile $Identity.sourceManifestPath)
    if ((Get-FileHash -LiteralPath $Identity.sourceManifestPath -Algorithm SHA256).Hash -ine $Identity.sourceManifestSha256) {
        throw 'Steam source manifest changed during preparation; retain the failed workspace.'
    }
}

function Write-OldModNewJson {
    param([string]$Path, $Value)
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($Value | ConvertTo-Json -Depth 10) + "`n")
    $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
}
