using MystiaStewardCompanion.Save;

try
{
    AssertSnapshotContractIsManaged();
    AssertLoadTokenAndMetrics();
    AssertNonEmptyLoadSeedCommitsAtomically();
    AssertFreshnessRequiresVerifiedStateAndExactDefinition();
    AssertServeDefinitionsRequireCompleteActiveSet();
    AssertInitializedBindingAndPointerReuse();
    AssertFinishedSetGate();
    AssertGenerationAndThreadGates();

    Console.WriteLine(
        "PASS: runtime mission diagnostics atomically seed non-empty saves, "
        + "commit controlled initial refreshes before later natural updates, "
        + "and enforce label, pointer, finished-multiset, generation, and thread gates.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL: {ex}");
    return 1;
}

static void AssertSnapshotContractIsManaged()
{
    var allowedTypes = new HashSet<Type>
    {
        typeof(string),
        typeof(bool),
        typeof(long),
        typeof(int),
        typeof(DateTime),
        typeof(RuntimeMissionDiagnosticPhase),
    };
    foreach (var property in typeof(RuntimeMissionDiagnosticSnapshot)
                 .GetProperties()
                 .Where(property => property.GetMethod?.IsStatic == false))
    {
        AssertTrue(
            allowedTypes.Contains(property.PropertyType),
            $"Snapshot property {property.Name} exposed native or complex state {property.PropertyType}.");
    }
}

static void AssertLoadTokenAndMetrics()
{
    var state = CreateAttachedState();
    var load = state.BeginLoadCapture(threadId: 7, Utc(1));
    AssertEqual(1L, load.Generation, "The first load did not establish generation 1.");
    AssertEqual(
        RuntimeMissionDiagnosticPhase.CapturingLoadSeed,
        state.Snapshot().Phase,
        "Load capture has the wrong initial phase.");

    var metrics = Metrics(
        jsonLength: 1234,
        parsedTrackingMissionCount: 14,
        parsedFinishedMissionCount: 98,
        parsedDlcPartitionCount: 5);
    AssertTrue(
        state.TryMarkLoadSeedReady(load, metrics, Utc(2)),
        "A matching load seed was rejected.");
    var ready = state.Snapshot();
    AssertEqual(RuntimeMissionDiagnosticPhase.LoadSeedReady, ready.Phase, "Seed-ready phase was not published.");
    AssertEqual(1234, ready.LoadJsonLength, "JSON length metric was lost.");
    AssertEqual(Sha256(), ready.LoadJsonSha256, "JSON hash metric was lost.");
    AssertEqual(12L, ready.SerializeElapsedMilliseconds, "Serialization timing was lost.");
    AssertEqual(4L, ready.ParseElapsedMilliseconds, "Parse timing was lost.");
    AssertEqual(14, ready.ParsedTrackingMissionCount, "Parsed mission count was lost.");
    AssertEqual(98, ready.ParsedFinishedMissionCount, "Parsed finished count was lost.");
    AssertFalse(
        state.TryBeginInitialization(load, threadId: 8, Utc(3), out _),
        "Initialization accepted a different thread.");
    AssertEqual(
        RuntimeMissionDiagnosticPhase.LoadSeedReady,
        state.Snapshot().Phase,
        "A rejected initialization changed the current load.");

    var replacement = state.BeginLoadCapture(threadId: 7, Utc(4));
    AssertEqual(2L, replacement.Generation, "A replacement load did not advance generation.");
    AssertFalse(
        state.TryMarkLoadSeedReady(load, metrics, Utc(5)),
        "A stale load token modified the replacement generation.");
    AssertEqual(
        RuntimeMissionDiagnosticPhase.CapturingLoadSeed,
        state.Snapshot().Phase,
        "A stale load token changed the replacement phase.");
}

