using System.Runtime.CompilerServices;
using HarmonyLib;
using MystiaStewardCompanion.Save;

try
{
    VerifyPlansAndIdentity();
    VerifyStableInsertionOrderAndExactClaims();
    VerifyApplicableRecipeFiltering();
    VerifyFreshCookCount();
    VerifyCookCountModesAndInsertionReadback();
    VerifyRetiredSurfaceRejectsBeforeNativeInsertion();
    VerifySubmitFirstSelectionTiming();
    VerifyTwoActivationCommitAndEpochTransfer();
    VerifyNativeRecipeSwitchReceipts();
    VerifyRecipeSwitchFailureAndOutputRetirement();
    VerifyOutputReadyEpochTransfer();
    VerifyFullVisualFailureRetiresOutputBeforeReads();
    VerifyNativeFullVisualExceptionRetiresOutputBeforeRecipePostfix();
    VerifyFullVisualPrefixDefersExactRecipeSwitch();
    VerifyFullVisualPrefixRejectsSwitchOwnershipDrift();
    VerifyFullVisualPrefixCarriesResetReceiptIntoRecipePostfix();
    VerifyNestedFullVisualScopesReleaseLifo();
    VerifyFullVisualResetReceiptRejectsFreshOutputAba();
    VerifyOutputBindingReentrancyAndAba();
    VerifySameActivationOutputClosure();
    VerifyExactOutputClosureGate();
    VerifyFinalOutputReceiptAndRetainedIdentity();
    VerifyPanelCloseTransactions();
    VerifyMutationContextTransitions();
    VerifySyntheticSourceIdentityAndDelayedRows();
    VerifyOutputClosureEntryExceptionCleanup();
    VerifyBusinessBoundaryTransactions();
    VerifyTransactionSequenceAndNestedProbeStack();
    VerifyOwnershipFailurePolicyAndCallbackCleanup();
    VerifyPostNativeOutputCallbackOwnership();
    VerifyRuntimeCallsStayOutsideServiceLock();
    VerifyUncertainMutationLatchAndNativeRows();
    VerifyOutputMismatchAndPoolReuse();
    VerifyReentrancyAndTransferFailure();
    VerifyPartialInsertionKeepsNativeBaseUsable();
    VerifyLogGenerationBudget();
    VerifyProductionSourceContract();
    HarmonyContractSmoke.Verify();
    Console.WriteLine("PASS: explicit target recipe variants route exact rows through one-shot native transactions.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL: {ex}");
    return 1;
}

static void VerifyPlansAndIdentity()
{
    var same = Set(
        1,
        Target(RuntimeUiTargetKind.Rare, 10, new[] { 1, 2, 3 }, new[] { 2, 3 }, "rare-a"),
        Target(RuntimeUiTargetKind.Normal, 10, new[] { 1, 2, 3 }, new[] { 2, 3 }, "normal-a"));
    var plans = RuntimeTargetRecipeVariantService.BuildPlansForTests(same);
    Equal(1, plans.Count, "same ordered extras were not coalesced");
    Equal(RuntimeUiTargetKinds.Rare | RuntimeUiTargetKinds.Normal, plans[0].Claims, "coalesced claims changed");
    Sequence(new[] { 2, 3 }, plans[0].ExtraIngredientIds, "ordered extras changed");
    Equal(64, plans[0].RevisionFingerprint.Length, "plan identity uses a truncated digest");
    True(plans[0].RevisionFingerprint.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'),
        "plan identity is not lowercase hexadecimal SHA-256");

    var different = RuntimeTargetRecipeVariantService.BuildPlansForTests(Set(
        2,
        Target(RuntimeUiTargetKind.Normal, 10, new[] { 1, 4 }, new[] { 4 }, "normal-b"),
        Target(RuntimeUiTargetKind.Rare, 10, new[] { 1, 2, 3 }, new[] { 2, 3 }, "rare-b")));
    Equal(2, different.Count, "different ordered extras were coalesced");
    Equal(RuntimeUiTargetKinds.Rare, different[0].Claims, "rare variant is not stable-first");
    Equal(RuntimeUiTargetKinds.Normal, different[1].Claims, "normal variant is not stable-second");

    Throws(() => RuntimeTargetRecipeVariantService.BuildPlansForTests(Set(
        3,
        Target(RuntimeUiTargetKind.Rare, 10, new[] { 1, 2 }, new[] { 2 }, "rare-c"),
        Target(RuntimeUiTargetKind.Normal, 10, new[] { 1, 2, 9 }, new[] { 2 }, "normal-c"))),
        "same variant with disagreeing target ingredient sets did not fail closed");
}

static void VerifyStableInsertionOrderAndExactClaims()
{
    var baseA = new FakeRecipe(0x101, 10, new[] { 1 }, 4);
    var baseB = new FakeRecipe(0x102, 20, new[] { 5 }, 3);
    var runtime = Install(baseA, baseB);
    var target = Set(
        4,
        Target(RuntimeUiTargetKind.Rare, 10, new[] { 1, 2 }, new[] { 2 }, "rare-d"),
        Target(RuntimeUiTargetKind.Normal, 10, new[] { 1, 3 }, new[] { 3 }, "normal-d"));
    Publish(target);
    True(RuntimeTargetRecipeVariantService.InjectForTests(runtime.Panel, target, 9), "variant insertion failed");
    Sequence(new[] { baseA.Pointer, runtime.Created[0].Pointer, runtime.Created[1].Pointer, baseB.Pointer },
        runtime.Recipes.Select(recipe => recipe.Pointer), "multi-plan rows were not adjacent to their base");
    Sequence(new[] { 1, 2 }, runtime.Created[0].Ingredients, "rare synthetic ingredients changed");
    Sequence(new[] { 1, 3 }, runtime.Created[1].Ingredients, "normal synthetic ingredients changed");

    var baseButton = new FakeButton(0x201);
    var rareButton = new FakeButton(0x202);
    var normalButton = new FakeButton(0x203);
    Bind(runtime, baseA, baseButton);
    Bind(runtime, runtime.Created[0], rareButton);
    Bind(runtime, runtime.Created[1], normalButton);
    True(RuntimeTargetRecipeVariantService.TryResolveRecipeRowClaims(
        runtime.Panel, baseA, baseButton, out var baseClaims, out var baseLease), "base exact row did not resolve");
    Equal(RuntimeUiTargetKinds.None, baseClaims, "variant-only base row gained aggregate claims");
    True(RuntimeTargetRecipeVariantService.TryValidateRecipeRowClaims(baseLease, out _), "fresh base lease failed");
    True(RuntimeTargetRecipeVariantService.TryResolveRecipeRowClaims(
        runtime.Panel, runtime.Created[0], rareButton, out var rareClaims, out _), "rare row did not resolve");
    Equal(RuntimeUiTargetKinds.Rare, rareClaims, "rare exact claim changed");
    True(RuntimeTargetRecipeVariantService.TryResolveRecipeRowClaims(
        runtime.Panel, runtime.Created[1], normalButton, out var normalClaims, out _), "normal row did not resolve");
    Equal(RuntimeUiTargetKinds.Normal, normalClaims, "normal exact claim changed");

    var neighborRuntime = Install(
        new FakeRecipe(0x111, 11, new[] { 1 }, 2),
        new FakeRecipe(0x112, 12, new[] { 4 }, 2),
        new FakeRecipe(0x113, 13, new[] { 7 }, 2));
    var neighborTarget = Set(
        5,
        Target(RuntimeUiTargetKind.Rare, 11, new[] { 1, 2 }, new[] { 2 }, "rare-e"),
        Target(RuntimeUiTargetKind.Normal, 12, new[] { 4, 5 }, new[] { 5 }, "normal-e"));
    Publish(neighborTarget);
    True(RuntimeTargetRecipeVariantService.InjectForTests(neighborRuntime.Panel, neighborTarget, 9),
        "adjacent-base insertion failed");
    Sequence(new[] { 11, 11, 12, 12, 13 }, neighborRuntime.Recipes.Select(recipe => recipe.Id),
        "descending group insertion split an adjacent base/variant pair");
}

static void VerifyApplicableRecipeFiltering()
{
    var applicableBase = new FakeRecipe(0x114, 20, new[] { 5 }, 3);
    var mixed = Install(applicableBase);
    mixed.Inventory[5] = 8;
    mixed.Inventory[6] = 8;
    var mixedTarget = Set(
        51,
        Target(RuntimeUiTargetKind.Rare, 10, new[] { 1, 2 }, new[] { 2 }, "missing-cooker-recipe"),
        Target(RuntimeUiTargetKind.Normal, 20, new[] { 5, 6 }, new[] { 6 }, "applicable-cooker-recipe"));
    Publish(mixedTarget);
    True(RuntimeTargetRecipeVariantService.InjectForTests(mixed.Panel, mixedTarget, 9),
        "a non-applicable target prevented an applicable recipe variant from being injected");
    Equal(1, mixed.InsertCalls, "mixed applicability inserted the wrong number of variants");
    Equal(1, mixed.Created.Count, "mixed applicability created a row for a missing recipe");
    Equal(20, mixed.Created.Single().Id, "mixed applicability injected the missing target recipe");
    Sequence(new[] { 5, 6 }, mixed.Created.Single().Ingredients,
        "the applicable recipe variant changed its exact ingredients");
    var mixedButton = new FakeButton(0x214);
    Bind(mixed, mixed.Created.Single(), mixedButton);
    True(RuntimeTargetRecipeVariantService.TryResolveRecipeRowClaims(
        mixed.Panel,
        mixed.Created.Single(),
        mixedButton,
        out var mixedClaims,
        out _), "the applicable recipe variant did not publish an exact row lease");
    Equal(RuntimeUiTargetKinds.Normal, mixedClaims,
        "the applicable recipe variant inherited the missing target's claim");
    Equal(0L, StatusCount("warningLogs"),
        "a normal non-applicable cooker target consumed the warning budget");
    Equal(0L, StatusCount("safety"),
        "a normal non-applicable cooker target consumed the safety-event reserve");

    var absent = Install(new FakeRecipe(0x115, 99, new[] { 9 }, 2));
    var absentTarget = Set(
        52,
        Target(RuntimeUiTargetKind.Rare, 10, new[] { 1, 2 }, new[] { 2 }, "all-missing"));
    Publish(absentTarget);
    absent.ThrowPanelSelectionState = true;
    True(RuntimeTargetRecipeVariantService.InjectForTests(absent.Panel, absentTarget, 9),
        "a cooker with no applicable targets was treated as a failed injection");
    absent.ThrowPanelSelectionState = false;
    Equal(0, absent.InsertCalls, "an all-non-applicable cooker received a synthetic row");
    Equal(0, absent.Created.Count, "an all-non-applicable cooker allocated a synthetic Recipe");
    Equal(0L, StatusCount("failures"), "an all-non-applicable cooker counted as a failure");
    Equal(0L, StatusCount("warningLogs"),
        "an all-non-applicable cooker consumed the warning budget");
    Equal(0L, StatusCount("safety"),
        "an all-non-applicable cooker consumed the safety-event reserve");
    var nativeButton = new FakeButton(0x215);
    True(RuntimeTargetRecipeVariantService.PrepareRecipeElementEnableForTests(
        absent.Panel,
        absent.Authoritative.Single(),
        nativeButton,
        out var nativeEnable), "an unrelated native recipe row was blocked");
    RuntimeTargetRecipeVariantService.CompleteRecipeElementEnableForTests(nativeEnable);
    True(RuntimeTargetRecipeVariantService.BeginSubmitForTests(nativeButton, out var nativeSubmit),
        "an all-non-applicable target disabled the ordinary native recipe path");
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(nativeSubmit);

    var duplicate = Install(
        new FakeRecipe(0x116, 21, new[] { 7 }, 2),
        new FakeRecipe(0x117, 21, new[] { 7 }, 2));
    duplicate.Inventory[7] = 8;
    duplicate.Inventory[8] = 8;
    var duplicateTarget = Set(
        53,
        Target(RuntimeUiTargetKind.Rare, 21, new[] { 7, 8 }, new[] { 8 }, "duplicate-authoritative"));
    Publish(duplicateTarget);
    False(RuntimeTargetRecipeVariantService.InjectForTests(duplicate.Panel, duplicateTarget, 9),
        "duplicate authoritative recipe rows were treated as a cooker mismatch");
    Equal(0, duplicate.InsertCalls, "duplicate authoritative rows were detected after native Insert");
    Equal(0, duplicate.Created.Count, "duplicate authoritative rows allocated a synthetic Recipe");
    Equal(1L, StatusCount("failures"), "duplicate authoritative rows did not fail closed exactly once");
}

static void VerifyFreshCookCount()
{
    Equal(2, RuntimeTargetRecipeVariantService.CalculateCookCountForTests(
        new[] { 1, 1, 2 }, new[] { 1 }, id => id == 1 ? 3 : 8),
        "duplicate ingredient multiplicity or selected return changed");
    Equal(-1, RuntimeTargetRecipeVariantService.CalculateCookCountForTests(
        new[] { 1, 2 }, Array.Empty<int>(), _ => -1), "all-infinite ingredients lost -1");
    Equal(0, RuntimeTargetRecipeVariantService.CalculateCookCountForTests(
        new[] { 1, 2 }, Array.Empty<int>(), id => id == 1 ? 0 : 10), "missing finite ingredient did not yield zero");
    Throws(() => RuntimeTargetRecipeVariantService.CalculateCookCountForTests(
        new[] { 1 }, Array.Empty<int>(), _ => -2), "invalid inventory sentinel was accepted");

    var runtime = Install(new FakeRecipe(0x121, 30, new[] { 1 }, 99));
    runtime.Inventory[1] = 4;
    runtime.Inventory[2] = 2;
    var target = Set(6, Target(RuntimeUiTargetKind.Rare, 30, new[] { 1, 2 }, new[] { 2 }, "rare-f"));
    Publish(target);
    True(RuntimeTargetRecipeVariantService.InjectForTests(runtime.Panel, target, 9), "cook-count setup failed");
    var synthetic = runtime.Created.Single();
    Equal(2, synthetic.CookCount, "synthetic creation used copied base CookCount");
    runtime.Inventory[2] = 1;
    Bind(runtime, synthetic, new FakeButton(0x221));
    Equal(1, synthetic.CookCount, "enabled row did not fresh-recompute its own CookCount");
    Equal(99, runtime.Recipes[0].CookCount, "synthetic CookCount write modified the shared base Recipe");
}

static void VerifyCookCountModesAndInsertionReadback()
{
    var multiplier = Install(new FakeRecipe(0x122, 31, new[] { 1 }, 9));
    multiplier.Panel.ExtraCostIngredient = 2;
    multiplier.Inventory[1] = 3;
    multiplier.Inventory[2] = 5;
    var multiplierTarget = Set(62,
        Target(RuntimeUiTargetKind.Rare, 31, new[] { 1, 2 }, new[] { 2 }, "multiplier"));
    Publish(multiplierTarget);
    True(RuntimeTargetRecipeVariantService.InjectForTests(multiplier.Panel, multiplierTarget, 9),
        "multiplier cook-count insertion failed");
    Equal(1, multiplier.Created.Single().CookCount,
        "synthetic CookCount ignored the native extra-cost multiplier");

    var free = Install(new FakeRecipe(0x123, 32, new[] { 1 }, 9));
    free.Panel.IsFreeCook = true;
    free.Inventory[1] = -2;
    free.Inventory[2] = -2;
    var freeTarget = Set(63,
        Target(RuntimeUiTargetKind.Rare, 32, new[] { 1, 2 }, new[] { 2 }, "free"));
    Publish(freeTarget);
    True(RuntimeTargetRecipeVariantService.InjectForTests(free.Panel, freeTarget, 9),
        "free-cook insertion failed");
    Equal(-1, free.Created.Single().CookCount, "free cook did not publish infinite CookCount");
    Equal(0, free.InventoryReadCalls, "free cook queried inventory while creating its row");
    Bind(free, free.Created.Single(), new FakeButton(0x223));
    Equal(0, free.InventoryReadCalls, "free cook queried inventory while refreshing its row");

    foreach (var configure in new Action<FakeRuntime>[]
    {
        runtime => runtime.FailReadbackAfterInsert = true,
        runtime => runtime.ReturnDifferentRecipeListAfterInsert = true,
        runtime => runtime.CorruptReadbackOrder = true,
        runtime => runtime.CorruptReadbackRecipeIdentity = true,
    })
    {
        var runtime = Install(new FakeRecipe(0x124, 33, new[] { 1 }, 2));
        runtime.Inventory[1] = 4;
        runtime.Inventory[2] = 4;
        configure(runtime);
        var target = Set(64,
            Target(RuntimeUiTargetKind.Rare, 33, new[] { 1, 2 }, new[] { 2 }, "readback"));
        Publish(target);
        False(RuntimeTargetRecipeVariantService.InjectForTests(runtime.Panel, target, 9),
            "post-Insert identity/readback drift was accepted");
        Equal(1, runtime.InsertCalls, "readback failure did not occur after exactly one native Insert");
    }

    var ledger = Install(new FakeRecipe(0x125, 34, new[] { 1 }, 2));
    ledger.Inventory[1] = 4;
    ledger.Inventory[2] = 4;
    ledger.FailReadbackAfterInsert = true;
    var ledgerTarget = Set(65,
        Target(RuntimeUiTargetKind.Rare, 34, new[] { 1, 2 }, new[] { 2 }, "ledger-a"));
    Publish(ledgerTarget);
    False(RuntimeTargetRecipeVariantService.InjectForTests(ledger.Panel, ledgerTarget, 9),
        "ledger setup did not fail uncertain after Insert");
    RuntimeTargetRecipeVariantService.RetireForBusinessBoundary(
        9,
        "test insertion-ledger boundary");
    ledger.ResetRecipeListToAuthoritative();
    ledger.FailReadbackAfterInsert = false;
    False(RuntimeTargetRecipeVariantService.InjectForTests(ledger.Panel, ledgerTarget, 9),
        "panel destroy cleared the business/target insertion ledger");
    Equal(0, ledger.InsertCalls, "blocked ledger replay performed another native Insert");
    var nextTarget = Set(66,
        Target(RuntimeUiTargetKind.Rare, 34, new[] { 1, 2 }, new[] { 2 }, "ledger-b"));
    Publish(nextTarget);
    True(RuntimeTargetRecipeVariantService.InjectForTests(ledger.Panel, nextTarget, 9),
        "a new target generation did not open a new insertion attempt");

    var maxRecipes = Enumerable.Range(0, 512)
        .Select(index => new FakeRecipe((nint)(0x2000 + index), 1000 + index, new[] { 1 }, 1))
        .ToArray();
    var maximum = Install(maxRecipes);
    maximum.Inventory[1] = 100;
    maximum.Inventory[2] = 100;
    var maximumTarget = Set(67,
        Target(RuntimeUiTargetKind.Rare, 1000, new[] { 1, 2 }, new[] { 2 }, "maximum"));
    Publish(maximumTarget);
    False(RuntimeTargetRecipeVariantService.InjectForTests(maximum.Panel, maximumTarget, 9),
        "recipe-list capacity overflow was accepted");
    Equal(0, maximum.InsertCalls, "recipe-list capacity was checked after native Insert");
}

static void VerifySubmitFirstSelectionTiming()
{
    var runtime = Install(new FakeRecipe(0x129, 35, new[] { 1 }, 4));
    runtime.Inventory[1] = 9;
    runtime.Inventory[2] = 9;
    runtime.Inventory[3] = 9;
    var target = Set(
        61,
        Target(RuntimeUiTargetKind.Rare, 35, new[] { 1, 2 }, new[] { 2 }, "rare-first"),
        Target(RuntimeUiTargetKind.Normal, 35, new[] { 1, 3 }, new[] { 3 }, "normal-first"));
    Publish(target);
    True(RuntimeTargetRecipeVariantService.InjectForTests(runtime.Panel, target, 9),
        "submit-first setup failed");
    var rare = runtime.Created[0];
    var normal = runtime.Created[1];
    var rareButton = new FakeButton(0x229);
    var normalButton = new FakeButton(0x22a);
    Bind(runtime, rare, rareButton);
    Bind(runtime, normal, normalButton);

    True(RuntimeTargetRecipeVariantService.BeginSubmitForTests(rareButton, out var first),
        "CallSubmit prefix could not establish a transaction before native selection");
    object nestedSame = rare;
    True(RuntimeTargetRecipeVariantService.RouteRecipeSelectionForTests(
        runtime.Panel, ref nestedSame, rareButton), "nested same selection did not reuse active identity");
    Same(runtime.Authoritative.Single(), nestedSame,
        "nested same selection did not receive the authoritative Recipe");
    object nestedDifferent = normal;
    False(RuntimeTargetRecipeVariantService.RouteRecipeSelectionForTests(
        runtime.Panel, ref nestedDifferent, normalButton),
        "nested different plan replaced the active transaction");
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(first);
    Equal(0, runtime.DebitCalls, "unconsumed submit-first activation mutated extras");

    True(RuntimeTargetRecipeVariantService.BeginSubmitForTests(rareButton, out var second),
        "second activation did not reuse the pending submit-first transaction");
    object nestedAgain = rare;
    True(RuntimeTargetRecipeVariantService.RouteRecipeSelectionForTests(
        runtime.Panel, ref nestedAgain, rareButton), "second activation could not reuse exact identity");
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(second);
}

static void VerifyRetiredSurfaceRejectsBeforeNativeInsertion()
{
    var prepared = PrepareMutatedTransaction(
        0x126,
        341,
        68,
        "applied");
    RuntimeTargetRecipeVariantService.RetireForShutdown(
        "test shutdown race before surface refresh");
    prepared.Runtime.ResetRecipeListToAuthoritative();

    False(RuntimeTargetRecipeVariantService.InjectForTests(
            prepared.Runtime.Panel,
            prepared.Target,
            9,
            RuntimeTargetRecipeVariantService.RecipeSurfaceRefreshKind.FullVisual),
        "A retired mutated panel was revived by a FullVisual refresh.");
    Equal(0, prepared.Runtime.InsertCalls,
        "The first retired-panel rejection reached native List.Insert.");
    Equal(0, prepared.Runtime.Created.Count,
        "The first retired-panel rejection allocated a synthetic Recipe.");
    Equal("Uncertain", ReadProperty(
        ReadCurrentTransaction(prepared.Runtime.Panel), "State")!.ToString()!,
        "A retired mutated transaction was not kept fail-closed.");

    prepared.Runtime.ResetRecipeListToAuthoritative();
    False(RuntimeTargetRecipeVariantService.InjectForTests(
            prepared.Runtime.Panel,
            prepared.Target,
            9,
            RuntimeTargetRecipeVariantService.RecipeSurfaceRefreshKind.DirectRecipeField),
        "A later direct refresh revived the retained retired panel.");
    Equal(0, prepared.Runtime.InsertCalls,
        "A retained retired panel was rejected only after native List.Insert.");
    Equal(0, prepared.Runtime.Created.Count,
        "A retained retired panel allocated a synthetic Recipe before rejection.");
    True(RuntimeTargetRecipeVariantService.Status.Contains(
            "pre-insert-retired-rejected",
            StringComparison.Ordinal),
        "The pre-insert retired-panel rejection is not distinguishable in diagnostics.");
}

static void VerifyTwoActivationCommitAndEpochTransfer()
{
    var runtime = Install(new FakeRecipe(0x131, 40, new[] { 1 }, 6));
    runtime.Inventory[1] = 8;
    runtime.Inventory[2] = 5;
    var target = Set(7, Target(RuntimeUiTargetKind.Rare, 40, new[] { 1, 2 }, new[] { 2 }, "rare-g"));
    Publish(target);
    True(RuntimeTargetRecipeVariantService.InjectForTests(runtime.Panel, target, 9), "transaction setup failed");
    var synthetic = runtime.Created.Single();
    var syntheticButton = new FakeButton(0x231);
    Bind(runtime, synthetic, syntheticButton);
    object routed = synthetic;
    True(RuntimeTargetRecipeVariantService.RouteRecipeSelectionForTests(runtime.Panel, ref routed, syntheticButton),
        "synthetic selection was blocked");
    Same(runtime.Recipes[0], routed, "synthetic selection did not route to the fresh authoritative wrapper");

    runtime.ThrowPanelCookingState = true;
    True(RuntimeTargetRecipeVariantService.BeginSubmitForTests(syntheticButton, out var first),
        "first recipe activation depended on the one-shot imported Recipe state");
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(first);
    Equal(0, runtime.DebitCalls, "an unconsumed first activation mutated inventory");
    True(RuntimeTargetRecipeVariantService.BeginSubmitForTests(syntheticButton, out var second),
        "second activation could not consume the armed callback");
    False(RuntimeTargetRecipeVariantService.BeginSubmitForTests(syntheticButton, out var nested),
        "nested exact submit was allowed while a publication lease was active");
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(nested);

    runtime.ThrowPanelCookingState = false;
    runtime.NativeImportBase(runtime.Recipes[0]);
    RuntimeTargetRecipeVariantService.ApplyExtrasForTests(runtime.Panel, out var visual);
    Equal(1, runtime.DebitCalls, "extras were not debited exactly once");
    Equal(1, runtime.AddRangeCalls, "extras were not added with exactly one AddRange");
    Sequence(new[] { 1, 2 }, runtime.Panel.Selected.Ids, "selected ingredients do not match base+extras");

    // MatchingSelected has consumed importedRecipe by this point on the real runtime,
    // while selectedIngredients still carries the committed base+extras transaction.
    runtime.Panel.HasImported = true;
    runtime.Panel.Imported = null;
    runtime.ThrowPanelCookingState = true;
    runtime.ResetRecipeListToAuthoritative();
    True(RuntimeTargetRecipeVariantService.InjectForTests(
        runtime.Panel,
        target,
        9,
        RuntimeTargetRecipeVariantService.RecipeSurfaceRefreshKind.FullVisual),
        "E1 to E2 list rebuild depended on the consumed imported Recipe");
    RuntimeTargetRecipeVariantService.CompleteUpdateVisualForTests(visual);
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(second);
    var combo = runtime.Combo(0x331, runtime.Authoritative.Single(), new[] { 2 });
    var outputButton = new FakeButton(0x232);
    True(RuntimeTargetRecipeVariantService.BeginSubmitForTests(outputButton, out var firstOutputActivation),
        "first unbound output activation was blocked");
    True(RuntimeTargetRecipeVariantService.PrepareOutputSelectionForTests(
        runtime.Panel, combo, outputButton, out var outputState), "exact output was blocked after E2 transfer");
    var outputClosure = runtime.RegisterOutputClosure(outputButton, combo, 0x431);
    RuntimeTargetRecipeVariantService.CompleteOutputSelectionForTests(outputState, outputButton);
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(firstOutputActivation);
    True(RuntimeTargetRecipeVariantService.BeginSubmitForTests(outputButton, out var secondOutputActivation),
        "second output activation could not enter the exact button probe");
    True(RuntimeTargetRecipeVariantService.BeginOutputClosureForTests(outputClosure, out var closureState),
        "exact output closure depended on the consumed imported Recipe");
    var close = RuntimeTargetRecipeVariantService.BeginPanelTeardownForTests(runtime.Panel);
    RuntimeTargetRecipeVariantService.CompletePanelCloseForTests(close);
    RuntimeTargetRecipeVariantService.CompleteOutputClosureForTests(closureState);
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(secondOutputActivation);
    Equal(1, runtime.DebitCalls, "final output replayed the extras debit");
    Equal(1, runtime.AddRangeCalls, "final output replayed AddRange");
}

static void VerifyOutputReadyEpochTransfer()
{
    var ready = ReadyOutputClosure(0x135, 405, 0x435);
    var target = RuntimeUiPinningService.Current;
    var priorTransaction = ReadCurrentTransaction(ready.Runtime.Panel);
    var priorSequence = (long)ReadProperty(priorTransaction, "Sequence")!;
    var priorEpoch = (long)ReadProperty(priorTransaction, "PanelEpoch")!;
    Equal("OutputReady", ReadProperty(priorTransaction, "State")!.ToString()!,
        "output-ready epoch setup did not reach the stable state");

    ready.Runtime.ResetRecipeListToAuthoritative();
    True(RuntimeTargetRecipeVariantService.InjectForTests(
        ready.Runtime.Panel,
        target,
        9,
        RuntimeTargetRecipeVariantService.RecipeSurfaceRefreshKind.FullVisual),
        "stable OutputReady did not transfer to a fresh recipe-list epoch");
    var transferred = ReadCurrentTransaction(ready.Runtime.Panel);
    Same(priorTransaction, transferred, "epoch transfer replaced the committed transaction identity");
    Equal(priorSequence, (long)ReadProperty(transferred, "Sequence")!,
        "epoch transfer changed the transaction sequence");
    True((long)ReadProperty(transferred, "PanelEpoch")! > priorEpoch,
        "epoch transfer did not advance the panel epoch");
    Equal("Applied", ReadProperty(transferred, "State")!.ToString()!,
        "epoch-local output binding was retained after recipe-list rebuild");
    foreach (var property in new[]
    {
        "OutputButtonPointer",
        "OutputComboPointer",
        "OutputClosurePointer",
        "OutputPointer",
    })
    {
        Equal((nint)0, (nint)ReadProperty(transferred, property)!,
            $"epoch transfer retained stale {property}");
    }
    Equal(0L, (long)ReadProperty(transferred, "OutputPanelEpoch")!,
        "epoch transfer retained stale OutputPanelEpoch");
    True(RuntimeTargetRecipeVariantService.BeginSubmitForTests(ready.Button, out var staleSubmit),
        "pooled output button could not enter the callback-clean rebind probe after epoch transfer");
    False(ready.Button.HasSubmitCallback,
        "epoch-transfer rebind probe retained the old final output callback");
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(staleSubmit);
    False(RuntimeTargetRecipeVariantService.BeginOutputClosureForTests(
        ready.Closure, out var staleClosure),
        "old output closure remained executable after epoch transfer");
    RuntimeTargetRecipeVariantService.CompleteOutputClosureForTests(staleClosure);
    Equal(0L, StatusCount("uncertain"), "safe output epoch reset latched Uncertain");

    var combo = ready.Runtime.Combo(0x335, ready.Runtime.Authoritative.Single(), new[] { 2 });
    var button = new FakeButton(0x835);
    True(RuntimeTargetRecipeVariantService.BeginSubmitForTests(button, out var firstSubmit),
        "fresh output button could not establish its first probe");
    True(RuntimeTargetRecipeVariantService.PrepareOutputSelectionForTests(
        ready.Runtime.Panel, combo, button, out var outputState),
        "fresh exact output combo was blocked after epoch reset");
    var closure = ready.Runtime.RegisterOutputClosure(button, combo, 0x535);
    True(RuntimeTargetRecipeVariantService.CompleteOutputSelectionForTests(
        outputState, button) == null,
        "fresh exact output callback was not registered after epoch reset");
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(firstSubmit);

    True(RuntimeTargetRecipeVariantService.BeginSubmitForTests(button, out var finalSubmit),
        "fresh exact output button could not enter final submit");
    True(RuntimeTargetRecipeVariantService.BeginOutputClosureForTests(
        closure, out var finalClosure),
        "fresh exact output closure was blocked after epoch reset");
    var close = RuntimeTargetRecipeVariantService.BeginPanelTeardownForTests(ready.Runtime.Panel);
    ready.Runtime.NativeClosePanel();
    RuntimeTargetRecipeVariantService.CompletePanelCloseForTests(close);
    RuntimeTargetRecipeVariantService.CompleteOutputClosureForTests(finalClosure);
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(finalSubmit);
    Equal(1L, StatusCount("completed"), "fresh output did not complete after epoch reset");
    Equal(0L, StatusCount("uncertain"), "fresh output completion became Uncertain");
    Equal(1, ready.Runtime.DebitCalls, "epoch reset replayed the extras debit");
    Equal(1, ready.Runtime.AddRangeCalls, "epoch reset replayed selected extras");

    var failed = ReadyOutputClosure(0x13a, 409, 0x43a);
    var failedTarget = RuntimeUiPinningService.Current;
    failed.Runtime.ResetRecipeListToAuthoritative();
    failed.Runtime.ThrowAfterInsert = true;
    False(RuntimeTargetRecipeVariantService.InjectForTests(
        failed.Runtime.Panel,
        failedTarget,
        9,
        RuntimeTargetRecipeVariantService.RecipeSurfaceRefreshKind.FullVisual),
        "failed OutputReady epoch transfer was accepted");
    False(RuntimeTargetRecipeVariantService.BeginSubmitForTests(
        failed.Button, out var failedRebind),
        "failed FullVisual insertion left its retired output button rebindable");
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(failedRebind);
    Equal("Applied", ReadProperty(
        ReadCurrentTransaction(failed.Runtime.Panel), "State")!.ToString()!,
        "failed recipe-surface insertion polluted the confirmed material transaction");
    Equal(0L, StatusCount("uncertain"),
        "failed recipe-surface insertion latched the confirmed material transaction Uncertain");
    True(RuntimeTargetRecipeVariantService.Status.Contains("mutationLatch=9:False", StringComparison.Ordinal),
        "failed recipe-surface insertion polluted the business mutation latch");
}

