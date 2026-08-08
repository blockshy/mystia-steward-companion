namespace MystiaStewardCompanion.Save;

internal static class SpecialBusinessContextRuleRegistry
{
    private static readonly IReadOnlyDictionary<string, SpecialBusinessContextRule> Rules = BuildRules();

    public static SpecialBusinessContextRule GetRule(string challengeType, bool active)
    {
        if (Rules.TryGetValue(challengeType, out var rule)) return rule;

        return active
            ? new SpecialBusinessContextRule(
                "unknown",
                "已检测到未知特殊经营挑战；当前版本只展示挑战类型，不推断目标或结算规则。",
                "未知挑战不会改变推荐排序。请以游戏内目标为准。",
                "未知挑战不会改变自动化策略。建议关闭自动化或手动确认。",
                Array.Empty<int>())
            : Rules[SpecialBusinessChallengeTypes.NotChallenge];
    }

    private static IReadOnlyDictionary<string, SpecialBusinessContextRule> BuildRules()
    {
        return new Dictionary<string, SpecialBusinessContextRule>(StringComparer.Ordinal)
        {
            [SpecialBusinessChallengeTypes.NotChallenge] = new(
                "standard",
                "当前不是特殊经营挑战，推荐和自动化按标准料理、酒水订单链路执行。",
                "使用标准推荐排序。",
                "使用标准自动化策略。",
                Array.Empty<int>()),
            ["Story_Basic"] = Trial(),
            ["Story_Advanced"] = Trial(),
            [SpecialBusinessChallengeTypes.StoryYuyuko] = StoryYuyukoChallenge(),
            [SpecialBusinessChallengeTypes.RetakeYuyuko] = RetakeYuyukoChallenge(),
            ["AnyChallenge"] = UnknownChallenge("游戏处于挑战占位或选择态；需要以实际挑战切换后的类型为准。"),
            [SpecialBusinessChallengeTypes.BloodPondHell] = BloodPondHell(),
            [SpecialBusinessChallengeTypes.WackyCookingCompetition] = WackyCookingCompetition(),
            ["Story_Seiga_TempleCuisineCompetition"] = TargetFund(),
            ["Story_Futo_TempleCuisineCompetition"] = TargetFund(),
            ["Story_Tochiko_TempleCuisineCompetition"] = TargetFund(),
            ["Story_Ichirin_MusicCompetition"] = NonFood("该挑战主要由音游流程结算，不接入料理推荐。"),
            ["Story_Minamitu_MusicCompetition"] = NonFood("该挑战主要由音游流程结算，不接入料理推荐。"),
            ["Story_Toramaru_MusicCompetition"] = NonFood("该挑战主要由音游流程结算，不接入料理推荐。"),
            ["Story_Flandre"] = NonFood("该挑战主要由战斗、卡牌和血量流程结算，不接入料理推荐。"),
            ["RogueLike"] = NonFood("该模式不是标准夜间经营推荐场景。"),
            [SpecialBusinessChallengeTypes.StoryMizuchi] = MizuchiStory(),
            [SpecialBusinessChallengeTypes.MizuchiTrial1] = MizuchiTrial(),
            [SpecialBusinessChallengeTypes.MizuchiTrial2] = MizuchiTrial(),
            [SpecialBusinessChallengeTypes.MizuchiTrial3] = MizuchiTrial(),
        };
    }

    private static SpecialBusinessContextRule Trial()
    {
        return new SpecialBusinessContextRule(
            "trial",
            "妖梦试炼包含多轮目标金额和符卡条件；目标金额和符卡计数来自游戏 HUD。",
            "只展示目标金额和符卡计数，不改变推荐排序。",
            "不改变自动化策略。建议只在确认标准订单可处理时启用。",
            Array.Empty<int>());
    }

    private static SpecialBusinessContextRule StoryYuyukoChallenge()
    {
        return new SpecialBusinessContextRule(
            "boss",
            "剧情版 P2/P3 的自动化必须避开幽幽子的实际厌恶 Tag；P3 还必须复用游戏手动订单评价链路推进进度。",
            "P2 会优先保证安全高评价；P3 会先满足原订单料理和酒水，再优先选择无厌恶 Tag 且可预测达到满意（Good）/完美（ExGood）的组合，并记录手动评价前后进度。",
            "剧情版幽幽子订单只有在确认 live controller、送达目标、手动 onEvaluate 回调和三阶段评分回调后才会自动提交；回调链路不完整时会暂停评价，避免订单被消耗但进度不涨。",
            Array.Empty<int>());
    }

