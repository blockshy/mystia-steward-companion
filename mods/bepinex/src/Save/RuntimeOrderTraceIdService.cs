using System.Text;
using MystiaStewardCompanion.Core;

namespace MystiaStewardCompanion.Save;

/// <summary>
/// Generates short, human-readable order identifiers for log correlation.
/// </summary>
/// <remarks>
/// The game does not expose a stable public order id. These ids are scoped to the current
/// Mod runtime and are only intended to connect UI rows, automation events, and aggregate logs.
/// </remarks>
internal static class RuntimeOrderTraceIdService
{
    private const int MaxTrackedOrdersPerKind = 512;
    private const int MaxUiTargetTraceDigits = 16;
    private static readonly object SyncRoot = new();
    private static readonly Dictionary<string, TraceRecord> RecordsByStableKey = new(StringComparer.Ordinal);
    private static int _rareSequence;
    private static int _normalSequence;

    public static string GetRareTraceId(NightBusinessOrder order)
    {
        var stableKey = BuildRareStableKey(
            order.FirstSeenAtUtc,
            order.OrderLifecycleSequence,
            order.DeskCode,
            order.RuntimeGuestId,
            order.FoodTagId,
            order.BeverageTagId,
            order.IsFreeOrder);
        return GetOrCreate("rare", "R", stableKey);
    }

    internal static string GetRareTraceId(CapturedRuntimeSpecialOrder order)
    {
        var stableKey = BuildRareStableKey(
            order.FirstCapturedAt,
            order.OrderLifecycleSequence,
            order.DeskCode,
            order.GuestId,
            order.FoodTagId,
            order.BeverageTagId,
            order.IsFreeOrder);
        return GetOrCreate("rare", "R", stableKey);
    }

    /// <summary>
    /// Validates the exact process-local order trace carried by a typed UI target.
    /// Whitespace, Unicode digits and a prefix that conflicts with the declared kind are rejected.
    /// </summary>
    internal static bool TryNormalizeTargetTraceId(
        RuntimeUiTargetKind kind,
        string traceId,
        bool enabled,
        out string normalized,
        out string failure)
    {
        if (!enabled)
        {
            normalized = "";
            failure = "";
            return true;
        }

        var expectedPrefix = kind == RuntimeUiTargetKind.Rare ? 'R' : 'N';
        if (!IsValidUiTargetTraceId(traceId, expectedPrefix))
        {
            normalized = "";
            failure = $"{kind} UI target trace must match {expectedPrefix}- followed by 1-{MaxUiTargetTraceDigits} ASCII decimal digits.";
            return false;
        }

        normalized = traceId;
        failure = "";
        return true;
    }

    public static string GetNormalTraceId(NormalBusinessOrder order)
    {
        var stableKey = !string.IsNullOrWhiteSpace(order.OrderKey)
            ? $"normal:{order.OrderKey}|lifecycle:{(order.OrderLifecycleSequence > 0 ? order.OrderLifecycleSequence.ToString(System.Globalization.CultureInfo.InvariantCulture) : "missing")}"
            : BuildNormalStableKey(
                order.FirstSeenAtUtc,
                order.DeskCode,
                order.GuestName,
                order.FoodId,
                order.FoodName,
                order.BeverageId,
                order.BeverageName);
        return GetOrCreate("normal", "N", stableKey);
    }

    internal static string GetNormalTraceId(CapturedRuntimeNormalOrder order)
    {
        var lifecycle = order.OrderLifecycleSequence > 0
            ? order.OrderLifecycleSequence.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "missing";
        return GetOrCreate(
            "normal",
            "N",
            $"normal:{order.RuntimeKey}|lifecycle:{lifecycle}");
    }

    public static string GetRequestTraceId(OrderTraceKind kind, string requestedTraceId, string stableKey)
    {
        if (!string.IsNullOrWhiteSpace(requestedTraceId)) return requestedTraceId.Trim();
        return GetOrCreate(kind == OrderTraceKind.Normal ? "normal" : "rare", kind == OrderTraceKind.Normal ? "N" : "R", stableKey);
    }