static void VerifyFullVisualFailureRetiresOutputBeforeReads()
{
    VerifyFailure(
        0x13b,
        419,
        0x43b,
        runtime => runtime.FailRecipeListRead = true,
        "recipe-list read failure");
    VerifyFailure(
        0x13c,
        420,
        0x43c,
        runtime => runtime.ThrowPanelSelectionState = true,
        "selection-state read failure");

    static void VerifyFailure(
        nint basePointer,
        int recipeId,
        nint closurePointer,
        Action<FakeRuntime> armFailure,
        string scenario)
    {
        var ready = ReadyOutputClosure(basePointer, recipeId, closurePointer);
        var target = RuntimeUiPinningService.Current;
        var transaction = ReadCurrentTransaction(ready.Runtime.Panel);
        var debitCalls = ready.Runtime.DebitCalls;
        var addRangeCalls = ready.Runtime.AddRangeCalls;
        var selected = ready.Runtime.Panel.Selected.Ids.ToArray();
        armFailure(ready.Runtime);
        ready.Runtime.ResetRecipeListToAuthoritative();

        False(RuntimeTargetRecipeVariantService.InjectForTests(
            ready.Runtime.Panel,
            target,
            9,
            RuntimeTargetRecipeVariantService.RecipeSurfaceRefreshKind.FullVisual),
            $"full visual {scenario} was accepted");
        Same(transaction, ReadCurrentTransaction(ready.Runtime.Panel),
            $"full visual {scenario} replaced the confirmed material transaction");
        Equal("Applied", ReadProperty(transaction, "State")!.ToString()!,
            $"full visual {scenario} retained a stale OutputReady state");
        foreach (var property in new[]
        {
            "OutputButtonPointer",
            "OutputComboPointer",
            "OutputClosurePointer",
            "OutputPointer",
        })
        {
            Equal((nint)0, (nint)ReadProperty(transaction, property)!,
                $"full visual {scenario} retained stale {property}");
        }
        Equal(0L, (long)ReadProperty(transaction, "OutputPanelEpoch")!,
            $"full visual {scenario} retained a stale output epoch");
        False(RuntimeTargetRecipeVariantService.BeginOutputClosureForTests(
            ready.Closure,
            out var staleClosure),
            $"full visual {scenario} left the old output closure executable");
        RuntimeTargetRecipeVariantService.CompleteOutputClosureForTests(staleClosure);
        Equal(debitCalls, ready.Runtime.DebitCalls,
            $"full visual {scenario} replayed the extras debit");
        Equal(addRangeCalls, ready.Runtime.AddRangeCalls,
            $"full visual {scenario} replayed the selected-list write");
        Sequence(selected, ready.Runtime.Panel.Selected.Ids,
            $"full visual {scenario} changed the confirmed material receipt");
        Equal(0L, StatusCount("uncertain"),
            $"full visual {scenario} polluted the confirmed material transaction");
        True(RuntimeTargetRecipeVariantService.Status.Contains(
                "mutationLatch=9:False",
                StringComparison.Ordinal),
            $"full visual {scenario} polluted the business mutation latch");
    }
}

static void VerifyNativeFullVisualExceptionRetiresOutputBeforeRecipePostfix()
{
    const string harmonyId =
        "mystia-steward-companion.tests.runtime-target-recipe-variant.full-visual-exception";
    var ready = ReadyOutputClosure(0x13d, 421, 0x43d);
    var transaction = ReadCurrentTransaction(ready.Runtime.Panel);
    var serviceType = typeof(RuntimeTargetRecipeVariantService);
    var privateStatic = System.Reflection.BindingFlags.NonPublic
        | System.Reflection.BindingFlags.Static;
    var beforeFullVisual = serviceType.GetMethod(
        "BeforeUpdateAllVisual",
        privateStatic) ?? throw new InvalidOperationException(
            "BeforeUpdateAllVisual patch method is missing");
    var afterFullVisual = serviceType.GetMethod(
        "AfterUpdateAllVisual",
        privateStatic) ?? throw new InvalidOperationException(
            "AfterUpdateAllVisual patch method is missing");
    var afterRecipeField = serviceType.GetMethod(
        "AfterUpdateRecipeField",
        privateStatic) ?? throw new InvalidOperationException(
            "AfterUpdateRecipeField patch method is missing");
    var fullVisual = typeof(FakePanel).GetMethod(
        nameof(FakePanel.ThrowingUpdateAllVisual))
        ?? throw new InvalidOperationException("throwing FullVisual probe is missing");
    var recipeField = typeof(FakePanel).GetMethod(
        nameof(FakePanel.ThrowingUpdateRecipeField))
        ?? throw new InvalidOperationException("throwing recipe-field probe is missing");
    var depthField = serviceType.GetField("_updateAllVisualDepth", privateStatic)
        ?? throw new InvalidOperationException("FullVisual depth field is missing");
    var harmony = new Harmony(harmonyId);

    try
    {
        harmony.Patch(
            fullVisual,
            prefix: new HarmonyMethod(beforeFullVisual) { priority = Priority.First },
            finalizer: new HarmonyMethod(afterFullVisual) { priority = Priority.Last });
        harmony.Patch(
            recipeField,
            postfix: new HarmonyMethod(afterRecipeField) { priority = Priority.Last });

        Exception? observed = null;
        try
        {
            ready.Runtime.Panel.ThrowingUpdateAllVisual();
        }
        catch (Exception ex)
        {
            observed = ex;
        }

        Same(FakePanel.ExpectedRecipeFieldException, observed!,
            "FullVisual finalizer replaced the nested native recipe-field exception");
        Equal(1, ready.Runtime.Panel.FullVisualCalls,
            "throwing FullVisual original did not run exactly once");
        Equal(1, ready.Runtime.Panel.RecipeFieldCalls,
            "nested throwing recipe field did not run exactly once");
        Same(transaction, ReadCurrentTransaction(ready.Runtime.Panel),
            "native FullVisual exception replaced the confirmed material transaction");
        Equal("Applied", ReadProperty(transaction, "State")!.ToString()!,
            "native FullVisual exception retained the stale OutputReady state");
        foreach (var property in new[]
        {
            "OutputButtonPointer",
            "OutputComboPointer",
            "OutputClosurePointer",
            "OutputPointer",
        })
        {
            Equal((nint)0, (nint)ReadProperty(transaction, property)!,
                $"native FullVisual exception retained stale {property}");
        }
        Equal(0L, (long)ReadProperty(transaction, "OutputPanelEpoch")!,
            "native FullVisual exception retained a stale output epoch");
        False(RuntimeTargetRecipeVariantService.BeginOutputClosureForTests(
            ready.Closure,
            out var staleClosure),
            "native FullVisual exception left the old output closure executable");
        RuntimeTargetRecipeVariantService.CompleteOutputClosureForTests(staleClosure);

        True(RuntimeTargetRecipeVariantService.BeginSubmitForTests(
            ready.Button,
            out var staleButtonSubmit),
            "retired output button could not clear its stale native callback");
        False(ready.Button.HasSubmitCallback,
            "retired output button kept the stale native callback executable");
        RuntimeTargetRecipeVariantService.CompleteSubmitForTests(staleButtonSubmit);
        Equal(0, (int)depthField.GetValue(null)!,
            "FullVisual exception leaked the nested refresh classification depth");
        Equal(0L, StatusCount("uncertain"),
            "pure native FullVisual failure polluted the confirmed material receipt");
        Equal(1, ready.Runtime.DebitCalls,
            "pure native FullVisual failure replayed the extras debit");
        Equal(1, ready.Runtime.AddRangeCalls,
            "pure native FullVisual failure replayed the selected-list write");
        True(RuntimeTargetRecipeVariantService.Status.Contains(
                "mutationLatch=9:False",
                StringComparison.Ordinal),
            "pure native FullVisual failure polluted the business mutation latch");
    }
    finally
    {
        harmony.UnpatchSelf();
    }
}

static void VerifyFullVisualPrefixDefersExactRecipeSwitch()
{
    const string harmonyId =
        "mystia-steward-companion.tests.runtime-target-recipe-variant.full-visual-switch";
    var ready = ReadyOutputClosure(0x13e, 422, 0x43e);
    var baseButton = new FakeButton(0x53e);
    Bind(ready.Runtime, ready.Runtime.Authoritative.Single(), baseButton);
    True(RuntimeTargetRecipeVariantService.BeginSubmitForTests(baseButton, out var switchSubmit),
        "OutputReady prefix-switch outer submit was blocked");
    object baseRecipe = ready.Runtime.Authoritative.Single();
    True(RuntimeTargetRecipeVariantService.PrepareRecipeSelectionForTests(
        ready.Runtime.Panel,
        ref baseRecipe,
        baseButton,
        out var selection),
        "OutputReady prefix-switch selection was blocked");
    True(RuntimeTargetRecipeVariantService.CompleteRecipeSelectionForTests(selection) == null,
        "OutputReady prefix-switch did not arm");
    ready.Runtime.NativeSwitchTo(ready.Runtime.Authoritative.Single());

    var serviceType = typeof(RuntimeTargetRecipeVariantService);
    var privateStatic = System.Reflection.BindingFlags.NonPublic
        | System.Reflection.BindingFlags.Static;
    var beforeFullVisual = serviceType.GetMethod("BeforeUpdateAllVisual", privateStatic)
        ?? throw new InvalidOperationException("BeforeUpdateAllVisual patch method is missing");
    var afterFullVisual = serviceType.GetMethod("AfterUpdateAllVisual", privateStatic)
        ?? throw new InvalidOperationException("AfterUpdateAllVisual patch method is missing");
    var fullVisual = typeof(FakePanel).GetMethod(nameof(FakePanel.SuccessfulUpdateAllVisual))
        ?? throw new InvalidOperationException("successful FullVisual probe is missing");
    var depthField = serviceType.GetField("_updateAllVisualDepth", privateStatic)
        ?? throw new InvalidOperationException("FullVisual depth field is missing");
    var harmony = new Harmony(harmonyId);

    try
    {
        harmony.Patch(
            fullVisual,
            prefix: new HarmonyMethod(beforeFullVisual) { priority = Priority.First },
            finalizer: new HarmonyMethod(afterFullVisual) { priority = Priority.Last });
        ready.Runtime.Panel.SuccessfulUpdateAllVisual();
        RuntimeTargetRecipeVariantService.CompleteSubmitForTests(switchSubmit);

        Equal(1, ready.Runtime.Panel.FullVisualCalls,
            "successful prefix-switch FullVisual original did not run exactly once");
        Equal(1, ready.Runtime.Panel.RecipeFieldCalls,
            "successful prefix-switch recipe field did not run exactly once");
        False(RuntimeTargetRecipeVariantService.BeginOutputClosureForTests(
            ready.Closure,
            out var staleClosure),
            "exact prefix-switch left the old OutputReady closure executable");
        RuntimeTargetRecipeVariantService.CompleteOutputClosureForTests(staleClosure);
        Equal(1L, StatusCount("cancelled"),
            "exact prefix-switch did not cancel the source transaction exactly once");
        Equal(0L, StatusCount("uncertain"),
            "exact prefix-switch was incorrectly classified as inconsistent ownership");
        True(RuntimeTargetRecipeVariantService.Status.Contains(
                "mutationLatch=9:False",
                StringComparison.Ordinal),
            "exact prefix-switch polluted the business mutation latch");
        Equal(0, (int)depthField.GetValue(null)!,
            "successful FullVisual switch leaked the nested refresh depth");
    }
    finally
    {
        harmony.UnpatchSelf();
    }
}

static void VerifyFullVisualPrefixRejectsSwitchOwnershipDrift()
{
    const string harmonyId =
        "mystia-steward-companion.tests.runtime-target-recipe-variant.full-visual-switch-drift";
    var ready = ReadyOutputClosure(0x13f, 423, 0x43f);
    var transaction = ReadCurrentTransaction(ready.Runtime.Panel);
    var baseButton = new FakeButton(0x53f);
    Bind(ready.Runtime, ready.Runtime.Authoritative.Single(), baseButton);
    True(RuntimeTargetRecipeVariantService.BeginSubmitForTests(baseButton, out var switchSubmit),
        "ownership-drift switch outer submit was blocked");
    object baseRecipe = ready.Runtime.Authoritative.Single();
    True(RuntimeTargetRecipeVariantService.PrepareRecipeSelectionForTests(
        ready.Runtime.Panel,
        ref baseRecipe,
        baseButton,
        out var selection),
        "ownership-drift switch selection was blocked");
    True(RuntimeTargetRecipeVariantService.CompleteRecipeSelectionForTests(selection) == null,
        "ownership-drift switch did not arm");
    var attempt = ReadCurrentSwitchAttempt(ready.Runtime.Panel);
    switchSubmit.Probe!.PanelEpoch++;

    var serviceType = typeof(RuntimeTargetRecipeVariantService);
    var privateStatic = System.Reflection.BindingFlags.NonPublic
        | System.Reflection.BindingFlags.Static;
    var beforeFullVisual = serviceType.GetMethod("BeforeUpdateAllVisual", privateStatic)
        ?? throw new InvalidOperationException("BeforeUpdateAllVisual patch method is missing");
    var afterFullVisual = serviceType.GetMethod("AfterUpdateAllVisual", privateStatic)
        ?? throw new InvalidOperationException("AfterUpdateAllVisual patch method is missing");
    var fullVisual = typeof(FakePanel).GetMethod(nameof(FakePanel.SuccessfulUpdateAllVisual))
        ?? throw new InvalidOperationException("successful FullVisual probe is missing");
    var depthField = serviceType.GetField("_updateAllVisualDepth", privateStatic)
        ?? throw new InvalidOperationException("FullVisual depth field is missing");
    var harmony = new Harmony(harmonyId);
    Exception? observed = null;

    try
    {
        harmony.Patch(
            fullVisual,
            prefix: new HarmonyMethod(beforeFullVisual) { priority = Priority.First },
            finalizer: new HarmonyMethod(afterFullVisual) { priority = Priority.Last });
        try
        {
            ready.Runtime.Panel.SuccessfulUpdateAllVisual();
        }
        catch (Exception ex)
        {
            observed = ex;
        }

        True(observed is InvalidOperationException,
            "ownership drift did not abort FullVisual before its native original");
        Equal(0, ready.Runtime.Panel.FullVisualCalls,
            "ownership drift allowed the FullVisual original to run");
        Equal(0, ready.Runtime.Panel.RecipeFieldCalls,
            "ownership drift allowed the nested recipe field to run");
        Equal("Uncertain", ReadProperty(attempt, "State")!.ToString()!,
            "ownership drift left the retained switch attempt armed");
        Equal("Uncertain", ReadProperty(transaction, "State")!.ToString()!,
            "ownership drift left the current OutputReady transaction executable");
        False(RuntimeTargetRecipeVariantService.BeginOutputClosureForTests(
            ready.Closure,
            out var staleClosure),
            "ownership drift left the old output closure executable");
        RuntimeTargetRecipeVariantService.CompleteOutputClosureForTests(staleClosure);
        True(RuntimeTargetRecipeVariantService.Status.Contains(
                "mutationLatch=9:True",
                StringComparison.Ordinal),
            "ownership drift did not latch the uncertain business mutation");
        Equal(0, (int)depthField.GetValue(null)!,
            "ownership-drift prefix exception leaked FullVisual depth");
    }
    finally
    {
        RuntimeTargetRecipeVariantService.CompleteSubmitForTests(switchSubmit, observed);
        harmony.UnpatchSelf();
    }
}

static void VerifyFullVisualPrefixCarriesResetReceiptIntoRecipePostfix()
{
    const string harmonyId =
        "mystia-steward-companion.tests.runtime-target-recipe-variant.full-visual-postfix-failure";
    var ready = ReadyOutputClosure(0x140, 424, 0x440);
    var transaction = ReadCurrentTransaction(ready.Runtime.Panel);
    ready.Runtime.ResetRecipeListToAuthoritative();
    ready.Runtime.FailReadbackAfterInsert = true;

    var serviceType = typeof(RuntimeTargetRecipeVariantService);
    var privateStatic = System.Reflection.BindingFlags.NonPublic
        | System.Reflection.BindingFlags.Static;
    var beforeFullVisual = serviceType.GetMethod("BeforeUpdateAllVisual", privateStatic)
        ?? throw new InvalidOperationException("BeforeUpdateAllVisual patch method is missing");
    var afterFullVisual = serviceType.GetMethod("AfterUpdateAllVisual", privateStatic)
        ?? throw new InvalidOperationException("AfterUpdateAllVisual patch method is missing");
    var afterRecipeField = serviceType.GetMethod("AfterUpdateRecipeField", privateStatic)
        ?? throw new InvalidOperationException("AfterUpdateRecipeField patch method is missing");
    var fullVisual = typeof(FakePanel).GetMethod(nameof(FakePanel.SuccessfulUpdateAllVisual))
        ?? throw new InvalidOperationException("successful FullVisual probe is missing");
    var recipeField = typeof(FakePanel).GetMethod(nameof(FakePanel.SuccessfulUpdateRecipeField))
        ?? throw new InvalidOperationException("successful recipe-field probe is missing");
    var depthField = serviceType.GetField("_updateAllVisualDepth", privateStatic)
        ?? throw new InvalidOperationException("FullVisual depth field is missing");
    var harmony = new Harmony(harmonyId);

    try
    {
        harmony.Patch(
            fullVisual,
            prefix: new HarmonyMethod(beforeFullVisual) { priority = Priority.First },
            finalizer: new HarmonyMethod(afterFullVisual) { priority = Priority.Last });
        harmony.Patch(
            recipeField,
            postfix: new HarmonyMethod(afterRecipeField) { priority = Priority.Last });
        ready.Runtime.Panel.SuccessfulUpdateAllVisual();

        Equal(1, ready.Runtime.Panel.FullVisualCalls,
            "postfix-failure FullVisual original did not run exactly once");
        Equal(1, ready.Runtime.Panel.RecipeFieldCalls,
            "postfix-failure recipe field did not run exactly once");
        Equal(1, ready.Runtime.InsertCalls,
            "postfix-failure probe did not reach the native Insert boundary");
        Same(transaction, ReadCurrentTransaction(ready.Runtime.Panel),
            "postfix insertion failure replaced the confirmed material transaction");
        Equal("Applied", ReadProperty(transaction, "State")!.ToString()!,
            "postfix insertion failure polluted the confirmed material transaction");
        False(RuntimeTargetRecipeVariantService.BeginSubmitForTests(
            ready.Button,
            out var staleButtonSubmit),
            "postfix insertion failure left the old output button AwaitingRebind");
        RuntimeTargetRecipeVariantService.CompleteSubmitForTests(staleButtonSubmit);
        False(RuntimeTargetRecipeVariantService.BeginOutputClosureForTests(
            ready.Closure,
            out var staleClosure),
            "postfix insertion failure left the old output closure executable");
        RuntimeTargetRecipeVariantService.CompleteOutputClosureForTests(staleClosure);
        True(RuntimeTargetRecipeVariantService.Status.Contains(
                "mutationLatch=9:False",
                StringComparison.Ordinal),
            "postfix insertion uncertainty polluted the business mutation latch");
        Equal(0, (int)depthField.GetValue(null)!,
            "postfix insertion failure leaked FullVisual depth");
    }
    finally
    {
        harmony.UnpatchSelf();
    }
}

static void VerifyNestedFullVisualScopesReleaseLifo()
{
    const string harmonyId =
        "mystia-steward-companion.tests.runtime-target-recipe-variant.full-visual-nested";
    var ready = ReadyOutputClosure(0x141, 425, 0x441);
    var transaction = ReadCurrentTransaction(ready.Runtime.Panel);
    ready.Runtime.ResetRecipeListToAuthoritative();
    var serviceType = typeof(RuntimeTargetRecipeVariantService);
    var privateStatic = System.Reflection.BindingFlags.NonPublic
        | System.Reflection.BindingFlags.Static;
    var beforeFullVisual = serviceType.GetMethod("BeforeUpdateAllVisual", privateStatic)
        ?? throw new InvalidOperationException("BeforeUpdateAllVisual patch method is missing");
    var afterFullVisual = serviceType.GetMethod("AfterUpdateAllVisual", privateStatic)
        ?? throw new InvalidOperationException("AfterUpdateAllVisual patch method is missing");
    var nestedFullVisual = typeof(FakePanel).GetMethod(nameof(FakePanel.NestedUpdateAllVisual))
        ?? throw new InvalidOperationException("nested FullVisual probe is missing");
    var recipeField = typeof(FakePanel).GetMethod(nameof(FakePanel.SuccessfulUpdateRecipeField))
        ?? throw new InvalidOperationException("successful recipe-field probe is missing");
    var afterRecipeField = serviceType.GetMethod("AfterUpdateRecipeField", privateStatic)
        ?? throw new InvalidOperationException("AfterUpdateRecipeField patch method is missing");
    var depthField = serviceType.GetField("_updateAllVisualDepth", privateStatic)
        ?? throw new InvalidOperationException("FullVisual depth field is missing");
    var scopeField = serviceType.GetField("_activeFullVisualRefreshScope", privateStatic)
        ?? throw new InvalidOperationException("FullVisual scope field is missing");
    var harmony = new Harmony(harmonyId);

    try
    {
        harmony.Patch(
            nestedFullVisual,
            prefix: new HarmonyMethod(beforeFullVisual) { priority = Priority.First },
            finalizer: new HarmonyMethod(afterFullVisual) { priority = Priority.Last });
        harmony.Patch(
            recipeField,
            postfix: new HarmonyMethod(afterRecipeField) { priority = Priority.Last });
        ready.Runtime.Panel.NestedUpdateAllVisual();

        Equal(2, ready.Runtime.Panel.NestedFullVisualCalls,
            "nested FullVisual original did not run exactly twice");
        Equal(1, ready.Runtime.Panel.RecipeFieldCalls,
            "inner FullVisual did not execute the recipe postfix exactly once");
        Same(transaction, ReadCurrentTransaction(ready.Runtime.Panel),
            "nested FullVisual replaced the confirmed material transaction");
        Equal("Applied", ReadProperty(transaction, "State")!.ToString()!,
            "nested FullVisual retained the stale OutputReady state");
        False(RuntimeTargetRecipeVariantService.BeginOutputClosureForTests(
            ready.Closure,
            out var staleClosure),
            "nested FullVisual left the old output closure executable");
        RuntimeTargetRecipeVariantService.CompleteOutputClosureForTests(staleClosure);
        Equal(0, (int)depthField.GetValue(null)!,
            "nested FullVisual scopes leaked refresh depth");
        True(scopeField.GetValue(null) == null,
            "nested FullVisual scopes did not restore the ThreadStatic parent chain");
        Equal(0L, StatusCount("uncertain"),
            "nested FullVisual scopes polluted the confirmed material receipt");
        True(RuntimeTargetRecipeVariantService.Status.Contains(
                "mutationLatch=9:False",
                StringComparison.Ordinal),
            "nested FullVisual scopes polluted the business mutation latch");
    }
    finally
    {
        harmony.UnpatchSelf();
    }
}

static void VerifyFullVisualResetReceiptRejectsFreshOutputAba()
{
    const string harmonyId =
        "mystia-steward-companion.tests.runtime-target-recipe-variant.full-visual-output-aba";
    var ready = ReadyOutputClosure(0x142, 426, 0x442);
    var transaction = ReadCurrentTransaction(ready.Runtime.Panel);
    var debitCalls = ready.Runtime.DebitCalls;
    var addRangeCalls = ready.Runtime.AddRangeCalls;
    FakeButton? freshButton = null;
    FakeClosure? freshClosure = null;
    ready.Runtime.Panel.BeforeSuccessfulRecipeField = () =>
    {
        var combo = ready.Runtime.Combo(
            0x742,
            ready.Runtime.Authoritative.Single(),
            new[] { 2 });
        freshButton = new FakeButton(0x542);
        True(RuntimeTargetRecipeVariantService.BeginSubmitForTests(
            freshButton,
            out var freshSubmit),
            "fresh output ABA could not establish its button probe");
        True(RuntimeTargetRecipeVariantService.PrepareOutputSelectionForTests(
            ready.Runtime.Panel,
            combo,
            freshButton,
            out var freshOutput),
            "fresh output ABA could not bind the replacement output");
        freshClosure = ready.Runtime.RegisterOutputClosure(
            freshButton,
            combo,
            0x443);
        True(RuntimeTargetRecipeVariantService.CompleteOutputSelectionForTests(
                freshOutput,
                freshButton) == null,
            "fresh output ABA registration failed");
        RuntimeTargetRecipeVariantService.CompleteSubmitForTests(freshSubmit);
        ready.Runtime.FailRecipeListRead = true;
    };

    var serviceType = typeof(RuntimeTargetRecipeVariantService);
    var privateStatic = System.Reflection.BindingFlags.NonPublic
        | System.Reflection.BindingFlags.Static;
    var beforeFullVisual = serviceType.GetMethod("BeforeUpdateAllVisual", privateStatic)
        ?? throw new InvalidOperationException("BeforeUpdateAllVisual patch method is missing");
    var afterFullVisual = serviceType.GetMethod("AfterUpdateAllVisual", privateStatic)
        ?? throw new InvalidOperationException("AfterUpdateAllVisual patch method is missing");
    var afterRecipeField = serviceType.GetMethod("AfterUpdateRecipeField", privateStatic)
        ?? throw new InvalidOperationException("AfterUpdateRecipeField patch method is missing");
    var fullVisual = typeof(FakePanel).GetMethod(nameof(FakePanel.SuccessfulUpdateAllVisual))
        ?? throw new InvalidOperationException("successful FullVisual probe is missing");
    var recipeField = typeof(FakePanel).GetMethod(nameof(FakePanel.SuccessfulUpdateRecipeField))
        ?? throw new InvalidOperationException("successful recipe-field probe is missing");
    var depthField = serviceType.GetField("_updateAllVisualDepth", privateStatic)
        ?? throw new InvalidOperationException("FullVisual depth field is missing");
    var scopeField = serviceType.GetField("_activeFullVisualRefreshScope", privateStatic)
        ?? throw new InvalidOperationException("FullVisual scope field is missing");
    var harmony = new Harmony(harmonyId);

    try
    {
        harmony.Patch(
            fullVisual,
            prefix: new HarmonyMethod(beforeFullVisual) { priority = Priority.First },
            finalizer: new HarmonyMethod(afterFullVisual) { priority = Priority.Last });
        harmony.Patch(
            recipeField,
            postfix: new HarmonyMethod(afterRecipeField) { priority = Priority.Last });
        ready.Runtime.Panel.SuccessfulUpdateAllVisual();

        True(freshButton != null && freshClosure != null,
            "native FullVisual probe did not install the fresh output ABA");
        Same(transaction, ReadCurrentTransaction(ready.Runtime.Panel),
            "fresh output ABA replaced the confirmed material transaction");
        Equal("Applied", ReadProperty(transaction, "State")!.ToString()!,
            "fresh output ABA survived the recipe postfix pre-read failure");
        False(RuntimeTargetRecipeVariantService.BeginOutputClosureForTests(
            ready.Closure,
            out var staleOriginalClosure),
            "fresh output ABA revived the prefix-retired output closure");
        RuntimeTargetRecipeVariantService.CompleteOutputClosureForTests(
            staleOriginalClosure);
        False(RuntimeTargetRecipeVariantService.BeginOutputClosureForTests(
            freshClosure!,
            out var staleFreshClosure),
            "fresh output ABA left the replacement output closure executable");
        RuntimeTargetRecipeVariantService.CompleteOutputClosureForTests(
            staleFreshClosure);
        True(RuntimeTargetRecipeVariantService.BeginSubmitForTests(
            freshButton!,
            out var freshRearm),
            "fresh output ABA could not clear its retired native callback");
        False(freshButton!.HasSubmitCallback,
            "fresh output ABA kept its retired native callback executable");
        RuntimeTargetRecipeVariantService.CompleteSubmitForTests(freshRearm);
        Equal(debitCalls, ready.Runtime.DebitCalls,
            "fresh output ABA replayed the extras debit");
        Equal(addRangeCalls, ready.Runtime.AddRangeCalls,
            "fresh output ABA replayed the selected-list write");
        Equal(0, (int)depthField.GetValue(null)!,
            "fresh output ABA leaked FullVisual depth");
        True(scopeField.GetValue(null) == null,
            "fresh output ABA leaked the FullVisual scope chain");
        True(RuntimeTargetRecipeVariantService.Status.Contains(
                "mutationLatch=9:False",
                StringComparison.Ordinal),
            "fresh output ABA polluted the business mutation latch");
    }
    finally
    {
        harmony.UnpatchSelf();
    }
}

