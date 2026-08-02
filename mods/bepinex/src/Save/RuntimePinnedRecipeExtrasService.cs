using System.Reflection;
using BepInEx.Logging;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace MystiaStewardCompanion.Save;

/// <summary>
/// Adds the published recommendation's extra ingredients during the game's native recipe-import refresh.
/// </summary>
internal static class RuntimePinnedRecipeExtrasService
{
    private const int MaxSelectedIngredientSlots = 5;
    private const int MaxDebitEntries = 64;
    private const int MaxTrackedAttempts = 16;
    private const int MaxWarningLogs = 4;

    private static readonly object SyncRoot = new();
    private static readonly List<TransactionAttempt> Attempts = new();

    private static ManualLogSource? _log;
    private static IPinnedRecipeExtrasRuntime _runtime = new ReflectionPinnedRecipeExtrasRuntime();
    private static long _attemptCount;
    private static long _successCount;
    private static long _rejectionCount;
    private static long _uncertainCount;
    private static long _blockedReplayCount;
    private static int _warningLogs;
    private static string _lastResult = "not used";

    public static string Status
    {
        get
        {
            lock (SyncRoot)
            {
                return $"attempts={_attemptCount}; successes={_successCount}; rejected={_rejectionCount}; uncertain={_uncertainCount}; blockedReplay={_blockedReplayCount}; tracked={Attempts.Count}; warningLogs={_warningLogs}/{MaxWarningLogs}; last={_lastResult}";
            }
        }
    }

    public static void Attach(ManualLogSource log)
    {
        _log = log;
    }

