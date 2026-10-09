using System.Text.Json.Nodes;
using static MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

namespace MystiaStewardCompanion.Business.Domain.Recommendation;

public static partial class RecommendationEngine
{
    /// <summary>
    /// 离线对照和内部应用层共用的操作分发入口。操作名沿用 TypeScript 导出名，便于同一 fixture
    /// 分别交给两种实现；该方法只执行纯计算，未知操作显式报错，不返回伪造的空推荐。
    /// </summary>
    public static JsonNode? Invoke(string operation, JsonObject args) => operation switch
    {
        "buildDynamicFoodTags" => BuildDynamicFoodTags(args),
        "resolveFoodTags" => ResolveFoodTags(args),
        "resolveTagPriority" => ResolveTagPriority(Arr(args["rawTags"]), Arr(args["runtimeRules"])),
        "findTagsThatCanSuppress" => FindTagsThatCanSuppress(Arr(args["activeTags"]), Arr(args["tagsToSuppress"]), Arr(args["runtimeRules"])),
        "hasForbiddenIngredientTag" => JsonValue.Create(HasForbiddenIngredientTag(Obj(args["ingredient"]), Obj(args["recipe"]))),
        "getNormalCustomersByPlace" => GetNormalCustomersByPlace(Obj(args["data"]), Str(args["place"])),
        "buildNormalFoodRecommendations" => BuildNormalFoodRecommendations(args),
        "buildNormalBeverageRecommendations" => BuildNormalBeverageRecommendations(args),
        "compareNormalFoodRecommendations" => JsonValue.Create(CompareNormalFoodRecommendations(Obj(args["left"]), Obj(args["right"]))),
        "compareNormalBeverageRecommendations" => JsonValue.Create(CompareNormalBeverageRecommendations(Obj(args["left"]), Obj(args["right"]))),
        "buildRareFoodCandidates" => BuildRareFoodCandidates(Obj(args["data"]), Obj(args["demand"]), Obj(args["context"]), args["options"] as JsonObject),
        "buildRareBeverageCandidates" => BuildRareBeverageCandidates(Obj(args["data"]), Obj(args["demand"]), Obj(args["context"])),
        "diagnoseRareFoodCandidateSearch" => DiagnoseRareFoodCandidateSearch(Obj(args["data"]), Obj(args["demand"]), Obj(args["context"]), Arr(args["generatedCandidates"])),
        "diagnoseRareBeverageCandidateSearch" => DiagnoseRareBeverageCandidateSearch(Obj(args["data"]), Obj(args["demand"]), Obj(args["context"])),
        "buildRareOrderPlans" => BuildRareOrderPlans(args),
        "buildRareOrderPlansFromCandidates" => BuildRareOrderPlansFromCandidates(args),
        "sortRareOrderPlans" => SortRareOrderPlans(Arr(args["plans"]), args["sortProfile"] as JsonObject, args["sortContext"] as JsonObject),
        "compareFoodCandidates" => JsonValue.Create(CompareFoodCandidates(Obj(args["left"]), Obj(args["right"]))),
        "compareBeverageCandidates" => JsonValue.Create(CompareBeverageCandidates(Obj(args["left"]), Obj(args["right"]))),
        "getVerifiedMissionRecipeSortContext" => GetVerifiedMissionRecipeSortContext(args),
        "isMissionRecipeFoodCandidate" => JsonValue.Create(IsMissionRecipeFoodCandidate(Obj(args["food"]), Obj(args["sortContext"]))),
        "isMissionRecipeExecutionPlan" => JsonValue.Create(IsMissionRecipeExecutionPlan(Obj(args["plan"]), Obj(args["sortContext"]))),
        "normalizeRecommendationSortProfile" => NormalizeSortProfile(args),
        "buildDefaultRecommendationSortProfile" => BuildDefaultRecommendationSortProfile(Str(args["preset"], "balanced")),
        "serializeRecommendationSortProfile" => JsonValue.Create(SerializeRecommendationSortProfile(args)),
        "estimateKoishiBrokenShieldEvaluationScore" => JsonValue.Create(EstimateKoishiBrokenShieldEvaluationScore(args)),
        "estimateKoishiBrokenShieldDamageLevel" => JsonValue.Create(EstimateKoishiBrokenShieldDamageLevel(args)),
        "estimateKoishiBrokenShieldFeedScore" => JsonValue.Create(EstimateKoishiBrokenShieldFeedScore(args)),
        "buildKoishiFeedPlanningInfo" => BuildKoishiFeedPlanningInfo(args),
        "isKoishiFeedPlanSustainable" => JsonValue.Create(IsKoishiFeedPlanSustainable(args)),
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "未定义的推荐领域操作。")
    };
}
