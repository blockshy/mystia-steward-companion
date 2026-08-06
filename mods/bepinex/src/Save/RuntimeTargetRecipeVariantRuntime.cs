using System.Reflection;

using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace MystiaStewardCompanion.Save;

/// <summary>
/// Exact BepInEx 783 reflection bindings for the cooking-panel recipe variant transaction.
/// This runtime caches metadata only; native wrappers never leave the active hook stack.
/// </summary>
internal sealed class ReflectionTargetRecipeVariantRuntime : ITargetRecipeVariantRuntime
{
    private const int MaximumIngredientSlots = 5;
    private const string PanelTypeName =
        "NightScene.UI.CookingUtility.WorkSceneCookingSelectionPannel";
    private const string MatchedComboNestedTypeName = "MatchedCookCombo";
    private const string OutputClosureNestedTypeName = "__c__DisplayClass79_0";
    private const string OutputClosureMethodName = "Method_Internal_Void_PDM_0";
    private const string RecipeTypeName = "GameData.Core.Collections.Recipe";
    private const string IngredientTypeName = "GameData.Core.Collections.Ingredient";
    private const string SellableTypeName = "GameData.Core.Collections.Sellable";
    private const string RecipeBaseTypeName = "GameData.Core.NonTradableObjectBase";
    private const string IngredientBaseTypeName = "GameData.Core.TradableObjectBase";
    private const string RuntimeStorageTypeName = "GameData.RunTime.Common.RunTimeStorage";
    private const string UiButtonBaseTypeName =
        "DEYU.AdpUISystem.LogicalCollection.UIButtonBase";
    private const string SelectableTypeName = "UnityEngine.UI.Selectable";
    private const string InteropUtilsTypeName = "Il2CppInterop.Common.Il2CppInteropUtils";

    private readonly object _bindingRoot = new();
    private ExactBindings? _bindings;
    private SelectedVisualBindings? _selectedVisualBindings;

    public nint GetNativePointer(object instance)
    {
        return instance is Il2CppObjectBase native ? native.Pointer : 0;
    }

    public bool TryWrapPanel(nint panelPointer, out object panel, out string error)
    {
        panel = null!;
        error = "";
        if (panelPointer == 0)
        {
            error = "cooking panel pointer is zero";
            return false;
        }

        var bindings = GetBindings();
        if (!HasExactNativeClass(panelPointer, bindings.PanelType))
        {
            error = "cooking panel pointer is not the exact native cooking selection panel class";
            return false;
        }
        panel = InvokeConstructor(bindings.PanelPointerConstructor, (IntPtr)panelPointer)
            ?? throw new InvalidOperationException("Cooking panel pointer constructor returned null.");
        if (panel.GetType() != bindings.PanelType || GetNativePointer(panel) != panelPointer)
        {
            panel = null!;
            error = "fresh cooking panel wrapper did not preserve the exact native identity";
            return false;
        }

        return true;
    }

    public bool TryWrapMatchedCombo(nint comboPointer, out object combo, out string error)
    {
        combo = null!;
        error = "";
        if (comboPointer == 0)
        {
            error = "matched combo pointer is zero";
            return false;
        }

        var bindings = GetBindings();
        if (!HasExactNativeClass(comboPointer, bindings.MatchedComboType))
        {
            error = "matched combo pointer is not the exact native MatchedCookCombo class";
            return false;
        }
        combo = InvokeConstructor(bindings.MatchedComboPointerConstructor, (IntPtr)comboPointer)
            ?? throw new InvalidOperationException("Matched combo pointer constructor returned null.");
        if (combo.GetType() != bindings.MatchedComboType || GetNativePointer(combo) != comboPointer)
        {
            combo = null!;
            error = "fresh matched combo wrapper did not preserve the exact native identity";
            return false;
        }

        return true;
    }

    public bool TryReadRecipeList(
        object panel,
        int maximumCount,
        out object recipeList,
        out IReadOnlyList<TargetRecipeDescriptor> recipes,
        out string error)
    {
        recipeList = null!;
        recipes = Array.Empty<TargetRecipeDescriptor>();
        error = "";
        if (panel.GetType().FullName != PanelTypeName)
        {
            error = "panel wrapper is not the exact cooking selection panel type";
            return false;
        }
        var bindings = GetBindings(panel.GetType());
        if (panel.GetType() != bindings.PanelType || GetNativePointer(panel) == 0)
        {
            error = "panel wrapper is not the exact live cooking selection panel type";
            return false;
        }

        recipeList = Invoke(bindings.GetRecipeInstances, panel)
            ?? throw new InvalidOperationException("m_RecipeInstances is null.");
        if (recipeList.GetType() != bindings.RecipeListType || GetNativePointer(recipeList) == 0)
        {
            error = "m_RecipeInstances is not the exact live List<Recipe> type";
            return false;
        }

        var count = InvokeRequired<int>(bindings.GetRecipeListCount, recipeList);
        if (maximumCount < 0 || count < 0 || count > maximumCount)
        {
            error = $"recipe row count {count} is outside 0..{maximumCount}";
            return false;
        }

        var result = new TargetRecipeDescriptor[count];
        var pointers = new HashSet<nint>();
        for (var index = 0; index < count; index += 1)
        {
            var recipe = Invoke(bindings.GetRecipeListItem, recipeList, index)
                ?? throw new InvalidOperationException($"Recipe row {index} is null.");
            if (!TryReadRecipeSnapshot(recipe, out var snapshot, out error)) return false;
            if (!pointers.Add(snapshot.RecipePointer))
            {
                error = $"recipe pointer {FormatPointer(snapshot.RecipePointer)} occurs more than once";
                return false;
            }

            result[index] = new TargetRecipeDescriptor(
                index,
                recipe,
                snapshot.RecipePointer,
                snapshot.RecipeId,
                snapshot.IngredientIds,
                snapshot.CookCount);
        }

        recipes = result;
        return true;
    }

