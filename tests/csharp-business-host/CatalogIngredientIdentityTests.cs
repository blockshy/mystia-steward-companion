using System.Text.Json.Nodes;
using MystiaStewardCompanion.Business.Domain.Support;
using J = MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

/// <summary>
/// 覆盖原始运行时目录到业务输入的真实规范化入口，避免只在协调器夹具中手动加字段，
/// 却遗漏生产链路中的白名单投影。名称仍用于展示，材料身份和份数不能丢失或被补造。
/// </summary>
internal static class CatalogIngredientIdentityTests
{
    internal static void Run(JsonObject catalog, Action<bool, string> check)
    {
        var raw = J.Obj(J.Clone(catalog));
        var recipe = J.Objects(raw["recipes"]).Single();
        recipe["ingredientIds"] = new JsonArray(1, 1);
        var normalized = J.Objects(RuntimeDataNormalizer.Build(raw)["recipes"]).Single();
        check(J.Numbers(normalized["ingredientIds"]).SequenceEqual(new double[] { 1, 1 }),
            "真实目录规范化必须保留精确材料ID的重复份数。");
        J.Arr(recipe["ingredientIds"])[0] = 99;
        check(J.Numbers(normalized["ingredientIds"]).SequenceEqual(new double[] { 1, 1 }),
            "规范化的材料身份不能共享源目录可变数组。");
        recipe.Remove("ingredientIds");
        check(!J.Objects(RuntimeDataNormalizer.Build(raw)["recipes"]).Single().ContainsKey("ingredientIds"),
            "目录未提供材料ID时不能根据中文名称补造身份。");
        recipe["ingredientIds"] = null;
        check(J.Objects(RuntimeDataNormalizer.Build(raw)["recipes"]).Single()["ingredientIds"] is null,
            "未知材料ID不能转为已确认空配方。");
        recipe["ingredientIds"] = new JsonArray();
        check(J.Objects(RuntimeDataNormalizer.Build(raw)["recipes"]).Single()["ingredientIds"] is JsonArray empty && empty.Count == 0,
            "真实空配方仍保留明确空数组。");
        recipe["ingredientIds"] = new JsonArray("1", -2);
        check(J.Objects(RuntimeDataNormalizer.Build(raw)["recipes"]).Single()["ingredientIds"]?.ToJsonString() == "[\"1\",-2]",
            "非法身份留给动作准入拒绝，不通过显示规范化将字符串或负数变成可执行ID。");
    }
}
