using System.Collections;
using System.Reflection;
using MystiaStewardCompanion.Save;

try
{
    VerifyManagedDefaultAndUnmanagedAutomaticParticipation();
    VerifyEnablePauseAndQueueTail();
    VerifyOrderScopedAndProtectedFrontInsertion();
    VerifyManagedSetTransitionsRequeueCurrentLifecycles();
    VerifyAuthorityResetRevokesManagedParticipation();
    VerifyNewLifecycleForEnabledGuestStartsPaused();
    VerifyPublicAndNativeIdentityAreStrict();
    VerifyOptionalBindingEnrichmentAndAuthorization();
    VerifyGenerationRevisionAndBatchMutationAreAtomic();
    VerifyPermitSerializesParticipationMutation();
    VerifyDeniedPermitsDoNotRetainMonitor();
    VerifyBusinessRetirementIsExactIdempotentAndPermitSerialized();
    VerifyRetirementAndBusinessRollover();
    VerifyBoundsAndImmutableSnapshots();
    VerifyTerminalReceiptBeforeDeliveryPolicyIsExactAndWrapperFree();
    VerifyPausedCookingClocksResumeWithoutChargingPausedTime();
    AutoResumeTests.Run();
    Console.WriteLine(
        "PASS: rare-guest participation is generation/revision fenced, exact-lifecycle scoped, "
        + "default-paused for managed guests, automatically participating for unmanaged guests, "
        + "explicit-position queued, non-preemptive-front insertable, permit-serialized, "
        + "authority-resettable, ABA-safe, bounded, "
        + "immutable, and wrapper-free.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL: {ex}");
    return 1;
}

static void VerifyManagedDefaultAndUnmanagedAutomaticParticipation()
{
    var revision = Begin(11, 7);
    var unmanaged = Identity(11, 1, 101, 8);
    var managed = Identity(11, 2, 102, 7);

    revision = RuntimeRareGuestParticipationState.ReconcileCurrentOrders(
        11,
        revision,
        collectionComplete: true,
        new[] { Observe(unmanaged), Observe(managed) });
    var snapshot = RuntimeRareGuestParticipationState.Snapshot;

    AssertEqual(2L, revision, "Initial authoritative reconciliation did not advance revision once.");
    AssertEqual(2, snapshot.Orders.Count, "Current exact rare-order set was not retained.");
    AssertOrder(snapshot, unmanaged, managed: false, participating: true, queuePosition: 1,
        RuntimeRareGuestParticipationState.AutomaticParticipationReason);
    AssertOrder(snapshot, managed, managed: true, participating: false, queuePosition: 0,
        RuntimeRareGuestParticipationState.DefaultPausedReason);
    AssertTrue(Authorize(unmanaged), "An unmanaged current lifecycle was not admitted automatically.");
    AssertFalse(Authorize(managed), "A managed current lifecycle did not default to paused.");

    var idempotentRevision = RuntimeRareGuestParticipationState.ReconcileCurrentOrders(
        11,
        revision,
        collectionComplete: true,
        new[] { Observe(unmanaged), Observe(managed) });
    AssertEqual(revision, idempotentRevision, "An identical current-order projection advanced revision.");
    AssertQueue(RuntimeRareGuestParticipationState.Snapshot, unmanaged);
}

static void VerifyEnablePauseAndQueueTail()
{
    var revision = Begin(21, 7, 9);
    var automatic = Identity(21, 1, 201, 8);
    var first = Identity(21, 2, 202, 7);
    var second = Identity(21, 3, 203, 9);
    revision = RuntimeRareGuestParticipationState.ReconcileCurrentOrders(
        21,
        revision,
        collectionComplete: true,
        new[] { Observe(automatic), Observe(first), Observe(second) });

    revision = MutateGuest(
        21,
        revision,
        7,
        new[] { first },
        action: RuntimeRareGuestParticipationAction.EnableTail);
    AssertOrder(RuntimeRareGuestParticipationState.Snapshot, first, true, true, 2,
        RuntimeRareGuestParticipationState.ManuallyEnabledReason);

    revision = MutateGuest(
        21,
        revision,
        7,
        new[] { first },
        action: RuntimeRareGuestParticipationAction.Pause);
    AssertOrder(RuntimeRareGuestParticipationState.Snapshot, first, true, false, 0,
        RuntimeRareGuestParticipationState.ManuallyPausedReason);

    revision = MutateGuest(
        21,
        revision,
        9,
        new[] { second },
        action: RuntimeRareGuestParticipationAction.EnableTail);
    revision = MutateGuest(
        21,
        revision,
        7,
        new[] { first },
        action: RuntimeRareGuestParticipationAction.EnableTail);

    var snapshot = RuntimeRareGuestParticipationState.Snapshot;
    AssertQueue(snapshot, automatic, second, first);
    AssertOrder(snapshot, second, true, true, 2,
        RuntimeRareGuestParticipationState.ManuallyEnabledReason);
    AssertOrder(snapshot, first, true, true, 3,
        RuntimeRareGuestParticipationState.ManuallyEnabledReason);

    var repeatedRevision = MutateGuest(
        21,
        revision,
        7,
        new[] { first },
        action: RuntimeRareGuestParticipationAction.EnableTail);
    AssertEqual(revision, repeatedRevision, "Repeated enable was not idempotent.");
    AssertQueue(RuntimeRareGuestParticipationState.Snapshot, automatic, second, first);
}

