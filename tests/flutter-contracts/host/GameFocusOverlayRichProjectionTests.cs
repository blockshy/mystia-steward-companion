using System.Text.Json;
using System.Text.Json.Nodes;
using MystiaStewardCompanion.GameMedia;
using MystiaStewardCompanion.LocalApi;
using MystiaStewardCompanion.Ui;

/// <summary>
/// 富专注卡片专项断言：只核验公开的标签语义、精确图片键与底色，不加载图片或构建Unity。
/// 输入刻意把Food ID、Recipe ID、客人运行时ID分开，防止测试本身掩盖混用身份的缺陷。
/// </summary>
internal static partial class GameFocusOverlayContractTests
{
    private sealed record RichFixture(LocalApiSnapshot Snapshot, JsonObject Result, JsonObject Catalog,
        GameMediaManifest Media, JsonObject Targets);

    private static void VerifyRichContent()
    {
        var fixture = Rich();
        var frame = RichCompose(fixture);
        foreach (var row in frame.Rows)
        {
            Equal(row.Groups.Select(group => group.Prefix), new[] { "点单", "料理", "配方", "加料", "厨具", "酒水" }, "卡片字段顺序应与Flutter专注一致。");
            Check(row.Badges.Count == 2 && row.Badges[1].Text == "待送达", "类型和阶段应在标题徽标区。");
            Check(row.Groups.Single(group => group.Prefix == "料理").Tags.Single().Tone == GameFocusOverlayTone.Recipe, "料理标签视觉语义错误。");
            Check(row.Groups.Single(group => group.Prefix == "加料").Tags.Single().Tone == GameFocusOverlayTone.Extra, "加料标签视觉语义错误。");
        }
        var cases = new (GameFocusOverlayContent Content, string Hidden)[]
        {
            (new() { ShowOrderTags = false }, "点单"), (new() { ShowFood = false }, "料理"),
            (new() { ShowIngredients = false }, "配方"), (new() { ShowExtras = false }, "加料"),
            (new() { ShowCooker = false }, "厨具"), (new() { ShowBeverage = false }, "酒水"),
        };
        foreach (var (content, hidden) in cases)
        {
            foreach (var plans in new[] { RichCapture(fixture), GameFocusOverlayPlans.Empty })
            {
                var selected = GameFocusOverlayProjection.Compose(fixture.Snapshot, plans, new() { Enabled = true, Content = content }, fixture.Media.Generation);
                Check(selected.Rows.Count == 2 && selected.Rows.All(row => row.Groups.All(group => group.Prefix != hidden)), "内容开关未同时过滤正式方案和占位：" + hidden);
                Check(selected.Rows.All(row => row.Groups.Count == 5 && row.Heading.Length > 0 && row.Badges.Count == 2), "字段开关错误清除了标题/阶段或改变其它字段。");
            }
        }
        var allHidden = new GameFocusOverlayContent { ShowOrderTags = false, ShowFood = false, ShowIngredients = false,
            ShowExtras = false, ShowCooker = false, ShowBeverage = false };
        Check(RichCompose(fixture, allHidden).Rows.All(row => row.Groups.Count == 0 && row.Heading.Length > 0), "全部内容隐藏仍应保留订单标题。");
        Check(RichCompose(fixture, new() { ShowRare = false }).Rows.All(row => row.Identity.Contains(":False:")), "普客单独显示失败。");
        Check(RichCompose(fixture, new() { ShowNormal = false }).Rows.All(row => row.Identity.Contains(":True:")), "稀客单独显示失败。");
        Equal(Traces(RichCompose(fixture, new() { RareFirst = true })), new[] { "R-0002", "N-0001" }, "稀客优先错误改变显示顺序。");
    }

