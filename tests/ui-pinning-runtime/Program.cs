using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx.Logging;
using HarmonyLib;
using MystiaStewardCompanion.Save;
using UnityEngine;

try
{
    VerifyPatchTargets();
    VerifyDualTargetClaimsAndColorRoundTrip();
    VerifyRuntimeUiTargetPublicationLease();
    VerifyCookingRefreshHoldsExactTargetPublicationLease();
    VerifyStorageRefreshHoldsExactTargetPublicationLease();
    VerifySharedListItemUsesSingleOwnershipAndBaseline();
    VerifyOrderHighlightRuntimeWiring();
    VerifyOpenPanelRefreshScheduling();
    VerifyOpenPanelSurfaceRefreshSemantics();
    VerifyCookingRefreshStagesFailClosed();
    VerifyIdenticalTargetPublicationIsIdempotent();
    VerifyOrderHighlightSurfaceIsolation();
    VerifyScopedNativePinnedMatching();
    VerifyTargetUpdatePreservesForceTotals();
    VerifyScopePinsOneTargetSnapshot();
    VerifyNestedScopeFinalizers();
    VerifyThreadLocalScopeIsolation();
    VerifyPinningAndHighlightRemainIndependent();
    VerifyDangerousListHooksAreAbsent();
    VerifyManagedHarmonyReturnPropagation();
    VerifyManagedPinnedListHighlighting();
    VerifyExactRecipeVariantRowHighlighting();
    VerifyEnabledEmptyTargetClearsVisuals();
    VerifyLifecycleGenerationGuards();
    Console.WriteLine("PASS: scoped pinning and pinned-list highlighting propagate through Harmony without mutating IL2CPP lists.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL: {ex}");
    return 1;
}

static void VerifyDualTargetClaimsAndColorRoundTrip()
{
    AssertTrue(RuntimeTargetHighlightColor.TryParseExactHex("A1B2C3", out var rareColor), "Strict uppercase rare color was rejected.");
    AssertTrue(RuntimeTargetHighlightColor.TryParseExactHex("4D5E6F", out var normalColor), "Strict uppercase normal color was rejected.");
    AssertFalse(RuntimeTargetHighlightColor.TryParseExactHex("a1b2c3", out _), "Lowercase color bypassed the exact wire contract.");
    AssertFalse(RuntimeTargetHighlightColor.TryParseExactHex("#A1B2C3", out _), "Hash-prefixed color bypassed the exact wire contract.");

    RuntimeUiTargetSnapshot Create(
        RuntimeUiTargetKind kind,
        RuntimeTargetHighlightColor color,
        string trace,
        string orderKey,
        int beverageId,
        bool listPinningEnabled = true,
        bool recipeVariantEnabled = true,
        bool cookerHighlightEnabled = true,
        bool seatHighlightEnabled = true,
        bool orderHighlightEnabled = true)
    {
        return new RuntimeUiTargetSnapshot(
            kind,
            color,
            listPinningEnabled,
            recipeVariantEnabled,
            cookerHighlightEnabled,
            seatHighlightEnabled,
            orderHighlightEnabled,
            trace,
            orderKey,
            orderLifecycleSequence: kind == RuntimeUiTargetKind.Rare ? 10 : 11,
            deskCode: kind == RuntimeUiTargetKind.Rare ? 0 : 1,
            recipeId: 90,
            ingredientIds: new[] { 91 },
            extraIngredientIds: new[] { 92 },
            beverageId,
            cookerTypeId: 3,
            targetRevision: $"{kind}-target");
    }

    var targetSet = new RuntimeUiTargetSetSnapshot(
        generation: 1,
        sessionGeneration: RuntimeNightBusinessLifecycle.Generation,
        new[]
        {
            Create(RuntimeUiTargetKind.Rare, rareColor, "R-10", "", beverageId: 93),
            Create(RuntimeUiTargetKind.Normal, normalColor, "N-11", "ptr:b", beverageId: 94),
        });

    AssertEqual(RuntimeUiTargetKinds.Rare | RuntimeUiTargetKinds.Normal, targetSet.GetRecipeClaims(90), "Shared recipe did not retain both claims.");
    AssertEqual(RuntimeUiTargetKinds.None, targetSet.GetBaseRecipeClaims(90), "Variant-enabled targets leaked onto the authoritative base row.");
    AssertEqual(RuntimeUiTargetKinds.Rare | RuntimeUiTargetKinds.Normal, targetSet.GetRecipeVariantClaims(90), "Shared recipe variants did not retain both claims.");
    AssertTrue(targetSet.HasRecipeVariants(90), "Shared variant recipe was not classified as row-controlled.");
    AssertEqual(RuntimeUiTargetKinds.Rare | RuntimeUiTargetKinds.Normal, targetSet.GetIngredientClaims(91), "Shared ingredient did not retain both claims.");
    AssertEqual(RuntimeUiTargetKinds.Rare | RuntimeUiTargetKinds.Normal, targetSet.GetCookerClaims(3), "Shared cooker did not retain both claims.");
    AssertEqual(RuntimeUiTargetKinds.Rare, targetSet.GetBeverageClaims(93), "Rare-only beverage claim changed.");
    AssertEqual(RuntimeUiTargetKinds.Normal, targetSet.GetBeverageClaims(94), "Normal-only beverage claim changed.");

    var splitFeatureSet = new RuntimeUiTargetSetSnapshot(
        generation: 2,
        sessionGeneration: RuntimeNightBusinessLifecycle.Generation,
        new[]
        {
            Create(
                RuntimeUiTargetKind.Rare,
                rareColor,
                "R-12",
                "",
                beverageId: 93,
                listPinningEnabled: true,
                recipeVariantEnabled: false,
                cookerHighlightEnabled: false,
                seatHighlightEnabled: true,
                orderHighlightEnabled: false),
            Create(
                RuntimeUiTargetKind.Normal,
                normalColor,
                "N-13",
                "ptr:d",
                beverageId: 94,
                listPinningEnabled: false,
                recipeVariantEnabled: false,
                cookerHighlightEnabled: true,
                seatHighlightEnabled: false,
                orderHighlightEnabled: true),
        });
    AssertEqual(RuntimeUiTargetKinds.Rare, splitFeatureSet.GetRecipeClaims(90), "Normal list pinning leaked through a disabled normal target feature.");
    AssertEqual(RuntimeUiTargetKinds.Rare, splitFeatureSet.GetBaseRecipeClaims(90), "A non-variant recipe target did not remain on the base row.");
    AssertFalse(splitFeatureSet.HasRecipeVariants(90), "A disabled variant target made the recipe row-controlled.");
    AssertEqual(RuntimeUiTargetKinds.Normal, splitFeatureSet.GetCookerClaims(3), "Rare cooker highlighting leaked through a disabled rare target feature.");
    AssertTrue(splitFeatureSet.TryGetTarget(RuntimeUiTargetKind.Rare, out var splitRare), "Split feature set lost its rare target.");
    AssertTrue(splitFeatureSet.TryGetTarget(RuntimeUiTargetKind.Normal, out var splitNormal), "Split feature set lost its normal target.");
    AssertTrue(splitRare.SeatHighlightEnabled && !splitNormal.SeatHighlightEnabled, "Seat highlighting was not retained per target kind.");
    AssertTrue(!splitRare.OrderHighlightEnabled && splitNormal.OrderHighlightEnabled, "Order highlighting was not retained per target kind.");

    AssertThrows<ArgumentException>(
        () => Create(
            RuntimeUiTargetKind.Rare,
            rareColor,
            "R-14",
            "",
            beverageId: 93,
            listPinningEnabled: false,
            recipeVariantEnabled: true,
            cookerHighlightEnabled: false,
            seatHighlightEnabled: false,
            orderHighlightEnabled: false),
        "Recipe variants were accepted without the same target's list-pinning feature.");

    var palette = targetSet.Palette;
    var baseline = new Color(0.2f, 0.3f, 0.4f, 0.7f);
    var normalEndpointTime = MathF.PI / (2f * 2.75f);
    var rareEndpointTime = 3f * MathF.PI / (2f * 2.75f);
    AssertColor(
        RuntimeTargetHighlightStyle.BuildListItemPulseColor(
            baseline,
            RuntimeUiTargetKinds.Normal,
            palette,
            normalEndpointTime),
        RuntimeTargetHighlightStyle.BuildListItemPulseColor(
            baseline,
            RuntimeUiTargetKinds.Rare | RuntimeUiTargetKinds.Normal,
            palette,
            normalEndpointTime),
        "Shared highlight did not reach the normal color endpoint.");
    AssertColor(
        RuntimeTargetHighlightStyle.BuildListItemPulseColor(
            baseline,
            RuntimeUiTargetKinds.Rare,
            palette,
            rareEndpointTime),
        RuntimeTargetHighlightStyle.BuildListItemPulseColor(
            baseline,
            RuntimeUiTargetKinds.Rare | RuntimeUiTargetKinds.Normal,
            palette,
            rareEndpointTime),
        "Shared highlight did not return to the rare color endpoint.");
}

static void VerifyRuntimeUiTargetPublicationLease()
{
    var businessGeneration = RuntimeNightBusinessLifecycle.Generation;
    PublishRareTarget(
        businessGeneration,
        listPinningEnabled: true,
        recipeVariantEnabled: true,
        cookerHighlightEnabled: false,
        seatHighlightEnabled: false,
        orderHighlightEnabled: false,
        recipeId: 120,
        beverageId: -1,
        ingredientIds: new[] { 121 },
        extraIngredientIds: new[] { 122 },
        cookerTypeId: -1,
        deskCode: -1,
        orderTraceId: "",
        targetRevision: "publication-lease");
    var expected = RuntimeUiPinningService.ReadTargetSet();
    AssertTrue(
        RuntimeUiPinningService.TryAcquireTargetPublicationLease(expected, out var lease),
        "The current active target snapshot could not acquire its publication lease.");

    using var workerStarted = new ManualResetEventSlim();
    using var workerFinished = new ManualResetEventSlim();
    Exception? workerFailure = null;
    var worker = new Thread(() =>
    {
        try
        {
            workerStarted.Set();
            PublishRareTarget(
                businessGeneration,
                listPinningEnabled: true,
                recipeVariantEnabled: false,
                cookerHighlightEnabled: false,
                seatHighlightEnabled: false,
                orderHighlightEnabled: false,
                recipeId: 123,
                beverageId: -1,
                ingredientIds: new[] { 124 },
                extraIngredientIds: Array.Empty<int>(),
                cookerTypeId: -1,
                deskCode: -1,
                orderTraceId: "",
                targetRevision: "publication-after-lease");
        }
        catch (Exception ex)
        {
            workerFailure = ex;
        }
        finally
        {
            workerFinished.Set();
        }
    });
    worker.Start();
    AssertTrue(workerStarted.Wait(TimeSpan.FromSeconds(2)), "The publication worker did not start.");
    AssertFalse(workerFinished.Wait(TimeSpan.FromMilliseconds(100)), "Target publication escaped an active UI target lease.");
    lease.Dispose();
    AssertTrue(workerFinished.Wait(TimeSpan.FromSeconds(5)), "Target publication did not resume after the lease was released.");
    worker.Join();
    if (workerFailure != null) throw new InvalidOperationException("Target publication worker failed.", workerFailure);
    AssertFalse(
        RuntimeUiPinningService.TryAcquireTargetPublicationLease(expected, out _),
        "A stale target snapshot acquired a publication lease.");

    var current = RuntimeUiPinningService.ReadTargetSet();
    AssertTrue(
        RuntimeUiPinningService.TryAcquireTargetPublicationLease(current, out var threadAffineLease),
        "The replacement target snapshot could not acquire a publication lease.");
    Exception? wrongThreadRelease = null;
    var wrongThread = new Thread(() =>
    {
        try
        {
            threadAffineLease.Dispose();
        }
        catch (Exception ex)
        {
            wrongThreadRelease = ex;
        }
    });
    wrongThread.Start();
    wrongThread.Join();
    AssertTrue(wrongThreadRelease is InvalidOperationException, "A publication lease was released by a different thread.");
    threadAffineLease.Dispose();

    AssertTrue(
        RuntimeUiPinningService.TryAcquireTargetPublicationLease(
            businessGeneration,
            out var businessLease),
        "An active business generation could not acquire its snapshot-independent publication lease.");
    using var businessPublicationStarted = new ManualResetEventSlim();
    using var businessPublicationFinished = new ManualResetEventSlim();
    Exception? businessPublicationFailure = null;
    var businessPublicationWorker = new Thread(() =>
    {
        try
        {
            businessPublicationStarted.Set();
            PublishRareTarget(
                businessGeneration,
                listPinningEnabled: true,
                recipeVariantEnabled: false,
                cookerHighlightEnabled: false,
                seatHighlightEnabled: false,
                orderHighlightEnabled: false,
                recipeId: 125,
                beverageId: -1,
                ingredientIds: new[] { 126 },
                extraIngredientIds: Array.Empty<int>(),
                cookerTypeId: -1,
                deskCode: -1,
                orderTraceId: "",
                targetRevision: "business-publication-after-lease");
        }
        catch (Exception ex)
        {
            businessPublicationFailure = ex;
        }
        finally
        {
            businessPublicationFinished.Set();
        }
    });
    businessPublicationWorker.Start();
    AssertTrue(businessPublicationStarted.Wait(TimeSpan.FromSeconds(2)), "The business-lease publication worker did not start.");
    AssertFalse(
        businessPublicationFinished.Wait(TimeSpan.FromMilliseconds(100)),
        "Target publication escaped an active snapshot-independent business lease.");
    businessLease.Dispose();
    AssertTrue(
        businessPublicationFinished.Wait(TimeSpan.FromSeconds(5)),
        "Target publication did not resume after the snapshot-independent business lease was released.");
    businessPublicationWorker.Join();
    if (businessPublicationFailure != null)
    {
        throw new InvalidOperationException("Business-lease publication worker failed.", businessPublicationFailure);
    }

    AssertTrue(
        RuntimeUiPinningService.TryAcquireTargetPublicationLease(
            businessGeneration,
            out var rotatedTargetBusinessLease),
        "A target rotation incorrectly invalidated the same business generation's publication lease.");
    rotatedTargetBusinessLease.Dispose();

    RuntimeNightBusinessLifecycle.ActivateNextGeneration();
    AssertFalse(
        RuntimeUiPinningService.TryAcquireTargetPublicationLease(
            businessGeneration,
            out _),
        "A prior business generation acquired a publication lease after the lifecycle advanced.");
}

static void VerifyCookingRefreshHoldsExactTargetPublicationLease()
{
    CookingSelectionPanelProbe.ResetRefreshProbe();
    RunTimePlayerDataProbe.Reset(nativeResult: false);
    var businessGeneration = RuntimeNightBusinessLifecycle.Generation;
    var panel = new CookingSelectionPanelProbe();
    try
    {
        PublishRareTarget(
            businessGeneration,
            listPinningEnabled: true,
            recipeVariantEnabled: false,
            cookerHighlightEnabled: false,
            seatHighlightEnabled: false,
            orderHighlightEnabled: false,
            recipeId: 130,
            beverageId: -1,
            ingredientIds: new[] { 131 },
            extraIngredientIds: Array.Empty<int>(),
            cookerTypeId: -1,
            deskCode: -1,
            orderTraceId: "",
            targetRevision: "cooking-refresh-initial");
        panel.OnPanelOpen();

        PublishRareTarget(
            businessGeneration,
            listPinningEnabled: true,
            recipeVariantEnabled: false,
            cookerHighlightEnabled: false,
            seatHighlightEnabled: false,
            orderHighlightEnabled: false,
            recipeId: 132,
            beverageId: -1,
            ingredientIds: new[] { 133 },
            extraIngredientIds: Array.Empty<int>(),
            cookerTypeId: -1,
            deskCode: -1,
            orderTraceId: "",
            targetRevision: "cooking-refresh-leased");
        var exactRefreshTarget = RuntimeUiPinningService.ReadTargetSet();
        var sequenceStart = CookingSelectionPanelProbe.RefreshSequence.Count;

        using var publicationStarted = new ManualResetEventSlim();
        using var publicationFinished = new ManualResetEventSlim();
        Exception? publicationFailure = null;
        Thread? publicationWorker = null;
        var ingredientRefreshActionInvoked = false;
        var recipeRefreshActionInvoked = false;
        var ingredientSurfaceRefreshActionInvoked = false;
        var recipeSurfaceRefreshActionInvoked = false;
        var exactTargetObservedByAllStages = true;
        var directIngredientTargetsPinned = false;
        var directRecipeTargetPinned = false;
        var visibleIngredientTargetsPinned = false;
        var visibleRecipeTargetPinned = false;
        var publicationWorkerStarted = false;
        var publicationEscapedDuringIngredientRefresh = false;
        var publicationEscapedDuringRecipeRefresh = false;
        var publicationEscapedDuringIngredientSurfaceRefresh = false;
        var publicationEscapedDuringRecipeSurfaceRefresh = false;
        CookingSelectionPanelProbe.IngredientRefreshAction = () =>
        {
            ingredientRefreshActionInvoked = true;
            exactTargetObservedByAllStages &= ReferenceEquals(
                exactRefreshTarget,
                RuntimeUiPinningService.ReadTargetSet());
            directIngredientTargetsPinned = new[]
            {
                PlayerSaveFileDefaultPropProbe.IngredientsSeafood,
                PlayerSaveFileDefaultPropProbe.IngredientsMeat,
                PlayerSaveFileDefaultPropProbe.IngredientsVegetable,
                PlayerSaveFileDefaultPropProbe.IngredientsOther,
            }.All(ingredientType => RunTimePlayerDataProbe.CheckPinned(ingredientType, 133));
            publicationWorker = new Thread(() =>
            {
                try
                {
                    publicationStarted.Set();
                    PublishRareTarget(
                        businessGeneration,
                        listPinningEnabled: true,
                        recipeVariantEnabled: false,
                        cookerHighlightEnabled: false,
                        seatHighlightEnabled: false,
                        orderHighlightEnabled: false,
                        recipeId: 134,
                        beverageId: -1,
                        ingredientIds: new[] { 135 },
                        extraIngredientIds: Array.Empty<int>(),
                        cookerTypeId: -1,
                        deskCode: -1,
                        orderTraceId: "",
                        targetRevision: "cooking-refresh-post-publication");
                }
                catch (Exception ex)
                {
                    publicationFailure = ex;
                }
                finally
                {
                    publicationFinished.Set();
                }
            });
            publicationWorker.Start();
            publicationWorkerStarted = publicationStarted.Wait(TimeSpan.FromSeconds(2));
            if (publicationWorkerStarted)
            {
                publicationEscapedDuringIngredientRefresh = publicationFinished.Wait(
                    TimeSpan.FromMilliseconds(100));
            }
            return true;
        };
        CookingSelectionPanelProbe.RecipeRefreshAction = () =>
        {
            recipeRefreshActionInvoked = true;
            exactTargetObservedByAllStages &= ReferenceEquals(
                exactRefreshTarget,
                RuntimeUiPinningService.ReadTargetSet());
            directRecipeTargetPinned = RunTimePlayerDataProbe.CheckPinned(
                PlayerSaveFileDefaultPropProbe.Recipes,
                132);
            publicationEscapedDuringRecipeRefresh = publicationFinished.IsSet;
            return true;
        };
        CookingSelectionPanelProbe.IngredientSurfaceRefreshAction = () =>
        {
            ingredientSurfaceRefreshActionInvoked = true;
            exactTargetObservedByAllStages &= ReferenceEquals(
                exactRefreshTarget,
                RuntimeUiPinningService.ReadTargetSet());
            visibleIngredientTargetsPinned = new[]
            {
                PlayerSaveFileDefaultPropProbe.IngredientsSeafood,
                PlayerSaveFileDefaultPropProbe.IngredientsMeat,
                PlayerSaveFileDefaultPropProbe.IngredientsVegetable,
                PlayerSaveFileDefaultPropProbe.IngredientsOther,
            }.All(ingredientType => RunTimePlayerDataProbe.CheckPinned(ingredientType, 133));
            publicationEscapedDuringIngredientSurfaceRefresh = publicationFinished.IsSet;
        };
        CookingSelectionPanelProbe.RecipeSurfaceRefreshAction = () =>
        {
            recipeSurfaceRefreshActionInvoked = true;
            exactTargetObservedByAllStages &= ReferenceEquals(
                exactRefreshTarget,
                RuntimeUiPinningService.ReadTargetSet());
            visibleRecipeTargetPinned = RunTimePlayerDataProbe.CheckPinned(
                PlayerSaveFileDefaultPropProbe.Recipes,
                132);
            publicationEscapedDuringRecipeSurfaceRefresh = publicationFinished.IsSet;
        };

        RuntimeUiPinningService.Tick();
        AssertTrue(ingredientRefreshActionInvoked,
            "The cooking target refresh did not invoke UpdateIngField.");
        AssertTrue(recipeRefreshActionInvoked,
            "The cooking target refresh did not invoke UpdateRecipeField.");
        AssertTrue(ingredientSurfaceRefreshActionInvoked,
            "The cooking target refresh did not rebuild m_StaticIngredientsGroup after both backing-data stages.");
        AssertTrue(recipeSurfaceRefreshActionInvoked,
            "The cooking target refresh did not rebuild m_StaticRecipeGroup after UpdateRecipeField.");
        AssertTrue(exactTargetObservedByAllStages,
            "A cooking refresh stage observed a target snapshot other than its exact publication lease.");
        AssertTrue(directIngredientTargetsPinned,
            "Direct UpdateIngField did not use the cooking CheckPinned scope for all four ingredient categories.");
        AssertTrue(directRecipeTargetPinned,
            "Direct UpdateRecipeField did not use the cooking CheckPinned scope for its new recipe target.");
        AssertTrue(visibleIngredientTargetsPinned,
            "m_StaticIngredientsGroup.UpdateElements ran outside the exact cooking CheckPinned scope for an ingredient category.");
        AssertTrue(visibleRecipeTargetPinned,
            "m_StaticRecipeGroup.UpdateElements ran outside the exact cooking CheckPinned scope.");
        AssertTrue(publicationWorkerStarted, "The cooking-refresh publication worker did not start.");
        AssertFalse(
            publicationEscapedDuringIngredientRefresh,
            "Cooking refresh allowed the exact target snapshot to rotate before UpdateIngField returned.");
        AssertFalse(
            publicationEscapedDuringRecipeRefresh,
            "Cooking refresh released the exact target publication lease before UpdateRecipeField returned.");
        AssertFalse(
            publicationEscapedDuringIngredientSurfaceRefresh,
            "Cooking refresh released the exact target publication lease before m_StaticIngredientsGroup.UpdateElements returned.");
        AssertFalse(
            publicationEscapedDuringRecipeSurfaceRefresh,
            "Cooking refresh released the exact target publication lease before m_StaticRecipeGroup.UpdateElements returned.");
        AssertSequenceEqual(
            new[] { "ingredient-data", "recipe-data", "ingredient-visible", "recipe-visible" },
            CookingSelectionPanelProbe.RefreshSequence.Skip(sequenceStart),
            "The direct cooking refresh did not preserve the exact native four-stage order.");
        AssertEqual(1, CookingSelectionPanelProbe.FullVisualRefreshCount,
            "A target publication invoked full UpdateAllVisual instead of the bounded four-stage refresh.");
        AssertEqual(2, CookingSelectionPanelProbe.IngredientRefreshCount,
            "The open panel did not run exactly one natural and one direct ingredient-data refresh.");
        AssertEqual(2, CookingSelectionPanelProbe.RecipeRefreshCount,
            "The open panel did not run exactly one natural and one direct recipe-field refresh.");
        AssertEqual(2, CookingSelectionPanelProbe.IngredientSurfaceRefreshCount,
            "The open panel did not run exactly one natural and one direct ingredient-surface refresh.");
        AssertEqual(2, CookingSelectionPanelProbe.RecipeSurfaceRefreshCount,
            "The open panel did not run exactly one natural and one direct recipe-surface refresh.");
        AssertEqual(1, CookingSelectionPanelProbe.SelectedSurfaceRefreshCount,
            "The bounded target refresh rebuilt the selected-ingredient surface.");
        AssertEqual(1, CookingSelectionPanelProbe.OutputSurfaceRefreshCount,
            "The bounded target refresh rebuilt the output surface.");
        AssertTrue(
            publicationFinished.Wait(TimeSpan.FromSeconds(5)),
            "Target publication did not resume after the cooking refresh completed.");
        publicationWorker?.Join();
        if (publicationFailure != null)
        {
            throw new InvalidOperationException("Cooking-refresh publication worker failed.", publicationFailure);
        }
    }
    finally
    {
        CookingSelectionPanelProbe.IngredientRefreshAction = null;
        CookingSelectionPanelProbe.RecipeRefreshAction = null;
        CookingSelectionPanelProbe.IngredientSurfaceRefreshAction = null;
        CookingSelectionPanelProbe.RecipeSurfaceRefreshAction = null;
        panel.OnPanelClose();
    }
}

