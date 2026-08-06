using MystiaStewardCompanion.Save;

try
{
    VerifyExactSuccessfulTransaction();
    VerifySharedRecipeSameExtrasExecutesOnce();
    VerifySharedRecipeConflictingExtrasFailClosed();
    VerifyFreeCookSkipsInventoryDebit();
    VerifyPreconditionsFailBeforeSideEffects();
    VerifyTargetRecheckPreventsStaleDebit();
    VerifyTargetPublicationWaitsForNativeTransaction();
    VerifyDebitFailureIsNeverRetried();
    VerifyPartialAddIsNeverRetriedOrRolledBack();
    VerifyNativeRefreshFinalizationControlsReplay();
    VerifyExactRuntimeContractSource();
    Console.WriteLine("PASS: pinned recipe extras use one exact, fail-closed native transaction.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL: {ex}");
    return 1;
}

static void VerifyExactSuccessfulTransaction()
{
    var runtime = FakeRuntime.Imported(
        panelPointer: 0x101,
        recipePointer: 0x201,
        recipeId: 31,
        baseIds: new[] { 1, 2 },
        selectedIds: new[] { 1, 2 },
        multiplier: 2,
        freeCook: false);
    runtime.Inventory[7] = 4;
    runtime.Inventory[8] = -1;
    var target = Target(1, recipeId: 31, extras: new[] { 7, 8 });
    Install(runtime, target);

    var result = RuntimePinnedRecipeExtrasService.TryApply(runtime.Panel, target);

    AssertEqual(RuntimePinnedRecipeExtrasApplyResult.Applied, result, "Exact import did not apply.");
    AssertSequence(new[] { 7, 7, 8, 8 }, runtime.Debited, "Debit did not preserve one multiplier-expanded transaction.");
    AssertSequence(new[] { 1, 2, 7, 8 }, runtime.Selected, "Extras were not appended exactly once.");
    AssertEqual(2, runtime.QuantityReads, "Inventory was not preflighted per distinct ingredient.");
    AssertEqual(1, runtime.DebitCalls, "Native debit was not invoked exactly once.");
    AssertContains(RuntimePinnedRecipeExtrasService.Status, "successes=1", "Success was not diagnosed.");
}

static void VerifyFreeCookSkipsInventoryDebit()
{
    var runtime = FakeRuntime.Imported(0x102, 0x202, 32, new[] { 1 }, new[] { 1 }, 0, freeCook: true);
    var target = Target(2, recipeId: 32, extras: new[] { 9, 10 });
    Install(runtime, target);

    var result = RuntimePinnedRecipeExtrasService.TryApply(runtime.Panel, target);

    AssertEqual(RuntimePinnedRecipeExtrasApplyResult.Applied, result, "Free cook import did not apply.");
    AssertEqual(0, runtime.QuantityReads, "Free cook read inventory.");
    AssertEqual(0, runtime.DebitCalls, "Free cook debited inventory.");
    AssertSequence(new[] { 1, 9, 10 }, runtime.Selected, "Free cook did not append extras.");
}

static void VerifySharedRecipeSameExtrasExecutesOnce()
{
    var runtime = FakeRuntime.Imported(0x103, 0x203, 33, new[] { 1 }, new[] { 1 }, 1, freeCook: false);
    runtime.Inventory[7] = 2;
    runtime.Inventory[8] = 2;
    var targetSet = DualTarget(3, recipeId: 33, rareExtras: new[] { 7, 8 }, normalExtras: new[] { 7, 8 });
    Install(runtime, targetSet);

    var result = RuntimePinnedRecipeExtrasService.TryApply(runtime.Panel, targetSet);

    AssertEqual(RuntimePinnedRecipeExtrasApplyResult.Applied, result, "Equal shared recipe extras did not apply.");
    AssertEqual(1, runtime.DebitCalls, "Equal shared recipe extras performed more than one debit transaction.");
    AssertSequence(new[] { 1, 7, 8 }, runtime.Selected, "Equal shared recipe extras were appended more than once.");
}

