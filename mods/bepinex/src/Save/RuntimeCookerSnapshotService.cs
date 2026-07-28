using MystiaStewardCompanion.Core;

namespace MystiaStewardCompanion.Save;

internal static class RuntimeCookerSnapshotService
{
    private static readonly object SyncRoot = new();

    public static string Status
    {
        get
        {
            lock (SyncRoot)
            {
                return _status;
            }
        }
    }

    private static string _status = "not read";

    public static void ApplyTo(RecommendationState state)
    {
        var cookers = ReadPlacedCookers();
        state.PlacedCookerStatus = Status;
        foreach (var cooker in cookers)
        {
            state.PlacedCookers.Add(cooker);
            foreach (var typeId in cooker.TypeIds)
            {
                state.PlacedCookerTypeIds.Add(typeId);
            }
        }
    }

    private static List<PlacedCookerInfo> ReadPlacedCookers()
    {
        var result = ReadCookSystemCookers(out var status);
        SetStatus(status);
        return result;
    }

    private static List<PlacedCookerInfo> ReadCookSystemCookers(out string status)
    {
        var result = new List<PlacedCookerInfo>();
        status = "manager not read";

        object? cookSystem;
        try
        {
            cookSystem = RuntimeCookerReflection.GetCookSystemManager();
        }
        catch (Exception ex)
        {
            status = $"manager error: {ex.Message}";
            return result;
        }

        if (cookSystem == null)
        {
            status = "manager missing";
            return result;
        }

        var controllerItems = RuntimeCookerReflection.ReadCookerControllersFromCookSystem(cookSystem, out var controllerStatus);
        var index = 0;
        foreach (var controller in controllerItems)
        {
            var controllerIndex = index++;
            if (!RuntimeCookerReflection.TryReadCookerControllerState(
                    controller,
                    out var controllerState,
                    out var stateStatus))
            {
                status = $"manager incomplete; controller={controllerIndex}; stage={stateStatus}; {controllerStatus}";
                return new List<PlacedCookerInfo>();
            }

            var typeIds = controllerState.TypeIds.Distinct().OrderBy(id => id).ToList();
            var typeNames = typeIds.Select(RuntimeCookerReflection.ResolveCookerTypeName).Where(name => name.Length > 0).Distinct().ToList();
            result.Add(new PlacedCookerInfo
            {
                ControllerIndex = controllerIndex,
                TypeIds = typeIds,
                TypeNames = typeNames,
                Name = typeNames.Count > 0 ? string.Join("/", typeNames) : controllerState.Cooker.GetType().Name,
                IsOpen = controllerState.CouldOpen,
                Source = "CookSystemManager",
            });
        }

        var typeSummary = result
            .SelectMany(cooker => cooker.TypeNames)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct()
            .ToArray();
        status = result.Count == 0
            ? $"manager empty; {controllerStatus}"
            : $"ok; {controllerStatus}; cookers={result.Count}; types={string.Join("/", typeSummary)}";
        return result;
    }

    private static void SetStatus(string status)
    {
        lock (SyncRoot)
        {
            _status = status;
        }
    }
}
