using System.Text;
using MystiaStewardCompanion.Save;
using MystiaStewardCompanion.Save.SpecialBusiness;
using GuestBase = GameData.Core.Collections.NightSceneUtility.GuestBase;
using NamedGuest = GameData.Core.Collections.NightSceneUtility.NamedGuest;
using OrderBase = NightScene.GuestManagementUtility.GuestsManager.OrderBase;
using NormalOrder = NightScene.GuestManagementUtility.GuestsManager.NormalOrder;
using SpecialOrder = NightScene.GuestManagementUtility.GuestsManager.SpecialOrder;

try
{
    VerifyChallengeAndGuestIdentity();
    VerifyNormalAndSpecialOrders();
    VerifyOrderBaseRuntimeTypeNormalization();
    VerifyControllerConflictFailsClosed();
    VerifyUnknownIdentityFailsClosed();
    VerifyNameAliasesAreIneffective();
    VerifyYuumaAutomationGate();
    VerifyYuumaSettlementTransactionTracker();
    VerifySourceContracts();
    Console.WriteLine("PASS: Blood Pond Hell uses exact Yuuma identity, read-only HUD hooks, generation-scoped targets, and fail-closed automation.");
    return 0;
}

catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL: {ex}");
    return 1;
}

static void VerifyYuumaSettlementTransactionTracker()
{
    var completed = new YuumaSettlementTransactionTracker();
    AssertEqual(
        YuumaSettlementTransactionStage.Ready,
        completed.Stage,
        "A new Yuuma settlement did not start at the only replay-safe state.");
    AssertFalse(completed.TryBeginEvaluation(), "Evaluation started before the food commit and cooker cleanup.");
    AssertFalse(completed.MarkUncertain(), "A side-effect-free Ready transaction became uncertain.");
    AssertTrue(completed.TryBeginFoodCommit(), "The first food-commit claim was rejected.");
    AssertFalse(completed.TryBeginFoodCommit(), "The food-commit claim could be replayed.");
    AssertTrue(completed.MarkFoodCommitted(), "A successful food commit was not latched.");
    AssertTrue(completed.MarkCleanupCommitted(), "A successful cooker cleanup was not latched.");
    AssertTrue(completed.TryBeginEvaluation(), "Evaluation could not start after delivery cleanup.");
    AssertFalse(completed.TryBeginEvaluation(), "The native evaluation claim could be replayed.");
    AssertTrue(completed.MarkEvaluationCommitted(), "A successful native evaluation was not latched.");
    AssertTrue(completed.TryBeginBookkeeping(), "Native post-delivery bookkeeping could not start.");
    AssertFalse(completed.TryBeginBookkeeping(), "Native post-delivery bookkeeping could be replayed.");
    AssertTrue(completed.MarkBookkeepingCommitted(), "A completed settlement was not latched.");
    AssertEqual(
        YuumaSettlementTransactionStage.Completed,
        completed.Stage,
        "The exact settlement sequence did not reach Completed.");
    AssertFalse(completed.MarkUncertain(), "A completed settlement was downgraded to uncertain.");

    var uncertain = new YuumaSettlementTransactionTracker();
    AssertTrue(uncertain.TryBeginFoodCommit(), "The uncertainty fixture did not enter an irreversible call.");
    AssertTrue(uncertain.MarkUncertain(), "An in-flight native call could not be quarantined.");
    AssertEqual(
        YuumaSettlementTransactionStage.Uncertain,
        uncertain.Stage,
        "An in-flight failure did not enter the terminal Uncertain state.");
    AssertFalse(uncertain.TryBeginFoodCommit(), "An uncertain food commit could be replayed.");
    AssertFalse(uncertain.MarkFoodCommitted(), "An uncertain transaction could advance after quarantine.");
    AssertFalse(uncertain.TryBeginEvaluation(), "An uncertain transaction could invoke native evaluation.");
    AssertFalse(uncertain.TryBeginBookkeeping(), "An uncertain transaction could invoke native bookkeeping.");
}

static void VerifyChallengeAndGuestIdentity()
{
    var module = new YuumaChallengeOrderModule();
    AssertTrue(
        module.MatchesChallenge(SpecialBusinessChallengeTypes.BloodPondHell),
        "The module did not match the exact Blood Pond Hell challenge identity.");
    AssertFalse(
        module.MatchesChallenge("Challenge_BloodPondHell"),
        "A challenge alias activated the Blood Pond Hell module.");
    AssertFalse(
        module.MatchesChallenge("story_bloodpondhell"),
        "Case-insensitive challenge matching was accepted.");
    AssertFalse(
        module.MatchesChallenge("饕餮尤魔"),
        "A display name activated the Blood Pond Hell module.");
    AssertEqual(1003, SpecialBusinessGuestIds.YuumaBoss, "The exact Yuuma runtime role ID changed.");
}

static void VerifyNormalAndSpecialOrders()
{
    RuntimeReflectionUtility.ResetRecordedCasts();
    var normalGuest = new GuestBase(SpecialBusinessGuestIds.YuumaBoss);
    var normal = Classify(
        new NormalOrder(normalGuest),
        new OrderController(normalGuest));
    AssertAllowedRole(normal, SpecialBusinessOrderRoles.YuumaBoss, "The exact NormalOrder boss identity was not allowed.");

    var specialGuest = new GuestBase(SpecialBusinessGuestIds.YuumaBoss);
    var special = Classify(
        new SpecialOrder(specialGuest),
        new OrderController(specialGuest));
    AssertAllowedRole(special, SpecialBusinessOrderRoles.YuumaBoss, "The exact SpecialOrder boss identity was not allowed.");

    var ordinaryGuest = new GuestBase(42);
    var ordinary = Classify(
        new NormalOrder(ordinaryGuest),
        new OrderController(ordinaryGuest));
    AssertTrue(ordinary == SpecialBusinessOrderClassification.Standard, "A verified non-boss order did not remain standard.");
    AssertTrue(ordinary.AutomationAllowed, "A verified non-boss order was incorrectly blocked.");
    AssertEqual(
        0,
        RuntimeReflectionUtility.RecordedCastTargets.Count,
        "An already exact NormalOrder or SpecialOrder unexpectedly entered runtime conversion.");
}

static void VerifyOrderBaseRuntimeTypeNormalization()
{
    var boss = new GuestBase(SpecialBusinessGuestIds.YuumaBoss);

    RuntimeReflectionUtility.ResetRecordedCasts();
    var normal = Classify(
        new OrderBase(new NormalOrder(boss), specialOrder: null),
        new OrderController(boss));
    AssertAllowedRole(
        normal,
        SpecialBusinessOrderRoles.YuumaBoss,
        "An OrderBase wrapper with exactly one NormalOrder conversion was not allowed.");
    AssertOrderBaseCastPair();

    RuntimeReflectionUtility.ResetRecordedCasts();
    var special = Classify(
        new OrderBase(normalOrder: null, specialOrder: new SpecialOrder(boss)),
        new OrderController(boss));
    AssertAllowedRole(
        special,
        SpecialBusinessOrderRoles.YuumaBoss,
        "An OrderBase wrapper with exactly one SpecialOrder conversion was not allowed.");
    AssertOrderBaseCastPair();

    RuntimeReflectionUtility.ResetRecordedCasts();
    var ordinary = new GuestBase(42);
    var ordinaryNormal = Classify(
        new OrderBase(new NormalOrder(ordinary), specialOrder: null),
        new OrderController(ordinary));
    AssertTrue(
        ordinaryNormal == SpecialBusinessOrderClassification.Standard,
        "A normalized non-boss OrderBase did not remain standard.");
    AssertTrue(
        ordinaryNormal.AutomationAllowed,
        "A normalized non-boss OrderBase was incorrectly blocked.");
    AssertOrderBaseCastPair();

    RuntimeReflectionUtility.ResetRecordedCasts();
    var missingBossController = Classify(
        new OrderBase(new NormalOrder(boss), specialOrder: null),
        controller: null);
    AssertBlockedRole(
        missingBossController,
        SpecialBusinessOrderRoles.YuumaUnverified,
        "A normalized boss OrderBase without controller identity did not fail closed.");
    AssertOrderBaseCastPair();

    RuntimeReflectionUtility.ResetRecordedCasts();
    var neither = Classify(
        new OrderBase(normalOrder: null, specialOrder: null),
        new OrderController(boss));
    AssertBlockedRole(
        neither,
        SpecialBusinessOrderRoles.YuumaUnverified,
        "An OrderBase wrapper with no valid concrete conversion did not fail closed.");
    AssertContains(
        neither.AutomationBlockReason,
        "neither NormalOrder nor SpecialOrder conversion succeeded",
        "The failed OrderBase conversion did not retain a bounded diagnostic reason.");
    AssertOrderBaseCastPair();

    RuntimeReflectionUtility.ResetRecordedCasts();
    var ambiguous = Classify(
        new OrderBase(new NormalOrder(boss), new SpecialOrder(boss)),
        new OrderController(boss));
    AssertBlockedRole(
        ambiguous,
        SpecialBusinessOrderRoles.YuumaUnverified,
        "An OrderBase wrapper with two valid concrete conversions did not fail closed.");
    AssertContains(
        ambiguous.AutomationBlockReason,
        "both NormalOrder and SpecialOrder conversions succeeded",
        "The ambiguous OrderBase conversion did not retain a bounded diagnostic reason.");
    AssertOrderBaseCastPair();

    RuntimeReflectionUtility.ResetRecordedCasts();
    var wrongWrapper = Classify(
        new OrderBase(new AliasOrder("not a NormalOrder"), specialOrder: null),
        new OrderController(boss));
    AssertBlockedRole(
        wrongWrapper,
        SpecialBusinessOrderRoles.YuumaUnverified,
        "An OrderBase conversion returning a lookalike wrapper did not fail closed.");
    AssertOrderBaseCastPair();

    RuntimeReflectionUtility.ResetRecordedCasts();
    var conflictingController = Classify(
        new OrderBase(new NormalOrder(boss), specialOrder: null),
        new OrderController(new GuestBase(42)));
    AssertBlockedRole(
        conflictingController,
        SpecialBusinessOrderRoles.YuumaUnverified,
        "A normalized OrderBase with conflicting controller identity did not fail closed.");
    AssertOrderBaseCastPair();
}

static void AssertOrderBaseCastPair()
{
    AssertEqual(
        2,
        RuntimeReflectionUtility.RecordedCastTargets.Count,
        "OrderBase normalization did not attempt exactly two concrete runtime conversions.");
    AssertEqual(
        "NightScene.GuestManagementUtility.GuestsManager+NormalOrder",
        RuntimeReflectionUtility.RecordedCastTargets[0],
        "OrderBase normalization did not attempt the exact NormalOrder conversion first.");
    AssertEqual(
        "NightScene.GuestManagementUtility.GuestsManager+SpecialOrder",
        RuntimeReflectionUtility.RecordedCastTargets[1],
        "OrderBase normalization did not attempt the exact SpecialOrder conversion second.");
}

static void VerifyControllerConflictFailsClosed()
{
    var orderGuest = new GuestBase(SpecialBusinessGuestIds.YuumaBoss);
    AssertBlockedRole(
        Classify(new NormalOrder(orderGuest), controller: null),
        SpecialBusinessOrderRoles.YuumaUnverified,
        "A boss order without a controller did not fail closed.");

    var conflictingGuest = new GuestBase(SpecialBusinessGuestIds.YuumaBoss + 1);
    var mismatch = Classify(
        new NormalOrder(orderGuest),
        new OrderController(conflictingGuest));
    AssertBlockedRole(
        mismatch,
        SpecialBusinessOrderRoles.YuumaUnverified,
        "A conflicting controller identity did not fail closed.");

    var nonBossOrderGuest = new GuestBase(42);
    var nonBossMismatch = Classify(
        new SpecialOrder(nonBossOrderGuest),
        new OrderController(new GuestBase(43)));
    AssertBlockedRole(
        nonBossMismatch,
        SpecialBusinessOrderRoles.YuumaUnverified,
        "A conflicting non-boss controller identity did not fail closed.");

    var missingControllerIdentity = Classify(
        new SpecialOrder(orderGuest),
        new MissingOrderingGuestController());
    AssertBlockedRole(
        missingControllerIdentity,
        SpecialBusinessOrderRoles.YuumaUnverified,
        "A boss order with an unreadable controller identity did not fail closed.");

    var ordinaryWithoutController = Classify(
        new NormalOrder(new GuestBase(42)),
        controller: null);
    AssertTrue(
        ordinaryWithoutController == SpecialBusinessOrderClassification.Standard,
        "A verified non-boss order without a controller did not remain standard.");

    var ordinaryWithUnreadableController = Classify(
        new SpecialOrder(new GuestBase(42)),
        new MissingOrderingGuestController());
    AssertTrue(
        ordinaryWithUnreadableController == SpecialBusinessOrderClassification.Standard,
        "A verified non-boss order with an unreadable controller did not remain standard.");
}

static void VerifyUnknownIdentityFailsClosed()
{
    AssertBlockedRole(
        Classify(order: null, controller: null),
        SpecialBusinessOrderRoles.YuumaUnverified,
        "A null order did not fail closed.");
    AssertBlockedRole(
        Classify(new AliasOrder("饕餮尤魔"), controller: null),
        SpecialBusinessOrderRoles.YuumaUnverified,
        "An unsupported order type did not fail closed.");

    var wrongIdDeclaration = new AliasGuest(SpecialBusinessGuestIds.YuumaBoss, "饕餮尤魔");
    AssertBlockedRole(
        Classify(new NormalOrder(wrongIdDeclaration), controller: null),
        SpecialBusinessOrderRoles.YuumaUnverified,
        "A lookalike Id property outside GuestBase did not fail closed.");
}

static void VerifyNameAliasesAreIneffective()
{
    var aliasNames = new[]
    {
        "Yuuma",
        "Toutetsu",
        "饕餮尤魔",
        "饕餮尢魔",
    };

    foreach (var alias in aliasNames)
    {
        var nonBoss = new NamedGuest(77, alias);
        var result = Classify(
            new NormalOrder(nonBoss),
            new OrderController(nonBoss));
        AssertTrue(
            result == SpecialBusinessOrderClassification.Standard,
            $"Name/probe alias '{alias}' changed a verified non-boss identity.");
    }
}

static void VerifyYuumaAutomationGate()
{
    var boss = new GuestBase(SpecialBusinessGuestIds.YuumaBoss);
    AssertAllowedRole(
        Classify(new NormalOrder(boss), new OrderController(boss)),
        SpecialBusinessOrderRoles.YuumaBoss,
        "A fully verified Yuuma NormalOrder did not allow controlled automation.");
    AssertAllowedRole(
        Classify(new SpecialOrder(boss), new OrderController(boss)),
        SpecialBusinessOrderRoles.YuumaBoss,
        "A fully verified Yuuma SpecialOrder did not allow controlled automation.");

    foreach (var classification in new[]
             {
                 Classify(new NormalOrder(boss), controller: null),
                 Classify(new SpecialOrder(boss), controller: null),
                 Classify(new NormalOrder(boss), new OrderController(new GuestBase(1))),
                 Classify(order: null, controller: null),
             })
    {
        AssertFalse(classification.AutomationAllowed, $"Yuuma role '{classification.Role}' unexpectedly allowed automation.");
        AssertTrue(
            classification.Role is SpecialBusinessOrderRoles.YuumaUnverified,
            $"Unexpected Yuuma fail-closed role '{classification.Role}'.");
        AssertTrue(
            classification.AutomationBlockReason.Length > 0,
            $"Yuuma role '{classification.Role}' did not explain the automation block.");
    }
}

