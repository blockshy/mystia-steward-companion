using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MystiaStewardCompanion.Plugin;

// 子进程仅为退出等待测试服务；参数强制隔离端口，不复用生产地址。
if (args.Length == 2 && args[0] == "--exit-peer" && int.TryParse(args[1], out var childPort) && childPort != 32146)
{
    using var child = Process.GetCurrentProcess();
    var listener = new TcpListener(IPAddress.Loopback, childPort);
    listener.Start();
    var ack = Encoding.ASCII.GetBytes($"mystia-companion-v2:ok:{child.Id}:{child.StartTime.ToUniversalTime().ToFileTimeUtc()}\n");
    for (var step = 0; step < 2; step++)
    {
        using var client = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert(await Read(client) == CompanionControlClient.Prefix + (step == 0 ? "identity\n" : "exit\n"), "退出握手顺序");
        await client.GetStream().WriteAsync(ack);
    }
    listener.Stop();
    await Task.Delay(300); // 受控延时用来证明父进程确实等待退出，并非仅把 ACK 当成完成。
    return;
}

// 所有 peer 只绑定系统分配的隔离端口，绝不访问生产 32146，也不启动游戏或客户端。
var frame = CompanionControlClient.BuildFrame("show", "http://127.0.0.1:32145", "synthetic", 123);
Assert(frame.EndsWith('\n') && !frame.Contains('\r'), "Windows 必须编码 LF 帧");
foreach (var invalid in new[] { "mystia-companion-v2:ok:1:2\r\n", "mystia-companion-v2:ok:0:2\n", "mystia-companion-v2:ok:1:2\nextra", "mystia-companion-v2:ok:+1:2\n" })
{
    try { CompanionControlClient.ParseAcknowledgement(invalid); throw new Exception("接受非法 ACK"); }
    catch (FormatException) { }
}
try { CompanionControlClient.BuildFrame("show", "", "bad\n--api=wrong", 123); throw new Exception("接受注入"); }
catch (ArgumentException) { }

if (OperatingSystem.IsWindows())
{
    using var self = Process.GetCurrentProcess();
    var acknowledgement = $"mystia-companion-v2:ok:{self.Id}:{self.StartTime.ToUniversalTime().ToFileTimeUtc()}\n";
    // 假服务器检查授权回调严格发生在第二个连接发送 show 命令之前。
    var authorized = false;
    var server = new TcpListener(IPAddress.Loopback, 0);
    server.Start();
    var port = ((IPEndPoint)server.LocalEndpoint).Port;
    var peer = Task.Run(async () =>
    {
        using (var identity = await server.AcceptTcpClientAsync())
        {
            Assert(await Read(identity) == "mystia-companion-v2:identity\n", "首包必须不含凭据");
            await identity.GetStream().WriteAsync(Encoding.ASCII.GetBytes(acknowledgement));
        }
        using var command = await server.AcceptTcpClientAsync();
        Assert(authorized, "发送动作前完成前台授权");
        Assert(await Read(command) == frame, "动作帧逐字匹配");
        await command.GetStream().WriteAsync(Encoding.ASCII.GetBytes(acknowledgement));
    });
    Assert(CompanionControlClient.TrySend("show", "http://127.0.0.1:32145", "synthetic", 123,
        self.MainModule!.FileName!, port, pid => authorized = pid == self.Id), "身份和动作握手成功");
    await peer.WaitAsync(TimeSpan.FromSeconds(3));
    server.Stop();

    var wrongPeer = new TcpListener(IPAddress.Loopback, 0);
    wrongPeer.Start();
    var wrongPort = ((IPEndPoint)wrongPeer.LocalEndpoint).Port;
    var wrong = Task.Run(async () =>
    {
        using var client = await wrongPeer.AcceptTcpClientAsync();
        Assert(await Read(client) == "mystia-companion-v2:identity\n", "错误 peer 也只能见到身份请求");
        await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(acknowledgement));
    });
    Assert(!CompanionControlClient.TrySend("show", "", "synthetic", 123,
        Path.Combine(Path.GetTempPath(), "unrelated.exe"), wrongPort), "路径不同必须拒绝且不发送凭据");
    await wrong.WaitAsync(TimeSpan.FromSeconds(3));
    Assert(!wrongPeer.Pending(), "失败不建立动作连接");
    wrongPeer.Stop();

    var reservation = new TcpListener(IPAddress.Loopback, 0);
    reservation.Start();
    var exitPort = ((IPEndPoint)reservation.LocalEndpoint).Port;
    reservation.Stop();
    var start = new ProcessStartInfo(self.MainModule!.FileName!) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
    start.ArgumentList.Add("--exit-peer");
    start.ArgumentList.Add(exitPort.ToString());
    using var exiting = Process.Start(start)!;
    try
    {
        // 等待测试子进程开始监听；不连接探测，避免消耗其严格的两个握手连接。
        var ready = Stopwatch.StartNew();
        while (ready.Elapsed < TimeSpan.FromSeconds(3) && !System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties()
                   .GetActiveTcpListeners().Any(endpoint => endpoint.Port == exitPort)) await Task.Delay(20);
        var elapsed = Stopwatch.StartNew();
        Assert(CompanionControlClient.TrySend("exit", "", "", 123, self.MainModule!.FileName!, exitPort), "退出请求成功");
        Assert(exiting.HasExited && exiting.ExitCode == 0 && elapsed.ElapsedMilliseconds >= 250, "ACK 后等待同一进程实际退出");
    }
    finally { if (!exiting.HasExited) exiting.Kill(); }
}
Console.WriteLine("PASS Flutter platform control: LF, bounded ACK, injection, exact process identity, authorization ordering, credential isolation, observed process exit");

static void Assert(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
static async Task<string> Read(TcpClient client)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
    using var bytes = new MemoryStream();
    await client.GetStream().CopyToAsync(bytes, timeout.Token);
    return Encoding.UTF8.GetString(bytes.ToArray());
}