static void VerifyStorageRefreshHoldsExactTargetPublicationLease()
{
    StoragePanelProbe.ResetRefreshProbe();
    var businessGeneration = RuntimeNightBusinessLifecycle.Generation;
    var panel = new StoragePanelProbe();
    try
    {
        PublishRareTarget(
            businessGeneration,
            listPinningEnabled: true,
            recipeVariantEnabled: false,
            cookerHighlightEnabled: false,
            seatHighlightEnabled: false,
            orderHighlightEnabled: false,
            recipeId: -1,
            beverageId: 230,
            ingredientIds: Array.Empty<int>(),
            extraIngredientIds: Array.Empty<int>(),
            cookerTypeId: -1,
            deskCode: -1,
            orderTraceId: "",
            targetRevision: "storage-refresh-initial");
        panel.OnPanelOpen();

        PublishRareTarget(
            businessGeneration,
            listPinningEnabled: true,
            recipeVariantEnabled: false,
            cookerHighlightEnabled: false,
            seatHighlightEnabled: false,
            orderHighlightEnabled: false,
            recipeId: -1,
            beverageId: 231,
            ingredientIds: Array.Empty<int>(),
            extraIngredientIds: Array.Empty<int>(),
            cookerTypeId: -1,
            deskCode: -1,
            orderTraceId: "",
            targetRevision: "storage-refresh-leased");
        var exactRefreshTarget = RuntimeUiPinningService.ReadTargetSet();

        using var publicationStarted = new ManualResetEventSlim();
        using var publicationFinished = new ManualResetEventSlim();
        Exception? publicationFailure = null;
        Thread? publicationWorker = null;
        var publicationEscapedDuringDataRefresh = false;
        var publicationEscapedDuringSurfaceRefresh = false;
        var surfaceRefreshActionInvoked = false;
        var exactTargetObservedByBothStages = true;
        var dataTargetPinned = false;
        var visibleTargetPinned = false;
        StoragePanelProbe.RefreshAction = () =>
        {
            exactTargetObservedByBothStages &= ReferenceEquals(
                exactRefreshTarget,
                RuntimeUiPinningService.ReadTargetSet());
            dataTargetPinned = RunTimePlayerDataProbe.CheckPinned(
                PlayerSaveFileDefaultPropProbe.Beverages,
                231);
            publicationWorker = new Thread(() =>
            {
                try
                {
                    publicationStarted.Set();
                    PublishRareTarget(
                        businessGeneration,
                        listPinningEnabled: true,
                        recipeVariantEnabled: false,
                        cookerHighlightEnabled: false,
                        seatHighlightEnabled: false,
                        orderHighlightEnabled: false,
                        recipeId: -1,
                        beverageId: 232,
                        ingredientIds: Array.Empty<int>(),
                        extraIngredientIds: Array.Empty<int>(),
                        cookerTypeId: -1,
                        deskCode: -1,
                        orderTraceId: "",
                        targetRevision: "storage-refresh-post-publication");
                }
                catch (Exception ex)
                {
                    publicationFailure = ex;
                }
                finally
                {
                    publicationFinished.Set();
                }
            });
            publicationWorker.Start();
            AssertTrue(publicationStarted.Wait(TimeSpan.FromSeconds(2)),
                "The storage-refresh publication worker did not start.");
            publicationEscapedDuringDataRefresh = publicationFinished.Wait(TimeSpan.FromMilliseconds(100));
            return true;
        };
        StoragePanelProbe.SurfaceRefreshAction = () =>
        {
            surfaceRefreshActionInvoked = true;
            exactTargetObservedByBothStages &= ReferenceEquals(
                exactRefreshTarget,
                RuntimeUiPinningService.ReadTargetSet());
            visibleTargetPinned = RunTimePlayerDataProbe.CheckPinned(
                PlayerSaveFileDefaultPropProbe.Beverages,
                231);
            publicationEscapedDuringSurfaceRefresh = publicationFinished.IsSet;
        };

        RuntimeUiPinningService.Tick();
        AssertFalse(publicationEscapedDuringDataRefresh,
            "Storage refresh allowed target publication while UpdateBevField was active.");
        AssertTrue(surfaceRefreshActionInvoked,
            "Storage refresh did not rebuild m_BevsGroup after UpdateBevField.");
        AssertTrue(exactTargetObservedByBothStages,
            "A storage refresh stage observed a target other than its exact publication lease.");
        AssertTrue(dataTargetPinned,
            "Direct UpdateBevField did not use the exact beverage CheckPinned scope.");
        AssertTrue(visibleTargetPinned,
            "m_BevsGroup.UpdateElements ran outside the exact beverage CheckPinned scope.");
        AssertFalse(publicationEscapedDuringSurfaceRefresh,
            "Storage refresh released the target publication lease before m_BevsGroup.UpdateElements returned.");
        AssertTrue(publicationFinished.Wait(TimeSpan.FromSeconds(5)),
            "Target publication did not resume after the storage surface refresh completed.");
        publicationWorker?.Join();
        if (publicationFailure != null)
        {
            throw new InvalidOperationException("Storage-refresh publication worker failed.", publicationFailure);
        }
    }
    finally
    {
        StoragePanelProbe.RefreshAction = null;
        StoragePanelProbe.SurfaceRefreshAction = null;
        panel.OnPanelClose();
    }
}

static void VerifyEnabledEmptyTargetClearsVisuals()
{
    const int recipeId = 81;
    const int beverageId = 82;
    const int cookerTypeId = 3;
    var baseColor = new Color(0.62f, 0.66f, 0.72f, 0.55f);
    var businessGeneration = RuntimeNightBusinessLifecycle.Generation;
    CookingSelectionPanelProbe.ResetRefreshProbe();
    StoragePanelProbe.ResetRefreshProbe();
    CookingSelectionPanelProbe.RecipeBoundColor = baseColor;
    RunTimePlayerDataProbe.Reset(nativeResult: false);

    PublishRareTarget(
        businessGeneration,
        listPinningEnabled: true,
        recipeVariantEnabled: false,
        cookerHighlightEnabled: true,
        seatHighlightEnabled: false,
        orderHighlightEnabled: false,
        recipeId,
        beverageId,
        ingredientIds: new[] { 83 },
        extraIngredientIds: Array.Empty<int>(),
        cookerTypeId,
        deskCode: -1,
        orderTraceId: "",
        targetRevision: "test-target");

    var cookingPanel = new CookingSelectionPanelProbe();
    var storagePanel = new StoragePanelProbe();
    var recipeButton = new UIButtonSimpleProbe(baseColor);
    CookingSelectionPanelProbe.RecipeRefreshAction = () =>
        RunTimePlayerDataProbe.CheckPinned(PlayerSaveFileDefaultPropProbe.Recipes, recipeId);
    StoragePanelProbe.RefreshAction = () =>
        RunTimePlayerDataProbe.CheckPinned(PlayerSaveFileDefaultPropProbe.Beverages, beverageId);

    try
    {
        cookingPanel.OnPanelOpen();
        storagePanel.OnPanelOpen();
        cookingPanel.OnRecipeElementEnabled(new RecipeProbe(recipeId), new object(), recipeButton);
        RuntimePinnedListHighlightService.Tick();

        AssertTrue(CookingSelectionPanelProbe.LastRecipeResult == true, "The active recipe target did not use scoped pinning.");
        AssertTrue(StoragePanelProbe.LastResult == true, "The active beverage target did not use scoped pinning.");
        AssertEqual(0, RunTimePlayerDataProbe.NativeCallCount, "The active Mod targets called the native pinned probe.");
        AssertHighlighted(baseColor, recipeButton.image.get_color(), "The active recipe target was not highlighted.");
        AssertTrue(RuntimeCookerHighlightService.LastEnabled, "The active cooker target did not enable the cooker stub.");
        AssertEqual(cookerTypeId, RuntimeCookerHighlightService.LastCookerTypeId, "The cooker stub did not retain the active cooker type.");

        var activeTargetGeneration = RuntimeUiPinningService.ReadTargetSet().Generation;
        var cookingRefreshCount = CookingSelectionPanelProbe.RecipeRefreshCount;
        var storageRefreshCount = StoragePanelProbe.RefreshCount;
        var setterCountBeforeClear = recipeButton.image.SetterCount;

        RunOnWorkerThread(() => PublishRareTarget(
            businessGeneration,
            listPinningEnabled: true,
            recipeVariantEnabled: false,
            cookerHighlightEnabled: true,
            seatHighlightEnabled: false,
            orderHighlightEnabled: false,
            recipeId: -1,
            beverageId: -1,
            ingredientIds: Array.Empty<int>(),
            extraIngredientIds: Array.Empty<int>(),
            cookerTypeId: -1,
            deskCode: -1,
            orderTraceId: "",
            targetRevision: "test-target"));

        var emptyTarget = RuntimeUiPinningService.ReadTargetSet();
        AssertEqual(0, emptyTarget.Targets.Count, "An empty publication retained a featureless target.");
        AssertEqual(activeTargetGeneration + 1, emptyTarget.Generation, "An empty target did not advance the target generation.");
        AssertEqual(setterCountBeforeClear, recipeButton.image.SetterCount, "Publishing an empty target touched Unity color off the main thread.");
        AssertHighlighted(baseColor, recipeButton.image.get_color(), "Publishing an empty target restored the list highlight off the main thread.");
        AssertFalse(RuntimeCookerHighlightService.LastEnabled, "An empty cooker target left the cooker stub enabled.");
        AssertEqual(-1, RuntimeCookerHighlightService.LastCookerTypeId, "An empty cooker target retained the prior cooker type.");
        AssertEqual(businessGeneration, RuntimeCookerHighlightService.LastSessionGeneration, "An empty cooker target changed the business generation.");

        RuntimeUiPinningService.Tick();
        AssertEqual(cookingRefreshCount + 1, CookingSelectionPanelProbe.RecipeRefreshCount, "An open cooking panel did not refresh once for the empty target.");
        AssertEqual(storageRefreshCount + 1, StoragePanelProbe.RefreshCount, "An open storage panel did not refresh once for the empty target.");
        AssertTrue(CookingSelectionPanelProbe.LastRecipeResult == false, "The empty recipe target did not restore the native pinned result.");
        AssertTrue(StoragePanelProbe.LastResult == false, "The empty beverage target did not restore the native pinned result.");
        AssertEqual(2, RunTimePlayerDataProbe.NativeCallCount, "The empty target did not execute both native pinned probes.");

        RuntimePinnedListHighlightService.Tick();
        AssertColor(baseColor, recipeButton.image.get_color(), "The empty target did not restore the recipe highlight color.");
        AssertContains(RuntimePinnedListHighlightService.Status, "tracked=recipe:0", "The empty target kept the old recipe image tracked.");

        RuntimeUiPinningService.Tick();
        AssertEqual(cookingRefreshCount + 1, CookingSelectionPanelProbe.RecipeRefreshCount, "The empty target refreshed the cooking panel more than once.");
        AssertEqual(storageRefreshCount + 1, StoragePanelProbe.RefreshCount, "The empty target refreshed the storage panel more than once.");
    }
    finally
    {
        CookingSelectionPanelProbe.RecipeRefreshAction = null;
        StoragePanelProbe.RefreshAction = null;
        cookingPanel.OnPanelClose();
        storagePanel.OnPanelClose();
    }
}

static void VerifySharedListItemUsesSingleOwnershipAndBaseline()
{
    const int recipeId = 95;
    var businessGeneration = RuntimeNightBusinessLifecycle.Generation;
    var baseline = new Color(0.31f, 0.43f, 0.57f, 0.68f);
    var rareColor = new RuntimeTargetHighlightColor(0xE1, 0xB2, 0x31);
    var normalColor = new RuntimeTargetHighlightColor(0x42, 0x91, 0xC8);
    var palette = new RuntimeTargetHighlightPalette(rareColor, normalColor);
    RuntimeUiTargetSnapshot Create(
        RuntimeUiTargetKind kind,
        RuntimeTargetHighlightColor color,
        string trace,
        string orderKey,
        long lifecycle,
        int deskCode)
    {
        return new RuntimeUiTargetSnapshot(
            kind,
            color,
            listPinningEnabled: true,
            recipeVariantEnabled: false,
            cookerHighlightEnabled: false,
            seatHighlightEnabled: false,
            orderHighlightEnabled: false,
            trace,
            orderKey,
            lifecycle,
            deskCode,
            recipeId,
            ingredientIds: new[] { 96 },
            extraIngredientIds: Array.Empty<int>(),
            beverageId: -1,
            cookerTypeId: -1,
            targetRevision: $"shared-list-{kind}");
    }

    var targetSet = new RuntimeUiTargetSetSnapshot(
        generation: 0,
        sessionGeneration: businessGeneration,
        new[]
        {
            Create(RuntimeUiTargetKind.Rare, rareColor, "R-95", "", 95, 0),
            Create(RuntimeUiTargetKind.Normal, normalColor, "N-96", "ptr:60", 96, 1),
        });
    RuntimeUiPinningService.UpdateTargets(
        businessGeneration,
        targetSet.Targets);

    CookingSelectionPanelProbe.RecipeBoundColor = baseline;
    var panel = new CookingSelectionPanelProbe();
    var button = new UIButtonSimpleProbe(baseline);
    try
    {
        panel.OnPanelOpen();
        panel.OnRecipeElementEnabled(new RecipeProbe(recipeId), new object(), button);
        AssertContains(RuntimePinnedListHighlightService.Status, "tracked=recipe:1", "A shared recipe registered more than one Image ownership.");

        var normalEndpointTime = MathF.PI / (2f * 2.75f);
        Time.realtimeSinceStartup = normalEndpointTime;
        RuntimePinnedListHighlightService.Tick();
        AssertColor(
            RuntimeTargetHighlightStyle.BuildListItemPulseColor(
                baseline,
                RuntimeUiTargetKinds.Rare | RuntimeUiTargetKinds.Normal,
                palette,
                normalEndpointTime),
            button.image.get_color(),
            "The shared list Image did not reach the normal-color endpoint.");

        var rareEndpointTime = 3f * MathF.PI / (2f * 2.75f);
        Time.realtimeSinceStartup = rareEndpointTime;
        RuntimePinnedListHighlightService.Tick();
        AssertColor(
            RuntimeTargetHighlightStyle.BuildListItemPulseColor(
                baseline,
                RuntimeUiTargetKinds.Rare | RuntimeUiTargetKinds.Normal,
                palette,
                rareEndpointTime),
            button.image.get_color(),
            "The shared list Image did not return to the rare-color endpoint.");

        RuntimeUiPinningService.UpdateTargets(
            businessGeneration,
            Array.Empty<RuntimeUiTargetSnapshot>());
        RuntimePinnedListHighlightService.Tick();
        AssertColor(baseline, button.image.get_color(), "Disabling a shared list target did not restore its single original baseline.");
        AssertContains(RuntimePinnedListHighlightService.Status, "tracked=recipe:0", "Disabling a shared list target retained Image ownership.");
    }
    finally
    {
        panel.OnPanelClose();
        CookingSelectionPanelProbe.RecipeBoundColor = new Color(0.8f, 0.8f, 0.8f, 0.5f);
    }
}

