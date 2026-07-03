using System.Reflection;
using Il2CppInterop.Runtime.InteropTypes;
using MystiaStewardCompanion.Core;
using UnityEngine;
using static MystiaStewardCompanion.Save.RuntimeReflectionUtility;

namespace MystiaStewardCompanion.Save;

internal sealed class NightBusinessReflectionProvider
{
    private static readonly TimeSpan UnmatchedCapturedOrderGrace = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan RuntimeCapturedOrderMaxAge = TimeSpan.FromHours(6);

    private const string GuestGroupControllerTypeName = "NightScene.GuestManagementUtility.GuestGroupController";
    private const string GuestsManagerTypeName = "NightScene.GuestManagementUtility.GuestsManager";
    private const string SpecialOrderTypeName = "NightScene.GuestManagementUtility.GuestsManager+SpecialOrder";
    private const string OrderControllerTypeName = "Night.UI.HUD.Ordering.OrderController";
    private const string OrderingElementTypeName = "NightScene.UI.GuestManagementUtility.OrderingElement";
    private const string WorkSceneServePannelTypeName = "NightScene.UI.GuestManagementUtility.WorkSceneServePannel";
    private const string IzakayaConfigureTypeName = "GameData.RunTime.NightSceneUtility.IzakayaConfigure";
    private const string DataBaseCharacterTypeName = "GameData.Core.Collections.CharacterUtility.DataBaseCharacter";
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
    private readonly RareCustomerIdentityResolver _rareIdentityResolver;
    private readonly RuntimeMappedGuestCatalogSnapshot? _mappedGuestSnapshot;
    private readonly RuntimeStaticDataSnapshot? _staticDataSnapshot;
    private readonly NightBusinessDiagnosticSink? _diagnostics;
    private readonly string _sceneName;
    private IReadOnlyList<string>? _foodTagCandidates;
    private IReadOnlyList<string>? _beverageTagCandidates;
    private List<NightBusinessCandidateDiagnostic>? _candidateDiagnostics;
    private readonly Dictionary<string, double> _performanceMs = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, double> PerformanceMs => _performanceMs;

    public NightBusinessReflectionProvider(
        DataRepository repository,
        NightBusinessDiagnosticSink? diagnostics = null,
        string sceneName = "",
        RuntimeMappedGuestCatalogSnapshot? mappedGuestSnapshot = null,
        RuntimeStaticDataSnapshot? staticDataSnapshot = null)
    {
        _repository = repository;
        _rareIdentityResolver = repository.RareCustomerIdentities;
        _mappedGuestSnapshot = mappedGuestSnapshot;
        _staticDataSnapshot = staticDataSnapshot;
        _diagnostics = diagnostics;
        _sceneName = sceneName;
    }