static SpecialBusinessOrderClassification Classify(object? order, object? controller)
{
    return new YuumaChallengeOrderModule().Classify(
        SpecialBusinessChallengeTypes.BloodPondHell,
        order,
        controller,
        "smoke");
}

static void VerifySourceContracts()
{
    var root = FindRepositoryRoot();
    var contextSource = File.ReadAllText(Path.Combine(
        root,
        "mods",
        "bepinex",
        "src",
        "Save",
        "RuntimeSpecialBusinessContextService.cs"));
    var moduleSource = File.ReadAllText(Path.Combine(
        root,
        "mods",
        "bepinex",
        "src",
        "Save",
        "SpecialBusiness",
        "YuumaChallengeOrderModule.cs"));
    var idsSource = File.ReadAllText(Path.Combine(
        root,
        "mods",
        "bepinex",
        "src",
        "Save",
        "SpecialBusiness",
        "SpecialBusinessIds.cs"));
    var deliverySource = File.ReadAllText(Path.Combine(
        root,
        "mods",
        "bepinex",
        "src",
        "Save",
        "RuntimeOrderPreparationService.Delivery.cs"));
    var directDeliverySource = File.ReadAllText(Path.Combine(
        root,
        "mods",
        "bepinex",
        "src",
        "Save",
        "RuntimeOrderPreparationService.DirectDelivery.cs"));
    var policySource = File.ReadAllText(Path.Combine(
        root,
        "mods",
        "bepinex",
        "src",
        "Save",
        "SpecialBusiness",
        "RuntimeOrderPreparationService.SpecialFoodTargetPolicy.cs"));
    var serviceSource = File.ReadAllText(Path.Combine(
        root,
        "mods",
        "bepinex",
        "src",
        "Save",
        "RuntimeOrderPreparationService.cs"));
    var cookingSource = File.ReadAllText(Path.Combine(
        root,
        "mods",
        "bepinex",
        "src",
        "Save",
        "RuntimeOrderPreparationService.Cooking.cs"));
    var matchingSource = File.ReadAllText(Path.Combine(
        root,
        "mods",
        "bepinex",
        "src",
        "Save",
        "RuntimeOrderPreparationService.OrderMatching.cs"));
    var specialCaptureSource = File.ReadAllText(Path.Combine(
        root,
        "mods",
        "bepinex",
        "src",
        "Save",
        "SpecialOrderRuntimeCapture.cs"));
    var normalCaptureSource = File.ReadAllText(Path.Combine(
        root,
        "mods",
        "bepinex",
        "src",
        "Save",
        "NormalOrderRuntimeCapture.cs"));
    var pluginSource = File.ReadAllText(Path.Combine(
        root,
        "mods",
        "bepinex",
        "src",
        "Plugin",
        "MystiaStewardCompanionPlugin.cs"));
    var diagnosticsSource = File.ReadAllText(Path.Combine(
        root,
        "mods",
        "bepinex",
        "src",
        "Save",
        "SpecialBusiness",
        "SpecialBusinessDiagnostics.cs"));
    var cookingPolicySource = File.ReadAllText(Path.Combine(
        root,
        "mods",
        "bepinex",
        "src",
        "Save",
        "SpecialBusiness",
        "RuntimeOrderPreparationService.WackyCookingPolicy.cs"));
    var localApiServerSource = File.ReadAllText(Path.Combine(
        root,
        "mods",
        "bepinex",
        "src",
        "LocalApi",
        "LocalApiServer.cs"));
    var orderPreparationModelsSource = File.ReadAllText(Path.Combine(
        root,
        "mods",
        "bepinex",
        "src",
        "LocalApi",
        "OrderPreparationModels.cs"));
    var localApiModelsSource = File.ReadAllText(Path.Combine(
        root,
        "mods",
        "bepinex",
        "src",
        "LocalApi",
        "LocalApiModels.cs"));

    VerifyExactHudHooks(contextSource);
    VerifyChallengeMapping(contextSource, idsSource);
    VerifyGenerationGate(contextSource);
    VerifyIncompleteDualTagClearsTarget(contextSource);
    VerifyCaptureFailureDiagnosticsAreBounded(contextSource);
    VerifyActiveYuumaGenerationAndOrderStatusContracts(
        contextSource,
        specialCaptureSource);
    VerifyDiagnosticIdentityAndDedupe(diagnosticsSource, cookingPolicySource);
    VerifyYuumaNativeDeliveryObserverRemoved(
        root,
        contextSource,
        pluginSource);
    VerifySideEffectMethodsAreBanned(contextSource, moduleSource, deliverySource, policySource);
    VerifyYuumaTargetRevisionPipeline(
        serviceSource,
        cookingSource,
        directDeliverySource,
        policySource,
        contextSource,
        localApiServerSource,
        orderPreparationModelsSource,
        localApiModelsSource);
    VerifyYuumaSettlementContract(
        serviceSource,
        cookingSource,
        deliverySource,
        directDeliverySource,
        policySource,
        contextSource,
        specialCaptureSource,
        normalCaptureSource,
        matchingSource,
        File.ReadAllText(Path.Combine(
            root,
            "mods",
            "bepinex",
            "src",
            "Save",
            "RuntimeOrderPreparationService.YuumaSettlement.cs")),
        File.ReadAllText(Path.Combine(
            root,
            "mods",
            "bepinex",
            "src",
            "Save",
            "SpecialBusiness",
            "YuumaSettlementTransactionTracker.cs")));
}

static void VerifyActiveYuumaGenerationAndOrderStatusContracts(
    string contextSource,
    string specialCaptureSource)
{
    var activeGeneration = Normalize(ExtractMethod(
        contextSource,
        "internal static bool TryGetActiveYuumaGeneration("));
    foreach (var gate in new[]
             {
                 "RuntimeNightBusinessLifecycle.IsActive",
                 "_targetBusinessGeneration!=activeGeneration",
                 "SpecialBusinessChallengeTypes.BloodPondHell",
                 "!string.Equals(_targetKind,\"yuuma\",StringComparison.Ordinal)",
             })
    {
        AssertContains(
            activeGeneration,
            Normalize(gate),
            $"The active Yuuma generation gate lost '{gate}'.");
    }

    var orderSystemChanged = Normalize(ExtractMethod(
        specialCaptureSource,
        "private static void OnOrderSystemChanged("));
    AssertContains(
        orderSystemChanged,
        "if(__2==null){NoteNotApplicable(\"OrderSystemChanged\",\"nullorder\");return;}",
        "A null native order-system callback is still recorded as a parse failure.");
}

static void VerifyDiagnosticIdentityAndDedupe(
    string diagnosticsSource,
    string cookingPolicySource)
{
    var objectKey = Normalize(ExtractMethod(
        diagnosticsSource,
        "private static string ObjectKey("));
    AssertContains(
        objectKey,
        "valueisIl2CppObjectBasenativeObject",
        "Special-business diagnostics no longer prefer the stable native IL2CPP pointer.");
    AssertContains(
        objectKey,
        "varpointer=nativeObject.Pointer;if(pointer!=IntPtr.Zero)return$\"native:0x{pointer.ToInt64():X}\";",
        "Special-business native diagnostic keys lost their explicit identity namespace.");
    AssertContains(
        objectKey,
        "catch",
        "A stale IL2CPP diagnostic wrapper can escape into order classification.");
    AssertContains(
        objectKey,
        "$\"managed:0x{RuntimeHelpers.GetHashCode(value):X}\"",
        "Managed-only diagnostic objects lost their distinct fallback namespace.");

    foreach (var signature in new[]
             {
                 "public static void AppendYuumaOrderClassification(",
                 "public static void AppendWackyOrderClassification(",
             })
    {
        var classificationDiagnostic = Normalize(ExtractMethod(diagnosticsSource, signature));
        AssertContains(
            classificationDiagnostic,
            "if(!AggregateModLogService.Enabled)return;",
            $"Classification diagnostic '{signature}' still builds native diagnostics while logging is disabled.");
        AssertContains(
            classificationDiagnostic,
            "catch",
            $"Classification diagnostic '{signature}' can affect the gameplay classification path.");
    }

    var cookingDiagnostic = Normalize(ExtractMethod(
        cookingPolicySource,
        "private static void AppendSpecialFoodTargetCookingJobDiagnostic("));
    foreach (var keyPart in new[]
             {
                 "RuntimeNightBusinessLifecycle.Generation",
                 "job.JobId",
                 "eventName",
                 "decision",
                 "job.SpecialFoodTargetRevision",
                 "job.YuumaSettlementTracker.Stage",
                 "actualFoodId",
             })
    {
        AssertContains(
            cookingDiagnostic,
            Normalize(keyPart),
            $"Cooking-job diagnostic dedupe no longer includes '{keyPart}'.");
    }
    AssertContains(
        cookingDiagnostic,
        "\"BloodPondHellCookingJobDiagnostic\",lines,onceKey",
        "Blood Pond Hell cooking diagnostics no longer use the bounded once key.");
}

static void VerifyExactHudHooks(string source)
{
    var attach = Normalize(ExtractMethod(source, "private static void TryAttach("));
    var expectedHooks = new[]
    {
        ("SetTargetTag", "new[]{typeof(string),typeof(string),typeof(bool)}", "OnYuumaTargetTagSet"),
        ("SetContext", "new[]{typeof(string),typeof(int),typeof(int),typeof(int),typeof(int),typeof(Il2CppSystem.Action)}", "OnYuumaContextSet"),
        ("SetTargetProgress", "new[]{typeof(int)}", "OnYuumaTargetProgressSet"),
        ("SetAngerProgress", "new[]{typeof(int)}", "OnYuumaAngerProgressSet"),
        ("SetTargetTime", "new[]{typeof(float)}", "OnYuumaTargetTimeSet"),
        ("SetTargetProgressImmediate", "new[]{typeof(int),typeof(int)}", "OnYuumaTargetProgressImmediate"),
    };

    foreach (var (methodName, parameters, callback) in expectedHooks)
    {
        var expected = $"PatchExactInstanceMethod(_harmony,IncomeControllerYuumaTypeName,\"{methodName}\",{parameters},nameof({callback}),patchedNow,missing);";
        AssertContains(attach, expected, $"Yuuma HUD hook '{methodName}' lost its exact signature.");
    }

    AssertEqual(
        expectedHooks.Length,
        CountOccurrences(attach, "IncomeControllerYuumaTypeName"),
        "Yuuma attached a HUD method outside the six audited setters.");
    AssertFalse(
        attach.Contains("PatchMethod(_harmony,IncomeControllerYuumaTypeName", StringComparison.Ordinal),
        "Yuuma fell back to the broad parameter-count hook resolver.");

    var resolver = Normalize(ExtractMethod(source, "private static void PatchExactInstanceMethod("));
    AssertContains(
        resolver,
        "GetMethods(BindingFlags.Public|BindingFlags.Instance|BindingFlags.DeclaredOnly)",
        "The exact HUD resolver no longer requires a declared public instance method.");
    AssertContains(
        resolver,
        "method.ReturnType==typeof(void)",
        "The exact HUD resolver no longer requires a void return type.");
    AssertContains(
        resolver,
        "method.GetParameters().Select(parameter=>parameter.ParameterType).SequenceEqual(parameterTypes)",
        "The exact HUD resolver no longer checks the complete parameter signature.");
    AssertContains(
        resolver,
        ".SingleOrDefault(",
        "The exact HUD resolver no longer rejects ambiguous overloads.");
    AssertContains(
        resolver,
        "harmony.Patch(target,postfix:newHarmonyMethod(postfix));",
        "The exact HUD resolver no longer observes the native call through a postfix-only patch.");
}

static void VerifyChallengeMapping(string contextSource, string idsSource)
{
    AssertContains(
        Normalize(idsSource),
        "publicconststringBloodPondHell=\"Story_BloodPondHell\";",
        "The Blood Pond Hell raw challenge identity changed.");

    var normalizeChallenge = Normalize(ExtractMethod(contextSource, "private static string NormalizeChallengeTypeText("));
    AssertContains(
        normalizeChallenge,
        "\"6\"=>SpecialBusinessChallengeTypes.BloodPondHell",
        "Challenge value 6 no longer maps to Blood Pond Hell.");

    var expectedTargetKind = Normalize(ExtractMethod(contextSource, "private static string GetExpectedTargetKind("));
    AssertContains(
        expectedTargetKind,
        "SpecialBusinessChallengeTypes.BloodPondHell=>\"yuuma\"",
        "Blood Pond Hell no longer owns the Yuuma HUD target.");
}

static void VerifyGenerationGate(string source)
{
    AssertContains(
        Normalize(source),
        "privatestaticlong_targetBusinessGeneration;",
        "The HUD target no longer stores the night-business generation.");

    var switchContext = Normalize(ExtractMethod(source, "private static void SwitchTargetContextLocked("));
    AssertContains(
        switchContext,
        "_targetBusinessGeneration=RuntimeNightBusinessLifecycle.Generation;",
        "A new HUD target context did not capture the current night-business generation.");

    var contextMatches = Normalize(ExtractMethod(source, "private static bool TargetContextMatchesLocked("));
    AssertContains(
        contextMatches,
        "_targetBusinessGeneration==RuntimeNightBusinessLifecycle.Generation",
        "HUD target reads no longer require the current night-business generation.");

    var readTarget = Normalize(ExtractMethod(source, "private static SpecialBusinessTarget ReadTargetForChallenge("));
    AssertContains(
        readTarget,
        "if(!TargetContextMatchesLocked(rawChallengeType,expectedKind)){returnSpecialBusinessTarget.Empty;}",
        "A stale HUD target is no longer rejected at the projection boundary.");

    var resetTarget = Normalize(ExtractMethod(source, "private static void ResetTargetStateLocked("));
    AssertContains(
        resetTarget,
        "_targetBusinessGeneration=0;",
        "Clearing a HUD target did not clear its generation owner.");
}

static void VerifyIncompleteDualTagClearsTarget(string source)
{
    var update = Normalize(ExtractMethod(source, "private static void UpdateYuumaFoodTarget("));
    AssertContains(
        update,
        "_foodTargetTags=complete?normalized:Array.Empty<string>();",
        "An incomplete Yuuma dual-Tag callback no longer clears the published target.");
    AssertContains(
        update,
        "varcomplete=firstTag.Length>0&&secondTag.Length>0&&normalized.Length==2;",
        "Yuuma publishes a dual-Tag target without exactly two normalized distinct Tags.");
    AssertContains(
        update,
        "SwitchTargetContextLocked(rawChallengeType,\"yuuma\");",
        "Yuuma Tag capture no longer binds the target to its exact owner.");
    AssertContains(
        update,
        "firstTag.Length>0&&secondTag.Length>0",
        "Yuuma Tag completeness no longer requires both raw Tag values.");
}

