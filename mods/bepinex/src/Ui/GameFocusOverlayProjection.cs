using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using System.Text.Json;
using MystiaStewardCompanion.Business.Domain.Orders;
using MystiaStewardCompanion.Business.Application;
using MystiaStewardCompanion.GameMedia;
using MystiaStewardCompanion.LocalApi;

namespace MystiaStewardCompanion.Ui;

/// <summary>只读方案仍须精确匹配生命周期；桌号、名称都不是联结身份。</summary>
internal readonly record struct GameFocusOrderIdentity(bool Rare, string Trace, long Lifecycle, int? Guest, string OrderKey)
{
    public bool IsValid => Trace.Length > 0 && Lifecycle > 0 && (Rare ? Guest >= 0 : OrderKey.Length > 0);
}

/// <summary>后台仅冻结展示标签和图片键，不跨线程传递可变JSON或Unity对象。</summary>
internal sealed record GameFocusOverlayPlan(string DemandFingerprint, IReadOnlyList<GameFocusOverlayGroup> Groups);

/// <summary>
/// 与外部专注页完全相同的展示来源，额外保留原始目录引用，防止目录先于快照签名更新的空隙。
/// 两个字符串均已冻结；不携带可变JSON，不以不断增长的业务输入版本清空同一作用域的旧方案。
/// </summary>
internal sealed record GameFocusOverlaySourceScope(string Context, string CatalogJson)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static GameFocusOverlaySourceScope FromSnapshot(LocalApiSnapshot snapshot,
        CompanionAuthorityDisplayScope authority, string catalogJson)
    {
        // 仅投影BusinessDisplayContext需要的少量字段，绝不序列化订单、库存、诊断等完整现场快照。
        // 特殊经营仍交给共享Build挑选字段，破盾数值/未知挑战错误的条件语义只有一份定义。
        var source = new JsonObject {
            ["automationSessionId"] = snapshot.AutomationSessionId,
            ["nightBusinessGeneration"] = snapshot.NightBusinessGeneration,
            ["nightBusinessLifecyclePhase"] = snapshot.NightBusinessLifecyclePhase,
            ["activeSceneName"] = snapshot.ActiveSceneName,
            ["runtimeLoaded"] = snapshot.RuntimeLoaded,
            ["runtimeDaySceneGeneration"] = snapshot.RuntimeDaySceneGeneration,
            ["runtimeDaySceneReady"] = snapshot.RuntimeDaySceneReady,
            ["runtimeDataSignature"] = snapshot.RuntimeDataSignature,
            ["specialBusiness"] = JsonSerializer.SerializeToNode(snapshot.SpecialBusiness, JsonOptions),
        };
        return new(BusinessDisplayContext.Build(source, authority.RegistryId, authority.AuthorityRevision,
            authority.ActiveProfileRevision, authority.ActiveProfileHash).ToJsonString(), catalogJson);
    }
}

internal sealed record GameFocusOverlayPlans(long Generation, string Scene, string CatalogSignature, string MediaGeneration,
    IReadOnlyDictionary<GameFocusOrderIdentity, GameFocusOverlayPlan> Details,
    IReadOnlyDictionary<GameFocusOrderIdentity, string> Colors, GameFocusOverlaySourceScope? SourceScope = null)
{
    public static GameFocusOverlayPlans Empty { get; } = new(0, "", "", "",
        new ReadOnlyDictionary<GameFocusOrderIdentity, GameFocusOverlayPlan>(new Dictionary<GameFocusOrderIdentity, GameFocusOverlayPlan>()),
        new ReadOnlyDictionary<GameFocusOrderIdentity, string>(new Dictionary<GameFocusOrderIdentity, string>()));
}

