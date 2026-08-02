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
    private const int MaxRareTraceDigits = 16;
    private static readonly object SyncRoot = new();
    private static readonly Dictionary<string, TraceRecord> RecordsByStableKey = new(StringComparer.Ordinal);
    private static int _rareSequence;
    private static int _normalSequence;

    public static string GetRareTraceId(NightBusinessOrder order)
    {
        var stableKey = BuildRareStableKey(
            order.FirstSeenAtUtc,
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
            order.DeskCode,
            order.GuestId,
            order.HasFoodTagId ? order.FoodTagId : null,
            order.HasBeverageTagId ? order.BeverageTagId : null,
            order.IsFreeOrder);
        return GetOrCreate("rare", "R", stableKey);
    }

    /// <summary>
    /// Normalizes the exact rare-order trace carried by the UI-assistance protocol.
    /// </summary>
    /// <remarks>
    /// Trace ids are opaque Ordinal identities. In particular, leading/trailing whitespace is
    /// rejected instead of being trimmed into a different request identity.
    /// </remarks>
    internal static string NormalizeRareTraceId(string traceId, bool enabled)
    {
        if (!TryNormalizeRareTraceId(traceId, enabled, out var normalized, out var failure))
        {
            throw new ArgumentException(failure, nameof(traceId));
        }

        return normalized;
    }

    internal static bool TryNormalizeRareTraceId(
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
        if (!IsValidRareTraceId(traceId))
        {
            normalized = "";
            failure = $"Rare-order trace must match R- followed by 1-{MaxRareTraceDigits} ASCII decimal digits.";
            return false;
        }

        normalized = traceId;
        failure = "";
        return true;
    }

    internal static bool TryResolveCurrentRareCapture(
        string orderTraceId,
        int deskCode,
        TimeSpan maxAge,
        out CapturedRuntimeSpecialOrder? capture,
        out string failure)
    {
        capture = null;
        if (!IsValidRareTraceId(orderTraceId))
        {
            failure = "target rare-order trace has an invalid exact shape";
            return false;
        }
        if (deskCode < 0)
        {
            failure = "target desk code is invalid";
            return false;
        }

        var traceMatches = new List<CapturedRuntimeSpecialOrder>();
        foreach (var candidate in SpecialOrderRuntimeCapture.Snapshot(maxAge))
        {
            if (candidate.OrderObject == null
                || candidate.ControllerObject == null
                || string.IsNullOrEmpty(candidate.RuntimeKey)
                || !RuntimeReflectionUtility.TryReadNativeObjectPointer(candidate.OrderObject, out var orderPointer)
                || !string.Equals(candidate.RuntimeKey, $"ptr:{orderPointer:x}", StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(GetRareTraceId(candidate), orderTraceId, StringComparison.Ordinal))
            {
                traceMatches.Add(candidate);
            }
        }

        if (traceMatches.Count != 1)
        {
            failure = $"target rare-order trace matched {traceMatches.Count} active captures";
            return false;
        }

        var matched = traceMatches[0];
        if (matched.DeskCode != deskCode)
        {
            failure = $"target desk mismatch: capture={matched.DeskCode}, target={deskCode}";
            return false;
        }

        capture = matched;
        failure = "";
        return true;
    }

    public static string GetNormalTraceId(NormalBusinessOrder order)
    {
        var stableKey = !string.IsNullOrWhiteSpace(order.OrderKey)
            ? $"normal:{order.OrderKey}"
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

    private static bool IsValidRareTraceId(string traceId)
    {
        if (traceId == null
            || traceId.Length < 3
            || traceId.Length > 2 + MaxRareTraceDigits
            || traceId[0] != 'R'
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
        int deskCode,
        int? runtimeGuestId,
        int? foodTagId,
        int? beverageTagId,
        bool isFreeOrder)
    {
        var builder = new StringBuilder("rare:");
        AppendIso(builder, firstSeenAtUtc);
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
