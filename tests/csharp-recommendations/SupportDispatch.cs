using System.Text.Json.Nodes;
using MystiaStewardCompanion.Business.Domain.Support;
using static MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

/// <summary>测试专用适配器；将位置参数投影为同一 JSON fixture，不进入生产业务程序集。</summary>
internal static class SupportDispatch
{
    internal static JsonNode? Invoke(string operation, JsonObject input) => operation switch
    {
        "buildRecommendationDataSet" => RuntimeDataNormalizer.Build(input["runtimeData"]),
        "normalizeCustomRecipeData" => CustomRecipes.NormalizeCustomRecipeData(input),
        "getEffectiveCustomRecipeEntries" => CustomRecipes.GetEffectiveCustomRecipeEntries(input["customRecipes"], (int)Num(input["customerId"]), Str(input["foodTag"])),
        "normalizeIdList" => Array(CustomRecipes.NormalizeIdList(input["ids"]).Select(x => (double)x)),
        "buildCustomFoodCandidates" => CustomRecipes.BuildCustomFoodCandidates(input),
        "mergeCustomFoodCandidates" => CustomRecipes.MergeCustomFoodCandidates(Arr(input["foodCandidates"]), Arr(input["customFoodCandidates"])),
        "buildRuntimeSets" => Cookers.BuildRuntimeSets(input["runtime"], Obj(input["data"])),
        "buildRecommendationCookerNameSet" => Cookers.BuildRecommendationCookerNameSet(Obj(input["runtimeSets"]), Bool(input["filterMissingCookers"])),
        "validateRecommendationCookerSnapshot" => JsonValue.Create(Cookers.ValidateRecommendationCookerSnapshot(Obj(input["runtime"]))),
        "buildAutomationCookerPool" => Cookers.BuildAutomationCookerPool(input["runtime"]),
        "findAvailableAutomationCookerSlot" => Cookers.FindAvailableAutomationCookerSlot(Obj(input["pool"]), Str(input["cookerKey"]), Numbers(input["unavailableControllerIndexes"]).Select(x => (int)x).ToHashSet()),
        "resolveCookerTypeId" => JsonValue.Create(Cookers.ResolveCookerTypeId(Str(input["value"]))),
        "buildRecommendationRuntimeContext" => RuntimeRecommendationSupport.BuildRecommendationRuntimeContext(Obj(input["runtime"]), Obj(input["runtimeSets"]), Obj(input["preferences"]), Obj(input["data"]), input["options"] as JsonObject),
        "buildRecommendationPlanSortContext" => RuntimeRecommendationSupport.BuildRecommendationPlanSortContext(Obj(input["favorites"]), (int)Num(input["customerId"]), Str(input["foodTag"]), Str(input["beverageTag"]), Obj(input["preferences"])),
        "buildPrimaryExecutionPlanPolicy" => PrimaryExecutionPlans.BuildPrimaryExecutionPlanPolicy(Obj(input["preferences"]), Bool(input["automationAllowed"], true)),
        "normalizePrimaryExecutionPlans" => PrimaryExecutionPlans.NormalizePrimaryExecutionPlans(Arr(input["plans"]), Obj(input["sortContext"]), Obj(input["policy"])),
        "getPrimaryExecutionPlan" => PrimaryExecutionPlans.GetPrimaryExecutionPlan(Arr(input["plans"])),
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "未定义的支持模块测试操作。")
    };
}
