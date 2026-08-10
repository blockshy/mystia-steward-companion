namespace MystiaStewardCompanion.Save;

internal static partial class RuntimeOrderPreparationService
{
    private static RuntimeAutomationControlDecision ObserveAutomationCookingJobControl(
        AutomationCookingJob job,
        RuntimeAutomationControlStage stage,
        DateTime observedAtUtc)
    {
        return RuntimeAutomationControlState.Observe(
            ToRuntimeAutomationControlTargetKind(job),
            stage,
            IsWackyKoishiBossTarget(job.Target),
            observedAtUtc);
    }

    private static RuntimeAutomationControlPermit AcquireAutomationCookingJobControlPermit(
        AutomationCookingJob job,
        RuntimeAutomationControlStage stage,
        DateTime observedAtUtc)
    {
        var permit = RuntimeAutomationControlState.AcquirePermit(
            ToRuntimeAutomationControlTargetKind(job),
            stage,
            IsWackyKoishiBossTarget(job.Target),
            observedAtUtc);
        ApplyAutomationCookingJobControlDecision(job, stage, permit.Decision, observedAtUtc);
        return permit;
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
