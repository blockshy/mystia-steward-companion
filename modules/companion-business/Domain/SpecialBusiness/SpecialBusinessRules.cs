using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

namespace MystiaStewardCompanion.Business.Domain.SpecialBusiness;

/// <summary>
/// 特殊经营的纯领域规则。只解释快照中的已确认事实，不读取游戏对象、不决定执行许可。
/// 规则字段与原 TypeScript 协议保持一致，供推荐、普通订单目标和游戏辅助目标共同使用。
/// </summary>
public static class SpecialBusinessRules
{
    public const string WackyChallenge = "Story_WackyCookingCompetition";
    public const string YuumaChallenge = "Story_BloodPondHell";
    public const string KoishiRole = "wacky-koishi-boss";
    public const string YuyukoRole = "yuyuko-boss-order";
    public const string YuumaRole = "yuuma-boss-order";
    public const double WackyMinimumProgress = .35;
    private static readonly HashSet<string> PassiveChallenges = new(StringComparer.Ordinal)
    {
        "Story_Basic", "Story_Advanced", "AnyChallenge", "Story_Seiga_TempleCuisineCompetition",
        "Story_Futo_TempleCuisineCompetition", "Story_Tochiko_TempleCuisineCompetition", "Story_Ichirin_MusicCompetition",
        "Story_Minamitu_MusicCompetition", "Story_Toramaru_MusicCompetition", "Story_Flandre", "RogueLike"
    };

    /// <summary>去空白、规范流行标签别名并按首次出现顺序去重；不重排游戏标签。</summary>
    public static string[] NormalizeTags(JsonNode? tags) => NormalizeTags(Strings(tags));
    public static string[] NormalizeTags(IEnumerable<string> tags) => tags.Select(x => x.Trim()).Where(x => x.Length > 0)
        .Select(x => x == "流行·喜爱" ? "流行喜爱" : x == "流行·厌恶" ? "流行厌恶" : x).Distinct(StringComparer.Ordinal).ToArray();
    public static bool PhaseTwo(string phase) => Regex.IsMatch(phase, @"phase\s*2|phase2|阶段\s*2|阶段二|second", RegexOptions.IgnoreCase);
    public static bool PhaseThree(string phase) => Regex.IsMatch(phase, @"phase\s*3|phase3|阶段\s*3|阶段三|third", RegexOptions.IgnoreCase);
    public static bool IsYuyuko(string challenge) => challenge is "Story_Yuyuko" or "Challenge_Yuyuko";
    public static bool IsMizuchi(string challenge) => challenge is "Story_Mizuchi" or "Story_Mizuchi_1" or "Story_Mizuchi_2" or "Story_Mizuchi_3";
    public static bool IsRegistered(string challenge) => PassiveChallenges.Contains(challenge) || challenge is WackyChallenge or YuumaChallenge || IsYuyuko(challenge) || IsMizuchi(challenge);
    public static bool IsPassive(string challenge) => PassiveChallenges.Contains(challenge);

    /// <summary>生成完整默认规则，避免跨场景遗留上一次的约束。</summary>
    public static JsonObject EmptyRule() => Object(
        ("foodTarget", Object(("enforcement", "none"), ("match", "any"), ("tags", new JsonArray()))),
        ("requiredExtraIngredientIds", new JsonArray()), ("forbiddenExtraIngredientIds", new JsonArray()),
        ("blockingReason", ""), ("requiresBaseOrderMatch", false), ("requiresHighEvaluation", false),
        ("highEvaluationMinPreferenceMatches", 0), ("preferHighFoodLevel", false), ("preferHighBeverageLevel", false),
        ("preferKoishiDamage", false), ("preferYuyukoPositiveSpell", false), ("yuyukoProgressEvaluationMode", "none"), ("reason", ""));

