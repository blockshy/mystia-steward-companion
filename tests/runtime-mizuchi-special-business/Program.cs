using GameData.Core.Collections.NightSceneUtility;
using GameData.Profile;
using MystiaStewardCompanion.Save;
using NightScene.GuestManagementUtility;
using NormalOrder = NightScene.GuestManagementUtility.GuestsManager.NormalOrder;
using OrderBase = NightScene.GuestManagementUtility.GuestsManager.OrderBase;
using SpecialOrder = NightScene.GuestManagementUtility.GuestsManager.SpecialOrder;

try
{
    VerifyExactChallengeContracts();
    VerifyPossessedAndOrdinaryRoles();
    VerifyStoryPossessedAndOrdinaryRoles();
    VerifyNormalOrdersRemainStandard();
    VerifyExactOrderBaseConversion();
    VerifyBaseControllerWrapperConversion();
    VerifyClosureIdentityFailures();
    VerifyTrialInvariantFailures();
    VerifyAutomationPolicyContracts();
    VerifySourceContracts();
    Console.WriteLine("PASS: Mizuchi story and trials use exact non-negative guest identities, callback-closure identity, native active/no-target states, scene-specific possessed/ordinary roles and target ingredients, fulfilled-only evaluation preflights, and read-only HUD progress hooks.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL: {ex}");
    return 1;
}

static void VerifyExactChallengeContracts()
{
    var module = new MizuchiOrderModule();
    foreach (var challenge in new[]
             {
                 SpecialBusinessChallengeTypes.StoryMizuchi,
                 SpecialBusinessChallengeTypes.MizuchiTrial1,
                 SpecialBusinessChallengeTypes.MizuchiTrial2,
                 SpecialBusinessChallengeTypes.MizuchiTrial3,
             })
    {
        AssertTrue(module.MatchesChallenge(challenge), $"Exact Mizuchi challenge was not matched: {challenge}");
    }

    foreach (var challenge in new[]
             {
                 "story_mizuchi",
                 "story_mizuchi_1",
                 "Challenge_Mizuchi_1",
                 "月都试炼1",
             })
    {
        AssertFalse(module.MatchesChallenge(challenge), $"Challenge alias unexpectedly activated the module: {challenge}");
    }

    AssertEqual(5005, MizuchiConstants.PepperWaterIngredientId, "Pepper-water ingredient ID changed.");
    AssertEqual(5002, MizuchiConstants.PuyoyoFruitIngredientId, "Puyoyo-fruit ingredient ID changed.");
    AssertEqual(-1, MizuchiConstants.NoControlledGuestId, "The native no-controlled-guest sentinel changed.");
    AssertEqual(3, MizuchiConstants.NoControlType, "The native no-control enum value changed.");
    AssertExpectedControl(SpecialBusinessChallengeTypes.MizuchiTrial1, 1);
    AssertExpectedControl(SpecialBusinessChallengeTypes.MizuchiTrial2, 0);
    AssertExpectedControl(SpecialBusinessChallengeTypes.MizuchiTrial3, 2);
    AssertTrue(
        MizuchiConstants.TryGetChallengeContract(
            SpecialBusinessChallengeTypes.StoryMizuchi,
            out var storyContract),
        "Story Mizuchi contract is unavailable.");
    AssertTrue(storyContract.IsBaseChallenge, "Story Mizuchi did not retain the base-challenge flag.");
    AssertEqual(5002, storyContract.TargetIngredientId, "Story Mizuchi target ingredient changed.");
    AssertEqual<int?>(null, storyContract.ExpectedControlType, "Story Mizuchi unexpectedly fixed one control type.");
}

