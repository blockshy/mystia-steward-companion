#requires -Version 7.0
# 发布验证只读取 ZIP 条目流，不提取文件。共享 Node 验证器核对完整清单、目录边界和两包映射，
# 避免通过解压不可信条目产生路径写入；这里同时限制解压字节数，不能只相信中央目录声明。
function Read-MystiaZipRecords {
    param([Parameter(Mandatory = $true)][string]$Path)
    $full = [IO.Path]::GetFullPath($Path)
    for ($cursor = $full; $cursor; $cursor = [IO.Path]::GetDirectoryName($cursor)) {
        $item = Get-Item -LiteralPath $cursor -Force
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw '发布 ZIP 路径不能经过重解析点。' }
    }
    $stream = [IO.FileStream]::new($full, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        if ($stream.Length -le 0 -or $stream.Length -gt 2147483648) { throw '发布 ZIP 大小无效。' }
        $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Read, $true)
        try {
            if ($zip.Entries.Count -gt 16386) { throw '发布 ZIP 条目过多。' }
            $rows = [Collections.Generic.List[object]]::new()
            [long]$total = 0
            foreach ($entry in $zip.Entries) {
                if ($entry.Length -lt 0 -or $entry.Length -gt 536870912) { throw '发布 ZIP 条目超出大小上限。' }
                $total += $entry.Length
                if ($total -gt 2147483648) { throw '发布 ZIP 解压总大小超出上限。' }
                $capture = $entry.FullName -ceq 'mystia-steward-companion/package-manifest.json' -or
                    $entry.FullName -ceq 'mystia-steward-companion-windows-x64/portable-client-v1.manifest'
                if ($capture -and $entry.Length -gt 4194304) { throw '发布 ZIP 清单超出大小上限。' }
                $inputStream = $entry.Open()
                $digest = [Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
                $textBuffer = if ($capture) { [IO.MemoryStream]::new() } else { $null }
                try {
                    $buffer = [byte[]]::new(262144)
                    [long]$read = 0
                    while (($count = $inputStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                        $read += $count
                        if ($read -gt $entry.Length) { throw 'ZIP 实际解压大小超过声明。' }
                        $digest.AppendData($buffer, 0, $count)
                        if ($capture) { $textBuffer.Write($buffer, 0, $count) }
                    }
                    if ($read -ne $entry.Length) { throw 'ZIP 实际解压大小与声明不符。' }
                    $row = [ordered]@{ path = $entry.FullName; size = $read
                        sha256 = [Convert]::ToHexString($digest.GetHashAndReset()).ToLowerInvariant()
                        attributes = $entry.ExternalAttributes }
                    if ($capture) { $row.text = [Text.UTF8Encoding]::new($false, $true).GetString($textBuffer.ToArray()) }
                    $rows.Add($row)
                } finally {
                    if ($textBuffer) { $textBuffer.Dispose() }
                    $digest.Dispose(); $inputStream.Dispose()
                }
            }
            return @{ entries = $rows.ToArray() }
        } finally { $zip.Dispose() }
    } finally { $stream.Dispose() }
}

function Assert-MystiaWindowsPackagePair {
    param(
        [Parameter(Mandatory = $true)][string]$ModPackageZip,
        [Parameter(Mandatory = $true)][string]$StandaloneZip,
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [string]$ExpectedVersion = '',
        [string]$ExpectedCommit = ''
    )
    $records = @{ mod = Read-MystiaZipRecords $ModPackageZip; standalone = Read-MystiaZipRecords $StandaloneZip
        version = $ExpectedVersion; sourceCommit = $ExpectedCommit }
    # 结构化 JSON 通过 stdin 传输，不把条目名或清单正文拼入 shell 代码，也不落盘临时解压树。
    $records | ConvertTo-Json -Depth 8 -Compress | & node (Join-Path $RepositoryRoot 'scripts/portable-client-package.mjs') verify-zip-records | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Windows 两 ZIP 完整载荷及便携目录契约验证失败。' }
}
