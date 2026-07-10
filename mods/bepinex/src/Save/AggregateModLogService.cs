using System.Text;
using BepInEx;
using BepInEx.Logging;

namespace MystiaStewardCompanion.Save;

/// <summary>
/// Optional aggregate log sink used for troubleshooting sessions.
/// </summary>
/// <remarks>
/// The listener intentionally captures all BepInEx log sources while enabled, not only this plugin's
/// source, because many runtime issues surface as Unity or BepInEx messages. All failures are swallowed:
/// diagnostics must never affect gameplay, automation, or the local API.
/// </remarks>
internal static class AggregateModLogService
{
    public const long MaxFileBytes = 10L * 1024L * 1024L;
    public const int DefaultMaxFileCount = 30;
    public const int MinFileCount = 1;
    public const int MaxFileCountLimit = 9999;

    private static readonly object SyncRoot = new();
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly TimeSpan AutomationRepeatSummaryInterval = TimeSpan.FromSeconds(30);
    private const int AutomationRepeatSummaryCount = 25;

    private static AggregateLogListener? _listener;
    private static StreamWriter? _writer;
    private static string _path = ResolvePath("");
    private static int _maxFileCount = DefaultMaxFileCount;
    private static long _currentBytes;
    private static bool _enabled;
    private static string _lastAutomationKey = "";
    private static OrderLogContext? _lastAutomationContext;
    private static string _lastAutomationMessage = "";
    private static int _lastAutomationRepeatCount;
    private static int _lastAutomationReportedCount;
    private static DateTime _lastAutomationFirstAt = DateTime.MinValue;

    public static bool Enabled
    {
        get
        {
            lock (SyncRoot)
            {
                return _enabled;
            }
        }
    }