    public bool TryReadRecipeSnapshot(
        object recipe,
        out TargetRecipeSnapshot snapshot,
        out string error)
    {
        snapshot = null!;
        error = "";
        var bindings = GetBindings();
        if (recipe.GetType() != bindings.RecipeType)
        {
            error = "recipe wrapper is not the exact Recipe type";
            return false;
        }

        var recipePointer = GetNativePointer(recipe);
        if (recipePointer == 0)
        {
            error = "recipe has no native identity";
            return false;
        }

        var recipeId = InvokeRequired<int>(bindings.GetRecipeId, recipe);
        if (recipeId < 0)
        {
            error = "recipe id is negative";
            return false;
        }

        if (Invoke(bindings.GetRecipeIngredients, recipe) is not Il2CppStructArray<int> ingredients)
        {
            error = "recipe ingredients are not the exact Il2CppStructArray<int> type";
            return false;
        }
        if (ingredients.Length < 0 || ingredients.Length > MaximumIngredientSlots)
        {
            error = $"recipe ingredient count {ingredients.Length} is outside 0..{MaximumIngredientSlots}";
            return false;
        }

        var ingredientIds = CopyIntArray(ingredients);
        var cookCount = InvokeRequired<int>(bindings.GetCookCount, recipe);
        if (cookCount < -1)
        {
            error = $"recipe CookCount {cookCount} is below the native infinite sentinel";
            return false;
        }

        snapshot = new TargetRecipeSnapshot(
            recipe,
            recipePointer,
            recipeId,
            ingredientIds,
            cookCount);
        return true;
    }

    public bool TryCreateSyntheticRecipe(
        object authoritativeRecipe,
        IReadOnlyList<int> fullIngredientIds,
        int cookCount,
        out object syntheticRecipe,
        out nint syntheticPointer,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(fullIngredientIds);
        syntheticRecipe = null!;
        syntheticPointer = 0;
        error = "";
        var bindings = GetBindings();
        if (authoritativeRecipe.GetType() != bindings.RecipeType
            || GetNativePointer(authoritativeRecipe) == 0)
        {
            error = "authoritative recipe wrapper is not the exact live Recipe type";
            return false;
        }
        if (!TryValidateIngredientIds(fullIngredientIds, out error)) return false;
        if (cookCount < -1)
        {
            error = "synthetic recipe CookCount is below the native infinite sentinel";
            return false;
        }

        var ingredients = CreateIntArray(fullIngredientIds);
        syntheticRecipe = InvokeConstructor(
            bindings.RecipeConstructor,
            InvokeRequired<int>(bindings.GetRecipeId, authoritativeRecipe),
            InvokeRequired<int>(bindings.GetFoodId, authoritativeRecipe),
            Invoke(bindings.GetCookerType, authoritativeRecipe),
            InvokeRequired<float>(bindings.GetBaseCookTime, authoritativeRecipe),
            ingredients)
            ?? throw new InvalidOperationException("Recipe constructor returned null.");
        Invoke(bindings.SetCookCount, syntheticRecipe, cookCount);

        syntheticPointer = GetNativePointer(syntheticRecipe);
        if (syntheticPointer == 0
            || syntheticPointer == GetNativePointer(authoritativeRecipe)
            || InvokeRequired<bool>(bindings.RecipeEquals, syntheticRecipe, authoritativeRecipe))
        {
            error = "synthetic Recipe does not have an independent native identity";
            return false;
        }
        if (!TryReadRecipeSnapshot(syntheticRecipe, out var snapshot, out error)
            || snapshot.RecipePointer != syntheticPointer
            || snapshot.RecipeId != InvokeRequired<int>(bindings.GetRecipeId, authoritativeRecipe)
            || snapshot.CookCount != cookCount
            || !snapshot.IngredientIds.SequenceEqual(fullIngredientIds))
        {
            if (error.Length == 0) error = "synthetic Recipe fields do not match the requested variant";
            return false;
        }

        return true;
    }

    public bool TrySetSyntheticCookCount(
        object syntheticRecipe,
        int cookCount,
        out string error)
    {
        error = "";
        var bindings = GetBindings();
        if (syntheticRecipe.GetType() != bindings.RecipeType
            || GetNativePointer(syntheticRecipe) == 0)
        {
            error = "synthetic recipe wrapper is not the exact live Recipe type";
            return false;
        }
        if (cookCount < -1)
        {
            error = "synthetic recipe CookCount is below the native infinite sentinel";
            return false;
        }

        Invoke(bindings.SetCookCount, syntheticRecipe, cookCount);
        if (InvokeRequired<int>(bindings.GetCookCount, syntheticRecipe) != cookCount)
        {
            error = "synthetic recipe CookCount did not retain the exact requested value";
            return false;
        }
        return true;
    }

