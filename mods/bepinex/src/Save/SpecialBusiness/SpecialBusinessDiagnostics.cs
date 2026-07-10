using System.Runtime.CompilerServices;
using System.Text;

namespace MystiaStewardCompanion.Save;

internal static class SpecialBusinessDiagnostics
{
    private const int MaxSeenOnceKeys = 512;
    private const int MaxProgressKeys = 256;
    private static readonly object SyncRoot = new();
    private static readonly HashSet<string> SeenOnceKeys = new(StringComparer.Ordinal);
    private static readonly Queue<string> SeenOnceOrder = new();
    private static readonly Dictionary<string, int> LastProgressBuckets = new(StringComparer.Ordinal);
    private static readonly Queue<string> ProgressKeyOrder = new();

    public static void Reset()
    {
        lock (SyncRoot)
        {
            SeenOnceKeys.Clear();
            SeenOnceOrder.Clear();
            LastProgressBuckets.Clear();
            ProgressKeyOrder.Clear();
        }
    }

    public static void AppendWackySnapshot(
        string title,
        IEnumerable<string> lines,
        string? onceKey = null)
    {
        AppendSnapshot("special-business.wacky", title, lines, onceKey);
    }

    public static void AppendWackyProgressSnapshot(
        string key,
        double? progress,
        string title,
        IEnumerable<string> lines,
        int bucketCount = 20)
    {
        AppendProgressSnapshot("special-business.wacky", key, progress, title, lines, bucketCount);
    }

    public static void AppendYuyukoSnapshot(
        string title,
        IEnumerable<string> lines,
        string? onceKey = null)
    {
        AppendSnapshot("special-business.yuyuko", title, lines, onceKey);
    }

    public static void AppendYuyukoProgressSnapshot(
        string key,
        double? progress,
        string title,
        IEnumerable<string> lines,
        int bucketCount = 20)
    {
        AppendProgressSnapshot("special-business.yuyuko", key, progress, title, lines, bucketCount);
    }

    public static void AppendWackyOrderClassification(
        string challengeType,
        string role,
        string roleLabel,
        SpecialBusinessOrderProbe guest,
        object? order,
        object? controller,
        string source,
        string reason)
    {
        AppendWackySnapshot(
            "Wacky Cooking Order Classified",
            new[]
            {
                $"challengeType: {challengeType}",
                $"role: {role}",
                $"roleLabel: {roleLabel}",
                $"source: {source}",
                $"reason: {reason}",
                $"guestId: {guest.Id?.ToString() ?? ""}",
                $"guestText: {guest.Text}",
                $"order: {DescribeObject(order)}",
                $"controller: {DescribeObject(controller)}",
                $"deskCode: {ReadIntMember(order, "DeskCode", "deskCode")}",
                $"controllerDeskCode: {ReadIntMember(controller, "DeskCode", "deskCode", "DeskIndex", "deskIndex")}",
                $"controllerSpawnType: {SpecialBusinessOrderProbe.ReadControllerSpawnType(controller)}",
                $"rawSpawnType: {ReadTextMember(controller, "SpawnType", "spawnType")}",
                $"isHerself: {ReadTextMember(controller, "IsHerself", "isHerself")}",
                $"isControlled: {ReadTextMember(controller, "IsControlled", "isControlled")}",
            },
            $"wacky-classify|{role}|{source}|{ObjectKey(order)}|{ObjectKey(controller)}|{guest.Id?.ToString() ?? guest.Text}");
    }

    public static string DescribeObject(object? value)
    {
        if (value == null) return "null";
        return $"{value.GetType().FullName}@0x{RuntimeHelpers.GetHashCode(value):X}";
    }

    public static string FormatIdName(int id, string name)
    {
        return id >= 0
            ? string.IsNullOrWhiteSpace(name) ? $"#{id}" : $"{id}/{name}"
            : string.IsNullOrWhiteSpace(name) ? "" : name;
    }

    public static string FormatTags(IEnumerable<string>? tags)
    {
        var values = tags?
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? Array.Empty<string>();
        return values.Length == 0 ? "(none)" : string.Join("、", values);
    }

    public static string FormatIds(IEnumerable<int>? ids)
    {
        var values = ids?
            .Where(id => id >= 0)
            .Select(id => id.ToString())
            .ToArray() ?? Array.Empty<string>();
        return values.Length == 0 ? "(none)" : string.Join(",", values);
    }

