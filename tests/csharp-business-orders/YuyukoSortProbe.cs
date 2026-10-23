using System.Reflection;
using System.Text.Json.Nodes;
using MystiaStewardCompanion.Business.Domain.SpecialBusiness;
using static MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

/// <summary>
/// 直接比较生产新排序与保留的旧比较器，逐项检查同一个 JsonObject 引用的位置。
/// 该探针只操作合成托管方案；既不复制一套评价算法，也不把分配下降解释为游戏帧率。
/// </summary>
internal static class YuyukoSortProbe
{
    internal static JsonObject Evaluate()
    {
        var checks = 0;
        void Check(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException(message); }
        // 反射只位于测试宿主，以调用真正的内部引用排序，避免为测试扩展生产公共可变节点接口。
        var type = typeof(YuyukoEvaluation);
        var sort = (Func<IEnumerable<JsonObject>, string, bool, IEnumerable<JsonObject>>)type
            .GetMethod("SortPlanReferences", BindingFlags.NonPublic | BindingFlags.Static)!
            .CreateDelegate(typeof(Func<IEnumerable<JsonObject>, string, bool, IEnumerable<JsonObject>>));
        var blockReason = (Func<JsonObject, string, bool, string>)type
            .GetMethod("BlockReason", BindingFlags.NonPublic | BindingFlags.Static)!
            .CreateDelegate(typeof(Func<JsonObject, string, bool, string>));
        var values = Enumerable.Range(0, 192).Select(index =>
        {
            var food = Object(("recipe", Object(("id", index % 9), ("name", "合成料理"), ("price", index % 4 * 10), ("level", index % 6))),
                ("meetsRequiredFood", index % 7 != 0), ("matchedPositiveTags", new JsonArray("素", " 鲜 ", "鲜", index % 3 == 0 ? "甜" : "")),
                ("matchedNegativeTags", index % 4 == 0 ? new JsonArray("肉", "肉", "素") : new JsonArray("素")),
                ("resourcePressure", index % 8 * 3d), ("extraIngredients", new JsonArray(Object(("id", 2)))));
            var beverage = Object(("beverage", Object(("id", index % 7), ("name", "合成酒水"), ("price", index % 5 * 5), ("level", index % 5))),
                ("meetsRequiredBeverage", index % 5 != 0), ("matchedTags", new JsonArray("水果", "低酒精", " 低酒精 ")),
                ("ownedQuantity", index % 5 == 0 ? -1 : index % 3 == 0 ? -4 : 20));
            return Object(("ordinal", index), ("food", index % 17 == 0 ? null : food),
                ("beverage", index % 19 == 0 ? null : beverage), ("demand", Object(("requiredFoodTag", "素"), ("requiredBeverageTag", "水果"))),
                ("bucket", index % 11 == 0 ? "blocked" : "complete"), ("estimatedPrice", index % 6 * 10),
                ("conditionResults", new JsonArray()));
        }).ToList();
        // 同分同价且内容相同的不同节点、同一节点重复出现，都必须保留输入稳定顺序。
        var tie = Obj(Clone(values[1])); tie["ordinal"] = 192; values.Add(tie); values.Add(values[1]);
        // 合法有限输入可以在原乘法中溢出为无穷评分；新排序仍按 Double.CompareTo 比较。
        var positiveInfinity = Obj(Clone(values[1])); Obj(positiveInfinity["food"])["resourcePressure"] = -double.MaxValue; values.Add(positiveInfinity);
        var negativeInfinity = Obj(Clone(values[1])); Obj(negativeInfinity["food"])["resourcePressure"] = double.MaxValue; values.Add(negativeInfinity);
        var before = values.Select(value => value.ToJsonString()).ToArray();
        long oldAllocated = 0, newAllocated = 0;
        foreach (var (mode, positive) in new[] { ("story-level-sum", false), ("retake-tag-order", false), ("none", true) })
        {
            int Compare(JsonObject left, JsonObject right) => positive
                ? YuyukoEvaluation.ComparePositivePlans(left, right) : YuyukoEvaluation.ComparePlans(left, right, mode);
            JsonObject[] Old() => values.OrderBy(value => value, Comparer<JsonObject>.Create(Compare)).ToArray();
            JsonObject[] New() => sort(values, mode, positive).ToArray();
            // 先经过两条实际路径，防止JIT首次成本混入本次托管分配对照。
            var expected = Old(); var actual = New();
            Check(actual.Length == values.Count && actual.Zip(expected).All(pair => ReferenceEquals(pair.First, pair.Second)),
                mode + ": 新旧完整顺序或节点所有权不一致。");
            Check(actual.Where(value => ReferenceEquals(value, values[1])).Count() == 2, mode + ": 去掉了合法重复引用。");
            var tieGroup = actual.Where(value => ReferenceEquals(value, values[1]) || ReferenceEquals(value, tie)).ToArray();
            Check(tieGroup.Length == 3 && ReferenceEquals(tieGroup[0], values[1]) && ReferenceEquals(tieGroup[1], tie)
                && ReferenceEquals(tieGroup[2], values[1]), mode + ": 双键相等的稳定次序被改变。");
            var scores = values.Select(value => positive ? YuyukoEvaluation.PositivePlanScore(value) : YuyukoEvaluation.PlanScore(value, mode)).ToArray();
            Check(scores.Any(score => score > 0) && scores.Any(score => score < 0), mode + ": 没有覆盖正负评分。");
            Check(scores.Any(double.IsPositiveInfinity) && scores.Any(double.IsNegativeInfinity), mode + ": 没有覆盖无穷评分边界。");
            for (var index = 1; index < actual.Length; index++)
                if (Compare(actual[index - 1], actual[index]) > 0) throw new InvalidOperationException(mode + ": 比较器顺序逆转。");
            Check(before.SequenceEqual(values.Select(value => value.ToJsonString())), mode + ": 排序修改输入字段。");

            var invalid = values.Where(value => positive ? !YuyukoEvaluation.IsPositiveSpellPlan(value) : !YuyukoEvaluation.IsProgressPlan(value, mode)).ToArray();
            foreach (var limit in new[] { 0, 1, 3, 500 })
            {
                // 只拼接旧控制流程；解释文本仍调用同一生产 BlockReason，不复制业务判断。
                var oldMessages = invalid.OrderBy(value => value, Comparer<JsonObject>.Create(Compare))
                    .Select(value => blockReason(value, mode, positive)).Where(value => value.Length > 0)
                    .Distinct(StringComparer.Ordinal).Take(Math.Max(1, limit)).ToArray();
                Check(YuyukoEvaluation.BlockedMessages(values, mode, positive, limit).SequenceEqual(oldMessages),
                    mode + ": 阻断解释顺序/去重/上限变化。");
            }
            var allocated = GC.GetAllocatedBytesForCurrentThread(); Old(); var oldBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            allocated = GC.GetAllocatedBytesForCurrentThread(); New(); var newBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            Check(newBytes < oldBytes, mode + ": 键排序没有减少重复评价分配。");
            oldAllocated += oldBytes; newAllocated += newBytes;
        }
        return Object(("verified", true), ("assertions", checks), ("plans", values.Count),
            ("oldSortAllocatedBytes", oldAllocated), ("keySortAllocatedBytes", newAllocated));
    }
}
