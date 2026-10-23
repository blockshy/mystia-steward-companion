using System.Globalization;
using System.Text.Json.Nodes;
using J = MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

namespace MystiaStewardCompanion.Business.Application.Automation;

public sealed partial class AutomationCoordinator
{
    /// <summary>
    /// 对普客实际开锅目标执行纯数据材料准入。基础材料 ID 来自已验证游戏配方目录，
    /// 特殊经营使用已锁定目标的 foodId/recipeId/加料，绝不按料理或材料名称反推身份。
    /// 返回空串仅表示当前快照足够；主线程仍须在真正扣料前重新验证库存和全部原生身份。
    /// </summary>
    private string NormalIngredientDeferral(JsonObject order, JsonObject? target)
    {
        if (!TryIngredientInteger(target is null ? order["foodId"] : target["foodId"], 0, out var foodId))
            return "料理目标身份不完整，等待目录与订单状态更新。";
        int? targetRecipeId = null;
        if (target is not null)
        {
            if (!TryIngredientInteger(target["recipeId"], 0, out var exactRecipeId))
                return "锁定料理的配方身份不完整，等待有效目标后再开锅。";
            targetRecipeId = exactRecipeId;
        }
        // 已锁定目标有双 ID，必须按双 ID 联结；同一种成品的另一条配方不构成歧义。
        // 普通点单只有 foodId，仍要求唯一目录配方，不能擅自挑第一条配方来扣料。
        var recipes = J.Objects(_input.Data["recipes"])
            .Where(recipe => TryIngredientInteger(recipe["id"], 0, out var id) && id == foodId
                && (targetRecipeId is null || TryIngredientInteger(recipe["recipeId"], 0, out var candidateRecipeId)
                    && candidateRecipeId == targetRecipeId.Value)).Take(2).ToArray();
        if (recipes.Length != 1 || !TryIngredientInteger(recipes[0]["recipeId"], 0, out _))
            return "料理配方缺失或身份不唯一，等待完整目录后再开锅。";
        if (recipes[0]["ingredientIds"] is not JsonArray ingredients)
            return "料理基础材料信息尚未完整，等待目录更新后再开锅。";
        if (target is not null && target["extraIngredientIds"] is not JsonArray)
            return "锁定料理的加料信息尚未完整，等待有效目标后再开锅。";

        // 重复基础材料和重复加料都是实际扣料次数，不可 Distinct。计数与原生
        // HasEnoughIngredients 一致；目录展示名只用于错误说明，不参与材料身份联结。
        var required = new Dictionary<int, int>();
        var extraIngredients = target?["extraIngredientIds"] as JsonArray;
        if (ingredients.Count + (extraIngredients?.Count ?? 0) > 5)
            return "料理基础材料与加料总数超过游戏上限，等待有效目标后再开锅。";
        foreach (var node in ingredients.Concat((IEnumerable<JsonNode?>?)extraIngredients ?? Enumerable.Empty<JsonNode?>()))
        {
            if (!TryIngredientInteger(node, 0, out var id))
                return "料理材料身份无效，等待完整目录与有效目标后再开锅。";
            required.TryGetValue(id, out var count);
            required[id] = count + 1;
        }
        // 真实空配方无需库存；缺失数组与真实空数组严格分开，不能把未知当作免费料理。
        if (required.Count == 0) return "";
        var runtime = _input.Snapshot["recommendationState"] as JsonObject;
        foreach (var (id, count) in required)
        {
            if (!TryNormalIngredientQuantity(runtime, id, out var quantity))
                return $"材料库存尚未确认：{IngredientDisplayName(id)}，等待库存更新后再开锅。";
            if (quantity != -1 && quantity < count)
                return $"材料不足：{IngredientDisplayName(id)}需要 {count} 份，库存 {quantity} 份；等待补充材料，其他可行订单继续调度。";
        }
        return "";
    }

    /// <summary>
    /// 原生 RuntimeStorageStateProjection 有意省略数量为零的项，FromSave 只把字典中
    /// 剩下的正数/无限项加入 availableIngredientIds。因此，只有运行时已加载、数量字典
    /// 存在、可用集合为合法完整 ID 数组且确实不含该 ID，缺少字典项才可解释为零。
    /// 集合缺失、类型错误、声称可用但数量缺失或显式 null 均为未知，不能制造零库存。
    /// </summary>
    private bool TryNormalIngredientQuantity(JsonObject? runtime, int id, out int quantity)
    {
        quantity = 0;
        if (runtime?["ownedIngredientQty"] is not JsonObject quantities) return false;
        if (quantities.TryGetPropertyValue(id.ToString(CultureInfo.InvariantCulture), out var node))
            return TryIngredientInteger(node, -1, out quantity);
        if (!J.Bool(_input.Snapshot["runtimeLoaded"]) || runtime["availableIngredientIds"] is not JsonArray available)
            return false;
        foreach (var candidate in available)
            if (!TryIngredientInteger(candidate, 0, out var availableId) || availableId == id) return false;
        return true;
    }

    /// <summary>仅精确且唯一的 ID 可补充展示名；名称缺失或目录重复时保留可诊断的原始 ID。</summary>
    private string IngredientDisplayName(int id)
    {
        var matches = J.Objects(_input.Data["ingredients"])
            .Where(item => TryIngredientInteger(item["id"], 0, out var candidate) && candidate == id).Take(2).ToArray();
        var name = matches.Length == 1 ? J.Str(matches[0]["name"]) : "";
        return name.Length > 0 ? $"{name}（#{id}）" : $"材料 #{id}";
    }

    /// <summary>
    /// 接受协议中的整数数字，包括领域组装的整数 double；不接受字符串数字、布尔、非有限值、
    /// 小数或非法负数。库存仅 -1 表示无限，不能让其它负数绕过缺料判断。
    /// </summary>
    private static bool TryIngredientInteger(JsonNode? node, int minimum, out int result)
    {
        result = 0;
        if (node is not JsonValue value) return false;
        if (value.TryGetValue<int>(out var integer))
        {
            result = integer;
            return integer >= minimum;
        }
        if (value.TryGetValue<long>(out var wide) && wide >= minimum && wide <= int.MaxValue)
        {
            result = (int)wide;
            return true;
        }
        if (value.TryGetValue<double>(out var number) && double.IsFinite(number)
            && number >= minimum && number <= int.MaxValue && number == Math.Truncate(number))
        {
            result = (int)number;
            return true;
        }
        return false;
    }
}
