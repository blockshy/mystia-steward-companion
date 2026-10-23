using System.Reflection;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace MystiaStewardCompanion.Save;

public static class RuntimeInventoryEditor
{
    private const string RuntimeStorageTypeName = "GameData.RunTime.Common.RunTimeStorage";

    public static RuntimeInventoryEditResult SetQuantity(string itemType, int itemId, int quantity,
        int? expectedQuantity = null)
    {
        if (itemId < 0) throw new ArgumentOutOfRangeException(nameof(itemId), "Inventory item id must be non-negative.");
        var normalizedType = NormalizeItemType(itemType);
        // 读、写和回读仍全部在原有主线程许可内同步执行。纯托管规则单独抽出，
        // 使无限库存/未知读取/部分执行等路径可在不加载游戏或 IL2CPP 的环境回归。
        return RuntimeInventoryMutation.SetQuantity(normalizedType, itemId, quantity,
            () => GetQuantity(normalizedType, itemId),
            delta => ApplyDelta(normalizedType, itemId, delta), expectedQuantity);
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
        return RuntimeInventoryMutation.ReadQuantity(value);
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
        if (parameterType == typeof(Il2CppSystem.Collections.Generic.IEnumerable<int>))
        {
            yield return BuildIl2CppIntArray(itemId, count)
                .Cast<Il2CppSystem.Collections.Generic.IEnumerable<int>>();
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
