using System.Reflection;

namespace MystiaStewardCompanion.Save;

internal static partial class RuntimeOrderPreparationService
{
    private const string DarkCuisineProductionKind = "dark-cuisine";
    private const string DarkCuisineEventManagerTypeName = "NightScene.EventUtility.EventManager";

    /// <summary>
    /// 缺省 FoodId=-1 仍表示未指定目标。只有额外种类明确的请求才能表达黑暗料理；
    /// 这里仅保留请求意图，真正权限还须由下方当前任务信号与精确订单生命周期共同确认。
    /// </summary>
    private static bool RequestsDarkCuisine(LocalApi.OrderPreparationRequest request)
        => request.FoodId == -1 && request.FoodProductionKind == DarkCuisineProductionKind;

    private static bool IsDarkCuisineTarget(CookingCollectionTarget target)
        => target.Kind == CookingCollectionTargetKind.RareOrder && target.FoodId == -1
            && target.FoodProductionKind == DarkCuisineProductionKind;

    /// <summary>
    /// 开锅和送达都复核当前任务代际，防止旧推荐、缺失字段或客户端伪造的负 ID 触发特殊料理。
    /// 外层已按 trace/lifecycle 绑定原生订单，本层再要求同一经营、任务代际及唯一目标信号。
    /// </summary>
    private static bool TryValidateDarkCuisineTaskTarget(CookingCollectionTarget target, out string diagnostic)
    {
        diagnostic = "";
        if (!IsDarkCuisineTarget(target) || target.OrderBinding is not { } binding
            || target.GuestId is not >= 0 || target.RuntimeGuestId is null || target.MissionGeneration <= 0)
        {
            diagnostic = "黑暗料理目标缺少明确的任务与订单身份。";
            return false;
        }
        var mission = RuntimeMissionDiagnosticCapture.Snapshot();
        var signals = RuntimeServeInWorkMissionDiagnosticCapture.Snapshot();
        if (!mission.RuntimeAvailable || mission.Phase != RuntimeMissionDiagnosticPhase.Ready
            || mission.Generation != target.MissionGeneration || mission.DefinitionFailureCount != 0
            || !signals.HookAttached || signals.MissionGeneration != target.MissionGeneration
            || signals.BusinessGeneration != binding.BusinessGeneration || signals.NightPhase != "Active")
        {
            diagnostic = "黑暗料理任务状态已变化，等待最新任务与订单信息。";
            return false;
        }
        var matching = signals.Signals.Where(s => s.CanonicalGuestId == target.GuestId
            && s.RawGuestId == target.RuntimeGuestId).ToArray();
        if (matching.Length != 1 || matching[0].FoodId != -1
            || matching[0].MissionGeneration != target.MissionGeneration
            || matching[0].BusinessGeneration != binding.BusinessGeneration)
        {
            diagnostic = "当前稀客没有唯一、有效的黑暗料理任务目标。";
            return false;
        }
        return true;
    }

    /// <summary>
    /// FinishCooking(0x525340) 在无 Medicine_Positive(110) 时检测 Modifier 材料 Tags
    /// 与当前成品 banTags 的交集（0x53B0F0、0x53B1D0），再由原生流程生成 Food -1。
    /// 这里只验证上述前提，不直接创建黑暗料理，不跳过正常扣料/QTE/计时/出锅事务。
    /// GetOverrideFood 会使用随机数，不能调用它做只读预测；只在覆盖效果集合确认为空时自动生产。
    /// </summary>
    private static bool TryValidateDarkCuisineProduction(
        CookingCollectionTarget target, object recipe, object initialFood, out string diagnostic)
    {
        diagnostic = "";
        if (target.FoodProductionKind.Length == 0) return target.FoodId >= 0;
        if (!TryValidateDarkCuisineTaskTarget(target, out diagnostic)) return false;
        try
        {
            if (ReadExactMember(recipe, "foodID") is not int baseFoodId || baseFoodId < 0
                || !TryReadCookControllerFoodResultIdentity(initialFood, "dark-cuisine-base", out var identity, out diagnostic)
                || identity.FoodId != baseFoodId)
            {
                diagnostic = "黑暗料理基底配方与初始成品不一致，尚未扣料。";
                return false;
            }
            if (!RuntimeConcreteCollectionReader.TryReadIntArray(ReadExactMember(initialFood, "Modifier"), out var modifiers, out _)
                || !modifiers.SequenceEqual(target.ExtraIngredientIds)
                || !RuntimeConcreteCollectionReader.TryReadIntArray(ReadExactMember(initialFood, "banTags"), out var banned, out _))
            {
                diagnostic = "无法精确确认黑暗料理的加料与禁忌标签，尚未扣料。";
                return false;
            }
            var forbidden = false;
            foreach (var ingredientId in modifiers)
            {
                var ingredient = InvokeStatic(DataBaseCoreTypeName, "RefIngredient", new object?[] { ingredientId });
                if (ingredient == null || ReadExactMember(ingredient, "id") is not int actualId || actualId != ingredientId
                    || !RuntimeConcreteCollectionReader.TryReadIntArray(ReadExactMember(ingredient, "Tags"), out var tags, out _))
                {
                    diagnostic = "无法精确读取黑暗料理加料的原生标签，尚未扣料。";
                    return false;
                }
                forbidden |= tags.Any(banned.Contains);
            }
            if (!forbidden)
            {
                diagnostic = "当前加料不触发游戏原生禁忌效果，尚未扣料。";
                return false;
            }
            var manager = GetSingletonInstance(DarkCuisineEventManagerTypeName);
            var buffType = FindType(DarkCuisineEventManagerTypeName + "+BuffType");
            var medicine = buffType?.GetField("Medicine_Positive", BindingFlags.Public | BindingFlags.Static);
            if (manager == null || buffType == null || !buffType.IsEnum || Enum.GetUnderlyingType(buffType) != typeof(int)
                || medicine == null || !medicine.IsLiteral || medicine.GetRawConstantValue() is not int buffId || buffId != 110)
            {
                diagnostic = "游戏烹饪效果信息暂不可验证，黑暗料理等待人工处理。";
                return false;
            }
            var method = InvocationMetadata.FindExactMethod(manager.GetType(), "CheckTimedBuffExists",
                BindingFlags.Public | BindingFlags.Instance, new[] { buffType });
            if (method?.ReturnType != typeof(bool)
                || method.Invoke(manager, new[] { Enum.ToObject(buffType, buffId) }) is not bool protection)
            {
                diagnostic = "无法确认防止黑暗料理的效果状态，尚未扣料。";
                return false;
            }
            if (protection)
            {
                diagnostic = "梅蒂欣的料理保护效果生效中，暂不能用禁忌加料制作黑暗料理。";
                return false;
            }
            if (!RuntimeConcreteCollectionReader.TryReadList(ReadExactMember(manager, "registeredOverrideFoodBuff"), out var overrides, out _)
                || overrides.Count != 0)
            {
                diagnostic = "当前存在或无法确认成品替换效果，黑暗料理暂不自动开锅。";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            diagnostic = "黑暗料理生产条件无法确认，尚未扣料：" + ex.GetBaseException().Message;
            return false;
        }
    }
}