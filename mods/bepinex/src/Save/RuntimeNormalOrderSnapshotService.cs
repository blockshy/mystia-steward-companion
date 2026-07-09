using System.Runtime.CompilerServices;
using MystiaStewardCompanion.Core;

namespace MystiaStewardCompanion.Save;

public sealed class RuntimeNormalOrderSnapshotService
{
    private const string OrderControllerTypeName = "Night.UI.HUD.Ordering.OrderController";
    private const string OrderingElementTypeName = "NightScene.UI.GuestManagementUtility.OrderingElement";
    private const string GuestsManagerTypeName = "NightScene.GuestManagementUtility.GuestsManager";
    private const string GuestGroupControllerTypeName = "NightScene.GuestManagementUtility.GuestGroupController";
    private const string NightSceneDirectorTypeName = "NightScene.NightSceneDirector";
    private static readonly TimeSpan RuntimeCapturedOrderMaxAge = TimeSpan.FromHours(6);
    private static readonly (string MemberName, string Source)[] ManagerControllerSources =
    {
        ("AllPresentedGuestGroupController", "Presented"),
        ("AllGuestInDeskController", "Desk"),
        ("AllGuestsControllersInDesk", "DeskMap"),
        ("CanPlayerRepellGuest", "Repellable"),
        ("ManualDesksDic", "ManualDesk"),
    };
    private static readonly object FirstSeenLock = new();
    private static readonly Dictionary<string, DateTime> FirstSeenByOrderKey = new(StringComparer.Ordinal);

    private readonly DataRepository _repository;
    private readonly IReadOnlyDictionary<int, Recipe> _recipesById;
    private readonly IReadOnlyDictionary<int, Beverage> _beveragesById;
    private readonly IReadOnlyDictionary<int, NormalCustomer> _normalCustomersById;
    private readonly Dictionary<string, double> _performanceMs = new(StringComparer.Ordinal);

    public RuntimeNormalOrderSnapshotService(DataRepository repository)
    {
        _repository = repository;
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

        var visibleOrders = new List<NormalBusinessOrder>();
        var resolveVisibleControllers = runtimeCapturedOrders.Count == 0;
        source.Add($"VisibleControllerResolve={resolveVisibleControllers}");

        try
        {
            var orderControllerOrders = Measure("orderController", () => ReadOrderControllerOrders(resolveVisibleControllers).ToList());
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
            var hudOrders = Measure("hud", () => ReadHudOrders(resolveVisibleControllers).ToList());
            source.Add($"HUD={hudOrders.Count}");
            visibleOrders.AddRange(hudOrders);
        }
        catch (Exception ex)
        {
            source.Add("HUD=err");
            errors.Add($"HUD: {ex.Message}");
        }

        if (runtimeCapturedOrders.Count > 0)
        {
            var reconciledCapturedOrders = Measure("runtimeCapture.reconcile", () => ReconcileRuntimeCapturedOrders(runtimeCapturedOrders, visibleOrders, source));
            source.Add("normalOrderMode=liveCaptureReconciled");
            return BuildContext(visibleOrders.Concat(reconciledCapturedOrders), source, errors);
        }

        source.Add("normalOrderMode=reflectionBootstrap");
        var orders = new List<NormalBusinessOrder>(visibleOrders);

        source.Add(Measure("manager.status", ReadManagerStatus));

        foreach (var controllerSource in ManagerControllerSources)
        {
            try
            {
                var controllers = Measure($"controllers.{controllerSource.Source}", () => ReadManagerControllers(controllerSource.MemberName).ToList());
                source.Add($"{controllerSource.Source}={controllers.Count}");
                orders.AddRange(Measure($"orders.{controllerSource.Source}", () => ReadControllerOrders(controllers, controllerSource.Source).ToList()));
            }
            catch (Exception ex)
            {
                source.Add($"{controllerSource.Source}=err");
                errors.Add($"{controllerSource.Source}: {ex.Message}");
            }
        }

        try
        {
            var queuedControllers = Measure("controllers.Queue", () => ReadQueuedControllers().ToList());
            source.Add($"Queue={queuedControllers.Count}");
            orders.AddRange(Measure("orders.Queue", () => ReadControllerOrders(queuedControllers, "Queue").ToList()));
        }
        catch (Exception ex)
        {
            source.Add("Queue=err");
            errors.Add($"Queue: {ex.Message}");
        }

        try
        {
            var manualControllers = Measure("controllers.ManualControlled", () => ReadManualControlledControllers().ToList());
            source.Add($"ManualControlled={manualControllers.Count}");
            orders.AddRange(Measure("orders.ManualControlled", () => ReadControllerOrders(manualControllers, "ManualControlled").ToList()));
        }
        catch (Exception ex)
        {
            source.Add("ManualControlled=err");
            errors.Add($"ManualControlled: {ex.Message}");
        }

        return BuildContext(orders, source, errors);
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
        if (!string.IsNullOrWhiteSpace(order.OrderKey)) return order.OrderKey;
        return $"{order.DeskCode}|{order.FoodId}|{order.BeverageId}";
    }

