using System.Collections.Concurrent;
using System.Reflection;

namespace MystiaStewardCompanion.Save;

internal enum RuntimeCollectionReadFailure
{
    None,
    Missing,
    UnsupportedShape,
    InvocationFailed,
    CountMismatch,
    ElementTypeMismatch,
}

internal readonly record struct RuntimeDictionaryEntry(object? Key, object? Value);

internal static class RuntimeConcreteCollectionReader
{
    private const int MaxCollectionCount = 100_000;
    private const BindingFlags InstanceFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private const string ManagedDictionaryTypeName = "System.Collections.Generic.Dictionary`2";
    private const string Il2CppDictionaryTypeName = "Il2CppSystem.Collections.Generic.Dictionary`2";
    private const string Il2CppReferenceArrayTypeName = "Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray`1";
    private const string Il2CppStructArrayTypeName = "Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray`1";

    private static readonly ConcurrentDictionary<Type, DictionaryShape> DictionaryShapes = new();
    private static readonly ConcurrentDictionary<Type, DictionaryLookupShape> DictionaryLookupShapes = new();
    private static readonly ConcurrentDictionary<Type, ArrayShape> ArrayShapes = new();

    public static bool TryGetDictionaryValue(
        object? dictionary,
        object? key,
        out object? value,
        out bool found,
        out RuntimeCollectionReadFailure failure)
    {
        value = null;
        found = false;
        if (dictionary == null)
        {
            failure = RuntimeCollectionReadFailure.Missing;
            return false;
        }

        var dictionaryType = dictionary.GetType();
        if (!TryResolveDictionaryLookupShape(dictionaryType, out var shape))
        {
            failure = RuntimeCollectionReadFailure.UnsupportedShape;
            return false;
        }

        if (key == null || key.GetType() != shape.KeyType)
        {
            failure = RuntimeCollectionReadFailure.ElementTypeMismatch;
            return false;
        }

        if (!TryReadIntProperty(dictionary, shape.Count, out var initialCount))
        {
            failure = RuntimeCollectionReadFailure.InvocationFailed;
            return false;
        }

        if (!IsSaneCount(initialCount))
        {
            failure = RuntimeCollectionReadFailure.CountMismatch;
            return false;
        }

        if (!TryInvoke(shape.ContainsKey, dictionary, new[] { key }, out var rawFound)
            || rawFound is not bool containsKey)
        {
            failure = RuntimeCollectionReadFailure.InvocationFailed;
            return false;
        }

        if (containsKey
            && !TryInvoke(shape.Indexer, dictionary, new[] { key }, out value))
        {
            value = null;
            failure = RuntimeCollectionReadFailure.InvocationFailed;
            return false;
        }

        if (!TryReadIntProperty(dictionary, shape.Count, out var finalCount))
        {
            value = null;
            failure = RuntimeCollectionReadFailure.InvocationFailed;
            return false;
        }

        if (finalCount != initialCount)
        {
            value = null;
            failure = RuntimeCollectionReadFailure.CountMismatch;
            return false;
        }

        found = containsKey;
        failure = RuntimeCollectionReadFailure.None;
        return true;
    }

