using System.Collections;
using System.Reflection;
using MystiaSteward.Core;
using UnityEngine;

namespace MystiaSteward.Save;

public sealed class NightBusinessReflectionProvider
{
    private static readonly TimeSpan CapturedOrderMaxAge = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan UnmatchedCapturedOrderGrace = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan RuntimeCapturedOrderMaxAge = TimeSpan.FromMinutes(3);

    private const string GuestGroupControllerTypeName = "NightScene.GuestManagementUtility.GuestGroupController";
    private const string GuestsManagerTypeName = "NightScene.GuestManagementUtility.GuestsManager";
    private const string OrderControllerTypeName = "Night.UI.HUD.Ordering.OrderController";
    private const string OrderingElementTypeName = "NightScene.UI.GuestManagementUtility.OrderingElement";
    private const string WorkSceneServePannelTypeName = "NightScene.UI.GuestManagementUtility.WorkSceneServePannel";
    private const string IzakayaConfigureTypeName = "GameData.RunTime.NightSceneUtility.IzakayaConfigure";
    private static readonly (string MemberName, string Source)[] ManagerControllerSources =
    {
        ("AllPresentedGuestGroupController", "Presented"),
        ("AllGuestInDeskController", "Desk"),
        ("AllGuestsControllersInDesk", "DeskMap"),
        ("CanPlayerRepellGuest", "Repellable"),
        ("ManualDesksDic", "ManualDesk"),
    };

    private readonly DataRepository _repository;
    private readonly NightBusinessDiagnosticSink? _diagnostics;
    private readonly string _sceneName;
    private readonly bool _useLogFallback;
    private IReadOnlyList<string>? _foodTagCandidates;
    private IReadOnlyList<string>? _beverageTagCandidates;

    public NightBusinessReflectionProvider(
        DataRepository repository,
        NightBusinessDiagnosticSink? diagnostics = null,
        string sceneName = "",
        bool useLogFallback = false)
    {
        _repository = repository;
        _diagnostics = diagnostics;
        _sceneName = sceneName;
        _useLogFallback = useLogFallback;
    }

    public NightBusinessContext LoadContext()
    {
        var guests = new List<NightBusinessGuest>();
        var orders = new List<NightBusinessOrder>();
        var errors = new List<string>();
        var sourceStats = new List<string>();

        try
        {
            var orderControllerOrders = ReadOrderControllerOrders().ToList();
            sourceStats.Add($"OrderController={orderControllerOrders.Count}");
            orders.AddRange(orderControllerOrders);
        }
        catch (Exception ex)
        {
            sourceStats.Add("OrderController=err");
            errors.Add($"order controller: {ex.Message}");
        }

        try
        {
            var hudOrders = ReadHudOrders().ToList();
            sourceStats.Add($"HUD={hudOrders.Count}");
            orders.AddRange(hudOrders);
        }
        catch (Exception ex)
        {
            sourceStats.Add("HUD=err");
            errors.Add($"HUD orders: {ex.Message}");
        }

        try
        {
            var servePanelContexts = ReadServePanelContexts().ToList();
            sourceStats.Add($"ServePanel={servePanelContexts.Count}");
            guests.AddRange(ReadServePanelRareGuests(servePanelContexts));
            orders.AddRange(ReadServePanelOrders(servePanelContexts));
        }
        catch (Exception ex)
        {
            sourceStats.Add("ServePanel=err");
            errors.Add($"serve panel: {ex.Message}");
        }

        var managerStatus = ReadManagerStatus();
        var queueStatus = ReadQueueStatus();

        foreach (var source in ManagerControllerSources)
        {
            try
            {
                var controllers = ReadManagerControllers(source.MemberName).ToList();
                sourceStats.Add($"{source.Source}={controllers.Count}");
                guests.AddRange(ReadRareGuests(controllers, source.Source));
                orders.AddRange(ReadControllerOrders(controllers, source.Source));
            }
            catch (Exception ex)
            {
                sourceStats.Add($"{source.Source}=err");
                errors.Add($"{source.Source}: {ex.Message}");
            }
        }

        try
        {
            var queuedControllers = ReadQueuedControllers().ToList();
            sourceStats.Add($"Queue={queuedControllers.Count}");
            guests.AddRange(ReadRareGuests(queuedControllers, "Queue"));
            orders.AddRange(ReadControllerOrders(queuedControllers, "Queue"));
        }
        catch (Exception ex)
        {
            sourceStats.Add("Queue=err");
            errors.Add($"Queue: {ex.Message}");
        }

        var activeGuests = DeduplicateGuests(guests);
        var rawLiveOrders = orders.ToList();
        var acceptedRuntimeOrders = new List<NightBusinessOrder>();
        try
        {
            var runtimeOrders = SpecialOrderRuntimeCapture.Snapshot(RuntimeCapturedOrderMaxAge);
            acceptedRuntimeOrders = ReadRuntimeCapturedOrders(runtimeOrders, activeGuests).ToList();
            sourceStats.Add($"RuntimeCapture={acceptedRuntimeOrders.Count}/{runtimeOrders.Count}");
            sourceStats.Add($"RuntimeCaptureStatus={SpecialOrderRuntimeCapture.Status}");
            orders.AddRange(acceptedRuntimeOrders);
        }
        catch (Exception ex)
        {
            sourceStats.Add("RuntimeCapture=err");
            errors.Add($"runtime capture: {ex.Message}");
        }

        var acceptedCapturedOrders = new List<NightBusinessOrder>();
        if (_useLogFallback)
        {
            try
            {
                var capturedOrders = SpecialOrderLogCapture.Snapshot(CapturedOrderMaxAge);
                acceptedCapturedOrders = ReadCapturedLogOrders(capturedOrders, activeGuests).ToList();
                sourceStats.Add($"OrderLog={acceptedCapturedOrders.Count}/{capturedOrders.Count}");
                orders.AddRange(acceptedCapturedOrders);
            }
            catch (Exception ex)
            {
                sourceStats.Add("OrderLog=err");
                errors.Add($"order log: {ex.Message}");
            }
        }
        else
        {
            sourceStats.Add("OrderLog=disabled");
        }

        var activeOrders = DeduplicateOrders(orders);
        var place = ReadCurrentPlace();
        var placeLabel = ReadCurrentPlaceLabel();
        WriteDiagnostics(
            managerStatus,
            queueStatus,
            sourceStats,
            errors,
            guests,
            rawLiveOrders,
            acceptedRuntimeOrders,
            acceptedCapturedOrders,
            activeGuests,
            activeOrders,
            place,
            placeLabel);

        return new NightBusinessContext
        {
            Place = place,
            PlaceLabel = placeLabel,
            ActiveRareGuests = activeGuests,
            Orders = activeOrders,
            Source = $"Night scene live orders; {managerStatus}; {queueStatus}; {string.Join("; ", sourceStats)}; guests={activeGuests.Count}; orders={activeOrders.Count}",
            Error = errors.Count == 0 ? null : string.Join("; ", errors),
        };
    }

