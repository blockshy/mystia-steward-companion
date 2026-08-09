using MystiaStewardCompanion.Save;

try
{
    AssertIndependentStateLifecycle();
    AssertEligibilityContracts();
    AssertEligibilityFailureClassification();
    AssertOpaqueExactIdentifierContracts();
    AssertSourceContracts();
    Console.WriteLine(
        "PASS: scheduled-event diagnostics preserve exact identities, reconstruct character-interact eligibility without side effects, and remain independently gated, bounded, exact-shape, and diagnostic-package only.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL: {ex}");
    return 1;
}

static void AssertIndependentStateLifecycle()
{
    var limits = new RuntimeScheduledEventDiagnosticLimits(
        MaxCaptureAttemptsPerStableWindow: 1,
        MaxScheduledBucketCount: 8,
        MaxEventsPerBucket: 4,
        MaxScheduledEventCount: 8,
        MaxPostMissionReferences: 8,
        MaxFinishedEventCount: 20_000,
        MaxFinishedMissionCount: 20_000,
        MaxLabelLength: 128);
    RuntimeScheduledEventDiagnosticBounds.ValidateCount(
        190,
        limits.MaxFinishedEventCount,
        "finished-events");
    RuntimeScheduledEventDiagnosticBounds.ValidateCount(
        98,
        limits.MaxFinishedMissionCount,
        "finished-missions");
    AssertThrows<InvalidOperationException>(
        () => RuntimeScheduledEventDiagnosticBounds.ValidateCount(
            20_001,
            limits.MaxFinishedEventCount,
            "finished-events"),
        "An oversized finished-event list was accepted.");
    var repeatedFinishedLabels =
        RuntimeScheduledEventDiagnosticBounds.BuildMembershipSet(
            new[] { "Repeatable_Mission", "Repeatable_Mission" },
            limits.MaxFinishedMissionCount,
            "finished-missions");
    AssertEqual(
        1,
        repeatedFinishedLabels.Count,
        "A legal repeated finished label was not retained as membership evidence.");
    var state = new RuntimeScheduledEventDiagnosticState(limits);
    AssertEqual(
        RuntimeScheduledEventDiagnosticPhase.Detached,
        state.Snapshot().Phase,
        "Fresh scheduled state was not detached.");

    var now = DateTime.UtcNow;
    state.SetReaderStatus("resolved", attached: true, now);
    state.ResetForMissionGeneration(1, ownerThreadId: 7, now.AddSeconds(1));
    AssertTrue(
        state.ArmMissionGeneration(1, ownerThreadId: 7, now.AddSeconds(2)),
        "Matching mission generation was not armed.");
    state.WaitForDayScene(
        1,
        daySceneGeneration: 3,
        ownerThreadId: 7,
        "day scene runtime not ready",
        now.AddSeconds(3));
    AssertTrue(
        state.TryBeginCapture(
            1,
            daySceneGeneration: 3,
            ownerThreadId: 7,
            now.AddSeconds(4),
            out var firstToken),
        "A ready independent generation did not begin capture.");

    var scheduledEvent = new RuntimeScheduledEventDiagnosticEntry(
        "Kizuna_Meirin_LV2_Upgrade_Event",
        Bucket: -1,
        BucketSource: "permanent",
        BucketOrdinal: 0,
        DefinitionExists: true,
        DefinitionAvailable: true,
        DefinitionStatus: "available",
        Finished: false,
        Disposition: "candidate",
        Reason: "",
        Trigger: null,
        Eligibility: RuntimeScheduledEventEligibility.Evaluate(
            RuntimeScheduledEventEligibility.OnTalkWithCharacterTrigger,
            "Meirin",
            eventFinished: false,
            kizunaEvidence: null),
        PostMissions: new[] { "DirectMission" },
        PostMissionsAfterPerformance: new[] { "PerformanceMission" });
    var directReference = MissionReference(
        "postMissions",
        "DirectMission",
        "candidate",
        preNodes: new[] { "DirectPrerequisite" },
        loopedMission: true);
    var performanceReference = MissionReference(
        "postMissionsAfterPerformance",
        "PerformanceMission",
        "skipped",
        reason: "mission-active",
        active: true);
    AssertTrue(
        state.TryCommitCapture(
            firstToken,
            new RuntimeScheduledMissionSourceReadResult(
                Complete: true,
                SourceMissionChangeVersion: 23,
                CorrectedDay: 55,
                ScheduledBucketCount: 2,
                ReadBucketCount: 1,
                FinishedEvents: Array.Empty<string>(),
                FinishedMissions: Array.Empty<string>(),
                Events: new[] { scheduledEvent },
                MissionReferences: new[] { directReference, performanceReference },
                CaptureElapsedMilliseconds: 4,
                Error: ""),
            now.AddSeconds(5)),
        "A matching capture was not committed.");

    var ready = state.Report();
    AssertEqual(
        RuntimeScheduledEventDiagnosticPhase.Ready,
        ready.Summary.Phase,
        "A complete capture was not marked ready.");
    AssertTrue(ready.Summary.CaptureComplete, "A complete capture was not published as complete.");
    AssertEqual(
        23L,
        ready.Summary.SourceMissionChangeVersion,
        "The capture did not preserve its source mission change version.");
    AssertEqual(1, ready.Summary.CandidateMissionReferenceCount, "Candidate count changed.");
    AssertEqual(1, ready.Summary.SkippedMissionReferenceCount, "Skipped count changed.");
    AssertEqual(1, ready.Summary.EligibleEventCount, "Eligible event count changed.");
    AssertEqual(
        0,
        ready.Summary.EligibilityFailureCount,
        "A valid event was counted as an eligibility failure.");
    AssertEqual(
        "postMissionsAfterPerformance",
        ready.MissionReferences[1].Source,
        "After-performance mission timing was merged with direct post missions.");
    AssertSequenceEqual(
        new[] { "DirectPrerequisite" },
        ready.MissionReferences[0].PreNodes,
        "Mission pre-node identities were not retained.");
    AssertTrue(
        ready.MissionReferences[0].LoopedMission,
        "The exact loopedMission marker was not retained.");
    AssertFalse(
        state.TryBeginCapture(
            1,
            daySceneGeneration: 3,
            ownerThreadId: 7,
            now.AddSeconds(6),
            out _),
        "A successful stable generation entered a hot recapture loop.");

    state.WaitForDayScene(
        1,
        daySceneGeneration: 3,
        ownerThreadId: 7,
        "scheduler entered an event",
        now.AddSeconds(7));
    AssertTrue(
        state.TryBeginCapture(
            1,
            daySceneGeneration: 3,
            ownerThreadId: 7,
            now.AddSeconds(8),
            out var secondToken),
        "A readiness loss did not open one new stable capture window.");
    AssertTrue(
        state.FailCapture(
            secondToken,
            "count-mismatch",
            captureElapsedMilliseconds: 2,
            now.AddSeconds(9)),
        "A matching capture failure was not recorded.");
    AssertEqual(
        RuntimeScheduledEventDiagnosticPhase.Unavailable,
        state.Snapshot().Phase,
        "A deterministic read failure did not fail closed.");
    AssertFalse(
        state.TryBeginCapture(
            1,
            daySceneGeneration: 3,
            ownerThreadId: 7,
            now.AddSeconds(10),
            out _),
        "A deterministic failure retried in the same stable window.");

    state.ResetForMissionGeneration(2, ownerThreadId: 7, now.AddSeconds(11));
    AssertTrue(
        state.ArmMissionGeneration(2, ownerThreadId: 7, now.AddSeconds(12)),
        "A new mission generation was not armed.");
    AssertFalse(
        state.TryCommitCapture(
            secondToken,
            new RuntimeScheduledMissionSourceReadResult(
                true,
                24,
                55,
                1,
                1,
                Array.Empty<string>(),
                Array.Empty<string>(),
                Array.Empty<RuntimeScheduledEventDiagnosticEntry>(),
                Array.Empty<RuntimeScheduledEventMissionReferenceDiagnostic>(),
                1,
                ""),
            now.AddSeconds(13)),
        "A stale capture token committed into a new mission generation.");
    AssertEqual(0, state.Report().Events.Count, "A generation reset retained stale events.");
}

