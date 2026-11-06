using System.Reflection;
using MystiaStewardCompanion.Save;

/// <summary>
/// 直接执行生产刷新作用域和发布租约，验证排序只能借用同线程、同经营、同面板的当前目标。
/// 不安装Harmony，实际列表桥与变体映射分别由各自专项覆盖。
/// </summary>
internal static class ListOrderContextChecks
{
    internal static void Run()
    {
        var checks = 0;
        void Check(bool value, string message)
        {
            checks++;
            if (!value) throw new InvalidOperationException(message);
        }
        void Invoke(string name, params object?[] args) => typeof(RuntimeUiPinningService)
            .GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, args);
        if (!RuntimeNightBusinessLifecycle.IsActive) RuntimeNightBusinessLifecycle.ActivateNextGeneration();
        var generation = RuntimeNightBusinessLifecycle.Generation;
        RuntimeUiTargetSnapshot Target(int slot, long sequence, int beverage) => new(
            slot % 2 == 0 ? RuntimeUiTargetKind.Rare : RuntimeUiTargetKind.Normal,
            new(30, 100, 220), true, false, false, false, false,
            $"{(slot % 2 == 0 ? 'R' : 'N')}-{sequence}", slot % 2 == 0 ? "" : $"ptr:{sequence:x}",
            sequence, slot, 12, new[] { 13 }, Array.Empty<int>(), beverage, 1, "order-context", slot);
        var targets = new[] { Target(0, 30, 14), Target(1, 10, 14), Target(2, 20, 15) };
        RuntimeUiPinningService.UpdateTargets(generation, targets);
        var current = RuntimeUiPinningService.ReadTargetSet();
        Check(current.GetOrderPriority(current.GetBeverageClaims(14)) == 10, "同酒水未取最早跨客类订单");
        Check(current.GetOrderPriority(current.GetBeverageClaims(15)) == 20, "独立物品优先级错误");
        Check(current.GetOrderPriority(current.GetRecipeClaims(12)) == 10, "共享料理优先级错误");
        Check(current.GetOrderPriority(current.GetIngredientClaims(13)) == 10, "共享食材优先级错误");
        Check(current.GetOrderPriority(RuntimeUiTargetKinds.None) == long.MaxValue, "非目标未保留末位优先级");
        var cooking = new CookingSelectionPanelProbe();
        var storage = new StoragePanelProbe();
        Check(!RuntimeUiPinningService.TryGetListOrderContext(RuntimeUiListSurfaceKind.Cooking, cooking, out _, out _),
            "作用域之外开放了列表排序");
        try
        {
            // 完整刷新中的内联分类没有panel参数，应从已进入的料理作用域取得唯一所属面板。
            Invoke("OnCookingRefreshStarted", cooking);
            Check(RuntimeUiPinningService.TryGetListOrderContext(RuntimeUiListSurfaceKind.Cooking, null, out var owner, out var scoped)
                && ReferenceEquals(owner, cooking) && ReferenceEquals(scoped, current), "内联分类未获得精确面板和目标");
            Check(!RuntimeUiPinningService.TryGetListOrderContext(RuntimeUiListSurfaceKind.Cooking, new CookingSelectionPanelProbe(), out _, out _),
                "接受了另一原生面板");
            Check(!RuntimeUiPinningService.TryGetListOrderContext(RuntimeUiListSurfaceKind.Cooking, storage, out _, out _),
                "接受了不同面板类型");
            var alias = new CookingSelectionPanelProbe();
            typeof(CookingSelectionPanelProbe).GetProperty("m_CachedPtr")!.SetValue(alias, cooking.m_CachedPtr);
            Check(RuntimeUiPinningService.TryGetListOrderContext(RuntimeUiListSurfaceKind.Cooking, alias, out _, out _),
                "同原生指针的嵌套wrapper被错误拒绝");
            Check(Task.Run(() => !RuntimeUiPinningService.TryGetListOrderContext(
                RuntimeUiListSurfaceKind.Cooking, cooking, out _, out _)).GetAwaiter().GetResult(), "后台线程取得了Unity列表上下文");
            Check(RuntimeUiPinningService.TryAcquireTargetPublicationLease(generation, out var lease), "无法取得同经营代次发布租约");
            using (lease)
                Check(RuntimeUiPinningService.TryGetListOrderContext(RuntimeUiListSurfaceKind.Cooking, cooking, out _, out _),
                    "持租约时上下文失效");
            Invoke("OnCookingRefreshStarted", alias);
            Invoke("OnCookingRefreshFinalized", (object?)null);
            Check(RuntimeUiPinningService.TryGetListOrderContext(RuntimeUiListSurfaceKind.Cooking, alias, out _, out _),
                "内层刷新结束提前清除了外层作用域");
            RuntimeUiPinningService.UpdateTargets(generation, new[] { targets[0], targets[2] });
            Check(!RuntimeUiPinningService.TryGetListOrderContext(RuntimeUiListSurfaceKind.Cooking, cooking, out _, out _),
                "发布变化后仍允许旧作用域写入");
        }
        finally { Invoke("OnCookingRefreshFinalized", (object?)null); }
        var after = RuntimeUiPinningService.ReadTargetSet();
        Check(after.GetOrderPriority(after.GetBeverageClaims(14)) == 30, "早单撤回后共享物品优先级未更新");
        Invoke("OnBeverageRefreshStarted", storage);
        try
        {
            Check(RuntimeUiPinningService.TryGetListOrderContext(RuntimeUiListSurfaceKind.Beverage, storage, out _, out var target)
                && ReferenceEquals(target, after), "酒水作用域未使用最新发布");
            Check(!RuntimeUiPinningService.TryGetListOrderContext(RuntimeUiListSurfaceKind.Cooking, null, out _, out _),
                "酒水作用域泄漏为料理作用域");
        }
        finally { Invoke("OnBeverageRefreshFinalized", (object?)null); }
        Check(!RuntimeUiPinningService.TryGetListOrderContext(RuntimeUiListSurfaceKind.Beverage, storage, out _, out _),
            "酒水刷新结束后仍可排序");
        Invoke("OnCookingRefreshStarted", cooking);
        Invoke("OnCookingRefreshStarted", cooking);
        Invoke("OnCookingRefreshFinalized", new InvalidOperationException("native refresh failed"));
        Check(!RuntimeUiPinningService.TryGetListOrderContext(RuntimeUiListSurfaceKind.Cooking, cooking, out _, out _),
            "内层原生刷新失败后外层仍允许排序");
        Invoke("OnCookingRefreshFinalized", (object?)null);
        RuntimeUiPinningService.UpdateTargets(generation, Array.Empty<RuntimeUiTargetSnapshot>());
        RuntimeUiPinningService.Abandon("list order context checks completed");
        Console.WriteLine($"PASS list-order context: {checks} assertions; production scopes/priority/publication lease, no Harmony or game.");
    }
}
