using MystiaStewardCompanion.Save;

try
{
    VerifySameGenerationCanAdoptFinalResult();
    VerifyControllerReuseNeverProducesASideEffect();
    VerifyTwoMissingObservationsInterruptTheJob();
    VerifyProgressStallBlocksTheJob();
    VerifyNativeFinalizeWaitNeverRequestsASideEffect();
    VerifyManualHandoffSuspendsCookingSideEffects();
    VerifyThreeUnreadableObservationsBlockTheJob();
    VerifyAlternatingOwnershipFailuresAreBounded();
    VerifyRegressiveProgressDoesNotCountAsProgress();
    VerifyNormalWaitingIsNotReportedAsProgressOrFailure();
    VerifyPausedIntervalsDoNotConsumeStallBudget();
    VerifySuspensionDoesNotConsumeOwnershipFailures();
    VerifyEffectiveDeliveryClockExcludesUnavailableIntervals();
    VerifyUncertainCommitCannotBeRetriedOrCleaned();
    VerifyDefiniteNonCommitCanRetry();
    VerifyCommittedCleanupRetriesAreBounded();
    VerifySafetyBarriersRequireExactAcknowledgement();
    Console.WriteLine(
        "PASS: cooking jobs accept same-generation final results, reject reused cookers without side effects, "
        + "block stalled progress, lock uncertain/committed side effects, and bound committed cleanup retries.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL: {ex}");
    return 1;
}

static void VerifySameGenerationCanAdoptFinalResult()
{
    var startedAt = Utc(12, 0, 0);
    var tracker = NewTracker(expectedGeneration: 41, startedAt, phase: 2, progress: 0.25f);

    var cooking = tracker.Observe(Observe(
        startedAt.AddSeconds(1),
        generation: 41,
        AutomationCookingObservationKind.Owned,
        phase: 2,
        progress: 0.7f,
        detail: "result=A"));
    AssertTransition(
        cooking,
        outcome: "progressed",
        reason: "cooking-progress",
        state: "cooking",
        directive: AutomationCookingJobDirective.None,
        terminal: false,
        progressed: true,
        "The original result did not advance normally.");

    // The game's native finalization may replace Result through GetFinalFood. The SetCook generation,
    // rather than the managed Result wrapper, owns the cooking job.
    var ready = tracker.Observe(Observe(
        startedAt.AddSeconds(2),
        generation: 41,
        AutomationCookingObservationKind.Owned,
        phase: 3,
        progress: 1f,
        detail: "result=A2"));
    AssertTransition(
        ready,
        outcome: "progressed",
        reason: "cooking-result-ready",
        state: "ready",
        directive: AutomationCookingJobDirective.DeliverOwnedResult,
        terminal: false,
        progressed: true,
        "A same-generation final Result replacement was not accepted.");
}

static void VerifyControllerReuseNeverProducesASideEffect()
{
    var startedAt = Utc(12, 10, 0);
    var tracker = NewTracker(expectedGeneration: 100, startedAt, phase: 2, progress: 0.8f);

    var reused = tracker.Observe(Observe(
        startedAt.AddMilliseconds(500),
        generation: 101,
        AutomationCookingObservationKind.Owned,
        phase: 2,
        progress: 0.1f,
        detail: "same food and recipe, new SetCook generation"));
    AssertTransition(
        reused,
        outcome: "interrupted",
        reason: "cooking-controller-reused",
        state: "terminal",
        directive: AutomationCookingJobDirective.None,
        terminal: true,
        progressed: false,
        "A reused cooker was not retired safely.");
}

static void VerifyTwoMissingObservationsInterruptTheJob()
{
    var startedAt = Utc(12, 20, 0);
    var tracker = NewTracker(expectedGeneration: 7, startedAt, phase: 2, progress: 0.6f);

    var first = tracker.Observe(Observe(
        startedAt.AddMilliseconds(100),
        generation: 7,
        AutomationCookingObservationKind.Missing,
        phase: 0,
        progress: 0f));
    AssertTransition(
        first,
        outcome: "waiting",
        reason: "cooking-result-temporarily-missing",
        state: "cooking",
        directive: AutomationCookingJobDirective.None,
        terminal: false,
        progressed: false,
        "The first missing observation should be tolerated.");

    var second = tracker.Observe(Observe(
        startedAt.AddMilliseconds(600),
        generation: 7,
        AutomationCookingObservationKind.Missing,
        phase: 0,
        progress: 0f));
    AssertTransition(
        second,
        outcome: "interrupted",
        reason: "cooking-result-removed",
        state: "terminal",
        directive: AutomationCookingJobDirective.None,
        terminal: true,
        progressed: false,
        "Two missing observations after the grace period did not interrupt the job.");
}

