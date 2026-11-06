using System.Reflection;
using UnityEngine;

namespace MystiaStewardCompanion.Save;

/// <summary>
/// 在厨具摆放区的原生交互背景形状内绘制多订单分色。目标集逐笔保存订单声明，显示仅合并同色；
/// 不改写交互图块身份、厨具、蒸汽或成品，也不按截图比例推算独立标记的位置。
/// </summary>
internal static class RuntimeCookerHighlightService
{
    private const float ScanIntervalSeconds = 1.25f;
    private static readonly object VisualRoot = new();
    private static readonly Dictionary<string, ActiveVisual> ActiveVisuals = new(StringComparer.Ordinal);
    private static readonly List<string> StaleKeys = new();
    // 仅存 Mod 自有视觉；非主线程关闭时延迟到下一次主线程 Tick 释放 Unity 资源。
    private static readonly List<RuntimeCookerColorBlocks> PendingDisposals = new();
    private static RuntimeUiTargetSetSnapshot _desiredTargetSet = RuntimeUiTargetSetSnapshot.Disabled;
    private static long _appliedTargetGeneration;
    private static bool _suspended = true;
    private static int _topologyMutationDepth;
    private static float _nextScanAt;
    private static string _status = "disabled";

    public static string Status
    {
        get
        {
            var desired = Volatile.Read(ref _desiredTargetSet);
            lock (VisualRoot)
                return $"{_status}; desired={desired.Generation}/session:{desired.SessionGeneration}; applied={_appliedTargetGeneration}; cookers={ActiveVisuals.Count}; suspended={_suspended}; topologyMutationDepth={_topologyMutationDepth}";
        }
    }

    /// <summary>发布不可变托管数据；后台线程不读取或创建 Unity 对象。</summary>
    public static void UpdateTargets(RuntimeUiTargetSetSnapshot targetSet)
    {
        ArgumentNullException.ThrowIfNull(targetSet);
        Volatile.Write(ref _desiredTargetSet, targetSet);
    }

    public static void Tick()
    {
        var lifecycle = RuntimeNightBusinessLifecycle.Snapshot;
        if (Environment.CurrentManagedThreadId != lifecycle.ThreadId) return;
        var desired = Volatile.Read(ref _desiredTargetSet);
        lock (VisualRoot)
        {
            DrainDisposalsLocked();
            if (_suspended || _topologyMutationDepth > 0) return;
            if (!lifecycle.IsActive || desired.SessionGeneration != lifecycle.Generation || !HasCookerHighlightTargets(desired))
            {
                ReleaseAllLocked();
                _status = "disabled";
                return;
            }
            if (_appliedTargetGeneration != desired.Generation)
            {
                // 刷新目标只触发原位核对；不销毁仍指向同一桌格的色块，避免刷新时闪烁和对象抖动。
                _appliedTargetGeneration = desired.Generation;
                _nextScanAt = 0f;
            }
            StaleKeys.Clear();
            foreach (var pair in ActiveVisuals)
                if (!pair.Value.Blocks.IsAlive) StaleKeys.Add(pair.Key);
            foreach (var key in StaleKeys) ReleaseVisualLocked(key);
        }
        // 普通帧只有存活检查，不分配集合、不修改渲染属性；扫描沿用既有低频目录检查。
        if (Time.realtimeSinceStartup >= _nextScanAt) ScanAndApply(desired);
    }

    public static void Suspend(string reason)
    {
        lock (VisualRoot)
        {
            _suspended = true;
            _topologyMutationDepth = 0;
            ReleaseAllLocked();
            _nextScanAt = 0f;
            _status = "suspended: " + reason;
        }
    }

    public static void Resume(string reason)
    {
        lock (VisualRoot)
        {
            ReleaseAllLocked();
            _suspended = false;
            _topologyMutationDepth = 0;
            _nextScanAt = 0f;
            _status = "waiting for main-thread reconcile: " + reason;
        }
    }

    /// <summary>场景已销毁时也仅处理 Mod 自有资源，不再解引用旧厨具包装。</summary>
    public static void Abandon(string reason) => Suspend(reason);