    private static void VerifyRichImages()
    {
        var fixture = Rich();
        var frame = RichCompose(fixture);
        var normal = frame.Rows[0]; var rare = frame.Rows[1];
        Equal(normal.AvatarKeys, new[] { "normal-group:3" }, "普客必须使用显式客群头像身份。");
        Equal(rare.AvatarKeys, new[] { "rare-guest:4" }, "真实稀客应使用规范guestId。");
        Icon(normal, "料理", "food:101"); Icon(rare, "料理", "food:102");
        Icon(normal, "酒水", "beverage:21"); Icon(rare, "酒水", "beverage:22");
        Icon(normal, "厨具", "cooker:2"); Icon(rare, "厨具", "cooker:1");
        Equal(Tags(normal, "配方").Select(tag => tag.IconKey ?? ""), new[] { "ingredient:1", "ingredient:2" }, "普客基础食材没有按双键依赖保序。");
        Equal(Tags(rare, "配方").Select(tag => tag.IconKey ?? ""), new[] { "ingredient:1", "ingredient:3" }, "稀客基础食材没有按双键依赖保序。");
        Icon(normal, "加料", "ingredient:3"); Icon(rare, "加料", "ingredient:4");
        Equal(Tags(normal, "点单").Select(tag => tag.IconKey ?? ""), new[] { "food:101", "beverage:21" }, "原点单缺少真实料理/酒水图片。");

        var copied = fixture with { Snapshot = Mutate(fixture.Snapshot, root =>
        {
            var order = root["normalBusiness"]!["orders"]![0]!;
            order["guestImageKind"] = "rare-guest"; order["guestImageId"] = 36; order["runtimeGuestId"] = 999999;
        }) };
        Equal(RichCompose(copied).Rows[0].AvatarKeys, new[] { "rare-guest:36" }, "普通订单形态的稀客分身不能套普客图。");
        foreach (var change in new Action<JsonObject>[]
        {
            order => order["guestImageKind"] = "", order => order["guestImageKind"] = "unknown",
            order => order["guestImageId"] = null, order => order["guestImageId"] = -1,
        })
        {
            var missing = fixture with { Snapshot = Mutate(fixture.Snapshot, root => change(root["normalBusiness"]!["orders"]![0]!.AsObject())) };
            Check(RichCompose(missing).Rows[0].AvatarKeys.Count == 0, "显式无效头像身份不能退回guestId/runtimeGuestId猜测。");
        }

        // 缺失、重复、错配的双身份仅撤销依赖图标，不能按中文材料名或厨具名补出另一张图。
        var wrongRecipe = fixture with { Catalog = (JsonObject)Clone(fixture.Catalog)! };
        wrongRecipe.Catalog["recipes"]![0]!["recipeId"] = 999;
        NoDependency(RichCompose(wrongRecipe).Rows[0]);
        var duplicateCatalog = fixture with { Catalog = (JsonObject)Clone(fixture.Catalog)! };
        duplicateCatalog.Catalog["recipes"]!.AsArray().Add(Clone(duplicateCatalog.Catalog["recipes"]![0]));
        NoDependency(RichCompose(duplicateCatalog).Rows[0]);
        var duplicateMedia = fixture with { Media = fixture.Media with { Recipes = fixture.Media.Recipes.Append(fixture.Media.Recipes[0]).ToArray() } };
        NoDependency(RichCompose(duplicateMedia).Rows[0]);
        var mismatchedFood = fixture with { Result = (JsonObject)Clone(fixture.Result)! };
        mismatchedFood.Result["normalOrderDetailPlans"]![0]!["executionFood"]!["foodId"] = 999;
        NoDependency(RichCompose(mismatchedFood).Rows[0]);
        var shortMedia = fixture with { Media = fixture.Media with { Recipes = fixture.Media.Recipes.Select(recipe => recipe.FoodId == 101 ? recipe with { IngredientIds = new[] { 1 } } : recipe).ToArray() } };
        Check(Tags(RichCompose(shortMedia).Rows[0], "配方").All(tag => tag.IconKey == null), "基础材料数量不符仍按位置错误拼图。");
        var repeats = fixture with { Media = fixture.Media with { Recipes = fixture.Media.Recipes.Select(recipe => recipe.FoodId == 101 ? recipe with { IngredientIds = new[] { 1, 1 } } : recipe).ToArray() } };
        Equal(Tags(RichCompose(repeats).Rows[0], "配方").Select(tag => tag.IconKey ?? ""), new[] { "ingredient:1", "ingredient:1" }, "重复食材被错误去重。");
        var unknownExtra = fixture with { Result = (JsonObject)Clone(fixture.Result)! };
        unknownExtra.Result["normalOrderDetailPlans"]![0]!["executionFood"]!["extraIngredients"]![0]!.AsObject().Remove("id");
        Check(Tags(RichCompose(unknownExtra).Rows[0], "加料").Single().IconKey == null, "加料名称不可代替显式ID。");
        foreach (var invalid in new JsonNode?[] { JsonValue.Create("3"), JsonValue.Create(3.5), JsonValue.Create(-1),
            JsonValue.Create(double.NaN), JsonValue.Create(double.PositiveInfinity), JsonValue.Create(double.NegativeInfinity),
            JsonValue.Create((double)int.MaxValue + 1), JsonValue.Create((long)int.MaxValue + 1), null })
        {
            var item = fixture with { Result = (JsonObject)Clone(fixture.Result)! };
            // NaN/Infinity不能序列化，直接把生产可能持有的内存数字交给边界检查，不能先以JSON往返抹掉类型。
            item.Result["normalOrderDetailPlans"]![0]!["executionFood"]!["extraIngredients"]![0]!["id"] = invalid;
            Check(Tags(RichCompose(item).Rows[0], "加料").Single().IconKey == null, "非法实体ID被宽松解析成图片身份。");
        }
        var integral = fixture with { Result = (JsonObject)Clone(fixture.Result)!, Catalog = (JsonObject)Clone(fixture.Catalog)! };
        integral.Result["normalOrderDetailPlans"]![0]!["executionFood"]!["foodId"] = 101d;
        integral.Result["normalOrderDetailPlans"]![0]!["executionFood"]!["recipe"]!["id"] = 101d;
        integral.Result["normalOrderDetailPlans"]![0]!["executionFood"]!["recipe"]!["recipeId"] = 201d;
        integral.Result["normalOrderDetailPlans"]![0]!["executionFood"]!["extraIngredients"]![0]!["id"] = 3d;
        integral.Catalog["recipes"]![0]!["id"] = 101d; integral.Catalog["recipes"]![0]!["recipeId"] = 201d;
        var integralRow = RichCompose(integral).Rows[0];
        Icon(integralRow, "料理", "food:101"); Icon(integralRow, "加料", "ingredient:3"); Icon(integralRow, "厨具", "cooker:2");
        Check(Tags(integralRow, "配方").All(tag => tag.IconKey != null), "值为精确整数的业务double没有恢复配方图片依赖。");
    }

