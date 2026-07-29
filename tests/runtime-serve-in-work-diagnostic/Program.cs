using System.Text.Json;
using MystiaStewardCompanion.Save;

try
{
    AssertSnapshotIsManagedAndSerializable();
    AssertSignalsRequireCurrentActiveGenerations();
    AssertResultValidationFailsClosed();
    AssertActiveSignalKeyConstructionFailsClosed();
    AssertLifecycleReconciliationRetainsOnlyActiveSignals();
    AssertExceptionsAndLifecycleClearSignals();
    AssertEventsAreStableDeduplicatedAndBounded();

    Console.WriteLine(
        "PASS: ServeInWork mission diagnostics gate signals by current mission/business generations, "
        + "fail closed on identity/definition/native failures, and publish bounded managed snapshots.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL: {ex}");
    return 1;
}

static void AssertSnapshotIsManagedAndSerializable()
{
    var allowedScalarTypes = new HashSet<Type>
    {
        typeof(string),
        typeof(bool),
        typeof(int),
        typeof(long),
        typeof(DateTime),
        typeof(IReadOnlyList<RuntimeServeInWorkMissionSignal>),
        typeof(IReadOnlyList<RuntimeServeInWorkMissionDiagnosticEvent>),
    };
    foreach (var property in typeof(RuntimeServeInWorkMissionDiagnosticSnapshot)
                 .GetProperties()
                 .Where(property => property.GetMethod?.IsStatic == false))
    {
        AssertTrue(
            allowedScalarTypes.Contains(property.PropertyType),
            $"Snapshot property {property.Name} exposed unsupported state {property.PropertyType}.");
    }

    var state = CreateActiveState();
    AssertTrue(
        ObserveMatched(state, missionGeneration: 1, businessGeneration: 1, rawId: 1100, canonicalId: 10, foodId: 37),
        "Serializable snapshot setup did not establish a signal.");
    var json = JsonSerializer.Serialize(state.Snapshot());
    AssertContains(json, "\"CanonicalGuestId\":10", "Snapshot JSON omitted its managed signal.");
    AssertContains(json, "\"Events\"", "Snapshot JSON omitted bounded diagnostic events.");
}

static void AssertSignalsRequireCurrentActiveGenerations()
{
    var state = new RuntimeServeInWorkMissionDiagnosticState();
    state.SetHookStatus("patched:1/1", attached: true, Utc(0));
    AssertTrue(state.ResetForMissionGeneration(1, Utc(1)), "Mission generation was not established.");

    AssertFalse(
        ObserveMatched(state, 1, 0, 3, 3, 1),
        "A signal was accepted outside an active business generation.");
    AssertEqual(0, state.Snapshot().Signals.Count, "An inactive observation leaked a signal.");

    AssertTrue(
        state.ApplyBusinessBoundary(1, "active", Utc(3)),
        "The first business generation was not activated.");
    AssertTrue(
        ObserveMatched(state, 1, 1, 3, 3, 1),
        "A current active observation did not establish a signal.");
    var active = state.Snapshot();
    AssertEqual("Active", active.NightPhase, "The active phase was not normalized.");
    AssertEqual(1, active.Signals.Count, "The active observation did not publish exactly one signal.");

    AssertFalse(
        ObserveMatched(state, 0, 1, 3, 3, 2),
        "A stale mission callback replaced the current signal.");
    AssertFalse(
        ObserveMatched(state, 1, 0, 3, 3, 2),
        "A stale business callback replaced the current signal.");
    AssertEqual(1, state.Snapshot().Signals.Single().FoodId, "A stale callback mutated the current signal.");
    AssertEqual(2L, state.Snapshot().LateObservationCount, "Stale callbacks were not counted.");

    AssertTrue(state.ApplyBusinessBoundary(1, "Closing", Utc(6)), "Closing was rejected.");
    AssertEqual(0, state.Snapshot().Signals.Count, "Closing did not clear current signals.");
    AssertFalse(
        state.ApplyBusinessBoundary(1, "Active", Utc(7)),
        "The same generation regressed from Closing to Active.");
    AssertTrue(state.ApplyBusinessBoundary(1, "Destroyed", Utc(8)), "Destroyed was rejected.");
    AssertEqual(0, state.Snapshot().Signals.Count, "Destroyed retained a signal.");

    AssertTrue(state.ApplyBusinessBoundary(2, "Active", Utc(9)), "A later business generation did not activate.");
    AssertTrue(
        ObserveMatched(state, 1, 2, 3, 3, 4),
        "A current callback in the later business generation was rejected.");
    AssertTrue(state.ResetForMissionGeneration(2, Utc(10)), "A later mission generation did not reset.");
    AssertEqual(0, state.Snapshot().Signals.Count, "Mission generation reset retained an old signal.");
    AssertFalse(
        ObserveMatched(state, 1, 2, 3, 3, 5),
        "A callback from the previous mission generation was accepted.");
}

