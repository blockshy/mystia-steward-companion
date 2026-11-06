using System.Reflection;
using MystiaStewardCompanion.LocalApi;

namespace MystiaStewardCompanion.Save;

internal static partial class RuntimeOrderPreparationService
{
    /// <summary>
    /// 只在 Unity 主线程、本次有界库存读取中枚举保温箱；返回数量不携带原生对象。
    /// 未完整读取时返回 null。仍被旧烹饪事务持有的成品暂不向另一订单提供。
    /// </summary>
    internal static int? CountStoredDarkCuisine()
        => TryReadStoredDarkCuisine(out _, out var items, out _) ? items.Count : null;

    private static bool TryReadStoredDarkCuisine(
        out object? configure, out IReadOnlyList<(nint Pointer, object Food)> items, out string diagnostic)
    {
        configure = null; items = Array.Empty<(nint, object)>(); diagnostic = "";
        try
        {
            configure = GetSingletonInstance(IzakayaConfigureTypeName);
            if (configure == null || !RuntimeConcreteCollectionReader.TryReadList(ReadStoredFoodList(configure), out var values, out _)
                || values.Count > 4096)
            {
                diagnostic = "保温箱成品列表暂不可完整读取。";
                return false;
            }
            var pointers = new HashSet<nint>();
            var result = new List<(nint Pointer, object Food)>();
            HashSet<nint> owned;
            lock (AutomationCookingJobLock)
                owned = AutomationCookingJobs.Select(job => job.CurrentResultPointer).Where(pointer => pointer != 0).ToHashSet();
            foreach (var item in values)
            {
                if (item == null || !TryReadCookControllerFoodResultIdentity(item, "StoredFoods", out var identity, out diagnostic)
                    || !TryReadNativeObjectPointer(item, out var pointer) || !pointers.Add(pointer))
                {
                    diagnostic = "保温箱成品身份缺失或重复，暂停自动取餐。";
                    return false;
                }
                if (identity.IsDarkCuisine && !owned.Contains(pointer)) result.Add((pointer, item));
            }
            // 指针仅用于这一次同步事务的精确实例选择，绝不保存为跨场景/跨帧身份。
            items = result.OrderBy(item => (long)item.Pointer).ToArray();
            return true;
        }
        catch (Exception ex)
        {
            diagnostic = "读取保温箱失败：" + ex.GetBaseException().Message;
            return false;
        }
    }

