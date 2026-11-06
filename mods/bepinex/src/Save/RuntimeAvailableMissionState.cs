namespace MystiaStewardCompanion.Save;

/// <summary>发布可触发任务的托管快照；读写均在锁内复制任务数组和场景数组，隔离调用方的集合修改。</summary>
/// <remarks>投影在锁外执行；本类不比较代次的新旧，采集方负责串行发布当前结果。</remarks>
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
