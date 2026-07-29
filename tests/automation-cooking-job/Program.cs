using System.Runtime.CompilerServices;
using HarmonyLib;
using MystiaStewardCompanion.Save;

try
{
    VerifyHarmonyMutationCompletionSemantics();
    VerifyCookerStartAvailabilityPolicy();
    VerifyCookControllerFoodResultIdentityDomain();
    VerifySameGenerationCanAdoptFinalResult();
    VerifyControllerReuseNeverProducesASideEffect();
    VerifyExplicitOwnershipLossInterruptsTheJob();
    VerifyPersistentMissingResultBlocksTheJob();
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
        "PASS: cooking jobs reuse only strict-idle or completed-Extract cookers, accept same-generation final "
        + "results, reject reused cookers without side effects, block stalled progress, lock uncertain/committed "
        + "side effects, and bound committed cleanup retries.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL: {ex}");
    return 1;
}

static void VerifyHarmonyMutationCompletionSemantics()
{
    const string harmonyId = "mystia-steward-companion.tests.automation-cooking-mutation";
    var harmony = new Harmony(harmonyId);
    var prefix = new HarmonyMethod(AccessTools.Method(
        typeof(HarmonyMutationReceiptProbe),
        nameof(HarmonyMutationReceiptProbe.Prefix)))
    {
        priority = Priority.First,
    };
    var postfix = new HarmonyMethod(AccessTools.Method(
        typeof(HarmonyMutationReceiptProbe),
        nameof(HarmonyMutationReceiptProbe.Postfix)));

    try
    {
        HarmonyMutationReceiptProbe.Reset();
        HarmonyMutationReceiptProbe.Postfix(default, __runOriginal: true);
        AssertHarmonyMutationReceipt(
            originalCalls: 0,
            postfixCalls: 1,
            completed: false,
            "A default mutation token incorrectly completed a receipt.");

        PatchMutationProbe(harmony, nameof(HarmonyMutationTargetProbe.Normal), prefix, postfix);
        HarmonyMutationReceiptProbe.Reset();
        new HarmonyMutationTargetProbe().Normal();
        AssertHarmonyMutationReceipt(
            originalCalls: 1,
            postfixCalls: 1,
            completed: true,
            "A normally returned original did not complete its matching mutation receipt.");

        PatchMutationProbe(harmony, nameof(HarmonyMutationTargetProbe.Throwing), prefix, postfix);
        HarmonyMutationReceiptProbe.Reset();
        try
        {
            new HarmonyMutationTargetProbe().Throwing();
            throw new InvalidOperationException("The throwing Harmony probe unexpectedly returned.");
        }
        catch (InvalidOperationException ex) when (ex.Message == HarmonyMutationTargetProbe.ThrowMessage)
        {
            // Expected: an original exception bypasses the ordinary postfix.
        }

        AssertHarmonyMutationReceipt(
            originalCalls: 1,
            postfixCalls: 0,
            completed: false,
            "A throwing original incorrectly completed its mutation receipt.");

        PatchMutationProbe(harmony, nameof(HarmonyMutationTargetProbe.NestedMutation), prefix, postfix);
        HarmonyMutationReceiptProbe.Reset();
        new HarmonyMutationTargetProbe().NestedMutation();
        AssertHarmonyMutationReceipt(
            originalCalls: 1,
            postfixCalls: 1,
            completed: false,
            "A stale outer postfix overwrote a newer nested mutation revision.");

        PatchMutationProbe(harmony, nameof(HarmonyMutationTargetProbe.Skipped), prefix, postfix);
        harmony.Patch(
            AccessTools.Method(typeof(HarmonyMutationTargetProbe), nameof(HarmonyMutationTargetProbe.Skipped)),
            prefix: new HarmonyMethod(AccessTools.Method(
                typeof(HarmonyMutationSkipProbe),
                nameof(HarmonyMutationSkipProbe.Prefix)))
            {
                priority = Priority.Last,
            });
        HarmonyMutationReceiptProbe.Reset();
        new HarmonyMutationTargetProbe().Skipped();
        AssertHarmonyMutationReceipt(
            originalCalls: 0,
            postfixCalls: 1,
            completed: false,
            "A skipped original incorrectly completed its mutation receipt.");
    }
    finally
    {
        harmony.UnpatchSelf();
    }
}

