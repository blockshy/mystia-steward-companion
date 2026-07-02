using MystiaStewardCompanion.LocalApi;

namespace MystiaStewardCompanion.Save;

internal static partial class RuntimeOrderPreparationService
{
    private const int PatientRecoverPerDeliveredItem = 15;

    private enum RuntimeDeliveryItemKind
    {
        Food,
        Beverage,
    }

    /// <summary>
    /// 按游戏原生上菜顺序提交一项料理或酒水。
    /// </summary>
    /// <param name="runtimeOrder">已匹配到的运行时订单、客人控制器和客人管理器。</param>
    /// <param name="sellable">准备送达的游戏 Sellable 对象。</param>
    /// <param name="kind">送达对象类型，用于选择订单字段和桌面显示器入口。</param>
    /// <param name="itemName">用户可读名称，仅用于错误信息。</param>
    /// <returns>是否完成送达提交，以及可展示给前端的诊断消息。</returns>
    /// <remarks>
    /// 旧实现直接写入 <c>ServFood</c> / <c>ServBeverage</c>，会绕过桌面 Sprite 和原生“空中待送达”状态。
    /// 这里先验证桌面显示器和 Sprite，再设置 <c>Served*InAir</c>、更新桌面显示并提交最终字段。
    /// 如果中途失败，会尽力清理空中状态，避免订单残留半提交数据。
    /// </remarks>
    private static (bool Ok, string Message) TryCommitRuntimeDelivery(
        RuntimeOrderMatch runtimeOrder,
        object sellable,
        RuntimeDeliveryItemKind kind,
        string itemName)
    {
        if (runtimeOrder.Order == null)
        {
            return (false, $"无法送达 {itemName}：订单对象不可用。");
        }

        if (runtimeOrder.Controller == null)
        {
            return (false, $"无法送达 {itemName}：客人控制器不可用。");
        }

        if (!TryReadSellableSprite(sellable, out var sprite, out var spriteMessage))
        {
            return (false, $"无法送达 {itemName}：{spriteMessage}");
        }

        if (!TryFindGuestTableDisplayer(runtimeOrder.Order, runtimeOrder.Controller, out var tableDisplayer, out var tableMessage))
        {
            return (false, $"无法送达 {itemName}：{tableMessage}");
        }

        if (!TrySetOrderInAir(runtimeOrder.Order, kind, sellable, out var inAirMessage))
        {
            return (false, $"无法送达 {itemName}：{inAirMessage}");
        }

        try
        {
            if (!TryUpdateGuestTableVisual(tableDisplayer, kind, sprite, out var visualMessage))
            {
                TryClearOrderInAir(runtimeOrder.Order, kind);
                return (false, $"无法送达 {itemName}：{visualMessage}");
            }

            TryClearOrderInAir(runtimeOrder.Order, kind);
            if (!TrySetOrderServed(runtimeOrder.Order, kind, sellable, out var servedMessage))
            {
                TryUpdateGuestTableVisual(tableDisplayer, kind, null, out _);
                return (false, $"无法送达 {itemName}：{servedMessage}");
            }

            return (true, $"{itemName} 已按游戏送达流程提交。");
        }
        catch (Exception ex)
        {
            TryClearOrderInAir(runtimeOrder.Order, kind);
            return (false, $"无法送达 {itemName}：{ex.GetBaseException().Message}");
        }
    }

    private static bool TrySetOrderInAir(object order, RuntimeDeliveryItemKind kind, object sellable, out string message)
    {
        var setterName = kind == RuntimeDeliveryItemKind.Food ? "set_ServedFoodInAir" : "set_ServedBeverageInAir";
        if (TryInvokeInstance(order, setterName, new object?[] { sellable }))
        {
            message = "";
            return true;
        }

        message = $"未找到或无法调用订单待送达字段入口 {setterName}。";
        return false;
    }

    private static void TryClearOrderInAir(object order, RuntimeDeliveryItemKind kind)
    {
        var setterName = kind == RuntimeDeliveryItemKind.Food ? "set_ServedFoodInAir" : "set_ServedBeverageInAir";
        TryInvokeInstance(order, setterName, new object?[] { null });
    }