static void VerifyStoryPossessedAndOrdinaryRoles()
{
    var challenge = SpecialBusinessChallengeTypes.StoryMizuchi;
    foreach (var control in new[]
             {
                 DLC5_MizuchiChallengeBossData.MizuchiControlType.WrongBeverageTag,
                 DLC5_MizuchiChallengeBossData.MizuchiControlType.WrongFoodOrder,
                 DLC5_MizuchiChallengeBossData.MizuchiControlType.WrongTalkingDialog,
             })
    {
        var possessed = CreateFixture(challenge, 201, 201, control);
        var identity = MizuchiOrderIdentity.Read(challenge, possessed.Order, possessed.Controller);
        AssertTrue(identity.Verified, $"Story possessed identity failed for {control}: {identity.Reason}");
        AssertTrue(identity.IsPossessed, $"Story controlled guest was not possessed for {control}.");
        AssertEqual(5002, identity.TargetIngredientId, "Story identity lost the Puyoyo-fruit target.");
        AssertEqual(true, identity.IsMizuchiChallenge, "Story identity lost the base-challenge flag.");
        AssertAllowedRole(
            Classify(challenge, possessed.Order, possessed.Controller),
            SpecialBusinessOrderRoles.MizuchiStoryPossessed,
            $"Story possessed role was rejected for {control}.");

        var ordinary = CreateFixture(challenge, 202, 201, control);
        AssertAllowedRole(
            Classify(challenge, ordinary.Order, ordinary.Controller),
            SpecialBusinessOrderRoles.MizuchiStoryOrdinary,
            $"Story ordinary role was rejected for {control}.");
    }

    var noTarget = CreateFixture(
        challenge,
        203,
        MizuchiConstants.NoControlledGuestId,
        DLC5_MizuchiChallengeBossData.MizuchiControlType.None);
    AssertAllowedRole(
        Classify(challenge, noTarget.Order, noTarget.Controller),
        SpecialBusinessOrderRoles.MizuchiStoryOrdinary,
        "Story native (-1, None) protection phase did not remain ordinary.");

    var invalidActive = CreateFixture(
        challenge,
        204,
        204,
        DLC5_MizuchiChallengeBossData.MizuchiControlType.None);
    AssertBlocked(
        Classify(challenge, invalidActive.Order, invalidActive.Controller),
        "control=3/0..2",
        SpecialBusinessOrderRoles.MizuchiStoryUnverified);

    var wrongIngredient = CreateFixture(
        challenge,
        205,
        205,
        DLC5_MizuchiChallengeBossData.MizuchiControlType.WrongFoodOrder);
    wrongIngredient.Parent.targetIngredientId = MizuchiConstants.PepperWaterIngredientId;
    AssertBlocked(
        Classify(challenge, wrongIngredient.Order, wrongIngredient.Controller),
        "ingredient=5005/5002",
        SpecialBusinessOrderRoles.MizuchiStoryUnverified);

    var wrongFlag = CreateFixture(
        challenge,
        206,
        206,
        DLC5_MizuchiChallengeBossData.MizuchiControlType.WrongFoodOrder);
    wrongFlag.Parent.isMizuchiChallenge = false;
    AssertBlocked(
        Classify(challenge, wrongFlag.Order, wrongFlag.Controller),
        "baseChallenge=False/True",
        SpecialBusinessOrderRoles.MizuchiStoryUnverified);
}

static void VerifyPossessedAndOrdinaryRoles()
{
    var cases = new[]
    {
        (SpecialBusinessChallengeTypes.MizuchiTrial1, DLC5_MizuchiChallengeBossData.MizuchiControlType.WrongFoodOrder),
        (SpecialBusinessChallengeTypes.MizuchiTrial2, DLC5_MizuchiChallengeBossData.MizuchiControlType.WrongBeverageTag),
        (SpecialBusinessChallengeTypes.MizuchiTrial3, DLC5_MizuchiChallengeBossData.MizuchiControlType.WrongTalkingDialog),
    };

    foreach (var (challenge, control) in cases)
    {
        var possessed = CreateFixture(challenge, guestId: 81, controlledGuestId: 81, control);
        var possessedIdentity = MizuchiOrderIdentity.Read(challenge, possessed.Order, possessed.Controller);
        AssertTrue(possessedIdentity.Verified, $"Possessed identity failed: {possessedIdentity.Reason}");
        AssertTrue(possessedIdentity.IsPossessed, "Controlled guest was not classified as possessed.");
        AssertEqual(81, possessedIdentity.SelectedGuestId, "Selected guest identity was not retained.");
        AssertEqual(5005, possessedIdentity.TargetIngredientId, "Target ingredient was not retained.");
        AssertEqual(2, possessedIdentity.CatchCount, "Catch progress was not retained.");
        AssertEqual(5, possessedIdentity.RequiredCatchCount, "Required catch count was not retained.");
        AssertAllowedRole(
            Classify(challenge, possessed.Order, possessed.Controller),
            SpecialBusinessOrderRoles.MizuchiTrialPossessed,
            "Possessed trial order was not allowed under its exact role.");

        var ordinary = CreateFixture(challenge, guestId: 82, controlledGuestId: 99, control);
        var ordinaryIdentity = MizuchiOrderIdentity.Read(challenge, ordinary.Order, ordinary.Controller);
        AssertTrue(ordinaryIdentity.Verified, $"Ordinary identity failed: {ordinaryIdentity.Reason}");
        AssertFalse(ordinaryIdentity.IsPossessed, "Uncontrolled guest was classified as possessed.");
        AssertAllowedRole(
            Classify(challenge, ordinary.Order, ordinary.Controller),
            SpecialBusinessOrderRoles.MizuchiTrialOrdinary,
            "Ordinary trial order did not receive its explicit role.");

        var zeroIdPossessed = CreateFixture(challenge, guestId: 0, controlledGuestId: 0, control);
        var zeroIdIdentity = MizuchiOrderIdentity.Read(
            challenge,
            zeroIdPossessed.Order,
            zeroIdPossessed.Controller);
        AssertTrue(zeroIdIdentity.Verified, $"Guest ID 0 identity failed: {zeroIdIdentity.Reason}");
        AssertTrue(zeroIdIdentity.IsPossessed, "The controlled guest with native ID 0 was not classified as possessed.");
        AssertEqual(0, zeroIdIdentity.SelectedGuestId, "The native selected guest ID 0 was not retained.");
        var zeroIdClassification = Classify(
            challenge,
            zeroIdPossessed.Order,
            zeroIdPossessed.Controller);
        AssertAllowedRole(
            zeroIdClassification,
            SpecialBusinessOrderRoles.MizuchiTrialPossessed,
            "The native guest ID 0 possessed order was rejected.");
        AssertEqual(0, zeroIdClassification.RuntimeGuestId, "The allowed role did not preserve native guest ID 0.");

        var zeroControlledOrdinary = CreateFixture(challenge, guestId: 84, controlledGuestId: 0, control);
        AssertAllowedRole(
            Classify(challenge, zeroControlledOrdinary.Order, zeroControlledOrdinary.Controller),
            SpecialBusinessOrderRoles.MizuchiTrialOrdinary,
            "A non-controlled guest was rejected while native guest ID 0 was possessed.");

        var noTarget = CreateFixture(
            challenge,
            guestId: 83,
            controlledGuestId: MizuchiConstants.NoControlledGuestId,
            DLC5_MizuchiChallengeBossData.MizuchiControlType.None);
        var noTargetIdentity = MizuchiOrderIdentity.Read(challenge, noTarget.Order, noTarget.Controller);
        AssertTrue(noTargetIdentity.Verified, $"Native no-target identity failed: {noTargetIdentity.Reason}");
        AssertFalse(noTargetIdentity.IsPossessed, "An order was classified as possessed during the native no-target phase.");
        AssertContains(noTargetIdentity.Reason, "no-target state", "The native no-target reason was not retained.");
        AssertAllowedRole(
            Classify(challenge, noTarget.Order, noTarget.Controller),
            SpecialBusinessOrderRoles.MizuchiTrialOrdinary,
            "The native (-1, None) no-target state did not produce an ordinary role.");
    }
}

