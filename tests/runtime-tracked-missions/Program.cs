using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MystiaStewardCompanion.LocalApi;
using MystiaStewardCompanion.Save;

try
{
    TestUnavailablePhases();
    TestStableActiveProjection();
    TestZeroConditionMission();
    TestStrictBusinessValidation();
    TestGenerationIdentity();
    TestPresentationProjection();
    TestPresentationRetryBudget();
    TestPresentationApplyIsTaskScoped();
    TestPublicApiPayload();
    Console.WriteLine(
        "PASS: tracked missions publish only active, strictly validated managed state "
        + "with deterministic ordering, tri-state conditions, and generation isolation.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL: {ex}");
    return 1;
}

static void TestUnavailablePhases()
{
    var state = new RuntimeMissionDiagnosticState();
    AssertUnavailable(
        state.ReadTrackedMissions(),
        generation: 0,
        RuntimeTrackedMissionsSnapshot.NotAttachedStatus);

    state.SetHookStatus("patched:9/9", attached: true, Utc(0));
    AssertUnavailable(
        state.ReadTrackedMissions(),
        generation: 0,
        RuntimeTrackedMissionsSnapshot.WaitingForLoadStatus);

    var load = state.BeginLoadCapture(threadId: 11, Utc(1));
    AssertUnavailable(
        state.ReadTrackedMissions(),
        load.Generation,
        RuntimeTrackedMissionsSnapshot.LoadingStatus);

    state.FailLoadCapture(load, "fixture-failure", Utc(2));
    var unavailable = state.ReadTrackedMissions();
    AssertUnavailable(
        unavailable,
        load.Generation,
        RuntimeTrackedMissionsSnapshot.RuntimeUnavailableStatus);
    AssertFalse(
        Serialize(unavailable).Contains("fixture-failure", StringComparison.Ordinal),
        "The business snapshot leaked an internal diagnostic error.");
}

static void TestStableActiveProjection()
{
    const int threadId = 19;
    var state = CreateReadyState(
        threadId,
        Loaded("zeta", conditionCount: 1, sourceOrdinal: 0, identity: (nint)103),
        Loaded("alpha", conditionCount: 2, sourceOrdinal: 2, identity: (nint)101),
        Loaded("beta", conditionCount: 1, sourceOrdinal: 1, identity: (nint)102));

    var initial = state.ReadTrackedMissions();
    AssertTrue(initial.RuntimeAvailable, "A valid ready state was not published.");
    AssertEqual(1L, initial.Generation, "The first load generation was not retained.");
    AssertEqual(RuntimeTrackedMissionsSnapshot.ReadyStatus, initial.Status, "Ready status mismatch.");
    AssertSequence(
        new[] { "alpha", "beta", "zeta" },
        initial.Missions.Select(mission => mission.Label),
        "Business missions were not ordered by stable ordinal labels.");
    foreach (var mission in initial.Missions)
    {
        AssertEqual(
            RuntimeTrackedMissionStatus.Tracking,
            mission.Status,
            $"Loaded mission {mission.Label} did not publish its controlled initial refresh.");
        AssertEqual<int?>(0, mission.CompletedConditionCount, "Initial incomplete progress count mismatch.");
        AssertEqual(
            mission.ConditionCount,
            mission.ConditionStates.Count,
            "Initial refreshed conditions lost their definition shape.");
        AssertTrue(
            mission.ConditionStates.All(value => value == false),
            "Initial refreshed conditions did not preserve their evaluated false values.");
    }

    AssertTrue(
        state.TryObserveStateRefresh(
            initial.Generation,
            threadId,
            Tracked((nint)101, "alpha", false, true),
            Utc(5)),
        "Tracking state refresh was rejected.");
    AssertTrue(
        state.TryObserveStateRefresh(
            initial.Generation,
            threadId,
            Tracked((nint)102, "beta", true),
            Utc(6)),
        "Fulfilled state refresh was rejected.");

    var refreshed = state.ReadTrackedMissions();
    var alpha = Find(refreshed, "alpha");
    AssertEqual(RuntimeTrackedMissionStatus.Tracking, alpha.Status, "Tracking status mismatch.");
    AssertEqual<int?>(1, alpha.CompletedConditionCount, "Tracking progress count mismatch.");
    AssertSequence(
        new bool?[] { false, true },
        alpha.ConditionStates,
        "Tracking condition states mismatch.");

    var beta = Find(refreshed, "beta");
    AssertEqual(RuntimeTrackedMissionStatus.Fulfilled, beta.Status, "Fulfilled status mismatch.");
    AssertEqual<int?>(1, beta.CompletedConditionCount, "Fulfilled progress count mismatch.");
    AssertSequence(
        new bool?[] { true },
        beta.ConditionStates,
        "Fulfilled condition states mismatch.");

    var stableJson = Serialize(refreshed);
    var stableSignature = Sha256(stableJson);
    AssertTrue(
        state.TryObserveStateRefresh(
            refreshed.Generation,
            threadId,
            Tracked((nint)101, "alpha", false, true),
            Utc(7)),
        "An identical natural refresh was rejected.");
    AssertEqual(
        stableJson,
        Serialize(state.ReadTrackedMissions()),
        "Diagnostic refresh counters or timestamps leaked into stable business content.");
    AssertEqual(
        stableSignature,
        Sha256(Serialize(state.ReadTrackedMissions())),
        "Equivalent business content produced an unstable signature input.");

    AssertTrue(
        state.TryObserveRemoval(
            refreshed.Generation,
            threadId,
            Tracked((nint)102, "beta", true),
            Utc(8)),
        "Mission removal was rejected.");
    var afterRemoval = state.ReadTrackedMissions();
    AssertFalse(
        afterRemoval.Missions.Any(mission => mission.Label == "beta"),
        "An inactive removed mission leaked into the business snapshot.");

    AssertTrue(
        state.TryObserveFinishedLabel(
            refreshed.Generation,
            threadId,
            "zeta",
            Utc(9)),
        "Finished label observation was rejected.");
    AssertSequence(
        new[] { "alpha" },
        state.ReadTrackedMissions().Missions.Select(mission => mission.Label),
        "Finished diagnostic history leaked into the active business list.");
}