static void AssertEligibilityFailureClassification()
{
    var limits = new RuntimeScheduledEventDiagnosticLimits(
        MaxCaptureAttemptsPerStableWindow: 1,
        MaxScheduledBucketCount: 8,
        MaxEventsPerBucket: 4,
        MaxScheduledEventCount: 8,
        MaxPostMissionReferences: 8,
        MaxFinishedEventCount: 20_000,
        MaxFinishedMissionCount: 20_000,
        MaxLabelLength: 128);
    var state = new RuntimeScheduledEventDiagnosticState(limits);
    var now = DateTime.UtcNow;
    state.SetReaderStatus("resolved", attached: true, now);
    state.ResetForMissionGeneration(1, ownerThreadId: 7, now.AddSeconds(1));
    AssertTrue(
        state.ArmMissionGeneration(1, ownerThreadId: 7, now.AddSeconds(2)),
        "Eligibility failure state did not arm.");
    state.WaitForDayScene(
        1,
        daySceneGeneration: 3,
        ownerThreadId: 7,
        "ready",
        now.AddSeconds(3));
    AssertTrue(
        state.TryBeginCapture(
            1,
            daySceneGeneration: 3,
            ownerThreadId: 7,
            now.AddSeconds(4),
            out var token),
        "Eligibility failure state did not begin capture.");

    var failedEligibility = RuntimeScheduledEventEligibility.Invalid(
        "eligibility-read-failed:InvalidOperationException:canonical-mismatch");
    var scheduledEvent = new RuntimeScheduledEventDiagnosticEntry(
        "Kizuna_Meirin_LV2_Upgrade_Event",
        Bucket: -1,
        BucketSource: "permanent",
        BucketOrdinal: 0,
        DefinitionExists: true,
        DefinitionAvailable: true,
        DefinitionStatus: "available",
        Finished: false,
        Disposition: "candidate",
        Reason: "",
        Trigger: new RuntimeScheduledEventTriggerDiagnostic(
            RuntimeScheduledEventEligibility.KizunaCheckPointTrigger,
            "KizunaCheckPoint",
            "Meirin",
            TimeDayType: 0,
            TimeDayTypeName: "",
            TimeCalculateType: 0,
            TimeCalculateTypeName: "",
            TimeDay: 0,
            TimeRangeMinimum: 0,
            TimeRangeMaximum: 0),
        Eligibility: failedEligibility,
        PostMissions: Array.Empty<string>(),
        PostMissionsAfterPerformance: new[]
        {
            "Kizuna_Meirin_LV2_Upgrade_Mission",
        });
    var missionReference = MissionReference(
        "postMissionsAfterPerformance",
        "Kizuna_Meirin_LV2_Upgrade_Mission",
        "candidate") with
    {
        SourceEventEligibilityDisposition = failedEligibility.Disposition,
        SourceEventEligibilityReason = failedEligibility.Reason,
    };
    var failedResult = new RuntimeScheduledMissionSourceReadResult(
        Complete: false,
        SourceMissionChangeVersion: 23,
        CorrectedDay: 55,
        ScheduledBucketCount: 2,
        ReadBucketCount: 1,
        FinishedEvents: Array.Empty<string>(),
        FinishedMissions: Array.Empty<string>(),
        Events: new[] { scheduledEvent },
        MissionReferences: new[] { missionReference },
        CaptureElapsedMilliseconds: 4,
        Error: "invalid-events=0; invalid-eligibility=1; invalid-mission-references=0");
    AssertThrows<InvalidOperationException>(
        () => state.TryCommitCapture(
            token,
            failedResult with
            {
                Complete = true,
                Error = "",
            },
            now.AddSeconds(5)),
        "An invalid eligibility result was accepted as a complete capture.");
    AssertTrue(
        state.TryCommitCapture(
            token,
            failedResult,
            now.AddSeconds(6)),
        "Eligibility failure state did not commit its fail-closed report.");

    var report = state.Report();
    AssertEqual(
        RuntimeScheduledEventDiagnosticPhase.Unavailable,
        report.Summary.Phase,
        "An eligibility read failure did not fail the report closed.");
    AssertEqual(
        0,
        report.Summary.EventDefinitionFailureCount,
        "An eligibility read failure was misclassified as an event definition failure.");
    AssertEqual(
        1,
        report.Summary.EligibilityFailureCount,
        "An eligibility read failure was not counted separately.");
    AssertEqual(
        1,
        report.Summary.CandidateMissionReferenceCount,
        "An eligibility read failure erased structural mission evidence.");
    AssertEqual(
        "Kizuna_Meirin_LV2_Upgrade_Mission",
        report.Events[0].PostMissionsAfterPerformance.Single(),
        "An eligibility read failure erased the post-mission chain.");
}

