using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MystiaStewardCompanion.Business.Domain.Orders;
using MystiaStewardCompanion.Business.Domain.Recommendation;
using MystiaStewardCompanion.Business.Domain.SpecialBusiness;
using static MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

namespace MystiaStewardCompanion.Business.Domain.GameUi;

/// <summary>
/// 从已选择的主方案生成游戏辅助目标。精确订单身份、特殊目标修订和材料映射任一不完整时返回空目标，
/// 禁止使用相似名称、过期推荐或另一笔订单填补缺失信息。此类不直接修改游戏 UI。
/// </summary>
public static class GameUiTargetService
{
    /// <summary>统一 JSON 查询入口：rare、normal、rare-source、normal-source、reconcile。</summary>
    public static JsonNode? Evaluate(JsonObject payload) => Str(payload["operation"]) switch
    {
        "rare" => BuildRare(payload), "normal" => BuildNormal(payload),
        "rare-source" => Source(Obj(payload["order"]), false), "normal-source" => Source(Obj(payload["order"]), true),
        "reconcile" => Reconcile(payload["target"] as JsonObject, Objects(payload["sources"])),
        _ => throw new ArgumentException("游戏辅助目标操作无效。", nameof(payload))
    };
    /// <summary>
    /// 宿主热路径入口：直接读取冻结输入，避免 JSON 参数袋在选目标前复制无关的完整推荐树。
    /// 两个通道仍进入与公开 JSON 查询相同的算法，当前身份、特殊规则和任务优先级没有旁路。
    /// </summary>
    public static JsonObject? Evaluate(FrozenGameUiTargetInput input) => input.Operation switch
    {
        "rare" => BuildRare(input),
        "normal" => BuildNormal(input),
        _ => throw new ArgumentException("游戏辅助目标操作无效。", nameof(input))
    };

    /// <summary>
    /// 保留既有 JSON 稀客入口。仅提取字段引用与标量，不克隆参数；options 中显式提供的
    /// specialBusiness 仍优先于外层字段，JSON null 仍按原来的空合并规则回退。
    /// </summary>
    public static JsonObject? BuildRare(JsonObject args)
    {
        var options = Obj(args["options"]);
        return BuildRare(new FrozenGameUiTargetInput("rare", Obj(args["data"]),
            Recommendations: args["recommendations"],
            Color: Str(args["color"]), Features: args["features"],
            SpecialBusiness: options["specialBusiness"] ?? args["specialBusiness"],
            PrioritizeMissionRecipe: Bool(options["prioritizeMissionRecipe"])));
    }

    private static JsonObject? BuildRare(FrozenGameUiTargetInput input)
    {
        var rows = Objects(input.Recommendations).ToList(); var data = input.Data;
        var ordered = OrderIdentityAndSorting.SortRareForExecution(rows.Select(x => Obj(x["order"])), input.SpecialBusiness);
        var candidates = new List<(JsonObject Recommendation, JsonObject? Food, JsonObject? Beverage)>();
        foreach (var order in ordered)
        {
            if (!StrongIdentity(order, false)) continue;
            var recommendation = rows.First(x => ReferenceEquals(x["order"], order)); var plan = Objects(recommendation["executionPlans"]).FirstOrDefault();
            if (plan == null) continue;
            var food = Bool(order["hasServedFood"]) ? null : plan["food"] as JsonObject; var beverage = Bool(order["hasServedBeverage"]) ? null : plan["beverage"] as JsonObject;
            if (food != null || beverage != null) candidates.Add((recommendation, food, beverage));
        }
        if (candidates.Count == 0) return null;
        var selected = candidates[0];
        if (input.PrioritizeMissionRecipe)
        {
            foreach (var c in candidates)
            {
                var context = RecommendationEngine.GetVerifiedMissionRecipeSortContext(Obj(c.Recommendation["order"]));
                if (c.Food != null && context != null && RecommendationEngine.IsMissionRecipeExecutionPlan(Objects(c.Recommendation["executionPlans"]).First(), context)) { selected = c; break; }
            }
        }
        var o = Obj(selected.Recommendation["order"]); var recipe = Obj(selected.Food?["recipe"]); var drink = Obj(selected.Beverage?["beverage"]);
        var basis = ResolveIngredients(recipe["ingredients"], data); if (basis == null) return null;
        var extras = selected.Food == null ? new JsonArray() : Array(Objects(selected.Food["extraIngredients"]).Select(x => x["id"])); if (!ValidIds(extras)) return null;
        return Target("rare", o, input.Color, input.Features, recipe, drink, basis, extras, selected.Food == null ? -1 : Num(recipe["recipeId"]), selected.Beverage == null ? -1 : Num(drink["id"]), "");
    }