static void VerifyOpenPanelRefreshScheduling()
{
    var mainThreadId = Environment.CurrentManagedThreadId;
    CookingSelectionPanelProbe.ResetRefreshProbe();
    StoragePanelProbe.ResetRefreshProbe();

    void Publish(int recipeId, int beverageId, bool listPinningEnabled = true)
    {
        PublishRareTarget(
            RuntimeNightBusinessLifecycle.Generation,
            listPinningEnabled,
            recipeVariantEnabled: false,
            cookerHighlightEnabled: false,
            seatHighlightEnabled: false,
            orderHighlightEnabled: false,
            recipeId,
            beverageId,
            ingredientIds: listPinningEnabled ? new[] { recipeId + 1000 } : Array.Empty<int>(),
            extraIngredientIds: Array.Empty<int>(),
            cookerTypeId: listPinningEnabled ? 3 : -1,
            deskCode: -1,
            orderTraceId: "",
            targetRevision: "test-target");
    }

    try
    {
        Publish(300, 400);
        var naturallyRefreshedCookingPanel = new CookingSelectionPanelProbe();
        var naturallyRefreshedStoragePanel = new StoragePanelProbe();
        naturallyRefreshedCookingPanel.OnPanelOpen();
        naturallyRefreshedStoragePanel.OnPanelOpen();
        AssertEqual(1, CookingSelectionPanelProbe.RecipeRefreshCount, "Cooking panel did not naturally apply an existing target.");
        AssertEqual(1, StoragePanelProbe.RefreshCount, "Storage panel did not naturally apply an existing target.");
        RuntimeUiPinningService.Tick();
        AssertEqual(1, CookingSelectionPanelProbe.RecipeRefreshCount, "Cooking panel repeated a target already applied during open.");
        AssertEqual(1, StoragePanelProbe.RefreshCount, "Storage panel repeated a target already applied during open.");
        naturallyRefreshedCookingPanel.OnPanelClose();
        naturallyRefreshedStoragePanel.OnPanelDestroyed();
        CookingSelectionPanelProbe.ResetRefreshProbe();
        StoragePanelProbe.ResetRefreshProbe();

        Publish(-1, -1, listPinningEnabled: false);
        var cookingPanel = new CookingSelectionPanelProbe();
        var storagePanel = new StoragePanelProbe();
        cookingPanel.OnPanelOpen();
        storagePanel.OnPanelOpen();
        AssertEqual(1, CookingSelectionPanelProbe.RecipeRefreshCount, "Cooking panel open did not perform its natural refresh.");
        AssertEqual(1, CookingSelectionPanelProbe.FullVisualRefreshCount,
            "Cooking panel open did not perform exactly one natural full refresh.");
        AssertEqual(1, StoragePanelProbe.RefreshCount, "Storage panel open did not perform its natural refresh.");

        RunOnWorkerThread(() => Publish(301, 401));
        AssertEqual(1, CookingSelectionPanelProbe.RecipeRefreshCount, "Worker target publication refreshed the cooking panel.");
        AssertEqual(1, StoragePanelProbe.RefreshCount, "Worker target publication refreshed the storage panel.");
        RuntimeUiPinningService.Tick();
        AssertEqual(2, CookingSelectionPanelProbe.RecipeRefreshCount, "An open cooking panel did not consume the new target once.");
        AssertEqual(1, CookingSelectionPanelProbe.FullVisualRefreshCount,
            "Target publication used full UpdateAllVisual instead of direct UpdateRecipeField.");
        AssertEqual(2, StoragePanelProbe.RefreshCount, "An open storage panel did not consume the new target once.");
        RuntimeUiPinningService.Tick();
        AssertEqual(2, CookingSelectionPanelProbe.RecipeRefreshCount, "Cooking panel refreshed twice for one target generation.");
        AssertEqual(2, StoragePanelProbe.RefreshCount, "Storage panel refreshed twice for one target generation.");

        RunOnWorkerThread(() => Publish(301, 401));
        RuntimeUiPinningService.Tick();
        AssertEqual(2, CookingSelectionPanelProbe.RecipeRefreshCount, "An identical target refreshed the cooking panel.");
        AssertEqual(2, StoragePanelProbe.RefreshCount, "An identical target refreshed the storage panel.");

        RunOnWorkerThread(() => Publish(302, 402));
        RuntimeUiPinningService.Tick();
        AssertEqual(3, CookingSelectionPanelProbe.RecipeRefreshCount, "A new target did not refresh the cooking panel exactly once.");
        AssertEqual(3, StoragePanelProbe.RefreshCount, "A new target did not refresh the storage panel exactly once.");

        cookingPanel.OnPanelClose();
        RunOnWorkerThread(() => Publish(303, 403));
        RuntimeUiPinningService.Tick();
        AssertEqual(3, CookingSelectionPanelProbe.RecipeRefreshCount, "A closed cooking panel was refreshed.");
        AssertEqual(4, StoragePanelProbe.RefreshCount, "The still-open storage panel did not refresh.");

        storagePanel.OnPanelDestroyed();
        RunOnWorkerThread(() => Publish(304, 404));
        RuntimeUiPinningService.Tick();
        AssertEqual(3, CookingSelectionPanelProbe.RecipeRefreshCount, "A closed cooking panel returned after another target.");
        AssertEqual(4, StoragePanelProbe.RefreshCount, "A destroyed storage panel was refreshed.");

        var staleCookingPanel = new CookingSelectionPanelProbe();
        var staleStoragePanel = new StoragePanelProbe();
        staleCookingPanel.OnPanelOpen();
        staleStoragePanel.OnPanelOpen();
        AssertEqual(4, CookingSelectionPanelProbe.RecipeRefreshCount, "Generation-mismatch cooking setup did not open.");
        AssertEqual(5, StoragePanelProbe.RefreshCount, "Generation-mismatch storage setup did not open.");
        RuntimeNightBusinessLifecycle.ActivateNextGeneration();
        RunOnWorkerThread(() => Publish(305, 405));
        RuntimeUiPinningService.Tick();
        AssertEqual(4, CookingSelectionPanelProbe.RecipeRefreshCount, "A prior-generation cooking panel was refreshed.");
        AssertEqual(5, StoragePanelProbe.RefreshCount, "A prior-generation storage panel was refreshed.");
        staleCookingPanel.OnPanelClose();
        staleStoragePanel.OnPanelDestroyed();

        var failingCookingPanel = new CookingSelectionPanelProbe();
        failingCookingPanel.OnPanelOpen();
        AssertEqual(5, CookingSelectionPanelProbe.RecipeRefreshCount, "Failure-path cooking setup did not open.");
        RunOnWorkerThread(() => Publish(306, 406));
        CookingSelectionPanelProbe.ThrowOnRecipeRefresh = true;
        RuntimeUiPinningService.Tick();
        AssertEqual(6, CookingSelectionPanelProbe.RecipeRefreshCount, "The failing cooking refresh was not attempted once.");
        RuntimeUiPinningService.Tick();
        AssertEqual(6, CookingSelectionPanelProbe.RecipeRefreshCount, "A failed cooking refresh retried without a new target.");
        AssertContains(RuntimeUiPinningService.Status, "failures:1", "Panel refresh failure diagnostics were not retained.");

        CookingSelectionPanelProbe.ThrowOnRecipeRefresh = false;
        RunOnWorkerThread(() => Publish(307, 407));
        RuntimeUiPinningService.Tick();
        AssertEqual(7, CookingSelectionPanelProbe.RecipeRefreshCount, "A later target did not recover after the one-shot failure.");
        failingCookingPanel.OnPanelClose();

        AssertTrue(
            CookingSelectionPanelProbe.RecipeRefreshThreadIds.All(threadId => threadId == mainThreadId),
            "A cooking panel refresh ran outside the Unity main thread.");
        AssertTrue(
            StoragePanelProbe.RefreshThreadIds.All(threadId => threadId == mainThreadId),
            "A storage panel refresh ran outside the Unity main thread.");
    }
    finally
    {
        CookingSelectionPanelProbe.ThrowOnRecipeRefresh = false;
        StoragePanelProbe.ThrowOnRefresh = false;
    }
}

static void VerifyOpenPanelSurfaceRefreshSemantics()
{
    CookingSelectionPanelProbe.ResetRefreshProbe();
    StoragePanelProbe.ResetRefreshProbe();
    var businessGeneration = RuntimeNightBusinessLifecycle.Generation;
    var cookingPanel = new CookingSelectionPanelProbe();
    var storagePanel = new StoragePanelProbe();
    CookingSelectionPanelProbe.IngredientRefreshAction = () =>
    {
        var ingredientIds = RuntimeUiPinningService.ReadTargetSet().Targets
            .Where(target => target.ListPinningEnabled)
            .SelectMany(target => target.IngredientIds)
            .Distinct()
            .OrderBy(ingredientId => ingredientId)
            .ToArray();
        cookingPanel.SetIngredientSurfaceSource(ingredientIds);
        return true;
    };
    CookingSelectionPanelProbe.RecipeRefreshAction = () =>
    {
        var recipeIds = RuntimeUiPinningService.ReadTargetSet().Targets
            .Where(target => target.ListPinningEnabled && target.RecipeId >= 0)
            .Select(target => target.RecipeId)
            .ToArray();
        cookingPanel.SetRecipeSurfaceSource(recipeIds);
        return true;
    };
    StoragePanelProbe.RefreshAction = () =>
    {
        var beverageIds = RuntimeUiPinningService.ReadTargetSet().Targets
            .Where(target => target.ListPinningEnabled && target.BeverageId >= 0)
            .Select(target => target.BeverageId)
            .ToArray();
        storagePanel.SetBeverageSurfaceSource(beverageIds);
        return true;
    };

    try
    {
        PublishRareTarget(
            businessGeneration,
            listPinningEnabled: true,
            recipeVariantEnabled: true,
            cookerHighlightEnabled: false,
            seatHighlightEnabled: false,
            orderHighlightEnabled: false,
            recipeId: 510,
            beverageId: 610,
            ingredientIds: new[] { 511 },
            extraIngredientIds: new[] { 512 },
            cookerTypeId: -1,
            deskCode: -1,
            orderTraceId: "",
            targetRevision: "surface-enabled");
        cookingPanel.OnPanelOpen();
        storagePanel.OnPanelOpen();
        AssertSequenceEqual(new[] { 511 }, cookingPanel.VisibleIngredientIds,
            "Natural cooking open did not materialize its ingredient surface.");
        AssertSequenceEqual(new[] { 510 }, cookingPanel.VisibleRecipeIds,
            "Natural cooking open did not materialize its recipe surface.");
        AssertSequenceEqual(new[] { 610 }, storagePanel.VisibleBeverageIds,
            "Natural storage open did not materialize its beverage surface.");

        PublishRareTarget(
            businessGeneration,
            listPinningEnabled: false,
            recipeVariantEnabled: false,
            cookerHighlightEnabled: false,
            seatHighlightEnabled: false,
            orderHighlightEnabled: false,
            recipeId: -1,
            beverageId: -1,
            ingredientIds: Array.Empty<int>(),
            extraIngredientIds: Array.Empty<int>(),
            cookerTypeId: -1,
            deskCode: -1,
            orderTraceId: "",
            targetRevision: "surface-disabled");
        RuntimeUiPinningService.Tick();
        AssertSequenceEqual(Array.Empty<int>(), cookingPanel.VisibleIngredientIds,
            "Disabling the open cooking feature rebuilt recipes but left stale ingredient rows visible.");
        AssertSequenceEqual(Array.Empty<int>(), cookingPanel.VisibleRecipeIds,
            "Disabling the open cooking feature rebuilt data but left stale recipe rows visible.");
        AssertSequenceEqual(Array.Empty<int>(), storagePanel.VisibleBeverageIds,
            "Disabling the open beverage feature rebuilt data but left stale beverage rows visible.");
        AssertEqual(1, CookingSelectionPanelProbe.SelectedSurfaceRefreshCount,
            "The bounded target refresh unexpectedly rebuilt the selected-ingredient surface.");
        AssertEqual(1, CookingSelectionPanelProbe.OutputSurfaceRefreshCount,
            "The bounded target refresh unexpectedly rebuilt the output surface.");

        var cookingDataCountBeforeFailure = CookingSelectionPanelProbe.RecipeRefreshCount;
        var cookingSurfaceCountBeforeFailure = CookingSelectionPanelProbe.RecipeSurfaceRefreshCount;
        var cookingAppliedBeforeFailure = ReadPanelRefreshGeneration("cooking", "applied");
        var panelFailuresBefore = ReadPanelRefreshCounter("failures");
        CookingSelectionPanelProbe.ThrowOnRecipeSurfaceRefresh = true;
        PublishRareTarget(
            businessGeneration,
            listPinningEnabled: true,
            recipeVariantEnabled: false,
            cookerHighlightEnabled: false,
            seatHighlightEnabled: false,
            orderHighlightEnabled: false,
            recipeId: 520,
            beverageId: 620,
            ingredientIds: new[] { 521 },
            extraIngredientIds: Array.Empty<int>(),
            cookerTypeId: -1,
            deskCode: -1,
            orderTraceId: "",
            targetRevision: "surface-failure");
        RuntimeUiPinningService.Tick();
        AssertEqual(cookingDataCountBeforeFailure + 1, CookingSelectionPanelProbe.RecipeRefreshCount,
            "Cooking surface failure did not first rebuild recipe data exactly once.");
        AssertEqual(cookingSurfaceCountBeforeFailure + 1, CookingSelectionPanelProbe.RecipeSurfaceRefreshCount,
            "Cooking surface failure did not attempt m_StaticRecipeGroup.UpdateElements exactly once.");
        AssertEqual(panelFailuresBefore + 1, ReadPanelRefreshCounter("failures"),
            "Cooking surface failure was not retained exactly once in panel diagnostics.");
        AssertEqual(cookingAppliedBeforeFailure, ReadPanelRefreshGeneration("cooking", "applied"),
            "Cooking data completion was incorrectly committed before the visible group refresh succeeded.");
        AssertContains(RuntimeUiPinningService.Status, "stage=recipe-visible-elements",
            "Cooking logical-group failure diagnostics lost the exact refresh stage.");
        RuntimeUiPinningService.Tick();
        AssertEqual(cookingDataCountBeforeFailure + 1, CookingSelectionPanelProbe.RecipeRefreshCount,
            "A partial cooking surface failure retried within the same target generation.");

        CookingSelectionPanelProbe.ThrowOnRecipeSurfaceRefresh = false;
        cookingPanel.OnPanelClose();
        storagePanel.OnPanelClose();

        CookingSelectionPanelProbe.ResetRefreshProbe();
        StoragePanelProbe.ResetRefreshProbe();
        var foodStoragePanel = new StoragePanelProbe(SellableTypeProbe.Food);
        foodStoragePanel.OnPanelOpen();
        AssertEqual(0, StoragePanelProbe.RefreshCount,
            "A food-mode storage panel unexpectedly rebuilt beverage data during natural open.");
        PublishRareTarget(
            businessGeneration,
            listPinningEnabled: true,
            recipeVariantEnabled: false,
            cookerHighlightEnabled: false,
            seatHighlightEnabled: false,
            orderHighlightEnabled: false,
            recipeId: 530,
            beverageId: 630,
            ingredientIds: new[] { 531 },
            extraIngredientIds: Array.Empty<int>(),
            cookerTypeId: -1,
            deskCode: -1,
            orderTraceId: "",
            targetRevision: "food-storage-not-beverage");
        RuntimeUiPinningService.Tick();
        AssertEqual(0, StoragePanelProbe.RefreshCount,
            "A food-mode storage panel was registered as a beverage surface.");
        AssertEqual(0, StoragePanelProbe.SurfaceRefreshCount,
            "A food-mode storage panel rebuilt m_BevsGroup after target publication.");
        foodStoragePanel.OnPanelClose();
    }
    finally
    {
        CookingSelectionPanelProbe.ThrowOnRecipeSurfaceRefresh = false;
        StoragePanelProbe.ThrowOnSurfaceRefresh = false;
        CookingSelectionPanelProbe.IngredientRefreshAction = null;
        CookingSelectionPanelProbe.RecipeRefreshAction = null;
        StoragePanelProbe.RefreshAction = null;
        cookingPanel.OnPanelClose();
        storagePanel.OnPanelClose();
    }
}

static void VerifyCookingRefreshStagesFailClosed()
{
    CookingSelectionPanelProbe.ResetRefreshProbe();
    var businessGeneration = RuntimeNightBusinessLifecycle.Generation;
    var cookingPanel = new CookingSelectionPanelProbe();

    void Publish(int targetId)
    {
        PublishRareTarget(
            businessGeneration,
            listPinningEnabled: true,
            recipeVariantEnabled: false,
            cookerHighlightEnabled: false,
            seatHighlightEnabled: false,
            orderHighlightEnabled: false,
            recipeId: 700 + targetId,
            beverageId: -1,
            ingredientIds: new[] { 800 + targetId },
            extraIngredientIds: Array.Empty<int>(),
            cookerTypeId: -1,
            deskCode: -1,
            orderTraceId: "",
            targetRevision: $"cooking-stage-{targetId}");
    }

    void AssertFailure(
        int targetId,
        string expectedStage,
        Action armFailure,
        Action disarmFailure,
        int expectedIngredientDataDelta,
        int expectedRecipeDataDelta,
        int expectedIngredientSurfaceDelta,
        int expectedRecipeSurfaceDelta)
    {
        var ingredientDataBefore = CookingSelectionPanelProbe.IngredientRefreshCount;
        var recipeDataBefore = CookingSelectionPanelProbe.RecipeRefreshCount;
        var ingredientSurfaceBefore = CookingSelectionPanelProbe.IngredientSurfaceRefreshCount;
        var recipeSurfaceBefore = CookingSelectionPanelProbe.RecipeSurfaceRefreshCount;
        var appliedBefore = ReadPanelRefreshGeneration("cooking", "applied");
        var failuresBefore = ReadPanelRefreshCounter("failures");

        armFailure();
        Publish(targetId);
        RuntimeUiPinningService.Tick();
        disarmFailure();

        AssertEqual(ingredientDataBefore + expectedIngredientDataDelta,
            CookingSelectionPanelProbe.IngredientRefreshCount,
            $"{expectedStage} executed an unexpected number of ingredient-data stages.");
        AssertEqual(recipeDataBefore + expectedRecipeDataDelta,
            CookingSelectionPanelProbe.RecipeRefreshCount,
            $"{expectedStage} executed an unexpected number of recipe-data stages.");
        AssertEqual(ingredientSurfaceBefore + expectedIngredientSurfaceDelta,
            CookingSelectionPanelProbe.IngredientSurfaceRefreshCount,
            $"{expectedStage} executed an unexpected number of ingredient visible stages.");
        AssertEqual(recipeSurfaceBefore + expectedRecipeSurfaceDelta,
            CookingSelectionPanelProbe.RecipeSurfaceRefreshCount,
            $"{expectedStage} executed an unexpected number of recipe visible stages.");
        AssertEqual(appliedBefore, ReadPanelRefreshGeneration("cooking", "applied"),
            $"{expectedStage} incorrectly committed a partial cooking refresh.");
        AssertEqual(failuresBefore + 1, ReadPanelRefreshCounter("failures"),
            $"{expectedStage} was not counted as exactly one panel refresh failure.");
        AssertContains(RuntimeUiPinningService.Status, $"stage={expectedStage}",
            $"{expectedStage} diagnostics lost the exact failed stage.");

        RuntimeUiPinningService.Tick();
        AssertEqual(ingredientDataBefore + expectedIngredientDataDelta,
            CookingSelectionPanelProbe.IngredientRefreshCount,
            $"{expectedStage} retried within the same target generation.");
        AssertEqual(failuresBefore + 1, ReadPanelRefreshCounter("failures"),
            $"{expectedStage} recorded the same generation more than once.");
    }

    try
    {
        PublishRareTarget(
            businessGeneration,
            listPinningEnabled: false,
            recipeVariantEnabled: false,
            cookerHighlightEnabled: false,
            seatHighlightEnabled: false,
            orderHighlightEnabled: false,
            recipeId: -1,
            beverageId: -1,
            ingredientIds: Array.Empty<int>(),
            extraIngredientIds: Array.Empty<int>(),
            cookerTypeId: -1,
            deskCode: -1,
            orderTraceId: "",
            targetRevision: "cooking-stage-baseline");
        cookingPanel.OnPanelOpen();

        AssertFailure(
            1,
            "ingredient-backing-data",
            () => CookingSelectionPanelProbe.ThrowOnIngredientRefresh = true,
            () => CookingSelectionPanelProbe.ThrowOnIngredientRefresh = false,
            expectedIngredientDataDelta: 1,
            expectedRecipeDataDelta: 0,
            expectedIngredientSurfaceDelta: 0,
            expectedRecipeSurfaceDelta: 0);
        AssertFailure(
            2,
            "recipe-backing-data",
            () => CookingSelectionPanelProbe.ThrowOnRecipeRefresh = true,
            () => CookingSelectionPanelProbe.ThrowOnRecipeRefresh = false,
            expectedIngredientDataDelta: 1,
            expectedRecipeDataDelta: 1,
            expectedIngredientSurfaceDelta: 0,
            expectedRecipeSurfaceDelta: 0);
        AssertFailure(
            3,
            "ingredient-visible-elements",
            () => CookingSelectionPanelProbe.ThrowOnIngredientSurfaceRefresh = true,
            () => CookingSelectionPanelProbe.ThrowOnIngredientSurfaceRefresh = false,
            expectedIngredientDataDelta: 1,
            expectedRecipeDataDelta: 1,
            expectedIngredientSurfaceDelta: 1,
            expectedRecipeSurfaceDelta: 0);
        AssertFailure(
            4,
            "recipe-visible-elements",
            () => CookingSelectionPanelProbe.ThrowOnRecipeSurfaceRefresh = true,
            () => CookingSelectionPanelProbe.ThrowOnRecipeSurfaceRefresh = false,
            expectedIngredientDataDelta: 1,
            expectedRecipeDataDelta: 1,
            expectedIngredientSurfaceDelta: 1,
            expectedRecipeSurfaceDelta: 1);

        var successSequenceStart = CookingSelectionPanelProbe.RefreshSequence.Count;
        Publish(5);
        var successfulTargetGeneration = RuntimeUiPinningService.ReadTargetSet().Generation;
        RuntimeUiPinningService.Tick();
        AssertEqual(successfulTargetGeneration, ReadPanelRefreshGeneration("cooking", "applied"),
            "A later target did not recover after all four one-shot cooking stage failures.");
        AssertSequenceEqual(
            new[] { "ingredient-data", "recipe-data", "ingredient-visible", "recipe-visible" },
            CookingSelectionPanelProbe.RefreshSequence.Skip(successSequenceStart),
            "A recovered cooking refresh did not execute its four stages exactly once and in order.");
        AssertEqual(1, CookingSelectionPanelProbe.SelectedSurfaceRefreshCount,
            "Cooking stage failures or recovery rebuilt the selected-ingredient surface.");
        AssertEqual(1, CookingSelectionPanelProbe.OutputSurfaceRefreshCount,
            "Cooking stage failures or recovery rebuilt the output surface.");
    }
    finally
    {
        CookingSelectionPanelProbe.ThrowOnIngredientRefresh = false;
        CookingSelectionPanelProbe.ThrowOnRecipeRefresh = false;
        CookingSelectionPanelProbe.ThrowOnIngredientSurfaceRefresh = false;
        CookingSelectionPanelProbe.ThrowOnRecipeSurfaceRefresh = false;
        cookingPanel.OnPanelClose();
    }
}

