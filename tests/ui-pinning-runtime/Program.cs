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
    VerifySharedListItemUsesSingleOwnershipAndBaseline();
    VerifyOrderHighlightRuntimeWiring();
    VerifyOpenPanelRefreshScheduling();
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
    CookingSelectionPanelProbe.RefreshAction = () =>
        RunTimePlayerDataProbe.CheckPinned(PlayerSaveFileDefaultPropProbe.Recipes, recipeId);
    StoragePanelProbe.RefreshAction = () =>
        RunTimePlayerDataProbe.CheckPinned(PlayerSaveFileDefaultPropProbe.Beverages, beverageId);

    try
    {
        cookingPanel.OnPanelOpen();
        storagePanel.OnPanelOpen();
        cookingPanel.OnRecipeElementEnabled(new RecipeProbe(recipeId), new object(), recipeButton);
        RuntimePinnedListHighlightService.Tick();

        AssertTrue(CookingSelectionPanelProbe.LastResult == true, "The active recipe target did not use scoped pinning.");
        AssertTrue(StoragePanelProbe.LastResult == true, "The active beverage target did not use scoped pinning.");
        AssertEqual(0, RunTimePlayerDataProbe.NativeCallCount, "The active Mod targets called the native pinned probe.");
        AssertHighlighted(baseColor, recipeButton.image.get_color(), "The active recipe target was not highlighted.");
        AssertTrue(RuntimeCookerHighlightService.LastEnabled, "The active cooker target did not enable the cooker stub.");
        AssertEqual(cookerTypeId, RuntimeCookerHighlightService.LastCookerTypeId, "The cooker stub did not retain the active cooker type.");

        var activeTargetGeneration = RuntimeUiPinningService.ReadTargetSet().Generation;
        var cookingRefreshCount = CookingSelectionPanelProbe.RefreshCount;
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
        AssertEqual(cookingRefreshCount + 1, CookingSelectionPanelProbe.RefreshCount, "An open cooking panel did not refresh once for the empty target.");
        AssertEqual(storageRefreshCount + 1, StoragePanelProbe.RefreshCount, "An open storage panel did not refresh once for the empty target.");
        AssertTrue(CookingSelectionPanelProbe.LastResult == false, "The empty recipe target did not restore the native pinned result.");
        AssertTrue(StoragePanelProbe.LastResult == false, "The empty beverage target did not restore the native pinned result.");
        AssertEqual(2, RunTimePlayerDataProbe.NativeCallCount, "The empty target did not execute both native pinned probes.");

        RuntimePinnedListHighlightService.Tick();
        AssertColor(baseColor, recipeButton.image.get_color(), "The empty target did not restore the recipe highlight color.");
        AssertContains(RuntimePinnedListHighlightService.Status, "tracked=recipe:0", "The empty target kept the old recipe image tracked.");

        RuntimeUiPinningService.Tick();
        AssertEqual(cookingRefreshCount + 1, CookingSelectionPanelProbe.RefreshCount, "The empty target refreshed the cooking panel more than once.");
        AssertEqual(storageRefreshCount + 1, StoragePanelProbe.RefreshCount, "The empty target refreshed the storage panel more than once.");
    }
    finally
    {
        CookingSelectionPanelProbe.RefreshAction = null;
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
        AssertEqual(1, CookingSelectionPanelProbe.RefreshCount, "Cooking panel did not naturally apply an existing target.");
        AssertEqual(1, StoragePanelProbe.RefreshCount, "Storage panel did not naturally apply an existing target.");
        RuntimeUiPinningService.Tick();
        AssertEqual(1, CookingSelectionPanelProbe.RefreshCount, "Cooking panel repeated a target already applied during open.");
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
        AssertEqual(1, CookingSelectionPanelProbe.RefreshCount, "Cooking panel open did not perform its natural refresh.");
        AssertEqual(1, StoragePanelProbe.RefreshCount, "Storage panel open did not perform its natural refresh.");

        RunOnWorkerThread(() => Publish(301, 401));
        AssertEqual(1, CookingSelectionPanelProbe.RefreshCount, "Worker target publication refreshed the cooking panel.");
        AssertEqual(1, StoragePanelProbe.RefreshCount, "Worker target publication refreshed the storage panel.");
        RuntimeUiPinningService.Tick();
        AssertEqual(2, CookingSelectionPanelProbe.RefreshCount, "An open cooking panel did not consume the new target once.");
        AssertEqual(2, StoragePanelProbe.RefreshCount, "An open storage panel did not consume the new target once.");
        RuntimeUiPinningService.Tick();
        AssertEqual(2, CookingSelectionPanelProbe.RefreshCount, "Cooking panel refreshed twice for one target generation.");
        AssertEqual(2, StoragePanelProbe.RefreshCount, "Storage panel refreshed twice for one target generation.");

        RunOnWorkerThread(() => Publish(301, 401));
        RuntimeUiPinningService.Tick();
        AssertEqual(2, CookingSelectionPanelProbe.RefreshCount, "An identical target refreshed the cooking panel.");
        AssertEqual(2, StoragePanelProbe.RefreshCount, "An identical target refreshed the storage panel.");

        RunOnWorkerThread(() => Publish(302, 402));
        RuntimeUiPinningService.Tick();
        AssertEqual(3, CookingSelectionPanelProbe.RefreshCount, "A new target did not refresh the cooking panel exactly once.");
        AssertEqual(3, StoragePanelProbe.RefreshCount, "A new target did not refresh the storage panel exactly once.");

        cookingPanel.OnPanelClose();
        RunOnWorkerThread(() => Publish(303, 403));
        RuntimeUiPinningService.Tick();
        AssertEqual(3, CookingSelectionPanelProbe.RefreshCount, "A closed cooking panel was refreshed.");
        AssertEqual(4, StoragePanelProbe.RefreshCount, "The still-open storage panel did not refresh.");

        storagePanel.OnPanelDestroyed();
        RunOnWorkerThread(() => Publish(304, 404));
        RuntimeUiPinningService.Tick();
        AssertEqual(3, CookingSelectionPanelProbe.RefreshCount, "A closed cooking panel returned after another target.");
        AssertEqual(4, StoragePanelProbe.RefreshCount, "A destroyed storage panel was refreshed.");

        var staleCookingPanel = new CookingSelectionPanelProbe();
        var staleStoragePanel = new StoragePanelProbe();
        staleCookingPanel.OnPanelOpen();
        staleStoragePanel.OnPanelOpen();
        AssertEqual(4, CookingSelectionPanelProbe.RefreshCount, "Generation-mismatch cooking setup did not open.");
        AssertEqual(5, StoragePanelProbe.RefreshCount, "Generation-mismatch storage setup did not open.");
        RuntimeNightBusinessLifecycle.ActivateNextGeneration();
        RunOnWorkerThread(() => Publish(305, 405));
        RuntimeUiPinningService.Tick();
        AssertEqual(4, CookingSelectionPanelProbe.RefreshCount, "A prior-generation cooking panel was refreshed.");
        AssertEqual(5, StoragePanelProbe.RefreshCount, "A prior-generation storage panel was refreshed.");
        staleCookingPanel.OnPanelClose();
        staleStoragePanel.OnPanelDestroyed();

        var failingCookingPanel = new CookingSelectionPanelProbe();
        failingCookingPanel.OnPanelOpen();
        AssertEqual(5, CookingSelectionPanelProbe.RefreshCount, "Failure-path cooking setup did not open.");
        RunOnWorkerThread(() => Publish(306, 406));
        CookingSelectionPanelProbe.ThrowOnRefresh = true;
        RuntimeUiPinningService.Tick();
        AssertEqual(6, CookingSelectionPanelProbe.RefreshCount, "The failing cooking refresh was not attempted once.");
        RuntimeUiPinningService.Tick();
        AssertEqual(6, CookingSelectionPanelProbe.RefreshCount, "A failed cooking refresh retried without a new target.");
        AssertContains(RuntimeUiPinningService.Status, "failures:1", "Panel refresh failure diagnostics were not retained.");

        CookingSelectionPanelProbe.ThrowOnRefresh = false;
        RunOnWorkerThread(() => Publish(307, 407));
        RuntimeUiPinningService.Tick();
        AssertEqual(7, CookingSelectionPanelProbe.RefreshCount, "A later target did not recover after the one-shot failure.");
        failingCookingPanel.OnPanelClose();

        AssertTrue(
            CookingSelectionPanelProbe.RefreshThreadIds.All(threadId => threadId == mainThreadId),
            "A cooking panel refresh ran outside the Unity main thread.");
        AssertTrue(
            StoragePanelProbe.RefreshThreadIds.All(threadId => threadId == mainThreadId),
            "A storage panel refresh ran outside the Unity main thread.");
    }
    finally
    {
        CookingSelectionPanelProbe.ThrowOnRefresh = false;
        StoragePanelProbe.ThrowOnRefresh = false;
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
    AssertEqual(12, patchedProbeCount, "Runtime UI pinning and list highlighting should install exactly twelve patches.");
    AssertPatch(
        typeof(CookingSelectionPanelProbe),
        nameof(CookingSelectionPanelProbe.UpdateAllVisual),
        prefix: "OnCookingRefreshStarted",
        postfix: null,
        finalizer: "OnCookingRefreshFinalized");
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
        postfixPriority: Priority.Last);
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
    AssertContains(RuntimeUiPinningService.Status, "forcedTotal=recipe:1, ingredients:4, beverage:1", "Scoped prefix diagnostics are incorrect.");
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

        CookingSelectionPanelProbe.RefreshAction = () => RunTimePlayerDataProbe.CheckPinned(PlayerSaveFileDefaultPropProbe.Recipes, 34);
        new CookingSelectionPanelProbe().UpdateAllVisual();
        AssertTrue(CookingSelectionPanelProbe.LastResult == true, "The managed Harmony wrapper did not propagate the forced recipe result.");
        AssertEqual(1, RunTimePlayerDataProbe.NativeCallCount, "The managed Harmony wrapper did not skip the target's original method.");
        AssertFalse(RunTimePlayerDataProbe.CheckPinned(PlayerSaveFileDefaultPropProbe.Recipes, 34), "Cooking scope leaked after a normal managed wrapper return.");
        AssertEqual(2, RunTimePlayerDataProbe.NativeCallCount, "The native CheckPinned probe did not resume after a normal wrapper return.");

        RunTimePlayerDataProbe.NativeResult = true;
        CookingSelectionPanelProbe.RefreshAction = () => RunTimePlayerDataProbe.CheckPinned(PlayerSaveFileDefaultPropProbe.Recipes, 99);
        new CookingSelectionPanelProbe().UpdateAllVisual();
        AssertTrue(CookingSelectionPanelProbe.LastResult == true, "The managed Harmony wrapper changed a native favorite result.");
        AssertEqual(3, RunTimePlayerDataProbe.NativeCallCount, "A non-target native favorite did not execute the original method.");

        RunTimePlayerDataProbe.Reset(nativeResult: false);
        var expectedException = new InvalidOperationException("managed wrapper failure");
        CookingSelectionPanelProbe.RefreshAction = () => throw expectedException;
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
        CookingSelectionPanelProbe.RefreshAction = null;
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
    int? postfixPriority = null)
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
