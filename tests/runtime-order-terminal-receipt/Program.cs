using MystiaStewardCompanion.Save;

try
{
    VerifyExactIdentity();
    VerifyEvaluatedTakesPriorityOverRemoved();
    VerifySameTupleLifecycleAbaIsIsolated();
    VerifyInvalidationIsExactAndAbaSafe();
    VerifyRequestLifecycleMatchingIsExact();
    VerifyOldPostfixDoesNotRemoveNewActiveLifecycle();
    VerifyCapacityIsBounded();
    VerifyInvalidIdentityAndSourceFailClosed();
    VerifyClearDoesNotRewindEitherSequence();
    VerifyStateContainsNoRuntimeWrapper();
    Console.WriteLine(
        "PASS: runtime order terminal receipts are lifecycle-exact, ABA-safe, monotonic, bounded, and wrapper-free.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL: {ex}");
    return 1;
}

static void VerifyExactIdentity()
{
    RuntimeOrderTerminalReceiptStore.Clear();
    var staleLifecycle = RuntimeOrderTerminalReceiptStore.BeginLifecycle(
        41,
        RuntimeOrderKind.Special,
        (nint)0x1000,
        (nint)0x2000);
    RuntimeOrderTerminalReceiptStore.Publish(EvaluatedState(
        41,
        RuntimeOrderKind.Special,
        (nint)0x1000,
        (nint)0x2000,
        staleLifecycle));

    var currentLifecycle = RuntimeOrderTerminalReceiptStore.BeginLifecycle(
        41,
        RuntimeOrderKind.Special,
        (nint)0x1000,
        (nint)0x2000);
    AssertTrue(currentLifecycle > staleLifecycle, "A repeated exact tuple did not advance its lifecycle.");
    AssertActive(41, RuntimeOrderKind.Special, 0x1000, 0x2000, currentLifecycle);
    var current = RuntimeOrderTerminalReceiptStore.Publish(RemovedState(
        41,
        RuntimeOrderKind.Special,
        (nint)0x1000,
        (nint)0x2000,
        currentLifecycle,
        RuntimeOrderTerminalReceiptSource.RemoveFromOrder));

    AssertMissing(Token(42, RuntimeOrderKind.Special, 0x1000, 0x2000, currentLifecycle),
        "A different business generation matched.");
    AssertMissing(Token(41, RuntimeOrderKind.Normal, 0x1000, 0x2000, currentLifecycle),
        "A different concrete order kind matched.");
    AssertMissing(Token(41, RuntimeOrderKind.Special, 0x1001, 0x2000, currentLifecycle),
        "A different order pointer matched.");
    AssertMissing(Token(41, RuntimeOrderKind.Special, 0x1000, 0x2001, currentLifecycle),
        "A different controller pointer matched.");

    var exactToken = Token(
        41,
        RuntimeOrderKind.Special,
        0x1000,
        0x2000,
        currentLifecycle);
    AssertTrue(
        RuntimeOrderTerminalReceiptStore.TryFind(exactToken, out var found),
        "The exact current receipt was not found.");
    AssertEqual(current, found, "The exact current receipt changed during lookup.");
    AssertTrue(
        RuntimeOrderTerminalReceiptStore.TryFind(exactToken, out var repeated),
        "An immutable receipt lookup was not idempotent.");
    AssertEqual(current, repeated, "A repeated receipt lookup returned a different observation.");
}

static void VerifyEvaluatedTakesPriorityOverRemoved()
{
    RuntimeOrderTerminalReceiptStore.Clear();
    var lifecycle = RuntimeOrderTerminalReceiptStore.BeginLifecycle(
        7,
        RuntimeOrderKind.Normal,
        (nint)0x3000,
        (nint)0x4000);
    RuntimeOrderTerminalReceiptStore.Publish(RemovedState(
        7,
        RuntimeOrderKind.Normal,
        (nint)0x3000,
        (nint)0x4000,
        lifecycle,
        RuntimeOrderTerminalReceiptSource.CleanOrderInfo));
    var evaluated = RuntimeOrderTerminalReceiptStore.Publish(EvaluatedState(
        7,
        RuntimeOrderKind.Normal,
        (nint)0x3000,
        (nint)0x4000,
        lifecycle,
        RuntimeOrderTerminalReceiptSource.EvaulateManualOrder));
    RuntimeOrderTerminalReceiptStore.Publish(RemovedState(
        7,
        RuntimeOrderKind.Normal,
        (nint)0x3000,
        (nint)0x4000,
        lifecycle,
        RuntimeOrderTerminalReceiptSource.RepellInternal));

    var token = Token(7, RuntimeOrderKind.Normal, 0x3000, 0x4000, lifecycle);
    AssertTrue(
        RuntimeOrderTerminalReceiptStore.TryFind(token, out var found),
        "The terminal receipt set was not found.");
    AssertEqual(RuntimeOrderTerminalDisposition.Evaluated, found.Disposition,
        "A later generic removal downgraded an exact evaluation.");
    AssertEqual(RuntimeOrderTerminalReceiptSource.EvaulateManualOrder, found.Source,
        "The exact evaluation Hook source was lost.");
    AssertEqual(evaluated.Sequence, found.Sequence,
        "The selected evaluation receipt was not the expected observation.");
}

