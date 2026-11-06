namespace MystiaStewardCompanion.Save;

internal enum RuntimeDaySceneReadinessPhase
{
    Closed,
    WaitingForFirstEnter,
    WaitingForEnterDayAction,
    WaitingForEnterDayFinish,
    WaitingForMapAction,
    WaitingForMapFinish,
    Ready,
}

internal enum RuntimeDaySceneFinishKind
{
    None,
    EnterDay,
    EnterDayMap,
}

internal readonly record struct RuntimeDaySceneFinishToken(
    RuntimeDaySceneFinishKind Kind,
    long Generation,
    nint ActionPointer);

/// <summary>以日间进入及地图调度动作的精确回调推进可读状态，避免用固定等待时间猜测场景已就绪。</summary>
/// <remarks>完成令牌只保存代次和原生指针值；调用方负责在主线程按实际回调顺序驱动本状态机。</remarks>
internal sealed class RuntimeDaySceneReadinessState
{
    private bool _panelOpen;
    private int _firstEnterDepth;
    private nint _managerPointer;
    private nint _enterDayActionPointer;
    private nint _enterDayMapActionPointer;
    private string _mapLabel = "";
    private bool _manualWorkReturn;

    public long Version { get; private set; }
    public long Generation { get; private set; }
    public RuntimeDaySceneReadinessPhase Phase { get; private set; } = RuntimeDaySceneReadinessPhase.Closed;
    public string LastEvent { get; private set; } = "not started";
    public bool Ready => _panelOpen && Phase == RuntimeDaySceneReadinessPhase.Ready;

    public string Status => $"phase={FormatPhase(Phase)}; generation={Generation}; map={_mapLabel}; event={LastEvent}";

    public void OpenPanel()
    {
        if (_panelOpen) return;

        _panelOpen = true;
        if (Phase == RuntimeDaySceneReadinessPhase.Closed)
        {
            Phase = RuntimeDaySceneReadinessPhase.WaitingForFirstEnter;
        }

        Touch("DaySceneSustainedPannel.OnPannelPostOpen");
    }

    public void BeginFirstEnter(nint managerPointer, bool manualWorkReturn)
    {
        Generation++;
        _managerPointer = managerPointer;
        _enterDayActionPointer = 0;
        _enterDayMapActionPointer = 0;
        _mapLabel = "";
        _manualWorkReturn = manualWorkReturn;
        _firstEnterDepth++;
        Phase = RuntimeDaySceneReadinessPhase.WaitingForEnterDayAction;
        Touch("DayScene.SceneManager.OnFirstEnterDaySceneFinish begin");
    }

    public void CaptureEnterDayAction(nint actionPointer)
    {
        if (_firstEnterDepth <= 0
            || _managerPointer == 0
            || actionPointer == 0
            || Phase != RuntimeDaySceneReadinessPhase.WaitingForEnterDayAction)
        {
            return;
        }

        _enterDayActionPointer = actionPointer;
        Phase = RuntimeDaySceneReadinessPhase.WaitingForEnterDayFinish;
        Touch("RunTimeScheduler.OnEnterDayScene action captured");
    }

    /// <summary>离开首次进入的调用作用域；手工工作返回且未产生调度动作时，使用明确的无动作完成路径。</summary>
    public void EndFirstEnter()
    {
        if (_firstEnterDepth <= 0) return;

        _firstEnterDepth--;
        if (_firstEnterDepth == 0
            && _manualWorkReturn
            && Phase == RuntimeDaySceneReadinessPhase.WaitingForEnterDayAction)
        {
            Phase = RuntimeDaySceneReadinessPhase.Ready;
            Touch("DayScene.SceneManager.OnFirstEnterDaySceneFinish completed without scheduler action");
        }
    }

    public RuntimeDaySceneFinishToken BeginSchedulerFinish(nint actionPointer)
    {
        if (actionPointer == 0) return default;

        if (Phase == RuntimeDaySceneReadinessPhase.WaitingForEnterDayFinish
            && actionPointer == _enterDayActionPointer)
        {
            Phase = RuntimeDaySceneReadinessPhase.WaitingForMapAction;
            Touch("RunTimeScheduler.OnEnterDayScene action finishing");
            return new RuntimeDaySceneFinishToken(
                RuntimeDaySceneFinishKind.EnterDay,
                Generation,
                actionPointer);
        }

        if (Phase == RuntimeDaySceneReadinessPhase.WaitingForMapFinish
            && actionPointer == _enterDayMapActionPointer)
        {
            return new RuntimeDaySceneFinishToken(
                RuntimeDaySceneFinishKind.EnterDayMap,
                Generation,
                actionPointer);
        }

        return default;
    }

