using System.Text.Json.Nodes;
using static MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

namespace MystiaStewardCompanion.Business.Domain.Recommendation;

public static partial class RecommendationEngine
{
    /// <summary>
    /// 为已验证的黑暗料理任务选择真实基底配方和一项禁忌加料。黑暗料理是成品身份，
    /// 没有自己的目录配方；productionRecipe 保存实际扣料/开锅身份，recipe 仅是统一组件
    /// 所需的成品展示投影。最终成品在原生 FinishCooking 中生成并再次验证，不能凭候选
    /// 直接造出或送达。该方法只接收托管目录，不读 Unity，不将特殊项写回普通目录。
    /// </summary>
    public static JsonArray BuildDarkCuisineCandidates(JsonObject data, JsonObject demand, JsonObject context)
    {
        var products = Objects(data["specialProducts"])
            .Where(p => Integer(p["id"]) && Num(p["id"]) == -1 && Str(p["productKind"]) == "dark-cuisine")
            .Take(2).ToArray();
        if (products.Length != 1) return new JsonArray();
        var product = products[0];
        if (Str(product["name"]).Length == 0 || !Integer(product["price"]) || Num(product["price"]) < 0)
            return new JsonArray();
        // 身份歧义不能以 First/Last 隐藏。名称仅供已有成本显示器读取；生产约束全部按 ID 验证。
        var allIngredients = Objects(data["ingredients"]).ToArray();
        var byId = allIngredients.Where(i => Integer(i["id"]) && Num(i["id"]) >= 0)
            .GroupBy(i => Num(i["id"])).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single());
        var byName = allIngredients.GroupBy(i => Str(i["name"])).Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single());
        var quantities = Obj(context["ownedIngredientQty"]);
        var candidates = new List<JsonObject>();
        if (Integer(context["storedDarkCuisineCount"]) && Num(context["storedDarkCuisineCount"]) > 0)
        {
            var display = Obj(Clone(product));
            display["recipeId"] = null; display["ingredients"] = new JsonArray();
            display["ingredientIds"] = new JsonArray(); display["negativeTags"] = new JsonArray(); display["cooker"] = "";
            var state = EvaluateIngredientState(display, System.Array.Empty<JsonObject>(), demand, context);
            var stockContext = Obj(Clone(context)); stockContext["hasCookerSnapshot"] = false;
            var stored = FoodCandidate(display, state, demand, stockContext, byName);
            stored["productKind"] = "dark-cuisine"; stored["supplySource"] = "stored";
            stored["productionRecipe"] = null;
            Arr(stored["conditionResults"]).Add(Condition("food.mission-product", "food", "info", "info", "任务料理", "优先使用保温箱内已制作的黑暗料理，取餐时再次确认具体成品。"));
            // 同一份库存可被玩家或另一订单抢先取走，主线程会再验证；本轮不同时推荐新生产，
            // 避免权重或加料标签意外把保温箱供给排到新开锅之后。
            return Array(new[] { stored });
        }
        foreach (var production in Objects(data["recipes"]).Where(r => Integer(r["id"]) && Num(r["id"]) >= 0
                     && Integer(r["recipeId"]) && Num(r["recipeId"]) >= 0))
        {
            if (!Has(context, "availableRecipeIds", Num(production["id"])) || !CookerAvailable(production, context)
                || ExtraSlots(production, context) < 1 || production["ingredientIds"] is not JsonArray ids
                || ids.Any(id => !Integer(id) || Num(id) < 0)) continue;
            var baseIds = Numbers(ids).ToArray();
            if (baseIds.Length >= 5 || baseIds.Any(id => !byId.ContainsKey(id)
                    || !Has(context, "availableIngredientIds", id) || IngredientExcluded(id, context))) continue;
            foreach (var extra in byId.Values.Where(i => !baseIds.Contains(Num(i["id"]))
                         && Has(context, "availableIngredientIds", Num(i["id"]))
                         && !IngredientExcluded(Num(i["id"]), context) && HasForbiddenIngredientTag(i, production)))
            {
                var demandCounts = baseIds.Append(Num(extra["id"])).GroupBy(id => id);
                if (demandCounts.Any(g => NullableNumber(quantities[Key(g.Key)]) is null
                        || Num(quantities[Key(g.Key)]) != -1 && Num(quantities[Key(g.Key)]) < g.Count())) continue;
                var display = Obj(Clone(production));
                display["id"] = -1; display["name"] = Str(product["name"]);
                display["price"] = Clone(product["price"]); display["level"] = Clone(product["level"]);
                display["positiveTags"] = Clone(product["positiveTags"]); display["negativeTags"] = new JsonArray();
                // 成品转换会清除原配方及加料标签，不能把禁忌材料的喜好标签算到黑暗料理上。
                var state = EvaluateIngredientState(display, System.Array.Empty<JsonObject>(), demand, context);
                state["ingredients"] = Array(new[] { extra }); state["extraCost"] = Num(extra["price"]);
                var candidate = FoodCandidate(display, state, demand, context, byName);
                candidate["baseCost"] = baseIds.Sum(id => Num(byId[id]["price"]));
                candidate["productKind"] = "dark-cuisine";
                candidate["supplySource"] = "cook";
                candidate["productionRecipe"] = Clone(production);
                candidate["extraIngredientReasonTags"] = Object((Key(Num(extra["id"])), Array(new[] { "制作黑暗料理" })));
                Arr(candidate["conditionResults"]).Add(Condition("food.mission-product", "food", "info", "info", "任务料理", "使用基底配方与禁忌加料制作黑暗料理；开锅前复核游戏效果。"));
                candidates.Add(candidate);
            }
        }
        // 任务特殊产物只需保留少量低成本可执行方式，不进入普通组合搜索，避免放大每单计算量。
        return Array(candidates.OrderBy(c => Str(c["supplySource"]) == "stored" ? 0 : 1).ThenBy(c => Num(c["baseCost"]) + Num(c["extraCost"]))
            .ThenBy(c => Num(Obj(c["productionRecipe"])["recipeId"]))
            .ThenBy(c => Num(Objects(c["extraIngredients"]).FirstOrDefault()?["id"], -1)).Take(8));
    }
}