static void VerifyOrderScopedAndProtectedFrontInsertion()
{
    var revision = Begin(22, 7);
    var automatic = Identity(22, 1, 221, 8);
    var remainder = Identity(22, 2, 222, 9);
    var protectedLater = Identity(22, 3, 223, 10);
    var first = Identity(22, 4, 224, 7);
    var second = Identity(22, 5, 225, 7);
    revision = RuntimeRareGuestParticipationState.ReconcileCurrentOrders(
        22,
        revision,
        collectionComplete: true,
        new[]
        {
            Observe(automatic),
            Observe(remainder),
            Observe(protectedLater),
            Observe(first),
            Observe(second),
        });

    revision = MutateGuest(
        22,
        revision,
        7,
        new[] { first, second },
        RuntimeRareGuestParticipationAction.EnableFront,
        new[] { protectedLater, automatic });
    var front = RuntimeRareGuestParticipationState.Snapshot;
    AssertQueue(front, automatic, remainder, protectedLater, first, second);
    AssertOrder(front, automatic, false, true, 1,
        RuntimeRareGuestParticipationState.AutomaticParticipationReason);
    AssertOrder(front, protectedLater, false, true, 3,
        RuntimeRareGuestParticipationState.AutomaticParticipationReason);
    AssertOrder(front, first, true, true, 4,
        RuntimeRareGuestParticipationState.ManuallyEnabledReason);
    AssertOrder(front, second, true, true, 5,
        RuntimeRareGuestParticipationState.ManuallyEnabledReason);
    AssertOrder(front, remainder, false, true, 2,
        RuntimeRareGuestParticipationState.AutomaticParticipationReason);

    revision = MutateOrder(
        22,
        revision,
        first,
        RuntimeRareGuestParticipationAction.Pause);
    AssertQueue(RuntimeRareGuestParticipationState.Snapshot, automatic, remainder, protectedLater, second);
    AssertOrder(RuntimeRareGuestParticipationState.Snapshot, second, true, true, 4,
        RuntimeRareGuestParticipationState.ManuallyEnabledReason);

    revision = MutateOrder(
        22,
        revision,
        first,
        RuntimeRareGuestParticipationAction.EnableTail);
    AssertQueue(RuntimeRareGuestParticipationState.Snapshot, automatic, remainder, protectedLater, second, first);

    var idempotentRevision = MutateOrder(
        22,
        revision,
        first,
        RuntimeRareGuestParticipationAction.EnableFront,
        new[] { protectedLater });
    AssertEqual(revision, idempotentRevision,
        "Front-enabling an already participating order unexpectedly reordered it.");
    AssertQueue(RuntimeRareGuestParticipationState.Snapshot, automatic, remainder, protectedLater, second, first);

    revision = MutateOrder(
        22,
        revision,
        second,
        RuntimeRareGuestParticipationAction.Pause);
    revision = MutateOrder(
        22,
        revision,
        second,
        RuntimeRareGuestParticipationAction.EnableFront,
        new[] { protectedLater });
    AssertQueue(RuntimeRareGuestParticipationState.Snapshot, automatic, remainder, protectedLater, second, first);

    revision = MutateOrder(
        22,
        revision,
        first,
        RuntimeRareGuestParticipationAction.Pause);
    AssertConflict(
        () => MutateOrder(
            22,
            revision,
            first,
            RuntimeRareGuestParticipationAction.EnableFront,
            new[] { first }),
        "protected-order-not-participating");
    AssertConflict(
        () => MutateOrder(
            22,
            revision,
            first,
            RuntimeRareGuestParticipationAction.EnableFront,
            new[] { Identity(22, 9, 229, 8) }),
        "protected-order-not-current");
    AssertThrows<ArgumentException>(
        () => RuntimeRareGuestParticipationState.MutateParticipation(
            22,
            revision,
            RuntimeRareGuestParticipationAction.EnableTail,
            RuntimeRareGuestParticipationTargetScope.Order,
            7,
            new[] { first, second }),
        "Order scope accepted multiple lifecycle identities.");
}

static void VerifyManagedSetTransitionsRequeueCurrentLifecycles()
{
    var revision = Begin(31, 7);
    var first = Identity(31, 1, 301, 7);
    var second = Identity(31, 2, 302, 7);
    var automatic = Identity(31, 3, 303, 8);
    revision = RuntimeRareGuestParticipationState.ReconcileCurrentOrders(
        31,
        revision,
        collectionComplete: true,
        new[] { Observe(first), Observe(second), Observe(automatic) });
    revision = MutateGuest(
        31,
        revision,
        7,
        new[] { first, second },
        action: RuntimeRareGuestParticipationAction.EnableTail);

    revision = RuntimeRareGuestParticipationState.ReplaceManagedGuestIds(
        31,
        revision,
        Array.Empty<int>());
    var unconfigured = RuntimeRareGuestParticipationState.Snapshot;
    AssertQueue(unconfigured, automatic, first, second);
    AssertOrder(unconfigured, first, false, true, 2,
        RuntimeRareGuestParticipationState.AutomaticParticipationReason);
    AssertOrder(unconfigured, second, false, true, 3,
        RuntimeRareGuestParticipationState.AutomaticParticipationReason);

    revision = RuntimeRareGuestParticipationState.ReplaceManagedGuestIds(
        31,
        revision,
        new[] { 7 });
    var configuredAgain = RuntimeRareGuestParticipationState.Snapshot;
    AssertOrder(configuredAgain, first, true, false, 0,
        RuntimeRareGuestParticipationState.DefaultPausedReason);
    AssertOrder(configuredAgain, second, true, false, 0,
        RuntimeRareGuestParticipationState.DefaultPausedReason);
    AssertQueue(configuredAgain, automatic);

    var sameRevision = RuntimeRareGuestParticipationState.ReplaceManagedGuestIds(
        31,
        revision,
        new[] { 7 });
    AssertEqual(revision, sameRevision, "An identical managed set advanced participation revision.");
}

static void VerifyAuthorityResetRevokesManagedParticipation()
{
    var revision = Begin(36, 7);
    var managed = Identity(36, 1, 361, 7);
    var automatic = Identity(36, 2, 362, 8);
    revision = RuntimeRareGuestParticipationState.ReconcileCurrentOrders(
        36,
        revision,
        collectionComplete: true,
        new[] { Observe(managed), Observe(automatic) });
    revision = MutateGuest(
        36,
        revision,
        7,
        new[] { managed },
        action: RuntimeRareGuestParticipationAction.EnableTail);
    AssertQueue(RuntimeRareGuestParticipationState.Snapshot, automatic, managed);

    var beforeResetRevision = revision;
    revision = RuntimeRareGuestParticipationState.ApplyManagedGuestIdsFromAuthority(
        new[] { 7 },
        resetManagedParticipation: true);
    var reset = RuntimeRareGuestParticipationState.Snapshot;
    AssertEqual(beforeResetRevision + 1, revision,
        "Authority reset with an unchanged roster did not advance revision.");
    AssertOrder(reset, managed, true, false, 0,
        RuntimeRareGuestParticipationState.AuthorityResetPausedReason);
    AssertOrder(reset, automatic, false, true, 1,
        RuntimeRareGuestParticipationState.AutomaticParticipationReason);
    AssertQueue(reset, automatic);

    var alreadyPausedRevision = revision;
    revision = RuntimeRareGuestParticipationState.ApplyManagedGuestIdsFromAuthority(
        new[] { 7 },
        resetManagedParticipation: true);
    AssertEqual(alreadyPausedRevision + 1, revision,
        "Repeated authority reset failed to fence commands while all managed lifecycles were paused.");
    AssertQueue(RuntimeRareGuestParticipationState.Snapshot, automatic);

    var ordinaryRevision = RuntimeRareGuestParticipationState.ApplyManagedGuestIdsFromAuthority(
        new[] { 7 },
        resetManagedParticipation: false);
    AssertEqual(revision, ordinaryRevision,
        "Ordinary unchanged roster publication behaved like an authority reset.");

    revision = MutateGuest(
        36,
        revision,
        7,
        new[] { managed },
        action: RuntimeRareGuestParticipationAction.EnableTail);
    revision = RuntimeRareGuestParticipationState.ReplaceManagedGuestIds(
        36,
        revision,
        Array.Empty<int>(),
        resetManagedParticipation: true);
    var removed = RuntimeRareGuestParticipationState.Snapshot;
    AssertQueue(removed, automatic, managed);
    AssertOrder(removed, automatic, false, true, 1,
        RuntimeRareGuestParticipationState.AutomaticParticipationReason);
    AssertOrder(removed, managed, false, true, 2,
        RuntimeRareGuestParticipationState.AutomaticParticipationReason);
}