static void AssertResultValidationFailsClosed()
{
    var state = CreateActiveState();
    AssertTrue(
        ObserveMatched(state, 1, 1, 28, 28, 37),
        "Valid baseline signal was rejected.");

    AssertTrue(
        state.ObserveResult(
            1,
            1,
            rawGuestId: 28,
            canonicalGuestId: 28,
            foodId: -1,
            result: false,
            RuntimeServeInWorkMissionDefinitionStatus.Pending,
            expectedFoodId: null,
            Utc(2)),
        "A current false result was not observed.");
    AssertEqual(0, state.Snapshot().Signals.Count, "A false result did not clear its guest.");

    AssertFalse(
        state.ObserveResult(
            1,
            1,
            rawGuestId: 1100,
            canonicalGuestId: null,
            foodId: 37,
            result: true,
            RuntimeServeInWorkMissionDefinitionStatus.Matched,
            expectedFoodId: 37,
            Utc(3)),
        "A true result without canonical identity established a signal.");
    AssertEqual(1L, state.Snapshot().IdentityMissingCount, "Missing identity was not counted.");

    AssertFalse(
        state.ObserveResult(
            1,
            1,
            rawGuestId: 28,
            canonicalGuestId: 28,
            foodId: -1,
            result: true,
            RuntimeServeInWorkMissionDefinitionStatus.Matched,
            expectedFoodId: -1,
            Utc(4)),
        "A negative food ID established a signal.");
    AssertEqual(1L, state.Snapshot().InvalidFoodCount, "Invalid food ID was not counted.");

    AssertFalse(
        state.ObserveResult(
            1,
            1,
            rawGuestId: 28,
            canonicalGuestId: 28,
            foodId: 37,
            result: true,
            RuntimeServeInWorkMissionDefinitionStatus.Pending,
            expectedFoodId: null,
            Utc(5)),
        "A pending definition established an actionable signal.");
    AssertEqual(1L, state.Snapshot().DefinitionPendingCount, "Pending definition was not counted.");

    AssertFalse(
        state.ObserveResult(
            1,
            1,
            rawGuestId: 28,
            canonicalGuestId: 28,
            foodId: 37,
            result: true,
            RuntimeServeInWorkMissionDefinitionStatus.Mismatch,
            expectedFoodId: 31,
            Utc(6)),
        "An explicit definition mismatch established a signal.");
    AssertFalse(
        state.ObserveResult(
            1,
            1,
            rawGuestId: 28,
            canonicalGuestId: 28,
            foodId: 37,
            result: true,
            RuntimeServeInWorkMissionDefinitionStatus.Matched,
            expectedFoodId: 31,
            Utc(7)),
        "A mismatched expected food ID established a signal.");
    AssertEqual(2L, state.Snapshot().DefinitionMismatchCount, "Definition mismatches were not counted.");

    var snapshot = state.Snapshot();
    AssertEqual(7L, snapshot.CallCount, "Current native calls were not counted.");
    AssertEqual(6L, snapshot.TrueResultCount, "True results were not counted.");
    AssertEqual(1L, snapshot.FalseResultCount, "False results were not counted.");
}

