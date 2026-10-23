using MystiaStewardCompanion.Save;
using UnityEngine;

namespace MystiaStewardCompanion.GameMedia;

/// <summary>
/// 主菜单图片复用的只读入口。只在主线程读取已完成依赖加载的游戏状态；不触发 DLC getter、
/// Steam 查询或业务目录加载。跨线程只交付复制后的字符串，不保存 IL2CPP wrapper。
/// </summary>
internal sealed class RuntimeGameMediaStartupProbe
{
    private readonly int _mainThreadId = Environment.CurrentManagedThreadId;
    private readonly long _deadline = Environment.TickCount64 + 120000;
    private readonly TaskCompletionSource<string?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _nextAttempt;
    private bool _started;
    private readonly Action<string, int>? _reportIdentity;
    internal Task<string?> Completion => _completion.Task;

    /// <summary>诊断只报告固定原因码与文件数；失败不得改变缓存验证或触发额外平台查询。</summary>
    internal RuntimeGameMediaStartupProbe(Action<string, int>? reportIdentity = null)
        => _reportIdentity = reportIdentity;

    internal void Tick()
    {
        if (Environment.CurrentManagedThreadId != _mainThreadId)
            throw new InvalidOperationException("图片启动证据只能在 Controller 主线程读取。");
        if (_started || _completion.Task.IsCompleted || Environment.TickCount64 < _nextAttempt) return;
        _nextAttempt = Environment.TickCount64 + 250;
        // 超时只表示无法提前复用，不把等待时间当作就绪证明，也不使主菜单永久反复校验。
        if (Environment.TickCount64 >= _deadline)
        {
            ReportIdentity("startup-state-timeout", 0);
            _completion.TrySetResult(null);
            return;
        }
        try
        {
            var loading = RuntimeReflectionUtility.FindType("Common.LoadingSceneManager");
            if (loading == null || RuntimeReflectionUtility.GetStaticMemberValue(loading, "IsDependenciesLoaded") is not true) return;
            var labels = ReadConfirmedDlcLabels();
            // 依赖完成与 DLC 缓存字段填充可能跨不同初始化路径；字段未知时只低频读状态，
            // 不提前终结这次启动机会，也不调用懒初始化 getter。上方 deadline 仍保证有界。
            if (labels == null) return;
            _started = true;
            var path = Path.Combine(Application.streamingAssetsPath, "aa", "catalog.bundle");
            var unity = Application.unityVersion;
            var version = Application.version;
            // 后台任务只持有纯托管路径、版本和标签。未知/失败一次回退到原有读档验证流程。
            _ = CompleteAsync(path, unity, version, labels);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _started = true;
            ReportIdentity("startup-state-unavailable", 0);
            _completion.TrySetResult(null);
        }
    }

    private async Task CompleteAsync(string path, string unity, string version, string[] labels)
    {
        try
        {
            var identity = await Task.Run(() => GameMediaSourceIdentity.Read(path, unity, version, labels)).ConfigureAwait(false);
            ReportIdentity(identity.StartupFailureReason ?? "ready", identity.BundleCount);
            _completion.TrySetResult(identity.StartupFingerprint);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ReportIdentity("source-read-failed", 0);
            _completion.TrySetResult(null);
        }
    }

    private void ReportIdentity(string reason, int bundleCount)
    {
        // 只在本次探测结束时记录一次，不在每帧/250ms 轮询中刷日志；日志故障不能阻断完成信号。
        try { _reportIdentity?.Invoke(reason, bundleCount); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { }
    }

    internal static string[]? ReadConfirmedDlcLabels()
    {
        var loading = RuntimeReflectionUtility.FindType("Common.LoadingSceneManager");
        var manager = RuntimeReflectionUtility.FindType("GamePlatform.MonoScripts.GamePlatformManager");
        if (loading == null || manager == null
            || RuntimeReflectionUtility.GetStaticMemberValue(loading, "IsDependenciesLoaded") is not true) return null;
        var platform = RuntimeReflectionUtility.GetStaticMemberValue(manager, "Platform");
        var steam = RuntimeReflectionUtility.TryCastRuntimeObject(platform, "GamePlatform.Core.SteamPlatform");
        if (steam == null || RuntimeReflectionUtility.GetMemberValue(steam, "m_SteamInit") is not true) return null;
        var profile = RuntimeReflectionUtility.GetStaticMemberValue(loading, "LoadedGameDataProfile");
        if (profile == null) return null;
        // Native RVA 0x7372B0 的公开 ActiveDLCLabel 会首次写缓存并调用平台，所以只读已缓存字段。
        // RVA 0x7E3020 在全部依赖加载完成后才设置 IsDependenciesLoaded；平台未初始化时返回
        // 空 DLC 并不表示用户没有 DLC，因此还必须检查 SteamPlatform.m_SteamInit。
        var raw = RuntimeReflectionUtility.GetMemberValue(profile, "m_ActiveDLCLabel");
        if (!RuntimeConcreteCollectionReader.TryReadList(raw, out var values, out _)
            || values.Any(value => value is not string)) return null;
        var labels = values.Cast<string>().ToArray();
        return GameMediaSourceIdentity.ValidLabels(labels) ? labels : null;
    }
}