static void VerifyNewLifecycleForEnabledGuestStartsPaused()
{
    var revision = Begin(41, 7);
    var firstLifecycle = Identity(41, 1, 401, 7);
    revision = RuntimeRareGuestParticipationState.ReconcileCurrentOrders(
        41,
        revision,
        collectionComplete: true,
        new[] { Observe(firstLifecycle) });
    revision = MutateGuest(
        41,
        revision,
        7,
        new[] { firstLifecycle },
        action: RuntimeRareGuestParticipationAction.EnableTail);

    var nextLifecycle = Identity(41, 2, 402, 7);
    revision = RuntimeRareGuestParticipationState.ReconcileCurrentOrders(
        41,
        revision,
        collectionComplete: true,
        new[] { Observe(firstLifecycle), Observe(nextLifecycle) });
    var snapshot = RuntimeRareGuestParticipationState.Snapshot;
    AssertOrder(snapshot, firstLifecycle, true, true, 1,
        RuntimeRareGuestParticipationState.ManuallyEnabledReason);
    AssertOrder(snapshot, nextLifecycle, true, false, 0,
        RuntimeRareGuestParticipationState.DefaultPausedReason);
    AssertFalse(Authorize(nextLifecycle),
        "A later lifecycle inherited another lifecycle's explicit participation grant.");
    AssertConflict(
        () => MutateGuest(
            41,
            revision,
            7,
            new[] { firstLifecycle },
            action: RuntimeRareGuestParticipationAction.EnableTail),
        "guest-current-orders-mismatch");
    AssertEqual(revision, RuntimeRareGuestParticipationState.Snapshot.Revision,
        "A stale guest lifecycle set changed participation revision.");
    AssertFalse(Authorize(nextLifecycle),
        "A stale guest lifecycle set partially enabled a newly arrived lifecycle.");
}

static void VerifyPublicAndNativeIdentityAreStrict()
{
    var revision = Begin(51, 7);
    var current = Identity(51, 1, 501, 7);
    var binding = Binding(current, 0x5100, 0x5200);
    revision = RuntimeRareGuestParticipationState.ReconcileCurrentOrders(
        51,
        revision,
        collectionComplete: true,
        new[] { Observe(current, binding) });

    AssertThrows<ArgumentException>(
        () => RuntimeRareGuestParticipationState.ReconcileCurrentOrders(
            51,
            revision,
            collectionComplete: true,
            new[] { Observe(new(51, "N-0001", 501, 7)) }),
        "A normal-order trace was accepted as rare-order identity.");
    AssertThrows<ArgumentException>(
        () => RuntimeRareGuestParticipationState.ReconcileCurrentOrders(
            51,
            revision,
            collectionComplete: true,
            new[] { Observe(new(51, "R-１", 501, 7)) }),
        "A Unicode digit was accepted in the public rare-order trace.");
    AssertThrows<ArgumentException>(
        () => RuntimeRareGuestParticipationState.ReconcileCurrentOrders(
            51,
            revision,
            collectionComplete: true,
            new[] { Observe(new(51, "R-0001", 0, 7)) }),
        "A missing lifecycle sequence was accepted.");
    AssertThrows<ArgumentException>(
        () => RuntimeRareGuestParticipationState.ReconcileCurrentOrders(
            51,
            revision,
            collectionComplete: true,
            new[] { Observe(new(51, "R-0001", 501, -1)) }),
        "A missing canonical guest id was accepted.");
    AssertThrows<ArgumentException>(
        () => RuntimeRareGuestParticipationState.ReconcileCurrentOrders(
            51,
            revision,
            collectionComplete: true,
            new[]
            {
                Observe(current),
                Observe(current with { GuestId = 8 }),
            }),
        "One public trace/lifecycle pair identified multiple canonical guests.");

    AssertThrows<ArgumentException>(
        () => RuntimeRareGuestParticipationState.ReconcileCurrentOrders(
            51,
            revision,
            collectionComplete: true,
            new[]
            {
                Observe(current, binding with { OrderKind = RuntimeOrderKind.Normal }),
            }),
        "A NormalOrder native binding was accepted for a rare lifecycle.");
    AssertThrows<ArgumentException>(
        () => RuntimeRareGuestParticipationState.ReconcileCurrentOrders(
            51,
            revision,
            collectionComplete: true,
            new[]
            {
                Observe(current, binding with { LifecycleSequence = 502 }),
            }),
        "A native binding for a different lifecycle was accepted.");
    AssertThrows<ArgumentException>(
        () => RuntimeRareGuestParticipationState.ReconcileCurrentOrders(
            51,
            revision,
            collectionComplete: true,
            new[]
            {
                Observe(current, binding with { OrderPointer = 0 }),
            }),
        "A native binding with no order pointer was accepted.");

    AssertConflict(
        () => RuntimeRareGuestParticipationState.ReconcileCurrentOrders(
            51,
            revision,
            collectionComplete: true,
            new[] { Observe(current with { GuestId = 8 }) }),
        "exact-identity-conflict");
    AssertConflict(
        () => RuntimeRareGuestParticipationState.ReconcileCurrentOrders(
            51,
            revision,
            collectionComplete: true,
            new[] { Observe(current, binding with { ControllerPointer = (nint)0x5300 }) }),
        "exact-identity-conflict");
    AssertEqual(revision, RuntimeRareGuestParticipationState.Snapshot.Revision,
        "Rejected identity drift partially changed state.");
    AssertEqual(binding, Find(current).Binding,
        "Rejected identity drift replaced the exact native binding.");
}