static void VerifyNormalOrdersRemainStandard()
{
    var guest = new GuestBase(81);
    var result = Classify(
        SpecialBusinessChallengeTypes.MizuchiTrial1,
        new NormalOrder(guest),
        controller: null);
    AssertTrue(result == SpecialBusinessOrderClassification.Standard, "NormalOrder was pulled into the Mizuchi special-order path.");
}

static void VerifyExactOrderBaseConversion()
{
    var fixture = CreateFixture(
        SpecialBusinessChallengeTypes.MizuchiTrial1,
        guestId: 91,
        controlledGuestId: 91,
        DLC5_MizuchiChallengeBossData.MizuchiControlType.WrongFoodOrder);
    var wrapped = new OrderBase(normalOrder: null, specialOrder: fixture.Order);
    AssertAllowedRole(
        Classify(SpecialBusinessChallengeTypes.MizuchiTrial1, wrapped, fixture.Controller),
        SpecialBusinessOrderRoles.MizuchiTrialPossessed,
        "Exact OrderBase -> SpecialOrder conversion was rejected.");

    var ambiguous = new OrderBase(new NormalOrder(new GuestBase(91)), fixture.Order);
    AssertBlocked(
        Classify(SpecialBusinessChallengeTypes.MizuchiTrial1, ambiguous, fixture.Controller),
        "both NormalOrder and SpecialOrder conversions succeeded");
}

static void VerifyBaseControllerWrapperConversion()
{
    var challenge = SpecialBusinessChallengeTypes.MizuchiTrial1;
    var fixture = CreateFixture(
        challenge,
        guestId: 96,
        controlledGuestId: 96,
        DLC5_MizuchiChallengeBossData.MizuchiControlType.WrongFoodOrder);
    var baseWrapper = fixture.Controller.AsBaseWrapper();

    AssertEqual(
        typeof(GuestGroupController).FullName,
        baseWrapper.GetType().FullName,
        "The regression fixture did not reproduce the runtime base wrapper.");
    AssertEqual(
        fixture.Controller.Pointer,
        baseWrapper.Pointer,
        "The regression fixture did not preserve the native controller identity.");
    AssertAllowedRole(
        Classify(challenge, fixture.Order, baseWrapper),
        SpecialBusinessOrderRoles.MizuchiTrialPossessed,
        "A GuestGroupController wrapper for the exact native SpecialGuestsController was rejected.");

    var uncastable = new GuestGroupController(fixture.Controller.OrderingGuest)
    {
        Pointer = fixture.Controller.Pointer,
        OverrideEvaluationCallback = fixture.Controller.OverrideEvaluationCallback,
    };
    AssertBlocked(
        Classify(challenge, fixture.Order, uncastable),
        "exact NightScene.GuestManagementUtility.SpecialGuestsController cast failed");

    var pointerMismatch = CreateFixture(
        challenge,
        guestId: 97,
        controlledGuestId: 97,
        DLC5_MizuchiChallengeBossData.MizuchiControlType.WrongFoodOrder);
    var staleBaseWrapper = pointerMismatch.Controller.AsBaseWrapper();
    pointerMismatch.Controller.Pointer = new IntPtr(pointerMismatch.Controller.Pointer.ToInt64() + 1);
    AssertBlocked(
        Classify(challenge, pointerMismatch.Order, staleBaseWrapper),
        "controller cast native pointer mismatch");
}