    /// <summary>
    /// Runs only from the outermost prefix of WorkSceneCookingSelectionPannel.UpdateAllVisual.
    /// </summary>
    public static RuntimePinnedRecipeExtrasApplyResult TryApply(
        object panel,
        RuntimeUiPinningService.PinningTargetSnapshot capturedTarget)
    {
        if (!capturedTarget.Enabled || !capturedTarget.ExtraIngredientFillEnabled)
        {
            return RuntimePinnedRecipeExtrasApplyResult.Disabled;
        }

        var extraIngredientIds = capturedTarget.ExtraIngredientIds;
        if (capturedTarget.Generation <= 0
            || capturedTarget.SessionGeneration <= 0
            || capturedTarget.RecipeId < 0
            || string.IsNullOrEmpty(capturedTarget.TargetRevision)
            || extraIngredientIds.Length == 0
            || extraIngredientIds.Any(id => id < 0))
        {
            return Reject("published extra-ingredient target is invalid");
        }

        try
        {
            if (!_runtime.TryReadPanel(panel, out var panelState, out var readError))
            {
                return Reject(readError);
            }

            if (!panelState.HasImported || panelState.ImportedRecipePointer == 0)
            {
                ClearCompletedAttempts(panelState.PanelPointer);
                return RuntimePinnedRecipeExtrasApplyResult.NotImported;
            }

            if (panelState.RecipeId != capturedTarget.RecipeId)
            {
                return Reject("imported recipe does not match the published recommendation");
            }

            var transaction = new TransactionAttempt(
                panelState.PanelPointer,
                capturedTarget.Generation,
                capturedTarget.SessionGeneration,
                capturedTarget.RecipeId,
                capturedTarget.TargetRevision,
                TransactionState.Applying);
            lock (SyncRoot)
            {
                RemoveAttemptsFromOtherTargetsLocked(transaction);
                if (Attempts.Any(attempt => attempt.HasSameIdentity(transaction)))
                {
                    _blockedReplayCount++;
                    _lastResult = "blocked an already attempted recipe-import transaction";
                    return RuntimePinnedRecipeExtrasApplyResult.BlockedReplay;
                }
            }

            if (!panelState.IsFreeCook && panelState.ExtraCostMultiplier <= 0)
            {
                return Reject("game extra-ingredient cost multiplier is not positive");
            }

            if (!panelState.SelectedIngredientIds.SequenceEqual(panelState.BaseIngredientIds))
            {
                return Reject("selected ingredients are not the exact native recipe import");
            }

            if (panelState.SelectedIngredientIds.Count + extraIngredientIds.Length > MaxSelectedIngredientSlots)
            {
                return Reject("recommended ingredients exceed the game's five-slot limit");
            }

            if (!panelState.IsFreeCook
                && !TryValidateDebitEntryCount(
                    extraIngredientIds.Length,
                    panelState.ExtraCostMultiplier,
                    out var debitError))
            {
                return Reject(debitError);
            }

            if (!panelState.IsFreeCook)
            {
                var requiredQuantities = extraIngredientIds
                    .GroupBy(id => id)
                    .ToDictionary(
                        group => group.Key,
                        group => checked(group.Count() * panelState.ExtraCostMultiplier));

                foreach (var (ingredientId, requiredQuantity) in requiredQuantities)
                {
                    var availableQuantity = _runtime.GetIngredientQuantity(ingredientId);
                    if (availableQuantity < -1)
                    {
                        return Reject($"ingredient {ingredientId} returned invalid quantity {availableQuantity}");
                    }

                    if (availableQuantity >= 0 && availableQuantity < requiredQuantity)
                    {
                        return Reject($"ingredient {ingredientId} inventory is insufficient");
                    }
                }
            }

            if (!IsCurrentTarget(capturedTarget, extraIngredientIds))
            {
                return Reject("published target changed before the native transaction");
            }

            lock (SyncRoot)
            {
                RemoveAttemptsFromOtherTargetsLocked(transaction);
                if (Attempts.Any(attempt => attempt.HasSameIdentity(transaction)))
                {
                    _blockedReplayCount++;
                    _lastResult = "blocked an already attempted recipe-import transaction";
                    return RuntimePinnedRecipeExtrasApplyResult.BlockedReplay;
                }

                if (Attempts.Count >= MaxTrackedAttempts) Attempts.RemoveAt(0);
                Attempts.Add(transaction);
                _attemptCount++;
                _lastResult = "native transaction started";
            }

            try
            {
                var executed = RuntimeUiPinningService.TryExecutePinnedRecipeExtrasTransaction(
                    capturedTarget,
                    extraIngredientIds,
                    () =>
                    {
                        if (!panelState.IsFreeCook)
                        {
                            _runtime.DebitIngredients(ExpandForDebit(extraIngredientIds, panelState.ExtraCostMultiplier));
                        }

                        _runtime.AddSelectedIngredients(panelState.SelectedIngredientList, extraIngredientIds);
                    });
                if (!executed)
                {
                    lock (SyncRoot) Attempts.Remove(transaction);
                    return Reject("published target changed at the native transaction boundary");
                }
            }
            catch (Exception ex)
            {
                MarkUncertain(transaction, $"native transaction result is uncertain: {DescribeException(ex)}");
                return RuntimePinnedRecipeExtrasApplyResult.Uncertain;
            }

            lock (SyncRoot)
            {
                transaction.State = TransactionState.Applied;
                _successCount++;
                _lastResult = $"added extras={string.Join(",", extraIngredientIds)} to recipe {capturedTarget.RecipeId}";
            }

            return RuntimePinnedRecipeExtrasApplyResult.Applied;
        }
        catch (Exception ex)
        {
            return Reject($"pre-transaction inspection failed: {DescribeException(ex)}", warn: true);
        }
    }

    /// <summary>
    /// Commits a successful import refresh, or keeps it non-retriable when the native refresh failed afterward.
    /// </summary>
    public static void OnRefreshFinalized(
        object panel,
        RuntimeUiPinningService.PinningTargetSnapshot capturedTarget,
        Exception? exception)
    {
        nint panelPointer;
        try
        {
            panelPointer = _runtime.GetPanelPointer(panel);
        }
        catch
        {
            return;
        }

        TransactionAttempt? attempt;
        lock (SyncRoot)
        {
            attempt = Attempts.LastOrDefault(candidate =>
                candidate.PanelPointer == panelPointer
                && candidate.TargetGeneration == capturedTarget.Generation
                && candidate.SessionGeneration == capturedTarget.SessionGeneration
                && candidate.RecipeId == capturedTarget.RecipeId
                && string.Equals(candidate.TargetRevision, capturedTarget.TargetRevision, StringComparison.Ordinal));
            if (attempt == null) return;

            if (exception == null && attempt.State == TransactionState.Applied)
            {
                Attempts.Remove(attempt);
                _lastResult = "native recipe-import refresh completed";
                return;
            }
        }

        if (exception != null)
        {
            MarkUncertain(
                attempt,
                $"native recipe-import refresh failed after modification: {DescribeException(exception)}");
        }
    }

