using System.Text.Json;
using System.Text.Json.Nodes;
using BepInEx.Logging;
using MystiaStewardCompanion.Business.Application;
using MystiaStewardCompanion.LocalApi;
using MystiaStewardCompanion.Save;
using MystiaStewardCompanion.Ui;
using MystiaStewardCompanion.Updates;

internal static partial class GameFocusOverlayContractTests
{
    /// <summary>
    /// 用真实DTO和共享BusinessDisplayContext作对照，确认小投影没有遗漏来源语义。
    /// 所有变更保持订单本身完全相同，必须由来源检查撤回旧推荐，而不能碰巧依赖订单指纹。
    /// </summary>
    private static void VerifyFullSourceScope()
    {
        var snapshot = Mutate(Fixture(), node => node["specialBusiness"] = JsonNode.Parse(@"
            {""active"":true,""challengeTypeAvailable"":true,""challengeType"":""fixture"",""phase"":""order"",
             ""foodTargetTags"":[""素""],""beverageTargetTags"":[""水果""],""requiredExtraIngredientIds"":[1],
             ""yuumaFoodTargetRevision"":3,""wackyKoishiShieldBroken"":false,
             ""currentValue"":2,""maxValue"":100,""targetValue"":80,""currentAnger"":1,""targetTimeProgress"":0.2}"));
        var authority = new CompanionAuthorityDisplayScope("scope-registry", 7, 4, "profile-hash");
        GameFocusOverlaySourceScope Scope(LocalApiSnapshot live) => GameFocusOverlaySourceScope.FromSnapshot(live, authority, "catalog-body");
        var scope = Scope(snapshot);
        var expected = BusinessDisplayContext.Build(JsonSerializer.SerializeToNode(snapshot, JsonOptions)!.AsObject(),
            authority.RegistryId, authority.AuthorityRevision, authority.ActiveProfileRevision, authority.ActiveProfileHash);
        Check(scope.Context == expected.ToJsonString(), "有限来源投影与共享展示语义不一致。");
        var plans = GameFocusOverlayProjection.Capture(JsonSerializer.SerializeToNode(snapshot, JsonOptions)!.AsObject(),
            Results(snapshot), sourceScope: scope);
        GameFocusOverlayFrame Display(LocalApiSnapshot live, GameFocusOverlaySourceScope current) =>
            GameFocusOverlayProjection.Compose(live, plans, new() { Enabled = true }, sourceScope: current);
        Check(Display(snapshot, scope).Rows.All(row => !row.Text().Contains("加载中…")), "同作用域不能展示已完成方案。");

        foreach (var change in new Action<JsonObject>[] {
            node => node["automationSessionId"] = "another-session",
            node => node["runtimeLoaded"] = !snapshot.RuntimeLoaded,
            node => node["runtimeDaySceneGeneration"] = 9,
            node => node["runtimeDaySceneReady"] = true,
            node => node["specialBusiness"]!["active"] = false,
            node => node["specialBusiness"]!["challengeTypeAvailable"] = false,
            node => node["specialBusiness"]!["challengeType"] = "changed-challenge",
            node => node["specialBusiness"]!["phase"] = "another-phase",
            node => node["specialBusiness"]!["foodTargetTags"] = new JsonArray("肉"),
            node => node["specialBusiness"]!["beverageTargetTags"] = new JsonArray("高酒精"),
            node => node["specialBusiness"]!["requiredExtraIngredientIds"] = new JsonArray(5),
            node => node["specialBusiness"]!["yuumaFoodTargetRevision"] = 4,
            node => node["specialBusiness"]!["wackyKoishiShieldBroken"] = true,
            node => node["specialBusiness"]!["wackyKoishiFoodPreferenceTags"] = new JsonArray("甜"),
            node => node["specialBusiness"]!["wackyKoishiFoodHateTags"] = new JsonArray("肉"),
            node => node["specialBusiness"]!["wackyKoishiBeveragePreferenceTags"] = new JsonArray("水果"),
        }) {
            var live = Mutate(snapshot, change);
            var frame = Display(live, Scope(live));
            Check(frame.Visible && frame.Rows.Count == 2 && frame.Rows.All(row => row.Text().Contains("加载中…")),
                "来源变化未保留两笔现场订单并撤回旧方案。");
            Check(Scope(live).Context == BusinessDisplayContext.Build(JsonSerializer.SerializeToNode(live, JsonOptions)!.AsObject(),
                authority.RegistryId, authority.AuthorityRevision, authority.ActiveProfileRevision, authority.ActiveProfileHash).ToJsonString(),
                "来源变化后的精简映射与完整JSON语义不同。");
        }
        foreach (var changed in new[] {
            authority with { RegistryId = "another-registry" }, authority with { AuthorityRevision = 8 },
            authority with { ActiveProfileRevision = 5 }, authority with { ActiveProfileHash = "changed-hash" },
        }) Check(Display(snapshot, GameFocusOverlaySourceScope.FromSnapshot(snapshot, changed, "catalog-body"))
            .Rows.All(row => row.Text().Contains("加载中…")), "主设备/配置身份变化仍复用旧方案。");
        Check(Display(snapshot, scope with { CatalogJson = "catalog-body-before-signature" }).Rows.All(row => row.Text().Contains("加载中…")),
            "目录正文先于快照签名改变时仍暴露旧配方。");

        // 未破盾时进度变化不影响配方；破盾后伤害策略相关数值必须进入作用域。
        foreach (var field in new[] { "currentValue", "maxValue", "targetValue" }) {
            var progress = Mutate(snapshot, node => node["specialBusiness"]![field] = 999);
            Check(Scope(progress) == scope, "未破盾进度导致无谓清空方案：" + field);
            var broken = Mutate(snapshot, node => node["specialBusiness"]!["wackyKoishiShieldBroken"] = true);
            var changed = Mutate(broken, node => node["specialBusiness"]![field] = 999);
            Check(Scope(broken) != Scope(changed), "破盾后的伤害数值变化没有撤销旧展示：" + field);
        }
        foreach (var change in new Action<JsonObject>[] {
            node => node["capturedAtUtc"] = DateTime.UtcNow,
            node => node["snapshotSignature"] = "observation-only",
            node => node["specialBusiness"]!["currentAnger"] = 50,
            node => node["specialBusiness"]!["targetTimeProgress"] = 0.9,
            node => node["specialBusiness"]!["error"] = "diagnostic-only",
        }) {
            var live = Mutate(snapshot, change);
            Check(Scope(live) == scope && Display(live, Scope(live)).Rows.All(row => !row.Text().Contains("加载中…")),
                "无关观察字段导致已完成方案闪烁。");
        }
        var unknown = Mutate(snapshot, node => node["specialBusiness"]!["challengeTypeAvailable"] = false);
        Check(Scope(unknown) != Scope(Mutate(unknown, node => node["specialBusiness"]!["error"] = "unknown changed")),
            "未知挑战来源错误变化必须遵循共享作用域语义。");
    }

    /// <summary>
    /// 使用真实宿主与存储但不Start：没有监听器、后台业务线程、Unity或游戏读写。
    /// 私有测试桥仅调度现有生产捕获/准备/提交路径，用确定顺序重现并发时旧结果被拒绝的窗口。
    /// </summary>
    private static void VerifySourcePublication(string root)
    {
        var path = Path.Combine(root, "source-scope"); Directory.CreateDirectory(path);
        var log = Logger.CreateLogSource("focus-scope-isolated");
        var effects = 0;
        Exception Forbidden() { effects++; return new InvalidOperationException("来源回归禁止游戏/更新动作。"); }
        try {
            var profile = FocusTestProfile();
            var authority = new CompanionDeviceAuthorityStore(Path.Combine(path, "devices.json"), log);
            const string client = "focus-primary-0001";
            CompanionDeviceRegisterRequest Registration(string version = "test") => new() {
                ProtocolVersion = 1, ProfileSchemaVersion = CompanionDeviceAuthorityStore.ProfileSchemaVersion,
                Platform = "windows", AppVersion = version, Profile = JsonSerializer.SerializeToElement(profile),
            };
            var state = authority.Register(client, "fixture", Registration(), DateTime.UtcNow);
            var initialAuthority = authority.DisplayScope;
            Check(initialAuthority.RegistryId == state.RegistryId && initialAuthority.ActiveProfileHash == state.ActiveProfileHash,
                "提交后的轻量身份与真实生效配置不同。");
            authority.Register(client, "fixture", Registration("metadata-only"), DateTime.UtcNow);
            Check(ReferenceEquals(initialAuthority, authority.DisplayScope), "仅设备资料提交导致轻量身份引用变化。");
            var reopened = new CompanionDeviceAuthorityStore(Path.Combine(path, "devices.json"), log);
            Check(reopened.DisplayScope == initialAuthority, "重新加载未恢复完整轻量身份。");
            // 原子替换失败时，不能让无锁展示看到没有成功保存的配置。使用本测试独占文件的
            // 真实Windows共享模式触发失败，不改ACL、不读取用户设备记录。
            var failedProfile = (JsonObject)Clone(profile)!; failedProfile["recommendationBudgetPolicy"] = "ignore";
            var failed = false;
            using (var held = new FileStream(Path.Combine(path, "devices.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try { authority.UpdatePrimaryProfile(client, new() {
                    ProtocolVersion = 1, ProfileSchemaVersion = CompanionDeviceAuthorityStore.ProfileSchemaVersion,
                    ExpectedAuthorityRevision = state.AuthorityRevision, ExpectedProfileRevision = state.ActiveProfileRevision,
                    Profile = JsonSerializer.SerializeToElement(failedProfile),
                }, DateTime.UtcNow); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { failed = true; }
            }
            Check(failed && ReferenceEquals(initialAuthority, authority.DisplayScope), "未成功落盘的配置发布了新展示身份。");
            Check(new CompanionDeviceAuthorityStore(Path.Combine(path, "devices.json"), log).DisplayScope == initialAuthority,
                "原子写失败改变了原设备文件。");
            using var update = new UpdateService(new UpdateServiceSettings { Enabled = false, AutoCheck = false }, log,
                Path.Combine(path, "updates"), () => DateTime.UtcNow, _ => throw Forbidden());
            using var server = new LocalApiServer(false, "auto", 19877, "focus-test", "synthetic-focus-token",
                () => throw Forbidden(), (_, _) => throw Forbidden(), _ => throw Forbidden(),
                () => throw Forbidden(), _ => throw Forbidden(), _ => throw Forbidden(),
                (_, _, _, _, _) => throw Forbidden(), (_, _, _, _) => throw Forbidden(),
                _ => throw Forbidden(), _ => throw Forbidden(), _ => throw Forbidden(), 1, _ => 0, _ => throw Forbidden(),
                () => throw Forbidden(), () => RuntimeAvailableMissionSnapshot.Unavailable(0, 0, RuntimeAvailableMissionSnapshot.NotAttachedStatus, "synthetic"),
                (_, _) => throw Forbidden(), (_, _, _, _) => throw Forbidden(), (_, _, _, _) => throw Forbidden(),
                update, new FavoriteStore(Path.Combine(path, "favorites.json"), log),
                new CustomRecipeStore(Path.Combine(path, "custom.json"), log), authority, log);
            Update(server.FocusOverlayStore, "{\"enabled\":true}");
            var snapshot = Fixture();
            server.SetRuntimeDataJson("{\"isComplete\":true}");
            server.SetSnapshotJson(JsonSerializer.Serialize(snapshot, JsonOptions), "first");
            var scope = server.ReadFocusOverlaySourceScope(snapshot);
            for (var index = 0; index < 10; index++) server.ReadFocusOverlaySourceScope(snapshot);
            var before = GC.GetAllocatedBytesForCurrentThread();
            var stable = true;
            for (var index = 0; index < 10000; index++) stable &= ReferenceEquals(scope, server.ReadFocusOverlaySourceScope(snapshot));
            var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Check(stable && bytes == 0, $"稳定帧scope读取分配或替换引用：{bytes}字节。");
            var observation = Mutate(snapshot, node => node["capturedAtUtc"] = DateTime.UtcNow);
            server.SetSnapshotJson(JsonSerializer.Serialize(observation, JsonOptions), "second");
            Check(ReferenceEquals(scope, server.ReadFocusOverlaySourceScope(observation)), "无关采样换帧未复用scope引用。");
            var frame = server.CaptureFocusFrameForTest();
            var result = Results(observation);
            server.PrepareFocusPublicationForTest(frame, result);
            Check(ReferenceEquals(server.FocusOverlayPlans, GameFocusOverlayPlans.Empty), "准备但尚未接受的业务结果提前发布了投影。");
            Check(server.CompleteFocusDisplayForTest(frame, result), "已验证完成结果未接受。");
            var accepted = server.FocusOverlayPlans;
            Check(accepted.SourceScope == scope, "业务发布与实时完整作用域不匹配。");

            // 更晚业务结果已经被接受后，再让旧准备结果走完成路径；它不得覆盖新投影。
            var newer = Mutate(observation, node => node["capturedAtUtc"] = DateTime.UtcNow.AddSeconds(1));
            server.SetSnapshotJson(JsonSerializer.Serialize(newer, JsonOptions), "third");
            var newerFrame = server.CaptureFocusFrameForTest();
            Check(server.CompleteFocusDisplayForTest(newerFrame, Results(newer)), "新完成结果未接受。");
            var newest = server.FocusOverlayPlans;
            var oldChanged = Results(observation); oldChanged["normalOrderDetailPlans"]![0]!["executionFood"]!["name"] = "拒绝的旧结果";
            Check(!server.CompleteFocusDisplayForTest(frame, oldChanged) && ReferenceEquals(newest, server.FocusOverlayPlans),
                "被新版本拒绝的准备结果覆盖了已接受投影。");

            profile["recommendationBudgetPolicy"] = "warn";
            state = authority.UpdatePrimaryProfile(client, new() { ProtocolVersion = 1,
                ProfileSchemaVersion = CompanionDeviceAuthorityStore.ProfileSchemaVersion,
                ExpectedAuthorityRevision = state.AuthorityRevision, ExpectedProfileRevision = state.ActiveProfileRevision,
                Profile = JsonSerializer.SerializeToElement(profile) }, DateTime.UtcNow);
            var changedScope = server.ReadFocusOverlaySourceScope(newer);
            Check(!ReferenceEquals(scope, changedScope) && changedScope != scope, "主配置提交后未立即失效来源。");
            Check(!server.CompleteFocusDisplayForTest(frame, result) && ReferenceEquals(newest, server.FocusOverlayPlans),
                "配置变更前的计算继续发布到了新作用域。");
            var display = GameFocusOverlayProjection.Compose(newer, newest, new() { Enabled = true }, sourceScope: changedScope);
            Check(display.Rows.Count == 2 && display.Rows.All(row => row.Text().Contains("加载中…")), "配置变化没有保留现场订单并使用占位。");
            server.SetRuntimeDataJson("{\"isComplete\":true,\"version\":2}");
            Check(server.ReadFocusOverlaySourceScope(newer) != changedScope, "目录先更新时轻量来源未变化。");
            var blockedSource = Mutate(newer, node => node["specialBusiness"] = JsonSerializer.SerializeToNode(
                new MystiaStewardCompanion.Core.SpecialBusinessContext { Active = true, ChallengeTypeAvailable = true, Phase = "new-target" }, JsonOptions));
            server.SetSnapshotJson(JsonSerializer.Serialize(blockedSource, JsonOptions), "special-source-change");
            Check(!server.CompleteFocusDisplayForTest(newerFrame, Results(newer)), "特殊经营来源变化后仍接受旧完成结果。");
            Check(effects == 0, "只读来源测试触发了游戏或更新委托。");
            Console.WriteLine($"SCOPE-STABILITY: 10000 reads, {bytes} allocated bytes; no locks or game execution.");
        }
        finally { Logger.Sources.Remove(log); }
    }

    /// <summary>显式合成当前协议的完整配置，全部自动化关闭；不借用用户配置或凭据。</summary>
    private static JsonObject FocusTestProfile()
    {
        var fields = new[] { "automationEnabled", "gameUiEnabled", "rareGameUiEnabled", "normalGameUiEnabled", "rareGuestParticipationModuleEnabled",
            "autoRareOrderEnabled", "autoNormalOrderEnabled", "autoNormalTakeBeverage", "autoNormalStartCooking", "autoNormalDeliverFood",
            "autoNormalCompleteOrder", "autoNormalStopOnError", "autoPrepCompleteOrder", "autoPrepTakeBeverage", "autoPrepStartCooking",
            "autoPrepCollectCooking", "autoPrepRecipeFavoritesOnly", "autoPrepBeverageFavoritesOnly", "autoPrepStopOnError", "filterMissingCookers",
            "missionRecipePriorityEnabled", "pinFavoriteRecipeEnabled", "pinFavoriteBeverageEnabled", "rareGameUiPinningEnabled", "normalGameUiPinningEnabled",
            "rareRecipeVariantEnabled", "normalRecipeVariantEnabled", "rareCookerHighlightEnabled", "normalCookerHighlightEnabled",
            "rareSeatHighlightEnabled", "normalSeatHighlightEnabled", "rareOrderHighlightEnabled", "normalOrderHighlightEnabled" };
        var profile = new JsonObject(); foreach (var field in fields) profile[field] = false;
        foreach (var field in new[] { "autoRareConcurrency", "autoNormalConcurrency", "autoMaxStepRetries", "recipeVariantLimitPerBase" }) profile[field] = 1;
        profile["autoMaxRollbacks"] = 0;
        profile["rareOrderHighlightPalette"] = new JsonArray("#FF8800", "#FFCC44");
        profile["normalOrderHighlightPalette"] = new JsonArray("#4488FF", "#44CCFF");
        profile["managedRareGuestIds"] = new JsonArray();
        profile["rareGuestParticipationAutoResumeEnabled"] = false; profile["rareGuestParticipationAutoResumeThresholdPercent"] = 30; profile["rareGuestParticipationAutoResumePosition"] = "tail"; profile["recommendationBudgetPolicy"] = "block";
        profile["recommendationExclusions"] = new JsonObject { ["excludedIngredientIds"] = new JsonArray(), ["excludedBeverageIds"] = new JsonArray() };
        var keys = new[] { "foodPreference", "beveragePreference", "negativeRisk", "extraCount", "resourcePressure", "totalCost", "profit", "beverageStock" };
        profile["recommendationSortProfile"] = new JsonObject { ["preset"] = "custom", ["objectives"] = new JsonArray(keys.Select(key =>
            (JsonNode)new JsonObject { ["key"] = key, ["enabled"] = true, ["weight"] = 50,
                ["direction"] = key is "negativeRisk" or "extraCount" or "resourcePressure" or "totalCost" ? "asc" : "desc" }).ToArray()) };
        return profile;
    }
}

namespace MystiaStewardCompanion.LocalApi
{
    /// <summary>仅测试项目编译的窄桥接，不增加生产HTTP入口，不替换任何校验或发布规则。</summary>
    internal sealed partial class LocalApiServer
    {
        internal object CaptureFocusFrameForTest() => CaptureBusinessFrame();
        internal void PrepareFocusPublicationForTest(object frame, JsonObject result)
            => CreateBusinessPublication((BusinessFrame)frame, result, new JsonArray(), new JsonObject(), 0);
        internal bool CompleteFocusDisplayForTest(object frame, JsonObject result)
            => PublishCompletedBusinessDisplay((BusinessFrame)frame, result, 0);
    }
}
