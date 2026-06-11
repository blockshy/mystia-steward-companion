using MystiaStewardCompanion.Core;

namespace MystiaStewardCompanion.Save;

public sealed class RuntimeNormalOrderSnapshotService
{
    private const string OrderControllerTypeName = "Night.UI.HUD.Ordering.OrderController";
    private const string OrderingElementTypeName = "NightScene.UI.GuestManagementUtility.OrderingElement";
    private const string GuestsManagerTypeName = "NightScene.GuestManagementUtility.GuestsManager";
    private const string GuestGroupControllerTypeName = "NightScene.GuestManagementUtility.GuestGroupController";
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

    public RuntimeNormalOrderSnapshotService(DataRepository repository)
    {
        _repository = repository;
    }

    public NormalBusinessContext Load()
    {
        var orders = new List<NormalBusinessOrder>();
        var errors = new List<string>();
        var source = new List<string>();

        try
        {
            var orderControllerOrders = ReadOrderControllerOrders().ToList();
            source.Add($"OrderController={orderControllerOrders.Count(order => order.Source == "OrderController")}");
            source.Add($"OrderControllerElement={orderControllerOrders.Count(order => order.Source == "OrderControllerElement")}");
            orders.AddRange(orderControllerOrders);
        }
        catch (Exception ex)
        {
            source.Add("OrderController=err");
            errors.Add($"order controller: {ex.Message}");
        }

        try
        {
            var hudOrders = ReadHudOrders().ToList();
            source.Add($"HUD={hudOrders.Count}");
            orders.AddRange(hudOrders);
        }
        catch (Exception ex)
        {
            source.Add("HUD=err");
            errors.Add($"HUD: {ex.Message}");
        }

        source.Add(ReadManagerStatus());

        foreach (var controllerSource in ManagerControllerSources)
        {
            try
            {
                var controllers = ReadManagerControllers(controllerSource.MemberName).ToList();
                source.Add($"{controllerSource.Source}={controllers.Count}");
                orders.AddRange(ReadControllerOrders(controllers, controllerSource.Source));
            }
            catch (Exception ex)
            {
                source.Add($"{controllerSource.Source}=err");
                errors.Add($"{controllerSource.Source}: {ex.Message}");
            }
        }

        try
        {
            var queuedControllers = ReadQueuedControllers().ToList();
            source.Add($"Queue={queuedControllers.Count}");
            orders.AddRange(ReadControllerOrders(queuedControllers, "Queue"));
        }
        catch (Exception ex)
        {
            source.Add("Queue=err");
            errors.Add($"Queue: {ex.Message}");
        }

        var deduplicated = ApplyFirstSeenOrder(orders)
            .OrderBy(order => order.FirstSeenAtUtc ?? DateTime.MaxValue)
            .ThenBy(order => order.DeskCode)
            .ThenBy(order => order.GuestName, StringComparer.Ordinal)
            .ToList();
        source.Add($"normalOrders={deduplicated.Count}");
        source.Add("normalOrderSort=firstSeen");

        return new NormalBusinessContext
        {
            Orders = deduplicated,
            Source = string.Join("; ", source),
            Error = errors.Count == 0 ? null : string.Join("; ", errors),
        };
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
        return $"{order.DeskCode}|{order.GuestName}|{order.FoodId}|{order.BeverageId}";
    }

    private static NormalBusinessOrder MergeOrderGroup(IEnumerable<NormalBusinessOrder> group)
    {
        var orders = group.ToList();
        var first = orders.First();
        return new NormalBusinessOrder
        {
            DeskCode = first.DeskCode,
            GuestName = first.GuestName,
            FoodId = first.FoodId,
            FoodName = first.FoodName,
            BeverageId = first.BeverageId,
            BeverageName = first.BeverageName,
            HasServedFood = orders.Any(order => order.HasServedFood),
            HasServedBeverage = orders.Any(order => order.HasServedBeverage),
            IsFulfilled = orders.Any(order => order.IsFulfilled),
            FirstSeenAtUtc = first.FirstSeenAtUtc,
            Source = string.Join("/", orders.Select(order => order.Source).Where(source => !string.IsNullOrWhiteSpace(source)).Distinct(StringComparer.Ordinal)),
        };
    }

    private static NormalBusinessOrder CopyWithFirstSeen(NormalBusinessOrder order, DateTime firstSeenAtUtc)
    {
        return new NormalBusinessOrder
        {
            DeskCode = order.DeskCode,
            GuestName = order.GuestName,
            FoodId = order.FoodId,
            FoodName = order.FoodName,
            BeverageId = order.BeverageId,
            BeverageName = order.BeverageName,
            HasServedFood = order.HasServedFood,
            HasServedBeverage = order.HasServedBeverage,
            IsFulfilled = order.IsFulfilled,
            FirstSeenAtUtc = firstSeenAtUtc,
            Source = order.Source,
        };
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

        var requestFood = SafeGet(order, "RequestFood");
        var requestBeverage = SafeGet(order, "RequestBeverage");
        var foodId = ReadSellableId(requestFood, SafeGet(order, "foodRequest"));
        var beverageId = ReadSellableId(requestBeverage, SafeGet(order, "beverageRequest"));
        var recipe = _repository.Recipes.FirstOrDefault(item => item.RecipeId == foodId || item.Id == foodId);
        var beverage = _repository.Beverages.FirstOrDefault(item => item.Id == beverageId);

        return new NormalBusinessOrder
        {
            DeskCode = RuntimeReflectionUtility.ToInt(SafeGet(order, "DeskCode"), -1),
            GuestName = ReadTextLikeValue(SafeGet(order, "Guest"))
                ?? ReadTextLikeValue(SafeGet(controller, "OrderingGuest"))
                ?? ReadTextLikeValue(SafeGet(controller, "NormalGuestsGroups"))
                ?? "",
            FoodId = foodId,
            FoodName = recipe?.Name ?? ReadTextLikeValue(requestFood) ?? "",
            BeverageId = beverageId,
            BeverageName = beverage?.Name ?? ReadTextLikeValue(requestBeverage) ?? "",
            HasServedFood = SafeGet(order, "ServFood") != null || SafeGet(order, "ServedFoodInAir") != null,
            HasServedBeverage = SafeGet(order, "ServBeverage") != null || SafeGet(order, "ServedBeverageInAir") != null,
            IsFulfilled = RuntimeReflectionUtility.ToBool(SafeGet(order, "IsFullfilled")),
            Source = source,
        };
    }

    private static bool IsNormalOrder(object? order)
    {
        if (order == null) return false;
        var typeName = order.GetType().Name;
        if (typeName.IndexOf("NormalOrder", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        var orderType = SafeGet(order, "Type")?.ToString();
        return string.Equals(orderType, "Normal", StringComparison.OrdinalIgnoreCase);
    }

    private static int ReadSellableId(object? sellable, object? fallback)
    {
        foreach (var member in new[] { "Id", "ID", "id" })
        {
            var value = SafeGet(sellable, member);
            var parsed = RuntimeReflectionUtility.ToInt(value, int.MinValue);
            if (parsed != int.MinValue) return parsed;
        }

        return RuntimeReflectionUtility.ToInt(fallback, -1);
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
            var text = memberValue?.ToString();
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }

        try
        {
            var text = value.ToString();
            if (!string.IsNullOrWhiteSpace(text) && !text.StartsWith(value.GetType().FullName ?? value.GetType().Name, StringComparison.Ordinal))
            {
                return text;
            }
        }
        catch
        {
            // Ignore conversion failures.
        }

        return null;
    }
}