static void VerifyOptionalBindingEnrichmentAndAuthorization()
{
    var revision = Begin(61, 7);
    var current = Identity(61, 1, 601, 7);
    var binding = Binding(current, 0x6100, 0x6200);
    revision = RuntimeRareGuestParticipationState.ReconcileCurrentOrders(
        61,
        revision,
        collectionComplete: true,
        new[] { Observe(current) });
    revision = MutateGuest(
        61,
        revision,
        7,
        new[] { current },
        action: RuntimeRareGuestParticipationAction.EnableTail);

    AssertTrue(Authorize(current), "Pre-binding admission rejected an enabled exact public lifecycle.");
    AssertFalse(Authorize(current, binding),
        "A side-effect binding was authorized before the state had enriched that exact binding.");
    using (var unavailableBindingPermit =
        RuntimeRareGuestParticipationState.AcquireBoundSideEffectPermit(current, binding))
    {
        AssertEqual("order-binding-unavailable", unavailableBindingPermit.Decision.ReasonCode,
            "A structurally exact but not-yet-enriched binding returned the wrong denial reason.");
    }

    AssertTrue(
        RuntimeRareGuestParticipationState.TryEnrichCurrentBinding(current, binding, out var enrichment)
            && enrichment.Allowed,
        "The exact active native binding could not enrich its admitted public lifecycle.");
    AssertEqual(revision, RuntimeRareGuestParticipationState.Snapshot.Revision,
        "Native binding enrichment consumed a public participation revision.");
    AssertTrue(Authorize(current, binding), "Exact enriched side-effect binding was rejected.");
    AssertFalse(Authorize(current, binding with { ControllerPointer = (nint)0x6201 }),
        "A different native controller binding was authorized.");
    AssertFalse(Authorize(current, binding with { LifecycleSequence = 602 }),
        "A binding for another lifecycle was authorized.");

    var idempotentRevision = RuntimeRareGuestParticipationState.ReconcileCurrentOrders(
        61,
        revision,
        collectionComplete: true,
        new[] { Observe(current) });
    AssertEqual(revision, idempotentRevision,
        "A projection without a binding erased a previously enriched binding.");
    AssertEqual(binding, Find(current).Binding,
        "A projection without a binding erased exact native identity.");
}

// 同一修订上的并发批量写入只能有一个成功；冲突请求不得部分改变名单或订单队列。
static void VerifyGenerationRevisionAndBatchMutationAreAtomic()
{
    var revision = Begin(71, 7);
    var first = Identity(71, 1, 701, 7);
    var second = Identity(71, 2, 702, 7);
    var unmanaged = Identity(71, 3, 703, 8);
    revision = RuntimeRareGuestParticipationState.ReconcileCurrentOrders(
        71,
        revision,
        collectionComplete: true,
        new[] { Observe(first), Observe(second), Observe(unmanaged) });

    AssertConflict(
        () => MutateGuest(
            70,
            revision,
            7,
            new[] { first with { BusinessGeneration = 70 } },
            action: RuntimeRareGuestParticipationAction.EnableTail),
        "business-generation-mismatch");
    AssertConflict(
        () => MutateGuest(
            71,
            revision - 1,
            7,
            new[] { first, second },
            action: RuntimeRareGuestParticipationAction.EnableTail),
        "participation-revision-mismatch");
    AssertConflict(
        () => MutateGuest(
            71,
            revision,
            7,
            new[] { first, Identity(71, 9, 709, 7) },
            action: RuntimeRareGuestParticipationAction.EnableTail),
        "guest-current-orders-mismatch");
    AssertFalse(Authorize(first), "A rejected mixed batch partially enabled its first target.");
    AssertConflict(
        () => MutateGuest(
            71,
            revision,
            8,
            new[] { unmanaged },
            action: RuntimeRareGuestParticipationAction.Pause),
        "order-not-managed");
    AssertTrue(Authorize(unmanaged), "A managed-only mutation paused an unmanaged lifecycle.");

    var successes = 0;
    var conflicts = 0;
    using var start = new ManualResetEventSlim();
    var tasks = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
    {
        start.Wait();
        try
        {
            MutateGuest(
                71,
                revision,
                7,
                new[] { first, second },
                action: RuntimeRareGuestParticipationAction.EnableTail);
            Interlocked.Increment(ref successes);
        }
        catch (RuntimeRareGuestParticipationConflictException ex)
            when (ex.Code == "participation-revision-mismatch")
        {
            Interlocked.Increment(ref conflicts);
        }
    })).ToArray();
    start.Set();
    Task.WaitAll(tasks);
    AssertEqual(1, successes, "Optimistic revision fencing admitted multiple concurrent writers.");
    AssertEqual(1, conflicts, "A stale concurrent writer was not rejected by exact revision.");
    AssertOrder(RuntimeRareGuestParticipationState.Snapshot, first, true, true, 2,
        RuntimeRareGuestParticipationState.ManuallyEnabledReason);
    AssertOrder(RuntimeRareGuestParticipationState.Snapshot, second, true, true, 3,
        RuntimeRareGuestParticipationState.ManuallyEnabledReason);
}