static void VerifyClosureIdentityFailures()
{
    var challenge = SpecialBusinessChallengeTypes.MizuchiTrial1;
    var control = DLC5_MizuchiChallengeBossData.MizuchiControlType.WrongFoodOrder;

    var callbackMissing = CreateFixture(challenge, 101, 101, control);
    callbackMissing.Controller.OverrideEvaluationCallback = null;
    AssertBlocked(Classify(challenge, callbackMissing.Order, callbackMissing.Controller), "OverrideEvaluationCallback");

    var multicast = CreateFixture(challenge, 102, 102, control, invocationCount: 2);
    AssertBlocked(Classify(challenge, multicast.Order, multicast.Controller), "exactly 1");

    var wrongMethod = CreateFixture(challenge, 103, 103, control, callbackMethod: "<MainChallengeLoop>g__Other|74");
    AssertBlocked(Classify(challenge, wrongMethod.Order, wrongMethod.Controller), "method mismatch");

    var groupMismatch = CreateFixture(challenge, 104, 104, control);
    groupMismatch.Closure.group = new SpecialGuestsController(new SpecialGuest(104));
    AssertBlocked(Classify(challenge, groupMismatch.Order, groupMismatch.Controller), "pointer mismatch");

    var selectedMismatch = CreateFixture(challenge, 105, 105, control);
    selectedMismatch.Closure.selectedGuestGroup = 106;
    AssertBlocked(Classify(challenge, selectedMismatch.Order, selectedMismatch.Controller), "closure guest identity mismatch");

    var controllerMismatch = CreateFixture(challenge, 107, 107, control);
    var wrongController = new SpecialGuestsController(new SpecialGuest(108));
    wrongController.OverrideEvaluationCallback = controllerMismatch.Controller.OverrideEvaluationCallback;
    AssertBlocked(Classify(challenge, controllerMismatch.Order, wrongController), "identity mismatch");

    var parentMissing = CreateFixture(challenge, 109, 109, control);
    parentMissing.Closure.field_Public___c__DisplayClass66_0_0 = null;
    AssertBlocked(Classify(challenge, parentMissing.Order, parentMissing.Controller), "parent Mizuchi closure");
}

static void VerifyTrialInvariantFailures()
{
    var challenge = SpecialBusinessChallengeTypes.MizuchiTrial2;
    var control = DLC5_MizuchiChallengeBossData.MizuchiControlType.WrongBeverageTag;

    var wrongControl = CreateFixture(challenge, 121, 121, DLC5_MizuchiChallengeBossData.MizuchiControlType.WrongFoodOrder);
    AssertBlocked(Classify(challenge, wrongControl.Order, wrongControl.Controller), "invariants mismatch");

    var staleActiveControl = CreateFixture(
        challenge,
        127,
        MizuchiConstants.NoControlledGuestId,
        control);
    AssertBlocked(
        Classify(challenge, staleActiveControl.Order, staleActiveControl.Controller),
        "controlled=-1, control=0/0");

    var missingActiveControl = CreateFixture(
        challenge,
        128,
        controlledGuestId: 128,
        DLC5_MizuchiChallengeBossData.MizuchiControlType.None);
    AssertBlocked(
        Classify(challenge, missingActiveControl.Order, missingActiveControl.Controller),
        "controlled=128, control=3/0");

    var oldIngredient = CreateFixture(challenge, 122, 122, control);
    oldIngredient.Parent.targetIngredientId = 5002;
    AssertBlocked(Classify(challenge, oldIngredient.Order, oldIngredient.Controller), "ingredient=5002");

    var baseChallenge = CreateFixture(challenge, 123, 123, control);
    baseChallenge.Parent.isMizuchiChallenge = true;
    AssertBlocked(Classify(challenge, baseChallenge.Order, baseChallenge.Controller), "baseChallenge=True");

    var invalidControlled = CreateFixture(challenge, 124, -2, control);
    AssertBlocked(Classify(challenge, invalidControlled.Order, invalidControlled.Controller), "outside the exact domain");

    var invalidGuest = CreateFixture(challenge, -1, -1, control);
    AssertBlocked(Classify(challenge, invalidGuest.Order, invalidGuest.Controller), "order/controller guest identity mismatch");

    var invalidProgress = CreateFixture(challenge, 125, 125, control);
    invalidProgress.Parent.needCatchMizuchiTime = 0;
    AssertBlocked(Classify(challenge, invalidProgress.Order, invalidProgress.Controller), "catches=2/0");

    var overflowProgress = CreateFixture(challenge, 126, 126, control);
    overflowProgress.Parent.catchMizuchiNum = 6;
    AssertBlocked(Classify(challenge, overflowProgress.Order, overflowProgress.Controller), "catches=6/5");
}

