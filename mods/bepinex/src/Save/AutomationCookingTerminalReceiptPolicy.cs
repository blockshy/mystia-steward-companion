namespace MystiaStewardCompanion.Save;

/// <summary>
/// Resolves a wrapper-free terminal receipt for a cooking job that has not committed delivery.
/// </summary>
/// <remarks>
/// This gate deliberately reads only immutable scalar receipt state. Callers must run it before
/// acquiring runtime wrappers or attempting any cooker, warmer, delivery, or evaluation side effect.
/// </remarks>
internal static class AutomationCookingTerminalReceiptPolicy
{
    public static bool TryFindOrderTerminatedBeforeDelivery(
        bool foodDeliveryCommitted,
        RuntimeOrderBindingToken? orderBinding,
        out RuntimeOrderTerminalReceipt receipt)
    {
        receipt = default;
        return !foodDeliveryCommitted
            && orderBinding.HasValue
            && RuntimeOrderTerminalReceiptStore.TryFind(orderBinding.Value, out receipt);
    }
}
