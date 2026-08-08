using System.Runtime.CompilerServices;
using System.Text;
using Il2CppInterop.Runtime.InteropTypes;

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

    public static void AppendYuumaSnapshot(
        string title,
        IEnumerable<string> lines,
        string? onceKey = null)
    {
        AppendSnapshot("special-business.yuuma", title, lines, onceKey);
    }

    public static void AppendYuumaProgressSnapshot(
        string key,
        double? progress,
        string title,
        IEnumerable<string> lines,
        int bucketCount = 20)
    {
        AppendProgressSnapshot("special-business.yuuma", key, progress, title, lines, bucketCount);
    }

    public static void AppendMizuchiSnapshot(
        string title,
        IEnumerable<string> lines,
        string? onceKey = null)
    {
        AppendSnapshot("special-business.mizuchi", title, lines, onceKey);
    }

    public static void AppendMizuchiOrderClassification(
        string challengeType,
        SpecialBusinessOrderClassification classification,
        MizuchiOrderIdentity identity,
        object? order,
        object? controller,
        string source)
    {
        if (!AggregateModLogService.Enabled) return;
        try
        {
            var generation = RuntimeNightBusinessLifecycle.Generation;
            AppendMizuchiSnapshot(
                "Mizuchi Order Classified",
                new[]
                {
                    $"generation: {generation}",
                    $"challengeType: {challengeType}",
                    $"source: {source}",
                    $"role: {classification.Role}",
                    $"roleLabel: {classification.RoleLabel}",
                    $"automationAllowed: {classification.AutomationAllowed}",
                    $"automationBlockReason: {classification.AutomationBlockReason}",
                    $"identityVerified: {identity.Verified}",
                    $"identityReason: {identity.Reason}",
                    $"orderGuestId: {identity.OrderGuestId?.ToString() ?? ""}",
                    $"controllerGuestId: {identity.ControllerGuestId?.ToString() ?? ""}",
                    $"groupGuestId: {identity.GroupGuestId?.ToString() ?? ""}",
                    $"selectedGuestId: {identity.SelectedGuestId?.ToString() ?? ""}",
                    $"controlledGuestId: {identity.ControlledGuestId?.ToString() ?? ""}",
                    $"controlType: {identity.ControlType?.ToString() ?? ""}",
                    $"targetIngredientId: {identity.TargetIngredientId?.ToString() ?? ""}",
                    $"isMizuchiChallenge: {identity.IsMizuchiChallenge?.ToString() ?? ""}",
                    $"catchProgress: {identity.CatchCount?.ToString() ?? ""}/{identity.RequiredCatchCount?.ToString() ?? ""}",
                    $"callbackMethod: {identity.CallbackMethod}",
                    $"orderPointer: 0x{(long)identity.OrderPointer:X}",
                    $"controllerPointer: 0x{(long)identity.ControllerPointer:X}",
                    $"callbackPointer: 0x{(long)identity.CallbackPointer:X}",
                    $"closurePointer: 0x{(long)identity.ClosurePointer:X}",
                    $"parentClosurePointer: 0x{(long)identity.ParentClosurePointer:X}",
                    $"order: {DescribeObject(order)}",
                    $"controller: {DescribeObject(controller)}",
                },
                $"classify|gen:{generation}|{challengeType}|{classification.Role}|{identity.OrderGuestId}|{identity.ControlledGuestId}|{identity.ControlType}|{identity.OrderPointer:X}|{identity.ControllerPointer:X}|{identity.CallbackPointer:X}");
        }
        catch
        {
            // Diagnostics must never affect order classification.
        }
    }

    public static void AppendMizuchiAutomationCheckpoint(
        string checkpoint,
        bool accepted,
        string requestRole,
        string candidateRole,
        IReadOnlyList<int> expectedExtraIngredientIds,
        IReadOnlyList<int>? actualExtraIngredientIds,
        string detail,
        nint orderPointer = 0,
        nint controllerPointer = 0,
        long orderLifecycleSequence = -1)
    {
        if (!AggregateModLogService.Enabled) return;
        try
        {
            var generation = RuntimeNightBusinessLifecycle.Generation;
            AppendMizuchiSnapshot(
                "Mizuchi Automation Checkpoint",
                new[]
                {
                    $"generation: {generation}",
                    $"checkpoint: {checkpoint}",
                    $"accepted: {accepted}",
                    $"requestRole: {requestRole}",
                    $"candidateRole: {candidateRole}",
                    $"expectedExtraIngredientIds: {FormatIds(expectedExtraIngredientIds)}",
                    $"actualExtraIngredientIds: {FormatIds(actualExtraIngredientIds ?? Array.Empty<int>())}",
                    $"orderPointer: 0x{(long)orderPointer:X}",
                    $"controllerPointer: 0x{(long)controllerPointer:X}",
                    $"orderLifecycleSequence: {orderLifecycleSequence}",
                    $"detail: {detail}",
                },
                $"automation|gen:{generation}|{checkpoint}|{accepted}|{requestRole}|{candidateRole}|"
                + $"{orderPointer:X}|{controllerPointer:X}|{orderLifecycleSequence}|{detail}");
        }
        catch
        {
            // Diagnostics must never affect automation safety checks.
        }
    }

    public static void AppendYuumaOrderClassification(
        string challengeType,
        SpecialBusinessOrderClassification classification,
        YuumaChallengeOrderIdentity identity,
        object? order,
        object? controller,
        string source)
    {
        if (!AggregateModLogService.Enabled) return;
        try
        {
            var generation = RuntimeNightBusinessLifecycle.Generation;
            AppendYuumaSnapshot(
                "Blood Pond Hell Order Classified",
                new[]
                {
                    $"generation: {generation}",
                    $"challengeType: {challengeType}",
                    $"source: {source}",
                    $"role: {classification.Role}",
                    $"roleLabel: {classification.RoleLabel}",
                    $"automationAllowed: {classification.AutomationAllowed}",
                    $"automationBlockReason: {classification.AutomationBlockReason}",
                    $"identityVerified: {identity.Verified}",
                    $"orderKind: {identity.OrderKind}",
                    $"orderGuestId: {identity.OrderGuestId?.ToString() ?? ""}",
                    $"controllerGuestId: {identity.ControllerGuestId?.ToString() ?? ""}",
                    $"identityReason: {identity.Reason}",
                    $"order: {DescribeObject(order)}",
                    $"controller: {DescribeObject(controller)}",
                },
                $"classify|gen:{generation}|{classification.Role}|{identity.OrderKind}|{identity.OrderGuestId}|{identity.ControllerGuestId}|{ObjectKey(order)}|{ObjectKey(controller)}");
        }
        catch
        {
            // Diagnostics must never affect order classification.
        }
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
        if (!AggregateModLogService.Enabled) return;
        try
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
        catch
        {
            // Diagnostics must never affect order classification.
        }
    }

    public static string DescribeObject(object? value)
    {
        if (value == null) return "null";
        return $"{value.GetType().FullName}@{ObjectKey(value)}";
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
        if (value == null) return "null";
        try
        {
            if (value is Il2CppObjectBase nativeObject)
            {
                var pointer = nativeObject.Pointer;
                if (pointer != IntPtr.Zero) return $"native:0x{pointer.ToInt64():X}";
            }
        }
        catch
        {
            // A stale IL2CPP wrapper must not turn diagnostics into a gameplay failure.
        }

        return $"managed:0x{RuntimeHelpers.GetHashCode(value):X}";
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
