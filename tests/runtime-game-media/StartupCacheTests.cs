using System.Diagnostics;
using System.Text.Json.Nodes;
using MystiaStewardCompanion.GameMedia;

/// <summary>
/// 在独占临时目录验证真实磁盘 PNG/清单与启动凭据。完全不加载 Unity 或真实游戏目录，
/// 重点验证无需配置提取器即可复用、旧代迁移、失效回退和真实目录到来后的安全边界。
/// </summary>
internal static class StartupCacheTests
{
    internal static void Run(Action<bool, string> check, string parent, GameMediaPlan original, byte[] png)
    {
        var root = Path.Combine(parent, "startup-cache");
        var store = new GameMediaStore(root);
        var fingerprint = new string('b', 64);
        var plan = original with { StartupFingerprint = fingerprint };
        var images = new Dictionary<string, GameMediaImage>
        {
            ["food:1"] = new(png, 1, 1), ["ingredient:2"] = new(png, 1, 1),
        };
        var missing = new[] { new GameMediaMissing("cooker:1", "合成缺图") };
        var first = store.Commit(original, Guid.NewGuid().ToString("N"), images, missing);
        var pointerPath = Path.Combine(root, "current.json");
        var pointer = File.ReadAllBytes(pointerPath);
        var pointerTime = File.GetLastWriteTimeUtc(pointerPath);
        check(store.TryLoadStartup(fingerprint, out var missingReceiptReason) == null
            && missingReceiptReason == "startup-receipt-missing",
            "旧代无启动凭据明确报告缺失，不猜测许可/目录或提前ready");
        var blockedRoot = Path.Combine(parent, "startup-receipt-unwritable");
        var blockedStore = new GameMediaStore(blockedRoot);
        var blocked = blockedStore.Commit(original, Guid.NewGuid().ToString("N"), images, missing);
        var blockedPointer = File.ReadAllBytes(Path.Combine(blockedRoot, "current.json"));
        // 用同名目录确定性模拟不能发布附属凭据，无需修改机器ACL或权限，且不删除异常证据。
        Directory.CreateDirectory(Path.Combine(blockedRoot, blocked.Generation, "startup.json"));
        check(blockedStore.TryLoad(plan)?.Generation == blocked.Generation
            && File.ReadAllBytes(Path.Combine(blockedRoot, "current.json")).SequenceEqual(blockedPointer)
            && blockedStore.ReadImage(blocked, "food:1").SequenceEqual(png),
            "旧代补写凭据失败不撤销已完整验证的图片，也不改原指针或PNG");
        check(blockedStore.TryLoadStartup(fingerprint) == null,
            "附属凭据写入失败后不伪称下次启动可提前复用");
        check(store.TryLoad(plan)?.Generation == first.Generation, "真实目录验证旧代后原代补写凭据，无需重提PNG");
        check(File.ReadAllBytes(pointerPath).SequenceEqual(pointer) && File.GetLastWriteTimeUtc(pointerPath) == pointerTime,
            "升级启动凭据不改写已发布指针或代次");
        var receiptPath = Path.Combine(root, first.Generation, "startup.json");
        var receipt = File.ReadAllBytes(receiptPath);
        var receiptTime = File.GetLastWriteTimeUtc(receiptPath);
        check(store.TryLoad(plan) != null && File.GetLastWriteTimeUtc(receiptPath) == receiptTime,
            "已存在相同凭据不每次重写");
        check(!System.Text.Encoding.UTF8.GetString(receipt).Contains(parent, StringComparison.Ordinal),
            "启动凭据不包含本机路径");
        check(store.TryLoad(plan with { StartupFingerprint = new string('c', 64) }) == null,
            "DLC/资源元数据变化后真实目录也不得重新命中旧来源图片");

        using (var startup = new GameMediaService(store, Task.FromResult<string?>(fingerprint)))
        {
            var diagnostics = new List<GameMediaDiagnostic>();
            startup.Diagnostic += diagnostics.Add;
            Pump(startup, "ready");
            check(startup.Manifest().Generation == first.Generation && startup.Image(first.Generation, "food:1").SequenceEqual(png),
                "不配置提取器、不读档也可完整校验并读取原代PNG");
            check(diagnostics.Select(value => value.Event).SequenceEqual(new[] { "cache-check", "cache-hit", "complete" }),
                "启动缓存命中只发布固定三条诊断");
            for (var i = 0; i < 30; i++) startup.Tick();
            check(diagnostics.Count == 3, "ready后持续Tick不重复缓存校验或诊断");
            var notifications = 0;
            startup.ReadinessChanged += () => notifications++;
            var actual = new PendingPlanExtractor(plan with
            {
                Items = plan.Items.Reverse().ToArray(),
                Recipes = plan.Recipes.Select(value => value with { IngredientIds = value.IngredientIds.ToArray() }).ToArray(),
            }, png, signalCompletion: true);
            startup.Configure(actual); startup.Tick();
            check(startup.IsReady && notifications == 0, "真实目录准备中保留已经完整验证的图片，不闪回初始化");
            actual.Preparation.SetResult(true); startup.Tick();
            check(startup.IsReady && notifications == 0 && actual.ExtractCalls == 0
                && startup.Manifest().Generation == first.Generation,
                "同一完整计划只确认目录，不撤权限、不重读PNG或重提图片");

            var changed = plan with { Recipes = new[] { new GameMediaRecipe(1, 101, new[] { 2 }, 1) } };
            startup.Configure(new SyntheticExtractor(changed, png)); startup.Tick();
            check(!startup.IsReady && notifications == 1, "真实配方重复食材数量改变立即撤销旧ready");
            Pump(startup, "ready");
            check(startup.Manifest().Generation != first.Generation, "不同完整计划不复用旧代");
        }

        // 以下每项重新发布受控原计划，避免损坏用例污染后续断言；所有写入仍在本次临时根内。
        var current = store.Commit(plan, Guid.NewGuid().ToString("N"), images, missing);
        using (var wrong = new GameMediaService(store, Task.FromResult<string?>(new string('c', 64))))
        {
            var events = new List<GameMediaDiagnostic>(); wrong.Diagnostic += events.Add;
            Pump(wrong, "waiting");
            for (var i = 0; i < 30; i++) wrong.Tick();
            check(!wrong.IsReady && events.Select(value => value.Event).SequenceEqual(new[] { "cache-check", "cache-miss" }),
                "版本/DLC/资源身份变化一次回退等待，不反复读取或授予ready");
            check(events.Last().Reason == "startup-identity-changed",
                "缓存未命中诊断明确说明启动身份变化，不笼统冒充正在提取");
            var extractor = new SyntheticExtractor(plan, png); wrong.Configure(extractor); Pump(wrong, "ready");
            check(extractor.Calls == 0, "身份未知后仍可通过真实目录安全恢复缓存");
        }
        using (var unknown = new GameMediaService(store, Task.FromResult<string?>(null)))
        {
            Pump(unknown, "waiting"); check(!unknown.IsReady, "缺失可靠DLC证据不能当作无DLC成功");
        }
        using (var reload = new GameMediaService(store, Task.FromResult<string?>(fingerprint)))
        {
            Pump(reload, "ready"); reload.RequestReload(); reload.Tick();
            check(!reload.IsReady && reload.Status.Phase == "waiting", "主菜单显式重读撤ready并等待真实提取器");
            var extractor = new SyntheticExtractor(plan, png);
            reload.Configure(extractor); Pump(reload, "ready");
            check(extractor.Calls == plan.Items.Count && reload.Manifest().Generation != current.Generation,
                "主菜单已排队的显式重读跨目录配置仍强制提取，不退化为旧缓存命中");
        }
        // 显式重读已原子建立新代，后续迟到任务/损坏测试使用该当前代，避免验证被回收文件。
        current = store.TryLoad(plan)!;
        var delayed = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var replaced = new GameMediaService(store, delayed.Task))
        {
            var extractor = new SyntheticExtractor(plan, png); replaced.Configure(extractor);
            delayed.SetResult(new string('d', 64)); Pump(replaced, "ready");
            check(replaced.Manifest().Generation == current.Generation && extractor.Calls == 0,
                "旧启动任务迟到不能覆盖已配置真实目录作业");
        }
        var afterDispose = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = new GameMediaService(store, afterDispose.Task);
        disposed.Dispose(); afterDispose.SetResult(fingerprint); disposed.Tick();
        check(!disposed.IsReady, "关闭后启动任务完成不得重新发布ready");

