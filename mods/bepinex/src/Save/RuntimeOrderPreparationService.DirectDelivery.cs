using MystiaStewardCompanion.LocalApi;

namespace MystiaStewardCompanion.Save;

internal static partial class RuntimeOrderPreparationService
{
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

    private static int ReadSellableType(object item)
    {
        var value = TryInvokeInstanceValue(item, "get_Type") ?? ReadMember(item, "Type");
        return ToInt(value);
    }

    private static int ReadSellableId(object item)
    {
        var value = TryInvokeInstanceValue(item, "get_id")
            ?? TryInvokeInstanceValue(item, "get_Id")
            ?? ReadMember(item, "id")
            ?? ReadMember(item, "Id");
        return ToInt(value);
    }

    /// <summary>
    /// 将已经出锅的料理直接送达给登记的目标订单。
    /// </summary>
    /// <remarks>
    /// 送达失败时不清理厨具，保留成品供下一轮重试或玩家手动处理；送达成功后才执行厨具清理。
    /// </remarks>
    private static (bool Remove, string Message) TryDeliverPendingCookedFood(PendingCookingCollection pending, object cookedFood)
    {
        var target = pending.Target;
        if (target.FoodId >= 0 && !IsSellable(cookedFood, sellableType: 0, id: target.FoodId))
        {
            return (true, $"{pending.RecipeName} 已完成，但成品不是目标料理 {target.FoodName}（料理 #{target.FoodId}），已停止自动送达并保留在厨具中。");
        }

        var request = BuildOrderRequestFromCookingTarget(target);
        var runtimeOrder = target.Kind == CookingCollectionTargetKind.NormalOrder
            ? FindRuntimeNormalOrder(request)
            : FindRuntimeOrder(request);
        if (runtimeOrder.Order == null || runtimeOrder.Manager == null)
        {
            return ShouldStopPendingDirectDelivery(pending, $"未找到目标订单对象。{runtimeOrder.Diagnostic}");
        }

        if (runtimeOrder.Controller == null)
        {
            return ShouldStopPendingDirectDelivery(pending, $"已找到目标订单，但未读取到可执行客人控制器；该订单可能只残留在 HUD 中。{runtimeOrder.Diagnostic}");
        }

        if (ReadOrderServedFood(runtimeOrder.Order) != null)
        {
            TryCompleteCookControllerAfterDirectDelivery(pending.CookController, cookedFood);
            return (true, $"{pending.RecipeName} 已完成，但目标订单已有料理，已释放厨具。");
        }

        var pendingFood = ReadMember(runtimeOrder.Order, "ServedFoodInAir");
        if (pendingFood != null && !IsSellable(pendingFood, sellableType: 0, id: target.FoodId))
        {
            return ShouldStopPendingDirectDelivery(pending, "订单已有其他待送达料理，暂不覆盖。");
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
        if (target.Kind == CookingCollectionTargetKind.NormalOrder && target.AutoCompleteOrder)
        {
            var evaluation = TryEvaluateRuntimeOrderIfReady(runtimeOrder, "当前普客订单", allowControllerMissing: true);
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

        return (true, message);
    }

    private static (bool Remove, string Message) ShouldStopPendingDirectDelivery(PendingCookingCollection pending, string message)
    {
        if (DateTime.UtcNow - pending.CreatedAtUtc >= PendingCookingIdleTimeout)
        {
            return (true, $"{pending.RecipeName} 自动送达已停止：{message} 成品保留在厨具中。");
        }

        return (false, $"{pending.RecipeName} 已完成，等待直接送达：{message}");
    }

    private static OrderPreparationRequest BuildOrderRequestFromCookingTarget(CookingCollectionTarget target)
    {
        return new OrderPreparationRequest
        {
            OrderKey = target.OrderKey,
            DeskCode = target.DeskCode,
            GuestId = target.GuestId,
            GuestName = target.GuestName,
            FoodTag = target.FoodTag,
            BeverageTag = target.BeverageTag,
            FoodId = target.FoodId,
            RecipeName = target.FoodName,
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