static void VerifyLifecycleGenerationGuards()
{
    var firstGeneration = RuntimeNightBusinessLifecycle.Generation;
    PublishRareTarget(
        firstGeneration,
        listPinningEnabled: true,
        recipeVariantEnabled: false,
        cookerHighlightEnabled: true,
        seatHighlightEnabled: false,
        orderHighlightEnabled: true,
        recipeId: 90,
        beverageId: 91,
        ingredientIds: new[] { 92 },
        extraIngredientIds: Array.Empty<int>(),
        cookerTypeId: 3,
        deskCode: 1,
        orderTraceId: "R-9001",
        targetRevision: "test-target");

    InvokePrivate("OnCookingRefreshStarted", new CookingSelectionPanelProbe());
    try
    {
        AssertTrue(InvokeCheckPinned(1, 90), "The active generation did not force its recipe target.");
        RuntimeNightBusinessLifecycle.BeginClosing();
        AssertTrue(InvokeCheckPinnedPrefix(1, 90).RunOriginal, "A scope captured before Closing still skipped native CheckPinned.");
        AssertEqual(0, RuntimeUiPinningService.ReadTargetSet().Targets.Count, "Closing exposed the stale generation target.");
    }
    finally
    {
        InvokePrivate("OnCookingRefreshFinalized", new object?[] { null });
    }

    AssertThrows<InvalidOperationException>(
        () => PublishRareTarget(
            firstGeneration,
            listPinningEnabled: true,
            recipeVariantEnabled: false,
            cookerHighlightEnabled: false,
            seatHighlightEnabled: false,
            orderHighlightEnabled: false,
            recipeId: 93,
            beverageId: 94,
            ingredientIds: Array.Empty<int>(),
            extraIngredientIds: Array.Empty<int>(),
            cookerTypeId: 3,
            deskCode: -1,
            orderTraceId: "",
            targetRevision: "test-target"),
        "Closing accepted a UI target publication.");

    RuntimeUiPinningService.InvalidateTarget(firstGeneration, "test closing");
    AssertEqual(0, RuntimeUiPinningService.ReadTargetSet().Targets.Count, "Invalidation retained a UI target.");
    AssertFalse(RuntimeOrderHighlightService.LastEnabled, "Invalidation left the HUD order-highlight target enabled.");
    AssertFalse(RuntimeThrowDeliverOrderHighlightService.LastEnabled, "Invalidation left the throw-delivery order-highlight target enabled.");
    AssertEqual("", RuntimeOrderHighlightService.LastOrderTraceId, "Invalidation retained the HUD order trace.");
    AssertEqual("", RuntimeThrowDeliverOrderHighlightService.LastOrderTraceId, "Invalidation retained the throw-delivery order trace.");
    AssertEqual(-1, RuntimeOrderHighlightService.LastDeskCode, "Invalidation retained the HUD target desk.");
    AssertEqual(-1, RuntimeThrowDeliverOrderHighlightService.LastDeskCode, "Invalidation retained the throw-delivery target desk.");
    AssertEqual(firstGeneration, RuntimeOrderHighlightService.LastSessionGeneration, "Invalidation cleared the HUD target with the wrong generation.");
    AssertEqual(firstGeneration, RuntimeThrowDeliverOrderHighlightService.LastSessionGeneration, "Invalidation cleared the throw-delivery target with the wrong generation.");

    RuntimeNightBusinessLifecycle.ActivateNextGeneration();
    var secondGeneration = RuntimeNightBusinessLifecycle.Generation;
    AssertEqual(firstGeneration + 1, secondGeneration, "The next business session did not advance its generation.");
    AssertThrows<InvalidOperationException>(
        () => PublishRareTarget(
            firstGeneration,
            listPinningEnabled: true,
            recipeVariantEnabled: false,
            cookerHighlightEnabled: false,
            seatHighlightEnabled: false,
            orderHighlightEnabled: false,
            recipeId: 95,
            beverageId: 96,
            ingredientIds: Array.Empty<int>(),
            extraIngredientIds: Array.Empty<int>(),
            cookerTypeId: 3,
            deskCode: -1,
            orderTraceId: "",
            targetRevision: "test-target"),
        "A stale generation target was accepted by the next business session.");

    RunOnWorkerThread(() => PublishRareTarget(
        secondGeneration,
        listPinningEnabled: true,
        recipeVariantEnabled: false,
        cookerHighlightEnabled: false,
        seatHighlightEnabled: false,
        orderHighlightEnabled: false,
        recipeId: 97,
        beverageId: 98,
        ingredientIds: new[] { 99 },
        extraIngredientIds: Array.Empty<int>(),
        cookerTypeId: 3,
        deskCode: -1,
        orderTraceId: "",
        targetRevision: "test-target"));
    InvokePrivate("OnCookingRefreshStarted", new CookingSelectionPanelProbe());
    try
    {
        AssertTrue(InvokeCheckPinned(1, 97), "The next generation did not accept its fresh target.");
        AssertFalse(InvokeCheckPinned(1, 90), "The next generation reused the previous target.");
    }
    finally
    {
        InvokePrivate("OnCookingRefreshFinalized", new object?[] { null });
    }

}

static void VerifyIdenticalTargetPublicationIsIdempotent()
{
    const int recipeId = 71;
    const int beverageId = 72;
    const int cookerTypeId = 3;
    var ingredientIds = new[] { 11, 29 };

    void Publish(
        bool nextListPinningEnabled = true,
        bool nextRecipeVariantEnabled = false,
        bool nextCookerHighlightEnabled = true,
        bool nextSeatHighlightEnabled = false,
        bool nextOrderHighlightEnabled = false,
        int nextRecipeId = recipeId,
        int nextBeverageId = beverageId,
        int[]? nextIngredientIds = null,
        int[]? nextExtraIngredientIds = null,
        int nextCookerTypeId = cookerTypeId,
        int nextDeskCode = -1,
        string nextOrderTraceId = "",
        string nextTargetRevision = "test-target")
    {
        PublishRareTarget(
            RuntimeNightBusinessLifecycle.Generation,
            nextListPinningEnabled,
            nextRecipeVariantEnabled,
            nextCookerHighlightEnabled,
            nextSeatHighlightEnabled,
            nextOrderHighlightEnabled,
            nextRecipeId,
            nextBeverageId,
            nextIngredientIds ?? ingredientIds,
            nextExtraIngredientIds ?? Array.Empty<int>(),
            nextCookerTypeId,
            nextDeskCode,
            nextOrderTraceId,
            nextTargetRevision);
    }

    void AssertPublishes(Action publish, string fieldName)
    {
        var logCount = ManualLogSource.InformationCount;
        var highlightUpdateCount = RuntimeCookerHighlightService.UpdateCount;
        var seatUpdateCount = RuntimeSeatHighlightService.UpdateCount;
        var orderUpdateCount = RuntimeOrderHighlightService.UpdateCount;
        var throwDeliveryOrderUpdateCount = RuntimeThrowDeliverOrderHighlightService.UpdateCount;
        publish();
        AssertEqual(logCount + 1, ManualLogSource.InformationCount, $"Changing {fieldName} did not publish a target log.");
        AssertEqual(highlightUpdateCount + 1, RuntimeCookerHighlightService.UpdateCount, $"Changing {fieldName} did not update cooker highlighting.");
        AssertEqual(seatUpdateCount + 1, RuntimeSeatHighlightService.UpdateCount, $"Changing {fieldName} did not update seat highlighting.");
        AssertEqual(orderUpdateCount + 1, RuntimeOrderHighlightService.UpdateCount, $"Changing {fieldName} did not update order highlighting.");
        AssertEqual(throwDeliveryOrderUpdateCount + 1, RuntimeThrowDeliverOrderHighlightService.UpdateCount, $"Changing {fieldName} did not update throw-delivery order highlighting.");
        Publish();
    }

    Publish();
    var targetGeneration = RuntimeUiPinningService.ReadTargetSet().Generation;
    var initialLogCount = ManualLogSource.InformationCount;
    var initialHighlightUpdateCount = RuntimeCookerHighlightService.UpdateCount;
    var initialSeatUpdateCount = RuntimeSeatHighlightService.UpdateCount;
    var initialOrderUpdateCount = RuntimeOrderHighlightService.UpdateCount;
    var initialThrowDeliveryOrderUpdateCount = RuntimeThrowDeliverOrderHighlightService.UpdateCount;

    Publish(nextIngredientIds: new[] { 11, 29 });

    AssertEqual(targetGeneration, RuntimeUiPinningService.ReadTargetSet().Generation, "An identical target advanced its generation.");
    AssertEqual(initialLogCount, ManualLogSource.InformationCount, "An identical target wrote another information log.");
    AssertEqual(initialHighlightUpdateCount, RuntimeCookerHighlightService.UpdateCount, "An identical target updated cooker highlighting again.");
    AssertEqual(initialSeatUpdateCount, RuntimeSeatHighlightService.UpdateCount, "An identical target updated seat highlighting again.");
    AssertEqual(initialOrderUpdateCount, RuntimeOrderHighlightService.UpdateCount, "An identical target updated order highlighting again.");
    AssertEqual(initialThrowDeliveryOrderUpdateCount, RuntimeThrowDeliverOrderHighlightService.UpdateCount, "An identical target updated throw-delivery order highlighting again.");

    AssertPublishes(() => Publish(nextListPinningEnabled: false), "listPinningEnabled");
    AssertPublishes(() => Publish(nextCookerHighlightEnabled: false), "cookerHighlightEnabled");
    AssertPublishes(() => Publish(nextRecipeVariantEnabled: true), "recipeVariantEnabled");
    AssertPublishes(() => Publish(nextSeatHighlightEnabled: true, nextDeskCode: 2), "seatHighlightEnabled");
    AssertPublishes(
        () => Publish(nextOrderHighlightEnabled: true, nextDeskCode: 2, nextOrderTraceId: "R-0001"),
        "orderHighlightEnabled");
    AssertPublishes(() => Publish(nextRecipeId: recipeId + 1), "recipeId");
    AssertPublishes(() => Publish(nextBeverageId: beverageId + 1), "beverageId");
    AssertPublishes(() => Publish(nextIngredientIds: new[] { 11, 30 }), "ingredientIds");
    AssertPublishes(() => Publish(nextExtraIngredientIds: new[] { 30 }), "extraIngredientIds");
    AssertPublishes(() => Publish(nextCookerTypeId: cookerTypeId + 1), "cookerTypeId");
    AssertPublishes(() => Publish(nextSeatHighlightEnabled: true, nextDeskCode: 3), "deskCode");
    AssertPublishes(
        () => Publish(nextOrderHighlightEnabled: true, nextDeskCode: 2, nextOrderTraceId: "R-0002"),
        "orderTraceId");
    AssertPublishes(() => Publish(nextTargetRevision: "next-target"), "targetRevision");
}

static void VerifyOrderHighlightSurfaceIsolation()
{
    var businessGeneration = RuntimeNightBusinessLifecycle.Generation;

    void Publish(string traceId, string targetRevision)
    {
        PublishRareTarget(
            businessGeneration,
            listPinningEnabled: false,
            recipeVariantEnabled: false,
            cookerHighlightEnabled: false,
            seatHighlightEnabled: false,
            orderHighlightEnabled: true,
            recipeId: -1,
            beverageId: -1,
            ingredientIds: Array.Empty<int>(),
            extraIngredientIds: Array.Empty<int>(),
            cookerTypeId: -1,
            deskCode: 2,
            orderTraceId: traceId,
            targetRevision: targetRevision);
    }

    var warningCount = ManualLogSource.WarningCount;
    RuntimeOrderHighlightService.ThrowOnUpdate = true;
    try
    {
        Publish("R-7001", "surface-isolation-hud-failure");
    }
    finally
    {
        RuntimeOrderHighlightService.ThrowOnUpdate = false;
    }
    AssertEqual(warningCount + 1, ManualLogSource.WarningCount, "A HUD target failure was not diagnosed once.");
    AssertTrue(RuntimeThrowDeliverOrderHighlightService.LastEnabled, "A HUD target failure prevented the throw-delivery surface from updating.");
    AssertEqual("R-7001", RuntimeThrowDeliverOrderHighlightService.LastOrderTraceId, "A HUD target failure changed the throw-delivery trace.");
    AssertEqual(2, RuntimeThrowDeliverOrderHighlightService.LastDeskCode, "A HUD target failure changed the throw-delivery desk.");
    AssertContains(RuntimeUiPinningService.Status, "orderSurfaces=hud:retry-pending,throwDelivery:synchronized",
        "A failed HUD update was not retained as a narrow retry-pending surface.");
    var throwDeliveryUpdatesBeforeHudRetry = RuntimeThrowDeliverOrderHighlightService.UpdateCount;
    Publish("R-7001", "surface-isolation-hud-failure");
    AssertEqual("R-7001", RuntimeOrderHighlightService.LastOrderTraceId,
        "Retrying the same target did not recover the pending HUD surface.");
    AssertEqual(throwDeliveryUpdatesBeforeHudRetry, RuntimeThrowDeliverOrderHighlightService.UpdateCount,
        "Retrying the failed HUD surface redundantly republished the synchronized throw-delivery surface.");

    warningCount = ManualLogSource.WarningCount;
    RuntimeThrowDeliverOrderHighlightService.ThrowOnUpdate = true;
    try
    {
        Publish("R-7002", "surface-isolation-throw-delivery-failure");
    }
    finally
    {
        RuntimeThrowDeliverOrderHighlightService.ThrowOnUpdate = false;
    }
    AssertEqual(warningCount + 1, ManualLogSource.WarningCount, "A throw-delivery target failure was not diagnosed once.");
    AssertTrue(RuntimeOrderHighlightService.LastEnabled, "A throw-delivery target failure prevented the HUD surface from updating.");
    AssertEqual("R-7002", RuntimeOrderHighlightService.LastOrderTraceId, "A throw-delivery target failure changed the HUD trace.");
    AssertEqual(2, RuntimeOrderHighlightService.LastDeskCode, "A throw-delivery target failure changed the HUD desk.");
    AssertContains(RuntimeUiPinningService.Status, "orderSurfaces=hud:synchronized,throwDelivery:retry-pending",
        "A failed throw-delivery update was not retained as a narrow retry-pending surface.");

    var hudUpdatesBeforeThrowDeliveryRetry = RuntimeOrderHighlightService.UpdateCount;
    Publish("R-7002", "surface-isolation-throw-delivery-failure");
    AssertEqual(hudUpdatesBeforeThrowDeliveryRetry, RuntimeOrderHighlightService.UpdateCount,
        "Retrying the failed throw-delivery surface redundantly republished the synchronized HUD surface.");
    AssertEqual(RuntimeOrderHighlightService.LastEnabled, RuntimeThrowDeliverOrderHighlightService.LastEnabled, "Recovered order-highlight surfaces disagree on the shared switch.");
    AssertEqual(RuntimeOrderHighlightService.LastOrderTraceId, RuntimeThrowDeliverOrderHighlightService.LastOrderTraceId, "Recovered order-highlight surfaces disagree on the shared trace.");
    AssertEqual(RuntimeOrderHighlightService.LastDeskCode, RuntimeThrowDeliverOrderHighlightService.LastDeskCode, "Recovered order-highlight surfaces disagree on the shared desk.");
}

static void VerifyOrderHighlightRuntimeWiring()
{
    var pluginSource = File.ReadAllText("mods/bepinex/src/Plugin/MystiaStewardCompanionPlugin.cs");
    var controllerSource = File.ReadAllText("mods/bepinex/src/Ui/StewardOverlayController.cs");
    var pinningSource = File.ReadAllText("mods/bepinex/src/Save/RuntimeUiPinningService.cs");

    AssertContains(
        pluginSource,
        "RuntimeThrowDeliverOrderHighlightService.Attach(Log);",
        "Plugin startup did not attach the independent throw-delivery order-highlight surface.");
    AssertContains(
        controllerSource,
        "RuntimeThrowDeliverOrderHighlightService.Tick();",
        "Controller LateUpdate did not tick the throw-delivery order-highlight surface.");
    AssertContains(
        controllerSource,
        "() => RuntimeThrowDeliverOrderHighlightService.Dispose(\"controller disposed\")",
        "Controller disposal did not release the throw-delivery order-highlight surface.");
    AssertContains(
        controllerSource,
        "RunOrderHighlightDisposalNoThrow",
        "HUD and throw-delivery disposal are not isolated from one another.");
    AssertContains(
        pinningSource,
        "RuntimeTargetRecipeVariantService.Attach(log);",
        "Runtime UI pinning did not attach the formal recipe-variant service before list highlighting.");
    var panelRefreshStart = pinningSource.IndexOf(
        "private static void TryRefreshOpenPanel",
        StringComparison.Ordinal);
    var panelRefreshEnd = panelRefreshStart < 0
        ? -1
        : pinningSource.IndexOf(
            "private static bool IsCurrentOpenPanel",
            panelRefreshStart,
            StringComparison.Ordinal);
    AssertTrue(panelRefreshStart >= 0 && panelRefreshEnd > panelRefreshStart,
        "Open-panel refresh implementation is missing.");
    var panelRefreshSource = pinningSource[panelRefreshStart..panelRefreshEnd];
    AssertContains(
        panelRefreshSource,
        "TryAcquireTargetPublicationLease(target, out exactTargetLease)",
        "Open-panel refresh does not acquire the exact target-snapshot publication lease.");
    AssertContains(
        panelRefreshSource,
        "attempt.SurfaceRefresh.Refresh(attempt.Instance)",
        "Open-panel refresh does not execute the exact backing-data and logical-group binding.");
    AssertContains(
        controllerSource,
        "RuntimeTargetRecipeVariantService.RetireFailClosed(\"controller disposed\");",
        "Controller disposal still references the removed diagnostic recipe-variant service.");
    AssertTrue(
        pluginSource.IndexOf("RuntimeUiPinningService.Attach(Log);", StringComparison.Ordinal)
        < pluginSource.IndexOf("RuntimePinnedListHighlightService.Attach(Log);", StringComparison.Ordinal),
        "Plugin startup did not attach UI pinning and its variant bindings before list highlighting.");
}