    public void InsertRecipe(object recipeList, int index, object recipe)
    {
        var bindings = GetBindings();
        if (recipeList.GetType() != bindings.RecipeListType
            || GetNativePointer(recipeList) == 0
            || recipe.GetType() != bindings.RecipeType
            || GetNativePointer(recipe) == 0)
        {
            throw new InvalidOperationException("Recipe Insert arguments do not use exact live runtime types.");
        }

        Invoke(bindings.InsertRecipeListItem, recipeList, index, recipe);
    }

    public bool TryCleanSubmitCallback(object button, out string error)
    {
        error = "";
        var bindings = GetBindings();
        if (!bindings.UiButtonBaseType.IsInstanceOfType(button) || GetNativePointer(button) == 0)
        {
            error = "button is not a live UIButtonBase";
            return false;
        }

        Invoke(bindings.CleanOnSubmitCallback, button);
        var callback = Invoke(bindings.GetOnSubmitCallback, button);
        if (callback != null && GetNativePointer(callback) != 0)
        {
            error = "button submit callback remained live after exact cleanup";
            return false;
        }
        return true;
    }

    public bool TryDisableButton(object button, out string error)
    {
        error = "";
        var bindings = GetBindings();
        if (!bindings.UiButtonBaseType.IsInstanceOfType(button)
            || !bindings.SelectableType.IsInstanceOfType(button)
            || GetNativePointer(button) == 0)
        {
            error = "button is not a live UIButtonBase/Selectable";
            return false;
        }

        Invoke(bindings.SetInteractable, button, false);
        return true;
    }

    public bool TryReadPanelCookingState(
        object panel,
        out TargetRecipePanelCookingState state,
        out string error)
    {
        state = null!;
        error = "";
        if (panel.GetType().FullName != PanelTypeName)
        {
            error = "panel wrapper is not the exact cooking selection panel type";
            return false;
        }
        var bindings = GetBindings(panel.GetType());
        var panelPointer = GetNativePointer(panel);
        if (panel.GetType() != bindings.PanelType || panelPointer == 0)
        {
            error = "panel wrapper is not the exact live cooking selection panel type";
            return false;
        }

        if (!TryReadPanelSelectionState(panel, out var selection, out error)
            || selection.PanelPointer != panelPointer)
        {
            if (error.Length == 0) error = "panel selection identity changed during cooking-state read";
            return false;
        }
        var selectedList = selection.SelectedIngredientList;
        var selectedIds = selection.SelectedIngredientIds;
        var extraCostIngredient = selection.ExtraCostIngredient;
        var isFreeCook = selection.IsFreeCook;
        var hasImportedRecipe = InvokeRequired<bool>(bindings.GetHasImported, panel);
        if (!hasImportedRecipe)
        {
            error = "panel has no synchronous imported Recipe receipt";
            return false;
        }

        var importedRecipe = Invoke(bindings.GetImportedRecipe, panel);
        if (importedRecipe == null
            || !TryReadRecipeSnapshot(importedRecipe, out var importedSnapshot, out error))
        {
            if (error.Length == 0) error = "imported recipe is null";
            return false;
        }

        state = new TargetRecipePanelCookingState(
            panelPointer,
            importedSnapshot.RecipePointer,
            importedSnapshot.RecipeId,
            importedSnapshot.IngredientIds,
            selectedList,
            selectedIds,
            extraCostIngredient,
            isFreeCook);
        return true;
    }

    public bool TryReadPanelSelectionState(
        object panel,
        out TargetRecipePanelSelectionState state,
        out string error)
    {
        state = null!;
        error = "";
        if (panel.GetType().FullName != PanelTypeName)
        {
            error = "panel wrapper is not the exact cooking selection panel type";
            return false;
        }
        var bindings = GetBindings(panel.GetType());
        var panelPointer = GetNativePointer(panel);
        if (panel.GetType() != bindings.PanelType || panelPointer == 0)
        {
            error = "panel wrapper is not the exact live cooking selection panel type";
            return false;
        }

        var selectedList = Invoke(bindings.GetSelectedIngredients, panel)
            ?? throw new InvalidOperationException("selectedIngredients is null.");
        if (selectedList.GetType() != bindings.SelectedIngredientListType
            || GetNativePointer(selectedList) == 0)
        {
            error = "selectedIngredients is not the exact live List<int> type";
            return false;
        }
        if (!TryReadSelectedIngredientIds(selectedList, bindings, out var selectedIds, out error))
        {
            return false;
        }

        state = new TargetRecipePanelSelectionState(
            panelPointer,
            selectedList,
            selectedIds,
            InvokeRequired<int>(bindings.GetExtraCostIngredient, panel),
            InvokeRequired<bool>(bindings.GetIsFreeCook, panel));
        return true;
    }

