using System.Diagnostics;
using UnityEngine;

namespace MystiaStewardCompanion.FocusProbe;

// Registered synchronously by BasePlugin.Load. No component/wrapper enters the worker.
public sealed class FocusHeartbeatBehaviour : MonoBehaviour
{
    private static HeartbeatState? _state;

    public FocusHeartbeatBehaviour(IntPtr pointer) : base(pointer) { }

    internal static void Configure(HeartbeatState state)
    {
        Guard.That(Interlocked.CompareExchange(ref _state, state, null) == null, "Heartbeat component registration cannot be repeated.");
    }

    private void Update()
    {
        var state = Volatile.Read(ref _state);
        if (state == null) return;
        try { state.Publish(NativeMethods.CurrentThreadId(), Stopwatch.GetTimestamp()); }
        catch (Exception ex) { state.Stop("Unity heartbeat callback failed: " + ex.Message); }
    }

    private void OnDestroy() => Volatile.Read(ref _state)?.Stop("Unity heartbeat component was destroyed.");
}