    public static bool TryReadDictionary(
        object? dictionary,
        out IReadOnlyList<RuntimeDictionaryEntry> entries,
        out RuntimeCollectionReadFailure failure)
    {
        entries = Array.Empty<RuntimeDictionaryEntry>();
        if (dictionary == null)
        {
            failure = RuntimeCollectionReadFailure.Missing;
            return false;
        }

        var dictionaryType = dictionary.GetType();
        if (!TryResolveDictionaryHeader(dictionaryType, out var countProperty))
        {
            failure = RuntimeCollectionReadFailure.UnsupportedShape;
            return false;
        }

        if (!TryReadIntProperty(dictionary, countProperty, out var initialCount))
        {
            failure = RuntimeCollectionReadFailure.InvocationFailed;
            return false;
        }

        if (!IsSaneCount(initialCount))
        {
            failure = RuntimeCollectionReadFailure.CountMismatch;
            return false;
        }

        if (initialCount == 0)
        {
            if (!TryReadIntProperty(dictionary, countProperty, out var finalCount))
            {
                failure = RuntimeCollectionReadFailure.InvocationFailed;
                return false;
            }

            if (finalCount != 0)
            {
                failure = RuntimeCollectionReadFailure.CountMismatch;
                return false;
            }

            failure = RuntimeCollectionReadFailure.None;
            return true;
        }

        if (!TryResolveDictionaryShape(dictionaryType, out var shape))
        {
            failure = RuntimeCollectionReadFailure.UnsupportedShape;
            return false;
        }

        if (!TryInvoke(shape.GetEnumerator, dictionary, Array.Empty<object?>(), out var enumerator)
            || enumerator == null
            || enumerator.GetType() != shape.EnumeratorType)
        {
            failure = RuntimeCollectionReadFailure.InvocationFailed;
            return false;
        }

        var result = new List<RuntimeDictionaryEntry>(initialCount);
        var readFailure = RuntimeCollectionReadFailure.None;
        try
        {
            for (var index = 0; index < initialCount; index++)
            {
                if (!TryInvokeBool(shape.MoveNext, enumerator, out var hasNext))
                {
                    readFailure = RuntimeCollectionReadFailure.InvocationFailed;
                    break;
                }

                if (!hasNext)
                {
                    readFailure = RuntimeCollectionReadFailure.CountMismatch;
                    break;
                }

                if (!TryReadProperty(enumerator, shape.Current, out var current)
                    || current == null
                    || current.GetType() != shape.KeyValuePairType)
                {
                    readFailure = RuntimeCollectionReadFailure.InvocationFailed;
                    break;
                }

                if (!TryReadProperty(current, shape.Key, out var key)
                    || !TryReadProperty(current, shape.Value, out var value))
                {
                    readFailure = RuntimeCollectionReadFailure.InvocationFailed;
                    break;
                }

                result.Add(new RuntimeDictionaryEntry(key, value));
            }

            if (readFailure == RuntimeCollectionReadFailure.None)
            {
                if (!TryInvokeBool(shape.MoveNext, enumerator, out var hasExtra))
                {
                    readFailure = RuntimeCollectionReadFailure.InvocationFailed;
                }
                else if (hasExtra)
                {
                    readFailure = RuntimeCollectionReadFailure.CountMismatch;
                }
                else if (!TryReadIntProperty(dictionary, shape.Count, out var finalCount))
                {
                    readFailure = RuntimeCollectionReadFailure.InvocationFailed;
                }
                else if (finalCount != initialCount)
                {
                    readFailure = RuntimeCollectionReadFailure.CountMismatch;
                }
            }
        }
        finally
        {
            if (!TryInvoke(shape.Dispose, enumerator, Array.Empty<object?>(), out _)
                && readFailure == RuntimeCollectionReadFailure.None)
            {
                readFailure = RuntimeCollectionReadFailure.InvocationFailed;
            }
        }

        if (readFailure != RuntimeCollectionReadFailure.None)
        {
            failure = readFailure;
            return false;
        }

        entries = result;
        failure = RuntimeCollectionReadFailure.None;
        return true;
    }

    public static bool TryReadReferenceArray(
        object? array,
        out IReadOnlyList<object?> values,
        out RuntimeCollectionReadFailure failure)
    {
        return TryReadArrayCore(array, RuntimeArrayKind.Reference, out values, out failure);
    }

    public static bool TryReadIntArray(
        object? array,
        out IReadOnlyList<int> values,
        out RuntimeCollectionReadFailure failure)
    {
        values = Array.Empty<int>();
        if (!HasExactArrayElementType(array, typeof(int)))
        {
            failure = array == null
                ? RuntimeCollectionReadFailure.Missing
                : RuntimeCollectionReadFailure.UnsupportedShape;
            return false;
        }

        if (!TryReadArrayCore(array, RuntimeArrayKind.Struct, out var rawValues, out failure)) return false;

        var result = new int[rawValues.Count];
        for (var index = 0; index < rawValues.Count; index++)
        {
            if (rawValues[index] is not int value)
            {
                failure = RuntimeCollectionReadFailure.ElementTypeMismatch;
                return false;
            }

            result[index] = value;
        }

        values = result;
        failure = RuntimeCollectionReadFailure.None;
        return true;
    }

