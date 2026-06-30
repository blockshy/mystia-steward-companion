using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;

namespace MystiaStewardCompanion.Save;

/// <summary>
/// 捕获游戏运行时产生的稀客订单，并为本地 API 和自动上菜流程提供可复用的订单快照。
/// </summary>
/// <remarks>
/// 游戏没有稳定的“当前稀客订单列表”公开接口，因此这里通过 Harmony 监听订单生成、加入、移除和状态更新等关键点。
/// 捕获结果只保存在内存中，并通过运行时对象指针、桌号、稀客和 Tag 信息合并多次回调，避免同一订单在不同 Hook 中重复出现。
/// </remarks>
public static class SpecialOrderRuntimeCapture
{
    private const string GuestsManagerTypeName = "NightScene.GuestManagementUtility.GuestsManager";
    private const string SpecialOrderTypeName = "NightScene.GuestManagementUtility.GuestsManager+SpecialOrder";
    private const string GuestGroupControllerTypeName = "NightScene.GuestManagementUtility.GuestGroupController";
    private const string SpecialGuestsControllerTypeName = "NightScene.GuestManagementUtility.SpecialGuestsController";
    private const string OrderControllerTypeName = "Night.UI.HUD.Ordering.OrderController";
    private const string PartnerManagerTypeName = "NightScene.PartnerUtility.PartnerManager";
    // 只保留最近的运行时订单，避免长时间经营或 Hook 异常时诊断缓存无限增长。
    private const int MaxOrders = 32;

