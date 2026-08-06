using System.Collections;
using System.Reflection;
using UnityEngine;

namespace MystiaStewardCompanion.Save;

internal static class RuntimeCookerHighlightService
{
    private const float ScanIntervalSeconds = 1.25f;

    private static readonly object DesiredRoot = new();
    private static readonly object VisualRoot = new();
    private static readonly Dictionary<nint, HighlightedRenderer> HighlightedRenderers = new();
    private static readonly Dictionary<nint, RendererBaseline> RendererBaselines = new();

    private static RuntimeUiTargetSetSnapshot _desiredTargetSet = RuntimeUiTargetSetSnapshot.Disabled;
    private static long _appliedTargetGeneration;
    private static bool _suspended = true;
    private static string _suspendReason = "night business inactive";
    private static int _topologyMutationDepth;
    private static float _nextScanAt;
    private static string _status = "disabled";

    public static string Status
    {
        get
        {
            var desired = Volatile.Read(ref _desiredTargetSet);
            lock (VisualRoot)
            {
                var cookerTargets = desired.Targets.Where(target => target.CookerHighlightEnabled).ToArray();
                var cookers = cookerTargets.Length == 0
                    ? "none"
                    : string.Join(",", cookerTargets.Select(target => $"{target.Kind}:{target.CookerTypeId}"));
                return $"{_status}; desired={desired.Generation}/session:{desired.SessionGeneration}/cookers:{cookers}; applied={_appliedTargetGeneration}; suspended={_suspended}; topologyMutationDepth={_topologyMutationDepth}";
            }
        }
    }

    /// <summary>
    /// Publishes managed desired state only. Unity objects are reconciled later by <see cref="Tick"/>.
    /// </summary>
    public static void UpdateTargets(RuntimeUiTargetSetSnapshot targetSet)
    {
        ArgumentNullException.ThrowIfNull(targetSet);
        lock (DesiredRoot)
        {
            if (ReferenceEquals(Volatile.Read(ref _desiredTargetSet), targetSet)) return;
            Volatile.Write(ref _desiredTargetSet, targetSet);
        }
    }

    public static void Tick()
    {
        var lifecycle = RuntimeNightBusinessLifecycle.Snapshot;
        var desired = Volatile.Read(ref _desiredTargetSet);
        lock (VisualRoot)
        {
            if (_suspended || _topologyMutationDepth > 0 || !lifecycle.IsActive) return;

            var desiredEnabled = HasCookerHighlightTargets(desired)
                && desired.SessionGeneration == lifecycle.Generation;
            if (_appliedTargetGeneration != desired.Generation)
            {
                RestoreAllLocked();
                _appliedTargetGeneration = desired.Generation;
                _nextScanAt = 0f;
            }

            if (!desiredEnabled)
            {
                _status = HasCookerHighlightTargets(desired)
                    ? "waiting: target belongs to a different night-business session"
                    : "disabled";
                if (RendererBaselines.Count == 0) return;
            }
        }

        if (Time.realtimeSinceStartup >= _nextScanAt)
        {
            ScanAndApply(desired);
        }

        if (!HasCookerHighlightTargets(desired)) return;
        PulseHighlightedRenderers(desired);
    }

    public static void Suspend(string reason)
    {
        lock (VisualRoot)
        {
            var topologyMutationWasActive = _topologyMutationDepth > 0;
            _topologyMutationDepth = 0;
            _suspended = true;
            _suspendReason = NormalizeReason(reason);
            if (topologyMutationWasActive)
            {
                HighlightedRenderers.Clear();
            }
            else
            {
                RestoreAllLocked();
            }
            RendererBaselines.Clear();
            _nextScanAt = 0f;
            _status = HasCookerHighlightTargets(Volatile.Read(ref _desiredTargetSet))
                ? $"suspended: {_suspendReason}"
                : "disabled";
        }
    }

    public static void Resume(string reason)
    {
        lock (VisualRoot)
        {
            HighlightedRenderers.Clear();
            RendererBaselines.Clear();
            _topologyMutationDepth = 0;
            _suspended = false;
            _suspendReason = NormalizeReason(reason);
            _nextScanAt = 0f;
            _status = HasCookerHighlightTargets(Volatile.Read(ref _desiredTargetSet))
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
            RendererBaselines.Clear();
            _topologyMutationDepth = 0;
            _suspended = true;
            _suspendReason = NormalizeReason(reason);
            _nextScanAt = 0f;
            _status = $"abandoned: {_suspendReason}";
        }
    }