        receiptPath = Path.Combine(root, current.Generation, "startup.json");
        receipt = File.ReadAllBytes(receiptPath);
        var bad = JsonNode.Parse(receipt)!.AsObject(); bad["manifestSha256"] = new string('0', 64);
        File.WriteAllText(receiptPath, bad.ToJsonString());
        check(store.TryLoadStartup(fingerprint) == null, "凭据必须绑定当前清单真实摘要");
        File.WriteAllBytes(receiptPath, receipt);
        bad = JsonNode.Parse(receipt)!.AsObject(); bad["unrecognized"] = true;
        File.WriteAllText(receiptPath, bad.ToJsonString());
        check(store.TryLoadStartup(fingerprint) == null, "启动凭据未知字段拒绝");
        File.WriteAllBytes(receiptPath, receipt);
        var image = Path.Combine(root, current.Generation, "food-1.png");
        var corrupted = File.ReadAllBytes(image); corrupted[20] ^= 1; File.WriteAllBytes(image, corrupted);
        check(store.TryLoadStartup(fingerprint, out var corruptedReason) == null
            && corruptedReason == "cache-content-invalid", "启动复用仍逐张校验PNG，并明确报告内容损坏");
        File.WriteAllBytes(image, png);
        check(store.TryLoadStartup(fingerprint)?.Manifest.Generation == current.Generation, "损坏恢复后完整校验可复用同代");