    private static bool TryReadArrayCore(
        object? array,
        RuntimeArrayKind expectedKind,
        out IReadOnlyList<object?> values,
        out RuntimeCollectionReadFailure failure)
    {
        values = Array.Empty<object?>();
        if (array == null)
        {
            failure = RuntimeCollectionReadFailure.Missing;
            return false;
        }

        if (array is Array managedArray)
        {
            if (!IsSupportedManagedArray(managedArray, expectedKind))
            {
                failure = RuntimeCollectionReadFailure.UnsupportedShape;
                return false;
            }

            if (!IsSaneCount(managedArray.Length))
            {
                failure = RuntimeCollectionReadFailure.CountMismatch;
                return false;
            }

            var result = new object?[managedArray.Length];
            try
            {
                for (var index = 0; index < managedArray.Length; index++)
                {
                    result[index] = managedArray.GetValue(index);
                }
            }
            catch
            {
                failure = RuntimeCollectionReadFailure.InvocationFailed;
                return false;
            }

            values = result;
            failure = RuntimeCollectionReadFailure.None;
            return true;
        }

        if (!TryResolveArrayShape(array.GetType(), out var shape) || shape.Kind != expectedKind)
        {
            failure = RuntimeCollectionReadFailure.UnsupportedShape;
            return false;
        }

        return TryReadIndexedCollection(array, shape.Length, shape.Indexer, out values, out failure);
    }

    private static bool TryReadIndexedCollection(
        object collection,
        PropertyInfo countProperty,
        MethodInfo indexer,
        out IReadOnlyList<object?> values,
        out RuntimeCollectionReadFailure failure)
    {
        values = Array.Empty<object?>();
        if (!TryReadIntProperty(collection, countProperty, out var initialCount))
        {
            failure = RuntimeCollectionReadFailure.InvocationFailed;
            return false;
        }

        if (!IsSaneCount(initialCount))
        {
            failure = RuntimeCollectionReadFailure.CountMismatch;
            return false;
        }

        var result = new object?[initialCount];
        for (var index = 0; index < initialCount; index++)
        {
            if (!TryInvoke(indexer, collection, new object?[] { index }, out result[index]))
            {
                failure = RuntimeCollectionReadFailure.InvocationFailed;
                return false;
            }
        }

        if (!TryReadIntProperty(collection, countProperty, out var finalCount))
        {
            failure = RuntimeCollectionReadFailure.InvocationFailed;
            return false;
        }

        if (finalCount != initialCount)
        {
            failure = RuntimeCollectionReadFailure.CountMismatch;
            return false;
        }

        values = result;
        failure = RuntimeCollectionReadFailure.None;
        return true;
    }

    private static bool TryResolveDictionaryShape(Type type, out DictionaryShape shape)
    {
        if (DictionaryShapes.TryGetValue(type, out shape!)) return true;
        if (!TryBuildDictionaryShape(type, out shape)) return false;
        shape = DictionaryShapes.GetOrAdd(type, shape);
        return true;
    }

    private static bool TryResolveDictionaryLookupShape(Type type, out DictionaryLookupShape shape)
    {
        if (DictionaryLookupShapes.TryGetValue(type, out shape!)) return true;
        if (!TryBuildDictionaryLookupShape(type, out shape)) return false;
        shape = DictionaryLookupShapes.GetOrAdd(type, shape);
        return true;
    }

    private static bool TryBuildDictionaryLookupShape(Type type, out DictionaryLookupShape shape)
    {
        shape = null!;
        if (!TryGetClosedGenericDefinition(type, out var definitionName, out var arguments)
            || arguments.Length != 2
            || (definitionName != ManagedDictionaryTypeName && definitionName != Il2CppDictionaryTypeName)
            || !TryResolveDictionaryHeader(type, out var count))
        {
            return false;
        }

        var containsKey = FindMethod(type, "ContainsKey", new[] { arguments[0] }, typeof(bool));
        var indexer = FindMethod(type, "get_Item", new[] { arguments[0] }, arguments[1]);
        if (containsKey == null || indexer == null) return false;

        shape = new DictionaryLookupShape(count, arguments[0], containsKey, indexer);
        return true;
    }

