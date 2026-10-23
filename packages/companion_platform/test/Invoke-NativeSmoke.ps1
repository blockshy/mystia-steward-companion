param([Parameter(Mandatory = $true)][string]$Executable)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# 仅运行编译自 native_smoke_main.dart 的测试程序；不传游戏PID、真实Token或正式控制口。
$executablePath = (Resolve-Path -LiteralPath $Executable).Path
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
if (-not $executablePath.StartsWith((Join-Path $repository 'temp') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw '原生验证只允许仓库 temp 内的明确测试可执行文件。'
}
$isolation = 'native-' + [Guid]::NewGuid().ToString('N')
$evidenceRoot = Join-Path $repository "temp/$isolation"
New-Item -ItemType Directory -Path $evidenceRoot | Out-Null
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$listener.Start()
$testPort = ([Net.IPEndPoint]$listener.LocalEndpoint).Port
$listener.Stop()
if ($testPort -eq 32146) { throw '拒绝使用生产控制端口。' }
$baseArguments = @("--control-port=$testPort", "--isolation-id=$isolation")
# 显式空Token与隔离地址组成完整pair，覆盖首次启动解析；不会建立业务HTTP连接。
$arguments = $baseArguments + @("--api=http://127.0.0.1:$testPort", '--token=')

# 恢复已存窗口几何会在原生桥注册 Pigeon 前调用 SetWindowPos，可能重入消息处理。
# 必须覆盖这条冷启动路径；只测空目录会遗漏 initialize 的首次通道就绪竞态。
# 使用与原生 SHGetKnownFolderPath 一致的当前用户 LocalApplicationData，不依赖可被重定向的环境变量。
$localApplicationData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
if ([string]::IsNullOrWhiteSpace($localApplicationData) -or -not [IO.Path]::IsPathRooted($localApplicationData)) {
    throw '无法定位当前用户的独立测试存储根。'
}
function Assert-RealDirectory([string]$Path) {
    $item = Get-Item -LiteralPath $Path -Force
    if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "测试存储路径不是普通目录，拒绝通过链接访问生产或外部存储：$Path"
    }
}
# 先核对所有既存祖先，任何重解析点都拒绝；不能先创建文件再检查是否被重定向。
$cursor = [IO.Path]::GetFullPath($localApplicationData)
while ($cursor) {
    Assert-RealDirectory $cursor
    $parent = [IO.Directory]::GetParent($cursor)
    $cursor = if ($null -ne $parent) { $parent.FullName } else { '' }
}
$storageParent = $localApplicationData
foreach ($component in @('mystia-steward-companion', 'flutter-v1', 'isolation')) {
    $storageParent = Join-Path $storageParent $component
    if (-not (Test-Path -LiteralPath $storageParent)) {
        New-Item -ItemType Directory -Path $storageParent | Out-Null
    }
    Assert-RealDirectory $storageParent
}
$testStorage = [IO.Path]::GetFullPath((Join-Path $storageParent $isolation))
if (-not [IO.Directory]::GetParent($testStorage).FullName.Equals([IO.Path]::GetFullPath($storageParent), [StringComparison]::OrdinalIgnoreCase)) {
    throw '测试隔离目录超出明确的 isolation 父目录。'
}
if (Test-Path -LiteralPath $testStorage) { throw '新的测试隔离目录已存在，拒绝复用或覆盖。' }
New-Item -ItemType Directory -Path $testStorage | Out-Null
Assert-RealDirectory $testStorage
# 固定合成数据不来自真实客户端。保留它和其他隔离证据，测试结束不清理用户目录。
$geometryFile = Join-Path $testStorage 'window-state-v1.txt'
$geometryBytes = [Text.UTF8Encoding]::new($false).GetBytes('1 10 10 918 1104 0 96')
$geometryStream = [IO.FileStream]::new($geometryFile, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
try { $geometryStream.Write($geometryBytes, 0, $geometryBytes.Length); $geometryStream.Flush($true) }
finally { $geometryStream.Dispose() }

function Send-Control([string]$Frame) {
    $client = [Net.Sockets.TcpClient]::new()
    try {
        if (-not $client.ConnectAsync('127.0.0.1', $testPort).Wait(2000)) { throw '测试控制端口连接超时。' }
        $stream = $client.GetStream()
        $stream.ReadTimeout = 2000
        $stream.WriteTimeout = 2000
        $bytes = [Text.Encoding]::UTF8.GetBytes($Frame)
        $stream.Write($bytes, 0, $bytes.Length)
        $client.Client.Shutdown([Net.Sockets.SocketShutdown]::Send)
        $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::UTF8)
        return $reader.ReadLine()
    } finally { $client.Dispose() }
}