static void AssertNonEmptyLoadSeedCommitsAtomically()
{
    var state = CreateAttachedState();
    var initialization = BeginInitialization(
        state,
        threadId: 11,
        parsedTrackingMissionCount: 2,
        parsedFinishedMissionCount: 2);
    var tasks = new[]
    {
        Loaded(
            label: "mission-a",
            sourcePartition: "CORE",
            sourceIsCore: true,
            sourceBucket: 0,
            mergedBucket: 0,
            sourceOrdinal: 0,
            savedFinishStateCount: 2,
            savedTrueFinishStateCount: 1,
            refreshedFinishStates: new[] { true, false },
            definition: Definition("mission-a", conditionCount: 2)),
        Loaded(
            label: "mission-b",
            sourcePartition: "DLC1",
            sourceIsCore: false,
            sourceBucket: 0,
            mergedBucket: 0,
            sourceOrdinal: 0,
            savedFinishStateCount: 1,
            savedTrueFinishStateCount: 1,
            refreshedFinishStates: new[] { true },
            definition: Definition("mission-b", conditionCount: 1)),
    };
    var invalid = Seed(
        initialization,
        runtimeTrackingBucketCount: 2,
        seedTrackingBucketCount: 2,
        tasks,
        seedFinished: new[] { "finished-a", "finished-a", "finished-b" },
        runtimeFinished: new[] { "finished-a", "finished-b" });
    AssertFalse(
        state.TryCommitInitialization(initialization, invalid, Utc(3), out var unavailable),
        "A runtime finished multiset with one missing occurrence was accepted.");
    AssertEqual(RuntimeMissionDiagnosticPhase.Unavailable, unavailable.Phase, "Invalid seed did not fail closed.");
    AssertEqual(
        "finished-mission-multiset-mismatch",
        unavailable.LastError,
        "Finished multiplicity mismatch was imprecise.");
    AssertEqual(0, unavailable.ActiveMissionCount, "A rejected seed leaked active tasks.");
    AssertEqual(0, unavailable.FinishedUniqueMissionCount, "A rejected seed leaked finished labels.");
    AssertEqual(0, state.Report().Tasks.Count, "A rejected seed leaked task details.");

    initialization = BeginInitialization(
        state,
        threadId: 11,
        parsedTrackingMissionCount: 2,
        parsedFinishedMissionCount: 2);
    var valid = Seed(
        initialization,
        runtimeTrackingBucketCount: 2,
        seedTrackingBucketCount: 2,
        tasks,
        seedFinished: new[] { "finished-a", "finished-a", "finished-b" },
        runtimeFinished: new[] { "finished-b", "finished-a", "finished-a" });
    AssertTrue(
        state.TryCommitInitialization(initialization, valid, Utc(6), out var committed),
        "A complete non-empty load seed was rejected.");
    AssertTrue(committed.RuntimeAvailable, "A committed load seed was not made available.");
    AssertEqual(RuntimeMissionDiagnosticPhase.Ready, committed.Phase, "Committed seed has the wrong phase.");
    AssertEqual(2, committed.TrackingBucketCount, "Runtime tracking bucket count was lost.");
    AssertEqual(2, committed.SeedTrackingMissionCount, "Loaded seed task count was lost.");
    AssertEqual(2, committed.ActiveMissionCount, "Loaded tasks were not active.");
    AssertEqual(0, committed.UnverifiedMissionCount, "Refreshed loaded tasks remained unverified.");
    AssertEqual(1, committed.TrackingMissionCount, "Initial tracking state count mismatch.");
    AssertEqual(1, committed.FulfilledMissionCount, "Initial fulfilled state count mismatch.");
    AssertEqual(
        2L,
        committed.StateRefreshCount,
        "Controlled initialization refreshes were not counted.");
    AssertEqual(
        2,
        committed.FinishedUniqueMissionCount,
        "Accepted finished multiplicity changed the unique diagnostic label count.");

    var report = state.Report();
    AssertEqual(2, report.Tasks.Count, "Loaded task details were not published.");
    var missionA = report.Tasks.Single(task => task.Label == "mission-a");
    AssertEqual(RuntimeMissionDiagnosticFreshness.Tracking, missionA.Freshness,
        "Initial controlled refresh did not publish tracking state.");
    AssertTrue(missionA.NativeIdentityBound, "Initial controlled refresh did not bind mission-a.");
    AssertEqual(2, missionA.CurrentFinishStateCount, "mission-a current state count mismatch.");
    AssertEqual<bool?>(false, missionA.Fulfilled, "mission-a fulfillment mismatch.");
    var missionB = report.Tasks.Single(task => task.Label == "mission-b");
    AssertEqual(RuntimeMissionDiagnosticFreshness.Fulfilled, missionB.Freshness,
        "Initial controlled refresh did not publish fulfilled state.");
    AssertTrue(missionB.NativeIdentityBound, "Initial controlled refresh did not bind mission-b.");
    AssertEqual(1, missionB.CurrentFinishStateCount, "mission-b current state count mismatch.");
    AssertEqual<bool?>(true, missionB.Fulfilled, "mission-b fulfillment mismatch.");
}