static void PatchMutationProbe(
    Harmony harmony,
    string methodName,
    HarmonyMethod prefix,
    HarmonyMethod postfix)
{
    harmony.Patch(
        AccessTools.Method(typeof(HarmonyMutationTargetProbe), methodName),
        prefix: prefix,
        postfix: postfix);
}

static void AssertHarmonyMutationReceipt(
    int originalCalls,
    int postfixCalls,
    bool completed,
    string message)
{
    if (HarmonyMutationTargetProbe.OriginalCalls != originalCalls
        || HarmonyMutationReceiptProbe.PostfixCalls != postfixCalls
        || HarmonyMutationReceiptProbe.Completed != completed)
    {
        throw new InvalidOperationException(
            $"{message} original={HarmonyMutationTargetProbe.OriginalCalls}/{originalCalls}; "
            + $"postfix={HarmonyMutationReceiptProbe.PostfixCalls}/{postfixCalls}; "
            + $"completed={HarmonyMutationReceiptProbe.Completed}/{completed}.");
    }
}

static void VerifyCookerStartAvailabilityPolicy()
{
    AssertCookerStartAvailability(
        phase: 0,
        resultEmpty: true,
        chosenRecipeEmpty: true,
        couldOpen: true,
        completedExtractObserved: false,
        AutomationCookerStartAvailability.StrictIdle,
        "An exact native-idle cooker was not startable.");
    AssertCookerStartAvailability(
        phase: 0,
        resultEmpty: true,
        chosenRecipeEmpty: false,
        couldOpen: true,
        completedExtractObserved: true,
        AutomationCookerStartAvailability.ExtractedResidual,
        "A normally completed Extract with only residual recipe metadata was not reusable.");

    foreach (var unavailable in new[]
    {
        (Phase: 0, ResultEmpty: true, ChosenRecipeEmpty: false, CouldOpen: true, CompletedExtract: false),
        (Phase: 1, ResultEmpty: true, ChosenRecipeEmpty: false, CouldOpen: true, CompletedExtract: true),
        (Phase: 0, ResultEmpty: false, ChosenRecipeEmpty: false, CouldOpen: true, CompletedExtract: true),
        (Phase: 0, ResultEmpty: true, ChosenRecipeEmpty: false, CouldOpen: false, CompletedExtract: true),
    })
    {
        AssertCookerStartAvailability(
            unavailable.Phase,
            unavailable.ResultEmpty,
            unavailable.ChosenRecipeEmpty,
            unavailable.CouldOpen,
            unavailable.CompletedExtract,
            AutomationCookerStartAvailability.Unavailable,
            "A cooker without the exact strict-idle or completed-Extract proof became startable.");
    }
}

static void AssertCookerStartAvailability(
    int phase,
    bool resultEmpty,
    bool chosenRecipeEmpty,
    bool couldOpen,
    bool completedExtractObserved,
    AutomationCookerStartAvailability expected,
    string message)
{
    var actual = AutomationCookerStartPolicy.Classify(
        phase,
        resultEmpty,
        chosenRecipeEmpty,
        couldOpen,
        completedExtractObserved);
    if (actual != expected)
    {
        throw new InvalidOperationException($"{message} Expected {expected}, got {actual}.");
    }
}

