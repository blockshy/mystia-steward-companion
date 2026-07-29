using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;

namespace MystiaStewardCompanion.Save;

/// <summary>
/// Passively identifies the typed RunTimeStorage exit that forwards the native -1 sentinel.
/// </summary>
internal static class RuntimeStorageSentinelDiagnostic
{
    private const string HarmonyId = "com.tyukki.mystia-steward-companion.runtime-storage-sentinel-diagnostic";
    private const string RunTimeStorageTypeName = "GameData.RunTime.Common.RunTimeStorage";
    private const string Il2CppEnumerableTypeName = "Il2CppSystem.Collections.Generic.IEnumerable`1";
    private const string Il2CppDictionaryTypeName = "Il2CppSystem.Collections.Generic.Dictionary`2";
    private const string Il2CppPredicateTypeName = "Il2CppSystem.Predicate`1";
    private const string Il2CppStructArrayTypeName = "Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray`1";
    private const int ObservedSentinelId = -1;
    private const int MaxDetailedOccurrencesPerKey = 4;
    private const int MaxLoggedEventsPerGeneration = 24;

    private static readonly object SyncRoot = new();
    private static readonly StorageOutDescriptor[] WrapperDescriptors =
    {
        new("BadgeOut", "Badge", StorageOutParameterShape.Single),
        new("BadgeOutRange", "Badge", StorageOutParameterShape.RangeEnumerable),
        new("BeverageOut", "Beverage", StorageOutParameterShape.Single),
        new("BeverageOutRange", "Beverage", StorageOutParameterShape.RangeEnumerable),
        new("CookerOut", "Cooker", StorageOutParameterShape.Single),
        new("CookerOutRange", "Cooker", StorageOutParameterShape.RangeStructArray),
        new("FoodOut", "Food", StorageOutParameterShape.Single),
        new("FoodOutRange", "Food", StorageOutParameterShape.RangeEnumerable),
        new("IngredientOut", "Ingredient", StorageOutParameterShape.Single),
        new("IngredientOutRange", "Ingredient", StorageOutParameterShape.RangeEnumerable),
        new("ItemOut", "Item", StorageOutParameterShape.Single),
        new("ItemOutRange", "Item", StorageOutParameterShape.RangeEnumerable),
    };

    [ThreadStatic]
    private static List<StorageOutContextToken>? _contextStack;

    private static readonly Dictionary<StorageSentinelDiagnosticKey, int> Occurrences = new();
    private static Harmony? _harmony;
    private static ManualLogSource? _log;
    private static bool _patched;
    private static string _status = "not attached";
    private static long _nextContextToken;
    private static long _diagnosticGeneration = long.MinValue;
    private static int _loggedEventsInGeneration;
    private static bool _generationLimitReported;

    public static string Status
    {
        get
        {
            lock (SyncRoot) return _status;
        }
    }

    public static void Attach(ManualLogSource log)
    {
        lock (SyncRoot)
        {
            _log = log;
            if (_patched) return;
        }

        try
        {
            var storageType = RuntimeReflectionUtility.FindType(RunTimeStorageTypeName)
                ?? throw new MissingMemberException(RunTimeStorageTypeName, "type");
            var methods = storageType.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            var wrapperTargets = WrapperDescriptors
                .Select(descriptor => ResolveWrapperMethod(methods, descriptor))
                .ToArray();
            var objectOut = methods.SingleOrDefault(IsExactObjectOutMethod)
                ?? throw new MissingMethodException(
                    RunTimeStorageTypeName,
                    "ObjectOut(Dictionary<int,int>, int, bool, Predicate<int>)");
            var wrapperPrefix = RequireHook(nameof(OnWrapperPrefix));
            var wrapperFinalizer = RequireHook(nameof(OnWrapperFinalizer));
            var objectOutPrefix = RequireHook(nameof(OnObjectOutPrefix));

            var harmony = _harmony ??= new Harmony(HarmonyId);
            foreach (var wrapperTarget in wrapperTargets)
            {
                harmony.Patch(
                    wrapperTarget,
                    prefix: new HarmonyMethod(wrapperPrefix) { priority = Priority.First },
                    finalizer: new HarmonyMethod(wrapperFinalizer) { priority = Priority.Last });
            }

            harmony.Patch(
                objectOut,
                prefix: new HarmonyMethod(objectOutPrefix) { priority = Priority.First });
            lock (SyncRoot)
            {
                _patched = true;
                _status = $"patched={wrapperTargets.Length + 1}";
            }

            log.LogInfo(
                "Runtime storage sentinel diagnostic patched: "
                + "12 typed RunTimeStorage out wrappers and ObjectOut.");
        }
        catch (Exception ex)
        {
            try
            {
                _harmony?.UnpatchSelf();
            }
            catch
            {
                // The observation service remains unavailable; preserve the original failure.
            }

            lock (SyncRoot)
            {
                _patched = false;
                _status = $"unavailable: {ex.GetBaseException().Message}";
            }

            log.LogWarning(
                $"Runtime storage sentinel diagnostic unavailable: {ex.GetBaseException().Message}");
        }
    }

