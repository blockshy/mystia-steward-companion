using System.Text.Json;
using System.Text.Json.Serialization;
using MystiaStewardCompanion.LocalApi;
using MystiaStewardCompanion.Save;

try
{
    TestSupportedProjection();
    TestAutomaticTriggerProjection();
    TestStrictCandidateBoundary();
    TestNativeStartMissionGates();
    TestDuplicateMissionRules();
    TestSourceLifecycleState();
    TestUnavailableAndStateIsolation();
    TestStableApiPayload();
    Console.WriteLine(
        "PASS: available missions use exact scene/kizuna trigger classification, "
        + "source lifecycle revisions, native start gates, no-receiver presentation, "
        + "deterministic aggregation, and stable payload signatures.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL: {ex}");
    return 1;
}

static void TestSupportedProjection()
{
    var snapshot = ProjectWithHistory(
        new[]
        {
            Candidate(
                sourceEvent: "Kizuna_Meirin_LV2_Upgrade_Event",
                mission: "Kizuna_Meirin_LV2_Upgrade_Mission",
                title: "请美铃品尝料理",
                preNodes: new[]
                {
                    "Kizuna_Meirin_LV2_Upgrade_Event",
                    "Earlier_Event",
                }),
            Candidate(
                sourceEvent: "Kizuna_Akyuu_LV2_Upgrade_Event",
                mission: "Kizuna_Akyuu_LV2_Upgrade_Mission",
                title: "阿求任务",
                receiver: "Akyuu",
                characterName: "稗田阿求",
                sceneNames: new[] { "人间之里" }),
        },
        finishedEvents: new[] { "Earlier_Event" });

    AssertTrue(snapshot.RuntimeAvailable, "A complete capture was unavailable.");
    AssertEqual(RuntimeAvailableMissionSnapshot.ReadyStatus, snapshot.Status, "Ready status mismatch.");
    AssertSequence(
        new[]
        {
            "Kizuna_Akyuu_LV2_Upgrade_Mission",
            "Kizuna_Meirin_LV2_Upgrade_Mission",
        },
        snapshot.Missions.Select(mission => mission.Label),
        "Available missions were not ordered by ordinal label.");
    AssertEqual("", snapshot.Error, "A ready snapshot published an error.");
    var a = snapshot.Missions[0];
    AssertEqual("Akyuu", a.ReceiverLabel, "Available receiver label mismatch.");
    AssertEqual("稗田阿求", a.CharacterName, "Available character name mismatch.");
    AssertSequence(new[] { "人间之里" }, a.SceneNames, "Available related scenes mismatch.");
    AssertEqual("ready", a.PresentationStatus, "Available presentation status mismatch.");
    AssertEqual("conditional", a.ActivationMode, "Kizuna activation mode mismatch.");
    AssertEqual("available", a.ActivationStatus, "Kizuna activation status mismatch.");
    AssertEqual("kizuna-checkpoint", a.TriggerKind, "Kizuna trigger kind mismatch.");
    AssertEqual("after-performance", a.SourceTiming, "Kizuna source timing mismatch.");
}

