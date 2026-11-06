namespace MystiaStewardCompanion.Save;

/// <summary>
/// 以不可变原生订单绑定读取终态。必须在权限检查和任何 wrapper 读取之前执行：
/// 暂停或主设备切换不能阻止已结束订单退休；已提交送达的事务仍走其独立清理协议。
/// </summary>
internal static class AutomationCookingTerminalReceiptPolicy
{
    public static bool TryFindOrderTerminatedBeforeDelivery(bool foodDeliveryCommitted,
        RuntimeOrderBindingToken? orderBinding, out RuntimeOrderTerminalReceipt receipt)
    {
        receipt = default;
        return !foodDeliveryCommitted && orderBinding.HasValue
            && RuntimeOrderTerminalReceiptStore.TryFind(orderBinding.Value, out receipt);
    }
}