    private static void VerifyRichMediaGeneration()
    {
        var fixture = Rich();
        var plans = RichCapture(fixture);
        var oldText = RichCompose(fixture).Rows.Select(row => row.Text()).ToArray();
        var newer = GameFocusOverlayProjection.Compose(fixture.Snapshot, plans, new() { Enabled = true }, new string('b', 32));
        Equal(newer.Rows.Select(row => row.Text()), oldText, "仅资源代次改变不应清空已核实的文字方案。");
        Check(newer.Rows.All(row => row.Groups.Where(group => group.Prefix != "点单").SelectMany(group => group.Tags).All(tag => tag.IconKey == null)), "旧方案图片依赖错误跨资源代次复用。");
        Check(newer.MediaGeneration == new string('b', 32), "返回帧没有携带当前资源代次。");
        var changed = fixture with { Media = fixture.Media with { Generation = new string('b', 32) } };
        Check(RichCompose(changed).Rows[0].Groups.Any(group => group.Tags.Any(tag => tag.IconKey == "food:101")), "新代次重新冻结后没有恢复图片。");
    }

    private static void VerifyRichColors()
    {
        var fixture = Rich();
        var frame = RichCompose(fixture);
        Check(frame.Rows[0].HighlightHex == "#3388FF" && frame.Rows[1].HighlightHex == "#FFAA33", "已确认颜色没有按精确身份映射。");
        foreach (var change in new Action<JsonObject>[]
        {
            target => target["businessGeneration"] = 60, target => target["traceId"] = "N-9999",
            target => target["orderLifecycleSequence"] = 999, target => target["orderKey"] = "wrong-key",
            target => target["kind"] = "rare", target => target["color"] = "#3388ff", target => target["color"] = "red",
        })
        {
            var broken = fixture with { Targets = (JsonObject)Clone(fixture.Targets)! };
            change(broken.Targets["orderColors"]![0]!.AsObject());
            Check(RichCompose(broken).Rows[0].HighlightHex == "#929AA6", "失配或非法颜色没有中性回退。");
        }
        var duplicates = fixture with { Targets = (JsonObject)Clone(fixture.Targets)! };
        duplicates.Targets["orderColors"]!.AsArray().Add(Clone(duplicates.Targets["orderColors"]![0]));
        Check(RichCompose(duplicates).Rows[0].HighlightHex == "#929AA6", "重复颜色身份不能取第一项。");
        var changedColors = fixture with { Targets = (JsonObject)Clone(fixture.Targets)! };
        changedColors.Targets["orderColors"]![0]!["color"] = "#22BBCC";
        Check(ReferenceEquals(changedColors.Result, fixture.Result) && RichCompose(changedColors).Rows[0].HighlightHex == "#22BBCC", "相同推荐引用阻止了独立颜色更新。");
        var demandChanged = fixture with { Snapshot = Mutate(fixture.Snapshot, root => root["normalBusiness"]!["orders"]![0]!["foodId"] = 999) };
        Check(RichCompose(demandChanged).Rows[0].HighlightHex == "#3388FF", "需求暂时等待新方案不应丢失同生命周期订单的颜色。");
        var pending = fixture with { Result = new JsonObject() };
        Check(RichCompose(pending).Rows[0].HighlightHex == "#3388FF", "现场订单有已知颜色时，不应因推荐行暂缺退回中性底色。");
    }

