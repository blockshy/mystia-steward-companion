using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx.Logging;
using HarmonyLib;
using MystiaStewardCompanion.Save;
using UnityEngine;

try
{
    if (args.Contains("--list-order-context", StringComparer.Ordinal))
    {
        ListOrderContextChecks.Run();
        return 0;
    }
    if (args.Contains("--beverage-targets", StringComparer.Ordinal))
    {
        BeverageMultiTargetChecks.Run();
        return 0;
    }
    if (args.Contains("--color-blocks-only", StringComparer.Ordinal))
    {
        // 本地只验证正式绑定/精确声明/视觉生命周期。没有安装 Harmony，不能宣称原生注入通过。
        ListColorBlockProbe.DirectCallbacks = true;
        typeof(RuntimePinnedListHighlightService).GetField("_log", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, new ManualLogSource());
        VerifyColorBlockLayoutAndNativeOwnership();
        VerifySharedListItemUsesSingleOwnershipAndBaseline();
        VerifyEightOrderSharedListOwnership();
        VerifyReusedColorSlotFollowsOrderLifecycle();
        VerifyManagedPinnedListHighlighting();
        VerifyExactRecipeVariantRowHighlighting();
        VerifyNativeMeshCallbackGuards();
        VerifyNativeMeshExceptionalWrites();
        Console.WriteLine($"PASS: direct list-color callbacks; {ListColorBlockProbe.Assertions} assertions; Harmony/native GPU not exercised.");
        return 0;
    }
    VerifyPatchTargets();
    VerifyColorBlockLayoutAndNativeOwnership();
    VerifyDualTargetClaimsAndColorRoundTrip();
    VerifyRuntimeUiTargetPublicationLease();
    VerifyTargetPublicationDiagnosticsDoNotBlockReaders();
    VerifyCookingRefreshHoldsExactTargetPublicationLease();
    VerifyStorageRefreshHoldsExactTargetPublicationLease();
    VerifySharedListItemUsesSingleOwnershipAndBaseline();
    VerifyEightOrderSharedListOwnership();
    VerifyReusedColorSlotFollowsOrderLifecycle();
    VerifyOrderHighlightRuntimeWiring();
    VerifyOpenPanelRefreshScheduling();
    VerifyAuthorityTransitionPreservesOpenPanels();
    VerifyOpenPanelSurfaceRefreshSemantics();
    VerifyCookingRefreshStagesFailClosed();
    VerifyIdenticalTargetPublicationIsIdempotent();
    VerifyOrderHighlightSurfaceIsolation();
    VerifyScopedNativePinnedMatching();
    VerifyTargetUpdatePreservesForceTotals();
    VerifyScopePinsOneTargetSnapshot();
    VerifyNestedScopeFinalizers();
    VerifyThreadLocalScopeIsolation();
    VerifyPinningAndHighlightRemainIndependent();
    VerifyDangerousListHooksAreAbsent();
    VerifyManagedHarmonyReturnPropagation();
    VerifyManagedPinnedListHighlighting();
    VerifyExactRecipeVariantRowHighlighting();
    VerifyEnabledEmptyTargetClearsVisuals();
    VerifyLifecycleGenerationGuards();
    VerifyFinishedOrderRevokesEverySurface();
    BeverageMultiTargetChecks.Run();
    Console.WriteLine("PASS: scoped pinning and pinned-list highlighting propagate through Harmony without mutating IL2CPP lists.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL: {ex}");
    return 1;
}

static void VerifyFinishedOrderRevokesEverySurface()
{
    // 保留同桌不同 lifecycle 的两类订单，证明撤销依据是完整身份而不是桌号。
    var business = RuntimeNightBusinessLifecycle.Generation;
    RuntimeUiTargetSnapshot Target(RuntimeUiTargetKind kind, long lifecycle)
        => new(kind, RuntimeTargetHighlightColor.DefaultRare,
            listPinningEnabled: true, recipeVariantEnabled: false,
            cookerHighlightEnabled: true, seatHighlightEnabled: true, orderHighlightEnabled: true,
            orderTraceId: kind == RuntimeUiTargetKind.Rare ? $"R-{lifecycle}" : $"N-{lifecycle}",
            orderKey: kind == RuntimeUiTargetKind.Rare ? "" : "ptr:abc",
            orderLifecycleSequence: lifecycle, deskCode: 2, recipeId: 10,
            ingredientIds: new[] { 1 }, extraIngredientIds: Array.Empty<int>(),
            beverageId: 2, cookerTypeId: 3, targetRevision: $"identity-{lifecycle}");
    var rare = Target(RuntimeUiTargetKind.Rare, 501);
    var normal = Target(RuntimeUiTargetKind.Normal, 502);
    var currentLifecycles = new HashSet<long> { 501, 502 };
    RuntimeUiTargetOrderResolver.Validate = target => currentLifecycles.Contains(target.OrderLifecycleSequence);
    try
    {
        Time.realtimeSinceStartup = 300f;
        RuntimeUiPinningService.UpdateTargets(business, new[] { rare, normal });
        var initial = RuntimeUiPinningService.ReadTargetSet();
        var before = RuntimeUiTargetOrderResolver.Calls;
        RuntimeUiPinningService.Tick();
        AssertEqual(before + 2, RuntimeUiTargetOrderResolver.Calls, "新发布须立即核验两个订单。");
        RuntimeUiPinningService.Tick();
        AssertEqual(before + 2, RuntimeUiTargetOrderResolver.Calls, "未变输入不应每帧重复枚举 capture。");
        Time.realtimeSinceStartup = 300.3f;
        RuntimeUiPinningService.Tick();
        AssertEqual(before + 4, RuntimeUiTargetOrderResolver.Calls, "250ms 健康兜底必须重新验证。");

        currentLifecycles.Remove(501);
        SpecialOrderRuntimeCapture.ChangeVersion++;
        RuntimeUiPinningService.Tick();
        var retained = RuntimeUiPinningService.ReadTargetSet();
        AssertTrue(retained.Generation > initial.Generation && retained.Targets.Count == 1,
            "结束的稀客订单须立即撤回，不能等待下一轮耗时方案计算。");
        AssertEqual(RuntimeUiTargetKind.Normal, retained.Targets[0].Kind, "同桌合法普客目标不能被一并清掉。");
        AssertEqual(normal.OrderTraceId, RuntimeOrderHighlightService.LastOrderTraceId, "HUD 未同步保留普客。");
        AssertEqual(normal.OrderTraceId, RuntimeThrowDeliverOrderHighlightService.LastOrderTraceId, "抛送未同步保留普客。");
        AssertTrue(RuntimeSeatHighlightService.LastEnabled && RuntimeCookerHighlightService.LastEnabled,
            "合法另一订单仍需要座位与厨具高亮。");

        currentLifecycles.Clear();
        currentLifecycles.Add(503); // 同桌新订单绝不能继承 502 的旧目标。
        NormalOrderRuntimeCapture.ChangeVersion++;
        RuntimeUiPinningService.Tick();
        AssertEqual(0, RuntimeUiPinningService.ReadTargetSet().Targets.Count, "同桌新 lifecycle 被错误当成旧订单。");
        AssertFalse(RuntimeCookerHighlightService.LastEnabled, "订单结束后厨具仍在高亮。");
        AssertFalse(RuntimeSeatHighlightService.LastEnabled, "订单结束后座位仍在高亮。");
        AssertFalse(RuntimeOrderHighlightService.LastEnabled, "订单结束后 HUD 未撤回。");
        AssertFalse(RuntimeThrowDeliverOrderHighlightService.LastEnabled, "订单结束后抛送未撤回。");

        // 后台旧计算再次发布相同历史对象也须在下一次消费时被拒绝，不能由旧响应恢复。
        RuntimeUiPinningService.UpdateTargets(business, new[] { normal });
        RuntimeUiPinningService.Tick();
        AssertEqual(0, RuntimeUiPinningService.ReadTargetSet().Targets.Count, "旧响应恢复了已失效目标。");
        RuntimeUiPinningService.UpdateTargets(business, new[] { Target(RuntimeUiTargetKind.Normal, 503) });
        RuntimeUiPinningService.Tick();
        AssertEqual(503L, RuntimeUiPinningService.ReadTargetSet().Targets.Single().OrderLifecycleSequence,
            "下一订单具备新完整身份后应正常恢复高亮。");
    }
    finally
    {
        RuntimeUiTargetOrderResolver.Validate = null;
        RuntimeUiPinningService.UpdateTargets(business, Array.Empty<RuntimeUiTargetSnapshot>());
    }
}

static void VerifyDualTargetClaimsAndColorRoundTrip()
{
    AssertTrue(RuntimeTargetHighlightColor.TryParseExactHex("A1B2C3", out var rareColor), "Strict uppercase rare color was rejected.");
    AssertTrue(RuntimeTargetHighlightColor.TryParseExactHex("4D5E6F", out var normalColor), "Strict uppercase normal color was rejected.");
    AssertFalse(RuntimeTargetHighlightColor.TryParseExactHex("a1b2c3", out _), "Lowercase color bypassed the exact wire contract.");
    AssertFalse(RuntimeTargetHighlightColor.TryParseExactHex("#A1B2C3", out _), "Hash-prefixed color bypassed the exact wire contract.");

    RuntimeUiTargetSnapshot Create(
        RuntimeUiTargetKind kind,
        RuntimeTargetHighlightColor color,
        string trace,
        string orderKey,
        int beverageId,
        bool listPinningEnabled = true,
        bool recipeVariantEnabled = true,
        bool cookerHighlightEnabled = true,
        bool seatHighlightEnabled = true,
        bool orderHighlightEnabled = true)
    {
        return new RuntimeUiTargetSnapshot(
            kind,
            color,
            listPinningEnabled,
            recipeVariantEnabled,
            cookerHighlightEnabled,
            seatHighlightEnabled,
            orderHighlightEnabled,
            trace,
            orderKey,
            orderLifecycleSequence: kind == RuntimeUiTargetKind.Rare ? 10 : 11,
            deskCode: kind == RuntimeUiTargetKind.Rare ? 0 : 1,
            recipeId: 90,
            ingredientIds: new[] { 91 },
            extraIngredientIds: new[] { 92 },
            beverageId,
            cookerTypeId: 3,
            targetRevision: $"{kind}-target");
    }

    var targetSet = new RuntimeUiTargetSetSnapshot(
        generation: 1,
        sessionGeneration: RuntimeNightBusinessLifecycle.Generation,
        new[]
        {
            Create(RuntimeUiTargetKind.Rare, rareColor, "R-10", "", beverageId: 93),
            Create(RuntimeUiTargetKind.Normal, normalColor, "N-11", "ptr:b", beverageId: 94),
        });

    AssertEqual(RuntimeUiTargetKinds.Rare | RuntimeUiTargetKinds.Normal, targetSet.GetRecipeClaims(90), "Shared recipe did not retain both claims.");
    AssertEqual(RuntimeUiTargetKinds.None, targetSet.GetBaseRecipeClaims(90), "Variant-enabled targets leaked onto the authoritative base row.");
    AssertEqual(RuntimeUiTargetKinds.Rare | RuntimeUiTargetKinds.Normal, targetSet.GetRecipeVariantClaims(90), "Shared recipe variants did not retain both claims.");
    AssertTrue(targetSet.HasRecipeVariants(90), "Shared variant recipe was not classified as row-controlled.");
    AssertEqual(RuntimeUiTargetKinds.Rare | RuntimeUiTargetKinds.Normal, targetSet.GetIngredientClaims(91), "Shared ingredient did not retain both claims.");
    AssertEqual(RuntimeUiTargetKinds.Rare | RuntimeUiTargetKinds.Normal, targetSet.GetCookerClaims(3), "Shared cooker did not retain both claims.");
    AssertEqual(RuntimeUiTargetKinds.Rare, targetSet.GetBeverageClaims(93), "Rare-only beverage claim changed.");
    AssertEqual(RuntimeUiTargetKinds.Normal, targetSet.GetBeverageClaims(94), "Normal-only beverage claim changed.");

    var splitFeatureSet = new RuntimeUiTargetSetSnapshot(
        generation: 2,
        sessionGeneration: RuntimeNightBusinessLifecycle.Generation,
        new[]
        {
            Create(
                RuntimeUiTargetKind.Rare,
                rareColor,
                "R-12",
                "",
                beverageId: 93,
                listPinningEnabled: true,
                recipeVariantEnabled: false,
                cookerHighlightEnabled: false,
                seatHighlightEnabled: true,
                orderHighlightEnabled: false),
            Create(
                RuntimeUiTargetKind.Normal,
                normalColor,
                "N-13",
                "ptr:d",
                beverageId: 94,
                listPinningEnabled: false,
                recipeVariantEnabled: false,
                cookerHighlightEnabled: true,
                seatHighlightEnabled: false,
                orderHighlightEnabled: true),
        });
    AssertEqual(RuntimeUiTargetKinds.Rare, splitFeatureSet.GetRecipeClaims(90), "Normal list pinning leaked through a disabled normal target feature.");
    AssertEqual(RuntimeUiTargetKinds.Rare, splitFeatureSet.GetBaseRecipeClaims(90), "A non-variant recipe target did not remain on the base row.");
    AssertFalse(splitFeatureSet.HasRecipeVariants(90), "A disabled variant target made the recipe row-controlled.");
    AssertEqual(RuntimeUiTargetKinds.Normal, splitFeatureSet.GetCookerClaims(3), "Rare cooker highlighting leaked through a disabled rare target feature.");
    AssertTrue(splitFeatureSet.TryGetTarget(splitFeatureSet.Targets.Single(target => target.Kind == RuntimeUiTargetKind.Rare).IdentityKey, out var splitRare), "Split feature set lost its rare target.");
    AssertTrue(splitFeatureSet.TryGetTarget(splitFeatureSet.Targets.Single(target => target.Kind == RuntimeUiTargetKind.Normal).IdentityKey, out var splitNormal), "Split feature set lost its normal target.");
    AssertTrue(splitRare.SeatHighlightEnabled && !splitNormal.SeatHighlightEnabled, "Seat highlighting was not retained per target kind.");
    AssertTrue(!splitRare.OrderHighlightEnabled && splitNormal.OrderHighlightEnabled, "Order highlighting was not retained per target kind.");

    AssertThrows<ArgumentException>(
        () => Create(
            RuntimeUiTargetKind.Rare,
            rareColor,
            "R-14",
            "",
            beverageId: 93,
            listPinningEnabled: false,
            recipeVariantEnabled: true,
            cookerHighlightEnabled: false,
            seatHighlightEnabled: false,
            orderHighlightEnabled: false),
        "Recipe variants were accepted without the same target's list-pinning feature.");

    var palette = targetSet.Palette;
    var baseline = new Color(0.2f, 0.3f, 0.4f, 0.7f);
    var normalEndpointTime = 1.6f;
    var rareEndpointTime = 3.1f;
    AssertColor(
        RuntimeTargetHighlightStyle.BuildListItemPulseColor(
            baseline,
            RuntimeUiTargetKinds.Normal,
            palette,
            normalEndpointTime),
        RuntimeTargetHighlightStyle.BuildListItemPulseColor(
            baseline,
            RuntimeUiTargetKinds.Rare | RuntimeUiTargetKinds.Normal,
            palette,
            normalEndpointTime),
        "Shared highlight did not reach the normal color endpoint.");
    AssertColor(
        RuntimeTargetHighlightStyle.BuildListItemPulseColor(
            baseline,
            RuntimeUiTargetKinds.Rare,
            palette,
            rareEndpointTime),
        RuntimeTargetHighlightStyle.BuildListItemPulseColor(
            baseline,
            RuntimeUiTargetKinds.Rare | RuntimeUiTargetKinds.Normal,
            palette,
            rareEndpointTime),
        "Shared highlight did not return to the rare color endpoint.");
}

static void VerifyRuntimeUiTargetPublicationLease()
{
    var businessGeneration = RuntimeNightBusinessLifecycle.Generation;
    PublishRareTarget(
        businessGeneration,
        listPinningEnabled: true,
        recipeVariantEnabled: true,
        cookerHighlightEnabled: false,
        seatHighlightEnabled: false,
        orderHighlightEnabled: false,
        recipeId: 120,
        beverageId: -1,
        ingredientIds: new[] { 121 },
        extraIngredientIds: new[] { 122 },
        cookerTypeId: -1,
        deskCode: -1,
        orderTraceId: "",
        targetRevision: "publication-lease");
    var expected = RuntimeUiPinningService.ReadTargetSet();
    AssertTrue(
        RuntimeUiPinningService.TryAcquireTargetPublicationLease(expected, out var lease),
        "The current active target snapshot could not acquire its publication lease.");

    using var workerStarted = new ManualResetEventSlim();
    using var workerFinished = new ManualResetEventSlim();
    Exception? workerFailure = null;
    var worker = new Thread(() =>
    {
        try
        {
            workerStarted.Set();
            PublishRareTarget(
                businessGeneration,
                listPinningEnabled: true,
                recipeVariantEnabled: false,
                cookerHighlightEnabled: false,
                seatHighlightEnabled: false,
                orderHighlightEnabled: false,
                recipeId: 123,
                beverageId: -1,
                ingredientIds: new[] { 124 },
                extraIngredientIds: Array.Empty<int>(),
                cookerTypeId: -1,
                deskCode: -1,
                orderTraceId: "",
                targetRevision: "publication-after-lease");
        }
        catch (Exception ex)
        {
            workerFailure = ex;
        }
        finally
        {
            workerFinished.Set();
        }
    });
    worker.Start();
    AssertTrue(workerStarted.Wait(TimeSpan.FromSeconds(2)), "The publication worker did not start.");
    AssertFalse(workerFinished.Wait(TimeSpan.FromMilliseconds(100)), "Target publication escaped an active UI target lease.");
    lease.Dispose();
    AssertTrue(workerFinished.Wait(TimeSpan.FromSeconds(5)), "Target publication did not resume after the lease was released.");
    worker.Join();
    if (workerFailure != null) throw new InvalidOperationException("Target publication worker failed.", workerFailure);
    AssertFalse(
        RuntimeUiPinningService.TryAcquireTargetPublicationLease(expected, out _),
        "A stale target snapshot acquired a publication lease.");

    var current = RuntimeUiPinningService.ReadTargetSet();
    AssertTrue(
        RuntimeUiPinningService.TryAcquireTargetPublicationLease(current, out var threadAffineLease),
        "The replacement target snapshot could not acquire a publication lease.");
    Exception? wrongThreadRelease = null;
    var wrongThread = new Thread(() =>
    {
        try
        {
            threadAffineLease.Dispose();
        }
        catch (Exception ex)
        {
            wrongThreadRelease = ex;
        }
    });
    wrongThread.Start();
    wrongThread.Join();
    AssertTrue(wrongThreadRelease is InvalidOperationException, "A publication lease was released by a different thread.");
    threadAffineLease.Dispose();

    AssertTrue(
        RuntimeUiPinningService.TryAcquireTargetPublicationLease(
            businessGeneration,
            out var businessLease),
        "An active business generation could not acquire its snapshot-independent publication lease.");
    using var businessPublicationStarted = new ManualResetEventSlim();
    using var businessPublicationFinished = new ManualResetEventSlim();
    Exception? businessPublicationFailure = null;
    var businessPublicationWorker = new Thread(() =>
    {
        try
        {
            businessPublicationStarted.Set();
            PublishRareTarget(
                businessGeneration,
                listPinningEnabled: true,
                recipeVariantEnabled: false,
                cookerHighlightEnabled: false,
                seatHighlightEnabled: false,
                orderHighlightEnabled: false,
                recipeId: 125,
                beverageId: -1,
                ingredientIds: new[] { 126 },
                extraIngredientIds: Array.Empty<int>(),
                cookerTypeId: -1,
                deskCode: -1,
                orderTraceId: "",
                targetRevision: "business-publication-after-lease");
        }
        catch (Exception ex)
        {
            businessPublicationFailure = ex;
        }
        finally
        {
            businessPublicationFinished.Set();
        }
    });
    businessPublicationWorker.Start();
    AssertTrue(businessPublicationStarted.Wait(TimeSpan.FromSeconds(2)), "The business-lease publication worker did not start.");
    AssertFalse(
        businessPublicationFinished.Wait(TimeSpan.FromMilliseconds(100)),
        "Target publication escaped an active snapshot-independent business lease.");
    businessLease.Dispose();
    AssertTrue(
        businessPublicationFinished.Wait(TimeSpan.FromSeconds(5)),
        "Target publication did not resume after the snapshot-independent business lease was released.");
    businessPublicationWorker.Join();
    if (businessPublicationFailure != null)
    {
        throw new InvalidOperationException("Business-lease publication worker failed.", businessPublicationFailure);
    }

    AssertTrue(
        RuntimeUiPinningService.TryAcquireTargetPublicationLease(
            businessGeneration,
            out var rotatedTargetBusinessLease),
        "A target rotation incorrectly invalidated the same business generation's publication lease.");
    rotatedTargetBusinessLease.Dispose();

    RuntimeNightBusinessLifecycle.ActivateNextGeneration();
    AssertFalse(
        RuntimeUiPinningService.TryAcquireTargetPublicationLease(
            businessGeneration,
            out _),
        "A prior business generation acquired a publication lease after the lifecycle advanced.");
}