static void VerifyPatchTargets()
{
    RuntimeUiPinningService.Attach(new ManualLogSource());
    RuntimePinnedListHighlightService.Attach(new ManualLogSource());

    var probeTypes = new HashSet<Type>
    {
        typeof(CookingSelectionPanelProbe),
        typeof(RunTimePlayerDataProbe),
        typeof(StoragePanelProbe),
    };
    var patchedProbeCount = Harmony.GetAllPatchedMethods().Count(method => method.DeclaringType != null && probeTypes.Contains(method.DeclaringType));
    AssertEqual(13, patchedProbeCount, "Runtime UI pinning and list highlighting should install exactly thirteen patches.");
    AssertPatch(
        typeof(CookingSelectionPanelProbe),
        nameof(CookingSelectionPanelProbe.UpdateAllVisual),
        prefix: "OnCookingRefreshStarted",
        postfix: null,
        finalizer: "OnCookingRefreshFinalized");
    AssertPatch(
        typeof(CookingSelectionPanelProbe),
        nameof(CookingSelectionPanelProbe.UpdateRecipeField),
        prefix: "OnCookingRefreshStarted",
        postfix: null,
        finalizer: "OnCookingRefreshFinalized");
    var cookingSurfaceRefresh = (RuntimeUiListSurfaceRefreshBinding?)typeof(RuntimeUiPinningService)
        .GetField("_cookingSurfaceRefresh", BindingFlags.NonPublic | BindingFlags.Static)!
        .GetValue(null);
    var expectedIngredientRefreshMethod = typeof(CookingSelectionPanelProbe).GetMethod(
        nameof(CookingSelectionPanelProbe.UpdateIngField),
        BindingFlags.Public | BindingFlags.Instance,
        binder: null,
        types: Type.EmptyTypes,
        modifiers: null);
    var expectedRecipeRefreshMethod = typeof(CookingSelectionPanelProbe).GetMethod(
        nameof(CookingSelectionPanelProbe.UpdateRecipeField),
        BindingFlags.Public | BindingFlags.Instance,
        binder: null,
        types: Type.EmptyTypes,
        modifiers: null);
    AssertEqual(4, cookingSurfaceRefresh?.Steps.Count ?? -1,
        "Cooking surface did not bind exactly four refresh stages.");
    AssertEqual("ingredient-backing-data", cookingSurfaceRefresh?.Steps[0].Stage,
        "Cooking surface lost the ingredient backing-data stage identity.");
    AssertEqual(expectedIngredientRefreshMethod, cookingSurfaceRefresh?.Steps[0].RefreshMethod,
        "Cooking surface did not bind the exact zero-argument UpdateIngField MethodInfo.");
    AssertEqual<PropertyInfo?>(null, cookingSurfaceRefresh?.Steps[0].ReceiverProperty,
        "UpdateIngField was incorrectly bound through a logical-group receiver.");
    AssertEqual("recipe-backing-data", cookingSurfaceRefresh?.Steps[1].Stage,
        "Cooking surface lost the recipe backing-data stage identity.");
    AssertEqual(expectedRecipeRefreshMethod, cookingSurfaceRefresh?.Steps[1].RefreshMethod,
        "Cooking surface did not bind the exact zero-argument UpdateRecipeField MethodInfo.");
    AssertEqual("ingredient-visible-elements", cookingSurfaceRefresh?.Steps[2].Stage,
        "Cooking surface lost the ingredient visible-elements stage identity.");
    AssertEqual("m_StaticIngredientsGroup", cookingSurfaceRefresh?.Steps[2].ReceiverProperty?.Name,
        "Cooking surface did not bind the exact m_StaticIngredientsGroup property.");
    AssertEqual(nameof(CookingIngredientLogicalGroupProbe.UpdateElements), cookingSurfaceRefresh?.Steps[2].RefreshMethod.Name,
        "Cooking ingredient surface did not bind the exact UpdateElements/0 method.");
    AssertEqual("recipe-visible-elements", cookingSurfaceRefresh?.Steps[3].Stage,
        "Cooking surface lost the recipe visible-elements stage identity.");
    AssertEqual("m_StaticRecipeGroup", cookingSurfaceRefresh?.Steps[3].ReceiverProperty?.Name,
        "Cooking surface did not bind the exact m_StaticRecipeGroup property.");
    AssertEqual(nameof(CookingRecipeLogicalGroupProbe.UpdateElements), cookingSurfaceRefresh?.Steps[3].RefreshMethod.Name,
        "Cooking recipe surface did not bind the exact UpdateElements/0 method.");
    AssertFalse(cookingSurfaceRefresh?.Steps.Any(step => step.RefreshMethod.IsGenericMethod) == true,
        "Target publication cached a generic cooking refresh method.");
    AssertPatch(
        typeof(RunTimePlayerDataProbe),
        nameof(RunTimePlayerDataProbe.CheckPinned),
        prefix: "OnCheckPinned",
        postfix: null,
        finalizer: null);
    AssertPatch(
        typeof(StoragePanelProbe),
        nameof(StoragePanelProbe.UpdateBevField),
        prefix: "OnBeverageRefreshStarted",
        postfix: null,
        finalizer: "OnBeverageRefreshFinalized");
    var storageSurfaceRefresh = (RuntimeUiListSurfaceRefreshBinding?)typeof(RuntimeUiPinningService)
        .GetField("_storageSurfaceRefresh", BindingFlags.NonPublic | BindingFlags.Static)!
        .GetValue(null);
    AssertEqual(2, storageSurfaceRefresh?.Steps.Count ?? -1,
        "Storage surface did not bind exactly two refresh stages.");
    AssertEqual("beverage-backing-data", storageSurfaceRefresh?.Steps[0].Stage,
        "Storage surface lost the beverage backing-data stage identity.");
    AssertEqual(nameof(StoragePanelProbe.UpdateBevField), storageSurfaceRefresh?.Steps[0].RefreshMethod.Name,
        "Storage surface did not bind the exact UpdateBevField/0 method.");
    AssertEqual("beverage-visible-elements", storageSurfaceRefresh?.Steps[1].Stage,
        "Storage surface lost the beverage visible-elements stage identity.");
    AssertEqual("m_BevsGroup", storageSurfaceRefresh?.Steps[1].ReceiverProperty?.Name,
        "Storage surface did not bind the exact m_BevsGroup property.");
    AssertEqual("openType", storageSurfaceRefresh?.OpenTypeProperty?.Name,
        "Storage surface did not bind the exact openType property.");
    AssertEqual(nameof(BeverageLogicalGroupProbe.UpdateElements), storageSurfaceRefresh?.Steps[1].RefreshMethod.Name,
        "Storage surface did not bind the exact UpdateElements/0 method.");
    AssertPatch(
        typeof(CookingSelectionPanelProbe),
        nameof(CookingSelectionPanelProbe.OnPanelOpen),
        prefix: "BeforeCookingPanelOpen",
        postfix: "AfterCookingPanelOpen",
        finalizer: null);
    AssertPatch(
        typeof(CookingSelectionPanelProbe),
        nameof(CookingSelectionPanelProbe.OnPanelClose),
        prefix: "BeforeCookingPanelTeardown",
        postfix: null,
        finalizer: null);
    AssertPatch(
        typeof(CookingSelectionPanelProbe),
        nameof(CookingSelectionPanelProbe.OnPanelDestroyed),
        prefix: "BeforeCookingPanelTeardown",
        postfix: null,
        finalizer: null);
    AssertPatch(
        typeof(StoragePanelProbe),
        nameof(StoragePanelProbe.OnPanelOpen),
        prefix: "BeforeStoragePanelOpen",
        postfix: "AfterStoragePanelOpen",
        finalizer: null);
    AssertPatch(
        typeof(StoragePanelProbe),
        nameof(StoragePanelProbe.OnPanelClose),
        prefix: "BeforeStoragePanelTeardown",
        postfix: null,
        finalizer: null);
    AssertPatch(
        typeof(StoragePanelProbe),
        nameof(StoragePanelProbe.OnPanelDestroyed),
        prefix: "BeforeStoragePanelTeardown",
        postfix: null,
        finalizer: null);
    AssertPatch(
        typeof(CookingSelectionPanelProbe),
        nameof(CookingSelectionPanelProbe.OnPanelOpen),
        prefix: "BeforePanelOpen",
        postfix: null,
        finalizer: null,
        patchOwner: typeof(RuntimePinnedListHighlightService),
        prefixPriority: Priority.First);
    AssertPatch(
        typeof(StoragePanelProbe),
        nameof(StoragePanelProbe.OnPanelOpen),
        prefix: "BeforePanelOpen",
        postfix: null,
        finalizer: null,
        patchOwner: typeof(RuntimePinnedListHighlightService),
        prefixPriority: Priority.First);
    AssertPatch(
        typeof(CookingSelectionPanelProbe),
        nameof(CookingSelectionPanelProbe.OnRecipeElementEnabled),
        prefix: "BeforeItemEnabled",
        postfix: "AfterRecipeItemEnabled",
        finalizer: null,
        patchOwner: typeof(RuntimePinnedListHighlightService),
        prefixPriority: Priority.First,
        postfixPriority: Priority.Last,
        postfixAfter: "com.tyukki.mystia-steward-companion.runtime-target-recipe-variant");
    AssertPatch(
        typeof(CookingSelectionPanelProbe),
        nameof(CookingSelectionPanelProbe.OnIngElementEnabled),
        prefix: "BeforeItemEnabled",
        postfix: "AfterIngredientItemEnabled",
        finalizer: null,
        patchOwner: typeof(RuntimePinnedListHighlightService),
        prefixPriority: Priority.First,
        postfixPriority: Priority.Last);
    AssertPatch(
        typeof(StoragePanelProbe),
        nameof(StoragePanelProbe.OnElementEnabled),
        prefix: "BeforeItemEnabled",
        postfix: "AfterStorageItemEnabled",
        finalizer: null,
        patchOwner: typeof(RuntimePinnedListHighlightService),
        prefixPriority: Priority.First,
        postfixPriority: Priority.Last);
    AssertPatch(
        typeof(CookingSelectionPanelProbe),
        nameof(CookingSelectionPanelProbe.OnPanelClose),
        prefix: "BeforeCookingPanelTeardown",
        postfix: null,
        finalizer: null,
        patchOwner: typeof(RuntimePinnedListHighlightService),
        prefixPriority: Priority.First);
    AssertPatch(
        typeof(CookingSelectionPanelProbe),
        nameof(CookingSelectionPanelProbe.OnPanelDestroyed),
        prefix: "BeforeCookingPanelTeardown",
        postfix: null,
        finalizer: null,
        patchOwner: typeof(RuntimePinnedListHighlightService),
        prefixPriority: Priority.First);
    AssertPatch(
        typeof(StoragePanelProbe),
        nameof(StoragePanelProbe.OnPanelClose),
        prefix: "BeforeStoragePanelTeardown",
        postfix: null,
        finalizer: null,
        patchOwner: typeof(RuntimePinnedListHighlightService),
        prefixPriority: Priority.First);
    AssertPatch(
        typeof(StoragePanelProbe),
        nameof(StoragePanelProbe.OnPanelDestroyed),
        prefix: "BeforeStoragePanelTeardown",
        postfix: null,
        finalizer: null,
        patchOwner: typeof(RuntimePinnedListHighlightService),
        prefixPriority: Priority.First);
    AssertContains(RuntimeUiPinningService.Status, "checkPinnedPrefix:patched", "CheckPinned prefix patch status is missing.");
    AssertContains(RuntimeUiPinningService.Status, "cookingScope:patched", "Cooking scope patch status is missing.");
    AssertContains(RuntimeUiPinningService.Status, "beverageScope:patched", "Beverage scope patch status is missing.");
    AssertContains(RuntimeUiPinningService.Status, "cookingPanel:patched", "Cooking panel lifecycle patch status is missing.");
    AssertContains(RuntimeUiPinningService.Status, "storagePanel:patched", "Storage panel lifecycle patch status is missing.");
    AssertContains(RuntimePinnedListHighlightService.Status, "hooks=patched", "Pinned-list highlight patch status is missing.");
    AssertContains(RuntimeUiPinningService.Status, "listHighlight=hooks=patched", "Pinned-list highlight diagnostics are missing from pinning status.");
    AssertContains(RuntimeUiPinningService.Status, "throwDeliveryOrder=", "Throw-delivery order-highlight diagnostics are missing from pinning status.");
}

static void VerifyScopedNativePinnedMatching()
{
    PublishRareTarget(
        RuntimeNightBusinessLifecycle.Generation,
        listPinningEnabled: true,
        recipeVariantEnabled: false,
        cookerHighlightEnabled: false,
        seatHighlightEnabled: false,
        orderHighlightEnabled: false,
        recipeId: 34,
        beverageId: 16,
        ingredientIds: new[] { 11, 29 },
        extraIngredientIds: Array.Empty<int>(),
        cookerTypeId: 3,
        deskCode: -1,
        orderTraceId: "",
        targetRevision: "test-target");

    AssertTrue(InvokeCheckPinnedPrefix(1, 34).RunOriginal, "CheckPinned was skipped outside a panel refresh scope.");
    AssertFalse(InvokeCheckPinned(1, 34), "CheckPinned was changed outside a panel refresh scope.");
    AssertTrue(InvokeCheckPinned(1, 99, originalResult: true), "The prefix changed an unrelated native pinned result.");

    InvokePrivate("OnCookingRefreshStarted", new CookingSelectionPanelProbe());
    try
    {
        var recipeCall = InvokeCheckPinnedPrefix(1, 34);
        AssertFalse(recipeCall.RunOriginal, "Recipe target did not skip the native CheckPinned method.");
        AssertTrue(recipeCall.Result, "Recipe.Id target was not forced true in cooking scope.");
        AssertTrue(InvokeCheckPinnedPrefix(1, 40).RunOriginal, "A non-target recipe skipped the native CheckPinned method.");
        AssertFalse(InvokeCheckPinned(1, 40), "Food ID incorrectly matched the Recipe.Id target.");
        AssertTrue(InvokeCheckPinned(0, 11), "Seafood ingredient target was not pinned.");
        AssertTrue(InvokeCheckPinned(4, 11), "Meat ingredient target was not pinned.");
        AssertTrue(InvokeCheckPinned(5, 29), "Vegetable ingredient target was not pinned.");
        AssertTrue(InvokeCheckPinned(6, 29), "Other ingredient target was not pinned.");
        AssertTrue(InvokeCheckPinnedPrefix(0, 8).RunOriginal, "A non-target ingredient skipped the native CheckPinned method.");
        AssertFalse(InvokeCheckPinned(0, 8), "Unrelated ingredient was pinned.");
        AssertTrue(InvokeCheckPinned(0, 8, originalResult: true), "A native ingredient favorite was not preserved.");
        AssertTrue(InvokeCheckPinnedPrefix(2, 16).RunOriginal, "The beverage target skipped CheckPinned inside cooking scope.");
        AssertFalse(InvokeCheckPinned(2, 16), "Beverage target leaked into cooking scope.");
        AssertTrue(InvokeCheckPinnedPrefix(3, 3).RunOriginal, "Cooker matching skipped the native CheckPinned method.");
        AssertFalse(InvokeCheckPinned(3, 3), "Cooker pinning was reintroduced alongside visual highlighting.");
        AssertTrue(InvokeCheckPinnedPrefix(1, -1).RunOriginal, "A negative ID skipped the native CheckPinned method.");
    }
    finally
    {
        InvokePrivate("OnCookingRefreshFinalized", new object?[] { null });
    }

    InvokePrivate("OnBeverageRefreshStarted", new StoragePanelProbe());
    try
    {
        AssertTrue(InvokeCheckPinned(2, 16), "Beverage target was not pinned in beverage scope.");
        AssertTrue(InvokeCheckPinnedPrefix(1, 34).RunOriginal, "The recipe target skipped CheckPinned inside beverage scope.");
        AssertFalse(InvokeCheckPinned(1, 34), "Recipe target leaked into beverage scope.");
        AssertTrue(InvokeCheckPinnedPrefix(2, 15).RunOriginal, "A non-target beverage skipped the native CheckPinned method.");
        AssertFalse(InvokeCheckPinned(2, 15), "Unrelated beverage was pinned.");
        AssertTrue(InvokeCheckPinned(2, 15, originalResult: true), "A native beverage favorite was not preserved.");
    }
    finally
    {
        InvokePrivate("OnBeverageRefreshFinalized", new object?[] { null });
    }

    AssertFalse(InvokeCheckPinned(1, 34), "Cooking scope leaked after its finalizer.");
    AssertFalse(InvokeCheckPinned(2, 16), "Beverage scope leaked after its finalizer.");
    AssertContains(RuntimeUiPinningService.Status, "forcedTotal=recipe:3, ingredients:12, beverage:3", "Scoped prefix diagnostics are incorrect.");
}

static void VerifyNestedScopeFinalizers()
{
    PublishRareTarget(
        RuntimeNightBusinessLifecycle.Generation,
        listPinningEnabled: true,
        recipeVariantEnabled: false,
        cookerHighlightEnabled: false,
        seatHighlightEnabled: false,
        orderHighlightEnabled: false,
        recipeId: 35,
        beverageId: 17,
        ingredientIds: new[] { 12 },
        extraIngredientIds: Array.Empty<int>(),
        cookerTypeId: 3,
        deskCode: -1,
        orderTraceId: "",
        targetRevision: "test-target");
    var expectedException = new InvalidOperationException("expected original failure");
    var nestedPanel = new CookingSelectionPanelProbe();
    InvokePrivate("OnCookingRefreshStarted", nestedPanel);
    InvokePrivate("OnCookingRefreshStarted", nestedPanel);
    var firstResult = InvokePrivate("OnCookingRefreshFinalized", expectedException);
    AssertEqual(expectedException, firstResult, "Cooking finalizer did not preserve the original exception.");
    AssertTrue(InvokeCheckPinned(1, 35), "Nested cooking scope ended too early.");
    var secondResult = InvokePrivate("OnCookingRefreshFinalized", expectedException);
    AssertEqual(expectedException, secondResult, "Nested cooking finalizer did not preserve the original exception.");
    AssertFalse(InvokeCheckPinned(1, 35), "Nested cooking scope leaked after both finalizers.");
    AssertContains(RuntimeUiPinningService.Status, "scopeImbalance=0", "Scope cleanup reported an unexpected imbalance.");
}

static void VerifyTargetUpdatePreservesForceTotals()
{
    PublishRareTarget(
        RuntimeNightBusinessLifecycle.Generation,
        listPinningEnabled: true,
        recipeVariantEnabled: false,
        cookerHighlightEnabled: false,
        seatHighlightEnabled: false,
        orderHighlightEnabled: false,
        recipeId: 34,
        beverageId: 16,
        ingredientIds: new[] { 11 },
        extraIngredientIds: Array.Empty<int>(),
        cookerTypeId: 3,
        deskCode: -1,
        orderTraceId: "",
        targetRevision: "test-target");
    InvokePrivate("OnCookingRefreshStarted", new CookingSelectionPanelProbe());
    try
    {
        AssertTrue(InvokeCheckPinned(1, 34), "Counter precondition did not match the recipe target.");
    }
    finally
    {
        InvokePrivate("OnCookingRefreshFinalized", new object?[] { null });
    }

    var forcesBeforeUpdate = ReadForcedTotal("recipe");
    PublishRareTarget(
        RuntimeNightBusinessLifecycle.Generation,
        listPinningEnabled: true,
        recipeVariantEnabled: false,
        cookerHighlightEnabled: false,
        seatHighlightEnabled: false,
        orderHighlightEnabled: false,
        recipeId: 35,
        beverageId: 17,
        ingredientIds: new[] { 12 },
        extraIngredientIds: Array.Empty<int>(),
        cookerTypeId: 3,
        deskCode: -1,
        orderTraceId: "",
        targetRevision: "test-target");
    AssertEqual(forcesBeforeUpdate, ReadForcedTotal("recipe"), "Target update reset the process force total.");
}

