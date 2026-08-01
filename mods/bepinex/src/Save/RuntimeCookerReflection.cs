using System.Reflection;
using Il2CppInterop.Runtime.InteropTypes;

namespace MystiaStewardCompanion.Save;

internal sealed class RuntimeCookerControllerState
{
    public object Cooker { get; init; } = null!;
    public IReadOnlyList<int> TypeIds { get; init; } = Array.Empty<int>();
    public bool IsEmptyDesk { get; init; }
    public int Phase { get; init; } = -1;
    public object? Result { get; init; }
    public object? ChosenRecipe { get; init; }
    public bool CouldOpen { get; init; }

    public bool ResultEmpty => Result == null;
    public bool ChosenRecipeEmpty => ChosenRecipe == null;
}

internal readonly record struct RuntimeCookerGridPosition(int X, int Y, int Z)
{
    public override string ToString()
    {
        return $"{X},{Y},{Z}";
    }
}

internal sealed class RuntimeCookerControllerEntry
{
    public object Controller { get; init; } = null!;
    public RuntimeCookerGridPosition GridPosition { get; init; }
    public string ControllerIdentity { get; init; } = "";
}

internal sealed class RuntimeCookerContentState
{
    public int Phase { get; init; } = -1;
    public object? Result { get; init; }
    public object? ChosenRecipe { get; init; }

    public bool IsExactReset => Phase == 0 && Result == null && ChosenRecipe == null;
}

internal static class RuntimeCookerReflection
{
    public const string CookSystemManagerTypeName = "NightScene.CookingUtility.CookSystemManager";
    public const string CookControllerTypeName = "NightScene.CookingUtility.CookController";