    /// <summary>根据快照中的挑战、阶段与订单角色构造约束；挑战类型不可读时返回明确阻断原因。</summary>
    /// <remarks>
    /// 可读性检查通过后，未激活经营或未命中特定规则的挑战保留默认规则。调用方仍须使用 <see cref="IsRegistered"/>
    /// 判断挑战是否受支持，不能把默认规则解释为未知挑战已经通过执行审核。
    /// </remarks>
    public static JsonObject BuildOrderRule(JsonNode? specialBusiness, string? role)
    {
        var s = Obj(specialBusiness); var r = EmptyRule(); role = (role ?? "").Trim();
        if (specialBusiness != null && !Bool(s["challengeTypeAvailable"]))
        { r["blockingReason"] = TextOr(Str(s["error"]).Trim(), "特殊经营类型暂时无法读取，推荐已暂停。"); return r; }
        if (!Bool(s["active"])) return r;
        var challenge = Str(s["challengeType"]); var label = TextOr(Str(s["displayName"]).Trim(), challenge);
        var phase2 = PhaseTwo(Str(s["phase"])); var phase3 = PhaseThree(Str(s["phase"]));
        if (challenge == WackyChallenge)
        {
            if (role == KoishiRole && phase3)
            {
                var broken = Bool(s["wackyKoishiShieldBroken"]);
                r["requiresBaseOrderMatch"] = broken; r["requiresHighEvaluation"] = !broken;
                r["highEvaluationMinPreferenceMatches"] = broken ? 0 : 3;
                r["preferHighFoodLevel"] = true; r["preferHighBeverageLevel"] = true; r["preferKoishiDamage"] = broken;
                r["reason"] = broken
                    ? "怪诞料理三阶段古明地恋本体已破防，需要先满足原订单料理和酒水要求，再按破防期预计伤害优先选择。"
                    : "怪诞料理三阶段古明地恋本体需要按场上揭示的正面/厌恶/酒水 Tag 选择高评价组合。";
                return r;
            }
            var tags = NormalizeTags(s["foodTargetTags"]); var qualified = phase2 || phase3;
            r["foodTarget"] = Object(("enforcement", tags.Length > 0 ? "require" : "none"), ("match", "any"), ("tags", Array(tags)));
            r["requiresBaseOrderMatch"] = qualified; r["requiresHighEvaluation"] = qualified;
            r["highEvaluationMinPreferenceMatches"] = qualified ? 2 : 0;
            r["preferHighFoodLevel"] = qualified; r["preferHighBeverageLevel"] = qualified;
            r["reason"] = tags.Length > 0 ? $"怪诞料理目标 Tag：{string.Join("、", tags)}{(qualified ? "，需要满足原订单并获得最高评价" : "")}" : qualified ? "怪诞料理需要满足原订单并获得最高评价。" : "";
        }
        else if (IsYuyuko(challenge))
        {
            var boss = role == YuyukoRole; var progress = phase3 && boss; var story = challenge == "Story_Yuyuko";
            r["requiresBaseOrderMatch"] = (phase2 || phase3) && boss; r["requiresHighEvaluation"] = (phase2 || phase3) && boss;
            r["highEvaluationMinPreferenceMatches"] = phase2 && boss ? 2 : 0;
            r["preferHighFoodLevel"] = progress && story; r["preferHighBeverageLevel"] = progress && story;
            r["preferYuyukoPositiveSpell"] = phase2 && boss;
            r["yuyukoProgressEvaluationMode"] = progress ? story ? "story-level-sum" : "retake-tag-order" : "none";
            r["reason"] = progress ? story
                ? "剧情版幽幽子第三阶段需要满足料理与酒水点单，并选择等级合计可达到满意（Good）/完美（ExGood）的组合。"
                : "重修版幽幽子第三阶段使用稀客原生 Tag 评价：料理与酒水先满足点单，再避开当前稀客厌恶，并至少额外命中一个当前稀客喜好以达到满意（Good）并推进。"
                : phase2 && boss ? "幽幽子第二阶段需要稀客触发正面符卡；自动化只选择满足当前稀客点单、避开该稀客厌恶 Tag，且排除点单 Tag 后仍预计可达完美（ExGood）的组合。" : "";
        }
        else if (challenge == YuumaChallenge)
        {
            if (role == "yuuma-order-unverified") { r["blockingReason"] = $"{label}订单角色身份尚未确认，推荐已暂停。"; return r; }
            if (role != YuumaRole) return r;
            var tags = NormalizeTags(s["foodTargetTags"]); var ready = tags.Length == 2;
            r["foodTarget"] = Object(("enforcement", "require"), ("match", "all"), ("tags", Array(tags)));
            r["requiresBaseOrderMatch"] = true;
            r["blockingReason"] = ready ? "" : $"{label}需要同时读取 2 个料理目标 Tag，当前读取到 {tags.Length} 个。";
            r["reason"] = ready ? $"{label}要求原订单成立，并同时满足目标 Tag：{string.Join("、", tags)}" : $"{label}目标 Tag 尚未完整读取。";
        }
        else if (IsMizuchi(challenge))
        {
            var story = challenge == "Story_Mizuchi"; var prefix = story ? "mizuchi-story-" : "mizuchi-trial-";
            var kind = role.StartsWith(prefix, StringComparison.Ordinal) ? role[prefix.Length..] : "";
            if (kind is not ("possessed-order" or "ordinary-order" or "unverified-order"))
            { r["blockingReason"] = $"{label}订单角色不属于当前已验证的瑞灵场景，推荐与自动化已暂停。"; return r; }
            if (kind == "unverified-order") { r["blockingReason"] = $"{label}订单附身身份尚未确认，推荐与自动化已暂停。"; return r; }
            var ingredientId = story ? 5002 : 5005; var ingredientName = story ? "噗噗呦果" : "辣椒水";
            r["requiresBaseOrderMatch"] = true;
            if (kind == "ordinary-order")
            { r["forbiddenExtraIngredientIds"] = new JsonArray(ingredientId); r["reason"] = $"{label}普通订单需要满足原始料理与酒水 Tag，且不得把{ingredientName}作为额外材料。"; return r; }
            var ids = Arr(s["requiredExtraIngredientIds"]);
            var ready = ids.Count == 1 && Integer(ids[0]) && Num(ids[0]) == ingredientId;
            r["requiredExtraIngredientIds"] = ready ? new JsonArray(ingredientId) : new JsonArray();
            r["blockingReason"] = ready ? "" : $"{label}附身订单需要精确读取额外材料 {ingredientId}，当前上下文不一致。";
            r["reason"] = ready ? $"{label}附身订单需要满足原始料理与酒水 Tag，并把{ingredientName}作为额外材料加入料理。" : $"{label}附身订单的额外材料要求尚未确认。";
        }
        return r;
    }

