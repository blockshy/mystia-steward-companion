using UnityEngine;

namespace MystiaStewardCompanion.Ui;

/// <summary>将 Unity 主线程生命周期转发给托管控制器，并在配置尚未就绪时逐帧尝试初始化。</summary>
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
        (_controller as StewardOverlayController)?.Dispose();
        _controller = null;
    }

    // 退出与销毁回调可能先后发生，控制器的 Dispose 必须保持幂等。
    private void OnApplicationQuit()
    {
        (_controller as StewardOverlayController)?.Dispose();
    }

    private void EnsureController()
    {
        _controller ??= StewardOverlayRuntimeContext.CreateController();
    }
}