static void TestAutomaticTriggerProjection()
{
    var snapshot = Project(
        Candidate(
            sourceEvent: "Enter_Map_Event",
            mission: "Enter_Map_Mission",
            triggerType: RuntimeAvailableMissionTriggerClassifier
                .OnEnterDaySceneMapTrigger,
            referenceSource:
                RuntimeAvailableMissionSourceState.BeforePerformanceSource,
            hasReceiver: false,
            receiver: "",
            characterName: "",
            sceneNames: Array.Empty<string>(),
            presentationStatus: RuntimeMissionPresentation.NoReceiverStatus),
        Candidate(
            sourceEvent: "Enter_Day_Event",
            mission: "Enter_Day_Mission",
            triggerType: RuntimeAvailableMissionTriggerClassifier
                .OnEnterDaySceneTrigger,
            sourcePhase: RuntimeAvailableMissionTriggerClassifier
                .WaitingAfterPerformancePhase));

    AssertTrue(snapshot.RuntimeAvailable, "Scene-triggered missions were unavailable.");
    AssertEqual(2, snapshot.Missions.Count, "Scene-triggered mission count mismatch.");
    var mapMission = snapshot.Missions.Single(mission =>
        mission.Label == "Enter_Map_Mission");
    AssertEqual("automatic", mapMission.ActivationMode, "Map activation mode mismatch.");
    AssertEqual("available", mapMission.ActivationStatus, "Map activation status mismatch.");
    AssertEqual("enter-day-scene-map", mapMission.TriggerKind, "Map trigger kind mismatch.");
    AssertEqual("before-performance", mapMission.SourceTiming, "Map source timing mismatch.");
    AssertEqual("", mapMission.ReceiverLabel, "No-receiver task forged a receiver.");
    AssertEqual("no-receiver", mapMission.PresentationStatus, "No-receiver status mismatch.");

    var dayMission = snapshot.Missions.Single(mission =>
        mission.Label == "Enter_Day_Mission");
    AssertEqual("triggering", dayMission.ActivationStatus, "Transition status mismatch.");
    AssertEqual("native-start-pending", dayMission.ActivationHint, "Transition hint mismatch.");
}

static void TestStrictCandidateBoundary()
{
    var unsupported = Project(
        Candidate(
            triggerType: 3,
            sourceEvent: "Talk_Event",
            mission: "Talk_Mission"),
        Candidate(
            eligibility: "ineligible",
            sourceEvent: "Not_Eligible_Event",
            mission: "Not_Eligible_Mission"),
        Candidate(
            referenceSource: "legacyPostMissions",
            sourceEvent: "Unknown_Source_Event",
            mission: "Unknown_Source_Mission"));
    AssertEqual(0, unsupported.Missions.Count, "An unsupported source entered the business list.");

    AssertUnavailable(
        Project(Candidate(
            sourceEvent: "Bad_Definition_Event",
            mission: "Bad_Definition_Mission",
            definitionAvailable: false)),
        "An unavailable exact mission definition did not fail closed.");
    AssertUnavailable(
        Project(Candidate(
            sourceEvent: "Missing_Title_Event",
            mission: "Missing_Title_Mission",
            title: "")),
        "A missing localized title did not fail closed.");
    AssertUnavailable(
        Project(Candidate(
            sourceEvent: "Whitespace_Title_Event",
            mission: "Whitespace_Title_Mission",
            title: "   ")),
        "A whitespace-only localized title did not fail closed.");
    var noReceiver = Project(Candidate(
        sourceEvent: "No_Receiver_Event",
        mission: "No_Receiver_Mission",
        hasReceiver: false,
        receiver: "",
        characterName: "",
        sceneNames: Array.Empty<string>(),
        presentationStatus: RuntimeMissionPresentation.NoReceiverStatus));
    AssertEqual(1, noReceiver.Missions.Count, "A no-receiver task was excluded.");
    AssertUnavailable(
        Project(Candidate(
            sourceEvent: "Contradictory_Receiver_Event",
            mission: "Contradictory_Receiver_Mission",
            hasReceiver: true,
            receiver: "",
            characterName: "",
            sceneNames: Array.Empty<string>(),
            presentationStatus: RuntimeMissionPresentation.NoReceiverStatus)),
        "An available task accepted a no-receiver presentation.");
    AssertUnavailable(
        Project(Candidate(
            sourceEvent: "Duplicate_PreNode_Event",
            mission: "Duplicate_PreNode_Mission",
            preNodes: new[] { "One", "One" })),
        "Duplicate exact preNodes did not fail closed.");
    AssertUnavailable(
        Project(Candidate(
            sourceEvent: "Long_Character_Event",
            mission: "Long_Character_Mission",
            characterName: new string(
                'x',
                RuntimeMissionPresentation.MaxDisplayNameLength + 1))),
        "An oversized character name escaped the presentation boundary.");
    AssertUnavailable(
        Project(Candidate(
            sourceEvent: "Whitespace_Character_Event",
            mission: "Whitespace_Character_Mission",
            characterName: "   ",
            presentationStatus:
                RuntimeMissionPresentation.SceneMarkerUnavailableStatus)),
        "A whitespace-only character name escaped the presentation boundary.");
    AssertUnavailable(
        Project(Candidate(
            sourceEvent: "Too_Many_Scenes_Event",
            mission: "Too_Many_Scenes_Mission",
            sceneNames: Enumerable.Range(
                    0,
                    RuntimeMissionPresentation.MaxSceneCount + 1)
                .Select(index => $"Scene {index}")
                .ToArray())),
        "An oversized related-scene set escaped the presentation boundary.");
    AssertUnavailable(
        Project(Candidate(
            sourceEvent: "Invalid_Status_Event",
            mission: "Invalid_Status_Mission",
            presentationStatus: "unavailable:bad_status")),
        "An invalid presentation status escaped the bounded code domain.");
}

