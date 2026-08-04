using MystiaStewardCompanion.Core;

namespace MystiaStewardCompanion.Save;

public sealed class RuntimeNormalOrderSnapshotService
{
    private const string OrderControllerTypeName = "Night.UI.HUD.Ordering.OrderController";
    private const string OrderingElementTypeName = "NightScene.UI.GuestManagementUtility.OrderingElement";
    private static readonly TimeSpan RuntimeCapturedOrderMaxAge = TimeSpan.FromHours(6);
    private static readonly object FirstSeenLock = new();
    private static readonly Dictionary<string, DateTime> FirstSeenByOrderKey = new(StringComparer.Ordinal);

    private readonly IReadOnlyDictionary<int, Recipe> _recipesById;
    private readonly IReadOnlyDictionary<int, Beverage> _beveragesById;
    private readonly IReadOnlyDictionary<int, NormalCustomer> _normalCustomersById;
    private readonly Dictionary<string, double> _performanceMs = new(StringComparer.Ordinal);

    public RuntimeNormalOrderSnapshotService(DataRepository repository)
    {
        _recipesById = repository.Recipes
            .GroupBy(recipe => recipe.Id)
            .ToDictionary(group => group.Key, group => group.First());
        _beveragesById = repository.Beverages
            .GroupBy(beverage => beverage.Id)
            .ToDictionary(group => group.Key, group => group.First());
        _normalCustomersById = repository.NormalCustomers
            .GroupBy(customer => customer.Id)
            .ToDictionary(group => group.Key, group => group.First());
    }

    public IReadOnlyDictionary<string, double> PerformanceMs => _performanceMs;

    public NormalBusinessContext Load()
    {
        _performanceMs.Clear();
        var errors = new List<string>();
        var source = new List<string>();
        var runtimeCapturedOrders = new List<NormalBusinessOrder>();

        try
        {
            runtimeCapturedOrders = Measure("runtimeCapture", () => ReadRuntimeCapturedOrders().ToList());
            source.Add($"RuntimeCapture={runtimeCapturedOrders.Count}");
            source.Add($"RuntimeCaptureStatus={NormalOrderRuntimeCapture.Status}");
        }
        catch (Exception ex)
        {
            source.Add("RuntimeCapture=err");
            errors.Add($"RuntimeCapture: {ex.Message}");
        }
        var runtimeCaptureReady = NormalOrderRuntimeCapture.IsBusinessReady;
        source.Add($"RuntimeCaptureReady={runtimeCaptureReady}");

        var visibleOrders = new List<NormalBusinessOrder>();

        try
        {
            var orderControllerOrders = Measure("orderController", () => ReadOrderControllerOrders().ToList());
            source.Add($"OrderController={orderControllerOrders.Count(order => order.Source == "OrderController")}");
            source.Add($"OrderControllerElement={orderControllerOrders.Count(order => order.Source == "OrderControllerElement")}");
            visibleOrders.AddRange(orderControllerOrders);
        }
        catch (Exception ex)
        {
            source.Add("OrderController=err");
            errors.Add($"order controller: {ex.Message}");
        }

        try
        {
            var hudOrders = Measure("hud", () => ReadHudOrders().ToList());
            source.Add($"HUD={hudOrders.Count}");
            visibleOrders.AddRange(hudOrders);
        }
        catch (Exception ex)
        {
            source.Add("HUD=err");
            errors.Add($"HUD: {ex.Message}");
        }

        if (runtimeCaptureReady)
        {
            source.Add("normalOrderMode=authoritativeCapture");
            var capturedNativeKeys = runtimeCapturedOrders
                .Select(order => order.OrderKey)
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .ToHashSet(StringComparer.Ordinal);
            var unboundVisibleOrders = visibleOrders
                .Where(order => string.IsNullOrWhiteSpace(order.OrderKey)
                    || !capturedNativeKeys.Contains(order.OrderKey));
            return BuildContext(runtimeCapturedOrders.Concat(unboundVisibleOrders), source, errors);
        }

        source.Add("normalOrderMode=visibleFailClosed");
        errors.Add("普客订单生命周期 Hook 尚未完整就绪，当前仅显示 HUD 订单且禁止自动化。");
        return BuildContext(visibleOrders, source, errors);
    }