static void VerifySameTupleLifecycleAbaIsIsolated()
{
    RuntimeOrderTerminalReceiptStore.Clear();
    var firstLifecycle = RuntimeOrderTerminalReceiptStore.BeginLifecycle(
        13,
        RuntimeOrderKind.Special,
        (nint)0x4100,
        (nint)0x4200);
    var firstToken = Token(
        13,
        RuntimeOrderKind.Special,
        0x4100,
        0x4200,
        firstLifecycle);
    var firstReceipt = RuntimeOrderTerminalReceiptStore.Publish(RemovedState(
        13,
        RuntimeOrderKind.Special,
        (nint)0x4100,
        (nint)0x4200,
        firstLifecycle,
        RuntimeOrderTerminalReceiptSource.RemoveFromOrder));

    var secondLifecycle = RuntimeOrderTerminalReceiptStore.BeginLifecycle(
        13,
        RuntimeOrderKind.Special,
        (nint)0x4100,
        (nint)0x4200);
    var secondToken = Token(
        13,
        RuntimeOrderKind.Special,
        0x4100,
        0x4200,
        secondLifecycle);

    AssertTrue(secondLifecycle > firstLifecycle,
        "The same native tuple reused its previous lifecycle sequence.");
    AssertMissing(secondToken,
        "An old receipt matched a new lifecycle token for the same native tuple.");

    var secondReceipt = RuntimeOrderTerminalReceiptStore.Publish(EvaluatedState(
        13,
        RuntimeOrderKind.Special,
        (nint)0x4100,
        (nint)0x4200,
        secondLifecycle));

    AssertTrue(RuntimeOrderTerminalReceiptStore.TryFind(firstToken, out var foundFirst),
        "The first lifecycle receipt disappeared after the native tuple was reused.");
    AssertEqual(firstReceipt, foundFirst,
        "A new lifecycle receipt matched the old lifecycle token.");
    AssertTrue(RuntimeOrderTerminalReceiptStore.TryFind(secondToken, out var foundSecond),
        "The reused tuple's new lifecycle receipt was not found.");
    AssertEqual(secondReceipt, foundSecond,
        "The new lifecycle token selected an old receipt.");
}

static void VerifyOldPostfixDoesNotRemoveNewActiveLifecycle()
{
    RuntimeOrderTerminalReceiptStore.Clear();
    var oldLifecycle = RuntimeOrderTerminalReceiptStore.BeginLifecycle(
        17,
        RuntimeOrderKind.Normal,
        (nint)0x4300,
        (nint)0x4400);
    AssertActive(17, RuntimeOrderKind.Normal, 0x4300, 0x4400, oldLifecycle);

    var newLifecycle = RuntimeOrderTerminalReceiptStore.BeginLifecycle(
        17,
        RuntimeOrderKind.Normal,
        (nint)0x4300,
        (nint)0x4400);
    var newToken = Token(
        17,
        RuntimeOrderKind.Normal,
        0x4300,
        0x4400,
        newLifecycle);
    AssertTrue(!RuntimeOrderTerminalReceiptStore.MatchesActiveLifecycle(
            Token(17, RuntimeOrderKind.Normal, 0x4300, 0x4400, oldLifecycle)),
        "The superseded lifecycle remained active.");
    AssertTrue(RuntimeOrderTerminalReceiptStore.MatchesActiveLifecycle(newToken),
        "The advanced lifecycle was not active.");

    RuntimeOrderTerminalReceiptStore.Publish(RemovedState(
        17,
        RuntimeOrderKind.Normal,
        (nint)0x4300,
        (nint)0x4400,
        oldLifecycle,
        RuntimeOrderTerminalReceiptSource.CleanOrderInfo));

    AssertTrue(RuntimeOrderTerminalReceiptStore.MatchesActiveLifecycle(newToken),
        "An old postfix removed the new active lifecycle for the same native tuple.");
    AssertActive(17, RuntimeOrderKind.Normal, 0x4300, 0x4400, newLifecycle);

    RuntimeOrderTerminalReceiptStore.Publish(EvaluatedState(
        17,
        RuntimeOrderKind.Normal,
        (nint)0x4300,
        (nint)0x4400,
        newLifecycle));
    AssertTrue(!RuntimeOrderTerminalReceiptStore.MatchesActiveLifecycle(newToken),
        "The terminal postfix did not retire its own active lifecycle.");
    AssertInactive(17, RuntimeOrderKind.Normal, 0x4300, 0x4400);
}