/// <summary>
/// 镜像Flutter专注页的展示语义：同一主方案、前缀/标签、精确图片和订单底色。
/// 不执行候选计算或游戏动作；内容开关仅过滤已冻结字段，不刷新业务输入。
/// </summary>
internal static class GameFocusOverlayProjection
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private sealed record LiveOrder(GameFocusOrderIdentity Id, JsonObject Values, int Position = 0);

    public static GameFocusOverlayPlans Capture(JsonObject snapshot, JsonObject result, JsonObject? catalog = null,
        GameMediaManifest? media = null, JsonObject? targets = null, GameFocusOverlaySourceScope? sourceScope = null)
    {
        var details = new Dictionary<GameFocusOrderIdentity, GameFocusOverlayPlan>();
        var colors = new Dictionary<GameFocusOrderIdentity, string>();
        var duplicates = new HashSet<GameFocusOrderIdentity>();
        var generation = Long(snapshot["nightBusinessGeneration"]);
        foreach (var (rare, field) in new[] { (true, "recommendations"), (false, "normalOrderDetailPlans") })
        {
            if (result[field] is not JsonArray rows) continue;
            foreach (var row in rows.OfType<JsonObject>())
            {
                if (row["order"] is not JsonObject order) continue;
                var id = Identity(order, rare);
                if (!id.IsValid) continue;
                if (details.ContainsKey(id)) { duplicates.Add(id); continue; }
                details[id] = new(Fingerprint(order), DescribePlan(row, rare, catalog, media));
            }
        }
        foreach (var id in duplicates) details.Remove(id);
        // 底色属于当前订单生命周期，不依赖推荐是否已生成。新单的占位卡片也须保持
        // 同一颜色；这里只联结精确身份，实际显示仍由Compose的完整现场集合控制。
        var identities = new HashSet<GameFocusOrderIdentity>(details.Keys);
        foreach (var (rare, field) in new[] { (true, "nightBusiness"), (false, "normalBusiness") })
            if (snapshot[field]?["orders"] is JsonArray orders)
                foreach (var order in orders.OfType<JsonObject>())
                {
                    var id = Identity(order, rare);
                    if (id.IsValid) identities.Add(id);
                }
        foreach (var id in identities)
        {
            var matches = (targets?["orderColors"] as JsonArray)?.OfType<JsonObject>().Where(color =>
                Text(color["kind"]) == (id.Rare ? "rare" : "normal") && Long(color["businessGeneration"]) == generation
                && Text(color["traceId"]) == id.Trace && Long(color["orderLifecycleSequence"]) == id.Lifecycle
                && (id.Rare || Text(color["orderKey"]) == id.OrderKey)).Take(2).ToArray();
            var value = matches?.Length == 1 ? Text(matches[0]["color"]) : "";
            if (value.Length == 7 && value[0] == '#' && value.Skip(1).All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F'))
                colors[id] = value;
        }
        return new(generation, Text(snapshot["activeSceneName"]), Text(snapshot["runtimeDataSignature"]), media?.Generation ?? "",
            new ReadOnlyDictionary<GameFocusOrderIdentity, GameFocusOverlayPlan>(details),
            new ReadOnlyDictionary<GameFocusOrderIdentity, string>(colors), sourceScope
                ?? new(BusinessDisplayContext.Build(snapshot, "", 0, 0, "").ToJsonString(), ""));
    }

    public static GameFocusOverlayFrame Compose(LocalApiSnapshot snapshot, GameFocusOverlayPlans plans,
        GameFocusOverlaySettings settings, string mediaGeneration = "", GameFocusOverlaySourceScope? sourceScope = null)
    {
        if (!settings.Enabled || snapshot.NightBusinessGeneration <= 0 || snapshot.NightBusinessLifecyclePhase != "Active")
            return GameFocusOverlayFrame.Hidden;
        var content = settings.Content;
        var current = new List<LiveOrder>();
        // 当前现场事实单独读取，旧方案不能复活消失的订单；只在快照变化时处理少量订单。
        if (content.ShowRare && snapshot.NightBusiness is { OrdersReadComplete: true } rare && string.IsNullOrEmpty(rare.Error))
            current.AddRange(rare.Orders.Select(order => Live(JsonSerializer.SerializeToNode(order, JsonOptions)!.AsObject(), true)));
        if (content.ShowNormal && snapshot.NormalBusiness is { OrdersReadComplete: true } normal && string.IsNullOrEmpty(normal.Error))
            current.AddRange(normal.Orders.Select(order => Live(JsonSerializer.SerializeToNode(order, JsonOptions)!.AsObject(), false)));
        var duplicates = current.GroupBy(order => order.Id).Where(group => group.Count() != 1).Select(group => group.Key).ToHashSet();
        current = current.Where(order => order.Id.IsValid && !duplicates.Contains(order.Id)).ToList();
        var ordered = OrderIdentityAndSorting.SortPresentation(current.Select((order, index) => (
            Kind: order.Id.Rare ? "rare" : "normal",
            Order: new JsonObject {
                ["firstSeenAtUtc"] = Text(order.Values["firstSeenAtUtc"]), ["traceId"] = order.Id.Trace,
                ["orderLifecycleSequence"] = order.Id.Lifecycle, ["orderKey"] = order.Id.OrderKey,
                ["deskCode"] = Long(order.Values["deskCode"]), ["overlayIndex"] = index,
            }))).Select((entry, index) => current[(int)Long(entry.Order["overlayIndex"])] with { Position = index }).ToList();
        var participation = snapshot.RareGuestParticipation;
        if (content.ShowRare && participation?.DisplayOrderKeys != null)
        {
            var queue = new List<LiveOrder>();
            var byKey = ordered.Where(order => order.Id.Rare).ToDictionary(order => order.Id);
            var used = new HashSet<GameFocusOrderIdentity>();
            // 显示键不是参与资格筛选器。只有它完整、唯一地覆盖当前已读现场订单，才允许
            // 改变稀客的显示先后；缺项、迟到旧键或未就绪一律保留现场时间排序，不能清空稀客。
            var valid = participation.Active && participation.Ready
                && participation.BusinessGeneration == snapshot.NightBusinessGeneration
                && participation.DisplayOrderKeys.Count == byKey.Count;
            foreach (var key in participation.DisplayOrderKeys)
            {
                if (!valid || key == null) { valid = false; break; }
                var id = new GameFocusOrderIdentity(true, key.TraceId ?? "", key.OrderLifecycleSequence, key.GuestId, "");
                if (key.BusinessGeneration != snapshot.NightBusinessGeneration || !used.Add(id) || !byKey.TryGetValue(id, out var order))
                { valid = false; break; }
                queue.Add(order);
            }
            if (valid && used.Count == byKey.Count)
            {
                // 完整队列含参与与暂停订单，只置换全部稀客原显示槽位，普客位置保持不变。
                var slots = queue.Select(order => order.Position).OrderBy(value => value).ToArray();
                ordered.RemoveAll(order => order.Id.Rare);
                ordered.AddRange(queue.Select((order, index) => order with { Position = slots[index] }));
            }
        }
        // 暂停徽标来自独立的当前事实，不由队列缺项、姓名或同一guestId推断。
        // 多张同客订单按trace+生命周期分别核对；与Flutter一样，只有完整唯一的当前集合
        // 才可解释暂停分组，部分/重复entry保留现场显示但不猜测暂停，也不能取第一条。
        var paused = new HashSet<GameFocusOrderIdentity>();
        if (participation is { Active: true, Ready: true } && participation.BusinessGeneration == snapshot.NightBusinessGeneration
            && participation.Entries != null)
        {
            var currentRare = current.Where(order => order.Id.Rare).Select(order => order.Id).ToHashSet();
            var entries = participation.Entries.OfType<LocalApiRareGuestParticipationEntry>().ToArray();
            var ids = entries.Select(entry => new GameFocusOrderIdentity(true, entry.TraceId ?? "",
                entry.OrderLifecycleSequence, entry.GuestId, "")).ToArray();
            if (entries.Length == currentRare.Count && ids.Distinct().Count() == ids.Length && ids.All(currentRare.Contains))
            {
                for (var index = 0; index < entries.Length; index++)
                    if (entries[index].Managed && !entries[index].Participating) paused.Add(ids[index]);
            }
        }
        // 旧方案必须同时属于当前完整展示作用域。这里的默认构建只服务纯托管调用/测试；
        // 生产控制器传入缓存scope，稳定帧既不进入Compose，也不重复编码上下文。
        sourceScope ??= GameFocusOverlaySourceScope.FromSnapshot(snapshot, CompanionAuthorityDisplayScope.Empty, "");
        var contextMatches = plans.Generation == snapshot.NightBusinessGeneration
            && plans.Scene == snapshot.ActiveSceneName && plans.CatalogSignature == snapshot.RuntimeDataSignature
            && plans.SourceScope != null && plans.SourceScope == sourceScope;
        // 暂停分组始终最后，优先于用户的稀客优先选项；其余分组和点单/参与顺序不变。
        // 只读排序不会启用暂停单，也不会授予自动化或原生高亮资格。
        var rows = ordered.OrderBy(order => paused.Contains(order.Id) ? 1 : 0)
            .ThenBy(order => content.RareFirst && !order.Id.Rare ? 1 : 0).ThenBy(order => order.Position)
            .Select(order => BuildRow(order, plans, content, snapshot.NightBusinessGeneration, contextMatches,
                plans.MediaGeneration == mediaGeneration, paused.Contains(order.Id))).ToArray();
        return new(true, snapshot.NightBusinessGeneration, mediaGeneration, Array.AsReadOnly(rows));
    }

    private static LiveOrder Live(JsonObject values, bool rare) => new(Identity(values, rare), values);

    private static GameFocusOverlayRow BuildRow(LiveOrder live, GameFocusOverlayPlans plans, GameFocusOverlayContent content,
        long generation, bool contextMatches, bool mediaMatches, bool schedulingPaused)
    {
        var id = live.Id; var order = live.Values;
        var groups = new List<GameFocusOverlayGroup>();
        if (content.ShowOrderTags)
            groups.Add(id.Rare ? Group("点单", Tag("料理需求：" + Clean(Text(order["foodTag"])), GameFocusOverlayTone.Request),
                    Tag("酒水需求：" + Clean(Text(order["beverageTag"])), GameFocusOverlayTone.Request))
                : Group("点单", Tag("料理 · " + Clean(Text(order["foodName"])), GameFocusOverlayTone.Recipe, Key("food", order["foodId"])),
                    Tag("酒水 · " + Clean(Text(order["beverageName"])), GameFocusOverlayTone.Beverage, Key("beverage", order["beverageId"]))));
        var matched = contextMatches && plans.Details.TryGetValue(id, out var known) && known.DemandFingerprint == Fingerprint(order);
        var fields = matched ? plans.Details[id].Groups : Pending();
        foreach (var field in fields)
        {
            var visible = field.Prefix switch {
                "料理" or "制作" or "供给" => content.ShowFood, "配方" => content.ShowIngredients,
                "加料" => content.ShowExtras, "厨具" => content.ShowCooker, "酒水" => content.ShowBeverage, _ => true,
            };
            if (!visible) continue;
            // 资源代次变化不能把旧配方图片依赖绑定新资源；已核实需求的文字仍可保留。
            groups.Add(mediaMatches ? field : new(field.Prefix, Array.AsReadOnly(field.Tags.Select(tag => tag with { IconKey = null }).ToArray())));
        }
        var block = Text(order[id.Rare ? "automationBlockReason" : "actionBlockReason"]);
        if (order[id.Rare ? "automationAllowed" : "canAutomate"] is JsonValue allowed
            && allowed.TryGetValue<bool>(out var permitted) && !permitted && block.Length != 0 && block != "暂不可自动处理")
            groups.Add(Group("说明", Tag(block, GameFocusOverlayTone.Warning)));
        var progress = Progress(order);
        // 游戏内没有提示浮层：用暂停说明替换稀客类型徽标，保留原阶段且最多两个徽标。
        // 这仅是标题展示，不改变自动化参与、执行门槛或游戏高亮来源。
        var badges = new[] { Tag(schedulingPaused ? "调度暂停" : id.Rare ? "稀客" : "普客",
                schedulingPaused ? GameFocusOverlayTone.Warning : GameFocusOverlayTone.Neutral),
            Tag(progress, progress is "已评价" or "已送齐" or "已有料理" or "已有酒水" ? GameFocusOverlayTone.Success : GameFocusOverlayTone.Request) };
        var image = order.ContainsKey("guestImageKind")
            ? Text(order["guestImageKind"]) is "rare-guest" or "normal-group" ? Key(Text(order["guestImageKind"]), order["guestImageId"]) : null
            : id.Rare ? Key("rare-guest", order["guestId"]) : null;
        var desk = Long(order["deskCode"]);
        return new($"{generation}:{id.Rare}:{id.Trace}:{id.Lifecycle}:{id.Guest}:{id.OrderKey}",
            $"{(desk >= 0 ? (desk + 1).ToString() : "—")} 桌 · {Clean(Text(order["guestName"]))}",
            Array.AsReadOnly(image == null ? Array.Empty<string>() : new[] { image }), Array.AsReadOnly(badges),
            contextMatches && plans.Colors.TryGetValue(id, out var color) ? color : "#929AA6", Array.AsReadOnly(groups.ToArray()));
    }

    /// <summary>字段顺序及特殊成品说明与Flutter专注相同，不复制收藏等交互按钮。</summary>
    private static IReadOnlyList<GameFocusOverlayGroup> DescribePlan(JsonObject row, bool rare, JsonObject? catalog, GameMediaManifest? media)
    {
        var plan = rare ? (row["executionPlans"] as JsonArray)?.FirstOrDefault() as JsonObject : null;
        var food = (rare ? plan?["food"] : row["executionFood"]) as JsonObject;
        var drink = (rare ? plan?["beverage"] : row["executionBeverage"]) as JsonObject;
        if (food == null && drink == null) return Array.AsReadOnly(new[] { Group("说明", Tag("暂无主方案，请到订单页查看原因。", GameFocusOverlayTone.Warning)) });
        var entity = food?["recipe"] as JsonObject;
        var special = Text(food?["productKind"]) == "dark-cuisine";
        var stored = special && Text(food?["supplySource"]) == "stored";
        var production = special ? food?["productionRecipe"] as JsonObject : entity;
        var dependency = RecipeDependency(production, catalog, media);
        if (!rare && EntityId(food?["foodId"]) != EntityId(entity?["id"])) dependency = null;
        var groups = new List<GameFocusOverlayGroup> {
            Group("料理", Tag(Clean(Text(rare ? entity?["name"] : food?["name"])), GameFocusOverlayTone.Recipe,
                Key("food", rare ? entity?["id"] : food?["foodId"]))),
        };
        if (stored) groups.Add(Group("供给", Tag("保温箱成品", GameFocusOverlayTone.Success)));
        else
        {
            if (special) groups.Add(Group("制作", Tag("任务成品", GameFocusOverlayTone.Warning),
                Tag("基底 · " + Clean(Text(production?["name"])), GameFocusOverlayTone.Recipe, Key("food", production?["id"]))));
            var ingredients = special ? production?["ingredients"] : rare ? entity?["ingredients"] : food?["baseIngredientNames"];
            // 普客候选在目录缺失时会带空材料数组；它代表尚未知晓配方，不能误写成
            // 确认无需材料。与Flutter的recipe实体存在性判断保持相同语义。
            if (!special && !rare && entity == null && ingredients is JsonArray { Count: 0 }) ingredients = null;
            groups.Add(IngredientGroup("配方", ingredients, false, dependency?.IngredientIds));
            groups.Add(IngredientGroup("加料", food?["extraIngredients"], true, null));
            groups.Add(Group("厨具", Tag(Clean(Text(special ? production?["cooker"] : rare ? entity?["cooker"] : food?["cookerName"])),
                GameFocusOverlayTone.Cooker, dependency?.CookerType is >= 1 and <= 5 ? "cooker:" + dependency.CookerType : null)));
        }
        var beverage = rare ? drink?["beverage"] : drink;
        groups.Add(Group("酒水", Tag(Clean(Text(beverage?["name"])), GameFocusOverlayTone.Beverage,
            Key("beverage", beverage?[rare ? "id" : "beverageId"]))));
        if (Text(plan?["bucket"]) == "blocked") groups.Add(Group("说明", Tag("当前主方案不可执行。", GameFocusOverlayTone.Warning)));
        if (row["blockedMessages"] is JsonArray messages)
            foreach (var message in messages.Take(8).Select(Text).Where(value => value.Length != 0))
                groups.Add(Group("阻断", Tag(message, GameFocusOverlayTone.Warning)));
        return Array.AsReadOnly(groups.ToArray());
    }

    /// <summary>图片依赖必须同时命中唯一料理目录和FoodId/RecipeId双键，名称不参与联结。</summary>
    private static GameMediaRecipe? RecipeDependency(JsonObject? entity, JsonObject? catalog, GameMediaManifest? media)
    {
        var foodId = EntityId(entity?["id"]); var recipeId = EntityId(entity?["recipeId"]);
        if (foodId < 0 || recipeId < 0 || catalog?["recipes"] is not JsonArray recipes || media == null
            || catalog["isComplete"] is JsonValue complete && complete.TryGetValue<bool>(out var ready) && !ready) return null;
        var matches = recipes.OfType<JsonObject>().Where(item => EntityId(item["id"]) == foodId).Take(2).ToArray();
        if (matches.Length != 1 || EntityId(matches[0]["recipeId"]) != recipeId) return null;
        var dependencies = media.Recipes.Where(item => item.FoodId == foodId && item.RecipeId == recipeId).Take(2).ToArray();
        return dependencies.Length == 1 ? dependencies[0] : null;
    }

    private static GameFocusOverlayGroup IngredientGroup(string prefix, JsonNode? input, bool extra, int[]? ids)
    {
        var tone = extra ? GameFocusOverlayTone.Extra : GameFocusOverlayTone.Ingredient;
        if (input is not JsonArray items) return Group(prefix, Tag(extra ? "加料未知" : "基础配方未知", GameFocusOverlayTone.Warning));
        if (items.Count == 0) return Group(prefix, Tag(extra ? "不加料" : "无基础材料", tone));
        var exact = ids != null && ids.Length == items.Count && ids.All(id => id >= 0);
        return new(prefix, Array.AsReadOnly(items.Take(16).Select((item, index) => Tag(
            Clean(item is JsonObject entry ? Text(entry["name"]) : Text(item)), tone,
            exact ? "ingredient:" + ids![index] : item is JsonObject entryId ? Key("ingredient", entryId["id"]) : null)).ToArray()));
    }

    private static IReadOnlyList<GameFocusOverlayGroup> Pending() => Array.AsReadOnly(new[] { "料理", "配方", "加料", "厨具", "酒水" }
        .Select(prefix => Group(prefix, Tag("加载中…", GameFocusOverlayTone.Neutral))).ToArray());
    private static GameFocusOverlayGroup Group(string prefix, params GameFocusOverlayTag[] tags) => new(prefix, Array.AsReadOnly(tags));
    private static GameFocusOverlayTag Tag(string text, GameFocusOverlayTone tone, string? key = null) => new(Clean(text), tone, key);
    private static string? Key(string kind, JsonNode? id) => EntityId(id) is var value && value >= 0 ? kind + ":" + value : null;

    /// <summary>
    /// 推荐领域的数字访问器会把部分实体ID写成JsonValue&lt;double&gt;，投影直接读取内存树，
    /// 不能依赖HTTP序列化后重新变成整数。只接受Int32范围的精确整数值，不接受字符串、
    /// 小数或非有限数；订单生命周期仍使用下方严格整数读取，避免放宽身份校验。
    /// </summary>
    private static int EntityId(JsonNode? node)
    {
        var integer = Long(node);
        if (integer is >= 0 and <= int.MaxValue) return (int)integer;
        return node is JsonValue value && value.TryGetValue<double>(out var number)
            && double.IsFinite(number) && number >= 0 && number <= int.MaxValue && number == Math.Truncate(number)
            ? (int)number : -1;
    }
    private static string Progress(JsonObject order) => Flag(order["hasEvaluated"]) ? "已评价" : Flag(order["readyToEvaluate"]) ? "待评价"
        : Flag(order["hasServedFood"]) && Flag(order["hasServedBeverage"]) ? "已送齐" : Flag(order["hasServedFood"]) ? "已有料理"
        : Flag(order["hasServedBeverage"]) ? "已有酒水" : "待送达";
    private static bool Flag(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var result) && result;
    private static GameFocusOrderIdentity Identity(JsonObject order, bool rare) => new(rare, Text(order["traceId"]),
        Long(order["orderLifecycleSequence"]), Long(order["guestId"]) is >= 0 and <= int.MaxValue ? (int)Long(order["guestId"]) : null,
        rare ? "" : Text(order["orderKey"]));

    /// <summary>仅忽略采集来源、末次观察和显示槽位，任何需求改变先撤销旧方案。</summary>
    private static string Fingerprint(JsonNode node)
    {
        JsonNode? Canonical(JsonNode? item, bool root = false) => item switch {
            JsonObject map => new JsonObject(map.Where(pair => !root || pair.Key is not ("source" or "lastSeenAtUtc" or "serviceOrderPosition" or "patienceRemainingRatio"))
                .OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => KeyValuePair.Create(pair.Key, Canonical(pair.Value)))),
            JsonArray array => new JsonArray(array.Select(value => Canonical(value)).ToArray()), null => null,
            _ => JsonNode.Parse(item.ToJsonString()),
        };
        return Canonical(node, true)!.ToJsonString();
    }
    private static long Long(JsonNode? node) => node is JsonValue value
        ? value.TryGetValue<long>(out var result) ? result : value.TryGetValue<int>(out var small) ? small : -1 : -1;
    private static string Text(JsonNode? node) => node is JsonValue value
        ? value.TryGetValue<string>(out var text) ? text : value.TryGetValue<DateTime>(out var date) ? date.ToString("O") : "" : "";
    private static string Clean(string value) => string.IsNullOrWhiteSpace(value) ? "待确认" : value.Replace('\n', ' ').Replace('\r', ' ')[..Math.Min(value.Length, 160)];
}
