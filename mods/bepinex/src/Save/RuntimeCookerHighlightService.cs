using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace MystiaStewardCompanion.Save;

internal static class RuntimeCookerHighlightService
{
    private const string CookSystemManagerTypeName = "NightScene.CookingUtility.CookSystemManager";
    private const string CookControllerTypeName = "NightScene.CookingUtility.CookController";

    private static readonly object SyncRoot = new();
    private static readonly Dictionary<nint, HighlightedRenderer> HighlightedRenderers = new();

    private static bool _enabled;
    private static int _targetCookerTypeId = -1;
    private static string _targetCookerName = "";
    private static float _nextScanAt;
    private static string _status = "disabled";

    public static string Status
    {
        get
        {
            lock (SyncRoot)
            {
                return _status;
            }
        }
    }

    public static void UpdateTarget(bool enabled, int cookerTypeId, string cookerName)
    {
        lock (SyncRoot)
        {
            var nextEnabled = enabled && cookerTypeId > 0;
            var changed = _enabled != nextEnabled
                || _targetCookerTypeId != cookerTypeId
                || !string.Equals(_targetCookerName, cookerName, StringComparison.Ordinal);

            _enabled = nextEnabled;
            _targetCookerTypeId = nextEnabled ? cookerTypeId : -1;
            _targetCookerName = nextEnabled ? cookerName.Trim() : "";
            if (changed) _nextScanAt = 0f;
            if (!nextEnabled)
            {
                RestoreAllLocked();
                _status = "disabled";
            }
        }
    }

    public static void Tick()
    {
        bool enabled;
        int targetCookerTypeId;
        lock (SyncRoot)
        {
            enabled = _enabled;
            targetCookerTypeId = _targetCookerTypeId;
        }

        if (!enabled || targetCookerTypeId <= 0)
        {
            Clear();
            return;
        }

        if (Time.realtimeSinceStartup >= _nextScanAt)
        {
            ScanAndApply(targetCookerTypeId);
        }

        PulseHighlightedRenderers();
    }

    public static void Clear()
    {
        lock (SyncRoot)
        {
            RestoreAllLocked();
            _enabled = false;
            _targetCookerTypeId = -1;
            _targetCookerName = "";
            _status = "disabled";
        }
    }

    private static void ScanAndApply(int targetCookerTypeId)
    {
        var renderers = new List<SpriteRenderer>();
        var controllerCount = 0;
        var matchedControllerCount = 0;
        var error = "";
        var sourceStatus = "sources=none";

        try
        {
            var cookSystem = GetSingletonInstance(CookSystemManagerTypeName);
            if (cookSystem == null)
            {
                SetStatus("waiting: cook system missing");
                return;
            }

            var controllers = ReadCookerControllers(cookSystem, out sourceStatus);
            foreach (var controller in controllers)
            {
                controllerCount++;
                var cooker = TryInvokeInstanceValue(controller, "get_Cooker")
                    ?? ReadMember(controller, "Cooker");
                if (cooker == null) continue;

                var typeIds = ReadCookerTypeIds(cooker);
                if (!typeIds.Contains(targetCookerTypeId)) continue;

                matchedControllerCount++;
                renderers.AddRange(ReadCookerRenderers(controller));
            }
        }
        catch (Exception ex)
        {
            error = ex.InnerException?.Message ?? ex.Message;
        }

        lock (SyncRoot)
        {
            _nextScanAt = Time.realtimeSinceStartup + 0.75f;
            if (!string.IsNullOrWhiteSpace(error))
            {
                _status = $"error: {error}";
                return;
            }

            var expectedPointers = renderers
                .Where(renderer => renderer != null)
                .Select(ReadUnityObjectPointer)
                .Where(pointer => pointer != IntPtr.Zero)
                .ToHashSet();

            foreach (var pointer in HighlightedRenderers.Keys.ToList())
            {
                if (expectedPointers.Contains(pointer)) continue;
                RestoreRendererLocked(pointer);
            }

            foreach (var renderer in renderers)
            {
                if (renderer == null) continue;
                var pointer = ReadUnityObjectPointer(renderer);
                if (pointer == IntPtr.Zero || HighlightedRenderers.ContainsKey(pointer)) continue;

                try
                {
                    HighlightedRenderers[pointer] = new HighlightedRenderer(renderer, renderer.color, renderer.enabled);
                    renderer.enabled = true;
                }
                catch
                {
                    // Ignore stale renderers; they will be dropped on the next scan.
                }
            }

            _status = matchedControllerCount == 0
                ? $"target missing; controllers={controllerCount}; {sourceStatus}; cooker={_targetCookerTypeId}/{_targetCookerName}"
                : $"active; controllers={controllerCount}; matched={matchedControllerCount}; renderers={HighlightedRenderers.Count}; {sourceStatus}; cooker={_targetCookerTypeId}/{_targetCookerName}";
        }
    }

