namespace MystiaStewardCompanion.Save;

internal enum RuntimeOrderTerminalDisposition
{
    Removed,
    Evaluated,
}

internal enum RuntimeOrderTerminalReceiptSource
{
    EvaluateOrder,
    EvaulateManualOrder,
    RemoveFromOrder,
    CleanOrderInfo,
    RepellInternal,
}

/// <summary>
/// Scalar-only state captured by one exact runtime order lifecycle Hook.
/// </summary>
internal readonly record struct RuntimeOrderTerminalHookState(
    long BusinessGeneration,
    RuntimeOrderKind OrderKind,
    nint OrderPointer,
    nint ControllerPointer,
    long LifecycleSequence,
    RuntimeOrderTerminalDisposition Disposition,
    RuntimeOrderTerminalReceiptSource Source);

/// <summary>
/// Immutable identity retained by a deferred order closeout transaction.
/// </summary>
internal readonly record struct RuntimeOrderBindingToken(
    long BusinessGeneration,
    RuntimeOrderKind OrderKind,
    nint OrderPointer,
    nint ControllerPointer,
    long LifecycleSequence);

/// <summary>
/// A wrapper-free terminal observation emitted after an exact lifecycle Hook succeeds.
/// </summary>
internal readonly record struct RuntimeOrderTerminalReceipt(
    long BusinessGeneration,
    RuntimeOrderKind OrderKind,
    nint OrderPointer,
    nint ControllerPointer,
    long LifecycleSequence,
    long Sequence,
    RuntimeOrderTerminalDisposition Disposition,
    RuntimeOrderTerminalReceiptSource Source);

/// <summary>
/// Retains a bounded immutable queue of exact terminal order observations for deferred closeout.
/// </summary>
/// <remarks>
/// The store owns independent monotonic process-local lifecycle and receipt sequences. A successful
/// binding starts a new lifecycle for one exact native identity, and consumers accept only receipts
/// from that lifecycle. This prevents both directions of an ABA match when IL2CPP reuses the same
/// native pointers. Receipt sequence is retained only for deterministic selection and diagnostics.
/// </remarks>
internal static class RuntimeOrderTerminalReceiptStore
{
    internal const int Capacity = 128;

    private static readonly object SyncRoot = new();
    private static readonly List<RuntimeOrderTerminalReceipt> Receipts = new();
    private static readonly Dictionary<RuntimeOrderIdentity, long> ActiveLifecycles = new();
    private static long _lastSequence;
    private static long _lastLifecycleSequence;

    /// <summary>
    /// Starts a new active lifecycle for one exact native order identity.
    /// </summary>
    /// <remarks>
    /// Rebinding the same tuple deliberately advances its lifecycle. Callers must retain the returned
    /// sequence in their binding token; the tuple alone is not a stable identity across native reuse.
    /// </remarks>
    public static long BeginLifecycle(
        long businessGeneration,
        RuntimeOrderKind orderKind,
        nint orderPointer,
        nint controllerPointer)
    {
        ValidateIdentity(businessGeneration, orderKind, orderPointer, controllerPointer);

        lock (SyncRoot)
        {
            var lifecycleSequence = checked(++_lastLifecycleSequence);
            ActiveLifecycles[new RuntimeOrderIdentity(
                businessGeneration,
                orderKind,
                orderPointer,
                controllerPointer)] = lifecycleSequence;
            return lifecycleSequence;
        }
    }

    /// <summary>
    /// Captures the active lifecycle sequence for an exact Hook prefix without retaining a wrapper.
    /// </summary>
    public static bool TryCaptureActiveLifecycle(
        long businessGeneration,
        RuntimeOrderKind orderKind,
        nint orderPointer,
        nint controllerPointer,
        out long lifecycleSequence)
    {
        lifecycleSequence = 0;
        if (!IsValidIdentity(businessGeneration, orderKind, orderPointer, controllerPointer))
        {
            return false;
        }

        lock (SyncRoot)
        {
            return ActiveLifecycles.TryGetValue(
                new RuntimeOrderIdentity(
                    businessGeneration,
                    orderKind,
                    orderPointer,
                    controllerPointer),
                out lifecycleSequence);
        }
    }

