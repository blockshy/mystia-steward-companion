using MystiaStewardCompanion.Core;

namespace MystiaStewardCompanion.Save;

internal static class RuntimeCookerSnapshotService
{
    private const int MaxFailureSampleLength = 160;
    private const int MaxAvailabilityDiagnosticLength = 240;

    public static void ApplyTo(RecommendationState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var snapshot = ReadPlacedCookers();
        state.PlacedCookers.Clear();
        state.PlacedCookerTypeIds.Clear();
        state.PlacedCookerSnapshotComplete = snapshot.Complete;
        state.PlacedCookerControllerCount = snapshot.ControllerCount;
        state.PlacedCookerEmptyControllerCount = snapshot.EmptyControllerCount;
        state.PlacedCookerLockedControllerCount = snapshot.LockedControllerCount;
        state.PlacedCookerReadFailureCount = snapshot.ReadFailureCount;
        state.PlacedCookerStatus = snapshot.Status;

        foreach (var cooker in snapshot.Cookers)
        {
            state.PlacedCookers.Add(cooker);
            foreach (var typeId in cooker.TypeIds)
            {
                state.PlacedCookerTypeIds.Add(typeId);
            }
        }
    }

    private static RuntimeCookerSnapshotReadResult ReadPlacedCookers()
    {
        if (!RuntimeCookerReflection.TryReadLockedCookerPositions(
                out var lockedPositions,
                out var lockedStatus))
        {
            return RuntimeCookerSnapshotReadResult.Unavailable(
                $"source unavailable; {SanitizeDiagnostic(lockedStatus, MaxFailureSampleLength)}");
        }

        object? cookSystem;
        try
        {
            cookSystem = RuntimeCookerReflection.GetCookSystemManager();
        }
        catch (Exception ex)
        {
            return RuntimeCookerSnapshotReadResult.Unavailable(
                $"source unavailable; manager error={SanitizeDiagnostic(ex.GetBaseException().Message, MaxFailureSampleLength)}");
        }

        if (cookSystem == null)
        {
            return RuntimeCookerSnapshotReadResult.Unavailable("source unavailable; manager missing");
        }

        if (!RuntimeCookerReflection.TryReadCookerControllerEntriesFromCookSystem(
                cookSystem,
                lockedPositions,
                out var controllerEntries,
                out var controllerStatus))
        {
            return RuntimeCookerSnapshotReadResult.Unavailable(
                $"source unavailable; {SanitizeDiagnostic(controllerStatus, MaxFailureSampleLength)}");
        }

        var lockedControllerCount = controllerEntries.Count(
            entry => lockedPositions.Contains(entry.GridPosition));

        var cookers = new List<PlacedCookerInfo>();
        var emptyControllerCount = 0;
        for (var controllerIndex = 0; controllerIndex < controllerEntries.Count; controllerIndex++)
        {
            var entry = controllerEntries[controllerIndex];
            if (lockedPositions.Contains(entry.GridPosition))
            {
                continue;
            }

            var controller = entry.Controller;
            if (!RuntimeCookerReflection.TryReadCookerControllerState(
                    controller,
                    out var controllerState,
                    out var stateStatus))
            {
                return RuntimeCookerSnapshotReadResult.Unavailable(
                    $"source unavailable; controller={controllerIndex}/{entry.ControllerIdentity}; "
                    + $"position={entry.GridPosition}; "
                    + SanitizeDiagnostic(stateStatus, MaxFailureSampleLength),
                    controllerEntries.Count,
                    lockedControllerCount);
            }

            if (!controllerState.CouldOpen)
            {
                return RuntimeCookerSnapshotReadResult.Unavailable(
                    $"source unavailable; controller={controllerIndex}/{entry.ControllerIdentity}; "
                    + $"position={entry.GridPosition}; couldOpen={controllerState.CouldOpen}; "
                    + "not present in LockedCookers; gate-mismatch",
                    controllerEntries.Count,
                    lockedControllerCount);
            }

            if (controllerState.IsEmptyDesk)
            {
                emptyControllerCount++;
                continue;
            }

            var typeIds = controllerState.TypeIds.Distinct().OrderBy(id => id).ToList();
            var typeNames = typeIds
                .Select(RuntimeCookerReflection.ResolveCookerTypeName)
                .Where(name => name.Length > 0)
                .Distinct()
                .ToList();
            var automationAvailability = RuntimeCookerStartAvailabilityService.Classify(
                controller,
                controllerState,
                out var automationAvailabilityDiagnostic);
            cookers.Add(new PlacedCookerInfo
            {
                ControllerIndex = controllerIndex,
                GridPosition = new CookerGridPosition
                {
                    X = entry.GridPosition.X,
                    Y = entry.GridPosition.Y,
                    Z = entry.GridPosition.Z,
                },
                ControllerIdentity = entry.ControllerIdentity,
                TypeIds = typeIds,
                TypeNames = typeNames,
                Name = string.Join("/", typeNames),
                ChallengeLocked = false,
                CouldOpen = controllerState.CouldOpen,
                AutomationAvailable = automationAvailability != AutomationCookerStartAvailability.Unavailable,
                AutomationAvailability = automationAvailability.ToString(),
                AutomationAvailabilityDiagnostic = SanitizeDiagnostic(
                    automationAvailabilityDiagnostic,
                    MaxAvailabilityDiagnosticLength),
                Source = "CookSystemManager.AllCookers+EventManager.LockedCookers",
            });
        }

        var typeSummary = cookers
            .SelectMany(cooker => cooker.TypeNames)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        var status = $"complete; {controllerStatus}; {lockedStatus}; "
            + $"controllers={controllerEntries.Count}; placed={cookers.Count}; "
            + $"empty={emptyControllerCount}; locked={lockedControllerCount}; failures=0; "
            + $"types={string.Join("/", typeSummary)}";

        if (cookers.Count + emptyControllerCount + lockedControllerCount != controllerEntries.Count)
        {
            return RuntimeCookerSnapshotReadResult.Unavailable(
                $"source unavailable; controller classification mismatch; {status}",
                controllerEntries.Count,
                lockedControllerCount);
        }

        return new RuntimeCookerSnapshotReadResult
        {
            Cookers = cookers,
            Complete = true,
            ControllerCount = controllerEntries.Count,
            EmptyControllerCount = emptyControllerCount,
            LockedControllerCount = lockedControllerCount,
            ReadFailureCount = 0,
            Status = status,
        };
    }

    private static string SanitizeDiagnostic(string value, int maxLength)
    {
        var sanitized = value.Replace('\r', ' ').Replace('\n', ' ');
        return sanitized.Length <= maxLength
            ? sanitized
            : sanitized[..maxLength] + "...";
    }

    private sealed class RuntimeCookerSnapshotReadResult
    {
        public List<PlacedCookerInfo> Cookers { get; init; } = new();
        public bool Complete { get; init; }
        public int ControllerCount { get; init; }
        public int EmptyControllerCount { get; init; }
        public int LockedControllerCount { get; init; }
        public int ReadFailureCount { get; init; }
        public string Status { get; init; } = "";

        public static RuntimeCookerSnapshotReadResult Unavailable(
            string status,
            int controllerCount = 0,
            int lockedControllerCount = 0)
        {
            var boundedLockedControllerCount = Math.Clamp(
                lockedControllerCount,
                0,
                controllerCount);
            return new RuntimeCookerSnapshotReadResult
            {
                ControllerCount = controllerCount,
                LockedControllerCount = boundedLockedControllerCount,
                ReadFailureCount = controllerCount - boundedLockedControllerCount,
                Status = status,
            };
        }
    }
}