    private static bool TryBuildDictionaryShape(Type type, out DictionaryShape shape)
    {
        shape = null!;
        if (!TryGetClosedGenericDefinition(type, out var definitionName, out var arguments)
            || !TryResolveDictionaryHeader(type, out var count))
        {
            return false;
        }

        var getEnumerator = FindMethod(type, "GetEnumerator", Type.EmptyTypes);
        if (getEnumerator == null) return false;

        var enumeratorType = getEnumerator.ReturnType;
        if (!IsClosedGenericType(
                enumeratorType,
                $"{definitionName}+Enumerator",
                arguments))
        {
            return false;
        }

        var moveNext = FindMethod(enumeratorType, "MoveNext", Type.EmptyTypes, typeof(bool));
        var dispose = FindMethod(enumeratorType, "Dispose", Type.EmptyTypes, typeof(void));
        var keyValuePairName = definitionName == ManagedDictionaryTypeName
            ? "System.Collections.Generic.KeyValuePair`2"
            : "Il2CppSystem.Collections.Generic.KeyValuePair`2";
        var current = FindProperty(enumeratorType, "Current");
        if (moveNext == null
            || dispose == null
            || current == null
            || !IsClosedGenericType(current.PropertyType, keyValuePairName, arguments))
        {
            return false;
        }

        var key = FindProperty(current.PropertyType, "Key", arguments[0]);
        var value = FindProperty(current.PropertyType, "Value", arguments[1]);
        if (key == null || value == null) return false;

        shape = new DictionaryShape(
            count,
            getEnumerator,
            enumeratorType,
            moveNext,
            current,
            current.PropertyType,
            key,
            value,
            dispose);
        return true;
    }

    private static bool TryResolveDictionaryHeader(Type type, out PropertyInfo count)
    {
        count = null!;
        if (!TryGetClosedGenericDefinition(type, out var definitionName, out var arguments)
            || arguments.Length != 2
            || (definitionName != ManagedDictionaryTypeName && definitionName != Il2CppDictionaryTypeName))
        {
            return false;
        }

        count = FindProperty(type, "Count", typeof(int))!;
        return count != null;
    }

    private static bool TryResolveArrayShape(Type type, out ArrayShape shape)
    {
        if (ArrayShapes.TryGetValue(type, out shape!)) return true;
        if (!TryBuildArrayShape(type, out shape)) return false;
        shape = ArrayShapes.GetOrAdd(type, shape);
        return true;
    }

    private static bool TryBuildArrayShape(Type type, out ArrayShape shape)
    {
        shape = null!;
        RuntimeArrayKind kind;
        Type elementType;
        if (TryGetClosedGenericDefinition(type, out var definitionName, out var arguments)
                 && arguments.Length == 1
                 && definitionName == Il2CppReferenceArrayTypeName)
        {
            kind = RuntimeArrayKind.Reference;
            elementType = arguments[0];
        }
        else if (TryGetClosedGenericDefinition(type, out definitionName, out arguments)
                 && arguments.Length == 1
                 && definitionName == Il2CppStructArrayTypeName)
        {
            kind = RuntimeArrayKind.Struct;
            elementType = arguments[0];
        }
        else
        {
            return false;
        }

        var length = FindProperty(type, "Length", typeof(int));
        var indexer = FindMethod(type, "get_Item", new[] { typeof(int) }, elementType);
        if (length == null || indexer == null) return false;

        shape = new ArrayShape(kind, elementType, length, indexer);
        return true;
    }

    private static bool HasExactArrayElementType(object? array, Type expectedElementType)
    {
        if (array is Array managedArray)
        {
            return managedArray.Rank == 1
                && managedArray.GetLowerBound(0) == 0
                && managedArray.GetType().GetElementType() == expectedElementType;
        }

        return array != null
            && TryResolveArrayShape(array.GetType(), out var shape)
            && shape.Kind == RuntimeArrayKind.Struct
            && shape.ElementType == expectedElementType;
    }

    private static bool TryGetClosedGenericDefinition(
        Type type,
        out string definitionName,
        out Type[] arguments)
    {
        definitionName = "";
        arguments = Array.Empty<Type>();
        if (!type.IsGenericType || type.ContainsGenericParameters) return false;

        definitionName = type.GetGenericTypeDefinition().FullName ?? "";
        arguments = type.GetGenericArguments();
        return !string.IsNullOrWhiteSpace(definitionName);
    }

    private static bool IsClosedGenericType(Type type, string definitionName, IReadOnlyList<Type> arguments)
    {
        if (!TryGetClosedGenericDefinition(type, out var actualName, out var actualArguments)
            || !string.Equals(actualName, definitionName, StringComparison.Ordinal)
            || actualArguments.Length != arguments.Count)
        {
            return false;
        }

        for (var index = 0; index < arguments.Count; index++)
        {
            if (actualArguments[index] != arguments[index]) return false;
        }

        return true;
    }

