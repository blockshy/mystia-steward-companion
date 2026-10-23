param([Parameter(Mandatory = $true)][string]$Executable)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$path = (Resolve-Path -LiteralPath $Executable).Path
$expected = [IO.Path]::GetFullPath((Join-Path $repository 'apps/companion-flutter/build/windows/x64/runner/Release/mystia-steward-companion.exe'))
if ($path -ne $expected) { throw '生命周期验证只允许本仓最终 Release。' }
$isolation = 'lifetime-' + [Guid]::NewGuid().ToString('N')
$output = Join-Path $repository "temp/$isolation"
New-Item -ItemType Directory -Path $output | Out-Null
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$listener.Start(); $port = ([Net.IPEndPoint]$listener.LocalEndpoint).Port; $listener.Stop()
if ($port -in @(32145, 32146)) { throw '拒绝生产端口。' }
$fixture = $null; $client = $null
try {
    # “游戏”仅为本次创建的无业务 PowerShell 睡眠进程，始终持有 Process 对象，不枚举真实游戏。
    $fixture = Start-Process -FilePath (Join-Path $PSHOME 'pwsh.exe') -ArgumentList @('-NoProfile', '-Command', 'Start-Sleep -Seconds 40') -WindowStyle Hidden -PassThru
    # 显式空Token与隔离地址成对，不读取已存凭据，也不建立业务HTTP连接。
    $client = Start-Process -FilePath $path -ArgumentList @("--control-port=$port", "--isolation-id=$isolation", "--game-pid=$($fixture.Id)", "--api=http://127.0.0.1:$port", '--token=') -WorkingDirectory $output -WindowStyle Hidden -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while (-not $client.HasExited -and $client.MainWindowHandle -eq [IntPtr]::Zero -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 40; $client.Refresh()
    }
    if ($client.HasExited -or $client.MainWindowHandle -eq [IntPtr]::Zero) { throw '测试客户端没有完成原生桥初始化与首帧。' }
    if ($fixture.HasExited) { throw '测试进程在绑定前退出。' }
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $fixture.Kill(); $fixture.WaitForExit()
    if (-not $client.WaitForExit(5000) -or $client.ExitCode -ne 0) { throw '被监视测试进程退出后，客户端未正常退出。' }
    [ordered]@{
        result = 'passed'; scope = 'owned synthetic process lifetime only; no game'
        isolationId = $isolation; syntheticProcessId = $fixture.Id; clientProcessId = $client.Id
        clientExitAfterSyntheticExitMs = $timer.Elapsed.TotalMilliseconds
        executableSha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'summary.json') -Encoding utf8
    Write-Output "Synthetic process lifetime PASS: $output"
} finally {
    # 仅清理自己创建并一直持有的两个 Process 对象，不查找或终止用户进程。
    if ($null -ne $client) { if (-not $client.HasExited) { $client.Kill() }; $client.Dispose() }
    if ($null -ne $fixture) { if (-not $fixture.HasExited) { $fixture.Kill() }; $fixture.Dispose() }
}
