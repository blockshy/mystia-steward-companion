using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;

namespace MystiaStewardCompanion.Save;

/// <summary>
/// Assigns a new generation to every CookController.SetCook call.
/// </summary>
internal static class RuntimeCookingGenerationTracker
{
    private const string CookControllerTypeName = "NightScene.CookingUtility.CookController";
    private const string SellableTypeName = "GameData.Core.Collections.Sellable";
    private const string RecipeTypeName = "GameData.Core.Collections.Recipe";
    private static readonly object SyncRoot = new();
    private static readonly Dictionary<nint, long> Generations = new();
    private static Harmony? _harmony;
    private static ManualLogSource? _log;
    private static long _nextGeneration;
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
        EnsureAttached();
    }

    public static bool EnsureAttached()
    {
        lock (SyncRoot)
        {
            if (_patched) return true;
        }

        try
        {
            var type = RuntimeReflectionUtility.FindType(CookControllerTypeName);
            var target = type?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .SingleOrDefault(IsTargetSetCookMethod);
            var prefix = typeof(RuntimeCookingGenerationTracker).GetMethod(
                nameof(OnSetCookStarting),
                BindingFlags.NonPublic | BindingFlags.Static);
            if (target == null || prefix == null)
            {
                lock (SyncRoot) _status = "unavailable: CookController.SetCook(Sellable, Recipe, bool) was not found";
                return false;
            }

            _harmony ??= new Harmony("com.tyukki.mystia-steward-companion.cooking-generation");
            _harmony.Patch(target, prefix: new HarmonyMethod(prefix));
            lock (SyncRoot)
            {
                _patched = true;
                _status = "patched=1";
            }

            _log?.LogInfo("Cooking generation tracker patched: CookController.SetCook(Sellable, Recipe, bool).");
            return true;
        }
        catch (Exception ex)
        {
            lock (SyncRoot) _status = $"error: {ex.GetBaseException().Message}";
            _log?.LogWarning($"Cooking generation tracker attach failed: {ex.GetBaseException().Message}");
            return false;
        }
    }

    public static bool TryGetGeneration(object cookController, out long generation, out string diagnostic)
    {
        generation = 0;
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
            if (!Generations.TryGetValue(pointer, out generation) || generation <= 0)
            {
                diagnostic = $"no SetCook generation for controller 0x{(long)pointer:X}";
                return false;
            }
        }

        diagnostic = $"controller=0x{(long)pointer:X}; generation={generation}";
        return true;
    }

    public static void ClearForSceneChange()
    {
        lock (SyncRoot) Generations.Clear();
    }

    private static void OnSetCookStarting(object __instance)
    {
        if (!TryReadNativePointer(__instance, out var pointer))
        {
            lock (SyncRoot) _status = "patched=1; last=SetCook controller pointer unavailable";
            return;
        }

        lock (SyncRoot)
        {
            _nextGeneration++;
            Generations[pointer] = _nextGeneration;
            _status = $"patched=1; tracked={Generations.Count}; generation={_nextGeneration}";
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
