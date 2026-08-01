try
{
    var root = FindRepositoryRoot();
    var service = Read(root, "mods/bepinex/src/Save/RuntimeOrderPreparationService.cs");
    var cooking = Read(root, "mods/bepinex/src/Save/RuntimeOrderPreparationService.Cooking.cs");
    var directDelivery = Read(root, "mods/bepinex/src/Save/RuntimeOrderPreparationService.DirectDelivery.cs");
    var settlement = Read(root, "mods/bepinex/src/Save/RuntimeOrderPreparationService.YuumaSettlement.cs");

    VerifyJobRetainsOnlyManagedCookerIdentity(service, cooking);
    VerifyFinalCommitHasFreshCookerBarrier(settlement);
    VerifyResetReacquiresBeforeMutation(directDelivery);
    VerifyExtractionReacquiresBeforeEveryCallback(settlement, directDelivery, cooking);

    Console.WriteLine(
        "PASS: Blood Pond Hell finalization retains no cooker wrapper, validates a fresh exact cooker before "
        + "the irreversible food commit, and reacquires before cooker reset and extraction callbacks without "
        + "rejecting a legal post-extract cooker takeover.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL: {ex}");
    return 1;
}

static void VerifyJobRetainsOnlyManagedCookerIdentity(string service, string cooking)
{
    var job = ExtractBlock(service, "private sealed class AutomationCookingJob");
    AssertContains(
        job,
        "public RuntimeCookerReservation CookerReservation { get; init; }",
        "AutomationCookingJob must retain the canonical managed reservation.");
    AssertContains(
        job,
        "public nint ControllerPointer { get; init; }",
        "AutomationCookingJob must retain the canonical native identity value.");
    AssertDoesNotContain(
        job,
        "object CookController",
        "AutomationCookingJob retained a long-lived IL2CPP cooker wrapper.");

    var registration = ExtractNamedMethod(cooking, "RegisterAutomationCookingJob");
    AssertContains(
        registration,
        "CookerReservation = cookerReservation",
        "Job registration did not persist the exact action reservation.");
    AssertDoesNotContain(
        registration,
        "CookController = cookController",
        "Job registration persisted the start-time IL2CPP wrapper.");

    var reacquire = ExtractNamedMethod(cooking, "TryReacquireAutomationCooker");
    var entriesRead = reacquire.IndexOf(
        "TryReadCookerControllerEntriesFromCookSystem(",
        StringComparison.Ordinal);
    var lockedRead = reacquire.IndexOf("TryReadLockedCookerPositions(", StringComparison.Ordinal);
    var reservationMatch = reacquire.IndexOf("job.CookerReservation.TryMatch(", StringComparison.Ordinal);
    var lockedRejection = reacquire.IndexOf(
        "lockedPositions.Contains(job.CookerReservation.GridPosition)",
        StringComparison.Ordinal);
    var challengeGate = reacquire.IndexOf(
        "job.CookerReservation.EvaluateChallengeGate(",
        StringComparison.Ordinal);
    var stateRead = reacquire.IndexOf(
        "RuntimeCookerReflection.TryReadCookerControllerState(",
        StringComparison.Ordinal);
    var ownershipAfter = reacquire.IndexOf(
        "RuntimeCookingGenerationTracker.TryGetOwnershipSnapshot(",
        reacquire.IndexOf("RuntimeCookingGenerationTracker.TryGetOwnershipSnapshot(", StringComparison.Ordinal) + 1,
        StringComparison.Ordinal);
    var bindingCreation = reacquire.IndexOf("new RuntimeAutomationCookerBinding(", StringComparison.Ordinal);
    AssertTrue(
        lockedRead >= 0
        && entriesRead > lockedRead
        && reservationMatch > entriesRead
        && lockedRejection > reservationMatch
        && stateRead > lockedRejection
        && challengeGate > stateRead
        && ownershipAfter > challengeGate
        && bindingCreation > ownershipAfter,
        "Fresh cooker binding must reject a locked exact reservation before reading controller state, then prove stable ownership before returning a wrapper.");

    foreach (var required in new[]
             {
                 "controllerPointer != job.ControllerPointer",
                 "RuntimeCookerChallengeGateState.Inconsistent",
                 "ownershipBefore != ownershipAfter",
                 "ownershipAfter.Generation == job.Generation",
                 "ownershipAfter.ContentRevision == job.ContentRevision",
                 "if (!ownershipMatches)",
                 "expectedCompletedMutation.HasValue",
                 "ownershipAfter.LastMutation == expectedCompletedMutation.Value",
                 "ownershipAfter.MutationCompleted",
             })
    {
        AssertContains(
            reacquire,
            required,
            $"Fresh cooker binding is missing fail-closed validation '{required}'.");
    }
}