    private static string GetOrCreate(string kind, string prefix, string stableKey)
    {
        var normalizedKey = string.IsNullOrWhiteSpace(stableKey)
            ? $"{kind}:unknown:{DateTime.UtcNow.Ticks}"
            : stableKey;

        lock (SyncRoot)
        {
            if (RecordsByStableKey.TryGetValue(normalizedKey, out var existing))
            {
                existing.LastSeenAtUtc = DateTime.UtcNow;
                return existing.TraceId;
            }

            var sequence = kind == "normal" ? ++_normalSequence : ++_rareSequence;
            var traceId = $"{prefix}-{sequence:0000}";
            RecordsByStableKey[normalizedKey] = new TraceRecord(kind, traceId);
            PruneLocked(kind);
            return traceId;
        }
    }

    private static bool IsValidUiTargetTraceId(string traceId, char expectedPrefix)
    {
        if (traceId == null
            || traceId.Length < 3
            || traceId.Length > 2 + MaxUiTargetTraceDigits
            || traceId[0] != expectedPrefix
            || traceId[1] != '-')
        {
            return false;
        }

        for (var index = 2; index < traceId.Length; index += 1)
        {
            var character = traceId[index];
            if (character < '0' || character > '9') return false;
        }

        return true;
    }

    private static string BuildRareStableKey(
        DateTime? firstSeenAtUtc,
        long orderLifecycleSequence,
        int deskCode,
        int? runtimeGuestId,
        int? foodTagId,
        int? beverageTagId,
        bool isFreeOrder)
    {
        var builder = new StringBuilder("rare:");
        AppendIso(builder, firstSeenAtUtc);
        Append(
            builder,
            orderLifecycleSequence > 0
                ? orderLifecycleSequence.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "missing-lifecycle");
        Append(builder, deskCode);
        Append(builder, runtimeGuestId);
        Append(builder, foodTagId);
        Append(builder, beverageTagId);
        Append(builder, isFreeOrder ? "free" : "paid");
        return builder.ToString();
    }

    private static string BuildNormalStableKey(
        DateTime? firstSeenAtUtc,
        int deskCode,
        string guestName,
        int foodId,
        string foodName,
        int beverageId,
        string beverageName)
    {
        var builder = new StringBuilder("normal:");
        AppendIso(builder, firstSeenAtUtc);
        Append(builder, deskCode);
        Append(builder, guestName);
        Append(builder, foodId);
        Append(builder, foodName);
        Append(builder, beverageId);
        Append(builder, beverageName);
        return builder.ToString();
    }

    private static void AppendIso(StringBuilder builder, DateTime? value)
    {
        Append(builder, value?.ToString("O") ?? "");
    }

    private static void Append(StringBuilder builder, object? value)
    {
        builder.Append('|');
        builder.Append(value?.ToString()?.Trim() ?? "");
    }

    private static void PruneLocked(string kind)
    {
        var records = RecordsByStableKey
            .Where(item => string.Equals(item.Value.Kind, kind, StringComparison.Ordinal))
            .OrderByDescending(item => item.Value.LastSeenAtUtc)
            .Skip(MaxTrackedOrdersPerKind)
            .Select(item => item.Key)
            .ToList();

        foreach (var key in records)
        {
            RecordsByStableKey.Remove(key);
        }
    }

    private sealed class TraceRecord
    {
        public TraceRecord(string kind, string traceId)
        {
            Kind = kind;
            TraceId = traceId;
            LastSeenAtUtc = DateTime.UtcNow;
        }

        public string Kind { get; }
        public string TraceId { get; }
        public DateTime LastSeenAtUtc { get; set; }
    }
}

internal enum OrderTraceKind
{
    Rare,
    Normal,
}
