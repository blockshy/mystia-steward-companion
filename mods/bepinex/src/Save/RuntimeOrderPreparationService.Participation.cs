namespace MystiaStewardCompanion.Save;

/// <summary>已开工订单的不可变身份。优先启用只保护真实工作，不保护被动展示的多个高亮订单。</summary>
internal readonly record struct RuntimeRareAutomationQueueCandidate(string JobId, RuntimeRareGuestParticipationOrderIdentity Identity);

internal static partial class RuntimeOrderPreparationService
{
    /// <summary>
    /// 在料理任务锁中只复制标量，释放后再取得调度锁，避免反向锁序。缺失绑定、跨经营轮次
    /// 或重复身份均拒绝整个优先操作，不能静默漏掉已开工订单而抢占其位置。
    /// 自动等待条恢复传入 includeSuspended=true，暂停但已有料理的订单也不被当作从未开工的候选。
    /// </summary>
    internal static IReadOnlyList<RuntimeRareAutomationQueueCandidate> SnapshotActiveRareAutomationQueueCandidates(
        long businessGeneration, bool includeSuspended = false)
    {
        if (businessGeneration <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(businessGeneration));
        }

        lock (AutomationCookingJobLock)
        {
            var result = new List<RuntimeRareAutomationQueueCandidate>();
            var identities = new HashSet<RuntimeRareGuestParticipationOrderIdentity>();
            foreach (var job in AutomationCookingJobs)
            {
                if (job.Target.Kind != CookingCollectionTargetKind.RareOrder
                    || (!includeSuspended && !string.Equals(job.ControlState, "active", StringComparison.Ordinal)))
                {
                    continue;
                }

                if (!job.Target.OrderBinding.HasValue)
                {
                    throw new InvalidOperationException(
                        $"Active rare cooking job {job.JobId} has no exact runtime order binding.");
                }

                var binding = job.Target.OrderBinding.Value;
                if (binding.BusinessGeneration != businessGeneration
                    || binding.OrderKind != RuntimeOrderKind.Special
                    || binding.OrderPointer == 0
                    || binding.ControllerPointer == 0
                    || !RuntimeOrderTerminalReceiptStore.MatchesRequestedLifecycle(
                        job.Target.RequestedOrderLifecycleSequence,
                        binding.LifecycleSequence)
                    || !job.Target.GuestId.HasValue
                    || job.Target.GuestId.Value < 0
                    || !RuntimeOrderTraceIdService.TryNormalizeTargetTraceId(
                        RuntimeUiTargetKind.Rare,
                        job.Target.TraceId,
                        enabled: true,
                        out var normalizedTraceId,
                        out _)
                    || !string.Equals(normalizedTraceId, job.Target.TraceId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Active rare cooking job {job.JobId} cannot provide a current exact queue anchor.");
                }

                var identity = new RuntimeRareGuestParticipationOrderIdentity(
                    binding.BusinessGeneration,
                    job.Target.TraceId,
                    binding.LifecycleSequence,
                    job.Target.GuestId.Value);
                if (!identities.Add(identity))
                {
                    throw new InvalidOperationException(
                        $"Multiple active rare cooking jobs claim the same exact queue anchor: {identity}.");
                }

                result.Add(new RuntimeRareAutomationQueueCandidate(job.JobId, identity));
            }

            return result;
        }
    }

}