static void VerifyNativeRecipeSwitchReceipts()
{
    foreach (var destination in new[] { "ordinary", "base" })
    {
        var baseRecipe = new FakeRecipe(0x5d0, 410, new[] { 1 }, 4);
        var ordinaryRecipe = new FakeRecipe(0x5d1, 411, new[] { 3 }, 4);
        var runtime = Install(baseRecipe, ordinaryRecipe);
        runtime.Panel.IsFreeCook = destination == "ordinary";
        runtime.Panel.ExtraCostIngredient = destination == "base" ? 2 : 1;
        runtime.Inventory[1] = 10;
        runtime.Inventory[2] = destination == "base" ? -1 : 10;
        runtime.Inventory[3] = 10;
        var target = Set(410,
            Target(RuntimeUiTargetKind.Rare, 410, new[] { 1, 2 }, new[] { 2 }, "switch-source"));
        Publish(target);
        True(RuntimeTargetRecipeVariantService.InjectForTests(runtime.Panel, target, 9),
            $"{destination} switch injection failed");
        var source = runtime.Created.Single();
        var sourceButton = new FakeButton(0x6d0);
        Bind(runtime, source, sourceButton);
        object sourceSelection = source;
        True(RuntimeTargetRecipeVariantService.RouteRecipeSelectionForTests(
            runtime.Panel, ref sourceSelection, sourceButton), $"{destination} source route failed");
        True(RuntimeTargetRecipeVariantService.BeginSubmitForTests(sourceButton, out var sourceSubmit),
            $"{destination} source submit failed");
        runtime.NativeImportBase(baseRecipe);
        RuntimeTargetRecipeVariantService.ApplyExtrasForTests(runtime.Panel, out var sourceVisual);
        RuntimeTargetRecipeVariantService.CompleteUpdateVisualForTests(sourceVisual);
        RuntimeTargetRecipeVariantService.CompleteSubmitForTests(sourceSubmit);

        var destinationRecipe = destination == "ordinary" ? ordinaryRecipe : baseRecipe;
        var destinationButton = new FakeButton(destination == "ordinary" ? 0x6d1 : 0x6d2);
        if (destination == "base") Bind(runtime, destinationRecipe, destinationButton);

        // A focus-only selection installs the native callback but cannot retire the mutation.
        object focus = destinationRecipe;
        True(RuntimeTargetRecipeVariantService.PrepareRecipeSelectionForTests(
            runtime.Panel, ref focus, destinationButton, out var focusState),
            $"{destination} focus selection was blocked");
        RuntimeTargetRecipeVariantService.CompleteRecipeSelectionForTests(focusState);
        Equal("Applied", ReadProperty(ReadCurrentTransaction(runtime.Panel), "State")!.ToString()!,
            $"{destination} focus-only selection retired the source transaction");

        // behavior=2 first activation selects only; no receipt means the old transaction remains.
        True(RuntimeTargetRecipeVariantService.BeginSubmitForTests(destinationButton, out var selectOnly),
            $"{destination} select-only activation was blocked");
        RuntimeTargetRecipeVariantService.CompleteSubmitForTests(selectOnly);
        Equal("Applied", ReadProperty(ReadCurrentTransaction(runtime.Panel), "State")!.ToString()!,
            $"{destination} unconsumed activation retired the source transaction");
        Equal(0, runtime.NativeCreditCalls,
            $"{destination} unconsumed activation performed a native refund");
        var inventoryReadsBeforeSwitch = runtime.InventoryReadCalls;

        // The next activation consumes the installed callback and yields the exact native receipt.
        True(RuntimeTargetRecipeVariantService.BeginSubmitForTests(destinationButton, out var switched),
            $"{destination} committed switch was blocked");
        runtime.NativeSwitchTo(destinationRecipe);
        RuntimeTargetRecipeVariantService.ApplyExtrasForTests(runtime.Panel, out var switchVisual);
        RuntimeTargetRecipeVariantService.CompleteUpdateVisualForTests(switchVisual);
        RuntimeTargetRecipeVariantService.CompleteSubmitForTests(switched);
        Equal(1, runtime.NativeCreditCalls, $"{destination} switch did not refund once");
        Equal(1, runtime.NativeBaseDebitCalls, $"{destination} switch did not debit its base once");
        Sequence(destinationRecipe.Ingredients, runtime.Panel.Selected.Ids,
            $"{destination} switch did not retain the native destination materials");
        Equal(1L, StatusCount("cancelled"),
            $"{destination} exact switch did not cancel the source transaction once");
        if (destination == "ordinary")
        {
            Equal(inventoryReadsBeforeSwitch, runtime.InventoryReadCalls,
                "free-cook switch queried inventory during arm or receipt");
        }
        else
        {
            Equal(-1, runtime.Inventory[2],
                "multiplier switch changed an infinite ingredient sentinel");
        }
    }

    var variantRuntime = Install(new FakeRecipe(0x5e0, 420, new[] { 1 }, 4));
    variantRuntime.Inventory[1] = 12;
    variantRuntime.Inventory[2] = 12;
    variantRuntime.Inventory[3] = 12;
    var variants = Set(
        420,
        Target(RuntimeUiTargetKind.Rare, 420, new[] { 1, 2 }, new[] { 2 }, "switch-a"),
        Target(RuntimeUiTargetKind.Normal, 420, new[] { 1, 3 }, new[] { 3 }, "switch-b"));
    Publish(variants);
    True(RuntimeTargetRecipeVariantService.InjectForTests(variantRuntime.Panel, variants, 9),
        "variant switch injection failed");
    var firstVariant = variantRuntime.Created[0];
    var secondVariant = variantRuntime.Created[1];
    var firstButton = new FakeButton(0x6e0);
    var secondButton = new FakeButton(0x6e1);
    Bind(variantRuntime, firstVariant, firstButton);
    Bind(variantRuntime, secondVariant, secondButton);
    object firstRoute = firstVariant;
    True(RuntimeTargetRecipeVariantService.RouteRecipeSelectionForTests(
        variantRuntime.Panel, ref firstRoute, firstButton), "variant A route failed");
    True(RuntimeTargetRecipeVariantService.BeginSubmitForTests(firstButton, out var firstSubmit),
        "variant A submit failed");
    variantRuntime.NativeImportBase(variantRuntime.Authoritative.Single());
    RuntimeTargetRecipeVariantService.ApplyExtrasForTests(variantRuntime.Panel, out var firstVisual);
    RuntimeTargetRecipeVariantService.CompleteUpdateVisualForTests(firstVisual);
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(firstSubmit);
    var firstSequence = (long)ReadProperty(ReadCurrentTransaction(variantRuntime.Panel), "Sequence")!;

    True(RuntimeTargetRecipeVariantService.BeginSubmitForTests(secondButton, out var variantSwitch),
        "variant B first-click submit was blocked");
    object secondRoute = secondVariant;
    True(RuntimeTargetRecipeVariantService.PrepareRecipeSelectionForTests(
        variantRuntime.Panel, ref secondRoute, secondButton, out var secondSelection),
        "variant B nested selection was blocked");
    RuntimeTargetRecipeVariantService.CompleteRecipeSelectionForTests(secondSelection);
    variantRuntime.NativeSwitchTo(variantRuntime.Authoritative.Single());
    RuntimeTargetRecipeVariantService.ApplyExtrasForTests(variantRuntime.Panel, out var secondVisual);
    RuntimeTargetRecipeVariantService.CompleteUpdateVisualForTests(secondVisual);
    var destinationBeforeVisualCompletedRebuild = ReadCurrentTransaction(variantRuntime.Panel);
    var destinationSequenceBeforeVisualCompletedRebuild = (long)ReadProperty(
        destinationBeforeVisualCompletedRebuild,
        "Sequence")!;
    var destinationEpochBeforeVisualCompletedRebuild = (long)ReadProperty(
        destinationBeforeVisualCompletedRebuild,
        "PanelEpoch")!;
    var debitsBeforeVisualCompletedRebuild = variantRuntime.DebitCalls;
    var writesBeforeVisualCompletedRebuild = variantRuntime.AddRangeCalls;
    variantRuntime.ResetRecipeListToAuthoritative();
    True(RuntimeTargetRecipeVariantService.InjectForTests(
        variantRuntime.Panel,
        variants,
        9,
        RuntimeTargetRecipeVariantService.RecipeSurfaceRefreshKind.FullVisual),
        "VisualCompleted variant switch was not transferred across the rebuilt recipe list");
    Equal(debitsBeforeVisualCompletedRebuild, variantRuntime.DebitCalls,
        "VisualCompleted epoch transfer replayed the destination extras debit");
    Equal(writesBeforeVisualCompletedRebuild, variantRuntime.AddRangeCalls,
        "VisualCompleted epoch transfer replayed the destination selected-list write");
    var destinationAfterVisualCompletedRebuild = ReadCurrentTransaction(variantRuntime.Panel);
    Same(destinationBeforeVisualCompletedRebuild, destinationAfterVisualCompletedRebuild,
        "VisualCompleted epoch transfer replaced the exact destination transaction object");
    Equal(destinationSequenceBeforeVisualCompletedRebuild,
        (long)ReadProperty(destinationAfterVisualCompletedRebuild, "Sequence")!,
        "VisualCompleted epoch transfer changed the destination transaction sequence");
    True((long)ReadProperty(destinationAfterVisualCompletedRebuild, "PanelEpoch")!
        > destinationEpochBeforeVisualCompletedRebuild,
        "VisualCompleted epoch transfer did not advance the exact destination epoch");
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(variantSwitch);
    var secondTransaction = ReadCurrentTransaction(variantRuntime.Panel);
    True((long)ReadProperty(secondTransaction, "Sequence")! > firstSequence,
        "variant switch reused the old transaction sequence");
    Sequence(new[] { 1, 3 }, variantRuntime.Panel.Selected.Ids,
        "variant B extras were not applied exactly once after the native receipt");
    Equal(2, variantRuntime.DebitCalls,
        "variant A to B did not perform exactly one extra debit per transaction");
    Equal(2, variantRuntime.AddRangeCalls,
        "variant A to B did not perform exactly one AddRange per transaction");
    Equal(1L, StatusCount("cancelled"),
        "VisualCompleted epoch transfer did not cancel only the source transaction");
    Equal(0L, StatusCount("uncertain"),
        "VisualCompleted epoch transfer became uncertain before the outer submit returned");

    var repeatedBase = new FakeRecipe(0x5e1, 421, new[] { 1, 1 }, 4);
    var repeatedRuntime = Install(repeatedBase);
    repeatedRuntime.Panel.ExtraCostIngredient = 2;
    repeatedRuntime.Inventory[1] = 30;
    repeatedRuntime.Inventory[2] = 20;
    var repeatedTarget = Set(421,
        Target(
            RuntimeUiTargetKind.Rare,
            421,
            new[] { 1, 2 },
            new[] { 1, 2 },
            "switch-same-repeated"));
    Publish(repeatedTarget);
    True(RuntimeTargetRecipeVariantService.InjectForTests(
        repeatedRuntime.Panel,
        repeatedTarget,
        9), "same-variant repeated-ingredient injection failed");
    var repeatedVariant = repeatedRuntime.Created.Single();
    var repeatedButton = new FakeButton(0x6e2);
    Bind(repeatedRuntime, repeatedVariant, repeatedButton);
    object repeatedRoute = repeatedVariant;
    True(RuntimeTargetRecipeVariantService.RouteRecipeSelectionForTests(
        repeatedRuntime.Panel,
        ref repeatedRoute,
        repeatedButton), "same-variant repeated-ingredient route failed");
    True(RuntimeTargetRecipeVariantService.BeginSubmitForTests(
        repeatedButton,
        out var repeatedInitialSubmit), "same-variant initial submit failed");
    repeatedRuntime.NativeImportBase(repeatedBase);
    RuntimeTargetRecipeVariantService.ApplyExtrasForTests(
        repeatedRuntime.Panel,
        out var repeatedInitialVisual);
    RuntimeTargetRecipeVariantService.CompleteUpdateVisualForTests(repeatedInitialVisual);
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(repeatedInitialSubmit);
    var repeatedInitialSequence = (long)ReadProperty(
        ReadCurrentTransaction(repeatedRuntime.Panel), "Sequence")!;
    var repeatedInventoryAfterFirstSelection = repeatedRuntime.Inventory.ToDictionary(pair => pair.Key, pair => pair.Value);

    True(RuntimeTargetRecipeVariantService.BeginSubmitForTests(
        repeatedButton,
        out var repeatedSwitch), "same-variant A to A switch did not arm");
    repeatedRuntime.NativeSwitchTo(repeatedBase);
    RuntimeTargetRecipeVariantService.ApplyExtrasForTests(
        repeatedRuntime.Panel,
        out var repeatedSwitchVisual);
    RuntimeTargetRecipeVariantService.CompleteUpdateVisualForTests(repeatedSwitchVisual);
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(repeatedSwitch);
    True((long)ReadProperty(ReadCurrentTransaction(repeatedRuntime.Panel), "Sequence")!
        > repeatedInitialSequence, "same-variant A to A switch reused its source transaction");
    Sequence(new[] { 1, 1, 1, 2 }, repeatedRuntime.Panel.Selected.Ids,
        "same-variant A to A switch changed repeated or overlapping ingredient order");
    foreach (var pair in repeatedInventoryAfterFirstSelection)
    {
        Equal(pair.Value, repeatedRuntime.Inventory[pair.Key],
            $"same-variant A to A switch changed final inventory for ingredient {pair.Key}");
    }
    Equal(2, repeatedRuntime.DebitCalls,
        "same-variant A to A switch did not debit ordered extras exactly once per transaction");
    Equal(2, repeatedRuntime.AddRangeCalls,
        "same-variant A to A switch did not append ordered extras exactly once per transaction");

    var limitedBase = new FakeRecipe(0x5e2, 422, new[] { 1 }, 4);
    var unfundedDestination = new FakeRecipe(0x5e3, 423, new[] { 3 }, 4);
    var limitedRuntime = Install(limitedBase, unfundedDestination);
    limitedRuntime.Inventory[1] = 10;
    limitedRuntime.Inventory[2] = 10;
    limitedRuntime.Inventory[3] = 0;
    var limitedTarget = Set(422,
        Target(RuntimeUiTargetKind.Rare, 422, new[] { 1, 2 }, new[] { 2 }, "switch-finite-minus-one"));
    Publish(limitedTarget);
    True(RuntimeTargetRecipeVariantService.InjectForTests(
        limitedRuntime.Panel,
        limitedTarget,
        9), "finite projected-minus-one setup injection failed");
    var limitedVariant = limitedRuntime.Created.Single();
    var limitedVariantButton = new FakeButton(0x6e3);
    Bind(limitedRuntime, limitedVariant, limitedVariantButton);
    object limitedRoute = limitedVariant;
    True(RuntimeTargetRecipeVariantService.RouteRecipeSelectionForTests(
        limitedRuntime.Panel,
        ref limitedRoute,
        limitedVariantButton), "finite projected-minus-one source route failed");
    True(RuntimeTargetRecipeVariantService.BeginSubmitForTests(
        limitedVariantButton,
        out var limitedInitialSubmit), "finite projected-minus-one source submit failed");
    limitedRuntime.NativeImportBase(limitedBase);
    RuntimeTargetRecipeVariantService.ApplyExtrasForTests(
        limitedRuntime.Panel,
        out var limitedInitialVisual);
    RuntimeTargetRecipeVariantService.CompleteUpdateVisualForTests(limitedInitialVisual);
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(limitedInitialSubmit);
    var unfundedButton = new FakeButton(0x6e4);
    True(RuntimeTargetRecipeVariantService.BeginSubmitForTests(
        unfundedButton,
        out var unfundedSubmit), "finite projected-minus-one outer submit was blocked too early");
    object unfundedRoute = unfundedDestination;
    True(RuntimeTargetRecipeVariantService.PrepareRecipeSelectionForTests(
        limitedRuntime.Panel,
        ref unfundedRoute,
        unfundedButton,
        out var unfundedSelection), "finite projected-minus-one native selection was blocked before preflight");
    var unfundedException = RuntimeTargetRecipeVariantService.CompleteRecipeSelectionForTests(
        unfundedSelection);
    True(unfundedException != null,
        "finite projected inventory result -1 was mistaken for the infinite-inventory sentinel");
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(unfundedSubmit, unfundedException);
    Equal(0, limitedRuntime.NativeCreditCalls,
        "finite projected-minus-one rejection reached the native refund callback");
    Equal("Applied", ReadProperty(
        ReadCurrentTransaction(limitedRuntime.Panel), "State")!.ToString()!,
        "finite projected-minus-one rejection changed the source transaction");
}

static void VerifyRecipeSwitchFailureAndOutputRetirement()
{
    var armFailure = PrepareMutatedTransaction(0x5f0, 430, 430, "applied");
    var baseButton = new FakeButton(0x6f0);
    Bind(armFailure.Runtime, armFailure.Runtime.Authoritative.Single(), baseButton);
    True(RuntimeTargetRecipeVariantService.BeginSubmitForTests(baseButton, out var failedSubmit),
        "switch arm-failure outer submit was blocked before native selection");
    object baseSelection = armFailure.Runtime.Authoritative.Single();
    True(RuntimeTargetRecipeVariantService.PrepareRecipeSelectionForTests(
        armFailure.Runtime.Panel, ref baseSelection, baseButton, out var failedSelection),
        "switch arm-failure selection was blocked before callback installation");
    armFailure.Runtime.ThrowPanelSelectionState = true;
    var armException = RuntimeTargetRecipeVariantService.CompleteRecipeSelectionForTests(failedSelection);
    armFailure.Runtime.ThrowPanelSelectionState = false;
    True(armException != null, "failed dynamic switch arm did not abort before callback execution");
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(failedSubmit, armException);
    Equal(0, armFailure.Runtime.NativeCreditCalls,
        "failed dynamic switch arm reached the native refund callback");
    Equal("Applied", ReadProperty(ReadCurrentTransaction(armFailure.Runtime.Panel), "State")!.ToString()!,
        "failed pre-callback switch arm changed the old transaction");

    var preservedOriginal = new InvalidOperationException("native selection failed");
    object anotherBase = armFailure.Runtime.Authoritative.Single();
    True(RuntimeTargetRecipeVariantService.PrepareRecipeSelectionForTests(
        armFailure.Runtime.Panel, ref anotherBase, baseButton, out var exceptionalSelection),
        "exception-preservation selection prefix was blocked");
    Same(preservedOriginal,
        RuntimeTargetRecipeVariantService.CompleteRecipeSelectionForTests(
            exceptionalSelection,
            preservedOriginal)!,
        "selection finalizer replaced the original native exception");

    var nativeFailure = PrepareMutatedTransaction(0x5f5, 435, 435, "applied");
    var nativeFailureBaseButton = new FakeButton(0x6f5);
    Bind(nativeFailure.Runtime, nativeFailure.Runtime.Authoritative.Single(), nativeFailureBaseButton);
    True(RuntimeTargetRecipeVariantService.BeginSubmitForTests(
        nativeFailureBaseButton,
        out var nativeFailureSubmit), "native-failure switch outer submit was blocked");
    object nativeFailureBase = nativeFailure.Runtime.Authoritative.Single();
    True(RuntimeTargetRecipeVariantService.PrepareRecipeSelectionForTests(
        nativeFailure.Runtime.Panel,
        ref nativeFailureBase,
        nativeFailureBaseButton,
        out var nativeFailureSelection), "native-failure switch selection was blocked");
    True(RuntimeTargetRecipeVariantService.CompleteRecipeSelectionForTests(nativeFailureSelection) == null,
        "native-failure switch did not arm before the simulated native exception");
    var nativeFailureException = new InvalidOperationException("native callback failed before visual receipt");
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(
        nativeFailureSubmit,
        nativeFailureException);
    Equal("Uncertain", ReadProperty(
        ReadCurrentTransaction(nativeFailure.Runtime.Panel), "State")!.ToString()!,
        "armed switch native exception did not mark the source transaction uncertain");
    True(RuntimeTargetRecipeVariantService.Status.Contains("mutationLatch=9:True", StringComparison.Ordinal),
        "armed switch native exception did not latch the current business");
    Equal(0L, StatusCount("cancelled"),
        "armed switch native exception cancelled the source without a receipt");
    Equal(0, nativeFailure.Runtime.NativeCreditCalls,
        "armed switch native exception simulated or replayed a native refund");
    Equal(1, nativeFailure.Runtime.DebitCalls,
        "armed switch native exception replayed the source extras debit");
    Equal(1, nativeFailure.Runtime.AddRangeCalls,
        "armed switch native exception replayed the source selected-list write");

    var visualFailure = PrepareMutatedTransaction(0x5f6, 436, 436, "applied");
    var visualFailureVariantButton = new FakeButton(0x6f6);
    Bind(visualFailure.Runtime, visualFailure.Synthetic, visualFailureVariantButton);
    True(RuntimeTargetRecipeVariantService.BeginSubmitForTests(
        visualFailureVariantButton,
        out var visualFailureSubmit), "visual-failure switch outer submit was blocked");
    object visualFailureVariant = visualFailure.Synthetic;
    True(RuntimeTargetRecipeVariantService.PrepareRecipeSelectionForTests(
        visualFailure.Runtime.Panel,
        ref visualFailureVariant,
        visualFailureVariantButton,
        out var visualFailureSelection), "visual-failure variant selection was blocked");
    True(RuntimeTargetRecipeVariantService.CompleteRecipeSelectionForTests(visualFailureSelection) == null,
        "visual-failure switch did not arm");
    visualFailure.Runtime.NativeSwitchTo(visualFailure.Runtime.Authoritative.Single());
    RuntimeTargetRecipeVariantService.ApplyExtrasForTests(
        visualFailure.Runtime.Panel,
        out var visualFailureState);
    var visualFailureDestination = ReadCurrentTransaction(visualFailure.Runtime.Panel);
    var visualFailureAttempt = ReadCurrentSwitchAttempt(visualFailure.Runtime.Panel);
    var visualFailureSource = ReadProperty(visualFailureAttempt, "SourceTransaction")!;
    var visualFailureException = new InvalidOperationException("native UpdateAllVisual failed");
    RuntimeTargetRecipeVariantService.CompleteUpdateVisualForTests(
        visualFailureState,
        visualFailureException);
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(
        visualFailureSubmit,
        visualFailureException);
    Equal("Uncertain", ReadProperty(visualFailureSource, "State")!.ToString()!,
        "visual exception did not mark the source switch transaction uncertain");
    Equal("Uncertain", ReadProperty(visualFailureDestination, "State")!.ToString()!,
        "visual exception did not mark the exact destination transaction uncertain");
    True(RuntimeTargetRecipeVariantService.Status.Contains("mutationLatch=9:True", StringComparison.Ordinal),
        "visual exception after receipt did not latch the current business");
    Equal(0L, StatusCount("cancelled"),
        "visual exception after receipt cancelled the source transaction");
    Equal(2, visualFailure.Runtime.DebitCalls,
        "visual exception after receipt replayed the destination extras debit");
    Equal(2, visualFailure.Runtime.AddRangeCalls,
        "visual exception after receipt replayed the destination selected-list write");

    var ready = ReadyOutputClosure(0x5f1, 431, 0x8f1);
    var readyBaseButton = new FakeButton(0x6f1);
    Bind(ready.Runtime, ready.Runtime.Authoritative.Single(), readyBaseButton);
    True(RuntimeTargetRecipeVariantService.BeginSubmitForTests(readyBaseButton, out var readySwitch),
        "OutputReady switch outer submit was blocked");
    object readyBase = ready.Runtime.Authoritative.Single();
    True(RuntimeTargetRecipeVariantService.PrepareRecipeSelectionForTests(
        ready.Runtime.Panel, ref readyBase, readyBaseButton, out var readySelection),
        "OutputReady base selection was blocked");
    True(RuntimeTargetRecipeVariantService.CompleteRecipeSelectionForTests(readySelection) == null,
        "OutputReady switch did not arm after callback installation");
    ready.Runtime.NativeSwitchTo(ready.Runtime.Authoritative.Single());
    RuntimeTargetRecipeVariantService.ApplyExtrasForTests(ready.Runtime.Panel, out var readyVisual);
    RuntimeTargetRecipeVariantService.CompleteUpdateVisualForTests(readyVisual);
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(readySwitch);
    False(RuntimeTargetRecipeVariantService.BeginOutputClosureForTests(ready.Closure, out _),
        "old OutputReady closure survived the exact native switch receipt");
    Equal(1L, StatusCount("cancelled"),
        "OutputReady switch did not cancel its old transaction exactly once");

    var uncertain = PrepareMutatedTransaction(0x5f2, 432, 432, "applied");
    var uncertainBaseButton = new FakeButton(0x6f2);
    Bind(uncertain.Runtime, uncertain.Runtime.Authoritative.Single(), uncertainBaseButton);
    True(RuntimeTargetRecipeVariantService.BeginSubmitForTests(
        uncertainBaseButton, out var uncertainSubmit), "uncertain switch outer submit failed");
    object uncertainBase = uncertain.Runtime.Authoritative.Single();
    True(RuntimeTargetRecipeVariantService.PrepareRecipeSelectionForTests(
        uncertain.Runtime.Panel, ref uncertainBase, uncertainBaseButton, out var uncertainSelection),
        "uncertain switch selection failed");
    True(RuntimeTargetRecipeVariantService.CompleteRecipeSelectionForTests(uncertainSelection) == null,
        "uncertain switch did not arm");
    uncertain.Runtime.NativeSwitchTo(uncertain.Runtime.Authoritative.Single());
    uncertain.Runtime.Inventory[2]--;
    Exception? receiptException = null;
    try
    {
        RuntimeTargetRecipeVariantService.ApplyExtrasForTests(uncertain.Runtime.Panel, out _);
    }
    catch (Exception ex)
    {
        receiptException = ex;
    }
    True(receiptException != null, "corrupt native refund receipt was accepted");
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(uncertainSubmit, receiptException);
    True(RuntimeTargetRecipeVariantService.Status.Contains("mutationLatch=9:True", StringComparison.Ordinal),
        "partial native switch failure did not latch the business uncertain");
    Equal(1, uncertain.Runtime.DebitCalls,
        "uncertain base switch replayed an extra debit after the corrupt receipt");

    var destroyed = PrepareMutatedTransaction(0x5f3, 433, 433, "applied");
    var destroyedBaseButton = new FakeButton(0x6f3);
    Bind(destroyed.Runtime, destroyed.Runtime.Authoritative.Single(), destroyedBaseButton);
    True(RuntimeTargetRecipeVariantService.BeginSubmitForTests(
        destroyedBaseButton, out var destroyedSubmit), "destroyed switch outer submit failed");
    object destroyedBase = destroyed.Runtime.Authoritative.Single();
    True(RuntimeTargetRecipeVariantService.PrepareRecipeSelectionForTests(
        destroyed.Runtime.Panel, ref destroyedBase, destroyedBaseButton, out var destroyedSelection),
        "destroyed switch selection failed");
    True(RuntimeTargetRecipeVariantService.CompleteRecipeSelectionForTests(destroyedSelection) == null,
        "destroyed switch did not arm");
    RuntimeTargetRecipeVariantService.RetireForBusinessBoundary(
        9,
        "test active-switch boundary");
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(
        destroyedSubmit,
        new InvalidOperationException("panel destroyed"));
    True(RuntimeTargetRecipeVariantService.Status.Contains("mutationLatch=9:True", StringComparison.Ordinal),
        "destroyed detached switch attempt did not latch the business uncertain");

    var oldAba = ReadyOutputClosure(0x5f4, 434, 0x8f4);
    var oldAbaVariant = oldAba.Runtime.Created.Single();
    var oldAbaDestinationButton = new FakeButton(0x6f4);
    Bind(oldAba.Runtime, oldAbaVariant, oldAbaDestinationButton);
    True(RuntimeTargetRecipeVariantService.BeginSubmitForTests(
        oldAbaDestinationButton,
        out var oldAbaSubmit), "old switch ABA outer submit failed");
    object oldAbaDestination = oldAbaVariant;
    True(RuntimeTargetRecipeVariantService.PrepareRecipeSelectionForTests(
        oldAba.Runtime.Panel,
        ref oldAbaDestination,
        oldAbaDestinationButton,
        out var oldAbaSelection), "old switch ABA destination selection failed");
    True(RuntimeTargetRecipeVariantService.CompleteRecipeSelectionForTests(oldAbaSelection) == null,
        "old switch ABA attempt did not arm");
    oldAba.Runtime.NativeSwitchTo(oldAba.Runtime.Authoritative.Single());
    RuntimeTargetRecipeVariantService.ApplyExtrasForTests(
        oldAba.Runtime.Panel,
        out var oldAbaVisual);
    RuntimeTargetRecipeVariantService.CompleteUpdateVisualForTests(oldAbaVisual);

    var newAba = ReadyOutputClosure(0x5f4, 434, 0x8f4, 10);
    Equal("OutputReady", ReadProperty(
        ReadCurrentTransaction(newAba.Runtime.Panel), "State")!.ToString()!,
        "new business did not establish the reused output identity before the old finalizer");
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(
        oldAbaSubmit,
        new InvalidOperationException("delayed old switch finalizer"));
    Equal("OutputReady", ReadProperty(
        ReadCurrentTransaction(newAba.Runtime.Panel), "State")!.ToString()!,
        "delayed old switch finalizer changed the new transaction sharing native pointers");
    True(RuntimeTargetRecipeVariantService.Status.Contains("mutationLatch=10:False", StringComparison.Ordinal),
        "delayed old switch finalizer latched the new business generation");
    True(RuntimeTargetRecipeVariantService.BeginSubmitForTests(
        newAba.Button,
        out var newAbaSubmit), "delayed old switch finalizer tombstoned the new output button");
    True(RuntimeTargetRecipeVariantService.BeginOutputClosureForTests(
        newAba.Closure,
        out var newAbaClosure), "delayed old switch finalizer tombstoned the reused closure pointer");
    var newAbaClose = RuntimeTargetRecipeVariantService.BeginPanelTeardownForTests(
        newAba.Runtime.Panel);
    newAba.Runtime.NativeClosePanel();
    RuntimeTargetRecipeVariantService.CompletePanelCloseForTests(newAbaClose);
    RuntimeTargetRecipeVariantService.CompleteOutputClosureForTests(newAbaClosure);
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(newAbaSubmit);
    Equal(1L, StatusCount("completed"),
        "new business output did not complete after the delayed old switch finalizer");
}

