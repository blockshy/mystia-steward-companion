using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;

namespace MystiaStewardCompanion.Save;

public static class SpecialOrderRuntimeCapture
{
    private const string GuestsManagerTypeName = "NightScene.GuestManagementUtility.GuestsManager";
    private const string SpecialOrderTypeName = "NightScene.GuestManagementUtility.GuestsManager+SpecialOrder";
    private const string GuestGroupControllerTypeName = "NightScene.GuestManagementUtility.GuestGroupController";
    private const string SpecialGuestsControllerTypeName = "NightScene.GuestManagementUtility.SpecialGuestsController";
    private const string OrderControllerTypeName = "Night.UI.HUD.Ordering.OrderController";
    private const string PartnerManagerTypeName = "NightScene.PartnerUtility.PartnerManager";
    private const int MaxOrders = 32;

    private static readonly object SyncRoot = new();
    private static readonly List<CapturedRuntimeSpecialOrder> Orders = new();
    private static readonly List<RuntimeParseFailureDiagnostic> RecentParseFailures = new();
    private static readonly HashSet<string> PatchedMethods = new(StringComparer.Ordinal);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);
    private static Harmony? _harmony;
    private static ManualLogSource? _log;
    private static DateTime _lastAttachAttemptUtc = DateTime.MinValue;
    private static string _status = "not attached";
    private static int _addCallbacks;
    private static int _removeCallbacks;
    private static int _generatedCallbacks;
    private static int _statusCallbacks;
    private static int _capturedOrders;
    private static int _parseFailures;
    private static long _changeVersion;
    private static string _lastCapture = "";
    private static string _lastParseFailure = "";
    private static string _lastOrderShape = "";

    public static long ChangeVersion
    {
        get
        {
            lock (SyncRoot)
            {
                return _changeVersion;
            }
        }
    }

    public static string Status
    {
        get
        {
            lock (SyncRoot)
            {
                return _status;
            }
        }
    }

    public static void Attach(ManualLogSource log)
    {
        _log = log;
        TryAttach(log, true);
    }

    public static IReadOnlyList<CapturedRuntimeSpecialOrder> Snapshot(TimeSpan maxAge)
    {
        TryAttach(_log, false);
        var now = DateTime.UtcNow;
        lock (SyncRoot)
        {
            Orders.RemoveAll(order => now - order.CapturedAt > maxAge);
            return Orders
                .OrderBy(order => order.FirstCapturedAt)
                .ThenBy(order => order.CapturedAt)
                .ToList();
        }
    }

    public static IReadOnlyList<string> RecentParseFailuresSnapshot(TimeSpan maxAge, int limit = 16)
    {
        var now = DateTime.UtcNow;
        lock (SyncRoot)
        {
            PruneRecentParseFailuresLocked(now, maxAge);
            return RecentParseFailures
                .OrderByDescending(failure => failure.CapturedAtUtc)
                .Take(limit)
                .Select(failure => $"{failure.CapturedAtUtc:O}; {failure.Message}")
                .ToList();
        }
    }

    private static void TryAttach(ManualLogSource? log, bool force)
    {
        lock (SyncRoot)
        {
            if (!force && DateTime.UtcNow - _lastAttachAttemptUtc < RetryInterval) return;
            _lastAttachAttemptUtc = DateTime.UtcNow;
        }

        var patchedNow = new List<string>();
        var missing = new List<string>();
        try
        {
            _harmony ??= new Harmony("com.tyukki.mystia-steward-companion.special-order-runtime-capture");

            PatchMethod(_harmony, GuestGroupControllerTypeName, "PushToOrder", 1, false, nameof(OnControllerOrderAdded), null, patchedNow, missing);
            PatchMethod(_harmony, SpecialGuestsControllerTypeName, "PostGenerateOrder", 2, false, null, nameof(OnGeneratedSpecialOrder), patchedNow, missing);
            PatchMethod(_harmony, GuestsManagerTypeName, "AddToOrder", 1, false, nameof(OnOrderAdded), null, patchedNow, missing);
            PatchMethod(_harmony, GuestsManagerTypeName, "RemoveFromOrder", 1, false, nameof(OnOrderRemoved), null, patchedNow, missing);
            PatchMethod(_harmony, OrderControllerTypeName, "AddOrder", 1, true, nameof(OnOrderAdded), null, patchedNow, missing);
            PatchMethod(_harmony, PartnerManagerTypeName, "OnOrderBaseAdd", 1, false, nameof(OnOrderAdded), null, patchedNow, missing);
            PatchMethod(_harmony, PartnerManagerTypeName, "OnOrderBaseStatusUpdate", 3, false, null, nameof(OnOrderStatusUpdated), patchedNow, missing);
            PatchMethod(_harmony, PartnerManagerTypeName, "NotifySystemChanged", 4, false, null, nameof(OnOrderSystemChanged), patchedNow, missing);

            lock (SyncRoot)
            {
                _status = PatchedMethods.Count == 0
                    ? $"waiting: {string.Join(", ", missing.Take(4))}"
                    : BuildStatusLocked();
            }

            if (patchedNow.Count > 0)
            {
                log?.LogInfo($"Special order runtime capture patched: {string.Join(", ", patchedNow)}.");
            }
            else if (force && PatchedMethods.Count == 0)
            {
                log?.LogWarning($"Special order runtime capture waiting for game types: {string.Join(", ", missing.Take(4))}.");
            }
        }
        catch (Exception ex)
        {
            lock (SyncRoot)
            {
                _status = $"error: {ex.Message}";
            }

            log?.LogWarning($"Special order runtime capture failed: {ex.Message}");
        }
    }

    private static void PatchMethod(
        Harmony harmony,
        string typeName,
        string methodName,
        int parameterCount,
        bool isStatic,
        string? prefixName,
        string? postfixName,
        ICollection<string> patchedNow,
        ICollection<string> missing)
    {
        var key = $"{typeName}.{methodName}/{parameterCount}/{(isStatic ? "static" : "instance")}";
        lock (SyncRoot)
        {
            if (PatchedMethods.Contains(key)) return;
        }

        var type = FindType(typeName);
        if (type == null)
        {
            missing.Add(typeName);
            return;
        }

        var flags = BindingFlags.Public | BindingFlags.NonPublic | (isStatic ? BindingFlags.Static : BindingFlags.Instance);
        var target = type
            .GetMethods(flags)
            .FirstOrDefault(method => method.Name == methodName && method.GetParameters().Length == parameterCount);
        var prefix = prefixName == null ? null : typeof(SpecialOrderRuntimeCapture).GetMethod(prefixName, BindingFlags.NonPublic | BindingFlags.Static);
        var postfix = postfixName == null ? null : typeof(SpecialOrderRuntimeCapture).GetMethod(postfixName, BindingFlags.NonPublic | BindingFlags.Static);
        if (target == null || (prefixName != null && prefix == null) || (postfixName != null && postfix == null))
        {
            missing.Add(key);
            return;
        }

        harmony.Patch(
            target,
            prefix: prefix == null ? null : new HarmonyMethod(prefix),
            postfix: postfix == null ? null : new HarmonyMethod(postfix));
        lock (SyncRoot)
        {
            PatchedMethods.Add(key);
        }

        patchedNow.Add(key);
    }

    private static void OnOrderAdded(object __0)
    {
        lock (SyncRoot) _addCallbacks++;
        AddOrder(ParseOrder(__0, "OrderAdd"));
    }

    private static void OnControllerOrderAdded(object __instance, object __0)
    {
        lock (SyncRoot) _addCallbacks++;
        AddOrder(ParseOrder(__0, "ControllerOrderAdd", __instance));
    }

    private static void OnOrderRemoved(object __0)
    {
        lock (SyncRoot) _removeCallbacks++;
        RemoveOrder(ParseOrder(__0, "OrderRemove"));
    }

    private static void OnOrderStatusUpdated(object __0, object __1)
    {
        lock (SyncRoot) _statusCallbacks++;
        UpdateOrderStatus(ParseOrder(__0, "OrderStatusUpdate"), __1);
    }

    private static void OnOrderSystemChanged(object __0, object __1, object __2)
    {
        lock (SyncRoot) _statusCallbacks++;
        UpdateOrderStatus(ParseOrder(__2, "OrderSystemChanged"), __1);
    }

    private static void OnGeneratedSpecialOrder(object __instance, object __result)
    {
        lock (SyncRoot) _generatedCallbacks++;
        AddOrder(ParseOrder(__result, "PostGenerateOrder", __instance));
    }

    private static void AddOrder(CapturedRuntimeSpecialOrder? order)
    {
        if (order == null) return;

        lock (SyncRoot)
        {
            var existing = Orders.Where(current => IsSameOrderSlot(current, order)).ToList();
            Orders.RemoveAll(current => IsSameOrderSlot(current, order));

            var next = existing.Aggregate(order, MergeCapturedOrder);
            Orders.Add(next);
            _capturedOrders++;
            _lastCapture = $"{next.CaptureSource}: desk={next.DeskCode}, guestId={next.GuestId?.ToString() ?? ""}, food={next.FoodTag}({next.FoodTagId}), bev={next.BeverageTag}({next.BeverageTagId})";
            _changeVersion++;
            _status = BuildStatusLocked();
            if (Orders.Count > MaxOrders)
            {
                Orders.RemoveRange(0, Orders.Count - MaxOrders);
            }
        }
    }

    private static void RemoveOrder(CapturedRuntimeSpecialOrder? order)
    {
        if (order == null) return;

        lock (SyncRoot)
        {
            var removed = Orders.RemoveAll(existing => IsSameOrderSlot(existing, order));
            _lastCapture = $"removed: desk={order.DeskCode}, guestId={order.GuestId?.ToString() ?? ""}";
            if (removed > 0) _changeVersion++;
            _status = BuildStatusLocked();
        }
    }

    private static void UpdateOrderStatus(CapturedRuntimeSpecialOrder? order, object? context)
    {
        if (order == null) return;

        var contextName = FormatValue(context);
        if (string.Equals(contextName, "OrderRemove", StringComparison.OrdinalIgnoreCase)
            || string.Equals(contextName, "2", StringComparison.Ordinal))
        {
            RemoveOrder(order with { CaptureSource = "OrderRemove" });
            return;
        }

        if (!IsOrderDeliveryContext(contextName)) return;
        if (!order.IsFulfilled) return;

        RemoveOrder(order with { CaptureSource = "OrderFulfilled" });
    }

    private static bool IsOrderDeliveryContext(string contextName)
    {
        return string.Equals(contextName, "FoodDelivered", StringComparison.OrdinalIgnoreCase)
            || string.Equals(contextName, "BeverageDelivered", StringComparison.OrdinalIgnoreCase)
            || string.Equals(contextName, "3", StringComparison.Ordinal)
            || string.Equals(contextName, "4", StringComparison.Ordinal);
    }

    private static CapturedRuntimeSpecialOrder? ParseOrder(object? order, string source, object? controller = null)
    {
        if (order == null)
        {
            NoteParseFailure(source, "order is null");
            return null;
        }

        var readableOrder = TryCastOrder(order, SpecialOrderTypeName) ?? order;
        var textParts = ParseOrderText(SafeToString(readableOrder));
        var orderTypeValue = GetMemberValue(readableOrder, "Type");
        var orderType = FormatValue(orderTypeValue);
        if (!IsSpecialOrderType(orderTypeValue, orderType)
            && !string.Equals(textParts.OrderType, "Special", StringComparison.OrdinalIgnoreCase)
            && readableOrder.GetType().Name.IndexOf("SpecialOrder", StringComparison.OrdinalIgnoreCase) < 0
            && !textParts.LooksLikeSpecialOrder)
        {
            NoteParseFailure(source, $"not special: {order.GetType().FullName}", readableOrder, textParts);
            return null;
        }

        var specialGuest = GetMemberValue(readableOrder, "SpecialGuests")
            ?? GetMemberValue(controller, "SpecialGuest")
            ?? GetMemberValue(controller, "OrderingGuest");
        var foodTagId = ToNullableInt(GetMemberValue(readableOrder, "RequestFoodTag"));
        var beverageTagId = NormalizeBeverageTagId(ToNullableInt(GetMemberValue(readableOrder, "RequestBeverageTag")));
        var deskCode = ToNullableInt(GetMemberValue(readableOrder, "DeskCode"))
            ?? ToNullableInt(GetMemberValue(controller, "DeskCode"))
            ?? textParts.DeskCode
            ?? -1;
        return ParseSpecialOrderParts(
            specialGuest,
            foodTagId,
            textParts.FoodTag,
            beverageTagId,
            textParts.BeverageTag,
            textParts.GuestName,
            deskCode,
            readableOrder,
            controller,
            source);
    }

    private static CapturedRuntimeSpecialOrder? ParseSpecialOrderParts(
        object? specialGuest,
        int? foodTagId,
        string textFoodTag,
        int? beverageTagId,
        string textBeverageTag,
        string textGuestName,
        int deskCode,
        object? order,
        object? controller,
        string source)
    {
        var guestId = ToNullableInt(GetMemberValue(specialGuest, "Id"));
        var foodTag = NormalizeTag(textFoodTag);
        if (string.IsNullOrWhiteSpace(foodTag) && !foodTagId.HasValue)
        {
            foodTag = NormalizeTag(InvokeInstanceMethod(controller, "GetOrderFoodText", order)?.ToString());
        }

        if (string.IsNullOrWhiteSpace(foodTag) && !foodTagId.HasValue)
        {
            var fallbackFoodTagId = ToNullableInt(GetMemberValue(order, "foodRequest"));
            if (fallbackFoodTagId.HasValue)
            {
                foodTag = NormalizeTag(InvokeInstanceMethod(specialGuest, "GetFoodTagText", fallbackFoodTagId.Value)?.ToString());
            }
        }

        var beverageTag = NormalizeTag(textBeverageTag);
        if (string.IsNullOrWhiteSpace(beverageTag))
        {
            beverageTag = NormalizeTag(InvokeInstanceMethod(controller, "GetOrderBevText", order)?.ToString());
        }

        if (string.IsNullOrWhiteSpace(beverageTag) && beverageTagId.HasValue && beverageTagId.Value >= 0)
        {
            beverageTag = NormalizeTag(InvokeInstanceMethod(specialGuest, "GetBevTagText", beverageTagId.Value)?.ToString());
        }

        var guestName = NormalizeGuestName(textGuestName);
        if (string.IsNullOrWhiteSpace(guestName) && specialGuest != null)
        {
            guestName = ReadGuestName(specialGuest, guestId);
        }

        if (!foodTagId.HasValue
            && !beverageTagId.HasValue
            && string.IsNullOrWhiteSpace(foodTag)
            && string.IsNullOrWhiteSpace(beverageTag))
        {
            NoteParseFailure(source, "empty food/beverage tag", order);
            return null;
        }

        if (string.IsNullOrWhiteSpace(guestName) && specialGuest == null)
        {
            NoteParseFailure(source, "special guest missing", order);
            return null;
        }

        var capturedAt = DateTime.UtcNow;
        return new CapturedRuntimeSpecialOrder(
            deskCode,
            guestId,
            string.IsNullOrWhiteSpace(guestName) ? "Special guest" : guestName,
            foodTagId ?? 0,
            foodTagId.HasValue,
            foodTag,
            beverageTagId ?? 0,
            beverageTagId.HasValue,
            beverageTag,
            IsOrderFulfilled(order),
            capturedAt,
            capturedAt,
            GetRuntimeObjectKey(order),
            source);
    }

    private static bool IsSameOrderSlot(CapturedRuntimeSpecialOrder left, CapturedRuntimeSpecialOrder right)
    {
        if (!string.IsNullOrWhiteSpace(left.RuntimeKey)
            && !string.IsNullOrWhiteSpace(right.RuntimeKey)
            && string.Equals(left.RuntimeKey, right.RuntimeKey, StringComparison.Ordinal))
        {
            return true;
        }

        if (left.DeskCode >= 0 && right.DeskCode >= 0 && left.DeskCode != right.DeskCode) return false;
        if (left.GuestId.HasValue && right.GuestId.HasValue) return left.GuestId.Value == right.GuestId.Value;
        return string.Equals(left.GuestName, right.GuestName, StringComparison.Ordinal);
    }

    private static CapturedRuntimeSpecialOrder MergeCapturedOrder(
        CapturedRuntimeSpecialOrder incoming,
        CapturedRuntimeSpecialOrder existing)
    {
        if (!CanMergeCapturedOrderDetails(incoming, existing))
        {
            return GetCapturedOrderCompletenessScore(incoming) >= GetCapturedOrderCompletenessScore(existing)
                ? incoming
                : existing with { CapturedAt = incoming.CapturedAt };
        }

        var food = SelectTagParts(
            incoming.FoodTagId,
            incoming.HasFoodTagId,
            incoming.FoodTag,
            existing.FoodTagId,
            existing.HasFoodTagId,
            existing.FoodTag);
        var beverage = SelectTagParts(
            incoming.BeverageTagId,
            incoming.HasBeverageTagId,
            incoming.BeverageTag,
            existing.BeverageTagId,
            existing.HasBeverageTagId,
            existing.BeverageTag);

        return incoming with
        {
            GuestId = incoming.GuestId ?? existing.GuestId,
            GuestName = string.IsNullOrWhiteSpace(incoming.GuestName) || string.Equals(incoming.GuestName, "Special guest", StringComparison.Ordinal)
                ? existing.GuestName
                : incoming.GuestName,
            FoodTagId = food.TagId,
            HasFoodTagId = food.HasTagId,
            FoodTag = food.Tag,
            BeverageTagId = beverage.TagId,
            HasBeverageTagId = beverage.HasTagId,
            BeverageTag = beverage.Tag,
            IsFulfilled = incoming.IsFulfilled || existing.IsFulfilled,
            FirstCapturedAt = existing.FirstCapturedAt < incoming.FirstCapturedAt ? existing.FirstCapturedAt : incoming.FirstCapturedAt,
            RuntimeKey = string.IsNullOrWhiteSpace(incoming.RuntimeKey) ? existing.RuntimeKey : incoming.RuntimeKey,
            CaptureSource = MergeCaptureSource(existing.CaptureSource, incoming.CaptureSource),
        };
    }

    private static bool CanMergeCapturedOrderDetails(CapturedRuntimeSpecialOrder left, CapturedRuntimeSpecialOrder right)
    {
        if (!string.IsNullOrWhiteSpace(left.RuntimeKey)
            && !string.IsNullOrWhiteSpace(right.RuntimeKey)
            && string.Equals(left.RuntimeKey, right.RuntimeKey, StringComparison.Ordinal))
        {
            return true;
        }

        if (HaveConflictingTags(left.FoodTag, left.HasFoodTagId, left.FoodTagId, right.FoodTag, right.HasFoodTagId, right.FoodTagId))
        {
            return false;
        }

        if (HaveConflictingTags(left.BeverageTag, left.HasBeverageTagId, left.BeverageTagId, right.BeverageTag, right.HasBeverageTagId, right.BeverageTagId))
        {
            return false;
        }

        return true;
    }

    private static bool HaveConflictingTags(
        string leftTag,
        bool leftHasTagId,
        int leftTagId,
        string rightTag,
        bool rightHasTagId,
        int rightTagId)
    {
        if (!string.IsNullOrWhiteSpace(leftTag)
            && !string.IsNullOrWhiteSpace(rightTag)
            && !string.Equals(leftTag, rightTag, StringComparison.Ordinal))
        {
            return true;
        }

        return leftHasTagId && rightHasTagId && leftTagId != rightTagId;
    }

    private static (int TagId, bool HasTagId, string Tag) SelectTagParts(
        int incomingTagId,
        bool incomingHasTagId,
        string incomingTag,
        int existingTagId,
        bool existingHasTagId,
        string existingTag)
    {
        var incomingScore = GetTagCompletenessScore(incomingHasTagId, incomingTag);
        var existingScore = GetTagCompletenessScore(existingHasTagId, existingTag);
        return incomingScore >= existingScore
            ? (incomingTagId, incomingHasTagId, incomingTag)
            : (existingTagId, existingHasTagId, existingTag);
    }

    private static int GetCapturedOrderCompletenessScore(CapturedRuntimeSpecialOrder order)
    {
        return GetTagCompletenessScore(order.HasFoodTagId, order.FoodTag)
            + GetTagCompletenessScore(order.HasBeverageTagId, order.BeverageTag)
            + (order.GuestId.HasValue ? 2 : 0)
            + (order.DeskCode >= 0 ? 1 : 0);
    }

    private static int GetTagCompletenessScore(bool hasTagId, string tag)
    {
        return (!string.IsNullOrWhiteSpace(tag) ? 8 : 0) + (hasTagId ? 2 : 0);
    }

    private static string MergeCaptureSource(string existing, string incoming)
    {
        if (string.IsNullOrWhiteSpace(existing)) return incoming;
        if (string.IsNullOrWhiteSpace(incoming)) return existing;
        if (string.Equals(existing, incoming, StringComparison.Ordinal)) return incoming;
        return $"{existing}+{incoming}";
    }

    private static bool IsOrderFulfilled(object? order)
    {
        var value = GetMemberValue(order, "IsFullfilled");
        if (value is bool boolValue) return boolValue;
        return string.Equals(value?.ToString(), "true", StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadGuestName(object specialGuest, int? guestId)
    {
        var stringId = GetMemberValue(specialGuest, "StringId")?.ToString();
        if (!string.IsNullOrWhiteSpace(stringId)) return stringId;
        return guestId.HasValue ? $"Guest {guestId.Value}" : "Special guest";
    }

    private static string NormalizeTag(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";

        var trimmed = value.Trim();
        if (string.Equals(trimmed, "Null", StringComparison.OrdinalIgnoreCase)) return "";
        if (string.Equals(trimmed, "None", StringComparison.OrdinalIgnoreCase)) return "";
        return trimmed.StartsWith("#", StringComparison.Ordinal) ? "" : trimmed;
    }

    private static object? InvokeInstanceMethod(object? instance, string name, params object?[] args)
    {
        if (instance == null) return null;

        var method = instance
            .GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(candidate => candidate.Name == name && candidate.GetParameters().Length == args.Length);
        if (method == null) return null;

        try
        {
            return method.Invoke(instance, args);
        }
        catch
        {
            return null;
        }
    }

    private static object? GetMemberValue(object? instance, string name)
    {
        if (instance == null) return null;
        var type = instance.GetType();

        while (type != null)
        {
            foreach (var fieldName in BuildFieldNameCandidates(name))
            {
                var field = type.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (TryReadField(instance, field, out var fieldValue) && fieldValue != null) return fieldValue;
            }

            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (TryReadProperty(instance, property, out var propertyValue)) return propertyValue;

            var pascalName = char.ToUpperInvariant(name[0]) + name[1..];
            property = type.GetProperty(pascalName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (TryReadProperty(instance, property, out propertyValue)) return propertyValue;

            type = type.BaseType;
        }

        return null;
    }

    private static object? TryCastOrder(object? order, string targetTypeName)
    {
        if (order is not Il2CppObjectBase il2CppObject) return null;

        var targetType = FindType(targetTypeName);
        if (targetType == null) return null;
        if (order.GetType() == targetType) return order;

        var tryCast = typeof(Il2CppObjectBase)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(method => method.Name == "TryCast"
                && method.IsGenericMethodDefinition
                && method.GetParameters().Length == 0);
        if (tryCast == null) return null;

        try
        {
            return tryCast.MakeGenericMethod(targetType).Invoke(il2CppObject, Array.Empty<object?>());
        }
        catch
        {
            return null;
        }
    }

    private static void NoteParseFailure(string source, string reason, object? order = null, ParsedOrderText? textParts = null)
    {
        var shape = DescribeOrderShape(order, textParts);
        lock (SyncRoot)
        {
            _parseFailures++;
            _lastParseFailure = $"{source}: {reason}";
            _lastOrderShape = shape;
            AddRecentParseFailureLocked(source, reason, shape);
            _status = BuildStatusLocked();
        }
    }

    private static void AddRecentParseFailureLocked(string source, string reason, string shape)
    {
        RecentParseFailures.Add(new RuntimeParseFailureDiagnostic(
            DateTime.UtcNow,
            TrimStatus($"{source}: {reason}; {shape}", 900)));
        PruneRecentParseFailuresLocked(DateTime.UtcNow, TimeSpan.FromMinutes(5));
        if (RecentParseFailures.Count > 40)
        {
            RecentParseFailures.RemoveRange(0, RecentParseFailures.Count - 40);
        }
    }

    private static void PruneRecentParseFailuresLocked(DateTime nowUtc, TimeSpan maxAge)
    {
        RecentParseFailures.RemoveAll(failure => nowUtc - failure.CapturedAtUtc > maxAge);
    }

    private static string BuildStatusLocked()
    {
        return $"patched={PatchedMethods.Count}; version={_changeVersion}; callbacks=add:{_addCallbacks},remove:{_removeCallbacks},generated:{_generatedCallbacks},statusUpdate:{_statusCallbacks}; captured={_capturedOrders}; parseFailures={_parseFailures}; lastCapture={_lastCapture}; lastParseFailure={_lastParseFailure}; lastOrderShape={_lastOrderShape}";
    }

    private static ParsedOrderText ParseOrderText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return ParsedOrderText.Empty;

        var lines = text.Replace('\r', '\n').Split('\n');
        return new ParsedOrderText(
            ReadLabeledInt(lines, "DeskCode"),
            ReadLabeledValue(lines, "OrderType"),
            ReadLabeledValue(lines, "ReqFoodTag"),
            ReadLabeledValue(lines, "ReqBevTag"),
            ReadLabeledValue(lines, "Guest"));
    }

    private static int? ReadLabeledInt(IReadOnlyList<string> lines, string label)
    {
        var value = ReadLabeledValue(lines, label);
        return int.TryParse(value, out var parsed) ? parsed : null;
    }

    private static string ReadLabeledValue(IReadOnlyList<string> lines, string label)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            if (!line.StartsWith(label, StringComparison.OrdinalIgnoreCase)) continue;

            var sameLineValue = NormalizeLabelValue(line[label.Length..]);
            if (!string.IsNullOrWhiteSpace(sameLineValue)) return sameLineValue;

            for (var j = i + 1; j < lines.Count; j++)
            {
                var candidateLine = lines[j].Trim();
                if (IsOrderTextFieldLine(candidateLine)) break;

                var candidate = NormalizeTag(candidateLine);
                if (!string.IsNullOrWhiteSpace(candidate)) return candidate;
            }
        }

        return "";
    }

    private static string NormalizeLabelValue(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith(":", StringComparison.Ordinal)) trimmed = trimmed[1..].Trim();
        return NormalizeTag(trimmed);
    }

    private static bool IsOrderTextFieldLine(string value)
    {
        if (value.Length == 0) return false;
        if (value.Length > 8 && value.All(c => c == '/' || char.IsWhiteSpace(c))) return true;

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

    private static string SafeToString(object? value)
    {
        if (value == null) return "";

        try
        {
            return value.ToString() ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static string NormalizeGuestName(string? value)
    {
        var normalized = NormalizeTag(value);
        return string.IsNullOrWhiteSpace(normalized) ? "" : normalized;
    }

    private static bool IsSpecialOrderType(object? value, string formatted)
    {
        if (string.Equals(formatted, "Special", StringComparison.OrdinalIgnoreCase)) return true;
        return ToNullableInt(value) == 1;
    }

    private static string DescribeOrderShape(object? order, ParsedOrderText? textParts)
    {
        if (order == null) return "";

        var typedOrder = TryCastOrder(order, SpecialOrderTypeName);
        var readableOrder = typedOrder ?? order;
        var text = textParts ?? ParseOrderText(SafeToString(readableOrder));
        var parts = new List<string>
        {
            $"type={order.GetType().FullName}",
            $"cast={typedOrder?.GetType().FullName ?? ""}",
            $"Type={ShortValue(GetMemberValue(readableOrder, "Type"))}",
            $"DeskCode={ShortValue(GetMemberValue(readableOrder, "DeskCode"))}",
            $"RequestFoodTag={ShortValue(GetMemberValue(readableOrder, "RequestFoodTag"))}",
            $"RequestBeverageTag={ShortValue(GetMemberValue(readableOrder, "RequestBeverageTag"))}",
            $"SpecialGuests={ShortValue(GetMemberValue(readableOrder, "SpecialGuests"))}",
            $"text={text.ToDiagnosticString()}",
        };

        return TrimStatus(string.Join("|", parts), 700);
    }

    private static string ShortValue(object? value)
    {
        return TrimStatus(FormatValue(value), 80);
    }

    private static string FormatValue(object? value)
    {
        if (value == null) return "null";
        if (value is string stringValue) return stringValue;
        var type = value.GetType();
        if (type.IsEnum || type.IsPrimitive || value is decimal) return value.ToString() ?? "";
        try
        {
            if (value is IConvertible convertible) return convertible.ToInt32(null).ToString();
        }
        catch
        {
            // Fall through to the type name.
        }

        return type.FullName ?? type.Name;
    }

    private static string TrimStatus(string value, int maxLength)
    {
        if (value.Length <= maxLength) return value;
        return value[..Math.Max(0, maxLength - 3)] + "...";
    }

    private static IEnumerable<string> BuildFieldNameCandidates(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) yield break;

        yield return name;
        yield return $"<{name}>k__BackingField";
        yield return $"m_{name}";
        yield return $"_{name}";

        var camelName = char.ToLowerInvariant(name[0]) + name[1..];
        if (!string.Equals(camelName, name, StringComparison.Ordinal))
        {
            yield return camelName;
            yield return $"<{camelName}>k__BackingField";
            yield return $"m_{camelName}";
            yield return $"_{camelName}";
        }
    }

    private static bool TryReadProperty(object? instance, PropertyInfo? property, out object? value)
    {
        value = null;
        if (property == null) return false;

        try
        {
            value = property.GetValue(instance);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadField(object? instance, FieldInfo? field, out object? value)
    {
        value = null;
        if (field == null) return false;

        try
        {
            value = field.GetValue(instance);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string GetRuntimeObjectKey(object? value)
    {
        var pointer = GetMemberValue(value, "Pointer");
        if (pointer is IntPtr intPtr && intPtr != IntPtr.Zero) return $"ptr:{intPtr.ToInt64():x}";
        return "";
    }

    private static Type? FindType(string fullName)
    {
        var direct = Type.GetType(fullName, false);
        if (direct != null) return direct;

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var type = assembly.GetType(fullName, false);
                if (type != null) return type;
            }
            catch
            {
                // Ignore assemblies that cannot resolve unrelated IL2CPP types.
            }
        }

        return null;
    }

    private static int? ToNullableInt(object? value)
    {
        if (value == null) return null;
        if (value is int intValue) return intValue;
        if (value is long longValue) return (int)longValue;
        if (value is short shortValue) return shortValue;
        try
        {
            if (value is IConvertible convertible) return convertible.ToInt32(null);
        }
        catch
        {
            // Fall through to string parsing.
        }

        return int.TryParse(value.ToString(), out var parsed) ? parsed : null;
    }

    private static int ToInt(object? value)
    {
        return ToNullableInt(value) ?? 0;
    }

    private static int? NormalizeBeverageTagId(int? value)
    {
        return value.HasValue && value.Value >= 0 ? value : null;
    }
}

internal readonly record struct ParsedOrderText(
    int? DeskCode,
    string OrderType,
    string FoodTag,
    string BeverageTag,
    string GuestName)
{
    public static ParsedOrderText Empty { get; } = new(null, "", "", "", "");

    public bool LooksLikeSpecialOrder =>
        !string.IsNullOrWhiteSpace(GuestName)
        && (!string.IsNullOrWhiteSpace(FoodTag) || !string.IsNullOrWhiteSpace(BeverageTag));

    public string ToDiagnosticString()
    {
        return $"desk={DeskCode?.ToString() ?? ""},type={OrderType},food={FoodTag},bev={BeverageTag},guest={GuestName}";
    }
}

public sealed record CapturedRuntimeSpecialOrder(
    int DeskCode,
    int? GuestId,
    string GuestName,
    int FoodTagId,
    bool HasFoodTagId,
    string FoodTag,
    int BeverageTagId,
    bool HasBeverageTagId,
    string BeverageTag,
    bool IsFulfilled,
    DateTime FirstCapturedAt,
    DateTime CapturedAt,
    string RuntimeKey,
    string CaptureSource);

internal sealed record RuntimeParseFailureDiagnostic(
    DateTime CapturedAtUtc,
    string Message);