    /// <summary>按规范化后的标签判断交集或全包含；空目标始终不构成命中。</summary>
    /// <remarks>仅 <c>all</c> 使用全包含语义；其他值沿用任一命中。无约束应由上层显式声明，而非借助空集合通过。</remarks>
    public static bool MatchesTags(JsonNode? tags, JsonNode? targetTags, string match)
    { var a = NormalizeTags(tags).ToHashSet(StringComparer.Ordinal); var t = NormalizeTags(targetTags); return t.Length > 0 && (match == "all" ? t.All(a.Contains) : t.Any(a.Contains)); }
    public static bool MatchesFoodTarget(JsonNode? tags, JsonNode? target) => Str(Obj(target)["enforcement"]) == "none" || MatchesTags(tags, Obj(target)["tags"], Str(Obj(target)["match"]));
    public static bool IsSpecialRole(string? role) => (role ?? "").Trim() is KoishiRole or "wacky-ghost-order" or "wacky-target-order" or YuyukoRole or YuumaRole or "yuuma-order-unverified" or "mizuchi-story-possessed-order" or "mizuchi-story-ordinary-order" or "mizuchi-story-unverified-order" or "mizuchi-trial-possessed-order" or "mizuchi-trial-ordinary-order" or "mizuchi-trial-unverified-order";
    public static int OrderPriority(JsonNode? special, string role)
    {
        var s = Obj(special); if (!Bool(s["active"]) || !Bool(s["challengeTypeAvailable"]) || !IsMizuchi(Str(s["challengeType"]))) return 0;
        role = role.Trim(); return role is "mizuchi-story-possessed-order" or "mizuchi-trial-possessed-order" ? 0 : role is "mizuchi-story-ordinary-order" or "mizuchi-trial-ordinary-order" ? 1 : role is "mizuchi-story-unverified-order" or "mizuchi-trial-unverified-order" ? 2 : 3;
    }