static void VerifyAutomationPolicyContracts()
{
    var pepper = MizuchiConstants.PepperWaterIngredientId;
    var fruit = MizuchiConstants.PuyoyoFruitIngredientId;
    AssertTrue(
        MizuchiAutomationPolicy.TryValidateRequest(
            SpecialBusinessOrderRoles.MizuchiStoryPossessed,
            new[] { fruit },
            out _),
        "Story possessed request with exact Puyoyo fruit was rejected.");
    AssertFalse(
        MizuchiAutomationPolicy.TryValidateRequest(
            SpecialBusinessOrderRoles.MizuchiStoryPossessed,
            new[] { pepper },
            out _),
        "Story possessed request without Puyoyo fruit was allowed.");
    AssertTrue(
        MizuchiAutomationPolicy.TryValidateRequest(
            SpecialBusinessOrderRoles.MizuchiStoryOrdinary,
            Array.Empty<int>(),
            out _),
        "Story ordinary request without Puyoyo fruit was rejected.");
    AssertFalse(
        MizuchiAutomationPolicy.TryValidateRequest(
            SpecialBusinessOrderRoles.MizuchiStoryOrdinary,
            new[] { fruit },
            out _),
        "Story ordinary request was allowed to feed Puyoyo fruit to the wrong guest.");
    AssertFalse(
        MizuchiAutomationPolicy.TryValidateRolePair(
            SpecialBusinessOrderRoles.MizuchiStoryPossessed,
            SpecialBusinessOrderRoles.MizuchiTrialPossessed,
            new[] { fruit },
            out _),
        "Story and trial possessed roles were treated as interchangeable.");
    AssertTrue(
        MizuchiAutomationPolicy.TryGetTargetIngredientId(
            SpecialBusinessOrderRoles.MizuchiStoryPossessed,
            out var storyIngredient),
        "Story target ingredient was unavailable from its exact role.");
    AssertEqual(fruit, storyIngredient, "Story role resolved the wrong target ingredient.");
    AssertTrue(
        MizuchiAutomationPolicy.TryValidateRequest(
            SpecialBusinessOrderRoles.MizuchiTrialPossessed,
            new[] { pepper },
            out _),
        "Possessed request with exact pepper-water Modifier was rejected.");
    AssertTrue(
        MizuchiAutomationPolicy.TryValidateRequest(
            SpecialBusinessOrderRoles.MizuchiTrialPossessed,
            new[] { 5010, pepper },
            out _),
        "Possessed request with additional unique extras was rejected.");
    AssertFalse(
        MizuchiAutomationPolicy.TryValidateRequest(
            SpecialBusinessOrderRoles.MizuchiTrialPossessed,
            Array.Empty<int>(),
            out _),
        "Possessed request without pepper water was allowed.");
    AssertFalse(
        MizuchiAutomationPolicy.TryValidateRequest(
            SpecialBusinessOrderRoles.MizuchiTrialPossessed,
            new[] { pepper, pepper },
            out _),
        "Possessed request with duplicate pepper water was allowed.");
    AssertTrue(
        MizuchiAutomationPolicy.TryValidateRequest(
            SpecialBusinessOrderRoles.MizuchiTrialOrdinary,
            Array.Empty<int>(),
            out _),
        "Ordinary trial request without forced extras was rejected.");
    AssertTrue(
        MizuchiAutomationPolicy.TryValidateRequest(
            SpecialBusinessOrderRoles.MizuchiTrialOrdinary,
            new[] { 5010 },
            out _),
        "Ordinary trial request with an unrelated unique extra was rejected.");
    AssertFalse(
        MizuchiAutomationPolicy.TryValidateRequest(
            SpecialBusinessOrderRoles.MizuchiTrialOrdinary,
            new[] { pepper },
            out _),
        "Ordinary trial request was allowed to feed pepper water to the wrong guest.");
    AssertFalse(
        MizuchiAutomationPolicy.TryValidateRequest(
            SpecialBusinessOrderRoles.MizuchiTrialUnverified,
            new[] { pepper },
            out _),
        "Unverified trial role entered automation.");

    AssertTrue(
        MizuchiAutomationPolicy.TryValidateRolePair(
            SpecialBusinessOrderRoles.MizuchiTrialPossessed,
            SpecialBusinessOrderRoles.MizuchiTrialPossessed,
            new[] { pepper },
            out _),
        "Exact possessed role pair was rejected.");
    AssertFalse(
        MizuchiAutomationPolicy.TryValidateRolePair(
            SpecialBusinessOrderRoles.MizuchiTrialPossessed,
            SpecialBusinessOrderRoles.MizuchiTrialOrdinary,
            new[] { pepper },
            out _),
        "Possessed request was allowed to target an ordinary order.");
    AssertFalse(
        MizuchiAutomationPolicy.TryValidateRolePair(
            "",
            SpecialBusinessOrderRoles.MizuchiTrialPossessed,
            new[] { pepper },
            out _),
        "A request without an exact trial role was allowed to target a possessed order.");
    AssertTrue(
        MizuchiAutomationPolicy.TryValidateRolePair("", "", Array.Empty<int>(), out _),
        "Non-Mizuchi order was changed by the Mizuchi policy.");
}