    private NormalBusinessContext BuildContext(IEnumerable<NormalBusinessOrder> orders, List<string> source, List<string> errors)
    {
        var deduplicated = Measure("deduplicate", () => ApplyFirstSeenOrder(orders)
                .OrderBy(order => order.FirstSeenAtUtc ?? DateTime.MaxValue)
                .ThenBy(order => order.DeskCode)
                .ThenBy(order => order.GuestName, StringComparer.Ordinal)
                .ToList());
        source.Add($"normalOrders={deduplicated.Count}");
        source.Add("normalOrderSort=firstSeen");

        return new NormalBusinessContext
        {
            Orders = deduplicated,
            Source = string.Join("; ", source),
            Error = errors.Count == 0 ? null : string.Join("; ", errors),
        };
    }

    private T Measure<T>(string key, Func<T> action)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            return action();
        }
        finally
        {
            _performanceMs[key] = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 2);
        }
    }

    private static IReadOnlyList<NormalBusinessOrder> ApplyFirstSeenOrder(IEnumerable<NormalBusinessOrder> orders)
    {
        var grouped = orders
            .GroupBy(BuildOrderKey, StringComparer.Ordinal)
            .Select(group => new
            {
                Key = group.Key,
                Order = MergeOrderGroup(group),
            })
            .ToList();
        var activeKeys = grouped.Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        var now = DateTime.UtcNow;

        lock (FirstSeenLock)
        {
            foreach (var staleKey in FirstSeenByOrderKey.Keys.Where(key => !activeKeys.Contains(key)).ToList())
            {
                FirstSeenByOrderKey.Remove(staleKey);
            }

            foreach (var group in grouped)
            {
                if (!FirstSeenByOrderKey.TryGetValue(group.Key, out var firstSeen))
                {
                    firstSeen = now;
                    FirstSeenByOrderKey[group.Key] = firstSeen;
                }
            }

            return grouped
                .Select(group => CopyWithFirstSeen(group.Order, FirstSeenByOrderKey[group.Key]))
                .ToList();
        }
    }

    private static string BuildOrderKey(NormalBusinessOrder order)
    {
        var rawKey = !string.IsNullOrWhiteSpace(order.OrderKey)
            ? order.OrderKey
            : $"{order.DeskCode}|{order.FoodId}|{order.BeverageId}";
        return order.OrderLifecycleSequence > 0
            ? $"{rawKey}|lifecycle:{order.OrderLifecycleSequence}"
            : $"{rawKey}|unbound";
    }

    private static NormalBusinessOrder MergeOrderGroup(IEnumerable<NormalBusinessOrder> group)
    {
        var orders = group.ToList();
        var first = orders.First();
        var guestName = orders
            .Select(order => order.GuestName)
            .FirstOrDefault(IsSpecificNormalGuestName) ?? first.GuestName;
        var specialBusinessRoleSource = ResolveSpecialBusinessRoleSource(orders);
        var lifecycleSequences = orders
            .Select(order => order.OrderLifecycleSequence)
            .Where(sequence => sequence > 0)
            .Distinct()
            .Take(2)
            .ToArray();
        var orderLifecycleSequence = lifecycleSequences.Length == 1 ? lifecycleSequences[0] : -1;
        return new NormalBusinessOrder
        {
            TraceId = first.TraceId,
            OrderKey = first.OrderKey,
            OrderLifecycleSequence = orderLifecycleSequence,
            DeskCode = first.DeskCode,
            GuestId = orders
                .Select(order => order.GuestId)
                .FirstOrDefault(value => value.HasValue),
            RuntimeGuestId = specialBusinessRoleSource?.RuntimeGuestId,
            GuestName = guestName,
            SpecialBusinessRole = specialBusinessRoleSource?.SpecialBusinessRole ?? "",
            SpecialBusinessRoleLabel = specialBusinessRoleSource?.SpecialBusinessRoleLabel ?? "",
            FoodPreferenceTags = orders
                .SelectMany(order => order.FoodPreferenceTags)
                .Distinct(StringComparer.Ordinal)
                .ToList(),
            BeveragePreferenceTags = orders
                .SelectMany(order => order.BeveragePreferenceTags)
                .Distinct(StringComparer.Ordinal)
                .ToList(),
            Fund = orders.Select(order => order.Fund).FirstOrDefault(value => value.HasValue),
            BaseFundCarry = orders.Select(order => order.BaseFundCarry).FirstOrDefault(value => value.HasValue),
            MaxFundCarry = orders.Select(order => order.MaxFundCarry).FirstOrDefault(value => value.HasValue),
            ExtraFundByBuff = orders.Select(order => order.ExtraFundByBuff).FirstOrDefault(value => value.HasValue),
            WillPayMoney = orders.Select(order => order.WillPayMoney).FirstOrDefault(value => value.HasValue),
            RemainingOrderCount = orders.Select(order => order.RemainingOrderCount).FirstOrDefault(value => value.HasValue),
            FoodId = first.FoodId,
            FoodName = first.FoodName,
            BeverageId = first.BeverageId,
            BeverageName = first.BeverageName,
            HasServedFood = orders.Any(order => order.HasServedFood),
            HasServedBeverage = orders.Any(order => order.HasServedBeverage),
            ReadyToEvaluate = orders.Any(order => order.ReadyToEvaluate),
            HasEvaluated = orders.Any(order => order.HasEvaluated),
            ControllerAvailable = orders.Any(order => order.ControllerAvailable),
            CanAutomate = orderLifecycleSequence > 0 && orders.Any(order => order.CanAutomate),
            ActionBlockReason = orderLifecycleSequence > 0
                ? ResolveActionBlockReason(orders)
                : "订单活动生命周期身份缺失或冲突，暂不执行自动化。",
            FirstSeenAtUtc = first.FirstSeenAtUtc,
            Source = string.Join("/", orders.Select(order => order.Source).Where(source => !string.IsNullOrWhiteSpace(source)).Distinct(StringComparer.Ordinal)),
        };
    }

    private static NormalBusinessOrder? ResolveSpecialBusinessRoleSource(IReadOnlyList<NormalBusinessOrder> orders)
    {
        return orders
            .Where(order => !string.IsNullOrWhiteSpace(order.SpecialBusinessRole))
            .OrderByDescending(order => order.ControllerAvailable && order.CanAutomate)
            .ThenByDescending(order => order.ControllerAvailable)
            .FirstOrDefault();
    }

    private static bool IsSpecificNormalGuestName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var text = value.Trim();
        return !string.Equals(text, "普客", StringComparison.Ordinal)
            && !string.Equals(text, "普通客", StringComparison.Ordinal)
            && !string.Equals(text, "Normal guest", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(text, "NormalGuest", StringComparison.OrdinalIgnoreCase);
    }

    private static NormalBusinessOrder CopyWithFirstSeen(NormalBusinessOrder order, DateTime firstSeenAtUtc)
    {
        var traceOrder = new NormalBusinessOrder
        {
            OrderKey = order.OrderKey,
            OrderLifecycleSequence = order.OrderLifecycleSequence,
            DeskCode = order.DeskCode,
            GuestId = order.GuestId,
            RuntimeGuestId = order.RuntimeGuestId,
            GuestName = order.GuestName,
            SpecialBusinessRole = order.SpecialBusinessRole,
            SpecialBusinessRoleLabel = order.SpecialBusinessRoleLabel,
            FoodPreferenceTags = order.FoodPreferenceTags.ToList(),
            BeveragePreferenceTags = order.BeveragePreferenceTags.ToList(),
            Fund = order.Fund,
            BaseFundCarry = order.BaseFundCarry,
            MaxFundCarry = order.MaxFundCarry,
            ExtraFundByBuff = order.ExtraFundByBuff,
            WillPayMoney = order.WillPayMoney,
            RemainingOrderCount = order.RemainingOrderCount,
            FoodId = order.FoodId,
            FoodName = order.FoodName,
            BeverageId = order.BeverageId,
            BeverageName = order.BeverageName,
            FirstSeenAtUtc = firstSeenAtUtc,
        };

        return new NormalBusinessOrder
        {
            TraceId = string.IsNullOrWhiteSpace(order.TraceId)
                ? RuntimeOrderTraceIdService.GetNormalTraceId(traceOrder)
                : order.TraceId,
            OrderKey = order.OrderKey,
            OrderLifecycleSequence = order.OrderLifecycleSequence,
            DeskCode = order.DeskCode,
            GuestId = order.GuestId,
            RuntimeGuestId = order.RuntimeGuestId,
            GuestName = order.GuestName,
            SpecialBusinessRole = order.SpecialBusinessRole,
            SpecialBusinessRoleLabel = order.SpecialBusinessRoleLabel,
            FoodPreferenceTags = order.FoodPreferenceTags.ToList(),
            BeveragePreferenceTags = order.BeveragePreferenceTags.ToList(),
            Fund = order.Fund,
            BaseFundCarry = order.BaseFundCarry,
            MaxFundCarry = order.MaxFundCarry,
            ExtraFundByBuff = order.ExtraFundByBuff,
            WillPayMoney = order.WillPayMoney,
            RemainingOrderCount = order.RemainingOrderCount,
            FoodId = order.FoodId,
            FoodName = order.FoodName,
            BeverageId = order.BeverageId,
            BeverageName = order.BeverageName,
            HasServedFood = order.HasServedFood,
            HasServedBeverage = order.HasServedBeverage,
            ReadyToEvaluate = order.ReadyToEvaluate,
            HasEvaluated = order.HasEvaluated,
            ControllerAvailable = order.ControllerAvailable,
            CanAutomate = order.CanAutomate,
            ActionBlockReason = order.ActionBlockReason,
            FirstSeenAtUtc = firstSeenAtUtc,
            Source = order.Source,
        };
    }

    private static string ResolveActionBlockReason(IReadOnlyList<NormalBusinessOrder> orders)
    {
        if (orders.Any(order => order.CanAutomate)) return "";

        var reason = orders
            .Select(order => order.ActionBlockReason)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        return string.IsNullOrWhiteSpace(reason)
            ? "订单仍在 HUD 中，但未读取到可执行客人控制器。"
            : reason;
    }

    private IEnumerable<NormalBusinessOrder> ReadOrderControllerOrders()
    {
        var orderControllerType = RuntimeReflectionUtility.FindType(OrderControllerTypeName);
        if (orderControllerType == null) yield break;

        foreach (var order in RuntimeReflectionUtility.EnumerateObjects(RuntimeReflectionUtility.InvokeStaticMethod(orderControllerType, "GetShowInUIOrders")))
        {
            var parsed = ReadNormalOrder(order, null, "OrderController");
            if (parsed != null) yield return parsed;
        }

        var controller = RuntimeReflectionUtility.GetSingletonInstance(orderControllerType)
            ?? RuntimeReflectionUtility.FindUnityObject(orderControllerType);
        if (controller == null) yield break;

        foreach (var element in RuntimeReflectionUtility.EnumerateObjects(RuntimeReflectionUtility.GetMemberValue(controller, "m_Orders")))
        {
            var order = RuntimeReflectionUtility.GetMemberValue(RuntimeReflectionUtility.NormalizeKeyValueValue(element), "ActiveOrder");
            var parsed = ReadNormalOrder(order, null, "OrderControllerElement");
            if (parsed != null) yield return parsed;
        }
    }

    private IEnumerable<NormalBusinessOrder> ReadHudOrders()
    {
        var orderingElementType = RuntimeReflectionUtility.FindType(OrderingElementTypeName);
        if (orderingElementType == null) yield break;

        foreach (var element in RuntimeReflectionUtility.FindUnityObjects(orderingElementType))
        {
            var order = RuntimeReflectionUtility.GetMemberValue(element, "ActiveOrder");
            var parsed = ReadNormalOrder(order, null, "HUD");
            if (parsed != null) yield return parsed;
        }
    }

    private IEnumerable<NormalBusinessOrder> ReadRuntimeCapturedOrders()
    {
        foreach (var captured in NormalOrderRuntimeCapture.Snapshot(RuntimeCapturedOrderMaxAge))
        {
            var parsed = ReadNormalOrder(
                captured.OrderObject,
                captured.ControllerObject,
                $"RuntimeCapture:{captured.CaptureSource}",
                captured.OrderLifecycleSequence);
            if (parsed != null) yield return parsed;
        }
    }

    private NormalBusinessOrder? ReadNormalOrder(
        object? order,
        object? controller,
        string source,
        long expectedLifecycleSequence = -1)
    {
        var resolution = RuntimeOrderTypeResolver.Resolve(order);
        if (!resolution.Resolved
            || resolution.Kind != RuntimeOrderKind.Normal
            || resolution.ReadableOrder == null)
        {
            return null;
        }

        var readableOrder = resolution.ReadableOrder;
        var lifecycleAvailable = TryReadActiveOrderLifecycle(
            readableOrder,
            controller,
            out var orderLifecycleSequence);
        if (expectedLifecycleSequence > 0
            && (!lifecycleAvailable || orderLifecycleSequence != expectedLifecycleSequence))
        {
            return null;
        }
        var classification = SpecialBusinessOrderClassifier.Classify(readableOrder, controller, source);

        var requestFood = SafeGet(readableOrder, "RequestFood") ?? SafeInvoke(readableOrder, "get_RequestFood");
        var requestBeverage = SafeGet(readableOrder, "RequestBeverage") ?? SafeInvoke(readableOrder, "get_RequestBeverage");
        var foodId = ReadSellableId(requestFood, ReadFirstMember(readableOrder, "foodRequest", "FoodRequest", "requestFoodId", "RequestFoodId", "RequestFoodID"));
        var beverageId = ReadSellableId(requestBeverage, ReadFirstMember(readableOrder, "beverageRequest", "BeverageRequest", "requestBevId", "RequestBevId", "requestBeverageId", "RequestBeverageId", "RequestBeverageID"));
        var recipe = ResolveRecipeByFoodId(foodId);
        _beveragesById.TryGetValue(beverageId, out var beverage);
        var guest = SafeGet(readableOrder, "Guest") ?? SafeInvoke(readableOrder, "get_Guest");
        var orderingGuest = SafeGet(controller, "OrderingGuest");
        var guestId = ReadGuestId(guest) ?? ReadGuestId(orderingGuest);
        var normalCustomer = guestId.HasValue && _normalCustomersById.TryGetValue(guestId.Value, out var customer)
            ? customer
            : null;
        var orderKey = BuildRuntimeOrderKey(readableOrder);
        var deskCode = RuntimeReflectionUtility.ToInt(SafeGet(readableOrder, "DeskCode"), -1);
        var guestName = ResolveNormalGuestName(guest)
            ?? ResolveNormalGuestName(orderingGuest)
            ?? ReadTextLikeValue(guest)
            ?? ReadTextLikeValue(orderingGuest)
            ?? classification.RoleLabel
            ?? "";

        return new NormalBusinessOrder
        {
            OrderKey = orderKey,
            OrderLifecycleSequence = lifecycleAvailable ? orderLifecycleSequence : -1,
            DeskCode = deskCode,
            GuestId = guestId,
            RuntimeGuestId = classification.RuntimeGuestId,
            GuestName = guestName,
            SpecialBusinessRole = classification.Role ?? "",
            SpecialBusinessRoleLabel = classification.RoleLabel ?? "",
            FoodPreferenceTags = normalCustomer?.PositiveTags.ToList() ?? new List<string>(),
            BeveragePreferenceTags = normalCustomer?.BeverageTags.ToList() ?? new List<string>(),
            Fund = ReadNullableIntMember("GetFund", controller, orderingGuest, guest),
            BaseFundCarry = ReadNullableIntMember("BaseFundCarry", controller, orderingGuest, guest),
            MaxFundCarry = ReadNullableIntMember("MaxFundCarry", controller, orderingGuest, guest),
            ExtraFundByBuff = ReadNullableIntMember("ExtraFundByBuff", controller, orderingGuest, guest),
            WillPayMoney = ReadNullableBoolMember("WillPayMoney", controller, orderingGuest, guest),
            RemainingOrderCount = ReadNullableIntMember("RemainOrderCount", controller, orderingGuest, guest),
            FoodId = foodId,
            FoodName = recipe?.Name ?? ReadTextLikeValue(requestFood) ?? "",
            BeverageId = beverageId,
            BeverageName = beverage?.Name ?? ReadTextLikeValue(requestBeverage) ?? "",
            HasServedFood = SafeGet(readableOrder, "ServFood") != null || SafeGet(readableOrder, "ServedFoodInAir") != null,
            HasServedBeverage = SafeGet(readableOrder, "ServBeverage") != null || SafeGet(readableOrder, "ServedBeverageInAir") != null,
            ReadyToEvaluate = RuntimeReflectionUtility.ToBool(SafeGet(readableOrder, "IsFullfilled")),
            HasEvaluated = RuntimeReflectionUtility.ToBool(SafeGet(controller, "HasEvaluated") ?? SafeInvoke(controller, "get_HasEvaluated")),
            ControllerAvailable = controller != null,
            CanAutomate = controller != null && classification.AutomationAllowed && lifecycleAvailable,
            ActionBlockReason = !classification.AutomationAllowed
                ? classification.AutomationBlockReason
                : controller == null
                    ? "订单仍在 HUD 中，但未读取到可执行客人控制器。"
                    : lifecycleAvailable
                        ? ""
                        : "订单缺少活动生命周期身份，暂不执行自动化。",
            Source = source,
        };
    }

    private static string BuildRuntimeOrderKey(object order)
    {
        return RuntimeReflectionUtility.TryReadNativeObjectPointer(order, out var pointer)
            ? $"ptr:{pointer:x}"
            : "";
    }

    private static bool TryReadActiveOrderLifecycle(
        object? order,
        object? controller,
        out long lifecycleSequence)
    {
        lifecycleSequence = 0;
        var lifecycleBefore = RuntimeNightBusinessLifecycle.Snapshot;
        if (!lifecycleBefore.IsActive
            || lifecycleBefore.Generation <= 0
            || order == null
            || controller == null
            || !RuntimeReflectionUtility.TryReadNativeObjectPointer(order, out var orderPointer)
            || !RuntimeReflectionUtility.TryReadNativeObjectPointer(controller, out var controllerPointer)
            || !RuntimeOrderTerminalReceiptStore.TryCaptureActiveLifecycle(
                lifecycleBefore.Generation,
                RuntimeOrderKind.Normal,
                orderPointer,
                controllerPointer,
                out lifecycleSequence))
        {
            lifecycleSequence = 0;
            return false;
        }

        var lifecycleAfter = RuntimeNightBusinessLifecycle.Snapshot;
        if (!lifecycleAfter.IsActive || lifecycleAfter.Generation != lifecycleBefore.Generation)
        {
            lifecycleSequence = 0;
            return false;
        }

        return lifecycleSequence > 0;
    }

    private static int ReadSellableId(object? sellable, object? fallback)
    {
        foreach (var member in new[] { "Id", "ID", "id", "foodID", "FoodID" })
        {
            var value = SafeGet(sellable, member) ?? SafeInvoke(sellable, $"get_{member}");
            var parsed = RuntimeReflectionUtility.ToInt(value, int.MinValue);
            if (parsed != int.MinValue) return parsed;
        }

        return RuntimeReflectionUtility.ToInt(fallback, -1);
    }

    private static object? ReadFirstMember(object? value, params string[] members)
    {
        foreach (var member in members)
        {
            var result = SafeGet(value, member);
            if (result != null) return result;

            result = SafeInvoke(value, $"get_{member}");
            if (result != null) return result;
        }

        return null;
    }

    private static object? SafeGet(object? value, string member)
    {
        try
        {
            return RuntimeReflectionUtility.GetMemberValue(value, member);
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadTextLikeValue(object? value)
    {
        if (value == null) return null;
        value = RuntimeReflectionUtility.NormalizeKeyValueValue(value);
        if (value == null) return null;

        foreach (var member in new[] { "Name", "name", "DisplayName", "displayName", "StringId", "stringId", "Text", "text", "Value", "value", "Title", "title" })
        {
            var memberValue = SafeGet(value, member);
            var text = NormalizeText(memberValue);
            if (text != null) return text;
        }

        try
        {
            return NormalizeText(value);
        }
        catch
        {
            // Ignore conversion failures.
        }

        return null;
    }

    private static object? SafeInvoke(object? value, string method)
    {
        try
        {
            return RuntimeReflectionUtility.InvokeMethod(value, method);
        }
        catch
        {
            return null;
        }
    }

    private static int? ReadNullableIntMember(string name, params object?[] instances)
    {
        foreach (var instance in instances)
        {
            var value = ToNullableInt(SafeGet(instance, name) ?? SafeInvoke(instance, $"get_{name}") ?? SafeInvoke(instance, name));
            if (value.HasValue) return value;
        }

        return null;
    }

    private static bool? ReadNullableBoolMember(string name, params object?[] instances)
    {
        foreach (var instance in instances)
        {
            var value = SafeGet(instance, name) ?? SafeInvoke(instance, $"get_{name}") ?? SafeInvoke(instance, name);
            if (value == null) continue;
            if (value is bool boolValue) return boolValue;
            if (bool.TryParse(value.ToString(), out var parsed)) return parsed;
        }

        return null;
    }

    private static int? ToNullableInt(object? value)
    {
        if (value == null) return null;
        if (value is int intValue) return intValue;
        if (value is long longValue) return (int)longValue;
        if (value is short shortValue) return shortValue;
        return int.TryParse(value.ToString(), out var parsed) ? parsed : null;
    }

    private string? ResolveNormalGuestName(object? guest)
    {
        var guestId = ReadGuestId(guest);
        if (!guestId.HasValue) return null;

        return _normalCustomersById.TryGetValue(guestId.Value, out var customer) ? customer.Name : null;
    }

    private Recipe? ResolveRecipeByFoodId(int foodId)
    {
        return _recipesById.TryGetValue(foodId, out var byId) ? byId : null;
    }

    private static int? ReadGuestId(object? guest)
    {
        foreach (var member in new[] { "Id", "ID", "id", "CharacterID", "characterID" })
        {
            var value = SafeGet(guest, member);
            var parsed = RuntimeReflectionUtility.ToInt(value, int.MinValue);
            if (parsed != int.MinValue) return parsed;

            value = SafeInvoke(guest, $"get_{member}");
            parsed = RuntimeReflectionUtility.ToInt(value, int.MinValue);
            if (parsed != int.MinValue) return parsed;
        }

        return null;
    }

    private static string? NormalizeText(object? value)
    {
        if (value == null) return null;

        string? text;
        try
        {
            text = value.ToString();
        }
        catch
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(text)) return null;

        text = text.Trim();
        if (LooksLikeRuntimeTypeName(text, value.GetType())) return null;
        return text;
    }

    private static bool LooksLikeRuntimeTypeName(string text, Type valueType)
    {
        var typeName = valueType.Name;
        var fullName = valueType.FullName;
        if (!string.IsNullOrWhiteSpace(fullName) && text.StartsWith(fullName, StringComparison.Ordinal)) return true;
        if (!string.IsNullOrWhiteSpace(typeName) && string.Equals(text, typeName, StringComparison.Ordinal)) return true;
        if (text.Contains("GameData.CoreLanguage.LanguageBase", StringComparison.Ordinal)) return true;
        if (string.Equals(text, "LanguageBase", StringComparison.Ordinal)) return true;
        if (text.StartsWith("Il2Cpp", StringComparison.Ordinal)) return true;
        return text.Contains('.') && text.IndexOf("GameData.", StringComparison.Ordinal) >= 0;
    }
}