    private static void VerifyRichSpecialProducts()
    {
        foreach (var stored in new[] { false, true })
        {
            var fixture = Rich();
            var food = fixture.Result["recommendations"]![0]!["executionPlans"]![0]!["food"]!;
            var production = Clone(food["recipe"]);
            food["productKind"] = "dark-cuisine";
            food["supplySource"] = stored ? "stored" : "cook";
            food["productionRecipe"] = stored ? null : production;
            food["recipe"]!["id"] = -1;
            food["recipe"]!["name"] = "黑暗料理";
            var row = RichCompose(fixture).Rows[1];
            Check(Tags(row, "料理").Single().IconKey == null, "黑暗料理-1没有目录图片，不能借用基底图片冒充成品。");
            if (stored)
            {
                Check(Tags(row, "供给").Single().Text == "保温箱成品", "现成黑暗料理应使用供给说明。");
                Check(row.Groups.All(group => group.Prefix is not ("配方" or "加料" or "厨具" or "制作")), "保温箱成品仍显示不需要的制作材料。");
            }
            else
            {
                Check(Tags(row, "制作").Last().IconKey == "food:102" && Tags(row, "制作").Last().Text == "基底 · 味噌汤", "特殊产物的基底图片必须使用真实productionRecipe Food ID。");
                Equal(Tags(row, "配方").Select(tag => tag.IconKey ?? ""), new[] { "ingredient:1", "ingredient:3" }, "黑暗料理未使用基底双身份材料依赖。");
                Icon(row, "厨具", "cooker:1");
            }
            var hidden = RichCompose(fixture, new() { ShowFood = false }).Rows[1];
            Check(hidden.Groups.All(group => group.Prefix is not ("料理" or "制作" or "供给")), "隐藏料理时应同时隐藏其附属制作/供给说明。");
        }
    }

