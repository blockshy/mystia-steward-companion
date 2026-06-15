using System.Text;
using BepInEx;

namespace MystiaStewardCompanion.Save;

internal static class RuntimeAutomationLogService
{
    private static readonly object SyncRoot = new();
    private static readonly TimeSpan RepeatSummaryInterval = TimeSpan.FromSeconds(30);
    private const long MaxBytes = 1024 * 1024;
    private const int RepeatSummaryCount = 25;

    private static string _lastKey = "";
    private static string _lastTarget = "";
    private static string _lastMessage = "";
    private static int _lastRepeatCount;
    private static int _lastReportedCount;
    private static DateTime _lastFirstAt = DateTime.MinValue;

    public static void Append(string action, string targetText, string message)
    {
        try
        {
            var path = ResolvePath();
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

            lock (SyncRoot)
            {
                var now = DateTime.Now;
                var key = string.Join("|", action, targetText, message);
                if (string.Equals(key, _lastKey, StringComparison.Ordinal))
                {
                    _lastRepeatCount++;
                    var unreportedCount = _lastRepeatCount - _lastReportedCount;
                    if (unreportedCount < RepeatSummaryCount && now - _lastFirstAt < RepeatSummaryInterval)
                    {
                        return;
                    }

                    RotateIfNeeded(path);
                    File.AppendAllText(
                        path,
                        FormatLine(
                            now,
                            "repeat",
                            targetText,
                            $"上一条重复 {unreportedCount} 次，累计 {_lastRepeatCount - 1} 次；{message}") + Environment.NewLine,
                        new UTF8Encoding(false));
                    _lastReportedCount = _lastRepeatCount;
                    _lastFirstAt = now;
                    return;
                }

                RotateIfNeeded(path);
                FlushRepeatSummary(path, now);
                File.AppendAllText(path, FormatLine(now, action, targetText, message) + Environment.NewLine, new UTF8Encoding(false));
                _lastKey = key;
                _lastTarget = targetText;
                _lastMessage = message;
                _lastRepeatCount = 1;
                _lastReportedCount = 1;
                _lastFirstAt = now;
            }
        }
        catch
        {
            // Diagnostics must never affect game automation.
        }
    }

    public static string ResolvePath()
    {
        return Path.Combine(Paths.ConfigPath, "MystiaStewardCompanion", "automation-jobs.log");
    }

    private static void FlushRepeatSummary(string path, DateTime now)
    {
        if (_lastRepeatCount <= _lastReportedCount) return;

        var unreportedCount = _lastRepeatCount - _lastReportedCount;
        File.AppendAllText(
            path,
            FormatLine(
                now,
                "repeat",
                _lastTarget,
                $"上一条重复 {unreportedCount} 次，累计 {_lastRepeatCount - 1} 次；{_lastMessage}") + Environment.NewLine,
            new UTF8Encoding(false));
        _lastReportedCount = _lastRepeatCount;
    }

    private static string FormatLine(DateTime now, string action, string targetText, string message)
    {
        return string.Join(" ",
            now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
            action,
            targetText,
            message);
    }

    private static void RotateIfNeeded(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length < MaxBytes) return;

            var backupPath = path + ".1";
            if (File.Exists(backupPath)) File.Delete(backupPath);
            File.Move(path, backupPath);
        }
        catch
        {
            // Ignore rotation failures; append may still succeed.
        }
    }
}