    private static bool TrySetOrderServed(object order, RuntimeDeliveryItemKind kind, object sellable, out string message)
    {
        var setterName = kind == RuntimeDeliveryItemKind.Food ? "set_ServFood" : "set_ServBeverage";
        var readName = kind == RuntimeDeliveryItemKind.Food ? "ServFood" : "ServBeverage";
        var getterName = kind == RuntimeDeliveryItemKind.Food ? "get_ServFood" : "get_ServBeverage";
        if (TryInvokeInstance(order, setterName, new object?[] { sellable })
            && (ReadMember(order, readName) ?? TryInvokeInstanceValue(order, getterName)) != null)
        {
            message = "";
            return true;
        }

        message = $"未找到或无法调用订单最终送达字段入口 {setterName}。";
        return false;
    }

    /// <summary>
    /// 在订单未满足时恢复顾客耐心，等价于原生上菜面板的 onRecoverPatient 闭包。
    /// </summary>
    /// <remarks>
    /// 原游戏在一轮上菜后若订单仍未满足，会按成功提交的料理/酒水数量恢复耐心，每项固定 15。
    /// 自动化可能一轮内同时提交料理和酒水，因此恢复动作必须由调用方在本轮全部提交后统一触发。
    /// </remarks>
    private static bool TryRecoverPatientAfterPartialDelivery(RuntimeOrderMatch runtimeOrder, int deliveredItemCount, out string message)
    {
        if (deliveredItemCount <= 0)
        {
            message = "";
            return true;
        }

        if (runtimeOrder.Order == null || runtimeOrder.Controller == null)
        {
            message = "订单或客人控制器不可用，无法恢复顾客耐心。";
            return false;
        }

        if (ReadBool(InvokeInstance(runtimeOrder.Order, "get_IsFullfilled", Array.Empty<object?>())))
        {
            message = "";
            return true;
        }

        if (IsManualControlledOrder(runtimeOrder.Order, runtimeOrder.Controller))
        {
            message = "";
            return true;
        }

        if (!TryReadPatientBounds(runtimeOrder.Controller, out var currentPatient, out var maxPatient, out var patientMessage))
        {
            message = $"订单尚未补齐，但{patientMessage}，已跳过恢复顾客耐心以避免耐心条越界。";
            return true;
        }

        if (maxPatient <= 0)
        {
            message = $"订单尚未补齐，但顾客耐心上限异常（{maxPatient}），已跳过恢复顾客耐心。";
            return true;
        }

        if (currentPatient > maxPatient)
        {
            if (TryInvokeInstance(runtimeOrder.Controller, "SetPatient", new object?[] { maxPatient }))
            {
                message = $"订单尚未补齐，检测到顾客耐心 {currentPatient}/{maxPatient} 已超过上限，已校正为上限值。";
                return true;
            }

            message = $"订单尚未补齐，检测到顾客耐心 {currentPatient}/{maxPatient} 已超过上限，但无法调用 GuestGroupController.SetPatient 校正。";
            return false;
        }

        var requestedRecoverValue = PatientRecoverPerDeliveredItem * deliveredItemCount;
        var remainingPatient = maxPatient - Math.Max(0, currentPatient);
        if (remainingPatient <= 0)
        {
            message = "订单尚未补齐，顾客耐心已满，本轮不恢复耐心。";
            return true;
        }

        var recoverValue = Math.Min(requestedRecoverValue, remainingPatient);
        if (TryInvokeInstance(runtimeOrder.Controller, "AddPatient", new object?[] { recoverValue }))
        {
            message = recoverValue == requestedRecoverValue
                ? $"订单尚未补齐，已按游戏规则恢复顾客耐心 {recoverValue}。"
                : $"订单尚未补齐，已按耐心上限恢复顾客耐心 {recoverValue}（原计划 {requestedRecoverValue}）。";
            return true;
        }

        message = "订单尚未补齐，但无法调用 GuestGroupController.AddPatient 恢复顾客耐心。";
        return false;
    }

