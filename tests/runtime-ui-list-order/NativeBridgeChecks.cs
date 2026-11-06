using System.Reflection;
using BepInEx.Logging;
using GameData.Core.Collections;
using MystiaStewardCompanion.Save;
using NightScene.UI.CookingUtility;
using NativeIngredientList = Il2CppSystem.Collections.Generic.List<Il2CppSystem.Collections.Generic.KeyValuePair<GameData.Core.Collections.Ingredient, int>>;
using NativeBeverageList = Il2CppSystem.Collections.Generic.List<Il2CppSystem.Collections.Generic.KeyValuePair<GameData.Core.Collections.Sellable, int>>;

/// <summary>
/// 用#783对应的精确FullName、属性及方法签名运行正式反射桥；所有postfix显式调用，
/// 不安装Harmony。完整刷新路径直接调用分类回调，不借道UpdateIngField，防止漏掉首次打开。
/// </summary>
internal static class NativeBridgeChecks
{
    internal static void Run(Action<bool, string> check)
    {
        check(!typeof(Il2CppSystem.Collections.Generic.KeyValuePair<Ingredient, int>).IsValueType
            && typeof(Il2CppSystem.Collections.Generic.KeyValuePair<Ingredient, int>).BaseType == typeof(Il2CppSystem.ValueType),
            "键值对夹具必须是原生值类型的CLR引用包装，不能用CLR struct掩盖封送差异");
        // 独立重现上轮setter故障：同样的盒对象通过泛型setter会污染payload，Object setter不会。
        // 这里只模拟错误后果，不在CLR测试进程创建非法原生指针；实际IL合同由独立只读探针核验。
        var legacyIngredient = Ingredients(11, 12);
        legacyIngredient[0] = legacyIngredient.Values[1];
        check(legacyIngredient.InvalidBoxPayload && legacyIngredient.GenericWrites == 1,
            "未重现食材泛型setter把装箱头误当作值payload的故障");
        var legacyBeverage = Beverages(40, 80);
        legacyBeverage[0] = legacyBeverage.Values[1];
        check(legacyBeverage.InvalidBoxPayload && legacyBeverage.GenericWrites == 1,
            "未重现酒水与食材共用的装箱封送故障");
        var log = new ManualLogSource();
        RuntimeUiListOrderService.Attach(log);
        check(RuntimeUiListOrderService.Status.StartsWith("patched:3/3", StringComparison.Ordinal)
            && HarmonyLib.Harmony.Registrations.Count == 3, "三处精确排序钩子签名没有全部注册");
        var recipeHook = HarmonyLib.Harmony.Registrations.Single(entry => entry.Original.Name == "UpdateRecipeField");
        check(recipeHook.Postfix.after.Contains(RuntimeTargetRecipeVariantService.HarmonyId)
            && recipeHook.Postfix.priority == HarmonyLib.Priority.Last, "料理排序未显式安排在加料插入验收之后");
        // 精确适配器不能接受普通CLR列表、错误元素或另一种资源的键值对，防止按相似结构兜底。
        var accessType = typeof(RuntimeUiListOrderService).GetNestedType("NativeListAccess", BindingFlags.NonPublic)!;
        foreach (var wrongList in new object[] { new System.Collections.Generic.List<int>(),
            new Il2CppSystem.Collections.Generic.List<int>(), new NativeBeverageList() })
        {
            var rejected = false;
            try
            {
                _ = Activator.CreateInstance(accessType, BindingFlags.NonPublic | BindingFlags.Instance, null,
                    new object[] { wrongList, "GameData.Core.Collections.Ingredient", true }, null);
            }
            catch (TargetInvocationException) { rejected = true; }
            check(rejected, "错误原生列表/资源元素类型未拒绝");
        }

        void Invoke(string name, params object[] arguments)
        {
            var method = typeof(RuntimeUiListOrderService).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new MissingMethodException(name);
            method.Invoke(null, arguments);
        }
        void Classify(WorkSceneCookingSelectionPannel panel, int source = 1, NativeIngredientList? lastOverride = null)
            => Invoke("AfterClassifyIngredients", source, panel.m_Ingredient_SeaFoodInstances,
                panel.m_Ingredient_MeatInstances, panel.m_Ingredient_VeggiesInsatance,
                lastOverride ?? panel.m_Ingredient_OtherInstances);
        void Bind(object panel, RuntimeUiListSurfaceKind kind, params RuntimeUiTargetSnapshot[] targets)
        {
            RuntimeUiPinningService.Panel = panel;
            RuntimeUiPinningService.Kind = kind;
            RuntimeUiPinningService.Target = new RuntimeUiTargetSetSnapshot(1, 1, targets);
            RuntimeUiPinningService.Current = RuntimeUiPinningService.AllowLease = true;
            RuntimeNightBusinessLifecycle.Snapshot = new(true, 1, Environment.CurrentManagedThreadId);
            RuntimeTargetRecipeVariantService.Claims.Clear();
            RuntimeTargetRecipeVariantService.Accept = true;
        }

        // 首次打开的UpdateAllVisual原生内联分类：不调用UpdateIngField也必须排序四类食材。
        var cooking = Cooking();
        var first = Target(0, 10, 90, 40, new[] { 11, 21, 31, 41 });
        var later = Target(5, 20, 91, 80, new[] { 12, 22, 32, 42 });
        Bind(cooking, RuntimeUiListSurfaceKind.Cooking, later, first);
        var beforeIngredients = Lists(cooking).SelectMany(list => list.Values).ToArray();
        Classify(cooking);
        check(log.Warnings.Count == 0, "首次食材排序发生原生读回/恢复错误：" + string.Join("; ", log.Warnings));
        foreach (var list in Lists(cooking))
        {
            var categoryBase = list.Values.Min(item => item.Key.id) / 10 * 10;
            check(list.Values.Select(item => item.Key.id).SequenceEqual(new[] { categoryBase + 1, categoryBase + 2, categoryBase + 9 }),
                "内联食材分类没有按订单顺序置顶");
            check(list.GenericWrites == 0 && list.BoxedWrites > 0 && !list.InvalidBoxPayload,
                "食材键值对仍经过不安全泛型setter，或未使用Object契约写入");
        }
        var afterIngredients = Lists(cooking).SelectMany(list => list.Values).ToArray();
        check(beforeIngredients.Length == afterIngredients.Length && beforeIngredients.All(item =>
            afterIngredients.Count(other => ReferenceEquals(item.Key, other.Key) && item.Value == other.Value) == 1),
            "食材桥修改了对象、分类成员或库存数");
        var writtenIngredients = Lists(cooking).Sum(list => list.Writes);
        Classify(cooking);
        check(Lists(cooking).Sum(list => list.Writes) == writtenIngredients, "相同目标重复分类后重复重排");

        // 全局分类钩子只允许当前面板InStorageOnly分支；一个参数列表身份不匹配即拒绝整组。
        foreach (var mode in new[] { "source", "foreign", "stale", "lease", "thread" })
        {
            var rejected = Cooking(); Bind(rejected, RuntimeUiListSurfaceKind.Cooking, later, first);
            if (mode == "stale") RuntimeUiPinningService.Current = false;
            if (mode == "lease") RuntimeUiPinningService.AllowLease = false;
            if (mode == "thread") RuntimeNightBusinessLifecycle.Snapshot = new(true, 1, Environment.CurrentManagedThreadId + 1);
            Classify(rejected, mode == "source" ? 0 : 1, mode == "foreign" ? Ingredients(49, 42, 41) : null);
            check(Lists(rejected).All(list => list.Writes == 0), mode + "无效食材上下文仍写入分类列表");
        }

        // 酒水前缀只允许Beverage模式及确切面板。数量为0/无限值时依然原样保留，不排序库存值。
        var storage = new WorkSceneStoragePannel { m_Beverages = Beverages(5, 80, 40, 3) };
        var beverageBefore = storage.m_Beverages.Values.ToArray();
        Bind(storage, RuntimeUiListSurfaceKind.Beverage, later, first);
        Invoke("AfterBeverageField", storage);
        check(storage.m_Beverages.Values.Select(item => item.Key.id).SequenceEqual(new[] { 40, 80, 5, 3 }), "酒水桥未按订单先后稳定置顶");
        check(beverageBefore.All(item => storage.m_Beverages.Values.Contains(item)), "酒水桥改变了库存对象或数量");
        check(storage.m_Beverages.GenericWrites == 0 && storage.m_Beverages.BoxedWrites > 0
            && !storage.m_Beverages.InvalidBoxPayload, "酒水键值对没有走安全Object setter");
        var beverageWrites = storage.m_Beverages.Writes;
        Invoke("AfterBeverageField", storage);
        check(storage.m_Beverages.Writes == beverageWrites, "稳定酒水刷新重复写列表");
        var wrongMode = new WorkSceneStoragePannel { openType = 0, m_Beverages = Beverages(80, 40) };
        Bind(wrongMode, RuntimeUiListSurfaceKind.Beverage, later, first);
        Invoke("AfterBeverageField", wrongMode);
        check(wrongMode.m_Beverages.Writes == 0, "食品仓库模式被酒水排序修改");
        var otherPanel = new WorkSceneStoragePannel { m_Beverages = Beverages(80, 40) };
        Invoke("AfterBeverageField", otherPanel);
        check(otherPanel.m_Beverages.Writes == 0, "无关仓库面板借用当前作用域排序");

        // 物品读失败、原生setter抛错或静默漏写均由正式桥进入同一个事务回退实现。
        foreach (var failure in new[] { "throw", "silent", "identity", "stale" })
        {
            var faulty = new WorkSceneStoragePannel { m_Beverages = Beverages(80, 5, 40) };
            Bind(faulty, RuntimeUiListSurfaceKind.Beverage, later, first);
            var original = faulty.m_Beverages.Values.ToArray();
            if (failure == "throw") faulty.m_Beverages.ThrowNextWrite = true;
            if (failure == "silent") faulty.m_Beverages.IgnoreNextWrite = true;
            if (failure == "identity") faulty.m_Beverages.Values[0].Key.Pointer = 0;
            if (failure == "stale") faulty.m_Beverages.Reading = () => RuntimeUiPinningService.Current = false;
            Invoke("AfterBeverageField", faulty);
            check(faulty.m_Beverages.Values.SequenceEqual(original), failure + "原生桥失败后未保留原顺序");
            check(faulty.m_Beverages.GenericWrites == 0 && !faulty.m_Beverages.InvalidBoxPayload,
                failure + "异常恢复再次经过了危险泛型setter");
            if (failure is "identity" or "stale")
                check(faulty.m_Beverages.Writes == 0, failure + "预写校验失败后仍写入列表");
        }

        // 绑定前料理映射以精确指针区分相同Recipe.id；模拟已验收的基础及加料声明边界。
        var recipePanel = Cooking();
        var basis = new Recipe { id = 90 }; var variant = new Recipe { id = 90 }; var otherRecipe = new Recipe { id = 91 };
        recipePanel.m_RecipeInstances.Values.AddRange(new[] { basis, otherRecipe, variant });
        var earlyVariant = Target(2, 5, 90, 40, new[] { 11, 12 }, variant: true);
        Bind(recipePanel, RuntimeUiListSurfaceKind.Cooking, later, first, earlyVariant);
        RuntimeTargetRecipeVariantService.Claims[basis.Pointer] = first.Claim;
        RuntimeTargetRecipeVariantService.Claims[variant.Pointer] = earlyVariant.Claim;
        RuntimeTargetRecipeVariantService.Claims[otherRecipe.Pointer] = later.Claim;
        Invoke("AfterRecipeField", recipePanel);
        check(recipePanel.m_RecipeInstances.Values.SequenceEqual(new[] { variant, basis, otherRecipe }),
            "料理桥将同ID的不同加料声明按基础ID合并");
        check(recipePanel.m_RecipeInstances.GenericWrites > 0 && recipePanel.m_RecipeInstances.BoxedWrites == 0,
            "引用型料理行不应误走装箱键值对写入路径");
        var staleRecipePanel = Cooking(); staleRecipePanel.m_RecipeInstances.Values.AddRange(new[] { otherRecipe, basis });
        Bind(staleRecipePanel, RuntimeUiListSurfaceKind.Cooking, later, first);
        RuntimeTargetRecipeVariantService.Accept = false;
        Invoke("AfterRecipeField", staleRecipePanel);
        check(staleRecipePanel.m_RecipeInstances.Writes == 0, "未验收/过期料理映射仍写入列表");

        var inactive = new WorkSceneStoragePannel { m_Beverages = Beverages(80, 40) };
        Bind(inactive, RuntimeUiListSurfaceKind.Beverage);
        Invoke("AfterBeverageField", inactive);
        check(inactive.m_Beverages.Writes == 0, "空辅助集修改原生酒水顺序");
        var oversized = new WorkSceneStoragePannel { m_Beverages = Beverages(Enumerable.Range(1, 513).ToArray()) };
        Bind(oversized, RuntimeUiListSurfaceKind.Beverage, later, first);
        Invoke("AfterBeverageField", oversized);
        check(oversized.m_Beverages.Writes == 0, "原生桥超出数量边界仍开始写入");
        check(log.Warnings.Count <= 4, "桥接异常没有限频日志");
        RuntimeUiPinningService.Panel = null;
        RuntimeUiPinningService.Target = RuntimeUiTargetSetSnapshot.Disabled;
    }