    /// <summary>
    /// 保留既有 JSON 普客入口及所有缺省值。特殊目标是否当前、经营代次和规则仍独立传入，
    /// 不因调用方使用强类型参数而把旧目标视为已验证。
    /// </summary>
    public static JsonObject? BuildNormal(JsonObject args)
    {
        return BuildNormal(new FrozenGameUiTargetInput("normal", Obj(args["data"]),
            Orders: args["orders"], ExecutionTargets: args["executionTargets"],
            ExecutionTargetsCurrent: Bool(args["executionTargetsCurrent"]), Color: Str(args["color"]),
            Features: args["features"], SpecialBusiness: args["specialBusiness"],
            BusinessGeneration: Num(args["businessGeneration"])));
    }

    private static JsonObject? BuildNormal(FrozenGameUiTargetInput input)
    {
        var data = input.Data; var special = input.SpecialBusiness; var selections = input.ExecutionTargetsCurrent ? Objects(input.ExecutionTargets).GroupBy(x => Str(x["orderKey"])).ToDictionary(x => x.Key, x => x.Last()) : new Dictionary<string, JsonObject>();
        foreach (var order in OrderIdentityAndSorting.SortNormalForExecution(Objects(input.Orders)))
        {
            if (!StrongIdentity(order, true) || Bool(order["hasEvaluated"]) || Bool(order["hasServedFood"]) && Bool(order["hasServedBeverage"])) continue;
            var required = SpecialBusinessRules.RequiresNormalTarget(special, Str(order["specialBusinessRole"]));
            selections.TryGetValue(OrderIdentityAndSorting.NormalKey(order), out var selection);
            var target = required && selection?["target"] is JsonObject candidate && IsCurrentNormalTarget(order, candidate, special, input.BusinessGeneration) ? candidate : null;
            if (required && target == null) continue;
            JsonObject? recipe = null;
            if (!Bool(order["hasServedFood"]))
            {
                var recipes = Objects(data["recipes"]);
                if (target == null) recipe = recipes.LastOrDefault(x => Num(x["id"]) == Num(order["foodId"]));
                else { var exact = recipes.Where(x => Num(x["id"]) == Num(target["foodId"]) && Num(x["recipeId"]) == Num(target["recipeId"])).ToArray(); if (exact.Length == 1) recipe = exact[0]; }
                if (recipe == null) continue;
            }
            var extras = Bool(order["hasServedFood"]) ? new JsonArray() : Arr(target?["extraIngredientIds"]); if (!ValidIds(extras)) continue;
            var basis = ResolveIngredients(recipe?["ingredients"], data); if (basis == null) continue;
            var beverageId = Bool(order["hasServedBeverage"]) ? -1 : Num(target?["beverageId"] ?? order["beverageId"]); if (!Bool(order["hasServedBeverage"]) && beverageId < 0) continue;
            var drink = Objects(data["beverages"]).LastOrDefault(x => Num(x["id"]) == beverageId) ?? new JsonObject();
            var result = Target("normal", order, input.Color, input.Features, recipe ?? new JsonObject(), drink, basis, extras, recipe == null ? -1 : Num(recipe["recipeId"]), beverageId, Str(order["orderKey"]));
            result["recipeName"] = SpecialBusinessRules.TextOr(Str(target?["recipeName"]), SpecialBusinessRules.TextOr(Str(recipe?["name"]), Str(order["foodName"])));
            result["beverageName"] = beverageId < 0 ? "" : SpecialBusinessRules.TextOr(Str(target?["beverageName"]), SpecialBusinessRules.TextOr(Str(drink["name"]), Str(order["beverageName"])));
            return result;
        }
        return null;
    }
    public static bool IsCurrentNormalTarget(JsonObject order, JsonObject target, JsonNode? special, double generation)
    {
        if (Num(target["matchFoodId"]) != Num(order["foodId"]) || Num(target["matchBeverageId"]) != Num(order["beverageId"])) return false;
        var policy = SpecialBusinessRules.BuildWirePolicy(special, Str(order["specialBusinessRole"]), generation);
        return policy.All(field => target[field.Key]?.ToJsonString() == field.Value?.ToJsonString());
    }
    public static JsonObject Source(JsonObject order, bool normal) => Object(("kind", normal ? "normal" : "rare"), ("sourceOrderKey", normal ? OrderIdentityAndSorting.NormalKey(order) : RareKey(order)), ("sourceOrderSignature", SourceSignature(order, normal)), ("hasServedFood", Bool(order["hasServedFood"])), ("hasServedBeverage", Bool(order["hasServedBeverage"])), ("terminal", normal && Bool(order["hasEvaluated"])));
    public static JsonObject? Reconcile(JsonObject? target, IEnumerable<JsonObject> sources)
    {
        if (target == null) return null;
        var matches = sources.Where(x => Str(x["kind"]) == Str(target["kind"]) && Str(x["sourceOrderKey"]) == Str(target["sourceOrderKey"]) && Str(x["sourceOrderSignature"]) == Str(target["sourceOrderSignature"])).ToArray();
        if (matches.Length != 1 || Bool(matches[0]["terminal"])) return null;
        var next = Obj(Clone(target));
        if (Bool(matches[0]["hasServedFood"])) { next["recipeId"] = -1; next["recipeName"] = ""; next["ingredientIds"] = new JsonArray(); next["extraIngredientIds"] = new JsonArray(); next["cookerTypeId"] = -1; next["cookerName"] = ""; }
        if (Bool(matches[0]["hasServedBeverage"])) { next["beverageId"] = -1; next["beverageName"] = ""; }
        if (Num(next["recipeId"]) < 0 && Num(next["beverageId"]) < 0) return null;
        next["targetRevision"] = Revision(next); return next;
    }
    private static JsonObject Target(string kind, JsonObject order, string color, JsonNode? features, JsonObject recipe, JsonObject beverage, double[] basis, JsonArray extras, double recipeId, double beverageId, string orderKey)
    {
        var result = Object(("kind", kind), ("color", color), ("features", features ?? new JsonObject()), ("sourceOrderKey", kind == "normal" ? OrderIdentityAndSorting.NormalKey(order) : RareKey(order)), ("sourceOrderSignature", SourceSignature(order, kind == "normal")),
            ("traceId", Str(order["traceId"])), ("orderKey", orderKey), ("orderLifecycleSequence", Num(order["orderLifecycleSequence"])), ("deskCode", Num(order["deskCode"])),
            ("recipeId", recipeId), ("recipeName", Str(recipe["name"])), ("ingredientIds", Array(basis.Concat(Numbers(extras)).Where(x => x >= 0 && double.IsFinite(x)).Select(Math.Truncate).Distinct().OrderBy(x => x))), ("extraIngredientIds", extras),
            ("beverageId", beverageId), ("beverageName", Str(beverage["name"])), ("cookerTypeId", CookerType(Str(recipe["cooker"]))), ("cookerName", Str(recipe["cooker"])));
        result["targetRevision"] = Revision(result); return result;
    }
    private static string Revision(JsonObject t) => string.Join("|", Str(t["kind"]), Str(t["sourceOrderKey"]), Str(t["sourceOrderSignature"]), Str(t["traceId"]), Str(t["orderKey"]), Key(Num(t["orderLifecycleSequence"])), Key(Num(t["recipeId"])), string.Join(",", Numbers(t["ingredientIds"]).Select(Key)), string.Join(",", Numbers(t["extraIngredientIds"]).Select(Key)), Key(Num(t["beverageId"])), Key(Num(t["cookerTypeId"])), Key(Num(t["deskCode"])));
    private static string RareKey(JsonObject o) => $"{Str(o["traceId"])}|lifecycle:{Key(Num(o["orderLifecycleSequence"]))}";
    private static string SourceSignature(JsonObject o, bool normal)
    {
        var names = normal ? new[] { "traceId", "orderKey", "orderLifecycleSequence", "firstSeenAtUtc", "deskCode", "guestId", "runtimeGuestId", "specialBusinessRole", "foodId", "beverageId" } : new[] { "traceId", "orderLifecycleSequence", "firstSeenAtUtc", "deskCode", "guestId", "runtimeGuestId", "specialBusinessRole", "foodTagId", "beverageTagId" };
        var values = names.Select(k => NullableNumber(o[k]) is double n ? Key(n) : Str(o[k])).ToList(); if (!normal) values.Add(Bool(o["isFreeOrder"]) ? "1" : "0"); return string.Join("|", values);
    }
    private static bool StrongIdentity(JsonObject o, bool normal) => Num(o["orderLifecycleSequence"]) > 0 && Num(o["deskCode"]) >= 0 && Regex.IsMatch(Str(o["traceId"]), normal ? @"^N-[0-9]{1,16}$" : @"^R-[0-9]{1,16}$") && (!normal || Regex.IsMatch(Str(o["orderKey"]), @"^ptr:[0-9a-f]{1,16}$") && Regex.IsMatch(Str(o["orderKey"])[4..], "[1-9a-f]"));
    private static bool ValidIds(JsonArray values) => values.All(x => Integer(x) && Num(x) >= 0);
    private static double[]? ResolveIngredients(JsonNode? names, JsonObject data)
    { var ingredients = Objects(data["ingredients"]).GroupBy(x => Str(x["name"])).ToDictionary(x => x.Key, x => x.Last()); var ids = Strings(names).Select(x => ingredients.TryGetValue(x, out var value) ? Num(value["id"], -1) : -1).ToArray(); return ids.All(x => x >= 0) ? ids : null; }
    private static int CookerType(string name) => name.Trim() switch { "料理台" => 5, "煮锅" or "锅" => 1, "烤架" or "烧烤架" or "烧烤台" => 2, "蒸锅" => 4, "油锅" or "炸锅" => 3, _ => -1 };
}
