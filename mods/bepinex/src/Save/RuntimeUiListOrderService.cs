using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;

namespace MystiaStewardCompanion.Save;

/// <summary>
/// 在游戏完成原生列表生成、控件读取列表之前，按点单生命周期稳定置顶当前辅助资源。
/// 只修改已核实面板的显示列表排列；库存、过滤结果、配方内容和原生选择行为均不改写。
/// </summary>
internal static class RuntimeUiListOrderService
{
    private const string CookingType = "NightScene.UI.CookingUtility.WorkSceneCookingSelectionPannel";
    private const string StorageType = "NightScene.UI.CookingUtility.WorkSceneStoragePannel";
    private const string AlbumType = "GameData.RunTime.Common.RunTimeAlbum";
    private const string RecipeType = "GameData.Core.Collections.Recipe";
    private const string IngredientType = "GameData.Core.Collections.Ingredient";
    private const string BeverageType = "GameData.Core.Collections.Sellable";
    private static readonly string[] IngredientFields = { "m_Ingredient_SeaFoodInstances", "m_Ingredient_MeatInstances",
        "m_Ingredient_VeggiesInsatance", "m_Ingredient_OtherInstances" };
    private static readonly HashSet<MethodInfo> Patched = new();
    private static Harmony? _harmony;
    private static ManualLogSource? _log;
    private static bool _armed;
    private static string _hookStatus = "not attached";
    private static string _lastFailure = "none";
    private static long _failures;
    private static int _warnings;
    [ThreadStatic] private static bool _applying;

    internal static string Status => $"{_hookStatus}; failures={Interlocked.Read(ref _failures)}; last={_lastFailure}";

    internal static void Attach(ManualLogSource log)
    {
        _log = log;
        try
        {
            _harmony ??= new Harmony("com.tyukki.mystia-steward-companion.runtime-ui-list-order");
            var cooking = RequireType(CookingType);
            var storage = RequireType(StorageType);
            var album = RequireType(AlbumType);
            var recipeMethod = RequireMethod(cooking, "UpdateRecipeField", false, 0);
            var beverageMethod = RequireMethod(storage, "UpdateBevField", false, 0);
            var ingredientMethod = RequireMethod(album, "ClassifyIngredientByType", true, 6);
            var parameters = ingredientMethod.GetParameters();
            // metadata 0x6003256 与 #783确认：来源枚举、四个Ingredient键值列表、可选过滤委托。
            // 不以参数个数相近的重载或其它分类入口替代，防止全局图鉴/白天列表被误改。
            if (!parameters[0].ParameterType.IsEnum || parameters[0].ParameterType.FullName != AlbumType + "+DataSource")
                throw new InvalidOperationException("ingredient classification source signature changed");
            for (var index = 1; index <= 4; index++)
                NativeListAccess.ValidateListType(parameters[index].ParameterType, IngredientType, pair: true);
            var filterType = parameters[5].ParameterType;
            if (!filterType.IsGenericType || filterType.GetGenericTypeDefinition().FullName != "Il2CppSystem.Func`2"
                || filterType.GetGenericArguments()[0] != parameters[1].ParameterType
                || !filterType.GetGenericArguments()[1].IsGenericType
                || filterType.GetGenericArguments()[1].GetGenericTypeDefinition().FullName != "Il2CppSystem.Collections.Generic.IEnumerable`1"
                || filterType.GetGenericArguments()[1].GetGenericArguments()[0] != parameters[1].ParameterType.GetGenericArguments()[0])
                throw new InvalidOperationException("ingredient classification filter signature changed");
            Patch(ingredientMethod, nameof(AfterClassifyIngredients));
            Patch(beverageMethod, nameof(AfterBeverageField));
            // 加料服务先完成合成行插入及验收，本服务再排序。显式after避免同Last优先级的偶然顺序。
            Patch(recipeMethod, nameof(AfterRecipeField));
            _armed = true;
            _hookStatus = "patched:3/3; order=lifecycle";
        }
        catch (Exception ex)
        {
            _armed = false;
            _hookStatus = "unavailable";
            NoteFailure(ex.GetBaseException().Message);
        }
    }