static void VerifyOutputBindingReentrancyAndAba()
{
    var (nativeOwned, _, _, _, nativeOwnedSubmit) = AppliedTransaction(0x136, 406, 406);
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(nativeOwnedSubmit);
    var nativeOwnedCombo = nativeOwned.Combo(
        0x336,
        nativeOwned.Authoritative.Single(),
        new[] { 2 });
    var nativeOwnedButton = new FakeButton(0x836) { HasSubmitCallback = true };
    var nativeOwnedCleanCalls = nativeOwned.CleanCalls;
    True(RuntimeTargetRecipeVariantService.BeginSubmitForTests(
        nativeOwnedButton,
        out var nativeOwnedOuter), "native-owned callback setup could not enter submit");
    True(RuntimeTargetRecipeVariantService.PrepareOutputSelectionForTests(
        nativeOwned.Panel,
        nativeOwnedCombo,
        nativeOwnedButton,
        out var nativeOwnedState), "exact output selection with a native callback was blocked");
    True(nativeOwnedButton.HasSubmitCallback,
        "output prefix cleared a callback before native OnOutputSelected owned the slot");
    Equal(nativeOwnedCleanCalls, nativeOwned.CleanCalls,
        "output prefix entered callback cleanup before native ownership");
    var nativeOwnedClosure = nativeOwned.RegisterOutputClosure(
        nativeOwnedButton,
        nativeOwnedCombo,
        0x436);
    True(RuntimeTargetRecipeVariantService.CompleteOutputSelectionForTests(
        nativeOwnedState,
        nativeOwnedButton) == null,
        "normal native output overwrite did not register the exact closure");
    Equal(nativeOwnedCleanCalls, nativeOwned.CleanCalls,
        "exact native output overwrite was unnecessarily post-cleaned");
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(nativeOwnedOuter);
    True(RuntimeTargetRecipeVariantService.BeginSubmitForTests(
        nativeOwnedButton,
        out var nativeOwnedFinal), "fresh exact output callback was not executable");
    True(RuntimeTargetRecipeVariantService.BeginOutputClosureForTests(
        nativeOwnedClosure,
        out var nativeOwnedClosureState), "fresh exact output closure was not registered");
    RuntimeTargetRecipeVariantService.CompleteOutputClosureForTests(
        nativeOwnedClosureState,
        new InvalidOperationException("test stops before native SetCook"));
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(
        nativeOwnedFinal,
        new InvalidOperationException("test stops before native SetCook"));

    var recipeRebind = Install(new FakeRecipe(0x13b, 410, new[] { 1 }, 3));
    recipeRebind.Inventory[1] = 8;
    recipeRebind.Inventory[2] = 8;
    var recipeRebindTarget = Set(410,
        Target(RuntimeUiTargetKind.Rare, 410, new[] { 1, 2 }, new[] { 2 }, "recipe-managed-rebind"));
    Publish(recipeRebindTarget);
    True(RuntimeTargetRecipeVariantService.InjectForTests(
        recipeRebind.Panel, recipeRebindTarget, 9),
        "recipe managed-rebind setup injection failed");
    var recipeRebindRow = recipeRebind.Created.Single();
    var recipeRebindButton = new FakeButton(0x83b);
    Bind(recipeRebind, recipeRebindRow, recipeRebindButton);
    recipeRebindButton.HasSubmitCallback = true;
    var recipeCleanCalls = recipeRebind.CleanCalls;
    True(RuntimeTargetRecipeVariantService.PrepareRecipeElementEnableForTests(
        recipeRebind.Panel,
        recipeRebindRow,
        recipeRebindButton,
        out var reboundRecipeState),
        "same recipe row could not replace its managed binding");
    RuntimeTargetRecipeVariantService.CompleteRecipeElementEnableForTests(reboundRecipeState);
    True(recipeRebindButton.HasSubmitCallback,
        "recipe row rebind cleared the callback owned by OnRecipeElementSelected");
    True(recipeRebindButton.Interactable,
        "recipe row rebind changed native interactability");
    Equal(recipeCleanCalls, recipeRebind.CleanCalls,
        "recipe row rebind entered the output-only callback cleaner");

    var reused = ReadyOutputClosure(0x138, 408, 0x438);
    var oldClosure = reused.Closure;
    var target = RuntimeUiPinningService.Current;
    reused.Runtime.ResetRecipeListToAuthoritative();
    True(RuntimeTargetRecipeVariantService.InjectForTests(
        reused.Runtime.Panel,
        target,
        9,
        RuntimeTargetRecipeVariantService.RecipeSurfaceRefreshKind.FullVisual),
        "same-button ABA setup could not transfer the stable OutputReady transaction");
    var freshCombo = reused.Runtime.Combo(
        0x338,
        reused.Runtime.Authoritative.Single(),
        new[] { 2 });
    True(RuntimeTargetRecipeVariantService.BeginSubmitForTests(
        reused.Button, out var firstFreshSubmit),
        "same-button ABA setup could not establish a fresh submit probe");
    True(RuntimeTargetRecipeVariantService.PrepareOutputSelectionForTests(
        reused.Runtime.Panel,
        freshCombo,
        reused.Button,
        out var freshOutputState),
        "same physical output button could not bind in the fresh epoch");
    var freshClosure = reused.Runtime.RegisterOutputClosure(
        reused.Button,
        freshCombo,
        0x538);
    True(RuntimeTargetRecipeVariantService.CompleteOutputSelectionForTests(
        freshOutputState,
        reused.Button) == null,
        "same physical output button did not reach fresh OutputReady");
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(firstFreshSubmit);

    False(RuntimeTargetRecipeVariantService.BeginOutputClosureForTests(
        oldClosure, out var staleClosureState),
        "old-epoch closure executed after the button pointer was reused");
    RuntimeTargetRecipeVariantService.CompleteOutputClosureForTests(staleClosureState);
    Equal("OutputReady", ReadProperty(
        ReadCurrentTransaction(reused.Runtime.Panel), "State")!.ToString()!,
        "old-epoch closure tombstoned the fresh same-pointer output binding");
    Equal(0L, StatusCount("uncertain"),
        "old-epoch closure poisoned the fresh same-pointer transaction");

    True(RuntimeTargetRecipeVariantService.BeginSubmitForTests(
        reused.Button, out var finalSubmit),
        "fresh same-pointer output button was disabled by the old closure");
    True(RuntimeTargetRecipeVariantService.BeginOutputClosureForTests(
        freshClosure, out var finalClosure),
        "fresh same-pointer output closure was disabled by the old closure");
    var finalClose = RuntimeTargetRecipeVariantService.BeginPanelTeardownForTests(
        reused.Runtime.Panel);
    reused.Runtime.NativeClosePanel();
    RuntimeTargetRecipeVariantService.CompletePanelCloseForTests(finalClose);
    RuntimeTargetRecipeVariantService.CompleteOutputClosureForTests(finalClosure);
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(finalSubmit);
    Equal(1L, StatusCount("completed"),
        "fresh same-pointer output did not complete after the old closure was blocked");
    Equal(1, reused.Runtime.DebitCalls,
        "same-button ABA handling replayed the extras debit");
    Equal(1, reused.Runtime.AddRangeCalls,
        "same-button ABA handling replayed the selected extras");
}

static void VerifySameActivationOutputClosure()
{
    var (runtime, _, _, _, recipeSubmit) = AppliedTransaction(0x139, 41, 71);
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(recipeSubmit);
    var combo = runtime.Combo(0x339, runtime.Authoritative.Single(), new[] { 2 });
    var outputButton = new FakeButton(0x639);

    True(RuntimeTargetRecipeVariantService.BeginSubmitForTests(outputButton, out var outputSubmit),
        "behavior-0 output activation did not establish an unbound button probe");
    True(RuntimeTargetRecipeVariantService.PrepareOutputSelectionForTests(
        runtime.Panel, combo, outputButton, out var outputState),
        "behavior-0 output selection was blocked");
    var closure = runtime.RegisterOutputClosure(outputButton, combo, 0x439);
    RuntimeTargetRecipeVariantService.CompleteOutputSelectionForTests(outputState, outputButton);
    True(RuntimeTargetRecipeVariantService.BeginOutputClosureForTests(closure, out var closureState),
        "behavior-0 final closure did not consume the same CallSubmit probe");
    var close = RuntimeTargetRecipeVariantService.BeginPanelTeardownForTests(runtime.Panel);
    RuntimeTargetRecipeVariantService.CompletePanelCloseForTests(close);
    RuntimeTargetRecipeVariantService.CompleteOutputClosureForTests(closureState);
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(outputSubmit);

    False(RuntimeTargetRecipeVariantService.BeginSubmitForTests(outputButton, out var replay),
        "completed behavior-0 output callback was replayable");
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(replay);
    Equal(1, runtime.DebitCalls, "behavior-0 completion replayed extras debit");
    Equal(1, runtime.AddRangeCalls, "behavior-0 completion replayed selected extras");
}

static void VerifyExactOutputClosureGate()
{
    var ordinaryRuntime = Install(new FakeRecipe(0x181, 90, new[] { 1 }, 1));
    var ordinary = new FakeClosure(0x481, ordinaryRuntime.Panel.Pointer, 0x581);
    True(RuntimeTargetRecipeVariantService.BeginOutputClosureForTests(ordinary, out var ordinaryState),
        "ordinary untracked generated closure was blocked");
    RuntimeTargetRecipeVariantService.CompleteOutputClosureForTests(ordinaryState);

    VerifyOutputCallbackRegistrationFailure(
        0x182,
        button => { },
        registerClosure: false,
        "native-clean output callback became ready");
    VerifyOutputCallbackRegistrationFailure(
        0x183,
        button => button.ExactDelegateMethod = false,
        registerClosure: true,
        "delegate-method drift became ready");
    VerifyOutputCallbackRegistrationFailure(
        0x184,
        button => button.ExactDelegateTarget = false,
        registerClosure: true,
        "delegate-target drift became ready");

    var noActive = ReadyOutputClosure(0x185, 94, 0x485);
    False(RuntimeTargetRecipeVariantService.BeginOutputClosureForTests(
        noActive.Closure, out var noActiveState),
        "registered final closure ran without an active CallSubmit probe");
    RuntimeTargetRecipeVariantService.CompleteOutputClosureForTests(noActiveState);

    var wrongButton = ReadyOutputClosure(0x18a, 99, 0x48a);
    var unrelatedButton = new FakeButton(0x78a);
    True(RuntimeTargetRecipeVariantService.BeginSubmitForTests(
        unrelatedButton, out var unrelatedSubmit),
        "untracked button could not establish the exact CallSubmit probe");
    False(RuntimeTargetRecipeVariantService.BeginOutputClosureForTests(
        wrongButton.Closure, out var wrongButtonState),
        "registered final closure accepted a different active button");
    RuntimeTargetRecipeVariantService.CompleteOutputClosureForTests(wrongButtonState);
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(unrelatedSubmit);

    var panelDrift = ReadyOutputClosure(0x186, 95, 0x486);
    True(RuntimeTargetRecipeVariantService.BeginSubmitForTests(
        panelDrift.Button, out var panelDriftSubmit),
        "panel-drift output submit setup failed");
    panelDrift.Closure.PanelPointer++;
    False(RuntimeTargetRecipeVariantService.BeginOutputClosureForTests(
        panelDrift.Closure, out var panelDriftState),
        "closure panel drift was accepted");
    RuntimeTargetRecipeVariantService.CompleteOutputClosureForTests(panelDriftState);
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(panelDriftSubmit);

    var comboDrift = ReadyOutputClosure(0x187, 96, 0x487);
    True(RuntimeTargetRecipeVariantService.BeginSubmitForTests(
        comboDrift.Button, out var comboDriftSubmit),
        "combo-drift output submit setup failed");
    comboDrift.Closure.ComboPointer++;
    False(RuntimeTargetRecipeVariantService.BeginOutputClosureForTests(
        comboDrift.Closure, out var comboDriftState),
        "closure combo drift was accepted");
    RuntimeTargetRecipeVariantService.CompleteOutputClosureForTests(comboDriftState);
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(comboDriftSubmit);

    var outputDrift = ReadyOutputClosure(0x189, 98, 0x489);
    True(RuntimeTargetRecipeVariantService.BeginSubmitForTests(
        outputDrift.Button, out var outputDriftSubmit),
        "output-drift submit setup failed");
    outputDrift.Closure.OutputPointer++;
    False(RuntimeTargetRecipeVariantService.BeginOutputClosureForTests(
        outputDrift.Closure, out var outputDriftState),
        "closure output pointer drift was accepted");
    RuntimeTargetRecipeVariantService.CompleteOutputClosureForTests(outputDriftState);
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(outputDriftSubmit);

    var exceptional = ReadyOutputClosure(0x188, 97, 0x488);
    True(RuntimeTargetRecipeVariantService.BeginSubmitForTests(
        exceptional.Button, out var exceptionalSubmit),
        "exceptional output submit setup failed");
    True(RuntimeTargetRecipeVariantService.BeginOutputClosureForTests(
        exceptional.Closure, out var exceptionalState),
        "exceptional final closure was blocked before the native call");
    RuntimeTargetRecipeVariantService.CompleteOutputClosureForTests(
        exceptionalState,
        new InvalidOperationException("native final closure failed"));
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(exceptionalSubmit);
    False(RuntimeTargetRecipeVariantService.BeginSubmitForTests(
        exceptional.Button, out var exceptionalReplay),
        "exceptional final closure remained replayable");
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(exceptionalReplay);
}

static void VerifyOutputCallbackRegistrationFailure(
    nint basePointer,
    Action<FakeButton> configure,
    bool registerClosure,
    string message)
{
    var (runtime, _, _, _, recipeSubmit) = AppliedTransaction(
        basePointer,
        100 + (int)(basePointer & 0xf),
        100 + (long)(basePointer & 0xf));
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(recipeSubmit);
    var combo = runtime.Combo(basePointer + 0x600, runtime.Authoritative.Single(), new[] { 2 });
    var button = new FakeButton(basePointer + 0x500);
    True(RuntimeTargetRecipeVariantService.BeginSubmitForTests(button, out var outputSubmit),
        "output registration failure setup could not establish a button probe");
    True(RuntimeTargetRecipeVariantService.PrepareOutputSelectionForTests(
        runtime.Panel, combo, button, out var outputState),
        "output registration failure setup was blocked before native registration");
    if (registerClosure)
    {
        runtime.RegisterOutputClosure(button, combo, basePointer + 0x700);
    }
    configure(button);
    RuntimeTargetRecipeVariantService.CompleteOutputSelectionForTests(outputState, button);
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(outputSubmit);
    False(RuntimeTargetRecipeVariantService.BeginSubmitForTests(button, out var retry), message);
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(retry);
}

static void VerifyFinalOutputReceiptAndRetainedIdentity()
{
    var completed = ReadyOutputClosure(0x18b, 110, 0x48b);
    True(RuntimeTargetRecipeVariantService.BeginSubmitForTests(completed.Button, out var completedSubmit),
        "completed receipt setup could not enter output submit");
    True(RuntimeTargetRecipeVariantService.BeginOutputClosureForTests(
        completed.Closure, out var completedClosure), "completed receipt closure was blocked");
    var completedClose = RuntimeTargetRecipeVariantService.BeginPanelTeardownForTests(completed.Runtime.Panel);
    RuntimeTargetRecipeVariantService.CompletePanelCloseForTests(completedClose);
    RuntimeTargetRecipeVariantService.CompleteOutputClosureForTests(completedClosure);
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(completedSubmit);
    True(RuntimeTargetRecipeVariantService.Status.Contains("completed=1", StringComparison.Ordinal)
        && RuntimeTargetRecipeVariantService.Status.Contains("uncertain=0", StringComparison.Ordinal),
        "normal closure plus exact close receipt did not become Completed");

    var missing = ReadyOutputClosure(0x18c, 111, 0x48c);
    True(RuntimeTargetRecipeVariantService.BeginSubmitForTests(missing.Button, out var missingSubmit),
        "missing receipt setup could not enter output submit");
    True(RuntimeTargetRecipeVariantService.BeginOutputClosureForTests(
        missing.Closure, out var missingClosure), "missing receipt closure was blocked before native call");
    RuntimeTargetRecipeVariantService.CompleteOutputClosureForTests(missingClosure);
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(missingSubmit);
    True(RuntimeTargetRecipeVariantService.Status.Contains("uncertain=1", StringComparison.Ordinal),
        "normal closure without exact close receipt did not become Uncertain");

    missing.Runtime.ResetRecipeListToAuthoritative();
    True(RuntimeTargetRecipeVariantService.InjectForTests(
        missing.Runtime.Panel,
        RuntimeUiPinningService.Current,
        9,
        RuntimeTargetRecipeVariantService.RecipeSurfaceRefreshKind.FullVisual),
        "same-business latch rebuild failed");
    False(RuntimeTargetRecipeVariantService.PrepareRecipeElementEnableForTests(
        missing.Runtime.Panel, missing.Runtime.Created.Single(), new FakeButton(0x78c), out _),
        "same-business rebuild enabled a synthetic row after uncertain mutation");

    missing.Runtime.ResetRecipeListToAuthoritative();
    RuntimeNightBusinessLifecycle.Snapshot = new NightBusinessSnapshot(true, 10);
    var nextBusiness = new RuntimeUiTargetSetSnapshot(
        112,
        10,
        new[] { Target(RuntimeUiTargetKind.Rare, 111, new[] { 1, 2 }, new[] { 2 }, "next-business") });
    Publish(nextBusiness);
    True(RuntimeTargetRecipeVariantService.InjectForTests(
        missing.Runtime.Panel,
        nextBusiness,
        10,
        RuntimeTargetRecipeVariantService.RecipeSurfaceRefreshKind.FullVisual),
        "new business did not clear the mutation-uncertain latch");
    Bind(missing.Runtime, missing.Runtime.Created.Single(), new FakeButton(0x78d));

    var failedClose = ReadyOutputClosure(0x18e, 113, 0x48e);
    True(RuntimeTargetRecipeVariantService.BeginSubmitForTests(failedClose.Button, out var failedCloseSubmit),
        "failed-close setup could not enter output submit");
    True(RuntimeTargetRecipeVariantService.BeginOutputClosureForTests(
        failedClose.Closure, out var failedCloseClosure), "failed-close closure was blocked");
    var failedCloseToken = RuntimeTargetRecipeVariantService.BeginPanelTeardownForTests(
        failedClose.Runtime.Panel);
    RuntimeTargetRecipeVariantService.CompletePanelCloseForTests(
        failedCloseToken,
        originalCompleted: false);
    RuntimeTargetRecipeVariantService.CompleteOutputClosureForTests(failedCloseClosure);
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(failedCloseSubmit);
    True(RuntimeTargetRecipeVariantService.Status.Contains("uncertain=1", StringComparison.Ordinal),
        "failed native close incorrectly produced a final-output receipt");

    var replaced = ReadyOutputClosure(0x18f, 114, 0x48f);
    True(RuntimeTargetRecipeVariantService.BeginSubmitForTests(replaced.Button, out var replacedSubmit),
        "retained-replacement setup could not enter output submit");
    True(RuntimeTargetRecipeVariantService.BeginOutputClosureForTests(
        replaced.Closure, out var replacedClosure), "retained-replacement closure was blocked");
    var replacedClose = RuntimeTargetRecipeVariantService.BeginPanelTeardownForTests(replaced.Runtime.Panel);
    RuntimeTargetRecipeVariantService.CompletePanelCloseForTests(replacedClose);
    replaced.Runtime.ResetRecipeListToAuthoritative();
    replaced.Runtime.FailRecipeListRead = true;
    var replacementTarget = Set(115,
        Target(RuntimeUiTargetKind.Rare, 114, new[] { 1, 2 }, new[] { 2 }, "replacement"));
    Publish(replacementTarget);
    False(RuntimeTargetRecipeVariantService.InjectForTests(
        replaced.Runtime.Panel, replacementTarget, 9),
        "a target replacement was allowed over a retained OutputSubmitting finalizer");
    Equal("Uncertain", ReadProperty(
        ReadCurrentTransaction(replaced.Runtime.Panel), "State")!.ToString()!,
        "target replacement did not fail closed the retained OutputSubmitting transaction");
    Equal(1L, StatusCount("uncertain"),
        "target replacement did not count the in-flight transaction exactly once");
    True(RuntimeTargetRecipeVariantService.Status.Contains("mutationLatch=9:True", StringComparison.Ordinal),
        "target replacement did not latch the uncertain business mutation");
    RuntimeTargetRecipeVariantService.CompleteOutputClosureForTests(replacedClosure);
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(replacedSubmit);
    Equal(1L, StatusCount("uncertain"),
        "late close/output tokens recounted the replaced uncertain transaction");
    Equal(0L, StatusCount("completed"),
        "a close receipt completed after its retained transaction was replaced");

    var removed = ReadyOutputClosure(0x18d, 112, 0x48d);
    True(RuntimeTargetRecipeVariantService.BeginSubmitForTests(removed.Button, out var removedSubmit),
        "retained-missing setup could not enter output submit");
    True(RuntimeTargetRecipeVariantService.BeginOutputClosureForTests(
        removed.Closure, out var removedClosure), "retained-missing closure was blocked");
    var close = RuntimeTargetRecipeVariantService.BeginPanelTeardownForTests(removed.Runtime.Panel);
    RuntimeTargetRecipeVariantService.CompletePanelCloseForTests(close);
    RuntimeTargetRecipeVariantService.RetireForBusinessBoundary(
        9,
        "test retained-output boundary");
    RuntimeTargetRecipeVariantService.CompleteOutputClosureForTests(removedClosure);
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(removedSubmit);
    True(RuntimeTargetRecipeVariantService.Status.Contains("uncertain=1", StringComparison.Ordinal),
        "missing retained transaction incorrectly became Completed");
    var reused = new FakeClosure(
        removed.Closure.Pointer,
        removed.Runtime.Panel.Pointer,
        removed.Closure.ComboPointer,
        removed.Closure.OutputPointer);
    True(RuntimeTargetRecipeVariantService.BeginOutputClosureForTests(reused, out var reusedState),
        "destroyed closure mapping blocked ordinary same-pointer reuse");
    RuntimeTargetRecipeVariantService.CompleteOutputClosureForTests(reusedState);
}

static void VerifyPanelCloseTransactions()
{
    foreach (var phase in new[] { "applied", "output-pending", "output-ready" })
    {
        var prepared = PrepareMutatedTransaction(
            (nint)(0x300 + phase.Length),
            160 + phase.Length,
            160 + phase.Length,
            phase);
        var close = RuntimeTargetRecipeVariantService.BeginPanelTeardownForTests(prepared.Runtime.Panel);
        prepared.Runtime.NativeClosePanel();
        RuntimeTargetRecipeVariantService.CompletePanelCloseForTests(close);
        Equal(1L, StatusCount("cancelled"), $"{phase} normal close was not Cancelled");
        Equal(0L, StatusCount("uncertain"), $"{phase} normal close became Uncertain");

        ForceCurrentTransactionUncertain(prepared.Runtime.Panel, "late terminal failure");
        Equal(1L, StatusCount("cancelled"), $"{phase} Cancelled terminal changed after a late failure");
        Equal(0L, StatusCount("uncertain"), $"{phase} Cancelled terminal polluted the business latch");

        prepared.Runtime.ResetRecipeListToAuthoritative();
        Publish(prepared.Target);
        True(RuntimeTargetRecipeVariantService.InjectForTests(
            prepared.Runtime.Panel, prepared.Target, 9), $"{phase} panel did not reopen after exact rollback");
        var nextSynthetic = prepared.Runtime.Created.Single();
        var nextButton = new FakeButton((nint)(0x800 + phase.Length));
        Bind(prepared.Runtime, nextSynthetic, nextButton);
        object nextSelection = nextSynthetic;
        True(RuntimeTargetRecipeVariantService.RouteRecipeSelectionForTests(
            prepared.Runtime.Panel, ref nextSelection, nextButton),
            $"{phase} reopened synthetic row did not establish a fresh transaction");
        True(RuntimeTargetRecipeVariantService.BeginSubmitForTests(nextButton, out var nextSubmit),
            $"{phase} reopened transaction submit was blocked");
        True(nextSubmit.TransactionSequence > prepared.Sequence,
            $"{phase} reopened panel reused its Cancelled transaction sequence");
        RuntimeTargetRecipeVariantService.CompleteSubmitForTests(nextSubmit);
    }

    var free = PrepareMutatedTransaction(0x31f, 179, 179, "applied", freeCook: true);
    Equal(0, free.Runtime.DebitCalls, "free-cook setup unexpectedly debited inventory");
    var freeClose = RuntimeTargetRecipeVariantService.BeginPanelTeardownForTests(free.Runtime.Panel);
    free.Runtime.NativeClosePanel();
    RuntimeTargetRecipeVariantService.CompletePanelCloseForTests(freeClose);
    Equal(1L, StatusCount("cancelled"), "free-cook normal close was not Cancelled");
    Equal(0L, StatusCount("uncertain"), "free-cook normal close became Uncertain");

    foreach (var phase in new[] { "applied", "output-pending", "output-ready" })
    {
        var failed = PrepareMutatedTransaction(
            (nint)(0x340 + phase.Length),
            180 + phase.Length,
            180 + phase.Length,
            phase);
        var close = RuntimeTargetRecipeVariantService.BeginPanelTeardownForTests(failed.Runtime.Panel);
        RuntimeTargetRecipeVariantService.CompletePanelCloseForTests(close, originalCompleted: false);
        Equal(0L, StatusCount("cancelled"), $"{phase} exceptional close was treated as rollback");
        Equal(1L, StatusCount("uncertain"), $"{phase} exceptional close did not latch Uncertain");
        failed.Runtime.ResetRecipeListToAuthoritative();
        False(RuntimeTargetRecipeVariantService.InjectForTests(
            failed.Runtime.Panel, failed.Target, 9),
            $"{phase} exceptional close allowed a same-business variant rebuild");
        False(RuntimeTargetRecipeVariantService.PrepareRecipeElementEnableForTests(
            failed.Runtime.Panel, failed.Synthetic, new FakeButton(0x8f0), out _),
            $"{phase} exceptional close enabled its old synthetic row");
        True(RuntimeTargetRecipeVariantService.PrepareRecipeElementEnableForTests(
            failed.Runtime.Panel, failed.Runtime.Authoritative.Single(), new FakeButton(0x8f1), out _),
            $"{phase} exceptional close disabled the authoritative base row");
    }

    var completed = ReadyOutputClosure(0x36f, 199, 0x66f);
    var completedTarget = RuntimeUiPinningService.Current;
    True(RuntimeTargetRecipeVariantService.BeginSubmitForTests(completed.Button, out var completedSubmit),
        "terminal close setup could not enter output submit");
    True(RuntimeTargetRecipeVariantService.BeginOutputClosureForTests(
        completed.Closure, out var completedClosure), "terminal close setup blocked the final closure");
    var completedClose = RuntimeTargetRecipeVariantService.BeginPanelTeardownForTests(completed.Runtime.Panel);
    completed.Runtime.NativeClosePanel();
    RuntimeTargetRecipeVariantService.CompletePanelCloseForTests(completedClose);
    RuntimeTargetRecipeVariantService.CompleteOutputClosureForTests(completedClosure);
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(completedSubmit);
    Equal(1L, StatusCount("completed"), "exact final close did not remain Completed");
    Equal(0L, StatusCount("cancelled"), "final OutputSubmitting close was misclassified as Cancelled");
    ForceCurrentTransactionUncertain(completed.Runtime.Panel, "late completed failure");
    RuntimeTargetRecipeVariantService.CompletePanelCloseForTests(completedClose, originalCompleted: false);
    RuntimeTargetRecipeVariantService.CompleteOutputClosureForTests(
        completedClosure,
        new InvalidOperationException("late finalizer"));
    Equal(1L, StatusCount("completed"), "late tokens downgraded Completed");
    Equal(0L, StatusCount("uncertain"), "late tokens polluted a Completed business");
    True(RuntimeTargetRecipeVariantService.Status.Contains("mutationLatch=9:False", StringComparison.Ordinal),
        "late terminal tokens polluted the mutation latch");
    completed.Runtime.ResetRecipeListToAuthoritative();
    Publish(completedTarget);
    True(RuntimeTargetRecipeVariantService.InjectForTests(
        completed.Runtime.Panel, completedTarget, 9),
        "Completed panel did not reopen with the same pointer and target");
    var reopenedRecipe = completed.Runtime.Created.Single();
    var reopenedButton = new FakeButton(0x86f);
    Bind(completed.Runtime, reopenedRecipe, reopenedButton);
    object reopenedSelection = reopenedRecipe;
    True(RuntimeTargetRecipeVariantService.RouteRecipeSelectionForTests(
        completed.Runtime.Panel, ref reopenedSelection, reopenedButton),
        "Completed panel reopen did not create a fresh selection");
    True(RuntimeTargetRecipeVariantService.BeginSubmitForTests(reopenedButton, out var reopenedSubmit),
        "Completed panel reopen did not create a fresh submit");
    True(reopenedSubmit.TransactionSequence > completedSubmit.TransactionSequence,
        "Completed panel reopen revived the old transaction sequence");
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(reopenedSubmit);
    var emptyTarget = Set(201);
    Publish(emptyTarget);
    True(RuntimeTargetRecipeVariantService.InjectForTests(
        completed.Runtime.Panel, emptyTarget, 9),
        "Completed transaction was revived as active when targets became empty");

    var rejected = Install(new FakeRecipe(0x37f, 200, new[] { 1 }, 1));
    var rejectedTarget = Set(200,
        Target(RuntimeUiTargetKind.Rare, 200, new[] { 1, 2 }, new[] { 2 }, "rejected"));
    Publish(rejectedTarget);
    True(RuntimeTargetRecipeVariantService.InjectForTests(rejected.Panel, rejectedTarget, 9),
        "rejected terminal setup injection failed");
    var rejectedRecipe = rejected.Created.Single();
    var rejectedButton = new FakeButton(0x87f);
    Bind(rejected, rejectedRecipe, rejectedButton);
    object rejectedSelection = rejectedRecipe;
    True(RuntimeTargetRecipeVariantService.RouteRecipeSelectionForTests(
        rejected.Panel, ref rejectedSelection, rejectedButton), "rejected terminal route failed");
    False(RuntimeTargetRecipeVariantService.BeginSubmitForTests(rejectedButton, out var rejectedSubmit),
        "insufficient inventory did not reject the pending transaction");
    Equal(1L, StatusCount("rejected"), "first rejection was not counted exactly once");
    ForceCurrentTransactionUncertain(rejected.Panel, "late rejected failure");
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(
        rejectedSubmit,
        new InvalidOperationException("late rejected submit"));
    Equal(1L, StatusCount("rejected"), "late failure recounted Rejected");
    Equal(0L, StatusCount("uncertain"), "late failure downgraded Rejected");
}