    /// <summary>
    /// 先验证一个精确成品实例，再沿原生 RemoveStoredFood → 送达事务处理。
    /// 移除、送达和必要的回存都在同一主线程调用内完成；任何提交不确定立即登记订单屏障，
    /// 不能因 HTTP 响应丢失或下一轮快照滞后再次取出/送达。不存在成品时才允许普通生产路径。
    /// </summary>
    private static bool TryUseStoredDarkCuisine(
        OrderPreparationRequest request, OrderPreparationResult result, CookingCollectionTarget target,
        RuntimeOrderMatch runtimeOrder, bool requireStock)
    {
        if (!RequestsDarkCuisine(request) || !request.AutoCompleteOrder
            || !(request.AutoCollectCooking || request.AutoDeliverFood)) return false;
        if (TryFindAutomationCookingJob(target, out _, out _)) return false;
        if (!TryValidateDarkCuisineTaskTarget(target, out var taskError)) return Wait(taskError);
        if (!TryReadStoredDarkCuisine(out var configure, out var items, out var readError)) return Wait(readError);
        if (items.Count == 0) return requireStock && Wait("保温箱黑暗料理已被取走，等待最新供给方案。");
        var selected = items[0];
        if (runtimeOrder.Order == null || runtimeOrder.Controller == null
            || !TryMatchRuntimeOrderBinding(target.OrderBinding, runtimeOrder.Order, runtimeOrder.Controller, out _)
            || !TryValidateCookingTargetOrderLifecycle(target, "before-stored-food-removal", out _)
            || !TryInspectStoredFoodIdentity(configure!, selected.Food, out var stillStored, out _) || !stillStored)
            return Wait("取餐前订单或保温箱成品已变化，未执行移除。");
        if (!TryReadOrderServedItem(runtimeOrder.Order, RuntimeDeliveryItemKind.Food, out var served, out _)
            || !TryReadOrderInAirItem(runtimeOrder.Order, RuntimeDeliveryItemKind.Food, out var pending, out _)
            || served != null || pending != null)
            return Wait("订单已有料理或送达状态未确认，不重复取餐。");
        var method = InvocationMetadata.FindExactMethod(configure!.GetType(), "RemoveStoredFood",
            BindingFlags.Public | BindingFlags.Instance, new[] { selected.Food.GetType(), typeof(int) });
        if (method?.ReturnType != typeof(void)) return Wait("当前游戏的保温箱取餐入口不可验证。");
        try
        {
            // 原生 0x605120 先 Remove 精确 Sellable，再通知伙伴和额外回调。异常后不能把
            // “列表已不含它”当作整个操作未发生，也不能无条件重试移除或回存。
            method.Invoke(configure, new object?[] { selected.Food, -1 });
        }
        catch (Exception ex)
        {
            return Block("取出黑暗料理的结果无法确认，请检查保温箱与订单：" + ex.GetBaseException().Message);
        }
        try
        {
            // RemoveStoredFood 会进入原生回调，因此先验证会话/订单仍有效，再访问保温箱。
            // 随后从本轮捕获索引重取订单，不能把回调之前取得的 wrapper 当作最新订单使用。
            if (!TryValidateCookingTargetOrderLifecycle(target, "after-stored-food-removal", out _)
                || !TryValidateDarkCuisineTaskTarget(target, out _)
                || !TryInspectStoredFoodIdentity(configure, selected.Food, out var remains, out _) || remains)
                return Block("取餐后成品归属或订单生命周期无法确认，已停止此订单以避免重复消费。");
            runtimeOrder = FindRuntimeOrder(request);
            if (runtimeOrder.Order == null || runtimeOrder.Controller == null
                || !TryMatchRuntimeOrderBinding(target.OrderBinding, runtimeOrder.Order, runtimeOrder.Controller, out _))
                return Block("取餐后无法重新确认原订单身份，已停止此订单以避免送错对象。");
            var delivery = TryCommitRuntimeDelivery(runtimeOrder, selected.Food, RuntimeDeliveryItemKind.Food, target.FoodName);
            if (delivery.CommitUncertain) return Block(delivery.Message);
            if (!delivery.Ok)
            {
                // 只有明确未送达、原会话仍有效且箱中没有同一实例，才允许回存一次。
                if (!TryValidateCookingTargetOrderLifecycle(target, "before-stored-food-return", out _)
                    || !TryInspectStoredFoodIdentity(configure, selected.Food, out var alreadyReturned, out _) || alreadyReturned
                    || !TryInvokeStoreFood(configure, selected.Food, out _, out _)
                    || !TryInspectStoredFoodIdentity(configure, selected.Food, out var returned, out _) || !returned)
                    return Block("黑暗料理未送达，回存结果无法确认，请检查保温箱和订单。");
                return Wait("黑暗料理已放回保温箱，等待订单可送达：" + delivery.Message);
            }
            result.ServedFood = true;
            result.Automation.Stage = "cooking-delivery";
            result.Steps.Add(new OrderPreparationStep
            {
                Code = OrderPreparationStepCodes.FoodDelivered, Name = "使用保温箱料理", Ok = true,
                Message = "已将保温箱内的黑暗料理送达此任务订单；未重复扣料或占用厨具。",
            });
            TryEvaluateMatchedAutomationOrderIfReady(result, request, runtimeOrder,
                "触发上菜评价", "当前订单", target);
            return true;
        }
        catch (Exception ex)
        {
            // 已执行移除后的任何异常均属于事务不确定，不能退回普通开锅或下一轮再取餐。
            return Block("保温箱成品取出后的送达结果无法确认：" + ex.GetBaseException().Message);
        }

        bool Wait(string message)
        {
            AddSkipped(result, "使用保温箱料理", message, OrderPreparationStepCodes.CookingCookerWaiting);
            return true;
        }
        bool Block(string message)
        {
            AddFailure(result, "使用保温箱料理", message, OrderPreparationStepCodes.FoodDeliveryCommitUncertain);
            RecordOrderSafetyBarrierIfNeeded(OrderPreparationStepCodes.FoodDeliveryCommitUncertain, target, message);
            return true;
        }
    }
}