    /// <summary>
    /// Captures the unique active controller and lifecycle for an exact order pointer.
    /// </summary>
    /// <remarks>
    /// The native RemoveFromOrder boundary exposes only OrderBase. A missing or ambiguous active
    /// tuple is rejected; callers must not recover the controller from a business projection.
    /// </remarks>
    public static bool TryCaptureActiveLifecycleByOrder(
        long businessGeneration,
        RuntimeOrderKind orderKind,
        nint orderPointer,
        out nint controllerPointer,
        out long lifecycleSequence)
    {
        controllerPointer = 0;
        lifecycleSequence = 0;
        if (businessGeneration <= 0
            || (orderKind != RuntimeOrderKind.Normal && orderKind != RuntimeOrderKind.Special)
            || orderPointer == 0)
        {
            return false;
        }

        lock (SyncRoot)
        {
            var found = false;
            foreach (var pair in ActiveLifecycles)
            {
                if (pair.Key.BusinessGeneration != businessGeneration
                    || pair.Key.OrderKind != orderKind
                    || pair.Key.OrderPointer != orderPointer)
                {
                    continue;
                }

                if (found)
                {
                    controllerPointer = 0;
                    lifecycleSequence = 0;
                    return false;
                }

                found = true;
                controllerPointer = pair.Key.ControllerPointer;
                lifecycleSequence = pair.Value;
            }

            return found;
        }
    }

    /// <summary>
    /// Reports whether a deferred binding still owns the active lifecycle for its exact native tuple.
    /// </summary>
    public static bool MatchesActiveLifecycle(RuntimeOrderBindingToken token)
    {
        if (!IsValidBindingToken(token)) return false;

        lock (SyncRoot)
        {
            return ActiveLifecycles.TryGetValue(ToIdentity(token), out var activeSequence)
                && activeSequence == token.LifecycleSequence;
        }
    }

    /// <summary>
    /// Invalidates one exact active lifecycle after its immutable business identity becomes corrupt.
    /// </summary>
    /// <remarks>
    /// This does not publish a terminal receipt: identity drift is not evidence that the game
    /// evaluated or removed the order. A stale token cannot invalidate a newer lifecycle that reused
    /// the same native tuple.
    /// </remarks>
    public static bool InvalidateActiveLifecycle(RuntimeOrderBindingToken token)
    {
        if (!IsValidBindingToken(token)) return false;

        lock (SyncRoot)
        {
            var identity = ToIdentity(token);
            if (!ActiveLifecycles.TryGetValue(identity, out var activeSequence)
                || activeSequence != token.LifecycleSequence)
            {
                return false;
            }

            return ActiveLifecycles.Remove(identity);
        }
    }

    /// <summary>
    /// Requires an API request to name the same positive lifecycle published by the fresh capture.
    /// </summary>
    public static bool MatchesRequestedLifecycle(
        long requestedLifecycleSequence,
        long capturedLifecycleSequence)
    {
        return requestedLifecycleSequence > 0
            && capturedLifecycleSequence > 0
            && requestedLifecycleSequence == capturedLifecycleSequence;
    }

    public static RuntimeOrderTerminalReceipt Publish(RuntimeOrderTerminalHookState state)
    {
        ValidateHookState(state);

        lock (SyncRoot)
        {
            if (state.LifecycleSequence > _lastLifecycleSequence)
            {
                throw new InvalidOperationException(
                    "Terminal receipt references an order lifecycle that was never allocated.");
            }

            var receipt = new RuntimeOrderTerminalReceipt(
                state.BusinessGeneration,
                state.OrderKind,
                state.OrderPointer,
                state.ControllerPointer,
                state.LifecycleSequence,
                checked(++_lastSequence),
                state.Disposition,
                state.Source);
            Receipts.Add(receipt);
            if (Receipts.Count > Capacity)
            {
                Receipts.RemoveRange(0, Receipts.Count - Capacity);
            }

            var identity = ToIdentity(state);
            if (ActiveLifecycles.TryGetValue(identity, out var activeSequence)
                && activeSequence == state.LifecycleSequence)
            {
                ActiveLifecycles.Remove(identity);
            }

            return receipt;
        }
    }

    /// <summary>
    /// Finds the strongest terminal fact for one exact lifecycle identity.
    /// </summary>
    /// <remarks>
    /// A successful evaluation is stronger than a generic removal even when the removal was observed
    /// later. Reads are immutable and idempotent so an outer evaluation postfix can supersede a removal
    /// published by a nested callback from the same native evaluation.
    /// </remarks>
    public static bool TryFind(
        RuntimeOrderBindingToken token,
        out RuntimeOrderTerminalReceipt receipt)
    {
        receipt = default;
        if (!IsValidBindingToken(token)) return false;

        lock (SyncRoot)
        {
            var selectedIndex = -1;
            for (var index = 0; index < Receipts.Count; index++)
            {
                var candidate = Receipts[index];
                if (!Matches(candidate, token)) continue;
                if (selectedIndex < 0 || IsStronger(candidate, Receipts[selectedIndex]))
                {
                    selectedIndex = index;
                }
            }

            if (selectedIndex < 0) return false;

            receipt = Receipts[selectedIndex];
            return true;
        }
    }

