namespace MystiaStewardCompanion.Save;

/// <summary>
/// Resolves one managed UI target to the exact active runtime order/controller binding.
/// The returned record contains no fallback identity and is never retained across target generations.
/// </summary>
internal static class RuntimeUiTargetOrderResolver
{
    internal static bool TryResolveCurrentCapture(
        RuntimeUiTargetSnapshot target,
        TimeSpan maxAge,
        out RuntimeUiTargetOrderBinding? binding,
        out string failure)
    {
        ArgumentNullException.ThrowIfNull(target);
        binding = null;

        if (target.OrderLifecycleSequence <= 0)
        {
            failure = "target order lifecycle sequence is not positive";
            return false;
        }
        if (target.DeskCode < 0)
        {
            failure = "target desk code is negative";
            return false;
        }
        if (!RuntimeOrderTraceIdService.TryNormalizeTargetTraceId(
                target.Kind,
                target.OrderTraceId,
                enabled: true,
                out var normalizedTraceId,
                out failure)
            || !string.Equals(normalizedTraceId, target.OrderTraceId, StringComparison.Ordinal))
        {
            if (string.IsNullOrEmpty(failure)) failure = "target order trace changed during exact normalization";
            return false;
        }

        return target.Kind == RuntimeUiTargetKind.Rare
            ? TryResolveRare(target, maxAge, out binding, out failure)
            : TryResolveNormal(target, maxAge, out binding, out failure);
    }

    private static bool TryResolveRare(
        RuntimeUiTargetSnapshot target,
        TimeSpan maxAge,
        out RuntimeUiTargetOrderBinding? binding,
        out string failure)
    {
        binding = null;
        if (target.OrderKey.Length != 0)
        {
            failure = "rare target unexpectedly carries a normal-order key";
            return false;
        }

        var matches = new List<RuntimeUiTargetOrderBinding>();
        foreach (var capture in SpecialOrderRuntimeCapture.Snapshot(maxAge))
        {
            if (capture.OrderLifecycleSequence != target.OrderLifecycleSequence
                || capture.DeskCode != target.DeskCode
                || !string.Equals(
                    RuntimeOrderTraceIdService.GetRareTraceId(capture),
                    target.OrderTraceId,
                    StringComparison.Ordinal)
                || !TryBuildBinding(
                    RuntimeUiTargetKind.Rare,
                    capture.RuntimeKey,
                    capture.OrderObject,
                    capture.ControllerObject,
                    capture.OrderLifecycleSequence,
                    capture.DeskCode,
                    out var candidate))
            {
                continue;
            }

            matches.Add(candidate);
        }

        return SelectUnique(target, matches, out binding, out failure);
    }

    private static bool TryResolveNormal(
        RuntimeUiTargetSnapshot target,
        TimeSpan maxAge,
        out RuntimeUiTargetOrderBinding? binding,
        out string failure)
    {
        binding = null;
        if (target.OrderKey.Length == 0)
        {
            failure = "normal target has no exact raw order key";
            return false;
        }

        var matches = new List<RuntimeUiTargetOrderBinding>();
        foreach (var capture in NormalOrderRuntimeCapture.Snapshot(maxAge))
        {
            if (capture.OrderLifecycleSequence != target.OrderLifecycleSequence
                || capture.DeskCode != target.DeskCode
                || !string.Equals(capture.RuntimeKey, target.OrderKey, StringComparison.Ordinal)
                || !string.Equals(
                    RuntimeOrderTraceIdService.GetNormalTraceId(capture),
                    target.OrderTraceId,
                    StringComparison.Ordinal)
                || !TryBuildBinding(
                    RuntimeUiTargetKind.Normal,
                    capture.RuntimeKey,
                    capture.OrderObject,
                    capture.ControllerObject,
                    capture.OrderLifecycleSequence,
                    capture.DeskCode,
                    out var candidate))
            {
                continue;
            }

            matches.Add(candidate);
        }

        return SelectUnique(target, matches, out binding, out failure);
    }

    private static bool TryBuildBinding(
        RuntimeUiTargetKind kind,
        string runtimeKey,
        object? order,
        object? controller,
        long lifecycleSequence,
        int deskCode,
        out RuntimeUiTargetOrderBinding binding)
    {
        binding = default;
        if (order == null
            || controller == null
            || !RuntimeReflectionUtility.TryReadNativeObjectPointer(order, out var orderPointer)
            || !RuntimeReflectionUtility.TryReadNativeObjectPointer(controller, out var controllerPointer)
            || !string.Equals(runtimeKey, $"ptr:{orderPointer:x}", StringComparison.Ordinal))
        {
            return false;
        }

        binding = new RuntimeUiTargetOrderBinding(
            kind,
            order,
            controller,
            orderPointer,
            controllerPointer,
            lifecycleSequence,
            deskCode);
        return true;
    }

    private static bool SelectUnique(
        RuntimeUiTargetSnapshot target,
        IReadOnlyList<RuntimeUiTargetOrderBinding> matches,
        out RuntimeUiTargetOrderBinding? binding,
        out string failure)
    {
        if (matches.Count != 1)
        {
            binding = null;
            failure = $"target {target.Kind} order identity matched {matches.Count} active captures";
            return false;
        }

        binding = matches[0];
        failure = "";
        return true;
    }
}

internal readonly record struct RuntimeUiTargetOrderBinding(
    RuntimeUiTargetKind Kind,
    object OrderObject,
    object ControllerObject,
    nint OrderPointer,
    nint ControllerPointer,
    long OrderLifecycleSequence,
    int DeskCode);