    private void WriteDiagnostics(
        string managerStatus,
        string queueStatus,
        IReadOnlyList<string> sourceStats,
        IReadOnlyList<string> errors,
        IReadOnlyList<NightBusinessGuest> rawGuests,
        IReadOnlyList<NightBusinessOrder> rawLiveOrders,
        IReadOnlyList<NightBusinessOrder> acceptedRuntimeOrders,
        IReadOnlyList<NightBusinessOrder> acceptedLogOrders,
        IReadOnlyList<NightBusinessGuest> activeGuests,
        IReadOnlyList<NightBusinessOrder> finalOrders,
        string? place,
        string? placeLabel)
    {
        if (_diagnostics == null) return;

        try
        {
            _diagnostics.Write(new NightBusinessDiagnosticSnapshot
            {
                CapturedAtUtc = DateTime.UtcNow,
                SceneName = _sceneName,
                Place = place,
                PlaceLabel = placeLabel,
                ManagerStatus = managerStatus,
                QueueStatus = queueStatus,
                SourceStats = sourceStats.ToList(),
                Errors = errors.ToList(),
                RawGuests = rawGuests.ToList(),
                RawLiveOrders = rawLiveOrders.ToList(),
                AcceptedRuntimeOrders = acceptedRuntimeOrders.ToList(),
                AcceptedLogOrders = acceptedLogOrders.ToList(),
                ActiveGuests = activeGuests.ToList(),
                FinalOrders = finalOrders.ToList(),
            });
        }
        catch
        {
            // Diagnostics must never affect gameplay or recommendation refreshes.
        }
    }

    private IEnumerable<NightBusinessOrder> ReadOrderControllerOrders()
    {
        var orderControllerType = FindType(OrderControllerTypeName);
        if (orderControllerType == null) yield break;

        foreach (var order in EnumerateObjects(InvokeStaticMethod(orderControllerType, "GetShowInUIOrders")))
        {
            var parsed = ReadOrder(order, null, "OrderController");
            if (parsed != null) yield return parsed;
        }

        var controller = GetSingletonInstance(orderControllerType) ?? FindUnityObject(orderControllerType);
        if (controller == null) yield break;

        foreach (var element in EnumerateObjects(GetMemberValue(controller, "m_Orders")))
        {
            var order = GetMemberValue(element, "ActiveOrder");
            var parsed = ReadOrder(order, null, "OrderControllerElement");
            if (parsed != null) yield return parsed;
        }
    }

    private IEnumerable<NightBusinessOrder> ReadHudOrders()
    {
        var orderingElementType = FindType(OrderingElementTypeName);
        if (orderingElementType == null) yield break;

        foreach (var element in FindUnityObjects(orderingElementType))
        {
            var order = GetMemberValue(element, "ActiveOrder");
            var parsed = ReadOrder(order, null, "HUD");
            if (parsed != null) yield return parsed;
        }
    }

    private IEnumerable<(object? Order, object? Controller)> ReadServePanelContexts()
    {
        var servePanelType = FindType(WorkSceneServePannelTypeName);
        if (servePanelType == null) yield break;

        foreach (var panel in FindUnityObjects(servePanelType))
        {
            var context = GetMemberValue(panel, "OpenContext");
            var order = GetMemberValue(panel, "operatingOrder") ?? GetMemberValue(context, "operatingOrder");
            var controller = GetMemberValue(panel, "currentGuestController") ?? GetMemberValue(context, "currentGuestController");
            if (order != null || controller != null) yield return (order, controller);
        }
    }

    private IEnumerable<NightBusinessGuest> ReadServePanelRareGuests(IEnumerable<(object? Order, object? Controller)> contexts)
    {
        foreach (var context in contexts)
        {
            var guest = ReadRareGuest(context.Controller, "ServePanel")
                ?? ReadRareGuestFromOrder(context.Order, context.Controller, "ServePanel");
            if (guest != null) yield return guest;
        }
    }

