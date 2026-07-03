using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;

namespace MystiaStewardCompanion.Save;

/// <summary>
/// 捕获普通客人订单与其 <c>GuestGroupController</c> 的运行时绑定关系。
/// </summary>
/// <remarks>
/// HUD 的 <c>OrderController</c> 只能说明订单仍对玩家可见，不能保证订单仍有可执行的客人控制器。
/// 普客自动化送达、恢复耐心和评价都必须通过 <c>GuestGroupController</c>，因此这里在
/// <c>GuestGroupController.PushToOrder</c> 阶段记录真实归属，并在订单移除或评价后清理。
/// </remarks>
public static class NormalOrderRuntimeCapture
{
    private const string GuestGroupControllerTypeName = "NightScene.GuestManagementUtility.GuestGroupController";
    private const string GuestsManagerTypeName = "NightScene.GuestManagementUtility.GuestsManager";
    private const int MaxOrders = 64;

    private static readonly object SyncRoot = new();
    private static readonly List<CapturedRuntimeNormalOrder> Orders = new();
    private static readonly HashSet<string> PatchedMethods = new(StringComparer.Ordinal);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);
    private static Harmony? _harmony;
    private static ManualLogSource? _log;
    private static DateTime _lastAttachAttemptUtc = DateTime.MinValue;
    private static string _status = "not attached";
    private static long _changeVersion;
    private static int _addCallbacks;
    private static int _removeCallbacks;
    private static int _capturedOrders;
    private static int _parseFailures;
    private static string _lastCapture = "";
    private static string _lastParseFailure = "";

    /// <summary>
    /// 捕获记录变更版本号，供主线程刷新快照时做轻量变更检测。
    /// </summary>
    public static long ChangeVersion
    {
        get
        {
            lock (SyncRoot)
            {
                return _changeVersion;
            }
        }
    }

    /// <summary>
    /// 返回当前 Hook 安装和最近捕获状态，用于快照来源诊断。
    /// </summary>
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

    public static bool IsAttached
    {
        get
        {
            lock (SyncRoot)
            {
                return PatchedMethods.Count > 0;
            }
        }
    }

    /// <summary>
    /// 尝试安装普通订单生命周期 Hook。
    /// </summary>
    /// <param name="log">BepInEx 日志源，用于记录安装成功或等待游戏类型加载。</param>
    public static void Attach(ManualLogSource log)
    {
        _log = log;
        TryAttach(log, true);
    }

    /// <summary>
    /// 重置延迟重试时间，让下一次快照读取可以立刻重新尝试安装 Hook。
    /// </summary>
    public static void ResetAttachRetryDelay()
    {
        lock (SyncRoot)
        {
            _lastAttachAttemptUtc = DateTime.MinValue;
        }
    }

    /// <summary>
    /// 返回最近捕获且仍在保留窗口内的普客订单绑定快照。
    /// </summary>
    /// <param name="maxAge">最后一次捕获后允许保留的最长时间。</param>
    /// <returns>按首次捕获时间排序的捕获记录副本。</returns>
    public static IReadOnlyList<CapturedRuntimeNormalOrder> Snapshot(TimeSpan maxAge)
    {
        TryAttach(_log, false);
        var now = DateTime.UtcNow;
        lock (SyncRoot)
        {
            Orders.RemoveAll(order => now - order.CapturedAt > maxAge);
            return Orders
                .OrderBy(order => order.FirstCapturedAt)
                .ThenBy(order => order.CapturedAt)
                .ToList();
        }
    }

    /// <summary>
    /// 清空当前缓存的普客订单绑定。
    /// </summary>
    /// <param name="reason">清理原因，用于调试状态显示。</param>
    public static void ClearOrders(string reason)
    {
        lock (SyncRoot)
        {
            if (Orders.Count > 0) _changeVersion++;
            Orders.Clear();
            _lastCapture = $"cleared: {reason}";
            _status = BuildStatusLocked();
        }
    }

    private static void TryAttach(ManualLogSource? log, bool force)
    {
        lock (SyncRoot)
        {
            if (!force && DateTime.UtcNow - _lastAttachAttemptUtc < RetryInterval) return;
            _lastAttachAttemptUtc = DateTime.UtcNow;
        }

        var patchedNow = new List<string>();
        var missing = new List<string>();
        try
        {
            _harmony ??= new Harmony("com.tyukki.mystia-steward-companion.normal-order-runtime-capture");

            PatchMethod(_harmony, GuestGroupControllerTypeName, "PushToOrder", 1, false, nameof(OnControllerOrderAdded), null, patchedNow, missing);
            PatchMethod(_harmony, GuestsManagerTypeName, "RemoveFromOrder", 1, false, nameof(OnOrderRemoved), null, patchedNow, missing);
            PatchMethod(_harmony, GuestsManagerTypeName, "EvaluateOrder", 3, false, nameof(OnOrderEvaluating), null, patchedNow, missing);

            lock (SyncRoot)
            {
                _status = PatchedMethods.Count == 0
                    ? $"waiting: {string.Join(", ", missing.Take(4))}"
                    : BuildStatusLocked();
            }

            if (patchedNow.Count > 0)
            {
                log?.LogInfo($"Normal order runtime capture patched: {string.Join(", ", patchedNow)}.");
            }
            else if (force && PatchedMethods.Count == 0)
            {
                log?.LogWarning($"Normal order runtime capture waiting for game types: {string.Join(", ", missing.Take(4))}.");
            }
        }
        catch (Exception ex)
        {
            lock (SyncRoot)
            {
                _status = $"error: {ex.Message}";
            }

            log?.LogWarning($"Normal order runtime capture failed: {ex.Message}");
        }
    }

    private static void PatchMethod(
        Harmony harmony,
        string typeName,
        string methodName,
        int parameterCount,
        bool isStatic,
        string? prefixName,
        string? postfixName,
        ICollection<string> patchedNow,
        ICollection<string> missing)
    {
        var key = $"{typeName}.{methodName}/{parameterCount}/{(isStatic ? "static" : "instance")}";
        lock (SyncRoot)
        {
            if (PatchedMethods.Contains(key)) return;
        }

        var type = RuntimeReflectionUtility.FindType(typeName);
        if (type == null)
        {
            missing.Add(typeName);
            return;
        }

        var flags = BindingFlags.Public | BindingFlags.NonPublic | (isStatic ? BindingFlags.Static : BindingFlags.Instance);
        var target = type
            .GetMethods(flags)
            .FirstOrDefault(method => method.Name == methodName && method.GetParameters().Length == parameterCount);
        var prefix = prefixName == null ? null : typeof(NormalOrderRuntimeCapture).GetMethod(prefixName, BindingFlags.NonPublic | BindingFlags.Static);
        var postfix = postfixName == null ? null : typeof(NormalOrderRuntimeCapture).GetMethod(postfixName, BindingFlags.NonPublic | BindingFlags.Static);
        if (target == null || (prefixName != null && prefix == null) || (postfixName != null && postfix == null))
        {
            missing.Add(key);
            return;
        }

        harmony.Patch(
            target,
            prefix: prefix == null ? null : new HarmonyMethod(prefix),
            postfix: postfix == null ? null : new HarmonyMethod(postfix));
        lock (SyncRoot)
        {
            PatchedMethods.Add(key);
        }

        patchedNow.Add(key);
    }

    private static void OnControllerOrderAdded(object __instance, object __0)
    {
        lock (SyncRoot) _addCallbacks++;
        AddOrder(ParseOrder(__0, "ControllerOrderAdd", __instance));
    }

    private static void OnOrderRemoved(object __0)
    {
        lock (SyncRoot) _removeCallbacks++;
        RemoveOrder(ParseOrder(__0, "OrderRemove"));
    }

    private static void OnOrderEvaluating(object __0)
    {
        lock (SyncRoot) _removeCallbacks++;
        var order = ParseControllerCurrentOrder(__0, "EvaluateOrder");
        if (order != null) RemoveOrder(order);
    }

    private static CapturedRuntimeNormalOrder? ParseControllerCurrentOrder(object? controller, string source)
    {
        if (controller == null)
        {
            NoteParseFailure(source, "controller is null");
            return null;
        }

        var order = RuntimeReflectionUtility.InvokeMethod(controller, "PeekOrders");
        return ParseOrder(order, source, controller);
    }

    private static CapturedRuntimeNormalOrder? ParseOrder(object? order, string source, object? controller = null)
    {
        if (order == null)
        {
            NoteParseFailure(source, "order is null");
            return null;
        }

        if (!IsNormalOrder(order))
        {
            return null;
        }

        var requestFood = RuntimeReflectionUtility.GetMemberValue(order, "RequestFood")
            ?? RuntimeReflectionUtility.InvokeMethod(order, "get_RequestFood");
        var requestBeverage = RuntimeReflectionUtility.GetMemberValue(order, "RequestBeverage")
            ?? RuntimeReflectionUtility.InvokeMethod(order, "get_RequestBeverage");
        var foodId = ReadSellableId(requestFood, ReadFirstMember(order, "foodRequest", "FoodRequest", "requestFoodId", "RequestFoodId", "RequestFoodID"));
        var beverageId = ReadSellableId(requestBeverage, ReadFirstMember(order, "beverageRequest", "BeverageRequest", "requestBevId", "RequestBevId", "requestBeverageId", "RequestBeverageId", "RequestBeverageID"));
        var deskCode = RuntimeReflectionUtility.ToInt(
            RuntimeReflectionUtility.GetMemberValue(order, "DeskCode")
            ?? RuntimeReflectionUtility.GetMemberValue(controller, "DeskCode"),
            -1);
        if (deskCode < 0 || (foodId < 0 && beverageId < 0))
        {
            NoteParseFailure(source, $"missing key fields: desk={deskCode}, food={foodId}, beverage={beverageId}");
            return null;
        }

        var guest = RuntimeReflectionUtility.GetMemberValue(order, "Guest")
            ?? RuntimeReflectionUtility.InvokeMethod(order, "get_Guest")
            ?? RuntimeReflectionUtility.GetMemberValue(controller, "OrderingGuest");
        var guestName = ReadTextLikeValue(guest);
        var capturedAt = DateTime.UtcNow;
        return new CapturedRuntimeNormalOrder(
            RuntimeOrderKey(order),
            deskCode,
            string.IsNullOrWhiteSpace(guestName) ? "普客" : guestName,
            foodId,
            beverageId,
            capturedAt,
            capturedAt,
            source)
        {
            OrderObject = order,
            ControllerObject = controller,
        };
    }

    private static void AddOrder(CapturedRuntimeNormalOrder? order)
    {
        if (order == null) return;

        lock (SyncRoot)
        {
            var existing = Orders.Where(current => IsSameOrderSlot(current, order)).ToList();
            Orders.RemoveAll(current => IsSameOrderSlot(current, order));
            var next = existing.Aggregate(order, MergeCapturedOrder);
            Orders.Add(next);
            _capturedOrders++;
            _lastCapture = $"{next.CaptureSource}: desk={next.DeskCode + 1}, food={next.FoodId}, beverage={next.BeverageId}, obj={(next.OrderObject == null ? "no" : "yes")}/{(next.ControllerObject == null ? "no" : "yes")}";
            _changeVersion++;
            _status = BuildStatusLocked();
            if (Orders.Count > MaxOrders)
            {
                Orders.RemoveRange(0, Orders.Count - MaxOrders);
            }
        }
    }

    private static void RemoveOrder(CapturedRuntimeNormalOrder? order)
    {
        if (order == null) return;

        lock (SyncRoot)
        {
            var removed = Orders.RemoveAll(existing => IsSameOrderSlot(existing, order));
            _lastCapture = $"removed: desk={order.DeskCode + 1}, food={order.FoodId}, beverage={order.BeverageId}";
            if (removed > 0) _changeVersion++;
            _status = BuildStatusLocked();
        }
    }

    private static CapturedRuntimeNormalOrder MergeCapturedOrder(
        CapturedRuntimeNormalOrder incoming,
        CapturedRuntimeNormalOrder existing)
    {
        return incoming with
        {
            GuestName = string.IsNullOrWhiteSpace(incoming.GuestName) || string.Equals(incoming.GuestName, "普客", StringComparison.Ordinal)
                ? existing.GuestName
                : incoming.GuestName,
            FirstCapturedAt = existing.FirstCapturedAt < incoming.FirstCapturedAt ? existing.FirstCapturedAt : incoming.FirstCapturedAt,
            RuntimeKey = string.IsNullOrWhiteSpace(incoming.RuntimeKey) ? existing.RuntimeKey : incoming.RuntimeKey,
            CaptureSource = MergeCaptureSource(existing.CaptureSource, incoming.CaptureSource),
            OrderObject = incoming.OrderObject ?? existing.OrderObject,
            ControllerObject = incoming.ControllerObject ?? existing.ControllerObject,
        };
    }

    private static bool IsSameOrderSlot(CapturedRuntimeNormalOrder left, CapturedRuntimeNormalOrder right)
    {
        if (!string.IsNullOrWhiteSpace(left.RuntimeKey)
            && !string.IsNullOrWhiteSpace(right.RuntimeKey))
        {
            return string.Equals(left.RuntimeKey, right.RuntimeKey, StringComparison.Ordinal);
        }

        return left.DeskCode == right.DeskCode
            && left.FoodId == right.FoodId
            && left.BeverageId == right.BeverageId;
    }

    private static bool IsNormalOrder(object order)
    {
        var typeName = order.GetType().Name;
        if (typeName.IndexOf("SpecialOrder", StringComparison.OrdinalIgnoreCase) >= 0) return false;
        if (typeName.IndexOf("NormalOrder", StringComparison.OrdinalIgnoreCase) >= 0) return true;

        var type = RuntimeReflectionUtility.GetMemberValue(order, "Type")
            ?? RuntimeReflectionUtility.InvokeMethod(order, "get_Type");
        var typeText = type?.ToString();
        if (typeText?.Contains("Special", StringComparison.OrdinalIgnoreCase) == true) return false;
        return typeText?.Contains("Normal", StringComparison.OrdinalIgnoreCase) == true
            || RuntimeReflectionUtility.ToInt(type, -1) == 0;
    }

    private static object? ReadFirstMember(object? instance, params string[] names)
    {
        foreach (var name in names)
        {
            var value = RuntimeReflectionUtility.GetMemberValue(instance, name);
            if (value != null) return value;
        }

        return null;
    }

    private static int ReadSellableId(object? sellable, object? fallback)
    {
        var value = RuntimeReflectionUtility.InvokeMethod(sellable, "get_id")
            ?? RuntimeReflectionUtility.InvokeMethod(sellable, "get_Id")
            ?? RuntimeReflectionUtility.GetMemberValue(sellable, "id")
            ?? RuntimeReflectionUtility.GetMemberValue(sellable, "Id")
            ?? fallback;
        return RuntimeReflectionUtility.ToInt(value, -1);
    }

    private static string ReadTextLikeValue(object? value)
    {
        if (value == null) return "";
        foreach (var name in new[] { "Name", "name", "LocalizedName", "Text", "text", "StringId", "stringId" })
        {
            var member = RuntimeReflectionUtility.GetMemberValue(value, name);
            if (member is string text && IsReadableText(text)) return text.Trim();
        }

        var fallback = value.ToString();
        return IsReadableText(fallback) ? fallback!.Trim() : "";
    }

    private static bool IsReadableText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (value.Contains("GameData.", StringComparison.Ordinal)) return false;
        if (value.Contains("NightScene.", StringComparison.Ordinal)) return false;
        if (value.Contains("Il2Cpp", StringComparison.Ordinal)) return false;
        return true;
    }

    private static string RuntimeOrderKey(object order)
    {
        try
        {
            return $"ptr:{RuntimeReflectionUtility.ReadObjectPointer(order):x}";
        }
        catch
        {
            return "";
        }
    }

    private static string MergeCaptureSource(string existing, string incoming)
    {
        if (string.IsNullOrWhiteSpace(existing)) return incoming;
        if (string.IsNullOrWhiteSpace(incoming)) return existing;

        var parts = existing
            .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Concat(incoming.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return string.Join("+", parts);
    }

    private static void NoteParseFailure(string source, string reason)
    {
        lock (SyncRoot)
        {
            _parseFailures++;
            _lastParseFailure = $"{source}: {reason}";
            _status = BuildStatusLocked();
        }
    }

    private static string BuildStatusLocked()
    {
        return $"patched={PatchedMethods.Count}; active={Orders.Count}; captured={_capturedOrders}; add={_addCallbacks}; remove={_removeCallbacks}; parseFailures={_parseFailures}; last={RuntimeReflectionUtility.Trim(_lastCapture, 120)}; lastFailure={RuntimeReflectionUtility.Trim(_lastParseFailure, 120)}";
    }
}

/// <summary>
/// 一条从游戏运行时捕获到的普通客人订单绑定。
/// </summary>
/// <remarks>
/// 运行时对象引用只在 Mod 内部用于重新定位可执行订单，不会序列化给前端。
/// </remarks>
public sealed record CapturedRuntimeNormalOrder(
    string RuntimeKey,
    int DeskCode,
    string GuestName,
    int FoodId,
    int BeverageId,
    DateTime FirstCapturedAt,
    DateTime CapturedAt,
    string CaptureSource)
{
    internal object? OrderObject { get; init; }
    internal object? ControllerObject { get; init; }
}
