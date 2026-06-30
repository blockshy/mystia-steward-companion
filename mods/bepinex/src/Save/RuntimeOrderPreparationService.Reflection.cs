using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MystiaStewardCompanion.LocalApi;

namespace MystiaStewardCompanion.Save;

internal static partial class RuntimeOrderPreparationService
{
    /// <summary>
    /// 安全调用实例方法，失败时返回 <c>null</c>。
    /// </summary>
    /// <remarks>
    /// 用于可选 getter 或版本差异字段读取，不应承载必须成功的业务动作。
    /// </remarks>
    private static object? TryInvokeInstanceValue(object target, string methodName)
    {
        return TryInvokeInstanceValue(target, methodName, Array.Empty<object?>());
    }

    private static object? TryInvokeInstanceValue(object target, string methodName, object?[] args)
    {
        try
        {
            return InvokeInstance(target, methodName, args);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 读取 IL2CPP/Unity 对象的稳定指针。
    /// </summary>
    /// <remarks>
    /// 指针用于运行时对象去重和短期回执匹配；无法读取原生指针时退回托管对象 hash，保证诊断和缓存仍可工作。
    /// </remarks>
    private static nint ReadObjectPointer(object target)
    {
        var pointer = ReadMember(target, "Pointer") ?? ReadMember(target, "NativePointer") ?? ReadMember(target, "m_CachedPtr");
        if (pointer is IntPtr intPtr) return intPtr;
        if (pointer is nint native) return native;
        if (pointer is IConvertible convertible) return new IntPtr(convertible.ToInt64(null));
        return new IntPtr(RuntimeHelpers.GetHashCode(target));
    }

    /// <summary>
    /// 读取对象字段或属性，兼容 IL2CPP backing field 与常见私有字段命名。
    /// </summary>
    /// <remarks>
    /// 先走通用 RuntimeReflectionUtility，再走本地精确字段扫描，避免多个模块重复维护反射候选名称。
    /// </remarks>
    private static object? ReadMember(object target, string name)
    {
        try
        {
            var utilityValue = RuntimeReflectionUtility.GetMemberValue(target, name);
            if (utilityValue != null) return utilityValue;
        }
        catch
        {
            // 通用工具失败时继续使用本地精确字段读取，反射适配层不因单个字段异常中断流程。
        }

        for (var type = target.GetType(); type != null; type = type.BaseType)
        {
            foreach (var fieldName in BuildFieldNameCandidates(name))
            {
                var field = type.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (field != null) return field.GetValue(target);
            }

            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (property != null) return property.GetValue(target);

            var pascalName = char.ToUpperInvariant(name[0]) + name[1..];
            if (!string.Equals(pascalName, name, StringComparison.Ordinal))
            {
                property = type.GetProperty(pascalName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (property != null) return property.GetValue(target);
            }
        }

        return null;
    }

    /// <summary>
    /// 写入对象字段或属性。
    /// </summary>
    /// <remarks>
    /// 仅用于订单字段、厨具状态等运行时自动化入口。调用方必须先确认写入符合游戏当前生命周期。
    /// </remarks>
    private static bool WriteMember(object target, string name, object? value)
    {
        for (var type = target.GetType(); type != null; type = type.BaseType)
        {
            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (property?.SetMethod != null)
            {
                property.SetValue(target, value);
                return true;
            }

            foreach (var fieldName in BuildFieldNameCandidates(name))
            {
                var field = type.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (field == null) continue;

                field.SetValue(target, value);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 构建一个 C# 属性在 IL2CPP/反编译环境下可能出现的字段名候选。
    /// </summary>
    private static IEnumerable<string> BuildFieldNameCandidates(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) yield break;

        yield return name;
        yield return $"<{name}>k__BackingField";
        yield return $"m_{name}";
        yield return $"_{name}";

        var camelName = char.ToLowerInvariant(name[0]) + name[1..];
        if (!string.Equals(camelName, name, StringComparison.Ordinal))
        {
            yield return camelName;
            yield return $"<{camelName}>k__BackingField";
            yield return $"m_{camelName}";
            yield return $"_{camelName}";
        }
    }

    private static IEnumerable<int> ReadIntEnumerable(object? value)
    {
        if (value == null) yield break;
        if (value is string) yield break;
        foreach (var item in EnumerateManaged(value).Concat(EnumerateByIndexer(value)))
        {
            yield return ToInt(item);
        }
    }

    /// <summary>
    /// 将托管集合、IL2CPP 索引集合或字典值统一枚举为对象序列。
    /// </summary>
    /// <remarks>
    /// 游戏运行时容器形态不稳定，枚举时通过对象指针去重，并对每条路径设置数量上限，避免错误容器导致长时间卡顿。
    /// </remarks>
    private static IEnumerable<object> ReadObjectEnumerable(object? value)
    {
        if (value == null || value is string) yield break;

        var seen = new HashSet<nint>();
        foreach (var item in EnumerateManaged(value).Concat(EnumerateByIndexer(value)).Concat(ReadDictionaryValues(value)))
        {
            if (item == null) continue;
            if (!TryRememberObject(item, seen)) continue;
            yield return item;
        }
    }

    /// <summary>
    /// 枚举 IL2CPP 字典或托管字典的值。
    /// </summary>
    /// <remarks>
    /// 反射读取字典内部 entries 时只接受 hashCode 非负的有效槽位，跳过空槽和删除槽。
    /// </remarks>
    private static IEnumerable<object?> ReadDictionaryValues(object? dictionary)
    {
        if (dictionary == null || dictionary is string) yield break;

        if (dictionary is IDictionary managedDictionary)
        {
            foreach (DictionaryEntry entry in managedDictionary)
            {
                yield return entry.Value;
            }

            yield break;
        }

        var entries = ReadMember(dictionary, "entries")
            ?? ReadMember(dictionary, "_entries")
            ?? ReadMember(dictionary, "m_Entries");
        var count = ToInt(ReadMember(dictionary, "count")
            ?? ReadMember(dictionary, "_count")
            ?? ReadMember(dictionary, "Count"));
        if (entries == null || count <= 0) yield break;

        var entryIndex = 0;
        foreach (var entry in EnumerateByIndexer(entries))
        {
            if (entryIndex++ >= Math.Min(count, 256)) yield break;
            if (entry == null) continue;

            var hashCode = ToInt(ReadMember(entry, "hashCode") ?? ReadMember(entry, "_hashCode"));
            if (hashCode < 0) continue;

            var value = ReadMember(entry, "value")
                ?? ReadMember(entry, "Value")
                ?? ReadMember(entry, "_value");
            if (value != null) yield return value;
        }
    }

    private static IEnumerable<object?> EnumerateManaged(object value)
    {
        if (LooksLikeIl2CppObject(value)) yield break;
        if (value is not IEnumerable enumerable) yield break;

        foreach (var item in enumerable)
        {
            yield return item;
        }
    }

    private static IEnumerable<object?> EnumerateByIndexer(object value)
    {
        var count = ToInt(TryInvokeInstanceValue(value, "get_Count")
            ?? ReadMember(value, "Count")
            ?? ReadMember(value, "Length")
            ?? ReadMember(value, "_size"));
        if (count <= 0) yield break;

        for (var index = 0; index < Math.Min(count, 256); index++)
        {
            yield return TryInvokeInstanceValue(value, "get_Item", new object?[] { index });
        }
    }

    private static IEnumerable<object?> FindUnityObjects(Type type)
    {
        var method = typeof(UnityEngine.Object).GetMethod("FindObjectsOfType", new[] { typeof(Type) });
        if (method == null) yield break;

        object? objects;
        try
        {
            objects = method.Invoke(null, new object[] { type });
        }
        catch
        {
            yield break;
        }

        foreach (var item in ReadObjectEnumerable(objects))
        {
            yield return item;
        }
    }

    /// <summary>
    /// 判断对象是否更适合按 IL2CPP 对象处理，而不是直接作为托管 IEnumerable 展开。
    /// </summary>
    /// <remarks>
    /// 很多 IL2CPP 游戏对象实现了运行时接口，但直接枚举会触发无关逻辑或异常；这类对象交给索引器/字段路径读取。
    /// </remarks>
    private static bool LooksLikeIl2CppObject(object value)
    {
        var type = value.GetType();
        var fullName = type.FullName ?? "";
        if (fullName.StartsWith("Il2Cpp", StringComparison.Ordinal)) return true;
        if (fullName.StartsWith("NightScene.", StringComparison.Ordinal)) return true;
        if (fullName.StartsWith("DayScene.", StringComparison.Ordinal)) return true;
        if (fullName.StartsWith("GameData.", StringComparison.Ordinal)) return true;
        if (fullName.StartsWith("DEYU.", StringComparison.Ordinal)) return true;
        return type.Assembly.GetName().Name?.Contains("Il2Cpp", StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>
    /// 获取游戏单例或场景中的 Unity 对象实例。
    /// </summary>
    /// <exception cref="InvalidOperationException">目标类型尚未加载时抛出。</exception>
    private static object? GetSingletonInstance(string typeName)
    {
        var type = FindType(typeName)
            ?? throw new InvalidOperationException($"{typeName} type is not loaded.");
        return RuntimeReflectionUtility.GetSingletonInstance(type)
            ?? RuntimeReflectionUtility.FindUnityObject(type);
    }

    /// <summary>
    /// 调用游戏静态方法。
    /// </summary>
    /// <exception cref="InvalidOperationException">目标类型尚未加载时抛出。</exception>
    /// <exception cref="MissingMethodException">未找到匹配参数的方法时抛出。</exception>
    private static object? InvokeStatic(string typeName, string methodName, object?[] args)
    {
        var type = FindType(typeName)
            ?? throw new InvalidOperationException($"{typeName} type is not loaded.");
        var utilityValue = RuntimeReflectionUtility.InvokeStaticMethod(type, methodName, args);
        if (utilityValue != null) return utilityValue;

        var method = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .FirstOrDefault(candidate => string.Equals(candidate.Name, methodName, StringComparison.Ordinal)
                && CanUseParameters(candidate.GetParameters(), args))
            ?? throw new MissingMethodException(typeName, methodName);
        return method.Invoke(null, args);
    }

    /// <summary>
    /// 调用游戏实例方法，找不到方法或方法执行失败时向上抛出异常。
    /// </summary>
    private static object? InvokeInstance(object target, string methodName, object?[] args)
    {
        var method = target.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(candidate => string.Equals(candidate.Name, methodName, StringComparison.Ordinal)
                && CanUseParameters(candidate.GetParameters(), args))
            ?? throw new MissingMethodException(target.GetType().FullName, methodName);
        return method.Invoke(target, args);
    }

    /// <summary>
    /// 尝试调用游戏实例方法，失败时返回 <c>false</c>。
    /// </summary>
    /// <remarks>
    /// 用于可选清理、视觉同步或兼容路径，不能用于必须成功的核心步骤。
    /// </remarks>
    private static bool TryInvokeInstance(object target, string methodName, object?[] args)
    {
        var method = target.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(candidate => string.Equals(candidate.Name, methodName, StringComparison.Ordinal)
                && CanUseParameters(candidate.GetParameters(), args));
        if (method == null) return false;

        try
        {
            method.Invoke(target, args);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 在当前 AppDomain 中查找 IL2CPP 类型。
    /// </summary>
    /// <remarks>
    /// 游戏类型加载顺序依赖场景，未找到类型通常代表场景尚未就绪，而不一定是致命错误。
    /// </remarks>
    private static Type? FindType(string fullName)
    {
        var direct = Type.GetType(fullName, false);
        if (direct != null) return direct;

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? type;
            try
            {
                type = assembly.GetType(fullName, false);
            }
            catch
            {
                continue;
            }

            if (type != null) return type;
        }

        return null;
    }

    /// <summary>
    /// 判断一组参数是否可以用于当前反射方法签名。
    /// </summary>
    /// <remarks>
    /// 支持 by-ref 参数和基础数值转换，满足游戏 API 中常见的 out/ref 与枚举数值签名。
    /// </remarks>
    private static bool CanUseParameters(ParameterInfo[] parameters, object?[] args)
    {
        if (parameters.Length != args.Length) return false;
        for (var i = 0; i < parameters.Length; i++)
        {
            var arg = args[i];
            var parameterType = parameters[i].ParameterType;
            if (parameterType.IsByRef)
            {
                parameterType = parameterType.GetElementType() ?? parameterType;
            }

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

    private static int ToInt(object? value)
    {
        if (value == null) return 0;
        if (value is int number) return number;
        if (value is Enum enumValue) return Convert.ToInt32(enumValue);
        if (value is IConvertible convertible) return Convert.ToInt32(convertible);
        return int.TryParse(value.ToString(), out var parsed) ? parsed : 0;
    }

    private static int ToInt(object? value, int fallback)
    {
        if (value == null) return fallback;
        try
        {
            if (value is int number) return number;
            if (value is Enum enumValue) return Convert.ToInt32(enumValue);
            if (value is IConvertible convertible) return Convert.ToInt32(convertible);
            return int.TryParse(value.ToString(), out var parsed) ? parsed : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static bool ReadBool(object? value)
    {
        if (value is bool boolValue) return boolValue;
        if (value is IConvertible convertible) return convertible.ToBoolean(null);
        return bool.TryParse(value?.ToString(), out var parsed) && parsed;
    }
}