static void VerifyProgressStallBlocksTheJob()
{
    var startedAt = Utc(12, 30, 0);
    var tracker = NewTracker(expectedGeneration: 15, startedAt, phase: 2, progress: 0.4f);

    var beforeLimit = tracker.Observe(Observe(
        startedAt.AddSeconds(89),
        generation: 15,
        AutomationCookingObservationKind.Owned,
        phase: 2,
        progress: 0.4f));
    AssertTransition(
        beforeLimit,
        outcome: "waiting",
        reason: "cooking-in-progress",
        state: "cooking",
        directive: AutomationCookingJobDirective.None,
        terminal: false,
        progressed: false,
        "A valid wait was retired before the stall timeout.");

    var stalled = tracker.Observe(Observe(
        startedAt.AddSeconds(90),
        generation: 15,
        AutomationCookingObservationKind.Owned,
        phase: 2,
        progress: 0.4f));
    AssertTransition(
        stalled,
        outcome: "blocked",
        reason: "cooking-progress-stalled",
        state: "terminal",
        directive: AutomationCookingJobDirective.None,
        terminal: true,
        progressed: false,
        "A cooking stage with no progress was allowed to wait forever.");
}

static void VerifyThreeUnreadableObservationsBlockTheJob()
{
    var startedAt = Utc(12, 40, 0);
    var tracker = NewTracker(expectedGeneration: 23, startedAt, phase: 2, progress: 0.3f);

    for (var attempt = 1; attempt <= 2; attempt++)
    {
        var waiting = tracker.Observe(Observe(
            startedAt.AddMilliseconds(250 * attempt),
            generation: 23,
            AutomationCookingObservationKind.Unreadable,
            phase: -1,
            progress: 0f));
        AssertTransition(
            waiting,
            outcome: "waiting",
            reason: "cooking-result-temporarily-unreadable",
            state: "cooking",
            directive: AutomationCookingJobDirective.None,
            terminal: false,
            progressed: false,
            $"Unreadable observation {attempt} should still be retryable.");
    }

    var blocked = tracker.Observe(Observe(
        startedAt.AddMilliseconds(750),
        generation: 23,
        AutomationCookingObservationKind.Unreadable,
        phase: -1,
        progress: 0f));
    AssertTransition(
        blocked,
        outcome: "blocked",
        reason: "cooking-result-unreadable",
        state: "terminal",
        directive: AutomationCookingJobDirective.None,
        terminal: true,
        progressed: false,
        "Repeated unreadable results did not enter a bounded blocked state.");
}

static void VerifyNativeFinalizeWaitNeverRequestsASideEffect()
{
    var startedAt = Utc(12, 35, 0);
    var tracker = NewTracker(expectedGeneration: 19, startedAt, phase: 2, progress: 0.8f);

    var firstFinalizeWait = tracker.Observe(Observe(
        startedAt.AddSeconds(1),
        generation: 19,
        AutomationCookingObservationKind.Owned,
        phase: 2,
        progress: 1f));
    AssertTransition(
        firstFinalizeWait,
        outcome: "progressed",
        reason: "cooking-native-finalize-waiting",
        state: "cooking",
        directive: AutomationCookingJobDirective.None,
        terminal: false,
        progressed: true,
        "Completed cooking progress should wait for the game's native finalization without requesting a Mod side effect.");

    var retry = tracker.Observe(Observe(
        startedAt.AddSeconds(3),
        generation: 19,
        AutomationCookingObservationKind.Owned,
        phase: 2,
        progress: 1f));
    AssertTransition(
        retry,
        outcome: "waiting",
        reason: "cooking-native-finalize-waiting",
        state: "cooking",
        directive: AutomationCookingJobDirective.None,
        terminal: false,
        progressed: false,
        "Native finalization waiting was incorrectly reported as progress or requested a side effect.");

    var stalled = tracker.Observe(Observe(
        startedAt.AddSeconds(91),
        generation: 19,
        AutomationCookingObservationKind.Owned,
        phase: 2,
        progress: 1f));
    AssertTransition(
        stalled,
        outcome: "blocked",
        reason: "cooking-progress-stalled",
        state: "terminal",
        directive: AutomationCookingJobDirective.None,
        terminal: true,
        progressed: false,
        "Waiting for native finalization bypassed the progress stall limit.");
}