/// <summary>
/// 用可阻塞的日志接收器复现后台发布后同步写盘：日志尚未返回时，另一线程仍必须可以
/// 推进真实 Tick、读取完整代次并取得精确目标许可。旧实现会在同一发布锁上等到日志解除。
/// 测试始终在 finally 放行并回收工作线程，不通过缩短安全门禁或忽略异常使断言通过。
/// </summary>
static void VerifyTargetPublicationDiagnosticsDoNotBlockReaders()
{
    void Publish(string revision) => PublishRareTarget(
        RuntimeNightBusinessLifecycle.Generation,
        listPinningEnabled: true, recipeVariantEnabled: false, cookerHighlightEnabled: true,
        seatHighlightEnabled: true, orderHighlightEnabled: true, recipeId: 201, beverageId: 202,
        ingredientIds: new[] { 203 }, extraIngredientIds: Array.Empty<int>(), cookerTypeId: 1,
        deskCode: 2, orderTraceId: "R-9401", targetRevision: revision);

    foreach (var scenario in new[] { "publish", "authority", "boundary", "warning", "status" })
    {
        RuntimeUiPinningService.Abandon("isolated diagnostic concurrency probe");
        Publish("diagnostic-before-" + scenario);
        var previous = RuntimeUiPinningService.ReadTargetSet();
        var session = previous.SessionGeneration;
        using var logEntered = new ManualResetEventSlim();
        using var releaseLog = new ManualResetEventSlim();
        using var readerFinished = new ManualResetEventSlim();
        Exception? publisherFailure = null;
        Exception? readerFailure = null;
        var publisherId = 0;
        var readerCompletedBeforeRelease = false;
        var entered = false;
        Thread? reader = null;
        void BlockPublisherLog(string message)
        {
            if (Environment.CurrentManagedThreadId != Volatile.Read(ref publisherId)) return;
            logEntered.Set();
            if (!releaseLog.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("诊断测试未在截止前释放日志接收器。");
        }
        ManualLogSource.BeforeInformationWrite = BlockPublisherLog;
        ManualLogSource.BeforeWarningWrite = BlockPublisherLog;
        if (scenario == "status") RuntimeCookerHighlightService.BeforeStatusRead = () => BlockPublisherLog("status");
        var publisher = new Thread(() =>
        {
            Volatile.Write(ref publisherId, Environment.CurrentManagedThreadId);
            try
            {
                if (scenario == "authority")
                    RuntimeUiPinningService.ClearTargetsForAuthorityTransition(session, "diagnostic authority probe");
                else if (scenario == "boundary")
                    RuntimeUiPinningService.ClearTargetsForBusinessBoundary(session, "diagnostic boundary probe");
                else
                {
                    RuntimeOrderHighlightService.ThrowOnUpdate = scenario == "warning";
                    // 同值调用不发布新代，但依旧返回详细 Status；模拟视觉状态读取等待，
                    // 验证原来的同值早退也不再持目标锁跨入视觉服务锁。
                    Publish((scenario == "status" ? "diagnostic-before-" : "diagnostic-after-") + scenario);
                }
            }
            catch (Exception ex) { publisherFailure = ex; }
        });
        try
        {
            publisher.Start();
            entered = logEntered.Wait(TimeSpan.FromSeconds(2));
            if (entered)
            {
                reader = new Thread(() =>
                {
                    try
                    {
                        // 这是生产 Tick 和生产许可实现，不是反射探测某个锁字段是否释放。
                        RuntimeUiPinningService.Tick();
                        var current = RuntimeUiPinningService.ReadTargetSet();
                        AssertTrue((scenario == "status" ? current.Generation == previous.Generation : current.Generation > previous.Generation)
                            && current.SessionGeneration == session,
                            scenario + ": 日志阻塞期间没有完整发布新目标代次。");
                        AssertEqual(scenario is "authority" or "boundary" ? 0 : 1, current.Targets.Count,
                            scenario + ": 日志阻塞期间看到了部分目标集合。");
                        if (scenario != "status")
                            AssertFalse(RuntimeUiPinningService.TryAcquireTargetPublicationLease(previous, out _),
                                scenario + ": 旧目标在日志阻塞期间仍获得精确许可。");
                        AssertTrue(RuntimeUiPinningService.TryAcquireTargetPublicationLease(current, out var lease),
                            scenario + ": 新目标在日志阻塞期间不能取得精确许可。");
                        lease.Dispose();
                        if (scenario == "authority")
                        {
                            AssertTrue(ReferenceEquals(previous, RuntimeUiPinningService.ReadSurfaceRefreshTargetSet()),
                                "切主屏障仍须保留原呈现目标，不能为解锁日志恢复旧执行目标。");
                        }
                    }
                    catch (Exception ex) { readerFailure = ex; }
                    finally { readerFinished.Set(); }
                });
                reader.Start();
                readerCompletedBeforeRelease = readerFinished.Wait(TimeSpan.FromSeconds(2));
            }
        }
        finally
        {
            releaseLog.Set();
            publisher.Join(TimeSpan.FromSeconds(5));
            reader?.Join(TimeSpan.FromSeconds(5));
            ManualLogSource.BeforeInformationWrite = null;
            ManualLogSource.BeforeWarningWrite = null;
            RuntimeCookerHighlightService.BeforeStatusRead = null;
            RuntimeOrderHighlightService.ThrowOnUpdate = false;
        }
        AssertTrue(entered, scenario + ": 发布未到达受控日志接收器。");
        AssertTrue(readerCompletedBeforeRelease, scenario + ": 同步日志仍持有目标发布锁，阻止 Tick/许可前进。");
        AssertFalse(publisher.IsAlive || reader?.IsAlive == true, scenario + ": 并发探针线程没有结束。");
        if (publisherFailure != null) throw new InvalidOperationException(scenario + ": 发布线程失败。", publisherFailure);
        if (readerFailure != null) throw new InvalidOperationException(scenario + ": 读取线程失败。", readerFailure);
    }
}

static void VerifyCookingRefreshHoldsExactTargetPublicationLease()
{
    CookingSelectionPanelProbe.ResetRefreshProbe();
    RunTimePlayerDataProbe.Reset(nativeResult: false);
    var businessGeneration = RuntimeNightBusinessLifecycle.Generation;
    var panel = new CookingSelectionPanelProbe();
    try
    {
        PublishRareTarget(
            businessGeneration,
            listPinningEnabled: true,
            recipeVariantEnabled: false,
            cookerHighlightEnabled: false,
            seatHighlightEnabled: false,
            orderHighlightEnabled: false,
            recipeId: 130,
            beverageId: -1,
            ingredientIds: new[] { 131 },
            extraIngredientIds: Array.Empty<int>(),
            cookerTypeId: -1,
            deskCode: -1,
            orderTraceId: "",
            targetRevision: "cooking-refresh-initial");
        panel.OnPanelOpen();

        PublishRareTarget(
            businessGeneration,
            listPinningEnabled: true,
            recipeVariantEnabled: false,
            cookerHighlightEnabled: false,
            seatHighlightEnabled: false,
            orderHighlightEnabled: false,
            recipeId: 132,
            beverageId: -1,
            ingredientIds: new[] { 133 },
            extraIngredientIds: Array.Empty<int>(),
            cookerTypeId: -1,
            deskCode: -1,
            orderTraceId: "",
            targetRevision: "cooking-refresh-leased");
        var exactRefreshTarget = RuntimeUiPinningService.ReadTargetSet();
        var sequenceStart = CookingSelectionPanelProbe.RefreshSequence.Count;

        using var publicationStarted = new ManualResetEventSlim();
        using var publicationFinished = new ManualResetEventSlim();
        Exception? publicationFailure = null;
        Thread? publicationWorker = null;
        var ingredientRefreshActionInvoked = false;
        var recipeRefreshActionInvoked = false;
        var ingredientSurfaceRefreshActionInvoked = false;
        var recipeSurfaceRefreshActionInvoked = false;
        var exactTargetObservedByAllStages = true;
        var directIngredientTargetsPinned = false;
        var directRecipeTargetPinned = false;
        var visibleIngredientTargetsPinned = false;
        var visibleRecipeTargetPinned = false;
        var publicationWorkerStarted = false;
        var publicationEscapedDuringIngredientRefresh = false;
        var publicationEscapedDuringRecipeRefresh = false;
        var publicationEscapedDuringIngredientSurfaceRefresh = false;
        var publicationEscapedDuringRecipeSurfaceRefresh = false;
        CookingSelectionPanelProbe.IngredientRefreshAction = () =>
        {
            ingredientRefreshActionInvoked = true;
            exactTargetObservedByAllStages &= ReferenceEquals(
                exactRefreshTarget,
                RuntimeUiPinningService.ReadTargetSet());
            directIngredientTargetsPinned = new[]
            {
                PlayerSaveFileDefaultPropProbe.IngredientsSeafood,
                PlayerSaveFileDefaultPropProbe.IngredientsMeat,
                PlayerSaveFileDefaultPropProbe.IngredientsVegetable,
                PlayerSaveFileDefaultPropProbe.IngredientsOther,
            }.All(ingredientType => RunTimePlayerDataProbe.CheckPinned(ingredientType, 133));
            publicationWorker = new Thread(() =>
            {
                try
                {
                    publicationStarted.Set();
                    PublishRareTarget(
                        businessGeneration,
                        listPinningEnabled: true,
                        recipeVariantEnabled: false,
                        cookerHighlightEnabled: false,
                        seatHighlightEnabled: false,
                        orderHighlightEnabled: false,
                        recipeId: 134,
                        beverageId: -1,
                        ingredientIds: new[] { 135 },
                        extraIngredientIds: Array.Empty<int>(),
                        cookerTypeId: -1,
                        deskCode: -1,
                        orderTraceId: "",
                        targetRevision: "cooking-refresh-post-publication");
                }
                catch (Exception ex)
                {
                    publicationFailure = ex;
                }
                finally
                {
                    publicationFinished.Set();
                }
            });
            publicationWorker.Start();
            publicationWorkerStarted = publicationStarted.Wait(TimeSpan.FromSeconds(2));
            if (publicationWorkerStarted)
            {
                publicationEscapedDuringIngredientRefresh = publicationFinished.Wait(
                    TimeSpan.FromMilliseconds(100));
            }
            return true;
        };
        CookingSelectionPanelProbe.RecipeRefreshAction = () =>
        {
            recipeRefreshActionInvoked = true;
            exactTargetObservedByAllStages &= ReferenceEquals(
                exactRefreshTarget,
                RuntimeUiPinningService.ReadTargetSet());
            directRecipeTargetPinned = RunTimePlayerDataProbe.CheckPinned(
                PlayerSaveFileDefaultPropProbe.Recipes,
                132);
            publicationEscapedDuringRecipeRefresh = publicationFinished.IsSet;
            return true;
        };
        CookingSelectionPanelProbe.IngredientSurfaceRefreshAction = () =>
        {
            ingredientSurfaceRefreshActionInvoked = true;
            exactTargetObservedByAllStages &= ReferenceEquals(
                exactRefreshTarget,
                RuntimeUiPinningService.ReadTargetSet());
            visibleIngredientTargetsPinned = new[]
            {
                PlayerSaveFileDefaultPropProbe.IngredientsSeafood,
                PlayerSaveFileDefaultPropProbe.IngredientsMeat,
                PlayerSaveFileDefaultPropProbe.IngredientsVegetable,
                PlayerSaveFileDefaultPropProbe.IngredientsOther,
            }.All(ingredientType => RunTimePlayerDataProbe.CheckPinned(ingredientType, 133));
            publicationEscapedDuringIngredientSurfaceRefresh = publicationFinished.IsSet;
        };
        CookingSelectionPanelProbe.RecipeSurfaceRefreshAction = () =>
        {
            recipeSurfaceRefreshActionInvoked = true;
            exactTargetObservedByAllStages &= ReferenceEquals(
                exactRefreshTarget,
                RuntimeUiPinningService.ReadTargetSet());
            visibleRecipeTargetPinned = RunTimePlayerDataProbe.CheckPinned(
                PlayerSaveFileDefaultPropProbe.Recipes,
                132);
            publicationEscapedDuringRecipeSurfaceRefresh = publicationFinished.IsSet;
        };

        RuntimeUiPinningService.Tick();
        AssertTrue(ingredientRefreshActionInvoked,
            "The cooking target refresh did not invoke UpdateIngField.");
        AssertTrue(recipeRefreshActionInvoked,
            "The cooking target refresh did not invoke UpdateRecipeField.");
        AssertTrue(ingredientSurfaceRefreshActionInvoked,
            "The cooking target refresh did not rebuild m_StaticIngredientsGroup after both backing-data stages.");
        AssertTrue(recipeSurfaceRefreshActionInvoked,
            "The cooking target refresh did not rebuild m_StaticRecipeGroup after UpdateRecipeField.");
        AssertTrue(exactTargetObservedByAllStages,
            "A cooking refresh stage observed a target snapshot other than its exact publication lease.");
        AssertTrue(directIngredientTargetsPinned,
            "Direct UpdateIngField did not use the cooking CheckPinned scope for all four ingredient categories.");
        AssertTrue(directRecipeTargetPinned,
            "Direct UpdateRecipeField did not use the cooking CheckPinned scope for its new recipe target.");
        AssertTrue(visibleIngredientTargetsPinned,
            "m_StaticIngredientsGroup.UpdateElements ran outside the exact cooking CheckPinned scope for an ingredient category.");
        AssertTrue(visibleRecipeTargetPinned,
            "m_StaticRecipeGroup.UpdateElements ran outside the exact cooking CheckPinned scope.");
        AssertTrue(publicationWorkerStarted, "The cooking-refresh publication worker did not start.");
        AssertFalse(
            publicationEscapedDuringIngredientRefresh,
            "Cooking refresh allowed the exact target snapshot to rotate before UpdateIngField returned.");
        AssertFalse(
            publicationEscapedDuringRecipeRefresh,
            "Cooking refresh released the exact target publication lease before UpdateRecipeField returned.");
        AssertFalse(
            publicationEscapedDuringIngredientSurfaceRefresh,
            "Cooking refresh released the exact target publication lease before m_StaticIngredientsGroup.UpdateElements returned.");
        AssertFalse(
            publicationEscapedDuringRecipeSurfaceRefresh,
            "Cooking refresh released the exact target publication lease before m_StaticRecipeGroup.UpdateElements returned.");
        AssertSequenceEqual(
            new[] { "ingredient-data", "recipe-data", "ingredient-visible", "recipe-visible" },
            CookingSelectionPanelProbe.RefreshSequence.Skip(sequenceStart),
            "The direct cooking refresh did not preserve the exact native four-stage order.");
        AssertEqual(1, CookingSelectionPanelProbe.FullVisualRefreshCount,
            "A target publication invoked full UpdateAllVisual instead of the bounded four-stage refresh.");
        AssertEqual(2, CookingSelectionPanelProbe.IngredientRefreshCount,
            "The open panel did not run exactly one natural and one direct ingredient-data refresh.");
        AssertEqual(2, CookingSelectionPanelProbe.RecipeRefreshCount,
            "The open panel did not run exactly one natural and one direct recipe-field refresh.");
        AssertEqual(2, CookingSelectionPanelProbe.IngredientSurfaceRefreshCount,
            "The open panel did not run exactly one natural and one direct ingredient-surface refresh.");
        AssertEqual(2, CookingSelectionPanelProbe.RecipeSurfaceRefreshCount,
            "The open panel did not run exactly one natural and one direct recipe-surface refresh.");
        AssertEqual(1, CookingSelectionPanelProbe.SelectedSurfaceRefreshCount,
            "The bounded target refresh rebuilt the selected-ingredient surface.");
        AssertEqual(1, CookingSelectionPanelProbe.OutputSurfaceRefreshCount,
            "The bounded target refresh rebuilt the output surface.");
        AssertTrue(
            publicationFinished.Wait(TimeSpan.FromSeconds(5)),
            "Target publication did not resume after the cooking refresh completed.");
        publicationWorker?.Join();
        if (publicationFailure != null)
        {
            throw new InvalidOperationException("Cooking-refresh publication worker failed.", publicationFailure);
        }
    }
    finally
    {
        CookingSelectionPanelProbe.IngredientRefreshAction = null;
        CookingSelectionPanelProbe.RecipeRefreshAction = null;
        CookingSelectionPanelProbe.IngredientSurfaceRefreshAction = null;
        CookingSelectionPanelProbe.RecipeSurfaceRefreshAction = null;
        panel.OnPanelClose();
    }
}

static void VerifyStorageRefreshHoldsExactTargetPublicationLease()
{
    StoragePanelProbe.ResetRefreshProbe();
    var businessGeneration = RuntimeNightBusinessLifecycle.Generation;
    var panel = new StoragePanelProbe();
    try
    {
        PublishRareTarget(
            businessGeneration,
            listPinningEnabled: true,
            recipeVariantEnabled: false,
            cookerHighlightEnabled: false,
            seatHighlightEnabled: false,
            orderHighlightEnabled: false,
            recipeId: -1,
            beverageId: 230,
            ingredientIds: Array.Empty<int>(),
            extraIngredientIds: Array.Empty<int>(),
            cookerTypeId: -1,
            deskCode: -1,
            orderTraceId: "",
            targetRevision: "storage-refresh-initial");
        panel.OnPanelOpen();

        PublishRareTarget(
            businessGeneration,
            listPinningEnabled: true,
            recipeVariantEnabled: false,
            cookerHighlightEnabled: false,
            seatHighlightEnabled: false,
            orderHighlightEnabled: false,
            recipeId: -1,
            beverageId: 231,
            ingredientIds: Array.Empty<int>(),
            extraIngredientIds: Array.Empty<int>(),
            cookerTypeId: -1,
            deskCode: -1,
            orderTraceId: "",
            targetRevision: "storage-refresh-leased");
        var exactRefreshTarget = RuntimeUiPinningService.ReadTargetSet();

        using var publicationStarted = new ManualResetEventSlim();
        using var publicationFinished = new ManualResetEventSlim();
        Exception? publicationFailure = null;
        Thread? publicationWorker = null;
        var publicationEscapedDuringDataRefresh = false;
        var publicationEscapedDuringSurfaceRefresh = false;
        var surfaceRefreshActionInvoked = false;
        var exactTargetObservedByBothStages = true;
        var dataTargetPinned = false;
        var visibleTargetPinned = false;
        StoragePanelProbe.RefreshAction = () =>
        {
            exactTargetObservedByBothStages &= ReferenceEquals(
                exactRefreshTarget,
                RuntimeUiPinningService.ReadTargetSet());
            dataTargetPinned = RunTimePlayerDataProbe.CheckPinned(
                PlayerSaveFileDefaultPropProbe.Beverages,
                231);
            publicationWorker = new Thread(() =>
            {
                try
                {
                    publicationStarted.Set();
                    PublishRareTarget(
                        businessGeneration,
                        listPinningEnabled: true,
                        recipeVariantEnabled: false,
                        cookerHighlightEnabled: false,
                        seatHighlightEnabled: false,
                        orderHighlightEnabled: false,
                        recipeId: -1,
                        beverageId: 232,
                        ingredientIds: Array.Empty<int>(),
                        extraIngredientIds: Array.Empty<int>(),
                        cookerTypeId: -1,
                        deskCode: -1,
                        orderTraceId: "",
                        targetRevision: "storage-refresh-post-publication");
                }
                catch (Exception ex)
                {
                    publicationFailure = ex;
                }
                finally
                {
                    publicationFinished.Set();
                }
            });
            publicationWorker.Start();
            AssertTrue(publicationStarted.Wait(TimeSpan.FromSeconds(2)),
                "The storage-refresh publication worker did not start.");
            publicationEscapedDuringDataRefresh = publicationFinished.Wait(TimeSpan.FromMilliseconds(100));
            return true;
        };
        StoragePanelProbe.SurfaceRefreshAction = () =>
        {
            surfaceRefreshActionInvoked = true;
            exactTargetObservedByBothStages &= ReferenceEquals(
                exactRefreshTarget,
                RuntimeUiPinningService.ReadTargetSet());
            visibleTargetPinned = RunTimePlayerDataProbe.CheckPinned(
                PlayerSaveFileDefaultPropProbe.Beverages,
                231);
            publicationEscapedDuringSurfaceRefresh = publicationFinished.IsSet;
        };

        RuntimeUiPinningService.Tick();
        AssertFalse(publicationEscapedDuringDataRefresh,
            "Storage refresh allowed target publication while UpdateBevField was active.");
        AssertTrue(surfaceRefreshActionInvoked,
            "Storage refresh did not rebuild m_BevsGroup after UpdateBevField.");
        AssertTrue(exactTargetObservedByBothStages,
            "A storage refresh stage observed a target other than its exact publication lease.");
        AssertTrue(dataTargetPinned,
            "Direct UpdateBevField did not use the exact beverage CheckPinned scope.");
        AssertTrue(visibleTargetPinned,
            "m_BevsGroup.UpdateElements ran outside the exact beverage CheckPinned scope.");
        AssertFalse(publicationEscapedDuringSurfaceRefresh,
            "Storage refresh released the target publication lease before m_BevsGroup.UpdateElements returned.");
        AssertTrue(publicationFinished.Wait(TimeSpan.FromSeconds(5)),
            "Target publication did not resume after the storage surface refresh completed.");
        publicationWorker?.Join();
        if (publicationFailure != null)
        {
            throw new InvalidOperationException("Storage-refresh publication worker failed.", publicationFailure);
        }
    }
    finally
    {
        StoragePanelProbe.RefreshAction = null;
        StoragePanelProbe.SurfaceRefreshAction = null;
        panel.OnPanelClose();
    }
}

static void VerifyEnabledEmptyTargetClearsVisuals()
{
    const int recipeId = 81;
    const int beverageId = 82;
    const int cookerTypeId = 3;
    var baseColor = new Color(0.62f, 0.66f, 0.72f, 0.55f);
    var businessGeneration = RuntimeNightBusinessLifecycle.Generation;
    CookingSelectionPanelProbe.ResetRefreshProbe();
    StoragePanelProbe.ResetRefreshProbe();
    CookingSelectionPanelProbe.RecipeBoundColor = baseColor;
    RunTimePlayerDataProbe.Reset(nativeResult: false);

    PublishRareTarget(
        businessGeneration,
        listPinningEnabled: true,
        recipeVariantEnabled: false,
        cookerHighlightEnabled: true,
        seatHighlightEnabled: false,
        orderHighlightEnabled: false,
        recipeId,
        beverageId,
        ingredientIds: new[] { 83 },
        extraIngredientIds: Array.Empty<int>(),
        cookerTypeId,
        deskCode: -1,
        orderTraceId: "",
        targetRevision: "test-target");

    var cookingPanel = new CookingSelectionPanelProbe();
    var storagePanel = new StoragePanelProbe();
    var recipeButton = new UIButtonSimpleProbe(baseColor);
    CookingSelectionPanelProbe.RecipeRefreshAction = () =>
        RunTimePlayerDataProbe.CheckPinned(PlayerSaveFileDefaultPropProbe.Recipes, recipeId);
    StoragePanelProbe.RefreshAction = () =>
        RunTimePlayerDataProbe.CheckPinned(PlayerSaveFileDefaultPropProbe.Beverages, beverageId);

    try
    {
        cookingPanel.OnPanelOpen();
        storagePanel.OnPanelOpen();
        cookingPanel.OnRecipeElementEnabled(new RecipeProbe(recipeId), recipeButton.Cluster, recipeButton);
        RuntimePinnedListHighlightService.Tick();

        AssertTrue(CookingSelectionPanelProbe.LastRecipeResult == true, "The active recipe target did not use scoped pinning.");
        AssertTrue(StoragePanelProbe.LastResult == true, "The active beverage target did not use scoped pinning.");
        AssertEqual(0, RunTimePlayerDataProbe.NativeCallCount, "The active Mod targets called the native pinned probe.");
        AssertHighlighted(baseColor, recipeButton.image, "The active recipe target was not highlighted.");
        AssertTrue(RuntimeCookerHighlightService.LastEnabled, "The active cooker target did not enable the cooker stub.");
        AssertEqual(cookerTypeId, RuntimeCookerHighlightService.LastCookerTypeId, "The cooker stub did not retain the active cooker type.");

        var activeTargetGeneration = RuntimeUiPinningService.ReadTargetSet().Generation;
        var cookingRefreshCount = CookingSelectionPanelProbe.RecipeRefreshCount;
        var storageRefreshCount = StoragePanelProbe.RefreshCount;
        var setterCountBeforeClear = recipeButton.image.SetterCount;

        RunOnWorkerThread(() => PublishRareTarget(
            businessGeneration,
            listPinningEnabled: true,
            recipeVariantEnabled: false,
            cookerHighlightEnabled: true,
            seatHighlightEnabled: false,
            orderHighlightEnabled: false,
            recipeId: -1,
            beverageId: -1,
            ingredientIds: Array.Empty<int>(),
            extraIngredientIds: Array.Empty<int>(),
            cookerTypeId: -1,
            deskCode: -1,
            orderTraceId: "",
            targetRevision: "test-target"));

        var emptyTarget = RuntimeUiPinningService.ReadTargetSet();
        AssertEqual(0, emptyTarget.Targets.Count, "An empty publication retained a featureless target.");
        AssertEqual(activeTargetGeneration + 1, emptyTarget.Generation, "An empty target did not advance the target generation.");
        AssertEqual(setterCountBeforeClear, recipeButton.image.SetterCount, "Publishing an empty target touched Unity color off the main thread.");
        AssertHighlighted(baseColor, recipeButton.image, "Publishing an empty target restored the list highlight off the main thread.");
        AssertFalse(RuntimeCookerHighlightService.LastEnabled, "An empty cooker target left the cooker stub enabled.");
        AssertEqual(-1, RuntimeCookerHighlightService.LastCookerTypeId, "An empty cooker target retained the prior cooker type.");
        AssertEqual(businessGeneration, RuntimeCookerHighlightService.LastSessionGeneration, "An empty cooker target changed the business generation.");

        RuntimeUiPinningService.Tick();
        AssertEqual(cookingRefreshCount + 1, CookingSelectionPanelProbe.RecipeRefreshCount, "An open cooking panel did not refresh once for the empty target.");
        AssertEqual(storageRefreshCount + 1, StoragePanelProbe.RefreshCount, "An open storage panel did not refresh once for the empty target.");
        AssertTrue(CookingSelectionPanelProbe.LastRecipeResult == false, "The empty recipe target did not restore the native pinned result.");
        AssertTrue(StoragePanelProbe.LastResult == false, "The empty beverage target did not restore the native pinned result.");
        AssertEqual(2, RunTimePlayerDataProbe.NativeCallCount, "The empty target did not execute both native pinned probes.");

        RuntimePinnedListHighlightService.Tick();
        AssertUnhighlighted(baseColor, recipeButton.image, "The empty target did not restore the recipe highlight color.");
        AssertContains(RuntimePinnedListHighlightService.Status, "tracked=recipe:0", "The empty target kept the old recipe image tracked.");

        RuntimeUiPinningService.Tick();
        AssertEqual(cookingRefreshCount + 1, CookingSelectionPanelProbe.RecipeRefreshCount, "The empty target refreshed the cooking panel more than once.");
        AssertEqual(storageRefreshCount + 1, StoragePanelProbe.RefreshCount, "The empty target refreshed the storage panel more than once.");
    }
    finally
    {
        CookingSelectionPanelProbe.RecipeRefreshAction = null;
        StoragePanelProbe.RefreshAction = null;
        cookingPanel.OnPanelClose();
        storagePanel.OnPanelClose();
    }
}

static void VerifyEightOrderSharedListOwnership()
{
    const int recipeId = 95;
    var business = RuntimeNightBusinessLifecycle.Generation;
    var baseline = new Color(0.31f, 0.43f, 0.57f, 0.68f);
    var targets = Enumerable.Range(0, 8).Select(slot => new RuntimeUiTargetSnapshot(
        slot % 2 == 0 ? RuntimeUiTargetKind.Rare : RuntimeUiTargetKind.Normal,
        new RuntimeTargetHighlightColor((byte)(30 + slot * 20), (byte)(200 - slot * 15), (byte)(50 + slot * 17)),
        true, false, false, false, false, $"{(slot % 2 == 0 ? 'R' : 'N')}-{800 + slot}",
        slot % 2 == 0 ? "" : $"ptr:{800 + slot:x}", 800 + slot, slot, recipeId,
        new[] { 96 }, Array.Empty<int>(), -1, -1, $"eight-list:{slot}", slot)).ToArray();
    CookingSelectionPanelProbe.RecipeBoundColor = baseline;
    var panel = new CookingSelectionPanelProbe();
    var button = new UIButtonSimpleProbe(baseline);
    try
    {
        RuntimeUiPinningService.UpdateTargets(business, targets);
        panel.OnPanelOpen();
        panel.OnRecipeElementEnabled(new RecipeProbe(recipeId), button.Cluster, button);
        var image = button.image;
        var palette = RuntimeUiPinningService.ReadTargetSet().Palette;
        // 八单共用一个列表条目时同时显示八段固定颜色，原图颜色始终由游戏保留。
        for (var slot = 0; slot < 8; slot++)
        {
            Time.realtimeSinceStartup = slot * 1.5f;
            RuntimePinnedListHighlightService.Tick();
            AssertTrue(ReferenceEquals(image, button.image), "八段颜色替换了原生条目图像。");
            AssertColorBlocks(baseline, image, (RuntimeUiTargetKinds)255, palette, "共享列表条目没有同时保留全部八单的颜色。");
            AssertContains(RuntimePinnedListHighlightService.Status, "tracked=recipe:1", "同条目重复登记了图像所有权。");
        }
        for (var count = 7; count >= 1; count--)
        {
            var remaining = targets.Take(count).ToArray();
            RuntimeUiPinningService.UpdateTargets(business, remaining);
            Time.realtimeSinceStartup = 0;
            RuntimePinnedListHighlightService.Tick();
            AssertUnhighlighted(baseline, image, "新发布不能沿用上一代原生行绑定。");
            // 生产路径会重绑已打开面板的原生逻辑组；这里显式提供新代次的当前行回调。
            panel.OnRecipeElementEnabled(new RecipeProbe(recipeId), button.Cluster, button);
            RuntimePinnedListHighlightService.Tick();
            AssertColorBlocks(baseline, image, (RuntimeUiTargetKinds)((1 << count) - 1), new RuntimeTargetHighlightPalette(remaining), "共享条目撤回一单破坏了其余声明或原始基线。");
            AssertContains(RuntimePinnedListHighlightService.Status, "tracked=recipe:1", "剩余订单丢失同条目所有权。");
        }
        RuntimeUiPinningService.UpdateTargets(business, Array.Empty<RuntimeUiTargetSnapshot>());
        RuntimePinnedListHighlightService.Tick();
        AssertUnhighlighted(baseline, image, "全部八单释放后没有还原原始颜色。");
        AssertContains(RuntimePinnedListHighlightService.Status, "tracked=recipe:0", "释放全部订单后仍保留列表图像所有权。");
    }
    finally
    {
        panel.OnPanelClose();
        RuntimeUiPinningService.UpdateTargets(business, Array.Empty<RuntimeUiTargetSnapshot>());
        CookingSelectionPanelProbe.RecipeBoundColor = new Color(0.8f, 0.8f, 0.8f, 0.5f);
    }
}

/// <summary>
/// 通过正式目标发布与料理、食材、酒水绑定回调验证色槽复用：蓝单结束后第五单仍取蓝色，
/// 但该色必须排到黄红绿之后。预期直接写明，不调用生产调色板排序来生成测试答案。
/// </summary>
static void VerifyReusedColorSlotFollowsOrderLifecycle()
{
    var business = RuntimeNightBusinessLifecycle.Generation;
    var baseline = new Color(1f, 1f, 1f, .6f);
    var blue = new RuntimeTargetHighlightColor(30, 100, 240);
    var yellow = new RuntimeTargetHighlightColor(240, 210, 30);
    var red = new RuntimeTargetHighlightColor(240, 50, 40);
    var green = new RuntimeTargetHighlightColor(40, 190, 70);
    var initialColors = new[] { blue, yellow, red, green };
    RuntimeUiTargetSnapshot Target(int slot, long lifecycle) => new(
        slot % 2 == 0 ? RuntimeUiTargetKind.Rare : RuntimeUiTargetKind.Normal,
        initialColors[slot], true, false, false, false, false,
        $"{(slot % 2 == 0 ? 'R' : 'N')}-{lifecycle}", slot % 2 == 0 ? "" : $"ptr:{lifecycle:x}",
        lifecycle, slot, 95, new[] { 96 }, Array.Empty<int>(), 97, -1, $"display-order:{lifecycle}", slot);
    var initial = Enumerable.Range(0, 4).Select(slot => Target(slot, 1001 + slot)).ToArray();
    var next = new[] { Target(0, 1005), initial[1], initial[2], initial[3] };
    var panel = new CookingSelectionPanelProbe();
    var storage = new StoragePanelProbe();
    var recipeButton = new UIButtonSimpleProbe(baseline);
    var ingredientButton = new UIButtonSimpleProbe(baseline);
    var beverageButton = new UIButtonSimpleProbe(baseline);
    var buttons = new[] { recipeButton, ingredientButton, beverageButton };
    var previousRecipeColor = CookingSelectionPanelProbe.RecipeBoundColor;
    var previousIngredientColor = CookingSelectionPanelProbe.IngredientBoundColor;
    var previousIngredientBinding = CookingSelectionPanelProbe.ApplyIngredientBoundColor;
    var previousBeverageColor = StoragePanelProbe.BoundColor;
    CookingSelectionPanelProbe.RecipeBoundColor = baseline;
    CookingSelectionPanelProbe.IngredientBoundColor = baseline;
    CookingSelectionPanelProbe.ApplyIngredientBoundColor = true;
    StoragePanelProbe.BoundColor = baseline;
    void BindCurrentRows()
    {
        panel.OnRecipeElementEnabled(new RecipeProbe(95), recipeButton.Cluster, recipeButton);
        panel.OnIngElementEnabled(new(new IngredientProbe(96), 10), ingredientButton.Cluster, ingredientButton);
        storage.OnElementEnabled(new(new SellableProbe(97, SellableTypeProbe.Beverage), 10), beverageButton.Cluster, beverageButton);
        RuntimePinnedListHighlightService.Tick();
    }
    void AssertOrder(RuntimeTargetHighlightColor[] expected, string stage)
    {
        foreach (var button in buttons)
        {
            var mesh = button.image.RebuildIfDirty();
            var colors = expected.Select(color => TintedVertexColor(baseline, color)).ToArray();
            AssertEqual(colors.Length, mesh.m_Colors.Distinct().Count(), stage + "：没有完整显示四种颜色。");
            for (var index = 0; index < mesh.m_Indices.Count; index += 3)
            {
                var a = mesh.m_Indices[index]; var b = mesh.m_Indices[index + 1]; var c = mesh.m_Indices[index + 2];
                // 替身原背景固定为[-1,1]；以三角重心定位实际左右色区，防止仅检查颜色集合漏掉逆序。
                var center = (mesh.m_Positions[a].x + mesh.m_Positions[b].x + mesh.m_Positions[c].x) / 3;
                var band = Math.Clamp((int)((center + 1) / 2 * colors.Length), 0, colors.Length - 1);
                AssertEqual(colors[band], mesh.m_Colors[a], stage + "：色块没有按订单先后显示。");
                AssertEqual(colors[band], mesh.m_Colors[b], stage + "：色区边界产生了串色。");
                AssertEqual(colors[band], mesh.m_Colors[c], stage + "：色区边界产生了串色。");
            }
        }
    }
    try
    {
        // 故意乱序提交：输入数组及普客/稀客分类顺序不应改变全局订单先后。
        RuntimeUiPinningService.UpdateTargets(business, initial.Reverse().ToArray());
        panel.OnPanelOpen(); storage.OnPanelOpen(); BindCurrentRows();
        AssertOrder(initialColors, "初始蓝黄红绿");
        var first = RuntimeUiPinningService.ReadTargetSet();
        RuntimeUiPinningService.UpdateTargets(business, next);
        var reused = RuntimeUiPinningService.ReadTargetSet();
        AssertTrue(reused.Generation > first.Generation, "新订单复用同一色槽没有发布新的目标代次。");
        AssertEqual(first.GetRecipeClaims(95), reused.GetRecipeClaims(95), "只更换订单身份却改写了精确声明槽。");
        AssertTrue(initialColors.Select((color, slot) => reused.Palette[slot] == color).All(value => value),
            "为了调整显示顺序而错误重分配了颜色槽。");
        BindCurrentRows();
        AssertOrder(new[] { yellow, red, green, blue }, "蓝色槽被第五单复用后");

        var dirtyCounts = buttons.Select(button => button.image.DirtyCount).ToArray();
        RuntimeUiPinningService.UpdateTargets(business, next.Reverse().ToArray());
        RuntimePinnedListHighlightService.Tick();
        AssertTrue(ReferenceEquals(reused, RuntimeUiPinningService.ReadTargetSet()), "仅输入重排触发了重复目标发布。");
        AssertSequenceEqual(dirtyCounts, buttons.Select(button => button.image.DirtyCount), "相同目标重复刷新错误重建了原背景。");
        AssertOrder(new[] { yellow, red, green, blue }, "相同目标刷新后");
    }
    finally
    {
        panel.OnPanelClose(); storage.OnPanelClose();
        RuntimeUiPinningService.UpdateTargets(business, Array.Empty<RuntimeUiTargetSnapshot>());
        CookingSelectionPanelProbe.RecipeBoundColor = previousRecipeColor;
        CookingSelectionPanelProbe.IngredientBoundColor = previousIngredientColor;
        CookingSelectionPanelProbe.ApplyIngredientBoundColor = previousIngredientBinding;
        StoragePanelProbe.BoundColor = previousBeverageColor;
    }
}

static void VerifySharedListItemUsesSingleOwnershipAndBaseline()
{
    const int recipeId = 95;
    var businessGeneration = RuntimeNightBusinessLifecycle.Generation;
    var baseline = new Color(0.31f, 0.43f, 0.57f, 0.68f);
    var rareColor = new RuntimeTargetHighlightColor(0xE1, 0xB2, 0x31);
    var normalColor = new RuntimeTargetHighlightColor(0x42, 0x91, 0xC8);
    var palette = new RuntimeTargetHighlightPalette(rareColor, normalColor);
    RuntimeUiTargetSnapshot Create(
        RuntimeUiTargetKind kind,
        RuntimeTargetHighlightColor color,
        string trace,
        string orderKey,
        long lifecycle,
        int deskCode)
    {
        return new RuntimeUiTargetSnapshot(
            kind,
            color,
            listPinningEnabled: true,
            recipeVariantEnabled: false,
            cookerHighlightEnabled: false,
            seatHighlightEnabled: false,
            orderHighlightEnabled: false,
            trace,
            orderKey,
            lifecycle,
            deskCode,
            recipeId,
            ingredientIds: new[] { 96 },
            extraIngredientIds: Array.Empty<int>(),
            beverageId: -1,
            cookerTypeId: -1,
            targetRevision: $"shared-list-{kind}");
    }

    var targetSet = new RuntimeUiTargetSetSnapshot(
        generation: 0,
        sessionGeneration: businessGeneration,
        new[]
        {
            Create(RuntimeUiTargetKind.Rare, rareColor, "R-95", "", 95, 0),
            Create(RuntimeUiTargetKind.Normal, normalColor, "N-96", "ptr:60", 96, 1),
        });
    RuntimeUiPinningService.UpdateTargets(
        businessGeneration,
        targetSet.Targets);

    CookingSelectionPanelProbe.RecipeBoundColor = baseline;
    var panel = new CookingSelectionPanelProbe();
    var button = new UIButtonSimpleProbe(baseline);
    try
    {
        panel.OnPanelOpen();
        panel.OnRecipeElementEnabled(new RecipeProbe(recipeId), button.Cluster, button);
        AssertContains(RuntimePinnedListHighlightService.Status, "tracked=recipe:1", "A shared recipe registered more than one Image ownership.");

        var normalEndpointTime = 1.6f;
        Time.realtimeSinceStartup = normalEndpointTime;
        RuntimePinnedListHighlightService.Tick();
        AssertColorBlocks(baseline, button.image, RuntimeUiTargetKinds.Rare | RuntimeUiTargetKinds.Normal, palette, "The shared list Image did not reach the normal-color endpoint.");

        var rareEndpointTime = 3.1f;
        Time.realtimeSinceStartup = rareEndpointTime;
        RuntimePinnedListHighlightService.Tick();
        AssertColorBlocks(baseline, button.image, RuntimeUiTargetKinds.Rare | RuntimeUiTargetKinds.Normal, palette, "The shared list Image did not return to the rare-color endpoint.");

        RuntimeUiPinningService.UpdateTargets(
            businessGeneration,
            Array.Empty<RuntimeUiTargetSnapshot>());
        RuntimePinnedListHighlightService.Tick();
        AssertUnhighlighted(baseline, button.image, "Disabling a shared list target did not restore its single original baseline.");
        AssertContains(RuntimePinnedListHighlightService.Status, "tracked=recipe:0", "Disabling a shared list target retained Image ownership.");
    }
    finally
    {
        panel.OnPanelClose();
        CookingSelectionPanelProbe.RecipeBoundColor = new Color(0.8f, 0.8f, 0.8f, 0.5f);
    }
}

static void VerifyOpenPanelRefreshScheduling()
{
    var mainThreadId = Environment.CurrentManagedThreadId;
    CookingSelectionPanelProbe.ResetRefreshProbe();
    StoragePanelProbe.ResetRefreshProbe();

    void Publish(int recipeId, int beverageId, bool listPinningEnabled = true)
    {
        PublishRareTarget(
            RuntimeNightBusinessLifecycle.Generation,
            listPinningEnabled,
            recipeVariantEnabled: false,
            cookerHighlightEnabled: false,
            seatHighlightEnabled: false,
            orderHighlightEnabled: false,
            recipeId,
            beverageId,
            ingredientIds: listPinningEnabled ? new[] { recipeId + 1000 } : Array.Empty<int>(),
            extraIngredientIds: Array.Empty<int>(),
            cookerTypeId: listPinningEnabled ? 3 : -1,
            deskCode: -1,
            orderTraceId: "",
            targetRevision: "test-target");
    }

    try
    {
        Publish(300, 400);
        var naturallyRefreshedCookingPanel = new CookingSelectionPanelProbe();
        var naturallyRefreshedStoragePanel = new StoragePanelProbe();
        naturallyRefreshedCookingPanel.OnPanelOpen();
        naturallyRefreshedStoragePanel.OnPanelOpen();
        AssertEqual(1, CookingSelectionPanelProbe.RecipeRefreshCount, "Cooking panel did not naturally apply an existing target.");
        AssertEqual(1, StoragePanelProbe.RefreshCount, "Storage panel did not naturally apply an existing target.");
        RuntimeUiPinningService.Tick();
        AssertEqual(1, CookingSelectionPanelProbe.RecipeRefreshCount, "Cooking panel repeated a target already applied during open.");
        AssertEqual(1, StoragePanelProbe.RefreshCount, "Storage panel repeated a target already applied during open.");
        naturallyRefreshedCookingPanel.OnPanelClose();
        naturallyRefreshedStoragePanel.OnPanelDestroyed();
        CookingSelectionPanelProbe.ResetRefreshProbe();
        StoragePanelProbe.ResetRefreshProbe();

        Publish(-1, -1, listPinningEnabled: false);
        var cookingPanel = new CookingSelectionPanelProbe();
        var storagePanel = new StoragePanelProbe();
        cookingPanel.OnPanelOpen();
        storagePanel.OnPanelOpen();
        AssertEqual(1, CookingSelectionPanelProbe.RecipeRefreshCount, "Cooking panel open did not perform its natural refresh.");
        AssertEqual(1, CookingSelectionPanelProbe.FullVisualRefreshCount,
            "Cooking panel open did not perform exactly one natural full refresh.");
        AssertEqual(1, StoragePanelProbe.RefreshCount, "Storage panel open did not perform its natural refresh.");

        RunOnWorkerThread(() => Publish(301, 401));
        AssertEqual(1, CookingSelectionPanelProbe.RecipeRefreshCount, "Worker target publication refreshed the cooking panel.");
        AssertEqual(1, StoragePanelProbe.RefreshCount, "Worker target publication refreshed the storage panel.");
        RuntimeUiPinningService.Tick();
        AssertEqual(2, CookingSelectionPanelProbe.RecipeRefreshCount, "An open cooking panel did not consume the new target once.");
        AssertEqual(1, CookingSelectionPanelProbe.FullVisualRefreshCount,
            "Target publication used full UpdateAllVisual instead of direct UpdateRecipeField.");
        AssertEqual(2, StoragePanelProbe.RefreshCount, "An open storage panel did not consume the new target once.");
        RuntimeUiPinningService.Tick();
        AssertEqual(2, CookingSelectionPanelProbe.RecipeRefreshCount, "Cooking panel refreshed twice for one target generation.");
        AssertEqual(2, StoragePanelProbe.RefreshCount, "Storage panel refreshed twice for one target generation.");

        RunOnWorkerThread(() => Publish(301, 401));
        RuntimeUiPinningService.Tick();
        AssertEqual(2, CookingSelectionPanelProbe.RecipeRefreshCount, "An identical target refreshed the cooking panel.");
        AssertEqual(2, StoragePanelProbe.RefreshCount, "An identical target refreshed the storage panel.");

        RunOnWorkerThread(() => Publish(302, 402));
        RuntimeUiPinningService.Tick();
        AssertEqual(3, CookingSelectionPanelProbe.RecipeRefreshCount, "A new target did not refresh the cooking panel exactly once.");
        AssertEqual(3, StoragePanelProbe.RefreshCount, "A new target did not refresh the storage panel exactly once.");

        cookingPanel.OnPanelClose();
        RunOnWorkerThread(() => Publish(303, 403));
        RuntimeUiPinningService.Tick();
        AssertEqual(3, CookingSelectionPanelProbe.RecipeRefreshCount, "A closed cooking panel was refreshed.");
        AssertEqual(4, StoragePanelProbe.RefreshCount, "The still-open storage panel did not refresh.");

        storagePanel.OnPanelDestroyed();
        RunOnWorkerThread(() => Publish(304, 404));
        RuntimeUiPinningService.Tick();
        AssertEqual(3, CookingSelectionPanelProbe.RecipeRefreshCount, "A closed cooking panel returned after another target.");
        AssertEqual(4, StoragePanelProbe.RefreshCount, "A destroyed storage panel was refreshed.");

        var staleCookingPanel = new CookingSelectionPanelProbe();
        var staleStoragePanel = new StoragePanelProbe();
        staleCookingPanel.OnPanelOpen();
        staleStoragePanel.OnPanelOpen();
        AssertEqual(4, CookingSelectionPanelProbe.RecipeRefreshCount, "Generation-mismatch cooking setup did not open.");
        AssertEqual(5, StoragePanelProbe.RefreshCount, "Generation-mismatch storage setup did not open.");
        RuntimeNightBusinessLifecycle.ActivateNextGeneration();
        RunOnWorkerThread(() => Publish(305, 405));
        RuntimeUiPinningService.Tick();
        AssertEqual(4, CookingSelectionPanelProbe.RecipeRefreshCount, "A prior-generation cooking panel was refreshed.");
        AssertEqual(5, StoragePanelProbe.RefreshCount, "A prior-generation storage panel was refreshed.");
        staleCookingPanel.OnPanelClose();
        staleStoragePanel.OnPanelDestroyed();

        var failingCookingPanel = new CookingSelectionPanelProbe();
        failingCookingPanel.OnPanelOpen();
        AssertEqual(5, CookingSelectionPanelProbe.RecipeRefreshCount, "Failure-path cooking setup did not open.");
        RunOnWorkerThread(() => Publish(306, 406));
        CookingSelectionPanelProbe.ThrowOnRecipeRefresh = true;
        RuntimeUiPinningService.Tick();
        AssertEqual(6, CookingSelectionPanelProbe.RecipeRefreshCount, "The failing cooking refresh was not attempted once.");
        RuntimeUiPinningService.Tick();
        AssertEqual(6, CookingSelectionPanelProbe.RecipeRefreshCount, "A failed cooking refresh retried without a new target.");
        AssertContains(RuntimeUiPinningService.Status, "failures:1", "Panel refresh failure diagnostics were not retained.");

        CookingSelectionPanelProbe.ThrowOnRecipeRefresh = false;
        RunOnWorkerThread(() => Publish(307, 407));
        RuntimeUiPinningService.Tick();
        AssertEqual(7, CookingSelectionPanelProbe.RecipeRefreshCount, "A later target did not recover after the one-shot failure.");
        failingCookingPanel.OnPanelClose();

        AssertTrue(
            CookingSelectionPanelProbe.RecipeRefreshThreadIds.All(threadId => threadId == mainThreadId),
            "A cooking panel refresh ran outside the Unity main thread.");
        AssertTrue(
            StoragePanelProbe.RefreshThreadIds.All(threadId => threadId == mainThreadId),
            "A storage panel refresh ran outside the Unity main thread.");

        CookingSelectionPanelProbe.ResetRefreshProbe();
        var destroyedWithoutClose = new CookingSelectionPanelProbe();
        destroyedWithoutClose.OnPanelOpen();
        AssertEqual(1, CookingSelectionPanelProbe.RecipeRefreshCount,
            "Destroy-without-close setup did not open the cooking panel once.");
        destroyedWithoutClose.OnPanelDestroyed();
        RuntimeUiPinningService.Tick();
        AssertContains(RuntimeUiPinningService.Status, "panelRefresh=cooking:closed",
            "An unchanged target did not evict the destroyed cooking panel by exact dead pointer.");
        RunOnWorkerThread(() => Publish(308, 408));
        RuntimeUiPinningService.Tick();
        AssertEqual(1, CookingSelectionPanelProbe.RecipeRefreshCount,
            "A destroyed cooking panel was refreshed without a destroy Hook.");
        AssertContains(RuntimeUiPinningService.Status, "panelRefresh=cooking:closed",
            "A later target restored a destroyed cooking panel registration.");
    }
    finally
    {
        CookingSelectionPanelProbe.ThrowOnRecipeRefresh = false;
        StoragePanelProbe.ThrowOnRefresh = false;
    }
}

static void VerifyOpenPanelSurfaceRefreshSemantics()
{
    CookingSelectionPanelProbe.ResetRefreshProbe();
    StoragePanelProbe.ResetRefreshProbe();
    var businessGeneration = RuntimeNightBusinessLifecycle.Generation;
    var cookingPanel = new CookingSelectionPanelProbe();
    var storagePanel = new StoragePanelProbe();
    CookingSelectionPanelProbe.IngredientRefreshAction = () =>
    {
        var ingredientIds = RuntimeUiPinningService.ReadTargetSet().Targets
            .Where(target => target.ListPinningEnabled)
            .SelectMany(target => target.IngredientIds)
            .Distinct()
            .OrderBy(ingredientId => ingredientId)
            .ToArray();
        cookingPanel.SetIngredientSurfaceSource(ingredientIds);
        return true;
    };
    CookingSelectionPanelProbe.RecipeRefreshAction = () =>
    {
        var recipeIds = RuntimeUiPinningService.ReadTargetSet().Targets
            .Where(target => target.ListPinningEnabled && target.RecipeId >= 0)
            .Select(target => target.RecipeId)
            .ToArray();
        cookingPanel.SetRecipeSurfaceSource(recipeIds);
        return true;
    };
    StoragePanelProbe.RefreshAction = () =>
    {
        var beverageIds = RuntimeUiPinningService.ReadTargetSet().Targets
            .Where(target => target.ListPinningEnabled && target.BeverageId >= 0)
            .Select(target => target.BeverageId)
            .ToArray();
        storagePanel.SetBeverageSurfaceSource(beverageIds);
        return true;
    };

    try
    {
        PublishRareTarget(
            businessGeneration,
            listPinningEnabled: true,
            recipeVariantEnabled: true,
            cookerHighlightEnabled: false,
            seatHighlightEnabled: false,
            orderHighlightEnabled: false,
            recipeId: 510,
            beverageId: 610,
            ingredientIds: new[] { 511 },
            extraIngredientIds: new[] { 512 },
            cookerTypeId: -1,
            deskCode: -1,
            orderTraceId: "",
            targetRevision: "surface-enabled");
        cookingPanel.OnPanelOpen();
        storagePanel.OnPanelOpen();
        AssertSequenceEqual(new[] { 511 }, cookingPanel.VisibleIngredientIds,
            "Natural cooking open did not materialize its ingredient surface.");
        AssertSequenceEqual(new[] { 510 }, cookingPanel.VisibleRecipeIds,
            "Natural cooking open did not materialize its recipe surface.");
        AssertSequenceEqual(new[] { 610 }, storagePanel.VisibleBeverageIds,
            "Natural storage open did not materialize its beverage surface.");

        PublishRareTarget(
            businessGeneration,
            listPinningEnabled: false,
            recipeVariantEnabled: false,
            cookerHighlightEnabled: false,
            seatHighlightEnabled: false,
            orderHighlightEnabled: false,
            recipeId: -1,
            beverageId: -1,
            ingredientIds: Array.Empty<int>(),
            extraIngredientIds: Array.Empty<int>(),
            cookerTypeId: -1,
            deskCode: -1,
            orderTraceId: "",
            targetRevision: "surface-disabled");
        RuntimeUiPinningService.Tick();
        AssertSequenceEqual(Array.Empty<int>(), cookingPanel.VisibleIngredientIds,
            "Disabling the open cooking feature rebuilt recipes but left stale ingredient rows visible.");
        AssertSequenceEqual(Array.Empty<int>(), cookingPanel.VisibleRecipeIds,
            "Disabling the open cooking feature rebuilt data but left stale recipe rows visible.");
        AssertSequenceEqual(Array.Empty<int>(), storagePanel.VisibleBeverageIds,
            "Disabling the open beverage feature rebuilt data but left stale beverage rows visible.");
        AssertEqual(1, CookingSelectionPanelProbe.SelectedSurfaceRefreshCount,
            "The bounded target refresh unexpectedly rebuilt the selected-ingredient surface.");
        AssertEqual(1, CookingSelectionPanelProbe.OutputSurfaceRefreshCount,
            "The bounded target refresh unexpectedly rebuilt the output surface.");

        var cookingDataCountBeforeFailure = CookingSelectionPanelProbe.RecipeRefreshCount;
        var cookingSurfaceCountBeforeFailure = CookingSelectionPanelProbe.RecipeSurfaceRefreshCount;
        var cookingAppliedBeforeFailure = ReadPanelRefreshGeneration("cooking", "applied");
        var panelFailuresBefore = ReadPanelRefreshCounter("failures");
        CookingSelectionPanelProbe.ThrowOnRecipeSurfaceRefresh = true;
        PublishRareTarget(
            businessGeneration,
            listPinningEnabled: true,
            recipeVariantEnabled: false,
            cookerHighlightEnabled: false,
            seatHighlightEnabled: false,
            orderHighlightEnabled: false,
            recipeId: 520,
            beverageId: 620,
            ingredientIds: new[] { 521 },
            extraIngredientIds: Array.Empty<int>(),
            cookerTypeId: -1,
            deskCode: -1,
            orderTraceId: "",
            targetRevision: "surface-failure");
        RuntimeUiPinningService.Tick();
        AssertEqual(cookingDataCountBeforeFailure + 1, CookingSelectionPanelProbe.RecipeRefreshCount,
            "Cooking surface failure did not first rebuild recipe data exactly once.");
        AssertEqual(cookingSurfaceCountBeforeFailure + 1, CookingSelectionPanelProbe.RecipeSurfaceRefreshCount,
            "Cooking surface failure did not attempt m_StaticRecipeGroup.UpdateElements exactly once.");
        AssertEqual(panelFailuresBefore + 1, ReadPanelRefreshCounter("failures"),
            "Cooking surface failure was not retained exactly once in panel diagnostics.");
        AssertEqual(cookingAppliedBeforeFailure, ReadPanelRefreshGeneration("cooking", "applied"),
            "Cooking data completion was incorrectly committed before the visible group refresh succeeded.");
        AssertContains(RuntimeUiPinningService.Status, "stage=recipe-visible-elements",
            "Cooking logical-group failure diagnostics lost the exact refresh stage.");
        RuntimeUiPinningService.Tick();
        AssertEqual(cookingDataCountBeforeFailure + 1, CookingSelectionPanelProbe.RecipeRefreshCount,
            "A partial cooking surface failure retried within the same target generation.");

        CookingSelectionPanelProbe.ThrowOnRecipeSurfaceRefresh = false;
        cookingPanel.OnPanelClose();
        storagePanel.OnPanelClose();

        CookingSelectionPanelProbe.ResetRefreshProbe();
        StoragePanelProbe.ResetRefreshProbe();
        var foodStoragePanel = new StoragePanelProbe(SellableTypeProbe.Food);
        foodStoragePanel.OnPanelOpen();
        AssertEqual(0, StoragePanelProbe.RefreshCount,
            "A food-mode storage panel unexpectedly rebuilt beverage data during natural open.");
        PublishRareTarget(
            businessGeneration,
            listPinningEnabled: true,
            recipeVariantEnabled: false,
            cookerHighlightEnabled: false,
            seatHighlightEnabled: false,
            orderHighlightEnabled: false,
            recipeId: 530,
            beverageId: 630,
            ingredientIds: new[] { 531 },
            extraIngredientIds: Array.Empty<int>(),
            cookerTypeId: -1,
            deskCode: -1,
            orderTraceId: "",
            targetRevision: "food-storage-not-beverage");
        RuntimeUiPinningService.Tick();
        AssertEqual(0, StoragePanelProbe.RefreshCount,
            "A food-mode storage panel was registered as a beverage surface.");
        AssertEqual(0, StoragePanelProbe.SurfaceRefreshCount,
            "A food-mode storage panel rebuilt m_BevsGroup after target publication.");
        foodStoragePanel.OnPanelClose();
    }
    finally
    {
        CookingSelectionPanelProbe.ThrowOnRecipeSurfaceRefresh = false;
        StoragePanelProbe.ThrowOnSurfaceRefresh = false;
        CookingSelectionPanelProbe.IngredientRefreshAction = null;
        CookingSelectionPanelProbe.RecipeRefreshAction = null;
        StoragePanelProbe.RefreshAction = null;
        cookingPanel.OnPanelClose();
        storagePanel.OnPanelClose();
    }
}

static void VerifyAuthorityTransitionPreservesOpenPanels()
{
    CookingSelectionPanelProbe.ResetRefreshProbe();
    StoragePanelProbe.ResetRefreshProbe();
    var businessGeneration = RuntimeNightBusinessLifecycle.Generation;
    PublishRareTarget(
        businessGeneration,
        listPinningEnabled: true,
        recipeVariantEnabled: false,
        cookerHighlightEnabled: false,
        seatHighlightEnabled: false,
        orderHighlightEnabled: false,
        recipeId: 470,
        beverageId: 570,
        ingredientIds: new[] { 471 },
        extraIngredientIds: Array.Empty<int>(),
        cookerTypeId: -1,
        deskCode: -1,
        orderTraceId: "",
        targetRevision: "authority-before");

    var cookingPanel = new CookingSelectionPanelProbe();
    var storagePanel = new StoragePanelProbe();
    try
    {
        cookingPanel.OnPanelOpen();
        storagePanel.OnPanelOpen();
        var presentationTarget = RuntimeUiPinningService.ReadTargetSet();
        var retireCount = RuntimeTargetRecipeVariantService.ShutdownRetireCount;
        var selectedRefreshCount = CookingSelectionPanelProbe.SelectedSurfaceRefreshCount;
        var outputRefreshCount = CookingSelectionPanelProbe.OutputSurfaceRefreshCount;

        RunOnWorkerThread(() => RuntimeUiPinningService.ClearTargetsForAuthorityTransition(
            businessGeneration,
            "test primary profile changed"));
        AssertEqual(1, CookingSelectionPanelProbe.RecipeRefreshCount,
            "Authority fencing refreshed the cooking page outside the Unity main thread.");
        AssertEqual(1, StoragePanelProbe.RefreshCount,
            "Authority fencing refreshed the storage page outside the Unity main thread.");
        AssertEqual(retireCount, RuntimeTargetRecipeVariantService.ShutdownRetireCount,
            "Authority fencing retired recipe-variant state as if the controller had shut down.");
        AssertContains(RuntimeUiPinningService.Status, "panelRefresh=cooking:open",
            "Authority fencing discarded the open cooking-page registration.");
        AssertContains(RuntimeUiPinningService.Status, "storage:open",
            "Authority fencing discarded the open storage-page registration.");
        AssertContains(RuntimeUiPinningService.Status, "authority-fence-preserved",
            "Authority fencing diagnostics do not distinguish the non-terminal transition.");
        AssertContains(RuntimeUiPinningService.Status, "authorityPanelFence=pending:",
            "Authority fencing diagnostics do not expose the deferred panel-refresh generation.");
        AssertEqual(0, RuntimeUiPinningService.ReadTargetSet().Targets.Count,
            "Authority fencing retained the previous writer's UI target.");
        AssertTrue(
            ReferenceEquals(
                presentationTarget,
                RuntimeUiPinningService.ReadSurfaceRefreshTargetSet()),
            "Authority fencing did not retain the last confirmed target for native list presentation.");
        AssertTrue(
            RuntimeUiPinningService.IsSurfaceRefreshTargetCurrentOrDeferred(presentationTarget),
            "The held panel presentation target was rejected during its exact authority fence.");
        AssertFalse(
            RuntimeUiPinningService.TryAcquireTargetPublicationLease(presentationTarget, out _),
            "The held presentation target still authorized a recipe action during the authority fence.");

        RuntimeUiPinningService.Tick();
        AssertEqual(1, CookingSelectionPanelProbe.RecipeRefreshCount,
            "The open cooking page consumed the transient empty authority fence.");
        AssertEqual(1, StoragePanelProbe.RefreshCount,
            "The open storage page consumed the transient empty authority fence.");
        AssertEqual(selectedRefreshCount, CookingSelectionPanelProbe.SelectedSurfaceRefreshCount,
            "Authority fencing rebuilt the selected-ingredient surface.");
        AssertEqual(outputRefreshCount, CookingSelectionPanelProbe.OutputSurfaceRefreshCount,
            "Authority fencing rebuilt the output surface.");

        var cookingRefreshUsedPresentationTarget = false;
        var cookingRefreshRetainedPresentationAfterPublication = false;
        var storageRefreshUsedPresentationTarget = false;
        CookingSelectionPanelProbe.RecipeRefreshAction = () =>
        {
            cookingRefreshUsedPresentationTarget =
                ReferenceEquals(
                    presentationTarget,
                    RuntimeUiPinningService.ReadSurfaceRefreshTargetSet())
                && RuntimeUiPinningService.ReadSurfaceRefreshTargetSet()
                    .GetRecipeClaims(470) != RuntimeUiTargetKinds.None;
            RunOnWorkerThread(() => PublishRareTarget(
                businessGeneration,
                listPinningEnabled: true,
                recipeVariantEnabled: true,
                cookerHighlightEnabled: false,
                seatHighlightEnabled: false,
                orderHighlightEnabled: false,
                recipeId: 472,
                beverageId: 572,
                ingredientIds: new[] { 473 },
                extraIngredientIds: new[] { 474 },
                cookerTypeId: -1,
                deskCode: -1,
                orderTraceId: "",
                targetRevision: "authority-after"));
            cookingRefreshRetainedPresentationAfterPublication =
                RuntimeUiPinningService.ReadTargetSet().GetRecipeClaims(472)
                    != RuntimeUiTargetKinds.None
                && ReferenceEquals(
                    presentationTarget,
                    RuntimeUiPinningService.ReadSurfaceRefreshTargetSet())
                && RuntimeUiPinningService.IsSurfaceRefreshTargetCurrentOrDeferred(
                    presentationTarget);
            return cookingRefreshUsedPresentationTarget
                && cookingRefreshRetainedPresentationAfterPublication;
        };
        StoragePanelProbe.RefreshAction = () =>
        {
            storageRefreshUsedPresentationTarget =
                ReferenceEquals(
                    presentationTarget,
                    RuntimeUiPinningService.ReadSurfaceRefreshTargetSet())
                && RuntimeUiPinningService.ReadSurfaceRefreshTargetSet()
                    .GetBeverageClaims(570) != RuntimeUiTargetKinds.None;
            return storageRefreshUsedPresentationTarget;
        };
        storagePanel.UpdateBevField();
        cookingPanel.UpdateAllVisual();
        AssertTrue(cookingRefreshUsedPresentationTarget,
            "A natural cooking-page refresh consumed the empty operational fence instead of the held presentation target.");
        AssertTrue(cookingRefreshRetainedPresentationAfterPublication,
            "An in-flight natural cooking refresh lost its held presentation target when the new authority published concurrently.");
        AssertTrue(storageRefreshUsedPresentationTarget,
            "A natural storage-page refresh consumed the empty operational fence instead of the held presentation target.");
        CookingSelectionPanelProbe.RecipeRefreshAction = null;
        StoragePanelProbe.RefreshAction = null;
        RuntimeUiPinningService.Tick();
        AssertEqual(3, CookingSelectionPanelProbe.RecipeRefreshCount,
            "The retained cooking page did not consume the new authority target once.");
        AssertEqual(3, StoragePanelProbe.RefreshCount,
            "The retained storage page did not consume the new authority target once.");
        AssertContains(RuntimeUiPinningService.Status, "target-set-published",
            "Fresh authority target publication did not replace the fence diagnostic state.");
        AssertContains(RuntimeUiPinningService.Status, "authorityPanelFence=none",
            "Fresh authority target publication did not close the panel-refresh fence.");
        AssertFalse(
            RuntimeUiPinningService.IsSurfaceRefreshTargetCurrentOrDeferred(presentationTarget),
            "The old presentation target remained valid after the new authority publication.");

        RunOnWorkerThread(() => RuntimeUiPinningService.ClearTargetsForAuthorityTransition(
            businessGeneration,
            "test primary profile disabled all targets"));
        RuntimeUiPinningService.Tick();
        AssertEqual(3, CookingSelectionPanelProbe.RecipeRefreshCount,
            "A second empty authority fence refreshed the cooking page before confirmation.");
        AssertEqual(3, StoragePanelProbe.RefreshCount,
            "A second empty authority fence refreshed the storage page before confirmation.");

        RuntimeUiPinningService.UpdateTargets(
            businessGeneration,
            Array.Empty<RuntimeUiTargetSnapshot>());
        AssertContains(RuntimeUiPinningService.Status, "authorityPanelFence=none",
            "A fresh authoritative empty target did not close the panel-refresh fence.");
        RuntimeUiPinningService.Tick();
        AssertEqual(4, CookingSelectionPanelProbe.RecipeRefreshCount,
            "A fresh authoritative empty target did not clear the retained cooking page once.");
        AssertEqual(4, StoragePanelProbe.RefreshCount,
            "A fresh authoritative empty target did not clear the retained storage page once.");
    }
    finally
    {
        CookingSelectionPanelProbe.RecipeRefreshAction = null;
        StoragePanelProbe.RefreshAction = null;
        cookingPanel.OnPanelClose();
        storagePanel.OnPanelDestroyed();
    }
}

static void VerifyCookingRefreshStagesFailClosed()
{
    CookingSelectionPanelProbe.ResetRefreshProbe();
    var businessGeneration = RuntimeNightBusinessLifecycle.Generation;
    var cookingPanel = new CookingSelectionPanelProbe();

    void Publish(int targetId)
    {
        PublishRareTarget(
            businessGeneration,
            listPinningEnabled: true,
            recipeVariantEnabled: false,
            cookerHighlightEnabled: false,
            seatHighlightEnabled: false,
            orderHighlightEnabled: false,
            recipeId: 700 + targetId,
            beverageId: -1,
            ingredientIds: new[] { 800 + targetId },
            extraIngredientIds: Array.Empty<int>(),
            cookerTypeId: -1,
            deskCode: -1,
            orderTraceId: "",
            targetRevision: $"cooking-stage-{targetId}");
    }

    void AssertFailure(
        int targetId,
        string expectedStage,
        Action armFailure,
        Action disarmFailure,
        int expectedIngredientDataDelta,
        int expectedRecipeDataDelta,
        int expectedIngredientSurfaceDelta,
        int expectedRecipeSurfaceDelta)
    {
        var ingredientDataBefore = CookingSelectionPanelProbe.IngredientRefreshCount;
        var recipeDataBefore = CookingSelectionPanelProbe.RecipeRefreshCount;
        var ingredientSurfaceBefore = CookingSelectionPanelProbe.IngredientSurfaceRefreshCount;
        var recipeSurfaceBefore = CookingSelectionPanelProbe.RecipeSurfaceRefreshCount;
        var appliedBefore = ReadPanelRefreshGeneration("cooking", "applied");
        var failuresBefore = ReadPanelRefreshCounter("failures");

        armFailure();
        Publish(targetId);
        RuntimeUiPinningService.Tick();
        disarmFailure();

        AssertEqual(ingredientDataBefore + expectedIngredientDataDelta,
            CookingSelectionPanelProbe.IngredientRefreshCount,
            $"{expectedStage} executed an unexpected number of ingredient-data stages.");
        AssertEqual(recipeDataBefore + expectedRecipeDataDelta,
            CookingSelectionPanelProbe.RecipeRefreshCount,
            $"{expectedStage} executed an unexpected number of recipe-data stages.");
        AssertEqual(ingredientSurfaceBefore + expectedIngredientSurfaceDelta,
            CookingSelectionPanelProbe.IngredientSurfaceRefreshCount,
            $"{expectedStage} executed an unexpected number of ingredient visible stages.");
        AssertEqual(recipeSurfaceBefore + expectedRecipeSurfaceDelta,
            CookingSelectionPanelProbe.RecipeSurfaceRefreshCount,
            $"{expectedStage} executed an unexpected number of recipe visible stages.");
        AssertEqual(appliedBefore, ReadPanelRefreshGeneration("cooking", "applied"),
            $"{expectedStage} incorrectly committed a partial cooking refresh.");
        AssertEqual(failuresBefore + 1, ReadPanelRefreshCounter("failures"),
            $"{expectedStage} was not counted as exactly one panel refresh failure.");
        AssertContains(RuntimeUiPinningService.Status, $"stage={expectedStage}",
            $"{expectedStage} diagnostics lost the exact failed stage.");

        RuntimeUiPinningService.Tick();
        AssertEqual(ingredientDataBefore + expectedIngredientDataDelta,
            CookingSelectionPanelProbe.IngredientRefreshCount,
            $"{expectedStage} retried within the same target generation.");
        AssertEqual(failuresBefore + 1, ReadPanelRefreshCounter("failures"),
            $"{expectedStage} recorded the same generation more than once.");
    }

    try
    {
        PublishRareTarget(
            businessGeneration,
            listPinningEnabled: false,
            recipeVariantEnabled: false,
            cookerHighlightEnabled: false,
            seatHighlightEnabled: false,
            orderHighlightEnabled: false,
            recipeId: -1,
            beverageId: -1,
            ingredientIds: Array.Empty<int>(),
            extraIngredientIds: Array.Empty<int>(),
            cookerTypeId: -1,
            deskCode: -1,
            orderTraceId: "",
            targetRevision: "cooking-stage-baseline");
        cookingPanel.OnPanelOpen();

        AssertFailure(
            1,
            "ingredient-backing-data",
            () => CookingSelectionPanelProbe.ThrowOnIngredientRefresh = true,
            () => CookingSelectionPanelProbe.ThrowOnIngredientRefresh = false,
            expectedIngredientDataDelta: 1,
            expectedRecipeDataDelta: 0,
            expectedIngredientSurfaceDelta: 0,
            expectedRecipeSurfaceDelta: 0);
        AssertFailure(
            2,
            "recipe-backing-data",
            () => CookingSelectionPanelProbe.ThrowOnRecipeRefresh = true,
            () => CookingSelectionPanelProbe.ThrowOnRecipeRefresh = false,
            expectedIngredientDataDelta: 1,
            expectedRecipeDataDelta: 1,
            expectedIngredientSurfaceDelta: 0,
            expectedRecipeSurfaceDelta: 0);
        AssertFailure(
            3,
            "ingredient-visible-elements",
            () => CookingSelectionPanelProbe.ThrowOnIngredientSurfaceRefresh = true,
            () => CookingSelectionPanelProbe.ThrowOnIngredientSurfaceRefresh = false,
            expectedIngredientDataDelta: 1,
            expectedRecipeDataDelta: 1,
            expectedIngredientSurfaceDelta: 1,
            expectedRecipeSurfaceDelta: 0);
        AssertFailure(
            4,
            "recipe-visible-elements",
            () => CookingSelectionPanelProbe.ThrowOnRecipeSurfaceRefresh = true,
            () => CookingSelectionPanelProbe.ThrowOnRecipeSurfaceRefresh = false,
            expectedIngredientDataDelta: 1,
            expectedRecipeDataDelta: 1,
            expectedIngredientSurfaceDelta: 1,
            expectedRecipeSurfaceDelta: 1);

        var successSequenceStart = CookingSelectionPanelProbe.RefreshSequence.Count;
        Publish(5);
        var successfulTargetGeneration = RuntimeUiPinningService.ReadTargetSet().Generation;
        RuntimeUiPinningService.Tick();
        AssertEqual(successfulTargetGeneration, ReadPanelRefreshGeneration("cooking", "applied"),
            "A later target did not recover after all four one-shot cooking stage failures.");
        AssertSequenceEqual(
            new[] { "ingredient-data", "recipe-data", "ingredient-visible", "recipe-visible" },
            CookingSelectionPanelProbe.RefreshSequence.Skip(successSequenceStart),
            "A recovered cooking refresh did not execute its four stages exactly once and in order.");
        AssertEqual(1, CookingSelectionPanelProbe.SelectedSurfaceRefreshCount,
            "Cooking stage failures or recovery rebuilt the selected-ingredient surface.");
        AssertEqual(1, CookingSelectionPanelProbe.OutputSurfaceRefreshCount,
            "Cooking stage failures or recovery rebuilt the output surface.");
    }
    finally
    {
        CookingSelectionPanelProbe.ThrowOnIngredientRefresh = false;
        CookingSelectionPanelProbe.ThrowOnRecipeRefresh = false;
        CookingSelectionPanelProbe.ThrowOnIngredientSurfaceRefresh = false;
        CookingSelectionPanelProbe.ThrowOnRecipeSurfaceRefresh = false;
        cookingPanel.OnPanelClose();
    }
}

static void VerifyLifecycleGenerationGuards()
{
    var firstGeneration = RuntimeNightBusinessLifecycle.Generation;
    PublishRareTarget(
        firstGeneration,
        listPinningEnabled: true,
        recipeVariantEnabled: false,
        cookerHighlightEnabled: true,
        seatHighlightEnabled: false,
        orderHighlightEnabled: true,
        recipeId: 90,
        beverageId: 91,
        ingredientIds: new[] { 92 },
        extraIngredientIds: Array.Empty<int>(),
        cookerTypeId: 3,
        deskCode: 1,
        orderTraceId: "R-9001",
        targetRevision: "test-target");

    InvokePrivate("OnCookingRefreshStarted", new CookingSelectionPanelProbe());
    try
    {
        AssertTrue(InvokeCheckPinned(1, 90), "The active generation did not force its recipe target.");
        RuntimeNightBusinessLifecycle.BeginClosing();
        AssertTrue(InvokeCheckPinnedPrefix(1, 90).RunOriginal, "A scope captured before Closing still skipped native CheckPinned.");
        AssertEqual(0, RuntimeUiPinningService.ReadTargetSet().Targets.Count, "Closing exposed the stale generation target.");
    }
    finally
    {
        InvokePrivate("OnCookingRefreshFinalized", new object?[] { null });
    }

    AssertThrows<InvalidOperationException>(
        () => PublishRareTarget(
            firstGeneration,
            listPinningEnabled: true,
            recipeVariantEnabled: false,
            cookerHighlightEnabled: false,
            seatHighlightEnabled: false,
            orderHighlightEnabled: false,
            recipeId: 93,
            beverageId: 94,
            ingredientIds: Array.Empty<int>(),
            extraIngredientIds: Array.Empty<int>(),
            cookerTypeId: 3,
            deskCode: -1,
            orderTraceId: "",
            targetRevision: "test-target"),
        "Closing accepted a UI target publication.");

    RuntimeUiPinningService.ClearTargetsForBusinessBoundary(firstGeneration, "test closing");
    AssertEqual(0, RuntimeUiPinningService.ReadTargetSet().Targets.Count, "Invalidation retained a UI target.");
    AssertFalse(RuntimeOrderHighlightService.LastEnabled, "Invalidation left the HUD order-highlight target enabled.");
    AssertFalse(RuntimeThrowDeliverOrderHighlightService.LastEnabled, "Invalidation left the throw-delivery order-highlight target enabled.");
    AssertEqual("", RuntimeOrderHighlightService.LastOrderTraceId, "Invalidation retained the HUD order trace.");
    AssertEqual("", RuntimeThrowDeliverOrderHighlightService.LastOrderTraceId, "Invalidation retained the throw-delivery order trace.");
    AssertEqual(-1, RuntimeOrderHighlightService.LastDeskCode, "Invalidation retained the HUD target desk.");
    AssertEqual(-1, RuntimeThrowDeliverOrderHighlightService.LastDeskCode, "Invalidation retained the throw-delivery target desk.");
    AssertEqual(firstGeneration, RuntimeOrderHighlightService.LastSessionGeneration, "Invalidation cleared the HUD target with the wrong generation.");
    AssertEqual(firstGeneration, RuntimeThrowDeliverOrderHighlightService.LastSessionGeneration, "Invalidation cleared the throw-delivery target with the wrong generation.");

    RuntimeNightBusinessLifecycle.ActivateNextGeneration();
    var secondGeneration = RuntimeNightBusinessLifecycle.Generation;
    AssertEqual(firstGeneration + 1, secondGeneration, "The next business session did not advance its generation.");
    AssertThrows<InvalidOperationException>(
        () => PublishRareTarget(
            firstGeneration,
            listPinningEnabled: true,
            recipeVariantEnabled: false,
            cookerHighlightEnabled: false,
            seatHighlightEnabled: false,
            orderHighlightEnabled: false,
            recipeId: 95,
            beverageId: 96,
            ingredientIds: Array.Empty<int>(),
            extraIngredientIds: Array.Empty<int>(),
            cookerTypeId: 3,
            deskCode: -1,
            orderTraceId: "",
            targetRevision: "test-target"),
        "A stale generation target was accepted by the next business session.");

    RunOnWorkerThread(() => PublishRareTarget(
        secondGeneration,
        listPinningEnabled: true,
        recipeVariantEnabled: false,
        cookerHighlightEnabled: false,
        seatHighlightEnabled: false,
        orderHighlightEnabled: false,
        recipeId: 97,
        beverageId: 98,
        ingredientIds: new[] { 99 },
        extraIngredientIds: Array.Empty<int>(),
        cookerTypeId: 3,
        deskCode: -1,
        orderTraceId: "",
        targetRevision: "test-target"));
    InvokePrivate("OnCookingRefreshStarted", new CookingSelectionPanelProbe());
    try
    {
        AssertTrue(InvokeCheckPinned(1, 97), "The next generation did not accept its fresh target.");
        AssertFalse(InvokeCheckPinned(1, 90), "The next generation reused the previous target.");
    }
    finally
    {
        InvokePrivate("OnCookingRefreshFinalized", new object?[] { null });
    }

}

static void VerifyIdenticalTargetPublicationIsIdempotent()
{
    const int recipeId = 71;
    const int beverageId = 72;
    const int cookerTypeId = 3;
    var ingredientIds = new[] { 11, 29 };

    void Publish(
        bool nextListPinningEnabled = true,
        bool nextRecipeVariantEnabled = false,
        bool nextCookerHighlightEnabled = true,
        bool nextSeatHighlightEnabled = false,
        bool nextOrderHighlightEnabled = false,
        int nextRecipeId = recipeId,
        int nextBeverageId = beverageId,
        int[]? nextIngredientIds = null,
        int[]? nextExtraIngredientIds = null,
        int nextCookerTypeId = cookerTypeId,
        int nextDeskCode = -1,
        string nextOrderTraceId = "",
        string nextTargetRevision = "test-target")
    {
        PublishRareTarget(
            RuntimeNightBusinessLifecycle.Generation,
            nextListPinningEnabled,
            nextRecipeVariantEnabled,
            nextCookerHighlightEnabled,
            nextSeatHighlightEnabled,
            nextOrderHighlightEnabled,
            nextRecipeId,
            nextBeverageId,
            nextIngredientIds ?? ingredientIds,
            nextExtraIngredientIds ?? Array.Empty<int>(),
            nextCookerTypeId,
            nextDeskCode,
            nextOrderTraceId,
            nextTargetRevision);
    }

    void AssertPublishes(Action publish, string fieldName)
    {
        var logCount = ManualLogSource.InformationCount;
        var highlightUpdateCount = RuntimeCookerHighlightService.UpdateCount;
        var seatUpdateCount = RuntimeSeatHighlightService.UpdateCount;
        var orderUpdateCount = RuntimeOrderHighlightService.UpdateCount;
        var throwDeliveryOrderUpdateCount = RuntimeThrowDeliverOrderHighlightService.UpdateCount;
        publish();
        AssertEqual(logCount + 1, ManualLogSource.InformationCount, $"Changing {fieldName} did not publish a target log.");
        AssertEqual(highlightUpdateCount + 1, RuntimeCookerHighlightService.UpdateCount, $"Changing {fieldName} did not update cooker highlighting.");
        AssertEqual(seatUpdateCount + 1, RuntimeSeatHighlightService.UpdateCount, $"Changing {fieldName} did not update seat highlighting.");
        AssertEqual(orderUpdateCount + 1, RuntimeOrderHighlightService.UpdateCount, $"Changing {fieldName} did not update order highlighting.");
        AssertEqual(throwDeliveryOrderUpdateCount + 1, RuntimeThrowDeliverOrderHighlightService.UpdateCount, $"Changing {fieldName} did not update throw-delivery order highlighting.");
        Publish();
    }

    Publish();
    var targetGeneration = RuntimeUiPinningService.ReadTargetSet().Generation;
    var initialLogCount = ManualLogSource.InformationCount;
    var initialHighlightUpdateCount = RuntimeCookerHighlightService.UpdateCount;
    var initialSeatUpdateCount = RuntimeSeatHighlightService.UpdateCount;
    var initialOrderUpdateCount = RuntimeOrderHighlightService.UpdateCount;
    var initialThrowDeliveryOrderUpdateCount = RuntimeThrowDeliverOrderHighlightService.UpdateCount;

    Publish(nextIngredientIds: new[] { 11, 29 });

    AssertEqual(targetGeneration, RuntimeUiPinningService.ReadTargetSet().Generation, "An identical target advanced its generation.");
    AssertEqual(initialLogCount, ManualLogSource.InformationCount, "An identical target wrote another information log.");
    AssertEqual(initialHighlightUpdateCount, RuntimeCookerHighlightService.UpdateCount, "An identical target updated cooker highlighting again.");
    AssertEqual(initialSeatUpdateCount, RuntimeSeatHighlightService.UpdateCount, "An identical target updated seat highlighting again.");
    AssertEqual(initialOrderUpdateCount, RuntimeOrderHighlightService.UpdateCount, "An identical target updated order highlighting again.");
    AssertEqual(initialThrowDeliveryOrderUpdateCount, RuntimeThrowDeliverOrderHighlightService.UpdateCount, "An identical target updated throw-delivery order highlighting again.");

    AssertPublishes(() => Publish(nextListPinningEnabled: false), "listPinningEnabled");
    AssertPublishes(() => Publish(nextCookerHighlightEnabled: false), "cookerHighlightEnabled");
    AssertPublishes(() => Publish(nextRecipeVariantEnabled: true), "recipeVariantEnabled");
    AssertPublishes(() => Publish(nextSeatHighlightEnabled: true, nextDeskCode: 2), "seatHighlightEnabled");
    AssertPublishes(
        () => Publish(nextOrderHighlightEnabled: true, nextDeskCode: 2, nextOrderTraceId: "R-0001"),
        "orderHighlightEnabled");
    AssertPublishes(() => Publish(nextRecipeId: recipeId + 1), "recipeId");
    AssertPublishes(() => Publish(nextBeverageId: beverageId + 1), "beverageId");
    AssertPublishes(() => Publish(nextIngredientIds: new[] { 11, 30 }), "ingredientIds");
    AssertPublishes(() => Publish(nextExtraIngredientIds: new[] { 30 }), "extraIngredientIds");
    AssertPublishes(() => Publish(nextCookerTypeId: cookerTypeId + 1), "cookerTypeId");
    AssertPublishes(() => Publish(nextSeatHighlightEnabled: true, nextDeskCode: 3), "deskCode");
    AssertPublishes(
        () => Publish(nextOrderHighlightEnabled: true, nextDeskCode: 2, nextOrderTraceId: "R-0002"),
        "orderTraceId");
    AssertPublishes(() => Publish(nextTargetRevision: "next-target"), "targetRevision");
}

static void VerifyOrderHighlightSurfaceIsolation()
{
    var businessGeneration = RuntimeNightBusinessLifecycle.Generation;

    void Publish(string traceId, string targetRevision)
    {
        PublishRareTarget(
            businessGeneration,
            listPinningEnabled: false,
            recipeVariantEnabled: false,
            cookerHighlightEnabled: false,
            seatHighlightEnabled: false,
            orderHighlightEnabled: true,
            recipeId: -1,
            beverageId: -1,
            ingredientIds: Array.Empty<int>(),
            extraIngredientIds: Array.Empty<int>(),
            cookerTypeId: -1,
            deskCode: 2,
            orderTraceId: traceId,
            targetRevision: targetRevision);
    }

    var warningCount = ManualLogSource.WarningCount;
    RuntimeOrderHighlightService.ThrowOnUpdate = true;
    try
    {
        Publish("R-7001", "surface-isolation-hud-failure");
    }
    finally
    {
        RuntimeOrderHighlightService.ThrowOnUpdate = false;
    }
    AssertEqual(warningCount + 1, ManualLogSource.WarningCount, "A HUD target failure was not diagnosed once.");
    AssertTrue(RuntimeThrowDeliverOrderHighlightService.LastEnabled, "A HUD target failure prevented the throw-delivery surface from updating.");
    AssertEqual("R-7001", RuntimeThrowDeliverOrderHighlightService.LastOrderTraceId, "A HUD target failure changed the throw-delivery trace.");
    AssertEqual(2, RuntimeThrowDeliverOrderHighlightService.LastDeskCode, "A HUD target failure changed the throw-delivery desk.");
    AssertContains(RuntimeUiPinningService.Status, "orderSurfaces=hud:retry-pending,throwDelivery:synchronized",
        "A failed HUD update was not retained as a narrow retry-pending surface.");
    var throwDeliveryUpdatesBeforeHudRetry = RuntimeThrowDeliverOrderHighlightService.UpdateCount;
    Publish("R-7001", "surface-isolation-hud-failure");
    AssertEqual("R-7001", RuntimeOrderHighlightService.LastOrderTraceId,
        "Retrying the same target did not recover the pending HUD surface.");
    AssertEqual(throwDeliveryUpdatesBeforeHudRetry, RuntimeThrowDeliverOrderHighlightService.UpdateCount,
        "Retrying the failed HUD surface redundantly republished the synchronized throw-delivery surface.");

    warningCount = ManualLogSource.WarningCount;
    RuntimeThrowDeliverOrderHighlightService.ThrowOnUpdate = true;
    try
    {
        Publish("R-7002", "surface-isolation-throw-delivery-failure");
    }
    finally
    {
        RuntimeThrowDeliverOrderHighlightService.ThrowOnUpdate = false;
    }
    AssertEqual(warningCount + 1, ManualLogSource.WarningCount, "A throw-delivery target failure was not diagnosed once.");
    AssertTrue(RuntimeOrderHighlightService.LastEnabled, "A throw-delivery target failure prevented the HUD surface from updating.");
    AssertEqual("R-7002", RuntimeOrderHighlightService.LastOrderTraceId, "A throw-delivery target failure changed the HUD trace.");
    AssertEqual(2, RuntimeOrderHighlightService.LastDeskCode, "A throw-delivery target failure changed the HUD desk.");
    AssertContains(RuntimeUiPinningService.Status, "orderSurfaces=hud:synchronized,throwDelivery:retry-pending",
        "A failed throw-delivery update was not retained as a narrow retry-pending surface.");

    var hudUpdatesBeforeThrowDeliveryRetry = RuntimeOrderHighlightService.UpdateCount;
    Publish("R-7002", "surface-isolation-throw-delivery-failure");
    AssertEqual(hudUpdatesBeforeThrowDeliveryRetry, RuntimeOrderHighlightService.UpdateCount,
        "Retrying the failed throw-delivery surface redundantly republished the synchronized HUD surface.");
    AssertEqual(RuntimeOrderHighlightService.LastEnabled, RuntimeThrowDeliverOrderHighlightService.LastEnabled, "Recovered order-highlight surfaces disagree on the shared switch.");
    AssertEqual(RuntimeOrderHighlightService.LastOrderTraceId, RuntimeThrowDeliverOrderHighlightService.LastOrderTraceId, "Recovered order-highlight surfaces disagree on the shared trace.");
    AssertEqual(RuntimeOrderHighlightService.LastDeskCode, RuntimeThrowDeliverOrderHighlightService.LastDeskCode, "Recovered order-highlight surfaces disagree on the shared desk.");
}

static void VerifyOrderHighlightRuntimeWiring()
{
    var pluginSource = File.ReadAllText("mods/bepinex/src/Plugin/MystiaStewardCompanionPlugin.cs");
    var controllerSource = File.ReadAllText("mods/bepinex/src/Ui/StewardOverlayController.cs");
    var pinningSource = File.ReadAllText("mods/bepinex/src/Save/RuntimeUiPinningService.cs");

    AssertContains(
        pluginSource,
        "RuntimeThrowDeliverOrderHighlightService.Attach(Log);",
        "Plugin startup did not attach the independent throw-delivery order-highlight surface.");
    AssertContains(
        controllerSource,
        "RuntimeThrowDeliverOrderHighlightService.Tick();",
        "Controller LateUpdate did not tick the throw-delivery order-highlight surface.");
    AssertContains(
        controllerSource,
        "() => RuntimeThrowDeliverOrderHighlightService.Dispose(\"controller disposed\")",
        "Controller disposal did not release the throw-delivery order-highlight surface.");
    AssertContains(
        controllerSource,
        "RunOrderHighlightDisposalNoThrow",
        "HUD and throw-delivery disposal are not isolated from one another.");
    AssertContains(
        pinningSource,
        "RuntimeTargetRecipeVariantService.Attach(log);",
        "Runtime UI pinning did not attach the formal recipe-variant service before list highlighting.");
    var panelRefreshStart = pinningSource.IndexOf(
        "private static void TryRefreshOpenPanel",
        StringComparison.Ordinal);
    var panelRefreshEnd = panelRefreshStart < 0
        ? -1
        : pinningSource.IndexOf(
            "private static bool IsCurrentOpenPanel",
            panelRefreshStart,
            StringComparison.Ordinal);
    AssertTrue(panelRefreshStart >= 0 && panelRefreshEnd > panelRefreshStart,
        "Open-panel refresh implementation is missing.");
    var panelRefreshSource = pinningSource[panelRefreshStart..panelRefreshEnd];
    AssertContains(
        panelRefreshSource,
        "TryAcquireTargetPublicationLease(target, out exactTargetLease)",
        "Open-panel refresh does not acquire the exact target-snapshot publication lease.");
    AssertContains(
        panelRefreshSource,
        "attempt.SurfaceRefresh.Refresh(attempt.Instance)",
        "Open-panel refresh does not execute the exact backing-data and logical-group binding.");
    AssertContains(
        controllerSource,
        "RuntimeTargetRecipeVariantService.RetireForShutdown(\"controller disposed\");",
        "Controller disposal still references the removed diagnostic recipe-variant service.");
    AssertTrue(
        pluginSource.IndexOf("RuntimeUiPinningService.Attach(Log);", StringComparison.Ordinal)
        < pluginSource.IndexOf("RuntimePinnedListHighlightService.Attach(Log);", StringComparison.Ordinal),
        "Plugin startup did not attach UI pinning and its variant bindings before list highlighting.");
}

static void VerifyPatchTargets()
{
    RuntimeUiPinningService.Attach(new ManualLogSource());
    RuntimePinnedListHighlightService.Attach(new ManualLogSource());

    var probeTypes = new HashSet<Type>
    {
        typeof(CookingSelectionPanelProbe),
        typeof(RunTimePlayerDataProbe),
        typeof(StoragePanelProbe),
    };
    var patchedProbeCount = Harmony.GetAllPatchedMethods().Count(method => method.DeclaringType != null && probeTypes.Contains(method.DeclaringType));
    AssertEqual(13, patchedProbeCount, "Runtime UI pinning and list highlighting should install exactly thirteen safe patches including direct ingredient refresh.");
    AssertPatch(
        typeof(CookingSelectionPanelProbe),
        nameof(CookingSelectionPanelProbe.UpdateAllVisual),
        prefix: "OnCookingRefreshStarted",
        postfix: null,
        finalizer: "OnCookingRefreshFinalized");
    AssertPatch(
        typeof(CookingSelectionPanelProbe),
        nameof(CookingSelectionPanelProbe.UpdateRecipeField),
        prefix: "OnCookingRefreshStarted",
        postfix: null,
        finalizer: "OnCookingRefreshFinalized");
    AssertPatch(
        typeof(CookingSelectionPanelProbe),
        nameof(CookingSelectionPanelProbe.UpdateIngField),
        prefix: "OnCookingRefreshStarted",
        postfix: null,
        finalizer: "OnCookingRefreshFinalized");
    var cookingSurfaceRefresh = (RuntimeUiListSurfaceRefreshBinding?)typeof(RuntimeUiPinningService)
        .GetField("_cookingSurfaceRefresh", BindingFlags.NonPublic | BindingFlags.Static)!
        .GetValue(null);
    var expectedIngredientRefreshMethod = typeof(CookingSelectionPanelProbe).GetMethod(
        nameof(CookingSelectionPanelProbe.UpdateIngField),
        BindingFlags.Public | BindingFlags.Instance,
        binder: null,
        types: Type.EmptyTypes,
        modifiers: null);
    var expectedRecipeRefreshMethod = typeof(CookingSelectionPanelProbe).GetMethod(
        nameof(CookingSelectionPanelProbe.UpdateRecipeField),
        BindingFlags.Public | BindingFlags.Instance,
        binder: null,
        types: Type.EmptyTypes,
        modifiers: null);
    AssertEqual(4, cookingSurfaceRefresh?.Steps.Count ?? -1,
        "Cooking surface did not bind exactly four refresh stages.");
    AssertEqual("ingredient-backing-data", cookingSurfaceRefresh?.Steps[0].Stage,
        "Cooking surface lost the ingredient backing-data stage identity.");
    AssertEqual(expectedIngredientRefreshMethod, cookingSurfaceRefresh?.Steps[0].RefreshMethod,
        "Cooking surface did not bind the exact zero-argument UpdateIngField MethodInfo.");
    AssertEqual<PropertyInfo?>(null, cookingSurfaceRefresh?.Steps[0].ReceiverProperty,
        "UpdateIngField was incorrectly bound through a logical-group receiver.");
    AssertEqual("recipe-backing-data", cookingSurfaceRefresh?.Steps[1].Stage,
        "Cooking surface lost the recipe backing-data stage identity.");
    AssertEqual(expectedRecipeRefreshMethod, cookingSurfaceRefresh?.Steps[1].RefreshMethod,
        "Cooking surface did not bind the exact zero-argument UpdateRecipeField MethodInfo.");
    AssertEqual("ingredient-visible-elements", cookingSurfaceRefresh?.Steps[2].Stage,
        "Cooking surface lost the ingredient visible-elements stage identity.");
    AssertEqual("m_StaticIngredientsGroup", cookingSurfaceRefresh?.Steps[2].ReceiverProperty?.Name,
        "Cooking surface did not bind the exact m_StaticIngredientsGroup property.");
    AssertEqual(nameof(CookingIngredientLogicalGroupProbe.UpdateElements), cookingSurfaceRefresh?.Steps[2].RefreshMethod.Name,
        "Cooking ingredient surface did not bind the exact UpdateElements/0 method.");
    AssertEqual("recipe-visible-elements", cookingSurfaceRefresh?.Steps[3].Stage,
        "Cooking surface lost the recipe visible-elements stage identity.");
    AssertEqual("m_StaticRecipeGroup", cookingSurfaceRefresh?.Steps[3].ReceiverProperty?.Name,
        "Cooking surface did not bind the exact m_StaticRecipeGroup property.");
    AssertEqual(nameof(CookingRecipeLogicalGroupProbe.UpdateElements), cookingSurfaceRefresh?.Steps[3].RefreshMethod.Name,
        "Cooking recipe surface did not bind the exact UpdateElements/0 method.");
    AssertFalse(cookingSurfaceRefresh?.Steps.Any(step => step.RefreshMethod.IsGenericMethod) == true,
        "Target publication cached a generic cooking refresh method.");
    AssertPatch(
        typeof(RunTimePlayerDataProbe),
        nameof(RunTimePlayerDataProbe.CheckPinned),
        prefix: "OnCheckPinned",
        postfix: null,
        finalizer: null);
    AssertPatch(
        typeof(StoragePanelProbe),
        nameof(StoragePanelProbe.UpdateBevField),
        prefix: "OnBeverageRefreshStarted",
        postfix: null,
        finalizer: "OnBeverageRefreshFinalized");
    var storageSurfaceRefresh = (RuntimeUiListSurfaceRefreshBinding?)typeof(RuntimeUiPinningService)
        .GetField("_storageSurfaceRefresh", BindingFlags.NonPublic | BindingFlags.Static)!
        .GetValue(null);
    AssertEqual(2, storageSurfaceRefresh?.Steps.Count ?? -1,
        "Storage surface did not bind exactly two refresh stages.");
    AssertEqual("beverage-backing-data", storageSurfaceRefresh?.Steps[0].Stage,
        "Storage surface lost the beverage backing-data stage identity.");
    AssertEqual(nameof(StoragePanelProbe.UpdateBevField), storageSurfaceRefresh?.Steps[0].RefreshMethod.Name,
        "Storage surface did not bind the exact UpdateBevField/0 method.");
    AssertEqual("beverage-visible-elements", storageSurfaceRefresh?.Steps[1].Stage,
        "Storage surface lost the beverage visible-elements stage identity.");
    AssertEqual("m_BevsGroup", storageSurfaceRefresh?.Steps[1].ReceiverProperty?.Name,
        "Storage surface did not bind the exact m_BevsGroup property.");
    AssertEqual("openType", storageSurfaceRefresh?.OpenTypeProperty?.Name,
        "Storage surface did not bind the exact openType property.");
    AssertEqual(nameof(BeverageLogicalGroupProbe.UpdateElements), storageSurfaceRefresh?.Steps[1].RefreshMethod.Name,
        "Storage surface did not bind the exact UpdateElements/0 method.");
    AssertPatch(
        typeof(CookingSelectionPanelProbe),
        nameof(CookingSelectionPanelProbe.OnPanelOpen),
        prefix: "BeforeCookingPanelOpen",
        postfix: "AfterCookingPanelOpen",
        finalizer: null);
    AssertPatch(
        typeof(CookingSelectionPanelProbe),
        nameof(CookingSelectionPanelProbe.OnPanelClose),
        prefix: "BeforeCookingPanelTeardown",
        postfix: null,
        finalizer: null);
    AssertNoPatch(
        typeof(CookingSelectionPanelProbe),
        nameof(CookingSelectionPanelProbe.OnPanelDestroyed),
        typeof(RuntimeUiPinningService),
        "Runtime UI pinning patched the cooking destroy method that shares an empty native alias.");
    AssertPatch(
        typeof(StoragePanelProbe),
        nameof(StoragePanelProbe.OnPanelOpen),
        prefix: "BeforeStoragePanelOpen",
        postfix: "AfterStoragePanelOpen",
        finalizer: null);
    AssertPatch(
        typeof(StoragePanelProbe),
        nameof(StoragePanelProbe.OnPanelClose),
        prefix: "BeforeStoragePanelTeardown",
        postfix: null,
        finalizer: null);
    AssertPatch(
        typeof(StoragePanelProbe),
        nameof(StoragePanelProbe.OnPanelDestroyed),
        prefix: "BeforeStoragePanelTeardown",
        postfix: null,
        finalizer: null);
    AssertPatch(
        typeof(CookingSelectionPanelProbe),
        nameof(CookingSelectionPanelProbe.OnPanelOpen),
        prefix: "BeforePanelOpen",
        postfix: null,
        finalizer: null,
        patchOwner: typeof(RuntimePinnedListHighlightService),
        prefixPriority: Priority.First);
    AssertPatch(
        typeof(StoragePanelProbe),
        nameof(StoragePanelProbe.OnPanelOpen),
        prefix: "BeforePanelOpen",
        postfix: null,
        finalizer: null,
        patchOwner: typeof(RuntimePinnedListHighlightService),
        prefixPriority: Priority.First);
    AssertPatch(
        typeof(CookingSelectionPanelProbe),
        nameof(CookingSelectionPanelProbe.OnRecipeElementEnabled),
        prefix: "BeforeItemEnabled",
        postfix: "AfterRecipeItemEnabled",
        finalizer: null,
        patchOwner: typeof(RuntimePinnedListHighlightService),
        prefixPriority: Priority.First,
        postfixPriority: Priority.Last,
        postfixAfter: "com.tyukki.mystia-steward-companion.runtime-target-recipe-variant");
    AssertPatch(
        typeof(CookingSelectionPanelProbe),
        nameof(CookingSelectionPanelProbe.OnIngElementEnabled),
        prefix: "BeforeItemEnabled",
        postfix: "AfterIngredientItemEnabled",
        finalizer: null,
        patchOwner: typeof(RuntimePinnedListHighlightService),
        prefixPriority: Priority.First,
        postfixPriority: Priority.Last);
    AssertPatch(
        typeof(StoragePanelProbe),
        nameof(StoragePanelProbe.OnElementEnabled),
        prefix: "BeforeItemEnabled",
        postfix: "AfterStorageItemEnabled",
        finalizer: null,
        patchOwner: typeof(RuntimePinnedListHighlightService),
        prefixPriority: Priority.First,
        postfixPriority: Priority.Last);
    AssertPatch(
        typeof(CookingSelectionPanelProbe),
        nameof(CookingSelectionPanelProbe.OnPanelClose),
        prefix: "BeforeCookingPanelTeardown",
        postfix: null,
        finalizer: null,
        patchOwner: typeof(RuntimePinnedListHighlightService),
        prefixPriority: Priority.First);
    AssertNoPatch(
        typeof(CookingSelectionPanelProbe),
        nameof(CookingSelectionPanelProbe.OnPanelDestroyed),
        typeof(RuntimePinnedListHighlightService),
        "Pinned-list highlighting patched the cooking destroy method that shares an empty native alias.");
    AssertPatch(
        typeof(StoragePanelProbe),
        nameof(StoragePanelProbe.OnPanelClose),
        prefix: "BeforeStoragePanelTeardown",
        postfix: null,
        finalizer: null,
        patchOwner: typeof(RuntimePinnedListHighlightService),
        prefixPriority: Priority.First);
    AssertPatch(
        typeof(StoragePanelProbe),
        nameof(StoragePanelProbe.OnPanelDestroyed),
        prefix: "BeforeStoragePanelTeardown",
        postfix: null,
        finalizer: null,
        patchOwner: typeof(RuntimePinnedListHighlightService),
        prefixPriority: Priority.First);
    AssertContains(RuntimeUiPinningService.Status, "checkPinnedPrefix:patched", "CheckPinned prefix patch status is missing.");
    AssertContains(RuntimeUiPinningService.Status, "cookingScope:patched", "Cooking scope patch status is missing.");
    AssertContains(RuntimeUiPinningService.Status, "beverageScope:patched", "Beverage scope patch status is missing.");
    AssertContains(RuntimeUiPinningService.Status, "cookingPanel:patched:2/2;destroy=unpatched-shared-native-alias", "Cooking panel safe lifecycle status is missing.");
    AssertContains(RuntimeUiPinningService.Status, "storagePanel:patched:3/3", "Storage panel lifecycle patch status is missing.");
    AssertPatch(typeof(UnityEngine.UI.Image), nameof(UnityEngine.UI.Image.OnPopulateMesh),
        prefix: null, postfix: "AfterNativeBackgroundMesh", finalizer: null,
        patchOwner: typeof(RuntimePinnedListHighlightService), postfixPriority: Priority.Last);
    AssertContains(RuntimePinnedListHighlightService.Status, "hooks=patched:9/9;cookingDestroy=unpatched-shared-native-alias", "Pinned-list safe hook status is missing.");
    AssertContains(RuntimeUiPinningService.Status, "listHighlight=hooks=patched:9/9;cookingDestroy=unpatched-shared-native-alias", "Pinned-list safe-hook diagnostics are missing from pinning status.");
    AssertContains(RuntimeUiPinningService.Status, "throwDeliveryOrder=", "Throw-delivery order-highlight diagnostics are missing from pinning status.");
}

static void VerifyScopedNativePinnedMatching()
{
    PublishRareTarget(
        RuntimeNightBusinessLifecycle.Generation,
        listPinningEnabled: true,
        recipeVariantEnabled: false,
        cookerHighlightEnabled: false,
        seatHighlightEnabled: false,
        orderHighlightEnabled: false,
        recipeId: 34,
        beverageId: 16,
        ingredientIds: new[] { 11, 29 },
        extraIngredientIds: Array.Empty<int>(),
        cookerTypeId: 3,
        deskCode: -1,
        orderTraceId: "",
        targetRevision: "test-target");

    AssertTrue(InvokeCheckPinnedPrefix(1, 34).RunOriginal, "CheckPinned was skipped outside a panel refresh scope.");
    AssertFalse(InvokeCheckPinned(1, 34), "CheckPinned was changed outside a panel refresh scope.");
    AssertTrue(InvokeCheckPinned(1, 99, originalResult: true), "The prefix changed an unrelated native pinned result.");

    InvokePrivate("OnCookingRefreshStarted", new CookingSelectionPanelProbe());
    try
    {
        var recipeCall = InvokeCheckPinnedPrefix(1, 34);
        AssertFalse(recipeCall.RunOriginal, "Recipe target did not skip the native CheckPinned method.");
        AssertTrue(recipeCall.Result, "Recipe.Id target was not forced true in cooking scope.");
        AssertTrue(InvokeCheckPinnedPrefix(1, 40).RunOriginal, "A non-target recipe skipped the native CheckPinned method.");
        AssertFalse(InvokeCheckPinned(1, 40), "Food ID incorrectly matched the Recipe.Id target.");
        AssertTrue(InvokeCheckPinned(0, 11), "Seafood ingredient target was not pinned.");
        AssertTrue(InvokeCheckPinned(4, 11), "Meat ingredient target was not pinned.");
        AssertTrue(InvokeCheckPinned(5, 29), "Vegetable ingredient target was not pinned.");
        AssertTrue(InvokeCheckPinned(6, 29), "Other ingredient target was not pinned.");
        AssertTrue(InvokeCheckPinnedPrefix(0, 8).RunOriginal, "A non-target ingredient skipped the native CheckPinned method.");
        AssertFalse(InvokeCheckPinned(0, 8), "Unrelated ingredient was pinned.");
        AssertTrue(InvokeCheckPinned(0, 8, originalResult: true), "A native ingredient favorite was not preserved.");
        AssertTrue(InvokeCheckPinnedPrefix(2, 16).RunOriginal, "The beverage target skipped CheckPinned inside cooking scope.");
        AssertFalse(InvokeCheckPinned(2, 16), "Beverage target leaked into cooking scope.");
        AssertTrue(InvokeCheckPinnedPrefix(3, 3).RunOriginal, "Cooker matching skipped the native CheckPinned method.");
        AssertFalse(InvokeCheckPinned(3, 3), "Cooker pinning was reintroduced alongside visual highlighting.");
        AssertTrue(InvokeCheckPinnedPrefix(1, -1).RunOriginal, "A negative ID skipped the native CheckPinned method.");
    }
    finally
    {
        InvokePrivate("OnCookingRefreshFinalized", new object?[] { null });
    }

    InvokePrivate("OnBeverageRefreshStarted", new StoragePanelProbe());
    try
    {
        AssertTrue(InvokeCheckPinned(2, 16), "Beverage target was not pinned in beverage scope.");
        AssertTrue(InvokeCheckPinnedPrefix(1, 34).RunOriginal, "The recipe target skipped CheckPinned inside beverage scope.");
        AssertFalse(InvokeCheckPinned(1, 34), "Recipe target leaked into beverage scope.");
        AssertTrue(InvokeCheckPinnedPrefix(2, 15).RunOriginal, "A non-target beverage skipped the native CheckPinned method.");
        AssertFalse(InvokeCheckPinned(2, 15), "Unrelated beverage was pinned.");
        AssertTrue(InvokeCheckPinned(2, 15, originalResult: true), "A native beverage favorite was not preserved.");
    }
    finally
    {
        InvokePrivate("OnBeverageRefreshFinalized", new object?[] { null });
    }

    AssertFalse(InvokeCheckPinned(1, 34), "Cooking scope leaked after its finalizer.");
    AssertFalse(InvokeCheckPinned(2, 16), "Beverage scope leaked after its finalizer.");
    AssertContains(RuntimeUiPinningService.Status, "forcedTotal=recipe:3, ingredients:12, beverage:3", "Scoped prefix diagnostics are incorrect.");
}

static void VerifyNestedScopeFinalizers()
{
    PublishRareTarget(
        RuntimeNightBusinessLifecycle.Generation,
        listPinningEnabled: true,
        recipeVariantEnabled: false,
        cookerHighlightEnabled: false,
        seatHighlightEnabled: false,
        orderHighlightEnabled: false,
        recipeId: 35,
        beverageId: 17,
        ingredientIds: new[] { 12 },
        extraIngredientIds: Array.Empty<int>(),
        cookerTypeId: 3,
        deskCode: -1,
        orderTraceId: "",
        targetRevision: "test-target");
    var expectedException = new InvalidOperationException("expected original failure");
    var nestedPanel = new CookingSelectionPanelProbe();
    InvokePrivate("OnCookingRefreshStarted", nestedPanel);
    InvokePrivate("OnCookingRefreshStarted", nestedPanel);
    var firstResult = InvokePrivate("OnCookingRefreshFinalized", expectedException);
    AssertEqual(expectedException, firstResult, "Cooking finalizer did not preserve the original exception.");
    AssertTrue(InvokeCheckPinned(1, 35), "Nested cooking scope ended too early.");
    var secondResult = InvokePrivate("OnCookingRefreshFinalized", expectedException);
    AssertEqual(expectedException, secondResult, "Nested cooking finalizer did not preserve the original exception.");
    AssertFalse(InvokeCheckPinned(1, 35), "Nested cooking scope leaked after both finalizers.");
    AssertContains(RuntimeUiPinningService.Status, "scopeImbalance=0", "Scope cleanup reported an unexpected imbalance.");
}

static void VerifyTargetUpdatePreservesForceTotals()
{
    PublishRareTarget(
        RuntimeNightBusinessLifecycle.Generation,
        listPinningEnabled: true,
        recipeVariantEnabled: false,
        cookerHighlightEnabled: false,
        seatHighlightEnabled: false,
        orderHighlightEnabled: false,
        recipeId: 34,
        beverageId: 16,
        ingredientIds: new[] { 11 },
        extraIngredientIds: Array.Empty<int>(),
        cookerTypeId: 3,
        deskCode: -1,
        orderTraceId: "",
        targetRevision: "test-target");
    InvokePrivate("OnCookingRefreshStarted", new CookingSelectionPanelProbe());
    try
    {
        AssertTrue(InvokeCheckPinned(1, 34), "Counter precondition did not match the recipe target.");
    }
    finally
    {
        InvokePrivate("OnCookingRefreshFinalized", new object?[] { null });
    }

    var forcesBeforeUpdate = ReadForcedTotal("recipe");
    PublishRareTarget(
        RuntimeNightBusinessLifecycle.Generation,
        listPinningEnabled: true,
        recipeVariantEnabled: false,
        cookerHighlightEnabled: false,
        seatHighlightEnabled: false,
        orderHighlightEnabled: false,
        recipeId: 35,
        beverageId: 17,
        ingredientIds: new[] { 12 },
        extraIngredientIds: Array.Empty<int>(),
        cookerTypeId: 3,
        deskCode: -1,
        orderTraceId: "",
        targetRevision: "test-target");
    AssertEqual(forcesBeforeUpdate, ReadForcedTotal("recipe"), "Target update reset the process force total.");
}

// 在独立工作线程建立作用域，检查线程局部状态不会覆盖调用线程的选择上下文。
static void VerifyThreadLocalScopeIsolation()
{
    PublishRareTarget(
        RuntimeNightBusinessLifecycle.Generation,
        listPinningEnabled: true,
        recipeVariantEnabled: false,
        cookerHighlightEnabled: false,
        seatHighlightEnabled: false,
        orderHighlightEnabled: false,
        recipeId: 34,
        beverageId: 16,
        ingredientIds: new[] { 11 },
        extraIngredientIds: Array.Empty<int>(),
        cookerTypeId: 3,
        deskCode: -1,
        orderTraceId: "",
        targetRevision: "test-target");

    using var scopeEntered = new ManualResetEventSlim();
    using var releaseScope = new ManualResetEventSlim();
    Exception? workerException = null;
    var workerMatched = false;
    var worker = new Thread(() =>
    {
        try
        {
            InvokePrivate("OnCookingRefreshStarted", new CookingSelectionPanelProbe());
            scopeEntered.Set();
            releaseScope.Wait();
            workerMatched = InvokeCheckPinned(1, 34);
        }
        catch (Exception ex)
        {
            workerException = ex;
        }
        finally
        {
            InvokePrivate("OnCookingRefreshFinalized", new object?[] { null });
        }
    });

    worker.Start();
    try
    {
        AssertTrue(scopeEntered.Wait(TimeSpan.FromSeconds(5)), "Worker cooking scope did not start in time.");
        AssertFalse(InvokeCheckPinned(1, 34), "A cooking scope leaked from the worker thread into the caller thread.");
    }
    finally
    {
        releaseScope.Set();
    }

    AssertTrue(worker.Join(TimeSpan.FromSeconds(5)), "Worker cooking scope did not finish in time.");
    if (workerException is not null)
    {
        throw new InvalidOperationException("Worker cooking scope failed.", workerException);
    }

    AssertTrue(workerMatched, "Worker cooking scope did not match its recipe target.");
}

// 刷新作用域开始后发布新目标，当前刷新仍应使用原快照；下一作用域才切换到新版本。
// 这样同一次原生列表遍历不会混合两代目标的置顶判断。
static void VerifyScopePinsOneTargetSnapshot()
{
    PublishRareTarget(
        RuntimeNightBusinessLifecycle.Generation,
        listPinningEnabled: true,
        recipeVariantEnabled: false,
        cookerHighlightEnabled: false,
        seatHighlightEnabled: false,
        orderHighlightEnabled: false,
        recipeId: 34,
        beverageId: 16,
        ingredientIds: new[] { 11 },
        extraIngredientIds: Array.Empty<int>(),
        cookerTypeId: 3,
        deskCode: -1,
        orderTraceId: "",
        targetRevision: "test-target");
    InvokePrivate("OnCookingRefreshStarted", new CookingSelectionPanelProbe());
    try
    {
        PublishRareTarget(
            RuntimeNightBusinessLifecycle.Generation,
            listPinningEnabled: true,
            recipeVariantEnabled: false,
            cookerHighlightEnabled: false,
            seatHighlightEnabled: false,
            orderHighlightEnabled: false,
            recipeId: 35,
            beverageId: 17,
            ingredientIds: new[] { 12 },
            extraIngredientIds: Array.Empty<int>(),
            cookerTypeId: 3,
            deskCode: -1,
            orderTraceId: "",
            targetRevision: "test-target");
        AssertTrue(InvokeCheckPinned(1, 34), "An active cooking refresh did not retain its initial target snapshot.");
        AssertFalse(InvokeCheckPinned(1, 35), "An active cooking refresh mixed in a newly published target.");
    }
    finally
    {
        InvokePrivate("OnCookingRefreshFinalized", new object?[] { null });
    }

    InvokePrivate("OnCookingRefreshStarted", new CookingSelectionPanelProbe());
    try
    {
        AssertTrue(InvokeCheckPinned(1, 35), "A new cooking refresh did not capture the latest target.");
    }
    finally
    {
        InvokePrivate("OnCookingRefreshFinalized", new object?[] { null });
    }

}

static void VerifyPinningAndHighlightRemainIndependent()
{
    PublishRareTarget(
        RuntimeNightBusinessLifecycle.Generation,
        listPinningEnabled: false,
        recipeVariantEnabled: false,
        cookerHighlightEnabled: true,
        seatHighlightEnabled: false,
        orderHighlightEnabled: false,
        recipeId: 34,
        beverageId: 16,
        ingredientIds: new[] { 11 },
        extraIngredientIds: Array.Empty<int>(),
        cookerTypeId: 3,
        deskCode: -1,
        orderTraceId: "",
        targetRevision: "test-target");
    AssertTrue(RuntimeCookerHighlightService.LastEnabled, "Highlight-only target did not enable cooker highlighting.");
    InvokePrivate("OnCookingRefreshStarted", new CookingSelectionPanelProbe());
    try
    {
        AssertTrue(InvokeCheckPinnedPrefix(1, 34).RunOriginal, "Highlight-only mode skipped the native CheckPinned method.");
        AssertFalse(InvokeCheckPinned(1, 34), "Highlight-only target unexpectedly enabled list pinning.");
    }
    finally
    {
        InvokePrivate("OnCookingRefreshFinalized", new object?[] { null });
    }

    AssertThrows<ArgumentException>(
        () => PublishRareTarget(
            RuntimeNightBusinessLifecycle.Generation,
            listPinningEnabled: true,
            recipeVariantEnabled: false,
            cookerHighlightEnabled: false,
            seatHighlightEnabled: false,
            orderHighlightEnabled: true,
            recipeId: 34,
            beverageId: 16,
            ingredientIds: new[] { 11 },
            extraIngredientIds: Array.Empty<int>(),
            cookerTypeId: 3,
            deskCode: 0,
            orderTraceId: "invalid-trace",
            targetRevision: "test-invalid-order-trace"),
        "Invalid exact order identity did not reject the whole atomic target publication.");
    AssertFalse(RuntimeOrderHighlightService.LastEnabled, "Invalid order trace enabled order highlighting.");
    AssertEqual("", RuntimeOrderHighlightService.LastOrderTraceId, "Invalid order trace reached the order highlighter.");
    AssertFalse(RuntimeThrowDeliverOrderHighlightService.LastEnabled, "Invalid order trace enabled throw-delivery order highlighting.");
    AssertEqual("", RuntimeThrowDeliverOrderHighlightService.LastOrderTraceId, "Invalid order trace reached the throw-delivery order highlighter.");

    PublishRareTarget(
        RuntimeNightBusinessLifecycle.Generation,
        listPinningEnabled: true,
        recipeVariantEnabled: false,
        cookerHighlightEnabled: false,
        seatHighlightEnabled: false,
        orderHighlightEnabled: false,
        recipeId: 34,
        beverageId: 16,
        ingredientIds: new[] { 11 },
        extraIngredientIds: Array.Empty<int>(),
        cookerTypeId: 3,
        deskCode: -1,
        orderTraceId: "",
        targetRevision: "test-target");
    AssertFalse(RuntimeCookerHighlightService.LastEnabled, "Pinning-only target unexpectedly enabled cooker highlighting.");
    InvokePrivate("OnCookingRefreshStarted", new CookingSelectionPanelProbe());
    try
    {
        AssertTrue(InvokeCheckPinned(1, 34), "Pinning-only target did not enable native pinned matching.");
    }
    finally
    {
        InvokePrivate("OnCookingRefreshFinalized", new object?[] { null });
    }

    PublishRareTarget(
        RuntimeNightBusinessLifecycle.Generation,
        listPinningEnabled: false,
        recipeVariantEnabled: false,
        cookerHighlightEnabled: false,
        seatHighlightEnabled: true,
        orderHighlightEnabled: false,
        recipeId: 34,
        beverageId: 16,
        ingredientIds: new[] { 11 },
        extraIngredientIds: new[] { 12 },
        cookerTypeId: 3,
        deskCode: 0,
        orderTraceId: "",
        targetRevision: "test-target");
    AssertFalse(RuntimeCookerHighlightService.LastEnabled, "Seat-only target unexpectedly enabled cooker highlighting.");
    AssertTrue(RuntimeSeatHighlightService.LastEnabled, "Seat-only target did not enable seat highlighting.");
    AssertEqual(0, RuntimeSeatHighlightService.LastDeskCode, "Seat-only target changed the zero-based desk code.");
    var seatOnlyPinningTarget = RuntimeUiPinningService.ReadTargetSet();
    AssertTrue(seatOnlyPinningTarget.TryGetTarget(seatOnlyPinningTarget.Targets.Single(target => target.Kind == RuntimeUiTargetKind.Rare).IdentityKey, out var seatOnlyTarget), "Seat-only publication lost its rare target.");
    AssertFalse(seatOnlyTarget.ListPinningEnabled, "Seat-only target unexpectedly enabled list pinning.");
    AssertFalse(seatOnlyTarget.RecipeVariantEnabled, "Seat-only target unexpectedly enabled recipe variants.");
    InvokePrivate("OnCookingRefreshStarted", new CookingSelectionPanelProbe());
    try
    {
        AssertFalse(InvokeCheckPinned(1, 34), "Seat-only target changed native recipe pinning.");
    }
    finally
    {
        InvokePrivate("OnCookingRefreshFinalized", new object?[] { null });
    }

    PublishRareTarget(
        RuntimeNightBusinessLifecycle.Generation,
        listPinningEnabled: false,
        recipeVariantEnabled: false,
        cookerHighlightEnabled: false,
        seatHighlightEnabled: false,
        orderHighlightEnabled: true,
        recipeId: 34,
        beverageId: 16,
        ingredientIds: new[] { 11 },
        extraIngredientIds: Array.Empty<int>(),
        cookerTypeId: 3,
        deskCode: 0,
        orderTraceId: "R-0001",
        targetRevision: "test-order-target");
    AssertFalse(RuntimeCookerHighlightService.LastEnabled, "Order-only target unexpectedly enabled cooker highlighting.");
    AssertFalse(RuntimeSeatHighlightService.LastEnabled, "Order-only target unexpectedly enabled seat highlighting.");
    AssertTrue(RuntimeOrderHighlightService.LastEnabled, "Order-only target did not enable order highlighting.");
    AssertEqual("R-0001", RuntimeOrderHighlightService.LastOrderTraceId, "Order-only target changed the order trace id.");
    AssertEqual(0, RuntimeOrderHighlightService.LastDeskCode, "Order-only target changed the zero-based desk code.");
    AssertTrue(RuntimeThrowDeliverOrderHighlightService.LastEnabled, "Order-only target did not enable throw-delivery order highlighting.");
    AssertEqual("R-0001", RuntimeThrowDeliverOrderHighlightService.LastOrderTraceId, "Order-only target changed the throw-delivery order trace id.");
    AssertEqual(0, RuntimeThrowDeliverOrderHighlightService.LastDeskCode, "Order-only target changed the throw-delivery zero-based desk code.");
    AssertEqual(RuntimeOrderHighlightService.LastSessionGeneration, RuntimeThrowDeliverOrderHighlightService.LastSessionGeneration, "Order-highlight surfaces received different business generations.");
    var orderOnlyPinningTarget = RuntimeUiPinningService.ReadTargetSet();
    AssertTrue(orderOnlyPinningTarget.TryGetTarget(orderOnlyPinningTarget.Targets.Single(target => target.Kind == RuntimeUiTargetKind.Rare).IdentityKey, out var orderOnlyTarget), "Order-only publication lost its rare target.");
    AssertFalse(orderOnlyTarget.ListPinningEnabled, "Order-only target unexpectedly enabled list pinning.");
    AssertFalse(orderOnlyTarget.RecipeVariantEnabled, "Order-only target unexpectedly enabled recipe variants.");
}

static void VerifyDangerousListHooksAreAbsent()
{
    foreach (var methodName in new[]
    {
        "OnRecipeFieldUpdated",
        "OnIngredientsClassified",
        "OnBeverageFieldUpdated",
        "ReorderList",
        "ReadMember",
    })
    {
        var method = typeof(RuntimeUiPinningService).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
        AssertEqual<MethodInfo?>(null, method, $"Dangerous direct list path remains: {methodName}.");
    }
}

static void VerifyManagedHarmonyReturnPropagation()
{
    PublishRareTarget(
        RuntimeNightBusinessLifecycle.Generation,
        listPinningEnabled: true,
        recipeVariantEnabled: false,
        cookerHighlightEnabled: false,
        seatHighlightEnabled: false,
        orderHighlightEnabled: false,
        recipeId: 34,
        beverageId: 16,
        ingredientIds: new[] { 11 },
        extraIngredientIds: Array.Empty<int>(),
        cookerTypeId: 3,
        deskCode: -1,
        orderTraceId: "",
        targetRevision: "test-target");

    try
    {
        RunTimePlayerDataProbe.Reset(nativeResult: false);
        AssertFalse(RunTimePlayerDataProbe.CheckPinned(PlayerSaveFileDefaultPropProbe.Recipes, 34), "The managed Harmony prefix leaked outside a refresh scope.");
        AssertEqual(1, RunTimePlayerDataProbe.NativeCallCount, "The native CheckPinned probe did not run outside a refresh scope.");

        CookingSelectionPanelProbe.RecipeRefreshAction = () => RunTimePlayerDataProbe.CheckPinned(PlayerSaveFileDefaultPropProbe.Recipes, 34);
        new CookingSelectionPanelProbe().UpdateAllVisual();
        AssertTrue(CookingSelectionPanelProbe.LastRecipeResult == true, "The managed Harmony wrapper did not propagate the forced recipe result.");
        AssertEqual(1, RunTimePlayerDataProbe.NativeCallCount, "The managed Harmony wrapper did not skip the target's original method.");
        AssertFalse(RunTimePlayerDataProbe.CheckPinned(PlayerSaveFileDefaultPropProbe.Recipes, 34), "Cooking scope leaked after a normal managed wrapper return.");
        AssertEqual(2, RunTimePlayerDataProbe.NativeCallCount, "The native CheckPinned probe did not resume after a normal wrapper return.");

        RunTimePlayerDataProbe.NativeResult = true;
        CookingSelectionPanelProbe.RecipeRefreshAction = () => RunTimePlayerDataProbe.CheckPinned(PlayerSaveFileDefaultPropProbe.Recipes, 99);
        new CookingSelectionPanelProbe().UpdateAllVisual();
        AssertTrue(CookingSelectionPanelProbe.LastRecipeResult == true, "The managed Harmony wrapper changed a native favorite result.");
        AssertEqual(3, RunTimePlayerDataProbe.NativeCallCount, "A non-target native favorite did not execute the original method.");

        RunTimePlayerDataProbe.Reset(nativeResult: false);
        var expectedException = new InvalidOperationException("managed wrapper failure");
        CookingSelectionPanelProbe.RecipeRefreshAction = () => throw expectedException;
        Exception? observedException = null;
        try
        {
            new CookingSelectionPanelProbe().UpdateAllVisual();
        }
        catch (Exception ex)
        {
            observedException = ex;
        }

        AssertEqual<Exception?>(expectedException, observedException, "The managed Harmony finalizer did not preserve the original exception.");
        AssertFalse(RunTimePlayerDataProbe.CheckPinned(PlayerSaveFileDefaultPropProbe.Recipes, 34), "Cooking scope leaked after an exceptional managed wrapper return.");
        AssertEqual(1, RunTimePlayerDataProbe.NativeCallCount, "The native CheckPinned probe did not resume after an exceptional wrapper return.");

        RunTimePlayerDataProbe.Reset(nativeResult: false);
        StoragePanelProbe.RefreshAction = () => RunTimePlayerDataProbe.CheckPinned(PlayerSaveFileDefaultPropProbe.Beverages, 16);
        new StoragePanelProbe().UpdateBevField();
        AssertTrue(StoragePanelProbe.LastResult == true, "The managed Harmony wrapper did not propagate the forced beverage result.");
        AssertEqual(0, RunTimePlayerDataProbe.NativeCallCount, "The managed Harmony wrapper did not skip the beverage target's original method.");
        AssertFalse(RunTimePlayerDataProbe.CheckPinned(PlayerSaveFileDefaultPropProbe.Beverages, 16), "Beverage scope leaked after a normal managed wrapper return.");
        AssertEqual(1, RunTimePlayerDataProbe.NativeCallCount, "The native CheckPinned probe did not resume after beverage wrapper return.");
    }
    finally
    {
        CookingSelectionPanelProbe.RecipeRefreshAction = null;
        StoragePanelProbe.RefreshAction = null;
    }
}

static void VerifyManagedPinnedListHighlighting()
{
    var mainThreadId = Environment.CurrentManagedThreadId;
    var cookingPanel = new CookingSelectionPanelProbe();
    var storagePanel = new StoragePanelProbe();
    var recipeBase = new Color(0.72f, 0.74f, 0.78f, 0.5f);
    var recipeRebound = new Color(0.24f, 0.46f, 0.68f, 0.4f);
    var ingredientBase = new Color(0.42f, 0.58f, 0.76f, 0.35f);
    var storageBase = new Color(0.36f, 0.52f, 0.7f, 0.6f);
    CookingSelectionPanelProbe.ApplyIngredientBoundColor = false;
    CookingSelectionPanelProbe.RecipeBoundColor = recipeBase;
    StoragePanelProbe.BoundColor = storageBase;
    Time.realtimeSinceStartup = 0.25f;

    PublishRareTarget(
        RuntimeNightBusinessLifecycle.Generation,
        listPinningEnabled: true,
        recipeVariantEnabled: false,
        cookerHighlightEnabled: false,
        seatHighlightEnabled: false,
        orderHighlightEnabled: false,
        recipeId: 34,
        beverageId: 16,
        ingredientIds: new[] { 11 },
        extraIngredientIds: Array.Empty<int>(),
        cookerTypeId: 3,
        deskCode: -1,
        orderTraceId: "",
        targetRevision: "test-target");

    try
    {
        var recipeButton = new UIButtonSimpleProbe(new Color(1f, 1f, 1f, 1f));
        cookingPanel.OnRecipeElementEnabled(new RecipeProbe(34), recipeButton.Cluster, recipeButton);
        AssertColor(recipeBase, recipeButton.image.get_color(), "Recipe binding did not preserve the game's base color.");
        RuntimePinnedListHighlightService.Tick();
        AssertHighlighted(recipeBase, recipeButton.image, "Recipe target was not highlighted.");

        CookingSelectionPanelProbe.RecipeBoundColor = recipeRebound;
        cookingPanel.OnRecipeElementEnabled(new RecipeProbe(40), recipeButton.Cluster, recipeButton);
        AssertUnhighlighted(recipeRebound, recipeButton.image, "A pooled recipe button kept the previous target highlight.");
        RuntimePinnedListHighlightService.Tick();
        AssertUnhighlighted(recipeRebound, recipeButton.image, "A non-target recipe was highlighted.");

        var ingredientButton = new UIButtonSimpleProbe(ingredientBase);
        cookingPanel.OnIngElementEnabled(
            new KeyValuePair<IngredientProbe, int>(new IngredientProbe(11), 2),
            ingredientButton.Cluster,
            ingredientButton);
        RuntimePinnedListHighlightService.Tick();
        AssertHighlighted(ingredientBase, ingredientButton.image, "Ingredient target was not highlighted.");
        cookingPanel.OnIngElementEnabled(
            new KeyValuePair<IngredientProbe, int>(new IngredientProbe(12), 2),
            ingredientButton.Cluster,
            ingredientButton);
        AssertUnhighlighted(ingredientBase, ingredientButton.image, "A pooled ingredient button kept the previous target highlight.");

        var foodButton = new UIButtonSimpleProbe(storageBase);
        storagePanel.OnElementEnabled(
            new KeyValuePair<SellableProbe, int>(new SellableProbe(16, SellableTypeProbe.Food), 1),
            foodButton.Cluster,
            foodButton);
        RuntimePinnedListHighlightService.Tick();
        AssertUnhighlighted(storageBase, foodButton.image, "A food item with the beverage target ID was highlighted.");

        var beverageButton = new UIButtonSimpleProbe(storageBase);
        storagePanel.OnElementEnabled(
            new KeyValuePair<SellableProbe, int>(new SellableProbe(16, SellableTypeProbe.Beverage), 1),
            beverageButton.Cluster,
            beverageButton);
        RuntimePinnedListHighlightService.Tick();
        AssertHighlighted(storageBase, beverageButton.image, "Beverage target was not highlighted.");
        storagePanel.OnPanelClose();
        AssertUnhighlighted(storageBase, beverageButton.image, "Storage panel close did not restore the beverage color.");

        CookingSelectionPanelProbe.RecipeBoundColor = recipeBase;
        cookingPanel.OnRecipeElementEnabled(new RecipeProbe(34), recipeButton.Cluster, recipeButton);
        RuntimePinnedListHighlightService.Tick();
        recipeButton.image.RebuildIfDirty();
        var setterCountBeforeWorkerUpdate = recipeButton.image.SetterCount;
        RunOnWorkerThread(() => PublishRareTarget(
            RuntimeNightBusinessLifecycle.Generation,
            listPinningEnabled: true,
            recipeVariantEnabled: false,
            cookerHighlightEnabled: false,
            seatHighlightEnabled: false,
            orderHighlightEnabled: false,
            recipeId: 35,
            beverageId: 17,
            ingredientIds: new[] { 12 },
            extraIngredientIds: Array.Empty<int>(),
            cookerTypeId: 3,
            deskCode: -1,
            orderTraceId: "",
            targetRevision: "test-target"));
        AssertEqual(setterCountBeforeWorkerUpdate, recipeButton.image.SetterCount, "Background target publication wrote a Unity image color.");
        AssertHighlighted(recipeBase, recipeButton.image, "Target publication touched Unity color off the main visual tick.");
        RuntimePinnedListHighlightService.Tick();
        AssertUnhighlighted(recipeBase, recipeButton.image, "Target change did not restore the old recipe color.");
        AssertTrue(recipeButton.image.SetterThreadIds.All(threadId => threadId == mainThreadId), "A recipe image setter ran outside the Unity main thread.");

        cookingPanel.OnRecipeElementEnabled(new RecipeProbe(35), recipeButton.Cluster, recipeButton);
        RuntimePinnedListHighlightService.Tick();
        AssertHighlighted(recipeBase, recipeButton.image, "New recipe target was not highlighted after rebinding.");
        cookingPanel.OnPanelClose();
        AssertUnhighlighted(recipeBase, recipeButton.image, "Cooking panel close did not restore the recipe color.");

        cookingPanel.OnRecipeElementEnabled(new RecipeProbe(35), recipeButton.Cluster, recipeButton);
        RuntimePinnedListHighlightService.Tick();
        RuntimePinnedListHighlightService.Suspend("test scene exit");
        AssertUnhighlighted(recipeBase, recipeButton.image, "Scene suspension did not restore the recipe color.");
        AssertContains(RuntimePinnedListHighlightService.Status, "state=suspended: test scene exit", "Scene suspension state was not retained.");

        PublishRareTarget(
            RuntimeNightBusinessLifecycle.Generation,
            listPinningEnabled: true,
            recipeVariantEnabled: false,
            cookerHighlightEnabled: false,
            seatHighlightEnabled: false,
            orderHighlightEnabled: false,
            recipeId: 35,
            beverageId: 17,
            ingredientIds: new[] { 12 },
            extraIngredientIds: Array.Empty<int>(),
            cookerTypeId: 3,
            deskCode: -1,
            orderTraceId: "",
            targetRevision: "test-target");
        AssertContains(RuntimePinnedListHighlightService.Status, "state=suspended: test scene exit", "Publishing the same target resumed a suspended scene.");
        cookingPanel.OnRecipeElementEnabled(new RecipeProbe(35), recipeButton.Cluster, recipeButton);
        var setterCountAfterSuspendedBinding = recipeButton.image.SetterCount;
        RuntimePinnedListHighlightService.Tick();
        AssertEqual(setterCountAfterSuspendedBinding, recipeButton.image.SetterCount, "A late element callback was highlighted while the scene was suspended.");
        AssertUnhighlighted(recipeBase, recipeButton.image, "A late element callback kept a highlight while the scene was suspended.");
        AssertContains(RuntimePinnedListHighlightService.Status, "tracked=recipe:0", "A late element callback was tracked while the scene was suspended.");

        RunOnWorkerThread(() => PublishRareTarget(
            RuntimeNightBusinessLifecycle.Generation,
            listPinningEnabled: true,
            recipeVariantEnabled: false,
            cookerHighlightEnabled: false,
            seatHighlightEnabled: false,
            orderHighlightEnabled: false,
            recipeId: 36,
            beverageId: 18,
            ingredientIds: new[] { 13 },
            extraIngredientIds: Array.Empty<int>(),
            cookerTypeId: 3,
            deskCode: -1,
            orderTraceId: "",
            targetRevision: "test-target"));
        AssertContains(RuntimePinnedListHighlightService.Status, "state=suspended: test scene exit", "Publishing a changed target resumed a suspended scene.");
        CookingSelectionPanelProbe.OpenAction = () => cookingPanel.OnRecipeElementEnabled(new RecipeProbe(36), recipeButton.Cluster, recipeButton);
        cookingPanel.OnPanelOpen();
        RuntimePinnedListHighlightService.Tick();
        AssertHighlighted(recipeBase, recipeButton.image, "Cooking panel open did not resume highlighting before its first element binding.");
        CookingSelectionPanelProbe.OpenAction = null;

        RuntimePinnedListHighlightService.Suspend("test storage reopen");
        AssertUnhighlighted(recipeBase, recipeButton.image, "Second scene suspension did not restore the recipe color.");
        var reopenedBeverageButton = new UIButtonSimpleProbe(storageBase);
        StoragePanelProbe.OpenAction = () => storagePanel.OnElementEnabled(
            new KeyValuePair<SellableProbe, int>(new SellableProbe(18, SellableTypeProbe.Beverage), 1),
            reopenedBeverageButton.Cluster,
            reopenedBeverageButton);
        storagePanel.OnPanelOpen();
        RuntimePinnedListHighlightService.Tick();
        AssertHighlighted(storageBase, reopenedBeverageButton.image, "Storage panel open did not resume highlighting before its first element binding.");
        storagePanel.OnPanelClose();
        AssertUnhighlighted(storageBase, reopenedBeverageButton.image, "Reopened storage panel did not restore its beverage color on close.");
        StoragePanelProbe.OpenAction = null;

        cookingPanel.OnPanelOpen();
        CookingSelectionPanelProbe.RecipeBoundColor = recipeBase;
        cookingPanel.OnRecipeElementEnabled(new RecipeProbe(36), recipeButton.Cluster, recipeButton);
        RuntimePinnedListHighlightService.Tick();
        recipeButton.image.RebuildIfDirty();
        var setterCountBeforeDisable = recipeButton.image.SetterCount;
        PublishRareTarget(
            RuntimeNightBusinessLifecycle.Generation,
            listPinningEnabled: false,
            recipeVariantEnabled: false,
            cookerHighlightEnabled: true,
            seatHighlightEnabled: false,
            orderHighlightEnabled: false,
            recipeId: 36,
            beverageId: 18,
            ingredientIds: new[] { 13 },
            extraIngredientIds: Array.Empty<int>(),
            cookerTypeId: 3,
            deskCode: -1,
            orderTraceId: "",
            targetRevision: "test-target");
        AssertEqual(setterCountBeforeDisable, recipeButton.image.SetterCount, "Disabling list pinning wrote a Unity image color off the main visual tick.");
        AssertHighlighted(recipeBase, recipeButton.image, "Disabling list pinning touched Unity color before LateUpdate.");
        RuntimePinnedListHighlightService.Tick();
        AssertUnhighlighted(recipeBase, recipeButton.image, "Disabling list pinning did not restore the recipe color.");
        AssertTrue(RuntimeCookerHighlightService.LastEnabled, "Cooker-only highlighting was disabled alongside list highlighting.");

        PublishRareTarget(
            RuntimeNightBusinessLifecycle.Generation,
            listPinningEnabled: true,
            recipeVariantEnabled: false,
            cookerHighlightEnabled: false,
            seatHighlightEnabled: false,
            orderHighlightEnabled: false,
            recipeId: 37,
            beverageId: 19,
            ingredientIds: new[] { 14 },
            extraIngredientIds: Array.Empty<int>(),
            cookerTypeId: 3,
            deskCode: -1,
            orderTraceId: "",
            targetRevision: "test-target");
        var setterRaceButton = new UIButtonSimpleProbe(recipeBase);
        cookingPanel.OnRecipeElementEnabled(new RecipeProbe(37), setterRaceButton.Cluster, setterRaceButton);
        using var setterEntered = new ManualResetEventSlim();
        using var setterRelease = new ManualResetEventSlim();
        UnityEngine.UI.VertexHelper.BlockNextWrite(setterEntered, setterRelease);
        var visualErrorsBeforeRace = ReadListHighlightCounter("visualErrors");
        Exception? setterRaceFailure = null;
        var setterRaceWorker = new Thread(() =>
        {
            try
            {
                if (!setterEntered.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException("The highlight setter barrier was not reached.");
                }

                PublishRareTarget(
                    RuntimeNightBusinessLifecycle.Generation,
                    listPinningEnabled: true,
                    recipeVariantEnabled: false,
                    cookerHighlightEnabled: false,
                    seatHighlightEnabled: false,
                    orderHighlightEnabled: false,
                    recipeId: 38,
                    beverageId: 20,
                    ingredientIds: new[] { 15 },
                    extraIngredientIds: Array.Empty<int>(),
                    cookerTypeId: 3,
                    deskCode: -1,
                    orderTraceId: "",
                    targetRevision: "test-target");
            }
            catch (Exception ex)
            {
                setterRaceFailure = ex;
            }
            finally
            {
                setterRelease.Set();
            }
        });
        setterRaceWorker.Start();
        try
        {
            RuntimePinnedListHighlightService.Tick();
            // 模拟本帧 Canvas 提交：在正式网格写入中让后台发布下一代目标。
            setterRaceButton.image.RebuildIfDirty();
        }
        finally
        {
            setterRelease.Set();
            if (!setterRaceWorker.Join(TimeSpan.FromSeconds(7)))
            {
                throw new TimeoutException("The setter-race worker did not exit.");
            }
        }

        if (setterRaceFailure != null) throw new InvalidOperationException("Setter-race target publication failed.", setterRaceFailure);
        AssertFalse(UnityEngine.UI.VertexHelper.WriteBarrierTimedOut, "The mesh write barrier timed out instead of observing a concurrent target update.");
        AssertEqual(visualErrorsBeforeRace, ReadListHighlightCounter("visualErrors"), "The setter-race path was hidden as a visual error.");
        AssertTrue(setterRaceButton.image.Mesh.m_Colors.All(value => value == UnityEngine.UI.VertexHelper.ToBytes(recipeBase)),
            "目标在写入期间撤回后，本次 Canvas 提交仍包含旧颜色，不能依赖下一帧重建补救。");
        AssertUnhighlighted(recipeBase, setterRaceButton.image, "A target change during the first highlight write left an untracked color.");
        AssertContains(RuntimePinnedListHighlightService.Status, "tracked=recipe:0", "A stale first highlight remained tracked after its target changed.");
        AssertTrue(setterRaceButton.image.SetterThreadIds.All(threadId => threadId == mainThreadId), "The setter-race image was written outside the Unity main thread.");
        AssertTrue(UnityEngine.UI.VertexHelper.WriteThreadIds.All(threadId => threadId == mainThreadId), "A native background mesh was written outside the Unity main thread.");

        var bindingErrorsBefore = ReadListHighlightCounter("bindingErrors");
        var failedBindingButton = new UIButtonSimpleProbe(recipeBase);
        cookingPanel.OnRecipeElementEnabled(new RecipeProbe(38, throwOnIdRead: true), failedBindingButton.Cluster, failedBindingButton);
        AssertEqual(bindingErrorsBefore + 1, ReadListHighlightCounter("bindingErrors"), "Element binding diagnostics did not record a reflection failure.");
    }
    finally
    {
        RuntimePinnedListHighlightService.Suspend("test cleanup");
        CookingSelectionPanelProbe.ApplyIngredientBoundColor = false;
        CookingSelectionPanelProbe.OpenAction = null;
        CookingSelectionPanelProbe.RecipeBoundColor = new Color(0.8f, 0.8f, 0.8f, 0.5f);
        StoragePanelProbe.OpenAction = null;
        StoragePanelProbe.BoundColor = new Color(0.35f, 0.55f, 0.75f, 0.6f);
    }
}

static void VerifyExactRecipeVariantRowHighlighting()
{
    const int recipeId = 141;
    var lifecycleLogStart = ManualLogSource.SnapshotInformationMessages().Length;
    var listHighlightRoot = typeof(RuntimePinnedListHighlightService)
        .GetField("SyncRoot", BindingFlags.NonPublic | BindingFlags.Static)?
        .GetValue(null)
        ?? throw new InvalidOperationException("Pinned-list SyncRoot was not found.");
    ManualLogSource.ResetInformationLogSafetyObservation();
    ManualLogSource.InformationLogUnsafeProbe = () => Monitor.IsEntered(listHighlightRoot);
    var businessGeneration = RuntimeNightBusinessLifecycle.Generation;
    var baseline = new Color(0.64f, 0.68f, 0.74f, 0.52f);
    AssertTrue(RuntimeTargetHighlightColor.TryParseExactHex("FFD35A", out var rareColor), "Rare test color was rejected.");
    AssertTrue(RuntimeTargetHighlightColor.TryParseExactHex("5FACD3", out var normalColor), "Normal test color was rejected.");

    RuntimeUiTargetSnapshot CreateTarget(
        RuntimeUiTargetKind kind,
        bool recipeVariantEnabled,
        string revision,
        RuntimeTargetHighlightColor? color = null,
        IReadOnlyList<int>? extras = null)
    {
        var isRare = kind == RuntimeUiTargetKind.Rare;
        var targetExtras = recipeVariantEnabled
            ? extras ?? new[] { isRare ? 143 : 144 }
            : Array.Empty<int>();
        return new RuntimeUiTargetSnapshot(
            kind,
            color ?? (isRare ? rareColor : normalColor),
            listPinningEnabled: true,
            recipeVariantEnabled: recipeVariantEnabled,
            cookerHighlightEnabled: false,
            seatHighlightEnabled: false,
            orderHighlightEnabled: false,
            orderTraceId: isRare ? "R-141" : "N-141",
            orderKey: isRare ? "" : "ptr:8d",
            orderLifecycleSequence: isRare ? 141 : 142,
            deskCode: isRare ? 0 : 1,
            recipeId,
            ingredientIds: new[] { 142 },
            extraIngredientIds: targetExtras,
            beverageId: -1,
            cookerTypeId: -1,
            targetRevision: $"{revision}-{kind}");
    }

    RuntimeTargetRecipeVariantService.RetireForShutdown("reset exact row-highlight smoke");
    RuntimeUiPinningService.UpdateTargets(
        businessGeneration,
        new[]
        {
            CreateTarget(RuntimeUiTargetKind.Rare, recipeVariantEnabled: false, revision: "uncontrolled"),
            CreateTarget(RuntimeUiTargetKind.Normal, recipeVariantEnabled: false, revision: "uncontrolled"),
        });

    CookingSelectionPanelProbe.RecipeBoundColor = baseline;
    var panel = new CookingSelectionPanelProbe();
    var priorIdModeRecipe = new RecipeProbe(recipeId);
    var priorIdModeButton = new UIButtonSimpleProbe(baseline);
    panel.OnPanelOpen();
    panel.OnRecipeElementEnabled(priorIdModeRecipe, priorIdModeButton.Cluster, priorIdModeButton);
    Time.realtimeSinceStartup = 0.25f;
    RuntimePinnedListHighlightService.Tick();
    AssertHighlighted(baseline, priorIdModeButton.image, "An uncontrolled recipe lost its existing recipe-id highlight.");

    RuntimeUiPinningService.UpdateTargets(
        businessGeneration,
        new[]
        {
            CreateTarget(RuntimeUiTargetKind.Rare, recipeVariantEnabled: true, revision: "controlled"),
            CreateTarget(RuntimeUiTargetKind.Normal, recipeVariantEnabled: false, revision: "controlled"),
        });
    var controlledTarget = RuntimeUiPinningService.ReadTargetSet();
    AssertTrue(controlledTarget.HasRecipeVariants(recipeId), "The active variant recipe was not placed in exact-only mode.");
    AssertEqual(RuntimeUiTargetKinds.Normal, controlledTarget.GetBaseRecipeClaims(recipeId), "The authoritative base row did not retain only the non-variant normal claim.");
    AssertEqual(RuntimeUiTargetKinds.Rare, controlledTarget.GetRecipeVariantClaims(recipeId), "The synthetic row did not retain the rare variant claim.");

    RuntimePinnedListHighlightService.Tick();
    AssertUnhighlighted(baseline, priorIdModeButton.image, "An old recipe-id aggregate color survived the transition to exact-only mode.");
    AssertContains(RuntimePinnedListHighlightService.Status, "tracked=recipe:0", "An old recipe-id row stayed tracked after becoming variant-controlled.");

    var baseRecipe = new RecipeProbe(recipeId);
    var baseButton = new UIButtonSimpleProbe(baseline);
    RuntimeTargetRecipeVariantService.BindRecipeRow(
        panel,
        baseRecipe,
        baseButton,
        RuntimeUiTargetKinds.Normal,
        "normal-base");
    panel.OnRecipeElementEnabled(baseRecipe, baseButton.Cluster, baseButton);

    var syntheticRecipe = new RecipeProbe(recipeId);
    var syntheticButton = new UIButtonSimpleProbe(baseline);
    var staleSyntheticLease = RuntimeTargetRecipeVariantService.BindRecipeRow(
        panel,
        syntheticRecipe,
        syntheticButton,
        RuntimeUiTargetKinds.Rare,
        "rare-extra-143-stale");
    var currentSyntheticLease = RuntimeTargetRecipeVariantService.BindRecipeRow(
        panel,
        syntheticRecipe,
        syntheticButton,
        RuntimeUiTargetKinds.Rare,
        "rare-extra-143-current");
    AssertFalse(
        RuntimeTargetRecipeVariantService.TryValidateRecipeRowClaims(staleSyntheticLease, out _),
        "A pooled exact row accepted a stale plan identity with the same pointer tuple.");
    AssertTrue(
        RuntimeTargetRecipeVariantService.TryValidateRecipeRowClaims(currentSyntheticLease, out var reboundClaims)
            && reboundClaims == RuntimeUiTargetKinds.Rare,
        "The current exact row plan identity was not retained after a pointer-stable rebind.");
    panel.OnRecipeElementEnabled(syntheticRecipe, syntheticButton.Cluster, syntheticButton);

    var lifecycleMessages = ReadExactRecipeLifecycleMessages(lifecycleLogStart);
    var boundMessages = lifecycleMessages
        .Where(message => message.Contains("event=bound;", StringComparison.Ordinal))
        .ToArray();
    AssertEqual(2, boundMessages.Length, "Exact recipe rows did not log one lifecycle bind each.");
    AssertTrue(
        boundMessages.Any(message => message.Contains("plan=normal-base", StringComparison.Ordinal)
            && message.Contains("claims=Normal", StringComparison.Ordinal)),
        "The authoritative exact-row bind log lost its plan or claim identity.");
    AssertTrue(
        boundMessages.Any(message => message.Contains("plan=rare-extra-143-current", StringComparison.Ordinal)
            && message.Contains("claims=Rare", StringComparison.Ordinal)),
        "The synthetic exact-row bind log lost its current plan or claim identity.");
    AssertTrue(
        boundMessages.All(message => message.Contains($"business={businessGeneration};", StringComparison.Ordinal)
            && message.Contains($"targetGen={controlledTarget.Generation};", StringComparison.Ordinal)
            && message.Contains("panel=0x", StringComparison.Ordinal)
            && message.Contains("recipePtr=0x", StringComparison.Ordinal)
            && message.Contains("button=0x", StringComparison.Ordinal)
            && message.Contains("image=0x", StringComparison.Ordinal)),
        "An exact-row bind log omitted its bounded native identity fields.");

    var unboundSameIdButton = new UIButtonSimpleProbe(baseline);
    panel.OnRecipeElementEnabled(new RecipeProbe(recipeId), unboundSameIdButton.Cluster, unboundSameIdButton);
    RuntimePinnedListHighlightService.Tick();

    AssertColorBlocks(baseline, baseButton.image, RuntimeUiTargetKinds.Normal, controlledTarget.Palette, "The exact authoritative row did not use only the normal target color.");
    AssertColorBlocks(baseline, syntheticButton.image, RuntimeUiTargetKinds.Rare, controlledTarget.Palette, "The exact synthetic row did not use its rare target color.");
    AssertUnhighlighted(baseline, unboundSameIdButton.image, "A same-id row without an exact panel/recipe/button binding was highlighted.");
    AssertContains(RuntimePinnedListHighlightService.Status, "tracked=recipe:2", "Exact rows did not retain one Image ownership each.");

    lifecycleMessages = ReadExactRecipeLifecycleMessages(lifecycleLogStart);
    AssertEqual(
        2,
        lifecycleMessages.Count(message => message.Contains("event=applied;", StringComparison.Ordinal)),
        "Exact recipe rows did not log their first durable highlight application once.");

    var releaseCountBeforePoolRebind = lifecycleMessages.Count(message => message.Contains("event=released;", StringComparison.Ordinal));
    panel.OnRecipeElementEnabled(baseRecipe, baseButton.Cluster, baseButton);
    lifecycleMessages = ReadExactRecipeLifecycleMessages(lifecycleLogStart);
    AssertEqual(
        releaseCountBeforePoolRebind + 1,
        lifecycleMessages.Count(message => message.Contains("event=released;", StringComparison.Ordinal)),
        "A pooled exact recipe row did not log its actual ownership release.");
    AssertTrue(
        lifecycleMessages.Any(message => message.Contains("event=released;", StringComparison.Ordinal)
            && message.Contains("reason=pool-rebind;", StringComparison.Ordinal)
            && message.Contains("plan=normal-base", StringComparison.Ordinal)),
        "The pooled exact-row release did not retain its precise reason and lease identity.");
    RuntimePinnedListHighlightService.Tick();
    lifecycleMessages = ReadExactRecipeLifecycleMessages(lifecycleLogStart);
    AssertEqual(
        3,
        lifecycleMessages.Count(message => message.Contains("event=applied;", StringComparison.Ordinal)),
        "A rebound exact row did not receive one new first-application lifecycle event.");

    RuntimeTargetRecipeVariantService.AdvancePanelEpoch(panel);
    RuntimePinnedListHighlightService.Tick();
    AssertUnhighlighted(baseline, baseButton.image, "A stale authoritative row lease kept its highlight after the panel epoch advanced.");
    AssertUnhighlighted(baseline, syntheticButton.image, "A stale synthetic row lease kept its highlight after the panel epoch advanced.");
    AssertContains(RuntimePinnedListHighlightService.Status, "tracked=recipe:0", "Stale exact recipe-row leases remained tracked.");
    lifecycleMessages = ReadExactRecipeLifecycleMessages(lifecycleLogStart);
    var invalidLeaseReleaseCount = lifecycleMessages.Count(message =>
        message.Contains("event=released;", StringComparison.Ordinal)
        && message.Contains("reason=exact-lease-invalid;", StringComparison.Ordinal));
    AssertEqual(2, invalidLeaseReleaseCount, "Epoch drift did not release both exact rows with the lease-invalid reason.");
    RuntimePinnedListHighlightService.Tick();
    AssertEqual(
        invalidLeaseReleaseCount,
        ReadExactRecipeLifecycleMessages(lifecycleLogStart).Count(message =>
            message.Contains("event=released;", StringComparison.Ordinal)
            && message.Contains("reason=exact-lease-invalid;", StringComparison.Ordinal)),
        "A released exact-row lease emitted another lifecycle event on a later Tick.");

    panel.OnPanelClose();
    RuntimeTargetRecipeVariantService.RetireForShutdown("exact row-highlight smoke complete");

    AssertTrue(RuntimeTargetHighlightColor.TryParseExactHex("2A9FD6", out var customNormalColor), "Custom normal test color was rejected.");
    RuntimeUiPinningService.UpdateTargets(
        businessGeneration,
        new[]
        {
            CreateTarget(RuntimeUiTargetKind.Rare, recipeVariantEnabled: false, revision: "symmetric"),
            CreateTarget(
                RuntimeUiTargetKind.Normal,
                recipeVariantEnabled: true,
                revision: "symmetric",
                color: customNormalColor,
                extras: new[] { 144 }),
        });
    var symmetricTarget = RuntimeUiPinningService.ReadTargetSet();
    AssertEqual(RuntimeUiTargetKinds.Rare, symmetricTarget.GetBaseRecipeClaims(recipeId), "The symmetric base row did not retain only the rare claim.");
    AssertEqual(RuntimeUiTargetKinds.Normal, symmetricTarget.GetRecipeVariantClaims(recipeId), "The symmetric synthetic row did not retain only the normal claim.");
    var symmetricPanel = new CookingSelectionPanelProbe();
    symmetricPanel.OnPanelOpen();
    var symmetricBaseRecipe = new RecipeProbe(recipeId);
    var symmetricBaseButton = new UIButtonSimpleProbe(baseline);
    RuntimeTargetRecipeVariantService.BindRecipeRow(
        symmetricPanel,
        symmetricBaseRecipe,
        symmetricBaseButton,
        RuntimeUiTargetKinds.Rare,
        "rare-base-symmetric");
    symmetricPanel.OnRecipeElementEnabled(symmetricBaseRecipe, symmetricBaseButton.Cluster, symmetricBaseButton);
    var symmetricSyntheticRecipe = new RecipeProbe(recipeId);
    var symmetricSyntheticButton = new UIButtonSimpleProbe(baseline);
    RuntimeTargetRecipeVariantService.BindRecipeRow(
        symmetricPanel,
        symmetricSyntheticRecipe,
        symmetricSyntheticButton,
        RuntimeUiTargetKinds.Normal,
        "normal-extra-144");
    symmetricPanel.OnRecipeElementEnabled(symmetricSyntheticRecipe, symmetricSyntheticButton.Cluster, symmetricSyntheticButton);
    Time.realtimeSinceStartup = 0.25f;
    RuntimePinnedListHighlightService.Tick();
    AssertColorBlocks(baseline, symmetricBaseButton.image, RuntimeUiTargetKinds.Rare, symmetricTarget.Palette, "The symmetric authoritative row did not use only the rare target color.");
    AssertColorBlocks(baseline, symmetricSyntheticButton.image, RuntimeUiTargetKinds.Normal, symmetricTarget.Palette, "The normal synthetic row did not use its custom target color.");
    var panelCloseReleaseCount = ReadExactRecipeLifecycleMessages(lifecycleLogStart).Count(message =>
        message.Contains("event=released;", StringComparison.Ordinal)
        && message.Contains("reason=panel-closed;", StringComparison.Ordinal));
    symmetricPanel.OnPanelClose();
    AssertEqual(
        panelCloseReleaseCount + 2,
        ReadExactRecipeLifecycleMessages(lifecycleLogStart).Count(message =>
            message.Contains("event=released;", StringComparison.Ordinal)
            && message.Contains("reason=panel-closed;", StringComparison.Ordinal)),
        "Cooking panel close did not log both exact-row ownership releases precisely.");
    RuntimeTargetRecipeVariantService.RetireForShutdown("symmetric exact row-highlight smoke complete");

    RuntimeUiPinningService.UpdateTargets(
        businessGeneration,
        new[]
        {
            CreateTarget(RuntimeUiTargetKind.Rare, recipeVariantEnabled: true, revision: "split-extras", extras: new[] { 143 }),
            CreateTarget(RuntimeUiTargetKind.Normal, recipeVariantEnabled: true, revision: "split-extras", color: customNormalColor, extras: new[] { 144 }),
        });
    var splitExtrasTarget = RuntimeUiPinningService.ReadTargetSet();
    AssertEqual(RuntimeUiTargetKinds.None, splitExtrasTarget.GetBaseRecipeClaims(recipeId), "Two variant targets leaked an aggregate claim onto the base row.");
    var splitPanel = new CookingSelectionPanelProbe();
    splitPanel.OnPanelOpen();
    var rareSplitRecipe = new RecipeProbe(recipeId);
    var rareSplitButton = new UIButtonSimpleProbe(baseline);
    RuntimeTargetRecipeVariantService.BindRecipeRow(
        splitPanel,
        rareSplitRecipe,
        rareSplitButton,
        RuntimeUiTargetKinds.Rare,
        "rare-extra-143-split");
    splitPanel.OnRecipeElementEnabled(rareSplitRecipe, rareSplitButton.Cluster, rareSplitButton);
    var normalSplitRecipe = new RecipeProbe(recipeId);
    var normalSplitButton = new UIButtonSimpleProbe(baseline);
    RuntimeTargetRecipeVariantService.BindRecipeRow(
        splitPanel,
        normalSplitRecipe,
        normalSplitButton,
        RuntimeUiTargetKinds.Normal,
        "normal-extra-144-split");
    splitPanel.OnRecipeElementEnabled(normalSplitRecipe, normalSplitButton.Cluster, normalSplitButton);
    RuntimePinnedListHighlightService.Tick();
    AssertColorBlocks(baseline, rareSplitButton.image, RuntimeUiTargetKinds.Rare, splitExtrasTarget.Palette, "Different rare extras did not retain their exact rare-only synthetic color.");
    AssertColorBlocks(baseline, normalSplitButton.image, RuntimeUiTargetKinds.Normal, splitExtrasTarget.Palette, "Different normal extras did not retain their exact normal-only synthetic color.");
    splitPanel.OnPanelClose();
    RuntimeTargetRecipeVariantService.RetireForShutdown("split extras exact row-highlight smoke complete");

    RuntimeUiPinningService.UpdateTargets(
        businessGeneration,
        new[]
        {
            CreateTarget(RuntimeUiTargetKind.Rare, recipeVariantEnabled: true, revision: "shared-extras", extras: new[] { 145 }),
            CreateTarget(RuntimeUiTargetKind.Normal, recipeVariantEnabled: true, revision: "shared-extras", color: customNormalColor, extras: new[] { 145 }),
        });
    var sharedExtrasTarget = RuntimeUiPinningService.ReadTargetSet();
    var sharedPanel = new CookingSelectionPanelProbe();
    sharedPanel.OnPanelOpen();
    var sharedRecipe = new RecipeProbe(recipeId);
    var sharedButton = new UIButtonSimpleProbe(baseline);
    RuntimeTargetRecipeVariantService.BindRecipeRow(
        sharedPanel,
        sharedRecipe,
        sharedButton,
        RuntimeUiTargetKinds.Rare | RuntimeUiTargetKinds.Normal,
        "shared-extra-145");
    sharedPanel.OnRecipeElementEnabled(sharedRecipe, sharedButton.Cluster, sharedButton);
    Time.realtimeSinceStartup = 0.75f;
    RuntimePinnedListHighlightService.Tick();
    AssertColorBlocks(baseline, sharedButton.image, RuntimeUiTargetKinds.Rare | RuntimeUiTargetKinds.Normal, sharedExtrasTarget.Palette, "A shared ordered-extras row did not retain its dual target claim and palette.");
    sharedPanel.OnPanelClose();
    RuntimeTargetRecipeVariantService.RetireForShutdown("shared extras exact row-highlight smoke complete");

    var boundedPanel = new CookingSelectionPanelProbe();
    boundedPanel.OnPanelOpen();
    for (var index = 0; index < 20; index += 1)
    {
        var boundedRecipe = new RecipeProbe(recipeId);
        var boundedButton = new UIButtonSimpleProbe(baseline);
        RuntimeTargetRecipeVariantService.BindRecipeRow(
            boundedPanel,
            boundedRecipe,
            boundedButton,
            RuntimeUiTargetKinds.Rare | RuntimeUiTargetKinds.Normal,
            $"bounded-exact-row-{index}");
        boundedPanel.OnRecipeElementEnabled(boundedRecipe, boundedButton.Cluster, boundedButton);
    }
    boundedPanel.OnPanelClose();
    RuntimeTargetRecipeVariantService.RetireForShutdown("bounded exact row-highlight logging smoke complete");

    lifecycleMessages = ReadExactRecipeLifecycleMessages(lifecycleLogStart);
    AssertEqual(32, lifecycleMessages.Length, "Exact-row lifecycle logging exceeded or failed to reach its per-business bound.");
    AssertContains(RuntimePinnedListHighlightService.Status, "exactRecipeLogs=32/32", "Exact-row lifecycle log status did not expose the active bound.");
    AssertTrue(
        ReadListHighlightCounter("exactRecipeLogsSuppressed") > 0,
        "Exact-row lifecycle events beyond the per-business bound were not counted as suppressed.");
    AssertFalse(
        ManualLogSource.InformationLoggedWhileUnsafe,
        "An exact-row lifecycle message was emitted while the pinned-list state lock was held.");
    ManualLogSource.InformationLogUnsafeProbe = null;
}

static string[] ReadExactRecipeLifecycleMessages(int startIndex)
{
    return ManualLogSource.SnapshotInformationMessages()
        .Skip(startIndex)
        .Where(message => message.StartsWith(
            "Runtime pinned list exact recipe lifecycle ",
            StringComparison.Ordinal))
        .ToArray();
}

static string PublishRareTarget(
    long sessionGeneration,
    bool listPinningEnabled,
    bool recipeVariantEnabled,
    bool cookerHighlightEnabled,
    bool seatHighlightEnabled,
    bool orderHighlightEnabled,
    int recipeId,
    int beverageId,
    IEnumerable<int> ingredientIds,
    IEnumerable<int> extraIngredientIds,
    int cookerTypeId,
    int deskCode,
    string orderTraceId,
    string targetRevision)
{
    var ingredients = ingredientIds.ToArray();
    var extras = extraIngredientIds.ToArray();
    var publishesTarget = recipeId >= 0
        || beverageId >= 0
        || cookerTypeId >= 0
        || deskCode >= 0
        || ingredients.Length > 0
        || extras.Length > 0
        || orderTraceId.Length > 0;
    var targets = publishesTarget
        ? new[]
        {
            new RuntimeUiTargetSnapshot(
                RuntimeUiTargetKind.Rare,
                RuntimeTargetHighlightColor.DefaultRare,
                listPinningEnabled,
                recipeVariantEnabled,
                cookerHighlightEnabled,
                seatHighlightEnabled,
                orderHighlightEnabled,
                orderHighlightEnabled ? orderTraceId : "R-1",
                orderKey: "",
                orderLifecycleSequence: 1,
                deskCode >= 0 ? deskCode : 0,
                recipeId,
                ingredients,
                extras,
                beverageId,
                cookerTypeId,
                targetRevision),
        }
        : Array.Empty<RuntimeUiTargetSnapshot>();
    return RuntimeUiPinningService.UpdateTargets(
        sessionGeneration,
        targets);
}

static void RunOnWorkerThread(Action action)
{
    Exception? failure = null;
    var thread = new Thread(() =>
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            failure = ex;
        }
    });
    thread.Start();
    thread.Join();
    if (failure != null) throw new InvalidOperationException("Worker-thread target publication failed.", failure);
}

