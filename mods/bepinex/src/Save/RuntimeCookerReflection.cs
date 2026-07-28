using System.Reflection;
using Il2CppInterop.Runtime.InteropTypes;

namespace MystiaStewardCompanion.Save;

internal sealed class RuntimeCookerControllerState
{
    public object Cooker { get; init; } = null!;
    public IReadOnlyList<int> TypeIds { get; init; } = Array.Empty<int>();
    public int Phase { get; init; } = -1;
    public bool ResultEmpty { get; init; }
    public bool ChosenRecipeEmpty { get; init; }
    public bool CouldOpen { get; init; }

    public bool IsIdle => Phase == 0 && ResultEmpty && ChosenRecipeEmpty && CouldOpen;
}

internal static class RuntimeCookerReflection
{
    private const int MaxCookerTypeCount = 32;
    public const string CookSystemManagerTypeName = "NightScene.CookingUtility.CookSystemManager";
    public const string CookControllerTypeName = "NightScene.CookingUtility.CookController";

    private const string CookerTypeTypeName = "GameData.Core.Collections.Cooker+CookerType";
    private const string CookerTypeName = "GameData.Core.Collections.Cooker";
    private const string CookPhaseTypeName = "NightScene.CookingUtility.CookController+CookPhase";
    private const string SellableTypeName = "GameData.Core.Collections.Sellable";
    private const string RecipeTypeName = "GameData.Core.Collections.Recipe";
    private const string Vector3IntTypeName = "UnityEngine.Vector3Int";
    private const string Il2CppDictionaryTypeName = "Il2CppSystem.Collections.Generic.Dictionary`2";
    private const string Il2CppEnumerableTypeName = "Il2CppSystem.Collections.Generic.IEnumerable`1";
    private const string Il2CppGenericEnumeratorTypeName = "Il2CppSystem.Collections.Generic.IEnumerator`1";
    private const BindingFlags DeclaredInstanceFlags =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;

    private static readonly Dictionary<int, string> CookerTypeNames = new()
    {
        [1] = "煮锅",
        [2] = "烧烤架",
        [3] = "油锅",
        [4] = "蒸锅",
        [5] = "料理台",
    };

    public static object? GetCookSystemManager()
    {
        var type = RuntimeReflectionUtility.FindType(CookSystemManagerTypeName);
        if (type == null) return null;
        var manager = RuntimeReflectionUtility.GetStaticMemberValue(type, "Instance");
        return manager != null && type.IsInstanceOfType(manager) ? manager : null;
    }

    public static List<int> ReadCookerTypeIds(object? cooker)
    {
        if (cooker == null) return new List<int>();
        return TryReadExactCookerTypeSequence(cooker, out var typeIds)
            ? typeIds
            : new List<int>();
    }

    private static bool TryReadExactCookerTypeSequence(object cooker, out List<int> typeIds)
    {
        typeIds = new List<int>();
        if (!TryGetSingleDeclaredMethod(
                cooker.GetType(),
                "get_AllAvailableCookerType",
                Type.EmptyTypes,
                out var getter)
            || !TryGetClosedGenericElementType(
                getter.ReturnType,
                Il2CppEnumerableTypeName,
                CookerTypeTypeName,
                out var cookerType))
        {
            return false;
        }

        object? sequence;
        try
        {
            sequence = getter.Invoke(cooker, Array.Empty<object?>());
        }
        catch
        {
            return false;
        }

        if (sequence is not Il2CppObjectBase
            || !getter.ReturnType.IsInstanceOfType(sequence)
            || !TryGetSingleDeclaredMethod(
                getter.ReturnType,
                "GetEnumerator",
                Type.EmptyTypes,
                out var getEnumerator)
            || !TryGetClosedGenericElementType(
                getEnumerator.ReturnType,
                Il2CppGenericEnumeratorTypeName,
                CookerTypeTypeName,
                out var enumeratorCookerType)
            || enumeratorCookerType != cookerType
            || !TryGetSingleDeclaredMethod(
                getEnumerator.ReturnType,
                "get_Current",
                Type.EmptyTypes,
                out var getCurrent)
            || getCurrent.ReturnType != cookerType
            || !TryGetSingleDeclaredMethod(
                typeof(Il2CppSystem.Collections.IEnumerator),
                "MoveNext",
                Type.EmptyTypes,
                out var moveNext)
            || moveNext.ReturnType != typeof(bool)
            || !TryGetSingleDeclaredMethod(
                typeof(Il2CppSystem.IDisposable),
                "Dispose",
                Type.EmptyTypes,
                out var dispose)
            || dispose.ReturnType != typeof(void))
        {
            return false;
        }

        object? enumerator;
        try
        {
            enumerator = getEnumerator.Invoke(sequence, Array.Empty<object?>());
        }
        catch
        {
            return false;
        }

        if (enumerator is not Il2CppObjectBase
            || !getEnumerator.ReturnType.IsInstanceOfType(enumerator))
        {
            return false;
        }

        var disposable = RuntimeReflectionUtility.TryCastRuntimeObject(
            enumerator,
            typeof(Il2CppSystem.IDisposable).FullName!);
        if (disposable == null) return false;

        var moveNextEnumerator = RuntimeReflectionUtility.TryCastRuntimeObject(
            enumerator,
            typeof(Il2CppSystem.Collections.IEnumerator).FullName!);
        var seen = new HashSet<int>();
        var completed = false;
        var valid = moveNextEnumerator != null;
        try
        {
            for (var index = 0; valid && index < MaxCookerTypeCount; index++)
            {
                if (moveNext.Invoke(moveNextEnumerator, Array.Empty<object?>()) is not bool hasNext)
                {
                    valid = false;
                    break;
                }

                if (!hasNext)
                {
                    completed = true;
                    break;
                }

                var current = getCurrent.Invoke(enumerator, Array.Empty<object?>());
                if (current == null || current.GetType() != cookerType)
                {
                    valid = false;
                    break;
                }

                var typeId = Convert.ToInt32(current);
                if (typeId == 0) continue;
                if (!CookerTypeNames.ContainsKey(typeId))
                {
                    valid = false;
                    break;
                }

                if (seen.Add(typeId)) typeIds.Add(typeId);
            }
        }
        catch
        {
            valid = false;
        }

        try
        {
            dispose.Invoke(disposable, Array.Empty<object?>());
        }
        catch
        {
            valid = false;
        }

        return valid && completed && typeIds.Count > 0;
    }

