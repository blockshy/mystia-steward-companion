namespace MystiaStewardCompanion.Save;

internal sealed record RuntimeMissionPresentation(
    string ReceiverLabel,
    string CharacterName,
    IReadOnlyList<string> SceneNames,
    string PresentationStatus)
{
    public const int MaxReceiverLength = 512;
    public const int MaxDisplayNameLength = 256;
    public const int MaxSceneCount = 64;
    public const int MaxStatusLength = 256;
    public const int MaxRetryCount = 4;
    public const int MaxAttemptCount = MaxRetryCount + 1;
    public const string NoReceiverStatus = "no-receiver";
    public const string PendingStatus = "unavailable:pending";
    public const string ReadyStatus = "ready";
    public const string ShapeUnavailableStatus = "unavailable:shape";
    public const string NpcCatalogUnavailableStatus = "unavailable:npc-catalog";
    public const string NpcMissingStatus = "unavailable:npc-missing";
    public const string NpcIdentityUnavailableStatus = "unavailable:npc-identity";
    public const string MappedIdentityUnavailableStatus =
        "unavailable:mapped-identity";
    public const string CharacterNameUnavailableStatus =
        "unavailable:character-name";
    public const string DestinationsUnavailableStatus =
        "unavailable:destinations";
    public const string MapCatalogUnavailableStatus =
        "unavailable:map-catalog";
    public const string SceneLanguageUnavailableStatus =
        "unavailable:scene-language";
    public const string DestinationMarkerUnavailableStatus =
        "unavailable:destination-marker";
    public const string SceneMarkerAmbiguousStatus =
        "unavailable:scene-marker-ambiguous";
    public const string SceneMarkerUnavailableStatus =
        "unavailable:scene-marker";
    public const string SceneNameUnavailableStatus =
        "unavailable:scene-name";
    public const string SceneCountUnavailableStatus =
        "unavailable:scene-count";
    public const string EntryReadUnavailableStatus =
        "unavailable:entry-read";

    public static RuntimeMissionPresentation NoReceiver { get; } = new(
        ReceiverLabel: "",
        CharacterName: "",
        SceneNames: Array.Empty<string>(),
        PresentationStatus: NoReceiverStatus);

    public static RuntimeMissionPresentation Pending(string receiverLabel)
    {
        return new RuntimeMissionPresentation(
            receiverLabel,
            CharacterName: "",
            SceneNames: Array.Empty<string>(),
            PresentationStatus: PendingStatus);
    }

    public static bool IsValid(RuntimeMissionPresentation? presentation)
    {
        if (presentation == null
            || presentation.ReceiverLabel == null
            || presentation.CharacterName == null
            || presentation.SceneNames == null
            || string.IsNullOrEmpty(presentation.PresentationStatus)
            || presentation.ReceiverLabel.Length > MaxReceiverLength
            || presentation.CharacterName.Length > MaxDisplayNameLength
            || (presentation.CharacterName.Length > 0
                && string.IsNullOrWhiteSpace(presentation.CharacterName))
            || presentation.SceneNames.Count > MaxSceneCount
            || presentation.PresentationStatus.Length > MaxStatusLength
            || presentation.SceneNames.Any(name =>
                string.IsNullOrWhiteSpace(name)
                || name.Length > MaxDisplayNameLength)
            || presentation.SceneNames
                .Distinct(StringComparer.Ordinal)
                .Count() != presentation.SceneNames.Count)
        {
            return false;
        }

        if (string.Equals(
                presentation.PresentationStatus,
                NoReceiverStatus,
                StringComparison.Ordinal))
        {
            return presentation.ReceiverLabel.Length == 0
                && presentation.CharacterName.Length == 0
                && presentation.SceneNames.Count == 0;
        }
        if (string.IsNullOrWhiteSpace(presentation.ReceiverLabel))
        {
            return false;
        }
        if (string.Equals(
                presentation.PresentationStatus,
                ReadyStatus,
                StringComparison.Ordinal))
        {
            return !string.IsNullOrWhiteSpace(presentation.CharacterName);
        }
        return presentation.PresentationStatus is PendingStatus
            or ShapeUnavailableStatus
            or NpcCatalogUnavailableStatus
            or NpcMissingStatus
            or NpcIdentityUnavailableStatus
            or MappedIdentityUnavailableStatus
            or CharacterNameUnavailableStatus
            or DestinationsUnavailableStatus
            or MapCatalogUnavailableStatus
            or SceneLanguageUnavailableStatus
            or DestinationMarkerUnavailableStatus
            or SceneMarkerAmbiguousStatus
            or SceneMarkerUnavailableStatus
            or SceneNameUnavailableStatus
            or SceneCountUnavailableStatus
            or EntryReadUnavailableStatus;
    }

    public static TimeSpan RetryDelayAfterAttempt(int attemptCount)
    {
        return attemptCount switch
        {
            1 => TimeSpan.FromMilliseconds(500),
            2 => TimeSpan.FromMilliseconds(1_000),
            3 => TimeSpan.FromMilliseconds(2_000),
            4 => TimeSpan.FromMilliseconds(4_000),
            _ => TimeSpan.Zero,
        };
    }
}

internal sealed record RuntimeMissionPresentationRequest(
    string Label,
    string ReceiverLabel);

internal sealed record RuntimeMissionPresentationApply(
    string Label,
    string ReceiverLabel,
    RuntimeMissionPresentation Presentation);