static void VerifyCaptureFailureDiagnosticsAreBounded(string source)
{
    var normalizedSource = Normalize(source);
    AssertContains(
        normalizedSource,
        "privateconstintMaxCaptureFailureDiagnostics=128;",
        "Capture failure diagnostics lost their fixed capacity.");

    var callback = Normalize(ExtractMethod(source, "private static void RunCaptureCallback("));
    AssertContains(
        callback,
        "CaptureFailureDiagnosticOrder.Enqueue(diagnostic);",
        "New capture failures are not tracked in bounded insertion order.");
    AssertContains(
        callback,
        "while(CaptureFailureDiagnosticOrder.Count>MaxCaptureFailureDiagnostics)",
        "Capture failure diagnostics are no longer evicted at the fixed capacity.");
    AssertContains(
        callback,
        "CaptureFailureDiagnostics.Remove(CaptureFailureDiagnosticOrder.Dequeue());",
        "Evicted capture failures are not removed from the deduplication set.");

    var clear = Normalize(ExtractMethod(source, "public static void ClearForBusinessEnd("));
    AssertContains(
        clear,
        "CaptureFailureDiagnostics.Clear();",
        "Capture failure deduplication state is not cleared between businesses.");
    AssertContains(
        clear,
        "CaptureFailureDiagnosticOrder.Clear();",
        "Capture failure insertion order is not cleared between businesses.");
}

static void VerifySideEffectMethodsAreBanned(
    string contextSource,
    string moduleSource,
    string deliverySource,
    string policySource)
{
    var forbiddenMethods = new[]
    {
        "SetSpoonPosition",
        "MainChallengeLoop",
        "RefreshYuumaWantedTag",
        "AddAnger",
        "AttackYuuma",
        "YuumaOverrideEvaluationCallback",
        "checkIfSatisfyOrder",
        "TryUpdateData",
        "TryUpdateAngerData",
        "UpdateVisual",
        "ResetParams",
    };

    foreach (var method in forbiddenMethods)
    {
        AssertFalse(
            contextSource.Contains(method, StringComparison.Ordinal),
            $"Yuuma runtime capture references forbidden side-effect method '{method}'.");
        AssertFalse(
            moduleSource.Contains(method, StringComparison.Ordinal),
            $"Yuuma order classification references forbidden side-effect method '{method}'.");
        AssertFalse(
            deliverySource.Contains(method, StringComparison.Ordinal),
            $"Yuuma automation delivery references forbidden side-effect method '{method}'.");
        AssertFalse(
            policySource.Contains(method, StringComparison.Ordinal),
            $"Yuuma automation policy references forbidden side-effect method '{method}'.");
    }

    var attach = ExtractMethod(contextSource, "private static void TryAttach(");
    AssertFalse(
        attach.Contains("Method_Internal_", StringComparison.Ordinal),
        "Yuuma HUD attachment uses a generated Method_Internal_* entry.");

    var moduleLiterals = Normalize(moduleSource);
    AssertFalse(
        moduleLiterals.Contains(".ContainsAny(", StringComparison.Ordinal)
            || moduleLiterals.Contains(".IsGuest(", StringComparison.Ordinal),
        "Yuuma order classification restored name-based identity helpers.");
    AssertContains(
        moduleLiterals,
        "SpecialBusinessModuleRegistry.AllowedSpecialOrder(SpecialBusinessOrderRoles.YuumaBoss",
        "The exact verified Yuuma boss role no longer opens the controlled automation path.");
}

static void VerifyYuumaNativeDeliveryObserverRemoved(
    string root,
    string contextSource,
    string pluginSource)
{
    var observerPath = Path.Combine(
        root,
        "mods",
        "bepinex",
        "src",
        "Save",
        "SpecialBusiness",
        "YuumaNativeDeliveryObserver.cs");
    AssertFalse(
        File.Exists(observerPath),
        "The ineffective native delivery observer source was restored.");
    var productionSourceRoot = Path.Combine(root, "mods", "bepinex", "src");
    foreach (var path in Directory.EnumerateFiles(
                 productionSourceRoot,
                 "*.cs",
                 SearchOption.AllDirectories))
    {
        var productionSource = Normalize(File.ReadAllText(path));
        AssertDoesNotContain(
            productionSource,
            "\"OpenServePanel\"",
            $"Production source restored the unverified native delivery entry: {path}");
        if (productionSource.Contains("WorkSceneServePannel", StringComparison.Ordinal))
        {
            AssertDoesNotContain(
                productionSource,
                "\"OnPanelClose\"",
                $"Production source restored the non-unique serve-panel close hook: {path}");
        }
    }
    foreach (var source in new[]
             {
                 contextSource,
                 pluginSource,
             })
    {
        AssertDoesNotContain(
            Normalize(source),
            "YuumaNativeDeliveryObserver",
            "Production code still references the removed native delivery observer.");
    }
}

static void VerifyYuumaTargetRevisionPipeline(
    string serviceSource,
    string cookingSource,
    string directDeliverySource,
    string policySource,
    string contextSource,
    string localApiServerSource,
    string orderPreparationModelsSource,
    string localApiModelsSource)
{
    AssertContains(
        Normalize(orderPreparationModelsSource),
        "publiclongSpecialTargetRevision{get;init;}",
        "The order request no longer carries the Blood Pond Hell target revision.");
    var requestParser = Normalize(ExtractMethod(
        localApiServerSource,
        "private string BuildOrderActionJson("));
    AssertContains(
        requestParser,
        "SpecialTargetRevision=ReadLongQuery(query,\"specialTargetRevision\",0)",
        "The Local API no longer parses target revision as a long with a zero default.");

    var rareTargetFactory = Normalize(ExtractMethod(
        serviceSource,
        "public static CookingCollectionTarget ForRareOrder("));
    AssertContains(
        rareTargetFactory,
        "SpecialFoodTargetRevision=request.SpecialTargetRevision",
        "Rare-order targets no longer retain the request revision.");
    var normalTargetFactory = Normalize(ExtractMethod(
        serviceSource,
        "public static CookingCollectionTarget ForNormalOrder("));
    AssertContains(
        normalTargetFactory,
        "longspecialFoodTargetRevision",
        "Normal-order target construction no longer accepts the request revision.");
    AssertContains(
        normalTargetFactory,
        "SpecialFoodTargetRevision=specialFoodTargetRevision",
        "Normal-order targets no longer retain the request revision.");

    var syntheticRequest = Normalize(ExtractMethod(
        directDeliverySource,
        "private static OrderPreparationRequest BuildOrderRequestFromCookingTarget("));
    AssertContains(
        syntheticRequest,
        "SpecialTargetRevision=target.SpecialFoodTargetRevision",
        "Fresh runtime-order lookup requests no longer retain the target revision.");

    var cookingJobRegistration = Normalize(ExtractMethod(
        cookingSource,
        "private static AutomationCookingJob RegisterAutomationCookingJob("));
    AssertContains(
        cookingJobRegistration,
        "longspecialFoodTargetRevision",
        "Cooking-job registration no longer receives the pre-side-effect revision.");
    AssertContains(
        cookingJobRegistration,
        "SpecialFoodTargetRevision=specialFoodTargetRevision",
        "Cooking-job registration no longer latches the pre-side-effect revision.");
    var jobSnapshot = Normalize(ExtractMethod(
        serviceSource,
        "public AutomationCookingJobSnapshot ToSnapshot("));
    AssertContains(
        jobSnapshot,
        "SpecialTargetRevision=SpecialFoodTargetRevision",
        "The automation job snapshot no longer publishes the latched revision.");
    AssertContains(
        Normalize(localApiModelsSource),
        "classAutomationCookingJobSnapshot",
        "The Local API automation job snapshot model was removed.");
    AssertContains(
        Normalize(localApiModelsSource),
        "publiclongSpecialTargetRevision{get;init;}",
        "The Local API automation job snapshot no longer exposes revision.");

    var sameTarget = Normalize(ExtractMethod(
        cookingSource,
        "private static bool IsSameCookingCollectionTarget("));
    AssertContains(
        sameTarget,
        "left.SpecialFoodTargetRevision!=right.SpecialFoodTargetRevision",
        "Cooking jobs from different target revisions can be reused as one target.");

    var requestValidation = Normalize(ExtractMethod(
        policySource,
        "private static bool TryValidateRequestedSpecialFoodTargetPolicy("));
    AssertContains(
        requestValidation,
        "request.SpecialTargetRevision<=0",
        "A Yuuma request no longer requires a positive revision.");
    AssertContains(
        requestValidation,
        "request.SpecialTargetRevision!=activeYuumaRevision",
        "A Yuuma request no longer requires the exact current revision.");
    AssertContains(
        requestValidation,
        "if(request.SpecialTargetRevision!=0)",
        "Ordinary and Yuyuko requests are no longer isolated from the Yuuma revision protocol.");

    var currentTargetValidation = Normalize(ExtractMethod(
        policySource,
        "private static bool TryValidateCurrentSpecialFoodTargetPolicy("));
    AssertContains(
        currentTargetValidation,
        "target.SpecialFoodTargetRevision<=0",
        "A running Yuuma target no longer requires a positive revision.");
    AssertContains(
        currentTargetValidation,
        "target.SpecialFoodTargetRevision!=currentRevision",
        "A running Yuuma target no longer requires the exact current revision.");
    AssertContains(
        currentTargetValidation,
        "if(target.SpecialFoodTargetRevision!=0)",
        "Ordinary and Yuyuko cooking targets are no longer revision zero.");

    var revisionCapture = Normalize(ExtractMethod(
        policySource,
        "private static bool TryCaptureYuumaFoodTargetRevision("));
    AssertContains(
        revisionCapture,
        "expectedRevision<=0",
        "Cooking may capture a non-positive Yuuma revision.");
    AssertContains(
        revisionCapture,
        "expectedRevision!=currentRevision",
        "Cooking may capture a stale Yuuma revision.");
    AssertContains(
        revisionCapture,
        "revision=expectedRevision",
        "The exact validated revision is not returned to job registration.");

    var revisionUpdate = Normalize(ExtractMethod(
        contextSource,
        "private static void UpdateYuumaFoodTarget("));
    AssertContains(
        revisionUpdate,
        "!string.Equals(_yuumaFoodTargetIdentity,identity,StringComparison.Ordinal)",
        "Blood Pond Hell target identity transitions no longer use exact Ordinal comparison.");
    AssertContains(
        revisionUpdate,
        "_yuumaFoodTargetRevision++;_yuumaFoodTargetIdentity=identity",
        "A complete A -> B or B -> A transition no longer advances revision before storing identity.");
    AssertTrue(
        requestValidation.Contains("request.SpecialTargetRevision!=activeYuumaRevision", StringComparison.Ordinal)
        && currentTargetValidation.Contains("target.SpecialFoodTargetRevision!=currentRevision", StringComparison.Ordinal),
        "An old A revision can survive A -> B -> A when the policy identity returns to A.");
}