    private static void Patch(MethodInfo method, string callback)
    {
        if (Patched.Contains(method)) return;
        var postfix = typeof(RuntimeUiListOrderService).GetMethod(callback, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("list order postfix missing");
        _harmony!.Patch(method, postfix: new HarmonyMethod(postfix)
        {
            priority = Priority.Last,
            after = new[] { RuntimeTargetRecipeVariantService.HarmonyId },
        });
        Patched.Add(method);
    }

    private static MethodInfo RequireMethod(Type type, string name, bool isStatic, int count)
    {
        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
            .Where(method => method.DeclaringType == type && method.Name == name && method.IsStatic == isStatic
                && method.ReturnType == typeof(void) && !method.IsGenericMethod && method.GetParameters().Length == count).ToArray();
        return methods.Length == 1 ? methods[0] : throw new InvalidOperationException("exact list builder method unavailable: " + name);
    }

    private static Type RequireType(string name) => RuntimeReflectionUtility.FindType(name)
        ?? throw new InvalidOperationException("exact list type unavailable: " + name);

    /// <summary>
    /// UpdateAllVisual在当前原生版本内联了UpdateIngField，因此直接监听唯一分类方法返回点。
    /// 四列表须逐一属于当前料理刷新面板；其它面板即使调用相同全局方法也保持原样。
    /// </summary>
    private static void AfterClassifyIngredients(int __0, object __1, object __2, object __3, object __4)
    {
        if (__0 != 1) return;
        Execute(RuntimeUiListSurfaceKind.Cooking, null, (panel, target, isCurrent) =>
        {
            var lists = new[] { __1, __2, __3, __4 };
            for (var index = 0; index < lists.Length; index++)
                if (!IsFieldList(panel, IngredientFields[index], lists[index])) return;
            // 先建立四个精确适配器，类型不完整时不开始排序任何一个分类。
            var accesses = lists.Select(list => new NativeListAccess(list, IngredientType, pair: true)).ToArray();
            for (var index = 0; index < accesses.Length; index++)
            {
                var slot = index;
                var access = accesses[index];
                Reorder("ingredient:" + IngredientFields[slot], access,
                    item => target.GetOrderPriority(target.GetIngredientClaims(item.EntityId)),
                    () => isCurrent() && IsFieldList(panel, IngredientFields[slot], lists[slot]));
            }
        });
    }

    private static void AfterBeverageField(object __instance)
        => Execute(RuntimeUiListSurfaceKind.Beverage, __instance, (panel, target, isCurrent) =>
        {
            var list = ReadExactField(panel, "m_Beverages");
            var access = new NativeListAccess(list, BeverageType, pair: true);
            Reorder("beverage:m_Beverages", access, item => target.GetOrderPriority(target.GetBeverageClaims(item.EntityId)),
                () => isCurrent() && IsFieldList(panel, "m_Beverages", list));
        });

    private static void AfterRecipeField(object __instance)
        => Execute(RuntimeUiListSurfaceKind.Cooking, __instance, (panel, target, isCurrent) =>
        {
            var list = ReadExactField(panel, "m_RecipeInstances");
            var access = new NativeListAccess(list, RecipeType, pair: false);
            Reorder("recipe:m_RecipeInstances", access, item =>
            {
                // 合成行与基础行可能拥有同一Recipe.id，必须使用已验收的精确指针映射区分。
                if (!RuntimeTargetRecipeVariantService.TryGetRecipeListOrderClaims(panel, list, target,
                        item.EntityPointer, item.EntityId, out var claims))
                    throw new InvalidOperationException("recipe list row ownership is incomplete or stale");
                return target.GetOrderPriority(claims);
            }, () => isCurrent() && IsFieldList(panel, "m_RecipeInstances", list));
        });

    private static void Reorder(string surface, IRuntimeUiListOrderAccess access,
        Func<RuntimeUiListOrderItem, long> priority, Func<bool> current)
    {
        // 分类名与精确字段名使失败可定位到食材、酒水或配方，避免仅有笼统的原生项无效提示。
        if (!RuntimeUiListOrderMutation.TryReorder(access, priority, current, out var failure)) NoteFailure(surface + ": " + failure);
    }

    private static void Execute(RuntimeUiListSurfaceKind kind, object? expectedPanel,
        Action<object, RuntimeUiTargetSetSnapshot, Func<bool>> action)
    {
        if (!_armed || _applying) return;
        var lifecycle = RuntimeNightBusinessLifecycle.Snapshot;
        if (!lifecycle.IsActive || lifecycle.ThreadId != Environment.CurrentManagedThreadId) return;
        try
        {
            if (!RuntimeUiPinningService.TryGetListOrderContext(kind, expectedPanel, out var panel, out var target)
                // 空辅助集的原生刷新已经恢复默认排列，无须再反射读取或排序整张列表。
                || !target.Targets.Any(item => item.ListPinningEnabled)
                || !RuntimeUiPinningService.TryAcquireTargetPublicationLease(target.SessionGeneration, out var lease)) return;
            using (lease)
            {
                var panelPointer = Pointer(panel);
                bool IsCurrent() => panelPointer != 0
                    && RuntimeUiPinningService.IsSurfaceRefreshTargetCurrentOrDeferred(target)
                    && RuntimeUiPinningService.TryGetListOrderContext(kind, panel, out var currentPanel, out var currentTarget)
                    && Pointer(currentPanel) == panelPointer && ReferenceEquals(currentTarget, target);
                if (!IsCurrent()) return;
                _applying = true;
                try { action(panel, target, IsCurrent); }
                finally { _applying = false; }
            }
        }
        catch (Exception ex) { NoteFailure(kind + ": " + ex.GetBaseException().Message); }
    }

    private static object ReadExactField(object panel, string name)
    {
        var type = panel.GetType();
        var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (property == null || property.DeclaringType != type || property.GetIndexParameters().Length != 0
            || property.GetMethod == null || property.GetMethod.IsStatic)
            throw new InvalidOperationException("exact native list field missing: " + name);
        return property.GetValue(panel) ?? throw new InvalidOperationException("native list field is null: " + name);
    }

    private static bool IsFieldList(object panel, string field, object list)
    {
        var pointer = Pointer(list);
        return pointer != 0 && Pointer(ReadExactField(panel, field)) == pointer;
    }

    private static nint Pointer(object? value)
        => RuntimeReflectionUtility.TryReadNativeObjectPointer(value, out var pointer) ? pointer : 0;

    private static void NoteFailure(string reason)
    {
        // 只记录短诊断，不写配置和对象正文；持续异常限制日志，避免经营热循环放大磁盘开销。
        var bounded = reason.Replace('\r', ' ').Replace('\n', ' ');
        if (bounded.Length > 240) bounded = bounded[..240];
        _lastFailure = bounded;
        Interlocked.Increment(ref _failures);
        if (Interlocked.Increment(ref _warnings) <= 4) _log?.LogWarning("UI list order: " + bounded);
    }

    /// <summary>把#783已确认的List/KeyValuePair/实体字段映射为纯置换边界；没有名称别名或类型猜测。</summary>
    private sealed class NativeListAccess : IRuntimeUiListOrderAccess
    {
        private readonly object _list;
        private readonly Type _entityType;
        private readonly Type _elementType;
        private readonly PropertyInfo _count, _item;
        private readonly PropertyInfo? _key, _quantity;
        private readonly MethodInfo _write;

        internal NativeListAccess(object list, string entityType, bool pair)
        {
            _list = list;
            _entityType = RequireType(entityType);
            var type = list.GetType();
            ValidateListType(type, entityType, pair);
            if (Pointer(list) == 0) throw new InvalidOperationException("native list pointer missing");
            _elementType = type.GetGenericArguments()[0];
            _count = type.GetProperty("Count", BindingFlags.Public | BindingFlags.Instance)
                ?? throw new InvalidOperationException("native list Count missing");
            _item = type.GetProperty("Item", BindingFlags.Public | BindingFlags.Instance)
                ?? throw new InvalidOperationException("native list indexer missing");
            var arguments = _item.GetIndexParameters();
            if (_count.DeclaringType != type || _count.PropertyType != typeof(int)
                || _count.GetMethod == null || _count.GetMethod.IsStatic || _count.GetIndexParameters().Length != 0
                || _item.DeclaringType != type || _item.PropertyType != _elementType
                || _item.GetMethod == null || _item.GetMethod.IsStatic || _item.SetMethod == null
                || arguments.Length != 1 || arguments[0].ParameterType != typeof(int))
                throw new InvalidOperationException("native list access signature changed");
            if (pair)
            {
                _key = _elementType.GetProperty("Key") ?? throw new InvalidOperationException("native pair Key missing");
                _quantity = _elementType.GetProperty("Value") ?? throw new InvalidOperationException("native pair Value missing");
                if (_key.PropertyType != _entityType || _quantity.PropertyType != typeof(int))
                    throw new InvalidOperationException("native pair signature changed");
                _write = RequireBoxedPairWriter(type, _elementType);
            }
            else
            {
                // 配方是明确的原生引用类型；同样逐项核验setter，不能接受继承或近似签名入口。
                var writer = _item.SetMethod!;
                var writerArguments = writer.GetParameters();
                if (writer.DeclaringType != type || writer.IsStatic || writer.IsGenericMethod
                    || writer.ReturnType != typeof(void) || writerArguments.Length != 2
                    || writerArguments[0].ParameterType != typeof(int)
                    || writerArguments[1].ParameterType != _elementType || !_elementType.IsClass)
                    throw new InvalidOperationException("exact reference recipe setter signature changed");
                _write = writer;
            }
        }

        /// <summary>
        /// 使用已核实的非泛型IList写入装箱键值对，不能调用List&lt;T&gt;.set_Item。
        /// #783将KeyValuePair生成为继承Il2CppSystem.ValueType的托管class；泛型setter只判断
        /// CLR的IsValueType，误把装箱对象首地址作为原生结构体数据传入。原生列表会因此写入
        /// 对象头而非食材/酒水及数量，后续UI读取可触发进程级访问冲突，托管catch无法恢复。
        /// 实际interop的System_Collections_IList_set_Item(Int32, Il2CppSystem.Object)接受装箱引用，
        /// 原生共享入口RVA 0x1033CA0先校验元素类型、执行unbox，再调用精确泛型setter。
        /// 仅接受这一签名；缺失时在任何写入之前停止，不退回存在封送问题的泛型入口。
        /// </summary>
        private static MethodInfo RequireBoxedPairWriter(Type listType, Type elementType)
        {
            var objectType = RequireType("Il2CppSystem.Object");
            if (!elementType.IsClass || elementType.IsValueType
                || elementType.BaseType?.FullName != "Il2CppSystem.ValueType"
                || !objectType.IsAssignableFrom(elementType))
                throw new InvalidOperationException("native boxed pair wrapper signature changed");
            var writer = listType.GetMethod("System_Collections_IList_set_Item",
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly,
                binder: null, types: new[] { typeof(int), objectType }, modifiers: null);
            if (writer == null || writer.DeclaringType != listType || writer.IsStatic || writer.IsGenericMethod
                || writer.ReturnType != typeof(void))
                throw new InvalidOperationException("exact boxed IList pair setter unavailable");
            return writer;
        }

        internal static void ValidateListType(Type type, string entityType, bool pair)
        {
            if (!type.IsGenericType || type.GetGenericTypeDefinition().FullName != "Il2CppSystem.Collections.Generic.List`1")
                throw new InvalidOperationException("native list wrapper type changed");
            var item = type.GetGenericArguments()[0];
            if (pair)
            {
                if (!item.IsGenericType || item.GetGenericTypeDefinition().FullName != "Il2CppSystem.Collections.Generic.KeyValuePair`2"
                    || item.GetGenericArguments()[0].FullName != entityType || item.GetGenericArguments()[1] != typeof(int))
                    throw new InvalidOperationException("native key/value item type changed");
            }
            else if (item.FullName != entityType) throw new InvalidOperationException("native recipe item type changed");
        }

        public nint Identity => Pointer(_list);
        public int Count => _count.GetValue(_list) is int count ? count : throw new InvalidOperationException("native count is not Int32");
        public RuntimeUiListOrderItem Read(int index)
        {
            var value = _item.GetValue(_list, new object[] { index });
            if (value == null || !_elementType.IsInstanceOfType(value)) throw new InvalidOperationException("native list item is missing");
            var entity = _key == null ? value : _key.GetValue(value);
            if (entity == null || !_entityType.IsInstanceOfType(entity) || Pointer(entity) == 0
                || RuntimeReflectionUtility.GetMemberValue(entity, "id") is not int id || id < 0)
                throw new InvalidOperationException("native item type/pointer/id is invalid");
            var quantity = _quantity == null ? 0 : _quantity.GetValue(value) is int count
                ? count : throw new InvalidOperationException("native quantity is not Int32");
            return new RuntimeUiListOrderItem(value, Pointer(entity), id, quantity);
        }
        public void Write(int index, RuntimeUiListOrderItem item)
        {
            // 回写的是读取时保留的原始装箱值，不重建KeyValuePair、不改数量，也不直接写原生内存。
            // 配方为原生引用类型，仍使用其原有泛型setter；食材/酒水由上面的装箱入口正确拆箱。
            if (!_elementType.IsInstanceOfType(item.Value))
                throw new InvalidOperationException("native write item type changed");
            _write.Invoke(_list, new object[] { index, item.Value });
        }
    }
}
