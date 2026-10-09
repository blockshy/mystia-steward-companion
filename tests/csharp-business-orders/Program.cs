using System.Text;
using System.Text.Json.Nodes;
using MystiaStewardCompanion.Business.Application;
using MystiaStewardCompanion.Business.Domain.Orders;
using MystiaStewardCompanion.Business.Domain.GameUi;
using MystiaStewardCompanion.Business.Domain.SpecialBusiness;
using static MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

// 离线差分入口：逐行读取纯值 fixture，检查输入未被修改，再逐行输出结果。
// 本测试宿主无网络、无游戏进程访问，异常必须显式返回，不能把异常伪装成空推荐。
Console.InputEncoding = new UTF8Encoding(false);
Console.OutputEncoding = new UTF8Encoding(false);
string? line;
var sharedCache = new OrderCandidateCache();
while ((line = Console.ReadLine()) != null)
{
    try
    {
        var request = Obj(JsonNode.Parse(line)); var input = Obj(request["args"]); var before = input.ToJsonString();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        JsonNode? result = Str(request["operation"]) switch
        {
            "orders" => OrderRecommendationService.Evaluate(input),
            "orders-cached" => OrderRecommendationService.Evaluate(input, sharedCache),
            "page" => BusinessQueries.EvaluatePage(Obj(input["query"]), Obj(input["snapshot"]), Obj(input["data"]),
                Obj(input["preferences"]), Obj(input["favorites"]), Obj(input["customRecipes"])),
            "page-cached" => BusinessQueries.EvaluatePage(Obj(input["query"]), Obj(input["snapshot"]), Obj(input["data"]),
                Obj(input["preferences"]), Obj(input["favorites"]), Obj(input["customRecipes"]), sharedCache),
            "rule" => SpecialBusinessRules.BuildOrderRule(input["specialBusiness"], Str(input["role"])),
            "wire" => SpecialBusinessRules.BuildWirePolicy(input["specialBusiness"], Str(input["role"]), Num(input["generation"])),
            "requires" => JsonValue.Create(SpecialBusinessRules.RequiresNormalTarget(input["specialBusiness"], Str(input["role"]))),
            "normal" => NormalTargetSelector.Select(input),
            "game-ui" => GameUiTargetService.Evaluate(input),
            "recipe-rows" => OrderRecommendationService.DeriveRecipeRowsFromCandidates(Arr(input["foods"]), Arr(input["beverages"]), Obj(input["options"])),
            "beverage-rows" => OrderRecommendationService.DeriveBeverageRowsFromCandidates(Arr(input["beverages"]), Arr(input["foods"]), Obj(input["options"])),
            "yuyuko-normal" => YuyukoEvaluation.NormalPair(Str(input["challengeType"]), input["food"], input["beverage"], input["modifierPreferences"]),
            "yuyuko-rare" => YuyukoEvaluation.RarePair(Str(input["mode"]), input["food"], input["beverage"], input["demand"]),
            "yuyuko-positive" => YuyukoEvaluation.PositiveSpellPair(input["food"], input["beverage"], input["demand"]),
            _ => throw new ArgumentException("未知测试操作。")
        };
        if (input.ToJsonString() != before) throw new InvalidOperationException("纯领域函数修改了输入。");
        watch.Stop();
        Console.WriteLine(Object(("ok", true), ("result", result), ("elapsedMs", watch.Elapsed.TotalMilliseconds),
            ("cacheHits", sharedCache.Hits), ("cacheMisses", sharedCache.Misses),
            ("cacheEntries", sharedCache.EntryCount), ("cacheBytes", sharedCache.RetainedBytes)).ToJsonString());
    }
    catch (Exception error) { Console.WriteLine(Object(("ok", false), ("error", error.ToString())).ToJsonString()); }
}