    private static readonly object SyncRoot = new();
    private static readonly List<CapturedRuntimeSpecialOrder> Orders = new();
    private static readonly List<RuntimeParseFailureDiagnostic> RecentParseFailures = new();
    private static readonly HashSet<string> PatchedMethods = new(StringComparer.Ordinal);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);
    private static Harmony? _harmony;
    private static ManualLogSource? _log;
    private static DateTime _lastAttachAttemptUtc = DateTime.MinValue;
    private static string _status = "not attached";
    private static int _addCallbacks;
    private static int _removeCallbacks;
    private static int _generatedCallbacks;
    private static int _statusCallbacks;
    private static int _capturedOrders;
    private static int _parseFailures;
    private static long _changeVersion;
    private static string _lastCapture = "";
    private static string _lastParseFailure = "";
    private static string _lastOrderShape = "";

    /// <summary>
    /// 捕获结果的变更版本号，供快照轮询方判断稀客订单是否需要重新发布。
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
    /// 返回当前 Hook 安装和解析状态，主要用于调试信息与本地 API 诊断。
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

    /// <summary>
    /// 尝试安装稀客订单相关的 Harmony Patch。
    /// </summary>
    /// <param name="log">用于输出首次安装、等待游戏类型加载或安装失败的 BepInEx 日志。</param>
    public static void Attach(ManualLogSource log)
    {
        _log = log;
        TryAttach(log, true);
    }

    /// <summary>
    /// 重置延迟重试时间，让下一次快照读取可以立刻尝试重新安装 Hook。
    /// </summary>
    public static void ResetAttachRetryDelay()
    {
        lock (SyncRoot)
        {
            _lastAttachAttemptUtc = DateTime.MinValue;
        }
    }

    /// <summary>
    /// 返回最近捕获到且未过期的稀客订单快照。
    /// </summary>
    /// <param name="maxAge">订单最后一次被捕获后允许保留的最长时间。</param>
    /// <returns>按首次捕获时间排序的订单副本，调用方可以安全枚举。</returns>
    /// <remarks>
    /// 读取快照时会顺带触发一次非强制 Attach。这样可以覆盖玩家进入经营场景后游戏类型才加载完成的情况，
    /// 但通过重试间隔避免每轮本地 API 轮询都反复扫描 AppDomain。
    /// </remarks>
    public static IReadOnlyList<CapturedRuntimeSpecialOrder> Snapshot(TimeSpan maxAge)
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
    /// 根据前端操作主动从捕获列表中移除一个订单。
    /// </summary>
    /// <param name="deskCode">游戏桌号，未知时为负数。</param>
    /// <param name="guestId">稀客运行时 ID，未知时为 <c>null</c>。</param>
    /// <param name="guestName">稀客名称，作为 ID 缺失时的辅助匹配条件。</param>
    /// <param name="foodTagId">料理 Tag ID，未指定时使用 <see cref="int.MinValue"/>。</param>
    /// <param name="beverageTagId">酒水 Tag ID，未指定时使用 <see cref="int.MinValue"/>。</param>
    /// <returns>被移除的捕获记录数量。</returns>
    public static int DismissOrder(int deskCode, int? guestId, string guestName, int foodTagId, int beverageTagId)
    {
        lock (SyncRoot)
        {
            var removed = Orders.RemoveAll(order => IsDismissRequestMatch(order, deskCode, guestId, guestName, foodTagId, beverageTagId));
            _lastCapture = $"dismissed: desk={deskCode}, guestId={guestId?.ToString() ?? ""}, foodTagId={foodTagId}, bevTagId={beverageTagId}";
            if (removed > 0)
            {
                _changeVersion++;
            }

            _status = BuildStatusLocked();
            return removed;
        }
    }

    /// <summary>
    /// 清空内存中的稀客订单捕获结果。
    /// </summary>
    /// <param name="reason">记录到诊断状态中的清理原因。</param>
    public static void ClearOrders(string reason)
    {
        lock (SyncRoot)
        {
            if (Orders.Count == 0) return;
            Orders.Clear();
            _lastCapture = $"cleared: {reason}";
            _changeVersion++;
            _status = BuildStatusLocked();
        }
    }

    /// <summary>
    /// 返回近期订单解析失败诊断，帮助确认游戏版本字段变化或 Hook 参数形态变化。
    /// </summary>
    /// <param name="maxAge">诊断记录允许保留的最长时间。</param>
    /// <param name="limit">返回的最多记录数。</param>
    /// <returns>按时间倒序排列的简短诊断文本。</returns>
    public static IReadOnlyList<string> RecentParseFailuresSnapshot(TimeSpan maxAge, int limit = 16)
    {
        var now = DateTime.UtcNow;
        lock (SyncRoot)
        {
            PruneRecentParseFailuresLocked(now, maxAge);
            return RecentParseFailures
                .OrderByDescending(failure => failure.CapturedAtUtc)
                .Take(limit)
                .Select(failure => $"{failure.CapturedAtUtc:O}; {failure.Message}")
                .ToList();
        }
    }

    /// <summary>
    /// 安装所有已知稀客订单生命周期 Hook。
    /// </summary>
    /// <remarks>
    /// 不同 DLC、普通订单和稀客手动订单会走不同游戏入口。这里一次性尝试多个方法，并允许部分缺失，
    /// 以便当前游戏版本只要暴露其中一条链路就能捕获订单。
    /// </remarks>
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
            _harmony ??= new Harmony("com.tyukki.mystia-steward-companion.special-order-runtime-capture");

            PatchMethod(_harmony, GuestGroupControllerTypeName, "PushToOrder", 1, false, nameof(OnControllerOrderAdded), null, patchedNow, missing);
            PatchMethod(_harmony, SpecialGuestsControllerTypeName, "PostGenerateOrder", 2, false, null, nameof(OnGeneratedSpecialOrder), patchedNow, missing);
            PatchMethod(_harmony, GuestsManagerTypeName, "SetManualControllerOrderInternal", 3, false, null, nameof(OnManualControllerOrderSet), patchedNow, missing);
            PatchMethod(_harmony, GuestsManagerTypeName, "EvaulateManualOrder", 2, false, nameof(OnManualOrderEvaluating), null, patchedNow, missing);
            PatchMethod(_harmony, GuestsManagerTypeName, "EndDlc4SpecialManualOrder", 1, false, nameof(OnManualOrderEnded), null, patchedNow, missing);
            PatchMethod(_harmony, GuestsManagerTypeName, "AddToOrder", 1, false, nameof(OnOrderAdded), null, patchedNow, missing);
            PatchMethod(_harmony, GuestsManagerTypeName, "RemoveFromOrder", 1, false, nameof(OnOrderRemoved), null, patchedNow, missing);
            PatchMethod(_harmony, OrderControllerTypeName, "AddOrder", 1, true, nameof(OnOrderAdded), null, patchedNow, missing);
            PatchMethod(_harmony, PartnerManagerTypeName, "OnOrderBaseAdd", 1, false, nameof(OnOrderAdded), null, patchedNow, missing);
            PatchMethod(_harmony, PartnerManagerTypeName, "OnOrderBaseStatusUpdate", 3, false, null, nameof(OnOrderStatusUpdated), patchedNow, missing);
            PatchMethod(_harmony, PartnerManagerTypeName, "NotifySystemChanged", 4, false, null, nameof(OnOrderSystemChanged), patchedNow, missing);

            lock (SyncRoot)
            {
                _status = PatchedMethods.Count == 0
                    ? $"waiting: {string.Join(", ", missing.Take(4))}"
                    : BuildStatusLocked();
            }

            if (patchedNow.Count > 0)
            {
                log?.LogInfo($"Special order runtime capture patched: {string.Join(", ", patchedNow)}.");
            }
            else if (force && PatchedMethods.Count == 0)
            {
                log?.LogWarning($"Special order runtime capture waiting for game types: {string.Join(", ", missing.Take(4))}.");
            }
        }
        catch (Exception ex)
        {
            lock (SyncRoot)
            {
                _status = $"error: {ex.Message}";
            }

            log?.LogWarning($"Special order runtime capture failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 为一个 IL2CPP 类型方法安装 Harmony Prefix 或 Postfix。
    /// </summary>
    /// <param name="harmony">当前捕获服务使用的 Harmony 实例。</param>
    /// <param name="typeName">游戏运行时类型全名。</param>
    /// <param name="methodName">需要监听的方法名。</param>
    /// <param name="parameterCount">用于区分重载的方法参数数量。</param>
    /// <param name="isStatic">目标方法是否为静态方法。</param>
    /// <param name="prefixName">本类中 Prefix 回调方法名，未使用时为 <c>null</c>。</param>
    /// <param name="postfixName">本类中 Postfix 回调方法名，未使用时为 <c>null</c>。</param>
    /// <param name="patchedNow">本次新安装成功的方法列表。</param>
    /// <param name="missing">当前游戏域尚未找到的方法或类型列表。</param>
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

        var type = FindType(typeName);
        if (type == null)
        {
            missing.Add(typeName);
            return;
        }

        var flags = BindingFlags.Public | BindingFlags.NonPublic | (isStatic ? BindingFlags.Static : BindingFlags.Instance);
        var target = type
            .GetMethods(flags)
            .FirstOrDefault(method => method.Name == methodName && method.GetParameters().Length == parameterCount);
        var prefix = prefixName == null ? null : typeof(SpecialOrderRuntimeCapture).GetMethod(prefixName, BindingFlags.NonPublic | BindingFlags.Static);
        var postfix = postfixName == null ? null : typeof(SpecialOrderRuntimeCapture).GetMethod(postfixName, BindingFlags.NonPublic | BindingFlags.Static);
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

    private static void OnOrderAdded(object __0)
    {
        lock (SyncRoot) _addCallbacks++;
        AddOrder(ParseOrder(__0, "OrderAdd"));
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

    private static void OnOrderStatusUpdated(object __0, object __1)
    {
        lock (SyncRoot) _statusCallbacks++;
        UpdateOrderStatus(ParseOrder(__0, "OrderStatusUpdate"), __1);
    }

    private static void OnOrderSystemChanged(object __0, object __1, object __2)
    {
        lock (SyncRoot) _statusCallbacks++;
        UpdateOrderStatus(ParseOrder(__2, "OrderSystemChanged"), __1);
    }

    private static void OnGeneratedSpecialOrder(object __instance, object __result)
    {
        lock (SyncRoot) _generatedCallbacks++;
        AddOrder(ParseOrder(__result, "PostGenerateOrder", __instance));
    }

    private static void OnManualControllerOrderSet(object __0, object __2)
    {
        lock (SyncRoot) _generatedCallbacks++;
        AddOrder(ParseOrder(__2, "ManualOrderSet", __0));
    }

    private static void OnManualOrderEvaluating(object __0)
    {
        lock (SyncRoot) _statusCallbacks++;
        var order = ParseControllerCurrentOrder(__0, "ManualOrderEvaluate");
        if (order is { IsFulfilled: true })
        {
            RemoveOrder(order with { CaptureSource = "ManualOrderEvaluate" });
        }
    }

    private static void OnManualOrderEnded(object __0)
    {
        lock (SyncRoot) _removeCallbacks++;
        var order = ParseControllerCurrentOrder(__0, "ManualOrderEnd");
        if (order is { IsFulfilled: true })
        {
            RemoveOrder(order with { CaptureSource = "ManualOrderEnd" });
        }
    }

    /// <summary>
    /// 合并一条新捕获的订单记录。
    /// </summary>
    /// <remarks>
    /// 同一稀客订单可能先由生成 Hook 捕获到 Tag，又由控制器 Hook 捕获到桌号或 guest 对象。
    /// 因此这里按运行时对象、桌号和稀客维度合并，并优先保留信息更完整的一份记录。
    /// </remarks>
    private static void AddOrder(CapturedRuntimeSpecialOrder? order)
    {
        if (order == null) return;

        lock (SyncRoot)
        {
            var existing = Orders.Where(current => CanMergeCapturedOrders(current, order)).ToList();
            Orders.RemoveAll(current => CanMergeCapturedOrders(current, order));

            var next = existing.Aggregate(order, MergeCapturedOrder);
            Orders.Add(next);
            _capturedOrders++;
            _lastCapture = $"{next.CaptureSource}: desk={next.DeskCode}, guestId={next.GuestId?.ToString() ?? ""}, food={next.FoodTag}({next.FoodTagId}), bev={next.BeverageTag}({next.BeverageTagId})";
            _changeVersion++;
            _status = BuildStatusLocked();
            if (Orders.Count > MaxOrders)
            {
                Orders.RemoveRange(0, Orders.Count - MaxOrders);
            }
        }
    }

    /// <summary>
    /// 根据游戏移除、评价完成或手动结束回调移除订单。
    /// </summary>
    private static void RemoveOrder(CapturedRuntimeSpecialOrder? order)
    {
        if (order == null) return;

        lock (SyncRoot)
        {
            var removed = Orders.RemoveAll(existing => IsSameOrderRemovalMatch(existing, order));
            _lastCapture = $"removed: desk={order.DeskCode}, guestId={order.GuestId?.ToString() ?? ""}";
            if (removed > 0) _changeVersion++;
            _status = BuildStatusLocked();
        }
    }

    /// <summary>
    /// 处理订单状态更新回调，只在明确移除或已完成送达时清理捕获记录。
    /// </summary>
    /// <remarks>
    /// 游戏部分状态更新只表示 UI 或伙伴系统刷新，不代表订单结束。这里保守识别完成上下文，避免订单刚生成后被误删。
    /// </remarks>
    private static void UpdateOrderStatus(CapturedRuntimeSpecialOrder? order, object? context)
    {
        if (order == null) return;

        var contextName = FormatValue(context);
        if (string.Equals(contextName, "OrderRemove", StringComparison.OrdinalIgnoreCase)
            || string.Equals(contextName, "2", StringComparison.Ordinal))
        {
            RemoveOrder(order with { CaptureSource = "OrderRemove" });
            return;
        }

        if (!IsOrderDeliveryContext(contextName)) return;
        if (!order.IsFulfilled) return;

        RemoveOrder(order with { CaptureSource = "OrderFulfilled" });
    }

    private static bool IsOrderDeliveryContext(string contextName)
    {
        return string.Equals(contextName, "FoodDelivered", StringComparison.OrdinalIgnoreCase)
            || string.Equals(contextName, "BeverageDelivered", StringComparison.OrdinalIgnoreCase)
            || string.Equals(contextName, "3", StringComparison.Ordinal)
            || string.Equals(contextName, "4", StringComparison.Ordinal);
    }

    /// <summary>
    /// 从运行时订单对象和可选控制器中解析稀客、桌号、料理 Tag 与酒水 Tag。
    /// </summary>
    /// <param name="order">游戏订单对象，可能是 IL2CPP 基类或具体 SpecialOrder。</param>
    /// <param name="source">触发解析的 Hook 名称，用于诊断。</param>
    /// <param name="controller">订单所属控制器，部分字段只能从控制器读取。</param>
    /// <returns>解析成功时返回标准捕获记录；非稀客订单或字段不足时返回 <c>null</c>。</returns>
    private static CapturedRuntimeSpecialOrder? ParseOrder(object? order, string source, object? controller = null)
    {
        if (order == null)
        {
            NoteParseFailure(source, "order is null");
            return null;
        }

        var readableOrder = TryCastOrder(order, SpecialOrderTypeName) ?? order;
        var textParts = ParseOrderText(SafeToString(readableOrder));
        var orderTypeValue = GetMemberValue(readableOrder, "Type");
        var orderType = FormatValue(orderTypeValue);
        var isManualSpecialOrder = IsManualSpecialOrder(readableOrder, controller);
        if (!IsSpecialOrderType(orderTypeValue, orderType)
            && !string.Equals(textParts.OrderType, "Special", StringComparison.OrdinalIgnoreCase)
            && readableOrder.GetType().Name.IndexOf("SpecialOrder", StringComparison.OrdinalIgnoreCase) < 0
            && !textParts.LooksLikeSpecialOrder
            && !isManualSpecialOrder)
        {
            NoteParseFailure(source, $"not special: {order.GetType().FullName}", readableOrder, textParts);
            return null;
        }

        var specialGuest = GetMemberValue(readableOrder, "SpecialGuests")
            ?? GetMemberValue(controller, "SpecialGuest")
            ?? GetMemberValue(controller, "OrderingGuest");
        var foodTagId = ToNullableInt(GetMemberValue(readableOrder, "RequestFoodTag"));
        var beverageTagId = NormalizeBeverageTagId(ToNullableInt(GetMemberValue(readableOrder, "RequestBeverageTag")));
        var deskCode = ToNullableInt(GetMemberValue(readableOrder, "DeskCode"))
            ?? ToNullableInt(GetMemberValue(controller, "DeskCode"))
            ?? textParts.DeskCode
            ?? -1;
        return ParseSpecialOrderParts(
            specialGuest,
            foodTagId,
            textParts.FoodTag,
            beverageTagId,
            textParts.BeverageTag,
            textParts.GuestName,
            deskCode,
            readableOrder,
            controller,
            source);
    }

    private static CapturedRuntimeSpecialOrder? ParseSpecialOrderParts(
        object? specialGuest,
        int? foodTagId,
        string textFoodTag,
        int? beverageTagId,
        string textBeverageTag,
        string textGuestName,
        int deskCode,
        object? order,
        object? controller,
        string source)
    {
        var guestId = ToNullableInt(GetMemberValue(specialGuest, "Id"));
        var foodTag = NormalizeTag(textFoodTag);
        if (string.IsNullOrWhiteSpace(foodTag) && !foodTagId.HasValue)
        {
            foodTag = NormalizeTag(InvokeInstanceMethod(controller, "GetOrderFoodText", order)?.ToString());
        }

        if (string.IsNullOrWhiteSpace(foodTag) && !foodTagId.HasValue)
        {
            var fallbackFoodTagId = ToNullableInt(GetMemberValue(order, "foodRequest"));
            if (fallbackFoodTagId.HasValue)
            {
                foodTag = NormalizeTag(InvokeInstanceMethod(specialGuest, "GetFoodTagText", fallbackFoodTagId.Value)?.ToString());
            }
        }

        var beverageTag = NormalizeTag(textBeverageTag);
        if (string.IsNullOrWhiteSpace(beverageTag))
        {
            beverageTag = NormalizeTag(InvokeInstanceMethod(controller, "GetOrderBevText", order)?.ToString());
        }

        if (string.IsNullOrWhiteSpace(beverageTag) && beverageTagId.HasValue && beverageTagId.Value >= 0)
        {
            beverageTag = NormalizeTag(InvokeInstanceMethod(specialGuest, "GetBevTagText", beverageTagId.Value)?.ToString());
        }

        var guestName = NormalizeGuestName(textGuestName);
        if (string.IsNullOrWhiteSpace(guestName) && specialGuest != null)
        {
            guestName = ReadGuestName(specialGuest, guestId);
        }

        if (!foodTagId.HasValue
            && !beverageTagId.HasValue
            && string.IsNullOrWhiteSpace(foodTag)
            && string.IsNullOrWhiteSpace(beverageTag))
        {
            NoteParseFailure(source, "empty food/beverage tag", order);
            return null;
        }

        if (string.IsNullOrWhiteSpace(guestName) && specialGuest == null)
        {
            NoteParseFailure(source, "special guest missing", order);
            return null;
        }

        var capturedAt = DateTime.UtcNow;
        return new CapturedRuntimeSpecialOrder(
            deskCode,
            guestId,
            string.IsNullOrWhiteSpace(guestName) ? "Special guest" : guestName,
            foodTagId ?? 0,
            foodTagId.HasValue,
            foodTag,
            beverageTagId ?? 0,
            beverageTagId.HasValue,
            beverageTag,
            IsOrderFulfilled(order),
            capturedAt,
            capturedAt,
            GetRuntimeObjectKey(order),
            source)
        {
            OrderObject = order,
            ControllerObject = controller,
        };
    }

    /// <summary>
    /// 在订单对象不可直接读取时，从控制器当前订单或控制器自身字段构造移除匹配记录。
    /// </summary>
    /// <remarks>
    /// 手动稀客订单结束时，游戏回调里不一定仍能拿到完整订单对象。这个降级记录只用于移除匹配，
    /// 因此允许缺失 Tag，但必须至少能确定稀客或桌号。
    /// </remarks>
    private static CapturedRuntimeSpecialOrder? ParseControllerCurrentOrder(object? controller, string source)
    {
        if (controller == null)
        {
            NoteParseFailure(source, "controller is null");
            return null;
        }

        var peekOrder = InvokeInstanceMethod(controller, "PeekOrders");
        if (peekOrder != null)
        {
            var parsed = ParseOrder(peekOrder, source, controller);
            if (parsed != null) return parsed;
        }

        var removal = BuildControllerRemovalOrder(controller, source);
        if (removal != null) return removal;

        NoteParseFailure(source, "controller order missing", controller);
        return null;
    }

    private static CapturedRuntimeSpecialOrder? BuildControllerRemovalOrder(object? controller, string source)
    {
        if (controller == null) return null;

        var specialGuest = GetMemberValue(controller, "SpecialGuest")
            ?? GetMemberValue(controller, "OrderingGuest");
        var guestId = ToNullableInt(GetMemberValue(specialGuest, "Id"));
        var guestName = specialGuest == null ? "" : ReadGuestName(specialGuest, guestId);
        if (string.IsNullOrWhiteSpace(guestName) && !guestId.HasValue) return null;

        var deskCode = ToNullableInt(GetMemberValue(controller, "DeskCode")) ?? -1;
        var capturedAt = DateTime.UtcNow;
        return new CapturedRuntimeSpecialOrder(
            deskCode,
            guestId,
            string.IsNullOrWhiteSpace(guestName) ? "Special guest" : guestName,
            0,
            false,
            "",
            0,
            false,
            "",
            false,
            capturedAt,
            capturedAt,
            "",
            source)
        {
            ControllerObject = controller,
        };
    }

    /// <summary>
    /// 判断两个捕获记录是否指向同一个订单槽位。
    /// </summary>
    /// <remarks>
    /// 运行时对象指针最可靠；指针不可用时退化为桌号与稀客信息匹配，避免不同桌的同名订单被合并。
    /// </remarks>
    private static bool IsSameOrderSlot(CapturedRuntimeSpecialOrder left, CapturedRuntimeSpecialOrder right)
    {
        if (!string.IsNullOrWhiteSpace(left.RuntimeKey)
            && !string.IsNullOrWhiteSpace(right.RuntimeKey)
            && string.Equals(left.RuntimeKey, right.RuntimeKey, StringComparison.Ordinal))
        {
            return true;
        }

        if (left.DeskCode >= 0 && right.DeskCode >= 0 && left.DeskCode != right.DeskCode) return false;
        if (left.GuestId.HasValue && right.GuestId.HasValue) return left.GuestId.Value == right.GuestId.Value;
        return string.Equals(left.GuestName, right.GuestName, StringComparison.Ordinal);
    }

    private static bool CanMergeCapturedOrders(CapturedRuntimeSpecialOrder left, CapturedRuntimeSpecialOrder right)
    {
        return IsSameOrderSlot(left, right) && CanMergeCapturedOrderDetails(left, right);
    }

    private static bool IsSameOrderRemovalMatch(CapturedRuntimeSpecialOrder existing, CapturedRuntimeSpecialOrder removed)
    {
        if (!string.IsNullOrWhiteSpace(existing.RuntimeKey)
            && !string.IsNullOrWhiteSpace(removed.RuntimeKey))
        {
            return string.Equals(existing.RuntimeKey, removed.RuntimeKey, StringComparison.Ordinal);
        }

        var removedHasDetails = HasAnyOrderDetail(removed);
        if (!removedHasDetails) return false;

        if (!IsSameOrderSlot(existing, removed)) return false;

        return CanMergeCapturedOrderDetails(existing, removed);
    }

    private static bool IsDismissRequestMatch(
        CapturedRuntimeSpecialOrder existing,
        int deskCode,
        int? guestId,
        string guestName,
        int foodTagId,
        int beverageTagId)
    {
        if (deskCode >= 0 && existing.DeskCode >= 0 && existing.DeskCode != deskCode) return false;

        var guestMatches = false;
        if (guestId.HasValue && existing.GuestId.HasValue && existing.GuestId.Value == guestId.Value)
        {
            guestMatches = true;
        }

        if (!guestMatches
            && !string.IsNullOrWhiteSpace(guestName)
            && string.Equals(existing.GuestName, guestName, StringComparison.Ordinal))
        {
            guestMatches = true;
        }

        var requestedFoodTag = foodTagId != int.MinValue;
        var requestedBeverageTag = beverageTagId != int.MinValue;
        var foodMatches = !requestedFoodTag
            || (existing.HasFoodTagId && existing.FoodTagId == foodTagId);
        var beverageMatches = !requestedBeverageTag
            || (existing.HasBeverageTagId && existing.BeverageTagId == beverageTagId);
        var detailsMatch = foodMatches && beverageMatches && (requestedFoodTag || requestedBeverageTag);

        if (detailsMatch) return true;
        return guestMatches && deskCode >= 0;
    }

    private static bool HasAnyOrderDetail(CapturedRuntimeSpecialOrder order)
    {
        return order.HasFoodTagId
            || order.HasBeverageTagId
            || !string.IsNullOrWhiteSpace(order.FoodTag)
            || !string.IsNullOrWhiteSpace(order.BeverageTag);
    }

    /// <summary>
    /// 合并两次 Hook 捕获到的同一订单信息。
    /// </summary>
    /// <remarks>
    /// 不同来源的记录可能分别拥有文本 Tag、数值 Tag、稀客 ID 或控制器对象。合并时保留更完整字段，
    /// 但如果料理或酒水 Tag 明显冲突，就不把两条记录视为同一订单。
    /// </remarks>
    private static CapturedRuntimeSpecialOrder MergeCapturedOrder(
        CapturedRuntimeSpecialOrder incoming,
        CapturedRuntimeSpecialOrder existing)
    {
        if (!CanMergeCapturedOrderDetails(incoming, existing))
        {
            return GetCapturedOrderCompletenessScore(incoming) >= GetCapturedOrderCompletenessScore(existing)
                ? incoming
                : existing with { CapturedAt = incoming.CapturedAt };
        }

        var food = SelectTagParts(
            incoming.FoodTagId,
            incoming.HasFoodTagId,
            incoming.FoodTag,
            existing.FoodTagId,
            existing.HasFoodTagId,
            existing.FoodTag);
        var beverage = SelectTagParts(
            incoming.BeverageTagId,
            incoming.HasBeverageTagId,
            incoming.BeverageTag,
            existing.BeverageTagId,
            existing.HasBeverageTagId,
            existing.BeverageTag);

        return incoming with
        {
            GuestId = incoming.GuestId ?? existing.GuestId,
            GuestName = string.IsNullOrWhiteSpace(incoming.GuestName) || string.Equals(incoming.GuestName, "Special guest", StringComparison.Ordinal)
                ? existing.GuestName
                : incoming.GuestName,
            FoodTagId = food.TagId,
            HasFoodTagId = food.HasTagId,
            FoodTag = food.Tag,
            BeverageTagId = beverage.TagId,
            HasBeverageTagId = beverage.HasTagId,
            BeverageTag = beverage.Tag,
            IsFulfilled = incoming.IsFulfilled || existing.IsFulfilled,
            FirstCapturedAt = existing.FirstCapturedAt < incoming.FirstCapturedAt ? existing.FirstCapturedAt : incoming.FirstCapturedAt,
            RuntimeKey = string.IsNullOrWhiteSpace(incoming.RuntimeKey) ? existing.RuntimeKey : incoming.RuntimeKey,
            CaptureSource = MergeCaptureSource(existing.CaptureSource, incoming.CaptureSource),
            OrderObject = incoming.OrderObject ?? existing.OrderObject,
            ControllerObject = incoming.ControllerObject ?? existing.ControllerObject,
        };
    }

    private static bool CanMergeCapturedOrderDetails(CapturedRuntimeSpecialOrder left, CapturedRuntimeSpecialOrder right)
    {
        if (!string.IsNullOrWhiteSpace(left.RuntimeKey)
            && !string.IsNullOrWhiteSpace(right.RuntimeKey)
            && string.Equals(left.RuntimeKey, right.RuntimeKey, StringComparison.Ordinal))
        {
            return true;
        }

        if (HaveConflictingTags(left.FoodTag, left.HasFoodTagId, left.FoodTagId, right.FoodTag, right.HasFoodTagId, right.FoodTagId))
        {
            return false;
        }

        if (HaveConflictingTags(left.BeverageTag, left.HasBeverageTagId, left.BeverageTagId, right.BeverageTag, right.HasBeverageTagId, right.BeverageTagId))
        {
            return false;
        }

        return true;
    }

    private static bool HaveConflictingTags(
        string leftTag,
        bool leftHasTagId,
        int leftTagId,
        string rightTag,
        bool rightHasTagId,
        int rightTagId)
    {
        if (!string.IsNullOrWhiteSpace(leftTag)
            && !string.IsNullOrWhiteSpace(rightTag)
            && !string.Equals(leftTag, rightTag, StringComparison.Ordinal))
        {
            return true;
        }

        return leftHasTagId && rightHasTagId && leftTagId != rightTagId;
    }

    private static (int TagId, bool HasTagId, string Tag) SelectTagParts(
        int incomingTagId,
        bool incomingHasTagId,
        string incomingTag,
        int existingTagId,
        bool existingHasTagId,
        string existingTag)
    {
        var incomingScore = GetTagCompletenessScore(incomingHasTagId, incomingTag);
        var existingScore = GetTagCompletenessScore(existingHasTagId, existingTag);
        return incomingScore >= existingScore
            ? (incomingTagId, incomingHasTagId, incomingTag)
            : (existingTagId, existingHasTagId, existingTag);
    }

    private static int GetCapturedOrderCompletenessScore(CapturedRuntimeSpecialOrder order)
    {
        return GetTagCompletenessScore(order.HasFoodTagId, order.FoodTag)
            + GetTagCompletenessScore(order.HasBeverageTagId, order.BeverageTag)
            + (order.GuestId.HasValue ? 2 : 0)
            + (order.DeskCode >= 0 ? 1 : 0);
    }

    private static int GetTagCompletenessScore(bool hasTagId, string tag)
    {
        return (!string.IsNullOrWhiteSpace(tag) ? 8 : 0) + (hasTagId ? 2 : 0);
    }

    private static string MergeCaptureSource(string existing, string incoming)
    {
        if (string.IsNullOrWhiteSpace(existing)) return incoming;
        if (string.IsNullOrWhiteSpace(incoming)) return existing;
        if (string.Equals(existing, incoming, StringComparison.Ordinal)) return incoming;
        return $"{existing}+{incoming}";
    }

    private static bool IsOrderFulfilled(object? order)
    {
        var value = GetMemberValue(order, "IsFullfilled");
        if (value is bool boolValue) return boolValue;
        return string.Equals(value?.ToString(), "true", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsManualSpecialOrder(object? order, object? controller)
    {
        if (!ToBool(GetMemberValue(order, "ManualOrder"))) return false;

        var specialGuest = GetMemberValue(order, "SpecialGuests")
            ?? GetMemberValue(controller, "SpecialGuest")
            ?? GetMemberValue(controller, "OrderingGuest");
        return IsExplicitSpecialGuest(specialGuest) || ToNullableInt(GetMemberValue(specialGuest, "Id")).HasValue;
    }

    private static bool IsExplicitSpecialGuest(object? guest)
    {
        var typeName = guest?.GetType().FullName ?? "";
        return typeName.IndexOf("NightSceneUtility.SpecialGuest", StringComparison.OrdinalIgnoreCase) >= 0
            || typeName.IndexOf("NightSceneUtility.MappedSpecialGuest", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool ToBool(object? value)
    {
        if (value is bool boolValue) return boolValue;
        return string.Equals(value?.ToString(), "true", StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadGuestName(object specialGuest, int? guestId)
    {
        var stringId = GetMemberValue(specialGuest, "StringId")?.ToString();
        if (!string.IsNullOrWhiteSpace(stringId)) return stringId;
        return guestId.HasValue ? $"Guest {guestId.Value}" : "Special guest";
    }

    private static string NormalizeTag(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";

        var trimmed = value.Trim();
        if (string.Equals(trimmed, "Null", StringComparison.OrdinalIgnoreCase)) return "";
        if (string.Equals(trimmed, "None", StringComparison.OrdinalIgnoreCase)) return "";
        return trimmed.StartsWith("#", StringComparison.Ordinal) ? "" : trimmed;
    }

    private static object? InvokeInstanceMethod(object? instance, string name, params object?[] args)
    {
        if (instance == null) return null;

        var method = instance
            .GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(candidate => candidate.Name == name && candidate.GetParameters().Length == args.Length);
        if (method == null) return null;

        try
        {
            return method.Invoke(instance, args);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 兼容读取 IL2CPP 自动属性、私有字段和常见字段命名风格。
    /// </summary>
    /// <remarks>
    /// Il2CppInterop 暴露的对象在不同版本或反编译形态下可能出现 <c>m_</c>、下划线和 backing field 等命名。
    /// 统一在这里收敛字段候选，避免业务解析逻辑里散落多套反射访问。
    /// </remarks>
    private static object? GetMemberValue(object? instance, string name)
    {
        if (instance == null) return null;
        var type = instance.GetType();

        while (type != null)
        {
            foreach (var fieldName in BuildFieldNameCandidates(name))
            {
                var field = type.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (TryReadField(instance, field, out var fieldValue) && fieldValue != null) return fieldValue;
            }

            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (TryReadProperty(instance, property, out var propertyValue)) return propertyValue;

            var pascalName = char.ToUpperInvariant(name[0]) + name[1..];
            property = type.GetProperty(pascalName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (TryReadProperty(instance, property, out propertyValue)) return propertyValue;

            type = type.BaseType;
        }

        return null;
    }

    /// <summary>
    /// 尝试将 IL2CPP 基类对象转换为指定的游戏订单类型。
    /// </summary>
    /// <remarks>
    /// Harmony 回调参数有时是基类或接口视角，直接反射字段会缺少子类成员。通过 TryCast 后再读取可提高解析成功率。
    /// </remarks>
    private static object? TryCastOrder(object? order, string targetTypeName)
    {
        if (order is not Il2CppObjectBase il2CppObject) return null;

        var targetType = FindType(targetTypeName);
        if (targetType == null) return null;
        if (order.GetType() == targetType) return order;

        var tryCast = typeof(Il2CppObjectBase)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(method => method.Name == "TryCast"
                && method.IsGenericMethodDefinition
                && method.GetParameters().Length == 0);
        if (tryCast == null) return null;

        try
        {
            return tryCast.MakeGenericMethod(targetType).Invoke(il2CppObject, Array.Empty<object?>());
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 记录一次订单解析失败，并保存经过截断的对象形态诊断。
    /// </summary>
    /// <remarks>
    /// 解析失败不抛出异常，避免 Hook 影响游戏原始流程；诊断通过本地 API 暴露给调试面板。
    /// </remarks>
    private static void NoteParseFailure(string source, string reason, object? order = null, ParsedOrderText? textParts = null)
    {
        var shape = DescribeOrderShape(order, textParts);
        lock (SyncRoot)
        {
            _parseFailures++;
            _lastParseFailure = $"{source}: {reason}";
            _lastOrderShape = shape;
            AddRecentParseFailureLocked(source, reason, shape);
            _status = BuildStatusLocked();
        }
    }

    private static void AddRecentParseFailureLocked(string source, string reason, string shape)
    {
        RecentParseFailures.Add(new RuntimeParseFailureDiagnostic(
            DateTime.UtcNow,
            TrimStatus($"{source}: {reason}; {shape}", 900)));
        PruneRecentParseFailuresLocked(DateTime.UtcNow, TimeSpan.FromMinutes(5));
        if (RecentParseFailures.Count > 40)
        {
            RecentParseFailures.RemoveRange(0, RecentParseFailures.Count - 40);
        }
    }

    private static void PruneRecentParseFailuresLocked(DateTime nowUtc, TimeSpan maxAge)
    {
        RecentParseFailures.RemoveAll(failure => nowUtc - failure.CapturedAtUtc > maxAge);
    }

    private static string BuildStatusLocked()
    {
        return $"patched={PatchedMethods.Count}; version={_changeVersion}; callbacks=add:{_addCallbacks},remove:{_removeCallbacks},generated:{_generatedCallbacks},statusUpdate:{_statusCallbacks}; captured={_capturedOrders}; parseFailures={_parseFailures}; lastCapture={_lastCapture}; lastParseFailure={_lastParseFailure}; lastOrderShape={_lastOrderShape}";
    }

    private static ParsedOrderText ParseOrderText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return ParsedOrderText.Empty;

        var lines = text.Replace('\r', '\n').Split('\n');
        return new ParsedOrderText(
            ReadLabeledInt(lines, "DeskCode"),
            ReadLabeledValue(lines, "OrderType"),
            ReadLabeledValue(lines, "ReqFoodTag"),
            ReadLabeledValue(lines, "ReqBevTag"),
            ReadLabeledValue(lines, "Guest"));
    }

    private static int? ReadLabeledInt(IReadOnlyList<string> lines, string label)
    {
        var value = ReadLabeledValue(lines, label);
        return int.TryParse(value, out var parsed) ? parsed : null;
    }

    private static string ReadLabeledValue(IReadOnlyList<string> lines, string label)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            if (!line.StartsWith(label, StringComparison.OrdinalIgnoreCase)) continue;

            var sameLineValue = NormalizeLabelValue(line[label.Length..]);
            if (!string.IsNullOrWhiteSpace(sameLineValue)) return sameLineValue;

            for (var j = i + 1; j < lines.Count; j++)
            {
                var candidateLine = lines[j].Trim();
                if (IsOrderTextFieldLine(candidateLine)) break;

                var candidate = NormalizeTag(candidateLine);
                if (!string.IsNullOrWhiteSpace(candidate)) return candidate;
            }
        }

        return "";
    }

    private static string NormalizeLabelValue(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith(":", StringComparison.Ordinal)) trimmed = trimmed[1..].Trim();
        return NormalizeTag(trimmed);
    }

    private static bool IsOrderTextFieldLine(string value)
    {
        if (value.Length == 0) return false;
        if (value.Length > 8 && value.All(c => c == '/' || char.IsWhiteSpace(c))) return true;

        return value.StartsWith("DeskCode:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("OrderType:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("ServFood:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("ServBev:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("Price:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("IsFreeOrder?", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("ReqFoodTag:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("ReqBevTag:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("Guest:", StringComparison.OrdinalIgnoreCase);
    }

    private static string SafeToString(object? value)
    {
        if (value == null) return "";

        try
        {
            return value.ToString() ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static string NormalizeGuestName(string? value)
    {
        var normalized = NormalizeTag(value);
        return string.IsNullOrWhiteSpace(normalized) ? "" : normalized;
    }

    private static bool IsSpecialOrderType(object? value, string formatted)
    {
        if (string.Equals(formatted, "Special", StringComparison.OrdinalIgnoreCase)) return true;
        return ToNullableInt(value) == 1;
    }

    private static string DescribeOrderShape(object? order, ParsedOrderText? textParts)
    {
        if (order == null) return "";

        var typedOrder = TryCastOrder(order, SpecialOrderTypeName);
        var readableOrder = typedOrder ?? order;
        var text = textParts ?? ParseOrderText(SafeToString(readableOrder));
        var parts = new List<string>
        {
            $"type={order.GetType().FullName}",
            $"cast={typedOrder?.GetType().FullName ?? ""}",
            $"Type={ShortValue(GetMemberValue(readableOrder, "Type"))}",
            $"DeskCode={ShortValue(GetMemberValue(readableOrder, "DeskCode"))}",
            $"RequestFoodTag={ShortValue(GetMemberValue(readableOrder, "RequestFoodTag"))}",
            $"RequestBeverageTag={ShortValue(GetMemberValue(readableOrder, "RequestBeverageTag"))}",
            $"SpecialGuests={ShortValue(GetMemberValue(readableOrder, "SpecialGuests"))}",
            $"text={text.ToDiagnosticString()}",
        };

        return TrimStatus(string.Join("|", parts), 700);
    }

    private static string ShortValue(object? value)
    {
        return TrimStatus(FormatValue(value), 80);
    }

    private static string FormatValue(object? value)
    {
        if (value == null) return "null";
        if (value is string stringValue) return stringValue;
        var type = value.GetType();
        if (type.IsEnum || type.IsPrimitive || value is decimal) return value.ToString() ?? "";
        try
        {
            if (value is IConvertible convertible) return convertible.ToInt32(null).ToString();
        }
        catch
        {
            // 无法转换为数字时继续使用类型名，避免诊断文本因为异常中断。
        }

        return type.FullName ?? type.Name;
    }

    private static string TrimStatus(string value, int maxLength)
    {
        if (value.Length <= maxLength) return value;
        return value[..Math.Max(0, maxLength - 3)] + "...";
    }

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

    private static bool TryReadProperty(object? instance, PropertyInfo? property, out object? value)
    {
        value = null;
        if (property == null) return false;

        try
        {
            value = property.GetValue(instance);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadField(object? instance, FieldInfo? field, out object? value)
    {
        value = null;
        if (field == null) return false;

        try
        {
            value = field.GetValue(instance);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string GetRuntimeObjectKey(object? value)
    {
        var pointer = GetMemberValue(value, "Pointer");
        if (pointer is IntPtr intPtr && intPtr != IntPtr.Zero) return $"ptr:{intPtr.ToInt64():x}";
        return "";
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
                // 某些程序集解析无关 IL2CPP 类型时会抛异常，查找目标类型时直接跳过即可。
            }
        }

        return null;
    }

    private static int? ToNullableInt(object? value)
    {
        if (value == null) return null;
        if (value is int intValue) return intValue;
        if (value is long longValue) return (int)longValue;
        if (value is short shortValue) return shortValue;
        try
        {
            if (value is IConvertible convertible) return convertible.ToInt32(null);
        }
        catch
        {
            // 无法按 IConvertible 读取时继续尝试字符串解析。
        }

        return int.TryParse(value.ToString(), out var parsed) ? parsed : null;
    }

    private static int ToInt(object? value)
    {
        return ToNullableInt(value) ?? 0;
    }

    private static int? NormalizeBeverageTagId(int? value)
    {
        return value.HasValue && value.Value >= 0 ? value : null;
    }
}

internal readonly record struct ParsedOrderText(
    int? DeskCode,
    string OrderType,
    string FoodTag,
    string BeverageTag,
    string GuestName)
{
    public static ParsedOrderText Empty { get; } = new(null, "", "", "", "");

    public bool LooksLikeSpecialOrder =>
        !string.IsNullOrWhiteSpace(GuestName)
        && (!string.IsNullOrWhiteSpace(FoodTag) || !string.IsNullOrWhiteSpace(BeverageTag));

    public string ToDiagnosticString()
    {
        return $"desk={DeskCode?.ToString() ?? ""},type={OrderType},food={FoodTag},bev={BeverageTag},guest={GuestName}";
    }
}

/// <summary>
/// 一条从游戏运行时捕获到的稀客订单。
/// </summary>
/// <remarks>
/// 公开字段用于 JSON 快照和自动上菜匹配；运行时对象引用仅在 Mod 内部用于再次定位订单，不会序列化给前端。
/// </remarks>
public sealed record CapturedRuntimeSpecialOrder(
    int DeskCode,
    int? GuestId,
    string GuestName,
    int FoodTagId,
    bool HasFoodTagId,
    string FoodTag,
    int BeverageTagId,
    bool HasBeverageTagId,
    string BeverageTag,
    bool IsFulfilled,
    DateTime FirstCapturedAt,
    DateTime CapturedAt,
    string RuntimeKey,
    string CaptureSource)
{
    internal object? OrderObject { get; init; }
    internal object? ControllerObject { get; init; }
}

/// <summary>
/// 稀客订单 Hook 解析失败的简短诊断记录。
/// </summary>
internal sealed record RuntimeParseFailureDiagnostic(
    DateTime CapturedAtUtc,
    string Message);
