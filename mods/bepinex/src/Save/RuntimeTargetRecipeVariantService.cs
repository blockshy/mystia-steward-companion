using System.Security.Cryptography;
using System.Text;
using System.Reflection;

using BepInEx.Logging;
using HarmonyLib;

namespace MystiaStewardCompanion.Save;

internal readonly record struct TargetRecipeVariantRowLease(
    nint PanelPointer,
    long PanelEpoch,
    nint RecipePointer,
    nint ButtonPointer,
    long TargetGeneration,
    string PlanIdentity);

internal sealed record TargetRecipeSnapshot(
    object Recipe,
    nint RecipePointer,
    int RecipeId,
    IReadOnlyList<int> IngredientIds,
    int CookCount);

internal sealed record TargetRecipeDescriptor(
    int Index,
    object Recipe,
    nint RecipePointer,
    int RecipeId,
    IReadOnlyList<int> IngredientIds,
    int CookCount);

internal sealed record TargetRecipePanelCookingState(
    nint PanelPointer,
    nint ImportedRecipePointer,
    int ImportedRecipeId,
    IReadOnlyList<int> ImportedIngredientIds,
    object SelectedIngredientList,
    IReadOnlyList<int> SelectedIngredientIds,
    int ExtraCostIngredient,
    bool IsFreeCook);

internal sealed record TargetRecipePanelSelectionState(
    nint PanelPointer,
    object SelectedIngredientList,
    IReadOnlyList<int> SelectedIngredientIds,
    int ExtraCostIngredient,
    bool IsFreeCook);

internal sealed record TargetRecipeMatchedComboSnapshot(
    nint RecipePointer,
    int RecipeId,
    IReadOnlyList<int> OrderedModifierIngredientIds);

internal sealed record TargetRecipeSelectedVisualState(
    nint IngredientListPointer,
    IReadOnlyList<int> OrderedIngredientIds);

internal readonly record struct TargetRecipeOutputClosureBindingSnapshot(
    nint ClosurePointer,
    nint PanelPointer,
    nint ComboPointer,
    nint OutputPointer);

internal readonly record struct TargetRecipeOutputClosureState(
    nint ClosurePointer,
    nint PanelPointer,
    nint ComboPointer,
    nint OutputPointer);

internal interface ITargetRecipeVariantRuntime
{
    nint GetNativePointer(object instance);

    bool TryWrapPanel(
        nint panelPointer,
        out object panel,
        out string error);

    bool TryWrapMatchedCombo(
        nint comboPointer,
        out object matchedCombo,
        out string error);

    bool TryReadRecipeList(
        object panel,
        int maximumCount,
        out object recipeList,
        out IReadOnlyList<TargetRecipeDescriptor> recipes,
        out string error);

    bool TryReadRecipeSnapshot(
        object recipe,
        out TargetRecipeSnapshot snapshot,
        out string error);

    bool TryCreateSyntheticRecipe(
        object authoritativeRecipe,
        IReadOnlyList<int> fullIngredientIds,
        int cookCount,
        out object syntheticRecipe,
        out nint syntheticPointer,
        out string error);

    bool TrySetSyntheticCookCount(
        object syntheticRecipe,
        int cookCount,
        out string error);

    void InsertRecipe(object recipeList, int index, object recipe);

    bool TryCleanSubmitCallback(object button, out string error);

    bool TryDisableButton(object button, out string error);

    bool TryReadPanelCookingState(
        object panel,
        out TargetRecipePanelCookingState state,
        out string error);

    bool TryReadPanelSelectionState(
        object panel,
        out TargetRecipePanelSelectionState state,
        out string error);

    bool TryReadSelectedVisualState(
        object panel,
        out TargetRecipeSelectedVisualState state,
        out string error);

    int GetIngredientQuantity(int ingredientId);

    void DebitIngredients(IReadOnlyList<int> expandedIngredientIds);

    void AddSelectedIngredients(
        object selectedIngredientList,
        IReadOnlyList<int> ingredientIds);

    bool TryReadMatchedCombo(
        object matchedCombo,
        out TargetRecipeMatchedComboSnapshot snapshot,
        out string error);

    bool TryReadExactOutputSubmitClosure(
        object button,
        out TargetRecipeOutputClosureBindingSnapshot snapshot,
        out string error);

    bool TryReadOutputSubmitClosureState(
        object closure,
        out TargetRecipeOutputClosureState state,
        out string error);
}

/// <summary>
/// Owns exact recipe-row variants and their one-shot ingredient transaction. Long-lived state
/// contains managed scalars only; every IL2CPP wrapper is confined to the active hook stack.
/// </summary>
internal static class RuntimeTargetRecipeVariantService
{
    private const string PanelTypeName =
        "NightScene.UI.CookingUtility.WorkSceneCookingSelectionPannel";
    private const string RecipeTypeName = "GameData.Core.Collections.Recipe";
    private const string UiElementClusterTypeName = "DEYU.UniversalUISystem.UIElementCluster";
    private const string UiButtonBaseTypeName = "DEYU.AdpUISystem.LogicalCollection.UIButtonBase";
    private const string UiButtonSimpleTypeName = "DEYU.AdpUISystem.LogicalCollection.UIButtonSimple";
    internal const string HarmonyId =
        "com.tyukki.mystia-steward-companion.runtime-target-recipe-variant";

    private const int MaximumRecipeCount = 512;
    private const int MaximumIngredientSlots = 5;
    private const int MaximumWarningLogs = 16;
    private const int MaximumTransactionLogsPerBusiness = 64;
    private const int MaximumCriticalTransactionLogsPerBusiness = 16;
    private const int MaximumActionTransactionLogsPerBusiness = 32;
    private const int MaximumSurfaceTransactionLogsPerBusiness = 16;
    private const int MaximumSafetyTransactionLogsPerBusiness = 32;

    private static readonly object StateRoot = new();
    private static readonly Dictionary<nint, PanelState> Panels = new();
    private static readonly Dictionary<nint, ButtonBinding> Buttons = new();
    private static readonly Dictionary<nint, ButtonCleanupLease> ButtonCleanupLeases = new();
    private static readonly Dictionary<nint, OutputClosureBinding> OutputClosures = new();
    private static readonly Dictionary<InsertionAttemptIdentity, string>
        UncertainInsertionAttempts = new();

    private static ITargetRecipeVariantRuntime _runtime =
        new ReflectionTargetRecipeVariantRuntime();
    private static ManualLogSource? _log;
    private static Harmony? _harmony;
    private static bool _attachAttempted;
    private static bool _injectionArmed;
    private static long _nextPanelEpoch;
    private static long _nextTransactionSequence;
    private static long _nextSelectionIntentSequence;
    private static long _nextSwitchAttemptSequence;
    private static long _nextButtonBindingSequence;
    private static long _nextButtonCleanupSequence;
    private static long _injectedRows;
    private static long _completedTransactions;
    private static long _cancelledTransactions;
    private static long _rejectedTransactions;
    private static long _uncertainTransactions;
    private static long _blockedSubmissions;
    private static long _failures;
    private static int _warningLogs;
    private static long _warningLogBusinessGeneration;
    private static int _transactionLogs;
    private static int _criticalTransactionLogs;
    private static int _actionTransactionLogs;
    private static int _surfaceTransactionLogs;
    private static int _safetyTransactionLogs;
    private static long _logBusinessGeneration;
    private static long _mutationLatchBusinessGeneration;
    private static bool _businessMutationUncertain;
    private static string _businessMutationUncertainReason = "";
    private static string _hookStatus = "not attached";
    private static string _lastResult = "not used";

    [ThreadStatic]
    private static ActiveSubmitContext? _activeSubmit;

    [ThreadStatic]
    private static ActiveOutputClosureContext? _activeOutputClosure;

    [ThreadStatic]
    private static List<string>? _deferredTransactionLogs;

    [ThreadStatic]
    private static int _updateAllVisualDepth;

    [ThreadStatic]
    private static FullVisualRefreshScope? _activeFullVisualRefreshScope;

    internal static bool IsAttached => _harmony != null;

    public static string Status
    {
        get
        {
            lock (StateRoot)
            {
                return $"hooks={_hookStatus}; armed={_injectionArmed}; panels={Panels.Count}; "
                    + $"buttons={Buttons.Count}; closures={OutputClosures.Count}; injected={_injectedRows}; "
                    + $"completed={_completedTransactions}; cancelled={_cancelledTransactions}; "
                    + $"rejected={_rejectedTransactions}; "
                    + $"uncertain={_uncertainTransactions}; blocked={_blockedSubmissions}; "
                    + $"failures={_failures}; warningLogs={_warningLogs}/{MaximumWarningLogs}; "
                    + $"transactionLogs={_transactionLogs}/{MaximumTransactionLogsPerBusiness}:"
                    + $"critical={_criticalTransactionLogs},action={_actionTransactionLogs},"
                    + $"surface={_surfaceTransactionLogs},safety={_safetyTransactionLogs}/"
                    + $"{MaximumSafetyTransactionLogsPerBusiness}; "
                    + $"mutationLatch={_mutationLatchBusinessGeneration}:"
                    + $"{_businessMutationUncertain}; insertionLedger={UncertainInsertionAttempts.Count}; "
                    + $"last={_lastResult}";
            }
        }
    }

    internal static void UseRuntimeForTests(ITargetRecipeVariantRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        lock (StateRoot)
        {
            _runtime = runtime;
            _log = null;
            _injectionArmed = true;
            _nextPanelEpoch = 0;
            _injectedRows = 0;
            _completedTransactions = 0;
            _cancelledTransactions = 0;
            _rejectedTransactions = 0;
            _uncertainTransactions = 0;
            _blockedSubmissions = 0;
            _failures = 0;
            _warningLogs = 0;
            _warningLogBusinessGeneration = 0;
            _transactionLogs = 0;
            _criticalTransactionLogs = 0;
            _actionTransactionLogs = 0;
            _surfaceTransactionLogs = 0;
            _safetyTransactionLogs = 0;
            _logBusinessGeneration = 0;
            _mutationLatchBusinessGeneration = 0;
            _businessMutationUncertain = false;
            _businessMutationUncertainReason = "";
            _hookStatus = "test runtime";
            _lastResult = "test runtime installed";
            Panels.Clear();
            Buttons.Clear();
            ButtonCleanupLeases.Clear();
            OutputClosures.Clear();
            UncertainInsertionAttempts.Clear();
            _activeSubmit = null;
            _activeOutputClosure = null;
            _deferredTransactionLogs = null;
            _updateAllVisualDepth = 0;
            _activeFullVisualRefreshScope = null;
        }
    }

    internal static IReadOnlyList<TargetRecipeVariantPlan> BuildPlansForTests(
        RuntimeUiTargetSetSnapshot targetSet) => BuildPlans(targetSet);

    internal static int CalculateCookCountForTests(
        IReadOnlyList<int> fullIngredientIds,
        IReadOnlyList<int> selectedIngredientIds,
        Func<int, int> getQuantity)
    {
        return CalculateCookCount(fullIngredientIds, selectedIngredientIds, getQuantity);
    }

    internal static bool InjectForTests(
        object panel,
        RuntimeUiTargetSetSnapshot targetSet,
        long businessGeneration,
        RecipeSurfaceRefreshKind refreshKind = RecipeSurfaceRefreshKind.DirectRecipeField)
    {
        return TryInject(panel, targetSet, businessGeneration, refreshKind);
    }

    private static IReadOnlyList<TargetRecipeVariantPlan> BuildPlans(
        RuntimeUiTargetSetSnapshot targetSet)
    {
        ArgumentNullException.ThrowIfNull(targetSet);
        var plans = new List<TargetRecipeVariantPlan>();
        foreach (var target in targetSet.Targets.OrderBy(target => target.Kind))
        {
            if (!target.ListPinningEnabled
                || !target.RecipeVariantEnabled
                || target.RecipeId < 0
                || target.ExtraIngredientIds.Count == 0)
            {
                continue;
            }

            var existing = plans.FirstOrDefault(plan =>
                plan.RecipeId == target.RecipeId
                && plan.ExtraIngredientIds.SequenceEqual(target.ExtraIngredientIds));
            if (existing == null)
            {
                plans.Add(new TargetRecipeVariantPlan(
                    target.RecipeId,
                    target.IngredientIds,
                    target.ExtraIngredientIds,
                    target.Kind,
                    target.TargetRevision));
            }
            else
            {
                existing.AddClaim(
                    target.Kind,
                    target.TargetRevision,
                    target.IngredientIds);
            }
        }

        return plans;
    }

    private static int CalculateCookCount(
        IReadOnlyList<int> fullIngredientIds,
        IReadOnlyList<int> selectedIngredientIds,
        Func<int, int> getQuantity)
    {
        ArgumentNullException.ThrowIfNull(fullIngredientIds);
        ArgumentNullException.ThrowIfNull(selectedIngredientIds);
        ArgumentNullException.ThrowIfNull(getQuantity);
        if (fullIngredientIds.Count == 0)
        {
            throw new InvalidOperationException("a recipe must contain at least one ingredient");
        }
        if (fullIngredientIds.Any(id => id < 0) || selectedIngredientIds.Any(id => id < 0))
        {
            throw new InvalidOperationException("recipe and selected ingredient ids must be non-negative");
        }

        var selectedCounts = selectedIngredientIds
            .GroupBy(id => id)
            .ToDictionary(group => group.Key, group => group.Count());
        var minimum = int.MaxValue;
        var hasFiniteIngredient = false;
        foreach (var group in fullIngredientIds.GroupBy(id => id))
        {
            var quantity = getQuantity(group.Key);
            if (quantity < -1)
            {
                throw new InvalidOperationException(
                    $"ingredient {group.Key} returned invalid quantity {quantity}");
            }
            if (quantity == -1) continue;

            hasFiniteIngredient = true;
            selectedCounts.TryGetValue(group.Key, out var selectedCount);
            var projectedQuantity = checked(quantity + selectedCount);
            minimum = Math.Min(minimum, projectedQuantity / group.Count());
        }

        return hasFiniteIngredient ? minimum : -1;
    }

    private static int CalculateSyntheticCookCount(
        TargetRecipePanelSelectionState state,
        IReadOnlyList<int> fullIngredientIds)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(fullIngredientIds);
        if (state.IsFreeCook)
        {
            return -1;
        }
        if (state.ExtraCostIngredient <= 0)
        {
            throw new InvalidOperationException(
                "extra ingredient cost multiplier is not positive");
        }

