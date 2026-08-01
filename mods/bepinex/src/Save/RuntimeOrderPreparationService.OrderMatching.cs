using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MystiaStewardCompanion.LocalApi;

namespace MystiaStewardCompanion.Save;

internal static partial class RuntimeOrderPreparationService
{
    private enum RuntimeOrderLookupPurpose
    {
        Delivery,
        Completion,
        NativeEvaluation,
        YuumaSettlement,
    }

    private static RuntimeOrderMatch FindRuntimeOrder(
        OrderPreparationRequest request,
        RuntimeOrderLookupPurpose purpose = RuntimeOrderLookupPurpose.Delivery)
    {
        var manager = GetSingletonInstance(GuestsManagerTypeName);
        if (manager == null)
        {
            return new RuntimeOrderMatch
            {
                Diagnostic = "GuestsManager singleton unavailable",
            };
        }

        var requiresLiveKoishiBoss = RequiresLiveWackyKoishiBossController(request);
        var requiresLiveYuyukoPhase3Boss = purpose == RuntimeOrderLookupPurpose.NativeEvaluation
            && RequiresLiveYuyukoPhase3BossController(request);
        var requiresYuyukoStoryManualEvaluation = purpose == RuntimeOrderLookupPurpose.NativeEvaluation
            && RequiresStoryYuyukoPhase3ManualEvaluation(request);
        var requiresYuumaSettlementManualContext = purpose == RuntimeOrderLookupPurpose.YuumaSettlement;
        var captured = requiresLiveKoishiBoss
            ? new RuntimeOrderMatch { Diagnostic = BuildWackyKoishiCaptureSkippedDiagnostic("capturedSkipped") }
            : FindCapturedRuntimeOrder(request, manager, purpose);
        if (!requiresLiveKoishiBoss && captured.Order != null && captured.Controller != null)
        {
            if (!requiresLiveYuyukoPhase3Boss)
            {
                AppendWackyBossRuntimeDiagnostic("rare-captured-match", request, captured, "accept", captured.Diagnostic);
                AppendYuyukoRuntimeDiagnostic("rare-captured-match", request, captured, "accept", captured.Diagnostic);
                return captured;
            }

            if (IsMatchingYuyukoPhase3EvaluationOrder(
                    captured.Order,
                    captured.Controller,
                    request,
                    out var capturedYuyukoDiagnostic))
            {
                var capturedMatch = new RuntimeOrderMatch
                {
                    Manager = captured.Manager,
                    Controller = captured.Controller,
                    Order = captured.Order,
                    ManualEvaluationCallback = requiresYuyukoStoryManualEvaluation
                        ? captured.ManualEvaluationCallback
                        : null,
                    Diagnostic = $"{captured.Diagnostic}; yuyukoPhase3Capture=validated; {capturedYuyukoDiagnostic}",
                };
                AppendYuyukoRuntimeDiagnostic(
                    "yuyuko-captured-evaluation-candidate",
                    request,
                    capturedMatch,
                    "accept",
                    capturedMatch.Diagnostic);
                return capturedMatch;
            }

            AppendYuyukoRuntimeDiagnostic(
                "yuyuko-captured-evaluation-candidate",
                request,
                captured,
                "reject",
                capturedYuyukoDiagnostic);
            captured = new RuntimeOrderMatch
            {
                Diagnostic = $"{captured.Diagnostic}; yuyukoPhase3CaptureRejected={capturedYuyukoDiagnostic}",
            };
        }

        var scannedControllers = 0;
        var scannedOrders = 0;
        var liveRejections = new List<string>();
        foreach (var controller in EnumerateGuestControllers(manager))
        {
            scannedControllers++;
            if (controller == null) continue;
            foreach (var enumeratedOrder in EnumerateControllerOrders(controller))
            {
                scannedOrders++;
                var order = NormalizeRuntimeSpecialOrder(enumeratedOrder);
                try
                {
                    if (requiresLiveYuyukoPhase3Boss)
                    {
                        if (!IsMatchingYuyukoPhase3EvaluationOrder(order, controller, request, out var yuyukoRejectReason))
                        {
                            if (liveRejections.Count < 4) liveRejections.Add(yuyukoRejectReason);
                            AppendYuyukoRuntimeDiagnostic(
                                "yuyuko-live-evaluation-candidate",
                                request,
                                new RuntimeOrderMatch { Manager = manager, Controller = controller, Order = order },
                                "reject",
                                yuyukoRejectReason);
                            continue;
                        }

                        AppendYuyukoRuntimeDiagnostic(
                            "yuyuko-live-evaluation-candidate",
                            request,
                            new RuntimeOrderMatch { Manager = manager, Controller = controller, Order = order },
                            "accept",
                            yuyukoRejectReason);
                    }
                    else if (!IsMatchingSpecialOrder(order, controller, request, purpose, out var specialRejectReason))
                    {
                        if (liveRejections.Count < 4) liveRejections.Add(specialRejectReason);
                        AppendYuyukoRuntimeDiagnostic(
                            "rare-live-candidate-rejected",
                            request,
                            new RuntimeOrderMatch { Manager = manager, Controller = controller, Order = order },
                            "reject",
                            specialRejectReason);
                        continue;
                    }
                }
                catch
                {
                    continue;
                }

                if (requiresLiveKoishiBoss
                    && !IsExecutableWackyKoishiBossRuntimeOrder(controller, order, out var rejectReason))
                {
                    AppendWackyBossRuntimeDiagnostic(
                        "rare-live-candidate-rejected",
                        request,
                        new RuntimeOrderMatch { Manager = manager, Controller = controller, Order = order },
                        "reject",
                        rejectReason);
                    continue;
                }

                var manualOrder = false;
                object? manualEvaluationCallback = null;
                var manualContextDiagnostic = "manual-context=not-required";
                if (requiresYuumaSettlementManualContext
                    && !TryResolveSpecialManualContext(
                        order,
                        controller,
                        out manualOrder,
                        out manualEvaluationCallback,
                        out manualContextDiagnostic))
                {
                    if (liveRejections.Count < 4) liveRejections.Add(manualContextDiagnostic);
                    continue;
                }

                if (requiresYuyukoStoryManualEvaluation)
                {
                    manualEvaluationCallback = FindCapturedYuyukoPhase3ManualEvaluationCallback(
                        order,
                        controller,
                        out manualContextDiagnostic);
                }

                var match = new RuntimeOrderMatch
                {
                    Manager = manager,
                    Controller = controller,
                    Order = order,
                    ManualOrder = requiresYuumaSettlementManualContext && manualOrder,
                    ManualEvaluationCallback = requiresYuumaSettlementManualContext
                        ? manualOrder ? manualEvaluationCallback : null
                        : requiresYuyukoStoryManualEvaluation ? manualEvaluationCallback : null,
                    Diagnostic = IsWackyKoishiBossRequest(request)
                        ? $"{DescribeWackyKoishiExecutionMode(request)}, liveController=ok, scannedControllers={scannedControllers}, scannedOrders={scannedOrders}, {manualContextDiagnostic}"
                        : requiresLiveYuyukoPhase3Boss
                            ? $"yuyuko phase3 liveController=ok, purpose={purpose}, evaluationMode={ResolveYuyukoPhase3EvaluationMode(request)}, scannedControllers={scannedControllers}, scannedOrders={scannedOrders}, {manualContextDiagnostic}"
                        : $"requestIdentity=({FormatRequestOrderIdentity(request)}), liveIdentity=matched, scannedControllers={scannedControllers}, scannedOrders={scannedOrders}, {manualContextDiagnostic}",
                };
                AppendWackyBossRuntimeDiagnostic("rare-live-match", request, match, "accept", match.Diagnostic);
                AppendYuyukoRuntimeDiagnostic("rare-live-match", request, match, "accept", match.Diagnostic);
                return match;
            }
        }

        return new RuntimeOrderMatch
        {
            Diagnostic = $"requestIdentity=({FormatRequestOrderIdentity(request)}), captured={captured.Diagnostic}, scannedControllers={scannedControllers}, scannedOrders={scannedOrders}, liveRejected=[{string.Join("; ", liveRejections)}]",
        };
    }