static void TestStrictBusinessValidation()
{
    var definitionFailure = CreateReadyState(
        threadId: 21,
        Loaded(
            "missing-definition",
            conditionCount: 1,
            definition: RuntimeMissionDefinitionDiagnosticReadResult.Failed(
                "mission-label-not-found")));
    AssertIncomplete(
        definitionFailure.ReadTrackedMissions(),
        "A task without an exact definition escaped the business gate.");

    var titleFailure = CreateReadyState(
        threadId: 22,
        Loaded(
            "missing-title",
            conditionCount: 1,
            definition: Definition(
                "missing-title",
                conditionCount: 1,
                title: "",
                titleStatus: "unavailable:title-key-missing")));
    AssertIncomplete(
        titleFailure.ReadTrackedMissions(),
        "A task without an exact localized title escaped the business gate.");

    var receiverFailure = CreateReadyState(
        threadId: 28,
        Loaded(
            "oversized-receiver",
            conditionCount: 1,
            definition: Definition(
                "oversized-receiver",
                conditionCount: 1,
                receiver: new string(
                    'r',
                    RuntimeMissionPresentation.MaxReceiverLength + 1))));
    AssertIncomplete(
        receiverFailure.ReadTrackedMissions(),
        "A task with an out-of-domain receiver escaped the business gate.");
    AssertEqual(
        "definition-receiver-invalid",
        receiverFailure.Report().Tasks.Single().ValidationError,
        "An out-of-domain receiver reported the wrong diagnostic.");

    var savedShape = CreateReadyState(
        threadId: 23,
        Loaded(
            "saved-shape-empty",
            conditionCount: 2,
            savedFinishStateCount: 0,
            conditionDataCount: 2));
    var savedShapeMission = Find(savedShape.ReadTrackedMissions(), "saved-shape-empty");
    AssertEqual(
        RuntimeTrackedMissionStatus.Tracking,
        savedShapeMission.Status,
        "Controlled refresh did not replace an empty saved-state list.");
    AssertSequence(
        new bool?[] { false, false },
        savedShapeMission.ConditionStates,
        "Controlled refresh did not publish current task progress.");
    AssertEqual(
        "",
        savedShape.Report().Tasks.Single().ValidationError,
        "A valid empty saved-state list was labeled as a diagnostic error.");

    var initialShapeFailure = CreateReadyState(
        threadId: 27,
        Loaded(
            "initial-refresh-shape-mismatch",
            conditionCount: 1,
            refreshedFinishStates: new[] { false, true }));
    AssertIncomplete(
        initialShapeFailure.ReadTrackedMissions(),
        "A mismatched controlled initialization shape escaped the business gate.");
    AssertEqual(
        "refreshed-condition-count-mismatch",
        initialShapeFailure.Report().Tasks.Single().ValidationError,
        "A mismatched controlled initialization shape reported the wrong diagnostic.");

    var runtimePending = CreateReadyState(threadId: 24);
    var runtimePendingGeneration = runtimePending.Snapshot().Generation;
    AssertTrue(
        runtimePending.TryCommitStartedMission(
            runtimePendingGeneration,
            threadId: 24,
            Tracked((nint)241, "runtime-pending"),
            Definition("runtime-pending", conditionCount: 1),
            stateVerified: false,
            Utc(9)),
        "An unverified started task fixture was rejected.");
    var runtimePendingMission = Find(
        runtimePending.ReadTrackedMissions(),
        "runtime-pending");
    AssertEqual(
        RuntimeTrackedMissionStatus.Unverified,
        runtimePendingMission.Status,
        "An unverified started task was not kept pending before its first refresh.");
    AssertSequence(
        new bool?[] { null },
        runtimePendingMission.ConditionStates,
        "An unverified started task fabricated current progress.");

    const int refreshThreadId = 25;
    var refreshedShapeFailure = CreateReadyState(
        refreshThreadId,
        Loaded("refresh-shape-mismatch", conditionCount: 1, identity: (nint)301));
    var generation = refreshedShapeFailure.Snapshot().Generation;
    AssertFalse(
        refreshedShapeFailure.TryObserveStateRefresh(
            generation,
            refreshThreadId,
            Tracked((nint)301, "refresh-shape-mismatch", false, true),
            Utc(10)),
        "A mismatched natural state shape was accepted.");
    AssertIncomplete(
        refreshedShapeFailure.ReadTrackedMissions(),
        "A mismatched natural state shape escaped the business gate.");
    AssertTrue(
        refreshedShapeFailure.TryObserveStateRefresh(
            generation,
            refreshThreadId,
            Tracked((nint)301, "refresh-shape-mismatch", true),
            Utc(11)),
        "A later exact natural state shape did not recover the task.");
    AssertEqual(
        RuntimeTrackedMissionStatus.Fulfilled,
        Find(refreshedShapeFailure.ReadTrackedMissions(), "refresh-shape-mismatch").Status,
        "An exact natural refresh did not restore the task projection.");

    var inactiveInvalidHistory = CreateReadyState(
        threadId: 26,
        Loaded("valid", conditionCount: 1),
        Loaded(
            "invalid-history",
            conditionCount: 1,
            definition: Definition(
                "invalid-history",
                conditionCount: 1,
                title: "",
                titleStatus: "unavailable:title-key-missing")));
    AssertTrue(
        inactiveInvalidHistory.TryObserveFinishedLabel(
            inactiveInvalidHistory.Snapshot().Generation,
            threadId: 26,
            "invalid-history",
            Utc(12)),
        "Invalid history fixture could not be deactivated.");
    var activeOnly = inactiveInvalidHistory.ReadTrackedMissions();
    AssertTrue(activeOnly.RuntimeAvailable, "Inactive invalid history poisoned active task projection.");
    AssertSequence(
        new[] { "valid" },
        activeOnly.Missions.Select(mission => mission.Label),
        "Inactive invalid history was published.");
}

