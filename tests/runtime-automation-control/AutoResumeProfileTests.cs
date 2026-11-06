using System.Text.Json;
using System.Text.Json.Nodes;
using MystiaStewardCompanion.Save;

/// <summary>验证共享配置发布与默认关闭边界，调度策略不借用执行租约或已停用的旧主配置。</summary>
internal static class AutoResumeProfileTests
{
    internal static void Run()
    {
        var checks = 0;
        void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
        var profile = new JsonObject();
        foreach (var key in new[] { "automationEnabled", "autoRareOrderEnabled", "autoNormalOrderEnabled",
                     "autoPrepCollectCooking", "autoPrepCompleteOrder", "autoNormalDeliverFood", "autoNormalCompleteOrder" }) profile[key] = false;
        void Publish(long revision) => RuntimeAutomationControlState.PublishAuthority(
            JsonSerializer.SerializeToElement(profile), revision, "test", "合成策略");
        RuntimeAutomationControlState.Reset("合成策略开始");
        Check(!RuntimeAutomationControlState.SnapshotRareGuestAutoResumeSettings().Enabled, "未注册不得启用。");
        Publish(31);
        var policy = RuntimeAutomationControlState.SnapshotRareGuestAutoResumeSettings();
        Check(!policy.Enabled && policy.ThresholdPercent == 30 && !policy.ToFront && policy.AuthorityRevision == 31,
            "历史内部夹具缺字段必须为关闭、30%、队尾。");
        profile["rareGuestParticipationAutoResumeEnabled"] = true;
        try { Publish(32); throw new Exception("仅启用字段不能借默认值激活策略。"); }
        catch (InvalidDataException) { checks++; }
        Check(RuntimeAutomationControlState.SnapshotRareGuestAutoResumeSettings() == policy, "不完整策略必须原子拒绝。");
        profile["rareGuestParticipationAutoResumeThresholdPercent"] = 30;
        try { Publish(32); throw new Exception("缺失位置字段不能借默认队尾。"); }
        catch (InvalidDataException) { checks++; }
        profile["rareGuestParticipationAutoResumePosition"] = "tail";
        Publish(32);
        Check(!RuntimeAutomationControlState.SnapshotRareGuestAutoResumeSettings().Enabled, "调度模块关闭时策略不生效。");
        profile["rareGuestParticipationModuleEnabled"] = true;
        profile["rareGuestParticipationAutoResumeThresholdPercent"] = 42;
        profile["rareGuestParticipationAutoResumePosition"] = "front";
        Publish(33);
        policy = RuntimeAutomationControlState.SnapshotRareGuestAutoResumeSettings();
        Check(policy.Enabled && policy.ToFront && policy.ThresholdPercent == 42 && policy.AuthorityRevision == 33,
            "独立调度可以在制作自动化和执行lease关闭时配置，但仍携带明确权威修订。");
        foreach (var field in new[] { "rareGuestParticipationAutoResumeEnabled", "rareGuestParticipationAutoResumeThresholdPercent", "rareGuestParticipationAutoResumePosition" })
        {
            var saved = JsonNode.Parse(profile[field]!.ToJsonString()); profile.Remove(field);
            try { Publish(34); throw new Exception("缺失任意字段的策略被接受。"); }
            catch (InvalidDataException) { checks++; }
            Check(RuntimeAutomationControlState.SnapshotRareGuestAutoResumeSettings() == policy, "部分字段失败不能更新权威策略。");
            profile[field] = saved;
        }
        foreach (var invalid in new JsonNode?[] { JsonValue.Create(0), JsonValue.Create(101), JsonValue.Create(2.5), JsonValue.Create("30"), null })
        {
            profile["rareGuestParticipationAutoResumeThresholdPercent"] = invalid;
            try { Publish(34); throw new Exception("非法阈值被接受。"); }
            catch (InvalidDataException) { checks++; }
            Check(RuntimeAutomationControlState.SnapshotRareGuestAutoResumeSettings() == policy, "拒绝畸形配置必须原子保留旧策略。");
        }
        profile["rareGuestParticipationAutoResumeThresholdPercent"] = 30;
        profile["rareGuestParticipationAutoResumePosition"] = "unknown";
        try { Publish(34); throw new Exception("非法位置被接受。"); }
        catch (InvalidDataException) { checks++; }
        profile["rareGuestParticipationAutoResumePosition"] = "tail";
        profile["rareGuestParticipationAutoResumeEnabled"] = false;
        Publish(35);
        Check(!RuntimeAutomationControlState.SnapshotRareGuestAutoResumeSettings().Enabled, "关闭共享策略必须立刻生效。");
        RuntimeAutomationControlState.Reset("合成策略结束");
        Check(RuntimeAutomationControlState.SnapshotRareGuestAutoResumeSettings().AuthorityRevision == 0, "结束宿主不得残留旧策略身份。");
        Console.WriteLine($"PASS: auto-resume shared-profile {checks} assertions.");
    }
}