    /// <summary>
    /// Drops all renderer wrappers before a native cooker topology mutation starts.
    /// </summary>
    public static void BeginTopologyMutation(string reason)
    {
        IncrementTopologyMutationDepth();
        try
        {
            lock (VisualRoot)
            {
                foreach (var (pointer, highlighted) in HighlightedRenderers)
                {
                    RendererBaselines[pointer] = new RendererBaseline(
                        highlighted.OriginalColor,
                        highlighted.OriginalEnabled);
                }
                HighlightedRenderers.Clear();

                _nextScanAt = 0f;
                _status = $"topology mutation active: {NormalizeReason(reason)}";
            }
        }
        catch
        {
            try
            {
                lock (VisualRoot)
                {
                    HighlightedRenderers.Clear();
                    RendererBaselines.Clear();
                    _nextScanAt = 0f;
                    _status = "topology mutation active: fail-closed";
                }
            }
            catch
            {
                // Never escape into the native topology mutation.
            }
        }
    }

    /// <summary>
    /// Re-enables fresh catalog scanning after the outermost topology mutation returns.
    /// </summary>
    public static void CompleteTopologyMutation(string reason)
    {
        var remainingDepth = DecrementTopologyMutationDepth();
        try
        {
            lock (VisualRoot)
            {
                if (remainingDepth == 0)
                {
                    _nextScanAt = 0f;
                    _status = HasCookerHighlightTargets(Volatile.Read(ref _desiredTargetSet))
                        ? $"waiting after topology mutation: {NormalizeReason(reason)}"
                        : "disabled";
                }
            }
        }
        catch
        {
            Interlocked.CompareExchange(ref _topologyMutationDepth, 1, 0);
            // Never escape into the native topology mutation postfix.
        }
    }

