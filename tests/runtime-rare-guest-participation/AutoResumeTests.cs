using MystiaStewardCompanion.Save;

/// <summary>纯托管等待条恢复回归；所有身份与比例均为合成值，不触碰 Unity、用户配置或真实游戏。</summary>
internal static class AutoResumeTests
{
    internal static void Run()
    {
        var assertions = 0;
        void Check(bool condition, string message) { assertions++; if (!condition) throw new InvalidOperationException(message); }
        var a = new RuntimeRareGuestParticipationOrderIdentity(701, "R-0001", 101, 30);
        var b = a with { TraceId = "R-0002", OrderLifecycleSequence = 102 };
        var c = a with { TraceId = "R-0003", OrderLifecycleSequence = 103 };
        var unmanaged = a with { TraceId = "R-0004", OrderLifecycleSequence = 104, GuestId = 36 };
        var all = new[] { a, b, c, unmanaged };
        void Start()
        {
            RuntimeRareGuestParticipationState.Reset();
            var revision = RuntimeRareGuestParticipationState.BeginBusiness(701, new[] { 30 });
            RuntimeRareGuestParticipationState.ReconcileCurrentOrders(701, revision, true,
                all.Select(identity => new RuntimeRareGuestParticipationOrderObservation(identity, null)));
        }
        IReadOnlyList<RuntimeRareGuestParticipationOrderIdentity> Resume(
            IReadOnlyList<(RuntimeRareGuestParticipationOrderIdentity Identity, double? Ratio)> values,
            bool front = false, bool complete = true,
            params RuntimeRareGuestParticipationOrderIdentity[] started)
            => RuntimeRareGuestParticipationState.ResumeBelowPatienceThreshold(701,
                RuntimeRareGuestParticipationState.Snapshot.Revision, complete, values, 30, front, started);
        (RuntimeRareGuestParticipationOrderIdentity, double?)[] Values(double? av, double? bv = null, double? cv = null)
            => new[] { (a, av), (b, bv), (c, cv), (unmanaged, (double?)0.01) };
        void Pause(RuntimeRareGuestParticipationOrderIdentity identity)
        {
            RuntimeRareGuestParticipationState.MutateParticipation(701, RuntimeRareGuestParticipationState.Snapshot.Revision,
                RuntimeRareGuestParticipationAction.Pause, RuntimeRareGuestParticipationTargetScope.Order, identity.GuestId, new[] { identity });
        }
        RuntimeRareGuestParticipationOrderIdentity[] Queue() => RuntimeRareGuestParticipationState.Snapshot.Orders
            .Where(entry => entry.Participating).OrderBy(entry => entry.QueuePosition).Select(entry => entry.Identity).ToArray();

        Start();
        var originalRevision = RuntimeRareGuestParticipationState.Snapshot.Revision;
        foreach (var invalid in new double?[] { null, double.NaN, double.PositiveInfinity, double.NegativeInfinity, -0.01, 1.01, 0.3, 1 })
            Check(Resume(Values(invalid)).Count == 0, "未知、越界和等于阈值的比例不得恢复。");
        Check(RuntimeRareGuestParticipationState.Snapshot.Revision == originalRevision, "无候选时不推进修订。");
        Check(Resume(Values(0.1), complete: false).Count == 0, "部分读取不能消费机会。");
        Check(Resume(Values(0.1).Take(3).ToArray()).Count == 0, "缺项集合不能恢复。");
        Check(Resume(new[] { (a, (double?)0.1), (a, (double?)0.1), (c, (double?)0.1), (unmanaged, (double?)0.1) }).Count == 0,
            "重复身份不能恢复其他有效项。");
        var stale = a with { OrderLifecycleSequence = 999 };
        Check(Resume(new[] { (stale, (double?)0.1), (b, (double?)null), (c, (double?)null), (unmanaged, (double?)null) }).Count == 0,
            "旧生命周期不能恢复当前同名订单。");
        Check(Resume(Values(0.1), started: new[] { stale }).Count == 0, "未知开工锚点拒绝整批。");
        Check(Resume(Values(0.1), started: new[] { a }).Count == 0, "已开工但暂停的目标不能被自动重启。");
        Check(Resume(Values(0.299, 0.2, 0.0)).SequenceEqual(new[] { a, b, c }), "同客多单各自启用，并保持首次点单顺序。");
        Check(Queue().SequenceEqual(new[] { unmanaged, a, b, c }), "排尾不能覆盖原队列。");
        Check(RuntimeRareGuestParticipationState.Snapshot.Revision == originalRevision + 1, "同批次只推进一次修订。");
        Check(RuntimeRareGuestParticipationState.Snapshot.Orders.Single(entry => entry.Identity == a).ReasonCode
            == RuntimeRareGuestParticipationState.LowPatienceResumedReason, "记录准确启用原因。");
        for (var index = 0; index < 100; index++) Check(Resume(Values(0, 0, 0)).Count == 0, "稳定低比例不得反复启用。");
        Pause(a);
        Check(Resume(Values(0)).Count == 0 && !Queue().Contains(a), "自动启用后人工再次暂停必须持续有效。");
        RuntimeRareGuestParticipationState.ApplyManagedGuestIdsFromAuthority(Array.Empty<int>(), resetManagedParticipation: false);
        RuntimeRareGuestParticipationState.ApplyManagedGuestIdsFromAuthority(new[] { 30 }, resetManagedParticipation: false);
        Check(Resume(Values(0, 0, 0)).Count == 0, "移出名单后重新加入不得重获本生命周期的自动启用机会。");
        RuntimeRareGuestParticipationState.ApplyManagedGuestIdsFromAuthority(new[] { 30 }, resetManagedParticipation: true);
        Check(Resume(Values(0, 0, 0)).Count == 0, "切换主配置不能重置一生一次的触发记录。");
        var rev = RuntimeRareGuestParticipationState.Snapshot.Revision;
        RuntimeRareGuestParticipationState.ReconcileCurrentOrders(701, rev, true,
            new[] { b, c, unmanaged }.Select(identity => new RuntimeRareGuestParticipationOrderObservation(identity, null)));
        Check(Resume(Values(0)).Count == 0, "离单不能被迟到采样复活。");
        rev = RuntimeRareGuestParticipationState.Snapshot.Revision;
        try
        {
            RuntimeRareGuestParticipationState.ReconcileCurrentOrders(701, rev, true,
                all.Select(identity => new RuntimeRareGuestParticipationOrderObservation(identity, null)));
            throw new Exception("已经离单的生命周期不能重新加入当前集合。");
        }
        catch (RuntimeRareGuestParticipationConflictException) { assertions++; }
        Check(Resume(Values(0)).Count == 0, "同一生命周期迟到重现不能重获自动启用机会。");

        Start();
        var reversed = Values(0.1, 0.1, 0.1).Reverse().ToArray();
        Check(Resume(reversed, front: true).SequenceEqual(new[] { a, b, c }), "反向采集顺序不应颠倒批次队首顺序。");
        Check(Queue().SequenceEqual(new[] { a, b, c, unmanaged }), "无开工任务时整批排至队首。");
        Start();
        Check(Resume(reversed, front: true, started: new[] { unmanaged }).SequenceEqual(new[] { a, b, c }), "开工锚点后可稳定插入整批。");
        Check(Queue().SequenceEqual(new[] { unmanaged, a, b, c }), "队首请求不能抢占已开始制作的任务。");

        // 一个原生客人有多笔订单时，新的生命周期仍有自己的一次机会。
        Pause(a);
        var replacement = a with { OrderLifecycleSequence = 1001 };
        rev = RuntimeRareGuestParticipationState.Snapshot.Revision;
        var next = new[] { replacement, b, c, unmanaged };
        RuntimeRareGuestParticipationState.ReconcileCurrentOrders(701, rev, true,
            next.Select(identity => new RuntimeRareGuestParticipationOrderObservation(identity, null)));
        Check(Resume(next.Select(identity => (identity, (double?)0.1)).ToArray()).SequenceEqual(new[] { replacement }),
            "同一trace的新生命周期只能影响本次订单，不继承旧单触发记录。");
        Check(!RuntimeRareGuestParticipationState.Snapshot.Orders.Any(entry => entry.Identity == a), "旧单始终不复活。");
        RuntimeRareGuestParticipationState.EndBusinessIfCurrent(701);
        try { Resume(next.Select(identity => (identity, (double?)0.1)).ToArray()); throw new Exception("关闭经营仍允许恢复。"); }
        catch (RuntimeRareGuestParticipationConflictException) { assertions++; }
        RuntimeRareGuestParticipationState.Reset();
        Console.WriteLine($"PASS: low-patience auto resume {assertions} assertions (strict threshold, full identity, once per lifecycle, stable batch and protected work).");
    }
}
