using MystiaStewardCompanion.Save;

namespace MystiaStewardCompanion.LocalApi;

/// <summary>稀客调度的纯值协议：只公开精确订单身份与队列位置，不传递原生对象或客户端推断顺序。</summary>
internal sealed class LocalApiRareGuestParticipationSnapshot
{
    public bool Active { get; init; }
    /// <summary>只有完整当前订单集合与调度快照一致，才可采用此只读展示顺序。</summary>
    public bool Ready { get; init; }
    /// <summary>
    /// 全部当前稀客的展示顺序：参与订单按队列在前，暂停订单按首次点单观测在后。
    /// null沿用普通时间排序；空数组表示来源未确认或现场无订单。此集合不代表执行许可，
    /// 自动化与游戏辅助必须继续检查Entries中的Participating及完整身份，禁止以显示代替准入。
    /// </summary>
    public List<LocalApiRareGuestParticipationIdentityDto>? DisplayOrderKeys { get; init; }
    public long BusinessGeneration { get; init; }
    public long ParticipationRevision { get; init; }
    public List<int> ManagedGuestIds { get; init; } = new();
    public List<LocalApiRareGuestParticipationEntry> Entries { get; init; } = new();

    public static LocalApiRareGuestParticipationSnapshot From(
        RuntimeRareGuestParticipationSnapshot snapshot, bool sourceComplete = true)
    {
        return new LocalApiRareGuestParticipationSnapshot
        {
            Active = snapshot.IsActive,
            Ready = sourceComplete && snapshot.IsActive,
            DisplayOrderKeys = snapshot.ManagedGuestIds.Count == 0 ? null
                : !sourceComplete || !snapshot.IsActive ? new()
                // 原生采集按FirstSeenAtUtc排序后进入状态机，ObservationSequence冻结该首次顺序。
                // 暂停只撤回执行资格，不能让用户在经营页、专注页失去仍真实存在的点单。
                : snapshot.Orders.OrderBy(order => order.Participating ? 0 : 1)
                    .ThenBy(order => order.Participating ? order.QueuePosition : order.ObservationSequence)
                    .ThenBy(order => order.Identity.TraceId, StringComparer.Ordinal)
                    .ThenBy(order => order.Identity.OrderLifecycleSequence)
                    .ThenBy(order => order.Identity.GuestId)
                    .Select(order => new LocalApiRareGuestParticipationIdentityDto
                    {
                        BusinessGeneration = order.Identity.BusinessGeneration, TraceId = order.Identity.TraceId,
                        OrderLifecycleSequence = order.Identity.OrderLifecycleSequence, GuestId = order.Identity.GuestId,
                    }).ToList(),
            BusinessGeneration = snapshot.BusinessGeneration,
            ParticipationRevision = snapshot.Revision,
            ManagedGuestIds = snapshot.ManagedGuestIds.ToList(),
            Entries = snapshot.Orders
                .Select(order => new LocalApiRareGuestParticipationEntry
                {
                    TraceId = order.Identity.TraceId,
                    OrderLifecycleSequence = order.Identity.OrderLifecycleSequence,
                    GuestId = order.Identity.GuestId,
                    Managed = order.Managed,
                    Participating = order.Participating,
                    ReasonCode = order.ReasonCode,
                    QueuePosition = order.QueuePosition > 0 ? order.QueuePosition : null,
                })
                .ToList(),
        };
    }
}

internal sealed class LocalApiRareGuestParticipationEntry
{
    public string TraceId { get; init; } = "";
    public long OrderLifecycleSequence { get; init; }
    public int GuestId { get; init; }
    public bool Managed { get; init; }
    public bool Participating { get; init; }
    public string ReasonCode { get; init; } = "";
    public int? QueuePosition { get; init; }
}

internal sealed class LocalApiRareGuestParticipationMutationRequest
{
    public long ExpectedAuthorityRevision { get; init; }
    public long ExpectedBusinessGeneration { get; init; }
    public long ExpectedParticipationRevision { get; init; }
    public string Action { get; init; } = "";
    public LocalApiRareGuestParticipationMutationTargetDto Target { get; init; } = new();
}

internal sealed class LocalApiRareGuestParticipationMutationTargetDto
{
    public string Type { get; init; } = "";
    public int GuestId { get; init; } = -1;
    public List<LocalApiRareGuestParticipationIdentityDto> ExpectedCurrentOrders { get; init; } = new();
    public LocalApiRareGuestParticipationIdentityDto? Order { get; init; }
}

internal sealed class LocalApiRareGuestParticipationIdentityDto
{
    public long BusinessGeneration { get; init; }
    public string TraceId { get; init; } = "";
    public long OrderLifecycleSequence { get; init; }
    public int GuestId { get; init; } = -1;
}

internal sealed class LocalApiRareGuestParticipationMutationDto
{
    public bool Ok { get; init; }
    public bool Changed { get; init; }
    public string Status { get; init; } = "";
    public LocalApiRareGuestParticipationSnapshot Participation { get; init; } = new();
    public string? Error { get; init; }
}