static void VerifySharedRecipeConflictingExtrasFailClosed()
{
    var runtime = FakeRuntime.Imported(0x104, 0x204, 34, new[] { 1 }, new[] { 1 }, 1, freeCook: false);
    runtime.Inventory[7] = 2;
    runtime.Inventory[8] = 2;
    var targetSet = DualTarget(4, recipeId: 34, rareExtras: new[] { 7 }, normalExtras: new[] { 8 });
    Install(runtime, targetSet);

    var result = RuntimePinnedRecipeExtrasService.TryApply(runtime.Panel, targetSet);

    AssertEqual(RuntimePinnedRecipeExtrasApplyResult.Rejected, result, "Conflicting shared recipe extras were not rejected.");
    AssertEqual(0, runtime.QuantityReads, "Conflicting shared recipe extras inspected inventory before rejection.");
    AssertEqual(0, runtime.DebitCalls, "Conflicting shared recipe extras debited inventory.");
    AssertSequence(runtime.InitialSelected, runtime.Selected, "Conflicting shared recipe extras changed native selection.");
}

static void VerifyPreconditionsFailBeforeSideEffects()
{
    AssertRejected(
        FakeRuntime.Imported(0x110, 0x210, 40, new[] { 1 }, new[] { 2 }, 1, false),
        Target(10, 40, new[] { 7 }),
        "Non-exact base selection was accepted.");
    AssertRejected(
        FakeRuntime.Imported(0x111, 0x211, 41, new[] { 1, 2, 3, 4 }, new[] { 1, 2, 3, 4 }, 1, false),
        Target(11, 41, new[] { 7, 8 }),
        "Over-capacity selection was accepted.");
    AssertRejected(
        FakeRuntime.Imported(0x112, 0x212, 42, new[] { 1 }, new[] { 1 }, 0, false),
        Target(12, 42, new[] { 7 }),
        "Non-positive multiplier was accepted.");

    var insufficient = FakeRuntime.Imported(0x113, 0x213, 43, new[] { 1 }, new[] { 1 }, 2, false);
    insufficient.Inventory[7] = 1;
    AssertRejected(insufficient, Target(13, 43, new[] { 7 }), "Insufficient inventory was accepted.");

    var invalid = FakeRuntime.Imported(0x114, 0x214, 44, new[] { 1 }, new[] { 1 }, 1, false);
    invalid.Inventory[7] = -2;
    AssertRejected(invalid, Target(14, 44, new[] { 7 }), "Invalid negative inventory was accepted.");
}

static void VerifyTargetRecheckPreventsStaleDebit()
{
    var runtime = FakeRuntime.Imported(0x120, 0x220, 50, new[] { 1 }, new[] { 1 }, 1, false);
    runtime.Inventory[7] = 1;
    var target = Target(20, 50, new[] { 7 });
    Install(runtime, target);
    runtime.AfterQuantityRead = () => RuntimeUiPinningService.Current = Target(21, 50, new[] { 7 });

    var result = RuntimePinnedRecipeExtrasService.TryApply(runtime.Panel, target);

    AssertEqual(RuntimePinnedRecipeExtrasApplyResult.Rejected, result, "A stale target reached the native transaction.");
    AssertEqual(0, runtime.DebitCalls, "A stale target debited inventory.");
    AssertSequence(new[] { 1 }, runtime.Selected, "A stale target changed the selected list.");
}

static void VerifyTargetPublicationWaitsForNativeTransaction()
{
    var runtime = FakeRuntime.Imported(0x121, 0x221, 51, new[] { 1 }, new[] { 1 }, 1, false);
    runtime.Inventory[7] = 1;
    var target = Target(22, 51, new[] { 7 });
    var nextTarget = Target(23, 51, new[] { 7 });
    Install(runtime, target);
    using var debitEntered = new ManualResetEventSlim();
    using var targetUpdated = new ManualResetEventSlim();
    runtime.BeforeDebit = () =>
    {
        debitEntered.Set();
        AssertEqual(false, targetUpdated.Wait(100), "Target publication entered the native transaction lease.");
    };
    var publisher = Task.Run(() =>
    {
        debitEntered.Wait();
        RuntimeUiPinningService.Current = nextTarget;
        targetUpdated.Set();
    });

    var result = RuntimePinnedRecipeExtrasService.TryApply(runtime.Panel, target);
    AssertEqual(true, publisher.Wait(2_000), "Target publication did not resume after the native transaction.");

    AssertEqual(RuntimePinnedRecipeExtrasApplyResult.Applied, result, "The leased native transaction did not complete.");
    AssertEqual(true, ReferenceEquals(RuntimeUiPinningService.Current, nextTarget), "The queued target was not published afterward.");
    AssertEqual(1, runtime.DebitCalls, "The leased transaction did not debit exactly once.");
    AssertSequence(new[] { 1, 7 }, runtime.Selected, "The leased transaction did not append the original target extras.");
}

