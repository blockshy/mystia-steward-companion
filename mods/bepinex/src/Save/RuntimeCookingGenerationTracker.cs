using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;

namespace MystiaStewardCompanion.Save;

internal enum RuntimeCookingContentMutation
{
    SetCook,
    Extract,
    Store,
}

internal readonly record struct RuntimeCookingOwnershipSnapshot(
    long Generation,
    long ContentRevision,
    RuntimeCookingContentMutation LastMutation,
    bool MutationCompleted);

internal readonly record struct RuntimeCookingMutationToken(
    nint ControllerPointer,
    long ContentRevision,
    RuntimeCookingContentMutation Mutation);

/// <summary>
/// Tracks the exact native cooking generation and controller-content revision.
/// </summary>
internal static class RuntimeCookingGenerationTracker
{
    private const string CookControllerTypeName = "NightScene.CookingUtility.CookController";
    private const string SellableTypeName = "GameData.Core.Collections.Sellable";
    private const string RecipeTypeName = "GameData.Core.Collections.Recipe";
    private const string Il2CppActionTypeName = "Il2CppSystem.Action`1";
    private static readonly object SyncRoot = new();
    private static readonly Dictionary<nint, RuntimeCookingOwnershipSnapshot> OwnershipByController = new();
    private static Harmony? _harmony;
    private static ManualLogSource? _log;
    private static long _nextGeneration;
    private static long _nextContentRevision;
    private static bool _patched;
    private static string _status = "not attached";

    public static string Status
    {
        get
        {
            lock (SyncRoot) return _status;
        }
    }

    public static void Attach(ManualLogSource log)
    {
        lock (SyncRoot) _log = log;
        EnsureAttached(force: true);
    }

    public static bool EnsureAttached(bool force = false)
    {
        if (!force && !RuntimeNightBusinessLifecycle.IsActive) return false;

        lock (SyncRoot)
        {
            if (_patched) return true;
        }

        try
        {
            var type = RuntimeReflectionUtility.FindType(CookControllerTypeName);
            var methods = type?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?? Array.Empty<MethodInfo>();
            var setCook = methods.SingleOrDefault(IsTargetSetCookMethod);
            var extract = methods.SingleOrDefault(IsTargetExtractMethod);
            var store = methods.SingleOrDefault(IsTargetStoreMethod);
            var setCookPrefix = typeof(RuntimeCookingGenerationTracker).GetMethod(
                nameof(OnSetCookStarting),
                BindingFlags.NonPublic | BindingFlags.Static);
            var extractPrefix = typeof(RuntimeCookingGenerationTracker).GetMethod(
                nameof(OnExtractStarting),
                BindingFlags.NonPublic | BindingFlags.Static);
            var storePrefix = typeof(RuntimeCookingGenerationTracker).GetMethod(
                nameof(OnStoreStarting),
                BindingFlags.NonPublic | BindingFlags.Static);
            var setCookPostfix = typeof(RuntimeCookingGenerationTracker).GetMethod(
                nameof(OnSetCookCompleted),
                BindingFlags.NonPublic | BindingFlags.Static);
            var extractPostfix = typeof(RuntimeCookingGenerationTracker).GetMethod(
                nameof(OnExtractCompleted),
                BindingFlags.NonPublic | BindingFlags.Static);
            var storePostfix = typeof(RuntimeCookingGenerationTracker).GetMethod(
                nameof(OnStoreCompleted),
                BindingFlags.NonPublic | BindingFlags.Static);
            if (setCook == null
                || extract == null
                || store == null
                || setCookPrefix == null
                || extractPrefix == null
                || storePrefix == null
                || setCookPostfix == null
                || extractPostfix == null
                || storePostfix == null)
            {
                lock (SyncRoot)
                {
                    _status = "unavailable: exact CookController SetCook/Extract/Store methods were not found";
                }

                return false;
            }

            _harmony ??= new Harmony("com.tyukki.mystia-steward-companion.cooking-ownership");
            _harmony.Patch(
                setCook,
                prefix: new HarmonyMethod(setCookPrefix),
                postfix: new HarmonyMethod(setCookPostfix));
            _harmony.Patch(
                extract,
                prefix: new HarmonyMethod(extractPrefix),
                postfix: new HarmonyMethod(extractPostfix));
            _harmony.Patch(
                store,
                prefix: new HarmonyMethod(storePrefix),
                postfix: new HarmonyMethod(storePostfix));
            lock (SyncRoot)
            {
                _patched = true;
                _status = "patched=3";
            }

            _log?.LogInfo(
                "Cooking ownership tracker patched: CookController.SetCook(Sellable, Recipe, bool), "
                + "Extract(Action<Sellable>), Store(Sellable).");
            return true;
        }
        catch (Exception ex)
        {
            try
            {
                _harmony?.UnpatchSelf();
            }
            catch
            {
                // The tracker remains unavailable; preserve the original patch failure below.
            }

            lock (SyncRoot) _status = $"error: {ex.GetBaseException().Message}";
            _log?.LogWarning($"Cooking generation tracker attach failed: {ex.GetBaseException().Message}");
            return false;
        }
    }

