namespace MystiaStewardCompanion.Save;

internal static partial class RuntimeOrderPreparationService
{
    private static RuntimeAutomationControlDecision ObserveAutomationCookingJobControl(
        AutomationCookingJob job,
        RuntimeAutomationControlStage stage,
        DateTime observedAtUtc)
    {
        var automationDecision = RuntimeAutomationControlState.Observe(
            ToRuntimeAutomationControlTargetKind(job),
            stage,
            IsWackyKoishiBossTarget(job.Target),
            observedAtUtc);
        if (!automationDecision.Allowed) return automationDecision;

        var managedGuestIds = RuntimeAutomationControlState.SnapshotManagedRareGuestIds();
        var participationPermit = AcquireRareCookingJobParticipationPermit(
            job,
            managedGuestIds);
        if (participationPermit == null) return automationDecision;
        using (participationPermit)
        {
            return MergeRareCookingJobParticipationDecision(
                job,
                automationDecision,
                managedGuestIds,
                participationPermit.Decision);
        }
    }

    private static RuntimeAutomationCookingJobControlPermit AcquireAutomationCookingJobControlPermit(
        AutomationCookingJob job,
        RuntimeAutomationControlStage stage,
        DateTime observedAtUtc)
    {
        var permit = RuntimeAutomationControlState.AcquirePermit(
            ToRuntimeAutomationControlTargetKind(job),
            stage,
            IsWackyKoishiBossTarget(job.Target),
            observedAtUtc);
        RuntimeRareGuestParticipationPermit? participationPermit = null;
        var decision = permit.Decision;
        if (decision.Allowed)
        {
            var managedGuestIds = RuntimeAutomationControlState.SnapshotManagedRareGuestIds();
            participationPermit = AcquireRareCookingJobParticipationPermit(
                job,
                managedGuestIds);
            if (participationPermit != null)
            {
                decision = MergeRareCookingJobParticipationDecision(
                    job,
                    decision,
                    managedGuestIds,
                    participationPermit.Decision);
            }
        }
        ApplyAutomationCookingJobControlDecision(job, stage, decision, observedAtUtc);
        return new RuntimeAutomationCookingJobControlPermit(
            decision,
            permit,
            participationPermit);
    }

    private static RuntimeRareGuestParticipationPermit? AcquireRareCookingJobParticipationPermit(
        AutomationCookingJob job,
        IReadOnlyList<int> managedGuestIds)
    {
        if (job.Target.Kind != CookingCollectionTargetKind.RareOrder
            || managedGuestIds.Count == 0)
        {
            return null;
        }

        if (!job.Target.OrderBinding.HasValue)
        {
            return DeniedRareGuestParticipationPermit(
                "order-binding-unavailable",
                "Managed rare-order cooking job has no exact active native binding.");
        }

        var binding = job.Target.OrderBinding.Value;
        var identity = RuntimeRareCookingJobParticipationPolicy.CreateIdentityFromExactBinding(
            binding,
            job.Target.TraceId,
            job.Target.GuestId ?? -1);
        if (!RuntimeRareGuestParticipationState.TryEnrichCurrentBinding(
                identity,
                binding,
                out var enrichmentDecision))
        {
            return DeniedRareGuestParticipationPermit(
                enrichmentDecision.ReasonCode,
                enrichmentDecision.Message);
        }
        return RuntimeRareGuestParticipationState.AcquireBoundSideEffectPermit(
            identity,
            binding);
    }

    private static RuntimeAutomationControlDecision MergeRareCookingJobParticipationDecision(
        AutomationCookingJob job,
        RuntimeAutomationControlDecision automationDecision,
        IReadOnlyList<int> managedGuestIds,
        RuntimeRareGuestParticipationDecision participationDecision)
    {
        if (!participationDecision.Allowed)
        {
            return SuspendForRareGuestParticipation(
                automationDecision,
                participationDecision.ReasonCode,
                participationDecision.Message);
        }

        if (!job.Target.OrderBinding.HasValue)
        {
            return SuspendForRareGuestParticipation(
                automationDecision,
                "order-binding-unavailable",
                "Managed rare-order cooking job has no exact active native binding.");
        }

        var binding = job.Target.OrderBinding.Value;
        var participation = RuntimeRareGuestParticipationState.Snapshot;
        var configuredAsManaged = job.Target.GuestId.HasValue
            && managedGuestIds.Contains(job.Target.GuestId.Value);
        var rostersAligned = RuntimeRareCookingJobParticipationPolicy.IsRosterAlignedWithExactBinding(
            participation,
            binding,
            managedGuestIds);
        if (!rostersAligned
            || participationDecision.Order == null
            || participationDecision.Order.Managed != configuredAsManaged)
        {
            return SuspendForRareGuestParticipation(
                automationDecision,
                "participation-profile-not-aligned",
                "Rare-order participation state is not aligned with the current primary profile.");
        }
        if (!RuntimeOrderTerminalReceiptStore.MatchesActiveLifecycle(binding))
        {
            return SuspendForRareGuestParticipation(
                automationDecision,
                "order-binding-inactive",
                "Rare-order native binding is no longer the active captured lifecycle.");
        }
        return automationDecision;
    }