static void VerifyDebitFailureIsNeverRetried()
{
    var runtime = FakeRuntime.Imported(0x130, 0x230, 60, new[] { 1 }, new[] { 1 }, 1, false);
    runtime.Inventory[7] = 2;
    runtime.ThrowOnDebit = true;
    var target = Target(30, 60, new[] { 7 });
    Install(runtime, target);

    AssertEqual(
        RuntimePinnedRecipeExtrasApplyResult.Uncertain,
        RuntimePinnedRecipeExtrasService.TryApply(runtime.Panel, target),
        "A throwing debit was not marked uncertain.");
    runtime.ThrowOnDebit = false;
    AssertEqual(
        RuntimePinnedRecipeExtrasApplyResult.BlockedReplay,
        RuntimePinnedRecipeExtrasService.TryApply(runtime.Panel, target),
        "An uncertain debit was retried.");
    AssertEqual(1, runtime.DebitCalls, "An uncertain debit was called more than once.");
    AssertSequence(new[] { 1 }, runtime.Selected, "A throwing debit mutated the selected list.");
}

static void VerifyPartialAddIsNeverRetriedOrRolledBack()
{
    var runtime = FakeRuntime.Imported(0x140, 0x240, 70, new[] { 1 }, new[] { 1 }, 1, false);
    runtime.Inventory[7] = 2;
    runtime.Inventory[8] = 2;
    runtime.ThrowAfterAdds = 1;
    var target = Target(40, 70, new[] { 7, 8 });
    Install(runtime, target);

    AssertEqual(
        RuntimePinnedRecipeExtrasApplyResult.Uncertain,
        RuntimePinnedRecipeExtrasService.TryApply(runtime.Panel, target),
        "A partial add was not marked uncertain.");
    runtime.ThrowAfterAdds = -1;
    AssertEqual(
        RuntimePinnedRecipeExtrasApplyResult.BlockedReplay,
        RuntimePinnedRecipeExtrasService.TryApply(runtime.Panel, target),
        "A partial add was retried.");
    AssertEqual(1, runtime.DebitCalls, "A partial add repeated the debit.");
    AssertSequence(new[] { 1, 7 }, runtime.Selected, "A partial add was guessed back or continued.");
}

static void VerifyNativeRefreshFinalizationControlsReplay()
{
    var runtime = FakeRuntime.Imported(0x150, 0x250, 80, new[] { 1 }, new[] { 1 }, 1, freeCook: true);
    var target = Target(50, 80, new[] { 7 });
    Install(runtime, target);
    AssertEqual(RuntimePinnedRecipeExtrasApplyResult.Applied, RuntimePinnedRecipeExtrasService.TryApply(runtime.Panel, target), "Setup import failed.");
    RuntimePinnedRecipeExtrasService.OnRefreshFinalized(runtime.Panel, target, null);

    runtime.Selected.Clear();
    runtime.Selected.Add(1);
    AssertEqual(
        RuntimePinnedRecipeExtrasApplyResult.Applied,
        RuntimePinnedRecipeExtrasService.TryApply(runtime.Panel, target),
        "A completed native refresh blocked a later legitimate click.");
    RuntimePinnedRecipeExtrasService.OnRefreshFinalized(runtime.Panel, target, new InvalidOperationException("native refresh failed"));

    runtime.Selected.Clear();
    runtime.Selected.Add(1);
    AssertEqual(
        RuntimePinnedRecipeExtrasApplyResult.BlockedReplay,
        RuntimePinnedRecipeExtrasService.TryApply(runtime.Panel, target),
        "A failed post-modification refresh was retried.");

    var nextTarget = Target(51, 80, new[] { 7 }, targetRevision: "next-order");
    RuntimeUiPinningService.Current = nextTarget;
    AssertEqual(
        RuntimePinnedRecipeExtrasApplyResult.Applied,
        RuntimePinnedRecipeExtrasService.TryApply(runtime.Panel, nextTarget),
        "A new source-order revision did not release the old uncertain transaction.");
}