    private static MethodInfo ResolveWrapperMethod(
        IReadOnlyList<MethodInfo> methods,
        StorageOutDescriptor descriptor)
    {
        return methods.SingleOrDefault(method => IsExactWrapperMethod(method, descriptor))
            ?? throw new MissingMethodException(
                RunTimeStorageTypeName,
                $"{descriptor.MethodName}({FormatParameterShape(descriptor.ParameterShape)}, bool)");
    }

    private static bool IsExactWrapperMethod(MethodInfo method, StorageOutDescriptor descriptor)
    {
        if (!method.IsPublic
            || !method.IsStatic
            || method.ReturnType != typeof(void)
            || !string.Equals(method.Name, descriptor.MethodName, StringComparison.Ordinal))
        {
            return false;
        }

        var parameters = method.GetParameters();
        if (parameters.Length != 2 || parameters[1].ParameterType != typeof(bool)) return false;
        return descriptor.ParameterShape switch
        {
            StorageOutParameterShape.Single => parameters[0].ParameterType == typeof(int),
            StorageOutParameterShape.RangeEnumerable => IsClosedGenericType(
                parameters[0].ParameterType,
                Il2CppEnumerableTypeName,
                typeof(int)),
            StorageOutParameterShape.RangeStructArray => IsClosedGenericType(
                parameters[0].ParameterType,
                Il2CppStructArrayTypeName,
                typeof(int)),
            _ => false,
        };
    }

    private static bool IsExactObjectOutMethod(MethodInfo method)
    {
        if (!method.IsPublic
            || !method.IsStatic
            || method.ReturnType != typeof(void)
            || !string.Equals(method.Name, "ObjectOut", StringComparison.Ordinal))
        {
            return false;
        }

        var parameters = method.GetParameters();
        return parameters.Length == 4
            && IsClosedGenericType(
                parameters[0].ParameterType,
                Il2CppDictionaryTypeName,
                typeof(int),
                typeof(int))
            && parameters[1].ParameterType == typeof(int)
            && parameters[2].ParameterType == typeof(bool)
            && IsClosedGenericType(
                parameters[3].ParameterType,
                Il2CppPredicateTypeName,
                typeof(int));
    }

    private static bool IsClosedGenericType(
        Type type,
        string genericTypeName,
        params Type[] genericArguments)
    {
        return type.IsGenericType
            && string.Equals(
                type.GetGenericTypeDefinition().FullName,
                genericTypeName,
                StringComparison.Ordinal)
            && type.GetGenericArguments().SequenceEqual(genericArguments);
    }