static void VerifyCookControllerFoodResultIdentityDomain()
{
    if (!CookControllerFoodResultIdentityPolicy.TryCreate(
            CookControllerFoodResultIdentity.ExactManagedTypeName,
            sellableType: 0,
            foodId: 14,
            out var catalogFood,
            out var catalogDiagnostic)
        || catalogFood.Kind != CookControllerFoodResultKind.CatalogFood
        || catalogFood.FoodId != 14
        || catalogFood.IsDarkCuisine)
    {
        throw new InvalidOperationException(
            $"A non-negative catalog food was rejected or misclassified: {catalogDiagnostic}");
    }

    if (!CookControllerFoodResultIdentityPolicy.TryCreate(
            CookControllerFoodResultIdentity.ExactManagedTypeName,
            sellableType: 0,
            foodId: -1,
            out var darkCuisine,
            out var darkCuisineDiagnostic)
        || darkCuisine.Kind != CookControllerFoodResultKind.DarkCuisine
        || darkCuisine.FoodId != -1
        || !darkCuisine.IsDarkCuisine)
    {
        throw new InvalidOperationException(
            $"The game's exact Food/-1 dark-cuisine result was rejected: {darkCuisineDiagnostic}");
    }

    var startedAt = Utc(11, 50, 0);
    var tracker = NewTracker(expectedGeneration: 39, startedAt, phase: 2, progress: 0.9f);
    var nativeFinalizeWait = tracker.Observe(Observe(
        startedAt.AddSeconds(1),
        generation: 39,
        AutomationCookingObservationKind.Owned,
        phase: 2,
        progress: 1f,
        detail: $"result={darkCuisine.SellableType}/{darkCuisine.FoodId}"));
    AssertTransition(
        nativeFinalizeWait,
        outcome: "progressed",
        reason: "cooking-native-finalize-waiting",
        state: "cooking",
        directive: AutomationCookingJobDirective.None,
        terminal: false,
        progressed: true,
        "A Phase 2 Food/-1 result bypassed the native finalization boundary.");

    var readyForMismatchRecovery = tracker.Observe(Observe(
        startedAt.AddSeconds(2),
        generation: 39,
        AutomationCookingObservationKind.Owned,
        phase: 3,
        progress: 1f,
        detail: $"result={darkCuisine.SellableType}/{darkCuisine.FoodId}"));
    AssertTransition(
        readyForMismatchRecovery,
        outcome: "progressed",
        reason: "cooking-result-ready",
        state: "ready",
        directive: AutomationCookingJobDirective.DeliverOwnedResult,
        terminal: false,
        progressed: true,
        "A Phase 3 Food/-1 result did not enter the existing mismatch recovery boundary.");

    AssertCookControllerFoodResultRejected(
        managedTypeName: "Tests.FakeSellable",
        sellableType: 0,
        foodId: -1,
        "An arbitrary object exposing Type/Id escaped the exact Sellable type boundary.");
    AssertCookControllerFoodResultRejected(
        managedTypeName: CookControllerFoodResultIdentity.ExactManagedTypeName,
        sellableType: 1,
        foodId: -1,
        "A Beverage/-1 result escaped the cooker food boundary.");
    AssertCookControllerFoodResultRejected(
        managedTypeName: CookControllerFoodResultIdentity.ExactManagedTypeName,
        sellableType: 0,
        foodId: -2,
        "An unknown negative food id escaped the cooker result boundary.");
    AssertCookControllerFoodResultRejected(
        managedTypeName: CookControllerFoodResultIdentity.ExactManagedTypeName,
        sellableType: -1,
        foodId: -1,
        "A missing/unreadable Sellable.Type sentinel was treated as dark cuisine.");
}