static void VerifyThreadLocalScopeIsolation()
{
    PublishRareTarget(
        RuntimeNightBusinessLifecycle.Generation,
        listPinningEnabled: true,
        recipeVariantEnabled: false,
        cookerHighlightEnabled: false,
        seatHighlightEnabled: false,
        orderHighlightEnabled: false,
        recipeId: 34,
        beverageId: 16,
        ingredientIds: new[] { 11 },
        extraIngredientIds: Array.Empty<int>(),
        cookerTypeId: 3,
        deskCode: -1,
        orderTraceId: "",
        targetRevision: "test-target");

    using var scopeEntered = new ManualResetEventSlim();
    using var releaseScope = new ManualResetEventSlim();
    Exception? workerException = null;
    var workerMatched = false;
    var worker = new Thread(() =>
    {
        try
        {
            InvokePrivate("OnCookingRefreshStarted", new CookingSelectionPanelProbe());
            scopeEntered.Set();
            releaseScope.Wait();
            workerMatched = InvokeCheckPinned(1, 34);
        }
        catch (Exception ex)
        {
            workerException = ex;
        }
        finally
        {
            InvokePrivate("OnCookingRefreshFinalized", new object?[] { null });
        }
    });

    worker.Start();
    try
    {
        AssertTrue(scopeEntered.Wait(TimeSpan.FromSeconds(5)), "Worker cooking scope did not start in time.");
        AssertFalse(InvokeCheckPinned(1, 34), "A cooking scope leaked from the worker thread into the caller thread.");
    }
    finally
    {
        releaseScope.Set();
    }

    AssertTrue(worker.Join(TimeSpan.FromSeconds(5)), "Worker cooking scope did not finish in time.");
    if (workerException is not null)
    {
        throw new InvalidOperationException("Worker cooking scope failed.", workerException);
    }

    AssertTrue(workerMatched, "Worker cooking scope did not match its recipe target.");
}

static void VerifyScopePinsOneTargetSnapshot()
{
    PublishRareTarget(
        RuntimeNightBusinessLifecycle.Generation,
        listPinningEnabled: true,
        recipeVariantEnabled: false,
        cookerHighlightEnabled: false,
        seatHighlightEnabled: false,
        orderHighlightEnabled: false,
        recipeId: 34,
        beverageId: 16,
        ingredientIds: new[] { 11 },
        extraIngredientIds: Array.Empty<int>(),
        cookerTypeId: 3,
        deskCode: -1,
        orderTraceId: "",
        targetRevision: "test-target");
    InvokePrivate("OnCookingRefreshStarted", new CookingSelectionPanelProbe());
    try
    {
        PublishRareTarget(
            RuntimeNightBusinessLifecycle.Generation,
            listPinningEnabled: true,
            recipeVariantEnabled: false,
            cookerHighlightEnabled: false,
            seatHighlightEnabled: false,
            orderHighlightEnabled: false,
            recipeId: 35,
            beverageId: 17,
            ingredientIds: new[] { 12 },
            extraIngredientIds: Array.Empty<int>(),
            cookerTypeId: 3,
            deskCode: -1,
            orderTraceId: "",
            targetRevision: "test-target");
        AssertTrue(InvokeCheckPinned(1, 34), "An active cooking refresh did not retain its initial target snapshot.");
        AssertFalse(InvokeCheckPinned(1, 35), "An active cooking refresh mixed in a newly published target.");
    }
    finally
    {
        InvokePrivate("OnCookingRefreshFinalized", new object?[] { null });
    }

    InvokePrivate("OnCookingRefreshStarted", new CookingSelectionPanelProbe());
    try
    {
        AssertTrue(InvokeCheckPinned(1, 35), "A new cooking refresh did not capture the latest target.");
    }
    finally
    {
        InvokePrivate("OnCookingRefreshFinalized", new object?[] { null });
    }

}

static void VerifyPinningAndHighlightRemainIndependent()
{
    PublishRareTarget(
        RuntimeNightBusinessLifecycle.Generation,
        listPinningEnabled: false,
        recipeVariantEnabled: false,
        cookerHighlightEnabled: true,
        seatHighlightEnabled: false,
        orderHighlightEnabled: false,
        recipeId: 34,
        beverageId: 16,
        ingredientIds: new[] { 11 },
        extraIngredientIds: Array.Empty<int>(),
        cookerTypeId: 3,
        deskCode: -1,
        orderTraceId: "",
        targetRevision: "test-target");
    AssertTrue(RuntimeCookerHighlightService.LastEnabled, "Highlight-only target did not enable cooker highlighting.");
    InvokePrivate("OnCookingRefreshStarted", new CookingSelectionPanelProbe());
    try
    {
        AssertTrue(InvokeCheckPinnedPrefix(1, 34).RunOriginal, "Highlight-only mode skipped the native CheckPinned method.");
        AssertFalse(InvokeCheckPinned(1, 34), "Highlight-only target unexpectedly enabled list pinning.");
    }
    finally
    {
        InvokePrivate("OnCookingRefreshFinalized", new object?[] { null });
    }

    AssertThrows<ArgumentException>(
        () => PublishRareTarget(
            RuntimeNightBusinessLifecycle.Generation,
            listPinningEnabled: true,
            recipeVariantEnabled: false,
            cookerHighlightEnabled: false,
            seatHighlightEnabled: false,
            orderHighlightEnabled: true,
            recipeId: 34,
            beverageId: 16,
            ingredientIds: new[] { 11 },
            extraIngredientIds: Array.Empty<int>(),
            cookerTypeId: 3,
            deskCode: 0,
            orderTraceId: "invalid-trace",
            targetRevision: "test-invalid-order-trace"),
        "Invalid exact order identity did not reject the whole atomic target publication.");
    AssertFalse(RuntimeOrderHighlightService.LastEnabled, "Invalid order trace enabled order highlighting.");
    AssertEqual("", RuntimeOrderHighlightService.LastOrderTraceId, "Invalid order trace reached the order highlighter.");
    AssertFalse(RuntimeThrowDeliverOrderHighlightService.LastEnabled, "Invalid order trace enabled throw-delivery order highlighting.");
    AssertEqual("", RuntimeThrowDeliverOrderHighlightService.LastOrderTraceId, "Invalid order trace reached the throw-delivery order highlighter.");

    PublishRareTarget(
        RuntimeNightBusinessLifecycle.Generation,
        listPinningEnabled: true,
        recipeVariantEnabled: false,
        cookerHighlightEnabled: false,
        seatHighlightEnabled: false,
        orderHighlightEnabled: false,
        recipeId: 34,
        beverageId: 16,
        ingredientIds: new[] { 11 },
        extraIngredientIds: Array.Empty<int>(),
        cookerTypeId: 3,
        deskCode: -1,
        orderTraceId: "",
        targetRevision: "test-target");
    AssertFalse(RuntimeCookerHighlightService.LastEnabled, "Pinning-only target unexpectedly enabled cooker highlighting.");
    InvokePrivate("OnCookingRefreshStarted", new CookingSelectionPanelProbe());
    try
    {
        AssertTrue(InvokeCheckPinned(1, 34), "Pinning-only target did not enable native pinned matching.");
    }
    finally
    {
        InvokePrivate("OnCookingRefreshFinalized", new object?[] { null });
    }

    PublishRareTarget(
        RuntimeNightBusinessLifecycle.Generation,
        listPinningEnabled: false,
        recipeVariantEnabled: false,
        cookerHighlightEnabled: false,
        seatHighlightEnabled: true,
        orderHighlightEnabled: false,
        recipeId: 34,
        beverageId: 16,
        ingredientIds: new[] { 11 },
        extraIngredientIds: new[] { 12 },
        cookerTypeId: 3,
        deskCode: 0,
        orderTraceId: "",
        targetRevision: "test-target");
    AssertFalse(RuntimeCookerHighlightService.LastEnabled, "Seat-only target unexpectedly enabled cooker highlighting.");
    AssertTrue(RuntimeSeatHighlightService.LastEnabled, "Seat-only target did not enable seat highlighting.");
    AssertEqual(0, RuntimeSeatHighlightService.LastDeskCode, "Seat-only target changed the zero-based desk code.");
    var seatOnlyPinningTarget = RuntimeUiPinningService.ReadTargetSet();
    AssertTrue(seatOnlyPinningTarget.TryGetTarget(RuntimeUiTargetKind.Rare, out var seatOnlyTarget), "Seat-only publication lost its rare target.");
    AssertFalse(seatOnlyTarget.ListPinningEnabled, "Seat-only target unexpectedly enabled list pinning.");
    AssertFalse(seatOnlyTarget.RecipeVariantEnabled, "Seat-only target unexpectedly enabled recipe variants.");
    InvokePrivate("OnCookingRefreshStarted", new CookingSelectionPanelProbe());
    try
    {
        AssertFalse(InvokeCheckPinned(1, 34), "Seat-only target changed native recipe pinning.");
    }
    finally
    {
        InvokePrivate("OnCookingRefreshFinalized", new object?[] { null });
    }

    PublishRareTarget(
        RuntimeNightBusinessLifecycle.Generation,
        listPinningEnabled: false,
        recipeVariantEnabled: false,
        cookerHighlightEnabled: false,
        seatHighlightEnabled: false,
        orderHighlightEnabled: true,
        recipeId: 34,
        beverageId: 16,
        ingredientIds: new[] { 11 },
        extraIngredientIds: Array.Empty<int>(),
        cookerTypeId: 3,
        deskCode: 0,
        orderTraceId: "R-0001",
        targetRevision: "test-order-target");
    AssertFalse(RuntimeCookerHighlightService.LastEnabled, "Order-only target unexpectedly enabled cooker highlighting.");
    AssertFalse(RuntimeSeatHighlightService.LastEnabled, "Order-only target unexpectedly enabled seat highlighting.");
    AssertTrue(RuntimeOrderHighlightService.LastEnabled, "Order-only target did not enable order highlighting.");
    AssertEqual("R-0001", RuntimeOrderHighlightService.LastOrderTraceId, "Order-only target changed the order trace id.");
    AssertEqual(0, RuntimeOrderHighlightService.LastDeskCode, "Order-only target changed the zero-based desk code.");
    AssertTrue(RuntimeThrowDeliverOrderHighlightService.LastEnabled, "Order-only target did not enable throw-delivery order highlighting.");
    AssertEqual("R-0001", RuntimeThrowDeliverOrderHighlightService.LastOrderTraceId, "Order-only target changed the throw-delivery order trace id.");
    AssertEqual(0, RuntimeThrowDeliverOrderHighlightService.LastDeskCode, "Order-only target changed the throw-delivery zero-based desk code.");
    AssertEqual(RuntimeOrderHighlightService.LastSessionGeneration, RuntimeThrowDeliverOrderHighlightService.LastSessionGeneration, "Order-highlight surfaces received different business generations.");
    var orderOnlyPinningTarget = RuntimeUiPinningService.ReadTargetSet();
    AssertTrue(orderOnlyPinningTarget.TryGetTarget(RuntimeUiTargetKind.Rare, out var orderOnlyTarget), "Order-only publication lost its rare target.");
    AssertFalse(orderOnlyTarget.ListPinningEnabled, "Order-only target unexpectedly enabled list pinning.");
    AssertFalse(orderOnlyTarget.RecipeVariantEnabled, "Order-only target unexpectedly enabled recipe variants.");
}

static void VerifyDangerousListHooksAreAbsent()
{
    foreach (var methodName in new[]
    {
        "OnRecipeFieldUpdated",
        "OnIngredientsClassified",
        "OnBeverageFieldUpdated",
        "ReorderList",
        "ReadMember",
    })
    {
        var method = typeof(RuntimeUiPinningService).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
        AssertEqual<MethodInfo?>(null, method, $"Dangerous direct list path remains: {methodName}.");
    }
}

static void VerifyManagedHarmonyReturnPropagation()
{
    PublishRareTarget(
        RuntimeNightBusinessLifecycle.Generation,
        listPinningEnabled: true,
        recipeVariantEnabled: false,
        cookerHighlightEnabled: false,
        seatHighlightEnabled: false,
        orderHighlightEnabled: false,
        recipeId: 34,
        beverageId: 16,
        ingredientIds: new[] { 11 },
        extraIngredientIds: Array.Empty<int>(),
        cookerTypeId: 3,
        deskCode: -1,
        orderTraceId: "",
        targetRevision: "test-target");

    try
    {
        RunTimePlayerDataProbe.Reset(nativeResult: false);
        AssertFalse(RunTimePlayerDataProbe.CheckPinned(PlayerSaveFileDefaultPropProbe.Recipes, 34), "The managed Harmony prefix leaked outside a refresh scope.");
        AssertEqual(1, RunTimePlayerDataProbe.NativeCallCount, "The native CheckPinned probe did not run outside a refresh scope.");

        CookingSelectionPanelProbe.RecipeRefreshAction = () => RunTimePlayerDataProbe.CheckPinned(PlayerSaveFileDefaultPropProbe.Recipes, 34);
        new CookingSelectionPanelProbe().UpdateAllVisual();
        AssertTrue(CookingSelectionPanelProbe.LastRecipeResult == true, "The managed Harmony wrapper did not propagate the forced recipe result.");
        AssertEqual(1, RunTimePlayerDataProbe.NativeCallCount, "The managed Harmony wrapper did not skip the target's original method.");
        AssertFalse(RunTimePlayerDataProbe.CheckPinned(PlayerSaveFileDefaultPropProbe.Recipes, 34), "Cooking scope leaked after a normal managed wrapper return.");
        AssertEqual(2, RunTimePlayerDataProbe.NativeCallCount, "The native CheckPinned probe did not resume after a normal wrapper return.");

        RunTimePlayerDataProbe.NativeResult = true;
        CookingSelectionPanelProbe.RecipeRefreshAction = () => RunTimePlayerDataProbe.CheckPinned(PlayerSaveFileDefaultPropProbe.Recipes, 99);
        new CookingSelectionPanelProbe().UpdateAllVisual();
        AssertTrue(CookingSelectionPanelProbe.LastRecipeResult == true, "The managed Harmony wrapper changed a native favorite result.");
        AssertEqual(3, RunTimePlayerDataProbe.NativeCallCount, "A non-target native favorite did not execute the original method.");

        RunTimePlayerDataProbe.Reset(nativeResult: false);
        var expectedException = new InvalidOperationException("managed wrapper failure");
        CookingSelectionPanelProbe.RecipeRefreshAction = () => throw expectedException;
        Exception? observedException = null;
        try
        {
            new CookingSelectionPanelProbe().UpdateAllVisual();
        }
        catch (Exception ex)
        {
            observedException = ex;
        }

        AssertEqual<Exception?>(expectedException, observedException, "The managed Harmony finalizer did not preserve the original exception.");
        AssertFalse(RunTimePlayerDataProbe.CheckPinned(PlayerSaveFileDefaultPropProbe.Recipes, 34), "Cooking scope leaked after an exceptional managed wrapper return.");
        AssertEqual(1, RunTimePlayerDataProbe.NativeCallCount, "The native CheckPinned probe did not resume after an exceptional wrapper return.");

        RunTimePlayerDataProbe.Reset(nativeResult: false);
        StoragePanelProbe.RefreshAction = () => RunTimePlayerDataProbe.CheckPinned(PlayerSaveFileDefaultPropProbe.Beverages, 16);
        new StoragePanelProbe().UpdateBevField();
        AssertTrue(StoragePanelProbe.LastResult == true, "The managed Harmony wrapper did not propagate the forced beverage result.");
        AssertEqual(0, RunTimePlayerDataProbe.NativeCallCount, "The managed Harmony wrapper did not skip the beverage target's original method.");
        AssertFalse(RunTimePlayerDataProbe.CheckPinned(PlayerSaveFileDefaultPropProbe.Beverages, 16), "Beverage scope leaked after a normal managed wrapper return.");
        AssertEqual(1, RunTimePlayerDataProbe.NativeCallCount, "The native CheckPinned probe did not resume after beverage wrapper return.");
    }
    finally
    {
        CookingSelectionPanelProbe.RecipeRefreshAction = null;
        StoragePanelProbe.RefreshAction = null;
    }
}

