#requires -Version 7.0
<#
.SYNOPSIS
    使用本机已有 AssetStudio 0.16.47 对游戏资源做隔离、只读的离线结构检查。
.DESCRIPTION
    本探针不加载 Unity 游戏进程、不调用 IL2CPP，也不修改游戏目录。
    AssetStudio.LoadFiles 会预处理 split 资源，因此必须先复制明确列出的输入到全新
    临时目录，再让第三方解码器只访问副本；不能直接把游戏目录交给 LoadFolder。
    初次仅传 catalog.bundle。根据生成的目录证据再显式传入必要的 bundle 闭包，
    禁止用遍历全部 bundle 的方式代替精确 Addressables 引用解析。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $GameAddressablesDirectory,
    [Parameter(Mandatory = $true)][string] $AssetStudioDirectory,
    [Parameter(Mandatory = $true)][string] $PythonExecutable,
    [Parameter(Mandatory = $true)][string] $WorkDirectory,
    [string[]] $InputRelativeFiles = @('catalog.bundle'),
    [switch] $NormalizeGameStream,
    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-NoReparseAncestor([string] $Path) {
    # 输出与来源逐级拒绝重解析点，避免看似位于 temp 的路径实际指向游戏或其它目录。
    $cursor = [IO.Path]::GetFullPath($Path)
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "不允许重解析路径：$cursor"
            }
        }
        $parent = [IO.Path]::GetDirectoryName($cursor)
        if ($parent -eq $cursor) { break }
        $cursor = $parent
    }
}

