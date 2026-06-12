using System.Collections;
using System.Reflection;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace MystiaStewardCompanion.Save;

public static class RuntimeInventoryEditor
{
    private const string RuntimeStorageTypeName = "GameData.RunTime.Common.RunTimeStorage";
    private const int MaxTargetQuantity = 9999;

    public static RuntimeInventoryEditResult SetQuantity(string itemType, int itemId, int quantity)
    {
        if (itemId < 0) throw new ArgumentOutOfRangeException(nameof(itemId), "Inventory item id must be non-negative.");
        var normalizedType = NormalizeItemType(itemType);
        var targetQuantity = Math.Clamp(quantity, 0, MaxTargetQuantity);
        var currentQuantity = GetQuantity(normalizedType, itemId);

        if (currentQuantity < 0)
        {
            return new RuntimeInventoryEditResult
            {
                ItemType = normalizedType,
                ItemId = itemId,
                RequestedQuantity = targetQuantity,
                PreviousQuantity = currentQuantity,
                Quantity = currentQuantity,
                Changed = false,
                Error = "This inventory item is infinite or not editable.",
            };
        }

        if (currentQuantity == targetQuantity)
        {
            return new RuntimeInventoryEditResult
            {
                ItemType = normalizedType,
                ItemId = itemId,
                RequestedQuantity = targetQuantity,
                PreviousQuantity = currentQuantity,
                Quantity = currentQuantity,
                Changed = false,
            };
        }

        var delta = targetQuantity - currentQuantity;
        try
        {
            ApplyDelta(normalizedType, itemId, delta);
        }
        catch
        {
            SetRawQuantity(normalizedType, itemId, targetQuantity);
        }

        var nextQuantity = GetQuantity(normalizedType, itemId);
        return new RuntimeInventoryEditResult
        {
            ItemType = normalizedType,
            ItemId = itemId,
            RequestedQuantity = targetQuantity,
            PreviousQuantity = currentQuantity,
            Quantity = nextQuantity,
            Changed = nextQuantity != currentQuantity,
        };
    }

    private static string NormalizeItemType(string itemType)
    {
        return itemType.Trim().ToLowerInvariant() switch
        {
            "ingredient" or "ingredients" or "material" or "materials" => "ingredient",
            "beverage" or "beverages" or "drink" or "drinks" => "beverage",
            _ => throw new ArgumentException("Inventory type must be ingredient or beverage.", nameof(itemType)),
        };
    }

    private static int GetQuantity(string itemType, int itemId)
    {
        var methodName = itemType == "ingredient" ? "GetIngredientCountById" : "GetBeverageCountById";
        var value = InvokeStatic(methodName, new object?[] { itemId });
        return ToInt(value);
    }

    private static void ApplyDelta(string itemType, int itemId, int delta)
    {
        if (delta == 0) return;

        var methodName = itemType switch
        {
            "ingredient" when delta > 0 => "IngredientInRange",
            "ingredient" => "IngredientOutRange",
            "beverage" when delta > 0 => "BeverageInRange",
            _ => "BeverageOutRange",
        };

        var method = FindUsableRuntimeStorageRangeMethod(methodName, itemId, Math.Abs(delta))
            ?? throw new MissingMethodException(RuntimeStorageTypeName, methodName);
        InvokeMethod(method.Method, null, method.Args);
    }

    private static RuntimeStorageInvocation? FindUsableRuntimeStorageRangeMethod(string name, int itemId, int count)
    {
        var type = FindType(RuntimeStorageTypeName);
        if (type == null) return null;

        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                     .Where(method => method.Name == name)
                     .OrderBy(method => method.GetParameters().Length))
        {
            var parameters = method.GetParameters();
            if (parameters.Length is < 1 or > 2) continue;

            foreach (var ids in BuildRepeatedIdArgumentCandidates(parameters[0].ParameterType, itemId, count))
            {
                var args = parameters.Length == 1
                    ? new object?[] { ids }
                    : new object?[] { ids, GetDefaultValue(parameters[1].ParameterType) };
                if (CanUseParameters(parameters, args))
                {
                    return new RuntimeStorageInvocation(method, args);
                }
            }
        }

