namespace MystiaStewardCompanion.Save;

internal static class SpecialBusinessContextRuleRegistry
{
    private static readonly IReadOnlyDictionary<string, SpecialBusinessContextRule> Rules = BuildRules();

    public static SpecialBusinessContextRule GetRule(string challengeType, bool active)
    {
        if (Rules.TryGetValue(challengeType, out var rule)) return rule;

        return active
            ? new SpecialBusinessContextRule(
                challengeType,
                "unknown",
                "已检测到未知特殊经营挑战；当前版本只展示挑战类型，不推断目标或结算规则。",
                "未知挑战不会改变推荐排序。请以游戏内目标为准。",
                "未知挑战不会改变自动化策略。建议关闭自动化或手动确认。")
            : Rules[SpecialBusinessChallengeTypes.NotChallenge];
    }

    private static IReadOnlyDictionary<string, SpecialBusinessContextRule> BuildRules()
    {
        return new Dictionary<string, SpecialBusinessContextRule>(StringComparer.Ordinal)
        {
            [SpecialBusinessChallengeTypes.NotChallenge] = new(
                "常规经营",
                "standard",
                "当前不是特殊经营挑战，推荐和自动化按标准料理、酒水订单链路执行。",
                "使用标准推荐排序。",
                "使用标准自动化策略。"),
            ["Story_Basic"] = Trial("妖梦科目一"),
            ["Story_Advanced"] = Trial("妖梦科目二"),
            [SpecialBusinessChallengeTypes.StoryYuyuko] = StoryYuyukoChallenge(),
            [SpecialBusinessChallengeTypes.RetakeYuyuko] = RetakeYuyukoChallenge(),
            ["AnyChallenge"] = UnknownChallenge("任意挑战", "游戏处于挑战占位或选择态；需要以实际挑战切换后的类型为准。"),
            ["Story_BloodPondHell"] = TagTarget("血池地狱 / 饕餮尤魔", "游戏会随机指定两个料理 Tag，命中会影响伤害和怒气。"),
            [SpecialBusinessChallengeTypes.WackyCookingCompetition] = WackyCookingCompetition(),
            ["Story_Seiga_TempleCuisineCompetition"] = TargetFund("青娥料理挑战"),
            ["Story_Futo_TempleCuisineCompetition"] = TargetFund("布都料理挑战"),
            ["Story_Tochiko_TempleCuisineCompetition"] = TargetFund("屠自古料理挑战"),
            ["Story_Ichirin_MusicCompetition"] = NonFood("一轮音游挑战", "该挑战主要由音游流程结算，不接入料理推荐。"),
            ["Story_Minamitu_MusicCompetition"] = NonFood("村纱音游挑战", "该挑战主要由音游流程结算，不接入料理推荐。"),
            ["Story_Toramaru_MusicCompetition"] = NonFood("寅丸音游挑战", "该挑战主要由音游流程结算，不接入料理推荐。"),
            ["Story_Flandre"] = NonFood("芙兰朵露笼女游戏", "该挑战主要由战斗、卡牌和血量流程结算，不接入料理推荐。"),
            ["RogueLike"] = NonFood("RogueLike 模式", "该模式不是标准夜间经营推荐场景。"),
            ["Story_Mizuchi"] = Mizuchi("瑞灵挑战"),
            ["Story_Mizuchi_1"] = Mizuchi("瑞灵挑战一"),
            ["Story_Mizuchi_2"] = Mizuchi("瑞灵挑战二"),
            ["Story_Mizuchi_3"] = Mizuchi("瑞灵挑战三"),
        };
    }

    private static SpecialBusinessContextRule Trial(string displayName)
    {
        return new SpecialBusinessContextRule(
            displayName,
            "trial",
            "妖梦试炼包含多轮目标金额和符卡条件；目标金额和符卡计数来自游戏 HUD。",
            "只展示目标金额和符卡计数，不改变推荐排序。",
            "不改变自动化策略。建议只在确认标准订单可处理时启用。");
    }