    /// <summary>
    /// Drops retained receipts and active identities at a business lifecycle boundary without rewinding
    /// either process-local sequence watermark.
    /// </summary>
    public static void Clear()
    {
        lock (SyncRoot)
        {
            Receipts.Clear();
            ActiveLifecycles.Clear();
        }
    }

    private static bool IsStronger(
        RuntimeOrderTerminalReceipt candidate,
        RuntimeOrderTerminalReceipt selected)
    {
        if (candidate.Disposition != selected.Disposition)
        {
            return candidate.Disposition == RuntimeOrderTerminalDisposition.Evaluated;
        }

        return candidate.Sequence > selected.Sequence;
    }

    private static bool Matches(
        RuntimeOrderTerminalReceipt candidate,
        RuntimeOrderBindingToken token)
    {
        return candidate.BusinessGeneration == token.BusinessGeneration
            && candidate.OrderKind == token.OrderKind
            && candidate.OrderPointer == token.OrderPointer
            && candidate.ControllerPointer == token.ControllerPointer
            && candidate.LifecycleSequence == token.LifecycleSequence;
    }

    private static void ValidateHookState(RuntimeOrderTerminalHookState state)
    {
        ValidateIdentity(
            state.BusinessGeneration,
            state.OrderKind,
            state.OrderPointer,
            state.ControllerPointer);
        if (state.LifecycleSequence <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(state), "Lifecycle sequence must be positive.");
        }

        if (state.Disposition != RuntimeOrderTerminalDisposition.Removed
            && state.Disposition != RuntimeOrderTerminalDisposition.Evaluated)
        {
            throw new ArgumentOutOfRangeException(nameof(state), "Unknown terminal disposition.");
        }

        if (state.Source != RuntimeOrderTerminalReceiptSource.EvaluateOrder
            && state.Source != RuntimeOrderTerminalReceiptSource.EvaulateManualOrder
            && state.Source != RuntimeOrderTerminalReceiptSource.RemoveFromOrder
            && state.Source != RuntimeOrderTerminalReceiptSource.CleanOrderInfo
            && state.Source != RuntimeOrderTerminalReceiptSource.RepellInternal)
        {
            throw new ArgumentOutOfRangeException(nameof(state), "Unknown terminal receipt source.");
        }

        var sourceIsEvaluation = state.Source == RuntimeOrderTerminalReceiptSource.EvaluateOrder
            || state.Source == RuntimeOrderTerminalReceiptSource.EvaulateManualOrder;
        if (sourceIsEvaluation != (state.Disposition == RuntimeOrderTerminalDisposition.Evaluated))
        {
            throw new ArgumentException(
                "Terminal disposition does not match the exact lifecycle Hook source.",
                nameof(state));
        }
    }

    private static bool IsValidBindingToken(RuntimeOrderBindingToken token)
    {
        return IsValidIdentity(
                token.BusinessGeneration,
                token.OrderKind,
                token.OrderPointer,
                token.ControllerPointer)
            && token.LifecycleSequence > 0;
    }

    private static RuntimeOrderIdentity ToIdentity(RuntimeOrderBindingToken token)
    {
        return new RuntimeOrderIdentity(
            token.BusinessGeneration,
            token.OrderKind,
            token.OrderPointer,
            token.ControllerPointer);
    }

    private static RuntimeOrderIdentity ToIdentity(RuntimeOrderTerminalHookState state)
    {
        return new RuntimeOrderIdentity(
            state.BusinessGeneration,
            state.OrderKind,
            state.OrderPointer,
            state.ControllerPointer);
    }

    private static void ValidateIdentity(
        long businessGeneration,
        RuntimeOrderKind orderKind,
        nint orderPointer,
        nint controllerPointer)
    {
        if (businessGeneration <= 0) throw new ArgumentOutOfRangeException(nameof(businessGeneration));
        if (orderKind != RuntimeOrderKind.Normal && orderKind != RuntimeOrderKind.Special)
        {
            throw new ArgumentOutOfRangeException(nameof(orderKind));
        }

        if (orderPointer == 0) throw new ArgumentOutOfRangeException(nameof(orderPointer));
        if (controllerPointer == 0) throw new ArgumentOutOfRangeException(nameof(controllerPointer));
    }

    private static bool IsValidIdentity(
        long businessGeneration,
        RuntimeOrderKind orderKind,
        nint orderPointer,
        nint controllerPointer)
    {
        return businessGeneration > 0
            && (orderKind == RuntimeOrderKind.Normal || orderKind == RuntimeOrderKind.Special)
            && orderPointer != 0
            && controllerPointer != 0;
    }

    private readonly record struct RuntimeOrderIdentity(
        long BusinessGeneration,
        RuntimeOrderKind OrderKind,
        nint OrderPointer,
        nint ControllerPointer);
}
