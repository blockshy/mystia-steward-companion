# 由发布测试显式调用；构造符合正式结构的文本载荷 ZIP，不跳过生产的两包验证函数。
$script:MystiaWindowsFixtureGenerator = Join-Path $PSScriptRoot 'windows-package-fixture.mjs'
function New-MystiaSyntheticWindowsPackagePair {
    param([Parameter(Mandatory = $true)][string]$Root, [string]$Version = '9.9.9', [string]$SourceCommit = ('9' * 40))
    $fixtureRoot = Join-Path ([IO.Path]::GetFullPath($Root)) ('.synthetic-windows-pair-' + [Guid]::NewGuid().ToString('N'))
    & node $script:MystiaWindowsFixtureGenerator $fixtureRoot $Version $SourceCommit
    if ($LASTEXITCODE -ne 0) { throw '合成 Windows 两包目录生成失败。' }
    try {
        [IO.Compression.ZipFile]::CreateFromDirectory((Join-Path $fixtureRoot 'mystia-steward-companion'),
            (Join-Path $Root 'mystia-steward-companion-bepinex.zip'), [IO.Compression.CompressionLevel]::Optimal, $true)
        [IO.Compression.ZipFile]::CreateFromDirectory((Join-Path $fixtureRoot 'mystia-steward-companion-windows-x64'),
            (Join-Path $Root 'mystia-steward-companion-companion-windows-x64.zip'), [IO.Compression.CompressionLevel]::Optimal, $true)
    } finally {
        # 仅移除本次新建的测试 staging；不使用调用方提供的任意目录作为递归删除目标。
        if ([IO.Path]::GetDirectoryName($fixtureRoot) -cne [IO.Path]::GetFullPath($Root) -or
            [IO.Path]::GetFileName($fixtureRoot) -cnotmatch '^\.synthetic-windows-pair-[0-9a-f]{32}$') { throw '测试 staging 边界无效。' }
        Remove-Item -LiteralPath $fixtureRoot -Recurse -Force
    }
}
