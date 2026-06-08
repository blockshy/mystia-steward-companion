using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MystiaStewardCompanion.LocalApi;

namespace MystiaStewardCompanion.Save;

internal static class RuntimeOrderPreparationService
{
    private const string DataBaseCoreTypeName = "GameData.Core.Collections.DataBaseCore";
    private const string IzakayaTrayTypeName = "GameData.RunTime.NightSceneUtility.IzakayaTray";
    private const string RuntimeStorageTypeName = "GameData.RunTime.Common.RunTimeStorage";
    private const string PartnerManagerTypeName = "NightScene.PartnerUtility.PartnerManager";
    private const string CookSystemManagerTypeName = "NightScene.CookingUtility.CookSystemManager";
    private const string GuestsManagerTypeName = "NightScene.GuestManagementUtility.GuestsManager";
    private const string MatchedCookComboTypeName = "NightScene.UI.CookingUtility.WorkSceneCookingSelectionPannel+MatchedCookCombo";
    private static readonly object PendingCookingLock = new();
    private static readonly List<PendingCookingCollection> PendingCookingCollections = new();

    public static OrderPreparationResult Prepare(OrderPreparationRequest request)
    {
        var result = new OrderPreparationResult
        {
            Order = new OrderPreparationOrder
            {
                DeskCode = request.DeskCode,
                GuestId = request.GuestId,
                GuestName = request.GuestName,
                FoodTag = request.FoodTag,
                BeverageTag = request.BeverageTag,
            },
            RecipeId = request.RecipeId,
            RecipeName = request.RecipeName,
            BeverageId = request.BeverageId,
            BeverageName = request.BeverageName,
        };

        if (request.FavoritesOnly)
        {
            if (request.AutoStartCooking && !request.RecipeFavorite)
            {
                return Fail(result, "收藏限定已开启，但当前订单没有匹配的收藏料理。");
            }

            if (request.AutoTakeBeverage && !request.BeverageFavorite)
            {
                return Fail(result, "收藏限定已开启，但当前订单没有匹配的收藏酒水。");
            }
        }

        result.Steps.Add(new OrderPreparationStep
        {
            Name = "选择订单",
            Ok = true,
            Message = $"桌 {request.DeskCode + 1} · {request.GuestName} · 料理 {request.FoodTag} · 酒水 {request.BeverageTag}",
        });

        if (request.AutoTakeBeverage)
        {
            if (request.BeverageId < 0)
            {
                AddFailure(result, "自动取酒", "没有可用的推荐酒水。");
                if (request.StopOnError) return Finish(result);
            }
            else
            {
                var beverageResult = TryTakeBeverageToTray(request.BeverageId, request.BeverageName);
                if (beverageResult.Ok)
                {
                    result.Steps.Add(new OrderPreparationStep
                    {
                        Name = "自动取酒",
                        Ok = true,
                        Message = beverageResult.Message,
                    });
                }
                else
                {
                    AddFailure(result, "自动取酒", beverageResult.Message);
                    if (request.StopOnError) return Finish(result);
                }
            }
        }
        else
        {
            AddSkipped(result, "自动取酒", "设置已关闭。");
        }

        if (request.AutoStartCooking)
        {
            if (request.RecipeId < 0)
            {
                AddFailure(result, "自动开始料理", "没有可用的推荐料理。");
                if (request.StopOnError) return Finish(result);
            }
            else
            {
                var cookingResult = TryStartCooking(request.RecipeId, request.RecipeName, request.ExtraIngredientIds, request.AutoCollectCooking);
                if (cookingResult.Ok)
                {
                    result.Steps.Add(new OrderPreparationStep
                    {
                        Name = "自动开始料理",
                        Ok = true,
                        Message = cookingResult.Message,
                    });
                }
                else
                {
                    AddFailure(result, "自动开始料理", cookingResult.Message);
                    if (request.StopOnError) return Finish(result);
                }
            }
        }
        else
        {
            AddSkipped(result, "自动开始料理", "设置已关闭。");
        }

        if (request.AutoCollectCooking)
        {
            AddSkipped(result, "自动收取料理", "料理完成后会自动尝试收入送餐盘。");
            if (request.StopOnError) return Finish(result);
        }
        else
        {
            AddSkipped(result, "自动收取料理", "设置已关闭。");
        }

        return Finish(result);
    }