    private static SpecialBusinessContextRule RetakeYuyukoChallenge()
    {
        return new SpecialBusinessContextRule(
            "boss",
            "重修版 P2 会周期性触发负面符卡；P3 包含血量、分身耐心和厨具锁定，分身订单需要 Good/ExGood 才能稳定推进。",
            "P2 会优先保证安全高评价；P3 会先满足原订单料理和酒水，再优先选择可达到满意（Good）/完美（ExGood）且无厌恶 Tag 的料理、加料与酒水组合。",
            "重修版幽幽子订单只有在确认 live controller、送达目标和 _50/_70 原生进度回调后才会调用游戏评价流程；回调链路不完整时会暂停评价，避免订单被消耗但进度不涨。",
            Array.Empty<int>());
    }

    private static SpecialBusinessContextRule UnknownChallenge(string summary)
    {
        return new SpecialBusinessContextRule(
            "unknown",
            summary,
            "等待游戏切换到具体挑战类型后再应用对应推荐规则。",
            "不改变自动化策略。建议确认具体挑战类型后再启用。",
            Array.Empty<int>());
    }

    private static SpecialBusinessContextRule WackyCookingCompetition()
    {
        return new SpecialBusinessContextRule(
            "tag-target",
            "第一阶段按喜好 Tag 命中数评价；第二阶段先要求完美（ExGood）再检查轮换目标 Tag；第三阶段分身另有改判规则，古明地恋本体使用揭示 Tag、护盾和投食进度机制。",
            "Mod 对第二阶段和第三阶段分身使用更严格的保守策略：保留原订单，并同时要求预估完美（ExGood）和当前目标 Tag；本体则按护盾与揭示状态选择。",
            "订单会标记特殊经营归属。开锅前检查阶段、目标 Tag 和剩余时间；出锅后再校验成品，无法确认原生评价链时暂停。",
            Array.Empty<int>());
    }

    private static SpecialBusinessContextRule BloodPondHell()
    {
        return new SpecialBusinessContextRule(
            "tag-target",
            "游戏会随机指定两个料理 Tag；原订单成立后，同时命中两个目标 Tag 才是完整挑战目标方案。",
            "仅对精确识别的血池地狱 BOSS 订单，在保持原订单成立的前提下选择同时满足两个动态料理 Tag 的方案。",
            "仅在 BOSS 身份、经营代际、原订单、双 Tag 目标、实际成品和实时订单均严格复核后自动制作；酒水先送达。料理送达与订单完成均开启时按订单原生路由精确结算，否则保留成品等待玩家处理。",
            Array.Empty<int>());
    }

    private static SpecialBusinessContextRule TargetFund()
    {
        return new SpecialBusinessContextRule(
            "target-fund",
            "料理挑战以目标营业额为核心；当前版本只展示游戏 HUD 目标金额。",
            "只展示目标营业额，不改变推荐排序。",
            "不改变自动化策略。建议观察目标金额与订单需求后手动确认。",
            Array.Empty<int>());
    }

    private static SpecialBusinessContextRule NonFood(string summary)
    {
        return new SpecialBusinessContextRule(
            "non-food",
            summary,
            "不接入料理、酒水推荐排序。",
            "不建议使用自动化处理该挑战。",
            Array.Empty<int>());
    }

    private static SpecialBusinessContextRule MizuchiStory()
    {
        return new SpecialBusinessContextRule(
            "mizuchi-story",
            "寻找瑞灵踪迹会随机让一名稀客表现为错误料理、错误酒水或错误对话；原始订单 Tag 仍有效，只有当前被瑞灵附身的稀客需要在料理 Modifier 中加入噗噗呦果（材料 5002）。",
            "附身订单优先，并在满足原始料理、酒水订单的同时强制加入一份噗噗呦果；普通稀客继续按原始订单推荐，且禁止把噗噗呦果作为额外材料。",
            "仅在评价闭包、稀客、随机控制类型、目标材料和订单生命周期均精确一致时接管；身份或角色漂移会立即停止副作用。",
            new[] { MizuchiConstants.PuyoyoFruitIngredientId });
    }

    private static SpecialBusinessContextRule MizuchiTrial()
    {
        return new SpecialBusinessContextRule(
            "mizuchi-trial",
            "月都试炼会让一名稀客表现为错误料理、错误酒水或错误对话；原始订单 Tag 仍有效，只有当前被瑞灵附身的稀客需要在料理 Modifier 中加入辣椒水（材料 5005）。",
            "附身订单优先，并在满足原始料理、酒水订单的同时强制加入一份辣椒水；普通稀客继续按原始订单推荐，且禁止把辣椒水作为额外材料。",
            "仅在评价闭包、稀客、控制类型、目标材料和订单生命周期均精确一致时接管；身份或角色漂移会立即停止副作用。",
            new[] { MizuchiConstants.PepperWaterIngredientId });
    }
}

internal sealed record SpecialBusinessContextRule(
    string Category,
    string RuleSummary,
    string RecommendationPolicy,
    string AutomationPolicy,
    IReadOnlyList<int> RequiredExtraIngredientIds);