static void TestNativeStartMissionGates()
{
    var missingPreNode = Project(
        Candidate(
            sourceEvent: "Source_Event",
            mission: "Needs_Previous_Mission",
            preNodes: new[] { "Previous_Mission" }));
    AssertEqual(0, missingPreNode.Missions.Count, "An unmet preNode was ignored.");

    var finishedMission = ProjectWithHistory(
        new[]
        {
            Candidate(
                sourceEvent: "Source_Event",
                mission: "Needs_Previous_Mission",
                preNodes: new[] { "Previous_Mission" }),
        },
        finishedMissions: new[] { "Previous_Mission" });
    AssertEqual(1, finishedMission.Missions.Count, "A finished mission preNode was not accepted.");

    var sourceAboutToFinish = Project(
        Candidate(
            sourceEvent: "Source_Event",
            mission: "After_Performance_Mission",
            preNodes: new[] { "Source_Event" }));
    AssertEqual(
        1,
        sourceAboutToFinish.Missions.Count,
        "The source event was not treated as finished at its native after-performance boundary.");

    var opaqueHistory = ProjectWithHistory(
        new[]
        {
            Candidate(
                sourceEvent: "Opaque_Source",
                mission: "Opaque_History_Mission",
                preNodes: new[] { "  edged history  " }),
        },
        finishedEvents: new[] { "", " ", "  edged history  ", "" });
    AssertEqual(
        1,
        opaqueHistory.Missions.Count,
        "Opaque empty/whitespace/duplicate history entries corrupted exact membership.");

    var active = Project(Candidate(
        sourceEvent: "Source_Event",
        mission: "Already_Active",
        active: true));
    AssertEqual(0, active.Missions.Count, "An active mission was published as available.");

    var finishedOneShot = Project(Candidate(
        sourceEvent: "Source_Event",
        mission: "Finished_OneShot",
        finished: true,
        loopedMission: false));
    AssertEqual(0, finishedOneShot.Missions.Count, "A finished one-shot mission was published.");

    var finishedLoop = Project(Candidate(
        sourceEvent: "Source_Event",
        mission: "Finished_Loop",
        finished: true,
        loopedMission: true));
    AssertEqual(1, finishedLoop.Missions.Count, "A finished loop mission was incorrectly excluded.");
}

