#requires -Version 7.0
# 使用真实 ZIP 编解码验证发布门禁，不提取或执行包载荷；所有条目均来自本次创建的合成树。
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $repository 'mods/bepinex/tools/windows-package-pair.ps1')
. (Join-Path $PSScriptRoot 'windows-package-fixture.ps1')
$temporary = Join-Path $repository 'temp'
$sandbox = Join-Path $temporary ('portable-zip-audit-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($sandbox)
$mod = Join-Path $sandbox 'mystia-steward-companion-bepinex.zip'
$standalone = Join-Path $sandbox 'mystia-steward-companion-companion-windows-x64.zip'
$prefix = 'mystia-steward-companion-windows-x64/'
function Add-TextEntry($Zip, [string]$Name, [string]$Text, [int]$Attributes = 0) {
    $entry = $Zip.CreateEntry($Name); $entry.ExternalAttributes = $Attributes
    $stream = $entry.Open()
    try { $bytes = [Text.UTF8Encoding]::new($false).GetBytes($Text); $stream.Write($bytes) }
    finally { $stream.Dispose() }
}
try {
    New-MystiaSyntheticWindowsPackagePair -Root $sandbox
    Assert-MystiaWindowsPackagePair -ModPackageZip $mod -StandaloneZip $standalone -RepositoryRoot $repository -ExpectedVersion '9.9.9' -ExpectedCommit ('9' * 40)
    $checks = 1
    foreach ($kind in @('flat-root', 'traversal', 'duplicate', 'case-duplicate', 'empty-directory', 'client-data', 'symlink', 'directory-attribute', 'marker-crlf', 'missing-marker', 'modified-payload')) {
        $bad = Join-Path $sandbox ($kind + '.zip'); Copy-Item -LiteralPath $standalone -Destination $bad
        $zip = [IO.Compression.ZipFile]::Open($bad, [IO.Compression.ZipArchiveMode]::Update)
        try {
            switch ($kind) {
                'flat-root' { Add-TextEntry $zip 'mystia-steward-companion-portable.exe' 'unexpected root payload' }
                'traversal' { Add-TextEntry $zip ($prefix + '../outside') 'never extract this' }
                'duplicate' { Add-TextEntry $zip ($prefix + 'flutter_windows.dll') 'duplicate' }
                'case-duplicate' { Add-TextEntry $zip ($prefix + 'FLUTTER_WINDOWS.DLL') 'duplicate' }
                'empty-directory' { Add-TextEntry $zip ($prefix + 'unknown/') '' }
                'client-data' { Add-TextEntry $zip ($prefix + 'client-data/preferences.json') 'synthetic private preference' }
                'symlink' { $zip.GetEntry($prefix + 'flutter_windows.dll').ExternalAttributes = -1610612736 }
                'directory-attribute' { $zip.GetEntry($prefix + 'flutter_windows.dll').ExternalAttributes = 16 }
                'missing-marker' { $zip.GetEntry($prefix + 'portable-client-v1.manifest').Delete() }
                'marker-crlf' {
                    $entry = $zip.GetEntry($prefix + 'portable-client-v1.manifest'); $reader = [IO.StreamReader]::new($entry.Open())
                    try { $text = $reader.ReadToEnd() } finally { $reader.Dispose() }
                    $entry.Delete(); Add-TextEntry $zip ($prefix + 'portable-client-v1.manifest') ($text.Replace("`n", "`r`n"))
                }
                'modified-payload' {
                    $zip.GetEntry($prefix + 'flutter_windows.dll').Delete()
                    Add-TextEntry $zip ($prefix + 'flutter_windows.dll') 'mixed version payload'
                }
            }
        } finally { $zip.Dispose() }
        $rejected = $false
        try { Assert-MystiaWindowsPackagePair -ModPackageZip $mod -StandaloneZip $bad -RepositoryRoot $repository }
        catch { $rejected = $true }
        if (-not $rejected) { throw "实际 ZIP 拒绝用例未生效：$kind" }
        $checks++
    }
    if (Test-Path -LiteralPath (Join-Path $sandbox 'outside')) { throw 'ZIP 验证意外提取了条目。' }
    Write-Output "PASS: $checks 项真实 ZIP 流验证：有效两包、路径/重复/链接/标记/内容及隐私数据污染拒绝。"
} finally {
    if ([IO.Path]::GetDirectoryName($sandbox) -cne $temporary -or [IO.Path]::GetFileName($sandbox) -cnotmatch '^portable-zip-audit-[0-9a-f]{32}$') {
        throw 'ZIP 审计清理边界无效。'
    }
    Remove-Item -LiteralPath $sandbox -Recurse -Force
}
