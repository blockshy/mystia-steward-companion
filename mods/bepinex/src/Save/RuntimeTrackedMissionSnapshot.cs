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
    IReadOnlyList<bool?> ConditionStates)
{
    /// <summary>与 ConditionStates 同索引的游戏本地化条件；空串明确表示描述暂不可读取。</summary>
    public IReadOnlyList<string> ConditionDescriptions { get; init; } = Array.Empty<string>();
}

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
