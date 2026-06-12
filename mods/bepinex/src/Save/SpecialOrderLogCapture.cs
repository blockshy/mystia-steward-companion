using BepInEx.Logging;

namespace MystiaStewardCompanion.Save;

public static class SpecialOrderLogCapture
{
    private static readonly object SyncRoot = new();
    private static readonly List<CapturedSpecialOrder> Orders = new();
    private static ILogListener? _listener;
    private static bool _attached;

    public static void Attach(ManualLogSource log)
    {
        if (_attached) return;

        _listener = new SpecialOrderLogListener();
        Logger.Listeners.Add(_listener);
        _attached = true;
        log.LogInfo("Special order log capture attached.");
    }

    public static IReadOnlyList<CapturedSpecialOrder> Snapshot(TimeSpan maxAge)
    {
        var now = DateTime.UtcNow;
        lock (SyncRoot)
        {
            Orders.RemoveAll(order => now - order.CapturedAt > maxAge);
            return Orders
                .OrderByDescending(order => order.CapturedAt)
                .ToList();
        }
    }

    private static void HandleLog(string? condition)
    {
        if (string.IsNullOrWhiteSpace(condition)) return;
        if (!condition.Contains("Generated Special Guest Order:", StringComparison.Ordinal)) return;

        var order = Parse(condition);
        if (order == null) return;

        lock (SyncRoot)
        {
            Orders.RemoveAll(existing => existing.DeskCode == order.DeskCode
                && string.Equals(existing.GuestName, order.GuestName, StringComparison.Ordinal));
            Orders.Add(order);

            if (Orders.Count > 24)
            {
                Orders.RemoveRange(0, Orders.Count - 24);
            }
        }
    }

    private sealed class SpecialOrderLogListener : ILogListener, IDisposable
    {
        public LogLevel LogLevelFilter => LogLevel.All;

        public void LogEvent(object sender, LogEventArgs eventArgs)
        {
            HandleLog(eventArgs.Data?.ToString());
        }

        public void Dispose()
        {
        }
    }

    private static CapturedSpecialOrder? Parse(string text)
    {
        var lines = text.Replace('\r', '\n').Split('\n');
        var guestName = ReadFieldValue(lines, "Guest") ?? ReadGuestNameFromHeader(text);
        var foodTag = ReadFieldValue(lines, "ReqFoodTag");
        var beverageTag = ReadFieldValue(lines, "ReqBevTag");
        var deskCodeText = ReadFieldValue(lines, "DeskCode");

        if (string.IsNullOrWhiteSpace(guestName)) return null;
        if (string.IsNullOrWhiteSpace(foodTag) && string.IsNullOrWhiteSpace(beverageTag)) return null;

        var deskCode = int.TryParse(deskCodeText, out var parsedDeskCode) ? parsedDeskCode : 0;
        return new CapturedSpecialOrder(
            deskCode,
            guestName.Trim(),
            NormalizeTag(foodTag),
            NormalizeTag(beverageTag),
            DateTime.UtcNow);
    }

    private static string? ReadGuestNameFromHeader(string text)
    {
        const string marker = "Generating order for Special Guest Group (";
        var start = text.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return null;

        start += marker.Length;
        var end = text.IndexOf(')', start);
        return end > start ? text[start..end].Trim() : null;
    }

    private static string? ReadFieldValue(IReadOnlyList<string> lines, string label)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var line = StripUnityPrefix(lines[i].Trim());
            if (!line.StartsWith(label, StringComparison.OrdinalIgnoreCase)) continue;

            var sameLineValue = NormalizeLabelValue(line[label.Length..]);
            if (!string.IsNullOrWhiteSpace(sameLineValue)) return sameLineValue;

            for (var j = i + 1; j < lines.Count; j++)
            {
                var candidate = StripUnityPrefix(lines[j].Trim());
                if (IsOrderFieldLine(candidate)) break;
                if (!string.IsNullOrWhiteSpace(candidate)) return candidate.Trim();
            }
        }

        return null;
    }

    private static string StripUnityPrefix(string value)
    {
        const string prefix = "GuesMana:";
        return value.StartsWith(prefix, StringComparison.Ordinal) ? value[prefix.Length..].Trim() : value;
    }

    private static string? NormalizeLabelValue(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith(":", StringComparison.Ordinal)) trimmed = trimmed[1..].Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static string NormalizeTag(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var trimmed = value.Trim();
        return string.Equals(trimmed, "Null", StringComparison.OrdinalIgnoreCase) ? "" : trimmed;
    }

    private static bool IsOrderFieldLine(string value)
    {
        return value.StartsWith("DeskCode:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("OrderType:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("ServFood:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("ServBev:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("Price:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("IsFreeOrder?", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("ReqFoodTag:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("ReqBevTag:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("Guest:", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record CapturedSpecialOrder(
    int DeskCode,
    string GuestName,
    string FoodTag,
    string BeverageTag,
    DateTime CapturedAt);
