using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace MystiaStewardCompanion.Save;

internal static class RuntimeCookerHighlightService
{
    private const float ScanIntervalSeconds = 1.25f;

    private static readonly object DesiredRoot = new();
    private static readonly object VisualRoot = new();
    private static readonly Dictionary<nint, HighlightedRenderer> HighlightedRenderers = new();

    private static CookerHighlightTargetSnapshot _desiredTarget = CookerHighlightTargetSnapshot.Disabled;
    private static long _appliedTargetGeneration;
    private static bool _suspended = true;
    private static string _suspendReason = "night business inactive";
    private static float _nextScanAt;
    private static string _status = "disabled";

    public static string Status
    {
        get
        {
            var desired = Volatile.Read(ref _desiredTarget);
            lock (VisualRoot)
            {
                return $"{_status}; desired={desired.Generation}/session:{desired.SessionGeneration}/cooker:{desired.CookerTypeId}/{desired.CookerName}; applied={_appliedTargetGeneration}; suspended={_suspended}";
            }
        }
    }

    /// <summary>
    /// Publishes managed desired state only. Unity objects are reconciled later by <see cref="Tick"/>.
    /// </summary>
    public static void UpdateTarget(long sessionGeneration, bool enabled, int cookerTypeId, string cookerName)
    {
        var normalizedEnabled = enabled && sessionGeneration > 0 && cookerTypeId > 0;
        var normalizedCookerTypeId = normalizedEnabled ? cookerTypeId : -1;
        var normalizedCookerName = normalizedEnabled ? cookerName.Trim() : "";
        lock (DesiredRoot)
        {
            var current = Volatile.Read(ref _desiredTarget);
            if (current.HasSameValues(sessionGeneration, normalizedEnabled, normalizedCookerTypeId, normalizedCookerName)) return;

            Volatile.Write(
                ref _desiredTarget,
                new CookerHighlightTargetSnapshot(
                    checked(current.Generation + 1),
                    sessionGeneration,
                    normalizedEnabled,
                    normalizedCookerTypeId,
                    normalizedCookerName));
        }
    }

    public static void Tick()
    {
        var lifecycle = RuntimeNightBusinessLifecycle.Snapshot;
        var desired = Volatile.Read(ref _desiredTarget);
        lock (VisualRoot)
        {
            if (_suspended || !lifecycle.IsActive) return;

            var desiredEnabled = desired.Enabled
                && desired.SessionGeneration == lifecycle.Generation
                && desired.CookerTypeId > 0;
            if (_appliedTargetGeneration != desired.Generation)
            {
                RestoreAllLocked();
                _appliedTargetGeneration = desired.Generation;
                _nextScanAt = 0f;
            }

            if (!desiredEnabled)
            {
                _status = desired.Enabled
                    ? "waiting: target belongs to a different night-business session"
                    : "disabled";
                return;
            }
        }

        if (Time.realtimeSinceStartup >= _nextScanAt)
        {
            ScanAndApply(desired);
        }

        PulseHighlightedRenderers(desired);
    }

    public static void Suspend(string reason)
    {
        lock (VisualRoot)
        {
            _suspended = true;
            _suspendReason = NormalizeReason(reason);
            RestoreAllLocked();
            _nextScanAt = 0f;
            _status = Volatile.Read(ref _desiredTarget).Enabled
                ? $"suspended: {_suspendReason}"
                : "disabled";
        }
    }

    public static void Resume(string reason)
    {
        lock (VisualRoot)
        {
            _suspended = false;
            _suspendReason = NormalizeReason(reason);
            _nextScanAt = 0f;
            _status = Volatile.Read(ref _desiredTarget).Enabled
                ? "waiting for main-thread reconcile"
                : "disabled";
        }
    }

    /// <summary>
    /// Drops wrappers after native scene destruction without dereferencing their Unity objects.
    /// </summary>
    public static void Abandon(string reason)
    {
        lock (VisualRoot)
        {
            HighlightedRenderers.Clear();
            _suspended = true;
            _suspendReason = NormalizeReason(reason);
            _nextScanAt = 0f;
            _status = $"abandoned: {_suspendReason}";
        }
    }