// 已准入的副作用持有参与状态许可，暂停操作须待其释放后提交，避免执行中途改变授权事实。
static void VerifyPermitSerializesParticipationMutation()
{
    var revision = Begin(76, 7);
    var current = Identity(76, 1, 761, 7);
    var binding = Binding(current, 0x7600, 0x7610);
    revision = RuntimeRareGuestParticipationState.ReconcileCurrentOrders(
        76,
        revision,
        collectionComplete: true,
        new[] { Observe(current, binding) });
    revision = MutateGuest(
        76,
        revision,
        7,
        new[] { current },
        action: RuntimeRareGuestParticipationAction.EnableTail);

    var permit = RuntimeRareGuestParticipationState.AcquireBoundSideEffectPermit(current, binding);
    AssertTrue(permit.Allowed, "An exact participating bound lifecycle was not admitted.");
    AssertEqual("participation-allowed", permit.Decision.ReasonCode,
        "Allowed participation permit reason changed.");
    AssertEqual(binding, permit.Decision.Order?.Binding,
        "Allowed bound permit did not retain the exact enriched token.");

    var syncRoot = typeof(RuntimeRareGuestParticipationState).GetField(
        "SyncRoot",
        BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null)
        ?? throw new InvalidOperationException("Participation monitor was not found for the concurrency probe.");
    var monitorProbe = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    using var mutationStarting = new ManualResetEventSlim();
    var expectedRevision = revision;
    var mutation = Task.Run(() =>
    {
        var monitorWasAvailable = Monitor.TryEnter(syncRoot);
        if (monitorWasAvailable) Monitor.Exit(syncRoot);
        monitorProbe.SetResult(monitorWasAvailable);
        mutationStarting.Set();
        return MutateGuest(
            76,
            expectedRevision,
            7,
            new[] { current },
            action: RuntimeRareGuestParticipationAction.Pause);
    });

    var completedBeforeRelease = false;
    try
    {
        AssertFalse(monitorProbe.Task.GetAwaiter().GetResult(),
            "Allowed participation permit did not retain the state monitor.");
        AssertTrue(mutationStarting.Wait(TimeSpan.FromSeconds(2)),
            "Pause mutation worker did not reach the monitor boundary.");
        completedBeforeRelease = mutation.Wait(TimeSpan.FromMilliseconds(150));
    }
    finally
    {
        permit.Dispose();
    }

    AssertFalse(completedBeforeRelease,
        "Pause mutation completed while an admitted native boundary still held its permit.");
    AssertTrue(mutation.Wait(TimeSpan.FromSeconds(2)),
        "Pause mutation did not complete after the participation permit was released.");
    revision = mutation.GetAwaiter().GetResult();
    permit.Dispose();
    AssertOrder(RuntimeRareGuestParticipationState.Snapshot, current, true, false, 0,
        RuntimeRareGuestParticipationState.ManuallyPausedReason);
    AssertEqual(expectedRevision + 1, revision,
        "Released permit did not allow exactly one pause revision.");
}

// 拒绝许可没有需要保护的执行区间；其对象尚未释放时也不能阻塞后续合法状态更改。
static void VerifyDeniedPermitsDoNotRetainMonitor()
{
    var revision = Begin(77, 7);
    var current = Identity(77, 1, 771, 7);
    var binding = Binding(current, 0x7700, 0x7710);
    revision = RuntimeRareGuestParticipationState.ReconcileCurrentOrders(
        77,
        revision,
        collectionComplete: true,
        new[] { Observe(current, binding) });

    var pausedPermit = RuntimeRareGuestParticipationState.AcquireAdmissionPermit(current);
    revision = CompleteMutationWhileDeniedPermitLives(
        pausedPermit,
        "order-paused",
        () => MutateGuest(
            77,
            revision,
            7,
            new[] { current },
            action: RuntimeRareGuestParticipationAction.EnableTail));

    var stalePermit = RuntimeRareGuestParticipationState.AcquireAdmissionPermit(
        current with { BusinessGeneration = 76 });
    revision = CompleteMutationWhileDeniedPermitLives(
        stalePermit,
        "business-generation-mismatch",
        () => MutateGuest(
            77,
            revision,
            7,
            new[] { current },
            action: RuntimeRareGuestParticipationAction.Pause));

    revision = MutateGuest(
        77,
        revision,
        7,
        new[] { current },
        action: RuntimeRareGuestParticipationAction.EnableTail);
    var mismatchedBindingPermit = RuntimeRareGuestParticipationState.AcquireBoundSideEffectPermit(
        current,
        binding with { ControllerPointer = (nint)0x7711 });
    revision = CompleteMutationWhileDeniedPermitLives(
        mismatchedBindingPermit,
        "order-binding-mismatch",
        () => MutateGuest(
            77,
            revision,
            7,
            new[] { current },
            action: RuntimeRareGuestParticipationAction.Pause));

    AssertOrder(RuntimeRareGuestParticipationState.Snapshot, current, true, false, 0,
        RuntimeRareGuestParticipationState.ManuallyPausedReason);
}

static long CompleteMutationWhileDeniedPermitLives(
    RuntimeRareGuestParticipationPermit permit,
    string expectedReasonCode,
    Func<long> mutation)
{
    AssertFalse(permit.Allowed, "A denial-path participation permit was unexpectedly allowed.");
    AssertEqual(expectedReasonCode, permit.Decision.ReasonCode,
        "Denied participation permit reason changed.");

    var task = Task.Run(mutation);
    try
    {
        AssertTrue(task.Wait(TimeSpan.FromSeconds(2)),
            "A denied participation permit retained the monitor and blocked mutation.");
        return task.GetAwaiter().GetResult();
    }
    finally
    {
        permit.Dispose();
        permit.Dispose();
    }
}

static void VerifyBusinessRetirementIsExactIdempotentAndPermitSerialized()
{
    var revision = Begin(78);
    var current = Identity(78, 1, 781, 7);
    revision = RuntimeRareGuestParticipationState.ReconcileCurrentOrders(
        78,
        revision,
        collectionComplete: true,
        new[] { Observe(current) });
    var permit = RuntimeRareGuestParticipationState.AcquireAdmissionPermit(current);
    AssertTrue(permit.Allowed, "An automatic order did not acquire the boundary permit used by the retirement probe.");

    var retirementStarted = new ManualResetEventSlim();
    var retirement = Task.Run(() =>
    {
        retirementStarted.Set();
        return RuntimeRareGuestParticipationState.EndBusinessIfCurrent(78);
    });
    try
    {
        AssertTrue(retirementStarted.Wait(TimeSpan.FromSeconds(2)),
            "Business-retirement worker did not reach the participation monitor boundary.");
        AssertFalse(retirement.Wait(TimeSpan.FromMilliseconds(150)),
            "Business retirement crossed an admitted atomic side-effect permit.");
    }
    finally
    {
        permit.Dispose();
    }

    AssertTrue(retirement.Wait(TimeSpan.FromSeconds(2)),
        "Business retirement did not finish after the admitted permit was released.");
    var retiredRevision = retirement.GetAwaiter().GetResult();
    AssertEqual(revision + 1, retiredRevision,
        "The first exact business retirement did not advance revision once.");
    AssertFalse(RuntimeRareGuestParticipationState.Snapshot.IsActive,
        "The exact business retirement left participation active.");
    AssertEqual(retiredRevision, RuntimeRareGuestParticipationState.EndBusinessIfCurrent(78),
        "A repeated Closing notification advanced retirement revision.");
    AssertEqual(retiredRevision, RuntimeRareGuestParticipationState.EndBusinessIfCurrent(999),
        "An inactive Destroyed notification changed retired state.");
    AssertConflict(
        () => MutateGuest(
            78,
            retiredRevision,
            7,
            new[] { current },
            action: RuntimeRareGuestParticipationAction.Pause),
        "business-inactive");

    RuntimeRareGuestParticipationState.BeginBusiness(79, Array.Empty<int>());
    AssertConflict(
        () => RuntimeRareGuestParticipationState.EndBusinessIfCurrent(78),
        "business-generation-mismatch");
    AssertTrue(RuntimeRareGuestParticipationState.Snapshot.IsActive,
        "A stale Closing boundary retired a newer active generation.");
    RuntimeRareGuestParticipationState.EndBusinessIfCurrent(79);
}

