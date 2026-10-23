using System.Text.Json.Nodes;
using MystiaStewardCompanion.Business.Domain.Orders;
using MystiaStewardCompanion.Business.Domain.Support;
using static MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

/// <summary>
/// 对真实返回树执行破坏性修改，验证内部引用重排没有泄露给公共接口或缓存。
/// 只操作人工构造的托管值，不访问游戏；输入不变检查由宿主再次统一执行。
/// </summary>
internal static class PlanMaterializationOwnershipProbe
{
    internal static JsonObject Evaluate(JsonObject input)
    {
        var checks = 0;
        void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); checks++; }

        // 合法的任务或收藏首项故意放在第119项，超过两个展示上限。公共规则必须先提升，
        // 不能为降低分配而在查找之前Take；不相关项目的原顺序也必须完整保留。
        var plans = new JsonArray(Enumerable.Range(0, 120).Select(index => (JsonNode?)Object(
            ("ordinal", index), ("bucket", "complete"), ("conditionResults", new JsonArray()),
            ("food", Object(("recipe", Object(("id", index), ("recipeId", index + 1000))),
                ("extraIngredients", new JsonArray()), ("conditionResults", new JsonArray()))),
            ("beverage", Object(("beverage", Object(("id", index + 2000))),
                ("meetsRequiredBeverage", true), ("conditionResults", new JsonArray()))))).ToArray());
        var original = plans.ToJsonString();
        var mission = Object(("missionRecipeFoodId", 119), ("missionRecipeId", 1119));
        var lifted = PrimaryExecutionPlans.NormalizePrimaryExecutionPlans(plans, mission, new JsonObject());
        Check(Num(lifted[0]?["ordinal"]) == 119 && lifted.Count == 120, "后位任务必须在截取之前提升。");
        Check(Objects(lifted).Skip(1).Select(value => Num(value["ordinal"])).SequenceEqual(Enumerable.Range(0, 119).Select(value => (double)value)), "提升首项不能改变其余稳定顺序。");
        var favorites = Object(("favoriteRecipeKeys", new JsonArray("119:")), ("favoriteBeverageIds", new JsonArray(2119)));
        var policy = Object(("requireRecipeFavorite", true), ("requireBeverageFavorite", true));
        var favoriteLifted = PrimaryExecutionPlans.NormalizePrimaryExecutionPlans(plans, favorites, policy);
        Check(Num(favoriteLifted[0]?["ordinal"]) == 119, "后位双收藏主方案必须保留。");
        Obj(lifted[0]?["food"]?["recipe"])["name"] = "外部修改公共返回值";
        Check(plans.ToJsonString() == original, "公共标准化返回值不能与传入计划共享节点。");
        Check(favoriteLifted[0]?["food"]?["recipe"]?["name"] == null, "两次公共标准化输出不能共享嵌套节点。");

        var cache = new OrderCandidateCache();
        var inputBefore = input.ToJsonString();
        JsonObject Read() => OrderRecommendationService.Evaluate(input, cache);
        var cold = Read(); var expected = cold.ToJsonString();
        var recommendation = Objects(cold["recommendations"]).FirstOrDefault()
            ?? throw new InvalidOperationException("所有权夹具必须包含真实稀客投影。");
        var plan = Objects(recommendation["executionPlans"]).FirstOrDefault()
            ?? throw new InvalidOperationException("所有权夹具必须包含真实可执行方案。");
        var rowsBefore = Arr(recommendation["recipes"]).ToJsonString();
        var budgetBefore = recommendation["budget"]?.ToJsonString();
        Obj(plan["food"]?["recipe"])["name"] = "外部修改计划料理";
        if (plan["budget"] is not JsonObject planBudget || recommendation["budget"] is not JsonObject)
            throw new InvalidOperationException("所有权夹具必须包含真实预算，不能跳过嵌套预算检查。");
        planBudget["remainingBudget"] = 999999;
        Check(Arr(recommendation["recipes"]).ToJsonString() == rowsBefore, "计划与料理展示行必须拥有独立嵌套节点。");
        Check(recommendation["budget"]?.ToJsonString() == budgetBefore, "首方案预算与投影摘要预算必须独立。");
        Obj(recommendation["customer"])["name"] = "外部修改投影客人";
        if (Arr(recommendation["recipes"]).FirstOrDefault() is not JsonObject firstRow)
            throw new InvalidOperationException("所有权夹具必须包含料理展示行，不能跳过独占行检查。");
        Obj(firstRow["recipe"])["name"] = "外部修改料理行";
        Check(input.ToJsonString() == inputBefore, "修改新投影不得污染原始目录或订单。");
        var hit = Read();
        Check(hit.ToJsonString() == expected, "修改首次返回值不得污染缓存命中。");
        var hitRow = Objects(hit["recommendations"]).First();
        Obj(hitRow["customer"])["name"] = "外部修改命中结果";
        Check(Read().ToJsonString() == expected && input.ToJsonString() == inputBefore, "命中结果也必须与缓存及输入隔离。");
        return Object(("verified", true), ("assertions", checks), ("planCount", Arr(recommendation["executionPlans"]).Count));
    }
}