    private static void ScanAndApply(CookerHighlightTargetSnapshot target)
    {
        lock (VisualRoot) _nextScanAt = Time.realtimeSinceStartup + ScanIntervalSeconds;

        var renderers = new List<SpriteRenderer>();
        var controllerCount = 0;
        var matchedControllerCount = 0;
        var error = "";
        var sourceStatus = "sources=none";

        try
        {
            var cookSystem = RuntimeCookerReflection.GetCookSystemManager();
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

                var typeIds = RuntimeCookerReflection.ReadCookerTypeIds(cooker);
                if (!typeIds.Contains(target.CookerTypeId)) continue;

                matchedControllerCount++;
                renderers.AddRange(ReadCookerRenderers(controller));
            }
        }
        catch (Exception ex)
        {
            error = ex.InnerException?.Message ?? ex.Message;
        }

        if (!IsTargetCurrent(target)) return;

        lock (VisualRoot)
        {
            if (_suspended || !IsTargetCurrent(target)) return;
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
                ? $"target missing; controllers={controllerCount}; {sourceStatus}; cooker={target.CookerTypeId}/{target.CookerName}"
                : $"active; controllers={controllerCount}; matched={matchedControllerCount}; renderers={HighlightedRenderers.Count}; {sourceStatus}; cooker={target.CookerTypeId}/{target.CookerName}";
        }
    }

    private static IReadOnlyList<object> ReadCookerControllers(object cookSystem, out string status)
    {
        return RuntimeCookerReflection.ReadCookerControllersFromCookSystem(cookSystem, out status);
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

    private static void PulseHighlightedRenderers(CookerHighlightTargetSnapshot target)
    {
        List<HighlightedRenderer> renderers;
        lock (VisualRoot)
        {
            if (_suspended || !IsTargetCurrent(target)) return;
            renderers = HighlightedRenderers.Values.ToList();
        }

        var pulse = 0.55f + (Mathf.Sin(Time.realtimeSinceStartup * 5.5f) + 1f) * 0.225f;
        var highlightColor = new Color(1f, 0.86f, 0.18f, 1f);
        foreach (var item in renderers)
        {
            try
            {
                if (item.Renderer == null) continue;
                var color = Color.Lerp(item.OriginalColor, highlightColor, pulse);
                color.a = Mathf.Max(item.OriginalColor.a, 0.85f);
                item.Renderer.enabled = true;
                item.Renderer.color = color;
            }
            catch
            {
                lock (VisualRoot)
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
        lock (VisualRoot)
        {
            _status = status;
        }
    }

    private static bool IsTargetCurrent(CookerHighlightTargetSnapshot target)
    {
        var lifecycle = RuntimeNightBusinessLifecycle.Snapshot;
        var desired = Volatile.Read(ref _desiredTarget);
        return lifecycle.IsActive
            && target.Enabled
            && target.SessionGeneration == lifecycle.Generation
            && ReferenceEquals(desired, target);
    }

    private static string NormalizeReason(string reason)
    {
        return string.IsNullOrWhiteSpace(reason) ? "night business unavailable" : reason.Trim();
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

    private sealed class CookerHighlightTargetSnapshot
    {
        public static readonly CookerHighlightTargetSnapshot Disabled = new(0, 0, false, -1, "");

        public CookerHighlightTargetSnapshot(
            long generation,
            long sessionGeneration,
            bool enabled,
            int cookerTypeId,
            string cookerName)
        {
            Generation = generation;
            SessionGeneration = sessionGeneration;
            Enabled = enabled;
            CookerTypeId = cookerTypeId;
            CookerName = cookerName;
        }

        public long Generation { get; }
        public long SessionGeneration { get; }
        public bool Enabled { get; }
        public int CookerTypeId { get; }
        public string CookerName { get; }

        public bool HasSameValues(long sessionGeneration, bool enabled, int cookerTypeId, string cookerName)
        {
            return SessionGeneration == sessionGeneration
                && Enabled == enabled
                && CookerTypeId == cookerTypeId
                && string.Equals(CookerName, cookerName, StringComparison.Ordinal);
        }
    }
}
