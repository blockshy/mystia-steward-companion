using System.Text.Json.Nodes;
using MystiaStewardCompanion.Business.Domain.Support;

/// <summary>
/// 双模式新增语义使用明确 C# 契约断言；固定 TypeScript oracle 仅验证历史客人模式，
/// 不修改旧实现来“证明”新功能。所有输入为合成值树，不访问游戏、文件或网络。
/// </summary>
internal static class CustomRecipeModeTests
{
    internal static void Run()
    {
        var checks = 0;
        void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
        JsonObject Entry(string id, string mode, int customer, string? tag = null) => new()
        {
            ["id"] = id, ["mode"] = mode, ["customerId"] = customer, ["customerName"] = $"客人{customer}",
            ["foodId"] = 10, ["recipeId"] = 110, ["recipeName"] = "合成料理", ["foodTag"] = tag,
            ["extraIngredientIds"] = new JsonArray(2), ["enabled"] = true, ["pinToTop"] = true,
            ["sortOrder"] = 100, ["createdAtUtc"] = "", ["updatedAtUtc"] = "",
        };
        var data = new JsonObject
        {
            ["version"] = 2, ["activeMode"] = "customer", ["modeRevision"] = 0, ["enabled"] = true,
            ["recipes"] = new JsonArray(Entry("customer-1", "customer", 1), Entry("food-1", "food", 1, "鲜"),
                Entry("food-2", "food", 2), Entry("customer-2", "customer", 2)),
        };
        string[] Ids(int customer, string tag) => CustomRecipes.GetEffectiveCustomRecipeEntries(data, customer, tag)
            .Select(entry => entry!["id"]!.GetValue<string>()).ToArray();
        Check(Ids(1, "鲜").SequenceEqual(new[] { "customer-1" }), "客人模式混入料理模式。");
        data["activeMode"] = "food";
        data["modeRevision"] = 1L;
        Check(Ids(1, "鲜").SequenceEqual(new[] { "food-1" }), "料理模式未使用本模式绑定。");
        Check(Ids(2, "甜").SequenceEqual(new[] { "food-2" }), "同料理绑定多个稀客未独立匹配。");
        Check(Ids(3, "鲜").Length == 0, "料理模式错误地向未绑定客人生效。");
        Check(Ids(1, "甜").Length == 0, "料理模式忽略了显式点单标签。");
        data["enabled"] = false;
        Check(Ids(1, "鲜").Length == 0, "全局停用仍产生自定义候选。");
        data["enabled"] = true;
        data["recipes"]![1]!["enabled"] = false;
        Check(Ids(1, "鲜").Length == 0, "条目停用仍产生绑定。");
        data["recipes"]![1]!["enabled"] = true;
        var before = data.ToJsonString();
        _ = CustomRecipes.NormalizeCustomRecipeData(data);
        Check(before == data.ToJsonString(), "规范化修改了输入配置。");
        data["activeMode"] = "unknown";
        Check(Ids(1, "鲜").Length == 0, "未知模式被猜为可执行模式。");
        data["activeMode"] = "food";
        data.Remove("modeRevision");
        Check(Ids(1, "鲜").Length == 0, "缺修订的新配置参与执行。");
        data["modeRevision"] = 2L;
        data["recipes"]![1]!.AsObject().Remove("mode");
        Check(Ids(1, "鲜").Length == 0, "缺模式条目被默认到当前模式。");
        data["recipes"]![1]!["mode"] = "food";
        data["version"] = 3;
        Check(Ids(1, "鲜").Length == 0, "未知 schema 被当作当前配置执行。");
        data["version"] = 2;
        var mixedOrder = JsonNode.Parse(data.ToJsonString())!.AsObject();
        var firstFood = Entry("a", "food", 9); firstFood["foodId"] = 1;
        var secondFood = Entry("b", "food", 1); secondFood["foodId"] = 2;
        var inactiveCustomer = Entry("c", "customer", 5);
        mixedOrder["recipes"] = new JsonArray(firstFood, secondFood, inactiveCustomer);
        var sorted = CustomRecipes.NormalizeCustomRecipeData(mixedOrder)["recipes"]!.AsArray();
        Check(sorted.Select(entry => entry!["id"]!.GetValue<string>()).SequenceEqual(new[] { "c", "a", "b" }),
            "混合模式同序号排序不满足固定模式及组内顺序。");
        mixedOrder["recipes"] = new JsonArray(JsonNode.Parse(inactiveCustomer.ToJsonString()),
            JsonNode.Parse(secondFood.ToJsonString()), JsonNode.Parse(firstFood.ToJsonString()));
        Check(CustomRecipes.NormalizeCustomRecipeData(mixedOrder)["recipes"]!.AsArray()
            .Select(entry => entry!["id"]!.GetValue<string>()).SequenceEqual(new[] { "c", "a", "b" }),
            "混合模式排序依赖输入排列，非活动条目干扰当前模式。");

        var legacy = JsonNode.Parse(data.ToJsonString())!.AsObject();
        legacy["version"] = 1;
        legacy.Remove("activeMode"); legacy.Remove("modeRevision");
        legacy["recipes"] = new JsonArray(Entry("legacy", "customer", 1));
        legacy["recipes"]![0]!.AsObject().Remove("mode");
        var migrated = CustomRecipes.NormalizeCustomRecipeData(legacy);
        Check(migrated["version"]!.GetValue<int>() == 2 && migrated["activeMode"]!.GetValue<string>() == "customer",
            "历史纯配置未明确归入客人模式。");
        Check(CustomRecipes.GetEffectiveCustomRecipeEntries(legacy, 1, "鲜").Count == 1
            && CustomRecipes.GetEffectiveCustomRecipeEntries(migrated, 1, "鲜").Count == 1,
            "历史迁移改变客人模式匹配结果。");

        var options = JsonNode.Parse(@"{
          ""data"": {
            ""recipes"": [{""id"":10,""recipeId"":110,""name"":""合成料理"",""ingredients"":[""豆腐""],""positiveTags"":[""家常""],""negativeTags"":[],""cooker"":""煮锅"",""price"":30}],
            ""ingredients"": [{""id"":1,""name"":""豆腐"",""tags"":[""家常""],""price"":2},{""id"":2,""name"":""蘑菇"",""tags"":[""鲜""],""price"":3}]
          },
          ""customer"": {""id"":1,""positiveTags"":[""鲜""],""negativeTags"":[]},
          ""requiredFoodTag"":""鲜"", ""requiredBeverageTag"":""水果"",
          ""context"": {""availableRecipeIds"":[10],""availableIngredientIds"":[1,2],""ownedIngredientQty"":{""1"":5,""2"":5},""hasCookerSnapshot"":true,""placedCookerNames"":[""煮锅""],""filterMissingCookers"":true}
        }")!.AsObject();
        options["customRecipes"] = JsonNode.Parse(data.ToJsonString());
        JsonArray Candidates() => CustomRecipes.BuildCustomFoodCandidates(options);
        Check(Candidates().Single()!["customRecipeId"]!.GetValue<string>() == "food-1", "实际候选未按当前模式构建。");
        options["customer"]!["id"] = 3;
        Check(Candidates().Count == 0, "实际料理候选泄漏到未绑定稀客。");
        options["customer"]!["id"] = 1;
        options["customRecipes"]!["activeMode"] = "customer";
        Check(Candidates().Single()!["customRecipeId"]!.GetValue<string>() == "customer-1", "切回客人模式未恢复独立候选。");
        options["context"]!["excludedIngredientIds"] = new JsonArray(2);
        Check(Candidates().Count == 0, "自定义模式绕过排除材料规则。");
        options["context"]!.AsObject().Remove("excludedIngredientIds");
        options["context"]!["placedCookerNames"] = new JsonArray();
        Check(Candidates().Count == 0, "自定义模式绕过厨具可用规则。");
        Console.WriteLine($"PASS: custom recipe dual-mode domain {checks} checks; no game or network access.");
    }
}