static void VerifyExactRuntimeContractSource()
{
    var sourcePath = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "../../../../../mods/bepinex/src/Save/RuntimePinnedRecipeExtrasService.cs"));
    var source = File.ReadAllText(sourcePath);
    var targetSourcePath = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "../../../../../mods/bepinex/src/Save/RuntimeUiPinningService.cs"));
    var targetSource = File.ReadAllText(targetSourcePath);

    AssertContains(source, "get_selectedIngredients", "Exact selectedIngredients getter is not locked.");
    AssertContains(source, "get_ExtraCostIng", "Exact ExtraCostIng getter is not locked.");
    AssertContains(source, "get_hasImported", "Exact hasImported getter is not locked.");
    AssertContains(source, "get_importedRecipe", "Exact importedRecipe getter is not locked.");
    AssertContains(source, "get_IsFreeCook", "Exact IsFreeCook getter is not locked.");
    AssertContains(source, "get_Ingredients", "Exact Recipe.Ingredients getter is not locked.");
    AssertContains(source, "get_Id", "Exact inherited Recipe.Id getter is not locked.");
    AssertContains(source, "GetIngredientCountById", "Exact inventory getter is not locked.");
    AssertContains(source, "IngredientOutRange", "Exact native range debit is not locked.");
    AssertContains(source, "typeof(Il2CppSystem.Collections.Generic.IEnumerable<int>)", "Exact IL2CPP IEnumerable<int> parameter is not locked.");
    AssertDoesNotContain(source, "ToString()", "Runtime code restored a text identity fallback.");
    AssertDoesNotContain(source, "GetFields(", "Runtime code restored field scanning.");
    AssertDoesNotContain(source, "GetProperties(", "Runtime code restored property scanning.");
    AssertDoesNotContain(source, "IngredientOut(", "Runtime code restored per-item debit fallback.");

    AssertContains(
        source,
        "RuntimeUiPinningService.TryExecutePinnedRecipeExtrasTransaction(",
        "The production extras service no longer enters the target-publication transaction.");
    AssertContains(
        source,
        "_runtime.DebitIngredients(ExpandForDebit(extraIngredientIds, panelState.ExtraCostMultiplier));",
        "The production native debit is no longer inside the transaction callback.");
    AssertContains(
        source,
        "_runtime.AddSelectedIngredients(panelState.SelectedIngredientList, extraIngredientIds);",
        "The production list write is no longer inside the transaction callback.");
    AssertContains(
        targetSource,
        "internal static bool TryExecutePinnedRecipeExtrasTransaction(",
        "The production target-publication transaction entry is missing.");
    AssertContains(
        targetSource,
        "lock (TargetPublicationRoot)",
        "The production transaction no longer holds the target-publication lock.");
    AssertContains(
        targetSource,
        "ReferenceEquals(currentTargetSet, capturedTargetSet)",
        "The production transaction no longer verifies the captured target instance.");
    AssertContains(
        targetSource,
        "currentExtraIngredientIds.SequenceEqual(extraIngredientIds)",
        "The production transaction no longer verifies the exact extra-ingredient sequence.");
    AssertContains(
        source,
        "RuntimeUiRecipeExtrasResolution.Conflict",
        "The production extras service no longer rejects conflicting dual-target sequences.");
    AssertContains(
        targetSource,
        "transaction();",
        "The production transaction callback is no longer invoked under the target lease.");

    var tryApplyBody = ExtractBalancedBlock(
        source,
        "public static RuntimePinnedRecipeExtrasApplyResult TryApply(",
        "production TryApply method");
    var extrasCallbackBody = ExtractBalancedBlock(
        tryApplyBody,
        "RuntimeUiPinningService.TryExecutePinnedRecipeExtrasTransaction(",
        "production extras transaction callback");
    AssertOccurrenceCount(tryApplyBody, "_runtime.DebitIngredients(", 1, "TryApply native debit count changed.");
    AssertOccurrenceCount(tryApplyBody, "_runtime.AddSelectedIngredients(", 1, "TryApply list-write count changed.");
    AssertContains(
        extrasCallbackBody,
        "_runtime.DebitIngredients(ExpandForDebit(extraIngredientIds, panelState.ExtraCostMultiplier));",
        "The production native debit left the target transaction callback.");
    AssertContains(
        extrasCallbackBody,
        "_runtime.AddSelectedIngredients(panelState.SelectedIngredientList, extraIngredientIds);",
        "The production list write left the target transaction callback.");

    var targetTransactionBody = ExtractBalancedBlock(
        targetSource,
        "internal static bool TryExecutePinnedRecipeExtrasTransaction(",
        "production target-publication transaction");
    var targetLockBody = ExtractBalancedBlock(
        targetTransactionBody,
        "lock (TargetPublicationRoot)",
        "production target-publication lock");
    AssertOccurrenceCount(targetTransactionBody, "lock (TargetPublicationRoot)", 1, "Target transaction lock count changed.");
    AssertOccurrenceCount(targetTransactionBody, "transaction();", 1, "Target transaction callback count changed.");
    foreach (var marker in new[]
             {
                 "var currentTargetSet = Volatile.Read(ref _targetSet);",
                 "var lifecycle = RuntimeNightBusinessLifecycle.Snapshot;",
                 "!lifecycle.IsActive",
                 "lifecycle.Generation != capturedTargetSet.SessionGeneration",
                 "!ReferenceEquals(currentTargetSet, capturedTargetSet)",
                 "currentTargetSet.Generation != capturedTargetSet.Generation",
                 "currentTargetSet.SessionGeneration != capturedTargetSet.SessionGeneration",
                 "currentTargetSet.ResolveRecipeExtras(",
                 "currentExtraIngredientIds.SequenceEqual(extraIngredientIds)",
                 "transaction();",
             })
    {
        AssertContains(
            targetLockBody,
            marker,
            "The production target lock no longer contains the complete target-identity transaction gate.");
    }
}