    public bool TryReadSelectedVisualState(
        object panel,
        out TargetRecipeSelectedVisualState state,
        out string error)
    {
        state = null!;
        error = "";
        if (panel.GetType().FullName != PanelTypeName)
        {
            error = "panel wrapper is not the exact cooking selection panel type";
            return false;
        }
        SelectedVisualBindings bindings;
        try
        {
            bindings = GetSelectedVisualBindings(panel.GetType());
        }
        catch (Exception ex)
        {
            error = $"selected visual binding failed: {ex.GetBaseException().Message}";
            return false;
        }
        if (panel.GetType() != bindings.PanelType || GetNativePointer(panel) == 0)
        {
            error = "panel wrapper is not the exact live cooking selection panel type";
            return false;
        }

        var selectedInstances = Invoke(bindings.GetSelectedInstances, panel)
            ?? throw new InvalidOperationException("m_SelectedInstances is null.");
        var listPointer = GetNativePointer(selectedInstances);
        if (selectedInstances.GetType() != bindings.SelectedInstanceListType
            || listPointer == 0)
        {
            error = "m_SelectedInstances is not the exact live List<Ingredient> type";
            return false;
        }
        var count = InvokeRequired<int>(
            bindings.GetSelectedInstanceCount,
            selectedInstances);
        if (count < 0 || count > MaximumIngredientSlots)
        {
            error = $"selected visual ingredient count {count} is outside 0..{MaximumIngredientSlots}";
            return false;
        }

        var ingredientIds = new int[count];
        for (var index = 0; index < count; index += 1)
        {
            var ingredient = Invoke(
                bindings.GetSelectedInstance,
                selectedInstances,
                index);
            if (ingredient == null
                || ingredient.GetType() != bindings.IngredientType
                || GetNativePointer(ingredient) == 0)
            {
                error = $"selected visual ingredient {index} is not an exact live Ingredient";
                return false;
            }
            var ingredientId = InvokeRequired<int>(bindings.GetIngredientId, ingredient);
            if (ingredientId < 0)
            {
                error = $"selected visual ingredient {index} has a negative id";
                return false;
            }
            ingredientIds[index] = ingredientId;
        }

        state = new TargetRecipeSelectedVisualState(listPointer, ingredientIds);
        return true;
    }

    public int GetIngredientQuantity(int ingredientId)
    {
        var bindings = GetBindings();
        return InvokeRequired<int>(bindings.GetIngredientQuantity, null, ingredientId);
    }

    public void DebitIngredients(IReadOnlyList<int> expandedIngredientIds)
    {
        ArgumentNullException.ThrowIfNull(expandedIngredientIds);
        var bindings = GetBindings();
        var ids = CreateIntArray(expandedIngredientIds, enforceSlotLimit: false);
        var enumerable = ids.Cast<Il2CppSystem.Collections.Generic.IEnumerable<int>>();
        Invoke(bindings.DebitIngredients, null, enumerable, false);
    }

    public void AddSelectedIngredients(
        object selectedIngredientList,
        IReadOnlyList<int> ingredientIds)
    {
        ArgumentNullException.ThrowIfNull(selectedIngredientList);
        ArgumentNullException.ThrowIfNull(ingredientIds);
        var bindings = GetBindings();
        if (selectedIngredientList.GetType() != bindings.SelectedIngredientListType
            || GetNativePointer(selectedIngredientList) == 0)
        {
            throw new InvalidOperationException(
                "selected ingredient AddRange target is not the exact live List<int> type.");
        }

        var ids = CreateIntArray(ingredientIds);
        var enumerable = ids.Cast<Il2CppSystem.Collections.Generic.IEnumerable<int>>();
        Invoke(bindings.AddSelectedIngredients, selectedIngredientList, enumerable);
    }

    public bool TryReadMatchedCombo(
        object matchedCombo,
        out TargetRecipeMatchedComboSnapshot snapshot,
        out string error)
    {
        snapshot = null!;
        error = "";
        var bindings = GetBindings();
        if (matchedCombo.GetType() != bindings.MatchedComboType
            || GetNativePointer(matchedCombo) == 0)
        {
            error = "matched combo wrapper is not the exact live MatchedCookCombo type";
            return false;
        }

        var recipe = Invoke(bindings.GetMatchedRecipe, matchedCombo);
        if (recipe == null || !TryReadRecipeSnapshot(recipe, out var recipeSnapshot, out error))
        {
            if (error.Length == 0) error = "matched combo recipe is null";
            return false;
        }
        if (Invoke(bindings.GetMatchedModifiers, matchedCombo) is not Il2CppStructArray<int> modifiers)
        {
            error = "matched combo modifiers are not the exact Il2CppStructArray<int> type";
            return false;
        }
        if (modifiers.Length < 0 || modifiers.Length > MaximumIngredientSlots)
        {
            error = $"matched combo modifier count {modifiers.Length} is outside 0..{MaximumIngredientSlots}";
            return false;
        }

        snapshot = new TargetRecipeMatchedComboSnapshot(
            recipeSnapshot.RecipePointer,
            recipeSnapshot.RecipeId,
            CopyIntArray(modifiers));
        return true;
    }