    public static void Abandon(string reason)
    {
        lock (SyncRoot)
        {
            Attempts.Clear();
            _lastResult = string.IsNullOrWhiteSpace(reason) ? "runtime abandoned" : reason.Trim();
        }
    }

    internal static void UseRuntimeForTests(IPinnedRecipeExtrasRuntime runtime)
    {
        lock (SyncRoot)
        {
            _runtime = runtime;
            Attempts.Clear();
            _attemptCount = 0;
            _successCount = 0;
            _rejectionCount = 0;
            _uncertainCount = 0;
            _blockedReplayCount = 0;
            _warningLogs = 0;
            _lastResult = "test runtime installed";
        }
    }

    private static bool IsCurrentTarget(
        RuntimeUiPinningService.PinningTargetSnapshot capturedTarget,
        IReadOnlyList<int> extraIngredientIds)
    {
        var currentTarget = RuntimeUiPinningService.ReadPinningTarget();
        return ReferenceEquals(currentTarget, capturedTarget)
            && currentTarget.Enabled
            && currentTarget.ExtraIngredientFillEnabled
            && currentTarget.Generation == capturedTarget.Generation
            && currentTarget.SessionGeneration == capturedTarget.SessionGeneration
            && currentTarget.RecipeId == capturedTarget.RecipeId
            && string.Equals(currentTarget.TargetRevision, capturedTarget.TargetRevision, StringComparison.Ordinal)
            && currentTarget.ExtraIngredientIds.SequenceEqual(extraIngredientIds);
    }

    private static int[] ExpandForDebit(IReadOnlyList<int> ids, int multiplier)
    {
        var expanded = new int[checked(ids.Count * multiplier)];
        var index = 0;
        foreach (var id in ids)
        {
            for (var repeat = 0; repeat < multiplier; repeat++) expanded[index++] = id;
        }

        return expanded;
    }

    private static bool TryValidateDebitEntryCount(int extraCount, int multiplier, out string error)
    {
        try
        {
            if (checked(extraCount * multiplier) <= MaxDebitEntries)
            {
                error = "";
                return true;
            }

            error = "extra-ingredient debit count exceeds the transaction limit";
            return false;
        }
        catch (OverflowException)
        {
            error = "extra-ingredient debit count overflowed";
            return false;
        }
    }

    private static RuntimePinnedRecipeExtrasApplyResult Reject(string reason, bool warn = false)
    {
        ManualLogSource? log = null;
        lock (SyncRoot)
        {
            _rejectionCount++;
            _lastResult = reason;
            if (warn && _warningLogs < MaxWarningLogs)
            {
                _warningLogs++;
                log = _log;
            }
        }

        TryLogWarning(log, $"Pinned recipe extras rejected: {reason}.");
        return RuntimePinnedRecipeExtrasApplyResult.Rejected;
    }

    private static void MarkUncertain(TransactionAttempt attempt, string reason)
    {
        ManualLogSource? log = null;
        lock (SyncRoot)
        {
            if (attempt.State != TransactionState.Uncertain)
            {
                attempt.State = TransactionState.Uncertain;
                _uncertainCount++;
            }

            _lastResult = reason;
            if (_warningLogs < MaxWarningLogs)
            {
                _warningLogs++;
                log = _log;
            }
        }

        TryLogWarning(log, $"Pinned recipe extras will not retry this import: {reason}.");
    }