static void VerifyManualHandoffSuspendsCookingSideEffects()
{
    var startedAt = Utc(12, 42, 0);
    var tracker = NewTracker(expectedGeneration: 27, startedAt, phase: 3, progress: 1f);
    tracker.EnterManualHandoff(startedAt.AddSeconds(1));
    if (tracker.State != "manual-handoff"
        || tracker.Outcome != "waiting"
        || tracker.ReasonCode != "cooking-manual-handoff"
        || tracker.EffectiveStallElapsed != TimeSpan.Zero)
    {
        throw new InvalidOperationException("A passive cooking receipt did not enter a non-side-effecting manual handoff state.");
    }

    tracker.Suspend(startedAt.AddMinutes(10));
    if (tracker.EffectiveStallElapsed != TimeSpan.Zero)
    {
        throw new InvalidOperationException("Manual handoff wall-clock time consumed the cooking stall budget.");
    }
}

static void VerifySafetyBarriersRequireExactAcknowledgement()
{
    var registry = new AutomationSafetyBarrierRegistry();
    registry.Register(new AutomationSafetyBarrierRecord(10, "rare:trace:a", "first", "cooking-delivery", "first"));
    registry.Register(new AutomationSafetyBarrierRecord(11, "normal:order:b", "other", "order", "other"));
    registry.Register(new AutomationSafetyBarrierRecord(12, "rare:trace:a", "latest", "order", "latest"));

    if (!registry.TryGetLatest("rare:trace:a", out var latest)
        || latest?.Sequence != 12
        || registry.TryGetLatest("rare:trace:missing", out _))
    {
        throw new InvalidOperationException("Safety barrier target lookup did not return the exact latest target barrier.");
    }

    var missing = registry.Acknowledge(99);
    if (missing.Found || missing.Sequences.Count != 0)
    {
        throw new InvalidOperationException("An unknown safety barrier acknowledgement changed registry state.");
    }

    var acknowledged = registry.Acknowledge(12);
    if (!acknowledged.Found
        || !acknowledged.Sequences.SequenceEqual(new long[] { 10, 12 })
        || registry.Contains(10)
        || registry.Contains(12)
        || !registry.Contains(11))
    {
        throw new InvalidOperationException("Acknowledging a target did not clear only its barriers through the selected sequence.");
    }
}

static void VerifyAlternatingOwnershipFailuresAreBounded()
{
    var startedAt = Utc(12, 45, 0);
    var tracker = NewTracker(expectedGeneration: 29, startedAt, phase: 2, progress: 0.3f);
    tracker.Observe(Observe(startedAt.AddMilliseconds(200), 29, AutomationCookingObservationKind.Missing, 0, 0f));
    tracker.Observe(Observe(startedAt.AddMilliseconds(400), 29, AutomationCookingObservationKind.Unreadable, -1, 0f));
    var terminal = tracker.Observe(Observe(startedAt.AddMilliseconds(700), 29, AutomationCookingObservationKind.Missing, 0, 0f));
    if (!terminal.Terminal)
    {
        throw new InvalidOperationException("Alternating missing and unreadable observations were allowed to wait forever.");
    }
}

static void VerifyRegressiveProgressDoesNotCountAsProgress()
{
    var startedAt = Utc(12, 47, 0);
    var tracker = NewTracker(expectedGeneration: 30, startedAt, phase: 2, progress: 0.6f);
    for (var attempt = 1; attempt <= 2; attempt++)
    {
        var waiting = tracker.Observe(Observe(
            startedAt.AddMilliseconds(attempt * 250),
            30,
            AutomationCookingObservationKind.Owned,
            2,
            0.5f - attempt * 0.05f));
        if (waiting.Progressed || waiting.Terminal)
        {
            throw new InvalidOperationException("Regressive progress was treated as forward progress.");
        }
    }

    var blocked = tracker.Observe(Observe(
        startedAt.AddMilliseconds(750),
        30,
        AutomationCookingObservationKind.Owned,
        2,
        0.35f));
    AssertTransition(
        blocked,
        outcome: "blocked",
        reason: "cooking-progress-regressed",
        state: "terminal",
        directive: AutomationCookingJobDirective.None,
        terminal: true,
        progressed: false,
        "Repeated regressive progress did not enter a bounded blocked state.");
}

static void VerifyNormalWaitingIsNotReportedAsProgressOrFailure()
{
    var startedAt = Utc(12, 50, 0);
    var tracker = NewTracker(expectedGeneration: 31, startedAt, phase: 2, progress: 0.5f);

    foreach (var elapsed in new[] { 1, 10, 30, 60 })
    {
        var waiting = tracker.Observe(Observe(
            startedAt.AddSeconds(elapsed),
            generation: 31,
            AutomationCookingObservationKind.Owned,
            phase: 2,
            progress: 0.5f));
        AssertTransition(
            waiting,
            outcome: "waiting",
            reason: "cooking-in-progress",
            state: "cooking",
            directive: AutomationCookingJobDirective.None,
            terminal: false,
            progressed: false,
            $"Normal wait at {elapsed}s was mistaken for progress or failure.");
    }
}

