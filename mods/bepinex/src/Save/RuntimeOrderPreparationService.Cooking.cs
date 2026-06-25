using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MystiaStewardCompanion.LocalApi;

namespace MystiaStewardCompanion.Save;

internal static partial class RuntimeOrderPreparationService
{
    private static int ResolveFoodIdFromRecipeId(int recipeId)
    {
        if (recipeId < 0) return -1;
        var recipe = InvokeStatic(DataBaseCoreTypeName, "RefRecipe", new object?[] { recipeId });
        return recipe == null ? -1 : ToInt(ReadMember(recipe, "foodID"));
    }

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
            // Fall back to the common case where food id and recipe id are identical.
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
        var target = collectionTarget ?? CookingCollectionTarget.ForTrayFood(targetFoodId, recipeName);
        if (autoCollect && target.Kind == CookingCollectionTargetKind.Tray && target.FoodId >= 0 && HasPendingTrayCooking(target.FoodId, out var pendingTrayMessage))
        {
            return CookingStartResult.Succeeded(pendingTrayMessage, "", true);
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

    private static CookingQteResult TryHandleCookingQte()
    {
        var completed = TryCompleteCookingQte(out var completeMessage);
        return completed
            ? CookingQteResult.Completed($"{completeMessage}；不会打开原生音游面板。")
            : CookingQteResult.Skip($"{completeMessage}；料理流程已继续。");
    }

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

    private static bool HasPendingNormalOrderCooking(string orderKey, object order, int deskCode, int foodId, out string message)
    {
        lock (PendingCookingLock)
        {
            foreach (var pending in PendingCookingCollections)
            {
                if (pending.Target.Kind != CookingCollectionTargetKind.NormalOrder) continue;
                if (pending.Target.FoodId != foodId) continue;
                if (!string.IsNullOrWhiteSpace(orderKey) && !string.IsNullOrWhiteSpace(pending.Target.OrderKey))
                {
                    if (!string.Equals(orderKey, pending.Target.OrderKey, StringComparison.Ordinal)) continue;
                    message = $"目标料理 {pending.Target.FoodName} 已在制作中，等待完成后会自动收至普客保温箱。";
                    return true;
                }

                if (pending.Target.Order != null && IsSameObject(pending.Target.Order, order))
                {
                    message = $"目标料理 {pending.Target.FoodName} 已在制作中，等待完成后会自动收至普客保温箱。";
                    return true;
                }

                if (pending.Target.DeskCode == deskCode)
                {
                    message = $"桌 {deskCode + 1} 的目标料理 {pending.Target.FoodName} 已在制作中，等待完成后会自动收至普客保温箱。";
                    return true;
                }
            }
        }

        message = "";
        return false;
    }

    private static bool HasPendingTrayCooking(int foodId, out string message)
    {
        lock (PendingCookingLock)
        {
            foreach (var pending in PendingCookingCollections)
            {
                if (pending.Target.Kind != CookingCollectionTargetKind.Tray) continue;
                if (pending.Target.FoodId != foodId) continue;
                var pendingAge = DateTime.UtcNow - pending.CreatedAtUtc;
                if (pendingAge >= PendingCookingIdleTimeout) continue;

                message = $"目标料理 {pending.Target.FoodName} 已在制作中，等待完成后会自动收入送餐盘。";
                return true;
            }
        }

        message = "";
        return false;
    }

    private static bool IsSameCookingCollectionTarget(CookingCollectionTarget left, CookingCollectionTarget right)
    {
        if (left.Kind != right.Kind) return false;
        if (left.Kind == CookingCollectionTargetKind.Tray)
        {
            return left.FoodId >= 0 && left.FoodId == right.FoodId;
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

    private static (bool Remove, string Message) TryCollectCookedFood(PendingCookingCollection pending)
    {
        var phase = ToInt(TryInvokeInstanceValue(pending.CookController, "get_Phase"), -1);
        var cookedFood = ReadCookControllerResult(pending.CookController);
        var chosenRecipe = ReadCookControllerChosenRecipe(pending.CookController);
        var pendingAge = DateTime.UtcNow - pending.CreatedAtUtc;
        var isExpiredIdle = pendingAge >= PendingCookingIdleTimeout;

        if (cookedFood == null)
        {
            if (phase == 0 && chosenRecipe == null && isExpiredIdle)
            {
                return (true, $"{pending.RecipeName} 自动收取任务已结束：厨具已空闲且未读取到成品。");
            }

            if (phase == 3 && isExpiredIdle)
            {
                return (true, $"{pending.RecipeName} 已完成，但长时间未读取到成品对象，已停止自动收取。");
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

        if (pending.Target.Kind == CookingCollectionTargetKind.NormalOrder)
        {
            return TryCollectNormalOrderFood(pending, cookedFood);
        }

        var tray = GetSingletonInstance(IzakayaTrayTypeName);
        if (tray == null)
        {
            return (false, "");
        }

        var isFull = InvokeInstance(tray, "get_IsTrayFull", Array.Empty<object?>());
        if (ReadBool(isFull))
        {
            return (false, "");
        }

        if (TryExtractWithGameMethod(pending.CookController))
        {
            return (true, $"{pending.RecipeName} 已自动收入送餐盘。");
        }

        InvokeInstance(tray, "Receive", new[] { cookedFood });
        TryInvokeInstance(pending.CookController, "AfterPlayerExtract", Array.Empty<object?>());
        TryInvokeInstance(pending.CookController, "CloseCookingVisual", Array.Empty<object?>());
        TryClearCookController(pending.CookController, cookedFood);
        return (true, $"{pending.RecipeName} 已自动收入送餐盘。");
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

    private static bool TryExtractWithGameMethod(object cookController)
    {
        var method = cookController.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(candidate => string.Equals(candidate.Name, "Extract", StringComparison.Ordinal)
                && candidate.GetParameters().Length == 1);
        if (method == null) return false;

        var parameterType = method.GetParameters()[0].ParameterType;
        if (!typeof(Delegate).IsAssignableFrom(parameterType)) return false;

        try
        {
            var callback = CreateTrayReceiveDelegate(parameterType);
            if (callback == null) return false;
            method.Invoke(cookController, new object?[] { callback });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int GetBeverageQuantity(int beverageId)
    {
        var value = InvokeStatic(RuntimeStorageTypeName, "GetBeverageCountById", new object?[] { beverageId });
        return ToInt(value);
    }

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

                            partnerMessage = "伙伴厨具入口返回的厨具已有待收取任务。";
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
            // The preferred Extract path performs cleanup. This fallback should not fail the collection.
        }
    }

    private static int[] ReadRecipeIngredientIds(object recipe)
    {
        var ingredients = ReadMember(recipe, "ingredients");
        return ReadIntEnumerable(ingredients).ToArray();
    }

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
