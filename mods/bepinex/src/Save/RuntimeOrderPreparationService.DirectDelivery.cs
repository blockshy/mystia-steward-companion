using MystiaStewardCompanion.Core;
using MystiaStewardCompanion.LocalApi;
using System.Reflection;

namespace MystiaStewardCompanion.Save;

internal static partial class RuntimeOrderPreparationService
{
    private enum WackyTagValidation
    {
        NotRequired,
        Matched,
        Mismatched,
        Unreadable,
    }

    /// <summary>
    /// 直接为订单创建并写入酒水。
    /// </summary>
    /// <remarks>
    /// 只有桌面显示和订单状态都提交成功后才扣减库存，避免库存与订单状态不一致。
    /// </remarks>
    private static (bool Ok, string Message) TryDeliverOrderBeverage(
        RuntimeOrderMatch runtimeOrder,
        int beverageId,
        string beverageName,
        string orderLabel)
    {
        var currentQuantity = GetBeverageQuantity(beverageId);
        if (currentQuantity == 0)
        {
            return (false, $"{beverageName} 当前库存为 0，无法送达{orderLabel}。");
        }

        var sellable = InvokeStatic(DataBaseCoreTypeName, "AsNewBeverage", new object?[] { beverageId });
        if (sellable == null)
        {
            return (false, $"无法从游戏数据库创建酒水对象：{beverageName} #{beverageId}。");
        }

        var delivery = TryCommitRuntimeDelivery(runtimeOrder, sellable, RuntimeDeliveryItemKind.Beverage, beverageName);
        if (!delivery.Ok)
        {
            return (false, delivery.Message);
        }

        if (currentQuantity > 0)
        {
            InvokeRuntimeStorageOut("BeverageOut", beverageId);
        }

        var quantityText = currentQuantity < 0 ? "无限库存" : $"剩余 {Math.Max(0, currentQuantity - 1)}";
        return (true, $"{beverageName} 已送达{orderLabel}（{quantityText}）。");
    }

    private static object? ReadOrderServedFood(object order)
    {
        return ReadMember(order, "ServFood")
            ?? TryInvokeInstanceValue(order, "get_ServFood");
    }

    private static object? ReadOrderServedBeverage(object order)
    {
        return ReadMember(order, "ServBeverage")
            ?? TryInvokeInstanceValue(order, "get_ServBeverage");
    }

    private static bool IsSellable(object? item, int sellableType, int id)
    {
        return item != null && ReadSellableType(item) == sellableType && ReadSellableId(item) == id;
    }

    private static bool IsFoodSellable(object? item)
    {
        return TryReadSellableIdentity(item, out var sellableType, out var id)
            && sellableType == 0
            && id >= 0;
    }

    private static bool TryReadSellableIdentity(object? item, out int sellableType, out int id)
    {
        sellableType = -1;
        id = -1;
        if (item == null) return false;

        sellableType = ReadSellableType(item);
        id = ReadSellableId(item);
        return sellableType >= 0 && id >= 0;
    }

    private static int ReadSellableType(object item)
    {
        var value = TryInvokeInstanceValue(item, "get_Type") ?? ReadMember(item, "Type");
        return ToInt(value, -1);
    }

    private static int ReadSellableId(object item)
    {
        var value = TryInvokeInstanceValue(item, "get_id")
            ?? TryInvokeInstanceValue(item, "get_Id")
            ?? ReadMember(item, "id")
            ?? ReadMember(item, "Id");
        return ToInt(value, -1);
    }