    /// <summary>生成绑定经营轮次的协议策略，并携带尤魔目标修订；不满足必需字段条件时返回空策略。</summary>
    /// <remarks>
    /// 输出是新建的 JSON 对象，供调用方序列化。目标标签排序使签名不受输入顺序影响；
    /// 尤魔修订号通过独立字段传递，并不拼入签名，接收端需要一并核对。
    /// </remarks>
    public static JsonObject BuildWirePolicy(JsonNode? special, string role, double generation)
    {
        var s = Obj(special); var target = Obj(BuildOrderRule(special, role)["foodTarget"]);
        var challenge = Str(s["challengeType"]); var owner = challenge == WackyChallenge ? "koishi" : challenge == YuumaChallenge ? "yuuma" : "";
        var revision = owner == "yuuma" && SafeInteger(s["yuumaFoodTargetRevision"]) && Num(s["yuumaFoodTargetRevision"]) > 0 ? Num(s["yuumaFoodTargetRevision"]) : 0;
        var tags = NormalizeTags(target["tags"]).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        if (!Bool(s["active"]) || owner.Length == 0 || generation <= 0 || generation > 9007199254740991d || generation != Math.Truncate(generation) || (owner == "yuuma" && revision <= 0) || Str(target["enforcement"]) != "require" || tags.Length == 0) return EmptyWirePolicy();
        var match = Str(target["match"]);
        return Object(("specialTargetChallenge", challenge), ("specialTargetOwner", owner), ("specialTargetGeneration", generation), ("specialTargetRevision", revision), ("specialTargetFoodTags", Array(tags)), ("specialTargetMatchMode", match), ("specialTargetSignature", $"{challenge}|{owner}|generation:{Key(generation)}|match:{match}|food:{string.Join(",", tags)}"));
    }
    public static JsonObject EmptyWirePolicy() => Object(("specialTargetChallenge", ""), ("specialTargetOwner", ""), ("specialTargetGeneration", 0), ("specialTargetRevision", 0), ("specialTargetFoodTags", new JsonArray()), ("specialTargetMatchMode", ""), ("specialTargetSignature", ""));
    /// <summary>判断当前特殊经营是否必须通过显式普通订单目标入口处理。</summary>
    /// <remarks>
    /// 激活状态下，挑战类型不可读或未注册均返回 <see langword="true"/>，防止退回通用选择路径。
    /// 该结果表示路径要求，具体目标是否可用仍须由目标构建与执行准入确认。
    /// </remarks>
    public static bool RequiresNormalTarget(JsonNode? special, string role)
    {
        var s = Obj(special); if (!Bool(s["active"])) return false; if (!Bool(s["challengeTypeAvailable"])) return true;
        var c = Str(s["challengeType"]); if (!IsRegistered(c)) return true;
        if (c == WackyChallenge) return role.Trim() is KoishiRole or "wacky-ghost-order" or "wacky-target-order";
        if (IsYuyuko(c)) return PhaseThree(Str(s["phase"])) && role.Trim() == YuyukoRole;
        if (c == YuumaChallenge) return role.Trim() is YuumaRole or "yuuma-order-unverified";
        return false;
    }
    /// <summary>已知目标标签剩余时间比例低于阈值时生成暂缓原因；未知比例不在此处额外阻断。</summary>
    /// <remarks>本方法只解释时间比例，不验证挑战类型；调用方负责限定适用的特殊经营阶段。</remarks>
    public static string CountdownDeferral(JsonNode? special)
    {
        var progress = NullableNumber(Obj(special)["targetTagTimeProgress"]);
        return progress == null || progress >= WackyMinimumProgress ? "" : $"怪诞料理 Tag 倒计时剩余约 {Key(Math.Max(0, Round(progress.Value * 100)))}%，等待刷新后再开锅，避免出锅时目标 Tag 已变化。";
    }
    /// <summary>破防完整投食只接受明确的挑战、本体角色和破防标志，不使用显示名称推断。</summary>
    public static bool IsKoishiFullFeed(JsonNode? special, string? role) => Bool(Obj(special)["active"]) && Str(Obj(special)["challengeType"]) == WackyChallenge && Bool(Obj(special)["wackyKoishiShieldBroken"]) && (role ?? "").Trim() == KoishiRole;
    /// <summary>构造目标标签、料理、配方和额外材料组合的拒绝记录键，消除标签及材料输入顺序的影响。</summary>
    /// <remarks>
    /// 标签为空或料理 ID 为负时不生成键；配方 ID 为负时沿用料理 ID。额外材料过滤无效值后取整数部分并排序，
    /// 保留重复项以区分材料份数。键不含订单或经营轮次，记录的作用域与失效时间由调用方管理。
    /// </remarks>
    public static string RejectedRecipeKey(JsonNode? targetTags, double foodId, double recipeId, IEnumerable<double> extras)
    {
        var tags = NormalizeTags(targetTags).OrderBy(x => x, StringComparer.Create(CultureInfo.GetCultureInfo("zh-Hans-CN"), false)).ToArray();
        return tags.Length == 0 || foodId < 0 ? "" : $"{string.Join("&", tags)}|food:{Key(foodId)}|recipe:{Key(recipeId >= 0 ? recipeId : foodId)}|extra:{string.Join(",", extras.Where(x => double.IsFinite(x) && x >= 0).Select(Math.Truncate).OrderBy(x => x).Select(Key))}";
    }
    public static string TextOr(string value, string fallback) => value.Length > 0 ? value : fallback;
}