static long ReadListHighlightCounter(string counterName)
{
    var match = Regex.Match(RuntimePinnedListHighlightService.Status, $@"\b{Regex.Escape(counterName)}=(\d+)");
    if (!match.Success || !long.TryParse(match.Groups[1].Value, out var value))
    {
        throw new InvalidOperationException($"Could not read {counterName} from status: {RuntimePinnedListHighlightService.Status}");
    }

    return value;
}

static long ReadPanelRefreshCounter(string counterName)
{
    var match = Regex.Match(RuntimeUiPinningService.Status, $@"\b{Regex.Escape(counterName)}:(\d+)");
    if (!match.Success || !long.TryParse(match.Groups[1].Value, out var value))
    {
        throw new InvalidOperationException(
            $"Could not read panel refresh {counterName} from status: {RuntimeUiPinningService.Status}");
    }

    return value;
}

static long ReadPanelRefreshGeneration(string panelKind, string generationKind)
{
    var match = Regex.Match(
        RuntimeUiPinningService.Status,
        $@"panelRefresh=[^;]*\b{Regex.Escape(panelKind)}:open@[^;]*?/{Regex.Escape(generationKind)}:(-?\d+)");
    if (!match.Success || !long.TryParse(match.Groups[1].Value, out var value))
    {
        throw new InvalidOperationException(
            $"Could not read {panelKind} {generationKind} generation from status: {RuntimeUiPinningService.Status}");
    }

    return value;
}