static void VerifyPausedIntervalsDoNotConsumeStallBudget()
{
    var startedAt = Utc(13, 0, 0);
    var tracker = NewTracker(expectedGeneration: 40, startedAt, phase: 2, progress: 0.5f);

    tracker.Observe(Observe(
        startedAt.AddSeconds(1),
        40,
        AutomationCookingObservationKind.Owned,
        2,
        0.5f,
        timeoutEligible: false));
    tracker.Observe(Observe(
        startedAt.AddMinutes(10),
        40,
        AutomationCookingObservationKind.Owned,
        2,
        0.5f,
        timeoutEligible: false));
    var resumed = tracker.Observe(Observe(
        startedAt.AddMinutes(10).AddSeconds(1),
        40,
        AutomationCookingObservationKind.Owned,
        2,
        0.5f,
        timeoutEligible: true));
    if (resumed.Terminal || tracker.EffectiveStallElapsed != TimeSpan.Zero)
    {
        throw new InvalidOperationException("Paused wall-clock time consumed the cooking stall budget.");
    }

    var beforeLimit = tracker.Observe(Observe(
        startedAt.AddMinutes(11).AddSeconds(30),
        40,
        AutomationCookingObservationKind.Owned,
        2,
        0.5f,
        timeoutEligible: true));
    if (beforeLimit.Terminal || tracker.EffectiveStallElapsed != TimeSpan.FromSeconds(89))
    {
        throw new InvalidOperationException("The resumed cooking stall budget did not advance by effective time only.");
    }

    var stalled = tracker.Observe(Observe(
        startedAt.AddMinutes(11).AddSeconds(31),
        40,
        AutomationCookingObservationKind.Owned,
        2,
        0.5f,
        timeoutEligible: true));
    if (!stalled.Terminal || stalled.ReasonCode != "cooking-progress-stalled")
    {
        throw new InvalidOperationException("Effective cooking time did not trigger the stall boundary.");
    }
}

static void VerifyEffectiveDeliveryClockExcludesUnavailableIntervals()
{
    var startedAt = Utc(13, 20, 0);
    var clock = new AutomationEffectiveTimeoutClock(startedAt, initiallyEligible: false);
    clock.Observe(startedAt.AddSeconds(10), eligible: false);
    clock.Observe(startedAt.AddSeconds(20), eligible: true);
    clock.Observe(startedAt.AddSeconds(30), eligible: true);
    clock.Observe(startedAt.AddMinutes(5), eligible: false);
    clock.Observe(startedAt.AddMinutes(10), eligible: false);
    clock.Observe(startedAt.AddMinutes(10).AddSeconds(1), eligible: true);
    clock.Observe(startedAt.AddMinutes(10).AddSeconds(11), eligible: true);

    if (clock.Elapsed != TimeSpan.FromSeconds(20))
    {
        throw new InvalidOperationException($"Unavailable delivery intervals changed the timeout budget: {clock.Elapsed}.");
    }
}

static void VerifySuspensionDoesNotConsumeOwnershipFailures()
{
    var startedAt = Utc(13, 10, 0);
    var tracker = NewTracker(expectedGeneration: 42, startedAt, phase: 2, progress: 0.5f);
    tracker.Observe(Observe(
        startedAt.AddMilliseconds(250),
        42,
        AutomationCookingObservationKind.Unreadable,
        -1,
        0f));
    var stallBeforeSuspension = tracker.EffectiveStallElapsed;
    tracker.Suspend(startedAt.AddMinutes(10));

    if (tracker.OwnershipObservationFailures != 1
        || tracker.EffectiveStallElapsed != stallBeforeSuspension)
    {
        throw new InvalidOperationException("Suspending an unavailable runtime changed retry or timeout budgets.");
    }

    var second = tracker.Observe(Observe(
        startedAt.AddMinutes(10).AddMilliseconds(250),
        42,
        AutomationCookingObservationKind.Unreadable,
        -1,
        0f));
    if (second.Terminal || tracker.OwnershipObservationFailures != 2)
    {
        throw new InvalidOperationException("Runtime suspension was counted as another ownership failure.");
    }
}