static void VerifyRetirementAndBusinessRollover()
{
    var revision = Begin(81, 7);
    var old = Identity(81, 1, 801, 7);
    var oldBinding = Binding(old, 0x8100, 0x8200);
    revision = RuntimeRareGuestParticipationState.ReconcileCurrentOrders(
        81,
        revision,
        collectionComplete: true,
        new[] { Observe(old, oldBinding) });
    revision = MutateGuest(
        81,
        revision,
        7,
        new[] { old },
        action: RuntimeRareGuestParticipationAction.EnableTail);
    var incompleteRevision = RuntimeRareGuestParticipationState.ReconcileCurrentOrders(
        81,
        revision,
        collectionComplete: false,
        currentOrders: null);
    AssertEqual(revision, incompleteRevision,
        "An incomplete/error collection changed participation revision.");
    AssertTrue(Authorize(old, oldBinding),
        "An incomplete/error collection retired an existing exact lifecycle.");

    revision = RuntimeRareGuestParticipationState.ReconcileCurrentOrders(
        81,
        revision,
        collectionComplete: true,
        Array.Empty<RuntimeRareGuestParticipationOrderObservation>());
    AssertFalse(Authorize(old), "An absent lifecycle remained authorized after complete reconciliation.");
    AssertEqual(0, RuntimeRareGuestParticipationState.Snapshot.Orders.Count,
        "An absent lifecycle remained in the current snapshot.");
    AssertConflict(
        () => RuntimeRareGuestParticipationState.ReconcileCurrentOrders(
            81,
            revision,
            collectionComplete: true,
            new[]
            {
                Observe(old, oldBinding with { ControllerPointer = (nint)0x8201 }),
            }),
        "exact-identity-conflict");
    AssertEqual(0, RuntimeRareGuestParticipationState.Snapshot.Orders.Count,
        "A retired public identity was rebound through a different native token.");
    AssertConflict(
        () => RuntimeRareGuestParticipationState.ReconcileCurrentOrders(
            81,
            revision,
            collectionComplete: true,
            new[] { Observe(old) }),
        "retired-lifecycle-reappeared");
    AssertEqual(0, RuntimeRareGuestParticipationState.Snapshot.Orders.Count,
        "A retired exact public lifecycle re-entered the current set without changing its token.");

    revision = RuntimeRareGuestParticipationState.EndBusinessIfCurrent(81);
    var ended = RuntimeRareGuestParticipationState.Snapshot;
    AssertFalse(ended.IsActive, "Ended business remained active.");
    AssertEqual(0, ended.Orders.Count, "Ended business retained order authorization.");
    AssertConflict(
        () => RuntimeRareGuestParticipationState.ReconcileCurrentOrders(
            81,
            revision,
            collectionComplete: true,
            Array.Empty<RuntimeRareGuestParticipationOrderObservation>()),
        "business-inactive");
    AssertConflict(
        () => RuntimeRareGuestParticipationState.BeginBusiness(81, Array.Empty<int>()),
        "business-generation-not-newer");

    var nextRevision = RuntimeRareGuestParticipationState.BeginBusiness(82, new[] { 7 });
    var reusedPointersNewLifecycle = Identity(82, 2, 802, 7);
    var nextBinding = Binding(reusedPointersNewLifecycle, 0x8100, 0x8200);
    nextRevision = RuntimeRareGuestParticipationState.ReconcileCurrentOrders(
        82,
        nextRevision,
        collectionComplete: true,
        new[] { Observe(reusedPointersNewLifecycle, nextBinding) });
    AssertFalse(Authorize(reusedPointersNewLifecycle),
        "A new business/lifecycle inherited stale participation authorization.");
    AssertFalse(Authorize(old), "A stale business identity crossed generation rollover.");
    AssertEqual(0, Find(reusedPointersNewLifecycle).QueuePosition,
        "A new managed lifecycle did not reset to default paused after rollover.");
}

static void VerifyBoundsAndImmutableSnapshots()
{
    RuntimeRareGuestParticipationState.Reset();
    AssertThrows<ArgumentOutOfRangeException>(
        () => RuntimeRareGuestParticipationState.BeginBusiness(
            91,
            Enumerable.Range(0, RuntimeRareGuestParticipationState.MaximumManagedGuestIds + 1)),
        "An unbounded managed guest set was accepted.");
    AssertThrows<ArgumentException>(
        () => RuntimeRareGuestParticipationState.BeginBusiness(91, new[] { 7, 7 }),
        "Duplicate managed guest ids were accepted.");

    var revision = RuntimeRareGuestParticipationState.BeginBusiness(91, new[] { 9, 7 });
    AssertSequenceEqual(new[] { 7, 9 }, RuntimeRareGuestParticipationState.Snapshot.ManagedGuestIds,
        "Managed guest snapshot was not canonical and sorted.");
    AssertThrows<ArgumentOutOfRangeException>(
        () => RuntimeRareGuestParticipationState.ReconcileCurrentOrders(
            91,
            revision,
            collectionComplete: true,
            Enumerable.Range(1, RuntimeRareGuestParticipationState.MaximumCurrentOrders + 1)
                .Select(index => Observe(Identity(91, index, index, 8)))),
        "An unbounded current lifecycle set was accepted.");

    revision = RuntimeRareGuestParticipationState.ReconcileCurrentOrders(
        91,
        revision,
        collectionComplete: true,
        new[] { Observe(Identity(91, 1, 901, 8)) });
    var snapshot = RuntimeRareGuestParticipationState.Snapshot;
    AssertReadOnly(snapshot.ManagedGuestIds, "Managed guest ids expose a mutable collection.");
    AssertReadOnly(snapshot.Orders, "Order snapshots expose a mutable collection.");

    var retainedTypes = typeof(RuntimeRareGuestParticipationOrderSnapshot)
        .GetProperties()
        .Select(property => property.PropertyType)
        .ToArray();
    AssertFalse(retainedTypes.Any(type => type == typeof(object)),
        "Order participation snapshot exposes an untyped runtime wrapper slot.");
    AssertFalse(retainedTypes.Any(type =>
            (type.Namespace ?? "").StartsWith("UnityEngine", StringComparison.Ordinal)
            || (type.Namespace ?? "").StartsWith("Il2Cpp", StringComparison.Ordinal)),
        "Order participation snapshot retains a Unity/IL2CPP wrapper type.");
}

