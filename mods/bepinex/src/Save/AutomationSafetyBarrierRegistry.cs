namespace MystiaStewardCompanion.Save;

internal sealed record AutomationSafetyBarrierRecord(
    long Sequence,
    long BusinessGeneration,
    string TargetIdentity,
    string Code,
    string Stage,
    string Message);

internal readonly record struct AutomationSafetyBarrierAcknowledgement(
    bool Found,
    IReadOnlyList<long> Sequences);

/// <summary>
/// 保留不可重复操作的失败屏障，等待租约所有者明确确认游戏状态，或所属经营代次结束。
/// 调用方以自动料理任务锁同步访问，使事件发布与屏障登记处于同一临界区。
/// </summary>
internal sealed class AutomationSafetyBarrierRegistry
{
    private readonly Dictionary<long, AutomationSafetyBarrierRecord> _barriers = new();

    public static string BuildOrderLifecycleTargetIdentity(
        long businessGeneration,
        string orderKind,
        nint orderPointer,
        nint controllerPointer,
        long lifecycleSequence)
    {
        if (businessGeneration <= 0) throw new ArgumentOutOfRangeException(nameof(businessGeneration));
        if (string.IsNullOrWhiteSpace(orderKind)) throw new ArgumentException("Concrete order kind is required.", nameof(orderKind));
        if (orderPointer == 0) throw new ArgumentOutOfRangeException(nameof(orderPointer));
        if (controllerPointer == 0) throw new ArgumentOutOfRangeException(nameof(controllerPointer));
        if (lifecycleSequence <= 0) throw new ArgumentOutOfRangeException(nameof(lifecycleSequence));

        return $"order-lifecycle:{businessGeneration}:{orderKind}:"
            + $"{(long)orderPointer:x}:{(long)controllerPointer:x}:"
            + lifecycleSequence;
    }

    public bool Contains(long sequence) => _barriers.ContainsKey(sequence);

    public void Register(AutomationSafetyBarrierRecord barrier)
    {
        if (barrier.Sequence <= 0) throw new ArgumentOutOfRangeException(nameof(barrier));
        if (barrier.BusinessGeneration <= 0) throw new ArgumentOutOfRangeException(nameof(barrier));
        if (string.IsNullOrWhiteSpace(barrier.TargetIdentity))
        {
            throw new ArgumentException("Automation safety barrier target identity is required.", nameof(barrier));
        }

        _barriers[barrier.Sequence] = barrier;
    }

    /// <summary>
    /// 在经营生命周期结束边界移除该代次全部未确认屏障。
    /// 活跃代次的屏障不会因容量限制而被静默淘汰。
    /// </summary>
    public IReadOnlyList<long> RetireBusinessGeneration(long businessGeneration)
    {
        if (businessGeneration <= 0) return Array.Empty<long>();

        var sequences = _barriers
            .Where(entry => entry.Value.BusinessGeneration == businessGeneration)
            .Select(entry => entry.Key)
            .OrderBy(value => value)
            .ToArray();
        foreach (var sequence in sequences)
        {
            _barriers.Remove(sequence);
        }

        return sequences;
    }

    public bool TryGetLatest(string targetIdentity, out AutomationSafetyBarrierRecord? barrier)
    {
        barrier = _barriers.Values
            .Where(candidate => string.Equals(candidate.TargetIdentity, targetIdentity, StringComparison.Ordinal))
            .OrderByDescending(candidate => candidate.Sequence)
            .FirstOrDefault();
        return barrier != null;
    }

    /// <summary>确认所选屏障及同一目标不晚于它的屏障；其他目标或更新的屏障继续保留。</summary>
    public AutomationSafetyBarrierAcknowledgement Acknowledge(long sequence)
    {
        if (!_barriers.TryGetValue(sequence, out var selected))
        {
            return new AutomationSafetyBarrierAcknowledgement(false, Array.Empty<long>());
        }

        var sequences = _barriers
            .Where(entry => entry.Key <= sequence
                && string.Equals(entry.Value.TargetIdentity, selected.TargetIdentity, StringComparison.Ordinal))
            .Select(entry => entry.Key)
            .OrderBy(value => value)
            .ToArray();
        foreach (var acknowledgedSequence in sequences)
        {
            _barriers.Remove(acknowledgedSequence);
        }

        return new AutomationSafetyBarrierAcknowledgement(true, sequences);
    }
}