    private static RuntimeAutomationControlDecision SuspendForRareGuestParticipation(
        RuntimeAutomationControlDecision automationDecision,
        string reasonCode,
        string message)
    {
        return new RuntimeAutomationControlDecision(
            false,
            "suspended-participation",
            reasonCode,
            message,
            automationDecision.AuthorityRevision,
            automationDecision.DeliveryConfigured,
            automationDecision.CompletionConfigured);
    }

    private static RuntimeRareGuestParticipationPermit DeniedRareGuestParticipationPermit(
        string reasonCode,
        string message)
    {
        var snapshot = RuntimeRareGuestParticipationState.Snapshot;
        return new RuntimeRareGuestParticipationPermit(
            new RuntimeRareGuestParticipationDecision(
                false,
                "suspended-participation",
                reasonCode,
                message,
                snapshot.BusinessGeneration,
                snapshot.Revision,
                Order: null),
            release: null);
    }

    private sealed class RuntimeAutomationCookingJobControlPermit : IDisposable
    {
        private RuntimeAutomationControlPermit? _automationPermit;
        private RuntimeRareGuestParticipationPermit? _participationPermit;

        public RuntimeAutomationCookingJobControlPermit(
            RuntimeAutomationControlDecision decision,
            RuntimeAutomationControlPermit automationPermit,
            RuntimeRareGuestParticipationPermit? participationPermit)
        {
            Decision = decision;
            _automationPermit = automationPermit;
            _participationPermit = participationPermit;
        }

        public RuntimeAutomationControlDecision Decision { get; }

        public bool Allowed => Decision.Allowed;

        public void Dispose()
        {
            Interlocked.Exchange(ref _participationPermit, null)?.Dispose();
            Interlocked.Exchange(ref _automationPermit, null)?.Dispose();
        }
    }

    private static RuntimeAutomationControlStage GetPendingCookingJobControlStage(
        AutomationCookingJob job)
    {
        return IsYuumaBossTarget(job.Target)
            ? RuntimeAutomationControlStage.YuumaSettlement
            : RuntimeAutomationControlStage.FoodDelivery;
    }

    private static RuntimeAutomationControlTargetKind ToRuntimeAutomationControlTargetKind(
        AutomationCookingJob job)
    {
        return job.Target.Kind == CookingCollectionTargetKind.RareOrder
            ? RuntimeAutomationControlTargetKind.Rare
            : RuntimeAutomationControlTargetKind.Normal;
    }

    private static bool ApplyAutomationCookingJobControlDecision(
        AutomationCookingJob job,
        RuntimeAutomationControlStage stage,
        RuntimeAutomationControlDecision decision,
        DateTime observedAtUtc)
    {
        var changed = !string.Equals(job.ControlState, decision.State, StringComparison.Ordinal)
            || !string.Equals(job.ControlReasonCode, decision.ReasonCode, StringComparison.Ordinal)
            || job.ControlAuthorityRevision != decision.AuthorityRevision
            || job.ControlStage != stage;
        job.ControlState = decision.State;
        job.ControlReasonCode = decision.ReasonCode;
        job.ControlMessage = decision.Message;
        job.ControlAuthorityRevision = decision.AuthorityRevision;
        job.ControlStage = stage;
        if (!decision.Allowed)
        {
            job.ControlSuspendedAtUtc ??= observedAtUtc;
            SuspendAutomationCookingJobClocks(job, observedAtUtc);
        }
        else
        {
            job.ControlSuspendedAtUtc = null;
        }

        if (!changed) return false;

        AppendAutomationLog(
            decision.Allowed ? "job-control-resumed" : "job-control-suspended",
            job.Target,
            job.FormatLogContext(
                decision.Allowed
                    ? $"controlStage={stage}; authorityRevision={decision.AuthorityRevision}"
                    : $"controlStage={stage}; controlReason={decision.ReasonCode}; {decision.Message}"));
        return true;
    }

    private static void SuspendAutomationCookingJobClocks(
        AutomationCookingJob job,
        DateTime observedAtUtc)
    {
        job.DeliveryTimeoutClock.Observe(observedAtUtc, eligible: false);
        job.FoodDeliveryEvaluationCloseoutTracker?.Suspend(observedAtUtc);
        job.ManualHandoffMissingOrderClock.Observe(observedAtUtc, eligible: false);
        job.ManualHandoffReadFailureClock.Observe(observedAtUtc, eligible: false);
        job.Tracker.Suspend(observedAtUtc);
    }
}