    private IEnumerable<NightBusinessOrder> ReadServePanelOrders(IEnumerable<(object? Order, object? Controller)> contexts)
    {
        foreach (var context in contexts)
        {
            var parsed = ReadOrder(context.Order, context.Controller, "ServePanel");
            if (parsed != null) yield return parsed;
        }
    }

    private IEnumerable<object?> ReadManagerControllers(string memberName)
    {
        var manager = FindGuestsManager();
        if (manager == null) yield break;

        foreach (var item in EnumerateObjects(GetMemberValue(manager, memberName)))
        {
            var controller = NormalizeController(item);
            if (controller != null) yield return controller;
        }
    }

    private IEnumerable<object?> ReadQueuedControllers()
    {
        var guestGroupControllerType = FindType(GuestGroupControllerTypeName);
        if (guestGroupControllerType == null) yield break;

        foreach (var item in EnumerateObjects(GetStaticMemberValue(guestGroupControllerType, "QueuedGuestControllers")))
        {
            var controller = NormalizeController(item);
            if (controller != null) yield return controller;
        }
    }

    private IEnumerable<NightBusinessOrder> ReadControllerOrders(IEnumerable<object?> controllers, string source)
    {
        foreach (var controller in controllers)
        {
            if (!IsRareGuestController(controller)) continue;

            foreach (var order in EnumerateControllerOrders(controller))
            {
                var parsed = ReadOrder(order, controller, source);
                if (parsed != null) yield return parsed;
            }
        }
    }

    private static IEnumerable<object?> EnumerateControllerOrders(object? controller)
    {
        if (controller == null) yield break;

        foreach (var order in EnumerateObjects(GetMemberValue(controller, "AllOrders")))
        {
            yield return order;
        }

        foreach (var order in EnumerateObjects(GetMemberValue(controller, "AllOrdersData")))
        {
            yield return order;
        }

        var peekOrder = InvokeInstanceMethod(controller, "PeekOrders");
        if (peekOrder != null) yield return peekOrder;
    }

    private IEnumerable<NightBusinessGuest> ReadRareGuests(IEnumerable<object?> controllers, string source)
    {
        foreach (var controller in controllers)
        {
            var guest = ReadRareGuest(controller, source);
            if (guest != null) yield return guest;
        }
    }

    private NightBusinessGuest? ReadRareGuest(object? controller, string source)
    {
        if (controller == null) return null;

        var specialGuest = GetMemberValue(controller, "SpecialGuest");
        var guest = specialGuest ?? GetMemberValue(controller, "OrderingGuest");
        if (guest == null) return null;

        var guestId = ToNullableInt(GetMemberValue(guest, "Id"));
        if (specialGuest == null && (!guestId.HasValue || !_repository.RareCustomersById.ContainsKey(guestId.Value)))
        {
            return null;
        }

        return new NightBusinessGuest
        {
            DeskCode = ToInt(GetMemberValue(controller, "DeskCode")),
            GuestId = guestId,
            GuestName = guestId.HasValue && _repository.RareCustomersById.TryGetValue(guestId.Value, out var rareCustomer)
                ? rareCustomer.Name
                : ReadGuestName(guest, guestId),
            Source = source,
        };
    }

    private NightBusinessGuest? ReadRareGuestFromOrder(object? order, object? controller, string source)
    {
        if (order == null) return null;

        var specialGuest = GetMemberValue(order, "SpecialGuests") ?? GetMemberValue(controller, "SpecialGuest");
        if (specialGuest == null) return null;

        var guestId = ToNullableInt(GetMemberValue(specialGuest, "Id"));
        return new NightBusinessGuest
        {
            DeskCode = ToInt(GetMemberValue(order, "DeskCode") ?? GetMemberValue(controller, "DeskCode")),
            GuestId = guestId,
            GuestName = guestId.HasValue && _repository.RareCustomersById.TryGetValue(guestId.Value, out var rareCustomer)
                ? rareCustomer.Name
                : ReadGuestName(specialGuest, guestId),
            Source = source,
        };
    }

    private NightBusinessOrder? ReadOrder(object? order, object? controller, string source)
    {
        if (order == null) return null;
        if (!IsSpecialOrder(order)) return null;

        var specialGuest = GetMemberValue(order, "SpecialGuests")
            ?? GetMemberValue(controller, "SpecialGuest")
            ?? GetMemberValue(controller, "OrderingGuest");
        if (specialGuest == null) return null;

        var guestId = ToNullableInt(GetMemberValue(specialGuest, "Id"));
        var foodTag = ResolveOrderTagText(order, controller, specialGuest, "GetOrderFoodText", "GetFoodTagText", "RequestFoodTag", "ReqFoodTag", useFoodTagMap: true);
        var beverageTag = ResolveOrderTagText(order, controller, specialGuest, "GetOrderBevText", "GetBevTagText", "RequestBeverageTag", "ReqBevTag", useFoodTagMap: false);
        var foodTagId = ResolveTagId(foodTag, GetMemberValue(order, "RequestFoodTag"));
        var beverageTagId = ResolveTagId(beverageTag, GetMemberValue(order, "RequestBeverageTag"));
        if (foodTagId == 0 && beverageTagId == 0 && string.IsNullOrWhiteSpace(foodTag) && string.IsNullOrWhiteSpace(beverageTag))
        {
            return null;
        }

        var deskCode = ToInt(GetMemberValue(order, "DeskCode") ?? GetMemberValue(controller, "DeskCode"));
        var guestName = guestId.HasValue && _repository.RareCustomersById.TryGetValue(guestId.Value, out var rareCustomer)
            ? rareCustomer.Name
            : ReadGuestName(specialGuest, guestId);

        return new NightBusinessOrder
        {
            DeskCode = deskCode,
            GuestId = guestId,
            GuestName = guestName,
            FoodTagId = foodTagId,
            FoodTag = foodTag,
            BeverageTagId = beverageTagId,
            BeverageTag = beverageTag,
            Source = source,
        };
    }