static void VerifySourceContracts()
{
    var root = FindRepositoryRoot();
    var contextRules = File.ReadAllText(Path.Combine(root, "mods", "bepinex", "src", "Save", "SpecialBusiness", "SpecialBusinessContextRuleRegistry.cs"));
    var contextRuntime = File.ReadAllText(Path.Combine(root, "mods", "bepinex", "src", "Save", "RuntimeSpecialBusinessContextService.cs"));
    var moduleRegistry = File.ReadAllText(Path.Combine(root, "mods", "bepinex", "src", "Save", "SpecialBusiness", "SpecialBusinessModuleRegistry.cs"));
    var identitySource = File.ReadAllText(Path.Combine(root, "mods", "bepinex", "src", "Save", "SpecialBusiness", "MizuchiOrderIdentity.cs"));
    var automationPolicy = File.ReadAllText(Path.Combine(root, "mods", "bepinex", "src", "Save", "SpecialBusiness", "RuntimeOrderPreparationService.MizuchiPolicy.cs"));
    var modifierPolicy = File.ReadAllText(Path.Combine(root, "mods", "bepinex", "src", "Save", "SpecialBusiness", "RuntimeOrderPreparationService.FoodModifierValidation.cs"));
    var yuyukoPolicy = File.ReadAllText(Path.Combine(root, "mods", "bepinex", "src", "Save", "SpecialBusiness", "RuntimeOrderPreparationService.YuyukoChallengePolicy.cs"));
    var orderMatching = File.ReadAllText(Path.Combine(root, "mods", "bepinex", "src", "Save", "RuntimeOrderPreparationService.OrderMatching.cs"));
    var cooking = File.ReadAllText(Path.Combine(root, "mods", "bepinex", "src", "Save", "RuntimeOrderPreparationService.Cooking.cs"));
    var directDelivery = File.ReadAllText(Path.Combine(root, "mods", "bepinex", "src", "Save", "RuntimeOrderPreparationService.DirectDelivery.cs"));
    var delivery = File.ReadAllText(Path.Combine(root, "mods", "bepinex", "src", "Save", "RuntimeOrderPreparationService.Delivery.cs"));
    var service = File.ReadAllText(Path.Combine(root, "mods", "bepinex", "src", "Save", "RuntimeOrderPreparationService.cs"));
    var modelSource = File.ReadAllText(Path.Combine(root, "mods", "bepinex", "src", "Core", "Models.cs"));

    AssertContains(moduleRegistry, "new MizuchiOrderModule()", "Mizuchi challenge module is not registered.");
    AssertContains(contextRules, "[SpecialBusinessChallengeTypes.StoryMizuchi] = MizuchiStory()", "Story Mizuchi active rule is missing.");
    AssertNotContains(contextRules, "MizuchiUnadapted", "The obsolete passive Story Mizuchi rule remains.");
    AssertContains(contextRules, "[SpecialBusinessChallengeTypes.MizuchiTrial1] = MizuchiTrial()", "Trial 1 rule is missing.");
    AssertContains(contextRules, "new[] { MizuchiConstants.PuyoyoFruitIngredientId }", "Story required ingredient is not projected.");
    AssertContains(contextRules, "new[] { MizuchiConstants.PepperWaterIngredientId }", "Trial required ingredient is not projected.");
    AssertContains(contextRuntime, "SpecialBusinessChallengeTypes.StoryMizuchi => \"mizuchi\"", "Story HUD progress owner is missing.");
    AssertContains(modelSource, "RequiredExtraIngredientIds", "Special-business API model lacks required extra ingredients.");

    foreach (var method in new[]
             {
                 "SetTargetNum",
                 "SetTargetCatchProgress",
                 "SetTargetCatchProgressImmediate",
             })
    {
        AssertContains(contextRuntime, $"\"{method}\"", $"Stable Mizuchi HUD hook is missing: {method}");
    }

    AssertNotContains(contextRuntime, "DLC5_MizuchiChallengeBossData._MainChallengeLoop_d__66", "Compiler-generated Mizuchi state machine was hooked.");
    AssertNotContains(
        identitySource,
        "Method_Internal_EvaluationResult_EvaluationResult_GuestGroupController_Boolean_byref_String_byref_Boolean_PDM_0(",
        "Identity validation invokes the native evaluation callback.");
    AssertNotContains(identitySource, "FindUnity", "Identity validation scans scene objects.");
    AssertNotContains(identitySource, "GetOrderFoodText", "Identity validation reads display text.");
    AssertNotContains(identitySource, "GetOrderBevText", "Identity validation reads display text.");
    AssertContains(
        identitySource,
        "TryCastRuntimeObject(\n            controller,\n            SpecialGuestsControllerTypeName)",
        "The captured base controller wrapper is not converted to the exact SpecialGuestsController.");
    AssertContains(
        identitySource,
        "specialControllerPointer != controllerPointer",
        "The exact controller cast does not preserve native object identity.");
    AssertContains(
        identitySource,
        "if (orderGuestId < 0",
        "The exact order identity no longer uses the runtime catalog's non-negative guest ID domain.");
    AssertContains(
        identitySource,
        "if (selectedGuestId < 0",
        "The callback closure no longer accepts native selected guest ID 0.");
    AssertContains(
        identitySource,
        "if (controlledGuestId < MizuchiConstants.NoControlledGuestId)",
        "The controlled guest domain no longer distinguishes native -1 from non-negative guest IDs.");
    AssertContains(
        identitySource,
        "controlledGuestId != MizuchiConstants.NoControlledGuestId",
        "The identity reader does not explicitly distinguish an active target from the native no-target state.");
    AssertContains(
        identitySource,
        "controlType == MizuchiConstants.NoControlType",
        "The native (-1, None) no-target state is not modeled explicitly.");
    AssertContains(
        identitySource,
        "contract.ExpectedControlType.HasValue",
        "The active-target state no longer distinguishes fixed trial controls from the story control set.");
    AssertContains(
        identitySource,
        "MizuchiConstants.IsActiveControlType(controlType)",
        "Story Mizuchi no longer requires one of the three exact active control types.");
    AssertNotContains(
        identitySource,
        "if (controlType != contract.ExpectedControlType",
        "The obsolete unconditional active-control invariant still rejects the native no-target state.");
    AssertNotContains(identitySource, "orderGuestId <= 0", "Guest ID 0 was restored as an invalid order identity.");
    AssertNotContains(identitySource, "selectedGuestId <= 0", "Guest ID 0 was restored as an invalid closure identity.");
    AssertNotContains(
        identitySource,
        "controlledGuestId != MizuchiConstants.NoControlledGuestId && controlledGuestId <= 0",
        "Guest ID 0 was restored as an invalid controlled identity.");
    AssertContains(orderMatching, "TryValidateMizuchiRolePair", "Request matching lacks exact Mizuchi role parity.");
    AssertContains(cooking, "created-food-before-deduction", "Cooking lacks pre-deduction Modifier validation.");
    AssertContains(cooking, "TryValidateCookingTargetOrderLifecycle", "Cooking lacks lifecycle checks around material deduction and SetCook.");
    AssertContains(cooking, "before-ingredient-deduction", "Cooking lacks the named pre-deduction role checkpoint.");
    AssertContains(cooking, "immediately-before-set-cook", "Cooking lacks the named pre-SetCook role checkpoint.");
    AssertContains(service, "TryValidateMizuchiCookingTargetFresh", "Cooking lifecycle validation does not fresh-read the Mizuchi role.");
    AssertContains(directDelivery, "before-beverage-setter", "Beverage delivery lacks an immediate Mizuchi recheck.");
    AssertContains(directDelivery, "immediately-before-food-setter", "Food delivery lacks an immediate Mizuchi recheck.");
    AssertContains(delivery, "TryValidateMizuchiEvaluationPreflight", "Evaluation lacks a Mizuchi contract preflight.");
    AssertContains(delivery, "fulfilledPreflight: fulfilledPreflight", "Mizuchi evaluation preflight is not attached to the fulfilled-only evaluation boundary.");
    var genericEvaluationStart = delivery.IndexOf(
        "private static RuntimeOrderEvaluationResult TryEvaluateRuntimeOrderIfReady(",
        StringComparison.Ordinal);
    var genericEvaluationEnd = delivery.IndexOf(
        "private static RuntimeOrderEvaluationResult TryInvokeRuntimeOrderEvaluationOnce(",
        genericEvaluationStart,
        StringComparison.Ordinal);
    AssertTrue(
        genericEvaluationStart >= 0 && genericEvaluationEnd > genericEvaluationStart,
        "The shared runtime evaluation method could not be isolated for source auditing.");
    var genericEvaluation = delivery[genericEvaluationStart..genericEvaluationEnd];
    var fulfilledReadIndex = genericEvaluation.IndexOf("get_IsFullfilled", StringComparison.Ordinal);
    var unfulfilledWaitIndex = genericEvaluation.IndexOf("if (!isFullfilled)", StringComparison.Ordinal);
    var fulfilledPreflightIndex = genericEvaluation.IndexOf("fulfilledPreflight?.Invoke()", StringComparison.Ordinal);
    var nativeEvaluationIndex = genericEvaluation.IndexOf("TryInvokeRuntimeOrderEvaluationOnce(", StringComparison.Ordinal);
    AssertTrue(
        fulfilledReadIndex >= 0
        && unfulfilledWaitIndex > fulfilledReadIndex
        && fulfilledPreflightIndex > unfulfilledWaitIndex
        && nativeEvaluationIndex > fulfilledPreflightIndex,
        "Mizuchi fulfilled preflight can run before the normal incomplete-order wait or after native evaluation starts.");
    AssertContains(automationPolicy, "TryValidateMizuchiFoodModifier", "Mizuchi policy lacks exact final Modifier validation.");
    AssertContains(modifierPolicy, "RuntimeConcreteCollectionReader.TryReadIntArray", "Shared Modifier reader is not using the exact concrete int-array path.");
    AssertContains(yuyukoPolicy, "TryValidateServedFoodExtraIngredients", "Yuyuko did not move to the shared strict Modifier reader.");
    AssertNotContains(yuyukoPolicy, "TryValidateYuyukoRetakeServedExtraIngredients", "Obsolete Yuyuko-only Modifier implementation remains.");
    AssertNotContains(automationPolicy, "FindUnity", "Mizuchi automation scans scene objects.");
    AssertNotContains(automationPolicy, "OverrideEvaluationCallback(", "Mizuchi automation directly invokes the evaluation callback.");
    AssertNotContains(service, "mizuchi-trial-contract-mismatch", "The obsolete trial-only safety code remains.");
    AssertContains(service, "mizuchi-contract-mismatch", "The shared Mizuchi safety code is missing.");
}