static void AssertServeDefinitionsRequireCompleteActiveSet()
{
    var validServeTask = Loaded(
        label: "mission-serve",
        sourceOrdinal: 0,
        refreshedFinishStates: new[] { false },
        definition: Definition(
            "mission-serve",
            conditionCount: 1,
            receiver: "Meirin",
            serveInWorkFoodIds: 50));
    var missingDefinitionTask = Loaded(
        label: "mission-definition-missing",
        sourceOrdinal: 1,
        refreshedFinishStates: new[] { false },
        definition: new RuntimeMissionDefinitionDiagnosticReadResult(
            Success: false,
            Definition: null,
            Failure: "definition-missing"));
    var incomplete = CreateReadyState(
        threadId: 31,
        validServeTask,
        missingDefinitionTask);
    AssertEqual(
        1,
        incomplete.Snapshot().DefinitionFailureCount,
        "Incomplete definition setup did not publish its failure count.");
    AssertFalse(
        incomplete.TryGetServeInWorkDefinitions(
            incomplete.Snapshot().Generation,
            out var partialDefinitions),
        "A partial active definition set was exposed as complete ServeInWork evidence.");
    AssertEqual(
        0,
        partialDefinitions.Count,
        "A failed ServeInWork definition read leaked a partial result.");

    var invalidReceiverTask = Loaded(
        label: "mission-invalid-receiver",
        sourceOrdinal: 1,
        refreshedFinishStates: new[] { false },
        definition: new RuntimeMissionDefinitionDiagnosticReadResult(
            Success: true,
            new RuntimeMissionDefinitionDiagnostic(
                Label: "mission-invalid-receiver",
                Title: "Invalid receiver",
                TitleStatus: "available",
                HasReceiver: false,
                Receiver: "",
                ConditionCount: 1,
                Conditions: new[]
                {
                    new RuntimeMissionDefinitionDiagnosticCondition(
                        Type: 4,
                        Amount: 50),
                },
                ServeInWorkFoodIds: new[] { 50 }),
            Failure: ""));
    var invalidReceiver = CreateReadyState(
        threadId: 32,
        validServeTask,
        invalidReceiverTask);
    AssertFalse(
        invalidReceiver.TryGetServeInWorkDefinitions(
            invalidReceiver.Snapshot().Generation,
            out var invalidReceiverDefinitions),
        "A ServeInWork definition without a valid receiver was silently skipped.");
    AssertEqual(
        0,
        invalidReceiverDefinitions.Count,
        "An invalid ServeInWork receiver leaked a partial definition set.");

    var complete = CreateReadyState(threadId: 33, validServeTask);
    AssertTrue(
        complete.TryGetServeInWorkDefinitions(
            complete.Snapshot().Generation,
            out var completeDefinitions),
        "A complete active definition set was rejected.");
    AssertEqual(
        1,
        completeDefinitions.Count,
        "The complete ServeInWork definition set changed size.");
}