static void AssertCookControllerFoodResultRejected(
    string? managedTypeName,
    int sellableType,
    int foodId,
    string message)
{
    if (CookControllerFoodResultIdentityPolicy.TryCreate(
            managedTypeName,
            sellableType,
            foodId,
            out _,
            out var diagnostic)
        || string.IsNullOrWhiteSpace(diagnostic))
    {
        throw new InvalidOperationException(message);
    }
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

static void VerifyExplicitOwnershipLossInterruptsTheJob()
{
    var startedAt = Utc(12, 20, 0);
    var tracker = NewTracker(expectedGeneration: 7, startedAt, phase: 2, progress: 0.6f);

    var ownershipLost = tracker.Observe(Observe(
        startedAt.AddMilliseconds(100),
        generation: 7,
        AutomationCookingObservationKind.OwnershipLost,
        phase: 0,
        progress: 0f));
    AssertTransition(
        ownershipLost,
        outcome: "interrupted",
        reason: "cooking-ownership-lost",
        state: "terminal",
        directive: AutomationCookingJobDirective.None,
        terminal: true,
        progressed: false,
        "An exact controller-content ownership loss did not interrupt the old job.");
}

static void VerifyPersistentMissingResultBlocksTheJob()
{
    var startedAt = Utc(12, 21, 0);
    var tracker = NewTracker(expectedGeneration: 8, startedAt, phase: 2, progress: 0.6f);

    AutomationCookingTransition transition = default;
    for (var attempt = 1; attempt <= 6; attempt++)
    {
        transition = tracker.Observe(Observe(
            startedAt.AddMilliseconds(attempt * 600),
            generation: 8,
            AutomationCookingObservationKind.Missing,
            phase: 2,
            progress: 0.6f));
        if (attempt < 6 && transition.Terminal)
        {
            throw new InvalidOperationException(
                $"A transient non-idle missing result was blocked at observation {attempt}.");
        }
    }

    AssertTransition(
        transition,
        outcome: "blocked",
        reason: "cooking-result-missing",
        state: "terminal",
        directive: AutomationCookingJobDirective.None,
        terminal: true,
        progressed: false,
        "A persistently missing result in a non-idle cooker did not enter a bounded blocked state.");
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
    AutomationCookingTransition terminal = default;
    for (var attempt = 1; attempt <= 6; attempt++)
    {
        terminal = tracker.Observe(Observe(
            startedAt.AddMilliseconds(attempt * 600),
            29,
            attempt % 2 == 0
                ? AutomationCookingObservationKind.Unreadable
                : AutomationCookingObservationKind.Missing,
            attempt % 2 == 0 ? -1 : 2,
            0.3f));
    }

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

internal readonly record struct HarmonyMutationProbeToken(long Revision);

internal sealed class HarmonyMutationTargetProbe
{
    internal const string ThrowMessage = "expected Harmony mutation probe failure";

    public static int OriginalCalls { get; private set; }

    public static void Reset()
    {
        OriginalCalls = 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Normal()
    {
        OriginalCalls++;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Throwing()
    {
        OriginalCalls++;
        throw new InvalidOperationException(ThrowMessage);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void NestedMutation()
    {
        OriginalCalls++;
        HarmonyMutationReceiptProbe.RecordNestedMutation();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Skipped()
    {
        OriginalCalls++;
    }
}

internal static class HarmonyMutationReceiptProbe
{
    private static long _nextRevision;
    private static long _currentRevision;

    public static int PostfixCalls { get; private set; }

    public static bool Completed { get; private set; }

    public static void Reset()
    {
        _nextRevision = 0;
        _currentRevision = 0;
        PostfixCalls = 0;
        Completed = false;
        HarmonyMutationTargetProbe.Reset();
    }

    public static void Prefix(out HarmonyMutationProbeToken __state)
    {
        _nextRevision++;
        _currentRevision = _nextRevision;
        Completed = false;
        __state = new HarmonyMutationProbeToken(_currentRevision);
    }

    public static void Postfix(HarmonyMutationProbeToken __state, bool __runOriginal)
    {
        PostfixCalls++;
        if (!__runOriginal || __state.Revision <= 0 || __state.Revision != _currentRevision)
        {
            return;
        }

        Completed = true;
    }

    public static void RecordNestedMutation()
    {
        _nextRevision++;
        _currentRevision = _nextRevision;
        Completed = false;
    }
}

internal static class HarmonyMutationSkipProbe
{
    public static bool Prefix()
    {
        return false;
    }
}
