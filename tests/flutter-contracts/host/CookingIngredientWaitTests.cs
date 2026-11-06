using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using MystiaStewardCompanion.Business.Application.Automation;
using MystiaStewardCompanion.LocalApi;
using MystiaStewardCompanion.Save;

/// <summary>
/// 直接调用生产开锅结果工厂、Finish结果翻译与业务状态机，验证扣料前缺料只等待。
/// 不调用TryStartCooking或任何Unity/IL2CPP入口；源码接线检查单独标明其证据边界。
/// 已送酒、其他失败和提交不确定必须保留，不能用材料等待掩盖实际副作用。
/// </summary>
internal static class CookingIngredientWaitTests
{
    private const string Reason = "ingredients-unavailable-before-cooking";
    private static int _checks;
    private static void Check(bool condition, string message)
    {
        _checks++;
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static int Run()
    {
        _checks = 0;
        var type = typeof(RuntimeOrderPreparationService);
        var resultType = type.GetNestedType("CookingStartResult", BindingFlags.NonPublic)!;
        var factory = resultType.GetMethod("WaitForIngredients", BindingFlags.Public | BindingFlags.Static)!;
        var wait = factory.Invoke(null, new object[] { "材料库存不足；尚未扣料，等待新方案。" })!;
        Check((bool)resultType.GetProperty("Waiting")!.GetValue(wait)!, "扣料前缺料结果应明确等待。");
        Check(!(bool)resultType.GetProperty("Ok")!.GetValue(wait)!, "未开锅不能返回开锅成功。");
        Check((string)resultType.GetProperty("Code")!.GetValue(wait)! == Reason, "缺料等待必须使用明确结构化码。");
        Check((string)resultType.GetProperty("JobId")!.GetValue(wait)! == "", "未开锅不能伪造料理任务。");
        var finish = type.GetMethod("Finish", BindingFlags.NonPublic | BindingFlags.Static)!;
        OrderPreparationResult Finish(OrderPreparationResult result) => (OrderPreparationResult)finish.Invoke(null, new object[] { result })!;
        OrderPreparationResult Waiting(string name)
        {
            var result = new OrderPreparationResult();
            result.Steps.Add(new() { Name = name, Code = Reason, Ok = true, Skipped = true, Message = "未扣料" });
            return result;
        }
        foreach (var name in new[] { "自动开始料理", "普客开始料理" })
        {
            var result = Finish(Waiting(name));
            Check(result.Ok && !result.Prepared && !result.ServedFood && !result.ServedBeverage, "单纯等待不能声称已准备或已送达。");
            Check(result.Automation.Outcome == "waiting" && result.Automation.Stage == "cooking-start"
                && result.Automation.ReasonCode == Reason && result.Automation.RetryAfterMs == 1000, "两类订单必须采用同一可恢复等待协议。");
            var state = AutomationMachine.Empty(name == "自动开始料理" ? "rare" : "normal", "synthetic:1", 100);
            state["retryCount"] = 2; state["retryStage"] = "ensure-cooking";
            var response = JsonSerializer.SerializeToNode(result, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
            for (var iteration = 0; iteration < 20; iteration++)
            {
                AutomationMachine.ApplyOutcome(state, response, "ensure-cooking", 1000 + iteration * 1000, true, 3);
                Check(state["retryCount"]!.GetValue<int>() == 2 && !state["paused"]!.GetValue<bool>(),
                    "重复缺料等待不能增加失败次数或触发出错暂停，也不清除真实历史失败。");
            }

            var served = Waiting(name);
            served.ServedBeverage = true;
            served.Steps.Insert(0, new() { Code = "beverage-delivered", Name = "送酒", Ok = true, Message = "已确认送酒" });
            Finish(served);
            Check(served.Prepared && served.ServedBeverage && served.Automation.Outcome == "waiting"
                && served.Automation.ReasonCode == Reason && served.Steps[0].Ok && !served.Steps[0].Skipped,
                "开锅等待必须保留此前已成功送酒的真实事实。");

            foreach (var barrier in new[] { "beverage-delivery-commit-uncertain", "cooking-start-unowned", "food-delivery-commit-uncertain" })
            {
                var blocked = Waiting(name);
                blocked.Steps.Insert(0, new() { Name = "不确定提交", Code = barrier, Ok = false, Message = "需要人工检查" });
                Finish(blocked);
                Check(blocked.Automation.Outcome == "blocked" && blocked.Automation.ReasonCode == barrier,
                    "材料等待不能掩盖扣料/送达的不确定提交屏障。");
            }
            var failure = Waiting(name);
            failure.Steps.Insert(0, new() { Name = "其他失败", Code = "synthetic-failure", Ok = false, Message = "其他步骤失败" });
            Check(Finish(failure).Automation.Outcome == "retryable-failure", "无关失败不能被缺料等待吞掉。");
            var started = Waiting(name);
            started.Automation.JobId = "synthetic-started-job";
            started.Steps.Add(new() { Name = name, Code = "cooking-started", Ok = true, Message = "已开锅" });
            Check(Finish(started).Automation.Outcome == "progressed" && started.Automation.JobId == "synthetic-started-job",
                "已经开锅的证据优先于材料等待，不能将任务当成无副作用。");
            var pending = Waiting(name);
            pending.Automation.JobId = "synthetic-existing-job";
            pending.Steps.Add(new() { Name = name, Code = "cooking-pending", Ok = true, Skipped = true });
            Check(Finish(pending).Automation.Stage == "cooking-delivery" && pending.Automation.ReasonCode == "cooking-pending",
                "既有锅次仍进入料理跟踪，不因材料等待返回重复开锅阶段。");
        }
        // 静态接线只证明等待返回位于实际扣料/SetCook之前，不冒充真实原生动作测试。
        var root = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (root != null && !File.Exists(Path.Combine(root.FullName, "mods/bepinex/src/Save/RuntimeOrderPreparationService.Cooking.cs"))) root = root.Parent;
        Check(root != null, "需在明确代码工作区内验证生产接线。");
        var source = File.ReadAllText(Path.Combine(root!.FullName, "mods/bepinex/src/Save/RuntimeOrderPreparationService.Cooking.cs"));
        var inventoryCheck = source.IndexOf("if (!HasEnoughIngredients(ingredientIds", StringComparison.Ordinal);
        var waitingReturn = source.IndexOf("return CookingStartResult.WaitForIngredients(", StringComparison.Ordinal);
        var deduction = source.IndexOf("InvokeRuntimeStorageOut(\"IngredientOut\"", StringComparison.Ordinal);
        var setCook = source.IndexOf("InvokeInstance(cookController, \"SetCook\"", StringComparison.Ordinal);
        Check(inventoryCheck >= 0 && waitingReturn > inventoryCheck && deduction > waitingReturn && setCook > deduction,
            "可恢复缺料返回必须在实际扣料和SetCook之前，不能覆盖已提交分支。");
        Console.WriteLine($"PASS: pre-cooking ingredient wait {_checks} assertions; production result translation and state machine, no game execution.");
        return 0;
    }
}