static void VerifyFinalCommitHasFreshCookerBarrier(string settlement)
{
    var finalization = ExtractNamedMethod(settlement, "TryFinalizeYuumaCookingJob");
    var preflight = finalization.IndexOf("TryPreflightYuumaSettlement(", StringComparison.Ordinal);
    var freshCooker = finalization.IndexOf(
        "TryValidateYuumaCookerBeforeFoodCommit(",
        StringComparison.Ordinal);
    var irreversibleClaim = finalization.IndexOf("TryBeginFoodCommit()", StringComparison.Ordinal);
    var finalSetter = finalization.IndexOf("finalFoodSetter.Invoke(", StringComparison.Ordinal);
    AssertTrue(
        preflight >= 0
        && freshCooker > preflight
        && irreversibleClaim > freshCooker
        && finalSetter > irreversibleClaim,
        "The final food transaction must fresh-bind the exact cooker after preflight and before entering its irreversible claim.");

    var beforeIrreversibleClaim = finalization[freshCooker..irreversibleClaim];
    AssertDoesNotContain(
        beforeIrreversibleClaim,
        "MarkUncertain",
        "A side-effect-free fresh-cooker rejection was incorrectly quarantined as an uncertain native commit.");

    var validation = ExtractNamedMethod(settlement, "TryValidateYuumaCookerBeforeFoodCommit");
    var reacquire = validation.IndexOf("TryReacquireAutomationCooker(", StringComparison.Ordinal);
    var resultRead = validation.IndexOf(".State.Result", StringComparison.Ordinal);
    var exactIdentity = validation.IndexOf("IsSameObject(", StringComparison.Ordinal);
    AssertTrue(
        reacquire >= 0 && resultRead > reacquire && exactIdentity > resultRead,
        "The pre-commit cooker barrier must compare the requested cooked food with the result of a fresh exact binding.");
    AssertContains(
        validation,
        "current.State.Result, cookedFood",
        "The pre-commit cooker barrier does not compare the fresh result with the requested cooked food.");
    AssertDoesNotContain(
        validation,
        "job.CookController",
        "The pre-commit cooker barrier reads the retained start-time wrapper.");
}

static void VerifyResetReacquiresBeforeMutation(string directDelivery)
{
    var reset = ExtractNamedMethod(
        directDelivery,
        "TryResetCookControllerAfterCommittedSideEffect");
    var reacquireBefore = reset.IndexOf("TryReacquireAutomationCooker(", StringComparison.Ordinal);
    var firstMutation = FirstNonNegative(
        reset.IndexOf("CloseCookingVisual", StringComparison.Ordinal),
        reset.IndexOf("WriteMember(", StringComparison.Ordinal));
    var reacquireAfter = reset.IndexOf(
        "TryReacquireAutomationCooker(",
        reacquireBefore + 1,
        StringComparison.Ordinal);
    AssertTrue(
        reacquireBefore >= 0
        && firstMutation > reacquireBefore
        && reacquireAfter > firstMutation,
        "Cooker reset must use a fresh exact wrapper before mutation and reacquire again before confirming the result.");
    AssertDoesNotContain(
        reset,
        "job.CookController",
        "Cooker reset can still mutate the retained start-time wrapper.");
}

