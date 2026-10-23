using MystiaStewardCompanion.Core;

namespace MystiaStewardCompanion.Save;

/// <summary>
/// 一次同步主线程采集内的成功读取复用。键只能是已核实的原生对象指针；值只保存
/// 脱离游戏对象的托管 DTO，绝不缓存 IL2CPP wrapper、委托、失败结果或跨帧执行权限。
/// </summary>
/// <remarks>
/// 每次 Load 开始必须 Clear。原生指针可能被对象池复用，不能把本缓存升级为按时间
/// 生效的跨帧缓存。缺少指针时照常独立读取；一次失败不能阻止其它来源重新核实对象。
/// </remarks>
internal sealed class RuntimeSnapshotReadCache
{
    private readonly HashSet<nint> _capturedNormalOrders = new();
    private readonly Dictionary<nint, NormalBusinessOrder> _visibleNormalOrders = new();
    private readonly Dictionary<nint, NightBusinessGuest> _rareGuests = new();

    /// <summary>仅在本次捕获完成后，使用同一次就绪检查结果启用提前剔除。</summary>
    public bool NormalCaptureIsAuthoritative { get; set; }

    public void Clear()
    {
        _capturedNormalOrders.Clear();
        _visibleNormalOrders.Clear();
        _rareGuests.Clear();
        NormalCaptureIsAuthoritative = false;
    }

    /// <summary>仅登记本次已通过生命周期检查、最终保留的普客捕获结果。</summary>
    public void RememberCapturedNormal(nint orderPointer)
    {
        if (orderPointer != 0) _capturedNormalOrders.Add(orderPointer);
    }

    /// <summary>
    /// 复用三个无控制器可视来源的读取；这些来源只展示订单，不能从命中缓存获得执行权限。
    /// 已确认捕获的同指针可视项原本会在 Load 尾部剔除，现提前剔除以避免重复原生读取。
    /// </summary>
    public NormalBusinessOrder? ReadVisibleNormal(
        nint orderPointer,
        string source,
        Func<NormalBusinessOrder?> read)
    {
        if (orderPointer == 0) return read();
        if (NormalCaptureIsAuthoritative && _capturedNormalOrders.Contains(orderPointer)) return null;
        if (_visibleNormalOrders.TryGetValue(orderPointer, out var existing))
        {
            return CopyVisibleNormal(existing, source);
        }

        var result = read();
        if (result != null) _visibleNormalOrders.Add(orderPointer, result);
        return result;
    }

    /// <summary>同一客人控制器可能同时出现在已呈现、桌位、可驱赶等集合中。</summary>
    public NightBusinessGuest? ReadRareGuest(
        nint controllerPointer,
        string source,
        Func<NightBusinessGuest?> read)
    {
        if (controllerPointer == 0) return read();
        if (_rareGuests.TryGetValue(controllerPointer, out var existing))
        {
            // 来源参与原有去重和诊断，不能把首次来源冒充为其它集合；其它字段均来自
            // 同次同步采集中的同一精确控制器，不依据姓名、桌号或客人目录 ID 合并。
            return new NightBusinessGuest
            {
                DeskCode = existing.DeskCode,
                GuestId = existing.GuestId,
                GuestName = existing.GuestName,
                Source = source,
                Fund = existing.Fund,
                BaseFundCarry = existing.BaseFundCarry,
                MaxFundCarry = existing.MaxFundCarry,
                ExtraFundByBuff = existing.ExtraFundByBuff,
                WillPayMoney = existing.WillPayMoney,
            };
        }

        var result = read();
        if (result != null) _rareGuests.Add(controllerPointer, result);
        return result;
    }

    private static NormalBusinessOrder CopyVisibleNormal(NormalBusinessOrder existing, string source)
    {
        return new NormalBusinessOrder
        {
            TraceId = existing.TraceId,
            OrderKey = existing.OrderKey,
            OrderLifecycleSequence = existing.OrderLifecycleSequence,
            DeskCode = existing.DeskCode,
            GuestId = existing.GuestId,
            RuntimeGuestId = existing.RuntimeGuestId,
            GuestImageKind = existing.GuestImageKind,
            GuestImageId = existing.GuestImageId,
            GuestName = existing.GuestName,
            SpecialBusinessRole = existing.SpecialBusinessRole,
            SpecialBusinessRoleLabel = existing.SpecialBusinessRoleLabel,
            FoodPreferenceTags = existing.FoodPreferenceTags.ToList(),
            BeveragePreferenceTags = existing.BeveragePreferenceTags.ToList(),
            Fund = existing.Fund,
            BaseFundCarry = existing.BaseFundCarry,
            MaxFundCarry = existing.MaxFundCarry,
            ExtraFundByBuff = existing.ExtraFundByBuff,
            WillPayMoney = existing.WillPayMoney,
            RemainingOrderCount = existing.RemainingOrderCount,
            FoodId = existing.FoodId,
            FoodName = existing.FoodName,
            BeverageId = existing.BeverageId,
            BeverageName = existing.BeverageName,
            HasServedFood = existing.HasServedFood,
            HasServedBeverage = existing.HasServedBeverage,
            ReadyToEvaluate = existing.ReadyToEvaluate,
            HasEvaluated = existing.HasEvaluated,
            ControllerAvailable = existing.ControllerAvailable,
            CanAutomate = existing.CanAutomate,
            ActionBlockReason = existing.ActionBlockReason,
            FirstSeenAtUtc = existing.FirstSeenAtUtc,
            Source = source,
        };
    }
}