    private static IReadOnlyList<object> ReadCookerControllers(object cookSystem, out string status)
    {
        var result = new List<object>();
        var seen = new HashSet<nint>();
        var sourceParts = new List<string>();

        void AddControllers(string source, IEnumerable<object?> controllers)
        {
            var scanned = 0;
            var added = 0;
            foreach (var controller in controllers)
            {
                scanned++;
                if (controller == null) continue;
                nint pointer;
                try
                {
                    pointer = ReadObjectPointer(controller);
                }
                catch
                {
                    pointer = new IntPtr(RuntimeHelpers.GetHashCode(controller));
                }

                if (!seen.Add(pointer)) continue;
                result.Add(controller);
                added++;
            }

            sourceParts.Add($"{source}:{scanned}/{added}");
        }

        var directControllers = TryInvokeInstanceValue(cookSystem, "get_AllCookerControllers")
            ?? ReadMember(cookSystem, "AllCookerControllers");
        AddControllers("AllCookerControllers", ReadObjectEnumerable(directControllers));

        var allCookers = ReadMember(cookSystem, "AllCookers");
        AddControllers("AllCookers", ReadDictionaryValues(allCookers).Where(value => value != null));

        var controllerType = FindType(CookControllerTypeName);
        if (controllerType != null)
        {
            AddControllers("UnityFind", FindUnityObjects(controllerType));
        }

        status = $"sources={string.Join(",", sourceParts)}";
        return result;
    }

    private static IEnumerable<SpriteRenderer> ReadCookerRenderers(object controller)
    {
        var visual = ReadMember(controller, "visual")
            ?? TryInvokeInstanceValue(controller, "get_visual");
        if (visual != null)
        {
            foreach (var renderer in ReadSpriteRenderers(ReadMember(visual, "m_CookerLight")
                         ?? ReadMember(visual, "CookerLight")
                         ?? ReadMember(visual, "cookerLight")))
            {
                yield return renderer;
            }

            foreach (var renderer in ReadSpriteRenderers(ReadMember(visual, "m_CookerSpriteRenderer")
                         ?? ReadMember(visual, "CookerSpriteRenderer")))
            {
                yield return renderer;
            }

            foreach (var renderer in ReadSpriteRenderersInChildren(visual))
            {
                yield return renderer;
            }
        }

        foreach (var renderer in ReadSpriteRenderers(ReadMember(controller, "sellableShadow")))
        {
            yield return renderer;
        }

        foreach (var renderer in ReadSpriteRenderersInChildren(controller))
        {
            yield return renderer;
        }
    }

    private static IEnumerable<SpriteRenderer> ReadSpriteRenderers(object? value)
    {
        if (value == null || value is string) yield break;

        if (value is SpriteRenderer renderer)
        {
            yield return renderer;
            yield break;
        }

        foreach (var item in ReadObjectEnumerable(value))
        {
            if (item is SpriteRenderer itemRenderer) yield return itemRenderer;
        }
    }

    private static IEnumerable<SpriteRenderer> ReadSpriteRenderersInChildren(object? value)
    {
        if (value == null || value is string) yield break;

        object? renderers = null;
        try
        {
            var method = value.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(candidate =>
                {
                    if (!string.Equals(candidate.Name, "GetComponentsInChildren", StringComparison.Ordinal)) return false;
                    var parameters = candidate.GetParameters();
                    return parameters.Length == 2
                        && parameters[0].ParameterType == typeof(Type)
                        && parameters[1].ParameterType == typeof(bool);
                });
            renderers = method?.Invoke(value, new object?[] { typeof(SpriteRenderer), true });
        }
        catch
        {
            renderers = null;
        }

        foreach (var item in ReadObjectEnumerable(renderers))
        {
            if (item is SpriteRenderer renderer) yield return renderer;
        }
    }