static long Begin(long generation, params int[] managedGuestIds)
{
    RuntimeRareGuestParticipationState.Reset();
    return RuntimeRareGuestParticipationState.BeginBusiness(generation, managedGuestIds);
}

static long MutateGuest(
    long generation,
    long expectedRevision,
    int guestId,
    IEnumerable<RuntimeRareGuestParticipationOrderIdentity> expectedCurrentOrders,
    RuntimeRareGuestParticipationAction action,
    IEnumerable<RuntimeRareGuestParticipationOrderIdentity>? protectedOrders = null)
{
    return RuntimeRareGuestParticipationState.MutateParticipation(
        generation,
        expectedRevision,
        action,
        RuntimeRareGuestParticipationTargetScope.Guest,
        guestId,
        expectedCurrentOrders,
        protectedOrders);
}

static long MutateOrder(
    long generation,
    long expectedRevision,
    RuntimeRareGuestParticipationOrderIdentity identity,
    RuntimeRareGuestParticipationAction action,
    IEnumerable<RuntimeRareGuestParticipationOrderIdentity>? protectedOrders = null)
{
    return RuntimeRareGuestParticipationState.MutateParticipation(
        generation,
        expectedRevision,
        action,
        RuntimeRareGuestParticipationTargetScope.Order,
        identity.GuestId,
        new[] { identity },
        protectedOrders);
}

static RuntimeRareGuestParticipationOrderIdentity Identity(
    long generation,
    int traceSequence,
    long lifecycleSequence,
    int guestId)
{
    return new RuntimeRareGuestParticipationOrderIdentity(
        generation,
        $"R-{traceSequence:0000}",
        lifecycleSequence,
        guestId);
}

static RuntimeRareGuestParticipationOrderObservation Observe(
    RuntimeRareGuestParticipationOrderIdentity identity,
    RuntimeOrderBindingToken? binding = null)
{
    return new RuntimeRareGuestParticipationOrderObservation(identity, binding);
}

static RuntimeOrderBindingToken Binding(
    RuntimeRareGuestParticipationOrderIdentity identity,
    long orderPointer,
    long controllerPointer)
{
    return new RuntimeOrderBindingToken(
        identity.BusinessGeneration,
        RuntimeOrderKind.Special,
        (nint)orderPointer,
        (nint)controllerPointer,
        identity.OrderLifecycleSequence);
}

static bool Authorize(
    RuntimeRareGuestParticipationOrderIdentity identity,
    RuntimeOrderBindingToken? binding = null)
{
    using var permit = binding.HasValue
        ? RuntimeRareGuestParticipationState.AcquireBoundSideEffectPermit(identity, binding.Value)
        : RuntimeRareGuestParticipationState.AcquireAdmissionPermit(identity);
    return permit.Allowed;
}

static RuntimeRareGuestParticipationOrderSnapshot Find(
    RuntimeRareGuestParticipationOrderIdentity identity)
{
    return RuntimeRareGuestParticipationState.Snapshot.Orders.Single(order => order.Identity == identity);
}

static void AssertOrder(
    RuntimeRareGuestParticipationSnapshot snapshot,
    RuntimeRareGuestParticipationOrderIdentity identity,
    bool managed,
    bool participating,
    int queuePosition,
    string reasonCode)
{
    var order = snapshot.Orders.Single(candidate => candidate.Identity == identity);
    AssertEqual(managed, order.Managed, $"Managed state changed for {identity}.");
    AssertEqual(participating, order.Participating, $"Participation changed for {identity}.");
    AssertEqual(queuePosition, order.QueuePosition, $"Queue position changed for {identity}.");
    AssertEqual(reasonCode, order.ReasonCode, $"Reason code changed for {identity}.");
}

static void AssertQueue(
    RuntimeRareGuestParticipationSnapshot snapshot,
    params RuntimeRareGuestParticipationOrderIdentity[] expected)
{
    var actual = snapshot.Orders
        .Where(order => order.Participating)
        .Select(order => order.Identity)
        .ToArray();
    AssertSequenceEqual(expected, actual, "Participating queue order changed.");
}

static void AssertConflict(Action action, string expectedCode)
{
    try
    {
        action();
    }
    catch (RuntimeRareGuestParticipationConflictException ex)
    {
        AssertEqual(expectedCode, ex.Code, "Conflict reason code changed.");
        return;
    }
    throw new InvalidOperationException($"Expected participation conflict {expectedCode} was not thrown.");
}

static void AssertReadOnly<T>(IReadOnlyList<T> values, string message)
{
    if (values is not IList list)
    {
        throw new InvalidOperationException($"{message} Collection does not implement IList for mutation probe.");
    }
    AssertTrue(list.IsReadOnly, message);
    AssertThrows<NotSupportedException>(() => list.Add(default(T)), message);
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

static void AssertSequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual, string message)
{
    if (!expected.SequenceEqual(actual))
    {
        throw new InvalidOperationException(
            $"{message} Expected=[{string.Join(", ", expected)}], Actual=[{string.Join(", ", actual)}].");
    }
}

static void AssertEqual<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{message} Expected={expected}, Actual={actual}.");
    }
}

static void AssertTrue(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
}

static void AssertFalse(bool value, string message)
{
    if (value) throw new InvalidOperationException(message);
}

