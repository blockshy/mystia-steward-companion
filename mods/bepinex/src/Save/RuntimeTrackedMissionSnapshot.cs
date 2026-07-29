namespace MystiaStewardCompanion.Save;

internal enum RuntimeTrackedMissionStatus
{
    Unverified,
    Tracking,
    Fulfilled,
}

internal sealed record RuntimeTrackedMissionSnapshot(
    string Label,
    string Title,
    string ReceiverLabel,
    string CharacterName,
    IReadOnlyList<string> SceneNames,
    string PresentationStatus,
    RuntimeTrackedMissionStatus Status,
    int ConditionCount,
    int? CompletedConditionCount,
    IReadOnlyList<bool?> ConditionStates);

internal sealed record RuntimeTrackedMissionsSnapshot(
    bool RuntimeAvailable,
    long Generation,
    string Status,
    IReadOnlyList<RuntimeTrackedMissionSnapshot> Missions)
{
    public const string NotAttachedStatus = "not-attached";
    public const string WaitingForLoadStatus = "waiting-for-load";
    public const string LoadingStatus = "loading";
    public const string ReadyStatus = "ready";
    public const string RuntimeUnavailableStatus = "runtime-unavailable";
    public const string MissionDataIncompleteStatus = "mission-data-incomplete";
}