static void TestDuplicateMissionRules()
{
    var compatible = ProjectWithHistory(
        new[]
        {
            Candidate(
                sourceEvent: "Source_A",
                mission: "Shared_Mission",
                title: "共享任务",
                preNodes: new[] { "Missing_A" }),
            Candidate(
                sourceEvent: "Source_B",
                mission: "Shared_Mission",
                title: "共享任务",
                preNodes: new[] { "Missing_A" }),
        },
        finishedEvents: new[] { "Missing_A" });
    AssertEqual(1, compatible.Missions.Count, "Compatible duplicate references were not deduplicated.");

    var oneSatisfied = Project(
        Candidate(
            sourceEvent: "Source_A",
            mission: "Shared_Mission",
            title: "共享任务",
            preNodes: new[] { "Source_A" }),
        Candidate(
            sourceEvent: "Source_B",
            mission: "Shared_Mission",
            title: "共享任务",
            preNodes: new[] { "Source_A" }));
    AssertEqual(
        1,
        oneSatisfied.Missions.Count,
        "A mission with one satisfied compatible source was not published.");

    var multipleSources = Project(
        Candidate(
            sourceEvent: "Map_Source",
            mission: "Multi_Source_Mission",
            title: "多来源任务",
            triggerType: RuntimeAvailableMissionTriggerClassifier
                .OnEnterDaySceneMapTrigger,
            referenceSource:
                RuntimeAvailableMissionSourceState.BeforePerformanceSource),
        Candidate(
            sourceEvent: "Kizuna_Source",
            mission: "Multi_Source_Mission",
            title: "多来源任务"));
    var aggregated = multipleSources.Missions.Single();
    AssertEqual("multiple", aggregated.ActivationMode, "Multiple activation modes were guessed.");
    AssertEqual("multiple", aggregated.TriggerKind, "Multiple trigger kinds were guessed.");
    AssertEqual("multiple", aggregated.SourceTiming, "Multiple source timings were guessed.");
    AssertEqual("multiple-sources", aggregated.ActivationHint, "Multiple source hint mismatch.");

    AssertUnavailable(
        Project(
            Candidate(
                sourceEvent: "Source_A",
                mission: "Conflicting_Mission",
                title: "标题 A"),
            Candidate(
                sourceEvent: "Source_B",
                mission: "Conflicting_Mission",
                title: "标题 B")),
        "Conflicting duplicate metadata did not fail closed.");
}