    private static MethodInfo RequireHook(string methodName)
    {
        return typeof(RuntimeStorageSentinelDiagnostic).GetMethod(
                methodName,
                BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(
                typeof(RuntimeStorageSentinelDiagnostic).FullName,
                methodName);
    }

    private static void OnWrapperPrefix(MethodBase __originalMethod, out StorageOutContextToken __state)
    {
        try
        {
            __state = PushContext(__originalMethod.Name);
        }
        catch
        {
            __state = default;
        }
    }

    private static Exception? OnWrapperFinalizer(
        Exception? __exception,
        StorageOutContextToken __state)
    {
        try
        {
            PopContext(__state);
        }
        catch
        {
            // Observation cleanup must not replace the native exception.
        }

        return __exception;
    }

    private static void OnObjectOutPrefix(int __1, bool __2)
    {
        try
        {
            ObserveObjectOut(__1, __2);
        }
        catch
        {
            // Observation must never affect native storage behavior.
        }
    }

    private static StorageOutContextToken PushContext(string methodName)
    {
        var descriptor = WrapperDescriptors.Single(item =>
            string.Equals(item.MethodName, methodName, StringComparison.Ordinal));
        var token = new StorageOutContextToken(
            Interlocked.Increment(ref _nextContextToken),
            descriptor.MethodName,
            descriptor.Category);
        (_contextStack ??= new List<StorageOutContextToken>()).Add(token);
        return token;
    }

    private static void PopContext(StorageOutContextToken token)
    {
        if (token.Token <= 0 || _contextStack == null || _contextStack.Count == 0) return;

        for (var index = _contextStack.Count - 1; index >= 0; index--)
        {
            if (_contextStack[index].Token != token.Token) continue;
            _contextStack.RemoveAt(index);
            break;
        }
    }

    private static void ObserveObjectOut(int objectId, bool suppressCallbacks)
    {
        if (objectId != ObservedSentinelId || !AggregateModLogService.Enabled) return;

        var lifecycle = RuntimeNightBusinessLifecycle.Snapshot;
        if (!lifecycle.IsActive || lifecycle.Generation <= 0) return;

        var context = _contextStack is { Count: > 0 }
            ? _contextStack[^1]
            : new StorageOutContextToken(0, "unscoped", "Unknown");
        var key = new StorageSentinelDiagnosticKey(
            context.Entry,
            context.Category,
            objectId,
            suppressCallbacks);
        string? title = null;
        string? content = null;
        lock (SyncRoot)
        {
            if (_diagnosticGeneration != lifecycle.Generation)
            {
                _diagnosticGeneration = lifecycle.Generation;
                _loggedEventsInGeneration = 0;
                _generationLimitReported = false;
                Occurrences.Clear();
            }

            var occurrence = Occurrences.TryGetValue(key, out var previous)
                ? previous + 1
                : 1;
            Occurrences[key] = occurrence;
            if (_loggedEventsInGeneration >= MaxLoggedEventsPerGeneration)
            {
                if (_generationLimitReported) return;
                _generationLimitReported = true;
                title = "Runtime storage sentinel observation limit reached";
                content = $"event=storage-out-negative-id-suppressed; "
                    + $"generation={lifecycle.Generation}; "
                    + $"limit={MaxLoggedEventsPerGeneration}; "
                    + $"thread={Environment.CurrentManagedThreadId}";
            }
            else if (occurrence <= MaxDetailedOccurrencesPerKey)
            {
                _loggedEventsInGeneration++;
                title = "Runtime storage sentinel observed";
                content = $"event=storage-out-negative-id; "
                    + $"generation={lifecycle.Generation}; "
                    + $"phase={lifecycle.Phase}; "
                    + $"entry={context.Entry}; "
                    + $"category={context.Category}; "
                    + $"id={objectId}; "
                    + $"suppressCallbacks={suppressCallbacks}; "
                    + $"occurrence={occurrence}; "
                    + $"thread={Environment.CurrentManagedThreadId}";
            }
            else if (occurrence == MaxDetailedOccurrencesPerKey + 1)
            {
                _loggedEventsInGeneration++;
                title = "Runtime storage sentinel duplicates suppressed";
                content = $"event=storage-out-negative-id-key-suppressed; "
                    + $"generation={lifecycle.Generation}; "
                    + $"entry={context.Entry}; "
                    + $"category={context.Category}; "
                    + $"id={objectId}; "
                    + $"suppressCallbacks={suppressCallbacks}; "
                    + $"detailedLimit={MaxDetailedOccurrencesPerKey}; "
                    + $"thread={Environment.CurrentManagedThreadId}";
            }
        }

        if (title != null && content != null)
        {
            AggregateModLogService.AppendSection("runtime-storage", title, content);
        }
    }

    private static string FormatParameterShape(StorageOutParameterShape shape)
    {
        return shape switch
        {
            StorageOutParameterShape.Single => "int",
            StorageOutParameterShape.RangeEnumerable => "IEnumerable<int>",
            StorageOutParameterShape.RangeStructArray => "Il2CppStructArray<int>",
            _ => "unknown",
        };
    }

    private enum StorageOutParameterShape
    {
        Single,
        RangeEnumerable,
        RangeStructArray,
    }

    private readonly record struct StorageOutDescriptor(
        string MethodName,
        string Category,
        StorageOutParameterShape ParameterShape);

    private readonly record struct StorageOutContextToken(
        long Token,
        string Entry,
        string Category);

    private readonly record struct StorageSentinelDiagnosticKey(
        string Entry,
        string Category,
        int ObjectId,
        bool SuppressCallbacks);
}