    private static RuntimeOrderMatch FindRuntimeNormalOrder(
        OrderPreparationRequest request,
        RuntimeOrderLookupPurpose purpose = RuntimeOrderLookupPurpose.Delivery)
    {
        var manager = GetSingletonInstance(GuestsManagerTypeName);
        if (manager == null) return new RuntimeOrderMatch();

        var requiresLiveKoishiBoss = RequiresLiveWackyKoishiBossController(request);
        var requiresYuumaSettlementManualContext = purpose == RuntimeOrderLookupPurpose.YuumaSettlement;
        var captured = FindCapturedRuntimeNormalOrder(request, manager, purpose);
        if (captured.Order != null && captured.Controller != null)
        {
            if (!requiresLiveKoishiBoss
                || IsExecutableWackyKoishiBossRuntimeOrder(captured.Controller, captured.Order, out var capturedRejectReason))
            {
                AppendWackyBossRuntimeDiagnostic("normal-captured-match", request, captured, "accept", captured.Diagnostic);
                return captured;
            }

            AppendWackyBossRuntimeDiagnostic(
                "normal-captured-candidate-rejected",
                request,
                captured,
                "reject",
                capturedRejectReason);
        }

        var scannedControllers = 0;
        var scannedControllerOrders = 0;
        var manualContextRejections = new List<string>();
        foreach (var controller in EnumerateGuestControllers(manager))
        {
            scannedControllers++;
            if (controller == null) continue;
            foreach (var order in EnumerateControllerOrders(controller))
            {
                scannedControllerOrders++;
                try
                {
                    if (!IsMatchingNormalOrder(order, request, controller)) continue;
                }
                catch
                {
                    continue;
                }

                if (requiresLiveKoishiBoss
                    && !IsExecutableWackyKoishiBossRuntimeOrder(controller, order, out var rejectReason))
                {
                    AppendWackyBossRuntimeDiagnostic(
                        "normal-live-candidate-rejected",
                        request,
                        new RuntimeOrderMatch { Manager = manager, Controller = controller, Order = order },
                        "reject",
                        rejectReason);
                    continue;
                }

                var manualOrder = false;
                object? manualEvaluationCallback = null;
                var manualContextDiagnostic = "manual-context=not-required";
                if (requiresYuumaSettlementManualContext
                    && !TryResolveNormalManualContext(
                        order,
                        controller,
                        out manualOrder,
                        out manualEvaluationCallback,
                        out manualContextDiagnostic))
                {
                    if (manualContextRejections.Count < 4) manualContextRejections.Add(manualContextDiagnostic);
                    continue;
                }

                var match = new RuntimeOrderMatch
                {
                    Manager = manager,
                    Controller = controller,
                    Order = order,
                    ManualOrder = requiresYuumaSettlementManualContext && manualOrder,
                    ManualEvaluationCallback = requiresYuumaSettlementManualContext && manualOrder
                        ? manualEvaluationCallback
                        : null,
                    Diagnostic = IsWackyKoishiBossRequest(request)
                        ? $"{DescribeWackyKoishiExecutionMode(request)}, liveController=ok, controllerOrders={scannedControllerOrders}, {manualContextDiagnostic}"
                        : $"controllerOrders={scannedControllerOrders}, {manualContextDiagnostic}",
                };
                AppendWackyBossRuntimeDiagnostic("normal-live-match", request, match, "accept", match.Diagnostic);
                return match;
            }
        }

        var scannedUiOrders = 0;
        foreach (var order in EnumerateOrderControllerOrders())
        {
            scannedUiOrders++;
            if (!IsMatchingNormalOrder(order, request)) continue;

            var controller = FindControllerForOrder(manager, order, request);
            if (controller != null && !SpecialBusinessOrderClassifier.Classify(order, controller).AutomationAllowed)
            {
                continue;
            }

            if (requiresLiveKoishiBoss)
            {
                var candidate = new RuntimeOrderMatch { Manager = manager, Controller = controller, Order = order };
                if (!IsExecutableWackyKoishiBossRuntimeOrder(controller, order, out var rejectReason))
                {
                    AppendWackyBossRuntimeDiagnostic(
                        "normal-ui-candidate-rejected",
                        request,
                        candidate,
                        "reject",
                        rejectReason);
                    continue;
                }
            }

            var manualOrder = false;
            object? manualEvaluationCallback = null;
            var manualContextDiagnostic = "manual-context=not-required";
            if (requiresYuumaSettlementManualContext
                && !TryResolveNormalManualContext(
                    order,
                    controller,
                    out manualOrder,
                    out manualEvaluationCallback,
                    out manualContextDiagnostic))
            {
                if (manualContextRejections.Count < 4) manualContextRejections.Add(manualContextDiagnostic);
                continue;
            }

            var match = new RuntimeOrderMatch
            {
                Manager = manager,
                Controller = controller,
                Order = order,
                ManualOrder = requiresYuumaSettlementManualContext && manualOrder,
                ManualEvaluationCallback = requiresYuumaSettlementManualContext && manualOrder
                    ? manualEvaluationCallback
                    : null,
                Diagnostic = $"controllers={scannedControllers}, controllerOrders={scannedControllerOrders}, captured=({captured.Diagnostic}), uiOrders={scannedUiOrders}, uiController={(controller == null ? "missing" : "ok")}, {manualContextDiagnostic}",
            };
            AppendWackyBossRuntimeDiagnostic("normal-ui-match", request, match, "accept", match.Diagnostic);
            return match;
        }

        return new RuntimeOrderMatch
        {
            Diagnostic = $"controllers={scannedControllers}, controllerOrders={scannedControllerOrders}, captured=({captured.Diagnostic}), uiOrders={scannedUiOrders}, manualRejected=[{string.Join("; ", manualContextRejections)}]",
        };
    }