    private string? ReadCurrentPlace()
    {
        var izakayaData = ReadIzakayaData();
        var name = GetMemberValue(izakayaData, "DaySceneMapName")?.ToString();
        var normalized = NormalizePlace(name);
        if (normalized != null) return normalized;

        return NormalizePlace(GetMemberValue(izakayaData, "DaySceneMapLabel")?.ToString());
    }

    private string? ReadCurrentPlaceLabel()
    {
        var izakayaData = ReadIzakayaData();
        return GetMemberValue(izakayaData, "DaySceneMapLabel")?.ToString();
    }

    private static object? ReadIzakayaData()
    {
        var type = FindType(IzakayaConfigureTypeName);
        if (type == null) return null;

        var configure = GetSingletonInstance(type);
        return GetMemberValue(configure, "IzakayaData");
    }

    private static object? FindGuestsManager()
    {
        var guestsManagerType = FindType(GuestsManagerTypeName);
        if (guestsManagerType == null) return null;
        return GetSingletonInstance(guestsManagerType) ?? FindUnityObject(guestsManagerType);
    }

    private static string ReadManagerStatus()
    {
        var guestsManagerType = FindType(GuestsManagerTypeName);
        if (guestsManagerType == null) return "manager=type-missing";

        var manager = GetSingletonInstance(guestsManagerType) ?? FindUnityObject(guestsManagerType);
        return manager == null ? "manager=missing" : "manager=ok";
    }

    private static string ReadQueueStatus()
    {
        var guestGroupControllerType = FindType(GuestGroupControllerTypeName);
        if (guestGroupControllerType == null) return "queue=type-missing";

        var queued = GetStaticMemberValue(guestGroupControllerType, "QueuedGuestControllers");
        return queued == null ? "queue=missing" : $"queue={CountObjects(queued)}";
    }

    private IEnumerable<NightBusinessOrder> ReadRuntimeCapturedOrders(
        IReadOnlyList<CapturedRuntimeSpecialOrder> capturedOrders,
        IReadOnlyList<NightBusinessGuest> activeGuests)
    {
        var now = DateTime.UtcNow;
        foreach (var captured in capturedOrders)
        {
            var activeGuest = FindActiveGuestForCapturedOrder(captured, activeGuests);
            var guestId = captured.GuestId ?? activeGuest?.GuestId;
            var fallbackGuestName = !string.IsNullOrWhiteSpace(captured.GuestName)
                ? captured.GuestName
                : activeGuest?.GuestName ?? "";
            var foodTag = ResolveCapturedTagText(
                captured.FoodTag,
                captured.HasFoodTagId ? captured.FoodTagId : null,
                useFoodTagMap: true);
            var beverageTag = ResolveCapturedTagText(
                captured.BeverageTag,
                captured.HasBeverageTagId ? captured.BeverageTagId : null,
                useFoodTagMap: false);
            var order = new NightBusinessOrder
            {
                DeskCode = captured.DeskCode,
                GuestId = guestId,
                GuestName = ResolveRareGuestName(guestId, fallbackGuestName),
                FoodTagId = captured.HasFoodTagId ? captured.FoodTagId : ResolveTagId(foodTag, null),
                FoodTag = foodTag,
                BeverageTagId = captured.HasBeverageTagId ? captured.BeverageTagId : ResolveTagId(beverageTag, null),
                BeverageTag = beverageTag,
                Source = string.IsNullOrWhiteSpace(captured.CaptureSource) ? "RuntimeCapture" : $"RuntimeCapture:{captured.CaptureSource}",
            };

            if (ShouldKeepCapturedOrder(order, captured.CapturedAt, activeGuests, now))
            {
                yield return order;
            }
        }
    }

    private static NightBusinessGuest? FindActiveGuestForCapturedOrder(
        CapturedRuntimeSpecialOrder captured,
        IReadOnlyList<NightBusinessGuest> activeGuests)
    {
        foreach (var guest in activeGuests)
        {
            if (captured.GuestId.HasValue && guest.GuestId.HasValue && captured.GuestId.Value == guest.GuestId.Value)
            {
                return guest;
            }

            if (!string.IsNullOrWhiteSpace(captured.GuestName)
                && string.Equals(captured.GuestName, guest.GuestName, StringComparison.Ordinal))
            {
                return guest;
            }
        }

        if (captured.DeskCode < 0) return null;

        var deskGuests = activeGuests
            .Where(guest => guest.DeskCode == captured.DeskCode)
            .GroupBy(guest => $"{guest.GuestId}:{guest.GuestName}", StringComparer.Ordinal)
            .Select(group => group.First())
            .Take(2)
            .ToList();
        return deskGuests.Count == 1 ? deskGuests[0] : null;
    }