    private const string CookerTypeTypeName = "GameData.Core.Collections.Cooker+CookerType";
    private const string CookerTypeName = "GameData.Core.Collections.Cooker";
    private const string CookPhaseTypeName = "NightScene.CookingUtility.CookController+CookPhase";
    private const string SellableTypeName = "GameData.Core.Collections.Sellable";
    private const string RecipeTypeName = "GameData.Core.Collections.Recipe";
    private const string Vector3IntTypeName = "UnityEngine.Vector3Int";
    private const string EventManagerTypeName = "NightScene.EventUtility.EventManager";
    private const string Il2CppDictionaryTypeName = "Il2CppSystem.Collections.Generic.Dictionary`2";
    private const string Il2CppEnumerableTypeName = "Il2CppSystem.Collections.Generic.IEnumerable`1";
    private const string Il2CppGenericEnumeratorTypeName = "Il2CppSystem.Collections.Generic.IEnumerator`1";
    private const string Il2CppStructArrayTypeName =
        "Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray`1";
    private const string MonoSingletonTypeName = "DEYU.Singletons.MonoSingleton`1";
    private const int MaxLockedCookerCount = 256;
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
        return TryGetExactMonoSingletonInstance(type, out var manager)
            ? manager
            : null;
    }

    public static bool TryReadCookerTypeIds(
        object? cooker,
        out IReadOnlyList<int> typeIds,
        out bool observedEmpty,
        out string status)
    {
        typeIds = Array.Empty<int>();
        observedEmpty = false;
        if (cooker == null)
        {
            status = "cooker-types=cooker-missing";
            return false;
        }

        if (!TryReadExactCookerTypeSequence(
                cooker,
                out var exactTypeIds,
                out observedEmpty,
                out status))
        {
            return false;
        }

        typeIds = exactTypeIds;
        return true;
    }

    private static bool TryReadExactCookerTypeSequence(
        object cooker,
        out List<int> typeIds,
        out bool observedEmpty,
        out string status)
    {
        typeIds = new List<int>();
        observedEmpty = false;
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
            status = $"cooker-types=getter-shape-invalid; cookerType={cooker.GetType().FullName}";
            return false;
        }

        object? sequence;
        try
        {
            sequence = getter.Invoke(cooker, Array.Empty<object?>());
        }
        catch (Exception ex)
        {
            status = $"cooker-types=getter-invoke-failed; error={RuntimeCookerTypeSequenceReader.FormatException(ex)}";
            return false;
        }

        if (sequence is not Il2CppObjectBase || !getter.ReturnType.IsInstanceOfType(sequence))
        {
            status = $"cooker-types=sequence-shape-invalid; declared={getter.ReturnType.FullName}; "
                + $"actual={sequence?.GetType().FullName ?? "null"}";
            return false;
        }

        if (!TryGetSingleDeclaredMethod(
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
            status = $"cooker-types=enumerator-contract-invalid; sequenceType={getter.ReturnType.FullName}";
            return false;
        }

        object? enumerator;
        try
        {
            enumerator = getEnumerator.Invoke(sequence, Array.Empty<object?>());
        }
        catch (Exception ex)
        {
            status = $"cooker-types=get-enumerator-failed; error={RuntimeCookerTypeSequenceReader.FormatException(ex)}";
            return false;
        }

        if (enumerator is not Il2CppObjectBase
            || !getEnumerator.ReturnType.IsInstanceOfType(enumerator))
        {
            status = $"cooker-types=enumerator-shape-invalid; declared={getEnumerator.ReturnType.FullName}; "
                + $"actual={enumerator?.GetType().FullName ?? "null"}";
            return false;
        }

        var disposable = RuntimeReflectionUtility.TryCastRuntimeObject(
            enumerator,
            typeof(Il2CppSystem.IDisposable).FullName!);
        if (disposable == null)
        {
            status = $"cooker-types=dispose-cast-failed; enumeratorType={enumerator.GetType().FullName}";
            return false;
        }

        var moveNextEnumerator = RuntimeReflectionUtility.TryCastRuntimeObject(
            enumerator,
            typeof(Il2CppSystem.Collections.IEnumerator).FullName!);
        if (moveNextEnumerator == null)
        {
            TryDisposeCookerTypeEnumerator(dispose, disposable);
            status = $"cooker-types=move-next-cast-failed; enumeratorType={enumerator.GetType().FullName}";
            return false;
        }

        if (!RuntimeCookerTypeSequenceReader.TryRead(
                () => moveNext.Invoke(moveNextEnumerator, Array.Empty<object?>()),
                () => getCurrent.Invoke(enumerator, Array.Empty<object?>()),
                cookerType,
                () => dispose.Invoke(disposable, Array.Empty<object?>()),
                out var exactTypeIds,
                out observedEmpty,
                out status))
        {
            return false;
        }

        typeIds.AddRange(exactTypeIds);
        return true;
    }

    private static void TryDisposeCookerTypeEnumerator(MethodInfo dispose, object disposable)
    {
        try
        {
            dispose.Invoke(disposable, Array.Empty<object?>());
        }
        catch
        {
            // The caller reports the earlier interface conversion failure.
        }
    }

    public static bool TryReadCookerControllerState(
        object controller,
        out RuntimeCookerControllerState state,
        out string status)
    {
        state = new RuntimeCookerControllerState();
        var controllerType = controller.GetType();
        if (!TryGetSingleDeclaredMethod(controllerType, "get_IsEmptyDesk", Type.EmptyTypes, out var getIsEmptyDesk)
            || getIsEmptyDesk.ReturnType != typeof(bool)
            || !TryGetExactControllerGetter(controllerType, "get_Cooker", CookerTypeName, out var getCooker)
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
        object? isEmptyDeskValue;
        try
        {
            isEmptyDeskValue = getIsEmptyDesk.Invoke(controller, Array.Empty<object?>());
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
            || couldOpenValue is not bool couldOpen
            || isEmptyDeskValue is not bool isEmptyDesk)
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

        if (!TryReadCookerTypeIds(
                cooker,
                out var typeIds,
                out var observedEmpty,
                out var typeStatus))
        {
            status = $"controller-state=cooker-types-unavailable; {typeStatus}; "
                + $"controllerType={controllerType.FullName}; cookerType={cooker.GetType().FullName}";
            return false;
        }

        if (!RuntimeCookerTypeSequenceReader.TryValidateControllerState(
                isEmptyDesk,
                observedEmpty,
                typeIds.Count,
                phase,
                result == null,
                chosenRecipe == null,
                out var consistencyStatus))
        {
            status = $"controller-state={consistencyStatus}; phase={phase}; "
                + $"resultEmpty={result == null}; chosenRecipeEmpty={chosenRecipe == null}; {typeStatus}; "
                + $"controllerType={controllerType.FullName}; cookerType={cooker.GetType().FullName}";
            return false;
        }

        state = new RuntimeCookerControllerState
        {
            Cooker = cooker,
            TypeIds = typeIds,
            IsEmptyDesk = isEmptyDesk,
            Phase = phase,
            Result = result,
            ChosenRecipe = chosenRecipe,
            CouldOpen = couldOpen,
        };
        status = $"controller-state={consistencyStatus}; phase={phase}; "
            + $"resultEmpty={state.ResultEmpty}; chosenRecipeEmpty={state.ChosenRecipeEmpty}; "
            + $"couldOpen={couldOpen}; {typeStatus}";
        return true;
    }

    public static string ResolveCookerTypeName(int typeId)
    {
        return CookerTypeNames.TryGetValue(typeId, out var name) ? name : $"#{typeId}";
    }

    public static bool TryReadCookerControllerEntriesFromCookSystem(
        object? cookSystem,
        IReadOnlySet<RuntimeCookerGridPosition> lockedPositions,
        out IReadOnlyList<RuntimeCookerControllerEntry> controllerEntries,
        out string status)
    {
        ArgumentNullException.ThrowIfNull(lockedPositions);
        controllerEntries = Array.Empty<RuntimeCookerControllerEntry>();
        if (cookSystem == null)
        {
            status = "allCookers=manager-missing";
            return false;
        }

        if (!TryGetSingleDeclaredMethod(
                cookSystem.GetType(),
                "get_AllCookers",
                Type.EmptyTypes,
                out var getAllCookers)
            || !TryGetExactAllCookersShape(getAllCookers.ReturnType, out var keyType, out var valueType))
        {
            status = $"allCookers=unsupported-getter; managerType={cookSystem.GetType().FullName}";
            return false;
        }

        object? allCookers;
        try
        {
            allCookers = getAllCookers.Invoke(cookSystem, Array.Empty<object?>());
        }
        catch (Exception ex)
        {
            status = $"allCookers=getter-failed; error={ex.GetBaseException().Message}";
            return false;
        }

        if (allCookers == null)
        {
            status = "allCookers=uninitialized";
            return false;
        }

        if (allCookers.GetType() != getAllCookers.ReturnType)
        {
            status = $"allCookers=value-type-mismatch; declared={getAllCookers.ReturnType.FullName}; actual={allCookers.GetType().FullName}";
            return false;
        }

        if (!RuntimeConcreteCollectionReader.TryReadDictionary(allCookers, out var entries, out var failure))
        {
            status = $"allCookers=read-failed; failure={failure}; type={allCookers.GetType().FullName}";
            return false;
        }

        var result = new List<RuntimeCookerControllerEntry>(entries.Count);
        var seenPointers = new HashSet<nint>();
        var seenPositions = new HashSet<RuntimeCookerGridPosition>();
        foreach (var entry in entries)
        {
            if (entry.Key == null
                || entry.Key.GetType() != keyType
                || entry.Value == null
                || !valueType.IsInstanceOfType(entry.Value))
            {
                status = $"allCookers=element-type-mismatch; type={allCookers.GetType().FullName}";
                return false;
            }

            if (entry.Value is not Il2CppObjectBase controller
                || controller.Pointer == IntPtr.Zero)
            {
                status = "allCookers=controller-pointer-unavailable";
                return false;
            }

            var pointer = (nint)controller.Pointer;
            if (pointer == 0 || !seenPointers.Add(pointer))
            {
                status = $"allCookers=invalid-controller-identity; pointer=0x{(long)pointer:X}";
                return false;
            }

            if (!TryReadExactVector3Int(entry.Key, keyType, out var dictionaryPosition)
                || !seenPositions.Add(dictionaryPosition))
            {
                status = $"allCookers=invalid-grid-key; pointer=0x{(long)pointer:X}";
                return false;
            }

            result.Add(new RuntimeCookerControllerEntry
            {
                Controller = entry.Value,
                GridPosition = dictionaryPosition,
                ControllerIdentity = $"0x{unchecked((ulong)(long)pointer):X}",
            });
        }

        var missingLockedPosition = lockedPositions
            .Where(position => !seenPositions.Contains(position))
            .OrderBy(position => position.X)
            .ThenBy(position => position.Y)
            .ThenBy(position => position.Z)
            .FirstOrDefault();
        if (lockedPositions.Any(position => !seenPositions.Contains(position)))
        {
            status = $"allCookers=locked-grid-missing; position={missingLockedPosition}";
            return false;
        }

        foreach (var entry in result)
        {
            if (lockedPositions.Contains(entry.GridPosition))
            {
                continue;
            }

            if (!TryReadControllerGridPosition(
                    entry.Controller,
                    keyType,
                    out var controllerPosition,
                    out var positionStatus))
            {
                status = $"allCookers=controller-grid-unavailable; pointer={entry.ControllerIdentity}; {positionStatus}";
                return false;
            }

            if (entry.GridPosition != controllerPosition)
            {
                status = $"allCookers=controller-grid-mismatch; pointer={entry.ControllerIdentity}; "
                    + $"key={entry.GridPosition}; controller={controllerPosition}";
                return false;
            }
        }

        result.Sort(static (left, right) =>
        {
            var compare = left.GridPosition.X.CompareTo(right.GridPosition.X);
            if (compare != 0) return compare;
            compare = left.GridPosition.Y.CompareTo(right.GridPosition.Y);
            if (compare != 0) return compare;
            compare = left.GridPosition.Z.CompareTo(right.GridPosition.Z);
            if (compare != 0) return compare;
            return string.CompareOrdinal(left.ControllerIdentity, right.ControllerIdentity);
        });
        status = $"allCookers=ok; entries={entries.Count}; controllers={result.Count}; "
            + $"lockedKeys={result.Count(entry => lockedPositions.Contains(entry.GridPosition))}; "
            + $"type={allCookers.GetType().FullName}";
        controllerEntries = result;
        return true;
    }

    public static bool TryReadLockedCookerPositions(
        out IReadOnlySet<RuntimeCookerGridPosition> positions,
        out string status)
    {
        positions = new HashSet<RuntimeCookerGridPosition>();
        var eventManagerType = RuntimeReflectionUtility.FindType(EventManagerTypeName);
        if (eventManagerType == null)
        {
            status = "lockedCookers=event-manager-type-missing";
            return false;
        }

        if (!TryGetExactMonoSingletonInstance(eventManagerType, out var eventManager))
        {
            status = "lockedCookers=event-manager-instance-missing";
            return false;
        }

        if (!TryGetSingleDeclaredMethod(
                eventManagerType,
                "get_LockedCookers",
                Type.EmptyTypes,
                out var getLockedCookers)
            || !TryGetClosedStructArrayElementType(
                getLockedCookers.ReturnType,
                Vector3IntTypeName,
                out var vectorType))
        {
            status = $"lockedCookers=unsupported-getter; managerType={eventManagerType.FullName}";
            return false;
        }

        object? array;
        try
        {
            array = getLockedCookers.Invoke(eventManager, Array.Empty<object?>());
        }
        catch (Exception ex)
        {
            status = $"lockedCookers=getter-failed; error={ex.GetBaseException().Message}";
            return false;
        }

        if (array == null || array.GetType() != getLockedCookers.ReturnType)
        {
            status = $"lockedCookers=value-type-mismatch; declared={getLockedCookers.ReturnType.FullName}; "
                + $"actual={array?.GetType().FullName ?? "null"}";
            return false;
        }

        if (!TryReadExactVector3IntArray(array, vectorType, out var values, out status))
        {
            return false;
        }

        positions = values.ToHashSet();
        status = $"lockedCookers=ok; entries={values.Count}; unique={positions.Count}";
        return true;
    }

    private static bool TryReadControllerGridPosition(
        object controller,
        Type vectorType,
        out RuntimeCookerGridPosition position,
        out string status)
    {
        position = default;
        var controllerType = controller.GetType();
        if (!TryGetExactControllerGetter(
                controllerType,
                "get_GridPosition",
                Vector3IntTypeName,
                out var getGridPosition)
            || getGridPosition.ReturnType != vectorType)
        {
            status = $"controller-grid=unsupported-shape; type={controllerType.FullName}";
            return false;
        }

        object? value;
        try
        {
            value = getGridPosition.Invoke(controller, Array.Empty<object?>());
        }
        catch (Exception ex)
        {
            status = $"controller-grid=invoke-failed; type={controllerType.FullName}; "
                + $"error={ex.GetBaseException().Message}";
            return false;
        }

        if (!TryReadExactVector3Int(value, vectorType, out position))
        {
            status = $"controller-grid=value-invalid; type={controllerType.FullName}";
            return false;
        }

        status = $"controller-grid=ok; position={position}";
        return true;
    }

    private static bool TryReadExactVector3Int(
        object? value,
        Type vectorType,
        out RuntimeCookerGridPosition position)
    {
        position = default;
        if (value == null || value.GetType() != vectorType)
        {
            return false;
        }

        if (!TryGetSingleDeclaredMethod(vectorType, "get_x", Type.EmptyTypes, out var getX)
            || getX.ReturnType != typeof(int)
            || !TryGetSingleDeclaredMethod(vectorType, "get_y", Type.EmptyTypes, out var getY)
            || getY.ReturnType != typeof(int)
            || !TryGetSingleDeclaredMethod(vectorType, "get_z", Type.EmptyTypes, out var getZ)
            || getZ.ReturnType != typeof(int))
        {
            return false;
        }

        try
        {
            if (getX.Invoke(value, Array.Empty<object?>()) is not int x
                || getY.Invoke(value, Array.Empty<object?>()) is not int y
                || getZ.Invoke(value, Array.Empty<object?>()) is not int z)
            {
                return false;
            }

            position = new RuntimeCookerGridPosition(x, y, z);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadExactVector3IntArray(
        object array,
        Type vectorType,
        out IReadOnlyList<RuntimeCookerGridPosition> positions,
        out string status)
    {
        positions = Array.Empty<RuntimeCookerGridPosition>();
        var arrayType = array.GetType();
        var length = arrayType.GetProperty(
            "Length",
            BindingFlags.Public | BindingFlags.Instance);
        var indexers = arrayType
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(method => method.Name == "get_Item"
                && method.ReturnType == vectorType
                && method.GetParameters()
                    .Select(parameter => parameter.ParameterType)
                    .SequenceEqual(new[] { typeof(int) }))
            .ToArray();
        if (length == null
            || length.PropertyType != typeof(int)
            || length.GetIndexParameters().Length != 0
            || indexers.Length != 1)
        {
            status = $"lockedCookers=unsupported-array-shape; type={arrayType.FullName}";
            return false;
        }

        int count;
        try
        {
            if (length.GetValue(array) is not int value)
            {
                status = $"lockedCookers=length-type-mismatch; type={arrayType.FullName}";
                return false;
            }

            count = value;
        }
        catch (Exception ex)
        {
            status = $"lockedCookers=length-failed; error={ex.GetBaseException().Message}";
            return false;
        }

        if (count is < 0 or > MaxLockedCookerCount)
        {
            status = $"lockedCookers=count-out-of-range; count={count}";
            return false;
        }

        var result = new RuntimeCookerGridPosition[count];
        try
        {
            for (var index = 0; index < count; index++)
            {
                var value = indexers[0].Invoke(array, new object?[] { index });
                if (!TryReadExactVector3Int(value, vectorType, out result[index]))
                {
                    status = $"lockedCookers=element-invalid; index={index}";
                    return false;
                }
            }
        }
        catch (Exception ex)
        {
            status = $"lockedCookers=indexer-failed; error={ex.GetBaseException().Message}";
            return false;
        }

        positions = result;
        status = $"lockedCookers=array-ok; entries={result.Length}";
        return true;
    }

    private static bool TryGetClosedStructArrayElementType(
        Type type,
        string expectedElementName,
        out Type elementType)
    {
        elementType = typeof(void);
        if (!type.IsGenericType || type.ContainsGenericParameters) return false;

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

        if (definition.FullName != Il2CppStructArrayTypeName
            || arguments.Length != 1
            || arguments[0].FullName != expectedElementName
            || !arguments[0].IsValueType)
        {
            return false;
        }

        elementType = arguments[0];
        return true;
    }

    private static bool TryGetExactMonoSingletonInstance(
        Type concreteType,
        out object instance)
    {
        instance = null!;
        var baseType = concreteType.BaseType;
        if (baseType == null
            || !baseType.IsGenericType
            || baseType.ContainsGenericParameters)
        {
            return false;
        }

        Type definition;
        Type[] arguments;
        try
        {
            definition = baseType.GetGenericTypeDefinition();
            arguments = baseType.GetGenericArguments();
        }
        catch
        {
            return false;
        }

        if (definition.FullName != MonoSingletonTypeName
            || arguments.Length != 1
            || arguments[0] != concreteType)
        {
            return false;
        }

        var getters = baseType
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(method => method.Name == "get_Instance"
                && method.ReturnType == concreteType
                && method.GetParameters().Length == 0)
            .ToArray();
        if (getters.Length != 1)
        {
            return false;
        }

        try
        {
            var value = getters[0].Invoke(null, Array.Empty<object?>());
            if (value == null || !concreteType.IsInstanceOfType(value))
            {
                return false;
            }

            instance = value;
            return true;
        }
        catch
        {
            return false;
        }
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