static void VerifyYuumaSettlementContract(
    string serviceSource,
    string cookingSource,
    string deliverySource,
    string directDeliverySource,
    string policySource,
    string contextSource,
    string specialCaptureSource,
    string normalCaptureSource,
    string matchingSource,
    string settlementSource,
    string settlementTrackerSource)
{
    VerifyYuumaLookupPurposeAndDeliveredItemIsolation(
        serviceSource,
        cookingSource,
        directDeliverySource,
        settlementSource);

    var validation = Normalize(ExtractMethod(
        policySource,
        "private static bool IsValidYuumaFoodTargetPolicy("));
    AssertContains(
        validation,
        "policy.MatchMode!=SpecialFoodTargetMatchMode.All",
        "Blood Pond Hell no longer requires the All Tag policy.");
    AssertContains(
        validation,
        "policy.FoodTags.Count!=2",
        "Blood Pond Hell no longer requires exactly two complete target Tags.");

    var requestIdentity = Normalize(ExtractMethod(
        policySource,
        "private static bool IsYuumaBossRequest("));
    AssertContains(
        requestIdentity,
        "returnstring.Equals(request.SpecialBusinessRole,SpecialBusinessOrderRoles.YuumaBoss,StringComparison.Ordinal);",
        "Yuuma request identity no longer requires the exact Ordinal boss role.");
    AssertDoesNotContain(
        requestIdentity,
        "IgnoreCase",
        "Yuuma request identity accepts a case-insensitive alias.");
    var targetIdentity = Normalize(ExtractMethod(
        policySource,
        "private static bool IsYuumaBossTarget("));
    AssertContains(
        targetIdentity,
        "returnstring.Equals(target.SpecialBusinessRole,SpecialBusinessOrderRoles.YuumaBoss,StringComparison.Ordinal);",
        "Yuuma cooking-target identity no longer requires the exact Ordinal boss role.");
    AssertDoesNotContain(
        targetIdentity,
        "IgnoreCase",
        "Yuuma cooking-target identity accepts a case-insensitive alias.");
    var deliveryState = Normalize(ExtractMethod(
        serviceSource,
        "private static bool TryReadYuumaOrderDeliveryState("));
    foreach (var exactDeliveryField in new[]
             {
                 "TryReadOrderServedItem(order,RuntimeDeliveryItemKind.Food",
                 "TryReadOrderInAirItem(order,RuntimeDeliveryItemKind.Food",
                 "TryReadOrderServedItem(order,RuntimeDeliveryItemKind.Beverage",
                 "TryReadOrderInAirItem(order,RuntimeDeliveryItemKind.Beverage",
                 "returnfoodRead&&foodInAirRead&&beverageRead&&beverageInAirRead",
             })
    {
        AssertContains(
            deliveryState,
            exactDeliveryField,
            $"The exact Yuuma delivery-state read is missing '{exactDeliveryField}'.");
    }
    var targetRevision = Normalize(ExtractMethod(
        policySource,
        "private static bool TryCaptureYuumaFoodTargetRevision("));
    AssertContains(
        targetRevision,
        "RuntimeSpecialBusinessContextService.TryGetActiveYuumaFoodTargetState(",
        "Yuuma cooking jobs no longer capture policy and revision atomically.");
    AssertContains(
        targetRevision,
        "expectedPolicy.HasSameIdentity(currentPolicy)",
        "Yuuma target revision capture no longer proves the current policy identity before cooking.");
    AssertContains(
        Normalize(contextSource),
        "if(complete&&!string.Equals(_yuumaFoodTargetIdentity,identity,StringComparison.Ordinal)){_yuumaFoodTargetRevision++;",
        "Yuuma target revision no longer advances only when the complete normalized identity changes.");

    var requiresPolicy = Normalize(ExtractMethod(
        policySource,
        "private static bool RequestRequiresActiveSpecialFoodTargetPolicy("));
    AssertContains(
        requiresPolicy,
        "if(IsYuumaBossRequest(request))returntrue;",
        "A Yuuma boss request without any policy fields no longer fails closed.");
    AssertContains(
        requiresPolicy,
        "request.SpecialBusinessRole==SpecialBusinessOrderRoles.WackyKoishiBoss&&RuntimeSpecialBusinessContextService.IsActiveWackyPhase(\"Phase3\")",
        "Wacky phase-three Koishi full-feed unexpectedly requires a rotating food-target policy.");
    var phaseThreeBypass = requiresPolicy.IndexOf(
        "request.SpecialBusinessRole==SpecialBusinessOrderRoles.WackyKoishiBoss&&RuntimeSpecialBusinessContextService.IsActiveWackyPhase(\"Phase3\")",
        StringComparison.Ordinal);
    var wackyRoleRequirement = requiresPolicy.IndexOf(
        "returnrequest.SpecialBusinessRoleisSpecialBusinessOrderRoles.WackyGhost",
        StringComparison.Ordinal);
    AssertTrue(
        phaseThreeBypass >= 0
        && wackyRoleRequirement >= 0
        && phaseThreeBypass < wackyRoleRequirement,
        "The Wacky phase-three Koishi bypass must run before the generic Wacky role requirement.");

    var requestValidation = Normalize(ExtractMethod(
        policySource,
        "private static bool TryValidateRequestedSpecialFoodTargetPolicy("));
    AssertContains(
        requestValidation,
        "policy.HasSameIdentity(activeYuumaPolicy)",
        "A self-consistent but stale request policy can bypass the current runtime policy.");
    AssertContains(
        requestValidation,
        "IsSpecialFoodTargetRoleAllowed(request.SpecialBusinessRole,policy,outvarroleError)",
        "A valid active special-food target policy can be borrowed by an unrelated order role.");

    var roleValidation = Normalize(ExtractMethod(
        policySource,
        "private static bool IsSpecialFoodTargetRoleAllowed("));
    AssertContains(
        roleValidation,
        "string.Equals(specialBusinessRole,SpecialBusinessOrderRoles.YuumaBoss,StringComparison.Ordinal)",
        "Blood Pond Hell no longer binds its target policy to the exact Yuuma boss role.");
    AssertContains(
        roleValidation,
        "SpecialBusinessOrderRoles.WackyGhostorSpecialBusinessOrderRoles.WackyKoishiBossorSpecialBusinessOrderRoles.WackyTarget",
        "Wacky Cooking target policy no longer remains isolated to its three explicit roles.");

    var cookedTagValidation = Normalize(ExtractMethod(
        directDeliverySource,
        "private static SpecialFoodTargetTagValidation ValidateSpecialFoodTargetTags("));
    AssertContains(
        cookedTagValidation,
        "policy.Matches(actualTags)",
        "The actual cooked food no longer uses the policy's Any/All semantics.");

    var targetChangeValidation = Normalize(ExtractMethod(
        directDeliverySource,
        "private static bool TryDetectSpecialFoodTargetPolicyChanged("));
    AssertContains(
        targetChangeValidation,
        "!expectedPolicy.HasSameIdentity(currentPolicy)",
        "A changed target owner, generation, mode, signature, or Tag set no longer invalidates the cooking job.");

    var normalizedCooking = Normalize(cookingSource);
    var normalizedService = Normalize(serviceSource);
    var cookingJobDefinition = Normalize(ExtractMethod(
        serviceSource,
        "private sealed class AutomationCookingJob"));
    AssertContains(
        cookingJobDefinition,
        "publicRuntimeCookerReservationCookerReservation{get;init;}",
        "A Blood Pond Hell cooking job no longer retains its exact managed cooker reservation.");
    AssertDoesNotContain(
        cookingJobDefinition,
        "objectCookController",
        "A Blood Pond Hell cooking job retained a long-lived IL2CPP cooker wrapper.");
    AssertContains(
        normalizedService,
        "publicboolAutoCompleteOrder{get;set;}",
        "The cooking job does not retain the caller's explicit auto-complete intent.");
    AssertContains(
        normalizedCooking,
        "AutoCompleteOrder=autoCompleteOrder",
        "A newly registered cooking job does not latch auto-complete intent.");

    var automaticDeliveryPolicy = Normalize(ExtractMethod(
        cookingSource,
        "private static bool WillAutomaticallyDeliverCookingTarget("));
    AssertContains(
        automaticDeliveryPolicy,
        "autoDeliverFood",
        "Automatic food delivery no longer depends on the explicit delivery switch.");

    var sameCookingTarget = Normalize(ExtractMethod(
        cookingSource,
        "private static bool IsSameCookingCollectionTarget("));
    AssertContains(
        sameCookingTarget,
        "!string.Equals(left.SpecialBusinessRole,right.SpecialBusinessRole,StringComparison.Ordinal)",
        "Cooking-job reuse can cross special-business roles.");
    AssertContains(
        sameCookingTarget,
        "!leftSpecialPolicy.HasSameIdentity(rightSpecialPolicy)",
        "Cooking-job reuse can cross a rotating special-food target identity.");
    var sameCookingOrder = Normalize(ExtractMethod(
        cookingSource,
        "private static bool IsSameCookingOrderIdentity("));
    AssertContains(
        sameCookingOrder,
        "RareOrderIdentityMatcher.Matches(",
        "Rare Blood Pond Hell jobs no longer require the exact raw order identity.");
    AssertContains(
        sameCookingOrder,
        "string.Equals(left.OrderKey,right.OrderKey,StringComparison.Ordinal)",
        "Normal Blood Pond Hell jobs no longer require the exact order key.");

    VerifyYuumaManualOrderCaptureContract(specialCaptureSource, normalCaptureSource, matchingSource);

    var directDelivery = Normalize(ExtractMethod(
        directDeliverySource,
        "private static (bool Remove, string Message, string Code) TryDeliverAutomationCookedFood("));
    AssertContains(
        directDelivery,
        "TryFinalizeYuumaCookingJob(job,cookedFood)",
        "Validated Blood Pond Hell food does not enter the dedicated settlement transaction.");
    foreach (var required in new[]
             {
                 "job.YuumaSettlementTracker.Stage!=SpecialBusiness.YuumaSettlementTransactionStage.Ready",
                 "if(job.AutoDeliverFood&&job.AutoCompleteOrder)",
                 "returnEnterManualHandoff(job,DateTime.UtcNow);",
             })
    {
        AssertContains(
            directDelivery,
            required,
            $"The Ready/resume Yuuma settlement gate is missing '{required}'.");
    }

    var finalization = Normalize(ExtractNamedMethod(
        settlementSource,
        "TryFinalizeYuumaCookingJob"));
    AssertDoesNotContain(
        finalization,
        "ShouldPlayerThrowDeliver",
        "The player ThrowDeliver buff capability must not block the dedicated headless food settlement.");
    AssertDoesNotContain(
        settlementSource,
        "TryReadShouldPlayerThrowDeliver",
        "The removed ThrowDeliver capability reader was restored to the Yuuma settlement service.");
    var settlementOrderValidationSource = Normalize(ExtractNamedMethod(
        settlementSource,
        "TryValidateYuumaSettlementOrder"));
    foreach (var inAirSettlementGate in new[]
             {
                 "outvarfoodInAir",
                 "outvarservedBeverage",
                 "outvarbeverageInAir",
                 "if(servedFood!=null||foodInAir!=null)",
                 "if(servedBeverage==null)",
                 "if(beverageInAir!=null)",
             })
    {
        AssertContains(
            settlementOrderValidationSource,
            inAirSettlementGate,
            $"Final-food settlement is missing exact in-air gate '{inAirSettlementGate}'.");
    }
    var beverageInAirSettlementGate = settlementOrderValidationSource.IndexOf(
        "if(beverageInAir!=null)",
        StringComparison.Ordinal);
    var missingBeverageSettlementGate = settlementOrderValidationSource.IndexOf(
        "if(servedBeverage==null)",
        StringComparison.Ordinal);
    AssertTrue(
        beverageInAirSettlementGate >= 0
        && missingBeverageSettlementGate > beverageInAirSettlementGate,
        "Final-food settlement does not prioritize the exact BeverageInAir gate over the generic missing-beverage state.");
    AssertContains(
        settlementOrderValidationSource[beverageInAirSettlementGate..missingBeverageSettlementGate],
        "returnfalse",
        "Final-food settlement can continue after observing a native BeverageInAir.");
    var yuumaIdentityGate = finalization.IndexOf("!IsYuumaBossTarget(job.Target)", StringComparison.Ordinal);
    var deliverySwitchGate = finalization.IndexOf("!job.AutoDeliverFood", StringComparison.Ordinal);
    var completionSwitchGate = finalization.IndexOf("!job.AutoCompleteOrder", StringComparison.Ordinal);
    var settlementOrderValidation = finalization.IndexOf(
        "TryValidateYuumaSettlementOrder(",
        StringComparison.Ordinal);
    var settlementPreflight = finalization.IndexOf("TryPreflightYuumaSettlement(", StringComparison.Ordinal);
    var freshCookerValidation = finalization.IndexOf(
        "TryValidateYuumaCookerBeforeFoodCommit(",
        StringComparison.Ordinal);
    var irreversibleFoodClaim = finalization.IndexOf(
        "TryBeginFoodCommit()",
        StringComparison.Ordinal);
    var deliveryCommit = finalization.IndexOf(
        "finalFoodSetter.Invoke(runtimeOrder.Order,new[]{cookedFood})",
        StringComparison.Ordinal);
    var firstOrderReacquire = finalization.IndexOf(
        "FindYuumaRuntimeOrder(job.Target,request)",
        Math.Max(0, deliveryCommit),
        StringComparison.Ordinal);
    var firstReacquireValidation = finalization.IndexOf(
        "TryValidateReacquiredYuumaSettlementOrder(",
        Math.Max(0, firstOrderReacquire),
        StringComparison.Ordinal);
    var cookerReset = finalization.IndexOf(
        "TryResetCookControllerAfterCommittedSideEffect(job,cookedFood,outvarresetDiagnostic)",
        Math.Max(0, firstReacquireValidation),
        StringComparison.Ordinal);
    var cookerExtraction = finalization.IndexOf(
        "TryCompleteYuumaCookerExtraction(",
        Math.Max(0, cookerReset),
        StringComparison.Ordinal);
    var cookerCleanup = finalization.IndexOf("MarkCleanupCommitted(", StringComparison.Ordinal);
    var secondOrderReacquire = finalization.IndexOf(
        "FindYuumaRuntimeOrder(job.Target,request)",
        Math.Max(0, firstOrderReacquire + 1),
        StringComparison.Ordinal);
    var secondReacquireValidation = finalization.IndexOf(
        "TryValidateReacquiredYuumaSettlementOrder(",
        Math.Max(0, firstReacquireValidation + 1),
        StringComparison.Ordinal);
    var evaluation = finalization.IndexOf("TryInvokeYuumaEvaluation(", StringComparison.Ordinal);
    var bookkeeping = finalization.IndexOf("TryApplyYuumaDeliveryBookkeeping(", StringComparison.Ordinal);
    AssertTrue(
        new[] { yuumaIdentityGate, deliverySwitchGate, completionSwitchGate }
            .All(index => index >= 0 && index < deliveryCommit)
        && settlementOrderValidation >= 0
        && settlementOrderValidation < settlementPreflight
        && settlementPreflight >= 0
        && freshCookerValidation > settlementPreflight
        && irreversibleFoodClaim > freshCookerValidation
        && deliveryCommit > irreversibleFoodClaim
        && firstOrderReacquire > deliveryCommit
        && firstReacquireValidation > firstOrderReacquire
        && cookerReset > firstReacquireValidation
        && cookerExtraction > cookerReset
        && cookerCleanup > cookerExtraction
        && secondOrderReacquire > cookerCleanup
        && secondReacquireValidation > secondOrderReacquire
        && evaluation > secondReacquireValidation
        && bookkeeping > evaluation,
        "Blood Pond Hell finalization must fresh-bind the cooker before the irreversible claim, commit the final setter, revalidate, reset the cooker, run exact extraction callbacks, reacquire and fully revalidate a second time, then evaluate and publish bookkeeping.");
    AssertDoesNotContain(
        finalization[freshCookerValidation..irreversibleFoodClaim],
        "MarkUncertain",
        "A side-effect-free cooker invalidation was quarantined as an uncertain native food commit.");
    AssertDoesNotContain(
        finalization,
        "TryCommitRuntimeDelivery(",
        "Blood Pond Hell finalization restored the generic in-air/table-visual delivery path instead of the exact native final setter.");
    var evaluationReturned = finalization.IndexOf(
        "if(!job.YuumaSettlementTracker.MarkEvaluationCommitted())",
        evaluation,
        StringComparison.Ordinal);
    AssertTrue(
        evaluationReturned > evaluation,
        "The post-evaluation source boundary is missing.");
    var postEvaluationSettlement = finalization[evaluationReturned..];
    foreach (var forbiddenPostEvaluationRead in new[]
             {
                 "FindYuumaRuntimeOrder",
                 "TryRead",
                 "ReadSellableId",
                 "CompareObjectIdentity",
                 "runtimeOrder.",
                 "committedOrder.",
                 "cookedFood",
             })
    {
        AssertDoesNotContain(
            postEvaluationSettlement,
            forbiddenPostEvaluationRead,
            $"Settlement touches a runtime wrapper after evaluation through '{forbiddenPostEvaluationRead}'.");
    }
    AssertContains(
        postEvaluationSettlement,
        "TryApplyYuumaDeliveryBookkeeping(bookkeepingContext,outvarbookkeepingDiagnostic)",
        "Post-evaluation bookkeeping does not consume only the context cached before evaluation.");

    var evaluationHelper = Normalize(ExtractNamedMethod(
        settlementSource,
        "TryInvokeYuumaEvaluation"));
    var settlementContextCreation = Normalize(ExtractNamedMethod(
        settlementSource,
        "TryCreateYuumaSettlementContext"));
    foreach (var routeToken in new[]
             {
                 "runtimeOrder.ManualOrder",
                 "YuumaOrderEvaluationRoute.ManualControlled",
                 "YuumaOrderEvaluationRoute.Standard",
             })
    {
        AssertContains(
            settlementContextCreation,
            routeToken,
            $"The settlement context's exact evaluation route is missing '{routeToken}'.");
    }
    AssertContains(
        evaluationHelper,
        "YuumaOrderEvaluationRoute.ManualControlled",
        "The finalizer does not explicitly recognize a manual-controlled order.");
    AssertContains(
        evaluationHelper,
        "runtimeOrder.ManualEvaluationCallback==null",
        "A manual-controlled order can proceed without its exact captured callback.");
    AssertContains(
        evaluationHelper,
        "YuumaOrderEvaluationRoute.Standard",
        "The finalizer does not explicitly recognize a standard order.");
    foreach (var invocation in new[]
             {
                 "manualMethod.Invoke(runtimeOrder.Manager,new[]{runtimeOrder.Controller,runtimeOrder.ManualEvaluationCallback})",
                 "standardMethod.Invoke(runtimeOrder.Manager,newobject?[]{runtimeOrder.Controller,false,null})",
             })
    {
        AssertContains(
            evaluationHelper,
            invocation,
            $"The exact Yuuma evaluation invocation is missing '{invocation}'.");
    }
    var evaluationResolver = Normalize(ExtractNamedMethod(
        settlementSource,
        "TryResolveYuumaEvaluationMethod"));
    foreach (var exactShape in new[]
             {
                 "\"EvaulateManualOrder\"",
                 "\"EvaluateOrder\"",
                 "parameters[0].ParameterType.FullName,YuumaGuestGroupControllerTypeName,StringComparison.Ordinal",
                 "parameters[0].ParameterType.IsInstanceOfType(runtimeOrder.Controller)",
                 "parameters.Length==2",
                 "parameters[1].ParameterType==context.ManualEvaluationCallback.GetType()",
                 "IsExactYuumaManualEvaluationCallbackType(parameters[1].ParameterType)",
                 "parameters.Length==3",
                 "parameters[1].ParameterType==typeof(bool)",
                 "parameters[2].ParameterType==typeof(Il2CppSystem.Action)",
             })
    {
        AssertContains(
            evaluationResolver,
            exactShape,
            $"The exact Yuuma evaluation method resolver is missing '{exactShape}'.");
    }
    var manualCallbackType = Normalize(ExtractNamedMethod(
        settlementSource,
        "IsExactYuumaManualEvaluationCallbackType"));
    foreach (var exactCallbackShape in new[]
             {
                 "type.IsGenericType",
                 "type.GetGenericTypeDefinition().FullName",
                 "Il2CppActionGenericTypeName",
                 "arguments.Length==1",
                 "arguments[0].FullName",
                 "YuumaEvaluationResultTypeName",
             })
    {
        AssertContains(
            manualCallbackType,
            exactCallbackShape,
            $"The manual evaluation callback type check is missing closed-generic identity '{exactCallbackShape}'.");
    }
    AssertDoesNotContain(
        evaluationHelper,
        "ManualEvaluationCallback??",
        "A missing manual callback can fall back to standard evaluation.");
    foreach (var forbiddenInference in new[] { "IsManualControlledOrder", "CaptureSource", "ToString(" })
    {
        AssertDoesNotContain(
            evaluationHelper,
            forbiddenInference,
            $"Yuuma evaluation re-infers its exact route through '{forbiddenInference}'.");
    }
    var fulfilledRead = evaluationHelper.IndexOf("get_IsFullfilled", StringComparison.Ordinal);
    var nativeManualEvaluation = evaluationHelper.IndexOf("manualMethod.Invoke", StringComparison.Ordinal);
    var nativeStandardEvaluation = evaluationHelper.IndexOf("standardMethod.Invoke", StringComparison.Ordinal);
    AssertTrue(
        fulfilledRead >= 0
        && nativeManualEvaluation > fulfilledRead
        && nativeStandardEvaluation > fulfilledRead,
        "Yuuma evaluation can run before the freshly delivered order is confirmed fulfilled.");
    var reacquireValidation = Normalize(ExtractNamedMethod(
        settlementSource,
        "TryValidateReacquiredYuumaSettlementOrder"));
    foreach (var strictReacquireCheck in new[]
             {
                 "orderPointer!=context.OrderPointer",
                 "controllerPointer!=context.ControllerPointer",
                 "currentRoute!=context.EvaluationRoute",
                 "ReferenceEquals(runtimeOrder.ManualEvaluationCallback,context.ManualEvaluationCallback)",
                 "TryReadYuumaOrderDeliveryState(runtimeOrder.Order,outvarservedFood,outvarfoodInAir,outvarservedBeverage,outvarbeverageInAir",
                 "foodInAir!=null||beverageInAir!=null",
                 "CompareObjectIdentity(servedFood,cookedFood)!=RuntimeObjectIdentityComparison.Same",
                 "servedBeverage==null",
                 "TryValidateYuumaDeliveredItemAgainstOriginalOrder(job.Target,servedBeverage,RuntimeDeliveryItemKind.Beverage",
                 "YuumaChallengeOrderIdentity.Read(runtimeOrder.Order,runtimeOrder.Controller)",
                 "identity.OrderGuestId!=SpecialBusinessGuestIds.YuumaBoss",
                 "identity.ControllerGuestId!=SpecialBusinessGuestIds.YuumaBoss",
                 "IsNightBusinessGenerationActive(context.BusinessGeneration)",
             })
    {
        AssertContains(
            reacquireValidation,
            strictReacquireCheck,
            $"Yuuma order reacquisition is missing strict post-callback check '{strictReacquireCheck}'.");
    }

    var runtimeOrderLookup = Normalize(ExtractNamedMethod(
        settlementSource,
        "FindYuumaRuntimeOrder"));
    var generationGate = runtimeOrderLookup.IndexOf(
        "policy==null||!IsNightBusinessGenerationActive(policy.BusinessGeneration)",
        StringComparison.Ordinal);
    var normalOrderScan = runtimeOrderLookup.IndexOf(
        "FindRuntimeNormalOrder(request,RuntimeOrderLookupPurpose.YuumaSettlement)",
        StringComparison.Ordinal);
    var specialOrderScan = runtimeOrderLookup.IndexOf(
        "FindRuntimeOrder(request,RuntimeOrderLookupPurpose.YuumaSettlement)",
        StringComparison.Ordinal);
    AssertTrue(
        generationGate >= 0
        && normalOrderScan > generationGate
        && specialOrderScan > generationGate,
        "Yuuma runtime-order lookup must reject an inactive target generation before scanning either order shape.");
    AssertContains(
        runtimeOrderLookup,
        "returntarget.Kind==CookingCollectionTargetKind.NormalOrder?FindRuntimeNormalOrder(request,RuntimeOrderLookupPurpose.YuumaSettlement):FindRuntimeOrder(request,RuntimeOrderLookupPurpose.YuumaSettlement)",
        "Rare and normal Yuuma orders no longer share the dedicated settlement lookup before the beverage transaction.");

    var finalSetterResolver = Normalize(ExtractNamedMethod(
        settlementSource,
        "TryResolveYuumaFinalSetter"));
    AssertContains(
        finalSetterResolver,
        "parameters[0].ParameterType.FullName,YuumaSellableTypeName,StringComparison.Ordinal",
        "Yuuma final setters must require the exact Sellable FullName.");
    AssertContains(
        finalSetterResolver,
        "parameters[0].ParameterType.IsInstanceOfType(deliveredItem)",
        "Yuuma final setters must also accept the exact delivered wrapper instance.");

    var extractionPreflight = Normalize(ExtractNamedMethod(
        settlementSource,
        "TryCreateYuumaCookerExtractionContext"));
    foreach (var exactExtractionShape in new[]
             {
                 "\"OnCookerAvailabilityUpdate\"",
                 "method.ReturnType==typeof(void)",
                 "parameters.Length==1",
                 "parameters[0].ParameterType==typeof(int)",
                 "\"AfterPlayerExtract\"",
                 "method.GetParameters().Length==0",
                 "availabilityMethods.Length!=1||extractionMethods.Length!=1",
             })
    {
        AssertContains(
            extractionPreflight,
            exactExtractionShape,
            $"Yuuma cooker extraction preflight is missing exact BepInEx 783 shape '{exactExtractionShape}'.");
    }
    AssertDoesNotContain(
        extractionPreflight,
        "TryInvokeInstance",
        "Yuuma cooker extraction restored a broad reflective invocation fallback.");
    AssertDoesNotContain(
        Normalize(ExtractDeclaration(
            settlementSource,
            "private sealed record YuumaCookerExtractionContext(")),
        "objectCookController",
        "Yuuma extraction context retained a cooker wrapper across native callbacks.");

    var preCommitCookerValidation = Normalize(ExtractNamedMethod(
        settlementSource,
        "TryValidateYuumaCookerBeforeFoodCommit"));
    var freshPreCommitBinding = preCommitCookerValidation.IndexOf(
        "TryReacquireAutomationCooker(",
        StringComparison.Ordinal);
    var freshPreCommitResult = preCommitCookerValidation.IndexOf(
        ".State.Result",
        StringComparison.Ordinal);
    var freshPreCommitIdentity = preCommitCookerValidation.IndexOf(
        "IsSameObject(",
        StringComparison.Ordinal);
    AssertTrue(
        freshPreCommitBinding >= 0
        && freshPreCommitResult > freshPreCommitBinding
        && freshPreCommitIdentity > freshPreCommitResult,
        "Yuuma final food commit does not compare cookedFood with a fresh exact cooker result.");
    AssertContains(
        preCommitCookerValidation,
        "current.State.Result,cookedFood",
        "Yuuma final food commit does not compare the fresh result with the requested cooked food.");
    AssertDoesNotContain(
        preCommitCookerValidation,
        "job.CookController",
        "Yuuma final food validation reads a retained cooker wrapper.");

    var extractionCompletion = Normalize(ExtractNamedMethod(
        settlementSource,
        "TryCompleteYuumaCookerExtraction"));
    var firstExtractionBinding = extractionCompletion.IndexOf(
        "TryReacquireAutomationCooker(",
        StringComparison.Ordinal);
    var availabilityCallback = extractionCompletion.IndexOf(
        "context.AvailabilityMethod.Invoke(",
        StringComparison.Ordinal);
    var secondExtractionBinding = extractionCompletion.IndexOf(
        "TryReacquireAutomationCooker(",
        Math.Max(0, firstExtractionBinding + 1),
        StringComparison.Ordinal);
    var afterPlayerExtractCallback = extractionCompletion.IndexOf(
        "context.ExtractionMethod.Invoke(",
        StringComparison.Ordinal);
    var thirdExtractionBinding = extractionCompletion.IndexOf(
        "TryReacquireAutomationCooker(",
        Math.Max(0, secondExtractionBinding + 1),
        StringComparison.Ordinal);
    AssertTrue(
        firstExtractionBinding >= 0
        && availabilityCallback > firstExtractionBinding
        && secondExtractionBinding > availabilityCallback
        && afterPlayerExtractCallback > secondExtractionBinding,
        "Yuuma cooker cleanup must fresh-bind before availability(-1), then bind again before AfterPlayerExtract.");
    AssertTrue(
        thirdExtractionBinding < 0,
        "Yuuma extraction must not reject a legal PureHellFryer SetCook performed inside AfterPlayerExtract.");
    AssertDoesNotContain(
        extractionCompletion[afterPlayerExtractCallback..],
        "RuntimeCookingContentMutation.Extract",
        "Yuuma extraction incorrectly requires the old Extract receipt after a legal cooker takeover.");
    AssertContains(
        Normalize(ExtractNamedMethod(cookingSource, "TryReacquireAutomationCooker")),
        "ownershipAfter.MutationCompleted",
        "Yuuma extraction's fresh binding does not require the matching postfix receipt.");
    AssertDoesNotContain(
        extractionCompletion,
        "context.CookController",
        "Yuuma extraction invokes a cooker wrapper cached before a native callback.");
    AssertDoesNotContain(
        extractionCompletion,
        "TryInvokeInstance",
        "Yuuma cooker extraction completion restored a broad reflective invocation fallback.");

    var beverageDelivery = Normalize(ExtractNamedMethod(
        settlementSource,
        "TryDeliverYuumaOrderBeverage"));
    AssertDoesNotContain(
        beverageDelivery,
        "ShouldPlayerThrowDeliver",
        "The player ThrowDeliver buff capability must not block the dedicated headless beverage transaction.");
    AssertContains(
        beverageDelivery,
        "TryDeliverYuumaOrderBeverage(CookingCollectionTargettarget,intbeverageId,stringbeverageName,stringorderLabel)",
        "The Yuuma beverage entry must accept only its stable target identity and item inputs.");
    AssertDoesNotContain(
        beverageDelivery,
        "TryDeliverYuumaOrderBeverage(RuntimeOrderMatch",
        "The Yuuma beverage entry must not trust a generic runtime-order wrapper supplied by its caller.");
    var beverageRequestBuild = beverageDelivery.IndexOf(
        "BuildOrderRequestFromCookingTarget(target)",
        StringComparison.Ordinal);
    var beverageInitialLookup = beverageDelivery.IndexOf(
        "FindYuumaRuntimeOrder(target,request)",
        Math.Max(0, beverageRequestBuild),
        StringComparison.Ordinal);
    var beverageInitialStateRead = beverageDelivery.IndexOf(
        "TryReadYuumaOrderDeliveryState(",
        Math.Max(0, beverageInitialLookup),
        StringComparison.Ordinal);
    var beverageInAirGate = beverageDelivery.IndexOf(
        "if(beverageInAir!=null)",
        Math.Max(0, beverageInitialStateRead),
        StringComparison.Ordinal);
    var foodInAirGate = beverageDelivery.IndexOf(
        "if(servedFood!=null||foodInAir!=null)",
        Math.Max(0, beverageInAirGate),
        StringComparison.Ordinal);
    var beverageQuantityRead = beverageDelivery.IndexOf(
        "GetBeverageQuantity(beverageId)",
        Math.Max(0, foodInAirGate),
        StringComparison.Ordinal);
    var beverageStoragePreflight = beverageDelivery.IndexOf(
        "TryCreateYuumaBeverageStorageContext(",
        StringComparison.Ordinal);
    var beverageOut = beverageDelivery.IndexOf(
        "storageContext.BeverageOutMethod.Invoke(null,newobject?[]{beverageId,false})",
        StringComparison.Ordinal);
    var deductedTargetValidation = beverageDelivery.IndexOf(
        "TryValidateCurrentYuumaTarget(target,outvardeductedTargetDiagnostic)",
        Math.Max(0, beverageOut),
        StringComparison.Ordinal);
    var deductedOrderLookup = beverageDelivery.IndexOf(
        "vardeductedOrder=FindYuumaRuntimeOrder(target,request)",
        Math.Max(0, deductedTargetValidation),
        StringComparison.Ordinal);
    var deductedOrderValidation = beverageDelivery.IndexOf(
        "TryValidateReacquiredYuumaBeverageOrder(",
        Math.Max(0, deductedOrderLookup),
        StringComparison.Ordinal);
    var freshSetterResolution = beverageDelivery.IndexOf(
        "TryResolveYuumaFinalSetter(",
        Math.Max(0, deductedOrderValidation),
        StringComparison.Ordinal);
    var beverageSetter = beverageDelivery.IndexOf(
        "freshFinalBeverageSetter.Invoke(deductedOrder.Order,new[]{sellable})",
        Math.Max(0, freshSetterResolution),
        StringComparison.Ordinal);
    var committedTargetValidation = beverageDelivery.IndexOf(
        "TryValidateCurrentYuumaTarget(target,outvarcommittedTargetDiagnostic)",
        Math.Max(0, beverageSetter),
        StringComparison.Ordinal);
    var committedOrderLookup = beverageDelivery.IndexOf(
        "varcommittedOrder=FindYuumaRuntimeOrder(target,request)",
        Math.Max(0, committedTargetValidation),
        StringComparison.Ordinal);
    var committedOrderValidation = beverageDelivery.IndexOf(
        "TryValidateReacquiredYuumaBeverageOrder(",
        Math.Max(0, committedOrderLookup),
        StringComparison.Ordinal);
    var beveragePatientRecovery = beverageDelivery.IndexOf(
        "TryRecoverPatientAfterPartialDelivery(",
        Math.Max(0, committedOrderValidation),
        StringComparison.Ordinal);
    var recoveredTargetValidation = beverageDelivery.IndexOf(
        "TryValidateCurrentYuumaTarget(target,outvarrecoveredTargetDiagnostic)",
        Math.Max(0, beveragePatientRecovery),
        StringComparison.Ordinal);
    var recoveredOrderLookup = beverageDelivery.IndexOf(
        "varrecoveredOrder=FindYuumaRuntimeOrder(target,request)",
        Math.Max(0, recoveredTargetValidation),
        StringComparison.Ordinal);
    var recoveredOrderValidation = beverageDelivery.IndexOf(
        "TryValidateReacquiredYuumaBeverageOrder(",
        Math.Max(0, recoveredOrderLookup),
        StringComparison.Ordinal);
    var beverageRangeAdjustment = beverageDelivery.IndexOf(
        "ApplyYuumaBeverageCostPolicy(storageContext,beverageId,isFreeBeverage,extraCostBeverages)",
        Math.Max(0, recoveredOrderValidation),
        StringComparison.Ordinal);
    var adjustedTargetValidation = beverageDelivery.IndexOf(
        "TryValidateCurrentYuumaTarget(target,outvaradjustedTargetDiagnostic)",
        Math.Max(0, beverageRangeAdjustment),
        StringComparison.Ordinal);
    var adjustedOrderLookup = beverageDelivery.IndexOf(
        "varadjustedOrder=FindYuumaRuntimeOrder(target,request)",
        Math.Max(0, adjustedTargetValidation),
        StringComparison.Ordinal);
    var adjustedOrderValidation = beverageDelivery.IndexOf(
        "TryValidateReacquiredYuumaBeverageOrder(",
        Math.Max(0, adjustedOrderLookup),
        StringComparison.Ordinal);
    var freshBookkeepingContext = beverageDelivery.IndexOf(
        "TryCreateYuumaBookkeepingContext(",
        Math.Max(0, adjustedOrderValidation),
        StringComparison.Ordinal);
    var beverageBookkeeping = beverageDelivery.IndexOf(
        "TryApplyYuumaDeliveryBookkeeping(freshBookkeepingContext,",
        Math.Max(0, freshBookkeepingContext),
        StringComparison.Ordinal);
    AssertTrue(
        beverageRequestBuild >= 0
        && beverageInitialLookup > beverageRequestBuild
        && beverageInitialStateRead > beverageInitialLookup
        && beverageInAirGate > beverageInitialStateRead
        && foodInAirGate > beverageInAirGate
        && beverageQuantityRead > foodInAirGate
        && beverageStoragePreflight > beverageQuantityRead
        && beverageOut > beverageStoragePreflight
        && deductedTargetValidation > beverageOut
        && deductedOrderLookup > deductedTargetValidation
        && deductedOrderValidation > deductedOrderLookup
        && freshSetterResolution > deductedOrderValidation
        && beverageSetter > freshSetterResolution
        && committedTargetValidation > beverageSetter
        && committedOrderLookup > committedTargetValidation
        && committedOrderValidation > committedOrderLookup
        && beveragePatientRecovery > committedOrderValidation
        && recoveredTargetValidation > beveragePatientRecovery
        && recoveredOrderLookup > recoveredTargetValidation
        && recoveredOrderValidation > recoveredOrderLookup
        && beverageRangeAdjustment > recoveredOrderValidation
        && adjustedTargetValidation > beverageRangeAdjustment
        && adjustedOrderLookup > adjustedTargetValidation
        && adjustedOrderValidation > adjustedOrderLookup
        && freshBookkeepingContext > adjustedOrderValidation
        && beverageBookkeeping > freshBookkeepingContext,
        "Each irreversible Yuuma beverage step, including patient recovery, must be followed by target/revision validation and a fresh exact-order lookup before bookkeeping.");
    AssertContains(
        beverageDelivery[beverageInAirGate..foodInAirGate],
        "OrderPreparationStepCodes.CookingPending",
        "A native BeverageInAir no longer stops the shared rare/normal Yuuma beverage entry as retryable CookingPending.");
    AssertContains(
        beverageDelivery[foodInAirGate..beverageQuantityRead],
        "OrderPreparationStepCodes.CookingPending",
        "A native FoodInAir no longer stops the Yuuma beverage transaction before inventory can be consumed.");
    foreach (var forbiddenPreflightSideEffect in new[]
             {
                 "BeverageOutMethod.Invoke",
                 "freshFinalBeverageSetter.Invoke",
                 "ApplyYuumaBeverageCostPolicy",
                 "TryRecoverPatientAfterPartialDelivery",
                 "TryApplyYuumaDeliveryBookkeeping",
             })
    {
        AssertDoesNotContain(
            beverageDelivery[beverageInitialLookup..beverageInAirGate],
            forbiddenPreflightSideEffect,
            $"The shared rare/normal BeverageInAir preflight performs '{forbiddenPreflightSideEffect}' before rejecting the transaction.");
    }
    var afterBeverageOut = beverageDelivery[beverageOut..];
    foreach (var staleRuntimeOrderUse in new[]
             {
                 "runtimeOrder.Order",
                 "runtimeOrder.Controller",
                 "runtimeOrder.Manager",
             })
    {
        AssertDoesNotContain(
            afterBeverageOut,
            staleRuntimeOrderUse,
            $"The preflight runtime-order wrapper crosses BeverageOut through '{staleRuntimeOrderUse}'.");
    }
    AssertContains(
        beverageDelivery[deductedOrderValidation..beverageSetter],
        "expectCommitted:false",
        "The post-BeverageOut lookup does not prove that the fresh order remains uncommitted.");
    AssertContains(
        beverageDelivery[committedOrderValidation..recoveredTargetValidation],
        "expectCommitted:true",
        "The post-setter lookup does not prove the exact beverage commit before patient recovery.");
    AssertContains(
        beverageDelivery[committedOrderValidation..recoveredTargetValidation],
        "committedOrder",
        "Patient recovery is not executed against the latest exact order wrapper.");
    AssertContains(
        beverageDelivery[committedOrderValidation..recoveredTargetValidation],
        "deliveredItemCount:1",
        "Blood Pond Hell beverage recovery no longer uses exactly one delivered item.");
    AssertContains(
        beverageDelivery[recoveredOrderValidation..beverageRangeAdjustment],
        "expectCommitted:true",
        "The post-recovery lookup does not prove the exact committed beverage before range adjustment.");
    AssertContains(
        beverageDelivery[adjustedOrderValidation..beverageBookkeeping],
        "expectCommitted:true",
        "The post-range lookup does not prove the exact committed beverage before bookkeeping.");
    AssertContains(
        beverageDelivery[adjustedOrderValidation..beverageBookkeeping],
        "adjustedOrder",
        "Post-range bookkeeping is not built from the latest exact order wrapper.");
    AssertContains(
        beverageDelivery[adjustedOrderValidation..beverageBookkeeping],
        "freshBookkeepingContext",
        "Post-range bookkeeping no longer uses a fresh cached native context.");
    foreach (var staleCommittedOrderUse in new[]
             {
                 "committedOrder.Order",
                 "committedOrder.Controller",
                 "committedOrder.Manager",
             })
    {
        AssertDoesNotContain(
            beverageDelivery[beveragePatientRecovery..beverageBookkeeping],
            staleCommittedOrderUse,
            $"The pre-recovery order wrapper crosses the patient callback through '{staleCommittedOrderUse}'.");
    }
    foreach (var staleRecoveredOrderUse in new[]
             {
                 "recoveredOrder.Order",
                 "recoveredOrder.Controller",
                 "recoveredOrder.Manager",
             })
    {
        AssertDoesNotContain(
            beverageDelivery[beverageRangeAdjustment..beverageBookkeeping],
            staleRecoveredOrderUse,
            $"The pre-range order wrapper crosses the inventory callback through '{staleRecoveredOrderUse}'.");
    }

    var beverageReacquireValidation = Normalize(ExtractNamedMethod(
        settlementSource,
        "TryValidateReacquiredYuumaBeverageOrder"));
    foreach (var beverageInAirContract in new[]
             {
                 "TryReadYuumaOrderDeliveryState(runtimeOrder.Order",
                 "outvarbeverageInAir",
                 "if(beverageInAir!=null)",
                 "returnfalse",
             })
    {
        AssertContains(
            beverageReacquireValidation,
            beverageInAirContract,
            $"Fresh Yuuma beverage reacquisition is missing BeverageInAir contract '{beverageInAirContract}'.");
    }
    var reacquiredBeverageInAirGate = beverageReacquireValidation.IndexOf(
        "if(beverageInAir!=null)",
        StringComparison.Ordinal);
    var reacquiredFoodGate = beverageReacquireValidation.IndexOf(
        "if(servedFood!=null||foodInAir!=null)",
        StringComparison.Ordinal);
    var reacquiredCommitGate = beverageReacquireValidation.IndexOf(
        "if(!expectCommitted&&servedBeverage!=null)",
        StringComparison.Ordinal);
    AssertTrue(
        reacquiredBeverageInAirGate >= 0
        && reacquiredFoodGate > reacquiredBeverageInAirGate
        && reacquiredCommitGate > reacquiredBeverageInAirGate,
        "The fresh-order BeverageInAir gate must run before food and committed-beverage state validation.");
    AssertEqual(
        4,
        CountOccurrences(beverageDelivery, "TryValidateReacquiredYuumaBeverageOrder("),
        "All four fresh Yuuma beverage reacquisitions must pass the BeverageInAir validator.");
    AssertContains(
        beverageReacquireValidation,
        "if(!expectCommitted&&servedBeverage!=null)",
        "The beverage validator no longer rejects an unexpected commit after BeverageOut.");
    AssertContains(
        beverageReacquireValidation,
        "CompareObjectIdentity(servedBeverage,deliveredBeverage)!=RuntimeObjectIdentityComparison.Same",
        "The beverage validator no longer requires the exact committed item after setter/range operations.");
    var patientRecovery = Normalize(ExtractNamedMethod(
        deliverySource,
        "TryRecoverPatientAfterPartialDelivery"));
    var manualControlledSkip = patientRecovery.IndexOf("IsManualControlledOrder(", StringComparison.Ordinal);
    var patientBoundsRead = patientRecovery.IndexOf("TryReadPatientBounds(", StringComparison.Ordinal);
    var patientMutation = patientRecovery.IndexOf(
        "TryInvokeInstance(",
        Math.Max(0, patientBoundsRead),
        StringComparison.Ordinal);
    AssertTrue(
        manualControlledSkip >= 0
        && patientBoundsRead > manualControlledSkip
        && patientMutation > patientBoundsRead,
        "Manual-controlled orders do not skip the shared patient recovery before patient reads or mutations.");
    AssertContains(
        patientRecovery[manualControlledSkip..patientBoundsRead],
        "message=\"\";returntrue;",
        "The manual-controlled recovery path is no longer an explicit successful no-op.");
    AssertEqual(
        1,
        CountOccurrences(beverageDelivery, "currentQuantity>0"),
        "Infinite beverage stock must still execute the same native inventory sequence; only finite sufficiency may branch on currentQuantity > 0.");
    foreach (var quantityContract in new[]
             {
                 "currentQuantity<0?\"无限库存\"",
                 "currentQuantity-(isFreeBeverage?0:extraCostBeverages)",
             })
    {
        AssertContains(
            beverageDelivery,
            quantityContract,
            $"Yuuma beverage result text is missing exact inventory accounting '{quantityContract}'.");
    }

    var beverageCostPolicy = Normalize(ExtractNamedMethod(
        settlementSource,
        "ApplyYuumaBeverageCostPolicy"));
    AssertContains(
        beverageCostPolicy,
        "if(isFreeBeverage){InvokeExactRuntimeStorageRange(storageContext.BeverageInRangeMethod,beverageId,1);return;}",
        "Free beverage delivery must reverse the base BeverageOut exactly once.");
    AssertContains(
        beverageCostPolicy,
        "varadditionalCost=extraCostBeverages-1;if(additionalCost>0){InvokeExactRuntimeStorageRange(storageContext.BeverageOutRangeMethod,beverageId,additionalCost);}",
        "Extra-cost beverage delivery must apply the remaining native range cost after the base BeverageOut.");

    var beverageStoragePreflightHelper = Normalize(ExtractNamedMethod(
        settlementSource,
        "TryCreateYuumaBeverageStorageContext"));
    foreach (var exactStorageShape in new[]
             {
                 "\"BeverageOut\"",
                 "parameters[0].ParameterType==typeof(int)",
                 "parameters[1].ParameterType==typeof(bool)",
                 "FindYuumaBeverageRangeMethods(storageType,\"BeverageInRange\")",
                 "FindYuumaBeverageRangeMethods(storageType,\"BeverageOutRange\")",
                 "beverageOutMethods.Length!=1",
                 "beverageInRangeMethods.Length!=1",
                 "beverageOutRangeMethods.Length!=1",
             })
    {
        AssertContains(
            beverageStoragePreflightHelper,
            exactStorageShape,
            $"Yuuma beverage storage preflight is missing exact BepInEx 783 shape '{exactStorageShape}'.");
    }
    var beverageRangeResolver = Normalize(ExtractNamedMethod(
        settlementSource,
        "FindYuumaBeverageRangeMethods"));
    AssertContains(
        beverageRangeResolver,
        "parameters[0].ParameterType==typeof(Il2CppSystem.Collections.Generic.IEnumerable<int>)",
        "Yuuma beverage range storage must resolve the exact generic enumerable signature.");
    var beverageRangeInvoker = Normalize(ExtractNamedMethod(
        settlementSource,
        "InvokeExactRuntimeStorageRange"));
    AssertContains(
        beverageRangeInvoker,
        "varids=newIl2CppStructArray<int>(count)",
        "Yuuma beverage range storage must build an exact Il2CppStructArray<int> argument.");
    AssertContains(
        beverageRangeInvoker,
        "ids.Cast<Il2CppSystem.Collections.Generic.IEnumerable<int>>()",
        "Yuuma beverage range storage must cast only to its exact generic interface.");

    var sellableTagReader = Normalize(ExtractNamedMethod(
        settlementSource,
        "TryReadExactSellableTagIds"));
    AssertContains(
        sellableTagReader,
        "if(rawTagsisIl2CppStructArray<int>il2CppTags)",
        "Yuuma item Tags must accept only the BepInEx 783 Il2CppStructArray<int> container.");
    AssertContains(
        sellableTagReader,
        "FindExactInstanceMethod(item.GetType(),\"get_Tags\",0,typeof(Il2CppStructArray<int>))",
        "Yuuma item Tags getter must declare the exact Il2CppStructArray<int> return type before invocation.");
    AssertContains(
        sellableTagReader,
        "get_Tags()返回未验证的容器",
        "Yuuma item Tags do not fail closed on an unverified container shape.");
    foreach (var forbiddenTagFallback in new[]
             {
                 "rawTagsisint[]",
                 "rawTagsisIEnumerable",
                 "EnumerateIl2Cpp",
                 "TryReadIntSequence",
             })
    {
        AssertDoesNotContain(
            sellableTagReader,
            forbiddenTagFallback,
            $"Yuuma item Tags restored unsupported container fallback '{forbiddenTagFallback}'.");
    }

    var normalCookingJob = Normalize(ExtractMethod(
        cookingSource,
        "private static (bool Delivered, string StepName, string Message, string Code)\n        TryProcessNormalOrderCookingJob("));
    var yuumaTerminalReturn = normalCookingJob.IndexOf(
        "if(result.Remove&&IsYuumaBossTarget(job.Target)){return(false,\"普客送达料理\",result.Message,result.Code);}",
        StringComparison.Ordinal);
    var servedFoodFallback = normalCookingJob.IndexOf(
        "ReadOrderServedFood(order)",
        StringComparison.Ordinal);
    AssertTrue(
        yuumaTerminalReturn >= 0 && servedFoodFallback > yuumaTerminalReturn,
        "A terminal normal-order Yuuma job must return before reading the potentially invalidated order wrapper's served food.");

    var bookkeepingPreflight = Normalize(ExtractNamedMethod(
        settlementSource,
        "TryCreateYuumaBookkeepingContext"));
    foreach (var exactBookkeepingShape in new[]
             {
                 "parameters[0].ParameterType==typeof(Il2CppSystem.Collections.Generic.IEnumerable<int>)",
                 "parameters[0].ParameterType.FullName,YuumaOrderBaseTypeName,StringComparison.Ordinal",
                 "parameters[0].ParameterType.IsInstanceOfType(runtimeOrder.Order)",
                 "parameters[1].ParameterType.FullName,YuumaOrderChangeContextTypeName,StringComparison.Ordinal",
                 "parameters[2].ParameterType==typeof(int)",
             })
    {
        AssertContains(
            bookkeepingPreflight,
            exactBookkeepingShape,
            $"Yuuma delivery bookkeeping preflight is missing exact native parameter identity '{exactBookkeepingShape}'.");
    }

    var bookkeepingHelper = Normalize(ExtractNamedMethod(
        settlementSource,
        "TryApplyYuumaDeliveryBookkeeping"));
    AssertContains(
        bookkeepingHelper,
        "TryApplyYuumaDeliveryBookkeeping(YuumaDeliveryBookkeepingContextcontext,outstringdiagnostic)",
        "Post-evaluation bookkeeping accepts live order/item inputs instead of only its cached context.");
    var consumeUpdate = bookkeepingHelper.IndexOf("AddBussinessFoodConsumes", StringComparison.Ordinal);
    var orderStatusUpdate = bookkeepingHelper.IndexOf("OnOrderBaseStatusUpdate", StringComparison.Ordinal);
    var deskUpdate = bookkeepingHelper.IndexOf("TryAddPlayerOccupiedDeskCode", StringComparison.Ordinal);
    var consumeInvoke = bookkeepingHelper.IndexOf("context.ConsumeMethod.Invoke", StringComparison.Ordinal);
    var statusInvoke = bookkeepingHelper.IndexOf("context.StatusMethod.Invoke", StringComparison.Ordinal);
    var deskInvoke = bookkeepingHelper.IndexOf("context.DeskMethod.Invoke", StringComparison.Ordinal);
    AssertTrue(
        consumeUpdate >= 0
        && orderStatusUpdate > consumeUpdate
        && deskUpdate > orderStatusUpdate,
        "Blood Pond Hell delivery bookkeeping no longer mirrors consume -> FoodDelivered -> occupied-desk order.");
    AssertTrue(
        consumeInvoke >= 0
        && statusInvoke > consumeInvoke
        && deskInvoke > statusInvoke,
        "Blood Pond Hell delivery bookkeeping invocation order is not consume -> status -> occupied desk.");
    AssertContains(
        bookkeepingHelper,
        "FoodDelivered",
        "The exact PartnerManager FoodDelivered context is missing.");
    foreach (var forbiddenRead in new[]
             {
                 "FindExact",
                 "RuntimeOrderMatch",
                 "deliveredItem",
                 "GetType(",
                 "ReadMember",
                 "ReadSellable",
                 "get_DeskCode",
                 "TryReadNativeObjectPointer",
                 "FindYuumaRuntimeOrder",
             })
    {
        AssertDoesNotContain(
            bookkeepingHelper,
            forbiddenRead,
            $"Post-evaluation bookkeeping re-reads a runtime wrapper through '{forbiddenRead}'.");
    }

    var normalizedTracker = Normalize(settlementTrackerSource);
    foreach (var requiredState in new[] { "Ready", "Attempting", "Committed", "Uncertain" })
    {
        AssertContains(
            normalizedTracker,
            requiredState,
            $"The Yuuma settlement tracker is missing irreversible state '{requiredState}'.");
    }
    AssertContains(
        normalizedTracker,
        "YuumaSettlementTransactionTracker",
        "The dedicated Yuuma transaction tracker is missing.");
    AssertContains(
        normalizedTracker,
        "TryBegin",
        "The Yuuma transaction tracker has no atomic attempt claim.");
    AssertContains(
        normalizedTracker,
        "MarkUncertain",
        "The Yuuma transaction tracker cannot permanently quarantine an uncertain native call.");
    AssertContains(
        finalization,
        "MarkUncertain",
        "An exception after an irreversible Yuuma side effect is not latched as uncertain.");
    var uncertainSettlement = Normalize(ExtractNamedMethod(
        settlementSource,
        "BlockUncertainYuumaSettlement"));
    AssertContains(
        uncertainSettlement,
        "OrderEvaluationCommitUncertain",
        "An uncertain Yuuma finalization does not install the existing non-replay safety barrier.");
    AssertContains(
        uncertainSettlement,
        "reasonCode:\"yuuma-settlement-uncertain\",terminal:true",
        "An uncertain Yuuma finalization is not published as a terminal ACK-required event.");

    var combinedSettlement = $"{settlementSource}\n{settlementTrackerSource}";
    foreach (var forbidden in new[]
             {
                 "WorkSceneServePannel",
                 "WorkSceneThrowDeliverPanel",
                 "OpenThrowDeliverPanel",
                 "OnThrowDelivering",
                 "ExecuteThrowDeliver",
                 "ThrowDeliver(",
                 "ShowOrder",
                 "ShowManualOrder",
                 "FinishOrderStatus",
                 "InvokeOrderUpdate",
                 "DisplayClass",
                 "MoveNext",
                 "CookController.Extract",
                 "YuumaFinalizationTransactionGate",
                 "YuumaOrderSettlementCoordinator",
                 "YuumaSettlementProgressState",
                 "TryClaimYuumaSettlement",
             })
    {
        AssertDoesNotContain(
            Normalize(combinedSettlement),
            Normalize(forbidden),
            $"Yuuma finalization restored forbidden UI/generated/legacy path '{forbidden}'.");
    }

    AssertFalse(
        $"{deliverySource}\n{directDeliverySource}\n{policySource}".Contains(
            "WackyTargetSignature",
            StringComparison.Ordinal),
        "The removed WackyTargetSignature compatibility contract was restored.");
}