        var required = ExpandIngredients(
            fullIngredientIds,
            state.ExtraCostIngredient);
        var returned = ExpandIngredients(
            state.SelectedIngredientIds,
            state.ExtraCostIngredient);
        return CalculateCookCount(required, returned, _runtime.GetIngredientQuantity);
    }

    private static bool TryInject(
        object panel,
        RuntimeUiTargetSetSnapshot targetSet,
        long businessGeneration,
        RecipeSurfaceRefreshKind refreshKind)
    {
        try
        {
            return TryInjectCore(panel, targetSet, businessGeneration, refreshKind);
        }
        catch (Exception ex)
        {
            return FailPanelSurfaceRefresh(
                TryGetNativePointer(panel),
                $"recipe variant injection probe threw: {DescribeException(ex)}");
        }
    }

    private static bool TryInjectCore(
        object panel,
        RuntimeUiTargetSetSnapshot targetSet,
        long businessGeneration,
        RecipeSurfaceRefreshKind refreshKind)
    {
        ArgumentNullException.ThrowIfNull(panel);
        ArgumentNullException.ThrowIfNull(targetSet);

        var panelPointer = TryGetNativePointer(panel);
        if (panelPointer == 0) return Fail("recipe panel has no native identity");

        PanelTransaction? fullVisualResetTransaction = null;
        var outputReset = default(OutputBindingResetReceipt);
        var outputBindingReset = false;
        var outputResetLogged = false;
        if (refreshKind == RecipeSurfaceRefreshKind.FullVisual)
        {
            var outputResetError = "";
            var outerOutputResetConsumed = false;
            lock (StateRoot)
            {
                outerOutputResetConsumed = TryConsumeOuterFullVisualOutputResetLocked(
                    panelPointer,
                    businessGeneration,
                    out fullVisualResetTransaction,
                    out outputReset);
                outputBindingReset = outerOutputResetConsumed
                    || TryResetOutputForFullVisualLocked(
                        panelPointer,
                        businessGeneration,
                        out fullVisualResetTransaction,
                        out outputReset,
                        out outputResetError);
            }
            if (outputResetError.Length != 0)
            {
                return FailPanelSurfaceRefresh(panelPointer, outputResetError);
            }
            if (outputBindingReset)
            {
                outputResetLogged = outerOutputResetConsumed;
                if (!outputResetLogged)
                {
                    LogOutputBindingReset(businessGeneration, panelPointer, outputReset);
                    outputResetLogged = true;
                }
            }
        }

        IReadOnlyList<TargetRecipeVariantPlan> sourcePlans;
        try
        {
            sourcePlans = BuildPlans(targetSet);
        }
        catch (Exception ex)
        {
            return FailPanelSurfaceRefresh(
                panelPointer,
                $"recipe variant target plans are inconsistent: {DescribeException(ex)}");
        }
        if (businessGeneration <= 0
            || targetSet.Generation <= 0
            || targetSet.SessionGeneration != businessGeneration)
        {
            return FailPanelSurfaceRefresh(
                panelPointer,
                "recipe variant target generation is invalid");
        }
        var generationError = "";
        var insertionReplayReason = "";
        lock (StateRoot)
        {
            if (!TryEnterBusinessGenerationLocked(
                    businessGeneration,
                    out generationError))
            {
                // Report after releasing StateRoot.
            }
            else
            {
                UncertainInsertionAttempts.TryGetValue(
                    new InsertionAttemptIdentity(
                        businessGeneration,
                        targetSet.Generation),
                    out insertionReplayReason);
            }
        }
        if (generationError.Length != 0)
        {
            return FailPanelSurfaceRefresh(panelPointer, generationError);
        }
        if (!string.IsNullOrEmpty(insertionReplayReason))
        {
            return FailPanelSurfaceRefresh(
                panelPointer,
                $"blocked uncertain recipe-list insertion replay: {insertionReplayReason}");
        }

        var unsafeProbeReason = "";
        lock (StateRoot)
        {
            TryRejectUnsafeSurfaceProbeLocked(
                panelPointer,
                targetSet,
                businessGeneration,
                out unsafeProbeReason);
        }
        if (unsafeProbeReason.Length != 0)
        {
            return FailPanelSurfaceRefresh(panelPointer, unsafeProbeReason);
        }

        if (!_runtime.TryReadRecipeList(
                panel,
                MaximumRecipeCount,
                out var recipeList,
                out var recipes,
                out var listError))
        {
            return FailPanelSurfaceRefresh(panelPointer, $"recipe list read failed: {listError}");
        }
        var recipeListPointer = TryGetNativePointer(recipeList);
        if (recipeListPointer == 0)
        {
            return FailPanelSurfaceRefresh(
                panelPointer,
                "recipe list has no exact native identity");
        }
        var stableMutationNeedsReceipt = false;
        lock (StateRoot)
        {
            stableMutationNeedsReceipt = Panels.TryGetValue(panelPointer, out var receiptPanel)
                && receiptPanel.BusinessGeneration == businessGeneration
                && receiptPanel.Transaction is { State:
                    TransactionState.Applied or TransactionState.OutputReady };
        }
        var hasApplicableTargetRecipe = sourcePlans.Any(plan =>
            recipes.Any(recipe => recipe.RecipeId == plan.RecipeId));
        TargetRecipePanelSelectionState? selectionState = null;
        var selectionListPointer = (nint)0;
        if ((stableMutationNeedsReceipt || hasApplicableTargetRecipe)
            && (!_runtime.TryReadPanelSelectionState(
                    panel,
                    out selectionState,
                    out var stateError)
                || selectionState.PanelPointer != panelPointer))
        {
            return FailPanelSurfaceRefresh(
                panelPointer,
                $"recipe panel state read failed: {stateError}");
        }
        if (selectionState != null)
        {
            selectionListPointer = TryGetNativePointer(selectionState.SelectedIngredientList);
        }

        PanelTransaction? transferredTransaction = null;
        RecipeSelectionIntent? transferredSelectionIntent = null;
        RecipeSwitchAttempt? transferredSwitchAttempt = null;
        PanelState? previous;
        var mutationUncertain = false;
        var mutationUncertainReason = "";
        var unsafeMutationTransitionReason = "";
        var insertionUncertain = false;
        var insertionUncertainReason = "";
        var transferredFromEpoch = 0L;
        var transferredFromState = TransactionState.PendingRecipeSubmit;
        var transferredAcrossTarget = false;
        var preservedOutputBinding = false;
        lock (StateRoot)
        {
            Panels.TryGetValue(panelPointer, out previous);
            var previousMatchesBusiness = previous != null
                && previous.BusinessGeneration == businessGeneration;
            var previousSurfaceIsLive = previousMatchesBusiness && !previous!.Retired;
            var sameTargetSet = previousSurfaceIsLive
                && ReferenceEquals(previous!.TargetSet, targetSet);
            if (sameTargetSet)
            {
                transferredSwitchAttempt = previous!.SwitchAttempt is { State:
                    RecipeSwitchAttemptState.Armed
                    or RecipeSwitchAttemptState.ReceiptObserved
                    or RecipeSwitchAttemptState.VisualCompleted }
                    ? previous.SwitchAttempt
                    : null;
                transferredSelectionIntent = transferredSwitchAttempt?.Destination;
            }
            mutationUncertain = _businessMutationUncertain;
            mutationUncertainReason = _businessMutationUncertainReason;
            if (previousMatchesBusiness
                && previous!.Transaction is { } candidate
                && candidate.BusinessGeneration == businessGeneration
                && !IsTerminalTransactionState(candidate.State))
            {
                transferredFromEpoch = candidate.PanelEpoch;
                transferredFromState = ReferenceEquals(candidate, fullVisualResetTransaction)
                    ? TransactionState.OutputReady
                    : candidate.State;
                transferredAcrossTarget = !sameTargetSet;
                if (TryRejectUnsafeSurfaceProbeLocked(
                        panelPointer,
                        targetSet,
                        businessGeneration,
                        out unsafeMutationTransitionReason))
                {
                    // Runtime reads happen outside StateRoot. Re-run the same transition gate
                    // before transferring the exact transaction so a nested state change cannot
                    // bypass the pre-probe classification.
                }
                else if (candidate.State == TransactionState.Uncertain)
                {
                    transferredTransaction = candidate;
                }
                else if (candidate.State is TransactionState.Applied
                    or TransactionState.OutputReady)
                {
                    if (!TryValidateMutationTransferReceipt(
                            candidate,
                            selectionState!,
                            selectionListPointer,
                            recipes,
                            out var receiptError))
                    {
                        unsafeMutationTransitionReason =
                            $"recipe surface refresh lost its exact mutation receipt: {receiptError}";
                        TryLogTransaction(
                            businessGeneration,
                            $"mutation-receipt-drift panel={FormatPointer(panelPointer)} "
                            + $"transaction={candidate.Sequence} reason={receiptError}",
                            TransactionLogKind.Safety);
                        MarkTransactionUncertainLocked(
                            candidate,
                            unsafeMutationTransitionReason);
                    }
                    else
                    {
                        if (candidate.State == TransactionState.OutputReady)
                        {
                            var outputBindingIsStable = candidate.OutputClosurePointer != 0
                                && OutputClosures.TryGetValue(
                                    candidate.OutputClosurePointer,
                                    out var outputClosure)
                                && TryValidateOutputClosureLocked(
                                    outputClosure,
                                    TransactionState.OutputReady,
                                    out var outputPanel,
                                    out var outputTransaction)
                                && ReferenceEquals(outputPanel, previous)
                                && ReferenceEquals(outputTransaction, candidate)
                                && _activeOutputClosure == null
                                && !(_activeSubmit?.Kind == SubmitKind.Output
                                    && _activeSubmit.PanelPointer == panelPointer
                                    && string.Equals(
                                        _activeSubmit.TransactionIdentity,
                                        candidate.Identity,
                                        StringComparison.Ordinal));
                            if (!outputBindingIsStable
                                || !TryGetOutputButtonForTransactionLocked(
                                    candidate,
                                    out var stableOutputButton))
                            {
                                unsafeMutationTransitionReason =
                                    "recipe surface refresh found an unstable exact output binding";
                                MarkTransactionUncertainLocked(
                                    candidate,
                                    unsafeMutationTransitionReason);
                            }
                            else if (refreshKind == RecipeSurfaceRefreshKind.FullVisual)
                            {
                                if (!ReferenceEquals(
                                        candidate,
                                        fullVisualResetTransaction))
                                {
                                    outputResetLogged = false;
                                }
                                outputBindingReset = true;
                                fullVisualResetTransaction = candidate;
                                outputReset = CaptureOutputBindingResetReceipt(
                                    candidate,
                                    stableOutputButton);
                                ResetReadyOutputBindingLocked(candidate);
                            }
                            else
                            {
                                preservedOutputBinding = true;
                            }
                        }
                        if (unsafeMutationTransitionReason.Length == 0)
                        {
                            transferredTransaction = candidate;
                        }
                    }
                }
                else if (candidate.MutationStarted)
                {
                    unsafeMutationTransitionReason =
                        $"recipe surface refreshed while the transaction was {candidate.State}";
                    MarkTransactionUncertainLocked(
                        candidate,
                        unsafeMutationTransitionReason);
                }
            }
            TombstonePanelRecipeButtonsLocked(panelPointer);
        }
        if (unsafeMutationTransitionReason.Length != 0)
        {
            return FailPanelSurfaceRefresh(
                panelPointer,
                unsafeMutationTransitionReason);
        }
        var controlled = new List<ControlledRecipe>();
        var pendingInsertions = new List<PendingRecipeInsertion>();
        var complete = true;
        var failure = "";
        var nativeInsertStarted = false;
        try
        {
            foreach (var group in sourcePlans.GroupBy(plan => plan.RecipeId).OrderBy(group => group.Key))
            {
                var authoritativeMatches = recipes.Where(recipe => recipe.RecipeId == group.Key).ToArray();
                if (authoritativeMatches.Length == 0)
                {
                    TryLogTransaction(
                        businessGeneration,
                        $"surface-nonapplicable panel={FormatPointer(panelPointer)} "
                        + $"targetGen={targetSet.Generation} recipe={group.Key}",
                        TransactionLogKind.Surface);
                    continue;
                }
                if (authoritativeMatches.Length > 1)
                {
                    complete = false;
                    failure = $"recipe {group.Key} has {authoritativeMatches.Length} authoritative rows";
                    break;
                }

                var authoritative = authoritativeMatches[0];
                if (authoritative.RecipePointer == 0
                    || authoritative.IngredientIds.Count == 0
                    || authoritative.IngredientIds.Any(id => id < 0))
                {
                    complete = false;
                    failure = $"recipe {group.Key} authoritative snapshot is invalid";
                    break;
                }

                var baseIdentity = $"base:{group.Key}:"
                    + TargetRecipeVariantPlan.BuildFingerprint(new[]
                    {
                        string.Join(",", authoritative.IngredientIds),
                        targetSet.Generation.ToString(),
                    });
                var runtimePlans = group.Select(plan => plan.WithBaseIngredients(
                    authoritative.IngredientIds)).ToArray();
                var owner = new ControlledRecipe(
                    group.Key,
                    authoritative.RecipePointer,
                    authoritative.IngredientIds,
                    targetSet.GetBaseRecipeClaims(group.Key),
                    baseIdentity);
                controlled.Add(owner);

                var planIndex = 0;
                foreach (var plan in runtimePlans)
                {
                    var fullIngredients = plan.BaseIngredientIds.Concat(plan.ExtraIngredientIds).ToArray();
                    if (fullIngredients.Length > MaximumIngredientSlots)
                    {
                        complete = false;
                        failure = $"recipe {group.Key} variant exceeds the five-slot limit";
                        break;
                    }

                    var cookCount = CalculateSyntheticCookCount(
                        selectionState!,
                        fullIngredients);
                    if (!_runtime.TryCreateSyntheticRecipe(
                            authoritative.Recipe,
                            fullIngredients,
                            cookCount,
                            out var syntheticRecipe,
                            out var syntheticPointer,
                            out var createError)
                        || syntheticPointer == 0
                        || syntheticPointer == authoritative.RecipePointer
                        || owner.SyntheticPlans.ContainsKey(syntheticPointer))
                    {
                        complete = false;
                        failure = $"recipe {group.Key} synthetic creation failed: {createError}";
                        break;
                    }

                    owner.SyntheticPlans.Add(syntheticPointer, plan);
                    pendingInsertions.Add(new PendingRecipeInsertion(
                        authoritative.Index,
                        planIndex,
                        syntheticRecipe,
                        owner,
                        syntheticPointer,
                        fullIngredients,
                        cookCount));
                    planIndex++;
                }
                if (!complete) break;
            }

            if (complete)
            {
                var orderedInsertions = pendingInsertions
                    .OrderByDescending(item => item.BaseIndex)
                    .ThenByDescending(item => item.PlanIndex)
                    .ToArray();
                if (recipes.Count + orderedInsertions.Length > MaximumRecipeCount)
                {
                    complete = false;
                    failure = "recipe variants would exceed the maximum recipe-list size";
                }
                else
                {
                    nativeInsertStarted = orderedInsertions.Length > 0;
                    foreach (var insertion in orderedInsertions)
                    {
                        _runtime.InsertRecipe(recipeList, insertion.BaseIndex + 1, insertion.Recipe);
                    }
                }
                if (complete
                    && nativeInsertStarted
                    && !TryValidateInsertedRecipeList(
                        panel,
                        recipeListPointer,
                        recipes,
                        orderedInsertions,
                        out var readbackError))
                {
                    complete = false;
                    insertionUncertain = true;
                    failure = $"recipe variant insertion readback is uncertain: {readbackError}";
                    insertionUncertainReason = failure;
                }
            }
        }
        catch (Exception ex)
        {
            complete = false;
            failure = $"recipe variant insertion result is uncertain: {DescribeException(ex)}";
            insertionUncertain = nativeInsertStarted;
            if (insertionUncertain) insertionUncertainReason = failure;
        }

        if (!ReferenceEquals(RuntimeUiPinningService.ReadTargetSet(), targetSet))
        {
            complete = false;
            failure = "target changed during recipe variant insertion";
            if (nativeInsertStarted)
            {
                insertionUncertain = true;
                insertionUncertainReason = failure;
            }
        }

        var epoch = Interlocked.Increment(ref _nextPanelEpoch);
        var installationConflict = false;
        lock (StateRoot)
        {
            var previousStillCurrent = previous == null
                ? !Panels.ContainsKey(panelPointer)
                : Panels.TryGetValue(panelPointer, out var currentPrevious)
                    && ReferenceEquals(currentPrevious, previous);
            var expectedTransferredState = outputBindingReset
                ? TransactionState.Applied
                : transferredFromState;
            var transactionStillCurrent = transferredTransaction == null
                || previousStillCurrent
                    && !previous!.Retired
                    && ReferenceEquals(previous!.Transaction, transferredTransaction)
                    && transferredTransaction.State == expectedTransferredState;
            if (!previousStillCurrent || !transactionStillCurrent)
            {
                installationConflict = true;
                complete = false;
                failure = "recipe variant panel changed during native list insertion";
                if (nativeInsertStarted)
                {
                    insertionUncertain = true;
                    insertionUncertainReason = failure;
                }
                TombstonePanelRecipeButtonsLocked(panelPointer);
            }
            else if (transferredTransaction != null)
            {
                transferredTransaction.PanelEpoch = epoch;
                if (_activeSubmit?.Kind == SubmitKind.Recipe
                    && _activeSubmit.PanelPointer == panelPointer
                    && string.Equals(
                        _activeSubmit.TransactionIdentity,
                        transferredTransaction.Identity,
                        StringComparison.Ordinal))
                {
                    _activeSubmit.PanelEpoch = epoch;
                }
            }
            if (transferredSwitchAttempt != null)
            {
                transferredSwitchAttempt.PanelEpoch = epoch;
                transferredSwitchAttempt.Destination.PanelEpoch = epoch;
                if (_activeSubmit?.SwitchAttemptSequence == transferredSwitchAttempt.Sequence
                    && string.Equals(
                        _activeSubmit.SwitchAttemptIdentity,
                        transferredSwitchAttempt.Identity,
                        StringComparison.Ordinal))
                {
                    _activeSubmit.PanelEpoch = epoch;
                }
            }
            if (transferredSelectionIntent != null)
            {
                transferredSelectionIntent.PanelEpoch = epoch;
            }
            if (insertionUncertain)
            {
                UncertainInsertionAttempts[
                    new InsertionAttemptIdentity(
                        businessGeneration,
                        targetSet.Generation)] = insertionUncertainReason;
            }
            if (!complete
                && outputReset.ButtonPointer != 0
                && Buttons.TryGetValue(
                    outputReset.ButtonPointer,
                    out var failedRebind)
                && failedRebind.Sequence == outputReset.ButtonBindingSequence
                && failedRebind.State == BindingState.AwaitingRebind)
            {
                failedRebind.State = BindingState.Tombstone;
            }
            foreach (var item in controlled) item.Complete = complete;
            if (!installationConflict)
            {
                Panels[panelPointer] = new PanelState(
                    panelPointer,
                    epoch,
                    businessGeneration,
                    targetSet,
                    recipeListPointer,
                    controlled,
                    transferredTransaction,
                    transferredSelectionIntent,
                    transferredSwitchAttempt,
                    mutationUncertain,
                    mutationUncertainReason);
                if (complete)
                {
                    _injectedRows += pendingInsertions.Count;
                    _lastResult = $"injected {pendingInsertions.Count} exact recipe variant rows";
                }
                else
                {
                    _failures++;
                    _lastResult = failure;
                }
            }
        }

        if (installationConflict) return FailPanelSurfaceRefresh(panelPointer, failure);
        if (!complete)
        {
            TryLogTransaction(
                businessGeneration,
                $"surface-refresh-failed panel={FormatPointer(panelPointer)} "
                + $"targetGen={targetSet.Generation} reason={failure}",
                TransactionLogKind.Safety);
            TryLogWarning($"Target recipe variant injection failed closed: {failure}.");
        }
        var summary = string.Join(
            ";",
            controlled.Select(item =>
            {
                var variants = string.Join(
                    "|",
                    item.SyntheticPlans.Select(pair =>
                        $"{FormatPointer(pair.Key)}:{pair.Value.Claims}:"
                        + string.Join(",", pair.Value.ExtraIngredientIds)));
                return $"recipe={item.RecipeId},base={FormatPointer(item.AuthoritativePointer)},"
                    + $"baseClaims={item.BaseClaims},variants=[{variants}]";
            }));
        TryLogTransaction(
            businessGeneration,
            $"inject panel={FormatPointer(panelPointer)} epoch={epoch} targetGen={targetSet.Generation} "
            + $"complete={complete} rows={pendingInsertions.Count} {summary}");
        if (transferredTransaction != null)
        {
            var transferEvent = transferredAcrossTarget
                ? sourcePlans.Count == 0
                    ? "target-empty-transfer"
                    : "target-transfer"
                : "epoch-transfer";
            TryLogTransaction(
                businessGeneration,
                $"{transferEvent} panel={FormatPointer(panelPointer)} "
                + $"fromEpoch={transferredFromEpoch} toEpoch={epoch} "
                + $"recipe={transferredTransaction.RecipeId} "
                + $"fromState={transferredFromState} toState={transferredTransaction.State} "
                + $"targetDisposition={(transferredAcrossTarget ? "transferred" : "same")} "
                + $"outputDisposition={(outputBindingReset ? "cleared" : preservedOutputBinding ? "preserved" : "none")}",
                TransactionLogKind.Action);
        }
        if (outputBindingReset && !outputResetLogged)
        {
            LogOutputBindingReset(businessGeneration, panelPointer, outputReset, epoch);
        }
        return complete;
    }

    private static bool TryRejectUnsafeSurfaceProbeLocked(
        nint panelPointer,
        RuntimeUiTargetSetSnapshot targetSet,
        long businessGeneration,
        out string reason)
    {
        reason = "";
        if (!Panels.TryGetValue(panelPointer, out var panel)
            || panel.BusinessGeneration != businessGeneration
            || panel.Transaction is not { } transaction
            || transaction.BusinessGeneration != businessGeneration
            || IsTerminalTransactionState(transaction.State)
            || transaction.State == TransactionState.Uncertain)
        {
            return false;
        }

        var targetChanged = !ReferenceEquals(panel.TargetSet, targetSet);
        var activeSwitch = panel.SwitchAttempt is { State:
            RecipeSwitchAttemptState.Armed
            or RecipeSwitchAttemptState.ReceiptObserved
            or RecipeSwitchAttemptState.VisualCompleted } switchAttempt
            ? switchAttempt
            : null;
        var activeTargetCallback = targetChanged
            && ((_activeSubmit != null
                    && _activeSubmit.PanelPointer == panelPointer
                    && (string.Equals(
                            _activeSubmit.TransactionIdentity,
                            transaction.Identity,
                            StringComparison.Ordinal)
                        || _activeSubmit.SwitchAttemptSequence > 0))
                || (_activeOutputClosure is { } activeOutput
                    && activeOutput.Identity.PanelPointer == panelPointer
                    && activeOutput.Identity.TransactionSequence == transaction.Sequence
                    && string.Equals(
                        activeOutput.Identity.TransactionIdentity,
                        transaction.Identity,
                        StringComparison.Ordinal)));

        if (panel.Retired)
        {
            reason = "recipe surface reopened before the retained transaction reached a terminal state";
            MarkTransactionUncertainLocked(transaction, reason);
        }
        else if (targetChanged && activeSwitch != null)
        {
            reason = "target changed during an active native recipe switch";
            MarkSwitchAttemptUncertainLocked(activeSwitch, reason);
        }
        else if (activeTargetCallback)
        {
            reason = "target changed during an active native recipe or output callback";
            MarkTransactionUncertainLocked(transaction, reason);
        }
        else if (transaction.State is TransactionState.Applying
            or TransactionState.OutputPending
            or TransactionState.OutputSubmitting
            or TransactionState.Switching)
        {
            reason = $"recipe surface refreshed while the transaction was {transaction.State}";
            MarkTransactionUncertainLocked(transaction, reason);
        }
        return reason.Length != 0;
    }

    private static bool TryValidateInsertedRecipeList(
        object panel,
        nint expectedListPointer,
        IReadOnlyList<TargetRecipeDescriptor> originalRecipes,
        IReadOnlyList<PendingRecipeInsertion> orderedInsertions,
        out string error)
    {
        error = "";
        if (!_runtime.TryReadRecipeList(
                panel,
                MaximumRecipeCount,
                out var freshList,
                out var freshRecipes,
                out error))
        {
            return false;
        }
        if (TryGetNativePointer(freshList) != expectedListPointer)
        {
            error = "recipe list native identity changed after insertion";
            return false;
        }

        var expectedPointers = originalRecipes
            .Select(recipe => recipe.RecipePointer)
            .ToList();
        foreach (var insertion in orderedInsertions)
        {
            var index = insertion.BaseIndex + 1;
            if (index < 0 || index > expectedPointers.Count)
            {
                error = "recipe insertion index is outside the expected list";
                return false;
            }
            expectedPointers.Insert(index, insertion.Pointer);
        }
        if (freshRecipes.Count != expectedPointers.Count
            || !freshRecipes.Select(recipe => recipe.RecipePointer)
                .SequenceEqual(expectedPointers))
        {
            error = "recipe list count or exact pointer order changed after insertion";
            return false;
        }

        var originals = originalRecipes.ToDictionary(recipe => recipe.RecipePointer);
        var synthetic = orderedInsertions.ToDictionary(recipe => recipe.Pointer);
        foreach (var recipe in freshRecipes)
        {
            if (originals.TryGetValue(recipe.RecipePointer, out var original))
            {
                if (recipe.RecipeId != original.RecipeId
                    || recipe.CookCount != original.CookCount
                    || !recipe.IngredientIds.SequenceEqual(original.IngredientIds))
                {
                    error = $"authoritative recipe {FormatPointer(recipe.RecipePointer)} changed after insertion";
                    return false;
                }
                continue;
            }
            if (!synthetic.TryGetValue(recipe.RecipePointer, out var insertion)
                || recipe.RecipeId != insertion.Owner.RecipeId
                || recipe.CookCount != insertion.CookCount
                || !recipe.IngredientIds.SequenceEqual(insertion.FullIngredientIds))
            {
                error = $"synthetic recipe {FormatPointer(recipe.RecipePointer)} changed after insertion";
                return false;
            }
        }
        return true;
    }

    private static bool TryValidateMutationTransferReceipt(
        PanelTransaction transaction,
        TargetRecipePanelSelectionState selectionState,
        nint selectedIngredientListPointer,
        IReadOnlyList<TargetRecipeDescriptor> recipes,
        out string error)
    {
        error = "";
        if (!transaction.MutationStarted
            || !transaction.MutationReceiptConfirmed
            || transaction.SelectedIngredientListPointer == 0)
        {
            error = "the selected-ingredient mutation has no confirmed managed receipt";
            return false;
        }
        if (selectionState.PanelPointer != transaction.PanelPointer
            || selectedIngredientListPointer != transaction.SelectedIngredientListPointer
            || selectionState.IsFreeCook != transaction.IsFreeCook
            || selectionState.ExtraCostIngredient != transaction.ExtraCostMultiplier
            || !selectionState.SelectedIngredientIds.SequenceEqual(
                transaction.BaseIngredientIds.Concat(transaction.ExtraIngredientIds)))
        {
            error = "the selected-ingredient mutation receipt drifted during recipe surface refresh";
            return false;
        }
        if (!selectionState.IsFreeCook && selectionState.ExtraCostIngredient <= 0)
        {
            error = "the selected-ingredient mutation receipt has a non-positive multiplier";
            return false;
        }
        if (recipes.Count(recipe =>
                recipe.RecipePointer == transaction.AuthoritativePointer
                && recipe.RecipeId == transaction.RecipeId
                && recipe.IngredientIds.SequenceEqual(transaction.BaseIngredientIds)) != 1)
        {
            error = "the authoritative recipe receipt drifted during recipe surface refresh";
            return false;
        }
        return true;
    }

    private static bool HasMutationAwaitingNativeSwitchLocked(PanelState panel)
    {
        return panel.Transaction is { MutationStarted: true } transaction
            && transaction.State is TransactionState.Applied or TransactionState.OutputReady;
    }

    private static Exception? CompleteRecipeSelection(
        RecipeSelectionHookState hookState,
        Exception? exception)
    {
        var intent = hookState.Intent as RecipeSelectionIntent;
        if (!hookState.OriginalAllowed || intent == null || exception != null) return exception;

        ActiveSubmitContext? active = null;
        lock (StateRoot)
        {
            if (!Panels.TryGetValue(intent.PanelPointer, out var panel)
                || panel.Retired
                || panel.PanelEpoch != intent.PanelEpoch
                || panel.BusinessGeneration != intent.BusinessGeneration
                || !ReferenceEquals(panel.TargetSet, intent.TargetSet)
                || panel.TargetSet.Generation != intent.TargetGeneration
                || !TryValidateSelectionIntentLocked(panel, intent))
            {
                return exception;
            }
            panel.SelectionIntent = intent;
            if (_activeSubmit is { } candidate
                && candidate.ButtonPointer == intent.ButtonPointer
                && candidate.Kind is SubmitKind.None or SubmitKind.RecipeSwitch
                && HasMutationAwaitingNativeSwitchLocked(panel))
            {
                active = candidate;
            }
        }

        if (active != null)
        {
            var error = "";
            var armed = false;
            try { armed = TryArmRecipeSwitch(intent, active, out error); }
            catch (Exception ex) { error = DescribeException(ex); }
            if (!armed)
            {
                lock (StateRoot) _lastResult = $"recipe switch arm failed: {error}";
                TryLogTransaction(
                    intent.BusinessGeneration,
                    $"switch-rejected panel={FormatPointer(intent.PanelPointer)} "
                    + $"epoch={intent.PanelEpoch} recipe={intent.RecipeId} "
                    + $"button={FormatPointer(intent.ButtonPointer)} source=selection-finalizer "
                    + $"reason={error}");
                return new InvalidOperationException(
                    $"Recipe switch was aborted before callback execution: {error}");
            }
        }
        return exception;
    }

    private static bool TryValidateSelectionIntentLocked(
        PanelState panel,
        RecipeSelectionIntent intent)
    {
        if (intent.PanelPointer != panel.PanelPointer
            || intent.PanelEpoch != panel.PanelEpoch
            || intent.BusinessGeneration != panel.BusinessGeneration
            || intent.TargetGeneration != panel.TargetSet.Generation
            || !ReferenceEquals(intent.TargetSet, panel.TargetSet)
            || intent.ButtonPointer == 0
            || intent.SourceRowRecipePointer == 0
            || intent.AuthoritativeRecipePointer == 0
            || intent.RecipeId < 0
            || intent.BaseIngredientIds.Length == 0
            || intent.BaseIngredientIds.Length + intent.ExtraIngredientIds.Length
                > MaximumIngredientSlots)
        {
            return false;
        }

        if (intent.DestinationKind == RecipeDestinationKind.Ordinary)
        {
            return intent.ButtonBindingSequence == 0
                && intent.SourceRowRecipePointer == intent.AuthoritativeRecipePointer
                && intent.ExtraIngredientIds.Length == 0
                && !panel.ControlledRecipes.Values.Any(owner =>
                    owner.AuthoritativePointer == intent.SourceRowRecipePointer
                    || owner.SyntheticPlans.ContainsKey(intent.SourceRowRecipePointer));
        }
        if (!Buttons.TryGetValue(intent.ButtonPointer, out var binding)
            || binding.Sequence != intent.ButtonBindingSequence
            || binding.State != BindingState.Ready
            || binding.PanelPointer != panel.PanelPointer
            || binding.PanelEpoch != panel.PanelEpoch
            || binding.RecipePointer != intent.SourceRowRecipePointer
            || binding.RecipeId != intent.RecipeId
            || !TryValidateBindingLocked(binding, out var bindingPanel)
            || !ReferenceEquals(bindingPanel, panel))
        {
            return false;
        }
        if (intent.DestinationKind == RecipeDestinationKind.Base)
        {
            return binding.RowKind == RowKind.Authoritative
                && binding.RecipePointer == intent.AuthoritativeRecipePointer
                && intent.ExtraIngredientIds.Length == 0;
        }
        return binding.RowKind == RowKind.Synthetic
            && panel.ControlledRecipes.TryGetValue(intent.RecipeId, out var controlled)
            && controlled.Complete
            && controlled.AuthoritativePointer == intent.AuthoritativeRecipePointer
            && controlled.BaseIngredientIds.SequenceEqual(intent.BaseIngredientIds)
            && controlled.SyntheticPlans.TryGetValue(
                intent.SourceRowRecipePointer,
                out var plan)
            && string.Equals(plan.Identity, intent.PlanIdentity, StringComparison.Ordinal)
            && plan.ExtraIngredientIds.SequenceEqual(intent.ExtraIngredientIds);
    }

    private static bool TryArmRecipeSwitch(
        RecipeSelectionIntent intent,
        ActiveSubmitContext active,
        out string error)
    {
        error = "";
        PanelState panel;
        PanelTransaction source;
        lock (StateRoot)
        {
            if (active.SwitchAttemptSequence > 0)
            {
                if (active.SwitchAttemptToken is RecipeSwitchAttempt retained
                    && retained.Sequence == active.SwitchAttemptSequence
                    && string.Equals(
                        retained.Identity,
                        active.SwitchAttemptIdentity,
                        StringComparison.Ordinal)
                    && retained.State == RecipeSwitchAttemptState.Armed
                    && ReferenceEquals(retained.Destination, intent))
                {
                    return true;
                }
                error = "active submit already owns a different recipe switch attempt";
                return false;
            }
            if (!Panels.TryGetValue(intent.PanelPointer, out panel!)
                || panel.Retired
                || panel.PanelEpoch != intent.PanelEpoch
                || !ReferenceEquals(panel.SelectionIntent, intent)
                || !ReferenceEquals(RuntimeUiPinningService.ReadTargetSet(), panel.TargetSet)
                || !TryValidateSelectionIntentLocked(panel, intent)
                || panel.Transaction is not { } candidate
                || !candidate.MutationStarted
                || candidate.State is not (TransactionState.Applied
                    or TransactionState.OutputReady))
            {
                error = "switch source or destination identity changed";
                return false;
            }
            if (candidate.State == TransactionState.OutputReady
                && (!TryGetOutputButtonForTransactionLocked(candidate, out var output)
                    || output.State != BindingState.Ready
                    || candidate.OutputClosurePointer == 0
                    || !OutputClosures.TryGetValue(candidate.OutputClosurePointer, out var closure)
                    || !TryValidateOutputClosureLocked(
                        closure,
                        TransactionState.OutputReady,
                        out var outputPanel,
                        out var outputTransaction)
                    || !ReferenceEquals(outputPanel, panel)
                    || !ReferenceEquals(outputTransaction, candidate)))
            {
                error = "switch source output binding is not stable";
                return false;
            }
            source = candidate;
        }

        if (!_runtime.TryWrapPanel(intent.PanelPointer, out var panelWrapper, out error)
            || !_runtime.TryReadPanelSelectionState(
                panelWrapper,
                out var selection,
                out error)
            || selection.PanelPointer != intent.PanelPointer
            || TryGetNativePointer(selection.SelectedIngredientList) == 0
            || !source.MutationReceiptConfirmed
            || TryGetNativePointer(selection.SelectedIngredientList)
                != source.SelectedIngredientListPointer
            || selection.IsFreeCook != source.IsFreeCook
            || selection.ExtraCostIngredient != source.ExtraCostMultiplier
            || !selection.SelectedIngredientIds.SequenceEqual(
                source.BaseIngredientIds.Concat(source.ExtraIngredientIds)))
        {
            if (error.Length == 0) error = "switch source selection no longer matches the variant";
            return false;
        }
        if (!selection.IsFreeCook && selection.ExtraCostIngredient <= 0)
        {
            error = "switch source multiplier is not positive";
            return false;
        }
        if (!_runtime.TryReadRecipeList(
                panelWrapper,
                MaximumRecipeCount,
                out _,
                out var recipes,
                out error))
        {
            return false;
        }
        if (recipes.Count(candidate =>
                candidate.RecipePointer == intent.AuthoritativeRecipePointer
                && candidate.RecipeId == intent.RecipeId
                && candidate.IngredientIds.SequenceEqual(intent.BaseIngredientIds)) != 1)
        {
            error = "switch destination authoritative recipe is not unique";
            return false;
        }
        if (intent.DestinationKind == RecipeDestinationKind.Variant
            && recipes.Count(candidate =>
                candidate.RecipePointer == intent.SourceRowRecipePointer
                && candidate.RecipeId == intent.RecipeId
                && candidate.IngredientIds.SequenceEqual(
                    intent.BaseIngredientIds.Concat(intent.ExtraIngredientIds))) != 1)
        {
            error = "switch destination variant row is not unique";
            return false;
        }

        var inventoryBefore = new Dictionary<int, int>();
        if (!selection.IsFreeCook)
        {
            try
            {
                foreach (var ingredientId in source.BaseIngredientIds
                    .Concat(source.ExtraIngredientIds)
                    .Concat(intent.BaseIngredientIds)
                    .Distinct())
                {
                    var quantity = _runtime.GetIngredientQuantity(ingredientId);
                    if (quantity < -1)
                    {
                        throw new InvalidOperationException(
                            $"ingredient {ingredientId} returned invalid quantity {quantity}");
                    }
                    if (quantity >= 0)
                    {
                        var projected = checked(
                            quantity
                            + checked(selection.ExtraCostIngredient
                                * source.BaseIngredientIds
                                    .Concat(source.ExtraIngredientIds)
                                    .Count(id => id == ingredientId))
                            - checked(selection.ExtraCostIngredient
                                * intent.BaseIngredientIds.Count(id => id == ingredientId)));
                        if (projected < 0)
                        {
                            throw new InvalidOperationException(
                                $"ingredient {ingredientId} cannot fund the native switch destination");
                        }
                    }
                    inventoryBefore.Add(ingredientId, quantity);
                }
            }
            catch (Exception ex)
            {
                error = $"switch inventory preflight failed: {DescribeException(ex)}";
                return false;
            }
        }

        if (!RuntimeUiPinningService.TryAcquireTargetRecipeVariantPublicationLease(
                intent.TargetSet,
                out var publicationLease))
        {
            error = "target changed before switch publication lease";
            return false;
        }

        lock (StateRoot)
        {
            if (!Panels.TryGetValue(intent.PanelPointer, out var currentPanel)
                || !ReferenceEquals(currentPanel, panel)
                || !ReferenceEquals(currentPanel.SelectionIntent, intent)
                || !ReferenceEquals(currentPanel.Transaction, source)
                || source.State is not (TransactionState.Applied
                    or TransactionState.OutputReady)
                || !ReferenceEquals(_activeSubmit, active)
                || active.ButtonPointer != intent.ButtonPointer
                || active.SwitchAttemptSequence > 0
                || !TryValidateSelectionIntentLocked(currentPanel, intent))
            {
                publicationLease.Dispose();
                error = "switch token changed at publication boundary";
                return false;
            }
            var attempt = new RecipeSwitchAttempt(
                currentPanel,
                source,
                intent,
                TryGetNativePointer(selection.SelectedIngredientList),
                selection.IsFreeCook,
                selection.ExtraCostIngredient,
                inventoryBefore);
            currentPanel.SwitchAttempt = attempt;
            active.AttachSwitch(
                currentPanel.PanelPointer,
                currentPanel.PanelEpoch,
                attempt.Sequence,
                attempt.Identity,
                attempt,
                attempt.BusinessGeneration);
            active.SwitchPublicationLease = publicationLease;
            _lastResult = "armed exact native recipe switch receipt";
            TryLogTransaction(
                currentPanel.BusinessGeneration,
                $"switch-armed panel={FormatPointer(currentPanel.PanelPointer)} "
                + $"epoch={currentPanel.PanelEpoch} transaction={source.Sequence} "
                + $"destination={intent.DestinationKind}:{intent.RecipeId}@"
                + $"{FormatPointer(intent.SourceRowRecipePointer)} attempt={attempt.Sequence}",
                TransactionLogKind.Action);
        }
        return true;
    }

    private static bool TryEnterBusinessGenerationLocked(
        long businessGeneration,
        out string error)
    {
        error = "";
        if (businessGeneration <= 0)
        {
            error = "recipe variant business generation is not positive";
            return false;
        }
        if (_mutationLatchBusinessGeneration > businessGeneration)
        {
            error = "recipe variant business generation moved backwards";
            return false;
        }
        if (_mutationLatchBusinessGeneration < businessGeneration)
        {
            _mutationLatchBusinessGeneration = businessGeneration;
            _businessMutationUncertain = false;
            _businessMutationUncertainReason = "";
            UncertainInsertionAttempts.Clear();
            if (_warningLogBusinessGeneration < businessGeneration)
            {
                _warningLogBusinessGeneration = businessGeneration;
                _warningLogs = 0;
            }
        }
        return true;
    }

    private static void LatchBusinessMutationUncertainLocked(
        long businessGeneration,
        string reason)
    {
        if (businessGeneration <= 0
            || _mutationLatchBusinessGeneration > businessGeneration)
        {
            return;
        }
        if (_mutationLatchBusinessGeneration < businessGeneration)
        {
            _mutationLatchBusinessGeneration = businessGeneration;
            _businessMutationUncertain = false;
            _businessMutationUncertainReason = "";
        }
        var newlyLatched = !_businessMutationUncertain
            || !string.Equals(
                _businessMutationUncertainReason,
                reason,
                StringComparison.Ordinal);
        _businessMutationUncertain = true;
        _businessMutationUncertainReason = reason;
        if (newlyLatched)
        {
            TryLogTransaction(
                businessGeneration,
                $"mutation-uncertain-latched generation={businessGeneration} reason={reason}",
                TransactionLogKind.Safety);
        }
    }

    private static bool FailPanelSurfaceRefresh(nint panelPointer, string reason)
    {
        long businessGeneration;
        lock (StateRoot)
        {
            TombstonePanelRecipeButtonsLocked(panelPointer);
            businessGeneration = Panels.TryGetValue(panelPointer, out var panel)
                ? panel.BusinessGeneration
                : RuntimeNightBusinessLifecycle.Snapshot.Generation;
        }
        TryLogTransaction(
            businessGeneration,
            $"surface-refresh-failed panel={FormatPointer(panelPointer)} reason={reason}",
            TransactionLogKind.Safety);
        return Fail(reason);
    }

    private static void RetirePanel(nint panelPointer, string reason)
    {
        lock (StateRoot)
        {
            TombstonePanelButtonsLocked(panelPointer);
            if (Panels.TryGetValue(panelPointer, out var panel)) panel.Retired = true;
            _lastResult = reason;
        }
    }

    private static void TombstonePanelRecipeButtonsLocked(nint panelPointer)
    {
        var keys = Buttons
            .Where(pair => pair.Value.PanelPointer == panelPointer
                && pair.Value.RowKind != RowKind.Output)
            .Select(pair => pair.Key)
            .ToArray();
        foreach (var key in keys)
        {
            var binding = Buttons[key];
            if (binding.RowKind == RowKind.Authoritative)
            {
                Buttons.Remove(key);
            }
            else
            {
                binding.State = BindingState.Tombstone;
            }
        }
    }

    private static void TombstonePanelButtonsLocked(nint panelPointer)
    {
        var keys = Buttons
            .Where(pair => pair.Value.PanelPointer == panelPointer)
            .Select(pair => pair.Key)
            .ToArray();
        foreach (var key in keys)
        {
            var binding = Buttons[key];
            if (binding.RowKind == RowKind.Authoritative)
            {
                Buttons.Remove(key);
            }
            else
            {
                binding.State = BindingState.Tombstone;
            }
        }
        foreach (var closure in OutputClosures.Values.Where(
            closure => closure.PanelPointer == panelPointer))
        {
            closure.State = BindingState.Tombstone;
        }
    }

    private static void ResetReadyOutputBindingLocked(
        PanelTransaction transaction)
    {
        if (transaction.State != TransactionState.OutputReady)
        {
            return;
        }
        if (TryGetOutputButtonForTransactionLocked(
                transaction,
                out var outputButton))
        {
            outputButton.State = BindingState.AwaitingRebind;
        }
        if (transaction.OutputClosurePointer != 0
            && OutputClosures.TryGetValue(
                transaction.OutputClosurePointer,
                out var outputClosure))
        {
            outputClosure.State = BindingState.Tombstone;
        }
        transaction.OutputButtonPointer = 0;
        transaction.OutputComboPointer = 0;
        transaction.OutputClosurePointer = 0;
        transaction.OutputPointer = 0;
        transaction.OutputPanelEpoch = 0;
        transaction.State = TransactionState.Applied;
    }

    private static bool TryConsumeOuterFullVisualOutputResetLocked(
        nint panelPointer,
        long businessGeneration,
        out PanelTransaction? transaction,
        out OutputBindingResetReceipt receipt)
    {
        transaction = null;
        receipt = default;
        var scope = _activeFullVisualRefreshScope;
        while (scope != null
            && (scope.OutputResetConsumed
                || scope.PanelPointer != panelPointer
                || scope.BusinessGeneration != businessGeneration
                || scope.ResetTransaction == null))
        {
            scope = scope.Parent;
        }
        if (scope?.ResetTransaction is not { } candidate)
        {
            return false;
        }

        // A matching scope receipt is single-use even when its exact owner drifted. In that
        // case the caller must inspect and reset any current OutputReady binding instead.
        scope.OutputResetConsumed = true;
        var reset = scope.OutputReset;
        var exactResetOwner = scope.ResetPanel is { } resetPanel
            && Panels.TryGetValue(panelPointer, out var currentPanel)
            && ReferenceEquals(currentPanel, resetPanel)
            && currentPanel.PanelEpoch == scope.ResetPanelEpoch
            && currentPanel.BusinessGeneration == businessGeneration
            && !currentPanel.Retired
            && ReferenceEquals(currentPanel.Transaction, candidate)
            && candidate.Sequence == scope.ResetTransactionSequence
            && string.Equals(
                candidate.Identity,
                scope.ResetTransactionIdentity,
                StringComparison.Ordinal)
            && candidate.State == TransactionState.Applied
            && candidate.OutputButtonPointer == 0
            && candidate.OutputComboPointer == 0
            && candidate.OutputClosurePointer == 0
            && candidate.OutputPointer == 0
            && candidate.OutputPanelEpoch == 0
            && reset.ButtonPointer != 0
            && reset.ButtonBindingSequence > 0
            && reset.ClosurePointer != 0
            && reset.ComboPointer != 0
            && reset.OutputPointer != 0
            && Buttons.TryGetValue(reset.ButtonPointer, out var outputButton)
            && outputButton.Sequence == reset.ButtonBindingSequence
            && outputButton.State == BindingState.AwaitingRebind
            && outputButton.RowKind == RowKind.Output
            && outputButton.PanelPointer == panelPointer
            && outputButton.PanelEpoch == reset.OutputPanelEpoch
            && outputButton.RecipePointer == candidate.AuthoritativePointer
            && outputButton.RecipeId == candidate.RecipeId
            && outputButton.TargetGeneration == candidate.OriginTargetGeneration
            && string.Equals(
                outputButton.PlanIdentity,
                candidate.PlanIdentity,
                StringComparison.Ordinal)
            && outputButton.TransactionSequence == candidate.Sequence
            && string.Equals(
                outputButton.TransactionIdentity,
                candidate.Identity,
                StringComparison.Ordinal)
            && OutputClosures.TryGetValue(reset.ClosurePointer, out var outputClosure)
            && outputClosure.State == BindingState.Tombstone
            && outputClosure.PanelPointer == panelPointer
            && outputClosure.PanelEpoch == reset.OutputPanelEpoch
            && outputClosure.ButtonPointer == reset.ButtonPointer
            && outputClosure.ButtonBindingSequence == reset.ButtonBindingSequence
            && outputClosure.ComboPointer == reset.ComboPointer
            && outputClosure.OutputPointer == reset.OutputPointer
            && outputClosure.TargetGeneration == candidate.OriginTargetGeneration
            && outputClosure.TransactionSequence == candidate.Sequence
            && string.Equals(
                outputClosure.TransactionIdentity,
                candidate.Identity,
                StringComparison.Ordinal);
        if (!exactResetOwner)
        {
            return false;
        }

        transaction = candidate;
        receipt = reset;
        return true;
    }

    private static bool TryResetOutputForFullVisualLocked(
        nint panelPointer,
        long businessGeneration,
        out PanelTransaction? transaction,
        out OutputBindingResetReceipt receipt,
        out string error)
    {
        transaction = null;
        receipt = default;
        error = "";
        if (!Panels.TryGetValue(panelPointer, out var panel)
            || panel.Retired
            || panel.BusinessGeneration != businessGeneration
            || panel.Transaction is not { } candidate
            || candidate.BusinessGeneration != businessGeneration
            || candidate.State != TransactionState.OutputReady)
        {
            return false;
        }

        transaction = candidate;
        var retainedSwitch = panel.SwitchAttempt is { State:
            RecipeSwitchAttemptState.Armed
            or RecipeSwitchAttemptState.ReceiptObserved
            or RecipeSwitchAttemptState.VisualCompleted } switchAttempt
            ? switchAttempt
            : null;
        var activeSwitch = _activeSubmit is { SwitchAttemptSequence: > 0 } active
            ? active
            : null;
        var activeSwitchTargetsPanel = activeSwitch?.PanelPointer == panelPointer;
        if (retainedSwitch != null || activeSwitchTargetsPanel)
        {
            var exactArmedSwitch = retainedSwitch is { State: RecipeSwitchAttemptState.Armed }
                && activeSwitch != null
                && activeSwitch.Kind == SubmitKind.RecipeSwitch
                && activeSwitch.PanelPointer == panelPointer
                && ReferenceEquals(panel.Transaction, candidate)
                && ReferenceEquals(panel.SelectionIntent, retainedSwitch.Destination)
                && ReferenceEquals(retainedSwitch.SourceTransaction, candidate)
                && retainedSwitch.SourceStateAtArm == TransactionState.OutputReady
                && retainedSwitch.PanelPointer == panelPointer
                && retainedSwitch.PanelEpoch == panel.PanelEpoch
                && retainedSwitch.BusinessGeneration == businessGeneration
                && retainedSwitch.TargetGeneration == panel.TargetSet.Generation
                && activeSwitch.PanelEpoch == panel.PanelEpoch
                && activeSwitch.SwitchBusinessGeneration == businessGeneration
                && activeSwitch.SwitchPublicationLease != null
                && activeSwitch.ButtonPointer == retainedSwitch.Destination.ButtonPointer
                && activeSwitch.SwitchAttemptSequence == retainedSwitch.Sequence
                && string.Equals(
                    activeSwitch.SwitchAttemptIdentity,
                    retainedSwitch.Identity,
                    StringComparison.Ordinal)
                && ReferenceEquals(activeSwitch.SwitchAttemptToken, retainedSwitch)
                && _activeOutputClosure == null
                && TryValidateSelectionIntentLocked(
                    panel,
                    retainedSwitch.Destination);
            if (exactArmedSwitch)
            {
                // The native recipe-switch receipt owns this reset. Clearing the source here
                // would invalidate SourceStateAtArm before the exact native receipt is checked.
                transaction = null;
                return false;
            }

            error = "full visual refresh found inconsistent recipe-switch ownership";
            var activeRetainedSwitch = activeSwitch?.SwitchAttemptToken
                as RecipeSwitchAttempt;
            if (retainedSwitch != null)
            {
                MarkSwitchAttemptUncertainLocked(retainedSwitch, error);
            }
            if (activeRetainedSwitch != null
                && !ReferenceEquals(activeRetainedSwitch, retainedSwitch))
            {
                MarkSwitchAttemptUncertainLocked(activeRetainedSwitch, error);
            }
            MarkTransactionUncertainLocked(candidate, error);
            if (activeSwitch != null
                && activeRetainedSwitch == null
                && activeSwitch.SwitchBusinessGeneration > 0)
            {
                LatchBusinessMutationUncertainLocked(
                    activeSwitch.SwitchBusinessGeneration,
                    error);
                _lastResult = error;
            }
            return false;
        }

        var outputBindingIsStable = candidate.OutputClosurePointer != 0
            && OutputClosures.TryGetValue(candidate.OutputClosurePointer, out var outputClosure)
            && TryValidateOutputClosureLocked(
                outputClosure,
                TransactionState.OutputReady,
                out var outputPanel,
                out var outputTransaction)
            && ReferenceEquals(outputPanel, panel)
            && ReferenceEquals(outputTransaction, candidate)
            && _activeOutputClosure == null
            && !(_activeSubmit?.Kind == SubmitKind.Output
                && _activeSubmit.PanelPointer == panelPointer
                && string.Equals(
                    _activeSubmit.TransactionIdentity,
                    candidate.Identity,
                    StringComparison.Ordinal));
        if (!outputBindingIsStable
            || !TryGetOutputButtonForTransactionLocked(candidate, out var outputButton))
        {
            error = "full visual refresh found an unstable exact output binding";
            MarkTransactionUncertainLocked(candidate, error);
            return false;
        }

        receipt = CaptureOutputBindingResetReceipt(candidate, outputButton);
        ResetReadyOutputBindingLocked(candidate);
        return true;
    }

    private static OutputBindingResetReceipt CaptureOutputBindingResetReceipt(
        PanelTransaction transaction,
        ButtonBinding outputButton)
    {
        return new OutputBindingResetReceipt(
            transaction.OutputPanelEpoch,
            outputButton.ButtonPointer,
            outputButton.Sequence,
            transaction.OutputComboPointer,
            transaction.OutputClosurePointer,
            transaction.OutputPointer);
    }

    private static void LogOutputBindingReset(
        long businessGeneration,
        nint panelPointer,
        OutputBindingResetReceipt receipt,
        long newPanelEpoch = 0)
    {
        TryLogTransaction(
            businessGeneration,
            $"output-reset panel={FormatPointer(panelPointer)} "
            + $"fromEpoch={receipt.OutputPanelEpoch} "
            + $"toEpoch={(newPanelEpoch > 0 ? newPanelEpoch.ToString() : "pending")} "
            + $"button={FormatPointer(receipt.ButtonPointer)} "
            + $"combo={FormatPointer(receipt.ComboPointer)} "
            + $"closure={FormatPointer(receipt.ClosurePointer)} "
            + $"output={FormatPointer(receipt.OutputPointer)}",
            TransactionLogKind.Action);
    }

    private static void RetireOutputBindingLocked(
        ButtonBinding retiredBinding)
    {
        var closures = OutputClosures.Values
            .Where(binding =>
                binding.ButtonPointer == retiredBinding.ButtonPointer
                && binding.PanelPointer == retiredBinding.PanelPointer
                && binding.PanelEpoch == retiredBinding.PanelEpoch
                && binding.TargetGeneration == retiredBinding.TargetGeneration
                && binding.ButtonBindingSequence == retiredBinding.Sequence
                && binding.TransactionSequence == retiredBinding.TransactionSequence
                && string.Equals(
                    binding.TransactionIdentity,
                    retiredBinding.TransactionIdentity,
                    StringComparison.Ordinal))
            .ToArray();
        foreach (var closure in closures)
        {
            closure.State = BindingState.Tombstone;
        }
        if (Buttons.ContainsKey(retiredBinding.ButtonPointer)
            || retiredBinding.RowKind != RowKind.Output
            || retiredBinding.TransactionSequence <= 0
            || !TryGetTransactionLocked(
                retiredBinding.PanelPointer,
                retiredBinding.TransactionIdentity,
                out _,
                out var transaction)
            || transaction.OutputPanelEpoch != retiredBinding.PanelEpoch
            || transaction.Sequence != retiredBinding.TransactionSequence
            || transaction.OutputButtonPointer != retiredBinding.ButtonPointer)
        {
            return;
        }
        if (transaction.State == TransactionState.OutputReady
            && closures.Any(binding =>
                binding.ClosurePointer == transaction.OutputClosurePointer))
        {
            ResetReadyOutputBindingLocked(transaction);
        }
        else if (transaction.State == TransactionState.OutputPending)
        {
            MarkTransactionUncertainLocked(
                transaction,
                "output button was rebound while native callback registration was pending");
        }
    }

    private static bool TryReleasePriorOutputOwnershipForRecipeRow(
        object button,
        ButtonBindingObservation observation,
        out string error)
    {
        error = "";
        var binding = observation.Binding;
        if (binding == null || binding.RowKind != RowKind.Output) return true;

        lock (StateRoot)
        {
            if (!IsObservedButtonBindingCurrentLocked(
                    binding.ButtonPointer,
                    observation))
            {
                error = "prior output binding changed before recipe-row handoff";
                return false;
            }
        }

        TargetRecipeOutputClosureBindingSnapshot snapshot = default;
        var exactOutputCallback = false;
        try
        {
            exactOutputCallback = _runtime.TryReadExactOutputSubmitClosure(
                button,
                out snapshot,
                out error);
        }
        catch (Exception ex)
        {
            error = $"prior output callback probe threw: {DescribeException(ex)}";
            return false;
        }

        if (!exactOutputCallback)
        {
            lock (StateRoot)
            {
                if (!IsObservedButtonBindingCurrentLocked(
                        binding.ButtonPointer,
                        observation))
                {
                    error = "prior output binding changed while proving callback ownership";
                    return false;
                }
                // A successfully inspected non-output callback is outside Mod ownership. Retire
                // only the stale managed output owner and never touch the current native callback.
                Buttons.Remove(binding.ButtonPointer);
                RetireOutputBindingLocked(binding);
            }
            return true;
        }

        lock (StateRoot)
        {
            if (!IsObservedButtonBindingCurrentLocked(
                    binding.ButtonPointer,
                    observation)
                || !OutputClosures.TryGetValue(
                    snapshot.ClosurePointer,
                    out var closure)
                || closure.PanelPointer != binding.PanelPointer
                || closure.PanelEpoch != binding.PanelEpoch
                || closure.ButtonPointer != binding.ButtonPointer
                || closure.ButtonBindingSequence != binding.Sequence
                || closure.TransactionSequence != binding.TransactionSequence
                || !string.Equals(
                    closure.TransactionIdentity,
                    binding.TransactionIdentity,
                    StringComparison.Ordinal)
                || closure.ComboPointer != snapshot.ComboPointer
                || closure.OutputPointer != snapshot.OutputPointer
                || snapshot.PanelPointer != binding.PanelPointer)
            {
                error = "live output callback is not owned by the observed output binding";
                return false;
            }
            Buttons.Remove(binding.ButtonPointer);
        }

        if (!TryCleanSubmitCallbackExclusive(
                button,
                binding.ButtonPointer,
                null,
                out var cleanupError))
        {
            lock (StateRoot)
            {
                binding.State = BindingState.Tombstone;
                if (!Buttons.ContainsKey(binding.ButtonPointer))
                {
                    Buttons[binding.ButtonPointer] = binding;
                }
            }
            error = $"exact prior output callback cleanup failed: {cleanupError}";
            return false;
        }

        lock (StateRoot)
        {
            RetireOutputBindingLocked(binding);
        }
        return true;
    }

    private static bool TryRetireObservedBindingForOutputSelection(
        ButtonBindingObservation observation,
        nint buttonPointer,
        out string error)
    {
        error = "";
        lock (StateRoot)
        {
            if (!IsObservedButtonBindingCurrentLocked(buttonPointer, observation))
            {
                error = "button binding changed before native output selection";
                return false;
            }
            if (observation.Binding == null) return true;

            Buttons.Remove(buttonPointer);
            if (observation.Binding.RowKind == RowKind.Output)
            {
                RetireOutputBindingLocked(observation.Binding);
            }
            return true;
        }
    }

    internal static bool PrepareRecipeElementEnableForTests(
        object panel,
        object recipe,
        object button,
        out EnableHookState state)
    {
        return PrepareRecipeElementEnable(panel, recipe, button, out state);
    }

    internal static void CompleteRecipeElementEnableForTests(
        EnableHookState state,
        bool originalCompleted = true)
    {
        CompleteRecipeElementEnable(state, originalCompleted);
    }

    private static bool PrepareRecipeElementEnable(
        object panel,
        object recipe,
        object button,
        out EnableHookState hookState)
    {
        var buttonPointer = TryGetNativePointer(button);
        ButtonBindingObservation observation;
        lock (StateRoot)
        {
            observation = CaptureButtonBindingObservationLocked(buttonPointer);
        }
        try
        {
            return PrepareRecipeElementEnableCore(
                panel,
                recipe,
                button,
                observation,
                out hookState);
        }
        catch (Exception ex)
        {
            hookState = default;
            return HandleRecipeEntryFailure(
                panel,
                recipe,
                button,
                observation,
                $"recipe-row enable probe threw: {DescribeException(ex)}");
        }
    }

    private static bool PrepareRecipeElementEnableCore(
        object panel,
        object recipe,
        object button,
        ButtonBindingObservation observedBindingIdentity,
        out EnableHookState hookState)
    {
        hookState = default;
        var panelPointer = TryGetNativePointer(panel);
        var buttonPointer = TryGetNativePointer(button);
        if (panelPointer == 0 || buttonPointer == 0) return true;
        var rawRecipePointer = TryGetNativePointer(recipe);

        var observedBinding = observedBindingIdentity.Binding;

        TargetRecipeSnapshot recipeSnapshot = null!;
        var snapshotError = "";
        var snapshotRead = false;
        try
        {
            snapshotRead = _runtime.TryReadRecipeSnapshot(
                recipe,
                out recipeSnapshot,
                out snapshotError);
        }
        catch (Exception ex)
        {
            snapshotError = DescribeException(ex);
        }

        IReadOnlyList<TargetRecipeDescriptor> currentRecipes = Array.Empty<TargetRecipeDescriptor>();
        var currentRecipeListPointer = (nint)0;
        var currentListError = "";
        var currentListRead = false;
        try
        {
            currentListRead = _runtime.TryReadRecipeList(
                panel,
                MaximumRecipeCount,
                out var currentRecipeList,
                out currentRecipes,
                out currentListError);
            if (currentListRead)
            {
                currentRecipeListPointer = TryGetNativePointer(currentRecipeList);
                currentListRead = currentRecipeListPointer != 0;
                if (!currentListRead) currentListError = "recipe list has no native identity";
            }
        }
        catch (Exception ex)
        {
            currentListError = DescribeException(ex);
        }
        if (!snapshotRead || recipeSnapshot.RecipePointer == 0)
        {
            var blockCurrentInput = false;
            lock (StateRoot)
            {
                if (Panels.TryGetValue(panelPointer, out var knownPanel)
                    && IsCurrentPanelContextLocked(knownPanel)
                    && currentListRead
                    && currentRecipeListPointer == knownPanel.RecipeListPointer)
                {
                    var rawMembership = currentRecipes.Count(candidate =>
                        candidate.RecipePointer == rawRecipePointer);
                    if (rawMembership == 0)
                    {
                        // A late callback for a row that no longer belongs to the exact current
                        // recipe list must not touch the binding now owning the pooled button.
                        blockCurrentInput = true;
                    }
                    else if (rawMembership != 1)
                    {
                        blockCurrentInput = true;
                    }
                    else if (TryFindSyntheticPlanLocked(
                        knownPanel,
                        rawRecipePointer,
                        out var knownOwner,
                        out var knownPlan))
                    {
                        blockCurrentInput = true;
                        if (IsObservedButtonBindingCurrentLocked(
                            buttonPointer,
                            observedBindingIdentity))
                        {
                            Buttons[buttonPointer] = CreateSyntheticTombstone(
                                knownPanel,
                                knownOwner,
                                knownPlan,
                                buttonPointer,
                                rawRecipePointer);
                        }
                    }
                    else
                    {
                        // The exact current list proves this is a native row. Release only the
                        // observed Mod recipe lease; the game owns its callback and interactable.
                        if (IsObservedButtonBindingCurrentLocked(
                                buttonPointer,
                                observedBindingIdentity)
                            && observedBinding is { RowKind: RowKind.Authoritative or RowKind.Synthetic })
                        {
                            Buttons.Remove(buttonPointer);
                        }
                        return true;
                    }
                }
                else if (observedBinding?.RowKind == RowKind.Synthetic
                    && observedBinding.RecipePointer == rawRecipePointer)
                {
                    blockCurrentInput = true;
                    if (IsObservedButtonBindingCurrentLocked(
                        buttonPointer,
                        observedBindingIdentity))
                    {
                        observedBinding.State = BindingState.Tombstone;
                    }
                }
                else if (observedBinding != null)
                {
                    // The read failed after another row acquired the pooled button. Preserve the
                    // current owner and suppress only this unprovable late input.
                    blockCurrentInput = true;
                }
            }
            if (blockCurrentInput)
            {
                return BlockRecipeRow(
                    $"recipe snapshot failed without current-list ownership: {snapshotError}; "
                    + $"list={currentListError}");
            }
            return true;
        }

        PanelState? panelState = null;
        ControlledRecipe? controlled = null;
        var rowKind = RowKind.Authoritative;
        var claims = RuntimeUiTargetKinds.None;
        var planIdentity = "";
        TargetRecipeVariantPlan? variantPlan = null;
        string? blockReason = null;
        lock (StateRoot)
        {
            if (!Panels.TryGetValue(panelPointer, out panelState))
            {
                if (observedBinding != null
                    && observedBinding.RecipePointer == recipeSnapshot.RecipePointer
                    && observedBinding.RowKind == RowKind.Synthetic)
                {
                    if (IsObservedButtonBindingCurrentLocked(buttonPointer, observedBindingIdentity))
                    {
                        observedBinding.State = BindingState.Tombstone;
                    }
                    blockReason = "known synthetic recipe lost its owning panel";
                }
            }
            else if (!currentListRead
                || currentRecipeListPointer != panelState.RecipeListPointer)
            {
                if (observedBinding != null
                    && IsObservedButtonBindingCurrentLocked(
                        buttonPointer,
                        observedBindingIdentity)
                    && observedBinding.RecipePointer != recipeSnapshot.RecipePointer)
                {
                    blockReason = "unprovable late recipe row lost the current list identity";
                }
                else if (TryFindSyntheticPlanLocked(
                    panelState,
                    recipeSnapshot.RecipePointer,
                    out var unreadOwner,
                    out var unreadPlan))
                {
                    blockReason = $"current synthetic recipe list read failed: {currentListError}";
                    if (IsObservedButtonBindingCurrentLocked(
                        buttonPointer,
                        observedBindingIdentity))
                    {
                        Buttons[buttonPointer] = CreateSyntheticTombstone(
                            panelState,
                            unreadOwner,
                            unreadPlan,
                            buttonPointer,
                            recipeSnapshot.RecipePointer);
                    }
                }
                else
                {
                    panelState.Retired = true;
                    TombstonePanelButtonsLocked(panelPointer);
                    _lastResult = $"retired recipe-variant panel after recipe-list drift: {currentListError}";
                    return true;
                }
            }
            else if (currentRecipes.Count(candidate =>
                    candidate.RecipePointer == recipeSnapshot.RecipePointer
                    && candidate.RecipeId == recipeSnapshot.RecipeId
                    && candidate.IngredientIds.SequenceEqual(
                        recipeSnapshot.IngredientIds)) != 1)
            {
                if (TryFindSyntheticPlanLocked(
                    panelState,
                    recipeSnapshot.RecipePointer,
                    out var staleOwner,
                    out var stalePlan))
                {
                    blockReason = "synthetic recipe is not a unique current-list member";
                    if (IsObservedButtonBindingCurrentLocked(
                        buttonPointer,
                        observedBindingIdentity))
                    {
                        Buttons[buttonPointer] = CreateSyntheticTombstone(
                            panelState,
                            staleOwner,
                            stalePlan,
                            buttonPointer,
                            recipeSnapshot.RecipePointer);
                    }
                }
                else if (TryFindAuthoritativeRecipeLocked(
                    panelState,
                    recipeSnapshot.RecipePointer,
                    out _))
                {
                    panelState.Retired = true;
                    TombstonePanelButtonsLocked(panelPointer);
                    _lastResult = "retired recipe-variant panel after authoritative list membership drift";
                    return true;
                }
                else
                {
                    // This is a delayed or otherwise non-current native row. Suppress only this
                    // invocation and preserve the binding for the row that owns the pooled button.
                    blockReason = "recipe row is not a unique member of the current exact list";
                }
            }
            else if (TryFindSyntheticPlanLocked(
                panelState,
                recipeSnapshot.RecipePointer,
                out controlled,
                out variantPlan))
            {
                rowKind = RowKind.Synthetic;
                claims = variantPlan.Claims;
                planIdentity = variantPlan.Identity;
                var expectedIngredients = variantPlan.BaseIngredientIds
                    .Concat(variantPlan.ExtraIngredientIds);
                if (recipeSnapshot.RecipeId != controlled.RecipeId
                    || recipeSnapshot.CookCount < -1
                    || !recipeSnapshot.IngredientIds.SequenceEqual(expectedIngredients))
                {
                    blockReason = "known synthetic recipe fields changed";
                }
                else if (!IsCurrentPanelContextLocked(panelState))
                {
                    blockReason = "known synthetic recipe panel or lifecycle changed";
                }
                else if (panelState.MutationUncertain)
                {
                    blockReason = $"recipe variant mutation is latched uncertain: {panelState.MutationUncertainReason}";
                }
                else if (!controlled.Complete)
                {
                    blockReason = "controlled synthetic recipe state is incomplete";
                }
                if (blockReason != null)
                {
                    if (IsObservedButtonBindingCurrentLocked(buttonPointer, observedBindingIdentity))
                    {
                        Buttons[buttonPointer] = CreateSyntheticTombstone(
                            panelState,
                            controlled,
                            variantPlan,
                            buttonPointer,
                            recipeSnapshot.RecipePointer);
                    }
                }
            }
            else if (TryFindAuthoritativeRecipeLocked(
                panelState,
                recipeSnapshot.RecipePointer,
                out controlled))
            {
                if (recipeSnapshot.RecipeId != controlled.RecipeId
                    || !recipeSnapshot.IngredientIds.SequenceEqual(controlled.BaseIngredientIds)
                    || !controlled.Complete
                    || !IsCurrentPanelContextLocked(panelState))
                {
                    // Any Mod evidence drift on a native base row retires the stale Mod panel,
                    // but never disables or replaces the game's own callback.
                    panelState.Retired = true;
                    TombstonePanelButtonsLocked(panelPointer);
                    _lastResult = "retired recipe-variant panel after authoritative row drift";
                    return true;
                }
                rowKind = RowKind.Authoritative;
                claims = controlled.BaseClaims;
                planIdentity = controlled.BaseIdentity;
            }
            else
            {
                if (panelState.ControlledRecipes.ContainsKey(recipeSnapshot.RecipeId))
                {
                    // Same Recipe ID is not ownership. A native row with an unknown pointer
                    // invalidates this panel snapshot and remains on the native path.
                    panelState.Retired = true;
                    TombstonePanelButtonsLocked(panelPointer);
                    _lastResult = "retired recipe-variant panel after an unknown same-id native row";
                    return true;
                }
                else
                {
                    if (IsObservedButtonBindingCurrentLocked(
                            buttonPointer,
                            observedBindingIdentity)
                        && observedBinding is { RowKind: RowKind.Authoritative or RowKind.Synthetic })
                    {
                        // The exact current list proves the pooled button now belongs to an
                        // unrelated native recipe. Release only the old Mod lease.
                        Buttons.Remove(buttonPointer);
                    }
                    return true;
                }
            }
        }
        if (blockReason != null)
        {
            return BlockRecipeRow(blockReason);
        }
        if (panelState == null || controlled == null) return true;

        if (observedBinding?.RowKind == RowKind.Output)
        {
            if (!TryReleasePriorOutputOwnershipForRecipeRow(
                    button,
                    observedBindingIdentity,
                    out var outputHandoffError))
            {
                return BlockRecipeRow(outputHandoffError);
            }
            observedBindingIdentity = default;
            observedBinding = null;
        }

        if (variantPlan != null)
        {
            TargetRecipePanelSelectionState cookingState = null!;
            var cookingError = "";
            var cookingRead = false;
            try
            {
                cookingRead = _runtime.TryReadPanelSelectionState(
                    panel,
                    out cookingState,
                    out cookingError);
            }
            catch (Exception ex)
            {
                cookingError = DescribeException(ex);
            }
            if (!cookingRead || cookingState.PanelPointer != panelPointer)
            {
                return TombstoneRecipeRow(
                    panelState,
                    recipeSnapshot,
                    buttonPointer,
                    planIdentity,
                    rowKind,
                    observedBindingIdentity,
                    $"synthetic cook-count state failed: {cookingError}");
            }

            int cookCount;
            try
            {
                cookCount = CalculateSyntheticCookCount(
                    cookingState,
                    variantPlan.BaseIngredientIds.Concat(
                        variantPlan.ExtraIngredientIds).ToArray());
            }
            catch (Exception ex)
            {
                return TombstoneRecipeRow(
                    panelState,
                    recipeSnapshot,
                    buttonPointer,
                    planIdentity,
                    rowKind,
                    observedBindingIdentity,
                    $"synthetic cook-count failed: {DescribeException(ex)}");
            }

            var countWritten = false;
            var countError = "";
            try
            {
                countWritten = _runtime.TrySetSyntheticCookCount(
                    recipe,
                    cookCount,
                    out countError);
            }
            catch (Exception ex)
            {
                countError = DescribeException(ex);
            }
            if (!countWritten)
            {
                return TombstoneRecipeRow(
                    panelState,
                    recipeSnapshot,
                    buttonPointer,
                    planIdentity,
                    rowKind,
                    observedBindingIdentity,
                    $"synthetic CookCount write failed: {countError}");
            }
        }

        var changedBeforeRegistration = false;
        ButtonBinding? pendingBinding = null;
        lock (StateRoot)
        {
            if (!IsCurrentPanelContextLocked(panelState))
            {
                if (rowKind == RowKind.Authoritative)
                {
                    return true;
                }
                if (IsObservedButtonBindingCurrentLocked(buttonPointer, observedBindingIdentity))
                {
                    Buttons[buttonPointer] = CreateSyntheticTombstone(
                        panelState,
                        controlled,
                        variantPlan!,
                        buttonPointer,
                        recipeSnapshot.RecipePointer);
                }
                changedBeforeRegistration = true;
            }
            else if (IsButtonCleanupInProgressLocked(buttonPointer)
                || !IsObservedButtonBindingCurrentLocked(buttonPointer, observedBindingIdentity)
                || observedBinding is { RowKind: RowKind.Output, State: not BindingState.Tombstone })
            {
                changedBeforeRegistration = true;
            }
            else
            {
                pendingBinding = new ButtonBinding(
                    panelPointer,
                    panelState.PanelEpoch,
                    buttonPointer,
                    recipeSnapshot.RecipePointer,
                    recipeSnapshot.RecipeId,
                    panelState.TargetSet.Generation,
                    planIdentity,
                    claims,
                    rowKind,
                    0,
                    "",
                    BindingState.Pending);
                Buttons[buttonPointer] = pendingBinding;
            }
        }
        if (changedBeforeRegistration)
        {
            lock (StateRoot)
            {
                _failures++;
                _lastResult = "panel or physical recipe-row ownership changed before registration";
            }
            return false;
        }
        hookState = new EnableHookState(
            panelPointer,
            panelState.PanelEpoch,
            buttonPointer,
            recipeSnapshot.RecipePointer,
            pendingBinding!.Sequence,
            true);
        return true;
    }

    private static void CompleteRecipeElementEnable(
        EnableHookState hookState,
        bool originalCompleted)
    {
        if (!hookState.Pending) return;
        lock (StateRoot)
        {
            if (!Buttons.TryGetValue(hookState.ButtonPointer, out var binding)
                || binding.PanelPointer != hookState.PanelPointer
                || binding.PanelEpoch != hookState.PanelEpoch
                || binding.RecipePointer != hookState.RecipePointer
                || binding.Sequence != hookState.BindingSequence
                || binding.State != BindingState.Pending)
            {
                return;
            }
            binding.State = originalCompleted ? BindingState.Ready : BindingState.Tombstone;
            if (originalCompleted
                && binding.Claims != RuntimeUiTargetKinds.None
                && Panels.TryGetValue(binding.PanelPointer, out var panel)
                && panel.PanelEpoch == binding.PanelEpoch)
            {
                TryLogTransaction(
                    panel.BusinessGeneration,
                    $"row-ready panel={FormatPointer(binding.PanelPointer)} "
                    + $"epoch={binding.PanelEpoch} targetGen={binding.TargetGeneration} "
                    + $"recipe={binding.RecipeId}@{FormatPointer(binding.RecipePointer)} "
                    + $"button={FormatPointer(binding.ButtonPointer)} row={binding.RowKind} "
                    + $"claims={binding.Claims} plan={binding.PlanIdentity}",
                    TransactionLogKind.Surface);
            }
        }
    }

    private static bool TombstoneRecipeRow(
        PanelState panelState,
        TargetRecipeSnapshot recipe,
        nint buttonPointer,
        string planIdentity,
        RowKind rowKind,
        ButtonBindingObservation observedBinding,
        string reason)
    {
        lock (StateRoot)
        {
            if (IsCurrentPanelContextLocked(panelState)
                && IsObservedButtonBindingCurrentLocked(buttonPointer, observedBinding))
            {
                Buttons[buttonPointer] = new ButtonBinding(
                    panelState.PanelPointer,
                    panelState.PanelEpoch,
                    buttonPointer,
                    recipe.RecipePointer,
                    recipe.RecipeId,
                    panelState.TargetSet.Generation,
                    planIdentity,
                    RuntimeUiTargetKinds.None,
                    rowKind,
                    0,
                    "",
                    BindingState.Tombstone);
            }
            _failures++;
            _lastResult = reason;
        }
        return false;
    }

    private static bool BlockRecipeRow(string reason)
    {
        lock (StateRoot)
        {
            _failures++;
            _lastResult = reason;
        }
        return false;
    }

    internal static bool TryResolveRecipeRowClaims(
        object panel,
        object recipe,
        object button,
        out RuntimeUiTargetKinds claims,
        out TargetRecipeVariantRowLease lease)
    {
        claims = RuntimeUiTargetKinds.None;
        lease = default;
        var panelPointer = TryGetNativePointer(panel);
        var recipePointer = TryGetNativePointer(recipe);
        var buttonPointer = TryGetNativePointer(button);
        if (panelPointer == 0 || recipePointer == 0 || buttonPointer == 0) return false;

        lock (StateRoot)
        {
            if (!Buttons.TryGetValue(buttonPointer, out var binding)
                || binding.PanelPointer != panelPointer
                || binding.RecipePointer != recipePointer
                || binding.ButtonPointer != buttonPointer
                || binding.State != BindingState.Ready
                || binding.RowKind == RowKind.Output
                || !TryValidateBindingLocked(binding, out _))
            {
                return false;
            }

            claims = binding.Claims;
            lease = new TargetRecipeVariantRowLease(
                binding.PanelPointer,
                binding.PanelEpoch,
                binding.RecipePointer,
                binding.ButtonPointer,
                binding.TargetGeneration,
                binding.PlanIdentity);
            return true;
        }
    }

    internal static bool TryValidateRecipeRowClaims(
        TargetRecipeVariantRowLease lease,
        out RuntimeUiTargetKinds claims)
    {
        claims = RuntimeUiTargetKinds.None;
        if (lease.PanelPointer == 0
            || lease.RecipePointer == 0
            || lease.ButtonPointer == 0
            || string.IsNullOrEmpty(lease.PlanIdentity))
        {
            return false;
        }

        lock (StateRoot)
        {
            if (!Buttons.TryGetValue(lease.ButtonPointer, out var binding)
                || binding.PanelPointer != lease.PanelPointer
                || binding.PanelEpoch != lease.PanelEpoch
                || binding.RecipePointer != lease.RecipePointer
                || binding.TargetGeneration != lease.TargetGeneration
                || !string.Equals(binding.PlanIdentity, lease.PlanIdentity, StringComparison.Ordinal)
                || binding.State != BindingState.Ready
                || binding.RowKind == RowKind.Output
                || !TryValidateBindingLocked(binding, out _))
            {
                return false;
            }
            claims = binding.Claims;
            return true;
        }
    }

    private static bool TryValidateBindingLocked(
        ButtonBinding binding,
        out PanelState panel)
    {
        panel = null!;
        if (!Panels.TryGetValue(binding.PanelPointer, out var candidate)
            || candidate.Retired)
        {
            return false;
        }
        if (binding.RowKind == RowKind.Output)
        {
            if (!IsCurrentPanelBusinessLocked(candidate)
                || candidate.Transaction is not { } transaction
                || transaction.OutputPanelEpoch != binding.PanelEpoch
                || transaction.OutputButtonPointer != binding.ButtonPointer
                || transaction.Sequence != binding.TransactionSequence
                || binding.TargetGeneration != transaction.OriginTargetGeneration
                || !string.Equals(
                    binding.TransactionIdentity,
                    transaction.Identity,
                    StringComparison.Ordinal))
            {
                return false;
            }
        }
        else if (candidate.PanelEpoch != binding.PanelEpoch
            || candidate.TargetSet.Generation != binding.TargetGeneration
            || !ReferenceEquals(
                RuntimeUiPinningService.ReadTargetSet(),
                candidate.TargetSet)
            || !IsCurrentPanelContextLocked(candidate))
        {
            return false;
        }
        panel = candidate;
        return true;
    }

    private static bool IsCurrentPanelBusinessLocked(PanelState panel)
    {
        if (!Panels.TryGetValue(panel.PanelPointer, out var current)
            || !ReferenceEquals(current, panel)
            || panel.Retired)
        {
            return false;
        }
        var lifecycle = RuntimeNightBusinessLifecycle.Snapshot;
        return lifecycle.IsActive
            && lifecycle.Generation == panel.BusinessGeneration;
    }

    private static bool IsCurrentPanelContextLocked(PanelState panel)
    {
        if (!Panels.TryGetValue(panel.PanelPointer, out var current)
            || !ReferenceEquals(current, panel)
            || panel.Retired
            || !ReferenceEquals(RuntimeUiPinningService.ReadTargetSet(), panel.TargetSet))
        {
            return false;
        }
        return IsCurrentPanelBusinessLocked(panel)
            && panel.TargetSet.SessionGeneration == panel.BusinessGeneration;
    }

    private static bool TryFindSyntheticPlanLocked(
        PanelState panel,
        nint recipePointer,
        out ControlledRecipe owner,
        out TargetRecipeVariantPlan plan)
    {
        owner = null!;
        plan = null!;
        if (recipePointer == 0) return false;
        foreach (var candidate in panel.ControlledRecipes.Values)
        {
            if (!candidate.SyntheticPlans.TryGetValue(recipePointer, out var found)) continue;
            owner = candidate;
            plan = found;
            return true;
        }
        return false;
    }

    private static bool TryFindAuthoritativeRecipeLocked(
        PanelState panel,
        nint recipePointer,
        out ControlledRecipe owner)
    {
        owner = null!;
        if (recipePointer == 0) return false;
        foreach (var candidate in panel.ControlledRecipes.Values)
        {
            if (candidate.AuthoritativePointer != recipePointer) continue;
            owner = candidate;
            return true;
        }
        return false;
    }

    private static ButtonBindingObservation CaptureButtonBindingObservationLocked(
        nint buttonPointer)
    {
        return Buttons.TryGetValue(buttonPointer, out var binding)
            ? new ButtonBindingObservation(
                binding,
                binding.Sequence,
                binding.State,
                binding.StateVersion)
            : default;
    }

    private static bool IsObservedButtonBindingCurrentLocked(
        nint buttonPointer,
        ButtonBindingObservation observedBinding)
    {
        return observedBinding.Binding == null
            ? !Buttons.ContainsKey(buttonPointer)
            : Buttons.TryGetValue(buttonPointer, out var current)
                && ReferenceEquals(current, observedBinding.Binding)
                && current.Sequence == observedBinding.Sequence
                && current.State == observedBinding.State
                && current.StateVersion == observedBinding.StateVersion;
    }

    private static ButtonBinding CreateSyntheticTombstone(
        PanelState panel,
        ControlledRecipe owner,
        TargetRecipeVariantPlan plan,
        nint buttonPointer,
        nint recipePointer)
    {
        return new ButtonBinding(
            panel.PanelPointer,
            panel.PanelEpoch,
            buttonPointer,
            recipePointer,
            owner.RecipeId,
            panel.TargetSet.Generation,
            plan.Identity,
            RuntimeUiTargetKinds.None,
            RowKind.Synthetic,
            0,
            "",
            BindingState.Tombstone);
    }

    private static bool HandleRecipeEntryFailure(
        object panel,
        object recipe,
        object button,
        ButtonBindingObservation observation,
        string reason)
    {
        var panelPointer = TryGetNativePointer(panel);
        var recipePointer = TryGetNativePointer(recipe);
        var buttonPointer = TryGetNativePointer(button);
        if (buttonPointer == 0) return true;
        var failClosed = false;
        lock (StateRoot)
        {
            if (observation.Binding != null
                && IsObservedButtonBindingCurrentLocked(
                    buttonPointer,
                    observation))
            {
                var binding = observation.Binding;
                if (binding.RowKind == RowKind.Authoritative)
                {
                    Buttons.Remove(buttonPointer);
                    return true;
                }
                if (binding.RowKind == RowKind.Synthetic
                    && binding.RecipePointer == recipePointer)
                {
                    binding.State = BindingState.Tombstone;
                    failClosed = true;
                }
                else
                {
                    // The pooled button acquired another exact owner while this hook was
                    // evaluating. Suppress only the stale input and preserve the current owner.
                    failClosed = true;
                }
            }
            else if (Panels.TryGetValue(panelPointer, out var panelState)
                && TryFindSyntheticPlanLocked(
                    panelState,
                    recipePointer,
                    out var owner,
                    out var plan))
            {
                var blockedBinding = CreateSyntheticTombstone(
                    panelState,
                    owner,
                    plan,
                    buttonPointer,
                    recipePointer);
                Buttons[buttonPointer] = blockedBinding;
                failClosed = true;
            }
            if (failClosed)
            {
                _blockedSubmissions++;
                _lastResult = reason;
            }
        }
        return failClosed ? BlockRecipeRow(reason) : true;
    }

    internal static bool RouteRecipeSelectionForTests(
        object panel,
        ref object recipe,
        object button)
    {
        var shouldRun = PrepareRecipeSelection(
            panel,
            ref recipe,
            button,
            out var state);
        CompleteRecipeSelection(state, shouldRun ? null : new InvalidOperationException(
            "recipe selection original was suppressed"));
        return shouldRun;
    }

    internal static bool PrepareRecipeSelectionForTests(
        object panel,
        ref object recipe,
        object button,
        out RecipeSelectionHookState state)
    {
        return PrepareRecipeSelection(panel, ref recipe, button, out state);
    }

    internal static Exception? CompleteRecipeSelectionForTests(
        RecipeSelectionHookState state,
        Exception? exception = null)
    {
        return CompleteRecipeSelection(state, exception);
    }

    private static bool PrepareRecipeSelection(
        object panel,
        ref object recipe,
        object button,
        out RecipeSelectionHookState hookState)
    {
        hookState = new RecipeSelectionHookState();
        var buttonPointer = TryGetNativePointer(button);
        ButtonBindingObservation observation;
        lock (StateRoot)
        {
            observation = CaptureButtonBindingObservationLocked(buttonPointer);
        }
        try
        {
            var shouldRun = RouteRecipeSelectionCore(
                panel,
                ref recipe,
                button,
                observation,
                out var intent);
            hookState.Intent = intent;
            hookState.OriginalAllowed = shouldRun;
            return shouldRun;
        }
        catch (Exception ex)
        {
            return HandleRecipeEntryFailure(
                panel,
                recipe,
                button,
                observation,
                $"recipe selection probe threw: {DescribeException(ex)}");
        }
    }

    private static bool RouteRecipeSelectionCore(
        object panel,
        ref object recipe,
        object button,
        ButtonBindingObservation entryBindingObservation,
        out RecipeSelectionIntent? intent)
    {
        intent = null;
        var panelPointer = TryGetNativePointer(panel);
        var buttonPointer = TryGetNativePointer(button);
        if (panelPointer == 0 || buttonPointer == 0) return true;
        var rawRecipePointer = TryGetNativePointer(recipe);
        TargetRecipeSnapshot recipeSnapshot = null!;
        var snapshotRead = false;
        try
        {
            snapshotRead = _runtime.TryReadRecipeSnapshot(
                recipe,
                out recipeSnapshot,
                out _);
        }
        catch
        {
            snapshotRead = false;
        }
        if (!snapshotRead)
        {
            var knownSynthetic = false;
            lock (StateRoot)
            {
                if (entryBindingObservation.Binding is { RowKind: RowKind.Synthetic } existingBinding
                    && existingBinding.RecipePointer == rawRecipePointer
                    && IsObservedButtonBindingCurrentLocked(
                        buttonPointer,
                        entryBindingObservation))
                {
                    existingBinding.State = BindingState.Tombstone;
                    knownSynthetic = true;
                }
                else if (Panels.TryGetValue(panelPointer, out var knownPanel)
                    && TryFindSyntheticPlanLocked(
                        knownPanel,
                        rawRecipePointer,
                        out var owner,
                        out var knownPlan))
                {
                    knownSynthetic = true;
                    if (IsObservedButtonBindingCurrentLocked(
                        buttonPointer,
                        entryBindingObservation))
                    {
                        Buttons[buttonPointer] = CreateSyntheticTombstone(
                            knownPanel,
                            owner,
                            knownPlan,
                            buttonPointer,
                            rawRecipePointer);
                    }
                }
                else if (entryBindingObservation.Binding != null)
                {
                    // A delayed selection failed after the physical button acquired a fresh
                    // binding. Preserve that binding and suppress only the stale selection.
                    knownSynthetic = true;
                }
            }
            if (knownSynthetic)
            {
                return BlockRecipeRow("known synthetic selection snapshot failed");
            }
            return true;
        }

        if (!_runtime.TryReadRecipeList(
                panel,
                MaximumRecipeCount,
                out _,
                out var currentRecipes,
                out var currentListError))
        {
            return HandleRecipeEntryFailure(
                panel,
                recipe,
                button,
                entryBindingObservation,
                $"recipe selection list refresh failed: {currentListError}");
        }
        if (currentRecipes.Count(candidate =>
                candidate.RecipePointer == recipeSnapshot.RecipePointer
                && candidate.RecipeId == recipeSnapshot.RecipeId
                && candidate.IngredientIds.SequenceEqual(recipeSnapshot.IngredientIds)) != 1)
        {
            return HandleRecipeEntryFailure(
                panel,
                recipe,
                button,
                entryBindingObservation,
                "recipe selection is not a unique current list member");
        }

        ButtonBinding? binding;
        PanelState? panelState;
        ControlledRecipe? controlled;
        TargetRecipeVariantPlan? plan;
        lock (StateRoot)
        {
            if (!Panels.TryGetValue(panelPointer, out panelState)
                || panelState.Retired
                || !ReferenceEquals(
                    RuntimeUiPinningService.ReadTargetSet(),
                    panelState.TargetSet))
            {
                if (Buttons.TryGetValue(buttonPointer, out var staleBinding))
                {
                    if (staleBinding.RowKind == RowKind.Authoritative)
                    {
                        Buttons.Remove(buttonPointer);
                    }
                    else
                    {
                        staleBinding.State = BindingState.Tombstone;
                        _blockedSubmissions++;
                        _lastResult = "blocked known synthetic selection after panel drift";
                        return false;
                    }
                }
                else if (panelState != null
                    && TryFindSyntheticPlanLocked(
                        panelState,
                        recipeSnapshot.RecipePointer,
                        out var staleOwner,
                        out var stalePlan))
                {
                    Buttons[buttonPointer] = CreateSyntheticTombstone(
                        panelState,
                        staleOwner,
                        stalePlan,
                        buttonPointer,
                        recipeSnapshot.RecipePointer);
                    _blockedSubmissions++;
                    _lastResult = "blocked known synthetic selection after panel drift";
                    return false;
                }
                return true;
            }
            if (!Buttons.TryGetValue(buttonPointer, out binding))
            {
                if (_activeSubmit?.Kind == SubmitKind.Recipe)
                {
                    _blockedSubmissions++;
                    _lastResult = "blocked a different recipe selection during exact submit";
                    return false;
                }
                if (!HasMutationAwaitingNativeSwitchLocked(panelState)
                    && !TryRetirePriorTransactionLocked(panelState))
                {
                    _blockedSubmissions++;
                    _lastResult = "blocked recipe switch after a variant mutation started";
                    return false;
                }
                if (currentRecipes.Any(candidate =>
                        candidate.RecipePointer == recipeSnapshot.RecipePointer)
                    && !TryFindSyntheticPlanLocked(
                        panelState,
                        recipeSnapshot.RecipePointer,
                        out _,
                        out _)
                    && !panelState.ControlledRecipes.Values.Any(owner =>
                        owner.AuthoritativePointer == recipeSnapshot.RecipePointer))
                {
                    intent = new RecipeSelectionIntent(
                        panelState,
                        null,
                        RecipeDestinationKind.Ordinary,
                        recipeSnapshot.RecipePointer,
                        recipeSnapshot.RecipePointer,
                        recipeSnapshot.RecipeId,
                        recipeSnapshot.IngredientIds,
                        Array.Empty<int>(),
                        "");
                    intent.ButtonPointer = buttonPointer;
                }
                return true;
            }
            if (binding.State == BindingState.Tombstone)
            {
                _blockedSubmissions++;
                return false;
            }
            if (binding.State != BindingState.Ready
                || binding.PanelPointer != panelPointer
                || binding.RecipePointer != recipeSnapshot.RecipePointer
                || !TryValidateBindingLocked(binding, out var validatedPanel)
                || !ReferenceEquals(validatedPanel, panelState))
            {
                if (binding.RowKind == RowKind.Authoritative)
                {
                    if (_activeSubmit?.Kind == SubmitKind.Recipe)
                    {
                        _blockedSubmissions++;
                        _lastResult = "blocked stale authoritative selection during exact submit";
                        return false;
                    }
                    if (!HasMutationAwaitingNativeSwitchLocked(panelState)
                        && !TryRetirePriorTransactionLocked(panelState))
                    {
                        _blockedSubmissions++;
                        _lastResult = "blocked stale base selection after a variant mutation started";
                        return false;
                    }
                    Buttons.Remove(buttonPointer);
                    return true;
                }
                binding.State = BindingState.Tombstone;
                _blockedSubmissions++;
                return false;
            }

            if (binding.RowKind == RowKind.Synthetic && panelState.MutationUncertain)
            {
                binding.State = BindingState.Tombstone;
                _blockedSubmissions++;
                _lastResult = $"blocked synthetic selection after uncertain mutation: {panelState.MutationUncertainReason}";
                return false;
            }
            if (binding.RowKind == RowKind.Authoritative)
            {
                if (_activeSubmit?.Kind == SubmitKind.Recipe)
                {
                    _blockedSubmissions++;
                    _lastResult = "blocked authoritative selection during exact submit";
                    return false;
                }
                if (!HasMutationAwaitingNativeSwitchLocked(panelState)
                    && !TryRetirePriorTransactionLocked(panelState))
                {
                    _blockedSubmissions++;
                    _lastResult = "blocked base selection after a variant mutation started";
                    return false;
                }
                if (binding.Claims != RuntimeUiTargetKinds.None)
                {
                    TryLogTransaction(
                        panelState.BusinessGeneration,
                        $"selection-pass row=base panel={FormatPointer(panelPointer)} "
                        + $"epoch={panelState.PanelEpoch} recipe={binding.RecipeId} "
                        + $"recipePtr={FormatPointer(binding.RecipePointer)} "
                        + $"button={FormatPointer(binding.ButtonPointer)} claims={binding.Claims} "
                        + $"plan={binding.PlanIdentity}");
                }
                intent = new RecipeSelectionIntent(
                    panelState,
                    binding,
                    RecipeDestinationKind.Base,
                    binding.RecipePointer,
                    binding.RecipePointer,
                    binding.RecipeId,
                    recipeSnapshot.IngredientIds,
                    Array.Empty<int>(),
                    binding.PlanIdentity);
                return true;
            }
            if (binding.RowKind != RowKind.Synthetic
                || !panelState.ControlledRecipes.TryGetValue(binding.RecipeId, out controlled)
                || !controlled.Complete
                || !controlled.SyntheticPlans.TryGetValue(binding.RecipePointer, out plan)
                || !string.Equals(plan.Identity, binding.PlanIdentity, StringComparison.Ordinal))
            {
                binding.State = BindingState.Tombstone;
                _blockedSubmissions++;
                return false;
            }
        }

        var authoritativeMatches = currentRecipes.Where(candidate =>
            candidate.RecipePointer == controlled.AuthoritativePointer
            && candidate.RecipeId == controlled.RecipeId
            && candidate.IngredientIds.SequenceEqual(controlled.BaseIngredientIds)).ToArray();
        if (authoritativeMatches.Length != 1)
        {
            return TombstoneSelection(
                binding,
                "authoritative recipe is no longer unique in the current list");
        }
        var fullIngredients = controlled.BaseIngredientIds
            .Concat(plan!.ExtraIngredientIds)
            .ToArray();
        if (currentRecipes.Count(candidate =>
                candidate.RecipePointer == binding.RecipePointer
                && candidate.RecipeId == controlled.RecipeId
                && candidate.CookCount >= -1
                && candidate.IngredientIds.SequenceEqual(fullIngredients)) != 1)
        {
            return TombstoneSelection(
                binding,
                "source synthetic recipe is no longer unique in the current list");
        }

        lock (StateRoot)
        {
            if (!TryValidateBindingLocked(binding, out var currentPanel)
                || !ReferenceEquals(currentPanel, panelState)
                || !currentPanel.ControlledRecipes.TryGetValue(binding.RecipeId, out var currentControlled)
                || currentControlled.AuthoritativePointer != authoritativeMatches[0].RecipePointer
                || !currentControlled.SyntheticPlans.TryGetValue(binding.RecipePointer, out var currentPlan)
                || !string.Equals(currentPlan.Identity, binding.PlanIdentity, StringComparison.Ordinal))
            {
                binding.State = BindingState.Tombstone;
                _blockedSubmissions++;
                return false;
            }

            if (_activeSubmit != null)
            {
                var routingSwitch = HasMutationAwaitingNativeSwitchLocked(currentPanel)
                    && _activeSubmit.ButtonPointer == binding.ButtonPointer
                    && _activeSubmit.Kind is SubmitKind.None or SubmitKind.RecipeSwitch;
                if (!routingSwitch
                    && (_activeSubmit.Kind != SubmitKind.Recipe
                    || _activeSubmit.ButtonPointer != binding.ButtonPointer
                    || _activeSubmit.PanelPointer != currentPanel.PanelPointer
                    || _activeSubmit.SourceRecipePointer != binding.RecipePointer
                    || !TryGetTransactionLocked(
                        currentPanel.PanelPointer,
                        _activeSubmit.TransactionIdentity,
                        out var activePanel,
                        out var activeTransaction)
                    || !ReferenceEquals(activePanel, currentPanel)
                    || activeTransaction.State != TransactionState.PendingRecipeSubmit
                    || activeTransaction.PanelEpoch != currentPanel.PanelEpoch
                    || activeTransaction.SourceButtonPointer != binding.ButtonPointer
                    || activeTransaction.SourceRecipePointer != binding.RecipePointer
                    || activeTransaction.AuthoritativePointer != currentControlled.AuthoritativePointer
                    || !string.Equals(
                        activeTransaction.PlanIdentity,
                        currentPlan.Identity,
                        StringComparison.Ordinal)))
                {
                    binding.State = BindingState.Tombstone;
                    _blockedSubmissions++;
                    _lastResult = "blocked a different recipe identity during exact submit";
                    return false;
                }
            }
            else if (!HasMutationAwaitingNativeSwitchLocked(currentPanel)
                && !TryGetOrCreateRecipeTransactionLocked(
                    currentPanel,
                    binding,
                    out _))
            {
                binding.State = BindingState.Tombstone;
                _blockedSubmissions++;
                _lastResult = "could not establish exact recipe selection transaction";
                return false;
            }
            intent = new RecipeSelectionIntent(
                currentPanel,
                binding,
                RecipeDestinationKind.Variant,
                binding.RecipePointer,
                currentControlled.AuthoritativePointer,
                currentControlled.RecipeId,
                currentPlan.BaseIngredientIds,
                currentPlan.ExtraIngredientIds,
                currentPlan.Identity);
            _lastResult = $"routed exact variant {currentPlan.Identity} to authoritative recipe";
        }

        recipe = authoritativeMatches[0].Recipe;
        TryLogTransaction(
            panelState.BusinessGeneration,
            $"selection-route panel={FormatPointer(panelPointer)} epoch={panelState.PanelEpoch} "
            + $"recipe={binding.RecipeId} synthetic={FormatPointer(binding.RecipePointer)} "
            + $"authoritative={FormatPointer(controlled.AuthoritativePointer)} plan={binding.PlanIdentity}");
        return true;
    }

    private static bool TombstoneSelection(
        ButtonBinding binding,
        string reason)
    {
        lock (StateRoot)
        {
            if (Buttons.TryGetValue(binding.ButtonPointer, out var current)
                && ReferenceEquals(current, binding))
            {
                current.State = BindingState.Tombstone;
            }
            _blockedSubmissions++;
            _lastResult = reason;
        }
        BlockRecipeRow(reason);
        return false;
    }

    private static bool TryRetirePriorTransactionLocked(PanelState panel)
    {
        var transaction = panel.Transaction;
        if (transaction == null) return true;
        if (!panel.Retired
            && transaction.MutationStarted
            && transaction.State is not (TransactionState.Cancelled
                or TransactionState.Completed
                or TransactionState.Rejected))
        {
            return false;
        }
        if (TryGetOutputButtonForTransactionLocked(transaction, out var output))
        {
            output.State = BindingState.Tombstone;
        }
        if (transaction.OutputClosurePointer != 0
            && OutputClosures.TryGetValue(
                transaction.OutputClosurePointer,
                out var closure))
        {
            closure.State = BindingState.Tombstone;
        }
        panel.Transaction = null;
        return true;
    }

    private static bool TryGetOrCreateRecipeTransactionLocked(
        PanelState panel,
        ButtonBinding binding,
        out PanelTransaction transaction)
    {
        transaction = null!;
        if (binding.RowKind != RowKind.Synthetic
            || binding.PanelPointer != panel.PanelPointer
            || binding.PanelEpoch != panel.PanelEpoch
            || binding.TargetGeneration != panel.TargetSet.Generation
            || !panel.ControlledRecipes.TryGetValue(binding.RecipeId, out var controlled)
            || !controlled.Complete
            || !controlled.SyntheticPlans.TryGetValue(binding.RecipePointer, out var plan)
            || !string.Equals(plan.Identity, binding.PlanIdentity, StringComparison.Ordinal))
        {
            return false;
        }

        if (panel.Transaction is { } existing)
        {
            if (existing.State == TransactionState.PendingRecipeSubmit
                && existing.PanelEpoch == panel.PanelEpoch
                && existing.OriginTargetGeneration == panel.TargetSet.Generation
                && existing.SourceButtonPointer == binding.ButtonPointer
                && existing.SourceRecipePointer == binding.RecipePointer
                && existing.AuthoritativePointer == controlled.AuthoritativePointer
                && string.Equals(existing.PlanIdentity, plan.Identity, StringComparison.Ordinal))
            {
                transaction = existing;
                return true;
            }
            if (existing.State is not (TransactionState.Cancelled
                    or TransactionState.Completed
                    or TransactionState.Rejected)
                && (existing.MutationStarted
                || existing.State is TransactionState.Applying
                    or TransactionState.Applied
                    or TransactionState.OutputPending
                    or TransactionState.OutputReady
                    or TransactionState.OutputSubmitting
                    or TransactionState.Uncertain))
            {
                return false;
            }
            if (!TryRetirePriorTransactionLocked(panel)) return false;
        }

        transaction = new PanelTransaction(
            panel.PanelPointer,
            panel.PanelEpoch,
            panel.TargetSet.Generation,
            panel.BusinessGeneration,
            controlled.RecipeId,
            controlled.AuthoritativePointer,
            binding.ButtonPointer,
            binding.RecipePointer,
            plan);
        panel.Transaction = transaction;
        return true;
    }

    internal static bool BeginSubmitForTests(
        object button,
        out SubmitHookState state)
    {
        return BeginSubmit(button, out state);
    }

    internal static void CompleteSubmitForTests(
        SubmitHookState state,
        Exception? exception = null)
    {
        CompleteSubmit(state, exception);
    }

    private static bool BeginSubmit(object button, out SubmitHookState hookState)
    {
        hookState = new SubmitHookState();
        try
        {
            return BeginSubmitCore(button, out hookState);
        }
        catch (Exception ex)
        {
            var reason = $"submit probe threw: {DescribeException(ex)}";
            var buttonPointer = TryGetNativePointer(button);
            var failClosed = hookState.Kind != SubmitKind.None;
            lock (StateRoot)
            {
                if (hookState.TransactionSequence > 0
                    && TryGetTransactionLocked(
                        hookState.PanelPointer,
                        hookState.TransactionIdentity,
                        out _,
                        out var transaction)
                    && transaction.Sequence == hookState.TransactionSequence
                    && (hookState.Kind != SubmitKind.Recipe
                        || transaction.SourceRecipePointer == hookState.SourceRecipePointer))
                {
                    failClosed = true;
                    if (transaction.MutationStarted)
                    {
                        MarkTransactionUncertainLocked(transaction, reason);
                    }
                    else
                    {
                        TryRejectBeforeMutationLocked(transaction, reason);
                    }
                }
                if (hookState.ButtonBindingSequence > 0
                    && Buttons.TryGetValue(buttonPointer, out var binding)
                    && binding.Sequence == hookState.ButtonBindingSequence)
                {
                    if (binding.RowKind == RowKind.Authoritative)
                    {
                        Buttons.Remove(buttonPointer);
                    }
                    else
                    {
                        binding.State = BindingState.Tombstone;
                        failClosed = true;
                    }
                }
                if (failClosed)
                {
                    _blockedSubmissions++;
                    _lastResult = reason;
                }
            }
            if (hookState.ProbeInstalled
                && ReferenceEquals(_activeSubmit, hookState.Probe))
            {
                _activeSubmit = hookState.Probe?.Parent;
            }
            DisposePublicationLeaseNoThrow(hookState.PublicationLease);
            hookState.PublicationLease = null;
            if (hookState.Probe?.SwitchPublicationLease is { } switchLease)
            {
                DisposePublicationLeaseNoThrow(switchLease);
                hookState.Probe.SwitchPublicationLease = null;
            }
            return !failClosed;
        }
    }

    private static bool BeginSubmitCore(object button, out SubmitHookState hookState)
    {
        hookState = new SubmitHookState();
        var buttonPointer = TryGetNativePointer(button);
        if (buttonPointer == 0) return true;

        ButtonBinding? staleOutputBinding = null;
        PanelState? rearmPanel = null;
        lock (StateRoot)
        {
            if (Buttons.TryGetValue(buttonPointer, out var staleCandidate)
                && staleCandidate.RowKind == RowKind.Output
                && staleCandidate.State == BindingState.AwaitingRebind)
            {
                staleOutputBinding = staleCandidate;
                Buttons.Remove(buttonPointer);
                Panels.TryGetValue(staleCandidate.PanelPointer, out rearmPanel);
            }
        }
        if (staleOutputBinding != null)
        {
            var cleaned = TryCleanSubmitCallbackExclusive(
                button,
                buttonPointer,
                null,
                out var cleanError);
            if (!cleaned)
            {
                var restored = false;
                lock (StateRoot)
                {
                    if (!Buttons.ContainsKey(buttonPointer))
                    {
                        Buttons[buttonPointer] = staleOutputBinding;
                        restored = true;
                    }
                    _blockedSubmissions++;
                    _failures++;
                    _lastResult = $"tombstoned output callback cleanup failed: {cleanError}";
                }
                if (restored)
                {
                    try { _runtime.TryDisableButton(button, out _); } catch { }
                }
                return false;
            }

            var rearmContextChanged = false;
            long rearmBusinessGeneration = 0;
            lock (StateRoot)
            {
                RetireOutputBindingLocked(staleOutputBinding);
                var currentPanelExists = Panels.TryGetValue(
                    staleOutputBinding.PanelPointer,
                    out var currentPanel);
                rearmContextChanged = Buttons.ContainsKey(buttonPointer)
                    || (rearmPanel == null
                        ? currentPanelExists
                        : !currentPanelExists
                            || !ReferenceEquals(currentPanel, rearmPanel)
                            || currentPanel!.PanelEpoch != rearmPanel.PanelEpoch
                            || currentPanel.Retired);
                rearmBusinessGeneration = currentPanelExists
                    ? currentPanel!.BusinessGeneration
                    : 0;
                if (rearmContextChanged)
                {
                    _blockedSubmissions++;
                    _lastResult = "blocked tombstoned output rearm after its panel context changed";
                }
            }
            if (rearmContextChanged) return false;
            if (rearmBusinessGeneration > 0)
            {
                TryLogTransaction(
                    rearmBusinessGeneration,
                    $"output-button-rearmed button={FormatPointer(buttonPointer)} "
                    + $"oldEpoch={staleOutputBinding.PanelEpoch} "
                    + $"oldTransaction={staleOutputBinding.TransactionSequence}");
            }
        }

        ButtonBinding? binding;
        RuntimeUiTargetSetSnapshot? expectedTargetSet = null;
        var probe = new ActiveSubmitContext(buttonPointer);
        RecipeSelectionIntent? queuedSwitchIntent = null;
        lock (StateRoot)
        {
            if (_activeSubmit == null)
            {
                queuedSwitchIntent = Panels.Values
                    .Where(panel =>
                        !panel.Retired
                        && panel.SelectionIntent is { } intent
                        && intent.ButtonPointer == buttonPointer
                        && HasMutationAwaitingNativeSwitchLocked(panel)
                        && TryValidateSelectionIntentLocked(panel, intent))
                    .Select(panel => panel.SelectionIntent)
                    .SingleOrDefault();
                if (queuedSwitchIntent != null)
                {
                    _activeSubmit = probe;
                    hookState.Probe = probe;
                    hookState.ProbeInstalled = true;
                    hookState.OriginalAllowed = true;
                }
            }
        }
        if (queuedSwitchIntent != null)
        {
            hookState.Kind = SubmitKind.RecipeSwitch;
            hookState.PanelPointer = queuedSwitchIntent.PanelPointer;
            hookState.PanelEpoch = queuedSwitchIntent.PanelEpoch;
            if (!TryArmRecipeSwitch(queuedSwitchIntent, probe, out var switchError))
            {
                lock (StateRoot)
                {
                    if (ReferenceEquals(_activeSubmit, probe)) _activeSubmit = null;
                    _blockedSubmissions++;
                    _lastResult = $"blocked recipe switch arm: {switchError}";
                }
                hookState.ProbeInstalled = false;
                hookState.OriginalAllowed = false;
                TryLogTransaction(
                    queuedSwitchIntent.BusinessGeneration,
                    $"switch-rejected panel={FormatPointer(queuedSwitchIntent.PanelPointer)} "
                    + $"epoch={queuedSwitchIntent.PanelEpoch} recipe={queuedSwitchIntent.RecipeId} "
                    + $"button={FormatPointer(queuedSwitchIntent.ButtonPointer)} source=queued-submit "
                    + $"reason={switchError}");
                return false;
            }
            return true;
        }
        lock (StateRoot)
        {
            Buttons.TryGetValue(buttonPointer, out binding);
            hookState.ButtonBindingSequence = binding?.Sequence ?? 0;
            var parentProbe = _activeSubmit;
            var currentTracked = binding != null
                && binding.RowKind is RowKind.Synthetic or RowKind.Output;
            if (parentProbe != null
                && (parentProbe.Kind != SubmitKind.None || currentTracked))
            {
                _blockedSubmissions++;
                _lastResult = "blocked a nested submit that conflicts with an exact Mod transaction";
                return false;
            }

            probe.Parent = parentProbe;
            _activeSubmit = probe;
            hookState.Probe = probe;
            hookState.ProbeInstalled = true;
            if (binding == null)
            {
                hookState.OriginalAllowed = true;
                return true;
            }
            if (binding.RowKind == RowKind.Authoritative)
            {
                if (Panels.TryGetValue(binding.PanelPointer, out var authoritativePanel)
                    && !HasMutationAwaitingNativeSwitchLocked(authoritativePanel)
                    && !TryRetirePriorTransactionLocked(authoritativePanel))
                {
                    _blockedSubmissions++;
                    _lastResult = "blocked base submit after a variant mutation started";
                    return false;
                }
                PanelState? basePanel = null;
                var validBaseBinding = binding.State == BindingState.Ready
                    && TryValidateBindingLocked(binding, out basePanel);
                if (!validBaseBinding)
                {
                    Buttons.Remove(buttonPointer);
                }
                if (validBaseBinding
                    && binding.Claims != RuntimeUiTargetKinds.None)
                {
                    TryLogTransaction(
                        basePanel!.BusinessGeneration,
                        $"submit-armed row=base panel={FormatPointer(binding.PanelPointer)} "
                        + $"epoch={binding.PanelEpoch} recipe={binding.RecipeId} "
                        + $"recipePtr={FormatPointer(binding.RecipePointer)} "
                        + $"button={FormatPointer(binding.ButtonPointer)} claims={binding.Claims} "
                        + $"plan={binding.PlanIdentity}");
                }
                hookState.OriginalAllowed = true;
                return true;
            }
            if (binding.State == BindingState.Tombstone
                || binding.State != BindingState.Ready
                || !TryValidateBindingLocked(binding, out var panel))
            {
                _blockedSubmissions++;
                _lastResult = "blocked a stale exact recipe/output callback";
                return false;
            }
            if (panel.MutationUncertain)
            {
                binding.State = BindingState.Tombstone;
                _blockedSubmissions++;
                _lastResult = $"blocked exact submit after uncertain mutation: {panel.MutationUncertainReason}";
                return false;
            }

            if (binding.RowKind == RowKind.Synthetic)
            {
                if (HasMutationAwaitingNativeSwitchLocked(panel))
                {
                    hookState.OriginalAllowed = true;
                    return true;
                }
                if (!TryGetOrCreateRecipeTransactionLocked(
                        panel,
                        binding,
                        out var transaction))
                {
                    _blockedSubmissions++;
                    _lastResult = "blocked an exact recipe callback outside its transaction state";
                    return false;
                }

                expectedTargetSet = panel.TargetSet;
                hookState.PanelPointer = panel.PanelPointer;
                hookState.PanelEpoch = panel.PanelEpoch;
                hookState.TransactionSequence = transaction.Sequence;
                hookState.TransactionIdentity = transaction.Identity;
                hookState.SourceRecipePointer = transaction.SourceRecipePointer;
                hookState.Kind = SubmitKind.Recipe;
                probe.AttachRecipe(
                    panel.PanelPointer,
                    panel.PanelEpoch,
                    transaction.SourceRecipePointer,
                    transaction.Identity);
            }
            else if (binding.RowKind == RowKind.Output
                && panel.Transaction is { } transaction
                && transaction.State == TransactionState.OutputReady
                && transaction.OutputButtonPointer == buttonPointer
                && transaction.OutputClosurePointer != 0
                && OutputClosures.TryGetValue(
                    transaction.OutputClosurePointer,
                    out var closureBinding)
                && TryValidateOutputClosureLocked(
                    closureBinding,
                    TransactionState.OutputReady,
                    out var outputPanel,
                    out var outputTransaction)
                && ReferenceEquals(outputPanel, panel)
                && ReferenceEquals(outputTransaction, transaction))
            {
                hookState.PanelPointer = panel.PanelPointer;
                hookState.PanelEpoch = binding.PanelEpoch;
                hookState.TransactionSequence = transaction.Sequence;
                hookState.TransactionIdentity = transaction.Identity;
                hookState.Kind = SubmitKind.Output;
                hookState.OriginalAllowed = true;
                probe.AttachOutput(
                    panel.PanelPointer,
                    binding.PanelEpoch,
                    transaction.Identity);
                return true;
            }
            else
            {
                _blockedSubmissions++;
                _lastResult = "blocked an exact callback outside its transaction state";
                return false;
            }
        }

        if (!RuntimeUiPinningService.TryAcquireTargetRecipeVariantPublicationLease(
                expectedTargetSet!,
                out var publicationLease))
        {
            return RejectSubmit(hookState, "target changed before submit publication lease");
        }
        hookState.PublicationLease = publicationLease;

        long submitBusinessGeneration;
        int submitRecipeId;
        var publicationTokenValid = false;
        lock (StateRoot)
        {
            publicationTokenValid = TryGetTransactionLocked(
                    hookState.PanelPointer,
                    hookState.TransactionIdentity,
                    out var panel,
                    out var transaction)
                && ReferenceEquals(panel.TargetSet, expectedTargetSet)
                && ReferenceEquals(_activeSubmit, probe)
                && transaction.Sequence == hookState.TransactionSequence
                && transaction.SourceRecipePointer == hookState.SourceRecipePointer
                && Buttons.TryGetValue(buttonPointer, out var currentBinding)
                && ReferenceEquals(currentBinding, binding)
                && currentBinding.RecipePointer == hookState.SourceRecipePointer
                && currentBinding.State == BindingState.Ready;
        }
        if (!publicationTokenValid)
        {
            DisposePublicationLeaseNoThrow(publicationLease);
            hookState.PublicationLease = null;
            return RejectSubmit(hookState, "submit token changed at publication boundary");
        }

        if (!_runtime.TryWrapPanel(
                hookState.PanelPointer,
                out var panelWrapper,
                out var wrapError))
        {
            DisposePublicationLeaseNoThrow(publicationLease);
            hookState.PublicationLease = null;
            return RejectSubmit(hookState, $"fresh panel wrapper failed: {wrapError}");
        }

        string preflightError;
        bool preflightPassed;
        PanelTransaction? preflightTransaction;
        lock (StateRoot)
        {
            preflightPassed = TryGetTransactionLocked(
                hookState.PanelPointer,
                hookState.TransactionIdentity,
                out _,
                out var transaction)
                && transaction.Sequence == hookState.TransactionSequence
                && transaction.SourceRecipePointer == hookState.SourceRecipePointer;
            preflightTransaction = preflightPassed ? transaction : null;
            preflightError = preflightPassed ? "" : "transaction changed before preflight";
        }
        if (preflightPassed)
        {
            preflightPassed = TryProjectedRecipePreflight(
                panelWrapper,
                preflightTransaction!,
                out preflightError);
        }
        if (!preflightPassed)
        {
            DisposePublicationLeaseNoThrow(publicationLease);
            hookState.PublicationLease = null;
            return RejectSubmit(hookState, preflightError);
        }

        var postflightValid = false;
        lock (StateRoot)
        {
            postflightValid = TryGetTransactionLocked(
                    hookState.PanelPointer,
                    hookState.TransactionIdentity,
                out var panel,
                out var transaction)
                && ReferenceEquals(panel.TargetSet, expectedTargetSet)
                && ReferenceEquals(_activeSubmit, probe)
                && transaction.Sequence == hookState.TransactionSequence
                && transaction.SourceRecipePointer == hookState.SourceRecipePointer;
            submitBusinessGeneration = postflightValid ? transaction.BusinessGeneration : 0;
            submitRecipeId = postflightValid ? transaction.RecipeId : -1;
            if (postflightValid) hookState.OriginalAllowed = true;
        }
        if (!postflightValid)
        {
            DisposePublicationLeaseNoThrow(publicationLease);
            hookState.PublicationLease = null;
            return RejectSubmit(hookState, "transaction changed after fresh preflight");
        }
        TryLogTransaction(
            submitBusinessGeneration,
            $"submit-armed kind={hookState.Kind} panel={FormatPointer(hookState.PanelPointer)} "
            + $"epoch={hookState.PanelEpoch} recipe={submitRecipeId} "
            + $"source={FormatPointer(hookState.SourceRecipePointer)} "
            + $"button={FormatPointer(buttonPointer)} transaction={hookState.TransactionSequence} "
            + $"plan={binding.PlanIdentity}");
        return true;
    }

    private static bool TryProjectedRecipePreflight(
        object panel,
        PanelTransaction transaction,
        out string error)
    {
        error = "";
        if (!_runtime.TryReadPanelSelectionState(panel, out var state, out error)
            || state.PanelPointer != transaction.PanelPointer)
        {
            error = string.IsNullOrEmpty(error)
                ? "fresh panel selection identity changed"
                : error;
            return false;
        }
        if (!_runtime.TryReadRecipeList(
                panel,
                MaximumRecipeCount,
                out _,
                out var recipes,
                out error))
        {
            return false;
        }
        if (recipes.Count(recipe =>
                recipe.RecipePointer == transaction.AuthoritativePointer
                && recipe.RecipeId == transaction.RecipeId
                && recipe.IngredientIds.SequenceEqual(transaction.BaseIngredientIds)) != 1)
        {
            error = "fresh authoritative recipe identity changed";
            return false;
        }

        var fullIngredients = transaction.BaseIngredientIds
            .Concat(transaction.ExtraIngredientIds)
            .ToArray();
        if (recipes.Count(recipe =>
                recipe.RecipePointer == transaction.SourceRecipePointer
                && recipe.RecipeId == transaction.RecipeId
                && recipe.CookCount >= -1
                && recipe.IngredientIds.SequenceEqual(fullIngredients)) != 1)
        {
            error = "fresh source synthetic recipe identity changed";
            return false;
        }
        if (fullIngredients.Length == 0 || fullIngredients.Length > MaximumIngredientSlots)
        {
            error = "projected recipe violates the five-slot limit";
            return false;
        }
        if (state.IsFreeCook) return true;
        if (state.ExtraCostIngredient <= 0)
        {
            error = "extra ingredient cost multiplier is not positive";
            return false;
        }

        try
        {
            var required = ExpandIngredients(fullIngredients, state.ExtraCostIngredient);
            var returned = ExpandIngredients(
                state.SelectedIngredientIds,
                state.ExtraCostIngredient);
            var count = CalculateCookCount(required, returned, _runtime.GetIngredientQuantity);
            if (count == 0)
            {
                error = "projected base and extra ingredient inventory is insufficient";
                return false;
            }
        }
        catch (Exception ex)
        {
            error = $"projected inventory preflight failed: {DescribeException(ex)}";
            return false;
        }
        return true;
    }

    private static bool TryOutputPreflight(
        object panel,
        PanelTransaction transaction,
        out string error)
    {
        error = "";
        if (transaction.OutputComboPointer == 0
            || transaction.OutputButtonPointer == 0
            || transaction.OutputClosurePointer == 0
            || transaction.OutputPointer == 0)
        {
            error = "output binding has no exact native identity";
            return false;
        }
        if (!_runtime.TryReadPanelSelectionState(panel, out var state, out error)
            || state.PanelPointer != transaction.PanelPointer
            || !transaction.MutationReceiptConfirmed
            || TryGetNativePointer(state.SelectedIngredientList)
                != transaction.SelectedIngredientListPointer
            || state.IsFreeCook != transaction.IsFreeCook
            || state.ExtraCostIngredient != transaction.ExtraCostMultiplier
            || !state.SelectedIngredientIds.SequenceEqual(
                transaction.BaseIngredientIds.Concat(transaction.ExtraIngredientIds)))
        {
            error = string.IsNullOrEmpty(error)
                ? "output panel state no longer matches the exact recipe variant"
                : error;
            return false;
        }
        if (!_runtime.TryWrapMatchedCombo(
                transaction.OutputComboPointer,
                out var combo,
                out error)
            || !_runtime.TryReadMatchedCombo(combo, out var comboState, out error)
            || comboState.RecipePointer != transaction.AuthoritativePointer
            || comboState.RecipeId != transaction.RecipeId
            || !comboState.OrderedModifierIngredientIds.SequenceEqual(
                transaction.ExtraIngredientIds))
        {
            error = string.IsNullOrEmpty(error)
                ? "fresh output combo no longer matches the exact recipe variant"
                : error;
            return false;
        }
        return true;
    }

    private static int[] ExpandIngredients(
        IReadOnlyList<int> ingredientIds,
        int multiplier)
    {
        var count = checked(ingredientIds.Count * multiplier);
        if (count > 64)
        {
            throw new InvalidOperationException("expanded ingredient debit exceeds 64 entries");
        }
        var expanded = new int[count];
        var index = 0;
        foreach (var ingredientId in ingredientIds)
        {
            for (var repeat = 0; repeat < multiplier; repeat += 1)
            {
                expanded[index++] = ingredientId;
            }
        }
        return expanded;
    }

    private static bool RejectSubmit(SubmitHookState hookState, string reason)
    {
        long businessGeneration = 0;
        var recipeId = -1;
        lock (StateRoot)
        {
            if (TryGetTransactionLocked(
                    hookState.PanelPointer,
                    hookState.TransactionIdentity,
                out _,
                out var transaction)
                && transaction.Sequence == hookState.TransactionSequence
                && (hookState.Kind != SubmitKind.Recipe
                    || transaction.SourceRecipePointer == hookState.SourceRecipePointer))
            {
                businessGeneration = transaction.BusinessGeneration;
                recipeId = transaction.RecipeId;
                if (transaction.MutationStarted)
                {
                    MarkTransactionUncertainLocked(transaction, reason);
                }
                else
                {
                    TryRejectBeforeMutationLocked(transaction, reason);
                }
            }
            _blockedSubmissions++;
            _lastResult = reason;
        }
        TryLogTransaction(
            businessGeneration,
            $"submit-rejected kind={hookState.Kind} panel={FormatPointer(hookState.PanelPointer)} "
            + $"epoch={hookState.PanelEpoch} recipe={recipeId} reason={reason}");
        return false;
    }

    private static void CompleteSubmit(
        SubmitHookState hookState,
        Exception? exception)
    {
        try
        {
            if (hookState.Probe is { SwitchAttemptSequence: > 0 } switchProbe)
            {
                CompleteRecipeSwitchSubmit(switchProbe, exception);
                return;
            }
            if (hookState.Kind != SubmitKind.Recipe) return;
            lock (StateRoot)
            {
                if (!hookState.OriginalAllowed
                    || !TryGetTransactionLocked(
                        hookState.PanelPointer,
                        hookState.TransactionIdentity,
                        out _,
                        out var transaction)
                    || transaction.Sequence != hookState.TransactionSequence
                    || transaction.SourceRecipePointer != hookState.SourceRecipePointer)
                {
                    return;
                }

                if (exception != null
                    && transaction.State is not (TransactionState.Cancelled
                        or TransactionState.Completed
                        or TransactionState.Rejected))
                {
                    if (transaction.MutationStarted)
                    {
                        MarkTransactionUncertainLocked(
                            transaction,
                            $"native submit failed after mutation: {DescribeException(exception)}");
                    }
                    else
                    {
                        TryRejectBeforeMutationLocked(
                            transaction,
                            $"native submit failed before mutation: {DescribeException(exception)}");
                    }
                    return;
                }

                if (!transaction.MutationStarted
                    && transaction.State == TransactionState.PendingRecipeSubmit)
                {
                    _lastResult = "recipe submit returned before consuming the armed callback";
                    TryLogTransaction(
                        transaction.BusinessGeneration,
                        $"submit-unconsumed panel={FormatPointer(transaction.PanelPointer)} "
                        + $"epoch={transaction.PanelEpoch} recipe={transaction.RecipeId}");
                }
                else if (transaction.State == TransactionState.Applying)
                {
                    MarkTransactionUncertainLocked(
                        transaction,
                        "recipe submit returned while the extra transaction was still applying");
                }
            }
        }
        finally
        {
            if (hookState.ProbeInstalled
                && ReferenceEquals(_activeSubmit, hookState.Probe))
            {
                _activeSubmit = hookState.Probe?.Parent;
            }
            DisposePublicationLeaseNoThrow(hookState.PublicationLease);
            hookState.PublicationLease = null;
            if (hookState.Probe?.SwitchPublicationLease is { } switchLease)
            {
                DisposePublicationLeaseNoThrow(switchLease);
                hookState.Probe.SwitchPublicationLease = null;
            }
        }
    }

    private static void CompleteRecipeSwitchSubmit(
        ActiveSubmitContext active,
        Exception? exception)
    {
        lock (StateRoot)
        {
            var retainedAttempt = active.SwitchAttemptToken as RecipeSwitchAttempt;
            if (!Panels.TryGetValue(active.PanelPointer, out var panel)
                || panel.SwitchAttempt is not { } attempt
                || attempt.Sequence != active.SwitchAttemptSequence
                || !string.Equals(
                    attempt.Identity,
                    active.SwitchAttemptIdentity,
                    StringComparison.Ordinal))
            {
                if (retainedAttempt != null
                    && retainedAttempt.Sequence == active.SwitchAttemptSequence
                    && string.Equals(
                        retainedAttempt.Identity,
                        active.SwitchAttemptIdentity,
                        StringComparison.Ordinal))
                {
                    MarkSwitchAttemptUncertainLocked(
                        retainedAttempt,
                        "recipe switch submit lost its exact retained attempt");
                }
                else if (active.SwitchBusinessGeneration > 0)
                {
                    const string reason = "recipe switch submit lost all managed attempt evidence";
                    LatchBusinessMutationUncertainLocked(
                        active.SwitchBusinessGeneration,
                        reason);
                    _uncertainTransactions++;
                    _lastResult = reason;
                }
                return;
            }

            if (attempt.State == RecipeSwitchAttemptState.Armed)
            {
                if (exception == null)
                {
                    panel.SwitchAttempt = null;
                    _lastResult = "recipe switch callback was not consumed";
                    TryLogTransaction(
                        attempt.BusinessGeneration,
                        $"switch-unconsumed panel={FormatPointer(attempt.PanelPointer)} "
                        + $"attempt={attempt.Sequence}",
                        TransactionLogKind.Action);
                }
                else
                {
                    MarkSwitchAttemptUncertainLocked(
                        attempt,
                        $"native recipe switch failed before a receipt was observed: {DescribeException(exception)}");
                }
                return;
            }

            if (exception != null)
            {
                MarkSwitchAttemptUncertainLocked(
                    attempt,
                    $"native recipe switch failed after its receipt: {DescribeException(exception)}");
                return;
            }
            if (attempt.State != RecipeSwitchAttemptState.VisualCompleted)
            {
                MarkSwitchAttemptUncertainLocked(
                    attempt,
                    "native recipe switch returned without completing its visual refresh");
                return;
            }

            if (attempt.SourceTransaction.State != TransactionState.Switching)
            {
                MarkSwitchAttemptUncertainLocked(
                    attempt,
                    "recipe switch source transaction changed before completion");
                return;
            }
            attempt.SourceTransaction.State = TransactionState.Cancelled;
            attempt.State = RecipeSwitchAttemptState.Completed;
            panel.SwitchAttempt = null;
            panel.SelectionIntent = null;
            _cancelledTransactions++;
            _lastResult = "cancelled prior recipe variant after exact native switch";
            TryLogTransaction(
                attempt.BusinessGeneration,
                $"switch-cancelled panel={FormatPointer(attempt.PanelPointer)} "
                + $"attempt={attempt.Sequence} source={attempt.SourceTransaction.Sequence} "
                + $"destination={attempt.Destination.DestinationKind}:"
                + $"{attempt.Destination.RecipeId}",
                TransactionLogKind.Critical);
        }
    }

    private static bool TryGetTransactionLocked(
        nint panelPointer,
        string transactionIdentity,
        out PanelState panel,
        out PanelTransaction transaction)
    {
        panel = null!;
        transaction = null!;
        if (panelPointer == 0
            || transactionIdentity.Length == 0
            || !Panels.TryGetValue(panelPointer, out var foundPanel)
            || foundPanel.Retired
            || foundPanel.Transaction is not { } candidate
            || !string.Equals(candidate.Identity, transactionIdentity, StringComparison.Ordinal))
        {
            return false;
        }
        panel = foundPanel;
        transaction = candidate;
        return true;
    }

    private static void DisposePublicationLeaseNoThrow(
        TargetRecipeVariantPublicationLease? publicationLease)
    {
        if (publicationLease == null) return;
        try { publicationLease.Dispose(); } catch { }
    }

    internal static void ApplyExtrasForTests(
        object panel,
        out UpdateVisualHookState state)
    {
        ApplyExtrasDuringNativeRefresh(panel, out state);
    }

    internal static void CompleteUpdateVisualForTests(
        UpdateVisualHookState state,
        Exception? exception = null)
    {
        object? panel = null;
        if (state.PanelPointer != 0)
        {
            try { _runtime.TryWrapPanel(state.PanelPointer, out panel, out _); } catch { }
        }
        CompleteUpdateVisual(panel, state, exception);
    }

    private static void ApplyExtrasDuringNativeRefresh(
        object panel,
        out UpdateVisualHookState hookState)
    {
        hookState = default;
        var panelPointer = TryGetNativePointer(panel);
        var active = _activeSubmit;
        if (panelPointer != 0
            && active != null
            && active.PanelPointer == panelPointer
            && active.SwitchAttemptSequence > 0)
        {
            RecipeSwitchAttempt? switchAttempt;
            lock (StateRoot)
            {
                switchAttempt = Panels.TryGetValue(panelPointer, out var switchPanel)
                    && switchPanel.SwitchAttempt is { } candidate
                    && candidate.Sequence == active.SwitchAttemptSequence
                    && string.Equals(
                        candidate.Identity,
                        active.SwitchAttemptIdentity,
                        StringComparison.Ordinal)
                    && candidate.State == RecipeSwitchAttemptState.Armed
                        ? candidate
                        : null;
            }
            if (switchAttempt != null)
            {
                ApplyRecipeSwitchDuringNativeRefresh(
                    panel,
                    active,
                    switchAttempt,
                    out hookState);
                return;
            }
        }
        if (panelPointer == 0
            || active == null
            || active.Kind != SubmitKind.Recipe
            || active.PanelPointer != panelPointer)
        {
            return;
        }

        PanelTransaction transaction;
        RuntimeUiTargetSetSnapshot expectedTargetSet;
        lock (StateRoot)
        {
            if (!TryGetTransactionLocked(
                    panelPointer,
                    active.TransactionIdentity,
                    out var panelState,
                    out transaction)
                || transaction.SourceRecipePointer != active.SourceRecipePointer)
            {
                throw new InvalidOperationException(
                    "recipe variant transaction disappeared during native refresh");
            }
            if (transaction.State == TransactionState.Applied
                || transaction.State == TransactionState.OutputPending
                || transaction.State == TransactionState.OutputReady)
            {
                hookState = new UpdateVisualHookState(
                    panelPointer,
                    transaction.Sequence,
                    transaction.Identity,
                    false,
                    true);
                return;
            }
            if (transaction.State != TransactionState.PendingRecipeSubmit)
            {
                throw RejectNested(
                    transaction,
                    "recipe variant transaction entered native refresh in an invalid state");
            }
            expectedTargetSet = panelState.TargetSet;
        }

        if (!ReferenceEquals(RuntimeUiPinningService.ReadTargetSet(), expectedTargetSet))
        {
            throw RejectNested(transaction, "target changed before nested recipe refresh");
        }
        if (!_runtime.TryReadPanelCookingState(panel, out var state, out var stateError)
            || state.PanelPointer != panelPointer
            || state.ImportedRecipePointer != transaction.AuthoritativePointer
            || state.ImportedRecipeId != transaction.RecipeId
            || !state.ImportedIngredientIds.SequenceEqual(transaction.BaseIngredientIds)
            || !state.SelectedIngredientIds.SequenceEqual(transaction.BaseIngredientIds))
        {
            throw RejectNested(
                transaction,
                string.IsNullOrEmpty(stateError)
                    ? "native base import no longer matches the exact authoritative recipe"
                    : stateError);
        }
        var selectedListPointer = TryGetNativePointer(state.SelectedIngredientList);
        if (selectedListPointer == 0)
        {
            throw RejectNested(transaction, "selected ingredient list has no native identity");
        }
        if (transaction.BaseIngredientIds.Length + transaction.ExtraIngredientIds.Length
            > MaximumIngredientSlots)
        {
            throw RejectNested(transaction, "recipe variant exceeds the five-slot limit");
        }

        int[] debitEntries = Array.Empty<int>();
        Dictionary<int, int> beforeQuantities = new();
        if (!state.IsFreeCook)
        {
            if (state.ExtraCostIngredient <= 0)
            {
                throw RejectNested(transaction, "extra ingredient cost multiplier is not positive");
            }
            try
            {
                debitEntries = ExpandIngredients(
                    transaction.ExtraIngredientIds,
                    state.ExtraCostIngredient);
                foreach (var group in debitEntries.GroupBy(id => id))
                {
                    var quantity = _runtime.GetIngredientQuantity(group.Key);
                    if (quantity < -1)
                    {
                        throw new InvalidOperationException(
                            $"ingredient {group.Key} returned invalid quantity {quantity}");
                    }
                    if (quantity >= 0 && quantity < group.Count())
                    {
                        throw new InvalidOperationException(
                            $"ingredient {group.Key} inventory is insufficient");
                    }
                    beforeQuantities.Add(group.Key, quantity);
                }
            }
            catch (Exception ex)
            {
                throw RejectNested(
                    transaction,
                    $"nested extra inventory preflight failed: {DescribeException(ex)}");
            }
        }

        lock (StateRoot)
        {
            if (!TryGetTransactionLocked(
                    panelPointer,
                    transaction.Identity,
                    out var currentPanel,
                    out var current)
                || !ReferenceEquals(current, transaction)
                || !ReferenceEquals(currentPanel.TargetSet, expectedTargetSet)
                || transaction.State != TransactionState.PendingRecipeSubmit)
            {
                throw RejectNested(
                    transaction,
                    "recipe variant transaction changed before native mutation");
            }
            transaction.State = TransactionState.Applying;
            transaction.MutationStarted = true;
        }

        try
        {
            if (!state.IsFreeCook)
            {
                // One native debit call is the only extra-inventory mutation.
                _runtime.DebitIngredients(debitEntries);
                foreach (var group in debitEntries.GroupBy(id => id))
                {
                    var afterQuantity = _runtime.GetIngredientQuantity(group.Key);
                    var beforeQuantity = beforeQuantities[group.Key];
                    if (beforeQuantity == -1 ? afterQuantity != -1 : afterQuantity != beforeQuantity - group.Count())
                    {
                        throw new InvalidOperationException(
                            $"ingredient {group.Key} debit verification failed");
                    }
                }
            }

            // One AddRange call is the only selected-ingredient mutation.
            _runtime.AddSelectedIngredients(
                state.SelectedIngredientList,
                transaction.ExtraIngredientIds);
            if (!_runtime.TryReadPanelCookingState(panel, out var afterState, out var afterError)
                || afterState.PanelPointer != panelPointer
                || TryGetNativePointer(afterState.SelectedIngredientList) != selectedListPointer
                || afterState.ImportedRecipePointer != transaction.AuthoritativePointer
                || afterState.ImportedRecipeId != transaction.RecipeId
                || !afterState.ImportedIngredientIds.SequenceEqual(transaction.BaseIngredientIds)
                || !afterState.SelectedIngredientIds.SequenceEqual(
                    transaction.BaseIngredientIds.Concat(transaction.ExtraIngredientIds)))
            {
                throw new InvalidOperationException(
                    string.IsNullOrEmpty(afterError)
                        ? "selected ingredient verification failed after AddRange"
                        : afterError);
            }
        }
        catch (Exception ex)
        {
            MarkTransactionUncertain(
                transaction,
                $"native extra transaction result is uncertain: {DescribeException(ex)}");
            throw;
        }

        lock (StateRoot)
        {
            if (transaction.State == TransactionState.Applying)
            {
                transaction.SelectedIngredientListPointer = selectedListPointer;
                transaction.IsFreeCook = state.IsFreeCook;
                transaction.ExtraCostMultiplier = state.ExtraCostIngredient;
                transaction.MutationReceiptConfirmed = true;
                transaction.State = TransactionState.Applied;
                _lastResult = $"applied exact extras for {transaction.PlanIdentity}";
            }
        }
        hookState = new UpdateVisualHookState(
            panelPointer,
            transaction.Sequence,
            transaction.Identity,
            true,
            true);
        TryLogTransaction(
            transaction.BusinessGeneration,
            $"extras-applied panel={FormatPointer(panelPointer)} epoch={transaction.PanelEpoch} "
            + $"recipe={transaction.RecipeId} extras={string.Join(",", transaction.ExtraIngredientIds)} "
            + $"multiplier={state.ExtraCostIngredient} free={state.IsFreeCook}");
    }

    private static void ApplyRecipeSwitchDuringNativeRefresh(
        object panel,
        ActiveSubmitContext active,
        RecipeSwitchAttempt attempt,
        out UpdateVisualHookState hookState)
    {
        hookState = default;
        if (!_runtime.TryReadPanelCookingState(panel, out var state, out var stateError)
            || state.PanelPointer != attempt.PanelPointer
            || TryGetNativePointer(state.SelectedIngredientList)
                != attempt.SelectedIngredientListPointer
            || state.ImportedRecipePointer
                != attempt.Destination.AuthoritativeRecipePointer
            || state.ImportedRecipeId != attempt.Destination.RecipeId
            || !state.ImportedIngredientIds.SequenceEqual(
                attempt.Destination.BaseIngredientIds)
            || !state.SelectedIngredientIds.SequenceEqual(
                attempt.Destination.BaseIngredientIds)
            || state.IsFreeCook != attempt.IsFreeCook
            || state.ExtraCostIngredient != attempt.ExtraCostMultiplier)
        {
            var reason = string.IsNullOrEmpty(stateError)
                ? "native recipe switch did not expose the exact imported base receipt"
                : stateError;
            lock (StateRoot) MarkSwitchAttemptUncertainLocked(attempt, reason);
            throw new InvalidOperationException(reason);
        }

        if (!attempt.IsFreeCook)
        {
            try
            {
                var multiplier = attempt.ExtraCostMultiplier;
                var oldIngredients = attempt.SourceTransaction.BaseIngredientIds
                    .Concat(attempt.SourceTransaction.ExtraIngredientIds)
                    .ToArray();
                foreach (var pair in attempt.InventoryBefore)
                {
                    var after = _runtime.GetIngredientQuantity(pair.Key);
                    var expected = pair.Value == -1
                        ? -1
                        : checked(
                            pair.Value
                            + checked(multiplier * oldIngredients.Count(id => id == pair.Key))
                            - checked(multiplier * attempt.Destination.BaseIngredientIds.Count(
                                id => id == pair.Key)));
                    if (after != expected)
                    {
                        throw new InvalidOperationException(
                            $"ingredient {pair.Key} switch inventory receipt expected {expected} but found {after}");
                    }
                }
            }
            catch (Exception ex)
            {
                var reason = $"native recipe switch inventory receipt failed: {DescribeException(ex)}";
                lock (StateRoot) MarkSwitchAttemptUncertainLocked(attempt, reason);
                throw new InvalidOperationException(reason, ex);
            }
        }

        PanelTransaction? destinationTransaction = null;
        lock (StateRoot)
        {
            if (!Panels.TryGetValue(attempt.PanelPointer, out var currentPanel)
                || currentPanel.Retired
                || !ReferenceEquals(currentPanel.SwitchAttempt, attempt)
                || attempt.State != RecipeSwitchAttemptState.Armed
                || !ReferenceEquals(currentPanel.Transaction, attempt.SourceTransaction)
                || attempt.SourceTransaction.State != attempt.SourceStateAtArm
                || (attempt.SourceStateAtArm == TransactionState.OutputReady
                    && (attempt.SourceTransaction.OutputClosurePointer == 0
                        || !OutputClosures.TryGetValue(
                            attempt.SourceTransaction.OutputClosurePointer,
                            out var currentSourceClosure)
                        || !TryValidateOutputClosureLocked(
                            currentSourceClosure,
                            TransactionState.OutputReady,
                            out var currentSourcePanel,
                            out var currentSourceTransaction)
                        || !ReferenceEquals(currentSourcePanel, currentPanel)
                        || !ReferenceEquals(
                            currentSourceTransaction,
                            attempt.SourceTransaction)))
                || !ReferenceEquals(_activeSubmit, active)
                || active.SwitchAttemptSequence != attempt.Sequence
                || !string.Equals(
                    active.SwitchAttemptIdentity,
                    attempt.Identity,
                    StringComparison.Ordinal)
                || !TryValidateSelectionIntentLocked(
                    currentPanel,
                    attempt.Destination))
            {
                const string reason = "recipe switch token changed before receipt commit";
                MarkSwitchAttemptUncertainLocked(attempt, reason);
                throw new InvalidOperationException(reason);
            }

            if (attempt.SourceTransaction.State == TransactionState.OutputReady)
            {
                ResetReadyOutputBindingLocked(attempt.SourceTransaction);
            }
            attempt.SourceTransaction.State = TransactionState.Switching;
            if (attempt.Destination.DestinationKind == RecipeDestinationKind.Variant)
            {
                if (!Buttons.TryGetValue(
                        attempt.Destination.ButtonPointer,
                        out var destinationBinding)
                    || destinationBinding.Sequence
                        != attempt.Destination.ButtonBindingSequence
                    || destinationBinding.State != BindingState.Ready
                    || !currentPanel.ControlledRecipes.TryGetValue(
                        attempt.Destination.RecipeId,
                        out var controlled)
                    || !controlled.SyntheticPlans.TryGetValue(
                        attempt.Destination.SourceRowRecipePointer,
                        out var destinationPlan)
                    || !string.Equals(
                        destinationPlan.Identity,
                        attempt.Destination.PlanIdentity,
                        StringComparison.Ordinal))
                {
                    const string reason = "variant destination changed before switch receipt commit";
                    MarkSwitchAttemptUncertainLocked(attempt, reason);
                    throw new InvalidOperationException(reason);
                }
                destinationTransaction = new PanelTransaction(
                    currentPanel.PanelPointer,
                    currentPanel.PanelEpoch,
                    currentPanel.TargetSet.Generation,
                    currentPanel.BusinessGeneration,
                    controlled.RecipeId,
                    controlled.AuthoritativePointer,
                    destinationBinding.ButtonPointer,
                    destinationBinding.RecipePointer,
                    destinationPlan);
                currentPanel.Transaction = destinationTransaction;
                attempt.DestinationTransactionSequence = destinationTransaction.Sequence;
                attempt.DestinationTransactionIdentity = destinationTransaction.Identity;
                active.AttachRecipe(
                    currentPanel.PanelPointer,
                    currentPanel.PanelEpoch,
                    destinationTransaction.SourceRecipePointer,
                    destinationTransaction.Identity);
            }
            else
            {
                currentPanel.Transaction = null;
            }
            attempt.State = RecipeSwitchAttemptState.ReceiptObserved;
            _lastResult = "observed exact native recipe switch receipt";
            TryLogTransaction(
                attempt.BusinessGeneration,
                $"switch-receipt panel={FormatPointer(attempt.PanelPointer)} "
                + $"epoch={currentPanel.PanelEpoch} source={attempt.SourceTransaction.Sequence} "
                + $"destination={attempt.Destination.DestinationKind}:"
                + $"{attempt.Destination.RecipeId} attempt={attempt.Sequence}",
                TransactionLogKind.Critical);
        }

        if (destinationTransaction != null)
        {
            try
            {
                ApplyExtrasDuringNativeRefresh(panel, out hookState);
                hookState = hookState with
                {
                    SwitchAttemptSequence = attempt.Sequence,
                    SwitchAttemptIdentity = attempt.Identity,
                };
            }
            catch
            {
                lock (StateRoot)
                {
                    MarkSwitchAttemptUncertainLocked(
                        attempt,
                        "variant destination failed after native switch receipt");
                }
                throw;
            }
        }
        else
        {
            hookState = new UpdateVisualHookState(
                attempt.PanelPointer,
                attempt.SourceTransaction.Sequence,
                attempt.SourceTransaction.Identity,
                false,
                false,
                attempt.Sequence,
                attempt.Identity);
        }
    }

    private static void MarkSwitchAttemptUncertainLocked(
        RecipeSwitchAttempt attempt,
        string reason)
    {
        if (attempt.State is RecipeSwitchAttemptState.Completed
            or RecipeSwitchAttemptState.Uncertain)
        {
            return;
        }
        attempt.State = RecipeSwitchAttemptState.Uncertain;
        LatchBusinessMutationUncertainLocked(attempt.BusinessGeneration, reason);
        if (attempt.SourceTransaction.State is not (TransactionState.Cancelled
            or TransactionState.Completed
            or TransactionState.Rejected
            or TransactionState.Uncertain))
        {
            attempt.SourceTransaction.State = TransactionState.Uncertain;
            _uncertainTransactions++;
        }
        var ownsCurrentPanel = Panels.TryGetValue(attempt.PanelPointer, out var panel)
            && panel.BusinessGeneration == attempt.BusinessGeneration
            && panel.TargetSet.Generation == attempt.TargetGeneration
            && ReferenceEquals(panel.SwitchAttempt, attempt);
        if (ownsCurrentPanel)
        {
            panel!.MutationUncertain = true;
            panel.MutationUncertainReason = reason;
            if (panel.Transaction is { } destination
                && !ReferenceEquals(destination, attempt.SourceTransaction)
                && attempt.DestinationTransactionSequence > 0
                && destination.Sequence == attempt.DestinationTransactionSequence
                && string.Equals(
                    destination.Identity,
                    attempt.DestinationTransactionIdentity,
                    StringComparison.Ordinal)
                && destination.State is not (TransactionState.Cancelled
                    or TransactionState.Completed
                    or TransactionState.Rejected
                    or TransactionState.Uncertain))
            {
                destination.State = TransactionState.Uncertain;
                _uncertainTransactions++;
            }
        }
        if (TryGetOutputButtonForTransactionLocked(
                attempt.SourceTransaction,
                out var output)
            && output.TransactionSequence == attempt.SourceTransaction.Sequence
            && string.Equals(
                output.TransactionIdentity,
                attempt.SourceTransaction.Identity,
                StringComparison.Ordinal))
        {
            output.State = BindingState.Tombstone;
        }
        if (attempt.SourceTransaction.OutputClosurePointer != 0
            && OutputClosures.TryGetValue(
                attempt.SourceTransaction.OutputClosurePointer,
                out var closure)
            && closure.TransactionSequence == attempt.SourceTransaction.Sequence
            && string.Equals(
                closure.TransactionIdentity,
                attempt.SourceTransaction.Identity,
                StringComparison.Ordinal)
            && closure.ButtonPointer == attempt.SourceTransaction.OutputButtonPointer
            && Buttons.TryGetValue(closure.ButtonPointer, out var closureButton)
            && closureButton.Sequence == closure.ButtonBindingSequence
            && closureButton.TransactionSequence == closure.TransactionSequence
            && string.Equals(
                closureButton.TransactionIdentity,
                closure.TransactionIdentity,
                StringComparison.Ordinal))
        {
            closure.State = BindingState.Tombstone;
        }
        _lastResult = reason;
        TryLogTransaction(
            attempt.BusinessGeneration,
            $"switch-uncertain panel={FormatPointer(attempt.PanelPointer)} "
            + $"attempt={attempt.Sequence} reason={reason}",
            TransactionLogKind.Critical);
    }

    private static Exception RejectNested(
        PanelTransaction transaction,
        string reason)
    {
        lock (StateRoot)
        {
            if (transaction.MutationStarted)
            {
                MarkTransactionUncertainLocked(transaction, reason);
            }
            else
            {
                TryRejectBeforeMutationLocked(transaction, reason);
            }
        }
        return new InvalidOperationException(reason);
    }

    private static void CompleteUpdateVisual(
        object? panel,
        UpdateVisualHookState hookState,
        Exception? exception)
    {
        if (hookState.SwitchAttemptSequence > 0)
        {
            lock (StateRoot)
            {
                var foundAttempt = false;
                if (Panels.TryGetValue(hookState.PanelPointer, out var switchPanel)
                    && switchPanel.SwitchAttempt is { } attempt
                    && attempt.Sequence == hookState.SwitchAttemptSequence
                    && string.Equals(
                        attempt.Identity,
                        hookState.SwitchAttemptIdentity,
                        StringComparison.Ordinal))
                {
                    foundAttempt = true;
                    if (exception != null)
                    {
                        MarkSwitchAttemptUncertainLocked(
                            attempt,
                            $"native visual refresh failed after recipe switch receipt: {DescribeException(exception)}");
                    }
                    else if (attempt.State == RecipeSwitchAttemptState.ReceiptObserved)
                    {
                        attempt.State = RecipeSwitchAttemptState.VisualCompleted;
                    }
                }
                if (!foundAttempt
                    && _activeSubmit?.SwitchAttemptToken is RecipeSwitchAttempt retained
                    && retained.Sequence == hookState.SwitchAttemptSequence
                    && string.Equals(
                        retained.Identity,
                        hookState.SwitchAttemptIdentity,
                        StringComparison.Ordinal))
                {
                    MarkSwitchAttemptUncertainLocked(
                        retained,
                        "visual finalizer lost its exact retained recipe switch attempt");
                }
            }
        }
        if (hookState.MutationApplied && exception != null)
        {
            lock (StateRoot)
            {
                if (TryGetTransactionLocked(
                        hookState.PanelPointer,
                        hookState.TransactionIdentity,
                        out _,
                        out var transaction))
                {
                    MarkTransactionUncertainLocked(
                        transaction,
                        $"native refresh failed after extras mutation: {DescribeException(exception)}");
                }
            }
        }
        if (hookState.CaptureSelectedVisual && panel != null)
        {
            TryLogSelectedVisualState(panel, hookState, exception);
        }
    }

    private static void TryLogSelectedVisualState(
        object panel,
        UpdateVisualHookState hookState,
        Exception? exception)
    {
        long businessGeneration;
        int recipeId;
        int[] expectedIngredients;
        lock (StateRoot)
        {
            if (_log == null
                || !TryGetTransactionLocked(
                    hookState.PanelPointer,
                    hookState.TransactionIdentity,
                    out _,
                    out var transaction)
                || transaction.Sequence != hookState.TransactionSequence)
            {
                return;
            }
            businessGeneration = transaction.BusinessGeneration;
            recipeId = transaction.RecipeId;
            expectedIngredients = transaction.BaseIngredientIds
                .Concat(transaction.ExtraIngredientIds)
                .ToArray();
        }

        TargetRecipePanelSelectionState selectionState = null!;
        TargetRecipeSelectedVisualState visualState = null!;
        var cookingError = "";
        var visualError = "";
        var cookingRead = false;
        var visualRead = false;
        try
        {
            cookingRead = TryGetNativePointer(panel) == hookState.PanelPointer
                && _runtime.TryReadPanelSelectionState(
                    panel,
                    out selectionState,
                    out cookingError);
        }
        catch (Exception ex)
        {
            cookingError = DescribeException(ex);
        }
        try
        {
            visualRead = TryGetNativePointer(panel) == hookState.PanelPointer
                && _runtime.TryReadSelectedVisualState(
                    panel,
                    out visualState,
                    out visualError);
        }
        catch (Exception ex)
        {
            visualError = DescribeException(ex);
        }

        var selectedPointer = cookingRead
            ? TryGetNativePointer(selectionState.SelectedIngredientList)
            : (nint)0;
        var selectedIds = cookingRead
            ? selectionState.SelectedIngredientIds.ToArray()
            : Array.Empty<int>();
        var visibleIds = visualRead
            ? visualState.OrderedIngredientIds.ToArray()
            : Array.Empty<int>();
        var aligned = exception == null
            && cookingRead
            && visualRead
            && selectionState.PanelPointer == hookState.PanelPointer
            && selectedIds.SequenceEqual(expectedIngredients)
            && visibleIds.SequenceEqual(selectedIds);
        var exceptionText = exception == null ? "none" : DescribeException(exception);
        var visualSignature = string.Join(
            "|",
            cookingRead,
            visualRead,
            aligned,
            selectedPointer,
            string.Join(",", selectedIds),
            visualRead ? visualState.IngredientListPointer : 0,
            string.Join(",", visibleIds),
            cookingError,
            visualError,
            exceptionText);
        long panelEpoch;
        long transactionSequence;
        TransactionState transactionState;
        lock (StateRoot)
        {
            if (!TryGetTransactionLocked(
                    hookState.PanelPointer,
                    hookState.TransactionIdentity,
                    out var currentPanel,
                    out var transaction)
                || transaction.Sequence != hookState.TransactionSequence)
            {
                return;
            }
            var signature = $"{currentPanel.PanelEpoch}|{transaction.State}|{visualSignature}";
            if (string.Equals(
                    transaction.LastSelectedVisualSignature,
                    signature,
                    StringComparison.Ordinal)) return;
            transaction.LastSelectedVisualSignature = signature;
            panelEpoch = currentPanel.PanelEpoch;
            transactionSequence = transaction.Sequence;
            transactionState = transaction.State;
        }

        TryLogTransaction(
            businessGeneration,
            $"visual-state panel={FormatPointer(hookState.PanelPointer)} epoch={panelEpoch} "
            + $"transaction={transactionSequence} state={transactionState} recipe={recipeId} "
            + $"exception={exceptionText} "
            + $"selected={FormatPointer(selectedPointer)}:[{string.Join(",", selectedIds)}] "
            + $"visible={FormatPointer(visualRead ? visualState.IngredientListPointer : 0)}:"
            + $"[{string.Join(",", visibleIds)}] aligned={aligned} "
            + $"cookingError={(cookingError.Length == 0 ? "none" : cookingError)} "
            + $"visualError={(visualError.Length == 0 ? "none" : visualError)}",
            aligned ? TransactionLogKind.Surface : TransactionLogKind.Critical);
    }

    internal static bool PrepareOutputSelectionForTests(
        object panel,
        object combo,
        object button,
        out OutputHookState state)
    {
        return PrepareOutputSelection(panel, combo, button, out state);
    }

    internal static Exception? CompleteOutputSelectionForTests(
        OutputHookState state,
        object button,
        Exception? exception = null)
    {
        return CompleteOutputSelection(state, button, exception);
    }

    private static bool PrepareOutputSelection(
        object panel,
        object combo,
        object button,
        out OutputHookState hookState)
    {
        PanelState? entryPanel = null;
        PanelTransaction? entryTransaction = null;
        ButtonBinding? entryBinding = null;
        var panelPointer = TryGetNativePointer(panel);
        var buttonPointer = TryGetNativePointer(button);
        if (panelPointer != 0 && buttonPointer != 0)
        {
            lock (StateRoot)
            {
                if (Panels.TryGetValue(panelPointer, out var currentPanel))
                {
                    entryPanel = currentPanel;
                    entryTransaction = currentPanel.Transaction;
                }
                Buttons.TryGetValue(buttonPointer, out entryBinding);
            }
        }
        try
        {
            return PrepareOutputSelectionCore(
                panel,
                combo,
                button,
                out hookState);
        }
        catch (Exception ex)
        {
            var reason = $"output selection probe threw: {DescribeException(ex)}";
            var allowed = HandleOutputSelectionEntryFailure(
                panel,
                button,
                reason,
                entryPanel,
                entryTransaction,
                entryBinding);
            hookState = allowed
                ? default
                : CreateAbortedOutputHookState(
                    entryPanel,
                    entryTransaction,
                    buttonPointer,
                    reason);
            return allowed;
        }
    }

    private static bool PrepareOutputSelectionCore(
        object panel,
        object combo,
        object button,
        out OutputHookState hookState)
    {
        hookState = default;
        var panelPointer = TryGetNativePointer(panel);
        var buttonPointer = TryGetNativePointer(button);
        var comboPointer = TryGetNativePointer(combo);
        if (panelPointer == 0 || buttonPointer == 0 || comboPointer == 0) return true;

        ButtonBindingObservation observedBinding;
        PanelState? entryPanel = null;
        PanelTransaction? entryTransaction = null;
        lock (StateRoot)
        {
            if (Panels.TryGetValue(panelPointer, out var currentPanel)
                && !currentPanel.Retired
                && currentPanel.Transaction is { } currentTransaction)
            {
                entryPanel = currentPanel;
                entryTransaction = currentTransaction;
            }
            observedBinding = CaptureButtonBindingObservationLocked(buttonPointer);
        }
        var prior = observedBinding.Binding;
        if (!TryRetireObservedBindingForOutputSelection(
                observedBinding,
                buttonPointer,
                out var retireError))
        {
            var reason = $"output binding retirement failed: {retireError}";
            HandleOutputSelectionEntryFailure(
                panel,
                button,
                reason,
                entryPanel,
                entryTransaction,
                prior);
            hookState = CreateAbortedOutputHookState(
                entryPanel,
                entryTransaction,
                buttonPointer,
                reason);
            return false;
        }

        if (entryPanel == null || entryTransaction == null)
        {
            if (prior == null) return true;
            var trackedContextAppeared = false;
            lock (StateRoot)
            {
                trackedContextAppeared = Panels.TryGetValue(
                        panelPointer,
                        out var currentPanel)
                    && !currentPanel.Retired
                    && currentPanel.Transaction != null;
            }
            if (!trackedContextAppeared) return true;
            var reason = "output panel context appeared after entry observation";
            BlockOutputSelectionAfterContextChange(
                    entryPanel,
                    entryTransaction,
                    prior,
                    button,
                    buttonPointer);
            hookState = CreateAbortedOutputHookState(
                entryPanel,
                entryTransaction,
                buttonPointer,
                reason);
            return false;
        }

        var entryContextIsCurrent = false;
        lock (StateRoot)
        {
            entryContextIsCurrent = Panels.TryGetValue(
                    panelPointer,
                    out var currentPanel)
                && ReferenceEquals(currentPanel, entryPanel)
                && !currentPanel.Retired
                && currentPanel.PanelEpoch == entryPanel.PanelEpoch
                && ReferenceEquals(currentPanel.Transaction, entryTransaction)
                && entryTransaction.Sequence > 0;
        }
        if (!entryContextIsCurrent)
        {
            var reason = "output panel context changed before native selection";
            BlockOutputSelectionAfterContextChange(
                entryPanel,
                entryTransaction,
                prior,
                button,
                buttonPointer);
            hookState = CreateAbortedOutputHookState(
                entryPanel,
                entryTransaction,
                buttonPointer,
                reason);
            return false;
        }

        var panelState = entryPanel;
        var transaction = entryTransaction;

        if (transaction.State is not (TransactionState.Applied
            or TransactionState.OutputReady))
        {
            return StageSuppressedOutputSelection(
                panelState,
                transaction,
                buttonPointer,
                comboPointer,
                "output selection arrived outside the exact applied transaction",
                invalidateTransaction: true,
                disableButton: true,
                out hookState);
        }

        TargetRecipeMatchedComboSnapshot comboState = null!;
        var comboError = "";
        var comboRead = false;
        try
        {
            comboRead = _runtime.TryReadMatchedCombo(
                combo,
                out comboState,
                out comboError);
        }
        catch (Exception ex)
        {
            comboError = DescribeException(ex);
        }
        if (!comboRead
            || comboState.RecipePointer != transaction.AuthoritativePointer
            || comboState.RecipeId != transaction.RecipeId
            || !comboState.OrderedModifierIngredientIds.SequenceEqual(
                transaction.ExtraIngredientIds))
        {
            var mismatchReason = !comboRead
                ? string.IsNullOrEmpty(comboError)
                    ? "output combo snapshot is unavailable"
                    : comboError
                : "output combo does not match the exact recipe variant: "
                    + $"actualRecipe={comboState.RecipeId}@{FormatPointer(comboState.RecipePointer)},"
                    + $"actualModifiers=[{string.Join(",", comboState.OrderedModifierIngredientIds)}],"
                    + $"expectedRecipe={transaction.RecipeId}@{FormatPointer(transaction.AuthoritativePointer)},"
                    + $"expectedModifiers=[{string.Join(",", transaction.ExtraIngredientIds)}]";
            return StageSuppressedOutputSelection(
                panelState,
                transaction,
                buttonPointer,
                comboPointer,
                mismatchReason,
                invalidateTransaction: false,
                disableButton: false,
                out hookState);
        }

        var transactionChanged = false;
        ButtonBinding? pendingOutputBinding = null;
        lock (StateRoot)
        {
            if (!TryGetTransactionLocked(
                    panelPointer,
                    transaction.Identity,
                    out var currentPanel,
                    out var current)
                || !ReferenceEquals(currentPanel, panelState)
                || !ReferenceEquals(current, transaction)
                || transaction.State is not (TransactionState.Applied
                    or TransactionState.OutputReady)
                || IsButtonCleanupInProgressLocked(buttonPointer)
                || Buttons.ContainsKey(buttonPointer))
            {
                transactionChanged = true;
            }
            else if (transaction.State == TransactionState.OutputReady)
            {
                var oldOutputReady = transaction.OutputClosurePointer != 0
                    && OutputClosures.TryGetValue(
                        transaction.OutputClosurePointer,
                        out var oldClosure)
                    && TryValidateOutputClosureLocked(
                        oldClosure,
                        TransactionState.OutputReady,
                        out var oldPanel,
                        out var oldTransaction)
                    && ReferenceEquals(oldPanel, panelState)
                    && ReferenceEquals(oldTransaction, transaction);
                if (!oldOutputReady)
                {
                    MarkTransactionUncertainLocked(
                        transaction,
                        "exact output binding drifted before replacement");
                    transactionChanged = true;
                }
                else
                {
                    ResetReadyOutputBindingLocked(transaction);
                }
            }
            if (!transactionChanged)
            {
                transaction.OutputButtonPointer = buttonPointer;
                transaction.OutputComboPointer = comboPointer;
                transaction.OutputClosurePointer = 0;
                transaction.OutputPointer = 0;
                transaction.OutputPanelEpoch = panelState.PanelEpoch;
                transaction.State = TransactionState.OutputPending;
                pendingOutputBinding = new ButtonBinding(
                    panelPointer,
                    panelState.PanelEpoch,
                    buttonPointer,
                    transaction.AuthoritativePointer,
                    transaction.RecipeId,
                    transaction.OriginTargetGeneration,
                    transaction.PlanIdentity,
                    RuntimeUiTargetKinds.None,
                    RowKind.Output,
                    transaction.Sequence,
                    transaction.Identity,
                    BindingState.Pending);
                Buttons[buttonPointer] = pendingOutputBinding;
            }
        }
        if (transactionChanged)
        {
            return StageSuppressedOutputSelection(
                panelState,
                transaction,
                buttonPointer,
                comboPointer,
                "output transaction changed before callback registration",
                invalidateTransaction: true,
                disableButton: true,
                out hookState);
        }
        hookState = new OutputHookState(
            panelPointer,
            panelState.PanelEpoch,
            buttonPointer,
            transaction.Sequence,
            transaction.Identity,
            pendingOutputBinding!.Sequence,
            OutputHookDisposition.RegisterExact);
        TryLogTransaction(
            transaction.BusinessGeneration,
            $"output-bound panel={FormatPointer(panelPointer)} epoch={panelState.PanelEpoch} "
            + $"transaction={transaction.Sequence} recipe={transaction.RecipeId} "
            + $"combo={FormatPointer(comboPointer)} "
            + $"comboRecipe={comboState.RecipeId}@{FormatPointer(comboState.RecipePointer)} "
            + $"modifiers=[{string.Join(",", comboState.OrderedModifierIngredientIds)}] "
            + $"expectedRecipe={transaction.RecipeId}@{FormatPointer(transaction.AuthoritativePointer)} "
            + $"expectedModifiers=[{string.Join(",", transaction.ExtraIngredientIds)}] "
            + $"button={FormatPointer(buttonPointer)}");
        return true;
    }

    private static bool BlockOutputSelectionAfterContextChange(
        PanelState? entryPanel,
        PanelTransaction? entryTransaction,
        ButtonBinding? prior,
        object button,
        nint buttonPointer)
    {
        var shouldDisable = false;
        lock (StateRoot)
        {
            if (Buttons.TryGetValue(buttonPointer, out var current))
            {
                if (prior != null && current.Sequence == prior.Sequence)
                {
                    current.State = BindingState.Tombstone;
                    shouldDisable = true;
                }
            }
            else if (entryPanel != null
                && entryTransaction != null
                && Panels.TryGetValue(entryPanel.PanelPointer, out var currentPanel)
                && ReferenceEquals(currentPanel, entryPanel))
            {
                var tombstone = prior?.RowKind == RowKind.Output
                    && prior.PanelPointer == entryPanel.PanelPointer
                    && prior.PanelEpoch == entryPanel.PanelEpoch
                    && prior.TransactionSequence == entryTransaction.Sequence
                    && string.Equals(
                        prior.TransactionIdentity,
                        entryTransaction.Identity,
                        StringComparison.Ordinal)
                        ? prior
                        : new ButtonBinding(
                            entryPanel.PanelPointer,
                            entryPanel.PanelEpoch,
                            buttonPointer,
                            entryTransaction.AuthoritativePointer,
                            entryTransaction.RecipeId,
                            entryTransaction.OriginTargetGeneration,
                            entryTransaction.PlanIdentity,
                            RuntimeUiTargetKinds.None,
                            RowKind.Output,
                            entryTransaction.Sequence,
                            entryTransaction.Identity,
                            BindingState.Tombstone);
                tombstone.State = BindingState.Tombstone;
                Buttons[buttonPointer] = tombstone;
                shouldDisable = true;
            }
        }
        if (shouldDisable)
        {
            try { _runtime.TryDisableButton(button, out _); } catch { }
        }
        return false;
    }

    private static bool StageSuppressedOutputSelection(
        PanelState panel,
        PanelTransaction transaction,
        nint buttonPointer,
        nint comboPointer,
        string reason,
        bool invalidateTransaction,
        bool disableButton,
        out OutputHookState hookState)
    {
        hookState = default;
        ButtonBinding? candidateBinding = null;
        var transactionStayedUsable = false;
        lock (StateRoot)
        {
            var exactCurrent = Panels.TryGetValue(panel.PanelPointer, out var currentPanel)
                && ReferenceEquals(currentPanel, panel)
                && !currentPanel.Retired
                && ReferenceEquals(currentPanel.Transaction, transaction)
                && currentPanel.PanelEpoch == transaction.PanelEpoch
                && transaction.Sequence > 0;
            if (exactCurrent
                && !IsButtonCleanupInProgressLocked(buttonPointer)
                && !Buttons.ContainsKey(buttonPointer))
            {
                candidateBinding = new ButtonBinding(
                    panel.PanelPointer,
                    panel.PanelEpoch,
                    buttonPointer,
                    transaction.AuthoritativePointer,
                    transaction.RecipeId,
                    transaction.OriginTargetGeneration,
                    transaction.PlanIdentity,
                    RuntimeUiTargetKinds.None,
                    RowKind.Output,
                    transaction.Sequence,
                    transaction.Identity,
                    BindingState.Pending);
                Buttons[buttonPointer] = candidateBinding;
                if (transaction.State == TransactionState.Applied
                    || transaction.State == TransactionState.OutputReady
                        && transaction.OutputButtonPointer != buttonPointer)
                {
                    transactionStayedUsable = true;
                }
                else if (invalidateTransaction
                    && !IsTerminalTransactionState(transaction.State)
                    && (transaction.MutationStarted
                        || transaction.State == TransactionState.Uncertain))
                {
                    MarkTransactionUncertainLocked(transaction, reason);
                }
                else if (invalidateTransaction
                    && !IsTerminalTransactionState(transaction.State))
                {
                    TryRejectBeforeMutationLocked(transaction, reason);
                }
                _blockedSubmissions++;
                if (invalidateTransaction) _failures++;
                _lastResult = reason;
            }
        }
        if (candidateBinding == null)
        {
            hookState = CreateAbortedOutputHookState(
                panel,
                transaction,
                buttonPointer,
                $"{reason}; suppression owner changed before native output selection");
            return false;
        }
        hookState = new OutputHookState(
            panel.PanelPointer,
            panel.PanelEpoch,
            buttonPointer,
            transaction.Sequence,
            transaction.Identity,
            candidateBinding.Sequence,
            OutputHookDisposition.SuppressAfterOriginal,
            reason,
            disableButton);
        TryLogTransaction(
            transaction.BusinessGeneration,
            $"output-candidate-suppressed panel={FormatPointer(panel.PanelPointer)} "
            + $"epoch={panel.PanelEpoch} recipe={transaction.RecipeId} "
            + $"combo={FormatPointer(comboPointer)} usable={transactionStayedUsable} "
            + $"reason={reason}",
            invalidateTransaction ? TransactionLogKind.Critical : TransactionLogKind.Action);
        return true;
    }

    private static OutputHookState CreateAbortedOutputHookState(
        PanelState? panel,
        PanelTransaction? transaction,
        nint buttonPointer,
        string reason)
    {
        return new OutputHookState(
            panel?.PanelPointer ?? 0,
            panel?.PanelEpoch ?? 0,
            buttonPointer,
            transaction?.Sequence ?? 0,
            transaction?.Identity ?? "",
            0,
            OutputHookDisposition.AbortBeforeOriginal,
            reason,
            true);
    }

    private static bool HandleOutputSelectionEntryFailure(
        object panel,
        object button,
        string reason,
        PanelState? expectedPanel,
        PanelTransaction? expectedTransaction,
        ButtonBinding? expectedBinding)
    {
        var panelPointer = TryGetNativePointer(panel);
        var buttonPointer = TryGetNativePointer(button);
        if (panelPointer == 0 || buttonPointer == 0) return true;
        var failClosed = false;
        var shouldDisable = false;
        lock (StateRoot)
        {
            var exactTransaction = expectedPanel != null
                && expectedTransaction != null
                && Panels.TryGetValue(panelPointer, out var panelState)
                && ReferenceEquals(panelState, expectedPanel)
                && ReferenceEquals(panelState.Transaction, expectedTransaction)
                && expectedTransaction.Sequence > 0;
            if (exactTransaction
                && expectedTransaction!.State is not (TransactionState.Cancelled
                    or TransactionState.Completed
                    or TransactionState.Rejected))
            {
                if (expectedTransaction.MutationStarted)
                {
                    MarkTransactionUncertainLocked(expectedTransaction, reason);
                }
                else
                {
                    TryRejectBeforeMutationLocked(expectedTransaction, reason);
                }
                failClosed = true;
            }

            if (expectedBinding != null
                && Buttons.TryGetValue(buttonPointer, out var currentBinding)
                && ReferenceEquals(currentBinding, expectedBinding)
                && currentBinding.RowKind is RowKind.Output or RowKind.Synthetic)
            {
                currentBinding.State = BindingState.Tombstone;
                failClosed = true;
                shouldDisable = true;
            }
            else if (exactTransaction)
            {
                if (Buttons.TryGetValue(buttonPointer, out currentBinding))
                {
                    if (currentBinding.RowKind == RowKind.Output
                        && currentBinding.TransactionSequence
                            == expectedTransaction!.Sequence
                        && string.Equals(
                            currentBinding.TransactionIdentity,
                            expectedTransaction.Identity,
                            StringComparison.Ordinal))
                    {
                        currentBinding.State = BindingState.Tombstone;
                        failClosed = true;
                        shouldDisable = true;
                    }
                }
                else
                {
                    Buttons[buttonPointer] = new ButtonBinding(
                        panelPointer,
                        expectedPanel!.PanelEpoch,
                        buttonPointer,
                        expectedTransaction!.AuthoritativePointer,
                        expectedTransaction.RecipeId,
                        expectedTransaction.OriginTargetGeneration,
                        expectedTransaction.PlanIdentity,
                        RuntimeUiTargetKinds.None,
                        RowKind.Output,
                        expectedTransaction.Sequence,
                        expectedTransaction.Identity,
                        BindingState.Tombstone);
                    failClosed = true;
                    shouldDisable = true;
                }
            }
            if (failClosed)
            {
                _blockedSubmissions++;
                _failures++;
                _lastResult = reason;
            }
        }
        if (shouldDisable)
        {
            try { _runtime.TryDisableButton(button, out _); } catch { }
        }
        return !failClosed;
    }

    private static Exception? CompleteOutputSelection(
        OutputHookState hookState,
        object button,
        Exception? exception)
    {
        if (hookState.Disposition == OutputHookDisposition.None) return exception;
        if (hookState.Disposition == OutputHookDisposition.AbortBeforeOriginal)
        {
            return exception ?? new InvalidOperationException(
                $"Output selection was aborted before native callback ownership: {hookState.Reason}");
        }
        if (hookState.Disposition == OutputHookDisposition.SuppressAfterOriginal)
        {
            return CompleteSuppressedOutputSelection(hookState, button, exception);
        }
        if (exception != null)
        {
            return FailOutputSelectionAfterNativeException(
                hookState,
                button,
                $"native output callback registration failed: {DescribeException(exception)}",
                exception);
        }

        var buttonPointer = TryGetNativePointer(button);
        var closureError = "";
        TargetRecipeOutputClosureBindingSnapshot closureSnapshot = default;
        var closureRead = false;
        try
        {
            closureRead = buttonPointer == hookState.ButtonPointer
                && _runtime.TryReadExactOutputSubmitClosure(
                    button,
                    out closureSnapshot,
                    out closureError);
        }
        catch (Exception ex)
        {
            closureError = DescribeException(ex);
        }
        if (!closureRead
            || closureSnapshot.ClosurePointer == 0
            || closureSnapshot.PanelPointer == 0
            || closureSnapshot.ComboPointer == 0
            || closureSnapshot.OutputPointer == 0)
        {
            return FailOutputClosureRegistration(
                hookState,
                button,
                string.IsNullOrEmpty(closureError)
                    ? "native output callback did not install the exact final closure"
                    : closureError);
        }

        var ready = false;
        lock (StateRoot)
        {
            if (!TryGetTransactionLocked(
                    hookState.PanelPointer,
                    hookState.TransactionIdentity,
                    out var panel,
                    out var transaction)
                || panel.PanelEpoch != hookState.PanelEpoch
                || transaction.Sequence != hookState.TransactionSequence
                || transaction.OutputButtonPointer != hookState.ButtonPointer
                || closureSnapshot.PanelPointer != hookState.PanelPointer
                || closureSnapshot.ComboPointer != transaction.OutputComboPointer
                || transaction.State != TransactionState.OutputPending
                || !Buttons.TryGetValue(hookState.ButtonPointer, out var binding)
                || binding.Sequence != hookState.BindingSequence
                || binding.State != BindingState.Pending
                || OutputClosures.ContainsKey(closureSnapshot.ClosurePointer))
            {
                ready = false;
            }
            else
            {
                transaction.OutputClosurePointer = closureSnapshot.ClosurePointer;
                transaction.OutputPointer = closureSnapshot.OutputPointer;
                OutputClosures[closureSnapshot.ClosurePointer] = new OutputClosureBinding(
                    closureSnapshot.ClosurePointer,
                    transaction.PanelPointer,
                    panel.PanelEpoch,
                    transaction.OutputButtonPointer,
                    transaction.OutputComboPointer,
                    transaction.OutputPointer,
                    transaction.OriginTargetGeneration,
                    binding.Sequence,
                    transaction.Sequence,
                    transaction.Identity,
                    BindingState.Ready);
                binding.State = BindingState.Ready;
                transaction.State = TransactionState.OutputReady;
                ready = true;
                TryLogTransaction(
                    transaction.BusinessGeneration,
                    $"output-ready panel={FormatPointer(transaction.PanelPointer)} "
                    + $"epoch={panel.PanelEpoch} recipe={transaction.RecipeId} "
                    + $"button={FormatPointer(transaction.OutputButtonPointer)} "
                    + $"closure={FormatPointer(transaction.OutputClosurePointer)} "
                    + $"output={FormatPointer(transaction.OutputPointer)}",
                    TransactionLogKind.Critical);
            }
        }
        if (!ready)
        {
            return FailOutputClosureRegistration(
                hookState,
                button,
                "output transaction changed before exact closure registration");
        }
        return null;
    }

    private static Exception? CompleteSuppressedOutputSelection(
        OutputHookState hookState,
        object button,
        Exception? exception)
    {
        if (exception != null)
        {
            return FailOutputSelectionAfterNativeException(
                hookState,
                button,
                $"native suppressed output selection failed: {DescribeException(exception)}",
                exception);
        }

        ButtonBinding? binding;
        lock (StateRoot)
        {
            binding = Buttons.TryGetValue(hookState.ButtonPointer, out var current)
                && current.Sequence == hookState.BindingSequence
                && current.RowKind == RowKind.Output
                && current.TransactionSequence == hookState.TransactionSequence
                && string.Equals(
                    current.TransactionIdentity,
                    hookState.TransactionIdentity,
                    StringComparison.Ordinal)
                    ? current
                    : null;
        }
        if (binding == null)
        {
            return FailSuppressedOutputPostcondition(
                hookState,
                button,
                "suppressed output owner changed before normal-return callback cleanup",
                cleanupFailed: false);
        }

        var callbackCleaned = TryCleanSubmitCallbackExclusive(
            button,
            hookState.ButtonPointer,
            binding,
            out var cleanupError);
        if (!callbackCleaned)
        {
            return FailSuppressedOutputPostcondition(
                hookState,
                button,
                $"suppressed output callback cleanup failed: {cleanupError}",
                cleanupFailed: true);
        }

        var shouldDisable = false;
        lock (StateRoot)
        {
            if (Buttons.TryGetValue(hookState.ButtonPointer, out var current)
                && ReferenceEquals(current, binding)
                && current.Sequence == hookState.BindingSequence)
            {
                current.State = BindingState.Tombstone;
                shouldDisable = hookState.DisableButton;
            }
        }
        if (shouldDisable)
        {
            try { _runtime.TryDisableButton(button, out _); } catch { }
        }
        return null;
    }

    private static Exception FailSuppressedOutputPostcondition(
        OutputHookState hookState,
        object button,
        string reason,
        bool cleanupFailed)
    {
        var shouldDisable = false;
        lock (StateRoot)
        {
            if (TryGetTransactionLocked(
                    hookState.PanelPointer,
                    hookState.TransactionIdentity,
                    out var panel,
                    out var transaction)
                && panel.PanelEpoch == hookState.PanelEpoch
                && transaction.Sequence == hookState.TransactionSequence
                && !IsTerminalTransactionState(transaction.State))
            {
                MarkTransactionUncertainLocked(transaction, reason);
            }
            if (Buttons.TryGetValue(hookState.ButtonPointer, out var current)
                && current.Sequence == hookState.BindingSequence)
            {
                current.State = BindingState.Tombstone;
                shouldDisable = true;
            }
            _failures++;
            _lastResult = reason;
        }
        if (shouldDisable)
        {
            try { _runtime.TryDisableButton(button, out _); } catch { }
        }
        return new InvalidOperationException(
            cleanupFailed
                ? reason
                : $"{reason}; callback ownership was not touched");
    }

    private static Exception? FailOutputSelectionAfterNativeException(
        OutputHookState hookState,
        object button,
        string reason,
        Exception originalException)
    {
        var shouldDisable = false;
        lock (StateRoot)
        {
            if (TryGetTransactionLocked(
                    hookState.PanelPointer,
                    hookState.TransactionIdentity,
                    out var panel,
                    out var transaction)
                && panel.PanelEpoch == hookState.PanelEpoch
                && transaction.Sequence == hookState.TransactionSequence
                && !IsTerminalTransactionState(transaction.State))
            {
                MarkTransactionUncertainLocked(transaction, reason);
            }
            if (hookState.BindingSequence > 0
                && Buttons.TryGetValue(hookState.ButtonPointer, out var current)
                && current.Sequence == hookState.BindingSequence)
            {
                current.State = BindingState.Tombstone;
                shouldDisable = true;
            }
            if (hookState.Disposition == OutputHookDisposition.RegisterExact)
            {
                _blockedSubmissions++;
            }
            _failures++;
            _lastResult = reason;
        }
        if (shouldDisable)
        {
            try { _runtime.TryDisableButton(button, out _); } catch { }
        }
        return originalException;
    }

    private static Exception? FailOutputClosureRegistration(
        OutputHookState hookState,
        object button,
        string reason)
    {
        ButtonBinding? failedBinding = null;
        lock (StateRoot)
        {
            if (TryGetTransactionLocked(
                    hookState.PanelPointer,
                    hookState.TransactionIdentity,
                    out var panel,
                    out var transaction)
                && !panel.Retired
                && panel.PanelEpoch == hookState.PanelEpoch
                && transaction.Sequence == hookState.TransactionSequence
                && transaction.State == TransactionState.OutputPending
                && transaction.OutputButtonPointer == hookState.ButtonPointer
                && Buttons.TryGetValue(
                    hookState.ButtonPointer,
                    out var currentBinding)
                && currentBinding.Sequence == hookState.BindingSequence
                && currentBinding.RowKind == RowKind.Output
                && currentBinding.TransactionSequence == hookState.TransactionSequence
                && string.Equals(
                    currentBinding.TransactionIdentity,
                    hookState.TransactionIdentity,
                    StringComparison.Ordinal))
            {
                MarkTransactionUncertainLocked(transaction, reason);
                currentBinding.State = BindingState.Tombstone;
                failedBinding = currentBinding;
                _blockedSubmissions++;
                _lastResult = reason;
            }
        }
        if (failedBinding == null)
        {
            lock (StateRoot)
            {
                _blockedSubmissions++;
                _failures++;
                _lastResult = reason;
            }
            return new InvalidOperationException(
                $"{reason}; callback owner changed before safe cleanup");
        }

        var callbackCleaned = TryCleanSubmitCallbackExclusive(
            button,
            hookState.ButtonPointer,
            failedBinding,
            out var cleanupError);
        var shouldDisable = false;
        lock (StateRoot)
        {
            shouldDisable = Buttons.TryGetValue(
                    hookState.ButtonPointer,
                    out var currentBinding)
                && ReferenceEquals(currentBinding, failedBinding);
        }
        if (shouldDisable)
        {
            try { _runtime.TryDisableButton(button, out _); } catch { }
        }
        lock (StateRoot) _failures++;
        if (callbackCleaned) return null;
        return new InvalidOperationException(
            $"{reason}; exact callback cleanup failed: {cleanupError}");
    }

    internal static bool BeginOutputClosureForTests(
        object closure,
        out OutputClosureHookState state)
    {
        return BeginOutputClosure(closure, out state);
    }

    internal static void CompleteOutputClosureForTests(
        OutputClosureHookState state,
        Exception? exception = null)
    {
        CompleteOutputClosure(state, exception);
    }

    private static bool BeginOutputClosure(
        object closure,
        out OutputClosureHookState hookState)
    {
        var pendingState = new OutputClosureHookState();
        try
        {
            var allowed = BeginOutputClosureCore(closure, out pendingState);
            hookState = pendingState;
            return allowed;
        }
        catch (Exception ex)
        {
            hookState = pendingState;
            var closurePointer = TryGetNativePointer(closure);
            var binding = pendingState.Binding;
            var reason = $"final output probe threw: {DescribeException(ex)}";
            lock (StateRoot)
            {
                if (binding == null
                    && closurePointer != 0
                    && OutputClosures.TryGetValue(closurePointer, out binding))
                {
                    pendingState.Tracked = true;
                    pendingState.Binding = binding;
                }
                if (binding != null)
                {
                    var wasCurrentOwner = TryGetCurrentOutputOwnerLocked(
                        binding,
                        out _,
                        out var transaction,
                        out var button);
                    binding.State = BindingState.Tombstone;
                    if (wasCurrentOwner)
                    {
                        button.State = BindingState.Tombstone;
                        MarkTransactionUncertainLocked(transaction, reason);
                    }
                    _blockedSubmissions++;
                    _lastResult = reason;
                }
                if (ReferenceEquals(_activeOutputClosure, pendingState.TerminalContext))
                {
                    _activeOutputClosure = null;
                }
            }
            DisposePublicationLeaseNoThrow(pendingState.PublicationLease);
            pendingState.PublicationLease = null;
            return binding == null;
        }
    }

    private static bool BeginOutputClosureCore(
        object closure,
        out OutputClosureHookState hookState)
    {
        hookState = new OutputClosureHookState();
        var closurePointer = TryGetNativePointer(closure);
        if (closurePointer == 0) return true;

        OutputClosureBinding? binding;
        ActiveSubmitContext? activeProbe;
        long expectedBusinessGeneration;
        lock (StateRoot)
        {
            if (!OutputClosures.TryGetValue(closurePointer, out binding)) return true;
            hookState.Tracked = true;
            hookState.Binding = binding;
            activeProbe = _activeSubmit;
            if (binding.State != BindingState.Ready
                || _activeOutputClosure != null
                || activeProbe == null
                || activeProbe.ButtonPointer != binding.ButtonPointer
                || (activeProbe.Kind != SubmitKind.None
                    && (activeProbe.Kind != SubmitKind.Output
                        || activeProbe.PanelPointer != binding.PanelPointer
                        || activeProbe.PanelEpoch != binding.PanelEpoch
                        || !string.Equals(
                            activeProbe.TransactionIdentity,
                            binding.TransactionIdentity,
                            StringComparison.Ordinal)))
                || !TryValidateOutputClosureLocked(
                    binding,
                    TransactionState.OutputReady,
                    out var panel,
                    out _))
            {
                return RejectOutputClosure(
                    binding,
                    "registered final output closure was invoked outside its exact active button submit");
            }
            expectedBusinessGeneration = panel.BusinessGeneration;
            if (activeProbe.Kind == SubmitKind.None)
            {
                activeProbe.AttachOutput(
                    binding.PanelPointer,
                    binding.PanelEpoch,
                    binding.TransactionIdentity);
            }
            hookState.ActiveProbe = activeProbe;
        }

        if (!_runtime.TryReadOutputSubmitClosureState(
                closure,
                out var closureState,
                out var closureError)
            || closureState.ClosurePointer != binding.ClosurePointer
            || closureState.PanelPointer != binding.PanelPointer
            || closureState.ComboPointer != binding.ComboPointer
            || closureState.OutputPointer != binding.OutputPointer)
        {
            return RejectOutputClosure(
                binding,
                string.IsNullOrEmpty(closureError)
                    ? "final output closure panel/combo/output identity drifted"
                    : closureError);
        }

        if (!RuntimeUiPinningService.TryAcquireTargetRecipeVariantPublicationLease(
                expectedBusinessGeneration,
                out var publicationLease))
        {
            return RejectOutputClosure(
                binding,
                "business generation changed before final output publication lease");
        }
        hookState.PublicationLease = publicationLease;

        var publicationTokenValid = false;
        lock (StateRoot)
        {
            publicationTokenValid = ReferenceEquals(_activeSubmit, activeProbe)
                && TryValidateOutputClosureLocked(
                    binding,
                    TransactionState.OutputReady,
                    out var publicationPanel,
                    out var publicationTransaction)
                && publicationPanel.BusinessGeneration == expectedBusinessGeneration
                && publicationTransaction.BusinessGeneration == expectedBusinessGeneration;
        }
        if (!publicationTokenValid)
        {
            DisposePublicationLeaseNoThrow(publicationLease);
            hookState.PublicationLease = null;
            return RejectOutputClosure(
                binding,
                "final output token changed at the publication boundary");
        }

        if (!_runtime.TryWrapPanel(
                binding.PanelPointer,
                out var panelWrapper,
                out var panelError))
        {
            DisposePublicationLeaseNoThrow(publicationLease);
            hookState.PublicationLease = null;
            return RejectOutputClosure(
                binding,
                $"fresh final output panel wrapper failed: {panelError}");
        }

        PanelTransaction? preflightTransaction;
        lock (StateRoot)
        {
            var found = TryValidateOutputClosureLocked(
                binding,
                TransactionState.OutputReady,
                out _,
                out var transaction);
            preflightTransaction = found ? transaction : null;
        }
        var preflightError = "";
        if (preflightTransaction == null
            || !TryOutputPreflight(
                panelWrapper,
                preflightTransaction,
                out preflightError))
        {
            DisposePublicationLeaseNoThrow(publicationLease);
            hookState.PublicationLease = null;
            return RejectOutputClosure(
                binding,
                preflightTransaction == null
                    ? "final output transaction changed before fresh preflight"
                    : preflightError);
        }

        long businessGeneration;
        int recipeId;
        var postflightValid = false;
        PanelState currentPanel = null!;
        PanelTransaction currentTransaction = null!;
        lock (StateRoot)
        {
            postflightValid = ReferenceEquals(_activeSubmit, activeProbe)
                && _activeOutputClosure == null
                && TryValidateOutputClosureLocked(
                    binding,
                    TransactionState.OutputReady,
                    out currentPanel,
                    out currentTransaction)
                && currentPanel.BusinessGeneration == expectedBusinessGeneration
                && currentTransaction.BusinessGeneration == expectedBusinessGeneration;
            if (postflightValid)
            {
                currentTransaction.State = TransactionState.OutputSubmitting;
                var terminalIdentity = new FinalOutputIdentity(
                    currentTransaction.PanelPointer,
                    binding.PanelEpoch,
                    binding.ButtonPointer,
                    binding.ButtonBindingSequence,
                    binding.ClosurePointer,
                    binding.ComboPointer,
                    binding.OutputPointer,
                    currentTransaction.OriginTargetGeneration,
                    currentTransaction.BusinessGeneration,
                    currentTransaction.Sequence,
                    currentTransaction.RecipeId,
                    currentTransaction.Identity);
                var terminalContext = new ActiveOutputClosureContext(
                    terminalIdentity,
                    activeProbe);
                _activeOutputClosure = terminalContext;
                hookState.TerminalContext = terminalContext;
                hookState.OriginalAllowed = true;
                businessGeneration = currentTransaction.BusinessGeneration;
                recipeId = currentTransaction.RecipeId;
            }
            else
            {
                businessGeneration = 0;
                recipeId = -1;
            }
        }
        if (!postflightValid)
        {
            DisposePublicationLeaseNoThrow(publicationLease);
            hookState.PublicationLease = null;
            return RejectOutputClosure(
                binding,
                "final output token changed after fresh preflight");
        }
        TryLogTransaction(
            businessGeneration,
            $"final-armed panel={FormatPointer(binding.PanelPointer)} "
            + $"epoch={binding.PanelEpoch} recipe={recipeId} "
            + $"button={FormatPointer(binding.ButtonPointer)} "
            + $"closure={FormatPointer(binding.ClosurePointer)} "
            + $"output={FormatPointer(binding.OutputPointer)}",
            TransactionLogKind.Critical);
        return true;
    }

    private static bool TryGetOutputButtonForTransactionLocked(
        PanelTransaction transaction,
        out ButtonBinding button)
    {
        button = null!;
        if (transaction.OutputButtonPointer == 0
            || !Buttons.TryGetValue(
                transaction.OutputButtonPointer,
                out var candidate))
        {
            return false;
        }
        button = candidate;
        return button.RowKind == RowKind.Output
            && button.PanelPointer == transaction.PanelPointer
            && button.PanelEpoch == transaction.OutputPanelEpoch
            && button.ButtonPointer == transaction.OutputButtonPointer
            && button.RecipePointer == transaction.AuthoritativePointer
            && button.RecipeId == transaction.RecipeId
            && button.TargetGeneration == transaction.OriginTargetGeneration
            && button.TransactionSequence == transaction.Sequence
            && string.Equals(
                button.TransactionIdentity,
                transaction.Identity,
                StringComparison.Ordinal)
            && string.Equals(
                button.PlanIdentity,
                transaction.PlanIdentity,
                StringComparison.Ordinal);
    }

    private static bool TryGetCurrentOutputOwnerLocked(
        OutputClosureBinding binding,
        out PanelState panel,
        out PanelTransaction transaction,
        out ButtonBinding button)
    {
        panel = null!;
        transaction = null!;
        button = null!;
        return OutputClosures.TryGetValue(binding.ClosurePointer, out var currentClosure)
            && ReferenceEquals(currentClosure, binding)
            && TryGetTransactionLocked(
                binding.PanelPointer,
                binding.TransactionIdentity,
                out panel,
                out transaction)
            && IsCurrentPanelBusinessLocked(panel)
            && transaction.Sequence == binding.TransactionSequence
            && transaction.OutputPanelEpoch == binding.PanelEpoch
            && transaction.OriginTargetGeneration == binding.TargetGeneration
            && transaction.OutputButtonPointer == binding.ButtonPointer
            && transaction.OutputComboPointer == binding.ComboPointer
            && transaction.OutputClosurePointer == binding.ClosurePointer
            && transaction.OutputPointer == binding.OutputPointer
            && TryGetOutputButtonForTransactionLocked(transaction, out button)
            && button.Sequence == binding.ButtonBindingSequence;
    }

    private static bool TryValidateOutputClosureLocked(
        OutputClosureBinding binding,
        TransactionState requiredState,
        out PanelState panel,
        out PanelTransaction transaction)
    {
        panel = null!;
        transaction = null!;
        return binding.State == BindingState.Ready
            && TryGetCurrentOutputOwnerLocked(
                binding,
                out panel,
                out transaction,
                out var button)
            && transaction.State == requiredState
            && button.State == BindingState.Ready;
    }

    private static bool RejectOutputClosure(
        OutputClosureBinding binding,
        string reason)
    {
        lock (StateRoot)
        {
            PanelTransaction currentTransaction = null!;
            ButtonBinding button = null!;
            var wasCurrentReady = binding.State == BindingState.Ready
                && TryGetCurrentOutputOwnerLocked(
                    binding,
                    out _,
                    out currentTransaction,
                    out button)
                && currentTransaction.State == TransactionState.OutputReady
                && button.State == BindingState.Ready;
            binding.State = BindingState.Tombstone;
            if (wasCurrentReady)
            {
                button.State = BindingState.Tombstone;
                MarkTransactionUncertainLocked(currentTransaction, reason);
            }
            _blockedSubmissions++;
            _lastResult = reason;
        }
        return false;
    }

    private static void CompleteOutputClosure(
        OutputClosureHookState hookState,
        Exception? exception)
    {
        try
        {
            if (!hookState.Tracked
                || !hookState.OriginalAllowed
                || hookState.Binding == null
                || hookState.TerminalContext == null)
            {
                return;
            }

            var binding = hookState.Binding;
            var terminalContext = hookState.TerminalContext;
            lock (StateRoot)
            {
                if (exception != null)
                {
                    MarkFinalOutputUncertainLocked(
                        terminalContext,
                        $"native final output closure failed: {DescribeException(exception)}");
                    return;
                }
                if (!ReferenceEquals(_activeOutputClosure, terminalContext)
                    || !ReferenceEquals(_activeSubmit, terminalContext.ActiveProbe)
                    || _activeSubmit?.ButtonPointer != terminalContext.Identity.ButtonPointer
                    || !terminalContext.CloseReceiptRecorded
                    || !terminalContext.CloseReceiptIdentity.Equals(
                        terminalContext.Identity))
                {
                    MarkFinalOutputUncertainLocked(
                        terminalContext,
                        "final output closure returned without its exact close receipt");
                    return;
                }

                ButtonBinding outputButton = null!;
                if (!TryGetRetainedTransactionLocked(
                        terminalContext.Identity,
                        out _,
                        out var transaction)
                    || transaction == null
                    || transaction.State != TransactionState.OutputSubmitting
                    || !TryGetOutputButtonForTransactionLocked(
                        transaction,
                        out outputButton)
                    || outputButton.Sequence
                        != terminalContext.Identity.ButtonBindingSequence
                    || !OutputClosures.TryGetValue(
                        binding.ClosurePointer,
                        out var currentClosure)
                    || !ReferenceEquals(currentClosure, binding)
                    || binding.ButtonBindingSequence
                        != terminalContext.Identity.ButtonBindingSequence
                    || binding.TransactionSequence != transaction.Sequence)
                {
                    MarkFinalOutputUncertainLocked(
                        terminalContext,
                        "retained final output transaction is missing or changed before completion");
                    return;
                }
                transaction.State = TransactionState.Completed;
                terminalContext.TerminalState = FinalOutputTerminalState.Completed;
                binding.State = BindingState.Tombstone;
                outputButton.State = BindingState.Tombstone;
                _completedTransactions++;
                _lastResult = "native final recipe closure completed";
                TryLogTransaction(
                    terminalContext.Identity.BusinessGeneration,
                    $"final-completed panel={FormatPointer(terminalContext.Identity.PanelPointer)} "
                    + $"epoch={terminalContext.Identity.PanelEpoch} "
                    + $"recipe={terminalContext.Identity.RecipeId} "
                    + $"transaction={terminalContext.Identity.TransactionSequence} "
                    + $"button={FormatPointer(terminalContext.Identity.ButtonPointer)} "
                    + $"closure={FormatPointer(terminalContext.Identity.ClosurePointer)} "
                    + $"output={FormatPointer(terminalContext.Identity.OutputPointer)}",
                    TransactionLogKind.Critical);
            }
        }
        finally
        {
            if (ReferenceEquals(_activeOutputClosure, hookState.TerminalContext))
            {
                _activeOutputClosure = null;
            }
            DisposePublicationLeaseNoThrow(hookState.PublicationLease);
            hookState.PublicationLease = null;
        }
    }

    private static bool TryGetRetainedTransactionLocked(
        FinalOutputIdentity identity,
        out PanelState panel,
        out PanelTransaction? transaction)
    {
        panel = null!;
        transaction = null;
        if (!Panels.TryGetValue(identity.PanelPointer, out var foundPanel)
            || foundPanel.BusinessGeneration != identity.BusinessGeneration
            || foundPanel.Transaction is not { } candidate
            || candidate.Sequence != identity.TransactionSequence
            || !string.Equals(
                candidate.Identity,
                identity.TransactionIdentity,
                StringComparison.Ordinal)
            || candidate.OutputButtonPointer != identity.ButtonPointer
            || candidate.OutputClosurePointer != identity.ClosurePointer
            || candidate.OutputComboPointer != identity.ComboPointer
            || candidate.OutputPointer != identity.OutputPointer
            || candidate.OutputPanelEpoch != identity.PanelEpoch
            || candidate.OriginTargetGeneration != identity.TargetGeneration
            || !TryGetOutputButtonForTransactionLocked(
                candidate,
                out var outputButton)
            || outputButton.Sequence != identity.ButtonBindingSequence)
        {
            return false;
        }
        panel = foundPanel;
        transaction = candidate;
        return true;
    }

    private static void MarkFinalOutputUncertainLocked(
        ActiveOutputClosureContext context,
        string reason)
    {
        if (context.TerminalState != FinalOutputTerminalState.Pending) return;
        context.TerminalState = FinalOutputTerminalState.Uncertain;
        if (TryGetRetainedTransactionLocked(
                context.Identity,
                out _,
                out var transaction)
            && transaction != null)
        {
            MarkTransactionUncertainLocked(transaction, reason);
        }
        else
        {
            LatchBusinessMutationUncertainLocked(
                context.Identity.BusinessGeneration,
                reason);
            _uncertainTransactions++;
            _lastResult = reason;
        }
        if (OutputClosures.TryGetValue(
                context.Identity.ClosurePointer,
                out var closure)
            && closure.TransactionSequence == context.Identity.TransactionSequence
            && closure.ButtonBindingSequence
                == context.Identity.ButtonBindingSequence
            && string.Equals(
                closure.TransactionIdentity,
                context.Identity.TransactionIdentity,
                StringComparison.Ordinal))
        {
            var ownsCurrentButton = TryGetCurrentOutputOwnerLocked(
                closure,
                out _,
                out _,
                out var button);
            closure.State = BindingState.Tombstone;
            if (ownsCurrentButton) button.State = BindingState.Tombstone;
        }
        TryLogTransaction(
            context.Identity.BusinessGeneration,
            $"final-uncertain panel={FormatPointer(context.Identity.PanelPointer)} "
            + $"epoch={context.Identity.PanelEpoch} recipe={context.Identity.RecipeId} "
            + $"transaction={context.Identity.TransactionSequence} "
            + $"reason={reason}",
            TransactionLogKind.Critical);
    }

    private static void MarkTransactionUncertain(
        PanelTransaction transaction,
        string reason)
    {
        lock (StateRoot) MarkTransactionUncertainLocked(transaction, reason);
    }

    private static bool TryRejectBeforeMutationLocked(
        PanelTransaction transaction,
        string reason)
    {
        if (transaction.MutationStarted
            || transaction.State != TransactionState.PendingRecipeSubmit
            || !Panels.TryGetValue(transaction.PanelPointer, out var panel)
            || panel.Retired
            || !ReferenceEquals(panel.Transaction, transaction)
            || panel.BusinessGeneration != transaction.BusinessGeneration
            || transaction.Sequence <= 0)
        {
            return false;
        }
        transaction.State = TransactionState.Rejected;
        _rejectedTransactions++;
        _lastResult = reason;
        return true;
    }

    private static void MarkTransactionUncertainLocked(
        PanelTransaction transaction,
        string reason)
    {
        if (transaction.State is TransactionState.Cancelled
            or TransactionState.Completed
            or TransactionState.Rejected)
        {
            return;
        }
        if (!Panels.TryGetValue(transaction.PanelPointer, out var panel)
            || !ReferenceEquals(panel.Transaction, transaction)
            || panel.BusinessGeneration != transaction.BusinessGeneration
            || transaction.Sequence <= 0)
        {
            return;
        }
        LatchBusinessMutationUncertainLocked(
            transaction.BusinessGeneration,
            reason);
        if (transaction.State != TransactionState.Uncertain)
        {
            transaction.State = TransactionState.Uncertain;
            _uncertainTransactions++;
        }
        panel.MutationUncertain = true;
        panel.MutationUncertainReason = reason;
        if (TryGetOutputButtonForTransactionLocked(transaction, out var output))
        {
            output.State = BindingState.Tombstone;
        }
        if (transaction.OutputClosurePointer != 0
            && OutputClosures.TryGetValue(
                transaction.OutputClosurePointer,
                out var closure))
        {
            closure.State = BindingState.Tombstone;
        }
        _lastResult = reason;
        TryLogTransaction(
            transaction.BusinessGeneration,
            $"uncertain panel={FormatPointer(transaction.PanelPointer)} epoch={transaction.PanelEpoch} "
            + $"recipe={transaction.RecipeId} reason={reason}",
            TransactionLogKind.Critical);
    }

    public static void Attach(ManualLogSource log)
    {
        ArgumentNullException.ThrowIfNull(log);
        lock (StateRoot)
        {
            _log = log;
            if (_attachAttempted) return;
            _attachAttempted = true;
            _hookStatus = "resolving";
            _injectionArmed = false;
        }

        try
        {
            var hooks = ResolveExactHookTargets();
            var harmony = new Harmony(HarmonyId);

            // Route/block boundaries must exist before a synthetic row can be published.
            harmony.Patch(
                hooks.OnRecipeElementSelected,
                prefix: RequireClosedRecipeSelectionPatch(
                    hooks.OnRecipeElementSelected.GetParameters()[0].ParameterType,
                    Priority.First),
                finalizer: RequirePatchMethod(
                    nameof(AfterRecipeElementSelected),
                    Priority.Last));
            PatchPrefixFinalizer(
                harmony,
                hooks.CallSubmitAction,
                nameof(BeforeSubmitAction),
                nameof(AfterSubmitAction),
                Priority.First,
                Priority.Last);
            PatchPrefixFinalizer(
                harmony,
                hooks.OnOutputSelected,
                nameof(BeforeOutputSelected),
                nameof(AfterOutputSelected),
                Priority.First,
                Priority.Last);
            PatchPrefixFinalizer(
                harmony,
                hooks.OutputSubmitClosure,
                nameof(BeforeOutputSubmitClosure),
                nameof(AfterOutputSubmitClosure),
                Priority.First,
                Priority.Last);
            PatchPrefixPostfix(
                harmony,
                hooks.OnRecipeElementEnabled,
                nameof(BeforeRecipeElementEnabled),
                nameof(AfterRecipeElementEnabled),
                Priority.First,
                Priority.Last);
            PatchPrefixFinalizer(
                harmony,
                hooks.UpdateAllVisual,
                nameof(BeforeUpdateAllVisual),
                nameof(AfterUpdateAllVisual),
                Priority.First,
                Priority.Last);
            PatchPrefixFinalizer(
                harmony,
                hooks.OnPanelClose,
                nameof(BeforePanelClose),
                nameof(AfterPanelClose),
                Priority.First,
                Priority.Last);
            PatchPrefixFinalizer(
                harmony,
                hooks.OnPanelDestroyed,
                nameof(BeforePanelDestroyed),
                nameof(AfterPanelDestroyed),
                Priority.First,
                Priority.Last);

            // Injection is deliberately last and is armed only after every safety hook exists.
            PatchPostfix(
                harmony,
                hooks.UpdateRecipeField,
                nameof(AfterUpdateRecipeField),
                Priority.Last);
            lock (StateRoot)
            {
                _harmony = harmony;
                _injectionArmed = true;
                _hookStatus = "patched:9/9:safety-first";
                _lastResult = "recipe variant service armed";
            }
            TryLogInfo("Target recipe variant service patched 9 exact hooks.");
        }
        catch (Exception ex)
        {
            lock (StateRoot)
            {
                _injectionArmed = false;
                _hookStatus = $"error:{DescribeException(ex)}";
                _failures++;
                _lastResult = "attach failed; injection remains disarmed";
            }
            TryLogWarning(
                $"Target recipe variant service unavailable; no rows will be injected: {DescribeException(ex)}.");
        }
    }

    public static void RetireFailClosed(string reason)
    {
        var normalized = string.IsNullOrWhiteSpace(reason)
            ? "recipe variant state retired"
            : reason.Trim();
        lock (StateRoot)
        {
            foreach (var panel in Panels.Values) panel.Retired = true;
            foreach (var panelPointer in Panels.Keys.ToArray())
            {
                TombstonePanelButtonsLocked(panelPointer);
            }
            _lastResult = normalized;
        }
    }

    private static void AfterUpdateRecipeField(object __instance)
    {
        try
        {
            lock (StateRoot)
            {
                if (!_injectionArmed) return;
            }
            var lifecycle = RuntimeNightBusinessLifecycle.Snapshot;
            if (!lifecycle.IsActive)
            {
                RetirePanel(TryGetNativePointer(__instance), "night business is inactive");
                return;
            }
            TryInject(
                __instance,
                RuntimeUiPinningService.ReadTargetSet(),
                lifecycle.Generation,
                _updateAllVisualDepth > 0
                    ? RecipeSurfaceRefreshKind.FullVisual
                    : RecipeSurfaceRefreshKind.DirectRecipeField);
        }
        catch (Exception ex)
        {
            FailPanelSurfaceRefresh(
                TryGetNativePointer(__instance),
                $"recipe variant postfix threw: {DescribeException(ex)}");
        }
        finally { FlushDeferredTransactionLogs(); }
    }

    private static bool BeforeRecipeElementEnabled(
        object __instance,
        object __0,
        object __2,
        out EnableHookState __state)
    {
        try { return PrepareRecipeElementEnable(__instance, __0, __2, out __state); }
        finally { FlushDeferredTransactionLogs(); }
    }

    private static void AfterRecipeElementEnabled(
        EnableHookState __state)
    {
        try { CompleteRecipeElementEnable(__state, true); }
        finally { FlushDeferredTransactionLogs(); }
    }

    private static bool BeforeRecipeElementSelectedExact<TRecipe>(
        object __instance,
        ref TRecipe __0,
        object __2,
        out RecipeSelectionHookState __state)
        where TRecipe : class
    {
        try
        {
            object recipe = __0;
            var shouldRun = PrepareRecipeSelection(
                __instance,
                ref recipe,
                __2,
                out __state);
            if (shouldRun)
            {
                if (recipe is not TRecipe exactRecipe)
                {
                    __state.OriginalAllowed = false;
                    return false;
                }
                __0 = exactRecipe;
            }
            return shouldRun;
        }
        finally { FlushDeferredTransactionLogs(); }
    }

    private static Exception? AfterRecipeElementSelected(
        Exception? __exception,
        RecipeSelectionHookState __state)
    {
        try { return CompleteRecipeSelection(__state, __exception); }
        catch (Exception ex)
        {
            return __exception ?? new InvalidOperationException(
                "Recipe selection finalizer failed before switch callback execution.",
                ex);
        }
        finally { FlushDeferredTransactionLogs(); }
    }

    private static bool BeforeSubmitAction(
        object __instance,
        out SubmitHookState __state)
    {
        try { return BeginSubmit(__instance, out __state); }
        finally { FlushDeferredTransactionLogs(); }
    }

    private static Exception? AfterSubmitAction(
        Exception? __exception,
        SubmitHookState __state)
    {
        try { CompleteSubmit(__state, __exception); }
        catch
        {
            if (__state.ProbeInstalled
                && ReferenceEquals(_activeSubmit, __state.Probe))
            {
                _activeSubmit = __state.Probe?.Parent;
            }
            DisposePublicationLeaseNoThrow(__state.PublicationLease);
            __state.PublicationLease = null;
        }
        finally { FlushDeferredTransactionLogs(); }
        return __exception;
    }

    private static void BeforeUpdateAllVisual(
        object __instance,
        out UpdateVisualHookState __state)
    {
        var refreshScope = new FullVisualRefreshScope(
            _activeFullVisualRefreshScope);
        _activeFullVisualRefreshScope = refreshScope;
        __state = new UpdateVisualHookState(
            0,
            0,
            "",
            false,
            false,
            RefreshScopeEntered: true,
            RefreshScopeToken: refreshScope);
        _updateAllVisualDepth++;
        try
        {
            var panelPointer = TryGetNativePointer(__instance);
            var lifecycle = RuntimeNightBusinessLifecycle.Snapshot;
            refreshScope.PanelPointer = panelPointer;
            refreshScope.BusinessGeneration = lifecycle.Generation;
            var outputReset = default(OutputBindingResetReceipt);
            PanelTransaction? resetTransaction = null;
            var outputBindingReset = false;
            var outputResetError = "";
            if (panelPointer != 0 && lifecycle.IsActive)
            {
                lock (StateRoot)
                {
                    outputBindingReset = TryResetOutputForFullVisualLocked(
                        panelPointer,
                        lifecycle.Generation,
                        out resetTransaction,
                        out outputReset,
                        out outputResetError);
                    if (outputBindingReset)
                    {
                        if (resetTransaction == null
                            || !Panels.TryGetValue(panelPointer, out var resetPanel)
                            || !ReferenceEquals(
                                resetPanel.Transaction,
                                resetTransaction))
                        {
                            outputResetError =
                                "full visual output reset lost its exact owner";
                        }
                        else
                        {
                            refreshScope.ResetPanel = resetPanel;
                            refreshScope.ResetPanelEpoch = resetPanel.PanelEpoch;
                            refreshScope.ResetTransaction = resetTransaction;
                            refreshScope.ResetTransactionSequence =
                                resetTransaction.Sequence;
                            refreshScope.ResetTransactionIdentity =
                                resetTransaction.Identity;
                            refreshScope.OutputReset = outputReset;
                        }
                    }
                }
            }
            if (outputResetError.Length != 0)
            {
                throw new InvalidOperationException(outputResetError);
            }
            if (outputBindingReset)
            {
                LogOutputBindingReset(lifecycle.Generation, panelPointer, outputReset);
            }

            ApplyExtrasDuringNativeRefresh(__instance, out var mutationState);
            __state = mutationState with
            {
                RefreshScopeEntered = true,
                RefreshScopeToken = refreshScope,
            };
        }
        finally { FlushDeferredTransactionLogs(); }
    }

    private static Exception? AfterUpdateAllVisual(
        Exception? __exception,
        object __instance,
        UpdateVisualHookState __state)
    {
        try { CompleteUpdateVisual(__instance, __state, __exception); } catch { }
        finally
        {
            if (__state.RefreshScopeEntered
                && __state.RefreshScopeToken is FullVisualRefreshScope refreshScope)
            {
                if (ReferenceEquals(_activeFullVisualRefreshScope, refreshScope))
                {
                    _activeFullVisualRefreshScope = refreshScope.Parent;
                    if (_updateAllVisualDepth > 0) _updateAllVisualDepth--;
                }
                else
                {
                    _activeFullVisualRefreshScope = null;
                    _updateAllVisualDepth = 0;
                }
            }
            FlushDeferredTransactionLogs();
        }
        return __exception;
    }

    private static bool BeforeOutputSelected(
        object __instance,
        object __0,
        object __2,
        out OutputHookState __state)
    {
        try { return PrepareOutputSelection(__instance, __0, __2, out __state); }
        finally { FlushDeferredTransactionLogs(); }
    }

    private static Exception? AfterOutputSelected(
        Exception? __exception,
        object __2,
        OutputHookState __state)
    {
        try { return CompleteOutputSelection(__state, __2, __exception); }
        finally { FlushDeferredTransactionLogs(); }
    }

    private static bool BeforeOutputSubmitClosure(
        object __instance,
        out OutputClosureHookState __state)
    {
        try { return BeginOutputClosure(__instance, out __state); }
        finally { FlushDeferredTransactionLogs(); }
    }

    private static Exception? AfterOutputSubmitClosure(
        Exception? __exception,
        OutputClosureHookState __state)
    {
        try { CompleteOutputClosure(__state, __exception); } catch { }
        finally { FlushDeferredTransactionLogs(); }
        return __exception;
    }

    private static void BeforePanelClose(
        object __instance,
        out PanelTeardownToken __state)
    {
        __state = BeginPanelTeardown(__instance, captureCloseReceipt: true);
    }

    private static Exception? AfterPanelClose(
        Exception? __exception,
        PanelTeardownToken __state)
    {
        try { CompletePanelClose(__state, __exception == null); } catch { }
        finally { FlushDeferredTransactionLogs(); }
        return __exception;
    }

    private static void BeforePanelDestroyed(
        object __instance,
        out PanelTeardownToken __state)
    {
        __state = BeginPanelTeardown(__instance, captureCloseReceipt: false);
    }

    private static Exception? AfterPanelDestroyed(
        Exception? __exception,
        PanelTeardownToken __state)
    {
        try { CompletePanelDestroyed(__state, __exception == null); } catch { }
        finally { FlushDeferredTransactionLogs(); }
        return __exception;
    }

    internal static PanelTeardownToken BeginPanelTeardownForTests(object panel)
    {
        return BeginPanelTeardown(panel, captureCloseReceipt: true);
    }

    internal static PanelTeardownToken BeginPanelDestroyedForTests(object panel)
    {
        return BeginPanelTeardown(panel, captureCloseReceipt: false);
    }

    internal static void CompletePanelCloseForTests(
        PanelTeardownToken token,
        bool originalCompleted = true)
    {
        CompletePanelClose(token, originalCompleted);
    }

    internal static void CompletePanelDestroyedForTests(
        PanelTeardownToken token,
        bool originalCompleted = true)
    {
        CompletePanelDestroyed(token, originalCompleted);
    }

    private static PanelTeardownToken BeginPanelTeardown(
        object panel,
        bool captureCloseReceipt)
    {
        var panelPointer = TryGetNativePointer(panel);
        if (panelPointer == 0) return default;
        lock (StateRoot)
        {
            var panelEpoch = Panels.TryGetValue(panelPointer, out var state)
                ? state.PanelEpoch
                : Buttons.Values
                    .Where(binding => binding.PanelPointer == panelPointer)
                    .Select(binding => binding.PanelEpoch)
                    .DefaultIfEmpty(0)
                    .Max();
            var closeIdentity = default(FinalOutputIdentity);
            var closeReceiptPending = captureCloseReceipt
                && state != null
                && TryCaptureExpectedCloseLocked(state, out closeIdentity);
            var transaction = state?.Transaction;
            var switchAttempt = state?.SwitchAttempt;
            if (state != null)
            {
                state.Retired = true;
            }
            TombstonePanelButtonsLocked(panelPointer);
            return new PanelTeardownToken(
                panelPointer,
                panelEpoch,
                transaction != null,
                transaction?.Sequence ?? 0,
                transaction?.Identity ?? "",
                switchAttempt?.BusinessGeneration
                    ?? transaction?.BusinessGeneration
                    ?? 0,
                transaction?.MutationStarted ?? false,
                transaction?.State ?? TransactionState.PendingRecipeSubmit,
                closeReceiptPending,
                closeReceiptPending ? closeIdentity : default,
                switchAttempt != null,
                switchAttempt?.Sequence ?? 0,
                switchAttempt?.Identity ?? "",
                switchAttempt);
        }
    }

    private static bool TryCaptureExpectedCloseLocked(
        PanelState panel,
        out FinalOutputIdentity identity)
    {
        identity = default;
        var context = _activeOutputClosure;
        if (context == null
            || context.TerminalState != FinalOutputTerminalState.Pending
            || context.CloseReceiptRecorded
            || !ReferenceEquals(_activeSubmit, context.ActiveProbe)
            || _activeSubmit?.ButtonPointer != context.Identity.ButtonPointer
            || context.Identity.PanelPointer != panel.PanelPointer
            || !OutputClosures.TryGetValue(
                context.Identity.ClosurePointer,
                out var binding)
            || !TryValidateOutputClosureLocked(
                binding,
                TransactionState.OutputSubmitting,
                out var currentPanel,
                out var transaction)
            || !ReferenceEquals(currentPanel, panel)
            || transaction.Sequence != context.Identity.TransactionSequence
            || transaction.OutputPointer != context.Identity.OutputPointer)
        {
            return false;
        }
        identity = context.Identity;
        return true;
    }

    private static void CompletePanelCloseReceipt(PanelTeardownToken token)
    {
        if (!token.CloseReceiptPending) return;
        var context = _activeOutputClosure;
        if (context == null
            || context.TerminalState != FinalOutputTerminalState.Pending
            || context.CloseReceiptRecorded
            || !context.Identity.Equals(token.CloseIdentity)
            || !ReferenceEquals(_activeSubmit, context.ActiveProbe)
            || _activeSubmit?.ButtonPointer != token.CloseIdentity.ButtonPointer)
        {
            return;
        }
        context.CloseReceiptIdentity = token.CloseIdentity;
        context.CloseReceiptRecorded = true;
        TryLogTransaction(
            token.CloseIdentity.BusinessGeneration,
            $"close-receipt panel={FormatPointer(token.PanelPointer)} "
            + $"epoch={token.PanelEpoch} recipe={token.CloseIdentity.RecipeId} "
            + $"transaction={token.CloseIdentity.TransactionSequence} "
            + $"closure={FormatPointer(token.CloseIdentity.ClosurePointer)} "
            + $"output={FormatPointer(token.CloseIdentity.OutputPointer)}");
    }

    private static void CompletePanelClose(
        PanelTeardownToken token,
        bool originalCompleted)
    {
        if (token.PanelPointer == 0 || token.PanelEpoch <= 0) return;
        if (originalCompleted && token.CloseReceiptPending)
        {
            CompletePanelCloseReceipt(token);
        }

        lock (StateRoot)
        {
            HandleSwitchAttemptTeardownLocked(
                token,
                originalCompleted
                    ? "native panel close occurred during a recipe switch"
                    : "native panel close failed during a recipe switch");
            if (!token.TransactionCaptured) return;
            if (!TryGetTeardownTransactionLocked(token, out var transaction))
            {
                if (token.MutationStarted
                    && !IsTerminalTransactionState(token.TransactionStateAtPrefix))
                {
                    var reason = originalCompleted
                        ? "panel close lost its exact mutated transaction"
                        : "native panel close failed after mutation and lost its exact transaction";
                    LatchBusinessMutationUncertainLocked(
                        token.BusinessGeneration,
                        reason);
                    _uncertainTransactions++;
                    _lastResult = reason;
                }
                return;
            }
            if (IsTerminalTransactionState(transaction.State)) return;

            if (!originalCompleted)
            {
                if (transaction.MutationStarted)
                {
                    MarkTransactionUncertainLocked(
                        transaction,
                        "native panel close failed after recipe mutation");
                }
                return;
            }

            if (transaction.State != token.TransactionStateAtPrefix)
            {
                if (transaction.MutationStarted)
                {
                    MarkTransactionUncertainLocked(
                        transaction,
                        "recipe transaction changed during native panel close");
                }
                return;
            }

            switch (token.TransactionStateAtPrefix)
            {
                case TransactionState.Applied:
                case TransactionState.OutputPending:
                case TransactionState.OutputReady:
                    transaction.State = TransactionState.Cancelled;
                    _cancelledTransactions++;
                    _lastResult = "native panel close rolled back the selected recipe";
                    TryLogTransaction(
                        transaction.BusinessGeneration,
                        $"cancelled panel={FormatPointer(transaction.PanelPointer)} "
                        + $"epoch={transaction.PanelEpoch} recipe={transaction.RecipeId} "
                        + $"transaction={transaction.Sequence}");
                    return;

                case TransactionState.Applying:
                    MarkTransactionUncertainLocked(
                        transaction,
                        "native panel close completed during recipe mutation");
                    return;

                case TransactionState.OutputSubmitting:
                    var context = _activeOutputClosure;
                    if (!token.CloseReceiptPending
                        || context == null
                        || !context.CloseReceiptRecorded
                        || !context.CloseReceiptIdentity.Equals(token.CloseIdentity))
                    {
                        MarkTransactionUncertainLocked(
                            transaction,
                            "native panel close did not produce the exact final-output receipt");
                    }
                    return;

                case TransactionState.Uncertain:
                case TransactionState.PendingRecipeSubmit:
                case TransactionState.Cancelled:
                case TransactionState.Completed:
                case TransactionState.Rejected:
                    return;
            }
        }
    }

    private static bool TryGetTeardownTransactionLocked(
        PanelTeardownToken token,
        out PanelTransaction transaction)
    {
        transaction = null!;
        if (!Panels.TryGetValue(token.PanelPointer, out var panel)
            || panel.PanelEpoch != token.PanelEpoch
            || panel.BusinessGeneration != token.BusinessGeneration
            || panel.Transaction is not { } candidate
            || candidate.Sequence != token.TransactionSequence
            || !string.Equals(
                candidate.Identity,
                token.TransactionIdentity,
                StringComparison.Ordinal))
        {
            return false;
        }
        transaction = candidate;
        return true;
    }

    private static void HandleSwitchAttemptTeardownLocked(
        PanelTeardownToken token,
        string reason)
    {
        if (!token.SwitchAttemptCaptured) return;
        var retained = token.SwitchAttemptToken as RecipeSwitchAttempt;
        if (retained != null
            && retained.Sequence == token.SwitchAttemptSequence
            && string.Equals(
                retained.Identity,
                token.SwitchAttemptIdentity,
                StringComparison.Ordinal))
        {
            MarkSwitchAttemptUncertainLocked(retained, reason);
            return;
        }
        LatchBusinessMutationUncertainLocked(token.BusinessGeneration, reason);
        _uncertainTransactions++;
        _lastResult = reason;
    }

    private static bool IsTerminalTransactionState(TransactionState state)
    {
        return state is TransactionState.Cancelled
            or TransactionState.Completed
            or TransactionState.Rejected;
    }

    private static void CompletePanelDestroyed(
        PanelTeardownToken token,
        bool originalCompleted)
    {
        if (token.PanelPointer == 0 || token.PanelEpoch <= 0) return;
        lock (StateRoot)
        {
            HandleSwitchAttemptTeardownLocked(
                token,
                originalCompleted
                    ? "recipe panel was destroyed during a recipe switch"
                    : "recipe panel destroy failed during a recipe switch");
            if (token.TransactionCaptured
                && token.MutationStarted
                && !IsTerminalTransactionState(token.TransactionStateAtPrefix))
            {
                const string reason = "recipe panel was destroyed after native ingredient mutation";
                if (TryGetTeardownTransactionLocked(token, out var transaction))
                {
                    if (!IsTerminalTransactionState(transaction.State))
                    {
                        var context = _activeOutputClosure;
                        if (token.TransactionStateAtPrefix == TransactionState.OutputSubmitting
                            && context != null
                            && context.Identity.PanelPointer == token.PanelPointer
                            && context.Identity.TransactionSequence == token.TransactionSequence
                            && string.Equals(
                                context.Identity.TransactionIdentity,
                                token.TransactionIdentity,
                                StringComparison.Ordinal))
                        {
                            MarkFinalOutputUncertainLocked(context, reason);
                        }
                        else
                        {
                            MarkTransactionUncertainLocked(transaction, reason);
                        }
                    }
                }
                else
                {
                    LatchBusinessMutationUncertainLocked(
                        token.BusinessGeneration,
                        reason);
                    _uncertainTransactions++;
                    _lastResult = reason;
                }
            }
            if (!originalCompleted) return;

            foreach (var buttonPointer in Buttons
                .Where(pair =>
                    pair.Value.PanelPointer == token.PanelPointer
                    && pair.Value.PanelEpoch <= token.PanelEpoch)
                .Select(pair => pair.Key)
                .ToArray())
            {
                if (Buttons.TryGetValue(buttonPointer, out var binding)
                    && binding.PanelPointer == token.PanelPointer
                    && binding.PanelEpoch <= token.PanelEpoch)
                {
                    Buttons.Remove(buttonPointer);
                }
            }
            foreach (var closurePointer in OutputClosures
                .Where(pair =>
                    pair.Value.PanelPointer == token.PanelPointer
                    && pair.Value.PanelEpoch <= token.PanelEpoch)
                .Select(pair => pair.Key)
                .ToArray())
            {
                OutputClosures.Remove(closurePointer);
            }
            if (Panels.TryGetValue(token.PanelPointer, out var panel)
                && panel.PanelEpoch == token.PanelEpoch)
            {
                Panels.Remove(token.PanelPointer);
            }
        }
    }

    private static ExactHookTargets ResolveExactHookTargets()
    {
        var panelType = FindType(PanelTypeName) ?? throw new TypeLoadException(PanelTypeName);
        var recipeType = FindType(RecipeTypeName) ?? throw new TypeLoadException(RecipeTypeName);
        var clusterType = FindType(UiElementClusterTypeName)
            ?? throw new TypeLoadException(UiElementClusterTypeName);
        var buttonBaseType = FindType(UiButtonBaseTypeName)
            ?? throw new TypeLoadException(UiButtonBaseTypeName);
        var buttonSimpleType = FindType(UiButtonSimpleTypeName)
            ?? throw new TypeLoadException(UiButtonSimpleTypeName);
        var comboType = panelType.GetNestedType(
            "MatchedCookCombo",
            BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new TypeLoadException($"{PanelTypeName}+MatchedCookCombo");
        var outputClosureType = panelType.GetNestedType(
            "__c__DisplayClass79_0",
            BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new TypeLoadException($"{PanelTypeName}+__c__DisplayClass79_0");
        if (!buttonBaseType.IsAssignableFrom(buttonSimpleType))
        {
            throw new InvalidOperationException(
                "UIButtonSimple does not inherit the exact UIButtonBase type.");
        }

        return new ExactHookTargets(
            RequireExactMethod(panelType, "UpdateRecipeField", typeof(void)),
            RequireExactMethod(
                panelType,
                "OnRecipeElementEnabled",
                typeof(void),
                recipeType,
                clusterType,
                buttonSimpleType),
            RequireExactMethod(
                panelType,
                "OnRecipeElementSelected",
                typeof(void),
                recipeType,
                clusterType,
                buttonSimpleType),
            RequireExactMethod(panelType, "UpdateAllVisual", typeof(void)),
            RequireExactMethod(
                panelType,
                "OnOutputSelected",
                typeof(void),
                comboType,
                clusterType,
                buttonSimpleType),
            RequireExactMethod(
                outputClosureType,
                "Method_Internal_Void_PDM_0",
                typeof(void)),
            RequireExactMethod(panelType, "OnPanelClose", typeof(void)),
            RequireExactMethod(panelType, "OnPanelDestroyed", typeof(void)),
            RequireExactMethod(buttonBaseType, "CallSubmitAction", typeof(void)));
    }

    private static MethodInfo RequireExactMethod(
        Type type,
        string name,
        Type returnType,
        params Type[] parameterTypes)
    {
        var matches = type.GetMethods(
                BindingFlags.Public
                | BindingFlags.NonPublic
                | BindingFlags.Instance
                | BindingFlags.DeclaredOnly)
            .Where(method =>
                method.Name == name
                && !method.IsStatic
                && method.ReturnType == returnType
                && method.GetParameters()
                    .Select(parameter => parameter.ParameterType)
                    .SequenceEqual(parameterTypes))
            .ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new MissingMethodException(type.FullName, name);
    }

    private static Type? FindType(string fullName)
    {
        var direct = Type.GetType(fullName, false);
        if (direct != null) return direct;
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var type = assembly.GetType(fullName, false);
                if (type != null) return type;
            }
            catch
            {
                // Unrelated generated interop assemblies may fail during lookup.
            }
        }
        return null;
    }

    private static void PatchPostfix(
        Harmony harmony,
        MethodInfo target,
        string patchName,
        int priority)
    {
        harmony.Patch(target, postfix: RequirePatchMethod(patchName, priority));
    }

    private static void PatchPrefixPostfix(
        Harmony harmony,
        MethodInfo target,
        string prefixName,
        string postfixName,
        int prefixPriority,
        int postfixPriority)
    {
        harmony.Patch(
            target,
            prefix: RequirePatchMethod(prefixName, prefixPriority),
            postfix: RequirePatchMethod(postfixName, postfixPriority));
    }

    private static void PatchPrefixFinalizer(
        Harmony harmony,
        MethodInfo target,
        string prefixName,
        string finalizerName,
        int prefixPriority,
        int finalizerPriority)
    {
        harmony.Patch(
            target,
            prefix: RequirePatchMethod(prefixName, prefixPriority),
            finalizer: RequirePatchMethod(finalizerName, finalizerPriority));
    }

    private static HarmonyMethod RequirePatchMethod(string name, int priority)
    {
        var method = typeof(RuntimeTargetRecipeVariantService).GetMethod(
            name,
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(
                typeof(RuntimeTargetRecipeVariantService).FullName,
                name);
        return new HarmonyMethod(method) { priority = priority };
    }

    private static HarmonyMethod RequireClosedRecipeSelectionPatch(
        Type recipeType,
        int priority)
    {
        var definition = typeof(RuntimeTargetRecipeVariantService).GetMethod(
            nameof(BeforeRecipeElementSelectedExact),
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(
                typeof(RuntimeTargetRecipeVariantService).FullName,
                nameof(BeforeRecipeElementSelectedExact));
        if (!definition.IsGenericMethodDefinition)
        {
            throw new InvalidOperationException(
                "The exact recipe-selection prefix is not a generic method definition.");
        }
        return new HarmonyMethod(definition.MakeGenericMethod(recipeType))
        {
            priority = priority,
        };
    }

    private static nint TryGetNativePointer(object instance)
    {
        try
        {
            return instance == null ? 0 : _runtime.GetNativePointer(instance);
        }
        catch
        {
            return 0;
        }
    }

    private static bool TryCleanSubmitCallbackExclusive(
        object button,
        nint buttonPointer,
        ButtonBinding? expectedBinding,
        out string error)
    {
        error = "";
        ButtonCleanupLease lease;
        lock (StateRoot)
        {
            if (ButtonCleanupLeases.TryGetValue(buttonPointer, out var activeLease))
            {
                activeLease.Contended = true;
                error = "button submit callback cleanup is already in progress";
                return false;
            }
            lease = new ButtonCleanupLease(
                buttonPointer,
                Interlocked.Increment(ref _nextButtonCleanupSequence));
            ButtonCleanupLeases[buttonPointer] = lease;
        }

        var cleaned = false;
        try
        {
            cleaned = _runtime.TryCleanSubmitCallback(button, out error);
        }
        catch (Exception ex)
        {
            error = DescribeException(ex);
        }

        lock (StateRoot)
        {
            var ownsLease = ButtonCleanupLeases.TryGetValue(
                    buttonPointer,
                    out var currentLease)
                && ReferenceEquals(currentLease, lease)
                && currentLease.ButtonPointer == buttonPointer
                && currentLease.Sequence == lease.Sequence;
            if (ownsLease)
            {
                ButtonCleanupLeases.Remove(buttonPointer);
            }
            var bindingIsStable = expectedBinding == null
                ? !Buttons.ContainsKey(buttonPointer)
                : Buttons.TryGetValue(buttonPointer, out var currentBinding)
                    && ReferenceEquals(currentBinding, expectedBinding);
            if (!ownsLease || lease.Contended || !bindingIsStable)
            {
                if (expectedBinding != null
                    && Buttons.TryGetValue(buttonPointer, out var invalidatedBinding)
                    && ReferenceEquals(invalidatedBinding, expectedBinding))
                {
                    invalidatedBinding.State = BindingState.Tombstone;
                }
                cleaned = false;
                var ownershipError = !ownsLease
                    ? "button submit callback cleanup lease changed"
                    : lease.Contended
                        ? "button submit callback cleanup was re-entered"
                        : "button binding changed during submit callback cleanup";
                error = string.IsNullOrEmpty(error)
                    ? ownershipError
                    : $"{error}; {ownershipError}";
            }
        }
        return cleaned;
    }

    private static bool IsButtonCleanupInProgressLocked(nint buttonPointer)
    {
        if (!ButtonCleanupLeases.TryGetValue(buttonPointer, out var lease))
        {
            return false;
        }
        lease.Contended = true;
        return true;
    }

    private static bool Fail(string reason)
    {
        lock (StateRoot)
        {
            _failures++;
            _lastResult = reason;
        }
        TryLogWarning($"Target recipe variant rejected: {reason}.");
        return false;
    }

    private static string DescribeException(Exception exception)
    {
        return exception.GetBaseException().Message;
    }

    private static string FormatPointer(nint pointer)
    {
        return $"0x{unchecked((ulong)(long)pointer):x}";
    }

    private static void TryLogInfo(string message)
    {
        ManualLogSource? log;
        lock (StateRoot) log = _log;
        try { log?.LogInfo(message); } catch { }
    }

    private static void TryLogTransaction(
        long businessGeneration,
        string message,
        TransactionLogKind kind = TransactionLogKind.Action)
    {
        var defer = Monitor.IsEntered(StateRoot);
        ManualLogSource? log;
        lock (StateRoot)
        {
            if (businessGeneration <= 0
                || businessGeneration < _logBusinessGeneration)
            {
                return;
            }
            if (businessGeneration > _logBusinessGeneration)
            {
                _logBusinessGeneration = businessGeneration;
                _transactionLogs = 0;
                _criticalTransactionLogs = 0;
                _actionTransactionLogs = 0;
                _surfaceTransactionLogs = 0;
                _safetyTransactionLogs = 0;
            }
            switch (kind)
            {
                case TransactionLogKind.Critical:
                    if (_transactionLogs >= MaximumTransactionLogsPerBusiness) return;
                    if (_criticalTransactionLogs >= MaximumCriticalTransactionLogsPerBusiness) return;
                    _criticalTransactionLogs++;
                    _transactionLogs++;
                    break;
                case TransactionLogKind.Action:
                    if (_transactionLogs >= MaximumTransactionLogsPerBusiness) return;
                    if (_actionTransactionLogs >= MaximumActionTransactionLogsPerBusiness) return;
                    _actionTransactionLogs++;
                    _transactionLogs++;
                    break;
                case TransactionLogKind.Surface:
                    if (_transactionLogs >= MaximumTransactionLogsPerBusiness) return;
                    if (_surfaceTransactionLogs >= MaximumSurfaceTransactionLogsPerBusiness) return;
                    _surfaceTransactionLogs++;
                    _transactionLogs++;
                    break;
                case TransactionLogKind.Safety:
                    if (_safetyTransactionLogs >= MaximumSafetyTransactionLogsPerBusiness) return;
                    _safetyTransactionLogs++;
                    break;
                default:
                    return;
            }
            log = _log;
        }
        if (log == null) return;
        var formatted = $"Target recipe variant {message}";
        if (defer)
        {
            (_deferredTransactionLogs ??= new List<string>()).Add(formatted);
            return;
        }
        try { log.LogInfo(formatted); } catch { }
    }

    private static void FlushDeferredTransactionLogs()
    {
        if (Monitor.IsEntered(StateRoot)
            || _deferredTransactionLogs is not { Count: > 0 } pending)
        {
            return;
        }
        _deferredTransactionLogs = null;
        ManualLogSource? log;
        lock (StateRoot) log = _log;
        if (log == null) return;
        foreach (var message in pending)
        {
            try { log.LogInfo(message); } catch { }
        }
    }

    private static void TryLogWarning(string message)
    {
        ManualLogSource? log;
        lock (StateRoot)
        {
            if (_warningLogs >= MaximumWarningLogs) return;
            _warningLogs++;
            log = _log;
        }
        try { log?.LogWarning(message); } catch { }
    }

    private sealed class PanelState
    {
        public PanelState(
            nint panelPointer,
            long panelEpoch,
            long businessGeneration,
            RuntimeUiTargetSetSnapshot targetSet,
            nint recipeListPointer,
            IEnumerable<ControlledRecipe> controlledRecipes,
            PanelTransaction? transaction,
            RecipeSelectionIntent? selectionIntent,
            RecipeSwitchAttempt? switchAttempt,
            bool mutationUncertain,
            string mutationUncertainReason)
        {
            PanelPointer = panelPointer;
            PanelEpoch = panelEpoch;
            BusinessGeneration = businessGeneration;
            TargetSet = targetSet;
            RecipeListPointer = recipeListPointer;
            ControlledRecipes = controlledRecipes.ToDictionary(item => item.RecipeId);
            Transaction = transaction;
            SelectionIntent = selectionIntent;
            SwitchAttempt = switchAttempt;
            MutationUncertain = mutationUncertain;
            MutationUncertainReason = mutationUncertainReason;
        }

        public nint PanelPointer { get; }
        public long PanelEpoch { get; }
        public long BusinessGeneration { get; }
        public RuntimeUiTargetSetSnapshot TargetSet { get; }
        public nint RecipeListPointer { get; }
        public Dictionary<int, ControlledRecipe> ControlledRecipes { get; }
        public PanelTransaction? Transaction { get; set; }
        public RecipeSelectionIntent? SelectionIntent { get; set; }
        public RecipeSwitchAttempt? SwitchAttempt { get; set; }
        public bool MutationUncertain { get; set; }
        public string MutationUncertainReason { get; set; }
        public bool Retired { get; set; }
    }

    private sealed class ButtonBinding
    {
        public ButtonBinding(
            nint panelPointer,
            long panelEpoch,
            nint buttonPointer,
            nint recipePointer,
            int recipeId,
            long targetGeneration,
            string planIdentity,
            RuntimeUiTargetKinds claims,
            RowKind rowKind,
            long transactionSequence,
            string transactionIdentity,
            BindingState state)
        {
            Sequence = Interlocked.Increment(ref _nextButtonBindingSequence);
            PanelPointer = panelPointer;
            PanelEpoch = panelEpoch;
            ButtonPointer = buttonPointer;
            RecipePointer = recipePointer;
            RecipeId = recipeId;
            TargetGeneration = targetGeneration;
            PlanIdentity = planIdentity;
            Claims = claims;
            RowKind = rowKind;
            TransactionSequence = transactionSequence;
            TransactionIdentity = transactionIdentity;
            _state = state;
        }

        public long Sequence { get; }
        public nint PanelPointer { get; }
        public long PanelEpoch { get; }
        public nint ButtonPointer { get; }
        public nint RecipePointer { get; }
        public int RecipeId { get; }
        public long TargetGeneration { get; }
        public string PlanIdentity { get; }
        public RuntimeUiTargetKinds Claims { get; }
        public RowKind RowKind { get; }
        public long TransactionSequence { get; }
        public string TransactionIdentity { get; }
        private BindingState _state;
        public BindingState State
        {
            get => _state;
            set
            {
                if (_state == value) return;
                _state = value;
                StateVersion++;
            }
        }
        public long StateVersion { get; private set; }
    }

    private sealed class ButtonCleanupLease
    {
        public ButtonCleanupLease(nint buttonPointer, long sequence)
        {
            ButtonPointer = buttonPointer;
            Sequence = sequence;
        }

        public nint ButtonPointer { get; }
        public long Sequence { get; }
        public bool Contended { get; set; }
    }

    internal sealed class ActiveSubmitContext
    {
        public ActiveSubmitContext(nint buttonPointer)
        {
            ButtonPointer = buttonPointer;
        }

        public nint ButtonPointer { get; }
        public ActiveSubmitContext? Parent { get; set; }
        public nint PanelPointer { get; private set; }
        public long PanelEpoch { get; set; }
        public nint SourceRecipePointer { get; private set; }
        public string TransactionIdentity { get; private set; } = "";
        public long SwitchAttemptSequence { get; private set; }
        public string SwitchAttemptIdentity { get; private set; } = "";
        internal object? SwitchAttemptToken { get; private set; }
        public long SwitchBusinessGeneration { get; private set; }
        public TargetRecipeVariantPublicationLease? SwitchPublicationLease { get; set; }
        public SubmitKind Kind { get; private set; }

        public void AttachRecipe(
            nint panelPointer,
            long panelEpoch,
            nint sourceRecipePointer,
            string transactionIdentity)
        {
            Attach(
                SubmitKind.Recipe,
                panelPointer,
                panelEpoch,
                sourceRecipePointer,
                transactionIdentity);
        }

        public void AttachOutput(
            nint panelPointer,
            long panelEpoch,
            string transactionIdentity)
        {
            Attach(SubmitKind.Output, panelPointer, panelEpoch, 0, transactionIdentity);
        }

        public void AttachSwitch(
            nint panelPointer,
            long panelEpoch,
            long attemptSequence,
            string attemptIdentity,
            object attemptToken,
            long businessGeneration)
        {
            Kind = SubmitKind.RecipeSwitch;
            PanelPointer = panelPointer;
            PanelEpoch = panelEpoch;
            SourceRecipePointer = 0;
            TransactionIdentity = "";
            SwitchAttemptSequence = attemptSequence;
            SwitchAttemptIdentity = attemptIdentity;
            SwitchAttemptToken = attemptToken;
            SwitchBusinessGeneration = businessGeneration;
        }

        private void Attach(
            SubmitKind kind,
            nint panelPointer,
            long panelEpoch,
            nint sourceRecipePointer,
            string transactionIdentity)
        {
            Kind = kind;
            PanelPointer = panelPointer;
            PanelEpoch = panelEpoch;
            SourceRecipePointer = sourceRecipePointer;
            TransactionIdentity = transactionIdentity;
        }
    }

    internal readonly record struct FinalOutputIdentity(
        nint PanelPointer,
        long PanelEpoch,
        nint ButtonPointer,
        long ButtonBindingSequence,
        nint ClosurePointer,
        nint ComboPointer,
        nint OutputPointer,
        long TargetGeneration,
        long BusinessGeneration,
        long TransactionSequence,
        int RecipeId,
        string TransactionIdentity);

    private readonly record struct InsertionAttemptIdentity(
        long BusinessGeneration,
        long TargetGeneration);

    private sealed class FullVisualRefreshScope
    {
        public FullVisualRefreshScope(FullVisualRefreshScope? parent)
        {
            Parent = parent;
        }

        public FullVisualRefreshScope? Parent { get; }
        public nint PanelPointer { get; set; }
        public long BusinessGeneration { get; set; }
        public PanelState? ResetPanel { get; set; }
        public long ResetPanelEpoch { get; set; }
        public PanelTransaction? ResetTransaction { get; set; }
        public long ResetTransactionSequence { get; set; }
        public string ResetTransactionIdentity { get; set; } = "";
        public OutputBindingResetReceipt OutputReset { get; set; }
        public bool OutputResetConsumed { get; set; }
    }

    private readonly record struct OutputBindingResetReceipt(
        long OutputPanelEpoch,
        nint ButtonPointer,
        long ButtonBindingSequence,
        nint ComboPointer,
        nint ClosurePointer,
        nint OutputPointer);

    internal sealed class ActiveOutputClosureContext
    {
        public ActiveOutputClosureContext(
            FinalOutputIdentity identity,
            ActiveSubmitContext activeProbe)
        {
            Identity = identity;
            ActiveProbe = activeProbe;
        }

        public FinalOutputIdentity Identity { get; }
        public ActiveSubmitContext ActiveProbe { get; }
        public bool CloseReceiptRecorded { get; set; }
        public FinalOutputIdentity CloseReceiptIdentity { get; set; }
        public FinalOutputTerminalState TerminalState { get; set; }
    }

    internal sealed class OutputClosureBinding
    {
        public OutputClosureBinding(
            nint closurePointer,
            nint panelPointer,
            long panelEpoch,
            nint buttonPointer,
            nint comboPointer,
            nint outputPointer,
            long targetGeneration,
            long buttonBindingSequence,
            long transactionSequence,
            string transactionIdentity,
            BindingState state)
        {
            ClosurePointer = closurePointer;
            PanelPointer = panelPointer;
            PanelEpoch = panelEpoch;
            ButtonPointer = buttonPointer;
            ComboPointer = comboPointer;
            OutputPointer = outputPointer;
            TargetGeneration = targetGeneration;
            ButtonBindingSequence = buttonBindingSequence;
            TransactionSequence = transactionSequence;
            TransactionIdentity = transactionIdentity;
            State = state;
        }

        public nint ClosurePointer { get; }
        public nint PanelPointer { get; }
        public long PanelEpoch { get; }
        public nint ButtonPointer { get; }
        public nint ComboPointer { get; }
        public nint OutputPointer { get; }
        public long TargetGeneration { get; }
        public long ButtonBindingSequence { get; }
        public long TransactionSequence { get; }
        public string TransactionIdentity { get; }
        public BindingState State { get; set; }
    }

    private sealed class ControlledRecipe
    {
        public ControlledRecipe(
            int recipeId,
            nint authoritativePointer,
            IReadOnlyList<int> baseIngredientIds,
            RuntimeUiTargetKinds baseClaims,
            string baseIdentity)
        {
            RecipeId = recipeId;
            AuthoritativePointer = authoritativePointer;
            BaseIngredientIds = baseIngredientIds.ToArray();
            BaseClaims = baseClaims;
            BaseIdentity = baseIdentity;
            SyntheticPlans = new Dictionary<nint, TargetRecipeVariantPlan>();
        }

        public int RecipeId { get; }
        public nint AuthoritativePointer { get; }
        public int[] BaseIngredientIds { get; }
        public RuntimeUiTargetKinds BaseClaims { get; }
        public string BaseIdentity { get; }
        public Dictionary<nint, TargetRecipeVariantPlan> SyntheticPlans { get; }
        public bool Complete { get; set; }
    }

    private sealed class PendingRecipeInsertion
    {
        public PendingRecipeInsertion(
            int baseIndex,
            int planIndex,
            object recipe,
            ControlledRecipe owner,
            nint pointer,
            IReadOnlyList<int> fullIngredientIds,
            int cookCount)
        {
            BaseIndex = baseIndex;
            PlanIndex = planIndex;
            Recipe = recipe;
            Owner = owner;
            Pointer = pointer;
            FullIngredientIds = fullIngredientIds.ToArray();
            CookCount = cookCount;
        }

        public int BaseIndex { get; }
        public int PlanIndex { get; }
        public object Recipe { get; }
        public ControlledRecipe Owner { get; }
        public nint Pointer { get; }
        public int[] FullIngredientIds { get; }
        public int CookCount { get; }
    }

    private sealed class PanelTransaction
    {
        public PanelTransaction(
            nint panelPointer,
            long panelEpoch,
            long targetGeneration,
            long businessGeneration,
            int recipeId,
            nint authoritativePointer,
            nint sourceButtonPointer,
            nint sourceRecipePointer,
            TargetRecipeVariantPlan plan)
        {
            Sequence = Interlocked.Increment(ref _nextTransactionSequence);
            PanelPointer = panelPointer;
            PanelEpoch = panelEpoch;
            OriginTargetGeneration = targetGeneration;
            BusinessGeneration = businessGeneration;
            RecipeId = recipeId;
            AuthoritativePointer = authoritativePointer;
            SourceButtonPointer = sourceButtonPointer;
            SourceRecipePointer = sourceRecipePointer;
            PlanIdentity = plan.Identity;
            BaseIngredientIds = plan.BaseIngredientIds.ToArray();
            ExtraIngredientIds = plan.ExtraIngredientIds.ToArray();
            Identity = $"{Sequence}:{panelPointer:x}:{panelEpoch}:{targetGeneration}:"
                + $"{sourceButtonPointer:x}:{sourceRecipePointer:x}:{plan.Identity}";
            State = TransactionState.PendingRecipeSubmit;
        }

        public nint PanelPointer { get; }
        public long Sequence { get; }
        public long PanelEpoch { get; set; }
        public long OriginTargetGeneration { get; }
        public long BusinessGeneration { get; }
        public int RecipeId { get; }
        public nint AuthoritativePointer { get; }
        public nint SourceButtonPointer { get; }
        public nint SourceRecipePointer { get; }
        public string PlanIdentity { get; }
        public string Identity { get; }
        public int[] BaseIngredientIds { get; }
        public int[] ExtraIngredientIds { get; }
        public TransactionState State { get; set; }
        public nint OutputButtonPointer { get; set; }
        public nint OutputComboPointer { get; set; }
        public nint OutputClosurePointer { get; set; }
        public nint OutputPointer { get; set; }
        public long OutputPanelEpoch { get; set; }
        public bool MutationStarted { get; set; }
        public bool MutationReceiptConfirmed { get; set; }
        public nint SelectedIngredientListPointer { get; set; }
        public int ExtraCostMultiplier { get; set; }
        public bool IsFreeCook { get; set; }
        public string LastSelectedVisualSignature { get; set; } = "";
    }

    private sealed class RecipeSelectionIntent
    {
        public RecipeSelectionIntent(
            PanelState panel,
            ButtonBinding? binding,
            RecipeDestinationKind destinationKind,
            nint sourceRowRecipePointer,
            nint authoritativeRecipePointer,
            int recipeId,
            IReadOnlyList<int> baseIngredientIds,
            IReadOnlyList<int> extraIngredientIds,
            string planIdentity)
        {
            Sequence = Interlocked.Increment(ref _nextSelectionIntentSequence);
            PanelPointer = panel.PanelPointer;
            PanelEpoch = panel.PanelEpoch;
            BusinessGeneration = panel.BusinessGeneration;
            TargetGeneration = panel.TargetSet.Generation;
            ButtonPointer = binding?.ButtonPointer ?? 0;
            ButtonBindingSequence = binding?.Sequence ?? 0;
            DestinationKind = destinationKind;
            SourceRowRecipePointer = sourceRowRecipePointer;
            AuthoritativeRecipePointer = authoritativeRecipePointer;
            RecipeId = recipeId;
            BaseIngredientIds = baseIngredientIds.ToArray();
            ExtraIngredientIds = extraIngredientIds.ToArray();
            PlanIdentity = planIdentity;
            TargetSet = panel.TargetSet;
        }

        public long Sequence { get; }
        public nint PanelPointer { get; }
        public long PanelEpoch { get; set; }
        public long BusinessGeneration { get; }
        public long TargetGeneration { get; }
        public nint ButtonPointer { get; set; }
        public long ButtonBindingSequence { get; }
        public RecipeDestinationKind DestinationKind { get; }
        public nint SourceRowRecipePointer { get; }
        public nint AuthoritativeRecipePointer { get; }
        public int RecipeId { get; }
        public int[] BaseIngredientIds { get; }
        public int[] ExtraIngredientIds { get; }
        public string PlanIdentity { get; }
        public RuntimeUiTargetSetSnapshot TargetSet { get; }
    }

    private sealed class RecipeSwitchAttempt
    {
        public RecipeSwitchAttempt(
            PanelState panel,
            PanelTransaction source,
            RecipeSelectionIntent destination,
            nint selectedIngredientListPointer,
            bool isFreeCook,
            int extraCostMultiplier,
            IReadOnlyDictionary<int, int> inventoryBefore)
        {
            Sequence = Interlocked.Increment(ref _nextSwitchAttemptSequence);
            Identity = $"{Sequence}:{panel.PanelPointer:x}:{panel.PanelEpoch}:"
                + $"{source.Sequence}:{destination.Sequence}";
            PanelPointer = panel.PanelPointer;
            PanelEpoch = panel.PanelEpoch;
            BusinessGeneration = panel.BusinessGeneration;
            TargetGeneration = panel.TargetSet.Generation;
            SourceTransaction = source;
            SourceStateAtArm = source.State;
            Destination = destination;
            SelectedIngredientListPointer = selectedIngredientListPointer;
            IsFreeCook = isFreeCook;
            ExtraCostMultiplier = extraCostMultiplier;
            InventoryBefore = new Dictionary<int, int>(inventoryBefore);
            State = RecipeSwitchAttemptState.Armed;
        }

        public long Sequence { get; }
        public string Identity { get; }
        public nint PanelPointer { get; }
        public long PanelEpoch { get; set; }
        public long BusinessGeneration { get; }
        public long TargetGeneration { get; }
        public PanelTransaction SourceTransaction { get; }
        public TransactionState SourceStateAtArm { get; }
        public RecipeSelectionIntent Destination { get; }
        public nint SelectedIngredientListPointer { get; }
        public bool IsFreeCook { get; }
        public int ExtraCostMultiplier { get; }
        public Dictionary<int, int> InventoryBefore { get; }
        public RecipeSwitchAttemptState State { get; set; }
        public long DestinationTransactionSequence { get; set; }
        public string DestinationTransactionIdentity { get; set; } = "";
    }

    internal sealed class SubmitHookState
    {
        public TargetRecipeVariantPublicationLease? PublicationLease { get; set; }
        internal ActiveSubmitContext? Probe { get; set; }
        public nint PanelPointer { get; set; }
        public long PanelEpoch { get; set; }
        public long TransactionSequence { get; set; }
        public string TransactionIdentity { get; set; } = "";
        public nint SourceRecipePointer { get; set; }
        public long ButtonBindingSequence { get; set; }
        public SubmitKind Kind { get; set; }
        public bool ProbeInstalled { get; set; }
        public bool OriginalAllowed { get; set; }
    }

    internal sealed class RecipeSelectionHookState
    {
        internal object? Intent { get; set; }
        public bool OriginalAllowed { get; set; }
    }

    internal sealed class OutputClosureHookState
    {
        public TargetRecipeVariantPublicationLease? PublicationLease { get; set; }
        internal ActiveSubmitContext? ActiveProbe { get; set; }
        internal OutputClosureBinding? Binding { get; set; }
        internal ActiveOutputClosureContext? TerminalContext { get; set; }
        public bool Tracked { get; set; }
        public bool OriginalAllowed { get; set; }
    }

    internal readonly record struct OutputHookState(
        nint PanelPointer,
        long PanelEpoch,
        nint ButtonPointer,
        long TransactionSequence,
        string TransactionIdentity,
        long BindingSequence,
        OutputHookDisposition Disposition,
        string Reason = "",
        bool DisableButton = false);

    internal readonly record struct EnableHookState(
        nint PanelPointer,
        long PanelEpoch,
        nint ButtonPointer,
        nint RecipePointer,
        long BindingSequence,
        bool Pending);

    private readonly record struct ButtonBindingObservation(
        ButtonBinding? Binding,
        long Sequence,
        BindingState State,
        long StateVersion);

    internal readonly record struct PanelTeardownToken(
        nint PanelPointer,
        long PanelEpoch,
        bool TransactionCaptured,
        long TransactionSequence,
        string TransactionIdentity,
        long BusinessGeneration,
        bool MutationStarted,
        TransactionState TransactionStateAtPrefix,
        bool CloseReceiptPending,
        FinalOutputIdentity CloseIdentity,
        bool SwitchAttemptCaptured,
        long SwitchAttemptSequence,
        string SwitchAttemptIdentity,
        object? SwitchAttemptToken);

    internal readonly record struct UpdateVisualHookState(
        nint PanelPointer,
        long TransactionSequence,
        string TransactionIdentity,
        bool MutationApplied,
        bool CaptureSelectedVisual,
        long SwitchAttemptSequence = 0,
        string SwitchAttemptIdentity = "",
        bool RefreshScopeEntered = false,
        object? RefreshScopeToken = null);

    private enum RowKind
    {
        Authoritative,
        Synthetic,
        Output,
    }

    private enum TransactionLogKind
    {
        Critical,
        Action,
        Surface,
        Safety,
    }

    internal enum BindingState
    {
        Pending,
        Ready,
        AwaitingRebind,
        Tombstone,
    }

    internal enum OutputHookDisposition
    {
        None,
        RegisterExact,
        SuppressAfterOriginal,
        AbortBeforeOriginal,
    }

    internal enum FinalOutputTerminalState
    {
        Pending,
        Completed,
        Uncertain,
    }

    internal enum SubmitKind
    {
        None,
        Recipe,
        Output,
        RecipeSwitch,
    }

    internal enum TransactionState
    {
        PendingRecipeSubmit,
        Applying,
        Applied,
        OutputPending,
        OutputReady,
        OutputSubmitting,
        Switching,
        Cancelled,
        Completed,
        Rejected,
        Uncertain,
    }

    internal enum RecipeSurfaceRefreshKind
    {
        DirectRecipeField,
        FullVisual,
    }

    private enum RecipeDestinationKind
    {
        Ordinary,
        Base,
        Variant,
    }

    private enum RecipeSwitchAttemptState
    {
        Armed,
        ReceiptObserved,
        VisualCompleted,
        Completed,
        Uncertain,
    }

    private sealed record ExactHookTargets(
        MethodInfo UpdateRecipeField,
        MethodInfo OnRecipeElementEnabled,
        MethodInfo OnRecipeElementSelected,
        MethodInfo UpdateAllVisual,
        MethodInfo OnOutputSelected,
        MethodInfo OutputSubmitClosure,
        MethodInfo OnPanelClose,
        MethodInfo OnPanelDestroyed,
        MethodInfo CallSubmitAction);
}

internal sealed class TargetRecipeVariantPlan
{
    private readonly List<string> _targetRevisions = new();

    public TargetRecipeVariantPlan(
        int recipeId,
        IReadOnlyList<int> expectedIngredientIds,
        IReadOnlyList<int> extraIngredientIds,
        RuntimeUiTargetKind kind,
        string targetRevision)
    {
        RecipeId = recipeId;
        ExpectedIngredientIds = Array.AsReadOnly(expectedIngredientIds.ToArray());
        BaseIngredientIds = Array.Empty<int>();
        ExtraIngredientIds = Array.AsReadOnly(extraIngredientIds.ToArray());
        AddClaim(kind, targetRevision);
    }

    private TargetRecipeVariantPlan(
        int recipeId,
        IReadOnlyList<int> expectedIngredientIds,
        IReadOnlyList<int> baseIngredientIds,
        IReadOnlyList<int> extraIngredientIds,
        RuntimeUiTargetKinds claims,
        IEnumerable<string> targetRevisions)
    {
        RecipeId = recipeId;
        ExpectedIngredientIds = Array.AsReadOnly(expectedIngredientIds.ToArray());
        BaseIngredientIds = Array.AsReadOnly(baseIngredientIds.ToArray());
        ExtraIngredientIds = Array.AsReadOnly(extraIngredientIds.ToArray());
        Claims = claims;
        _targetRevisions.AddRange(targetRevisions);
    }

    public int RecipeId { get; }
    public IReadOnlyList<int> ExpectedIngredientIds { get; }
    public IReadOnlyList<int> BaseIngredientIds { get; }
    public IReadOnlyList<int> ExtraIngredientIds { get; }
    public RuntimeUiTargetKinds Claims { get; private set; }
    public string RevisionFingerprint => BuildFingerprint(_targetRevisions);
    public string Identity => $"variant:{RecipeId}:{string.Join(",", ExtraIngredientIds)}:{RevisionFingerprint}";

    public void AddClaim(RuntimeUiTargetKind kind, string targetRevision)
    {
        Claims |= kind == RuntimeUiTargetKind.Rare
            ? RuntimeUiTargetKinds.Rare
            : RuntimeUiTargetKinds.Normal;
        _targetRevisions.Add(targetRevision);
    }

    public void AddClaim(
        RuntimeUiTargetKind kind,
        string targetRevision,
        IReadOnlyList<int> expectedIngredientIds)
    {
        if (!ExpectedIngredientIds.SequenceEqual(expectedIngredientIds))
        {
            throw new InvalidOperationException(
                $"merged targets for recipe {RecipeId} disagree on the ingredient set");
        }
        AddClaim(kind, targetRevision);
    }

    public TargetRecipeVariantPlan WithBaseIngredients(
        IReadOnlyList<int> baseIngredientIds)
    {
        var projectedSet = baseIngredientIds
            .Concat(ExtraIngredientIds)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();
        if (!projectedSet.SequenceEqual(ExpectedIngredientIds))
        {
            throw new InvalidOperationException(
                $"recipe {RecipeId} authoritative base plus extras does not match the target ingredient set");
        }
        return new TargetRecipeVariantPlan(
            RecipeId,
            ExpectedIngredientIds,
            baseIngredientIds,
            ExtraIngredientIds,
            Claims,
            _targetRevisions);
    }

    internal static string BuildFingerprint(IEnumerable<string> values)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", values)));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