static void VerifyInvalidationIsExactAndAbaSafe()
{
    RuntimeOrderTerminalReceiptStore.Clear();
    var oldLifecycle = RuntimeOrderTerminalReceiptStore.BeginLifecycle(
        19,
        RuntimeOrderKind.Special,
        (nint)0x4350,
        (nint)0x4450);
    var oldToken = Token(
        19,
        RuntimeOrderKind.Special,
        0x4350,
        0x4450,
        oldLifecycle);
    var newLifecycle = RuntimeOrderTerminalReceiptStore.BeginLifecycle(
        19,
        RuntimeOrderKind.Special,
        (nint)0x4350,
        (nint)0x4450);
    var newToken = oldToken with { LifecycleSequence = newLifecycle };

    AssertTrue(!RuntimeOrderTerminalReceiptStore.InvalidateActiveLifecycle(oldToken),
        "A stale invalidation token matched the reused tuple's new lifecycle.");
    AssertTrue(RuntimeOrderTerminalReceiptStore.MatchesActiveLifecycle(newToken),
        "A stale invalidation token retired the reused tuple's new lifecycle.");
    AssertTrue(RuntimeOrderTerminalReceiptStore.InvalidateActiveLifecycle(newToken),
        "The exact active lifecycle could not be invalidated.");
    AssertTrue(!RuntimeOrderTerminalReceiptStore.MatchesActiveLifecycle(newToken),
        "An explicitly invalidated lifecycle remained active.");
    AssertMissing(newToken,
        "Invalidating a corrupt lifecycle fabricated a terminal receipt.");
}

static void VerifyRequestLifecycleMatchingIsExact()
{
    RuntimeOrderTerminalReceiptStore.Clear();
    var oldLifecycle = RuntimeOrderTerminalReceiptStore.BeginLifecycle(
        23,
        RuntimeOrderKind.Special,
        (nint)0x4500,
        (nint)0x4600);
    var newLifecycle = RuntimeOrderTerminalReceiptStore.BeginLifecycle(
        23,
        RuntimeOrderKind.Special,
        (nint)0x4500,
        (nint)0x4600);

    AssertTrue(newLifecycle > oldLifecycle,
        "The request ABA fixture did not advance the reused tuple lifecycle.");
    AssertTrue(!RuntimeOrderTerminalReceiptStore.MatchesRequestedLifecycle(
            oldLifecycle,
            newLifecycle),
        "A delayed request matched a reused native tuple's new lifecycle.");
    AssertTrue(RuntimeOrderTerminalReceiptStore.MatchesRequestedLifecycle(
            newLifecycle,
            newLifecycle),
        "The exact positive request lifecycle did not match its fresh capture.");
    AssertTrue(!RuntimeOrderTerminalReceiptStore.MatchesRequestedLifecycle(0, newLifecycle),
        "A missing request lifecycle was accepted.");
    AssertTrue(!RuntimeOrderTerminalReceiptStore.MatchesRequestedLifecycle(newLifecycle, 0),
        "A missing capture lifecycle was accepted.");
}

