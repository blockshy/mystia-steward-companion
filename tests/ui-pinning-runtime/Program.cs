using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx.Logging;
using HarmonyLib;
using MystiaStewardCompanion.Save;
using UnityEngine;

try
{
    VerifyPatchTargets();
    VerifyOpenPanelRefreshScheduling();
    VerifyIdenticalTargetPublicationIsIdempotent();
    VerifyScopedNativePinnedMatching();
    VerifyTargetUpdatePreservesForceTotals();
    VerifyScopePinsOneTargetSnapshot();
    VerifyNestedScopeFinalizers();
    VerifyThreadLocalScopeIsolation();
    VerifyPinningAndHighlightRemainIndependent();
    VerifyDangerousListHooksAreAbsent();
    VerifyManagedHarmonyReturnPropagation();
    VerifyManagedPinnedListHighlighting();
    VerifyLifecycleGenerationGuards();
    Console.WriteLine("PASS: scoped pinning and pinned-list highlighting propagate through Harmony without mutating IL2CPP lists.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL: {ex}");
    return 1;
}

static void VerifyOpenPanelRefreshScheduling()
{
    var mainThreadId = Environment.CurrentManagedThreadId;
    CookingSelectionPanelProbe.ResetRefreshProbe();
    StoragePanelProbe.ResetRefreshProbe();

    void Publish(int recipeId, int beverageId, bool enabled = true)
    {
        RuntimeUiPinningService.UpdateTarget(
            RuntimeNightBusinessLifecycle.Generation,
            enabled,
            highlightEnabled: false,
            recipeId,
            beverageId,
            ingredientIds: enabled ? new[] { recipeId + 1000 } : Array.Empty<int>(),
            recipeName: enabled ? $"recipe-{recipeId}" : "",
            beverageName: enabled ? $"beverage-{beverageId}" : "",
            cookerTypeId: enabled ? 3 : -1,
            cookerName: enabled ? "cooker" : "");
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

        Publish(-1, -1, enabled: false);
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
    RuntimeUiPinningService.UpdateTarget(
        firstGeneration,
        enabled: true,
        highlightEnabled: true,
        recipeId: 90,
        beverageId: 91,
        ingredientIds: new[] { 92 },
        recipeName: "first-generation",
        beverageName: "first-generation",
        cookerTypeId: 3,
        cookerName: "first-generation");

    InvokePrivate("OnCookingRefreshStarted", new CookingSelectionPanelProbe());
    try
    {
        AssertTrue(InvokeCheckPinned(1, 90), "The active generation did not force its recipe target.");
        RuntimeNightBusinessLifecycle.BeginClosing();
        AssertTrue(InvokeCheckPinnedPrefix(1, 90).RunOriginal, "A scope captured before Closing still skipped native CheckPinned.");
        AssertFalse(RuntimeUiPinningService.ReadPinningTarget().Enabled, "Closing exposed the stale generation target.");
    }
    finally
    {
        InvokePrivate("OnCookingRefreshFinalized", new object?[] { null });
    }

    AssertThrows<InvalidOperationException>(
        () => RuntimeUiPinningService.UpdateTarget(
            firstGeneration,
            enabled: true,
            highlightEnabled: false,
            recipeId: 93,
            beverageId: 94,
            ingredientIds: Array.Empty<int>(),
            recipeName: "closing",
            beverageName: "closing",
            cookerTypeId: 3,
            cookerName: "closing"),
        "Closing accepted a UI target publication.");

    RuntimeUiPinningService.InvalidateTarget(firstGeneration, "test closing");
    AssertFalse(RuntimeUiPinningService.ReadPinningTarget().Enabled, "Invalidation left pinning enabled.");

    RuntimeNightBusinessLifecycle.ActivateNextGeneration();
    var secondGeneration = RuntimeNightBusinessLifecycle.Generation;
    AssertEqual(firstGeneration + 1, secondGeneration, "The next business session did not advance its generation.");
    AssertThrows<InvalidOperationException>(
        () => RuntimeUiPinningService.UpdateTarget(
            firstGeneration,
            enabled: true,
            highlightEnabled: false,
            recipeId: 95,
            beverageId: 96,
            ingredientIds: Array.Empty<int>(),
            recipeName: "stale",
            beverageName: "stale",
            cookerTypeId: 3,
            cookerName: "stale"),
        "A stale generation target was accepted by the next business session.");

    RunOnWorkerThread(() => RuntimeUiPinningService.UpdateTarget(
        secondGeneration,
        enabled: true,
        highlightEnabled: false,
        recipeId: 97,
        beverageId: 98,
        ingredientIds: new[] { 99 },
        recipeName: "second-generation",
        beverageName: "second-generation",
        cookerTypeId: 3,
        cookerName: "second-generation"));
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
        bool nextEnabled = true,
        bool nextHighlightEnabled = true,
        int nextRecipeId = recipeId,
        int nextBeverageId = beverageId,
        int[]? nextIngredientIds = null,
        string recipeName = "recipe",
        string beverageName = "beverage",
        int nextCookerTypeId = cookerTypeId,
        string cookerName = "cooker")
    {
        RuntimeUiPinningService.UpdateTarget(
            RuntimeNightBusinessLifecycle.Generation,
            nextEnabled,
            nextHighlightEnabled,
            nextRecipeId,
            nextBeverageId,
            nextIngredientIds ?? ingredientIds,
            recipeName,
            beverageName,
            nextCookerTypeId,
            cookerName);
    }

    void AssertPublishes(Action publish, string fieldName)
    {
        var logCount = ManualLogSource.InformationCount;
        var highlightUpdateCount = RuntimeCookerHighlightService.UpdateCount;
        publish();
        AssertEqual(logCount + 1, ManualLogSource.InformationCount, $"Changing {fieldName} did not publish a target log.");
        AssertEqual(highlightUpdateCount + 1, RuntimeCookerHighlightService.UpdateCount, $"Changing {fieldName} did not update cooker highlighting.");
        Publish();
    }

    Publish();
    var targetGeneration = RuntimeUiPinningService.ReadPinningTarget().Generation;
    var initialLogCount = ManualLogSource.InformationCount;
    var initialHighlightUpdateCount = RuntimeCookerHighlightService.UpdateCount;

    Publish(nextIngredientIds: new[] { 11, 29 });

    AssertEqual(targetGeneration, RuntimeUiPinningService.ReadPinningTarget().Generation, "An identical target advanced its generation.");
    AssertEqual(initialLogCount, ManualLogSource.InformationCount, "An identical target wrote another information log.");
    AssertEqual(initialHighlightUpdateCount, RuntimeCookerHighlightService.UpdateCount, "An identical target updated cooker highlighting again.");

    AssertPublishes(() => Publish(nextEnabled: false), "enabled");
    AssertPublishes(() => Publish(nextHighlightEnabled: false), "highlightEnabled");
    AssertPublishes(() => Publish(nextRecipeId: recipeId + 1), "recipeId");
    AssertPublishes(() => Publish(nextBeverageId: beverageId + 1), "beverageId");
    AssertPublishes(() => Publish(nextIngredientIds: new[] { 11, 30 }), "ingredientIds");
    AssertPublishes(() => Publish(recipeName: "recipe changed"), "recipeName");
    AssertPublishes(() => Publish(beverageName: "beverage changed"), "beverageName");
    AssertPublishes(() => Publish(nextCookerTypeId: cookerTypeId + 1), "cookerTypeId");
    AssertPublishes(() => Publish(cookerName: "cooker changed"), "cookerName");
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
}