static void TestZeroConditionMission()
{
    const int threadId = 20;
    var state = CreateReadyState(
        threadId,
        Loaded("zero-condition", conditionCount: 0, identity: (nint)201));
    var initial = Find(state.ReadTrackedMissions(), "zero-condition");
    AssertEqual(
        RuntimeTrackedMissionStatus.Fulfilled,
        initial.Status,
        "A controlled initial refresh did not fulfill a zero-condition task.");
    AssertEqual<int?>(
        0,
        initial.CompletedConditionCount,
        "A zero-condition task returned the wrong initial completed count.");
    AssertEqual(
        0,
        initial.ConditionStates.Count,
        "A zero-condition task fabricated condition entries.");

    AssertTrue(
        state.TryObserveStateRefresh(
            state.Snapshot().Generation,
            threadId,
            Tracked((nint)201, "zero-condition"),
            Utc(9)),
        "An exact empty natural refresh was rejected for a zero-condition task.");
    var refreshed = Find(state.ReadTrackedMissions(), "zero-condition");
    AssertEqual(
        RuntimeTrackedMissionStatus.Fulfilled,
        refreshed.Status,
        "A naturally refreshed zero-condition task was not fulfilled.");
    AssertEqual<int?>(
        0,
        refreshed.CompletedConditionCount,
        "A naturally refreshed zero-condition task returned the wrong completed count.");
    AssertEqual(
        0,
        refreshed.ConditionStates.Count,
        "A naturally refreshed zero-condition task fabricated condition entries.");
}