static RuntimeScheduledEventMissionReferenceDiagnostic MissionReference(
    string source,
    string missionLabel,
    string disposition,
    string reason = "",
    bool active = false,
    IReadOnlyList<string>? preNodes = null,
    bool loopedMission = false)
{
    return new RuntimeScheduledEventMissionReferenceDiagnostic(
        EventLabel: "Kizuna_Meirin_LV2_Upgrade_Event",
        EventBucket: -1,
        Source: source,
        SourceOrdinal: 0,
        MissionLabel: missionLabel,
        DefinitionExists: true,
        DefinitionAvailable: true,
        DefinitionStatus: "available",
        Title: missionLabel,
        TitleStatus: "available",
        HasReceiver: false,
        Receiver: "",
        DefinitionConditionCount: 0,
        Active: active,
        Finished: false,
        SourceEventEligibilityDisposition: "eligible",
        SourceEventEligibilityReason: "on-talk-with-character",
        Disposition: disposition,
        Reason: reason,
        PreNodes: preNodes ?? Array.Empty<string>(),
        LoopedMission: loopedMission);
}

static void AssertEligibilityContracts()
{
    AssertEqual(
        "eligible",
        RuntimeScheduledEventEligibility.Evaluate(
            RuntimeScheduledEventEligibility.OnEnterDaySceneMapTrigger,
            "HakureiShrine",
            eventFinished: false,
            kizunaEvidence: null).Disposition,
        "A map-entry trigger with an exact map identity was not eligible.");
    AssertEqual(
        "on-enter-day-scene-map",
        RuntimeScheduledEventEligibility.Evaluate(
            RuntimeScheduledEventEligibility.OnEnterDaySceneMapTrigger,
            " ",
            eventFinished: false,
            kizunaEvidence: null).Reason,
        "An opaque whitespace map identity was normalized.");
    AssertEqual(
        "ineligible",
        RuntimeScheduledEventEligibility.Evaluate(
            RuntimeScheduledEventEligibility.OnEnterDaySceneMapTrigger,
            "",
            eventFinished: false,
            kizunaEvidence: null).Disposition,
        "A map-entry trigger without a map identity was accepted.");
    AssertEqual(
        "eligible",
        RuntimeScheduledEventEligibility.Evaluate(
            RuntimeScheduledEventEligibility.OnEnterDaySceneTrigger,
            triggerId: null,
            eventFinished: false,
            kizunaEvidence: null).Disposition,
        "A day-scene entry trigger incorrectly required a trigger identity.");
    AssertEqual(
        "excluded",
        RuntimeScheduledEventEligibility.Evaluate(
            RuntimeScheduledEventEligibility.OnEnterDaySceneTrigger,
            triggerId: null,
            eventFinished: true,
            kizunaEvidence: null).Disposition,
        "A finished day-scene entry event was not excluded.");

    var onTalk = RuntimeScheduledEventEligibility.Evaluate(
        RuntimeScheduledEventEligibility.OnTalkWithCharacterTrigger,
        "Meirin",
        eventFinished: false,
        kizunaEvidence: null);
    AssertEqual("eligible", onTalk.Disposition, "A valid on-talk trigger was not eligible.");
    AssertEqual(
        "on-talk-with-character",
        onTalk.Reason,
        "The on-talk eligibility reason changed.");
    AssertEqual(
        "eligible",
        RuntimeScheduledEventEligibility.Evaluate(
            RuntimeScheduledEventEligibility.OnTalkWithCharacterTrigger,
            " ",
            eventFinished: false,
            kizunaEvidence: null).Disposition,
        "An opaque whitespace trigger identity was normalized.");
    AssertEqual(
        "ineligible",
        RuntimeScheduledEventEligibility.Evaluate(
            RuntimeScheduledEventEligibility.OnTalkWithCharacterTrigger,
            "",
            eventFinished: false,
            kizunaEvidence: null).Disposition,
        "An empty on-talk trigger identity was accepted.");
    AssertEqual(
        "not-applicable",
        RuntimeScheduledEventEligibility.Evaluate(
            triggerType: 19,
            "Meirin",
            eventFinished: false,
            kizunaEvidence: null).Disposition,
        "A non-character-interact trigger was treated as eligible.");
    AssertEqual(
        "excluded",
        RuntimeScheduledEventEligibility.Evaluate(
            RuntimeScheduledEventEligibility.KizunaCheckPointTrigger,
            "Meirin",
            eventFinished: true,
            kizunaEvidence: null).Disposition,
        "A finished event was not excluded before Kizuna reads.");

    var unresolved = KizunaEvidence(
        identityResolved: false,
        runtimeGuestId: null,
        canonicalCharacterId: null,
        characterIsSpecial: null,
        recorded: null,
        level: null,
        exp: null,
        level5Gate: null);
    AssertEligibility(
        unresolved,
        "ineligible",
        "character-identity-unresolved");
    AssertEligibility(
        KizunaEvidence(
            identityResolved: true,
            runtimeGuestId: null,
            canonicalCharacterId: 10,
            characterIsSpecial: false,
            recorded: null,
            level: null,
            exp: null,
            level5Gate: null),
        "ineligible",
        "character-not-special");
    AssertEligibility(
        KizunaEvidence(
            identityResolved: true,
            runtimeGuestId: 10,
            canonicalCharacterId: 10,
            characterIsSpecial: true,
            recorded: false,
            level: null,
            exp: null,
            level5Gate: null),
        "ineligible",
        "kizuna-not-recorded");

    var thresholds = new[]
    {
        (Level: 1, Exp: 6),
        (Level: 2, Exp: 17),
        (Level: 3, Exp: 30),
    };
    foreach (var threshold in thresholds)
    {
        var exact = RecordedKizuna(
            threshold.Level,
            threshold.Exp,
            level5Gate: null);
        var result = AssertEligibility(exact, "eligible", "kizuna-checkpoint");
        AssertEqual(
            threshold.Exp,
            result.RequiredBondExp,
            $"Level {threshold.Level} required EXP changed.");
        AssertEligibility(
            RecordedKizuna(
                threshold.Level,
                threshold.Exp - 1,
                level5Gate: null),
            "ineligible",
            "bond-exp-not-full");
        AssertEligibility(
            RecordedKizuna(
                threshold.Level,
                threshold.Exp + 1,
                level5Gate: null),
            "ineligible",
            "bond-exp-not-full");
    }

    AssertEligibility(
        RecordedKizuna(level: 4, exp: 45, level5Gate: false),
        "ineligible",
        "level-5-event-gate-closed");
    var levelFour = AssertEligibility(
        RecordedKizuna(level: 4, exp: 45, level5Gate: true),
        "eligible",
        "kizuna-checkpoint");
    AssertEqual(45, levelFour.RequiredBondExp, "Level 4 required EXP changed.");
    AssertEligibility(
        RecordedKizuna(level: 0, exp: 0, level5Gate: null),
        "ineligible",
        "bond-level-unsupported");
    AssertEligibility(
        RecordedKizuna(level: 5, exp: 0, level5Gate: null),
        "ineligible",
        "bond-level-unsupported");

    AssertEqual<int?>(
        6,
        RuntimeScheduledEventEligibility.RequiredBondExp(1),
        "Level 1 threshold changed.");
    AssertEqual<int?>(
        17,
        RuntimeScheduledEventEligibility.RequiredBondExp(2),
        "Level 2 threshold changed.");
    AssertEqual<int?>(
        30,
        RuntimeScheduledEventEligibility.RequiredBondExp(3),
        "Level 3 threshold changed.");
    AssertEqual<int?>(
        45,
        RuntimeScheduledEventEligibility.RequiredBondExp(4),
        "Level 4 threshold changed.");
    AssertEqual<int?>(
        null,
        RuntimeScheduledEventEligibility.RequiredBondExp(5),
        "An unsupported level gained a threshold.");

    AssertThrows<InvalidOperationException>(
        () => AssertEligibility(
            RecordedKizuna(level: -1, exp: 0, level5Gate: null),
            "ineligible",
            "bond-level-unsupported"),
        "A negative bond level did not fail closed.");
    AssertThrows<InvalidOperationException>(
        () => AssertEligibility(
            RecordedKizuna(level: 1, exp: -1, level5Gate: null),
            "ineligible",
            "bond-exp-not-full"),
        "A negative bond EXP did not fail closed.");
    AssertThrows<InvalidOperationException>(
        () => RuntimeScheduledEventEligibility.Evaluate(
            RuntimeScheduledEventEligibility.KizunaCheckPointTrigger,
            "Meirin",
            eventFinished: false,
            kizunaEvidence: null),
        "A Kizuna trigger without exact evidence was accepted.");
    AssertThrows<InvalidOperationException>(
        () => RuntimeScheduledEventEligibility.Evaluate(
            RuntimeScheduledEventEligibility.OnTalkWithCharacterTrigger,
            "Meirin",
            eventFinished: false,
            kizunaEvidence: unresolved),
        "An on-talk trigger accepted unrelated Kizuna evidence.");
    AssertThrows<ArgumentException>(
        () => RuntimeScheduledEventEligibility.Invalid(""),
        "An empty eligibility failure reason was accepted.");
}