static void VerifyScopedNativePinnedMatching()
{
    RuntimeUiPinningService.UpdateTarget(
        RuntimeNightBusinessLifecycle.Generation,
        enabled: true,
        highlightEnabled: false,
        recipeId: 34,
        beverageId: 16,
        ingredientIds: new[] { 11, 29 },
        recipeName: "recipe",
        beverageName: "beverage",
        cookerTypeId: 3,
        cookerName: "cooker");

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
    RuntimeUiPinningService.UpdateTarget(
        RuntimeNightBusinessLifecycle.Generation,
        enabled: true,
        highlightEnabled: false,
        recipeId: 35,
        beverageId: 17,
        ingredientIds: new[] { 12 },
        recipeName: "nested",
        beverageName: "nested",
        cookerTypeId: 3,
        cookerName: "cooker");
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
    RuntimeUiPinningService.UpdateTarget(
        RuntimeNightBusinessLifecycle.Generation,
        enabled: true,
        highlightEnabled: false,
        recipeId: 34,
        beverageId: 16,
        ingredientIds: new[] { 11 },
        recipeName: "counter-first",
        beverageName: "counter-first",
        cookerTypeId: 3,
        cookerName: "cooker");
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
    RuntimeUiPinningService.UpdateTarget(
        RuntimeNightBusinessLifecycle.Generation,
        enabled: true,
        highlightEnabled: false,
        recipeId: 35,
        beverageId: 17,
        ingredientIds: new[] { 12 },
        recipeName: "counter-second",
        beverageName: "counter-second",
        cookerTypeId: 3,
        cookerName: "cooker");
    AssertEqual(forcesBeforeUpdate, ReadForcedTotal("recipe"), "Target update reset the process force total.");
}