    /// <summary>
    /// 将已经出锅的料理直接送达给登记的目标订单。
    /// </summary>
    /// <remarks>
    /// 订单或桌面对象暂不可写时保留成品供下一轮重试；非目标成品会放入游戏料理暂存容器，送达成功后会执行厨具清理。
    /// </remarks>
    private static (bool Remove, string Message, string Code) TryDeliverPendingCookedFood(PendingCookingCollection pending, object cookedFood)
    {
        var target = pending.Target;
        if (target.FoodId >= 0 && !IsSellable(cookedFood, sellableType: 0, id: target.FoodId))
        {
            var actualFoodId = ReadSellableId(cookedFood);
            var actualText = actualFoodId >= 0 ? $"料理 #{actualFoodId}" : "未知成品";
            var actualFoodTags = ReadFoodTagNames(cookedFood).ToArray();
            var activeTargetTags = target.WackyTargetFoodTags;
            AppendWackyPendingDiagnostic(
                "cooked-food-id-mismatch",
                pending,
                "store-mismatched-food",
                actualFoodId,
                activeTargetTags,
                actualFoodTags,
                $"actual={actualText}; expected={target.FoodName}({target.FoodId})");
            if (TryStoreMismatchedCookResultInWarmer(pending, cookedFood, actualFoodId, out var storeMessage))
            {
                var mismatchMessage = $"{pending.RecipeName} 已完成，但成品 {actualText} 不是目标料理 {target.FoodName}（料理 #{target.FoodId}），已放入保温箱并释放该自动化待办，将在下一轮重试目标料理。{storeMessage}";
                AppendWackyPendingDiagnostic(
                    "cooked-food-id-mismatch-stored",
                    pending,
                    "stored-in-warmer",
                    actualFoodId,
                    activeTargetTags,
                    actualFoodTags,
                    storeMessage);
                RecordAutomationRuntimeEvent(
                    OrderPreparationStepCodes.CookingMismatchStored,
                    target,
                    mismatchMessage,
                    actualFoodId,
                    actualFoodTags: actualFoodTags);
                return (
                    true,
                    mismatchMessage,
                    OrderPreparationStepCodes.CookingMismatchStored);
            }

            AppendWackyPendingDiagnostic(
                "cooked-food-id-mismatch-store-failed",
                pending,
                "keep-on-cooker",
                actualFoodId,
                activeTargetTags,
                actualFoodTags,
                storeMessage);
            return ShouldStopPendingDirectDelivery(
                pending,
                $"{pending.RecipeName} 已完成，但成品 {actualText} 不是目标料理 {target.FoodName}（料理 #{target.FoodId}），且写入保温箱失败：{storeMessage}");
        }

        if (TryDetectWackyTargetSignatureChanged(pending, out var originalTargetSignature, out var currentTargetSignature, out var originalTargetTags, out var currentTargetTags))
        {
            var actualFoodId = ReadSellableId(cookedFood);
            var actualTagsForSignature = ReadFoodTagNames(cookedFood).ToArray();
            var signatureMessage = $"{pending.RecipeName} 已完成，但怪诞料理目标已变化：开锅时 {FormatWackyTargetForMessage(originalTargetSignature, originalTargetTags)}，当前 {FormatWackyTargetForMessage(currentTargetSignature, currentTargetTags)}";
            AppendWackyPendingDiagnostic(
                "wacky-target-signature-changed",
                pending,
                "store-stale-target",
                actualFoodId,
                currentTargetTags,
                actualTagsForSignature,
                signatureMessage);
            if (TryStoreMismatchedCookResultInWarmer(pending, cookedFood, actualFoodId, out var signatureStoreMessage))
            {
                var staleMessage = $"{signatureMessage}，已放入保温箱并释放该自动化待办，将在下一轮按当前目标重新推荐并开锅。{signatureStoreMessage}";
                AppendWackyPendingDiagnostic(
                    "wacky-target-signature-changed-stored",
                    pending,
                    "stored-in-warmer",
                    actualFoodId,
                    currentTargetTags,
                    actualTagsForSignature,
                    signatureStoreMessage);
                RecordAutomationRuntimeEvent(
                    OrderPreparationStepCodes.CookingMismatchStored,
                    target,
                    staleMessage,
                    actualFoodId,
                    currentTargetTags,
                    actualTagsForSignature);
                return (
                    true,
                    staleMessage,
                    OrderPreparationStepCodes.CookingMismatchStored);
            }

            AppendWackyPendingDiagnostic(
                "wacky-target-signature-changed-store-failed",
                pending,
                "keep-on-cooker",
                actualFoodId,
                currentTargetTags,
                actualTagsForSignature,
                signatureStoreMessage);
            return ShouldStopPendingDirectDelivery(
                pending,
                $"{signatureMessage}，且写入保温箱失败：{signatureStoreMessage}");
        }

        var wackyTagValidation = ValidateWackyTags(target, cookedFood, out var targetTags, out var actualTags);
        if (wackyTagValidation is WackyTagValidation.Mismatched or WackyTagValidation.Unreadable)
        {
            var actualFoodId = ReadSellableId(cookedFood);
            var unreadableTags = wackyTagValidation == WackyTagValidation.Unreadable;
            var resultCode = unreadableTags
                ? OrderPreparationStepCodes.CookingTagsUnreadableStored
                : OrderPreparationStepCodes.CookingMismatchStored;
            var diagnosticPrefix = unreadableTags ? "cooked-food-tags-unreadable" : "cooked-food-tag-mismatch";
            var tagMessage = unreadableTags
                ? $"{pending.RecipeName} 已完成，但无法读取成品 Tag，不能确认满足当前怪诞料理目标 Tag（{string.Join("、", targetTags)}）"
                : $"{pending.RecipeName} 已完成，但成品 Tag（{string.Join("、", actualTags)}）不含当前怪诞料理目标 Tag（{string.Join("、", targetTags)}）";
            AppendWackyPendingDiagnostic(
                diagnosticPrefix,
                pending,
                unreadableTags ? "store-unreadable-tags" : "store-tag-mismatch",
                actualFoodId,
                targetTags,
                actualTags,
                tagMessage);
            if (TryStoreMismatchedCookResultInWarmer(pending, cookedFood, actualFoodId, out var wackyStoreMessage))
            {
                var nextAction = unreadableTags
                    ? "已放入保温箱并暂停该订单自动化，请检查运行时 Tag 读取后再继续。"
                    : "已放入保温箱并释放该自动化待办，将在下一轮重新推荐并重试。";
                var wackyMessage = $"{tagMessage}，{nextAction}{wackyStoreMessage}";
                AppendWackyPendingDiagnostic(
                    $"{diagnosticPrefix}-stored",
                    pending,
                    "stored-in-warmer",
                    actualFoodId,
                    targetTags,
                    actualTags,
                    wackyStoreMessage);
                if (!unreadableTags) RememberRecentWackyRejectedRecipe(target, targetTags);
                RecordAutomationRuntimeEvent(
                    resultCode,
                    target,
                    wackyMessage,
                    actualFoodId,
                    targetTags,
                    actualTags);
                return (
                    true,
                    wackyMessage,
                    resultCode);
            }

            AppendWackyPendingDiagnostic(
                $"{diagnosticPrefix}-store-failed",
                pending,
                "keep-on-cooker",
                actualFoodId,
                targetTags,
                actualTags,
                wackyStoreMessage);
            return ShouldStopPendingDirectDelivery(
                pending,
                $"{tagMessage}，且写入保温箱失败：{wackyStoreMessage}");
        }

        if (wackyTagValidation == WackyTagValidation.Matched)
        {
            AppendWackyPendingDiagnostic(
                "cooked-food-tag-match",
                pending,
                "continue-delivery",
                ReadSellableId(cookedFood),
                targetTags,
                actualTags,
                "cooked food tags matched active wacky target tags");
        }

        var request = BuildOrderRequestFromCookingTarget(target);
        var runtimeOrder = target.Kind == CookingCollectionTargetKind.NormalOrder
            ? FindRuntimeNormalOrder(request)
            : FindRuntimeOrder(request);
        if (runtimeOrder.Order == null || runtimeOrder.Manager == null)
        {
            return HandleUndeliverableCookedFood(
                pending,
                cookedFood,
                PendingDeliveryFailureKind.MissingOrder,
                $"未找到目标订单对象。{runtimeOrder.Diagnostic}");
        }

        if (runtimeOrder.Controller == null)
        {
            return HandleUndeliverableCookedFood(
                pending,
                cookedFood,
                PendingDeliveryFailureKind.MissingController,
                $"已找到目标订单，但未读取到可执行客人控制器；该订单可能只残留在 HUD 中。{runtimeOrder.Diagnostic}");
        }

        pending.ResetDeliveryFailures();

        if (ReadOrderServedFood(runtimeOrder.Order) != null)
        {
            TryCompleteCookControllerAfterDirectDelivery(pending.CookController, cookedFood);
            return (true, $"{pending.RecipeName} 已完成，但目标订单已有料理，已释放厨具。", "");
        }

        var pendingFood = ReadMember(runtimeOrder.Order, "ServedFoodInAir");
        if (pendingFood != null && !IsSellable(pendingFood, sellableType: 0, id: target.FoodId))
        {
            return ShouldStopPendingDirectDelivery(pending, "订单已有其他待送达料理，暂不覆盖。");
        }

        if (IsWackyKoishiBossTarget(target))
        {
            AppendWackyBossRuntimeDiagnostic(
                "pending-food-delivery-before",
                request,
                runtimeOrder,
                "commit-food",
                $"food={target.FoodName}; cookedFood={SpecialBusinessDiagnostics.DescribeObject(cookedFood)}");
        }
        if (IsYuyukoBossTarget(target))
        {
            AppendYuyukoRuntimeDiagnostic(
                "pending-food-delivery-before",
                request,
                runtimeOrder,
                "commit-food",
                $"food={target.FoodName}; cookedFood={SpecialBusinessDiagnostics.DescribeObject(cookedFood)}");
        }

        var delivery = TryCommitRuntimeDelivery(runtimeOrder, cookedFood, RuntimeDeliveryItemKind.Food, target.FoodName);
        if (!delivery.Ok)
        {
            return ShouldStopPendingDirectDelivery(pending, delivery.Message);
        }

        TryCompleteCookControllerAfterDirectDelivery(pending.CookController, cookedFood);
        var recoverSuffix = TryRecoverPatientAfterPartialDelivery(runtimeOrder, 1, out var recoverMessage) && !string.IsNullOrWhiteSpace(recoverMessage)
            ? recoverMessage
            : "";
        var label = target.Kind == CookingCollectionTargetKind.NormalOrder ? "普客订单" : "稀客订单";
        var evaluationSuffix = "";
        if (RequiresNativeWackyKoishiBossEvaluationEntry(request))
        {
            var evaluation = TryEvaluateWackyKoishiBossRuntimeOrderIfReady(request, runtimeOrder, $"当前{label}");
            evaluationSuffix = string.IsNullOrWhiteSpace(evaluation.Message) ? "" : evaluation.Message;
        }
        else if (target.Kind == CookingCollectionTargetKind.NormalOrder && target.AutoCompleteOrder)
        {
            if (IsWackyKoishiBossTarget(target))
            {
                AppendWackyBossRuntimeDiagnostic(
                    "pending-food-delivery-evaluate",
                    request,
                    runtimeOrder,
                    "call-generic-evaluate",
                    "Koishi boss clue-stage order uses regular order evaluation after direct food delivery.");
            }

            var evaluation = IsYuyukoBossTarget(target)
                ? TryEvaluateYuyukoChallengeRuntimeOrderIfReady(request, runtimeOrder, "当前普客订单", reacquireLiveOrder: false, allowControllerMissing: true)
                : TryEvaluateRuntimeOrderIfReady(runtimeOrder, "当前普客订单", allowControllerMissing: true);
            evaluationSuffix = string.IsNullOrWhiteSpace(evaluation.Message) ? "" : evaluation.Message;
        }

        var message = $"{target.FoodName} 已直接送达{label}。";
        if (!string.IsNullOrWhiteSpace(recoverSuffix))
        {
            message += recoverSuffix;
        }

        if (!string.IsNullOrWhiteSpace(evaluationSuffix))
        {
            message += evaluationSuffix;
        }

        if (IsYuyukoBossTarget(target))
        {
            AppendYuyukoRuntimeDiagnostic(
                "pending-food-delivery-after",
                request,
                runtimeOrder,
                "food-delivered",
                message);
        }

        AppendWackyPendingDiagnostic(
            "cooked-food-delivered",
            pending,
            "delivered-to-order",
            ReadSellableId(cookedFood),
            targetTags,
            actualTags,
            message);
        RecordAutomationRuntimeEvent(OrderPreparationStepCodes.FoodDelivered, target, message);
        return (true, message, OrderPreparationStepCodes.FoodDelivered);
    }