static void TestSourceLifecycleState()
{
    var state = new RuntimeAvailableMissionSourceState();
    var detached = state.Snapshot();
    AssertFalse(detached.HooksAttached, "Source state started attached.");
    AssertFalse(detached.RuntimeAvailable, "Detached source state was available.");

    state.SetHookStatus(true, "patched:4/4", DateTime.UnixEpoch);
    state.ResetForMissionGeneration(7, 41, DateTime.UnixEpoch.AddSeconds(1));
    var reset = state.Snapshot();
    AssertTrue(reset.HooksAttached, "Source hooks were not retained across reset.");
    AssertFalse(reset.RuntimeAvailable, "Source state armed before scheduler initialization.");
    AssertTrue(
        state.ArmMissionGeneration(7, 41, DateTime.UnixEpoch.AddSeconds(2)),
        "Exact source generation did not arm.");
    AssertFalse(
        state.ArmMissionGeneration(6, 41, DateTime.UnixEpoch.AddSeconds(3)),
        "A stale source generation armed.");

    var armed = state.Snapshot();
    AssertTrue(armed.RuntimeAvailable, "Armed source state was unavailable.");
    AssertTrue(
        state.ObserveSchedulerBoundary(
            7,
            41,
            "  opaque event  ",
            "schedule-event",
            DateTime.UnixEpoch.AddSeconds(4)),
        "Exact scheduler boundary was rejected.");
    AssertTrue(
        state.Snapshot().SourceRevision > armed.SourceRevision,
        "Scheduler boundary did not advance source revision.");

    AssertTrue(
        state.CommitBeforePerformance(
            7,
            41,
            "  opaque event  ",
            new[]
            {
                RuntimeAvailableMissionStartOutcome.Started,
                RuntimeAvailableMissionStartOutcome.Retired,
            },
            new[] { "After_A", "After_A", "  After B  " },
            DateTime.UnixEpoch.AddSeconds(5)),
        "Before-performance transition did not commit.");
    var waiting = state.Snapshot();
    AssertEqual(1, waiting.Transitions.Count, "Waiting transition count mismatch.");
    AssertEqual(
        RuntimeAvailableMissionSourcePhase.WaitingAfterPerformance,
        waiting.Transitions[0].Phase,
        "Waiting transition phase mismatch.");
    AssertSequence(
        new[] { 0, 1, 2 },
        waiting.Transitions[0].References.Select(reference => reference.SourceOrdinal),
        "Repeated after-performance references lost source ordinals.");
    AssertSequence(
        new[] { "After_A", "After_A", "  After B  " },
        waiting.Transitions[0].References.Select(reference => reference.MissionLabel),
        "Opaque after-performance identities were normalized.");

    AssertTrue(
        state.CommitAfterPerformance(
            7,
            41,
            "  opaque event  ",
            new[] { "After_A", "After_A", "  After B  " },
            new[]
            {
                RuntimeAvailableMissionStartOutcome.Started,
                RuntimeAvailableMissionStartOutcome.Retired,
                RuntimeAvailableMissionStartOutcome.Started,
            },
            DateTime.UnixEpoch.AddSeconds(6)),
        "After-performance transition did not retire.");
    AssertEqual(
        0,
        state.Snapshot().Transitions.Count,
        "Terminal source transition remained active.");

    AssertFalse(
        state.CommitBeforePerformance(
            7,
            41,
            "Uncertain_Event",
            new[] { RuntimeAvailableMissionStartOutcome.Uncertain },
            new[] { "Must_Not_Leak" },
            DateTime.UnixEpoch.AddSeconds(7)),
        "An uncertain native start outcome was accepted.");
    var failed = state.Snapshot();
    AssertFalse(failed.RuntimeAvailable, "Uncertain source state did not fail closed.");
    AssertEqual(0, failed.Transitions.Count, "Failed source state leaked transitions.");

    state.ResetForMissionGeneration(8, 41, DateTime.UnixEpoch.AddSeconds(8));
    AssertTrue(
        state.ArmMissionGeneration(8, 41, DateTime.UnixEpoch.AddSeconds(9)),
        "A new exact generation did not recover source state.");
    AssertTrue(state.Snapshot().RuntimeAvailable, "Recovered source state was unavailable.");
}

static void TestUnavailableAndStateIsolation()
{
    var incomplete = RuntimeAvailableMissionCapture.Project(
        Input(
            complete: false,
            Candidate(
                sourceEvent: "Source_Event",
                mission: "Must_Not_Leak")));
    AssertUnavailable(incomplete, "An incomplete capture was published.");
    AssertEqual(0, incomplete.Missions.Count, "Unavailable content leaked partial missions.");

    var state = new RuntimeAvailableMissionState();
    AssertUnavailable(state.Snapshot(), "Initial state was available.");
    var ready = state.Publish(
        Input(
            complete: true,
            Candidate(
                sourceEvent: "Source_Event",
                mission: "Available_Mission")));
    AssertTrue(ready.RuntimeAvailable, "State did not publish a fresh projection.");
    AssertEqual(1, ready.Missions.Count, "State lost a projected mission.");

    var copied = state.Snapshot();
    AssertFalse(
        ReferenceEquals(ready.Missions, copied.Missions),
        "State exposed its mutable mission array identity.");
    var unavailable = state.SetUnavailable(2, 8, "scene-changed");
    AssertUnavailable(unavailable, "State did not clear after lifecycle invalidation.");
    AssertEqual(0, state.Snapshot().Missions.Count, "State retained stale missions.");
    var recovered = state.Publish(
        Input(
            complete: true,
            Candidate(
                sourceEvent: "Recovered_Source",
                mission: "Recovered_Mission")));
    AssertTrue(recovered.RuntimeAvailable, "A later fresh read did not recover from failure.");
    AssertEqual("Recovered_Mission", recovered.Missions.Single().Label, "Recovery content mismatch.");
}

