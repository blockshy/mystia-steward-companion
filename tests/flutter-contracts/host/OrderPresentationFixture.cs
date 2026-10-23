using System.Text.Json;
using System.Text.Json.Nodes;
using MystiaStewardCompanion.Business.Application;
using MystiaStewardCompanion.Business.Domain.Orders;
using MystiaStewardCompanion.Core;

/// <summary>
/// 从真实 Mod DTO 和生产业务入口导出跨语言样本，而非在 Dart 手抄计算行。
/// 特意保留 DTO 的 null、默认字段、时间和正常/稀客差异，防止前端完整需求比较
/// 因宿主投影添加字段而永久拒绝所有方案。仅纯托管数据，不读取或驱动 Unity。
/// </summary>
internal static class OrderPresentationFixture
{
    internal static JsonObject Build(JsonElement profile)
    {
        var snapshot = JsonNode.Parse(JsonSerializer.Serialize(new
        {
            nightBusiness = new NightBusinessContext
            {
                Orders = new List<NightBusinessOrder> { new()
                {
                    TraceId = "R-101", OrderLifecycleSequence = 101, DeskCode = 0,
                    GuestId = 3, RuntimeGuestId = 3, GuestName = "测试稀客",
                    FoodTagId = 1, FoodTag = "素", BeverageTagId = 2, BeverageTag = "水果",
                    FirstSeenAtUtc = DateTime.UnixEpoch, LastSeenAtUtc = DateTime.UnixEpoch.AddSeconds(1),
                    Fund = 100, WillPayMoney = true,
                } },
            },
            normalBusiness = new NormalBusinessContext
            {
                Orders = new List<NormalBusinessOrder> { new()
                {
                    TraceId = "N-102", OrderKey = "ptr:1234", OrderLifecycleSequence = 102,
                    DeskCode = 1, GuestId = 3, RuntimeGuestId = 3, GuestName = "测试普客",
                    GuestImageKind = "normal-group", GuestImageId = 3,
                    FoodId = 101, FoodName = "测试料理", BeverageId = 21, BeverageName = "测试酒水",
                    FirstSeenAtUtc = DateTime.UnixEpoch, ControllerAvailable = true,
                } },
            },
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }))!.AsObject();
        // 目录仅一份基础料理和一杯酒水，仍由生产引擎实际计算，不注入任何预制执行方案。
        var catalog = JsonNode.Parse(@"{
          ""ingredients"":[{""id"":1,""name"":""豆腐"",""tags"":[""素""],""price"":2}],
          ""recipes"":[{""id"":101,""recipeId"":201,""name"":""测试料理"",""ingredients"":[""豆腐""],""positiveTags"":[""素""],""negativeTags"":[],""cooker"":""煮锅"",""level"":1,""price"":20}],
          ""beverages"":[{""id"":21,""name"":""测试酒水"",""tags"":[""水果""],""level"":1,""price"":10}],
          ""rareCustomers"":[{""id"":3,""name"":""测试稀客"",""positiveTags"":[""素""],""negativeTags"":[],""beverageTags"":[""水果""],""places"":[""妖怪兽道""]}],
          ""normalCustomers"":[{""id"":3,""name"":""测试普客"",""positiveTags"":[""素""],""beverageTags"":[""水果""],""places"":[""妖怪兽道""]}]
        }")!.AsObject();
        snapshot["recommendationState"] = JsonNode.Parse(@"{
          ""availableRecipeIds"":[101],""availableIngredientIds"":[1],""availableBeverageIds"":[21],
          ""ownedIngredientQty"":{""1"":20},""ownedBeverageQty"":{""21"":20},
          ""placedCookerSnapshotComplete"":true,""placedCookerControllerCount"":1,
          ""placedCookerEmptyControllerCount"":0,""placedCookerLockedControllerCount"":0,
          ""placedCookerReadFailureCount"":0,""placedCookerTypeIds"":[1]
        }");
        var payload = BusinessQueries.BuildOrderPayload(snapshot, catalog,
            JsonNode.Parse(profile.GetRawText())!.AsObject(), new JsonObject(), new JsonObject(), new JsonArray());
        var result = OrderRecommendationService.Evaluate(payload);
        if (result["recommendations"] is not JsonArray { Count: 1 }
            || result["normalOrderDetailPlans"] is not JsonArray { Count: 1 })
            throw new InvalidOperationException("真实生产入口未生成两类订单展示样本。");
        return new JsonObject { ["snapshot"] = snapshot, ["result"] = result };
    }
}