    private static (bool Remove, string Message, string Code) ShouldStopPendingDirectDelivery(PendingCookingCollection pending, string message)
    {
        if (DateTime.UtcNow - pending.CreatedAtUtc >= PendingCookingIdleTimeout)
        {
            return (true, $"{pending.RecipeName} 自动送达已停止：{message} 成品保留在厨具中。", "");
        }

        return (false, $"{pending.RecipeName} 已完成，等待直接送达：{message}", OrderPreparationStepCodes.CookingPending);
    }

    private static (bool Remove, string Message, string Code) HandleUndeliverableCookedFood(
        PendingCookingCollection pending,
        object cookedFood,
        PendingDeliveryFailureKind failureKind,
        string reason)
    {
        var nowUtc = DateTime.UtcNow;
        var failure = pending.RecordDeliveryFailure(failureKind, nowUtc);
        var threshold = GetPendingFailureRetireAttempts(failureKind);
        var delay = GetPendingFailureRetireDelay(failureKind);
        var failureAge = nowUtc - failure.FirstAtUtc;
        var failureDetail = FormatPendingDeliveryFailureDetail(failureKind, failure.Count, threshold, failureAge, delay);
        if (failure.Count < threshold || failureAge < delay)
        {
            return ShouldStopPendingDirectDelivery(pending, $"{reason}{failureDetail}");
        }

        var actualFoodId = ReadSellableId(cookedFood);
        var actualTags = ReadFoodTagNames(cookedFood).ToArray();
        if (IsWackyTargetContext(pending.Target))
        {
            AppendWackyPendingDiagnostic(
                "wacky-undeliverable-target",
                pending,
                "store-undeliverable-food",
                actualFoodId,
                pending.Target.WackyTargetFoodTags,
                actualTags,
                $"{reason}{failureDetail}");
        }

        if (TryStoreMismatchedCookResultInWarmer(pending, cookedFood, actualFoodId, out var storeMessage))
        {
            var targetLabel = failureKind == PendingDeliveryFailureKind.MissingController
                ? "目标订单暂不可执行"
                : "目标订单已不存在、已切换或暂不可达";
            var message = $"{pending.RecipeName} 已完成，但{targetLabel}，已放入保温箱并释放该自动化待办。原因：{reason}{failureDetail} {storeMessage}";
            if (IsWackyTargetContext(pending.Target))
            {
                AppendWackyPendingDiagnostic(
                    "wacky-undeliverable-target-stored",
                    pending,
                    "stored-in-warmer",
                    actualFoodId,
                    pending.Target.WackyTargetFoodTags,
                    actualTags,
                    storeMessage);
            }

            RecordAutomationRuntimeEvent(
                OrderPreparationStepCodes.CookingMismatchStored,
                pending.Target,
                message,
                actualFoodId,
                pending.Target.WackyTargetFoodTags,
                actualTags);
            return (true, message, OrderPreparationStepCodes.CookingMismatchStored);
        }

        if (IsWackyTargetContext(pending.Target))
        {
            AppendWackyPendingDiagnostic(
                "wacky-undeliverable-target-store-failed",
                pending,
                "keep-on-cooker",
                actualFoodId,
                pending.Target.WackyTargetFoodTags,
                actualTags,
                storeMessage);
        }

        return ShouldStopPendingDirectDelivery(
            pending,
            $"{reason}{failureDetail} 已达到自动释放阈值，但写入保温箱失败：{storeMessage}");
    }