    public static string FormatOrderContext(OrderLogContext? context)
    {
        if (context == null) return "trace=none kind=none";
        var builder = new StringBuilder();
        AppendPair(builder, "trace", context.TraceId);
        AppendPair(builder, "kind", context.Kind);
        if (context.DeskCode >= 0) AppendPair(builder, "desk", (context.DeskCode + 1).ToString());
        AppendPair(builder, "orderKey", context.OrderKey);
        if (context.GuestId.HasValue) AppendPair(builder, "guestId", context.GuestId.Value.ToString());
        AppendPair(builder, "guest", context.GuestName);
        if (context.MatchFoodId >= 0) AppendPair(builder, "matchFoodId", context.MatchFoodId.ToString());
        if (context.MatchBeverageId >= 0) AppendPair(builder, "matchBeverageId", context.MatchBeverageId.ToString());
        if (context.FoodId >= 0) AppendPair(builder, "foodId", context.FoodId.ToString());
        AppendPair(builder, "food", context.FoodName);
        if (context.BeverageId >= 0) AppendPair(builder, "beverageId", context.BeverageId.ToString());
        AppendPair(builder, "beverage", context.BeverageName);
        AppendPair(builder, "rule", context.RuleReason);
        return builder.Length == 0 ? "trace=none kind=none" : builder.ToString();
    }

    private static void AppendPair(StringBuilder builder, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        if (builder.Length > 0) builder.Append("; ");
        builder.Append(key);
        builder.Append('=');
        builder.Append(value.Trim());
    }

    private static void AppendSnapshot(
        string channel,
        string title,
        IEnumerable<string> lines,
        string? onceKey)
    {
        if (!AggregateModLogService.Enabled) return;
        try
        {
            if (!string.IsNullOrWhiteSpace(onceKey))
            {
                lock (SyncRoot)
                {
                    var scopedKey = $"{channel}|{onceKey}";
                    if (!SeenOnceKeys.Add(scopedKey)) return;
                    SeenOnceOrder.Enqueue(scopedKey);
                    while (SeenOnceOrder.Count > MaxSeenOnceKeys)
                    {
                        SeenOnceKeys.Remove(SeenOnceOrder.Dequeue());
                    }
                }
            }

            AggregateModLogService.AppendSection(
                channel,
                title,
                string.Join(Environment.NewLine, lines.Where(line => !string.IsNullOrWhiteSpace(line))));
        }
        catch
        {
            // Diagnostics must never affect gameplay.
        }
    }

    private static void AppendProgressSnapshot(
        string channel,
        string key,
        double? progress,
        string title,
        IEnumerable<string> lines,
        int bucketCount)
    {
        if (!AggregateModLogService.Enabled) return;
        var normalizedBucketCount = Math.Max(1, bucketCount);
        var bucket = progress.HasValue && !double.IsNaN(progress.Value)
            ? Math.Clamp((int)Math.Floor(progress.Value * normalizedBucketCount), 0, normalizedBucketCount)
            : -1;
        var scopedKey = $"{channel}|{key}";
        lock (SyncRoot)
        {
            if (LastProgressBuckets.TryGetValue(scopedKey, out var previous) && previous == bucket) return;
            if (!LastProgressBuckets.ContainsKey(scopedKey)) ProgressKeyOrder.Enqueue(scopedKey);
            LastProgressBuckets[scopedKey] = bucket;
            while (ProgressKeyOrder.Count > MaxProgressKeys)
            {
                LastProgressBuckets.Remove(ProgressKeyOrder.Dequeue());
            }
        }

        AppendSnapshot(channel, title, lines, onceKey: null);
    }

    private static string ObjectKey(object? value)
    {
        return value == null ? "null" : RuntimeHelpers.GetHashCode(value).ToString("X");
    }

    private static string ReadIntMember(object? value, params string[] members)
    {
        foreach (var member in members)
        {
            var raw = RuntimeReflectionUtility.GetMemberValue(value, member)
                ?? RuntimeReflectionUtility.InvokeMethod(value, $"get_{member}");
            var parsed = RuntimeReflectionUtility.ToInt(raw, int.MinValue);
            if (parsed != int.MinValue) return parsed.ToString();
        }

        return "";
    }

    private static string ReadTextMember(object? value, params string[] members)
    {
        foreach (var member in members)
        {
            var raw = RuntimeReflectionUtility.GetMemberValue(value, member)
                ?? RuntimeReflectionUtility.InvokeMethod(value, $"get_{member}");
            var text = raw?.ToString()?.Trim() ?? "";
            if (text.Length > 0) return text;
        }

        return "";
    }
}