static RuntimeScheduledEventEligibilityDiagnostic AssertEligibility(
    RuntimeScheduledEventKizunaEvidence evidence,
    string expectedDisposition,
    string expectedReason)
{
    var result = RuntimeScheduledEventEligibility.Evaluate(
        RuntimeScheduledEventEligibility.KizunaCheckPointTrigger,
        "Meirin",
        eventFinished: false,
        evidence);
    AssertEqual(
        expectedDisposition,
        result.Disposition,
        "Kizuna eligibility disposition changed.");
    AssertEqual(
        expectedReason,
        result.Reason,
        "Kizuna eligibility reason changed.");
    return result;
}

static RuntimeScheduledEventKizunaEvidence RecordedKizuna(
    int level,
    int exp,
    bool? level5Gate)
{
    return KizunaEvidence(
        identityResolved: true,
        runtimeGuestId: 10,
        canonicalCharacterId: 10,
        characterIsSpecial: true,
        recorded: true,
        level,
        exp,
        level5Gate);
}

static RuntimeScheduledEventKizunaEvidence KizunaEvidence(
    bool identityResolved,
    int? runtimeGuestId,
    int? canonicalCharacterId,
    bool? characterIsSpecial,
    bool? recorded,
    int? level,
    int? exp,
    bool? level5Gate)
{
    return new RuntimeScheduledEventKizunaEvidence(
        identityResolved,
        runtimeGuestId,
        canonicalCharacterId,
        characterIsSpecial,
        recorded,
        level,
        exp,
        level5Gate);
}