static RuntimeUiTargetSetSnapshot Target(
    long generation,
    int recipeId,
    int[] extras,
    string? targetRevision = null)
{
    return new RuntimeUiTargetSetSnapshot(
        generation,
        sessionGeneration: 9,
        new[]
        {
            CreateTarget(
                RuntimeUiTargetKind.Rare,
                generation,
                recipeId,
                extras,
                targetRevision ?? $"target-{generation}"),
        });
}

static RuntimeUiTargetSetSnapshot DualTarget(
    long generation,
    int recipeId,
    int[] rareExtras,
    int[] normalExtras)
{
    return new RuntimeUiTargetSetSnapshot(
        generation,
        sessionGeneration: 9,
        new[]
        {
            CreateTarget(RuntimeUiTargetKind.Rare, generation, recipeId, rareExtras, $"rare-{generation}"),
            CreateTarget(RuntimeUiTargetKind.Normal, generation, recipeId, normalExtras, $"normal-{generation}"),
        });
}

static RuntimeUiTargetSnapshot CreateTarget(
    RuntimeUiTargetKind kind,
    long generation,
    int recipeId,
    int[] extras,
    string revision)
{
    return new RuntimeUiTargetSnapshot(
        kind,
        kind == RuntimeUiTargetKind.Rare
            ? RuntimeTargetHighlightColor.DefaultRare
            : RuntimeTargetHighlightColor.DefaultNormal,
        listPinningEnabled: true,
        recipeVariantEnabled: true,
        cookerHighlightEnabled: false,
        seatHighlightEnabled: false,
        orderHighlightEnabled: false,
        $"{(kind == RuntimeUiTargetKind.Rare ? "R" : "N")}-{generation}",
        kind == RuntimeUiTargetKind.Normal ? $"ptr:{generation + 1:x}" : "",
        orderLifecycleSequence: generation + 1,
        deskCode: kind == RuntimeUiTargetKind.Rare ? 0 : 1,
        recipeId,
        ingredientIds: new[] { 1 },
        extraIngredientIds: extras,
        beverageId: -1,
        cookerTypeId: -1,
        targetRevision: revision);
}

static void Install(FakeRuntime runtime, RuntimeUiTargetSetSnapshot target)
{
    RuntimePinnedRecipeExtrasService.UseRuntimeForTests(runtime);
    RuntimeUiPinningService.Current = target;
}