static void VerifyYuumaLookupPurposeAndDeliveredItemIsolation(
    string serviceSource,
    string cookingSource,
    string directDeliverySource,
    string settlementSource)
{
    var rarePrepare = Normalize(ExtractMethod(
        serviceSource,
        "public static OrderPreparationResult Prepare("));
    AssertContains(
        rarePrepare,
        "runtimeOrderCache??=yuumaRequest?FindRuntimeOrder(request,RuntimeOrderLookupPurpose.YuumaSettlement):FindRuntimeOrder(request)",
        "Rare prepare no longer isolates strict Yuuma settlement lookup from ordinary and Yuyuko lookup.");
    var rarePrepareLookup = rarePrepare.IndexOf("RuntimeOrderMatchGetRuntimeOrder()", StringComparison.Ordinal);
    AssertTrue(
        rarePrepareLookup >= 0
        && rarePrepareLookup < rarePrepare.IndexOf("if(request.AutoTakeBeverage)", StringComparison.Ordinal)
        && rarePrepareLookup < rarePrepare.IndexOf("if(request.AutoStartCooking)", StringComparison.Ordinal),
        "Rare prepare selects strict Yuuma lookup after a beverage read or cooking side effect.");
    AssertContains(
        rarePrepare,
        "existingBeverage!=null",
        "Rare prepare no longer detects an existing beverage.");
    AssertContains(
        rarePrepare,
        "TryValidateYuumaDeliveredItemAgainstOriginalOrder(actionTarget,existingBeverage,RuntimeDeliveryItemKind.Beverage",
        "Rare prepare no longer validates an existing Yuuma beverage against the original Tag identity.");

    var rareComplete = Normalize(ExtractMethod(
        serviceSource,
        "public static OrderPreparationResult CompleteFirst("));
    AssertContains(
        rareComplete,
        "IsYuumaBossRequest(request)?FindRuntimeOrder(request,RuntimeOrderLookupPurpose.YuumaSettlement):FindRuntimeOrder(request,RuntimeOrderLookupPurpose.Completion)",
        "Rare completion no longer isolates strict Yuuma lookup from ordinary and Yuyuko Completion lookup.");
    AssertContains(
        rareComplete,
        "TryValidateYuumaDeliveredItemAgainstOriginalOrder(BuildRareAutomationTarget(request),currentBeverage,RuntimeDeliveryItemKind.Beverage",
        "Rare completion no longer validates an existing Yuuma beverage against the original Tag identity.");

    var normalComplete = Normalize(ExtractMethod(
        serviceSource,
        "public static OrderPreparationResult CompleteNormalFirst("));
    AssertContains(
        normalComplete,
        "yuumaSettlement?FindRuntimeNormalOrder(request,RuntimeOrderLookupPurpose.YuumaSettlement):FindRuntimeNormalOrder(request)",
        "Normal completion no longer isolates strict Yuuma lookup from ordinary and Yuyuko lookup.");
    AssertContains(
        normalComplete,
        "TryValidateYuumaDeliveredItemAgainstOriginalOrder(orderAutomationTarget,servedBeverage,RuntimeDeliveryItemKind.Beverage",
        "Normal completion no longer validates an existing Yuuma beverage against the original item ID.");
    var normalJobResult = normalComplete.IndexOf("varcookingJobResult=autoDeliverFood", StringComparison.Ordinal);
    var normalJobDelivered = normalComplete.IndexOf(
        "if(cookingJobResult.Delivered)",
        Math.Max(0, normalJobResult),
        StringComparison.Ordinal);
    var normalYuumaCompletion = normalComplete.IndexOf(
        "if(yuumaSettlement)",
        Math.Max(0, normalJobDelivered),
        StringComparison.Ordinal);
    var normalCompletedOrder = normalComplete.IndexOf(
        "result.CompletedOrder=true",
        Math.Max(0, normalYuumaCompletion),
        StringComparison.Ordinal);
    var normalImmediateFinish = normalComplete.IndexOf(
        "returnFinish(result)",
        Math.Max(0, normalCompletedOrder),
        StringComparison.Ordinal);
    var normalLegacyServedRead = normalComplete.IndexOf(
        "result.ServedFood=ReadOrderServedFood(runtimeOrder.Order)",
        Math.Max(0, normalImmediateFinish),
        StringComparison.Ordinal);
    AssertTrue(
        normalJobResult >= 0
        && normalJobDelivered > normalJobResult
        && normalYuumaCompletion > normalJobDelivered
        && normalCompletedOrder > normalYuumaCompletion
        && normalImmediateFinish > normalCompletedOrder
        && normalLegacyServedRead > normalImmediateFinish,
        "A delivered normal Yuuma cooking job does not mark CompletedOrder and immediately finish before stale served-item reads.");
    var normalYuumaEvaluationGate = normalComplete.IndexOf(
        "if(yuumaSettlement)",
        Math.Max(0, normalLegacyServedRead),
        StringComparison.Ordinal);
    var normalYuyukoEvaluation = normalComplete.IndexOf(
        "TryEvaluateYuyukoChallengeOrderIfReady(",
        Math.Max(0, normalYuumaEvaluationGate),
        StringComparison.Ordinal);
    var normalGenericEvaluation = normalComplete.IndexOf(
        "TryEvaluateOrderIfReady(",
        Math.Max(0, normalYuumaEvaluationGate),
        StringComparison.Ordinal);
    AssertTrue(
        normalYuumaEvaluationGate > normalLegacyServedRead
        && normalYuyukoEvaluation > normalYuumaEvaluationGate
        && normalGenericEvaluation > normalYuyukoEvaluation,
        "Normal evaluation does not intercept Yuuma before the independent Yuyuko and generic evaluation branches.");
    AssertDoesNotContain(
        normalComplete[normalYuumaEvaluationGate..normalYuyukoEvaluation],
        "TryEvaluateOrderIfReady(",
        "The Yuuma evaluation intercept invokes the generic evaluation entry.");
    AssertDoesNotContain(
        normalComplete[normalYuumaEvaluationGate..normalYuyukoEvaluation],
        "TryEvaluateYuyukoChallengeOrderIfReady(",
        "The Yuuma evaluation intercept invokes the Yuyuko evaluation entry.");

    var normalCookingJobProcessor = Normalize(ExtractMethod(
        cookingSource,
        "private static (bool Delivered, string StepName, string Message, string Code)\n        TryProcessNormalOrderCookingJob("));
    var normalFoodDeliveredResult = normalCookingJobProcessor.IndexOf(
        "result.Code==OrderPreparationStepCodes.FoodDelivered",
        StringComparison.Ordinal);
    var normalYuumaRemovedResult = normalCookingJobProcessor.IndexOf(
        "result.Remove&&IsYuumaBossTarget(job.Target)",
        Math.Max(0, normalFoodDeliveredResult),
        StringComparison.Ordinal);
    var normalLegacyJobServedRead = normalCookingJobProcessor.IndexOf(
        "ReadOrderServedFood(order)",
        Math.Max(0, normalYuumaRemovedResult),
        StringComparison.Ordinal);
    AssertTrue(
        normalFoodDeliveredResult >= 0
        && normalYuumaRemovedResult > normalFoodDeliveredResult
        && normalLegacyJobServedRead > normalYuumaRemovedResult,
        "A Yuuma normal cooking job can report Delivered from the legacy served-field fallback instead of exact FoodDelivered.");

    var directDelivery = Normalize(ExtractMethod(
        directDeliverySource,
        "private static (bool Remove, string Message, string Code) TryDeliverAutomationCookedFood("));
    AssertContains(
        directDelivery,
        "varyuumaTarget=IsYuumaBossTarget(target);varruntimeOrder=yuumaTarget?FindYuumaRuntimeOrder(target,request):target.Kind==CookingCollectionTargetKind.NormalOrder?FindRuntimeNormalOrder(request):FindRuntimeOrder(request)",
        "Cooking-job delivery no longer reserves settlement lookup for Yuuma while preserving ordinary/Yuyuko lookup.");
    var directLookup = directDelivery.IndexOf("varruntimeOrder=yuumaTarget", StringComparison.Ordinal);
    AssertTrue(
        directLookup >= 0
        && directLookup < directDelivery.IndexOf("TryReadYuumaOrderDeliveryState(", directLookup, StringComparison.Ordinal)
        && directLookup < directDelivery.IndexOf("StoreCookedFoodForAlreadyHandledTarget(", directLookup, StringComparison.Ordinal)
        && directLookup < directDelivery.IndexOf("TryFinalizeYuumaCookingJob(", directLookup, StringComparison.Ordinal),
        "Cooking-job delivery reads, stores/releases, or settles before strict Yuuma order lookup.");

    var manualHandoff = Normalize(ExtractMethod(
        cookingSource,
        "private static (bool Remove, string Message, string Code) TryProcessManualHandoffReceipt("));
    AssertContains(
        manualHandoff,
        "IsYuumaBossTarget(job.Target)?FindYuumaRuntimeOrder(job.Target,request):job.Target.Kind==CookingCollectionTargetKind.NormalOrder?FindRuntimeNormalOrder(request):FindRuntimeOrder(request,RuntimeOrderLookupPurpose.Completion)",
        "Yuuma handoff no longer uses settlement lookup while ordinary/Yuyuko receipts retain their original purposes.");
    var handoffLookup = manualHandoff.IndexOf("FindYuumaRuntimeOrder(job.Target,request)", StringComparison.Ordinal);
    AssertTrue(
        handoffLookup >= 0
        && handoffLookup < manualHandoff.IndexOf("if(runtimeOrder.Order==null)", StringComparison.Ordinal)
        && handoffLookup < manualHandoff.IndexOf("TryReadOrderServedItem(", StringComparison.Ordinal),
        "Yuuma handoff reads state or releases its receipt before strict settlement lookup.");

    var beverageDelivery = Normalize(ExtractNamedMethod(
        settlementSource,
        "TryDeliverYuumaOrderBeverage"));
    AssertEqual(
        2,
        CountOccurrences(beverageDelivery, "TryValidateYuumaDeliveredItemAgainstOriginalOrder("),
        "Dedicated Yuuma beverage delivery must validate the existing beverage and new candidate exactly once each.");
    var deliveredItemValidation = Normalize(ExtractNamedMethod(
        settlementSource,
        "TryValidateYuumaDeliveredItemAgainstOriginalOrder"));
    foreach (var normalIdContract in new[]
             {
                 "target.Kind==CookingCollectionTargetKind.NormalOrder",
                 "target.MatchFoodId",
                 "target.MatchBeverageId",
                 "actualId!=expectedId",
             })
    {
        AssertContains(
            deliveredItemValidation,
            normalIdContract,
            $"Normal Yuuma delivered-item ID validation is missing '{normalIdContract}'.");
    }

    foreach (var rareTagContract in new[]
             {
                 "target.FoodTagId",
                 "target.BeverageTagId",
                 "TryReadExactSellableTagIds",
                 "tagIds.Contains(expectedTagId.Value)",
             })
    {
        AssertContains(
            deliveredItemValidation,
            rareTagContract,
            $"Rare Yuuma delivered-item Tag validation is missing '{rareTagContract}'.");
    }
}