static void TestStableApiPayload()
{
    var options = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
    var snapshot = Project(
        Candidate(
            sourceEvent: "Source_Z",
            mission: "zeta",
            title: "Z"),
        Candidate(
            sourceEvent: "Source_A",
            mission: "alpha",
            title: "A"));
    var json = LocalApiAvailableMissionsPayload.BuildJson(snapshot, "", options);
    using var document = JsonDocument.Parse(json);
    var root = document.RootElement;
    AssertTrue(root.GetProperty("ok").GetBoolean(), "API payload was not successful.");
    AssertTrue(root.GetProperty("runtimeAvailable").GetBoolean(), "Ready API payload was unavailable.");
    AssertEqual(2, root.GetProperty("availableCount").GetInt32(), "Available count mismatch.");
    AssertEqual("alpha", root.GetProperty("missions")[0].GetProperty("label").GetString(), "API order mismatch.");
    AssertEqual("Meirin", root.GetProperty("missions")[0].GetProperty("receiverLabel").GetString(),
        "Available API receiver label missing.");
    AssertEqual("红美铃", root.GetProperty("missions")[0].GetProperty("characterName").GetString(),
        "Available API character name missing.");
    AssertEqual("红魔馆", root.GetProperty("missions")[0].GetProperty("sceneNames")[0].GetString(),
        "Available API related scene missing.");
    AssertEqual("ready", root.GetProperty("missions")[0].GetProperty("presentationStatus").GetString(),
        "Available API presentation status missing.");
    AssertEqual("conditional", root.GetProperty("missions")[0].GetProperty("activationMode").GetString(),
        "Available API activation mode missing.");
    AssertEqual("available", root.GetProperty("missions")[0].GetProperty("activationStatus").GetString(),
        "Available API activation status missing.");
    AssertEqual("kizuna-checkpoint", root.GetProperty("missions")[0].GetProperty("triggerKind").GetString(),
        "Available API trigger kind missing.");
    AssertEqual("after-performance", root.GetProperty("missions")[0].GetProperty("sourceTiming").GetString(),
        "Available API source timing missing.");
    AssertEqual(17L, root.GetProperty("sourceRevision").GetInt64(),
        "Available API source revision mismatch.");
    AssertFalse(root.TryGetProperty("daySceneGeneration", out _),
        "Removed daySceneGeneration leaked into the new protocol.");
    var signature = root.GetProperty("contentSignature").GetString() ?? "";
    AssertEqual(64, signature.Length, "Content signature length mismatch.");
    AssertTrue(
        signature.All(character =>
            character is >= '0' and <= '9'
            or >= 'a' and <= 'f'),
        "Content signature was not lowercase SHA-256.");
    AssertEqual(
        json,
        LocalApiAvailableMissionsPayload.BuildJson(snapshot, "", options),
        "Equivalent content produced unstable JSON.");

    var unchangedJson = LocalApiAvailableMissionsPayload.BuildJson(
        snapshot,
        signature,
        options);
    using var unchangedDocument = JsonDocument.Parse(unchangedJson);
    var unchanged = unchangedDocument.RootElement;
    AssertEqual(2, unchanged.EnumerateObject().Count(), "Unchanged payload leaked content.");
    AssertTrue(unchanged.GetProperty("unchanged").GetBoolean(), "Unchanged marker missing.");
    AssertEqual(signature, unchanged.GetProperty("contentSignature").GetString(), "Unchanged signature mismatch.");

    var unavailable = RuntimeAvailableMissionSnapshot.Unavailable(4, 9, "source-not-ready");
    var unavailableJson = LocalApiAvailableMissionsPayload.BuildJson(
        unavailable,
        "",
        options);
    using var unavailableDocument = JsonDocument.Parse(unavailableJson);
    var unavailableRoot = unavailableDocument.RootElement;
    AssertFalse(
        unavailableRoot.GetProperty("runtimeAvailable").GetBoolean(),
        "Unavailable payload was marked available.");
    AssertEqual(
        0,
        unavailableRoot.GetProperty("missions").GetArrayLength(),
        "Unavailable payload leaked missions.");
}

