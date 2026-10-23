using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace MystiaStewardCompanion.Plugin;

/// <summary>
/// 新客户端的有界回环控制协议。先进行不携带凭据的身份握手，核对进程创建时间和完整
/// 可执行文件路径，持有该进程句柄后才发送 Token；ACK 表示接管，退出另由进程句柄确认。
/// </summary>
internal static class CompanionControlClient
{
    internal const string Prefix = "mystia-companion-v2:";
    internal const int Port = 32146;

    internal static bool TrySend(string action, string endpoint, string token, int gamePid,
        string executablePath, int port = Port, Action<int>? authorizeForeground = null)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(executablePath)) return false;
        try
        {
            var identity = ParseAcknowledgement(Exchange(Prefix + "identity\n", port));
            using var process = Process.GetProcessById(identity.Pid);
            // 先固定 OS 句柄，后续创建时间、路径与退出等待均关联此一进程对象。
            _ = process.Handle;
            var actualExecutable = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(actualExecutable) ||
                process.StartTime.ToUniversalTime().ToFileTimeUtc() != identity.CreationTime ||
                !string.Equals(Path.GetFullPath(actualExecutable),
                    Path.GetFullPath(executablePath), StringComparison.OrdinalIgnoreCase)) return false;
            if (action is "show" or "toggle")
            {
                // 必须在命令发送前授权；在收到 show ACK 后授权已可能错过 GUI 消息处理。
                if (authorizeForeground != null) authorizeForeground(identity.Pid);
                else AllowSetForegroundWindow((uint)identity.Pid);
            }
            var acknowledged = ParseAcknowledgement(Exchange(BuildFrame(action, endpoint, token, gamePid), port));
            if (acknowledged != identity) return false;
            return action != "exit" || process.WaitForExit(5000);
        }
        catch
        {
            // 不输出网络内容或异常文本，避免请求中的 API Token 进入 Mod 日志。
            return false;
        }
    }

    internal static (int Pid, long CreationTime) ParseAcknowledgement(string text)
    {
        if (!text.EndsWith('\n') || text.Contains('\r')) throw new FormatException("控制 ACK 格式无效。");
        var parts = text[..^1].Split(':');
        if (parts.Length != 4 || parts[0] != "mystia-companion-v2" || parts[1] != "ok" ||
            !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 0 ||
            !long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var created) || created <= 0)
            throw new FormatException("控制 ACK 身份无效。");
        return (pid, created);
    }

    internal static string BuildFrame(string action, string endpoint, string token, int gamePid)
    {
        if (action is not ("show" or "toggle" or "exit")) throw new ArgumentException("控制操作无效。");
        if (action == "exit") return Prefix + "exit\n";
        if (gamePid <= 0 || endpoint.Any(IsUnsafe) || token.Any(IsUnsafe)) throw new ArgumentException("控制参数无效。");
        // 协议明确使用 LF。AppendLine 在 Windows 上会引入被服务端拒绝的 CRLF。
        var frame = Prefix + action + "\n--game-pid=" + gamePid.ToString(CultureInfo.InvariantCulture) + "\n";
        if (endpoint.Length != 0) frame += "--api=" + endpoint + "\n";
        if (token.Length != 0) frame += "--token=" + token + "\n";
        if (Encoding.UTF8.GetByteCount(frame) > 4096) throw new ArgumentException("控制参数过长。");
        return frame;
    }

    private static bool IsUnsafe(char value) => value is '\n' or '\r' or '\0';

    private static string Exchange(string request, int port)
    {
        using var client = new TcpClient(AddressFamily.InterNetwork);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        client.ConnectAsync("127.0.0.1", port, deadline.Token).GetAwaiter().GetResult();
        using var stream = client.GetStream();
        stream.ReadTimeout = 700;
        stream.WriteTimeout = 500;
        var bytes = Encoding.UTF8.GetBytes(request);
        stream.Write(bytes, 0, bytes.Length);
        client.Client.Shutdown(SocketShutdown.Send);
        var response = new List<byte>();
        var timer = Stopwatch.StartNew();
        while (response.Count < 128 && timer.ElapsedMilliseconds < 700)
        {
            var value = stream.ReadByte();
            if (value < 0) throw new IOException("控制 ACK 不完整。");
            response.Add((byte)value);
            if (value == '\n') return Encoding.ASCII.GetString(response.ToArray());
        }
        throw new IOException("控制 ACK 超过限制。");
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