    public static OrderPreparationResult CompleteFirst(OrderPreparationRequest request)
    {
        var result = new OrderPreparationResult
        {
            Order = new OrderPreparationOrder
            {
                DeskCode = request.DeskCode,
                GuestId = request.GuestId,
                GuestName = request.GuestName,
                FoodTag = request.FoodTag,
                BeverageTag = request.BeverageTag,
            },
            RecipeId = request.RecipeId,
            RecipeName = request.RecipeName,
            BeverageId = request.BeverageId,
            BeverageName = request.BeverageName,
        };

        result.Steps.Add(new OrderPreparationStep
        {
            Name = "选择订单",
            Ok = true,
            Message = $"桌 {request.DeskCode + 1} · {request.GuestName} · 料理 {request.FoodTag} · 酒水 {request.BeverageTag}",
        });

        if (request.RecipeId < 0)
        {
            AddFailure(result, "匹配料理", "当前第一笔订单没有可用的推荐料理。");
            return Finish(result);
        }

        if (request.BeverageId < 0)
        {
            AddFailure(result, "匹配酒水", "当前第一笔订单没有可用的推荐酒水。");
            return Finish(result);
        }

        var recipe = InvokeStatic(DataBaseCoreTypeName, "RefRecipe", new object?[] { request.RecipeId });
        if (recipe == null)
        {
            AddFailure(result, "匹配料理", $"无法从游戏数据库读取料理配方：{request.RecipeName} #{request.RecipeId}。");
            return Finish(result);
        }

        var expectedFoodId = ToInt(ReadMember(recipe, "foodID"));
        if (expectedFoodId < 0)
        {
            AddFailure(result, "匹配料理", $"配方 {request.RecipeName} 未读取到有效成品料理 ID。");
            return Finish(result);
        }

        var tray = GetSingletonInstance(IzakayaTrayTypeName);
        if (tray == null)
        {
            AddFailure(result, "匹配送餐盘", "当前送餐盘对象不可用，请确认已进入夜晚经营页面。");
            return Finish(result);
        }

        var trayItems = ReadTrayItems(tray).ToList();
        var food = trayItems.FirstOrDefault(item => IsSellable(item, sellableType: 0, expectedFoodId));
        if (food == null)
        {
            AddFailure(result, "匹配送餐盘料理", $"送餐盘中没有找到 {request.RecipeName} 的成品料理（料理 #{expectedFoodId}）。");
            return Finish(result);
        }

        var beverage = trayItems.FirstOrDefault(item => IsSellable(item, sellableType: 1, request.BeverageId));
        if (beverage == null)
        {
            AddFailure(result, "匹配送餐盘酒水", $"送餐盘中没有找到 {request.BeverageName}（酒水 #{request.BeverageId}）。");
            return Finish(result);
        }

        result.Steps.Add(new OrderPreparationStep
        {
            Name = "匹配送餐盘",
            Ok = true,
            Message = $"已找到料理 {request.RecipeName} 和酒水 {request.BeverageName}。",
        });

        var runtimeOrder = FindRuntimeOrder(request);
        if (runtimeOrder.Order == null || runtimeOrder.Controller == null || runtimeOrder.Manager == null)
        {
            AddFailure(result, "匹配运行时订单", "未找到当前第一笔稀客订单对象，可能订单已完成、客人已离场或经营状态刚刷新。");
            return Finish(result);
        }

        result.Steps.Add(new OrderPreparationStep
        {
            Name = "匹配运行时订单",
            Ok = true,
            Message = $"已匹配桌 {request.DeskCode + 1} · {request.GuestName} 的订单对象。",
        });

        WriteMember(runtimeOrder.Order, "ServFood", food);
        WriteMember(runtimeOrder.Order, "ServBeverage", beverage);
        if (!ReadBool(InvokeInstance(runtimeOrder.Order, "get_IsFullfilled", Array.Empty<object?>())))
        {
            WriteMember(runtimeOrder.Order, "ServFood", null);
            WriteMember(runtimeOrder.Order, "ServBeverage", null);
            AddFailure(result, "写入订单", "料理和酒水已匹配，但游戏判定订单未满足；本次未从送餐盘移除物品。");
            return Finish(result);
        }

        InvokeInstance(tray, "Deliver", new object?[] { food });
        InvokeInstance(tray, "Deliver", new object?[] { beverage });
        result.Steps.Add(new OrderPreparationStep
        {
            Name = "写入订单",
            Ok = true,
            Message = "已将送餐盘中的料理和酒水写入订单。",
        });

        InvokeInstance(runtimeOrder.Manager, "EvaluateOrder", new object?[] { runtimeOrder.Controller, false, null });
        result.Steps.Add(new OrderPreparationStep
        {
            Name = "触发上菜评价",
            Ok = true,
            Message = "已调用游戏评价流程完成当前订单。",
        });

        return Finish(result);
    }

