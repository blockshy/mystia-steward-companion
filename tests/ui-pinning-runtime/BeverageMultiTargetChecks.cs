using System.Reflection;
using MystiaStewardCompanion.Save;

/// <summary>
/// 酒水多目标专项直接执行正式发布及作用域前缀，原生边界只模拟已交叉核对的
/// UpdateBevField「先排序、后按 CheckPinned 布尔值稳定置顶」行为。
/// 不注入 Harmony、不读取游戏，不将这一托管证据视为真实 IL2CPP 注入通过。
/// </summary>
internal static class BeverageMultiTargetChecks
{
    internal static void Run()
    {
        var checks = 0;
        void Check(bool value, string message)
        {
            checks++;
            if (!value) throw new InvalidOperationException(message);
        }
        MethodInfo Method(string name) => typeof(RuntimeUiPinningService).GetMethod(name,
            BindingFlags.NonPublic | BindingFlags.Static) ?? throw new MissingMethodException(name);
        var begin = Method("OnBeverageRefreshStarted");
        var finish = Method("OnBeverageRefreshFinalized");
        var prefix = Method("OnCheckPinned");
        if (!RuntimeNightBusinessLifecycle.IsActive) RuntimeNightBusinessLifecycle.ActivateNextGeneration();
        var generation = RuntimeNightBusinessLifecycle.Generation;
        var panel = new StoragePanelProbe();
        RuntimeUiTargetSnapshot Target(int slot, int beverageId, bool enabled = true) => new(
            kind: slot < 4 ? RuntimeUiTargetKind.Rare : RuntimeUiTargetKind.Normal,
            color: new RuntimeTargetHighlightColor((byte)(20 + slot * 25), 100, 200),
            listPinningEnabled: enabled, recipeVariantEnabled: false,
            // 关闭列表辅助的订单仍保留订单高亮，构成合法目标；全功能关闭的快照按正式协议应拒绝。
            cookerHighlightEnabled: false, seatHighlightEnabled: false, orderHighlightEnabled: !enabled,
            orderTraceId: (slot < 4 ? "R-" : "N-") + (801 + slot),
            orderKey: slot < 4 ? "" : "ptr:" + (801 + slot).ToString("x"),
            orderLifecycleSequence: 801 + slot, deskCode: slot, recipeId: -1,
            ingredientIds: Array.Empty<int>(), extraIngredientIds: Array.Empty<int>(),
            beverageId: beverageId, cookerTypeId: -1, targetRevision: "beverage-multiple", colorSlot: slot);
        // 前缀返回 true 时保留原生收藏判断，返回 false 时读取实际 ref __result；
        // 不能只检验目标集合里有ID，而漏掉最终游戏查询的匹配逻辑。
        bool IsPinned(int id, bool nativePinned = false)
        {
            object?[] arguments = { 2, id, nativePinned };
            var runOriginal = (bool)prefix.Invoke(null, arguments)!;
            return runOriginal ? nativePinned : (bool)arguments[2]!;
        }
        int[] SortVisible(int[] source)
        {
            begin.Invoke(null, new object[] { panel });
            try { return source.OrderBy(id => id).OrderByDescending(id => IsPinned(id)).ToArray(); }
            finally { finish.Invoke(null, new object?[] { null }); }
        }
        try
        {
            // 两类各四个不同酒水必须全部置顶，同类第二至第四项不得被第一项遮蔽。
            RuntimeUiPinningService.UpdateTargets(generation,
                Enumerable.Range(0, 8).Select(slot => Target(slot, 21 + slot)).ToArray());
            Check(SortVisible(new[] { 1, 22, 3, 28, 25, 21, 24, 26, 27, 23, 99 })
                .SequenceEqual(new[] { 21, 22, 23, 24, 25, 26, 27, 28, 1, 3, 99 }),
                "八笔当前目标的不同酒水没有全部置顶。");
            for (var id = 21; id <= 28; id++)
                Check(!IsPinned(id), "酒水列表作用域结束后泄漏了临时置顶。");
            begin.Invoke(null, new object[] { panel });
            try
            {
                Check(IsPinned(99, nativePinned: true), "辅助覆盖了用户已有原生收藏。");
                Check(!IsPinned(-1), "已送达哨兵值被当成真实酒水置顶。");
            }
            finally { finish.Invoke(null, new object?[] { null }); }

            // 多笔共用同一酒水时只出现一项库存记录，但颜色声明不能仅保留第一单。
            RuntimeUiPinningService.UpdateTargets(generation,
                Enumerable.Range(0, 8).Select(slot => Target(slot, slot < 4 ? 31 : 32)).ToArray());
            Check(SortVisible(new[] { 1, 31, 2, 32 }).SequenceEqual(new[] { 31, 32, 1, 2 }),
                "共用酒水未稳定置顶或错误复制库存条目。");
            var shared = RuntimeUiPinningService.ReadTargetSet();
            Check((int)shared.GetBeverageClaims(31) == 15 && (int)shared.GetBeverageClaims(32) == 240,
                "共用酒水丢失后续订单的颜色声明。");

            // 已送达和关闭列表辅助只移除对应声明，其他待取酒水仍全部保留。
            RuntimeUiPinningService.UpdateTargets(generation, new[]
            {
                Target(0, -1), Target(1, 22, enabled: false), Target(2, 23), Target(7, 28),
            });
            Check(SortVisible(new[] { 1, 21, 22, 23, 28 }).SequenceEqual(new[] { 23, 28, 1, 21, 22 }),
                "撤回一笔后遗漏仍然待取的酒水，或恢复了已送达/关闭的声明。");
            RuntimeUiPinningService.UpdateTargets(generation, Array.Empty<RuntimeUiTargetSnapshot>());
            Check(SortVisible(new[] { 28, 23, 1 }).SequenceEqual(new[] { 1, 23, 28 }),
                "目标清空后仍有临时置顶残留。");
            Console.WriteLine($"PASS beverage multi-targets: {checks} assertions; production scopes and prefix, no Harmony/native game injection.");
        }
        finally
        {
            RuntimeUiPinningService.UpdateTargets(generation, Array.Empty<RuntimeUiTargetSnapshot>());
            RuntimeUiPinningService.Abandon("beverage offline checks completed");
        }
    }
}