    private static void PulseHighlightedRenderers()
    {
        List<HighlightedRenderer> renderers;
        lock (SyncRoot)
        {
            renderers = HighlightedRenderers.Values.ToList();
        }

        var pulse = 0.55f + (Mathf.Sin(Time.realtimeSinceStartup * 5.5f) + 1f) * 0.225f;
        var target = new Color(1f, 0.86f, 0.18f, 1f);
        foreach (var item in renderers)
        {
            try
            {
                if (item.Renderer == null) continue;
                var color = Color.Lerp(item.OriginalColor, target, pulse);
                color.a = Mathf.Max(item.OriginalColor.a, 0.85f);
                item.Renderer.enabled = true;
                item.Renderer.color = color;
            }
            catch
            {
                lock (SyncRoot)
                {
                    HighlightedRenderers.Remove(item.Pointer);
                }
            }
        }
    }

    private static void RestoreAllLocked()
    {
        foreach (var pointer in HighlightedRenderers.Keys.ToList())
        {
            RestoreRendererLocked(pointer);
        }
    }

    private static void RestoreRendererLocked(nint pointer)
    {
        if (!HighlightedRenderers.TryGetValue(pointer, out var item)) return;

        try
        {
            if (item.Renderer != null)
            {
                item.Renderer.color = item.OriginalColor;
                item.Renderer.enabled = item.OriginalEnabled;
            }
        }
        catch
        {
            // The renderer may already be destroyed during scene changes.
        }

        HighlightedRenderers.Remove(pointer);
    }

    private static List<int> ReadCookerTypeIds(object cooker)
    {
        try
        {
            var directType = ToInt(TryInvokeInstanceValue(cooker, "get_Type")
                ?? ReadMember(cooker, "Type")
                ?? ReadMember(cooker, "type"));
            if (directType > 0) return new List<int> { directType };
        }
        catch
        {
            // Fall back to all available cooker types below.
        }

        var cookerTypes = TryInvokeInstanceValue(cooker, "get_AllAvailableCookerType");
        return ReadIntEnumerable(cookerTypes).Where(id => id > 0).Distinct().ToList();
    }

    private static object? GetSingletonInstance(string typeName)
    {
        var type = FindType(typeName);
        if (type == null) return null;

        var property = type.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy);
        if (property != null) return property.GetValue(null);

        var method = type.GetMethod("get_Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy);
        return method?.Invoke(null, Array.Empty<object?>());
    }

    private static object? TryInvokeInstanceValue(object target, string methodName)
    {
        try
        {
            var method = target.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(candidate => string.Equals(candidate.Name, methodName, StringComparison.Ordinal)
                    && candidate.GetParameters().Length == 0);
            return method == null ? null : method.Invoke(target, Array.Empty<object?>());
        }
        catch
        {
            return null;
        }
    }

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

    private static IEnumerable<object> ReadObjectEnumerable(object? value)
    {
        if (value == null || value is string) yield break;

        var seen = new HashSet<nint>();
        foreach (var item in EnumerateManaged(value).Concat(EnumerateByIndexer(value)))
        {
            if (item == null) continue;
            nint pointer;
            try
            {
                pointer = ReadObjectPointer(item);
            }
            catch
            {
                pointer = new IntPtr(RuntimeHelpers.GetHashCode(item));
            }

            if (!seen.Add(pointer)) continue;
            yield return item;
        }
    }

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
        if (entries != null && count > 0)
        {
            var entryIndex = 0;
            foreach (var entry in EnumerateByIndexer(entries))
            {
                if (entryIndex++ >= Math.Min(count, 256)) break;
                if (entry == null) continue;

                var hashCode = ToInt(ReadMember(entry, "hashCode") ?? ReadMember(entry, "_hashCode"));
                if (hashCode < 0) continue;

                var value = ReadMember(entry, "value")
                    ?? ReadMember(entry, "Value")
                    ?? ReadMember(entry, "_value");
                if (value != null) yield return value;
            }
        }

