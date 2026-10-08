[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not [OperatingSystem]::IsWindows()) { throw 'Windows-only fixed-path metadata probe.' }
$base = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
$app = Join-Path $base 'com.tyukki.mystia-steward-companion'
# Only configured identifier and documented WebView profile subpaths. No enumeration or file reads.
$relative = @('', 'Default', 'Default/Local Storage', 'Default/Local Storage/leveldb',
    'EBWebView', 'EBWebView/Default', 'EBWebView/Default/Local Storage', 'EBWebView/Default/Local Storage/leveldb')
$observations = foreach ($item in $relative) {
    $path = if ($item.Length -eq 0) { $app } else { Join-Path $app $item }
    $found = Get-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
    [ordered]@{ relative = $item; exists = $null -ne $found; directory = $null -ne $found -and $found.PSIsContainer
        reparse = $null -ne $found -and (($found.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) }
}
[ordered]@{ schemaVersion = 1; kind = 'flutter-identity-storage-location'; platform = 'windows';
    identifier = 'com.tyukki.mystia-steward-companion'; root = $app; rootRule = 'LocalApplicationData/identifier';
    utc = [DateTime]::UtcNow.ToString('O'); fileContentsRead = $false; directoryEnumeration = $false; observations = @($observations) } | ConvertTo-Json -Depth 5 -Compress