static void TestGenerationIdentity()
{
    const int threadId = 31;
    var state = CreateAttachedState();
    CommitLoad(
        state,
        threadId,
        Loaded("same-content", conditionCount: 1));
    var first = state.ReadTrackedMissions();
    var firstJson = Serialize(first);

    CommitLoad(
        state,
        threadId,
        Loaded("same-content", conditionCount: 1));
    var second = state.ReadTrackedMissions();
    AssertEqual(
        first.Generation + 1,
        second.Generation,
        "Reloading did not advance the mission generation.");
    AssertNotEqual(
        firstJson,
        Serialize(second),
        "A new load generation retained the old business content identity.");
    AssertEqual(
        first.Missions.Single().Label,
        second.Missions.Single().Label,
        "Generation isolation test changed the task fixture.");
}

static void TestPresentationProjection()
{
    var threadId = Environment.CurrentManagedThreadId;
    var state = CreateReadyState(
        threadId,
        Loaded(
            "receiver-task",
            conditionCount: 1,
            definition: Definition(
                "receiver-task",
                conditionCount: 1,
                receiver: "Meirin")));
    var generation = state.Snapshot().Generation;
    var mappedIdentity = Utc(20);
    AssertTrue(
        state.TryReadPresentationRequests(
            generation,
            daySceneGeneration: 7,
            mappedIdentity,
            nowUtc: Utc(20),
            out var requests),
        "Presentation requests were not available on the owner thread.");
    AssertSequence(
        new[] { "Meirin" },
        requests.Select(request => request.ReceiverLabel),
        "Presentation requests lost the exact receiver.");
    AssertTrue(
        state.TryApplyPresentations(
            generation,
            daySceneGeneration: 7,
            mappedIdentity,
            new[]
            {
                PresentationApply(
                    "receiver-task",
                    new RuntimeMissionPresentation(
                        "Meirin",
                        "红美铃",
                        new[] { "红魔馆" },
                        RuntimeMissionPresentation.ReadyStatus)),
            },
            Utc(21),
            out var readyCount),
        "Exact managed presentation metadata was rejected.");
    AssertEqual(1, readyCount, "Ready presentation count mismatch.");

    var mission = Find(state.ReadTrackedMissions(), "receiver-task");
    AssertEqual("Meirin", mission.ReceiverLabel, "Receiver label mismatch.");
    AssertEqual("红美铃", mission.CharacterName, "Character name mismatch.");
    AssertSequence(new[] { "红魔馆" }, mission.SceneNames, "Related scene mismatch.");
    AssertEqual(
        RuntimeMissionPresentation.ReadyStatus,
        mission.PresentationStatus,
        "Presentation status mismatch.");
    AssertTrue(
        state.TryReadPresentationRequests(
            generation,
            daySceneGeneration: 7,
            mappedIdentity,
            nowUtc: Utc(21),
            out var stableRequests),
        "Stable presentation identity was rejected.");
    AssertEqual(
        0,
        stableRequests.Count,
        "A generation-bound presentation was scheduled for duplicate native reads.");
    AssertTrue(
        state.TryReadPresentationRequests(
            generation,
            daySceneGeneration: 8,
            mappedIdentity,
            nowUtc: Utc(22),
            out var nextDayRequests),
        "A new day-scene generation was rejected.");
    AssertEqual(
        1,
        nextDayRequests.Count,
        "A new day-scene generation did not invalidate presentation metadata.");
    AssertFalse(
        state.TryApplyPresentations(
            generation,
            daySceneGeneration: 8,
            mappedIdentity,
            new[]
            {
                PresentationApply(
                    "receiver-task",
                    new RuntimeMissionPresentation(
                        "Meirin",
                        new string(
                            'x',
                            RuntimeMissionPresentation.MaxDisplayNameLength + 1),
                        Array.Empty<string>(),
                        RuntimeMissionPresentation.ReadyStatus)),
            },
            Utc(22),
            out _),
        "An oversized presentation escaped the managed state boundary.");
    AssertEqual(
        "红美铃",
        Find(state.ReadTrackedMissions(), "receiver-task").CharacterName,
        "A rejected presentation mutated the last exact managed snapshot.");
}