    public static bool TryReadCookerControllerState(
        object controller,
        out RuntimeCookerControllerState state,
        out string status)
    {
        state = new RuntimeCookerControllerState();
        var controllerType = controller.GetType();
        if (!TryGetExactControllerGetter(controllerType, "get_Cooker", CookerTypeName, out var getCooker)
            || !TryGetExactControllerGetter(controllerType, "get_Phase", CookPhaseTypeName, out var getPhase)
            || !getPhase.ReturnType.IsEnum
            || !TryGetExactControllerGetter(controllerType, "get_Result", SellableTypeName, out var getResult)
            || !TryGetExactControllerGetter(controllerType, "get_ChosenRecipe", RecipeTypeName, out var getChosenRecipe)
            || !TryGetSingleDeclaredMethod(controllerType, "get_CouldCookerOpen", Type.EmptyTypes, out var getCouldOpen)
            || getCouldOpen.ReturnType != typeof(bool))
        {
            status = $"controller-state=unsupported-shape; type={controllerType.FullName}";
            return false;
        }

        object? cooker;
        object? phaseValue;
        object? result;
        object? chosenRecipe;
        object? couldOpenValue;
        try
        {
            cooker = getCooker.Invoke(controller, Array.Empty<object?>());
            phaseValue = getPhase.Invoke(controller, Array.Empty<object?>());
            result = getResult.Invoke(controller, Array.Empty<object?>());
            chosenRecipe = getChosenRecipe.Invoke(controller, Array.Empty<object?>());
            couldOpenValue = getCouldOpen.Invoke(controller, Array.Empty<object?>());
        }
        catch (Exception ex)
        {
            status = $"controller-state=invoke-failed; type={controllerType.FullName}; error={ex.GetBaseException().Message}";
            return false;
        }

        if (cooker == null
            || !getCooker.ReturnType.IsInstanceOfType(cooker)
            || phaseValue == null
            || phaseValue.GetType() != getPhase.ReturnType
            || result != null && !getResult.ReturnType.IsInstanceOfType(result)
            || chosenRecipe != null && !getChosenRecipe.ReturnType.IsInstanceOfType(chosenRecipe)
            || couldOpenValue is not bool couldOpen)
        {
            status = $"controller-state=value-type-mismatch; type={controllerType.FullName}";
            return false;
        }

        int phase;
        try
        {
            phase = Convert.ToInt32(phaseValue);
        }
        catch
        {
            status = $"controller-state=phase-invalid; type={controllerType.FullName}";
            return false;
        }

        if (phase is < 0 or > 3)
        {
            status = $"controller-state=phase-out-of-range; value={phase}; type={controllerType.FullName}";
            return false;
        }

        var typeIds = ReadCookerTypeIds(cooker);
        if (typeIds.Count == 0)
        {
            status = $"controller-state=cooker-types-unavailable; type={controllerType.FullName}";
            return false;
        }

        state = new RuntimeCookerControllerState
        {
            Cooker = cooker,
            TypeIds = typeIds,
            Phase = phase,
            ResultEmpty = result == null,
            ChosenRecipeEmpty = chosenRecipe == null,
            CouldOpen = couldOpen,
        };
        status = $"controller-state=ok; phase={phase}; resultEmpty={state.ResultEmpty}; chosenRecipeEmpty={state.ChosenRecipeEmpty}; couldOpen={couldOpen}; types={string.Join(",", typeIds)}";
        return true;
    }

    public static string ResolveCookerTypeName(int typeId)
    {
        return CookerTypeNames.TryGetValue(typeId, out var name) ? name : $"#{typeId}";
    }