static void VerifyThreadLocalScopeIsolation()
{
    RuntimeUiPinningService.UpdateTarget(
        RuntimeNightBusinessLifecycle.Generation,
        enabled: true,
        highlightEnabled: false,
        recipeId: 34,
        beverageId: 16,
        ingredientIds: new[] { 11 },
        recipeName: "threaded",
        beverageName: "threaded",
        cookerTypeId: 3,
        cookerName: "cooker");

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
    RuntimeUiPinningService.UpdateTarget(
        RuntimeNightBusinessLifecycle.Generation,
        enabled: true,
        highlightEnabled: false,
        recipeId: 34,
        beverageId: 16,
        ingredientIds: new[] { 11 },
        recipeName: "first",
        beverageName: "first",
        cookerTypeId: 3,
        cookerName: "cooker");
    InvokePrivate("OnCookingRefreshStarted", new CookingSelectionPanelProbe());
    try
    {
        RuntimeUiPinningService.UpdateTarget(
            RuntimeNightBusinessLifecycle.Generation,
            enabled: true,
            highlightEnabled: false,
            recipeId: 35,
            beverageId: 17,
            ingredientIds: new[] { 12 },
            recipeName: "second",
            beverageName: "second",
            cookerTypeId: 3,
            cookerName: "cooker");
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
    RuntimeUiPinningService.UpdateTarget(
        RuntimeNightBusinessLifecycle.Generation,
        enabled: false,
        highlightEnabled: true,
        recipeId: 34,
        beverageId: 16,
        ingredientIds: new[] { 11 },
        recipeName: "recipe",
        beverageName: "beverage",
        cookerTypeId: 3,
        cookerName: "cooker");
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

    RuntimeUiPinningService.UpdateTarget(
        RuntimeNightBusinessLifecycle.Generation,
        enabled: true,
        highlightEnabled: false,
        recipeId: 34,
        beverageId: 16,
        ingredientIds: new[] { 11 },
        recipeName: "recipe",
        beverageName: "beverage",
        cookerTypeId: 3,
        cookerName: "cooker");
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
    RuntimeUiPinningService.UpdateTarget(
        RuntimeNightBusinessLifecycle.Generation,
        enabled: true,
        highlightEnabled: false,
        recipeId: 34,
        beverageId: 16,
        ingredientIds: new[] { 11 },
        recipeName: "harmony-recipe",
        beverageName: "harmony-beverage",
        cookerTypeId: 3,
        cookerName: "cooker");

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

    RuntimeUiPinningService.UpdateTarget(
        RuntimeNightBusinessLifecycle.Generation,
        enabled: true,
        highlightEnabled: false,
        recipeId: 34,
        beverageId: 16,
        ingredientIds: new[] { 11 },
        recipeName: "highlight-recipe",
        beverageName: "highlight-beverage",
        cookerTypeId: 3,
        cookerName: "cooker");

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
        RunOnWorkerThread(() => RuntimeUiPinningService.UpdateTarget(
            RuntimeNightBusinessLifecycle.Generation,
            enabled: true,
            highlightEnabled: false,
            recipeId: 35,
            beverageId: 17,
            ingredientIds: new[] { 12 },
            recipeName: "next-recipe",
            beverageName: "next-beverage",
            cookerTypeId: 3,
            cookerName: "cooker"));
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

        RuntimeUiPinningService.UpdateTarget(
            RuntimeNightBusinessLifecycle.Generation,
            enabled: true,
            highlightEnabled: false,
            recipeId: 35,
            beverageId: 17,
            ingredientIds: new[] { 12 },
            recipeName: "same-suspended-target",
            beverageName: "same-suspended-target",
            cookerTypeId: 3,
            cookerName: "cooker");
        AssertContains(RuntimePinnedListHighlightService.Status, "state=suspended: test scene exit", "Publishing the same target resumed a suspended scene.");
        cookingPanel.OnRecipeElementEnabled(new RecipeProbe(35), new object(), recipeButton);
        var setterCountAfterSuspendedBinding = recipeButton.image.SetterCount;
        RuntimePinnedListHighlightService.Tick();
        AssertEqual(setterCountAfterSuspendedBinding, recipeButton.image.SetterCount, "A late element callback was highlighted while the scene was suspended.");
        AssertColor(recipeBase, recipeButton.image.get_color(), "A late element callback kept a highlight while the scene was suspended.");
        AssertContains(RuntimePinnedListHighlightService.Status, "tracked=recipe:0", "A late element callback was tracked while the scene was suspended.");

        RunOnWorkerThread(() => RuntimeUiPinningService.UpdateTarget(
            RuntimeNightBusinessLifecycle.Generation,
            enabled: true,
            highlightEnabled: false,
            recipeId: 36,
            beverageId: 18,
            ingredientIds: new[] { 13 },
            recipeName: "changed-suspended-target",
            beverageName: "changed-suspended-target",
            cookerTypeId: 3,
            cookerName: "cooker"));
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
        RuntimeUiPinningService.UpdateTarget(
            RuntimeNightBusinessLifecycle.Generation,
            enabled: false,
            highlightEnabled: true,
            recipeId: 36,
            beverageId: 18,
            ingredientIds: new[] { 13 },
            recipeName: "highlight-only",
            beverageName: "highlight-only",
            cookerTypeId: 3,
            cookerName: "cooker");
        AssertEqual(setterCountBeforeDisable, recipeButton.image.SetterCount, "Disabling list pinning wrote a Unity image color off the main visual tick.");
        AssertHighlighted(recipeBase, recipeButton.image.get_color(), "Disabling list pinning touched Unity color before LateUpdate.");
        RuntimePinnedListHighlightService.Tick();
        AssertColor(recipeBase, recipeButton.image.get_color(), "Disabling list pinning did not restore the recipe color.");
        AssertTrue(RuntimeCookerHighlightService.LastEnabled, "Cooker-only highlighting was disabled alongside list highlighting.");

        RuntimeUiPinningService.UpdateTarget(
            RuntimeNightBusinessLifecycle.Generation,
            enabled: true,
            highlightEnabled: false,
            recipeId: 37,
            beverageId: 19,
            ingredientIds: new[] { 14 },
            recipeName: "setter-race",
            beverageName: "setter-race",
            cookerTypeId: 3,
            cookerName: "cooker");
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

                RuntimeUiPinningService.UpdateTarget(
                    RuntimeNightBusinessLifecycle.Generation,
                    enabled: true,
                    highlightEnabled: false,
                    recipeId: 38,
                    beverageId: 20,
                    ingredientIds: new[] { 15 },
                    recipeName: "setter-race-next",
                    beverageName: "setter-race-next",
                    cookerTypeId: 3,
                    cookerName: "cooker");
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