static void TestPresentationRetryBudget()
{
    var threadId = Environment.CurrentManagedThreadId;
    var state = CreateReadyState(
        threadId,
        Loaded(
            "retry-presentation",
            conditionCount: 1,
            definition: Definition(
                "retry-presentation",
                conditionCount: 1,
                receiver: "Akyuu")));
    var generation = state.Snapshot().Generation;
    var mappedIdentity = Utc(30);
    var attemptAt = Utc(31);
    var unavailable = new[]
    {
        PresentationApply(
            "retry-presentation",
            new RuntimeMissionPresentation(
                "Akyuu",
                CharacterName: "",
                SceneNames: Array.Empty<string>(),
                PresentationStatus:
                    RuntimeMissionPresentation.EntryReadUnavailableStatus)),
    };

    for (var attempt = 1;
         attempt <= RuntimeMissionPresentation.MaxAttemptCount;
         attempt++)
    {
        AssertTrue(
            state.TryReadPresentationRequests(
                generation,
                daySceneGeneration: 9,
                mappedIdentity,
                attemptAt,
                out var requests),
            $"Presentation retry request {attempt} was rejected.");
        AssertEqual(
            1,
            requests.Count,
            $"Presentation retry request {attempt} was not scheduled.");
        AssertTrue(
            state.TryApplyPresentations(
                generation,
                daySceneGeneration: 9,
                mappedIdentity,
                unavailable,
                attemptAt,
                out _),
            $"Presentation retry result {attempt} was rejected.");
        AssertTrue(
            state.TryReadPresentationRequests(
                generation,
                daySceneGeneration: 9,
                mappedIdentity,
                attemptAt.AddMilliseconds(100),
                out var immediateRequests),
            $"Immediate retry check {attempt} was rejected.");
        AssertEqual(
            0,
            immediateRequests.Count,
            $"Presentation retry {attempt} ignored its backoff.");

        if (attempt <= RuntimeMissionPresentation.MaxRetryCount)
        {
            attemptAt += RuntimeMissionPresentation.RetryDelayAfterAttempt(
                attempt);
        }
    }

    AssertTrue(
        state.TryReadPresentationRequests(
            generation,
            daySceneGeneration: 9,
            mappedIdentity,
            attemptAt.AddDays(1),
            out var exhaustedRequests),
        "Exhausted presentation retry identity was rejected.");
    AssertEqual(
        0,
        exhaustedRequests.Count,
        "Presentation metadata retried after the bounded budget was exhausted.");
    AssertTrue(
        state.TryReadPresentationRequests(
            generation,
            daySceneGeneration: 10,
            mappedIdentity,
            attemptAt.AddDays(1),
            out var nextGenerationRequests),
        "A new day-scene identity was rejected after retry exhaustion.");
    AssertEqual(
        1,
        nextGenerationRequests.Count,
        "A new day-scene identity did not reset the retry budget.");
}

static void TestPresentationApplyIsTaskScoped()
{
    var threadId = Environment.CurrentManagedThreadId;
    var state = CreateReadyState(
        threadId,
        Loaded(
            "same-receiver-ready",
            conditionCount: 1,
            definition: Definition(
                "same-receiver-ready",
                conditionCount: 1,
                receiver: "Meirin")),
        Loaded(
            "same-receiver-pending",
            conditionCount: 1,
            definition: Definition(
                "same-receiver-pending",
                conditionCount: 1,
                receiver: "Meirin")));
    var generation = state.Snapshot().Generation;
    var mappedIdentity = Utc(50);
    var readyPresentation = new RuntimeMissionPresentation(
        "Meirin",
        "红美铃",
        new[] { "红魔馆" },
        RuntimeMissionPresentation.ReadyStatus);
    AssertTrue(
        state.TryApplyPresentations(
            generation,
            daySceneGeneration: 11,
            mappedIdentity,
            new[]
            {
                PresentationApply("same-receiver-ready", readyPresentation),
            },
            Utc(51),
            out var readyCount),
        "A task-scoped ready presentation was rejected.");
    AssertEqual(1, readyCount, "Task-scoped ready count was incorrect.");

    AssertTrue(
        state.TryReadPresentationRequests(
            generation,
            daySceneGeneration: 11,
            mappedIdentity,
            Utc(51),
            out var pendingRequests),
        "Pending same-receiver task could not be read.");
    AssertSequence(
        new[] { "same-receiver-pending" },
        pendingRequests.Select(request => request.Label),
        "A ready same-receiver task was requested again.");

    AssertTrue(
        state.TryApplyPresentations(
            generation,
            daySceneGeneration: 11,
            mappedIdentity,
            new[]
            {
                PresentationApply(
                    "same-receiver-pending",
                    new RuntimeMissionPresentation(
                        "Meirin",
                        CharacterName: "",
                        SceneNames: Array.Empty<string>(),
                        PresentationStatus:
                            RuntimeMissionPresentation
                                .EntryReadUnavailableStatus)),
            },
            Utc(52),
            out readyCount),
        "A task-scoped unavailable presentation was rejected.");
    AssertEqual(
        0,
        readyCount,
        "Ready count included an unrequested same-receiver sibling.");
    var readyMission = Find(
        state.ReadTrackedMissions(),
        "same-receiver-ready");
    AssertEqual(
        RuntimeMissionPresentation.ReadyStatus,
        readyMission.PresentationStatus,
        "An unavailable sibling downgraded a ready presentation.");
    AssertEqual(
        "红美铃",
        readyMission.CharacterName,
        "An unavailable sibling replaced ready character metadata.");

    AssertFalse(
        state.TryApplyPresentations(
            generation,
            daySceneGeneration: 11,
            mappedIdentity,
            new[]
            {
                PresentationApply(
                    "same-receiver-ready",
                    readyPresentation),
                PresentationApply(
                    "same-receiver-ready",
                    readyPresentation),
            },
            Utc(53),
            out _),
        "Duplicate task labels bypassed the presentation apply boundary.");
    AssertFalse(
        state.TryApplyPresentations(
            generation,
            daySceneGeneration: 11,
            mappedIdentity,
            new[]
            {
                new RuntimeMissionPresentationApply(
                    "same-receiver-ready",
                    "Akyuu",
                    readyPresentation),
            },
            Utc(53),
            out _),
        "A task/receiver identity mismatch bypassed the apply boundary.");
    AssertFalse(
        RuntimeMissionPresentation.IsValid(
            new RuntimeMissionPresentation(
                "Meirin",
                CharacterName: "   ",
                SceneNames: Array.Empty<string>(),
                PresentationStatus:
                    RuntimeMissionPresentation.SceneMarkerUnavailableStatus)),
        "A whitespace-only character name escaped the presentation boundary.");
}

