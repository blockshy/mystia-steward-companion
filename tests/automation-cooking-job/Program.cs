using System.Runtime.CompilerServices;
using HarmonyLib;
using MystiaStewardCompanion.Save;

try
{
    VerifyAutomationCompletionInvariant();
    VerifyTargetedAutomationCancellationPolicy();
    VerifyCommittedDeliveryEvaluationBoundary();
    VerifyHarmonyMutationCompletionSemantics();
    VerifyCookerStartAvailabilityPolicy();
    VerifyCookControllerFoodResultIdentityDomain();
    VerifySpecialFoodTargetPolicyMatching();
    VerifySpecialFoodTargetPolicyIdentity();
    VerifySameGenerationCanAdoptFinalResult();
    VerifyControllerReuseNeverProducesASideEffect();
    VerifyExplicitOwnershipLossInterruptsTheJob();
    VerifyPersistentMissingResultBlocksTheJob();
    VerifyProgressStallBlocksTheJob();
    VerifyNativeFinalizeWaitNeverRequestsASideEffect();
    VerifyControllerLeaseIsMonotonicAndIndependentFromEvaluationReceipt();
    VerifyProductionControllerLeaseBoundary();
    VerifyEvaluationCloseoutIsEffectivelyBounded();
    VerifyProductionOrderReceiptBoundary();
    VerifyManualHandoffSuspendsCookingSideEffects();
    VerifyExpiredManualHandoffRetainsTheOrderSlot();
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
    VerifyYuumaSwitchDisabledManualHandoffIsBarrierFree();
    VerifyInvalidatedCookerReservationsNeverInvokeOldWrappers();
    VerifyProductionFreshCookerBindingContract();
    Console.WriteLine(
        "PASS: cooking jobs reuse only strict-idle or completed-Extract cookers, accept same-generation final "
        + "results, reject reused cookers without side effects, block stalled progress, lock uncertain/committed "
        + "side effects, bound committed cleanup retries, reject invalidated cooker reservations before touching "
        + "old wrappers, close direct-delivery evaluation before retiring irreversible jobs, reject direct "
        + "delivery without completion before runtime access, and keep the "
        + "controller lease independent from evaluation receipts while keeping the switch-disabled Blood "
        + "Pond Hell handoff receipt deterministic and barrier-free.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL: {ex}");
    return 1;
}

static void VerifyAutomationCompletionInvariant()
{
    AssertValidConfiguration(
        AutomationOrderActionKind.PrepareRare,
        autoTakeBeverage: false,
        autoCollectCooking: false,
        autoDeliverFood: false,
        autoCompleteOrder: false,
        "Rare cooking-only/manual-handoff configuration was rejected.");
    AssertValidConfiguration(
        AutomationOrderActionKind.CompleteNormal,
        autoTakeBeverage: false,
        autoCollectCooking: false,
        autoDeliverFood: false,
        autoCompleteOrder: false,
        "Normal cooking-only/manual-handoff configuration was rejected.");
    AssertValidConfiguration(
        AutomationOrderActionKind.PrepareRare,
        autoTakeBeverage: true,
        autoCollectCooking: true,
        autoDeliverFood: true,
        autoCompleteOrder: true,
        "A fully enabled rare automation configuration was rejected.");
    AssertValidConfiguration(
        AutomationOrderActionKind.CompleteNormal,
        autoTakeBeverage: true,
        autoCollectCooking: true,
        autoDeliverFood: true,
        autoCompleteOrder: true,
        "A fully enabled normal automation configuration was rejected.");

    foreach (var invalid in new[]
             {
                 (AutomationOrderActionKind.PrepareRare, true, false, false, "rare beverage"),
                 (AutomationOrderActionKind.PrepareRare, false, true, false, "rare cooked-food collection"),
                 (AutomationOrderActionKind.CompleteNormal, true, false, false, "normal beverage"),
                 (AutomationOrderActionKind.CompleteNormal, false, true, false, "normal cooked-food collection"),
                 (AutomationOrderActionKind.CompleteNormal, false, false, true, "normal food delivery"),
             })
    {
        if (AutomationOrderConfigurationPolicy.TryValidate(
                invalid.Item1,
                invalid.Item2,
                invalid.Item3,
                invalid.Item4,
                autoCompleteOrder: false,
                out var error)
            || string.IsNullOrWhiteSpace(error))
        {
            throw new InvalidOperationException(
                $"The {invalid.Item5} configuration can still deliver without completion.");
        }
    }

    if (AutomationOrderConfigurationPolicy.TryValidate(
            AutomationOrderActionKind.CompleteRare,
            autoTakeBeverage: false,
            autoCollectCooking: false,
            autoDeliverFood: false,
            autoCompleteOrder: false,
            out var completeRareError)
        || string.IsNullOrWhiteSpace(completeRareError))
    {
        throw new InvalidOperationException(
            "The rare completion endpoint can still evaluate while completion intent is disabled.");
    }

    var root = FindRepositoryRoot();
    var service = File.ReadAllText(Path.Combine(
        root,
        "mods",
        "bepinex",
        "src",
        "Save",
        "RuntimeOrderPreparationService.cs"));
    foreach (var (marker, actionKind) in new[]
             {
                 ("public static OrderPreparationResult Prepare(", "AutomationOrderActionKind.PrepareRare"),
                 ("public static OrderPreparationResult CompleteFirst(", "AutomationOrderActionKind.CompleteRare"),
                 ("public static OrderPreparationResult CompleteNormalFirst(", "AutomationOrderActionKind.CompleteNormal"),
             })
    {
        var method = ExtractSourceBlock(service, marker);
        var guard = method.IndexOf("BuildAutomationConfigurationInvalidResult(", StringComparison.Ordinal);
        var runtimeGate = method.IndexOf("RuntimeNightBusinessAutomationGate.Refresh()", StringComparison.Ordinal);
        AssertTrue(
            guard >= 0 && runtimeGate > guard,
            $"{marker} can reach the game runtime before validating the completion invariant.");
        AssertContains(
            method,
            actionKind,
            $"{marker} uses the wrong configuration policy action kind.");
    }

    AssertContains(
        service,
        "public const string AutomationConfigurationInvalid = AutomationOrderConfigurationPolicy.InvalidReasonCode;",
        "The runtime result no longer exposes the canonical automation-config-invalid reason code.");
    AssertContains(
        service,
        "result.Automation.Stage = \"configuration\";",
        "Invalid automation configuration no longer produces a structured configuration-stage result.");
    AssertContains(
        service,
        "result.Automation.RetryAfterMs = 0;",
        "Invalid automation configuration can still enter a retry loop.");
}