        return null;
    }

    private static IEnumerable<object> BuildRepeatedIdArgumentCandidates(Type parameterType, int itemId, int count)
    {
        if (parameterType.IsArray && parameterType.GetElementType() == typeof(int))
        {
            yield return Enumerable.Repeat(itemId, count).ToArray();
            yield break;
        }

        if (parameterType == typeof(Il2CppStructArray<int>) || parameterType.FullName?.Contains("Il2CppStructArray") == true)
        {
            yield return BuildIl2CppIntArray(itemId, count);
            yield break;
        }

        if (typeof(IEnumerable).IsAssignableFrom(parameterType))
        {
            yield return Enumerable.Repeat(itemId, count).ToArray();
            yield return BuildIl2CppIntArray(itemId, count);
        }
    }

    private static Il2CppStructArray<int> BuildIl2CppIntArray(int itemId, int count)
    {
        var array = new Il2CppStructArray<int>(count);
        for (var i = 0; i < count; i++)
        {
            array[i] = itemId;
        }

        return array;
    }

    private static void SetRawQuantity(string itemType, int itemId, int quantity)
    {
        var propertyName = itemType == "ingredient" ? "Ingredients" : "Beverages";
        var type = FindType(RuntimeStorageTypeName)
            ?? throw new InvalidOperationException("RunTimeStorage type is not loaded.");
        var dictionary = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            ?.GetValue(null);
        if (dictionary == null) throw new InvalidOperationException($"RunTimeStorage.{propertyName} is not available.");

        if (dictionary is IDictionary managedDictionary)
        {
            if (quantity <= 0)
            {
                managedDictionary.Remove(itemId);
            }
            else
            {
                managedDictionary[itemId] = quantity;
            }

            return;
        }

        if (quantity <= 0 && TryInvokeDictionaryMethod(dictionary, "Remove", itemId))
        {
            return;
        }

        if (TryInvokeDictionaryMethod(dictionary, "set_Item", itemId, quantity)) return;
        if (quantity > 0 && TryInvokeDictionaryMethod(dictionary, "Add", itemId, quantity)) return;

        throw new InvalidOperationException($"Cannot write RunTimeStorage.{propertyName}.");
    }

    private static object? InvokeStatic(string methodName, object?[] args)
    {
        var method = FindRuntimeStorageMethod(methodName)
            ?? throw new MissingMethodException(RuntimeStorageTypeName, methodName);
        return InvokeMethod(method, null, args);
    }

    private static MethodInfo? FindRuntimeStorageMethod(string name)
    {
        var type = FindType(RuntimeStorageTypeName);
        return type?.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
    }

    private static bool TryInvokeDictionaryMethod(object dictionary, string methodName, params object?[] args)
    {
        foreach (var method in dictionary.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if (!string.Equals(method.Name, methodName, StringComparison.Ordinal)) continue;
            var parameters = method.GetParameters();
            if (parameters.Length != args.Length) continue;
            if (!CanUseParameters(parameters, args)) continue;

            try
            {
                InvokeMethod(method, dictionary, args);
                return true;
            }
            catch
            {
                // Try the next overload.
            }
        }

        return false;
    }

    private static bool CanUseParameters(IReadOnlyList<ParameterInfo> parameters, IReadOnlyList<object?> args)
    {
        for (var i = 0; i < parameters.Count; i++)
        {
            var arg = args[i];
            var parameterType = parameters[i].ParameterType;
            if (arg == null)
            {
                if (parameterType.IsValueType) return false;
                continue;
            }

            var argType = arg.GetType();
            if (parameterType.IsAssignableFrom(argType)) continue;
            if (parameterType.IsPrimitive && arg is IConvertible) continue;
            return false;
        }

        return true;
    }

    private static object? GetDefaultValue(Type type)
    {
        if (type == typeof(bool)) return false;
        if (type == typeof(int)) return 0;
        return type.IsValueType ? Activator.CreateInstance(type) : null;
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
                // Some IL2CPP interop assemblies can throw while resolving unrelated types.
            }
        }

        return null;
    }

    private static object? InvokeMethod(MethodInfo method, object? instance, object?[] args)
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

    private static int ToInt(object? value)
    {
        if (value == null) return 0;
        if (value is int intValue) return intValue;
        if (value is long longValue) return (int)Math.Clamp(longValue, int.MinValue, int.MaxValue);
        if (value is short shortValue) return shortValue;
        if (value is byte byteValue) return byteValue;

        if (value is IDictionary dictionary && dictionary.Count == 0) return 0;
        return value is IConvertible convertible ? Convert.ToInt32(convertible) : 0;
    }
}

internal sealed class RuntimeStorageInvocation
{
    public RuntimeStorageInvocation(MethodInfo method, object?[] args)
    {
        Method = method;
        Args = args;
    }

    public MethodInfo Method { get; }
    public object?[] Args { get; }
}

public sealed class RuntimeInventoryEditResult
{
    public string ItemType { get; init; } = "";
    public int ItemId { get; init; }
    public int RequestedQuantity { get; init; }
    public int PreviousQuantity { get; init; }
    public int Quantity { get; init; }
    public bool Changed { get; init; }
    public string? Error { get; init; }
}