// 纯终态策略必须绕过暂停授权，但不能借旧原生地址终态结束新生命周期。
static void VerifyTerminalReceiptBeforeDeliveryPolicyIsExactAndWrapperFree()
{
    RuntimeOrderTerminalReceiptStore.Clear();
    var generation = 271L;
    var orderPointer = (nint)0x2710;
    var controllerPointer = (nint)0x2720;
    var evaluatedLifecycle = RuntimeOrderTerminalReceiptStore.BeginLifecycle(
        generation,
        RuntimeOrderKind.Special,
        orderPointer,
        controllerPointer);
    var evaluatedBinding = new RuntimeOrderBindingToken(
        generation,
        RuntimeOrderKind.Special,
        orderPointer,
        controllerPointer,
        evaluatedLifecycle);
    RuntimeOrderTerminalReceiptStore.Publish(new RuntimeOrderTerminalHookState(
        generation,
        RuntimeOrderKind.Special,
        orderPointer,
        controllerPointer,
        evaluatedLifecycle,
        RuntimeOrderTerminalDisposition.Evaluated,
        RuntimeOrderTerminalReceiptSource.EvaluateOrder));
    AssertTrue(
        AutomationCookingTerminalReceiptPolicy.TryFindOrderTerminatedBeforeDelivery(
            foodDeliveryCommitted: false,
            evaluatedBinding,
            out var evaluatedReceipt),
        "An exact Evaluated receipt did not retire an undelivered cooking job.");
    AssertEqual(RuntimeOrderTerminalDisposition.Evaluated, evaluatedReceipt.Disposition,
        "The undelivered-job policy changed the exact Evaluated disposition.");

    var removedLifecycle = RuntimeOrderTerminalReceiptStore.BeginLifecycle(
        generation,
        RuntimeOrderKind.Special,
        orderPointer,
        controllerPointer);
    var removedBinding = evaluatedBinding with { LifecycleSequence = removedLifecycle };
    RuntimeOrderTerminalReceiptStore.Publish(new RuntimeOrderTerminalHookState(
        generation,
        RuntimeOrderKind.Special,
        orderPointer,
        controllerPointer,
        removedLifecycle,
        RuntimeOrderTerminalDisposition.Removed,
        RuntimeOrderTerminalReceiptSource.RemoveFromOrder));
    AssertTrue(
        AutomationCookingTerminalReceiptPolicy.TryFindOrderTerminatedBeforeDelivery(
            foodDeliveryCommitted: false,
            removedBinding,
            out var removedReceipt),
        "An exact Removed receipt did not retire an undelivered cooking job.");
    AssertEqual(RuntimeOrderTerminalDisposition.Removed, removedReceipt.Disposition,
        "The undelivered-job policy changed the exact Removed disposition.");
    var currentLifecycle = RuntimeOrderTerminalReceiptStore.BeginLifecycle(
        generation,
        RuntimeOrderKind.Special,
        orderPointer,
        controllerPointer);
    var currentBinding = evaluatedBinding with { LifecycleSequence = currentLifecycle };
    AssertTrue(
        !AutomationCookingTerminalReceiptPolicy.TryFindOrderTerminatedBeforeDelivery(
            foodDeliveryCommitted: false,
            currentBinding,
            out _),
        "A stale receipt from a previous native lifecycle retired a newer job without its own receipt.");
    AssertTrue(
        !AutomationCookingTerminalReceiptPolicy.TryFindOrderTerminatedBeforeDelivery(
            foodDeliveryCommitted: true,
            removedBinding,
            out _),
        "A terminal receipt bypassed cleanup after Mod delivery had already committed.");
    AssertTrue(
        !AutomationCookingTerminalReceiptPolicy.TryFindOrderTerminatedBeforeDelivery(
            foodDeliveryCommitted: false,
            orderBinding: null,
            out _),
        "A job without an exact native binding guessed a terminal receipt.");

    var lease = new AutomationCookingControllerLease();
    AssertTrue(
        lease.Release(
            AutomationCookingControllerLeaseReleaseReason.OrderTerminatedBeforeDelivery,
            new DateTime(2026, 10, 28, 12, 41, 30, DateTimeKind.Utc)),
        "Terminal-before-delivery retirement did not release its logical controller lease.");
    AssertTrue(!lease.HoldsReservation,
        "Terminal-before-delivery retirement retained a logical controller reservation.");
    AssertEqual(
        AutomationCookingControllerLeaseReleaseReason.OrderTerminatedBeforeDelivery,
        lease.ReleaseReason,
        "Terminal-before-delivery retirement lost its exact lease-release reason.");
    RuntimeOrderTerminalReceiptStore.Clear();
}


/// <summary>
/// 使用实际生产计时器检验长时间调度暂停：暂停的墙钟时间不能耗尽料理停滞、送达和评价预算；
/// 恢复后应保留此前的有效消耗，并从下一次连续有效观测继续计时，不重置制作进度。
/// </summary>
static void VerifyPausedCookingClocksResumeWithoutChargingPausedTime()
{
    var started = new DateTime(2026, 10, 28, 13, 0, 0, DateTimeKind.Utc);
    var delivery = new AutomationEffectiveTimeoutClock(started, initiallyEligible: true);
    var evaluation = new AutomationOrderEvaluationCloseoutTracker(started, 10,
        TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(60));
    var cooking = new AutomationCookingJobTracker(291, started, 1, 0.4f);
    delivery.Observe(started.AddSeconds(2), eligible: true);
    evaluation.RecordFailure(started.AddSeconds(2), eligible: true);
    cooking.Observe(new AutomationCookingObservation(started.AddSeconds(2), 291,
        AutomationCookingObservationKind.Owned, 1, 0.4f));
    delivery.Observe(started.AddSeconds(3), eligible: false);
    evaluation.Suspend(started.AddSeconds(3));
    cooking.Suspend(started.AddSeconds(3));
    var resumed = started.AddHours(1);
    delivery.Observe(resumed, eligible: true);
    AssertFalse(evaluation.RecordFailure(resumed, eligible: true),
        "一小时调度暂停不应耗尽订单评价有效时限。");
    var transition = cooking.Observe(new AutomationCookingObservation(resumed, 291,
        AutomationCookingObservationKind.Owned, 1, 0.4f));
    AssertFalse(transition.Terminal, "恢复时不能把暂停的墙钟时间判为制作停滞。");
    AssertEqual(TimeSpan.FromSeconds(2), delivery.Elapsed, "送达时钟误计暂停时间。");
    AssertEqual(TimeSpan.FromSeconds(2), evaluation.EffectiveElapsed, "评价时钟误计暂停时间。");
    AssertEqual(TimeSpan.FromSeconds(2), cooking.EffectiveStallElapsed, "制作时钟误计暂停时间。");
    AssertEqual(0.4f, cooking.LastProgress, "恢复调度不应重置已开始料理的进度。");
    delivery.Observe(resumed.AddSeconds(1), eligible: true);
    evaluation.RecordFailure(resumed.AddSeconds(1), eligible: true);
    cooking.Observe(new AutomationCookingObservation(resumed.AddSeconds(1), 291,
        AutomationCookingObservationKind.Owned, 1, 0.4f));
    AssertEqual(TimeSpan.FromSeconds(3), delivery.Elapsed, "恢复后的送达时钟未继续计时。");
    AssertEqual(TimeSpan.FromSeconds(3), evaluation.EffectiveElapsed, "恢复后的评价时钟未继续计时。");
    AssertEqual(TimeSpan.FromSeconds(3), cooking.EffectiveStallElapsed, "恢复后的制作时钟未继续计时。");
}
