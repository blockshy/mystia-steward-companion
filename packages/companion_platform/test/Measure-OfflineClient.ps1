param(
    [Parameter(Mandatory = $true)][string]$Executable,
    [ValidateRange(60, 3600)][int]$DurationSeconds = 3600,
    [ValidateRange(1, 30)][int]$IntervalSeconds = 5
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# 只测新建离线实例；每次使用独立凭据目录和两个动态端口，不提供游戏 PID 或 Token。
# 该采样反映 Release 离线静置成本，不声称是游戏负载、帧时延或旧客户端性能对照。
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$path = (Resolve-Path -LiteralPath $Executable).Path
$allowed = [IO.Path]::GetFullPath((Join-Path $repository 'apps/companion-flutter/build/windows/x64/runner/Release'))
if ([IO.Path]::GetDirectoryName($path) -ne $allowed -or [IO.Path]::GetFileName($path) -ne 'mystia-steward-companion.exe') {
    throw '性能采样只允许本仓最终 Release 可执行文件。'
}
$isolation = 'perf-' + [Guid]::NewGuid().ToString('N')
$output = Join-Path $repository "temp/$isolation"
New-Item -ItemType Directory -Path $output | Out-Null
function New-IsolatedPort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    try { $listener.Start(); return ([Net.IPEndPoint]$listener.LocalEndpoint).Port }
    finally { $listener.Stop() }
}
$controlPort = New-IsolatedPort
$apiPort = New-IsolatedPort
if ($controlPort -in @(32145, 32146) -or $apiPort -in @(32145, 32146) -or $controlPort -eq $apiPort) {
    throw '隔离端口分配冲突，请重新运行。'
}
$metadata = [ordered]@{
    scope = 'Flutter Release offline visible idle; no game, no production credentials'
    isolationId = $isolation; controlPort = $controlPort; apiPort = $apiPort
    executableSha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    requestedDurationSeconds = $DurationSeconds; sampleIntervalSeconds = $IntervalSeconds
    logicalProcessors = [Environment]::ProcessorCount
}
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class OfflineWindowMeasurement {
    private delegate bool EnumWindowProc(IntPtr window, IntPtr state);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowProc callback, IntPtr state);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetClassNameW(IntPtr window, StringBuilder name, int count);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll", SetLastError=true)]
    public static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    public static IntPtr FindMainWindow(int processId, bool requireVisible) {
        IntPtr result = IntPtr.Zero;
        EnumWindows((window, state) => {
            GetWindowThreadProcessId(window, out uint owner);
            if (owner != (uint)processId || (requireVisible && !IsWindowVisible(window))) return true;
            var name = new StringBuilder(256);
            GetClassNameW(window, name, name.Capacity);
            if (name.ToString() != "FLUTTER_RUNNER_WIN32_WINDOW") return true;
            result = window; return false;
        }, IntPtr.Zero);
        return result;
    }
}
'@
$process = $null
$timer = [Diagnostics.Stopwatch]::StartNew()
function Send-OwnedControl([string]$Action) {
    $connection = [Net.Sockets.TcpClient]::new()
    try {
        if (-not $connection.ConnectAsync('127.0.0.1', $controlPort).Wait(2000)) { throw '受控窗口命令连接超时。' }
        $stream = $connection.GetStream(); $stream.ReadTimeout = 2000; $stream.WriteTimeout = 2000
        $request = [Text.Encoding]::ASCII.GetBytes("mystia-companion-v2:${Action}`n")
        $stream.Write($request, 0, $request.Length)
        $connection.Client.Shutdown([Net.Sockets.SocketShutdown]::Send)
        return [IO.StreamReader]::new($stream).ReadLine()
    } finally { $connection.Dispose() }
}
try {
    # 地址与空Token显式成对，声明本次为匿名离线启动，不能恢复任何旧连接凭据。
    $process = Start-Process -FilePath $path -ArgumentList @("--control-port=$controlPort", "--isolation-id=$isolation", "--api=http://127.0.0.1:$apiPort", '--token=') -WorkingDirectory $output -WindowStyle Hidden -PassThru
    $creation = $process.StartTime.ToUniversalTime().ToFileTimeUtc()
    $mainWindow = [IntPtr]::Zero
    while ($timer.Elapsed.TotalSeconds -lt 30 -and -not $process.HasExited) {
        $process.Refresh()
        # 隐藏启动下先找同 PID 的精确窗口类。MainWindowHandle 可能命中输入法辅助窗，不能使用。
        $mainWindow = [OfflineWindowMeasurement]::FindMainWindow($process.Id, $false)
        if ($mainWindow -ne [IntPtr]::Zero) { break }
        Start-Sleep -Milliseconds 20
    }
    if ($process.HasExited -or $mainWindow -eq [IntPtr]::Zero) { throw '30秒内未观察到Flutter主窗口。' }
    $metadata.nativeWindowDiscoveredMs = $timer.Elapsed.TotalMilliseconds
    # Start-Process Hidden 会覆盖第一次 ShowWindow，因此显式请求本次实例显示，并独立量响应。
    # 该指标包含受控 show，不能称为自然冷启动、首帧完成或业务连接就绪。
    # 动态端口从探测到真正绑定之间存在竞争；动作前先用无副作用 identity 核验本次进程。
    if ((Send-OwnedControl 'identity') -cne "mystia-companion-v2:ok:$($process.Id):$creation") { throw '显示前 identity 身份不匹配。' }
    $showTimer = [Diagnostics.Stopwatch]::StartNew()
    if ((Send-OwnedControl 'show') -cne "mystia-companion-v2:ok:$($process.Id):$creation") { throw '显示 ACK 身份不匹配。' }
    while ($timer.Elapsed.TotalSeconds -lt 30 -and [OfflineWindowMeasurement]::FindMainWindow($process.Id, $true) -eq [IntPtr]::Zero) {
        Start-Sleep -Milliseconds 20
    }
    if ([OfflineWindowMeasurement]::FindMainWindow($process.Id, $true) -eq [IntPtr]::Zero) { throw '受控显示后Flutter主窗口仍不可见。' }
    $metadata.controlShowToWindowVisibleMs = $showTimer.Elapsed.TotalMilliseconds
    $metadata.processToControlledWindowVisibleMs = $timer.Elapsed.TotalMilliseconds
    # 只取消本次测试窗口的置顶，不操作其他窗口；长时采样不持续遮挡用户桌面。
    if (-not [OfflineWindowMeasurement]::SetWindowPos($mainWindow, [IntPtr](-2), 0, 0, 0, 0, 0x13)) {
        throw '无法取消受控采样窗口的置顶。'
    }
    $metadata.topmostDisabledForSampling = $true
    $metadata.windowSelection = 'same PID; visible; FLUTTER_RUNNER_WIN32_WINDOW'
    $rect = [OfflineWindowMeasurement+Rect]::new()
    if (-not [OfflineWindowMeasurement]::GetClientRect($mainWindow, [ref]$rect)) { throw '无法读取受控主窗口尺寸。' }
    $metadata.clientWidthPhysicalPixels = $rect.Right - $rect.Left
    $metadata.clientHeightPhysicalPixels = $rect.Bottom - $rect.Top
    $metadata.windowDpi = [OfflineWindowMeasurement]::GetDpiForWindow($mainWindow)
    # FILETIME 超过 JavaScript 安全整数范围，证据用十进制字符串保持精确身份。
    $metadata.pid = $process.Id; $metadata.creationTimeFileTime = $creation.ToString()
    $metadata | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'metadata.json') -Encoding utf8
    Write-Output "Offline performance sampling started: $output"
    $samples = [Collections.Generic.List[object]]::new()
    $previousCpu = $process.TotalProcessorTime.TotalMilliseconds
    $previousElapsed = $timer.Elapsed.TotalMilliseconds
    while ($timer.Elapsed.TotalSeconds -lt $DurationSeconds) {
        Start-Sleep -Seconds $IntervalSeconds
        $process.Refresh()
        if ($process.HasExited) { throw '连续采样期间客户端提前退出。' }
        $elapsed = $timer.Elapsed.TotalMilliseconds
        $cpu = $process.TotalProcessorTime.TotalMilliseconds
        $percentOneCore = 100 * ($cpu - $previousCpu) / ($elapsed - $previousElapsed)
        $sample = [ordered]@{
            # 保存区间起点，使暖机筛选不纳入跨越30秒边界的CPU区间。
            intervalStartSeconds = $previousElapsed / 1000
            elapsedSeconds = $elapsed / 1000; cpuPercentOneCore = $percentOneCore
            cpuPercentMachine = $percentOneCore / [Environment]::ProcessorCount
            workingSetBytes = $process.WorkingSet64; privateBytes = $process.PrivateMemorySize64
            handles = $process.HandleCount; threads = $process.Threads.Count
            # 这里只记录Win32可见状态，不能推导窗口未遮挡、完成绘制或业务内容已就绪。
            exactFlutterMainWindowVisible = [OfflineWindowMeasurement]::FindMainWindow($process.Id, $true) -ne [IntPtr]::Zero
        }
        $samples.Add($sample)
        $sample | ConvertTo-Json -Compress | Add-Content -LiteralPath (Join-Path $output 'samples.jsonl') -Encoding utf8
        $previousCpu = $cpu; $previousElapsed = $elapsed
    }
    # 退出 ACK 的身份必须与本次创建进程一致，然后独立等待 OS 进程退出。
    if ((Send-OwnedControl 'identity') -cne "mystia-companion-v2:ok:$($process.Id):$creation") { throw '退出前 identity 身份不匹配。' }
    $client = [Net.Sockets.TcpClient]::new()
    try {
        if (-not $client.ConnectAsync('127.0.0.1', $controlPort).Wait(2000)) { throw '采样退出连接超时。' }
        $stream = $client.GetStream(); $stream.ReadTimeout = 2000
        $bytes = [Text.Encoding]::ASCII.GetBytes("mystia-companion-v2:exit`n")
        $stream.Write($bytes, 0, $bytes.Length); $client.Client.Shutdown([Net.Sockets.SocketShutdown]::Send)
        $reader = [IO.StreamReader]::new($stream)
        if ($reader.ReadLine() -cne "mystia-companion-v2:ok:$($process.Id):$creation") { throw '采样退出身份不匹配。' }
    } finally { $client.Dispose() }
    if (-not $process.WaitForExit(5000) -or $process.ExitCode -ne 0) { throw '采样客户端未正常退出。' }
    $stable = @($samples | Where-Object { $_.intervalStartSeconds -ge 30 })
    if ($stable.Count -lt 2) { throw '稳定采样数量不足。' }
    $metadata.result = 'passed'
    $metadata.measuredSeconds = $samples[-1].elapsedSeconds
    $metadata.sampleCount = $samples.Count
    $metadata.idleMeanCpuPercentOneCore = ($stable.cpuPercentOneCore | Measure-Object -Average).Average
    $metadata.idleMeanWorkingSetBytes = ($stable.workingSetBytes | Measure-Object -Average).Average
    $metadata.idleMaximumPrivateBytes = ($stable.privateBytes | Measure-Object -Maximum).Maximum
    $metadata.privateBytesDeltaAfterWarmup = $stable[-1].privateBytes - $stable[0].privateBytes
    $metadata.workingSetDeltaAfterWarmup = $stable[-1].workingSetBytes - $stable[0].workingSetBytes
    $metadata.handleDeltaAfterWarmup = $stable[-1].handles - $stable[0].handles
    $metadata.processExitObserved = $true
    $metadata | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'summary.json') -Encoding utf8
    Write-Output "Offline performance sampling PASS: $output"
} catch {
    $metadata.result = 'failed'; $metadata.failure = $_.Exception.Message
    $metadata | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'summary.json') -Encoding utf8
    throw
} finally {
    # 保留此次 Process 对象，只回收它自身；不按名称搜索/终止游戏或任何已有客户端。
    if ($null -ne $process) { if (-not $process.HasExited) { $process.Kill() }; $process.Dispose() }
}