static void AssertRejected(
    FakeRuntime runtime,
    RuntimeUiTargetSetSnapshot target,
    string message)
{
    Install(runtime, target);
    var result = RuntimePinnedRecipeExtrasService.TryApply(runtime.Panel, target);
    AssertEqual(RuntimePinnedRecipeExtrasApplyResult.Rejected, result, message);
    AssertEqual(0, runtime.DebitCalls, $"{message} Native debit ran.");
    AssertSequence(runtime.InitialSelected, runtime.Selected, $"{message} Selected ingredients changed.");
}

static void AssertContains(string value, string expected, string message)
{
    if (!value.Contains(expected, StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"{message} Expected '{expected}' in '{value}'.");
    }
}

static void AssertDoesNotContain(string value, string expected, string message)
{
    if (value.Contains(expected, StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"{message} Unexpected '{expected}'.");
    }
}

static string ExtractBalancedBlock(string source, string marker, string description)
{
    var markerIndex = source.IndexOf(marker, StringComparison.Ordinal);
    var openBrace = markerIndex < 0 ? -1 : source.IndexOf('{', markerIndex + marker.Length);
    if (markerIndex < 0 || openBrace < 0)
    {
        throw new InvalidOperationException($"Could not isolate {description}.");
    }

    var depth = 0;
    for (var index = openBrace; index < source.Length; index++)
    {
        if (source[index] == '{') depth++;
        if (source[index] != '}') continue;

        depth--;
        if (depth == 0) return source[(openBrace + 1)..index];
    }

    throw new InvalidOperationException($"Could not find the closing brace for {description}.");
}

static void AssertOccurrenceCount(string source, string marker, int expected, string message)
{
    var count = 0;
    var cursor = 0;
    while (cursor < source.Length)
    {
        var index = source.IndexOf(marker, cursor, StringComparison.Ordinal);
        if (index < 0) break;
        count++;
        cursor = index + marker.Length;
    }

    if (count != expected)
    {
        throw new InvalidOperationException($"{message} Expected {expected}, got {count} for '{marker}'.");
    }
}

static void AssertSequence<T>(IEnumerable<T> expected, IEnumerable<T> actual, string message)
{
    if (!expected.SequenceEqual(actual))
    {
        throw new InvalidOperationException($"{message} Expected [{string.Join(",", expected)}], got [{string.Join(",", actual)}].");
    }
}

static void AssertEqual<T>(T expected, T actual, string message)
    where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{message} Expected {expected}, got {actual}.");
    }
}

namespace MystiaStewardCompanion.Save
{
    internal readonly record struct RuntimeTargetHighlightColor(byte R, byte G, byte B)
    {
        public static readonly RuntimeTargetHighlightColor DefaultRare = new(0xFF, 0xDB, 0x2E);
        public static readonly RuntimeTargetHighlightColor DefaultNormal = new(0x5F, 0xAC, 0xD3);
    }

    internal readonly record struct RuntimeTargetHighlightPalette(
        RuntimeTargetHighlightColor Rare,
        RuntimeTargetHighlightColor Normal);

    internal static class RuntimeOrderTraceIdService
    {
        internal static bool TryNormalizeTargetTraceId(
            RuntimeUiTargetKind kind,
            string traceId,
            bool enabled,
            out string normalized,
            out string failure)
        {
            var prefix = kind == RuntimeUiTargetKind.Rare ? "R-" : "N-";
            if (enabled
                && traceId.StartsWith(prefix, StringComparison.Ordinal)
                && traceId.Length is >= 3 and <= 18
                && traceId.Skip(2).All(character => character is >= '0' and <= '9'))
            {
                normalized = traceId;
                failure = "";
                return true;
            }

            normalized = enabled ? "" : traceId;
            failure = "invalid typed trace";
            return !enabled;
        }
    }

    internal static class RuntimeUiPinningService
    {
        private static readonly object TargetPublicationRoot = new();
        private static RuntimeUiTargetSetSnapshot _current = RuntimeUiTargetSetSnapshot.Disabled;

        public static RuntimeUiTargetSetSnapshot Current
        {
            get
            {
                lock (TargetPublicationRoot) return _current;
            }
            set
            {
                lock (TargetPublicationRoot) _current = value;
            }
        }

        internal static RuntimeUiTargetSetSnapshot ReadTargetSet() => Current;