static void AssertFreshnessRequiresVerifiedStateAndExactDefinition()
{
    var state = CreateReadyState(
        threadId: 13,
        Loaded(
            label: "mission-two",
            savedFinishStateCount: 2,
            savedTrueFinishStateCount: 2,
            identity: (nint)101,
            definition: Definition("mission-two", conditionCount: 2)),
        Loaded(
            label: "mission-mismatch",
            sourceOrdinal: 1,
            savedFinishStateCount: 1,
            savedTrueFinishStateCount: 0,
            identity: (nint)102,
            definition: Definition("mission-mismatch", conditionCount: 1)));
    var generation = state.Snapshot().Generation;

    AssertTrue(
        state.TryObserveStateRefresh(
            generation,
            13,
            Tracked((nint)101, "mission-two", false, true),
            Utc(10)),
        "An exact natural refresh was rejected.");
    AssertTaskFreshness(
        state,
        "mission-two",
        RuntimeMissionDiagnosticFreshness.Tracking,
        fulfilled: false);

    AssertTrue(
        state.TryObserveStateRefresh(
            generation,
            13,
            Tracked((nint)101, "mission-two", true, true),
            Utc(11)),
        "A fulfilled exact natural refresh was rejected.");
    AssertTaskFreshness(
        state,
        "mission-two",
        RuntimeMissionDiagnosticFreshness.Fulfilled,
        fulfilled: true);

    AssertFalse(
        state.TryObserveStateRefresh(
            generation,
            13,
            Tracked((nint)102, "mission-mismatch", true, true),
            Utc(12)),
        "A refresh whose flag count disagrees with the definition was accepted as fresh.");
    var mismatch = FindTask(state, "mission-mismatch");
    AssertEqual(
        RuntimeMissionDiagnosticFreshness.Unverified,
        mismatch.Freshness,
        "A condition-count mismatch did not remain Unverified.");
    AssertEqual<bool?>(null, mismatch.Fulfilled, "A condition-count mismatch published fulfillment.");
    AssertEqual(
        "refreshed-condition-count-mismatch",
        mismatch.ValidationError,
        "A condition-count mismatch reported the wrong diagnostic.");
    AssertTrue(state.Snapshot().RuntimeAvailable, "A per-task freshness mismatch invalidated structural capture.");

    var emptyState = CreateReadyState(
        threadId: 15,
        Loaded(
            label: "mission-empty",
            savedFinishStateCount: 0,
            identity: (nint)201,
            definition: Definition("mission-empty", conditionCount: 0)));
    var emptyGeneration = emptyState.Snapshot().Generation;
    AssertTaskFreshness(
        emptyState,
        "mission-empty",
        RuntimeMissionDiagnosticFreshness.Fulfilled,
        fulfilled: true);
    AssertTrue(
        emptyState.TryObserveStateRefresh(
            emptyGeneration,
            15,
            Tracked((nint)201, "mission-empty"),
            Utc(13)),
        "A naturally refreshed zero-condition task was rejected.");
    AssertTaskFreshness(
        emptyState,
        "mission-empty",
        RuntimeMissionDiagnosticFreshness.Fulfilled,
        fulfilled: true);
}