if (-not $OutputDirectory) { $OutputDirectory = Join-Path $WorkDirectory ('run-' + [Guid]::NewGuid().ToString('N')) }
$output = [IO.Path]::GetFullPath($OutputDirectory)
$allowedOutput = [IO.Path]::GetFullPath($WorkDirectory) + [IO.Path]::DirectorySeparatorChar
if (-not $output.StartsWith($allowedOutput, [StringComparison]::OrdinalIgnoreCase)) {
    throw '输出必须是显式工作目录下的新子目录。'
}
if (Test-Path -LiteralPath $output) { throw '输出目录已存在；为保留历史证据，不覆盖或续写。' }
$gameRoot = [IO.Path]::GetFullPath($GameAddressablesDirectory).TrimEnd([char[]]@('\', '/')) + [IO.Path]::DirectorySeparatorChar
if ($allowedOutput.StartsWith($gameRoot, [StringComparison]::OrdinalIgnoreCase)) { throw '临时工作目录不得位于游戏资源目录内。' }
Assert-NoReparseAncestor $output
Assert-NoReparseAncestor $GameAddressablesDirectory
Assert-NoReparseAncestor $AssetStudioDirectory
if ($InputRelativeFiles.Count -eq 0 -or $InputRelativeFiles.Count -gt 96) {
    throw '单次必须明确列出 1–96 个输入文件。'
}

# 固定本机已有解码器的两个核心程序集字节，不把目录名当成版本证据。
$pins = [ordered]@{
    'AssetStudio.dll' = '4070BFDF99CCA18FD7EFB4D825732F16F711AE46DD92A22007835DCF868A1EBB'
    'AssetStudioUtility.dll' = '0CA737B0A0B863DD683019040C42C4F084A115CA715D1FB668305F40E34FDE72'
}
foreach ($name in $pins.Keys) {
    $hash = (Get-FileHash -LiteralPath (Join-Path $AssetStudioDirectory $name) -Algorithm SHA256).Hash
    if ($hash -ne $pins[$name]) { throw "解码器字节已变化：$name" }
}

$sources = [Collections.Generic.List[object]]::new()
$leafNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$totalBytes = [long] 0
foreach ($relative in $InputRelativeFiles) {
    # 输入允许范围仅为已经确认的 catalog 与 Addressables bundle；不接受任意路径或 URL。
    if ($relative -notmatch '^(catalog\.bundle|StandaloneWindows64[/\\][a-zA-Z0-9_]{1,100}\.bundle)$') {
        throw "不是允许的相对资源路径：$relative"
    }
    $source = [IO.Path]::GetFullPath((Join-Path $GameAddressablesDirectory $relative))
    Assert-NoReparseAncestor $source
    $file = Get-Item -LiteralPath $source
    if ($file.PSIsContainer -or $file.Length -eq 0) { throw "输入不是非空普通文件：$relative" }
    if (-not $leafNames.Add($file.Name)) { throw "输入文件名重复：$relative" }
    $totalBytes += $file.Length
    if ($totalBytes -gt 268435456) { throw '本探针单次输入超过 256 MiB；应先缩小必要引用闭包。' }
    $sources.Add([ordered]@{
        relativePath = $relative
        sourcePath = $source
        length = $file.Length
        sha256 = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
    })
}

$staging = Join-Path $output 'input'
$normalizedDirectory = Join-Path $output 'normalized'
$decoded = Join-Path $output 'decoded'
[void][IO.Directory]::CreateDirectory($staging)
[void][IO.Directory]::CreateDirectory($decoded)
if ($NormalizeGameStream) { [void][IO.Directory]::CreateDirectory($normalizedDirectory) }
$stagedPaths = [Collections.Generic.List[string]]::new()
$normalization = [Collections.Generic.List[object]]::new()
foreach ($entry in $sources) {
    $target = Join-Path $staging ([IO.Path]::GetFileName($entry.sourcePath))
    [IO.File]::Copy($entry.sourcePath, $target, $false)
    if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.sha256) {
        throw '临时副本与来源 SHA256 不一致。'
    }
    if ($NormalizeGameStream) {
        # 只把已核对 SHA 的临时副本传给标准库 Python，保留原始副本与变换后副本双份证据。
        $normalizedTarget = Join-Path $normalizedDirectory ([IO.Path]::GetFileName($entry.sourcePath))
        $result = & $PythonExecutable -B (Join-Path $PSScriptRoot 'normalize_bundle_copy.py') $target $normalizedTarget $WorkDirectory
        if ($LASTEXITCODE -ne 0) { throw '临时 bundle 格式还原失败。' }
        $normalization.Add(($result | ConvertFrom-Json))
        $stagedPaths.Add($normalizedTarget)
    } else {
        $stagedPaths.Add($target)
    }
}

# 此步只加载托管资源解析器，既不启动 AssetStudioGUI，也不调用纹理/游戏运行时。
foreach ($name in @('K4os.Compression.LZ4.dll', 'Newtonsoft.Json.dll', 'AssetStudio.dll')) {
    [void][Reflection.Assembly]::LoadFrom((Join-Path $AssetStudioDirectory $name))
}
$manager = [AssetStudio.AssetsManager]::new()
$records = [Collections.Generic.List[object]]::new()
$containers = [Collections.Generic.List[object]]::new()
$parseErrors = [Collections.Generic.List[object]]::new()
$serializedFiles = [Collections.Generic.List[object]]::new()
try {
    $manager.LoadFiles($stagedPaths.ToArray())
    if ($manager.assetsFileList.Count -eq 0) { throw '未成功解析任何 SerializedFile；不能记作通过。' }
    $index = 0
    foreach ($file in $manager.assetsFileList) {
        # PPtr 的 fileID 只能相对于本 SerializedFile 外部表解释，不能跨文件猜 PathID。
        $externals = [Collections.Generic.List[object]]::new()
        $externalIndex = 0
        foreach ($external in $file.m_Externals) {
            $externalIndex++
            $externals.Add(@{ fileId = $externalIndex; file = $external.fileName; path = $external.pathName })
        }
        $serializedFiles.Add(@{ file = $file.fileName; externals = $externals.ToArray() })
        foreach ($asset in $file.Objects) {
            $index++
            $typeName = $asset.type.ToString()
            $nameField = $asset.GetType().GetField('m_Name')
            $assetName = if ($null -eq $nameField) { $null } else { $nameField.GetValue($asset) }
            $record = [ordered]@{
                index = $index; type = $typeName; name = $assetName
                file = $file.fileName; pathId = $asset.m_PathID.ToString()
                originalBundle = [IO.Path]::GetFileName($file.originalPath)
                unityVersion = $file.unityVersion; byteSize = $asset.byteSize
            }
            # 名称仅写入 JSON 证据，绝不拼入输出路径，避免资产名称形成路径穿越。
            if ($typeName -eq 'TextAsset') {
                if ($asset.m_Script.Length -gt 16777216) { throw '单个文本资源超过 16 MiB，停止探针。' }
                $textPath = Join-Path $decoded ('{0:D5}-TextAsset.bin' -f $index)
                [IO.File]::WriteAllBytes($textPath, $asset.m_Script)
                $record['extractedTextPath'] = $textPath
                try {
                    $text = [Text.Encoding]::UTF8.GetString($asset.m_Script)
                    $json = $text | ConvertFrom-Json -Depth 100
                    $jsonPath = Join-Path $decoded ('{0:D5}-TextAsset.json' -f $index)
                    [IO.File]::WriteAllText($jsonPath, ($json | ConvertTo-Json -Depth 100), [Text.UTF8Encoding]::new($false))
                    $record['extractedJsonPath'] = $jsonPath
                } catch { $record['isJson'] = $false }
            }
            if ($typeName -eq 'MonoBehaviour') {
                try {
                    $tree = $asset.ToType()
                    if ($null -eq $tree) { throw '缺少可解析的 TypeTree。' }
                    $treePath = Join-Path $decoded ('{0:D5}-MonoBehaviour.json' -f $index)
                    [IO.File]::WriteAllText($treePath, [Newtonsoft.Json.JsonConvert]::SerializeObject($tree, [Newtonsoft.Json.Formatting]::Indented), [Text.UTF8Encoding]::new($false))
                    $record['typeTreePath'] = $treePath
                } catch {
                    # 未解析的 Profile 必须显式留下缺口，不能用资产名称猜测实体 ID。
                    $parseErrors.Add([ordered]@{ index = $index; type = $typeName; name = $assetName; error = $_.Exception.Message })
                }
            }
            if ($typeName -in @('AssetBundle', 'ResourceManager')) {
                foreach ($pair in $asset.m_Container) {
                    $pointer = if ($typeName -eq 'AssetBundle') { $pair.Value.asset } else { $pair.Value }
                    $containers.Add([ordered]@{ key = $pair.Key; file = $file.fileName; fileId = $pointer.m_FileID; pathId = $pointer.m_PathID.ToString() })
                }
            }
            $records.Add($record)
        }
    }
    if ($records.Count -eq 0) { throw '解析结果没有对象；不能生成成功证据。' }
} finally {
    $manager.Clear()
}

# 再读来源摘要，证明探针执行期间输入没有被本脚本改写或被外部更新替换。
foreach ($entry in $sources) {
    if ((Get-FileHash -LiteralPath $entry.sourcePath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.sha256) {
        throw "来源在检查期间变化：$($entry.relativePath)"
    }
}
$report = [ordered]@{
    kind = 'offline-game-media-structure-probe'; status = 'inspected-not-image-mapping-validated'
    utc = [DateTime]::UtcNow.ToString('o'); gameWasStartedOrConnected = $false
    toolDirectory = $AssetStudioDirectory; toolPins = $pins
    sourceFiles = $sources.ToArray(); inputBytes = $totalBytes; normalization = $normalization.ToArray()
    objects = $records.ToArray(); containers = $containers.ToArray(); typeTreeErrors = $parseErrors.ToArray()
    serializedFiles = $serializedFiles.ToArray()
}
$reportPath = Join-Path $output 'report.json'
[IO.File]::WriteAllText($reportPath, ($report | ConvertTo-Json -Depth 100), [Text.UTF8Encoding]::new($false))
[ordered]@{ report = $reportPath; objects = $records.Count; containers = $containers.Count; typeTreeErrors = $parseErrors.Count } | ConvertTo-Json -Compress
