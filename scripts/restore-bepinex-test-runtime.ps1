#requires -Version 7.0
<#
.SYNOPSIS
恢复隔离 HTTP 宿主必需的锁定 BepInEx 运行库，不连接游戏、不安装到游戏目录。
.DESCRIPTION
整包摘要来自 references.lock.json，单文件路径/长度/摘要来自宿主共用的 props。
可传已下载的原始包进行离线恢复；缺省只下载明确锁定的官方版本，不解析 latest。
已有正确文件可复用，错误文件保留并拒绝覆盖；所有下载和临时提取文件均在 finally 清理。
#>
[CmdletBinding()]
param([string]$ArchivePath, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repository 'temp/flutter-mod-host-runtime' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
# 拒绝目录链上的链接，防止缓存恢复意外写进另一个工作区或游戏安装。
for ($cursor = [IO.DirectoryInfo]::new($output); $null -ne $cursor; $cursor = $cursor.Parent) {
    if ($cursor.Exists -and ($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "运行库输出目录不能包含重解析点：$($cursor.FullName)"
    }
}
$reference = Get-Content -LiteralPath (Join-Path $repository 'mods/bepinex/References/references.lock.json') -Raw | ConvertFrom-Json
[xml]$properties = Get-Content -LiteralPath (Join-Path $repository 'tests/flutter-contracts/host/BepInExRuntime.props') -Raw
$runtime = $properties.Project.PropertyGroup
$fileName = [string]$runtime.BepInExRuntimeFileName
$entryName = [string]$runtime.BepInExRuntimeArchiveEntry
$expectedHash = [string]$runtime.BepInExRuntimeSha256
$expectedSize = [long]$runtime.BepInExRuntimeFileSize
if ([IO.Path]::GetFileName($fileName) -cne $fileName -or $expectedHash -notmatch '^[A-Fa-f0-9]{64}$' -or $expectedSize -le 0) {
    throw '宿主运行库锁无效。'
}
function Test-RuntimeFile([string]$Path) {
    $file = Get-Item -LiteralPath $Path
    return (-not ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -and
        $file.Length -eq $expectedSize -and
        (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -eq $expectedHash)
}
$destination = Join-Path $output $fileName
if (Test-Path -LiteralPath $destination) {
    if (-not (Test-RuntimeFile $destination)) { throw "已有运行库与锁不符，已保留原文件：$destination" }
    Write-Output "锁定测试运行库已核验：$destination"
    return
}
$download = $null
$temporary = $null
try {
    if (-not $ArchivePath) {
        $temporaryRoot = Join-Path $repository 'temp'
        [IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
        $download = Join-Path $temporaryRoot "bepinex-runtime-$([Guid]::NewGuid().ToString('N')).zip"
        $source = $reference.source.bepInEx
        $asset = [string]$source.asset
        if ($asset -notmatch '^BepInEx-Unity\.IL2CPP-win-x64-[A-Za-z0-9.+-]+\.zip$' -or [int]$source.build -le 0) {
            throw 'BepInEx 原始包锁无效。'
        }
        $url = "https://builds.bepinex.dev/projects/bepinex_be/$($source.build)/$([Uri]::EscapeDataString($asset))"
        $ProgressPreference = 'SilentlyContinue'
        Invoke-WebRequest -Uri $url -OutFile $download -TimeoutSec 120
        $ArchivePath = $download
    }
    if ((Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash -ne $reference.source.bepInEx.sha256) {
        throw '原始 BepInEx 分发包摘要不符，拒绝提取。'
    }
    [IO.Directory]::CreateDirectory($output) | Out-Null
    $temporary = Join-Path $output ".$fileName.$([Guid]::NewGuid().ToString('N')).tmp"
    $archive = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($ArchivePath))
    try {
        $entries = @($archive.Entries | Where-Object { $_.FullName -ceq $entryName })
        if ($entries.Count -ne 1 -or $entries[0].Length -ne $expectedSize) {
            throw '锁定分发包中的运行库路径、唯一性或长度不符。'
        }
        # 只提取唯一目标，不展开其他条目，因此压缩包路径不能影响输出文件名。
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entries[0], $temporary, $false)
    } finally { $archive.Dispose() }
    if (-not (Test-RuntimeFile $temporary)) { throw '提取后的运行库摘要不符。' }
    [IO.File]::Move($temporary, $destination)
    Write-Output "锁定测试运行库已恢复：$destination"
} finally {
    # 只清理由本次调用创建的精确临时文件，不删除调用者提供的压缩包或整个目录。
    foreach ($path in @($temporary, $download)) {
        if ($path -and (Test-Path -LiteralPath $path -PathType Leaf)) { Remove-Item -LiteralPath $path -Force }
    }
}
