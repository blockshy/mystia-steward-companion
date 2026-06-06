using System.Net;
using System.Net.Sockets;
using System.Text;
using BepInEx;
using BepInEx.Logging;

namespace MystiaSteward.LocalApi;

internal sealed class LocalApiServer : IDisposable
{
    private const int MaxRequestBytes = 8192;

    private readonly ManualLogSource _log;
    private readonly object _snapshotLock = new();
    private readonly string _healthJson;
    private readonly string _logOutputPath;
    private TcpListener? _listener;
    private Thread? _thread;
    private bool _running;
    private string _snapshotJson = "{\"runtimeLoaded\":false,\"status\":\"Snapshot is not ready.\"}";

    public LocalApiServer(string configuredHost, int port, string pluginVersion, ManualLogSource log)
    {
        BindAddress = ResolveLoopbackAddress(configuredHost, log);
        Port = Math.Clamp(port, 1024, 65535);
        _log = log;
        _logOutputPath = ResolveLogOutputPath();
        _healthJson = $"{{\"ok\":true,\"pluginVersion\":\"{EscapeJson(pluginVersion)}\",\"bindAddress\":\"{BindAddress}\",\"port\":{Port}}}";
    }

    public IPAddress BindAddress { get; }
    public int Port { get; }
    public string BaseUrl => $"http://{FormatHostForUrl(BindAddress)}:{Port}";

    public void Start()
    {
        if (_running) return;

        _listener = new TcpListener(BindAddress, Port);
        _listener.Start();
        _running = true;
        _thread = new Thread(ListenLoop)
        {
            IsBackground = true,
            Name = "Mystia Steward Local API",
        };
        _thread.Start();
        _log.LogInfo($"Local API listening at {BaseUrl}. Use 127.0.0.1 to avoid proxy and localhost resolution issues.");
    }

    public void SetSnapshotJson(string snapshotJson)
    {
        lock (_snapshotLock)
        {
            _snapshotJson = snapshotJson;
        }
    }

    public void Dispose()
    {
        _running = false;

        try
        {
            _listener?.Stop();
        }
        catch
        {
            // Stopping the listener during shutdown should not surface as a plugin error.
        }

        _listener = null;
        _thread = null;
    }