    private static void ClearCompletedAttempts(nint panelPointer)
    {
        if (panelPointer == 0) return;
        lock (SyncRoot)
        {
            Attempts.RemoveAll(attempt =>
                attempt.PanelPointer == panelPointer
                && attempt.State == TransactionState.Applied);
        }
    }

    private static void RemoveAttemptsFromOtherTargetsLocked(TransactionAttempt current)
    {
        Attempts.RemoveAll(attempt =>
            attempt.PanelPointer == current.PanelPointer
            && (attempt.TargetGeneration != current.TargetGeneration
                || attempt.SessionGeneration != current.SessionGeneration
                || !string.Equals(attempt.TargetRevision, current.TargetRevision, StringComparison.Ordinal)));
    }

    private static string DescribeException(Exception exception)
    {
        return exception.GetBaseException().Message;
    }

    private static void TryLogWarning(ManualLogSource? log, string message)
    {
        if (log == null) return;
        try
        {
            log.LogWarning(message);
        }
        catch
        {
            // Logging must never escape into the game's recipe-import callback.
        }
    }

    private sealed class TransactionAttempt
    {
        public TransactionAttempt(
            nint panelPointer,
            long targetGeneration,
            long sessionGeneration,
            int recipeId,
            string targetRevision,
            TransactionState state)
        {
            PanelPointer = panelPointer;
            TargetGeneration = targetGeneration;
            SessionGeneration = sessionGeneration;
            RecipeId = recipeId;
            TargetRevision = targetRevision;
            State = state;
        }

        public nint PanelPointer { get; }
        public long TargetGeneration { get; }
        public long SessionGeneration { get; }
        public int RecipeId { get; }
        public string TargetRevision { get; }
        public TransactionState State { get; set; }

        public bool HasSameIdentity(TransactionAttempt other)
        {
            return PanelPointer == other.PanelPointer
                && TargetGeneration == other.TargetGeneration
                && SessionGeneration == other.SessionGeneration
                && RecipeId == other.RecipeId
                && string.Equals(TargetRevision, other.TargetRevision, StringComparison.Ordinal);
        }
    }

    private enum TransactionState
    {
        Applying,
        Applied,
        Uncertain,
    }
}

internal enum RuntimePinnedRecipeExtrasApplyResult
{
    Disabled,
    NotImported,
    Rejected,
    Applied,
    BlockedReplay,
    Uncertain,
}

internal interface IPinnedRecipeExtrasRuntime
{
    bool TryReadPanel(object panel, out PinnedRecipeExtrasPanelState state, out string error);
    nint GetPanelPointer(object panel);
    int GetIngredientQuantity(int ingredientId);
    void DebitIngredients(IReadOnlyList<int> ingredientIds);
    void AddSelectedIngredients(object selectedIngredientList, IReadOnlyList<int> ingredientIds);
}

internal sealed class PinnedRecipeExtrasPanelState
{
    public nint PanelPointer { get; init; }
    public bool HasImported { get; init; }
    public nint ImportedRecipePointer { get; init; }
    public int RecipeId { get; init; } = -1;
    public IReadOnlyList<int> BaseIngredientIds { get; init; } = Array.Empty<int>();
    public IReadOnlyList<int> SelectedIngredientIds { get; init; } = Array.Empty<int>();
    public object SelectedIngredientList { get; init; } = null!;
    public int ExtraCostMultiplier { get; init; }
    public bool IsFreeCook { get; init; }
}

internal sealed class ReflectionPinnedRecipeExtrasRuntime : IPinnedRecipeExtrasRuntime
{
    private const int NativeSelectedIngredientSlotLimit = 5;
    private const string PanelTypeName = "NightScene.UI.CookingUtility.WorkSceneCookingSelectionPannel";
    private const string RecipeTypeName = "GameData.Core.Collections.Recipe";
    private const string RecipeBaseTypeName = "GameData.Core.NonTradableObjectBase";
    private const string RuntimeStorageTypeName = "GameData.RunTime.Common.RunTimeStorage";

    private readonly object _bindingRoot = new();
    private ExactBindings? _bindings;