static void AssertExceptionsAndLifecycleClearSignals()
{
    var state = CreateActiveState();
    AssertTrue(ObserveMatched(state, 1, 1, 3, 3, 1), "Exception test setup failed.");
    AssertTrue(
        state.ObserveNativeException(
            1,
            1,
            rawGuestId: 3,
            canonicalGuestId: 3,
            exceptionType: "InvalidOperationException",
            Utc(2)),
        "A current native exception was not observed.");
    var failed = state.Snapshot();
    AssertEqual(0, failed.Signals.Count, "A native exception retained the affected signal.");
    AssertEqual(1L, failed.NativeExceptionCount, "A native exception was not counted.");
    AssertEqual("InvalidOperationException", failed.LastError, "Native exception identity was lost.");

    AssertTrue(ObserveMatched(state, 1, 1, 3, 3, 1), "Identity clear setup failed.");
    AssertTrue(
        state.ClearForMissionIdentity(1, rawGuestId: 3, canonicalGuestId: 3, Utc(4)),
        "Mission identity clear did not remove its signal.");
    AssertEqual(0, state.Snapshot().Signals.Count, "Mission identity clear retained its signal.");

    AssertTrue(ObserveMatched(state, 1, 1, 3, 3, 1), "Lifecycle clear setup A failed.");
    AssertTrue(ObserveMatched(state, 1, 1, 28, 28, 37), "Lifecycle clear setup B failed.");
    AssertTrue(state.ClearForMissionLifecycle(1, Utc(6)), "Current mission lifecycle clear was rejected.");
    AssertEqual(0, state.Snapshot().Signals.Count, "Mission lifecycle clear retained signals.");
    var cleared = state.Snapshot();
    AssertTrue(
        state.ClearForMissionLifecycle(1, Utc(7)),
        "An unchanged current-generation lifecycle clear was rejected.");
    var unchanged = state.Snapshot();
    AssertEqual(
        cleared.ChangeVersion,
        unchanged.ChangeVersion,
        "An unchanged lifecycle clear published a redundant state change.");
    AssertEqual(
        cleared.Events.Count,
        unchanged.Events.Count,
        "An unchanged lifecycle clear recorded a duplicate diagnostic event.");

    AssertTrue(ObserveMatched(state, 1, 1, 3, 3, 1), "Stale clear setup failed.");
    AssertFalse(
        state.ClearForMissionLifecycle(0, Utc(8)),
        "A stale mission lifecycle callback was accepted.");
    AssertEqual(1, state.Snapshot().Signals.Count, "A stale lifecycle callback cleared a current signal.");

    state.SetHookStatus("unavailable", attached: false, Utc(9));
    AssertEqual(0, state.Snapshot().Signals.Count, "Hook detachment retained current signals.");
    AssertFalse(
        ObserveMatched(state, 1, 1, 3, 3, 1),
        "A detached hook state established a signal.");
}

static void AssertLifecycleReconciliationRetainsOnlyActiveSignals()
{
    var state = CreateActiveState();
    AssertTrue(
        ObserveMatched(state, 1, 1, rawId: 1003, canonicalId: 3, foodId: 1),
        "Reconciliation setup A failed.");
    AssertTrue(
        ObserveMatched(state, 1, 1, rawId: 1028, canonicalId: 28, foodId: 37),
        "Reconciliation setup B failed.");

    AssertTrue(
        state.ReconcileForMissionLifecycle(
            1,
            new[]
            {
                new RuntimeServeInWorkMissionSignalKey(3, 1),
                new RuntimeServeInWorkMissionSignalKey(99, 50),
            },
            Utc(2)),
        "Current lifecycle reconciliation was rejected.");
    var reconciled = state.Snapshot();
    AssertEqual(1, reconciled.Signals.Count, "Reconciliation did not remove only the stale signal.");
    AssertEqual(3, reconciled.Signals.Single().CanonicalGuestId, "Reconciliation removed the active signal.");
    AssertEqual(
        "mission-lifecycle-reconciled",
        reconciled.LastEvent,
        "Reconciliation did not publish its precise lifecycle event.");

    AssertTrue(
        state.ReconcileForMissionLifecycle(
            1,
            new[]
            {
                new RuntimeServeInWorkMissionSignalKey(3, 1),
            },
            Utc(3)),
        "An unchanged lifecycle reconciliation was rejected.");
    var unchanged = state.Snapshot();
    AssertEqual(
        reconciled.ChangeVersion,
        unchanged.ChangeVersion,
        "An unchanged reconciliation published a redundant state change.");
    AssertEqual(
        reconciled.Events.Count,
        unchanged.Events.Count,
        "An unchanged reconciliation recorded a redundant diagnostic event.");

    AssertFalse(
        state.ReconcileForMissionLifecycle(
            0,
            Array.Empty<RuntimeServeInWorkMissionSignalKey>(),
            Utc(4)),
        "A stale reconciliation generation was accepted.");
    AssertEqual(
        1,
        state.Snapshot().Signals.Count,
        "A stale reconciliation removed a current signal.");

    AssertTrue(
        state.ReconcileForMissionLifecycle(
            1,
            Array.Empty<RuntimeServeInWorkMissionSignalKey>(),
            Utc(5)),
        "An empty active definition set was rejected.");
    AssertEqual(
        0,
        state.Snapshot().Signals.Count,
        "A fulfilled or removed task retained its ServeInWork signal.");
}