static void AssertOpaqueExactIdentifierContracts()
{
    const int maximumLength = 8;
    var historyValues = new object?[]
    {
        "X",
        "X ",
        " X",
        "X ",
        "",
        "   ",
    };
    var historyLabels = historyValues
        .Select((value, index) =>
            RuntimeScheduledEventDiagnosticIdentity.ReadHistoryLabel(
                value,
                maximumLength,
                "finished-events",
                index))
        .ToArray();
    AssertSequenceEqual(
        new[] { "X", "X ", " X", "X ", "", "   " },
        historyLabels,
        "Finished history identities were normalized or reordered.");

    var historyMembership = RuntimeScheduledEventDiagnosticBounds.BuildMembershipSet(
        historyLabels,
        maximumCount: 8,
        "finished-events");
    AssertEqual(
        5,
        historyMembership.Count,
        "Exact duplicate history identities were not collapsed as membership evidence.");
    AssertTrue(historyMembership.Contains("X"), "The unpadded identity was lost.");
    AssertTrue(historyMembership.Contains("X "), "The trailing-space identity was normalized.");
    AssertTrue(historyMembership.Contains(" X"), "The leading-space identity was normalized.");
    AssertTrue(historyMembership.Contains(""), "An exact empty history identity was rejected.");
    AssertTrue(historyMembership.Contains("   "), "A whitespace history identity was normalized.");
    AssertFalse(
        historyMembership.Contains("X  "),
        "History membership introduced a normalized or aliased identity.");
    AssertEqual(
        "Main_HumanVillage_006-Event ",
        RuntimeScheduledEventDiagnosticIdentity.ReadHistoryLabel(
            "Main_HumanVillage_006-Event ",
            maximumLength: 128,
            "finished-events",
            53),
        "The observed trailing-space game identity was rejected or normalized.");

    AssertEqual(
        " Node ",
        RuntimeScheduledEventDiagnosticIdentity.ReadNodeLabel(
            " Node ",
            maximumLength,
            "scheduled-events-bucket--1",
            2),
        "A node identity was trimmed.");
    AssertEqual(
        "   ",
        RuntimeScheduledEventDiagnosticIdentity.ReadNodeLabel(
            "   ",
            maximumLength,
            "scheduled-events-bucket--1",
            3),
        "A whitespace node identity was normalized.");
    AssertEqual(
        "12345678",
        RuntimeScheduledEventDiagnosticIdentity.ReadNodeLabel(
            "12345678",
            maximumLength,
            "scheduled-events-bucket--1",
            4),
        "A node identity at the exact length limit was rejected.");

    AssertEqual<string?>(
        null,
        RuntimeScheduledEventDiagnosticIdentity.ReadOptionalIdentifier(
            null,
            maximumLength,
            "scheduled-event-trigger-id-bucket--1",
            0),
        "A null optional trigger identity was converted to a string.");
    AssertEqual(
        "",
        RuntimeScheduledEventDiagnosticIdentity.ReadOptionalIdentifier(
            "",
            maximumLength,
            "scheduled-event-trigger-id-bucket--1",
            1),
        "An empty optional trigger identity was rejected.");
    AssertEqual(
        " Meirin ",
        RuntimeScheduledEventDiagnosticIdentity.ReadOptionalIdentifier(
            " Meirin ",
            maximumLength,
            "scheduled-event-trigger-id-bucket--1",
            2),
        "An optional trigger identity was trimmed.");
    AssertEqual(
        "   ",
        RuntimeScheduledEventDiagnosticIdentity.ReadOptionalIdentifier(
            "   ",
            maximumLength,
            "scheduled-event-trigger-id-bucket--1",
            3),
        "A whitespace optional trigger identity was normalized.");

    var nullableTrigger = new RuntimeScheduledEventTriggerDiagnostic(
        TriggerType: 1,
        TriggerTypeName: "OnTalkWithCharacter",
        TriggerId: null,
        TimeDayType: 0,
        TimeDayTypeName: "None",
        TimeCalculateType: 0,
        TimeCalculateTypeName: "None",
        TimeDay: 0,
        TimeRangeMinimum: 0,
        TimeRangeMaximum: 0);
    AssertEqual<string?>(
        null,
        nullableTrigger.TriggerId,
        "The diagnostic contract did not retain a null trigger identity.");

    AssertThrowsMessage<InvalidOperationException>(
        () => RuntimeScheduledEventDiagnosticIdentity.ReadHistoryLabel(
            null,
            maximumLength,
            "finished-events",
            5),
        "opaque-identifier-invalid:source=finished-events; index=5; reason=null; length=-1; limit=8",
        "A null history identity did not produce a structured error.");
    AssertThrowsMessage<InvalidOperationException>(
        () => RuntimeScheduledEventDiagnosticIdentity.ReadHistoryLabel(
            42,
            maximumLength,
            "finished-events",
            6),
        "opaque-identifier-invalid:source=finished-events; index=6; reason=type; length=-1; limit=8; actualType=System.Int32",
        "A non-string history identity did not produce a structured error.");
    AssertThrowsMessage<InvalidOperationException>(
        () => RuntimeScheduledEventDiagnosticIdentity.ReadHistoryLabel(
            "123456789",
            maximumLength,
            "finished-events",
            7),
        "opaque-identifier-invalid:source=finished-events; index=7; reason=overlong; length=9; limit=8",
        "An overlong history identity was accepted.");
    AssertThrowsMessage<InvalidOperationException>(
        () => RuntimeScheduledEventDiagnosticIdentity.ReadNodeLabel(
            "",
            maximumLength,
            "scheduled-events-bucket--1",
            8),
        "opaque-identifier-invalid:source=scheduled-events-bucket--1; index=8; reason=empty; length=0; limit=8",
        "An empty node identity was accepted.");
    AssertThrowsMessage<InvalidOperationException>(
        () => RuntimeScheduledEventDiagnosticIdentity.ReadOptionalIdentifier(
            "123456789",
            maximumLength,
            "scheduled-event-trigger-id-bucket--1",
            9),
        "opaque-identifier-invalid:source=scheduled-event-trigger-id-bucket--1; index=9; reason=overlong; length=9; limit=8",
        "An overlong trigger identity was accepted.");
}

