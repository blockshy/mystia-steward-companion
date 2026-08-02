namespace MystiaStewardCompanion.Save;

internal enum AutomationOrderActionKind
{
    PrepareRare,
    CompleteRare,
    CompleteNormal,
}

/// <summary>
/// Validates automation stage combinations before any game runtime access or side effect.
/// </summary>
internal static class AutomationOrderConfigurationPolicy
{
    public const string InvalidReasonCode = "automation-config-invalid";

    public static bool TryValidate(
        AutomationOrderActionKind actionKind,
        bool autoTakeBeverage,
        bool autoCollectCooking,
        bool autoDeliverFood,
        bool autoCompleteOrder,
        out string error)
    {
        var targetLabel = actionKind == AutomationOrderActionKind.CompleteNormal ? "普客" : "稀客";
        var directDeliveryEnabled = autoTakeBeverage
            || autoCollectCooking
            || autoDeliverFood;
        if (directDeliveryEnabled && !autoCompleteOrder)
        {
            error = $"{targetLabel}自动送达酒水或料理时必须同时开启“自动完成订单”；"
                + "本次请求已在执行任何游戏操作前拒绝。";
            return false;
        }

        if (actionKind == AutomationOrderActionKind.CompleteRare && !autoCompleteOrder)
        {
            error = "稀客订单完成入口要求开启“自动完成订单”；"
                + "本次请求已在执行任何游戏操作前拒绝。";
            return false;
        }

        error = "";
        return true;
    }
}