        internal static bool TryExecutePinnedRecipeExtrasTransaction(
            RuntimeUiTargetSetSnapshot capturedTargetSet,
            int recipeId,
            IReadOnlyList<int> extraIngredientIds,
            Action transaction)
        {
            lock (TargetPublicationRoot)
            {
                if (!ReferenceEquals(_current, capturedTargetSet)
                    || _current.Generation != capturedTargetSet.Generation
                    || _current.SessionGeneration != capturedTargetSet.SessionGeneration
                    || _current.ResolveRecipeExtras(
                        recipeId,
                        out var currentExtras) != RuntimeUiRecipeExtrasResolution.Resolved
                    || !currentExtras.SequenceEqual(extraIngredientIds))
                {
                    return false;
                }

                transaction();
                return true;
            }
        }
    }

    internal sealed class FakeRuntime : IPinnedRecipeExtrasRuntime
    {
        private FakeRuntime(PinnedRecipeExtrasPanelState state, List<int> selected)
        {
            State = state;
            Selected = selected;
            InitialSelected = selected.ToArray();
        }

        public object Panel { get; } = new();
        public PinnedRecipeExtrasPanelState State { get; }
        public List<int> Selected { get; }
        public int[] InitialSelected { get; }
        public Dictionary<int, int> Inventory { get; } = new();
        public List<int> Debited { get; } = new();
        public int QuantityReads { get; private set; }
        public int DebitCalls { get; private set; }
        public bool ThrowOnDebit { get; set; }
        public int ThrowAfterAdds { get; set; } = -1;
        public Action? AfterQuantityRead { get; set; }
        public Action? BeforeDebit { get; set; }

        public static FakeRuntime Imported(
            nint panelPointer,
            nint recipePointer,
            int recipeId,
            int[] baseIds,
            int[] selectedIds,
            int multiplier,
            bool freeCook)
        {
            var selected = selectedIds.ToList();
            return new FakeRuntime(
                new PinnedRecipeExtrasPanelState
                {
                    PanelPointer = panelPointer,
                    HasImported = true,
                    ImportedRecipePointer = recipePointer,
                    RecipeId = recipeId,
                    BaseIngredientIds = baseIds,
                    SelectedIngredientIds = selectedIds,
                    SelectedIngredientList = selected,
                    ExtraCostMultiplier = multiplier,
                    IsFreeCook = freeCook,
                },
                selected);
        }

        public bool TryReadPanel(object panel, out PinnedRecipeExtrasPanelState state, out string error)
        {
            state = new PinnedRecipeExtrasPanelState
            {
                PanelPointer = State.PanelPointer,
                HasImported = State.HasImported,
                ImportedRecipePointer = State.ImportedRecipePointer,
                RecipeId = State.RecipeId,
                BaseIngredientIds = State.BaseIngredientIds.ToArray(),
                SelectedIngredientIds = Selected.ToArray(),
                SelectedIngredientList = Selected,
                ExtraCostMultiplier = State.ExtraCostMultiplier,
                IsFreeCook = State.IsFreeCook,
            };
            error = "";
            return ReferenceEquals(panel, Panel);
        }

        public nint GetPanelPointer(object panel) => ReferenceEquals(panel, Panel) ? State.PanelPointer : 0;

        public int GetIngredientQuantity(int ingredientId)
        {
            QuantityReads++;
            var value = Inventory.TryGetValue(ingredientId, out var quantity) ? quantity : 0;
            AfterQuantityRead?.Invoke();
            return value;
        }

        public void DebitIngredients(IReadOnlyList<int> ingredientIds)
        {
            DebitCalls++;
            BeforeDebit?.Invoke();
            if (ThrowOnDebit) throw new InvalidOperationException("native debit failed");
            Debited.AddRange(ingredientIds);
        }

        public void AddSelectedIngredients(object selectedIngredientList, IReadOnlyList<int> ingredientIds)
        {
            if (!ReferenceEquals(selectedIngredientList, Selected)) throw new InvalidOperationException("wrong selected list");
            var added = 0;
            foreach (var id in ingredientIds)
            {
                Selected.Add(id);
                added++;
                if (ThrowAfterAdds == added) throw new InvalidOperationException("native list add failed");
            }
        }
    }
}