static void VerifyManagedPinnedListHighlighting()
{
    var mainThreadId = Environment.CurrentManagedThreadId;
    var cookingPanel = new CookingSelectionPanelProbe();
    var storagePanel = new StoragePanelProbe();
    var recipeBase = new Color(0.72f, 0.74f, 0.78f, 0.5f);
    var recipeRebound = new Color(0.24f, 0.46f, 0.68f, 0.4f);
    var ingredientBase = new Color(0.42f, 0.58f, 0.76f, 0.35f);
    var storageBase = new Color(0.36f, 0.52f, 0.7f, 0.6f);
    CookingSelectionPanelProbe.ApplyIngredientBoundColor = false;
    CookingSelectionPanelProbe.RecipeBoundColor = recipeBase;
    StoragePanelProbe.BoundColor = storageBase;
    Time.realtimeSinceStartup = 0.25f;

    PublishRareTarget(
        RuntimeNightBusinessLifecycle.Generation,
        listPinningEnabled: true,
        recipeVariantEnabled: false,
        cookerHighlightEnabled: false,
        seatHighlightEnabled: false,
        orderHighlightEnabled: false,
        recipeId: 34,
        beverageId: 16,
        ingredientIds: new[] { 11 },
        extraIngredientIds: Array.Empty<int>(),
        cookerTypeId: 3,
        deskCode: -1,
        orderTraceId: "",
        targetRevision: "test-target");

    try
    {
        var recipeButton = new UIButtonSimpleProbe(new Color(1f, 1f, 1f, 1f));
        cookingPanel.OnRecipeElementEnabled(new RecipeProbe(34), new object(), recipeButton);
        AssertColor(recipeBase, recipeButton.image.get_color(), "Recipe binding did not preserve the game's base color.");
        RuntimePinnedListHighlightService.Tick();
        AssertHighlighted(recipeBase, recipeButton.image.get_color(), "Recipe target was not highlighted.");

        CookingSelectionPanelProbe.RecipeBoundColor = recipeRebound;
        cookingPanel.OnRecipeElementEnabled(new RecipeProbe(40), new object(), recipeButton);
        AssertColor(recipeRebound, recipeButton.image.get_color(), "A pooled recipe button kept the previous target highlight.");
        RuntimePinnedListHighlightService.Tick();
        AssertColor(recipeRebound, recipeButton.image.get_color(), "A non-target recipe was highlighted.");

        var ingredientButton = new UIButtonSimpleProbe(ingredientBase);
        cookingPanel.OnIngElementEnabled(
            new KeyValuePair<IngredientProbe, int>(new IngredientProbe(11), 2),
            new object(),
            ingredientButton);
        RuntimePinnedListHighlightService.Tick();
        AssertHighlighted(ingredientBase, ingredientButton.image.get_color(), "Ingredient target was not highlighted.");
        cookingPanel.OnIngElementEnabled(
            new KeyValuePair<IngredientProbe, int>(new IngredientProbe(12), 2),
            new object(),
            ingredientButton);
        AssertColor(ingredientBase, ingredientButton.image.get_color(), "A pooled ingredient button kept the previous target highlight.");

        var foodButton = new UIButtonSimpleProbe(storageBase);
        storagePanel.OnElementEnabled(
            new KeyValuePair<SellableProbe, int>(new SellableProbe(16, SellableTypeProbe.Food), 1),
            new object(),
            foodButton);
        RuntimePinnedListHighlightService.Tick();
        AssertColor(storageBase, foodButton.image.get_color(), "A food item with the beverage target ID was highlighted.");

        var beverageButton = new UIButtonSimpleProbe(storageBase);
        storagePanel.OnElementEnabled(
            new KeyValuePair<SellableProbe, int>(new SellableProbe(16, SellableTypeProbe.Beverage), 1),
            new object(),
            beverageButton);
        RuntimePinnedListHighlightService.Tick();
        AssertHighlighted(storageBase, beverageButton.image.get_color(), "Beverage target was not highlighted.");
        storagePanel.OnPanelClose();
        AssertColor(storageBase, beverageButton.image.get_color(), "Storage panel close did not restore the beverage color.");

        CookingSelectionPanelProbe.RecipeBoundColor = recipeBase;
        cookingPanel.OnRecipeElementEnabled(new RecipeProbe(34), new object(), recipeButton);
        RuntimePinnedListHighlightService.Tick();
        var setterCountBeforeWorkerUpdate = recipeButton.image.SetterCount;
        RunOnWorkerThread(() => PublishRareTarget(
            RuntimeNightBusinessLifecycle.Generation,
            listPinningEnabled: true,
            recipeVariantEnabled: false,
            cookerHighlightEnabled: false,
            seatHighlightEnabled: false,
            orderHighlightEnabled: false,
            recipeId: 35,
            beverageId: 17,
            ingredientIds: new[] { 12 },
            extraIngredientIds: Array.Empty<int>(),
            cookerTypeId: 3,
            deskCode: -1,
            orderTraceId: "",
            targetRevision: "test-target"));
        AssertEqual(setterCountBeforeWorkerUpdate, recipeButton.image.SetterCount, "Background target publication wrote a Unity image color.");
        AssertHighlighted(recipeBase, recipeButton.image.get_color(), "Target publication touched Unity color off the main visual tick.");
        RuntimePinnedListHighlightService.Tick();
        AssertColor(recipeBase, recipeButton.image.get_color(), "Target change did not restore the old recipe color.");
        AssertTrue(recipeButton.image.SetterThreadIds.All(threadId => threadId == mainThreadId), "A recipe image setter ran outside the Unity main thread.");

        cookingPanel.OnRecipeElementEnabled(new RecipeProbe(35), new object(), recipeButton);
        RuntimePinnedListHighlightService.Tick();
        AssertHighlighted(recipeBase, recipeButton.image.get_color(), "New recipe target was not highlighted after rebinding.");
        cookingPanel.OnPanelDestroyed();
        AssertColor(recipeBase, recipeButton.image.get_color(), "Cooking panel destroy did not restore the recipe color.");

        cookingPanel.OnRecipeElementEnabled(new RecipeProbe(35), new object(), recipeButton);
        RuntimePinnedListHighlightService.Tick();
        RuntimePinnedListHighlightService.Suspend("test scene exit");
        AssertColor(recipeBase, recipeButton.image.get_color(), "Scene suspension did not restore the recipe color.");
        AssertContains(RuntimePinnedListHighlightService.Status, "state=suspended: test scene exit", "Scene suspension state was not retained.");

        PublishRareTarget(
            RuntimeNightBusinessLifecycle.Generation,
            listPinningEnabled: true,
            recipeVariantEnabled: false,
            cookerHighlightEnabled: false,
            seatHighlightEnabled: false,
            orderHighlightEnabled: false,
            recipeId: 35,
            beverageId: 17,
            ingredientIds: new[] { 12 },
            extraIngredientIds: Array.Empty<int>(),
            cookerTypeId: 3,
            deskCode: -1,
            orderTraceId: "",
            targetRevision: "test-target");
        AssertContains(RuntimePinnedListHighlightService.Status, "state=suspended: test scene exit", "Publishing the same target resumed a suspended scene.");
        cookingPanel.OnRecipeElementEnabled(new RecipeProbe(35), new object(), recipeButton);
        var setterCountAfterSuspendedBinding = recipeButton.image.SetterCount;
        RuntimePinnedListHighlightService.Tick();
        AssertEqual(setterCountAfterSuspendedBinding, recipeButton.image.SetterCount, "A late element callback was highlighted while the scene was suspended.");
        AssertColor(recipeBase, recipeButton.image.get_color(), "A late element callback kept a highlight while the scene was suspended.");
        AssertContains(RuntimePinnedListHighlightService.Status, "tracked=recipe:0", "A late element callback was tracked while the scene was suspended.");

        RunOnWorkerThread(() => PublishRareTarget(
            RuntimeNightBusinessLifecycle.Generation,
            listPinningEnabled: true,
            recipeVariantEnabled: false,
            cookerHighlightEnabled: false,
            seatHighlightEnabled: false,
            orderHighlightEnabled: false,
            recipeId: 36,
            beverageId: 18,
            ingredientIds: new[] { 13 },
            extraIngredientIds: Array.Empty<int>(),
            cookerTypeId: 3,
            deskCode: -1,
            orderTraceId: "",
            targetRevision: "test-target"));
        AssertContains(RuntimePinnedListHighlightService.Status, "state=suspended: test scene exit", "Publishing a changed target resumed a suspended scene.");
        CookingSelectionPanelProbe.OpenAction = () => cookingPanel.OnRecipeElementEnabled(new RecipeProbe(36), new object(), recipeButton);
        cookingPanel.OnPanelOpen();
        RuntimePinnedListHighlightService.Tick();
        AssertHighlighted(recipeBase, recipeButton.image.get_color(), "Cooking panel open did not resume highlighting before its first element binding.");
        CookingSelectionPanelProbe.OpenAction = null;

        RuntimePinnedListHighlightService.Suspend("test storage reopen");
        AssertColor(recipeBase, recipeButton.image.get_color(), "Second scene suspension did not restore the recipe color.");
        var reopenedBeverageButton = new UIButtonSimpleProbe(storageBase);
        StoragePanelProbe.OpenAction = () => storagePanel.OnElementEnabled(
            new KeyValuePair<SellableProbe, int>(new SellableProbe(18, SellableTypeProbe.Beverage), 1),
            new object(),
            reopenedBeverageButton);
        storagePanel.OnPanelOpen();
        RuntimePinnedListHighlightService.Tick();
        AssertHighlighted(storageBase, reopenedBeverageButton.image.get_color(), "Storage panel open did not resume highlighting before its first element binding.");
        storagePanel.OnPanelClose();
        AssertColor(storageBase, reopenedBeverageButton.image.get_color(), "Reopened storage panel did not restore its beverage color on close.");
        StoragePanelProbe.OpenAction = null;

        cookingPanel.OnPanelOpen();
        CookingSelectionPanelProbe.RecipeBoundColor = recipeBase;
        cookingPanel.OnRecipeElementEnabled(new RecipeProbe(36), new object(), recipeButton);
        RuntimePinnedListHighlightService.Tick();
        var setterCountBeforeDisable = recipeButton.image.SetterCount;
        PublishRareTarget(
            RuntimeNightBusinessLifecycle.Generation,
            listPinningEnabled: false,
            recipeVariantEnabled: false,
            cookerHighlightEnabled: true,
            seatHighlightEnabled: false,
            orderHighlightEnabled: false,
            recipeId: 36,
            beverageId: 18,
            ingredientIds: new[] { 13 },
            extraIngredientIds: Array.Empty<int>(),
            cookerTypeId: 3,
            deskCode: -1,
            orderTraceId: "",
            targetRevision: "test-target");
        AssertEqual(setterCountBeforeDisable, recipeButton.image.SetterCount, "Disabling list pinning wrote a Unity image color off the main visual tick.");
        AssertHighlighted(recipeBase, recipeButton.image.get_color(), "Disabling list pinning touched Unity color before LateUpdate.");
        RuntimePinnedListHighlightService.Tick();
        AssertColor(recipeBase, recipeButton.image.get_color(), "Disabling list pinning did not restore the recipe color.");
        AssertTrue(RuntimeCookerHighlightService.LastEnabled, "Cooker-only highlighting was disabled alongside list highlighting.");

        PublishRareTarget(
            RuntimeNightBusinessLifecycle.Generation,
            listPinningEnabled: true,
            recipeVariantEnabled: false,
            cookerHighlightEnabled: false,
            seatHighlightEnabled: false,
            orderHighlightEnabled: false,
            recipeId: 37,
            beverageId: 19,
            ingredientIds: new[] { 14 },
            extraIngredientIds: Array.Empty<int>(),
            cookerTypeId: 3,
            deskCode: -1,
            orderTraceId: "",
            targetRevision: "test-target");
        var setterRaceButton = new UIButtonSimpleProbe(recipeBase);
        cookingPanel.OnRecipeElementEnabled(new RecipeProbe(37), new object(), setterRaceButton);
        using var setterEntered = new ManualResetEventSlim();
        using var setterRelease = new ManualResetEventSlim();
        setterRaceButton.image.BlockNextSetter(setterEntered, setterRelease);
        var visualErrorsBeforeRace = ReadListHighlightCounter("visualErrors");
        Exception? setterRaceFailure = null;
        var setterRaceWorker = new Thread(() =>
        {
            try
            {
                if (!setterEntered.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException("The highlight setter barrier was not reached.");
                }

                PublishRareTarget(
                    RuntimeNightBusinessLifecycle.Generation,
                    listPinningEnabled: true,
                    recipeVariantEnabled: false,
                    cookerHighlightEnabled: false,
                    seatHighlightEnabled: false,
                    orderHighlightEnabled: false,
                    recipeId: 38,
                    beverageId: 20,
                    ingredientIds: new[] { 15 },
                    extraIngredientIds: Array.Empty<int>(),
                    cookerTypeId: 3,
                    deskCode: -1,
                    orderTraceId: "",
                    targetRevision: "test-target");
            }
            catch (Exception ex)
            {
                setterRaceFailure = ex;
            }
            finally
            {
                setterRelease.Set();
            }
        });
        setterRaceWorker.Start();
        try
        {
            RuntimePinnedListHighlightService.Tick();
        }
        finally
        {
            setterRelease.Set();
            if (!setterRaceWorker.Join(TimeSpan.FromSeconds(7)))
            {
                throw new TimeoutException("The setter-race worker did not exit.");
            }
        }

        if (setterRaceFailure != null) throw new InvalidOperationException("Setter-race target publication failed.", setterRaceFailure);
        AssertFalse(setterRaceButton.image.BarrierTimedOut, "The highlight setter barrier timed out instead of observing a concurrent target update.");
        AssertEqual(visualErrorsBeforeRace, ReadListHighlightCounter("visualErrors"), "The setter-race path was hidden as a visual error.");
        AssertColor(recipeBase, setterRaceButton.image.get_color(), "A target change during the first highlight write left an untracked color.");
        AssertContains(RuntimePinnedListHighlightService.Status, "tracked=recipe:0", "A stale first highlight remained tracked after its target changed.");
        AssertTrue(setterRaceButton.image.SetterThreadIds.All(threadId => threadId == mainThreadId), "The setter-race image was written outside the Unity main thread.");

        var bindingErrorsBefore = ReadListHighlightCounter("bindingErrors");
        cookingPanel.OnRecipeElementEnabled(new RecipeProbe(38, throwOnIdRead: true), new object(), new UIButtonSimpleProbe(recipeBase));
        AssertEqual(bindingErrorsBefore + 1, ReadListHighlightCounter("bindingErrors"), "Element binding diagnostics did not record a reflection failure.");
    }
    finally
    {
        RuntimePinnedListHighlightService.Suspend("test cleanup");
        CookingSelectionPanelProbe.ApplyIngredientBoundColor = false;
        CookingSelectionPanelProbe.OpenAction = null;
        CookingSelectionPanelProbe.RecipeBoundColor = new Color(0.8f, 0.8f, 0.8f, 0.5f);
        StoragePanelProbe.OpenAction = null;
        StoragePanelProbe.BoundColor = new Color(0.35f, 0.55f, 0.75f, 0.6f);
    }
}

