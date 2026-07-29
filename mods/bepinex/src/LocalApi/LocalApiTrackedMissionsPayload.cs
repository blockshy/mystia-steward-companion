using System.Text.Json;
using MystiaStewardCompanion.Save;

namespace MystiaStewardCompanion.LocalApi;

internal sealed class LocalApiTrackedMissionsDto
{
    public bool Ok { get; init; } = true;
    public bool RuntimeAvailable { get; init; }
    public long Generation { get; init; }
    public string Status { get; init; } = "";
    public string ContentSignature { get; init; } = "";
    public bool Unchanged { get; init; }
    public int UnverifiedCount { get; init; }
    public int TrackingCount { get; init; }
    public int FulfilledCount { get; init; }
    public IReadOnlyList<LocalApiTrackedMissionDto> Missions { get; init; } =
        Array.Empty<LocalApiTrackedMissionDto>();
}

internal sealed class LocalApiTrackedMissionDto
{
    public string Label { get; init; } = "";
    public string Title { get; init; } = "";
    public string ReceiverLabel { get; init; } = "";
    public string CharacterName { get; init; } = "";
    public IReadOnlyList<string> SceneNames { get; init; } = Array.Empty<string>();
    public string PresentationStatus { get; init; } = "";
    public string Status { get; init; } = "";
    public int ConditionCount { get; init; }
    public int? CompletedConditionCount { get; init; }
    public IReadOnlyList<bool?> ConditionStates { get; init; } = Array.Empty<bool?>();
}

internal sealed class LocalApiTrackedMissionsUnchangedDto
{
    public bool Unchanged { get; init; } = true;
    public string ContentSignature { get; init; } = "";
}

internal sealed class LocalApiTrackedMissionsContentDto
{
    public bool RuntimeAvailable { get; init; }
    public long Generation { get; init; }
    public string Status { get; init; } = "";
    public int UnverifiedCount { get; init; }
    public int TrackingCount { get; init; }
    public int FulfilledCount { get; init; }
    public IReadOnlyList<LocalApiTrackedMissionDto> Missions { get; init; } =
        Array.Empty<LocalApiTrackedMissionDto>();
}

internal static class LocalApiTrackedMissionsPayload
{
    public static string BuildJson(
        RuntimeTrackedMissionsSnapshot snapshot,
        string knownSignature,
        JsonSerializerOptions jsonOptions)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(jsonOptions);
        if (snapshot.Missions.Any(mission =>
                !IsValidPresentation(
                    mission.ReceiverLabel,
                    mission.CharacterName,
                    mission.SceneNames,
                    mission.PresentationStatus)))
        {
            throw new InvalidOperationException(
                "Tracked mission presentation fields are invalid.");
        }

        var missions = snapshot.Missions
            .Select(mission => new LocalApiTrackedMissionDto
            {
                Label = mission.Label,
                Title = mission.Title,
                ReceiverLabel = mission.ReceiverLabel,
                CharacterName = mission.CharacterName,
                SceneNames = mission.SceneNames.ToArray(),
                PresentationStatus = mission.PresentationStatus,
                Status = MissionStatusValue(mission.Status),
                ConditionCount = mission.ConditionCount,
                CompletedConditionCount = mission.CompletedConditionCount,
                ConditionStates = mission.ConditionStates.ToArray(),
            })
            .ToArray();
        var content = new LocalApiTrackedMissionsContentDto
        {
            RuntimeAvailable = snapshot.RuntimeAvailable,
            Generation = snapshot.Generation,
            Status = snapshot.Status,
            UnverifiedCount = missions.Count(
                mission => string.Equals(
                    mission.Status,
                    "unverified",
                    StringComparison.Ordinal)),
            TrackingCount = missions.Count(
                mission => string.Equals(
                    mission.Status,
                    "tracking",
                    StringComparison.Ordinal)),
            FulfilledCount = missions.Count(
                mission => string.Equals(
                    mission.Status,
                    "fulfilled",
                    StringComparison.Ordinal)),
            Missions = missions,
        };
        var canonicalJson = JsonSerializer.Serialize(content, jsonOptions);
        var contentSignature = LocalApiSnapshotSignature.Compute(canonicalJson);
        if (!string.IsNullOrWhiteSpace(knownSignature)
            && string.Equals(
                knownSignature,
                contentSignature,
                StringComparison.Ordinal))
        {
            return JsonSerializer.Serialize(
                new LocalApiTrackedMissionsUnchangedDto
                {
                    ContentSignature = contentSignature,
                },
                jsonOptions);
        }

        return JsonSerializer.Serialize(
            new LocalApiTrackedMissionsDto
            {
                RuntimeAvailable = content.RuntimeAvailable,
                Generation = content.Generation,
                Status = content.Status,
                ContentSignature = contentSignature,
                Unchanged = false,
                UnverifiedCount = content.UnverifiedCount,
                TrackingCount = content.TrackingCount,
                FulfilledCount = content.FulfilledCount,
                Missions = content.Missions,
            },
            jsonOptions);
    }

    private static string MissionStatusValue(RuntimeTrackedMissionStatus status)
    {
        return status switch
        {
            RuntimeTrackedMissionStatus.Unverified => "unverified",
            RuntimeTrackedMissionStatus.Tracking => "tracking",
            RuntimeTrackedMissionStatus.Fulfilled => "fulfilled",
            _ => throw new InvalidOperationException(
                $"Unknown tracked mission status {status}."),
        };
    }

    private static bool IsValidPresentation(
        string? receiverLabel,
        string? characterName,
        IReadOnlyList<string>? sceneNames,
        string? presentationStatus)
    {
        return RuntimeMissionPresentation.IsValid(
            receiverLabel == null
                || characterName == null
                || sceneNames == null
                || presentationStatus == null
                    ? null
                    : new RuntimeMissionPresentation(
                        receiverLabel,
                        characterName,
                        sceneNames,
                        presentationStatus));
    }
}