static bool InvokeCheckPinned(int pinnedType, int pinnedId, bool originalResult = false)
{
    var call = InvokeCheckPinnedPrefix(pinnedType, pinnedId);
    return call.RunOriginal ? originalResult : call.Result;
}

static (bool RunOriginal, bool Result) InvokeCheckPinnedPrefix(int pinnedType, int pinnedId)
{
    var args = new object?[] { pinnedType, pinnedId, false };
    var runOriginal = (bool)(InvokePrivate("OnCheckPinned", args) ?? true);
    return (runOriginal, (bool)(args[2] ?? false));
}

static long ReadForcedTotal(string targetType)
{
    var match = Regex.Match(RuntimeUiPinningService.Status, $@"forcedTotal=[^;]*\b{Regex.Escape(targetType)}:(\d+)");
    if (!match.Success || !long.TryParse(match.Groups[1].Value, out var value))
    {
        throw new InvalidOperationException($"Could not read {targetType} forcedTotal from status: {RuntimeUiPinningService.Status}");
    }

    return value;
}

static object? InvokePrivate(string methodName, params object?[] args)
{
    var method = typeof(RuntimeUiPinningService).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMethodException(typeof(RuntimeUiPinningService).FullName, methodName);
    try
    {
        return method.Invoke(null, args);
    }
    catch (TargetInvocationException ex) when (ex.InnerException != null)
    {
        throw ex.InnerException;
    }
}