    private static bool TryReadPatientBounds(object controller, out int currentPatient, out int maxPatient, out string message)
    {
        currentPatient = 0;
        maxPatient = 0;

        var currentValue = ReadMember(controller, "CurrentPatient") ?? TryInvokeInstanceValue(controller, "get_CurrentPatient");
        if (!TryReadIntValue(currentValue, out currentPatient))
        {
            message = "无法读取 GuestGroupController.CurrentPatient";
            return false;
        }

        var maxValue = ReadMember(controller, "MaxPatient") ?? TryInvokeInstanceValue(controller, "get_MaxPatient");
        if (!TryReadIntValue(maxValue, out maxPatient))
        {
            message = "无法读取 GuestGroupController.MaxPatient";
            return false;
        }

        message = "";
        return true;
    }

    private static bool TryReadIntValue(object? value, out int number)
    {
        number = 0;
        if (value == null) return false;

        number = ToInt(value, int.MinValue);
        return number != int.MinValue;
    }

    private static bool AddPatientRecoveryStepIfNeeded(
        OrderPreparationResult result,
        RuntimeOrderMatch runtimeOrder,
        int deliveredItemCount)
    {
        if (!TryRecoverPatientAfterPartialDelivery(runtimeOrder, deliveredItemCount, out var message))
        {
            AddFailure(result, "恢复顾客耐心", message);
            return false;
        }

        if (!string.IsNullOrWhiteSpace(message))
        {
            result.Steps.Add(new OrderPreparationStep
            {
                Name = "恢复顾客耐心",
                Ok = true,
                Message = message,
            });
        }

        return true;
    }

    private static bool TryEvaluateOrderIfReady(
        OrderPreparationResult result,
        RuntimeOrderMatch runtimeOrder,
        string stepName,
        string orderLabel,
        bool allowControllerMissing = false)
    {
        var evaluation = TryEvaluateRuntimeOrderIfReady(runtimeOrder, orderLabel, allowControllerMissing);
        if (!evaluation.Ok)
        {
            AddFailure(result, stepName, evaluation.Message);
            return false;
        }

        if (evaluation.Completed)
        {
            result.CompletedOrder = true;
        }

        result.Steps.Add(new OrderPreparationStep
        {
            Name = stepName,
            Ok = true,
            Skipped = evaluation.Skipped,
            Message = evaluation.Message,
        });
        return true;
    }

    private static (bool Ok, bool Completed, bool Skipped, string Message) TryEvaluateRuntimeOrderIfReady(
        RuntimeOrderMatch runtimeOrder,
        string orderLabel,
        bool allowControllerMissing = false)
    {
        if (runtimeOrder.Order == null || runtimeOrder.Manager == null)
        {
            return (false, false, false, "订单或客人管理器不可用，无法调用游戏评价流程。");
        }

        if (!ReadBool(InvokeInstance(runtimeOrder.Order, "get_IsFullfilled", Array.Empty<object?>())))
        {
            return (true, false, true, "订单尚未同时满足料理和酒水，等待下一轮补齐。");
        }

        if (runtimeOrder.Controller == null)
        {
            if (allowControllerMissing)
            {
                return (true, false, true, "订单已满足，但暂未读取到客人控制器，等待下一轮触发评价。");
            }

            return (false, false, false, "已匹配订单，但未找到对应客人控制器，无法调用游戏评价流程。");
        }

        if (ReadBool(ReadMember(runtimeOrder.Controller, "HasEvaluated") ?? TryInvokeInstanceValue(runtimeOrder.Controller, "get_HasEvaluated")))
        {
            return (true, true, true, $"{orderLabel}已触发过评价，本次不重复调用。");
        }

        InvokeInstance(runtimeOrder.Manager, "EvaluateOrder", new object?[] { runtimeOrder.Controller, false, null });
        return (true, true, false, $"已调用游戏评价流程完成{orderLabel}。");
    }