static void VerifyExtractionReacquiresBeforeEveryCallback(
    string settlement,
    string directDelivery,
    string cooking)
{
    var context = ExtractDeclaration(
        settlement,
        "private sealed record YuumaCookerExtractionContext(");
    AssertDoesNotContain(
        context,
        "object CookController",
        "The extraction context caches a cooker wrapper across native callbacks.");
    AssertDoesNotContain(
        context,
        "object PartnerManager",
        "The extraction context caches a PartnerManager wrapper across native callbacks.");

    var extraction = ExtractNamedMethod(settlement, "TryCompleteYuumaCookerExtraction");
    var firstReacquire = extraction.IndexOf("TryReacquireAutomationCooker(", StringComparison.Ordinal);
    var availability = extraction.IndexOf("AvailabilityMethod.Invoke(", StringComparison.Ordinal);
    var secondReacquire = extraction.IndexOf(
        "TryReacquireAutomationCooker(",
        firstReacquire + 1,
        StringComparison.Ordinal);
    var afterExtract = extraction.IndexOf("ExtractionMethod.Invoke(", StringComparison.Ordinal);
    var thirdReacquire = extraction.IndexOf(
        "TryReacquireAutomationCooker(",
        secondReacquire + 1,
        StringComparison.Ordinal);
    AssertTrue(
        firstReacquire >= 0
        && availability > firstReacquire
        && secondReacquire > availability
        && afterExtract > secondReacquire,
        "Extraction must fresh-bind before availability notification and rebind after that callback before AfterPlayerExtract.");
    AssertTrue(
        thirdReacquire < 0,
        "AfterPlayerExtract may legally start the next PureHellFryer batch, so the old cooker generation must not be reacquired after it returns.");
    AssertDoesNotContain(
        extraction[afterExtract..],
        "RuntimeCookingContentMutation.Extract",
        "A legal post-extract cooker takeover was incorrectly required to preserve the old Extract receipt.");

    var finalization = ExtractNamedMethod(settlement, "TryFinalizeYuumaCookingJob");
    var extractionCall = finalization.IndexOf("TryCompleteYuumaCookerExtraction(", StringComparison.Ordinal);
    var cleanupCommit = finalization.IndexOf("MarkCleanupCommitted()", extractionCall, StringComparison.Ordinal);
    var targetValidation = finalization.IndexOf(
        "TryValidateCurrentYuumaFoodTarget(",
        cleanupCommit,
        StringComparison.Ordinal);
    var orderReacquire = finalization.IndexOf(
        "FindYuumaRuntimeOrder(job.Target, request)",
        targetValidation,
        StringComparison.Ordinal);
    var orderValidation = finalization.IndexOf(
        "TryValidateReacquiredYuumaSettlementOrder(",
        orderReacquire,
        StringComparison.Ordinal);
    AssertTrue(
        extractionCall >= 0
        && cleanupCommit > extractionCall
        && targetValidation > cleanupCommit
        && orderReacquire > targetValidation
        && orderValidation > orderReacquire,
        "A normal extraction callback return must be followed by fresh business-target and exact-order validation before evaluation.");
    var freshBinding = ExtractNamedMethod(cooking, "TryReacquireAutomationCooker");
    AssertContains(
        freshBinding,
        "ownershipAfter.MutationCompleted",
        "Fresh post-mutation binding does not require the matching postfix receipt.");
    AssertDoesNotContain(
        extraction,
        "context.CookController",
        "Extraction invokes a cooker wrapper cached before a native callback.");

    var generalExtraction = ExtractNamedMethod(
        directDelivery,
        "CompleteCookerExtractionAfterReset");
    var generalFirstReacquire = generalExtraction.IndexOf(
        "TryReacquireAutomationCooker(",
        StringComparison.Ordinal);
    var generalAvailability = generalExtraction.IndexOf(
        "OnCookerAvailabilityUpdate",
        generalFirstReacquire,
        StringComparison.Ordinal);
    var generalSecondReacquire = generalExtraction.IndexOf(
        "TryReacquireAutomationCooker(",
        generalFirstReacquire + 1,
        StringComparison.Ordinal);
    var generalAfterExtract = generalExtraction.IndexOf(
        "AfterPlayerExtract",
        generalSecondReacquire,
        StringComparison.Ordinal);
    var generalThirdReacquire = generalExtraction.IndexOf(
        "TryReacquireAutomationCooker(",
        generalSecondReacquire + 1,
        StringComparison.Ordinal);
    AssertTrue(
        generalFirstReacquire >= 0
        && generalAvailability > generalFirstReacquire
        && generalSecondReacquire > generalAvailability
        && generalAfterExtract > generalSecondReacquire
        && generalThirdReacquire < 0,
        "General committed cleanup must fresh-bind before each callback without rejecting a legal post-extract cooker takeover.");
    AssertDoesNotContain(
        generalExtraction[generalAfterExtract..],
        "RuntimeCookingContentMutation.Extract",
        "General cleanup still requires the old Extract receipt after AfterPlayerExtract returns.");
}

