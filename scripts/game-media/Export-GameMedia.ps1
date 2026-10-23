#requires -Version 7.0
<#
.SYNOPSIS
    从已验证的临时资源副本导出真实 Sprite；不访问游戏进程、不改写游戏目录。
.DESCRIPTION
    物品只接受游戏实际 ItemSprites 图集中的 Foods/Beverages/Ingredients 序列化命名。
    这些前缀和整数解析已核对当前 Native 的 LoadingSceneManager.cctor 与 MatchPass。
    厨具只接受 DefaultCookers.png 容器列出的五种 enum 子 Sprite，避免把升级厨具 ID
    错当成类型。所有路径由 kind 与整数生成，未知/冲突身份拒绝导出。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string[]] $ReportPaths,
    [Parameter(Mandatory = $true)][string] $AssetStudioDirectory,
    [Parameter(Mandatory = $true)][string] $WorkDirectory,
    [string] $GuestBindingsPath,
    [string] $OutputDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-NoReparseAncestor([string] $Path) {
    # AssetStudio 会预处理 split 文件；所有输入及输出必须逐祖先拒绝 junction，
    # 不能只检查字符串前缀，否则 temp 内链接可能把写入引向真实游戏目录。
    $cursor = [IO.Path]::GetFullPath($Path)
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "不允许重解析路径：$cursor" }
        }
        $parent = [IO.Path]::GetDirectoryName($cursor)
        if ($parent -eq $cursor) { break }
        $cursor = $parent
    }
}