static void VerifyAppliedTargetSnapshotTransfer()
{
    var sourceRecipe = new FakeRecipe(0x390, 210, new[] { 1 }, 4);
    var destinationRecipe = new FakeRecipe(0x391, 211, new[] { 3 }, 4);
    var runtime = Install(sourceRecipe, destinationRecipe);
    foreach (var ingredientId in new[] { 1, 2, 3, 4 }) runtime.Inventory[ingredientId] = 12;
    var sourceTarget = Set(
        210,
        Target(RuntimeUiTargetKind.Rare, 210, new[] { 1, 2 }, new[] { 2 }, "source-before-rotation"));
    Publish(sourceTarget);
    True(RuntimeTargetRecipeVariantService.InjectForTests(runtime.Panel, sourceTarget, 9),
        "source target setup injection failed");
    var sourceSynthetic = runtime.Created.Single();
    var sourceButton = new FakeButton(0x890);
    Bind(runtime, sourceSynthetic, sourceButton);
    object sourceSelection = sourceSynthetic;
    True(RuntimeTargetRecipeVariantService.RouteRecipeSelectionForTests(
        runtime.Panel, ref sourceSelection, sourceButton), "source target selection route failed");
    True(RuntimeTargetRecipeVariantService.BeginSubmitForTests(sourceButton, out var sourceSubmit),
        "source target submit failed");
    runtime.NativeImportBase(sourceRecipe);
    RuntimeTargetRecipeVariantService.ApplyExtrasForTests(runtime.Panel, out var sourceVisual);
    RuntimeTargetRecipeVariantService.CompleteUpdateVisualForTests(sourceVisual);
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(sourceSubmit);
    var sourceTransaction = ReadCurrentTransaction(runtime.Panel);
    var sourceSequence = (long)ReadProperty(sourceTransaction, "Sequence")!;
    var sourceIdentity = (string)ReadProperty(sourceTransaction, "Identity")!;
    var sourceOriginGeneration = (long)ReadProperty(sourceTransaction, "OriginTargetGeneration")!;
    Equal(sourceTarget.Generation, sourceOriginGeneration,
        "source transaction did not retain its originating target generation");
    Equal(1, runtime.DebitCalls, "source transaction did not debit its extras exactly once");
    Equal(1, runtime.AddRangeCalls, "source transaction did not write its extras exactly once");

    runtime.ResetRecipeListToAuthoritative();
    var emptyTarget = Set(sourceTarget.Generation + 1);
    Publish(emptyTarget);
    True(RuntimeTargetRecipeVariantService.InjectForTests(
        runtime.Panel,
        emptyTarget,
        9,
        RuntimeTargetRecipeVariantService.RecipeSurfaceRefreshKind.DirectRecipeField),
        "an empty target snapshot discarded a confirmed mutation receipt");
    var emptyTransfer = ReadCurrentTransaction(runtime.Panel);
    Same(sourceTransaction, emptyTransfer, "empty target transfer replaced the source transaction object");
    Equal(sourceSequence, (long)ReadProperty(emptyTransfer, "Sequence")!,
        "empty target transfer changed the source transaction sequence");
    Equal(sourceIdentity, (string)ReadProperty(emptyTransfer, "Identity")!,
        "empty target transfer changed the source transaction identity");
    Equal(sourceOriginGeneration, (long)ReadProperty(emptyTransfer, "OriginTargetGeneration")!,
        "empty target transfer rewrote the source transaction origin");
    Same(emptyTarget, ReadProperty(ReadCurrentPanelState(runtime.Panel), "TargetSet")!,
        "empty target transfer retained the old panel target snapshot");
    Equal("Applied", ReadProperty(emptyTransfer, "State")!.ToString()!,
        "empty target transfer changed a confirmed Applied transaction");
    Equal(0, runtime.InsertCalls, "empty target transfer inserted a recipe variant row");
    Equal(0, runtime.Created.Count, "empty target transfer allocated a synthetic Recipe");
    Equal(0L, StatusCount("uncertain"), "empty target transfer became Uncertain");
    Equal(0L, StatusCount("safety"),
        "empty target transfer consumed the safety-event reserve");
    True(RuntimeTargetRecipeVariantService.Status.Contains("mutationLatch=9:False", StringComparison.Ordinal),
        "empty target transfer polluted the business mutation latch");
    False(RuntimeTargetRecipeVariantService.PrepareRecipeElementEnableForTests(
        runtime.Panel, sourceSynthetic, new FakeButton(0x891), out _),
        "an old synthetic row survived the empty target surface");

    runtime.ResetRecipeListToAuthoritative();
    var destinationTarget = Set(
        emptyTarget.Generation + 1,
        Target(RuntimeUiTargetKind.Normal, 211, new[] { 3, 4 }, new[] { 4 }, "destination-after-rotation"));
    Publish(destinationTarget);
    True(RuntimeTargetRecipeVariantService.InjectForTests(
        runtime.Panel,
        destinationTarget,
        9,
        RuntimeTargetRecipeVariantService.RecipeSurfaceRefreshKind.DirectRecipeField),
        "a new target could not reuse the confirmed source mutation receipt");
    var destinationSurfaceTransfer = ReadCurrentTransaction(runtime.Panel);
    Same(sourceTransaction, destinationSurfaceTransfer,
        "the disappearing source target replaced its confirmed transaction before a native switch");
    Equal(sourceOriginGeneration,
        (long)ReadProperty(destinationSurfaceTransfer, "OriginTargetGeneration")!,
        "the disappearing source target rewrote the transaction origin");
    Same(destinationTarget, ReadProperty(ReadCurrentPanelState(runtime.Panel), "TargetSet")!,
        "the destination surface retained the empty target snapshot");
    Equal(1, runtime.InsertCalls, "the destination surface inserted the wrong number of variants");
    Equal(0L, StatusCount("safety"),
        "successful Applied target transfer consumed the safety-event reserve");
    var destinationSynthetic = runtime.Created.Single();
    Equal(destinationRecipe.Id, destinationSynthetic.Id,
        "the destination surface injected the disappeared source recipe");
    var destinationButton = new FakeButton(0x892);
    Bind(runtime, destinationSynthetic, destinationButton);
    True(RuntimeTargetRecipeVariantService.TryResolveRecipeRowClaims(
        runtime.Panel,
        destinationSynthetic,
        destinationButton,
        out var destinationClaims,
        out _), "the destination synthetic row did not publish its exact lease");
    Equal(RuntimeUiTargetKinds.Normal, destinationClaims,
        "the destination synthetic row inherited the disappeared rare claim");

    var snapshotLeasesBeforeSwitch = RuntimeUiTargetPublicationLease.SnapshotCreated;
    var snapshotDisposalsBeforeSwitch = RuntimeUiTargetPublicationLease.SnapshotDisposed;
    True(RuntimeTargetRecipeVariantService.BeginSubmitForTests(destinationButton, out var destinationSubmit),
        "the destination row could not begin an exact native switch");
    object destinationSelection = destinationSynthetic;
    True(RuntimeTargetRecipeVariantService.PrepareRecipeSelectionForTests(
        runtime.Panel,
        ref destinationSelection,
        destinationButton,
        out var destinationSelectionState), "the destination row selection was blocked before native switch");
    True(RuntimeTargetRecipeVariantService.CompleteRecipeSelectionForTests(destinationSelectionState) == null,
        "the destination row could not arm an exact native switch");
    Equal(snapshotLeasesBeforeSwitch + 1, RuntimeUiTargetPublicationLease.SnapshotCreated,
        "the destination switch did not acquire the current target snapshot lease exactly once");
    Equal(snapshotDisposalsBeforeSwitch, RuntimeUiTargetPublicationLease.SnapshotDisposed,
        "the destination switch released its target snapshot lease before native completion");
    runtime.NativeSwitchTo(destinationRecipe);
    RuntimeTargetRecipeVariantService.ApplyExtrasForTests(runtime.Panel, out var destinationVisual);
    RuntimeTargetRecipeVariantService.CompleteUpdateVisualForTests(destinationVisual);
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(destinationSubmit);
    Equal(snapshotDisposalsBeforeSwitch + 1, RuntimeUiTargetPublicationLease.SnapshotDisposed,
        "the destination switch leaked its target snapshot lease");
    var destinationTransaction = ReadCurrentTransaction(runtime.Panel);
    False(ReferenceEquals(sourceTransaction, destinationTransaction),
        "the exact native switch reused the disappeared source transaction object");
    True((long)ReadProperty(destinationTransaction, "Sequence")! > sourceSequence,
        "the exact native switch reused the source transaction sequence");
    Equal(destinationTarget.Generation,
        (long)ReadProperty(destinationTransaction, "OriginTargetGeneration")!,
        "the destination transaction retained the disappeared source generation");
    Equal("Cancelled", ReadProperty(sourceTransaction, "State")!.ToString()!,
        "the exact native switch did not cancel the disappeared source transaction");
    Equal("Applied", ReadProperty(destinationTransaction, "State")!.ToString()!,
        "the exact native switch did not leave the destination transaction Applied");
    Sequence(new[] { 3, 4 }, runtime.Panel.Selected.Ids,
        "the exact native switch did not select the destination base plus extras");
    Equal(2, runtime.DebitCalls, "target rotation replayed or skipped an extras debit");
    Equal(2, runtime.AddRangeCalls, "target rotation replayed or skipped a selected-list write");
    Equal(1, runtime.NativeCreditCalls, "target rotation did not use exactly one native refund");
    Equal(1, runtime.NativeBaseDebitCalls, "target rotation did not use exactly one native base debit");
    Equal(0L, StatusCount("uncertain"), "the exact cross-snapshot switch became Uncertain");
}

static void VerifyOutputReadyTargetSnapshotTransfer()
{
    var ready = ReadyOutputClosure(0x39a, 220, 0x79a);
    var sourceTarget = RuntimeUiPinningService.Current;
    var sourceTransaction = ReadCurrentTransaction(ready.Runtime.Panel);
    var sourceSequence = (long)ReadProperty(sourceTransaction, "Sequence")!;
    var sourceIdentity = (string)ReadProperty(sourceTransaction, "Identity")!;
    var sourceOriginGeneration = (long)ReadProperty(sourceTransaction, "OriginTargetGeneration")!;
    var sourcePanelEpoch = (long)ReadProperty(sourceTransaction, "PanelEpoch")!;
    var sourceOutputEpoch = (long)ReadProperty(sourceTransaction, "OutputPanelEpoch")!;
    var outputIdentity = new Dictionary<string, nint>();
    foreach (var property in new[]
    {
        "OutputButtonPointer",
        "OutputComboPointer",
        "OutputClosurePointer",
        "OutputPointer",
    })
    {
        outputIdentity.Add(property, (nint)ReadProperty(sourceTransaction, property)!);
    }

    ready.Runtime.ResetRecipeListToAuthoritative();
    var emptyTarget = Set(sourceTarget.Generation + 1);
    Publish(emptyTarget);
    True(RuntimeTargetRecipeVariantService.InjectForTests(
        ready.Runtime.Panel,
        emptyTarget,
        9,
        RuntimeTargetRecipeVariantService.RecipeSurfaceRefreshKind.DirectRecipeField),
        "direct target refresh discarded an exact OutputReady transaction");
    var transferred = ReadCurrentTransaction(ready.Runtime.Panel);
    Same(sourceTransaction, transferred, "direct target refresh replaced the OutputReady transaction");
    Equal(sourceSequence, (long)ReadProperty(transferred, "Sequence")!,
        "direct target refresh changed the OutputReady sequence");
    Equal(sourceIdentity, (string)ReadProperty(transferred, "Identity")!,
        "direct target refresh changed the OutputReady identity");
    Equal(sourceOriginGeneration, (long)ReadProperty(transferred, "OriginTargetGeneration")!,
        "direct target refresh rewrote the OutputReady origin");
    True((long)ReadProperty(transferred, "PanelEpoch")! > sourcePanelEpoch,
        "direct target refresh did not advance the recipe-surface epoch");
    Equal(sourceOutputEpoch, (long)ReadProperty(transferred, "OutputPanelEpoch")!,
        "direct recipe refresh changed the independent output epoch");
    Equal("OutputReady", ReadProperty(transferred, "State")!.ToString()!,
        "direct recipe refresh cleared the exact output receipt");
    foreach (var pair in outputIdentity)
    {
        Equal(pair.Value, (nint)ReadProperty(transferred, pair.Key)!,
            $"direct recipe refresh changed {pair.Key}");
    }
    Same(emptyTarget, ReadProperty(ReadCurrentPanelState(ready.Runtime.Panel), "TargetSet")!,
        "direct OutputReady transfer retained the old target snapshot");
    Equal(0L, StatusCount("safety"),
        "a successful cross-target output transfer consumed the safety-event reserve");

    var businessLeasesBefore = RuntimeUiTargetPublicationLease.BusinessCreated;
    var businessDisposalsBefore = RuntimeUiTargetPublicationLease.BusinessDisposed;
    var snapshotLeasesBefore = RuntimeUiTargetPublicationLease.SnapshotCreated;
    True(RuntimeTargetRecipeVariantService.BeginSubmitForTests(ready.Button, out var outputSubmit),
        "preserved output button could not enter its exact submit");
    True(RuntimeTargetRecipeVariantService.BeginOutputClosureForTests(
        ready.Closure, out var outputClosure),
        "preserved output closure remained tied to the old target snapshot");
    Equal(businessLeasesBefore + 1, RuntimeUiTargetPublicationLease.BusinessCreated,
        "preserved output did not acquire one business-generation lease");
    Equal(businessDisposalsBefore, RuntimeUiTargetPublicationLease.BusinessDisposed,
        "preserved output released its business-generation lease before native completion");
    Equal(snapshotLeasesBefore, RuntimeUiTargetPublicationLease.SnapshotCreated,
        "preserved output incorrectly acquired an exact target snapshot lease");
    var close = RuntimeTargetRecipeVariantService.BeginPanelTeardownForTests(ready.Runtime.Panel);
    ready.Runtime.NativeClosePanel();
    RuntimeTargetRecipeVariantService.CompletePanelCloseForTests(close);
    RuntimeTargetRecipeVariantService.CompleteOutputClosureForTests(outputClosure);
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(outputSubmit);
    Equal(businessDisposalsBefore + 1, RuntimeUiTargetPublicationLease.BusinessDisposed,
        "preserved output leaked its business-generation lease");
    Equal(1L, StatusCount("completed"),
        "preserved cross-snapshot output did not complete with its exact close receipt");
    Equal(0L, StatusCount("uncertain"),
        "preserved cross-snapshot output became Uncertain");
    Equal(1, ready.Runtime.DebitCalls,
        "preserved cross-snapshot output replayed the extras debit");
    Equal(1, ready.Runtime.AddRangeCalls,
        "preserved cross-snapshot output replayed the selected-list write");

    var handoff = ReadyOutputClosure(0x39b, 221, 0x79b);
    var handoffSourceTarget = RuntimeUiPinningService.Current;
    handoff.Runtime.ResetRecipeListToAuthoritative();
    var handoffTarget = Set(
        handoffSourceTarget.Generation + 1,
        Target(
            RuntimeUiTargetKind.Rare,
            221,
            new[] { 1, 2 },
            new[] { 2 },
            "direct-output-handoff"));
    Publish(handoffTarget);
    True(RuntimeTargetRecipeVariantService.InjectForTests(
        handoff.Runtime.Panel,
        handoffTarget,
        9,
        RuntimeTargetRecipeVariantService.RecipeSurfaceRefreshKind.DirectRecipeField),
        "direct output handoff setup could not preserve OutputReady");
    var cleanupCallsBeforeHandoff = handoff.Runtime.CleanCalls;
    True(RuntimeTargetRecipeVariantService.PrepareRecipeElementEnableForTests(
        handoff.Runtime.Panel,
        handoff.Runtime.Authoritative.Single(),
        handoff.Button,
        out var handoffState),
        "a pooled output button could not become a current recipe row after direct refresh");
    RuntimeTargetRecipeVariantService.CompleteRecipeElementEnableForTests(handoffState);
    Equal(cleanupCallsBeforeHandoff + 1, handoff.Runtime.CleanCalls,
        "direct-refresh output ownership was not cleaned exactly once during recipe-row handoff");
    var handoffTransaction = ReadCurrentTransaction(handoff.Runtime.Panel);
    Equal("Applied", ReadProperty(handoffTransaction, "State")!.ToString()!,
        "recipe-row handoff retained an unusable OutputReady transaction");
    Equal((nint)0, (nint)ReadProperty(handoffTransaction, "OutputButtonPointer")!,
        "recipe-row handoff retained the old output button identity");
    Equal((nint)0, (nint)ReadProperty(handoffTransaction, "OutputClosurePointer")!,
        "recipe-row handoff retained the old output closure identity");
    Equal(0L, StatusCount("uncertain"),
        "exact recipe-row handoff made the transferred output transaction Uncertain");
}

static void VerifyMutationContextTransitions()
{
    VerifyAppliedTargetSnapshotTransfer();
    VerifyOutputReadyTargetSnapshotTransfer();

    var reentrant = PrepareMutatedTransaction(0x3af, 229, 229, "applied");
    var combo = reentrant.Runtime.Combo(0x7af, reentrant.Runtime.Authoritative.Single(), new[] { 99 });
    var button = new FakeButton(0x8af) { HasSubmitCallback = true };
    reentrant.Runtime.OnClean = () =>
    {
        var close = RuntimeTargetRecipeVariantService.BeginPanelTeardownForTests(reentrant.Runtime.Panel);
        reentrant.Runtime.NativeClosePanel();
        RuntimeTargetRecipeVariantService.CompletePanelCloseForTests(close);
    };
    True(RuntimeTargetRecipeVariantService.PrepareOutputSelectionForTests(
        reentrant.Runtime.Panel, combo, button, out var suppressed),
        "mismatched output did not defer cleanup until native return");
    reentrant.Runtime.RegisterOutputClosure(button, combo, 0x9af);
    True(RuntimeTargetRecipeVariantService.CompleteOutputSelectionForTests(
        suppressed,
        button) == null,
        "normal-return output cleanup could not preserve a reentrant close receipt");
    var transaction = ReadCurrentTransaction(reentrant.Runtime.Panel);
    Equal("Cancelled", ReadProperty(transaction, "State")!.ToString()!,
        "reentrant close did not preserve the Cancelled terminal");
    Equal((nint)0, (nint)ReadProperty(transaction, "OutputButtonPointer")!,
        "stale output blocker rewrote a Cancelled output-button field");
    Equal((nint)0, (nint)ReadProperty(transaction, "OutputComboPointer")!,
        "stale output blocker rewrote a Cancelled combo field");
    Equal(1L, StatusCount("cancelled"), "reentrant close changed the Cancelled count");
    Equal(0L, StatusCount("rejected"), "reentrant close recounted the terminal as Rejected");
    Equal(0L, StatusCount("uncertain"), "reentrant close downgraded Cancelled");
    Equal(1L, StatusCount("blocked"), "suppressed output candidate was not counted exactly once");
    Equal(0L, StatusCount("failures"), "stale output blocker changed failure counters");
}

static void VerifySyntheticSourceIdentityAndDelayedRows()
{
    var missing = Install(new FakeRecipe(0x3c1, 230, new[] { 1 }, 3));
    missing.Inventory[1] = 8;
    missing.Inventory[2] = 8;
    var missingTarget = Set(230,
        Target(RuntimeUiTargetKind.Rare, 230, new[] { 1, 2 }, new[] { 2 }, "missing-source"));
    Publish(missingTarget);
    True(RuntimeTargetRecipeVariantService.InjectForTests(missing.Panel, missingTarget, 9),
        "missing-source setup injection failed");
    var removedSynthetic = missing.Created.Single();
    var removedButton = new FakeButton(0x8c1);
    Bind(missing, removedSynthetic, removedButton);
    object removedSelection = removedSynthetic;
    True(RuntimeTargetRecipeVariantService.RouteRecipeSelectionForTests(
        missing.Panel, ref removedSelection, removedButton), "missing-source setup route failed");
    missing.Recipes.Remove(removedSynthetic);
    False(RuntimeTargetRecipeVariantService.BeginSubmitForTests(removedButton, out var removedSubmit),
        "submit preflight accepted a removed source synthetic Recipe");
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(removedSubmit);
    Equal(0, missing.DebitCalls, "removed source synthetic Recipe reached the extra debit");

    var replacedSource = Install(new FakeRecipe(0x3c5, 234, new[] { 1 }, 3));
    replacedSource.Inventory[1] = 8;
    replacedSource.Inventory[2] = 8;
    var replacedSourceTarget = Set(234,
        Target(RuntimeUiTargetKind.Rare, 234, new[] { 1, 2 }, new[] { 2 }, "replaced-source"));
    Publish(replacedSourceTarget);
    True(RuntimeTargetRecipeVariantService.InjectForTests(
        replacedSource.Panel, replacedSourceTarget, 9),
        "replaced-source setup injection failed");
    var originalSource = replacedSource.Created.Single();
    var replacedSourceButton = new FakeButton(0x8c5);
    Bind(replacedSource, originalSource, replacedSourceButton);
    object replacedSourceSelection = originalSource;
    True(RuntimeTargetRecipeVariantService.RouteRecipeSelectionForTests(
        replacedSource.Panel, ref replacedSourceSelection, replacedSourceButton),
        "replaced-source setup route failed");
    replacedSource.Recipes.Remove(originalSource);
    replacedSource.Recipes.Add(new FakeRecipe(0x9c5, 234, new[] { 1, 2 }, 3));
    False(RuntimeTargetRecipeVariantService.BeginSubmitForTests(
        replacedSourceButton, out var replacedSourceSubmit),
        "submit preflight accepted a same-ID synthetic Recipe with a different pointer");
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(replacedSourceSubmit);
    Equal(0, replacedSource.DebitCalls, "same-ID source replacement reached the extra debit");

    var corrupt = Install(new FakeRecipe(0x3c2, 231, new[] { 1 }, 3));
    corrupt.Inventory[1] = 8;
    corrupt.Inventory[2] = 8;
    var corruptTarget = Set(231,
        Target(RuntimeUiTargetKind.Rare, 231, new[] { 1, 2 }, new[] { 2 }, "corrupt-source"));
    Publish(corruptTarget);
    True(RuntimeTargetRecipeVariantService.InjectForTests(corrupt.Panel, corruptTarget, 9),
        "corrupt-source setup injection failed");
    var corruptSynthetic = corrupt.Created.Single();
    var corruptButton = new FakeButton(0x8c2);
    Bind(corrupt, corruptSynthetic, corruptButton);
    corruptSynthetic.CookCount = -2;
    object corruptSelection = corruptSynthetic;
    False(RuntimeTargetRecipeVariantService.RouteRecipeSelectionForTests(
        corrupt.Panel, ref corruptSelection, corruptButton),
        "route accepted a source synthetic Recipe with invalid CookCount");

    var rebuilt = Install(new FakeRecipe(0x3c3, 232, new[] { 1 }, 3));
    rebuilt.Inventory[1] = 8;
    rebuilt.Inventory[2] = 8;
    var rebuiltTarget = Set(232,
        Target(RuntimeUiTargetKind.Rare, 232, new[] { 1, 2 }, new[] { 2 }, "rebuilt-source"));
    Publish(rebuiltTarget);
    True(RuntimeTargetRecipeVariantService.InjectForTests(rebuilt.Panel, rebuiltTarget, 9),
        "E1 source setup injection failed");
    var firstSynthetic = rebuilt.Created.Single();
    var pooledButton = new FakeButton(0x8c3);
    Bind(rebuilt, firstSynthetic, pooledButton);
    object firstSelection = firstSynthetic;
    True(RuntimeTargetRecipeVariantService.RouteRecipeSelectionForTests(
        rebuilt.Panel, ref firstSelection, pooledButton), "E1 source setup route failed");
    True(RuntimeTargetRecipeVariantService.BeginSubmitForTests(pooledButton, out var firstSubmit),
        "E1 source submit did not arm");
    pooledButton.HasSubmitCallback = true;
    var recipeCleanCalls = rebuilt.CleanCalls;
    True(RuntimeTargetRecipeVariantService.PrepareRecipeElementEnableForTests(
        rebuilt.Panel, firstSynthetic, pooledButton, out var delayedE1Enable),
        "E1 delayed-enable token setup failed");
    rebuilt.ResetRecipeListToAuthoritative();
    rebuilt.ThrowPanelCookingState = true;
    True(RuntimeTargetRecipeVariantService.InjectForTests(
        rebuilt.Panel,
        rebuiltTarget,
        9,
        RuntimeTargetRecipeVariantService.RecipeSurfaceRefreshKind.FullVisual),
        "E2 rebuild depended on the already-consumed imported Recipe");
    var secondSynthetic = rebuilt.Created.Single();
    True(secondSynthetic.Pointer != firstSynthetic.Pointer, "E2 reused the E1 synthetic pointer");
    True(RuntimeTargetRecipeVariantService.PrepareRecipeElementEnableForTests(
        rebuilt.Panel, secondSynthetic, pooledButton, out var secondEnable),
        "fresh E2 synthetic could not replace the E1 managed binding");
    RuntimeTargetRecipeVariantService.CompleteRecipeElementEnableForTests(secondEnable);
    True(RuntimeTargetRecipeVariantService.TryResolveRecipeRowClaims(
        rebuilt.Panel, secondSynthetic, pooledButton, out var secondClaims, out var secondLease)
        && secondClaims == RuntimeUiTargetKinds.Rare,
        "fresh E2 synthetic did not establish the current exact row lease");
    True(pooledButton.HasSubmitCallback,
        "E2 recipe-row handoff cleared the native selection callback");
    True(pooledButton.Interactable,
        "E2 recipe-row handoff disabled the reused physical button");
    Equal(recipeCleanCalls, rebuilt.CleanCalls,
        "E2 recipe-row handoff entered the output-only callback cleaner");
    rebuilt.ThrowPanelCookingState = false;

    rebuilt.OnReadPanelSelectionState = () =>
        ToggleButtonBindingStateVersion(pooledButton.Pointer);
    False(RuntimeTargetRecipeVariantService.PrepareRecipeElementEnableForTests(
        rebuilt.Panel, secondSynthetic, pooledButton, out _),
        "same-object binding state ABA replaced the current E2 row lease");
    True(RuntimeTargetRecipeVariantService.TryValidateRecipeRowClaims(secondLease, out _),
        "same-object binding state ABA invalidated the current E2 row lease");

    RuntimeTargetRecipeVariantService.CompleteRecipeElementEnableForTests(delayedE1Enable);
    True(RuntimeTargetRecipeVariantService.TryValidateRecipeRowClaims(secondLease, out var delayedClaims)
        && delayedClaims == RuntimeUiTargetKinds.Rare,
        "delayed E1 enable completion replaced the fresh E2 row lease");

    rebuilt.ThrowRecipeSnapshots.Add(firstSynthetic.Pointer);
    False(RuntimeTargetRecipeVariantService.PrepareRecipeElementEnableForTests(
        rebuilt.Panel, firstSynthetic, pooledButton, out _),
        "stale E1 synthetic snapshot failure fell through as a current native row");
    True(RuntimeTargetRecipeVariantService.TryValidateRecipeRowClaims(secondLease, out _),
        "stale E1 synthetic input replaced the fresh E2 row lease");
    True(pooledButton.HasSubmitCallback,
        "stale E1 synthetic input cleared the fresh physical callback");
    True(pooledButton.Interactable,
        "stale E1 synthetic input disabled the fresh physical button");
    Equal(recipeCleanCalls, rebuilt.CleanCalls,
        "stale E1 synthetic input entered the output-only callback cleaner");
    object staleSelection = firstSynthetic;
    False(RuntimeTargetRecipeVariantService.RouteRecipeSelectionForTests(
        rebuilt.Panel, ref staleSelection, pooledButton),
        "stale E1 selection snapshot failure fell through to native");
    True(RuntimeTargetRecipeVariantService.TryValidateRecipeRowClaims(secondLease, out _),
        "stale E1 selection snapshot failure tombstoned the fresh E2 lease");
    rebuilt.ThrowRecipeSnapshots.Remove(firstSynthetic.Pointer);

    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(firstSubmit);
    object secondSelection = secondSynthetic;
    True(RuntimeTargetRecipeVariantService.RouteRecipeSelectionForTests(
        rebuilt.Panel, ref secondSelection, pooledButton), "E2 source route failed");
    True(RuntimeTargetRecipeVariantService.BeginSubmitForTests(pooledButton, out var secondSubmit),
        "E2 source submit did not arm");
    True(secondSubmit.TransactionSequence > firstSubmit.TransactionSequence,
        "E2 reused the E1 pending transaction sequence");
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(
        firstSubmit,
        new InvalidOperationException("late E1 token"));
    rebuilt.NativeImportBase(rebuilt.Authoritative.Single());
    RuntimeTargetRecipeVariantService.ApplyExtrasForTests(rebuilt.Panel, out var visual);
    RuntimeTargetRecipeVariantService.CompleteUpdateVisualForTests(visual);
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(secondSubmit);
    Equal(1, rebuilt.DebitCalls, "late E1 token corrupted the E2 transaction");

    var outputReuse = ReadyOutputClosure(0x3c4, 233, 0x6c4);
    var close = RuntimeTargetRecipeVariantService.BeginPanelTeardownForTests(outputReuse.Runtime.Panel);
    outputReuse.Runtime.NativeClosePanel();
    RuntimeTargetRecipeVariantService.CompletePanelCloseForTests(close);
    outputReuse.Runtime.ResetRecipeListToAuthoritative();
    var outputTarget = RuntimeUiPinningService.Current;
    True(RuntimeTargetRecipeVariantService.InjectForTests(
        outputReuse.Runtime.Panel,
        outputTarget,
        9,
        RuntimeTargetRecipeVariantService.RecipeSurfaceRefreshKind.FullVisual),
        "output-button reuse panel rebuild failed");
    var outputReuseCleanCalls = outputReuse.Runtime.CleanCalls;
    True(RuntimeTargetRecipeVariantService.PrepareRecipeElementEnableForTests(
        outputReuse.Runtime.Panel,
        outputReuse.Runtime.Authoritative.Single(),
        outputReuse.Button,
        out var authoritativeEnable),
        "clean output button could not be reused by the authoritative base row");
    False(outputReuse.Button.HasSubmitCallback,
        "recipe-row handoff retained the exact prior Mod output callback");
    Equal(outputReuseCleanCalls + 1, outputReuse.Runtime.CleanCalls,
        "recipe-row handoff did not use the output-owned callback cleaner exactly once");
    True(outputReuse.Button.Interactable,
        "recipe-row enable disabled a button outside its native ownership");
    RuntimeTargetRecipeVariantService.CompleteRecipeElementEnableForTests(authoritativeEnable);

    var changedCallback = ReadyOutputClosure(0x3c6, 235, 0x6c6);
    var changedClose = RuntimeTargetRecipeVariantService.BeginPanelTeardownForTests(
        changedCallback.Runtime.Panel);
    changedCallback.Runtime.NativeClosePanel();
    RuntimeTargetRecipeVariantService.CompletePanelCloseForTests(changedClose);
    changedCallback.Runtime.ResetRecipeListToAuthoritative();
    var changedTarget = RuntimeUiPinningService.Current;
    True(RuntimeTargetRecipeVariantService.InjectForTests(
        changedCallback.Runtime.Panel,
        changedTarget,
        9,
        RuntimeTargetRecipeVariantService.RecipeSurfaceRefreshKind.FullVisual),
        "changed-callback output-button reuse rebuild failed");
    changedCallback.Button.SubmitClosure = null;
    changedCallback.Button.HasSubmitCallback = true;
    var changedCleanCalls = changedCallback.Runtime.CleanCalls;
    True(RuntimeTargetRecipeVariantService.PrepareRecipeElementEnableForTests(
        changedCallback.Runtime.Panel,
        changedCallback.Runtime.Authoritative.Single(),
        changedCallback.Button,
        out var changedEnable),
        "recipe row was blocked after the callback had changed away from the old output closure");
    Equal(changedCleanCalls, changedCallback.Runtime.CleanCalls,
        "recipe-row handoff cleaned a callback that was no longer the exact old output closure");
    True(changedCallback.Button.HasSubmitCallback,
        "recipe-row handoff touched the replacement native recipe callback");
    RuntimeTargetRecipeVariantService.CompleteRecipeElementEnableForTests(changedEnable);

    var unrelatedReuse = Install(new FakeRecipe(0x3c7, 236, new[] { 1 }, 3));
    unrelatedReuse.Inventory[1] = 8;
    unrelatedReuse.Inventory[2] = 8;
    var unrelatedTarget = Set(236,
        Target(RuntimeUiTargetKind.Rare, 236, new[] { 1, 2 }, new[] { 2 }, "unrelated-reuse"));
    Publish(unrelatedTarget);
    True(RuntimeTargetRecipeVariantService.InjectForTests(
        unrelatedReuse.Panel, unrelatedTarget, 9),
        "unrelated native-row reuse injection failed");
    var unrelatedSynthetic = unrelatedReuse.Created.Single();
    var unrelatedButton = new FakeButton(0x8c7) { HasSubmitCallback = true };
    Bind(unrelatedReuse, unrelatedSynthetic, unrelatedButton);
    True(RuntimeTargetRecipeVariantService.TryResolveRecipeRowClaims(
        unrelatedReuse.Panel,
        unrelatedSynthetic,
        unrelatedButton,
        out _,
        out var unrelatedLease),
        "unrelated native-row reuse setup lease was missing");
    var unrelatedNative = new FakeRecipe(0xac7, 999, new[] { 4 }, 2);
    unrelatedReuse.Recipes.Add(unrelatedNative);
    var unrelatedCleanCalls = unrelatedReuse.CleanCalls;
    True(RuntimeTargetRecipeVariantService.PrepareRecipeElementEnableForTests(
        unrelatedReuse.Panel,
        unrelatedNative,
        unrelatedButton,
        out _),
        "current unrelated native recipe was mistaken for a delayed synthetic row");
    False(RuntimeTargetRecipeVariantService.TryValidateRecipeRowClaims(unrelatedLease, out _),
        "unrelated native recipe reuse retained the old Mod row lease");
    True(unrelatedButton.HasSubmitCallback,
        "unrelated native recipe reuse cleared the native recipe callback");
    True(unrelatedButton.Interactable,
        "unrelated native recipe reuse disabled the pooled button");
    Equal(unrelatedCleanCalls, unrelatedReuse.CleanCalls,
        "unrelated native recipe reuse entered output callback cleanup");
}

