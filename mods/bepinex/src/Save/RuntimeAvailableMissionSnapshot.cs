namespace MystiaStewardCompanion.Save;

/// <summary>可触发任务的发布值；不可用状态必须清空任务列表，避免将读取失败伪装成已确认的候选结果。</summary>
internal sealed record RuntimeAvailableMissionSnapshot(
    bool RuntimeAvailable,
    long MissionGeneration,
    long SourceRevision,
    string Status,
    string Error,
    IReadOnlyList<RuntimeAvailableMissionSnapshotEntry> Missions)
{
    public const string NotAttachedStatus = "not-attached";
    public const string WaitingForLoadStatus = "waiting-for-load";
    public const string LoadingStatus = "loading";
    public const string ReadyStatus = "ready";
    public const string RuntimeUnavailableStatus = "runtime-unavailable";
    public const string MissionDataIncompleteStatus = "mission-data-incomplete";

    public static RuntimeAvailableMissionSnapshot Unavailable(
        long missionGeneration,
        long sourceRevision,
        string error)
    {
        return Unavailable(
            missionGeneration,
            sourceRevision,
            RuntimeUnavailableStatus,
            error);
    }

    public static RuntimeAvailableMissionSnapshot Unavailable(
        long missionGeneration,
        long sourceRevision,
        string status,
        string error)
    {
        if (string.Equals(status, ReadyStatus, StringComparison.Ordinal)
            || (status != NotAttachedStatus
                && status != WaitingForLoadStatus
                && status != LoadingStatus
                && status != RuntimeUnavailableStatus
                && status != MissionDataIncompleteStatus))
        {
            throw new ArgumentOutOfRangeException(
                nameof(status),
                status,
                "Unknown unavailable mission status.");
        }
        return new RuntimeAvailableMissionSnapshot(
            RuntimeAvailable: false,
            MissionGeneration: missionGeneration,
            SourceRevision: sourceRevision,
            Status: status,
            Error: error,
            Missions: Array.Empty<RuntimeAvailableMissionSnapshotEntry>());
    }
}

internal sealed record RuntimeAvailableMissionSnapshotEntry(
    string Label,
    string Title,
    string ReceiverLabel,
    string CharacterName,
    IReadOnlyList<string> SceneNames,
    string PresentationStatus,
    string ActivationMode,
    string ActivationStatus,
    string TriggerKind,
    string SourceTiming,
    string ActivationHint);

/// <summary>同次采集的完成记录、候选和版本证据；Complete 为假时，即使存在部分候选也不得发布为就绪。</summary>
internal sealed record RuntimeAvailableMissionCaptureInput(
    bool Complete,
    long MissionGeneration,
    long SourceRevision,
    long SourceMissionChangeVersion,
    IReadOnlyList<string> FinishedEvents,
    IReadOnlyList<string> FinishedMissions,
    IReadOnlyList<RuntimeAvailableMissionCandidate> Candidates,
    string Error);

/// <summary>一条调度来源与任务定义的关联；同一任务允许多个来源，投影时需先确认定义元数据一致。</summary>
internal sealed record RuntimeAvailableMissionCandidate(
    string SourceEventLabel,
    int TriggerType,
    string EligibilityDisposition,
    string ReferenceSource,
    string SourcePhase,
    string MissionLabel,
    bool DefinitionAvailable,
    string Title,
    bool HasReceiver,
    string ReceiverLabel,
    string CharacterName,
    IReadOnlyList<string> SceneNames,
    string PresentationStatus,
    int DefinitionConditionCount,
    IReadOnlyList<string> PreNodes,
    bool LoopedMission,
    bool Active,
    bool Finished);