static void AssertSourceContracts()
{
    var root = FindRepositoryRoot(AppContext.BaseDirectory);
    var capture = File.ReadAllText(Path.Combine(
        root,
        "mods",
        "bepinex",
        "src",
        "Save",
        "RuntimeScheduledEventDiagnosticCapture.cs"));
    var sourceReader = File.ReadAllText(Path.Combine(
        root,
        "mods",
        "bepinex",
        "src",
        "Save",
        "RuntimeScheduledMissionSourceReader.cs"));
    capture += sourceReader;
    var eligibility = File.ReadAllText(Path.Combine(
        root,
        "mods",
        "bepinex",
        "src",
        "Save",
        "RuntimeScheduledEventEligibility.cs"));
    var collections = File.ReadAllText(Path.Combine(
        root,
        "mods",
        "bepinex",
        "src",
        "Save",
        "RuntimeConcreteCollectionReader.cs"));
    var missionCapture = File.ReadAllText(Path.Combine(
        root,
        "mods",
        "bepinex",
        "src",
        "Save",
        "RuntimeMissionDiagnosticCapture.cs"));
    var localApi = File.ReadAllText(Path.Combine(
        root,
        "mods",
        "bepinex",
        "src",
        "LocalApi",
        "LocalApiServer.cs"));
    var controller = File.ReadAllText(Path.Combine(
        root,
        "mods",
        "bepinex",
        "src",
        "Ui",
        "StewardOverlayController.cs"));

    RequireContains(
        capture,
        "GameData.RunTime.Common.RunTimeScheduler",
        "GameData.RunTime.Common.RunTimePlayerData",
        "GameData.RunTime.Common.GameDate",
        "GameData.Core.Collections.DataBaseScheduler",
        "GameData.Profile.SchedulerNodeCollection.EventNode",
        "\"scheduledEvents\"",
        "\"finishedEvents\"",
        "\"finishedMissions\"",
        "\"allNPCs\"",
        "\"RecordedSpecialNPCs\"",
        "\"CurrentBondLevel\"",
        "\"CurrentBondExp\"",
        "\"ShouldHaveLevel5KizunaEvent\"",
        "\"characterId\"",
        "\"identity\"",
        "\"CorrectedDay\"",
        "\"TargetNodeExists\"",
        "\"RefEvent\"",
        "\"RefMission\"",
        "\"preNodes\"",
        "\"loopedMission\"",
        "\"scheduledEvent\"",
        "\"triggerType\"",
        "\"triggerId\"",
        "\"time\"",
        "\"postMissions\"",
        "\"postMissionsAfterPerformance\"",
        "RuntimeConcreteCollectionReader.TryGetDictionaryValue",
        "RuntimeMappedGuestCatalog.TryGetLoadedSnapshot",
        "_mappedGuestSnapshot.Entries",
        "RuntimeSchedulerCharacterIdentity.IsNormal",
        "RuntimeScheduledEventEligibility.Evaluate",
        "RuntimeScheduledEventEligibility.Invalid",
        "\"eligibility-read-failed:",
        "RuntimeConcreteCollectionReader.TryReadStringArray",
        "RuntimeScheduledEventDiagnosticIdentity.ReadHistoryLabel",
        "RuntimeScheduledEventDiagnosticIdentity.ReadNodeLabel",
        "RuntimeScheduledEventDiagnosticIdentity.ReadOptionalIdentifier",
        "RuntimeScheduledEventDiagnosticBounds.BuildMembershipSet",
        "finishedEvents.SequenceEqual(finalFinishedEvents, StringComparer.Ordinal)",
        "finishedMissions.SequenceEqual(finalFinishedMissions, StringComparer.Ordinal)",
        "RuntimeMissionDefinitionDiagnosticReader.Read",
        "RuntimeSceneReadinessCapture.CanReadDaySceneRuntime()",
        "stateBefore.Phase is RuntimeScheduledEventDiagnosticPhase.Ready",
        "SourceMissionChangeVersion",
        "missionAfter.ChangeVersion != missionBefore.ChangeVersion",
        "MaxCaptureAttemptsPerStableWindow: 1",
        "MaxScheduledBucketCount: 4096",
        "MaxEventsPerBucket: 2048",
        "MaxScheduledEventCount: 4096",
        "MaxPostMissionReferences: 4096",
        "MaxFinishedEventCount: 20_000",
        "MaxFinishedMissionCount: 20_000",
        "\"candidate\"",
        "\"skipped\"",
        "\"invalid\"");
    RequireAbsent(
        capture,
        "TryReadDictionary(",
        "CanContinue",
        "StartMission",
        "CheckCharacterInteractEvent",
        "CheckCharacterInteractMission",
        "GetAvailableInteractMissionForCharacter",
        "TryTrigger",
        "TryGetEventListAtDay",
        "HaveEventScheduled",
        "GetAllNodes",
        "AllNodesMapping",
        "FindUnityObject",
        "FindObjectsOfType",
        "UpdateFinishStates",
        "HasFulfilled",
        "GetMissionData",
        "FailCurrentGeneration",
        "FailInitialization",
        "TryCommitInitialization",
        "ValidateLabel(",
        ".Trim(",
        "TrimStart(",
        "TrimEnd(",
        "IsNullOrWhiteSpace(",
        "StringComparison.OrdinalIgnoreCase",
        "ByRuntimeStringId",
        "HasSpecialNPCKizunaExpFull",
        "RefOrGenerateSpecialRunTimeData",
        "GetOrGenerateSpecialNPCKizunaLevel",
        "RefNPC",
        ".ToUpper(",
        ".ToLower(",
        ".ToUpperInvariant(",
        ".ToLowerInvariant(");
    RequireContains(
        sourceReader,
        "public static RuntimeScheduledMissionSourceReadResult ReadFresh(",
        "public static bool TryResolve(out string failure)",
        "RuntimeConcreteCollectionReader.TryReadStringArray(",
        "RuntimeScheduledEventDiagnosticIdentity.ReadNodeLabel(",
        "RequireExactInstanceProperty(",
        "missionNodeType,",
        "\"preNodes\"",
        "\"loopedMission\"");
    RequireAbsent(
        sourceReader,
        "RuntimeScheduledEventDiagnosticState",
        "CanContinue",
        "StartMission",
        "CheckCharacterInteractEvent");
    RequireContains(
        eligibility,
        "OnTalkWithCharacterTrigger = 3",
        "KizunaCheckPointTrigger = 5",
        "1 => 6",
        "2 => 17",
        "3 => 30",
        "4 => 45",
        "kizunaEvidence.CurrentBondExp != requiredBondExp.Value",
        "kizunaEvidence.Level5Gate != true",
        "\"eligible\"",
        "\"ineligible\"",
        "\"not-applicable\"",
        "\"excluded\"");
    RequireAbsent(
        eligibility,
        ".Trim(",
        "IsNullOrWhiteSpace(",
        "OrdinalIgnoreCase",
        "HasSpecialNPCKizunaExpFull",
        "RefOrGenerateSpecialRunTimeData",
        "CheckCharacterInteractEvent");
    RequireContains(
        collections,
        "Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStringArray",
        "TryReadStringArray(",
        "\"Length\"",
        "\"get_Item\"",
        "typeof(string)");
    RequireContains(
        missionCapture,
        "RuntimeScheduledEventDiagnosticCapture.ResetForMissionGeneration(",
        "RuntimeScheduledEventDiagnosticCapture.ArmMissionGeneration(");
    RequireContains(
        controller,
        "RuntimeScheduledEventDiagnosticCapture.Tick(_mainThreadId);");
    RequireContains(
        localApi,
        "\"snapshot/runtime-scheduled-event-diagnostic.json\"",
        "ToJson(RuntimeScheduledEventDiagnosticCapture.Report())");

    var eventExists = capture.IndexOf("shape.TargetNodeExists.Invoke(", StringComparison.Ordinal);
    var eventRead = capture.IndexOf("shape.RefEvent.Invoke(", StringComparison.Ordinal);
    if (eventExists < 0 || eventRead <= eventExists)
    {
        throw new InvalidOperationException("RefEvent is not guarded by TargetNodeExists.");
    }
    var startMetadataMethod = sourceReader.IndexOf(
        "private static MissionStartMetadataReadResult ReadMissionStartMetadata(",
        StringComparison.Ordinal);
    var missionExists = sourceReader.IndexOf(
        "if (shape.TargetNodeExists.Invoke(",
        startMetadataMethod,
        StringComparison.Ordinal);
    var missionRead = sourceReader.IndexOf(
        "var mission = shape.RefMission.Invoke(",
        missionExists,
        StringComparison.Ordinal);
    var preNodesRead = sourceReader.IndexOf(
        "shape.PreNodes.GetValue(mission)",
        missionRead,
        StringComparison.Ordinal);
    var loopedMissionRead = sourceReader.IndexOf(
        "shape.LoopedMission.GetValue(mission)",
        preNodesRead,
        StringComparison.Ordinal);
    if (startMetadataMethod < 0
        || missionExists <= startMetadataMethod
        || missionRead <= missionExists
        || preNodesRead <= missionRead
        || loopedMissionRead <= preNodesRead)
    {
        throw new InvalidOperationException(
            "Mission start metadata is not read through guarded RefMission, exact preNodes, and loopedMission members.");
    }
    var postMissionsRead = capture.IndexOf(
        "var postMissions = ReadPostMissionArray(",
        StringComparison.Ordinal);
    var postPerformanceRead = capture.IndexOf(
        "var postMissionsAfterPerformance = ReadPostMissionArray(",
        postMissionsRead,
        StringComparison.Ordinal);
    var eligibilityRead = capture.IndexOf(
        "var eligibility = ReadEligibility(",
        postPerformanceRead,
        StringComparison.Ordinal);
    if (postMissionsRead < 0
        || postPerformanceRead <= postMissionsRead
        || eligibilityRead <= postPerformanceRead)
    {
        throw new InvalidOperationException(
            "Eligibility reads can erase already available post-mission structural evidence.");
    }
    var npcLookup = capture.IndexOf(
        "var npc = ReadRequiredDictionaryValue(",
        StringComparison.Ordinal);
    var specialIdentity = capture.IndexOf(
        "var isSpecial = !RuntimeSchedulerCharacterIdentity.IsNormal(boxedIdentity);",
        npcLookup,
        StringComparison.Ordinal);
    var mappedIdentityLookup = capture.IndexOf(
        "var matchingIdentities = _mappedGuestSnapshot.Entries",
        specialIdentity,
        StringComparison.Ordinal);
    var canonicalIdentityCheck = capture.IndexOf(
        "if (characterId != mappedIdentity.SourceGuestId.Value)",
        mappedIdentityLookup,
        StringComparison.Ordinal);
    var recordedKizunaRead = capture.IndexOf(
        "EnsureRecordedSpecialNpcs();",
        canonicalIdentityCheck,
        StringComparison.Ordinal);
    if (npcLookup < 0
        || specialIdentity <= npcLookup
        || mappedIdentityLookup <= specialIdentity
        || canonicalIdentityCheck <= mappedIdentityLookup
        || recordedKizunaRead <= canonicalIdentityCheck)
    {
        throw new InvalidOperationException(
            "Kizuna state is read before normal-character and canonical-identity gates.");
    }
    var currentBucket = capture.IndexOf(
        "correctedDay,\n            \"current-day\"",
        StringComparison.Ordinal);
    var permanentBucket = capture.IndexOf(
        "PermanentBucket,\n            \"permanent\"",
        StringComparison.Ordinal);
    if (currentBucket < 0 || permanentBucket <= currentBucket)
    {
        throw new InvalidOperationException(
            "Scheduled capture does not read only the current-day and permanent buckets in order.");
    }

    var tick = capture.IndexOf(
        "public static void Tick(int unityMainThreadId)",
        StringComparison.Ordinal);
    var terminalExit = capture.IndexOf(
        "stateBefore.Phase is RuntimeScheduledEventDiagnosticPhase.Ready",
        tick,
        StringComparison.Ordinal);
    var missionSnapshot = capture.IndexOf(
        "var missionBefore = RuntimeMissionDiagnosticCapture.Snapshot();",
        tick,
        StringComparison.Ordinal);
    var readinessRead = capture.IndexOf(
        "RuntimeSceneReadinessCapture.CanReadDaySceneRuntime()",
        tick,
        StringComparison.Ordinal);
    var mappedSnapshot = capture.IndexOf(
        "RuntimeMappedGuestCatalog.TryGetLoadedSnapshot(",
        tick,
        StringComparison.Ordinal);
    var beginCapture = capture.IndexOf(
        "State.TryBeginCapture(",
        tick,
        StringComparison.Ordinal);
    if (tick < 0
        || terminalExit <= tick
        || missionSnapshot <= terminalExit
        || readinessRead <= terminalExit
        || mappedSnapshot <= readinessRead
        || beginCapture <= mappedSnapshot)
    {
        throw new InvalidOperationException(
            "Scheduled capture does not wait for mission, day, and mapped identity readiness in order.");
    }
    AssertEqual(
        1,
        CountOccurrences(
            capture,
            "if (Environment.CurrentManagedThreadId != missionSnapshot.OwnerThreadId)"),
        "The capture contains a duplicated Unity owner-thread gate.");
    AssertEqual(
        2,
        CountOccurrences(sourceReader, "ReadCorrectedDay(shape)"),
        "The fresh reader does not verify corrected day before and after native reads.");
    AssertEqual(
        2,
        CountOccurrences(capture, "new object?[] { seed.Label }"),
        "TargetNodeExists and RefEvent do not share the original scheduled identity.");
}