static RuntimeAvailableMissionSnapshot Project(
    params RuntimeAvailableMissionCandidate[] candidates)
{
    return ProjectWithHistory(candidates);
}

static RuntimeAvailableMissionSnapshot ProjectWithHistory(
    IReadOnlyList<RuntimeAvailableMissionCandidate> candidates,
    IReadOnlyList<string>? finishedEvents = null,
    IReadOnlyList<string>? finishedMissions = null)
{
    return RuntimeAvailableMissionCapture.Project(
        new RuntimeAvailableMissionCaptureInput(
            Complete: true,
            MissionGeneration: 2,
            SourceRevision: 17,
            SourceMissionChangeVersion: 13,
            FinishedEvents: finishedEvents ?? Array.Empty<string>(),
            FinishedMissions: finishedMissions ?? Array.Empty<string>(),
            Candidates: candidates,
            Error: ""));
}

static RuntimeAvailableMissionCaptureInput Input(
    bool complete,
    params RuntimeAvailableMissionCandidate[] candidates)
{
    return new RuntimeAvailableMissionCaptureInput(
        Complete: complete,
        MissionGeneration: 2,
        SourceRevision: 17,
        SourceMissionChangeVersion: 13,
        FinishedEvents: Array.Empty<string>(),
        FinishedMissions: Array.Empty<string>(),
        Candidates: candidates,
        Error: complete ? "" : "fixture-incomplete");
}

static RuntimeAvailableMissionCandidate Candidate(
    string sourceEvent,
    string mission,
    string title = "任务标题",
    int triggerType = RuntimeAvailableMissionTriggerClassifier.KizunaCheckPointTrigger,
    string eligibility = RuntimeAvailableMissionCapture.EligibleDisposition,
    string referenceSource = RuntimeAvailableMissionSourceState.AfterPerformanceSource,
    string sourcePhase = RuntimeAvailableMissionTriggerClassifier.ScheduledPhase,
    bool definitionAvailable = true,
    bool hasReceiver = true,
    string receiver = "Meirin",
    string characterName = "红美铃",
    IReadOnlyList<string>? sceneNames = null,
    string presentationStatus = RuntimeMissionPresentation.ReadyStatus,
    int conditionCount = 1,
    IReadOnlyList<string>? preNodes = null,
    bool loopedMission = false,
    bool active = false,
    bool finished = false)
{
    return new RuntimeAvailableMissionCandidate(
        SourceEventLabel: sourceEvent,
        TriggerType: triggerType,
        EligibilityDisposition: eligibility,
        ReferenceSource: referenceSource,
        SourcePhase: sourcePhase,
        MissionLabel: mission,
        DefinitionAvailable: definitionAvailable,
        Title: title,
        HasReceiver: hasReceiver,
        ReceiverLabel: receiver,
        CharacterName: characterName,
        SceneNames: sceneNames ?? new[] { "红魔馆" },
        PresentationStatus: presentationStatus,
        DefinitionConditionCount: conditionCount,
        PreNodes: preNodes ?? Array.Empty<string>(),
        LoopedMission: loopedMission,
        Active: active,
        Finished: finished);
}

static void AssertUnavailable(
    RuntimeAvailableMissionSnapshot snapshot,
    string message)
{
    AssertFalse(snapshot.RuntimeAvailable, message);
    AssertEqual(
        RuntimeAvailableMissionSnapshot.RuntimeUnavailableStatus,
        snapshot.Status,
        $"{message} Status mismatch.");
    AssertEqual(0, snapshot.Missions.Count, $"{message} Partial missions leaked.");
}

static void AssertTrue(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertFalse(bool condition, string message)
{
    AssertTrue(!condition, message);
}

static void AssertEqual<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException(
            $"{message} Expected={expected}; Actual={actual}");
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
            + $"Actual=[{string.Join(",", actual)}]");
    }
}
