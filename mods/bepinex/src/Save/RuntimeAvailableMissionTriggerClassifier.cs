namespace MystiaStewardCompanion.Save;

internal sealed record RuntimeAvailableMissionTriggerClassification(
    string ActivationMode,
    string ActivationStatus,
    string TriggerKind,
    string SourceTiming,
    string ActivationHint);

internal static class RuntimeAvailableMissionTriggerClassifier
{
    public const int OnEnterDaySceneMapTrigger = 0;
    public const int OnEnterDaySceneTrigger = 1;
    public const int KizunaCheckPointTrigger = 5;

    public const string ScheduledPhase = "scheduled";
    public const string WaitingAfterPerformancePhase =
        "waiting-after-performance";

    public const string ConditionalMode = "conditional";
    public const string AutomaticMode = "automatic";
    public const string MultipleMode = "multiple";

    public const string AvailableStatus = "available";
    public const string TriggeringStatus = "triggering";

    public const string EnterDaySceneMapKind = "enter-day-scene-map";
    public const string EnterDaySceneKind = "enter-day-scene";
    public const string KizunaCheckPointKind = "kizuna-checkpoint";
    public const string MultipleKind = "multiple";

    public const string BeforePerformanceTiming = "before-performance";
    public const string AfterPerformanceTiming = "after-performance";
    public const string MultipleTiming = "multiple";

    public const string EnterTargetDayMapHint = "enter-target-day-map";
    public const string EnterDaySceneHint = "enter-day-scene";
    public const string KizunaReadyHint = "kizuna-ready";
    public const string NativeStartPendingHint = "native-start-pending";
    public const string MultipleSourcesHint = "multiple-sources";

    public static bool TryClassify(
        int triggerType,
        string eligibilityDisposition,
        string referenceSource,
        string sourcePhase,
        out RuntimeAvailableMissionTriggerClassification classification)
    {
        classification = null!;
        if (!TryReadSourceTiming(referenceSource, out var sourceTiming)
            || (sourcePhase != ScheduledPhase
                && sourcePhase != WaitingAfterPerformancePhase))
        {
            return false;
        }

        var transitionObserved = string.Equals(
            sourcePhase,
            WaitingAfterPerformancePhase,
            StringComparison.Ordinal);
        if (!transitionObserved
            && !string.Equals(
                eligibilityDisposition,
                RuntimeAvailableMissionCapture.EligibleDisposition,
                StringComparison.Ordinal))
        {
            return false;
        }

        string activationMode;
        string triggerKind;
        string activationHint;
        switch (triggerType)
        {
            case OnEnterDaySceneMapTrigger:
                activationMode = AutomaticMode;
                triggerKind = EnterDaySceneMapKind;
                activationHint = EnterTargetDayMapHint;
                break;
            case OnEnterDaySceneTrigger:
                activationMode = AutomaticMode;
                triggerKind = EnterDaySceneKind;
                activationHint = EnterDaySceneHint;
                break;
            case KizunaCheckPointTrigger:
                activationMode = ConditionalMode;
                triggerKind = KizunaCheckPointKind;
                activationHint = KizunaReadyHint;
                break;
            default:
                return false;
        }

        classification = new RuntimeAvailableMissionTriggerClassification(
            activationMode,
            transitionObserved ? TriggeringStatus : AvailableStatus,
            triggerKind,
            sourceTiming,
            transitionObserved ? NativeStartPendingHint : activationHint);
        return true;
    }

    public static RuntimeAvailableMissionTriggerClassification Merge(
        IReadOnlyList<RuntimeAvailableMissionTriggerClassification> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count == 0)
        {
            throw new InvalidOperationException(
                "available-mission-trigger-source-missing");
        }

        var modes = sources
            .Select(source => source.ActivationMode)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var kinds = sources
            .Select(source => source.TriggerKind)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var timings = sources
            .Select(source => source.SourceTiming)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var hints = sources
            .Select(source => source.ActivationHint)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var triggering = sources.Any(source => string.Equals(
            source.ActivationStatus,
            TriggeringStatus,
            StringComparison.Ordinal));
        return new RuntimeAvailableMissionTriggerClassification(
            modes.Length == 1 ? modes[0] : MultipleMode,
            triggering ? TriggeringStatus : AvailableStatus,
            kinds.Length == 1 ? kinds[0] : MultipleKind,
            timings.Length == 1 ? timings[0] : MultipleTiming,
            triggering
                ? NativeStartPendingHint
                : hints.Length == 1
                    ? hints[0]
                    : MultipleSourcesHint);
    }

    public static bool IsValid(
        RuntimeAvailableMissionTriggerClassification? classification)
    {
        return classification != null
            && classification.ActivationMode is ConditionalMode
                or AutomaticMode
                or MultipleMode
            && classification.ActivationStatus is AvailableStatus
                or TriggeringStatus
            && classification.TriggerKind is EnterDaySceneMapKind
                or EnterDaySceneKind
                or KizunaCheckPointKind
                or MultipleKind
            && classification.SourceTiming is BeforePerformanceTiming
                or AfterPerformanceTiming
                or MultipleTiming
            && classification.ActivationHint is EnterTargetDayMapHint
                or EnterDaySceneHint
                or KizunaReadyHint
                or NativeStartPendingHint
                or MultipleSourcesHint;
    }

    private static bool TryReadSourceTiming(
        string referenceSource,
        out string sourceTiming)
    {
        switch (referenceSource)
        {
            case RuntimeAvailableMissionSourceState.BeforePerformanceSource:
                sourceTiming = BeforePerformanceTiming;
                return true;
            case RuntimeAvailableMissionSourceState.AfterPerformanceSource:
                sourceTiming = AfterPerformanceTiming;
                return true;
            default:
                sourceTiming = "";
                return false;
        }
    }
}