    private static SpecialBusinessContextRule StoryYuyukoChallenge()
    {
        return new SpecialBusinessContextRule(
            "幽幽子挑战（剧情版）",
            "boss",
            "剧情版 P2/P3 的自动化必须避开幽幽子的实际厌恶 Tag；P3 还必须复用游戏手动订单评价链路推进进度。",
            "P2 会优先保证安全高评价；P3 会先满足原订单料理和酒水，再优先选择无厌恶 Tag 且可预测触发橙评/粉评的组合，并记录手动评价前后进度。",
            "剧情版幽幽子订单只有在确认 live controller、送达目标、手动 onEvaluate 回调和三阶段评分回调后才会自动提交；回调链路不完整时会暂停评价，避免订单被消耗但进度不涨。");
    }

    private static SpecialBusinessContextRule RetakeYuyukoChallenge()
    {
        return new SpecialBusinessContextRule(
            "幽幽子挑战（重修版）",
            "boss",
            "重修版 P2 会周期性触发负面符卡；P3 包含血量、分身耐心和厨具锁定，分身订单需要 Good/ExGood 才能稳定推进。",
            "P2 会优先保证安全高评价；P3 会先满足原订单料理和酒水，再优先选择可触发橙评/粉评且无厌恶 Tag 的料理、加料与酒水组合。",
            "重修版幽幽子订单只有在确认 live controller、送达目标和 _50/_70 原生进度回调后才会调用游戏评价流程；回调链路不完整时会暂停评价，避免订单被消耗但进度不涨。");
    }

    private static SpecialBusinessContextRule UnknownChallenge(string displayName, string summary)
    {
        return new SpecialBusinessContextRule(
            displayName,
            "unknown",
            summary,
            "等待游戏切换到具体挑战类型后再应用对应推荐规则。",
            "不改变自动化策略。建议确认具体挑战类型后再启用。");
    }

    private static SpecialBusinessContextRule WackyCookingCompetition()
    {
        return new SpecialBusinessContextRule(
            "怪诞料理大赛",
            "tag-target",
            "游戏会随机指定一个料理 Tag，命中会影响挑战分数；该 Tag 会按倒计时刷新，P3 还会按料理和酒水等级影响结算。",
            "优先命中游戏 HUD 指定的目标 Tag；P3 捕获到阶段信息时，会同时偏向高等级料理与酒水；若目标 Tag 倒计时过低，自动化会等待下一轮目标。",
            "怪诞料理大赛订单会标记特殊经营归属，并按当前目标 Tag 与剩余时间决定是否开锅。");
    }

    private static SpecialBusinessContextRule TagTarget(string displayName, string summary)
    {
        return new SpecialBusinessContextRule(
            displayName,
            "tag-target",
            summary,
            "推荐会在同一可完成订单内优先命中游戏 HUD 指定的目标 Tag。",
            "不改变自动化选菜。若目标 Tag 与标准订单冲突，请手动处理。");
    }

    private static SpecialBusinessContextRule TargetFund(string displayName)
    {
        return new SpecialBusinessContextRule(
            displayName,
            "target-fund",
            "料理挑战以目标营业额为核心；当前版本只展示游戏 HUD 目标金额。",
            "只展示目标营业额，不改变推荐排序。",
            "不改变自动化策略。建议观察目标金额与订单需求后手动确认。");
    }

    private static SpecialBusinessContextRule NonFood(string displayName, string summary)
    {
        return new SpecialBusinessContextRule(
            displayName,
            "non-food",
            summary,
            "不接入料理、酒水推荐排序。",
            "不建议使用自动化处理该挑战。");
    }

    private static SpecialBusinessContextRule Mizuchi(string displayName)
    {
        return new SpecialBusinessContextRule(
            displayName,
            "mizuchi",
            "瑞灵挑战包含抓捕、错误料理/酒水 Tag、辣椒等特殊规则；仍需实机日志确认完整副作用。",
            "当前只提示挑战存在，不推断特殊目标。",
            "不改变自动化策略。建议关闭自动化或手动确认。");
    }
}

internal sealed record SpecialBusinessContextRule(
    string DisplayName,
    string Category,
    string RuleSummary,
    string RecommendationPolicy,
    string AutomationPolicy);