static void VerifyCapacityIsBounded()
{
    RuntimeOrderTerminalReceiptStore.Clear();
    var firstLifecycle = 0L;
    var latestLifecycle = 0L;
    for (var index = 0; index <= RuntimeOrderTerminalReceiptStore.Capacity; index++)
    {
        var lifecycle = RuntimeOrderTerminalReceiptStore.BeginLifecycle(
            1,
            RuntimeOrderKind.Normal,
            (nint)(0x5000 + index),
            (nint)(0x7000 + index));
        if (index == 0) firstLifecycle = lifecycle;
        latestLifecycle = lifecycle;
        RuntimeOrderTerminalReceiptStore.Publish(RemovedState(
            1,
            RuntimeOrderKind.Normal,
            (nint)(0x5000 + index),
            (nint)(0x7000 + index),
            lifecycle,
            RuntimeOrderTerminalReceiptSource.RemoveFromOrder));
    }

    AssertMissing(Token(1, RuntimeOrderKind.Normal, 0x5000, 0x7000, firstLifecycle),
        "The oldest receipt was not evicted at capacity.");
    AssertTrue(
        RuntimeOrderTerminalReceiptStore.TryFind(
            Token(
                1,
                RuntimeOrderKind.Normal,
                0x5000 + RuntimeOrderTerminalReceiptStore.Capacity,
                0x7000 + RuntimeOrderTerminalReceiptStore.Capacity,
                latestLifecycle),
            out _),
        "The newest receipt was not retained.");
}

static void VerifyInvalidIdentityAndSourceFailClosed()
{
    RuntimeOrderTerminalReceiptStore.Clear();
    AssertThrows<ArgumentOutOfRangeException>(() => RuntimeOrderTerminalReceiptStore.BeginLifecycle(
            0,
            RuntimeOrderKind.Normal,
            (nint)0x8000,
            (nint)0x9000),
        "A lifecycle was started with a non-positive generation.");
    AssertThrows<ArgumentOutOfRangeException>(() => RuntimeOrderTerminalReceiptStore.BeginLifecycle(
            1,
            RuntimeOrderKind.Normal,
            0,
            (nint)0x9000),
        "A lifecycle was started with a zero order pointer.");
    AssertCaptureMissing(0, RuntimeOrderKind.Normal, 0x8000, 0x9000,
        "An invalid generation exposed an active lifecycle.");
    AssertCaptureMissing(1, RuntimeOrderKind.Normal, 0x8000, 0x9000,
        "A tuple that was never bound exposed an active lifecycle.");

    var validLifecycle = RuntimeOrderTerminalReceiptStore.BeginLifecycle(
        1,
        RuntimeOrderKind.Normal,
        (nint)0x8000,
        (nint)0x9000);
    AssertThrows<ArgumentOutOfRangeException>(() => RuntimeOrderTerminalReceiptStore.Publish(
        RemovedState(0, RuntimeOrderKind.Normal, (nint)0x8000, (nint)0x9000,
            validLifecycle,
            RuntimeOrderTerminalReceiptSource.RemoveFromOrder)),
        "A non-positive generation was published.");
    AssertThrows<ArgumentOutOfRangeException>(() => RuntimeOrderTerminalReceiptStore.Publish(
        RemovedState(1, RuntimeOrderKind.Normal, 0, (nint)0x9000,
            validLifecycle,
            RuntimeOrderTerminalReceiptSource.RemoveFromOrder)),
        "A zero order pointer was published.");
    AssertThrows<ArgumentOutOfRangeException>(() => RuntimeOrderTerminalReceiptStore.Publish(
        RemovedState(1, RuntimeOrderKind.Normal, (nint)0x8000, 0,
            validLifecycle,
            RuntimeOrderTerminalReceiptSource.RemoveFromOrder)),
        "A zero controller pointer was published.");
    AssertThrows<ArgumentOutOfRangeException>(() => RuntimeOrderTerminalReceiptStore.Publish(
        RemovedState(1, (RuntimeOrderKind)99, (nint)0x8000, (nint)0x9000,
            validLifecycle,
            RuntimeOrderTerminalReceiptSource.RemoveFromOrder)),
        "An unknown concrete order kind was published.");
    AssertThrows<ArgumentOutOfRangeException>(() => RuntimeOrderTerminalReceiptStore.Publish(
        RemovedState(1, RuntimeOrderKind.Normal, (nint)0x8000, (nint)0x9000,
            validLifecycle,
            (RuntimeOrderTerminalReceiptSource)99)),
        "An unknown terminal Hook source was published.");
    AssertThrows<ArgumentOutOfRangeException>(() => RuntimeOrderTerminalReceiptStore.Publish(
        RemovedState(1, RuntimeOrderKind.Normal, (nint)0x8000, (nint)0x9000,
            0,
            RuntimeOrderTerminalReceiptSource.RemoveFromOrder)),
        "A non-positive lifecycle sequence was published.");
    AssertThrows<InvalidOperationException>(() => RuntimeOrderTerminalReceiptStore.Publish(
        RemovedState(1, RuntimeOrderKind.Normal, (nint)0x8000, (nint)0x9000,
            validLifecycle + 1,
            RuntimeOrderTerminalReceiptSource.RemoveFromOrder)),
        "A terminal receipt referenced a lifecycle sequence that was never allocated.");
    AssertThrows<ArgumentOutOfRangeException>(() => RuntimeOrderTerminalReceiptStore.Publish(
        new RuntimeOrderTerminalHookState(
            1,
            RuntimeOrderKind.Normal,
            (nint)0x8000,
            (nint)0x9000,
            validLifecycle,
            (RuntimeOrderTerminalDisposition)99,
            RuntimeOrderTerminalReceiptSource.RemoveFromOrder)),
        "An unknown terminal disposition was published.");
    AssertThrows<ArgumentException>(() => RuntimeOrderTerminalReceiptStore.Publish(
        RemovedState(1, RuntimeOrderKind.Normal, (nint)0x8000, (nint)0x9000,
            validLifecycle,
            RuntimeOrderTerminalReceiptSource.EvaluateOrder)),
        "A removed disposition was accepted from an evaluation Hook.");
    AssertThrows<ArgumentException>(() => RuntimeOrderTerminalReceiptStore.Publish(
        EvaluatedState(1, RuntimeOrderKind.Normal, (nint)0x8000, (nint)0x9000,
            validLifecycle,
            RuntimeOrderTerminalReceiptSource.CleanOrderInfo)),
        "An evaluated disposition was accepted from a removal Hook.");

    AssertMissing(Token(0, RuntimeOrderKind.Normal, 0x8000, 0x9000, validLifecycle),
        "An invalid query generation matched.");
    AssertMissing(Token(1, RuntimeOrderKind.Normal, 0, 0x9000, validLifecycle),
        "An invalid query order pointer matched.");
    AssertMissing(Token(1, RuntimeOrderKind.Normal, 0x8000, 0x9000, 0),
        "An invalid query lifecycle sequence matched.");
    AssertTrue(!RuntimeOrderTerminalReceiptStore.MatchesActiveLifecycle(
            Token(1, RuntimeOrderKind.Normal, 0x8000, 0x9000, 0)),
        "An invalid lifecycle token matched the active lifecycle.");
}