    public bool TryReadExactOutputSubmitClosure(
        object button,
        out TargetRecipeOutputClosureBindingSnapshot snapshot,
        out string error)
    {
        snapshot = default;
        error = "";
        var bindings = GetBindings();
        var buttonPointer = GetNativePointer(button);
        if (!bindings.UiButtonBaseType.IsInstanceOfType(button)
            || buttonPointer == 0)
        {
            error = "output button is not an exact live UIButtonBase";
            return false;
        }

        var callback = Invoke(bindings.GetOnSubmitCallback, button);
        if (callback == null
            || callback.GetType() != bindings.ActionType
            || GetNativePointer(callback) == 0
            || !HasExactNativeClass(GetNativePointer(callback), bindings.ActionType))
        {
            error = "output button has no exact live Action submit callback";
            return false;
        }

        var actualMethodPointer = InvokeRequired<IntPtr>(
            bindings.GetDelegateMethodPointer,
            callback);
        var pointerField = Invoke(
            bindings.GetGeneratedMethodPointerField,
            null,
            bindings.OutputClosureMethod) as FieldInfo;
        if (pointerField == null
            || !pointerField.IsStatic
            || pointerField.FieldType != typeof(IntPtr)
            || pointerField.DeclaringType != bindings.OutputClosureType
            || pointerField.GetValue(null) is not IntPtr expectedMethodPointer
            || expectedMethodPointer == IntPtr.Zero
            || actualMethodPointer == IntPtr.Zero
            || actualMethodPointer != expectedMethodPointer)
        {
            error = "output submit delegate does not point to the exact final cooking closure method";
            return false;
        }

        var target = Invoke(bindings.GetDelegateTarget, callback);
        var closurePointer = target == null ? 0 : GetNativePointer(target);
        if (closurePointer == 0
            || !HasExactNativeClass(closurePointer, bindings.OutputClosureType))
        {
            error = "output submit delegate target is not the exact final cooking closure type";
            return false;
        }

        var closure = InvokeConstructor(
            bindings.OutputClosurePointerConstructor,
            (IntPtr)closurePointer)
            ?? throw new InvalidOperationException(
                "Final output closure pointer constructor returned null.");
        var panel = Invoke(bindings.GetOutputClosurePanel, closure);
        var panelPointer = panel == null ? 0 : GetNativePointer(panel);
        if (panel == null
            || panel.GetType() != bindings.PanelType
            || panelPointer == 0
            || !HasExactNativeClass(panelPointer, bindings.PanelType))
        {
            error = "output submit closure panel field is not the exact live cooking panel";
            return false;
        }
        var combo = Invoke(bindings.GetOutputClosureCombo, closure);
        var comboPointer = combo == null ? 0 : GetNativePointer(combo);
        if (combo == null
            || combo.GetType() != bindings.MatchedComboType
            || comboPointer == 0
            || !HasExactNativeClass(comboPointer, bindings.MatchedComboType))
        {
            error = "output submit closure solved field is not the exact live matched combo";
            return false;
        }
        if (!TryReadExactClosureOutput(
                closure,
                bindings,
                out var outputPointer,
                out error))
        {
            return false;
        }

        snapshot = new TargetRecipeOutputClosureBindingSnapshot(
            closurePointer,
            panelPointer,
            comboPointer,
            outputPointer);
        return true;
    }

    public bool TryReadOutputSubmitClosureState(
        object closure,
        out TargetRecipeOutputClosureState state,
        out string error)
    {
        state = default;
        error = "";
        var bindings = GetBindings();
        var closurePointer = GetNativePointer(closure);
        if (closure.GetType() != bindings.OutputClosureType
            || closurePointer == 0
            || !HasExactNativeClass(closurePointer, bindings.OutputClosureType))
        {
            error = "final output closure is not the exact live generated closure type";
            return false;
        }

        var panel = Invoke(bindings.GetOutputClosurePanel, closure);
        var panelPointer = panel == null ? 0 : GetNativePointer(panel);
        if (panel == null
            || panel.GetType() != bindings.PanelType
            || panelPointer == 0
            || !HasExactNativeClass(panelPointer, bindings.PanelType))
        {
            error = "final output closure panel field is not the exact live cooking panel";
            return false;
        }

        var combo = Invoke(bindings.GetOutputClosureCombo, closure);
        var comboPointer = combo == null ? 0 : GetNativePointer(combo);
        if (combo == null
            || combo.GetType() != bindings.MatchedComboType
            || comboPointer == 0
            || !HasExactNativeClass(comboPointer, bindings.MatchedComboType))
        {
            error = "final output closure solved field is not the exact live matched combo";
            return false;
        }

        if (!TryReadExactClosureOutput(
                closure,
                bindings,
                out var outputPointer,
                out error))
        {
            return false;
        }

        state = new TargetRecipeOutputClosureState(
            closurePointer,
            panelPointer,
            comboPointer,
            outputPointer);
        return true;
    }

    private bool TryReadExactClosureOutput(
        object closure,
        ExactBindings bindings,
        out nint outputPointer,
        out string error)
    {
        outputPointer = 0;
        error = "";
        if (closure.GetType() != bindings.OutputClosureType
            || GetNativePointer(closure) == 0)
        {
            error = "final output closure wrapper is not the exact live generated type";
            return false;
        }
        var output = Invoke(bindings.GetOutputClosureOutput, closure);
        outputPointer = output == null ? 0 : GetNativePointer(output);
        if (output == null
            || output.GetType() != bindings.SellableType
            || outputPointer == 0)
        {
            error = "final output closure output field is not the exact live Sellable type";
            outputPointer = 0;
            return false;
        }
        return true;
    }