$process = $null
try {
    # 首次启动也必须在争用端口及创建Flutter窗口前拒绝不完整凭据。
    # 每次均保留Process句柄，绝不按名称结束用户或其它测试进程。
    $invalidLaunches = @(
        @{ arguments = @("--api=http://127.0.0.1:$testPort") },
        @{ arguments = @('--token=synthetic-rejected-token') },
        @{ arguments = @("--api=http://127.0.0.1:$testPort", '--token=', '--token=synthetic-rejected-token') }
    )
    foreach ($case in $invalidLaunches) {
        $invalid = Start-Process -FilePath $executablePath -ArgumentList ($baseArguments + $case.arguments) -WorkingDirectory $evidenceRoot -WindowStyle Hidden -PassThru
        try {
            # 产品runner把StartCompanionControl的明确错误统一映射为EXIT_FAILURE(1)。
            if (-not $invalid.WaitForExit(5000) -or $invalid.ExitCode -ne 1) { throw '不完整启动凭据没有在创建窗口前明确拒绝。' }
        } finally { if (-not $invalid.HasExited) { $invalid.Kill() }; $invalid.Dispose() }
    }
    $process = Start-Process -FilePath $executablePath -ArgumentList $arguments -WorkingDirectory $evidenceRoot -WindowStyle Hidden -PassThru
    $created = $process.StartTime.ToUniversalTime().ToFileTimeUtc()
    $nativeFile = Join-Path $evidenceRoot 'platform-native-evidence.json'
    $deadline = [DateTime]::UtcNow.AddSeconds(25)
    while (-not (Test-Path -LiteralPath $nativeFile) -and -not $process.HasExited -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 100
        $process.Refresh()
    }
    if (-not (Test-Path -LiteralPath $nativeFile)) { throw '测试程序没有生成原生证据。' }
    $native = Get-Content -LiteralPath $nativeFile -Raw | ConvertFrom-Json
    if ($native.result -ne 'passed') { throw "原生验证失败，见 $nativeFile" }
    $identity = Send-Control "mystia-companion-v2:identity`n"
    if ($identity -cne "mystia-companion-v2:ok:$($process.Id):$created") { throw '无副作用身份握手不匹配。' }
    $rejection = Send-Control "mystia-companion-v2:invalid`n"
    if ($rejection -cne 'mystia-companion-v2:rejected') { throw '无效请求没有明确拒绝。' }
    $second = Start-Process -FilePath $executablePath -ArgumentList $arguments -WorkingDirectory $evidenceRoot -WindowStyle Hidden -PassThru
    try {
        if (-not $second.WaitForExit(5000) -or $second.ExitCode -ne 0) { throw '次实例没有转发激活并正常退出。' }
    } finally { if (-not $second.HasExited) { $second.Kill() }; $second.Dispose() }
    $reply = Send-Control "mystia-companion-v2:exit`n"
    $expected = "mystia-companion-v2:ok:$($process.Id):$created"
    if ($reply -cne $expected) { throw '退出 ACK 的进程 PID/创建时间不匹配。' }
    if (-not $process.WaitForExit(5000)) { throw 'ACK 后客户端没有按期退出。' }
    if ($process.ExitCode -ne 0) { throw '客户端异常退出。' }
    $summary = [ordered]@{
        result = 'passed'; isolationId = $isolation; controlPort = $testPort
        native = $native; invalidFrameRejected = $true; secondaryExited = $true
        invalidCredentialLaunchesRejected = $invalidLaunches.Count
        identityHandshakeMatched = $true; exitIdentityMatched = $true; processExitObserved = $true
        restoredSyntheticGeometry = $true; isolatedStoragePath = $testStorage
        executableSha256 = (Get-FileHash -LiteralPath $executablePath -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    $summary | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $evidenceRoot 'summary.json') -Encoding utf8
    Write-Output "Native platform smoke PASS: $evidenceRoot"
} finally {
    # 仅终止本次明确创建且仍持有句柄的测试子进程，不按名称枚举或结束其他客户端。
    if ($null -ne $process) { if (-not $process.HasExited) { $process.Kill() }; $process.Dispose() }
}