    private IEnumerable<NightBusinessOrder> ReadCapturedLogOrders(
        IReadOnlyList<CapturedSpecialOrder> capturedOrders,
        IReadOnlyList<NightBusinessGuest> activeGuests)
    {
        var now = DateTime.UtcNow;
        foreach (var captured in capturedOrders)
        {
            var rareCustomer = _repository.RareCustomers.FirstOrDefault(customer =>
                string.Equals(customer.Name, captured.GuestName, StringComparison.Ordinal));
            var foodTag = CanonicalizeTagText(captured.FoodTag, useFoodTagMap: true);
            var beverageTag = CanonicalizeTagText(captured.BeverageTag, useFoodTagMap: false);
            var order = new NightBusinessOrder
            {
                DeskCode = captured.DeskCode,
                GuestId = rareCustomer?.Id,
                GuestName = rareCustomer?.Name ?? captured.GuestName,
                FoodTagId = ResolveTagId(foodTag, null),
                FoodTag = foodTag,
                BeverageTagId = ResolveTagId(beverageTag, null),
                BeverageTag = beverageTag,
                Source = "OrderLog",
            };

            if (ShouldKeepCapturedOrder(order, captured.CapturedAt, activeGuests, now))
            {
                yield return order;
            }
        }
    }

    private string ResolveRareGuestName(int? guestId, string fallback)
    {
        return guestId.HasValue && _repository.RareCustomersById.TryGetValue(guestId.Value, out var rareCustomer)
            ? rareCustomer.Name
            : fallback;
    }

    private string ResolveCapturedTagText(string tagText, int? tagId, bool useFoodTagMap)
    {
        if (tagId.HasValue && TryResolveTagTextFromMap(tagId.Value, useFoodTagMap, out var mapped)) return mapped;

        var normalized = CanonicalizeTagText(tagText, useFoodTagMap);
        if (!string.IsNullOrWhiteSpace(normalized)) return normalized;
        return tagId.HasValue ? ResolveTagTextFromMap(tagId.Value, useFoodTagMap) : "";
    }

    private static bool ShouldKeepCapturedOrder(
        NightBusinessOrder order,
        DateTime capturedAtUtc,
        IReadOnlyList<NightBusinessGuest> activeGuests,
        DateTime nowUtc)
    {
        if (MatchesActiveGuest(order, activeGuests)) return true;
        return nowUtc - capturedAtUtc <= UnmatchedCapturedOrderGrace;
    }