    public static IReadOnlyList<object> ReadCookerControllersFromCookSystem(object? cookSystem, out string status)
    {
        if (cookSystem == null)
        {
            status = "allCookers=manager-missing";
            return Array.Empty<object>();
        }

        if (!TryGetSingleDeclaredMethod(
                cookSystem.GetType(),
                "get_AllCookers",
                Type.EmptyTypes,
                out var getAllCookers)
            || !TryGetExactAllCookersShape(getAllCookers.ReturnType, out var keyType, out var valueType))
        {
            status = $"allCookers=unsupported-getter; managerType={cookSystem.GetType().FullName}";
            return Array.Empty<object>();
        }

        object? allCookers;
        try
        {
            allCookers = getAllCookers.Invoke(cookSystem, Array.Empty<object?>());
        }
        catch (Exception ex)
        {
            status = $"allCookers=getter-failed; error={ex.GetBaseException().Message}";
            return Array.Empty<object>();
        }

        if (allCookers == null)
        {
            status = "allCookers=uninitialized";
            return Array.Empty<object>();
        }

        if (allCookers.GetType() != getAllCookers.ReturnType)
        {
            status = $"allCookers=value-type-mismatch; declared={getAllCookers.ReturnType.FullName}; actual={allCookers.GetType().FullName}";
            return Array.Empty<object>();
        }

        if (!RuntimeConcreteCollectionReader.TryReadDictionary(allCookers, out var entries, out var failure))
        {
            status = $"allCookers=read-failed; failure={failure}; type={allCookers.GetType().FullName}";
            return Array.Empty<object>();
        }

        var result = new List<object>(entries.Count);
        var seen = new HashSet<nint>();
        foreach (var entry in entries)
        {
            if (entry.Key == null
                || entry.Key.GetType() != keyType
                || entry.Value == null
                || !valueType.IsInstanceOfType(entry.Value))
            {
                status = $"allCookers=element-type-mismatch; type={allCookers.GetType().FullName}";
                return Array.Empty<object>();
            }

            if (entry.Value is not Il2CppObjectBase controller
                || controller.Pointer == IntPtr.Zero)
            {
                status = "allCookers=controller-pointer-unavailable";
                return Array.Empty<object>();
            }

            var pointer = (nint)controller.Pointer;
            if (pointer == 0 || !seen.Add(pointer))
            {
                status = $"allCookers=invalid-controller-identity; pointer=0x{(long)pointer:X}";
                return Array.Empty<object>();
            }

            result.Add(entry.Value);
        }

        status = $"allCookers=ok; entries={entries.Count}; controllers={result.Count}; type={allCookers.GetType().FullName}";
        return result;
    }

    private static bool TryGetExactAllCookersShape(Type dictionaryType, out Type keyType, out Type valueType)
    {
        keyType = typeof(void);
        valueType = typeof(void);
        if (!dictionaryType.IsGenericType) return false;

        Type genericDefinition;
        Type[] arguments;
        try
        {
            genericDefinition = dictionaryType.GetGenericTypeDefinition();
            arguments = dictionaryType.GetGenericArguments();
        }
        catch
        {
            return false;
        }

        var definitionName = genericDefinition.FullName;
        if (definitionName != Il2CppDictionaryTypeName)
        {
            return false;
        }

        if (arguments.Length != 2
            || arguments[0].FullName != Vector3IntTypeName
            || arguments[1].FullName != CookControllerTypeName)
        {
            return false;
        }

        keyType = arguments[0];
        valueType = arguments[1];
        return true;
    }

    private static bool TryGetClosedGenericElementType(
        Type type,
        string expectedDefinitionName,
        string expectedElementName,
        out Type elementType)
    {
        elementType = typeof(void);
        if (!type.IsGenericType) return false;

        Type definition;
        Type[] arguments;
        try
        {
            definition = type.GetGenericTypeDefinition();
            arguments = type.GetGenericArguments();
        }
        catch
        {
            return false;
        }

        if (definition.FullName != expectedDefinitionName
            || arguments.Length != 1
            || arguments[0].FullName != expectedElementName
            || !arguments[0].IsEnum)
        {
            return false;
        }

        elementType = arguments[0];
        return true;
    }

    private static bool TryGetExactControllerGetter(
        Type controllerType,
        string methodName,
        string returnTypeName,
        out MethodInfo method)
    {
        return TryGetSingleDeclaredMethod(
                controllerType,
                methodName,
                Type.EmptyTypes,
                out method)
            && method.ReturnType.FullName == returnTypeName;
    }

    private static bool TryGetSingleDeclaredMethod(
        Type type,
        string name,
        Type[] parameterTypes,
        out MethodInfo method)
    {
        method = null!;
        MethodInfo[] matches;
        try
        {
            matches = type
                .GetMethods(DeclaredInstanceFlags)
                .Where(candidate => candidate.Name == name
                    && candidate.GetParameters()
                        .Select(parameter => parameter.ParameterType)
                        .SequenceEqual(parameterTypes))
                .ToArray();
        }
        catch
        {
            return false;
        }

        if (matches.Length != 1) return false;
        method = matches[0];
        return true;
    }
}