    /// <summary>原生锁定/拆换厨具前立即隐藏自有色块；不把任何旧厨具包装带过对象池变更。</summary>
    public static void BeginTopologyMutation(string reason)
    {
        Interlocked.Increment(ref _topologyMutationDepth);
        try
        {
            lock (VisualRoot)
            {
                ReleaseAllLocked();
                _nextScanAt = 0f;
                _status = "topology changing: " + reason;
            }
        }
        catch { /* 前置屏障失败时保持 depth > 0，不能把异常传入原生游戏。 */ }
    }

    public static void CompleteTopologyMutation(string reason)
    {
        try
        {
            lock (VisualRoot)
            {
                if (_topologyMutationDepth > 0) _topologyMutationDepth--;
                if (_topologyMutationDepth == 0)
                {
                    _nextScanAt = 0f;
                    _status = "topology changed: " + reason;
                }
            }
        }
        catch { Interlocked.CompareExchange(ref _topologyMutationDepth, 1, 0); }
    }

    private static void ScanAndApply(RuntimeUiTargetSetSnapshot targetSet)
    {
        lock (VisualRoot)
        {
            if (_suspended || _topologyMutationDepth > 0 || !IsCurrent(targetSet)) return;
            _nextScanAt = Time.realtimeSinceStartup + ScanIntervalSeconds;
        }
        try
        {
            // 锁表必须先于目录和实例属性读取：尤魔技能可能正在拆卸/替换厨具对象。
            if (!RuntimeCookerReflection.TryReadLockedCookerPositions(out var lockedPositions, out var lockStatus))
                throw new InvalidOperationException(lockStatus);
            var cookSystem = RuntimeCookerReflection.GetCookSystemManager()
                ?? throw new InvalidOperationException("cooker system unavailable");
            if (!RuntimeCookerReflection.TryReadCookerControllerEntriesFromCookSystem(cookSystem, lockedPositions, out var entries, out var directoryStatus))
                throw new InvalidOperationException(directoryStatus);
            var grid = ReadReferenceGrid();
            var candidates = new Dictionary<string, Candidate>(StringComparer.Ordinal);
            var rejected = new List<string>();
            foreach (var entry in entries)
            {
                if (lockedPositions.Contains(entry.GridPosition)) continue;
                if (!RuntimeCookerReflection.TryReadCookerControllerState(entry.Controller, out var state, out var stateStatus))
                    throw new InvalidOperationException(stateStatus);
                if (!state.CouldOpen) throw new InvalidOperationException("unclassified unavailable cooker");
                if (state.IsEmptyDesk) continue;
                var claims = RuntimeUiTargetKinds.None;
                foreach (var cookerTypeId in state.TypeIds) claims |= targetSet.GetCookerClaims(cookerTypeId);
                if (claims == RuntimeUiTargetKinds.None) continue;
                Candidate? candidate;
                try { candidate = ReadCookerAnchor(entry, grid, claims); }
                catch (Exception exception)
                {
                    // 单个桌格的美术模板或排序不受支持，只撤回这一处视觉；不让它牵连其他已确证厨具。
                    if (rejected.Count < 8) rejected.Add($"{entry.GridPosition.X},{entry.GridPosition.Y}: {exception.GetBaseException().Message}");
                    continue;
                }
                if (candidate == null) continue;
                if (!candidates.TryAdd(entry.ControllerIdentity, candidate))
                    throw new InvalidOperationException("ambiguous cooker identity");
            }
            lock (VisualRoot)
            {
                if (_suspended || _topologyMutationDepth > 0 || !IsCurrent(targetSet)) return;
                StaleKeys.Clear();
                foreach (var key in ActiveVisuals.Keys)
                    if (!candidates.ContainsKey(key)) StaleKeys.Add(key);
                foreach (var key in StaleKeys) ReleaseVisualLocked(key);
                foreach (var pair in candidates)
                {
                    var candidate = pair.Value;
                    try
                    {
                        if (ActiveVisuals.TryGetValue(pair.Key, out var active)
                            && (active.AnchorPointer != candidate.AnchorPointer || active.Session != targetSet.SessionGeneration || !active.Blocks.IsAlive))
                            ReleaseVisualLocked(pair.Key);
                        if (!ActiveVisuals.TryGetValue(pair.Key, out active))
                        {
                            var blocks = RuntimeCookerColorBlocks.Create(candidate.Anchor);
                            active = new ActiveVisual(blocks, candidate.AnchorPointer, targetSet.SessionGeneration);
                            ActiveVisuals.Add(pair.Key, active);
                        }
                        active.Blocks.Apply(candidate.Claims, targetSet.Palette, candidate.Surface);
                    }
                    catch (Exception exception)
                    {
                        // 显示资源或单锅网格失败只撤回该精确实例；目录有效时不能清空其它厨具。
                        // 遮罩增强失败另由 Blocks 使用原图安全回退，此处处理无法继续绘制的真实局部错误。
                        ReleaseVisualLocked(pair.Key);
                        if (rejected.Count < 8) rejected.Add($"{pair.Key}: {exception.GetBaseException().Message}");
                    }
                }
                // 仅在低频目录复核后更新诊断纯文本，不逐帧记录。下一轮实机日志可以确认实际图块与模板。
                _status = "active: cooker native placement bands; " + string.Join("; ", candidates
                    .Where(pair => ActiveVisuals.ContainsKey(pair.Key)).Select(pair =>
                {
                    var value = pair.Value;
                    return
                    $"sprite:{value.Surface.Sprite.Pointer:x}/template:{value.Surface.TemplatePointer:x}"
                    + $"/position:{value.Surface.Position.x:F3},{value.Surface.Position.y:F3},{value.Surface.Position.z:F3}"
                    + $"/sorting:{value.Surface.SortingLayer}:{value.Surface.SortingOrder}"
                    + $"/anchor:{value.AnchorPointer:x}/{value.Surface.SortingContext}"
                    + $"/{ActiveVisuals[pair.Key].Blocks.GeometryStatus}";
                }));
                if (rejected.Count > 0) _status += "; skipped=" + string.Join(" | ", rejected);
            }
        }
        catch (Exception exception)
        {
            // 目录、几何或对象身份不确定时只移除本 Mod 的视觉，绝不猜测新厨具或修改原材质。
            lock (VisualRoot) { ReleaseAllLocked(); _status = "unavailable: " + exception.GetBaseException().Message; }
        }
    }

