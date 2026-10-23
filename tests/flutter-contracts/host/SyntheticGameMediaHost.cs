using System.Diagnostics;
using MystiaStewardCompanion.GameMedia;

/// <summary>
/// 生产 UI 到真实 HTTP 的可选合成资源宿主。只创建独占临时缓存和一个合成 PNG；
/// 不引用运行时提取器、不访问游戏目录或 Unity API。所有 Tick 固定在同一个专属线程，
/// HTTP 重读仍走真实服务状态机，不把服务端 ready 常量化，也不削弱客户端验证。
/// </summary>
internal sealed class SyntheticGameMediaHost : IDisposable
{
    private readonly ManualResetEventSlim _stop = new();
    private readonly Thread _thread;
    private readonly TaskCompletionSource<GameMediaService> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal GameMediaService Service { get; private set; } = null!;

    private SyntheticGameMediaHost(string root)
    {
        _thread = new Thread(() => Run(root)) { IsBackground = true, Name = "synthetic-game-media-driver" };
    }

    internal static SyntheticGameMediaHost Start(string root)
    {
        var host = new SyntheticGameMediaHost(root);
        host._thread.Start();
        try
        {
            host.Service = host._ready.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            return host;
        }
        catch { host.Dispose(); throw; }
    }

    private void Run(string root)
    {
        try
        {
            using var service = new GameMediaService(new GameMediaStore(root));
            service.Configure(new Extractor());
            var watch = Stopwatch.StartNew();
            while (!_stop.IsSet)
            {
                service.Tick();
                if (service.IsReady) _ready.TrySetResult(service);
                if (!_ready.Task.IsCompleted && (service.Status.Phase == "failed" || watch.ElapsedMilliseconds > 9000))
                    throw new InvalidOperationException("合成图片服务初始化失败。");
                _stop.Wait(5);
            }
        }
        catch (Exception exception)
        {
            _ready.TrySetException(new InvalidOperationException("合成图片驱动失败。", exception));
        }
    }

    public void Dispose()
    {
        _stop.Set();
        if (_thread.IsAlive && !_thread.Join(TimeSpan.FromSeconds(5)))
            throw new TimeoutException("合成图片驱动未按期结束。");
        _stop.Dispose();
    }

    private sealed class Extractor : IRuntimeGameMediaExtractor
    {
        private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        public bool TryCreatePlan(out GameMediaPlan? plan, out string error)
        {
            plan = new GameMediaPlan(new string('d', 64), new[] { new GameMediaKey("food", 1, "合成料理") }, Array.Empty<GameMediaRecipe>());
            error = ""; return true;
        }
        public GameMediaExtractState Extract(GameMediaKey item, out GameMediaImage? image, out string message)
        { image = new GameMediaImage(Png.ToArray(), 1, 1); message = ""; return GameMediaExtractState.Ready; }
    }
}