static void VerifyYuumaManualOrderCaptureContract(
    string specialCaptureSource,
    string normalCaptureSource,
    string matchingSource)
{
    foreach (var (label, source) in new[]
             {
                 ("special", specialCaptureSource),
                 ("normal", normalCaptureSource),
             })
    {
        var normalized = Normalize(source);
        AssertContains(
            normalized,
            "publicboolManualOrder{get;init;}",
            $"The {label} capture does not retain the exact OrderBase.ManualOrder value.");
        AssertContains(
            normalized,
            "internalobject?ManualEvaluationCallback{get;init;}",
            $"The {label} capture does not retain the exact native manual-evaluation callback.");

        var manualSetter = Normalize(ExtractMethod(
            source,
            "private static void OnManualControllerOrderSet("));
        AssertContains(
            manualSetter,
            "object?__1",
            $"The {label} manual setter hook does not capture the callback argument.");
        AssertContains(
            manualSetter,
            "orderisnot{ManualOrder:true}",
            $"The {label} manual setter accepts an order without exact ManualOrder=true.");
        AssertContains(
            manualSetter,
            "orderwith{ManualEvaluationCallback=__1}",
            $"The {label} manual setter does not bind its callback to the same captured order.");
        AssertContains(
            normalized,
            "requireExactManualOrderSetter:true",
            $"The {label} capture does not require the exact BepInEx 783 manual-order setter signature.");
        var exactManualSetter = Normalize(ExtractMethod(
            source,
            "private static bool IsExactManualOrderSetter("));
        foreach (var required in new[]
                 {
                     "parameters[0].ParameterType.FullName",
                     "GuestGroupControllerTypeName",
                     "parameters[2].ParameterType.FullName",
                     "OrderBaseTypeName",
                     "callbackType.GetGenericTypeDefinition().FullName",
                     "Il2CppActionGenericTypeName",
                     "callbackArguments[0].FullName",
                     "EvaluationResultTypeName",
                 })
        {
            AssertContains(
                exactManualSetter,
                required,
                $"The {label} capture's exact setter resolver is missing '{required}'.");
        }

        var exactManualRead = Normalize(ExtractMethod(
            source,
            "private static bool TryReadExactManualOrder("));
        foreach (var required in new[]
                 {
                     "GetProperty(\"ManualOrder\",flags)",
                     "property.PropertyType!=typeof(bool)",
                     "property.GetValue(order)isnotboolvalue",
                 })
        {
            AssertContains(
                exactManualRead,
                required,
                $"The {label} capture's exact ManualOrder reader is missing '{required}'.");
        }
        AssertDoesNotContain(
            exactManualRead,
            "ReadMember",
            $"The {label} ManualOrder reader restored a broad reflection fallback.");
    }

    var normalizedMatching = Normalize(matchingSource);
    AssertContains(
        normalizedMatching,
        "publicboolManualOrder{get;init;}",
        "RuntimeOrderMatch does not carry the exact manual-order identity.");
    AssertContains(
        normalizedMatching,
        "ManualOrder=requiresYuumaSettlementManualContext&&manualOrder",
        "Yuuma settlement matches no longer project the fresh wrapper ManualOrder state.");
    AssertContains(
        normalizedMatching,
        "ManualEvaluationCallback=requiresYuumaSettlementManualContext?manualOrder?manualEvaluationCallback:null",
        "Yuuma settlement matches no longer project the callback resolved for the current wrapper state.");
    AssertDoesNotContain(
        normalizedMatching,
        "ManualOrder=requiresYuumaSettlementManualContext&&captured.ManualOrder",
        "Captured ManualOrder is trusted as current executable state.");

    var capturedSpecialLookup = Normalize(ExtractNamedMethod(
        matchingSource,
        "FindCapturedRuntimeOrder"));
    var capturedSpecialLivenessGate = capturedSpecialLookup.IndexOf(
        "IsCapturedSpecialOrderLive(",
        StringComparison.Ordinal);
    var capturedSpecialManualRefresh = capturedSpecialLookup.IndexOf(
        "TryResolveSpecialManualContext(",
        Math.Max(0, capturedSpecialLivenessGate),
        StringComparison.Ordinal);
    var capturedSpecialProjection = capturedSpecialLookup.IndexOf(
        "returnnewRuntimeOrderMatch",
        Math.Max(0, capturedSpecialManualRefresh),
        StringComparison.Ordinal);
    AssertTrue(
        capturedSpecialLivenessGate >= 0
        && capturedSpecialManualRefresh > capturedSpecialLivenessGate
        && capturedSpecialProjection > capturedSpecialManualRefresh,
        "A captured special Yuuma candidate does not refresh manual state after liveness/identity and before projection.");
    AssertContains(
        capturedSpecialLookup[capturedSpecialManualRefresh..],
        "outmanualOrder,outmanualEvaluationCallback",
        "Captured special Yuuma lookup does not receive the current wrapper manual state.");

    var capturedNormalLookup = Normalize(ExtractNamedMethod(
        matchingSource,
        "FindCapturedRuntimeNormalOrder"));
    var capturedNormalOwnershipGate = capturedNormalLookup.IndexOf(
        "EnumerateControllerOrders(",
        StringComparison.Ordinal);
    var capturedNormalIdentityGate = capturedNormalLookup.IndexOf(
        "IsMatchingNormalOrder(",
        Math.Max(0, capturedNormalOwnershipGate),
        StringComparison.Ordinal);
    var capturedNormalManualRefresh = capturedNormalLookup.IndexOf(
        "TryResolveNormalManualContext(",
        Math.Max(0, capturedNormalIdentityGate),
        StringComparison.Ordinal);
    var capturedNormalProjection = capturedNormalLookup.IndexOf(
        "returnnewRuntimeOrderMatch",
        Math.Max(0, capturedNormalManualRefresh),
        StringComparison.Ordinal);
    AssertTrue(
        capturedNormalOwnershipGate >= 0
        && capturedNormalIdentityGate > capturedNormalOwnershipGate
        && capturedNormalManualRefresh > capturedNormalIdentityGate
        && capturedNormalProjection > capturedNormalManualRefresh,
        "A captured normal Yuuma candidate does not refresh manual state after ownership/identity and before projection.");
    AssertContains(
        capturedNormalLookup[capturedNormalManualRefresh..],
        "outmanualOrder,outmanualEvaluationCallback",
        "Captured normal Yuuma lookup does not receive the current wrapper manual state.");

    foreach (var (label, resolverName) in new[]
             {
                 ("special", "TryResolveSpecialManualContext"),
                 ("normal", "TryResolveNormalManualContext"),
             })
    {
        var resolver = Normalize(ExtractNamedMethod(matchingSource, resolverName));
        var currentFalse = resolver.IndexOf("if(!manualOrder)", StringComparison.Ordinal);
        var callbackLookup = resolver.IndexOf("varcallbackCandidate", StringComparison.Ordinal);
        var missingCallback = resolver.IndexOf(
            "if(callbackCandidate==null)",
            Math.Max(0, callbackLookup),
            StringComparison.Ordinal);
        AssertTrue(
            resolver.IndexOf("TryReadExactManualOrder(", StringComparison.Ordinal) >= 0
            && currentFalse >= 0
            && callbackLookup > currentFalse
            && missingCallback > callbackLookup,
            $"The {label} resolver does not base settlement routing on current ManualOrder before callback lookup.");
        AssertContains(
            resolver[currentFalse..callbackLookup],
            "manualCallback=not-required\";returntrue;",
            $"A captured {label} candidate with current ManualOrder=false is not accepted as the current standard route.");
        AssertContains(
            resolver[missingCallback..],
            "manualCallback=missing",
            $"A captured {label} candidate with current ManualOrder=true and no callback lacks a rejection diagnostic.");
        AssertContains(
            resolver[missingCallback..],
            "returnfalse;",
            $"A captured {label} candidate with current ManualOrder=true and no callback is not rejected.");
    }
    AssertContains(
        normalizedMatching,
        "requiresYuumaSettlementManualContext=purpose==RuntimeOrderLookupPurpose.YuumaSettlement",
        "Yuuma strict manual-order matching is not isolated behind a dedicated lookup purpose.");
    AssertContains(
        normalizedMatching,
        "if(requiresYuumaSettlementManualContext&&!TryResolveSpecialManualContext(",
        "Special-order callback validation is no longer scoped to Yuuma settlement.");
    AssertContains(
        normalizedMatching,
        "if(requiresYuumaSettlementManualContext&&!TryResolveNormalManualContext(",
        "Normal-order callback validation is no longer scoped to Yuuma settlement.");
    AssertContains(
        normalizedMatching,
        "if(requiresYuumaSettlementManualContext){try{varownedByController=EnumerateControllerOrders(captured.ControllerObject).Any(order=>CompareObjectIdentity(order,captured.OrderObject)==RuntimeObjectIdentityComparison.Same);if(!ownedByController)continue;}catch{continue;}}",
        "Captured normal Yuuma settlement orders can outlive their exact controller ownership.");
    AssertContains(
        normalizedMatching,
        "requiresYuyukoStoryManualEvaluation",
        "The established Yuyuko story manual-evaluation boundary was removed.");
    AssertContains(
        normalizedMatching,
        "FindCapturedYuyukoPhase3ManualEvaluationCallback(",
        "The established Yuyuko story callback lookup was replaced by the Yuuma settlement path.");
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

static string ExtractMethod(string source, string signature)
{
    var signatureIndex = source.IndexOf(signature, StringComparison.Ordinal);
    if (signatureIndex < 0)
    {
        throw new InvalidOperationException($"Source method not found: {signature}");
    }

    var openingBrace = source.IndexOf('{', signatureIndex);
    if (openingBrace < 0)
    {
        throw new InvalidOperationException($"Source method has no body: {signature}");
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

        if (inString || inCharacter) continue;
        if (current == '{')
        {
            depth++;
        }
        else if (current == '}' && --depth == 0)
        {
            return source[signatureIndex..(index + 1)];
        }
    }

    throw new InvalidOperationException($"Source method body is incomplete: {signature}");
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
            var marker = source[lineStart..(nameIndex + methodName.Length + 1)];
            return ExtractMethod(source, marker);
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

static string Normalize(string value)
{
    var builder = new StringBuilder(value.Length);
    foreach (var character in value)
    {
        if (!char.IsWhiteSpace(character))
        {
            builder.Append(character);
        }
    }

    return builder.ToString();
}

static int CountOccurrences(string value, string needle)
{
    var count = 0;
    var offset = 0;
    while ((offset = value.IndexOf(needle, offset, StringComparison.Ordinal)) >= 0)
    {
        count++;
        offset += needle.Length;
    }

    return count;
}

static void AssertBlockedRole(
    SpecialBusinessOrderClassification classification,
    string expectedRole,
    string message)
{
    AssertFalse(classification.AutomationAllowed, message);
    AssertEqual(expectedRole, classification.Role, message);
    AssertTrue(classification.AutomationBlockReason.Length > 0, $"{message} No block reason was provided.");
}

static void AssertAllowedRole(
    SpecialBusinessOrderClassification classification,
    string expectedRole,
    string message)
{
    AssertTrue(classification.AutomationAllowed, message);
    AssertEqual(expectedRole, classification.Role, message);
    AssertEqual("", classification.AutomationBlockReason, $"{message} An allowed role retained a block reason.");
}

static void AssertContains(string value, string expected, string message)
{
    AssertTrue(value.Contains(expected, StringComparison.Ordinal), $"{message} Missing: {expected}");
}

static void AssertDoesNotContain(string value, string expected, string message)
{
    AssertFalse(value.Contains(expected, StringComparison.Ordinal), $"{message} Found: {expected}");
}

static void AssertEqual<T>(T expected, T actual, string message)
    where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{message} Expected: {expected}; actual: {actual}.");
    }
}

static void AssertFalse(bool condition, string message)
{
    AssertTrue(!condition, message);
}

static void AssertTrue(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