static void VerifyClearDoesNotRewindEitherSequence()
{
    RuntimeOrderTerminalReceiptStore.Clear();
    var firstLifecycle = RuntimeOrderTerminalReceiptStore.BeginLifecycle(
        1,
        RuntimeOrderKind.Special,
        (nint)0xA000,
        (nint)0xB000);
    var first = RuntimeOrderTerminalReceiptStore.Publish(RemovedState(
        1,
        RuntimeOrderKind.Special,
        (nint)0xA000,
        (nint)0xB000,
        firstLifecycle,
        RuntimeOrderTerminalReceiptSource.RemoveFromOrder));
    var pendingLifecycle = RuntimeOrderTerminalReceiptStore.BeginLifecycle(
        1,
        RuntimeOrderKind.Special,
        (nint)0xA100,
        (nint)0xB100);
    RuntimeOrderTerminalReceiptStore.Clear();
    AssertMissing(Token(1, RuntimeOrderKind.Special, 0xA000, 0xB000, firstLifecycle),
        "Clear retained a terminal receipt.");
    AssertCaptureMissing(1, RuntimeOrderKind.Special, 0xA100, 0xB100,
        "Clear retained an active lifecycle.");
    AssertTrue(!RuntimeOrderTerminalReceiptStore.MatchesActiveLifecycle(
            Token(1, RuntimeOrderKind.Special, 0xA100, 0xB100, pendingLifecycle)),
        "Clear retained an active lifecycle token.");

    var nextLifecycle = RuntimeOrderTerminalReceiptStore.BeginLifecycle(
        1,
        RuntimeOrderKind.Special,
        (nint)0xA100,
        (nint)0xB100);
    AssertTrue(nextLifecycle > pendingLifecycle,
        "Clear rewound the process-local lifecycle sequence watermark.");
    var nextReceipt = RuntimeOrderTerminalReceiptStore.Publish(RemovedState(
        1,
        RuntimeOrderKind.Special,
        (nint)0xA100,
        (nint)0xB100,
        nextLifecycle,
        RuntimeOrderTerminalReceiptSource.RemoveFromOrder));
    AssertTrue(nextReceipt.Sequence > first.Sequence,
        "Clear rewound the process-local receipt sequence watermark.");
}

