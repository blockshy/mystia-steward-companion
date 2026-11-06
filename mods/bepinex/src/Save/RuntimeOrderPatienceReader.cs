using MystiaStewardCompanion.Core;

namespace MystiaStewardCompanion.Save;

/// <summary>
/// 主线程上的窄等待条采样。原生GuestGroupController.UpdatePatient使用CurrentPatient/MaxPatient
/// 推送百分比，GuestTableDisplayer再将它映射到桌位条；不根据时间、桌号或客人名称猜测。
/// 本类不调用SetPatient/AddPatient，不持有跨帧wrapper，不改变订单捕获版本、生命周期或参与状态。
/// </summary>
internal static class RuntimeOrderPatienceReader
{
    private static readonly TimeSpan CapturedOrderMaxAge = TimeSpan.FromHours(6);

    /// <summary>
    /// 从已核实的订单/控制器读取，再复核同一活动生命周期，防止对象池复用或读取中切场。
    /// HUD没有精确控制器时返回未知；只接受实际Int32/Boolean属性，不将字符串或缺失值默认为零。
    /// </summary>
    internal static double? Read(object? order, object? controller, RuntimeOrderKind kind, long expectedLifecycle)
    {
        if (order == null || controller == null || expectedLifecycle <= 0) return null;
        var before = RuntimeNightBusinessLifecycle.Snapshot;
        if (!before.IsActive || !RuntimeReflectionUtility.TryReadNativeObjectPointer(order, out var orderPointer)
            || !RuntimeReflectionUtility.TryReadNativeObjectPointer(controller, out var controllerPointer)
            || !RuntimeOrderTerminalReceiptStore.TryCaptureActiveLifecycle(before.Generation, kind, orderPointer,
                controllerPointer, out var sequence) || sequence != expectedLifecycle) return null;
        try
        {
            var value = ReadVerifiedValues(order, controller, RuntimeReflectionUtility.GetMemberValue);
            var after = RuntimeNightBusinessLifecycle.Snapshot;
            if (!after.IsActive || after.Generation != before.Generation
                || !RuntimeOrderTerminalReceiptStore.TryCaptureActiveLifecycle(before.Generation, kind, orderPointer,
                    controllerPointer, out sequence) || sequence != expectedLifecycle) return null;
            return value;
        }
        catch { return null; }
    }

    /// <summary>
    /// 原生AddToPatientCountdown为在桌客人设置等待条回调；RemoveFromPatientCountdown清空回调并隐藏条。
    /// IsOrdering仅证明仍在桌，因此必须再验证等待条回调和当前订单尚未送齐。委托只检查非空，不调用或保留。
    /// 抽出纯读取边界供离线测试；生产入口在调用前后始终另行核验精确原生生命周期。
    /// </summary>
    internal static double? ReadVerifiedValues(object order, object controller, Func<object?, string, object?> read)
    {
        try
        {
            if (read(controller, "IsOrdering") is not true || read(order, "IsFullfilled") is not false
                || read(controller, "OnPatientUpdatedCallback") == null) return null;
            return Ratio(read(controller, "CurrentPatient"), read(controller, "MaxPatient"));
        }
        catch { return null; }
    }

    /// <summary>异常数值不夹到0或1，以免把未知错误变成自动调度的低耐心条件。</summary>
    internal static double? Ratio(object? currentValue, object? maximumValue)
        => currentValue is int current && maximumValue is int maximum
            && maximum > 0 && current >= 0 && current <= maximum ? current / (double)maximum : null;