static void AssertInitializedBindingAndPointerReuse()
{
    var state = CreateReadyState(
        threadId: 17,
        Loaded(
            label: "mission-loaded",
            savedFinishStateCount: 1,
            identity: (nint)301,
            definition: Definition("mission-loaded", conditionCount: 1)));
    var generation = state.Snapshot().Generation;
    AssertTrue(
        FindTask(state, "mission-loaded").NativeIdentityBound,
        "Controlled initialization did not bind the loaded task.");

    AssertTrue(
        state.TryObserveStateRefresh(
            generation,
            17,
            Tracked((nint)301, "mission-loaded", false),
            Utc(20)),
        "A loaded task rejected a later natural callback on the same pointer.");

    AssertTrue(
        state.TryObserveFinish(
            generation,
            17,
            Tracked((nint)301, "mission-loaded", true),
            Utc(21)),
        "A bound loaded task could not finish.");
    AssertFalse(FindTask(state, "mission-loaded").Active, "Finished task remained active.");

    AssertTrue(state.ObserveStartAttempt(generation, 17, Utc(22)), "Start attempt was not observed.");
    AssertTrue(
        state.TryCommitStartedMission(
            generation,
            17,
            Tracked((nint)301, "mission-reused", false),
            Definition("mission-reused", conditionCount: 1),
            stateVerified: false,
            Utc(23)),
        "An inactive native pointer could not be reused.");
    AssertTaskFreshness(
        state,
        "mission-reused",
        RuntimeMissionDiagnosticFreshness.Unverified,
        fulfilled: null);
    AssertFalse(
        FindTask(state, "mission-loaded").NativeIdentityBound,
        "Reusing an inactive pointer left the previous task identity bound.");
    AssertTrue(
        state.TryObserveStateRefresh(
            generation,
            17,
            Tracked((nint)301, "mission-reused", true),
            Utc(24)),
        "A reused pointer remained associated with the inactive task.");
    AssertTaskFreshness(
        state,
        "mission-reused",
        RuntimeMissionDiagnosticFreshness.Fulfilled,
        fulfilled: true);

    var verifiedStartState = CreateReadyState(threadId: 18);
    var verifiedStartGeneration = verifiedStartState.Snapshot().Generation;
    AssertTrue(
        verifiedStartState.ObserveStartAttempt(
            verifiedStartGeneration,
            18,
            Utc(24)),
        "Verified start attempt was not observed.");
    AssertTrue(
        verifiedStartState.TryCommitStartedMission(
            verifiedStartGeneration,
            18,
            Tracked((nint)351, "mission-started-verified", true),
            Definition("mission-started-verified", conditionCount: 1),
            stateVerified: true,
            Utc(25)),
        "A newly started task with a controlled refresh was rejected.");
    AssertEqual(
        1L,
        verifiedStartState.Snapshot().StateRefreshCount,
        "The controlled refresh for a newly started task was not counted.");
    AssertTaskFreshness(
        verifiedStartState,
        "mission-started-verified",
        RuntimeMissionDiagnosticFreshness.Fulfilled,
        fulfilled: true);

    var duplicateState = CreateReadyState(threadId: 19);
    var duplicateGeneration = duplicateState.Snapshot().Generation;
    AssertTrue(
        duplicateState.TryCommitStartedMission(
            duplicateGeneration,
            19,
            Tracked((nint)401, "mission-active", false),
            Definition("mission-active", conditionCount: 1),
            stateVerified: false,
            Utc(25)),
        "Active pointer conflict setup failed.");
    AssertFalse(
        duplicateState.TryCommitStartedMission(
            duplicateGeneration,
            19,
            Tracked((nint)401, "mission-conflict", true),
            Definition("mission-conflict", conditionCount: 1),
            stateVerified: true,
            Utc(26)),
        "An active pointer was rebound to another label.");
    AssertEqual(
        "duplicate-active-mission-identity",
        duplicateState.Snapshot().LastError,
        "Active pointer conflict reported the wrong failure.");
    AssertFalse(duplicateState.Snapshot().RuntimeAvailable, "Active pointer conflict did not fail closed.");

    var loopState = CreateReadyState(threadId: 20);
    var loopGeneration = loopState.Snapshot().Generation;
    AssertTrue(
        loopState.TryCommitStartedMission(
            loopGeneration,
            20,
            Tracked((nint)501, "mission-loop", false),
            Definition("mission-loop", conditionCount: 1),
            stateVerified: false,
            Utc(27)),
        "Loop mission setup failed.");
    AssertTrue(
        loopState.TryObserveFinish(
            loopGeneration,
            20,
            Tracked((nint)501, "mission-loop", true),
            Utc(28)),
        "The first loop mission instance could not finish.");
    AssertTrue(
        loopState.TryCommitStartedMission(
            loopGeneration,
            20,
            Tracked((nint)502, "mission-loop", false),
            Definition("mission-loop", conditionCount: 1),
            stateVerified: false,
            Utc(29)),
        "A loop mission label could not restart with a new pointer.");
    AssertTrue(
        loopState.TryObserveStateRefresh(
            loopGeneration,
            20,
            Tracked((nint)502, "mission-loop", true),
            Utc(30)),
        "The restarted loop mission did not retain its new pointer.");
    AssertFalse(
        loopState.TryObserveStateRefresh(
            loopGeneration,
            20,
            Tracked((nint)501, "mission-loop", false),
            Utc(31)),
        "A late callback from the previous loop mission instance was rebound.");
    AssertTaskFreshness(
        loopState,
        "mission-loop",
        RuntimeMissionDiagnosticFreshness.Fulfilled,
        fulfilled: true);
    AssertFalse(
        loopState.Snapshot().RuntimeAvailable,
        "A late callback from an obsolete loop mission pointer did not fail closed.");
}