    private static void VerifyRichUnknowns()
    {
        var fixture = Rich();
        var partial = fixture with { Catalog = (JsonObject)Clone(fixture.Catalog)! };
        partial.Catalog["isComplete"] = false;
        NoDependency(RichCompose(partial).Rows[0]);
        var absent = fixture with { Result = (JsonObject)Clone(fixture.Result)! };
        absent.Result["normalOrderDetailPlans"]![0]!["executionFood"]!["recipe"] = null;
        absent.Result["normalOrderDetailPlans"]![0]!["executionFood"]!["baseIngredientNames"] = new JsonArray();
        Check(Tags(RichCompose(absent).Rows[0], "配方").Single().Text == "基础配方未知", "普客目录缺失不能把回退空集合当真实无基础材料。");
        var empty = fixture with { Result = (JsonObject)Clone(fixture.Result)! };
        empty.Result["normalOrderDetailPlans"]![0]!["executionFood"]!["extraIngredients"] = new JsonArray();
        Check(Tags(RichCompose(empty).Rows[0], "加料").Single().Text == "不加料", "明确空加料集合应显示不加料。");
        empty.Result["normalOrderDetailPlans"]![0]!["executionFood"]!["extraIngredients"] = null;
        Check(Tags(RichCompose(empty).Rows[0], "加料").Single().Text == "加料未知", "加料未读取不能显示不加料。");
    }

    private static RichFixture Rich()
    {
        var snapshot = Mutate(Fixture(), root =>
        {
            root["normalBusiness"]!["orders"]![0]!["guestImageKind"] = "normal-group";
            root["normalBusiness"]!["orders"]![0]!["guestImageId"] = 3;
        });
        var result = Results(snapshot);
        var normal = result["normalOrderDetailPlans"]![0]!["executionFood"]!;
        normal["foodId"] = 101;
        normal["recipe"] = new JsonObject { ["id"] = 101, ["recipeId"] = 201, ["name"] = "煎豆腐" };
        normal["extraIngredients"]![0]!["id"] = 3;
        result["normalOrderDetailPlans"]![0]!["executionBeverage"]!["beverageId"] = 21;
        var rare = result["recommendations"]![0]!["executionPlans"]![0]!;
        rare["food"]!["recipe"]!["id"] = 102; rare["food"]!["recipe"]!["recipeId"] = 202;
        rare["food"]!["extraIngredients"]![0]!["id"] = 4; rare["beverage"]!["beverage"]!["id"] = 22;
        var catalog = JsonNode.Parse("{\"isComplete\":true,\"recipes\":[{\"id\":101,\"recipeId\":201},{\"id\":102,\"recipeId\":202}]}")!.AsObject();
        var media = new GameMediaManifest(1, new string('a', 32), "fixture", Array.Empty<GameMediaItem>(),
            new[] { new GameMediaRecipe(101, 201, new[] { 1, 2 }, 2), new GameMediaRecipe(102, 202, new[] { 1, 3 }, 1) }, Array.Empty<GameMediaMissing>());
        var targets = new JsonObject { ["orderColors"] = new JsonArray(
            new JsonObject { ["kind"] = "normal", ["businessGeneration"] = 61, ["traceId"] = "N-0001", ["orderLifecycleSequence"] = 1, ["orderKey"] = "native:N-0001", ["color"] = "#3388FF" },
            new JsonObject { ["kind"] = "rare", ["businessGeneration"] = 61, ["traceId"] = "R-0002", ["orderLifecycleSequence"] = 2, ["color"] = "#FFAA33" }) };
        return new(snapshot, result, catalog, media, targets);
    }

    private static GameFocusOverlayPlans RichCapture(RichFixture fixture) => GameFocusOverlayProjection.Capture(
        JsonSerializer.SerializeToNode(fixture.Snapshot, JsonOptions)!.AsObject(), fixture.Result, fixture.Catalog, fixture.Media, fixture.Targets);
    private static GameFocusOverlayFrame RichCompose(RichFixture fixture, GameFocusOverlayContent? content = null)
        => GameFocusOverlayProjection.Compose(fixture.Snapshot, RichCapture(fixture), new() { Enabled = true, Content = content ?? new() }, fixture.Media.Generation);
    private static IReadOnlyList<GameFocusOverlayTag> Tags(GameFocusOverlayRow row, string prefix) => row.Groups.Single(group => group.Prefix == prefix).Tags;
    private static void Icon(GameFocusOverlayRow row, string prefix, string key) => Check(Tags(row, prefix).Single().IconKey == key, prefix + "图片身份错误。");
    private static void NoDependency(GameFocusOverlayRow row)
        => Check(Tags(row, "配方").All(tag => tag.IconKey == null) && Tags(row, "厨具").Single().IconKey == null, "未确认配方双身份仍生成依赖图片。");
}