    private static bool IsSupportedManagedArray(Array array, RuntimeArrayKind expectedKind)
    {
        if (array.Rank != 1 || array.GetLowerBound(0) != 0) return false;
        var elementType = array.GetType().GetElementType();
        if (elementType == null) return false;

        return expectedKind switch
        {
            RuntimeArrayKind.Reference => elementType != typeof(string)
                && (!elementType.IsValueType || !IsBlittableValueType(elementType, new HashSet<Type>())),
            RuntimeArrayKind.Struct => elementType.IsValueType
                && IsBlittableValueType(elementType, new HashSet<Type>()),
            _ => false,
        };
    }

    private static bool IsBlittableValueType(Type type, HashSet<Type> visiting)
    {
        if (type.IsEnum) return IsBlittableValueType(Enum.GetUnderlyingType(type), visiting);
        if (type.IsPrimitive || type.IsPointer || type == typeof(IntPtr) || type == typeof(UIntPtr)) return true;
        if (!type.IsValueType || !visiting.Add(type)) return false;

        try
        {
            return type
                .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .All(field => IsBlittableValueType(field.FieldType, visiting));
        }
        finally
        {
            visiting.Remove(type);
        }
    }

    private static PropertyInfo? FindProperty(Type type, string name, Type? propertyType = null)
    {
        try
        {
            var property = type.GetProperty(name, InstanceFlags);
            if (property == null
                || property.GetMethod == null
                || property.GetIndexParameters().Length != 0
                || (propertyType != null && property.PropertyType != propertyType))
            {
                return null;
            }

            return property;
        }
        catch
        {
            return null;
        }
    }

    private static MethodInfo? FindMethod(
        Type type,
        string name,
        IReadOnlyList<Type> parameterTypes,
        Type? returnType = null)
    {
        var methods = type
            .GetMethods(InstanceFlags)
            .Where(candidate => candidate.Name == name
                && (returnType == null || candidate.ReturnType == returnType))
            .Where(candidate =>
            {
                var parameters = candidate.GetParameters();
                if (parameters.Length != parameterTypes.Count) return false;
                for (var index = 0; index < parameters.Length; index++)
                {
                    if (parameters[index].ParameterType != parameterTypes[index]) return false;
                }

                return true;
            })
            .ToList();
        return methods.Count == 1 ? methods[0] : null;
    }

    private static bool TryReadIntProperty(object instance, PropertyInfo property, out int value)
    {
        value = -1;
        if (!TryReadProperty(instance, property, out var rawValue) || rawValue is not int intValue) return false;
        value = intValue;
        return true;
    }

    private static bool TryReadProperty(object instance, PropertyInfo property, out object? value)
    {
        value = null;
        try
        {
            value = property.GetValue(instance);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryInvoke(
        MethodInfo method,
        object instance,
        object?[] arguments,
        out object? result)
    {
        result = null;
        try
        {
            result = method.Invoke(instance, arguments);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryInvokeBool(MethodInfo method, object instance, out bool value)
    {
        value = false;
        if (!TryInvoke(method, instance, Array.Empty<object?>(), out var result) || result is not bool boolValue)
        {
            return false;
        }

        value = boolValue;
        return true;
    }

    private static bool IsSaneCount(int count)
    {
        return count >= 0 && count <= MaxCollectionCount;
    }

    private enum RuntimeArrayKind
    {
        Reference,
        Struct,
    }

    private sealed record DictionaryShape(
        PropertyInfo Count,
        MethodInfo GetEnumerator,
        Type EnumeratorType,
        MethodInfo MoveNext,
        PropertyInfo Current,
        Type KeyValuePairType,
        PropertyInfo Key,
        PropertyInfo Value,
        MethodInfo Dispose);

    private sealed record DictionaryLookupShape(
        PropertyInfo Count,
        Type KeyType,
        MethodInfo ContainsKey,
        MethodInfo Indexer);

    private sealed record ArrayShape(
        RuntimeArrayKind Kind,
        Type ElementType,
        PropertyInfo Length,
        MethodInfo Indexer);
}
