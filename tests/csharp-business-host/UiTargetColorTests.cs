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
            host.Authority.Register(client, "color regression", new CompanionDeviceRegisterRequest
            {
                ProtocolVersion = 1, ProfileSchemaVersion = 1, Platform = "windows", AppVersion = "offline",
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
            var catalog = LeaseExpiryTests.Catalog();
            host.Publish(snapshot, catalog);
            host.Start();

            // 同时验证默认颜色、自定义颜色与两种单独开启情形；每轮都经真实存储更新与后台重算。
            foreach (var scenario in new[]
            {
                (Rare: true, Normal: true, RareColor: "#FFDB2E", NormalColor: "#5FACD3"),
                (Rare: true, Normal: true, RareColor: "#12ABEF", NormalColor: "#A012F9"),
                (Rare: true, Normal: false, RareColor: "#000000", NormalColor: "#FFFFFF"),
                (Rare: false, Normal: true, RareColor: "#000000", NormalColor: "#FFFFFF"),
                (Rare: false, Normal: false, RareColor: "#FFDB2E", NormalColor: "#5FACD3"),
            })
            {
                foreach (var kind in new[] { "rare", "normal" })
                {
                    var enabled = kind == "rare" ? scenario.Rare : scenario.Normal;
                    foreach (var suffix in new[] { "GameUiPinningEnabled", "RecipeVariantEnabled", "CookerHighlightEnabled", "SeatHighlightEnabled", "OrderHighlightEnabled" })
                        settings[kind + suffix] = enabled;
                    settings[kind + "TargetHighlightColor"] = kind == "rare" ? scenario.RareColor : scenario.NormalColor;
                }
                host.Import(J.Object(("snapshot", snapshot), ("catalog", catalog), ("profile", settings)), client);
                await Until(() => J.Bool(host.Status()["isCurrent"]), host);
                var targets = RuntimeUiPinningService.Targets.ToArray();
                var status = host.Status();
                Check(status["error"] == null, "合法颜色不得让经营业务进入错误状态。");
                Check(targets.Length == (scenario.Rare ? 1 : 0) + (scenario.Normal ? 1 : 0), "真实发布的目标数量必须遵从 main 的稀客/普客开关。");
                foreach (var kind in new[] { "rare", "normal" })
                {
                    var enabled = kind == "rare" ? scenario.Rare : scenario.Normal;
                    var color = kind == "rare" ? scenario.RareColor : scenario.NormalColor;
                    var projection = status["gameUiTargets"]?[kind];
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

            // 直接覆盖游戏协议的严格边界及非零槽位；不能靠放宽旧协议让上面的业务测试通过。
            foreach (var index in new[] { 0, 1 })
            {
                var prefix = "target" + index;
                var wire = J.Object((prefix + "Kind", "normal"), (prefix + "Color", "12ABEF"),
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
