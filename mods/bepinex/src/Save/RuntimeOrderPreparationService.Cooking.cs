using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MystiaStewardCompanion.LocalApi;

namespace MystiaStewardCompanion.Save;

internal static partial class RuntimeOrderPreparationService
{
    /// <summary>
    /// 按配方 ID 读取最终料理 ID。
    /// </summary>
    /// <remarks>
    /// 前端推荐以配方为主，而订单匹配和送达验证多以成品料理 ID 为准，需要在运行时数据库中做一次转换。
    /// </remarks>
    private static int ResolveFoodIdFromRecipeId(int recipeId)
    {
        if (recipeId < 0) return -1;
        var recipe = InvokeStatic(DataBaseCoreTypeName, "RefRecipe", new object?[] { recipeId });
        return recipe == null ? -1 : ToInt(ReadMember(recipe, "foodID"));
    }

    /// <summary>
    /// 按成品料理 ID 反查可用于开火的配方 ID。
    /// </summary>
    /// <remarks>
    /// 普客订单有时只暴露成品料理 ID。正常路径遍历游戏配方表；若表读取失败，则尝试“料理 ID 与配方 ID 相同”的常见情形。
    /// </remarks>
    private static int ResolveRecipeIdFromFoodId(int foodId)
    {
        if (foodId < 0) return -1;

        try
        {
            foreach (var recipeId in ReadIntEnumerable(InvokeStatic(DataBaseCoreTypeName, "GetAllRecipes", Array.Empty<object?>())))
            {
                var recipe = InvokeStatic(DataBaseCoreTypeName, "RefRecipe", new object?[] { recipeId });
                if (recipe == null) continue;
                if (ToInt(ReadMember(recipe, "foodID")) == foodId) return recipeId;
            }
        }
        catch
        {
            // 游戏多数基础料理的 food id 与 recipe id 一致，配方表不可枚举时用该规则做最后尝试。
        }

        try
        {
            var fallbackRecipe = InvokeStatic(DataBaseCoreTypeName, "RefRecipe", new object?[] { foodId });
            return fallbackRecipe == null ? -1 : foodId;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>
    /// 调用游戏烹饪系统开始制作料理。
    /// </summary>
    /// <param name="recipeId">目标配方 ID。</param>
    /// <param name="recipeName">用于用户提示和自动化日志的料理名称。</param>
    /// <param name="extraIngredientIds">推荐算法选择的额外加料材料 ID。</param>
    /// <param name="autoCollect">料理完成后是否登记出锅直送任务。参数名沿用内部烹饪流程命名，外部语义是直接送达。</param>
    /// <param name="collectionTarget">料理完成后的直接送达目标；未指定时仅按料理名称登记稀客目标。</param>
    /// <returns>开火结果以及原生 QTE 处理状态。</returns>
    /// <remarks>
    /// 此方法会扣除材料库存、写入厨具控制器、触发游戏开火回调，并可能登记后续出锅直送任务。
    /// 调用前必须已位于夜晚经营场景，且应运行在 Unity 主线程。
    /// </remarks>
    private static CookingStartResult TryStartCooking(
        int recipeId,
        string recipeName,
        IReadOnlyList<int> extraIngredientIds,
        bool autoCollect,
        CookingCollectionTarget? collectionTarget = null)
    {
        var recipe = InvokeStatic(DataBaseCoreTypeName, "RefRecipe", new object?[] { recipeId });
        if (recipe == null)
        {
            return CookingStartResult.Failed($"无法从游戏数据库读取料理配方：{recipeName} #{recipeId}。");
        }

        var baseFood = CreateFoodFromRecipe(recipe);
        if (baseFood == null)
        {
            return CookingStartResult.Failed($"无法从配方创建料理对象：{recipeName} #{recipeId}。");
        }

        var targetFoodId = ToInt(ReadMember(recipe, "foodID"));
        var target = collectionTarget ?? CookingCollectionTarget.ForRareOrder(new OrderPreparationRequest { RecipeName = recipeName }, targetFoodId);
        if (autoCollect && HasPendingCookingTarget(target, out var pendingMessage))
        {
            return CookingStartResult.Succeeded(pendingMessage, "", true);
        }

        var cookerSelection = TryGetCookerForOrder(baseFood, recipe);
        if (!cookerSelection.Ok || cookerSelection.CookController == null)
        {
            AppendAutomationLog("start-failed", collectionTarget, $"{recipeName}: {cookerSelection.Message}");
            return CookingStartResult.Failed(cookerSelection.Message);
        }

        var cookController = cookerSelection.CookController;
        var cooker = InvokeInstance(cookController, "get_Cooker", Array.Empty<object?>());
        if (cooker == null)
        {
            AppendAutomationLog("start-failed", collectionTarget, $"{recipeName}: controller has no cooker");
            return CookingStartResult.Failed("已找到可用厨具控制器，但无法读取厨具数据。");
        }

        var baseIngredientIds = ReadRecipeIngredientIds(recipe);
        if (baseIngredientIds.Length + extraIngredientIds.Count > MaxFoodIngredientCount)
        {
            AppendAutomationLog("start-failed", collectionTarget, $"{recipeName}: too many ingredients base={baseIngredientIds.Length}; extra={extraIngredientIds.Count}");
            return CookingStartResult.Failed($"料理材料超过游戏上限：基础 {baseIngredientIds.Length} 个，加料 {extraIngredientIds.Count} 个，最多 {MaxFoodIngredientCount} 个。");
        }

        var finalFood = CreateCookResult(recipe, extraIngredientIds, cooker) ?? baseFood;
        var ingredientIds = baseIngredientIds.Concat(extraIngredientIds).ToArray();
        if (!HasEnoughIngredients(ingredientIds, out var missingIngredientId))
        {
            AppendAutomationLog("start-failed", collectionTarget, $"{recipeName}: missing ingredient #{missingIngredientId}");
            return CookingStartResult.Failed($"材料不足，缺少材料 #{missingIngredientId}。");
        }

        if (ingredientIds.Length > 0)
        {
            foreach (var ingredientId in ingredientIds)
            {
                InvokeRuntimeStorageOut("IngredientOut", ingredientId);
            }
        }

        InvokeInstance(cookController, "SetCook", new object?[] { finalFood, recipe, true });
        var qteResult = TryHandleCookingQte();
        InvokeInstance(cookController, "StartCookCountDown", new object?[] { 1f, false });

        var cookSystem = GetSingletonInstance(CookSystemManagerTypeName);
        if (cookSystem != null)
        {
            TryInvokeInstance(cookSystem, "CallCookerStartCallback", new object?[] { finalFood, recipe });
        }

        if (autoCollect)
        {
            RegisterPendingCookingCollection(cookController, recipeName, target);
        }

        var extraText = extraIngredientIds.Count == 0 ? "不加料" : string.Join(",", extraIngredientIds);
        AppendAutomationLog("start-ok", collectionTarget, $"{recipeName}; cooker={DescribeCookController(cookController)}; autoCollect={autoCollect}; extra={extraText}");
        return CookingStartResult.Succeeded($"{recipeName} 已开始制作（配方 #{recipeId}，加料：{extraText}）。", qteResult.Message, qteResult.Skipped);
    }

    /// <summary>
    /// 尝试直接结算游戏原生 QTE，避免自动开火后弹出音游面板打断流程。
    /// </summary>
    private static CookingQteResult TryHandleCookingQte()
    {
        var completed = TryCompleteCookingQte(out var completeMessage);
        return completed
            ? CookingQteResult.Completed($"{completeMessage}；不会打开原生音游面板。")
            : CookingQteResult.Skip($"{completeMessage}；料理流程已继续。");
    }

    /// <summary>
    /// QTE 自动处理结果。
    /// </summary>
    private sealed class CookingQteResult
    {
        public string Message { get; private init; } = "";
        public bool Skipped { get; private init; }
        public static CookingQteResult Skip(string message)
        {
            return new CookingQteResult
            {
                Message = message,
                Skipped = true,
            };
        }

        public static CookingQteResult Completed(string message)
        {
            return new CookingQteResult
            {
                Message = message,
                Skipped = false,
            };
        }
    }

    /// <summary>
    /// 调用游戏 QTE 奖励管理器的成功回调。
    /// </summary>
    /// <param name="message">返回给订单准备步骤的说明文本。</param>
    /// <returns>成功调用原生结算入口时返回 <c>true</c>。</returns>
    private static bool TryCompleteCookingQte(out string message)
    {
        try
        {
            var manager = GetSingletonInstance(QteRewardManagerTypeName);
            if (manager == null)
            {
                message = "自动完成原生 QTE 失败：QTE 奖励管理器不可用。";
                return false;
            }

            InvokeInstance(manager, "OnQTESucceeded", new object?[] { -1, true });
            message = "已尝试自动完成原生 QTE 奖励结算。";
            return true;
        }
        catch (Exception ex)
        {
            message = $"自动完成原生 QTE 失败：{ex.GetBaseException().Message}";
            return false;
        }
    }

    /// <summary>
    /// 登记一个等待出锅后直接送达的烹饪任务。
    /// </summary>
    /// <remarks>
    /// 同一个厨具或同一个目标订单只保留一条待办，避免重复点击导致同一锅料理被多次直送。
    /// </remarks>
    private static void RegisterPendingCookingCollection(object cookController, string recipeName, CookingCollectionTarget target)
    {
        lock (PendingCookingLock)
        {
            var removed = PendingCookingCollections.RemoveAll(pending => ReferenceEquals(pending.CookController, cookController) || IsSameCookingCollectionTarget(pending.Target, target));
            PendingCookingCollections.Add(new PendingCookingCollection
            {
                CookController = cookController,
                RecipeName = recipeName,
                CreatedAtUtc = DateTime.UtcNow,
                Target = target,
            });
            AppendAutomationLog("pending-add", target, $"{recipeName}; cooker={DescribeCookController(cookController)}; replaced={removed}");
        }
    }

    /// <summary>
    /// 判断指定普客订单是否已有目标料理正在制作。
    /// </summary>
    /// <remarks>
    /// 优先用前端锁定的订单 key 匹配；缺失时退回运行时订单对象或桌号，保证重复轮询不会反复开火。
    /// </remarks>
    private static bool HasPendingNormalOrderCooking(string orderKey, object order, int deskCode, int foodId, int beverageId, out string message)
    {
        lock (PendingCookingLock)
        {
            foreach (var pending in PendingCookingCollections)
            {
                if (!IsMatchingPendingNormalOrderCooking(pending, orderKey, order, deskCode, foodId, beverageId)) continue;
                message = FormatPendingNormalOrderCookingMessage(pending, deskCode);
                return true;
            }
        }

        message = "";
        return false;
    }

    private static (bool Found, bool Delivered, string StepName, string Message) TryProcessPendingNormalOrderCooking(string orderKey, object order, int deskCode, int foodId, int beverageId)
    {
        lock (PendingCookingLock)
        {
            for (var i = PendingCookingCollections.Count - 1; i >= 0; i--)
            {
                var pending = PendingCookingCollections[i];
                if (!IsMatchingPendingNormalOrderCooking(pending, orderKey, order, deskCode, foodId, beverageId)) continue;

                var result = TryCollectCookedFood(pending);
                if (!string.IsNullOrWhiteSpace(result.Message))
                {
                    AppendAutomationLog("pending", pending.Target, result.Message);
                }

                if (result.Remove)
                {
                    AppendAutomationLog("pending-remove", pending.Target, $"{pending.RecipeName}; age={(DateTime.UtcNow - pending.CreatedAtUtc).TotalSeconds:F1}s");
                    PendingCookingCollections.RemoveAt(i);
                }

                var delivered = ReadOrderServedFood(order) != null
                    || result.Message.Contains("已直接送达普客订单", StringComparison.Ordinal);
                if (delivered)
                {
                    return (true, true, "普客送达料理", string.IsNullOrWhiteSpace(result.Message)
                        ? $"{pending.Target.FoodName} 已直接送达普客订单。"
                        : result.Message);
                }

                if (!string.IsNullOrWhiteSpace(result.Message))
                {
                    return (true, false, "普客送达料理", result.Message);
                }

                return (true, false, "普客开始料理", FormatPendingNormalOrderCookingMessage(pending, deskCode));
            }
        }

        return (false, false, "", "");
    }

    private static bool IsMatchingPendingNormalOrderCooking(PendingCookingCollection pending, string orderKey, object order, int deskCode, int foodId, int beverageId)
    {
        if (pending.Target.Kind != CookingCollectionTargetKind.NormalOrder) return false;
        if (pending.Target.FoodId != foodId) return false;
        if (pending.Target.BeverageId >= 0 && beverageId >= 0 && pending.Target.BeverageId != beverageId) return false;
        if (!string.IsNullOrWhiteSpace(orderKey) && !string.IsNullOrWhiteSpace(pending.Target.OrderKey))
        {
            if (string.Equals(orderKey, pending.Target.OrderKey, StringComparison.Ordinal)) return true;
        }

        if (pending.Target.Order != null && IsSameObject(pending.Target.Order, order)) return true;
        return pending.Target.DeskCode == deskCode;
    }

    private static string FormatPendingNormalOrderCookingMessage(PendingCookingCollection pending, int deskCode)
    {
        return pending.Target.DeskCode == deskCode
            ? $"桌 {deskCode + 1} 的目标料理 {pending.Target.FoodName} 已在制作中，等待完成后会自动直接送达。"
            : $"目标料理 {pending.Target.FoodName} 已在制作中，等待完成后会自动直接送达。";
    }

    private static bool HasPendingCookingTarget(CookingCollectionTarget target, out string message)
    {
        lock (PendingCookingLock)
        {
            foreach (var pending in PendingCookingCollections)
            {
                if (!IsSameCookingCollectionTarget(pending.Target, target)) continue;
                var pendingAge = DateTime.UtcNow - pending.CreatedAtUtc;
                if (pendingAge >= PendingCookingIdleTimeout) continue;

                message = $"目标料理 {pending.Target.FoodName} 已在制作中，等待完成后会自动直接送达。";
                return true;
            }
        }

        message = "";
        return false;
    }

    /// <summary>
    /// 判断两个出锅直送目标是否代表同一个业务目标。
    /// </summary>
    private static bool IsSameCookingCollectionTarget(CookingCollectionTarget left, CookingCollectionTarget right)
    {
        if (left.Kind != right.Kind) return false;
        if (left.Kind == CookingCollectionTargetKind.RareOrder)
        {
            if (left.FoodId != right.FoodId) return false;
            if (left.DeskCode >= 0 && right.DeskCode >= 0 && left.DeskCode != right.DeskCode) return false;
            if (left.GuestId.HasValue && right.GuestId.HasValue) return left.GuestId.Value == right.GuestId.Value;
            return !string.IsNullOrWhiteSpace(left.GuestName)
                && !string.IsNullOrWhiteSpace(right.GuestName)
                && string.Equals(left.GuestName, right.GuestName, StringComparison.Ordinal);
        }

        if (left.Kind != CookingCollectionTargetKind.NormalOrder) return false;
        if (left.FoodId != right.FoodId) return false;
        if (!string.IsNullOrWhiteSpace(left.OrderKey) && !string.IsNullOrWhiteSpace(right.OrderKey))
        {
            return string.Equals(left.OrderKey, right.OrderKey, StringComparison.Ordinal);
        }

        if (left.Order != null && right.Order != null && IsSameObject(left.Order, right.Order)) return true;
        return left.DeskCode >= 0 && left.DeskCode == right.DeskCode;
    }

    /// <summary>
    /// 尝试从一个待处理任务中读取成品并直接送达目标订单。
    /// </summary>
    /// <returns>
    /// <c>Remove</c> 表示该待办是否应从队列删除；<c>Message</c> 是需要展示或记录的处理结果。
    /// </returns>
    /// <remarks>
    /// 游戏完成料理后，CookController 的阶段和 Result 字段并不总是在同一帧稳定，因此这里同时看阶段、成品对象和等待时间。
    /// </remarks>
    private static (bool Remove, string Message) TryCollectCookedFood(PendingCookingCollection pending)
    {
        var phase = ToInt(TryInvokeInstanceValue(pending.CookController, "get_Phase"), -1);
        TryFinalizeCookControllerIfProgressComplete(pending.CookController, phase);
        phase = ToInt(TryInvokeInstanceValue(pending.CookController, "get_Phase"), phase);
        var cookedFood = ReadCookControllerResult(pending.CookController);
        var chosenRecipe = ReadCookControllerChosenRecipe(pending.CookController);
        var pendingAge = DateTime.UtcNow - pending.CreatedAtUtc;
        var isExpiredIdle = pendingAge >= PendingCookingIdleTimeout;

        if (cookedFood == null)
        {
            if (phase == 0 && chosenRecipe == null && isExpiredIdle)
            {
                return (true, $"{pending.RecipeName} 出锅直送任务已结束：厨具已空闲且未读取到成品。");
            }

            if (phase == 3 && isExpiredIdle)
            {
                return (true, $"{pending.RecipeName} 已完成，但长时间未读取到成品对象，已停止出锅直送。");
            }

            return (false, "");
        }

        if (phase == 0 && pendingAge < PendingCookingCollectGrace)
        {
            return (false, "");
        }

        if (phase != 3 && phase != 0)
        {
            return (false, "");
        }

        return TryDeliverPendingCookedFood(pending, cookedFood);
    }

    private static void TryFinalizeCookControllerIfProgressComplete(object cookController, int phase)
    {
        if (phase != 2) return;
        var progress = ToFloat(TryInvokeInstanceValue(cookController, "get_CookingProgress") ?? ReadMember(cookController, "CookingProgress"), 0f);
        if (progress < 0.999f) return;

        try
        {
            TryInvokeInstance(cookController, "FinishCooking", Array.Empty<object?>());
        }
        catch
        {
            // 料理已经进入可收取边界时，FinishCooking 只是对齐游戏自身状态；失败则保留下一轮重试。
        }
    }

    private static object? ReadCookControllerResult(object cookController)
    {
        try
        {
            return TryInvokeInstanceValue(cookController, "get_Result")
                ?? ReadMember(cookController, "Result");
        }
        catch
        {
            return null;
        }
    }

    private static object? ReadCookControllerChosenRecipe(object cookController)
    {
        try
        {
            return TryInvokeInstanceValue(cookController, "get_ChosenRecipe")
                ?? ReadMember(cookController, "ChosenRecipe");
        }
        catch
        {
            return null;
        }
    }

    private static bool TryRememberObject(object value, HashSet<nint> seen)
    {
        try
        {
            return seen.Add(ReadObjectPointer(value));
        }
        catch
        {
            return seen.Add(new IntPtr(RuntimeHelpers.GetHashCode(value)));
        }
    }

    private static int GetBeverageQuantity(int beverageId)
    {
        var value = InvokeStatic(RuntimeStorageTypeName, "GetBeverageCountById", new object?[] { beverageId });
        return ToInt(value);
    }

    /// <summary>
    /// 调用运行时库存扣减方法。
    /// </summary>
    /// <remarks>
    /// 部分游戏方法包含可选参数，除第一个物品 ID 外均使用类型默认值，保持与原生调用约定兼容。
    /// </remarks>
    private static void InvokeRuntimeStorageOut(string methodName, int itemId)
    {
        var type = FindType(RuntimeStorageTypeName)
            ?? throw new InvalidOperationException("RunTimeStorage type is not loaded.");
        var method = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .FirstOrDefault(candidate =>
            {
                if (!string.Equals(candidate.Name, methodName, StringComparison.Ordinal)) return false;
                var parameters = candidate.GetParameters();
                return parameters.Length >= 1
                    && parameters[0].ParameterType == typeof(int);
            })
            ?? throw new MissingMethodException(RuntimeStorageTypeName, methodName);
        var parameters = method.GetParameters();
        var args = new object?[parameters.Length];
        args[0] = itemId;
        for (var i = 1; i < parameters.Length; i++)
        {
            args[i] = GetDefaultValue(parameters[i].ParameterType);
        }

        method.Invoke(null, args);
    }

    private static object? CreateFoodFromRecipe(object recipe)
    {
        var foodId = ToInt(ReadMember(recipe, "foodID"));
        if (foodId < 0) return null;
        return InvokeStatic(DataBaseCoreTypeName, "AsNewFood", new object?[] { foodId });
    }

    /// <summary>
    /// 创建能体现额外加料结果的料理对象。
    /// </summary>
    /// <remarks>
    /// 推荐算法可能为满足 Tag 选择额外食材，必须通过游戏的 MatchedCookCombo 生成最终成品，
    /// 否则 UI 推荐与游戏实际料理效果会不一致。
    /// </remarks>
    private static object? CreateCookResult(object recipe, IReadOnlyList<int> extraIngredientIds, object cooker)
    {
        var combo = CreateMatchedCookCombo(recipe, extraIngredientIds);
        return combo == null ? null : InvokeInstance(combo, "GetResult", new[] { cooker });
    }

    private static object? CreateMatchedCookCombo(object recipe, IReadOnlyList<int> extraIngredientIds)
    {
        var type = FindType(MatchedCookComboTypeName);
        if (type == null) return null;

        foreach (var constructor in type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            var parameters = constructor.GetParameters();
            if (parameters.Length != 2) continue;
            if (!parameters[0].ParameterType.IsInstanceOfType(recipe)) continue;

            foreach (var modifiers in BuildIntArrayArgumentCandidates(parameters[1].ParameterType, extraIngredientIds))
            {
                var args = new object?[] { recipe, modifiers };
                if (!CanUseParameters(parameters, args)) continue;
                return constructor.Invoke(args);
            }
        }

        return null;
    }

    /// <summary>
    /// 选择可用于当前配方的厨具控制器。
    /// </summary>
    /// <remarks>
    /// 优先使用伙伴管理器入口，因为它包含游戏原生的订单厨具选择规则；失败后再扫描玩家厨具列表，
    /// 并排除本服务已登记为待直送的厨具，避免同一个灶台被并发复用。
    /// </remarks>
    private static (bool Ok, object? CookController, string Message) TryGetCookerForOrder(object baseFood, object recipe)
    {
        string? partnerMessage = null;
        var partnerManager = GetSingletonInstance(PartnerManagerTypeName);
        if (partnerManager != null)
        {
            var method = partnerManager.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(candidate =>
                {
                    if (!string.Equals(candidate.Name, "TryGetCookerForOrder", StringComparison.Ordinal)) return false;
                    var parameters = candidate.GetParameters();
                    return parameters.Length == 4
                        && !parameters[0].ParameterType.IsByRef
                        && parameters[1].ParameterType.IsByRef
                        && parameters[2].ParameterType.IsByRef;
                });
            if (method != null)
            {
                foreach (var canUsedCooker in BuildIntArrayArgumentCandidates(method.GetParameters()[3].ParameterType, Array.Empty<int>()))
                {
                    var args = new object?[] { baseFood, null, null, canUsedCooker };
                    try
                    {
                        var status = ToInt(method.Invoke(partnerManager, args));
                        var selectedController = args[1];
                        if (status == 3 && selectedController != null)
                        {
                            if (!IsCookControllerReserved(selectedController))
                            {
                                return (true, selectedController, "已通过伙伴厨具入口找到空闲可用厨具。");
                            }

                            partnerMessage = "伙伴厨具入口返回的厨具已有待直送任务。";
                            break;
                        }

                        partnerMessage = status switch
                        {
                            0 => "伙伴厨具入口未返回空闲厨具。",
                            1 => "伙伴厨具入口判断当前经营环境无法制作该料理。",
                            2 => "伙伴厨具入口未匹配到该料理的可用配方。",
                            _ => $"伙伴厨具入口返回状态 {status}。",
                        };
                        break;
                    }
                    catch
                    {
                        partnerMessage = "伙伴厨具入口调用失败。";
                    }
                }
            }
            else
            {
                partnerMessage = "未找到伙伴厨具入口 TryGetCookerForOrder。";
            }
        }
        else
        {
            partnerMessage = "当前经营伙伴管理器不可用。";
        }

        var cookSystemResult = TryGetCookerFromCookSystem(recipe);
        if (cookSystemResult.Ok)
        {
            return cookSystemResult;
        }

        return (false, null, $"{cookSystemResult.Message}（{partnerMessage}）");
    }

    /// <summary>
    /// 从厨具系统中扫描空闲且支持配方类型的厨具。
    /// </summary>
    private static (bool Ok, object? CookController, string Message) TryGetCookerFromCookSystem(object recipe)
    {
        var cookSystem = GetSingletonInstance(CookSystemManagerTypeName);
        if (cookSystem == null)
        {
            return (false, null, "当前厨具管理器不可用，请确认已进入夜晚经营页面。");
        }

        var controllers = InvokeInstance(cookSystem, "get_AllCookerControllers", Array.Empty<object?>());
        var recipeCookerType = ToInt(ReadMember(recipe, "cookerType"));
        var totalCount = 0;
        var openCount = 0;
        var matchingCount = 0;

        foreach (var cookController in ReadObjectEnumerable(controllers))
        {
            totalCount++;
            if (IsCookControllerReserved(cookController))
            {
                continue;
            }

            if (!ReadBool(InvokeInstance(cookController, "get_CouldCookerOpen", Array.Empty<object?>())))
            {
                continue;
            }

            openCount++;
            var cooker = InvokeInstance(cookController, "get_Cooker", Array.Empty<object?>());
            if (cooker == null || !CookerSupportsRecipe(cooker, recipeCookerType))
            {
                continue;
            }

            matchingCount++;
            return (true, cookController, $"已通过玩家厨具列表找到空闲可用厨具（共 {totalCount} 个，空闲 {openCount} 个）。");
        }

        if (totalCount == 0)
        {
            return (false, null, "当前没有读取到任何厨具。");
        }

        if (openCount == 0)
        {
            return (false, null, $"当前没有空闲厨具（读取到 {totalCount} 个厨具）。");
        }

        return (false, null, $"当前有 {openCount} 个空闲厨具，但没有符合配方厨具类型 {recipeCookerType} 的厨具。");
    }

    private static bool IsCookControllerReserved(object cookController)
    {
        lock (PendingCookingLock)
        {
            return PendingCookingCollections.Any(pending =>
                ReferenceEquals(pending.CookController, cookController)
                || IsSameObject(pending.CookController, cookController));
        }
    }

    private static bool CookerSupportsRecipe(object cooker, int recipeCookerType)
    {
        var cookerTypes = InvokeInstance(cooker, "get_AllAvailableCookerType", Array.Empty<object?>());
        return ReadIntEnumerable(cookerTypes).Contains(recipeCookerType);
    }

    private static string DescribeCookController(object cookController)
    {
        try
        {
            var cooker = TryInvokeInstanceValue(cookController, "get_Cooker");
            var cookerId = cooker == null ? -1 : ToInt(ReadMember(cooker, "id") ?? ReadMember(cooker, "Id"), -1);
            var pointer = (long)ReadObjectPointer(cookController);
            return cookerId >= 0 ? $"#{cookerId}@0x{pointer:X}" : $"0x{pointer:X}";
        }
        catch
        {
            return "unknown";
        }
    }

    private static void TryClearCookController(object cookController, object cookedFood)
    {
        try
        {
            WriteMember(cookController, "LastResult", cookedFood);
            WriteMember(cookController, "Result", null);
            WriteMember(cookController, "ChosenRecipe", null);

            var phaseProperty = cookController.GetType().GetProperty("Phase", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var phaseType = phaseProperty?.PropertyType
                ?? cookController.GetType().GetField("<Phase>k__BackingField", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.FieldType;
            var idleValue = phaseType?.IsEnum == true ? Enum.ToObject(phaseType, 0) : 0;
            WriteMember(cookController, "Phase", idleValue);
        }
        catch
        {
            // 送达已经成功时，兜底清理失败不应影响订单结果。
        }
    }

    /// <summary>
    /// 读取配方基础材料列表。
    /// </summary>
    /// <remarks>
    /// 重复材料必须按原始数组保留，因为游戏材料上限和库存扣减都按槽位计算。
    /// </remarks>
    private static int[] ReadRecipeIngredientIds(object recipe)
    {
        var ingredients = ReadMember(recipe, "ingredients");
        return ReadIntEnumerable(ingredients).ToArray();
    }

    /// <summary>
    /// 检查库存是否足以扣除基础材料和额外加料。
    /// </summary>
    /// <remarks>
    /// 相同材料会先聚合数量再比较库存，避免重复材料配方被误判为只需一份材料。
    /// </remarks>
    private static bool HasEnoughIngredients(IEnumerable<int> ingredientIds, out int missingIngredientId)
    {
        var required = ingredientIds
            .Where(id => id >= 0)
            .GroupBy(id => id)
            .ToDictionary(group => group.Key, group => group.Count());

        foreach (var (ingredientId, count) in required)
        {
            var current = GetIngredientQuantity(ingredientId);
            if (current >= 0 && current < count)
            {
                missingIngredientId = ingredientId;
                return false;
            }
        }

        missingIngredientId = -1;
        return true;
    }

    private static int GetIngredientQuantity(int ingredientId)
    {
        var value = InvokeStatic(RuntimeStorageTypeName, "GetIngredientCountById", new object?[] { ingredientId });
        return ToInt(value);
    }

    private static IEnumerable<object> BuildIntArrayArgumentCandidates(Type parameterType, IReadOnlyList<int> ids)
    {
        if (parameterType.IsArray && parameterType.GetElementType() == typeof(int))
        {
            yield return ids.ToArray();
            yield break;
        }

        if (parameterType == typeof(Il2CppStructArray<int>) || parameterType.FullName?.Contains("Il2CppStructArray") == true)
        {
            yield return BuildIl2CppIntArray(ids);
            yield break;
        }

        if (typeof(IEnumerable).IsAssignableFrom(parameterType)
            || parameterType.FullName?.Contains("IEnumerable", StringComparison.Ordinal) == true)
        {
            yield return ids.ToArray();
            yield return BuildIl2CppIntArray(ids);
        }
    }

    private static Il2CppStructArray<int> BuildIl2CppIntArray(IReadOnlyList<int> ids)
    {
        var array = new Il2CppStructArray<int>(ids.Count);
        for (var i = 0; i < ids.Count; i++)
        {
            array[i] = ids[i];
        }

        return array;
    }
}