    private void ListenLoop()
    {
        while (_running)
        {
            try
            {
                var client = _listener?.AcceptTcpClient();
                if (client == null) continue;
                ThreadPool.QueueUserWorkItem(_ => HandleClient(client));
            }
            catch (SocketException) when (!_running)
            {
                return;
            }
            catch (ObjectDisposedException) when (!_running)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogWarning($"Local API accept failed: {ex.Message}");
            }
        }
    }

    private void HandleClient(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 2500;
                client.SendTimeout = 2500;
                using var stream = client.GetStream();
                var request = ReadRequest(stream);
                var firstLine = request.Split('\n').FirstOrDefault()?.TrimEnd('\r') ?? "";
                var parts = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                {
                    WriteResponse(stream, 400, "Bad Request", "{\"error\":\"bad request\"}");
                    return;
                }

                var method = parts[0];
                var path = parts[1].Split('?')[0];
                if (string.Equals(method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
                {
                    WriteResponse(stream, 204, "No Content", "");
                    return;
                }

                if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
                {
                    WriteResponse(stream, 405, "Method Not Allowed", "{\"error\":\"method not allowed\"}");
                    return;
                }

                switch (path)
                {
                    case "/health":
                    case "/api/health":
                        WriteResponse(stream, 200, "OK", _healthJson);
                        break;
                    case "/snapshot":
                    case "/api/snapshot":
                        WriteResponse(stream, 200, "OK", GetSnapshotJson());
                        break;
                    case "/logs":
                    case "/api/logs":
                        WriteResponse(stream, 200, "OK", BuildLogsJson());
                        break;
                    default:
                        WriteResponse(stream, 404, "Not Found", "{\"error\":\"not found\"}");
                        break;
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning($"Local API request failed: {ex.Message}");
            }
        }
    }

    private string GetSnapshotJson()
    {
        lock (_snapshotLock)
        {
            return _snapshotJson;
        }
    }

    private string BuildLogsJson()
    {
        try
        {
            var exists = File.Exists(_logOutputPath);
            var lines = exists ? ReadLogTail(_logOutputPath) : new List<string>();
            var builder = new StringBuilder();
            builder.Append('{');
            builder.Append("\"capturedAtUtc\":\"").Append(DateTime.UtcNow.ToString("O")).Append("\",");
            builder.Append("\"path\":\"").Append(EscapeJson(_logOutputPath)).Append("\",");
            builder.Append("\"exists\":").Append(exists ? "true" : "false").Append(',');
            builder.Append("\"lines\":[");
            for (var i = 0; i < lines.Count; i++)
            {
                if (i > 0) builder.Append(',');
                builder.Append('"').Append(EscapeJson(lines[i])).Append('"');
            }
            builder.Append("],\"error\":null}");
            return builder.ToString();
        }
        catch (Exception ex)
        {
            return "{\"capturedAtUtc\":\""
                + DateTime.UtcNow.ToString("O")
                + "\",\"path\":\""
                + EscapeJson(_logOutputPath)
                + "\",\"exists\":false,\"lines\":[],\"error\":\""
                + EscapeJson(ex.Message)
                + "\"}";
        }
    }

    private static List<string> ReadLogTail(string path)
    {
        const int maxBytes = 256 * 1024;
        const int maxLines = 300;

        var info = new FileInfo(path);
        var start = Math.Max(0, info.Length - maxBytes);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        stream.Seek(start, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        if (start > 0) reader.ReadLine();

        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
            if (lines.Count > maxLines) lines.RemoveAt(0);
        }

        return lines;
    }

    private static string ReadRequest(NetworkStream stream)
    {
        var buffer = new byte[MaxRequestBytes];
        var total = 0;
        while (total < buffer.Length)
        {
            var count = stream.Read(buffer, total, buffer.Length - total);
            if (count <= 0) break;
            total += count;
            if (total >= 4
                && buffer[total - 4] == '\r'
                && buffer[total - 3] == '\n'
                && buffer[total - 2] == '\r'
                && buffer[total - 1] == '\n')
            {
                break;
            }
        }

        return Encoding.ASCII.GetString(buffer, 0, total);
    }

    private static void WriteResponse(NetworkStream stream, int status, string reason, string body)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var headers = new StringBuilder();
        headers.Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n");
        headers.Append("Content-Type: application/json; charset=utf-8\r\n");
        headers.Append("Content-Length: ").Append(bodyBytes.Length).Append("\r\n");
        headers.Append("Access-Control-Allow-Origin: *\r\n");
        headers.Append("Access-Control-Allow-Methods: GET, OPTIONS\r\n");
        headers.Append("Access-Control-Allow-Headers: Content-Type, X-Mystia-Steward-Token\r\n");
        headers.Append("Cache-Control: no-store\r\n");
        headers.Append("Connection: close\r\n");
        headers.Append("\r\n");

        var headerBytes = Encoding.ASCII.GetBytes(headers.ToString());
        stream.Write(headerBytes, 0, headerBytes.Length);
        if (bodyBytes.Length > 0)
        {
            stream.Write(bodyBytes, 0, bodyBytes.Length);
        }
    }

    private static IPAddress ResolveLoopbackAddress(string configuredHost, ManualLogSource log)
    {
        if (IPAddress.TryParse(configuredHost, out var parsed) && IPAddress.IsLoopback(parsed))
        {
            return parsed.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Loopback : IPAddress.Loopback;
        }

        if (!string.IsNullOrWhiteSpace(configuredHost)
            && !string.Equals(configuredHost, "127.0.0.1", StringComparison.Ordinal)
            && !string.Equals(configuredHost, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            log.LogWarning($"Local API host '{configuredHost}' is not loopback. Falling back to 127.0.0.1.");
        }

        return IPAddress.Loopback;
    }

    private static string EscapeJson(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
    }

    private static string ResolveLogOutputPath()
    {
        try
        {
            return Path.Combine(Paths.BepInExRootPath, "LogOutput.log");
        }
        catch
        {
            return Path.Combine(AppContext.BaseDirectory, "BepInEx", "LogOutput.log");
        }
    }

    private static string FormatHostForUrl(IPAddress address)
    {
        return address.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{address}]"
            : address.ToString();
    }
}