    public bool TryReadPanel(object panel, out PinnedRecipeExtrasPanelState state, out string error)
    {
        state = new PinnedRecipeExtrasPanelState();
        error = "";

        var panelPointer = GetPanelPointer(panel);
        if (panelPointer == 0)
        {
            error = "cooking panel has no native identity";
            return false;
        }

        var bindings = GetBindings(panel.GetType());
        var hasImported = InvokeRequired<bool>(bindings.GetHasImported, panel);
        if (!hasImported)
        {
            state = new PinnedRecipeExtrasPanelState
            {
                PanelPointer = panelPointer,
                HasImported = false,
            };
            return true;
        }

        var recipe = Invoke(bindings.GetImportedRecipe, panel);
        if (recipe == null || recipe is not Il2CppObjectBase nativeRecipe || nativeRecipe.Pointer == IntPtr.Zero)
        {
            error = "imported recipe has no native identity";
            return false;
        }

        var selectedList = Invoke(bindings.GetSelectedIngredients, panel)
            ?? throw new InvalidOperationException("Selected ingredient list is null.");
        if (selectedList.GetType() != typeof(Il2CppSystem.Collections.Generic.List<int>))
        {
            error = "selected ingredient list has an unexpected runtime type";
            return false;
        }

        var ingredients = Invoke(bindings.GetRecipeIngredients, recipe) as Il2CppStructArray<int>
            ?? throw new InvalidOperationException("Recipe ingredients are not an exact Il2CppStructArray<int>.");
        var baseIds = new int[ingredients.Length];
        for (var i = 0; i < ingredients.Length; i++) baseIds[i] = ingredients[i];

        var count = InvokeRequired<int>(bindings.GetListCount, selectedList);
        if (count < 0 || count > NativeSelectedIngredientSlotLimit)
        {
            error = "selected ingredient count is outside the native slot range";
            return false;
        }

        var selectedIds = new int[count];
        for (var i = 0; i < count; i++)
        {
            selectedIds[i] = InvokeRequired<int>(bindings.GetListItem, selectedList, i);
        }

        state = new PinnedRecipeExtrasPanelState
        {
            PanelPointer = panelPointer,
            HasImported = true,
            ImportedRecipePointer = nativeRecipe.Pointer,
            RecipeId = InvokeRequired<int>(bindings.GetRecipeId, recipe),
            BaseIngredientIds = baseIds,
            SelectedIngredientIds = selectedIds,
            SelectedIngredientList = selectedList,
            ExtraCostMultiplier = InvokeRequired<int>(bindings.GetExtraCostIngredient, panel),
            IsFreeCook = InvokeRequired<bool>(bindings.GetIsFreeCook, panel),
        };
        return true;
    }

    public nint GetPanelPointer(object panel)
    {
        return panel is Il2CppObjectBase nativePanel ? nativePanel.Pointer : 0;
    }

    public int GetIngredientQuantity(int ingredientId)
    {
        var bindings = GetBindings();
        return InvokeRequired<int>(bindings.GetIngredientQuantity, null, ingredientId);
    }

    public void DebitIngredients(IReadOnlyList<int> ingredientIds)
    {
        var bindings = GetBindings();
        var ids = new Il2CppStructArray<int>(ingredientIds.Count);
        for (var i = 0; i < ingredientIds.Count; i++) ids[i] = ingredientIds[i];
        var enumerable = ids.Cast<Il2CppSystem.Collections.Generic.IEnumerable<int>>();
        Invoke(bindings.DebitIngredients, null, enumerable, false);
    }

    public void AddSelectedIngredients(object selectedIngredientList, IReadOnlyList<int> ingredientIds)
    {
        var bindings = GetBindings();
        foreach (var ingredientId in ingredientIds)
        {
            Invoke(bindings.AddListItem, selectedIngredientList, ingredientId);
        }
    }