static MizuchiFixture CreateFixture(
    string challenge,
    int guestId,
    int controlledGuestId,
    DLC5_MizuchiChallengeBossData.MizuchiControlType control,
    int invocationCount = 1,
    string callbackMethod = "<MainChallengeLoop>g__GroupOverrideEvaluationCallback|74")
{
    if (!MizuchiConstants.TryGetChallengeContract(challenge, out var contract))
    {
        throw new InvalidOperationException($"Unsupported Mizuchi fixture challenge: {challenge}");
    }
    var guest = new SpecialGuest(guestId);
    var order = new SpecialOrder(guest);
    var controller = new SpecialGuestsController(guest);
    var parent = new DLC5_MizuchiChallengeBossData.__c__DisplayClass66_0
    {
        catchMizuchiNum = 2,
        currentGuestWhoIsControlledByMizuchi = controlledGuestId,
        typeOfMizuchi = control,
        targetIngredientId = contract.TargetIngredientId,
        isMizuchiChallenge = contract.IsBaseChallenge,
        needCatchMizuchiTime = 5,
    };
    var closure = new DLC5_MizuchiChallengeBossData.__c__DisplayClass66_9
    {
        selectedGuestGroup = guestId,
        group = controller,
        field_Public___c__DisplayClass66_0_0 = parent,
    };
    controller.OverrideEvaluationCallback = new GuestGroupController.OverrideEvalResultDelegate(
        closure,
        callbackMethod,
        invocationCount);
    return new MizuchiFixture(order, controller, closure, parent);
}