    public void CaptureEnterDayMapAction(
        nint managerPointer,
        nint actionPointer,
        bool hasPendingEnterDayEvent,
        string? mapLabel)
    {
        if (_managerPointer == 0
            || managerPointer != _managerPointer
            || hasPendingEnterDayEvent
            || actionPointer == 0)
        {
            return;
        }

        if (Phase == RuntimeDaySceneReadinessPhase.Ready)
        {
            Generation++;
            _enterDayActionPointer = 0;
        }
        else if (Phase != RuntimeDaySceneReadinessPhase.WaitingForMapAction
                 && Phase != RuntimeDaySceneReadinessPhase.WaitingForMapFinish)
        {
            return;
        }

        if (Phase == RuntimeDaySceneReadinessPhase.WaitingForMapFinish
            && actionPointer == _enterDayMapActionPointer)
        {
            return;
        }

        _enterDayMapActionPointer = actionPointer;
        _mapLabel = mapLabel?.Trim() ?? "";
        Phase = RuntimeDaySceneReadinessPhase.WaitingForMapFinish;
        Touch("RunTimeScheduler.OnEnterDaySceneMap action captured");
    }

    /// <summary>仅当前代次、当前地图动作的完成回调可置为就绪，旧动作回执不会恢复新场景的可读状态。</summary>
    public void CompleteSchedulerFinish(RuntimeDaySceneFinishToken token)
    {
        if (token.Kind != RuntimeDaySceneFinishKind.EnterDayMap
            || token.Generation != Generation
            || token.ActionPointer == 0
            || token.ActionPointer != _enterDayMapActionPointer
            || Phase != RuntimeDaySceneReadinessPhase.WaitingForMapFinish)
        {
            return;
        }

        Phase = RuntimeDaySceneReadinessPhase.Ready;
        Touch("RunTimeScheduler.OnEnterDaySceneMap action completed");
    }

    /// <summary>就绪阶段之外仍复核管理器身份及所有切场景/事件门禁，防止已就绪状态在新动作开始后继续放行。</summary>
    public bool CanRead(
        nint currentManagerPointer,
        bool isMapSwapping,
        bool hasPendingEnterDayEvent,
        bool hasValidMap,
        bool runTimeSchedulerIsExecuting,
        bool sceneDirectorIsInEvent,
        bool daySceneManagerIsExecutingScheduledActions,
        bool universalGameManagerIsSwitchScene)
    {
        return Ready
            && _managerPointer != 0
            && currentManagerPointer == _managerPointer
            && !isMapSwapping
            && !hasPendingEnterDayEvent
            && hasValidMap
            && !runTimeSchedulerIsExecuting
            && !sceneDirectorIsInEvent
            && !daySceneManagerIsExecutingScheduledActions
            && !universalGameManagerIsSwitchScene;
    }

    public void Reset(string reason)
    {
        _panelOpen = false;
        _firstEnterDepth = 0;
        _managerPointer = 0;
        _enterDayActionPointer = 0;
        _enterDayMapActionPointer = 0;
        _mapLabel = "";
        _manualWorkReturn = false;
        Generation++;
        Phase = RuntimeDaySceneReadinessPhase.Closed;
        Touch(reason);
    }

    private void Touch(string reason)
    {
        LastEvent = reason;
        Version++;
    }

    private static string FormatPhase(RuntimeDaySceneReadinessPhase phase)
    {
        return phase switch
        {
            RuntimeDaySceneReadinessPhase.Closed => "closed",
            RuntimeDaySceneReadinessPhase.WaitingForFirstEnter => "waiting-first-enter",
            RuntimeDaySceneReadinessPhase.WaitingForEnterDayAction => "waiting-enter-day-action",
            RuntimeDaySceneReadinessPhase.WaitingForEnterDayFinish => "waiting-enter-day-finish",
            RuntimeDaySceneReadinessPhase.WaitingForMapAction => "waiting-map-action",
            RuntimeDaySceneReadinessPhase.WaitingForMapFinish => "waiting-map-finish",
            RuntimeDaySceneReadinessPhase.Ready => "ready",
            _ => "unknown",
        };
    }
}