    private static object ReadReferenceGrid()
    {
        var mapType = RuntimeReflectionUtility.FindType("NightScene.MapManager")
            ?? throw new InvalidOperationException("night map type unavailable");
        var map = RuntimeReflectionUtility.GetStaticMemberValue(mapType, "Instance")
            ?? throw new InvalidOperationException("night map unavailable");
        var grid = RuntimeReflectionUtility.GetMemberValue(map, "referenceGrid");
        if (grid?.GetType().FullName != "UnityEngine.Grid") throw new InvalidOperationException("reference grid unavailable");
        return grid;
    }

    /// <summary>
    /// Native CookSystemManager.Initialize（0x527FE0）把 referenceGrid.GetCellCenterWorld 的结果
    /// 传入 CookAnimator.Initialize（0x579C10），后者直接设置 visual.transform.position。
    /// metadata 与 #783 interop 同时确认 visual、m_CookerSpriteRenderer、referenceGrid 字段。
    /// 因而使用该已知中心及相邻格距，不用子节点枚举、图片边界或固定相机像素猜测桌格。
    /// </summary>
    private static Candidate? ReadCookerAnchor(RuntimeCookerControllerEntry entry, object grid, RuntimeUiTargetKinds claims)
    {
        var visual = RuntimeReflectionUtility.GetMemberValue(entry.Controller, "visual");
        if (visual?.GetType().FullName != "NightScene.UI.CookingUtility.CookAnimator" || visual is not Component component)
            throw new InvalidOperationException("cooker visual identity unavailable");
        var source = RuntimeReflectionUtility.GetMemberValue(visual, "m_CookerSpriteRenderer") as SpriteRenderer
            ?? throw new InvalidOperationException("cooker renderer identity unavailable");
        if (!source.transform.IsChildOf(component.transform))
            throw new InvalidOperationException("cooker renderer is outside its visual hierarchy");
        if (!source.enabled || !component.gameObject.activeInHierarchy || !source.gameObject.activeInHierarchy) return null;
        var anchor = component.transform;
        var method = grid.GetType().GetMethod("GetCellCenterWorld", BindingFlags.Instance | BindingFlags.Public,
            null, new[] { typeof(Vector3Int) }, null);
        if (method == null || method.ReturnType != typeof(Vector3)) throw new InvalidOperationException("grid geometry unavailable");
        Vector3 Center(int x, int y) => method.Invoke(grid, new object[] { new Vector3Int(x, y, entry.GridPosition.Z) }) is Vector3 center
            ? center : throw new InvalidOperationException("grid center unavailable");
        var center = Center(entry.GridPosition.X, entry.GridPosition.Y);
        var right = Center(checked(entry.GridPosition.X + 1), entry.GridPosition.Y);
        var up = Center(entry.GridPosition.X, checked(entry.GridPosition.Y + 1));
        var width = MathF.Abs(right.x - center.x);
        var height = MathF.Abs(up.y - center.y);
        var position = anchor.position;
        // 当前游戏桌格为 XY 平面的矩形网格；未来版本形状或锚点不匹配时停止显示，避免错误贴标。
        if (!Finite(center) || !Finite(right) || !Finite(up) || !Finite(position) || width <= 0 || height <= 0
            || MathF.Abs(right.y - center.y) > .001f || MathF.Abs(up.x - center.x) > .001f
            || MathF.Abs(right.z - center.z) > .001f || MathF.Abs(up.z - center.z) > .001f
            || MathF.Abs(position.x - center.x) > .001f || MathF.Abs(position.y - center.y) > .001f || MathF.Abs(position.z - center.z) > .001f)
            throw new InvalidOperationException("cooker grid anchor mismatch");
        // 网格位置验证使用 CookAnimator 根；实际背景挂载使用它明确持有的 SpriteRenderer。
        // 原资源中二者之间隔着动态 SortingGroup，挂根会丢失组排序；源图标还有(-0.5,-0.5)偏移，
        // 所以不能用源图标位置冒充桌格中心。Create 保留世界变换，Apply 再设置精确桌格位置。
        var renderAnchor = source.transform;
        if (!RuntimeReflectionUtility.TryReadNativeObjectPointer(renderAnchor, out var pointer) || pointer == 0)
            throw new InvalidOperationException("cooker renderer anchor identity unavailable");
        return new Candidate(renderAnchor, pointer, claims, RuntimeCookerBackgroundSurface.Read(entry.GridPosition, source));
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
    private static bool IsCurrent(RuntimeUiTargetSetSnapshot targetSet)
    {
        var lifecycle = RuntimeNightBusinessLifecycle.Snapshot;
        return lifecycle.IsActive && lifecycle.Generation == targetSet.SessionGeneration && ReferenceEquals(Volatile.Read(ref _desiredTargetSet), targetSet);
    }
    private static bool HasCookerHighlightTargets(RuntimeUiTargetSetSnapshot targetSet)
    {
        for (var index = 0; index < targetSet.Targets.Count; index++)
            if (targetSet.Targets[index].CookerHighlightEnabled) return true;
        return false;
    }

    private static void ReleaseVisualLocked(string key)
    {
        if (!ActiveVisuals.Remove(key, out var active)) return;
        if (Environment.CurrentManagedThreadId == RuntimeNightBusinessLifecycle.Snapshot.ThreadId) active.Blocks.Dispose();
        else PendingDisposals.Add(active.Blocks);
    }
    private static void ReleaseAllLocked()
    {
        var isMainThread = Environment.CurrentManagedThreadId == RuntimeNightBusinessLifecycle.Snapshot.ThreadId;
        foreach (var active in ActiveVisuals.Values)
            if (isMainThread) active.Blocks.Dispose(); else PendingDisposals.Add(active.Blocks);
        ActiveVisuals.Clear();
        StaleKeys.Clear();
        if (isMainThread) DrainDisposalsLocked();
    }
    private static void DrainDisposalsLocked()
    {
        foreach (var blocks in PendingDisposals) blocks.Dispose();
        PendingDisposals.Clear();
    }

    // 仅自有视觉及纯值身份跨帧保留。游戏 Transform 只在本次扫描 Candidate 的局部生命周期内存在。
    private sealed record ActiveVisual(RuntimeCookerColorBlocks Blocks, nint AnchorPointer, long Session);
    private sealed record Candidate(Transform Anchor, nint AnchorPointer, RuntimeUiTargetKinds Claims,
        RuntimeCookerBackgroundSurface Surface);
}
