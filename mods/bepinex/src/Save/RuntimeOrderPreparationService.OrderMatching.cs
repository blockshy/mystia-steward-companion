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
        NativeEvaluation,
    }

    private static RuntimeOrderMatch FindRuntimeOrder(
        OrderPreparationRequest request,
        RuntimeOrderLookupPurpose purpose = RuntimeOrderLookupPurpose.Delivery)
    {
        var manager = GetSingletonInstance(GuestsManagerTypeName);
        if (manager == null) return new RuntimeOrderMatch();

        var requiresLiveKoishiBoss = RequiresLiveWackyKoishiBossController(request);
        var requiresLiveYuyukoPhase3Boss = purpose == RuntimeOrderLookupPurpose.NativeEvaluation
            && RequiresLiveYuyukoPhase3BossController(request);
        var requiresYuyukoStoryManualEvaluation = purpose == RuntimeOrderLookupPurpose.NativeEvaluation
            && RequiresStoryYuyukoPhase3ManualEvaluation(request);
        var yuyukoManualEvaluationCallbackDiagnostic = "";
        var yuyukoManualEvaluationCallback = requiresYuyukoStoryManualEvaluation
            ? FindCapturedYuyukoPhase3ManualEvaluationCallback(request, out yuyukoManualEvaluationCallbackDiagnostic)
            : null;
        var captured = requiresLiveKoishiBoss
            ? new RuntimeOrderMatch { Diagnostic = BuildWackyKoishiCaptureSkippedDiagnostic("capturedSkipped") }
            : requiresLiveYuyukoPhase3Boss
                ? new RuntimeOrderMatch
                {
                    ManualEvaluationCallback = yuyukoManualEvaluationCallback,
                    Diagnostic = BuildYuyukoPhase3CaptureSkippedDiagnostic("capturedSkipped")
                        + (requiresYuyukoStoryManualEvaluation
                            ? $"; {yuyukoManualEvaluationCallbackDiagnostic}"
                            : $"; evaluationMode={ResolveYuyukoPhase3EvaluationMode(request)}"),
                }
                : FindCapturedRuntimeOrder(request, manager);
        if (!requiresLiveKoishiBoss && !requiresLiveYuyukoPhase3Boss && captured.Order != null && captured.Controller != null)
        {
            AppendWackyBossRuntimeDiagnostic("rare-captured-match", request, captured, "accept", captured.Diagnostic);
            AppendYuyukoRuntimeDiagnostic("rare-captured-match", request, captured, "accept", captured.Diagnostic);
            return captured;
        }

        var scannedControllers = 0;
        var scannedOrders = 0;
        foreach (var controller in EnumerateGuestControllers(manager))
        {
            scannedControllers++;
            if (controller == null) continue;
            foreach (var order in EnumerateControllerOrders(controller))
            {
                scannedOrders++;
                try
                {
                    if (requiresLiveYuyukoPhase3Boss)
                    {
                        if (!IsMatchingYuyukoPhase3EvaluationOrder(order, controller, request, out var yuyukoRejectReason))
                        {
                            AppendYuyukoRuntimeDiagnostic(
                                "yuyuko-live-evaluation-candidate",
                                request,
                                new RuntimeOrderMatch { Manager = manager, Controller = controller, Order = order, ManualEvaluationCallback = yuyukoManualEvaluationCallback },
                                "reject",
                                yuyukoRejectReason);
                            continue;
                        }

                        AppendYuyukoRuntimeDiagnostic(
                            "yuyuko-live-evaluation-candidate",
                            request,
                            new RuntimeOrderMatch { Manager = manager, Controller = controller, Order = order, ManualEvaluationCallback = yuyukoManualEvaluationCallback },
                            "accept",
                            yuyukoRejectReason);
                    }
                    else if (!IsMatchingSpecialOrder(order, controller, request, out var specialRejectReason))
                    {
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

                var match = new RuntimeOrderMatch
                {
                    Manager = manager,
                    Controller = controller,
                    Order = order,
                    ManualEvaluationCallback = requiresYuyukoStoryManualEvaluation ? yuyukoManualEvaluationCallback : null,
                    Diagnostic = IsWackyKoishiBossRequest(request)
                        ? $"{DescribeWackyKoishiExecutionMode(request)}, liveController=ok, scannedControllers={scannedControllers}, scannedOrders={scannedOrders}"
                        : requiresLiveYuyukoPhase3Boss
                            ? requiresYuyukoStoryManualEvaluation
                                ? $"yuyuko phase3 liveController=ok, purpose={purpose}, evaluationMode={ResolveYuyukoPhase3EvaluationMode(request)}, scannedControllers={scannedControllers}, scannedOrders={scannedOrders}, manualCallback={(yuyukoManualEvaluationCallback == null ? "missing" : "captured")}, callbackCapture=({yuyukoManualEvaluationCallbackDiagnostic})"
                                : $"yuyuko phase3 liveController=ok, purpose={purpose}, evaluationMode={ResolveYuyukoPhase3EvaluationMode(request)}, scannedControllers={scannedControllers}, scannedOrders={scannedOrders}"
                        : "",
                };
                AppendWackyBossRuntimeDiagnostic("rare-live-match", request, match, "accept", match.Diagnostic);
                AppendYuyukoRuntimeDiagnostic("rare-live-match", request, match, "accept", match.Diagnostic);
                return match;
            }
        }

        return new RuntimeOrderMatch
        {
            ManualEvaluationCallback = requiresYuyukoStoryManualEvaluation ? yuyukoManualEvaluationCallback : null,
            Diagnostic = $"captured={captured.Diagnostic}, scannedControllers={scannedControllers}, scannedOrders={scannedOrders}",
        };
    }

    private static RuntimeOrderMatch FindRuntimeNormalOrder(OrderPreparationRequest request)
    {
        var strictMatch = FindRuntimeNormalOrderCore(request);
        if (strictMatch.Order != null && strictMatch.Controller != null)
        {
            return strictMatch;
        }

        if (RequiresLiveWackyKoishiBossController(request))
        {
            return strictMatch;
        }

        if (string.IsNullOrWhiteSpace(request.OrderKey) || request.DeskCode < 0 || GetNormalMatchFoodId(request) < 0 || GetNormalMatchBeverageId(request) < 0)
        {
            return strictMatch;
        }

        var relaxedRequest = CopyNormalOrderRequestWithoutOrderKey(request);
        var relaxedMatch = FindRuntimeNormalOrderCore(relaxedRequest);
        if (relaxedMatch.Order == null)
        {
            return strictMatch;
        }

        if (relaxedMatch.Controller == null && strictMatch.Order != null)
        {
            return strictMatch;
        }

        return new RuntimeOrderMatch
        {
            Manager = relaxedMatch.Manager,
            Controller = relaxedMatch.Controller,
            Order = relaxedMatch.Order,
            Diagnostic = $"orderKeyFallback strict=({strictMatch.Diagnostic}) relaxed=({relaxedMatch.Diagnostic})",
        };
    }

    private static RuntimeOrderMatch FindRuntimeNormalOrderCore(OrderPreparationRequest request)
    {
        var manager = GetSingletonInstance(GuestsManagerTypeName);
        if (manager == null) return new RuntimeOrderMatch();

        var requiresLiveKoishiBoss = RequiresLiveWackyKoishiBossController(request);
        var captured = FindCapturedRuntimeNormalOrder(request, manager);
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

                var match = new RuntimeOrderMatch
                {
                    Manager = manager,
                    Controller = controller,
                    Order = order,
                    Diagnostic = IsWackyKoishiBossRequest(request)
                        ? $"{DescribeWackyKoishiExecutionMode(request)}, liveController=ok, controllerOrders={scannedControllerOrders}"
                        : $"controllerOrders={scannedControllerOrders}",
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

            var match = new RuntimeOrderMatch
            {
                Manager = manager,
                Controller = controller,
                Order = order,
                Diagnostic = $"controllers={scannedControllers}, controllerOrders={scannedControllerOrders}, captured=({captured.Diagnostic}), uiOrders={scannedUiOrders}, uiController={(controller == null ? "missing" : "ok")}",
            };
            AppendWackyBossRuntimeDiagnostic("normal-ui-match", request, match, "accept", match.Diagnostic);
            return match;
        }

        return new RuntimeOrderMatch
        {
            Diagnostic = $"controllers={scannedControllers}, controllerOrders={scannedControllerOrders}, captured=({captured.Diagnostic}), uiOrders={scannedUiOrders}",
        };
    }

    private static OrderPreparationRequest CopyNormalOrderRequestWithoutOrderKey(OrderPreparationRequest request)
    {
        return new OrderPreparationRequest
        {
            TraceId = request.TraceId,
            DeskCode = request.DeskCode,
            GuestId = request.GuestId,
            GuestName = request.GuestName,
            SpecialBusinessRole = request.SpecialBusinessRole,
            FoodTag = request.FoodTag,
            BeverageTag = request.BeverageTag,
            MatchFoodId = request.MatchFoodId,
            MatchBeverageId = request.MatchBeverageId,
            FoodId = request.FoodId,
            RecipeId = request.RecipeId,
            RecipeName = request.RecipeName,
            ExtraIngredientIds = request.ExtraIngredientIds,
            PredictedFoodTags = request.PredictedFoodTags,
            WackyTargetFoodTags = request.WackyTargetFoodTags,
            ExecutionMode = request.ExecutionMode,
            ExecutionReason = request.ExecutionReason,
            BeverageId = request.BeverageId,
            BeverageName = request.BeverageName,
            AutoTakeBeverage = request.AutoTakeBeverage,
            AutoStartCooking = request.AutoStartCooking,
            AutoCollectCooking = request.AutoCollectCooking,
            AutoDeliverFood = request.AutoDeliverFood,
            AutoCompleteOrder = request.AutoCompleteOrder,
            RecipeFavoritesOnly = request.RecipeFavoritesOnly,
            BeverageFavoritesOnly = request.BeverageFavoritesOnly,
            StopOnError = request.StopOnError,
            RecipeFavorite = request.RecipeFavorite,
            BeverageFavorite = request.BeverageFavorite,
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
        if (!SpecialBusinessOrderClassifier.Classify(order, controller).AutomationAllowed) return false;
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

    private static RuntimeOrderMatch FindCapturedRuntimeOrder(OrderPreparationRequest request, object manager)
    {
        var capturedOrders = SpecialOrderRuntimeCapture.Snapshot(TimeSpan.FromHours(6));
        var candidates = capturedOrders
            .Select(captured => new
            {
                Order = captured,
                Score = ScoreCapturedOrder(captured, request),
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

            return new RuntimeOrderMatch
            {
                Manager = manager,
                Controller = captured.ControllerObject,
                Order = captured.OrderObject,
                ManualEvaluationCallback = captured.ManualEvaluationCallback,
                Diagnostic = $"capturedCandidates={candidates.Count}, score={candidate.Score}, source={captured.CaptureSource}, manualCallback={(captured.ManualEvaluationCallback == null ? "missing" : "captured")}",
            };
        }

        return new RuntimeOrderMatch
        {
            Diagnostic = $"capturedCandidates={candidates.Count}, capturedTotal={capturedOrders.Count}, captured=[{FormatCapturedOrderSummary(capturedOrders)}]",
        };
    }

    private static object? FindCapturedYuyukoPhase3ManualEvaluationCallback(
        OrderPreparationRequest request,
        out string diagnostic)
    {
        var capturedOrders = SpecialOrderRuntimeCapture.Snapshot(TimeSpan.FromHours(6));
        var candidates = capturedOrders
            .Select(captured => new
            {
                Order = captured,
                Score = ScoreCapturedOrder(captured, request),
            })
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Order.FirstCapturedAt)
            .ThenBy(candidate => candidate.Order.CapturedAt)
            .ToList();
        var callbackCandidate = candidates.FirstOrDefault(candidate => candidate.Order.ManualEvaluationCallback != null);
        if (callbackCandidate == null)
        {
            diagnostic = $"manualCallback=missing, capturedCandidates={candidates.Count}, capturedTotal={capturedOrders.Count}, captured=[{FormatCapturedOrderSummary(capturedOrders)}]";
            return null;
        }

        var callback = callbackCandidate.Order.ManualEvaluationCallback;
        diagnostic = $"manualCallback=captured, capturedCandidates={candidates.Count}, score={callbackCandidate.Score}, source={callbackCandidate.Order.CaptureSource}, callback={YuyukoChallengeEvaluationTracker.DescribeCallback(callback)}";
        return callback;
    }

    private static RuntimeOrderMatch FindCapturedRuntimeNormalOrder(OrderPreparationRequest request, object manager)
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

            try
            {
                if (!IsMatchingNormalOrder(captured.OrderObject, request, captured.ControllerObject)) continue;
            }
            catch
            {
                continue;
            }

            return new RuntimeOrderMatch
            {
                Manager = manager,
                Controller = captured.ControllerObject,
                Order = captured.OrderObject,
                Diagnostic = $"normalCapturedCandidates={candidates.Count}, score={candidate.Score}, source={captured.CaptureSource}",
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
            .Select(order => $"desk={order.DeskCode + 1},guest={order.GuestName},food={order.FoodId},bev={order.BeverageId},source={order.CaptureSource},obj={(order.OrderObject == null ? "no" : "yes")}/{(order.ControllerObject == null ? "no" : "yes")}")
            .ToArray();
        var suffix = capturedOrders.Count > items.Length ? $" ... total={capturedOrders.Count}" : "";
        return string.Join("; ", items) + suffix;
    }

    private static int ScoreCapturedOrder(CapturedRuntimeSpecialOrder captured, OrderPreparationRequest request)
    {
        if (captured.OrderObject == null || captured.ControllerObject == null) return 0;

        var score = 0;
        var deskMatched = false;
        if (captured.DeskCode >= 0 && request.DeskCode >= 0)
        {
            if (captured.DeskCode == request.DeskCode)
            {
                score += 12;
                deskMatched = true;
            }
            else
            {
                score -= 8;
            }
        }

        if (request.GuestId.HasValue && captured.GuestId.HasValue)
        {
            score += request.GuestId.Value == captured.GuestId.Value ? 8 : -2;
        }

        if (!string.IsNullOrWhiteSpace(request.GuestName) && !string.IsNullOrWhiteSpace(captured.GuestName))
        {
            score += TextMatches(captured.GuestName, request.GuestName) ? 6 : 0;
        }

        if (!string.IsNullOrWhiteSpace(request.FoodTag) && !string.IsNullOrWhiteSpace(captured.FoodTag))
        {
            if (!TextMatches(captured.FoodTag, request.FoodTag)) return 0;
            score += 3;
        }

        if (!string.IsNullOrWhiteSpace(request.BeverageTag) && !string.IsNullOrWhiteSpace(captured.BeverageTag))
        {
            if (!TextMatches(captured.BeverageTag, request.BeverageTag)) return 0;
            score += 3;
        }

        return score >= (deskMatched ? 8 : 12) ? score : 0;
    }

    private static bool TextMatches(string left, string right)
    {
        left = left.Trim();
        right = right.Trim();
        if (left.Length == 0 || right.Length == 0) return false;
        return string.Equals(left, right, StringComparison.Ordinal)
            || left.Contains(right, StringComparison.Ordinal)
            || right.Contains(left, StringComparison.Ordinal);
    }

    private static bool SpecialOrderTagsMatch(object order, OrderPreparationRequest request, out string rejectReason)
    {
        rejectReason = "";
        if (!string.IsNullOrWhiteSpace(request.FoodTag))
        {
            var orderFoodTag = ReadSpecialOrderTagText(order, isFood: true);
            if (!string.IsNullOrWhiteSpace(orderFoodTag) && !TextMatches(orderFoodTag, request.FoodTag))
            {
                rejectReason = $"food tag mismatch order={orderFoodTag}, expected={request.FoodTag}";
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(request.BeverageTag))
        {
            var orderBeverageTag = ReadSpecialOrderTagText(order, isFood: false);
            if (!string.IsNullOrWhiteSpace(orderBeverageTag) && !TextMatches(orderBeverageTag, request.BeverageTag))
            {
                rejectReason = $"beverage tag mismatch order={orderBeverageTag}, expected={request.BeverageTag}";
                return false;
            }
        }

        return true;
    }

    private static string ReadSpecialOrderTagText(object order, bool isFood)
    {
        var memberName = isFood ? "RequestFoodTag" : "RequestBeverageTag";
        var raw = ReadMember(order, memberName) ?? TryInvokeInstanceValue(order, $"get_{memberName}");
        var tagId = ToInt(raw, int.MinValue);
        if (tagId != int.MinValue)
        {
            string tagName;
            if (isFood)
            {
                _ = TryReadFoodTagName(tagId, out tagName);
            }
            else
            {
                tagName = TryReadBeverageTagName(tagId);
            }
            if (!string.IsNullOrWhiteSpace(tagName)) return tagName;
        }

        var textMember = isFood ? "ReqFoodTag" : "ReqBevTag";
        return ReadMember(order, textMember)?.ToString()?.Trim() ?? "";
    }

    private static string FormatCapturedOrderSummary(IReadOnlyList<CapturedRuntimeSpecialOrder> capturedOrders)
    {
        if (capturedOrders.Count == 0) return "";

        var items = capturedOrders
            .Take(4)
            .Select(order => $"desk={order.DeskCode + 1},guest={order.GuestName}/{order.GuestId?.ToString() ?? ""},food={order.FoodTag},bev={order.BeverageTag},source={order.CaptureSource},obj={(order.OrderObject == null ? "no" : "yes")}/{(order.ControllerObject == null ? "no" : "yes")},manualCallback={(order.ManualEvaluationCallback == null ? "no" : "yes")}")
            .ToArray();
        var suffix = capturedOrders.Count > items.Length ? $" ... total={capturedOrders.Count}" : "";
        return string.Join("; ", items) + suffix;
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
            foreach (var order in ReadObjectEnumerable(ReadMember(controller, name)))
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

    private static object? NormalizeDictionaryItem(object item)
    {
        return ReadMember(item, "Value") ?? item;
    }

    private static bool IsMatchingSpecialOrder(
        object order,
        object controller,
        OrderPreparationRequest request,
        out string rejectReason)
    {
        rejectReason = "";
        if (ToInt(ReadMember(order, "DeskCode") ?? TryInvokeInstanceValue(order, "get_DeskCode")) != request.DeskCode)
        {
            rejectReason = "desk mismatch";
            return false;
        }

        if (!IsSpecialOrder(order))
        {
            rejectReason = "not special order";
            return false;
        }

        if (request.GuestId.HasValue)
        {
            var orderGuestId = ReadGuestId(ReadMember(order, "SpecialGuests") ?? TryInvokeInstanceValue(order, "get_SpecialGuests"));
            var controllerGuestId = ReadGuestId(ReadMember(controller, "SpecialGuest") ?? TryInvokeInstanceValue(controller, "get_SpecialGuest"));
            if (orderGuestId != request.GuestId.Value && controllerGuestId != request.GuestId.Value)
            {
                rejectReason = $"guest mismatch order={orderGuestId}, controller={controllerGuestId}, expected={request.GuestId.Value}";
                return false;
            }
        }

        if (!SpecialOrderTagsMatch(order, request, out rejectReason))
        {
            return false;
        }

        return true;
    }

    private static bool IsMatchingYuyukoPhase3EvaluationOrder(
        object order,
        object controller,
        OrderPreparationRequest request,
        out string rejectReason)
    {
        rejectReason = "";
        if (ToInt(ReadMember(order, "DeskCode") ?? TryInvokeInstanceValue(order, "get_DeskCode")) != request.DeskCode)
        {
            rejectReason = "desk mismatch";
            return false;
        }

        if (!IsSpecialOrder(order))
        {
            rejectReason = "not special order";
            return false;
        }

        var orderGuestId = ReadGuestId(ReadMember(order, "SpecialGuests") ?? TryInvokeInstanceValue(order, "get_SpecialGuests"));
        var controllerGuestId = ReadGuestId(ReadMember(controller, "SpecialGuest") ?? TryInvokeInstanceValue(controller, "get_SpecialGuest"));
        var hasReadableGuest = orderGuestId >= 0 || controllerGuestId >= 0;
        if (request.GuestId.HasValue
            && hasReadableGuest
            && orderGuestId != request.GuestId.Value
            && controllerGuestId != request.GuestId.Value)
        {
            rejectReason = $"guest mismatch order={orderGuestId}, controller={controllerGuestId}, expected={request.GuestId.Value}";
            return false;
        }

        if (!SpecialOrderTagsMatch(order, request, out rejectReason))
        {
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

        var orderFullfilled = ReadBool(TryInvokeInstanceValue(order, "get_IsFullfilled") ?? ReadMember(order, "IsFullfilled"));
        var servedFoodLevel = ReadSellableLevel(servedFood);
        var servedBeverageLevel = ReadSellableLevel(servedBeverage);
        var levelSum = servedFoodLevel >= 0 && servedBeverageLevel >= 0
            ? (servedFoodLevel + servedBeverageLevel).ToString()
            : "";
        rejectReason = $"yuyuko phase3 evaluation signature matched; guestReadable={hasReadableGuest}; orderGuest={orderGuestId}; controllerGuest={controllerGuestId}; callback=present; fulfilled={orderFullfilled}; servedFood={FormatYuyukoServedTarget(servedFood)}; servedBeverage={FormatYuyukoServedTarget(servedBeverage)}; servedLevelSum={levelSum}";
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
        if (sellable == null || expectedId < 0)
        {
            return true;
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

    private static int ReadGuestId(object? guest)
    {
        if (guest == null) return -1;
        return ToInt(TryInvokeInstanceValue(guest, "get_id")
            ?? TryInvokeInstanceValue(guest, "get_Id")
            ?? TryInvokeInstanceValue(guest, "get_CharacterID")
            ?? ReadMember(guest, "id")
            ?? ReadMember(guest, "Id")
            ?? ReadMember(guest, "CharacterID"));
    }

    private sealed class RuntimeOrderMatch
    {
        public object? Manager { get; init; }
        public object? Controller { get; init; }
        public object? Order { get; init; }
        public object? ManualEvaluationCallback { get; init; }
        public string Diagnostic { get; init; } = "";
    }
}
