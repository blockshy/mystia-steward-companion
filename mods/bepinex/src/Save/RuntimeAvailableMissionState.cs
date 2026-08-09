namespace MystiaStewardCompanion.Save;

internal sealed class RuntimeAvailableMissionState
{
    private readonly object _gate = new();
    private RuntimeAvailableMissionSnapshot _snapshot =
        RuntimeAvailableMissionSnapshot.Unavailable(
            missionGeneration: 0,
            sourceRevision: 0,
            error: "available-mission-runtime-not-ready");

    public RuntimeAvailableMissionSnapshot Snapshot()
    {
        lock (_gate)
        {
            return Copy(_snapshot);
        }
    }

    public RuntimeAvailableMissionSnapshot Publish(
        RuntimeAvailableMissionCaptureInput input)
    {
        var next = RuntimeAvailableMissionCapture.Project(input);
        lock (_gate)
        {
            _snapshot = Copy(next);
            return Copy(_snapshot);
        }
    }

    public RuntimeAvailableMissionSnapshot SetUnavailable(
        long missionGeneration,
        long sourceRevision,
        string error)
    {
        return SetUnavailable(
            missionGeneration,
            sourceRevision,
            RuntimeAvailableMissionSnapshot.RuntimeUnavailableStatus,
            error);
    }

    public RuntimeAvailableMissionSnapshot SetUnavailable(
        long missionGeneration,
        long sourceRevision,
        string status,
        string error)
    {
        var next = RuntimeAvailableMissionSnapshot.Unavailable(
            missionGeneration,
            sourceRevision,
            status,
            error);
        lock (_gate)
        {
            _snapshot = next;
            return Copy(_snapshot);
        }
    }

    private static RuntimeAvailableMissionSnapshot Copy(
        RuntimeAvailableMissionSnapshot snapshot)
    {
        return snapshot with
        {
            Missions = snapshot.Missions
                .Select(mission => mission with
                {
                    SceneNames = mission.SceneNames.ToArray(),
                })
                .ToArray(),
        };
    }
}
