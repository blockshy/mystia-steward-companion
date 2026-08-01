using System.Reflection;
using MystiaStewardCompanion.Core;
using UnityEngine;
using static MystiaStewardCompanion.Save.RuntimeReflectionUtility;

namespace MystiaStewardCompanion.Save;

internal sealed class NightBusinessReflectionProvider
{
    private static readonly TimeSpan RuntimeCapturedOrderMaxAge = TimeSpan.FromHours(6);

    private const string GuestGroupControllerTypeName = "NightScene.GuestManagementUtility.GuestGroupController";
    private const string GuestsManagerTypeName = "NightScene.GuestManagementUtility.GuestsManager";
    private const string OrderControllerTypeName = "Night.UI.HUD.Ordering.OrderController";
    private const string OrderingElementTypeName = "NightScene.UI.GuestManagementUtility.OrderingElement";
    private const string WorkSceneServePannelTypeName = "NightScene.UI.GuestManagementUtility.WorkSceneServePannel";
    private const string IzakayaConfigureTypeName = "GameData.RunTime.NightSceneUtility.IzakayaConfigure";
    private const int MaxCandidateDiagnostics = 80;
    private static readonly (string MemberName, string Source)[] ManagerControllerSources =
    {
        ("AllPresentedGuestGroupController", "Presented"),
        ("AllGuestInDeskController", "Desk"),
        ("AllGuestsControllersInDesk", "DeskMap"),
        ("CanPlayerRepellGuest", "Repellable"),
        ("ManualDesksDic", "ManualDesk"),
    };

    private readonly DataRepository _repository;
    private readonly RuntimeMappedGuestCatalogSnapshot? _mappedGuestSnapshot;
    private readonly RuntimeStaticDataSnapshot? _staticDataSnapshot;
    private readonly bool _diagnosticsEnabled;
    private readonly string _sceneName;
    private IReadOnlyList<string>? _foodTagCandidates;
    private IReadOnlyList<string>? _beverageTagCandidates;
    private List<NightBusinessCandidateDiagnostic>? _candidateDiagnostics;
    private readonly Dictionary<string, double> _performanceMs = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, double> PerformanceMs => _performanceMs;

    public NightBusinessReflectionProvider(
        DataRepository repository,
        bool diagnosticsEnabled = false,
        string sceneName = "",
        RuntimeMappedGuestCatalogSnapshot? mappedGuestSnapshot = null,
        RuntimeStaticDataSnapshot? staticDataSnapshot = null)
    {
        _repository = repository;
        _mappedGuestSnapshot = mappedGuestSnapshot;
        _staticDataSnapshot = staticDataSnapshot;
        _diagnosticsEnabled = diagnosticsEnabled;
        _sceneName = sceneName;
    }