    private static NormalBusinessOrder MergeOrderGroup(IEnumerable<NormalBusinessOrder> group)
    {
        var orders = group.ToList();
        var first = orders.First();
        var guestName = orders
            .Select(order => order.GuestName)
            .FirstOrDefault(IsSpecificNormalGuestName) ?? first.GuestName;
        var specialBusinessRoleSource = ResolveSpecialBusinessRoleSource(orders);
        return new NormalBusinessOrder
        {
            TraceId = first.TraceId,
            OrderKey = first.OrderKey,
            DeskCode = first.DeskCode,
            GuestId = orders
                .Select(order => order.GuestId)
                .FirstOrDefault(value => value.HasValue),
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
            CanAutomate = orders.Any(order => order.CanAutomate),
            ActionBlockReason = ResolveActionBlockReason(orders),
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
            DeskCode = order.DeskCode,
            GuestId = order.GuestId,
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
            DeskCode = order.DeskCode,
            GuestId = order.GuestId,
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

    private List<NormalBusinessOrder> ReconcileRuntimeCapturedOrders(
        IReadOnlyList<NormalBusinessOrder> runtimeCapturedOrders,
        IReadOnlyList<NormalBusinessOrder> visibleOrders,
        List<string> source)
    {
        var liveRuntimeKeys = visibleOrders
            .Select(order => order.OrderKey)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.Ordinal);
        var liveSlots = visibleOrders
            .Select(BuildOrderSlot)
            .Where(slot => !string.IsNullOrWhiteSpace(slot))
            .ToHashSet(StringComparer.Ordinal);
        var liveOrderKeyBySlot = visibleOrders
            .Select(order => new
            {
                Slot = BuildOrderSlot(order),
                order.OrderKey,
            })
            .Where(order => !string.IsNullOrWhiteSpace(order.Slot) && !string.IsNullOrWhiteSpace(order.OrderKey))
            .GroupBy(order => order.Slot, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().OrderKey, StringComparer.Ordinal);
        var matched = runtimeCapturedOrders
            .Select(order => NormalizeCapturedOrder(order, liveRuntimeKeys, liveSlots, liveOrderKeyBySlot))
            .Where(order => order != null)
            .Cast<NormalBusinessOrder>()
            .ToList();
        var pruned = NormalOrderRuntimeCapture.PruneMissing(liveRuntimeKeys, liveSlots, "normal snapshot live reconciliation");

        source.Add($"RuntimeCaptureLive={visibleOrders.Count}");
        source.Add($"RuntimeCaptureMerged={matched.Count}");
        source.Add($"RuntimeCapturePruned={pruned}");
        return matched;
    }

    private static NormalBusinessOrder? NormalizeCapturedOrder(
        NormalBusinessOrder order,
        IReadOnlySet<string> liveRuntimeKeys,
        IReadOnlySet<string> liveSlots,
        IReadOnlyDictionary<string, string> liveOrderKeyBySlot)
    {
        if (!string.IsNullOrWhiteSpace(order.OrderKey) && liveRuntimeKeys.Contains(order.OrderKey)) return order;
        var slot = BuildOrderSlot(order);
        if (slot.Length == 0 || !liveSlots.Contains(slot)) return null;
        if (!liveOrderKeyBySlot.TryGetValue(slot, out var liveOrderKey)
            || string.IsNullOrWhiteSpace(liveOrderKey)
            || string.Equals(order.OrderKey, liveOrderKey, StringComparison.Ordinal))
        {
            return order;
        }

        return CopyWithOrderKey(order, liveOrderKey);
    }

    private static NormalBusinessOrder CopyWithOrderKey(NormalBusinessOrder order, string orderKey)
    {
        return new NormalBusinessOrder
        {
            TraceId = order.TraceId,
            OrderKey = orderKey,
            DeskCode = order.DeskCode,
            GuestId = order.GuestId,
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
            FirstSeenAtUtc = order.FirstSeenAtUtc,
            Source = string.IsNullOrWhiteSpace(order.Source)
                ? "RuntimeCaptureSlotMatched"
                : $"{order.Source}/RuntimeCaptureSlotMatched",
        };
    }

    private static string BuildOrderSlot(NormalBusinessOrder order)
    {
        return order.DeskCode < 0 || (order.FoodId < 0 && order.BeverageId < 0)
            ? ""
            : $"{order.DeskCode}|{order.FoodId}|{order.BeverageId}";
    }

    private IEnumerable<NormalBusinessOrder> ReadOrderControllerOrders(bool resolveController)
    {
        var orderControllerType = RuntimeReflectionUtility.FindType(OrderControllerTypeName);
        if (orderControllerType == null) yield break;

        foreach (var order in RuntimeReflectionUtility.EnumerateObjects(RuntimeReflectionUtility.InvokeStaticMethod(orderControllerType, "GetShowInUIOrders")))
        {
            var parsed = ReadNormalOrder(order, resolveController ? FindControllerForOrder(order) : null, "OrderController");
            if (parsed != null) yield return parsed;
        }

        var controller = RuntimeReflectionUtility.GetSingletonInstance(orderControllerType)
            ?? RuntimeReflectionUtility.FindUnityObject(orderControllerType);
        if (controller == null) yield break;

        foreach (var element in RuntimeReflectionUtility.EnumerateObjects(RuntimeReflectionUtility.GetMemberValue(controller, "m_Orders")))
        {
            var order = RuntimeReflectionUtility.GetMemberValue(RuntimeReflectionUtility.NormalizeKeyValueValue(element), "ActiveOrder");
            var parsed = ReadNormalOrder(order, resolveController ? FindControllerForOrder(order) : null, "OrderControllerElement");
            if (parsed != null) yield return parsed;
        }
    }

    private IEnumerable<NormalBusinessOrder> ReadHudOrders(bool resolveController)
    {
        var orderingElementType = RuntimeReflectionUtility.FindType(OrderingElementTypeName);
        if (orderingElementType == null) yield break;

        foreach (var element in RuntimeReflectionUtility.FindUnityObjects(orderingElementType))
        {
            var order = RuntimeReflectionUtility.GetMemberValue(element, "ActiveOrder");
            var parsed = ReadNormalOrder(order, resolveController ? FindControllerForOrder(order) : null, "HUD");
            if (parsed != null) yield return parsed;
        }
    }

    private IEnumerable<object?> ReadManagerControllers(string memberName)
    {
        var manager = FindGuestsManager();
        if (manager == null) yield break;

        foreach (var item in RuntimeReflectionUtility.EnumerateObjects(RuntimeReflectionUtility.GetMemberValue(manager, memberName)))
        {
            var controller = RuntimeReflectionUtility.NormalizeKeyValueValue(item);
            if (controller != null) yield return controller;
        }
    }

    private static IEnumerable<object?> ReadQueuedControllers()
    {
        var guestGroupControllerType = RuntimeReflectionUtility.FindType(GuestGroupControllerTypeName);
        if (guestGroupControllerType == null) yield break;

        foreach (var item in RuntimeReflectionUtility.EnumerateObjects(RuntimeReflectionUtility.GetStaticMemberValue(guestGroupControllerType, "QueuedGuestControllers")))
        {
            var controller = RuntimeReflectionUtility.NormalizeKeyValueValue(item);
            if (controller != null) yield return controller;
        }
    }

    private IEnumerable<NormalBusinessOrder> ReadControllerOrders(IEnumerable<object?> controllers, string source)
    {
        foreach (var controller in controllers)
        {
            foreach (var order in EnumerateControllerOrders(controller))
            {
                var parsed = ReadNormalOrder(order, controller, source);
                if (parsed != null) yield return parsed;
            }
        }
    }

    private IEnumerable<NormalBusinessOrder> ReadRuntimeCapturedOrders()
    {
        foreach (var captured in NormalOrderRuntimeCapture.Snapshot(RuntimeCapturedOrderMaxAge))
        {
            var parsed = ReadNormalOrder(captured.OrderObject, captured.ControllerObject, $"RuntimeCapture:{captured.CaptureSource}");
            if (parsed != null) yield return parsed;
        }
    }

    private static IEnumerable<object?> EnumerateControllerOrders(object? controller)
    {
        if (controller == null) yield break;

        foreach (var memberName in new[] { "AllOrders", "AllOrdersData" })
        {
            foreach (var order in RuntimeReflectionUtility.EnumerateObjects(RuntimeReflectionUtility.GetMemberValue(controller, memberName)))
            {
                var normalized = RuntimeReflectionUtility.NormalizeKeyValueValue(order);
                if (normalized != null) yield return normalized;
            }
        }

        var peekOrder = RuntimeReflectionUtility.InvokeMethod(controller, "PeekOrders");
        if (peekOrder != null) yield return peekOrder;
    }

    private static object? FindGuestsManager()
    {
        var guestsManagerType = RuntimeReflectionUtility.FindType(GuestsManagerTypeName);
        if (guestsManagerType == null) return null;
        return RuntimeReflectionUtility.GetSingletonInstance(guestsManagerType)
            ?? RuntimeReflectionUtility.FindUnityObject(guestsManagerType);
    }

    private static object? FindControllerForOrder(object? order)
    {
        if (order == null) return null;
        var manager = FindGuestsManager();
        if (manager == null) return null;

        foreach (var controller in EnumerateAllKnownControllers(manager))
        {
            if (controller == null) continue;
            foreach (var candidate in EnumerateControllerOrders(controller))
            {
                if (candidate != null && IsSameObject(candidate, order)) return controller;
            }
        }

        var deskCode = RuntimeReflectionUtility.ToInt(SafeGet(order, "DeskCode") ?? SafeInvoke(order, "get_DeskCode"), -999);
        if (deskCode < 0) return null;
        return EnumerateAllKnownControllers(manager)
            .FirstOrDefault(controller => RuntimeReflectionUtility.ToInt(SafeGet(controller, "DeskCode") ?? SafeInvoke(controller, "get_DeskCode"), -999) == deskCode);
    }

    private static IEnumerable<object?> EnumerateAllKnownControllers(object manager)
    {
        foreach (var (memberName, _) in ManagerControllerSources)
        {
            foreach (var item in RuntimeReflectionUtility.EnumerateObjects(RuntimeReflectionUtility.GetMemberValue(manager, memberName)))
            {
                var controller = RuntimeReflectionUtility.NormalizeKeyValueValue(item);
                if (controller != null) yield return controller;
            }
        }

        foreach (var controller in ReadQueuedControllers())
        {
            if (controller != null) yield return controller;
        }

        foreach (var controller in ReadManualControlledControllers())
        {
            if (controller != null) yield return controller;
        }
    }

    private static IEnumerable<object?> ReadManualControlledControllers()
    {
        var directorType = RuntimeReflectionUtility.FindType(NightSceneDirectorTypeName);
        if (directorType == null) yield break;

        var director = RuntimeReflectionUtility.GetSingletonInstance(directorType)
            ?? RuntimeReflectionUtility.FindUnityObject(directorType);
        if (director == null) yield break;

        foreach (var item in RuntimeReflectionUtility.EnumerateObjects(RuntimeReflectionUtility.GetMemberValue(director, "controlledGuest")))
        {
            var controller = RuntimeReflectionUtility.NormalizeKeyValueValue(item);
            if (controller != null) yield return controller;
        }
    }

    private static string ReadManagerStatus()
    {
        var guestsManagerType = RuntimeReflectionUtility.FindType(GuestsManagerTypeName);
        if (guestsManagerType == null) return "manager=type-missing";

        var manager = RuntimeReflectionUtility.GetSingletonInstance(guestsManagerType)
            ?? RuntimeReflectionUtility.FindUnityObject(guestsManagerType);
        return manager == null ? "manager=missing" : "manager=ok";
    }

    private NormalBusinessOrder? ReadNormalOrder(object? order, object? controller, string source)
    {
        if (order == null) return null;
        if (!IsNormalOrder(order)) return null;
        var classification = SpecialBusinessOrderClassifier.Classify(order, controller, source);

        var requestFood = SafeGet(order, "RequestFood") ?? SafeInvoke(order, "get_RequestFood");
        var requestBeverage = SafeGet(order, "RequestBeverage") ?? SafeInvoke(order, "get_RequestBeverage");
        var foodId = ReadSellableId(requestFood, ReadFirstMember(order, "foodRequest", "FoodRequest", "requestFoodId", "RequestFoodId", "RequestFoodID"));
        var beverageId = ReadSellableId(requestBeverage, ReadFirstMember(order, "beverageRequest", "BeverageRequest", "requestBevId", "RequestBevId", "requestBeverageId", "RequestBeverageId", "RequestBeverageID"));
        var recipe = ResolveRecipeByFoodId(foodId);
        _beveragesById.TryGetValue(beverageId, out var beverage);
        var guest = SafeGet(order, "Guest") ?? SafeInvoke(order, "get_Guest");
        var orderingGuest = SafeGet(controller, "OrderingGuest");
        var guestId = ReadGuestId(guest) ?? ReadGuestId(orderingGuest);
        var normalCustomer = guestId.HasValue && _normalCustomersById.TryGetValue(guestId.Value, out var customer)
            ? customer
            : null;
        var orderKey = BuildRuntimeOrderKey(order);
        var deskCode = RuntimeReflectionUtility.ToInt(SafeGet(order, "DeskCode"), -1);
        var guestName = ResolveNormalGuestName(guest)
            ?? ResolveNormalGuestName(orderingGuest)
            ?? ReadTextLikeValue(guest)
            ?? ReadTextLikeValue(orderingGuest)
            ?? classification.RoleLabel
            ?? "";

        return new NormalBusinessOrder
        {
            OrderKey = orderKey,
            DeskCode = deskCode,
            GuestId = guestId,
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
            HasServedFood = SafeGet(order, "ServFood") != null || SafeGet(order, "ServedFoodInAir") != null,
            HasServedBeverage = SafeGet(order, "ServBeverage") != null || SafeGet(order, "ServedBeverageInAir") != null,
            ReadyToEvaluate = RuntimeReflectionUtility.ToBool(SafeGet(order, "IsFullfilled")),
            HasEvaluated = RuntimeReflectionUtility.ToBool(SafeGet(controller, "HasEvaluated") ?? SafeInvoke(controller, "get_HasEvaluated")),
            ControllerAvailable = controller != null,
            CanAutomate = controller != null && !classification.BlocksNormalAutomation,
            ActionBlockReason = classification.BlocksNormalAutomation
                ? classification.AutomationBlockReason
                : controller == null ? "订单仍在 HUD 中，但未读取到可执行客人控制器。" : "",
            Source = source,
        };
    }

    private static bool IsNormalOrder(object? order)
    {
        if (order == null) return false;
        var typeName = order.GetType().Name;
        if (typeName.IndexOf("NormalOrder", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        var orderType = SafeGet(order, "Type")?.ToString();
        if (string.Equals(orderType, "Normal", StringComparison.OrdinalIgnoreCase)) return true;
        return RuntimeReflectionUtility.ToInt(SafeGet(order, "Type"), -1) == 0;
    }

    private static string BuildRuntimeOrderKey(object order)
    {
        try
        {
            return $"ptr:{ReadObjectPointer(order):x}";
        }
        catch
        {
            return $"hash:{RuntimeHelpers.GetHashCode(order)}";
        }
    }

    private static bool IsSameObject(object left, object right)
    {
        try
        {
            return ReadObjectPointer(left) == ReadObjectPointer(right);
        }
        catch
        {
            return ReferenceEquals(left, right);
        }
    }

    private static nint ReadObjectPointer(object target)
    {
        var pointer = SafeGet(target, "Pointer")
            ?? SafeGet(target, "NativePointer")
            ?? SafeGet(target, "m_CachedPtr");
        if (pointer is IntPtr intPtr) return intPtr;
        if (pointer is nint native) return native;
        if (pointer is IConvertible convertible) return new IntPtr(convertible.ToInt64(null));
        return new IntPtr(RuntimeHelpers.GetHashCode(target));
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
