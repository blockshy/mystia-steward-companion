using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using BepInEx;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MystiaStewardCompanion.LocalApi;

namespace MystiaStewardCompanion.Save;

internal static class RuntimeOrderPreparationService
{
    private const string DataBaseCoreTypeName = "GameData.Core.Collections.DataBaseCore";
    private const string IzakayaConfigureTypeName = "GameData.RunTime.NightSceneUtility.IzakayaConfigure";
    private const string IzakayaTrayTypeName = "GameData.RunTime.NightSceneUtility.IzakayaTray";
    private const string RuntimeStorageTypeName = "GameData.RunTime.Common.RunTimeStorage";
    private const string PartnerManagerTypeName = "NightScene.PartnerUtility.PartnerManager";
    private const string CookSystemManagerTypeName = "NightScene.CookingUtility.CookSystemManager";
    private const string QteRewardManagerTypeName = "NightScene.CookingUtility.QTERewardManager";
    private const string GuestsManagerTypeName = "NightScene.GuestManagementUtility.GuestsManager";
    private const string OrderControllerTypeName = "Night.UI.HUD.Ordering.OrderController";
    private const string MatchedCookComboTypeName = "NightScene.UI.CookingUtility.WorkSceneCookingSelectionPannel+MatchedCookCombo";
    private static readonly object PendingCookingLock = new();
    private static readonly object AutomationLogLock = new();
    private static readonly object TrayObservationLock = new();
    private static readonly List<PendingCookingCollection> PendingCookingCollections = new();
    private static readonly List<CompletedNormalCookingCollection> CompletedNormalCookingCollections = new();
    private static readonly Dictionary<string, DateTime> TrayObservationFirstSeen = new();
    private static readonly TimeSpan PendingCookingCollectGrace = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PendingCookingIdleTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan CompletedNormalCookingRememberTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan TrayObservationRetention = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan AutomationLogRepeatSummaryInterval = TimeSpan.FromSeconds(30);
    private const long AutomationLogMaxBytes = 1024 * 1024;
    private const int AutomationLogRepeatSummaryCount = 25;
    private static string _lastAutomationLogKey = "";
    private static string _lastAutomationLogTarget = "";
    private static string _lastAutomationLogMessage = "";
    private static int _lastAutomationLogRepeatCount;
    private static int _lastAutomationLogReportedCount;
    private static DateTime _lastAutomationLogFirstAt = DateTime.MinValue;
    private static DateTime _lastAutomationLogLastAt = DateTime.MinValue;
    private enum CookingCollectionTargetKind
    {
        Tray,
        NormalOrder,
    }

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

                    if (!string.IsNullOrWhiteSpace(cookingResult.QteMessage))
                    {
                        result.Steps.Add(new OrderPreparationStep
                        {
                            Name = "料理 QTE",
                            Ok = true,
                            Skipped = cookingResult.QteSkipped,
                            Message = cookingResult.QteMessage,
                        });
                    }
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

        var runtimeOrder = FindRuntimeOrder(request);
        if (runtimeOrder.Order == null || runtimeOrder.Controller == null || runtimeOrder.Manager == null)
        {
            var diagnostic = string.IsNullOrWhiteSpace(runtimeOrder.Diagnostic) ? "" : $"（{runtimeOrder.Diagnostic}）";
            AddFailure(result, "匹配运行时订单", $"未找到当前第一笔稀客订单对象，可能订单已完成、客人已离场或经营状态刚刷新。{diagnostic}");
            return Finish(result);
        }

        result.Steps.Add(new OrderPreparationStep
        {
            Name = "匹配运行时订单",
            Ok = true,
            Message = $"已匹配桌 {request.DeskCode + 1} · {request.GuestName} 的订单对象。",
        });

        var currentFood = ReadMember(runtimeOrder.Order, "ServFood") ?? TryInvokeInstanceValue(runtimeOrder.Order, "get_ServFood");
        var currentBeverage = ReadMember(runtimeOrder.Order, "ServBeverage") ?? TryInvokeInstanceValue(runtimeOrder.Order, "get_ServBeverage");
        result.ServedFood = currentFood != null;
        result.ServedBeverage = currentBeverage != null;

        var trayItems = ReadTrayItems(tray).ToList();
        RefreshTrayObservations(trayItems);
        var missingTrayItem = false;
        var food = currentFood;
        var beverage = currentBeverage;
        var matchedFoodId = expectedFoodId;
        var matchedBacklogFood = false;
        var matchedBacklogAgeSeconds = 0;
        var matchedFoodFromTray = false;
        var matchedBeverageFromTray = false;

        if (food == null)
        {
            food = FindRareOrderFoodInTray(
                trayItems,
                expectedFoodId,
                request.AcceptableFoodIds,
                TimeSpan.FromSeconds(Math.Max(0, request.TrayBacklogMinSeconds)),
                out matchedFoodId,
                out matchedBacklogFood,
                out matchedBacklogAgeSeconds);
            if (food == null)
            {
                AddFailure(result, "匹配送餐盘料理", $"送餐盘中没有找到目标料理 {request.RecipeName}（料理 #{expectedFoodId}）。{FormatTraySummary(trayItems)}");
                missingTrayItem = true;
            }
            else
            {
                WriteMember(runtimeOrder.Order, "ServFood", food);
                result.ServedFood = true;
                matchedFoodFromTray = true;
                if (matchedBacklogFood)
                {
                    result.Steps.Add(new OrderPreparationStep
                    {
                        Name = "复用堆积料理",
                        Ok = true,
                        Message = $"送餐盘中料理 #{matchedFoodId} 已堆积 {matchedBacklogAgeSeconds} 秒，且满足当前料理 Tag，本次优先用于该稀客订单。",
                    });
                }
            }
        }
        else
        {
            result.Steps.Add(new OrderPreparationStep
            {
                Name = "送达料理",
                Ok = true,
                Skipped = true,
                Message = "订单已有料理，本次不重复送达。",
            });
        }

        if (beverage == null)
        {
            beverage = trayItems.FirstOrDefault(item => IsSellable(item, sellableType: 1, id: request.BeverageId));
            if (beverage == null)
            {
                AddFailure(result, "匹配送餐盘酒水", $"送餐盘中没有找到目标酒水 {request.BeverageName}（酒水 #{request.BeverageId}）。{FormatTraySummary(trayItems)}");
                missingTrayItem = true;
            }
            else
            {
                WriteMember(runtimeOrder.Order, "ServBeverage", beverage);
                result.ServedBeverage = true;
                matchedBeverageFromTray = true;
            }
        }
        else
        {
            result.Steps.Add(new OrderPreparationStep
            {
                Name = "送达酒水",
                Ok = true,
                Skipped = true,
                Message = "订单已有酒水，本次不重复送达。",
            });
        }

        if (missingTrayItem)
        {
            DeliverMatchedRareOrderPart(result, tray, food, matchedFoodFromTray, "送达料理", FormatRareFoodDeliveryMessage(request.RecipeName, expectedFoodId, matchedFoodId, matchedBacklogFood, "已先送达，等待补齐酒水后完成订单。"));
            DeliverMatchedRareOrderPart(result, tray, beverage, matchedBeverageFromTray, "送达酒水", $"{request.BeverageName} 已先送达，等待补齐料理后完成订单。");
            return Finish(result);
        }