static void AssertActiveSignalKeyConstructionFailsClosed()
{
    var canonicalIds = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["Meirin"] = 15,
        ["Akyuu"] = 3,
    };
    int? Resolve(string receiver)
    {
        return canonicalIds.TryGetValue(receiver, out var canonicalId)
            ? canonicalId
            : null;
    }

    AssertTrue(
        RuntimeServeInWorkMissionSignalReconciler.TryBuildActiveSignalKeys(
            new[]
            {
                new RuntimeServeInWorkMissionReconcileDefinition(
                    "Meirin",
                    new[] { 50 },
                    Fulfilled: false),
                new RuntimeServeInWorkMissionReconcileDefinition(
                    "Akyuu",
                    new[] { 1 },
                    Fulfilled: true),
            },
            Resolve,
            out var activeSignals),
        "A complete active definition set was rejected.");
    AssertEqual(1, activeSignals.Count, "Fulfilled definitions were not excluded.");
    AssertEqual(
        new RuntimeServeInWorkMissionSignalKey(15, 50),
        activeSignals.Single(),
        "The active definition did not produce its exact canonical/food key.");

    AssertFalse(
        RuntimeServeInWorkMissionSignalReconciler.TryBuildActiveSignalKeys(
            new[]
            {
                new RuntimeServeInWorkMissionReconcileDefinition(
                    "Unknown",
                    new[] { 50 },
                    Fulfilled: false),
            },
            Resolve,
            out _),
        "An unresolved receiver produced an active key.");
    AssertFalse(
        RuntimeServeInWorkMissionSignalReconciler.TryBuildActiveSignalKeys(
            new[]
            {
                new RuntimeServeInWorkMissionReconcileDefinition(
                    "Meirin",
                    new[] { -1 },
                    Fulfilled: false),
            },
            Resolve,
            out _),
        "A negative task food ID produced an active key.");
}

static void AssertEventsAreStableDeduplicatedAndBounded()
{
    var state = CreateActiveState();
    for (var index = 0; index < 4; index++)
    {
        AssertTrue(
            ObserveMatched(state, 1, 1, 3, 3, 1),
            "Repeated matching observation was rejected.");
    }

    var repeated = state.Snapshot();
    AssertEqual(4L, repeated.CallCount, "Deduplication incorrectly removed native call counts.");
    AssertEqual(
        1,
        repeated.Events.Count(entry => entry.Code == "signal-committed"),
        "Stable repeated events were not deduplicated.");

    for (var canonicalId = 100; canonicalId < 180; canonicalId++)
    {
        AssertTrue(
            ObserveMatched(state, 1, 1, canonicalId, canonicalId, canonicalId),
            $"Unique observation {canonicalId} was rejected.");
    }

    var bounded = state.Snapshot();
    AssertEqual(64, bounded.Events.Count, "Diagnostic event retention was not bounded to 64.");
    AssertEqual(
        bounded.Events.Count,
        bounded.Events.Select(entry => entry.Signature).Distinct(StringComparer.Ordinal).Count(),
        "Bounded events retained duplicate stable signatures.");
    AssertTrue(
        bounded.Events.Zip(bounded.Events.Skip(1), (left, right) => left.Sequence < right.Sequence).All(value => value),
        "Bounded events lost sequence ordering.");
}

static RuntimeServeInWorkMissionDiagnosticState CreateActiveState()
{
    var state = new RuntimeServeInWorkMissionDiagnosticState();
    state.SetHookStatus("patched:1/1", attached: true, Utc(0));
    AssertTrue(state.ResetForMissionGeneration(1, Utc(0)), "Test mission generation setup failed.");
    AssertTrue(state.ApplyBusinessBoundary(1, "Active", Utc(0)), "Test business generation setup failed.");
    return state;
}

static bool ObserveMatched(
    RuntimeServeInWorkMissionDiagnosticState state,
    long missionGeneration,
    long businessGeneration,
    int rawId,
    int canonicalId,
    int foodId)
{
    return state.ObserveResult(
        missionGeneration,
        businessGeneration,
        rawId,
        canonicalId,
        foodId,
        result: true,
        RuntimeServeInWorkMissionDefinitionStatus.Matched,
        expectedFoodId: foodId,
        Utc(1));
}

static DateTime Utc(int second)
{
    return new DateTime(2026, 7, 28, 0, 0, second, DateTimeKind.Utc);
}

static void AssertTrue(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void AssertFalse(bool condition, string message)
{
    AssertTrue(!condition, message);
}

static void AssertEqual<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{message} Expected={expected}; Actual={actual}.");
    }
}

static void AssertContains(string text, string expected, string message)
{
    if (!text.Contains(expected, StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"{message} Missing={expected}; Actual={text}.");
    }
}