static RuntimeMissionPresentationApply PresentationApply(
    string label,
    RuntimeMissionPresentation presentation)
{
    return new RuntimeMissionPresentationApply(
        label,
        presentation.ReceiverLabel,
        presentation);
}

static void TestPublicApiPayload()
{
    var jsonOptions = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
    var snapshot = new RuntimeTrackedMissionsSnapshot(
        RuntimeAvailable: true,
        Generation: 17,
        RuntimeTrackedMissionsSnapshot.ReadyStatus,
        new[]
        {
            new RuntimeTrackedMissionSnapshot(
                "mission-a",
                "任务甲",
                ReceiverLabel: "",
                CharacterName: "",
                SceneNames: Array.Empty<string>(),
                PresentationStatus: RuntimeMissionPresentation.NoReceiverStatus,
                Status: RuntimeTrackedMissionStatus.Unverified,
                ConditionCount: 1,
                CompletedConditionCount: null,
                new bool?[] { null }),
            new RuntimeTrackedMissionSnapshot(
                "mission-b",
                "任务乙",
                ReceiverLabel: "Meirin",
                CharacterName: "红美铃",
                SceneNames: new[] { "红魔馆" },
                PresentationStatus: RuntimeMissionPresentation.ReadyStatus,
                Status: RuntimeTrackedMissionStatus.Tracking,
                ConditionCount: 2,
                CompletedConditionCount: 1,
                new bool?[] { true, false }),
            new RuntimeTrackedMissionSnapshot(
                "mission-c",
                "任务丙",
                ReceiverLabel: "Akyuu",
                CharacterName: "稗田阿求",
                SceneNames: Array.Empty<string>(),
                PresentationStatus: "unavailable:scene-marker",
                Status: RuntimeTrackedMissionStatus.Fulfilled,
                ConditionCount: 1,
                CompletedConditionCount: 1,
                new bool?[] { true }),
        });

    var fullJson = LocalApiTrackedMissionsPayload.BuildJson(
        snapshot,
        knownSignature: "",
        jsonOptions);
    using var fullDocument = JsonDocument.Parse(fullJson);
    var root = fullDocument.RootElement;
    AssertTrue(root.GetProperty("ok").GetBoolean(), "The structured task read was not successful.");
    AssertTrue(root.GetProperty("runtimeAvailable").GetBoolean(), "Ready task data was unavailable.");
    AssertEqual(1, root.GetProperty("unverifiedCount").GetInt32(), "Unverified count mismatch.");
    AssertEqual(1, root.GetProperty("trackingCount").GetInt32(), "Tracking count mismatch.");
    AssertEqual(1, root.GetProperty("fulfilledCount").GetInt32(), "Fulfilled count mismatch.");
    var missions = root.GetProperty("missions");
    AssertEqual("unverified", missions[0].GetProperty("status").GetString(), "Unverified status was not lowercase.");
    AssertEqual(JsonValueKind.Null, missions[0].GetProperty("completedConditionCount").ValueKind,
        "Unverified progress was not nullable.");
    AssertEqual("tracking", missions[1].GetProperty("status").GetString(), "Tracking status was not lowercase.");
    AssertEqual("Meirin", missions[1].GetProperty("receiverLabel").GetString(), "Receiver label missing.");
    AssertEqual("红美铃", missions[1].GetProperty("characterName").GetString(), "Character name missing.");
    AssertEqual("红魔馆", missions[1].GetProperty("sceneNames")[0].GetString(), "Related scene missing.");
    AssertEqual("ready", missions[1].GetProperty("presentationStatus").GetString(), "Presentation status missing.");
    AssertEqual("fulfilled", missions[2].GetProperty("status").GetString(), "Fulfilled status was not lowercase.");
    var signature = root.GetProperty("contentSignature").GetString() ?? "";
    AssertTrue(
        signature.Length == 64 && signature.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f'),
        "Task content signature was not canonical lowercase SHA-256.");
    foreach (var forbidden in new[]
             {
                 "changeVersion",
                 "changedAtUtc",
                 "nativeIdentity",
                 "sourcePartition",
                 "validationError",
             })
    {
        AssertFalse(fullJson.Contains(forbidden, StringComparison.Ordinal),
            $"Public task payload leaked internal field {forbidden}.");
    }

    var unchangedJson = LocalApiTrackedMissionsPayload.BuildJson(
        snapshot,
        signature,
        jsonOptions);
    using var unchangedDocument = JsonDocument.Parse(unchangedJson);
    var unchanged = unchangedDocument.RootElement;
    AssertTrue(unchanged.GetProperty("unchanged").GetBoolean(),
        "Matching knownSignature did not return the compact unchanged payload.");
    AssertEqual(signature, unchanged.GetProperty("contentSignature").GetString(),
        "Unchanged payload returned a different signature.");
    AssertEqual(2, unchanged.EnumerateObject().Count(),
        "Unchanged payload contains fields from the full response.");

    var nextGenerationJson = LocalApiTrackedMissionsPayload.BuildJson(
        snapshot with { Generation = 18 },
        knownSignature: "",
        jsonOptions);
    using var nextGenerationDocument = JsonDocument.Parse(nextGenerationJson);
    AssertNotEqual(
        signature,
        nextGenerationDocument.RootElement.GetProperty("contentSignature").GetString(),
        "A new mission generation reused the previous API content signature.");
}