    private static bool MatchesActiveGuest(NightBusinessOrder order, IReadOnlyList<NightBusinessGuest> activeGuests)
    {
        foreach (var guest in activeGuests)
        {
            if (order.DeskCode >= 0 && guest.DeskCode >= 0 && order.DeskCode != guest.DeskCode) continue;
            if (order.GuestId.HasValue && guest.GuestId.HasValue && order.GuestId.Value == guest.GuestId.Value) return true;
            if (!string.IsNullOrWhiteSpace(order.GuestName)
                && string.Equals(order.GuestName, guest.GuestName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static List<NightBusinessOrder> DeduplicateOrders(IEnumerable<NightBusinessOrder> orders)
    {
        var result = new List<NightBusinessOrder>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var order in orders.OrderByDescending(GetOrderCompletenessScore))
        {
            var key = $"{order.DeskCode}:{order.GuestId}";
            if (!seen.Add(key)) continue;
            result.Add(order);
        }

        return result
            .OrderBy(order => order.DeskCode)
            .ThenBy(order => order.GuestName)
            .ToList();
    }

    private static int GetOrderCompletenessScore(NightBusinessOrder order)
    {
        var score = 0;
        if (!string.IsNullOrWhiteSpace(order.FoodTag)) score += 8;
        if (!string.IsNullOrWhiteSpace(order.BeverageTag)) score += 8;
        if (order.FoodTagId != 0) score += 2;
        if (order.BeverageTagId != 0) score += 2;
        if (string.Equals(order.Source, "OrderLog", StringComparison.Ordinal)) score += 4;
        if (string.Equals(order.Source, "OrderController", StringComparison.Ordinal)) score += 2;
        if (string.Equals(order.Source, "ServePanel", StringComparison.Ordinal)) score += 1;
        return score;
    }

    private static List<NightBusinessGuest> DeduplicateGuests(IEnumerable<NightBusinessGuest> guests)
    {
        var result = new List<NightBusinessGuest>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var guest in guests)
        {
            var key = $"{guest.DeskCode}:{guest.GuestId}:{guest.GuestName}";
            if (!seen.Add(key)) continue;
            result.Add(guest);
        }

        return result
            .OrderBy(guest => guest.DeskCode)
            .ThenBy(guest => guest.GuestName)
            .ToList();
    }

    private static bool IsSpecialOrder(object order)
    {
        var type = GetMemberValue(order, "Type")?.ToString();
        if (string.Equals(type, "Special", StringComparison.OrdinalIgnoreCase)) return true;
        return order.GetType().Name.IndexOf("SpecialOrder", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private bool IsRareGuestController(object? controller)
    {
        if (controller == null) return false;
        if (GetMemberValue(controller, "SpecialGuest") != null) return true;

        var guest = GetMemberValue(controller, "OrderingGuest");
        var guestId = ToNullableInt(GetMemberValue(guest, "Id"));
        return guestId.HasValue && _repository.RareCustomersById.ContainsKey(guestId.Value);
    }

    private string ResolveTagText(object specialGuest, string methodName, int tagId, bool useFoodTagMap)
    {
        if (TryResolveTagTextFromMap(tagId, useFoodTagMap, out var mapped)) return mapped;

        var method = specialGuest.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (method != null)
        {
            try
            {
                var value = method.Invoke(specialGuest, new object[] { tagId })?.ToString();
                var normalized = CanonicalizeTagText(value, useFoodTagMap);
                if (!string.IsNullOrWhiteSpace(normalized)) return normalized;
            }
            catch
            {
                // Fall through to the local map.
            }
        }

        return ResolveTagTextFromMap(tagId, useFoodTagMap);
    }

    private bool TryResolveTagTextFromMap(int tagId, bool useFoodTagMap, out string tagText)
    {
        var key = tagId.ToString();
        if (useFoodTagMap && _repository.FoodTagIdMap.TryGetValue(key, out var mapped))
        {
            tagText = FoodTags.NormalizeName(mapped) ?? mapped;
            return true;
        }

        tagText = "";
        return false;
    }

    private string ResolveTagTextFromMap(int tagId, bool useFoodTagMap)
    {
        if (TryResolveTagTextFromMap(tagId, useFoodTagMap, out var mapped)) return mapped;

        if (tagId == 0) return "";
        return $"#{tagId}";
    }

    private string ResolveOrderTagText(
        object order,
        object? controller,
        object specialGuest,
        string controllerMethodName,
        string guestMethodName,
        string orderPropertyName,
        string orderTextLabel,
        bool useFoodTagMap)
    {
        var tagValue = GetMemberValue(order, orderPropertyName);
        var tagId = ToNullableInt(tagValue);
        if (tagId.HasValue && TryResolveTagTextFromMap(tagId.Value, useFoodTagMap, out var mapped)) return mapped;

        var normalized = CanonicalizeTagText(ResolveOrderTagFromText(SafeToString(order), orderTextLabel), useFoodTagMap);
        if (!string.IsNullOrWhiteSpace(normalized)) return normalized;

        var controllerValue = InvokeInstanceMethod(controller, controllerMethodName, order)?.ToString();
        normalized = CanonicalizeTagText(controllerValue, useFoodTagMap);
        if (!string.IsNullOrWhiteSpace(normalized)) return normalized;

        return tagId.HasValue ? ResolveTagText(specialGuest, guestMethodName, tagId.Value, useFoodTagMap) : "";
    }

    private static string ResolveOrderTagFromText(string? orderText, string label)
    {
        if (string.IsNullOrWhiteSpace(orderText)) return "";

        var lines = orderText.Replace('\r', '\n').Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (!line.StartsWith(label, StringComparison.OrdinalIgnoreCase)) continue;

            var sameLineValue = NormalizeLabelValue(line[label.Length..]);
            if (!string.IsNullOrWhiteSpace(sameLineValue)) return sameLineValue;

            for (var j = i + 1; j < lines.Length; j++)
            {
                var candidateLine = lines[j].Trim();
                if (IsOrderTextFieldLine(candidateLine)) break;

                var candidate = NormalizeTagText(candidateLine);
                if (!string.IsNullOrWhiteSpace(candidate)) return candidate;
            }
        }

        return "";
    }

    private static string? SafeToString(object? value)
    {
        if (value == null) return null;

        try
        {
            return value.ToString();
        }
        catch
        {
            return null;
        }
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

    private static string? NormalizeLabelValue(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith(":", StringComparison.Ordinal)) trimmed = trimmed[1..].Trim();
        return NormalizeTagText(trimmed);
    }

    private static string? NormalizeTagText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var trimmed = value.Trim();
        if (trimmed.Length == 0) return null;
        if (string.Equals(trimmed, "Null", StringComparison.OrdinalIgnoreCase)) return null;
        if (string.Equals(trimmed, "None", StringComparison.OrdinalIgnoreCase)) return null;
        if (trimmed.StartsWith("#", StringComparison.Ordinal)) return null;

        var normalized = FoodTags.NormalizeName(trimmed);
        return string.IsNullOrWhiteSpace(normalized) ? trimmed : normalized;
    }

    private string CanonicalizeTagText(string? value, bool useFoodTagMap)
    {
        var normalized = NormalizeTagText(value);
        if (string.IsNullOrWhiteSpace(normalized)) return "";

        var candidates = GetKnownTagCandidates(useFoodTagMap);
        foreach (var candidate in candidates)
        {
            if (string.Equals(normalized, candidate, StringComparison.Ordinal)) return candidate;
        }

        foreach (var candidate in candidates)
        {
            if (normalized.Contains(candidate, StringComparison.Ordinal)) return candidate;
        }

        return normalized;
    }

    private IReadOnlyList<string> GetKnownTagCandidates(bool useFoodTagMap)
    {
        if (useFoodTagMap)
        {
            _foodTagCandidates ??= BuildKnownTagCandidates(useFoodTagMap: true);
            return _foodTagCandidates;
        }

        _beverageTagCandidates ??= BuildKnownTagCandidates(useFoodTagMap: false);
        return _beverageTagCandidates;
    }

    private IReadOnlyList<string> BuildKnownTagCandidates(bool useFoodTagMap)
    {
        var values = new List<string>();

        void Add(string? value)
        {
            var normalized = NormalizeTagText(value);
            if (!string.IsNullOrWhiteSpace(normalized)) values.Add(normalized);
        }

        if (useFoodTagMap)
        {
            foreach (var tag in FoodTags.All) Add(tag);
            foreach (var tag in _repository.FoodTagIdMap.Values) Add(tag);
            foreach (var recipe in _repository.Recipes)
            {
                foreach (var tag in recipe.PositiveTags) Add(tag);
                foreach (var tag in recipe.NegativeTags) Add(tag);
            }

            foreach (var ingredient in _repository.Ingredients)
            {
                foreach (var tag in ingredient.Tags) Add(tag);
            }

            foreach (var customer in _repository.NormalCustomers)
            {
                foreach (var tag in customer.PositiveTags) Add(tag);
            }

            foreach (var customer in _repository.RareCustomers)
            {
                foreach (var tag in customer.PositiveTags) Add(tag);
                foreach (var tag in customer.NegativeTags) Add(tag);
            }
        }
        else
        {
            foreach (var beverage in _repository.Beverages)
            {
                foreach (var tag in beverage.Tags) Add(tag);
            }

            foreach (var customer in _repository.NormalCustomers)
            {
                foreach (var tag in customer.BeverageTags) Add(tag);
            }

            foreach (var customer in _repository.RareCustomers)
            {
                foreach (var tag in customer.BeverageTags) Add(tag);
            }
        }

        return values
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(value => value.Length)
            .ThenBy(value => value, StringComparer.Ordinal)
            .ToList();
    }

    private int ResolveTagId(string tagText, object? fallbackValue)
    {
        if (!string.IsNullOrWhiteSpace(tagText))
        {
            foreach (var item in _repository.FoodTagIdMap)
            {
                var mapped = FoodTags.NormalizeName(item.Value) ?? item.Value;
                if (string.Equals(mapped, tagText, StringComparison.Ordinal) && int.TryParse(item.Key, out var parsed))
                {
                    return parsed;
                }
            }
        }

        return ToInt(fallbackValue);
    }

    private static string ReadGuestName(object specialGuest, int? guestId)
    {
        var stringId = GetMemberValue(specialGuest, "StringId")?.ToString();
        if (!string.IsNullOrWhiteSpace(stringId)) return stringId;
        return guestId.HasValue ? $"Guest {guestId.Value}" : "Special guest";
    }

    private static string? NormalizePlace(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        foreach (var place in PlaceNames.All)
        {
            if (string.Equals(value, place, StringComparison.OrdinalIgnoreCase)) return place;
            if (value.IndexOf(place, StringComparison.OrdinalIgnoreCase) >= 0) return place;
        }

        return null;
    }

    private static object? FindUnityObject(Type type)
    {
        var method = typeof(UnityEngine.Object).GetMethod("FindObjectOfType", new[] { typeof(Type) });
        if (method == null) return null;

        try
        {
            return method.Invoke(null, new object[] { type });
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<object?> FindUnityObjects(Type type)
    {
        var method = typeof(UnityEngine.Object).GetMethod("FindObjectsOfType", new[] { typeof(Type) });
        if (method == null) yield break;

        object? objects = null;
        try
        {
            objects = method.Invoke(null, new object[] { type });
        }
        catch
        {
            yield break;
        }

        foreach (var item in EnumerateObjects(objects))
        {
            yield return item;
        }
    }

    private static object? GetSingletonInstance(Type type)
    {
        foreach (var name in new[] { "Instance", "UniqueInstance", "instance", "m_Instance", "m_instance", "s_Instance", "m_UniqueInstance" })
        {
            var current = type;
            while (current != null)
            {
                var property = FindProperty(current, name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                if (property != null)
                {
                    try
                    {
                        var value = property.GetValue(null);
                        if (value != null) return value;
                    }
                    catch
                    {
                        // Try the next known singleton name.
                    }
                }

                var field = current.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                if (field != null)
                {
                    try
                    {
                        var value = field.GetValue(null);
                        if (value != null) return value;
                    }
                    catch
                    {
                        // Try the next known singleton name.
                    }
                }

                current = current.BaseType;
            }
        }

        return null;
    }

    private static object? InvokeStaticMethod(Type type, string name)
    {
        var method = FindMethod(type, name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        if (method == null) return null;

        try
        {
            return method.Invoke(null, Array.Empty<object?>());
        }
        catch
        {
            return null;
        }
    }

    private static object? InvokeInstanceMethod(object? instance, string name, params object?[] args)
    {
        if (instance == null) return null;

        var method = FindMethod(instance.GetType(), name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, args.Length);
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

    private static object? GetMemberValue(object? instance, string name)
    {
        if (instance == null) return null;
        var type = instance.GetType();

        while (type != null)
        {
            if (TryReadKnownField(instance, type, name, out var knownFieldValue)) return knownFieldValue;

            var property = FindProperty(type, name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (TryReadProperty(instance, property, out var propertyValue)) return propertyValue;

            var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (TryReadField(instance, field, out var fieldValue) && fieldValue != null) return fieldValue;

            var pascalName = char.ToUpperInvariant(name[0]) + name[1..];
            if (TryReadKnownField(instance, type, pascalName, out knownFieldValue)) return knownFieldValue;

            property = FindProperty(type, pascalName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (TryReadProperty(instance, property, out propertyValue)) return propertyValue;

            field = type.GetField(pascalName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (TryReadField(instance, field, out fieldValue) && fieldValue != null) return fieldValue;

            type = type.BaseType;
        }

        return null;
    }

    private static object? GetStaticMemberValue(Type type, string name)
    {
        var current = type;
        while (current != null)
        {
            if (TryReadKnownStaticField(current, name, out var knownFieldValue)) return knownFieldValue;

            var property = FindProperty(current, name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            if (TryReadProperty(null, property, out var propertyValue)) return propertyValue;

            var field = current.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            if (TryReadField(null, field, out var fieldValue) && fieldValue != null) return fieldValue;

            var pascalName = char.ToUpperInvariant(name[0]) + name[1..];
            if (TryReadKnownStaticField(current, pascalName, out knownFieldValue)) return knownFieldValue;

            current = current.BaseType;
        }

        return null;
    }

    private static bool TryReadKnownField(object instance, Type type, string name, out object? value)
    {
        value = null;
        foreach (var fieldName in BuildFieldNameCandidates(name))
        {
            var field = type.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (TryReadField(instance, field, out value) && value != null) return true;
        }

        return false;
    }

    private static bool TryReadKnownStaticField(Type type, string name, out object? value)
    {
        value = null;
        foreach (var fieldName in BuildFieldNameCandidates(name))
        {
            var field = type.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            if (TryReadField(null, field, out value) && value != null) return true;
        }

        return false;
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

    private static PropertyInfo? FindProperty(Type type, string name, BindingFlags flags)
    {
        try
        {
            return type.GetProperty(name, flags);
        }
        catch (AmbiguousMatchException)
        {
            return type.GetProperties(flags).FirstOrDefault(property => property.Name == name);
        }
    }

    private static MethodInfo? FindMethod(Type type, string name, BindingFlags flags)
    {
        try
        {
            return type.GetMethod(name, flags);
        }
        catch (AmbiguousMatchException)
        {
            return type.GetMethods(flags).FirstOrDefault(method => method.Name == name);
        }
    }

    private static MethodInfo? FindMethod(Type type, string name, BindingFlags flags, int parameterCount)
    {
        return type
            .GetMethods(flags)
            .FirstOrDefault(method => method.Name == name && method.GetParameters().Length == parameterCount);
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

    private static IEnumerable<object?> EnumerateObjects(object? value)
    {
        if (value == null) yield break;

        if (value is IEnumerable enumerable && value is not string)
        {
            foreach (var item in enumerable)
            {
                yield return item;
            }

            yield break;
        }

        var values = GetMemberValue(value, "Values");
        if (values != null && !ReferenceEquals(values, value))
        {
            foreach (var item in EnumerateObjects(values))
            {
                yield return item;
            }

            yield break;
        }

        var enumerated = false;
        foreach (var item in EnumerateObjectsByReflection(value))
        {
            enumerated = true;
            yield return item;
        }

        if (enumerated) yield break;

        var count = ToInt(GetMemberValue(value, "Count") ?? GetMemberValue(value, "Length"));
        if (count <= 0) yield break;

        var indexer = value.GetType().GetProperty("Item", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (indexer == null) yield break;

        for (var i = 0; i < count; i++)
        {
            object? item = null;
            try
            {
                item = indexer.GetValue(value, new object[] { i });
            }
            catch
            {
                // Stop trying this collection if its indexer is incompatible.
                yield break;
            }

            yield return item;
        }
    }

    private static IEnumerable<object?> EnumerateObjectsByReflection(object value)
    {
        object? enumerator = null;
        try
        {
            var method = FindMethod(value.GetType(), "GetEnumerator", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            enumerator = method?.Invoke(value, Array.Empty<object?>());
        }
        catch
        {
            yield break;
        }

        if (enumerator == null) yield break;

        var enumeratorType = enumerator.GetType();
        var moveNext = FindMethod(enumeratorType, "MoveNext", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (moveNext == null) yield break;

        while (true)
        {
            object? moved;
            try
            {
                moved = moveNext.Invoke(enumerator, Array.Empty<object?>());
            }
            catch
            {
                yield break;
            }

            if (moved is not bool isMoved || !isMoved) yield break;
            yield return GetMemberValue(enumerator, "Current");
        }
    }

    private static object? NormalizeController(object? value)
    {
        if (value == null) return null;

        var fromKeyValue = GetMemberValue(value, "Value")
            ?? GetMemberValue(value, "value")
            ?? GetMemberValue(value, "m_Value")
            ?? GetMemberValue(value, "Item2");
        return fromKeyValue ?? value;
    }

    private static int CountObjects(object? value)
    {
        if (value == null) return 0;
        var count = ToInt(GetMemberValue(value, "Count") ?? GetMemberValue(value, "Length"));
        if (count > 0) return count;

        var total = 0;
        foreach (var _ in EnumerateObjects(value))
        {
            total++;
        }

        return total;
    }

    private static int? ToNullableInt(object? value)
    {
        if (value == null) return null;
        if (value is int intValue) return intValue;
        if (value is long longValue) return (int)longValue;
        if (value is short shortValue) return shortValue;
        return int.TryParse(value.ToString(), out var parsed) ? parsed : null;
    }

    private static int ToInt(object? value)
    {
        if (value == null) return 0;
        if (value is int intValue) return intValue;
        if (value is long longValue) return (int)longValue;
        if (value is short shortValue) return shortValue;
        if (int.TryParse(value.ToString(), out var parsed)) return parsed;
        return 0;
    }
}