        SourceIdentity(check, parent);
        LargeSourceIdentity(check, parent, original, png);
    }

    private static void SourceIdentity(Action<bool, string> check, string parent)
    {
        var root = Path.Combine(parent, "source"); Directory.CreateDirectory(root);
        var bundles = Path.Combine(root, "StandaloneWindows64"); Directory.CreateDirectory(bundles);
        var catalog = Path.Combine(root, "catalog.bundle"); File.WriteAllBytes(catalog, new byte[] { 1, 2, 3 });
        var bundle = Path.Combine(bundles, "core_012345.bundle"); File.WriteAllBytes(bundle, new byte[] { 4, 5, 6 });
        var a = GameMediaSourceIdentity.Read(catalog, "unity", "game", new[] { "CORE", "DLC1" });
        check(a.StartupFingerprint?.Length == 64 && a.StartupFailureReason == null && a.BundleCount == 1,
            "完整资源索引/元数据/DLC证据可建立启动摘要，并报告准确包数");
        check(a == GameMediaSourceIdentity.Read(catalog, "unity", "game", new[] { "DLC1", "CORE" }), "DLC集合顺序不改变身份");
        var noDlc = GameMediaSourceIdentity.Read(catalog, "unity", "game", new[] { "CORE" });
        check(noDlc.SourceFingerprint == a.SourceFingerprint && noDlc.StartupFingerprint != a.StartupFingerprint,
            "活动DLC改变必须失效，但保留旧source公式以便真实目录升级旧缓存");
        check(GameMediaSourceIdentity.Read(catalog, "unity2", "game", new[] { "CORE", "DLC1" }).StartupFingerprint != a.StartupFingerprint,
            "Unity版本改变启动身份");
        check(GameMediaSourceIdentity.Read(catalog, "unity", "game2", new[] { "CORE", "DLC1" }).StartupFingerprint != a.StartupFingerprint,
            "游戏版本改变启动身份");
        foreach (var labels in new IReadOnlyList<string>?[] { null, Array.Empty<string>(), new[] { "CORE", "CORE" } })
        {
            var unknown = GameMediaSourceIdentity.Read(catalog, "unity", "game", labels);
            check(unknown.StartupFingerprint == null && unknown.StartupFailureReason == "dlc-state-unavailable"
                && unknown.BundleCount == 0 && unknown.SourceFingerprint == a.SourceFingerprint,
                "未知、空或重复DLC集合明确拒绝启动复用，不将未枚举包数当作已确认空目录");
        }
        File.SetLastWriteTimeUtc(bundle, File.GetLastWriteTimeUtc(bundle).AddSeconds(10));
        check(GameMediaSourceIdentity.Read(catalog, "unity", "game", new[] { "CORE", "DLC1" }).StartupFingerprint != a.StartupFingerprint,
            "资源文件元数据改变拒绝启动复用");
        File.WriteAllBytes(catalog, new byte[] { 1, 2, 4 });
        check(GameMediaSourceIdentity.Read(catalog, "unity", "game", new[] { "CORE", "DLC1" }).SourceFingerprint != a.SourceFingerprint,
            "等长资源索引内容改变仍由真实SHA识别");
        File.Delete(bundle);
        var empty = GameMediaSourceIdentity.Read(catalog, "unity", "game", new[] { "CORE", "DLC1" });
        check(empty.StartupFingerprint == null && empty.StartupFailureReason == "bundle-set-empty"
            && empty.BundleCount == 0, "资源文件缺失明确报告空集合，不把空清单当作可信来源");
        // 用普通空文件和缺失目录制造确定性失败，不依赖操作系统 ACL，也不创建路径链接。
        File.WriteAllBytes(bundle, Array.Empty<byte>());
        var invalid = GameMediaSourceIdentity.Read(catalog, "unity", "game", new[] { "CORE", "DLC1" });
        check(invalid.StartupFingerprint == null && invalid.StartupFailureReason == "bundle-metadata-invalid"
            && invalid.BundleCount == 0, "零长度包明确报告无效元数据，失败包不计入已验证数量");
        File.Delete(bundle); Directory.Delete(bundles);
        var unavailable = GameMediaSourceIdentity.Read(catalog, "unity", "game", new[] { "CORE", "DLC1" });
        check(unavailable.StartupFingerprint == null && unavailable.StartupFailureReason == "bundle-metadata-unavailable"
            && unavailable.BundleCount == 0, "资源目录不可用有固定原因码，不泄露异常中的本机路径");
    }

    /// <summary>
    /// 用刚超过旧上限的真实文件集合复现原故障，再通过生产 Store/Service 验证实际复用。
    /// 4,097 个一字节包足以捕获旧限制及截断清单，避免为上限测试创建 65,537 个磁盘文件。
    /// 文件只包含合成字节，不是 Unity 包；本用例验证的是来源元数据协议，不验证解包器。
    /// </summary>
    private static void LargeSourceIdentity(Action<bool, string> check, string parent, GameMediaPlan original, byte[] png)
    {
        const int bundleCount = 4097;
        var root = Path.Combine(parent, "source-over-4096");
        var bundles = Path.Combine(root, "StandaloneWindows64"); Directory.CreateDirectory(bundles);
        var catalog = Path.Combine(root, "catalog.bundle"); File.WriteAllBytes(catalog, new byte[] { 8, 6, 4, 2 });
        var modified = new DateTime(2020, 1, 2, 3, 4, 6, DateTimeKind.Utc);
        // 故意按文件名倒序创建；指纹依据文件名的序数排序，不能依赖文件创建或枚举顺序。
        for (var index = bundleCount - 1; index >= 0; index--)
        {
            var path = Path.Combine(bundles, $"chunk-{index:D5}.bundle");
            File.WriteAllBytes(path, new byte[] { 42 }); File.SetLastWriteTimeUtc(path, modified);
        }
        var labels = new[] { "CORE", "DLC1" };
        var identity = GameMediaSourceIdentity.Read(catalog, "unity", "game", labels);
        check(identity.StartupFingerprint?.Length == 64 && identity.StartupFailureReason == null
            && identity.BundleCount == bundleCount, "超过旧4096上限的完整资源集合生成可信启动指纹并报告全部包数");
        var fingerprint = identity.StartupFingerprint ?? throw new InvalidOperationException("大资源集合未生成启动指纹。");
        check(GameMediaSourceIdentity.Read(catalog, "unity", "game", labels.Reverse().ToArray()) == identity,
            "大资源集合重复读取和DLC集合重排保持同一来源身份");

        // 先发布没有 startup.json 的旧代；只有真实目录计划完整验证成功后才能补写凭据。
        // 这里让 SourceFingerprint 来自上述真实文件读取，避免使用任意常量绕过故障链。
        var storeRoot = Path.Combine(parent, "large-source-startup-cache");
        var store = new GameMediaStore(storeRoot);
        var oldPlan = original with { SourceFingerprint = identity.SourceFingerprint, StartupFingerprint = null };
        var plan = oldPlan with { StartupFingerprint = fingerprint };
        var images = new Dictionary<string, GameMediaImage>
        {
            ["food:1"] = new(png, 1, 1), ["ingredient:2"] = new(png, 1, 1),
        };
        var cached = store.Commit(oldPlan, Guid.NewGuid().ToString("N"), images,
            new[] { new GameMediaMissing("cooker:1", "合成缺图") });
        var receiptPath = Path.Combine(storeRoot, cached.Generation, "startup.json");
        var pointerPath = Path.Combine(storeRoot, "current.json"); var pointer = File.ReadAllBytes(pointerPath);
        check(!File.Exists(receiptPath) && store.TryLoadStartup(fingerprint) == null,
            "大资源集合对应的旧图片代无凭据时不能提前复用");
        check(store.TryLoad(plan)?.Generation == cached.Generation && File.Exists(receiptPath)
            && File.ReadAllBytes(pointerPath).SequenceEqual(pointer),
            "大资源集合真实计划通过后原代补写启动凭据，不重提图片或替换指针");
        check(store.TryLoadStartup(fingerprint, out var reusedReason)?.Manifest.Generation == cached.Generation
            && reusedReason == "ready",
            "超过4096包的实际来源凭据可在下一次启动完整复用");
        using (var startup = new GameMediaService(store, Task.FromResult<string?>(fingerprint)))
        {
            Pump(startup, "ready");
            check(startup.Manifest().Generation == cached.Generation && startup.Image(cached.Generation, "food:1").SequenceEqual(png),
                "大资源集合缓存无需真实目录提取器即可就绪且返回原PNG");
        }

        var tail = Path.Combine(bundles, "chunk-04096.bundle");
        // 每种变化独立恢复，分别证明旧边界以后的文件名、长度和时间均参与指纹。
        // 长度变化时还原原时间，避免测试仅因 WriteAllBytes 顺带修改时间而通过。
        var originalTime = File.GetLastWriteTimeUtc(tail);
        File.WriteAllBytes(tail, new byte[] { 42, 43 }); File.SetLastWriteTimeUtc(tail, originalTime);
        AssertTailChange("长度");
        File.WriteAllBytes(tail, new byte[] { 42 }); File.SetLastWriteTimeUtc(tail, originalTime);
        check(GameMediaSourceIdentity.Read(catalog, "unity", "game", labels) == identity,
            "恢复尾端文件长度和时间后恢复同一指纹");
        File.SetLastWriteTimeUtc(tail, originalTime.AddSeconds(10)); AssertTailChange("最后写入时间");
        File.SetLastWriteTimeUtc(tail, originalTime);
        var renamed = Path.Combine(bundles, "renamed-tail.bundle"); File.Move(tail, renamed);
        AssertTailChange("文件名"); File.Move(renamed, tail);
        check(GameMediaSourceIdentity.Read(catalog, "unity", "game", labels) == identity
            && store.TryLoadStartup(fingerprint)?.Manifest.Generation == cached.Generation,
            "文件名恢复后仍复用原凭据，失败查询没有覆盖原代");

        var unknown = GameMediaSourceIdentity.Read(catalog, "unity", "game", null);
        check(unknown.StartupFingerprint == null && unknown.StartupFailureReason == "dlc-state-unavailable",
            "资源集合足够完整仍不能替代可靠DLC证据");
        using (var service = new GameMediaService(store, Task.FromResult(unknown.StartupFingerprint)))
        {
            // 已有缓存会先进入 verifying；必须驱动完成后台检查，不能只检查构造时的未就绪。
            Pump(service, "waiting");
            for (var index = 0; index < 30; index++) service.Tick();
            check(!service.IsReady && service.Status.Phase == "waiting",
                "大资源集合已有有效缓存但DLC未知时仍等待真实目录，不授予ready");
        }

        void AssertTailChange(string field)
        {
            var changed = GameMediaSourceIdentity.Read(catalog, "unity", "game", labels);
            check(changed.StartupFingerprint?.Length == 64 && changed.StartupFailureReason == null && changed.BundleCount == bundleCount
                && changed.SourceFingerprint == identity.SourceFingerprint
                && changed.StartupFingerprint != fingerprint,
                "旧4096边界之后的包" + field + "变化必须改变启动指纹，而非改变catalog来源摘要");
            var changedFingerprint = changed.StartupFingerprint ?? throw new InvalidOperationException("尾端资源变化后未生成启动指纹。");
            check(store.TryLoadStartup(changedFingerprint, out var reason) == null && reason == "startup-identity-changed"
                && store.TryLoad(plan with { StartupFingerprint = changedFingerprint }) == null,
                "尾端包" + field + "变化不得使用或重签旧启动凭据");
        }
    }

    private static void Pump(GameMediaService service, string phase)
    {
        var timer = Stopwatch.StartNew();
        while (service.Status.Phase != phase)
        {
            if (timer.ElapsedMilliseconds > 10000 || service.Status.Phase == "failed")
                throw new InvalidOperationException("启动缓存等待失败：" + service.Status.Phase + " → " + phase);
            service.Tick(); Thread.Sleep(1);
        }
    }
}