    public static IReadOnlyList<string> ProcessPendingCookingCollections()
    {
        var messages = new List<string>();
        lock (PendingCookingLock)
        {
            for (var i = PendingCookingCollections.Count - 1; i >= 0; i--)
            {
                var pending = PendingCookingCollections[i];
                (bool Remove, string Message) result;
                try
                {
                    result = TryCollectCookedFood(pending);
                }
                catch (Exception ex)
                {
                    result = (true, $"{pending.RecipeName} 自动收取已停止：{ex.Message}");
                }

                if (!string.IsNullOrWhiteSpace(result.Message))
                {
                    messages.Add(result.Message);
                }

                if (result.Remove)
                {
                    PendingCookingCollections.RemoveAt(i);
                }
            }
        }

        return messages;
    }

    private static (bool Ok, string Message) TryTakeBeverageToTray(int beverageId, string beverageName)
    {
        var currentQuantity = GetBeverageQuantity(beverageId);
        if (currentQuantity == 0)
        {
            return (false, $"{beverageName} 当前库存为 0，无法放入送餐盘。");
        }

        var sellable = InvokeStatic(DataBaseCoreTypeName, "AsNewBeverage", new object?[] { beverageId });
        if (sellable == null)
        {
            return (false, $"无法从游戏数据库创建酒水对象：{beverageName} #{beverageId}。");
        }

        var tray = GetSingletonInstance(IzakayaTrayTypeName);
        if (tray == null)
        {
            return (false, "当前送餐盘对象不可用，请确认已进入夜晚经营页面。");
        }

        var isFull = InvokeInstance(tray, "get_IsTrayFull", Array.Empty<object?>());
        if (isFull is bool isTrayFull && isTrayFull)
        {
            return (false, "送餐盘已满，无法继续取酒。");
        }

        InvokeInstance(tray, "Receive", new[] { sellable });
        if (currentQuantity > 0)
        {
            InvokeRuntimeStorageOut("BeverageOut", beverageId);
        }

        var quantityText = currentQuantity < 0 ? "无限库存" : $"剩余 {Math.Max(0, currentQuantity - 1)}";
        return (true, $"{beverageName} 已放入送餐盘（{quantityText}）。");
    }

