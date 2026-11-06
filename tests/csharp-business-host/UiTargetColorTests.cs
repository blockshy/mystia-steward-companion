using System.Text.Json;
using System.Text.Json.Nodes;
using BepInEx.Logging;
using MystiaStewardCompanion.LocalApi;
using MystiaStewardCompanion.Save;
using J = MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

/// <summary>
/// 通过真实业务循环、领域推荐、游戏目标解析器和不可变目标类型验证颜色边界。
/// 只替换最后的 Unity 发布动作；解析器不得使用宽松替身，否则无法发现 CSS 前缀泄漏。
/// </summary>
internal static class UiTargetColorTests
{
    internal static async Task Run(JsonElement profile)
    {
        var directory = Path.GetFullPath(Path.Combine("temp", "mystia-business-ui-colors-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        var log = Logger.CreateLogSource("business-ui-colors-offline");
        var checks = 0;
        var effects = 0;
        void Check(bool value, string message)
        {
            checks++;
            if (!value) throw new InvalidOperationException(message);
        }
        var host = new LocalApiServer(directory, log, _ =>
        {
            Interlocked.Increment(ref effects);
            throw new InvalidOperationException("颜色回归不允许调用游戏动作。");
        });
        try
        {
            const string client = "11111111-1111-1111-1111-111111111111";
            var settings = J.Obj(JsonNode.Parse(profile.GetRawText()));
            settings["automationEnabled"] = false;
            settings["gameUiEnabled"] = true;
            host.Authority.Register(client, "color regression", new CompanionDeviceRegisterRequest
            {
                ProtocolVersion = 1, ProfileSchemaVersion = CompanionDeviceAuthorityStore.ProfileSchemaVersion, Platform = "windows", AppVersion = "offline",
                Profile = JsonSerializer.SerializeToElement(settings),
            }, DateTime.UtcNow);
            var snapshot = LeaseExpiryTests.Snapshot();
            var normal = J.Obj(J.Arr(snapshot["normalBusiness"]?["orders"])[0]);
            normal["traceId"] = "N-1";
            normal["orderKey"] = "ptr:123";
            J.Obj(snapshot["nightBusiness"])["orders"] = new JsonArray(J.Object(
                ("traceId", "R-1"), ("orderLifecycleSequence", 2), ("deskCode", 1),
                ("guestId", 3), ("runtimeGuestId", 3), ("guestName", "测试稀客"),
                ("foodTag", "素"), ("beverageTag", "水果"), ("firstSeenAtUtc", "offline")));
            J.Obj(snapshot["nightBusiness"])["ordersReadComplete"] = true;
            J.Obj(snapshot["normalBusiness"])["ordersReadComplete"] = true;
            var catalog = LeaseExpiryTests.Catalog();
            host.Publish(snapshot, catalog);
            host.Start();

            // 总开关关闭与重新开启只切换执行投影，既有十个分项和颜色不得被覆盖。
            // 同时验证默认颜色、自定义颜色与两种单独开启情形；每轮都经真实存储更新与后台重算。
            foreach (var scenario in new[]
            {
                (Master: true, Rare: true, Normal: true, RareColor: "#FFDB2E", NormalColor: "#5FACD3"),
                (Master: true, Rare: true, Normal: true, RareColor: "#12ABEF", NormalColor: "#A012F9"),
                (Master: false, Rare: true, Normal: true, RareColor: "#12ABEF", NormalColor: "#A012F9"),
                (Master: true, Rare: true, Normal: true, RareColor: "#12ABEF", NormalColor: "#A012F9"),
                (Master: true, Rare: true, Normal: false, RareColor: "#000000", NormalColor: "#FFFFFF"),
                (Master: true, Rare: false, Normal: true, RareColor: "#000000", NormalColor: "#FFFFFF"),
                (Master: true, Rare: false, Normal: false, RareColor: "#FFDB2E", NormalColor: "#5FACD3"),
            })
            {
                settings["gameUiEnabled"] = scenario.Master;
                foreach (var kind in new[] { "rare", "normal" })
                {
                    var enabled = kind == "rare" ? scenario.Rare : scenario.Normal;
                    foreach (var suffix in new[] { "GameUiPinningEnabled", "RecipeVariantEnabled", "CookerHighlightEnabled", "SeatHighlightEnabled", "OrderHighlightEnabled" })
                        settings[kind + suffix] = enabled;

                }
                settings["rareOrderHighlightPalette"] = new JsonArray(scenario.RareColor, "#F08C78");
                settings["normalOrderHighlightPalette"] = new JsonArray(scenario.NormalColor, "#97C878");
                host.Import(J.Object(("snapshot", snapshot), ("catalog", catalog), ("profile", settings)), client);
                await Until(() => J.Bool(host.Status()["isCurrent"]), host);
                var targets = RuntimeUiPinningService.Targets.ToArray();
                var status = host.Status();
                Check(status["error"] == null, "合法颜色不得让经营业务进入错误状态。");
                Check(targets.Length == (scenario.Master ? (scenario.Rare ? 1 : 0) + (scenario.Normal ? 1 : 0) : 0),
                    "真实发布目标必须同时遵守模块总开关和稀客/普客分项，关闭总开关要撤销旧目标。");
                Check(J.Arr(status["gameUiTargets"]?["orderColors"]).Count == 2,
                    "关闭游戏辅助或分项不能移除客户端订单颜色。");
                var persisted = host.Authority.ReadBusinessState(DateTime.UtcNow).ActiveProfile;
                foreach (var kind in new[] { "rare", "normal" })
                {
                    var selected = kind == "rare" ? scenario.Rare : scenario.Normal;
                    var enabled = scenario.Master && selected;
                    var color = kind == "rare" ? scenario.RareColor : scenario.NormalColor;
                    foreach (var suffix in new[] { "GameUiPinningEnabled", "RecipeVariantEnabled", "CookerHighlightEnabled", "SeatHighlightEnabled", "OrderHighlightEnabled" })
                        Check(persisted.GetProperty(kind + suffix).GetBoolean() == selected, "总开关改变了分项偏好：" + kind + suffix);
                    Check(persisted.GetProperty(kind + "OrderHighlightPalette")[0].GetString() == color, "总开关改变了所属类别的高亮颜色。");
                    var projection = J.Objects(status["gameUiTargets"]?[kind]).SingleOrDefault();
                    if (!enabled) { Check(projection == null, "关闭辅助后必须清空对应业务目标。"); continue; }
                    var actual = targets.Single(target => target.Kind == (kind == "rare" ? RuntimeUiTargetKind.Rare : RuntimeUiTargetKind.Normal));
                    Check(J.Str(projection?["color"]) == color, "业务返回的 CSS 颜色必须与 main 一致，不能被协议编码修改。");
                    Check(actual.Color.ToExactHex() == color[1..], "游戏目标必须保留精确 RGB 值且不含 CSS 前缀。");
                    Check(actual.ListPinningEnabled && actual.RecipeVariantEnabled && actual.CookerHighlightEnabled
                        && actual.SeatHighlightEnabled && actual.OrderHighlightEnabled, "转换不得丢失已有的五种目标功能。");
                    Check(actual.OrderTraceId == (kind == "rare" ? "R-1" : "N-1")
                        && actual.OrderLifecycleSequence == (kind == "rare" ? 2 : 1)
                        && actual.DeskCode == (kind == "rare" ? 1 : 0)
                        && actual.OrderKey == (kind == "rare" ? "" : "ptr:123"), "转换不得改动精确订单身份。");
                    Check(actual.RecipeId == 201 && actual.BeverageId == 21 && actual.CookerTypeId == 1
                        && actual.IngredientIds.SequenceEqual(new[] { 1 }) && actual.ExtraIngredientIds.Count == 0,
                        "真实解析器必须完整保留配方、厨具和材料列表。");
                }
            }

            // 独立类别总开关必须同时约束五个原生功能，关闭后保留分项和配色。
            // 每轮重放关闭前的真实旧帧，验证异步迟到结果不能复活刚撤回的类别。
            foreach (var kind in new[] { "rare", "normal" })
                foreach (var suffix in new[] { "GameUiPinningEnabled", "RecipeVariantEnabled", "CookerHighlightEnabled", "SeatHighlightEnabled", "OrderHighlightEnabled" })
                    settings[kind + suffix] = true;
            settings["gameUiEnabled"] = true;
            settings["rareGameUiEnabled"] = true; settings["normalGameUiEnabled"] = true;
            host.Import(J.Object(("snapshot", snapshot), ("catalog", catalog), ("profile", settings)), client);
            await Until(() => J.Bool(host.Status()["isCurrent"]), host);
            foreach (var scenario in new[] {
                (Master: true, Rare: false, Normal: true), (Master: true, Rare: true, Normal: false),
                (Master: true, Rare: false, Normal: false), (Master: true, Rare: true, Normal: true),
                (Master: false, Rare: true, Normal: false), (Master: true, Rare: true, Normal: false),
                (Master: true, Rare: true, Normal: true),
            })
            {
                var replayOldFrame = host.CaptureGameUiReplayForTest();
                settings["gameUiEnabled"] = scenario.Master;
                settings["rareGameUiEnabled"] = scenario.Rare; settings["normalGameUiEnabled"] = scenario.Normal;
                host.Import(J.Object(("snapshot", snapshot), ("catalog", catalog), ("profile", settings)), client);
                await Until(() => J.Bool(host.Status()["isCurrent"]), host);
                var targets = RuntimeUiPinningService.Targets.ToArray();
                Check(replayOldFrame(), "旧权威帧重放复活了关闭类别的游戏辅助。");
                Check(targets.Length == (scenario.Master ? (scenario.Rare ? 1 : 0) + (scenario.Normal ? 1 : 0) : 0),
                    "类别总开关未精确撤回全部所属原生目标。");
                Check(J.Arr(host.Status()["gameUiTargets"]?["orderColors"]).Count == 2,
                    "类别总开关不应修改独立的客户端订单展示颜色。");
                var persisted = host.Authority.ReadBusinessState(DateTime.UtcNow).ActiveProfile;
                foreach (var kind in new[] { "rare", "normal" })
                {
                    var selected = kind == "rare" ? scenario.Rare : scenario.Normal;
                    Check(persisted.GetProperty(kind + "GameUiEnabled").GetBoolean() == selected,
                        "类别总开关没有经过真实配置存储。");
                    foreach (var suffix in new[] { "GameUiPinningEnabled", "RecipeVariantEnabled", "CookerHighlightEnabled", "SeatHighlightEnabled", "OrderHighlightEnabled" })
                        Check(persisted.GetProperty(kind + suffix).GetBoolean(), "类别总开关覆盖了原子偏好：" + kind + suffix);
                    Check(persisted.GetProperty(kind + "OrderHighlightPalette").GetRawText()
                        == JsonSerializer.SerializeToElement(settings[kind + "OrderHighlightPalette"]).GetRawText(),
                        "类别总开关改写了原有配色。");
                    var enabled = scenario.Master && selected;
                    var actual = targets.SingleOrDefault(target => target.Kind == (kind == "rare" ? RuntimeUiTargetKind.Rare : RuntimeUiTargetKind.Normal));
                    Check((actual != null) == enabled, "关闭一类影响了另一类辅助，或未清空关闭类。");
                    if (actual != null) Check(actual.ListPinningEnabled && actual.RecipeVariantEnabled
                        && actual.CookerHighlightEnabled && actual.SeatHighlightEnabled && actual.OrderHighlightEnabled,
                        "重新开启类别总开关没有恢复全部五种原功能。");
                }
            }

            // 经真实宿主捕获/发布验证完整性，不仅调用分配器：未知空列表暂停显示，恢复不能改色。
            var stableSnapshot = J.Obj(J.Clone(snapshot));
            var rareRows = J.Arr(stableSnapshot["nightBusiness"]?["orders"]);
            var secondRare = J.Obj(J.Clone(rareRows[0]));
            secondRare["traceId"] = "R-2"; secondRare["orderLifecycleSequence"] = 3; secondRare["deskCode"] = 2;
            secondRare["firstSeenAtUtc"] = "a-earlier";
            rareRows.Add(secondRare);
            host.Publish(stableSnapshot, catalog);
            await Until(() => J.Bool(host.Status()["isCurrent"]), host);
            var stableSlots = J.Objects(host.Status()["gameUiTargets"]?["orderColors"])
                .ToDictionary(row => J.Str(row["traceId"]), row => J.Num(row["colorSlot"]));
            Check(stableSlots.Count == 3 && stableSlots["R-2"] == 2, "新增订单应占空槽，不能因时间排序抢原单颜色。");
            var unknownSnapshot = J.Obj(J.Clone(stableSnapshot));
            foreach (var contextName in new[] { "nightBusiness", "normalBusiness" })
            {
                var context = J.Obj(unknownSnapshot[contextName]);
                context["orders"] = new JsonArray(); context["ordersReadComplete"] = false;
                context["error"] = "离线夹具：本轮采集不可用";
            }
            host.Publish(unknownSnapshot, catalog);
            await Until(() => J.Bool(host.Status()["isCurrent"]), host);
            Check(J.Arr(host.Status()["gameUiTargets"]?["orderColors"]).Count == 0,
                "未知帧不能把旧颜色行误发布为当前订单。");
            host.Publish(stableSnapshot, catalog);
            await Until(() => J.Bool(host.Status()["isCurrent"]), host);
            Check(J.Objects(host.Status()["gameUiTargets"]?["orderColors"])
                .All(row => stableSlots[J.Str(row["traceId"])] == J.Num(row["colorSlot"])),
                "宿主不能将失败采集的空列表解释为全部订单结束。");
            var emptySnapshot = J.Obj(J.Clone(stableSnapshot));
            J.Obj(emptySnapshot["nightBusiness"])["orders"] = new JsonArray();
            host.Publish(emptySnapshot, catalog);
            await Until(() => J.Bool(host.Status()["isCurrent"]), host);
            var newRareSnapshot = J.Obj(J.Clone(emptySnapshot));
            J.Obj(newRareSnapshot["nightBusiness"])["orders"] = new JsonArray(J.Clone(secondRare));
            host.Publish(newRareSnapshot, catalog);
            await Until(() => J.Bool(host.Status()["isCurrent"]), host);
            Check(J.Num(J.Objects(host.Status()["gameUiTargets"]?["orderColors"])
                .Single(row => J.Str(row["kind"]) == "rare")["colorSlot"]) == 0,
                "已确认完整空集应释放旧槽，下一新订单可使用首槽。");

            // 八单混合现场必须由实际颜色数量限制辅助集合；原始订单和自动化候选不能被截断。
            // 使用真实宿主计算、配置存储和目标解析器，避免仅分配器单测通过而发布层仍复用颜色。
            var crowded = J.Obj(J.Clone(snapshot));
            var fourRare = new JsonArray(); var fourNormal = new JsonArray();
            for (var index = 0; index < 4; index++)
            {
                var rareOrder = J.Obj(J.Clone(J.Arr(snapshot["nightBusiness"]?["orders"])[0]));
                rareOrder["traceId"] = "R-" + (301 + index); rareOrder["orderLifecycleSequence"] = 101 + index;
                rareOrder["deskCode"] = index; rareOrder["runtimeGuestId"] = 31 + index;
                rareOrder["firstSeenAtUtc"] = $"2026-11-04T00:00:0{index}Z";
                fourRare.Add(rareOrder);
                var normalOrder = J.Obj(J.Clone(normal));
                normalOrder["traceId"] = "N-" + (301 + index); normalOrder["orderLifecycleSequence"] = 201 + index;
                normalOrder["orderKey"] = "ptr:" + (501 + index).ToString("x"); normalOrder["deskCode"] = 4 + index;
                normalOrder["firstSeenAtUtc"] = $"2026-11-04T00:00:0{index}Z";
                fourNormal.Add(normalOrder);
            }
            J.Obj(crowded["nightBusiness"])["orders"] = fourRare;
            J.Obj(crowded["normalBusiness"])["orders"] = fourNormal;
            foreach (var kind in new[] { "rare", "normal" })
                foreach (var suffix in new[] { "GameUiPinningEnabled", "RecipeVariantEnabled", "CookerHighlightEnabled", "SeatHighlightEnabled", "OrderHighlightEnabled" })
                    settings[kind + suffix] = true;
            host.Import(J.Object(("snapshot", crowded), ("catalog", catalog), ("profile", settings)), client);
            await Until(() => J.Bool(host.Status()["isCurrent"]), host);
            var firstSelected = RuntimeUiPinningService.Targets.ToArray();
            Check(firstSelected.Length == 4 && firstSelected.Count(target => target.Kind == RuntimeUiTargetKind.Rare) == 2
                && firstSelected.Count(target => target.Kind == RuntimeUiTargetKind.Normal) == 2,
                "各两色的八单现场只能辅助两稀客与两普客。");
            Check(firstSelected.GroupBy(target => target.Kind).All(group => group.Select(target => target.Color.ToExactHex()).Distinct().Count() == group.Count()),
                "生产游戏目标中的同类辅助订单不能共享颜色。");
            Check(J.Arr(host.Status()["gameUiTargets"]?["orderColors"]).Count == 4,
                "未入选订单不能借用另一单颜色显示为正在辅助。");
            var unchangedRareColor = firstSelected.Single(target => target.OrderTraceId == "R-302").Color.ToExactHex();
            J.Obj(fourRare[0])["hasServedFood"] = true; J.Obj(fourRare[0])["hasServedBeverage"] = true;
            host.Publish(crowded, catalog);
            await Until(() => J.Bool(host.Status()["isCurrent"]), host);
            Check(RuntimeUiPinningService.Targets.All(target => target.OrderTraceId != "R-301")
                && RuntimeUiPinningService.Targets.Any(target => target.OrderTraceId == "R-303"),
                "已送齐但仍在原始列表的订单必须让出辅助颜色给下一单。");
            Check(RuntimeUiPinningService.Targets.Single(target => target.OrderTraceId == "R-302").Color.ToExactHex() == unchangedRareColor,
                "补入下一单不能改变仍在辅助的订单颜色。");
            settings["rareOrderHighlightPalette"] = new JsonArray("#F2CA5C", "#EF856D", "#E785B4");
            settings["normalOrderHighlightPalette"] = new JsonArray("#5FACD3");
            host.Import(J.Object(("snapshot", crowded), ("catalog", catalog), ("profile", settings)), client);
            await Until(() => J.Bool(host.Status()["isCurrent"]), host);
            Check(RuntimeUiPinningService.Targets.Count(target => target.Kind == RuntimeUiTargetKind.Rare) == 3
                && RuntimeUiPinningService.Targets.Count(target => target.Kind == RuntimeUiTargetKind.Normal) == 1,
                "调色数量变化应独立调整两类辅助上限，且不补回已送齐订单。");

            // 原始订单完整不代表人工调度集合也完整。先建立两色，再反转同一入选集合的
            // 队列顺序；若未知调度被误当成取消，恢复时两单便会互换颜色，能稳定复现问题。
            settings["rareOrderHighlightPalette"] = new JsonArray("#FFDB2E", "#F08C78");
            settings["rareGuestParticipationModuleEnabled"] = true;
            settings["managedRareGuestIds"] = new JsonArray(3);
            J.Obj(fourRare[0])["hasServedFood"] = false;
            J.Obj(fourRare[0])["hasServedBeverage"] = false;
            var participation = J.Object(("active", true),
                ("businessGeneration", crowded["nightBusinessGeneration"]), ("participationRevision", 1),
                ("managedGuestIds", new JsonArray(3)), ("entries", new JsonArray()));
            var entries = J.Arr(participation["entries"]);
            for (var index = 0; index < fourRare.Count; index++)
            {
                var order = J.Obj(fourRare[index]);
                entries.Add(J.Object(("traceId", order["traceId"]),
                    ("orderLifecycleSequence", order["orderLifecycleSequence"]), ("guestId", order["guestId"]),
                    ("managed", true), ("participating", true), ("queuePosition", index + 1)));
            }
            crowded["rareGuestParticipation"] = participation;
            host.Import(J.Object(("snapshot", crowded), ("catalog", catalog), ("profile", settings)), client);
            await Until(() => J.Bool(host.Status()["isCurrent"]), host);
            var beforeUnknown = RuntimeUiPinningService.Targets.Where(target => target.Kind == RuntimeUiTargetKind.Rare)
                .ToDictionary(target => target.OrderTraceId, target => target.Color.ToExactHex());
            Check(beforeUnknown.Count == 2 && beforeUnknown.ContainsKey("R-301") && beforeUnknown.ContainsKey("R-302"),
                "有效调度集合应按队列选中前两笔稀客。");
            J.Obj(entries[0])["queuePosition"] = 2; J.Obj(entries[1])["queuePosition"] = 1;
            participation["participationRevision"] = 2;
            var missingEntry = J.Obj(J.Clone(crowded));
            J.Arr(missingEntry["rareGuestParticipation"]?["entries"]).RemoveAt(3);
            host.Publish(missingEntry, catalog);
            await Until(() => J.Bool(host.Status()["isCurrent"]), host);
            Check(!RuntimeUiPinningService.Targets.Any(target => target.Kind == RuntimeUiTargetKind.Rare)
                && !J.Objects(host.Status()["gameUiTargets"]?["orderColors"]).Any(row => J.Str(row["kind"]) == "rare"),
                "调度集合缺行时必须隐藏稀客辅助，不能把原始完整名单当成许可。");
            host.Publish(crowded, catalog);
            await Until(() => J.Bool(host.Status()["isCurrent"]), host);
            Check(RuntimeUiPinningService.Targets.Where(target => target.Kind == RuntimeUiTargetKind.Rare)
                .All(target => beforeUnknown[target.OrderTraceId] == target.Color.ToExactHex()),
                "未知调度恢复后，队列顺序变化不能交换仍入选订单的颜色。");
            // 明确暂停属于完整有效集合，应真正释放两色，不能误用未知状态保留逻辑阻塞下一单。
            for (var index = 0; index < entries.Count; index++)
            {
                J.Obj(entries[index])["participating"] = index >= 2;
                J.Obj(entries[index])["queuePosition"] = index >= 2 ? JsonValue.Create(index - 1) : null;
            }
            participation["participationRevision"] = 3;
            host.Publish(crowded, catalog);
            await Until(() => J.Bool(host.Status()["isCurrent"]), host);
            Check(RuntimeUiPinningService.Targets.Where(target => target.Kind == RuntimeUiTargetKind.Rare)
                .Select(target => target.OrderTraceId).OrderBy(trace => trace).SequenceEqual(new[] { "R-303", "R-304" }),
                "明确暂停前两笔后，后两笔必须获得释放的辅助颜色。");

            // 直接覆盖游戏协议的严格边界及非零槽位；不能靠放宽旧协议让上面的业务测试通过。
            foreach (var index in new[] { 0, 1 })
            {
                var prefix = "target" + index;
                var wire = J.Object((prefix + "Kind", "normal"), (prefix + "Color", "12ABEF"), (prefix + "ColorSlot", 7),
                    (prefix + "ListPinningEnabled", true), (prefix + "RecipeVariantEnabled", false),
                    (prefix + "CookerHighlightEnabled", false), (prefix + "SeatHighlightEnabled", false),
                    (prefix + "OrderHighlightEnabled", false), (prefix + "TraceId", "N-1"),
                    (prefix + "OrderKey", "ptr:123"), (prefix + "OrderLifecycleSequence", 1), (prefix + "DeskCode", 0),
                    (prefix + "RecipeId", -1), (prefix + "IngredientIds", new JsonArray()),
                    (prefix + "ExtraIngredientIds", new JsonArray()), (prefix + "BeverageId", 21),
                    (prefix + "CookerTypeId", -1), (prefix + "Revision", "offline-revision"));
                var actual = LocalApiServer.ParseUiTargetForColorTest(wire, index);
                Check(actual.Color.ToExactHex() == "12ABEF" && actual.RecipeId == -1
                    && actual.IngredientIds.Count == 0 && actual.ExtraIngredientIds.Count == 0 && actual.CookerTypeId == -1,
                    "完整协议必须接受空材料与缺失料理/厨具哨兵值。");
                foreach (var invalid in new[] { "#12ABEF", "12abef", "12ABE", "12ABEFF", "12ABEG", " 12ABEF", "12ABEF ", "" })
                {
                    wire[prefix + "Color"] = invalid;
                    var rejected = false;
                    try { LocalApiServer.ParseUiTargetForColorTest(wire, index); }
                    catch (FormatException) { rejected = true; }
                    Check(rejected, "真实游戏协议必须拒绝非法颜色：" + invalid);
                }
            }
            Check(Volatile.Read(ref effects) == 0, "测试不得产生任何游戏动作。");
            Console.WriteLine($"PASS UI target colors: {checks} assertions; real business loop and production parser preserve CSS/RGB boundary.");
        }
        finally
        {
            await host.Stop();
            Logger.Sources.Remove(log);
            // 仅删除本测试创建且已核验位于工作区 temp 内的独占目录，不接触用户配置。
            var temporaryRoot = Path.GetFullPath("temp") + Path.DirectorySeparatorChar;
            if (directory.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase)) Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task Until(Func<bool> condition, LocalApiServer host)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (!condition())
        {
            if (Environment.TickCount64 >= deadline) throw new TimeoutException("颜色目标未发布：" + host.Status().ToJsonString());
            await Task.Delay(20);
        }
    }
}

namespace MystiaStewardCompanion.LocalApi
{
    internal sealed partial class LocalApiServer
    {
        /// <summary>只公开真实生产编解码边界供本测试调用，不复制或替代任何字段验证逻辑。</summary>
        internal static RuntimeUiTargetSnapshot ParseUiTargetForColorTest(JsonObject wire, int index)
            => ReadUiPinningTarget(ToBusinessQuery(wire), index);
    }
}