$root = [IO.Path]::GetFullPath($WorkDirectory) + [IO.Path]::DirectorySeparatorChar
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $WorkDirectory ('export-' + [Guid]::NewGuid().ToString('N')) }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $output.StartsWith($root, [StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $output)) {
    throw '导出必须写入探针下全新目录。'
}
$tool = [IO.Path]::GetFullPath($AssetStudioDirectory)
Assert-NoReparseAncestor $root
Assert-NoReparseAncestor $output
Assert-NoReparseAncestor $tool
if ($GuestBindingsPath) { Assert-NoReparseAncestor $GuestBindingsPath }
$pins = @{
    'AssetStudio.dll' = '4070BFDF99CCA18FD7EFB4D825732F16F711AE46DD92A22007835DCF868A1EBB'
    'AssetStudioUtility.dll' = '0CA737B0A0B863DD683019040C42C4F084A115CA715D1FB668305F40E34FDE72'
}
foreach ($name in $pins.Keys) {
    if ((Get-FileHash -LiteralPath (Join-Path $tool $name) -Algorithm SHA256).Hash -ne $pins[$name]) { throw '本机解码器字节发生变化。' }
}
$paths = [Collections.Generic.List[string]]::new()
$sources = [Collections.Generic.List[object]]::new()
foreach ($reportPath in $ReportPaths) {
    $full = [IO.Path]::GetFullPath($reportPath)
    if (-not $full.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { throw '只接受 temp 探针报告。' }
    Assert-NoReparseAncestor $full
    $report = Get-Content -LiteralPath $full -Raw | ConvertFrom-Json -Depth 100
    foreach ($entry in $report.normalization) {
        $path = [IO.Path]::GetFullPath($entry.target)
        if (-not $path.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { throw '副本路径超出探针目录。' }
        Assert-NoReparseAncestor $path
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.normalizedSha256) { throw '临时副本 SHA256 已改变。' }
        $paths.Add($path)
    }
    foreach ($source in $report.sourceFiles) { $sources.Add($source) }
}
# 原工具按宿主 EXE 所在目录查找 x64 解码器；PowerShell 宿主目录与工具目录不同。
# 只在本次新输出目录生成路径适配副本，保留所有解码函数的原字节；绝不修改安装目录。
[void][IO.Directory]::CreateDirectory($output)
$loaderDirectory = Join-Path $output 'temporary-loader'
[void][IO.Directory]::CreateDirectory($loaderDirectory)
[void][Reflection.Assembly]::LoadFrom((Join-Path $tool 'Mono.Cecil.dll'))
$loaderSource = Join-Path $tool 'AssetStudio.PInvoke.dll'
$loaderTarget = Join-Path $loaderDirectory 'AssetStudio.PInvoke.dll'
$loaderOriginalHash = (Get-FileHash -LiteralPath $loaderSource -Algorithm SHA256).Hash.ToLowerInvariant()
$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($loaderSource)
try {
    $loaderTypes = @($assembly.MainModule.Types | Where-Object FullName -CEQ 'AssetStudio.PInvoke.DllLoader')
    $methods = @($loaderTypes[0].Methods | Where-Object Name -CEQ 'GetDirectedDllDirectory')
    if ($loaderTypes.Count -ne 1 -or $methods.Count -ne 1 -or $methods[0].Parameters.Count -ne 0 -or $methods[0].ReturnType.FullName -cne 'System.String') {
        throw '工具路径适配方法与已核验签名不符。'
    }
    $body = $methods[0].Body
    $beforeIl = @($body.Instructions | ForEach-Object ToString)
    $body.Instructions.Clear()
    $body.Variables.Clear()
    $body.ExceptionHandlers.Clear()
    $body.Instructions.Add([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ldstr, (Join-Path $tool 'x64')))
    $body.Instructions.Add([Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ret))
    $afterIl = @($body.Instructions | ForEach-Object ToString)
    $assembly.Write($loaderTarget)
} finally { $assembly.Dispose() }
[void][Reflection.Assembly]::LoadFrom($loaderTarget)
$loaderEvidence = @{ method = 'GetDirectedDllDirectory'; sourceSha256 = $loaderOriginalHash; adaptedSha256 = (Get-FileHash -LiteralPath $loaderTarget -Algorithm SHA256).Hash.ToLowerInvariant(); beforeIL = $beforeIl; afterIL = $afterIl }
foreach ($name in @('K4os.Compression.LZ4.dll', 'Newtonsoft.Json.dll', 'SixLabors.ImageSharp.dll', 'AssetStudio.dll', 'Texture2DDecoderWrapper.dll', 'AssetStudioUtility.dll')) {
    [void][Reflection.Assembly]::LoadFrom((Join-Path $tool $name))
}
# 解码器以当前进程内存方式加载；不启动第三方 GUI，不把动态库复制进产品。
[void][Runtime.InteropServices.NativeLibrary]::Load((Join-Path $tool 'x64/Texture2DDecoderNative.dll'))
$manager = [AssetStudio.AssetsManager]::new()
$selected = [ordered]@{}
$evidence = [Collections.Generic.List[object]]::new()
$missingGuests = [Collections.Generic.List[object]]::new()

function Resolve-ExactPointer($Pointer) {
    # PPtr 同时提供 TryGet(out T) 与泛型 TryGet<T2>(out T2)。PowerShell 的动态
    # 重载绑定无法仅凭 [ref] 消除歧义，因此按实际 PPtr<T> 精确选择非泛型方法。
    $methods = @($Pointer.GetType().GetMethods() | Where-Object {
        $_.Name -eq 'TryGet' -and -not $_.IsGenericMethod -and $_.GetParameters().Count -eq 1
    })
    if ($methods.Count -ne 1) { throw 'PPtr 非泛型 TryGet 签名不唯一。' }
    $arguments = [object[]]@($null)
    if (-not $methods[0].Invoke($Pointer, $arguments)) { throw '精确 PPtr 指向的资源未载入。' }
    return $arguments[0]
}

function Add-ExactSprite([string] $Kind, [int] $Id, $Sprite, [string] $Proof) {
    if ($Id -lt 0) { return } # 游戏 -1 等占位素材不能伪装为实际实体图片。
    $key = "${Kind}:$Id"
    $identity = "$($Sprite.assetsFile.fileName):$($Sprite.m_PathID)"
    if ($selected.Contains($key)) {
        if ($selected[$key].identity -cne $identity) { throw "实体图片身份冲突：$key" }
        return
    }
    $selected[$key] = @{ sprite = $Sprite; identity = $identity; proof = $Proof; kind = $Kind; id = $Id }
}

try {
    $manager.LoadFiles($paths.ToArray())
    foreach ($file in $manager.assetsFileList) {
        foreach ($asset in $file.Objects) {
            if ($asset -isnot [AssetStudio.SpriteAtlas] -or $asset.m_Name -cnotmatch '^(Core|DLC[1-5]|DLCMusic)_ItemSprites$') { continue }
            foreach ($pointer in $asset.m_PackedSprites) {
                $sprite = Resolve-ExactPointer $pointer
                if ($sprite -isnot [AssetStudio.Sprite]) { throw "图集引用不是 Sprite：$($asset.m_Name)" }
                # Native 正则匹配并非锚定开头：DLC 实际名称带 DLC1_ 等前缀。
                # 此处显式接受已知 DLC 前缀并要求与所属 ItemSprites 图集一致，仍不按名称推断客人。
                if ($sprite.m_Name -cmatch '^(?:(?<Pack>DLC[1-5]|DLCMusic)_)?(?<Kind>Foods|Beverages|Ingredients)_(?<Id>-?[0-9]+)$') {
                    $pack = if ($Matches.ContainsKey('Pack')) { $Matches['Pack'] } else { 'Core' }
                    if ($asset.m_Name -cne "${pack}_ItemSprites") { throw 'Sprite 前缀与所属物品图集不一致。' }
                    $kind = @{ Foods = 'food'; Beverages = 'beverage'; Ingredients = 'ingredient' }[$Matches['Kind']]
                    Add-ExactSprite $kind ([int]$Matches['Id']) $sprite ("ItemSprites/" + $asset.m_Name)
                }
            }
        }
    }
    $cookerTypes = @{ Pot = 1; Grill = 2; Fryer = 3; Steamer = 4; CuttingBoard = 5 }
    foreach ($file in $manager.assetsFileList) {
        foreach ($bundle in $file.Objects) {
            if ($bundle -isnot [AssetStudio.AssetBundle]) { continue }
            foreach ($pair in $bundle.m_Container) {
                if ($pair.Key -cne 'Assets/_SortedAssets/Art/Core/ItemSprites/DefaultCookers.png') { continue }
                $value = Resolve-ExactPointer $pair.Value.asset
                if ($value -is [AssetStudio.Sprite] -and $cookerTypes.ContainsKey($value.m_Name)) {
                    Add-ExactSprite 'cooker' $cookerTypes[$value.m_Name] $value ('DefaultCookers/' + $value.m_Name)
                }
            }
        }
    }
    foreach ($id in 1..5) { if (-not $selected.Contains("cooker:$id")) { throw "标准厨具类型 $id 缺失。" } }
    if ($GuestBindingsPath) {
        $guestBindings = Get-Content -LiteralPath $GuestBindingsPath -Raw | ConvertFrom-Json -Depth 100
        if ($guestBindings.version -ne 1) { throw '不支持的客人绑定版本。' }
        # 一次建立“容器精确路径→图集”索引，避免每个客人重复枚举全部对象。
        $atlasContainers = @{}
        $wantedAtlasContainers = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($binding in $guestBindings.bindings) { [void]$wantedAtlasContainers.Add($binding.atlasContainer) }
        foreach ($file in $manager.assetsFileList) {
            foreach ($bundle in $file.Objects) {
                if ($bundle -isnot [AssetStudio.AssetBundle]) { continue }
                foreach ($pair in $bundle.m_Container) {
                    if (-not $wantedAtlasContainers.Contains($pair.Key)) { continue }
                    $value = Resolve-ExactPointer $pair.Value.asset
                    if ($value -isnot [AssetStudio.SpriteAtlas]) { continue }
                    if (-not $atlasContainers.ContainsKey($pair.Key)) { $atlasContainers[$pair.Key] = [Collections.Generic.List[object]]::new() }
                    $atlasContainers[$pair.Key].Add($value)
                }
            }
        }
        foreach ($entry in $guestBindings.missing) { $missingGuests.Add($entry) }
        foreach ($binding in $guestBindings.bindings) {
            if ($binding.kind -cne 'rare-guest' -or $binding.id -lt 0) { throw '未核验的客人绑定类型。' }
            # GUID 已经由目录唯一联结为容器路径；这里只查该容器的实际图集，再按
            # 序列化 AssetReference 的子对象名称取 Sprite，不全局搜索同名角色图片。
            $matches = [Collections.Generic.List[object]]::new()
            if ($atlasContainers.ContainsKey($binding.atlasContainer)) {
                foreach ($atlas in $atlasContainers[$binding.atlasContainer]) {
                    foreach ($pointer in $atlas.m_PackedSprites) {
                        $sprite = Resolve-ExactPointer $pointer
                        if ($sprite.m_Name -ceq $binding.spriteName) { $matches.Add($sprite) }
                    }
                }
            }
            $unique = @($matches | Group-Object { "$($_.assetsFile.fileName):$($_.m_PathID)" } | ForEach-Object { $_.Group[0] })
            if ($unique.Count -ne 1) {
                $missingGuests.Add(@{ kind = $binding.kind; id = $binding.id; reason = '精确图集子对象未加载或不唯一'; atlasContainer = $binding.atlasContainer; spriteName = $binding.spriteName })
                continue
            }
            Add-ExactSprite $binding.kind $binding.id $unique[0] ('GuestProfile/faceInNoteBook/' + $binding.proof.atlasGuid)
        }
    }
    if ($selected.Count -eq 0 -or $selected.Count -gt 4096) { throw '实体图片数量不合理。' }
    $images = Join-Path $output 'images'
    [void][IO.Directory]::CreateDirectory($images)
    foreach ($key in $selected.Keys) {
        $selection = $selected[$key]
        $sprite = $selection.sprite
        $image = [AssetStudio.SpriteHelper]::GetImage($sprite)
        if ($null -eq $image) { throw "Sprite 无法解码：$key" }
        try {
            if ($image.Width -le 0 -or $image.Height -le 0 -or $image.Width -gt 8192 -or $image.Height -gt 8192) { throw "图片尺寸异常：$key" }
            $relative = 'images/' + $selection.kind + '-' + $selection.id + '.png'
            $path = Join-Path $output $relative
            [SixLabors.ImageSharp.ImageExtensions]::SaveAsPng($image, $path)
            $evidence.Add([ordered]@{
                key = $key; path = $relative; spriteName = $sprite.m_Name; file = $sprite.assetsFile.fileName
                pathId = $sprite.m_PathID.ToString(); proof = $selection.proof
                width = $image.Width; height = $image.Height
                sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
            })
        } finally { $image.Dispose() }
    }
} catch {
    # 完整记录异常链，避免静态构造器外层错误掩盖实际原生依赖缺失。
    $messages = [Collections.Generic.List[string]]::new()
    $failure = $_.Exception
    while ($null -ne $failure) { $messages.Add($failure.GetType().FullName + ': ' + $failure.Message); $failure = $failure.InnerException }
    [IO.File]::WriteAllText((Join-Path $output 'failure.txt'), ($messages -join "`n"), [Text.UTF8Encoding]::new($false))
    throw
} finally { $manager.Clear() }
$manifest = [ordered]@{ version = 1; items = [ordered]@{} }
if ((Get-FileHash -LiteralPath $loaderSource -Algorithm SHA256).Hash.ToLowerInvariant() -ne $loaderOriginalHash) { throw '原工具 DLL 在导出期间变化。' }
foreach ($entry in ($evidence | Sort-Object key)) { $manifest.items[$entry.key] = $entry.path }
[IO.File]::WriteAllText((Join-Path $output 'manifest.json'), ($manifest | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $output 'source-evidence.json'), (@{ sources = $sources.ToArray(); images = $evidence.ToArray(); toolPins = $pins; loaderAdaptation = $loaderEvidence; missingGuests = $missingGuests.ToArray() } | ConvertTo-Json -Depth 30), [Text.UTF8Encoding]::new($false))
[ordered]@{ output = $output; images = $evidence.Count; manifest = (Join-Path $output 'manifest.json') } | ConvertTo-Json -Compress
