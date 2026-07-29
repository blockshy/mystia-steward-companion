namespace MystiaStewardCompanion.Save;

internal sealed record RuntimeAvailableMissionSnapshot(
    bool RuntimeAvailable,
    long MissionGeneration,
    long DaySceneGeneration,
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
        long daySceneGeneration,
        string error)
    {
        return Unavailable(
            missionGeneration,
            daySceneGeneration,
            RuntimeUnavailableStatus,
            error);
    }

    public static RuntimeAvailableMissionSnapshot Unavailable(
        long missionGeneration,
        long daySceneGeneration,
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
            DaySceneGeneration: daySceneGeneration,
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
    string PresentationStatus);

internal sealed record RuntimeAvailableMissionCaptureInput(
    bool Complete,
    long MissionGeneration,
    long DaySceneGeneration,
    long SourceMissionChangeVersion,
    IReadOnlyList<string> FinishedEvents,
    IReadOnlyList<string> FinishedMissions,
    IReadOnlyList<RuntimeAvailableMissionCandidate> Candidates,
    string Error);

internal sealed record RuntimeAvailableMissionCandidate(
    string SourceEventLabel,
    int TriggerType,
    string EligibilityDisposition,
    string ReferenceSource,
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