    private static int GetPendingFailureRetireAttempts(PendingDeliveryFailureKind failureKind)
    {
        return failureKind == PendingDeliveryFailureKind.MissingController
            ? PendingMissingControllerRetireAttempts
            : PendingMissingTargetRetireAttempts;
    }

    private static TimeSpan GetPendingFailureRetireDelay(PendingDeliveryFailureKind failureKind)
    {
        return failureKind == PendingDeliveryFailureKind.MissingController
            ? PendingMissingControllerRetireDelay
            : PendingMissingTargetRetireDelay;
    }

    private static string FormatPendingDeliveryFailureDetail(
        PendingDeliveryFailureKind failureKind,
        int count,
        int threshold,
        TimeSpan age,
        TimeSpan delay)
    {
        var reason = failureKind == PendingDeliveryFailureKind.MissingController
            ? "控制器不可达"
            : "订单不可达";
        return $"（{reason}连续 {count}/{threshold} 次，持续 {age.TotalSeconds:F1}/{delay.TotalSeconds:F0}s）";
    }

    private static WackyTagValidation ValidateWackyTags(
        CookingCollectionTarget target,
        object cookedFood,
        out IReadOnlyList<string> targetTags,
        out IReadOnlyList<string> actualTags)
    {
        var activeTargetTags = target.WackyTargetFoodTags;
        targetTags = activeTargetTags;
        actualTags = Array.Empty<string>();
        if (targetTags.Count == 0) return WackyTagValidation.NotRequired;

        if (!TryReadFoodTagNames(cookedFood, out actualTags)) return WackyTagValidation.Unreadable;
        return actualTags.Any(tag => activeTargetTags.Contains(tag, StringComparer.Ordinal))
            ? WackyTagValidation.Matched
            : WackyTagValidation.Mismatched;
    }

