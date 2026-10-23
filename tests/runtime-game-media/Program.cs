using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using MystiaStewardCompanion.GameMedia;
using MystiaStewardCompanion.Save;

// 全部路径位于本测试新建的独占目录。测试驱动线程始终不变，模拟 Unity 每帧 Tick，
// 只让真实缓存读写在后台进行；断言实际清单/PNG/许可行为，不检查内部字段或计时器实现。
var root = Path.Combine(Path.GetTempPath(), "mystia-game-media-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var checks = 0;
void Check(bool value, string label) { checks++; if (!value) throw new InvalidOperationException(label); }
void Throws<T>(Action action, string label) where T : Exception
{
    try { action(); } catch (T) { checks++; return; }
    throw new InvalidOperationException(label);
}
void Until(GameMediaService service, string phase, SyntheticExtractor? observed = null, ISet<string>? phases = null)
{
    var watch = Stopwatch.StartNew();
    while (service.Status.Phase != phase)
    {
        if (watch.ElapsedMilliseconds > 10000 || service.Status.Phase == "failed")
            throw new InvalidOperationException("等待状态失败：" + service.Status.Phase + " → " + phase);
        var before = observed?.Calls ?? 0;
        phases?.Add(service.Status.Phase);
        service.Tick();
        phases?.Add(service.Status.Phase);
        if (observed != null && observed.Calls - before > 1) throw new InvalidOperationException("单帧提取超过一项");
        Thread.Sleep(1);
    }
}
try
{
    var png = MakePng();
    var plan = new GameMediaPlan(new string('a', 64), new[] { new GameMediaKey("food", 1, "合成料理"),
        new GameMediaKey("ingredient", 2, "合成食材"), new GameMediaKey("cooker", 1, "合成厨具") },
        new[] { new GameMediaRecipe(1, 101, new[] { 2, 2 }, 1) });
    var store = new GameMediaStore(root);
    Check(!store.HasCacheCandidate, "空目录不宣称已有缓存候选");
    var extractor = new SyntheticExtractor(plan, png);
    using var service = new GameMediaService(store);
    var diagnostics = new List<GameMediaDiagnostic>();
    var diagnosticReadCompleted = true;
    service.Diagnostic += _ => throw new IOException("合成日志写入故障");
    service.Diagnostic += value =>
    {
        diagnostics.Add(value);
        // 若发布者错误地持有资源状态锁，另一线程的状态读取会被阻断。本断言验证外部
        // 订阅者确实在锁外执行，同时上一订阅者的异常不能阻断这里或把资源作业改成失败。
        diagnosticReadCompleted &= Task.Run(() => service.Status).Wait(TimeSpan.FromSeconds(2));
    };
    Check(!service.IsReady && service.Status.Phase == "waiting", "未配置不就绪");
    Throws<GameMediaException>(() => service.Manifest(), "未就绪不可读清单");
    service.Configure(extractor);
    Until(service, "ready", extractor);
    Check(extractor.Calls == 3, "每帧只提取一个实体");
    var first = service.Manifest();
    Check(diagnosticReadCompleted && diagnostics.Select(value => value.Event)
        .SequenceEqual(new[] { "cache-check", "cache-miss", "complete" }), "诊断锁外低频输出且日志异常不阻断图片初始化");
    Check(diagnostics.Last().CacheHit == false && diagnostics.Last().Generation == first.Generation,
        "新提取完成诊断携带准确缓存结果与已发布代次");
    Check(first.Items.Count == 2 && first.Missing.Single().Key == "cooker:1", "精确缺图不冒充已提取");
    Check(first.Recipes.Single().IngredientIds.SequenceEqual(new[] { 2, 2 }), "保留重复食材和双身份");
    Check(service.Image(first.Generation, "food:1").SequenceEqual(png), "读取真实PNG字节");
    Throws<GameMediaException>(() => service.Image(first.Generation, "../../current.json"), "拒绝路径键");
    Throws<GameMediaException>(() => service.Image(new string('0', 32), "food:1"), "拒绝旧代次");

    using (var reused = new GameMediaService(store))
    {
        var reuseExtractor = new SyntheticExtractor(plan, png);
        var reuseDiagnostics = new List<GameMediaDiagnostic>();
        reused.Diagnostic += reuseDiagnostics.Add;
        Check(reused.Status.Phase == "verifying" && !reused.IsReady, "已有缓存在目录配置前仅为验证中，不授予ready");
        reused.Configure(reuseExtractor);
        var phases = new HashSet<string>();
        Until(reused, "ready", phases: phases);
        Check(reuseExtractor.Calls == 0 && reused.Manifest().Generation == first.Generation, "完整磁盘缓存无需再提取");
        Check(!phases.Contains("extracting") && phases.Contains("verifying"), "缓存命中全程不冒充实际提取阶段");
        Check(reuseDiagnostics.Select(value => value.Event).SequenceEqual(new[] { "cache-check", "cache-hit", "complete" })
            && reuseDiagnostics.Last().CacheHit == true && reuseDiagnostics.Last().Generation == first.Generation,
            "缓存命中仅三条固定诊断，无每图片或每帧日志");
    }
    var savedPointer = File.ReadAllBytes(Path.Combine(root, "current.json"));
    var savedPointerTime = File.GetLastWriteTimeUtc(Path.Combine(root, "current.json"));

    StartupCacheTests.Run(Check, root, plan, png);

    // 使用真实 Task 完成状态模拟纯托管指纹准备。断言完成后的下一次 Tick 就能推进，
    // 不等待五秒、不用脆弱的毫秒耗时阈值，也不让测试直接修改服务的私有计时器。
    using (var waking = new GameMediaService(store))
    {
        var pending = new PendingPlanExtractor(plan, png, signalCompletion: true);
        waking.Configure(pending);
        waking.Tick(); waking.Tick();
        Check(pending.PlanCalls == 1 && !waking.IsReady, "准备任务未完成不重复读取计划或提前ready");
        Check(waking.RequestReload().Phase == "verifying", "验证中reload复用当前作业");
        pending.Preparation.SetResult(true);
        waking.Tick();
        Check(pending.PlanCalls == 2 && waking.Status.Phase == "verifying", "纯托管准备完成后下一Tick继续，无固定五秒延迟");
        Until(waking, "ready");
        Check(pending.ExtractCalls == 0 && waking.Manifest().Generation == first.Generation,
            "验证中重复reload不会变成强制提取，缓存同代复用");
        Check(File.ReadAllBytes(Path.Combine(root, "current.json")).SequenceEqual(savedPointer)
            && File.GetLastWriteTimeUtc(Path.Combine(root, "current.json")) == savedPointerTime,
            "缓存复用不改写指针字节或写入时间");
    }
    using (var nativePending = new GameMediaService(store))
    {
        var pending = new PendingPlanExtractor(plan, png, signalCompletion: false);
        nativePending.Configure(pending); nativePending.Tick();
        pending.Preparation.SetResult(true); nativePending.Tick();
        Check(pending.PlanCalls == 1 && !nativePending.IsReady,
            "没有后台完成信号的原生目录等待仍节流，不退化为每帧读取");
    }
    using (var preparationThenNativePending = new GameMediaService(store))
    {
        var pending = new PendingPlanExtractor(plan, png, signalCompletion: true) { NativeReady = false };
        preparationThenNativePending.Configure(pending); preparationThenNativePending.Tick();
        pending.Preparation.SetResult(true); preparationThenNativePending.Tick(); preparationThenNativePending.Tick();
        Check(pending.PlanCalls == 2 && !preparationThenNativePending.IsReady,
            "后台完成唤醒只消费一次，随后目录仍未就绪时恢复原生读取节流");
    }
    using (var replaced = new GameMediaService(store))
    {
        var old = new PendingPlanExtractor(plan, png, signalCompletion: true);
        replaced.Configure(old); replaced.Tick();
        var current = new PendingPlanExtractor(plan, png, signalCompletion: true);
        replaced.Configure(current); replaced.Tick();
        old.Preparation.SetResult(true); replaced.Tick();
        Check(current.PlanCalls == 1 && !replaced.IsReady, "旧目录准备完成不能唤醒或发布新目录作业");
        replaced.Dispose(); current.Preparation.SetResult(true); replaced.Tick();
        Check(current.PlanCalls == 1 && !replaced.IsReady, "服务关闭后准备完成也不再调用提取器");
    }
    using (var failedPreparation = new GameMediaService(store))
    {
        var pending = new PendingPlanExtractor(plan, png, signalCompletion: true);
        failedPreparation.Configure(pending); failedPreparation.Tick();
        pending.Preparation.SetException(new IOException("合成指纹读取失败"));
        failedPreparation.Tick();
        Check(failedPreparation.Status.Phase == "failed" && !failedPreparation.IsReady && pending.ExtractCalls == 0,
            "后台版本验证失败及时进入failed，不使用未经验证的已有缓存");
    }
    var invalidPng = png.ToArray(); invalidPng[20] ^= 1;
    Throws<InvalidDataException>(() => store.Commit(plan, Guid.NewGuid().ToString("N"),
        new Dictionary<string, GameMediaImage> { ["food:1"] = new(invalidPng, 1, 1), ["ingredient:2"] = new(png, 1, 1) },
        new[] { new GameMediaMissing("cooker:1", "缺图") }), "坏CRC拒绝发布");
    Check(File.ReadAllBytes(Path.Combine(root, "current.json")).SequenceEqual(savedPointer), "失败保留原指针字节");
    using (var cancelled = new CancellationTokenSource())
    {
        cancelled.Cancel();
        Throws<OperationCanceledException>(() => store.Commit(plan, Guid.NewGuid().ToString("N"),
            new Dictionary<string, GameMediaImage>(), Array.Empty<GameMediaMissing>(), cancelled.Token), "取消作业不发布");
    }
    Check(File.ReadAllBytes(Path.Combine(root, "current.json")).SequenceEqual(savedPointer), "取消保留原代");

    // 损坏已发布图片必须立即撤销ready；重试会重新提取整代，不混用原代的其余图片。
    File.WriteAllBytes(Path.Combine(root, first.Generation, "food-1.png"), invalidPng);
    Throws<GameMediaException>(() => service.Image(first.Generation, "food:1"), "读取损坏图片失败");
    Check(service.Status.Phase == "failed" && !service.IsReady, "缓存损坏撤销ready");
    Check(diagnostics.Count(value => value.Event == "failed") == 1, "同一作业失效只记录一次失败诊断");
    Check(store.TryLoad(plan) == null, "启动验证拒绝损坏图片");
    using (var restarted = new GameMediaService(store))
    {
        var restartExtractor = new SyntheticExtractor(plan, png);
        restarted.Configure(restartExtractor);
        var phases = new HashSet<string>();
        Until(restarted, "ready", restartExtractor, phases);
        Check(restartExtractor.Calls == 3 && restarted.Manifest().Generation != first.Generation,
            "新服务启动遇到损坏缓存会自动整代重提，无需手动reload");
        Check(phases.Contains("verifying") && phases.Contains("extracting"), "缓存候选损坏先验证再明确进入实际提取");
    }
    diagnostics.Clear();
    Check(service.RequestReload().Phase == "waiting", "显式重读不伪装为可复用缓存验证");
    service.RequestReload(); Until(service, "ready");
    Check(extractor.Calls == 6 && service.Manifest().Generation != first.Generation, "重复reload合并为一轮整代提取");
    var second = service.Manifest();
    Check(diagnostics.Select(value => value.Event).SequenceEqual(new[] { "forced-extract", "complete" })
        && diagnostics.All(value => value.CacheHit == false), "显式重读诊断不伪称缓存失效或命中");
    Throws<GameMediaException>(() => service.Image(first.Generation, "food:1"), "切代后旧PNG不可读");
    Check(!Directory.Exists(Path.Combine(root, first.Generation)), "成功切代回收旧受管目录");

    var changedPlan = plan with { SourceFingerprint = new string('b', 64) };
    Check(store.TryLoad(changedPlan) == null, "资源指纹变化拒绝旧缓存");
    Check(store.TryLoad(plan with { Items = plan.Items.Take(2).ToArray() }) == null, "目录集合变化拒绝缓存");
    Throws<InvalidDataException>(() => GameMediaStore.ValidatePlan(plan with {
        Items = new[] { new GameMediaKey("food", -1, "错误") } }), "负身份拒绝");
    Throws<InvalidDataException>(() => GameMediaStore.ValidatePlan(plan with {
        Items = new[] { plan.Items[0], plan.Items[0] } }), "重复身份拒绝");
    Throws<InvalidDataException>(() => GameMediaStore.ValidatePng(new byte[GameMediaStore.MaximumImageBytes + 1], 1, 1), "单图上限");
    Throws<InvalidOperationException>(() => Task.Run(() => service.Tick()).GetAwaiter().GetResult(), "禁止后台线程提取");

    using var profile = JsonDocument.Parse("{\"automationEnabled\":true,\"autoRareOrderEnabled\":true,\"autoNormalOrderEnabled\":true,\"autoPrepCollectCooking\":true,\"autoPrepCompleteOrder\":true,\"autoNormalDeliverFood\":true,\"autoNormalCompleteOrder\":true}");
    RuntimeAutomationControlState.ConfigureResourceReadiness(() => service.IsReady);
    RuntimeAutomationControlState.PublishAuthority(profile.RootElement, 1, "test", "test");
    RuntimeAutomationControlState.PublishLease(1, DateTime.UtcNow.AddMinutes(1));
    using (var permit = RuntimeAutomationControlState.AcquirePermit(RuntimeAutomationControlTargetKind.Rare,
        RuntimeAutomationControlStage.FoodDelivery, false, DateTime.UtcNow)) Check(permit.Allowed, "ready允许原有效租约");
    service.RequestReload();
    foreach (var kind in Enum.GetValues<RuntimeAutomationControlTargetKind>())
    foreach (var stage in Enum.GetValues<RuntimeAutomationControlStage>())
    using (var permit = RuntimeAutomationControlState.AcquirePermit(kind, stage, true, DateTime.UtcNow))
        Check(!permit.Allowed && permit.Decision.ReasonCode == "game-media-not-ready", "重读阻止旧租约持续料理所有副作用阶段");
    extractor.Fail = true;
    for (var i = 0; i < 5000 && service.Status.Phase != "failed"; i++) { service.Tick(); Thread.Sleep(1); }
    Check(service.Status.Phase == "failed" && !service.IsReady, "提取失败不伪称成功");
    Check(store.TryLoad(plan)?.Generation == second.Generation, "重读失败仍保留原磁盘代但不对外ready");
    extractor.Fail = false; service.RequestReload(); Until(service, "ready");
    service.Dispose();
    Check(!service.IsReady, "关闭立即撤销ready");
    Throws<GameMediaException>(() => service.RequestReload(), "关闭后不再排队");
    Console.WriteLine($"PASS runtime-game-media: {checks} checks");
    return 0;
}
finally
{
    RuntimeAutomationControlState.ConfigureResourceReadiness(null);
    RuntimeAutomationControlState.Reset("测试结束");
    // 仅清理上述随机独占临时根，不读取/写入任何游戏目录。
    Directory.Delete(root, recursive: true);
}

static byte[] MakePng()
{
    using var output = new MemoryStream(); output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
    void Chunk(string type, byte[] body)
    {
        var header = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(header, (uint)body.Length); output.Write(header);
        var name = Encoding.ASCII.GetBytes(type); output.Write(name); output.Write(body); uint crc = uint.MaxValue;
        foreach (var value in name.Concat(body)) { crc ^= value; for (var i = 0; i < 8; i++) crc = (crc & 1) == 0 ? crc >> 1 : 0xedb88320U ^ (crc >> 1); }
        BinaryPrimitives.WriteUInt32BigEndian(header, ~crc); output.Write(header);
    }
    var ihdr = new byte[13]; BinaryPrimitives.WriteInt32BigEndian(ihdr, 1); BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), 1); ihdr[8] = 8; ihdr[9] = 6;
    Chunk("IHDR", ihdr); using var compressed = new MemoryStream();
    using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true)) zlib.Write(new byte[] { 0, 255, 0, 0, 255 });
    Chunk("IDAT", compressed.ToArray()); Chunk("IEND", Array.Empty<byte>()); return output.ToArray();
}