static void VerifyStateContainsNoRuntimeWrapper()
{
    var allowedTypes = new HashSet<Type>
    {
        typeof(long),
        typeof(nint),
        typeof(RuntimeOrderKind),
        typeof(RuntimeOrderTerminalDisposition),
        typeof(RuntimeOrderTerminalReceiptSource),
    };
    foreach (var stateType in new[]
             {
                 typeof(RuntimeOrderTerminalHookState),
                 typeof(RuntimeOrderBindingToken),
                 typeof(RuntimeOrderTerminalReceipt),
             })
    {
        foreach (var property in stateType.GetProperties())
        {
            AssertTrue(
                allowedTypes.Contains(property.PropertyType),
                $"{stateType.Name}.{property.Name} retains non-scalar type {property.PropertyType.FullName}.");
        }
    }
}

static RuntimeOrderTerminalHookState EvaluatedState(
    long generation,
    RuntimeOrderKind kind,
    nint orderPointer,
    nint controllerPointer,
    long lifecycleSequence,
    RuntimeOrderTerminalReceiptSource source = RuntimeOrderTerminalReceiptSource.EvaluateOrder)
{
    return new RuntimeOrderTerminalHookState(
        generation,
        kind,
        orderPointer,
        controllerPointer,
        lifecycleSequence,
        RuntimeOrderTerminalDisposition.Evaluated,
        source);
}

static RuntimeOrderTerminalHookState RemovedState(
    long generation,
    RuntimeOrderKind kind,
    nint orderPointer,
    nint controllerPointer,
    long lifecycleSequence,
    RuntimeOrderTerminalReceiptSource source)
{
    return new RuntimeOrderTerminalHookState(
        generation,
        kind,
        orderPointer,
        controllerPointer,
        lifecycleSequence,
        RuntimeOrderTerminalDisposition.Removed,
        source);
}

static RuntimeOrderBindingToken Token(
    long generation,
    RuntimeOrderKind kind,
    long orderPointer,
    long controllerPointer,
    long lifecycleSequence)
{
    return new RuntimeOrderBindingToken(
        generation,
        kind,
        (nint)orderPointer,
        (nint)controllerPointer,
        lifecycleSequence);
}

static void AssertActive(
    long generation,
    RuntimeOrderKind kind,
    long orderPointer,
    long controllerPointer,
    long expectedLifecycle)
{
    AssertTrue(
        RuntimeOrderTerminalReceiptStore.TryCaptureActiveLifecycle(
            generation,
            kind,
            (nint)orderPointer,
            (nint)controllerPointer,
            out var actualLifecycle),
        "The exact active lifecycle was not found.");
    AssertEqual(expectedLifecycle, actualLifecycle,
        "The exact active lifecycle sequence changed.");
}

static void AssertInactive(
    long generation,
    RuntimeOrderKind kind,
    long orderPointer,
    long controllerPointer)
{
    AssertCaptureMissing(
        generation,
        kind,
        orderPointer,
        controllerPointer,
        "A terminal lifecycle remained active.");
}

static void AssertCaptureMissing(
    long generation,
    RuntimeOrderKind kind,
    long orderPointer,
    long controllerPointer,
    string message)
{
    AssertTrue(
        !RuntimeOrderTerminalReceiptStore.TryCaptureActiveLifecycle(
            generation,
            kind,
            (nint)orderPointer,
            (nint)controllerPointer,
            out _),
        message);
}

static void AssertMissing(RuntimeOrderBindingToken token, string message)
{
    AssertTrue(!RuntimeOrderTerminalReceiptStore.TryFind(token, out _), message);
}

static void AssertThrows<TException>(Action action, string message)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException(message);
}

static void AssertEqual<T>(T expected, T actual, string message)
    where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{message} Expected={expected}; Actual={actual}.");
    }
}

static void AssertTrue(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
