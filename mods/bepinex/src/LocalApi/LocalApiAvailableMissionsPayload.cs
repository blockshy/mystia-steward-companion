using System.Text.Json;
using MystiaStewardCompanion.Save;

namespace MystiaStewardCompanion.LocalApi;

internal sealed class LocalApiAvailableMissionsDto
{
    public bool Ok { get; init; } = true;
    public bool RuntimeAvailable { get; init; }
    public string Status { get; init; } = "";
    public long MissionGeneration { get; init; }
    public long DaySceneGeneration { get; init; }
    public string ContentSignature { get; init; } = "";
    public bool Unchanged { get; init; }
    public int AvailableCount { get; init; }
    public IReadOnlyList<LocalApiAvailableMissionDto> Missions { get; init; } =
        Array.Empty<LocalApiAvailableMissionDto>();
    public string Error { get; init; } = "";
}

internal sealed class LocalApiAvailableMissionDto
{
    public string Label { get; init; } = "";
    public string Title { get; init; } = "";
    public string ReceiverLabel { get; init; } = "";
    public string CharacterName { get; init; } = "";
    public IReadOnlyList<string> SceneNames { get; init; } = Array.Empty<string>();
    public string PresentationStatus { get; init; } = "";
}

internal sealed class LocalApiAvailableMissionsUnchangedDto
{
    public bool Unchanged { get; init; } = true;
    public string ContentSignature { get; init; } = "";
}

internal sealed class LocalApiAvailableMissionsContentDto
{
    public bool RuntimeAvailable { get; init; }
    public string Status { get; init; } = "";
    public long MissionGeneration { get; init; }
    public long DaySceneGeneration { get; init; }
    public int AvailableCount { get; init; }
    public IReadOnlyList<LocalApiAvailableMissionDto> Missions { get; init; } =
        Array.Empty<LocalApiAvailableMissionDto>();
    public string Error { get; init; } = "";
}

internal static class LocalApiAvailableMissionsPayload
{
    public static string BuildJson(
        RuntimeAvailableMissionSnapshot snapshot,
        string knownSignature,
        JsonSerializerOptions jsonOptions)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(jsonOptions);

        if (snapshot.Missions == null
            || snapshot.Status == null
            || snapshot.Error == null)
        {
            throw new InvalidOperationException(
                "Available mission snapshot fields must not be null.");
        }
        if (snapshot.RuntimeAvailable
            && (snapshot.Status != RuntimeAvailableMissionSnapshot.ReadyStatus
                || snapshot.MissionGeneration < 1
                || snapshot.DaySceneGeneration < 1
                || snapshot.Error.Length != 0))
        {
            throw new InvalidOperationException(
                "Ready mission snapshots require positive generations and no error.");
        }
        if (!snapshot.RuntimeAvailable
            && (snapshot.Status == RuntimeAvailableMissionSnapshot.ReadyStatus
                || snapshot.Missions.Count != 0))
        {
            throw new InvalidOperationException(
                "Unavailable mission snapshots must use a non-ready status without missions.");
        }

        var missions = snapshot.Missions
            .OrderBy(mission => mission.Label, StringComparer.Ordinal)
            .ThenBy(mission => mission.Title, StringComparer.Ordinal)
            .Select(mission => new LocalApiAvailableMissionDto
            {
                Label = mission.Label,
                Title = mission.Title,
                ReceiverLabel = mission.ReceiverLabel,
                CharacterName = mission.CharacterName,
                SceneNames = mission.SceneNames.ToArray(),
                PresentationStatus = mission.PresentationStatus,
            })
            .ToArray();
        if (missions.Any(mission =>
                string.IsNullOrEmpty(mission.Label)
                || string.IsNullOrWhiteSpace(mission.Title)
                || mission.ReceiverLabel == null
                || mission.CharacterName == null
                || mission.SceneNames == null
                || mission.PresentationStatus == null
                || !IsValidPresentation(mission))
            || missions.Select(mission => mission.Label)
                .Distinct(StringComparer.Ordinal)
                .Count() != missions.Length)
        {
            throw new InvalidOperationException(
                "Available mission snapshots require unique, complete mission entries.");
        }
        var content = new LocalApiAvailableMissionsContentDto
        {
            RuntimeAvailable = snapshot.RuntimeAvailable,
            Status = snapshot.Status,
            MissionGeneration = snapshot.MissionGeneration,
            DaySceneGeneration = snapshot.DaySceneGeneration,
            AvailableCount = missions.Length,
            Missions = missions,
            Error = snapshot.Error,
        };
        var canonicalJson = JsonSerializer.Serialize(content, jsonOptions);
        var contentSignature = LocalApiSnapshotSignature.Compute(canonicalJson);
        if (!string.IsNullOrEmpty(knownSignature)
            && string.Equals(
                knownSignature,
                contentSignature,
                StringComparison.Ordinal))
        {
            return JsonSerializer.Serialize(
                new LocalApiAvailableMissionsUnchangedDto
                {
                    ContentSignature = contentSignature,
                },
                jsonOptions);
        }

        return JsonSerializer.Serialize(
            new LocalApiAvailableMissionsDto
            {
                RuntimeAvailable = content.RuntimeAvailable,
                Status = content.Status,
                MissionGeneration = content.MissionGeneration,
                DaySceneGeneration = content.DaySceneGeneration,
                ContentSignature = contentSignature,
                Unchanged = false,
                AvailableCount = content.AvailableCount,
                Missions = content.Missions,
                Error = content.Error,
            },
            jsonOptions);
    }

    private static bool IsValidPresentation(
        LocalApiAvailableMissionDto mission)
    {
        return !string.IsNullOrWhiteSpace(mission.ReceiverLabel)
            && !string.Equals(
                mission.PresentationStatus,
                RuntimeMissionPresentation.NoReceiverStatus,
                StringComparison.Ordinal)
            && RuntimeMissionPresentation.IsValid(
                new RuntimeMissionPresentation(
                    mission.ReceiverLabel,
                    mission.CharacterName,
                    mission.SceneNames,
                    mission.PresentationStatus));
    }
}
