using System.Text.Json;
using System.Text.Json.Serialization;
using MystiaStewardCompanion.LocalApi;
using MystiaStewardCompanion.Save;

try
{
    TestSupportedProjection();
    TestStrictCandidateBoundary();
    TestNativeStartMissionGates();
    TestDuplicateMissionRules();
    TestUnavailableAndStateIsolation();
    TestStableApiPayload();
    Console.WriteLine(
        "PASS: available missions use strict type-5 after-performance eligibility, "
        + "native start gates, deterministic deduplication, and stable payload signatures.");
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
            referenceSource: "postMissions",
            sourceEvent: "Immediate_Event",
            mission: "Immediate_Mission"));
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
    AssertUnavailable(
        Project(Candidate(
            sourceEvent: "Missing_Receiver_Event",
            mission: "Missing_Receiver_Mission",
            hasReceiver: false)),
        "A missing receiver did not fail closed.");
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

    var unavailable = RuntimeAvailableMissionSnapshot.Unavailable(4, 9, "scene-not-ready");
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
            DaySceneGeneration: 7,
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
        DaySceneGeneration: 7,
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
    int triggerType = RuntimeAvailableMissionCapture.SupportedTriggerType,
    string eligibility = RuntimeAvailableMissionCapture.EligibleDisposition,
    string referenceSource = RuntimeAvailableMissionCapture.SupportedReferenceSource,
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