    public NightBusinessContext LoadContext()
    {
        _performanceMs.Clear();
        var guests = new List<NightBusinessGuest>();
        var orders = new List<NightBusinessOrder>();
        var reflectionOrders = new List<NightBusinessOrder>();
        var errors = new List<string>();
        var sourceStats = new List<string>();
        _candidateDiagnostics = _diagnosticsEnabled ? new List<NightBusinessCandidateDiagnostic>() : null;
        sourceStats.Add(_mappedGuestSnapshot == null
            ? "MappedGuests=cached-missing"
            : $"MappedGuests={_mappedGuestSnapshot.ResolvedCount}/{_mappedGuestSnapshot.Entries.Count}; {_mappedGuestSnapshot.Status}");
        sourceStats.Add(_staticDataSnapshot == null
            ? "StaticData=cached-missing"
            : $"StaticData={_staticDataSnapshot.Status}");
        if (_mappedGuestSnapshot != null && _staticDataSnapshot != null)
        {
            Measure("diagnostics.staticData", () => WriteRuntimeStaticDataDiagnostics(_mappedGuestSnapshot, _staticDataSnapshot));
        }

        IReadOnlyList<CapturedRuntimeSpecialOrder> runtimeOrders = Array.Empty<CapturedRuntimeSpecialOrder>();
        try
        {
            runtimeOrders = Measure("runtimeCapture.snapshot", () => SpecialOrderRuntimeCapture.Snapshot(RuntimeCapturedOrderMaxAge));
            sourceStats.Add($"RuntimeCaptureCache={runtimeOrders.Count}");
        }
        catch (Exception ex)
        {
            sourceStats.Add("RuntimeCapture=err");
            errors.Add($"runtime capture: {ex.Message}");
        }

        var runtimeCaptureReady = SpecialOrderRuntimeCapture.IsBusinessReady;
        sourceStats.Add(runtimeCaptureReady ? "OrderReadMode=RuntimeCapture" : "OrderReadMode=Unavailable");

        try
        {
            var servePanelContexts = Measure("rare.servePanel.contexts", () => ReadServePanelContexts().ToList());
            sourceStats.Add($"ServePanel={servePanelContexts.Count}");
            guests.AddRange(Measure("rare.servePanel.guests", () => ReadServePanelRareGuests(servePanelContexts).ToList()));
            if (_diagnosticsEnabled)
            {
                reflectionOrders.AddRange(Measure("rare.servePanel.orders", () => ReadServePanelOrders(servePanelContexts).ToList()));
            }
        }
        catch (Exception ex)
        {
            sourceStats.Add("ServePanel=err");
            errors.Add($"serve panel: {ex.Message}");
        }

        if (_diagnosticsEnabled)
        {
            try
            {
                var orderControllerOrders = Measure("rare.orderController", () => ReadOrderControllerOrders().ToList());
                sourceStats.Add($"OrderController={orderControllerOrders.Count}");
                reflectionOrders.AddRange(orderControllerOrders);
            }
            catch (Exception ex)
            {
                sourceStats.Add("OrderController=err");
                errors.Add($"order controller: {ex.Message}");
            }

            try
            {
                var hudOrders = Measure("rare.hud", () => ReadHudOrders().ToList());
                sourceStats.Add($"HUD={hudOrders.Count}");
                reflectionOrders.AddRange(hudOrders);
            }
            catch (Exception ex)
            {
                sourceStats.Add("HUD=err");
                errors.Add($"HUD orders: {ex.Message}");
            }
        }

        var managerStatus = Measure("manager.status", ReadManagerStatus);
        var queueStatus = Measure("queue.status", ReadQueueStatus);

        foreach (var source in ManagerControllerSources)
        {
            try
            {
                var controllers = Measure($"controllers.{source.Source}", () => ReadManagerControllers(source.MemberName).ToList());
                sourceStats.Add($"{source.Source}={controllers.Count}");
                guests.AddRange(Measure($"rare.guests.{source.Source}", () => ReadRareGuests(controllers, source.Source).ToList()));
            }
            catch (Exception ex)
            {
                sourceStats.Add($"{source.Source}=err");
                errors.Add($"{source.Source}: {ex.Message}");
            }
        }

        try
        {
            var queuedControllers = Measure("controllers.Queue", () => ReadQueuedControllers().ToList());
            sourceStats.Add($"Queue={queuedControllers.Count}");
            guests.AddRange(Measure("rare.guests.Queue", () => ReadRareGuests(queuedControllers, "Queue").ToList()));
        }
        catch (Exception ex)
        {
            sourceStats.Add("Queue=err");
            errors.Add($"Queue: {ex.Message}");
        }

        var activeGuests = Measure("deduplicate.guests", () => DeduplicateGuests(guests));
        var rawLiveOrders = reflectionOrders.ToList();
        var acceptedRuntimeOrders = new List<NightBusinessOrder>();
        if (runtimeCaptureReady)
        {
            acceptedRuntimeOrders = Measure(
                "runtimeCapture.accept",
                () => ReadRuntimeCapturedOrders(runtimeOrders, activeGuests).ToList());
            sourceStats.Add($"RuntimeCapture={acceptedRuntimeOrders.Count}/{runtimeOrders.Count}");
            sourceStats.Add($"RuntimeBinding=confirmed:{acceptedRuntimeOrders.Count},invalid:{runtimeOrders.Count - acceptedRuntimeOrders.Count}");
            sourceStats.Add($"RuntimeCaptureStatus={SpecialOrderRuntimeCapture.Status}");
            sourceStats.Add($"UiPinning={RuntimeUiPinningService.Status}");
            orders.AddRange(acceptedRuntimeOrders);
            sourceStats.Add("ReflectionFallback=disabled");
        }
        else
        {
            sourceStats.Add($"RuntimeBinding=not-ready; ignored={runtimeOrders.Count}");
            sourceStats.Add($"RuntimeCaptureStatus={SpecialOrderRuntimeCapture.Status}");
            sourceStats.Add($"UiPinning={RuntimeUiPinningService.Status}");
            sourceStats.Add("ReflectionFallback=disabled");
            errors.Add("稀客订单生命周期 Hook 尚未完整就绪，本轮读取已停止。");
        }

        var activeOrders = Measure("deduplicate.orders", () => DeduplicateOrders(orders));
        var placeIdentity = Measure("place.identity", ReadCurrentPlaceIdentity);
        var place = placeIdentity.Place;
        var placeLabel = placeIdentity.Label;
        var recentRuntimeParseFailures = !_diagnosticsEnabled
            ? Array.Empty<string>()
            : Measure("runtimeCapture.failures", () => SpecialOrderRuntimeCapture.RecentParseFailuresSnapshot(TimeSpan.FromMinutes(5)));
        Measure("diagnostics.nightBusiness", () => WriteDiagnostics(
            managerStatus,
            queueStatus,
            sourceStats,
            errors,
            guests,
            rawLiveOrders,
            acceptedRuntimeOrders,
            activeGuests,
            activeOrders,
            _candidateDiagnostics ?? new List<NightBusinessCandidateDiagnostic>(),
            recentRuntimeParseFailures,
            place,
            placeLabel));
        _candidateDiagnostics = null;

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

    private void Measure(string key, Action action)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            action();
        }
        finally
        {
            _performanceMs[key] = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 2);
        }
    }

    private void WriteRuntimeStaticDataDiagnostics(
        RuntimeMappedGuestCatalogSnapshot mappedGuestSnapshot,
        RuntimeStaticDataSnapshot staticDataSnapshot)
    {
        if (!_diagnosticsEnabled) return;

        try
        {
            var mappedSection = RuntimeStaticDataDiagnosticFormatter.FormatMappedSpecialGuests(mappedGuestSnapshot);
            if (mappedSection != null)
            {
                AggregateModLogService.AppendSection(mappedSection.Channel, mappedSection.Title, mappedSection.Content);
            }

            foreach (var section in RuntimeStaticDataDiagnosticFormatter.FormatStaticData(staticDataSnapshot))
            {
                AggregateModLogService.AppendSection(section.Channel, section.Title, section.Content);
            }
        }
        catch
        {
            // Static-data diagnostics must never affect runtime reads.
        }
    }

    private void WriteDiagnostics(
        string managerStatus,
        string queueStatus,
        IReadOnlyList<string> sourceStats,
        IReadOnlyList<string> errors,
        IReadOnlyList<NightBusinessGuest> rawGuests,
        IReadOnlyList<NightBusinessOrder> rawLiveOrders,
        IReadOnlyList<NightBusinessOrder> acceptedRuntimeOrders,
        IReadOnlyList<NightBusinessGuest> activeGuests,
        IReadOnlyList<NightBusinessOrder> finalOrders,
        IReadOnlyList<NightBusinessCandidateDiagnostic> candidates,
        IReadOnlyList<string> recentRuntimeParseFailures,
        string? place,
        string? placeLabel)
    {
        if (!_diagnosticsEnabled) return;

        try
        {
            var snapshot = new NightBusinessDiagnosticSnapshot
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
                ActiveGuests = activeGuests.ToList(),
                FinalOrders = finalOrders.ToList(),
                Candidates = candidates.ToList(),
                RecentRuntimeParseFailures = recentRuntimeParseFailures.ToList(),
            };
            AggregateModLogService.AppendSection(
                "night-business",
                "Night Business Diagnostic",
                NightBusinessDiagnosticFormatter.Format(snapshot));
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
            var controller = NormalizeKeyValueValue(item);
            if (controller != null) yield return controller;
        }
    }

    private IEnumerable<object?> ReadQueuedControllers()
    {
        var guestGroupControllerType = FindType(GuestGroupControllerTypeName);
        if (guestGroupControllerType == null) yield break;

        foreach (var item in EnumerateObjects(GetStaticMemberValue(guestGroupControllerType, "QueuedGuestControllers")))
        {
            var controller = NormalizeKeyValueValue(item);
            if (controller != null) yield return controller;
        }
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
        if (controller == null)
        {
            RecordCandidate("GuestController", source, accepted: false, "controller is null", null);
            return null;
        }

        var specialGuest = GetMemberValue(controller, "SpecialGuest");
        var orderingGuest = GetMemberValue(controller, "OrderingGuest");
        var guest = specialGuest ?? orderingGuest;
        if (guest == null)
        {
            RecordCandidate("GuestController", source, accepted: false, "SpecialGuest and OrderingGuest are null", () => DescribeControllerCandidate(controller));
            return null;
        }

        var guestId = ReadGuestId(guest);
        var identity = specialGuest != null
            ? ResolveRareCustomerIdentity(guest)
            : ResolveOrderingGuestRareCustomerIdentity(orderingGuest, AllowOrderingGuestIdResolution(source));
        if (specialGuest == null && identity == null && !IsSpecialGuestObject(orderingGuest))
        {
            RecordCandidate("GuestController", source, accepted: false, "OrderingGuest is not an explicit rare guest", () => DescribeControllerCandidate(controller));
            return null;
        }

        var result = new NightBusinessGuest
        {
            DeskCode = ToInt(GetMemberValue(controller, "DeskCode")),
            GuestId = identity?.Id ?? guestId,
            GuestName = identity?.Name ?? ReadGuestName(guest, guestId),
            Source = source,
            Fund = ReadNullableIntMember(controller, "GetFund"),
            BaseFundCarry = ReadNullableIntMember(controller, "BaseFundCarry"),
            MaxFundCarry = ReadNullableIntMember(controller, "MaxFundCarry"),
            ExtraFundByBuff = ReadNullableIntMember(controller, "ExtraFundByBuff"),
            WillPayMoney = ReadNullableBoolMember(controller, "WillPayMoney"),
        };
        RecordCandidate("GuestController", source, accepted: true, "accepted rare guest", () => DescribeControllerCandidate(controller));
        return result;
    }

    private NightBusinessGuest? ReadRareGuestFromOrder(object? order, object? controller, string source)
    {
        if (order == null) return null;

        var resolution = RuntimeOrderTypeResolver.Resolve(order);
        if (!resolution.Resolved
            || resolution.Kind != RuntimeOrderKind.Special
            || resolution.ReadableOrder == null)
        {
            return null;
        }

        var readableOrder = resolution.ReadableOrder;
        var specialGuest = GetMemberValue(readableOrder, "SpecialGuests") ?? GetMemberValue(controller, "SpecialGuest");
        var orderingGuest = GetMemberValue(controller, "OrderingGuest");
        if (specialGuest == null
            && (IsSpecialGuestObject(orderingGuest)
                || ResolveOrderingGuestRareCustomerIdentity(orderingGuest, allowIdOnly: true) != null))
        {
            specialGuest = orderingGuest;
        }

        if (specialGuest == null)
        {
            RecordCandidate("GuestFromOrder", source, accepted: false, "SpecialGuests missing on order and controller", () => DescribeOrderCandidate(order, controller));
            return null;
        }

        var guestId = ReadGuestId(specialGuest);
        var identity = ResolveRareCustomerIdentity(specialGuest);
        var result = new NightBusinessGuest
        {
            DeskCode = ToInt(GetMemberValue(readableOrder, "DeskCode") ?? GetMemberValue(controller, "DeskCode")),
            GuestId = identity?.Id ?? guestId,
            GuestName = identity?.Name ?? ReadGuestName(specialGuest, guestId),
            Source = source,
            Fund = ReadNullableIntMember(controller, "GetFund"),
            BaseFundCarry = ReadNullableIntMember(controller, "BaseFundCarry"),
            MaxFundCarry = ReadNullableIntMember(controller, "MaxFundCarry"),
            ExtraFundByBuff = ReadNullableIntMember(controller, "ExtraFundByBuff"),
            WillPayMoney = ReadNullableBoolMember(controller, "WillPayMoney"),
        };
        RecordCandidate("GuestFromOrder", source, accepted: true, "accepted rare guest from order", () => DescribeOrderCandidate(order, controller));
        return result;
    }

    private NightBusinessOrder? ReadOrder(object? order, object? controller, string source)
    {
        if (order == null)
        {
            RecordCandidate("Order", source, accepted: false, "order is null", () => DescribeControllerCandidate(controller));
            return null;
        }

        var resolution = RuntimeOrderTypeResolver.Resolve(order);
        if (!resolution.Resolved)
        {
            RecordCandidate("Order", source, accepted: false, resolution.Reason, () => DescribeOrderCandidate(order, controller));
            return null;
        }

        if (resolution.Kind != RuntimeOrderKind.Special || resolution.ReadableOrder == null)
        {
            RecordCandidate("Order", source, accepted: false, "exact concrete type is not SpecialOrder", () => DescribeOrderCandidate(order, controller));
            return null;
        }

        var readableOrder = resolution.ReadableOrder;
        var classification = SpecialBusinessOrderClassifier.Classify(readableOrder, controller, source);
        var classifiedSpecialBusinessOrder = !string.IsNullOrWhiteSpace(classification.Role)
            && !string.Equals(classification.Role, SpecialBusinessOrderRoles.WackyTarget, StringComparison.Ordinal);

        var now = DateTime.UtcNow;

        var specialGuest = GetMemberValue(readableOrder, "SpecialGuests")
            ?? GetMemberValue(controller, "SpecialGuest");
        var orderingGuest = GetMemberValue(controller, "OrderingGuest");
        if (specialGuest == null && classifiedSpecialBusinessOrder && orderingGuest != null)
        {
            specialGuest = orderingGuest;
        }

        if (specialGuest == null
            && (IsSpecialGuestObject(orderingGuest)
                || ResolveOrderingGuestRareCustomerIdentity(orderingGuest, allowIdOnly: true) != null))
        {
            specialGuest = orderingGuest;
        }

        if (specialGuest == null)
        {
            RecordCandidate("Order", source, accepted: false, "SpecialGuests/SpecialGuest/OrderingGuest missing", () => DescribeOrderCandidate(readableOrder, controller));
            return null;
        }

        var guestId = ReadGuestId(specialGuest);
        var identity = IsSpecialGuestObject(specialGuest)
            ? ResolveRareCustomerIdentity(specialGuest)
            : ResolveOrderingGuestRareCustomerIdentity(specialGuest, classifiedSpecialBusinessOrder);
        var foodTag = ResolveOrderTagText(readableOrder, controller, specialGuest, "GetOrderFoodText", "GetFoodTagText", "RequestFoodTag", useFoodTagMap: true);
        var beverageTag = ResolveOrderTagText(readableOrder, controller, specialGuest, "GetOrderBevText", "GetBevTagText", "RequestBeverageTag", useFoodTagMap: false);
        var foodTagId = ToNullableInt(GetMemberValue(readableOrder, "RequestFoodTag"));
        var beverageTagId = ToNullableInt(GetMemberValue(readableOrder, "RequestBeverageTag"));
        if (!foodTagId.HasValue && !beverageTagId.HasValue && string.IsNullOrWhiteSpace(foodTag) && string.IsNullOrWhiteSpace(beverageTag))
        {
            RecordCandidate("Order", source, accepted: false, "empty food and beverage tag", () => DescribeOrderCandidate(readableOrder, controller));
            return null;
        }

        var deskCode = ToNullableInt(GetMemberValue(readableOrder, "DeskCode"))
            ?? ToNullableInt(GetMemberValue(controller, "DeskCode"))
            ?? -1;
        var result = new NightBusinessOrder
        {
            DeskCode = deskCode,
            GuestId = identity?.Id ?? guestId,
            RuntimeGuestId = guestId,
            GuestName = identity?.Name ?? ReadGuestName(specialGuest, guestId),
            SpecialBusinessRole = classification.Role,
            SpecialBusinessRoleLabel = classification.RoleLabel,
            AutomationAllowed = classification.AutomationAllowed,
            AutomationBlockReason = classification.AutomationBlockReason,
            FoodTagId = foodTagId,
            FoodTag = foodTag,
            BeverageTagId = beverageTagId,
            BeverageTag = beverageTag,
            Source = source,
            FirstSeenAtUtc = now,
            LastSeenAtUtc = now,
            IsFreeOrder = ReadOrderFreeState(readableOrder),
            Fund = ReadNullableIntMember(controller, "GetFund"),
            BaseFundCarry = ReadNullableIntMember(controller, "BaseFundCarry"),
            MaxFundCarry = ReadNullableIntMember(controller, "MaxFundCarry"),
            ExtraFundByBuff = ReadNullableIntMember(controller, "ExtraFundByBuff"),
            WillPayMoney = ReadNullableBoolMember(controller, "WillPayMoney"),
            RemainingOrderCount = ReadNullableIntMember(controller, "RemainOrderCount"),
            HasServedFood = ReadOrderServedState(readableOrder, "ServFood", "ServedFoodInAir"),
            HasServedBeverage = ReadOrderServedState(readableOrder, "ServBeverage", "ServedBeverageInAir"),
        };
        RecordCandidate("Order", source, accepted: true, "accepted special order", () => DescribeOrderCandidate(readableOrder, controller));
        return result;
    }

    private CurrentPlaceIdentity ReadCurrentPlaceIdentity()
    {
        var izakayaData = ReadIzakayaData();
        var label = GetMemberValue(izakayaData, "DaySceneMapLabel")?.ToString();
        if (string.IsNullOrWhiteSpace(label)) return new CurrentPlaceIdentity(null, label);

        var name = GetMemberValue(izakayaData, "DaySceneMapName")?.ToString();
        return new CurrentPlaceIdentity(
            NormalizePlace(name) ?? NormalizePlace(label),
            label);
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

    private readonly record struct CurrentPlaceIdentity(string? Place, string? Label);

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
        foreach (var captured in capturedOrders)
        {
            if (!HasCapturedOrderDetails(captured)
                || captured.OrderObject == null
                || captured.ControllerObject == null
                || string.IsNullOrWhiteSpace(captured.RuntimeKey))
            {
                continue;
            }

            var activeGuest = FindActiveGuestForCapturedOrder(captured, activeGuests);
            var identity = ResolveRareCustomerIdentity(captured.GuestId, captured.GuestName);
            var classification = SpecialBusinessOrderClassifier.Classify(captured.OrderObject, captured.ControllerObject);
            var guestId = identity?.Id ?? captured.GuestId ?? activeGuest?.GuestId;
            var fallbackGuestName = !string.IsNullOrWhiteSpace(captured.GuestName)
                ? captured.GuestName
                : activeGuest?.GuestName ?? "";
            var foodTag = ResolveCapturedTagText(
                captured.FoodTagDisplayText,
                captured.HasFoodTagId ? captured.FoodTagId : null,
                useFoodTagMap: true);
            var beverageTag = ResolveCapturedTagText(
                captured.BeverageTagDisplayText,
                captured.HasBeverageTagId ? captured.BeverageTagId : null,
                useFoodTagMap: false);
            var order = new NightBusinessOrder
            {
                DeskCode = captured.DeskCode,
                GuestId = guestId,
                RuntimeGuestId = captured.GuestId,
                GuestName = identity?.Name ?? ResolveRareGuestName(guestId, fallbackGuestName),
                SpecialBusinessRole = classification.Role,
                SpecialBusinessRoleLabel = classification.RoleLabel,
                AutomationAllowed = classification.AutomationAllowed,
                AutomationBlockReason = classification.AutomationBlockReason,
                FoodTagId = captured.HasFoodTagId ? captured.FoodTagId : null,
                FoodTag = foodTag,
                BeverageTagId = captured.HasBeverageTagId ? captured.BeverageTagId : null,
                BeverageTag = beverageTag,
                Source = string.IsNullOrWhiteSpace(captured.CaptureSource) ? "RuntimeCapture" : $"RuntimeCapture:{captured.CaptureSource}",
                FirstSeenAtUtc = captured.FirstCapturedAt,
                LastSeenAtUtc = captured.CapturedAt,
                IsFreeOrder = captured.IsFreeOrder,
                Fund = activeGuest?.Fund ?? ReadNullableIntMember(captured.ControllerObject, "GetFund"),
                BaseFundCarry = activeGuest?.BaseFundCarry ?? ReadNullableIntMember(captured.ControllerObject, "BaseFundCarry"),
                MaxFundCarry = activeGuest?.MaxFundCarry ?? ReadNullableIntMember(captured.ControllerObject, "MaxFundCarry"),
                ExtraFundByBuff = activeGuest?.ExtraFundByBuff ?? ReadNullableIntMember(captured.ControllerObject, "ExtraFundByBuff"),
                WillPayMoney = activeGuest?.WillPayMoney ?? ReadNullableBoolMember(captured.ControllerObject, "WillPayMoney"),
                RemainingOrderCount = ReadNullableIntMember(captured.ControllerObject, "RemainOrderCount"),
                HasServedFood = captured.IsFulfilled
                    || (captured.OrderObject != null && ReadOrderServedState(captured.OrderObject, "ServFood", "ServedFoodInAir")),
                HasServedBeverage = captured.IsFulfilled
                    || (captured.OrderObject != null && ReadOrderServedState(captured.OrderObject, "ServBeverage", "ServedBeverageInAir")),
            };

            yield return order;
        }
    }

    private NightBusinessGuest? FindActiveGuestForCapturedOrder(
        CapturedRuntimeSpecialOrder captured,
        IReadOnlyList<NightBusinessGuest> activeGuests)
    {
        var capturedIdentity = ResolveRareCustomerIdentity(captured.GuestId, captured.GuestName);
        foreach (var guest in activeGuests)
        {
            if (!IsCompatibleDesk(captured.DeskCode, guest.DeskCode)) continue;

            if (capturedIdentity != null && guest.GuestId.HasValue && capturedIdentity.Id == guest.GuestId.Value)
            {
                return guest;
            }

            if (captured.GuestId.HasValue && guest.GuestId.HasValue && captured.GuestId.Value == guest.GuestId.Value)
            {
                return guest;
            }

            if (capturedIdentity != null
                && string.Equals(capturedIdentity.Name, guest.GuestName, StringComparison.Ordinal))
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

    private string ResolveRareGuestName(int? guestId, string fallback)
    {
        return ResolveRareCustomerIdentity(guestId, fallback)?.Name ?? fallback;
    }

    private string ResolveCapturedTagText(string tagText, int? tagId, bool useFoodTagMap)
    {
        if (tagId.HasValue && TryResolveTagTextFromMap(tagId.Value, useFoodTagMap, out var mapped)) return mapped;

        var normalized = CanonicalizeTagText(tagText, useFoodTagMap);
        if (!string.IsNullOrWhiteSpace(normalized)) return normalized;
        return tagId.HasValue ? ResolveTagTextFromMap(tagId.Value, useFoodTagMap) : "";
    }

    private static bool HasCapturedOrderDetails(CapturedRuntimeSpecialOrder captured)
    {
        return captured.HasFoodTagId
            || captured.HasBeverageTagId
            || !string.IsNullOrWhiteSpace(captured.FoodTagDisplayText)
            || !string.IsNullOrWhiteSpace(captured.BeverageTagDisplayText);
    }

    private static bool IsCompatibleDesk(int orderDeskCode, int guestDeskCode)
    {
        if (orderDeskCode < 0) return true;
        return guestDeskCode >= 0 && orderDeskCode == guestDeskCode;
    }

    private static List<NightBusinessOrder> DeduplicateOrders(IEnumerable<NightBusinessOrder> orders)
    {
        var bySlot = new Dictionary<string, NightBusinessOrder>(StringComparer.Ordinal);

        foreach (var order in orders)
        {
            var key = $"{order.DeskCode}:{order.RuntimeGuestId?.ToString() ?? "missing"}:{order.FoodTagId?.ToString() ?? "missing"}:{order.BeverageTagId?.ToString() ?? "missing"}";
            if (!bySlot.TryGetValue(key, out var existing))
            {
                bySlot[key] = order;
                continue;
            }

            var selected = GetOrderCompletenessScore(order) > GetOrderCompletenessScore(existing) ? order : existing;
            var isFreeOrder = order.IsFreeOrder || existing.IsFreeOrder;
            bySlot[key] = CopyOrderWithSeenTimes(
                selected,
                MinSeenAt(existing.FirstSeenAtUtc, order.FirstSeenAtUtc),
                MaxSeenAt(existing.LastSeenAtUtc, order.LastSeenAtUtc),
                isFreeOrder,
                fallbackOrder: ReferenceEquals(selected, order) ? existing : order);
        }

        return bySlot.Values
            .Select(WithTraceId)
            .OrderBy(order => order.FirstSeenAtUtc ?? DateTime.MaxValue)
            .ThenBy(order => order.LastSeenAtUtc ?? DateTime.MaxValue)
            .ThenBy(order => order.DeskCode)
            .ThenBy(order => order.GuestName)
            .ToList();
    }

    private static NightBusinessOrder WithTraceId(NightBusinessOrder order)
    {
        if (!string.IsNullOrWhiteSpace(order.TraceId)) return order;
        return CopyOrderWithSeenTimes(
            order,
            order.FirstSeenAtUtc,
            order.LastSeenAtUtc,
            order.IsFreeOrder,
            RuntimeOrderTraceIdService.GetRareTraceId(order));
    }

    private static NightBusinessOrder CopyOrderWithSeenTimes(
        NightBusinessOrder order,
        DateTime? firstSeenAtUtc,
        DateTime? lastSeenAtUtc,
        bool isFreeOrder,
        string? traceId = null,
        NightBusinessOrder? fallbackOrder = null)
    {
        return new NightBusinessOrder
        {
            TraceId = string.IsNullOrWhiteSpace(traceId) ? order.TraceId : traceId,
            DeskCode = order.DeskCode,
            GuestId = order.GuestId,
            RuntimeGuestId = order.RuntimeGuestId ?? fallbackOrder?.RuntimeGuestId,
            GuestName = order.GuestName,
            SpecialBusinessRole = order.SpecialBusinessRole,
            SpecialBusinessRoleLabel = order.SpecialBusinessRoleLabel,
            AutomationAllowed = order.AutomationAllowed,
            AutomationBlockReason = order.AutomationBlockReason,
            FoodTagId = order.FoodTagId,
            FoodTag = order.FoodTag,
            BeverageTagId = order.BeverageTagId,
            BeverageTag = order.BeverageTag,
            Source = order.Source,
            FirstSeenAtUtc = firstSeenAtUtc,
            LastSeenAtUtc = lastSeenAtUtc,
            IsFreeOrder = isFreeOrder,
            Fund = order.Fund ?? fallbackOrder?.Fund,
            BaseFundCarry = order.BaseFundCarry ?? fallbackOrder?.BaseFundCarry,
            MaxFundCarry = order.MaxFundCarry ?? fallbackOrder?.MaxFundCarry,
            ExtraFundByBuff = order.ExtraFundByBuff ?? fallbackOrder?.ExtraFundByBuff,
            WillPayMoney = order.WillPayMoney ?? fallbackOrder?.WillPayMoney,
            RemainingOrderCount = order.RemainingOrderCount ?? fallbackOrder?.RemainingOrderCount,
            HasServedFood = order.HasServedFood,
            HasServedBeverage = order.HasServedBeverage,
        };
    }

    private static DateTime? MinSeenAt(DateTime? left, DateTime? right)
    {
        if (!left.HasValue) return right;
        if (!right.HasValue) return left;
        return left.Value <= right.Value ? left : right;
    }

    private static DateTime? MaxSeenAt(DateTime? left, DateTime? right)
    {
        if (!left.HasValue) return right;
        if (!right.HasValue) return left;
        return left.Value >= right.Value ? left : right;
    }

    private static int GetOrderCompletenessScore(NightBusinessOrder order)
    {
        var score = 0;
        if (!string.IsNullOrWhiteSpace(order.FoodTag)) score += 8;
        if (!string.IsNullOrWhiteSpace(order.BeverageTag)) score += 8;
        if (order.FoodTagId.HasValue) score += 2;
        if (order.BeverageTagId.HasValue) score += 2;
        if (order.HasServedFood) score += 1;
        if (order.HasServedBeverage) score += 1;
        if (string.Equals(order.Source, "OrderController", StringComparison.Ordinal)) score += 2;
        if (string.Equals(order.Source, "ServePanel", StringComparison.Ordinal)) score += 1;
        return score;
    }

    private static bool ReadOrderServedState(object order, params string[] memberNames)
    {
        foreach (var memberName in memberNames)
        {
            if (GetMemberValue(order, memberName) != null) return true;
        }

        return false;
    }

    private static bool ReadOrderFreeState(object order)
    {
        var value = GetMemberValue(order, "FreeOrder");
        if (value is bool boolValue) return boolValue;
        return false;
    }

    private static List<NightBusinessGuest> DeduplicateGuests(IEnumerable<NightBusinessGuest> guests)
    {
        var result = new List<NightBusinessGuest>();
        var indexByKey = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var guest in guests)
        {
            var key = $"{guest.DeskCode}:{guest.GuestId}:{guest.GuestName}";
            if (!indexByKey.TryGetValue(key, out var index))
            {
                indexByKey[key] = result.Count;
                result.Add(guest);
                continue;
            }

            if (GetGuestRuntimeInfoScore(guest) > GetGuestRuntimeInfoScore(result[index]))
            {
                result[index] = guest;
            }
        }

        return result
            .OrderBy(guest => guest.DeskCode)
            .ThenBy(guest => guest.GuestName)
            .ToList();
    }

    private static int GetGuestRuntimeInfoScore(NightBusinessGuest guest)
    {
        var score = 0;
        if (guest.Fund.HasValue) score += 8;
        if (guest.MaxFundCarry.HasValue) score += 4;
        if (guest.BaseFundCarry.HasValue) score += 2;
        if (guest.ExtraFundByBuff.HasValue) score += 1;
        if (guest.WillPayMoney.HasValue) score += 1;
        return score;
    }

    private bool IsRareGuestController(object? controller, bool allowOrderingGuestId = false)
    {
        if (controller == null) return false;
        if (GetMemberValue(controller, "SpecialGuest") != null) return true;

        var guest = GetMemberValue(controller, "OrderingGuest");
        return IsSpecialGuestObject(guest) || ResolveOrderingGuestRareCustomerIdentity(guest, allowOrderingGuestId) != null;
    }

    private RareCustomerIdentity? ResolveOrderingGuestRareCustomerIdentity(object? guest, bool allowIdOnly = false)
    {
        if (guest == null) return null;
        if (IsSpecialGuestObject(guest)) return ResolveRareCustomerIdentity(guest);

        var guestId = ReadGuestId(guest);
        var stringId = ReadGuestStringId(guest);
        var sourceGuestId = ReadSourceGuestId(guest);

        if (sourceGuestId.HasValue)
        {
            return ResolveRareCustomerIdentity(sourceGuestId, stringId);
        }

        if (!string.IsNullOrWhiteSpace(stringId))
        {
            return ResolveRareCustomerIdentity(null, stringId);
        }

        if (allowIdOnly && guestId.HasValue)
        {
            return ResolveRareCustomerIdentity(guestId, stringId);
        }

        return null;
    }

    private static bool AllowOrderingGuestIdResolution(string source)
    {
        return string.Equals(source, "ManualDesk", StringComparison.Ordinal);
    }

    private static bool IsSpecialGuestObject(object? guest)
    {
        if (guest == null) return false;
        var typeName = guest.GetType().FullName ?? guest.GetType().Name;
        return typeName.IndexOf("SpecialGuest", StringComparison.Ordinal) >= 0;
    }

    private RareCustomerIdentity? ResolveRareCustomerIdentity(object? guest)
    {
        if (guest == null) return null;
        return ResolveRareCustomerIdentityFromFields(guest);
    }

    private RareCustomerIdentity? ResolveRareCustomerIdentity(int? guestId, string? runtimeStringId)
    {
        return ResolveCachedMappedGuestIdentity(guestId, runtimeStringId);
    }

    private RareCustomerIdentity? ResolveCachedMappedGuestIdentity(int? guestId, string? runtimeStringId)
    {
        if (_mappedGuestSnapshot == null) return null;

        RuntimeMappedGuestEntry? entry = null;
        if (guestId.HasValue)
        {
            _mappedGuestSnapshot.ByRuntimeId.TryGetValue(guestId.Value, out entry);
        }

        if (entry == null && !string.IsNullOrWhiteSpace(runtimeStringId))
        {
            _mappedGuestSnapshot.ByRuntimeStringId.TryGetValue(runtimeStringId.Trim(), out entry);
        }

        if (entry == null) return null;

        if (entry.LocalRareCustomerId.HasValue && !string.IsNullOrWhiteSpace(entry.LocalRareCustomerName))
        {
            return new RareCustomerIdentity(entry.LocalRareCustomerId.Value, entry.LocalRareCustomerName);
        }

        return null;
    }

    private RareCustomerIdentity? ResolveRareCustomerIdentityFromFields(object? guest)
    {
        if (guest == null) return null;

        var guestId = ReadGuestId(guest);
        var stringId = ReadGuestStringId(guest);
        var sourceGuestId = ReadSourceGuestId(guest);

        return ResolveRareCustomerIdentity(guestId, stringId)
            ?? ResolveRareCustomerIdentity(sourceGuestId, stringId);
    }

    private static int? ReadGuestId(object? guest)
    {
        return ToNullableInt(GetMemberValue(guest, "Id") ?? GetMemberValue(guest, "ID"));
    }

    private static string? ReadGuestStringId(object? guest)
    {
        return GetMemberValue(guest, "StringId")?.ToString()
            ?? GetMemberValue(guest, "StrID")?.ToString();
    }

    private static int? ReadSourceGuestId(object? guest)
    {
        return ToNullableInt(GetMemberValue(guest, "SourceGuestID") ?? GetMemberValue(guest, "SourceGuestId"));
    }

    private void RecordCandidate(string kind, string source, bool accepted, string reason, Func<string>? detailsFactory)
    {
        if (_candidateDiagnostics == null) return;
        if (_candidateDiagnostics.Count >= MaxCandidateDiagnostics) return;

        var details = detailsFactory?.Invoke() ?? "";
        _candidateDiagnostics.Add(new NightBusinessCandidateDiagnostic
        {
            Kind = kind,
            Source = source,
            Accepted = accepted,
            Reason = reason,
            Details = TrimDiagnostic(details, 1200),
        });
    }

    private string DescribeControllerCandidate(object? controller)
    {
        if (controller == null) return "controller=null";

        var specialGuest = GetMemberValue(controller, "SpecialGuest");
        var orderingGuest = GetMemberValue(controller, "OrderingGuest");
        var parts = new List<string>
        {
            $"controllerType={ShortType(controller)}",
            $"desk={ShortValue(GetMemberValue(controller, "DeskCode"))}",
            $"spawnType={ShortValue(GetMemberValue(controller, "GuestControllerSpawnType"))}",
            $"isHerself={ShortValue(GetMemberValue(controller, "IsHerself"))}",
            $"isControlled={ShortValue(GetMemberValue(controller, "IsControlled"))}",
            $"specialGuest=[{DescribeGuestObject(specialGuest)}]",
            $"orderingGuest=[{DescribeGuestObject(orderingGuest)}]",
        };

        return string.Join("; ", parts);
    }

    private string DescribeOrderCandidate(object? order, object? controller)
    {
        if (order == null) return $"order=null; controller=[{DescribeControllerCandidate(controller)}]";

        var isNormalOrder = IsNormalOrderCandidate(order);
        var orderGuest = GetMemberValue(order, "Guest");
        var specialGuest = GetMemberValue(order, "SpecialGuests")
            ?? GetMemberValue(controller, "SpecialGuest")
            ?? (isNormalOrder ? null : GetMemberValue(controller, "OrderingGuest"));
        var orderingGuest = GetMemberValue(controller, "OrderingGuest");
        var requestFood = GetMemberValue(order, "RequestFood");
        var requestBeverage = GetMemberValue(order, "RequestBeverage");
        var parts = new List<string>
        {
            $"orderType={ShortType(order)}",
            $"orderEnumType={ShortValue(GetMemberValue(order, "Type"))}",
            $"desk={ShortValue(GetMemberValue(order, "DeskCode") ?? GetMemberValue(controller, "DeskCode"))}",
            $"requestFoodTag={ShortValue(GetMemberValue(order, "RequestFoodTag"))}",
            $"requestBeverageTag={ShortValue(GetMemberValue(order, "RequestBeverageTag"))}",
            $"foodRequest={ShortValue(GetMemberValue(order, "foodRequest"))}",
            $"beverageRequest={ShortValue(GetMemberValue(order, "beverageRequest"))}",
            $"requestFood=[{DescribeSellableObject(requestFood)}]",
            $"requestBeverage=[{DescribeSellableObject(requestBeverage)}]",
            $"guest=[{DescribeGuestObject(orderGuest ?? orderingGuest, includeMapping: !isNormalOrder)}]",
            $"specialGuest=[{DescribeGuestObject(specialGuest)}]",
            $"controller=[{DescribeControllerCandidate(controller)}]",
        };

        return string.Join("; ", parts);
    }

    private static bool IsNormalOrderCandidate(object? order)
    {
        var resolution = RuntimeOrderTypeResolver.Resolve(order);
        return resolution.Resolved && resolution.Kind == RuntimeOrderKind.Normal;
    }

    private string DescribeGuestObject(object? guest, bool includeMapping = true)
    {
        if (guest == null) return "null";

        var id = ReadGuestId(guest);
        var stringId = ReadGuestStringId(guest);
        var sourceGuestId = ReadSourceGuestId(guest);
        var identity = IsSpecialGuestObject(guest)
            ? ResolveRareCustomerIdentity(guest)
            : ResolveOrderingGuestRareCustomerIdentity(guest);
        var knownName = identity?.Name ?? ResolveNormalCustomerName(id) ?? "";

        var parts = new List<string>
        {
            $"type={ShortType(guest)}",
            $"id={id?.ToString() ?? ""}",
            $"known={knownName}",
            $"stringId={TrimDiagnostic(stringId ?? "", 80)}",
            $"sourceGuestId={sourceGuestId?.ToString() ?? ""}",
        };

        if (includeMapping && id.HasValue && IsSpecialGuestObject(guest))
        {
            parts.Add($"mapping=[{DescribeSpecialGuestMapping(id.Value)}]");
        }

        return string.Join(",", parts);
    }

    private string DescribeSellableObject(object? sellable)
    {
        if (sellable == null) return "null";

        var id = ToNullableInt(GetMemberValue(sellable, "id")
            ?? GetMemberValue(sellable, "Id")
            ?? GetMemberValue(sellable, "ID")
            ?? GetMemberValue(sellable, "foodID")
            ?? GetMemberValue(sellable, "FoodID"));
        var name = GetMemberValue(sellable, "Name")?.ToString()
            ?? GetMemberValue(sellable, "name")?.ToString();
        return string.Join(",",
            $"type={ShortType(sellable)}",
            $"id={id?.ToString() ?? ""}",
            $"name={TrimDiagnostic(name ?? "", 80)}");
    }

    private string? ResolveNormalCustomerName(int? id)
    {
        if (!id.HasValue) return null;
        return _repository.NormalCustomers.FirstOrDefault(customer => customer.Id == id.Value)?.Name;
    }

    private string DescribeSpecialGuestMapping(int id)
    {
        if (_mappedGuestSnapshot == null) return "identity snapshot unavailable";
        if (!_mappedGuestSnapshot.ByRuntimeId.TryGetValue(id, out var entry)) return "identity missing";

        return string.Join(",",
            $"runtimeId={entry.RuntimeId?.ToString() ?? ""}",
            $"runtimeStringId={TrimDiagnostic(entry.RuntimeStringId, 80)}",
            $"sourceGuestId={entry.SourceGuestId?.ToString() ?? ""}",
            $"sourceStringId={TrimDiagnostic(entry.SourceStringId, 80)}",
            $"localGuestId={entry.LocalRareCustomerId?.ToString() ?? ""}",
            $"aliasSource={entry.AliasSource}");
    }

    private static string ShortType(object? value)
    {
        return value?.GetType().FullName ?? "null";
    }

    private static string ShortValue(object? value)
    {
        return TrimDiagnostic(FormatDiagnosticValue(value), 100);
    }

    private static string FormatDiagnosticValue(object? value)
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

    private static string TrimDiagnostic(string value, int maxLength)
    {
        if (value.Length <= maxLength) return value;
        return value[..Math.Max(0, maxLength - 3)] + "...";
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
        var tagMap = useFoodTagMap
            ? _repository.FoodTagIdMap
            : _repository.BeverageTagIdMap;
        if (tagMap.TryGetValue(key, out var mapped))
        {
            tagText = NormalizeTagText(mapped) ?? mapped;
            return true;
        }

        tagText = "";
        return false;
    }

    private string ResolveTagTextFromMap(int tagId, bool useFoodTagMap)
    {
        if (TryResolveTagTextFromMap(tagId, useFoodTagMap, out var mapped)) return mapped;

        if (tagId < 0) return "";
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
        bool useFoodTagMap)
    {
        var tagValue = GetMemberValue(order, orderPropertyName);
        var tagId = ToNullableInt(tagValue);
        if (tagId.HasValue && TryResolveTagTextFromMap(tagId.Value, useFoodTagMap, out var mapped)) return mapped;

        var controllerValue = InvokeMethod(controller, controllerMethodName, order)?.ToString();
        var normalized = CanonicalizeTagText(controllerValue, useFoodTagMap);
        if (!string.IsNullOrWhiteSpace(normalized)) return normalized;

        return tagId.HasValue ? ResolveTagText(specialGuest, guestMethodName, tagId.Value, useFoodTagMap) : "";
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

    private static int? ToNullableInt(object? value)
    {
        if (value == null) return null;
        if (value is int intValue) return intValue;
        if (value is long longValue) return (int)longValue;
        if (value is short shortValue) return shortValue;
        return int.TryParse(value.ToString(), out var parsed) ? parsed : null;
    }

    private static int? ReadNullableIntMember(object? instance, string name)
    {
        return ToNullableInt(
            GetMemberValue(instance, name)
            ?? RuntimeReflectionUtility.InvokeMethod(instance, $"get_{name}")
            ?? RuntimeReflectionUtility.InvokeMethod(instance, name));
    }

    private static bool? ReadNullableBoolMember(object? instance, string name)
    {
        var value = GetMemberValue(instance, name)
            ?? RuntimeReflectionUtility.InvokeMethod(instance, $"get_{name}")
            ?? RuntimeReflectionUtility.InvokeMethod(instance, name);
        if (value == null) return null;
        if (value is bool boolValue) return boolValue;
        if (bool.TryParse(value.ToString(), out var parsed)) return parsed;
        return null;
    }

}