static string FindRepositoryRoot(string startPath)
{
    var current = new DirectoryInfo(startPath);
    while (current != null)
    {
        if (File.Exists(Path.Combine(current.FullName, "package.json"))
            && Directory.Exists(Path.Combine(current.FullName, "mods", "bepinex")))
        {
            return current.FullName;
        }
        current = current.Parent;
    }
    throw new DirectoryNotFoundException("Repository root was not found.");
}

static void RequireContains(string source, params string[] values)
{
    foreach (var value in values)
    {
        if (!source.Contains(value, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Required source contract is missing: {value}");
        }
    }
}

static void RequireAbsent(string source, params string[] values)
{
    foreach (var value in values)
    {
        if (source.Contains(value, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Forbidden source contract is present: {value}");
        }
    }
}

static int CountOccurrences(string source, string value)
{
    var count = 0;
    var offset = 0;
    while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
    {
        count++;
        offset += value.Length;
    }
    return count;
}

static void AssertEqual<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException(
            $"{message} Expected '{expected}', actual '{actual}'.");
    }
}

static void AssertSequenceEqual<T>(
    IReadOnlyList<T> expected,
    IReadOnlyList<T> actual,
    string message)
{
    if (!expected.SequenceEqual(actual))
    {
        throw new InvalidOperationException(
            $"{message} Expected '{string.Join(", ", expected)}', "
            + $"actual '{string.Join(", ", actual)}'.");
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

static void AssertThrowsMessage<TException>(
    Action action,
    string expectedMessage,
    string message)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException ex)
    {
        AssertEqual(expectedMessage, ex.Message, message);
        return;
    }
    throw new InvalidOperationException(message);
}
