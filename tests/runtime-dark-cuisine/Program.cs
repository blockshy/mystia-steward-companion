using MystiaStewardCompanion.Save;
using static MystiaStewardCompanion.Save.RuntimeOrderPreparationService;

// 真实 partial + 可控外部边界：覆盖错误回调发生前后，避免仅断言源码文字。
var assertions = 0;
void Check(bool condition, string detail)
{
    assertions++;
    if (!condition) throw new InvalidOperationException(detail);
}
var scenario = Reset();
Check(ValidateProduction().Accepted, "明确黑暗料理任务+真实基底+禁忌加料应允许生产");
Check(scenario.Effects.RandomOverrideCalls == 0, "不得调用随机成品替换入口预测");
scenario.Target.FoodProductionKind = "";
Check(!ValidateProduction().Accepted, "缺省 -1 不能被当作黑暗料理");
scenario.Target.FoodId = 5;
Check(ValidateProduction().Accepted, "普通料理仍走普通验证分支");
scenario = Reset(); scenario.Effects.Protected = true;
Check(!ValidateProduction().Accepted, "药物保护生效时不应消耗材料尝试黑暗料理");
scenario = Reset(); scenario.Effects.registeredOverrideFoodBuff = new List<object> { new() };
Check(!ValidateProduction().Accepted, "存在成品替换效果时拒绝生产");
scenario = Reset(); scenario.Effects.registeredOverrideFoodBuff = new object();
Check(!ValidateProduction().Accepted, "无法完整读取成品替换集合时拒绝生产");
scenario = Reset(); scenario.Effects.ThrowOnRead = true;
Check(!ValidateProduction().Accepted, "保护效果读取异常不能放行");
scenario = Reset(); scenario.Target.MissionGeneration = 6;
Check(!ValidateProduction().Accepted, "旧任务代际不能放行");
scenario = Reset(); scenario.Signals = scenario.Signals with { Signals = new[] { new Signal(), new Signal() } };
Check(!ValidateProduction().Accepted, "重复任务目标不能猜测匹配");
scenario = Reset(); scenario.Mission = scenario.Mission with { DefinitionFailureCount = 1 };
Check(!ValidateProduction().Accepted, "任务定义不完整时只等待当前目标");
scenario = Reset();
Check(!ValidateProduction(initial: new FakeFood { Id = 5, banTags = new[] { 99 } }).Accepted, "无禁忌交集不能生产");
Check(!ValidateProduction(initial: new FakeFood { Id = 5, Modifier = new[] { 3 } }).Accepted, "实际加料不匹配应拒绝");
Check(!ValidateProduction(initial: new FakeFood { Id = -1 }).Accepted, "初始成品必须属于真实基底");
scenario = Reset(); scenario.Storage.Items.Add(new FakeFood());
var taken = TakeStock();
Check(taken.Handled && taken.Result.ServedFood && scenario.Storage.Removes == 1 && scenario.DeliveryCalls == 1, "已存成品仅取出并送达一次");
Check(scenario.FindOrderCalls == 1 && scenario.EvaluationCalls == 1 && scenario.Barriers == 0, "取餐后重取订单并复用评价事务");
Check(CountStoredDarkCuisine() == 0, "已取成品不得仍计入库存");
scenario = Reset(); scenario.Storage.Items.Add(new FakeFood()); scenario.Storage.ThrowAfterRemove = true;
TakeStock();
Check(scenario.Barriers == 1 && scenario.DeliveryCalls == 0 && scenario.ReturnCalls == 0, "移除回调异常后不能再次送达或回存");
scenario = Reset(); scenario.Storage.Items.Add(new FakeFood()); scenario.Delivery = "uncertain";
TakeStock();
Check(scenario.Barriers == 1 && scenario.ReturnCalls == 0 && scenario.EvaluationCalls == 0, "送达不确定时不能回存或重复评价");
scenario = Reset(); scenario.Storage.Items.Add(new FakeFood()); scenario.Delivery = "not-committed";
var waiting = TakeStock();
Check(waiting.Handled && !waiting.Result.ServedFood && scenario.ReturnCalls == 1 && scenario.Storage.Items.Count == 1 && scenario.Barriers == 0,
    "明确未提交时允许回存同一成品一次");
scenario = Reset(); scenario.Storage.Items.Add(new FakeFood()); scenario.Delivery = "not-committed"; scenario.StoreSucceeds = false;
TakeStock();
Check(scenario.Barriers == 1 && scenario.ReturnCalls == 1, "回存失败不可继续取餐或生产");
scenario = Reset(); scenario.Storage.Items.Add(new FakeFood()); scenario.Storage.AfterRemove = () => scenario.Lifecycle = false;
TakeStock();
Check(scenario.Barriers == 1 && scenario.DeliveryCalls == 0 && scenario.Events.Last() == "lifecycle", "回调结束会话后不再访问原生保温箱");
scenario = Reset(); scenario.Storage.Items.Add(new FakeFood()); scenario.Storage.AfterRemove = () => scenario.Binding = false;
TakeStock();
Check(scenario.Barriers == 1 && scenario.DeliveryCalls == 0, "取餐后订单身份漂移不可送达");
scenario = Reset(); scenario.Storage.Items.Add(new FakeFood()); OwnStock(42);
Check(CountStoredDarkCuisine() == 0 && TakeStock().Handled && scenario.Storage.Removes == 0, "旧烹饪事务仍持有的成品不得抢占");
scenario = Reset(); scenario.Storage.Items.Add(new FakeFood()); scenario.Storage.Items.Add(new FakeFood());
Check(CountStoredDarkCuisine() is null && TakeStock().Handled && scenario.Storage.Removes == 0, "重复原生实例使库存未知且不可取餐");
scenario = Reset();
Check(!TakeStock(requireStock: false).Handled && TakeStock().Handled, "无成品时普通生产可继续，明确库存方案必须等待重算");
scenario = Reset(); scenario.Storage.Items.Add(new FakeFood()); scenario.ExistingJob = true;
Check(!TakeStock().Handled && scenario.Storage.Removes == 0, "已有烹饪事务不能另取库存造成双重送达");
Console.WriteLine($"Runtime dark cuisine guard/storage smoke passed: {assertions} assertions (managed native-boundary substitutes; no game).");