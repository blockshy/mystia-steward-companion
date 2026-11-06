using System.Text.Json;
using System.Text.Json.Nodes;
using MystiaStewardCompanion.Core;
using MystiaStewardCompanion.LocalApi;
using MystiaStewardCompanion.Ui;

/// <summary>
/// 专注层协议的离线回归：链接完整生产Mod源码，但只调用纯托管设置仓库与展示投影。
/// 所有文件均位于本次测试的独占临时目录；不读取真实配置，不创建Unity对象，不执行游戏动作。
/// 断言针对用户可观察结果、持久化事务和精确身份边界，不复制生产投影实现作为预期值。
/// </summary>
internal static partial class GameFocusOverlayContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static int _assertions;
    private static int _scenarios;

    internal static int Run()
    {
        _assertions = 0;
        _scenarios = 0;
        var root = Path.Combine(Path.GetTempPath(), "mystia-focus-contracts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var failures = new List<string>();
        try
        {





            Scenario("版本2设置仓库", () => Check(GameFocusOverlayStoreContractTests.Run() == 0, "设置仓库回归失败。"));
            Scenario("主方案文本与占位", VerifyPlanText);
            Scenario("身份、代次、场景及目录隔离", VerifyIdentityAndContext);
            Scenario("需求指纹和非业务采样字段", VerifyDemandFingerprint);
            Scenario("重复身份与部分采集", VerifyIncompleteAndDuplicates);
            Scenario("混合排序及稀客优先", VerifySorting);
            Scenario("调度暂停尾排、恢复及非暂停组内顺序", VerifyParticipation);
            Scenario("同稀客多生命周期全量显示与旧单隔离", VerifyPausedMultiOrderDisplay);
            Scenario("黑暗料理与保温箱文本", VerifyDarkCuisine);
            Scenario("真实生产业务入口投影", VerifyProductionPayload);
            Scenario("九项内容选择与标签结构", VerifyRichContent);
            Scenario("精确图片身份及配方依赖", VerifyRichImages);
            Scenario("资源代次切换", VerifyRichMediaGeneration);
            Scenario("特殊产物基底图片与成品供给", VerifyRichSpecialProducts);
            Scenario("订单底色与方案独立", VerifyRichColors);
            Scenario("不完整目录和未知基础配方", VerifyRichUnknowns);
            Scenario("完整展示来源及无关进度复用", VerifyFullSourceScope);
            Scenario("来源轻量缓存及业务提交边界", () => VerifySourcePublication(root));
            foreach (var failure in failures) Console.Error.WriteLine("FAIL: " + failure);
            Console.WriteLine($"{(failures.Count == 0 ? "PASS" : "FAIL")}: game focus overlay contracts; {_scenarios} scenarios, {_assertions} assertions, {failures.Count} failures; no game execution.");
            return failures.Count == 0 ? 0 : 1;
        }
        finally
        {
            // 只删除本测试刚创建的已知绝对路径，失败证据已写入标准错误，绝不清理用户目录。
            Directory.Delete(root, recursive: true);
        }

        void Scenario(string name, Action action)
        {
            _scenarios++;
            try { action(); Console.WriteLine("PASS: " + name); }
            catch (Exception ex) { failures.Add(name + " — " + ex); }
        }
    }

    private static void VerifyPlanText()
    {
        var snapshot = Fixture();
        var result = Results(snapshot);
        var plans = Capture(snapshot, result);
        var frame = Compose(snapshot, plans);
        Check(frame.Visible && frame.Rows.Count == 2, "有效经营中的两类订单应可见。");
        Check(frame.Rows[0].Heading == "1 桌 · 测试普客" && frame.Rows[0].Badges.Last().Text == "待送达", "普客桌号与状态应位于标题行。");
        Check(frame.Rows[1].Heading == "2 桌 · 测试稀客" && frame.Rows[1].Badges.Last().Text == "待送达", "稀客桌号应由零基转为一基。");
        Contains(frame.Rows[0].Text(), "料理 · 煎豆腐", "厨具 · 油锅", "配方 · 豆腐、鸡蛋", "加料 · 海苔", "酒水 · 绿茶");
        Contains(frame.Rows[1].Text(), "料理 · 味噌汤", "厨具 · 煮锅", "配方 · 豆腐、海苔", "加料 · 蘑菇", "酒水 · 果汁");
        // 捕获之后原始JSON可被后台调用者释放或修改；已发布短文本必须保持独立。
        result["normalOrderDetailPlans"]![0]!["executionFood"]!["name"] = "意外后写";
        Check(Compose(snapshot, plans).Rows[0].Text().Contains("料理 · 煎豆腐"), "后台输入后写污染了已冻结方案。");
        var pending = Compose(snapshot, GameFocusOverlayPlans.Empty);
        Check(pending.Rows.All(row => row.Text().Contains("料理 · 加载中…") && row.Text().Contains("酒水 · 加载中…")), "尚无方案时必须保留统一占位结构。");
        Check(pending.Rows.All(row => row.Text().Split('\n').Length == 6), "占位行数与正式方案不一致，易造成界面跳动。");
        var empty = Results(snapshot);
        empty["recommendations"]![0]!["executionPlans"] = new JsonArray();
        empty["normalOrderDetailPlans"]![0]!["executionFood"] = null;
        empty["normalOrderDetailPlans"]![0]!["executionBeverage"] = null;
        Check(Compose(snapshot, Capture(snapshot, empty)).Rows.All(row => row.Text().Contains("暂无主方案")), "求解完成但无候选不应永久显示加载中。");
        var served = Mutate(snapshot, root =>
        {
            root["nightBusiness"]!["orders"]![0]!["hasServedFood"] = true;
            root["normalBusiness"]!["orders"]![0]!["readyToEvaluate"] = true;
        });
        var servedFrame = Compose(served, Capture(served, Results(served)));
        Check(servedFrame.Rows[0].Badges.Last().Text == "待评价" && servedFrame.Rows[1].Badges.Last().Text == "已有料理", "进度状态没有保留在订单顶部。");
    }

    private static void VerifyIdentityAndContext()
    {
        var snapshot = Fixture();
        var plans = Capture(snapshot, Results(snapshot));
        foreach (var change in new Action<JsonObject>[]
        {
            root => root["nightBusinessGeneration"] = 62,
            root => root["activeSceneName"] = "其他经营场景",
            root => root["runtimeDataSignature"] = "其他目录",
        })
            Check(Compose(Mutate(snapshot, change), plans).Rows.All(row => row.Text().Contains("加载中…")), "不同代次/场景/目录错误复用了旧推荐。");
        foreach (var change in new Action<JsonObject>[]
        {
            root => root["normalBusiness"]!["orders"]![0]!["traceId"] = "N-0009",
            root => root["normalBusiness"]!["orders"]![0]!["orderLifecycleSequence"] = 999,
            root => root["normalBusiness"]!["orders"]![0]!["guestId"] = 999,
            root => root["normalBusiness"]!["orders"]![0]!["orderKey"] = "different-controller",
            root => root["nightBusiness"]!["orders"]![0]!["traceId"] = "R-0009",
            root => root["nightBusiness"]!["orders"]![0]!["orderLifecycleSequence"] = 999,
            root => root["nightBusiness"]!["orders"]![0]!["guestId"] = 999,
        })
        {
            var frame = Compose(Mutate(snapshot, change), plans);
            Check(frame.Rows.Count == 2 && frame.Rows.Count(row => row.Text().Contains("加载中…")) == 1, "精确身份变化必须仅撤销对应订单的旧方案。");
        }
        foreach (var phase in new[] { "Inactive", "Closing", "Destroyed", "Preparing" })
            Check(!Compose(Mutate(snapshot, root => root["nightBusinessLifecyclePhase"] = phase), plans).Visible, "非Active生命周期不得显示：" + phase);
        Check(!Compose(Mutate(snapshot, root => root["nightBusinessGeneration"] = 0), plans).Visible, "未确认经营代次不得显示。");
        Check(!GameFocusOverlayProjection.Compose(snapshot, plans, new()).Visible, "默认关闭设置不得展示。");
    }

    private static void VerifyDemandFingerprint()
    {
        var snapshot = Fixture();
        var plans = Capture(snapshot, Results(snapshot));
        foreach (var change in new Action<JsonObject>[]
        {
            root => root["nightBusiness"]!["orders"]![0]!["foodTag"] = "肉",
            root => root["nightBusiness"]!["orders"]![0]!["beverageTag"] = "高酒精",
            root => root["nightBusiness"]!["orders"]![0]!["fund"] = 5,
            root => root["nightBusiness"]!["orders"]![0]!["hasServedBeverage"] = true,
            root => root["nightBusiness"]!["orders"]![0]!["specialBusinessRole"] = "changed-role",
            root => root["nightBusiness"]!["orders"]![0]!["missionRecipePriority"] = new JsonObject { ["foodId"] = -1, ["productKind"] = "dark-cuisine" },
            root => root["normalBusiness"]!["orders"]![0]!["foodId"] = 999,
            root => root["normalBusiness"]!["orders"]![0]!["beverageId"] = 999,
            root => root["normalBusiness"]!["orders"]![0]!["hasServedFood"] = true,
            root => root["normalBusiness"]!["orders"]![0]!["foodPreferenceTags"] = new JsonArray("新需求"),
        })
            Check(Compose(Mutate(snapshot, change), plans).Rows.Count(row => row.Text().Contains("加载中…")) == 1, "需求变动未撤销旧主方案。");
        var sampled = Mutate(snapshot, root =>
        {
            root["nightBusiness"]!["orders"]![0]!["lastSeenAtUtc"] = DateTime.UnixEpoch.AddDays(1);
            root["nightBusiness"]!["orders"]![0]!["source"] = "new-source";
            root["normalBusiness"]!["orders"]![0]!["source"] = "new-source";
        });
        Check(Compose(sampled, plans).Rows.All(row => !row.Text().Contains("加载中…")), "仅采集来源/最后观察时间变化不应清空方案。");
        foreach (var ratio in new double?[] { 1, .3, .01, 0, null })
        {
            var patience = Mutate(snapshot, root =>
            {
                root["nightBusiness"]!["orders"]![0]!["patienceRemainingRatio"] = ratio;
                root["normalBusiness"]!["orders"]![0]!["patienceRemainingRatio"] = ratio;
            });
            Check(Compose(patience, plans).Rows.All(row => !row.Text().Contains("加载中…")),
                "耐心条自然变化/暂不可读不能撤销现有制作方案。");
        }
        var result = Results(snapshot);
        foreach (var row in new[] { result["recommendations"]![0]!, result["normalOrderDetailPlans"]![0]! })
        {
            row["order"]!["serviceOrderPosition"] = 99;
            // 字段顺序不同不能误判需求变化：JSON对象是语义映射，不是原始字符串签名。
            var old = row["order"]!.AsObject();
            row["order"] = new JsonObject(old.Reverse().Select(pair => KeyValuePair.Create(pair.Key, Clone(pair.Value))));
        }
        Check(Compose(snapshot, Capture(snapshot, result)).Rows.All(row => !row.Text().Contains("加载中…")), "显示槽位或JSON字段顺序错误影响需求指纹。");
    }

    private static void VerifyIncompleteAndDuplicates()
    {
        var snapshot = Fixture();
        var plans = Capture(snapshot, Results(snapshot));
        foreach (var field in new[] { "nightBusiness", "normalBusiness" })
        {
            foreach (var error in new[] { false, true })
            {
                var changed = Mutate(snapshot, root =>
                {
                    if (error) root[field]!["error"] = "synthetic incomplete";
                    else root[field]!["ordersReadComplete"] = false;
                });
                var frame = Compose(changed, plans);
                Check(frame.Rows.Count == 1, "部分采集必须收起未知通道、保留另一通道。");
            }
            var duplicated = Mutate(snapshot, root => root[field]!["orders"]!.AsArray().Add(Clone(root[field]!["orders"]![0])));
            Check(Compose(duplicated, plans).Rows.Count == 1, "重复现场身份不能任意选择一条展示。");
        }
        foreach (var field in new[] { "recommendations", "normalOrderDetailPlans" })
        {
            var result = Results(snapshot);
            result[field]!.AsArray().Add(Clone(result[field]![0]));
            Check(Compose(snapshot, Capture(snapshot, result)).Rows.Count(row => row.Text().Contains("加载中…")) == 1, "重复推荐身份必须撤销其缓存，不得取第一条。");
        }
        foreach (var change in new Action<JsonObject>[]
        {
            root => root["normalBusiness"]!["orders"]![0]!["orderKey"] = "",
            root => root["normalBusiness"]!["orders"]![0]!["traceId"] = "",
            root => root["nightBusiness"]!["orders"]![0]!["guestId"] = null,
            root => root["nightBusiness"]!["orders"]![0]!["orderLifecycleSequence"] = 0,
        })
            Check(Compose(Mutate(snapshot, change), plans).Rows.Count == 1, "不完整现场身份不得通过名称/桌号补造显示身份。");
    }

    private static void VerifySorting()
    {
        var snapshot = SortingFixture();
        var plans = Capture(snapshot, Results(snapshot));
        Equal(Traces(Compose(snapshot, plans)), new[] { "N-0001", "R-0002", "N-0003", "R-0004" }, "两通道必须按首次点单时间交错显示。");
        Equal(Traces(Compose(snapshot, plans, new() { Enabled = true, Content = new() { RareFirst = true } })), new[] { "R-0002", "R-0004", "N-0001", "N-0003" }, "稀客优先只分组，不改变组内次序。");
        Equal(Traces(Compose(snapshot, plans, new() { Enabled = true, Content = new() { ShowRare = false } })), new[] { "N-0001", "N-0003" }, "关闭稀客显示不应改变普客顺序。");
        Equal(Traces(Compose(snapshot, plans, new() { Enabled = true, Content = new() { ShowNormal = false } })), new[] { "R-0002", "R-0004" }, "关闭普客显示不应改变稀客顺序。");
        var neither = Compose(snapshot, plans, new() { Enabled = true, Content = new() { ShowNormal = false, ShowRare = false } });
        Check(neither.Rows.Count == 0, "全部类型关闭应有准确空状态。");
    }

    private static void VerifyParticipation()
    {
        var snapshot = SortingFixture();
        var plans = Capture(snapshot, Results(snapshot));
        var reordered = WithQueue(snapshot, "R-0004", "R-0002");
        Equal(Traces(Compose(reordered, plans)), new[] { "N-0001", "R-0004", "N-0003", "R-0002" }, "队列优先级只能置换稀客原显示槽位。");
        var paused = WithQueue(snapshot, "R-0004");
        var pausedFrame = Compose(paused, plans);
        Equal(Traces(pausedFrame), new[] { "N-0001", "R-0004", "N-0003", "R-0002" }, "暂停只撤销参与资格，显示列表仍须保留全部当前订单及固定普客槽位。");
        Check(pausedFrame.Rows.Single(row => row.Identity.Contains(":R-0002:")).Badges[0].Text == "调度暂停"
            && pausedFrame.Rows.All(row => row.Badges.Count == 2), "暂停应替换类型徽标，不能挤掉阶段或堆叠第三个徽标。");
        var resumed = Compose(WithQueue(snapshot, "R-0002", "R-0004"), plans);
        Equal(Traces(resumed), new[] { "N-0001", "R-0002", "N-0003", "R-0004" }, "恢复队列后应保持全部精确订单，不需要重做推荐。");
        Check(resumed.Rows.All(row => row.Badges[0].Text != "调度暂停"), "恢复后仍残留暂停徽标。");
        var allPaused = Compose(WithQueue(snapshot), plans);
        Equal(Traces(allPaused), new[] { "N-0001", "N-0003", "R-0002", "R-0004" }, "全部暂停稀客须保留并排在所有非暂停普客之后。");
        Check(allPaused.Rows.Where(row => row.Identity.Contains(":True:")).All(row => row.Badges[0].Text == "调度暂停"), "全部暂停缺少精确状态说明。");
        Equal(Traces(Compose(WithQueue(snapshot), plans, new() { Enabled = true, Content = new() { RareFirst = true } })),
            new[] { "N-0001", "N-0003", "R-0002", "R-0004" }, "稀客优先不得把暂停稀客移到仍需制作的普客前。");
        Equal(Traces(Compose(paused, plans, new() { Enabled = true, Content = new() { RareFirst = true } })),
            new[] { "R-0004", "N-0001", "N-0003", "R-0002" }, "参与稀客优先、普客其次、暂停稀客最后，组内次序须稳定。");
        foreach (var change in new Action<JsonObject>[]
        {
            root => root["rareGuestParticipation"]!["active"] = false,
            root => root["rareGuestParticipation"]!["ready"] = false,
            root => root["rareGuestParticipation"]!["businessGeneration"] = 60,
            root => root["rareGuestParticipation"]!["displayOrderKeys"]![0]!["businessGeneration"] = 60,
            root => root["rareGuestParticipation"]!["displayOrderKeys"]![0]!["orderLifecycleSequence"] = 999,
            root => root["rareGuestParticipation"]!["displayOrderKeys"]![0]!["traceId"] = "",
            root => root["rareGuestParticipation"]!["displayOrderKeys"]!.AsArray().RemoveAt(1),
            root => root["rareGuestParticipation"]!["displayOrderKeys"]!.AsArray().Clear(),
            root => root["rareGuestParticipation"]!["displayOrderKeys"] = null,
            root => root["rareGuestParticipation"]!["displayOrderKeys"]!.AsArray().Add(Clone(root["rareGuestParticipation"]!["displayOrderKeys"]![0])),
        })
        {
            var frame = Compose(Mutate(reordered, change), plans);
            Equal(Traces(frame), new[] { "N-0001", "R-0002", "N-0003", "R-0004" }, "失配/缺项/空队列必须回退当前现场时间顺序，不能过滤订单。");
            Check(frame.Rows.All(row => !row.Text().Contains("加载中…")), "显示队列异常不应撤销仍精确匹配的只读方案。");
        }
        // 暂停标签只能读取当前代次唯一精确entry；队列顺序本身或同guestId都不足以证明暂停。
        foreach (var change in new Action<JsonObject>[]
        {
            root => root["rareGuestParticipation"]!["active"] = false,
            root => root["rareGuestParticipation"]!["ready"] = false,
            root => root["rareGuestParticipation"]!["businessGeneration"] = 60,
            root => root["rareGuestParticipation"]!["entries"]!.AsArray().Clear(),
            root => root["rareGuestParticipation"]!["entries"]![1]!["managed"] = false,
            root => root["rareGuestParticipation"]!["entries"]![1]!["participating"] = true,
            root => root["rareGuestParticipation"]!["entries"]![1]!["orderLifecycleSequence"] = 999,
            root => root["rareGuestParticipation"]!["entries"]![1]!["guestId"] = 999,
            root => root["rareGuestParticipation"]!["entries"]!.AsArray().Add(Clone(root["rareGuestParticipation"]!["entries"]![1])),
        })
        {
            var frame = Compose(Mutate(paused, change), plans);
            Check(frame.Rows.Count == 4 && frame.Rows.Single(row => row.Identity.Contains(":R-0002:")).Badges[0].Text == "稀客",
                "缺少当前唯一暂停证据时错误标注暂停或隐藏订单。");
        }
        var rareHidden = Compose(reordered, plans, new() { Enabled = true, Content = new() { ShowRare = false } });
        Check(rareHidden.Rows.Count == 2, "用户关闭稀客显示后不应误报调度同步错误。");
    }

    /// <summary>复现同一稀客四张现场订单；生命周期是合成测试值，不冒充日志中未提供的数据。</summary>
    private static void VerifyPausedMultiOrderDisplay()
    {
        var snapshot = Mutate(Fixture(), root =>
        {
            var orders = new JsonArray();
            foreach (var (trace, life, desk, seconds) in new[] {
                ("R-0005", 1005, 7, 5), ("R-0022", 1022, 1, 22),
                ("R-0024", 1024, 2, 24), ("R-0025", 1025, 5, 25) })
            {
                var value = JsonSerializer.SerializeToNode(Rare(trace, life, desk, seconds), JsonOptions)!;
                value["guestId"] = 30; value["runtimeGuestId"] = 30; value["guestName"] = "测试同一稀客";
                orders.Add(value);
            }
            root["nightBusiness"]!["orders"] = orders;
        });
        var plans = Capture(snapshot, Results(snapshot));
        var paused = WithQueue(snapshot);
        var frame = Compose(paused, plans);
        Equal(Traces(frame), new[] { "N-0001", "R-0005", "R-0022", "R-0024", "R-0025" }, "同guestId四单不能按客人去重或因全部暂停消失。");
        Check(frame.Rows.Skip(1).All(row => row.Badges[0].Text == "调度暂停" && row.Badges.Count == 2
            && row.Text().Contains("料理 · 味噌汤")), "暂停订单应保留阶段及当前只读方案。");
        var resumed = Compose(WithQueue(snapshot, "R-0024"), plans);
        Equal(Traces(resumed), new[] { "N-0001", "R-0024", "R-0005", "R-0022", "R-0025" }, "恢复一单应移到暂停订单前方，仍完整保留全部现场订单。");
        Check(resumed.Rows.Single(row => row.Identity.Contains(":R-0024:")).Badges[0].Text == "稀客"
            && resumed.Rows.Count(row => row.Badges[0].Text == "调度暂停") == 3, "恢复一单错误影响同guestId的其它生命周期。");
        Equal(Traces(Compose(WithQueue(snapshot), plans)), Traces(frame), "重新暂停后未按原点单顺序显示全部订单。");
        var left = Mutate(paused, root => root["nightBusiness"]!["orders"]!.AsArray().RemoveAt(1));
        Equal(Traces(Compose(left, plans)), new[] { "N-0001", "R-0005", "R-0024", "R-0025" }, "旧队列或旧推荐复活已离开的订单。");
        var replaced = Mutate(paused, root => root["nightBusiness"]!["orders"]![1]!["orderLifecycleSequence"] = 9022);
        var replacement = Compose(replaced, plans).Rows.Single(row => row.Identity.Contains(":R-0022:"));
        Check(replacement.Badges[0].Text == "稀客" && replacement.Text().Contains("加载中…"),
            "相同trace/guest的新生命周期继承旧暂停或旧推荐。");
        var incomplete = Mutate(paused, root => root["nightBusiness"]!["ordersReadComplete"] = false);
        Equal(Traces(Compose(incomplete, plans)), new[] { "N-0001" }, "现场集合未知时不能借旧队列恢复稀客。");
    }

    private static void VerifyDarkCuisine()
    {
        var snapshot = Fixture();
        foreach (var stored in new[] { false, true })
        {
            var result = Results(snapshot);
            var food = result["recommendations"]![0]!["executionPlans"]![0]!["food"]!.AsObject();
            food["productKind"] = "dark-cuisine";
            food["supplySource"] = stored ? "stored" : "craft";
            food["recipe"] = new JsonObject { ["name"] = "黑暗料理" };
            food["productionRecipe"] = new JsonObject { ["name"] = "凉拌豆腐", ["cooker"] = "料理台", ["ingredients"] = new JsonArray("豆腐") };
            food["extraIngredients"] = new JsonArray(new JsonObject { ["name"] = "蝉蜕" });
            var text = Compose(snapshot, Capture(snapshot, result)).Rows.Single(row => row.Identity.Contains(":True:")).Text();
            if (stored) Contains(text, "料理 · 黑暗料理", "供给 · 保温箱成品");
            else Contains(text, "料理 · 黑暗料理", "基底 · 凉拌豆腐", "厨具 · 料理台", "配方 · 豆腐", "加料 · 蝉蜕");
            Check(!text.Contains("料理 #-1") && !text.Contains("配方 · 待确认"), "黑暗料理不能回落到不存在的目录配方。");
        }
    }

    private static void VerifyProductionPayload()
    {
        // 此样本由真实DTO和生产OrderRecommendationService生成，覆盖业务层实际附加字段，
        // 防止只有手写JSON通过、真实客户端输入却因默认字段差异而永久显示“加载中…”。
        using var profile = JsonDocument.Parse("{}");
        var fixture = OrderPresentationFixture.Build(profile.RootElement);
        var source = fixture["snapshot"]!.AsObject();
        source["nightBusinessGeneration"] = 61;
        source["nightBusinessLifecyclePhase"] = "Active";
        source["activeSceneName"] = "production-fixture";
        source["runtimeDataSignature"] = "catalog-production";
        source["nightBusiness"]!["ordersReadComplete"] = true;
        source["normalBusiness"]!["ordersReadComplete"] = true;
        var snapshot = source.Deserialize<LocalApiSnapshot>(JsonOptions)!;
        // 生产发布的是完整DTO；该引擎小夹具原始只含订单，需要补齐DTO默认来源字段后再捕获。
        var plans = Capture(snapshot, fixture["result"]!.AsObject());
        var frame = Compose(snapshot, plans);
        Check(frame.Rows.Count == 2 && frame.Rows.All(row => !row.Text().Contains("加载中…")), "真实生产结果无法与当前DTO精确需求对应。");
        foreach (var row in frame.Rows) Contains(row.Text(), "料理 · 测试料理", "厨具 · 煮锅", "配方 · 豆腐", "酒水 · 测试酒水");
        // 生产业务层有些数字是值为整数的JsonValue<double>，HTTP序列化后Flutter读成int。
        // 游戏内投影直接消费内存结果，也必须保留相同精确图片身份，不能只测手写int夹具。
        foreach (var row in frame.Rows)
        {
            Check(Tags(row, "料理").Single().IconKey == "food:101", "真实生产内存结果中的料理ID没有正确转为图片键。");
            Check(Tags(row, "酒水").Single().IconKey == "beverage:21", "真实生产内存结果中的酒水ID没有正确转为图片键。");
        }
    }

    /// <summary>标准小样本保留所有生产DTO默认字段；记录时间与桌号故意不同，防止误按桌号排序。</summary>
    private static LocalApiSnapshot Fixture() => new()
    {
        NightBusinessGeneration = 61, NightBusinessLifecyclePhase = "Active", ActiveSceneName = "fixture-night", RuntimeDataSignature = "catalog-1",
        NightBusiness = new() { OrdersReadComplete = true, Orders = new() { Rare("R-0002", 2, 1, 2) } },
        NormalBusiness = new() { OrdersReadComplete = true, Orders = new() { Normal("N-0001", 1, 0, 1) } },
    };

    private static LocalApiSnapshot SortingFixture() => Mutate(Fixture(), root =>
    {
        root["nightBusiness"]!["orders"]!.AsArray().Insert(0, JsonSerializer.SerializeToNode(Rare("R-0004", 4, 3, 4), JsonOptions));
        root["normalBusiness"]!["orders"]!.AsArray().Insert(0, JsonSerializer.SerializeToNode(Normal("N-0003", 3, 2, 3), JsonOptions));
    });

    private static NightBusinessOrder Rare(string trace, long lifecycle, int desk, int seconds) => new()
    {
        TraceId = trace, OrderLifecycleSequence = lifecycle, GuestId = 4, RuntimeGuestId = 4, GuestName = "测试稀客", DeskCode = desk,
        FoodTagId = 1, FoodTag = "素", BeverageTagId = 2, BeverageTag = "水果", Fund = 100,
        FirstSeenAtUtc = DateTime.UnixEpoch.AddSeconds(seconds), LastSeenAtUtc = DateTime.UnixEpoch.AddSeconds(seconds),
    };

    private static NormalBusinessOrder Normal(string trace, long lifecycle, int desk, int seconds) => new()
    {
        TraceId = trace, OrderLifecycleSequence = lifecycle, OrderKey = "native:" + trace, GuestId = 3, RuntimeGuestId = 3, GuestName = "测试普客", DeskCode = desk,
        FoodId = 101, FoodName = "煎豆腐", BeverageId = 21, BeverageName = "绿茶", FirstSeenAtUtc = DateTime.UnixEpoch.AddSeconds(seconds),
    };

    private static JsonObject Results(LocalApiSnapshot snapshot) => new()
    {
        ["recommendations"] = new JsonArray(snapshot.NightBusiness!.Orders.Select(order => (JsonNode)new JsonObject
        {
            ["order"] = JsonSerializer.SerializeToNode(order, JsonOptions),
            ["executionPlans"] = JsonNode.Parse("[{\"food\":{\"recipe\":{\"name\":\"味噌汤\",\"cooker\":\"煮锅\",\"ingredients\":[\"豆腐\",\"海苔\"]},\"extraIngredients\":[{\"name\":\"蘑菇\"}]},\"beverage\":{\"beverage\":{\"name\":\"果汁\"}}}]")
        }).ToArray()),
        ["normalOrderDetailPlans"] = new JsonArray(snapshot.NormalBusiness!.Orders.Select(order => (JsonNode)new JsonObject
        {
            ["order"] = JsonSerializer.SerializeToNode(order, JsonOptions),
            ["executionFood"] = JsonNode.Parse("{\"name\":\"煎豆腐\",\"cookerName\":\"油锅\",\"baseIngredientNames\":[\"豆腐\",\"鸡蛋\"],\"extraIngredients\":[{\"name\":\"海苔\"}]}"),
            ["executionBeverage"] = new JsonObject { ["name"] = "绿茶" },
        }).ToArray()),
    };

    private static LocalApiSnapshot WithQueue(LocalApiSnapshot snapshot, params string[] traces) => Mutate(snapshot, root =>
    {
        // 参数仅表达参与队列，显示键始终完整：暂停订单按首次点单顺序排在后方。
        // entries同样覆盖全部现场生命周期，避免夹具把缺少状态误当成合法参与证据。
        var participating = traces.ToHashSet(StringComparer.Ordinal);
        var orders = traces.Select(trace => snapshot.NightBusiness!.Orders.Single(order => order.TraceId == trace))
            .Concat(snapshot.NightBusiness!.Orders.Where(order => !participating.Contains(order.TraceId))
                .OrderBy(order => order.FirstSeenAtUtc).ThenBy(order => order.OrderLifecycleSequence)).ToArray();
        root["rareGuestParticipation"] = JsonSerializer.SerializeToNode(new LocalApiRareGuestParticipationSnapshot
        {
            Active = true, Ready = true, BusinessGeneration = snapshot.NightBusinessGeneration, ParticipationRevision = 8,
            ManagedGuestIds = orders.Select(order => order.GuestId!.Value).Distinct().ToList(),
            DisplayOrderKeys = orders.Select(order => new LocalApiRareGuestParticipationIdentityDto
                {
                    BusinessGeneration = snapshot.NightBusinessGeneration, TraceId = order.TraceId,
                    GuestId = order.GuestId!.Value, OrderLifecycleSequence = order.OrderLifecycleSequence,
                }).ToList(),
            Entries = orders.Select((order, index) => new LocalApiRareGuestParticipationEntry {
                TraceId = order.TraceId, GuestId = order.GuestId!.Value, OrderLifecycleSequence = order.OrderLifecycleSequence,
                Managed = true, Participating = participating.Contains(order.TraceId),
                QueuePosition = participating.Contains(order.TraceId) ? index + 1 : null,
                ReasonCode = participating.Contains(order.TraceId) ? "participating" : "managed-paused",
            }).ToList(),
        }, JsonOptions);
    });

    private static GameFocusOverlayPlans Capture(LocalApiSnapshot snapshot, JsonObject result)
        => GameFocusOverlayProjection.Capture(JsonSerializer.SerializeToNode(snapshot, JsonOptions)!.AsObject(), result);

    private static GameFocusOverlayFrame Compose(LocalApiSnapshot snapshot, GameFocusOverlayPlans plans, GameFocusOverlaySettings? settings = null)
        => GameFocusOverlayProjection.Compose(snapshot, plans, settings ?? new() { Enabled = true });

    private static LocalApiSnapshot Mutate(LocalApiSnapshot snapshot, Action<JsonObject> change)
    {
        var root = JsonSerializer.SerializeToNode(snapshot, JsonOptions)!.AsObject();
        change(root);
        return root.Deserialize<LocalApiSnapshot>(JsonOptions)!;
    }

    private static JsonNode? Clone(JsonNode? node) => node == null ? null : JsonNode.Parse(node.ToJsonString());
    private static string Text(this GameFocusOverlayRow row) => string.Join("\n", row.Groups.Select(group => group.Prefix + " · " + string.Join("、", group.Tags.Select(tag => tag.Text))));
    private static string[] Traces(GameFocusOverlayFrame frame) => frame.Rows.Select(row => row.Identity.Split(':')[2]).ToArray();

    private static GameFocusOverlayState Update(GameFocusOverlayStore store, string json)
    {
        using var document = JsonDocument.Parse(json);
        return store.Update(store.Snapshot.Revision, document.RootElement);
    }

    private static void Check(bool condition, string message)
    {
        _assertions++;
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Contains(string text, params string[] fragments)
    {
        foreach (var fragment in fragments) Check(text.Contains(fragment, StringComparison.Ordinal), $"缺少预期文本“{fragment}”：{text}");
    }

    private static void Equal(IEnumerable<string> actual, IEnumerable<string> expected, string message)
        => Check(actual.SequenceEqual(expected), message + " 实际：" + string.Join(",", actual));

    private static void Throws<T>(Action action, string message) where T : Exception
    {
        _assertions++;
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException(message);
    }

    /// <summary>不同Windows文件系统可把禁止原子替换映射为IO或访问拒绝，两者均必须保持事务未提交。</summary>
    private static void ThrowsFileWrite(Action action)
    {
        _assertions++;
        try { action(); }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }
        throw new InvalidOperationException("目标文件被占用时必须报告持久化失败。");
    }
}