    public NightBusinessContext LoadContext()
    {
        _performanceMs.Clear();
        var guests = new List<NightBusinessGuest>();
        var orders = new List<NightBusinessOrder>();
        var errors = new List<string>();
        var sourceStats = new List<string>();
        _candidateDiagnostics = _diagnostics == null ? null : new List<NightBusinessCandidateDiagnostic>();
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

        var preferRuntimeCapturedOrders = _diagnostics == null && runtimeOrders.Count > 0;
        sourceStats.Add(preferRuntimeCapturedOrders ? "OrderReadMode=RuntimeCapture" : "OrderReadMode=Reflection");

        try
        {
            var servePanelContexts = Measure("rare.servePanel.contexts", () => ReadServePanelContexts().ToList());
            sourceStats.Add($"ServePanel={servePanelContexts.Count}");
            guests.AddRange(Measure("rare.servePanel.guests", () => ReadServePanelRareGuests(servePanelContexts).ToList()));
            if (!preferRuntimeCapturedOrders)
            {
                orders.AddRange(Measure("rare.servePanel.orders", () => ReadServePanelOrders(servePanelContexts).ToList()));
            }
        }
        catch (Exception ex)
        {
            sourceStats.Add("ServePanel=err");
            errors.Add($"serve panel: {ex.Message}");
        }

        if (!preferRuntimeCapturedOrders)
        {
            try
            {
                var orderControllerOrders = Measure("rare.orderController", () => ReadOrderControllerOrders().ToList());
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
                var hudOrders = Measure("rare.hud", () => ReadHudOrders().ToList());
                sourceStats.Add($"HUD={hudOrders.Count}");
                orders.AddRange(hudOrders);
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
                if (!preferRuntimeCapturedOrders)
                {
                    orders.AddRange(Measure($"rare.orders.{source.Source}", () => ReadControllerOrders(controllers, source.Source).ToList()));
                }
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
            if (!preferRuntimeCapturedOrders)
            {
                orders.AddRange(Measure("rare.orders.Queue", () => ReadControllerOrders(queuedControllers, "Queue").ToList()));
            }
        }
        catch (Exception ex)
        {
            sourceStats.Add("Queue=err");
            errors.Add($"Queue: {ex.Message}");
        }

        var activeGuests = Measure("deduplicate.guests", () => DeduplicateGuests(guests));
        var rawLiveOrders = orders.ToList();
        var acceptedRuntimeOrders = new List<NightBusinessOrder>();
        var reflectionFallbackOrders = new List<NightBusinessOrder>();
        if (runtimeOrders.Count > 0)
        {
            acceptedRuntimeOrders = Measure("runtimeCapture.accept", () => ReadRuntimeCapturedOrders(runtimeOrders, activeGuests).ToList());
            sourceStats.Add($"RuntimeCapture={acceptedRuntimeOrders.Count}/{runtimeOrders.Count}");
            sourceStats.Add($"RuntimeCaptureStatus={SpecialOrderRuntimeCapture.Status}");
            sourceStats.Add($"UiPinning={RuntimeUiPinningService.Status}");
            orders.AddRange(acceptedRuntimeOrders);
            if (preferRuntimeCapturedOrders && acceptedRuntimeOrders.Count < runtimeOrders.Count)
            {
                reflectionFallbackOrders = Measure(
                    "runtimeCapture.reflectionFallback",
                    () => ReadReflectionFallbackOrders(sourceStats, errors).ToList());
                sourceStats.Add($"ReflectionFallback={reflectionFallbackOrders.Count}");
                orders.AddRange(reflectionFallbackOrders);
            }
            else
            {
                sourceStats.Add("ReflectionFallback=skipped");
            }
        }
        else
        {
            sourceStats.Add($"RuntimeCaptureStatus={SpecialOrderRuntimeCapture.Status}");
            sourceStats.Add($"UiPinning={RuntimeUiPinningService.Status}");
            sourceStats.Add("ReflectionFallback=not-needed");
        }

        var activeOrders = Measure("deduplicate.orders", () => DeduplicateOrders(orders));
        var place = Measure("place.name", ReadCurrentPlace);
        var placeLabel = Measure("place.label", ReadCurrentPlaceLabel);
        var recentRuntimeParseFailures = _diagnostics == null
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
        if (_diagnostics == null) return;

        try
        {
            RuntimeStaticDataDiagnosticSink.WriteMappedSpecialGuests(
                RuntimeStaticDataDiagnosticSink.ResolvePath(_diagnostics.Path),
                mappedGuestSnapshot);
            RuntimeStaticDataDiagnosticSink.WriteStaticData(_diagnostics.Path, staticDataSnapshot);
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
                ActiveGuests = activeGuests.ToList(),
                FinalOrders = finalOrders.ToList(),
                Candidates = candidates.ToList(),
                RecentRuntimeParseFailures = recentRuntimeParseFailures.ToList(),
            });
        }
        catch
        {
            // Diagnostics must never affect gameplay or recommendation refreshes.
        }
    }

    private IReadOnlyList<NightBusinessOrder> ReadReflectionFallbackOrders(
        ICollection<string> sourceStats,
        ICollection<string> errors)
    {
        var result = new List<NightBusinessOrder>();

        try
        {
            var servePanelContexts = Measure("fallback.rare.servePanel.contexts", () => ReadServePanelContexts().ToList());
            sourceStats.Add($"FallbackServePanel={servePanelContexts.Count}");
            result.AddRange(Measure("fallback.rare.servePanel.orders", () => ReadServePanelOrders(servePanelContexts).ToList()));
        }
        catch (Exception ex)
        {
            sourceStats.Add("FallbackServePanel=err");
            errors.Add($"fallback serve panel: {ex.Message}");
        }

        try
        {
            var orderControllerOrders = Measure("fallback.rare.orderController", () => ReadOrderControllerOrders().ToList());
            sourceStats.Add($"FallbackOrderController={orderControllerOrders.Count}");
            result.AddRange(orderControllerOrders);
        }
        catch (Exception ex)
        {
            sourceStats.Add("FallbackOrderController=err");
            errors.Add($"fallback order controller: {ex.Message}");
        }

        try
        {
            var hudOrders = Measure("fallback.rare.hud", () => ReadHudOrders().ToList());
            sourceStats.Add($"FallbackHUD={hudOrders.Count}");
            result.AddRange(hudOrders);
        }
        catch (Exception ex)
        {
            sourceStats.Add("FallbackHUD=err");
            errors.Add($"fallback HUD orders: {ex.Message}");
        }

        foreach (var source in ManagerControllerSources)
        {
            try
            {
                var controllers = Measure($"fallback.controllers.{source.Source}", () => ReadManagerControllers(source.MemberName).ToList());
                sourceStats.Add($"Fallback{source.Source}={controllers.Count}");
                result.AddRange(Measure($"fallback.rare.orders.{source.Source}", () => ReadControllerOrders(controllers, source.Source).ToList()));
            }
            catch (Exception ex)
            {
                sourceStats.Add($"Fallback{source.Source}=err");
                errors.Add($"fallback {source.Source}: {ex.Message}");
            }
        }

        try
        {
            var queuedControllers = Measure("fallback.controllers.Queue", () => ReadQueuedControllers().ToList());
            sourceStats.Add($"FallbackQueue={queuedControllers.Count}");
            result.AddRange(Measure("fallback.rare.orders.Queue", () => ReadControllerOrders(queuedControllers, "Queue").ToList()));
        }
        catch (Exception ex)
        {
            sourceStats.Add("FallbackQueue=err");
            errors.Add($"fallback Queue: {ex.Message}");
        }

        return result;
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

    private IEnumerable<NightBusinessOrder> ReadControllerOrders(IEnumerable<object?> controllers, string source)
    {
        foreach (var controller in controllers)
        {
            if (!IsRareGuestController(controller, AllowOrderingGuestIdResolution(source)))
            {
                RecordCandidate("Controller", source, accepted: false, "not recognized as rare guest controller", () => DescribeControllerCandidate(controller));
                continue;
            }

            RecordCandidate("Controller", source, accepted: true, "rare guest controller", () => DescribeControllerCandidate(controller));

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

        var peekOrder = InvokeMethod(controller, "PeekOrders");
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

        var specialGuest = GetMemberValue(order, "SpecialGuests") ?? GetMemberValue(controller, "SpecialGuest");
        var orderingGuest = GetMemberValue(controller, "OrderingGuest");
        if (specialGuest == null
            && (IsSpecialGuestObject(orderingGuest)
                || ResolveOrderingGuestRareCustomerIdentity(orderingGuest, IsSpecialOrder(order) || IsManualSpecialOrder(order, controller)) != null))
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
            DeskCode = ToInt(GetMemberValue(order, "DeskCode") ?? GetMemberValue(controller, "DeskCode")),
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

        var readableOrder = TryCastOrder(order, SpecialOrderTypeName) ?? order;
        if (!IsSpecialOrder(readableOrder) && !IsManualSpecialOrder(readableOrder, controller))
        {
            RecordCandidate("Order", source, accepted: false, "not a special order by current rules", () => DescribeOrderCandidate(readableOrder, controller));
            return null;
        }

        var now = DateTime.UtcNow;

        var specialGuest = GetMemberValue(readableOrder, "SpecialGuests")
            ?? GetMemberValue(controller, "SpecialGuest");
        var orderingGuest = GetMemberValue(controller, "OrderingGuest");
        if (specialGuest == null
            && (IsSpecialGuestObject(orderingGuest)
                || ResolveOrderingGuestRareCustomerIdentity(orderingGuest, IsManualSpecialOrder(readableOrder, controller)) != null))
        {
            specialGuest = orderingGuest;
        }

        if (specialGuest == null)
        {
            RecordCandidate("Order", source, accepted: false, "SpecialGuests/SpecialGuest/OrderingGuest missing", () => DescribeOrderCandidate(readableOrder, controller));
            return null;
        }

        var guestId = ReadGuestId(specialGuest);
        var identity = ResolveRareCustomerIdentity(specialGuest);
        var foodTag = ResolveOrderTagText(readableOrder, controller, specialGuest, "GetOrderFoodText", "GetFoodTagText", "RequestFoodTag", "ReqFoodTag", useFoodTagMap: true);
        var beverageTag = ResolveOrderTagText(readableOrder, controller, specialGuest, "GetOrderBevText", "GetBevTagText", "RequestBeverageTag", "ReqBevTag", useFoodTagMap: false);
        var foodTagId = ResolveTagId(foodTag, GetMemberValue(readableOrder, "RequestFoodTag"), useFoodTagMap: true);
        var beverageTagId = ResolveTagId(beverageTag, GetMemberValue(readableOrder, "RequestBeverageTag"), useFoodTagMap: false);
        if (foodTagId == 0 && beverageTagId == 0 && string.IsNullOrWhiteSpace(foodTag) && string.IsNullOrWhiteSpace(beverageTag))
        {
            RecordCandidate("Order", source, accepted: false, "empty food and beverage tag", () => DescribeOrderCandidate(readableOrder, controller));
            return null;
        }

        var deskCode = ToInt(GetMemberValue(readableOrder, "DeskCode") ?? GetMemberValue(controller, "DeskCode"));
        var result = new NightBusinessOrder
        {
            DeskCode = deskCode,
            GuestId = identity?.Id ?? guestId,
            GuestName = identity?.Name ?? ReadGuestName(specialGuest, guestId),
            FoodTagId = foodTagId,
            FoodTag = foodTag,
            BeverageTagId = beverageTagId,
            BeverageTag = beverageTag,
            Source = source,
            FirstSeenAtUtc = now,
            LastSeenAtUtc = now,
            IsFreeOrder = ReadOrderFreeState(readableOrder),
            HasServedFood = ReadOrderServedState(readableOrder, "ServFood", "ServedFoodInAir"),
            HasServedBeverage = ReadOrderServedState(readableOrder, "ServBeverage", "ServedBeverageInAir"),
        };
        RecordCandidate("Order", source, accepted: true, "accepted special order", () => DescribeOrderCandidate(readableOrder, controller));
        return result;
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
            var identity = ResolveRareCustomerIdentity(captured.GuestId, captured.GuestName);
            var guestId = identity?.Id ?? captured.GuestId ?? activeGuest?.GuestId;
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
                GuestName = identity?.Name ?? ResolveRareGuestName(guestId, fallbackGuestName),
                FoodTagId = ResolveTagId(foodTag, captured.HasFoodTagId ? captured.FoodTagId : null, useFoodTagMap: true),
                FoodTag = foodTag,
                BeverageTagId = ResolveTagId(beverageTag, captured.HasBeverageTagId ? captured.BeverageTagId : null, useFoodTagMap: false),
                BeverageTag = beverageTag,
                Source = string.IsNullOrWhiteSpace(captured.CaptureSource) ? "RuntimeCapture" : $"RuntimeCapture:{captured.CaptureSource}",
                FirstSeenAtUtc = captured.FirstCapturedAt,
                LastSeenAtUtc = captured.CapturedAt,
                IsFreeOrder = captured.IsFreeOrder,
            };

            if (ShouldKeepCapturedOrder(order, captured, activeGuests, now))
            {
                yield return order;
            }
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

    private bool ShouldKeepCapturedOrder(
        NightBusinessOrder order,
        CapturedRuntimeSpecialOrder captured,
        IReadOnlyList<NightBusinessGuest> activeGuests,
        DateTime nowUtc)
    {
        if (!HasCapturedOrderDetails(captured)) return false;
        if (MatchesActiveGuest(order, activeGuests)) return true;
        if (IsCapturedRuntimeOrderStillLive(captured)) return true;
        return nowUtc - captured.CapturedAt <= UnmatchedCapturedOrderGrace;
    }

    private static bool HasCapturedOrderDetails(CapturedRuntimeSpecialOrder captured)
    {
        return captured.HasFoodTagId
            || captured.HasBeverageTagId
            || !string.IsNullOrWhiteSpace(captured.FoodTag)
            || !string.IsNullOrWhiteSpace(captured.BeverageTag);
    }

    private bool IsCapturedRuntimeOrderStillLive(CapturedRuntimeSpecialOrder captured)
    {
        if (captured.OrderObject == null || captured.ControllerObject == null) return false;

        try
        {
            if (IsRuntimeOrderFulfilled(captured.OrderObject)) return false;

            foreach (var currentOrder in EnumerateControllerOrders(captured.ControllerObject))
            {
                if (currentOrder == null) continue;
                var readableCurrentOrder = TryCastOrder(currentOrder, SpecialOrderTypeName) ?? currentOrder;
                if (!IsSameRuntimeObject(readableCurrentOrder, captured.OrderObject)
                    && !IsSameRuntimeObject(currentOrder, captured.OrderObject))
                {
                    continue;
                }

                if (IsRuntimeOrderFulfilled(readableCurrentOrder)) return false;
                return ReadOrder(readableCurrentOrder, captured.ControllerObject, "RuntimeCaptureLive") != null;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static object? TryCastOrder(object? order, string targetTypeName)
    {
        if (order is not Il2CppObjectBase il2CppObject) return null;

        var targetType = FindType(targetTypeName);
        if (targetType == null) return null;
        if (targetType.IsInstanceOfType(order)) return order;

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

    private static bool IsSameRuntimeObject(object left, object right)
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

    private static bool IsRuntimeOrderFulfilled(object order)
    {
        var value = GetMemberValue(order, "IsFullfilled");
        if (value is bool boolValue) return boolValue;
        return string.Equals(value?.ToString(), "true", StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesActiveGuest(NightBusinessOrder order, IReadOnlyList<NightBusinessGuest> activeGuests)
    {
        foreach (var guest in activeGuests)
        {
            if (!IsCompatibleDesk(order.DeskCode, guest.DeskCode)) continue;

            if (order.GuestId.HasValue && guest.GuestId.HasValue && order.GuestId.Value == guest.GuestId.Value) return true;
            if (!string.IsNullOrWhiteSpace(order.GuestName)
                && string.Equals(order.GuestName, guest.GuestName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
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
            var key = $"{order.DeskCode}:{order.GuestId}";
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
                isFreeOrder);
        }

        return bySlot.Values
            .OrderBy(order => order.FirstSeenAtUtc ?? DateTime.MaxValue)
            .ThenBy(order => order.LastSeenAtUtc ?? DateTime.MaxValue)
            .ThenBy(order => order.DeskCode)
            .ThenBy(order => order.GuestName)
            .ToList();
    }

    private static NightBusinessOrder CopyOrderWithSeenTimes(
        NightBusinessOrder order,
        DateTime? firstSeenAtUtc,
        DateTime? lastSeenAtUtc,
        bool isFreeOrder)
    {
        return new NightBusinessOrder
        {
            DeskCode = order.DeskCode,
            GuestId = order.GuestId,
            GuestName = order.GuestName,
            FoodTagId = order.FoodTagId,
            FoodTag = order.FoodTag,
            BeverageTagId = order.BeverageTagId,
            BeverageTag = order.BeverageTag,
            Source = order.Source,
            FirstSeenAtUtc = firstSeenAtUtc,
            LastSeenAtUtc = lastSeenAtUtc,
            IsFreeOrder = isFreeOrder,
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
        if (order.FoodTagId != 0) score += 2;
        if (order.BeverageTagId != 0) score += 2;
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
        if (bool.TryParse(value?.ToString(), out var parsed)) return parsed;
        return bool.TryParse(ResolveOrderTextValue(SafeToString(order), "IsFreeOrder?"), out var textParsed) && textParsed;
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

    private static bool IsSpecialOrder(object order)
    {
        var type = GetMemberValue(order, "Type")?.ToString();
        if (string.Equals(type, "Special", StringComparison.OrdinalIgnoreCase)) return true;
        return order.GetType().Name.IndexOf("SpecialOrder", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private bool IsRareGuestController(object? controller, bool allowOrderingGuestId = false)
    {
        if (controller == null) return false;
        if (GetMemberValue(controller, "SpecialGuest") != null) return true;

        var guest = GetMemberValue(controller, "OrderingGuest");
        return IsSpecialGuestObject(guest) || ResolveOrderingGuestRareCustomerIdentity(guest, allowOrderingGuestId) != null;
    }

    private bool IsManualSpecialOrder(object order, object? controller)
    {
        if (!ToBool(GetMemberValue(order, "ManualOrder"))) return false;
        if (IsSpecialGuestObject(GetMemberValue(order, "SpecialGuests"))) return true;
        return IsRareGuestController(controller, allowOrderingGuestId: true);
    }

    private RareCustomerIdentity? ResolveOrderingGuestRareCustomerIdentity(object? guest, bool allowIdOnly = false)
    {
        if (guest == null) return null;
        if (IsSpecialGuestObject(guest)) return ResolveRareCustomerIdentity(guest);

        var guestId = ReadGuestId(guest);
        var stringId = ReadGuestStringId(guest);
        var displayName = ReadGuestDisplayName(guest);
        var sourceGuestId = ReadSourceGuestId(guest);

        if (sourceGuestId.HasValue)
        {
            return ResolveRareCustomerIdentity(sourceGuestId, stringId)
                ?? ResolveRareCustomerIdentity(sourceGuestId, displayName);
        }

        if (!string.IsNullOrWhiteSpace(stringId))
        {
            return ResolveRareCustomerIdentity(null, stringId);
        }

        if (allowIdOnly && guestId.HasValue)
        {
            return ResolveRareCustomerIdentity(guestId, displayName);
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

        var identity = ResolveRareCustomerIdentityFromFields(guest);
        if (identity != null) return identity;

        var guestId = ReadGuestId(guest);
        if (!guestId.HasValue) return null;

        return ResolveRareCustomerIdentityFromFields(ReadMappedSpecialGuest(guestId.Value));
    }

    private RareCustomerIdentity? ResolveRareCustomerIdentity(int? guestId, string? runtimeNameOrStringId)
    {
        return ResolveCachedMappedGuestIdentity(guestId, runtimeNameOrStringId)
            ?? _rareIdentityResolver.Resolve(guestId, runtimeNameOrStringId);
    }

    private RareCustomerIdentity? ResolveCachedMappedGuestIdentity(int? guestId, string? runtimeNameOrStringId)
    {
        if (guestId.HasValue && _repository.RareCustomersById.TryGetValue(guestId.Value, out var directCustomer))
        {
            return new RareCustomerIdentity(directCustomer.Id, directCustomer.Name);
        }

        if (_mappedGuestSnapshot == null) return null;

        RuntimeMappedGuestEntry? entry = null;
        if (guestId.HasValue)
        {
            _mappedGuestSnapshot.ByRuntimeId.TryGetValue(guestId.Value, out entry);
        }

        if (entry == null && !string.IsNullOrWhiteSpace(runtimeNameOrStringId))
        {
            _mappedGuestSnapshot.ByRuntimeStringId.TryGetValue(runtimeNameOrStringId.Trim(), out entry);
        }

        if (entry == null) return null;

        if (entry.LocalRareCustomerId.HasValue && !string.IsNullOrWhiteSpace(entry.LocalRareCustomerName))
        {
            return new RareCustomerIdentity(entry.LocalRareCustomerId.Value, entry.LocalRareCustomerName);
        }

        if (entry.RuntimeId.HasValue && _repository.RareCustomersById.TryGetValue(entry.RuntimeId.Value, out var runtimeCustomer))
        {
            return new RareCustomerIdentity(runtimeCustomer.Id, runtimeCustomer.Name);
        }

        if (entry.SourceGuestId.HasValue && _repository.RareCustomersById.TryGetValue(entry.SourceGuestId.Value, out var sourceCustomer))
        {
            return new RareCustomerIdentity(sourceCustomer.Id, sourceCustomer.Name);
        }

        return entry.RuntimeCustomer == null
            ? null
            : new RareCustomerIdentity(entry.RuntimeCustomer.Id, entry.RuntimeCustomer.Name);
    }

    private RareCustomerIdentity? ResolveRareCustomerIdentityFromFields(object? guest)
    {
        if (guest == null) return null;

        var guestId = ReadGuestId(guest);
        var stringId = ReadGuestStringId(guest);
        var name = ReadGuestDisplayName(guest);
        var sourceGuestId = ReadSourceGuestId(guest);

        return ResolveRareCustomerIdentity(guestId, stringId)
            ?? ResolveRareCustomerIdentity(guestId, name)
            ?? ResolveRareCustomerIdentity(sourceGuestId, stringId)
            ?? ResolveRareCustomerIdentity(sourceGuestId, name);
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

    private static string? ReadGuestDisplayName(object? guest)
    {
        return GetMemberValue(guest, "Name")?.ToString()
            ?? GetMemberValue(guest, "DisplayName")?.ToString()
            ?? GetMemberValue(guest, "CharacterName")?.ToString();
    }

    private static int? ReadSourceGuestId(object? guest)
    {
        return ToNullableInt(GetMemberValue(guest, "SourceGuestID") ?? GetMemberValue(guest, "SourceGuestId"));
    }

    private static object? ReadMappedSpecialGuest(int id)
    {
        var dataBaseCharacterType = FindType(DataBaseCharacterTypeName);
        if (dataBaseCharacterType == null) return null;

        var isMapped = InvokeStaticMethod(dataBaseCharacterType, "IsSpecialGuestMapped", id);
        var targetId = ToBool(isMapped) ? ToNullableInt(InvokeStaticMethod(dataBaseCharacterType, "MappedID2TargetID", id)) : null;
        var refGuestId = targetId ?? id;
        var specialExists = InvokeStaticMethod(dataBaseCharacterType, "SpecialGuestExists", refGuestId);
        if (!ToBool(specialExists) && !targetId.HasValue) return null;

        return InvokeStaticMethod(dataBaseCharacterType, "RefSGuest", refGuestId);
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
            $"allOrders={SafeCount(GetMemberValue(controller, "AllOrders"))}",
            $"allOrdersData={SafeCount(GetMemberValue(controller, "AllOrdersData"))}",
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
            $"text={TrimDiagnostic(SafeToString(order) ?? "", 260)}",
        };

        return string.Join("; ", parts);
    }

    private static bool IsNormalOrderCandidate(object? order)
    {
        if (order == null) return false;
        var typeName = order.GetType().Name;
        if (typeName.IndexOf("NormalOrder", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        var type = GetMemberValue(order, "Type");
        return string.Equals(type?.ToString(), "Normal", StringComparison.OrdinalIgnoreCase) || ToNullableInt(type) == 0;
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
        var dataBaseCharacterType = FindType(DataBaseCharacterTypeName);
        if (dataBaseCharacterType == null) return "DataBaseCharacter not found";

        var isMapped = InvokeStaticMethod(dataBaseCharacterType, "IsSpecialGuestMapped", id);
        var specialExists = InvokeStaticMethod(dataBaseCharacterType, "SpecialGuestExists", id);
        var targetId = ToBool(isMapped) ? InvokeStaticMethod(dataBaseCharacterType, "MappedID2TargetID", id) : null;
        var refGuestId = ToNullableInt(targetId) ?? id;
        var refGuest = (ToBool(specialExists) || ToNullableInt(targetId).HasValue)
            ? InvokeStaticMethod(dataBaseCharacterType, "RefSGuest", refGuestId)
            : null;
        return string.Join(",",
            $"isMapped={ShortValue(isMapped)}",
            $"specialExists={ShortValue(specialExists)}",
            $"targetId={ShortValue(targetId)}",
            $"refGuest=[{DescribeGuestObject(refGuest, includeMapping: false)}]");
    }

    private static int SafeCount(object? value)
    {
        try
        {
            return CountObjects(value);
        }
        catch
        {
            return -1;
        }
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
        string orderTextLabel,
        bool useFoodTagMap)
    {
        var tagValue = GetMemberValue(order, orderPropertyName);
        var tagId = ToNullableInt(tagValue);
        if (tagId.HasValue && TryResolveTagTextFromMap(tagId.Value, useFoodTagMap, out var mapped)) return mapped;

        var normalized = CanonicalizeTagText(ResolveOrderTagFromText(SafeToString(order), orderTextLabel), useFoodTagMap);
        if (!string.IsNullOrWhiteSpace(normalized)) return normalized;

        var controllerValue = InvokeMethod(controller, controllerMethodName, order)?.ToString();
        normalized = CanonicalizeTagText(controllerValue, useFoodTagMap);
        if (!string.IsNullOrWhiteSpace(normalized)) return normalized;

        return tagId.HasValue ? ResolveTagText(specialGuest, guestMethodName, tagId.Value, useFoodTagMap) : "";
    }

    private static string ResolveOrderTagFromText(string? orderText, string label)
    {
        return ResolveOrderTextValue(orderText, label);
    }

    private static string ResolveOrderTextValue(string? orderText, string label)
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

    private int ResolveTagId(string tagText, object? fallbackValue, bool useFoodTagMap)
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

        var fallbackId = ToNullableInt(fallbackValue);
        if (!fallbackId.HasValue) return 0;
        if (fallbackId.Value < 0 && !TryResolveTagTextFromMap(fallbackId.Value, useFoodTagMap, out _)) return 0;
        return fallbackId.Value;
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
        return ToNullableInt(GetMemberValue(instance, name));
    }

    private static bool? ReadNullableBoolMember(object? instance, string name)
    {
        var value = GetMemberValue(instance, name);
        if (value == null) return null;
        if (value is bool boolValue) return boolValue;
        if (bool.TryParse(value.ToString(), out var parsed)) return parsed;
        return null;
    }

}