    private static bool IsManualControlledOrder(object order, object controller)
    {
        return ReadBool(ReadMember(order, "ManualOrder") ?? TryInvokeInstanceValue(order, "get_ManualOrder"))
            || ReadBool(ReadMember(controller, "IsControlled") ?? TryInvokeInstanceValue(controller, "get_IsControlled"));
    }

    private static bool TryReadSellableSprite(object sellable, out object? sprite, out string message)
    {
        var text = ReadMember(sellable, "Text") ?? TryInvokeInstanceValue(sellable, "get_Text");
        if (text != null)
        {
            sprite = ReadMember(text, "Visual")
                ?? TryInvokeInstanceValue(text, "get_Visual")
                ?? ReadMember(text, "_Visual_k__BackingField")
                ?? ReadMember(text, "<Visual>k__BackingField");
            if (sprite != null)
            {
                message = "";
                return true;
            }
        }

        try
        {
            // 该 helper 依赖运行时赋值的 BGGetter；未初始化时会返回 null。
            // 原生上菜面板的桌面显示直接读取 sellable.Text.Visual，因此这里只作为兜底。
            sprite = InvokeStatic(SellablePropertyHelperTypeName, "GetSellabeBGSprite", new object?[] { sellable });
            if (sprite != null)
            {
                message = "";
                return true;
            }

            message = "游戏未返回该料理或酒水的桌面 Sprite。";
            return false;
        }
        catch (Exception ex)
        {
            sprite = null;
            message = $"读取桌面 Sprite 失败：{ex.GetBaseException().Message}";
            return false;
        }
    }

    private static bool TryFindGuestTableDisplayer(object order, object controller, out object tableDisplayer, out string message)
    {
        tableDisplayer = new object();
        var deskCode = ToInt(ReadMember(order, "DeskCode")
            ?? TryInvokeInstanceValue(order, "get_DeskCode")
            ?? ReadMember(controller, "DeskCode")
            ?? TryInvokeInstanceValue(controller, "get_DeskCode"), -1);
        if (deskCode < 0)
        {
            message = "未读取到订单桌号，无法定位桌面显示器。";
            return false;
        }

        object? tileManager;
        try
        {
            tileManager = GetSingletonInstance(TileManagerTypeName);
        }
        catch (Exception ex)
        {
            message = $"读取 TileManager 失败：{ex.GetBaseException().Message}";
            return false;
        }

        if (tileManager == null)
        {
            message = "当前 TileManager 不可用，请确认已进入夜晚经营场景。";
            return false;
        }

        var guestTables = ReadMember(tileManager, "GuestTables") ?? TryInvokeInstanceValue(tileManager, "get_GuestTables");
        if (guestTables == null)
        {
            message = "未读取到 TileManager.GuestTables。";
            return false;
        }

        object? tableData = null;
        try
        {
            tableData = InvokeInstance(guestTables, "get_Item", new object?[] { deskCode });
        }
        catch
        {
            // 字典中没有该桌号时保持 null，由下方返回可诊断错误。
        }

        if (tableData == null)
        {
            message = $"TileManager.GuestTables 中没有桌 {deskCode + 1} 的数据。";
            return false;
        }

        var displayer = ReadMember(tableData, "tableDisplayer") ?? ReadMember(tableData, "TableDisplayer");
        if (displayer == null)
        {
            message = $"桌 {deskCode + 1} 的 GuestTableData 未包含 tableDisplayer。";
            return false;
        }

        tableDisplayer = displayer;
        message = "";
        return true;
    }

    private static bool TryUpdateGuestTableVisual(object tableDisplayer, RuntimeDeliveryItemKind kind, object? sprite, out string message)
    {
        var methodName = kind == RuntimeDeliveryItemKind.Food ? "SetFoodVisual" : "SetBeverageVisual";
        if (TryInvokeInstance(tableDisplayer, methodName, new[] { sprite }))
        {
            message = "";
            return true;
        }

        message = $"无法调用 GuestTableDisplayer.{methodName}。";
        return false;
    }

}