static void VerifyOutputClosureEntryExceptionCleanup()
{
    foreach (var configure in new Action<FakeRuntime>[]
    {
        runtime => runtime.ThrowWrapPanel = true,
        runtime => runtime.ThrowPanelSelectionState = true,
    })
    {
        var ready = ReadyOutputClosure(0x3d1, 240, 0x6d1);
        True(RuntimeTargetRecipeVariantService.BeginSubmitForTests(ready.Button, out var submit),
            "throwing final-output probe could not enter its button submit");
        configure(ready.Runtime);
        var created = RuntimeUiTargetPublicationLease.Created;
        var disposed = RuntimeUiTargetPublicationLease.Disposed;
        False(RuntimeTargetRecipeVariantService.BeginOutputClosureForTests(
            ready.Closure, out var closureState),
            "throwing final-output runtime probe fell through to the native closure");
        Equal(created + 1, RuntimeUiTargetPublicationLease.Created,
            "throwing final-output probe did not acquire exactly one lease");
        Equal(disposed + 1, RuntimeUiTargetPublicationLease.Disposed,
            "throwing final-output probe leaked its acquired lease");
        Equal(1L, StatusCount("uncertain"),
            "throwing final-output probe did not latch the known transaction Uncertain");
        RuntimeTargetRecipeVariantService.CompleteOutputClosureForTests(closureState);
        Equal(disposed + 1, RuntimeUiTargetPublicationLease.Disposed,
            "prefix=false finalizer disposed the recovered lease twice");
        RuntimeTargetRecipeVariantService.CompleteSubmitForTests(submit);
        False(RuntimeTargetRecipeVariantService.BeginSubmitForTests(ready.Button, out var retry),
            "throwing final-output probe released its exact callback tombstone");
        RuntimeTargetRecipeVariantService.CompleteSubmitForTests(retry);
    }
}

static void VerifyBusinessBoundaryTransactions()
{
    foreach (var phase in new[] { "applied", "output-ready", "output-submitting" })
    {
        var prepared = PrepareMutatedTransaction(
            (nint)(0x3e0 + phase.Length),
            250 + phase.Length,
            250 + phase.Length,
            phase);
        RuntimeTargetRecipeVariantService.RetireForBusinessBoundary(
            9,
            $"test {phase} closing boundary");
        Equal(1L, StatusCount("uncertain"),
            $"{phase} business boundary lost the no-replay state");
        True(RuntimeTargetRecipeVariantService.Status.Contains("panels=0", StringComparison.Ordinal),
            $"{phase} business boundary retained its panel identity");
        True(RuntimeTargetRecipeVariantService.Status.Contains("buttons=0", StringComparison.Ordinal),
            $"{phase} business boundary retained button identities");
        True(RuntimeTargetRecipeVariantService.Status.Contains("closures=0", StringComparison.Ordinal),
            $"{phase} business boundary retained output-closure identities");
        if (prepared.OutputSubmit != null)
        {
            RuntimeTargetRecipeVariantService.CompleteOutputClosureForTests(prepared.OutputClosureState!);
            RuntimeTargetRecipeVariantService.CompleteSubmitForTests(prepared.OutputSubmit);
            Equal(1L, StatusCount("uncertain"),
                $"{phase} late finalizer recounted the retired uncertain transaction");
        }
    }

    var pending = Install(new FakeRecipe(0x3f1, 270, new[] { 1 }, 2));
    pending.Inventory[1] = 5;
    pending.Inventory[2] = 5;
    var pendingTarget = Set(270,
        Target(RuntimeUiTargetKind.Rare, 270, new[] { 1, 2 }, new[] { 2 }, "pending-boundary"));
    Publish(pendingTarget);
    True(RuntimeTargetRecipeVariantService.InjectForTests(pending.Panel, pendingTarget, 9),
        "pending boundary setup injection failed");
    var pendingRecipe = pending.Created.Single();
    var pendingButton = new FakeButton(0x8f2);
    Bind(pending, pendingRecipe, pendingButton);
    object pendingSelection = pendingRecipe;
    True(RuntimeTargetRecipeVariantService.RouteRecipeSelectionForTests(
        pending.Panel, ref pendingSelection, pendingButton), "pending boundary setup route failed");
    RuntimeTargetRecipeVariantService.RetireForBusinessBoundary(9, "test unmutated boundary");
    Equal(0L, StatusCount("uncertain"), "unmutated pending boundary became Uncertain");
    True(RuntimeTargetRecipeVariantService.Status.Contains("panels=0", StringComparison.Ordinal),
        "unmutated pending boundary retained its panel");

    var cancelled = PrepareMutatedTransaction(0x3f4, 273, 273, "applied");
    var cancelledClose = RuntimeTargetRecipeVariantService.BeginPanelTeardownForTests(
        cancelled.Runtime.Panel);
    cancelled.Runtime.NativeClosePanel();
    RuntimeTargetRecipeVariantService.CompletePanelCloseForTests(cancelledClose);
    RuntimeTargetRecipeVariantService.RetireForBusinessBoundary(9, "test cancelled boundary");
    Equal(1L, StatusCount("cancelled"), "business boundary changed the Cancelled terminal");
    Equal(0L, StatusCount("uncertain"), "business boundary downgraded Cancelled");
    True(RuntimeTargetRecipeVariantService.Status.Contains("panels=0", StringComparison.Ordinal),
        "business boundary retained a Cancelled panel state");

    var terminal = ReadyOutputClosure(0x3f2, 271, 0x6f2);
    True(RuntimeTargetRecipeVariantService.BeginSubmitForTests(terminal.Button, out var terminalSubmit),
        "terminal boundary setup submit failed");
    True(RuntimeTargetRecipeVariantService.BeginOutputClosureForTests(
        terminal.Closure, out var terminalClosure), "terminal boundary setup closure failed");
    var terminalClose = RuntimeTargetRecipeVariantService.BeginPanelTeardownForTests(terminal.Runtime.Panel);
    terminal.Runtime.NativeClosePanel();
    RuntimeTargetRecipeVariantService.CompletePanelCloseForTests(terminalClose);
    RuntimeTargetRecipeVariantService.CompleteOutputClosureForTests(terminalClosure);
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(terminalSubmit);
    RuntimeTargetRecipeVariantService.RetireForBusinessBoundary(9, "test completed boundary");
    Equal(1L, StatusCount("completed"), "business boundary changed a Completed transaction");
    Equal(0L, StatusCount("uncertain"), "business boundary downgraded a Completed transaction");
    True(RuntimeTargetRecipeVariantService.Status.Contains("panels=0", StringComparison.Ordinal),
        "business boundary retained a Completed panel state");

    var transferred = ReadyOutputClosure(0x3f5, 274, 0x6f5);
    var transferredTarget = RuntimeUiPinningService.Current;
    var oldClosure = transferred.Closure;
    transferred.Runtime.ResetRecipeListToAuthoritative();
    True(RuntimeTargetRecipeVariantService.InjectForTests(
        transferred.Runtime.Panel,
        transferredTarget,
        9,
        RuntimeTargetRecipeVariantService.RecipeSurfaceRefreshKind.FullVisual),
        "business-boundary transfer setup failed");
    False(RuntimeTargetRecipeVariantService.BeginOutputClosureForTests(
        oldClosure,
        out var retainedOldClosure),
        "epoch transfer removed the old closure before the business boundary");
    RuntimeTargetRecipeVariantService.CompleteOutputClosureForTests(retainedOldClosure);
    RuntimeTargetRecipeVariantService.RetireForBusinessBoundary(9, "test transferred boundary");
    True(RuntimeTargetRecipeVariantService.BeginOutputClosureForTests(
        oldClosure,
        out var releasedOldClosure),
        "business boundary retained the prior-epoch output-closure tombstone");
    RuntimeTargetRecipeVariantService.CompleteOutputClosureForTests(releasedOldClosure);

    var reused = ReadyOutputClosure(0x3f6, 275, 0x6f6, businessGeneration: 10);
    RuntimeTargetRecipeVariantService.RetireForBusinessBoundary(9, "delayed prior-generation boundary");
    True(RuntimeTargetRecipeVariantService.Status.Contains("panels=1", StringComparison.Ordinal),
        "a delayed prior-generation boundary removed the current reused panel");
    True(RuntimeTargetRecipeVariantService.Status.Contains("mutationLatch=10:False", StringComparison.Ordinal),
        "a delayed prior-generation boundary latched the current business uncertain");
    True(RuntimeTargetRecipeVariantService.BeginSubmitForTests(reused.Button, out var reusedSubmit),
        "a delayed prior-generation boundary tombstoned the current output button");
    True(RuntimeTargetRecipeVariantService.BeginOutputClosureForTests(
        reused.Closure, out var reusedClosure),
        "a delayed prior-generation boundary removed the current output closure");
    var reusedClose = RuntimeTargetRecipeVariantService.BeginPanelTeardownForTests(reused.Runtime.Panel);
    reused.Runtime.NativeClosePanel();
    RuntimeTargetRecipeVariantService.CompletePanelCloseForTests(reusedClose);
    RuntimeTargetRecipeVariantService.CompleteOutputClosureForTests(reusedClosure);
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(reusedSubmit);
    Equal(1L, StatusCount("completed"),
        "the current reused transaction did not complete after a delayed old boundary");
}

static void VerifyTransactionSequenceAndNestedProbeStack()
{
    var runtime = Install(new FakeRecipe(0x191, 120, new[] { 1 }, 4));
    runtime.Inventory[1] = 8;
    runtime.Inventory[2] = 8;
    var target = Set(120,
        Target(RuntimeUiTargetKind.Rare, 120, new[] { 1, 2 }, new[] { 2 }, "aba"));
    Publish(target);
    True(RuntimeTargetRecipeVariantService.InjectForTests(runtime.Panel, target, 9), "ABA setup failed");
    var synthetic = runtime.Created.Single();
    var syntheticButton = new FakeButton(0x291);
    var baseButton = new FakeButton(0x292);
    Bind(runtime, synthetic, syntheticButton);
    Bind(runtime, runtime.Authoritative.Single(), baseButton);
    object firstSelection = synthetic;
    True(RuntimeTargetRecipeVariantService.RouteRecipeSelectionForTests(
        runtime.Panel, ref firstSelection, syntheticButton), "first ABA selection failed");
    True(RuntimeTargetRecipeVariantService.BeginSubmitForTests(syntheticButton, out var firstSubmit),
        "first ABA submit failed");
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(firstSubmit);
    object baseSelection = runtime.Authoritative.Single();
    True(RuntimeTargetRecipeVariantService.RouteRecipeSelectionForTests(
        runtime.Panel, ref baseSelection, baseButton), "unmutated ABA transaction did not retire");
    object secondSelection = synthetic;
    True(RuntimeTargetRecipeVariantService.RouteRecipeSelectionForTests(
        runtime.Panel, ref secondSelection, syntheticButton), "second ABA selection failed");
    True(RuntimeTargetRecipeVariantService.BeginSubmitForTests(syntheticButton, out var secondSubmit),
        "second ABA submit failed");
    True(secondSubmit.TransactionSequence > firstSubmit.TransactionSequence,
        "same-pointer/same-plan transactions reused a sequence");
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(
        firstSubmit,
        new InvalidOperationException("late first transaction"));
    runtime.NativeImportBase(runtime.Authoritative.Single());
    RuntimeTargetRecipeVariantService.ApplyExtrasForTests(runtime.Panel, out var secondVisual);
    RuntimeTargetRecipeVariantService.CompleteUpdateVisualForTests(secondVisual);
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(secondSubmit);
    Equal(1, runtime.DebitCalls, "late first token corrupted the second transaction");

    var nestedRuntime = Install(new FakeRecipe(0x192, 121, new[] { 1 }, 2));
    True(RuntimeTargetRecipeVariantService.BeginSubmitForTests(new FakeButton(0x293), out var ordinaryOuter),
        "ordinary outer submit was blocked");
    True(RuntimeTargetRecipeVariantService.BeginSubmitForTests(new FakeButton(0x294), out var ordinaryInner),
        "ordinary nested submit was blocked by a scalar ThreadStatic probe");
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(ordinaryInner);
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(ordinaryOuter);

    nestedRuntime.Inventory[1] = 5;
    nestedRuntime.Inventory[2] = 5;
    var nestedTarget = Set(121,
        Target(RuntimeUiTargetKind.Rare, 121, new[] { 1, 2 }, new[] { 2 }, "nested"));
    Publish(nestedTarget);
    True(RuntimeTargetRecipeVariantService.InjectForTests(nestedRuntime.Panel, nestedTarget, 9),
        "tracked nested setup failed");
    var trackedButton = new FakeButton(0x295);
    Bind(nestedRuntime, nestedRuntime.Created.Single(), trackedButton);
    True(RuntimeTargetRecipeVariantService.BeginSubmitForTests(trackedButton, out var trackedOuter),
        "tracked outer submit failed");
    False(RuntimeTargetRecipeVariantService.BeginSubmitForTests(new FakeButton(0x296), out var blockedInner),
        "tracked to ordinary nested submit was allowed");
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(blockedInner);
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(trackedOuter);

    var leaseRuntime = Install(new FakeRecipe(0x193, 122, new[] { 1 }, 2));
    var leaseTarget = Set(122,
        Target(RuntimeUiTargetKind.Rare, 122, new[] { 1, 2 }, new[] { 2 }, "lease"));
    Publish(leaseTarget);
    True(RuntimeTargetRecipeVariantService.InjectForTests(leaseRuntime.Panel, leaseTarget, 9),
        "lease cleanup setup failed");
    var leaseButton = new FakeButton(0x297);
    Bind(leaseRuntime, leaseRuntime.Created.Single(), leaseButton);
    False(RuntimeTargetRecipeVariantService.BeginSubmitForTests(leaseButton, out var rejectedPrefix),
        "insufficient inventory unexpectedly armed a tracked submit");
    Equal(1, RuntimeUiTargetPublicationLease.Created, "rejected prefix did not acquire its publication lease");
    Equal(1, RuntimeUiTargetPublicationLease.Disposed, "rejected prefix leaked its publication lease");
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(rejectedPrefix);
    True(RuntimeTargetRecipeVariantService.BeginSubmitForTests(new FakeButton(0x298), out var afterRejected),
        "prefix=false finalizer did not pop its exact submit probe");
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(afterRejected);
}

static void VerifyOwnershipFailurePolicyAndCallbackCleanup()
{
    var syntheticRuntime = Install(new FakeRecipe(0x1a1, 130, new[] { 1 }, 2));
    syntheticRuntime.Inventory[1] = 4;
    syntheticRuntime.Inventory[2] = 4;
    var target = Set(130,
        Target(RuntimeUiTargetKind.Rare, 130, new[] { 1, 2 }, new[] { 2 }, "ownership"));
    Publish(target);
    True(RuntimeTargetRecipeVariantService.InjectForTests(syntheticRuntime.Panel, target, 9),
        "ownership setup failed");
    var synthetic = syntheticRuntime.Created.Single();
    syntheticRuntime.ThrowRecipeSnapshots.Add(synthetic.Pointer);
    var syntheticButton = new FakeButton(0x2a1);
    False(RuntimeTargetRecipeVariantService.PrepareRecipeElementEnableForTests(
        syntheticRuntime.Panel, synthetic, syntheticButton, out _),
        "known synthetic snapshot exception fell through to native");
    True(syntheticButton.Interactable,
        "known synthetic snapshot exception changed native interactability");
    Equal(0, syntheticRuntime.CleanCalls,
        "known synthetic snapshot exception entered the output-only callback cleaner");

    var baseRuntime = Install(new FakeRecipe(0x1a2, 131, new[] { 1 }, 2));
    baseRuntime.Inventory[1] = 4;
    baseRuntime.Inventory[2] = 4;
    var baseTarget = Set(131,
        Target(RuntimeUiTargetKind.Rare, 131, new[] { 1, 2 }, new[] { 2 }, "base-fail-open"));
    Publish(baseTarget);
    True(RuntimeTargetRecipeVariantService.InjectForTests(baseRuntime.Panel, baseTarget, 9),
        "base fail-open setup failed");
    baseRuntime.ThrowRecipeSnapshots.Add(baseRuntime.Authoritative.Single().Pointer);
    var baseButton = new FakeButton(0x2a2);
    True(RuntimeTargetRecipeVariantService.PrepareRecipeElementEnableForTests(
        baseRuntime.Panel, baseRuntime.Authoritative.Single(), baseButton, out _),
        "authoritative snapshot exception disabled the native base row");
    True(baseButton.Interactable, "authoritative snapshot exception changed native interactability");

    var unknownRuntime = Install(new FakeRecipe(0x1a3, 132, new[] { 1 }, 2));
    unknownRuntime.Inventory[1] = 4;
    unknownRuntime.Inventory[2] = 4;
    var unknownTarget = Set(132,
        Target(RuntimeUiTargetKind.Rare, 132, new[] { 1, 2 }, new[] { 2 }, "unknown"));
    Publish(unknownTarget);
    True(RuntimeTargetRecipeVariantService.InjectForTests(unknownRuntime.Panel, unknownTarget, 9),
        "unknown same-id setup failed");
    var nativeUnknown = new FakeRecipe(0x7a3, 132, new[] { 9 }, 1);
    unknownRuntime.Recipes.Add(nativeUnknown);
    True(RuntimeTargetRecipeVariantService.PrepareRecipeElementEnableForTests(
        unknownRuntime.Panel, nativeUnknown, new FakeButton(0x2a3), out _),
        "unknown same-ID native row was treated as Mod-owned synthetic");
    False(RuntimeTargetRecipeVariantService.PrepareRecipeElementEnableForTests(
        unknownRuntime.Panel, unknownRuntime.Created.Single(), new FakeButton(0x2a4), out _),
        "retired panel allowed a known synthetic row to fall through");

    VerifyOutputRegistrationIdentityCleanup(0x1a4, closure => closure.PanelPointer++,
        "closure panel drift did not clear the just-installed callback");
    VerifyOutputRegistrationIdentityCleanup(0x1a5, closure => closure.ComboPointer++,
        "closure combo drift did not clear the just-installed callback");

    var priorCleanupThrow = ReadyOutputClosure(0x1ab, 140, 0x4ab);
    var wrongPriorCombo = priorCleanupThrow.Runtime.Combo(
        0x7ab, priorCleanupThrow.Runtime.Authoritative.Single(), new[] { 99 });
    var priorCleanCalls = priorCleanupThrow.Runtime.CleanCalls;
    priorCleanupThrow.Runtime.ThrowClean = true;
    True(RuntimeTargetRecipeVariantService.PrepareOutputSelectionForTests(
        priorCleanupThrow.Runtime.Panel,
        wrongPriorCombo,
        priorCleanupThrow.Button,
        out var suppressedPrior),
        "mismatched output did not run native OnOutputSelected before post-clean");
    Equal(priorCleanCalls, priorCleanupThrow.Runtime.CleanCalls,
        "mismatched output cleaned the old callback in the prefix");
    priorCleanupThrow.Runtime.RegisterOutputClosure(
        priorCleanupThrow.Button,
        wrongPriorCombo,
        0x7bb);
    var suppressedFailure = RuntimeTargetRecipeVariantService.CompleteOutputSelectionForTests(
        suppressedPrior,
        priorCleanupThrow.Button);
    True(suppressedFailure is InvalidOperationException,
        "throwing mismatch post-clean did not abort the outer submit");
    False(priorCleanupThrow.Button.Interactable,
        "throwing mismatch post-clean did not disable the unsafe button");
    Equal("Uncertain", ReadProperty(
        ReadCurrentTransaction(priorCleanupThrow.Runtime.Panel), "State")!.ToString()!,
        "throwing prior cleanup was treated as a harmless combo mismatch");
    Equal(1L, StatusCount("uncertain"),
        "throwing prior cleanup did not count Uncertain exactly once");
    True(RuntimeTargetRecipeVariantService.Status.Contains(
        "mutationLatch=9:True", StringComparison.Ordinal),
        "throwing prior cleanup did not latch the current business");
    False(priorCleanupThrow.Runtime.RuntimeCallObservedUnderLock,
        "throwing mismatch post-clean called runtime code under StateRoot");
    Equal(1, priorCleanupThrow.Runtime.DebitCalls,
        "throwing prior cleanup replayed the extras debit");
    Equal(1, priorCleanupThrow.Runtime.AddRangeCalls,
        "throwing prior cleanup replayed selected extras");

    var cleanup = OutputRegistrationPending(0x1a6, 135);
    cleanup.Closure.PanelPointer++;
    cleanup.Runtime.FailClean = true;
    var cleanupFailure = RuntimeTargetRecipeVariantService.CompleteOutputSelectionForTests(
        cleanup.State, cleanup.Button);
    True(cleanupFailure is InvalidOperationException,
        "cleanup failure did not abort the behavior-0 outer submit");
    True(cleanup.Button.HasSubmitCallback, "fake cleanup failure unexpectedly removed the callback");
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(cleanup.Submit, cleanupFailure);
    False(RuntimeTargetRecipeVariantService.BeginSubmitForTests(cleanup.Button, out var cleanupRetry),
        "failed callback cleanup released its exact output-button tombstone");
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(cleanupRetry);

    var cleanupThrow = OutputRegistrationPending(0x1a8, 137);
    cleanupThrow.Closure.PanelPointer++;
    cleanupThrow.Runtime.ThrowClean = true;
    var cleanupThrown = RuntimeTargetRecipeVariantService.CompleteOutputSelectionForTests(
        cleanupThrow.State, cleanupThrow.Button);
    True(cleanupThrown is InvalidOperationException,
        "callback cleanup throw without a native exception did not abort behavior-0");
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(cleanupThrow.Submit, cleanupThrown);
    False(RuntimeTargetRecipeVariantService.BeginSubmitForTests(cleanupThrow.Button, out var cleanupThrowRetry),
        "throwing callback cleanup released its exact output-button tombstone");
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(cleanupThrowRetry);

    var preserve = OutputRegistrationPending(0x1a7, 136);
    preserve.Runtime.ThrowClean = true;
    var nativeException = new InvalidOperationException("native output selection failed");
    var preserved = RuntimeTargetRecipeVariantService.CompleteOutputSelectionForTests(
        preserve.State, preserve.Button, nativeException);
    Same(nativeException, preserved!, "Mod cleanup exception replaced the native exception");
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(preserve.Submit, preserved);

    var boundaryCleanup = ReadyOutputClosure(0x1a9, 138, 0x4a9);
    RuntimeTargetRecipeVariantService.RetireForBusinessBoundary(
        9,
        "test output-closure ownership boundary");
    True(RuntimeTargetRecipeVariantService.BeginOutputClosureForTests(
        boundaryCleanup.Closure, out var releasedClosure),
        "business boundary retained its exact output-closure mapping");
    RuntimeTargetRecipeVariantService.CompleteOutputClosureForTests(releasedClosure);
}

static void VerifyOutputRegistrationIdentityCleanup(
    nint basePointer,
    Action<FakeClosure> mutate,
    string message)
{
    var pending = OutputRegistrationPending(basePointer, 140 + (int)(basePointer & 0xf));
    mutate(pending.Closure);
    var result = RuntimeTargetRecipeVariantService.CompleteOutputSelectionForTests(
        pending.State, pending.Button);
    True(result == null, message);
    False(pending.Button.HasSubmitCallback, message);
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(pending.Submit);
}

static (FakeRuntime Runtime, FakeButton Button, FakeClosure Closure,
    RuntimeTargetRecipeVariantService.OutputHookState State,
    RuntimeTargetRecipeVariantService.SubmitHookState Submit) OutputRegistrationPending(
    nint basePointer,
    int recipeId)
{
    var (runtime, _, _, _, recipeSubmit) = AppliedTransaction(basePointer, recipeId, recipeId);
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(recipeSubmit);
    var combo = runtime.Combo(basePointer + 0x600, runtime.Authoritative.Single(), new[] { 2 });
    var button = new FakeButton(basePointer + 0x500);
    True(RuntimeTargetRecipeVariantService.BeginSubmitForTests(button, out var submit),
        "pending output registration could not establish submit probe");
    True(RuntimeTargetRecipeVariantService.PrepareOutputSelectionForTests(
        runtime.Panel, combo, button, out var state), "pending output registration was blocked");
    var closure = runtime.RegisterOutputClosure(button, combo, basePointer + 0x700);
    return (runtime, button, closure, state, submit);
}

static void VerifyPostNativeOutputCallbackOwnership()
{
    var alreadyClean = ReadyOutputClosure(0x1ad, 142, 0x4ad);
    var wrongCombo = alreadyClean.Runtime.Combo(
        0x7ad,
        alreadyClean.Runtime.Authoritative.Single(),
        new[] { 99 });
    var candidate = new FakeButton(0x8ad) { HasSubmitCallback = true };
    var cleanCalls = alreadyClean.Runtime.CleanCalls;
    True(RuntimeTargetRecipeVariantService.PrepareOutputSelectionForTests(
        alreadyClean.Runtime.Panel,
        wrongCombo,
        candidate,
        out var suppressed),
        "mismatched output did not allow native callback ownership");
    True(candidate.HasSubmitCallback,
        "mismatched output prefix cleared the prior native callback");
    Equal(cleanCalls, alreadyClean.Runtime.CleanCalls,
        "mismatched output prefix entered native callback cleanup");

    // Simulate OnOutputSelected's invalid branch: the game has already cleaned the slot.
    candidate.HasSubmitCallback = false;
    candidate.SubmitClosure = null;
    True(RuntimeTargetRecipeVariantService.CompleteOutputSelectionForTests(
        suppressed,
        candidate) == null,
        "already-clean native invalid branch was not accepted by the finalizer");
    Equal(cleanCalls + 1, alreadyClean.Runtime.CleanCalls,
        "normal-return finalizer did not verify cleanup exactly once");
    Equal("OutputReady", ReadProperty(
        ReadCurrentTransaction(alreadyClean.Runtime.Panel), "State")!.ToString()!,
        "harmless mismatched candidate invalidated a different exact output");
    Equal(0L, StatusCount("uncertain"),
        "already-clean mismatched candidate latched Uncertain");

    var nativeFailure = ReadyOutputClosure(0x1ae, 143, 0x4ae);
    var failedCombo = nativeFailure.Runtime.Combo(
        0x7ae,
        nativeFailure.Runtime.Authoritative.Single(),
        new[] { 99 });
    var failedCandidate = new FakeButton(0x6ae) { HasSubmitCallback = true };
    var failureCleanCalls = nativeFailure.Runtime.CleanCalls;
    True(RuntimeTargetRecipeVariantService.PrepareOutputSelectionForTests(
        nativeFailure.Runtime.Panel,
        failedCombo,
        failedCandidate,
        out var failedSuppression),
        "native-exception suppression setup was blocked before OnOutputSelected");
    var nativeException = new InvalidOperationException("native output selection failed before ownership");
    var preserved = RuntimeTargetRecipeVariantService.CompleteOutputSelectionForTests(
        failedSuppression,
        failedCandidate,
        nativeException);
    Same(nativeException, preserved!,
        "output finalizer replaced the original native exception");
    Equal(failureCleanCalls, nativeFailure.Runtime.CleanCalls,
        "output finalizer cleaned an unknown callback after native exception");
    True(failedCandidate.HasSubmitCallback,
        "output finalizer touched the unknown callback after native exception");
    False(failedCandidate.Interactable,
        "native-exception output candidate remained interactable");
    Equal("Uncertain", ReadProperty(
        ReadCurrentTransaction(nativeFailure.Runtime.Panel), "State")!.ToString()!,
        "native output exception did not fail the mutated transaction closed");
}

static void VerifyRuntimeCallsStayOutsideServiceLock()
{
    var (runtime, _, _, _, recipeSubmit) = AppliedTransaction(0x1b1, 150, 150);
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(recipeSubmit);
    var combo = runtime.Combo(0x3b1, runtime.Authoritative.Single(), new[] { 99 });
    var button = new FakeButton(0x6b1) { HasSubmitCallback = true };
    runtime.OnClean = () =>
    {
        var close = RuntimeTargetRecipeVariantService.BeginPanelTeardownForTests(runtime.Panel);
        RuntimeTargetRecipeVariantService.CompletePanelCloseForTests(close);
    };
    True(RuntimeTargetRecipeVariantService.PrepareOutputSelectionForTests(
        runtime.Panel, combo, button, out var suppressed),
        "mismatched output did not defer cleanup until the normal-return finalizer");
    runtime.RegisterOutputClosure(button, combo, 0x7b1);
    True(RuntimeTargetRecipeVariantService.CompleteOutputSelectionForTests(
        suppressed,
        button) == null,
        $"reentrant post-clean did not preserve the native close receipt; clean={runtime.CleanCalls}; status={RuntimeTargetRecipeVariantService.Status}");
    False(runtime.RuntimeCallObservedUnderLock,
        "output failure called native cleanup/disable while StateRoot was held");
}

static (FakeRuntime Runtime, FakeButton Button, FakeClosure Closure) ReadyOutputClosure(
    nint basePointer,
    int recipeId,
    nint closurePointer,
    long businessGeneration = 9)
{
    var (runtime, _, _, _, recipeSubmit) = AppliedTransaction(
        basePointer,
        recipeId,
        recipeId,
        businessGeneration);
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(recipeSubmit);
    var combo = runtime.Combo(basePointer + 0x600, runtime.Authoritative.Single(), new[] { 2 });
    var button = new FakeButton(basePointer + 0x500);
    True(RuntimeTargetRecipeVariantService.BeginSubmitForTests(button, out var firstOutputSubmit),
        "ready output setup could not establish the first button probe");
    True(RuntimeTargetRecipeVariantService.PrepareOutputSelectionForTests(
        runtime.Panel, combo, button, out var outputState),
        "ready output setup could not select the exact combo");
    var closure = runtime.RegisterOutputClosure(button, combo, closurePointer);
    RuntimeTargetRecipeVariantService.CompleteOutputSelectionForTests(outputState, button);
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(firstOutputSubmit);
    return (runtime, button, closure);
}

static void VerifyUncertainMutationLatchAndNativeRows()
{
    var runtime = Install(new FakeRecipe(0x141, 50, new[] { 1 }, 3));
    runtime.Inventory[1] = 5;
    runtime.Inventory[2] = 5;
    var target = Set(8, Target(RuntimeUiTargetKind.Rare, 50, new[] { 1, 2 }, new[] { 2 }, "rare-h"));
    Publish(target);
    True(RuntimeTargetRecipeVariantService.InjectForTests(runtime.Panel, target, 9), "uncertain setup failed");
    var synthetic = runtime.Created.Single();
    var syntheticButton = new FakeButton(0x241);
    Bind(runtime, synthetic, syntheticButton);
    object routed = synthetic;
    True(RuntimeTargetRecipeVariantService.RouteRecipeSelectionForTests(runtime.Panel, ref routed, syntheticButton),
        "uncertain route setup failed");
    True(RuntimeTargetRecipeVariantService.BeginSubmitForTests(syntheticButton, out var submit),
        "uncertain submit setup failed");
    runtime.NativeImportBase(runtime.Authoritative.Single());
    runtime.ThrowAfterDebit = true;
    Throws(() => RuntimeTargetRecipeVariantService.ApplyExtrasForTests(runtime.Panel, out _),
        "post-debit exception did not escape and abort the native callback");
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(submit, new InvalidOperationException("native aborted"));
    Equal(1, runtime.DebitCalls, "uncertain setup did not perform exactly one debit");

    object retry = synthetic;
    False(RuntimeTargetRecipeVariantService.RouteRecipeSelectionForTests(runtime.Panel, ref retry, syntheticButton),
        "uncertain latch allowed the same synthetic row to replay");
    var freshSyntheticButton = new FakeButton(0x242);
    False(RuntimeTargetRecipeVariantService.PrepareRecipeElementEnableForTests(
        runtime.Panel, synthetic, freshSyntheticButton, out _), "uncertain latch enabled another synthetic row");
    Equal(1, runtime.DebitCalls, "uncertain latch replayed debit");
    Equal(0, runtime.AddRangeCalls, "uncertain debit failure unexpectedly reached AddRange");

    var baseButton = new FakeButton(0x243);
    Bind(runtime, runtime.Authoritative.Single(), baseButton);
    object baseRecipe = runtime.Authoritative.Single();
    False(RuntimeTargetRecipeVariantService.RouteRecipeSelectionForTests(runtime.Panel, ref baseRecipe, baseButton),
        "uncertain active transaction allowed a base-recipe switch before panel teardown");
    var close = RuntimeTargetRecipeVariantService.BeginPanelTeardownForTests(runtime.Panel);
    RuntimeTargetRecipeVariantService.CompletePanelCloseForTests(close);
    baseRecipe = runtime.Authoritative.Single();
    True(RuntimeTargetRecipeVariantService.RouteRecipeSelectionForTests(runtime.Panel, ref baseRecipe, baseButton),
        "safe panel teardown did not restore the authoritative native path");
    var unrelated = new FakeRecipe(0x149, 999, new[] { 9 }, 1);
    object unrelatedObject = unrelated;
    True(RuntimeTargetRecipeVariantService.RouteRecipeSelectionForTests(
        runtime.Panel, ref unrelatedObject, new FakeButton(0x244)), "uncertain latch blocked an unrelated recipe");
}