    public static bool TryGetOwnershipSnapshot(
        object cookController,
        out RuntimeCookingOwnershipSnapshot snapshot,
        out string diagnostic)
    {
        snapshot = default;
        if (!RuntimeNightBusinessLifecycle.IsActive)
        {
            diagnostic = $"night business unavailable: {RuntimeNightBusinessLifecycle.Status}";
            return false;
        }

        if (!EnsureAttached())
        {
            diagnostic = Status;
            return false;
        }

        if (!TryReadNativePointer(cookController, out var pointer))
        {
            diagnostic = "CookController native pointer is unavailable";
            return false;
        }

        lock (SyncRoot)
        {
            if (!OwnershipByController.TryGetValue(pointer, out snapshot)
                || snapshot.Generation <= 0
                || snapshot.ContentRevision <= 0)
            {
                diagnostic = $"no owned SetCook content for controller 0x{(long)pointer:X}";
                return false;
            }
        }

        diagnostic = $"controller=0x{(long)pointer:X}; generation={snapshot.Generation}; "
            + $"contentRevision={snapshot.ContentRevision}; mutation={snapshot.LastMutation}; "
            + $"completed={snapshot.MutationCompleted}";
        return true;
    }

    public static void ClearForSceneChange()
    {
        lock (SyncRoot) OwnershipByController.Clear();
    }

    private static void OnSetCookStarting(object __instance, out RuntimeCookingMutationToken __state)
    {
        __state = TryRecordContentMutation(
            __instance,
            RuntimeCookingContentMutation.SetCook,
            startsNewGeneration: true);
    }

    private static void OnSetCookCompleted(RuntimeCookingMutationToken __state, bool __runOriginal)
    {
        TryCompleteContentMutation(__state, __runOriginal);
    }

    private static void OnExtractStarting(object __instance, out RuntimeCookingMutationToken __state)
    {
        __state = TryRecordContentMutation(
            __instance,
            RuntimeCookingContentMutation.Extract,
            startsNewGeneration: false);
    }

    private static void OnExtractCompleted(RuntimeCookingMutationToken __state, bool __runOriginal)
    {
        TryCompleteContentMutation(__state, __runOriginal);
    }

    private static void OnStoreStarting(object __instance, out RuntimeCookingMutationToken __state)
    {
        __state = TryRecordContentMutation(
            __instance,
            RuntimeCookingContentMutation.Store,
            startsNewGeneration: false);
    }

    private static void OnStoreCompleted(RuntimeCookingMutationToken __state, bool __runOriginal)
    {
        TryCompleteContentMutation(__state, __runOriginal);
    }

    private static RuntimeCookingMutationToken TryRecordContentMutation(
        object cookController,
        RuntimeCookingContentMutation mutation,
        bool startsNewGeneration)
    {
        try
        {
            return RecordContentMutation(cookController, mutation, startsNewGeneration);
        }
        catch (Exception ex)
        {
            ReportHookFailure($"{mutation} prefix", ex);
            return default;
        }
    }

    private static void TryCompleteContentMutation(
        RuntimeCookingMutationToken token,
        bool originalRan)
    {
        try
        {
            CompleteContentMutation(token, originalRan);
        }
        catch (Exception ex)
        {
            ReportHookFailure($"{token.Mutation} postfix", ex);
        }
    }

