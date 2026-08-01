namespace MystiaStewardCompanion.Save;

internal enum RuntimeOrderKind
{
    Normal,
    Special,
}

internal readonly record struct RuntimeOrderTypeResolution(
    bool RecognizedOrderType,
    bool Resolved,
    RuntimeOrderKind Kind,
    object? ReadableOrder,
    string Reason)
{
    public string KindName => Resolved
        ? Kind == RuntimeOrderKind.Normal ? "NormalOrder" : "SpecialOrder"
        : "";
}

/// <summary>
/// Resolves the three exact order wrapper shapes declared by Assembly-CSharp.
/// </summary>
internal static class RuntimeOrderTypeResolver
{
    internal const string OrderBaseTypeName =
        "NightScene.GuestManagementUtility.GuestsManager+OrderBase";
    internal const string NormalOrderTypeName =
        "NightScene.GuestManagementUtility.GuestsManager+NormalOrder";
    internal const string SpecialOrderTypeName =
        "NightScene.GuestManagementUtility.GuestsManager+SpecialOrder";

    public static RuntimeOrderTypeResolution Resolve(object? order)
    {
        if (order == null)
        {
            return new RuntimeOrderTypeResolution(false, false, default, null, "order is null");
        }

        var typeName = order.GetType().FullName ?? "";
        if (string.Equals(typeName, NormalOrderTypeName, StringComparison.Ordinal))
        {
            return new RuntimeOrderTypeResolution(true, true, RuntimeOrderKind.Normal, order, "exact NormalOrder");
        }

        if (string.Equals(typeName, SpecialOrderTypeName, StringComparison.Ordinal))
        {
            return new RuntimeOrderTypeResolution(true, true, RuntimeOrderKind.Special, order, "exact SpecialOrder");
        }

        if (!string.Equals(typeName, OrderBaseTypeName, StringComparison.Ordinal))
        {
            return new RuntimeOrderTypeResolution(
                false,
                false,
                default,
                null,
                $"unsupported order type {typeName}");
        }

        var normalOrder = RuntimeReflectionUtility.TryCastRuntimeObject(order, NormalOrderTypeName);
        var specialOrder = RuntimeReflectionUtility.TryCastRuntimeObject(order, SpecialOrderTypeName);
        var hasNormalOrder = HasExactType(normalOrder, NormalOrderTypeName);
        var hasSpecialOrder = HasExactType(specialOrder, SpecialOrderTypeName);
        if (hasNormalOrder == hasSpecialOrder)
        {
            var castStatus = hasNormalOrder
                ? "both NormalOrder and SpecialOrder conversions succeeded"
                : "neither NormalOrder nor SpecialOrder conversion succeeded";
            return new RuntimeOrderTypeResolution(
                true,
                false,
                default,
                null,
                $"OrderBase runtime type is unresolved: {castStatus}");
        }

        return hasNormalOrder
            ? new RuntimeOrderTypeResolution(true, true, RuntimeOrderKind.Normal, normalOrder, "OrderBase -> NormalOrder")
            : new RuntimeOrderTypeResolution(true, true, RuntimeOrderKind.Special, specialOrder, "OrderBase -> SpecialOrder");
    }

    private static bool HasExactType(object? value, string expectedTypeName)
    {
        return value != null
            && string.Equals(value.GetType().FullName, expectedTypeName, StringComparison.Ordinal);
    }
}