static void VerifyOutputMismatchAndPoolReuse()
{
    var ready = ReadyOutputClosure(0x151, 60, 0x451);
    var transaction = ReadCurrentTransaction(ready.Runtime.Panel);
    var wrongCombo = ready.Runtime.Combo(0x351, ready.Runtime.Authoritative.Single(), new[] { 99 });
    var pooled = new FakeButton(0x251) { HasSubmitCallback = true };
    var cleanCalls = ready.Runtime.CleanCalls;
    True(RuntimeTargetRecipeVariantService.PrepareOutputSelectionForTests(
        ready.Runtime.Panel,
        wrongCombo,
        pooled,
        out var suppressed),
        "mismatched output did not enter native-owned callback replacement");
    True(pooled.HasSubmitCallback,
        "mismatched output prefix cleared the native callback before OnOutputSelected");
    Equal(cleanCalls, ready.Runtime.CleanCalls,
        "mismatched output prefix entered the callback cleaner");
    ready.Runtime.RegisterOutputClosure(pooled, wrongCombo, 0x551);
    True(RuntimeTargetRecipeVariantService.CompleteOutputSelectionForTests(
        suppressed,
        pooled) == null,
        "mismatched output callback could not be safely suppressed after native return");
    True(pooled.Interactable, "mismatched output candidate permanently changed native interactability");
    False(pooled.HasSubmitCallback, "mismatched output candidate retained a pooled callback");
    Equal(cleanCalls + 1, ready.Runtime.CleanCalls,
        "mismatched native output callback was not post-cleaned exactly once");
    False(RuntimeTargetRecipeVariantService.BeginSubmitForTests(pooled, out var blockedSubmit),
        "mismatched output candidate callback was not blocked");
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(blockedSubmit);
    Equal("OutputReady", ReadProperty(transaction, "State")!.ToString()!,
        "one mismatched candidate invalidated the exact ready output");
    Equal(0L, StatusCount("uncertain"), "mismatched candidate latched Uncertain");
    Equal(0L, StatusCount("rejected"), "mismatched candidate rejected the transaction");

    True(RuntimeTargetRecipeVariantService.BeginSubmitForTests(
        ready.Button, out var finalSubmit),
        "exact output button was blocked after another candidate mismatch");
    True(RuntimeTargetRecipeVariantService.BeginOutputClosureForTests(
        ready.Closure, out var finalClosure),
        "exact output closure was blocked after another candidate mismatch");
    var close = RuntimeTargetRecipeVariantService.BeginPanelTeardownForTests(ready.Runtime.Panel);
    ready.Runtime.NativeClosePanel();
    RuntimeTargetRecipeVariantService.CompletePanelCloseForTests(close);
    RuntimeTargetRecipeVariantService.CompleteOutputClosureForTests(finalClosure);
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(finalSubmit);
    Equal(1L, StatusCount("completed"), "exact output did not complete after candidate mismatch");
    Equal(1, ready.Runtime.DebitCalls, "candidate mismatch replayed the extras debit");
    Equal(1, ready.Runtime.AddRangeCalls, "candidate mismatch replayed selected extras");
}

static void VerifyReentrancyAndTransferFailure()
{
    var (runtime, target, _, syntheticButton, submit) = AppliedTransaction(0x161, 70, 10);
    Equal(1, runtime.DebitCalls, "reentrancy setup did not commit once");
    False(RuntimeTargetRecipeVariantService.BeginSubmitForTests(syntheticButton, out _),
        "applied recipe callback was re-entered");
    runtime.ResetRecipeListToAuthoritative();
    runtime.FailRecipeListRead = true;
    False(RuntimeTargetRecipeVariantService.InjectForTests(
        runtime.Panel,
        target,
        9,
        RuntimeTargetRecipeVariantService.RecipeSurfaceRefreshKind.FullVisual),
        "failed E1 to E2 transfer was accepted");
    var combo = runtime.Combo(0x361, runtime.Authoritative.Single(), new[] { 2 });
    var blockedButton = new FakeButton(0x261) { HasSubmitCallback = true };
    True(RuntimeTargetRecipeVariantService.PrepareOutputSelectionForTests(
        runtime.Panel, combo, blockedButton, out var blockedOutput),
        "uncertain output did not enter native-owned callback suppression");
    runtime.RegisterOutputClosure(blockedButton, combo, 0x461);
    True(RuntimeTargetRecipeVariantService.CompleteOutputSelectionForTests(
        blockedOutput,
        blockedButton) == null,
        "uncertain output callback could not be suppressed after native return");
    True(blockedButton.HasSubmitCallback,
        "a recipe-list read failure removed a native output callback from a receipt-complete transaction");
    True(blockedButton.Interactable,
        "a recipe-list read failure disabled an otherwise valid native output button");
    Equal(0L, StatusCount("uncertain"),
        "a pure recipe-list read failure polluted the material transaction");
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(submit, new InvalidOperationException("rebuild failed"));
    Equal(1L, StatusCount("uncertain"),
        "the later native full-visual exception did not fail closed the active transaction");
    Equal(1, runtime.DebitCalls, "failed transfer replayed debit");
    Equal(1, runtime.AddRangeCalls, "failed transfer replayed AddRange");
}

static void VerifyPartialInsertionKeepsNativeBaseUsable()
{
    var runtime = Install(new FakeRecipe(0x171, 80, new[] { 1 }, 2));
    runtime.Inventory[1] = 4;
    runtime.Inventory[2] = 4;
    runtime.ThrowAfterInsert = true;
    var target = Set(11, Target(RuntimeUiTargetKind.Rare, 80, new[] { 1, 2 }, new[] { 2 }, "rare-j"));
    Publish(target);
    False(RuntimeTargetRecipeVariantService.InjectForTests(runtime.Panel, target, 9),
        "uncertain partial insertion was accepted");
    var createdCount = runtime.Created.Count;
    runtime.ThrowAfterInsert = false;
    False(RuntimeTargetRecipeVariantService.InjectForTests(runtime.Panel, target, 9),
        "uncertain native Insert was replayed in the same business/target epoch");
    Equal(createdCount, runtime.Created.Count, "blocked uncertain Insert replay allocated another Recipe");
    var baseButton = new FakeButton(0x271);
    True(RuntimeTargetRecipeVariantService.PrepareRecipeElementEnableForTests(
        runtime.Panel, runtime.Authoritative.Single(), baseButton, out var baseState),
        "partial Mod insertion disabled the authoritative native row");
    RuntimeTargetRecipeVariantService.CompleteRecipeElementEnableForTests(baseState);
    True(RuntimeTargetRecipeVariantService.BeginSubmitForTests(baseButton, out _),
        "partial insertion left a tombstone on the authoritative row");
    var partialSynthetic = runtime.Recipes.Single(recipe => recipe.Pointer != runtime.Authoritative.Single().Pointer);
    False(RuntimeTargetRecipeVariantService.PrepareRecipeElementEnableForTests(
        runtime.Panel, partialSynthetic, new FakeButton(0x272), out _),
        "partial synthetic row was not blocked fail-closed");
}