    private ExactBindings GetBindings(Type? panelType = null)
    {
        lock (_bindingRoot)
        {
            if (_bindings != null)
            {
                if (panelType != null && panelType != _bindings.PanelType)
                {
                    throw new InvalidOperationException($"Unexpected cooking panel type {panelType.FullName}.");
                }

                return _bindings;
            }

            var exactPanelType = panelType ?? FindType(PanelTypeName)
                ?? throw new TypeLoadException(PanelTypeName);
            if (exactPanelType.FullName != PanelTypeName)
            {
                throw new InvalidOperationException($"Unexpected cooking panel type {exactPanelType.FullName}.");
            }

            var recipeType = FindType(RecipeTypeName) ?? throw new TypeLoadException(RecipeTypeName);
            var recipeBaseType = recipeType.BaseType;
            if (recipeBaseType?.FullName != RecipeBaseTypeName)
            {
                throw new InvalidOperationException("Recipe does not inherit the exact NonTradableObjectBase type.");
            }

            var storageType = FindType(RuntimeStorageTypeName) ?? throw new TypeLoadException(RuntimeStorageTypeName);
            var selectedListType = typeof(Il2CppSystem.Collections.Generic.List<int>);
            var intArrayType = typeof(Il2CppStructArray<int>);
            var intEnumerableType = typeof(Il2CppSystem.Collections.Generic.IEnumerable<int>);

            _bindings = new ExactBindings(
                exactPanelType,
                RequireExactMethod(exactPanelType, "get_selectedIngredients", false, selectedListType),
                RequireExactMethod(exactPanelType, "get_ExtraCostIng", false, typeof(int)),
                RequireExactMethod(exactPanelType, "get_hasImported", false, typeof(bool)),
                RequireExactMethod(exactPanelType, "get_importedRecipe", false, recipeType),
                RequireExactMethod(exactPanelType, "get_IsFreeCook", false, typeof(bool)),
                RequireExactMethod(recipeType, "get_Ingredients", false, intArrayType),
                RequireExactMethod(recipeBaseType, "get_Id", false, typeof(int)),
                RequireExactMethod(selectedListType, "get_Count", false, typeof(int)),
                RequireExactMethod(selectedListType, "get_Item", false, typeof(int), typeof(int)),
                RequireExactMethod(selectedListType, "Add", false, typeof(void), typeof(int)),
                RequireExactMethod(storageType, "GetIngredientCountById", true, typeof(int), typeof(int)),
                RequireExactMethod(storageType, "IngredientOutRange", true, typeof(void), intEnumerableType, typeof(bool)));
            return _bindings;
        }
    }

    private static MethodInfo RequireExactMethod(
        Type type,
        string name,
        bool isStatic,
        Type returnType,
        params Type[] parameterTypes)
    {
        var flags = BindingFlags.Public
            | BindingFlags.NonPublic
            | (isStatic ? BindingFlags.Static : BindingFlags.Instance)
            | BindingFlags.DeclaredOnly;
        var matches = type.GetMethods(flags)
            .Where(method =>
                method.Name == name
                && method.IsStatic == isStatic
                && method.ReturnType == returnType
                && method.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(parameterTypes))
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
                // An unrelated generated interop type can fail during lookup.
            }
        }

        return null;
    }

    private static T InvokeRequired<T>(MethodInfo method, object? instance, params object?[] args)
    {
        var value = Invoke(method, instance, args);
        return value is T typed
            ? typed
            : throw new InvalidOperationException($"{method.DeclaringType?.FullName}.{method.Name} returned an unexpected value.");
    }

    private static object? Invoke(MethodInfo method, object? instance, params object?[] args)
    {
        try
        {
            return method.Invoke(instance, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            throw ex.InnerException;
        }
    }

    private sealed record ExactBindings(
        Type PanelType,
        MethodInfo GetSelectedIngredients,
        MethodInfo GetExtraCostIngredient,
        MethodInfo GetHasImported,
        MethodInfo GetImportedRecipe,
        MethodInfo GetIsFreeCook,
        MethodInfo GetRecipeIngredients,
        MethodInfo GetRecipeId,
        MethodInfo GetListCount,
        MethodInfo GetListItem,
        MethodInfo AddListItem,
        MethodInfo GetIngredientQuantity,
        MethodInfo DebitIngredients);
}