static SpecialBusinessOrderClassification Classify(string challenge, object? order, object? controller)
{
    return new MizuchiOrderModule().Classify(challenge, order, controller, "smoke");
}

static void AssertExpectedControl(string challenge, int expected)
{
    AssertTrue(MizuchiConstants.TryGetChallengeContract(challenge, out var contract), $"Control type was unavailable: {challenge}");
    AssertEqual<int?>(expected, contract.ExpectedControlType, $"Wrong control type: {challenge}");
}

static void AssertAllowedRole(SpecialBusinessOrderClassification result, string role, string message)
{
    AssertTrue(result.AutomationAllowed, $"{message} Reason: {result.AutomationBlockReason}");
    AssertEqual(role, result.Role, message);
    AssertTrue(result.RuntimeGuestId >= 0, "Allowed role lost its non-negative runtime guest identity.");
}

static void AssertBlocked(
    SpecialBusinessOrderClassification result,
    string reasonFragment,
    string expectedRole = SpecialBusinessOrderRoles.MizuchiTrialUnverified)
{
    AssertFalse(result.AutomationAllowed, "Invalid Mizuchi identity unexpectedly allowed automation.");
    AssertEqual(expectedRole, result.Role, "Invalid identity did not use the scene-specific unverified role.");
    AssertContains(result.AutomationBlockReason, reasonFragment, "Blocked result lost its exact failure reason.");
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

    throw new DirectoryNotFoundException("Repository root not found.");
}

static void AssertContains(string value, string fragment, string message)
{
    if (!value.Contains(fragment, StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"{message} Missing: {fragment}");
    }
}

static void AssertNotContains(string value, string fragment, string message)
{
    if (value.Contains(fragment, StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"{message} Unexpected: {fragment}");
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

static void AssertEqual<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{message} Expected={expected}; Actual={actual}");
    }
}

internal sealed record MizuchiFixture(
    SpecialOrder Order,
    SpecialGuestsController Controller,
    DLC5_MizuchiChallengeBossData.__c__DisplayClass66_9 Closure,
    DLC5_MizuchiChallengeBossData.__c__DisplayClass66_0 Parent);