static void VerifyLogGenerationBudget()
{
    Install(new FakeRecipe(0x17a, 90, new[] { 1 }, 2));
    var service = typeof(RuntimeTargetRecipeVariantService);
    var logKindType = service.GetNestedType(
        "TransactionLogKind",
        System.Reflection.BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("transaction log kind is missing");
    var action = Enum.Parse(logKindType, "Action");
    var safety = Enum.Parse(logKindType, "Safety");
    var log = service.GetMethod(
        "TryLogTransaction",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
        ?? throw new InvalidOperationException("transaction log entry is missing");
    var count = service.GetField(
        "_transactionLogs",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
        ?? throw new InvalidOperationException("transaction log budget field is missing");
    var actionCount = service.GetField(
        "_actionTransactionLogs",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
        ?? throw new InvalidOperationException("action transaction log budget field is missing");
    var criticalCount = service.GetField(
        "_criticalTransactionLogs",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
        ?? throw new InvalidOperationException("critical transaction log budget field is missing");
    var surfaceCount = service.GetField(
        "_surfaceTransactionLogs",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
        ?? throw new InvalidOperationException("surface transaction log budget field is missing");
    var safetyCount = service.GetField(
        "_safetyTransactionLogs",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
        ?? throw new InvalidOperationException("safety transaction log budget field is missing");
    var safetyMaximum = (int)(service.GetField(
        "MaximumSafetyTransactionLogsPerBusiness",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
        ?.GetRawConstantValue()
        ?? throw new InvalidOperationException("safety transaction log maximum is missing"));
    var generation = service.GetField(
        "_logBusinessGeneration",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
        ?? throw new InvalidOperationException("transaction log generation field is missing");

    log.Invoke(null, new[] { (object)10L, "current-generation", action });
    Equal(1, (int)count.GetValue(null)!, "current generation did not consume one log entry");
    Equal(10L, (long)generation.GetValue(null)!, "current log generation was not recorded");
    log.Invoke(null, new[] { (object)0L, "invalid-generation", action });
    log.Invoke(null, new[] { (object)9L, "stale-generation", action });
    Equal(1, (int)count.GetValue(null)!,
        "invalid or stale generation consumed the current business log budget");
    Equal(1, (int)actionCount.GetValue(null)!,
        "invalid or stale generation consumed the current action log budget");
    Equal(0, (int)criticalCount.GetValue(null)!,
        "invalid or stale generation changed the critical log budget");
    Equal(0, (int)surfaceCount.GetValue(null)!,
        "invalid or stale generation changed the surface log budget");
    Equal(10L, (long)generation.GetValue(null)!,
        "invalid or stale generation changed the current log generation");
    log.Invoke(null, new[] { (object)11L, "next-generation", action });
    Equal(1, (int)count.GetValue(null)!, "next business did not reset the log budget");
    Equal(1, (int)actionCount.GetValue(null)!, "next business did not reset the action log budget");
    Equal(0, (int)criticalCount.GetValue(null)!, "next business did not reset the critical log budget");
    Equal(0, (int)surfaceCount.GetValue(null)!, "next business did not reset the surface log budget");
    Equal(11L, (long)generation.GetValue(null)!, "next business did not advance the log generation");
    for (var index = 0; index < safetyMaximum + 3; index++)
    {
        log.Invoke(null, new[] { (object)11L, $"safety-{index}", safety });
    }
    Equal(safetyMaximum, (int)safetyCount.GetValue(null)!,
        "the independent safety-event budget exceeded its exact cap");
    Equal(1, (int)count.GetValue(null)!,
        "safety events consumed the ordinary transaction log budget");
    log.Invoke(null, new[] { (object)12L, "next-generation-safety", safety });
    Equal(1, (int)safetyCount.GetValue(null)!,
        "the next business did not reset the safety-event budget");
    Equal(0, (int)count.GetValue(null)!,
        "a safety-only next generation inherited the ordinary log budget");
    Equal(0, (int)actionCount.GetValue(null)!,
        "a safety-only next generation inherited the action log budget");
    Equal(12L, (long)generation.GetValue(null)!,
        "a safety-only event did not advance the log generation");
}

static void VerifyProductionSourceContract()
{
    var saveDirectory = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "../../../../../mods/bepinex/src/Save"));
    var corePath = Path.Combine(saveDirectory, "RuntimeTargetRecipeVariantService.cs");
    var runtimePath = Path.Combine(saveDirectory, "RuntimeTargetRecipeVariantRuntime.cs");
    var pinningPath = Path.Combine(saveDirectory, "RuntimeUiPinningService.cs");
    var oldPath = Path.Combine(saveDirectory, "RuntimeTargetRecipeVariantDiagnosticService.cs");
    var projectPath = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "../../../../../tests/runtime-target-recipe-variant/RuntimeTargetRecipeVariantSmoke.csproj"));
    True(File.Exists(corePath) && File.Exists(runtimePath) && File.Exists(pinningPath),
        "formal recipe variant files are missing");
    False(File.Exists(oldPath), "old diagnostic recipe variant source still exists");
    var core = File.ReadAllText(corePath);
    var runtime = File.ReadAllText(runtimePath);
    var pinning = File.ReadAllText(pinningPath);
    var project = File.ReadAllText(projectPath);
    True(project.Contains("RuntimeTargetRecipeVariantRuntime.cs", StringComparison.Ordinal),
        "smoke does not compile the production runtime adapter directly");
    False((core + runtime).Contains("diagnostic", StringComparison.OrdinalIgnoreCase),
        "formal recipe variant implementation retained diagnostic code");
    False(core.Contains("object[] __args", StringComparison.Ordinal),
        "production restored the HarmonyX object[] non-writeback path");
    True(core.Contains("BeforeRecipeElementSelectedExact<TRecipe>", StringComparison.Ordinal)
        && core.Contains("MakeGenericMethod(recipeType)", StringComparison.Ordinal),
        "production does not install the closed generic exact Recipe prefix");
    True(core.Contains("TryAcquireTargetPublicationLease", StringComparison.Ordinal),
        "submit does not hold a target publication lease");
    True(pinning.Contains("long expectedBusinessGeneration", StringComparison.Ordinal)
        && pinning.Contains("lifecycle.Generation != expectedBusinessGeneration", StringComparison.Ordinal),
        "final output cannot hold an exact business-generation publication lease");
    True(core.Contains("OriginTargetGeneration", StringComparison.Ordinal)
        && core.Contains("TryValidateMutationTransferReceipt(", StringComparison.Ordinal),
        "confirmed mutation origin is not separated from the current panel target surface");
    True(core.Contains("RecipeSurfaceRefreshKind.DirectRecipeField", StringComparison.Ordinal)
        && core.Contains("RecipeSurfaceRefreshKind.FullVisual", StringComparison.Ordinal),
        "direct target refresh and full visual rebuild do not have explicit semantics");
    True(core.Contains(
            "RuntimeUiPinningService.ReadSurfaceRefreshTargetSet()",
            StringComparison.Ordinal)
        && core.Contains(
            "RuntimeUiPinningService.IsSurfaceRefreshTargetCurrentOrDeferred(targetSet)",
            StringComparison.Ordinal),
        "recipe-surface refreshes do not preserve the exact deferred authority presentation target");
    True(pinning.Contains("authorityPanelFence=", StringComparison.Ordinal)
        && pinning.Contains("IsAuthorityFenceTargetLocked(target)", StringComparison.Ordinal)
        && pinning.Contains("TryAcquireTargetPublicationLease", StringComparison.Ordinal),
        "authority panel refresh fencing is not separated from operational action publication leases");
    var fullVisualPrefixStart = core.IndexOf(
        "private static void BeforeUpdateAllVisual",
        StringComparison.Ordinal);
    var fullVisualPrefixEnd = fullVisualPrefixStart < 0
        ? -1
        : core.IndexOf(
            "private static Exception? AfterUpdateAllVisual",
            fullVisualPrefixStart,
            StringComparison.Ordinal);
    True(fullVisualPrefixStart >= 0 && fullVisualPrefixEnd > fullVisualPrefixStart,
        "full visual refresh scope hooks are missing");
    var fullVisualPrefix = core[fullVisualPrefixStart..fullVisualPrefixEnd];
    var depthEntry = fullVisualPrefix.IndexOf(
        "_updateAllVisualDepth++",
        StringComparison.Ordinal);
    var mutationProbe = fullVisualPrefix.IndexOf(
        "ApplyExtrasDuringNativeRefresh",
        StringComparison.Ordinal);
    var outputResetProbe = fullVisualPrefix.IndexOf(
        "TryResetOutputForFullVisualLocked",
        StringComparison.Ordinal);
    True(depthEntry >= 0
        && outputResetProbe > depthEntry
        && mutationProbe > outputResetProbe,
        "full visual prefix work can run before the refresh-depth token is established");
    True(core.Contains("exactArmedSwitch", StringComparison.Ordinal)
        && core.Contains("SourceStateAtArm", StringComparison.Ordinal)
        && core.Contains("TryValidateSelectionIntentLocked", StringComparison.Ordinal),
        "full visual output reset does not defer to an exact armed native recipe switch");
    True(core.Contains("TryConsumeOuterFullVisualOutputResetLocked", StringComparison.Ordinal)
        && core.Contains("OutputResetConsumed", StringComparison.Ordinal)
        && core.Contains("RefreshScopeToken", StringComparison.Ordinal),
        "outer FullVisual output-reset receipt is not carried into the recipe postfix");
    True(core.Contains("surface-nonapplicable", StringComparison.Ordinal),
        "non-applicable cooker filtering is not represented by a bounded surface result");
    False(core.Contains(
            "recipe variant panel or target context changed after native ingredient mutation",
            StringComparison.Ordinal),
        "the removed broad target-context mutation latch survived the transfer design");
    True(core.Contains("MutationUncertain", StringComparison.Ordinal),
        "panel-level no-replay latch is missing");
    True(core.Contains("SourceRecipePointer", StringComparison.Ordinal)
        && core.Contains("fresh source synthetic recipe identity changed", StringComparison.Ordinal),
        "recipe transaction does not retain and fresh-check its exact source synthetic Recipe");
    True(core.Contains("TransactionState.Cancelled", StringComparison.Ordinal)
        && core.Contains("CompletePanelClose(", StringComparison.Ordinal)
        && core.Contains("TryRejectBeforeMutationLocked", StringComparison.Ordinal),
        "manual-close or terminal monotonicity contract is missing");
    False(core.Contains("RowKind.Unsafe", StringComparison.Ordinal),
        "ambiguous synthetic/output ownership type survived cleanup");
    True(core.Contains("OnOutputSelected", StringComparison.Ordinal)
        && core.Contains("PatchPrefixFinalizer(", StringComparison.Ordinal),
        "output callback registration is not closed by a finalizer");
    True(core.Contains("Method_Internal_Void_PDM_0", StringComparison.Ordinal)
        && core.Contains("patched:8/8:safety-first", StringComparison.Ordinal)
        && core.Contains("BeginOutputClosure", StringComparison.Ordinal)
        && core.Contains("CompleteOutputClosure", StringComparison.Ordinal),
        "exact final output closure is not retained within the eight safe hooks");
    False(core.Contains("hooks.OnPanelDestroyed", StringComparison.Ordinal)
        || core.Contains("nameof(BeforePanelDestroyed)", StringComparison.Ordinal)
        || core.Contains(
            "RequireExactMethod(panelType, \"OnPanelDestroyed\"",
            StringComparison.Ordinal),
        "production still installs the cooking destroy Hook that shares an empty native alias");
    True(core.Contains("RetireForBusinessBoundary(", StringComparison.Ordinal)
        && core.Contains("panel.BusinessGeneration == businessGeneration", StringComparison.Ordinal)
        && core.Contains("pair.Value.PanelEpoch <= panel.PanelEpoch", StringComparison.Ordinal),
        "business-boundary retirement does not fence exact generation and panel epochs");
    True(core.Contains("RetireForShutdown(", StringComparison.Ordinal)
        && !core.Contains("RetireFailClosed(", StringComparison.Ordinal),
        "controller shutdown still uses the ambiguous recipe-variant retirement entry");
    var retiredPreInsertGate = core.IndexOf(
        "TryRejectRetiredSurfaceBeforeNativeInsertionLocked(",
        StringComparison.Ordinal);
    var firstNativeInsert = core.IndexOf("_runtime.InsertRecipe(", StringComparison.Ordinal);
    True(retiredPreInsertGate >= 0 && firstNativeInsert > retiredPreInsertGate,
        "a retained retired transaction can reach native Insert before its explicit gate");
    True(runtime.Contains("\"get_method\"", StringComparison.Ordinal)
        && runtime.Contains(
            "GetIl2CppMethodInfoPointerFieldForGeneratedMethod",
            StringComparison.Ordinal)
        && runtime.Contains("HasExactNativeClass", StringComparison.Ordinal),
        "runtime does not compare the exact raw delegate method and target class pointers");
    True(runtime.Contains("\"get_output\"", StringComparison.Ordinal),
        "runtime does not read the exact final output pointer");
    var runtimeAdapterType = typeof(ReflectionTargetRecipeVariantRuntime);
    var coreBindingsType = runtimeAdapterType.GetNestedType(
        "ExactBindings",
        System.Reflection.BindingFlags.NonPublic);
    var selectedVisualBindingsType = runtimeAdapterType.GetNestedType(
        "SelectedVisualBindings",
        System.Reflection.BindingFlags.NonPublic);
    True(coreBindingsType != null && selectedVisualBindingsType != null,
        "runtime does not define independent core and selected-visual binding sets");
    True(runtimeAdapterType.GetFields(
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
        .Any(field => field.FieldType == selectedVisualBindingsType),
        "runtime does not cache selected-visual bindings independently");
    False(coreBindingsType!.GetProperties().Any(property =>
            property.Name.Contains("SelectedInstance", StringComparison.Ordinal)
            || property.Name.Equals("IngredientType", StringComparison.Ordinal)
            || property.Name.Equals("GetIngredientId", StringComparison.Ordinal)),
        "diagnostic selected-visual metadata leaked into the core binding set");
    var selectionReaderStart = runtime.IndexOf(
        "public bool TryReadPanelSelectionState(",
        StringComparison.Ordinal);
    var selectionReaderEnd = runtime.IndexOf(
        "public bool TryReadSelectedVisualState(",
        selectionReaderStart,
        StringComparison.Ordinal);
    True(selectionReaderStart >= 0 && selectionReaderEnd > selectionReaderStart,
        "pure panel-selection reader source boundary is missing");
    var selectionReader = runtime[selectionReaderStart..selectionReaderEnd];
    False(selectionReader.Contains("GetHasImported", StringComparison.Ordinal)
        || selectionReader.Contains("GetImportedRecipe", StringComparison.Ordinal),
        "panel-selection reader still depends on the one-shot imported Recipe hint");
    var projectedPreflightStart = core.IndexOf(
        "private static bool TryProjectedRecipePreflight(",
        StringComparison.Ordinal);
    var projectedPreflightEnd = core.IndexOf(
        "private static int[] ExpandIngredients(",
        projectedPreflightStart,
        StringComparison.Ordinal);
    True(projectedPreflightStart >= 0 && projectedPreflightEnd > projectedPreflightStart,
        "recipe/output selection-only preflight source boundary is missing");
    var latePreflightSource = core[projectedPreflightStart..projectedPreflightEnd];
    True(Count(latePreflightSource, "TryReadPanelSelectionState(") == 2,
        "recipe and output preflight do not both use the selection-only panel state");
    False(latePreflightSource.Contains("TryReadPanelCookingState(", StringComparison.Ordinal)
        || latePreflightSource.Contains("HasImportedRecipe", StringComparison.Ordinal)
        || latePreflightSource.Contains("ImportedRecipePointer", StringComparison.Ordinal),
        "late recipe/output preflight still depends on the consumed imported Recipe state");
    Equal(3, Count(core, "_runtime.TryReadPanelCookingState("),
        "full cooking state escaped the three synchronous imported-Recipe receipt checks");
    False(runtime.Contains("\"get_Method\"", StringComparison.Ordinal),
        "runtime compares an Il2Cpp reflection wrapper instead of the raw delegate method pointer");
    True(core.Contains("Target recipe variant", StringComparison.Ordinal)
        && core.Contains("MaximumTransactionLogsPerBusiness", StringComparison.Ordinal)
        && core.Contains("TransactionLogKind.Surface", StringComparison.Ordinal),
        "bounded formal transaction logs are missing");
    False(core.Contains("IsCriticalTransactionLog", StringComparison.Ordinal),
        "transaction log priority still depends on a message prefix");
    True(core.Contains("ButtonCleanupLeases", StringComparison.Ordinal)
        && core.Contains("businessGeneration <= 0", StringComparison.Ordinal)
        && core.Contains("pair.Value.PanelEpoch <= panel.PanelEpoch", StringComparison.Ordinal),
        "callback cleanup ownership, stale log, or prior-epoch business-boundary guard is missing");
    Equal(1, Count(core, "_runtime.TryCleanSubmitCallback("),
        "callback cleanup bypasses the single exclusive cleanup lease");
    var recipeRowStart = core.IndexOf(
        "private static bool PrepareRecipeElementEnableCore(",
        StringComparison.Ordinal);
    var recipeRowEnd = core.IndexOf(
        "internal static bool BeginSubmitForTests(",
        recipeRowStart,
        StringComparison.Ordinal);
    True(recipeRowStart >= 0 && recipeRowEnd > recipeRowStart,
        "recipe-row ownership source boundary is missing");
    var recipeRowSource = core[recipeRowStart..recipeRowEnd];
    False(recipeRowSource.Contains("TryCleanSubmitCallbackExclusive(", StringComparison.Ordinal)
        || recipeRowSource.Contains("TryDisableButton(", StringComparison.Ordinal),
        "recipe-row core still directly cleans callbacks or changes native interactability");
    True(recipeRowSource.Contains(
            "TryReleasePriorOutputOwnershipForRecipeRow(",
            StringComparison.Ordinal),
        "recipe-row core does not route pooled output ownership through its exact handoff");
    var outputHandoffStart = core.IndexOf(
        "private static bool TryReleasePriorOutputOwnershipForRecipeRow(",
        StringComparison.Ordinal);
    var outputHandoffEnd = core.IndexOf(
        "internal static bool PrepareRecipeElementEnableForTests(",
        outputHandoffStart,
        StringComparison.Ordinal);
    True(outputHandoffStart >= 0 && outputHandoffEnd > outputHandoffStart,
        "exact output-to-recipe ownership handoff source boundary is missing");
    var outputHandoffSource = core[outputHandoffStart..outputHandoffEnd];
    Equal(1, Count(outputHandoffSource, "TryCleanSubmitCallbackExclusive("),
        "output-to-recipe handoff does not have exactly one output-owned cleanup entry");
    True(outputHandoffSource.Contains("TryReadExactOutputSubmitClosure(", StringComparison.Ordinal)
        && outputHandoffSource.Contains("closure.TransactionSequence", StringComparison.Ordinal)
        && outputHandoffSource.Contains("closure.TransactionIdentity", StringComparison.Ordinal)
        && outputHandoffSource.Contains("closure.ButtonBindingSequence", StringComparison.Ordinal),
        "output-to-recipe cleanup is not guarded by the exact fresh closure and transaction owner");
    False(outputHandoffSource.Contains("TryDisableButton(", StringComparison.Ordinal),
        "output-to-recipe handoff changes native recipe-row interactability");
    var outputSelectionStart = core.IndexOf(
        "private static bool PrepareOutputSelectionCore(",
        StringComparison.Ordinal);
    var outputSelectionEnd = core.IndexOf(
        "private static bool BlockOutputSelectionAfterContextChange(",
        outputSelectionStart,
        StringComparison.Ordinal);
    True(outputSelectionStart >= 0 && outputSelectionEnd > outputSelectionStart,
        "output-selection ownership source boundary is missing");
    var outputSelectionSource = core[outputSelectionStart..outputSelectionEnd];
    False(outputSelectionSource.Contains("TryCleanSubmitCallbackExclusive(", StringComparison.Ordinal)
        || outputSelectionSource.Contains("TryCleanSubmitCallback(", StringComparison.Ordinal),
        "OnOutputSelected prefix still cleans a callback before native ownership");
    True(outputSelectionSource.Contains("OutputHookDisposition.RegisterExact", StringComparison.Ordinal)
        && outputSelectionSource.Contains("StageSuppressedOutputSelection(", StringComparison.Ordinal),
        "output selection does not separate exact registration from post-native suppression");
    True(core.Contains("private static Exception? CompleteSuppressedOutputSelection(", StringComparison.Ordinal)
        && core.Contains("FailOutputSelectionAfterNativeException(", StringComparison.Ordinal),
        "normal-return output post-clean or native-exception preservation is missing");
    True(recipeRowSource.Contains("IsObservedButtonBindingCurrentLocked", StringComparison.Ordinal),
        "recipe-row path does not use exact observed-binding replacement");
    False(core.Contains("SetCook(", StringComparison.Ordinal),
        "service bypasses the game's native final callback");
    False(core.Contains("TextMeshPro", StringComparison.Ordinal)
        || core.Contains("TextSha256", StringComparison.Ordinal),
        "obsolete TMP/hash probe survived in the formal service");
    Equal(1, Count(runtime, "\"IngredientOutRange\""),
        "runtime does not bind exactly one native extra debit entry");
    Equal(1, Count(runtime, "\"AddRange\""),
        "runtime does not bind the exact selected-ingredient AddRange once");
}

static int Count(string value, string needle)
{
    var count = 0;
    for (var index = 0; (index = value.IndexOf(needle, index, StringComparison.Ordinal)) >= 0; index += needle.Length)
        count++;
    return count;
}

static long StatusCount(string name)
{
    var status = RuntimeTargetRecipeVariantService.Status;
    var marker = $"{name}=";
    var start = status.IndexOf(marker, StringComparison.Ordinal);
    if (start < 0) throw new InvalidOperationException($"status field {name} is missing: {status}");
    start += marker.Length;
    var semicolon = status.IndexOf(';', start);
    var slash = status.IndexOf('/', start);
    var end = semicolon < 0
        ? slash
        : slash < 0
            ? semicolon
            : Math.Min(semicolon, slash);
    if (end < 0) end = status.Length;
    return long.Parse(status[start..end], System.Globalization.CultureInfo.InvariantCulture);
}

static object ReadCurrentPanelState(FakePanel panel)
{
    var service = typeof(RuntimeTargetRecipeVariantService);
    var panels = (System.Collections.IDictionary)(service
        .GetField("Panels", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
        .GetValue(null)!);
    return panels[panel.Pointer]
        ?? throw new InvalidOperationException("expected panel state is missing");
}

static object ReadCurrentTransaction(FakePanel panel)
{
    return ReadProperty(ReadCurrentPanelState(panel), "Transaction")
        ?? throw new InvalidOperationException("expected panel transaction is missing");
}

static object ReadCurrentSwitchAttempt(FakePanel panel)
{
    var service = typeof(RuntimeTargetRecipeVariantService);
    var panels = (System.Collections.IDictionary)(service
        .GetField("Panels", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
        .GetValue(null)!);
    var panelState = panels[panel.Pointer]
        ?? throw new InvalidOperationException("expected panel state is missing");
    return ReadProperty(panelState, "SwitchAttempt")
        ?? throw new InvalidOperationException("expected recipe switch attempt is missing");
}

static object? ReadProperty(object instance, string name)
{
    return instance.GetType()
        .GetProperty(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)!
        .GetValue(instance);
}

static void ToggleButtonBindingStateVersion(nint buttonPointer)
{
    var service = typeof(RuntimeTargetRecipeVariantService);
    var stateRoot = service
        .GetField("StateRoot", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
        .GetValue(null)!;
    var buttons = (System.Collections.IDictionary)(service
        .GetField("Buttons", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
        .GetValue(null)!);
    lock (stateRoot)
    {
        var binding = buttons[buttonPointer]
            ?? throw new InvalidOperationException("expected button binding is missing");
        var state = binding.GetType().GetProperty("State")!;
        state.SetValue(binding, Enum.Parse(state.PropertyType, "Tombstone"));
        state.SetValue(binding, Enum.Parse(state.PropertyType, "Ready"));
    }
}

static void ForceCurrentTransactionUncertain(FakePanel panel, string reason)
{
    var service = typeof(RuntimeTargetRecipeVariantService);
    var stateRoot = service
        .GetField("StateRoot", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
        .GetValue(null)!;
    var transaction = ReadCurrentTransaction(panel);
    var mark = service.GetMethod(
        "MarkTransactionUncertainLocked",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
    lock (stateRoot)
    {
        mark.Invoke(null, new[] { transaction, reason });
    }
}

static (FakeRuntime Runtime,
    RuntimeUiTargetSetSnapshot Target,
    FakeRecipe Synthetic,
    long Sequence,
    RuntimeTargetRecipeVariantService.SubmitHookState? OutputSubmit,
    RuntimeTargetRecipeVariantService.OutputClosureHookState? OutputClosureState)
    PrepareMutatedTransaction(
        nint basePointer,
        int recipeId,
        long generation,
        string phase,
        bool freeCook = false)
{
    var runtime = Install(new FakeRecipe(basePointer, recipeId, new[] { 1 }, 4));
    runtime.Panel.IsFreeCook = freeCook;
    runtime.Inventory[1] = 12;
    runtime.Inventory[2] = 12;
    var target = Set(generation,
        Target(RuntimeUiTargetKind.Rare, recipeId, new[] { 1, 2 }, new[] { 2 }, $"phase-{phase}"));
    Publish(target);
    True(RuntimeTargetRecipeVariantService.InjectForTests(runtime.Panel, target, 9),
        $"{phase} mutated transaction injection failed");
    var synthetic = runtime.Created.Single();
    var sourceButton = new FakeButton(basePointer + 0x100);
    Bind(runtime, synthetic, sourceButton);
    object selected = synthetic;
    True(RuntimeTargetRecipeVariantService.RouteRecipeSelectionForTests(
        runtime.Panel, ref selected, sourceButton), $"{phase} mutated transaction route failed");
    True(RuntimeTargetRecipeVariantService.BeginSubmitForTests(sourceButton, out var recipeSubmit),
        $"{phase} mutated transaction submit failed");
    runtime.NativeImportBase(runtime.Authoritative.Single());
    RuntimeTargetRecipeVariantService.ApplyExtrasForTests(runtime.Panel, out var visual);
    RuntimeTargetRecipeVariantService.CompleteUpdateVisualForTests(visual);
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(recipeSubmit);

    if (phase == "applied")
    {
        return (runtime, target, synthetic, recipeSubmit.TransactionSequence, null, null);
    }

    var combo = runtime.Combo(basePointer + 0x600, runtime.Authoritative.Single(), new[] { 2 });
    var outputButton = new FakeButton(basePointer + 0x500);
    True(RuntimeTargetRecipeVariantService.BeginSubmitForTests(outputButton, out var outputSelectionSubmit),
        $"{phase} output selection submit failed");
    True(RuntimeTargetRecipeVariantService.PrepareOutputSelectionForTests(
        runtime.Panel, combo, outputButton, out var outputState),
        $"{phase} output selection failed");
    if (phase == "output-pending")
    {
        RuntimeTargetRecipeVariantService.CompleteSubmitForTests(outputSelectionSubmit);
        return (runtime, target, synthetic, recipeSubmit.TransactionSequence, null, null);
    }

    var closure = runtime.RegisterOutputClosure(outputButton, combo, basePointer + 0x700);
    var registrationException = RuntimeTargetRecipeVariantService.CompleteOutputSelectionForTests(
        outputState,
        outputButton);
    True(registrationException == null, $"{phase} output closure registration failed");
    RuntimeTargetRecipeVariantService.CompleteSubmitForTests(outputSelectionSubmit);
    if (phase == "output-ready")
    {
        return (runtime, target, synthetic, recipeSubmit.TransactionSequence, null, null);
    }
    if (phase != "output-submitting")
    {
        throw new InvalidOperationException($"unknown mutated transaction phase: {phase}");
    }

    True(RuntimeTargetRecipeVariantService.BeginSubmitForTests(outputButton, out var outputSubmit),
        "output-submitting button submit failed");
    True(RuntimeTargetRecipeVariantService.BeginOutputClosureForTests(
        closure,
        out var closureState), "output-submitting final closure failed to arm");
    return (runtime, target, synthetic, recipeSubmit.TransactionSequence, outputSubmit, closureState);
}

static (FakeRuntime Runtime, RuntimeUiTargetSetSnapshot Target, FakeRecipe Synthetic, FakeButton Button,
    RuntimeTargetRecipeVariantService.SubmitHookState Submit) AppliedTransaction(
        nint basePointer,
        int recipeId,
        long generation,
        long businessGeneration = 9)
{
    var runtime = Install(new FakeRecipe(basePointer, recipeId, new[] { 1 }, 3));
    RuntimeNightBusinessLifecycle.Snapshot = new NightBusinessSnapshot(true, businessGeneration);
    runtime.Inventory[1] = 8;
    runtime.Inventory[2] = 8;
    var target = new RuntimeUiTargetSetSnapshot(
        generation,
        businessGeneration,
        new[]
        {
            Target(
                RuntimeUiTargetKind.Rare,
                recipeId,
                new[] { 1, 2 },
                new[] { 2 },
                $"rare-{generation}"),
        });
    Publish(target);
    True(RuntimeTargetRecipeVariantService.InjectForTests(
        runtime.Panel,
        target,
        businessGeneration), "applied transaction setup failed");
    var synthetic = runtime.Created.Single();
    var button = new FakeButton(basePointer + 0x100);
    Bind(runtime, synthetic, button);
    object routed = synthetic;
    True(RuntimeTargetRecipeVariantService.RouteRecipeSelectionForTests(runtime.Panel, ref routed, button), "applied route failed");
    True(RuntimeTargetRecipeVariantService.BeginSubmitForTests(button, out var submit), "applied submit failed");
    runtime.NativeImportBase(runtime.Authoritative.Single());
    RuntimeTargetRecipeVariantService.ApplyExtrasForTests(runtime.Panel, out _);
    return (runtime, target, synthetic, button, submit);
}

static FakeRuntime Install(params FakeRecipe[] recipes)
{
    var runtime = new FakeRuntime(recipes);
    RuntimeNightBusinessLifecycle.Snapshot = new NightBusinessSnapshot(true, 9);
    RuntimeUiTargetVariantTestState.Reset();
    RuntimeUiTargetPublicationLease.ResetCounts();
    RuntimeTargetRecipeVariantService.UseRuntimeForTests(runtime);
    return runtime;
}

static void Publish(RuntimeUiTargetSetSnapshot target) => RuntimeUiPinningService.Current = target;

static void Bind(FakeRuntime runtime, FakeRecipe recipe, FakeButton button)
{
    True(RuntimeTargetRecipeVariantService.PrepareRecipeElementEnableForTests(
        runtime.Panel, recipe, button, out var state), "exact recipe row enable was skipped");
    RuntimeTargetRecipeVariantService.CompleteRecipeElementEnableForTests(state);
}

static RuntimeUiTargetSetSnapshot Set(long generation, params RuntimeUiTargetSnapshot[] targets) =>
    new(generation, 9, targets);

static RuntimeUiTargetSnapshot Target(
    RuntimeUiTargetKind kind,
    int recipeId,
    int[] ingredients,
    int[] extras,
    string revision) =>
    new(kind, true, true, recipeId, ingredients, extras, revision);

static void True(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
}

static void False(bool value, string message) => True(!value, message);

static void Equal<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{message}; expected={expected}; actual={actual}");
}

static void Same(object expected, object actual, string message)
{
    if (!ReferenceEquals(expected, actual)) throw new InvalidOperationException(message);
}

static void Sequence<T>(IEnumerable<T> expected, IEnumerable<T> actual, string message)
{
    if (!expected.SequenceEqual(actual))
        throw new InvalidOperationException($"{message}; expected=[{string.Join(',', expected)}]; actual=[{string.Join(',', actual)}]");
}

static void Throws(Action action, string message)
{
    try { action(); }
    catch { return; }
    throw new InvalidOperationException(message);
}

namespace MystiaStewardCompanion.Save
{
    internal enum RuntimeUiTargetKind { Rare, Normal }

    [Flags]
    internal enum RuntimeUiTargetKinds { None = 0, Rare = 1, Normal = 2 }

    internal sealed class RuntimeUiTargetSnapshot
    {
        public RuntimeUiTargetSnapshot(
            RuntimeUiTargetKind kind,
            bool listPinningEnabled,
            bool recipeVariantEnabled,
            int recipeId,
            IReadOnlyList<int> ingredientIds,
            IReadOnlyList<int> extraIngredientIds,
            string targetRevision)
        {
            Kind = kind;
            ListPinningEnabled = listPinningEnabled;
            RecipeVariantEnabled = recipeVariantEnabled;
            RecipeId = recipeId;
            IngredientIds = ingredientIds.OrderBy(id => id).ToArray();
            ExtraIngredientIds = extraIngredientIds.ToArray();
            TargetRevision = targetRevision;
        }

        public RuntimeUiTargetKind Kind { get; }
        public RuntimeUiTargetKinds Claim => Kind == RuntimeUiTargetKind.Rare ? RuntimeUiTargetKinds.Rare : RuntimeUiTargetKinds.Normal;
        public bool ListPinningEnabled { get; }
        public bool RecipeVariantEnabled { get; }
        public int RecipeId { get; }
        public IReadOnlyList<int> IngredientIds { get; }
        public IReadOnlyList<int> ExtraIngredientIds { get; }
        public string TargetRevision { get; }
    }

    internal sealed class RuntimeUiTargetSetSnapshot
    {
        public RuntimeUiTargetSetSnapshot(long generation, long sessionGeneration, IReadOnlyList<RuntimeUiTargetSnapshot> targets)
        {
            Generation = generation;
            SessionGeneration = sessionGeneration;
            Targets = targets.OrderBy(target => target.Kind).ToArray();
        }

        public long Generation { get; }
        public long SessionGeneration { get; }
        public IReadOnlyList<RuntimeUiTargetSnapshot> Targets { get; }
        public RuntimeUiTargetKinds GetBaseRecipeClaims(int recipeId)
        {
            var claims = RuntimeUiTargetKinds.None;
            foreach (var target in Targets.Where(target => target.ListPinningEnabled
                && target.RecipeId == recipeId
                && (!target.RecipeVariantEnabled || target.ExtraIngredientIds.Count == 0))) claims |= target.Claim;
            return claims;
        }
    }

    internal readonly record struct NightBusinessSnapshot(bool IsActive, long Generation);

    internal static class RuntimeNightBusinessLifecycle
    {
        public static NightBusinessSnapshot Snapshot { get; set; } = new(true, 9);
    }

    internal static class RuntimeUiTargetVariantTestState
    {
        public static void Reset() => RuntimeUiPinningService.Current = new(0, 0, Array.Empty<RuntimeUiTargetSnapshot>());
    }

    internal static class RuntimeUiPinningService
    {
        public static RuntimeUiTargetSetSnapshot Current { get; set; } = new(0, 0, Array.Empty<RuntimeUiTargetSnapshot>());
        public static RuntimeUiTargetSetSnapshot ReadTargetSet() => Current;
        public static RuntimeUiTargetSetSnapshot ReadSurfaceRefreshTargetSet() => Current;
        public static bool IsSurfaceRefreshTargetCurrentOrDeferred(
            RuntimeUiTargetSetSnapshot expected) => ReferenceEquals(Current, expected);
        public static bool TryAcquireTargetPublicationLease(
            RuntimeUiTargetSetSnapshot expected,
            out RuntimeUiTargetPublicationLease lease)
        {
            lease = null!;
            if (!ReferenceEquals(Current, expected) || !RuntimeNightBusinessLifecycle.Snapshot.IsActive) return false;
            lease = new RuntimeUiTargetPublicationLease(isBusinessGenerationLease: false);
            return true;
        }

        public static bool TryAcquireTargetPublicationLease(
            long expectedBusinessGeneration,
            out RuntimeUiTargetPublicationLease lease)
        {
            lease = null!;
            var lifecycle = RuntimeNightBusinessLifecycle.Snapshot;
            if (!lifecycle.IsActive || lifecycle.Generation != expectedBusinessGeneration) return false;
            lease = new RuntimeUiTargetPublicationLease(isBusinessGenerationLease: true);
            return true;
        }
    }

    internal sealed class RuntimeUiTargetPublicationLease : IDisposable
    {
        private bool _disposed;
        private readonly bool _isBusinessGenerationLease;
        public static int Created { get; private set; }
        public static int Disposed { get; private set; }
        public static int SnapshotCreated { get; private set; }
        public static int SnapshotDisposed { get; private set; }
        public static int BusinessCreated { get; private set; }
        public static int BusinessDisposed { get; private set; }

        public RuntimeUiTargetPublicationLease(bool isBusinessGenerationLease)
        {
            _isBusinessGenerationLease = isBusinessGenerationLease;
            Created++;
            if (isBusinessGenerationLease) BusinessCreated++;
            else SnapshotCreated++;
        }

        public static void ResetCounts()
        {
            Created = 0;
            Disposed = 0;
            SnapshotCreated = 0;
            SnapshotDisposed = 0;
            BusinessCreated = 0;
            BusinessDisposed = 0;
        }

        public void Dispose()
        {
            if (_disposed) throw new InvalidOperationException("lease disposed twice");
            _disposed = true;
            Disposed++;
            if (_isBusinessGenerationLease) BusinessDisposed++;
            else SnapshotDisposed++;
        }
    }

    internal abstract class FakeNative
    {
        protected FakeNative(nint pointer) { Pointer = pointer; }
        public nint Pointer { get; }
    }

    internal sealed class FakeRecipe : FakeNative
    {
        public FakeRecipe(nint pointer, int id, int[] ingredients, int cookCount) : base(pointer)
        {
            Id = id;
            Ingredients = ingredients;
            CookCount = cookCount;
        }
        public int Id { get; }
        public int[] Ingredients { get; }
        public int CookCount { get; set; }
    }

    internal sealed class FakeButton : FakeNative
    {
        public FakeButton(nint pointer) : base(pointer) { }
        public bool Interactable { get; set; } = true;
        public bool HasSubmitCallback { get; set; }
        public FakeClosure? SubmitClosure { get; set; }
        public bool ExactDelegateMethod { get; set; } = true;
        public bool ExactDelegateTarget { get; set; } = true;
    }

    internal sealed class FakeSelectedList : FakeNative
    {
        public FakeSelectedList(nint pointer) : base(pointer) { }
        public List<int> Ids { get; } = new();
    }

    internal sealed class FakeRecipeList : FakeNative
    {
        public FakeRecipeList(nint pointer, List<FakeRecipe> recipes) : base(pointer)
        {
            Recipes = recipes;
        }

        public List<FakeRecipe> Recipes { get; }
    }

    internal sealed class FakeCombo : FakeNative
    {
        public FakeCombo(nint pointer, FakeRecipe recipe, int[] modifiers) : base(pointer)
        {
            Recipe = recipe;
            Modifiers = modifiers;
        }
        public FakeRecipe Recipe { get; }
        public int[] Modifiers { get; }
    }

    internal sealed class FakeClosure : FakeNative
    {
        public FakeClosure(
            nint pointer,
            nint panelPointer,
            nint comboPointer,
            nint outputPointer = 0) : base(pointer)
        {
            PanelPointer = panelPointer;
            ComboPointer = comboPointer;
            OutputPointer = outputPointer == 0 ? pointer + 0x1000 : outputPointer;
        }

        public nint PanelPointer { get; set; }
        public nint ComboPointer { get; set; }
        public nint OutputPointer { get; set; }
    }

    internal sealed class FakePanel : FakeNative
    {
        internal static readonly InvalidOperationException ExpectedRecipeFieldException =
            new("nested native UpdateRecipeField failed");

        public FakePanel(nint pointer, IEnumerable<FakeRecipe> recipes) : base(pointer)
        {
            Recipes = recipes.ToList();
            Selected = new FakeSelectedList(pointer + 1);
        }
        public List<FakeRecipe> Recipes { get; }
        public FakeSelectedList Selected { get; }
        public bool HasImported { get; set; }
        public FakeRecipe? Imported { get; set; }
        public int ExtraCostIngredient { get; set; } = 1;
        public bool IsFreeCook { get; set; }
        public int FullVisualCalls { get; private set; }
        public int RecipeFieldCalls { get; private set; }
        public int NestedFullVisualCalls { get; private set; }
        public Action? BeforeSuccessfulRecipeField { get; set; }

        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
        public void ThrowingUpdateAllVisual()
        {
            FullVisualCalls++;
            ThrowingUpdateRecipeField();
        }

        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
        public void ThrowingUpdateRecipeField()
        {
            RecipeFieldCalls++;
            throw ExpectedRecipeFieldException;
        }

        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
        public void SuccessfulUpdateAllVisual()
        {
            FullVisualCalls++;
            var beforeRecipeField = BeforeSuccessfulRecipeField;
            BeforeSuccessfulRecipeField = null;
            beforeRecipeField?.Invoke();
            SuccessfulUpdateRecipeField();
        }

        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
        public void SuccessfulUpdateRecipeField()
        {
            RecipeFieldCalls++;
        }

        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
        public void NestedUpdateAllVisual()
        {
            NestedFullVisualCalls++;
            if (NestedFullVisualCalls == 1)
            {
                NestedUpdateAllVisual();
            }
            else
            {
                SuccessfulUpdateRecipeField();
            }
        }
    }

    internal sealed class FakeRuntime : ITargetRecipeVariantRuntime
    {
        private static readonly object ServiceStateRoot = typeof(RuntimeTargetRecipeVariantService)
            .GetField("StateRoot", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null)!;
        private long _nextPointer = 0x900;
        private readonly Dictionary<nint, FakeCombo> _combos = new();

        public FakeRuntime(IEnumerable<FakeRecipe> recipes)
        {
            Authoritative = recipes.ToList();
            Panel = new FakePanel(0x100, Authoritative);
            RecipeList = new FakeRecipeList(0x102, Panel.Recipes);
        }

        public FakePanel Panel { get; }
        public FakeRecipeList RecipeList { get; }
        public List<FakeRecipe> Authoritative { get; }
        public List<FakeRecipe> Recipes => Panel.Recipes;
        public List<FakeRecipe> Created { get; } = new();
        public Dictionary<int, int> Inventory { get; } = new();
        public int DebitCalls { get; private set; }
        public int AddRangeCalls { get; private set; }
        public int NativeCreditCalls { get; private set; }
        public int NativeBaseDebitCalls { get; private set; }
        public int CleanCalls { get; private set; }
        public int InventoryReadCalls { get; private set; }
        public int InsertCalls { get; private set; }
        public bool ThrowAfterDebit { get; set; }
        public bool ThrowAfterInsert { get; set; }
        public bool FailRecipeListRead { get; set; }
        public bool FailReadbackAfterInsert { get; set; }
        public bool ReturnDifferentRecipeListAfterInsert { get; set; }
        public bool CorruptReadbackOrder { get; set; }
        public bool CorruptReadbackRecipeIdentity { get; set; }
        public bool FailClean { get; set; }
        public bool ThrowClean { get; set; }
        public bool ThrowWrapPanel { get; set; }
        public bool ThrowPanelCookingState { get; set; }
        public bool ThrowPanelSelectionState { get; set; }
        public int[]? SelectedVisualOverride { get; set; }
        public bool RuntimeCallObservedUnderLock { get; private set; }
        public Action? OnClean { get; set; }
        public Action? OnReadPanelSelectionState { get; set; }
        public HashSet<nint> ThrowRecipeSnapshots { get; } = new();

        public nint GetNativePointer(object instance) => instance is FakeNative native ? native.Pointer : 0;

        public bool TryWrapPanel(nint panelPointer, out object panel, out string error)
        {
            if (ThrowWrapPanel) throw new InvalidOperationException("fake panel wrapper threw");
            panel = Panel;
            error = panelPointer == Panel.Pointer ? "" : "wrong panel pointer";
            return error.Length == 0;
        }

        public bool TryWrapMatchedCombo(nint comboPointer, out object matchedCombo, out string error)
        {
            if (_combos.TryGetValue(comboPointer, out var combo))
            {
                matchedCombo = combo;
                error = "";
                return true;
            }
            matchedCombo = null!;
            error = "unknown combo";
            return false;
        }

        public bool TryReadRecipeList(object panel, int maximumCount, out object recipeList,
            out IReadOnlyList<TargetRecipeDescriptor> recipes, out string error)
        {
            recipeList = ReturnDifferentRecipeListAfterInsert && InsertCalls > 0
                ? new FakeRecipeList(0x103, Recipes)
                : RecipeList;
            if (FailRecipeListRead
                || (FailReadbackAfterInsert && InsertCalls > 0)
                || !ReferenceEquals(panel, Panel)
                || Recipes.Count > maximumCount)
            {
                recipes = Array.Empty<TargetRecipeDescriptor>();
                error = "fake recipe list unavailable";
                return false;
            }
            var projected = Recipes.Select((recipe, index) => new TargetRecipeDescriptor(
                index, recipe, recipe.Pointer, recipe.Id, recipe.Ingredients.ToArray(), recipe.CookCount)).ToArray();
            if (InsertCalls > 0 && CorruptReadbackOrder && projected.Length > 1)
                (projected[0], projected[1]) = (projected[1], projected[0]);
            if (InsertCalls > 0 && CorruptReadbackRecipeIdentity && projected.Length > 0)
            {
                var changed = projected[^1];
                projected[^1] = changed with { RecipeId = changed.RecipeId + 1 };
            }
            recipes = projected;
            error = "";
            return true;
        }

        public bool TryReadRecipeSnapshot(object recipe, out TargetRecipeSnapshot snapshot, out string error)
        {
            var typed = (FakeRecipe)recipe;
            if (ThrowRecipeSnapshots.Contains(typed.Pointer))
                throw new InvalidOperationException("fake recipe snapshot threw");
            snapshot = new TargetRecipeSnapshot(typed, typed.Pointer, typed.Id, typed.Ingredients.ToArray(), typed.CookCount);
            error = "";
            return true;
        }

        public bool TryCreateSyntheticRecipe(object authoritativeRecipe, IReadOnlyList<int> fullIngredientIds,
            int cookCount, out object syntheticRecipe, out nint syntheticPointer, out string error)
        {
            var source = (FakeRecipe)authoritativeRecipe;
            var created = new FakeRecipe((nint)Interlocked.Increment(ref _nextPointer), source.Id,
                fullIngredientIds.ToArray(), cookCount);
            Created.Add(created);
            syntheticRecipe = created;
            syntheticPointer = created.Pointer;
            error = "";
            return true;
        }

        public bool TrySetSyntheticCookCount(object syntheticRecipe, int cookCount, out string error)
        {
            ((FakeRecipe)syntheticRecipe).CookCount = cookCount;
            error = "";
            return true;
        }

        public void InsertRecipe(object recipeList, int index, object recipe)
        {
            if (!ReferenceEquals(recipeList, RecipeList))
                throw new InvalidOperationException("wrong fake recipe-list identity");
            InsertCalls++;
            Recipes.Insert(index, (FakeRecipe)recipe);
            if (ThrowAfterInsert) throw new InvalidOperationException("uncertain insert");
        }

        public bool TryCleanSubmitCallback(object button, out string error)
        {
            ObserveServiceLock();
            CleanCalls++;
            var typed = (FakeButton)button;
            var onClean = OnClean;
            OnClean = null;
            onClean?.Invoke();
            if (ThrowClean) throw new InvalidOperationException("fake callback cleanup threw");
            if (FailClean)
            {
                error = "fake callback cleanup failed";
                return false;
            }
            typed.HasSubmitCallback = false;
            typed.SubmitClosure = null;
            error = "";
            return true;
        }

        public bool TryDisableButton(object button, out string error)
        {
            ObserveServiceLock();
            ((FakeButton)button).Interactable = false;
            error = "";
            return true;
        }

        public bool TryReadPanelCookingState(object panel, out TargetRecipePanelCookingState state, out string error)
        {
            if (ThrowPanelCookingState) throw new InvalidOperationException("fake panel cooking state threw");
            if (!ReferenceEquals(panel, Panel) || !Panel.HasImported || Panel.Imported == null)
            {
                state = null!;
                error = !ReferenceEquals(panel, Panel)
                    ? "wrong panel"
                    : "fake panel has no imported Recipe receipt";
                return false;
            }
            state = new TargetRecipePanelCookingState(
                Panel.Pointer,
                Panel.Imported.Pointer,
                Panel.Imported.Id,
                Panel.Imported.Ingredients.ToArray(),
                Panel.Selected,
                Panel.Selected.Ids.ToArray(),
                Panel.ExtraCostIngredient,
                Panel.IsFreeCook);
            error = "";
            return true;
        }

        public bool TryReadPanelSelectionState(
            object panel,
            out TargetRecipePanelSelectionState state,
            out string error)
        {
            if (ThrowPanelSelectionState) throw new InvalidOperationException("fake panel selection state threw");
            var onRead = OnReadPanelSelectionState;
            OnReadPanelSelectionState = null;
            onRead?.Invoke();
            state = new TargetRecipePanelSelectionState(
                Panel.Pointer,
                Panel.Selected,
                Panel.Selected.Ids.ToArray(),
                Panel.ExtraCostIngredient,
                Panel.IsFreeCook);
            error = ReferenceEquals(panel, Panel) ? "" : "wrong panel";
            return error.Length == 0;
        }

        public bool TryReadSelectedVisualState(
            object panel,
            out TargetRecipeSelectedVisualState state,
            out string error)
        {
            var ids = SelectedVisualOverride ?? Panel.Selected.Ids.ToArray();
            state = new TargetRecipeSelectedVisualState(Panel.Pointer + 2, ids);
            error = ReferenceEquals(panel, Panel) ? "" : "wrong panel";
            return error.Length == 0;
        }

        public int GetIngredientQuantity(int ingredientId)
        {
            InventoryReadCalls++;
            return Inventory.TryGetValue(ingredientId, out var count) ? count : 0;
        }

        public void DebitIngredients(IReadOnlyList<int> expandedIngredientIds)
        {
            DebitCalls++;
            foreach (var id in expandedIngredientIds)
            {
                if (Inventory[id] >= 0) Inventory[id]--;
            }
            if (ThrowAfterDebit) throw new InvalidOperationException("uncertain debit");
        }

        public void AddSelectedIngredients(object selectedIngredientList, IReadOnlyList<int> ingredientIds)
        {
            AddRangeCalls++;
            ((FakeSelectedList)selectedIngredientList).Ids.AddRange(ingredientIds);
        }

        public bool TryReadMatchedCombo(object matchedCombo, out TargetRecipeMatchedComboSnapshot snapshot, out string error)
        {
            var combo = (FakeCombo)matchedCombo;
            snapshot = new TargetRecipeMatchedComboSnapshot(combo.Recipe.Pointer, combo.Recipe.Id, combo.Modifiers.ToArray());
            error = "";
            return true;
        }

        public bool TryReadExactOutputSubmitClosure(
            object button,
            out TargetRecipeOutputClosureBindingSnapshot snapshot,
            out string error)
        {
            var typed = (FakeButton)button;
            if (!typed.HasSubmitCallback)
            {
                snapshot = default;
                error = "fake native callback is clean";
                return false;
            }
            if (!typed.ExactDelegateMethod)
            {
                snapshot = default;
                error = "fake delegate method drift";
                return false;
            }
            if (!typed.ExactDelegateTarget || typed.SubmitClosure == null)
            {
                snapshot = default;
                error = "fake delegate target drift";
                return false;
            }
            snapshot = new TargetRecipeOutputClosureBindingSnapshot(
                typed.SubmitClosure.Pointer,
                typed.SubmitClosure.PanelPointer,
                typed.SubmitClosure.ComboPointer,
                typed.SubmitClosure.OutputPointer);
            error = "";
            return true;
        }

        public bool TryReadOutputSubmitClosureState(
            object closure,
            out TargetRecipeOutputClosureState state,
            out string error)
        {
            if (closure is not FakeClosure typed)
            {
                state = default;
                error = "fake closure type drift";
                return false;
            }
            state = new TargetRecipeOutputClosureState(
                typed.Pointer,
                typed.PanelPointer,
                typed.ComboPointer,
                typed.OutputPointer);
            error = "";
            return true;
        }

        public FakeCombo Combo(nint pointer, FakeRecipe recipe, int[] modifiers)
        {
            var combo = new FakeCombo(pointer, recipe, modifiers);
            _combos[pointer] = combo;
            return combo;
        }

        public FakeClosure RegisterOutputClosure(
            FakeButton button,
            FakeCombo combo,
            nint pointer)
        {
            var closure = new FakeClosure(pointer, Panel.Pointer, combo.Pointer);
            button.HasSubmitCallback = true;
            button.SubmitClosure = closure;
            return closure;
        }

        public void NativeImportBase(FakeRecipe recipe)
        {
            Panel.Selected.Ids.Clear();
            Panel.Selected.Ids.AddRange(recipe.Ingredients);
            if (!Panel.IsFreeCook)
            {
                foreach (var id in recipe.Ingredients)
                {
                    for (var repeat = 0; repeat < Panel.ExtraCostIngredient; repeat++)
                    {
                        if (Inventory[id] >= 0) Inventory[id]--;
                    }
                }
            }
            Panel.HasImported = true;
            Panel.Imported = recipe;
        }

        public void NativeSwitchTo(FakeRecipe recipe)
        {
            NativeCreditCalls++;
            if (!Panel.IsFreeCook)
            {
                foreach (var id in Panel.Selected.Ids)
                {
                    for (var repeat = 0; repeat < Panel.ExtraCostIngredient; repeat++)
                    {
                        if (Inventory[id] >= 0) Inventory[id]++;
                    }
                }
            }
            Panel.Selected.Ids.Clear();
            NativeBaseDebitCalls++;
            if (!Panel.IsFreeCook)
            {
                foreach (var id in recipe.Ingredients)
                {
                    for (var repeat = 0; repeat < Panel.ExtraCostIngredient; repeat++)
                    {
                        if (Inventory[id] >= 0) Inventory[id]--;
                    }
                }
            }
            Panel.Selected.Ids.AddRange(recipe.Ingredients);
            Panel.HasImported = true;
            Panel.Imported = recipe;
        }

        public void NativeClosePanel()
        {
            Panel.Selected.Ids.Clear();
            Panel.HasImported = false;
            Panel.Imported = null;
        }

        public void ResetRecipeListToAuthoritative()
        {
            Recipes.Clear();
            Recipes.AddRange(Authoritative);
            Created.Clear();
            InsertCalls = 0;
        }

        private void ObserveServiceLock()
        {
            if (Monitor.IsEntered(ServiceStateRoot)) RuntimeCallObservedUnderLock = true;
        }
    }
}
