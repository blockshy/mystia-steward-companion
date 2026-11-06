namespace MystiaStewardCompanion.Save;

/// <summary>按挑战类型分类当前运行时订单的扩展点；调用方须在允许读取游戏对象的线程调用。</summary>
internal interface ISpecialBusinessOrderModule
{
    bool MatchesChallenge(string challengeType);

    SpecialBusinessOrderClassification Classify(
        string challengeType,
        object? order,
        object? controller,
        string source);
}

/// <summary>按注册顺序选择首个非标准分类；未命中的挑战保留标准分类，不在此推断额外规则。</summary>
internal static class SpecialBusinessModuleRegistry
{
    private static readonly IReadOnlyList<ISpecialBusinessOrderModule> Modules = new ISpecialBusinessOrderModule[]
    {
        new MizuchiOrderModule(),
        new WackyCookingCompetitionOrderModule(),
        new YuyukoChallengeOrderModule(),
        new YuumaChallengeOrderModule(),
    };

    public static bool IsActiveChallenge(string challengeType)
    {
        return !string.IsNullOrWhiteSpace(challengeType)
            && !string.Equals(challengeType, SpecialBusinessChallengeTypes.NotChallenge, StringComparison.Ordinal);
    }

    public static SpecialBusinessOrderClassification Classify(
        string challengeType,
        object? order,
        object? controller,
        string source)
    {
        foreach (var module in Modules)
        {
            if (!module.MatchesChallenge(challengeType)) continue;
            var classification = module.Classify(challengeType, order, controller, source);
            if (classification != SpecialBusinessOrderClassification.Standard)
            {
                return classification;
            }
        }

        return SpecialBusinessOrderClassification.Standard;
    }

    /// <summary>允许进入该特殊订单的后续业务流程；返回值并不表示实时执行条件、租约或原生回调已验证。</summary>
    public static SpecialBusinessOrderClassification AllowedSpecialOrder(
        string role,
        string label,
        int? runtimeGuestId = null)
    {
        return new SpecialBusinessOrderClassification(
            AutomationAllowed: true,
            Role: role,
            RoleLabel: label,
            AutomationBlockReason: "",
            RuntimeGuestId: runtimeGuestId);
    }

    public static SpecialBusinessOrderClassification Blocked(string role, string label, string reason)
    {
        return new SpecialBusinessOrderClassification(
            AutomationAllowed: false,
            Role: role,
            RoleLabel: label,
            AutomationBlockReason: reason,
            RuntimeGuestId: null);
    }
}