static void VerifyExactRecipeVariantRowHighlighting()
{
    const int recipeId = 141;
    var lifecycleLogStart = ManualLogSource.SnapshotInformationMessages().Length;
    var listHighlightRoot = typeof(RuntimePinnedListHighlightService)
        .GetField("SyncRoot", BindingFlags.NonPublic | BindingFlags.Static)?
        .GetValue(null)
        ?? throw new InvalidOperationException("Pinned-list SyncRoot was not found.");
    ManualLogSource.ResetInformationLogSafetyObservation();
    ManualLogSource.InformationLogUnsafeProbe = () => Monitor.IsEntered(listHighlightRoot);
    var businessGeneration = RuntimeNightBusinessLifecycle.Generation;
    var baseline = new Color(0.64f, 0.68f, 0.74f, 0.52f);
    AssertTrue(RuntimeTargetHighlightColor.TryParseExactHex("FFD35A", out var rareColor), "Rare test color was rejected.");
    AssertTrue(RuntimeTargetHighlightColor.TryParseExactHex("5FACD3", out var normalColor), "Normal test color was rejected.");

    RuntimeUiTargetSnapshot CreateTarget(
        RuntimeUiTargetKind kind,
        bool recipeVariantEnabled,
        string revision,
        RuntimeTargetHighlightColor? color = null,
        IReadOnlyList<int>? extras = null)
    {
        var isRare = kind == RuntimeUiTargetKind.Rare;
        var targetExtras = recipeVariantEnabled
            ? extras ?? new[] { isRare ? 143 : 144 }
            : Array.Empty<int>();
        return new RuntimeUiTargetSnapshot(
            kind,
            color ?? (isRare ? rareColor : normalColor),
            listPinningEnabled: true,
            recipeVariantEnabled: recipeVariantEnabled,
            cookerHighlightEnabled: false,
            seatHighlightEnabled: false,
            orderHighlightEnabled: false,
            orderTraceId: isRare ? "R-141" : "N-141",
            orderKey: isRare ? "" : "ptr:8d",
            orderLifecycleSequence: isRare ? 141 : 142,
            deskCode: isRare ? 0 : 1,
            recipeId,
            ingredientIds: new[] { 142 },
            extraIngredientIds: targetExtras,
            beverageId: -1,
            cookerTypeId: -1,
            targetRevision: $"{revision}-{kind}");
    }

    RuntimeTargetRecipeVariantService.RetireFailClosed("reset exact row-highlight smoke");
    RuntimeUiPinningService.UpdateTargets(
        businessGeneration,
        new[]
        {
            CreateTarget(RuntimeUiTargetKind.Rare, recipeVariantEnabled: false, revision: "uncontrolled"),
            CreateTarget(RuntimeUiTargetKind.Normal, recipeVariantEnabled: false, revision: "uncontrolled"),
        });

    CookingSelectionPanelProbe.RecipeBoundColor = baseline;
    var panel = new CookingSelectionPanelProbe();
    var priorIdModeRecipe = new RecipeProbe(recipeId);
    var priorIdModeButton = new UIButtonSimpleProbe(baseline);
    panel.OnPanelOpen();
    panel.OnRecipeElementEnabled(priorIdModeRecipe, new object(), priorIdModeButton);
    Time.realtimeSinceStartup = 0.25f;
    RuntimePinnedListHighlightService.Tick();
    AssertHighlighted(baseline, priorIdModeButton.image.get_color(), "An uncontrolled recipe lost its existing recipe-id highlight.");

    RuntimeUiPinningService.UpdateTargets(
        businessGeneration,
        new[]
        {
            CreateTarget(RuntimeUiTargetKind.Rare, recipeVariantEnabled: true, revision: "controlled"),
            CreateTarget(RuntimeUiTargetKind.Normal, recipeVariantEnabled: false, revision: "controlled"),
        });
    var controlledTarget = RuntimeUiPinningService.ReadTargetSet();
    AssertTrue(controlledTarget.HasRecipeVariants(recipeId), "The active variant recipe was not placed in exact-only mode.");
    AssertEqual(RuntimeUiTargetKinds.Normal, controlledTarget.GetBaseRecipeClaims(recipeId), "The authoritative base row did not retain only the non-variant normal claim.");
    AssertEqual(RuntimeUiTargetKinds.Rare, controlledTarget.GetRecipeVariantClaims(recipeId), "The synthetic row did not retain the rare variant claim.");

    RuntimePinnedListHighlightService.Tick();
    AssertColor(baseline, priorIdModeButton.image.get_color(), "An old recipe-id aggregate color survived the transition to exact-only mode.");
    AssertContains(RuntimePinnedListHighlightService.Status, "tracked=recipe:0", "An old recipe-id row stayed tracked after becoming variant-controlled.");

    var baseRecipe = new RecipeProbe(recipeId);
    var baseButton = new UIButtonSimpleProbe(baseline);
    RuntimeTargetRecipeVariantService.BindRecipeRow(
        panel,
        baseRecipe,
        baseButton,
        RuntimeUiTargetKinds.Normal,
        "normal-base");
    panel.OnRecipeElementEnabled(baseRecipe, new object(), baseButton);

    var syntheticRecipe = new RecipeProbe(recipeId);
    var syntheticButton = new UIButtonSimpleProbe(baseline);
    var staleSyntheticLease = RuntimeTargetRecipeVariantService.BindRecipeRow(
        panel,
        syntheticRecipe,
        syntheticButton,
        RuntimeUiTargetKinds.Rare,
        "rare-extra-143-stale");
    var currentSyntheticLease = RuntimeTargetRecipeVariantService.BindRecipeRow(
        panel,
        syntheticRecipe,
        syntheticButton,
        RuntimeUiTargetKinds.Rare,
        "rare-extra-143-current");
    AssertFalse(
        RuntimeTargetRecipeVariantService.TryValidateRecipeRowClaims(staleSyntheticLease, out _),
        "A pooled exact row accepted a stale plan identity with the same pointer tuple.");
    AssertTrue(
        RuntimeTargetRecipeVariantService.TryValidateRecipeRowClaims(currentSyntheticLease, out var reboundClaims)
            && reboundClaims == RuntimeUiTargetKinds.Rare,
        "The current exact row plan identity was not retained after a pointer-stable rebind.");
    panel.OnRecipeElementEnabled(syntheticRecipe, new object(), syntheticButton);

    var lifecycleMessages = ReadExactRecipeLifecycleMessages(lifecycleLogStart);
    var boundMessages = lifecycleMessages
        .Where(message => message.Contains("event=bound;", StringComparison.Ordinal))
        .ToArray();
    AssertEqual(2, boundMessages.Length, "Exact recipe rows did not log one lifecycle bind each.");
    AssertTrue(
        boundMessages.Any(message => message.Contains("plan=normal-base", StringComparison.Ordinal)
            && message.Contains("claims=Normal", StringComparison.Ordinal)),
        "The authoritative exact-row bind log lost its plan or claim identity.");
    AssertTrue(
        boundMessages.Any(message => message.Contains("plan=rare-extra-143-current", StringComparison.Ordinal)
            && message.Contains("claims=Rare", StringComparison.Ordinal)),
        "The synthetic exact-row bind log lost its current plan or claim identity.");
    AssertTrue(
        boundMessages.All(message => message.Contains($"business={businessGeneration};", StringComparison.Ordinal)
            && message.Contains($"targetGen={controlledTarget.Generation};", StringComparison.Ordinal)
            && message.Contains("panel=0x", StringComparison.Ordinal)
            && message.Contains("recipePtr=0x", StringComparison.Ordinal)
            && message.Contains("button=0x", StringComparison.Ordinal)
            && message.Contains("image=0x", StringComparison.Ordinal)),
        "An exact-row bind log omitted its bounded native identity fields.");

    var unboundSameIdButton = new UIButtonSimpleProbe(baseline);
    panel.OnRecipeElementEnabled(new RecipeProbe(recipeId), new object(), unboundSameIdButton);
    RuntimePinnedListHighlightService.Tick();

    AssertColor(
        RuntimeTargetHighlightStyle.BuildListItemPulseColor(
            baseline,
            RuntimeUiTargetKinds.Normal,
            controlledTarget.Palette,
            Time.realtimeSinceStartup),
        baseButton.image.get_color(),
        "The exact authoritative row did not use only the normal target color.");
    AssertColor(
        RuntimeTargetHighlightStyle.BuildListItemPulseColor(
            baseline,
            RuntimeUiTargetKinds.Rare,
            controlledTarget.Palette,
            Time.realtimeSinceStartup),
        syntheticButton.image.get_color(),
        "The exact synthetic row did not use its rare target color.");
    AssertColor(baseline, unboundSameIdButton.image.get_color(), "A same-id row without an exact panel/recipe/button binding was highlighted.");
    AssertContains(RuntimePinnedListHighlightService.Status, "tracked=recipe:2", "Exact rows did not retain one Image ownership each.");

    lifecycleMessages = ReadExactRecipeLifecycleMessages(lifecycleLogStart);
    AssertEqual(
        2,
        lifecycleMessages.Count(message => message.Contains("event=applied;", StringComparison.Ordinal)),
        "Exact recipe rows did not log their first durable highlight application once.");

    var releaseCountBeforePoolRebind = lifecycleMessages.Count(message => message.Contains("event=released;", StringComparison.Ordinal));
    panel.OnRecipeElementEnabled(baseRecipe, new object(), baseButton);
    lifecycleMessages = ReadExactRecipeLifecycleMessages(lifecycleLogStart);
    AssertEqual(
        releaseCountBeforePoolRebind + 1,
        lifecycleMessages.Count(message => message.Contains("event=released;", StringComparison.Ordinal)),
        "A pooled exact recipe row did not log its actual ownership release.");
    AssertTrue(
        lifecycleMessages.Any(message => message.Contains("event=released;", StringComparison.Ordinal)
            && message.Contains("reason=pool-rebind;", StringComparison.Ordinal)
            && message.Contains("plan=normal-base", StringComparison.Ordinal)),
        "The pooled exact-row release did not retain its precise reason and lease identity.");
    RuntimePinnedListHighlightService.Tick();
    lifecycleMessages = ReadExactRecipeLifecycleMessages(lifecycleLogStart);
    AssertEqual(
        3,
        lifecycleMessages.Count(message => message.Contains("event=applied;", StringComparison.Ordinal)),
        "A rebound exact row did not receive one new first-application lifecycle event.");

    RuntimeTargetRecipeVariantService.AdvancePanelEpoch(panel);
    RuntimePinnedListHighlightService.Tick();
    AssertColor(baseline, baseButton.image.get_color(), "A stale authoritative row lease kept its highlight after the panel epoch advanced.");
    AssertColor(baseline, syntheticButton.image.get_color(), "A stale synthetic row lease kept its highlight after the panel epoch advanced.");
    AssertContains(RuntimePinnedListHighlightService.Status, "tracked=recipe:0", "Stale exact recipe-row leases remained tracked.");
    lifecycleMessages = ReadExactRecipeLifecycleMessages(lifecycleLogStart);
    var invalidLeaseReleaseCount = lifecycleMessages.Count(message =>
        message.Contains("event=released;", StringComparison.Ordinal)
        && message.Contains("reason=exact-lease-invalid;", StringComparison.Ordinal));
    AssertEqual(2, invalidLeaseReleaseCount, "Epoch drift did not release both exact rows with the lease-invalid reason.");
    RuntimePinnedListHighlightService.Tick();
    AssertEqual(
        invalidLeaseReleaseCount,
        ReadExactRecipeLifecycleMessages(lifecycleLogStart).Count(message =>
            message.Contains("event=released;", StringComparison.Ordinal)
            && message.Contains("reason=exact-lease-invalid;", StringComparison.Ordinal)),
        "A released exact-row lease emitted another lifecycle event on a later Tick.");

    panel.OnPanelClose();
    RuntimeTargetRecipeVariantService.RetireFailClosed("exact row-highlight smoke complete");

    AssertTrue(RuntimeTargetHighlightColor.TryParseExactHex("2A9FD6", out var customNormalColor), "Custom normal test color was rejected.");
    RuntimeUiPinningService.UpdateTargets(
        businessGeneration,
        new[]
        {
            CreateTarget(RuntimeUiTargetKind.Rare, recipeVariantEnabled: false, revision: "symmetric"),
            CreateTarget(
                RuntimeUiTargetKind.Normal,
                recipeVariantEnabled: true,
                revision: "symmetric",
                color: customNormalColor,
                extras: new[] { 144 }),
        });
    var symmetricTarget = RuntimeUiPinningService.ReadTargetSet();
    AssertEqual(RuntimeUiTargetKinds.Rare, symmetricTarget.GetBaseRecipeClaims(recipeId), "The symmetric base row did not retain only the rare claim.");
    AssertEqual(RuntimeUiTargetKinds.Normal, symmetricTarget.GetRecipeVariantClaims(recipeId), "The symmetric synthetic row did not retain only the normal claim.");
    var symmetricPanel = new CookingSelectionPanelProbe();
    symmetricPanel.OnPanelOpen();
    var symmetricBaseRecipe = new RecipeProbe(recipeId);
    var symmetricBaseButton = new UIButtonSimpleProbe(baseline);
    RuntimeTargetRecipeVariantService.BindRecipeRow(
        symmetricPanel,
        symmetricBaseRecipe,
        symmetricBaseButton,
        RuntimeUiTargetKinds.Rare,
        "rare-base-symmetric");
    symmetricPanel.OnRecipeElementEnabled(symmetricBaseRecipe, new object(), symmetricBaseButton);
    var symmetricSyntheticRecipe = new RecipeProbe(recipeId);
    var symmetricSyntheticButton = new UIButtonSimpleProbe(baseline);
    RuntimeTargetRecipeVariantService.BindRecipeRow(
        symmetricPanel,
        symmetricSyntheticRecipe,
        symmetricSyntheticButton,
        RuntimeUiTargetKinds.Normal,
        "normal-extra-144");
    symmetricPanel.OnRecipeElementEnabled(symmetricSyntheticRecipe, new object(), symmetricSyntheticButton);
    Time.realtimeSinceStartup = 0.25f;
    RuntimePinnedListHighlightService.Tick();
    AssertColor(
        RuntimeTargetHighlightStyle.BuildListItemPulseColor(
            baseline,
            RuntimeUiTargetKinds.Rare,
            symmetricTarget.Palette,
            Time.realtimeSinceStartup),
        symmetricBaseButton.image.get_color(),
        "The symmetric authoritative row did not use only the rare target color.");
    AssertColor(
        RuntimeTargetHighlightStyle.BuildListItemPulseColor(
            baseline,
            RuntimeUiTargetKinds.Normal,
            symmetricTarget.Palette,
            Time.realtimeSinceStartup),
        symmetricSyntheticButton.image.get_color(),
        "The normal synthetic row did not use its custom target color.");
    var panelCloseReleaseCount = ReadExactRecipeLifecycleMessages(lifecycleLogStart).Count(message =>
        message.Contains("event=released;", StringComparison.Ordinal)
        && message.Contains("reason=panel-closed;", StringComparison.Ordinal));
    symmetricPanel.OnPanelClose();
    AssertEqual(
        panelCloseReleaseCount + 2,
        ReadExactRecipeLifecycleMessages(lifecycleLogStart).Count(message =>
            message.Contains("event=released;", StringComparison.Ordinal)
            && message.Contains("reason=panel-closed;", StringComparison.Ordinal)),
        "Cooking panel close did not log both exact-row ownership releases precisely.");
    RuntimeTargetRecipeVariantService.RetireFailClosed("symmetric exact row-highlight smoke complete");

    RuntimeUiPinningService.UpdateTargets(
        businessGeneration,
        new[]
        {
            CreateTarget(RuntimeUiTargetKind.Rare, recipeVariantEnabled: true, revision: "split-extras", extras: new[] { 143 }),
            CreateTarget(RuntimeUiTargetKind.Normal, recipeVariantEnabled: true, revision: "split-extras", color: customNormalColor, extras: new[] { 144 }),
        });
    var splitExtrasTarget = RuntimeUiPinningService.ReadTargetSet();
    AssertEqual(RuntimeUiTargetKinds.None, splitExtrasTarget.GetBaseRecipeClaims(recipeId), "Two variant targets leaked an aggregate claim onto the base row.");
    var splitPanel = new CookingSelectionPanelProbe();
    splitPanel.OnPanelOpen();
    var rareSplitRecipe = new RecipeProbe(recipeId);
    var rareSplitButton = new UIButtonSimpleProbe(baseline);
    RuntimeTargetRecipeVariantService.BindRecipeRow(
        splitPanel,
        rareSplitRecipe,
        rareSplitButton,
        RuntimeUiTargetKinds.Rare,
        "rare-extra-143-split");
    splitPanel.OnRecipeElementEnabled(rareSplitRecipe, new object(), rareSplitButton);
    var normalSplitRecipe = new RecipeProbe(recipeId);
    var normalSplitButton = new UIButtonSimpleProbe(baseline);
    RuntimeTargetRecipeVariantService.BindRecipeRow(
        splitPanel,
        normalSplitRecipe,
        normalSplitButton,
        RuntimeUiTargetKinds.Normal,
        "normal-extra-144-split");
    splitPanel.OnRecipeElementEnabled(normalSplitRecipe, new object(), normalSplitButton);
    RuntimePinnedListHighlightService.Tick();
    AssertColor(
        RuntimeTargetHighlightStyle.BuildListItemPulseColor(
            baseline,
            RuntimeUiTargetKinds.Rare,
            splitExtrasTarget.Palette,
            Time.realtimeSinceStartup),
        rareSplitButton.image.get_color(),
        "Different rare extras did not retain their exact rare-only synthetic color.");
    AssertColor(
        RuntimeTargetHighlightStyle.BuildListItemPulseColor(
            baseline,
            RuntimeUiTargetKinds.Normal,
            splitExtrasTarget.Palette,
            Time.realtimeSinceStartup),
        normalSplitButton.image.get_color(),
        "Different normal extras did not retain their exact normal-only synthetic color.");
    splitPanel.OnPanelClose();
    RuntimeTargetRecipeVariantService.RetireFailClosed("split extras exact row-highlight smoke complete");

    RuntimeUiPinningService.UpdateTargets(
        businessGeneration,
        new[]
        {
            CreateTarget(RuntimeUiTargetKind.Rare, recipeVariantEnabled: true, revision: "shared-extras", extras: new[] { 145 }),
            CreateTarget(RuntimeUiTargetKind.Normal, recipeVariantEnabled: true, revision: "shared-extras", color: customNormalColor, extras: new[] { 145 }),
        });
    var sharedExtrasTarget = RuntimeUiPinningService.ReadTargetSet();
    var sharedPanel = new CookingSelectionPanelProbe();
    sharedPanel.OnPanelOpen();
    var sharedRecipe = new RecipeProbe(recipeId);
    var sharedButton = new UIButtonSimpleProbe(baseline);
    RuntimeTargetRecipeVariantService.BindRecipeRow(
        sharedPanel,
        sharedRecipe,
        sharedButton,
        RuntimeUiTargetKinds.Rare | RuntimeUiTargetKinds.Normal,
        "shared-extra-145");
    sharedPanel.OnRecipeElementEnabled(sharedRecipe, new object(), sharedButton);
    Time.realtimeSinceStartup = 0.75f;
    RuntimePinnedListHighlightService.Tick();
    AssertColor(
        RuntimeTargetHighlightStyle.BuildListItemPulseColor(
            baseline,
            RuntimeUiTargetKinds.Rare | RuntimeUiTargetKinds.Normal,
            sharedExtrasTarget.Palette,
            Time.realtimeSinceStartup),
        sharedButton.image.get_color(),
        "A shared ordered-extras row did not retain its dual target claim and palette.");
    sharedPanel.OnPanelClose();
    RuntimeTargetRecipeVariantService.RetireFailClosed("shared extras exact row-highlight smoke complete");

    var boundedPanel = new CookingSelectionPanelProbe();
    boundedPanel.OnPanelOpen();
    for (var index = 0; index < 20; index += 1)
    {
        var boundedRecipe = new RecipeProbe(recipeId);
        var boundedButton = new UIButtonSimpleProbe(baseline);
        RuntimeTargetRecipeVariantService.BindRecipeRow(
            boundedPanel,
            boundedRecipe,
            boundedButton,
            RuntimeUiTargetKinds.Rare | RuntimeUiTargetKinds.Normal,
            $"bounded-exact-row-{index}");
        boundedPanel.OnRecipeElementEnabled(boundedRecipe, new object(), boundedButton);
    }
    boundedPanel.OnPanelClose();
    RuntimeTargetRecipeVariantService.RetireFailClosed("bounded exact row-highlight logging smoke complete");

    lifecycleMessages = ReadExactRecipeLifecycleMessages(lifecycleLogStart);
    AssertEqual(32, lifecycleMessages.Length, "Exact-row lifecycle logging exceeded or failed to reach its per-business bound.");
    AssertContains(RuntimePinnedListHighlightService.Status, "exactRecipeLogs=32/32", "Exact-row lifecycle log status did not expose the active bound.");
    AssertTrue(
        ReadListHighlightCounter("exactRecipeLogsSuppressed") > 0,
        "Exact-row lifecycle events beyond the per-business bound were not counted as suppressed.");
    AssertFalse(
        ManualLogSource.InformationLoggedWhileUnsafe,
        "An exact-row lifecycle message was emitted while the pinned-list state lock was held.");
    ManualLogSource.InformationLogUnsafeProbe = null;
}

static string[] ReadExactRecipeLifecycleMessages(int startIndex)
{
    return ManualLogSource.SnapshotInformationMessages()
        .Skip(startIndex)
        .Where(message => message.StartsWith(
            "Runtime pinned list exact recipe lifecycle ",
            StringComparison.Ordinal))
        .ToArray();
}

static string PublishRareTarget(
    long sessionGeneration,
    bool listPinningEnabled,
    bool recipeVariantEnabled,
    bool cookerHighlightEnabled,
    bool seatHighlightEnabled,
    bool orderHighlightEnabled,
    int recipeId,
    int beverageId,
    IEnumerable<int> ingredientIds,
    IEnumerable<int> extraIngredientIds,
    int cookerTypeId,
    int deskCode,
    string orderTraceId,
    string targetRevision)
{
    var ingredients = ingredientIds.ToArray();
    var extras = extraIngredientIds.ToArray();
    var publishesTarget = recipeId >= 0
        || beverageId >= 0
        || cookerTypeId >= 0
        || deskCode >= 0
        || ingredients.Length > 0
        || extras.Length > 0
        || orderTraceId.Length > 0;
    var targets = publishesTarget
        ? new[]
        {
            new RuntimeUiTargetSnapshot(
                RuntimeUiTargetKind.Rare,
                RuntimeTargetHighlightColor.DefaultRare,
                listPinningEnabled,
                recipeVariantEnabled,
                cookerHighlightEnabled,
                seatHighlightEnabled,
                orderHighlightEnabled,
                orderHighlightEnabled ? orderTraceId : "R-1",
                orderKey: "",
                orderLifecycleSequence: 1,
                deskCode >= 0 ? deskCode : 0,
                recipeId,
                ingredients,
                extras,
                beverageId,
                cookerTypeId,
                targetRevision),
        }
        : Array.Empty<RuntimeUiTargetSnapshot>();
    return RuntimeUiPinningService.UpdateTargets(
        sessionGeneration,
        targets);
}

static void RunOnWorkerThread(Action action)
{
    Exception? failure = null;
    var thread = new Thread(() =>
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            failure = ex;
        }
    });
    thread.Start();
    thread.Join();
    if (failure != null) throw new InvalidOperationException("Worker-thread target publication failed.", failure);
}

static long ReadListHighlightCounter(string counterName)
{
    var match = Regex.Match(RuntimePinnedListHighlightService.Status, $@"\b{Regex.Escape(counterName)}=(\d+)");
    if (!match.Success || !long.TryParse(match.Groups[1].Value, out var value))
    {
        throw new InvalidOperationException($"Could not read {counterName} from status: {RuntimePinnedListHighlightService.Status}");
    }

    return value;
}

static long ReadPanelRefreshCounter(string counterName)
{
    var match = Regex.Match(RuntimeUiPinningService.Status, $@"\b{Regex.Escape(counterName)}:(\d+)");
    if (!match.Success || !long.TryParse(match.Groups[1].Value, out var value))
    {
        throw new InvalidOperationException(
            $"Could not read panel refresh {counterName} from status: {RuntimeUiPinningService.Status}");
    }

    return value;
}

static long ReadPanelRefreshGeneration(string panelKind, string generationKind)
{
    var match = Regex.Match(
        RuntimeUiPinningService.Status,
        $@"panelRefresh=[^;]*\b{Regex.Escape(panelKind)}:open@[^;]*?/{Regex.Escape(generationKind)}:(-?\d+)");
    if (!match.Success || !long.TryParse(match.Groups[1].Value, out var value))
    {
        throw new InvalidOperationException(
            $"Could not read {panelKind} {generationKind} generation from status: {RuntimeUiPinningService.Status}");
    }

    return value;
}

static bool InvokeCheckPinned(int pinnedType, int pinnedId, bool originalResult = false)
{
    var call = InvokeCheckPinnedPrefix(pinnedType, pinnedId);
    return call.RunOriginal ? originalResult : call.Result;
}

static (bool RunOriginal, bool Result) InvokeCheckPinnedPrefix(int pinnedType, int pinnedId)
{
    var args = new object?[] { pinnedType, pinnedId, false };
    var runOriginal = (bool)(InvokePrivate("OnCheckPinned", args) ?? true);
    return (runOriginal, (bool)(args[2] ?? false));
}

static long ReadForcedTotal(string targetType)
{
    var match = Regex.Match(RuntimeUiPinningService.Status, $@"forcedTotal=[^;]*\b{Regex.Escape(targetType)}:(\d+)");
    if (!match.Success || !long.TryParse(match.Groups[1].Value, out var value))
    {
        throw new InvalidOperationException($"Could not read {targetType} forcedTotal from status: {RuntimeUiPinningService.Status}");
    }

    return value;
}

static object? InvokePrivate(string methodName, params object?[] args)
{
    var method = typeof(RuntimeUiPinningService).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMethodException(typeof(RuntimeUiPinningService).FullName, methodName);
    try
    {
        return method.Invoke(null, args);
    }
    catch (TargetInvocationException ex) when (ex.InnerException != null)
    {
        throw ex.InnerException;
    }
}

static void AssertPatch(
    Type declaringType,
    string originalName,
    string? prefix,
    string? postfix,
    string? finalizer,
    Type? patchOwner = null,
    int? prefixPriority = null,
    int? postfixPriority = null,
    string? postfixAfter = null)
{
    var original = declaringType.GetMethod(originalName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
        ?? throw new MissingMethodException(declaringType.FullName, originalName);
    var patch = Harmony.GetPatchInfo(original)
        ?? throw new InvalidOperationException($"Patch for {declaringType.FullName}.{originalName} was not installed.");
    var serviceType = patchOwner ?? typeof(RuntimeUiPinningService);
    var actualPrefix = patch.Prefixes.Select(item => item.PatchMethod).SingleOrDefault(method => method.DeclaringType == serviceType)?.Name;
    var actualPostfix = patch.Postfixes.Select(item => item.PatchMethod).SingleOrDefault(method => method.DeclaringType == serviceType)?.Name;
    var actualFinalizer = patch.Finalizers.Select(item => item.PatchMethod).SingleOrDefault(method => method.DeclaringType == serviceType)?.Name;
    AssertEqual(prefix, actualPrefix, $"Unexpected prefix for {originalName}.");
    AssertEqual(postfix, actualPostfix, $"Unexpected postfix for {originalName}.");
    AssertEqual(finalizer, actualFinalizer, $"Unexpected finalizer for {originalName}.");
    if (prefixPriority.HasValue)
    {
        var actualPriority = patch.Prefixes.Single(item => item.PatchMethod.DeclaringType == serviceType).priority;
        AssertEqual(prefixPriority.Value, actualPriority, $"Unexpected prefix priority for {originalName}.");
    }
    if (postfixPriority.HasValue)
    {
        var actualPriority = patch.Postfixes.Single(item => item.PatchMethod.DeclaringType == serviceType).priority;
        AssertEqual(postfixPriority.Value, actualPriority, $"Unexpected postfix priority for {originalName}.");
    }
    if (postfixAfter != null)
    {
        var after = patch.Postfixes.Single(item => item.PatchMethod.DeclaringType == serviceType).after;
        AssertTrue(after.Contains(postfixAfter, StringComparer.Ordinal), $"Postfix ordering for {originalName} did not follow the variant binding service.");
    }
}

static void AssertHighlighted(Color original, Color actual, string message)
{
    AssertEqualWithin(original.a, actual.a, $"{message} Alpha changed.");
    var rgbChanged = Math.Abs(original.r - actual.r) > 0.001f
        || Math.Abs(original.g - actual.g) > 0.001f
        || Math.Abs(original.b - actual.b) > 0.001f;
    if (!rgbChanged) throw new InvalidOperationException(message);
}

static void AssertColor(Color expected, Color actual, string message)
{
    AssertEqualWithin(expected.r, actual.r, $"{message} Red channel differs.");
    AssertEqualWithin(expected.g, actual.g, $"{message} Green channel differs.");
    AssertEqualWithin(expected.b, actual.b, $"{message} Blue channel differs.");
    AssertEqualWithin(expected.a, actual.a, $"{message} Alpha channel differs.");
}

static void AssertEqualWithin(float expected, float actual, string message)
{
    if (Math.Abs(expected - actual) > 0.001f)
    {
        throw new InvalidOperationException($"{message} Expected '{expected}', actual '{actual}'.");
    }
}

static void AssertContains(string actual, string expected, string message)
{
    if (!actual.Contains(expected, StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"{message} Expected fragment '{expected}', actual '{actual}'.");
    }
}

static void AssertSequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual, string message)
{
    if (!expected.SequenceEqual(actual))
    {
        throw new InvalidOperationException(
            $"{message} Expected '[{string.Join(",", expected)}]', actual '[{string.Join(",", actual)}]'.");
    }
}

static void AssertTrue(bool actual, string message)
{
    if (!actual) throw new InvalidOperationException(message);
}

static void AssertFalse(bool actual, string message)
{
    if (actual) throw new InvalidOperationException(message);
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

static void AssertEqual<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{message} Expected '{expected}', actual '{actual}'.");
    }
}