static void VerifyCommittedCleanupRetriesAreBounded()
{
    var cleanup = new AutomationBoundedCleanupTracker(maxAttempts: 2);
    if (!cleanup.CanCommit || cleanup.TryBeginAttempt(eligible: true))
    {
        throw new InvalidOperationException("Cleanup started before the non-idempotent operation committed.");
    }

    Commit(cleanup);
    if (cleanup.CanCommit || cleanup.TryBeginCommit())
    {
        throw new InvalidOperationException("A committed non-idempotent operation remained callable.");
    }

    if (cleanup.TryBeginAttempt(eligible: false) || cleanup.AttemptCount != 0)
    {
        throw new InvalidOperationException("An unavailable runtime consumed a committed cleanup attempt.");
    }

    if (!cleanup.TryBeginAttempt(eligible: true)
        || !cleanup.TryBeginAttempt(eligible: true)
        || !cleanup.Exhausted
        || cleanup.TryBeginAttempt(eligible: true))
    {
        throw new InvalidOperationException("Committed cleanup retries were not bounded exactly.");
    }

    var successfulCleanup = new AutomationBoundedCleanupTracker(maxAttempts: 2);
    Commit(successfulCleanup);
    if (!successfulCleanup.TryBeginAttempt(eligible: true))
    {
        throw new InvalidOperationException("An eligible cleanup attempt was rejected.");
    }

    successfulCleanup.Complete();
    if (!successfulCleanup.Completed
        || successfulCleanup.Exhausted
        || successfulCleanup.TryBeginAttempt(eligible: true))
    {
        throw new InvalidOperationException("A completed cleanup remained retryable or exhausted.");
    }
}

static void VerifyUncertainCommitCannotBeRetriedOrCleaned()
{
    var tracker = new AutomationBoundedCleanupTracker(maxAttempts: 2);
    if (!tracker.TryBeginCommit())
    {
        throw new InvalidOperationException("The initial non-idempotent attempt was rejected.");
    }

    tracker.ResolveCommit(AutomationCommitResolution.Uncertain);
    if (!tracker.CommitUncertain
        || tracker.Committed
        || tracker.CanCommit
        || tracker.TryBeginCommit()
        || tracker.TryBeginAttempt(eligible: true))
    {
        throw new InvalidOperationException("An uncertain side effect remained repeatable or cleanup-eligible.");
    }
}

static void VerifyDefiniteNonCommitCanRetry()
{
    var tracker = new AutomationBoundedCleanupTracker(maxAttempts: 2);
    if (!tracker.TryBeginCommit())
    {
        throw new InvalidOperationException("The first non-idempotent attempt was rejected.");
    }

    tracker.ResolveCommit(AutomationCommitResolution.NotCommitted);
    if (!tracker.CanCommit || tracker.Committed || tracker.CommitUncertain || !tracker.TryBeginCommit())
    {
        throw new InvalidOperationException("A definitively absent side effect could not be retried.");
    }

    tracker.ResolveCommit(AutomationCommitResolution.Committed);
    if (!tracker.Committed || tracker.CanCommit || tracker.TryBeginCommit())
    {
        throw new InvalidOperationException("A confirmed side effect was not locked to commit-once semantics.");
    }
}

static void Commit(AutomationBoundedCleanupTracker tracker)
{
    if (!tracker.TryBeginCommit())
    {
        throw new InvalidOperationException("The non-idempotent commit attempt was rejected.");
    }

    tracker.ResolveCommit(AutomationCommitResolution.Committed);
}

static AutomationCookingJobTracker NewTracker(
    long expectedGeneration,
    DateTime startedAt,
    int phase,
    float progress)
{
    return new AutomationCookingJobTracker(expectedGeneration, startedAt, phase, progress);
}

static AutomationCookingObservation Observe(
    DateTime observedAt,
    long generation,
    AutomationCookingObservationKind kind,
    int phase,
    float progress,
    string detail = "",
    bool timeoutEligible = true)
{
    return new AutomationCookingObservation(
        observedAt,
        generation,
        kind,
        phase,
        progress,
        detail,
        timeoutEligible);
}

static DateTime Utc(int hour, int minute, int second)
{
    return new DateTime(2026, 7, 12, hour, minute, second, DateTimeKind.Utc);
}

static void AssertTransition(
    AutomationCookingTransition actual,
    string outcome,
    string reason,
    string state,
    AutomationCookingJobDirective directive,
    bool terminal,
    bool progressed,
    string message)
{
    var expected = new AutomationCookingTransition(outcome, reason, state, directive, terminal, progressed);
    if (actual != expected)
    {
        throw new InvalidOperationException($"{message} Expected '{expected}', actual '{actual}'.");
    }
}