    private bool TryReadSelectedIngredientIds(
        object selectedList,
        ExactBindings bindings,
        out IReadOnlyList<int> selectedIds,
        out string error)
    {
        selectedIds = Array.Empty<int>();
        error = "";
        var count = InvokeRequired<int>(bindings.GetSelectedIngredientCount, selectedList);
        if (count < 0 || count > MaximumIngredientSlots)
        {
            error = $"selected ingredient count {count} is outside 0..{MaximumIngredientSlots}";
            return false;
        }

        var result = new int[count];
        for (var index = 0; index < count; index += 1)
        {
            result[index] = InvokeRequired<int>(bindings.GetSelectedIngredient, selectedList, index);
        }
        selectedIds = result;
        return true;
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

            var matchedComboType = exactPanelType.GetNestedType(
                MatchedComboNestedTypeName,
                BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new TypeLoadException($"{PanelTypeName}+{MatchedComboNestedTypeName}");
            if (matchedComboType.DeclaringType != exactPanelType
                || matchedComboType.Name != MatchedComboNestedTypeName)
            {
                throw new InvalidOperationException("MatchedCookCombo is not the exact cooking-panel nested type.");
            }
            var outputClosureType = exactPanelType.GetNestedType(
                OutputClosureNestedTypeName,
                BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new TypeLoadException(
                    $"{PanelTypeName}+{OutputClosureNestedTypeName}");
            if (outputClosureType.DeclaringType != exactPanelType
                || outputClosureType.Name != OutputClosureNestedTypeName)
            {
                throw new InvalidOperationException(
                    "Final output closure is not the exact cooking-panel nested type.");
            }

            var recipeType = FindType(RecipeTypeName) ?? throw new TypeLoadException(RecipeTypeName);
            var sellableType = FindType(SellableTypeName)
                ?? throw new TypeLoadException(SellableTypeName);
            var recipeBaseType = recipeType.BaseType;
            if (recipeBaseType?.FullName != RecipeBaseTypeName)
            {
                throw new InvalidOperationException("Recipe does not inherit the exact NonTradableObjectBase type.");
            }

            var buttonBaseType = FindType(UiButtonBaseTypeName)
                ?? throw new TypeLoadException(UiButtonBaseTypeName);
            var selectableType = FindType(SelectableTypeName)
                ?? throw new TypeLoadException(SelectableTypeName);
            if (!selectableType.IsAssignableFrom(buttonBaseType))
            {
                throw new InvalidOperationException("UIButtonBase does not inherit the exact Selectable type.");
            }

            var storageType = FindType(RuntimeStorageTypeName)
                ?? throw new TypeLoadException(RuntimeStorageTypeName);
            var interopUtilsType = FindType(InteropUtilsTypeName)
                ?? throw new TypeLoadException(InteropUtilsTypeName);
            var intArrayType = typeof(Il2CppStructArray<int>);
            var intEnumerableType = typeof(Il2CppSystem.Collections.Generic.IEnumerable<int>);
            var selectedListType = typeof(Il2CppSystem.Collections.Generic.List<int>);
            var getRecipeInstances = RequireExactGetter(exactPanelType, "get_m_RecipeInstances");
            var recipeListType = getRecipeInstances.ReturnType;
            if (!recipeListType.IsGenericType
                || recipeListType.GetGenericTypeDefinition()
                    != typeof(Il2CppSystem.Collections.Generic.List<>)
                || recipeListType.GetGenericArguments().Length != 1
                || recipeListType.GetGenericArguments()[0] != recipeType)
            {
                throw new InvalidOperationException(
                    "m_RecipeInstances is not the exact closed IL2CPP List<Recipe> type.");
            }

            var getCookerType = RequireExactGetter(recipeType, "get_CookerType");
            var getRecipeId = RequireExactMethod(
                recipeBaseType,
                "get_Id",
                false,
                typeof(int));
            var actionType = typeof(Il2CppSystem.Action);
            var delegateType = typeof(Il2CppSystem.Delegate);
            var getOnSubmitCallback = RequireExactMethod(
                buttonBaseType,
                "get_OnSubmitCallback",
                false,
                actionType);
            var outputClosureMethod = RequireExactMethod(
                outputClosureType,
                OutputClosureMethodName,
                false,
                typeof(void));
            _bindings = new ExactBindings(
                exactPanelType,
                matchedComboType,
                outputClosureType,
                recipeType,
                sellableType,
                recipeListType,
                selectedListType,
                buttonBaseType,
                selectableType,
                actionType,
                RequireExactConstructor(exactPanelType, typeof(IntPtr)),
                RequireExactConstructor(matchedComboType, typeof(IntPtr)),
                RequireExactConstructor(outputClosureType, typeof(IntPtr)),
                getRecipeInstances,
                RequireExactMethod(recipeListType, "get_Count", false, typeof(int)),
                RequireExactMethod(recipeListType, "get_Item", false, recipeType, typeof(int)),
                RequireExactMethod(recipeListType, "Insert", false, typeof(void), typeof(int), recipeType),
                RequireExactConstructor(
                    recipeType,
                    typeof(int),
                    typeof(int),
                    getCookerType.ReturnType,
                    typeof(float),
                    intArrayType),
                getRecipeId,
                RequireExactMethod(recipeType, "get_FoodID", false, typeof(int)),
                RequireExactMethod(recipeType, "get_Ingredients", false, intArrayType),
                getCookerType,
                RequireExactMethod(recipeType, "get_BaseCookTime", false, typeof(float)),
                RequireExactMethod(recipeType, "get_CookCount", false, typeof(int)),
                RequireExactMethod(recipeType, "set_CookCount", false, typeof(void), typeof(int)),
                RequireExactMethod(recipeType, "Equals", false, typeof(bool), recipeType),
                RequireExactMethod(buttonBaseType, "CleanOnSubmitCallback", false, typeof(void)),
                RequireExactMethod(selectableType, "set_interactable", false, typeof(void), typeof(bool)),
                RequireExactMethod(exactPanelType, "get_selectedIngredients", false, selectedListType),
                RequireExactMethod(exactPanelType, "get_ExtraCostIng", false, typeof(int)),
                RequireExactMethod(exactPanelType, "get_hasImported", false, typeof(bool)),
                RequireExactMethod(exactPanelType, "get_importedRecipe", false, recipeType),
                RequireExactMethod(exactPanelType, "get_IsFreeCook", false, typeof(bool)),
                RequireExactMethod(selectedListType, "get_Count", false, typeof(int)),
                RequireExactMethod(selectedListType, "get_Item", false, typeof(int), typeof(int)),
                RequireExactMethod(selectedListType, "AddRange", false, typeof(void), intEnumerableType),
                RequireExactMethod(storageType, "GetIngredientCountById", true, typeof(int), typeof(int)),
                RequireExactMethod(
                    storageType,
                    "IngredientOutRange",
                    true,
                    typeof(void),
                    intEnumerableType,
                    typeof(bool)),
                RequireExactMethod(matchedComboType, "get_Recipe", false, recipeType),
                RequireExactMethod(matchedComboType, "get_Modifiers", false, intArrayType),
                getOnSubmitCallback,
                RequireExactMethod(delegateType, "get_method", false, typeof(IntPtr)),
                RequireExactMethod(
                    delegateType,
                    "get_Target",
                    false,
                    typeof(Il2CppSystem.Object)),
                outputClosureMethod,
                RequireExactMethod(
                    interopUtilsType,
                    "GetIl2CppMethodInfoPointerFieldForGeneratedMethod",
                    true,
                    typeof(FieldInfo),
                    typeof(MethodBase)),
                RequireExactMethod(
                    outputClosureType,
                    "get___4__this",
                    false,
                    exactPanelType),
                RequireExactMethod(
                    outputClosureType,
                    "get_solved",
                    false,
                    matchedComboType),
                RequireExactMethod(
                    outputClosureType,
                    "get_output",
                    false,
                    sellableType));
            return _bindings;
        }
    }

    private SelectedVisualBindings GetSelectedVisualBindings(Type panelType)
    {
        lock (_bindingRoot)
        {
            if (_selectedVisualBindings != null)
            {
                if (panelType != _selectedVisualBindings.PanelType)
                {
                    throw new InvalidOperationException(
                        $"Unexpected selected-visual panel type {panelType.FullName}.");
                }
                return _selectedVisualBindings;
            }

            if (panelType.FullName != PanelTypeName)
            {
                throw new InvalidOperationException(
                    $"Unexpected selected-visual panel type {panelType.FullName}.");
            }
            var ingredientType = FindType(IngredientTypeName)
                ?? throw new TypeLoadException(IngredientTypeName);
            var ingredientBaseType = ingredientType.BaseType;
            var objectBaseType = FindType(RecipeBaseTypeName)
                ?? throw new TypeLoadException(RecipeBaseTypeName);
            if (ingredientBaseType?.FullName != IngredientBaseTypeName
                || ingredientBaseType.BaseType != objectBaseType)
            {
                throw new InvalidOperationException(
                    "Ingredient does not inherit the exact TradableObjectBase chain.");
            }

            var getSelectedInstances = RequireExactGetter(
                panelType,
                "get_m_SelectedInstances");
            var selectedInstanceListType = getSelectedInstances.ReturnType;
            if (!selectedInstanceListType.IsGenericType
                || selectedInstanceListType.GetGenericTypeDefinition()
                    != typeof(Il2CppSystem.Collections.Generic.List<>)
                || selectedInstanceListType.GetGenericArguments().Length != 1
                || selectedInstanceListType.GetGenericArguments()[0] != ingredientType)
            {
                throw new InvalidOperationException(
                    "m_SelectedInstances is not the exact closed IL2CPP List<Ingredient> type.");
            }

            _selectedVisualBindings = new SelectedVisualBindings(
                panelType,
                ingredientType,
                selectedInstanceListType,
                getSelectedInstances,
                RequireExactMethod(
                    selectedInstanceListType,
                    "get_Count",
                    false,
                    typeof(int)),
                RequireExactMethod(
                    selectedInstanceListType,
                    "get_Item",
                    false,
                    ingredientType,
                    typeof(int)),
                RequireExactMethod(
                    objectBaseType,
                    "get_Id",
                    false,
                    typeof(int)));
            return _selectedVisualBindings;
        }
    }

    private static bool TryValidateIngredientIds(
        IReadOnlyList<int> ingredientIds,
        out string error)
    {
        error = "";
        if (ingredientIds.Count > MaximumIngredientSlots)
        {
            error = $"ingredient count {ingredientIds.Count} exceeds {MaximumIngredientSlots}";
            return false;
        }
        if (ingredientIds.Any(id => id < 0))
        {
            error = "ingredient ids must be non-negative";
            return false;
        }
        return true;
    }

    private static Il2CppStructArray<int> CreateIntArray(
        IReadOnlyList<int> values,
        bool enforceSlotLimit = true)
    {
        if (enforceSlotLimit && !TryValidateIngredientIds(values, out var error))
        {
            throw new InvalidOperationException(error);
        }
        if (!enforceSlotLimit && values.Any(id => id < 0))
        {
            throw new InvalidOperationException("ingredient ids must be non-negative");
        }

        var array = new Il2CppStructArray<int>(values.Count);
        for (var index = 0; index < values.Count; index += 1) array[index] = values[index];
        return array;
    }

    private static int[] CopyIntArray(Il2CppStructArray<int> values)
    {
        var result = new int[values.Length];
        for (var index = 0; index < values.Length; index += 1) result[index] = values[index];
        return result;
    }

    private static ConstructorInfo RequireExactConstructor(Type type, params Type[] parameterTypes)
    {
        var matches = type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(constructor =>
                constructor.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(parameterTypes))
            .ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new MissingMethodException(type.FullName, ".ctor");
    }

    private static MethodInfo RequireExactGetter(Type type, string name)
    {
        var matches = type.GetMethods(
                BindingFlags.Public
                | BindingFlags.NonPublic
                | BindingFlags.Instance
                | BindingFlags.DeclaredOnly)
            .Where(method =>
                method.Name == name
                && !method.IsStatic
                && method.GetParameters().Length == 0)
            .ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new MissingMethodException(type.FullName, name);
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
            | BindingFlags.DeclaredOnly
            | (isStatic ? BindingFlags.Static : BindingFlags.Instance);
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
                // An unrelated generated interop assembly can fail during lookup.
            }
        }
        return null;
    }

    private static bool HasExactNativeClass(nint objectPointer, Type exactType)
    {
        var expectedClassPointer = Il2CppClassPointerStore.GetNativeClassPointer(exactType);
        var actualClassPointer = IL2CPP.il2cpp_object_get_class(objectPointer);
        return expectedClassPointer != IntPtr.Zero
            && actualClassPointer != IntPtr.Zero
            && actualClassPointer == expectedClassPointer;
    }

    private static T InvokeRequired<T>(MethodInfo method, object? instance, params object?[] args)
    {
        var value = Invoke(method, instance, args);
        return value is T typed
            ? typed
            : throw new InvalidOperationException(
                $"{method.DeclaringType?.FullName}.{method.Name} returned an unexpected value.");
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

    private static object? InvokeConstructor(ConstructorInfo constructor, params object?[] args)
    {
        try
        {
            return constructor.Invoke(args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            throw ex.InnerException;
        }
    }

    private static string FormatPointer(nint pointer)
    {
        return $"0x{unchecked((ulong)(long)pointer):x}";
    }

    private sealed record ExactBindings(
        Type PanelType,
        Type MatchedComboType,
        Type OutputClosureType,
        Type RecipeType,
        Type SellableType,
        Type RecipeListType,
        Type SelectedIngredientListType,
        Type UiButtonBaseType,
        Type SelectableType,
        Type ActionType,
        ConstructorInfo PanelPointerConstructor,
        ConstructorInfo MatchedComboPointerConstructor,
        ConstructorInfo OutputClosurePointerConstructor,
        MethodInfo GetRecipeInstances,
        MethodInfo GetRecipeListCount,
        MethodInfo GetRecipeListItem,
        MethodInfo InsertRecipeListItem,
        ConstructorInfo RecipeConstructor,
        MethodInfo GetRecipeId,
        MethodInfo GetFoodId,
        MethodInfo GetRecipeIngredients,
        MethodInfo GetCookerType,
        MethodInfo GetBaseCookTime,
        MethodInfo GetCookCount,
        MethodInfo SetCookCount,
        MethodInfo RecipeEquals,
        MethodInfo CleanOnSubmitCallback,
        MethodInfo SetInteractable,
        MethodInfo GetSelectedIngredients,
        MethodInfo GetExtraCostIngredient,
        MethodInfo GetHasImported,
        MethodInfo GetImportedRecipe,
        MethodInfo GetIsFreeCook,
        MethodInfo GetSelectedIngredientCount,
        MethodInfo GetSelectedIngredient,
        MethodInfo AddSelectedIngredients,
        MethodInfo GetIngredientQuantity,
        MethodInfo DebitIngredients,
        MethodInfo GetMatchedRecipe,
        MethodInfo GetMatchedModifiers,
        MethodInfo GetOnSubmitCallback,
        MethodInfo GetDelegateMethodPointer,
        MethodInfo GetDelegateTarget,
        MethodInfo OutputClosureMethod,
        MethodInfo GetGeneratedMethodPointerField,
        MethodInfo GetOutputClosurePanel,
        MethodInfo GetOutputClosureCombo,
        MethodInfo GetOutputClosureOutput);

    private sealed record SelectedVisualBindings(
        Type PanelType,
        Type IngredientType,
        Type SelectedInstanceListType,
        MethodInfo GetSelectedInstances,
        MethodInfo GetSelectedInstanceCount,
        MethodInfo GetSelectedInstance,
        MethodInfo GetIngredientId);
}