        foreach (var item in ReadObjectEnumerable(dictionary))
        {
            var value = NormalizeDictionaryItem(item);
            if (value != null) yield return value;
        }
    }

    private static IEnumerable<int> ReadIntEnumerable(object? value)
    {
        if (value == null || value is string) yield break;

        foreach (var item in EnumerateManaged(value).Concat(EnumerateByIndexer(value)))
        {
            yield return ToInt(item);
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

        for (var index = 0; index < Math.Min(count, 128); index++)
        {
            yield return TryInvokeInstanceValue(value, "get_Item", new object?[] { index });
        }
    }

    private static object? TryInvokeInstanceValue(object target, string methodName, object?[] args)
    {
        try
        {
            var method = target.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(candidate => string.Equals(candidate.Name, methodName, StringComparison.Ordinal)
                    && CanUseParameters(candidate.GetParameters(), args));
            return method == null ? null : method.Invoke(target, args);
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<object?> FindUnityObjects(Type type)
    {
        var method = typeof(UnityEngine.Object).GetMethod("FindObjectsOfType", new[] { typeof(Type) });
        if (method == null) yield break;

        object? objects = null;
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

    private static bool CanUseParameters(ParameterInfo[] parameters, object?[] args)
    {
        if (parameters.Length != args.Length) return false;
        for (var i = 0; i < parameters.Length; i++)
        {
            if (args[i] == null) continue;
            if (!parameters[i].ParameterType.IsInstanceOfType(args[i])) return false;
        }

        return true;
    }

    private static bool LooksLikeIl2CppObject(object value)
    {
        var type = value.GetType();
        var fullName = type.FullName ?? "";
        if (fullName.StartsWith("Il2Cpp", StringComparison.Ordinal)) return true;
        if (fullName.StartsWith("NightScene.", StringComparison.Ordinal)) return true;
        if (fullName.StartsWith("GameData.", StringComparison.Ordinal)) return true;
        return type.Assembly.GetName().Name?.Contains("Il2Cpp", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static object? ReadMember(object target, string name)
    {
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

    private static object? NormalizeDictionaryItem(object item)
    {
        return ReadMember(item, "Value") ?? ReadMember(item, "value");
    }

    private static IEnumerable<string> BuildFieldNameCandidates(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) yield break;

        yield return name;
        yield return $"m_{name}";
        yield return $"_{name}";
        yield return $"<{name}>k__BackingField";

        var camelName = char.ToLowerInvariant(name[0]) + name[1..];
        if (!string.Equals(camelName, name, StringComparison.Ordinal))
        {
            yield return camelName;
            yield return $"m_{camelName}";
            yield return $"_{camelName}";
            yield return $"<{camelName}>k__BackingField";
        }
    }

    private static int ToInt(object? value)
    {
        if (value == null) return -1;
        if (value is int number) return number;
        if (value is Enum) return Convert.ToInt32(value);
        if (value is IConvertible convertible)
        {
            try
            {
                return convertible.ToInt32(null);
            }
            catch
            {
                return -1;
            }
        }

        return int.TryParse(value.ToString(), out var parsed) ? parsed : -1;
    }

    private static nint ReadObjectPointer(object target)
    {
        var pointer = ReadMember(target, "Pointer") ?? ReadMember(target, "NativePointer") ?? ReadMember(target, "m_CachedPtr");
        if (pointer is IntPtr intPtr) return intPtr;
        if (pointer is nint native) return native;
        if (pointer is IConvertible convertible)
        {
            try
            {
                return new IntPtr(convertible.ToInt64(null));
            }
            catch
            {
                return new IntPtr(RuntimeHelpers.GetHashCode(target));
            }
        }

        return new IntPtr(RuntimeHelpers.GetHashCode(target));
    }

    private static nint ReadUnityObjectPointer(SpriteRenderer renderer)
    {
        try
        {
            var pointer = ReadMember(renderer, "m_CachedPtr")
                ?? ReadMember(renderer, "Pointer")
                ?? ReadMember(renderer, "NativePointer");
            if (pointer is IntPtr intPtr) return intPtr;
            if (pointer is nint native) return native;
            if (pointer is IConvertible convertible) return new IntPtr(convertible.ToInt64(null));
        }
        catch
        {
            return IntPtr.Zero;
        }

        return new IntPtr(RuntimeHelpers.GetHashCode(renderer));
    }

    private static void SetStatus(string status)
    {
        lock (SyncRoot)
        {
            _status = status;
        }
    }

    private sealed class HighlightedRenderer
    {
        public HighlightedRenderer(SpriteRenderer renderer, Color originalColor, bool originalEnabled)
        {
            Renderer = renderer;
            Pointer = ReadUnityObjectPointer(renderer);
            OriginalColor = originalColor;
            OriginalEnabled = originalEnabled;
        }

        public SpriteRenderer Renderer { get; }
        public nint Pointer { get; }
        public Color OriginalColor { get; }
        public bool OriginalEnabled { get; }
    }
}