static void AssertFinishedSetGate()
{
    var state = CreateReadyState(
        threadId: 21,
        Loaded(
            label: "mission-finish-by-label",
            savedFinishStateCount: 1,
            definition: Definition("mission-finish-by-label", conditionCount: 1)));
    var generation = state.Snapshot().Generation;
    AssertTrue(
        state.TryObserveFinishedLabel(
            generation,
            21,
            "mission-finish-by-label",
            Utc(30)),
        "A native finished label was rejected.");
    AssertFalse(FindTask(state, "mission-finish-by-label").Active, "Finished label did not deactivate its task.");
    AssertEqual(1, state.Snapshot().FinishedUniqueMissionCount, "Finished label was not added to the set.");

    AssertTrue(
        state.TryObserveFinishNode(
            generation,
            21,
            new[] { "mission-tail-a", "mission-tail-b" },
            Utc(31)),
        "An append-only FinishNode suffix was rejected.");
    AssertEqual(3, state.Snapshot().FinishedUniqueMissionCount, "FinishNode suffix was not committed.");
    AssertEqual(1L, state.Snapshot().FinishNodeObservationCount, "FinishNode observation was not counted.");

    var invalidState = CreateReadyState(threadId: 23);
    var invalidGeneration = invalidState.Snapshot().Generation;
    AssertFalse(
        invalidState.TryObserveFinishNode(
            invalidGeneration,
            23,
            new[] { "" },
            Utc(32)),
        "An invalid FinishNode label was accepted.");
    AssertFalse(invalidState.Snapshot().RuntimeAvailable, "Invalid FinishNode data did not fail closed.");
    AssertEqual(0, invalidState.Snapshot().FinishedUniqueMissionCount, "Invalid FinishNode data was partially committed.");
}