    private static bool TryDetectWackyTargetSignatureChanged(
        PendingCookingCollection pending,
        out string originalSignature,
        out string currentSignature,
        out IReadOnlyList<string> originalTags,
        out IReadOnlyList<string> currentTags)
    {
        originalSignature = pending.Target.WackyTargetSignature;
        originalTags = pending.Target.WackyTargetFoodTags;
        currentSignature = "";
        currentTags = Array.Empty<string>();
        if (string.IsNullOrWhiteSpace(originalSignature)) return false;

        RuntimeSpecialBusinessContextService.TryGetActiveWackyTargetSignature(out currentSignature, out currentTags);
        return !string.Equals(originalSignature, currentSignature, StringComparison.Ordinal);
    }

    private static string FormatWackyTargetForMessage(string signature, IReadOnlyList<string> tags)
    {
        if (string.IsNullOrWhiteSpace(signature)) return "非怪诞料理目标";
        return tags.Count == 0 ? signature : $"{signature}（Tag {string.Join("、", tags)}）";
    }

    private static IEnumerable<string> ReadFoodTagNames(object cookedFood)
    {
        if (!TryReadFoodTagNames(cookedFood, out var tags)) yield break;
        foreach (var tag in tags) yield return tag;
    }

    private static bool TryReadFoodTagNames(object cookedFood, out IReadOnlyList<string> tags)
    {
        tags = Array.Empty<string>();
        var rawTags = TryInvokeInstanceValue(cookedFood, "get_Tags")
            ?? ReadMember(cookedFood, "Tags")
            ?? TryInvokeInstanceValue(cookedFood, "get_RawTags")
            ?? ReadMember(cookedFood, "RawTags");
        if (rawTags == null) return false;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var rawTag in ReadIntEnumerable(rawTags))
            {
                if (rawTag < 0) continue;

                if (!TryReadFoodTagName(rawTag, out var tagName)) return false;

                var normalized = FoodTags.NormalizeName(tagName) ?? tagName;
                seen.Add(normalized);
            }
        }
        catch
        {
            return false;
        }

        tags = seen.ToArray();
        return true;
    }

    private static bool TryReadFoodTagName(int tagId, out string tagName)
    {
        try
        {
            tagName = InvokeStatic(DataBaseLanguageTypeName, "GetFoodTag", new object?[] { tagId })?.ToString()?.Trim() ?? "";
            return !string.IsNullOrWhiteSpace(tagName);
        }
        catch
        {
            tagName = "";
            return false;
        }
    }

    private static bool TryStoreMismatchedCookResultInWarmer(PendingCookingCollection pending, object cookedFood, int actualFoodId, out string message)
    {
        try
        {
            var configure = GetSingletonInstance(IzakayaConfigureTypeName);
            if (configure == null)
            {
                message = "当前料理暂存容器不可用";
                return false;
            }

            var beforeCount = CountStoredFoods(configure, actualFoodId);
            if (!TryInvokeStoreFood(configure, cookedFood, out var storeDiagnostic))
            {
                message = storeDiagnostic;
                return false;
            }

            var afterCount = CountStoredFoods(configure, actualFoodId);
            if (actualFoodId >= 0 && beforeCount >= 0 && afterCount >= 0 && afterCount <= beforeCount)
            {
                message = $"StoreFood 后未读取到保温箱数量增加（料理 #{actualFoodId}: {beforeCount}->{afterCount}）";
                return false;
            }

            var resetMessage = TryResetCookControllerAfterWarmerStore(pending.CookController, cookedFood);
            var storeStatus = actualFoodId >= 0 && beforeCount >= 0 && afterCount >= 0
                ? $"保温箱数量 {beforeCount}->{afterCount}。"
                : "已调用游戏 StoreFood。";
            message = string.IsNullOrWhiteSpace(resetMessage) ? storeStatus : $"{storeStatus}{resetMessage}";
            return true;
        }
        catch (Exception ex)
        {
            message = ex.GetBaseException().Message;
            return false;
        }
    }

    private static bool TryInvokeStoreFood(object configure, object cookedFood, out string diagnostic)
    {
        diagnostic = "";
        var methods = configure.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(method => string.Equals(method.Name, "StoreFood", StringComparison.Ordinal))
            .OrderByDescending(method => method.GetParameters().Length == 2)
            .ThenBy(method => method.GetParameters().Length)
            .ToArray();
        if (methods.Length == 0)
        {
            diagnostic = "未找到 StoreFood 方法";
            return false;
        }

        var errors = new List<string>();
        foreach (var method in methods)
        {
            if (!TryBuildStoreFoodArguments(method, cookedFood, out var args, out var skippedReason))
            {
                errors.Add($"{FormatMethodSignature(method)}: {skippedReason}");
                continue;
            }

            try
            {
                method.Invoke(configure, args);
                return true;
            }
            catch (Exception ex)
            {
                var root = ex.GetBaseException();
                errors.Add($"{FormatMethodSignature(method)}: {root.GetType().Name}: {root.Message}");
            }
        }

        diagnostic = errors.Count == 0
            ? "未找到可用的 StoreFood 入口"
            : $"StoreFood 调用失败：{string.Join("；", errors.Distinct(StringComparer.Ordinal))}";
        return false;
    }

    private static bool TryBuildStoreFoodArguments(
        MethodInfo method,
        object cookedFood,
        out object?[] args,
        out string skippedReason)
    {
        args = Array.Empty<object?>();
        skippedReason = "";
        var parameters = method.GetParameters();
        if (parameters.Length == 1)
        {
            if (!parameters[0].ParameterType.IsInstanceOfType(cookedFood))
            {
                skippedReason = $"第一个参数需要 {parameters[0].ParameterType.FullName}，实际为 {cookedFood.GetType().FullName}";
                return false;
            }

            args = new object?[] { cookedFood };
            return true;
        }

        if (parameters.Length == 2)
        {
            if (!parameters[0].ParameterType.IsInstanceOfType(cookedFood))
            {
                skippedReason = $"第一个参数需要 {parameters[0].ParameterType.FullName}，实际为 {cookedFood.GetType().FullName}";
                return false;
            }

            if (!TryBuildStoreFoodSenderArgument(parameters[1].ParameterType, out var sender))
            {
                skippedReason = $"第二个参数不是可传入的整数类型：{parameters[1].ParameterType.FullName}";
                return false;
            }

            args = new[] { cookedFood, sender };
            return true;
        }

        skippedReason = $"参数数量不支持：{parameters.Length}";
        return false;
    }

    private static bool TryBuildStoreFoodSenderArgument(Type parameterType, out object? value)
    {
        value = null;
        if (parameterType.IsByRef)
        {
            parameterType = parameterType.GetElementType() ?? parameterType;
        }

        try
        {
            if (parameterType.IsEnum)
            {
                value = Enum.ToObject(parameterType, -1);
                return true;
            }

            if (parameterType == typeof(int))
            {
                value = -1;
                return true;
            }

            if (parameterType.IsPrimitive)
            {
                value = Convert.ChangeType(-1, parameterType);
                return true;
            }
        }
        catch
        {
            value = null;
            return false;
        }

        return false;
    }

    private static string FormatMethodSignature(MethodInfo method)
    {
        var parameters = string.Join(", ", method.GetParameters().Select(parameter => parameter.ParameterType.Name));
        return $"{method.DeclaringType?.FullName ?? method.Name}.{method.Name}({parameters})";
    }

    private static object? ReadStoredFoodList(object configure)
    {
        return ReadMember(configure, "StoredFoods")
            ?? TryInvokeInstanceValue(configure, "get_StoredFoods")
            ?? TryInvokeInstanceValue(configure, "GetStoredFoods");
    }

    private static int CountStoredFoods(object configure, int foodId)
    {
        if (foodId < 0) return -1;

        var storedFoods = ReadStoredFoodList(configure);
        if (storedFoods == null) return -1;

        var rawCount = ToInt(TryInvokeInstanceValue(storedFoods, "get_Count")
            ?? ReadMember(storedFoods, "Count")
            ?? ReadMember(storedFoods, "_size"), -1);
        var count = 0;
        var scanned = 0;
        foreach (var food in ReadObjectEnumerable(storedFoods))
        {
            scanned++;
            if (IsSellable(food, sellableType: 0, id: foodId))
            {
                count++;
            }
        }

        return scanned == 0 && rawCount > 0 ? -1 : count;
    }

    private static string TryResetCookControllerAfterWarmerStore(object cookController, object cookedFood)
    {
        try
        {
            TryInvokeInstance(cookController, "CloseCookingVisual", Array.Empty<object?>());
            TryClearCookController(cookController, cookedFood);

            var phaseAfterClear = ToInt(TryInvokeInstanceValue(cookController, "get_Phase"), -1);
            if (phaseAfterClear <= 0)
            {
                return "厨具阶段已恢复空闲。";
            }

            return $"厨具复位状态异常（phase={phaseAfterClear}），成品已进入保温箱。";
        }
        catch (Exception ex)
        {
            return $"厨具复位诊断：{ex.GetBaseException().Message}。";
        }
    }

    private static OrderPreparationRequest BuildOrderRequestFromCookingTarget(CookingCollectionTarget target)
    {
        return new OrderPreparationRequest
        {
            TraceId = target.TraceId,
            OrderKey = target.OrderKey,
            DeskCode = target.DeskCode,
            GuestId = target.GuestId,
            GuestName = target.GuestName,
            SpecialBusinessRole = target.SpecialBusinessRole,
            FoodTag = target.FoodTag,
            BeverageTag = target.BeverageTag,
            MatchFoodId = target.MatchFoodId,
            MatchBeverageId = target.MatchBeverageId,
            FoodId = target.FoodId,
            RecipeId = target.RecipeId,
            RecipeName = target.FoodName,
            ExtraIngredientIds = target.ExtraIngredientIds,
            PredictedFoodTags = target.PredictedFoodTags,
            WackyTargetFoodTags = target.WackyTargetFoodTags,
            ExecutionMode = target.ExecutionMode,
            ExecutionReason = target.ExecutionReason,
            BeverageId = target.BeverageId,
            BeverageName = target.BeverageName,
            AutoCompleteOrder = target.AutoCompleteOrder,
        };
    }

    private static void TryCompleteCookControllerAfterDirectDelivery(object cookController, object cookedFood)
    {
        try
        {
            TryInvokeInstance(cookController, "AfterPlayerExtract", Array.Empty<object?>());
            TryInvokeInstance(cookController, "CloseCookingVisual", Array.Empty<object?>());
            TryClearCookController(cookController, cookedFood);
        }
        catch
        {
            // 料理已成功送达订单；厨具清理失败只能留给后续轮询或玩家手动处理，不能回滚订单状态。
        }
    }
}