static RuntimeMissionDiagnosticState CreateAttachedState()
{
    var state = new RuntimeMissionDiagnosticState();
    state.SetHookStatus("patched:9/9", attached: true, Utc(0));
    return state;
}

static RuntimeMissionDiagnosticState CreateReadyState(
    int threadId,
    params RuntimeMissionDiagnosticLoadedSeed[] tasks)
{
    var state = CreateAttachedState();
    CommitLoad(state, threadId, tasks);
    return state;
}

static void CommitLoad(
    RuntimeMissionDiagnosticState state,
    int threadId,
    params RuntimeMissionDiagnosticLoadedSeed[] tasks)
{
    var load = state.BeginLoadCapture(threadId, Utc(1));
    AssertTrue(
        state.TryMarkLoadSeedReady(
            load,
            Metrics(tasks.Length),
            Utc(2)),
        "Test setup could not publish a load seed.");
    AssertTrue(
        state.TryBeginInitialization(load, threadId, Utc(3), out var initialization),
        "Test setup could not begin initialization.");
    var bucketCount = tasks.Length == 0
        ? 0
        : tasks.Select(task => task.MergedBucket).Distinct().Count();
    var seed = new RuntimeMissionDiagnosticInitializationSeed(
        initialization.Generation,
        initialization.ThreadId,
        RuntimeTrackingBucketCount: bucketCount,
        SeedTrackingBucketCount: bucketCount,
        TrackingBufferCount: 0,
        CurrentDate: 55,
        SelectedDlcPartitions: new[] { "DLC1" },
        TrackedMissions: tasks,
        SeedFinishedMissionLabels: Array.Empty<string>(),
        RuntimeFinishedMissionLabels: Array.Empty<string>(),
        DefinitionReadElapsedMilliseconds: 3);
    AssertTrue(
        state.TryCommitInitialization(initialization, seed, Utc(4), out _),
        "Test setup could not commit a load generation.");
}