static void AssertGenerationAndThreadGates()
{
    var state = CreateReadyState(threadId: 25);
    var snapshot = state.Snapshot();
    AssertFalse(
        state.ObserveStartAttempt(snapshot.Generation - 1, 25, Utc(40)),
        "A stale generation callback was accepted.");
    AssertTrue(state.Snapshot().RuntimeAvailable, "A stale callback invalidated the current generation.");

    AssertFalse(
        state.ObserveStartAttempt(snapshot.Generation, 26, Utc(41)),
        "A callback from another thread was accepted.");
    AssertFalse(state.Snapshot().RuntimeAvailable, "A thread mismatch did not fail closed.");
    AssertEqual(
        "lifecycle-thread-mismatch",
        state.Snapshot().LastError,
        "A thread mismatch reported the wrong failure.");

    var explicitFailureState = CreateReadyState(threadId: 27);
    var generation = explicitFailureState.Snapshot().Generation;
    explicitFailureState.FailCurrentGeneration(
        generation,
        threadId: 28,
        "native-FinishMission-exception",
        Utc(42));
    AssertFalse(
        explicitFailureState.Snapshot().RuntimeAvailable,
        "A cross-thread explicit failure was ignored.");
    AssertEqual(
        "lifecycle-thread-mismatch",
        explicitFailureState.Snapshot().LastError,
        "Cross-thread explicit failure bypassed the thread gate.");
}

static RuntimeMissionDiagnosticState CreateAttachedState()
{
    var state = new RuntimeMissionDiagnosticState();
    state.SetHookStatus("patched:9/9", attached: true, Utc(0));
    return state;
}

static RuntimeMissionDiagnosticInitializationToken BeginInitialization(
    RuntimeMissionDiagnosticState state,
    int threadId,
    int parsedTrackingMissionCount,
    int parsedFinishedMissionCount)
{
    var load = state.BeginLoadCapture(threadId, Utc(1));
    AssertTrue(
        state.TryMarkLoadSeedReady(
            load,
            Metrics(
                jsonLength: 2048,
                parsedTrackingMissionCount,
                parsedFinishedMissionCount,
                parsedDlcPartitionCount: 1),
            Utc(2)),
        "Test setup could not publish a load seed.");
    AssertTrue(
        state.TryBeginInitialization(load, threadId, Utc(3), out var initialization),
        "Test setup could not begin initialization.");
    return initialization;
}

static RuntimeMissionDiagnosticState CreateReadyState(
    int threadId,
    params RuntimeMissionDiagnosticLoadedSeed[] tasks)
{
    var state = CreateAttachedState();
    var initialization = BeginInitialization(
        state,
        threadId,
        parsedTrackingMissionCount: tasks.Length,
        parsedFinishedMissionCount: 0);
    var bucketCount = tasks.Length == 0
        ? 0
        : tasks.Select(task => task.MergedBucket).Distinct().Count();
    var seed = Seed(
        initialization,
        runtimeTrackingBucketCount: bucketCount,
        seedTrackingBucketCount: bucketCount,
        tasks,
        seedFinished: Array.Empty<string>(),
        runtimeFinished: Array.Empty<string>());
    AssertTrue(
        state.TryCommitInitialization(initialization, seed, Utc(4), out _),
        "Test setup could not commit a load generation.");
    return state;
}

static RuntimeMissionDiagnosticLoadMetrics Metrics(
    int jsonLength,
    int parsedTrackingMissionCount,
    int parsedFinishedMissionCount,
    int parsedDlcPartitionCount)
{
    return new RuntimeMissionDiagnosticLoadMetrics(
        jsonLength,
        Sha256(),
        SerializeElapsedMilliseconds: 12,
        ParseElapsedMilliseconds: 4,
        FileVersion: "1.4.3",
        SavedGameDay: 55,
        parsedTrackingMissionCount,
        parsedFinishedMissionCount,
        parsedDlcPartitionCount);
}

static RuntimeMissionDiagnosticInitializationSeed Seed(
    RuntimeMissionDiagnosticInitializationToken token,
    int runtimeTrackingBucketCount,
    int seedTrackingBucketCount,
    IReadOnlyList<RuntimeMissionDiagnosticLoadedSeed> tasks,
    IReadOnlyList<string> seedFinished,
    IReadOnlyList<string> runtimeFinished)
{
    return new RuntimeMissionDiagnosticInitializationSeed(
        token.Generation,
        token.ThreadId,
        runtimeTrackingBucketCount,
        seedTrackingBucketCount,
        TrackingBufferCount: 0,
        CurrentDate: 55,
        SelectedDlcPartitions: new[] { "DLC1" },
        TrackedMissions: tasks,
        SeedFinishedMissionLabels: seedFinished,
        RuntimeFinishedMissionLabels: runtimeFinished,
        DefinitionReadElapsedMilliseconds: 3);
}