    /// <summary>
    /// 稳定经营上下文可以跨数秒复用，但耐心不能。仅枚举已有捕获记录，按唯一活动生命周期
    /// 匹配当前DTO，再读三个控制器属性；不全场景扫描、不刷新候选、不借用旧比例填补失败。
    /// 调用方应在周期快照或自动恢复策略的主线程采样点调用，不能每帧调用。
    /// </summary>
    internal static NightBusinessContext? Refresh(NightBusinessContext? context)
    {
        if (context == null || context.Orders.Count == 0) return context;
        var values = new Dictionary<long, double?>();
        try
        {
            if (context.OrdersReadComplete && string.IsNullOrEmpty(context.Error) && SpecialOrderRuntimeCapture.IsBusinessReady)
            {
                foreach (var group in SpecialOrderRuntimeCapture.Snapshot(CapturedOrderMaxAge)
                    .Where(order => order.OrderLifecycleSequence > 0).GroupBy(order => order.OrderLifecycleSequence))
                {
                    var matches = group.Take(2).ToArray();
                    if (matches.Length != 1) continue;
                    var captured = matches[0];
                    values[group.Key] = Read(captured.OrderObject, captured.ControllerObject, RuntimeOrderKind.Special, group.Key);
                }
            }
        }
        // 捕获入口不可用时撤销本轮全部观察；不能让外层异常恢复路径沿用上次低耐心值。
        catch { values.Clear(); }
        return Project(context, values);
    }

    /// <summary>普客使用相同精确采样规则；普通订单形态的稀客分身也由其实际Normal生命周期联结。</summary>
    internal static NormalBusinessContext? Refresh(NormalBusinessContext? context)
    {
        if (context == null || context.Orders.Count == 0) return context;
        var values = new Dictionary<long, double?>();
        try
        {
            if (context.OrdersReadComplete && string.IsNullOrEmpty(context.Error) && NormalOrderRuntimeCapture.IsBusinessReady)
            {
                foreach (var group in NormalOrderRuntimeCapture.Snapshot(CapturedOrderMaxAge)
                    .Where(order => order.OrderLifecycleSequence > 0).GroupBy(order => order.OrderLifecycleSequence))
                {
                    var matches = group.Take(2).ToArray();
                    if (matches.Length != 1) continue;
                    var captured = matches[0];
                    values[group.Key] = Read(captured.OrderObject, captured.ControllerObject, RuntimeOrderKind.Normal, group.Key);
                }
            }
        }
        catch { values.Clear(); }
        return Project(context, values);
    }

    /// <summary>同一类的活动生命周期由终态仓全局唯一分配；DTO自身重复身份时不附加任何观察值。</summary>
    internal static NightBusinessContext Project(NightBusinessContext context, IReadOnlyDictionary<long, double?> values)
    {
        var duplicates = context.Orders.GroupBy(order => order.OrderLifecycleSequence).Where(group => group.Count() != 1)
            .Select(group => group.Key).ToHashSet();
        var changed = false;
        var orders = context.Orders.Select(order => {
            var ratio = context.OrdersReadComplete && string.IsNullOrEmpty(context.Error) && order.OrderLifecycleSequence > 0
                && !duplicates.Contains(order.OrderLifecycleSequence) && !(order.HasServedFood && order.HasServedBeverage)
                && values.TryGetValue(order.OrderLifecycleSequence, out var value) && IsRatio(value) ? value : null;
            if (ratio == order.PatienceRemainingRatio) return order;
            changed = true;
            return Copy(order, ratio);
        }).ToList();
        return !changed ? context : new NightBusinessContext {
            Place = context.Place, PlaceLabel = context.PlaceLabel, Orders = orders, ActiveRareGuests = context.ActiveRareGuests,
            ActiveRareGuestsReadComplete = context.ActiveRareGuestsReadComplete, OrdersReadComplete = context.OrdersReadComplete,
            Source = context.Source, Error = context.Error,
        };
    }

    internal static NormalBusinessContext Project(NormalBusinessContext context, IReadOnlyDictionary<long, double?> values)
    {
        var duplicates = context.Orders.GroupBy(order => order.OrderLifecycleSequence).Where(group => group.Count() != 1)
            .Select(group => group.Key).ToHashSet();
        var changed = false;
        var orders = context.Orders.Select(order => {
            var ratio = context.OrdersReadComplete && string.IsNullOrEmpty(context.Error) && order.OrderLifecycleSequence > 0
                && !duplicates.Contains(order.OrderLifecycleSequence) && !order.HasEvaluated && !order.ReadyToEvaluate
                && !(order.HasServedFood && order.HasServedBeverage)
                && values.TryGetValue(order.OrderLifecycleSequence, out var value) && IsRatio(value) ? value : null;
            if (ratio == order.PatienceRemainingRatio) return order;
            changed = true;
            return Copy(order, ratio);
        }).ToList();
        return !changed ? context : new NormalBusinessContext { Orders = orders,
            OrdersReadComplete = context.OrdersReadComplete, Source = context.Source, Error = context.Error };
    }