    private static IEnumerable<object> EnumerateOrderControllerOrders()
    {
        var orderControllerType = FindType(OrderControllerTypeName);
        if (orderControllerType == null) yield break;

        object? showOrders = null;
        try
        {
            showOrders = InvokeStatic(OrderControllerTypeName, "GetShowInUIOrders", Array.Empty<object?>());
        }
        catch
        {
            // Try active UI elements below.
        }

        foreach (var order in ReadObjectEnumerable(showOrders))
        {
            yield return order;
        }

        object? controller = null;
        try
        {
            controller = GetSingletonInstance(OrderControllerTypeName);
        }
        catch
        {
            // Static instance may not exist before the HUD is built.
        }

        if (controller == null) yield break;

        foreach (var element in ReadObjectEnumerable(ReadMember(controller, "m_Orders")))
        {
            var activeOrder = ReadMember(NormalizeDictionaryItem(element) ?? element, "ActiveOrder");
            if (activeOrder != null) yield return activeOrder;
        }
    }

    private static object? FindControllerForOrder(object manager, object order, OrderPreparationRequest request)
    {
        foreach (var controller in EnumerateGuestControllers(manager))
        {
            if (controller == null) continue;
            foreach (var candidate in EnumerateControllerOrders(controller))
            {
                if (IsSameObject(candidate, order)) return controller;
            }
        }

        foreach (var controller in EnumerateGuestControllers(manager))
        {
            if (controller == null) continue;
            if (ToInt(ReadMember(controller, "DeskCode") ?? TryInvokeInstanceValue(controller, "get_DeskCode"), -999) != request.DeskCode) continue;
            if (EnumerateControllerOrders(controller).Any(candidate => IsMatchingNormalOrder(candidate, request, controller)))
            {
                return controller;
            }
        }

        return null;
    }

    private static bool IsMatchingNormalOrder(object order, OrderPreparationRequest request, object? controller = null)
    {
        if (!IsNormalOrder(order)) return false;
        if (!IsSpecialBusinessOrderAllowedForRequest(order, controller, request, out _)) return false;
        if (!string.IsNullOrWhiteSpace(request.OrderKey)
            && !string.Equals(BuildRuntimeOrderKey(order), request.OrderKey, StringComparison.Ordinal))
        {
            return false;
        }

        var deskCode = ToInt(ReadMember(order, "DeskCode") ?? TryInvokeInstanceValue(order, "get_DeskCode"), -999);
        if (request.DeskCode >= 0 && deskCode != request.DeskCode) return false;

        var matchFoodId = GetNormalMatchFoodId(request);
        var matchBeverageId = GetNormalMatchBeverageId(request);
        if (matchFoodId >= 0 && ReadNormalFoodId(order) != matchFoodId) return false;
        if (matchBeverageId >= 0 && ReadNormalBeverageId(order) != matchBeverageId) return false;
        return true;
    }

    private static int GetNormalMatchFoodId(OrderPreparationRequest request)
    {
        return request.MatchFoodId >= 0 ? request.MatchFoodId : request.FoodId;
    }