    private static RuntimeCookingMutationToken RecordContentMutation(
        object cookController,
        RuntimeCookingContentMutation mutation,
        bool startsNewGeneration)
    {
        if (!RuntimeNightBusinessLifecycle.IsActive) return default;

        if (!TryReadNativePointer(cookController, out var pointer))
        {
            lock (SyncRoot) _status = $"patched=3; last={mutation} controller pointer unavailable";
            return default;
        }

        lock (SyncRoot)
        {
            OwnershipByController.TryGetValue(pointer, out var current);
            var generation = current.Generation;
            if (startsNewGeneration)
            {
                _nextGeneration++;
                generation = _nextGeneration;
            }

            _nextContentRevision++;
            OwnershipByController[pointer] = new RuntimeCookingOwnershipSnapshot(
                generation,
                _nextContentRevision,
                mutation,
                MutationCompleted: false);
            _status = $"patched=3; tracked={OwnershipByController.Count}; generation={generation}; "
                + $"contentRevision={_nextContentRevision}; mutation={mutation}; completed=False";
            return new RuntimeCookingMutationToken(pointer, _nextContentRevision, mutation);
        }
    }

    private static void CompleteContentMutation(
        RuntimeCookingMutationToken token,
        bool originalRan)
    {
        if (!originalRan || token.ControllerPointer == 0 || token.ContentRevision <= 0) return;

        lock (SyncRoot)
        {
            if (!OwnershipByController.TryGetValue(token.ControllerPointer, out var current)
                || current.ContentRevision != token.ContentRevision
                || current.LastMutation != token.Mutation)
            {
                return;
            }

            OwnershipByController[token.ControllerPointer] = current with
            {
                MutationCompleted = true,
            };
            _status = $"patched=3; tracked={OwnershipByController.Count}; generation={current.Generation}; "
                + $"contentRevision={current.ContentRevision}; mutation={current.LastMutation}; completed=True";
        }
    }

    private static void ReportHookFailure(string boundary, Exception exception)
    {
        try
        {
            var message = exception.GetBaseException().Message;
            lock (SyncRoot) _status = $"patched=3; {boundary} failed: {message}";
            _log?.LogWarning($"Cooking ownership tracker {boundary} failed: {message}");
        }
        catch
        {
            // Diagnostic hooks must never affect the game's native cooker call.
        }
    }

    private static bool IsTargetSetCookMethod(MethodInfo method)
    {
        if (!string.Equals(method.Name, "SetCook", StringComparison.Ordinal)
            || method.ReturnType != typeof(void))
        {
            return false;
        }

        var parameters = method.GetParameters();
        return parameters.Length == 3
            && string.Equals(parameters[0].ParameterType.FullName, SellableTypeName, StringComparison.Ordinal)
            && string.Equals(parameters[1].ParameterType.FullName, RecipeTypeName, StringComparison.Ordinal)
            && parameters[2].ParameterType == typeof(bool);
    }

    private static bool IsTargetExtractMethod(MethodInfo method)
    {
        if (!string.Equals(method.Name, "Extract", StringComparison.Ordinal)
            || method.ReturnType != typeof(void))
        {
            return false;
        }

        var parameters = method.GetParameters();
        if (parameters.Length != 1 || !parameters[0].ParameterType.IsGenericType) return false;

        var parameterType = parameters[0].ParameterType;
        var genericArguments = parameterType.GetGenericArguments();
        return string.Equals(
                parameterType.GetGenericTypeDefinition().FullName,
                Il2CppActionTypeName,
                StringComparison.Ordinal)
            && genericArguments.Length == 1
            && string.Equals(genericArguments[0].FullName, SellableTypeName, StringComparison.Ordinal);
    }

    private static bool IsTargetStoreMethod(MethodInfo method)
    {
        if (!string.Equals(method.Name, "Store", StringComparison.Ordinal)
            || method.ReturnType != typeof(void))
        {
            return false;
        }

        var parameters = method.GetParameters();
        return parameters.Length == 1
            && string.Equals(parameters[0].ParameterType.FullName, SellableTypeName, StringComparison.Ordinal);
    }

    private static bool TryReadNativePointer(object target, out nint pointer)
    {
        pointer = 0;
        try
        {
            var value = RuntimeReflectionUtility.GetMemberValue(target, "Pointer")
                ?? RuntimeReflectionUtility.GetMemberValue(target, "NativePointer")
                ?? RuntimeReflectionUtility.GetMemberValue(target, "m_CachedPtr");
            if (value is IntPtr intPtr)
            {
                pointer = intPtr;
            }
            else if (value is IConvertible convertible)
            {
                pointer = new IntPtr(convertible.ToInt64(null));
            }

            return pointer != 0;
        }
        catch
        {
            pointer = 0;
            return false;
        }
    }
}