static RuntimeMissionDiagnosticLoadMetrics Metrics(int taskCount)
{
    return new RuntimeMissionDiagnosticLoadMetrics(
        JsonLength: 2048,
        JsonSha256: "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
        SerializeElapsedMilliseconds: 12,
        ParseElapsedMilliseconds: 4,
        FileVersion: "1.4.3",
        SavedGameDay: 55,
        ParsedTrackingMissionCount: taskCount,
        ParsedFinishedMissionCount: 0,
        ParsedDlcPartitionCount: 1);
}

static RuntimeMissionDiagnosticLoadedSeed Loaded(
    string label,
    int conditionCount,
    int sourceOrdinal = 0,
    int? savedFinishStateCount = null,
    int conditionDataCount = 0,
    IReadOnlyList<bool>? refreshedFinishStates = null,
    nint? identity = null,
    RuntimeMissionDefinitionDiagnosticReadResult? definition = null)
{
    var savedCount = savedFinishStateCount ?? conditionCount;
    var resolvedDefinition = definition ?? Definition(label, conditionCount);
    return new RuntimeMissionDiagnosticLoadedSeed(
        SourcePartition: "CORE",
        SourceIsCore: true,
        SourceBucket: 0,
        MergedBucket: 0,
        sourceOrdinal,
        label,
        SavedFinishStateCount: savedCount,
        SavedTrueFinishStateCount: 0,
        ConditionDataCount: conditionDataCount,
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
    string? title = null,
    string titleStatus = "available",
    string receiver = "")
{
    var conditions = Enumerable.Range(0, conditionCount)
        .Select(_ => new RuntimeMissionDefinitionDiagnosticCondition(Type: 0, Amount: 0))
        .ToArray();
    return new RuntimeMissionDefinitionDiagnosticReadResult(
        Success: true,
        new RuntimeMissionDefinitionDiagnostic(
            label,
            title ?? $"Title {label}",
            titleStatus,
            HasReceiver: receiver.Length > 0,
            Receiver: receiver,
            conditionCount,
            conditions,
            ServeInWorkFoodIds: Array.Empty<int>()),
        Failure: "");
}

static RuntimeMissionDiagnosticTrackedSeed Tracked(
    nint identity,
    string label,
    params bool[] finishStates)
{
    return new RuntimeMissionDiagnosticTrackedSeed(identity, label, finishStates);
}

static RuntimeTrackedMissionSnapshot Find(
    RuntimeTrackedMissionsSnapshot snapshot,
    string label)
{
    return snapshot.Missions.Single(mission =>
        string.Equals(mission.Label, label, StringComparison.Ordinal));
}

static void AssertUnavailable(
    RuntimeTrackedMissionsSnapshot snapshot,
    long generation,
    string status)
{
    AssertFalse(snapshot.RuntimeAvailable, "Unavailable state was published as available.");
    AssertEqual(generation, snapshot.Generation, "Unavailable generation mismatch.");
    AssertEqual(status, snapshot.Status, "Unavailable status mismatch.");
    AssertEqual(0, snapshot.Missions.Count, "Unavailable state leaked task data.");
}

static void AssertIncomplete(
    RuntimeTrackedMissionsSnapshot snapshot,
    string message)
{
    AssertFalse(snapshot.RuntimeAvailable, message);
    AssertEqual(
        RuntimeTrackedMissionsSnapshot.MissionDataIncompleteStatus,
        snapshot.Status,
        "Strict validation returned the wrong public status.");
    AssertEqual(0, snapshot.Missions.Count, "Strict validation leaked a partial task list.");
}

static string Serialize(RuntimeTrackedMissionsSnapshot snapshot)
{
    var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    return JsonSerializer.Serialize(snapshot, options);
}

static string Sha256(string value)
{
    return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
        .ToLowerInvariant();
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

static void AssertNotEqual<T>(T left, T right, string message)
{
    if (EqualityComparer<T>.Default.Equals(left, right))
    {
        throw new InvalidOperationException($"{message} Both={left}.");
    }
}

static void AssertSequence<T>(
    IEnumerable<T> expected,
    IEnumerable<T> actual,
    string message)
{
    if (!expected.SequenceEqual(actual))
    {
        throw new InvalidOperationException(
            $"{message} Expected=[{string.Join(",", expected)}]; "
            + $"Actual=[{string.Join(",", actual)}].");
    }
}