static void AssertPatch(
    Type declaringType,
    string originalName,
    string? prefix,
    string? postfix,
    string? finalizer,
    Type? patchOwner = null,
    int? prefixPriority = null,
    int? postfixPriority = null,
    string? postfixAfter = null)
{
    var original = declaringType.GetMethod(originalName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
        ?? throw new MissingMethodException(declaringType.FullName, originalName);
    var patch = Harmony.GetPatchInfo(original)
        ?? throw new InvalidOperationException($"Patch for {declaringType.FullName}.{originalName} was not installed.");
    var serviceType = patchOwner ?? typeof(RuntimeUiPinningService);
    var actualPrefix = patch.Prefixes.Select(item => item.PatchMethod).SingleOrDefault(method => method.DeclaringType == serviceType)?.Name;
    var actualPostfix = patch.Postfixes.Select(item => item.PatchMethod).SingleOrDefault(method => method.DeclaringType == serviceType)?.Name;
    var actualFinalizer = patch.Finalizers.Select(item => item.PatchMethod).SingleOrDefault(method => method.DeclaringType == serviceType)?.Name;
    AssertEqual(prefix, actualPrefix, $"Unexpected prefix for {originalName}.");
    AssertEqual(postfix, actualPostfix, $"Unexpected postfix for {originalName}.");
    AssertEqual(finalizer, actualFinalizer, $"Unexpected finalizer for {originalName}.");
    if (prefixPriority.HasValue)
    {
        var actualPriority = patch.Prefixes.Single(item => item.PatchMethod.DeclaringType == serviceType).priority;
        AssertEqual(prefixPriority.Value, actualPriority, $"Unexpected prefix priority for {originalName}.");
    }
    if (postfixPriority.HasValue)
    {
        var actualPriority = patch.Postfixes.Single(item => item.PatchMethod.DeclaringType == serviceType).priority;
        AssertEqual(postfixPriority.Value, actualPriority, $"Unexpected postfix priority for {originalName}.");
    }
    if (postfixAfter != null)
    {
        var after = patch.Postfixes.Single(item => item.PatchMethod.DeclaringType == serviceType).after;
        AssertTrue(after.Contains(postfixAfter, StringComparer.Ordinal), $"Postfix ordering for {originalName} did not follow the variant binding service.");
    }
}

static void AssertNoPatch(
    Type declaringType,
    string originalName,
    Type patchOwner,
    string message)
{
    var original = declaringType.GetMethod(
        originalName,
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
        ?? throw new MissingMethodException(declaringType.FullName, originalName);
    var patch = Harmony.GetPatchInfo(original);
    if (patch == null) return;

    var ownerPresent = patch.Prefixes
            .Concat(patch.Postfixes)
            .Concat(patch.Finalizers)
            .Any(item => item.PatchMethod.DeclaringType == patchOwner);
    AssertFalse(ownerPresent, message);
}

/// <summary>
/// 验证完整背景色带的几何、原生图标层级、同色去重与明确身份失效。
/// 仅测试已声明引用，不从测试场景中的任意图像补选当前物品图标。
/// </summary>
static void VerifyColorBlockLayoutAndNativeOwnership()
{
    var baseline = new Color(.2f, .3f, .4f, .45f);
    var button = new UIButtonSimpleProbe(baseline);
    var recipe = new RecipeProbe(401);
    button.Cluster.Bind(recipe);
    var icon = button.Cluster.Icon;
    var nativeIconColor = icon.color;
    var targets = Enumerable.Range(0, 8).Select(slot => new RuntimeUiTargetSnapshot(
        slot < 4 ? RuntimeUiTargetKind.Rare : RuntimeUiTargetKind.Normal,
        new RuntimeTargetHighlightColor((byte)(30 + slot * 20), (byte)(200 - slot * 15), (byte)(50 + slot * 17)),
        true, false, false, false, false, $"{(slot < 4 ? 'R' : 'N')}-{900 + slot}",
        slot < 4 ? "" : $"ptr:{900 + slot:x}", 900 + slot, slot, 401,
        new[] { 96 }, Array.Empty<int>(), -1, -1, $"layout:{slot}", slot)).ToArray();
    var palette = new RuntimeTargetHighlightPalette(targets);
    var blocks = RuntimePinnedListColorBlocks.TryCreate(button, button.image, button.Cluster, recipe.Text.Visual, out var failure);
    AssertTrue(blocks != null, "合法背景/图标/cluster 被拒绝：" + failure);
    var initialNativeWrites = button.image.SetterCount;
    var nativeChildren = button.transform.Children.ToArray();
    var allocatedObjects = UnityEngine.Object.All.OfType<GameObject>().Count();
    try
    {
        // 分色直接写入原 Image 的顶点；原生对象数量、子节点和交互状态必须完全不变。
        AssertTrue(blocks!.Apply((RuntimeUiTargetKinds)255, palette), "八色完整背景应用失败");
        for (var count = 1; count <= 8; count++)
        {
            var claims = (RuntimeUiTargetKinds)((1 << count) - 1);
            AssertTrue(blocks.Apply(claims, palette), "颜色数量更新失败");
            var mesh = new UnityEngine.UI.VertexHelper();
            button.image.OnPopulateMesh(mesh);
            var originalLeft = mesh.m_Positions.Min(point => point.x);
            var originalRight = mesh.m_Positions.Max(point => point.x);
            var originalBottom = mesh.m_Positions.Min(point => point.y);
            var originalTop = mesh.m_Positions.Max(point => point.y);
            AssertTrue(blocks.ApplyToNativeMesh(mesh, claims, palette, out failure), "原生背景网格分色失败：" + failure);
            AssertNativeBands(mesh, baseline, claims, palette, "背景色带未保留完整声明");
            for (var index = 0; index < count; index++)
            {
                var tint = TintedVertexColor(baseline, palette[index]);
                var points = mesh.m_Positions.Where((point, vertex) => mesh.m_Colors[vertex] == tint).ToArray();
                // 同时检查左右分区的位置和全高覆盖，避免仅凭出现了全部颜色就接受错误方向。
                AssertEqualWithin(originalLeft + (originalRight - originalLeft) * index / count, points.Min(point => point.x), "色区左边界未按槽位排列");
                AssertEqualWithin(originalLeft + (originalRight - originalLeft) * (index + 1) / count, points.Max(point => point.x), "色区未均分原背景宽度");
                AssertEqualWithin(originalBottom, points.Min(point => point.y), "左右色区未延伸到原背景下边界");
                AssertEqualWithin(originalTop, points.Max(point => point.y), "左右色区未延伸到原背景上边界");
            }
            AssertTrue(nativeChildren.SequenceEqual(button.transform.Children), "原生背景分色新增或调整了子节点");
            AssertEqual(allocatedObjects, UnityEngine.Object.All.OfType<GameObject>().Count(), "原生背景分色创建了额外视觉对象");
            for (var index = 0; index < mesh.m_Positions.Count; index++)
            {
                AssertEqualWithin((mesh.m_Positions[index].x + 1) * .5f, mesh.m_Uv0S[index].x, "裁剪破坏原生 UV 横坐标");
                AssertEqualWithin(mesh.m_Positions[index].y + .5f, mesh.m_Uv0S[index].y, "裁剪破坏原生 UV 纵坐标");
                AssertEqual(mesh.m_Uv0S[index], mesh.m_Uv3S[index], "裁剪丢失附加 UV 通道");
            }
            AssertTrue(blocks.RestoreNativeMesh(mesh, out failure), "回退原生网格失败：" + failure);
            AssertEqual(4, mesh.m_Positions.Count, "回退没有恢复原顶点数量");
            AssertTrue(mesh.m_Colors.All(value => value == UnityEngine.UI.VertexHelper.ToBytes(baseline)), "回退没有恢复原顶点颜色");
        }
        AssertTrue(button.image.raycastTarget, "原生按钮射线配置被修改");
        AssertColor(nativeIconColor, icon.color, "物品图标颜色被更改");
        AssertTrue(ReferenceEquals(recipe.Text.Visual, icon.sprite), "物品图标被替换");
        AssertEqual(initialNativeWrites, button.image.SetterCount, "更新色带写入了原生图像");

        var duplicatePalette = new RuntimeTargetHighlightPalette(targets[0].Color, targets[0].Color);
        AssertTrue(blocks.Apply(RuntimeUiTargetKinds.Rare | RuntimeUiTargetKinds.Normal, duplicatePalette), "同色声明应用失败");
        var duplicateMesh = new UnityEngine.UI.VertexHelper();
        button.image.OnPopulateMesh(duplicateMesh);
        AssertTrue(blocks.ApplyToNativeMesh(duplicateMesh, RuntimeUiTargetKinds.Rare | RuntimeUiTargetKinds.Normal, duplicatePalette, out failure), failure);
        AssertEqual(1, duplicateMesh.m_Colors.Distinct().Count(), "同色声明没有仅在视觉层去重");
        AssertTrue(blocks.Apply(RuntimeUiTargetKinds.Normal, duplicatePalette), "移除一笔同色声明失败");
        AssertTrue(blocks.Apply(RuntimeUiTargetKinds.None, duplicatePalette), "空声明未正常隐藏");
        AssertTrue(blocks.Apply(RuntimeUiTargetKinds.Normal, duplicatePalette), "空声明后没有复用旧资源");

        var beforeHotFrames = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 10000; index++) blocks.Apply(RuntimeUiTargetKinds.Normal, duplicatePalette);
        AssertEqual(0L, GC.GetAllocatedBytesForCurrentThread() - beforeHotFrames, "背景颜色无变化热帧产生托管分配");
        button.image.enabled = false;
        AssertTrue(blocks.RefreshVisibility(), "隐藏原生背景时精确绑定被错误撤回");
        button.image.enabled = true;
        button.image.set_color(new Color(.2f, .3f, .4f, .2f));
        AssertTrue(blocks.RefreshVisibility(), "原生透明度变化令合法绑定失效");
        var alphaMesh = new UnityEngine.UI.VertexHelper();
        button.image.OnPopulateMesh(alphaMesh);
        AssertTrue(blocks.ApplyToNativeMesh(alphaMesh, RuntimeUiTargetKinds.Normal, duplicatePalette, out failure), failure);
        AssertTrue(alphaMesh.m_Colors.All(value => value.a == 51), "原生透明度被重复相乘或忽略");

        icon.sprite = new Sprite();
        AssertFalse(blocks.RefreshVisibility(), "池化图标切到别的 sprite 后仍沿用旧色带");
        icon.sprite = recipe.Text.Visual;
        icon.transform.SetAsFirstSibling();
        AssertTrue(blocks.RefreshVisibility(), "原图标顺序变化不应破坏直接背景网格绑定");
    }
    finally { blocks!.Dispose(); }
    AssertTrue(!button.Destroyed && !button.image.Destroyed && !icon.Destroyed,
        "清理背景分色销毁了原生按钮/图标");
    AssertEqual(allocatedObjects, UnityEngine.Object.All.OfType<GameObject>().Count(), "背景分色意外创建了覆盖层");

    // 真实图标缺失、重复声明、错用他项 cluster 时必须拒绝，而不是扫描场景猜一个图标。
    var unavailable = RuntimePinnedListColorBlocks.TryCreate(button, button.image, new object(), recipe.Text.Visual, out _);
    AssertTrue(unavailable == null, "未知 cluster 被接受");
    var different = new UIButtonSimpleProbe(baseline);
    different.Cluster.Bind(recipe);
    unavailable = RuntimePinnedListColorBlocks.TryCreate(button, button.image, different.Cluster, recipe.Text.Visual, out _);
    AssertTrue(unavailable == null, "其他按钮的 cluster 被误用");
    icon.sprite = new Sprite();
    unavailable = RuntimePinnedListColorBlocks.TryCreate(button, button.image, button.Cluster, recipe.Text.Visual, out _);
    AssertTrue(unavailable == null, "不匹配当前物品的 sprite 被误用");
    icon.sprite = recipe.Text.Visual;
    var duplicated = new DuplicateClusterProbe(button.gameObject, icon.transform);
    unavailable = RuntimePinnedListColorBlocks.TryCreate(button, button.image, duplicated, recipe.Text.Visual, out _);
    AssertTrue(unavailable == null, "多个匹配图标的歧义被忽略");
}

/// <summary>
/// 直接执行正式背景回调，覆盖共享 VertexHelper 的空输入、无关 Image、线程屏障和坏网格回退。
/// 这里只确认声明的托管边界，不将反射回调调用记作真实 Harmony/IL2CPP 注入通过。
/// </summary>
static void VerifyNativeMeshCallbackGuards()
{
    AssertEqual(9, (int)typeof(RuntimePinnedListHighlightService)
        .GetField("ExpectedHookCount", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!,
        "列表 hook 声明未包含原背景网格入口");
    var callback = typeof(RuntimePinnedListHighlightService).GetMethod("AfterNativeBackgroundMesh", BindingFlags.NonPublic | BindingFlags.Static)!;
    AssertTrue(callback.ReturnType == typeof(void) && callback.GetParameters().All(value => value.ParameterType == typeof(object))
        && callback.GetParameters().Length == 2, "背景回调签名与运行时边界不一致");
    var baseline = new Color(.4f, .5f, .6f, .3f);
    var panel = new CookingSelectionPanelProbe();
    var button = new UIButtonSimpleProbe(baseline);
    CookingSelectionPanelProbe.RecipeBoundColor = baseline;
    PublishRareTarget(RuntimeNightBusinessLifecycle.Generation, listPinningEnabled: true, recipeVariantEnabled: false,
        cookerHighlightEnabled: false, seatHighlightEnabled: false, orderHighlightEnabled: false,
        recipeId: 77, beverageId: -1, ingredientIds: Array.Empty<int>(), extraIngredientIds: Array.Empty<int>(),
        cookerTypeId: -1, deskCode: -1, orderTraceId: "", targetRevision: "mesh-guards");
    panel.OnPanelOpen();
    try
    {
        panel.OnRecipeElementEnabled(new RecipeProbe(77), button.Cluster, button);
        RuntimePinnedListHighlightService.Tick();
        var empty = new UnityEngine.UI.VertexHelper();
        ListColorBlockProbe.Invoke("AfterNativeBackgroundMesh", button.image, empty);
        AssertEqual(0, empty.m_Positions.Count, "合法空网格产生了额外覆盖物");
        AssertContains(RuntimePinnedListHighlightService.Status, "tracked=recipe:1", "空网格不应永久撤回当前图像登记");
        AssertHighlighted(baseline, button.image, "空网格后正常重建未恢复着色");

        var iconColor = button.Cluster.Icon.color;
        var iconMesh = button.Cluster.Icon.RebuildIfDirty();
        AssertTrue(iconMesh.m_Colors.All(value => value == UnityEngine.UI.VertexHelper.ToBytes(iconColor)), "全局 Image 钩子错误染色中心物品图标");
        var unrelated = new UIButtonSimpleProbe(baseline);
        AssertUnhighlighted(baseline, unrelated.image, "全局 Image 钩子错误染色未登记背景");

        var workerMesh = new UnityEngine.UI.VertexHelper();
        button.image.OnPopulateMesh(workerMesh);
        var written = UnityEngine.UI.VertexHelper.WriteThreadIds.Count;
        RunOnWorkerThread(() => ListColorBlockProbe.Invoke("AfterNativeBackgroundMesh", button.image, workerMesh));
        AssertEqual(written, UnityEngine.UI.VertexHelper.WriteThreadIds.Count, "后台网格回调越过 Unity 主线程屏障");
        AssertTrue(workerMesh.m_Colors.All(value => value == UnityEngine.UI.VertexHelper.ToBytes(baseline)), "后台调用污染原生网格");

        var invalid = new UnityEngine.UI.VertexHelper();
        button.image.OnPopulateMesh(invalid);
        invalid.m_Uv2S.RemoveAt(0);
        var beforeColors = invalid.m_Colors.ToArray();
        ListColorBlockProbe.Invoke("AfterNativeBackgroundMesh", button.image, invalid);
        AssertTrue(beforeColors.SequenceEqual(invalid.m_Colors) && invalid.m_Positions.Count == 4 && invalid.m_Indices.Count == 6,
            "通道不完整时先破坏了原生网格再失败");
        AssertContains(RuntimePinnedListHighlightService.Status, "tracked=recipe:0", "失败网格绑定没有撤回");
    }
    finally
    {
        panel.OnPanelClose();
        RuntimeUiPinningService.UpdateTargets(RuntimeNightBusinessLifecycle.Generation, Array.Empty<RuntimeUiTargetSnapshot>());
        CookingSelectionPanelProbe.RecipeBoundColor = new Color(.8f, .8f, .8f, .5f);
    }
}

/// <summary>
/// 在正式 AddVert 写入边界注入重入/复核异常，确认 Service 保护的是正在提交的同一网格，
/// 而非仅在下一帧清理登记。不会以提前抛错代替写入完成后的失败路径。
/// </summary>
static void VerifyNativeMeshExceptionalWrites()
{
    var baseline = new Color(.5f, .6f, .7f, .4f);
    var panel = new CookingSelectionPanelProbe();
    var recipe = new RecipeProbe(78);
    var button = new UIButtonSimpleProbe(baseline);
    CookingSelectionPanelProbe.RecipeBoundColor = baseline;
    PublishRareTarget(RuntimeNightBusinessLifecycle.Generation, listPinningEnabled: true, recipeVariantEnabled: true,
        cookerHighlightEnabled: false, seatHighlightEnabled: false, orderHighlightEnabled: false,
        recipeId: 78, beverageId: -1, ingredientIds: new[] { 13 }, extraIngredientIds: new[] { 13 },
        cookerTypeId: 3, deskCode: -1, orderTraceId: "", targetRevision: "mesh-exceptional-writes");
    panel.OnPanelOpen();
    try
    {
        RuntimeTargetRecipeVariantService.BindRecipeRow(panel, recipe, button, RuntimeUiTargetKinds.Rare, "mesh-exact-row");
        panel.OnRecipeElementEnabled(recipe, button.Cluster, button);
        RuntimePinnedListHighlightService.Tick();
        var beforeErrors = ReadListHighlightCounter("visualErrors");
        var nestedCalls = 0;
        UnityEngine.UI.VertexHelper.NextWriteAction = () =>
        {
            nestedCalls++;
            // 此时共享网格只有一个顶点，嵌套读取会判为坏网格；正式重入屏障必须直接返回。
            ListColorBlockProbe.Invoke("AfterNativeBackgroundMesh", button.image, button.image.Mesh);
        };
        button.image.RebuildIfDirty();
        AssertEqual(1, nestedCalls, "没有在实际网格写入期间触发嵌套回调");
        AssertEqual(beforeErrors, ReadListHighlightCounter("visualErrors"), "嵌套网格回调误撤回外层有效登记");
        AssertContains(RuntimePinnedListHighlightService.Status, "tracked=recipe:1", "嵌套回调清理了有效行绑定");
        AssertNativeBands(button.image.Mesh, baseline, RuntimeUiTargetKinds.Rare,
            RuntimeUiPinningService.ReadTargetSet().Palette, "重入后外层网格没有正常完成");

        var wroteVertex = false;
        var failedChecks = 0;
        RuntimeTargetRecipeVariantService.BeforeRowValidation = () =>
        {
            if (!wroteVertex) return;
            failedChecks++;
            throw new InvalidOperationException("隔离测试：正式网格写入后精确行租约读取失败。");
        };
        UnityEngine.UI.VertexHelper.NextWriteAction = () => wroteVertex = true;
        button.image.SetVerticesDirty();
        button.image.RebuildIfDirty();
        AssertTrue(wroteVertex && failedChecks == 1, "没有到达正式写入后的精确租约异常路径");
        AssertEqual(4, button.image.Mesh.currentVertCount, "写后异常没有同步恢复原顶点结构");
        AssertEqual(6, button.image.Mesh.currentIndexCount, "写后异常没有同步恢复原索引结构");
        AssertTrue(button.image.Mesh.m_Colors.All(value => value == UnityEngine.UI.VertexHelper.ToBytes(baseline)),
            "写后复核抛错仍让旧颜色进入本帧 Canvas 提交");
        AssertContains(RuntimePinnedListHighlightService.Status, "tracked=recipe:0", "写后异常没有撤销失败行租约");
        AssertEqual(beforeErrors + 1, ReadListHighlightCounter("visualErrors"), "写后异常未准确记录一次视觉失败");

        RuntimeTargetRecipeVariantService.BeforeRowValidation = null;
        RuntimeTargetRecipeVariantService.BindRecipeRow(panel, recipe, button, RuntimeUiTargetKinds.Rare, "mesh-exact-row-recovered");
        panel.OnRecipeElementEnabled(recipe, button.Cluster, button);
        RuntimePinnedListHighlightService.Tick();
        AssertHighlighted(baseline, button.image, "异常后重入屏障没有释放，阻止后续正常网格恢复");
    }
    finally
    {
        RuntimeTargetRecipeVariantService.BeforeRowValidation = null;
        UnityEngine.UI.VertexHelper.NextWriteAction = null;
        panel.OnPanelClose();
        RuntimeUiPinningService.UpdateTargets(RuntimeNightBusinessLifecycle.Generation, Array.Empty<RuntimeUiTargetSnapshot>());
        CookingSelectionPanelProbe.RecipeBoundColor = new Color(.8f, .8f, .8f, .5f);
    }
}

static void AssertHighlighted(Color original, UiImageProbe image, string message)
{
    AssertColor(original, image.get_color(), message + " 原生图像颜色不应变化。");
    var mesh = image.RebuildIfDirty();
    AssertTrue(mesh.m_Colors.Any(value => value != UnityEngine.UI.VertexHelper.ToBytes(original)), message + " 原背景网格没有着色。");
}

static void AssertUnhighlighted(Color original, UiImageProbe image, string message)
{
    AssertColor(original, image.get_color(), message + " 原生图像颜色不应变化。");
    var mesh = image.RebuildIfDirty();
    AssertTrue(mesh.m_Colors.All(value => value == UnityEngine.UI.VertexHelper.ToBytes(original)), message + " 旧背景色带未清理。");
}

static void AssertColorBlocks(Color original, UiImageProbe image, RuntimeUiTargetKinds claims,
    RuntimeTargetHighlightPalette palette, string message)
{
    AssertColor(original, image.get_color(), message + " 原生图像颜色不应变化。");
    AssertNativeBands(image.RebuildIfDirty(), original, claims, palette, message);
}

static Color32 TintedVertexColor(Color baseline, RuntimeTargetHighlightColor tint)
{
    var original = UnityEngine.UI.VertexHelper.ToBytes(baseline);
    var color = tint.ToUnityColor();
    return new Color32((byte)MathF.Round(original.r * color.r), (byte)MathF.Round(original.g * color.g),
        (byte)MathF.Round(original.b * color.b), original.a);
}

/// <summary>检查原生背景三角形着色，禁止用仅检查私有对象存在来替代真实网格输出。</summary>
static void AssertNativeBands(UnityEngine.UI.VertexHelper mesh, Color baseline, RuntimeUiTargetKinds claims,
    RuntimeTargetHighlightPalette palette, string message)
{
    var colors = Enumerable.Range(0, 8).Where(slot => ((int)claims & (1 << slot)) != 0)
        .Select(slot => palette[slot]).Distinct().Select(color => TintedVertexColor(baseline, color)).ToArray();
    AssertEqual(colors.Length, mesh.m_Colors.Distinct().Count(), message + " 原背景没有完整分色。");
    AssertTrue(colors.All(color => mesh.m_Colors.Contains(color)), message + " 某一订单颜色未进入原背景。");
    var left = mesh.m_Indices.Min(index => mesh.m_Positions[index].x);
    var right = mesh.m_Indices.Max(index => mesh.m_Positions[index].x);
    for (var index = 0; index < mesh.m_Indices.Count; index += 3)
    {
        var a = mesh.m_Indices[index]; var b = mesh.m_Indices[index + 1]; var c = mesh.m_Indices[index + 2];
        AssertEqual(mesh.m_Colors[a], mesh.m_Colors[b], message + " 三角形跨颜色导致渐变。");
        AssertEqual(mesh.m_Colors[a], mesh.m_Colors[c], message + " 三角形跨颜色导致渐变。");
        // 直接回调中的料理、追加食材和酒水均以实际 X 坐标验色，覆盖每次目标刷新与精确配方绑定。
        var center = (mesh.m_Positions[a].x + mesh.m_Positions[b].x + mesh.m_Positions[c].x) / 3;
        var band = Math.Clamp((int)((center - left) / (right - left) * colors.Length), 0, colors.Length - 1);
        AssertEqual(colors[band], mesh.m_Colors[a], message + " 原背景颜色没有按从左到右的顺序显示。");
    }
}

static void AssertColor(Color expected, Color actual, string message)
{
    AssertEqualWithin(expected.r, actual.r, $"{message} Red channel differs.");
    AssertEqualWithin(expected.g, actual.g, $"{message} Green channel differs.");
    AssertEqualWithin(expected.b, actual.b, $"{message} Blue channel differs.");
    AssertEqualWithin(expected.a, actual.a, $"{message} Alpha channel differs.");
}

static void AssertEqualWithin(float expected, float actual, string message)
{
    ListColorBlockProbe.Assertions++;
    if (Math.Abs(expected - actual) > 0.001f)
    {
        throw new InvalidOperationException($"{message} Expected '{expected}', actual '{actual}'.");
    }
}

static void AssertContains(string actual, string expected, string message)
{
    ListColorBlockProbe.Assertions++;
    if (!actual.Contains(expected, StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"{message} Expected fragment '{expected}', actual '{actual}'.");
    }
}

static void AssertSequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual, string message)
{
    ListColorBlockProbe.Assertions++;
    if (!expected.SequenceEqual(actual))
    {
        throw new InvalidOperationException(
            $"{message} Expected '[{string.Join(",", expected)}]', actual '[{string.Join(",", actual)}]'.");
    }
}

static void AssertTrue(bool actual, string message)
{
    ListColorBlockProbe.Assertions++;
    if (!actual) throw new InvalidOperationException(message);
}

static void AssertFalse(bool actual, string message)
{
    ListColorBlockProbe.Assertions++;
    if (actual) throw new InvalidOperationException(message);
}

static void AssertThrows<TException>(Action action, string message)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException(message);
}

static void AssertEqual<T>(T expected, T actual, string message)
{
    ListColorBlockProbe.Assertions++;
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{message} Expected '{expected}', actual '{actual}'.");
    }
}