static void VerifyTargetedAutomationCancellationPolicy()
{
    foreach (var (wireValue, expectedTarget) in new[]
             {
                 ("commands", AutomationCancellationTarget.Commands),
                 ("rare", AutomationCancellationTarget.Rare),
                 ("normal", AutomationCancellationTarget.Normal),
                 ("all", AutomationCancellationTarget.All),
             })
    {
        if (!AutomationCancellationTargetPolicy.TryParse(wireValue, out var target)
            || target != expectedTarget
            || !string.Equals(
                AutomationCancellationTargetPolicy.ToWireValue(target),
                wireValue,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Cancellation target '{wireValue}' did not round-trip exactly.");
        }
    }

    foreach (var invalid in new[] { "", "Commands", "Rare", " normal", "all ", "*", "jobs" })
    {
        if (AutomationCancellationTargetPolicy.TryParse(invalid, out _))
        {
            throw new InvalidOperationException($"Noncanonical cancellation target '{invalid}' was accepted.");
        }
    }

    AssertTrue(
        AutomationCancellationTargetPolicy.IncludesCookingJob(AutomationCancellationTarget.Rare, rareTarget: true)
        && !AutomationCancellationTargetPolicy.IncludesCookingJob(AutomationCancellationTarget.Rare, rareTarget: false),
        "Rare cancellation does not isolate rare cooking jobs.");
    AssertTrue(
        AutomationCancellationTargetPolicy.IncludesCookingJob(AutomationCancellationTarget.Normal, rareTarget: false)
        && !AutomationCancellationTargetPolicy.IncludesCookingJob(AutomationCancellationTarget.Normal, rareTarget: true),
        "Normal cancellation does not isolate normal cooking jobs.");
    AssertTrue(
        AutomationCancellationTargetPolicy.IncludesCookingJob(AutomationCancellationTarget.All, rareTarget: true)
        && AutomationCancellationTargetPolicy.IncludesCookingJob(AutomationCancellationTarget.All, rareTarget: false),
        "All cancellation does not cover both cooking-job kinds.");
    AssertTrue(
        !AutomationCancellationTargetPolicy.IncludesCookingJob(AutomationCancellationTarget.Commands, rareTarget: true)
        && !AutomationCancellationTargetPolicy.IncludesCookingJob(AutomationCancellationTarget.Commands, rareTarget: false),
        "Command-only cancellation can still select active cooking jobs.");

    var activeJobs = new[]
    {
        (Id: "rare-1", Rare: true),
        (Id: "normal-1", Rare: false),
        (Id: "rare-2", Rare: true),
        (Id: "normal-2", Rare: false),
    };
    AssertSequence(
        activeJobs.Select(job => job.Id).ToArray(),
        RetainJobsAfterCancellation(activeJobs, AutomationCancellationTarget.Commands),
        "Command-only cancellation removed an active rare or normal cooking job.");
    AssertSequence(
        new[] { "normal-1", "normal-2" },
        RetainJobsAfterCancellation(activeJobs, AutomationCancellationTarget.Rare),
        "Rare cancellation did not retain exactly the active normal cooking jobs.");
    AssertSequence(
        new[] { "rare-1", "rare-2" },
        RetainJobsAfterCancellation(activeJobs, AutomationCancellationTarget.Normal),
        "Normal cancellation did not retain exactly the active rare cooking jobs.");
    AssertSequence(
        Array.Empty<string>(),
        RetainJobsAfterCancellation(activeJobs, AutomationCancellationTarget.All),
        "All cancellation retained active cooking jobs.");

    var root = FindRepositoryRoot();
    var service = File.ReadAllText(Path.Combine(
        root,
        "mods",
        "bepinex",
        "src",
        "Save",
        "RuntimeOrderPreparationService.cs"));
    var clearJobs = ExtractSourceBlock(
        service,
        "public static int ClearAutomationCookingJobs(");
    AssertContains(
        clearJobs,
        "AutomationCancellationTargetPolicy.IncludesCookingJob(",
        "Cooking-job cancellation no longer filters the requested canonical target.");
    AssertContains(
        clearJobs,
        "preserveIrreversibleTransactions",
        "User cancellation no longer distinguishes safely releasable jobs from irreversible transactions.");
    AssertContains(
        clearJobs,
        "CanCancelAutomationCookingJob(job, out var retentionReason)",
        "User cancellation can remove a cooking job after an irreversible side effect started.");
    var targetFilter = clearJobs.IndexOf("AutomationCancellationTargetPolicy.IncludesCookingJob(", StringComparison.Ordinal);
    var eventCommit = clearJobs.IndexOf("RecordAutomationRuntimeEvent(", StringComparison.Ordinal);
    var removal = clearJobs.IndexOf("AutomationCookingJobs.RemoveAt(index);", StringComparison.Ordinal);
    AssertTrue(
        targetFilter >= 0 && eventCommit > targetFilter && removal > eventCommit,
        "A nonmatching cooking job can be logged or removed before the target filter.");
    AssertDoesNotContain(
        clearJobs,
        "AutomationCancellationTarget target =",
        "ClearAutomationCookingJobs restored an implicit all-target default.");

    var canCancel = ExtractSourceBlock(
        service,
        "private static bool CanCancelAutomationCookingJob(");
    AssertContains(canCancel, "job.FoodDeliveryCommitted || job.FoodDeliveryCommitUncertain",
        "Cancellation no longer preserves an in-flight food-delivery transaction.");
    AssertContains(canCancel, "job.WarmerStoreCommitted || job.WarmerStoreCommitUncertain",
        "Cancellation no longer preserves an in-flight warmer transaction.");
    AssertContains(canCancel, "YuumaSettlementTransactionStage.Ready",
        "Cancellation no longer preserves an in-flight Yuuma settlement.");
    AssertDoesNotContain(canCancel, "TryGetUnresolvedAutomationSafetyBarrier",
        "A standalone safety barrier incorrectly keeps a safely releasable cooking job alive after lease release.");
}

static void VerifyCommittedDeliveryEvaluationBoundary()
{
    var root = FindRepositoryRoot();
    var saveRoot = Path.Combine(root, "mods", "bepinex", "src", "Save");
    var service = File.ReadAllText(Path.Combine(saveRoot, "RuntimeOrderPreparationService.cs"));
    var cooking = File.ReadAllText(Path.Combine(saveRoot, "RuntimeOrderPreparationService.Cooking.cs"));
    var delivery = File.ReadAllText(Path.Combine(saveRoot, "RuntimeOrderPreparationService.Delivery.cs"));
    var directDelivery = File.ReadAllText(Path.Combine(saveRoot, "RuntimeOrderPreparationService.DirectDelivery.cs"));

    var matchedEvaluation = ExtractSourceBlock(
        delivery,
        "private static RuntimeOrderEvaluationResult TryEvaluateMatchedAutomationOrderRuntimeIfReady(");
    AssertContains(matchedEvaluation, "TryEvaluateWackyKoishiBossRuntimeOrderIfReady",
        "The shared direct-delivery evaluation route no longer preserves Koishi full-feed evaluation.");
    AssertContains(matchedEvaluation, "TryEvaluateYuyukoChallengeRuntimeOrderIfReady",
        "The shared direct-delivery evaluation route no longer preserves Yuyuko evaluation.");
    AssertContains(matchedEvaluation, "target.Kind == CookingCollectionTargetKind.RareOrder",
        "Yuyuko evaluation no longer distinguishes rare fresh reacquisition from the normal-order route.");
    AssertContains(matchedEvaluation, "TryEvaluateRuntimeOrderIfReady",
        "The shared direct-delivery evaluation route no longer reaches ordinary order evaluation.");
    AssertContains(matchedEvaluation, "fulfilledPreflight: fulfilledPreflight",
        "Mizuchi validation is not attached to the fulfilled-only evaluation boundary.");
    AssertTrue(
        matchedEvaluation.IndexOf("IsYuumaBossTarget(target)", StringComparison.Ordinal)
            < matchedEvaluation.IndexOf("TryEvaluateWackyKoishiBossRuntimeOrderIfReady", StringComparison.Ordinal),
        "The shared evaluation route can reach a non-Yuuma evaluator before rejecting Yuuma settlement.");

    var genericEvaluation = ExtractSourceBlock(
        delivery,
        "private static RuntimeOrderEvaluationResult TryEvaluateRuntimeOrderIfReady(");
    var fulfilledReadIndex = genericEvaluation.IndexOf("get_IsFullfilled", StringComparison.Ordinal);
    var unfulfilledWaitIndex = genericEvaluation.IndexOf("if (!isFullfilled)", StringComparison.Ordinal);
    var fulfilledPreflightIndex = genericEvaluation.IndexOf("fulfilledPreflight?.Invoke()", StringComparison.Ordinal);
    var nativeEvaluationIndex = genericEvaluation.IndexOf("TryInvokeRuntimeOrderEvaluationOnce(", StringComparison.Ordinal);
    AssertTrue(
        fulfilledReadIndex >= 0
        && unfulfilledWaitIndex > fulfilledReadIndex
        && fulfilledPreflightIndex > unfulfilledWaitIndex
        && nativeEvaluationIndex > fulfilledPreflightIndex,
        "An incomplete order can still enter a special fulfilled preflight, or a fulfilled order can invoke native evaluation before that preflight.");

    var transaction = ExtractSourceBlock(
        directDelivery,
        "private static (bool Remove, string Message, string Code) TryCompleteCommittedFoodDeliveryTransaction(");
    var cleanup = transaction.IndexOf("TryCompleteCommittedFoodDeliveryCleanup(job)", StringComparison.Ordinal);
    var evaluation = transaction.IndexOf("TryResolveCommittedFoodDeliveryEvaluation(job", StringComparison.Ordinal);
    AssertTrue(cleanup >= 0 && evaluation > cleanup,
        "Committed food delivery no longer closes cooker cleanup before fresh order evaluation.");
    AssertContains(transaction, "job.FoodDeliveryCleanupTerminal",
        "A terminal cooker-cleanup failure can still skip the independent order-evaluation closeout.");

    var resolveEvaluation = ExtractSourceBlock(
        directDelivery,
        "private static bool TryResolveCommittedFoodDeliveryEvaluation(");
    AssertTrue(
        resolveEvaluation.IndexOf("if (!job.AutoCompleteOrder)", StringComparison.Ordinal)
            < resolveEvaluation.IndexOf("get_IsFullfilled", StringComparison.Ordinal)
        && resolveEvaluation.IndexOf("get_IsFullfilled", StringComparison.Ordinal)
            < resolveEvaluation.IndexOf("TryEvaluateMatchedAutomationOrderRuntimeIfReady", StringComparison.Ordinal),
        "The job does not use its latched completion intent and fresh fulfilled state before evaluating.");
    AssertContains(resolveEvaluation, "OrderPreparationStepCodes.OrderEvaluationCommitUncertain",
        "An unclassified evaluation exception no longer enters the commit-uncertain safety boundary.");

    var prepare = ExtractSourceBlock(service, "public static OrderPreparationResult Prepare(");
    AssertTrue(
        prepare.IndexOf("beverageDeliveredThisRequest = true", StringComparison.Ordinal)
            < prepare.IndexOf("TryEvaluateMatchedAutomationOrderIfReady(", StringComparison.Ordinal)
        && prepare.IndexOf("TryEvaluateMatchedAutomationOrderIfReady(", StringComparison.Ordinal)
            < prepare.IndexOf("if (request.AutoStartCooking)", StringComparison.Ordinal),
        "Rare Prepare can still let cooking work interrupt evaluation after beverage became the final item.");

    var completeRare = ExtractSourceBlock(service, "public static OrderPreparationResult CompleteFirst(");
    AssertTrue(
        completeRare.IndexOf("deliveredItemCount > 0", StringComparison.Ordinal)
            < completeRare.IndexOf("TryEvaluateMatchedAutomationOrderIfReady(", StringComparison.Ordinal)
        && completeRare.IndexOf("TryEvaluateMatchedAutomationOrderIfReady(", StringComparison.Ordinal)
            < completeRare.IndexOf("AddPatientRecoveryStepIfNeeded", StringComparison.Ordinal),
        "Rare completion can still let patient recovery interrupt evaluation after direct delivery.");

    var completeNormal = ExtractSourceBlock(service, "public static OrderPreparationResult CompleteNormalFirst(");
    var firstImmediateEvaluation = completeNormal.IndexOf("TryEvaluateMatchedAutomationOrderIfReady(", StringComparison.Ordinal);
    var pendingFoodCommit = completeNormal.IndexOf("RuntimeDeliveryItemKind.Food", firstImmediateEvaluation, StringComparison.Ordinal);
    var secondImmediateEvaluation = completeNormal.IndexOf("TryEvaluateMatchedAutomationOrderIfReady(", pendingFoodCommit, StringComparison.Ordinal);
    var patientRecovery = completeNormal.IndexOf("AddPatientRecoveryStepIfNeeded", secondImmediateEvaluation, StringComparison.Ordinal);
    AssertTrue(firstImmediateEvaluation >= 0
        && pendingFoodCommit > firstImmediateEvaluation
        && secondImmediateEvaluation > pendingFoodCommit
        && patientRecovery > secondImmediateEvaluation,
        "Normal direct beverage or in-air food delivery can still be interrupted before immediate evaluation.");
    var completedJobBranch = completeNormal.IndexOf("if (cookingJobResult.CompletedOrder)", StringComparison.Ordinal);
    var completedJobReturn = completeNormal.IndexOf("return Finish(result)", completedJobBranch, StringComparison.Ordinal);
    var staleOrderRead = completeNormal.IndexOf("result.ServedFood = ReadOrderServedFood(runtimeOrder.Order)", completedJobReturn, StringComparison.Ordinal);
    AssertTrue(completedJobBranch >= 0 && completedJobReturn > completedJobBranch && staleOrderRead > completedJobReturn,
        "CompleteNormalFirst can access a stale order wrapper after a cooking job evaluated and retired it.");

    var processNormalJob = ExtractSourceBlock(
        cooking,
        "private static (bool Delivered, bool CompletedOrder, string StepName, string Message, string Code)\n        TryProcessNormalOrderCookingJob(");
    AssertTrue(
        processNormalJob.IndexOf("AutomationFoodDeliveryEvaluationState.Completed", StringComparison.Ordinal)
            < processNormalJob.IndexOf("ReadOrderServedFood(order)", StringComparison.Ordinal),
        "Normal job processing can read the pre-evaluation order wrapper before reporting completed evaluation.");
}

static string[] RetainJobsAfterCancellation(
    IEnumerable<(string Id, bool Rare)> activeJobs,
    AutomationCancellationTarget target)
{
    return activeJobs
        .Where(job => !AutomationCancellationTargetPolicy.IncludesCookingJob(target, job.Rare))
        .Select(job => job.Id)
        .ToArray();
}

static void AssertSequence(
    IReadOnlyList<string> expected,
    IReadOnlyList<string> actual,
    string message)
{
    if (!expected.SequenceEqual(actual, StringComparer.Ordinal))
    {
        throw new InvalidOperationException(
            $"{message} Expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}].");
    }
}

static void AssertValidConfiguration(
    AutomationOrderActionKind actionKind,
    bool autoTakeBeverage,
    bool autoCollectCooking,
    bool autoDeliverFood,
    bool autoCompleteOrder,
    string message)
{
    if (!AutomationOrderConfigurationPolicy.TryValidate(
            actionKind,
            autoTakeBeverage,
            autoCollectCooking,
            autoDeliverFood,
            autoCompleteOrder,
            out var error)
        || !string.IsNullOrEmpty(error))
    {
        throw new InvalidOperationException(message);
    }
}

static void VerifySpecialFoodTargetPolicyMatching()
{
    var anySignature = SpecialFoodTargetPolicy.BuildSignature(
        "Story_WackyCookingCompetition",
        "koishi",
        9,
        SpecialFoodTargetMatchMode.Any,
        new[] { "传说", "海味" });
    AssertSpecialFoodTargetPolicy(
        "Story_WackyCookingCompetition",
        "koishi",
        9,
        new[] { "海味", "传说", "海味" },
        "any",
        anySignature,
        out var anyPolicy);
    if (!anyPolicy.Matches(new[] { "海味" }) || anyPolicy.Matches(new[] { "肉" }))
    {
        throw new InvalidOperationException("The Any special-food target policy did not preserve one-of matching.");
    }

    var allSignature = SpecialFoodTargetPolicy.BuildSignature(
        "Story_BloodPondHell",
        "yuuma",
        12,
        SpecialFoodTargetMatchMode.All,
        new[] { "传说", "海味" });
    AssertSpecialFoodTargetPolicy(
        "Story_BloodPondHell",
        "yuuma",
        12,
        new[] { "传说", "海味" },
        "all",
        allSignature,
        out var allPolicy);
    if (!allPolicy.Matches(new[] { "传说", "海味", "肉" })
        || allPolicy.Matches(new[] { "传说" })
        || allPolicy.Matches(Array.Empty<string>()))
    {
        throw new InvalidOperationException("The Blood Pond Hell All policy accepted an incomplete actual Tag set.");
    }

    if (SpecialFoodTargetPolicy.TryCreate(
            " Story_BloodPondHell",
            "yuuma",
            12,
            new[] { "传说", "海味" },
            "all",
            allSignature,
            out _,
            out _)
        || SpecialFoodTargetPolicy.TryCreate(
            "Story_BloodPondHell",
            "yuuma",
            12,
            new[] { "传说", "海味" },
            "ALL",
            allSignature,
            out _,
            out _)
        || SpecialFoodTargetPolicy.TryCreate(
            "Story_BloodPondHell",
            "yuuma",
            0,
            new[] { "传说", "海味" },
            "all",
            allSignature,
            out _,
            out _)
        || SpecialFoodTargetPolicy.TryCreate(
            "Story_BloodPondHell",
            "yuuma",
            12,
            new[] { "传说", "海味" },
            "all",
            $"{allSignature}|alias",
            out _,
            out _))
    {
        throw new InvalidOperationException("An alias, invalid generation, or noncanonical signature was accepted.");
    }
}

static void VerifySpecialFoodTargetPolicyIdentity()
{
    var expected = SpecialFoodTargetPolicy.CreateActive(
        "Story_BloodPondHell",
        "yuuma",
        18,
        new[] { "传说", "海味" },
        SpecialFoodTargetMatchMode.All);
    var same = SpecialFoodTargetPolicy.CreateActive(
        "Story_BloodPondHell",
        "yuuma",
        18,
        new[] { "海味", "传说" },
        SpecialFoodTargetMatchMode.All);
    var changedGeneration = SpecialFoodTargetPolicy.CreateActive(
        "Story_BloodPondHell",
        "yuuma",
        19,
        new[] { "传说", "海味" },
        SpecialFoodTargetMatchMode.All);
    var changedTags = SpecialFoodTargetPolicy.CreateActive(
        "Story_BloodPondHell",
        "yuuma",
        18,
        new[] { "传说", "肉" },
        SpecialFoodTargetMatchMode.All);

    if (!expected.HasSameIdentity(same)
        || expected.HasSameIdentity(changedGeneration)
        || expected.HasSameIdentity(changedTags))
    {
        throw new InvalidOperationException("Special-food target identity did not bind generation and normalized Tags.");
    }
}

static void AssertSpecialFoodTargetPolicy(
    string challenge,
    string owner,
    long generation,
    IReadOnlyList<string> tags,
    string mode,
    string signature,
    out SpecialFoodTargetPolicy policy)
{
    if (!SpecialFoodTargetPolicy.TryCreate(
            challenge,
            owner,
            generation,
            tags,
            mode,
            signature,
            out var parsed,
            out var error)
        || parsed == null)
    {
        throw new InvalidOperationException($"A valid special-food target policy was rejected: {error}");
    }

    policy = parsed;
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

static void VerifyControllerLeaseIsMonotonicAndIndependentFromEvaluationReceipt()
{
    var startedAt = Utc(12, 41, 0);
    var completedReceiptLease = new AutomationCookingControllerLease();
    AssertTrue(completedReceiptLease.HoldsReservation,
        "A new cooking job did not start with an active controller lease.");
    AssertTrue(
        completedReceiptLease.Release(
            AutomationCookingControllerLeaseReleaseReason.DeliveryCleanupCompleted,
            startedAt),
        "Completed cooker cleanup did not release the controller lease.");
    AssertTrue(!completedReceiptLease.HoldsReservation,
        "An evaluation-only receipt still reserves its old controller.");
    AssertEqual(
        AutomationCookingControllerLeaseReleaseReason.DeliveryCleanupCompleted,
        completedReceiptLease.ReleaseReason,
        "The cleanup-completed lease release reason was not preserved.");
    AssertEqual(startedAt, completedReceiptLease.ReleasedAtUtc!.Value,
        "The controller lease release timestamp was not preserved.");

    AssertTrue(
        !completedReceiptLease.Release(
            AutomationCookingControllerLeaseReleaseReason.DeliveryCleanupTerminated,
            startedAt.AddSeconds(1)),
        "A released evaluation receipt reacquired or rewrote its controller lease.");
    AssertEqual(
        AutomationCookingControllerLeaseReleaseReason.DeliveryCleanupCompleted,
        completedReceiptLease.ReleaseReason,
        "A late terminal observation rewrote a monotonic lease release.");

    var terminalReceiptLease = new AutomationCookingControllerLease();
    terminalReceiptLease.Release(
        AutomationCookingControllerLeaseReleaseReason.DeliveryCleanupTerminated,
        startedAt.AddSeconds(2));
    AssertTrue(!terminalReceiptLease.HoldsReservation,
        "A terminal cleanup receipt still reserves a controller it promises not to access.");

    var manualHandoffLease = new AutomationCookingControllerLease();
    manualHandoffLease.Release(
        AutomationCookingControllerLeaseReleaseReason.ManualHandoff,
        startedAt.AddSeconds(3));
    AssertTrue(!manualHandoffLease.HoldsReservation,
        "A passive manual-handoff receipt still reserves its controller.");

    var newCookingLease = new AutomationCookingControllerLease();
    completedReceiptLease.Release(
        AutomationCookingControllerLeaseReleaseReason.ManualHandoff,
        startedAt.AddMinutes(1));
    AssertTrue(newCookingLease.HoldsReservation,
        "A late old-receipt transition changed the new job's independent controller lease.");
    var reservations = new[]
    {
        (ControllerPointer: (nint)0x1000, Lease: completedReceiptLease),
        (ControllerPointer: (nint)0x1000, Lease: newCookingLease),
    };
    AssertEqual(
        1,
        reservations.Count(candidate =>
            candidate.ControllerPointer == (nint)0x1000
            && candidate.Lease.HoldsReservation),
        "A released closeout receipt and a new job on the same pointer did not leave exactly one active holder.");

    var tracker = NewTracker(expectedGeneration: 26, startedAt, phase: 3, progress: 1f);
    tracker.EnterEvaluationPending(startedAt.AddSeconds(4), "cooking-evaluation-after-cleanup");
    AssertEqual("evaluation-pending", tracker.State,
        "Cleanup completion did not enter the explicit evaluation-receipt state.");
    AssertEqual("waiting", tracker.Outcome,
        "An evaluation receipt was reported as active cooker progress.");
    tracker.Suspend(startedAt.AddMinutes(10));
    AssertEqual(TimeSpan.Zero, tracker.EffectiveStallElapsed,
        "Evaluation closeout wall time consumed the physical cooker stall budget.");

    var invalidReasonRejected = false;
    try
    {
        new AutomationCookingControllerLease().Release(
            AutomationCookingControllerLeaseReleaseReason.None,
            startedAt);
    }
    catch (ArgumentOutOfRangeException)
    {
        invalidReasonRejected = true;
    }

    AssertTrue(invalidReasonRejected,
        "The controller lease accepted a release without an exact terminal reason.");
}

static void VerifyProductionControllerLeaseBoundary()
{
    var root = FindRepositoryRoot();
    var saveRoot = Path.Combine(root, "mods", "bepinex", "src", "Save");
    var cooking = File.ReadAllText(Path.Combine(saveRoot, "RuntimeOrderPreparationService.Cooking.cs"));
    var delivery = File.ReadAllText(Path.Combine(saveRoot, "RuntimeOrderPreparationService.DirectDelivery.cs"));

    var registration = ExtractSourceBlock(
        cooking,
        "private static AutomationCookingJob RegisterAutomationCookingJob(");
    AssertDoesNotContain(registration, "replacedJobs",
        "Registration restored the obsolete silent job-replacement path.");
    AssertDoesNotContain(registration, "cooking-job-replaced",
        "Registration restored the obsolete replacement terminal event.");
    AssertContains(registration, "duplicateTarget",
        "Registration no longer fails closed if the pre-side-effect same-target guard is bypassed.");
    AssertContains(registration, "job.HoldsControllerReservation",
        "Registration does not distinguish an active controller lease from an old evaluation receipt.");
    AssertContains(registration, "existing active lease",
        "Registration can silently overwrite another active controller holder.");

    var reservation = ExtractSourceBlock(
        cooking,
        "private static bool IsCookControllerReserved(");
    AssertContains(reservation, "job.HoldsControllerReservation",
        "Cooker reservation still derives ownership from manual-handoff state instead of the explicit lease.");
    AssertDoesNotContain(reservation, "!job.ManualHandoffObserved",
        "The obsolete manual-handoff reservation predicate was restored.");
    AssertContains(reservation, "ownerJob=",
        "Cooker waiting diagnostics no longer identify the exact owning job.");

    var manualHandoff = ExtractSourceBlock(
        cooking,
        "private static (bool Remove, string Message, string Code) EnterManualHandoff(");
    AssertTrue(
        manualHandoff.IndexOf("ControllerLease.Release", StringComparison.Ordinal)
            < manualHandoff.IndexOf("Tracker.EnterManualHandoff", StringComparison.Ordinal),
        "Manual handoff can become passive before releasing its controller lease.");

    var cleanup = ExtractSourceBlock(
        delivery,
        "private static (bool Remove, string Message, string Code) TryCompleteCommittedFoodDeliveryCleanup(");
    var cleanupCommitted = cleanup.IndexOf("FoodDeliveryCleanupTracker.Complete();", StringComparison.Ordinal);
    var extractionFinished = cleanup.IndexOf("CompleteCookerExtractionAfterReset(job)", StringComparison.Ordinal);
    var evaluationReceipt = cleanup.IndexOf("EnterCommittedFoodDeliveryEvaluationReceipt(", StringComparison.Ordinal);
    var completedGuard = cleanup.IndexOf("if (job.FoodDeliveryCleanupCompleted)", StringComparison.Ordinal);
    var terminalGuard = cleanup.IndexOf("if (job.FoodDeliveryCleanupTerminal)", StringComparison.Ordinal);
    var wrapperGuard = cleanup.IndexOf("if (job.DeliveredFood == null", StringComparison.Ordinal);
    AssertTrue(cleanupCommitted >= 0
        && extractionFinished > cleanupCommitted
        && evaluationReceipt > extractionFinished,
        "The controller lease can be released before strict reset and one-shot extraction callbacks finish.");
    AssertTrue(completedGuard >= 0
        && terminalGuard > completedGuard
        && wrapperGuard > terminalGuard,
        "A released receipt can mistake its intentionally cleared food wrapper for a new cleanup failure.");

    var terminalCleanup = ExtractSourceBlock(
        delivery,
        "private static (bool Remove, string Message, string Code) BlockCommittedFoodDeliveryCleanup(");
    AssertContains(
        terminalCleanup,
        "AutomationCookingControllerLeaseReleaseReason.DeliveryCleanupTerminated",
        "A terminal cleanup that promises no more controller access still holds the Mod lease.");

    var enterReceipt = ExtractSourceBlock(
        delivery,
        "private static void EnterCommittedFoodDeliveryEvaluationReceipt(");
    AssertTrue(
        enterReceipt.IndexOf("ControllerLease.Release", StringComparison.Ordinal)
            < enterReceipt.IndexOf("DeliveredFood = null", StringComparison.Ordinal)
        && enterReceipt.IndexOf("DeliveredFood = null", StringComparison.Ordinal)
            < enterReceipt.IndexOf("Tracker.EnterEvaluationPending", StringComparison.Ordinal),
        "Evaluation receipt transition no longer releases the lease and drops the IL2CPP food wrapper atomically.");
}

static void VerifyEvaluationCloseoutIsEffectivelyBounded()
{
    var startedAt = Utc(12, 44, 0);
    var tracker = new AutomationOrderEvaluationCloseoutTracker(
        startedAt,
        maxAttempts: 3,
        minimumAttemptWindow: TimeSpan.FromSeconds(2),
        maxEffectiveDuration: TimeSpan.FromSeconds(5));

    AssertTrue(!tracker.RecordFailure(startedAt, eligible: true),
        "The first transient order lookup failure exhausted closeout immediately.");
    AssertTrue(!tracker.RecordFailure(startedAt.AddSeconds(1), eligible: true),
        "Closeout exhausted before its attempt and effective-time boundaries were both met.");
    tracker.Suspend(startedAt.AddMinutes(10));
    AssertEqual(TimeSpan.FromSeconds(1), tracker.EffectiveElapsed,
        "A suspended automation interval consumed evaluation closeout time.");
    AssertTrue(!tracker.RecordFailure(startedAt.AddMinutes(10).AddSeconds(1), eligible: true),
        "The first observation after suspension incorrectly charged the paused interval.");
    AssertTrue(tracker.RecordFailure(startedAt.AddMinutes(10).AddSeconds(2), eligible: true),
        "Closeout did not stop after the configured attempt and effective-time boundary.");

    var durationBound = new AutomationOrderEvaluationCloseoutTracker(
        startedAt,
        maxAttempts: 100,
        minimumAttemptWindow: TimeSpan.FromSeconds(2),
        maxEffectiveDuration: TimeSpan.FromSeconds(5));
    AssertTrue(durationBound.RecordFailure(startedAt.AddSeconds(5), eligible: true),
        "A low-frequency closeout bypassed its maximum effective duration.");
}

static void VerifyProductionOrderReceiptBoundary()
{
    var root = FindRepositoryRoot();
    var saveRoot = Path.Combine(root, "mods", "bepinex", "src", "Save");
    var service = File.ReadAllText(Path.Combine(saveRoot, "RuntimeOrderPreparationService.cs"));
    var cooking = File.ReadAllText(Path.Combine(saveRoot, "RuntimeOrderPreparationService.Cooking.cs"));
    var evaluationDelivery = File.ReadAllText(Path.Combine(saveRoot, "RuntimeOrderPreparationService.Delivery.cs"));
    var delivery = File.ReadAllText(Path.Combine(saveRoot, "RuntimeOrderPreparationService.DirectDelivery.cs"));
    var lifecycle = File.ReadAllText(Path.Combine(saveRoot, "RuntimeNightBusinessLifecycle.cs"));
    var yuyuko = File.ReadAllText(Path.Combine(
        saveRoot,
        "SpecialBusiness",
        "RuntimeOrderPreparationService.YuyukoChallengePolicy.cs"));

    var target = ExtractSourceBlock(service, "private sealed class CookingCollectionTarget");
    AssertContains(target, "RuntimeOrderBindingToken? OrderBinding",
        "Cooking targets do not retain their exact wrapper-free order receipt identity.");
    AssertDoesNotContain(target, "public object? Order",
        "Cooking targets restored the cross-frame IL2CPP order-wrapper fallback.");

    var binding = ExtractSourceBlock(service, "private static bool TryBindCookingTargetRuntimeOrder(");
    AssertTrue(
        binding.IndexOf("RuntimeOrderTypeResolver.Resolve", StringComparison.Ordinal)
            < binding.IndexOf("TryCaptureActiveLifecycle", StringComparison.Ordinal)
        && binding.IndexOf("TryReadNativeObjectPointer", StringComparison.Ordinal)
            < binding.IndexOf("TryCaptureActiveLifecycle", StringComparison.Ordinal)
        && binding.IndexOf("TryCaptureActiveLifecycle", StringComparison.Ordinal)
            < binding.IndexOf("new RuntimeOrderBindingToken", StringComparison.Ordinal)
        && binding.IndexOf("lifecycleAfter.Generation != lifecycleBefore.Generation", StringComparison.Ordinal)
            < binding.IndexOf("new RuntimeOrderBindingToken", StringComparison.Ordinal),
        "Order receipt binding is not fenced by an exact active lifecycle and stable business generation.");

    var start = ExtractSourceBlock(cooking, "private static CookingStartResult TryStartCooking(");
    AssertTrue(
        start.IndexOf("!target.OrderBinding.HasValue", StringComparison.Ordinal)
            < start.IndexOf("InvokeRuntimeStorageOut(\"IngredientOut\"", StringComparison.Ordinal),
        "Cooking can deduct ingredients before requiring an exact order/controller receipt identity.");
    AssertDoesNotContain(start, "CookingCollectionTarget?",
        "Cooking restored the dead nullable target compatibility path.");
    AssertDoesNotContain(start, "CookingCollectionTarget.ForRareOrder(new OrderPreparationRequest",
        "Cooking restored a dummy unbound target that can never pass the exact binding gate.");

    var matchNormal = ExtractSourceBlock(cooking, "private static bool IsMatchingNormalOrderCookingJob(");
    AssertContains(matchNormal, "TryMatchRuntimeOrderBinding",
        "Normal cooking-job lookup no longer uses the shared exact runtime identity check.");
    AssertDoesNotContain(matchNormal, "IsSameObject(job.Target.Order",
        "Normal cooking-job lookup restored the retained-wrapper fallback.");

    var exactMatch = ExtractSourceBlock(service, "private static bool TryMatchRuntimeOrderBinding(");
    AssertContains(exactMatch, "lifecycle.Generation != token.BusinessGeneration",
        "The shared runtime-order matcher no longer fences the business generation.");
    AssertContains(exactMatch, "resolution.Kind != token.OrderKind",
        "The shared runtime-order matcher no longer requires the exact concrete order kind.");
    AssertContains(exactMatch, "orderPointer != token.OrderPointer",
        "The shared runtime-order matcher no longer requires the exact native order pointer.");
    AssertContains(exactMatch, "controllerPointer != token.ControllerPointer",
        "The shared runtime-order matcher no longer requires the exact native controller pointer.");
    AssertContains(exactMatch, "RuntimeOrderTerminalReceiptStore.MatchesActiveLifecycle(token)",
        "The shared runtime-order matcher no longer rejects a reused native tuple from another order lifecycle.");

    var resolve = ExtractSourceBlock(delivery, "private static bool TryResolveCommittedFoodDeliveryEvaluation(");
    AssertTrue(
        resolve.IndexOf("RuntimeOrderTerminalReceiptStore.TryFind", StringComparison.Ordinal)
            < resolve.IndexOf("FindRuntimeNormalOrder", StringComparison.Ordinal),
        "A deferred evaluation reads runtime wrappers before checking the exact terminal receipt.");
    AssertContains(resolve, "RuntimeOrderTerminalDisposition.Evaluated",
        "An exact external evaluation receipt no longer completes deferred closeout.");
    AssertContains(resolve, "AutomationFoodDeliveryEvaluationState.OrderTerminated",
        "An exact removal receipt is no longer distinguished from successful evaluation.");
    AssertTrue(
        resolve.IndexOf("TryMatchRuntimeOrderBinding", StringComparison.Ordinal)
            < resolve.IndexOf("get_IsFullfilled", StringComparison.Ordinal),
        "Deferred evaluation can read or evaluate a fresh order before matching its exact generation/kind/order/controller identity.");
    AssertContains(resolve, "OrderEvaluationTargetMismatch",
        "A deterministic evaluation target mismatch no longer enters its explicit terminal boundary.");

    var nativeEvaluation = ExtractSourceBlock(
        evaluationDelivery,
        "private static RuntimeOrderEvaluationResult TryInvokeRuntimeOrderEvaluationOnce(\n        object manager,");
    AssertTrue(
        nativeEvaluation.IndexOf("InvokeInstance(manager, methodName, args)", StringComparison.Ordinal)
            < nativeEvaluation.IndexOf("TryConfirmRuntimeOrderEvaluatedReceipt(orderBinding", StringComparison.Ordinal)
        && nativeEvaluation.IndexOf("TryConfirmRuntimeOrderEvaluatedReceipt(orderBinding", StringComparison.Ordinal)
            < nativeEvaluation.IndexOf("return new(true, true, false", StringComparison.Ordinal),
        "A normally returned evaluation can complete without consuming its exact synchronous Evaluated receipt.");
    AssertContains(nativeEvaluation, "OrderEvaluationCommitUncertain",
        "A skipped native evaluation without an exact receipt no longer enters the no-replay ACK boundary.");
    var catchStart = nativeEvaluation.IndexOf("catch (Exception ex)", StringComparison.Ordinal);
    var catchReceipt = nativeEvaluation.IndexOf(
        "TryConfirmRuntimeOrderEvaluatedReceipt(orderBinding",
        catchStart,
        StringComparison.Ordinal);
    AssertTrue(catchStart >= 0 && catchReceipt > catchStart,
        "An exceptional evaluation does not check its exact terminal receipt.");
    AssertDoesNotContain(
        nativeEvaluation[catchStart..],
        "TryReadRuntimeOrderEvaluated(controller",
        "An exceptional evaluation re-reads a possibly retired controller wrapper after the native call.");

    var deliver = ExtractSourceBlock(
        delivery,
        "private static (bool Remove, string Message, string Code) TryDeliverAutomationCookedFood(");
    AssertTrue(
        deliver.IndexOf("TryMatchRuntimeOrderBinding", StringComparison.Ordinal)
            < deliver.IndexOf("job.ResetDeliveryFailures()", StringComparison.Ordinal),
        "Cooked food can reach delivery handling before the fresh order/controller matches the opening token.");

    var bounded = ExtractSourceBlock(
        delivery,
        "private static bool ContinueOrCloseCommittedFoodDeliveryEvaluation(");
    AssertContains(bounded, "OrderEvaluationCloseoutUnresolved",
        "Transient evaluation failures can still retry forever without an ACK safety barrier.");
    AssertContains(bounded, "RecordOrderSafetyBarrierIfNeeded",
        "Exhausted evaluation closeout no longer publishes its exact order-level safety barrier.");

    AssertContains(yuyuko, "OrderPreparationStepCodes.OrderEvaluationTargetMismatch",
        "Yuyuko deterministic served-target mismatches are not classified separately from transient reads.");
    AssertContains(lifecycle, "RuntimeOrderTerminalReceiptStore.Clear",
        "Runtime terminal receipts are not cleared at the night-business boundary.");
    AssertContains(lifecycle, "ClearAutomationSafetyBarriersForBusinessGeneration(snapshot.Generation)",
        "Retired business generations no longer clear their unresolved automation barriers.");

    var eventWriter = ExtractSourceBlock(service, "private static long RecordAutomationRuntimeEvent(\n        string code,");
    AssertTrue(
        eventWriter.IndexOf("businessLifecycle.IsActive", StringComparison.Ordinal)
            < eventWriter.IndexOf("UnresolvedAutomationSafetyBarriers.Register", StringComparison.Ordinal)
        && eventWriter.IndexOf("businessLifecycle.Generation == orderBinding.Value.BusinessGeneration", StringComparison.Ordinal)
            < eventWriter.IndexOf("UnresolvedAutomationSafetyBarriers.Register", StringComparison.Ordinal),
        "A native callback can refill an ACK barrier after its business generation has already ended.");
    AssertContains(eventWriter, "business-lifecycle-ended",
        "A late old-generation uncertainty is not downgraded to a bounded non-ACK diagnostic.");

    var suspend = ExtractSourceBlock(service, "private static void SuspendAutomationCookingJobClocks(");
    AssertContains(suspend, "FoodDeliveryEvaluationCloseoutTracker?.Suspend(observedAtUtc)",
        "A blocked automation gate still charges deferred evaluation closeout time.");

    var jobType = ExtractSourceBlock(service, "private sealed class AutomationCookingJob");
    AssertTrue(
        jobType.IndexOf("if (ManualHandoffExpired)", StringComparison.Ordinal)
            < jobType.IndexOf("if (ManualHandoffObserved)", StringComparison.Ordinal),
        "The transaction stage can never expose an expired manual handoff.");
}

static void VerifyExpiredManualHandoffRetainsTheOrderSlot()
{
    var startedAt = Utc(12, 43, 0);
    var tracker = NewTracker(expectedGeneration: 28, startedAt, phase: 3, progress: 1f);
    tracker.EnterManualHandoff(startedAt.AddSeconds(1));
    tracker.MarkManualHandoffExpired(startedAt.AddSeconds(2));
    if (tracker.State != "manual-handoff-expired"
        || tracker.Outcome != "waiting"
        || tracker.ReasonCode != "cooking-manual-handoff-expired"
        || tracker.EffectiveStallElapsed != TimeSpan.Zero)
    {
        throw new InvalidOperationException(
            "A rotated Blood Pond Hell target did not retain a non-side-effecting expired handoff slot.");
    }

    tracker.Suspend(startedAt.AddMinutes(10));
    if (tracker.EffectiveStallElapsed != TimeSpan.Zero)
    {
        throw new InvalidOperationException("An expired handoff consumed the cooking stall budget.");
    }
}

static void VerifySafetyBarriersRequireExactAcknowledgement()
{
    var rareLifecycle1 = AutomationSafetyBarrierRegistry.BuildOrderLifecycleTargetIdentity(
        41,
        "Special",
        (nint)0x1001,
        (nint)0x2001,
        7);
    var rareLifecycle2 = AutomationSafetyBarrierRegistry.BuildOrderLifecycleTargetIdentity(
        41,
        "Special",
        (nint)0x1001,
        (nint)0x2001,
        8);
    var normalLifecycle = AutomationSafetyBarrierRegistry.BuildOrderLifecycleTargetIdentity(
        42,
        "Normal",
        (nint)0x1002,
        (nint)0x2002,
        9);
    if (string.Equals(rareLifecycle1, rareLifecycle2, StringComparison.Ordinal))
    {
        throw new InvalidOperationException(
            "Safety barrier identity ignored the order lifecycle sequence during native pointer reuse.");
    }

    var registry = new AutomationSafetyBarrierRegistry();
    registry.Register(new AutomationSafetyBarrierRecord(10, 41, rareLifecycle1, "first", "cooking-delivery", "first"));
    registry.Register(new AutomationSafetyBarrierRecord(11, 42, normalLifecycle, "other", "order", "other"));
    registry.Register(new AutomationSafetyBarrierRecord(12, 41, rareLifecycle1, "latest", "order", "latest"));

    if (!registry.TryGetLatest(rareLifecycle1, out var latest)
        || latest?.Sequence != 12
        || registry.TryGetLatest(rareLifecycle2, out _))
    {
        throw new InvalidOperationException(
            "Safety barrier lookup crossed an exact order lifecycle boundary or lost the latest matching barrier.");
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

    for (var sequence = 100L; sequence < 170L; sequence++)
    {
        registry.Register(new AutomationSafetyBarrierRecord(
            sequence,
            41,
            rareLifecycle2,
            "bounded-generation",
            "order",
            "bounded-generation"));
    }

    var retired = registry.RetireBusinessGeneration(41);
    if (retired.Count != 70
        || retired[0] != 100
        || retired[^1] != 169
        || registry.Contains(100)
        || registry.Contains(169)
        || !registry.Contains(11))
    {
        throw new InvalidOperationException(
            "Business-boundary barrier retirement was not complete, ordered, or generation-isolated.");
    }
}

static void VerifyYuumaSwitchDisabledManualHandoffIsBarrierFree()
{
    var signature = SpecialFoodTargetPolicy.BuildSignature(
        "Story_BloodPondHell",
        "yuuma",
        41,
        SpecialFoodTargetMatchMode.All,
        new[] { "传说", "海味" });
    AssertSpecialFoodTargetPolicy(
        "Story_BloodPondHell",
        "yuuma",
        41,
        new[] { "传说", "海味" },
        "all",
        signature,
        out var policy);
    if (!policy.Matches(new[] { "传说", "海味" }))
    {
        throw new InvalidOperationException("The exact Blood Pond Hell target policy was not established for the handoff test.");
    }

    var startedAt = Utc(12, 58, 0);
    var tracker = NewTracker(expectedGeneration: 41, startedAt, phase: 3, progress: 1f);
    tracker.EnterManualHandoff(startedAt.AddSeconds(1));
    if (tracker.State != "manual-handoff"
        || tracker.Outcome != "waiting"
        || tracker.ReasonCode != "cooking-manual-handoff"
        || tracker.EffectiveStallElapsed != TimeSpan.Zero)
    {
        throw new InvalidOperationException(
            "The switch-disabled Blood Pond Hell path did not enter the deterministic non-side-effecting handoff state.");
    }

    var barriers = new AutomationSafetyBarrierRegistry();
    if (barriers.TryGetLatest("rare:trace:yuuma", out _))
    {
        throw new InvalidOperationException("A deterministic Blood Pond Hell handoff created an uncertainty barrier.");
    }

    var acknowledgement = barriers.Acknowledge(41);
    if (acknowledgement.Found || acknowledgement.Sequences.Count != 0)
    {
        throw new InvalidOperationException("A deterministic Blood Pond Hell handoff required a safety acknowledgement.");
    }
}

static void VerifyInvalidatedCookerReservationsNeverInvokeOldWrappers()
{
    if (!RuntimeCookerReservation.TryCreate(
            0,
            "0x1001",
            4,
            0,
            2,
            out var reservation,
            out var reservationError))
    {
        throw new InvalidOperationException($"The exact cooker fixture was rejected: {reservationError}");
    }

    var oldController = new SideEffectProbe();
    var replacementController = new SideEffectProbe();
    var initialEntries = BuildCookerEntries(oldController, "0x1001");
    AssertFreshCookerSideEffect(
        expected: true,
        reservation,
        initialEntries,
        lockedPositions: new HashSet<RuntimeCookerGridPosition>(),
        couldOpen: true,
        expectedOwnership: new TestCookerOwnership(7, 11),
        currentOwnership: new TestCookerOwnership(7, 11),
        oldController,
        "An unchanged exact reservation did not reach its fresh wrapper.");
    AssertEqual(1, oldController.InvocationCount, "The valid fresh wrapper was not invoked exactly once.");

    AssertFreshCookerSideEffect(
        expected: false,
        reservation,
        initialEntries,
        lockedPositions: new HashSet<RuntimeCookerGridPosition> { reservation.GridPosition },
        couldOpen: false,
        expectedOwnership: new TestCookerOwnership(7, 11),
        currentOwnership: new TestCookerOwnership(7, 11),
        oldController,
        "A challenge-locked reservation reached the old wrapper.");

    var replacementEntries = BuildCookerEntries(replacementController, "0x2001");
    AssertFreshCookerSideEffect(
        expected: false,
        reservation,
        replacementEntries,
        lockedPositions: new HashSet<RuntimeCookerGridPosition>(),
        couldOpen: true,
        expectedOwnership: new TestCookerOwnership(7, 11),
        currentOwnership: new TestCookerOwnership(7, 11),
        replacementController,
        "A replacement controller reused the old reservation.");

    AssertFreshCookerSideEffect(
        expected: false,
        reservation,
        initialEntries,
        lockedPositions: new HashSet<RuntimeCookerGridPosition>(),
        couldOpen: true,
        expectedOwnership: new TestCookerOwnership(7, 11),
        currentOwnership: new TestCookerOwnership(7, 12),
        oldController,
        "A changed content revision reached the old wrapper.");

    AssertEqual(
        1,
        oldController.InvocationCount,
        "An invalidated event path invoked the retained old controller wrapper.");
    AssertEqual(
        0,
        replacementController.InvocationCount,
        "An old reservation invoked a replacement controller wrapper.");
}

static IReadOnlyList<RuntimeCookerControllerEntry> BuildCookerEntries(
    SideEffectProbe controller,
    string identity)
{
    return new[]
    {
        new RuntimeCookerControllerEntry
        {
            Controller = controller,
            ControllerIdentity = identity,
            GridPosition = new RuntimeCookerGridPosition(4, 0, 2),
        },
    };
}

static void AssertFreshCookerSideEffect(
    bool expected,
    RuntimeCookerReservation reservation,
    IReadOnlyList<RuntimeCookerControllerEntry> currentEntries,
    IReadOnlySet<RuntimeCookerGridPosition> lockedPositions,
    bool couldOpen,
    TestCookerOwnership expectedOwnership,
    TestCookerOwnership currentOwnership,
    SideEffectProbe expectedFreshController,
    string message)
{
    var invoked = TryInvokeWithFreshCooker(
        reservation,
        currentEntries,
        lockedPositions,
        couldOpen,
        expectedOwnership,
        currentOwnership);
    if (invoked != expected)
    {
        throw new InvalidOperationException(message);
    }

    if (invoked && expectedFreshController.InvocationCount == 0)
    {
        throw new InvalidOperationException("The side effect did not use the wrapper returned by the fresh catalog read.");
    }
}

static bool TryInvokeWithFreshCooker(
    RuntimeCookerReservation reservation,
    IReadOnlyList<RuntimeCookerControllerEntry> currentEntries,
    IReadOnlySet<RuntimeCookerGridPosition> lockedPositions,
    bool couldOpen,
    TestCookerOwnership expectedOwnership,
    TestCookerOwnership currentOwnership)
{
    if (!reservation.TryMatch(currentEntries, out var freshEntry, out _)
        || reservation.EvaluateChallengeGate(lockedPositions, couldOpen)
            != RuntimeCookerChallengeGateState.Available
        || currentOwnership != expectedOwnership)
    {
        return false;
    }

    ((SideEffectProbe)freshEntry.Controller).Invoke();
    return true;
}

static void VerifyProductionFreshCookerBindingContract()
{
    var root = FindRepositoryRoot();
    var service = File.ReadAllText(Path.Combine(
        root,
        "mods",
        "bepinex",
        "src",
        "Save",
        "RuntimeOrderPreparationService.cs"));
    var cooking = File.ReadAllText(Path.Combine(
        root,
        "mods",
        "bepinex",
        "src",
        "Save",
        "RuntimeOrderPreparationService.Cooking.cs"));

    var jobSource = ExtractSourceBlock(
        service,
        "private sealed class AutomationCookingJob");
    AssertContains(
        jobSource,
        "public RuntimeCookerReservation CookerReservation { get; init; }",
        "AutomationCookingJob no longer retains its exact managed cooker reservation.");
    AssertDoesNotContain(
        jobSource,
        "object CookController",
        "AutomationCookingJob retained a long-lived IL2CPP cooker wrapper.");

    var processor = ExtractSourceBlock(
        cooking,
        "private static (bool Remove, string Message, string Code) TryProcessAutomationCookingJob(");
    var freshBinding = processor.IndexOf("TryReacquireAutomationCooker(", StringComparison.Ordinal);
    var contentRead = processor.IndexOf("cookerBinding.State", StringComparison.Ordinal);
    AssertTrue(
        freshBinding >= 0 && contentRead > freshBinding,
        "An existing cooking job can read cooker content before a fresh exact reservation binding.");
    AssertDoesNotContain(
        processor,
        "job.CookController",
        "An existing cooking job can still touch a retained cooker wrapper.");

    var reacquire = ExtractSourceBlock(
        cooking,
        "private static bool TryReacquireAutomationCooker(");
    foreach (var required in new[]
             {
                 "TryReadCookerControllerEntriesFromCookSystem(",
                 "TryReadLockedCookerPositions(",
                 "job.CookerReservation.TryMatch(",
                 "lockedPositions.Contains(job.CookerReservation.GridPosition)",
                 "job.CookerReservation.EvaluateChallengeGate(",
                 "controllerPointer != job.ControllerPointer",
                 "ownershipBefore != ownershipAfter",
                 "ownershipAfter.Generation == job.Generation",
                 "ownershipAfter.ContentRevision == job.ContentRevision",
                 "if (!ownershipMatches)",
             })
    {
        AssertContains(
            reacquire,
            required,
            $"Fresh cooker binding is missing the fail-closed boundary '{required}'.");
    }

    var reservationMatch = reacquire.IndexOf("job.CookerReservation.TryMatch(", StringComparison.Ordinal);
    var lockedRejection = reacquire.IndexOf(
        "lockedPositions.Contains(job.CookerReservation.GridPosition)",
        StringComparison.Ordinal);
    var stateRead = reacquire.IndexOf(
        "RuntimeCookerReflection.TryReadCookerControllerState(",
        StringComparison.Ordinal);
    AssertTrue(
        reservationMatch >= 0 && lockedRejection > reservationMatch && stateRead > lockedRejection,
        "A challenge-locked reservation can still reach native controller state getters before rejection.");

    var reservedSelection = ExtractSourceBlock(
        cooking,
        "private static (\n        bool Ok,\n        bool Waiting,\n        object? CookController,\n        RuntimeCookerControllerState? ControllerState,\n        string Message) TryGetCookerFromCookSystem(");
    var selectionMatch = reservedSelection.IndexOf("reservation.TryMatch(", StringComparison.Ordinal);
    var selectionLockedRejection = reservedSelection.IndexOf(
        "lockedPositions.Contains(reservation.GridPosition)",
        StringComparison.Ordinal);
    var selectionStateRead = reservedSelection.IndexOf(
        "RuntimeCookerReflection.TryReadCookerControllerState(",
        StringComparison.Ordinal);
    AssertTrue(
        selectionMatch >= 0
        && selectionLockedRejection > selectionMatch
        && selectionStateRead > selectionLockedRejection,
        "New cooking can still read a challenge-locked controller before rejecting its reservation.");
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

static string FindRepositoryRoot()
{
    var current = new DirectoryInfo(AppContext.BaseDirectory);
    while (current != null)
    {
        if (File.Exists(Path.Combine(current.FullName, "package.json"))
            && Directory.Exists(Path.Combine(current.FullName, "mods", "bepinex")))
        {
            return current.FullName;
        }

        current = current.Parent;
    }

    throw new InvalidOperationException("Repository root was not found from the test output directory.");
}

static string ExtractSourceBlock(string source, string marker)
{
    var markerIndex = source.IndexOf(marker, StringComparison.Ordinal);
    if (markerIndex < 0)
    {
        throw new InvalidOperationException($"Source marker was not found: {marker}");
    }

    var openBrace = source.IndexOf('{', markerIndex);
    if (openBrace < 0)
    {
        throw new InvalidOperationException($"Source block has no opening brace: {marker}");
    }

    var depth = 0;
    for (var index = openBrace; index < source.Length; index++)
    {
        if (source[index] == '{')
        {
            depth++;
        }
        else if (source[index] == '}' && --depth == 0)
        {
            return source[markerIndex..(index + 1)];
        }
    }

    throw new InvalidOperationException($"Source block is not balanced: {marker}");
}

static void AssertContains(string source, string expected, string message)
{
    if (!source.Contains(expected, StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"{message} Missing: {expected}");
    }
}

static void AssertDoesNotContain(string source, string forbidden, string message)
{
    if (source.Contains(forbidden, StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"{message} Found: {forbidden}");
    }
}

static void AssertTrue(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertEqual<T>(T expected, T actual, string message)
    where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{message} Expected: {expected}; actual: {actual}.");
    }
}

internal readonly record struct TestCookerOwnership(long Generation, long ContentRevision);

internal sealed class SideEffectProbe
{
    public int InvocationCount { get; private set; }

    public void Invoke()
    {
        InvocationCount++;
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