    private static (bool Ok, string Message) TryStartCooking(
        int recipeId,
        string recipeName,
        IReadOnlyList<int> extraIngredientIds,
        bool autoCollect)
    {
        var recipe = InvokeStatic(DataBaseCoreTypeName, "RefRecipe", new object?[] { recipeId });
        if (recipe == null)
        {
            return (false, $"无法从游戏数据库读取料理配方：{recipeName} #{recipeId}。");
        }

        var baseFood = CreateFoodFromRecipe(recipe);
        if (baseFood == null)
        {
            return (false, $"无法从配方创建料理对象：{recipeName} #{recipeId}。");
        }

        var cookerSelection = TryGetCookerForOrder(baseFood, recipe);
        if (!cookerSelection.Ok || cookerSelection.CookController == null)
        {
            return (false, cookerSelection.Message);
        }

        var cookController = cookerSelection.CookController;
        var cooker = InvokeInstance(cookController, "get_Cooker", Array.Empty<object?>());
        if (cooker == null)
        {
            return (false, "已找到可用厨具控制器，但无法读取厨具数据。");
        }

        var finalFood = CreateCookResult(recipe, extraIngredientIds, cooker) ?? baseFood;
        var ingredientIds = ReadRecipeIngredientIds(recipe).Concat(extraIngredientIds).ToArray();
        if (!HasEnoughIngredients(ingredientIds, out var missingIngredientId))
        {
            return (false, $"材料不足，缺少材料 #{missingIngredientId}。");
        }

        if (ingredientIds.Length > 0)
        {
            foreach (var ingredientId in ingredientIds)
            {
                InvokeRuntimeStorageOut("IngredientOut", ingredientId);
            }
        }

        InvokeInstance(cookController, "SetCook", new object?[] { finalFood, recipe, true });
        InvokeInstance(cookController, "StartCookCountDown", new object?[] { 1f, false });

        var cookSystem = GetSingletonInstance(CookSystemManagerTypeName);
        if (cookSystem != null)
        {
            TryInvokeInstance(cookSystem, "CallCookerStartCallback", new object?[] { finalFood, recipe });
        }

        if (autoCollect)
        {
            RegisterPendingCookingCollection(cookController, recipeName);
        }

        var extraText = extraIngredientIds.Count == 0 ? "不加料" : string.Join(",", extraIngredientIds);
        return (true, $"{recipeName} 已开始制作（配方 #{recipeId}，加料：{extraText}）。");
    }

    private static void RegisterPendingCookingCollection(object cookController, string recipeName)
    {
        lock (PendingCookingLock)
        {
            PendingCookingCollections.RemoveAll(pending => ReferenceEquals(pending.CookController, cookController));
            PendingCookingCollections.Add(new PendingCookingCollection
            {
                CookController = cookController,
                RecipeName = recipeName,
                CreatedAtUtc = DateTime.UtcNow,
            });
        }
    }