sealed class SyntheticExtractor : IRuntimeGameMediaExtractor
{
    private readonly GameMediaPlan _plan; private readonly byte[] _png;
    internal int Calls; internal bool Fail;
    internal SyntheticExtractor(GameMediaPlan plan, byte[] png) { _plan = plan; _png = png; }
    public bool TryCreatePlan(out GameMediaPlan? plan, out string error) { plan = _plan; error = ""; return true; }
    public GameMediaExtractState Extract(GameMediaKey key, out GameMediaImage? image, out string message)
    {
        Calls++; image = new(_png.ToArray(), 1, 1); message = "";
        return Fail ? GameMediaExtractState.Failed : key.Kind == "cooker" ? GameMediaExtractState.Missing : GameMediaExtractState.Ready;
    }
}

/// <summary>
/// 只暴露纯托管任务的完成信号，按真实提取器的调用约定返回计划或抛出准备异常。
/// signalCompletion=false 模拟依赖 Unity 目录就绪但没有可唤醒后台任务的等待分支。
/// </summary>
sealed class PendingPlanExtractor : IRuntimeGameMediaExtractor
{
    private readonly GameMediaPlan _plan;
    private readonly byte[] _png;
    private readonly bool _signalCompletion;
    internal readonly TaskCompletionSource<bool> Preparation = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal int PlanCalls;
    internal int ExtractCalls;
    internal bool NativeReady = true;
    public Task? PendingPlanPreparation { get; private set; }
    internal PendingPlanExtractor(GameMediaPlan plan, byte[] png, bool signalCompletion)
    { _plan = plan; _png = png; _signalCompletion = signalCompletion; }
    public bool TryCreatePlan(out GameMediaPlan? plan, out string error)
    {
        PlanCalls++; plan = null; error = "合成准备等待";
        PendingPlanPreparation = null;
        if (!Preparation.Task.IsCompleted)
        {
            PendingPlanPreparation = _signalCompletion ? Preparation.Task : null;
            return false;
        }
        Preparation.Task.GetAwaiter().GetResult();
        if (!NativeReady) return false;
        plan = _plan; error = ""; return true;
    }
    public GameMediaExtractState Extract(GameMediaKey key, out GameMediaImage? image, out string message)
    {
        ExtractCalls++; image = new(_png.ToArray(), 1, 1); message = "";
        return key.Kind == "cooker" ? GameMediaExtractState.Missing : GameMediaExtractState.Ready;
    }
}
