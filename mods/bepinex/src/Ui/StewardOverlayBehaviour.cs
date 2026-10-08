using UnityEngine;
using MystiaStewardCompanion.Plugin.CompanionControl;

namespace MystiaStewardCompanion.Ui;

public sealed class StewardOverlayBehaviour : MonoBehaviour
{
    private object? _controller;

    public StewardOverlayBehaviour(IntPtr pointer) : base(pointer)
    {
    }

    private void Awake()
    {
        EnsureController();
    }

    private void Update()
    {
        EnsureController();
        (_controller as StewardOverlayController)?.Update();
    }

    private void LateUpdate()
    {
        (_controller as StewardOverlayController)?.LateUpdate();
    }

    private void OnDestroy()
    {
        ControlExitDiagnostic.Global(ExitGlobalStage.DestroyEntered);
        (_controller as StewardOverlayController)?.Dispose();
        _controller = null;
        ControlExitDiagnostic.Global(ExitGlobalStage.DestroyReturned);
    }

    private void OnApplicationQuit()
    {
        ControlExitDiagnostic.Global(ExitGlobalStage.QuitEntered);
        (_controller as StewardOverlayController)?.Dispose();
        ControlExitDiagnostic.Global(ExitGlobalStage.QuitReturned);
    }

    private void EnsureController()
    {
        _controller ??= StewardOverlayRuntimeContext.CreateController();
    }
}