    private static void ScanAndApply(RuntimeUiTargetSetSnapshot targetSet)
    {
        lock (VisualRoot)
        {
            if (_suspended || _topologyMutationDepth > 0) return;
            _nextScanAt = Time.realtimeSinceStartup + ScanIntervalSeconds;
        }

        var openRenderers = new List<SpriteRenderer>();
        var targetRenderers = new Dictionary<nint, TargetRenderer>();
        var controllerCount = 0;
        var lockedControllerCount = 0;
        var matchedControllerCount = 0;
        var error = "";
        var sourceStatus = "sources=none";

        try
        {
            if (!RuntimeCookerReflection.TryReadLockedCookerPositions(
                    out var lockedPositions,
                    out var lockedStatus))
            {
                error = lockedStatus;
            }
            else
            {
                var cookSystem = RuntimeCookerReflection.GetCookSystemManager();
                if (cookSystem == null)
                {
                    error = "cook system missing";
                }
                else if (!RuntimeCookerReflection.TryReadCookerControllerEntriesFromCookSystem(
                         cookSystem,
                         lockedPositions,
                         out var controllerEntries,
                         out var controllerStatus))
                {
                    error = controllerStatus;
                }
                else
                {
                    sourceStatus = $"{lockedStatus}; {controllerStatus}";
                    controllerCount = controllerEntries.Count;
                    foreach (var entry in controllerEntries)
                    {
                        if (lockedPositions.Contains(entry.GridPosition))
                        {
                            lockedControllerCount++;
                            continue;
                        }

                        if (!RuntimeCookerReflection.TryReadCookerControllerState(
                                entry.Controller,
                                out var state,
                                out var stateStatus))
                        {
                            error = $"controller={entry.ControllerIdentity}; {stateStatus}";
                            break;
                        }

                        if (!state.CouldOpen)
                        {
                            error = $"controller={entry.ControllerIdentity}; position={entry.GridPosition}; "
                                + $"couldOpen={state.CouldOpen}; not present in LockedCookers; gate-mismatch";
                            break;
                        }

                        if (state.IsEmptyDesk) continue;

                        var controllerRenderers = ReadCookerRenderers(entry.Controller).ToArray();
                        openRenderers.AddRange(controllerRenderers);
                        var claims = RuntimeUiTargetKinds.None;
                        if (HasCookerHighlightTargets(targetSet))
                        {
                            foreach (var cookerTypeId in state.TypeIds)
                            {
                                claims |= targetSet.GetCookerClaims(cookerTypeId);
                            }
                        }
                        if (claims != RuntimeUiTargetKinds.None)
                        {
                            matchedControllerCount++;
                            foreach (var renderer in controllerRenderers)
                            {
                                if (renderer == null) continue;
                                var pointer = ReadUnityObjectPointer(renderer);
                                if (pointer == IntPtr.Zero) continue;
                                if (targetRenderers.TryGetValue(pointer, out var existing))
                                {
                                    existing.Claims |= claims;
                                }
                                else
                                {
                                    targetRenderers[pointer] = new TargetRenderer(renderer, pointer, claims);
                                }
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            error = ex.InnerException?.Message ?? ex.Message;
        }

        if (!IsTargetSnapshotCurrent(targetSet)) return;

        lock (VisualRoot)
        {
            if (_suspended || _topologyMutationDepth > 0 || !IsTargetSnapshotCurrent(targetSet)) return;
            if (!string.IsNullOrWhiteSpace(error))
            {
                RestoreAllLocked();
                _status = $"error: {error}";
                return;
            }

            RestoreRetainedBaselinesLocked(openRenderers);
            if (!HasCookerHighlightTargets(targetSet))
            {
                _status = "disabled";
                return;
            }

            var expectedPointers = targetRenderers.Keys.ToHashSet();

            foreach (var pointer in HighlightedRenderers.Keys.ToList())
            {
                if (expectedPointers.Contains(pointer)) continue;
                RestoreRendererLocked(pointer);
            }

            foreach (var targetRenderer in targetRenderers.Values)
            {
                var renderer = targetRenderer.Renderer;
                var pointer = targetRenderer.Pointer;
                if (HighlightedRenderers.TryGetValue(pointer, out var existing))
                {
                    existing.Renderer = renderer;
                    existing.Claims = targetRenderer.Claims;
                    continue;
                }

                try
                {
                    var baseline = new RendererBaseline(renderer.color, renderer.enabled);
                    HighlightedRenderers[pointer] = new HighlightedRenderer(
                        renderer,
                        pointer,
                        targetRenderer.Claims,
                        baseline.OriginalColor,
                        baseline.OriginalEnabled);
                    renderer.enabled = true;
                }
                catch
                {
                    // Ignore stale renderers; they will be dropped on the next scan.
                }
            }

            _status = matchedControllerCount == 0
                ? $"target missing; controllers={controllerCount}; locked={lockedControllerCount}; {sourceStatus}; cookers={DescribeCookerTargets(targetSet)}"
                : $"active; controllers={controllerCount}; locked={lockedControllerCount}; matched={matchedControllerCount}; renderers={HighlightedRenderers.Count}; {sourceStatus}; cookers={DescribeCookerTargets(targetSet)}";
        }
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

    private static void PulseHighlightedRenderers(RuntimeUiTargetSetSnapshot targetSet)
    {
        List<HighlightedRenderer> renderers;
        lock (VisualRoot)
        {
            if (_suspended || _topologyMutationDepth > 0 || !IsTargetCurrent(targetSet)) return;
            renderers = HighlightedRenderers.Values.ToList();
        }

        foreach (var item in renderers)
        {
            if (Volatile.Read(ref _topologyMutationDepth) > 0 || !IsTargetCurrent(targetSet)) return;
            try
            {
                if (item.Renderer == null) continue;
                item.Renderer.enabled = true;
                item.Renderer.color = RuntimeTargetHighlightStyle.BuildCookerSpritePulseColor(
                    item.OriginalColor,
                    item.Claims,
                    targetSet.Palette,
                    Time.realtimeSinceStartup);
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

    private static int DecrementTopologyMutationDepth()
    {
        while (true)
        {
            var current = Volatile.Read(ref _topologyMutationDepth);
            if (current <= 0)
            {
                return 0;
            }

            var next = current - 1;
            if (Interlocked.CompareExchange(ref _topologyMutationDepth, next, current) == current)
            {
                return next;
            }
        }
    }

    private static void IncrementTopologyMutationDepth()
    {
        while (true)
        {
            var current = Volatile.Read(ref _topologyMutationDepth);
            if (current >= int.MaxValue)
            {
                return;
            }

            if (Interlocked.CompareExchange(
                    ref _topologyMutationDepth,
                    current + 1,
                    current) == current)
            {
                return;
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

    private static void RestoreRetainedBaselinesLocked(IEnumerable<SpriteRenderer> openRenderers)
    {
        if (RendererBaselines.Count == 0) return;

        var freshRenderers = new Dictionary<nint, SpriteRenderer>();
        foreach (var renderer in openRenderers)
        {
            if (renderer == null) continue;
            var pointer = ReadUnityObjectPointer(renderer);
            if (pointer != IntPtr.Zero) freshRenderers.TryAdd(pointer, renderer);
        }

        foreach (var pointer in RendererBaselines.Keys.ToList())
        {
            var baseline = RendererBaselines[pointer];
            if (freshRenderers.TryGetValue(pointer, out var renderer))
            {
                try
                {
                    renderer.color = baseline.OriginalColor;
                    renderer.enabled = baseline.OriginalEnabled;
                }
                catch
                {
                    // The fresh renderer may have been released after the directory scan.
                }
            }

            RendererBaselines.Remove(pointer);
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
            var pointer = ReadObjectPointer(item);
            if (pointer == IntPtr.Zero) continue;
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
                return IntPtr.Zero;
            }
        }

        return IntPtr.Zero;
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

        return IntPtr.Zero;
    }

    private static bool IsTargetCurrent(RuntimeUiTargetSetSnapshot targetSet)
    {
        return HasCookerHighlightTargets(targetSet) && IsTargetSnapshotCurrent(targetSet);
    }

    private static bool IsTargetSnapshotCurrent(RuntimeUiTargetSetSnapshot targetSet)
    {
        var lifecycle = RuntimeNightBusinessLifecycle.Snapshot;
        var desired = Volatile.Read(ref _desiredTargetSet);
        return lifecycle.IsActive
            && targetSet.SessionGeneration == lifecycle.Generation
            && ReferenceEquals(desired, targetSet);
    }

    private static bool HasCookerHighlightTargets(RuntimeUiTargetSetSnapshot targetSet)
    {
        return targetSet.Targets.Any(target =>
            target.CookerHighlightEnabled && target.CookerTypeId > 0);
    }

    private static string DescribeCookerTargets(RuntimeUiTargetSetSnapshot targetSet)
    {
        return string.Join(
            ",",
            targetSet.Targets
                .Where(target => target.CookerHighlightEnabled && target.CookerTypeId > 0)
                .Select(target => $"{target.Kind}:{target.CookerTypeId}"));
    }

    private static string NormalizeReason(string reason)
    {
        return string.IsNullOrWhiteSpace(reason) ? "night business unavailable" : reason.Trim();
    }

    private sealed class HighlightedRenderer
    {
        public HighlightedRenderer(
            SpriteRenderer renderer,
            nint pointer,
            RuntimeUiTargetKinds claims,
            Color originalColor,
            bool originalEnabled)
        {
            Renderer = renderer;
            Pointer = pointer;
            Claims = claims;
            OriginalColor = originalColor;
            OriginalEnabled = originalEnabled;
        }

        public SpriteRenderer Renderer { get; set; }
        public nint Pointer { get; }
        public RuntimeUiTargetKinds Claims { get; set; }
        public Color OriginalColor { get; }
        public bool OriginalEnabled { get; }
    }

    private sealed class TargetRenderer
    {
        public TargetRenderer(SpriteRenderer renderer, nint pointer, RuntimeUiTargetKinds claims)
        {
            Renderer = renderer;
            Pointer = pointer;
            Claims = claims;
        }

        public SpriteRenderer Renderer { get; }
        public nint Pointer { get; }
        public RuntimeUiTargetKinds Claims { get; set; }
    }

    private readonly record struct RendererBaseline(
        Color OriginalColor,
        bool OriginalEnabled);

}