static int FirstNonNegative(params int[] values)
{
    return values.Where(value => value >= 0).DefaultIfEmpty(-1).Min();
}

static string Read(string root, string relativePath)
{
    return File.ReadAllText(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
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

    throw new DirectoryNotFoundException("Could not locate the repository root from the smoke output directory.");
}

static string ExtractNamedMethod(string source, string methodName)
{
    var searchIndex = 0;
    while (true)
    {
        var nameIndex = source.IndexOf(methodName + "(", searchIndex, StringComparison.Ordinal);
        if (nameIndex < 0)
        {
            throw new InvalidOperationException($"Source method not found: {methodName}");
        }

        var lineStart = source.LastIndexOf('\n', nameIndex);
        lineStart = lineStart < 0 ? 0 : lineStart + 1;
        var declarationPrefix = source[lineStart..nameIndex];
        if (declarationPrefix.Contains("static", StringComparison.Ordinal)
            && (declarationPrefix.Contains("private", StringComparison.Ordinal)
                || declarationPrefix.Contains("internal", StringComparison.Ordinal)
                || declarationPrefix.Contains("public", StringComparison.Ordinal)))
        {
            return ExtractBlock(source, source[lineStart..(nameIndex + methodName.Length + 1)]);
        }

        searchIndex = nameIndex + methodName.Length;
    }
}

static string ExtractDeclaration(string source, string signature)
{
    var signatureIndex = source.IndexOf(signature, StringComparison.Ordinal);
    if (signatureIndex < 0)
    {
        throw new InvalidOperationException($"Source declaration not found: {signature}");
    }

    var terminator = source.IndexOf(");", signatureIndex, StringComparison.Ordinal);
    if (terminator < 0)
    {
        throw new InvalidOperationException($"Source declaration is incomplete: {signature}");
    }

    return source[signatureIndex..(terminator + 2)];
}

static string ExtractBlock(string source, string signature)
{
    var signatureIndex = source.IndexOf(signature, StringComparison.Ordinal);
    if (signatureIndex < 0)
    {
        throw new InvalidOperationException($"Source block not found: {signature}");
    }

    var openingBrace = source.IndexOf('{', signatureIndex);
    if (openingBrace < 0)
    {
        throw new InvalidOperationException($"Source block has no body: {signature}");
    }

    var depth = 0;
    var inString = false;
    var inCharacter = false;
    var escaped = false;
    for (var index = openingBrace; index < source.Length; index++)
    {
        var current = source[index];
        if (escaped)
        {
            escaped = false;
            continue;
        }

        if ((inString || inCharacter) && current == '\\')
        {
            escaped = true;
            continue;
        }

        if (!inCharacter && current == '"')
        {
            inString = !inString;
            continue;
        }

        if (!inString && current == '\'')
        {
            inCharacter = !inCharacter;
            continue;
        }

        if (inString || inCharacter)
        {
            continue;
        }

        if (current == '{')
        {
            depth++;
        }
        else if (current == '}' && --depth == 0)
        {
            return source[signatureIndex..(index + 1)];
        }
    }

    throw new InvalidOperationException($"Source block is incomplete: {signature}");
}

static void AssertContains(string source, string expected, string message)
{
    AssertTrue(
        source.Contains(expected, StringComparison.Ordinal),
        $"{message} Missing: {expected}");
}

static void AssertDoesNotContain(string source, string forbidden, string message)
{
    AssertTrue(
        !source.Contains(forbidden, StringComparison.Ordinal),
        $"{message} Found: {forbidden}");
}

static void AssertTrue(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