static RuntimeMissionDiagnosticLoadedSeed Loaded(
    string label,
    string sourcePartition = "CORE",
    bool sourceIsCore = true,
    int sourceBucket = 0,
    int mergedBucket = 0,
    int sourceOrdinal = 0,
    int savedFinishStateCount = 1,
    int savedTrueFinishStateCount = 0,
    int conditionDataCount = 0,
    IReadOnlyList<bool>? refreshedFinishStates = null,
    nint? identity = null,
    RuntimeMissionDefinitionDiagnosticReadResult? definition = null)
{
    var resolvedDefinition = definition ?? Definition(label, savedFinishStateCount);
    var conditionCount = resolvedDefinition.Definition?.ConditionCount
        ?? savedFinishStateCount;
    return new RuntimeMissionDiagnosticLoadedSeed(
        sourcePartition,
        sourceIsCore,
        sourceBucket,
        mergedBucket,
        sourceOrdinal,
        label,
        savedFinishStateCount,
        savedTrueFinishStateCount,
        conditionDataCount,
        new RuntimeMissionDiagnosticTrackedSeed(
            identity ?? IdentityFor(label),
            label,
            refreshedFinishStates?.ToArray() ?? new bool[conditionCount]),
        resolvedDefinition);
}

static nint IdentityFor(string label)
{
    ulong value = 14695981039346656037;
    foreach (var character in label)
    {
        value ^= character;
        value *= 1099511628211;
    }

    return (nint)(value == 0 ? 1 : unchecked((long)value));
}

static RuntimeMissionDefinitionDiagnosticReadResult Definition(
    string label,
    int conditionCount,
    string receiver = "",
    params int[] serveInWorkFoodIds)
{
    var conditions = Enumerable.Range(0, conditionCount)
        .Select(index => new RuntimeMissionDefinitionDiagnosticCondition(
            serveInWorkFoodIds.ElementAtOrDefault(index) > 0 ? 4 : 0,
            serveInWorkFoodIds.ElementAtOrDefault(index)))
        .ToArray();
    return new RuntimeMissionDefinitionDiagnosticReadResult(
        Success: true,
        new RuntimeMissionDefinitionDiagnostic(
            label,
            Title: $"Title {label}",
            TitleStatus: "available",
            HasReceiver: receiver.Length > 0,
            receiver,
            conditionCount,
            conditions,
            serveInWorkFoodIds.ToArray()),
        Failure: "");
}

static RuntimeMissionDiagnosticTrackedSeed Tracked(
    nint identity,
    string label,
    params bool[] finishStates)
{
    return new RuntimeMissionDiagnosticTrackedSeed(identity, label, finishStates);
}

static RuntimeMissionDiagnosticTaskSnapshot FindTask(
    RuntimeMissionDiagnosticState state,
    string label)
{
    return state.Report().Tasks.Single(task =>
        string.Equals(task.Label, label, StringComparison.Ordinal));
}

static void AssertTaskFreshness(
    RuntimeMissionDiagnosticState state,
    string label,
    RuntimeMissionDiagnosticFreshness expected,
    bool? fulfilled)
{
    var task = FindTask(state, label);
    AssertEqual(expected, task.Freshness, $"Task {label} has the wrong freshness.");
    AssertEqual(fulfilled, task.Fulfilled, $"Task {label} has the wrong fulfilled projection.");
}

static DateTime Utc(int second)
{
    return new DateTime(2026, 7, 28, 0, 0, 0, DateTimeKind.Utc).AddSeconds(second);
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

static string Sha256()
{
    return "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
}