    private static RuntimeUiTargetSnapshot Target(int slot, long lifecycle, int recipe, int beverage, int[] ingredients,
        bool variant = false) => new(slot % 2 == 0 ? RuntimeUiTargetKind.Rare : RuntimeUiTargetKind.Normal,
        new(100, 150, 200), true, variant, false, false, false,
        (slot % 2 == 0 ? "R-" : "N-") + lifecycle, slot % 2 == 0 ? "" : "ptr:" + lifecycle.ToString("x"),
        lifecycle, slot, recipe, ingredients, variant ? ingredients.TakeLast(1) : Array.Empty<int>(),
        beverage, 1, "bridge:" + lifecycle, slot);

    private static WorkSceneCookingSelectionPannel Cooking() => new()
    {
        m_Ingredient_SeaFoodInstances = Ingredients(19, 12, 11),
        m_Ingredient_MeatInstances = Ingredients(29, 22, 21),
        m_Ingredient_VeggiesInsatance = Ingredients(39, 32, 31),
        m_Ingredient_OtherInstances = Ingredients(49, 42, 41),
    };
    private static NativeIngredientList[] Lists(WorkSceneCookingSelectionPannel panel) => new[]
    { panel.m_Ingredient_SeaFoodInstances, panel.m_Ingredient_MeatInstances, panel.m_Ingredient_VeggiesInsatance, panel.m_Ingredient_OtherInstances };
    private static NativeIngredientList Ingredients(params int[] ids)
    {
        var result = new NativeIngredientList();
        result.Values.AddRange(ids.Select(id => new Il2CppSystem.Collections.Generic.KeyValuePair<Ingredient, int>(new Ingredient { id = id }, id + 100)));
        return result;
    }
    private static NativeBeverageList Beverages(params int[] ids)
    {
        var result = new NativeBeverageList();
        result.Values.AddRange(ids.Select(id => new Il2CppSystem.Collections.Generic.KeyValuePair<Sellable, int>(new Sellable { id = id }, id == 40 ? int.MaxValue : id + 200)));
        return result;
    }
}