        result.Steps.Add(new OrderPreparationStep
        {
            Name = "匹配送餐盘",
            Ok = true,
            Message = $"已找到{(matchedBacklogFood ? $"堆积料理 #{matchedFoodId}" : $"目标料理 {request.RecipeName}")}和目标酒水 {request.BeverageName}。",
        });

        if (!ReadBool(InvokeInstance(runtimeOrder.Order, "get_IsFullfilled", Array.Empty<object?>())))
        {
            if (matchedFoodFromTray)
            {
                WriteMember(runtimeOrder.Order, "ServFood", null);
                result.ServedFood = currentFood != null;
            }

            if (matchedBeverageFromTray)
            {
                WriteMember(runtimeOrder.Order, "ServBeverage", null);
                result.ServedBeverage = currentBeverage != null;
            }

            AddFailure(result, "写入订单", "料理和酒水已匹配，但游戏判定订单未满足；本次未从送餐盘移除物品。");
            return Finish(result);
        }

        DeliverMatchedRareOrderPart(result, tray, food, matchedFoodFromTray, "送达料理", FormatRareFoodDeliveryMessage(request.RecipeName, expectedFoodId, matchedFoodId, matchedBacklogFood, "已送达。"));
        DeliverMatchedRareOrderPart(result, tray, beverage, matchedBeverageFromTray, "送达酒水", $"{request.BeverageName} 已送达。");
        result.Steps.Add(new OrderPreparationStep
        {
            Name = "写入订单",
            Ok = true,
            Message = "订单料理和酒水已满足，准备触发评价。",
        });

        InvokeInstance(runtimeOrder.Manager, "EvaluateOrder", new object?[] { runtimeOrder.Controller, false, null });
        result.CompletedOrder = true;
        result.Steps.Add(new OrderPreparationStep
        {
            Name = "触发上菜评价",
            Ok = true,
            Message = "已调用游戏评价流程完成当前订单。",
        });