    private static (bool Remove, string Message) TryCollectCookedFood(PendingCookingCollection pending)
    {
        var phase = ToInt(InvokeInstance(pending.CookController, "get_Phase", Array.Empty<object?>()));
        if (phase == 0)
        {
            return (true, "");
        }

        if (phase != 3)
        {
            return (false, "");
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

        var cookedFood = InvokeInstance(pending.CookController, "get_Result", Array.Empty<object?>());
        if (cookedFood == null)
        {
            return (true, $"{pending.RecipeName} 已完成，但未读取到成品对象，已停止自动收取。");
        }

        InvokeInstance(tray, "Receive", new[] { cookedFood });
        TryInvokeInstance(pending.CookController, "AfterPlayerExtract", Array.Empty<object?>());
        TryInvokeInstance(pending.CookController, "CloseCookingVisual", Array.Empty<object?>());
        TryClearCookController(pending.CookController, cookedFood);
        return (true, $"{pending.RecipeName} 已自动收入送餐盘。");
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

    private static Delegate? CreateTrayReceiveDelegate(Type delegateType)
    {
        var invoke = delegateType.GetMethod("Invoke");
        var parameter = invoke?.GetParameters().FirstOrDefault();
        if (parameter == null) return null;

        var method = typeof(RuntimeOrderPreparationService)
            .GetMethod(nameof(ReceiveCookedFoodGeneric), BindingFlags.NonPublic | BindingFlags.Static)
            ?.MakeGenericMethod(parameter.ParameterType);
        return method == null ? null : Delegate.CreateDelegate(delegateType, method);
    }

    private static void ReceiveCookedFoodGeneric<T>(T sellable)
    {
        if (sellable == null) return;
        var tray = GetSingletonInstance(IzakayaTrayTypeName);
        if (tray == null) return;
        InvokeInstance(tray, "Receive", new object?[] { sellable });
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
                        if (status == 3 && args[1] != null)
                        {
                            return (true, args[1], "已通过伙伴厨具入口找到空闲可用厨具。");
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

    private static bool CookerSupportsRecipe(object cooker, int recipeCookerType)
    {
        var cookerTypes = InvokeInstance(cooker, "get_AllAvailableCookerType", Array.Empty<object?>());
        return ReadIntEnumerable(cookerTypes).Contains(recipeCookerType);
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

        if (typeof(IEnumerable).IsAssignableFrom(parameterType))
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

    private static IEnumerable<object> ReadTrayItems(object tray)
    {
        return ReadObjectEnumerable(InvokeInstance(tray, "get_Tray", Array.Empty<object?>()));
    }

    private static bool IsSellable(object item, int sellableType, int id)
    {
        return ReadSellableType(item) == sellableType && ReadSellableId(item) == id;
    }

    private static int ReadSellableType(object item)
    {
        var value = TryInvokeInstanceValue(item, "get_Type") ?? ReadMember(item, "Type");
        return ToInt(value);
    }

    private static int ReadSellableId(object item)
    {
        var value = TryInvokeInstanceValue(item, "get_id")
            ?? TryInvokeInstanceValue(item, "get_Id")
            ?? ReadMember(item, "id")
            ?? ReadMember(item, "Id");
        return ToInt(value);
    }

    private static RuntimeOrderMatch FindRuntimeOrder(OrderPreparationRequest request)
    {
        var manager = GetSingletonInstance(GuestsManagerTypeName);
        if (manager == null) return new RuntimeOrderMatch();

        foreach (var controller in EnumerateGuestControllers(manager))
        {
            if (controller == null) continue;
            foreach (var order in EnumerateControllerOrders(controller))
            {
                try
                {
                    if (!IsMatchingSpecialOrder(order, controller, request)) continue;
                }
                catch
                {
                    continue;
                }

                return new RuntimeOrderMatch
                {
                    Manager = manager,
                    Controller = controller,
                    Order = order,
                };
            }
        }

        return new RuntimeOrderMatch();
    }

    private static IEnumerable<object> EnumerateGuestControllers(object manager)
    {
        var seen = new HashSet<nint>();
        foreach (var name in new[]
                 {
                     "AllPresentedGuestGroupController",
                     "AllGuestInDeskController",
                     "AllGuestsControllersInDesk",
                     "CanPlayerRepellGuest",
                     "ManualDesksDic",
                 })
        {
            foreach (var item in ReadObjectEnumerable(ReadMember(manager, name)))
            {
                object? controller;
                nint pointer;
                try
                {
                    controller = NormalizeDictionaryItem(item);
                    if (controller == null) continue;
                    pointer = ReadObjectPointer(controller);
                }
                catch
                {
                    continue;
                }

                if (!seen.Add(pointer)) continue;
                yield return controller;
            }
        }
    }

    private static IEnumerable<object> EnumerateControllerOrders(object controller)
    {
        var seen = new HashSet<nint>();
        foreach (var name in new[] { "AllOrders", "AllOrdersData", "PeekOrders" })
        {
            foreach (var order in ReadObjectEnumerable(ReadMember(controller, name)))
            {
                nint pointer;
                try
                {
                    pointer = ReadObjectPointer(order);
                }
                catch
                {
                    continue;
                }

                if (!seen.Add(pointer)) continue;
                yield return order;
            }
        }
    }

    private static object? NormalizeDictionaryItem(object item)
    {
        return ReadMember(item, "Value") ?? item;
    }

    private static bool IsMatchingSpecialOrder(object order, object controller, OrderPreparationRequest request)
    {
        if (ToInt(ReadMember(order, "DeskCode") ?? TryInvokeInstanceValue(order, "get_DeskCode")) != request.DeskCode)
        {
            return false;
        }

        if (!IsSpecialOrder(order))
        {
            return false;
        }

        if (request.GuestId.HasValue)
        {
            var orderGuestId = ReadGuestId(ReadMember(order, "SpecialGuests") ?? TryInvokeInstanceValue(order, "get_SpecialGuests"));
            var controllerGuestId = ReadGuestId(ReadMember(controller, "SpecialGuest") ?? TryInvokeInstanceValue(controller, "get_SpecialGuest"));
            if (orderGuestId != request.GuestId.Value && controllerGuestId != request.GuestId.Value)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSpecialOrder(object order)
    {
        if ((ReadMember(order, "SpecialGuests") ?? TryInvokeInstanceValue(order, "get_SpecialGuests")) != null)
        {
            return true;
        }

        var type = ReadMember(order, "Type") ?? TryInvokeInstanceValue(order, "get_Type");
        return type?.ToString()?.Contains("Special", StringComparison.OrdinalIgnoreCase) == true || ToInt(type) == 1;
    }

    private static int ReadGuestId(object? guest)
    {
        if (guest == null) return -1;
        return ToInt(TryInvokeInstanceValue(guest, "get_id")
            ?? TryInvokeInstanceValue(guest, "get_Id")
            ?? TryInvokeInstanceValue(guest, "get_CharacterID")
            ?? ReadMember(guest, "id")
            ?? ReadMember(guest, "Id")
            ?? ReadMember(guest, "CharacterID"));
    }

    private static object? TryInvokeInstanceValue(object target, string methodName)
    {
        try
        {
            return InvokeInstance(target, methodName, Array.Empty<object?>());
        }
        catch
        {
            return null;
        }
    }

    private static nint ReadObjectPointer(object target)
    {
        var pointer = ReadMember(target, "Pointer") ?? ReadMember(target, "NativePointer") ?? ReadMember(target, "m_CachedPtr");
        if (pointer is IntPtr intPtr) return intPtr;
        if (pointer is nint native) return native;
        if (pointer is IConvertible convertible) return new IntPtr(convertible.ToInt64(null));
        return new IntPtr(RuntimeHelpers.GetHashCode(target));
    }

    private static object? ReadMember(object target, string name)
    {
        for (var type = target.GetType(); type != null; type = type.BaseType)
        {
            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (property != null) return property.GetValue(target);

            var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (field != null) return field.GetValue(target);

            var generatedField = type.GetField($"<{name}>k__BackingField", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (generatedField != null) return generatedField.GetValue(target);
        }

        return null;
    }

    private static bool WriteMember(object target, string name, object? value)
    {
        for (var type = target.GetType(); type != null; type = type.BaseType)
        {
            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (property?.SetMethod != null)
            {
                property.SetValue(target, value);
                return true;
            }

            var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                ?? type.GetField($"<{name}>k__BackingField", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (field == null) continue;

            field.SetValue(target, value);
            return true;
        }

        return false;
    }

    private static IEnumerable<int> ReadIntEnumerable(object? value)
    {
        if (value == null) yield break;
        if (value is string) yield break;
        if (value is IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                yield return ToInt(item);
            }
        }
    }

    private static IEnumerable<object> ReadObjectEnumerable(object? value)
    {
        if (value == null) yield break;
        if (value is string) yield break;
        if (value is IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                if (item != null) yield return item;
            }
        }
    }

    private static object? GetSingletonInstance(string typeName)
    {
        var type = FindType(typeName)
            ?? throw new InvalidOperationException($"{typeName} type is not loaded.");
        var property = type.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy);
        if (property != null) return property.GetValue(null);

        var method = type.GetMethod("get_Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy);
        return method?.Invoke(null, Array.Empty<object?>());
    }

    private static object? InvokeStatic(string typeName, string methodName, object?[] args)
    {
        var type = FindType(typeName)
            ?? throw new InvalidOperationException($"{typeName} type is not loaded.");
        var method = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .FirstOrDefault(candidate => string.Equals(candidate.Name, methodName, StringComparison.Ordinal)
                && CanUseParameters(candidate.GetParameters(), args))
            ?? throw new MissingMethodException(typeName, methodName);
        return method.Invoke(null, args);
    }

    private static object? InvokeInstance(object target, string methodName, object?[] args)
    {
        var method = target.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(candidate => string.Equals(candidate.Name, methodName, StringComparison.Ordinal)
                && CanUseParameters(candidate.GetParameters(), args))
            ?? throw new MissingMethodException(target.GetType().FullName, methodName);
        return method.Invoke(target, args);
    }

    private static bool TryInvokeInstance(object target, string methodName, object?[] args)
    {
        var method = target.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(candidate => string.Equals(candidate.Name, methodName, StringComparison.Ordinal)
                && CanUseParameters(candidate.GetParameters(), args));
        if (method == null) return false;

        try
        {
            method.Invoke(target, args);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static Type? FindType(string fullName)
    {
        var direct = Type.GetType(fullName, false);
        if (direct != null) return direct;

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? type;
            try
            {
                type = assembly.GetType(fullName, false);
            }
            catch
            {
                continue;
            }

            if (type != null) return type;
        }

        return null;
    }

    private static bool CanUseParameters(ParameterInfo[] parameters, object?[] args)
    {
        if (parameters.Length != args.Length) return false;
        for (var i = 0; i < parameters.Length; i++)
        {
            var arg = args[i];
            var parameterType = parameters[i].ParameterType;
            if (parameterType.IsByRef)
            {
                parameterType = parameterType.GetElementType() ?? parameterType;
            }

            if (arg == null)
            {
                if (parameterType.IsValueType) return false;
                continue;
            }

            var argType = arg.GetType();
            if (parameterType.IsAssignableFrom(argType)) continue;
            if (parameterType.IsPrimitive && arg is IConvertible) continue;
            return false;
        }

        return true;
    }

    private static object? GetDefaultValue(Type type)
    {
        if (type == typeof(bool)) return false;
        if (type == typeof(int)) return 0;
        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }

    private static int ToInt(object? value)
    {
        if (value == null) return 0;
        if (value is int number) return number;
        if (value is Enum enumValue) return Convert.ToInt32(enumValue);
        if (value is IConvertible convertible) return Convert.ToInt32(convertible);
        return int.TryParse(value.ToString(), out var parsed) ? parsed : 0;
    }

    private static bool ReadBool(object? value)
    {
        if (value is bool boolValue) return boolValue;
        if (value is IConvertible convertible) return convertible.ToBoolean(null);
        return bool.TryParse(value?.ToString(), out var parsed) && parsed;
    }

    private static OrderPreparationResult Fail(OrderPreparationResult result, string error)
    {
        result.Error = error;
        result.Ok = false;
        result.Prepared = false;
        result.Steps.Add(new OrderPreparationStep
        {
            Name = "准备校验",
            Ok = false,
            Message = error,
        });
        return result;
    }

    private static OrderPreparationResult Finish(OrderPreparationResult result)
    {
        result.Prepared = result.Steps.Any(step => step.Ok && !step.Skipped && step.Name != "选择订单");
        result.Ok = result.Error == null && result.Steps.All(step => step.Ok || step.Skipped);
        if (!result.Ok && result.Error == null)
        {
            result.Error = result.Steps.FirstOrDefault(step => !step.Ok && !step.Skipped)?.Message;
        }

        return result;
    }

    private static void AddFailure(OrderPreparationResult result, string name, string message)
    {
        result.Steps.Add(new OrderPreparationStep
        {
            Name = name,
            Ok = false,
            Message = message,
        });
    }

    private static void AddSkipped(OrderPreparationResult result, string name, string message)
    {
        result.Steps.Add(new OrderPreparationStep
        {
            Name = name,
            Ok = true,
            Skipped = true,
            Message = message,
        });
    }

    private sealed class PendingCookingCollection
    {
        public object CookController { get; init; } = new();
        public string RecipeName { get; init; } = "";
        public DateTime CreatedAtUtc { get; init; }
    }

    private sealed class RuntimeOrderMatch
    {
        public object? Manager { get; init; }
        public object? Controller { get; init; }
        public object? Order { get; init; }
    }
}