    private static bool IsRatio(double? ratio) => ratio.HasValue && double.IsFinite(ratio.Value) && ratio is >= 0 and <= 1;

    /// <summary>完整复制原订单，只替换现场观察值，不能重建trace、点单时间、需求或任务配方。</summary>
    private static NightBusinessOrder Copy(NightBusinessOrder order, double? ratio) => new() {
        TraceId = order.TraceId, OrderLifecycleSequence = order.OrderLifecycleSequence, DeskCode = order.DeskCode,
        GuestId = order.GuestId, RuntimeGuestId = order.RuntimeGuestId, GuestName = order.GuestName,
        SpecialBusinessRole = order.SpecialBusinessRole, SpecialBusinessRoleLabel = order.SpecialBusinessRoleLabel,
        AutomationAllowed = order.AutomationAllowed, AutomationBlockReason = order.AutomationBlockReason,
        FoodTagId = order.FoodTagId, FoodTag = order.FoodTag, BeverageTagId = order.BeverageTagId, BeverageTag = order.BeverageTag,
        Source = order.Source, FirstSeenAtUtc = order.FirstSeenAtUtc, LastSeenAtUtc = order.LastSeenAtUtc, IsFreeOrder = order.IsFreeOrder,
        Fund = order.Fund, BaseFundCarry = order.BaseFundCarry, MaxFundCarry = order.MaxFundCarry,
        ExtraFundByBuff = order.ExtraFundByBuff, WillPayMoney = order.WillPayMoney, RemainingOrderCount = order.RemainingOrderCount,
        HasServedFood = order.HasServedFood, HasServedBeverage = order.HasServedBeverage,
        MissionRecipePriority = order.MissionRecipePriority, PatienceRemainingRatio = ratio,
    };

    private static NormalBusinessOrder Copy(NormalBusinessOrder order, double? ratio) => new() {
        TraceId = order.TraceId, OrderKey = order.OrderKey, OrderLifecycleSequence = order.OrderLifecycleSequence, DeskCode = order.DeskCode,
        GuestId = order.GuestId, RuntimeGuestId = order.RuntimeGuestId, GuestName = order.GuestName,
        GuestImageKind = order.GuestImageKind, GuestImageId = order.GuestImageId,
        SpecialBusinessRole = order.SpecialBusinessRole, SpecialBusinessRoleLabel = order.SpecialBusinessRoleLabel,
        FoodPreferenceTags = order.FoodPreferenceTags, BeveragePreferenceTags = order.BeveragePreferenceTags,
        Fund = order.Fund, BaseFundCarry = order.BaseFundCarry, MaxFundCarry = order.MaxFundCarry,
        ExtraFundByBuff = order.ExtraFundByBuff, WillPayMoney = order.WillPayMoney, RemainingOrderCount = order.RemainingOrderCount,
        FoodId = order.FoodId, FoodName = order.FoodName, BeverageId = order.BeverageId, BeverageName = order.BeverageName,
        HasServedFood = order.HasServedFood, HasServedBeverage = order.HasServedBeverage,
        ReadyToEvaluate = order.ReadyToEvaluate, HasEvaluated = order.HasEvaluated, ControllerAvailable = order.ControllerAvailable,
        CanAutomate = order.CanAutomate, ActionBlockReason = order.ActionBlockReason,
        FirstSeenAtUtc = order.FirstSeenAtUtc, Source = order.Source, PatienceRemainingRatio = ratio,
    };
}
