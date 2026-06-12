using UnityEngine;

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

    private void OnGUI()
    {
        (_controller as StewardOverlayController)?.OnGUI();
    }

    private void OnDestroy()
    {
        (_controller as StewardOverlayController)?.Dispose();
        _controller = null;
    }

    private void OnApplicationQuit()
    {
        (_controller as StewardOverlayController)?.Dispose();
    }

    private void EnsureController()
    {
        _controller ??= StewardOverlayRuntimeContext.CreateController();
    }
}