        return Finish(result);
    }

    private static void DeliverMatchedRareOrderPart(
        OrderPreparationResult result,
        object tray,
        object? sellable,
        bool shouldDeliver,
        string stepName,
        string message)
    {
        if (!shouldDeliver || sellable == null) return;
        InvokeInstance(tray, "Deliver", new object?[] { sellable });
        result.Steps.Add(new OrderPreparationStep
        {
            Name = stepName,
            Ok = true,
            Message = message,
        });
    }

    private static object? FindRareOrderFoodInTray(
        IReadOnlyList<object> trayItems,
        int expectedFoodId,
        IReadOnlyList<int> acceptableFoodIds,
        TimeSpan backlogThreshold,
        out int matchedFoodId,
        out bool matchedBacklogFood,
        out int matchedBacklogAgeSeconds)
    {
        matchedFoodId = expectedFoodId;
        matchedBacklogFood = false;
        matchedBacklogAgeSeconds = 0;

        var exact = trayItems.FirstOrDefault(item => IsSellable(item, sellableType: 0, id: expectedFoodId));
        if (exact != null)
        {
            return exact;
        }

        var acceptable = acceptableFoodIds
            .Where(id => id >= 0 && id != expectedFoodId)
            .Distinct()
            .ToHashSet();
        if (acceptable.Count == 0) return null;

        foreach (var item in trayItems)
        {
            if (ReadSellableType(item) != 0) continue;

            var foodId = ReadSellableId(item);
            if (!acceptable.Contains(foodId)) continue;
            if (!TryGetTrayObservationAge(item, out var age) || age < backlogThreshold) continue;

            matchedFoodId = foodId;
            matchedBacklogFood = true;
            matchedBacklogAgeSeconds = Math.Max(0, (int)Math.Floor(age.TotalSeconds));
            return item;
        }

        return null;
    }

    private static string FormatRareFoodDeliveryMessage(
        string recipeName,
        int expectedFoodId,
        int matchedFoodId,
        bool matchedBacklogFood,
        string suffix)
    {
        if (!matchedBacklogFood)
        {
            return $"{recipeName} {suffix}";
        }

        return $"已复用送餐盘中堆积料理 #{matchedFoodId}（原目标料理 #{expectedFoodId}：{recipeName}），{suffix}";
    }

    private static void RefreshTrayObservations(IReadOnlyList<object> trayItems)
    {
        var now = DateTime.UtcNow;
        var currentKeys = new HashSet<string>();

        lock (TrayObservationLock)
        {
            foreach (var item in trayItems)
            {
                var key = BuildTrayObservationKey(item);
                currentKeys.Add(key);
                if (!TrayObservationFirstSeen.ContainsKey(key))
                {
                    TrayObservationFirstSeen[key] = now;
                }
            }

            foreach (var key in TrayObservationFirstSeen.Keys.ToArray())
            {
                if (currentKeys.Contains(key)) continue;
                if (now - TrayObservationFirstSeen[key] <= TrayObservationRetention) continue;
                TrayObservationFirstSeen.Remove(key);
            }
        }
    }

    private static bool TryGetTrayObservationAge(object item, out TimeSpan age)
    {
        var key = BuildTrayObservationKey(item);
        lock (TrayObservationLock)
        {
            if (TrayObservationFirstSeen.TryGetValue(key, out var firstSeen))
            {
                age = DateTime.UtcNow - firstSeen;
                return true;
            }
        }

        age = TimeSpan.Zero;
        return false;
    }

    private static string BuildTrayObservationKey(object item)
    {
        var type = ReadSellableType(item);
        var id = ReadSellableId(item);
        try
        {
            return $"{type}:{id}:ptr:{ReadObjectPointer(item):x}";
        }
        catch
        {
            return $"{type}:{id}:hash:{RuntimeHelpers.GetHashCode(item)}";
        }
    }

    public static OrderPreparationResult CompleteNormalFirst(OrderPreparationRequest request)
    {
        var result = new OrderPreparationResult
        {
            Order = new OrderPreparationOrder
            {
                DeskCode = request.DeskCode,
                GuestName = string.IsNullOrWhiteSpace(request.GuestName) ? "普客" : request.GuestName,
                FoodTag = "普客",
                BeverageTag = "普客",
            },
            RecipeId = request.RecipeId,
            RecipeName = request.RecipeName,
            BeverageId = request.BeverageId,
            BeverageName = request.BeverageName,
        };

        result.Steps.Add(new OrderPreparationStep
        {
            Name = "选择普客订单",
            Ok = true,
            Message = $"桌 {request.DeskCode + 1} · {result.Order.GuestName} · 料理 {request.RecipeName}",
        });

        var runtimeOrder = FindRuntimeNormalOrder(request);
        if (runtimeOrder.Order == null || runtimeOrder.Manager == null)
        {
            var diagnostic = string.IsNullOrWhiteSpace(runtimeOrder.Diagnostic) ? "" : $"（{runtimeOrder.Diagnostic}）";
            AddFailure(result, "匹配普客订单", $"未找到当前第一笔普客订单对象，可能订单已完成、客人已离场或经营状态刚刷新。{diagnostic}");
            return Finish(result);
        }

        result.Steps.Add(new OrderPreparationStep
        {
            Name = "匹配普客订单",
            Ok = true,
            Message = $"已匹配桌 {request.DeskCode + 1} 的普客订单对象。",
        });

        var expectedFoodId = request.FoodId >= 0 ? request.FoodId : ResolveFoodIdFromRecipeId(request.RecipeId);
        var foodAlreadyServed = ReadMember(runtimeOrder.Order, "ServFood") != null;
        if (foodAlreadyServed)
        {
            AddSkipped(result, "普客料理", "该订单已经送达料理，不再自动处理。");
        }
        else if (expectedFoodId < 0)
        {
            AddFailure(result, "普客料理", "订单没有有效的料理 ID。");
            if (request.StopOnError) return Finish(result);
        }
        else
        {
            var pendingFood = ReadMember(runtimeOrder.Order, "ServedFoodInAir");
            if (pendingFood != null && IsSellable(pendingFood, sellableType: 0, id: expectedFoodId))
            {
                AddSkipped(result, "普客料理", $"目标料理 {request.RecipeName} 已处于订单待送达状态，等待玩家在游戏内确认。");
            }
            else if (pendingFood != null)
            {
                AddFailure(result, "普客料理", $"订单已有其他待送达料理，暂不自动制作 {request.RecipeName}。");
                if (request.StopOnError) return Finish(result);
            }
            else if (TryConfirmCompletedNormalOrderCooking(request.OrderKey, request.DeskCode, expectedFoodId, out var completedMessage))
            {
                AddSkipped(result, "普客保温箱", completedMessage);
            }
            else if (!string.IsNullOrWhiteSpace(completedMessage))
            {
                AddSkipped(result, "普客保温箱复查", completedMessage);
            }
            else if (HasPendingNormalOrderCooking(request.OrderKey, runtimeOrder.Order, request.DeskCode, expectedFoodId, out var pendingMessage))
            {
                AddSkipped(result, "普客开始料理", pendingMessage);
            }
            else if (request.AutoStartCooking)
            {
                var recipeId = request.RecipeId >= 0 ? request.RecipeId : ResolveRecipeIdFromFoodId(expectedFoodId);
                if (recipeId < 0)
                {
                    AddFailure(result, "普客开始料理", $"未找到料理 {request.RecipeName}（成品 #{expectedFoodId}）对应的配方 ID。");
                    if (request.StopOnError) return Finish(result);
                }
                else
                {
                    var target = CookingCollectionTarget.ForNormalOrder(
                        runtimeOrder.Manager,
                        runtimeOrder.Controller,
                        runtimeOrder.Order,
                        request.OrderKey,
                        expectedFoodId,
                        request.RecipeName,
                        request.DeskCode,
                        result.Order.GuestName);
                    var cookingResult = TryStartCooking(recipeId, request.RecipeName, request.ExtraIngredientIds, request.AutoCollectCooking, target);
                    if (cookingResult.Ok)
                    {
                        result.Steps.Add(new OrderPreparationStep
                        {
                            Name = "普客开始料理",
                            Ok = true,
                            Message = cookingResult.Message,
                        });
                        if (!string.IsNullOrWhiteSpace(cookingResult.QteMessage))
                        {
                            result.Steps.Add(new OrderPreparationStep
                            {
                                Name = "料理 QTE",
                                Ok = true,
                                Skipped = cookingResult.QteSkipped,
                                Message = cookingResult.QteMessage,
                            });
                        }
                        AddSkipped(result, "普客保温箱", request.AutoCollectCooking
                            ? "料理已开始制作，完成后会自动收至普客保温箱。"
                            : "料理已开始制作，自动收至保温箱已关闭。");
                    }
                    else
                    {
                        AddFailure(result, "普客开始料理", cookingResult.Message);
                        if (request.StopOnError) return Finish(result);
                    }
                }
            }
            else
            {
                AddSkipped(result, "普客料理", $"普客订单尚未获得目标料理 {request.RecipeName}（料理 #{expectedFoodId}），自动制作料理已关闭。");
            }
        }

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
                    result = DateTime.UtcNow - pending.CreatedAtUtc >= PendingCookingIdleTimeout
                        ? (true, $"{pending.RecipeName} 自动收取已停止：{ex.GetBaseException().Message}")
                        : (false, "");
                }

                if (!string.IsNullOrWhiteSpace(result.Message))
                {
                    messages.Add(result.Message);
                    AppendAutomationLog("pending", pending.Target, result.Message);
                }

                if (result.Remove)
                {
                    AppendAutomationLog("pending-remove", pending.Target, $"{pending.RecipeName}; age={(DateTime.UtcNow - pending.CreatedAtUtc).TotalSeconds:F1}s");
                    PendingCookingCollections.RemoveAt(i);
                }
            }
        }

        return messages;
    }

    private static (bool Ok, string Message) TryTakeBeverageToTray(int beverageId, string beverageName)
    {
        var tray = GetSingletonInstance(IzakayaTrayTypeName);
        if (tray == null)
        {
            return (false, "当前送餐盘对象不可用，请确认已进入夜晚经营页面。");
        }

        if (ReadTrayItems(tray).Any(item => IsSellable(item, sellableType: 1, id: beverageId)))
        {
            return (true, $"{beverageName} 已在送餐盘中，本次不重复取酒。");
        }

        var currentQuantity = GetBeverageQuantity(beverageId);
        if (currentQuantity == 0)
        {
            return (false, $"{beverageName} 当前库存为 0，无法放入送餐盘。");
        }

        var isFull = InvokeInstance(tray, "get_IsTrayFull", Array.Empty<object?>());
        if (isFull is bool isTrayFull && isTrayFull)
        {
            return (false, "送餐盘已满，无法继续取酒。");
        }

        var sellable = InvokeStatic(DataBaseCoreTypeName, "AsNewBeverage", new object?[] { beverageId });
        if (sellable == null)
        {
            return (false, $"无法从游戏数据库创建酒水对象：{beverageName} #{beverageId}。");
        }

        InvokeInstance(tray, "Receive", new[] { sellable });
        if (currentQuantity > 0)
        {
            InvokeRuntimeStorageOut("BeverageOut", beverageId);
        }

        var quantityText = currentQuantity < 0 ? "无限库存" : $"剩余 {Math.Max(0, currentQuantity - 1)}";
        return (true, $"{beverageName} 已放入送餐盘（{quantityText}）。");
    }

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

        var finalFood = CreateCookResult(recipe, extraIngredientIds, cooker) ?? baseFood;
        var ingredientIds = ReadRecipeIngredientIds(recipe).Concat(extraIngredientIds).ToArray();
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

    private static void RememberCompletedNormalOrderCooking(CookingCollectionTarget target, object storedFood)
    {
        if (target.Kind != CookingCollectionTargetKind.NormalOrder || target.FoodId < 0) return;

        lock (PendingCookingLock)
        {
            var now = DateTime.UtcNow;
            PruneCompletedNormalOrderCooking(now);
            CompletedNormalCookingCollections.RemoveAll(item => IsSameCompletedNormalOrderCooking(item, target.OrderKey, target.DeskCode, target.FoodId));
            CompletedNormalCookingCollections.Add(new CompletedNormalCookingCollection
            {
                OrderKey = target.OrderKey,
                DeskCode = target.DeskCode,
                FoodId = target.FoodId,
                FoodName = target.FoodName,
                StoredFoodKey = GetStoredFoodKey(storedFood),
                StoredAtUtc = now,
                LastConfirmedAtUtc = now,
            });
        }
    }

    private static bool TryConfirmCompletedNormalOrderCooking(string orderKey, int deskCode, int foodId, out string message)
    {
        lock (PendingCookingLock)
        {
            PruneCompletedNormalOrderCooking(DateTime.UtcNow);
            var completed = CompletedNormalCookingCollections.FirstOrDefault(item => IsSameCompletedNormalOrderCooking(item, orderKey, deskCode, foodId));
            if (completed != null)
            {
                var storageStatus = ReadNormalStorageStatus(foodId, completed.StoredFoodKey);
                if (storageStatus.HasTarget)
                {
                    completed.LastConfirmedAtUtc = DateTime.UtcNow;
                    message = $"目标料理 {completed.FoodName} 已在普客保温箱中，等待玩家手动送达。{storageStatus.Message}";
                    return true;
                }

                if (storageStatus.CanVerify)
                {
                    CompletedNormalCookingCollections.Remove(completed);
                    message = $"此前记录 {completed.FoodName} 已收至普客保温箱，但当前暂存容器未读取到该料理，已撤销本地回执并重新处理。{storageStatus.Message}";
                    AppendAutomationLog("completed-missing", CookingCollectionTarget.ForNormalOrder(null, null, null, orderKey, foodId, completed.FoodName, deskCode, ""), message);
                    return false;
                }

                message = $"目标料理 {completed.FoodName} 已有收取回执，但当前暂存容器暂时不可验证，下一轮继续复查。{storageStatus.Message}";
                return true;
            }
        }

        message = "";
        return false;
    }

    private static bool IsSameCompletedNormalOrderCooking(CompletedNormalCookingCollection item, string orderKey, int deskCode, int foodId)
    {
        if (item.FoodId != foodId) return false;
        if (!string.IsNullOrWhiteSpace(orderKey) && !string.IsNullOrWhiteSpace(item.OrderKey))
        {
            return string.Equals(orderKey, item.OrderKey, StringComparison.Ordinal);
        }

        return item.DeskCode >= 0 && deskCode >= 0 && item.DeskCode == deskCode;
    }

    private static void PruneCompletedNormalOrderCooking(DateTime now)
    {
        CompletedNormalCookingCollections.RemoveAll(item => now - item.StoredAtUtc >= CompletedNormalCookingRememberTimeout);
    }

    private static NormalStorageStatus ReadNormalStorageStatus(int foodId, string storedFoodKey)
    {
        var configure = GetSingletonInstance(IzakayaConfigureTypeName);
        if (configure == null)
        {
            return NormalStorageStatus.Unknown("当前料理暂存容器不可用。");
        }

        var storedFoods = ReadStoredFoodList(configure);
        if (storedFoods == null)
        {
            return NormalStorageStatus.Unknown("未读取到 StoredFoods 列表。");
        }

        var rawCount = ToInt(TryInvokeInstanceValue(storedFoods, "get_Count")
            ?? ReadMember(storedFoods, "Count")
            ?? ReadMember(storedFoods, "_size"), -1);
        var scanned = 0;
        var matchedById = 0;
        var matchedByObject = false;
        foreach (var food in ReadObjectEnumerable(storedFoods))
        {
            scanned++;
            if (!string.IsNullOrWhiteSpace(storedFoodKey) && string.Equals(GetStoredFoodKey(food), storedFoodKey, StringComparison.Ordinal))
            {
                matchedByObject = true;
            }

            if (IsSellable(food, sellableType: 0, id: foodId))
            {
                matchedById++;
            }
        }

        if (matchedByObject || matchedById > 0)
        {
            var detail = matchedByObject
                ? $"已确认目标对象仍在暂存容器中（同料理数量 {matchedById}）。"
                : $"已确认暂存容器中存在同名料理 {matchedById} 份。";
            return NormalStorageStatus.Verified(true, detail);
        }

        if (rawCount > 0 && scanned == 0)
        {
            return NormalStorageStatus.Unknown($"暂存容器显示有 {rawCount} 个对象，但当前无法枚举。");
        }

        return NormalStorageStatus.Verified(false, rawCount >= 0
            ? $"暂存容器当前总数 {rawCount}，目标料理数量 0。"
            : "暂存容器可读取，但目标料理数量为 0。");
    }

    private static object? ReadStoredFoodList(object configure)
    {
        return ReadMember(configure, "StoredFoods")
            ?? TryInvokeInstanceValue(configure, "get_StoredFoods")
            ?? TryInvokeInstanceValue(configure, "GetStoredFoods");
    }

    private static string GetStoredFoodKey(object? food)
    {
        if (food == null) return "";
        try
        {
            return $"ptr:{ReadObjectPointer(food):x}";
        }
        catch
        {
            return $"hash:{RuntimeHelpers.GetHashCode(food)}";
        }
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

    private static (bool Remove, string Message) TryCollectNormalOrderFood(PendingCookingCollection pending, object cookedFood)
    {
        if (pending.Target.FoodId >= 0 && !IsSellable(cookedFood, sellableType: 0, id: pending.Target.FoodId))
        {
            return (true, $"{pending.RecipeName} 已完成，但成品不是目标料理 {pending.Target.FoodName}（料理 #{pending.Target.FoodId}），本次不会放入普客保温箱。");
        }

        if (!TryStoreFoodInNormalStorage(cookedFood, pending.Target.FoodId, out var storeMessage))
        {
            return (false, $"{pending.RecipeName} 已完成，但{storeMessage}，等待下一轮重试。");
        }

        RememberCompletedNormalOrderCooking(pending.Target, cookedFood);
        TryResetCookControllerAfterNormalWarmerCollect(pending.CookController, cookedFood);

        return (true, $"{pending.RecipeName} 已自动收至普客保温箱，等待玩家手动送达。{storeMessage}");
    }

    private static bool TryStoreFoodInNormalStorage(object cookedFood, int expectedFoodId, out string message)
    {
        try
        {
            var configure = GetSingletonInstance(IzakayaConfigureTypeName);
            if (configure == null)
            {
                message = "当前料理暂存容器不可用";
                return false;
            }

            var beforeCount = CountStoredFoods(configure, expectedFoodId);
            if (!TryInvokeStoreFood(configure, cookedFood))
            {
                message = "写入料理暂存容器失败：未找到可用的 StoreFood 入口";
                return false;
            }

            var storageStatus = ReadNormalStorageStatus(expectedFoodId, GetStoredFoodKey(cookedFood));
            var afterCount = CountStoredFoods(configure, expectedFoodId);
            if (beforeCount >= 0 && afterCount >= 0 && afterCount <= beforeCount)
            {
                message = $"写入料理暂存容器后数量未增加（料理 #{expectedFoodId}: {beforeCount}->{afterCount}）";
                return false;
            }

            if (!storageStatus.HasTarget && storageStatus.CanVerify)
            {
                message = $"写入料理暂存容器后未读取到目标料理（料理 #{expectedFoodId}）。{storageStatus.Message}";
                return false;
            }

            message = beforeCount >= 0 && afterCount >= 0
                ? $"料理暂存数量 {beforeCount}->{afterCount}。"
                : storageStatus.Message;
            return true;
        }
        catch (Exception ex)
        {
            message = $"写入料理暂存容器失败：{ex.GetBaseException().Message}";
            return false;
        }
    }

    private static bool TryInvokeStoreFood(object configure, object cookedFood)
    {
        return TryInvokeInstance(configure, "StoreFood", new object?[] { cookedFood, -1 })
            || TryInvokeInstance(configure, "StoreFood", new object?[] { cookedFood });
    }

    private static int CountStoredFoods(object configure, int expectedFoodId)
    {
        if (expectedFoodId < 0) return -1;

        var storedFoods = ReadStoredFoodList(configure);
        if (storedFoods == null) return -1;

        var rawCount = ToInt(TryInvokeInstanceValue(storedFoods, "get_Count")
            ?? ReadMember(storedFoods, "Count")
            ?? ReadMember(storedFoods, "_size"), -1);
        var count = 0;
        var scanned = 0;
        foreach (var food in ReadObjectEnumerable(storedFoods))
        {
            scanned++;
            if (IsSellable(food, sellableType: 0, id: expectedFoodId))
            {
                count++;
            }
        }

        if (scanned == 0 && rawCount > 0)
        {
            return -1;
        }

        return count;
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

    private static void TryResetCookControllerAfterNormalWarmerCollect(object cookController, object cookedFood)
    {
        try
        {
            TryInvokeInstance(cookController, "CloseCookingVisual", Array.Empty<object?>());
            TryClearCookController(cookController, cookedFood);
        }
        catch
        {
            // Do not call AfterPlayerExtract here. That path represents a player extract
            // and can trigger cooker/order side effects beyond placing food in the warmer.
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

    private static void AppendAutomationLog(string action, CookingCollectionTarget? target, string message)
    {
        try
        {
            var path = ResolveAutomationLogPath();
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            lock (AutomationLogLock)
            {
                var now = DateTime.Now;
                var targetText = FormatAutomationTarget(target);
                var key = string.Join("|", action, targetText, message);
                if (string.Equals(key, _lastAutomationLogKey, StringComparison.Ordinal))
                {
                    _lastAutomationLogRepeatCount++;
                    _lastAutomationLogLastAt = now;
                    var unreportedCount = _lastAutomationLogRepeatCount - _lastAutomationLogReportedCount;
                    if (unreportedCount < AutomationLogRepeatSummaryCount
                        && now - _lastAutomationLogFirstAt < AutomationLogRepeatSummaryInterval)
                    {
                        return;
                    }

                    RotateAutomationLogIfNeeded(path);
                    File.AppendAllText(
                        path,
                        FormatAutomationLogLine(
                            now,
                            "repeat",
                            targetText,
                            $"上一条重复 {unreportedCount} 次，累计 {_lastAutomationLogRepeatCount - 1} 次；{message}") + Environment.NewLine,
                        new UTF8Encoding(false));
                    _lastAutomationLogReportedCount = _lastAutomationLogRepeatCount;
                    _lastAutomationLogFirstAt = now;
                    return;
                }

                RotateAutomationLogIfNeeded(path);
                FlushAutomationLogRepeatSummary(path, now);
                File.AppendAllText(path, FormatAutomationLogLine(now, action, targetText, message) + Environment.NewLine, new UTF8Encoding(false));
                _lastAutomationLogKey = key;
                _lastAutomationLogTarget = targetText;
                _lastAutomationLogMessage = message;
                _lastAutomationLogRepeatCount = 1;
                _lastAutomationLogReportedCount = 1;
                _lastAutomationLogFirstAt = now;
                _lastAutomationLogLastAt = now;
            }
        }
        catch
        {
            // Diagnostics must never affect game automation.
        }
    }

    private static void FlushAutomationLogRepeatSummary(string path, DateTime now)
    {
        if (_lastAutomationLogRepeatCount <= _lastAutomationLogReportedCount) return;

        var unreportedCount = _lastAutomationLogRepeatCount - _lastAutomationLogReportedCount;
        File.AppendAllText(
            path,
            FormatAutomationLogLine(
                now,
                "repeat",
                _lastAutomationLogTarget,
                $"上一条重复 {unreportedCount} 次，累计 {_lastAutomationLogRepeatCount - 1} 次；{_lastAutomationLogMessage}") + Environment.NewLine,
            new UTF8Encoding(false));
        _lastAutomationLogReportedCount = _lastAutomationLogRepeatCount;
    }

    private static string FormatAutomationLogLine(DateTime now, string action, string targetText, string message)
    {
        return string.Join(" ",
            now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
            action,
            targetText,
            message);
    }

    public static string ResolveAutomationLogPath()
    {
        return Path.Combine(Paths.ConfigPath, "MystiaStewardCompanion", "automation-jobs.log");
    }

    private static void RotateAutomationLogIfNeeded(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length < AutomationLogMaxBytes) return;
            var backupPath = path + ".1";
            if (File.Exists(backupPath)) File.Delete(backupPath);
            File.Move(path, backupPath);
        }
        catch
        {
            // Ignore rotation failures; append may still succeed.
        }
    }

    private static string FormatAutomationTarget(CookingCollectionTarget? target)
    {
        if (target == null) return "target=none";
        return target.Kind == CookingCollectionTargetKind.NormalOrder
            ? $"target=normal desk={target.DeskCode + 1} orderKey={target.OrderKey} food={target.FoodId}/{target.FoodName} guest={target.GuestName}"
            : "target=rare-tray";
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

    private static IEnumerable<object> ReadTrayItems(object tray)
    {
        var trayList = InvokeInstance(tray, "get_Tray", Array.Empty<object?>());
        if (trayList == null) yield break;

        var seen = new HashSet<nint>();
        var slotCount = ToInt(TryInvokeInstanceValue(tray, "get_TrayMaxNum"));
        if (slotCount <= 0)
        {
            slotCount = ReadFixedListCapacity(trayList);
        }

        if (slotCount <= 0)
        {
            slotCount = ToInt(TryInvokeInstanceValue(trayList, "Count"));
        }

        for (var index = 0; index < Math.Min(slotCount, 32); index++)
        {
            var item = TryInvokeInstanceValue(trayList, "get_Item", new object?[] { index });
            if (item == null) continue;

            nint pointer;
            try
            {
                pointer = ReadObjectPointer(item);
            }
            catch
            {
                pointer = new IntPtr(RuntimeHelpers.GetHashCode(item));
            }

            if (!seen.Add(pointer)) continue;
            yield return item;
        }

        foreach (var item in ReadObjectEnumerable(trayList))
        {
            nint pointer;
            try
            {
                pointer = ReadObjectPointer(item);
            }
            catch
            {
                pointer = new IntPtr(RuntimeHelpers.GetHashCode(item));
            }

            if (!seen.Add(pointer)) continue;
            yield return item;
        }
    }

    private static int ReadFixedListCapacity(object fixedList)
    {
        var elements = ReadMember(fixedList, "elements");
        if (elements is Array array) return array.Length;

        var length = ReadMember(elements ?? fixedList, "Length")
            ?? ReadMember(elements ?? fixedList, "Count")
            ?? ReadMember(elements ?? fixedList, "max_length")
            ?? ReadMember(elements ?? fixedList, "maxLength");
        return ToInt(length);
    }

    private static bool IsSellable(object item, int sellableType, int id)
    {
        return ReadSellableType(item) == sellableType && ReadSellableId(item) == id;
    }

    private static string FormatTraySummary(IReadOnlyList<object> trayItems)
    {
        if (trayItems.Count == 0)
        {
            return "当前读取到的送餐盘为空。";
        }

        var items = trayItems
            .Take(8)
            .Select(item => $"type={ReadSellableType(item)},id={ReadSellableId(item)}")
            .ToArray();
        var suffix = trayItems.Count > items.Length ? $" 等 {trayItems.Count} 个" : "";
        return $"当前读取到的送餐盘：{string.Join("; ", items)}{suffix}。";
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

        var captured = FindCapturedRuntimeOrder(request, manager);
        if (captured.Order != null && captured.Controller != null)
        {
            return captured;
        }

        var scannedControllers = 0;
        var scannedOrders = 0;
        foreach (var controller in EnumerateGuestControllers(manager))
        {
            scannedControllers++;
            if (controller == null) continue;
            foreach (var order in EnumerateControllerOrders(controller))
            {
                scannedOrders++;
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

        return new RuntimeOrderMatch
        {
            Diagnostic = $"captured={captured.Diagnostic}, scannedControllers={scannedControllers}, scannedOrders={scannedOrders}",
        };
    }

    private static RuntimeOrderMatch FindRuntimeNormalOrder(OrderPreparationRequest request)
    {
        var manager = GetSingletonInstance(GuestsManagerTypeName);
        if (manager == null) return new RuntimeOrderMatch();

        var scannedControllers = 0;
        var scannedControllerOrders = 0;
        foreach (var controller in EnumerateGuestControllers(manager))
        {
            scannedControllers++;
            if (controller == null) continue;
            foreach (var order in EnumerateControllerOrders(controller))
            {
                scannedControllerOrders++;
                try
                {
                    if (!IsMatchingNormalOrder(order, request)) continue;
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
                    Diagnostic = $"controllerOrders={scannedControllerOrders}",
                };
            }
        }

        var scannedUiOrders = 0;
        foreach (var order in EnumerateOrderControllerOrders())
        {
            scannedUiOrders++;
            if (!IsMatchingNormalOrder(order, request)) continue;

            var controller = FindControllerForOrder(manager, order, request);
            return new RuntimeOrderMatch
            {
                Manager = manager,
                Controller = controller,
                Order = order,
                Diagnostic = $"controllers={scannedControllers}, controllerOrders={scannedControllerOrders}, uiOrders={scannedUiOrders}",
            };
        }

        return new RuntimeOrderMatch
        {
            Diagnostic = $"controllers={scannedControllers}, controllerOrders={scannedControllerOrders}, uiOrders={scannedUiOrders}",
        };
    }

    private static IEnumerable<object> EnumerateOrderControllerOrders()
    {
        var orderControllerType = FindType(OrderControllerTypeName);
        if (orderControllerType == null) yield break;

        object? showOrders = null;
        try
        {
            showOrders = InvokeStatic(OrderControllerTypeName, "GetShowInUIOrders", Array.Empty<object?>());
        }
        catch
        {
            // Try active UI elements below.
        }

        foreach (var order in ReadObjectEnumerable(showOrders))
        {
            yield return order;
        }

        object? controller = null;
        try
        {
            controller = GetSingletonInstance(OrderControllerTypeName);
        }
        catch
        {
            // Static instance may not exist before the HUD is built.
        }

        if (controller == null) yield break;

        foreach (var element in ReadObjectEnumerable(ReadMember(controller, "m_Orders")))
        {
            var activeOrder = ReadMember(NormalizeDictionaryItem(element) ?? element, "ActiveOrder");
            if (activeOrder != null) yield return activeOrder;
        }
    }

    private static object? FindControllerForOrder(object manager, object order, OrderPreparationRequest request)
    {
        foreach (var controller in EnumerateGuestControllers(manager))
        {
            if (controller == null) continue;
            foreach (var candidate in EnumerateControllerOrders(controller))
            {
                if (IsSameObject(candidate, order)) return controller;
            }
        }

        foreach (var controller in EnumerateGuestControllers(manager))
        {
            if (controller == null) continue;
            if (ToInt(ReadMember(controller, "DeskCode") ?? TryInvokeInstanceValue(controller, "get_DeskCode"), -999) != request.DeskCode) continue;
            if (EnumerateControllerOrders(controller).Any(candidate => IsMatchingNormalOrder(candidate, request)))
            {
                return controller;
            }
        }

        return null;
    }

    private static bool IsMatchingNormalOrder(object order, OrderPreparationRequest request)
    {
        if (!IsNormalOrder(order)) return false;
        if (!string.IsNullOrWhiteSpace(request.OrderKey)
            && !string.Equals(BuildRuntimeOrderKey(order), request.OrderKey, StringComparison.Ordinal))
        {
            return false;
        }

        var deskCode = ToInt(ReadMember(order, "DeskCode") ?? TryInvokeInstanceValue(order, "get_DeskCode"), -999);
        if (request.DeskCode >= 0 && deskCode != request.DeskCode) return false;

        if (request.FoodId >= 0 && ReadNormalFoodId(order) != request.FoodId) return false;
        if (request.BeverageId >= 0 && ReadNormalBeverageId(order) != request.BeverageId) return false;
        return true;
    }

    private static string BuildRuntimeOrderKey(object order)
    {
        try
        {
            return $"ptr:{ReadObjectPointer(order):x}";
        }
        catch
        {
            return $"hash:{RuntimeHelpers.GetHashCode(order)}";
        }
    }

    private static bool IsNormalOrder(object order)
    {
        if (IsSpecialOrder(order)) return false;
        var typeName = order.GetType().Name;
        if (typeName.IndexOf("NormalOrder", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        var type = ReadMember(order, "Type") ?? TryInvokeInstanceValue(order, "get_Type");
        return type?.ToString()?.Contains("Normal", StringComparison.OrdinalIgnoreCase) == true || ToInt(type, -1) == 0;
    }

    private static int ReadNormalFoodId(object order)
    {
        return ReadNormalSellableId(
            ReadMember(order, "RequestFood") ?? TryInvokeInstanceValue(order, "get_RequestFood"),
            ReadMember(order, "foodRequest"));
    }

    private static int ReadNormalBeverageId(object order)
    {
        return ReadNormalSellableId(
            ReadMember(order, "RequestBeverage") ?? TryInvokeInstanceValue(order, "get_RequestBeverage"),
            ReadMember(order, "beverageRequest"));
    }

    private static int ReadNormalSellableId(object? sellable, object? fallback)
    {
        if (sellable != null)
        {
            foreach (var member in new[] { "id", "Id", "ID", "foodID", "FoodID" })
            {
                var parsed = ToInt(ReadMember(sellable, member) ?? TryInvokeInstanceValue(sellable, $"get_{member}"), int.MinValue);
                if (parsed != int.MinValue) return parsed;
            }
        }

        return ToInt(fallback, -1);
    }

    private static bool IsSameObject(object left, object right)
    {
        try
        {
            return ReadObjectPointer(left) == ReadObjectPointer(right);
        }
        catch
        {
            return ReferenceEquals(left, right);
        }
    }

    private static RuntimeOrderMatch FindCapturedRuntimeOrder(OrderPreparationRequest request, object manager)
    {
        var capturedOrders = SpecialOrderRuntimeCapture.Snapshot(TimeSpan.FromHours(6));
        var candidates = capturedOrders
            .Select(captured => new
            {
                Order = captured,
                Score = ScoreCapturedOrder(captured, request),
            })
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Order.FirstCapturedAt)
            .ThenBy(candidate => candidate.Order.CapturedAt)
            .ToList();

        foreach (var candidate in candidates)
        {
            var captured = candidate.Order;
            if (captured.OrderObject == null || captured.ControllerObject == null) continue;

            return new RuntimeOrderMatch
            {
                Manager = manager,
                Controller = captured.ControllerObject,
                Order = captured.OrderObject,
                Diagnostic = $"capturedCandidates={candidates.Count}, score={candidate.Score}, source={captured.CaptureSource}",
            };
        }

        return new RuntimeOrderMatch
        {
            Diagnostic = $"capturedCandidates={candidates.Count}, capturedTotal={capturedOrders.Count}, captured=[{FormatCapturedOrderSummary(capturedOrders)}]",
        };
    }

    private static int ScoreCapturedOrder(CapturedRuntimeSpecialOrder captured, OrderPreparationRequest request)
    {
        if (captured.OrderObject == null || captured.ControllerObject == null) return 0;

        var score = 0;
        var deskMatched = false;
        if (captured.DeskCode >= 0 && request.DeskCode >= 0)
        {
            if (captured.DeskCode == request.DeskCode)
            {
                score += 12;
                deskMatched = true;
            }
            else
            {
                score -= 8;
            }
        }

        if (request.GuestId.HasValue && captured.GuestId.HasValue)
        {
            score += request.GuestId.Value == captured.GuestId.Value ? 8 : -2;
        }

        if (!string.IsNullOrWhiteSpace(request.GuestName) && !string.IsNullOrWhiteSpace(captured.GuestName))
        {
            score += TextMatches(captured.GuestName, request.GuestName) ? 6 : 0;
        }

        if (!string.IsNullOrWhiteSpace(request.FoodTag) && !string.IsNullOrWhiteSpace(captured.FoodTag))
        {
            score += TextMatches(captured.FoodTag, request.FoodTag) ? 3 : -2;
        }

        if (!string.IsNullOrWhiteSpace(request.BeverageTag) && !string.IsNullOrWhiteSpace(captured.BeverageTag))
        {
            score += TextMatches(captured.BeverageTag, request.BeverageTag) ? 3 : -2;
        }

        return score >= (deskMatched ? 8 : 12) ? score : 0;
    }

    private static bool TextMatches(string left, string right)
    {
        left = left.Trim();
        right = right.Trim();
        if (left.Length == 0 || right.Length == 0) return false;
        return string.Equals(left, right, StringComparison.Ordinal)
            || left.Contains(right, StringComparison.Ordinal)
            || right.Contains(left, StringComparison.Ordinal);
    }

    private static string FormatCapturedOrderSummary(IReadOnlyList<CapturedRuntimeSpecialOrder> capturedOrders)
    {
        if (capturedOrders.Count == 0) return "";

        var items = capturedOrders
            .Take(4)
            .Select(order => $"desk={order.DeskCode + 1},guest={order.GuestName}/{order.GuestId?.ToString() ?? ""},food={order.FoodTag},bev={order.BeverageTag},source={order.CaptureSource},obj={(order.OrderObject == null ? "no" : "yes")}/{(order.ControllerObject == null ? "no" : "yes")}")
            .ToArray();
        var suffix = capturedOrders.Count > items.Length ? $" ... total={capturedOrders.Count}" : "";
        return string.Join("; ", items) + suffix;
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
        foreach (var name in new[] { "AllOrders", "AllOrdersData" })
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

        var peekOrder = TryInvokeInstanceValue(controller, "PeekOrders");
        if (peekOrder == null) yield break;

        var shouldYieldPeekOrder = false;
        try
        {
            shouldYieldPeekOrder = seen.Add(ReadObjectPointer(peekOrder));
        }
        catch
        {
            // Ignore stale IL2CPP order objects while scanning live controllers.
        }

        if (shouldYieldPeekOrder)
        {
            yield return peekOrder;
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
        return TryInvokeInstanceValue(target, methodName, Array.Empty<object?>());
    }

    private static object? TryInvokeInstanceValue(object target, string methodName, object?[] args)
    {
        try
        {
            return InvokeInstance(target, methodName, args);
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
        try
        {
            var utilityValue = RuntimeReflectionUtility.GetMemberValue(target, name);
            if (utilityValue != null) return utilityValue;
        }
        catch
        {
            // Fall back to the local exact-field reader below.
        }

        for (var type = target.GetType(); type != null; type = type.BaseType)
        {
            foreach (var fieldName in BuildFieldNameCandidates(name))
            {
                var field = type.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (field != null) return field.GetValue(target);
            }

            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (property != null) return property.GetValue(target);

            var pascalName = char.ToUpperInvariant(name[0]) + name[1..];
            if (!string.Equals(pascalName, name, StringComparison.Ordinal))
            {
                property = type.GetProperty(pascalName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (property != null) return property.GetValue(target);
            }
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

            foreach (var fieldName in BuildFieldNameCandidates(name))
            {
                var field = type.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (field == null) continue;

                field.SetValue(target, value);
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> BuildFieldNameCandidates(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) yield break;

        yield return name;
        yield return $"<{name}>k__BackingField";
        yield return $"m_{name}";
        yield return $"_{name}";

        var camelName = char.ToLowerInvariant(name[0]) + name[1..];
        if (!string.Equals(camelName, name, StringComparison.Ordinal))
        {
            yield return camelName;
            yield return $"<{camelName}>k__BackingField";
            yield return $"m_{camelName}";
            yield return $"_{camelName}";
        }
    }

    private static IEnumerable<int> ReadIntEnumerable(object? value)
    {
        if (value == null) yield break;
        if (value is string) yield break;
        foreach (var item in EnumerateManaged(value).Concat(EnumerateByIndexer(value)))
        {
            yield return ToInt(item);
        }
    }

    private static IEnumerable<object> ReadObjectEnumerable(object? value)
    {
        if (value == null || value is string) yield break;

        var seen = new HashSet<nint>();
        foreach (var item in EnumerateManaged(value).Concat(EnumerateByIndexer(value)).Concat(ReadDictionaryValues(value)))
        {
            if (item == null) continue;
            if (!TryRememberObject(item, seen)) continue;
            yield return item;
        }
    }

    private static IEnumerable<object?> ReadDictionaryValues(object? dictionary)
    {
        if (dictionary == null || dictionary is string) yield break;

        if (dictionary is IDictionary managedDictionary)
        {
            foreach (DictionaryEntry entry in managedDictionary)
            {
                yield return entry.Value;
            }

            yield break;
        }

        var entries = ReadMember(dictionary, "entries")
            ?? ReadMember(dictionary, "_entries")
            ?? ReadMember(dictionary, "m_Entries");
        var count = ToInt(ReadMember(dictionary, "count")
            ?? ReadMember(dictionary, "_count")
            ?? ReadMember(dictionary, "Count"));
        if (entries == null || count <= 0) yield break;

        var entryIndex = 0;
        foreach (var entry in EnumerateByIndexer(entries))
        {
            if (entryIndex++ >= Math.Min(count, 256)) yield break;
            if (entry == null) continue;

            var hashCode = ToInt(ReadMember(entry, "hashCode") ?? ReadMember(entry, "_hashCode"));
            if (hashCode < 0) continue;

            var value = ReadMember(entry, "value")
                ?? ReadMember(entry, "Value")
                ?? ReadMember(entry, "_value");
            if (value != null) yield return value;
        }
    }

    private static IEnumerable<object?> EnumerateManaged(object value)
    {
        if (LooksLikeIl2CppObject(value)) yield break;
        if (value is not IEnumerable enumerable) yield break;

        foreach (var item in enumerable)
        {
            yield return item;
        }
    }

    private static IEnumerable<object?> EnumerateByIndexer(object value)
    {
        var count = ToInt(TryInvokeInstanceValue(value, "get_Count")
            ?? ReadMember(value, "Count")
            ?? ReadMember(value, "Length")
            ?? ReadMember(value, "_size"));
        if (count <= 0) yield break;

        for (var index = 0; index < Math.Min(count, 256); index++)
        {
            yield return TryInvokeInstanceValue(value, "get_Item", new object?[] { index });
        }
    }

    private static IEnumerable<object?> FindUnityObjects(Type type)
    {
        var method = typeof(UnityEngine.Object).GetMethod("FindObjectsOfType", new[] { typeof(Type) });
        if (method == null) yield break;

        object? objects;
        try
        {
            objects = method.Invoke(null, new object[] { type });
        }
        catch
        {
            yield break;
        }

        foreach (var item in ReadObjectEnumerable(objects))
        {
            yield return item;
        }
    }

    private static bool LooksLikeIl2CppObject(object value)
    {
        var type = value.GetType();
        var fullName = type.FullName ?? "";
        if (fullName.StartsWith("Il2Cpp", StringComparison.Ordinal)) return true;
        if (fullName.StartsWith("NightScene.", StringComparison.Ordinal)) return true;
        if (fullName.StartsWith("DayScene.", StringComparison.Ordinal)) return true;
        if (fullName.StartsWith("GameData.", StringComparison.Ordinal)) return true;
        if (fullName.StartsWith("DEYU.", StringComparison.Ordinal)) return true;
        return type.Assembly.GetName().Name?.Contains("Il2Cpp", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static object? GetSingletonInstance(string typeName)
    {
        var type = FindType(typeName)
            ?? throw new InvalidOperationException($"{typeName} type is not loaded.");
        return RuntimeReflectionUtility.GetSingletonInstance(type)
            ?? RuntimeReflectionUtility.FindUnityObject(type);
    }

    private static object? InvokeStatic(string typeName, string methodName, object?[] args)
    {
        var type = FindType(typeName)
            ?? throw new InvalidOperationException($"{typeName} type is not loaded.");
        var utilityValue = RuntimeReflectionUtility.InvokeStaticMethod(type, methodName, args);
        if (utilityValue != null) return utilityValue;

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

    private static int ToInt(object? value, int fallback)
    {
        if (value == null) return fallback;
        try
        {
            if (value is int number) return number;
            if (value is Enum enumValue) return Convert.ToInt32(enumValue);
            if (value is IConvertible convertible) return Convert.ToInt32(convertible);
            return int.TryParse(value.ToString(), out var parsed) ? parsed : fallback;
        }
        catch
        {
            return fallback;
        }
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
        public CookingCollectionTarget Target { get; init; } = CookingCollectionTarget.Tray();
    }

    private sealed class CompletedNormalCookingCollection
    {
        public string OrderKey { get; init; } = "";
        public int DeskCode { get; init; } = -1;
        public int FoodId { get; init; } = -1;
        public string FoodName { get; init; } = "";
        public string StoredFoodKey { get; init; } = "";
        public DateTime StoredAtUtc { get; init; }
        public DateTime LastConfirmedAtUtc { get; set; }
    }

    private sealed class NormalStorageStatus
    {
        public bool CanVerify { get; private init; }
        public bool HasTarget { get; private init; }
        public string Message { get; private init; } = "";

        public static NormalStorageStatus Verified(bool hasTarget, string message)
        {
            return new NormalStorageStatus
            {
                CanVerify = true,
                HasTarget = hasTarget,
                Message = message,
            };
        }

        public static NormalStorageStatus Unknown(string message)
        {
            return new NormalStorageStatus
            {
                CanVerify = false,
                HasTarget = false,
                Message = message,
            };
        }
    }

    private sealed class CookingCollectionTarget
    {
        public CookingCollectionTargetKind Kind { get; private init; }
        public object? Manager { get; private init; }
        public object? Controller { get; private init; }
        public object? Order { get; private init; }
        public string OrderKey { get; private init; } = "";
        public int FoodId { get; private init; } = -1;
        public string FoodName { get; private init; } = "";
        public int DeskCode { get; private init; } = -1;
        public string GuestName { get; private init; } = "";

        public static CookingCollectionTarget Tray()
        {
            return new CookingCollectionTarget
            {
                Kind = CookingCollectionTargetKind.Tray,
            };
        }

        public static CookingCollectionTarget ForTrayFood(int foodId, string foodName)
        {
            return new CookingCollectionTarget
            {
                Kind = CookingCollectionTargetKind.Tray,
                FoodId = foodId,
                FoodName = foodName,
            };
        }

        public static CookingCollectionTarget ForNormalOrder(
            object? manager,
            object? controller,
            object? order,
            string orderKey,
            int foodId,
            string foodName,
            int deskCode,
            string guestName)
        {
            return new CookingCollectionTarget
            {
                Kind = CookingCollectionTargetKind.NormalOrder,
                Manager = manager,
                Controller = controller,
                Order = order,
                OrderKey = orderKey,
                FoodId = foodId,
                FoodName = foodName,
                DeskCode = deskCode,
                GuestName = guestName,
            };
        }
    }

    private sealed class CookingStartResult
    {
        public bool Ok { get; private init; }
        public string Message { get; private init; } = "";
        public string QteMessage { get; private init; } = "";
        public bool QteSkipped { get; private init; }

        public static CookingStartResult Succeeded(string message, string qteMessage, bool qteSkipped)
        {
            return new CookingStartResult
            {
                Ok = true,
                Message = message,
                QteMessage = qteMessage,
                QteSkipped = qteSkipped,
            };
        }

        public static CookingStartResult Failed(string message)
        {
            return new CookingStartResult
            {
                Ok = false,
                Message = message,
            };
        }
    }

    private sealed class RuntimeOrderMatch
    {
        public object? Manager { get; init; }
        public object? Controller { get; init; }
        public object? Order { get; init; }
        public string Diagnostic { get; init; } = "";
    }

}