    private static int GetNormalMatchBeverageId(OrderPreparationRequest request)
    {
        return request.MatchBeverageId >= 0 ? request.MatchBeverageId : request.BeverageId;
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

    private static bool IsNormalOrder(object order)
    {
        if (IsSpecialOrder(order)) return false;
        var typeName = order.GetType().Name;
        if (typeName.IndexOf("NormalOrder", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        var type = ReadMember(order, "Type") ?? TryInvokeInstanceValue(order, "get_Type");
        return type?.ToString()?.Contains("Normal", StringComparison.OrdinalIgnoreCase) == true || ToInt(type, -1) == 0;
    }

    private static int ReadNormalFoodId(object order)
    {
        return ReadNormalSellableId(
            ReadMember(order, "RequestFood") ?? TryInvokeInstanceValue(order, "get_RequestFood"),
            ReadMember(order, "foodRequest"));
    }

    private static int ReadNormalBeverageId(object order)
    {
        return ReadNormalSellableId(
            ReadMember(order, "RequestBeverage") ?? TryInvokeInstanceValue(order, "get_RequestBeverage"),
            ReadMember(order, "beverageRequest"));
    }

    private static int ReadNormalSellableId(object? sellable, object? fallback)
    {
        if (sellable != null)
        {
            foreach (var member in new[] { "id", "Id", "ID", "foodID", "FoodID" })
            {
                var parsed = ToInt(ReadMember(sellable, member) ?? TryInvokeInstanceValue(sellable, $"get_{member}"), int.MinValue);
                if (parsed != int.MinValue) return parsed;
            }
        }

        return ToInt(fallback, -1);
    }

    private enum RuntimeObjectIdentityComparison
    {
        Same,
        Different,
        Unknown,
    }

    private static RuntimeObjectIdentityComparison CompareObjectIdentity(object left, object right)
    {
        if (ReferenceEquals(left, right)) return RuntimeObjectIdentityComparison.Same;

        var leftReadable = TryReadNativeObjectPointer(left, out var leftPointer);
        var rightReadable = TryReadNativeObjectPointer(right, out var rightPointer);
        if (!leftReadable || !rightReadable) return RuntimeObjectIdentityComparison.Unknown;

        return leftPointer == rightPointer
            ? RuntimeObjectIdentityComparison.Same
            : RuntimeObjectIdentityComparison.Different;
    }

    private static bool IsSameObject(object left, object right)
    {
        return CompareObjectIdentity(left, right) == RuntimeObjectIdentityComparison.Same;
    }

    private static RuntimeOrderMatch FindCapturedRuntimeOrder(
        OrderPreparationRequest request,
        object manager,
        RuntimeOrderLookupPurpose purpose)
    {
        var capturedOrders = SpecialOrderRuntimeCapture.Snapshot(TimeSpan.FromHours(6));
        var identityCandidates = capturedOrders
            .Select(captured =>
            {
                var matched = TryMatchCapturedOrderIdentity(captured, request, out var rejectReason);
                return new CapturedOrderIdentityEvaluation
                {
                    Order = captured,
                    Matched = matched,
                    RejectReason = rejectReason,
                };
            })
            .ToList();
        var candidates = identityCandidates
            .Where(candidate => candidate.Matched)
            .OrderBy(candidate => candidate.Order.FirstCapturedAt)
            .ThenBy(candidate => candidate.Order.CapturedAt)
            .ToList();
        var liveRejections = new List<string>();

        foreach (var candidate in candidates)
        {
            var captured = candidate.Order;
            var requiresYuumaSettlementManualContext = purpose == RuntimeOrderLookupPurpose.YuumaSettlement;
            if (captured.OrderObject == null || captured.ControllerObject == null)
            {
                liveRejections.Add($"{FormatCapturedOrderIdentity(captured)}: captured order/controller missing");
                continue;
            }

            if (!IsCapturedSpecialOrderLive(captured, request, purpose, out var liveRejectReason))
            {
                liveRejections.Add($"{FormatCapturedOrderIdentity(captured)}: {liveRejectReason}");
                continue;
            }

            var manualOrder = false;
            object? manualEvaluationCallback = null;
            var manualContextDiagnostic = "manual-context=not-required";
            if (requiresYuumaSettlementManualContext
                && !TryResolveSpecialManualContext(
                    captured.OrderObject,
                    captured.ControllerObject,
                    out manualOrder,
                    out manualEvaluationCallback,
                    out manualContextDiagnostic))
            {
                liveRejections.Add($"{FormatCapturedOrderIdentity(captured)}: {manualContextDiagnostic}");
                continue;
            }

            return new RuntimeOrderMatch
            {
                Manager = manager,
                Controller = captured.ControllerObject,
                Order = captured.OrderObject,
                ManualOrder = requiresYuumaSettlementManualContext && manualOrder,
                ManualEvaluationCallback = requiresYuumaSettlementManualContext
                    ? manualOrder ? manualEvaluationCallback : null
                    : captured.ManualEvaluationCallback,
                Diagnostic = $"capturedCandidates={candidates.Count}, identity=({FormatCapturedOrderIdentity(captured)}), display=({FormatCapturedOrderDisplay(captured)}), source={captured.CaptureSource}, capturedManual={captured.ManualOrder}, currentManual={manualOrder}, {manualContextDiagnostic}",
            };
        }

        return new RuntimeOrderMatch
        {
            Diagnostic = $"requestIdentity=({FormatRequestOrderIdentity(request)}), capturedCandidates={candidates.Count}, capturedTotal={capturedOrders.Count}, captured=[{FormatCapturedOrderSummary(identityCandidates)}], liveRejected=[{string.Join("; ", liveRejections.Take(4))}]",
        };
    }

    private static object? FindCapturedYuyukoPhase3ManualEvaluationCallback(
        object order,
        object controller,
        out string diagnostic)
    {
        var capturedOrders = SpecialOrderRuntimeCapture.Snapshot(TimeSpan.FromHours(6));
        var candidates = capturedOrders
            .Where(captured => captured.OrderObject != null
                && captured.ControllerObject != null
                && CompareObjectIdentity(captured.OrderObject, order) == RuntimeObjectIdentityComparison.Same
                && CompareObjectIdentity(captured.ControllerObject, controller) == RuntimeObjectIdentityComparison.Same)
            .OrderByDescending(captured => captured.CapturedAt)
            .ToList();
        var callbackCandidate = candidates.FirstOrDefault(candidate =>
            candidate.ManualEvaluationCallback != null);
        if (callbackCandidate == null)
        {
            diagnostic = $"manualCallback=missing, exactObjectCandidates={candidates.Count}, capturedTotal={capturedOrders.Count}";
            return null;
        }

        var callback = callbackCandidate.ManualEvaluationCallback;
        diagnostic = $"manualCallback=captured, exactObjectCandidates={candidates.Count}, identity=({FormatCapturedOrderIdentity(callbackCandidate)}), display=({FormatCapturedOrderDisplay(callbackCandidate)}), source={callbackCandidate.CaptureSource}, callback={YuyukoChallengeEvaluationTracker.DescribeCallback(callback)}";
        return callback;
    }

    private static bool TryResolveSpecialManualContext(
        object order,
        object controller,
        out bool manualOrder,
        out object? manualEvaluationCallback,
        out string diagnostic)
    {
        manualEvaluationCallback = null;
        if (!TryReadExactManualOrder(order, out manualOrder, out var readDiagnostic))
        {
            diagnostic = $"manual=unavailable, {readDiagnostic}";
            return false;
        }

        if (!manualOrder)
        {
            diagnostic = "manual=false, manualCallback=not-required";
            return true;
        }

        var capturedOrders = SpecialOrderRuntimeCapture.Snapshot(TimeSpan.FromHours(6));
        var candidates = capturedOrders
            .Where(captured => captured.ManualOrder
                && captured.OrderObject != null
                && captured.ControllerObject != null
                && CompareObjectIdentity(captured.OrderObject, order) == RuntimeObjectIdentityComparison.Same
                && CompareObjectIdentity(captured.ControllerObject, controller) == RuntimeObjectIdentityComparison.Same)
            .OrderByDescending(captured => captured.CapturedAt)
            .ToList();
        var callbackCandidate = candidates.FirstOrDefault(candidate =>
            candidate.ManualEvaluationCallback != null
            && HasCaptureSource(candidate.CaptureSource, "ManualOrderSet"));
        if (callbackCandidate == null)
        {
            diagnostic = $"manual=true, manualCallback=missing, exactObjectCandidates={candidates.Count}, capturedTotal={capturedOrders.Count}";
            return false;
        }

        manualEvaluationCallback = callbackCandidate.ManualEvaluationCallback;
        diagnostic = $"manual=true, manualCallback=captured, exactObjectCandidates={candidates.Count}, identity=({FormatCapturedOrderIdentity(callbackCandidate)}), display=({FormatCapturedOrderDisplay(callbackCandidate)}), source={callbackCandidate.CaptureSource}, callback={YuyukoChallengeEvaluationTracker.DescribeCallback(manualEvaluationCallback)}";
        return true;
    }

    private static bool TryResolveNormalManualContext(
        object order,
        object? controller,
        out bool manualOrder,
        out object? manualEvaluationCallback,
        out string diagnostic)
    {
        manualEvaluationCallback = null;
        if (!TryReadExactManualOrder(order, out manualOrder, out var readDiagnostic))
        {
            diagnostic = $"manual=unavailable, {readDiagnostic}";
            return false;
        }

        if (!manualOrder)
        {
            diagnostic = "manual=false, manualCallback=not-required";
            return true;
        }

        if (controller == null)
        {
            diagnostic = "manual=true, controller=missing, manualCallback=unresolved";
            return false;
        }

        var capturedOrders = NormalOrderRuntimeCapture.Snapshot(TimeSpan.FromHours(6));
        var candidates = capturedOrders
            .Where(captured => captured.ManualOrder
                && captured.OrderObject != null
                && captured.ControllerObject != null
                && CompareObjectIdentity(captured.OrderObject, order) == RuntimeObjectIdentityComparison.Same
                && CompareObjectIdentity(captured.ControllerObject, controller) == RuntimeObjectIdentityComparison.Same)
            .OrderByDescending(captured => captured.CapturedAt)
            .ToList();
        var callbackCandidate = candidates.FirstOrDefault(candidate =>
            candidate.ManualEvaluationCallback != null
            && HasCaptureSource(candidate.CaptureSource, "ManualOrderSet"));
        if (callbackCandidate == null)
        {
            diagnostic = $"manual=true, manualCallback=missing, exactObjectCandidates={candidates.Count}, capturedTotal={capturedOrders.Count}";
            return false;
        }

        manualEvaluationCallback = callbackCandidate.ManualEvaluationCallback;
        diagnostic = $"manual=true, manualCallback=captured, exactObjectCandidates={candidates.Count}, source={callbackCandidate.CaptureSource}";
        return true;
    }

    private static bool TryReadExactManualOrder(
        object order,
        out bool manualOrder,
        out string diagnostic)
    {
        manualOrder = false;
        const BindingFlags flags = BindingFlags.Public
            | BindingFlags.NonPublic
            | BindingFlags.Instance
            | BindingFlags.DeclaredOnly;

        for (var type = order.GetType(); type != null; type = type.BaseType)
        {
            var property = type.GetProperty("ManualOrder", flags);
            if (property == null) continue;
            if (property.PropertyType != typeof(bool))
            {
                diagnostic = $"OrderBase.ManualOrder type={property.PropertyType.FullName}";
                return false;
            }

            try
            {
                if (property.GetValue(order) is not bool value)
                {
                    diagnostic = "OrderBase.ManualOrder value is not System.Boolean";
                    return false;
                }

                manualOrder = value;
                diagnostic = "OrderBase.ManualOrder=read";
                return true;
            }
            catch (Exception ex)
            {
                diagnostic = $"OrderBase.ManualOrder read failed: {ex.GetBaseException().Message}";
                return false;
            }
        }

        diagnostic = "OrderBase.ManualOrder property missing";
        return false;
    }

    private static bool HasCaptureSource(string captureSource, string expectedSource)
    {
        return captureSource
            .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(expectedSource, StringComparer.Ordinal);
    }

    private static RuntimeOrderMatch FindCapturedRuntimeNormalOrder(
        OrderPreparationRequest request,
        object manager,
        RuntimeOrderLookupPurpose purpose)
    {
        var capturedOrders = NormalOrderRuntimeCapture.Snapshot(TimeSpan.FromHours(6));
        var candidates = capturedOrders
            .Select(captured => new
            {
                Order = captured,
                Score = ScoreCapturedNormalOrder(captured, request),
            })
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Order.FirstCapturedAt)
            .ThenBy(candidate => candidate.Order.CapturedAt)
            .ToList();

        foreach (var candidate in candidates)
        {
            var captured = candidate.Order;
            if (captured.OrderObject == null || captured.ControllerObject == null) continue;

            var requiresYuumaSettlementManualContext = purpose == RuntimeOrderLookupPurpose.YuumaSettlement;
            if (requiresYuumaSettlementManualContext)
            {
                try
                {
                    var ownedByController = EnumerateControllerOrders(captured.ControllerObject)
                        .Any(order => CompareObjectIdentity(order, captured.OrderObject)
                            == RuntimeObjectIdentityComparison.Same);
                    if (!ownedByController) continue;
                }
                catch
                {
                    continue;
                }
            }

            try
            {
                if (!IsMatchingNormalOrder(captured.OrderObject, request, captured.ControllerObject)) continue;
            }
            catch
            {
                continue;
            }

            var manualOrder = false;
            object? manualEvaluationCallback = null;
            var manualContextDiagnostic = "manual-context=not-required";
            if (requiresYuumaSettlementManualContext
                && !TryResolveNormalManualContext(
                    captured.OrderObject,
                    captured.ControllerObject,
                    out manualOrder,
                    out manualEvaluationCallback,
                    out manualContextDiagnostic))
            {
                continue;
            }

            return new RuntimeOrderMatch
            {
                Manager = manager,
                Controller = captured.ControllerObject,
                Order = captured.OrderObject,
                ManualOrder = requiresYuumaSettlementManualContext && manualOrder,
                ManualEvaluationCallback = requiresYuumaSettlementManualContext && manualOrder
                    ? manualEvaluationCallback
                    : null,
                Diagnostic = $"normalCapturedCandidates={candidates.Count}, score={candidate.Score}, source={captured.CaptureSource}, capturedManual={captured.ManualOrder}, currentManual={manualOrder}, {manualContextDiagnostic}",
            };
        }

        return new RuntimeOrderMatch
        {
            Diagnostic = $"normalCapturedCandidates={candidates.Count}, normalCapturedTotal={capturedOrders.Count}, normalCaptured=[{FormatCapturedNormalOrderSummary(capturedOrders)}]",
        };
    }

    private static int ScoreCapturedNormalOrder(CapturedRuntimeNormalOrder captured, OrderPreparationRequest request)
    {
        if (captured.OrderObject == null || captured.ControllerObject == null) return 0;

        var score = 0;
        if (!string.IsNullOrWhiteSpace(request.OrderKey) && !string.IsNullOrWhiteSpace(captured.RuntimeKey))
        {
            score += string.Equals(request.OrderKey, captured.RuntimeKey, StringComparison.Ordinal) ? 32 : -12;
        }

        if (request.DeskCode >= 0 && captured.DeskCode >= 0)
        {
            score += request.DeskCode == captured.DeskCode ? 12 : -8;
        }

        var matchFoodId = GetNormalMatchFoodId(request);
        var matchBeverageId = GetNormalMatchBeverageId(request);
        if (matchFoodId >= 0 && captured.FoodId >= 0)
        {
            score += matchFoodId == captured.FoodId ? 8 : -4;
        }

        if (matchBeverageId >= 0 && captured.BeverageId >= 0)
        {
            score += matchBeverageId == captured.BeverageId ? 8 : -4;
        }

        return score >= 16 ? score : 0;
    }

    private static string FormatCapturedNormalOrderSummary(IReadOnlyList<CapturedRuntimeNormalOrder> capturedOrders)
    {
        if (capturedOrders.Count == 0) return "";

        var items = capturedOrders
            .Take(4)
            .Select(order => $"desk={order.DeskCode + 1},guest={order.GuestName},food={order.FoodId},bev={order.BeverageId},source={order.CaptureSource},manual={order.ManualOrder},manualCallback={(order.ManualEvaluationCallback == null ? "no" : "yes")},obj={(order.OrderObject == null ? "no" : "yes")}/{(order.ControllerObject == null ? "no" : "yes")}")
            .ToArray();
        var suffix = capturedOrders.Count > items.Length ? $" ... total={capturedOrders.Count}" : "";
        return string.Join("; ", items) + suffix;
    }

    private static bool TryMatchCapturedOrderIdentity(
        CapturedRuntimeSpecialOrder captured,
        OrderPreparationRequest request,
        out string rejectReason)
    {
        return RareOrderIdentityMatcher.Matches(
            BuildRequestOrderIdentity(request),
            new RareOrderIdentity(
                captured.DeskCode >= 0 ? captured.DeskCode : null,
                captured.GuestId,
                captured.HasFoodTagId ? captured.FoodTagId : null,
                captured.HasBeverageTagId ? captured.BeverageTagId : null),
            out rejectReason);
    }

    private static bool TryMatchLiveSpecialOrderIdentity(
        object order,
        object controller,
        OrderPreparationRequest request,
        out string rejectReason)
    {
        if (!TryBuildLiveSpecialOrderIdentity(order, controller, out var candidate, out rejectReason))
        {
            return false;
        }

        return RareOrderIdentityMatcher.Matches(
            BuildRequestOrderIdentity(request),
            candidate,
            out rejectReason);
    }

    private static bool TryBuildLiveSpecialOrderIdentity(
        object order,
        object controller,
        out RareOrderIdentity identity,
        out string rejectReason)
    {
        var readableOrder = NormalizeRuntimeSpecialOrder(order);
        var orderDeskCode = TryReadInt(
            ReadMember(readableOrder, "DeskCode") ?? TryInvokeInstanceValue(readableOrder, "get_DeskCode"));
        var controllerDeskCode = TryReadInt(
            ReadMember(controller, "DeskCode") ?? TryInvokeInstanceValue(controller, "get_DeskCode"));
        if (orderDeskCode.HasValue
            && controllerDeskCode.HasValue
            && orderDeskCode.Value != controllerDeskCode.Value)
        {
            identity = default;
            rejectReason = $"order/controller desk conflict order={orderDeskCode.Value}, controller={controllerDeskCode.Value}";
            return false;
        }

        var orderGuestId = TryReadGuestId(
            ReadMember(readableOrder, "SpecialGuests") ?? TryInvokeInstanceValue(readableOrder, "get_SpecialGuests"));
        var controllerGuestId = TryReadGuestId(
            ReadMember(controller, "SpecialGuest")
                ?? TryInvokeInstanceValue(controller, "get_SpecialGuest")
                ?? ReadMember(controller, "OrderingGuest")
                ?? TryInvokeInstanceValue(controller, "get_OrderingGuest"));

        identity = new RareOrderIdentity(
            orderDeskCode ?? controllerDeskCode,
            orderGuestId ?? controllerGuestId,
            TryReadSpecialOrderTagId(readableOrder, isFood: true),
            TryReadSpecialOrderTagId(readableOrder, isFood: false));
        rejectReason = "";
        return true;
    }

    private static object NormalizeRuntimeSpecialOrder(object order)
    {
        return RuntimeReflectionUtility.TryCastRuntimeObject(order, SpecialOrderTypeName) ?? order;
    }

    private static int? TryReadSpecialOrderTagId(object order, bool isFood)
    {
        var memberName = isFood ? "RequestFoodTag" : "RequestBeverageTag";
        var raw = ReadMember(order, memberName) ?? TryInvokeInstanceValue(order, $"get_{memberName}");
        return TryReadInt(raw);
    }

    private static int? TryReadGuestId(object? guest)
    {
        if (guest == null) return null;
        return TryReadInt(
            TryInvokeInstanceValue(guest, "get_Id")
                ?? ReadMember(guest, "Id"));
    }

    private static int? TryReadInt(object? value)
    {
        if (value == null) return null;
        try
        {
            if (value is int number) return number;
            if (value is Enum enumValue) return Convert.ToInt32(enumValue);
            if (value is IConvertible convertible) return Convert.ToInt32(convertible);
            return int.TryParse(value.ToString(), out var parsed) ? parsed : null;
        }
        catch
        {
            return null;
        }
    }

    private static RareOrderIdentity BuildRequestOrderIdentity(OrderPreparationRequest request)
    {
        return new RareOrderIdentity(
            request.DeskCode >= 0 ? request.DeskCode : null,
            request.RuntimeGuestId,
            request.FoodTagId,
            request.BeverageTagId);
    }

    private static string FormatRequestOrderIdentity(OrderPreparationRequest request)
    {
        return RareOrderIdentityMatcher.Format(BuildRequestOrderIdentity(request));
    }

    private static string FormatCapturedOrderIdentity(CapturedRuntimeSpecialOrder captured)
    {
        return RareOrderIdentityMatcher.Format(new RareOrderIdentity(
            captured.DeskCode >= 0 ? captured.DeskCode : null,
            captured.GuestId,
            captured.HasFoodTagId ? captured.FoodTagId : null,
            captured.HasBeverageTagId ? captured.BeverageTagId : null));
    }

    private static string FormatCapturedOrderDisplay(CapturedRuntimeSpecialOrder captured)
    {
        return $"food={captured.FoodTagDisplayText},beverage={captured.BeverageTagDisplayText}";
    }

    private static string FormatCapturedOrderSummary(IReadOnlyList<CapturedOrderIdentityEvaluation> capturedOrders)
    {
        if (capturedOrders.Count == 0) return "";

        var items = capturedOrders
            .Take(4)
            .Select(candidate =>
            {
                var order = candidate.Order;
                return $"identity=({FormatCapturedOrderIdentity(order)}),display=({FormatCapturedOrderDisplay(order)}),match={(candidate.Matched ? "yes" : "no")},reason={candidate.RejectReason},source={order.CaptureSource},obj={(order.OrderObject == null ? "no" : "yes")}/{(order.ControllerObject == null ? "no" : "yes")},manual={order.ManualOrder},manualCallback={(order.ManualEvaluationCallback == null ? "no" : "yes")}";
            })
            .ToArray();
        var suffix = capturedOrders.Count > items.Length ? $" ... total={capturedOrders.Count}" : "";
        return string.Join("; ", items) + suffix;
    }

    private static bool IsCapturedSpecialOrderLive(
        CapturedRuntimeSpecialOrder captured,
        OrderPreparationRequest request,
        RuntimeOrderLookupPurpose purpose,
        out string rejectReason)
    {
        var orderObject = captured.OrderObject;
        var controllerObject = captured.ControllerObject;
        var fulfilledValue = orderObject == null
            ? null
            : TryInvokeInstanceValue(orderObject, "get_IsFullfilled")
                ?? ReadMember(orderObject, "IsFullfilled");
        bool? fulfilled = fulfilledValue == null ? null : ReadBool(fulfilledValue);
        var ownedByController = false;

        if (orderObject != null && controllerObject != null)
        {
            try
            {
                ownedByController = EnumerateControllerOrders(controllerObject)
                    .Any(order => CompareObjectIdentity(order, orderObject) == RuntimeObjectIdentityComparison.Same);
            }
            catch (Exception ex)
            {
                rejectReason = $"captured controller orders unreadable: {ex.GetType().Name}";
                return false;
            }
        }

        if (!RareOrderIdentityMatcher.IsExecutableCapturedOrder(
                orderObject != null,
                controllerObject != null,
                fulfilled,
                ownedByController,
                allowFulfilled: purpose != RuntimeOrderLookupPurpose.Delivery,
                out rejectReason))
        {
            return false;
        }

        return IsMatchingSpecialOrder(orderObject!, controllerObject!, request, purpose, out rejectReason);
    }

    private static IEnumerable<object> EnumerateGuestControllers(object manager)
    {
        var seen = new HashSet<nint>();
        foreach (var name in new[]
                 {
                     "AllPresentedGuestGroupController",
                     "AllGuestInDeskController",
                     "AllGuestsControllersInDesk",
                     "CanPlayerRepellGuest",
                     "ManualDesksDic",
                 })
        {
            foreach (var item in ReadObjectEnumerable(ReadMember(manager, name)))
            {
                object? controller;
                nint pointer;
                try
                {
                    controller = NormalizeDictionaryItem(item);
                    if (controller == null) continue;
                    pointer = ReadObjectPointer(controller);
                }
                catch
                {
                    continue;
                }

                if (!seen.Add(pointer)) continue;
                yield return controller;
            }
        }

        foreach (var controller in EnumerateManualControlledGuestControllers())
        {
            nint pointer;
            try
            {
                pointer = ReadObjectPointer(controller);
            }
            catch
            {
                continue;
            }

            if (!seen.Add(pointer)) continue;
            yield return controller;
        }
    }

    private static IEnumerable<object> EnumerateManualControlledGuestControllers()
    {
        var director = GetSingletonInstance(NightSceneDirectorTypeName);
        if (director == null) yield break;

        foreach (var item in ReadObjectEnumerable(ReadMember(director, "controlledGuest")))
        {
            object? controller;
            try
            {
                controller = NormalizeDictionaryItem(item);
            }
            catch
            {
                continue;
            }

            if (controller != null) yield return controller;
        }
    }

    private static IEnumerable<object> EnumerateControllerOrders(object controller)
    {
        var seen = new HashSet<nint>();
        foreach (var name in new[] { "AllOrders", "AllOrdersData" })
        {
            foreach (var order in ReadControllerOrderCollection(ReadMember(controller, name)))
            {
                nint pointer;
                try
                {
                    pointer = ReadObjectPointer(order);
                }
                catch
                {
                    continue;
                }

                if (!seen.Add(pointer)) continue;
                yield return order;
            }
        }

        var peekOrder = TryInvokeInstanceValue(controller, "PeekOrders");
        if (peekOrder == null) yield break;

        var shouldYieldPeekOrder = false;
        try
        {
            shouldYieldPeekOrder = seen.Add(ReadObjectPointer(peekOrder));
        }
        catch
        {
            // Ignore stale IL2CPP order objects while scanning live controllers.
        }

        if (shouldYieldPeekOrder)
        {
            yield return peekOrder;
        }
    }

    private static IReadOnlyList<object> ReadControllerOrderCollection(object? value)
    {
        if (value == null || value is string) return Array.Empty<object>();

        var orders = new List<object>();
        try
        {
            var items = HasIl2CppEnumerator(value)
                ? EnumerateIl2Cpp(value)
                : ReadObjectEnumerable(value).Cast<object?>();
            foreach (var item in items)
            {
                if (item != null) orders.Add(item);
            }
        }
        catch
        {
            // PeekOrders below still provides an exact top-order fallback.
        }

        return orders;
    }

    private static object? NormalizeDictionaryItem(object item)
    {
        return ReadMember(item, "Value") ?? item;
    }

    private static bool IsMatchingSpecialOrder(
        object order,
        object controller,
        OrderPreparationRequest request,
        RuntimeOrderLookupPurpose purpose,
        out string rejectReason)
    {
        if (!IsSpecialOrder(order))
        {
            rejectReason = "not special order";
            return false;
        }

        if (!IsSpecialBusinessOrderAllowedForRequest(order, controller, request, out rejectReason))
        {
            return false;
        }

        var fulfilledValue = TryInvokeInstanceValue(order, "get_IsFullfilled") ?? ReadMember(order, "IsFullfilled");
        if (fulfilledValue == null)
        {
            rejectReason = "fulfilled state missing";
            return false;
        }

        if (ReadBool(fulfilledValue) && purpose == RuntimeOrderLookupPurpose.Delivery)
        {
            rejectReason = "order fulfilled";
            return false;
        }

        return TryMatchLiveSpecialOrderIdentity(order, controller, request, out rejectReason);
    }

    private static bool IsSpecialBusinessOrderAllowedForRequest(
        object order,
        object? controller,
        OrderPreparationRequest request,
        out string rejectReason)
    {
        var classification = SpecialBusinessOrderClassifier.Classify(
            order,
            controller,
            "RuntimeOrderPreparationService");
        if (!classification.AutomationAllowed)
        {
            rejectReason = classification.AutomationBlockReason.Length > 0
                ? classification.AutomationBlockReason
                : $"special business role {classification.Role} blocked automation";
            return false;
        }

        var requestIsYuumaBoss = IsYuumaBossRequest(request);
        var candidateIsYuumaBoss = string.Equals(
            classification.Role,
            SpecialBusinessOrderRoles.YuumaBoss,
            StringComparison.Ordinal);
        if (requestIsYuumaBoss != candidateIsYuumaBoss)
        {
            rejectReason = $"Yuuma role mismatch request={request.SpecialBusinessRole}; candidate={classification.Role}";
            return false;
        }

        if (candidateIsYuumaBoss)
        {
            var identity = YuumaChallengeOrderIdentity.Read(order, controller);
            if (!identity.Verified
                || identity.OrderGuestId != SpecialBusinessGuestIds.YuumaBoss
                || identity.ControllerGuestId != SpecialBusinessGuestIds.YuumaBoss)
            {
                rejectReason = $"Yuuma identity recheck failed: {identity.Reason}; order={identity.OrderGuestId}; controller={identity.ControllerGuestId}";
                return false;
            }
        }

        rejectReason = "";
        return true;
    }

    private static bool IsMatchingYuyukoPhase3EvaluationOrder(
        object order,
        object controller,
        OrderPreparationRequest request,
        out string rejectReason)
    {
        if (!IsSpecialOrder(order))
        {
            rejectReason = "not special order";
            return false;
        }

        if (!TryMatchLiveSpecialOrderIdentity(order, controller, request, out rejectReason))
        {
            return false;
        }

        var fulfilledValue = TryInvokeInstanceValue(order, "get_IsFullfilled") ?? ReadMember(order, "IsFullfilled");
        if (fulfilledValue == null)
        {
            rejectReason = "fulfilled state missing";
            return false;
        }

        if (!ReadBool(fulfilledValue))
        {
            rejectReason = "order not fulfilled";
            return false;
        }

        var evaluationCallback = ReadControllerCallback(controller, "OverrideEvaluationCallback");
        if (evaluationCallback == null)
        {
            rejectReason = "OverrideEvaluationCallback missing";
            return false;
        }

        var servedFood = ReadOrderServedFood(order);
        if (!ServedYuyukoPhase3TargetMatches(servedFood, sellableType: 0, request.FoodId, "food", out rejectReason))
        {
            return false;
        }

        var servedBeverage = ReadOrderServedBeverage(order);
        if (!ServedYuyukoPhase3TargetMatches(servedBeverage, sellableType: 1, request.BeverageId, "beverage", out rejectReason))
        {
            return false;
        }

        var servedFoodLevel = ReadSellableLevel(servedFood);
        var servedBeverageLevel = ReadSellableLevel(servedBeverage);
        var levelSum = servedFoodLevel >= 0 && servedBeverageLevel >= 0
            ? (servedFoodLevel + servedBeverageLevel).ToString()
            : "";
        _ = TryBuildLiveSpecialOrderIdentity(order, controller, out var matchedIdentity, out _);
        rejectReason = $"yuyuko phase3 evaluation signature matched; identity=({RareOrderIdentityMatcher.Format(matchedIdentity)}); callback=present; fulfilled=True; servedFood={FormatYuyukoServedTarget(servedFood)}; servedBeverage={FormatYuyukoServedTarget(servedBeverage)}; servedLevelSum={levelSum}";
        return true;
    }

    private static bool ServedYuyukoPhase3TargetMatches(
        object? sellable,
        int sellableType,
        int expectedId,
        string label,
        out string rejectReason)
    {
        rejectReason = "";
        if (expectedId < 0)
        {
            return true;
        }

        if (sellable == null)
        {
            rejectReason = $"served {label} missing";
            return false;
        }

        if (!TryReadSellableIdentity(sellable, out var actualType, out var actualId))
        {
            rejectReason = $"served {label} identity unavailable";
            return false;
        }

        if (actualType != sellableType || actualId != expectedId)
        {
            rejectReason = $"served {label} mismatch actual={actualType}/{actualId}, expected={sellableType}/{expectedId}";
            return false;
        }

        return true;
    }

    private static string FormatYuyukoServedTarget(object? sellable)
    {
        if (sellable == null) return "null";
        return TryReadSellableIdentity(sellable, out var sellableType, out var id)
            ? $"{sellableType}/{id}"
            : SpecialBusinessDiagnostics.DescribeObject(sellable);
    }

    private static bool IsSpecialOrder(object order)
    {
        if ((ReadMember(order, "SpecialGuests") ?? TryInvokeInstanceValue(order, "get_SpecialGuests")) != null)
        {
            return true;
        }

        var type = ReadMember(order, "Type") ?? TryInvokeInstanceValue(order, "get_Type");
        return type?.ToString()?.Contains("Special", StringComparison.OrdinalIgnoreCase) == true || ToInt(type) == 1;
    }

    private sealed class RuntimeOrderMatch
    {
        public object? Manager { get; init; }
        public object? Controller { get; init; }
        public object? Order { get; init; }
        public bool ManualOrder { get; init; }
        public object? ManualEvaluationCallback { get; init; }
        public string Diagnostic { get; init; } = "";
    }

    private sealed class CapturedOrderIdentityEvaluation
    {
        public CapturedRuntimeSpecialOrder Order { get; init; } = null!;
        public bool Matched { get; init; }
        public string RejectReason { get; init; } = "";
    }
}