    public static string ResolvePath(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath)) return configuredPath.Trim();
        return Path.Combine(Paths.ConfigPath, "MystiaStewardCompanion", "aggregate-mod.log");
    }

    public static IReadOnlyList<string> EnumerateFiles(string? configuredPath)
    {
        var path = ResolvePath(configuredPath);
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return Array.Empty<string>();

        var activeName = Path.GetFileName(path);
        var prefix = Path.GetFileNameWithoutExtension(path) + ".";
        var extension = Path.GetExtension(path);
        return Directory.EnumerateFiles(directory, "*" + extension, SearchOption.TopDirectoryOnly)
            .Where(candidate =>
            {
                var name = Path.GetFileName(candidate);
                return string.Equals(name, activeName, StringComparison.OrdinalIgnoreCase)
                    || (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                        && name.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
            })
            .OrderBy(GetFileOrder)
            .ToArray();
    }

    public static int NormalizeMaxFileCount(int value)
    {
        return Math.Clamp(value, MinFileCount, MaxFileCountLimit);
    }

    public static long GetMaxTotalBytes(int maxFileCount)
    {
        return MaxFileBytes * NormalizeMaxFileCount(maxFileCount);
    }

    public static void Configure(bool enabled, string? configuredPath, int maxFileCount)
    {
        var path = ResolvePath(configuredPath);
        var normalizedMaxFileCount = NormalizeMaxFileCount(maxFileCount);
        lock (SyncRoot)
        {
            if (_enabled == enabled
                && string.Equals(_path, path, StringComparison.OrdinalIgnoreCase)
                && _maxFileCount == normalizedMaxFileCount)
            {
                return;
            }

            DisableLocked("aggregate log reconfigured");
            _path = path;
            _maxFileCount = normalizedMaxFileCount;
            if (!enabled) return;

            try
            {
                EnsureWriterLocked();
                WriteServiceLineLocked("aggregate log enabled");
                _listener = new AggregateLogListener();
                Logger.Listeners.Add(_listener);
                _enabled = true;
            }
            catch
            {
                DisableLocked("aggregate log enable failed");
            }
        }
    }

    public static void Shutdown()
    {
        lock (SyncRoot)
        {
            DisableLocked("aggregate log shutdown");
        }
    }

    public static void AppendSection(string channel, string title, string content)
    {
        try
        {
            lock (SyncRoot)
            {
                if (!_enabled) return;
                WriteLineLocked($"==== {FormatTimestamp()} [{NormalizeChannel(channel)}] {title} ====");
                foreach (var line in SplitLines(content))
                {
                    WriteLineLocked(line);
                }
                WriteLineLocked("");
            }
        }
        catch
        {
            // Logging diagnostics must never affect the game process.
        }
    }

    public static void AppendAutomation(string action, OrderLogContext? context, string message)
    {
        try
        {
            lock (SyncRoot)
            {
                if (!_enabled) return;

                var now = DateTime.Now;
                var contextText = FormatContext(context);
                var key = string.Join("|", action, contextText, message);
                if (string.Equals(key, _lastAutomationKey, StringComparison.Ordinal))
                {
                    _lastAutomationRepeatCount++;
                    var unreportedCount = _lastAutomationRepeatCount - _lastAutomationReportedCount;
                    if (unreportedCount < AutomationRepeatSummaryCount && now - _lastAutomationFirstAt < AutomationRepeatSummaryInterval)
                    {
                        return;
                    }

                    WriteAutomationLineLocked(
                        "repeat",
                        context,
                        $"上一条重复 {unreportedCount} 次，累计 {_lastAutomationRepeatCount - 1} 次；{message}");
                    _lastAutomationReportedCount = _lastAutomationRepeatCount;
                    _lastAutomationFirstAt = now;
                    return;
                }

                FlushAutomationRepeatSummaryLocked();
                WriteAutomationLineLocked(action, context, message);
                _lastAutomationKey = key;
                _lastAutomationContext = context;
                _lastAutomationMessage = message;
                _lastAutomationRepeatCount = 1;
                _lastAutomationReportedCount = 1;
                _lastAutomationFirstAt = now;
            }
        }
        catch
        {
            // Logging diagnostics must never affect game automation.
        }
    }

    private static void WriteEvent(LogEventArgs eventArgs)
    {
        try
        {
            lock (SyncRoot)
            {
                if (!_enabled) return;
                WriteLineLocked(FormatEvent(eventArgs));
            }
        }
        catch
        {
            // Logging diagnostics must never affect the game process.
        }
    }

    private static void DisableLocked(string reason)
    {
        try
        {
            if (_listener != null)
            {
                Logger.Listeners.Remove(_listener);
                _listener = null;
            }

            if (_writer != null)
            {
                FlushAutomationRepeatSummaryLocked();
                WriteServiceLineLocked(reason);
            }
        }
        catch
        {
            // Best effort only.
        }
        finally
        {
            _enabled = false;
            CloseWriterLocked();
            ResetAutomationRepeatStateLocked();
            RuntimeStaticDataDiagnosticFormatter.Reset();
            SpecialBusinessDiagnostics.Reset();
        }
    }

    private static void EnsureWriterLocked()
    {
        if (_writer != null) return;

        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        if (File.Exists(_path) && new FileInfo(_path).Length >= MaxFileBytes)
        {
            RotateFileLocked();
        }

        var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _currentBytes = stream.Length;
        _writer = new StreamWriter(stream, Utf8NoBom)
        {
            AutoFlush = true,
        };
        PruneFileCountLocked();
    }

    private static void WriteServiceLineLocked(string message)
    {
        WriteLineLocked($"==== {FormatTimestamp()} [service] {message}; path={_path}; maxFileBytes={MaxFileBytes}; maxFileCount={_maxFileCount}; maxTotalBytes={GetMaxTotalBytes(_maxFileCount)} ====");
    }

    private static void WriteLineLocked(string line)
    {
        EnsureWriterLocked();
        var text = line + Environment.NewLine;
        var bytes = Utf8NoBom.GetByteCount(text);
        if (_currentBytes > 0 && _currentBytes + bytes > MaxFileBytes)
        {
            RotateFileLocked();
            EnsureWriterLocked();
        }

        _writer!.Write(text);
        _currentBytes += bytes;
    }

    private static void RotateFileLocked()
    {
        CloseWriterLocked();
        if (!File.Exists(_path)) return;

        var archivePath = GetNextArchivePath(_path);
        File.Move(_path, archivePath);
        _currentBytes = 0;
    }

    private static void PruneFileCountLocked()
    {
        var files = EnumerateFiles(_path);
        var overflow = files.Count - _maxFileCount;
        if (overflow <= 0) return;

        foreach (var file in files)
        {
            if (overflow <= 0) break;
            if (string.Equals(file, _path, StringComparison.OrdinalIgnoreCase)) continue;

            try
            {
                File.Delete(file);
                overflow--;
            }
            catch
            {
                // Best effort only; log retention must not affect diagnostics writing.
            }
        }
    }

    private static void CloseWriterLocked()
    {
        try
        {
            _writer?.Flush();
            _writer?.Dispose();
        }
        catch
        {
            // Best effort only.
        }
        finally
        {
            _writer = null;
            _currentBytes = 0;
        }
    }

    private static string GetNextArchivePath(string path)
    {
        var directory = Path.GetDirectoryName(path) ?? "";
        var fileName = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        var nextIndex = Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, fileName + ".*" + extension, SearchOption.TopDirectoryOnly)
                .Select(GetArchiveIndex)
                .DefaultIfEmpty(0)
                .Max() + 1
            : 1;
        return Path.Combine(directory, $"{fileName}.{nextIndex}{extension}");
    }

    private static int GetFileOrder(string path)
    {
        var index = GetArchiveIndex(path);
        return index > 0 ? index : int.MaxValue;
    }

    private static int GetArchiveIndex(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var dotIndex = name.LastIndexOf('.');
        if (dotIndex < 0) return 0;
        return int.TryParse(name[(dotIndex + 1)..], out var index) ? index : 0;
    }

    private static string FormatEvent(LogEventArgs eventArgs)
    {
        var sourceName = eventArgs.Source?.SourceName ?? "unknown";
        var message = NormalizeMessage(eventArgs.Data?.ToString() ?? "");
        return $"{FormatTimestamp()} [bepinex] level={eventArgs.Level} source={EscapeToken(sourceName)} thread={Environment.CurrentManagedThreadId} {message}";
    }

    private static void FlushAutomationRepeatSummaryLocked()
    {
        if (_lastAutomationRepeatCount <= _lastAutomationReportedCount) return;

        var unreportedCount = _lastAutomationRepeatCount - _lastAutomationReportedCount;
        WriteAutomationLineLocked(
            "repeat",
            _lastAutomationContext,
            $"上一条重复 {unreportedCount} 次，累计 {_lastAutomationRepeatCount - 1} 次；{_lastAutomationMessage}");
        _lastAutomationReportedCount = _lastAutomationRepeatCount;
    }

    private static void ResetAutomationRepeatStateLocked()
    {
        _lastAutomationKey = "";
        _lastAutomationContext = null;
        _lastAutomationMessage = "";
        _lastAutomationRepeatCount = 0;
        _lastAutomationReportedCount = 0;
        _lastAutomationFirstAt = DateTime.MinValue;
    }

    private static void WriteAutomationLineLocked(string action, OrderLogContext? context, string message)
    {
        WriteLineLocked($"{FormatTimestamp()} [automation] action={EscapeToken(action)} {FormatContext(context)} message={EscapeToken(NormalizeMessage(message))}");
    }

    private static string FormatContext(OrderLogContext? context)
    {
        if (context == null) return "trace=none kind=none";

        var builder = new StringBuilder(160);
        AppendToken(builder, "trace", context.TraceId);
        AppendToken(builder, "kind", context.Kind);
        if (context.DeskCode >= 0) AppendToken(builder, "desk", (context.DeskCode + 1).ToString());
        AppendToken(builder, "orderKey", context.OrderKey);
        if (context.GuestId.HasValue) AppendToken(builder, "guestId", context.GuestId.Value.ToString());
        AppendToken(builder, "guest", context.GuestName);
        if (context.MatchFoodId >= 0) AppendToken(builder, "matchFoodId", context.MatchFoodId.ToString());
        if (context.MatchBeverageId >= 0) AppendToken(builder, "matchBeverageId", context.MatchBeverageId.ToString());
        if (context.FoodId >= 0) AppendToken(builder, "foodId", context.FoodId.ToString());
        AppendToken(builder, "food", context.FoodName);
        if (context.BeverageId >= 0) AppendToken(builder, "beverageId", context.BeverageId.ToString());
        AppendToken(builder, "beverage", context.BeverageName);
        AppendToken(builder, "rule", context.RuleReason);
        return builder.Length == 0 ? "trace=none kind=none" : builder.ToString();
    }

    private static void AppendToken(StringBuilder builder, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        if (builder.Length > 0) builder.Append(' ');
        builder.Append(key);
        builder.Append('=');
        builder.Append(EscapeToken(value.Trim()));
    }

    private static string FormatTimestamp()
    {
        return DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz");
    }

    private static string NormalizeChannel(string channel)
    {
        var value = string.IsNullOrWhiteSpace(channel) ? "mod" : channel.Trim().ToLowerInvariant();
        return value.Replace(" ", "-", StringComparison.Ordinal);
    }

    private static string NormalizeMessage(string message)
    {
        return message
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace("\n", "\n    ", StringComparison.Ordinal);
    }

    private static IEnumerable<string> SplitLines(string content)
    {
        return (content ?? "")
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
    }

    private static string EscapeToken(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace(" ", "\\ ", StringComparison.Ordinal);
    }

    private sealed class AggregateLogListener : ILogListener
    {
        public LogLevel LogLevelFilter => LogLevel.All;

        public void LogEvent(object sender, LogEventArgs eventArgs)
        {
            WriteEvent(eventArgs);
        }

        public void Dispose()
        {
        }
    }
}

internal sealed class OrderLogContext
{
    public string TraceId { get; init; } = "";
    public string Kind { get; init; } = "";
    public string OrderKey { get; init; } = "";
    public int DeskCode { get; init; } = -1;
    public int? GuestId { get; init; }
    public string GuestName { get; init; } = "";
    public int MatchFoodId { get; init; } = -1;
    public int MatchBeverageId { get; init; } = -1;
    public int FoodId { get; init; } = -1;
    public string FoodName { get; init; } = "";
    public int BeverageId { get; init; } = -1;
    public string BeverageName { get; init; } = "";
    public string RuleReason { get; init; } = "";
}
